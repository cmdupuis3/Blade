// Zonking — final type resolution (audit §4: Check/Zonk.fs). After type
// checking, remaining IRTInfer nodes are either solved-but-unresolved or
// genuinely unconstrained; zonking walks the typed AST, resolves every type
// through the substitution, and defaults leftovers to Float64. Extracted
// verbatim from TypeCheck.fs (Phase 3).
module Blade.Zonk

open Blade.IR
open Blade.Types
open Blade.TypedAst
open Blade.Unify

let rec zonkType (subst: Subst) (ty: IRType) : IRType =
    let resolved = subst.Resolve ty
    match resolved with
    | IRTInfer n ->
        // Function-boundary HM type variables survive zonking — IR-phase
        // monomorphization will substitute them at call sites. Genuinely
        // unresolved (non-boundary) inference vars still default to Float64
        // for backwards compatibility with underconstrained local lets —
        // EXCEPT literal vars, which default to their seeded value class so an
        // unpinned `let x = 1` stays Int64 rather than becoming Float64.
        if subst.IsPolymorphicId(n) then resolved
        else
            match subst.GetLiteralDefault(n) with
            | Some et -> IRTScalar et
            | None -> IRTScalar ETFloat64
    | IRTScalar _ | IRTUnit | IRTNat _ | IRTNamed _ -> resolved
    | IRTTuple ts -> IRTTuple (ts |> List.map (zonkType subst))
    | IRTComputation t -> IRTComputation (zonkType subst t)
    | IRTLoop lt ->
        IRTLoop { lt with
                    ArrayTypes = lt.ArrayTypes |> List.map (zonkType subst)
                    KernelType = lt.KernelType |> Option.map (zonkType subst) }
    | IRTPoly (base', var) -> IRTPoly (zonkType subst base', var)
    | IRTUnitAnnotated (inner, units) -> IRTUnitAnnotated (zonkType subst inner, units)
    | IRTIdxTagged (inner, idxRef) -> IRTIdxTagged (zonkType subst inner, idxRef)
    | IRTDist (order, elem, axes) ->
        // ERASURE POINT: Dist<r, τ> is a typecheck-time invariant. All
        // Dist-aware checking (order guard, operator dispatch, signature
        // unification) happens during inference, before zonking; downstream
        // of the checker a Dist value IS the tuple of its packed cumulant
        // component arrays, so Lowering/IR/CodeGen never see IRTDist (the
        // CodeGen sentinel arm is the backstop if one leaks).
        let e = zonkType subst elem
        IRTTuple (distComponentTypes order e axes)
    | IRTArrow (slots, ret, identity) ->
        let zonkSlot = function
            | SIdx idx -> SIdx (zonkIndexType subst idx)
            | SIdxVirt idx -> SIdxVirt (zonkIndexType subst idx)
            | SVal ty -> SVal (zonkType subst ty)
        IRTArrow (slots |> List.map zonkSlot, zonkType subst ret, identity)
    | IRTGroupKeys (outer, source, enumValues) -> IRTGroupKeys (zonkIndexType subst outer, zonkIndexType subst source, enumValues)

and zonkIndexType (subst: Subst) (idx: IRIndexType) : IRIndexType = idx  // Extents are IRExpr, not IRType

/// Zonk a TypedParam
let zonkParam (subst: Subst) (p: TypedParam) : TypedParam =
    { p with Type = zonkType subst p.Type }

/// Zonk a TypedVarInfo
let zonkVarInfo (subst: Subst) (v: TypedVarInfo) : TypedVarInfo =
    { v with Type = zonkType subst v.Type }

