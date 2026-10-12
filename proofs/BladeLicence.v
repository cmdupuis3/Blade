(* ===================================================================== *)
(* BladeLicence.v -- NUMERICS AS A PARAMETER: the licence is a declared  *)
(* value equivalence.                                                    *)
(* (docs/plans/plan-one-model-surface-calculus.md, item 6)               *)
(*                                                                       *)
(* The group acts on indices and promises nothing about values.  Blade's *)
(* bit-changing choices (omp on a reduce, BLADE_FP_REASSOC) are          *)
(* LICENCES: declarations that the result need only equal the canonical  *)
(* one up to a declared relation R.  This file states the law class, the *)
(* well-formedness premise, the TYPING RELATION and the quotient theorem *)
(* relative to R, so the licence mechanism is inside the theory.  Values *)
(* stay nat (decision D2); genericity is over the relation.              *)
(*                                                                       *)
(*   invariant_under_R, Ksym_R, Kcls_R, kwf_R                            *)
(*                            BladeLowering.invariant_under,             *)
(*                            BladeOptimal.Ksym/Kcls and                 *)
(*                            BladeSurface.kwf with R for =              *)
(*   Ksym_R_Kcls_R, Kcls_R_Ksym_R, Ksym_R_iff_Kcls_R                     *)
(*                            invariant up to R under every position     *)
(*                            permutation iff constant up to R on        *)
(*                            classes (a list Permutation of the         *)
(*                            tabulation becomes a position permutation; *)
(*                            the two R-steps chain by transitivity)     *)
(*   Ksym_eq_iff, Kcls_eq_iff, kwf_eq_iff                                *)
(*                            R = eq gives back the tower's definitions  *)
(*                            exactly                                    *)
(*   apply_kernel_canon_R     the canonical read of a kwf_R application  *)
(*                            agrees with the dense value up to R        *)
(*   has_ty_R                 BladeSurface's has_ty with kwf_R R in the  *)
(*                            three kernel rules; has_ty_R_eq_iff (R =   *)
(*                            eq is has_ty), has_ty_has_ty_R (exact      *)
(*                            derivations are derivations up to every    *)
(*                            reflexive R), ty_appM_method_R (inversion) *)
(*   progress_R, preservation_R, normalizes_R, eval_sound_R              *)
(*                            type soundness of the calculus up to R,    *)
(*                            BladeSurface's proofs copied verbatim:     *)
(*                            they never consult kwf                     *)
(*   typed_app_is_quotient_R, typed_appO_is_quotient_R                   *)
(*                            for a has_ty_R R derivation of L <@> f     *)
(*                            (method_for or object_for): the typed      *)
(*                            shape is out_shape, the canonical read is  *)
(*                            the dense value up to R on every dense     *)
(*                            cell, one cell per orbit.  R need only be  *)
(*                            reflexive and transitive.  The binding-    *)
(*                            direct form is binding_app_is_quotient_R   *)
(*   typed_app_is_quotient_eq, typed_app_is_quotient_any_R               *)
(*                            R = eq is BladeSurface's middle conjunct;  *)
(*                            a has_ty derivation is a quotient up to    *)
(*                            every reflexive transitive R               *)
(*   licensed_not_exact       the parameter is real: f v = v0 + 3 v1     *)
(*                            declared comm is kwf up to parity (R x y   *)
(*                            := x mod 2 = y mod 2) and NOT kwf; has_ty  *)
(*                            refuses every application of it            *)
(*                            (not_kwf_untypable), has_ty_R Rmod2 types  *)
(*                            it, and the R-quotient theorem applies to  *)
(*                            that derivation                            *)
(*   fold_perm_invariant      a reduce kernel (left fold of op over the  *)
(*                            tabulation) with op associative and        *)
(*                            commutative is invariant EXACTLY under     *)
(*                            every permutation: the wrapping-integer /  *)
(*                            rational contract                          *)
(*   fold_perm_invariant_R, kfold_kwf_R, kfold_quotient_R                *)
(*                            op associative-commutative only up to a    *)
(*                            CONGRUENCE R: the fold is invariant up to  *)
(*                            R under every REORDERING, the comm reduce  *)
(*                            kernel is kwf_R and typable under has_ty_R *)
(*                            R, and the quotient theorem holds up to R  *)
(*   fold_split_R, fold_chunks_R, tree_reduce_R, reassoc_licence         *)
(*                            RE-BRACKETING needs one more premise: e an *)
(*                            R-identity.  Then two partial folds        *)
(*                            combined, per-chunk partials, and every    *)
(*                            combination tree over every reordering of  *)
(*                            the operands equal the canonical left fold *)
(*                            up to R: the omp-on-reduce licence.        *)
(*                            fold_split_needs_identity: without it the  *)
(*                            split counts e twice                       *)
(*   fold_licensed_not_exact  op x y = 3x + y: the fold is kwf up to     *)
(*                            parity and not kwf                         *)
(*   rounded_fold_not_exact, kfold_opr_not_kwf                           *)
(*                            BladeNumericContract's round-to-nearest-   *)
(*                            even fadd at EVERY precision p >= 2, arity *)
(*                            3: the rounded fold of (2^p, 1, 1) is 2^p  *)
(*                            in order and 2^p + 2 rotated, so the comm  *)
(*                            rounded reduce is never kwf                *)
(*   rounded_fold2_not_exact, rounded_fold2_exact                        *)
(*                            arity 2: data (2^p + 1, 1) separates the   *)
(*                            two orders, but on REPRESENTABLE data the  *)
(*                            arity-2 rounded fold is exactly order-     *)
(*                            independent (fadd commutes)                *)
(*   rounded_licence_coarse, assoc_congruence_collapses,                 *)
(*   assoc_congruence_collapses_p2                                       *)
(*                            a float licence is necessarily coarse: any *)
(*                            R the rounded reduce is kwf_R for, and any *)
(*                            R under which fadd is associative, relates *)
(*                            2^p and 2^p + 2; with the congruence too,  *)
(*                            at p = 2, 4 and 24.  So the congruence     *)
(*                            route of fold_perm_invariant_R never gives *)
(*                            a fine licence for a floating-point fold;  *)
(*                            one is DECLARED as kwf_R directly          *)
(*   pin_mod2_swap, pin_rounded_fold                                     *)
(*                            computed: 1 vs 3 with equal parity; 4 vs 6 *)
(*                                                                       *)
(* The R of the reduce theorems is the REASSOCIATION class: a congruence *)
(* for op under which op is associative and commutative (with e an       *)
(* identity, for re-bracketing).  Nothing in it relates op to a second   *)
(* operation, so it licenses reordering and re-bracketing a fold and     *)
(* nothing else -- BladeNumericContract's                                *)
(* no_ordering_recovers_the_reduced_value is the statement that a        *)
(* reassociation licence is not a distributivity licence, and this       *)
(* file's licences are of the first kind only.                           *)
(*                                                                       *)
(* Scope, stated once.  R is a PARAMETER, not a model of IEEE rounding:  *)
(* the theory says what follows once a program declares R; it does not   *)
(* say how far apart two R-related floats are.  Those rounding-error     *)
(* bounds live in proofs/reals/ (axiomatic reals, outside the tower) and *)
(* are not imported; a tolerance relation |x - y| <= eps is not          *)
(* transitive and so is not a licence here.  The rounded-fold theorems   *)
(* use the tower's integer-data rounding model (no exponent range), on   *)
(* nat values.  Only the ATTAINMENT half of the headline is restated     *)
(* relative to R (one cell per orbit, the canonical read correct up to   *)
(* R); the OPTIMALITY half -- BladeOptimal's orbit lower bound -- is     *)
(* not: a Ksym_R lower bound would need R to separate each perturbed     *)
(* value, and fails for a total R.  Rank-0 kernels and s = 1 identity    *)
(* groups, as in BladeSurface.                                           *)
(*                                                                       *)
(* Imports BladeNumericContract, BladeDMWF, BladeShape, BladeCompute,    *)
(* BladeMonad, BladeMixedRadix, BladeLowering, BladeCompleteness,        *)
(* BladeOptimal, BladeObject, BladeSurface.  Coq 8.18 / Rocq 9, stdlib   *)
(* only (List, Arith, Lia, Permutation, FinFun, ZArith), no axioms.      *)
(* ===================================================================== *)

From Blade Require Import BladeNumericContract.
From Blade Require Import BladeDMWF BladeShape BladeCompute BladeMonad BladeMixedRadix
                          BladeLowering BladeCompleteness BladeOptimal
                          BladeObject BladeSurface.
Require Import List Arith Lia Permutation FinFun ZArith.
Import ListNotations.

(* ===================================================================== *)
(* PART A.  THE PARAMETERIZED LICENCE.                                   *)
(* ===================================================================== *)

Section Licence.
  Variable R : nat -> nat -> Prop.
  Hypothesis R_refl : forall x, R x x.
  Hypothesis R_sym : forall x y, R x y -> R y x.
  Hypothesis R_trans : forall x y z, R x y -> R y z -> R x z.

  (* BladeLowering.invariant_under with R for = *)
  Definition invariant_under_R (f : (nat -> nat) -> nat) (s : nat -> nat) : Prop :=
    forall v, R (f (fun p => v (s p))) (f v).

  Definition Ksym_R (r : nat) (f : (nat -> nat) -> nat) : Prop :=
    (forall v v', (forall p, p < r -> v p = v' p) -> R (f v) (f v')) /\
    (forall s s', perm_pair r s s' -> invariant_under_R f s).

  Definition Kcls_R (r : nat) (f : (nat -> nat) -> nat) : Prop :=
    forall v v', scls r v = scls r v' -> R (f v) (f v').

  Lemma Kcls_R_Ksym_R : forall r f, Kcls_R r f -> Ksym_R r f.
  Proof.
    intros r f H. split.
    - intros v v' Hv. apply H. apply scls_local. exact Hv.
    - intros s s' Hp v. apply H. exact (scls_perm r s s' Hp v).
  Qed.

  (* BladeOptimal.Ksym_Kcls, chained by transitivity *)
  Lemma Ksym_R_Kcls_R : forall r f, Ksym_R r f -> Kcls_R r f.
  Proof.
    intros r f [Hloc Hinv] v v' E. unfold scls in E.
    assert (HP : Permutation (tab r v') (tab r v)).
    { apply perm_trans with (isort (tab r v')); [apply isort_perm |].
      rewrite <- E. apply Permutation_sym. apply isort_perm. }
    apply (Permutation_nth (tab r v') (tab r v) 0) in HP.
    cbv zeta in HP. destruct HP as (_ & g & Hfun & Hinj & Hnth).
    assert (Hlen : length (tab r v') = r)
      by (unfold tab; rewrite map_length, seq_length; reflexivity).
    rewrite Hlen in Hfun, Hinj, Hnth.
    pose proof (proj1 (bInjective_bSurjective Hfun) Hinj) as Hsurj.
    destruct (bSurjective_bBijective Hfun Hsurj) as (g' & Hfun' & Hbij).
    assert (Hpp : perm_pair r g g').
    { split; [exact Hfun |]. split; [exact Hfun' |].
      split; intros p Hp; apply (Hbij p Hp). }
    apply R_trans with (f (fun p => v' (g p))).
    - apply Hloc. intros p Hp.
      specialize (Hnth p Hp).
      rewrite nth_tab in Hnth by exact Hp.
      rewrite nth_tab in Hnth by (apply Hfun; exact Hp).
      exact Hnth.
    - exact (Hinv g g' Hpp v').
  Qed.

  Theorem Ksym_R_iff_Kcls_R : forall r f, Ksym_R r f <-> Kcls_R r f.
  Proof. intros r f. split; [apply Ksym_R_Kcls_R | apply Kcls_R_Ksym_R]. Qed.

End Licence.

(* R = eq is BladeOptimal's law class exactly *)
Theorem Ksym_eq_iff : forall r f, Ksym_R eq r f <-> Ksym r f.
Proof. intros r f. unfold Ksym_R, Ksym, invariant_under_R, invariant_under. tauto. Qed.

Theorem Kcls_eq_iff : forall r f, Kcls_R eq r f <-> Kcls r f.
Proof. intros r f. unfold Kcls_R, Kcls. tauto. Qed.

(* ===================================================================== *)
(* PART B.  THE SURFACE CALCULUS UP TO R.                                *)
(* ===================================================================== *)

Section SurfaceR.
  Variable R : nat -> nat -> Prop.
  Hypothesis R_refl : forall x, R x x.
  Hypothesis R_sym : forall x y, R x y -> R y x.
  Hypothesis R_trans : forall x y z, R x y -> R y z -> R x z.

  Definition klocal_R (f : kernel) : Prop :=
    forall v v', (forall p, p < k_arity f -> v p = v' p) ->
                 R (k_body f v) (k_body f v').

  (* BladeSurface.kwf with invariant_under_R: comm is a claim up to R *)
  Definition kwf_R (f : kernel) : Prop :=
    klocal_R f /\
    (k_comm f = true ->
     forall s s', perm_pair (k_arity f) s s' -> invariant_under_R R (k_body f) s).

  Lemma kwf_R_Ksym_R : forall f, kwf_R f -> k_comm f = true ->
    Ksym_R R (k_arity f) (k_body f).
  Proof. intros f [Hl Hc] E. split; [exact Hl | exact (Hc E)]. Qed.

  (* an exact licence is a licence up to any reflexive R *)
  Lemma kwf_kwf_R : forall f, kwf f -> kwf_R f.
  Proof.
    intros f [Hl Hc]. split.
    - intros v v' Hv. rewrite (Hl v v' Hv). apply R_refl.
    - intros E s s' Hp v. rewrite (Hc E s s' Hp v). apply R_refl.
  Qed.

  (* rle never produces a group of multiplicity 0 *)
  Lemma rle_mult_pos : forall b g, In g (rle b) -> g_mult g <> 0.
  Proof.
    intros b g Hg Em.
    induction b as [|[x s] b IH]; simpl in Hg; [exact Hg |].
    destruct (rle b) as [|g' gs]; simpl in Hg.
    - destruct Hg as [<- | []]. discriminate.
    - destruct (Nat.eq_dec x (g_name g')).
      + destruct (shape_eq_dec s (g_shape g')); simpl in Hg;
          destruct Hg as [<- | Hg]; try discriminate; apply IH; try (right; exact Hg); exact Hg.
      + simpl in Hg. destruct Hg as [<- | Hg]; [discriminate | apply IH; exact Hg].
  Qed.

  (* BladeSurface.apply_kernel_canon, up to R *)
  Theorem apply_kernel_canon_R : forall f ro b t,
    kwf_R f -> k_arity f = length b ->
    (forall x s, In (x, s) b -> fst (ro x) = s) ->
    In t (enumShape (dense_shape b)) ->
    shape_dense (out_shape (k_comm f) b) t /\
    R (apply_kernel f (as_of ro b) (scanon (out_shape (k_comm f) b) t))
      (apply_kernel f (as_of ro b) t).
  Proof.
    intros f ro b t Hwf Har Hro Ht.
    rewrite dense_shape_groups in Ht.
    assert (Hro' : forall g, In g (rle b) -> fst (ro (g_name g)) = g_shape g).
    { intros g Hg. apply Hro. rewrite <- (rle_expand b).
      apply in_concat. exists (expand g). split; [apply in_map; exact Hg |].
      unfold expand. destruct (g_mult g) eqn:Em.
      - exfalso. exact (rle_mult_pos b g Hg Em).
      - simpl. left. reflexivity. }
    destruct (groups_quotient (k_comm f) ro (rle b) t Hro' Ht) as [Hd HP].
    unfold out_shape. split; [exact Hd |].
    rewrite <- (rle_expand b) at 1 3. unfold apply_kernel.
    destruct (k_comm f) eqn:Ec.
    - pose proof (Ksym_R_Kcls_R R R_trans (k_arity f) (k_body f)
                    (kwf_R_Ksym_R f Hwf Ec)) as Hcls.
      apply Hcls. unfold scls, tab.
      assert (Lv : length (vals (as_of ro (concat (map expand (rle b)))) t) = k_arity f).
      { rewrite vals_length. unfold as_of. rewrite map_length, rle_expand. exact (eq_sym Har). }
      assert (Lv' : length (vals (as_of ro (concat (map expand (rle b))))
                                 (scanon (concat (map (group_shape true) (rle b))) t))
                    = k_arity f).
      { rewrite vals_length. unfold as_of. rewrite map_length, rle_expand. exact (eq_sym Har). }
      rewrite (tab_nth_r _ _ _ Lv), (tab_nth_r _ _ _ Lv').
      apply isort_respects. apply Permutation_sym. exact HP.
    - assert (E : concat (map (group_shape false) (rle b)) = concat (map dshape (rle b))).
      { reflexivity. }
      rewrite E. rewrite scanon_fixes by exact Ht. apply R_refl.
  Qed.

  (* the quotient for a binding directly: the premises ty_appM_method    *)
  (* extracts from a typing derivation, with kwf_R for kwf.  The typed   *)
  (* form is typed_app_is_quotient_R below.                              *)
  Lemma binding_app_is_quotient_R : forall G rho xs f b,
    env_ok G rho ->
    bind_names G xs = Some b ->
    kwf_R f -> k_arity f = length b ->
    fst (den rho (EAppM (EMethod xs) f)) = out_shape (k_comm f) b /\
    (forall t, In t (enumShape (dense_shape b)) ->
       shape_dense (out_shape (k_comm f) b) t /\
       R (qread (den rho (EAppM (EMethod xs) f)) t)
         (snd (den rho (EAppM (EMethod xs) f)) t)) /\
    (forall k : list nat -> nat,
       length (trace (object_prog (out_shape (k_comm f) b)) k)
       = shapeCard (out_shape (k_comm f) b)).
  Proof.
    intros G rho xs f b Hok Hb Hwf Har.
    pose proof (bind_arrs_as_of G rho xs b Hok Hb) as Hl.
    pose proof (binding_shapes G rho xs b Hok Hb) as Hsh.
    simpl. rewrite Hl. rewrite (shapes_of_as_of _ _ Hsh). simpl.
    split; [reflexivity |]. split.
    - intros t Ht'. unfold qread. simpl.
      exact (apply_kernel_canon_R f (rd rho) b t Hwf Har Hsh Ht').
    - intro k. apply object_work.
  Qed.

  (* object_for(f) <@> (x1, ..., xn) denotes the same array *)
  Lemma binding_appO_is_quotient_R : forall G rho xs f b,
    env_ok G rho ->
    bind_names G xs = Some b ->
    kwf_R f -> k_arity f = length b ->
    fst (den rho (EAppO (EObject f) xs)) = out_shape (k_comm f) b /\
    (forall t, In t (enumShape (dense_shape b)) ->
       shape_dense (out_shape (k_comm f) b) t /\
       R (qread (den rho (EAppO (EObject f) xs)) t)
         (snd (den rho (EAppO (EObject f) xs)) t)).
  Proof.
    intros G rho xs f b Hok Hb Hwf Har.
    destruct (binding_app_is_quotient_R G rho xs f b Hok Hb Hwf Har) as (H1 & H2 & _).
    change (den rho (EAppO (EObject f) xs)) with (den rho (EAppM (EMethod xs) f)).
    split; [exact H1 | exact H2].
  Qed.

End SurfaceR.

Theorem kwf_eq_iff : forall f, kwf_R eq f <-> kwf f.
Proof. intro f. unfold kwf_R, kwf, klocal_R, klocal, invariant_under_R, invariant_under. tauto. Qed.

(* ===================================================================== *)
(* PART B'.  TYPING UP TO R.  BladeSurface's has_ty with kwf_R R in the  *)
(* three kernel rules (T_Object, T_AppM, T_AppZ); nothing else changes.  *)
(* kwf is consulted by nothing in BladeSurface but these rules, their    *)
(* inversions and apply_kernel_canon, so progress, preservation,         *)
(* normalization and eval_sound transfer verbatim -- re-proved below by  *)
(* copying.                                                              *)
(* ===================================================================== *)

Inductive has_ty_R (R : nat -> nat -> Prop) (G : tenv) : expr -> ty -> Prop :=
| TR_Arr : forall s k,
    has_ty_R R G (EArr s k) (TArr s)
| TR_Var : forall x s,
    lookup x G = Some s ->
    has_ty_R R G (EVar x) (TArr s)
| TR_Method : forall xs b,
    bind_names G xs = Some b ->
    has_ty_R R G (EMethod xs) (TLoop b)
| TR_Zip : forall xs b s,
    bind_names G xs = Some b -> b <> [] -> all_shape s b ->
    has_ty_R R G (EZip xs) (TZipLoop s (length b))
| TR_Object : forall f,
    kwf_R R f ->
    has_ty_R R G (EObject f) (TObj f)
| TR_AppM : forall L b f,
    has_ty_R R G L (TLoop b) -> kwf_R R f -> k_arity f = length b ->
    has_ty_R R G (EAppM L f) (TComp (out_shape (k_comm f) b))
| TR_AppZ : forall L s n f,
    has_ty_R R G L (TZipLoop s n) -> kwf_R R f -> k_arity f = n ->
    has_ty_R R G (EAppM L f) (TComp s)
| TR_AppO : forall O f xs b,
    has_ty_R R G O (TObj f) -> bind_names G xs = Some b -> k_arity f = length b ->
    has_ty_R R G (EAppO O xs) (TComp (out_shape (k_comm f) b))
| TR_Compute : forall c s,
    has_ty_R R G c (TComp s) ->
    has_ty_R R G (ECompute c) (TArr s)
| TR_PipeC : forall c g s,
    has_ty_R R G c (TComp s) ->
    has_ty_R R G (EPipe c g) (TComp s)
| TR_PipeA : forall e g s,
    has_ty_R R G e (TArr s) ->
    has_ty_R R G (EPipe e g) (TArr s).

(* an exact derivation is a derivation up to every reflexive R *)
Theorem has_ty_has_ty_R : forall (R : nat -> nat -> Prop), (forall x, R x x) ->
  forall G e t, has_ty G e t -> has_ty_R R G e t.
Proof.
  intros R Hr G e t H.
  induction H; econstructor; eauto using kwf_kwf_R.
Qed.

(* R = eq: the two typing relations coincide *)
Theorem has_ty_R_eq_iff : forall G e t, has_ty_R eq G e t <-> has_ty G e t.
Proof.
  intros G e t. split; intro H.
  - induction H; econstructor; eauto; apply kwf_eq_iff; assumption.
  - apply has_ty_has_ty_R; [reflexivity | exact H].
Qed.

Section TypingR.
  Variable R : nat -> nat -> Prop.

  Lemma canon_loop_R : forall G L b, has_ty_R R G L (TLoop b) -> exists xs, L = EMethod xs.
  Proof. intros G L b H. inversion H; subst. eexists. reflexivity. Qed.

  Lemma canon_zip_R : forall G L s n, has_ty_R R G L (TZipLoop s n) -> exists xs, L = EZip xs.
  Proof. intros G L s n H. inversion H; subst. eexists. reflexivity. Qed.

  Lemma canon_obj_R : forall G Ob f, has_ty_R R G Ob (TObj f) -> Ob = EObject f.
  Proof. intros G Ob f H. inversion H; subst. reflexivity. Qed.

  Lemma ty_appM_method_R : forall G xs f s,
    has_ty_R R G (EAppM (EMethod xs) f) (TComp s) ->
    exists b, bind_names G xs = Some b /\ kwf_R R f /\ k_arity f = length b /\
              s = out_shape (k_comm f) b.
  Proof.
    intros G xs f s H. inversion H; subst.
    - match goal with [H' : has_ty_R _ _ (EMethod _) (TLoop _) |- _] => inversion H'; subst end.
      exists b. split; [assumption | split; [assumption | split; [assumption | reflexivity]]].
    - match goal with [H' : has_ty_R _ _ (EMethod _) (TZipLoop _ _) |- _] => inversion H' end.
  Qed.

  Lemma ty_appM_zip_R : forall G xs f s,
    has_ty_R R G (EAppM (EZip xs) f) (TComp s) ->
    exists b, bind_names G xs = Some b /\ b <> [] /\ all_shape s b /\
              kwf_R R f /\ k_arity f = length b.
  Proof.
    intros G xs f s H. inversion H; subst.
    - match goal with [H' : has_ty_R _ _ (EZip _) (TLoop _) |- _] => inversion H' end.
    - match goal with [H' : has_ty_R _ _ (EZip _) (TZipLoop _ _) |- _] => inversion H'; subst end.
      exists b. split; [assumption | split; [assumption | split; [assumption | split; [assumption | first [assumption | reflexivity | (symmetry; assumption)]]]]].
  Qed.

  Lemma ty_appO_R : forall G f xs s,
    has_ty_R R G (EAppO (EObject f) xs) (TComp s) ->
    exists b, bind_names G xs = Some b /\ kwf_R R f /\ k_arity f = length b /\
              s = out_shape (k_comm f) b.
  Proof.
    intros G f xs s H. inversion H; subst.
    match goal with [H' : has_ty_R _ _ (EObject _) (TObj _) |- _] => inversion H'; subst end.
    exists b. split; [assumption | split; [assumption | split; [assumption | reflexivity]]].
  Qed.

  Lemma cvalue_ty_R : forall G c t, cvalue c -> has_ty_R R G c t -> exists s, t = TComp s.
  Proof.
    intros G c t Hc. revert t. induction Hc; intros t Ht; inversion Ht; subst;
      try (eexists; reflexivity).
    match goal with [H' : has_ty_R _ _ c (TArr _) |- _] =>
      destruct (IHHc _ H') as [s' E]; discriminate end.
  Qed.

  Lemma value_arr_R : forall G e s, value e -> has_ty_R R G e (TArr s) -> exists k, e = EArr s k.
  Proof.
    intros G e s Hv Ht. inversion Hv; subst; try solve [inversion Ht; subst; eexists; reflexivity].
    match goal with [Hc : cvalue _ |- _] =>
      destruct (cvalue_ty_R G e (TArr s) Hc Ht) as [s' E]; discriminate end.
  Qed.

  Lemma value_comp_R : forall G e s, value e -> has_ty_R R G e (TComp s) -> cvalue e.
  Proof.
    intros G e s Hv Ht. inversion Hv; subst; try solve [inversion Ht]. assumption.
  Qed.

  (* BladeSurface.progress, verbatim with has_ty_R *)
  Theorem progress_R : forall G rho e t,
    env_ok G rho -> has_ty_R R G e t -> value e \/ exists e', step rho e e'.
  Proof.
    intros G rho e t Hok Ht. induction Ht.
    - left. constructor.
    - right. destruct (Hok x s H) as [k Hk]. exists (EArr s k).
      change (EArr s k) with (EArr (fst (s, k)) (snd (s, k))). constructor. exact Hk.
    - left. constructor.
    - left. constructor.
    - left. constructor.
    - destruct (canon_loop_R G L b Ht) as [xs ->]. left. constructor. constructor.
    - destruct (canon_zip_R G L s n Ht) as [xs ->]. left. constructor. constructor.
    - rewrite (canon_obj_R G O f Ht). left. constructor. constructor.
    - right. destruct IHHt as [Hv | [c' Hs]].
      + pose proof (value_comp_R G c s Hv Ht) as Hc.
        inversion Hc; subst.
        * destruct (ty_appM_method_R _ _ _ _ Ht) as (b & Hb & _).
          destruct (bind_arrs_some G rho xs b Hok Hb) as [l Hl].
          eexists. apply S_AppM. exact Hl.
        * destruct (ty_appM_zip_R _ _ _ _ Ht) as (b & Hb & _).
          destruct (bind_arrs_some G rho xs b Hok Hb) as [l Hl].
          eexists. apply S_AppZ. exact Hl.
        * destruct (ty_appO_R _ _ _ _ Ht) as (b & Hb & _).
          destruct (bind_arrs_some G rho xs b Hok Hb) as [l Hl].
          eexists. apply S_AppO. exact Hl.
        * eexists. apply S_ComputePipe.
      + eexists. apply S_Compute. exact Hs.
    - destruct IHHt as [Hv | [c' Hs]].
      + left. constructor. constructor. exact (value_comp_R G c s Hv Ht).
      + right. eexists. apply S_Pipe. exact Hs.
    - destruct IHHt as [Hv | [e' Hs]].
      + right. destruct (value_arr_R G e s Hv Ht) as [k ->].
        eexists. apply S_PipeA.
      + right. eexists. apply S_Pipe. exact Hs.
  Qed.

  (* BladeSurface.preservation, verbatim with has_ty_R *)
  Theorem preservation_R : forall G rho e e' t,
    env_ok G rho -> has_ty_R R G e t -> step rho e e' -> has_ty_R R G e' t.
  Proof.
    intros G rho e e' t Hok Ht Hs. revert t Ht.
    induction Hs; intros t Ht.
    - inversion Ht; subst.
      match goal with [Hl : lookup x G = Some ?s |- _] =>
        destruct (Hok x s Hl) as [k Hk]; rewrite H in Hk; injection Hk as E; subst A end.
      simpl. constructor.
    - inversion Ht; subst.
      match goal with [H' : has_ty_R _ _ (EAppM _ _) (TComp _) |- _] =>
        destruct (ty_appM_method_R _ _ _ _ H') as (b & Hb & _ & _ & ->) end.
      rewrite (bind_arrs_shapes G rho xs b l Hok Hb H). constructor.
    - inversion Ht; subst.
      match goal with [H' : has_ty_R _ _ (EAppM _ _) (TComp _) |- _] =>
        destruct (ty_appM_zip_R _ _ _ _ H') as (b & Hb & Hne & Hall & _ & _) end.
      pose proof (bind_arrs_shapes G rho xs b l Hok Hb H) as E.
      rewrite (zshape_of l s); [constructor | rewrite E; exact Hne | rewrite E; exact Hall].
    - inversion Ht; subst.
      match goal with [H' : has_ty_R _ _ (EAppO _ _) (TComp _) |- _] =>
        destruct (ty_appO_R _ _ _ _ H') as (b & Hb & _ & _ & ->) end.
      rewrite (bind_arrs_shapes G rho xs b l Hok Hb H). constructor.
    - inversion Ht; subst.
      match goal with [H' : has_ty_R _ _ (EPipe _ _) (TComp _) |- _] => inversion H'; subst end.
      constructor. constructor. assumption.
    - inversion Ht; subst. constructor. apply IHHs. assumption.
    - inversion Ht; subst.
      + match goal with [H' : has_ty_R _ _ (EArr _ _) (TComp _) |- _] => inversion H' end.
      + match goal with [H' : has_ty_R _ _ (EArr _ _) (TArr _) |- _] => inversion H'; subst end.
        constructor.
    - inversion Ht; subst; constructor; apply IHHs; assumption.
  Qed.

  Lemma star_preservation_R : forall G rho e e' t,
    env_ok G rho -> has_ty_R R G e t -> star rho e e' -> has_ty_R R G e' t.
  Proof.
    intros G rho e e' t Hok Ht Hs. induction Hs; [exact Ht |].
    apply IHHs. exact (preservation_R G rho e e' t Hok Ht H).
  Qed.

  Theorem normalizes_R : forall G rho e t,
    env_ok G rho -> has_ty_R R G e t -> exists v, star rho e v /\ value v.
  Proof.
    intros G rho e t Hok Ht.
    remember (size e) as n eqn:En. revert e t En Ht.
    induction n as [n IH] using lt_wf_ind; intros e t En Ht.
    destruct (progress_R G rho e t Hok Ht) as [Hv | [e' Hs]].
    - exists e. split; [constructor | exact Hv].
    - pose proof (step_size rho e e' Hs) as Hlt.
      destruct (IH (size e') ltac:(lia) e' t eq_refl
                   (preservation_R G rho e e' t Hok Ht Hs)) as (v & Hv1 & Hv2).
      exists v. split; [apply star_step with e'; assumption | exact Hv2].
  Qed.

  Theorem eval_sound_R : forall G rho e s,
    env_ok G rho -> has_ty_R R G e (TArr s) ->
    exists k, star rho e (EArr s k) /\ den rho e = (s, k).
  Proof.
    intros G rho e s Hok Ht.
    destruct (normalizes_R G rho e (TArr s) Hok Ht) as (v & Hst & Hv).
    pose proof (star_preservation_R G rho e v (TArr s) Hok Ht Hst) as Hv'.
    destruct (value_arr_R G v s Hv Hv') as [k ->].
    exists k. split; [exact Hst | rewrite (star_den rho e _ Hst); reflexivity].
  Qed.

End TypingR.

(* a kernel that is not kwf is refused by has_ty at every application  *)
Lemma not_kwf_untypable : forall f, ~ kwf f ->
  forall G L t, ~ has_ty G (EAppM L f) t.
Proof.
  intros f Hn G L t H. inversion H; subst; contradiction.
Qed.

(* THE THEOREM, up to R.  BladeSurface.typed_app_is_quotient with       *)
(* has_ty_R R for has_ty and R for = in the read; R need only be        *)
(* reflexive and transitive.                                            *)
Theorem typed_app_is_quotient_R : forall (R : nat -> nat -> Prop),
  (forall x, R x x) -> (forall x y z, R x y -> R y z -> R x z) ->
  forall G rho xs f s',
  env_ok G rho ->
  has_ty_R R G (EAppM (EMethod xs) f) (TComp s') ->
  exists b,
    bind_names G xs = Some b /\
    s' = out_shape (k_comm f) b /\
    fst (den rho (EAppM (EMethod xs) f)) = s' /\
    (forall t, In t (enumShape (dense_shape b)) ->
       shape_dense s' t /\
       R (qread (den rho (EAppM (EMethod xs) f)) t)
         (snd (den rho (EAppM (EMethod xs) f)) t)) /\
    (forall k : list nat -> nat,
       length (trace (object_prog s') k) = shapeCard s').
Proof.
  intros R Hr Htr G rho xs f s' Hok Ht.
  destruct (ty_appM_method_R R _ _ _ _ Ht) as (b & Hb & Hwf & Har & ->).
  exists b. split; [exact Hb |]. split; [reflexivity |].
  exact (binding_app_is_quotient_R R Hr Htr G rho xs f b Hok Hb Hwf Har).
Qed.

Theorem typed_appO_is_quotient_R : forall (R : nat -> nat -> Prop),
  (forall x, R x x) -> (forall x y z, R x y -> R y z -> R x z) ->
  forall G rho xs f s',
  env_ok G rho ->
  has_ty_R R G (EAppO (EObject f) xs) (TComp s') ->
  exists b,
    bind_names G xs = Some b /\ s' = out_shape (k_comm f) b /\
    fst (den rho (EAppO (EObject f) xs)) = s' /\
    (forall t, In t (enumShape (dense_shape b)) ->
       shape_dense s' t /\
       R (qread (den rho (EAppO (EObject f) xs)) t)
         (snd (den rho (EAppO (EObject f) xs)) t)).
Proof.
  intros R Hr Htr G rho xs f s' Hok Ht.
  destruct (ty_appO_R R _ _ _ _ Ht) as (b & Hb & Hwf & Har & ->).
  exists b. split; [exact Hb |]. split; [reflexivity |].
  exact (binding_appO_is_quotient_R R Hr Htr G rho xs f b Hok Hb Hwf Har).
Qed.

(* R = eq: BladeSurface's theorem, from a has_ty derivation *)
Corollary typed_app_is_quotient_eq : forall G rho xs f s',
  env_ok G rho ->
  has_ty G (EAppM (EMethod xs) f) (TComp s') ->
  exists b,
    bind_names G xs = Some b /\
    (forall t, In t (enumShape (dense_shape b)) ->
       shape_dense s' t /\
       qread (den rho (EAppM (EMethod xs) f)) t
       = snd (den rho (EAppM (EMethod xs) f)) t).
Proof.
  intros G rho xs f s' Hok Ht.
  apply has_ty_R_eq_iff in Ht.
  destruct (typed_app_is_quotient_R eq (@eq_refl nat) (@eq_trans nat) G rho xs f s' Hok Ht)
    as (b & Hb & _ & _ & H & _).
  exists b. split; [exact Hb | exact H].
Qed.

(* every has_ty derivation is a quotient up to EVERY declared preorder:  *)
(* the exact licence is the strongest one                                *)
Corollary typed_app_is_quotient_any_R : forall (R : nat -> nat -> Prop),
  (forall x, R x x) -> (forall x y z, R x y -> R y z -> R x z) ->
  forall G rho xs f s',
  env_ok G rho ->
  has_ty G (EAppM (EMethod xs) f) (TComp s') ->
  exists b,
    bind_names G xs = Some b /\
    (forall t, In t (enumShape (dense_shape b)) ->
       shape_dense s' t /\
       R (qread (den rho (EAppM (EMethod xs) f)) t)
         (snd (den rho (EAppM (EMethod xs) f)) t)).
Proof.
  intros R Hr Htr G rho xs f s' Hok Ht.
  destruct (typed_app_is_quotient_R R Hr Htr G rho xs f s' Hok
              (has_ty_has_ty_R R Hr G _ _ Ht)) as (b & Hb & _ & _ & H & _).
  exists b. split; [exact Hb | exact H].
Qed.

(* ===================================================================== *)
(* PART C.  INSTANCES: THE PARAMETER IS REAL.                            *)
(* ===================================================================== *)

(* --- C1. R = eq: the exact (integer) contract -- kwf_eq_iff and      *)
(* typed_app_is_quotient_eq above.                                       *)

(* --- C2. a licence up to a coarser R that is not a licence up to eq -- *)

Definition Rmod2 (x y : nat) : Prop := x mod 2 = y mod 2.

Lemma Rmod2_refl : forall x, Rmod2 x x.
Proof. intro x. reflexivity. Qed.

Lemma Rmod2_sym : forall x y, Rmod2 x y -> Rmod2 y x.
Proof. intros x y H. unfold Rmod2 in *. symmetry. exact H. Qed.

Lemma Rmod2_trans : forall x y z, Rmod2 x y -> Rmod2 y z -> Rmod2 x z.
Proof. intros x y z H1 H2. unfold Rmod2 in *. rewrite H1. exact H2. Qed.

Definition f3 (v : nat -> nat) : nat := v 0 + 3 * v 1.

Definition swap01 (p : nat) : nat :=
  match p with 0 => 1 | 1 => 0 | _ => p end.

Lemma swap01_perm : perm_pair 2 swap01 swap01.
Proof.
  split; [| split; [| split]]; intros p Hp;
    destruct p as [|[|p]]; simpl; lia.
Qed.

Lemma mod2_3 : forall a b, (a + 3 * b) mod 2 = (a + b) mod 2.
Proof.
  intros a b. replace (a + 3 * b) with ((a + b) + b * 2) by lia.
  apply Nat.Div0.mod_add.
Qed.

Lemma f3_swap_R : invariant_under_R Rmod2 f3 swap01.
Proof.
  intro v. unfold Rmod2, f3, swap01. rewrite !mod2_3. f_equal. lia.
Qed.

Lemma f3_not_swap_eq : ~ invariant_under nat nat f3 swap01.
Proof.
  intro H. specialize (H (fun p => p)). unfold f3, swap01 in H. simpl in H. discriminate.
Qed.

(* every permutation of two positions is the identity or the swap *)
Lemma perm_pair_2_cases : forall s s', perm_pair 2 s s' ->
  (s 0 = 0 /\ s 1 = 1) \/ (s 0 = 1 /\ s 1 = 0).
Proof.
  intros s s' (Hs & _ & Hi & _).
  pose proof (Hs 0 ltac:(lia)) as H0. pose proof (Hs 1 ltac:(lia)) as H1.
  pose proof (Hi 0 ltac:(lia)) as I0. pose proof (Hi 1 ltac:(lia)) as I1.
  destruct (s 0) as [|[|]] eqn:E0; destruct (s 1) as [|[|]] eqn:E1; try lia;
    first [ left; split; reflexivity | right; split; reflexivity
          | rewrite <- I0 in I1; discriminate ].
Qed.

Definition kmod2 : kernel := mkK 2 f3 true.

Lemma kmod2_kwf_R : kwf_R Rmod2 kmod2.
Proof.
  split.
  - intros v v' Hv. cbn in Hv. unfold Rmod2. simpl. unfold f3.
    rewrite (Hv 0 ltac:(lia)), (Hv 1 ltac:(lia)). reflexivity.
  - intros _ s s' Hp v. simpl in Hp |- *.
    destruct (perm_pair_2_cases s s' Hp) as [[E0 E1] | [E0 E1]];
      unfold Rmod2, f3; rewrite E0, E1.
    + reflexivity.
    + rewrite !mod2_3. f_equal. lia.
Qed.

Lemma kmod2_not_kwf : ~ kwf kmod2.
Proof.
  intros [_ Hc]. apply f3_not_swap_eq.
  exact (Hc eq_refl swap01 swap01 swap01_perm).
Qed.

(* the comm claim on f3 is licensed up to parity and refused up to       *)
(* equality: has_ty rejects every application of it, has_ty_R Rmod2     *)
(* types it, and the R-quotient theorem applies to that derivation       *)
Theorem licensed_not_exact :
  kwf_R Rmod2 kmod2 /\ ~ kwf kmod2 /\
  (forall G L t, ~ has_ty G (EAppM L kmod2) t) /\
  forall G rho xs b,
    env_ok G rho -> bind_names G xs = Some b -> length b = 2 ->
    has_ty_R Rmod2 G (EAppM (EMethod xs) kmod2) (TComp (out_shape true b)) /\
    fst (den rho (EAppM (EMethod xs) kmod2)) = out_shape true b /\
    (forall t, In t (enumShape (dense_shape b)) ->
       shape_dense (out_shape true b) t /\
       Rmod2 (qread (den rho (EAppM (EMethod xs) kmod2)) t)
             (snd (den rho (EAppM (EMethod xs) kmod2)) t)).
Proof.
  split; [exact kmod2_kwf_R |]. split; [exact kmod2_not_kwf |].
  split; [exact (not_kwf_untypable kmod2 kmod2_not_kwf) |].
  intros G rho xs b Hok Hb Hl.
  assert (Hty : has_ty_R Rmod2 G (EAppM (EMethod xs) kmod2) (TComp (out_shape true b))).
  { change true with (k_comm kmod2).
    apply TR_AppM; [apply TR_Method; exact Hb | exact kmod2_kwf_R | simpl; lia]. }
  split; [exact Hty |].
  destruct (typed_app_is_quotient_R Rmod2 Rmod2_refl Rmod2_trans G rho xs kmod2 _ Hok Hty)
    as (b' & Hb' & Es & H1 & H2 & _).
  rewrite Hb in Hb'. injection Hb' as <-.
  split; [exact H1 | exact H2].
Qed.

(* --- C3. the reassociation instance: a FOLD kernel ------------------ *)

(* a reduce kernel: the left fold of op over the tabulation, in         *)
(* position order (BladeOptimal.tab)                                    *)
Definition fold_k (op : nat -> nat -> nat) (e r : nat) (v : nat -> nat) : nat :=
  fold_left op (tab r v) e.

Definition kfold (op : nat -> nat -> nat) (e r : nat) : kernel :=
  mkK r (fold_k op e r) true.

Lemma tab_perm : forall r s s' v, perm_pair r s s' ->
  Permutation (tab r (fun p => v (s p))) (tab r v).
Proof.
  intros r s s' v Hp. unfold tab. rewrite <- (map_map s v).
  apply Permutation_map. exact (map_s_perm r s s' Hp).
Qed.

Lemma tab_local : forall r v v', (forall p, p < r -> v p = v' p) -> tab r v = tab r v'.
Proof.
  intros r v v' H. unfold tab. apply map_ext_in. intros a Ha.
  apply in_seq in Ha. apply H. lia.
Qed.

(* exact: an associative-commutative op folds to the same value in every *)
(* order (the wrapping-integer / rational contract of                   *)
(* BladeNumericContract)                                                 *)
Lemma fold_left_perm : forall (op : nat -> nat -> nat),
  (forall x y z, op (op x y) z = op x (op y z)) ->
  (forall x y, op x y = op y x) ->
  forall l l', Permutation l l' ->
  forall acc, fold_left op l acc = fold_left op l' acc.
Proof.
  intros op Ha Hc l l' HP. induction HP as [| x l l' HP IH | x y l | l l' l'' H1 IH1 H2 IH2];
    intro acc; simpl.
  - reflexivity.
  - apply IH.
  - f_equal. rewrite !Ha. f_equal. apply Hc.
  - rewrite IH1. apply IH2.
Qed.

Theorem fold_perm_invariant : forall (op : nat -> nat -> nat) e r,
  (forall x y z, op (op x y) z = op x (op y z)) ->
  (forall x y, op x y = op y x) ->
  forall s s', perm_pair r s s' -> invariant_under nat nat (fold_k op e r) s.
Proof.
  intros op e r Ha Hc s s' Hp v. unfold fold_k.
  apply (fold_left_perm op Ha Hc). apply (tab_perm r s s' v Hp).
Qed.

Corollary kfold_kwf : forall (op : nat -> nat -> nat) e r,
  (forall x y z, op (op x y) z = op x (op y z)) ->
  (forall x y, op x y = op y x) ->
  kwf (kfold op e r).
Proof.
  intros op e r Ha Hc. split.
  - intros v v' Hv. simpl in Hv |- *. unfold fold_k. rewrite (tab_local r v v' Hv). reflexivity.
  - intros _ s s' Hp. exact (fold_perm_invariant op e r Ha Hc s s' Hp).
Qed.

(* a combination tree: how a parallel reduction brackets its operands *)
Inductive rtree : Type :=
| RLeaf : nat -> rtree
| RNode : rtree -> rtree -> rtree.

Fixpoint rt_eval (op : nat -> nat -> nat) (t : rtree) : nat :=
  match t with
  | RLeaf x => x
  | RNode t1 t2 => op (rt_eval op t1) (rt_eval op t2)
  end.

Fixpoint rt_leaves (t : rtree) : list nat :=
  match t with
  | RLeaf x => [x]
  | RNode t1 t2 => rt_leaves t1 ++ rt_leaves t2
  end.

(* up to a congruence: op associative-commutative only up to R *)
Section FoldR.
  Variable R : nat -> nat -> Prop.
  Hypothesis R_refl : forall x, R x x.
  Hypothesis R_sym : forall x y, R x y -> R y x.
  Hypothesis R_trans : forall x y z, R x y -> R y z -> R x z.
  Variable op : nat -> nat -> nat.
  Hypothesis op_cong : forall x x' y y', R x x' -> R y y' -> R (op x y) (op x' y').
  Hypothesis op_assoc_R : forall x y z, R (op (op x y) z) (op x (op y z)).
  Hypothesis op_comm_R : forall x y, R (op x y) (op y x).

  Lemma fold_left_R_acc : forall l acc acc', R acc acc' ->
    R (fold_left op l acc) (fold_left op l acc').
  Proof.
    induction l as [|x l IH]; intros acc acc' H; simpl; [exact H |].
    apply IH. apply op_cong; [exact H | apply R_refl].
  Qed.

  Lemma fold_left_perm_R : forall l l', Permutation l l' ->
    forall acc, R (fold_left op l acc) (fold_left op l' acc).
  Proof.
    intros l l' HP. induction HP as [| x l l' HP IH | x y l | l l' l'' H1 IH1 H2 IH2];
      intro acc; simpl.
    - apply R_refl.
    - apply IH.
    - apply fold_left_R_acc.
      apply R_trans with (op acc (op y x)); [apply op_assoc_R |].
      apply R_trans with (op acc (op x y)).
      + apply op_cong; [apply R_refl | apply op_comm_R].
      + apply R_sym. apply op_assoc_R.
    - apply R_trans with (fold_left op l' acc); [apply IH1 | apply IH2].
  Qed.

  (* the omp-on-reduce licence: reordering licensed by a congruence *)
  Theorem fold_perm_invariant_R : forall e r s s', perm_pair r s s' ->
    invariant_under_R R (fold_k op e r) s.
  Proof.
    intros e r s s' Hp v. unfold fold_k.
    apply fold_left_perm_R. apply (tab_perm r s s' v Hp).
  Qed.

  Corollary kfold_kwf_R : forall e r, kwf_R R (kfold op e r).
  Proof.
    intros e r. split.
    - intros v v' Hv. simpl in Hv |- *. unfold fold_k.
      rewrite (tab_local r v v' Hv). apply R_refl.
    - intros _ s s' Hp. exact (fold_perm_invariant_R e r s s' Hp).
  Qed.

  (* the comm reduce kernel is typable up to R ... *)
  Lemma kfold_typable_R : forall e G xs b,
    bind_names G xs = Some b ->
    has_ty_R R G (EAppM (EMethod xs) (kfold op e (length b))) (TComp (out_shape true b)).
  Proof.
    intros e G xs b Hb.
    change true with (k_comm (kfold op e (length b))).
    apply TR_AppM; [apply TR_Method; exact Hb | apply kfold_kwf_R | reflexivity].
  Qed.

  (* ... so the quotient theorem holds for it up to R, from its typing *)
  Corollary kfold_quotient_R : forall e G rho xs b,
    env_ok G rho -> bind_names G xs = Some b ->
    forall t, In t (enumShape (dense_shape b)) ->
      shape_dense (out_shape true b) t /\
      R (qread (den rho (EAppM (EMethod xs) (kfold op e (length b)))) t)
        (snd (den rho (EAppM (EMethod xs) (kfold op e (length b)))) t).
  Proof.
    intros e G rho xs b Hok Hb.
    destruct (typed_app_is_quotient_R R R_refl R_trans G rho xs _ _ Hok
                (kfold_typable_R e G xs b Hb)) as (b' & Hb' & _ & _ & H & _).
    rewrite Hb in Hb'. injection Hb' as <-. exact H.
  Qed.

  (* --- re-bracketing: needs e to be an R-identity ------------------- *)
  Section Identity.
    Variable e : nat.
    Hypothesis e_id_R : forall x, R (op e x) x.

    Lemma fold_left_acc_R : forall l a,
      R (fold_left op l a) (op a (fold_left op l e)).
    Proof.
      induction l as [|x l IH]; intro a; simpl.
      - apply R_sym. apply R_trans with (op e a); [apply op_comm_R | apply e_id_R].
      - apply R_trans with (op (op a x) (fold_left op l e)); [apply IH |].
        apply R_trans with (op a (op x (fold_left op l e))); [apply op_assoc_R |].
        apply op_cong; [apply R_refl |].
        apply R_sym. apply R_trans with (fold_left op l x).
        + apply fold_left_R_acc. apply e_id_R.
        + apply IH.
    Qed.

    (* two partial folds combined: the lane-parallel / OpenMP split *)
    Theorem fold_split_R : forall l1 l2,
      R (op (fold_left op l1 e) (fold_left op l2 e)) (fold_left op (l1 ++ l2) e).
    Proof.
      intros l1 l2. rewrite fold_left_app. apply R_sym. apply fold_left_acc_R.
    Qed.

    (* per-chunk partials folded left to right *)
    Corollary fold_chunks_R : forall cs,
      R (fold_left op (map (fun c => fold_left op c e) cs) e) (fold_left op (concat cs) e).
    Proof.
      induction cs as [|c cs IH]; simpl; [apply R_refl |].
      apply R_trans with (op (op e (fold_left op c e))
                             (fold_left op (map (fun c => fold_left op c e) cs) e));
        [apply fold_left_acc_R |].
      apply R_trans with (op (fold_left op c e) (fold_left op (concat cs) e)).
      - apply op_cong; [apply e_id_R | exact IH].
      - apply fold_split_R.
    Qed.

    (* every bracketing: a combination tree evaluates, up to R, to the  *)
    (* left fold of its leaves                                          *)
    Theorem tree_reduce_R : forall t, R (rt_eval op t) (fold_left op (rt_leaves t) e).
    Proof.
      induction t as [x | t1 IH1 t2 IH2]; simpl.
      - apply R_sym. apply e_id_R.
      - apply R_trans with (op (fold_left op (rt_leaves t1) e) (fold_left op (rt_leaves t2) e)).
        + apply op_cong; assumption.
        + apply fold_split_R.
    Qed.

    (* THE REDUCE LICENCE: any bracketing of any reordering of the       *)
    (* operands equals the canonical left fold up to R                  *)
    Theorem reassoc_licence : forall r s s' v t,
      perm_pair r s s' -> rt_leaves t = tab r (fun p => v (s p)) ->
      R (rt_eval op t) (fold_k op e r v).
    Proof.
      intros r s s' v t Hp Hl.
      apply R_trans with (fold_left op (rt_leaves t) e); [apply tree_reduce_R |].
      rewrite Hl. exact (fold_perm_invariant_R e r s s' Hp v).
    Qed.
  End Identity.
End FoldR.

(* the identity premise is needed: + is associative, commutative and a   *)
(* congruence for =, but with e = 1 the split counts e twice             *)
Example fold_split_needs_identity :
  Nat.add (fold_left Nat.add [0] 1) (fold_left Nat.add [0] 1) = 2 /\
  fold_left Nat.add ([0] ++ [0]) 1 = 1.
Proof. vm_compute. split; reflexivity. Qed.

(* a fold licensed up to parity and not exactly: op3 x y = 3x + y is    *)
(* neither commutative nor associative, and is both modulo 2            *)
Definition op3 (x y : nat) : nat := 3 * x + y.

Lemma op3_mod2 : forall x y, (op3 x y) mod 2 = (x + y) mod 2.
Proof.
  intros x y. unfold op3. replace (3 * x + y) with ((x + y) + x * 2) by lia.
  apply Nat.Div0.mod_add.
Qed.

Lemma op3_cong : forall x x' y y', Rmod2 x x' -> Rmod2 y y' -> Rmod2 (op3 x y) (op3 x' y').
Proof.
  unfold Rmod2. intros x x' y y' Hx Hy. rewrite !op3_mod2.
  rewrite (Nat.Div0.add_mod x y), (Nat.Div0.add_mod x' y'), Hx, Hy. reflexivity.
Qed.

Lemma op3_assoc_R : forall x y z, Rmod2 (op3 (op3 x y) z) (op3 x (op3 y z)).
Proof.
  unfold Rmod2. intros x y z. rewrite !op3_mod2.
  rewrite (Nat.Div0.add_mod (op3 x y) z), op3_mod2, <- Nat.Div0.add_mod.
  rewrite (Nat.Div0.add_mod x (op3 y z)), op3_mod2, <- Nat.Div0.add_mod.
  f_equal. lia.
Qed.

Lemma op3_comm_R : forall x y, Rmod2 (op3 x y) (op3 y x).
Proof. unfold Rmod2. intros x y. rewrite !op3_mod2. f_equal. lia. Qed.

Theorem fold_licensed_not_exact :
  kwf_R Rmod2 (kfold op3 0 2) /\ ~ kwf (kfold op3 0 2).
Proof.
  split.
  - exact (kfold_kwf_R Rmod2 Rmod2_refl Rmod2_sym Rmod2_trans op3
             op3_cong op3_assoc_R op3_comm_R 0 2).
  - intros [_ Hc]. specialize (Hc eq_refl swap01 swap01 swap01_perm (fun p => 1 - p)).
    vm_compute in Hc. discriminate.
Qed.

(* --- C4. rounded addition: the licence must be declared, and is coarse *)
(* BladeNumericContract's round-to-nearest-even addition at precision p, *)
(* on nat data.  At arity 3 the fold of fadd is NOT invariant under      *)
(* reordering, at every precision p >= 2 (data 2^p, 1, 1: the binary64   *)
(* 2^53 + 1 + 1 shape), so a comm claim on a rounded reduce is never     *)
(* kwf; any R it is kwf_R for relates 2^p and 2^p + 2.  At arity 2 the   *)
(* fold of REPRESENTABLE data is exactly order-independent (fadd         *)
(* commutes); only non-representable data separates the two orders.     *)
(* And the congruence route of C3 cannot give a fine licence: any R      *)
(* under which fadd is associative relates 2^p and 2^p + 2, and with     *)
(* the congruence at p = 2 it already relates 4 and 24.                  *)

Definition opr (p : Z) (x y : nat) : nat := Z.to_nat (fadd p (Z.of_nat x) (Z.of_nat y)).

Lemma opr_comm : forall p x y, opr p x y = opr p y x.
Proof. intros p x y. unfold opr, fadd. rewrite Z.add_comm. reflexivity. Qed.

(* the rotation 0 -> 1 -> 2 -> 0 on three positions *)
Definition rot3 (p : nat) : nat := match p with 0 => 1 | 1 => 2 | 2 => 0 | _ => p end.
Definition rot3' (p : nat) : nat := match p with 0 => 2 | 1 => 0 | 2 => 1 | _ => p end.

Lemma rot3_perm : perm_pair 3 rot3 rot3'.
Proof.
  split; [| split; [| split]]; intros q Hq;
    destruct q as [|[|[|q]]]; simpl; lia.
Qed.

(* the five rounded sums the counterexamples need, at every p >= 2 *)
Section RoundedSums.
  Variable p : Z.
  Hypothesis Hp : (2 <= p)%Z.

  Let P := Z.to_nat (2 ^ p).

  Lemma pow2_ge4 : (4 <= 2 ^ p)%Z.
  Proof. apply pow2_gt. exact Hp. Qed.

  Lemma opr_small : forall x y, (Z.of_nat x + Z.of_nat y < 2 ^ p)%Z ->
    opr p x y = x + y.
  Proof.
    intros x y H. unfold opr, fadd. rewrite rne_exact by lia. lia.
  Qed.

  Lemma opr_0_P : opr p 0 P = P.
  Proof.
    pose proof pow2_ge4. unfold opr, fadd, P. rewrite Z2Nat.id by lia. simpl Z.of_nat.
    rewrite Z.add_0_l. rewrite rne_pow2 by lia. reflexivity.
  Qed.

  (* the tie: 2^p + 1 rounds to 2^p *)
  Lemma opr_P_1 : opr p P 1 = P.
  Proof.
    pose proof pow2_ge4. unfold opr, fadd, P. rewrite Z2Nat.id by lia. simpl Z.of_nat.
    rewrite rne_tie by exact Hp. reflexivity.
  Qed.

  (* 2^p + 2 is representable *)
  Lemma rne_P2 : rne p (2 ^ p + 2) = (2 ^ p + 2)%Z.
  Proof.
    pose proof pow2_ge4 as H4. rewrite (pow2_split p) in H4 by lia.
    replace (2 ^ p + 2)%Z with (2 * (2 ^ (p - 1) + 1))%Z
      by (rewrite (pow2_split p) by lia; lia).
    apply rne_binade_even; [lia |]. rewrite (pow2_split p) by lia. lia.
  Qed.

  Lemma opr_2_P : opr p 2 P = Z.to_nat (2 ^ p + 2).
  Proof.
    pose proof pow2_ge4. unfold opr, fadd, P. rewrite Z2Nat.id by lia.
    change (Z.of_nat 2) with 2%Z. rewrite Z.add_comm. rewrite rne_P2. reflexivity.
  Qed.

  Lemma opr_P1_tie : opr p 0 (Z.to_nat (2 ^ p + 1)) = P.
  Proof.
    pose proof pow2_ge4. unfold opr, fadd, P. rewrite Z2Nat.id by lia. simpl Z.of_nat.
    rewrite Z.add_0_l. rewrite rne_tie by exact Hp. reflexivity.
  Qed.

  Lemma opr_1_P1 : opr p 1 (Z.to_nat (2 ^ p + 1)) = Z.to_nat (2 ^ p + 2).
  Proof.
    pose proof pow2_ge4. unfold opr, fadd. rewrite Z2Nat.id by lia. simpl Z.of_nat.
    replace (1 + (2 ^ p + 1))%Z with (2 ^ p + 2)%Z by lia. rewrite rne_P2. reflexivity.
  Qed.

  Lemma P2_neq_P : Z.to_nat (2 ^ p + 2) <> P.
  Proof. pose proof pow2_ge4. unfold P. intro E. apply Z2Nat.inj in E; lia. Qed.

  (* the two orders of (2^p, 1, 1) *)
  Lemma rounded_fold_values :
    fold_k (opr p) 0 3 (fun q => match q with 0 => P | _ => 1 end) = P /\
    fold_k (opr p) 0 3 (fun q => match rot3 q with 0 => P | _ => 1 end)
    = Z.to_nat (2 ^ p + 2).
  Proof.
    pose proof pow2_ge4. unfold fold_k, tab. cbn [seq map fold_left rot3].
    rewrite opr_0_P, !opr_P_1. split; [reflexivity |].
    replace (opr p 0 1) with 1 by (rewrite opr_small by (simpl; lia); reflexivity).
    replace (opr p 1 1) with 2 by (rewrite opr_small by (simpl; lia); reflexivity).
    exact opr_2_P.
  Qed.

  Theorem rounded_fold_not_exact :
    ~ invariant_under nat nat (fold_k (opr p) 0 3) rot3.
  Proof.
    intro H. specialize (H (fun q => match q with 0 => P | _ => 1 end)).
    destruct rounded_fold_values as [E1 E2]. cbv beta in H.
    rewrite E1 in H.
    rewrite E2 in H. exact (P2_neq_P H).
  Qed.

  Corollary kfold_opr_not_kwf : ~ kwf (kfold (opr p) 0 3).
  Proof.
    intros [_ Hc]. apply rounded_fold_not_exact.
    exact (Hc eq_refl rot3 rot3' rot3_perm).
  Qed.

  (* any declared licence for the rounded reduce identifies two distinct *)
  (* representable values: the licence is necessarily coarser than =    *)
  Theorem rounded_licence_coarse : forall R : nat -> nat -> Prop,
    kwf_R R (kfold (opr p) 0 3) -> R (Z.to_nat (2 ^ p + 2)) P.
  Proof.
    intros R [_ Hc].
    specialize (Hc eq_refl rot3 rot3' rot3_perm (fun q => match q with 0 => P | _ => 1 end)).
    destruct rounded_fold_values as [E1 E2].
    simpl k_body in Hc. cbv beta in Hc. rewrite E1 in Hc.
    rewrite E2 in Hc. exact Hc.
  Qed.

  (* the congruence route collapses: associativity up to R alone        *)
  (* relates 2^p and 2^p + 2                                             *)
  Theorem assoc_congruence_collapses : forall R : nat -> nat -> Prop,
    (forall x y z, R (opr p (opr p x y) z) (opr p x (opr p y z))) ->
    R P (Z.to_nat (2 ^ p + 2)).
  Proof.
    intros R Ha. pose proof pow2_ge4. specialize (Ha P 1 1).
    rewrite !opr_P_1 in Ha.
    replace (opr p 1 1) with 2 in Ha by (rewrite opr_small by (simpl; lia); reflexivity).
    rewrite opr_comm, opr_2_P in Ha. exact Ha.
  Qed.

  (* arity 2: data (2^p + 1, 1), the first operand not representable *)
  Theorem rounded_fold2_not_exact :
    fold_k (opr p) 0 2 (fun q => match q with 0 => Z.to_nat (2 ^ p + 1) | _ => 1 end) = P /\
    fold_k (opr p) 0 2 (fun q => match swap01 q with 0 => Z.to_nat (2 ^ p + 1) | _ => 1 end)
    = Z.to_nat (2 ^ p + 2).
  Proof.
    pose proof pow2_ge4. unfold fold_k, tab. cbn [seq map fold_left swap01].
    rewrite opr_P1_tie, opr_P_1. split; [reflexivity |].
    replace (opr p 0 1) with 1 by (rewrite opr_small by (simpl; lia); reflexivity).
    exact opr_1_P1.
  Qed.
End RoundedSums.

(* arity 2, representable data: the rounded fold is EXACTLY order-       *)
(* independent, because fadd commutes; so 3 is the least arity at which  *)
(* reordering a rounded reduce of representable data changes the result  *)
Theorem rounded_fold2_exact : forall p v,
  (forall q, q < 2 -> rne p (Z.of_nat (v q)) = Z.of_nat (v q)) ->
  fold_k (opr p) 0 2 (fun q => v (swap01 q)) = fold_k (opr p) 0 2 v.
Proof.
  intros p v Hrep. unfold fold_k, tab. cbn [seq map fold_left swap01].
  assert (H0 : forall x, rne p (Z.of_nat x) = Z.of_nat x -> opr p 0 x = x).
  { intros x Hx. unfold opr, fadd. simpl Z.of_nat. rewrite Z.add_0_l, Hx. apply Nat2Z.id. }
  rewrite (H0 (v 1) (Hrep 1 ltac:(lia))), (H0 (v 0) (Hrep 0 ltac:(lia))).
  apply opr_comm.
Qed.

(* p = 2, with the congruence: every R that makes fadd a congruence and  *)
(* associative relates 4 and 24 (4 ~ 6 ~ 8 ~ 12 ~ 16 ~ 24)               *)
Theorem assoc_congruence_collapses_p2 : forall R : nat -> nat -> Prop,
  (forall x y z, R x y -> R y z -> R x z) ->
  (forall x, R x x) ->
  (forall x x' y y', R x x' -> R y y' -> R (opr 2 x y) (opr 2 x' y')) ->
  (forall x y z, R (opr 2 (opr 2 x y) z) (opr 2 x (opr 2 y z))) ->
  R 4 24.
Proof.
  intros R Htr Hr Hc Ha.
  assert (A46 : R 4 6) by (specialize (Ha 4 1 1); vm_compute in Ha; exact Ha).
  assert (A68 : R 6 8) by (specialize (Hc 4 6 2 2 A46 (Hr 2)); vm_compute in Hc; exact Hc).
  assert (A812 : R 8 12) by (specialize (Hc 4 6 4 6 A46 A46); vm_compute in Hc; exact Hc).
  assert (A1216 : R 12 16) by (specialize (Hc 4 6 8 12 A46 A812); vm_compute in Hc; exact Hc).
  assert (A1624 : R 16 24) by (specialize (Hc 8 12 8 12 A812 A812); vm_compute in Hc; exact Hc).
  apply Htr with 6; [exact A46 |]. apply Htr with 8; [exact A68 |].
  apply Htr with 12; [exact A812 |]. apply Htr with 16; [exact A1216 | exact A1624].
Qed.

(* ===================================================================== *)
(* PART D.  PINS.                                                        *)
(* ===================================================================== *)

(* the mod-2 kernel at the swap: the values differ (1 vs 3), the        *)
(* parities agree                                                        *)
Example pin_mod2_swap :
  f3 (fun p => swap01 p) = 1 /\ f3 (fun p => p) = 3 /\ Rmod2 1 3.
Proof. vm_compute. split; [reflexivity | split; reflexivity]. Qed.

(* the rounded fold at p = 2, data (4, 1, 1): 4 in position order, 6   *)
(* rotated                                                               *)
Example pin_rounded_fold :
  fold_k (opr 2) 0 3 (fun q => match q with 0 => 4 | _ => 1 end) = 4 /\
  fold_k (opr 2) 0 3 (fun q => match rot3 q with 0 => 4 | _ => 1 end) = 6.
Proof. vm_compute. split; reflexivity. Qed.
