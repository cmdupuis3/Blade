/// Symmetry deduction (the deduction triad), EARLY TIER: adjacent-pair swap
/// parity of fixed-arity typed kernel bodies, deduced bottom-up from
/// per-primitive tables (opSwapClass, combineBinOp) plus interprocedural
/// SIGN-PARITY call summaries (deduceSignParities). Pure analysis over
/// TypedAst; TypeCheck's buildApplyInfo hook and checkFunctionDecl summary
/// decide what a parity MEANS (placed before TypeCheck so it can invoke this internally).
///
/// Transposing one ADJACENT parameter pair (pi, pj) gives each subtree a
/// Parity: PInv (e[swap]=e), PNeg (e[swap]=-e), PConj (e[swap]=conj e), or
/// PBottom -- the closed-world default: "no claim," dense storage, never compact-and-corrupt.
module Blade.Deduce

open Blade.Ast
open Blade.Types
open Blade.IR
open Blade.TypedAst

/// Swap-parity of a kernel body under transposition of one adjacent parameter pair.
///
/// PConj is the Hermitian element: e[swap] = conj(e), the law of
/// `x * conj(y)`. Over complex elements it is NOT invariance -- identity
/// -mirror (comm) storage would answer mirrored reads un-conjugated and
/// sign-mirror (anticomm) storage would answer them negated, both silent
/// corruption -- so it exists to REFUTE those declarations, never to
/// license storage (no compact class is user-writable for it yet). Over
/// REAL elements conj is the identity and the same body IS invariant, so
/// PConj's consumers must type-gate before treating it as a contradiction.
type Parity =
    | PInv
    | PNeg
    | PConj
    | PBottom

/// SIGN-parity of a function body under NEGATING one parameter --
/// interprocedural half of table 2, lifted from primitives to callees.
///
///   SOdd     f(.., -x, ..) = -f(..)   (odd/sign-linear in that parameter)
///   SEven    f(.., -x, ..) =  f(..)   (that parameter's SIGN is irrelevant;
///                                      NOT "the parameter is irrelevant" --
///                                      `extents(row)` is even in `row`)
///   SUnknown no claim.
///
/// Deduced bottom-up like the pair parities, per function in decl order --
/// what lets `mymean(x - y)` come out PNeg, not PBottom, under swap.
type SignParity =
    | SOdd
    | SEven
    | SUnknown

/// Table 1: swap class of a primitive op under operand exchange. Mirrors
/// the boolean comm tables (TypeCheck.inferApply's section arm /
/// Lowering.lowerTypedSection), extended with `-` as antisymmetric (`/`, `%`, `^`, comparisons: neither).
let private opSwapClass (op: BinOp) : Parity =
    match op with
    | OpAdd | OpMul | OpEq | OpNeq | OpAnd | OpOr -> PInv
    | OpSub -> PNeg
    | _ -> PBottom

/// Structural equality of `l` and `r` MODULO the pair swap (pi pj): does
/// transposing l yield r, node for node? Vars compare by VarId, not surface
/// name; the swapped pair cross-matches, others must match id (unknown kinds never mirror).
let rec private mirrorEq (pi: IRId) (pj: IRId) (l: TypedExpr) (r: TypedExpr) : bool =
    match l.Kind, r.Kind with
    | TExprVar (_, idL, _), TExprVar (_, idR, _) ->
        (idL = pi && idR = pj) || (idL = pj && idR = pi)
        || (idL = idR && idL <> pi && idL <> pj)
    | TExprLit a, TExprLit b -> a = b
    | TExprSection a, TExprSection b -> a = b
    | TExprBinOp (mA, oA, lA, rA), TExprBinOp (mB, oB, lB, rB) ->
        mA = mB && oA = oB
        && ((mirrorEq pi pj lA lB && mirrorEq pi pj rA rB)
            // A COMMUTATIVE op (opSwapClass PInv is exactly that set) also
            // mirrors cross-matched: swap(l) = rB op lB equals lB op rB.
            // What certifies `x*conj(y) + conj(x)*y` invariant -- the swap
            // image of each addend is the OTHER one with its factors commuted.
            || (opSwapClass oA = PInv && mirrorEq pi pj lA rB && mirrorEq pi pj rA lB))
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
    // Deliberately NO TExprMatch arm: matches only mirror if PATTERN BINDERS
    // correspond, and this function has no binder-correspondence parameter
    // for that (deducePackFold supplies one by hand) -- `false` is already sound.
    | _ -> false

/// Reduce kernels inside mirror candidates: only a literal operator section
/// or the SAME named reference compares equal (lambda binder ids defeat structural comparison -- conservative false).
and private kernelEq (a: TypedExpr) (b: TypedExpr) : bool =
    match a.Kind, b.Kind with
    | TExprSection x, TExprSection y -> x = y
    | TExprVar (_, ia, _), TExprVar (_, ib, _) -> ia = ib
    | _ -> false

/// Conjugate-mirror: does transposing (pi pj) in `l` yield conj(r), node
/// for node? Sound as a ONE-SIDED certificate because the swap is an
/// involution: swap(l) = conj(r) semantically forces swap(r) = conj(l).
/// conj peels through its own nodes (scalar OpConj and the array-level
/// TExprArrayConjugate) and distributes over the field ops; everything
/// else answers FALSE -- a bare var is never the conjugate of another,
/// so failure lands on PBottom, never a wrong claim.
let rec private conjMirrorEq (pi: IRId) (pj: IRId) (l: TypedExpr) (r: TypedExpr) : bool =
    let peeled (e: TypedExpr) =
        match e.Kind with
        | TExprUnaryOp (OpConj, inner) | TExprArrayConjugate inner -> Some inner
        | _ -> None
    (match peeled r with
     | Some r' -> mirrorEq pi pj l r'   // swap(l) = conj(conj r') = r'
     | None -> false)
    || (match peeled l with
        | Some l' -> mirrorEq pi pj l' r   // swap(conj l') = conj(swap l') = conj(r) iff swap(l') = r
        | None -> false)
    || (match l.Kind, r.Kind with
        // conj is a field automorphism: it distributes over + - * / and
        // negation, so the certificate recurses per operand (cross-matched
        // too when the op commutes -- the same license mirrorEq uses).
        | TExprBinOp (mA, oA, lA, rA), TExprBinOp (mB, oB, lB, rB)
            when mA = mB && oA = oB
                 && (match oA with OpAdd | OpSub | OpMul | OpDiv -> true | _ -> false) ->
            (conjMirrorEq pi pj lA lB && conjMirrorEq pi pj rA rB)
            || (opSwapClass oA = PInv
                && conjMirrorEq pi pj lA rB && conjMirrorEq pi pj rA lB)
        | TExprUnaryOp (OpNeg, a), TExprUnaryOp (OpNeg, b)
        | TExprArrayNegate a, TExprArrayNegate b -> conjMirrorEq pi pj a b
        | _ -> false)

/// Table 2: combine child parities through a binary op (the non-mirror
/// case) -- the sign chain rule.
let private combineBinOp (op: BinOp) (a: Parity) (b: Parity) : Parity =
    match op with
    | OpMul | OpDiv ->
        // Sign-multiplicative in each operand: (-x)*y = -(x*y), x/(-y) =
        // -(x/y); PNeg*PNeg = PInv -- (a-b)*(a-b) is even. conj is a field
        // automorphism, so it is multiplicative too: conj(l)*conj(r) =
        // conj(l*r) -- but ONLY jointly (PConj paired with PInv would need
        // the invariant side provably real, which parities don't track).
        (match a, b with
         | PInv, PInv -> PInv
         | PInv, PNeg | PNeg, PInv -> PNeg
         | PNeg, PNeg -> PInv
         | PConj, PConj -> PConj
         | _ -> PBottom)
    | OpAdd | OpSub ->
        // Jointly sign-linear: both operands must transform the same way
        // ((-x)+(-y) = -(x+y); mixed parities certify nothing). conj
        // distributes over both: conj(l) +/- conj(r) = conj(l +/- r).
        (match a, b with
         | PInv, PInv -> PInv
         | PNeg, PNeg -> PNeg
         | PConj, PConj -> PConj
         | _ -> PBottom)
    | OpEq | OpNeq | OpAnd | OpOr | OpLt | OpLe | OpGt | OpGe ->
        // Boolean results absorb sign: only joint invariance survives.
        (match a, b with PInv, PInv -> PInv | _ -> PBottom)
    | _ ->
        // %, ^, and anything else: invariance only (^ lowers to pow()).
        (match a, b with PInv, PInv -> PInv | _ -> PBottom)

/// Every EXPRESSION carried inside a pattern. Only `TPatGuarded` holds one,
/// but it can sit at any depth of a composite pattern -- walking the whole
/// shape catches what `TypedMatchCase.Guard` alone would miss.
let rec private patGuardExprs (p: TypedPattern) : TypedExpr list =
    match p.Kind with
    | TPatGuarded (inner, g) -> g :: patGuardExprs inner
    | TPatTuple ps -> ps |> List.collect patGuardExprs
    | TPatCons (h, t) -> patGuardExprs h @ patGuardExprs t
    | TPatVariant (_, Some payload, _) -> patGuardExprs payload
    | TPatStruct (_, flds) -> flds |> List.collect (snd >> patGuardExprs)
    | TPatWild | TPatVar _ | TPatLit _ | TPatVariant (_, None, _) -> []

/// Conservative "does this subtree reference VarId v": unknown node kinds
/// answer TRUE, so every consumer of this helper fails toward PBottom / SUnknown.
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
    | TExprMatch (scrut, cases) ->
        // Pattern BINDERS are fresh ids, never `v`; only the scrutinee,
        // guards, and arm bodies can mention it -- precise here (not the
        // blanket TRUE below) so an untouched match reads even/invariant.
        usesVar v scrut
        || cases |> List.exists (fun c ->
               usesVar v c.Body
               || (match c.Guard with Some g -> usesVar v g | None -> false)
               || (patGuardExprs c.Pattern |> List.exists (usesVar v)))
    | TExprLet (_, _, value, body) -> usesVar v value || usesVar v body
    | _ -> true   // unknown: assume it uses v

// Binding-form normalization (ONE descent, shared by every walker below).
//
// Blade's bind-then-use idiom is the brace block `{ let d = x - y; d * d }`
// (TExprBlock([TStmtLet b], Some final)) or TExprLet (`wrapMutualReturnBody`'s
// declared-return chains); neither is understood below, so bindings are
// eliminated ONCE via substitution rather than taught to every walker
// (`mirrorEq` lacks a binder-correspondence param): `{ let d = x - y; d * d }`
// becomes (x-y)*(x-y), certified by `combineBinOp OpMul PNeg PNeg = PInv`.
//
// NO-REGRESSION INVARIANT: an un-eliminated binding leaves its node
// standing, so a bail-out is a loss of PRECISION only, never soundness.

