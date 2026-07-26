/// Stage-3 symmetry deduction (the deduction triad, docs/plan-implicit-
/// formers-and-deduction.md §3.2) — EARLY TIER: adjacent-pair swap parity of
/// fixed-arity typed kernel bodies, deduced bottom-up from the per-primitive
/// tables. Pure analysis over TypedAst: no unification, no side effects; the
/// consumers (TypeCheck's buildApplyInfo hook and checkFunctionDecl summary)
/// decide what a parity MEANS (validation of a declared `where comm`,
/// pin suggestions). Placed before TypeCheck in the build so typeCheck can
/// invoke it internally (the Zonk pattern).
///
/// The judgment, per the plan (and the adversarial review that shaped it):
/// for the transposition σ of one ADJACENT parameter pair (pi, pj), every
/// subtree gets a Parity — PInv (e[σ] ≡ e), PNeg (e[σ] ≡ −e), or PBottom
/// (unknown). Two per-primitive tables drive it:
///   1. the 3-way SWAP CLASS (comm / antisym / neither), consulted when a
///      binary node's two children are structural MIRRORS of each other
///      under σ (Mirror is a sibling-scoped fact resolved locally at the
///      node — it is not itself a propagating lattice value);
///   2. per-operand SIGN behavior, which propagates a child's PNeg upward:
///      * and / are sign-multiplicative in each operand (PNeg·PNeg = PInv —
///      the (a−b)*(a−b) case); + and − require both children to transform
///      the same way; comparisons and logicals absorb sign; %, ^ and every
///      unlisted node are PBottom (the closed-world default that keeps the
///      analysis sound as node kinds are added).
///
/// Soundness bias: PInv/PNeg are only ever produced by the finite rules
/// below; anything unrecognized — exotic node kinds, reduce over non-(+)
/// kernels, lambdas with fresh binders inside mirror candidates — collapses
/// to PBottom, which downstream means "no claim": dense storage, no
/// suggestion, no validation verdict. Wrong-guess state is dense, never
/// compact-and-corrupt.
module Blade.Deduce

open Blade.Ast
open Blade.Types
open Blade.IR
open Blade.TypedAst

/// Swap-parity of a kernel body under transposition of one adjacent
/// parameter pair.
type Parity =
    | PInv
    | PNeg
    | PBottom

/// Table 1: swap class of a primitive binary op under operand exchange.
/// Mirrors the boolean comm tables (TypeCheck.inferApply's section arm /
/// Lowering.lowerTypedSection) and extends `-` to antisymmetric — the 3-way
/// classification the plan's §2.3 calls for. `/`, `%`, `^`, comparisons:
/// neither.
let private opSwapClass (op: BinOp) : Parity =
    match op with
    | OpAdd | OpMul | OpEq | OpNeq | OpAnd | OpOr -> PInv
    | OpSub -> PNeg
    | _ -> PBottom

/// Structural equality of `l` and `r` MODULO the pair swap: does applying
/// σ = (pi pj) to l yield r, node for node? Vars compare by VarId (binder
/// identity, never surface name — match-arm rebinding makes names unsafe);
/// the swapped pair cross-matches; every other var must be the SAME id.
/// Unknown node kinds never mirror (conservative).
let rec private mirrorEq (pi: IRId) (pj: IRId) (l: TypedExpr) (r: TypedExpr) : bool =
    match l.Kind, r.Kind with
    | TExprVar (_, idL, _), TExprVar (_, idR, _) ->
        (idL = pi && idR = pj) || (idL = pj && idR = pi)
        || (idL = idR && idL <> pi && idL <> pj)
    | TExprLit a, TExprLit b -> a = b
    | TExprSection a, TExprSection b -> a = b
    | TExprBinOp (mA, oA, lA, rA), TExprBinOp (mB, oB, lB, rB) ->
        mA = mB && oA = oB && mirrorEq pi pj lA lB && mirrorEq pi pj rA rB
    | TExprUnaryOp (oA, iA), TExprUnaryOp (oB, iB) ->
        oA = oB && mirrorEq pi pj iA iB
    | TExprApp (fA, aA), TExprApp (fB, aB) ->
        aA.Length = aB.Length
        && mirrorEq pi pj fA fB
        && List.forall2 (mirrorEq pi pj) aA aB
    | TExprReduce (aA, kA, iA), TExprReduce (aB, kB, iB) ->
        mirrorEq pi pj aA aB
        && kernelEq kA kB
        && (match iA, iB with
            | None, None -> true
            | Some x, Some y -> mirrorEq pi pj x y
            | _ -> false)
    | TExprExtents aA, TExprExtents aB -> mirrorEq pi pj aA aB
    | TExprIndex (aA, iA, _), TExprIndex (aB, iB, _) ->
        iA.Length = iB.Length
        && mirrorEq pi pj aA aB
        && List.forall2 (mirrorEq pi pj) iA iB
    | TExprTuple aA, TExprTuple aB ->
        aA.Length = aB.Length && List.forall2 (mirrorEq pi pj) aA aB
    | TExprIf (cA, tA, eA), TExprIf (cB, tB, eB) ->
        mirrorEq pi pj cA cB && mirrorEq pi pj tA tB && mirrorEq pi pj eA eB
    | TExprField (oA, fA, _), TExprField (oB, fB, _) ->
        fA = fB && mirrorEq pi pj oA oB
    | _ -> false

