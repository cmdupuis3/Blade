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
