(* ===================================================================== *)
(* floats/BladeBinary64Witness.v -- EXACT RECURRENCE REDUCTION: IEEE     *)
(* binary64 does not preserve the reduction, checked on the real thing.  *)
(*                                                                       *)
(*       THIS FILE IS NOT PART OF THE AXIOM-FREE TOWER.                  *)
(*                                                                       *)
(* Rocq's primitive floats.  Print Assumptions lists the primitive type  *)
(* and operations (float, add, mul, opp, eqb): the values below          *)
(* are computed by the kernel's binary64 evaluation and are trusted to   *)
(* that extent.  Own directory, own _CoqProject, not counted.  The       *)
(* axiom-free statement at every precision is BladeNumericContract.v.    *)
(*                                                                       *)
(*   binary64_original_inexact,       a = b = 1, two particles: exact    *)
(*   binary64_reduced_inexact  answer 2, original fold 1, reduced 2; and *)
(*                             the other way round at (-2^53, -1);       *)
(*   binary64_no_ordering_recovers_the_reduced_value   three particles:  *)
(*                             all twelve orderings of the original give *)
(*                             2^53 + 6, all orderings of the reduced    *)
(*                             form 2^53 + 8;                            *)
(*   binary64_reduced_overflows,      the reduced form returns infinity, *)
(*   binary64_reduced_nan      or NaN, where the original is finite;     *)
(*   binary64_raw_moments_lose_the_variance   why a float summary must   *)
(*                             be SHIFTED: at 1e8, 1e8 + 1, 1e8 + 2 the  *)
(*                             raw formula Q / N - (S / N)^2 returns 2;  *)
(*                             the central form returns 2/3, correctly   *)
(*                             rounded (BladeShiftedMoments,             *)
(*                             reals/BladeShiftedRounding.v).            *)
(*                                                                       *)
(* Scope, stated once.  With a = 1 the product a x is exact, so a fused  *)
(* multiply-add computes the same values: the first three witnesses do   *)
(* not depend on -ffp-contract.  Part D evaluates without contraction;   *)
(* compiled by g++ -O3 -ffp-contract=fast the raw formula gives 1,       *)
(* the central one still 2/3.                                            *)
(* ===================================================================== *)

Require Import Floats List Bool.
Import ListNotations.
Open Scope float_scope.

(* ===================================================================== *)
(* Part A.  Two particles, a = b = 1.  Because a = 1 the product a * x   *)
(* is exact, so a fused multiply-add computes the same values: these     *)
(* witnesses do not depend on -ffp-contract.                             *)
(* ===================================================================== *)

Definition two53 : float := 0x1p+53.

Definition orig2 (a b x1 x2 : float) : float := (a * x1 + b) + (a * x2 + b).
Definition red2 (a b x1 x2 : float) : float := a * (x1 + x2) + 2 * b.

(* Exact answer 2.  The original fold returns 1; the reduced form 2.     *)
Theorem binary64_original_inexact :
  orig2 1 1 two53 (- two53) = 1 /\ red2 1 1 two53 (- two53) = 2.
Proof. split; reflexivity. Qed.

(* Exact answer -2^53 + 1.  The original fold is exact; the reduced form *)
(* returns -2^53 + 2.                                                    *)
Theorem binary64_reduced_inexact :
  orig2 1 1 (- two53) (-1) = -0x1.fffffffffffffp+52 /\
  red2 1 1 (- two53) (-1) = -0x1.ffffffffffffep+52.
Proof. split; reflexivity. Qed.

Theorem binary64_update_not_bitwise :
  PrimFloat.eqb (orig2 1 1 two53 (- two53)) (red2 1 1 two53 (- two53))
  = false.
Proof. reflexivity. Qed.

(* ===================================================================== *)
(* Part B.  A reassociation license is not a distributivity license.     *)
(* Three particles: all twelve orderings and parenthesizations of the    *)
(* original fold, against all orderings of the reduced form.             *)
(* ===================================================================== *)

Definition orders3 (y1 y2 y3 : float) : list float :=
  flat_map (fun t : float * float * float =>
              let '(u, v, w) := t in [(u + v) + w; u + (v + w)])
           [(y1, y2, y3); (y1, y3, y2); (y2, y1, y3);
            (y2, y3, y1); (y3, y1, y2); (y3, y2, y1)].

Definition orig3 (a b x1 x2 x3 : float) : list float :=
  orders3 (a * x1 + b) (a * x2 + b) (a * x3 + b).

Definition red3 (a b x1 x2 x3 : float) : list float :=
  flat_map (fun S => [a * S + 3 * b; 3 * b + a * S]) (orders3 x1 x2 x3).

Definition all_equal_to (v : float) (l : list float) : bool :=
  forallb (fun y => PrimFloat.eqb y v) l.

(* Exact answer 2^53 + 7.  Every ordering of the original gives          *)
(* 2^53 + 6; every ordering of the reduced form gives 2^53 + 8.          *)
Theorem binary64_no_ordering_recovers_the_reduced_value :
  all_equal_to 0x1.0000000000003p+53 (orig3 1 1 1 3 two53) = true /\
  all_equal_to 0x1.0000000000004p+53 (red3 1 1 1 3 two53) = true.
Proof. split; reflexivity. Qed.

(* ===================================================================== *)
(* Part C.  Exceptional values.  The reduced form can overflow, or       *)
(* produce NaN, where the original is finite.                            *)
(* ===================================================================== *)

Definition big : float := 0x1p+1023.

Theorem binary64_reduced_overflows :
  orig2 0x1p-2 0 big big = 0x1p+1022 /\
  red2 0x1p-2 0 big big = infinity.
Proof. split; reflexivity. Qed.

Theorem binary64_reduced_nan :
  orig2 0 1 big big = 2 /\
  PrimFloat.is_nan (red2 0 1 big big) = true.
Proof. split; reflexivity. Qed.

(* ===================================================================== *)
(* Part D.  WHY THE FLOAT SUMMARY IS SHIFTED.  Three particles at 1e8,   *)
(* 1e8 + 1, 1e8 + 2; exact variance 2/3.  From raw power sums,           *)
(* Q / N - (S / N)^2 returns 2.  From the shifted sum about the mean,    *)
(* M2 / N returns 2/3 correctly rounded.                                 *)
(* ===================================================================== *)

Definition p1 : float := 100000000.
Definition p2 : float := 100000001.
Definition p3 : float := 100000002.

Definition raw_var : float :=
  let S := (p1 + p2) + p3 in
  let Q := (p1 * p1 + p2 * p2) + p3 * p3 in
  Q / 3 - (S / 3) * (S / 3).

Definition central_var : float :=
  let m := ((p1 + p2) + p3) / 3 in
  (((p1 - m) * (p1 - m) + (p2 - m) * (p2 - m)) + (p3 - m) * (p3 - m)) / 3.

Theorem binary64_raw_moments_lose_the_variance :
  raw_var = 2 /\ central_var = 0x1.5555555555555p-1.
Proof. split; reflexivity. Qed.