/// Node count over the kinds the walkers understand. Anything else counts as
/// "large" so it can never pass the duplication cap.
let rec private exprSize (e: TypedExpr) : int =
    let sum = List.sumBy exprSize
    match e.Kind with
    | TExprLit _ | TExprSection _ | TExprArity _ | TExprVar _ | TExprWildcard
    | TExprZero | TExprRange _ | TExprReverse _ | TExprQualified _ -> 1
    | TExprBinOp (_, _, l, r) -> 1 + exprSize l + exprSize r
    | TExprUnaryOp (_, i) -> 1 + exprSize i
    | TExprExtents a | TExprArrayNegate a | TExprArrayConjugate a -> 1 + exprSize a
    | TExprField (o, _, _) -> 1 + exprSize o
    | TExprApp (f, args) -> 1 + exprSize f + sum args
    | TExprTupleIndex (t, i) -> 1 + exprSize t + exprSize i
    | TExprIndex (a, idxs, _) -> 1 + exprSize a + sum idxs
    | TExprTuple es | TExprSequence es | TExprStack es | TExprZip es -> 1 + sum es
    | TExprComplexLit (re, im) -> 1 + exprSize re + exprSize im
    | TExprFma (a, b, c) -> 1 + exprSize a + exprSize b + exprSize c
    | TExprIf (c, t, f) -> 1 + exprSize c + exprSize t + exprSize f
    | TExprReduce (a, k, i) ->
        1 + exprSize a + exprSize k + (match i with Some x -> exprSize x | None -> 0)
    | _ -> 1000   // unmodelled: "large"

/// Occurrence count of `v`, conservative UPWARD: unmodeled kinds count as 2
/// ("many"); the `usesVar` fallback (conservative-TRUE) keeps it never an
/// UNDER-estimate, which the `= 0` / `= 1` decisions below rely on.
let rec private countVar (v: IRId) (e: TypedExpr) : int =
    let c = countVar v
    let sum = List.sumBy c
    match e.Kind with
    | TExprLit _ | TExprSection _ | TExprArity _ | TExprWildcard
    | TExprZero | TExprRange _ | TExprReverse _ | TExprQualified _ -> 0
    | TExprVar (_, id, _) -> if id = v then 1 else 0
    | TExprBinOp (_, _, l, r) -> c l + c r
    | TExprUnaryOp (_, i) -> c i
    | TExprExtents a | TExprArrayNegate a | TExprArrayConjugate a -> c a
    | TExprField (o, _, _) -> c o
    | TExprConstraintCheck (cond, _, _) -> c cond
    | TExprBreakIf cond -> c cond
    | TExprApp (f, args) -> c f + sum args
    | TExprTupleIndex (t, i) -> c t + c i
    | TExprIndex (a, idxs, _) -> c a + sum idxs
    | TExprTuple es | TExprSequence es | TExprStack es | TExprZip es -> sum es
    | TExprArrayLit (es, _) -> sum es
    | TExprComplexLit (re, im) -> c re + c im
    | TExprFma (x, y, z) -> c x + c y + c z
    | TExprIf (cond, t, f) -> c cond + c t + c f
    | TExprReduce (a, k, i) -> c a + c k + (match i with Some x -> c x | None -> 0)
    | TExprLet (_, _, value, body) -> c value + c body
    | TExprMatch (scrut, cases) ->
        c scrut
        + (cases |> List.sumBy (fun case ->
               c case.Body
               + (match case.Guard with Some g -> c g | None -> 0)
               + (patGuardExprs case.Pattern |> List.sumBy c)))
    | _ -> if usesVar v e then 2 else 0

/// Capture-free substitution of `repl` for every TExprVar bearing VarId `v`.
///
/// Returns None on an unrewritable node kind, LEAVING THE BINDING IN PLACE
/// (precision loss, not unsoundness) -- the single `| _ ->` arm is what
/// makes TExprLambda's `Captures`, TypedApplyInfo's ten expression fields,
/// TExprAssign, and other mutation sites safe BY CONSTRUCTION. Binder IRIds
/// are globally unique, so no alpha-renaming is needed: `repl` can't be captured.
let rec private substVar (v: IRId) (repl: TypedExpr) (e: TypedExpr) : TypedExpr option =
    let sub = substVar v repl
    let subs (es: TypedExpr list) =
        let rs = es |> List.map sub
        if rs |> List.forall Option.isSome then Some (rs |> List.map Option.get) else None
    let ok k = Some { e with Kind = k }
    match e.Kind with
    | TExprVar (_, id, _) when id = v -> Some repl
    | TExprLit _ | TExprSection _ | TExprArity _ | TExprVar _ | TExprWildcard
    | TExprZero | TExprRange _ | TExprReverse _ | TExprQualified _ -> Some e
    | TExprBinOp (m, op, l, r) ->
        (match sub l, sub r with
         | Some a, Some b -> ok (TExprBinOp (m, op, a, b))
         | _ -> None)
    | TExprUnaryOp (op, i) -> sub i |> Option.bind (fun a -> ok (TExprUnaryOp (op, a)))
    | TExprExtents a -> sub a |> Option.bind (fun x -> ok (TExprExtents x))
    | TExprArrayNegate a -> sub a |> Option.bind (fun x -> ok (TExprArrayNegate x))
    | TExprArrayConjugate a -> sub a |> Option.bind (fun x -> ok (TExprArrayConjugate x))
    | TExprField (o, f, i) -> sub o |> Option.bind (fun x -> ok (TExprField (x, f, i)))
    | TExprConstraintCheck (cond, code, msg) ->
        sub cond |> Option.bind (fun x -> ok (TExprConstraintCheck (x, code, msg)))
    | TExprBreakIf cond ->
        sub cond |> Option.bind (fun x -> ok (TExprBreakIf x))
    | TExprApp (f, args) ->
        (match sub f, subs args with
         | Some g, Some a -> ok (TExprApp (g, a))
         | _ -> None)
    | TExprTupleIndex (t, i) ->
        (match sub t, sub i with
         | Some a, Some b -> ok (TExprTupleIndex (a, b))
         | _ -> None)
    | TExprIndex (a, idxs, ident) ->
        (match sub a, subs idxs with
         | Some x, Some ix -> ok (TExprIndex (x, ix, ident))
         | _ -> None)
    | TExprTuple es -> subs es |> Option.bind (fun x -> ok (TExprTuple x))
    | TExprSequence es -> subs es |> Option.bind (fun x -> ok (TExprSequence x))
    | TExprStack es -> subs es |> Option.bind (fun x -> ok (TExprStack x))
    | TExprZip es -> subs es |> Option.bind (fun x -> ok (TExprZip x))
    | TExprArrayLit (es, aty) -> subs es |> Option.bind (fun x -> ok (TExprArrayLit (x, aty)))
    | TExprComplexLit (re, im) ->
        (match sub re, sub im with
         | Some a, Some b -> ok (TExprComplexLit (a, b))
         | _ -> None)
    | TExprFma (x, y, z) ->
        (match sub x, sub y, sub z with
         | Some a, Some b, Some c -> ok (TExprFma (a, b, c))
         | _ -> None)
    | TExprIf (cond, t, f) ->
        (match sub cond, sub t, sub f with
         | Some a, Some b, Some d -> ok (TExprIf (a, b, d))
         | _ -> None)
    | TExprReduce (a, k, i) ->
        let si = match i with None -> Some None | Some x -> sub x |> Option.map Some
        (match sub a, sub k, si with
         | Some x, Some y, Some z -> ok (TExprReduce (x, y, z))
         | _ -> None)
    | TExprLet (n, vid, value, body) ->
        (match sub value, sub body with
         | Some a, Some b -> ok (TExprLet (n, vid, a, b))
         | _ -> None)
    | TExprBlock (stmts, final) ->
        // Only the two statement forms that compute a value are rewritable;
        // an assignment or a for-in loop drops out through the None below,
        // which is precisely the guard against inlining across a mutation.
        let subStmt (s: TypedStmt) =
            match s with
            | TStmtLet b ->
                (match sub b.Value,
                       (let ps = b.PostChecks |> List.map (fun (i, x) -> sub x |> Option.map (fun y -> (i, y)))
                        if ps |> List.forall Option.isSome then Some (ps |> List.map Option.get) else None) with
                 | Some nv, Some nps -> Some (TStmtLet { b with Value = nv; PostChecks = nps })
                 | _ -> None)
            | TStmtExpr x -> sub x |> Option.map TStmtExpr
            | TStmtAssign _ | TStmtForIn _ -> None
        let ss = stmts |> List.map subStmt
        let sf = match final with None -> Some None | Some x -> sub x |> Option.map Some
        (match ss |> List.forall Option.isSome, sf with
         | true, Some f -> ok (TExprBlock (ss |> List.map Option.get, f))
         | _ -> None)
    | TExprMatch (scrut, cases) ->
        // Patterns are left alone (BINDERS are fresh, never `v`) except a
        // `TPatGuarded` expression; if one mentions `v`, substitution is
        // refused -- a stranded FREE var would make parityOf misread it as PInv, a false claim not a lost one.
        let subCase (c: TypedMatchCase) =
            if patGuardExprs c.Pattern |> List.exists (usesVar v) then None
            else
                let sg = match c.Guard with None -> Some None | Some g -> sub g |> Option.map Some
                (match sub c.Body, sg with
                 | Some b, Some g -> Some { c with Body = b; Guard = g }
                 | _ -> None)
        let cs = cases |> List.map subCase
        (match sub scrut, cs |> List.forall Option.isSome with
         | Some s, true -> ok (TExprMatch (s, cs |> List.map Option.get))
         | _ -> None)
    | _ -> if usesVar v e then None else Some e

/// Is this whole tree inside the world `substVar` can rewrite? Queried with
/// an IRId no binder can hold, so it can only discover an unmodelled node kind.
let private isRewritable (e: TypedExpr) : bool =
    (substVar System.Int32.MinValue e e).IsSome

/// Duplication cap: a leaf, or one operator over leaves (`x - y` = 3 nodes).
/// SINGLE-use bindings inline unconditionally, so this governs only the duplicating case.
let private smallValueSize = 3

