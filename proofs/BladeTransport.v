(* ===================================================================== *)
(* BladeTransport.v -- FUNCTORIALITY OF THE QUOTIENT through every       *)
(* combinator, and the orbit work of a composed computation.             *)
(* (docs/plans/plan-one-model-surface-calculus.md, item 3)               *)
(*                                                                       *)
(* In the one model a Shape IS the group data: its records are the       *)
(* blocks of the Young action, and enumShape s is the quotient of the    *)
(* shape's dense box by that action (BladeObject.enumShape_is_quotient). *)
(* So the group of a computation is the shape its typing derivation      *)
(* assigns (BladeSurface.out_shape, and fst (den rho e)); this file      *)
(* states how that shape -- the group -- is transported through each     *)
(* combinator (formalism 10.2-10.5), and what a composed computation     *)
(* costs.                                                                *)
(*                                                                       *)
(* Part A, the outer product <*> (method_for(xs1 ++ xs2)):               *)
(*   rle_app                  the identity groups of a concatenated      *)
(*                            binding are the two encodings glued at the *)
(*                            SEAM (gmerge)                              *)
(*   out_shape_app,           the typed shape is the factors' groups     *)
(*   out_shape_app_clean      glued at the seam; their concatenation     *)
(*                            when the boundary names differ             *)
(*   dense_shape_app          the dense shape always splits              *)
(*   block_perm_app           the orbit relation of s1 ++ s2 is the      *)
(*                            PRODUCT of the two: a direct product group *)
(*   shapeCard_product_clean  orbit count of a product = product of the  *)
(*                            counts, when the seam is clean             *)
(*   shapeCard_product_seam   in general the glued group is LARGER, so   *)
(*                            the count never exceeds the product:       *)
(*                            C(n+m1+m2-1, m1+m2) <= C(n+m1-1, m1) *     *)
(*                            C(n+m2-1, m2) (seam_merge_pin: 15 vs 25)   *)
(*   method_product_transport the same, as a law about typed terms       *)
(* Part B, zip:                                                          *)
(*   zip_transport            the group of a co-iteration is the         *)
(*                            operands' own (common) group: shapeCard    *)
(*                            independent of arity; its canonical read   *)
(*                            is the diagonal of the operands' reads.    *)
(*                            The pullback is trivial: T_Zip refuses     *)
(*                            operands of different shapes               *)
(* Part C, pipe >>@:                                                     *)
(*   pipe_transport,          the group of the first stage; qread        *)
(*   pipe_typed_transport     commutes with every elementwise stage      *)
(*   pipe_object_prog         same questions, mapped answers: no new     *)
(*                            traversal step (one new kernel application *)
(*                            per orbit, which Part E counts)            *)
(* Part D, a small pair layer for <&> and <&!> (not in BladeSurface):    *)
(*   par_group_product        sigma(C1 <&> C2) = sigma(C1) x sigma(C2):  *)
(*                            shapes, product orbit relation, product    *)
(*                            count, canonical read of the pair          *)
(*   fuse_meet                both fused outputs carry ONE shape, the    *)
(*                            out_shape of the conjunction of licences   *)
(*                            (necessary: meet_necessary_pin)            *)
(*   fuse_is_par              fusion computes what the two separate      *)
(*                            applications compute, cell by cell         *)
(*   fusion_one_traversal,    the fused object program visits each orbit *)
(*   fuse_object_prog         representative of the meet ONCE; for a     *)
(*                            typed fusion its run is den2's pair.  With *)
(*                            agreeing licences the unfused pair takes   *)
(*                            twice the traversal steps                  *)
(*   fusion_traversals_       the meet is one of the two traversals      *)
(*     never_cost                                                        *)
(*   fusion_evaluations       but fusion NEVER SAVES kernel evaluations, *)
(*                            and COSTS some when the licences disagree: *)
(*                            a comm kernel fused with a non-comm one is *)
(*                            evaluated on the dense traversal           *)
(*                            (fusion_mismatch_pin: 50 vs 40)            *)
(*   pair_sound               the pair layer steps (step2) to two typed  *)
(*                            array terms carrying its denotation        *)
(* Part E, the orbit work of a pipeline (W = sum_v omega(G_v, X_v)):     *)
(*   enum_out_in_dense,       every stored cell is a dense cell; the     *)
(*   quotient_never_costs     quotient never costs                       *)
(*   orbit_work               a pipeline of stages (kernel + operands    *)
(*                            that are top-level arrays or EARLIER       *)
(*                            stages), each stage's shape by out_shape;  *)
(*                            W = sum of the stages' shapeCards          *)
(*   pipe_prog_work,          ATTAINMENT: the composed object program    *)
(*   pipeline_attains_        (each stage's object program in turn, each *)
(*     orbit_work             stage materialized once, read back from    *)
(*                            storage) asks exactly W questions of the   *)
(*                            kernel-value oracle, and every stored      *)
(*                            stage is veval of its denotation           *)
(*   stage_env_is_surface     an Env-only stage is BladeSurface's        *)
(*                            method_for application over rho            *)
(*   two_node_pipeline_work   for Out(i,j) = g(h(A i), h(A j)), g comm:  *)
(*                            W = n + C(n+1, 2)                          *)
(*   two_node_bound_is_       ... the same NUMBER as BladeOrbitWork's    *)
(*     orbit_work             free-model class count (counts coincide)   *)
(*   two_node_translation     ... and the same PROGRAM: the calculus's   *)
(*                            composed program, queries relabelled to    *)
(*                            Q1 0 / Q2 1 and the triangle read back by  *)
(*                            qread, is a free-model program correct for *)
(*                            every oracle in the law class asking       *)
(*                            exactly W; the free-model lower bound      *)
(*                            applies to it, and it attains it           *)
(* Part F, pins (vm_compute): three_stage_orbit_work (35 at n = 5; 55    *)
(* without the licence), three_stage_run (the program, run: 35           *)
(* questions), fused_work_pin (15 vs 30), seam_merge_pin.                *)
(* Part G: group_of, the group a derivation assigns as a function on the *)
(* syntax of expr; group_of_den (it is the typed shape and the           *)
(* denotation's shape) and group_of_laws (the combinators' equations).   *)
(* Part H: stage_is_surface -- EVERY stage, Prev sources included, is    *)
(* on the nose the surface term method_for(names) <@> k over the local   *)
(* environment of its operands; with kwf and arity (pipe_ok) it is       *)
(* typed, group_of reads off its shape (orbit_work's summand), and it is *)
(* the quotient of its dense stage; pipe_ok_stages_typed.                *)
(* Part I, the scope pins: d1_not_built_pin (16 vs 10),                  *)
(* typed_work_not_optimal_pin (12 vs 9), fusion_mismatch_pin (50 vs 40), *)
(* meet_necessary_pin.                                                   *)
(*                                                                       *)
(* Scope, stated once.  Compound identity groups (8.4 at s > 1), and     *)
(* groups of symmetric operands, are NOT quotiented: BladeSurface's      *)
(* out_shape quotients only groups of rank-1 operands and leaves every   *)
(* other group dense.  Decision D1 (flattening to one record over the    *)
(* product extent) is not built here, so orbit_work counts such a stage  *)
(* densely (d1_not_built_pin: 16 where D1 gives C(5, 2) = 10).  Values   *)
(* are nat (D2).  FOLDS ARE OUT: sharing partial sums across fibres is   *)
(* Ensemble Computation, NP-complete (BladeOrbitWork's header); a        *)
(* pipeline here is a DAG of maps, and <&!>'s reduction-join form        *)
(* (10.2) is a fold.  The LOWER bound for pipelines is BladeOrbitWork's  *)
(* orbit_work_lower_bound in the free model (unary and binary symbols,   *)
(* commutativity the only law); this file proves the attaining side for *)
(* every pipeline of the stage language and bridges to the free model,  *)
(* program and all, on the two-node schema only.  A general schema-to-  *)
(* free-model translation would give  free lower bound <= orbit_work,    *)
(* with equality only when each comm stage's typed group is its whole    *)
(* licence (rank-1 operands, adjacent repeats): typed_work_not_optimal_  *)
(* pin has 12 against 9.  The stage language is a separate, untyped      *)
(* layer: attainment and correctness hold for every list of stages,      *)
(* relative to den_pipe (a forward or dangling Prev j reads dflt in      *)
(* both), and hold even for a comm-flagged kernel whose body is not      *)
(* symmetric; what kwf buys is Part H (each stage a typed surface term   *)
(* and the quotient of its dense stage, stage by stage).  The pair layer *)
(* is an extension above BladeSurface (Par, Fuse with a one-step step2), *)
(* not a change to its expr, and its meet is all-or-nothing: a meet of   *)
(* partial licences (comm on different position subsets) cannot be      *)
(* expressed, kernels carrying one comm flag.  TWO COST METRICS: Parts C *)
(* and D count tuple visits of a composite oracle (traversal steps; a    *)
(* fused question answers both kernels), Part E counts kernel            *)
(* applications (the free model's metric, so an elementwise stage costs  *)
(* one application per orbit there).                                     *)
(*                                                                       *)
(* Imports the tower through BladeSurface.  Coq 8.18 / Rocq 9, stdlib    *)
(* only.                                                                 *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF BladeShape BladeTrinity BladeTrinityAsym
                          BladeBinomial BladeCompute BladeMonad BladeMixedRadix
                          BladeLowering BladeCompleteness BladeOptimal
                          BladeOrbitWork BladeDeduceExact BladeObject BladeSurface.
Require Import List Arith Lia Permutation Bool.
Import ListNotations.

(* ===================================================================== *)
(* PART A.  THE OUTER PRODUCT <*> : shape concatenation, with the seam.  *)
(* method_for(xs1) <*> method_for(xs2) = method_for(xs1 ++ xs2) (10.2).  *)
(* ===================================================================== *)

(* the run-length step: rle ((x, s) :: b) = push (x, s) (rle b) *)
Definition push (p : nat * Shape) (gs : list grp) : list grp :=
  match gs with
  | g :: gs' =>
      if Nat.eq_dec (fst p) (g_name g)
      then if shape_eq_dec (snd p) (g_shape g)
           then mkG (fst p) (snd p) (S (g_mult g)) :: gs'
           else mkG (fst p) (snd p) 1 :: g :: gs'
      else mkG (fst p) (snd p) 1 :: g :: gs'
  | [] => [mkG (fst p) (snd p) 1]
  end.

Lemma rle_cons : forall x s b, rle ((x, s) :: b) = push (x, s) (rle b).
Proof. intros x s b. simpl. destruct (rle b); reflexivity. Qed.

(* two groups that may be glued: same name and same shape *)
Definition same_grp (g h : grp) : bool :=
  if Nat.eq_dec (g_name g) (g_name h)
  then if shape_eq_dec (g_shape g) (g_shape h) then true else false
  else false.

(* gluing two run-length encodings: the last group of the first meets   *)
(* the first group of the second at the SEAM                             *)
Fixpoint gmerge (gs1 gs2 : list grp) : list grp :=
  match gs1 with
  | [] => gs2
  | g :: gs1' =>
      match gs1' with
      | [] => match gs2 with
              | h :: hs => if same_grp g h
                           then mkG (g_name g) (g_shape g) (g_mult g + g_mult h) :: hs
                           else g :: h :: hs
              | [] => [g]
              end
      | _ => g :: gmerge gs1' gs2
      end
  end.

Lemma push_gmerge : forall p G H,
  G <> [] -> push p (gmerge G H) = gmerge (push p G) H.
Proof.
  intros [x s] G H HG. destruct G as [|g G']; [contradiction |].
  destruct G' as [|g2 G''].
  - (* G = [g] *)
    cbn [push fst snd].
    destruct (Nat.eq_dec x (g_name g)) as [Ex | Nx];
      [destruct (shape_eq_dec s (g_shape g)) as [Esx | Nsx] |];
      destruct H as [|h hs]; cbn [gmerge push fst snd]; try reflexivity;
      unfold same_grp, push; cbn [fst snd g_name g_shape g_mult];
      repeat match goal with
             | |- context [Nat.eq_dec ?a ?b] =>
                 destruct (Nat.eq_dec a b); cbn [g_name g_shape g_mult] in *; try congruence
             | |- context [shape_eq_dec ?a ?b] =>
                 destruct (shape_eq_dec a b); cbn [g_name g_shape g_mult] in *; try congruence
             end; try reflexivity.
  - (* G = g :: g2 :: G'' *)
    cbn [push fst snd].
    change (gmerge (g :: g2 :: G'') H) with (g :: gmerge (g2 :: G'') H).
    cbn [push fst snd].
    destruct (Nat.eq_dec x (g_name g)); [destruct (shape_eq_dec s (g_shape g)) |];
      reflexivity.
Qed.

Lemma rle_nil : forall b, rle b = [] -> b = [].
Proof.
  intros [|[x s] b] H; [reflexivity |]. rewrite rle_cons in H.
  unfold push in H. cbn [fst snd] in H. destruct (rle b) as [|g gs]; [discriminate |].
  destruct (Nat.eq_dec x (g_name g)); [destruct (shape_eq_dec s (g_shape g)) |];
    discriminate.
Qed.

Lemma push_nonnil : forall p gs, push p gs <> [].
Proof.
  intros [x s] [|g gs]; cbn [push fst snd]; [discriminate |].
  destruct (Nat.eq_dec x (g_name g)); [destruct (shape_eq_dec s (g_shape g)) |];
    discriminate.
Qed.

(* THE SEAM LAW: the identity groups of a concatenated binding are the  *)
(* two encodings glued at the seam                                       *)
Theorem rle_app : forall b1 b2, rle (b1 ++ b2) = gmerge (rle b1) (rle b2).
Proof.
  induction b1 as [|[x s] b1 IH]; intro b2; [reflexivity |].
  cbn [app]. rewrite !rle_cons, IH.
  destruct b1 as [|p b1'].
  - cbn [rle]. destruct (rle b2) as [|h hs]; [reflexivity |].
    unfold push, gmerge, same_grp; cbn [fst snd g_name g_shape g_mult].
    destruct (Nat.eq_dec x (g_name h)); [destruct (shape_eq_dec s (g_shape h)) |];
      reflexivity.
  - apply push_gmerge. intro E. apply rle_nil in E. discriminate.
Qed.

(* ---------------- the clean seam ---------------- *)

Definition last_name (b : list (nat * Shape)) : option nat := hd_error (rev (map fst b)).
Definition first_name (b : list (nat * Shape)) : option nat := hd_error (map fst b).

(* no identity group straddles the seam: the boundary names differ *)
Definition seam_clean (b1 b2 : list (nat * Shape)) : Prop :=
  forall x y, last_name b1 = Some x -> first_name b2 = Some y -> x <> y.

Lemma rle_hd : forall y s b, exists g gs, rle ((y, s) :: b) = g :: gs /\ g_name g = y.
Proof.
  intros y s b. rewrite rle_cons. unfold push. cbn [fst snd].
  destruct (rle b) as [|g gs].
  - exists (mkG y s 1), []. split; reflexivity.
  - destruct (Nat.eq_dec y (g_name g)); [destruct (shape_eq_dec s (g_shape g)) |];
      eexists; eexists; split; reflexivity.
Qed.

Lemma push_app : forall p G H, G <> [] -> push p (G ++ H) = push p G ++ H.
Proof.
  intros [x s] [|g G] H HG; [contradiction |]. cbn [app push fst snd].
  destruct (Nat.eq_dec x (g_name g)); [destruct (shape_eq_dec s (g_shape g)) |];
    reflexivity.
Qed.

Lemma last_name_cons : forall p b, b <> [] -> last_name (p :: b) = last_name b.
Proof.
  intros p b Hb. unfold last_name. cbn [map rev].
  destruct (rev (map fst b)) as [|z zs] eqn:E.
  - exfalso. apply Hb. destruct b as [|q b']; [reflexivity |].
    cbn [map rev] in E. destruct (rev (map fst b')); discriminate.
  - reflexivity.
Qed.

Theorem rle_app_clean : forall b1 b2,
  seam_clean b1 b2 -> rle (b1 ++ b2) = rle b1 ++ rle b2.
Proof.
  induction b1 as [|[x s] b1 IH]; intros b2 Hc; [reflexivity |].
  cbn [app]. rewrite !rle_cons.
  destruct b1 as [|p b1'].
  - cbn [app rle]. destruct b2 as [|[y s'] b2'].
    + cbn [rle push fst snd]. reflexivity.
    + destruct (rle_hd y s' b2') as (g & gs & E & Hn). rewrite E.
      assert (Nxy : x <> y) by (apply Hc; reflexivity).
      cbn [push fst snd app].
      destruct (Nat.eq_dec x (g_name g)) as [Ex | _]; [congruence | reflexivity].
  - rewrite IH.
    + apply push_app. intro E. apply rle_nil in E. discriminate.
    + intros a c Ha Hb. apply (Hc a c); [| exact Hb].
      rewrite last_name_cons by discriminate. exact Ha.
Qed.

(* the dense shape of a product always splits *)
Theorem dense_shape_app : forall b1 b2,
  dense_shape (b1 ++ b2) = dense_shape b1 ++ dense_shape b2.
Proof. intros. unfold dense_shape. rewrite map_app, concat_app. reflexivity. Qed.

(* <*> IS SHAPE CONCATENATION: the output structure of a product of     *)
(* loops is the concatenation of the factors' output structures when no *)
(* identity group straddles the seam ...                                 *)
Theorem out_shape_app_clean : forall comm b1 b2,
  seam_clean b1 b2 -> out_shape comm (b1 ++ b2) = out_shape comm b1 ++ out_shape comm b2.
Proof.
  intros comm b1 b2 Hc. unfold out_shape. rewrite rle_app_clean by exact Hc.
  rewrite map_app, concat_app. reflexivity.
Qed.

(* ... and in general the factors' groups glued at the seam *)
Theorem out_shape_app : forall comm b1 b2,
  out_shape comm (b1 ++ b2) = concat (map (group_shape comm) (gmerge (rle b1) (rle b2))).
Proof. intros. unfold out_shape. rewrite rle_app. reflexivity. Qed.

(* without a claim the seam is invisible *)
Corollary out_shape_app_nocomm : forall b1 b2,
  out_shape false (b1 ++ b2) = out_shape false b1 ++ out_shape false b2.
Proof. intros. rewrite !comm_license_needed. apply dense_shape_app. Qed.

(* ---------------- the product of groups ---------------- *)

Lemma shapeCard_app : forall s1 s2, shapeCard (s1 ++ s2) = shapeCard s1 * shapeCard s2.
Proof. intros. unfold shapeCard. rewrite map_app. apply fold_mul_app. Qed.

(* the orbit relation of a concatenated shape is the PRODUCT of the two  *)
(* orbit relations: the Young group of s1 ++ s2 is the direct product    *)
Theorem block_perm_app : forall s1 s2 t1 t2 t1' t2',
  length t1 = swidth s1 -> length t1' = swidth s1 ->
  (block_perm (s1 ++ s2) (t1 ++ t2) (t1' ++ t2')
   <-> block_perm s1 t1 t1' /\ block_perm s2 t2 t2').
Proof.
  induction s1 as [|c s1 IH]; intros s2 t1 t2 t1' t2' L1 L1'.
  - unfold swidth in L1, L1'. simpl in L1, L1'.
    destruct t1; [| discriminate]. destruct t1'; [| discriminate].
    simpl. split; [intro H; split; [reflexivity | exact H] | intros [_ H]; exact H].
  - unfold swidth in L1, L1'. simpl in L1, L1'. cbn [app block_perm].
    rewrite !firstn_app, !skipn_app.
    replace (ix_r c - length t1) with 0 by lia.
    replace (ix_r c - length t1') with 0 by lia.
    rewrite !firstn_O, !app_nil_r.
    rewrite IH by (rewrite skipn_length; unfold swidth; lia).
    tauto.
Qed.

(* the orbit count of a product of groups is the product of the counts  *)
Theorem shapeCard_product_clean : forall comm b1 b2,
  seam_clean b1 b2 ->
  shapeCard (out_shape comm (b1 ++ b2))
  = shapeCard (out_shape comm b1) * shapeCard (out_shape comm b2).
Proof. intros. rewrite out_shape_app_clean by assumption. apply shapeCard_app. Qed.

(* ---------------- the seam as an inequality ---------------- *)

Lemma nondecr_cons_intro : forall a t,
  Forall (fun x => a <= x) t -> nondecr t -> nondecr (a :: t).
Proof.
  intros a [|b t] HF Hn; simpl; [exact I |].
  inversion HF; subst. split; [assumption | exact Hn].
Qed.

Lemma in_firstn' : forall (A : Type) n (l : list A) x, In x (firstn n l) -> In x l.
Proof.
  intros A n l x H. rewrite <- (firstn_skipn n l). apply in_or_app. left. exact H.
Qed.

Lemma in_skipn' : forall (A : Type) n (l : list A) x, In x (skipn n l) -> In x l.
Proof.
  intros A n l x H. rewrite <- (firstn_skipn n l). apply in_or_app. right. exact H.
Qed.

Lemma nondecr_firstn : forall t n, nondecr t -> nondecr (firstn n t).
Proof.
  induction t as [|a t IH]; intros n H; destruct n as [|n]; simpl; try exact I.
  apply nondecr_cons_intro.
  - pose proof (nondecr_lb a t H) as HF. rewrite Forall_forall in *.
    intros x Hx. apply HF. exact (in_firstn' _ _ _ _ Hx).
  - apply IH. exact (nondecr_tail a t H).
Qed.

Lemma nondecr_skipn : forall t n, nondecr t -> nondecr (skipn n t).
Proof.
  induction t as [|a t IH]; intros n H; destruct n as [|n]; simpl; try exact I;
    [exact H | apply IH; exact (nondecr_tail a t H)].
Qed.

(* one sorted block of m1 + m2 splits into two sorted blocks: the       *)
(* multisets of size m1 + m2 inject into pairs of multisets              *)
Lemma enum_split_incl : forall m1 m2 l u t,
  In t (enum (m1 + m2) l u) -> In t (enumShape [mkIx m1 l u; mkIx m2 l u]).
Proof.
  intros m1 m2 l u t Ht. apply enum_sound in Ht.
  apply canonical_iff_nondecr in Ht as [[Hl HF] Hn].
  change [mkIx m1 l u; mkIx m2 l u] with ([mkIx m1 l u] ++ [mkIx m2 l u]).
  apply enumShape_app. exists (firstn m1 t), (skipn m1 t).
  split; [symmetry; apply firstn_skipn |].
  rewrite !enumShape_single_eq. unfold enumIx. cbn [ix_r ix_l ix_u].
  split; apply enum_complete; apply canonical_iff_nondecr; split.
  - split; [rewrite firstn_length; lia |].
    rewrite Forall_forall in *. intros x Hx. apply HF. exact (in_firstn' _ _ _ _ Hx).
  - apply nondecr_firstn. exact Hn.
  - split; [rewrite skipn_length; lia |].
    rewrite Forall_forall in *. intros x Hx. apply HF. exact (in_skipn' _ _ _ _ Hx).
  - apply nondecr_skipn. exact Hn.
Qed.

Lemma groupCard_merge_le : forall m1 m2 l u,
  groupCard (mkIx (m1 + m2) l u) <= groupCard (mkIx m1 l u) * groupCard (mkIx m2 l u).
Proof.
  intros m1 m2 l u.
  rewrite groupCard_length.
  replace (groupCard (mkIx m1 l u) * groupCard (mkIx m2 l u))
    with (shapeCard [mkIx m1 l u; mkIx m2 l u])
    by (unfold shapeCard; simpl; lia).
  rewrite shapeCard_is_length.
  apply NoDup_incl_length; [apply enum_NoDup |].
  intros t Ht. apply enum_split_incl. exact Ht.
Qed.

Lemma gmerge_snoc : forall G g h H,
  gmerge (G ++ [g]) (h :: H) = G ++ gmerge [g] (h :: H).
Proof.
  induction G as [|a G IH]; intros g h H; [reflexivity |].
  cbn [app]. destruct G as [|b G'].
  - reflexivity.
  - change (gmerge (a :: (b :: G') ++ [g]) (h :: H))
      with (a :: gmerge ((b :: G') ++ [g]) (h :: H)).
    rewrite IH. reflexivity.
Qed.

Lemma shapeCard_concat_map : forall (A : Type) (f : A -> Shape) (l : list A),
  shapeCard (concat (map f l)) = fold_right Nat.mul 1 (map (fun a => shapeCard (f a)) l).
Proof.
  intros A f l. induction l as [|a l IH]; [reflexivity |].
  cbn [map concat fold_right]. rewrite shapeCard_app, IH. reflexivity.
Qed.

Lemma dshape_merge : forall g h,
  g_shape g = g_shape h ->
  dshape (mkG (g_name g) (g_shape g) (g_mult g + g_mult h)) = dshape g ++ dshape h.
Proof.
  intros g h E. unfold dshape. cbn [g_shape g_mult].
  rewrite repeat_app, concat_app, E. reflexivity.
Qed.

Lemma merge_card_le : forall comm g h,
  same_grp g h = true ->
  shapeCard (group_shape comm (mkG (g_name g) (g_shape g) (g_mult g + g_mult h)))
  <= shapeCard (group_shape comm g) * shapeCard (group_shape comm h).
Proof.
  intros comm g h Hs. unfold same_grp in Hs.
  destruct (Nat.eq_dec (g_name g) (g_name h)) as [_ | ]; [| discriminate].
  destruct (shape_eq_dec (g_shape g) (g_shape h)) as [Esh | ]; [| discriminate].
  unfold group_shape, grouped. cbn [g_shape g_mult].
  rewrite <- Esh.
  destruct (comm && match g_shape g with [c] => Nat.eqb (ix_r c) 1 | _ => false end) eqn:Eg.
  - destruct (g_shape g) as [|c [|c' s']]; [| | rewrite andb_false_r in Eg; discriminate].
    + rewrite andb_false_r in Eg. discriminate.
    + unfold shapeCard. cbn [map fold_right]. rewrite !Nat.mul_1_r.
      apply groupCard_merge_le.
  - fold (dshape g). replace (dshape h) with (dshape (mkG (g_name h) (g_shape g) (g_mult h)))
      by (unfold dshape; cbn [g_shape g_mult]; rewrite Esh; reflexivity).
    unfold dshape. cbn [g_shape g_mult].
    rewrite repeat_app, concat_app, shapeCard_app. lia.
Qed.

Lemma gmerge_nil_r : forall G, gmerge G [] = G.
Proof.
  induction G as [|g G IH]; [reflexivity |].
  destruct G as [|g2 G']; [reflexivity |].
  change (gmerge (g :: g2 :: G') []) with (g :: gmerge (g2 :: G') []).
  rewrite IH. reflexivity.
Qed.

(* THE PRODUCT LAW WITH ITS SEAM: the orbit count of method_for(xs1 ++  *)
(* xs2) never exceeds the product of the factors' orbit counts (equal   *)
(* when the seam is clean, shapeCard_product_clean).  Gluing the seam   *)
(* group can only enlarge the group: C(n+m1+m2-1, m1+m2) <=              *)
(* C(n+m1-1, m1) * C(n+m2-1, m2).                                        *)
Theorem shapeCard_product_seam : forall comm b1 b2,
  shapeCard (out_shape comm (b1 ++ b2))
  <= shapeCard (out_shape comm b1) * shapeCard (out_shape comm b2).
Proof.
  intros comm b1 b2. rewrite out_shape_app. unfold out_shape.
  destruct (rle b2) as [|h H] eqn:E2.
  - rewrite gmerge_nil_r. cbn [map concat]. change (shapeCard []) with 1. lia.
  - destruct (rle b1) as [|g0 G0] eqn:E1.
    + cbn [gmerge map concat]. change (shapeCard []) with 1. lia.
    + destruct (exists_last (l := g0 :: G0) ltac:(discriminate)) as (G & g & EG).
      rewrite EG, gmerge_snoc.
      rewrite map_app, concat_app, !shapeCard_app.
      rewrite !shapeCard_concat_map. cbn [gmerge].
      destruct (same_grp g h) eqn:Es; cbn [map fold_right].
      * rewrite map_app, fold_mul_app. cbn [map fold_right].
        pose proof (merge_card_le comm g h Es) as Hle.
        set (a := fold_right Nat.mul 1 (map (fun a0 => shapeCard (group_shape comm a0)) G)).
        set (z := fold_right Nat.mul 1 (map (fun a0 => shapeCard (group_shape comm a0)) H)).
        set (m := shapeCard (group_shape comm (mkG (g_name g) (g_shape g) (g_mult g + g_mult h)))) in *.
        set (cg := shapeCard (group_shape comm g)) in *.
        set (ch := shapeCard (group_shape comm h)) in *.
        rewrite Nat.mul_1_r.
        replace (a * cg * (ch * z)) with (a * ((cg * ch) * z)) by ring.
        apply Nat.mul_le_mono_l. apply Nat.mul_le_mono_r. exact Hle.
      * rewrite map_app, fold_mul_app. cbn [map fold_right]. nia.
Qed.

(* ---------------- typed: the denotation's shape ---------------- *)

(* a deferred computation denotes an array of exactly its typed shape *)
Lemma den_shape_comp : forall G rho c s,
  env_ok G rho -> has_ty G c (TComp s) -> fst (den rho c) = s.
Proof.
  intros G rho c s Hok Ht.
  destruct (eval_sound G rho (ECompute c) s Hok (T_Compute G c s Ht)) as (k & _ & Hd).
  cbn [den] in Hd. rewrite Hd. reflexivity.
Qed.

Lemma bind_names_app : forall G xs1 xs2 b,
  bind_names G (xs1 ++ xs2) = Some b ->
  exists b1 b2, bind_names G xs1 = Some b1 /\ bind_names G xs2 = Some b2 /\ b = b1 ++ b2.
Proof.
  intros G xs1. induction xs1 as [|y xs1 IH]; intros xs2 b Hb.
  - exists [], b. split; [reflexivity | split; [exact Hb | reflexivity]].
  - cbn [app bind_names] in Hb.
    destruct (lookup y G) as [s|] eqn:Ey; [| discriminate].
    destruct (bind_names G (xs1 ++ xs2)) as [b'|] eqn:Eb; [| discriminate].
    injection Hb as <-.
    destruct (IH xs2 b' Eb) as (b1 & b2 & H1 & H2 & ->).
    exists ((y, s) :: b1), b2. cbn [bind_names]. rewrite Ey, H1.
    split; [reflexivity | split; [exact H2 | reflexivity]].
Qed.

(* <*> AS A TYPED LAW.  The typed output of method_for(xs1 ++ xs2) <@> f *)
(* is the factors' identity groups glued at the seam; it is the          *)
(* concatenation of the factors' out_shapes under f's licence when the   *)
(* seam is clean (then its orbit relation is the product,                *)
(* block_perm_app, and its orbit count the product of counts); and its   *)
(* orbit count never exceeds the product.  (A factor method_for(xs1) <@> *)
(* f is itself ill-typed -- f's arity is the whole binding's -- so the   *)
(* factors enter through out_shape, not through typed terms.)            *)
Theorem method_product_transport : forall G rho xs1 xs2 f s,
  env_ok G rho ->
  has_ty G (EAppM (EMethod (xs1 ++ xs2)) f) (TComp s) ->
  exists b1 b2,
    bind_names G xs1 = Some b1 /\ bind_names G xs2 = Some b2 /\
    fst (den rho (EAppM (EMethod (xs1 ++ xs2)) f)) = s /\
    s = concat (map (group_shape (k_comm f)) (gmerge (rle b1) (rle b2))) /\
    shapeCard s <= shapeCard (out_shape (k_comm f) b1) * shapeCard (out_shape (k_comm f) b2) /\
    (seam_clean b1 b2 ->
       s = out_shape (k_comm f) b1 ++ out_shape (k_comm f) b2 /\
       shapeCard s = shapeCard (out_shape (k_comm f) b1) * shapeCard (out_shape (k_comm f) b2)).
Proof.
  intros G rho xs1 xs2 f s Hok Ht.
  pose proof (den_shape_comp G rho _ s Hok Ht) as Hd.
  destruct (ty_appM_method _ _ _ _ Ht) as (b & Hb & _ & _ & ->).
  destruct (bind_names_app G xs1 xs2 b Hb) as (b1 & b2 & H1 & H2 & ->).
  exists b1, b2. split; [exact H1 |]. split; [exact H2 |]. split; [exact Hd |].
  split; [apply out_shape_app |]. split; [apply shapeCard_product_seam |].
  intro Hc. split; [apply out_shape_app_clean; exact Hc | apply shapeCard_product_clean; exact Hc].
Qed.

(* ===================================================================== *)
(* PART B.  ZIP: the group of a co-iteration is the operands' own group, *)
(* whatever the arity.  The pullback along the diagonal is TRIVIAL here: *)
(* T_Zip demands one common shape, so a zip of a symmetric operand with  *)
(* a dense one is refused by the typing rule, not intersected.           *)
(* ===================================================================== *)

Lemma bind_names_names : forall G xs b, bind_names G xs = Some b -> map fst b = xs.
Proof.
  intros G xs. induction xs as [|y xs IH]; intros b Hb; cbn [bind_names] in Hb.
  - injection Hb as <-. reflexivity.
  - destruct (lookup y G); [| discriminate].
    destruct (bind_names G xs) as [b'|] eqn:Eb; [| discriminate].
    injection Hb as <-. cbn [map fst]. rewrite (IH b' eq_refl). reflexivity.
Qed.

Theorem zip_transport : forall G rho xs f s,
  env_ok G rho ->
  has_ty G (EAppM (EZip xs) f) (TComp s) ->
  fst (den rho (EAppM (EZip xs) f)) = s /\
  (forall x, In x xs -> fst (rd rho x) = s) /\
  (forall x, In x xs -> shapeCard (fst (den rho (EAppM (EZip xs) f))) = shapeCard (fst (rd rho x))) /\
  (forall t, qread (den rho (EAppM (EZip xs) f)) t
             = k_body f (fun p => nth p (map (fun x => qread (rd rho x) t) xs) 0)).
Proof.
  intros G rho xs f s Hok Ht.
  pose proof (den_shape_comp G rho _ s Hok Ht) as Hd.
  destruct (ty_appM_zip _ _ _ _ Ht) as (b & Hb & Hne & Hall & _ & _).
  assert (Hop : forall x, In x xs -> fst (rd rho x) = s).
  { intros x Hx. rewrite <- (bind_names_names G xs b Hb) in Hx.
    apply in_map_iff in Hx as ([y s'] & <- & Hin). cbn [fst].
    rewrite (binding_shapes G rho xs b Hok Hb y s' Hin).
    unfold all_shape in Hall. rewrite Forall_forall in Hall.
    exact (Hall (y, s') Hin). }
  split; [exact Hd |]. split; [exact Hop |].
  split; [intros x Hx; rewrite Hd, (Hop x Hx); reflexivity |].
  intro t. unfold qread at 1. rewrite Hd.
  cbn [den]. rewrite (bind_arrs_as_of G rho xs b Hok Hb). cbn [snd].
  unfold apply_zip, zvals, as_of. rewrite map_map. cbn [snd fst].
  assert (EL : map (fun x => snd (rd rho (fst x)) (scanon s t)) b
               = map (fun x => qread (rd rho x) t) xs).
  { rewrite <- (bind_names_names G xs b Hb), map_map.
    apply map_ext_in. intros [y s'] Hin. cbn [fst].
    unfold qread. rewrite (Hop y); [reflexivity |].
    rewrite <- (bind_names_names G xs b Hb). apply in_map_iff. exists (y, s'). auto. }
  rewrite EL. reflexivity.
Qed.

(* ===================================================================== *)
(* PART C.  PIPE >>@ : the group of the first stage, and the canonical   *)
(* read commutes with every elementwise stage.                           *)
(* ===================================================================== *)

Lemma pipe_typed_inv : forall G e g t, has_ty G (EPipe e g) t -> has_ty G e t.
Proof. intros G e g t H. inversion H; subst; assumption. Qed.

Theorem pipe_transport : forall rho e g,
  fst (den rho (EPipe e g)) = fst (den rho e) /\
  (forall t, qread (den rho (EPipe e g)) t = g (qread (den rho e) t)) /\
  veval (den rho (EPipe e g)) = map g (veval (den rho e)).
Proof.
  intros rho e g. split; [reflexivity |]. split; [reflexivity |].
  cbn [den]. apply compose_apply_duality.
Qed.

(* typed: a pipe has the type, hence the group, of its first stage *)
Corollary pipe_typed_transport : forall G rho e g s,
  env_ok G rho -> has_ty G (EPipe e g) (TComp s) ->
  has_ty G e (TComp s) /\ fst (den rho (EPipe e g)) = s /\ fst (den rho e) = s.
Proof.
  intros G rho e g s Hok Ht. pose proof (pipe_typed_inv G e g _ Ht) as He.
  split; [exact He |]. split; [| exact (den_shape_comp G rho e s Hok He)].
  rewrite (proj1 (pipe_transport rho e g)). exact (den_shape_comp G rho e s Hok He).
Qed.

(* the object's program for a pipe asks the same questions and maps the *)
(* answers: an elementwise stage adds no TRAVERSAL STEP (questions here  *)
(* are tuple visits of a composite oracle).  It does add one KERNEL      *)
(* APPLICATION per orbit, which Part E's orbit_work counts (the free     *)
(* model's metric): the kq stage of three_stage_orbit_work is 15 of 35.  *)
Theorem pipe_object_prog : forall (s : Shape) (k : list nat -> nat) (g : nat -> nat),
  run (object_prog s) (fun t => g (k t)) = map g (run (object_prog s) k) /\
  trace (object_prog s) (fun t => g (k t)) = trace (object_prog s) k.
Proof.
  intros s k g. rewrite !object_prog_run, !object_prog_trace. split; [| reflexivity].
  unfold veval. cbn [fst snd]. rewrite map_map. reflexivity.
Qed.

(* ===================================================================== *)
(* PART D.  PARALLEL <&> AND FUSION <&!> : a small pair layer.           *)
(* The calculus of BladeSurface has no pairs; this extension adds them   *)
(* above it.  Par c1 c2 is c1 <&> c2 for two deferred computations;      *)
(* Fuse xs f1 f2 is (method_for(xs) <@> f1) <&!> (method_for(xs) <@> f2) *)
(* -- full fusion is restricted to computations from the SAME loop       *)
(* (10.2), so the syntax carries one name list.  Its output structure is *)
(* the MEET of the two licences: the traversal is quotiented only if     *)
(* both kernels license it.                                              *)
(* ===================================================================== *)

Inductive comp2 : Type :=
| Par  : expr -> expr -> comp2
| Fuse : list nat -> kernel -> kernel -> comp2.

Inductive ty2 : Type :=
| TPair : Shape -> Shape -> ty2.

Inductive has_ty2 (G : tenv) : comp2 -> ty2 -> Prop :=
| T_Par : forall c1 c2 s1 s2,
    has_ty G c1 (TComp s1) -> has_ty G c2 (TComp s2) ->
    has_ty2 G (Par c1 c2) (TPair s1 s2)
| T_Fuse : forall xs b f1 f2,
    bind_names G xs = Some b -> kwf f1 -> kwf f2 ->
    k_arity f1 = length b -> k_arity f2 = length b ->
    has_ty2 G (Fuse xs f1 f2)
      (TPair (out_shape (k_comm f1 && k_comm f2) b) (out_shape (k_comm f1 && k_comm f2) b)).

Definition den2 (rho : venv) (c : comp2) : Arr nat * Arr nat :=
  match c with
  | Par c1 c2 => (den rho c1, den rho c2)
  | Fuse xs f1 f2 =>
      match bind_arrs rho xs with
      | Some l => ((out_shape (k_comm f1 && k_comm f2) (shapes_of l), apply_kernel f1 l),
                   (out_shape (k_comm f1 && k_comm f2) (shapes_of l), apply_kernel f2 l))
      | None => (dflt, dflt)
      end
  end.

(* the flattened pair: one array over the concatenated shape *)
Definition par_flat (A B : Arr nat) : Arr (nat * nat) :=
  (fst A ++ fst B,
   fun t => (snd A (firstn (swidth (fst A)) t), snd B (skipn (swidth (fst A)) t))).

Lemma scanon_length_w : forall s t, length t = swidth s -> length (scanon s t) = swidth s.
Proof.
  induction s as [|c s IH]; intros t Hl; [reflexivity |].
  unfold swidth in *. cbn [scanon fold_right] in *.
  rewrite app_length, isort_length, firstn_length, IH.
  - unfold swidth. lia.
  - rewrite skipn_length. unfold swidth. lia.
Qed.

(* sigma(C1 <&> C2) = sigma(C1) x sigma(C2) (10.5).  The pair's shapes   *)
(* are the two typed shapes; flattened, its orbit relation is the        *)
(* product of the two (block_perm_app), its orbit count the product, and *)
(* its canonicalizing read is the pair of the two canonical reads.       *)
Theorem par_group_product : forall G rho c1 c2 s1 s2,
  env_ok G rho -> has_ty2 G (Par c1 c2) (TPair s1 s2) ->
  fst (fst (den2 rho (Par c1 c2))) = s1 /\ fst (snd (den2 rho (Par c1 c2))) = s2 /\
  fst (par_flat (den rho c1) (den rho c2)) = s1 ++ s2 /\
  shapeCard (s1 ++ s2) = shapeCard s1 * shapeCard s2 /\
  (forall t1 t2 t1' t2', length t1 = swidth s1 -> length t1' = swidth s1 ->
     (block_perm (s1 ++ s2) (t1 ++ t2) (t1' ++ t2')
      <-> block_perm s1 t1 t1' /\ block_perm s2 t2 t2')) /\
  (forall t1 t2, length t1 = swidth s1 ->
     qread (par_flat (den rho c1) (den rho c2)) (t1 ++ t2)
     = (qread (den rho c1) t1, qread (den rho c2) t2)).
Proof.
  intros G rho c1 c2 s1 s2 Hok Ht.
  inversion Ht as [? ? ? ? H1 H2 | ]; subst.
  pose proof (den_shape_comp G rho c1 s1 Hok H1) as E1.
  pose proof (den_shape_comp G rho c2 s2 Hok H2) as E2.
  cbn [den2 fst snd]. split; [exact E1 |]. split; [exact E2 |].
  split; [unfold par_flat; cbn [fst]; rewrite E1, E2; reflexivity |].
  split; [apply shapeCard_app |]. split; [intros; apply block_perm_app; assumption |].
  intros t1 t2 L1. unfold qread, par_flat. cbn [fst snd].
  rewrite E1, E2. rewrite scanon_app by (symmetry; exact L1).
  pose proof (scanon_length_w s1 t1 L1) as L.
  rewrite (firstn_app_exact _ _ _ _ L), (skipn_app_exact _ _ _ _ L). reflexivity.
Qed.

(* ---------------- fusion: the meet, correctness, one traversal ---------------- *)

(* a kernel may be read on any traversal at least as fine as its licence *)
Lemma apply_kernel_canon_meet : forall c f ro b t,
  kwf f -> k_arity f = length b ->
  (forall x s, In (x, s) b -> fst (ro x) = s) ->
  In t (enumShape (dense_shape b)) ->
  (c = true -> k_comm f = true) ->
  apply_kernel f (as_of ro b) (scanon (out_shape c b) t) = apply_kernel f (as_of ro b) t.
Proof.
  intros c f ro b t Hwf Har Hro Ht Hc. destruct c.
  - rewrite <- (Hc eq_refl). exact (proj2 (apply_kernel_canon f ro b t Hwf Har Hro Ht)).
  - rewrite comm_license_needed, scanon_fixes by exact Ht. reflexivity.
Qed.

(* THE MEET.  Both outputs of a fusion carry ONE shape, the out_shape of *)
(* the conjunction of the licences: when the kernels agree it is each    *)
(* one's own typed shape; when one is not commutative the shared         *)
(* traversal is the dense one.                                           *)
Theorem fuse_meet : forall G xs f1 f2 s1 s2,
  has_ty2 G (Fuse xs f1 f2) (TPair s1 s2) ->
  exists b, bind_names G xs = Some b /\ s1 = s2 /\
    s1 = out_shape (k_comm f1 && k_comm f2) b /\
    (k_comm f1 = k_comm f2 -> s1 = out_shape (k_comm f1) b /\ s2 = out_shape (k_comm f2) b) /\
    (k_comm f1 = false \/ k_comm f2 = false -> s1 = dense_shape b).
Proof.
  intros G xs f1 f2 s1 s2 Ht. inversion Ht as [| ? b ? ? Hb Hw1 Hw2 Ha1 Ha2]; subst.
  exists b. split; [exact Hb |]. split; [reflexivity |]. split; [reflexivity |].
  split.
  - intro E. rewrite E, andb_diag. split; reflexivity.
  - intros [E | E]; rewrite E; [| rewrite andb_false_r]; apply comm_license_needed.
Qed.

(* FUSION IS PARALLEL APPLICATION.  At every cell of the dense output,   *)
(* the canonical reads of the two fused outputs are the canonical reads  *)
(* of the two separate applications method_for(xs) <@> f_i.             *)
Theorem fuse_is_par : forall G rho xs f1 f2 s1 s2,
  env_ok G rho -> has_ty2 G (Fuse xs f1 f2) (TPair s1 s2) ->
  exists b, bind_names G xs = Some b /\
  fst (fst (den2 rho (Fuse xs f1 f2))) = s1 /\ fst (snd (den2 rho (Fuse xs f1 f2))) = s2 /\
  forall t, In t (enumShape (dense_shape b)) ->
    qread (fst (den2 rho (Fuse xs f1 f2))) t = qread (den rho (EAppM (EMethod xs) f1)) t /\
    qread (snd (den2 rho (Fuse xs f1 f2))) t = qread (den rho (EAppM (EMethod xs) f2)) t.
Proof.
  intros G rho xs f1 f2 s1 s2 Hok Ht.
  inversion Ht as [| ? b ? ? Hb Hw1 Hw2 Ha1 Ha2]; subst.
  exists b. split; [exact Hb |].
  pose proof (bind_arrs_as_of G rho xs b Hok Hb) as Hl.
  pose proof (binding_shapes G rho xs b Hok Hb) as Hsh.
  cbn [den2 den]. rewrite Hl, (shapes_of_as_of _ _ Hsh). cbn [fst snd].
  split; [reflexivity |]. split; [reflexivity |].
  intros t Hdt. unfold qread. cbn [fst snd].
  split.
  - rewrite (apply_kernel_canon_meet _ f1 (rd rho) b t Hw1 Ha1 Hsh Hdt)
      by (intro E; apply andb_true_iff in E; exact (proj1 E)).
    symmetry. exact (proj2 (apply_kernel_canon f1 (rd rho) b t Hw1 Ha1 Hsh Hdt)).
  - rewrite (apply_kernel_canon_meet _ f2 (rd rho) b t Hw2 Ha2 Hsh Hdt)
      by (intro E; apply andb_true_iff in E; exact (proj2 E)).
    symmetry. exact (proj2 (apply_kernel_canon f2 (rd rho) b t Hw2 Ha2 Hsh Hdt)).
Qed.

(* ---------------- the work of fusion ---------------- *)

(* sequencing oracle programs *)
Fixpoint pbind {Q A O1 O2 : Type} (P : prog Q A O1) (K : O1 -> prog Q A O2)
  : prog Q A O2 :=
  match P with
  | Ret o => K o
  | Ask q c => Ask q (fun a => pbind (c a) K)
  end.

Lemma pbind_run : forall (Q A O1 O2 : Type) (P : prog Q A O1) (K : O1 -> prog Q A O2) k,
  run (pbind P K) k = run (K (run P k)) k.
Proof. intros Q A O1 O2 P K k. induction P as [o | q c IH]; [reflexivity | apply IH]. Qed.

Lemma pbind_trace : forall (Q A O1 O2 : Type) (P : prog Q A O1) (K : O1 -> prog Q A O2) k,
  trace (pbind P K) k = trace P k ++ trace (K (run P k)) k.
Proof.
  intros Q A O1 O2 P K k. induction P as [o | q c IH]; [reflexivity |].
  cbn [pbind trace run]. rewrite IH. reflexivity.
Qed.

(* the unfused pair: two traversals, the oracle told which kernel        *)
Definition par_prog (s1 s2 : Shape) : prog (bool * list nat) nat (list nat * list nat) :=
  pbind (ask_all (map (pair false) (enumShape s1)) [])
        (fun r1 => pbind (ask_all (map (pair true) (enumShape s2)) [])
                         (fun r2 => Ret (r1, r2))).

Definition par_oracle (k1 k2 : list nat -> nat) (q : bool * list nat) : nat :=
  if fst q then k2 (snd q) else k1 (snd q).

(* the fused pair: ONE traversal, both kernels answered at each tuple   *)
Definition fused_prog (s : Shape) : prog (list nat) (nat * nat) (list (nat * nat)) :=
  object_prog s.

Definition fused_oracle (k1 k2 : list nat -> nat) (t : list nat) : nat * nat := (k1 t, k2 t).

Theorem par_prog_spec : forall s1 s2 k1 k2,
  run (par_prog s1 s2) (par_oracle k1 k2) = (veval (s1, k1), veval (s2, k2)) /\
  length (trace (par_prog s1 s2) (par_oracle k1 k2)) = shapeCard s1 + shapeCard s2.
Proof.
  intros s1 s2 k1 k2. unfold par_prog. split.
  - rewrite pbind_run, pbind_run. cbn [run]. rewrite !ask_all_run. cbn [rev app].
    unfold veval. cbn [fst snd]. rewrite !map_map. reflexivity.
  - rewrite pbind_trace, pbind_trace. cbn [trace]. rewrite !ask_all_trace, app_nil_r.
    rewrite app_length, !map_length, !shapeCard_is_length. reflexivity.
Qed.

(* FUSION DISTRIBUTES, AS A PERFORMANCE GUARANTEE: the fused object's     *)
(* program computes both outputs and visits each orbit representative    *)
(* ONCE -- shapeCard s traversal steps, where the unfused pair over the  *)
(* same shape takes 2 * shapeCard s.  The 2x baseline is the unfused     *)
(* cost only when both kernels' own traversals ARE s, i.e. when the      *)
(* licences agree (fuse_object_prog); with a comm kernel and a non-comm  *)
(* one the meet is dense and fusion costs evaluations                    *)
(* (fusion_mismatch_pin: 50 vs 40).                                      *)
Theorem fusion_one_traversal : forall s k1 k2,
  run (fused_prog s) (fused_oracle k1 k2)
    = combine (veval (s, k1)) (veval (s, k2)) /\
  map fst (run (fused_prog s) (fused_oracle k1 k2)) = veval (s, k1) /\
  map snd (run (fused_prog s) (fused_oracle k1 k2)) = veval (s, k2) /\
  length (trace (fused_prog s) (fused_oracle k1 k2)) = shapeCard s /\
  length (trace (par_prog s s) (par_oracle k1 k2)) = 2 * shapeCard s.
Proof.
  intros s k1 k2. unfold fused_prog. rewrite object_prog_run.
  assert (Ec : forall L : list (list nat),
            map (fused_oracle k1 k2) L = combine (map k1 L) (map k2 L)).
  { induction L as [|t L IH]; [reflexivity | cbn [map combine]; rewrite IH; reflexivity]. }
  unfold veval. cbn [fst snd]. rewrite Ec.
  split; [reflexivity |].
  split; [rewrite <- Ec, map_map; reflexivity |].
  split; [rewrite <- Ec, map_map; reflexivity |].
  split; [apply object_work |].
  rewrite (proj2 (par_prog_spec s s k1 k2)). lia.
Qed.

(* the meet never costs TRAVERSALS: the fused traversal is never longer  *)
(* than the two separate ones together (it is one of them).  It can cost *)
(* KERNEL EVALUATIONS: see fusion_evaluations and fusion_mismatch_pin.   *)
Theorem fusion_traversals_never_cost : forall c1 c2 b,
  shapeCard (out_shape (c1 && c2) b) <= shapeCard (out_shape c1 b) + shapeCard (out_shape c2 b).
Proof. intros [|] [|] b; cbn [andb]; lia. Qed.

(* the fused object program of a TYPED fusion: its oracle is the two     *)
(* kernels on the binding, its run is the pair of den2's materialized    *)
(* outputs, its work shapeCard of the meet.  The unfused baseline is the *)
(* pair of the two OWN traversals; it equals 2 * shapeCard of the meet   *)
(* only when the licences agree.                                         *)
Theorem fuse_object_prog : forall G rho xs f1 f2 s1 s2,
  env_ok G rho -> has_ty2 G (Fuse xs f1 f2) (TPair s1 s2) ->
  exists b, bind_names G xs = Some b /\
    s1 = out_shape (k_comm f1 && k_comm f2) b /\
    run (fused_prog s1) (fused_oracle (apply_kernel f1 (as_of (rd rho) b))
                                      (apply_kernel f2 (as_of (rd rho) b)))
      = combine (veval (fst (den2 rho (Fuse xs f1 f2))))
                (veval (snd (den2 rho (Fuse xs f1 f2)))) /\
    length (trace (fused_prog s1) (fused_oracle (apply_kernel f1 (as_of (rd rho) b))
                                                (apply_kernel f2 (as_of (rd rho) b))))
      = shapeCard s1 /\
    length (trace (par_prog (out_shape (k_comm f1) b) (out_shape (k_comm f2) b))
                  (par_oracle (apply_kernel f1 (as_of (rd rho) b))
                              (apply_kernel f2 (as_of (rd rho) b))))
      = shapeCard (out_shape (k_comm f1) b) + shapeCard (out_shape (k_comm f2) b) /\
    (k_comm f1 = k_comm f2 ->
       shapeCard (out_shape (k_comm f1) b) + shapeCard (out_shape (k_comm f2) b)
       = 2 * shapeCard s1).
Proof.
  intros G rho xs f1 f2 s1 s2 Hok Ht.
  inversion Ht as [| ? b ? ? Hb Hw1 Hw2 Ha1 Ha2]; subst.
  exists b. split; [exact Hb |]. split; [reflexivity |].
  pose proof (bind_arrs_as_of G rho xs b Hok Hb) as Hl.
  pose proof (shapes_of_as_of _ _ (binding_shapes G rho xs b Hok Hb)) as Hsh.
  destruct (fusion_one_traversal (out_shape (k_comm f1 && k_comm f2) b)
              (apply_kernel f1 (as_of (rd rho) b)) (apply_kernel f2 (as_of (rd rho) b)))
    as (Hrun & _ & _ & Hw & _).
  split; [| split; [exact Hw |]].
  - rewrite Hrun. cbn [den2]. rewrite Hl, Hsh. reflexivity.
  - split; [apply par_prog_spec |].
    intro E. rewrite E, andb_diag. lia.
Qed.

(* ---------------- reduction of the pair layer ---------------- *)

(* a parallel pair hands each side to compute; a fused pair runs ONE    *)
(* traversal producing both arrays                                       *)
Inductive step2 (rho : venv) : comp2 -> expr * expr -> Prop :=
| S2_Par : forall c1 c2, step2 rho (Par c1 c2) (ECompute c1, ECompute c2)
| S2_Fuse : forall xs f1 f2 l,
    bind_arrs rho xs = Some l ->
    step2 rho (Fuse xs f1 f2)
      (EArr (out_shape (k_comm f1 && k_comm f2) (shapes_of l)) (apply_kernel f1 l),
       EArr (out_shape (k_comm f1 && k_comm f2) (shapes_of l)) (apply_kernel f2 l)).

(* progress, preservation and denotation for the pair layer: a typed    *)
(* pair steps to two well-typed array terms whose denotations are the    *)
(* pair's, and each then evaluates by the calculus (eval_sound)          *)
Theorem pair_sound : forall G rho c s1 s2,
  env_ok G rho -> has_ty2 G c (TPair s1 s2) ->
  exists e1 e2, step2 rho c (e1, e2) /\
    has_ty G e1 (TArr s1) /\ has_ty G e2 (TArr s2) /\
    den rho e1 = fst (den2 rho c) /\ den rho e2 = snd (den2 rho c) /\
    exists k1 k2, star rho e1 (EArr s1 k1) /\ star rho e2 (EArr s2 k2).
Proof.
  intros G rho c s1 s2 Hok Ht.
  assert (Hev : forall e1 e2, has_ty G e1 (TArr s1) -> has_ty G e2 (TArr s2) ->
            exists k1 k2, star rho e1 (EArr s1 k1) /\ star rho e2 (EArr s2 k2)).
  { intros e1 e2 H1 H2.
    destruct (eval_sound G rho e1 s1 Hok H1) as (k1 & S1 & _).
    destruct (eval_sound G rho e2 s2 Hok H2) as (k2 & S2 & _).
    exists k1, k2. split; assumption. }
  inversion Ht as [c1 c2 ? ? H1 H2 | xs b f1 f2 Hb Hw1 Hw2 Ha1 Ha2]; subst.
  - exists (ECompute c1), (ECompute c2).
    pose proof (T_Compute G c1 s1 H1) as T1. pose proof (T_Compute G c2 s2 H2) as T2.
    split; [constructor |]. split; [exact T1 |]. split; [exact T2 |].
    split; [reflexivity |]. split; [reflexivity |]. exact (Hev _ _ T1 T2).
  - pose proof (bind_arrs_as_of G rho xs b Hok Hb) as Hl.
    pose proof (binding_shapes G rho xs b Hok Hb) as Hsh.
    set (l := as_of (rd rho) b) in *.
    assert (Es : shapes_of l = b) by exact (shapes_of_as_of _ _ Hsh).
    exists (EArr (out_shape (k_comm f1 && k_comm f2) (shapes_of l)) (apply_kernel f1 l)),
           (EArr (out_shape (k_comm f1 && k_comm f2) (shapes_of l)) (apply_kernel f2 l)).
    split; [constructor; exact Hl |].
    split; [rewrite Es; constructor |]. split; [rewrite Es; constructor |].
    cbn [den2]. rewrite Hl. cbn [den fst snd].
    split; [reflexivity |]. split; [reflexivity |].
    apply Hev; rewrite Es; constructor.
Qed.

(* ===================================================================== *)
(* PART E.  THE ORBIT WORK OF A COMPOSED COMPUTATION.                    *)
(* A pipeline is a list of stages; each stage is a kernel applied (as    *)
(* the outer product, <@> on method_for) to operands that are top-level  *)
(* arrays or EARLIER stages' outputs.  Its output structure is computed  *)
(* from its operands' shapes by out_shape, exactly as the surface rule   *)
(* does; identity is by name, an environment name x and a stage index j  *)
(* being kept apart (2x, 2j+1).  The orbit work is the sum of the        *)
(* stages' orbit counts; the composed object program -- each stage's     *)
(* object program in turn, each stage MATERIALIZED once and read back    *)
(* from storage by later stages -- attains it and is correct.            *)
(* ===================================================================== *)

(* --- the quotient never costs ------------------------------------- *)

Lemma enum_group_in_dshape : forall c g t,
  In t (enumShape (group_shape c g)) -> In t (enumShape (dshape g)).
Proof.
  intros c g t Ht. destruct (grouped c g) eqn:E.
  - destruct (grouped_true c g E) as (_ & d & Hs & Hr).
    rewrite (group_shape_grouped c g d E Hs) in Ht.
    rewrite enumShape_single_eq in Ht. unfold enumIx in Ht. cbn [ix_r ix_l ix_u] in Ht.
    apply enum_sound, canonical_dense in Ht.
    unfold dshape. rewrite Hs, concat_repeat_single.
    apply enumShape_repeat1; [exact Hr | exact Ht].
  - rewrite group_shape_ungrouped in Ht by exact E. exact Ht.
Qed.

(* every cell the typed output stores is a cell of the dense output *)
Theorem enum_out_in_dense : forall c b t,
  In t (enumShape (out_shape c b)) -> In t (enumShape (dense_shape b)).
Proof.
  intros c b. unfold out_shape. rewrite dense_shape_groups.
  induction (rle b) as [|g gs IH]; intros t Ht; [exact Ht |].
  cbn [map concat] in *. apply enumShape_app in Ht as (t1 & t2 & -> & H1 & H2).
  apply enumShape_app. exists t1, t2. split; [reflexivity |].
  split; [exact (enum_group_in_dshape c g t1 H1) | exact (IH t2 H2)].
Qed.

Corollary quotient_never_costs : forall c b,
  shapeCard (out_shape c b) <= shapeCard (dense_shape b).
Proof.
  intros c b. rewrite !shapeCard_is_length.
  apply NoDup_incl_length; [apply enumShape_NoDup |].
  intros t Ht. exact (enum_out_in_dense c b t Ht).
Qed.

(* FUSION NEVER SAVES KERNEL EVALUATIONS, and costs some when the        *)
(* licences disagree.  The fused program evaluates both kernels at each  *)
(* cell of the meet (2 * shapeCard of the meet); the unfused pair        *)
(* evaluates each on its own traversal.  Equal when the licences agree;  *)
(* otherwise the comm kernel is dragged onto the dense traversal.        *)
Theorem fusion_evaluations : forall c1 c2 b,
  shapeCard (out_shape c1 b) + shapeCard (out_shape c2 b)
    <= 2 * shapeCard (out_shape (c1 && c2) b) /\
  (c1 = c2 -> shapeCard (out_shape c1 b) + shapeCard (out_shape c2 b)
              = 2 * shapeCard (out_shape (c1 && c2) b)).
Proof.
  intros c1 c2 b. split.
  - pose proof (quotient_never_costs true b) as Hq.
    rewrite <- comm_license_needed in Hq.
    destruct c1, c2; cbn [andb]; lia.
  - intros ->. rewrite andb_diag. lia.
Qed.

(* --- agreement on the transversal ---------------------------------- *)

Definition agree (A B : Arr nat) : Prop :=
  fst A = fst B /\ forall t, In t (enumShape (fst A)) -> snd A t = snd B t.

Lemma agree_refl : forall A, agree A A.
Proof. intro A. split; [reflexivity | intros; reflexivity]. Qed.

Lemma agree_veval : forall A B, agree A B -> veval A = veval B.
Proof.
  intros A B [Es Ek]. unfold veval. rewrite <- Es. apply map_ext_in. exact Ek.
Qed.

Lemma agree_nth : forall j prev prev',
  Forall2 agree prev prev' -> agree (nth j prev dflt) (nth j prev' dflt).
Proof.
  intros j prev prev' H. revert j. induction H as [|A B l l' HAB H IH]; intro j.
  - destruct j; apply agree_refl.
  - destruct j; [exact HAB | apply IH].
Qed.

Definition op_agree (p p' : nat * Arr nat) : Prop := fst p = fst p' /\ agree (snd p) (snd p').

(* operands that agree give the same kernel arguments at every dense cell *)
Lemma vals_agree : forall l l', Forall2 op_agree l l' ->
  forall t, In t (enumShape (dense_shape (shapes_of l))) -> vals l t = vals l' t.
Proof.
  intros l l' H. induction H as [|[x A] [x' A'] l l' [Ex HA] H IH]; intros t Ht.
  - reflexivity.
  - cbn [shapes_of map dense_shape concat fst snd] in Ht.
    change (concat (map snd (map (fun p => (fst p, fst (snd p))) l)))
      with (dense_shape (shapes_of l)) in Ht.
    apply enumShape_app in Ht as (t1 & t2 & -> & H1 & H2).
    destruct HA as [Es Ek]. cbn [fst snd] in Es, Ek.
    cbn [vals]. rewrite <- Es.
    pose proof (enumShape_tuple_length _ _ H1) as L1.
    rewrite (firstn_app_exact _ _ _ _ L1), (skipn_app_exact _ _ _ _ L1).
    rewrite (Ek t1 H1). f_equal. apply IH. exact H2.
Qed.

Lemma shapes_of_agree : forall l l', Forall2 op_agree l l' -> shapes_of l = shapes_of l'.
Proof.
  intros l l' H. induction H as [|[x A] [x' A'] l l' [Ex [Es _]] H IH]; [reflexivity |].
  cbn [fst snd] in Ex, Es. unfold shapes_of in *. cbn [map fst snd].
  rewrite Ex, Es, IH. reflexivity.
Qed.

(* --- stages ------------------------------------------------------- *)

Inductive src : Type :=
| Env  : nat -> src      (* a top-level array, by name   *)
| Prev : nat -> src.     (* an earlier stage, by index   *)

Record stage : Type := mkStage { st_k : kernel; st_srcs : list src }.

(* identity groups: a name per source, environment names and stage      *)
(* indices kept apart                                                    *)
Definition src_name (o : src) : nat :=
  match o with Env x => 2 * x | Prev j => 2 * j + 1 end.

Definition src_arr (rho : venv) (prev : list (Arr nat)) (o : src) : nat * Arr nat :=
  (src_name o, match o with Env x => rd rho x | Prev j => nth j prev dflt end).

Definition stage_ops (rho : venv) (prev : list (Arr nat)) (st : stage) : list (nat * Arr nat) :=
  map (src_arr rho prev) (st_srcs st).

(* the reference semantics of a stage: the surface rule's denotation *)
Definition stage_den (rho : venv) (prev : list (Arr nat)) (st : stage) : Arr nat :=
  (out_shape (k_comm (st_k st)) (shapes_of (stage_ops rho prev st)),
   apply_kernel (st_k st) (stage_ops rho prev st)).

Fixpoint den_pipe (rho : venv) (sts : list stage) (prev : list (Arr nat)) : list (Arr nat) :=
  match sts with
  | [] => prev
  | st :: sts' => den_pipe rho sts' (prev ++ [stage_den rho prev st])
  end.

(* static shapes, from the operand shapes alone *)
Definition src_shape (esh : nat -> Shape) (psh : list Shape) (o : src) : nat * Shape :=
  (src_name o, match o with Env x => esh x | Prev j => nth j psh [] end).

Definition stage_shape (esh : nat -> Shape) (psh : list Shape) (st : stage) : Shape :=
  out_shape (k_comm (st_k st)) (map (src_shape esh psh) (st_srcs st)).

Fixpoint pipe_shapes (esh : nat -> Shape) (sts : list stage) (psh : list Shape) : list Shape :=
  match sts with
  | [] => psh
  | st :: sts' => pipe_shapes esh sts' (psh ++ [stage_shape esh psh st])
  end.

(* THE ORBIT WORK of a pipeline: the sum of its stages' orbit counts,    *)
(* each node counted over its own support modulo its own group          *)
Definition orbit_work (esh : nat -> Shape) (sts : list stage) : nat :=
  lsum (map shapeCard (pipe_shapes esh sts [])).

Definition rsh (rho : venv) (x : nat) : Shape := fst (rd rho x).

Lemma stage_den_shape : forall rho prev st,
  fst (stage_den rho prev st) = stage_shape (rsh rho) (map fst prev) st.
Proof.
  intros rho prev st. unfold stage_den, stage_shape, stage_ops, shapes_of.
  cbn [fst]. f_equal. rewrite map_map. apply map_ext.
  intros [x | j]; unfold src_arr, src_shape; cbn [fst snd].
  - reflexivity.
  - f_equal. change [] with (fst dflt). rewrite map_nth. reflexivity.
Qed.

Lemma stage_ops_agree : forall rho prev prev' st,
  Forall2 agree prev prev' ->
  Forall2 op_agree (stage_ops rho prev st) (stage_ops rho prev' st).
Proof.
  intros rho prev prev' st H. unfold stage_ops.
  induction (st_srcs st) as [|o os IH]; [constructor |].
  cbn [map]. constructor; [| exact IH].
  destruct o as [x | j]; unfold src_arr, op_agree; cbn [fst snd];
    (split; [reflexivity |]); [apply agree_refl | apply agree_nth; exact H].
Qed.

(* --- the composed object program ---------------------------------- *)

(* a stage materialized: its answers stored in transversal order, read  *)
(* back by position (BladeObject.qread_is_storage)                       *)
Definition mat (s : Shape) (ans : list nat) : Arr nat :=
  (s, fun t => nth (index_of t (enumShape s)) ans 0).

Lemma mat_fst : forall s ans, fst (mat s ans) = s.
Proof. reflexivity. Qed.

(* the kernel-value oracle: a query is (stage index, argument values)   *)
Definition stage_queries (rho : venv) (prev : list (Arr nat)) (st : stage)
  : list (nat * list nat) :=
  map (fun t => (length prev, vals (stage_ops rho prev st) t))
      (enumShape (fst (stage_den rho prev st))).

Lemma stage_queries_length : forall rho prev st,
  length (stage_queries rho prev st) = shapeCard (fst (stage_den rho prev st)).
Proof. intros. unfold stage_queries. rewrite map_length. symmetry. apply shapeCard_is_length. Qed.

Fixpoint pipe_prog (rho : venv) (sts : list stage) (prev : list (Arr nat))
  : prog (nat * list nat) nat (list (Arr nat)) :=
  match sts with
  | [] => Ret prev
  | st :: sts' =>
      pbind (ask_all (stage_queries rho prev st) [])
            (fun ans => pipe_prog rho sts' (prev ++ [mat (fst (stage_den rho prev st)) ans]))
  end.

(* the oracle answers stage i's kernel *)
Definition oracle_ok (K : nat * list nat -> nat) (sts : list stage) (off : nat) : Prop :=
  forall i st vs, nth_error sts i = Some st ->
    K (off + i, vs) = k_body (st_k st) (fun p => nth p vs 0).

Lemma oracle_ok_tail : forall K st sts off,
  oracle_ok K (st :: sts) off -> oracle_ok K sts (S off).
Proof.
  intros K st sts off H i st' vs Hi. replace (S off + i) with (off + S i) by lia.
  apply H. exact Hi.
Qed.

(* ATTAINMENT, the work: the composed program asks the orbit work      *)
Lemma pipe_prog_trace_gen : forall rho sts prev K,
  length (trace (pipe_prog rho sts prev) K) + lsum (map shapeCard (map fst prev))
  = lsum (map shapeCard (pipe_shapes (rsh rho) sts (map fst prev))).
Proof.
  intros rho sts. induction sts as [|st sts IH]; intros prev K; [reflexivity |].
  cbn [pipe_prog pipe_shapes]. rewrite pbind_trace, app_length, ask_all_trace.
  rewrite stage_queries_length.
  match goal with |- context [pipe_prog rho sts (prev ++ [?M])] =>
    pose proof (IH (prev ++ [M]) K) as H end.
  rewrite !map_app in H. cbn [map] in H. rewrite !mat_fst in H.
  rewrite lsum_app in H. cbn [lsum] in H.
  change (@map (Arr nat) Shape fst prev)
    with (@map (prod Shape (list nat -> nat)) Shape fst prev) in H.
  rewrite <- stage_den_shape. lia.
Qed.

Theorem pipe_prog_work : forall rho sts K,
  length (trace (pipe_prog rho sts []) K) = orbit_work (rsh rho) sts.
Proof.
  intros rho sts K. pose proof (pipe_prog_trace_gen rho sts [] K) as H.
  cbn [map lsum] in H. unfold orbit_work. lia.
Qed.

(* ATTAINMENT, correctness: each materialized stage agrees with the     *)
(* reference denotation on its transversal                               *)
Lemma pipe_prog_correct_gen : forall rho sts prev prev' K,
  Forall2 agree prev prev' -> oracle_ok K sts (length prev) ->
  Forall2 agree (run (pipe_prog rho sts prev) K) (den_pipe rho sts prev').
Proof.
  intros rho sts. induction sts as [|st sts IH]; intros prev prev' K Hp Hk;
    [exact Hp |].
  cbn [pipe_prog den_pipe]. rewrite pbind_run.
  apply IH.
  - apply Forall2_app; [exact Hp |]. constructor; [| constructor].
    pose proof (stage_ops_agree rho prev prev' st Hp) as Hops.
    pose proof (shapes_of_agree _ _ Hops) as Hsh.
    unfold agree, mat. cbn [fst snd]. split.
    + unfold stage_den. cbn [fst]. rewrite Hsh. reflexivity.
    + intros t Ht. rewrite ask_all_run. cbn [rev app].
      unfold stage_queries. rewrite map_map.
      rewrite (nth_index_of _ (fun t0 => K (length prev, vals (stage_ops rho prev st) t0))
                 0 t _ Ht).
      pose proof (Hk 0 st (vals (stage_ops rho prev st) t) eq_refl) as E.
      rewrite Nat.add_0_r in E. rewrite E.
      unfold stage_den. cbn [snd]. unfold apply_kernel.
      unfold stage_den in Ht. cbn [fst] in Ht.
      rewrite (vals_agree _ _ Hops t (enum_out_in_dense _ _ t Ht)). reflexivity.
  - rewrite app_length. cbn [length]. rewrite Nat.add_1_r.
    exact (oracle_ok_tail K st sts (length prev) Hk).
Qed.

Lemma Forall2_agree_veval : forall l l',
  Forall2 agree l l' -> map veval l = map veval l'.
Proof.
  intros l l' H. induction H as [|A B l l' HAB H IH]; [reflexivity |].
  cbn [map]. rewrite (agree_veval A B HAB), IH. reflexivity.
Qed.

(* THE ORBIT WORK FORMULA, ATTAINED.  Running each stage's object        *)
(* program in turn, materializing each stage once, asks exactly          *)
(* orbit_work = sum of the stages' orbit counts questions (for every     *)
(* oracle), and against the kernels' own oracle every stage's stored     *)
(* result is veval of its denotation.                                    *)
Theorem pipeline_attains_orbit_work : forall rho sts K,
  oracle_ok K sts 0 ->
  length (trace (pipe_prog rho sts []) K) = orbit_work (rsh rho) sts /\
  length (run (pipe_prog rho sts []) K) = length sts /\
  map veval (run (pipe_prog rho sts []) K) = map veval (den_pipe rho sts []).
Proof.
  intros rho sts K Hk.
  pose proof (pipe_prog_correct_gen rho sts [] [] K (Forall2_nil _) Hk) as H.
  split; [apply pipe_prog_work |].
  split; [| exact (Forall2_agree_veval _ _ H)].
  rewrite (Forall2_length H).
  assert (L : forall sts prev, length (den_pipe rho sts prev) = length prev + length sts).
  { induction sts0 as [|s0 ss IH0]; intro prev; cbn [den_pipe length]; [lia |].
    rewrite IH0, app_length. cbn [length]. lia. }
  rewrite L. reflexivity.
Qed.

(* --- a stage over top-level arrays IS the surface application ------ *)

(* identity is by name only through equality: an injective renaming    *)
(* leaves the identity groups, hence the output structure, unchanged    *)
Definition ren_grp (r : nat -> nat) (g : grp) : grp := mkG (r (g_name g)) (g_shape g) (g_mult g).

Lemma rle_rename : forall (r : nat -> nat), (forall x y, r x = r y -> x = y) ->
  forall b, rle (map (fun p => (r (fst p), snd p)) b) = map (ren_grp r) (rle b).
Proof.
  intros r Hr. induction b as [|[x s] b IH]; [reflexivity |].
  cbn [map fst snd]. rewrite !rle_cons, IH.
  destruct (rle b) as [|g gs]; [reflexivity |].
  cbn [map push fst snd ren_grp g_name g_shape g_mult].
  destruct (Nat.eq_dec (r x) (r (g_name g))) as [E | N];
    destruct (Nat.eq_dec x (g_name g)) as [E' | N'].
  - destruct (shape_eq_dec s (g_shape g)); reflexivity.
  - exfalso. exact (N' (Hr _ _ E)).
  - exfalso. apply N. rewrite E'. reflexivity.
  - reflexivity.
Qed.

Lemma out_shape_rename : forall (r : nat -> nat), (forall x y, r x = r y -> x = y) ->
  forall c b, out_shape c (map (fun p => (r (fst p), snd p)) b) = out_shape c b.
Proof.
  intros r Hr c b. unfold out_shape. rewrite (rle_rename r Hr), map_map.
  f_equal; apply map_ext; intros [n s m]; reflexivity.
Qed.

Lemma vals_rename : forall (r : nat -> nat) (l : list (nat * Arr nat)) t,
  vals (map (fun p => (r (fst p), snd p)) l) t = vals l t.
Proof.
  intros r l. induction l as [|[x A] l IH]; intro t; [reflexivity |].
  cbn [map vals fst snd]. rewrite IH. reflexivity.
Qed.

Theorem stage_env_is_surface : forall G rho prev k xs b,
  env_ok G rho -> bind_names G xs = Some b ->
  fst (stage_den rho prev (mkStage k (map Env xs))) = fst (den rho (EAppM (EMethod xs) k)) /\
  forall t, snd (stage_den rho prev (mkStage k (map Env xs))) t
            = snd (den rho (EAppM (EMethod xs) k)) t.
Proof.
  intros G rho prev k xs b Hok Hb.
  assert (Eops : stage_ops rho prev (mkStage k (map Env xs))
                 = map (fun p => (2 * fst p, snd p)) (as_of (rd rho) b)).
  { unfold stage_ops, as_of. cbn [st_srcs]. rewrite !map_map.
    rewrite <- (bind_names_names G xs b Hb), map_map. reflexivity. }
  assert (Hinj : forall x y, 2 * x = 2 * y -> x = y) by (intros; lia).
  cbn [den]. rewrite (bind_arrs_as_of G rho xs b Hok Hb).
  unfold stage_den. cbn [st_k fst snd]. rewrite Eops. split.
  - unfold shapes_of. rewrite map_map. cbn [fst snd].
    rewrite <- (out_shape_rename (fun x => 2 * x) Hinj (k_comm k)
                 (map (fun p => (fst p, fst (snd p))) (as_of (rd rho) b))).
    rewrite map_map. reflexivity.
  - intro t. unfold apply_kernel. rewrite vals_rename. reflexivity.
Qed.

(* --- the two-node instance: BladeOrbitWork's schema ----------------- *)

(* Out(i, j) = g(h(A i), h(A j)): node h over A, node g over (h, h) *)
Definition two_node_pipeline (fh fg : kernel) : list stage :=
  [mkStage fh [Env 0]; mkStage fg [Prev 0; Prev 0]].

Lemma tr_C_n_1 : forall n, C n 1 = n.
Proof. induction n as [|n IH]; [reflexivity |]. cbn [C]. rewrite C_zero, IH. lia. Qed.

Lemma shapeCard_idx : forall n, shapeCard [mkIx 1 0 n] = n.
Proof.
  intro n. rewrite shapeCard_binom. cbn [map fold_right ix_r ix_l ix_u].
  rewrite Nat.mul_1_r. replace (n - 0 + 1 - 1) with n by lia. apply tr_C_n_1.
Qed.

(* W = omega(1, [n]) + omega(S_2, [n]^2) = n + C(n+1, 2), from the       *)
(* typing rule alone, over any environment where A is over Idx<n>.  h's *)
(* licence is irrelevant: a unary group quotients to itself.             *)
Theorem two_node_pipeline_work_env : forall esh fh fg n,
  esh 0 = [mkIx 1 0 n] -> k_comm fg = true ->
  orbit_work esh (two_node_pipeline fh fg) = n + C (n + 1) 2.
Proof.
  intros esh fh fg n He Hg.
  assert (Hs : pipe_shapes esh (two_node_pipeline fh fg) []
               = [[mkIx 1 0 n];
                  out_shape true [(1, [mkIx 1 0 n]); (1, [mkIx 1 0 n])]]).
  { unfold two_node_pipeline, pipe_shapes, stage_shape. cbn [st_k st_srcs map].
    unfold src_shape. cbv beta iota. rewrite He.
    rewrite Hg. destruct (k_comm fh); reflexivity. }
  unfold orbit_work. rewrite Hs, (proj1 (covariance_shape 1 n)).
  cbn [map lsum]. rewrite shapeCard_idx, (proj2 (covariance_shape 1 n)). lia.
Qed.

Corollary two_node_pipeline_work : forall fh fg n,
  k_comm fg = true ->
  orbit_work (fun _ => [mkIx 1 0 n]) (two_node_pipeline fh fg) = n + C (n + 1) 2.
Proof. intros fh fg n Hg. apply two_node_pipeline_work_env; [reflexivity | exact Hg]. Qed.

(* ... and BladeOrbitWork's free-model class count for the same schema   *)
(* is the same number.  THE TWO COUNTS COINCIDE ON THIS SCHEMA; this     *)
(* corollary relates numbers, not programs (the program-level bridge is  *)
(* two_node_translation below).                                          *)
Corollary two_node_bound_is_orbit_work : forall fh fg n,
  k_comm fg = true ->
  let comm := fun s => s =? 1 in
  orbit_work (fun _ => [mkIx 1 0 n]) (two_node_pipeline fh fg)
    = length (nodup qry_dec (all_classes comm cleaf (kfree comm chash) (targets2 0 1 n))) /\
  (forall P : prog qry nat (list nat),
     (forall k, Kc comm k -> run P k = map (eval cleaf k) (targets2 0 1 n)) ->
     orbit_work (fun _ => [mkIx 1 0 n]) (two_node_pipeline fh fg)
       <= length (trace P (kfree comm chash))).
Proof.
  intros fh fg n Hg comm.
  rewrite (two_node_pipeline_work fh fg n Hg). split.
  - symmetry. apply (two_node_orbit_work comm cleaf chash chash_inj
                       (fun i j E => ltac:(unfold cleaf in E; lia)) 0 1 eq_refl n).
  - intros P Hcor. exact (proj1 (two_node_orbit_work_nat n P Hcor)).
Qed.

(* the attaining side for the same schema, on the calculus, in the      *)
(* calculus's kernel-value oracle model                                  *)
Corollary two_node_attained : forall rho fh fg n K,
  rsh rho 0 = [mkIx 1 0 n] -> k_comm fg = true ->
  oracle_ok K (two_node_pipeline fh fg) 0 ->
  length (trace (pipe_prog rho (two_node_pipeline fh fg) []) K) = n + C (n + 1) 2 /\
  map veval (run (pipe_prog rho (two_node_pipeline fh fg) []) K)
    = map veval (den_pipe rho (two_node_pipeline fh fg) []).
Proof.
  intros rho fh fg n K Hr Hg Hk.
  destruct (pipeline_attains_orbit_work rho _ K Hk) as (Hw & _ & Hc).
  split; [| exact Hc]. rewrite Hw. apply two_node_pipeline_work_env; assumption.
Qed.

(* --- the program-level bridge: the same program, in the free model ---- *)

(* a program's run and trace depend on its stages only through their     *)
(* operands and licences: the kernel bodies live in the oracle           *)
Lemma pipe_prog_shape_only : forall rho sts sts',
  Forall2 (fun st st' => st_srcs st = st_srcs st' /\ k_comm (st_k st) = k_comm (st_k st'))
          sts sts' ->
  forall prev K,
  run (pipe_prog rho sts prev) K = run (pipe_prog rho sts' prev) K /\
  trace (pipe_prog rho sts prev) K = trace (pipe_prog rho sts' prev) K.
Proof.
  intros rho sts sts' H. induction H as [|st st' sts sts' [Hs Hc] H IH]; intros prev K.
  - split; reflexivity.
  - assert (Eo : stage_ops rho prev st = stage_ops rho prev st')
      by (unfold stage_ops; rewrite Hs; reflexivity).
    assert (Ed : fst (stage_den rho prev st) = fst (stage_den rho prev st'))
      by (unfold stage_den; cbn [fst]; rewrite Eo, Hc; reflexivity).
    assert (Eq : stage_queries rho prev st = stage_queries rho prev st')
      by (unfold stage_queries; rewrite Eo, Ed; reflexivity).
    cbn [pipe_prog]. rewrite pbind_run, pbind_run, pbind_trace, pbind_trace, Eq, Ed.
    destruct (IH (prev ++ [mat (fst (stage_den rho prev st'))
                               (run (ask_all (stage_queries rho prev st') []) K)]) K)
      as [IHr IHt].
    split; [exact IHr | rewrite IHt; reflexivity].
Qed.

(* relabel the queries of a program *)
Fixpoint tr_prog {Q Q' O : Type} (tq : Q -> Q') (P : prog Q nat O) : prog Q' nat O :=
  match P with
  | Ret o => Ret o
  | Ask q c => Ask (tq q) (fun a => tr_prog tq (c a))
  end.

Lemma tr_prog_run : forall (Q Q' O : Type) (tq : Q -> Q') (P : prog Q nat O) k,
  run (tr_prog tq P) k = run P (fun q => k (tq q)).
Proof. intros Q Q' O tq P k. induction P as [o | q c IH]; [reflexivity | apply IH]. Qed.

Lemma tr_prog_trace : forall (Q Q' O : Type) (tq : Q -> Q') (P : prog Q nat O) k,
  trace (tr_prog tq P) k = map tq (trace P (fun q => k (tq q))).
Proof.
  intros Q Q' O tq P k. induction P as [o | q c IH]; [reflexivity |].
  cbn [tr_prog trace run map]. rewrite IH. reflexivity.
Qed.

(* the schema's query translation: stage 0 asks symbol 0 (h, unary),    *)
(* stage 1 asks symbol 1 (g, binary) -- BladeOrbitWork's Q1 0 / Q2 1     *)
Definition tq2 (q : nat * list nat) : qry :=
  match fst q with
  | 0 => Q1 0 (nth 0 (snd q) 0)
  | _ => Q2 1 (nth 0 (snd q) 0) (nth 1 (snd q) 0)
  end.

(* A is BladeOrbitWork's data cleaf over Idx<n> *)
Definition rhoA (n : nat) : venv := [(0, ([mkIx 1 0 n], fun t => cleaf (hd 0 t)))].

(* the stages as the calculus states them; the bodies are the oracle's *)
Definition kh0 : kernel := mkK 1 (fun _ => 0) false.
Definition kg0 : kernel := mkK 2 (fun _ => 0) true.

(* read the stored triangle canonically at every dense (i, j) *)
Definition two_node_readout (n : nat) (mats : list (Arr nat)) : list nat :=
  map (fun ij => qread (nth 1 mats dflt) [fst ij; snd ij]) (BladeOrbitWork.dense n).

(* THE CALCULUS'S PROGRAM, as a free-model program *)
Definition two_node_qprog (n : nat) : prog qry nat (list nat) :=
  pbind (tr_prog tq2 (pipe_prog (rhoA n) (two_node_pipeline kh0 kg0) []))
        (fun mats => Ret (two_node_readout n mats)).

Lemma isort2 : forall i j, isort [i; j] = [i; j] \/ isort [i; j] = [j; i].
Proof.
  intros i j. apply Permutation_length_2_inv. apply isort_perm.
Qed.

(* THE BRIDGE.  The composed object program of the calculus, its        *)
(* queries relabelled to BladeOrbitWork's symbols and its stored         *)
(* triangle read back canonically, is a free-model program that is       *)
(* correct for EVERY oracle in the law class and asks exactly the orbit  *)
(* work, n + C(n+1, 2) questions of the free oracle; so the free-model   *)
(* lower bound applies to this very program and it attains it.          *)
Theorem two_node_translation : forall n,
  let comm := fun s => s =? 1 in
  (forall k, Kc comm k -> run (two_node_qprog n) k = map (eval cleaf k) (targets2 0 1 n)) /\
  length (trace (two_node_qprog n) (kfree comm chash))
    = orbit_work (rsh (rhoA n)) (two_node_pipeline kh0 kg0) /\
  orbit_work (rsh (rhoA n)) (two_node_pipeline kh0 kg0) = n + C (n + 1) 2 /\
  (forall P : prog qry nat (list nat),
     (forall k, Kc comm k -> run P k = map (eval cleaf k) (targets2 0 1 n)) ->
     length (trace (two_node_qprog n) (kfree comm chash))
       <= length (trace P (kfree comm chash))).
Proof.
  intro n. cbv zeta.
  assert (Hw : orbit_work (rsh (rhoA n)) (two_node_pipeline kh0 kg0) = n + C (n + 1) 2)
    by (apply two_node_pipeline_work_env; reflexivity).
  assert (Ht : forall k, length (trace (two_node_qprog n) k)
                         = orbit_work (rsh (rhoA n)) (two_node_pipeline kh0 kg0)).
  { intro k. unfold two_node_qprog. rewrite pbind_trace, tr_prog_trace. cbn [trace].
    rewrite app_nil_r, map_length. apply pipe_prog_work. }
  split; [| split; [apply Ht | split; [exact Hw |]]].
  - intros k Hk.
    set (K := fun q => k (tq2 q)).
    set (fh := mkK 1 (fun v => k (Q1 0 (v 0))) false).
    set (fg := mkK 2 (fun v => k (Q2 1 (v 0) (v 1))) true).
    unfold two_node_qprog. rewrite pbind_run, tr_prog_run. cbn [run].
    fold K.
    rewrite (proj1 (pipe_prog_shape_only (rhoA n) (two_node_pipeline kh0 kg0)
                      (two_node_pipeline fh fg)
                      ltac:(repeat constructor) [] K)).
    assert (Hok : oracle_ok K (two_node_pipeline fh fg) 0).
    { intros i st vs Hi. destruct i as [|[|i]]; cbn in Hi;
        [injection Hi as <-; reflexivity | injection Hi as <-; reflexivity
        | destruct i; discriminate]. }
    pose proof (pipe_prog_correct_gen (rhoA n) (two_node_pipeline fh fg) [] [] K
                  (Forall2_nil _) Hok) as Hag.
    set (mats := run (pipe_prog (rhoA n) (two_node_pipeline fh fg) []) K) in *.
    pose proof (agree_nth 1 _ _ Hag) as [Ef Ek].
    set (S0 := stage_den (rhoA n) [] (mkStage fh [Env 0])).
    assert (ES1 : nth 1 (den_pipe (rhoA n) (two_node_pipeline fh fg) []) dflt
                  = stage_den (rhoA n) [S0] (mkStage fg [Prev 0; Prev 0]))
      by reflexivity.
    rewrite ES1 in Ef, Ek.
    assert (Esh : fst (stage_den (rhoA n) [S0] (mkStage fg [Prev 0; Prev 0])) = [mkIx 2 0 n]).
    { unfold stage_den. cbn [fst st_k].
      replace (shapes_of (stage_ops (rhoA n) [S0] (mkStage fg [Prev 0; Prev 0])))
        with [(1, [mkIx 1 0 n]); (1, [mkIx 1 0 n])] by reflexivity.
      exact (proj1 (covariance_shape 1 n)). }
    rewrite Esh in Ef. rewrite Ef in Ek.
    unfold two_node_readout, targets2. rewrite map_map. apply map_ext_in.
    intros [i j] Hij. unfold BladeOrbitWork.dense in Hij.
    apply in_prod_iff in Hij as [Hi Hj]. apply in_seq in Hi. apply in_seq in Hj.
    unfold qread. rewrite Ef. cbn [fst snd scanon ix_r firstn skipn]. rewrite app_nil_r.
    assert (Hin : In (isort [i; j]) (enumShape [mkIx 2 0 n])).
    { pose proof (scanon_in_shape [mkIx 2 0 n] [i; j]) as H.
      cbn [scanon firstn skipn] in H. rewrite app_nil_r in H. apply H.
      cbn [shape_dense ix_r ix_l ix_u firstn skipn]. split; [| reflexivity].
      split; [reflexivity | repeat constructor; lia]. }
    rewrite Ek by exact Hin.
    destruct (isort2 i j) as [E | E]; rewrite E.
    + reflexivity.
    + cbn [eval cell fst snd]. apply Hk. reflexivity.
  - intros P Hcor. rewrite Ht, Hw. exact (proj1 (two_node_orbit_work_nat n P Hcor)).
Qed.


(* ===================================================================== *)
(* PART F.  PINS.                                                        *)
(* ===================================================================== *)

Definition kh : kernel := mkK 1 (fun v => v 0 + 1) false.
Definition kg : kernel := mkK 2 (fun v => v 0 * v 1) true.
Definition kg_dense : kernel := mkK 2 (fun v => v 0 * v 1) false.
Definition kq : kernel := mkK 1 (fun v => 2 * v 0) false.

(* h(A), then g(h, h) commutative, then an elementwise q on g:          *)
(* 5 + C(6, 2) + C(6, 2) = 35 at n = 5; without the comm licence the     *)
(* same pipeline is 5 + 25 + 25 = 55                                      *)
Definition three_stage (g : kernel) : list stage :=
  [mkStage kh [Env 0]; mkStage g [Prev 0; Prev 0]; mkStage kq [Prev 1]].

Example three_stage_orbit_work :
  orbit_work (fun _ => [mkIx 1 0 5]) (three_stage kg) = 35 /\
  orbit_work (fun _ => [mkIx 1 0 5]) (three_stage kg_dense) = 55.
Proof. split; vm_compute; reflexivity. Qed.

Definition rho5 : venv := [(0, ([mkIx 1 0 5], fun t => hd 0 t))].

Definition K3 (q : nat * list nat) : nat :=
  match fst q with
  | 0 => nth 0 (snd q) 0 + 1
  | 1 => nth 0 (snd q) 0 * nth 1 (snd q) 0
  | _ => 2 * nth 0 (snd q) 0
  end.

(* the composed object program, run: 35 questions, and the last stage's *)
(* stored triangle is 2 (i + 1)(j + 1) on i <= j                         *)
Example three_stage_run :
  length (trace (pipe_prog rho5 (three_stage kg) []) K3) = 35 /\
  firstn 6 (veval (nth 2 (run (pipe_prog rho5 (three_stage kg) []) K3) dflt))
    = [2; 4; 6; 8; 10; 8].
Proof. split; vm_compute; reflexivity. Qed.

(* fusion: one traversal of the 15 orbit representatives of             *)
(* SymIdx<2, 5>, against 30 for the unfused pair                         *)
Example fused_work_pin :
  length (trace (fused_prog [mkIx 2 0 5]) (fused_oracle (fun t => hd 0 t) (fun t => length t))) = 15 /\
  length (trace (par_prog [mkIx 2 0 5] [mkIx 2 0 5])
                (par_oracle (fun t => hd 0 t) (fun t => length t))) = 30.
Proof. split; vm_compute; reflexivity. Qed.

(* the seam is real: method_for(A) <*> method_for(A) under a comm kernel *)
(* is NOT the product of the factors' groups -- the glued group S_2 is   *)
(* larger than S_1 x S_1, so 15 orbits where the product has 25          *)
Example seam_merge_pin :
  let bA := [(0, [mkIx 1 0 5])] in
  out_shape true (bA ++ bA) = [mkIx 2 0 5] /\
  shapeCard (out_shape true (bA ++ bA)) = 15 /\
  shapeCard (out_shape true bA) * shapeCard (out_shape true bA) = 25 /\
  dense_shape (bA ++ bA) = dense_shape bA ++ dense_shape bA.
Proof. vm_compute. repeat split. Qed.

(* ===================================================================== *)
(* PART G.  group_of : the group a typing derivation assigns, as a       *)
(* function on the syntax, one equation per constructor.                 *)
(* ===================================================================== *)

Fixpoint group_of (G : tenv) (e : expr) : option Shape :=
  match e with
  | EArr s _ => Some s
  | EVar x => lookup x G
  | EAppM (EMethod xs) f => option_map (out_shape (k_comm f)) (bind_names G xs)
  | EAppM (EZip xs) f =>
      match bind_names G xs with Some ((_, s) :: _) => Some s | _ => None end
  | EAppO (EObject f) xs => option_map (out_shape (k_comm f)) (bind_names G xs)
  | ECompute c => group_of G c
  | EPipe e' _ => group_of G e'
  | _ => None
  end.

Lemma group_of_typed : forall G e t, has_ty G e t ->
  forall s, (t = TComp s \/ t = TArr s) -> group_of G e = Some s.
Proof.
  intros G e t H. induction H; intros s0 Hs;
    destruct Hs as [E | E]; try discriminate; injection E as <-.
  - reflexivity.
  - exact H.
  - destruct (canon_loop G L b H) as [xs ->].
    inversion H; subst. cbn [group_of].
    match goal with [Hb : bind_names G xs = Some b |- _] => rewrite Hb end. reflexivity.
  - destruct (canon_zip G L s n H) as [xs ->].
    inversion H; subst. cbn [group_of].
    match goal with [Hb : bind_names G xs = Some ?b |- _] => rewrite Hb;
      destruct b as [|[y s'] b']; [contradiction |] end.
    match goal with [Ha : all_shape _ ((y, s') :: _) |- _] =>
      unfold all_shape in Ha; inversion Ha as [| ? ? Hh _]; cbn [snd] in Hh; rewrite Hh end.
    reflexivity.
  - rewrite (canon_obj G O f H). cbn [group_of]. rewrite H0. reflexivity.
  - cbn [group_of]. apply IHhas_ty. left. reflexivity.
  - cbn [group_of]. apply IHhas_ty. left. reflexivity.
  - cbn [group_of]. apply IHhas_ty. right. reflexivity.
Qed.

(* group_of IS the typed group, and it is the shape of the denotation:   *)
(* a shape is the group data (enumShape_is_quotient), so this is the     *)
(* group of the computation, read off the derivation                     *)
Theorem group_of_den : forall G rho e s,
  env_ok G rho -> (has_ty G e (TComp s) \/ has_ty G e (TArr s)) ->
  group_of G e = Some s /\ fst (den rho e) = s.
Proof.
  intros G rho e s Hok [Ht | Ht].
  - split; [exact (group_of_typed G e _ Ht s (or_introl eq_refl)) |].
    exact (den_shape_comp G rho e s Hok Ht).
  - split; [exact (group_of_typed G e _ Ht s (or_intror eq_refl)) |].
    destruct (eval_sound G rho e s Hok Ht) as (k & _ & Hd). rewrite Hd. reflexivity.
Qed.

(* the per-constructor laws, in the form the formalism states them,   *)
(* for the combinators.  EArr, EVar and EAppO are definitional (the     *)
(* equations of group_of) and omitted; <&> and <&!> are not expr       *)
(* constructors -- their laws are par_group_product and fuse_meet.      *)
Theorem group_of_laws : forall G xs1 xs2 ys f e g c,
  (* <*> : the factors' groups glued at the seam; concatenation when clean *)
  (forall b1 b2, bind_names G xs1 = Some b1 -> bind_names G xs2 = Some b2 ->
     group_of G (EAppM (EMethod (xs1 ++ xs2)) f)
     = Some (concat (map (group_shape (k_comm f)) (gmerge (rle b1) (rle b2)))) /\
     (seam_clean b1 b2 ->
        group_of G (EAppM (EMethod (xs1 ++ xs2)) f)
        = Some (out_shape (k_comm f) b1 ++ out_shape (k_comm f) b2))) /\
  (* zip : the first (= every, when typed) operand's own group *)
  (forall y s b, bind_names G ys = Some ((y, s) :: b) ->
     group_of G (EAppM (EZip ys) f) = Some s) /\
  (* >>@ : the first stage's group *)
  group_of G (EPipe e g) = group_of G e /\
  (* compute : unchanged *)
  group_of G (ECompute c) = group_of G c.
Proof.
  intros G xs1 xs2 ys f e g c. split; [| split; [| split; reflexivity]].
  - intros b1 b2 H1 H2.
    assert (Hb : bind_names G (xs1 ++ xs2) = Some (b1 ++ b2)).
    { revert b1 H1. induction xs1 as [|x xs1 IH]; intros b1 H1.
      - cbn in H1. injection H1 as <-. exact H2.
      - cbn [app bind_names] in *. destruct (lookup x G); [| discriminate].
        destruct (bind_names G xs1) as [b1'|]; [| discriminate].
        injection H1 as <-. rewrite (IH b1' eq_refl). reflexivity. }
    cbn [group_of]. rewrite Hb. cbn [option_map]. split.
    + rewrite out_shape_app. reflexivity.
    + intro Hc. rewrite out_shape_app_clean by exact Hc. reflexivity.
  - intros y s b H. cbn [group_of]. rewrite H. reflexivity.
Qed.

(* ===================================================================== *)
(* PART H.  EVERY STAGE IS A SURFACE TERM; ITS SHAPE IS ITS group_of.    *)
(* A stage reading earlier stages (Prev j) is not syntactically a        *)
(* BladeSurface term, but it denotes one: bind the stage's operands      *)
(* under their identity names (2x for Env x, 2j+1 for Prev j) as a local *)
(* environment, and the stage is method_for(names) <@> k there.          *)
(* ===================================================================== *)

Lemma src_name_inj : forall o o', src_name o = src_name o' -> o = o'.
Proof.
  intros [x | j] [x' | j'] E; cbn [src_name] in E;
    first [f_equal; lia | exfalso; lia].
Qed.

Lemma lookup_ops : forall rho prev os o, In o os ->
  lookup (src_name o) (map (src_arr rho prev) os) = Some (snd (src_arr rho prev o)).
Proof.
  intros rho prev os. induction os as [|o' os IH]; intros o Hin; [destruct Hin |].
  cbn [map]. unfold src_arr at 1. cbn [lookup].
  destruct (Nat.eqb (src_name o) (src_name o')) eqn:E.
  - apply Nat.eqb_eq, src_name_inj in E. subst o'. reflexivity.
  - destruct Hin as [<- | Hin]; [rewrite Nat.eqb_refl in E; discriminate |].
    apply IH. exact Hin.
Qed.

Lemma bind_arrs_ops : forall rho prev os os',
  incl os' os ->
  bind_arrs (map (src_arr rho prev) os) (map src_name os') = Some (map (src_arr rho prev) os').
Proof.
  intros rho prev os os'. induction os' as [|o os' IH]; intro Hi; [reflexivity |].
  cbn [map bind_arrs]. rewrite (lookup_ops rho prev os o (Hi o (or_introl eq_refl))).
  rewrite IH by (intros y Hy; apply Hi; right; exact Hy). reflexivity.
Qed.

Lemma lookup_shapes : forall x (l : list (nat * Arr nat)),
  lookup x (shapes_of l) = option_map fst (lookup x l).
Proof.
  intros x l. induction l as [|[y A] l IH]; [reflexivity |].
  cbn [shapes_of map lookup fst snd]. destruct (Nat.eqb x y); [reflexivity | exact IH].
Qed.

Lemma bind_names_shapes : forall (rho' : venv) xs l,
  bind_arrs rho' xs = Some l -> bind_names (shapes_of rho') xs = Some (shapes_of l).
Proof.
  intros rho' xs. induction xs as [|x xs IH]; intros l H; cbn [bind_arrs] in H.
  - injection H as <-. reflexivity.
  - destruct (lookup x rho') as [A|] eqn:Ex; [| discriminate].
    destruct (bind_arrs rho' xs) as [l'|] eqn:El; [| discriminate].
    injection H as <-. cbn [bind_names]. rewrite lookup_shapes, Ex, (IH l' eq_refl).
    reflexivity.
Qed.

Lemma env_ok_shapes : forall rho' : venv, env_ok (shapes_of rho') rho'.
Proof.
  intros rho' x s H. rewrite lookup_shapes in H.
  destruct (lookup x rho') as [[s' k]|] eqn:E; cbn in H; [| discriminate].
  injection H as <-. exists k. reflexivity.
Qed.

(* THE STAGE, AS A SURFACE TERM.  Every stage (Env and Prev sources     *)
(* alike) denotes, on the nose, the surface application method_for of   *)
(* its identity names over the local environment of its operands; when  *)
(* its kernel is well formed and of the right arity the term is typed,  *)
(* group_of reads the stage's shape -- the summand of orbit_work -- off  *)
(* the derivation, and the stage is the QUOTIENT of the dense stage      *)
(* (BladeSurface.typed_app_is_quotient).                                 *)
Theorem stage_is_surface : forall rho prev st,
  let rho' := stage_ops rho prev st in
  let e := EAppM (EMethod (map src_name (st_srcs st))) (st_k st) in
  stage_den rho prev st = den rho' e /\
  (kwf (st_k st) -> k_arity (st_k st) = length (st_srcs st) ->
     env_ok (shapes_of rho') rho' /\
     has_ty (shapes_of rho') e (TComp (stage_shape (rsh rho) (map fst prev) st)) /\
     group_of (shapes_of rho') e = Some (stage_shape (rsh rho) (map fst prev) st) /\
     (forall t, In t (enumShape (dense_shape (shapes_of rho'))) ->
        qread (stage_den rho prev st) t = apply_kernel (st_k st) rho' t)).
Proof.
  intros rho prev st rho' e.
  assert (Hb : bind_arrs rho' (map src_name (st_srcs st)) = Some rho').
  { unfold rho', stage_ops. apply bind_arrs_ops. intros y Hy. exact Hy. }
  assert (Hden : stage_den rho prev st = den rho' e).
  { unfold e. cbn [den]. rewrite Hb. reflexivity. }
  split; [exact Hden |].
  intros Hwf Har.
  pose proof (env_ok_shapes rho') as Hok.
  pose proof (bind_names_shapes rho' _ rho' Hb) as Hbn.
  assert (Hty : has_ty (shapes_of rho') e (TComp (fst (stage_den rho prev st)))).
  { unfold e, stage_den. cbn [fst]. fold rho'.
    apply T_AppM with (b := shapes_of rho').
    - constructor. exact Hbn.
    - exact Hwf.
    - unfold shapes_of, rho', stage_ops. rewrite !map_length. exact Har. }
  rewrite stage_den_shape in Hty.
  split; [exact Hok |]. split; [exact Hty |].
  split; [exact (proj1 (group_of_den _ rho' e _ Hok (or_introl Hty))) |].
  intros t Ht.
  destruct (typed_app_is_quotient (shapes_of rho') rho' (map src_name (st_srcs st))
              (st_k st) _ Hok Hty) as (b & Hb' & _ & _ & Hq & _).
  rewrite Hbn in Hb'. injection Hb' as <-.
  destruct (Hq t Ht) as [_ HE]. fold e in HE. rewrite <- Hden in HE.
  rewrite HE. reflexivity.
Qed.

(* the stage language's well-formedness: every kernel well formed and   *)
(* of its operand count.  The ATTAINMENT theorem needs none of it (the  *)
(* program and den_pipe read the same, total, semantics -- a dangling    *)
(* or forward Prev j reads dflt in both); it is what makes every stage   *)
(* a typed surface term and the quotient of its dense stage.             *)
Definition pipe_ok (sts : list stage) : Prop :=
  Forall (fun st => kwf (st_k st) /\ k_arity (st_k st) = length (st_srcs st)) sts.

Corollary pipe_ok_stages_typed : forall rho sts, pipe_ok sts ->
  forall st prev, In st sts ->
    has_ty (shapes_of (stage_ops rho prev st))
           (EAppM (EMethod (map src_name (st_srcs st))) (st_k st))
           (TComp (stage_shape (rsh rho) (map fst prev) st)) /\
    group_of (shapes_of (stage_ops rho prev st))
             (EAppM (EMethod (map src_name (st_srcs st))) (st_k st))
      = Some (stage_shape (rsh rho) (map fst prev) st).
Proof.
  intros rho sts Hp st prev Hin. unfold pipe_ok in Hp. rewrite Forall_forall in Hp.
  destruct (Hp st Hin) as [Hwf Har].
  destruct (proj2 (stage_is_surface rho prev st) Hwf Har) as (_ & Ht & Hg & _).
  split; assumption.
Qed.

(* ===================================================================== *)
(* PART I.  THE SCOPE PINS: where the typed orbit work is NOT optimal,   *)
(* and where fusion costs.                                               *)
(* ===================================================================== *)

(* D1 is not built: method_for(A, A) <@> comm with A over Idx<2> x       *)
(* Idx<2> stays dense, 16 cells, where flattening to one record over the *)
(* product extent (8.4 at s = 2, decision D1) gives SymIdx<2, 4>: 10     *)
Example d1_not_built_pin :
  orbit_work (fun _ => [mkIx 1 0 2; mkIx 1 0 2]) [mkStage kg [Env 0; Env 0]] = 16 /\
  shapeCard [mkIx 2 0 4] = C 5 2 /\ C 5 2 = 10.
Proof. vm_compute. repeat split. Qed.

(* the free-model count of the same schema, with a cheap commutative     *)
(* test oracle (as BladeOrbitWork.two_node_computed)                     *)
Definition ktest (q : qry) : nat :=
  match q with Q1 _ a => 10 + a | Q2 _ a b => 100 + 7 * (a + b) + a * b end.

Definition gg_cell (t : list nat) : tm :=
  App2 1 (App2 1 (Leaf (nth 0 t 0)) (Leaf (nth 1 t 0)))
         (App2 1 (Leaf (nth 2 t 0)) (Leaf (nth 3 t 0))).

Definition gg_targets (n : nat) : list tm :=
  map gg_cell (enumShape [mkIx 1 0 n; mkIx 1 0 n; mkIx 1 0 n; mkIx 1 0 n]).

(* g(g(A i, A j), g(A k, A l)) with g comm: the typed rule quotients the *)
(* inner stage (S_2 on rank-1 operands) but leaves the outer stage dense *)
(* (its operands are SymIdx<2, n>, rank 2): orbit_work 3 + 9 = 12 at     *)
(* n = 2, where the free model counts 3 + C(4, 2) = 9 classes (at n = 5: *)
(* 15 + 225 = 240 against 15 + C(16, 2) = 135).  So a schema-to-free-    *)
(* model translation gives  free lower bound <= orbit_work, with         *)
(* equality only when each comm stage's typed group is its whole licence *)
Example typed_work_not_optimal_pin :
  orbit_work (fun _ => [mkIx 1 0 2]) [mkStage kg [Env 0; Env 0]; mkStage kg [Prev 0; Prev 0]] = 12 /\
  length (nodup qry_dec (all_classes (fun s => s =? 1) (fun i => i) ktest (gg_targets 2))) = 9 /\
  orbit_work (fun _ => [mkIx 1 0 5]) [mkStage kg [Env 0; Env 0]; mkStage kg [Prev 0; Prev 0]] = 240 /\
  15 + C 16 2 = 135.
Proof. vm_compute. repeat split. Qed.

(* fusing a comm kernel with a non-comm one over method_for(A, A), A over *)
(* Idx<5>: the meet is dense, 25 traversal steps = 50 kernel evaluations, *)
(* against 15 + 25 = 40 for the unfused pair                              *)
Example fusion_mismatch_pin :
  let b := [(0, [mkIx 1 0 5]); (0, [mkIx 1 0 5])] in
  shapeCard (out_shape (true && false) b) = 25 /\
  2 * length (trace (fused_prog (out_shape (true && false) b))
                    (fused_oracle (fun t => hd 0 t) (fun t => length t))) = 50 /\
  length (trace (par_prog (out_shape true b) (out_shape false b))
                (par_oracle (fun t => hd 0 t) (fun t => length t))) = 40.
Proof. vm_compute. repeat split. Qed.

(* the meet is NECESSARY, not just safe: read on the comm traversal, a   *)
(* non-symmetric kernel (first position) loses the cell (1, 0)           *)
Definition lA : list (nat * Arr nat) :=
  [(0, ([mkIx 1 0 2], fun t => hd 0 t)); (0, ([mkIx 1 0 2], fun t => hd 0 t))].
Definition kfirst : kernel := mkK 2 (fun v => v 0) false.

Example meet_necessary_pin :
  qread (out_shape true (shapes_of lA), apply_kernel kfirst lA) [1; 0] = 0 /\
  apply_kernel kfirst lA [1; 0] = 1 /\
  qread (out_shape (true && false) (shapes_of lA), apply_kernel kfirst lA) [1; 0] = 1.
Proof. vm_compute. repeat split. Qed.
