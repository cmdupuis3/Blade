(* ===================================================================== *)
(* BladeWordClosure.v -- WORD CLOSURE FOR THE FINITE EQUIVARIANCE        *)
(* DISCHARGE: the stage 6b obligation of                                 *)
(* the retired transforms-as-types plan 3.5 (uniform rule) and 7's 6b   *)
(* bullet, whose mandate for this file reads: Coq -- word-closure lemma  *)
(* (proved).  It is also the discrete row of 8's BladeGenerator split,   *)
(* the one the design lists in the PROVED column.                        *)
(*                                                                       *)
(* THE CLAIM, in one line: if a map intertwines the GENERATORS of a      *)
(* finite group, it intertwines every element.  Formally, over an        *)
(* arbitrary pair of generator-indexed actions and an arbitrary map      *)
(* between the two carriers,                                            *)
(*                                                                       *)
(*   (forall i x, f (a_in i x) = a_out i (f x))                          *)
(*     -> forall w x, f (wact a_in w x) = wact a_out w (f x)             *)
(*                                                                       *)
(* where `wact` applies a WORD -- a list of generator indices -- right   *)
(* to left, matching BladePointGroup's `word_mat` convention (the matrix *)
(* of [i1; ...; ik] is g_i1 * ... * g_ik, so g_ik hits the vector        *)
(* first).  The induction is on the word, i.e. on its LENGTH: this is    *)
(* the finite-group twin of BladeDeduce's S_n-is-generated-by-adjacent-  *)
(* transpositions, and it is the reason the continuous rule of 3.5 --    *)
(* one identity per Lie generator, plus one per pi_0 generator -- has a  *)
(* sound finite row at all.                                             *)
(*                                                                       *)
(* WHAT THE COMPILER DOES, AND WHY THIS IS BELT-AND-BRACES IN THE        *)
(* OPPOSITE DIRECTION.  src/ml/compiler/MLPolyExtract.fs's `discharge`   *)
(* checks the equivariance identity at EVERY element of the enumerated   *)
(* word set (|G| <= 8 for the shipped roster {C4, D4}), not at the       *)
(* generators alone: at that size the extra checks are microseconds and  *)
(* the code is simpler.  This file proves that the generator checks      *)
(* WOULD HAVE SUFFICED.  The two point opposite ways on purpose -- the   *)
(* implementation is conservatively redundant, the theorem says the      *)
(* redundancy is redundancy -- so a future optimization to               *)
(* generators-only is a licensed refactor and not a new obligation.      *)
(*                                                                       *)
(* THE ABSTRACT MULTIPLICATION TABLE.  Section TableClosure states the   *)
(* same content one level up, over an element-indexed action and a       *)
(* Cayley table `tbl` with no words in sight: if the two actions respect *)
(* the table and f intertwines at i and at j, it intertwines at i * j.   *)
(* That is the algebraic core; word closure is its iteration from a      *)
(* generating set, and BladePointGroup's c4_table / d4_table are the     *)
(* concrete tables the hypotheses hold of (c4_rep_property,              *)
(* d4_rep_property, proved there).                                      *)
(*                                                                       *)
(* THE INSTANTIATION is at the C4/D4 E PLANE, over Z * Z, and it is      *)
(* pinned to the SHIPPED matrices rather than to a private copy:         *)
(* rot_is_R90 and mir_is_Sref prove that the two generator FUNCTIONS     *)
(* used below are exactly BladePointGroup's R90 and Sref acting on a     *)
(* coordinate pair.  The three anchors mirror the corpus:                *)
(*                                                                       *)
(*   J = R90 written out by hand    -> tests/corpus/ml-equiv/058         *)
(*   x^2 + y^2 is C4- (and D4-)     -> tests/corpus/ml-equiv/059         *)
(*     invariant                                                        *)
(*   x^2 - y^2 is NOT (it dies at   -> tests/corpus/ml-equiv/060         *)
(*     the generator r)                                                 *)
(*                                                                       *)
(* NOT MODELLED, on purpose: the POLYNOMIAL side.  Nothing here says     *)
(* that a Blade body extracts to the map it is checked as, and nothing   *)
(* here reasons about coefficients -- `f` is an arbitrary function, so   *)
(* the lemma covers polynomial maps by being indifferent to what they    *)
(* are.  Extraction faithfulness is a property of MLPolyExtract.fs and   *)
(* is discharged by the corpus, in the Test_PermOracle tradition: the    *)
(* value pins of 058-061 are the same maps evaluated by the compiled     *)
(* program.  Also not modelled: infinite groups (that is 6c's cited exp  *)
(* step) and any group off the {C4, D4} roster.                          *)
(*                                                                       *)
(* Imports BladePointGroup (mat, mentry, R90, Sref, c4_words, d4_words). *)
(* Rocq 9.0, stdlib only; no Admitted, no Axiom, no classical reasoning. *)
(* ===================================================================== *)

From Blade Require Import BladePointGroup.
Require Import List ZArith.
Import ListNotations.
Open Scope nat_scope.

(* ===================================================================== *)
(* WC1.  THE WORD ACTION, AND THE CLOSURE LEMMA.                         *)
(* ===================================================================== *)

(* The action of a WORD of generator indices.  Right to left, so that     *)
(* `wact a [i1; ...; ik] x = a i1 (... (a ik x))` -- the same order in    *)
(* which the matrix product g_i1 * ... * g_ik hits a column vector, which *)
(* is BladePointGroup's `word_mat` convention.                            *)
Fixpoint wact {T : Type} (a : nat -> T -> T) (w : list nat) (x : T) : T :=
  match w with
  | [] => x
  | i :: w' => a i (wact a w' x)
  end.

Lemma wact_app : forall (T : Type) (a : nat -> T -> T) (w1 w2 : list nat) (x : T),
  wact a (w1 ++ w2) x = wact a w1 (wact a w2 x).
Proof.
  intros T a w1. induction w1 as [|i w1 IH]; intros w2 x; cbn; [reflexivity |].
  rewrite IH. reflexivity.
Qed.

Section WordClosure.
  Context {X Y : Type}.
  Variable ain  : nat -> X -> X.
  Variable aout : nat -> Y -> Y.
  Variable f    : X -> Y.
  Hypothesis Hgen : forall i x, f (ain i x) = aout i (f x).

  (* THE LEMMA.  Induction on the word -- equivalently on its length,     *)
  (* since a word is its own length-indexed list of generator indices.    *)
  Theorem word_closure : forall w x, f (wact ain w x) = wact aout w (f x).
  Proof.
    induction w as [|i w IH]; intro x; cbn; [reflexivity |].
    rewrite Hgen, IH. reflexivity.
  Qed.
End WordClosure.

(* The form the discharge actually needs: a FINITE GROUP PRESENTED BY ITS *)
(* WORD SET.  Checking the generators licenses every listed element, so   *)
(* MLPolyExtract's all-elements sweep proves nothing the generator sweep  *)
(* would not have.                                                       *)
Theorem generators_suffice_on_word_set :
  forall (X Y : Type) (ain : nat -> X -> X) (aout : nat -> Y -> Y) (f : X -> Y)
         (words : list (list nat)),
    (forall i x, f (ain i x) = aout i (f x)) ->
    Forall (fun w => forall x, f (wact ain w x) = wact aout w (f x)) words.
Proof.
  intros X Y ain aout f words Hgen. apply Forall_forall.
  intros w _ x. apply (word_closure ain aout f Hgen).
Qed.

(* ===================================================================== *)
(* WC2.  THE SAME CONTENT OVER AN ABSTRACT MULTIPLICATION TABLE.         *)
(* No words: an element-indexed action on each side, a Cayley table the   *)
(* two actions respect, and equivariance closed under the product.       *)
(* BladePointGroup's c4_rep_property / d4_rep_property are exactly the    *)
(* hypotheses discharged at the shipped tables.                          *)
(* ===================================================================== *)

Section TableClosure.
  Context {X Y : Type}.
  Variable ein  : nat -> X -> X.
  Variable eout : nat -> Y -> Y.
  Variable f    : X -> Y.
  Variable tbl  : nat -> nat -> nat.
  Hypothesis Hin  : forall i j x, ein  (tbl i j) x = ein  i (ein  j x).
  Hypothesis Hout : forall i j y, eout (tbl i j) y = eout i (eout j y).

  Theorem equivariance_closed_under_product : forall i j,
    (forall x, f (ein i x) = eout i (f x)) ->
    (forall x, f (ein j x) = eout j (f x)) ->
    forall x, f (ein (tbl i j) x) = eout (tbl i j) (f x).
  Proof.
    intros i j Hi Hj x. rewrite Hin, Hi, Hj, Hout. reflexivity.
  Qed.
End TableClosure.

(* ===================================================================== *)
(* WC3.  THE INSTANTIATION: THE C4 / D4 E PLANE OVER Z * Z.              *)
(* ===================================================================== *)

Definition point : Type := (Z * Z)%type.

(* The two generator functions, written as coordinate maps... *)
Definition rot (p : point) : point := (Z.opp (snd p), fst p).
Definition mir (p : point) : point := (fst p, Z.opp (snd p)).

(* ...and pinned to the SHIPPED matrices: act2 is the linear action of a  *)
(* 2x2 integer matrix on a coordinate pair, and the two lemmas below say  *)
(* rot IS R90 and mir IS Sref.  Without them this section would be about  *)
(* a private pair of functions that merely resemble the registry.         *)
Definition act2 (A : mat) (p : point) : point :=
  (Z.add (Z.mul (mentry A 0 0) (fst p)) (Z.mul (mentry A 0 1) (snd p)),
   Z.add (Z.mul (mentry A 1 0) (fst p)) (Z.mul (mentry A 1 1) (snd p))).

Lemma rot_is_R90 : forall p, rot p = act2 R90 p.
Proof. intros [a b]. unfold rot, act2, R90, mentry. cbn [nth fst snd]. f_equal; ring. Qed.

Lemma mir_is_Sref : forall p, mir p = act2 Sref p.
Proof. intros [a b]. unfold mir, act2, Sref, mentry. cbn [nth fst snd]. f_equal; ring. Qed.

(* Generator index 0 = r, 1 = s -- BladePointGroup's order.  Indices past *)
(* the group's generator count act trivially; no word ever reaches them.  *)
Definition c4_gen (i : nat) : point -> point :=
  match i with O => rot | S _ => (fun p => p) end.

Definition d4_gen (i : nat) : point -> point :=
  match i with O => rot | S O => mir | S (S _) => (fun p => p) end.

(* The trivial action, for invariance claims: the one-dimensional trivial *)
(* representation, which is what a scalar-returning certified function    *)
(* claims (MLEquiv's `Inv` return -> the 1x1 identity output action).     *)
Definition triv (T : Type) (i : nat) (y : T) : T := y.

Lemma wact_triv : forall (T : Type) (w : list nat) (y : T), wact (triv T) w y = y.
Proof.
  intros T w y. induction w as [|i w IH]; cbn; [reflexivity |].
  unfold triv at 1. exact IH.
Qed.

(* The word sets are the shipped ones, and they are small -- the 6b       *)
(* budget (|G| <= 8, so check everything), as a number.                   *)
Example shipped_word_counts : (length c4_words, length d4_words) = (4, 8).
Proof. vm_compute. reflexivity. Qed.

(* --------------------------------------------------------------------- *)
(* ANCHOR 1 (corpus 058): J = R90, HAND-WRITTEN, IS C4-EQUIVARIANT.       *)
(* The generator check is one line; word closure does the other three     *)
(* elements.  This is the map the composition judgment refuses (raw       *)
(* component reads assembled into an array literal) and the engine        *)
(* certifies.                                                            *)
(* --------------------------------------------------------------------- *)

Definition J : point -> point := rot.

Lemma J_c4_generator : forall i p, J (c4_gen i p) = c4_gen i (J p).
Proof. intros [|i] [a b]; unfold J, c4_gen, rot; cbn [fst snd]; reflexivity. Qed.

Theorem J_equivariant_on_c4_words :
  forall w p, J (wact c4_gen w p) = wact c4_gen w (J p).
Proof. exact (word_closure c4_gen c4_gen J J_c4_generator). Qed.

Corollary J_equivariant_at_every_c4_element :
  Forall (fun w => forall p, J (wact c4_gen w p) = wact c4_gen w (J p)) c4_words.
Proof.
  apply (generators_suffice_on_word_set _ _ c4_gen c4_gen J c4_words J_c4_generator).
Qed.

(* THE NEGATIVE CONTROL ON THE OTHER SIDE OF THE ROSTER.  J is available  *)
(* at C4 because End_{C4}(E) is C; D4's E is of REAL type, and the        *)
(* obstruction is visible at the mirror generator -- the same fact        *)
(* BladePointGroup records as d4E_J_not_equivariant, here as a failure of *)
(* the equivariance identity at a single point.                          *)
Example J_not_d4_equivariant :
  J (d4_gen (S O) (1%Z, 0%Z)) <> d4_gen (S O) (J (1%Z, 0%Z)).
Proof. vm_compute. discriminate. Qed.

(* --------------------------------------------------------------------- *)
(* ANCHOR 2 (corpus 059): x^2 + y^2 IS INVARIANT, AT BOTH GROUPS.         *)
(* The output action is the trivial one, so the identity reads            *)
(* f(g x) = f(x) -- which is exactly how the discharge treats a           *)
(* scalar-returning certificate.                                         *)
(* --------------------------------------------------------------------- *)

Definition nsq (p : point) : Z := Z.add (Z.mul (fst p) (fst p)) (Z.mul (snd p) (snd p)).

Lemma nsq_c4_generator : forall i p, nsq (c4_gen i p) = triv Z i (nsq p).
Proof. intros [|i] [a b]; unfold nsq, triv, c4_gen, rot; cbn [fst snd]; ring. Qed.

Lemma nsq_d4_generator : forall i p, nsq (d4_gen i p) = triv Z i (nsq p).
Proof. intros [|[|i]] [a b]; unfold nsq, triv, d4_gen, rot, mir; cbn [fst snd]; ring. Qed.

Theorem nsq_invariant_on_c4_words : forall w p, nsq (wact c4_gen w p) = nsq p.
Proof.
  intros w p.
  rewrite (word_closure c4_gen (triv Z) nsq nsq_c4_generator w p).
  apply wact_triv.
Qed.

Corollary nsq_invariant_on_d4_words : forall w p, nsq (wact d4_gen w p) = nsq p.
Proof.
  intros w p.
  rewrite (word_closure d4_gen (triv Z) nsq nsq_d4_generator w p).
  apply wact_triv.
Qed.

(* --------------------------------------------------------------------- *)
(* ANCHOR 3 (corpus 060): x^2 - y^2 IS NOT, AND IT DIES AT r.             *)
(* One point refutes the claim, and it is the generator the compiler      *)
(* names in the BL4008 message.  Note the sharpness: the failing element  *)
(* is r itself, so no amount of word closure could have rescued it -- the *)
(* generator check is where a false claim is caught, which is why the     *)
(* diagnostic reports an ELEMENT and not just "not equivariant".          *)
(* --------------------------------------------------------------------- *)

Definition diffsq (p : point) : Z := Z.sub (Z.mul (fst p) (fst p)) (Z.mul (snd p) (snd p)).

Example diffsq_not_c4_invariant : diffsq (c4_gen O (1%Z, 0%Z)) <> diffsq (1%Z, 0%Z).
Proof. vm_compute. discriminate. Qed.

(* It is not noise either: it is a SIGN FLIP, i.e. x^2 - y^2 spans a      *)
(* non-trivial character of C4 (the B label).  The engine's message says  *)
(* "the left side has coefficient -1, the right side 1", and this is that *)
(* statement at the level of values.                                      *)
Example diffsq_flips_under_r : forall p, diffsq (c4_gen O p) = Z.opp (diffsq p).
Proof. intros [a b]. unfold diffsq, c4_gen, rot. cbn [fst snd]. ring. Qed.

(* ===================================================================== *)
(* Notes:                                                                *)
(*  - Scope.  WC1/WC2 are fully general (any carriers, any               *)
(*    generator-indexed actions, any map); WC3 is a finite computation    *)
(*    over the two shipped tables, in the 6.1 closure BladePointGroup     *)
(*    states.  Nothing is quantified over "all finite groups" in the      *)
(*    concrete section, and nothing needs to be: the general half is      *)
(*    already general.                                                    *)
(*  - Sharpness.  word_closure needs the generator hypothesis at EVERY    *)
(*    index, which is why d4_gen carries two arms and c4_gen one.  A map  *)
(*    that intertwines r but not s is exactly J, and                      *)
(*    J_not_d4_equivariant is the refutation -- so the hypothesis is not  *)
(*    decorative.                                                        *)
(*  - Direction.  The lemma is one-way on purpose: generators suffice.    *)
(*    The converse (every element implies every generator) is trivial and *)
(*    is what the shipped discharge relies on, since a generator IS an    *)
(*    element of the enumerated word set.                                 *)
(*  - The polynomial layer is absent by design (see the header): f is an  *)
(*    arbitrary function here, so the theorem applies to the extracted    *)
(*    normal form without knowing anything about coefficients.            *)
(* ===================================================================== *)