/// Per-binding DUPLICATION budget, in nodes added.
///
/// `smallValueSize` alone does NOT bound growth: a chain
///     let a = x - y   let b = a * a   let c = b * b   ...
/// has each VALUE at 3 nodes pre-substitution, yet each link doubles the
/// tree (N links -> 2^N nodes). What blows up is occurrence count `n` in
/// the EXPANDED body, so THAT's capped per binding (over-budget bindings kept as-is).
let private duplicationBudget = 256

/// Reduce one `let name = value; body`: the binding-free body when safe,
/// else the rebuilt binding (see the no-regression invariant above).
let private reduceLet (name: string) (vid: IRId) (value: TypedExpr) (body: TypedExpr) : TypedExpr =
    // A let's type is its body's type, so the body node carries the right
    // Type/Span for the rebuilt node.
    let keep () = { body with Kind = TExprLet (name, vid, value, body) }
    if not (isRewritable value) then keep ()
    else
        let n = countVar vid body
        // Substitution replaces `n` one-node var references with the value, so
        // it adds exactly n * (size - 1) nodes.
        let inlineOk =
            n = 1                                    // no duplication at all
            || (exprSize value <= smallValueSize
                && n * (exprSize value - 1) <= duplicationBudget)
        if n = 0 then body            // dead binding: the value is unreachable
        elif inlineOk then
            match substVar vid value body with
            | Some flat -> flat
            | None -> keep ()
        else keep ()

/// Reduce a brace block. Statement guard is POSITIVE -- every statement
/// must be a plain, non-destructuring `let`, excluding TStmtAssign/
/// TStmtForIn/TStmtExpr, DSConsRest (pack-fold), and mutual-group PostChecks.
///
/// NOT guarded on `IsMutable`: `assignOfBindingMut` maps ordinary `let` to
/// `Assignable` (only `static`/`let const` is ReadOnly), so gating on it
/// would no-op this pass. The real guard is structural: no assignment/loop
/// or VALUE-embedded assignment can pass (kept as unrewritable).
let private reduceBlock (orig: TypedExpr) (stmts: TypedStmt list) (final: TypedExpr option) : TypedExpr =
    match stmts, final with
    | [], Some inner -> inner
    | _, None -> orig     // a statement-only block computes nothing to judge
    | _, Some fin ->
        let simpleLet (s: TypedStmt) =
            match s with
            | TStmtLet b when List.isEmpty b.SubBindings
                              && List.isEmpty b.PostChecks
                              && b.Destructure = DSPositional -> Some b
            | _ -> None
        let bs = stmts |> List.map simpleLet
        if bs |> List.forall Option.isSome then
            List.foldBack
                (fun (b: TypedBinding) acc -> reduceLet b.Name b.VarId b.Value acc)
                (bs |> List.map Option.get)
                fin
        else orig

/// Normalize binding forms so every walker below sees a binding-free tree
/// wherever possible (post-order: children first, then the node). Node
/// kinds outside the rewritable world are UNCHANGED/UNRECURSED -- flattening
/// inside a lambda body or apply-info record buys nothing and would need `Captures`/`ArrayTypes` support.
let rec private flattenBindings (e: TypedExpr) : TypedExpr =
    let f = flattenBindings
    let fs = List.map f
    let k kind = { e with Kind = kind }
    let rebuilt =
        match e.Kind with
        | TExprBinOp (m, op, l, r) -> k (TExprBinOp (m, op, f l, f r))
        | TExprUnaryOp (op, i) -> k (TExprUnaryOp (op, f i))
        | TExprExtents a -> k (TExprExtents (f a))
        | TExprArrayNegate a -> k (TExprArrayNegate (f a))
        | TExprArrayConjugate a -> k (TExprArrayConjugate (f a))
        | TExprField (o, fl, i) -> k (TExprField (f o, fl, i))
        | TExprConstraintCheck (c, code, msg) -> k (TExprConstraintCheck (f c, code, msg))
        | TExprBreakIf c -> k (TExprBreakIf (f c))
        | TExprApp (fn, args) -> k (TExprApp (f fn, fs args))
        | TExprTupleIndex (t, i) -> k (TExprTupleIndex (f t, f i))
        | TExprIndex (a, idxs, ident) -> k (TExprIndex (f a, fs idxs, ident))
        | TExprTuple es -> k (TExprTuple (fs es))
        | TExprSequence es -> k (TExprSequence (fs es))
        | TExprStack es -> k (TExprStack (fs es))
        | TExprZip es -> k (TExprZip (fs es))
        | TExprArrayLit (es, aty) -> k (TExprArrayLit (fs es, aty))
        | TExprComplexLit (re, im) -> k (TExprComplexLit (f re, f im))
        | TExprFma (a, b, c) -> k (TExprFma (f a, f b, f c))
        | TExprIf (c, t, el) -> k (TExprIf (f c, f t, f el))
        | TExprReduce (a, kern, i) -> k (TExprReduce (f a, f kern, Option.map f i))
        | TExprLet (n, vid, value, body) -> k (TExprLet (n, vid, f value, f body))
        | TExprMatch (scrut, cases) ->
            k (TExprMatch (f scrut,
                           cases |> List.map (fun c ->
                               { c with Body = f c.Body; Guard = Option.map f c.Guard })))
        | TExprBlock (stmts, final) ->
            let fStmt (s: TypedStmt) =
                match s with
                | TStmtLet b -> TStmtLet { b with Value = f b.Value }
                | TStmtExpr x -> TStmtExpr (f x)
                | other -> other
            k (TExprBlock (stmts |> List.map fStmt, Option.map f final))
        | _ -> e
    match rebuilt.Kind with
    | TExprBlock (stmts, final) -> reduceBlock rebuilt stmts final
    | TExprLet (n, vid, value, body) -> reduceLet n vid value body
    | _ -> rebuilt

// Sign-linearity summaries (the interprocedural half of table 2)

/// Table 2', the SIGN chain rule through a binary op: how `l op r` behaves
/// when the tracked parameter is negated, given how each operand behaves.
let private combineSign (op: BinOp) (a: SignParity) (b: SignParity) : SignParity =
    match op with
    | OpMul | OpDiv ->
        // Multiplicative in EACH operand: (-l)*r=-(l*r), l/(-r)=-(l/r), and
        // (-l)/(-r)=l/r (two flips cancel, as PNeg*PNeg=PInv does for swap).
        (match a, b with
         | SEven, SEven -> SEven
         | SOdd, SEven | SEven, SOdd -> SOdd
         | SOdd, SOdd -> SEven
         | _ -> SUnknown)
    | OpAdd | OpSub ->
        // Jointly linear: (-l) +/- (-r) = -(l +/- r), unchanged when both are;
        // a MIXED pair (`-l + r`) is neither, so unknown.
        (match a, b with
         | SEven, SEven -> SEven
         | SOdd, SOdd -> SOdd
         | _ -> SUnknown)
    | _ ->
        // Comparisons, logicals, `%`, `^`: a flipped operand changes the
        // node's VALUE (`x > 0` vs `-x > 0`) -- a boolean isn't "the negation
        // of" another boolean, so only joint evenness composes.
        (match a, b with
         | SEven, SEven -> SEven
         | _ -> SUnknown)

/// Sign-parity of one subtree under negating parameter `p`. `resolver`
/// supplies a summarized callee's sign parities in decl order (None = not
/// summarized), keyed by BINDER ID, not surface name (shadowing-safe).
let rec private signParityOf (resolver: IRId -> SignParity list option)
                             (p: IRId) (e: TypedExpr) : SignParity =
    let sp = signParityOf resolver p
    match e.Kind with
    // Base case (literals, sections, other parameters, captures): a subtree
    // never mentioning p evaluates unchanged (even). `usesVar` is
    // conservative-TRUE and nothing here descends into a binding form, so a
    // var reached is p, a parameter, or a capture -- never a local.
    | _ when not (usesVar p e) -> SEven
    | TExprVar (_, id, _) ->
        // The guard above already claimed every var that is not p.
        if id = p then SOdd else SEven
    | TExprBinOp (_, op, l, r) -> combineSign op (sp l) (sp r)
    | TExprUnaryOp (op, inner) ->
        (match op, sp inner with
         | _, SEven -> SEven          // unchanged input, unchanged result, any op
         | (OpNeg | OpReal | OpImag | OpConj), SOdd -> SOdd
         // R-linear ops commute with sign: -(-x), Re(-z)=-Re(z), conj(-z)=
         // -conj(z) (`!` yields bool; `arg`/OpMath intrinsics aren't sign-linear).
         | _ -> SUnknown)
    | TExprArrayNegate a ->
        // Whole-array negation is the array-level OpNeg: sign passes through.
        (match sp a with SOdd -> SOdd | SEven -> SEven | SUnknown -> SUnknown)
    | TExprArrayConjugate a ->
        // Conjugation is R-linear elementwise: conj(-A) = -conj(A).
        (match sp a with SOdd -> SOdd | SEven -> SEven | SUnknown -> SUnknown)
    | TExprApp (f, args) ->
        // THE chain rule: negating p flips argument i iff it's SOdd in p
        // AND the callee is SOdd in position i (SEven absorbs it); result
        // flips by (-1)^k, k = count of such flips (legitimate since each
        // summary is universally quantified: f(-x1,-x2)=s1*f(x1,-x2)=
        // s1*s2*f(x1,x2)). SUnknown, an unsummarized callee, or a partial application: no claim.
        if sp f <> SEven then SUnknown   // p itself in callee position
        else
            let argPs = args |> List.map sp
            if argPs |> List.forall ((=) SEven) then SEven
            // Every argument unchanged, so the call is unchanged -- no
            // summary needed (parityOf's all-invariant App rule agrees).
            else
                match f.Kind with
                | TExprVar (_, fid, _) ->
                    (match resolver fid with
                     | Some summary when summary.Length = args.Length ->
                         let rec walk ps ss flips =
                             match ps, ss with
                             | [], _ -> if flips % 2 = 1 then SOdd else SEven
                             | SEven :: pr, _ :: sr -> walk pr sr flips
                             | SOdd :: pr, SOdd :: sr -> walk pr sr (flips + 1)
                             | SOdd :: pr, SEven :: sr -> walk pr sr flips
                             | _ -> SUnknown   // SUnknown on either side
                         walk argPs summary 0
                     | _ -> SUnknown)
                | _ -> SUnknown
    | TExprReduce (arr, kernel, init) ->
        let kernelEven = sp kernel = SEven
        let initEven = match init with Some i -> sp i = SEven | None -> true
        (match sp arr with
         | SEven when kernelEven && initEven -> SEven
         | SOdd when init.IsNone
                     && (match kernel.Kind with TExprSection OpAdd -> true | _ -> false) ->
             SOdd   // Sum(-x) = -Sum(x)
         // reduce(_, (*)) scales by (-1)^extent (unknowable); a SEEDED
         // fold's UNnegated accumulator, and min/max/user combinators, get no law.
         | _ -> SUnknown)
    | TExprExtents a ->
        // extents(-x) = extents(x): negation doesn't change shape, so an ODD
        // child yields EVEN extents -- what makes
        // `mymean(row) = reduce(row, (+)) / extents(row)` odd overall
        // (odd/even = odd). A value-DEPENDENT shape (mask/filter) stays unknown.
        (match sp a with SOdd | SEven -> SEven | SUnknown -> SUnknown)
    | TExprIndex (arr, idxs, _) ->
        // Indexing is linear in the array -- (-A)(i)=-(A(i)) -- but only at
        // the SAME cell, so every index must be even before parity passes through.
        if idxs |> List.forall (fun i -> sp i = SEven) then sp arr else SUnknown
    | TExprIf (c, t, f) ->
        // The condition must be unchanged (else it could select a
        // different branch); with it fixed, matching branches propagate, including SOdd/SOdd.
        if sp c <> SEven then SUnknown
        else
            (match sp t, sp f with
             | SEven, SEven -> SEven
             | SOdd, SOdd -> SOdd
             | _ -> SUnknown)
    | TExprMatch (scrut, cases) when not (List.isEmpty cases) ->
        // The sign twin of parityOf's match rule: an EVEN scrutinee selects
        // the same arm when p is negated, so binders are even and arms compose like `if` branches.
        let guards =
            cases |> List.collect (fun c ->
                (match c.Guard with Some g -> [g] | None -> [])
                @ patGuardExprs c.Pattern)
        if sp scrut <> SEven then SUnknown
        elif guards |> List.exists (fun g -> sp g <> SEven) then SUnknown
        else
            let bodies = cases |> List.map (fun c -> sp c.Body)
            if bodies |> List.forall ((=) SEven) then SEven
            elif bodies |> List.forall ((=) SOdd) then SOdd
            else SUnknown
    | TExprTuple es ->
        // Aggregates have no negation as a value operation, so only
        // invariance composes.
        if es |> List.forall (fun x -> sp x = SEven) then SEven else SUnknown
    | TExprField (o, _, _) ->
        (match sp o with SEven -> SEven | _ -> SUnknown)   // same reasoning as tuples
    | _ -> SUnknown   // closed world: unlisted node kinds certify nothing

