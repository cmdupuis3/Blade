(* ===================================================================== *)
(* BladeDeduce.v -- soundness kernel of the stage-3 signature deduction  *)
(* (docs/plan-implicit-formers-and-deduction.md par. 8; implementation   *)
(* src/Deduce.fs).  The compiler deduces kernel symmetry syntactically   *)
(* -- a {PInv, PNeg, PBottom} parity per adjacent parameter pair, from   *)
(* two per-primitive tables -- and every rule that ever ANSWERS (PInv /  *)
(* PNeg rather than PBottom) appeals to one of the semantic laws proved  *)
(* here.  PBottom needs no law: it is the closed-world default and       *)
(* licenses nothing.                                                     *)
(*                                                                       *)
(* Contents:                                                             *)
(*   mirror_comm_invariant, mirror_antisym_antiinvariant                 *)
(*                       table 1 (swap class) at a MIRROR node: the      *)
(*                       kernel op (g (v 0)) (g (v 1)) is invariant      *)
(*                       under swap for commutative op, anti-invariant   *)
(*                       for antisymmetric op.  Generalizes 9.19 to a    *)
(*                       per-side accessor and adds the signed half.     *)
(*   signprod_neg_neg, signprod_mixed, signsum_joint                     *)
(*                       table 2 (sign composition): PNeg.PNeg = PInv    *)
(*                       through a sign-multiplicative op -- the         *)
(*                       (a-b)*(a-b) case the adversarial review flagged *)
(*                       as the one plausible misreading -- plus the     *)
(*                       mixed product and joint-linear sum rows.        *)
(*   chain_flip_once, chain_flip_twice                                   *)
(*                       the sign chain rule (signParityOf's call rule): *)
(*                       one odd position flips the result, two flips    *)
(*                       cancel -- the k = 1, 2 instances of (-1)^k.     *)
(*   adjacent_transpositions_generate                                    *)
(*                       a list function invariant under swapping ANY    *)
(*                       adjacent pair is invariant under EVERY          *)
(*                       permutation -- the n-1-checks-instead-of-n!     *)
(*                       theorem par. 3.2 rests on, in the tower's list  *)
(*                       semantics (induction over Permutation; no       *)
(*                       group theory needed).                           *)
(*   exchange_law, packfold_permutation                                  *)
(*                       the all-arity pack license (deducePackFold):    *)
(*                       comm + assoc give the exchange law              *)
(*                       x op (y op R) = y op (x op R), and the          *)
(*                       head::tail fold g(x1) op ... op g(xn) is then   *)
(*                       invariant under every permutation of the pack,  *)
(*                       at every arity -- proved BY                     *)
(*                       adjacent_transpositions_generate, mirroring     *)
(*                       how the implementation reduces the whole-Sn     *)
(*                       claim to adjacent checks.                       *)
(*   signed_exchange_collapse, no_signed_exchange_Zsub                   *)
(*                       the vacuity theorem: a SIGNED exchange law      *)
(*                       forces x op (x op R) to be self-negating for    *)
(*                       all x, R (so any carrier where only 0 is        *)
(*                       self-negating collapses), and concretely Z's    *)
(*                       subtraction admits none.  This is why packs     *)
(*                       never claim PNeg and `antisymm` has no pack     *)
(*                       tier.                                           *)
(*                                                                       *)
(* The call-site half -- deduced parity feeding H-and-Stab storage --    *)
(* is already proved: output_symmetry_soundness /                        *)
(* output_antisymmetry_soundness (BladeLowering) consume exactly the     *)
(* invariant_under / antiinvariant_under facts produced here.            *)
(*                                                                       *)
(* Imports BladeLowering.  Coq 8.18, stdlib only.                        *)
(* ===================================================================== *)

From Blade Require Import BladeLowering.
Require Import List Arith Lia ZArith Permutation.
Import ListNotations.

(* ---------------- table 1 at a mirror node ---------------- *)

Section MirrorNode.
  Variables T W : Type.
  Variable op : W -> W -> W.
  Variable g : T -> W.

  (* parityOf's mirror rule, PInv row: the node's two children are      *)
  (* mirror images (here literally g (v 0) / g (v 1)) and op is         *)
  (* commutative, so the swap is invisible.                             *)
  Hypothesis Hc : forall x y, op x y = op y x.

  Theorem mirror_comm_invariant :
    invariant_under T W (fun v => op (g (v 0)) (g (v 1))) swap.
  Proof.
    intro v. simpl. apply Hc.
  Qed.
End MirrorNode.

Section MirrorNodeSigned.
  Variables T W : Type.
  Variable op : W -> W -> W.
  Variable neg : W -> W.
  Variable g : T -> W.

  (* parityOf's mirror rule, PNeg row: an antisymmetric op at the       *)
  (* mirror node negates under the swap.  The storage-side consumer is  *)
  (* output_antisymmetry_soundness.                                     *)
  Hypothesis Ha : forall x y, op x y = neg (op y x).

  Theorem mirror_antisym_antiinvariant :
    antiinvariant_under T W (fun v => op (g (v 0)) (g (v 1))) neg swap.
  Proof.
    intro v. simpl. apply Ha.
  Qed.
End MirrorNodeSigned.

(* ---------------- table 2: sign composition ---------------- *)

Section SignComposition.
  Variables T W : Type.
  Variable mul : W -> W -> W.
  Variable neg : W -> W.

  (* mul is sign-multiplicative in each operand and neg is involutive.  *)
  Hypothesis Hml : forall x y, mul (neg x) y = neg (mul x y).
  Hypothesis Hmr : forall x y, mul x (neg y) = neg (mul x y).
  Hypothesis Hnn : forall w, neg (neg w) = w.

  Variables f h : (nat -> T) -> W.

  (* combineBinOp's PNeg.PNeg = PInv row: the product of two            *)
  (* anti-invariant factors is invariant -- (a-b)*(a-b) is even.        *)
  Theorem signprod_neg_neg :
    antiinvariant_under T W f neg swap ->
    antiinvariant_under T W h neg swap ->
    invariant_under T W (fun v => mul (f v) (h v)) swap.
  Proof.
    intros Hf Hh v.
    change (mul (f (fun p => v (swap p))) (h (fun p => v (swap p)))
            = mul (f v) (h v)).
    rewrite (Hf v), (Hh v), Hml, Hmr, Hnn. reflexivity.
  Qed.

  (* combineBinOp's PInv.PNeg = PNeg row: one flipping factor flips     *)
  (* the product.                                                       *)
  Theorem signprod_mixed :
    invariant_under T W f swap ->
    antiinvariant_under T W h neg swap ->
    antiinvariant_under T W (fun v => mul (f v) (h v)) neg swap.
  Proof.
    intros Hf Hh v.
    change (mul (f (fun p => v (swap p))) (h (fun p => v (swap p)))
            = neg (mul (f v) (h v))).
    rewrite (Hf v), (Hh v), Hmr. reflexivity.
  Qed.
End SignComposition.

Section SignSum.
  Variables T W : Type.
  Variable add : W -> W -> W.
  Variable neg : W -> W.

  (* add is jointly sign-linear: negating BOTH operands negates the     *)
  (* sum.  (Mixed parities certify nothing -- there is no rule to be    *)
  (* sound about; the implementation answers PBottom.)                  *)
  Hypothesis Hj : forall x y, add (neg x) (neg y) = neg (add x y).

  Variables f h : (nat -> T) -> W.

  (* combineBinOp's PNeg+PNeg = PNeg row for + and - .                  *)
  Theorem signsum_joint :
    antiinvariant_under T W f neg swap ->
    antiinvariant_under T W h neg swap ->
    antiinvariant_under T W (fun v => add (f v) (h v)) neg swap.
  Proof.
    intros Hf Hh v.
    change (add (f (fun p => v (swap p))) (h (fun p => v (swap p)))
            = neg (add (f v) (h v))).
    rewrite (Hf v), (Hh v), Hj. reflexivity.
  Qed.
End SignSum.

(* ---------------- the sign chain rule ---------------- *)

Section ChainRule.
  Variables T S W : Type.
  Variable negS : S -> S.
  Variable neg : W -> W.

  (* signParityOf's call rule at k = 1: an SOdd callee position fed by  *)
  (* a flipping argument flips the call.                                *)
  Section One.
    Variable h : S -> W.
    Hypothesis Hodd : forall s, h (negS s) = neg (h s).
    Variable k : (nat -> T) -> S.

    Theorem chain_flip_once :
      (forall v : nat -> T, k (fun p => v (swap p)) = negS (k v)) ->
      antiinvariant_under T W (fun v => h (k v)) neg swap.
    Proof.
      intros Hk v.
      change (h (k (fun p => v (swap p))) = neg (h (k v))).
      rewrite (Hk v), Hodd. reflexivity.
    Qed.
  End One.

  (* k = 2: two flips through two SOdd positions cancel -- (-1)^2.      *)
  Section Two.
    Variable h : S -> S -> W.
    Hypothesis Hodd1 : forall a b, h (negS a) b = neg (h a b).
    Hypothesis Hodd2 : forall a b, h a (negS b) = neg (h a b).
    Hypothesis Hnn : forall w, neg (neg w) = w.
    Variables k1 k2 : (nat -> T) -> S.

    Theorem chain_flip_twice :
      (forall v : nat -> T, k1 (fun p => v (swap p)) = negS (k1 v)) ->
      (forall v : nat -> T, k2 (fun p => v (swap p)) = negS (k2 v)) ->
      invariant_under T W (fun v => h (k1 v) (k2 v)) swap.
    Proof.
      intros Hk1 Hk2 v.
      change (h (k1 (fun p => v (swap p))) (k2 (fun p => v (swap p)))
              = h (k1 v) (k2 v)).
      rewrite (Hk1 v), (Hk2 v), Hodd1, Hodd2, Hnn. reflexivity.
    Qed.
  End Two.
End ChainRule.

(* ---------------- adjacent transpositions generate Sn ---------------- *)

Section AdjacentGeneration.
  Variables T W : Type.
  Variable f : list T -> W.

  (* f cannot see a swap of ANY adjacent pair, in any context.  This is *)
  (* exactly what the implementation checks: the n-1 adjacent pairs (or *)
  (* for packs, the one exchange law that implies them all).            *)
  Hypothesis Hadj :
    forall pre (a b : T) post, f (pre ++ a :: b :: post) = f (pre ++ b :: a :: post).

  Lemma adj_perm_ctx : forall (l l' : list T),
    Permutation l l' -> forall pre, f (pre ++ l) = f (pre ++ l').
  Proof.
    intros l l' HP. induction HP as [| x l1 l2 HP IH | x y l1 | l1 l2 l3 HP1 IH1 HP2 IH2];
      intro pre.
    - reflexivity.
    - (* skip: push x into the context *)
      change (pre ++ x :: l1) with (pre ++ [x] ++ l1).
      change (pre ++ x :: l2) with (pre ++ [x] ++ l2).
      rewrite !app_assoc. apply IH.
    - (* swap at the head of the suffix = an adjacent swap in context *)
      apply Hadj.
    - rewrite IH1. apply IH2.
  Qed.

  (* The n-1-instead-of-n! theorem: adjacent invariance is FULL         *)
  (* permutation invariance.  (Adjacent transpositions generate Sn;     *)
  (* here via induction over Permutation, no group theory needed.)      *)
  Theorem adjacent_transpositions_generate : forall l l',
    Permutation l l' -> f l = f l'.
  Proof.
    intros l l' HP. exact (adj_perm_ctx l l' HP []).
  Qed.
End AdjacentGeneration.

(* ---------------- the all-arity pack license ---------------- *)

Section PackFold.
  Variables T W : Type.
  Variable g : T -> W.
  Variable op : W -> W -> W.
  Hypothesis Hc : forall x y, op x y = op y x.
  Hypothesis Ha : forall x y z, op (op x y) z = op x (op y z).

  (* The exchange law the implementation checks once per pack kernel:   *)
  (* derived from comm + assoc, it is the adjacent-swap fact at every   *)
  (* depth of the fold.                                                 *)
  Lemma exchange_law : forall x y r, op x (op y r) = op y (op x r).
  Proof.
    intros. rewrite <- Ha, (Hc x y), Ha. reflexivity.
  Qed.

  Lemma fold_right_permutation : forall (z : W) (l l' : list W),
    Permutation l l' -> fold_right op z l = fold_right op z l'.
  Proof.
    intros z l l' HP.
    induction HP as [| x l1 l2 HP IH | x y l1 | l1 l2 l3 HP1 IH1 HP2 IH2]; simpl.
    - reflexivity.
    - rewrite IH. reflexivity.
    - apply exchange_law.
    - rewrite IH1. exact IH2.
  Qed.

  (* Swapping the base of the fold with a new head element.             *)
  Lemma fold_base_swap : forall (a b : W) (l : list W),
    op a (fold_right op b l) = op b (fold_right op a l).
  Proof.
    intros a b l. revert a b.
    induction l as [|w l IH]; intros a b; simpl.
    - apply Hc.
    - rewrite exchange_law, IH, exchange_law. reflexivity.
  Qed.

  (* deducePackFold's shape: | [x] -> g x | x :: xs -> g x op fold xs.  *)
  Fixpoint packfold (x : T) (xs : list T) : W :=
    match xs with
    | [] => g x
    | y :: ys => op (g x) (packfold y ys)
    end.

  Lemma packfold_fold_right : forall xs x,
    packfold x xs = fold_right op (g x) (map g xs).
  Proof.
    induction xs as [|y ys IH]; intro x; simpl.
    - reflexivity.
    - rewrite IH. apply fold_base_swap.
  Qed.

  (* Head-adjacent swap: the step-vs-base boundary pair.                *)
  Lemma packfold_head_swap : forall x y l,
    packfold x (y :: l) = packfold y (x :: l).
  Proof.
    intros. simpl. rewrite !packfold_fold_right. apply fold_base_swap.
  Qed.

  (* Interior adjacent swap, at any depth of the tail.                  *)
  Lemma packfold_interior_swap : forall x l1 a b l2,
    packfold x (l1 ++ a :: b :: l2) = packfold x (l1 ++ b :: a :: l2).
  Proof.
    intros. rewrite !packfold_fold_right.
    apply fold_right_permutation, Permutation_map,
          Permutation_app_head, perm_swap.
  Qed.

  (* Pad the empty case so the adjacent-generation theorem applies to   *)
  (* whole argument lists; w0 is never reached from a nonempty pack.    *)
  Variable w0 : W.
  Definition packF (l : list T) : W :=
    match l with
    | [] => w0
    | x :: xs => packfold x xs
    end.

  Lemma packF_adj :
    forall pre (a b : T) post, packF (pre ++ a :: b :: post) = packF (pre ++ b :: a :: post).
  Proof.
    intros pre a b post. destruct pre as [|p pre']; simpl.
    - apply packfold_head_swap.
    - apply packfold_interior_swap.
  Qed.

  (* The all-arity license behind `where comm(pack)` suggestions: the   *)
  (* AC-fold of g over the pack is invariant under EVERY permutation of *)
  (* the pack elements, at every arity -- obtained from the adjacent    *)
  (* checks exactly as the implementation obtains it.                   *)
  Theorem packfold_permutation : forall x xs y ys,
    Permutation (x :: xs) (y :: ys) -> packfold x xs = packfold y ys.
  Proof.
    intros x xs y ys HP.
    exact (adjacent_transpositions_generate T W packF packF_adj
             (x :: xs) (y :: ys) HP).
  Qed.
End PackFold.

(* ---------------- no signed exchange law ---------------- *)

Section SignedExchangeCollapse.
  Variable W : Type.
  Variable op : W -> W -> W.
  Variable neg : W -> W.

  (* A signed exchange law -- what an all-arity ANTISYMMETRIC pack      *)
  (* license would require -- forces every x op (x op r) to be its own  *)
  (* negation.                                                          *)
  Theorem signed_exchange_collapse :
    (forall x y r, op x (op y r) = neg (op y (op x r))) ->
    forall x r, op x (op x r) = neg (op x (op x r)).
  Proof.
    intros HsEL x r. exact (HsEL x x r).
  Qed.

  (* On any carrier where no value is self-negating, that collapses the *)
  (* combiner: there is no antisymmetric analog of the AC-fold license. *)
  (* Stated pointwise (the witnesses x, r double as W's inhabitation).  *)
  Corollary signed_exchange_impossible :
    forall (x r : W),
    (forall w, neg w = w -> False) ->
    (forall a b c, op a (op b c) = neg (op b (op a c))) ->
    False.
  Proof.
    intros x r Hfix HsEL.
    apply (Hfix (op x (op x r))).
    symmetry. exact (HsEL x x r).
  Qed.
End SignedExchangeCollapse.

(* Concretely: integer subtraction -- the antisymmetric primitive --    *)
(* admits no signed exchange law (x = y = 0, r = 1 gives 1 = -1).       *)
Theorem no_signed_exchange_Zsub :
  ~ (forall x y r : Z, (x - (y - r))%Z = (- (y - (x - r)))%Z).
Proof.
  intro H. specialize (H 0%Z 0%Z 1%Z). lia.
Qed.
