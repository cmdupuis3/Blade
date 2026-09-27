// Zonking: final type resolution. After type checking, remaining IRTInfer
// nodes are either solved-but-unresolved or genuinely unconstrained; zonking
// walks the typed AST, resolves every type through the substitution, and
// defaults leftovers to Float64.
module Blade.Zonk

open Blade.IR
open Blade.Types
open Blade.TypedAst
open Blade.Unify

// Stage-2 rank deduction: closing a satisfied lower bound.

/// The array a satisfied rank lower bound closes to: `k` rank-1 SymNone slots
/// with `label`-derived symbolic extents over a caller-supplied element type.
///
/// Shared by both close sites (checkFunctionDecl's decl close and zonk's
/// auto-close below) so the two cannot drift on slot shape. Extent names are
/// cosmetic (unify never compares extents) but must be unique per close so
/// two independently-closed params never read as the same dimension in
/// emitted C++; `label` is the uniquifier the caller supplies.
let mkDeducedRankArray (freshId: unit -> IRId) (elemTy: IRType) (label: string) (k: int) : IRType =
    let slots =
        List.init k (fun i ->
            { Id = freshId ()
              Rank = 1
              Extent = IRParam ($"__{label}_deduced_n{i}", 0, IRTNat None)
              Symmetry = SymNone
              Tag = None; IxKind = IxKPlain
              Kind = SDimension
              Dependencies = [] })
    mkArrayLike { ElemType = elemTy; IndexTypes = slots; IsVirtual = false; Identity = None }

/// Close the body-only rank deduction (stage 2) over a parameter list: a param
/// whose type is still an unresolved inference var but carries a rank lower
/// bound (accumulated from the body's builtin pins and direct-call demands,
/// max-joined) is pinned to a fresh rank-k array with a free element type.
/// The minimum rank the body forces is the cell rank, since bounds only ever
/// came from this body's own uses. Params with no bound stay fully generic
/// (scalar-or-array polymorphism); params under a `T^k` annotation are
/// governed by their exact arity constraint and are skipped.
///
/// Lives here rather than in TypeCheck so zonk's auto-close (see `zonkType`)
/// can share `mkDeducedRankArray` with it -- Zonk compiles before TypeCheck,
/// which already opens this module.
let closeDeducedRanks (subst: Subst) (builder: IRBuilder) (label: string) (paramTypes: IRType list) : unit =
    paramTypes |> List.iter (fun pt ->
        match subst.Resolve pt with
        | IRTInfer id when (subst.GetArityConstraint id).IsNone ->
            (match subst.GetRankLowerBound(id) with
             | Some k when k > 0 ->
                 // Cannot fail: the var is bound-satisfying by construction
                 // (fresh rank-k array, no arity pin, no occurs possibility,
                 // not a literal var).
                 unify subst pt
                       (mkDeducedRankArray (fun () -> builder.FreshId())
                                           (builder.FreshInferType()) label k)
                 |> ignore
             | _ -> ())
        | _ -> ())