/// Per-parameter sign-linearity summary, one entry per parameter in decl
/// order. Consumed by `parityOf`'s call rule and nested calls' own
/// summaries -- a self- or forward-call resolves to None (SUnknown), so no fixpoint is needed.
let deduceSignParities (resolver: IRId -> SignParity list option)
                       (parms: TypedParam list) (body: TypedExpr) : SignParity list =
    // Binding forms are eliminated ONCE here, not per parameter or at the two producer call sites in TypeCheck.
    let body = flattenBindings body
    parms |> List.map (fun p -> signParityOf resolver p.VarId body)

// Conjugation-linearity (the Hermitian twin of the sign summaries)

/// Is this p-free subtree a REAL constant (one conjugation fixes)? Element
/// TYPE decides it, except buildApplyInfo's complex re-stamp upgrades real
/// literals to the body's complex type (so reading the stamp back would
/// reject `H <@> (v * 2.0)`); literals/field-exprs are judged syntactically, else fall back to the stamp.
let rec private isRealConstant (e: TypedExpr) : bool =
    match e.Kind with
    | TExprLit (LitInt _ | LitFloat _) -> true
    | TExprLit _ -> false
    | TExprUnaryOp (OpNeg, x) -> isRealConstant x
    | TExprBinOp (_, (OpAdd | OpSub | OpMul | OpDiv), l, r) ->
        isRealConstant l && isRealConstant r
    | _ ->
        match stripUnits e.Type with
        | IRTScalar (ETComplex64 | ETComplex128) -> false
        | IRTScalar _ -> true
        | _ -> false

/// Does a body COMMUTE WITH COMPLEX CONJUGATION in `p` -- f(conj z) =
/// conj(f z)? Hermitian twin of the sign law: each compact class recovers
/// its missing triangle via a mirror involution on the VALUE (negation for
/// AntisymIdx, conjugation for HermitianIdx, identity for SymIdx). A kernel
/// keeps its class iff it commutes with that involution: antisymmetric asks
/// `signParityOf ... = SOdd`, symmetric asks nothing, Hermitian asks this.
///
/// Conjugation is a FIELD AUTOMORPHISM fixing the reals, so the certificate
/// is syntactic (p, real constants, field ops, conjugation-commuting unary
/// ops = "real"; excluded: complex constants, `imag`/`arg`, `^`/OpMath). Unlisted: FALSE.
let rec private conjCommutesIn (p: IRId) (e: TypedExpr) : bool =
    let cc = conjCommutesIn p
    match e.Kind with
    // Base case: a p-free subtree is a constant of the map; conj(c)=c iff c is real.
    | _ when not (usesVar p e) -> isRealConstant e
    | TExprVar (_, id, _) -> id = p   // the identity map trivially commutes
    | TExprBinOp (_, (OpAdd | OpSub | OpMul | OpDiv), l, r) -> cc l && cc r
    // conj(-z)=-conj z; conj(conj z)=z; Re(conj z)=Re z is real, so conj
    // fixes it (`!` yields bool; rest are on the reject list above).
    | TExprUnaryOp ((OpNeg | OpConj | OpReal), inner) -> cc inner
    // An arm chosen independently of p is the same for z and conj z, so it carries its own law.
    | TExprIf (c, t, f) -> not (usesVar p c) && cc t && cc f
    | _ -> false

/// Per-parameter conjugation-linearity summary, in declaration order -- the
/// `deduceSignParities` shape for the Hermitian half. Intraprocedural: a
/// call certifies nothing (no summary side-channel), landing on FALSE.
let deduceConjCommutes (parms: TypedParam list) (body: TypedExpr) : bool list =
    let body = flattenBindings body
    parms |> List.map (fun p -> conjCommutesIn p.VarId body)

/// Parity of one subtree under the swap (pi pj): a bare occurrence of
/// either swapped param with no enclosing mirror is PBottom. `resolver` is
/// the sign-summary lookup used by the call rule below.
let rec private parityOf (resolver: IRId -> SignParity list option)
                         (pi: IRId) (pj: IRId) (e: TypedExpr) : Parity =
    let allInv ps = if ps |> List.forall ((=) PInv) then PInv else PBottom
    let par = parityOf resolver pi pj
    match e.Kind with
    | TExprLit _ | TExprSection _ -> PInv
    | TExprVar (_, id, _) -> if id = pi || id = pj then PBottom else PInv
    | TExprBinOp (_, op, l, r) ->
        // The mirror rule answers only when the op HAS a swap class. A mirror
        // hit under a non-commuting op (`(x + y) / (y + x)`) certifies nothing
        // by itself, but the operands may each be invariant on their own --
        // so it must fall through to the chain rule, not stop at PBottom.
        // Answering PBottom here was sound but INCOMPLETE, and it let a wrong
        // `anticomm` pin on such a body through unrefuted
        // (proofs/BladeDeduceExact.v: shipped_rule_incomplete, parfix_exact).
        if mirrorEq pi pj l r && opSwapClass op <> PBottom then opSwapClass op
        else
            // A SIGN law proved from the children outranks the conjugate
            // mirror below. The two can hold at once -- `x*y + conj(x)*conj(y)`
            // is z + conj z with each side invariant, so e[swap] = e AND
            // e[swap] = conj e, which only says the value is real -- and when
            // they do, the sign law is the one storage reads: comm (identity
            // mirror) is then exactly right, and PConj's refutation would be a
            // false positive. PNeg with PConj likewise means purely imaginary,
            // where anticomm is right. PConj only speaks when no sign law does.
            let combined = combineBinOp op (par l) (par r)
            if combined <> PBottom then combined
            // The PConj birth site: swap(l) = conj(r) makes swap(l op r) =
            // conj(r) op conj(l) = conj(r op l), which is conj(l op r) exactly
            // when the op commutes AND conj distributes over it -- OpAdd/OpMul
            // only (OpSub/OpDiv fail commutation; booleans absorb conj's law).
            elif (match op with OpAdd | OpMul -> true | _ -> false)
                 && conjMirrorEq pi pj l r then PConj
            else PBottom
    | TExprUnaryOp (op, inner) ->
        (match op, par inner with
         | _, PInv -> PInv
         // R-linear ops commute with sign (conj(-z) = -conj(z) included).
         | (OpNeg | OpReal | OpImag | OpConj), PNeg -> PNeg
         // The conjugate mirror composes: -conj(w) = conj(-w) and
         // conj(conj(w)) re-conjugates on both sides, so PConj survives...
         | (OpNeg | OpConj), PConj -> PConj
         // ...collapses to invariance under real(): real(conj w) = real(w)...
         | OpReal, PConj -> PInv
         // ...and to ANTIsymmetry under imag(): imag(conj w) = -imag(w).
         | OpImag, PConj -> PNeg
         | _ -> PBottom)
    | TExprApp (f, args) ->
        // Invariant when callee and every argument are invariant; else the
        // same chain rule as signParityOf's App arm: call = (-1)^k times
        // itself, k = count of PNeg args in SOdd callee positions. Makes
        // `mymean(x - y)` PNeg and `mymean(x-y) * mymean(x-y)` PInv via
        // combineBinOp's PNeg*PNeg (invisible to the mirror rule). PBottom
        // arg, unsummarized callee, SUnknown position, or arity mismatch: no claim.
        let fp = par f
        let argPs = args |> List.map par
        if fp = PInv && argPs |> List.forall ((=) PInv) then PInv
        elif fp <> PInv then PBottom
        else
            match f.Kind with
            | TExprVar (_, fid, _) ->
                (match resolver fid with
                 | Some summary when summary.Length = args.Length ->
                     let rec walk ps ss flips =
                         match ps, ss with
                         | [], _ -> if flips % 2 = 1 then PNeg else PInv
                         | PInv :: pr, _ :: sr -> walk pr sr flips
                         | PNeg :: pr, SOdd :: sr -> walk pr sr (flips + 1)
                         | PNeg :: pr, SEven :: sr -> walk pr sr flips
                         | _ -> PBottom   // PBottom argument or SUnknown position
                     walk argPs summary 0
                 | _ -> PBottom)
            | _ -> PBottom
    | TExprReduce (arr, kernel, init) ->
        (match par arr, init with
         | PInv, None -> PInv
         | PInv, Some i -> (match par i with PInv -> PInv | _ -> PBottom)
         | PNeg, None when (match kernel.Kind with TExprSection OpAdd -> true | _ -> false) ->
             PNeg   // Sum(-x) = -Sum(x); reduce(*) and seeded folds certify nothing
         | PConj, None when (match kernel.Kind with TExprSection OpAdd -> true | _ -> false) ->
             PConj   // Sum(conj x) = conj(Sum x) -- the Hermitian dot product's law
         | _ -> PBottom)
    | TExprExtents arr ->
        (match par arr with PInv -> PInv | _ -> PBottom)
    | TExprIndex (arr, idxs, _) ->
        allInv (par arr :: (idxs |> List.map par))
    | TExprTuple es -> allInv (es |> List.map par)
    | TExprIf (c, t, f) ->
        // The condition must be INVARIANT (else it could select a
        // different branch, and no law relates two different branches).
        // With it pinned, matching branches propagate, including PNeg/PNeg.
        if par c <> PInv then PBottom
        else
            (match par t, par f with
             | PInv, PInv -> PInv
             | PNeg, PNeg -> PNeg
             | PConj, PConj -> PConj
             | _ -> PBottom)
    | TExprMatch (scrut, cases) when not (List.isEmpty cases) ->
        // The multi-way TExprIf. Also why PATTERN-BOUND vars need no
        // substitution: an INVARIANT scrutinee decomposes to the same
        // sub-values under the swap, so binders are invariant too (matches
        // the TExprVar arm). Guards, including in-pattern ones, must be invariant, not merely agreeing.
        let guards =
            cases |> List.collect (fun c ->
                (match c.Guard with Some g -> [g] | None -> [])
                @ patGuardExprs c.Pattern)
        if par scrut <> PInv then PBottom
        elif guards |> List.exists (fun g -> par g <> PInv) then PBottom
        else
            let bodies = cases |> List.map (fun c -> par c.Body)
            if bodies |> List.forall ((=) PInv) then PInv
            elif bodies |> List.forall ((=) PNeg) then PNeg
            elif bodies |> List.forall ((=) PConj) then PConj
            else PBottom
    | TExprField (o, _, _) ->
        (match par o with PInv -> PInv | _ -> PBottom)
    | _ -> PBottom   // closed world: unlisted node kinds certify nothing