/// Reduce kernels inside mirror candidates: only a literal operator section
/// or the SAME named reference compares equal (a lambda's fresh binder ids
/// defeat structural comparison — conservative false).
and private kernelEq (a: TypedExpr) (b: TypedExpr) : bool =
    match a.Kind, b.Kind with
    | TExprSection x, TExprSection y -> x = y
    | TExprVar (_, ia, _), TExprVar (_, ib, _) -> ia = ib
    | _ -> false

/// Table 2: combine child parities through a binary op (the non-mirror
/// case) — the sign chain rule.
let private combineBinOp (op: BinOp) (a: Parity) (b: Parity) : Parity =
    match op with
    | OpMul | OpDiv ->
        // Sign-multiplicative in each operand: (−x)·y = −(x·y),
        // x/(−y) = −(x/y); PNeg·PNeg = PInv — (a−b)*(a−b) is even.
        (match a, b with
         | PInv, PInv -> PInv
         | PInv, PNeg | PNeg, PInv -> PNeg
         | PNeg, PNeg -> PInv
         | _ -> PBottom)
    | OpAdd | OpSub ->
        // Jointly sign-linear: both operands must transform the same way
        // ((−x)+(−y) = −(x+y); mixed parities certify nothing).
        (match a, b with
         | PInv, PInv -> PInv
         | PNeg, PNeg -> PNeg
         | _ -> PBottom)
    | OpEq | OpNeq | OpAnd | OpOr | OpLt | OpLe | OpGt | OpGe ->
        // Boolean results absorb sign: only joint invariance survives.
        (match a, b with PInv, PInv -> PInv | _ -> PBottom)
    | _ ->
        // %, ^, and anything else: invariance only (no literal-exponent
        // cleverness in v1 — ^ lowers to generic pow()).
        (match a, b with PInv, PInv -> PInv | _ -> PBottom)

/// Parity of one subtree under σ = (pi pj). A bare occurrence of either
/// swapped param that no enclosing mirror node accounts for is PBottom.
let rec private parityOf (pi: IRId) (pj: IRId) (e: TypedExpr) : Parity =
    let allInv ps = if ps |> List.forall ((=) PInv) then PInv else PBottom
    match e.Kind with
    | TExprLit _ | TExprSection _ -> PInv
    | TExprVar (_, id, _) -> if id = pi || id = pj then PBottom else PInv
    | TExprBinOp (_, op, l, r) ->
        if mirrorEq pi pj l r then opSwapClass op
        else combineBinOp op (parityOf pi pj l) (parityOf pi pj r)
    | TExprUnaryOp (op, inner) ->
        (match op, parityOf pi pj inner with
         | _, PInv -> PInv
         | (OpNeg | OpReal | OpImag), PNeg -> PNeg   // R-linear: sign passes
         | _ -> PBottom)
    | TExprApp (f, args) ->
        // Invariant only when callee and every argument are invariant. The
        // eta-wrapper-to-summary case is resolved by the CALLER against
        // FuncDeducedPairs; interprocedural sign-linearity is late-tier work.
        allInv (parityOf pi pj f :: (args |> List.map (parityOf pi pj)))
    | TExprReduce (arr, kernel, init) ->
        (match parityOf pi pj arr, init with
         | PInv, None -> PInv
         | PInv, Some i -> (match parityOf pi pj i with PInv -> PInv | _ -> PBottom)
         | PNeg, None when (match kernel.Kind with TExprSection OpAdd -> true | _ -> false) ->
             PNeg   // Σ(−x) = −Σx; reduce(*) and seeded folds certify nothing
         | _ -> PBottom)
    | TExprExtents arr ->
        (match parityOf pi pj arr with PInv -> PInv | _ -> PBottom)
    | TExprIndex (arr, idxs, _) ->
        allInv (parityOf pi pj arr :: (idxs |> List.map (parityOf pi pj)))
    | TExprTuple es -> allInv (es |> List.map (parityOf pi pj))
    | TExprIf (c, t, f) -> allInv ([c; t; f] |> List.map (parityOf pi pj))
    | TExprField (o, _, _) ->
        (match parityOf pi pj o with PInv -> PInv | _ -> PBottom)
    | _ -> PBottom   // closed world: unlisted node kinds certify nothing