/// Zonk all types in a TypedExpr tree (bottom-up)
let rec zonkExpr (subst: Subst) (expr: TypedExpr) : TypedExpr =
    let z = zonkExpr subst
    let zs = List.map z
    let zt = zonkType subst
    let kind =
        match expr.Kind with
        // Leaves
        | TExprLit _ | TExprVar _ | TExprQualified _
        | TExprArity _ | TExprRange _ | TExprReverse _
        | TExprWildcard
        | TExprSection _ -> expr.Kind
        // Unary expr
        | TExprUnaryOp (op, e) -> TExprUnaryOp (op, z e)
        | TExprPure e -> TExprPure (z e)
        | TExprCompute e -> TExprCompute (z e)
        | TExprRead e -> TExprRead (z e)
        | TExprFillRandom e -> TExprFillRandom (z e)
        | TExprRandGen (k, key, dims) -> TExprRandGen (k, z key, dims)
        | TExprRank e -> TExprRank (z e)
        | TExprDotDot (lo, hi) -> TExprDotDot (z lo, z hi)
        | TExprReynolds (k, a) -> TExprReynolds (z k, a)
        // Binary expr
        | TExprBinOp (m, op, l, r) -> TExprBinOp (m, op, z l, z r)
        | TExprBind (a, b) -> TExprBind (z a, z b)
        | TExprParallel (a, b) -> TExprParallel (z a, z b)
        | TExprFusion (a, b) -> TExprFusion (z a, z b)
        | TExprFunctorMap (f, c) -> TExprFunctorMap (z f, z c)
        | TExprChoice (a, b) -> TExprChoice (z a, z b)
        | TExprFallback (a, b) -> TExprFallback (z a, z b)
        | TExprCompose (op, a, b) -> TExprCompose (op, z a, z b)
        | TExprGuard (c, b) -> TExprGuard (z c, z b)
        | TExprMask (a, p) -> TExprMask (z a, z p)
        | TExprCompound (d, m) -> TExprCompound (z d, z m)
        | TExprIntersect (a, b) -> TExprIntersect (z a, z b)
        | TExprUnion (a, b) -> TExprUnion (z a, z b)
        | TExprUnique a -> TExprUnique (z a)
        | TExprContains (a, v) -> TExprContains (z a, z v)
        | TExprGroupBy (v, k) -> TExprGroupBy (z v, z k)
        | TExprGroupKeys ks -> TExprGroupKeys (List.map z ks)
        | TExprSort (a, k) -> TExprSort (z a, z k)
        | TExprReduce (a, k, i) -> TExprReduce (z a, z k, Option.map z i)
        | TExprProdSum args -> TExprProdSum (List.map z args)
        | TExprTranspose (a, d1, d2) -> TExprTranspose (z a, d1, d2)
        | TExprDecompact (a, d) -> TExprDecompact (z a, d)
        | TExprGram (l, r, s) -> TExprGram (z l, z r, s)
        | TExprArrayNegate a -> TExprArrayNegate (z a)
        | TExprArrayConjugate a -> TExprArrayConjugate (z a)
        | TExprExtents a -> TExprExtents (z a)
        | TExprZero -> TExprZero
        | TExprReplicate (c, b) -> TExprReplicate (z c, z b)
        | TExprAssign (l, r) -> TExprAssign (z l, z r)
        | TExprConstraintCheck (c, msg) -> TExprConstraintCheck (z c, msg)
        | TExprPartialApp (op, arg, isL) -> TExprPartialApp (op, z arg, isL)
        // Ternary
        | TExprIf (c, t, e) -> TExprIf (z c, z t, z e)
        // Indexing
        | TExprApp (f, args) -> TExprApp (z f, zs args)
        | TExprTupleIndex (t, i) -> TExprTupleIndex (z t, z i)
        | TExprPolyTail (p, drop) -> TExprPolyTail (z p, drop)
        | TExprIndex (arr, idxs, id) -> TExprIndex (z arr, zs idxs, id)
        | TExprField (obj, fld, idx) -> TExprField (z obj, fld, idx)
        // Collections
        | TExprTuple es -> TExprTuple (zs es)
        | TExprComplexLit (re, im) -> TExprComplexLit (z re, z im)
        | TExprArrayLit (es, arrTy) -> TExprArrayLit (zs es, arrTy)
        | TExprZip es -> TExprZip (zs es)
        | TExprStack es -> TExprStack (zs es)
        | TExprJoin (es, d) -> TExprJoin (zs es, d)
        | TExprSequence es -> TExprSequence (zs es)
        | TExprAlign (es, sp) -> TExprAlign (zs es, sp)
        | TExprBlocked (it, bs) -> TExprBlocked (it, z bs)
        // Structured
        | TExprLet (name, vid, value, body) -> TExprLet (name, vid, z value, z body)
        | TExprMatch (scr, cases) ->
            TExprMatch (z scr, cases |> List.map (zonkMatchCase subst))
        | TExprLambda info -> TExprLambda (zonkLambdaInfo subst info)
        | TExprStruct (tn, flds) -> TExprStruct (tn, flds |> List.map (fun (n, e) -> (n, z e)))
        | TExprBlock (stmts, final) ->
            TExprBlock (stmts |> List.map (zonkStmt subst), final |> Option.map z)
        // Loop constructs
        | TExprMethodFor info ->
            TExprMethodFor { info with
                                Arrays = zs info.Arrays
                                ArrayTypes = info.ArrayTypes |> List.map (fun at ->
                                    { at with IndexTypes = at.IndexTypes |> List.map (zonkIndexType subst) }) }
        | TExprObjectFor info ->
            TExprObjectFor { info with Kernel = z info.Kernel }
        | TExprApply info ->
            TExprApply { info with
                            Loop = z info.Loop
                            Kernel = z info.Kernel
                            Arrays = zs info.Arrays
                            ArrayTypes = info.ArrayTypes |> List.map (fun at ->
                                { at with IndexTypes = at.IndexTypes |> List.map (zonkIndexType subst) })
                            SharedIndexTypes = info.SharedIndexTypes |> List.map (zonkIndexType subst)
                            OutputType = zt info.OutputType }
    { expr with Kind = kind; Type = zt expr.Type }