/// Deduce the swap parity of each ADJACENT parameter pair of a fixed-arity
/// kernel: n params yield n-1 entries (empty for arity < 2), matching the
/// Sn generator structure and the call site's consecutive-identity grouping
/// (H cap Stab). `resolver` supplies callee summaries (deduceSignParities).
let deduceAdjacentPairs (resolver: IRId -> SignParity list option)
                        (parms: TypedParam list) (body: TypedExpr) : Parity list =
    match parms with
    | [] | [_] -> []
    | _ ->
        let body = flattenBindings body
        parms
        |> List.pairwise
        |> List.map (fun (a, b) -> parityOf resolver a.VarId b.VarId body)

// WITNESSED LAW FAILURE -- the refutation twin of PInv / PNeg.
//
// `parityOf` answers PBottom for two very different bodies: one the analysis
// cannot see through (`sqrt(x * y)` behind a call it has no summary for), and
// one that is PROVABLY not invariant (`x / y`, `x`, `x - 2.0 * y`). The first
// must stay trusted -- PBottom is the documented escape hatch, and a `comm`
// clause there is the author's claim. The second is a false claim the checker
// can DISPROVE, and trusting it stores a triangle and answers half the reads
// with the wrong value (x / y over (A, A) returned r(2, 0) = 0.25, true 4).
//
// The certificate is a CONCRETE COUNTEREXAMPLE: the body is evaluated at a few
// sample points (a, b) and at their swap (b, a); a clear difference at one
// point proves f(x, y) <> f(y, x) as a LAW. It is sound by construction -- a
// symmetric body evaluates to the same value either way (exactly over
// integers; up to rounding over floats, which the relative tolerance absorbs:
// every sample is a small dyadic number) -- and it refutes any false claim
// whose failure shows at one of the samples. The evaluator is closed-world:
// it runs only literals, the parameters, captured scalars (below), scalar
// arithmetic, comparisons, logic, `if`, casts and the math intrinsics.
// Anything else -- a call, an array read, a complex operand, a local binding
// that could not be inlined -- answers "no evaluation", and no witness is
// claimed (the body stays trusted exactly as before).
//
// ARITHMETIC IS FORMALISM 2.4, the contract every lane runs: integer values
// stay exact Int64 (Int32 nodes narrow to 32 bits), `+ - *` and negation WRAP,
// `/` and `%` truncate (MIN / -1 wraps to MIN, MIN % -1 is 0), integer `^` is
// exact modulo 2^64, and division/modulo by zero, a negative integer exponent
// and a NaN/out-of-range float->int cast PANIC (src/Interp/Numerics.fs is the
// reference). A float evaluator refuted TRUE claims under wrap (`(x - y) + c`
// at c = Int64.MinValue is honestly sign-antisymmetric) and could accept false
// ones. Each node's kind comes from its own type when that is concrete, else
// from `isInt` (kernel parameters are still inference variables at the
// lambda seam; the operand arrays decide).
//
// ARITY > 2. A claim on a pair of a wider kernel (`comm(x, y, z)` is the
// claim on every pair the group's transpositions generate) is a law for
// EVERY value of the remaining parameters, so it is disproved by a
// counterexample at ANY fixed values of them: `others` are bound to
// constants that stay fixed across the swap. (Each other parameter gets its
// own constant, so a body cannot be accidentally symmetric because two of
// them coincide.)
//
// CAPTURED SCALARS are free inputs of the claim in the same way. A kernel
// that reads a captured Int/Float variable declares a law for the body as
// written, and nothing ties the clause to the one value the capture has at
// this apply (the same kernel text is checked once, at its declaration or
// its apply, before any value flows). So a capture is bound to a sampled
// constant, fixed across the swap; a claim that holds only for the actual
// captured value is refused -- conservatively -- rather than trusted. Before,
// the evaluator gave up on a capture and the claim was trusted: `(x - y) + c`
// with a captured c = Int64.MinValue was stored on the strict triangle and
// its diagonal read as 0 against the dense -9223372036854775808.
//
// `negate` checks the ANTICOMMUTATIVE law instead: f(y, x) = -f(x, y), so a
// witness is a point where f(b, a) is not -f(a, b). It ALSO checks the
// DIAGONAL: strict `AntisymIdx` storage has no diagonal cell and answers
// every (i, i) read with 0, so `anticomm` asserts f(a, a) = 0. The sign law
// alone does not force that -- f(a, a) = -f(a, a) has the solution
// Int64.MinValue in wrapping Int64, and a body can satisfy the law off the
// diagonal and lie on it (`if x > y then 1.0 else -1.0`, f(a, a) = -1). The
// diagonal check runs even on a pair the parity tables PROVED (`skipSwap`):
// a structural PNeg proof gives f(a, a) = 0 exactly in Int64 except where
// the body panics there (`k / (x - y)`), and a panicking diagonal is not 0
// either. A non-finite FLOAT diagonal (inf - inf, 0.0 / 0.0) is skipped, not
// refused: that is the documented exception to the stored zero (formalism
// 3.4), the same non-finite exclusion the swap samples make.

/// One scalar of the witness evaluator: formalism 2.4 keeps integers and
/// floats apart, so the evaluator does too.
type WitnessVal =
    | WInt of int64
    | WFloat of float
    | WBool of bool

/// A 2.4 panic raised while evaluating a witness sample (the message names it).
exception private WitnessPanic of string

/// How a declared swap law failed at a concrete point.
type LawFailure =
    /// f(.., a, .., b, ..) = u, but f(.., b, .., a, ..) = v where the law
    /// required `required` (u for comm, -u for anticomm). Values rendered.
    | SwapLawFails of a: string * b: string * u: string * v: string * required: string
    /// anticomm only: f(.., a, .., a, ..) is `value` (rendered; a panic reads
    /// "a panic (...)"), not the 0 strict-triangular storage answers there.
    | DiagonalNonzero of a: string * value: string

/// A witnessed law failure with the values the other inputs were held at.
type LawWitness =
    { Failure: LawFailure
      /// The other parameters' fixed values (binder id, rendered value).
      Fixed: (IRId * string) list
      /// The captured scalars the body read, with their sampled values (name, rendered).
      Captured: (string * string) list }

let private renderWitnessVal (v: WitnessVal) : string =
    let inv = System.Globalization.CultureInfo.InvariantCulture
    match v with
    | WInt i -> i.ToString(inv)
    | WFloat f -> f.ToString("G6", inv)
    | WBool b -> if b then "true" else "false"

/// The scalar kind a node's type pins, if it is concrete.
let rec private witnessKindOf (t: IRType) : ElemType option =
    match stripUnits t with
    | IRTIdxTagged (inner, _) -> witnessKindOf inner
    | IRTScalar ((ETInt32 | ETInt64 | ETFloat32 | ETFloat64 | ETBool) as et) -> Some et
    | _ -> None