let rec zonkType (subst: Subst) (ty: IRType) : IRType =
    let resolved = subst.Resolve ty
    match resolved with
    | IRTInfer n ->
        // Function-boundary HM type variables survive zonking -- IR-phase
        // monomorphization substitutes them at call sites. Other unresolved
        // inference vars default to Float64, except literal vars, which
        // default to their seeded value class so an unpinned `let x = 1`
        // stays Int64 rather than becoming Float64.
        if subst.IsPolymorphicId(n) then resolved
        else
            // Stage-2 rank auto-close, checked before the scalar defaults
            // below. checkFunctionDecl closes declared params; a lambda param
            // (or other straggler) carrying an unsatisfied rank lower bound
            // has no such site -- inferLambda deliberately does not close
            // (it cannot know kernel position; for lambdas used as kernels,
            // buildApplyInfo's array-side fallback closes them with strictly
            // more context). Zonk is that leftover case, and reaching it with
            // a bound in hand is the deduction succeeding, so this is
            // infallible: direct construction + Bind, no unify needed. `T^k`
            // params are skipped, mirroring closeDeducedRanks. The element
            // type takes the same default the scalar arm below would have
            // produced -- a rank bound carries no element information.
            let autoCloseRank =
                if (subst.GetArityConstraint n).IsSome then None
                else subst.GetRankLowerBound(n)
            match autoCloseRank with
            | Some k when k > 0 ->
                let mkId () = match subst.Fresh() with IRTInfer i -> i | _ -> 0
                let elem =
                    match subst.GetLiteralDefault(n) with
                    | Some et -> IRTScalar et
                    | None -> IRTScalar ETFloat64
                // The label embeds the var id, so two independently-closed
                // lambdas can never collide on an extent name. Bind makes the
                // close idempotent and globally consistent (every later
                // Resolve of `n` sees this same array) and introduces no new
                // inference var, so "no IRTInfer survives zonking" still holds.
                let arr = mkDeducedRankArray mkId elem $"zonk{n}" k
                subst.Bind(n, arr)
                // Zonk-closed ranks also join the deduced-facts channel so
                // `ide check --json`'s deduced[] shows them too.
                Blade.TypeEnv.DeducedFacts.recordZonkClosedRank n k
                arr
            | _ ->
                match subst.GetLiteralDefault(n) with
                | Some et -> IRTScalar et
                // A var only ever seen as a SUBSCRIPT is an index: Int64.
                | None when subst.IsIndexDefault n -> IRTScalar ETInt64
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
        // ERASURE POINT: Dist<r, T> is a typecheck-time invariant. All
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

// ---------------------------------------------------------------------------
// SUBSCRIPT POSITIONS AND GUARDS (formalism 3.10), applied while zonking --
// the one walk that sees every node with its final type.
//
// POSITIONS. Arithmetic on an index value is a POSITION (a plain integer),
// never an index value: `i + 3` with `i : Nat<X>` is not proven to lie in X.
// An ANNOTATED operand already refuses the arithmetic (inferArithType's
// IndexTypeArithForbidden); an unannotated kernel parameter is still an open
// variable when its body is typed, so the node took the variable's type and
// became `Nat<X>` once the parameter met the iteration -- the loophole that
// let `u(i + 3)` read past u. Its type is not rewritten (the binding, the
// kernel return and the apply output all carry the same variable); instead
// the guard below trusts no type at all, only a closed PROVEN list.
//
// GUARDS. A subscript into a slot of a NAMED index type is either PROVEN or
// CHECKED. Proven is a closed list: an integer literal (judged at compile
// time), a bare VARIABLE of exactly that index type that is not bound to an
// unproven value (a lambda parameter a RANGE feeds -- RangeFedParams; every
// other lambda parameter receives data -- a function parameter -- its
// callers are coerced, see below -- or a let of a proven value), an index
// cast or guard already emitted, and a halo window read. EVERYTHING else --
// a position, a plain Int64, a `Nat<_>` wildcard, an `if` / `match` / block,
// an element read out of an index-typed array, a call -- is wrapped in a
// BL8006 guard `0 <= k < extent`, against the static extent or, for a
// runtime extent, `extents(A)` of the array variable. The same guard is
// applied to an argument meeting a `Nat<I>` PARAMETER of a direct call when
// it is not proven (a function's calls to itself are typed before its
// parameter is pinned, so the call judgment's eager coercion misses them).
//
// Stand-downs: compiler-synthesized buffers and indices (`__` names: `let
// rec` prefix buffers, reduce desugars, element-bound loops, and the AD
// sweeps, whose `__hi + offset` walks run over loop bounds already shrunk by
// the halo -- GradSweeps' interior loop), keyed / compact / ragged slots, and
// an anonymous (untagged) slot, which is not guarded at all (documented).
// ---------------------------------------------------------------------------