and zonkMatchCase (subst: Subst) (case: TypedMatchCase) : TypedMatchCase =
    { Pattern = zonkPattern subst case.Pattern
      Guard = case.Guard |> Option.map (zonkExpr subst)
      Body = zonkExpr subst case.Body }

and zonkPattern (subst: Subst) (pat: TypedPattern) : TypedPattern =
    let zt = zonkType subst
    let kind =
        match pat.Kind with
        | TPatWild | TPatLit _ -> pat.Kind
        | TPatVar (n, id) -> TPatVar (n, id)
        | TPatTuple ps -> TPatTuple (ps |> List.map (zonkPattern subst))
        | TPatCons (h, t) -> TPatCons (zonkPattern subst h, zonkPattern subst t)
        | TPatVariant (tag, payload, isEnum) -> TPatVariant (tag, payload |> Option.map (zonkPattern subst), isEnum)
        | TPatStruct (tn, flds) -> TPatStruct (tn, flds |> List.map (fun (n, p) -> (n, zonkPattern subst p)))
        | TPatGuarded (p, e) -> TPatGuarded (zonkPattern subst p, zonkExpr subst e)
    { Kind = kind
      Type = zt pat.Type
      Bindings = pat.Bindings |> List.map (fun (n, id, ty) -> (n, id, zt ty)) }

and zonkStmt (subst: Subst) (stmt: TypedStmt) : TypedStmt =
    match stmt with
    | TStmtLet b -> TStmtLet (zonkBinding subst b)
    | TStmtAssign (l, r) -> TStmtAssign (zonkExpr subst l, zonkExpr subst r)
    | TStmtExpr e -> TStmtExpr (zonkExpr subst e)
    | TStmtForIn (name, vid, lo, hi, body) ->
        TStmtForIn (name, vid, zonkExpr subst lo, zonkExpr subst hi, body |> List.map (zonkStmt subst))

and zonkBinding (subst: Subst) (b: TypedBinding) : TypedBinding =
    let zt = zonkType subst
    { b with
        Type = zt b.Type
        Value = zonkExpr subst b.Value
        SubBindings = b.SubBindings |> List.map (fun (n, id, ty) -> (n, id, zt ty))
        PostChecks = b.PostChecks |> List.map (fun (id, e) -> (id, zonkExpr subst e)) }

and zonkLambdaInfo (subst: Subst) (info: TypedLambdaInfo) : TypedLambdaInfo =
    { info with
        Params = info.Params |> List.map (zonkParam subst)
        Body = zonkExpr subst info.Body
        ReturnType = zonkType subst info.ReturnType
        Captures = info.Captures |> List.map (zonkVarInfo subst) }

/// Zonk a TypedFunctionDecl
let zonkFunctionDecl (subst: Subst) (decl: TypedFunctionDecl) : TypedFunctionDecl =
    { decl with
        Params = decl.Params |> List.map (zonkParam subst)
        ReturnType = zonkType subst decl.ReturnType
        Body = zonkExpr subst decl.Body }

/// Zonk a TypedTypeDef
let zonkTypeDef (subst: Subst) (td: TypedTypeDef) : TypedTypeDef =
    let zt = zonkType subst
    match td with
    | TTDAlias (n, tp, ty) -> TTDAlias (n, tp, zt ty)
    | TTDStruct (n, tp, flds) ->
        TTDStruct (n, tp, flds |> List.map (fun (fn, ft) -> (fn, zt ft)))
    | TTDVariant (n, tp, vs) ->
        TTDVariant (n, tp, vs |> List.map (fun (vn, vt) -> (vn, vt |> Option.map zt)))
    | TTDIndexType _ | TTDEnumIdx _ ->
        // Index aliases carry concrete extents (literal int) and (for EnumIdx)
        // concrete value lists. No inference variables to resolve, pass through.
        td
    | TTDMutualGroup members ->
        TTDMutualGroup (members |> List.map (fun (n, ty) -> (n, zt ty)))

/// Zonk a TypedDecl
let zonkDecl (subst: Subst) (decl: TypedDecl) : TypedDecl =
    match decl with
    | TDeclLet b -> TDeclLet (zonkBinding subst b)
    | TDeclStatic b -> TDeclStatic (zonkBinding subst b)
    | TDeclFunction fd -> TDeclFunction (zonkFunctionDecl subst fd)
    | TDeclType td -> TDeclType (zonkTypeDef subst td)
    | TDeclImpl impl ->
        TDeclImpl { impl with Methods = impl.Methods |> List.map (zonkFunctionDecl subst) }
    | TDeclInterface _ | TDeclUnit _ | TDeclImport _ -> decl

/// Zonk an entire TypedModule
let zonkModule (subst: Subst) (modul: TypedModule) : TypedModule =
    { modul with Decls = modul.Decls |> List.map (zonkDecl subst) }

// ============================================================================