/// `inlineFn` resolves a call head's binder id to a PURE, NON-RECURSIVE user
/// function's (parameter ids, typed body) -- the checker's FuncWitnessBodies
/// table -- so a body that calls one evaluates through it (D12). A call it
/// does not resolve leaves the body unevaluable (trusted), as before.
let witnessSwapLawFailure (inlineFn: IRId -> (IRId list * TypedExpr) option)
                          (pi: IRId) (pj: IRId) (others: IRId list) (negate: bool)
                          (skipSwap: bool) (isInt: bool) (body: TypedExpr)
                          : LawWitness option =
    let body = flattenBindings body
    // A sample seed is a float (the float pool has halves); an occurrence
    // reads it at its own kind when that is concrete, else at `isInt`'s.
    let seedVal (v: float) = if isInt then WInt (int64 v) else WFloat v
    let paramVal (v: float) (e: TypedExpr) =
        match witnessKindOf e.Type with
        | Some (ETInt32 | ETInt64) -> WInt (int64 v)
        | Some (ETFloat32 | ETFloat64) -> WFloat v
        | _ -> seedVal v
    // Fixed values of the other parameters: integers, rendered as the kind
    // each occurrence reads them at (float pools were dyadic for a reason --
    // exact in binary -- and the int pool's members are exact either way).
    let othersPoolInt = [ 3L; 5L; -2L; 7L; 4L; 6L; -3L; 8L ]
    let othersPoolFloat = [ 0.75; 1.25; -0.5; 2.5; 3.25; -1.75; 1.5; 4.5 ]
    let otherVal (k: int) (asInt: bool) =
        if asInt then WInt (othersPoolInt.[k % othersPoolInt.Length] + int64 (k / othersPoolInt.Length))
        else WFloat (othersPoolFloat.[k % othersPoolFloat.Length] + float (k / othersPoolFloat.Length))
    let fixedIdx = others |> List.mapi (fun k id -> (id, k)) |> Map.ofList
    let fixedSeen = System.Collections.Generic.Dictionary<IRId, WitnessVal>()
    // Captured scalars: sampled from pools disjoint from the parameter
    // samples, assigned in first-read order (deterministic), stable across
    // every evaluation of this call.
    let capPoolInt = [ 11L; -5L; 13L; 9L; -7L; 17L ]
    let capPoolFloat = [ 0.375; -2.25; 1.625; 5.5; -0.875; 2.125 ]
    let captured = System.Collections.Generic.Dictionary<IRId, WitnessVal>()
    let capturedOrder = System.Collections.Generic.List<string * IRId>()
    let capture (name: string) (id: IRId) (e: TypedExpr) : WitnessVal option =
        match captured.TryGetValue id with
        | true, v -> Some v
        | _ ->
            let k = captured.Count
            let v =
                match witnessKindOf e.Type with
                | Some (ETInt32 | ETInt64) -> Some (WInt capPoolInt.[k % capPoolInt.Length])
                | Some (ETFloat32 | ETFloat64) -> Some (WFloat capPoolFloat.[k % capPoolFloat.Length])
                | _ -> None   // bool, aggregate, function, unresolved: no evaluation
            v |> Option.iter (fun v -> captured.[id] <- v; capturedOrder.Add((name, id)))
            v
    // Captured ARRAY cells (D12): a read `w(i, j)` of a captured array at
    // integer coordinates evaluates to a sampled value, per cell -- the law
    // must hold for every content of the capture, exactly as for a captured
    // scalar. Keyed by (array binder, coordinates), memoized, so the same cell
    // reads the same value across a swap; pools disjoint from the scalars'.
    let cellPoolInt = [ 23L; -9L; 31L; 4L; -15L; 27L; 6L; -21L ]
    let cellPoolFloat = [ 0.3125; -1.875; 2.6875; 4.0625; -0.5625; 1.4375; 3.8125; -2.9375 ]
    let cells = System.Collections.Generic.Dictionary<IRId * int64 list, WitnessVal>()
    let cellOrder = System.Collections.Generic.List<string * (IRId * int64 list)>()
    // A cell read honours the captured array's DECLARED storage class: a
    // compact symmetric group answers every permutation of its coordinates
    // from one cell (coordinates sorted), an antisymmetric one negates by the
    // permutation's sign and reads 0 on a repeated coordinate -- the type
    // guarantees those laws, so sampling must not break them. Any other
    // structured slot (compound, sparse, orbit, Hermitian, a partial read)
    // is not sampled: the body stays unevaluable (trusted).
    let canonicalCell (arrTy: IRType) (coords: int64 list) : (int64 list * int) option =
        match stripUnits arrTy with
        | ArrayElem at ->
            let rec walk (slots: IRIndexType list) (cs: int64 list) (acc: int64 list) (sign: int) =
                match slots, cs with
                | [], [] -> Some (List.rev acc, sign)
                | [], _ | _, [] -> None
                | ix :: rest, _ ->
                    let r = max 1 ix.Rank
                    if ix.IxKind <> IxKPlain || cs.Length < r then None
                    else
                        let mine = List.take r cs
                        let tail = List.skip r cs
                        match ix.Symmetry with
                        | SymNone -> walk rest tail (List.rev mine @ acc) sign
                        | SymSymmetric -> walk rest tail (List.rev (List.sort mine) @ acc) sign
                        | SymAntisymmetric ->
                            if List.length (List.distinct mine) < r then Some ([], 0)
                            else
                                // Parity of the sorting permutation (count inversions).
                                let arr = List.toArray mine
                                let mutable inv = 0
                                for a in 0 .. arr.Length - 1 do
                                    for b in a + 1 .. arr.Length - 1 do
                                        if arr.[a] > arr.[b] then inv <- inv + 1
                                let s' = if inv % 2 = 0 then sign else -sign
                                walk rest tail (List.rev (List.sort mine) @ acc) s'
                        | _ -> None
            walk at.IndexTypes coords [] 1
        | _ -> Some (coords, 1)
    let cell (name: string) (id: IRId) (arrTy: IRType) (coords: int64 list) (e: TypedExpr) : WitnessVal option =
        match canonicalCell arrTy coords with
        | None -> None
        | Some (_, 0) ->
            (match witnessKindOf e.Type with
             | Some (ETInt32 | ETInt64) -> Some (WInt 0L)
             | Some (ETFloat32 | ETFloat64) -> Some (WFloat 0.0)
             | _ -> None)
        | Some (canon, sign) ->
        let negIf (v: WitnessVal) =
            if sign >= 0 then v
            else
                match v with
                | WInt i -> WInt (0L - i)
                | WFloat f -> WFloat (-f)
                | b -> b
        let key = (id, canon)
        match cells.TryGetValue key with
        | true, v -> Some (negIf v)
        | _ ->
            let k = cells.Count
            let v =
                match witnessKindOf e.Type with
                | Some (ETInt32 | ETInt64) -> Some (WInt (cellPoolInt.[k % cellPoolInt.Length] + int64 (k / cellPoolInt.Length)))
                | Some (ETFloat32 | ETFloat64) -> Some (WFloat (cellPoolFloat.[k % cellPoolFloat.Length] + float (k / cellPoolFloat.Length)))
                | _ -> None
            v |> Option.iter (fun v ->
                cells.[key] <- v
                let label = name + "(" + (canon |> List.map string |> String.concat ", ") + ")"
                cellOrder.Add((label, key)))
            v |> Option.map negIf
    // Inlining guards: the resolver only answers non-recursive functions, so
    // the depth cap bounds a long call chain, not a cycle; the per-evaluation
    // budget bounds the fan-out of a chain that calls each callee more than
    // once (2^depth evaluations). Past either, the body answers None -- the
    // claim stays trusted, as for any body the evaluator cannot decide.
    let mutable inlineDepth = 0
    let mutable inlineBudget = 0
    let panic msg = raise (WitnessPanic msg)
    let asFloat (v: WitnessVal) =
        match v with
        | WInt i -> float i
        | WFloat f -> f
        | WBool b -> if b then 1.0 else 0.0
    let truthy (v: WitnessVal) =
        match v with
        | WBool b -> b
        | WInt i -> i <> 0L
        | WFloat f -> f <> 0.0
    // Integer `^`: exact modulo 2^64 (Numerics.intPow64At).
    let intPow (b: int64) (e: int64) =
        if e < 0L then panic "integer power with a negative exponent"
        let mutable r = 1UL
        let mutable x = uint64 b
        let mutable n = uint64 e
        while n <> 0UL do
            if n &&& 1UL <> 0UL then r <- r * x
            x <- x * x
            n <- n >>> 1
        int64 r
    // Real `^` (Numerics.realPow / realPowInt): x * x at exactly 2, repeated
    // squaring at an integer exponent, else pow.
    let floatPowInt (b: float) (e: int64) =
        let mutable r = 1.0
        let mutable x = b
        let mutable n = if e < 0L then uint64 (0L - e) else uint64 e
        while n <> 0UL do
            if n &&& 1UL <> 0UL then r <- r * x
            x <- x * x
            n <- n >>> 1
        if e < 0L then 1.0 / r else r
    let f2i (x: float) (target: ElemType) =
        match target with
        | ETInt32 ->
            if not (x >= -2147483648.0 && x < 2147483648.0) then
                panic "float-to-integer conversion of NaN or an out-of-range value"
            int64 (int32 x)
        | _ ->
            if not (x >= -9223372036854775808.0 && x < 9223372036854775808.0) then
                panic "float-to-integer conversion of NaN or an out-of-range value"
            int64 x
    let rec ev (x: float) (y: float) (e: TypedExpr) : WitnessVal option =
        let go = ev x y
        // Int32 nodes narrow (wrap) to 32 bits; every other int is Int64.
        let narrow (v: int64) =
            match witnessKindOf e.Type with
            | Some ETInt32 -> int64 (int32 v)
            | _ -> v
        match e.Kind with
        | TExprLit (LitInt v) -> Some (WInt v)
        | TExprLit (LitFloat v) -> Some (WFloat v)
        | TExprLit (LitBool b) -> Some (WBool b)
        | TExprVar (_, id, _) when id = pi -> Some (paramVal x e)
        | TExprVar (_, id, _) when id = pj -> Some (paramVal y e)
        | TExprVar (_, id, _) when fixedIdx.ContainsKey id ->
            (match fixedSeen.TryGetValue id with
             | true, v -> Some v
             | _ ->
                 let asInt =
                     match witnessKindOf e.Type with
                     | Some (ETInt32 | ETInt64) -> true
                     | Some (ETFloat32 | ETFloat64) -> false
                     | _ -> isInt
                 let v = otherVal fixedIdx.[id] asInt
                 fixedSeen.[id] <- v
                 Some v)
        // Every other variable the evaluator can reach is FREE in the body:
        // it descends into no binding form (a surviving `let`, a block with
        // statements, a match, a lambda all answer None below), so this is a
        // capture, never a local.
        | TExprVar (name, id, _) -> capture name id e
        | TExprBlock ([], Some f) -> go f
        // `|> compute` on a scalar is the identity (the parser binds a
        // trailing `|> compute` into the last branch of a kernel's `if`).
        | TExprCompute inner | TExprPure inner -> go inner
        | TExprBinOp (Elementwise, op, l, r) ->
            (match go l, go r with
             | Some a, Some b ->
                 match a, b, op with
                 | WInt p, WInt q, _ ->
                     match op with
                     | OpAdd -> Some (WInt (narrow (p + q)))
                     | OpSub -> Some (WInt (narrow (p - q)))
                     | OpMul -> Some (WInt (narrow (p * q)))
                     | OpDiv ->
                         if q = 0L then panic "integer division by zero"
                         elif q = -1L then Some (WInt (narrow (0L - p)))
                         else Some (WInt (narrow (p / q)))
                     | OpMod ->
                         if q = 0L then panic "integer modulo by zero"
                         elif q = -1L then Some (WInt 0L)
                         else Some (WInt (narrow (p % q)))
                     | OpCaret -> Some (WInt (narrow (intPow p q)))
                     | OpMath2 "atan2" -> Some (WFloat (System.Math.Atan2 (float p, float q)))
                     | OpEq -> Some (WBool (p = q))
                     | OpNeq -> Some (WBool (p <> q))
                     | OpLt -> Some (WBool (p < q))
                     | OpLe -> Some (WBool (p <= q))
                     | OpGt -> Some (WBool (p > q))
                     | OpGe -> Some (WBool (p >= q))
                     | OpAnd -> Some (WBool (p <> 0L && q <> 0L))
                     | OpOr -> Some (WBool (p <> 0L || q <> 0L))
                     | _ -> None
                 | (WBool _, _, (OpAnd | OpOr | OpEq | OpNeq)) | (_, WBool _, (OpAnd | OpOr | OpEq | OpNeq)) ->
                     (match op with
                      | OpAnd -> Some (WBool (truthy a && truthy b))
                      | OpOr -> Some (WBool (truthy a || truthy b))
                      | OpEq -> Some (WBool (truthy a = truthy b))
                      | _ -> Some (WBool (truthy a <> truthy b)))
                 | _ ->
                     // Real arithmetic (a mixed pair only arises from an
                     // operand kind the checker could not pin; it promotes,
                     // as the old all-float evaluator did).
                     let p = asFloat a
                     let q = asFloat b
                     match op with
                     | OpAdd -> Some (WFloat (p + q))
                     | OpSub -> Some (WFloat (p - q))
                     | OpMul -> Some (WFloat (p * q))
                     | OpDiv -> Some (WFloat (p / q))
                     | OpMod -> Some (WFloat (p - q * System.Math.Truncate (p / q)))
                     | OpCaret ->
                         (match b with
                          | WInt n -> Some (WFloat (floatPowInt p n))
                          | _ -> Some (WFloat (if q = 2.0 then p * p else System.Math.Pow (p, q))))
                     | OpMath2 "atan2" -> Some (WFloat (System.Math.Atan2 (p, q)))
                     | OpEq -> Some (WBool (p = q))
                     | OpNeq -> Some (WBool (p <> q))
                     | OpLt -> Some (WBool (p < q))
                     | OpLe -> Some (WBool (p <= q))
                     | OpGt -> Some (WBool (p > q))
                     | OpGe -> Some (WBool (p >= q))
                     | OpAnd -> Some (WBool (p <> 0.0 && q <> 0.0))
                     | OpOr -> Some (WBool (p <> 0.0 || q <> 0.0))
                     | _ -> None
             | _ -> None)
        | TExprUnaryOp (op, inner) ->
            (match go inner with
             | Some a ->
                 match op, a with
                 | OpNeg, WInt i -> Some (WInt (narrow (0L - i)))
                 | OpNeg, WFloat f -> Some (WFloat (-f))
                 | OpNot, _ -> Some (WBool (not (truthy a)))
                 | (OpReal | OpConj), (WInt _ | WFloat _) -> Some a
                 | OpMath "abs", WInt i -> Some (WInt (narrow (if i < 0L then 0L - i else i)))
                 | OpMath ("floor" | "ceil"), WInt _ -> Some a
                 | OpMath name, (WInt _ | WFloat _) ->
                     let f = asFloat a
                     let r =
                         match name with
                         | "exp" -> Some (exp f)
                         | "log" -> Some (log f)
                         | "sqrt" -> Some (sqrt f)
                         | "sin" -> Some (sin f)
                         | "cos" -> Some (cos f)
                         | "tan" -> Some (tan f)
                         | "tanh" -> Some (tanh f)
                         | "abs" -> Some (abs f)
                         | "floor" -> Some (floor f)
                         | "ceil" -> Some (ceil f)
                         | _ -> None
                     r |> Option.map WFloat
                 | OpCast name, (WInt _ | WFloat _) ->
                     (match Blade.Types.castTargetOf name, a with
                      | Some ((ETInt32 | ETInt64) as t), WFloat f -> Some (WInt (f2i f t))
                      | Some ETInt32, WInt i -> Some (WInt (int64 (int32 i)))
                      | Some ETInt64, WInt _ -> Some a
                      | Some (ETFloat32 | ETFloat64), _ -> Some (WFloat (asFloat a))
                      | _ -> None)
                 | _ -> None
             | None -> None)
        | TExprIf (c, t, f) ->
            (match go c with
             | Some cv -> if truthy cv then go t else go f
             | None -> None)
        // A call of a pure, non-recursive user function: its body with the
        // arguments substituted for its parameters (D12). Substitution is
        // capture-free (binder ids are globally unique); a body outside the
        // rewritable world answers None, as an unevaluable kernel does.
        | TExprApp ({ Kind = TExprVar (_, fid, _) }, args) when inlineDepth < 16 ->
            inlineBudget <- inlineBudget + 1
            if inlineBudget > 256 then None else
            (match inlineFn fid with
             | Some (ps, fbody) when ps.Length = args.Length ->
                 let substituted =
                     List.zip ps args
                     |> List.fold (fun acc (p, a) -> acc |> Option.bind (substVar p a)) (Some fbody)
                 (match substituted with
                  | Some b ->
                      inlineDepth <- inlineDepth + 1
                      try go (flattenBindings b)
                      finally inlineDepth <- inlineDepth - 1
                  | None -> None)
             | _ -> None)
        // A read of a CAPTURED array at integer coordinates (not a parameter
        // or a fixed other input): a sampled cell value (see `cell`).
        | TExprIndex ({ Kind = TExprVar (name, aid, _) } as arrE, idxs, _)
                when aid <> pi && aid <> pj && not (fixedIdx.ContainsKey aid) && not (List.isEmpty idxs) ->
            let coords = idxs |> List.map go
            if coords |> List.forall (function Some (WInt _) -> true | _ -> false) then
                cell name aid arrE.Type (coords |> List.map (function Some (WInt i) -> i | _ -> 0L)) e
            else None
        | _ -> None
    /// One evaluation: Ok value, Error panic message, or None (no evaluation).
    let run (a: float) (b: float) : Result<WitnessVal, string> option =
        inlineBudget <- 0
        try ev a b body |> Option.map Ok
        with WitnessPanic msg -> Some (Error msg)
    let finite (v: WitnessVal) =
        match v with
        | WFloat f -> System.Double.IsFinite f
        | _ -> true
    let negV (v: WitnessVal) =
        match v with
        | WInt i -> WInt (0L - i)   // the wrapping negation the law is stated in
        | WFloat f -> WFloat (-f)
        | b -> b
    let lawHolds (expected: WitnessVal) (v: WitnessVal) =
        match expected, v with
        | WInt p, WInt q -> p = q
        | WBool p, WBool q -> p = q
        | _ ->
            let p = asFloat expected
            let q = asFloat v
            abs (q - p) <= 1e-9 * (max 1.0 (max (abs p) (abs q)))
    let render = renderWitnessVal
    let witness (failure: LawFailure) =
        { Failure = failure
          Fixed = others |> List.choose (fun id ->
                      match fixedSeen.TryGetValue id with
                      | true, v -> Some (id, render v)
                      | _ -> None)
          Captured =
              (capturedOrder |> Seq.map (fun (n, id) -> (n, render captured.[id])) |> List.ofSeq)
              @ (cellOrder |> Seq.map (fun (label, key) -> (label, render cells.[key])) |> List.ofSeq) }
    let samples =
        if isInt then [ (2.0, 1.0); (1.0, 3.0); (3.0, 5.0); (-2.0, 7.0); (4.0, 9.0) ]
        else [ (2.0, 1.0); (1.0, 3.0); (0.5, 4.0); (-1.5, 2.5); (3.0, 5.0) ]
    let diagonalSamples = if isInt then [ 2.0; -3.0; 5.0 ] else [ 2.0; -1.5; 0.5 ]
    let swapFailure () =
        let usesPair = usesVar pi body || usesVar pj body
        // Anticommutativity is a claim even about a body that ignores the pair:
        // a constant c != 0 has f(y, x) = c, not -c.
        if skipSwap || (not usesPair && not negate) then None
        else
            samples
            |> List.tryPick (fun (a, b) ->
                match run a b, run b a with
                | Some (Ok u), Some (Ok v) when finite u && finite v ->
                    let expected = if negate then negV u else u
                    if lawHolds expected v then None
                    else
                        let pa = render (seedVal a)
                        let pb = render (seedVal b)
                        Some (witness (SwapLawFails (pa, pb, render u, render v, render expected)))
                | _ -> None)
    let diagonalFailure () =
        if not negate then None
        else
            diagonalSamples
            |> List.tryPick (fun a ->
                match run a a with
                | Some (Ok v) ->
                    let isZero =
                        match v with
                        | WInt i -> i = 0L
                        | WBool _ -> true   // a boolean body has no sign law to store
                        | WFloat f -> not (System.Double.IsFinite f) || abs f <= 1e-9 * (max 1.0 (abs a))
                    if isZero then None
                    else Some (witness (DiagonalNonzero (render (seedVal a), render v)))
                | Some (Error msg) ->
                    Some (witness (DiagonalNonzero (render (seedVal a), $"a panic ({msg})")))
                | None -> None)
    swapFailure () |> Option.orElseWith diagonalFailure

