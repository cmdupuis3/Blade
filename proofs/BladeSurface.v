(* ===================================================================== *)
(* BladeSurface.v -- THE SURFACE CALCULUS: progress, preservation, and   *)
(* the quotient as a theorem about typing derivations.                   *)
(* (docs/plans/plan-one-model-surface-calculus.md, item 2)               *)
(*                                                                       *)
(* The structure-first core of Blade (formalism 7, 8, 10, 13, 14),       *)
(* closed over a global binding environment -- Blade's top level, where  *)
(* identity is by name (8.3).  Arrays, names, loop objects from either   *)
(* side (method_for = the outer product, zip = co-iteration,             *)
(* object_for), application <@>, compute, and the elementwise >>@        *)
(* fragment.  A kernel is an arity, a body over position-indexed values  *)
(* and a comm flag; kwf is Fun-Intro's "metadata well-formed" premise:   *)
(* locality, and comm only when the body is invariant under every        *)
(* permutation of its positions (BladeLowering.invariant_under over      *)
(* BladeCompleteness.perm_pair).                                         *)
(*                                                                       *)
(* The licence lives in the typing rule for application.  out_shape     *)
(* groups neighbouring equal names (8.3) and, under comm, replaces a     *)
(* group of g rank-1 operands over [l, u) by the ONE DMWF record         *)
(* (g, l, u) -- 8.4 step 3 at s = 1, 11.2's H-and-Stab as a function of  *)
(* the derivation.  That is the only place structure is introduced, and  *)
(* what it introduces is exactly a DMWF record: the sense in which the   *)
(* DMWF is the core.                                                     *)
(*                                                                       *)
(*   progress, preservation, step_size, normalizes                       *)
(*                            type soundness of the calculus, under      *)
(*                            env_ok G rho                               *)
(*   den, step_den, eval_sound                                           *)
(*                            every term denotes an element of the one   *)
(*                            object (BladeObject); every step preserves *)
(*                            the denotation ON THE NOSE; every          *)
(*                            well-typed array term evaluates to an EArr *)
(*                            carrying its denotation, so veval of the   *)
(*                            denotation IS what the program computes    *)
(*   typed_app_is_quotient    for a well-typed L <@> f: the typed output *)
(*                            shape is out_shape; every cell of the      *)
(*                            DENSE output (what a collection-first      *)
(*                            system computes) is recovered from the     *)
(*                            typed array by canonicalizing access; and  *)
(*                            the typed array has exactly one cell per   *)
(*                            orbit of the identity-group action, which  *)
(*                            is what the object's program asks          *)
(*   comm_license_needed      without comm the typed shape is the dense  *)
(*                            one: nothing is quotiented undeclared      *)
(*   covariance_shape         method_for(A, A) <@> comm kernel over      *)
(*                            Idx<n> types at SymIdx<2, n>, C(n+1, 2)    *)
(*                            cells (a computed pin at n = 5: 15)        *)
(*   method_vs_zip_rank       the outer product and co-iteration of the  *)
(*                            same operands differ in output rank        *)
(*   deduced_comm_is_licence  the free-model bridge: a binary kernel     *)
(*                            body equal modulo the declared laws to its *)
(*                            own swap (BladeDeduceExact's teq) is a     *)
(*                            well-formed comm kernel -- deduction is a  *)
(*                            licence source                             *)
(*                                                                       *)
(* Scope, stated once.  Rank-0 kernels (no T-dimensions, no irank        *)
(* slices); identity groups quotiented over rank-1 operands only (8.4's  *)
(* s = 1); values are nat (the tower's convention); no local let; no     *)
(* folds; no let rec.  Each is a listed item of the plan, not a hole.    *)
(*                                                                       *)
(* Imports BladeDMWF, BladeShape, BladeTrinity, BladeTrinityAsym,        *)
(* BladeBinomial, BladeCompute, BladeMonad, BladeMixedRadix,             *)
(* BladeLowering, BladeCompleteness, BladeOptimal, BladeOrbitWork,       *)
(* BladeDeduceExact, BladeObject.  Coq 8.18 / Rocq 9, stdlib only.       *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF BladeShape BladeTrinity BladeTrinityAsym
                          BladeBinomial BladeCompute BladeMonad BladeMixedRadix
                          BladeLowering BladeCompleteness BladeOptimal
                          BladeOrbitWork BladeDeduceExact BladeObject.
Require Import List Arith Lia Permutation Bool.
Import ListNotations.

(* ===================================================================== *)
(* PART A.  KERNELS, SYNTAX, TYPES.                                      *)
(* ===================================================================== *)

Record kernel : Type := mkK {
  k_arity : nat;
  k_body  : (nat -> nat) -> nat;
  k_comm  : bool
}.

(* the kernel reads only positions < arity *)
Definition klocal (f : kernel) : Prop :=
  forall v v', (forall p, p < k_arity f -> v p = v' p) -> k_body f v = k_body f v'.

(* Fun-Intro's metadata premise: comm is a CLAIM the body must honour *)
Definition kwf (f : kernel) : Prop :=
  klocal f /\
  (k_comm f = true ->
   forall s s', perm_pair (k_arity f) s s' -> invariant_under nat nat (k_body f) s).

Lemma kwf_Ksym : forall f, kwf f -> k_comm f = true -> Ksym (k_arity f) (k_body f).
Proof. intros f [Hl Hc] E. split; [exact Hl | exact (Hc E)]. Qed.

Inductive expr : Type :=
| EArr     : Shape -> (list nat -> nat) -> expr   (* an array value             *)
| EVar     : nat -> expr                          (* a top-level name           *)
| EMethod  : list nat -> expr                     (* method_for(x1, ..., xn)    *)
| EZip     : list nat -> expr                     (* method_for(zip(x1, .., xn))*)
| EObject  : kernel -> expr                       (* object_for(f)              *)
| EAppM    : expr -> kernel -> expr               (* L <@> f                    *)
| EAppO    : expr -> list nat -> expr             (* O <@> (x1, ..., xn)        *)
| ECompute : expr -> expr                         (* c |> compute               *)
| EPipe    : expr -> (nat -> nat) -> expr.        (* c >>@ g (elementwise)      *)

Inductive ty : Type :=
| TArr     : Shape -> ty
| TLoop    : list (nat * Shape) -> ty             (* the binding: names + shapes *)
| TZipLoop : Shape -> nat -> ty                   (* the common shape, the arity *)
| TObj     : kernel -> ty
| TComp    : Shape -> ty.

(* ---------------- environments ---------------- *)

Definition tenv := list (nat * Shape).
Definition venv := list (nat * Arr nat).

Fixpoint lookup {A : Type} (x : nat) (e : list (nat * A)) : option A :=
  match e with
  | [] => None
  | (y, a) :: e' => if Nat.eqb x y then Some a else lookup x e'
  end.

Definition env_ok (G : tenv) (rho : venv) : Prop :=
  forall x s, lookup x G = Some s -> exists k, lookup x rho = Some (s, k).

(* the binding of a name list: each name with its shape *)
Fixpoint bind_names (G : tenv) (xs : list nat) : option (list (nat * Shape)) :=
  match xs with
  | [] => Some []
  | x :: xs' =>
      match lookup x G, bind_names G xs' with
      | Some s, Some b => Some ((x, s) :: b)
      | _, _ => None
      end
  end.

Fixpoint bind_arrs (rho : venv) (xs : list nat) : option (list (nat * Arr nat)) :=
  match xs with
  | [] => Some []
  | x :: xs' =>
      match lookup x rho, bind_arrs rho xs' with
      | Some A, Some l => Some ((x, A) :: l)
      | _, _ => None
      end
  end.

Definition dflt : Arr nat := ([], fun _ => 0).

(* the total read of a name *)
Definition rd (rho : venv) (x : nat) : Arr nat :=
  match lookup x rho with Some A => A | None => dflt end.

(* a binding, resolved through rd *)
Definition as_of (ro : nat -> Arr nat) (b : list (nat * Shape)) : list (nat * Arr nat) :=
  map (fun p => (fst p, ro (fst p))) b.

Lemma tab_nth_r : forall (l : list nat) d r,
  length l = r -> map (fun p => nth p l d) (seq 0 r) = l.
Proof. intros l d r <-. apply tab_nth. Qed.

Lemma as_of_cons : forall ro x s b,
  as_of ro ((x, s) :: b) = (x, ro x) :: as_of ro b.
Proof. reflexivity. Qed.

Lemma rd_some : forall rho x A, lookup x rho = Some A -> rd rho x = A.
Proof. intros rho x A H. unfold rd. rewrite H. reflexivity. Qed.

Lemma bind_names_lookup : forall G xs b x s,
  bind_names G xs = Some b -> In (x, s) b -> lookup x G = Some s.
Proof.
  intros G xs. induction xs as [|y xs IH]; intros b x s Hb Hin; simpl in Hb.
  - injection Hb as <-. destruct Hin.
  - destruct (lookup y G) as [s'|] eqn:Ey; [| discriminate].
    destruct (bind_names G xs) as [b'|] eqn:Eb; [| discriminate].
    injection Hb as <-. destruct Hin as [E | Hin].
    + injection E as <- <-. exact Ey.
    + exact (IH b' x s eq_refl Hin).
Qed.

Lemma bind_arrs_as_of : forall G rho xs b,
  env_ok G rho -> bind_names G xs = Some b ->
  bind_arrs rho xs = Some (as_of (rd rho) b).
Proof.
  intros G rho xs. induction xs as [|y xs IH]; intros b Hok Hb; simpl in Hb.
  - injection Hb as <-. reflexivity.
  - destruct (lookup y G) as [s|] eqn:Ey; [| discriminate].
    destruct (bind_names G xs) as [b'|] eqn:Eb; [| discriminate].
    injection Hb as <-.
    destruct (Hok y s Ey) as [k Hk].
    cbn [bind_arrs]. rewrite Hk. rewrite (IH b' Hok eq_refl).
    rewrite as_of_cons, (rd_some rho y (s, k) Hk). reflexivity.
Qed.

(* the shapes a binding records are the shapes of the arrays it reads *)
Lemma binding_shapes : forall G rho xs b,
  env_ok G rho -> bind_names G xs = Some b ->
  forall x s, In (x, s) b -> fst (rd rho x) = s.
Proof.
  intros G rho xs b Hok Hb x s Hin.
  pose proof (bind_names_lookup G xs b x s Hb Hin) as Ex.
  destruct (Hok x s Ex) as [k Hk].
  unfold rd. rewrite Hk. reflexivity.
Qed.

Lemma bind_names_length : forall G xs b,
  bind_names G xs = Some b -> length b = length xs.
Proof.
  intros G xs. induction xs as [|y xs IH]; intros b Hb; simpl in Hb.
  - injection Hb as <-. reflexivity.
  - destruct (lookup y G); [| discriminate].
    destruct (bind_names G xs) as [b'|] eqn:Eb; [| discriminate].
    injection Hb as <-. simpl. rewrite (IH b' eq_refl). reflexivity.
Qed.

(* ===================================================================== *)
(* PART B.  IDENTITY GROUPS AND THE OUTPUT SHAPE (8.3, 8.4, 11.2).       *)
(* ===================================================================== *)

Definition IxRec_eq_dec : forall a b : IxRec, {a = b} + {a <> b}.
Proof. decide equality; apply Nat.eq_dec. Defined.

Definition shape_eq_dec : forall s s' : Shape, {s = s'} + {s <> s'} :=
  list_eq_dec IxRec_eq_dec.

Record grp : Type := mkG { g_name : nat; g_shape : Shape; g_mult : nat }.

(* run-length encoding of the binding by name: the identity groups *)
Fixpoint rle (b : list (nat * Shape)) : list grp :=
  match b with
  | [] => []
  | (x, s) :: b' =>
      match rle b' with
      | g :: gs =>
          if Nat.eq_dec x (g_name g)
          then if shape_eq_dec s (g_shape g)
               then mkG x s (S (g_mult g)) :: gs
               else mkG x s 1 :: g :: gs
          else mkG x s 1 :: g :: gs
      | [] => [mkG x s 1]
      end
  end.

Definition expand (g : grp) : list (nat * Shape) :=
  repeat (g_name g, g_shape g) (g_mult g).

Lemma rle_expand : forall b, concat (map expand (rle b)) = b.
Proof.
  induction b as [|[x s] b IH]; simpl; [reflexivity |].
  destruct (rle b) as [|g gs] eqn:E; simpl in *.
  - rewrite <- IH. reflexivity.
  - destruct (Nat.eq_dec x (g_name g)) as [-> | N].
    + destruct (shape_eq_dec s (g_shape g)) as [-> | N'].
      * simpl. unfold expand at 1. simpl. rewrite <- IH. reflexivity.
      * simpl. rewrite <- IH. reflexivity.
    + simpl. rewrite <- IH. reflexivity.
Qed.

(* the dense (ungrouped) shape of a group, and of a binding *)
Definition dshape (g : grp) : Shape := concat (repeat (g_shape g) (g_mult g)).
Definition dense_shape (b : list (nat * Shape)) : Shape := concat (map snd b).

Lemma map_repeat' : forall (A B : Type) (f : A -> B) x n,
  map f (repeat x n) = repeat (f x) n.
Proof. intros A B f x n. induction n; simpl; [reflexivity | rewrite IHn; reflexivity]. Qed.

Lemma dense_shape_expand : forall gs,
  concat (map snd (concat (map expand gs))) = concat (map dshape gs).
Proof.
  induction gs as [|g gs IH]; simpl; [reflexivity |].
  rewrite map_app, concat_app, IH. f_equal.
  unfold expand, dshape. rewrite map_repeat'. reflexivity.
Qed.

Lemma dense_shape_groups : forall b,
  dense_shape b = concat (map dshape (rle b)).
Proof.
  intro b. unfold dense_shape. rewrite <- (rle_expand b) at 1. apply dense_shape_expand.
Qed.

(* a group is QUOTIENTED when comm is declared and its operands are     *)
(* rank 1: 8.4 step 3 at s = 1                                          *)
Definition grouped (comm : bool) (g : grp) : bool :=
  comm && match g_shape g with
          | [c] => Nat.eqb (ix_r c) 1
          | _ => false
          end.

Definition group_shape (comm : bool) (g : grp) : Shape :=
  if grouped comm g
  then match g_shape g with
       | [c] => [mkIx (g_mult g) (ix_l c) (ix_u c)]
       | _ => []
       end
  else dshape g.

(* OutputSymmetry (11.3) as a function of the binding and the claim *)
Definition out_shape (comm : bool) (b : list (nat * Shape)) : Shape :=
  concat (map (group_shape comm) (rle b)).

Lemma grouped_true : forall comm g,
  grouped comm g = true ->
  comm = true /\ exists c, g_shape g = [c] /\ ix_r c = 1.
Proof.
  intros comm g H. unfold grouped in H. apply andb_true_iff in H as [Hc Hs].
  split; [exact Hc |].
  destruct (g_shape g) as [|c [|c' s']]; try discriminate.
  exists c. split; [reflexivity | apply Nat.eqb_eq; exact Hs].
Qed.

Lemma group_shape_grouped : forall comm g c,
  grouped comm g = true -> g_shape g = [c] ->
  group_shape comm g = [mkIx (g_mult g) (ix_l c) (ix_u c)].
Proof. intros comm g c Hg Hs. unfold group_shape. rewrite Hg, Hs. reflexivity. Qed.

Lemma group_shape_ungrouped : forall comm g,
  grouped comm g = false -> group_shape comm g = dshape g.
Proof. intros comm g Hg. unfold group_shape. rewrite Hg. reflexivity. Qed.

(* NOTHING IS QUOTIENTED WITHOUT A DECLARATION *)
Theorem comm_license_needed : forall b, out_shape false b = dense_shape b.
Proof.
  intro b. unfold out_shape. rewrite dense_shape_groups. reflexivity.
Qed.

(* ---------------- widths ---------------- *)

Lemma swidth_app : forall s1 s2, swidth (s1 ++ s2) = swidth s1 + swidth s2.
Proof.
  induction s1 as [|c s1 IH]; intro s2; simpl; [reflexivity |].
  unfold swidth in *. simpl. rewrite IH. lia.
Qed.

Lemma swidth_concat_repeat : forall s m,
  swidth (concat (repeat s m)) = m * swidth s.
Proof.
  intros s m. induction m as [|m IH]; simpl; [reflexivity |].
  rewrite swidth_app, IH. lia.
Qed.

Lemma swidth_group_shape : forall comm g,
  swidth (group_shape comm g) = swidth (dshape g).
Proof.
  intros comm g. destruct (grouped comm g) eqn:E.
  - destruct (grouped_true comm g E) as (_ & c & Hs & Hr).
    rewrite (group_shape_grouped comm g c E Hs).
    unfold dshape. rewrite Hs, swidth_concat_repeat.
    unfold swidth. simpl. rewrite Hr. lia.
  - rewrite group_shape_ungrouped by exact E. reflexivity.
Qed.

Lemma swidth_concat_map : forall (A : Type) (f : A -> Shape) (l : list A),
  swidth (concat (map f l)) = lsum (map (fun a => swidth (f a)) l).
Proof.
  intros A f l. induction l as [|a l IH]; simpl; [reflexivity |].
  rewrite swidth_app, IH. reflexivity.
Qed.

(* grouping never changes the output RANK, only which tuples are stored *)
Lemma swidth_out_shape : forall comm b,
  swidth (out_shape comm b) = swidth (dense_shape b).
Proof.
  intros comm b. unfold out_shape. rewrite dense_shape_groups.
  rewrite !swidth_concat_map. f_equal. apply map_ext. intro g.
  apply swidth_group_shape.
Qed.

(* ===================================================================== *)
(* PART C.  SEMANTICS: applying a kernel on the output tuple.            *)
(* Position p reads operand p at p's segment of the tuple (the segment   *)
(* has the width of the operand's own shape).                            *)
(* ===================================================================== *)

Fixpoint vals (l : list (nat * Arr nat)) (c : list nat) : list nat :=
  match l with
  | [] => []
  | (x, A) :: l' =>
      snd A (firstn (swidth (fst A)) c) :: vals l' (skipn (swidth (fst A)) c)
  end.

Definition apply_kernel (f : kernel) (l : list (nat * Arr nat)) (c : list nat) : nat :=
  k_body f (fun p => nth p (vals l c) 0).

(* co-iteration: every operand reads the SAME tuple *)
Definition zvals (l : list (nat * Arr nat)) (c : list nat) : list nat :=
  map (fun p => snd (snd p) c) l.

Definition apply_zip (f : kernel) (l : list (nat * Arr nat)) (c : list nat) : nat :=
  k_body f (fun p => nth p (zvals l c) 0).

Definition zshape (l : list (nat * Arr nat)) : Shape :=
  match l with [] => [] | p :: _ => fst (snd p) end.

Definition shapes_of (l : list (nat * Arr nat)) : list (nat * Shape) :=
  map (fun p => (fst p, fst (snd p))) l.

Lemma shapes_of_as_of : forall ro b,
  (forall x s, In (x, s) b -> fst (ro x) = s) ->
  shapes_of (as_of ro b) = b.
Proof.
  intros ro b H. unfold shapes_of, as_of. rewrite map_map.
  induction b as [|[x s] b IH]; simpl; [reflexivity |].
  rewrite (H x s (or_introl eq_refl)). f_equal.
  apply IH. intros y s' Hin. apply H. right. exact Hin.
Qed.

Lemma vals_length : forall l c, length (vals l c) = length l.
Proof.
  induction l as [|[x A] l IH]; intro c; simpl; [reflexivity | rewrite IH; reflexivity].
Qed.

Definition widths (l : list (nat * Arr nat)) : nat :=
  lsum (map (fun p => swidth (fst (snd p))) l).

Lemma vals_app : forall l1 l2 t1 t2,
  widths l1 = length t1 ->
  vals (l1 ++ l2) (t1 ++ t2) = vals l1 t1 ++ vals l2 t2.
Proof.
  induction l1 as [|[x A] l1 IH]; intros l2 t1 t2 Hw; simpl in *.
  - unfold widths in Hw. simpl in Hw. destruct t1; [reflexivity | discriminate].
  - unfold widths in Hw. simpl in Hw.
    assert (Hle : swidth (fst A) <= length t1) by lia.
    rewrite firstn_app, skipn_app.
    replace (swidth (fst A) - length t1) with 0 by lia.
    rewrite firstn_O, app_nil_r. simpl.
    f_equal. rewrite <- IH; [reflexivity |].
    unfold widths. rewrite skipn_length. lia.
Qed.

(* ===================================================================== *)
(* PART D.  TYPING (13.2), VALUES, REDUCTION (14.4).                     *)
(* ===================================================================== *)

Definition all_shape (s : Shape) (b : list (nat * Shape)) : Prop :=
  Forall (fun p => snd p = s) b.

Inductive has_ty (G : tenv) : expr -> ty -> Prop :=
| T_Arr : forall s k,
    has_ty G (EArr s k) (TArr s)
| T_Var : forall x s,
    lookup x G = Some s ->
    has_ty G (EVar x) (TArr s)
| T_Method : forall xs b,
    bind_names G xs = Some b ->
    has_ty G (EMethod xs) (TLoop b)
| T_Zip : forall xs b s,
    bind_names G xs = Some b -> b <> [] -> all_shape s b ->
    has_ty G (EZip xs) (TZipLoop s (length b))
| T_Object : forall f,
    kwf f ->
    has_ty G (EObject f) (TObj f)
| T_AppM : forall L b f,
    has_ty G L (TLoop b) -> kwf f -> k_arity f = length b ->
    has_ty G (EAppM L f) (TComp (out_shape (k_comm f) b))
| T_AppZ : forall L s n f,
    has_ty G L (TZipLoop s n) -> kwf f -> k_arity f = n ->
    has_ty G (EAppM L f) (TComp s)
| T_AppO : forall O f xs b,
    has_ty G O (TObj f) -> bind_names G xs = Some b -> k_arity f = length b ->
    has_ty G (EAppO O xs) (TComp (out_shape (k_comm f) b))
| T_Compute : forall c s,
    has_ty G c (TComp s) ->
    has_ty G (ECompute c) (TArr s)
| T_PipeC : forall c g s,
    has_ty G c (TComp s) ->
    has_ty G (EPipe c g) (TComp s)
| T_PipeA : forall e g s,
    has_ty G e (TArr s) ->
    has_ty G (EPipe e g) (TArr s).

(* deferred computations are values: nothing runs before compute *)
Inductive cvalue : expr -> Prop :=
| CV_AppM : forall xs f, cvalue (EAppM (EMethod xs) f)
| CV_AppZ : forall xs f, cvalue (EAppM (EZip xs) f)
| CV_AppO : forall f xs, cvalue (EAppO (EObject f) xs)
| CV_Pipe : forall c g, cvalue c -> cvalue (EPipe c g).

Inductive value : expr -> Prop :=
| V_Arr    : forall s k, value (EArr s k)
| V_Method : forall xs, value (EMethod xs)
| V_Zip    : forall xs, value (EZip xs)
| V_Object : forall f, value (EObject f)
| V_Comp   : forall c, cvalue c -> value c.

Inductive step (rho : venv) : expr -> expr -> Prop :=
| S_Var : forall x A,
    lookup x rho = Some A ->
    step rho (EVar x) (EArr (fst A) (snd A))
| S_AppM : forall xs f l,
    bind_arrs rho xs = Some l ->
    step rho (ECompute (EAppM (EMethod xs) f))
             (EArr (out_shape (k_comm f) (shapes_of l)) (apply_kernel f l))
| S_AppZ : forall xs f l,
    bind_arrs rho xs = Some l ->
    step rho (ECompute (EAppM (EZip xs) f))
             (EArr (zshape l) (apply_zip f l))
| S_AppO : forall f xs l,
    bind_arrs rho xs = Some l ->
    step rho (ECompute (EAppO (EObject f) xs))
             (EArr (out_shape (k_comm f) (shapes_of l)) (apply_kernel f l))
| S_ComputePipe : forall c g,
    step rho (ECompute (EPipe c g)) (EPipe (ECompute c) g)
| S_Compute : forall c c',
    step rho c c' ->
    step rho (ECompute c) (ECompute c')
| S_PipeA : forall s k g,
    step rho (EPipe (EArr s k) g) (EArr s (fun t => g (k t)))
| S_Pipe : forall e e' g,
    step rho e e' ->
    step rho (EPipe e g) (EPipe e' g).

(* ---------------- canonical forms ---------------- *)

Lemma canon_loop : forall G L b, has_ty G L (TLoop b) -> exists xs, L = EMethod xs.
Proof. intros G L b H. inversion H; subst. eexists. reflexivity. Qed.

Lemma canon_zip : forall G L s n, has_ty G L (TZipLoop s n) -> exists xs, L = EZip xs.
Proof. intros G L s n H. inversion H; subst. eexists. reflexivity. Qed.

Lemma canon_obj : forall G O f, has_ty G O (TObj f) -> O = EObject f.
Proof. intros G O f H. inversion H; subst. reflexivity. Qed.

(* inversion of the three application typings *)
Lemma ty_appM_method : forall G xs f s,
  has_ty G (EAppM (EMethod xs) f) (TComp s) ->
  exists b, bind_names G xs = Some b /\ kwf f /\ k_arity f = length b /\
            s = out_shape (k_comm f) b.
Proof.
  intros G xs f s H. inversion H; subst.
  - match goal with [H' : has_ty _ (EMethod _) (TLoop _) |- _] => inversion H'; subst end.
    exists b. split; [assumption | split; [assumption | split; [assumption | reflexivity]]].
  - match goal with [H' : has_ty _ (EMethod _) (TZipLoop _ _) |- _] => inversion H' end.
Qed.

Lemma ty_appM_zip : forall G xs f s,
  has_ty G (EAppM (EZip xs) f) (TComp s) ->
  exists b, bind_names G xs = Some b /\ b <> [] /\ all_shape s b /\
            kwf f /\ k_arity f = length b.
Proof.
  intros G xs f s H. inversion H; subst.
  - match goal with [H' : has_ty _ (EZip _) (TLoop _) |- _] => inversion H' end.
  - match goal with [H' : has_ty _ (EZip _) (TZipLoop _ _) |- _] => inversion H'; subst end.
    exists b. split; [assumption | split; [assumption | split; [assumption | split; [assumption | first [assumption | reflexivity | (symmetry; assumption)]]]]].
Qed.

Lemma ty_appO : forall G f xs s,
  has_ty G (EAppO (EObject f) xs) (TComp s) ->
  exists b, bind_names G xs = Some b /\ kwf f /\ k_arity f = length b /\
            s = out_shape (k_comm f) b.
Proof.
  intros G f xs s H. inversion H; subst.
  match goal with [H' : has_ty _ (EObject _) (TObj _) |- _] => inversion H'; subst end.
  exists b. split; [assumption | split; [assumption | split; [assumption | reflexivity]]].
Qed.

Lemma cvalue_ty : forall G c t, cvalue c -> has_ty G c t -> exists s, t = TComp s.
Proof.
  intros G c t Hc. revert t. induction Hc; intros t Ht; inversion Ht; subst;
    try (eexists; reflexivity).
  match goal with [H' : has_ty _ c (TArr _) |- _] =>
    destruct (IHHc _ H') as [s' E]; discriminate end.
Qed.

Lemma value_arr : forall G e s, value e -> has_ty G e (TArr s) -> exists k, e = EArr s k.
Proof.
  intros G e s Hv Ht. inversion Hv; subst; try solve [inversion Ht; subst; eexists; reflexivity].
  match goal with [Hc : cvalue _ |- _] =>
    destruct (cvalue_ty G e (TArr s) Hc Ht) as [s' E]; discriminate end.
Qed.

Lemma value_comp : forall G e s, value e -> has_ty G e (TComp s) -> cvalue e.
Proof.
  intros G e s Hv Ht. inversion Hv; subst; try solve [inversion Ht]. assumption.
Qed.

Lemma bind_arrs_some : forall G rho xs b,
  env_ok G rho -> bind_names G xs = Some b -> exists l, bind_arrs rho xs = Some l.
Proof.
  intros G rho xs b Hok Hb. exists (as_of (rd rho) b).
  exact (bind_arrs_as_of G rho xs b Hok Hb).
Qed.

(* ---------------- progress ---------------- *)

Theorem progress : forall G rho e t,
  env_ok G rho -> has_ty G e t -> value e \/ exists e', step rho e e'.
Proof.
  intros G rho e t Hok Ht. induction Ht.
  - left. constructor.
  - right. destruct (Hok x s H) as [k Hk]. exists (EArr s k).
    change (EArr s k) with (EArr (fst (s, k)) (snd (s, k))). constructor. exact Hk.
  - left. constructor.
  - left. constructor.
  - left. constructor.
  - destruct (canon_loop G L b Ht) as [xs ->]. left. constructor. constructor.
  - destruct (canon_zip G L s n Ht) as [xs ->]. left. constructor. constructor.
  - rewrite (canon_obj G O f Ht). left. constructor. constructor.
  - right. destruct IHHt as [Hv | [c' Hs]].
    + pose proof (value_comp G c s Hv Ht) as Hc.
      inversion Hc; subst.
      * destruct (ty_appM_method _ _ _ _ Ht) as (b & Hb & _).
        destruct (bind_arrs_some G rho xs b Hok Hb) as [l Hl].
        eexists. apply S_AppM. exact Hl.
      * destruct (ty_appM_zip _ _ _ _ Ht) as (b & Hb & _).
        destruct (bind_arrs_some G rho xs b Hok Hb) as [l Hl].
        eexists. apply S_AppZ. exact Hl.
      * destruct (ty_appO _ _ _ _ Ht) as (b & Hb & _).
        destruct (bind_arrs_some G rho xs b Hok Hb) as [l Hl].
        eexists. apply S_AppO. exact Hl.
      * eexists. apply S_ComputePipe.
    + eexists. apply S_Compute. exact Hs.
  - destruct IHHt as [Hv | [c' Hs]].
    + left. constructor. constructor. exact (value_comp G c s Hv Ht).
    + right. eexists. apply S_Pipe. exact Hs.
  - destruct IHHt as [Hv | [e' Hs]].
    + right. destruct (value_arr G e s Hv Ht) as [k ->].
      eexists. apply S_PipeA.
    + right. eexists. apply S_Pipe. exact Hs.
Qed.

(* ---------------- preservation ---------------- *)

Lemma bind_arrs_shapes : forall G rho xs b l,
  env_ok G rho -> bind_names G xs = Some b -> bind_arrs rho xs = Some l ->
  shapes_of l = b.
Proof.
  intros G rho xs b l Hok Hb Hl.
  rewrite (bind_arrs_as_of G rho xs b Hok Hb) in Hl. injection Hl as <-.
  apply shapes_of_as_of. exact (binding_shapes G rho xs b Hok Hb).
Qed.

Lemma zshape_of : forall l s,
  shapes_of l <> [] -> all_shape s (shapes_of l) -> zshape l = s.
Proof.
  intros l s Hne Hall. destruct l as [|[x A] l]; [contradiction |].
  unfold all_shape in Hall. simpl in Hall. inversion Hall; subst. reflexivity.
Qed.

Theorem preservation : forall G rho e e' t,
  env_ok G rho -> has_ty G e t -> step rho e e' -> has_ty G e' t.
Proof.
  intros G rho e e' t Hok Ht Hs. revert t Ht.
  induction Hs; intros t Ht.
  - (* S_Var *)
    inversion Ht; subst.
    match goal with [Hl : lookup x G = Some ?s |- _] =>
      destruct (Hok x s Hl) as [k Hk]; rewrite H in Hk; injection Hk as E; subst A end.
    simpl. constructor.
  - (* S_AppM *)
    inversion Ht; subst.
    match goal with [H' : has_ty _ (EAppM _ _) (TComp _) |- _] =>
      destruct (ty_appM_method _ _ _ _ H') as (b & Hb & _ & _ & ->) end.
    rewrite (bind_arrs_shapes G rho xs b l Hok Hb H). constructor.
  - (* S_AppZ *)
    inversion Ht; subst.
    match goal with [H' : has_ty _ (EAppM _ _) (TComp _) |- _] =>
      destruct (ty_appM_zip _ _ _ _ H') as (b & Hb & Hne & Hall & _ & _) end.
    pose proof (bind_arrs_shapes G rho xs b l Hok Hb H) as E.
    rewrite (zshape_of l s); [constructor | rewrite E; exact Hne | rewrite E; exact Hall].
  - (* S_AppO *)
    inversion Ht; subst.
    match goal with [H' : has_ty _ (EAppO _ _) (TComp _) |- _] =>
      destruct (ty_appO _ _ _ _ H') as (b & Hb & _ & _ & ->) end.
    rewrite (bind_arrs_shapes G rho xs b l Hok Hb H). constructor.
  - (* S_ComputePipe *)
    inversion Ht; subst.
    match goal with [H' : has_ty _ (EPipe _ _) (TComp _) |- _] => inversion H'; subst end.
    constructor. constructor. assumption.
  - (* S_Compute *)
    inversion Ht; subst. constructor. apply IHHs. assumption.
  - (* S_PipeA *)
    inversion Ht; subst.
    + match goal with [H' : has_ty _ (EArr _ _) (TComp _) |- _] => inversion H' end.
    + match goal with [H' : has_ty _ (EArr _ _) (TArr _) |- _] => inversion H'; subst end.
      constructor.
  - (* S_Pipe *)
    inversion Ht; subst; constructor; apply IHHs; assumption.
Qed.

(* ---------------- normalization ---------------- *)

Fixpoint size (e : expr) : nat :=
  match e with
  | EArr _ _ => 0
  | EVar _ => 1
  | EMethod _ => 0
  | EZip _ => 0
  | EObject _ => 0
  | EAppM L _ => size L + 1
  | EAppO Ob _ => size Ob + 1
  | ECompute c => 2 * size c + 1
  | EPipe e _ => size e + 1
  end.

Theorem step_size : forall rho e e', step rho e e' -> size e' < size e.
Proof. intros rho e e' H. induction H; simpl; lia. Qed.

Inductive star (rho : venv) : expr -> expr -> Prop :=
| star_refl : forall e, star rho e e
| star_step : forall e e' e'', step rho e e' -> star rho e' e'' -> star rho e e''.

Lemma star_preservation : forall G rho e e' t,
  env_ok G rho -> has_ty G e t -> star rho e e' -> has_ty G e' t.
Proof.
  intros G rho e e' t Hok Ht Hs. induction Hs; [exact Ht |].
  apply IHHs. exact (preservation G rho e e' t Hok Ht H).
Qed.

Theorem normalizes : forall G rho e t,
  env_ok G rho -> has_ty G e t -> exists v, star rho e v /\ value v.
Proof.
  intros G rho e t Hok Ht.
  remember (size e) as n eqn:En. revert e t En Ht.
  induction n as [n IH] using lt_wf_ind; intros e t En Ht.
  destruct (progress G rho e t Hok Ht) as [Hv | [e' Hs]].
  - exists e. split; [constructor | exact Hv].
  - pose proof (step_size rho e e' Hs) as Hlt.
    destruct (IH (size e') ltac:(lia) e' t eq_refl
                 (preservation G rho e e' t Hok Ht Hs)) as (v & Hv1 & Hv2).
    exists v. split; [apply star_step with e'; assumption | exact Hv2].
Qed.

(* ===================================================================== *)
(* PART E.  DENOTATION INTO THE ONE OBJECT.                              *)
(* ===================================================================== *)

Fixpoint den (rho : venv) (e : expr) : Arr nat :=
  match e with
  | EArr s k => (s, k)
  | EVar x => rd rho x
  | EAppM (EMethod xs) f =>
      match bind_arrs rho xs with
      | Some l => (out_shape (k_comm f) (shapes_of l), apply_kernel f l)
      | None => dflt
      end
  | EAppM (EZip xs) f =>
      match bind_arrs rho xs with
      | Some l => (zshape l, apply_zip f l)
      | None => dflt
      end
  | EAppO (EObject f) xs =>
      match bind_arrs rho xs with
      | Some l => (out_shape (k_comm f) (shapes_of l), apply_kernel f l)
      | None => dflt
      end
  | ECompute c => den rho c
  | EPipe e g => pipe (den rho e) g
  | _ => dflt
  end.

(* every step preserves the denotation ON THE NOSE *)
Theorem step_den : forall rho e e', step rho e e' -> den rho e = den rho e'.
Proof.
  intros rho e e' H. induction H; simpl.
  - unfold rd. rewrite H. destruct A. reflexivity.
  - rewrite H. reflexivity.
  - rewrite H. reflexivity.
  - rewrite H. reflexivity.
  - reflexivity.
  - exact IHstep.
  - reflexivity.
  - rewrite IHstep. reflexivity.
Qed.

Lemma star_den : forall rho e e', star rho e e' -> den rho e = den rho e'.
Proof.
  intros rho e e' H. induction H; [reflexivity |].
  rewrite (step_den rho e e' H). exact IHstar.
Qed.

(* every well-typed array term evaluates to an EArr carrying its         *)
(* denotation: the materialized-value semantics IS the calculus's        *)
Theorem eval_sound : forall G rho e s,
  env_ok G rho -> has_ty G e (TArr s) ->
  exists k, star rho e (EArr s k) /\ den rho e = (s, k).
Proof.
  intros G rho e s Hok Ht.
  destruct (normalizes G rho e (TArr s) Hok Ht) as (v & Hst & Hv).
  pose proof (star_preservation G rho e v (TArr s) Hok Ht Hst) as Hv'.
  destruct (value_arr G v s Hv Hv') as [k ->].
  exists k. split; [exact Hst | rewrite (star_den rho e _ Hst); reflexivity].
Qed.

Corollary eval_veval : forall G rho e s,
  env_ok G rho -> has_ty G e (TArr s) ->
  exists k, star rho e (EArr s k) /\ veval (den rho e) = map k (enumShape s).
Proof.
  intros G rho e s Hok Ht.
  destruct (eval_sound G rho e s Hok Ht) as (k & Hst & Hd).
  exists k. split; [exact Hst | rewrite Hd; reflexivity].
Qed.

(* ===================================================================== *)
(* PART F.  THE QUOTIENT, AS A THEOREM ABOUT TYPING DERIVATIONS.         *)
(* ===================================================================== *)

(* --- one group ------------------------------------------------------ *)

Lemma concat_repeat_single : forall (c : IxRec) m,
  concat (repeat [c] m) = repeat c m.
Proof. intros c m. induction m; simpl; [reflexivity | rewrite IHm; reflexivity]. Qed.

(* the enumeration of m copies of a rank-1 record is the dense box *)
Lemma enumShape_repeat1 : forall c m t,
  ix_r c = 1 ->
  (In t (enumShape (repeat c m)) <-> dense_tuple m (ix_l c) (ix_u c) t).
Proof.
  intros c m t Hr. revert t. induction m as [|m IH]; intro t; cbn [repeat].
  - simpl. unfold dense_tuple. split.
    + intros [<- | []]. split; [reflexivity | constructor].
    + intros [Hl _]. destruct t; [left; reflexivity | discriminate].
  - rewrite enumShape_cons, in_sprod. unfold enumIx. rewrite Hr, enum1_singletons.
    split.
    + intros (t1 & t2 & H1 & H2 & ->).
      apply in_map_iff in H1 as (i & <- & Hi). apply in_seq in Hi.
      apply IH in H2 as [Hl HF]. unfold dense_tuple.
      split; [simpl; lia | constructor; [lia | exact HF]].
    + intros [Hl HF]. destruct t as [|i t']; [discriminate |].
      inversion HF; subst. injection Hl as Hl.
      exists [i], t'. split; [| split].
      * apply in_map_iff. exists i. split; [reflexivity | apply in_seq; lia].
      * apply IH. split; assumption.
      * reflexivity.
Qed.

Lemma vals_repeat1 : forall x A m t,
  swidth (fst A) = 1 -> length t = m ->
  vals (repeat (x, A) m) t = map (fun i => snd A [i]) t.
Proof.
  intros x A m. induction m as [|m IH]; intros t Hw Hl; simpl.
  - destruct t; [reflexivity | discriminate].
  - destruct t as [|i t']; [discriminate |]. injection Hl as Hl.
    rewrite Hw. simpl. f_equal. apply IH; assumption.
Qed.

Lemma widths_as_of : forall ro b,
  (forall x s, In (x, s) b -> fst (ro x) = s) ->
  widths (as_of ro b) = swidth (dense_shape b).
Proof.
  intros ro b H. unfold widths, as_of, dense_shape. rewrite map_map.
  induction b as [|[x s] b IH]; simpl; [reflexivity |].
  rewrite swidth_app. rewrite (H x s (or_introl eq_refl)). f_equal.
  apply IH. intros y s' Hin. apply H. right. exact Hin.
Qed.

Lemma as_of_expand : forall ro g,
  as_of ro (expand g) = repeat (g_name g, ro (g_name g)) (g_mult g).
Proof. intros ro g. unfold as_of, expand. rewrite map_repeat'. reflexivity. Qed.

Lemma group_quotient : forall comm ro g t1,
  fst (ro (g_name g)) = g_shape g ->
  In t1 (enumShape (dshape g)) ->
  shape_dense (group_shape comm g) t1 /\
  Permutation (vals (as_of ro (expand g)) t1)
              (vals (as_of ro (expand g)) (scanon (group_shape comm g) t1)).
Proof.
  intros comm ro g t1 Hro Ht1.
  destruct (grouped comm g) eqn:E.
  - destruct (grouped_true comm g E) as (_ & c & Hs & Hr).
    rewrite (group_shape_grouped comm g c E Hs).
    unfold dshape in Ht1. rewrite Hs, concat_repeat_single in Ht1.
    apply enumShape_repeat1 in Ht1; [| exact Hr].
    pose proof (proj1 Ht1) as Hl.
    assert (Hd : shape_dense [mkIx (g_mult g) (ix_l c) (ix_u c)] t1).
    { simpl. rewrite firstn_all2 by lia. rewrite skipn_all2 by lia. split; [exact Ht1 | reflexivity]. }
    split; [exact Hd |].
    simpl scanon. rewrite firstn_all2 by lia. simpl. rewrite app_nil_r.
    rewrite as_of_expand.
    assert (Hw : swidth (fst (ro (g_name g))) = 1).
    { rewrite Hro, Hs. unfold swidth. simpl. lia. }
    rewrite (vals_repeat1 _ _ _ _ Hw Hl).
    rewrite (vals_repeat1 _ _ _ _ Hw) by (rewrite isort_length; exact Hl).
    apply Permutation_map. apply isort_perm.
  - rewrite group_shape_ungrouped by exact E.
    split; [apply enumShape_shape_dense; exact Ht1 |].
    rewrite scanon_fixes by exact Ht1. apply Permutation_refl.
Qed.

(* --- splitting along a concatenation of blocks ---------------------- *)

Lemma scanon_app : forall s1 s2 t1 t2,
  swidth s1 = length t1 ->
  scanon (s1 ++ s2) (t1 ++ t2) = scanon s1 t1 ++ scanon s2 t2.
Proof.
  induction s1 as [|c s1 IH]; intros s2 t1 t2 Hw; simpl in *.
  - unfold swidth in Hw. simpl in Hw. destruct t1; [reflexivity | discriminate].
  - unfold swidth in Hw. simpl in Hw.
    rewrite firstn_app, skipn_app.
    replace (ix_r c - length t1) with 0 by lia.
    rewrite firstn_O, app_nil_r.
    rewrite <- app_assoc. f_equal. apply IH.
    unfold swidth. rewrite skipn_length. lia.
Qed.

Lemma shape_dense_app : forall s1 s2 t1 t2,
  swidth s1 = length t1 ->
  shape_dense s1 t1 -> shape_dense s2 t2 -> shape_dense (s1 ++ s2) (t1 ++ t2).
Proof.
  induction s1 as [|c s1 IH]; intros s2 t1 t2 Hw H1 H2; simpl in *.
  - subst t1. exact H2.
  - unfold swidth in Hw. simpl in Hw. destruct H1 as [Hd H1].
    rewrite firstn_app, skipn_app.
    replace (ix_r c - length t1) with 0 by lia.
    rewrite firstn_O, app_nil_r.
    split; [exact Hd |].
    apply IH; [| exact H1 | exact H2].
    unfold swidth. rewrite skipn_length. lia.
Qed.

Lemma scanon_length : forall s t,
  shape_dense s t -> length (scanon s t) = swidth s.
Proof.
  intros s t Hd. apply enumShape_tuple_length. apply scanon_in_shape. exact Hd.
Qed.

Lemma shape_dense_length : forall s t, shape_dense s t -> length t = swidth s.
Proof.
  induction s as [|c s IH]; intros t H; simpl in *.
  - subst. reflexivity.
  - destruct H as [[Hl _] H2]. unfold swidth. simpl.
    rewrite <- (firstn_skipn (ix_r c) t), app_length, Hl.
    f_equal. apply IH. exact H2.
Qed.

Lemma lsum_app : forall l1 l2, lsum (l1 ++ l2) = lsum l1 + lsum l2.
Proof. induction l1 as [|a l1 IH]; intro l2; simpl; [reflexivity | rewrite IH; lia]. Qed.

Lemma widths_app : forall l1 l2, widths (l1 ++ l2) = widths l1 + widths l2.
Proof. intros. unfold widths. rewrite map_app. apply lsum_app. Qed.

(* --- all groups ------------------------------------------------------ *)

Lemma groups_quotient : forall comm ro gs t,
  (forall g, In g gs -> fst (ro (g_name g)) = g_shape g) ->
  In t (enumShape (concat (map dshape gs))) ->
  shape_dense (concat (map (group_shape comm) gs)) t /\
  Permutation (vals (as_of ro (concat (map expand gs))) t)
              (vals (as_of ro (concat (map expand gs)))
                    (scanon (concat (map (group_shape comm) gs)) t)).
Proof.
  intros comm ro gs. induction gs as [|g gs IH]; intros t Hro Ht; simpl in *.
  - destruct Ht as [<- | []]. split; [reflexivity | apply Permutation_refl].
  - apply enumShape_app in Ht as (t1 & t2 & -> & H1 & H2).
    pose proof (Hro g (or_introl eq_refl)) as Hg.
    destruct (group_quotient comm ro g t1 Hg H1) as [Hd1 HP1].
    destruct (IH t2 (fun g' Hg' => Hro g' (or_intror Hg')) H2) as [Hd2 HP2].
    pose proof (enumShape_tuple_length _ _ H1) as L1.
    assert (W1 : swidth (group_shape comm g) = length t1)
      by (rewrite swidth_group_shape; symmetry; exact L1).
    split.
    + apply shape_dense_app; assumption.
    + rewrite scanon_app by exact W1.
      unfold as_of. rewrite map_app. fold (as_of ro (expand g)).
      fold (as_of ro (concat (map expand gs))).
      assert (Wv : widths (as_of ro (expand g)) = length t1).
      { rewrite widths_as_of.
        - unfold dense_shape, expand. rewrite map_repeat'. simpl. exact (eq_sym L1).
        - intros x s Hin. unfold expand in Hin.
          apply repeat_spec in Hin. injection Hin as -> ->. exact Hg. }
      rewrite vals_app by exact Wv.
      rewrite vals_app by (rewrite Wv; symmetry; rewrite (scanon_length _ _ Hd1); exact W1).
      apply Permutation_app; assumption.
Qed.

(* --- the licence discharges the kernel ------------------------------ *)

Lemma apply_kernel_canon : forall f ro b t,
  kwf f -> k_arity f = length b ->
  (forall x s, In (x, s) b -> fst (ro x) = s) ->
  In t (enumShape (dense_shape b)) ->
  shape_dense (out_shape (k_comm f) b) t /\
  apply_kernel f (as_of ro b) (scanon (out_shape (k_comm f) b) t)
  = apply_kernel f (as_of ro b) t.
Proof.
  intros f ro b t Hwf Har Hro Ht.
  rewrite dense_shape_groups in Ht.
  assert (Hro' : forall g, In g (rle b) -> fst (ro (g_name g)) = g_shape g).
  { intros g Hg. apply Hro. rewrite <- (rle_expand b).
    apply in_concat. exists (expand g). split; [apply in_map; exact Hg |].
    unfold expand. destruct (g_mult g) eqn:Em.
    - (* a group of multiplicity 0 does not arise from rle *)
      exfalso. clear - Hg Em. induction b as [|[x s] b IH]; simpl in Hg; [exact Hg |].
      destruct (rle b) as [|g' gs]; simpl in Hg.
      + destruct Hg as [<- | []]. discriminate.
      + destruct (Nat.eq_dec x (g_name g')).
        * destruct (shape_eq_dec s (g_shape g')); simpl in Hg;
            destruct Hg as [<- | Hg]; try discriminate; apply IH; try (right; exact Hg); exact Hg.
        * simpl in Hg. destruct Hg as [<- | Hg]; [discriminate | apply IH; exact Hg].
    - simpl. left. reflexivity. }
  destruct (groups_quotient (k_comm f) ro (rle b) t Hro' Ht) as [Hd HP].
  unfold out_shape. split; [exact Hd |].
  rewrite <- (rle_expand b) at 1 3. unfold apply_kernel.
  destruct (k_comm f) eqn:Ec.
  - pose proof (Ksym_Kcls (k_arity f) (k_body f) (kwf_Ksym f Hwf Ec)) as Hcls.
    apply Hcls. unfold scls, tab.
    assert (Lv : length (vals (as_of ro (concat (map expand (rle b)))) t) = k_arity f).
    { rewrite vals_length. unfold as_of. rewrite map_length, rle_expand. exact (eq_sym Har). }
    assert (Lv' : length (vals (as_of ro (concat (map expand (rle b))))
                               (scanon (concat (map (group_shape true) (rle b))) t))
                  = k_arity f).
    { rewrite vals_length. unfold as_of. rewrite map_length, rle_expand. exact (eq_sym Har). }
    rewrite (tab_nth_r _ _ _ Lv), (tab_nth_r _ _ _ Lv').
    apply isort_respects. apply Permutation_sym. exact HP.
  - (* no claim, no quotient: the blockwise canonical form is the identity *)
    assert (E : concat (map (group_shape false) (rle b)) = concat (map dshape (rle b))).
    { reflexivity. }
    rewrite E. rewrite scanon_fixes by exact Ht. reflexivity.
Qed.

(* THE THEOREM.  For a well-typed application, the typed output is the  *)
(* quotient: its shape is out_shape; reading it canonically at ANY cell *)
(* of the dense output gives the dense kernel value; and it holds one   *)
(* cell per orbit, which is exactly what the object's program asks.     *)
Theorem typed_app_is_quotient : forall G rho xs f s',
  env_ok G rho ->
  has_ty G (EAppM (EMethod xs) f) (TComp s') ->
  exists b,
    bind_names G xs = Some b /\
    s' = out_shape (k_comm f) b /\
    fst (den rho (EAppM (EMethod xs) f)) = s' /\
    (forall t, In t (enumShape (dense_shape b)) ->
       shape_dense s' t /\
       qread (den rho (EAppM (EMethod xs) f)) t
       = snd (den rho (EAppM (EMethod xs) f)) t) /\
    (forall k : list nat -> nat,
       length (trace (object_prog s') k) = shapeCard s').
Proof.
  intros G rho xs f s' Hok Ht.
  destruct (ty_appM_method _ _ _ _ Ht) as (b & Hb & Hwf & Har & ->).
  exists b. split; [exact Hb |]. split; [reflexivity |].
  pose proof (bind_arrs_as_of G rho xs b Hok Hb) as Hl.
  pose proof (binding_shapes G rho xs b Hok Hb) as Hsh.
  simpl. rewrite Hl. rewrite (shapes_of_as_of _ _ Hsh). simpl.
  split; [reflexivity |]. split.
  - intros t Ht'. unfold qread. simpl.
    destruct (apply_kernel_canon f (rd rho) b t Hwf Har Hsh Ht') as [Hd HE].
    split; [exact Hd | exact HE].
  - intro k. apply object_work.
Qed.

(* --- pins --------------------------------------------------------- *)

(* method_for(A, A) <@> f with f comm, A over Idx<n>: SymIdx<2, n> *)
Theorem covariance_shape : forall x n,
  out_shape true [(x, [mkIx 1 0 n]); (x, [mkIx 1 0 n])] = [mkIx 2 0 n]
  /\ shapeCard [mkIx 2 0 n] = C (n + 1) 2.
Proof.
  intros x n. split.
  - unfold out_shape. cbn [rle g_name g_shape].
    destruct (Nat.eq_dec x x) as [_ | N]; [| contradiction].
    destruct (shape_eq_dec [mkIx 1 0 n] [mkIx 1 0 n]) as [_ | N]; [| contradiction].
    reflexivity.
  - rewrite shapeCard_binom. simpl. rewrite Nat.mul_1_r. f_equal. lia.
Qed.

Example covariance_cells_5 : shapeCard [mkIx 2 0 5] = 15.
Proof. vm_compute. reflexivity. Qed.

(* three singleton groups: (A, B, A) licenses nothing (8.3) *)
Example aba_unlicensed : forall n,
  out_shape true [(0, [mkIx 1 0 n]); (1, [mkIx 1 0 n]); (0, [mkIx 1 0 n])]
  = [mkIx 1 0 n; mkIx 1 0 n; mkIx 1 0 n].
Proof. intro n. reflexivity. Qed.

(* the outer product and co-iteration of the same operands have         *)
(* different output ranks: confusing them changes the result's rank     *)
Lemma dense_shape_all : forall b s,
  all_shape s b -> swidth (dense_shape b) = length b * swidth s.
Proof.
  intros b s Hall. unfold dense_shape.
  induction Hall as [|[x s'] b Hs Hall IH]; simpl; [reflexivity |].
  simpl in Hs. subst s'. rewrite swidth_app, IH. lia.
Qed.

Theorem method_vs_zip_rank : forall comm b s,
  2 <= length b -> 1 <= swidth s -> all_shape s b ->
  swidth (out_shape comm b) <> swidth s.
Proof.
  intros comm b s Hn Hw Hall.
  rewrite swidth_out_shape, (dense_shape_all b s Hall). nia.
Qed.

(* ===================================================================== *)
(* PART G.  THE FREE-MODEL BRIDGE: deduction is a licence source.        *)
(* A binary kernel whose body is a term equal modulo the declared laws   *)
(* to its own swap (BladeDeduceExact's teq -- what src/Deduce.fs's       *)
(* mirror walk decides, proved exact there) is a well-formed comm        *)
(* kernel under every interpretation of the operator symbols that        *)
(* respects the laws.                                                    *)
(* ===================================================================== *)

Fixpoint leaf_in (i : nat) (t : tm) : Prop :=
  match t with
  | Leaf j => i = j
  | App1 _ t1 => leaf_in i t1
  | App2 _ t1 t2 => leaf_in i t1 \/ leaf_in i t2
  end.

Lemma eval_local : forall (leaf leaf' : nat -> nat) k t,
  (forall i, leaf_in i t -> leaf i = leaf' i) ->
  eval leaf k t = eval leaf' k t.
Proof.
  intros leaf leaf' k t. induction t as [j | s t1 IH1 | s t1 IH1 t2 IH2]; intro H;
    cbn [eval].
  - apply H. reflexivity.
  - rewrite IH1; [reflexivity | intros i Hi; apply H; exact Hi].
  - rewrite IH1, IH2; [reflexivity | intros i Hi; apply H; right; exact Hi
                                   | intros i Hi; apply H; left; exact Hi].
Qed.

(* a permutation of two positions is the identity or the swap *)
Lemma perm_pair_2 : forall s s',
  perm_pair 2 s s' ->
  (s 0 = 0 /\ s 1 = 1) \/ (s 0 = 1 /\ s 1 = 0).
Proof.
  intros s s' (Hs & _ & Hi & _).
  pose proof (Hs 0 ltac:(lia)). pose proof (Hs 1 ltac:(lia)).
  pose proof (Hi 0 ltac:(lia)). pose proof (Hi 1 ltac:(lia)).
  destruct (s 0) as [|[|a]] eqn:E0; destruct (s 1) as [|[|b]] eqn:E1; try lia.
Qed.

Definition term_kernel (k : qry -> nat) (t : tm) : kernel :=
  mkK 2 (fun v => eval v k t) true.

Theorem deduced_comm_is_licence : forall (comm : nat -> bool) (k : qry -> nat) (t : tm),
  Kc comm k ->
  (forall i, leaf_in i t -> i < 2) ->
  teq comm (ren (sw 0 1) t) t ->
  kwf (term_kernel k t).
Proof.
  intros comm k t Hk Hleaf Hteq. unfold kwf, term_kernel. simpl. split.
  - intros v v' H. apply eval_local. intros i Hi. apply H. apply Hleaf. exact Hi.
  - intros _ s s' Hp v. unfold invariant_under.
    destruct (perm_pair_2 s s' Hp) as [[E0 E1] | [E0 E1]].
    + apply eval_local. intros i Hi. apply Hleaf in Hi.
      destruct i as [|[|i]]; [rewrite E0 | rewrite E1 | lia]; reflexivity.
    + transitivity (eval v k (ren (sw 0 1) t)).
      * rewrite eval_ren. apply eval_local. intros i Hi. apply Hleaf in Hi.
        destruct i as [|[|i]]; [rewrite E0 | rewrite E1 | lia]; reflexivity.
      * exact (teq_sound comm _ _ Hteq v k Hk).
Qed.