/// Per-zonk context: a fresh-id source for guards that must bind their index
/// (an impure one), the let-bound variables that hold UNPROVEN values, and the
/// static extent of a named index type (for call-argument guards, where the
/// function type carries only the tag).
type SubscriptGuardCtx = {
    FreshId: unit -> IRId
    Positions: System.Collections.Generic.HashSet<IRId>
    IndexExtent: string -> int64 option
    /// The kernel parameters noteDataKernelParams marked: unproven AND exempt
    /// from the `__`-name stand-down (they carry user data).
    DataVars: System.Collections.Generic.HashSet<IRId>
    /// The labels of a STRING-valued EnumIdx, in declaration (= ordinal)
    /// order; None for any other type name.
    EnumLabels: string -> string list option
    /// Lambda parameters a RANGE operand feeds (`method_for(range<I>) <@>
    /// lambda(i) -> ..`), collected over the whole module before zonk
    /// (TypeCheck.rangeFedLambdaParams). Every OTHER user lambda parameter of
    /// an index type is unproven -- a mask predicate, a sort key, a `>>@`
    /// stage, a kernel over a key column all receive DATA.
    RangeFedParams: System.Collections.Generic.HashSet<IRId>
}

/// A USER index type's values (a compiler tag -- a halo window, a `__`
/// buffer's own index -- belongs to the machinery that made it).
let private userIndexValue (t: IRType) =
    match t with
    | IRTIdxTagged (IRTScalar (ETInt32 | ETInt64), IRefNamed tag) -> not (tag.StartsWith "__")
    | IRTIdxTagged (IRTScalar (ETInt32 | ETInt64), IRefAnon _) -> true
    | _ -> false

let subscriptGuardCtx =
    new System.Threading.ThreadLocal<SubscriptGuardCtx option>(fun () -> None)

let private taggedIndexInner (t: IRType) =
    match t with
    | IRTIdxTagged (IRTScalar (ETInt32 | ETInt64) as inner, (IRefNamed _ | IRefAnon _)) -> Some inner
    | _ -> None

/// A kernel parameter carrying index-typed DATA (noteDataKernelParams) is
/// never a compiler-owned walk, whatever its name: the eta wrapper a named
/// kernel becomes names its parameters `__k..`.
let private isDataVar (vid: IRId) =
    match subscriptGuardCtx.Value with
    | Some ctx -> ctx.DataVars.Contains vid
    | None -> false

let rec private mentionsReservedName (e: TypedExpr) : bool =
    match e.Kind with
    | TExprVar (name, vid, _) -> name.StartsWith "__" && not (isDataVar vid)
    | TExprBinOp (_, _, l, r) -> mentionsReservedName l || mentionsReservedName r
    | TExprUnaryOp (_, x) -> mentionsReservedName x
    | TExprApp (f, args) -> mentionsReservedName f || args |> List.exists mentionsReservedName
    | TExprIf (c, t, f) -> mentionsReservedName c || mentionsReservedName t || mentionsReservedName f
    | TExprBlock (_, Some f) -> mentionsReservedName f
    | _ -> false

let rec private isPureIndexExpr (e: TypedExpr) : bool =
    match e.Kind with
    | TExprVar _ | TExprLit _ -> true
    | TExprBinOp (Blade.Ast.Elementwise, (Blade.Ast.OpAdd | Blade.Ast.OpSub | Blade.Ast.OpMul), l, r) ->
        isPureIndexExpr l && isPureIndexExpr r
    | TExprUnaryOp (Blade.Ast.OpNeg, x) -> isPureIndexExpr x
    | _ -> false

let private isLiteralIndex (e: TypedExpr) =
    match e.Kind with
    | TExprLit (Blade.Ast.LitInt _) -> true
    | TExprUnaryOp (Blade.Ast.OpNeg, { Kind = TExprLit (Blade.Ast.LitInt _) }) -> true
    | _ -> false

let private unprovenVar (vid: IRId) =
    match subscriptGuardCtx.Value with
    | Some ctx -> ctx.Positions.Contains vid
    | None -> false