// Late tier: arity-polymorphic (Poly-pack) kernels -- the all-arity
// exchange law.
//
// A pack's adjacent pairs exist only per materialized arity, but the
// canonical head::tail recursion makes one decl-level check suffice for
// EVERY arity: g(x1) op...op g(xn) is fully symmetric when `op` is AC with
// the same g at base/step (AC-fold induction; antisymmetric `op` can't
// satisfy the SIGNED exchange law, so packs claim only PInv/PBottom, never
// PNeg). Wrapper kernels (comoment = mean(prod(a))) inherit this
// compositionally: invariant iff every pack-touching part whole-pack-calls an invariant function.

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

/// The all-arity pack-fold template check for Poly-pack function `fname` with pack parameter `packId`:
///
///     match arity(pack) with
///     | 1 -> { let head :: tail = pack; g(head) }
///     | _ -> { let head :: tail = pack; g(head) op fname(tail) }
///
/// PInv when `op` is associative AND commutative (+ * && ||), the two g's
/// are structurally identical (mirrorEq over the head binders), and neither
/// touches a tail or the pack. Else PBottom; PNeg is impossible (no signed exchange law).
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

/// Compositional pack parity for WRAPPER functions: is the body invariant
/// under any permutation of the pack's elements? Invariance composes
/// through EVERY operator (permuting invariant subvalues changes nothing).
/// Base cases: expressions untouching the pack, `arity(pack)`, a whole-pack
/// call to an already-invariant function, and the bare pack itself (unknown).
let packParityOf (resolver: IRId -> Parity option) (packId: IRId) (body: TypedExpr) : Parity =
    let rec go (e: TypedExpr) : Parity =
        let allInv es = if es |> List.forall (fun x -> go x = PInv) then PInv else PBottom
        match e.Kind with
        | _ when not (usesVar packId e) -> PInv
        | TExprVar _ -> PBottom   // the bare pack (pack-free vars hit the guard above)
        | TExprApp ({ Kind = TExprVar (_, fid, _) }, args) ->
            let argOk (a: TypedExpr) =
                match a.Kind with
                | TExprVar (_, aid, _) when aid = packId ->
                    (match resolver fid with Some PInv -> true | _ -> false)
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
        | TExprMatch (scrut, cases) ->
            // Invariance composes through every construct, so each part need
            // only be invariant. Requiring the SCRUTINEE invariant keeps
            // pattern binders safe too: `match pack with h :: t` has a
            // bare-pack scrutinee (PBottom), so a decomposed binder is safe.
            allInv (scrut :: (cases |> List.collect (fun c ->
                c.Body :: (match c.Guard with Some g -> [g] | None -> [])
                        @ patGuardExprs c.Pattern)))
        | TExprBlock ([], Some inner) -> go inner
        | _ -> PBottom
    // Same normalization the fixed-arity entry points get: `{ let s =
    // prod(a); mean(s) }` flattens to `mean(prod(a))` for the wrapper walk
    // (deducePackFold keeps the RAW body; DSConsRest is skipped by flattening).
    go (flattenBindings body)

