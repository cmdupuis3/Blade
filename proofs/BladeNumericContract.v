(* ===================================================================== *)
(* BladeNumericContract.v -- EXACT RECURRENCE REDUCTION, the numerical   *)
(* contract (docs/research/exact-recurrence-reduction-proofs.md, 11.2).  *)
(*                                                                       *)
(* The certificate theorems are ring identities.  Section 11.2 asks      *)
(* which arithmetic they may be run in.  Two answers are exact, and one  *)
(* is a refusal; all three are proved here, with no axiom.               *)
(*                                                                       *)
(*   Zm, Zm_ring_theory        integers modulo m as a ring with LEIBNIZ  *)
(*                             equality (proofs of a boolean equation    *)
(*                             are unique -- Eqdep_dec, no axiom);       *)
(*   wrapped_moment_reduction_sound   BladeMomentClosure's theorem read  *)
(*                             in it: reduction is exact in wrapping     *)
(*                             arithmetic, overflow included;            *)
(*   wrapped_observation_exact,       the wrapped REDUCED run carries    *)
(*                             the                                       *)
(*   int64_observation_exact   residues of the true ORIGINAL moments, so *)
(*                             whenever a true moment is representable   *)
(*                             the signed reading returns it, whatever   *)
(*                             overflowed on the way (m = 2^64);         *)
(*   rational_moment_reduction_sound  the same theorem over canonical    *)
(*                             rationals (Qc);                           *)
(*   rne, fadd, fmul           round-to-nearest, ties-to-even, precision *)
(*                             p, unbounded exponent, on integer data;   *)
(*   rounding_breaks_update_original_inexact,                            *)
(*   rounding_breaks_update_reduced_inexact   at EVERY precision p >= 2  *)
(*                             the S' formula and the particle-wise fold *)
(*                             disagree -- once with the original wrong  *)
(*                             and the reduced form exact, once the      *)
(*                             other way round;                          *)
(*   no_ordering_recovers_the_reduced_value   three particles, p in      *)
(*                             {24, 53, 64, 113}: every ordering and     *)
(*                             parenthesization of the original fold     *)
(*                             gives one value, every ordering of the    *)
(*                             reduced form another.  A reassociation    *)
(*                             license is not a distributivity license.  *)
(*                                                                       *)
(* Scope, stated once.  Blade emits int64_t and does not pass -fwrapv,   *)
(* so signed overflow in the emitted C++ is undefined: the wrapping      *)
(* theorems describe the emitted code only where nothing overflows, or   *)
(* under a wrapping build.  The rounding model has no exponent range and *)
(* integer data; the real binary64 check, by kernel evaluation of        *)
(* primitive floats, is floats/BladeBinary64Witness.v, and the           *)
(* rounding-error bound under the standard model is                      *)
(* reals/BladeRoundingBound.v -- both outside this tower.                *)
(*                                                                       *)
(* Stdlib only (ZArith, QArith.Qcanon, Eqdep_dec, Ring, Lia, List), no   *)
(* axioms.  Imports BladeBinomial, BladeSummary, BladeMomentClosure.     *)
(* ===================================================================== *)

From Blade Require Import BladeBinomial BladeSummary BladeMomentClosure.
Require Import List Arith Lia ZArith Ring Bool Eqdep_dec QArith Qcanon.
Import ListNotations.

(* ===================================================================== *)
(* Part A.  EXACT FRAGMENT 1: integers modulo m, as a ring with Leibniz  *)
(* equality.  Every theorem of BladeMomentClosure is stated over an      *)
(* abstract commutative ring, so it holds here -- wrap-around included.  *)
(* ===================================================================== *)

Section Wrap.
  Variable m : Z.
  Hypothesis Hm : (0 < m)%Z.

  Definition canon (x : Z) : bool := Z.eqb (x mod m) x.

  Definition Zm : Type := { x : Z | canon x = true }.

  Lemma canon_mod : forall x, canon (x mod m) = true.
  Proof.
    intro x. unfold canon. apply Z.eqb_eq. apply Z.mod_mod. lia.
  Qed.

  Definition wrap (x : Z) : Zm := exist _ (x mod m)%Z (canon_mod x).

  Definition val (a : Zm) : Z := proj1_sig a.

  Lemma val_wrap : forall x, val (wrap x) = (x mod m)%Z.
  Proof. reflexivity. Qed.

  Lemma val_canon : forall a : Zm, (val a mod m = val a)%Z.
  Proof. intros [x Hx]. simpl. apply Z.eqb_eq. exact Hx. Qed.

  (* Proofs of a boolean equation are unique: no axiom.                  *)
  Lemma Zm_eq : forall a b : Zm, val a = val b -> a = b.
  Proof.
    intros [x Hx] [y Hy]. simpl. intro E. subst y. f_equal.
    apply UIP_dec. apply bool_dec.
  Qed.

  Lemma wrap_val : forall a : Zm, wrap (val a) = a.
  Proof. intro a. apply Zm_eq. rewrite val_wrap. apply val_canon. Qed.

  Lemma wrap_eq : forall x y, (x mod m = y mod m)%Z -> wrap x = wrap y.
  Proof. intros x y E. apply Zm_eq. rewrite !val_wrap. exact E. Qed.

  Definition zm0 : Zm := wrap 0.
  Definition zm1 : Zm := wrap 1.
  Definition zmadd (a b : Zm) : Zm := wrap (val a + val b)%Z.
  Definition zmmul (a b : Zm) : Zm := wrap (val a * val b)%Z.
  Definition zmsub (a b : Zm) : Zm := wrap (val a - val b)%Z.
  Definition zmopp (a : Zm) : Zm := wrap (- val a)%Z.

  (* wrap is a ring homomorphism Z -> Z/m.                               *)
  Lemma wrap_add : forall x y, wrap (x + y)%Z = zmadd (wrap x) (wrap y).
  Proof.
    intros x y. apply wrap_eq. rewrite !val_wrap. apply Z.add_mod. lia.
  Qed.

  Lemma wrap_mul : forall x y, wrap (x * y)%Z = zmmul (wrap x) (wrap y).
  Proof.
    intros x y. apply wrap_eq. rewrite !val_wrap. apply Z.mul_mod. lia.
  Qed.

  Lemma wrap_sub : forall x y, wrap (x - y)%Z = zmsub (wrap x) (wrap y).
  Proof.
    intros x y. apply wrap_eq. rewrite !val_wrap. apply Zminus_mod.
  Qed.

  Lemma wrap_opp : forall x, wrap (- x)%Z = zmopp (wrap x).
  Proof.
    intros x. apply wrap_eq. rewrite val_wrap.
    replace (- x)%Z with (0 - x)%Z by ring.
    replace (- (x mod m))%Z with (0 - x mod m)%Z by ring.
    rewrite (Zminus_mod 0 x), (Zminus_mod 0 (x mod m)).
    rewrite Z.mod_mod by lia. reflexivity.
  Qed.

  Theorem Zm_ring_theory :
    ring_theory zm0 zm1 zmadd zmmul zmsub zmopp (@eq Zm).
  Proof.
    constructor.
    - intro a. rewrite <- (wrap_val a). unfold zm0.
      rewrite <- wrap_add. f_equal.
    - intros a b. unfold zmadd. f_equal. ring.
    - intros a b c. rewrite <- (wrap_val a), <- (wrap_val b), <- (wrap_val c).
      rewrite <- !wrap_add. f_equal. ring.
    - intro a. rewrite <- (wrap_val a). unfold zm1.
      rewrite <- wrap_mul. f_equal. ring.
    - intros a b. unfold zmmul. f_equal. ring.
    - intros a b c. rewrite <- (wrap_val a), <- (wrap_val b), <- (wrap_val c).
      rewrite <- !wrap_mul. f_equal. ring.
    - intros a b c. rewrite <- (wrap_val a), <- (wrap_val b), <- (wrap_val c).
      rewrite <- !wrap_add, <- !wrap_mul, <- wrap_add. f_equal. ring.
    - intros a b. rewrite <- (wrap_val a), <- (wrap_val b).
      rewrite <- wrap_sub, <- wrap_opp, <- wrap_add. f_equal.
    - intro a. rewrite <- (wrap_val a). unfold zm0.
      rewrite <- wrap_opp, <- wrap_add. f_equal. ring.
  Qed.

  Local Notation Wsum := (rsum Zm zm0 zmadd).
  Local Notation Wpow := (rpow Zm zm1 zmmul).
  Local Notation Wnat := (ofnat Zm zm0 zm1 zmadd).
  Local Notation Wpsum := (psum Zm zm0 zm1 zmadd zmmul).
  Local Notation Zsum := (rsum Z 0%Z Z.add).
  Local Notation Zpow := (rpow Z 1%Z Z.mul).
  Local Notation Znat := (ofnat Z 0%Z 1%Z Z.add).
  Local Notation Zpsum := (psum Z 0%Z 1%Z Z.add Z.mul).

  Lemma wrap_rsum : forall l, wrap (Zsum l) = Wsum (map wrap l).
  Proof.
    induction l as [|x l IH]; [reflexivity|].
    cbn [rsum map]. rewrite wrap_add, IH. reflexivity.
  Qed.

  Lemma wrap_rpow : forall x k, wrap (Zpow x k) = Wpow (wrap x) k.
  Proof.
    intros x. induction k as [|k IH]; [reflexivity|].
    cbn [rpow]. rewrite wrap_mul, IH. reflexivity.
  Qed.

  Lemma wrap_ofnat : forall n, wrap (Znat n) = Wnat n.
  Proof.
    induction n as [|n IH]; [reflexivity|].
    cbn [ofnat]. rewrite wrap_add, IH. reflexivity.
  Qed.

  Lemma wrap_psum : forall k xs,
    wrap (Zpsum k xs) = Wpsum k (map wrap xs).
  Proof.
    intros k xs. unfold psum. rewrite wrap_rsum, !map_map. f_equal.
    apply map_ext. intro x. apply wrap_rpow.
  Qed.

  (* The reduction theorem, read in wrapping arithmetic: a direct        *)
  (* instance.  Coefficients are arbitrary functions of the WRAPPED      *)
  (* summary; every product and sum may overflow.                        *)
  Theorem wrapped_moment_reduction_sound : forall (U Y : Type) (r N : nat)
      (alpha beta : U -> list Zm -> Zm) (hb : list Zm -> Y) w xs,
    length xs = N ->
    trace (Fm Zm zm0 zm1 zmadd zmmul U r alpha beta)
          (fun xs => hb (qr Zm zm0 zm1 zmadd zmmul r xs)) w xs
    = trace (Gm Zm zm0 zm1 zmadd zmmul U r N alpha beta) hb w
            (qr Zm zm0 zm1 zmadd zmmul r xs).
  Proof.
    intros U Y r N alpha beta hb w xs HN.
    apply (moment_reduction_sound Zm zm0 zm1 zmadd zmmul zmsub zmopp
             Zm_ring_theory U r N alpha beta Y hb w xs HN).
  Qed.

  (* ------------------------------------------------------------------- *)
  (* Wrapped versus true integers.  Coefficients computed from the       *)
  (* summary by ring operations commute with wrap; then the wrapped      *)
  (* REDUCED run carries the residues of the true ORIGINAL moments.      *)
  (* ------------------------------------------------------------------- *)

  Lemma wrap_qr : forall r xs,
    map wrap (qr Z 0%Z 1%Z Z.add Z.mul r xs)
    = qr Zm zm0 zm1 zmadd zmmul r (map wrap xs).
  Proof.
    intros r xs. unfold qr. rewrite map_map. apply map_ext.
    intro k. apply wrap_psum.
  Qed.

  Lemma wrap_mom : forall N z j,
    wrap (mom Z 0%Z 1%Z Z.add N z j)
    = mom Zm zm0 zm1 zmadd N (map wrap z) j.
  Proof.
    intros N z j. destruct j as [|j]; cbn [mom].
    - apply wrap_ofnat.
    - change zm0 with (wrap 0). rewrite map_nth. reflexivity.
  Qed.

  Lemma wrap_Gm : forall (U : Type) r N
      (alpha beta : U -> list Z -> Z) (alphaW betaW : U -> list Zm -> Zm),
    (forall u z, alphaW u (map wrap z) = wrap (alpha u z)) ->
    (forall u z, betaW u (map wrap z) = wrap (beta u z)) ->
    forall u z,
      map wrap (Gm Z 0%Z 1%Z Z.add Z.mul U r N alpha beta u z)
      = Gm Zm zm0 zm1 zmadd zmmul U r N alphaW betaW u (map wrap z).
  Proof.
    intros U r N alpha beta alphaW betaW Ha Hb u z. unfold Gm.
    rewrite map_map. apply map_ext. intro k.
    rewrite wrap_rsum, map_map. f_equal. apply map_ext. intro j.
    rewrite !wrap_mul, wrap_ofnat, !wrap_rpow, wrap_mom, Ha, Hb.
    reflexivity.
  Qed.

  Theorem wrapped_reduced_run_is_residue : forall (U : Type) r N
      (alpha beta : U -> list Z -> Z) (alphaW betaW : U -> list Zm -> Zm),
    (forall u z, alphaW u (map wrap z) = wrap (alpha u z)) ->
    (forall u z, betaW u (map wrap z) = wrap (beta u z)) ->
    forall w xs, length xs = N ->
      run (Gm Zm zm0 zm1 zmadd zmmul U r N alphaW betaW) w
          (map wrap (qr Z 0%Z 1%Z Z.add Z.mul r xs))
      = map wrap (qr Z 0%Z 1%Z Z.add Z.mul r
                    (run (Fm Z 0%Z 1%Z Z.add Z.mul U r alpha beta) w xs)).
  Proof.
    intros U r N alpha beta alphaW betaW Ha Hb w.
    induction w as [|u w IH]; intros xs HN; [reflexivity|].
    rewrite !run_cons.
    rewrite <- (wrap_Gm U r N alpha beta alphaW betaW Ha Hb).
    rewrite <- (moment_step_closed Z 0%Z 1%Z Z.add Z.mul Z.sub Z.opp
                  Zth U r N alpha beta u xs HN).
    apply IH. unfold Fm. rewrite map_length. exact HN.
  Qed.

  (* The signed reading of a residue, for an even modulus: the           *)
  (* representative in [-m/2, m/2).                                      *)
  Definition sread (a : Zm) : Z :=
    if Z.ltb (val a) (m / 2) then val a else (val a - m)%Z.

  Lemma sread_wrap : forall v,
    (m mod 2 = 0)%Z -> (- (m / 2) <= v < m / 2)%Z -> sread (wrap v) = v.
  Proof.
    intros v Hev Hv. unfold sread. rewrite val_wrap.
    assert (Hm2 : (m = 2 * (m / 2))%Z).
    { rewrite (Z.div_mod m 2) at 1 by lia. lia. }
    destruct (Z_lt_ge_dec v 0) as [Hneg|Hpos].
    - assert (E : (v mod m = v + m)%Z).
      { symmetry. apply (Z.mod_unique_pos v m (-1)); lia. }
      rewrite E. destruct (Z.ltb_spec (v + m) (m / 2)); lia.
    - rewrite Z.mod_small by lia.
      destruct (Z.ltb_spec v (m / 2)); lia.
  Qed.

  (* THE INTEGER CONTRACT.  Run the reduced program in wrapping          *)
  (* arithmetic.  Whenever the TRUE k-th moment of the original          *)
  (* execution is representable, the signed reading returns it exactly,  *)
  (* whatever overflowed on the way.                                     *)
  Theorem wrapped_observation_exact : forall (U : Type) r N
      (alpha beta : U -> list Z -> Z) (alphaW betaW : U -> list Zm -> Zm),
    (m mod 2 = 0)%Z ->
    (forall u z, alphaW u (map wrap z) = wrap (alpha u z)) ->
    (forall u z, betaW u (map wrap z) = wrap (beta u z)) ->
    forall w xs k, length xs = N -> (k < r)%nat ->
      let truth := psum Z 0%Z 1%Z Z.add Z.mul (S k)
                     (run (Fm Z 0%Z 1%Z Z.add Z.mul U r alpha beta) w xs) in
      (- (m / 2) <= truth < m / 2)%Z ->
      sread (nth k (run (Gm Zm zm0 zm1 zmadd zmmul U r N alphaW betaW) w
                        (map wrap (qr Z 0%Z 1%Z Z.add Z.mul r xs))) zm0)
      = truth.
  Proof.
    intros U r N alpha beta alphaW betaW Hev Ha Hb w xs k HN Hk truth Hrep.
    rewrite (wrapped_reduced_run_is_residue U r N alpha beta alphaW betaW
               Ha Hb w xs HN).
    change zm0 with (wrap 0). rewrite map_nth.
    unfold qr. rewrite (nth_map_lt Z nat _ (seq 1 r) k 0%Z 0%nat)
      by (rewrite seq_length; exact Hk).
    rewrite seq_nth by exact Hk. apply sread_wrap; assumption.
  Qed.

End Wrap.

(* 64-bit two's complement is the instance m = 2^64.                     *)
Definition int64_modulus : Z := (2 ^ 64)%Z.

Lemma int64_modulus_pos : (0 < int64_modulus)%Z.
Proof. reflexivity. Qed.

Corollary int64_observation_exact : forall (U : Type) r N
    (alpha beta : U -> list Z -> Z)
    (alphaW betaW : U -> list (Zm int64_modulus) -> Zm int64_modulus),
  (forall u z, alphaW u (map (wrap int64_modulus int64_modulus_pos) z)
               = wrap int64_modulus int64_modulus_pos (alpha u z)) ->
  (forall u z, betaW u (map (wrap int64_modulus int64_modulus_pos) z)
               = wrap int64_modulus int64_modulus_pos (beta u z)) ->
  forall w xs k, length xs = N -> (k < r)%nat ->
    let truth := psum Z 0%Z 1%Z Z.add Z.mul (S k)
                   (run (Fm Z 0%Z 1%Z Z.add Z.mul U r alpha beta) w xs) in
    (- 2 ^ 63 <= truth < 2 ^ 63)%Z ->
    sread int64_modulus
      (nth k (run (Gm (Zm int64_modulus)
                      (zm0 int64_modulus int64_modulus_pos)
                      (zm1 int64_modulus int64_modulus_pos)
                      (zmadd int64_modulus int64_modulus_pos)
                      (zmmul int64_modulus int64_modulus_pos)
                      U r N alphaW betaW) w
                  (map (wrap int64_modulus int64_modulus_pos)
                       (qr Z 0%Z 1%Z Z.add Z.mul r xs)))
             (zm0 int64_modulus int64_modulus_pos))
    = truth.
Proof.
  intros U r N alpha beta alphaW betaW Ha Hb w xs k HN Hk truth Hrep.
  apply (wrapped_observation_exact int64_modulus int64_modulus_pos U r N
           alpha beta alphaW betaW); try assumption; reflexivity.
Qed.

(* ===================================================================== *)
(* Part B.  EXACT FRAGMENT 2: canonical rationals.                       *)
(* ===================================================================== *)

Theorem rational_moment_reduction_sound : forall (U Y : Type) (r N : nat)
    (alpha beta : U -> list Qc -> Qc) (hb : list Qc -> Y) w xs,
  length xs = N ->
  trace (Fm Qc 0%Qc 1%Qc Qcplus Qcmult U r alpha beta)
        (fun xs => hb (qr Qc 0%Qc 1%Qc Qcplus Qcmult r xs)) w xs
  = trace (Gm Qc 0%Qc 1%Qc Qcplus Qcmult U r N alpha beta) hb w
          (qr Qc 0%Qc 1%Qc Qcplus Qcmult r xs).
Proof.
  intros U Y r N alpha beta hb w xs HN.
  apply (moment_reduction_sound Qc 0%Qc 1%Qc Qcplus Qcmult Qcminus Qcopp
           Qcrt U r N alpha beta Y hb w xs HN).
Qed.

(* ===================================================================== *)
(* Part C.  ROUNDED ARITHMETIC IS NOT A FRAGMENT.  Round-to-nearest,     *)
(* ties-to-even, at ANY precision p >= 2 (unbounded exponent, integer    *)
(* data): the S' formula and the particle-wise fold disagree, and        *)
(* neither is the uniformly accurate one.                                *)
(* ===================================================================== *)

Open Scope Z_scope.

Definition rne (p x : Z) : Z :=
  let ax := Z.abs x in
  let s := 2 ^ Z.max 0 (Z.log2 ax + 1 - p) in
  let q := ax / s in
  let rm := ax mod s in
  let q' := if 2 * rm <? s then q
            else if s <? 2 * rm then q + 1
            else if Z.even q then q else q + 1 in
  Z.sgn x * (q' * s).

Definition fadd (p x y : Z) : Z := rne p (x + y).
Definition fmul (p x y : Z) : Z := rne p (x * y).

Lemma rne_opp : forall p x, rne p (- x) = - rne p x.
Proof.
  intros p x. unfold rne. rewrite Z.abs_opp, Z.sgn_opp. ring.
Qed.

(* p significant bits hold every integer below 2^p exactly.              *)
Lemma rne_exact : forall p x, 1 <= p -> Z.abs x < 2 ^ p -> rne p x = x.
Proof.
  intros p x Hp Hx. unfold rne.
  assert (He : Z.max 0 (Z.log2 (Z.abs x) + 1 - p) = 0).
  { destruct (Z.eq_dec (Z.abs x) 0) as [E|E].
    - rewrite E. change (Z.log2 0) with 0. lia.
    - assert (Z.log2 (Z.abs x) < p) by (apply Z.log2_lt_pow2; lia). lia. }
  rewrite He. change (2 ^ 0) with 1.
  rewrite Z.div_1_r, Z.mod_1_r. change (2 * 0 <? 1) with true. cbv iota.
  rewrite Z.mul_1_r, Z.mul_comm. apply Z.abs_sgn.
Qed.

(* The binade [2^p, 2^(p+1)): spacing 2.                                 *)
Lemma binade_log2 : forall p t, 1 <= p -> 2 ^ (p - 1) <= t < 2 ^ p ->
  forall d, 0 <= d <= 1 -> Z.log2 (2 * t + d) = p.
Proof.
  intros p t Hp Ht d Hd. apply Z.log2_unique; [lia|].
  replace (p + 1) with (Z.succ p) by lia. rewrite Z.pow_succ_r by lia.
  replace p with (Z.succ (p - 1)) at 1 by lia.
  rewrite Z.pow_succ_r by lia. lia.
Qed.

Lemma rne_binade_even : forall p t, 1 <= p -> 2 ^ (p - 1) <= t < 2 ^ p ->
  rne p (2 * t) = 2 * t.
Proof.
  intros p t Hp Ht. unfold rne.
  assert (Hpos : 0 < 2 ^ (p - 1)) by (apply Z.pow_pos_nonneg; lia).
  rewrite Z.abs_eq by lia.
  pose proof (binade_log2 p t Hp Ht 0 ltac:(lia)) as HL.
  rewrite Z.add_0_r in HL. rewrite HL.
  replace (Z.max 0 (p + 1 - p)) with 1 by lia. change (2 ^ 1) with 2.
  replace (2 * t) with (t * 2) by ring.
  rewrite Z.div_mul, Z.mod_mul by lia. rewrite Z.sgn_pos by lia.
  change (2 * 0 <? 2) with true. cbv iota. ring.
Qed.

Lemma rne_binade_odd : forall p t, 1 <= p -> 2 ^ (p - 1) <= t < 2 ^ p ->
  rne p (2 * t + 1) = if Z.even t then 2 * t else 2 * t + 2.
Proof.
  intros p t Hp Ht. unfold rne.
  assert (Hpos : 0 < 2 ^ (p - 1)) by (apply Z.pow_pos_nonneg; lia).
  rewrite Z.abs_eq by lia.
  rewrite (binade_log2 p t Hp Ht 1 ltac:(lia)).
  replace (Z.max 0 (p + 1 - p)) with 1 by lia. change (2 ^ 1) with 2.
  assert (Hq : (2 * t + 1) / 2 = t).
  { symmetry. apply (Z.div_unique_pos (2 * t + 1) 2 t 1); lia. }
  assert (Hr : (2 * t + 1) mod 2 = 1).
  { symmetry. apply (Z.mod_unique_pos (2 * t + 1) 2 t 1); lia. }
  rewrite Hq, Hr. rewrite Z.sgn_pos by lia.
  change (2 * 1 <? 2) with false. change (2 <? 2 * 1) with false. cbv iota.
  destruct (Z.even t); ring.
Qed.

Lemma pow2_split : forall p, 1 <= p -> 2 ^ p = 2 * 2 ^ (p - 1).
Proof.
  intros p Hp. replace p with (Z.succ (p - 1)) at 1 by lia.
  apply Z.pow_succ_r. lia.
Qed.

Lemma rne_pow2 : forall p, 1 <= p -> rne p (2 ^ p) = 2 ^ p.
Proof.
  intros p Hp. rewrite (pow2_split p Hp). apply rne_binade_even; [exact Hp|].
  assert (0 < 2 ^ (p - 1)) by (apply Z.pow_pos_nonneg; lia).
  split; [lia|]. rewrite (pow2_split p Hp). lia.
Qed.

(* The tie: 2^p + 1 lies halfway between 2^p and 2^p + 2, and 2^p has    *)
(* the even significand.                                                 *)
Lemma rne_tie : forall p, 2 <= p -> rne p (2 ^ p + 1) = 2 ^ p.
Proof.
  intros p Hp. rewrite (pow2_split p ltac:(lia)).
  assert (Hpos : 0 < 2 ^ (p - 1)) by (apply Z.pow_pos_nonneg; lia).
  rewrite rne_binade_odd.
  - rewrite Z.even_pow by lia. reflexivity.
  - lia.
  - split; [lia|]. rewrite (pow2_split p ltac:(lia)). lia.
Qed.

Lemma pow2_gt : forall p, 2 <= p -> 4 <= 2 ^ p.
Proof.
  intros p Hp. change 4 with (2 ^ 2). apply Z.pow_le_mono_r; lia.
Qed.

(* Two particles, a = b = 1.  With N = 2 there is one summation order up *)
(* to commutativity, so no reassociation license changes either side.    *)
Definition orig2 (p a b x1 x2 : Z) : Z :=
  fadd p (fadd p (fmul p a x1) b) (fadd p (fmul p a x2) b).
Definition red2 (p a b x1 x2 : Z) : Z :=
  fadd p (fmul p a (fadd p x1 x2)) (fmul p 2 b).

(* The exact answer is 2.  The ORIGINAL loses it; the reduced form is    *)
(* exact.                                                                *)
Theorem rounding_breaks_update_original_inexact : forall p, 2 <= p ->
  orig2 p 1 1 (2 ^ p) (- 2 ^ p) = 1 /\ red2 p 1 1 (2 ^ p) (- 2 ^ p) = 2.
Proof.
  intros p Hp. pose proof (pow2_gt p Hp) as H4.
  unfold orig2, red2, fadd, fmul. split.
  - rewrite !Z.mul_1_l, rne_opp, (rne_pow2 p ltac:(lia)), (rne_tie p Hp).
    rewrite (rne_exact p (- 2 ^ p + 1)) by lia.
    replace (2 ^ p + (- 2 ^ p + 1)) with 1 by ring.
    apply rne_exact; lia.
  - replace (2 ^ p + - 2 ^ p) with 0 by ring.
    rewrite (rne_exact p 0) by lia. rewrite Z.mul_1_l.
    rewrite (rne_exact p 0) by lia. change (2 * 1) with 2.
    rewrite (rne_exact p 2) by lia. apply rne_exact; lia.
Qed.

(* The exact answer is -2^p + 1.  The original is exact; the REDUCED     *)
(* form loses it.                                                        *)
Theorem rounding_breaks_update_reduced_inexact : forall p, 2 <= p ->
  orig2 p 1 1 (- 2 ^ p) (-1) = - 2 ^ p + 1 /\
  red2 p 1 1 (- 2 ^ p) (-1) = - 2 ^ p + 2.
Proof.
  intros p Hp. pose proof (pow2_gt p Hp) as H4.
  unfold orig2, red2, fadd, fmul. split.
  - rewrite !Z.mul_1_l, rne_opp, (rne_pow2 p ltac:(lia)).
    rewrite (rne_exact p (- 2 ^ p + 1)) by lia.
    rewrite (rne_exact p (-1)) by lia. change (-1 + 1) with 0.
    rewrite (rne_exact p 0) by lia. rewrite Z.add_0_r.
    apply rne_exact; lia.
  - replace (- 2 ^ p + -1) with (- (2 ^ p + 1)) by ring.
    rewrite rne_opp, (rne_tie p Hp), Z.mul_1_l, rne_opp,
      (rne_pow2 p ltac:(lia)).
    change (2 * 1) with 2. rewrite (rne_exact p 2) by lia.
    apply rne_exact; lia.
Qed.

Corollary rounded_update_not_closed : forall p, 2 <= p ->
  exists a b x1 x2, orig2 p a b x1 x2 <> red2 p a b x1 x2.
Proof.
  intros p Hp. exists 1, 1, (2 ^ p), (- 2 ^ p).
  destruct (rounding_breaks_update_original_inexact p Hp) as [-> ->]. lia.
Qed.

(* --------------------------------------------------------------------- *)
(* A reassociation license is not a distributivity license.  Three       *)
(* particles: EVERY ordering and parenthesization of the original fold   *)
(* gives one value, every ordering of the reduced form another.          *)
(* --------------------------------------------------------------------- *)

Definition orders3 (p y1 y2 y3 : Z) : list Z :=
  flat_map (fun t : Z * Z * Z =>
              let '(u, v, w) := t in
              [fadd p (fadd p u v) w; fadd p u (fadd p v w)])
           [(y1, y2, y3); (y1, y3, y2); (y2, y1, y3);
            (y2, y3, y1); (y3, y1, y2); (y3, y2, y1)].

Definition orig3 (p a b x1 x2 x3 : Z) : list Z :=
  orders3 p (fadd p (fmul p a x1) b) (fadd p (fmul p a x2) b)
            (fadd p (fmul p a x3) b).

Definition red3 (p a b x1 x2 x3 : Z) : list Z :=
  flat_map (fun S => [fadd p (fmul p a S) (fmul p 3 b);
                      fadd p (fmul p 3 b) (fmul p a S)])
           (orders3 p x1 x2 x3).

Definition disjointb (l1 l2 : list Z) : bool :=
  forallb (fun v => negb (existsb (Z.eqb v) l2)) l1.

Lemma disjointb_sound : forall l1 l2, disjointb l1 l2 = true ->
  forall v, In v l1 -> ~ In v l2.
Proof.
  intros l1 l2 H v H1 H2. unfold disjointb in H.
  rewrite forallb_forall in H. specialize (H v H1).
  apply negb_true_iff in H.
  assert (existsb (Z.eqb v) l2 = true)
    by (apply existsb_exists; exists v; split; [exact H2|apply Z.eqb_refl]).
  congruence.
Qed.

(* binary32, binary64, x87 extended, binary128.                          *)
Theorem no_ordering_recovers_the_reduced_value : forall p,
  In p [24; 53; 64; 113] ->
  forall v, In v (orig3 p 1 1 1 3 (2 ^ p)) ->
            ~ In v (red3 p 1 1 1 3 (2 ^ p)).
Proof.
  intros p Hp. apply disjointb_sound.
  cbn [In] in Hp.
  destruct Hp as [<-|[<-|[<-|[<-|[]]]]]; vm_compute; reflexivity.
Qed.

Close Scope Z_scope.