/// Deduce the swap parity of each ADJACENT parameter pair of a fixed-arity
/// typed kernel: n params yield n−1 entries (empty for arity < 2). Adjacent
/// pairs match both the Sₙ generator structure and the call site's
/// consecutive-identity grouping (H ∩ Stab), so nothing is lost between
/// kernel parity and storage licensing.
let deduceAdjacentPairs (parms: TypedParam list) (body: TypedExpr) : Parity list =
    match parms with
    | [] | [_] -> []
    | _ ->
        parms
        |> List.pairwise
        |> List.map (fun (a, b) -> parityOf a.VarId b.VarId body)

// ============================================================================
// Late tier: arity-polymorphic (Poly-pack) kernels — the ∀-arity exchange law
// ============================================================================
//
// A pack kernel's adjacent pairs only exist per materialized arity, but the
// canonical head::tail recursion makes a single decl-level check sufficient
// for EVERY arity: g(x1) ⊛ g(x2) ⊛ … ⊛ g(xn) is fully symmetric whenever ⊛
// is associative AND commutative and the base case is the same g as the step
// (the AC-fold induction; the adversarial review verified both directions,
// including that an antisymmetric ⊛ cannot satisfy the SIGNED exchange law —
// so packs only ever claim PInv or PBottom, never PNeg, and pack validation
// can produce no false errors). Wrapper kernels (comoment = mean(prod(a)))
// inherit the property compositionally: an expression is invariant under
// pack permutation when every pack-touching part is a whole-pack call to an
// already-summarized-invariant function.

/// Conservative "does this subtree reference VarId v" — unknown node kinds
/// answer TRUE (assume it does), which makes every consumer of this helper
/// fail toward PBottom.
let rec private usesVar (v: IRId) (e: TypedExpr) : bool =
    match e.Kind with
    | TExprLit _ | TExprSection _ | TExprArity _ -> false
    | TExprVar (_, id, _) -> id = v
    | TExprBinOp (_, _, l, r) -> usesVar v l || usesVar v r
    | TExprUnaryOp (_, i) -> usesVar v i
    | TExprApp (f, args) -> usesVar v f || List.exists (usesVar v) args
    | TExprReduce (a, k, i) ->
        usesVar v a || usesVar v k || (match i with Some x -> usesVar v x | None -> false)
    | TExprExtents a -> usesVar v a
    | TExprIndex (a, idxs, _) -> usesVar v a || List.exists (usesVar v) idxs
    | TExprTuple es | TExprSequence es -> List.exists (usesVar v) es
    | TExprIf (c, t, f) -> usesVar v c || usesVar v t || usesVar v f
    | TExprField (o, _, _) -> usesVar v o
    | _ -> true   // unknown: assume it uses v

/// Unwrap a trivial block (`{ e }` with no statements) around an expression.
let rec private unwrapBlock (e: TypedExpr) : TypedExpr =
    match e.Kind with
    | TExprBlock ([], Some inner) -> unwrapBlock inner
    | _ -> e

/// One arm of the pack-fold template: `{ let head :: tail = pack; EXPR }`.
/// Returns (headId, tailId, EXPR) when the arm has exactly that shape.
let private consArm (packId: IRId) (armBody: TypedExpr) : (IRId * IRId * TypedExpr) option =
    match (unwrapBlock armBody).Kind with
    | TExprBlock ([TStmtLet b], Some e) ->
        (match b.Destructure, b.SubBindings, b.Value.Kind with
         | DSConsRest, [(_, headId, _); (_, tailId, _)], TExprVar (_, vid, _) when vid = packId ->
             Some (headId, tailId, e)
         | _ -> None)
    | _ -> None

