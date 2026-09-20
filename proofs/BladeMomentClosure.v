(* ===================================================================== *)
(* BladeMomentClosure.v -- EXACT RECURRENCE REDUCTION, the algebra:      *)
(* which summaries of a shared-coefficient particle update close, which  *)
(* provably do not, and derivatives without analysis                     *)
(* (docs/research/exact-recurrence-reduction-proofs.md, P4, P5, P8-P10,  *)
(* and polynomial forms of P4a, P6, P7).                                 *)
(*                                                                       *)
(* BladeSummary says what a certificate buys.  This file builds one,     *)
(* over an ABSTRACT commutative ring (a ring_theory; instantiations Z,   *)
(* Q, the dual numbers below -- never floating point, which is not a     *)
(* ring).                                                                *)
(*                                                                       *)
(*   affine_sum_closed,        P4 at r = 2: under x_i' = a x_i + b,      *)
(*   affine_square_sum_closed  S' = a S + N b,                           *)
(*                             Q' = a^2 Q + 2ab S + N b^2;               *)
(*   affine_moment_tower_closed  P4 at every k and every extent:         *)
(*                             p_k' = sum_j C(k,j) a^j b^(k-j) p_j, with *)
(*                             the tower's own Pascal C (bcl_binomial);  *)
(*   moment_certificate,       a and b ARBITRARY functions of the input  *)
(*   moment_reduction_sound    and the current summary, shared across    *)
(*                             the array: r coordinates carry every      *)
(*                             prefix observation of every finite        *)
(*                             execution, at every extent N (N a         *)
(*                             parameter of G).                          *)
(*   tree_summary_sound        P2's non-time example: singleton, concat  *)
(*                             and affine trees summarized by (n, S, Q). *)
(*   squaring_observes_dyadic_moments  t squarings observe p_(2^t).      *)
(*   affine_update_unit_covariant  units (the draft's 11.3): rescale x   *)
(*                             and b by c, keep a dimensionless, and p_k *)
(*                             rescales by c^k -- the summary is a       *)
(*                             GRADED product and the update respects    *)
(*                             it.                                       *)
(*                                                                       *)
(*   generators_closed_algebra_closed  P9: F* maps R[q_1, q_2, ...] into *)
(*                             itself as soon as it does on generators;  *)
(*   poly_certificate_sound    supplied expressions for G and hb ARE a   *)
(*                             certificate (poly_certificate_affine_N3:  *)
(*                             checked by `ring` -- at a FIXED extent; a *)
(*                             P9 certificate does not scale in N, the   *)
(*                             tower theorem above is the uniform one).  *)
(*                                                                       *)
(* DERIVATIVES WITHOUT ANALYSIS.  The dual numbers R[eps]/(eps^2) are a  *)
(* commutative ring (dual_ring_theory), so every theorem above holds AT  *)
(* them, and the tangent part of an identity is its derivative:          *)
(*                                                                       *)
(*   dual_psum                 Dq for the moment summary: the tangent of *)
(*                             p_(k+1) along v is (k+1) sum_i x_i^k v_i; *)
(*   moment_tangent_sound      P5a for the fragment, every finite        *)
(*                             execution: push (x, v) through the        *)
(*                             particle system then summarize =          *)
(*                             summarize then push through the reduced   *)
(*                             system.  No chain rule was invoked; it is *)
(*                             Part A at another ring;                   *)
(*   pullback_SQ               the draft's cotangent pullback,           *)
(*                             d loss / d x_i = lam_S + 2 x_i lam_Q.     *)
(*                                                                       *)
(* REFUSALS.  Against every update function, by one collision            *)
(* (BladeSummary.collision_refutes_update):                              *)
(*                                                                       *)
(*   same_SQ_674_1250          the draft's counterexample, pinned;       *)
(*   square_not_closed_SQ      squaring admits no update of (S, Q) at    *)
(*                             any extent N >= 3, even on strictly       *)
(*                             positive arrays ((1,5,6) / (2,3,7),       *)
(*                             padded);                                  *)
(*   square_SQ_threshold       and the threshold is EXACT: the           *)
(*                             functional relation holds iff N <= 2 (by  *)
(*                             two_point_newton).  The draft's P8 bound  *)
(*                             N >= d r = 4 is sufficient, not sharp;    *)
(*   power_not_closed_S        x -> x^d never closes the sum alone,      *)
(*                             every d >= 2, every N >= 2.               *)
(*                                                                       *)
(* Against every POLYNOMIAL encoding, by formal Jacobian rank -- the     *)
(* chain rule is evaluation in the dual numbers, the rank bound is       *)
(* `ring` at a fixed size:                                               *)
(*                                                                       *)
(*   poly_rank_bound_1_2,      one polynomial coordinate cannot carry    *)
(*   poly_rank_bound_2_3       two observations with a nonzero 2 x 2     *)
(*                             minor, nor two coordinates three with a   *)
(*                             nonzero 3 x 3 one;                        *)
(*   SQ_needs_two_coordinates  P4a at r = 2;                             *)
(*   squaring_three_steps_need_three_coordinates  P7 at N = H = 3, minor *)
(*                             96 at (1, 2, 3) (squaring_minor_N3).      *)
(*                                                                       *)
(* P10, the trap for synthesis:                                          *)
(*                                                                       *)
(*   p10_trajectory            F(x, y) = (x y, y) observed at x: x y^t;  *)
(*   p10_next_observable_is_new  x y^(T+1) is NOT a polynomial in        *)
(*                             x, x y, ..., x y^T -- the ascending chain *)
(*                             of observable subalgebras never           *)
(*                             stabilizes, so adjoining future           *)
(*                             observations does not terminate, though   *)
(*                             q = (x, y) is exact;                      *)
(*   p10_needs_two_coordinates,  and that state cannot be shrunk:        *)
(*   p10_no_identification     not to one polynomial coordinate, and no  *)
(*                             certified summary of any kind identifies  *)
(*                             two states with x, y nonzero.             *)
(*                                                                       *)
(* Scope, stated once.  The rank bound here stops at sizes (1,2) and     *)
(* (2,3), where `ring` expands the minor; BladeRankBound takes it to     *)
(* every size (D_factor and lift_basis below are its interface), and     *)
(* with it P4a at every r, P7 at every N and horizon, P8's refusal for   *)
(* pure powers at every d and r, and P10 to "not finitely generated".    *)
(* NOT REACHED in either file: anything against C^1 encodings (Rolle,    *)
(* inverse function theorem -- Coq's Reals are axiomatic and this tower  *)
(* is axiom-free); P8 for the general fragment with coefficient          *)
(* polynomials; P5 for reverse mode as Blade emits it, and any link to   *)
(* Grad*.fs.  A polynomial identity is read as valid in the dual numbers *)
(* over the base ring, which is what a symbolic certificate provides; an *)
(* equation between FUNCTIONS on Z is a weaker hypothesis and is not     *)
(* what the rank bounds assume. Exact arithmetic throughout; no source   *)
(* fragment, no recognition, no code generation.                         *)
(*                                                                       *)
(* Imports BladeBinomial, BladeSummary.  Coq 8.18, stdlib only.          *)
(* ===================================================================== *)

From Blade Require Import BladeBinomial BladeSummary.
Require Import List Arith Lia ZArith Ring.
Import ListNotations.

(* ===================================================================== *)
(* Part A.  Finite-sum algebra over an abstract commutative ring.        *)
(* ===================================================================== *)

Section Moments.
  Variable R : Type.
  Variables (r0 r1 : R) (radd rmul rsub : R -> R -> R) (ropp : R -> R).
  Hypothesis Rth : ring_theory r0 r1 radd rmul rsub ropp (@eq R).
  Add Ring moment_ring : Rth.

  Local Infix "+!" := radd (at level 50, left associativity).
  Local Infix "*!" := rmul (at level 40, left associativity).
  Local Infix "-!" := rsub (at level 50, left associativity).

  Fixpoint rsum (l : list R) : R :=
    match l with [] => r0 | x :: l' => x +! rsum l' end.

  Fixpoint rpow (x : R) (k : nat) : R :=
    match k with 0 => r1 | S k' => x *! rpow x k' end.

  Fixpoint ofnat (n : nat) : R :=
    match n with 0 => r0 | S n' => r1 +! ofnat n' end.

  (* The k-th power sum p_k; p_0 is the extent, read in R.               *)
  Definition psum (k : nat) (xs : list R) : R :=
    rsum (map (fun x => rpow x k) xs).

  Lemma psum_cons : forall k x xs, psum k (x :: xs) = rpow x k +! psum k xs.
  Proof. reflexivity. Qed.

  Lemma rsum_app : forall l1 l2, rsum (l1 ++ l2) = rsum l1 +! rsum l2.
  Proof.
    induction l1 as [|x l1 IH]; intros l2; simpl; [ring|].
    rewrite IH. ring.
  Qed.

  Lemma psum_app : forall k xs ys,
    psum k (xs ++ ys) = psum k xs +! psum k ys.
  Proof. intros. unfold psum. rewrite map_app. apply rsum_app. Qed.

  Lemma ofnat_add : forall n m, ofnat (n + m) = ofnat n +! ofnat m.
  Proof.
    induction n as [|n IH]; intros m; simpl; [ring|]. rewrite IH. ring.
  Qed.

  Lemma psum_0 : forall xs, psum 0 xs = ofnat (length xs).
  Proof.
    unfold psum. induction xs as [|x xs IH]; simpl in *; [reflexivity|].
    rewrite IH. reflexivity.
  Qed.

  Lemma nth_map_lt : forall (A : Type) (f : A -> R) l j d d',
    j < length l -> nth j (map f l) d = f (nth j l d').
  Proof.
    intros A f l j d d' Hj.
    rewrite (nth_indep _ d (f d')) by (rewrite map_length; exact Hj).
    apply map_nth.
  Qed.

  (* The shared-coefficient affine update of one particle.               *)
  Definition aff (a b x : R) : R := a *! x +! b.

  (* P4 at r = 2, the draft's (M2).                                      *)
  Theorem affine_sum_closed : forall a b xs,
    psum 1 (map (aff a b) xs)
    = a *! psum 1 xs +! ofnat (length xs) *! b.
  Proof.
    intros a b xs. unfold psum, aff.
    induction xs as [|x xs IH]; simpl in *; [ring|]. rewrite IH. ring.
  Qed.

  Theorem affine_square_sum_closed : forall a b xs,
    psum 2 (map (aff a b) xs)
    = a *! a *! psum 2 xs +! (r1 +! r1) *! a *! b *! psum 1 xs
      +! ofnat (length xs) *! b *! b.
  Proof.
    intros a b xs. unfold psum, aff.
    induction xs as [|x xs IH]; simpl in *; [ring|]. rewrite IH. ring.
  Qed.

  (* ------------------------------------------------------------------- *)
  (* Generic r.  (a x + b)^k as a coefficient list, built by the         *)
  (* recurrence  c_(k+1) = b c_k + a X c_k;  summing a polynomial over   *)
  (* the particles turns coefficients of X^j into coefficients of p_j.   *)
  (* ------------------------------------------------------------------- *)

  Fixpoint padd (l1 l2 : list R) : list R :=
    match l1, l2 with
    | [], _ => l2
    | _, [] => l1
    | x :: l1', y :: l2' => (x +! y) :: padd l1' l2'
    end.

  Definition pscale (c : R) (l : list R) : list R :=
    map (fun x => c *! x) l.

  Fixpoint peval (l : list R) (x : R) : R :=
    match l with [] => r0 | c :: l' => c +! x *! peval l' x end.

  Lemma peval_padd : forall l1 l2 x,
    peval (padd l1 l2) x = peval l1 x +! peval l2 x.
  Proof.
    induction l1 as [|a l1 IH]; intros [|b l2] x; simpl; try ring.
    rewrite IH. ring.
  Qed.

  Lemma peval_pscale : forall c l x,
    peval (pscale c l) x = c *! peval l x.
  Proof.
    induction l as [|a l IH]; intros x; simpl; [ring|]. rewrite IH. ring.
  Qed.

  Fixpoint bcl (a b : R) (k : nat) : list R :=
    match k with
    | 0 => [r1]
    | S k' => padd (pscale b (bcl a b k')) (r0 :: pscale a (bcl a b k'))
    end.

  Lemma peval_bcl : forall a b k x,
    peval (bcl a b k) x = rpow (a *! x +! b) k.
  Proof.
    induction k as [|k IH]; intros x; simpl; [ring|].
    rewrite peval_padd. simpl. rewrite !peval_pscale, IH. ring.
  Qed.

  Lemma padd_length : forall l1 l2,
    length (padd l1 l2) = Nat.max (length l1) (length l2).
  Proof.
    induction l1 as [|a l1 IH]; intros [|b l2]; simpl; try reflexivity.
    rewrite IH. reflexivity.
  Qed.

  Lemma bcl_length : forall a b k, length (bcl a b k) = S k.
  Proof.
    induction k as [|k IH]; simpl; [reflexivity|].
    rewrite padd_length. simpl. unfold pscale. rewrite !map_length, IH.
    lia.
  Qed.

  Lemma nth_padd : forall l1 l2 j,
    nth j (padd l1 l2) r0 = nth j l1 r0 +! nth j l2 r0.
  Proof.
    induction l1 as [|a l1 IH]; intros [|b l2] [|j]; simpl; try ring.
    apply IH.
  Qed.

  Lemma nth_pscale : forall c l j,
    nth j (pscale c l) r0 = c *! nth j l r0.
  Proof.
    induction l as [|a l IH]; intros [|j]; simpl; try ring. apply IH.
  Qed.

  (* The coefficients ARE the binomial ones -- the tower's Pascal C.     *)
  Theorem bcl_binomial : forall a b k j,
    nth j (bcl a b k) r0 = ofnat (C k j) *! rpow a j *! rpow b (k - j).
  Proof.
    induction k as [|k IH]; intros j.
    - destruct j as [|[|j]]; simpl; ring.
    - cbn [bcl]. rewrite nth_padd, nth_pscale. destruct j as [|j].
      + cbn [nth]. rewrite IH, Nat.sub_0_r, !C_zero. simpl. ring.
      + cbn [nth]. rewrite nth_pscale, !IH.
        change (C (S k) (S j)) with (C k j + C k (S j)).
        change (S k - S j) with (k - j).
        rewrite ofnat_add.
        destruct (le_lt_dec k j) as [Hle|Hlt].
        * rewrite (C_small k (S j)) by lia.
          replace (k - S j) with 0 by lia. replace (k - j) with 0 by lia.
          simpl. ring.
        * replace (k - j) with (S (k - S j)) by lia. simpl. ring.
  Qed.

  (* Coefficients of X^j become coefficients of the moment p_(k0+j).     *)
  Fixpoint mdot (l : list R) (k0 : nat) (xs : list R) : R :=
    match l with
    | [] => r0
    | c :: l' => c *! psum k0 xs +! mdot l' (S k0) xs
    end.

  Lemma rsum_peval_step : forall c l k0 xs,
    rsum (map (fun x => rpow x k0 *! (c +! x *! peval l x)) xs)
    = c *! rsum (map (fun x => rpow x k0) xs)
      +! rsum (map (fun x => rpow x (S k0) *! peval l x) xs).
  Proof.
    intros c l k0. induction xs as [|x xs IHx]; cbn [map rsum]; [ring|].
    rewrite IHx. change (rpow x (S k0)) with (x *! rpow x k0). ring.
  Qed.

  Lemma rsum_peval : forall l k0 xs,
    rsum (map (fun x => rpow x k0 *! peval l x) xs) = mdot l k0 xs.
  Proof.
    induction l as [|c l IH]; intros k0 xs.
    - simpl. induction xs as [|x xs IHx]; simpl; [reflexivity|].
      rewrite IHx. ring.
    - cbn [mdot]. rewrite <- IH. unfold psum. apply rsum_peval_step.
  Qed.

  Lemma mdot_seq : forall l k0 xs,
    mdot l k0 xs
    = rsum (map (fun j => nth j l r0 *! psum (k0 + j) xs)
                (seq 0 (length l))).
  Proof.
    induction l as [|c l IH]; intros k0 xs; [reflexivity|].
    cbn [mdot length]. rewrite IH.
    change (seq 0 (S (length l))) with (0 :: seq 1 (length l)).
    rewrite <- seq_shift. cbn [map rsum nth]. rewrite map_map.
    rewrite Nat.add_0_r. f_equal. f_equal. apply map_ext. intros j.
    cbn [nth]. rewrite Nat.add_succ_r. reflexivity.
  Qed.

  (* P4, the draft's (M1), at every k and every extent.                  *)
  Theorem affine_moment_tower_closed : forall a b k xs,
    psum k (map (aff a b) xs)
    = rsum (map (fun j => ofnat (C k j) *! rpow a j *! rpow b (k - j)
                          *! psum j xs)
                (seq 0 (S k))).
  Proof.
    intros a b k xs. unfold psum at 1. rewrite map_map.
    transitivity (rsum (map (fun x => rpow x 0 *! peval (bcl a b k) x) xs)).
    { f_equal. apply map_ext. intro x. rewrite peval_bcl. unfold aff.
      simpl. ring. }
    rewrite rsum_peval, mdot_seq, bcl_length.
    f_equal. apply map_ext. intro j. rewrite bcl_binomial. reflexivity.
  Qed.

  (* Units (the draft's 11.3).  Rescale the unit of x by c, with a       *)
  (* dimensionless and b carrying x's unit: p_k rescales by c^k.  The    *)
  (* summary is a GRADED product -- S carries the unit, Q its square --  *)
  (* and the reduced update respects the grading.                        *)
  Lemma rpow_mul : forall c x k, rpow (c *! x) k = rpow c k *! rpow x k.
  Proof.
    intros c x. induction k as [|k IH]; simpl; [ring|]. rewrite IH. ring.
  Qed.

  Lemma rpow_add : forall x n m, rpow x (n + m) = rpow x n *! rpow x m.
  Proof.
    intros x n m. induction n as [|n IH]; simpl; [ring|]. rewrite IH. ring.
  Qed.

  Lemma rpow_rpow : forall x n m, rpow (rpow x n) m = rpow x (n * m).
  Proof.
    intros x n m. induction m as [|m IH].
    - rewrite Nat.mul_0_r. reflexivity.
    - replace (n * S m) with (n + n * m) by ring. rewrite rpow_add, <- IH.
      reflexivity.
  Qed.

  Lemma psum_scale : forall c k xs,
    psum k (map (fun x => c *! x) xs) = rpow c k *! psum k xs.
  Proof.
    intros c k xs. unfold psum. rewrite map_map.
    induction xs as [|x xs IH]; cbn [map rsum]; [ring|].
    rewrite IH, rpow_mul. ring.
  Qed.

  Theorem affine_update_unit_covariant : forall c a b k xs,
    psum k (map (aff a (c *! b)) (map (fun x => c *! x) xs))
    = rpow c k *! psum k (map (aff a b) xs).
  Proof.
    intros c a b k xs. rewrite <- psum_scale. f_equal.
    rewrite !map_map. apply map_ext. intro x. unfold aff. ring.
  Qed.

  (* Squaring every particle doubles the index of every power sum, so    *)
  (* the sum observed after t squarings is the moment p_(2^t).           *)
  Lemma rpow_square : forall x k, rpow (x *! x) k = rpow x (2 * k).
  Proof.
    intros x. induction k as [|k IH]; [reflexivity|].
    replace (2 * S k) with (S (S (2 * k))) by lia. simpl rpow at 1.
    cbn [rpow]. rewrite IH. ring.
  Qed.

  Lemma psum_square : forall k xs,
    psum k (map (fun x => x *! x) xs) = psum (2 * k) xs.
  Proof.
    intros k xs. unfold psum. rewrite map_map. f_equal. apply map_ext.
    intro x. apply rpow_square.
  Qed.

  Theorem squaring_observes_dyadic_moments : forall t xs,
    psum 1 (run (fun (_ : unit) l => map (fun x => x *! x) l)
                (repeat tt t) xs)
    = psum (2 ^ t) xs.
  Proof.
    induction t as [|t IH]; intros xs; [reflexivity|].
    simpl repeat. rewrite run_cons, IH, psum_square. reflexivity.
  Qed.

  (* ------------------------------------------------------------------- *)
  (* The certificate.  The coefficients a, b are ARBITRARY functions of  *)
  (* the input and the current summary, shared by every particle; the    *)
  (* extent N is a known parameter of the reduced update.                *)
  (* ------------------------------------------------------------------- *)

  Section MomentReduction.
    Variable U : Type.
    Variables r N : nat.
    Variables alpha beta : U -> list R -> R.

    Definition qr (xs : list R) : list R :=
      map (fun k => psum k xs) (seq 1 r).

    Definition Fm (u : U) (xs : list R) : list R :=
      map (aff (alpha u (qr xs)) (beta u (qr xs))) xs.

    Definition mom (z : list R) (j : nat) : R :=
      match j with 0 => ofnat N | S j' => nth j' z r0 end.

    Definition Gm (u : U) (z : list R) : list R :=
      map (fun k =>
             rsum (map (fun j => ofnat (C k j) *! rpow (alpha u z) j
                                 *! rpow (beta u z) (k - j) *! mom z j)
                       (seq 0 (S k))))
          (seq 1 r).

    Lemma mom_qr : forall xs j, length xs = N -> j <= r ->
      mom (qr xs) j = psum j xs.
    Proof.
      intros xs j HN Hj. destruct j as [|j]; simpl.
      - rewrite psum_0, HN. reflexivity.
      - unfold qr. rewrite (nth_map_lt nat _ _ _ r0 0)
          by (rewrite seq_length; lia).
        rewrite seq_nth by lia. reflexivity.
    Qed.

    Theorem moment_step_closed : forall u xs, length xs = N ->
      qr (Fm u xs) = Gm u (qr xs).
    Proof.
      intros u xs HN. unfold Gm. unfold qr at 1.
      apply map_ext_in. intros k Hk. apply in_seq in Hk.
      unfold Fm. rewrite affine_moment_tower_closed.
      f_equal. apply map_ext_in. intros j Hj. apply in_seq in Hj.
      rewrite mom_qr by (try exact HN; lia). reflexivity.
    Qed.

    Theorem moment_certificate : forall (Y : Type) (hb : list R -> Y),
      certificate Fm qr (fun xs => hb (qr xs)) (fun xs => length xs = N)
                  Gm hb.
    Proof.
      intros Y hb. constructor.
      - intros u xs HN. unfold Fm. rewrite map_length. exact HN.
      - intros u xs HN. apply moment_step_closed. exact HN.
      - reflexivity.
    Qed.

    (* P1 for the fragment: every prefix observation of every finite     *)
    (* execution, at every extent, with r summary coordinates.           *)
    Theorem moment_reduction_sound : forall (Y : Type) (hb : list R -> Y)
        w xs,
      length xs = N ->
      trace Fm (fun xs => hb (qr xs)) w xs = trace Gm hb w (qr xs).
    Proof.
      intros Y hb w xs HN.
      apply (summary_trace_sound U (list R) (list R) Y Fm qr
               (fun xs => hb (qr xs)) (fun xs => length xs = N) Gm hb).
      - apply moment_certificate.
      - exact HN.
    Qed.

  End MomentReduction.

  (* ------------------------------------------------------------------- *)
  (* P2's concrete non-time example: a tree denoting a finite sequence,  *)
  (* summarized by (extent, sum, sum of squares).                        *)
  (* ------------------------------------------------------------------- *)

  Inductive sexp : Type :=
  | Single : R -> sexp
  | Cat : sexp -> sexp -> sexp
  | Aff : R -> R -> sexp -> sexp.

  Fixpoint den (e : sexp) : list R :=
    match e with
    | Single x => [x]
    | Cat e1 e2 => den e1 ++ den e2
    | Aff a b e' => map (aff a b) (den e')
    end.

  Fixpoint summ (e : sexp) : R * R * R :=
    match e with
    | Single x => (r1, x, x *! x)
    | Cat e1 e2 =>
        let '(n1, s1, t1) := summ e1 in
        let '(n2, s2, t2) := summ e2 in
        (n1 +! n2, s1 +! s2, t1 +! t2)
    | Aff a b e' =>
        let '(n, s, t) := summ e' in
        (n, a *! s +! n *! b,
         a *! a *! t +! (r1 +! r1) *! a *! b *! s +! n *! b *! b)
    end.

  Theorem tree_summary_sound : forall e,
    summ e = (ofnat (length (den e)), psum 1 (den e), psum 2 (den e)).
  Proof.
    induction e as [x|e1 IH1 e2 IH2|a b e IH]; simpl.
    - unfold psum. simpl. f_equal; [f_equal|]; ring.
    - rewrite IH1, IH2, app_length, ofnat_add, !psum_app. reflexivity.
    - rewrite IH, map_length, affine_sum_closed, affine_square_sum_closed.
      reflexivity.
  Qed.

  (* ------------------------------------------------------------------- *)
  (* P9: polynomial summaries.  Closure of the generated algebra under   *)
  (* F* is decided on the generators, and supplied expressions for G and *)
  (* hb are a finite certificate.                                        *)
  (* ------------------------------------------------------------------- *)

  Inductive pexp : Type :=
  | PVar : nat -> pexp
  | PConst : R -> pexp
  | PAdd : pexp -> pexp -> pexp
  | PMul : pexp -> pexp -> pexp.

  Fixpoint pev (e : pexp) (rho : nat -> R) : R :=
    match e with
    | PVar i => rho i
    | PConst c => c
    | PAdd e1 e2 => pev e1 rho +! pev e2 rho
    | PMul e1 e2 => pev e1 rho *! pev e2 rho
    end.

  Fixpoint psubst (sigma : nat -> pexp) (e : pexp) : pexp :=
    match e with
    | PVar i => sigma i
    | PConst c => PConst c
    | PAdd e1 e2 => PAdd (psubst sigma e1) (psubst sigma e2)
    | PMul e1 e2 => PMul (psubst sigma e1) (psubst sigma e2)
    end.

  Lemma pev_ext : forall e rho rho',
    (forall i, rho i = rho' i) -> pev e rho = pev e rho'.
  Proof.
    induction e as [i|c|e1 IH1 e2 IH2|e1 IH1 e2 IH2]; intros rho rho' H;
      simpl; auto.
    - rewrite (IH1 rho rho' H), (IH2 rho rho' H). reflexivity.
    - rewrite (IH1 rho rho' H), (IH2 rho rho' H). reflexivity.
  Qed.

  Lemma pev_subst : forall sigma e rho,
    pev (psubst sigma e) rho = pev e (fun i => pev (sigma i) rho).
  Proof.
    induction e as [i|c|e1 IH1 e2 IH2|e1 IH1 e2 IH2]; intros rho; simpl;
      auto.
    - rewrite IH1, IH2. reflexivity.
    - rewrite IH1, IH2. reflexivity.
  Qed.

  (* A polynomial update and a family of polynomial generators.          *)
  Definition pstep (Fs : nat -> pexp) (rho : nat -> R) : nat -> R :=
    fun i => pev (Fs i) rho.
  Definition qenv (qs : nat -> pexp) (rho : nat -> R) : nat -> R :=
    fun j => pev (qs j) rho.

  (* If F* sends every GENERATOR into B = R[q_1, q_2, ...], it sends all *)
  (* of B into B: e o q goes to (psubst gs e) o q.                       *)
  Theorem generators_closed_algebra_closed : forall Fs qs gs,
    (forall j rho, pev (qs j) (pstep Fs rho) = pev (gs j) (qenv qs rho)) ->
    forall e rho,
      pev e (qenv qs (pstep Fs rho)) = pev (psubst gs e) (qenv qs rho).
  Proof.
    intros Fs qs gs H e rho. rewrite pev_subst. apply pev_ext.
    intro j. unfold qenv at 1. apply H.
  Qed.

  (* m generators as a list-valued summary; supplied expressions gs and  *)
  (* hbe ARE a certificate once the identities are checked.              *)
  Definition lenv (z : list R) : nat -> R := fun i => nth i z r0.
  Definition qlist (m : nat) (qs : nat -> pexp) (rho : nat -> R) : list R :=
    map (fun j => pev (qs j) rho) (seq 0 m).
  Definition Glist (m : nat) (gs : nat -> pexp) (z : list R) : list R :=
    map (fun j => pev (gs j) (lenv z)) (seq 0 m).

  Theorem poly_certificate_sound : forall m Fs qs gs ho hbe,
    (forall j rho, j < m ->
       pev (qs j) (pstep Fs rho) = pev (gs j) (lenv (qlist m qs rho))) ->
    (forall rho, pev ho rho = pev hbe (lenv (qlist m qs rho))) ->
    certificate (fun (_ : unit) rho => pstep Fs rho) (qlist m qs)
                (fun rho => pev ho rho) (fun _ => True)
                (fun _ z => Glist m gs z) (fun z => pev hbe (lenv z)).
  Proof.
    intros m Fs qs gs ho hbe HG Hh. constructor.
    - auto.
    - intros _ rho _. unfold qlist at 1, Glist. apply map_ext_in.
      intros j Hj. apply in_seq in Hj. apply HG. lia.
    - intros rho _. apply Hh.
  Qed.

  (* ------------------------------------------------------------------- *)
  (* P10's dynamics: F(x, y) = (x y, y) observed at x gives x y^t, so    *)
  (* the least observable algebra is R[x, xy, xy^2, ...].  That it is    *)
  (* not finitely generated is NOT mechanized here.                      *)
  (* ------------------------------------------------------------------- *)

  Definition p10F (p : R * R) : R * R := (fst p *! snd p, snd p).

  Theorem p10_trajectory : forall t x y,
    Nat.iter t p10F (x, y) = (x *! rpow y t, y).
  Proof.
    induction t as [|t IH]; intros x y; simpl.
    - f_equal. ring.
    - rewrite IH. unfold p10F. simpl. f_equal. ring.
  Qed.

End Moments.

Arguments PVar {R} _.
Arguments PConst {R} _.
Arguments PAdd {R} _ _.
Arguments PMul {R} _ _.

(* ===================================================================== *)
(* Part B.  Derivatives without analysis: the dual numbers R[eps]/eps^2  *)
(* are a commutative ring, and everything in Part A holds over EVERY     *)
(* commutative ring.                                                     *)
(* ===================================================================== *)

Section Dual.
  Variable R : Type.
  Variables (r0 r1 : R) (radd rmul rsub : R -> R -> R) (ropp : R -> R).
  Hypothesis Rth : ring_theory r0 r1 radd rmul rsub ropp (@eq R).
  Add Ring dual_base_ring : Rth.

  Local Infix "+!" := radd (at level 50, left associativity).
  Local Infix "*!" := rmul (at level 40, left associativity).
  Local Infix "-!" := rsub (at level 50, left associativity).

  (* (value, tangent).                                                   *)
  Definition dual : Type := (R * R)%type.
  Definition d0 : dual := (r0, r0).
  Definition d1 : dual := (r1, r0).
  Definition dadd (p q : dual) : dual := (fst p +! fst q, snd p +! snd q).
  Definition dmul (p q : dual) : dual :=
    (fst p *! fst q, fst p *! snd q +! snd p *! fst q).
  Definition dsub (p q : dual) : dual := (fst p -! fst q, snd p -! snd q).
  Definition dopp (p : dual) : dual := (ropp (fst p), ropp (snd p)).

  Theorem dual_ring_theory :
    ring_theory d0 d1 dadd dmul dsub dopp (@eq dual).
  Proof.
    constructor; intros;
      repeat match goal with p : dual |- _ => destruct p end;
      unfold d0, d1, dadd, dmul, dsub, dopp; simpl; f_equal; ring.
  Qed.

  Local Notation Bpow := (rpow R r1 rmul).
  Local Notation Bnat := (ofnat R r0 r1 radd).
  Local Notation Bsum := (rsum R r0 radd).
  Local Notation Bpsum := (psum R r0 r1 radd rmul).
  Local Notation Dpow := (rpow dual d1 dmul).
  Local Notation Dpsum := (psum dual d0 d1 dadd dmul).

  (* The power rule, as an identity of dual numbers.                     *)
  Lemma rpow_dual : forall x v k,
    Dpow (x, v) (S k) = (Bpow x (S k), Bnat (S k) *! Bpow x k *! v).
  Proof.
    intros x v. induction k as [|k IH].
    - simpl. unfold dmul. simpl. f_equal; ring.
    - change (Dpow (x, v) (S (S k)))
        with (dmul (x, v) (Dpow (x, v) (S k))).
      rewrite IH. unfold dmul. simpl. f_equal; ring.
  Qed.

  (* Dq for the moment summary: the tangent of p_(k+1) at x along v is   *)
  (* (k+1) sum_i x_i^k v_i.                                              *)
  Theorem dual_psum : forall k xs,
    Dpsum (S k) xs
    = (Bpsum (S k) (map fst xs),
       Bnat (S k) *! Bsum (map (fun p => Bpow (fst p) k *! snd p) xs)).
  Proof.
    intros k. induction xs as [|[x v] xs IH].
    - unfold psum. simpl. unfold d0. f_equal; ring.
    - rewrite psum_cons, IH, rpow_dual. unfold dadd.
      cbn [fst snd map]. rewrite psum_cons. cbn [rsum]. f_equal. ring.
  Qed.

  (* The cotangent pullback the draft states for q = (S, Q):             *)
  (* d loss / d x_i = lam_S + 2 x_i lam_Q, paired against any tangent.   *)
  Theorem pullback_SQ : forall lamS lamQ xs,
    lamS *! snd (Dpsum 1 xs) +! lamQ *! snd (Dpsum 2 xs)
    = Bsum (map (fun p => (lamS +! (r1 +! r1) *! fst p *! lamQ) *! snd p)
                xs).
  Proof.
    intros lamS lamQ xs. rewrite !dual_psum. cbn [snd].
    induction xs as [|[x v] xs IH]; simpl in *; [ring|].
    rewrite <- IH. ring.
  Qed.

  (* P5a for the fragment, every finite execution: pushing (x, v)        *)
  (* through the particle system and summarizing equals summarizing and  *)
  (* pushing (z, dz) through the reduced system.  This is Part A's       *)
  (* theorem AT the dual ring -- no chain rule was needed.  alphaD and   *)
  (* betaD are the coefficient functions as forward mode evaluates them; *)
  (* parameters enter through their tangent parts and through xs.        *)
  Theorem moment_tangent_sound : forall (U Y : Type) (r N : nat)
      (alphaD betaD : U -> list dual -> dual) (hb : list dual -> Y) w xs,
    length xs = N ->
    trace (Fm dual d0 d1 dadd dmul U r alphaD betaD)
          (fun l => hb (qr dual d0 d1 dadd dmul r l)) w xs
    = trace (Gm dual d0 d1 dadd dmul U r N alphaD betaD) hb w
            (qr dual d0 d1 dadd dmul r xs).
  Proof.
    intros U Y r N alphaD betaD hb w xs HN.
    apply (moment_reduction_sound dual d0 d1 dadd dmul dsub dopp
             dual_ring_theory U r N alphaD betaD Y hb w xs HN).
  Qed.

  (* ------------------------------------------------------------------- *)
  (* A rank bound against POLYNOMIAL encodings.  Evaluate a polynomial   *)
  (* expression in the dual numbers: the tangent part is the formal      *)
  (* directional derivative, and the chain rule is just evaluation.      *)
  (* ------------------------------------------------------------------- *)

  Local Notation Bpev := (pev R radd rmul).

  Fixpoint pevD (e : pexp R) (rho : nat -> dual) : dual :=
    match e with
    | PVar i => rho i
    | PConst c => (c, r0)
    | PAdd e1 e2 => dadd (pevD e1 rho) (pevD e2 rho)
    | PMul e1 e2 => dmul (pevD e1 rho) (pevD e2 rho)
    end.

  Lemma pevD_ext : forall e rho rho',
    (forall i, rho i = rho' i) -> pevD e rho = pevD e rho'.
  Proof.
    induction e as [i|c|e1 IH1 e2 IH2|e1 IH1 e2 IH2]; intros rho rho' H;
      simpl; auto.
    - rewrite (IH1 rho rho' H), (IH2 rho rho' H). reflexivity.
    - rewrite (IH1 rho rho' H), (IH2 rho rho' H). reflexivity.
  Qed.

  Lemma pevD_fst : forall e rho,
    fst (pevD e rho) = Bpev e (fun i => fst (rho i)).
  Proof.
    induction e as [i|c|e1 IH1 e2 IH2|e1 IH1 e2 IH2]; intros rho; simpl;
      auto.
    - rewrite IH1, IH2. reflexivity.
    - rewrite IH1, IH2. reflexivity.
  Qed.

  Definition lift (x v : nat -> R) : nat -> dual := fun i => (x i, v i).

  (* The formal derivative of e at x along v.                            *)
  Definition D (e : pexp R) (x v : nat -> R) : R := snd (pevD e (lift x v)).

  Lemma pevD_fst_lift : forall e x v, fst (pevD e (lift x v)) = Bpev e x.
  Proof.
    intros e x v. rewrite pevD_fst. apply pev_ext. intro i. reflexivity.
  Qed.

  Definition env2 {T : Type} (z1 z2 : T) (i : nat) : T :=
    match i with 0 => z1 | _ => z2 end.

  Lemma pevD_fst_env2 : forall e z1 t1 z2 t2,
    fst (pevD e (env2 (z1, t1) (z2, t2))) = Bpev e (env2 z1 z2).
  Proof.
    intros. rewrite pevD_fst. apply pev_ext. intros [|i]; reflexivity.
  Qed.

  (* The tangent of a two-variable expression is linear in the two       *)
  (* incoming tangents, with coefficients that depend on the values only.*)
  Lemma pevD_lin2 : forall e z1 z2 t1 t2,
    snd (pevD e (env2 (z1, t1) (z2, t2)))
    = t1 *! snd (pevD e (env2 (z1, r1) (z2, r0)))
      +! t2 *! snd (pevD e (env2 (z1, r0) (z2, r1))).
  Proof.
    induction e as [i|c|e1 IH1 e2 IH2|e1 IH1 e2 IH2]; intros z1 z2 t1 t2.
    - destruct i as [|i]; simpl; ring.
    - simpl. ring.
    - simpl. rewrite (IH1 z1 z2 t1 t2), (IH2 z1 z2 t1 t2). ring.
    - simpl. rewrite (IH1 z1 z2 t1 t2), (IH2 z1 z2 t1 t2).
      rewrite !pevD_fst_env2. ring.
  Qed.

  (* If O = Rr(q1, q2) as an identity over the dual numbers, DO is a     *)
  (* combination of Dq1 and Dq2 with direction-independent coefficients. *)
  Lemma D_factor2 : forall O Rr q1 q2,
    (forall rho, pevD O rho = pevD Rr (env2 (pevD q1 rho) (pevD q2 rho))) ->
    forall x v,
      D O x v
      = D q1 x v *! snd (pevD Rr (env2 (Bpev q1 x, r1) (Bpev q2 x, r0)))
        +! D q2 x v *! snd (pevD Rr (env2 (Bpev q1 x, r0) (Bpev q2 x, r1))).
  Proof.
    intros O Rr q1 q2 H x v. unfold D at 1. rewrite H.
    rewrite (surjective_pairing (pevD q1 (lift x v))).
    rewrite (surjective_pairing (pevD q2 (lift x v))).
    rewrite pevD_lin2. rewrite !pevD_fst_lift. reflexivity.
  Qed.

  Definition det3 (a1 a2 a3 b1 b2 b3 c1 c2 c3 : R) : R :=
    a1 *! (b2 *! c3 -! b3 *! c2) -! a2 *! (b1 *! c3 -! b3 *! c1)
    +! a3 *! (b1 *! c2 -! b2 *! c1).

  (* m = 2, s = 3: three observations carried by two polynomial summary  *)
  (* coordinates have a vanishing 3 x 3 formal Jacobian minor, at every  *)
  (* point, along every three directions.                                *)
  Theorem poly_rank_bound_2_3 : forall q1 q2 O1 O2 O3 R1 R2 R3,
    (forall rho, pevD O1 rho = pevD R1 (env2 (pevD q1 rho) (pevD q2 rho))) ->
    (forall rho, pevD O2 rho = pevD R2 (env2 (pevD q1 rho) (pevD q2 rho))) ->
    (forall rho, pevD O3 rho = pevD R3 (env2 (pevD q1 rho) (pevD q2 rho))) ->
    forall x va vb vc,
      det3 (D O1 x va) (D O1 x vb) (D O1 x vc)
           (D O2 x va) (D O2 x vb) (D O2 x vc)
           (D O3 x va) (D O3 x vb) (D O3 x vc) = r0.
  Proof.
    intros q1 q2 O1 O2 O3 R1 R2 R3 H1 H2 H3 x va vb vc.
    rewrite !(D_factor2 O1 R1 q1 q2 H1), !(D_factor2 O2 R2 q1 q2 H2),
            !(D_factor2 O3 R3 q1 q2 H3).
    unfold det3. ring.
  Qed.

  (* m = 1, s = 2.                                                       *)
  Theorem poly_rank_bound_1_2 : forall q O1 O2 R1 R2,
    (forall rho, pevD O1 rho = pevD R1 (fun _ => pevD q rho)) ->
    (forall rho, pevD O2 rho = pevD R2 (fun _ => pevD q rho)) ->
    forall x va vb,
      D O1 x va *! D O2 x vb -! D O1 x vb *! D O2 x va = r0.
  Proof.
    intros q O1 O2 R1 R2 H1 H2 x va vb.
    assert (E : forall Rr p, pevD Rr (fun _ => p) = pevD Rr (env2 p p)).
    { intros Rr p. apply pevD_ext. intros [|i]; reflexivity. }
    assert (H1' : forall rho,
              pevD O1 rho = pevD R1 (env2 (pevD q rho) (pevD q rho))).
    { intro rho. rewrite H1. apply E. }
    assert (H2' : forall rho,
              pevD O2 rho = pevD R2 (env2 (pevD q rho) (pevD q rho))).
    { intro rho. rewrite H2. apply E. }
    rewrite !(D_factor2 O1 R1 q q H1'), !(D_factor2 O2 R2 q q H2'). ring.
  Qed.

  (* ------------------------------------------------------------------- *)
  (* P10.  Evaluate at x = eps, y = n: every observable g_k = x y^k      *)
  (* becomes (0, n^k), all values vanish, and the tangent of ANY         *)
  (* polynomial expression in the g_k is a LINEAR form in the n^k with   *)
  (* coefficients that do not depend on n.                               *)
  (* ------------------------------------------------------------------- *)

  Lemma Dpow_const : forall n k, Dpow (n, r0) k = (Bpow n k, r0).
  Proof.
    intros n. induction k as [|k IH]; [reflexivity|].
    change (Dpow (n, r0) (S k)) with (dmul (n, r0) (Dpow (n, r0) k)).
    rewrite IH. unfold dmul. simpl. f_equal; ring.
  Qed.

  Definition zlift (t : nat -> R) : nat -> dual := fun k => (r0, t k).

  Lemma zlift_fst : forall e t,
    fst (pevD e (zlift t)) = Bpev e (fun _ => r0).
  Proof.
    intros. rewrite pevD_fst. apply pev_ext. intro i. reflexivity.
  Qed.

  Lemma zlift_linear : forall e a s b t,
    snd (pevD e (zlift (fun k => a *! s k +! b *! t k)))
    = a *! snd (pevD e (zlift s)) +! b *! snd (pevD e (zlift t)).
  Proof.
    induction e as [i|c|e1 IH1 e2 IH2|e1 IH1 e2 IH2]; intros a s b t;
      simpl.
    - reflexivity.
    - ring.
    - rewrite (IH1 a s b t), (IH2 a s b t). ring.
    - rewrite (IH1 a s b t), (IH2 a s b t), !zlift_fst. ring.
  Qed.

  Lemma zlift_zero : forall e, snd (pevD e (zlift (fun _ => r0))) = r0.
  Proof.
    induction e as [i|c|e1 IH1 e2 IH2|e1 IH1 e2 IH2]; simpl;
      try reflexivity.
    - rewrite IH1, IH2. ring.
    - rewrite IH1, IH2. ring.
  Qed.

  Definition unitR (j : nat) : nat -> R :=
    fun i => if Nat.eqb i j then r1 else r0.
  Definition truncm (m : nat) (t : nat -> R) : nat -> R :=
    fun k => if Nat.ltb k m then t k else r0.

  Lemma zlift_basis : forall e t m,
    snd (pevD e (zlift (truncm m t)))
    = Bsum (map (fun k => t k *! snd (pevD e (zlift (unitR k))))
                (seq 0 m)).
  Proof.
    intros e t. induction m as [|m IH].
    - transitivity (snd (pevD e (zlift (fun _ => r0)))).
      + reflexivity.
      + apply zlift_zero.
    - rewrite seq_S, map_app,
        (rsum_app R r0 r1 radd rmul rsub ropp Rth), <- IH.
      rewrite (pevD_ext e (zlift (truncm (S m) t))
                 (zlift (fun k => r1 *! truncm m t k +! t m *! unitR m k))).
      + rewrite (zlift_linear e r1 (truncm m t) (t m) (unitR m)).
        cbn [map rsum Nat.add]. ring.
      + intro i. unfold zlift. f_equal. unfold truncm, unitR.
        destruct (Nat.ltb_spec i m), (Nat.ltb_spec i (S m)),
                 (Nat.eqb_spec i m); try lia; subst; ring.
  Qed.

  (* The observables g_0 .. g_T at (x, y); variables beyond T read 0, so *)
  (* { pevD E (genv T x y) } is exactly the subalgebra R[g_0, ..., g_T]. *)
  Definition genv (T : nat) (x y : dual) : nat -> dual :=
    fun k => if Nat.ltb k (S T) then dmul x (Dpow y k) else d0.

  Theorem p10_degree_one_part : forall (E : pexp R) T n,
    snd (pevD E (genv T (r0, r1) (n, r0)))
    = Bsum (map (fun k => Bpow n k *! snd (pevD E (zlift (unitR k))))
                (seq 0 (S T))).
  Proof.
    intros E T n. rewrite <- (zlift_basis E (fun k => Bpow n k) (S T)).
    f_equal. apply pevD_ext. intro k. unfold genv, zlift, truncm.
    destruct (Nat.ltb k (S T)); [|reflexivity].
    rewrite Dpow_const. unfold dmul. simpl. f_equal; ring.
  Qed.

  (* ------------------------------------------------------------------- *)
  (* The tangent of an expression is LINEAR in the incoming tangents at  *)
  (* any base point, so it decomposes over the unit directions.  This is *)
  (* what BladeRankBound's general bound rests on.                       *)
  (* ------------------------------------------------------------------- *)

  Lemma lift_linear : forall e x a s b t,
    snd (pevD e (lift x (fun k => a *! s k +! b *! t k)))
    = a *! snd (pevD e (lift x s)) +! b *! snd (pevD e (lift x t)).
  Proof.
    induction e as [i|c|e1 IH1 e2 IH2|e1 IH1 e2 IH2]; intros x a s b t;
      simpl.
    - reflexivity.
    - ring.
    - rewrite (IH1 x a s b t), (IH2 x a s b t). ring.
    - rewrite (IH1 x a s b t), (IH2 x a s b t), !pevD_fst_lift. ring.
  Qed.

  Lemma lift_zero : forall e x, snd (pevD e (lift x (fun _ => r0))) = r0.
  Proof.
    induction e as [i|c|e1 IH1 e2 IH2|e1 IH1 e2 IH2]; intros x; simpl;
      try reflexivity.
    - rewrite IH1, IH2. ring.
    - rewrite IH1, IH2. ring.
  Qed.

  Lemma lift_basis : forall e x t m,
    snd (pevD e (lift x (truncm m t)))
    = Bsum (map (fun k => t k *! snd (pevD e (lift x (unitR k))))
                (seq 0 m)).
  Proof.
    intros e x t. induction m as [|m IH].
    - transitivity (snd (pevD e (lift x (fun _ => r0)))).
      + reflexivity.
      + apply lift_zero.
    - rewrite seq_S, map_app,
        (rsum_app R r0 r1 radd rmul rsub ropp Rth), <- IH.
      rewrite (pevD_ext e (lift x (truncm (S m) t))
                 (lift x (fun k => r1 *! truncm m t k +! t m *! unitR m k))).
      + rewrite (lift_linear e x r1 (truncm m t) (t m) (unitR m)).
        cbn [map rsum Nat.add]. ring.
      + intro i. unfold lift. f_equal. unfold truncm, unitR.
        destruct (Nat.ltb_spec i m), (Nat.ltb_spec i (S m)),
                 (Nat.eqb_spec i m); try lia; subst; ring.
  Qed.

  (* m summary coordinates q_0 .. q_(m-1); a reconstruction may name     *)
  (* other variables, which read zero -- it sees m coordinates only.     *)
  Definition qenvD (m : nat) (q : nat -> pexp R) (rho : nat -> dual)
    : nat -> dual :=
    fun j => if Nat.ltb j m then pevD (q j) rho else d0.
  Definition qval (m : nat) (q : nat -> pexp R) (x : nat -> R) : nat -> R :=
    fun j => if Nat.ltb j m then Bpev (q j) x else r0.

  (* If O = Rr(q_0 .. q_(m-1)) over the dual numbers, then DO is a       *)
  (* combination of the Dq_j with direction-independent coefficients.    *)
  Theorem D_factor : forall m q O Rr,
    (forall rho, pevD O rho = pevD Rr (qenvD m q rho)) ->
    forall x v,
      D O x v
      = Bsum (map (fun j => D (q j) x v
                            *! snd (pevD Rr (lift (qval m q x) (unitR j))))
                  (seq 0 m)).
  Proof.
    intros m q O Rr H x v. unfold D at 1. rewrite H.
    rewrite (pevD_ext Rr (qenvD m q (lift x v))
               (lift (qval m q x) (truncm m (fun j => D (q j) x v)))).
    - apply lift_basis.
    - intro j.
      change (lift (qval m q x) (truncm m (fun j0 => D (q j0) x v)) j)
        with (qval m q x j, truncm m (fun j0 => D (q j0) x v) j).
      unfold qenvD, qval, truncm. destruct (Nat.ltb j m); [|reflexivity].
      rewrite <- (pevD_fst_lift (q j) x v). unfold D.
      apply surjective_pairing.
  Qed.

End Dual.

(* ===================================================================== *)
(* Part C.  Closed instances over Z.                                     *)
(* ===================================================================== *)

Open Scope Z_scope.

Definition zpsum : nat -> list Z -> Z := psum Z 0 1 Z.add Z.mul.
Definition zq2 (xs : list Z) : Z * Z := (zpsum 1 xs, zpsum 2 xs).
Definition sqmap (_ : unit) (xs : list Z) : list Z :=
  map (fun x => x * x) xs.

Lemma zpsum_app : forall k xs ys,
  zpsum k (xs ++ ys) = zpsum k xs + zpsum k ys.
Proof.
  intros. apply (psum_app Z 0 1 Z.add Z.mul Z.sub Z.opp InitialRing.Zth).
Qed.

(* The draft's counterexample, pinned: same (S, Q), different next Q.    *)
Example same_SQ_674_1250 :
  zq2 [3; -3; 4; -4] = (0, 50) /\ zq2 [5; -5; 0; 0] = (0, 50) /\
  zq2 (sqmap tt [3; -3; 4; -4]) = (50, 674) /\
  zq2 (sqmap tt [5; -5; 0; 0]) = (50, 1250).
Proof. vm_compute. repeat split. Qed.

(* A smaller one that lives INSIDE the positive chamber, with pairwise   *)
(* distinct coordinates, already at extent 3.                            *)
Example same_SQ_positive_N3 :
  zq2 [1; 5; 6] = (12, 62) /\ zq2 [2; 3; 7] = (12, 62) /\
  zq2 (sqmap tt [1; 5; 6]) = (62, 1922) /\
  zq2 (sqmap tt [2; 3; 7]) = (62, 2498).
Proof. vm_compute. repeat split. Qed.

Definition pos_adm (N : nat) (xs : list Z) : Prop :=
  length xs = N /\ Forall (fun x => 0 < x) xs.

Lemma collision_tail_positive : forall m a b c,
  0 < a -> 0 < b -> 0 < c ->
  Forall (fun x => 0 < x) ([a; b; c] ++ map Z.of_nat (seq 8 m)).
Proof.
  intros m a b c Ha Hb Hc. apply Forall_forall. intros x Hin.
  apply in_app_or in Hin. destruct Hin as [Hin|Hin].
  - simpl in Hin. destruct Hin as [E|[E|[E|[]]]]; subst; assumption.
  - apply in_map_iff in Hin. destruct Hin as [y [E Hy]]. subst.
    apply in_seq in Hy. lia.
Qed.

(* At every extent N >= 3 the squaring update admits NO update of the    *)
(* summary (S, Q) -- no function G of any kind -- even with the states   *)
(* restricted to strictly positive arrays.                               *)
Theorem square_not_closed_SQ : forall N, (3 <= N)%nat ->
  forall G : unit -> Z * Z -> Z * Z,
    ~ (forall u xs, pos_adm N xs -> zq2 (sqmap u xs) = G u (zq2 xs)).
Proof.
  intros N HN.
  apply (collision_refutes_update unit (list Z) (Z * Z) sqmap zq2
           (pos_adm N) tt
           ([1; 5; 6] ++ map Z.of_nat (seq 8 (N - 3)))
           ([2; 3; 7] ++ map Z.of_nat (seq 8 (N - 3)))).
  - split; [|apply collision_tail_positive; lia].
    rewrite app_length, map_length, seq_length. simpl. lia.
  - split; [|apply collision_tail_positive; lia].
    rewrite app_length, map_length, seq_length. simpl. lia.
  - unfold zq2. rewrite !zpsum_app. reflexivity.
  - unfold zq2, sqmap. rewrite !map_app, !zpsum_app. intro H.
    apply (f_equal snd) in H. cbn [snd] in H.
    apply Z.add_cancel_r in H. vm_compute in H. discriminate.
Qed.

(* Below the threshold the first two power sums determine the array up   *)
(* to order (Newton), and the tower closes.                              *)
Lemma two_point_newton : forall x1 x2 y1 y2 : Z,
  x1 + x2 = y1 + y2 -> x1 * x1 + x2 * x2 = y1 * y1 + y2 * y2 ->
  x1 * x1 * (x1 * x1) + x2 * x2 * (x2 * x2)
  = y1 * y1 * (y1 * y1) + y2 * y2 * (y2 * y2).
Proof.
  intros x1 x2 y1 y2 H1 H2.
  assert (E : x1 * x2 = y1 * y2).
  { assert (E2 : 2 * (x1 * x2) = 2 * (y1 * y2)).
    { replace (2 * (x1 * x2))
        with ((x1 + x2) * (x1 + x2) - (x1 * x1 + x2 * x2)) by ring.
      rewrite H1, H2. ring. }
    lia. }
  replace (x1 * x1 * (x1 * x1) + x2 * x2 * (x2 * x2))
    with ((x1 * x1 + x2 * x2) * (x1 * x1 + x2 * x2)
          - 2 * (x1 * x2) * (x1 * x2)) by ring.
  rewrite H2, E. ring.
Qed.

Theorem square_closed_SQ_small : forall xs ys,
  (length xs <= 2)%nat -> length ys = length xs ->
  zq2 xs = zq2 ys -> zq2 (sqmap tt xs) = zq2 (sqmap tt ys).
Proof.
  intros xs ys Hl Hy H.
  destruct xs as [|x1 [|x2 [|x3 xs]]]; simpl in Hl; try lia;
    destruct ys as [|y1 [|y2 [|y3 ys]]]; simpl in Hy; try discriminate.
  - reflexivity.
  - apply (f_equal fst) in H. unfold zq2, zpsum, psum in H. simpl in H.
    assert (x1 = y1) by lia. subst. reflexivity.
  - unfold zq2, zpsum, psum in *. simpl in *.
    pose proof (f_equal fst H) as H1. pose proof (f_equal snd H) as H2.
    simpl in H1, H2.
    assert (E1 : x1 + x2 = y1 + y2) by lia.
    assert (E2 : x1 * x1 + x2 * x2 = y1 * y1 + y2 * y2) by lia.
    pose proof (two_point_newton x1 x2 y1 y2 E1 E2) as E4.
    f_equal; lia.
Qed.

(* The exact threshold for squaring against (S, Q): the functional       *)
(* relation a G would have to realize holds iff N <= 2.  (The draft's    *)
(* P8 gives N >= d r = 4 as SUFFICIENT for refusal; it is not sharp.)    *)
Theorem square_SQ_threshold : forall N,
  (forall xs ys, length xs = N -> length ys = N ->
     zq2 xs = zq2 ys -> zq2 (sqmap tt xs) = zq2 (sqmap tt ys))
  <-> (N <= 2)%nat.
Proof.
  intros N. split.
  - intros H. destruct (le_lt_dec N 2) as [Hle|Hgt]; [exact Hle|].
    exfalso.
    assert (L : forall a b c : Z,
              length ([a; b; c] ++ map Z.of_nat (seq 8 (N - 3))) = N).
    { intros. rewrite app_length, map_length, seq_length. simpl. lia. }
    specialize (H _ _ (L 1 5 6) (L 2 3 7)).
    unfold zq2, sqmap in H. rewrite !map_app, !zpsum_app in H.
    specialize (H eq_refl). apply (f_equal snd) in H. cbn [snd] in H.
    apply Z.add_cancel_r in H. vm_compute in H. discriminate.
  - intros HN xs ys Hx Hy. apply square_closed_SQ_small; lia.
Qed.

(* --------------------------------------------------------------------- *)
(* Rank-bound instances.                                                 *)
(* --------------------------------------------------------------------- *)

Definition zpevD : pexp Z -> (nat -> Z * Z) -> Z * Z := pevD Z 0 Z.add Z.mul.

Fixpoint ppow (e : pexp Z) (k : nat) : pexp Z :=
  match k with O => PConst 1 | S k' => PMul e (ppow e k') end.

(* p_k on three particles, and on two.                                   *)
Definition pP3 (k : nat) : pexp Z :=
  PAdd (ppow (PVar 0) k) (PAdd (ppow (PVar 1) k) (ppow (PVar 2) k)).
Definition pP2 (k : nat) : pexp Z :=
  PAdd (ppow (PVar 0) k) (ppow (PVar 1) k).

Definition unitv (j : nat) : nat -> Z := fun i => if Nat.eqb i j then 1 else 0.
Definition pt123 : nat -> Z :=
  fun i => match i with O => 1 | S O => 2 | _ => 3 end.

(* The 3 x 3 minor of the draft's P7 at N = H = 3, x = (1, 2, 3).        *)
Example squaring_minor_N3 :
  det3 Z Z.add Z.mul Z.sub
       (D Z 0 Z.add Z.mul (pP3 1) pt123 (unitv 0))
       (D Z 0 Z.add Z.mul (pP3 1) pt123 (unitv 1))
       (D Z 0 Z.add Z.mul (pP3 1) pt123 (unitv 2))
       (D Z 0 Z.add Z.mul (pP3 2) pt123 (unitv 0))
       (D Z 0 Z.add Z.mul (pP3 2) pt123 (unitv 1))
       (D Z 0 Z.add Z.mul (pP3 2) pt123 (unitv 2))
       (D Z 0 Z.add Z.mul (pP3 4) pt123 (unitv 0))
       (D Z 0 Z.add Z.mul (pP3 4) pt123 (unitv 1))
       (D Z 0 Z.add Z.mul (pP3 4) pt123 (unitv 2)) = 96.
Proof. vm_compute. reflexivity. Qed.

(* P7 at N = H = 3, against polynomial encodings: the sums observed at   *)
(* steps 0, 1, 2 of the squaring recurrence are p_1, p_2, p_4            *)
(* (squaring_observes_dyadic_moments), and NO two polynomial summary     *)
(* coordinates with polynomial reconstructions carry all three.          *)
Theorem squaring_three_steps_need_three_coordinates :
  ~ exists q1 q2 R1 R2 R3 : pexp Z,
      (forall rho, zpevD (pP3 1) rho
                   = zpevD R1 (env2 (zpevD q1 rho) (zpevD q2 rho))) /\
      (forall rho, zpevD (pP3 2) rho
                   = zpevD R2 (env2 (zpevD q1 rho) (zpevD q2 rho))) /\
      (forall rho, zpevD (pP3 4) rho
                   = zpevD R3 (env2 (zpevD q1 rho) (zpevD q2 rho))).
Proof.
  intros (q1 & q2 & R1 & R2 & R3 & H1 & H2 & H3).
  pose proof (poly_rank_bound_2_3 Z 0 1 Z.add Z.mul Z.sub Z.opp
                InitialRing.Zth q1 q2 (pP3 1) (pP3 2) (pP3 4) R1 R2 R3
                H1 H2 H3 pt123 (unitv 0) (unitv 1) (unitv 2)) as Hdet.
  rewrite squaring_minor_N3 in Hdet. discriminate.
Qed.

(* P4a at r = 2, against polynomial encodings: S and Q cannot both be    *)
(* carried by ONE polynomial summary coordinate (N = 2 suffices).        *)
Theorem SQ_needs_two_coordinates :
  ~ exists q R1 R2 : pexp Z,
      (forall rho, zpevD (pP2 1) rho = zpevD R1 (fun _ => zpevD q rho)) /\
      (forall rho, zpevD (pP2 2) rho = zpevD R2 (fun _ => zpevD q rho)).
Proof.
  intros (q & R1 & R2 & H1 & H2).
  pose proof (poly_rank_bound_1_2 Z 0 1 Z.add Z.mul Z.sub Z.opp
                InitialRing.Zth q (pP2 1) (pP2 2) R1 R2 H1 H2
                (unitv 1) (unitv 0) (unitv 1)) as Hdet.
  vm_compute in Hdet. discriminate.
Qed.

(* P10: the observations x and x y need two polynomial coordinates ...   *)
Theorem p10_needs_two_coordinates :
  ~ exists q R1 R2 : pexp Z,
      (forall rho, zpevD (PVar 0) rho = zpevD R1 (fun _ => zpevD q rho)) /\
      (forall rho, zpevD (PMul (PVar 0) (PVar 1)) rho
                   = zpevD R2 (fun _ => zpevD q rho)).
Proof.
  intros (q & R1 & R2 & H1 & H2).
  pose proof (poly_rank_bound_1_2 Z 0 1 Z.add Z.mul Z.sub Z.opp
                InitialRing.Zth q (PVar 0) (PMul (PVar 0) (PVar 1))
                R1 R2 H1 H2 (unitv 0) (unitv 0) (unitv 1)) as Hdet.
  vm_compute in Hdet. discriminate.
Qed.

(* ... and EVERY certified summary of it, polynomial or not, is          *)
(* injective where x and y are nonzero: the quotient by future           *)
(* observations is the identity there.                                   *)
Theorem p10_no_identification : forall (W : Type) (q : Z * Z -> W) G hb,
  certificate (fun (_ : unit) p => p10F Z Z.mul p) q fst
              (fun p => fst p <> 0 /\ snd p <> 0) G hb ->
  forall p p', (fst p <> 0 /\ snd p <> 0) -> (fst p' <> 0 /\ snd p' <> 0) ->
    q p = q p' -> p = p'.
Proof.
  intros W q G hb c [x y] [x' y'] Hp Hp' Hq.
  pose proof (summary_refines_future unit (Z * Z) W Z _ q fst _ G hb c
                (x, y) (x', y') Hp Hp' Hq) as Hf.
  pose proof (Hf []) as E0. pose proof (Hf [tt]) as E1.
  simpl in E0, E1. subst x'. f_equal.
  apply (Z.mul_reg_l y y' x); [apply Hp|exact E1].
Qed.

(* --------------------------------------------------------------------- *)
(* P10: the ascending chain of observable subalgebras never stabilizes.  *)
(* x y^(T+1) is NOT a polynomial in x, x y, ..., x y^T -- so no finite   *)
(* set of observables (each lies in some R[g_0 .. g_T]) generates the    *)
(* least observable algebra, although q = (x, y) is an exact two-        *)
(* coordinate state.  "Polynomial identity" is read where a symbolic     *)
(* certificate lives: valid in every commutative ring, in particular in  *)
(* the dual numbers over Z.                                              *)
(* --------------------------------------------------------------------- *)

Definition zpow : Z -> nat -> Z := rpow Z 1 Z.mul.
Definition zrsum : list Z -> Z := rsum Z 0 Z.add.

Lemma zpow_succ : forall n k, zpow n (S k) = n * zpow n k.
Proof. reflexivity. Qed.

Lemma zpow_pos : forall n k, 1 <= n -> 0 < zpow n k.
Proof.
  intros n k Hn. induction k as [|k IH]; [reflexivity|].
  rewrite zpow_succ. nia.
Qed.

Lemma zpow_mono : forall n k j, 1 <= n -> zpow n k <= zpow n (k + j).
Proof.
  intros n k j Hn. induction j as [|j IH].
  - rewrite Nat.add_0_r. lia.
  - rewrite Nat.add_succ_r, zpow_succ.
    pose proof (zpow_pos n (k + j) Hn). nia.
Qed.

Lemma zrsum_snoc : forall (f : nat -> Z) m,
  zrsum (map f (seq 0 (S m))) = zrsum (map f (seq 0 m)) + f m.
Proof.
  intros f m. unfold zrsum. rewrite seq_S, map_app.
  rewrite (rsum_app Z 0 1 Z.add Z.mul Z.sub Z.opp InitialRing.Zth).
  simpl. lia.
Qed.

Lemma abs_sum_nonneg : forall (c : nat -> Z) m,
  0 <= zrsum (map (fun k => Z.abs (c k)) (seq 0 m)).
Proof.
  intros c. induction m as [|m IH]; [simpl; lia|].
  rewrite zrsum_snoc. lia.
Qed.

Lemma lower_terms_bound : forall (c : nat -> Z) n T m,
  1 <= n -> (m <= S T)%nat ->
  zrsum (map (fun k => zpow n k * c k) (seq 0 m))
  <= zrsum (map (fun k => Z.abs (c k)) (seq 0 m)) * zpow n T.
Proof.
  intros c n T m Hn. induction m as [|m IH]; intros Hm.
  - simpl. lia.
  - rewrite !zrsum_snoc. specialize (IH ltac:(lia)).
    pose proof (zpow_pos n m Hn) as Hp.
    pose proof (zpow_mono n m (T - m) Hn) as Hmono.
    replace (m + (T - m))%nat with T in Hmono by lia.
    assert (Hterm : zpow n m * c m <= Z.abs (c m) * zpow n T).
    { pose proof (Z.abs_nonneg (c m)).
      assert (c m <= Z.abs (c m)) by lia.
      assert (zpow n m * c m <= zpow n m * Z.abs (c m)) by nia. nia. }
    lia.
Qed.

Theorem p10_next_observable_is_new : forall (T : nat) (E : pexp Z),
  ~ (forall x y : Z * Z,
       zpevD E (genv Z 0 1 Z.add Z.mul T x y)
       = dmul Z Z.add Z.mul x
              (rpow (Z * Z) (d1 Z 0 1) (dmul Z Z.add Z.mul) y (S T))).
Proof.
  intros T E H.
  set (c := fun k => snd (zpevD E (zlift Z 0 (unitR Z 0 1 k)))).
  set (B := zrsum (map (fun k => Z.abs (c k)) (seq 0 (S T)))).
  assert (HB : 0 <= B) by apply abs_sum_nonneg.
  specialize (H (0, 1) (B + 1, 0)). apply (f_equal snd) in H.
  unfold zpevD in H.
  rewrite (p10_degree_one_part Z 0 1 Z.add Z.mul Z.sub Z.opp
             InitialRing.Zth E T (B + 1)) in H.
  rewrite (Dpow_const Z 0 1 Z.add Z.mul Z.sub Z.opp InitialRing.Zth) in H.
  unfold dmul in H. cbn [fst snd] in H.
  fold zpow in H. fold c in H.
  pose proof (lower_terms_bound c (B + 1) T (S T) ltac:(lia) ltac:(lia))
    as Hb.
  fold B in Hb. unfold zrsum in Hb.
  pose proof (zpow_pos (B + 1) T ltac:(lia)) as Hp.
  rewrite zpow_succ in H.
  change (rsum Z 0 Z.add
            (map (fun k => zpow (B + 1) k * c k) (seq 0 (S T)))
          = 0 * 0 + 1 * ((B + 1) * zpow (B + 1) T)) in H.
  rewrite H in Hb. nia.
Qed.

(* P8's refusal at r = 1 for the pure power x -> x^d, uniformly in d     *)
(* and N: the sum alone never closes, from extent 2 on (the draft's      *)
(* threshold N >= d r is sufficient, not sharp).                         *)
Definition powmap (d : nat) (_ : unit) (xs : list Z) : list Z :=
  map (fun x => zpow x d) xs.

Lemma zpow_zero : forall d, (1 <= d)%nat -> zpow 0 d = 0.
Proof. intros [|d] H; [lia|reflexivity]. Qed.

Lemma zpow_one : forall d, zpow 1 d = 1.
Proof.
  induction d as [|d IH]; [reflexivity|]. rewrite zpow_succ, IH.
  reflexivity.
Qed.

Lemma zpow_two : forall d, (2 <= d)%nat -> 2 < zpow 2 d.
Proof.
  intros d Hd. destruct d as [|[|d]]; try lia. rewrite !zpow_succ.
  pose proof (zpow_pos 2 d ltac:(lia)). lia.
Qed.

Theorem power_not_closed_S : forall d N, (2 <= d)%nat -> (2 <= N)%nat ->
  forall G : unit -> Z -> Z,
    ~ (forall u xs, length xs = N ->
         zpsum 1 (powmap d u xs) = G u (zpsum 1 xs)).
Proof.
  intros d N Hd HN.
  apply (collision_refutes_update unit (list Z) Z (powmap d) (zpsum 1)
           (fun xs => length xs = N) tt
           ([0; 2] ++ repeat 0 (N - 2)) ([1; 1] ++ repeat 0 (N - 2))).
  - rewrite app_length, repeat_length. simpl. lia.
  - rewrite app_length, repeat_length. simpl. lia.
  - rewrite !zpsum_app. reflexivity.
  - unfold powmap. rewrite !map_app, !zpsum_app. intro H.
    apply Z.add_cancel_r in H. unfold zpsum, psum in H.
    cbn [map rsum rpow] in H.
    rewrite zpow_zero, zpow_one in H by lia.
    pose proof (zpow_two d Hd). lia.
Qed.

(* P9 in use: supplied polynomial expressions for G, checked by `ring`,  *)
(* ARE a certificate.  Fixed extent 3 -- a P9 certificate is a finite    *)
(* identity and does not scale in N; affine_moment_tower_closed is the   *)
(* uniform statement.                                                    *)
Definition affFs (a b : Z) (i : nat) : pexp Z :=
  PAdd (PMul (PConst a) (PVar i)) (PConst b).
Definition sqQs (j : nat) : pexp Z :=
  match j with O => pP3 1 | _ => pP3 2 end.
Definition sqGs (a b : Z) (j : nat) : pexp Z :=
  match j with
  | O => PAdd (PMul (PConst a) (PVar 0)) (PConst (b * 3))
  | _ => PAdd (PMul (PConst (a * a)) (PVar 1))
              (PAdd (PMul (PConst (a * b * 2)) (PVar 0))
                    (PConst (b * b * 3)))
  end.

Example poly_certificate_affine_N3 : forall a b : Z,
  certificate (fun (_ : unit) rho => pstep Z Z.add Z.mul (affFs a b) rho)
              (qlist Z Z.add Z.mul 2 sqQs)
              (fun rho => pev Z Z.add Z.mul (pP3 2) rho) (fun _ => True)
              (fun _ z => Glist Z 0 Z.add Z.mul 2 (sqGs a b) z)
              (fun z => pev Z Z.add Z.mul (PVar 1) (lenv Z 0 z)).
Proof.
  intros a b. apply poly_certificate_sound.
  - intros j rho Hj. destruct j as [|[|j]]; [| |lia].
    + unfold pstep. simpl. ring.
    + unfold pstep. simpl. ring.
  - intros rho. reflexivity.
Qed.
