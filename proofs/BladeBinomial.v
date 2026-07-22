(* ===================================================================== *)
(* BladeBinomial.v -- the STORAGE CARDINALITY closed form.               *)
(*                                                                       *)
(* The binomial identification: BladeDMWF's mscard       *)
(* (defined by the DMWF recursion itself) equals the multiset            *)
(* coefficient, so the symmetric arrow's storage size is the classical   *)
(*   |SymIdx<r> over [l, u)|  =  C(u - l + r - 1, r)                     *)
(* and in particular  |SymIdx<r, n>|  =  C(n + r - 1, r)                 *)
(* (formalism 14.x).  The engine is the hockey-stick identity, proved    *)
(* by induction over the very sum the dmwf_equation produces.            *)
(*                                                                       *)
(* Imports BladeDMWF.  Coq 8.18, stdlib only.                            *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF.
Require Import List Arith Lia.
Import ListNotations.

(* Pascal-recursion binomial coefficient. *)
Fixpoint C (n k : nat) {struct n} : nat :=
  match k with
  | 0 => 1
  | S k' => match n with
            | 0 => 0
            | S n' => C n' k' + C n' k
            end
  end.

Lemma C_zero : forall n, C n 0 = 1.
Proof. destruct n; reflexivity. Qed.

Lemma C_small : forall n k, n < k -> C n k = 0.
Proof.
  induction n as [|n IH]; intros k Hk; destruct k as [|k']; simpl; try lia.
  rewrite (IH k') by lia. rewrite (IH (S k')) by lia. reflexivity.
Qed.

Example C_4_2 : C 4 2 = 6.
Proof. reflexivity. Qed.

(* The hockey-stick identity, in exactly the shape the DMWF recursion    *)
(* produces: summing the residual-space cardinalities over the emitted   *)
(* head index telescopes to one binomial of the next rank.               *)
Lemma hockey : forall m r l,
  lsum (map (fun i => C (l + m - i + r - 1) r) (seq l m)) = C (m + r) (S r).
Proof.
  induction m as [|m IH]; intros r l; simpl.
  - symmetry. apply C_small. lia.
  - replace (l + S m - l + r - 1) with (m + r) by lia.
    transitivity (C (m + r) r
                  + lsum (map (fun i => C (S l + m - i + r - 1) r)
                              (seq (S l) m))).
    { f_equal. f_equal. apply map_ext. intro a. f_equal. lia. }
    rewrite IH.
    replace (S m + r) with (S (m + r)) by lia.
    reflexivity.
Qed.

Theorem mscard_binom : forall r l u,
  mscard r l u = C (u - l + r - 1) r.
Proof.
  induction r as [|r IH]; intros l u; simpl.
  - symmetry. apply C_zero.
  - destruct (le_lt_dec l u) as [Hle | Hlt].
    + transitivity
        (lsum (map (fun i => C (l + (u - l) - i + r - 1) r) (seq l (u - l)))).
      { f_equal. apply map_ext. intro a. rewrite IH. f_equal. lia. }
      rewrite hockey. f_equal. lia.
    + replace (u - l) with 0 by lia. simpl.
      symmetry. apply C_small. lia.
Qed.

(* The headline: the symmetric arrow's enumeration -- hence its storage  *)
(* -- has the classical multiset-coefficient size.                       *)
Theorem enum_length_binom : forall r l u,
  length (enum r l u) = C (u - l + r - 1) r.
Proof. intros. rewrite enum_length. apply mscard_binom. Qed.

Corollary storage_cardinality : forall r n,
  length (enum r 0 n) = C (n + r - 1) r.
Proof. intros. rewrite enum_length_binom. f_equal. lia. Qed.

(* With C available, the counting lemma acquires its general statement;  *)
(* the full proof is BladeCounting.v (counting_general_C).               *)