/// Is `e` PROVEN to be a position of the index type named `tag` (see the
/// section note)? `tag = None` asks "proven for SOME named index type" (a let
/// binding, whose later use decides the slot).
let rec private isProvenIndex (tag: string option) (e: TypedExpr) : bool =
    let tagOk (t: IRType) =
        match t, tag with
        | IRTIdxTagged (_, IRefNamed n), Some want -> n = want
        | IRTIdxTagged (_, IRefNamed _), None -> true
        | IRTIdxTagged (_, IRefAnon _), _ -> true
        | _ -> false
    match e.Kind with
    | TExprLit (Blade.Ast.LitInt _) -> true
    // A negated literal is never a position: as a subscript it is refused at
    // compile time, and as an index-typed VALUE it is `-1`, group_by's
    // "excluded" key -- a `let` holding it is unproven, its reads guarded.
    | TExprUnaryOp (Blade.Ast.OpNeg, { Kind = TExprLit (Blade.Ast.LitInt _) }) -> false
    | TExprVar (_, vid, _) -> tagOk e.Type && not (unprovenVar vid)
    // an index cast / subscript guard already emitted
    | TExprBlock (stmts, Some _) when stmts |> List.exists (function
                                        | TStmtExpr { Kind = TExprConstraintCheck (_, "BL8006", _) } -> true
                                        | _ -> false) -> true
    | TExprBlock ([], Some f) -> isProvenIndex tag f
    | TExprApp (f, _) | TExprIndex (f, _, _) ->
        // a halo window read `w(o)`: the halo machinery owns the position
        (match f.Type with
         | IRTIdxTagged (_, IRefNamed t) -> t.StartsWith haloWinTagPrefix
         | _ -> false)
    | _ -> false

/// The BL8006 guard around one unproven index `a` against extent `ext`.
let private guardIndex (a: TypedExpr) (ext: TypedExpr) (what: string) : TypedExpr =
    let span = a.Span
    let mk k ty = mkTypedSpan k ty span
    let intTy = IRTScalar ETInt64
    let boolTy = IRTScalar ETBool
    let msg = $"index out of bounds: {what}"
    let guardOn (v: TypedExpr) =
        let cond =
            mk (TExprBinOp (Blade.Ast.Elementwise, Blade.Ast.OpAnd,
                            mk (TExprBinOp (Blade.Ast.Elementwise, Blade.Ast.OpLe, mk (TExprLit (Blade.Ast.LitInt 0L)) intTy, v)) boolTy,
                            mk (TExprBinOp (Blade.Ast.Elementwise, Blade.Ast.OpLt, v, ext)) boolTy)) boolTy
        TStmtExpr (mk (TExprConstraintCheck (cond, "BL8006", msg)) IRTUnit)
    if isPureIndexExpr a then
        mk (TExprBlock ([ guardOn a ], Some a)) a.Type
    else
        match subscriptGuardCtx.Value with
        | Some ctx ->
            let vid = ctx.FreshId ()
            let name = $"__sub{vid}"
            let v = mk (TExprVar (name, vid, None)) a.Type
            let tb : TypedBinding = {
                Name = name; VarId = vid; Type = a.Type
                Identity = None; IsMutable = false; Value = a
                SubBindings = []; Destructure = DSPositional; PostChecks = [] }
            mk (TExprBlock ([ TStmtLet tb; guardOn v ], Some v)) a.Type
        | None ->
            failwith "internal: an impure subscript needs a guard binding, but zonk ran without a SubscriptGuardCtx (only TypeCheck.checkModule may zonk a module)"

/// A slot the guard understands: a plain, dense, one-coordinate slot of a
/// user-NAMED index type.
let private guardableSlot (ix: IRIndexType) : string option =
    match ix.Tag with
    | Some tag when not (tag.StartsWith "__") && ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank <= 1 ->
        Some tag
    | _ -> None