/// The ∀-arity pack-fold template check for a Poly-pack function `fname`
/// with pack parameter `packId`:
///
///     match arity(pack) with
///     | 1 -> { let head :: tail = pack; g(head) }
///     | _ -> { let head :: tail = pack; g(head) ⊛ fname(tail) }
///
/// (self-call on either side of ⊛). Returns PInv when ⊛ is associative AND
/// commutative (+ * && ||), the two g's are structurally identical (modulo
/// the two arms' distinct head binders — checked with mirrorEq over that
/// pair), and no g touches a tail or the pack itself. Anything else is
/// PBottom. PNeg is deliberately impossible for packs (no signed exchange
/// law exists — reviewer-verified).
let deducePackFold (fname: string) (packName: string) (packId: IRId) (body: TypedExpr) : Parity =
    let isAcOp op = match op with OpAdd | OpMul | OpAnd | OpOr -> true | _ -> false
    match (unwrapBlock body).Kind with
    | TExprMatch (scrut, [case1; caseN]) ->
        let scrutIsArity =
            match (unwrapBlock scrut).Kind with
            | TExprArity n -> n = packName
            | _ -> false
        let case1Ok =
            match case1.Pattern.Kind, case1.Guard with
            | TPatLit (LitInt 1L), None -> true
            | _ -> false
        let caseNOk =
            match caseN.Pattern.Kind, caseN.Guard with
            | TPatWild, None -> true
            | _ -> false
        if not (scrutIsArity && case1Ok && caseNOk) then PBottom
        else
            match consArm packId case1.Body, consArm packId caseN.Body with
            | Some (h1, t1, baseG), Some (h2, t2, stepExpr) ->
                let selfCallOn (e: TypedExpr) =
                    match e.Kind with
                    | TExprApp ({ Kind = TExprVar (n, _, _) }, [{ Kind = TExprVar (_, aid, _) }]) ->
                        n = fname && aid = t2
                    | _ -> false
                let stepG =
                    match stepExpr.Kind with
                    | TExprBinOp (_, op, l, r) when isAcOp op ->
                        if selfCallOn r then Some l
                        elif selfCallOn l then Some r
                        else None
                    | _ -> None
                match stepG with
                | Some g when mirrorEq h1 h2 baseG g
                              && not (usesVar t1 baseG) && not (usesVar packId baseG)
                              && not (usesVar t2 g) && not (usesVar packId g) ->
                    PInv
                | _ -> PBottom
            | _ -> PBottom
    | _ -> PBottom

/// Compositional pack parity for WRAPPER functions over a Poly pack: is the
/// body invariant under any permutation of the pack's elements? Invariance
/// composes through EVERY operator (no comm/sign table needed — permuting
/// inputs of invariant subvalues changes nothing), so the only base cases
/// are: expressions that never touch the pack (invariant), `arity(pack)`
/// (permutation-invariant by definition), a whole-pack call to a function
/// the `resolver` already summarizes as invariant, and the bare pack itself
/// (unknown). Unknown node kinds that touch the pack are unknown.
let packParityOf (resolver: string -> Parity option) (packId: IRId) (body: TypedExpr) : Parity =
    let rec go (e: TypedExpr) : Parity =
        let allInv es = if es |> List.forall (fun x -> go x = PInv) then PInv else PBottom
        match e.Kind with
        | _ when not (usesVar packId e) -> PInv
        | TExprVar _ -> PBottom   // the bare pack (pack-free vars hit the guard above)
        | TExprApp ({ Kind = TExprVar (fname, _, _) }, args) ->
            let argOk (a: TypedExpr) =
                match a.Kind with
                | TExprVar (_, aid, _) when aid = packId ->
                    (match resolver fname with Some PInv -> true | _ -> false)
                | _ -> go a = PInv
            if args |> List.forall argOk then PInv else PBottom
        | TExprBinOp (_, _, l, r) -> allInv [l; r]
        | TExprUnaryOp (_, i) -> go i
        | TExprReduce (a, k, i) ->
            allInv ([a; k] @ (match i with Some x -> [x] | None -> []))
        | TExprExtents a -> go a
        | TExprIndex (a, idxs, _) -> allInv (a :: idxs)
        | TExprTuple es | TExprSequence es -> allInv es
        | TExprIf (c, t, f) -> allInv [c; t; f]
        | TExprField (o, _, _) -> go o
        | TExprBlock ([], Some inner) -> go inner
        | _ -> PBottom
    go body
