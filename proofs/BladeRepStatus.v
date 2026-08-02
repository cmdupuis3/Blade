(* ===================================================================== *)
(* BladeRepStatus.v -- soundness of the typed representation-status      *)
(* transfer table (retired equivariance-in-types plan, Stage B; B4 asks  *)
(* for exactly this file).  src/ml/compiler/MLEquiv.fs's `judge` walks a *)
(* `where ml.equiv(G)`-certified body bottom-up over TypedAst nodes and  *)
(* assigns each subexpression a status                                   *)
(*   Rep spec  -- "transforms as the representation named spec"          *)
(*   Inv shape -- "invariant" (held fixed by the group action)           *)
(*   Opaque    -- unclassified (the PBottom default; licenses nothing)   *)
(* by a fixed per-primitive TABLE (MLEquiv.fs:522-1126, the certificate  *)
(* being "a conditional theorem", file header lines 9-14).  This file    *)
(* proves, ABSTRACTLY -- no concrete group, no concrete representation,  *)
(* matching the B4 brief -- that every table row which ever answers Rep  *)
(* or Inv (never Opaque) is the soundness theorem for a genuine semantic *)
(* fact about functions of a G-set input.  Opaque needs no law: see the  *)
(* closing remark.                                                       *)
(*                                                                        *)
(* Contents, one theorem per transfer rule (MLEquiv.fs line refs in each *)
(* theorem's own comment):                                               *)
(*   neg_covariant, sum_covariant, diff_covariant                        *)
(*                       MLEquiv.fs:554 (unary pass-through) and 573-575 *)
(*                       (Rep + Rep / Rep - Rep, same spec).  diff is    *)
(*                       sum read through negation, as the B4 brief      *)
(*                       suggests ("or derive from sum + negation") --   *)
(*                       both ride on three lemmas (rho_zero, rho_neg,   *)
(*                       group_idempotent_is_zero) that get from rho     *)
(*                       being additive to rho commuting with 0 and -.   *)
(*   scalar_scale_covariant                                              *)
(*                       MLEquiv.fs:586 (Inv InvScalar * Rep -> Rep, and *)
(*                       symmetrically for OpDiv): scaling a covariant   *)
(*                       function by an INVARIANT SCALAR function stays  *)
(*                       covariant.                                      *)
(*   inv_closure_binop                                                   *)
(*                       MLEquiv.fs:591-592 (Inv op Inv -> Inv, every    *)
(*                       operator): pointwise combination of invariants  *)
(*                       by ANY binary op is invariant.                  *)
(*   const_invariant                                                     *)
(*                       MLEquiv.fs:527 (ExprLit -> Inv InvScalar).      *)
(*   certified_call_sound, certified_call_inv_arg_sound                  *)
(*                       MLEquiv.fs:1033-1049 (the pinned-callee arm):   *)
(*                       an equivariant F composed with a covariant f is *)
(*                       covariant; the second theorem adds the extra    *)
(*                       invariant argument (weight buffer) every real   *)
(*                       ml.* op in the table actually carries.          *)
(*   inv_call_inv                                                        *)
(*                       MLEquiv.fs:1078-1102 (the uncertified-callee    *)
(*                       arm): an ARBITRARY, uncertified F applied to    *)
(*                       all-invariant arguments is invariant, at every  *)
(*                       arity (modeled over a list of arguments).       *)
(*   branch_agree_sound                                                  *)
(*                       MLEquiv.fs:594-603 (ExprIf + joinStatus): an    *)
(*                       invariant condition with both arms transforming *)
(*                       as the SAME rho makes the if-composite          *)
(*                       transform as that rho.                          *)
(*   equivariance_downward                                               *)
(*                       the A3/D1 meet direction's soundness anchor     *)
(*                       (retired equivariance-in-types plan Stage A3     *)
(*                       ml.restrict + branching rules, D1 collision     *)
(*                       = subgroup meet -- NOT YET a MLEquiv table       *)
(*                       row (A3 is "named-not-shipped" per the plan);    *)
(*                       this is the theorem that row will cite.          *)
(*                                                                        *)
(* Coq 8.18, stdlib only.  Self-contained: no BladeDeduce/BladeLowering   *)
(* import.  That tower's `invariant_under`/`antiinvariant_under` model    *)
(* invariance of a KERNEL under permuting its OWN ARGUMENT LIST (the      *)
(* comm/antisymm parity table, src/Deduce.fs); this file models           *)
(* invariance/equivariance of a FUNCTION OF A G-SET INPUT under the group *)
(* ACTING ON ITS DOMAIN (the rep-status table, src/ml/compiler/MLEquiv.fs)*)
(* -- a different mathematical object, so the two files share vocabulary  *)
(* ("invariant") but not a definition, and this file imports nothing      *)
(* from that tower.                                                       *)
(* ===================================================================== *)

Require Import List.

(* ---------------------------------------------------------------------- *)
(* Shared vocabulary.  G acts on X via `act`.  A function f : X -> A is   *)
(* INVARIANT if it cannot see the action; given a representation rho of G *)
(* on V, f : X -> V TRANSFORMS AS rho if moving the input by g is the     *)
(* same as applying rho g to the output -- exactly MLEquiv.fs:9-14's      *)
(* "conditional theorem" reading of Inv / Rep spec.                       *)
(* ---------------------------------------------------------------------- *)

Definition invariant (G X A : Type) (act : G -> X -> X) (f : X -> A) : Prop :=
  forall (g : G) (x : X), f (act g x) = f x.

Definition transforms_as (G X V : Type)
    (act : G -> X -> X) (rho : G -> V -> V) (f : X -> V) : Prop :=
  forall (g : G) (x : X), f (act g x) = rho g (f x).

(* ===================================================================== *)
(* neg_covariant / sum_covariant / diff_covariant                        *)
(* ===================================================================== *)

Section SumDiff.
  Variables G X V : Type.
  Variable act : G -> X -> X.
  Variable rho : G -> V -> V.
  Variable add : V -> V -> V.
  Variable zero : V.
  Variable neg : V -> V.

  (* V is the abelian group the block-diagonal action lives on -- a real *)
  (* or rational vector space is overkill per the B4 brief; these are    *)
  (* the only laws this section needs).                                  *)
  Hypothesis Hassoc  : forall a b c, add (add a b) c = add a (add b c).
  Hypothesis Hcomm   : forall a b, add a b = add b a.
  Hypothesis Hzero_l : forall a, add zero a = a.
  Hypothesis Hneg_r  : forall a, add a (neg a) = zero.

  (* rho is additive-linear: the "linear in V" premise (plan B1). *)
  Hypothesis Hadd : forall g u v, rho g (add u v) = add (rho g u) (rho g v).

  (* ---- bookkeeping: consequences of V being an abelian group ---- *)
  Lemma add_zero_r : forall a, add a zero = a.
  Proof. intro a. rewrite Hcomm. apply Hzero_l. Qed.

  Lemma neg_l : forall a, add (neg a) a = zero.
  Proof. intro a. rewrite Hcomm. apply Hneg_r. Qed.

  Lemma group_idempotent_is_zero : forall a, add a a = a -> a = zero.
  Proof.
    intros a Ha.
    assert (Hgen : add (neg a) (add a a) = a).
    { rewrite <- Hassoc, neg_l, Hzero_l. reflexivity. }
    rewrite Ha in Hgen. rewrite neg_l in Hgen. symmetry. exact Hgen.
  Qed.

  Lemma add_cancel_l : forall a b, add a b = zero -> b = neg a.
  Proof.
    intros a b Hab.
    assert (Hstep : add (neg a) (add a b) = add (neg a) zero)
      by (rewrite Hab; reflexivity).
    rewrite <- Hassoc, neg_l, Hzero_l, add_zero_r in Hstep.
    exact Hstep.
  Qed.

  (* rho commutes with 0 and with negation -- consequences of "rho g" *)
  (* being additive for EVERY g, not extra hypotheses.                *)
  Lemma rho_zero : forall g, rho g zero = zero.
  Proof.
    intro g. apply group_idempotent_is_zero.
    rewrite <- Hadd, Hzero_l. reflexivity.
  Qed.

  Lemma rho_neg : forall g v, rho g (neg v) = neg (rho g v).
  Proof.
    intros g v. apply add_cancel_l.
    rewrite <- Hadd, Hneg_r. apply rho_zero.
  Qed.

  (* MLEquiv.fs:554, ExprUnaryOp passes the child's status straight       *)
  (* through unchanged (`j inner`) for EVERY unary operator, negation     *)
  (* included.  This is the semantic fact that makes the pass-through     *)
  (* sound for negation: rho commutes with V's negation because rho g is  *)
  (* additive.                                                            *)
  Theorem neg_covariant : forall f : X -> V,
    transforms_as G X V act rho f ->
    transforms_as G X V act rho (fun x => neg (f x)).
  Proof.
    unfold transforms_as. intros f Hf g x. simpl.
    rewrite (Hf g x). symmetry. apply rho_neg.
  Qed.

  (* MLEquiv.fs:573-575, `Rep s1, Rep s2, OpAdd, s1 = s2 -> Rep s1`: the   *)
  (* sum of two rho-covariant functions is rho-covariant, because rho is  *)
  (* additive-linear.                                                     *)
  Theorem sum_covariant : forall f h : X -> V,
    transforms_as G X V act rho f -> transforms_as G X V act rho h ->
    transforms_as G X V act rho (fun x => add (f x) (h x)).
  Proof.
    unfold transforms_as. intros f h Hf Hh g x. simpl.
    rewrite (Hf g x), (Hh g x). symmetry. apply Hadd.
  Qed.

  (* MLEquiv.fs:573-575, the OpSub row of the same table entry: a - b is  *)
  (* add a (neg b), so this is sum_covariant read through neg_covariant   *)
  (* -- "derive from sum + negation," not a second primitive law.         *)
  Theorem diff_covariant : forall f h : X -> V,
    transforms_as G X V act rho f -> transforms_as G X V act rho h ->
    transforms_as G X V act rho (fun x => add (f x) (neg (h x))).
  Proof.
    intros f h Hf Hh.
    exact (sum_covariant f (fun x => neg (h x)) Hf (neg_covariant h Hh)).
  Qed.
End SumDiff.

(* ===================================================================== *)
(* scalar_scale_covariant                                                *)
(* ===================================================================== *)

Section ScalarScale.
  Variables G X V S : Type.
  Variable act : G -> X -> X.
  Variable rho : G -> V -> V.
  Variable smul : S -> V -> V.

  (* rho is S-linear (the module structure a representation carries).    *)
  Hypothesis Hsmul : forall g c v, rho g (smul c v) = smul c (rho g v).

  (* MLEquiv.fs:586, `Rep s, Inv InvScalar, (OpMul | OpDiv) -> Rep s` (and *)
  (* symmetrically `Inv InvScalar, Rep s, OpMul`): scaling a rho-covariant *)
  (* function by an INVARIANT SCALAR FUNCTION stays covariant. The         *)
  (* InvShape guard (MLEquiv.fs:95-103, `nonScalarScale`) is exactly what  *)
  (* makes `invariant .. s` -- s a PROVEN SCALAR, not merely an invariant  *)
  (* aggregate -- the right hypothesis here: an elementwise product with   *)
  (* an invariant ARRAY of matching extent is a diagonal matrix that does  *)
  (* NOT commute with rho in general, so that case is rejected in the      *)
  (* table rather than mis-typed by this theorem.                          *)
  Theorem scalar_scale_covariant : forall (s : X -> S) (f : X -> V),
    invariant G X S act s -> transforms_as G X V act rho f ->
    transforms_as G X V act rho (fun x => smul (s x) (f x)).
  Proof.
    unfold invariant, transforms_as.
    intros s f Hs Hf g x. simpl.
    rewrite (Hs g x), (Hf g x). symmetry. apply Hsmul.
  Qed.
End ScalarScale.

(* ===================================================================== *)
(* inv_closure_binop                                                     *)
(* ===================================================================== *)

Section InvClosure.
  Variables G X A B W : Type.
  Variable act : G -> X -> X.
  Variable op : A -> B -> W.

  (* MLEquiv.fs:591-592, `Inv shl, Inv shr, _ -> Ok (Inv ..)`: this row    *)
  (* fires for EVERY operator (the wildcard `_`), so the soundness fact    *)
  (* it needs is operator-agnostic: pointwise combination of two           *)
  (* invariant functions by ANY binary operation is invariant, because     *)
  (* neither operand ever sees the action.                                 *)
  Theorem inv_closure_binop : forall (f : X -> A) (h : X -> B),
    invariant G X A act f -> invariant G X B act h ->
    invariant G X W act (fun x => op (f x) (h x)).
  Proof.
    unfold invariant. intros f h Hf Hh g x. simpl.
    rewrite (Hf g x), (Hh g x). reflexivity.
  Qed.
End InvClosure.

(* ===================================================================== *)
(* const_invariant                                                       *)
(* ===================================================================== *)

(* MLEquiv.fs:527, `ExprLit _ -> Ok (Inv InvScalar)`: a constant function *)
(* trivially cannot see the action.                                      *)
Theorem const_invariant : forall (G X A : Type) (act : G -> X -> X) (c : A),
  invariant G X A act (fun _ : X => c).
Proof. unfold invariant. intros. reflexivity. Qed.

(* ===================================================================== *)
(* certified_call_sound / certified_call_inv_arg_sound                   *)
(* ===================================================================== *)

Section CertifiedCall.
  Variables G X V W : Type.
  Variable act : G -> X -> X.
  Variable rho : G -> V -> V.
  Variable sigma : G -> W -> W.

  (* MLEquiv.fs:1033-1049, the pinned-callee arm of `judgeApp`: F carries  *)
  (* a `where ml.equiv(G)` certificate relating its Rep-typed parameter's   *)
  (* spec (rho) to its Rep-typed return's spec (sigma) -- F is             *)
  (* equivariant, i.e. F commutes with the two actions -- and              *)
  (* `requireRep` demands every Rep-typed argument already carry the       *)
  (* matching status. The composite F . f is then trusted to transform as  *)
  (* sigma, with no re-derivation of F's body: this is the interprocedural *)
  (* TRUST the certificate buys (retired equivariance-in-types plan B2,    *)
  (* "pinned callee -> declared signature").                                *)
  Theorem certified_call_sound : forall (F : V -> W) (f : X -> V),
    (forall g v, F (rho g v) = sigma g (F v)) ->
    transforms_as G X V act rho f ->
    transforms_as G X W act sigma (fun x => F (f x)).
  Proof.
    unfold transforms_as. intros F f HF Hf g x. simpl.
    rewrite (Hf g x). apply HF.
  Qed.

  (* The variant `judgeApp` actually exercises at every multi-argument      *)
  (* ml.* call (`derive_linear`, `tensor_product`, ...): F additionally     *)
  (* takes an INVARIANT argument -- a weight buffer or similar -- that      *)
  (* `requireInv` holds fixed. Because c does not move under the action,    *)
  (* the same commutation hypothesis, read at the fixed value c x, carries  *)
  (* the composite through.  (A call with several Rep-typed arguments of    *)
  (* POSSIBLY DIFFERENT representations -- `tensor_product`'s two inputs --  *)
  (* is the same shape iterated argument-by-argument, exactly as            *)
  (* `List.zip cert.Params args` checks each position independently at      *)
  (* MLEquiv.fs:1041-1048; that per-argument independence is what lets a    *)
  (* two-theorem family stand in for the n-ary rule here.)                  *)
  Theorem certified_call_inv_arg_sound : forall (S : Type) (F : V -> S -> W)
      (f : X -> V) (c : X -> S),
    (forall g v s, F (rho g v) s = sigma g (F v s)) ->
    transforms_as G X V act rho f ->
    invariant G X S act c ->
    transforms_as G X W act sigma (fun x => F (f x) (c x)).
  Proof.
    unfold transforms_as, invariant. intros S F f c HF Hf Hc g x. simpl.
    rewrite (Hf g x), (Hc g x). apply HF.
  Qed.
End CertifiedCall.

(* ===================================================================== *)
(* inv_call_inv                                                          *)
(* ===================================================================== *)

Section UncertifiedCall.
  Variables G X A W : Type.
  Variable act : G -> X -> X.

  (* MLEquiv.fs:1078-1102, the uncertified-callee arm of `judgeApp`: no    *)
  (* certificate names F (a builtin, a plain helper, a lambda, an array    *)
  (* read), so the judgment falls back to the conditional-theorem reading  *)
  (* at the argument list alone -- `sts |> List.tryFindIndex (isInv >>     *)
  (* not)` -- `None` (every argument invariant) answers `Inv`.  Modeled    *)
  (* over a LIST of invariant argument functions so one theorem covers     *)
  (* every arity, the way the implementation's fold over `sts` does.       *)
  Theorem inv_call_inv : forall (F : list A -> W) (fs : list (X -> A)),
    Forall (fun f => invariant G X A act f) fs ->
    invariant G X W act (fun x => F (map (fun f => f x) fs)).
  Proof.
    intros F fs Hfs. unfold invariant. intros g x. simpl.
    f_equal.
    apply map_ext_in. intros f Hin.
    rewrite Forall_forall in Hfs.
    exact (Hfs f Hin g x).
  Qed.
End UncertifiedCall.

(* ===================================================================== *)
(* branch_agree_sound                                                    *)
(* ===================================================================== *)

Section BranchAgree.
  Variables G X V : Type.
  Variable act : G -> X -> X.
  Variable rho : G -> V -> V.

  (* MLEquiv.fs:594-603, `ExprIf` under `joinStatus`: the CONDITION must   *)
  (* be invariant (BL4008 otherwise: an if condition inside an             *)
  (* equiv-certified body must be invariant) and the two ARMS must AGREE   *)
  (* on the same rho (`joinStatus`'s `Rep s1, Rep s2 when s1 = s2` row);   *)
  (* the if-composite then transforms as that same rho.  The condition is  *)
  (* modeled as a bool-valued function so `if` is literal Coq `if`.        *)
  Theorem branch_agree_sound : forall (c : X -> bool) (f h : X -> V),
    invariant G X bool act c ->
    transforms_as G X V act rho f -> transforms_as G X V act rho h ->
    transforms_as G X V act rho (fun x => if c x then f x else h x).
  Proof.
    unfold invariant, transforms_as.
    intros c f h Hc Hf Hh g x. simpl.
    rewrite (Hc g x).
    destruct (c x) eqn:E.
    - apply Hf.
    - apply Hh.
  Qed.
End BranchAgree.

(* ===================================================================== *)
(* equivariance_downward                                                 *)
(* ===================================================================== *)

Section SubgroupRestriction.
  Variables G X V : Type.
  Variable e : G.
  Variable gop : G -> G -> G.
  Variable ginv : G -> G.
  Variable act : G -> X -> X.
  Variable rho : G -> V -> V.
  Variable H : G -> Prop.

  (* G is a genuine group -- included for faithfulness to "a group G ..." *)
  (* even though none of this section's theorems need associativity or    *)
  (* invertibility of G itself (only the ACTION's identity/composition     *)
  (* laws, below, are load-bearing for the corollary).                    *)
  Hypothesis Gassoc : forall a b c, gop (gop a b) c = gop a (gop b c).
  Hypothesis Gid_l  : forall a, gop e a = a.
  Hypothesis Gid_r  : forall a, gop a e = a.
  Hypothesis Ginv_l : forall a, gop (ginv a) a = e.
  Hypothesis Ginv_r : forall a, gop a (ginv a) = e.

  (* H is a genuine SUBGROUP predicate: contains the identity, closed      *)
  (* under the group operation and under inverse -- the standard closure   *)
  (* conditions.  `H_op`/`H_inv` are unused by `equivariance_downward`     *)
  (* itself (the direction MLEquiv's A3/D1 restriction actually needs is   *)
  (* pure quantifier weakening, below); they are kept because the theorem  *)
  (* would be dishonest without them -- restricting an equivariance claim  *)
  (* to an arbitrary SUBSET of G, not a subgroup, is not the fact          *)
  (* `ml.restrict`'s decomposition (O(3) irreps -> a registered point       *)
  (* group's irreps) needs, since the target of that decomposition is       *)
  (* always a genuine group.                                                *)
  Hypothesis H_id  : H e.
  Hypothesis H_op  : forall a b, H a -> H b -> H (gop a b).
  Hypothesis H_inv : forall a, H a -> H (ginv a).

  (* rho and act respect identity and composition -- the representation    *)
  (* premise the B4 brief asks for -- rho respects identity and             *)
  (* composition -- used below to show the restriction is not just a        *)
  (* relabeling: rho|_H and act|_H are again a representation and a group   *)
  (* action, of H.                                                          *)
  Hypothesis Hact_id   : forall x, act e x = x.
  Hypothesis Hact_comp : forall g1 g2 x, act (gop g1 g2) x = act g1 (act g2 x).
  Hypothesis Hrho_id   : forall v, rho e v = v.
  Hypothesis Hrho_comp : forall g1 g2 v, rho (gop g1 g2) v = rho g1 (rho g2 v).

  Variable f : X -> V.

  (* The soundness anchor for MLEquiv.fs:38-47's asymmetry: a               *)
  (* `PgIrrepsIdx<Point p, ..>` space carries the RESTRICTION of an          *)
  (* O(3)/SO(3) action to the subgroup p <= O(3), so a full-group            *)
  (* equivariance claim licenses the SAME claim read only over H's           *)
  (* elements -- the defining property "forall g, .." trivially               *)
  (* specializes to "forall g, H g -> ..".  This is the theorem                *)
  (* `ml.restrict` + D1's subgroup meet (retired equivariance-in-types plan    *)
  (* Stage A3/D1) will cite once that walker exists; it is not yet a           *)
  (* MLEquiv.fs table row (A3 is "named-not-shipped" per the plan).            *)
  Theorem equivariance_downward :
    transforms_as G X V act rho f ->
    forall g, H g -> forall x, f (act g x) = rho g (f x).
  Proof.
    unfold transforms_as. intros Hf g _ x. exact (Hf g x).
  Qed.

  (* Restricting really does land on a representation of H, not merely a    *)
  (* function labeled by elements of H: rho|_H and act|_H still satisfy      *)
  (* the identity/composition laws, at every pair of H's own elements.       *)
  Corollary subgroup_action_and_representation :
    (forall x, act e x = x) /\
    (forall g1 g2, H g1 -> H g2 -> forall x, act (gop g1 g2) x = act g1 (act g2 x)) /\
    (forall v, rho e v = v) /\
    (forall g1 g2, H g1 -> H g2 -> forall v, rho (gop g1 g2) v = rho g1 (rho g2 v)).
  Proof.
    repeat split.
    - exact Hact_id.
    - intros g1 g2 _ _ x. exact (Hact_comp g1 g2 x).
    - exact Hrho_id.
    - intros g1 g2 _ _ v. exact (Hrho_comp g1 g2 v).
  Qed.
End SubgroupRestriction.

(* ===================================================================== *)
(* Closing remark: why Opaque needs no theorem                           *)
(* ===================================================================== *)
(* Every theorem above backs a table row that ANSWERS Rep or Inv.  The    *)
(* remaining rows of `judge` -- the final `| _ -> Ok Opaque` catch-all     *)
(* (MLEquiv.fs:704) and every arm that falls through to it -- assert       *)
(* NOTHING about the value: `Opaque` is not a claim that fails to be        *)
(* proved here, it is the absence of a claim.  Soundness of the whole       *)
(* table is exactly Propose-implies-a-proof for the rows that DO answer     *)
(* (the theorems above) conjoined with the trivial fact that answering       *)
(* "no claim" is sound for ANY function, by construction -- there is no      *)
(* semantic content to discharge for a Prop that is never asserted. This     *)
(* is why B4's obligation is finite (one theorem per ANSWERING row) even     *)
(* though `judge`'s match has a catch-all: the catch-all is not a 	     *)
(* (row, theorem) pair the way every case above is.                          *)