/// A STRING key subscripting a string-valued EnumIdx slot -- a cell of a
/// foreign-key column `Array<RegionIdx like StationIdx>` read as
/// `region_weight(r)` -- is mapped to its ORDINAL here, where its final type
/// is known (the kernel parameter carrying it was still open when the
/// subscript was typed). A literal label is folded at type-check time
/// (TypeCheckSupport.foldEnumIdxLabels); this is its run-time twin, built
/// from ordinary nodes both lanes already evaluate:
///     { let __ek = key; check(__ek == l0 || ... , BL8006);
///       if __ek == l0 then 0 else if ... else n-1 }
/// A key outside the label set aborts (BL8006) instead of reading some cell.
let private enumKeyOrdinal (ix: IRIndexType) (a: TypedExpr) : TypedExpr option =
    let isStringKey =
        match IR.stripUnits a.Type with
        | IRTIdxTagged (IRTScalar ETString, _) | IRTScalar ETString -> true
        | _ -> false
    match subscriptGuardCtx.Value, ix.Tag with
    | Some ctx, Some tag when isStringKey ->
        match ctx.EnumLabels tag with
        | Some labels when not labels.IsEmpty ->
            let span = a.Span
            let mk k ty = mkTypedSpan k ty span
            let intTy = IRTScalar ETInt64
            let boolTy = IRTScalar ETBool
            let vid = ctx.FreshId ()
            let name = $"__ek{vid}"
            let v = mk (TExprVar (name, vid, None)) a.Type
            let eq (s: string) =
                mk (TExprBinOp (Blade.Ast.Elementwise, Blade.Ast.OpEq, v, mk (TExprLit (Blade.Ast.LitString s)) a.Type)) boolTy
            let cond =
                labels |> List.map eq
                |> List.reduce (fun l r -> mk (TExprBinOp (Blade.Ast.Elementwise, Blade.Ast.OpOr, l, r)) boolTy)
            let check =
                TStmtExpr (mk (TExprConstraintCheck (cond, "BL8006", $"index out of bounds: a key that is not a label of {tag}")) IRTUnit)
            let ordinal =
                labels
                |> List.mapi (fun i s -> (i, s))
                |> List.rev
                |> List.fold (fun (acc: TypedExpr option) (i, s) ->
                    let lit = mk (TExprLit (Blade.Ast.LitInt (int64 i))) intTy
                    match acc with
                    | None -> Some lit                                   // the last label: n - 1
                    | Some rest -> Some (mk (TExprIf (eq s, lit, rest)) intTy)) None
                |> Option.get
            let tb : TypedBinding = {
                Name = name; VarId = vid; Type = a.Type
                Identity = None; IsMutable = false; Value = a
                SubBindings = []; Destructure = DSPositional; PostChecks = [] }
            Some (mk (TExprBlock ([ TStmtLet tb; check ], Some ordinal)) intTy)
        | _ -> None
    | _ -> None

/// Wrap the unproven subscripts of one (zonked) read in their guards.
let private guardSubscripts (arr: TypedExpr) (idxs: TypedExpr list) : TypedExpr list =
    let synthetic = match arr.Kind with TExprVar (n, _, _) -> n.StartsWith "__" | _ -> false
    match arr.Type with
    | ArrayElem at when not synthetic && idxs.Length <= at.IndexTypes.Length
                        && not (idxs |> List.exists (fun a -> a.Kind.IsTExprTuple)) ->
        let rank = at.IndexTypes.Length
        List.mapi (fun k (a: TypedExpr) ->
            let ix = at.IndexTypes.[k]
            match enumKeyOrdinal ix a with
            | Some ord -> ord
            | None ->
            match guardableSlot ix with
            | Some tag when not a.Kind.IsTExprWildcard
                            && not (isLiteralIndex a)
                            && not (mentionsReservedName a)
                            && not (isProvenIndex (Some tag) a) ->
                let intTy = IRTScalar ETInt64
                let mkT k ty = mkTypedSpan k ty a.Span
                let extent =
                    match Blade.IRPrint.tryEvalIntIR ix.Extent with
                    | Some n -> Some (mkT (TExprLit (Blade.Ast.LitInt n)) intTy, $"0 .. {n - 1L}")
                    | None ->
                        match arr.Kind with
                        | TExprVar _ ->
                            // runtime extent: read it off the array itself
                            let exts =
                                if rank = 1 then mkT (TExprExtents arr) intTy
                                else mkT (TExprTupleIndex (mkT (TExprExtents arr) (IRTTuple (List.replicate rank intTy)),
                                                           mkT (TExprLit (Blade.Ast.LitInt (int64 k))) intTy)) intTy
                            Some (exts, "its extent")
                        | _ -> None
                match extent with
                | Some (ext, shown) -> guardIndex a ext $"a position outside {tag} ({shown})"
                | None -> a
            | _ -> a) idxs
    | _ -> idxs