// Late tier, witness half: a declared comm/anticomm on a Poly pack.
//
// A pack is ONE declared parameter, so the decl-level witness (which builds
// its pairs from parameter indices) never saw a pair inside it, and IRMono
// expands the group per arity without re-checking it: `packdiv(a) where
// comm(a) = head / packdiv(tail)` stored a triangle and served 1/4 for 4
// (N2). The pack's pairs exist per ARITY, so the body is unrolled at small
// arities into the closed fixed-arity body `specializeFunction` builds for
// the primal -- `arity(view)` folded, `match arity(view)` reduced to its live
// arm, `let head :: tail = view` and `view[k]` resolved to expanded element
// parameters, the self call re-entered on the tail view -- and the ordinary
// witness runs on each claimed pair of the expanded group. Every shape
// outside that world answers None: no unrolled body, no witness, and the
// claim stays trusted exactly as before (a precision loss, never a false
// refusal).

/// The pack arities the witness tries. Two operands already exhibit most
/// failures; three and four catch folds whose small instances happen to hold.
let private packWitnessArities = [ 2; 3; 4 ]

/// Unroll a pack function body at a concrete view. `views` maps a pack-view
/// binder id (the declared pack, every `tail`) to the element expressions it
/// still holds; `viewNames` is the same by surface name, for `arity(name)`.
let rec private unrollPackBody (fname: string) (declPackId: IRId) (declPackName: string)
                               (declBody: TypedExpr) (budget: int ref)
                               (views: Map<IRId, TypedExpr list>) (viewNames: Map<string, TypedExpr list>)
                               (e: TypedExpr) : TypedExpr option =
    let go = unrollPackBody fname declPackId declPackName declBody budget views viewNames
    let gos (es: TypedExpr list) =
        let rs = es |> List.map go
        if rs |> List.forall Option.isSome then Some (rs |> List.map Option.get) else None
    let ok k = Some { e with Kind = k }
    let touchesView (x: TypedExpr) =
        views |> Map.exists (fun vid _ -> usesVar vid x)
    let viewOf (x: TypedExpr) =
        match (unwrapBlock x).Kind with
        | TExprVar (_, vid, _) -> Map.tryFind vid views
        | _ -> None
    match e.Kind with
    | TExprArity name ->
        (match Map.tryFind name viewNames with
         | Some elems -> ok (TExprLit (LitInt (int64 elems.Length)))
         | None -> None)
    | TExprVar (_, vid, _) when Map.containsKey vid views -> None   // a bare pack: not unrollable here
    | TExprTupleIndex (p, { Kind = TExprLit (LitInt k) }) when (viewOf p).IsSome ->
        let elems = (viewOf p).Value
        if k >= 0L && k < int64 elems.Length then Some elems.[int k] else None
    | TExprApp ({ Kind = TExprVar (n, _, _) }, [arg]) when n = fname && (viewOf arg).IsSome ->
        budget.Value <- budget.Value - 1
        let elems = (viewOf arg).Value
        // An empty view is fine: `match arity(view)` picks a `| 0` base arm,
        // and a `let head :: tail` of it answers None in `walk`.
        if budget.Value < 0 then None
        else
            unrollPackBody fname declPackId declPackName declBody budget
                (Map.ofList [ declPackId, elems ]) (Map.ofList [ declPackName, elems ]) declBody
    | TExprMatch (scrut, cases) ->
        (match (unwrapBlock scrut).Kind with
         | TExprArity name when Map.containsKey name viewNames ->
             let k = int64 viewNames.[name].Length
             // The first arm that matches the literal arity, as the primal's
             // specialization reduces it; a guard or any other pattern is
             // outside the world (None), never skipped.
             let rec pick (cs: TypedMatchCase list) =
                 match cs with
                 | [] -> None
                 | c :: rest ->
                     match c.Pattern.Kind, c.Guard with
                     | TPatLit (LitInt n), None -> if n = k then Some c else pick rest
                     | TPatWild, None -> Some c
                     | _ -> None
             pick cases |> Option.bind (fun c -> go c.Body)
         | _ -> if touchesView e then None else Some e)
    | TExprBlock (stmts, final) ->
        // Thread the statements: a `let head :: tail = view` binds the head to
        // the view's first element (substituted into everything after it) and
        // the tail to the rest; a plain let is kept, its value unrolled.
        let rec walk (vs: Map<IRId, TypedExpr list>) (vns: Map<string, TypedExpr list>)
                     (ss: TypedStmt list) (acc: TypedStmt list) (substs: (IRId * TypedExpr) list) =
            let subAll (x: TypedExpr) =
                substs |> List.fold (fun st (vid, repl) -> st |> Option.bind (substVar vid repl)) (Some x)
            let goIn = unrollPackBody fname declPackId declPackName declBody budget vs vns
            match ss with
            | [] ->
                (match final with
                 | None -> None
                 | Some f ->
                     subAll f |> Option.bind goIn
                     |> Option.map (fun f' -> { e with Kind = TExprBlock (List.rev acc, Some f') }))
            | TStmtLet b :: rest ->
                (match b.Destructure, b.SubBindings, (unwrapBlock b.Value).Kind with
                 | DSConsRest, [ (_, headId, _); (tailName, tailId, _) ], TExprVar (_, vid, _) when Map.containsKey vid vs ->
                     (match vs.[vid] with
                      | h :: t -> walk (Map.add tailId t vs) (Map.add tailName t vns) rest acc ((headId, h) :: substs)
                      | [] -> None)
                 | DSPositional, [], _ when List.isEmpty b.PostChecks ->
                     subAll b.Value |> Option.bind goIn
                     |> Option.bind (fun v -> walk vs vns rest (TStmtLet { b with Value = v } :: acc) substs)
                 | _ -> None)
            | _ -> None
        walk views viewNames stmts [] []
    | TExprBinOp (m, op, l, r) ->
        (match go l, go r with
         | Some a, Some b -> ok (TExprBinOp (m, op, a, b))
         | _ -> None)
    | TExprUnaryOp (op, i) -> go i |> Option.bind (fun a -> ok (TExprUnaryOp (op, a)))
    | TExprIf (c, t, f) ->
        (match go c, go t, go f with
         | Some a, Some b, Some d -> ok (TExprIf (a, b, d))
         | _ -> None)
    | TExprCompute i -> go i |> Option.bind (fun a -> ok (TExprCompute a))
    | TExprPure i -> go i |> Option.bind (fun a -> ok (TExprPure a))
    | TExprLet (n, vid, value, body) ->
        (match go value, go body with
         | Some a, Some b -> ok (TExprLet (n, vid, a, b))
         | _ -> None)
    | TExprApp (f, args) when not (touchesView f) ->
        gos args |> Option.bind (fun a -> ok (TExprApp (f, a)))
    | _ -> if touchesView e then None else Some e

/// A witnessed failure of a declared comm/anticomm group on a pack function:
/// (arity, expanded position p, expanded position q, witness), positions
/// counted over the EXPANDED parameter list -- a pack slot of arity k
/// occupies k consecutive positions, as IRMono's expandGroups lays them out.
/// `paramIds` are the declared parameters in order; `packIndex` the pack's;
/// `elemType` the pack's element type.
let witnessPackLawFailure (inlineFn: IRId -> (IRId list * TypedExpr) option)
                          (fname: string) (packName: string) (packId: IRId) (packIndex: int)
                          (paramIds: IRId list) (elemType: IRType)
                          (groups: int list list) (negate: bool) (isInt: bool) (body: TypedExpr)
                          : (int * int * int * IRId list * LawWitness) option =
    let groupsWithPack = groups |> List.filter (List.contains packIndex)
    if List.isEmpty groupsWithPack then None
    else
        packWitnessArities |> List.tryPick (fun k ->
            // Element parameters get ids no binder can hold (binder ids are
            // positive and globally unique).
            let elemIds = List.init k (fun i -> System.Int32.MinValue + 16 + i)
            let elems =
                elemIds |> List.mapi (fun i id ->
                    { Kind = TExprVar ($"{packName}[{i}]", id, None); Type = elemType; Span = body.Span })
            let budget = ref 64
            match unrollPackBody fname packId packName body budget
                      (Map.ofList [ packId, elems ]) (Map.ofList [ packName, elems ]) body with
            | None -> None
            | Some unrolled ->
                let expanded =
                    paramIds |> List.mapi (fun i id -> if i = packIndex then elemIds else [ id ]) |> List.concat
                let posOf (i: int) =
                    let start = if i <= packIndex then i else i + k - 1
                    if i = packIndex then List.init k (fun j -> start + j) else [ start ]
                groupsWithPack
                |> List.collect (fun g -> g |> List.distinct |> List.sort |> List.collect posOf |> List.pairwise)
                |> List.distinct
                |> List.tryPick (fun (p, q) ->
                    if p < 0 || q >= expanded.Length || p = q then None
                    else
                        let pi = expanded.[p]
                        let pj = expanded.[q]
                        let others = expanded |> List.filter (fun id -> id <> pi && id <> pj)
                        witnessSwapLawFailure inlineFn pi pj others negate false isInt unrolled
                        |> Option.map (fun w -> (k, p, q, expanded, w))))