/// The same guard on an argument meeting a `Nat<I>` PARAMETER of a direct
/// call, when it is not proven (the call judgment coerces a CLOSED plain
/// integer eagerly -- this catches what was open there: a function's calls to
/// itself, typed before its parameter was pinned; eta wrappers).
let private guardIndexArgs (f: TypedExpr) (args: TypedExpr list) : TypedExpr list =
    match f.Type, subscriptGuardCtx.Value with
    | FuncElem (ps, _), Some ctx ->
        args |> List.mapi (fun k a ->
            if k >= ps.Length then a
            else
                match ps.[k] with
                | IRTIdxTagged (IRTScalar (ETInt32 | ETInt64), IRefNamed tag)
                    when not (tag.StartsWith "__")
                         && not (isLiteralIndex a)
                         && not (mentionsReservedName a)
                         && not (isProvenIndex (Some tag) a) ->
                    (match ctx.IndexExtent tag with
                     | Some n ->
                         let ext = mkTypedSpan (TExprLit (Blade.Ast.LitInt n)) (IRTScalar ETInt64) a.Span
                         guardIndex a ext $"an argument outside {tag} (0 .. {n - 1L})"
                     | None -> a)
                | _ -> a)
    | _ -> args

/// A kernel parameter bound to the ELEMENTS of an index-typed DATA array (a
/// foreign-key column: `method_for(station_region) <@> lambda(r) ->
/// region_weight(r)`) holds whatever the column stores -- data, not an
/// iteration index -- so it is UNPROVEN, exactly like a `let` of a read out
/// of that column: its subscripts are guarded. Only a range operand
/// (`range<I>`, `0..n`) hands its kernel proven positions. Must run before
/// the kernel body is zonked (the guards are placed there). A NAMED function
/// kernel (`method_for(station_region) <@> wt`) reaches the slot as its eta
/// wrapper `lambda(__k) -> wt(__k)`: marking `__k` makes the call argument
/// unproven, and guardIndexArgs guards it against `wt`'s `Nat<I>` parameter
/// (the function body keeps trusting its parameter, as for every caller).
let private noteDataKernelParams (subst: Subst) (info: TypedApplyInfo) : unit =
    match subscriptGuardCtx.Value with
    | Some ctx when not info.IsComposeApply ->
        let operands =
            info.Arrays |> List.collect (fun a ->
                match a.Kind with
                | TExprZip es -> es
                | _ -> [ a ])
        let isData (a: TypedExpr) =
            match a.Kind with
            | TExprRange _ | TExprDotDot _ -> false
            | _ -> true
        match info.Kernel.Kind with
        | TExprLambda li
        | TExprReynolds ({ Kind = TExprLambda li }, _) when operands.Length = li.Params.Length ->
            List.iter2 (fun (p: TypedParam) (a: TypedExpr) ->
                if isData a && userIndexValue (zonkType subst p.Type) then
                    ctx.Positions.Add p.VarId |> ignore
                    ctx.DataVars.Add p.VarId |> ignore) li.Params operands
        | _ -> ()
    | _ -> ()

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
        // The weights extent is a resolved static int, not a type -- only the
        // paired expression is zonked.
        | TExprRandGen (k, key, pars, weights, address, dims) ->
            TExprRandGen (k, z key, List.map z pars, weights |> Option.map (fun (w, n) -> (z w, n)), address |> Option.map (fun (s, o) -> (z s, z o)), dims)
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
        | TExprSparse (v, k) -> TExprSparse (z v, z k)
        | TExprIntersect (a, b) -> TExprIntersect (z a, z b)
        | TExprUnion (a, b) -> TExprUnion (z a, z b)
        | TExprUnique a -> TExprUnique (z a)
        | TExprContains (a, v) -> TExprContains (z a, z v)
        | TExprDisplayEmit (h, q, d, m, idOpt) -> TExprDisplayEmit (h, q, z d, m, Option.map z idOpt)
        | TExprDisplayJson (r, d) -> TExprDisplayJson (r, z d)
        | TExprDisplayNum d -> TExprDisplayNum (z d)
        | TExprDisplayStr d -> TExprDisplayStr (z d)
        | TExprGroupBy (v, k) -> TExprGroupBy (z v, z k)
        | TExprGroupKeys ks -> TExprGroupKeys (List.map z ks)
        | TExprGroupBucket gk -> TExprGroupBucket (z gk)
        | TExprSegments _ -> expr.Kind
        | TExprUngroup (g, src) -> TExprUngroup (z g, src)
        | TExprUngroupRows (rows, offs, src) -> TExprUngroupRows (zs rows, offs, src)
        | TExprSegmentsGrid _ -> expr.Kind
        | TExprUngroupGrid (g, srcs, b) -> TExprUngroupGrid (z g, srcs, b)
        | TExprSort (a, k) -> TExprSort (z a, z k)
        | TExprReduce (a, k, i) -> TExprReduce (z a, z k, Option.map z i)
        | TExprProdSum args -> TExprProdSum (List.map z args)
        | TExprTranspose (a, d1, d2) -> TExprTranspose (z a, d1, d2)
        | TExprDecompact (a, d) -> TExprDecompact (z a, d)
        | TExprGram (l, r, s) -> TExprGram (z l, z r, s)
        | TExprGramApply (l, r, x) -> TExprGramApply (z l, z r, z x)
        | TExprMatmul (l, r) -> TExprMatmul (z l, z r)
        | TExprEigh a -> TExprEigh (z a)
        | TExprLu a -> TExprLu (z a)
        | TExprLuSolve (l, p, b, t) -> TExprLuSolve (z l, z p, z b, t)
        | TExprSolve (a, b) -> TExprSolve (z a, z b)
        | TExprArrayNegate a -> TExprArrayNegate (z a)
        | TExprArrayConjugate a -> TExprArrayConjugate (z a)
        | TExprExtents a -> TExprExtents (z a)
        | TExprZero -> TExprZero
        | TExprReplicate (c, b) -> TExprReplicate (z c, z b)
        | TExprAssign (l, r) -> TExprAssign (z l, z r)
        | TExprConstraintCheck (c, code, msg) -> TExprConstraintCheck (z c, code, msg)
        | TExprBreakIf c -> TExprBreakIf (z c)
        | TExprPartialApp (op, arg, isL) -> TExprPartialApp (op, z arg, isL)
        // Ternary
        | TExprIf (c, t, e) -> TExprIf (z c, z t, z e)
        // Indexing
        | TExprApp (f, args) ->
            let f' = z f
            TExprApp (f', guardIndexArgs f' (zs args))
        | TExprTupleIndex (t, i) -> TExprTupleIndex (z t, z i)
        | TExprPolyTail (p, drop) -> TExprPolyTail (z p, drop)
        | TExprIndex (arr, idxs, id) ->
            let arr' = z arr
            TExprIndex (arr', guardSubscripts arr' (zs idxs), id)
        | TExprField (obj, fld, idx) -> TExprField (z obj, fld, idx)
        // Collections
        | TExprTuple es -> TExprTuple (zs es)
        | TExprComplexLit (re, im) -> TExprComplexLit (z re, z im)
        | TExprFma (a, b, c) -> TExprFma (z a, z b, z c)
        // THE LITERAL'S OWN ARRAY TYPE TOO, for TExprApply's reason (see its
        // ArrayTypes note below). `inferArrayLitType` snapshots the ELEMENT
        // type off `exprs.[0].Type` at the moment the literal is inferred, so
        // a literal whose first element is an as-yet-unresolved var --
        // `lambda(t) -> [t, 2.0 * t]`, where `t` is an unannotated kernel
        // param resolved later by the apply -- kept that stale var here. The
        // param itself zonks (via zonkParam), so nothing in the FUNCTION
        // signature looks wrong; only the literal's ElemType stays open, and
        // it reaches IR validation as BL6001 "unresolved type variable T?N in
        // body". Writing `[2.0 * t, t]` instead hid the bug entirely, since
        // element 0 was then already Float64.
        | TExprArrayLit (es, arrTy) ->
            TExprArrayLit (zs es,
                           { arrTy with ElemType = zt arrTy.ElemType
                                        IndexTypes = arrTy.IndexTypes |> List.map (zonkIndexType subst) })
        | TExprZip es -> TExprZip (zs es)
        | TExprStack es -> TExprStack (zs es)
        | TExprJoin (es, d) -> TExprJoin (zs es, d)
        | TExprSequence es -> TExprSequence (zs es)
        | TExprAlign (es, sp) -> TExprAlign (zs es, sp)
        // Structured
        | TExprLet (name, vid, value, body) ->
            let value' = z value
            notePosition vid (zt value.Type) value'
            TExprLet (name, vid, value', z body)
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
                                    { at with ElemType = zt at.ElemType
                                              IndexTypes = at.IndexTypes |> List.map (zonkIndexType subst) }) }
        | TExprObjectFor info ->
            TExprObjectFor { info with Kernel = z info.Kernel }
        | TExprApply info ->
            noteDataKernelParams subst info
            TExprApply { info with
                            Loop = z info.Loop
                            Kernel = z info.Kernel
                            Arrays = zs info.Arrays
                            // ELEMENT TYPES TOO, not just the index records. A
                            // loop's ArrayTypes are a SNAPSHOT taken while the
                            // body was being typed, so an element that was an
                            // open var then and got unified later (two `T^1`
                            // params' synthesized elements merging, say) kept
                            // the stale var here and reached codegen as
                            // `BLADE_UNRESOLVED_ELEM_TYPE_N`. Invisible before
                            // the element could legitimately still be a var at
                            // this point -- `loopOperandArrayType` used to
                            // hard-default every unresolved element to Float64.
                            ArrayTypes = info.ArrayTypes |> List.map (fun at ->
                                { at with ElemType = zt at.ElemType
                                          IndexTypes = at.IndexTypes |> List.map (zonkIndexType subst) })
                            SharedIndexTypes = info.SharedIndexTypes |> List.map (zonkIndexType subst)
                            OutputType = zt info.OutputType }
    // Types are left as inference made them: a position keeps the `Nat<X>`
    // its open operand gave it, because the guard (guardSubscripts) trusts no
    // type -- only the closed PROVEN list -- and retyping a value node without
    // its binding / kernel return / apply output would only make them disagree.
    { expr with Kind = kind; Type = zt expr.Type }

/// A `let` of an index type whose zonked VALUE is not PROVEN (a position, a
/// read out of an index-typed array, a call, a branch...) holds an unproven
/// value: its uses are guarded like the value itself would have been. (A
/// destructured let's LEAVES are always unproven: see zonkBinding.)
and notePosition (vid: IRId) (bindingTy: IRType) (value: TypedExpr) : unit =
    match subscriptGuardCtx.Value with
    | Some ctx when (taggedIndexInner bindingTy).IsSome && not (isProvenIndex None value) ->
        ctx.Positions.Add vid |> ignore
    | _ -> ()

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
    let value' = zonkExpr subst b.Value
    notePosition b.VarId (zt b.Type) value'
    // `let (a, b) = (keys(3), keys(0))`: a leaf of an index type is a
    // component of a value nobody proved -- unproven, whatever it holds.
    (match subscriptGuardCtx.Value with
     | Some ctx ->
         for (_, id, ty) in b.SubBindings do
             if userIndexValue (zt ty) then ctx.Positions.Add id |> ignore
     | None -> ())
    { b with
        Type = zt b.Type
        Value = value'
        SubBindings = b.SubBindings |> List.map (fun (n, id, ty) -> (n, id, zt ty))
        PostChecks = b.PostChecks |> List.map (fun (id, e) -> (id, zonkExpr subst e)) }

and zonkLambdaInfo (subst: Subst) (info: TypedLambdaInfo) : TypedLambdaInfo =
    // A user lambda parameter of an index type is PROVEN only when a range
    // feeds it; anything else hands it data (see RangeFedParams). `__`
    // parameters belong to the desugarers (noteDataKernelParams marks the
    // eta wrapper's when data feeds it).
    (match subscriptGuardCtx.Value with
     | Some ctx ->
         for p in info.Params do
             if not (p.Name.StartsWith "__") && not (ctx.RangeFedParams.Contains p.VarId)
                && userIndexValue (zonkType subst p.Type) then
                 ctx.Positions.Add p.VarId |> ignore
     | None -> ())
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
