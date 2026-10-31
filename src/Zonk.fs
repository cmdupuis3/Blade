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
                | Some et when subst.IsWideDefault n ->
                    IRTScalar (match et with
                               | ETInt32 | ETInt64 -> ETInt64
                               | ETFloat32 | ETFloat64 -> ETFloat64
                               | ETComplex64 | ETComplex128 -> ETComplex128
                               | other -> other)
                | Some et -> IRTScalar et
                // A var passed to a concrete scalar PARAMETER takes its type.
                | None when (subst.GetParamDefault n).IsSome -> IRTScalar (subst.GetParamDefault n).Value
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
    | IRTUnitAnnotated (inner, units) -> IR.unitAnnotate (zonkType subst inner) units
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
    /// The RUNTIME extent of a named index type whose extent is a program
    /// binding (`type RegionIdx = Idx<nr>` over a plain `let nr = ...`): the
    /// binding, as a variable reference the guard can compare against. The
    /// fallback when `IndexExtent` has no static value -- the named-function
    /// kernel's twin of a lambda kernel's `extents(A)` fallback.
    IndexExtentVar: string -> (string * IRId * IRType) option
    /// The kernel parameters noteDataKernelParams marked: unproven AND exempt
    /// from the `__`-name stand-down (they carry user data).
    DataVars: System.Collections.Generic.HashSet<IRId>
    /// Uses of an integer EnumIdx value zonk cannot read faithfully
    /// (intEnumEdges), reported by TypeCheck.checkModule as BL4003.
    EnumKeyErrors: System.Collections.Generic.List<Blade.Ast.Span * string>
    /// The labels of a STRING-valued EnumIdx, in declaration (= ordinal)
    /// order; None for any other type name.
    EnumLabels: string -> string list option
    /// The keys of an INTEGER-valued EnumIdx, in declaration (= ordinal)
    /// order; None for any other type name.
    EnumKeys: string -> int64 list option
    /// Lambda parameters a RANGE operand feeds (`method_for(range<I>) <@>
    /// lambda(i) -> ..`), collected over the whole module before zonk
    /// (TypeCheck.rangeFedLambdaParams). Every OTHER user lambda parameter of
    /// an index type is unproven -- a mask predicate, a sort key, a `>>@`
    /// stage, a kernel over a key column all receive DATA.
    RangeFedParams: System.Collections.Generic.HashSet<IRId>
    /// Calls the checker REPLACED after typing them (TypeEnv.ArrowRetargets,
    /// by node): a call handing an array to an arrow parameter is the call of
    /// that declaration's array instance. Zonked in place of the original.
    Retargets: System.Collections.Generic.Dictionary<TypedExpr, TypedExpr>
    /// Arrow applications whose head turned out an array (TypeEnv.
    /// ArrowSubscripts, by HEAD node): the subscript each is, zonked -- and
    /// guarded -- in place of the application.
    Subscripts: System.Collections.Generic.Dictionary<TypedExpr, TypedExpr>
    /// The grouping (`gk`, the bare group_keys name) a `group_by` result's
    /// OUTER slot came from, by that slot's id (TypeEnv.GroupOuters): what
    /// its group count, each group's length and its key type are read from.
    GroupOuter: IRId -> TypedExpr option
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
                SubBindings = []; Destructure = DSPositional; PostChecks = []; ErasedType = None }
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
///
/// An INTEGER-valued EnumIdx (the D2 ruling) maps the same way: its
/// subscripts are KEYS, so a computed integer subscript -- foreign-key data,
/// a plain Int64 -- is looked up among the keys, and one that is none of
/// them aborts with BL8006. A literal key was folded at type-check time; a
/// PROVEN index (an iteration index of `range<R>`, which counts positions, or
/// an emitted guard) is already a position and is read as one.
let private enumKeyOrdinal (ix: IRIndexType) (a: TypedExpr) : TypedExpr option =
    let isStringKey =
        match IR.stripUnits a.Type with
        | IRTIdxTagged (IRTScalar ETString, _) | IRTScalar ETString -> true
        | _ -> false
    // A value OF the integer EnumIdx type is a key whose representation is
    // already its POSITION (intEnumEdges: literals, data cells and casts
    // convert on the way in), so only a PLAIN integer is a key to look up.
    let isIntValue =
        match IR.stripUnits a.Type with
        | IRTScalar (ETInt64 | ETInt32) -> true
        | _ -> false
    let keyLabels (ctx: SubscriptGuardCtx) (tag: string) : (TypedExpr -> TypedExpr) list option =
        if isStringKey then
            ctx.EnumLabels tag
            |> Option.map (List.map (fun s -> fun (v: TypedExpr) -> mkTypedSpan (TExprLit (Blade.Ast.LitString s)) v.Type a.Span))
        elif isIntValue && not a.Kind.IsTExprWildcard then
            ctx.EnumKeys tag
            |> Option.map (List.map (fun k -> fun (v: TypedExpr) -> mkTypedSpan (TExprLit (Blade.Ast.LitInt k)) v.Type a.Span))
        else None
    match subscriptGuardCtx.Value, ix.Tag with
    | Some ctx, Some tag when isStringKey || isIntValue ->
        match keyLabels ctx tag with
        | Some labels when not labels.IsEmpty ->
            let span = a.Span
            let mk k ty = mkTypedSpan k ty span
            let intTy = IRTScalar ETInt64
            let boolTy = IRTScalar ETBool
            let vid = ctx.FreshId ()
            let name = $"__ek{vid}"
            let v = mk (TExprVar (name, vid, None)) a.Type
            let eq (s: TypedExpr -> TypedExpr) =
                mk (TExprBinOp (Blade.Ast.Elementwise, Blade.Ast.OpEq, v, s v)) boolTy
            let cond =
                labels |> List.map eq
                |> List.reduce (fun l r -> mk (TExprBinOp (Blade.Ast.Elementwise, Blade.Ast.OpOr, l, r)) boolTy)
            let what = if isStringKey then "label" else "key"
            let check =
                TStmtExpr (mk (TExprConstraintCheck (cond, "BL8006", $"index out of bounds: a key that is not a {what} of {tag}")) IRTUnit)
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
                SubBindings = []; Destructure = DSPositional; PostChecks = []; ErasedType = None }
            Some (mk (TExprBlock ([ TStmtLet tb; check ], Some ordinal)) intTy)
        | _ -> None
    | _ -> None

/// An element of a parameter pack, `P[0]` with `P: Poly<..>`.
let private isPackElement (arr: TypedExpr) =
    match arr.Kind with
    | TExprTupleIndex ({ Kind = TExprVar _; Type = pt }, { Kind = TExprLit (Blade.Ast.LitInt _) }) ->
        (match IR.stripUnits pt with
         | IRTPoly _ -> true
         | _ -> false)
    | _ -> false

/// A subscript some guard already covers -- an emitted index cast or guard --
/// or a halo window read (isProvenIndex's non-variable cases).
let private guardedOrHaloIndex (a: TypedExpr) =
    match a.Kind with
    | TExprVar _ | TExprLit _ -> false
    | _ -> isProvenIndex None a

/// PER-DIMENSION SUBSCRIPTS of a product-framed compact group, folded to
/// their compound positions (docs/formalism.md 3.2/8.4): `G3(x1, y1, x2, y2,
/// x3, y3)` over `SymIdx<3, <XIdx, YIdx>>` becomes `G3(p1, p2, p3)` with
/// p = x * extent(YIdx) + y, every non-literal coordinate guarded
/// `0 <= c < extent(factor)` FIRST (BL8006 -- guarding the folded position
/// instead would let (0, 2) over a 2-wide YIdx alias into position (1, 0)).
///
/// The typed tree keeps the per-dimension spelling until here, on purpose:
/// the subscript judgment's post-unification half judges every coordinate
/// against ITS factor (`Nat<DayIdx>` into an XIdx factor is BL4003), which
/// the eager check cannot see when the coordinate is a kernel parameter
/// whose type arrives with the iteration. After this fold both back ends see
/// the flat read and canonicalize it exactly as the flat spelling.
///
/// Factor extents: the tag's recorded literal, else the named factor's
/// declared extent. The typecheck arm (dispatchAppOrIndex's product read)
/// refused the read when neither is static, so a miss here is an internal
/// error, not a user one.
let private foldProductSubscripts (arr: TypedExpr) (idxs: TypedExpr list) : TypedExpr list =
    match arr.Type with
    | ArrayElem at when idxs.Length > Blade.IR.flatSubscriptArity at.IndexTypes
                        && idxs.Length = Blade.IR.perDimSubscriptArity at.IndexTypes
                        && not (idxs |> List.exists (fun a -> a.Kind.IsTExprWildcard)) ->
        let intTy = IRTScalar ETInt64
        let litOf (e: TypedExpr) =
            match e.Kind with
            | TExprLit (Blade.Ast.LitInt v) -> Some v
            | TExprUnaryOp (Blade.Ast.OpNeg, { Kind = TExprLit (Blade.Ast.LitInt v) }) -> Some (-v)
            | _ -> None
        let extentOf (f: IRIndexType) : int64 =
            match Blade.IRPrint.tryEvalIntIR f.Extent with
            | Some n -> n
            | None ->
                let declared =
                    match f.Tag, subscriptGuardCtx.Value with
                    | Some tag, Some ctx -> ctx.IndexExtent tag
                    | _ -> None
                match declared with
                | Some n -> n
                | None ->
                    failwith (sprintf "internal: a per-dimension subscript reached zonk with factor %s of no static extent; the typecheck arm (dispatchAppOrIndex) should have refused it" (Blade.IRPrint.ppIndexType f))
        let foldTuple (factors: IRIndexType list) (coords: TypedExpr list) : TypedExpr =
            let span = (List.head coords).Span
            let mk k ty = mkTypedSpan k ty span
            let ns = factors |> List.map extentOf
            let lits = coords |> List.map litOf
            if lits |> List.forall Option.isSome then
                // Every coordinate a literal (each judged in range at
                // typecheck): a literal position, so codegen sees exactly the
                // flat spelling `G3(p1, p2, p3)`.
                let pos =
                    List.zip lits ns
                    |> List.tail
                    |> List.fold (fun acc (c, n) -> acc * n + c.Value) (List.head lits).Value
                mk (TExprLit (Blade.Ast.LitInt pos)) intTy
            else
                let lit (n: int64) = mk (TExprLit (Blade.Ast.LitInt n)) intTy
                let coordExprs =
                    List.zip3 coords factors ns
                    |> List.map (fun (c, f, n) ->
                        match litOf c with
                        | Some v -> lit v
                        | None -> guardIndex c (lit n) $"a coordinate outside {Blade.IRPrint.ppIndexType f} (0 .. {n - 1L})")
                let mul a b = mk (TExprBinOp (Blade.Ast.Elementwise, Blade.Ast.OpMul, a, b)) intTy
                let add a b = mk (TExprBinOp (Blade.Ast.Elementwise, Blade.Ast.OpAdd, a, b)) intTy
                List.zip (List.tail coordExprs) (List.tail ns)
                |> List.fold (fun acc (c, n) -> add (mul acc (lit n)) c) (List.head coordExprs)
        let rec walk (args: TypedExpr list) (slots: IRIndexType list) (acc: TypedExpr list) =
            match args, slots with
            | [], _ | _, [] -> List.rev acc @ args
            | _, ix :: rest ->
                match Blade.IR.productFrameFactorsOf ix with
                | Some factors ->
                    let need = ix.Rank * factors.Length
                    let positions =
                        List.truncate need args
                        |> List.chunkBySize factors.Length
                        |> List.map (foldTuple factors)
                    walk (List.skip need args) rest (List.rev positions @ acc)
                | None ->
                    let span = max 1 ix.Rank
                    walk (List.skip span args) rest (List.rev (List.truncate span args) @ acc)
        walk idxs at.IndexTypes []
    | _ -> idxs

/// The integer EnumIdx a type names, with its keys: a value of it (`R`, or an
/// array element of it) is a KEY, represented as its POSITION in the key
/// list (the D2 ruling, 2026-10-16).
let private intEnumKeysOfType (ty: IRType) : (string * int64 list) option =
    match IR.stripUnits ty, subscriptGuardCtx.Value with
    | IRTIdxTagged (IRTScalar (ETInt64 | ETInt32), IRefNamed tag), Some ctx ->
        ctx.EnumKeys tag |> Option.filter (fun ks -> not ks.IsEmpty) |> Option.map (fun ks -> (tag, ks))
    | _ -> None

/// THE OBSERVED VALUE of a position-represented EnumIdx value: its key, an
/// Int64, read from the type's KEY TABLE at the position -- `[k0, k1, ...](p)`
/// (F5; it was an O(n) `if p == 0 then k0 else if ...` chain per observation).
/// Positions are in range by construction, so the read is unguarded. The C++
/// lane renders the subscripted all-literal table as the per-type static
/// `blade_arith::key_table<k0, k1, ...>::tab[p]` (CodeGenExpr, the
/// IRIndex-of-IRArrayLit arm): O(1); the interpreter reads the same table.
let private posToKey (keys: int64 list) (p: TypedExpr) : TypedExpr =
    let span = p.Span
    let mk k ty = mkTypedSpan k ty span
    let intTy = IRTScalar ETInt64
    let tableId = match subscriptGuardCtx.Value with Some ctx -> ctx.FreshId () | None -> 0
    let tableTy : IRArrayType =
        { ElemType = intTy
          IndexTypes =
            [ { Id = tableId; Rank = 1; Extent = IRLit (IRLitInt (int64 keys.Length))
                Symmetry = SymNone; Tag = None; Kind = SDimension; IxKind = IxKPlain
                Dependencies = [] } ]
          IsVirtual = false
          Identity = None }
    let table = mk (TExprArrayLit (keys |> List.map (fun k -> mk (TExprLit (Blade.Ast.LitInt k)) intTy), tableTy)) (mkArrayLike tableTy)
    mk (TExprIndex (table, [ p ], None)) intTy

/// Wrap the unproven subscripts of one (zonked) read in their guards.
let private guardSubscriptsPlain (arr: TypedExpr) (idxs: TypedExpr list) : TypedExpr list =
    let synthetic = match arr.Kind with TExprVar (n, _, _) -> n.StartsWith "__" | _ -> false
    match arr.Type with
    | ArrayElem at when not synthetic && idxs.Length <= at.IndexTypes.Length
                        && not (idxs |> List.exists (fun a -> a.Kind.IsTExprTuple)) ->
        let rank = at.IndexTypes.Length
        let mkTA (a: TypedExpr) k ty = mkTypedSpan k ty a.Span
        // The extent of slot k read off the array itself: a variable, or a
        // literal element of a parameter pack (`P[0]`, a variable once arity
        // monomorphization expands the pack).
        let runtimeExtent (a: TypedExpr) (k: int) : TypedExpr option =
            let intTy = IRTScalar ETInt64
            match arr.Kind with
            | TExprVar _
            | TExprTupleIndex ({ Kind = TExprVar _ }, { Kind = TExprLit (Blade.Ast.LitInt _) }) ->
                Some (if rank = 1 then mkTA a (TExprExtents arr) intTy
                      else mkTA a (TExprTupleIndex (mkTA a (TExprExtents arr) (IRTTuple (List.replicate rank intTy)),
                                                    mkTA a (TExprLit (Blade.Ast.LitInt (int64 k))) intTy)) intTy)
            | _ -> None
        // A RAGGED row's own length, for its inner coordinate, checked in ONE
        // short-circuit condition that first checks the OUTER coordinate
        // (when its row count is static) -- `0 <= i < rows && 0 <= j <
        // extents(u(i))` -- so the row is never formed from a bad `i`. Placed
        // on the OUTER subscript of a flat read `u(i, j)`: C++ evaluates an
        // operator[]'s argument before the call, so the check runs before the
        // row view exists. A curried read `u(i)(j)` checks on `j`. Only for row
        // expressions that are safe to evaluate twice.
        let rec pureRow (e: TypedExpr) =
            match e.Kind with
            | TExprVar _ -> true
            | TExprApp (f, xs) | TExprIndex (f, xs, _) -> pureRow f && xs |> List.forall isPureIndexExpr
            | _ -> false
        let isRagged (ix: IRIndexType) =
            match ix.IxKind with
            // (A group_by result's rows are materialized as plain arrays and keep
            // their own discipline.)
            | IxKRaggedInline | IxKRagged | IxKRaggedOpaque -> true
            | _ -> false
        let intTy = IRTScalar ETInt64
        let boolTy = IRTScalar ETBool
        let mkAt (a: TypedExpr) k ty = mkTypedSpan k ty a.Span
        let inRange (a: TypedExpr) (v: TypedExpr) (ext: TypedExpr) =
            [ mkAt a (TExprBinOp (Blade.Ast.Elementwise, Blade.Ast.OpLe, mkAt a (TExprLit (Blade.Ast.LitInt 0L)) intTy, v)) boolTy
              mkAt a (TExprBinOp (Blade.Ast.Elementwise, Blade.Ast.OpLt, v, ext)) boolTy ]
        // The row count: static when the extent is, else read off the array
        // (`extents(u)[0]`, as the plain guard below reads it). Never dropped:
        // the row view must not be formed from an unchecked outer coordinate.
        let outerConds (baseArr: TypedExpr) (baseAt: IRArrayType) (i: TypedExpr) =
            match Blade.IRPrint.tryEvalIntIR baseAt.IndexTypes.[0].Extent with
            | Some n -> inRange i i (mkAt i (TExprLit (Blade.Ast.LitInt n)) intTy)
            | None ->
                match baseArr.Kind with
                | TExprVar _ ->
                    let r = baseAt.IndexTypes.Length
                    let exts =
                        if r = 1 then mkAt i (TExprExtents baseArr) intTy
                        else mkAt i (TExprTupleIndex (mkAt i (TExprExtents baseArr) (IRTTuple (List.replicate r intTy)),
                                                      mkAt i (TExprLit (Blade.Ast.LitInt 0L)) intTy)) intTy
                    inRange i i exts
                | _ -> []
        let checkedBlock (a: TypedExpr) (conds: TypedExpr list) =
            let cond = conds |> List.reduce (fun l r -> mkAt a (TExprBinOp (Blade.Ast.Elementwise, Blade.Ast.OpAnd, l, r)) boolTy)
            mkAt a (TExprBlock ([ TStmtExpr (mkAt a (TExprConstraintCheck (cond, "BL8006", "index out of bounds: a position outside its ragged row")) IRTUnit) ], Some a)) a.Type
        let raggedFlat =
            rank = 2 && idxs.Length = 2 && isRagged at.IndexTypes.[1] && pureRow arr
            && ((Blade.IRPrint.tryEvalIntIR at.IndexTypes.[0].Extent).IsSome || arr.Kind.IsTExprVar)
            && isPureIndexExpr idxs.[0] && isPureIndexExpr idxs.[1]
            && not idxs.[1].Kind.IsTExprWildcard && not (mentionsReservedName idxs.[1])
        let raggedFlatCheck (i: TypedExpr) =
            let j = idxs.[1]
            let rowTy = mkArrayLike { at with IndexTypes = [ at.IndexTypes.[1] ] }
            let row = mkAt i (TExprIndex (arr, [ i ], None)) rowTy
            checkedBlock i (outerConds arr at i @ inRange j j (mkAt j (TExprExtents row) intTy))
        let raggedCurriedCheck (j: TypedExpr) =
            let outer =
                match arr.Kind with
                | TExprIndex (b, [ i ], _) | TExprApp (b, [ i ]) ->
                    (match b.Type with
                     | ArrayElem bat when bat.IndexTypes.Length = 2 -> outerConds b bat i
                     | _ -> [])
                | _ -> []
            checkedBlock j (outer @ inRange j j (mkAt j (TExprExtents arr) intTy))
        List.mapi (fun k (a: TypedExpr) ->
            let ix = at.IndexTypes.[k]
            // An integer EnumIdx VALUE subscripting a slot of ANOTHER type (an
            // anonymous axis) is observed: its key is the position read there.
            let a =
                match intEnumKeysOfType a.Type with
                | Some (tag, keys) when ix.Tag <> Some tag -> posToKey keys a
                | _ -> a
            match enumKeyOrdinal ix a with
            | Some ord -> ord
            | None ->
            // THE RAGGED INNER COORDINATE (formalism 3.10): checked at run
            // time against its row's own length, `extents(u(i))` (BL8006). It
            // was never checked: `u(0, 2)` on a length-1 row read row 1's
            // cell. A literal one the checker could see is judged at compile
            // time too (TypeCheckSupport.raggedInnerLiteralError).
            match k with
            | 0 when raggedFlat -> raggedFlatCheck a
            | 1 when raggedFlat -> a
            | 0 when rank = 1 && isRagged ix && pureRow arr && isPureIndexExpr a
                     && not a.Kind.IsTExprWildcard && not (mentionsReservedName a) -> raggedCurriedCheck a
            | _ ->
            // A LITERAL position into a plain slot with NO static extent --
            // `m(5)` with `m: T^1`, or `m: Array<Float64 like Idx<n>>` (formalism
            // 3.10 rule 3). Rule 3 checks a literal at compile time against the
            // static extent; here there is none, so it was checked nowhere and a
            // read past a short argument returned whatever lay beyond it. It is
            // checked at run time against the array's own extent instead, named
            // slot or anonymous alike -- rule 3 is not limited to named slots,
            // only the guarantee for COMPUTED subscripts is. In a shape-
            // specialized copy the extent is a literal again, and the test of
            // an in-range literal folds away.
            match ix.IxKind, ix.Symmetry with
            // A direct static range can lower to offset + ordinal, with no
            // array storage left to check the ordinal. Preserve its indexing
            // check explicitly, including an anonymous range's computed
            // position (which the named-axis rule below does not cover).
            | IxKPlain, SymNone when ix.Rank = 1 && rank = 1
                                     && (match arr.Kind with TExprRange _ | TExprDotDot _ -> true | _ -> false)
                                     && (Blade.IRPrint.tryEvalIntIR ix.Extent).IsSome
                                     && not (isLiteralIndex a)
                                     && not a.Kind.IsTExprWildcard
                                     && not (ix.Tag |> Option.exists (fun tag -> isProvenIndex (Some tag) a))
                                     && not (guardedOrHaloIndex a) ->
                let n = (Blade.IRPrint.tryEvalIntIR ix.Extent).Value
                let ext = mkAt a (TExprLit (Blade.Ast.LitInt n)) intTy
                guardIndex a ext $"a position outside the range (0 .. {n - 1L})"
            | IxKPlain, SymNone when ix.Rank <= 1
                                     && isLiteralIndex a
                                     && (match ix.Tag with Some t -> not (t.StartsWith "__") | None -> true)
                                     && (Blade.IRPrint.tryEvalIntIR ix.Extent).IsNone
                                     && (runtimeExtent a k).IsSome ->
                let shown = match ix.Tag with Some t -> $"{t} (its extent)" | None -> "the array's extent"
                guardIndex a (runtimeExtent a k).Value $"a literal position outside {shown}"
            // A COMPUTED position into an element of a parameter PACK
            // (`P[0](k)`, `P: Poly<T^1>`). The element's axis is unnamed here,
            // but the arity specialization hands it each caller's array --
            // a NAMED axis included, whose reads formalism 3.10 guarantees --
            // and no guard is placed after zonk. A plain declaration's
            // parameter gets an ARRAY INSTANCE per named axis instead
            // (TypeEnv.FuncAxisSubscriptParams); a pack's elements cannot be
            // fixed one by one, so the read is checked against the element's
            // own extent for every caller, named or anonymous alike.
            | IxKPlain, SymNone when ix.Rank <= 1
                                     && ix.Tag.IsNone
                                     && isPackElement arr
                                     && not (isLiteralIndex a)
                                     && not a.Kind.IsTExprWildcard
                                     && not (mentionsReservedName a)
                                     && not (guardedOrHaloIndex a)
                                     && (runtimeExtent a k).IsSome ->
                guardIndex a (runtimeExtent a k).Value "a position outside the pack element's extent"
            | _ ->
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
                        // runtime extent: read it off the array itself
                        runtimeExtent a k |> Option.map (fun exts -> (exts, "its extent"))
                match extent with
                | Some (ext, shown) -> guardIndex a ext $"a position outside {tag} ({shown})"
                | None -> a
            | _ -> a) idxs
    | _ -> idxs

/// A read of a `group_by` result's STORAGE (G1; see groupReadSubscripts for
/// the rules): `grouped(i)` -- the group coordinate, judged on the
/// subscript -- or `grouped(i, j)`, judged as ONE block that binds each
/// coordinate once (`__gi`, `__gj`), checks both, and reads with the
/// bindings, so an impure or non-trivial row coordinate (`k % 3`, `f(k)`) is
/// checked too and evaluated once. Answers the read (or the guarded row
/// read), None when `arr` is not grouped storage.
let private groupStorageRead (arr: TypedExpr) (idxs: TypedExpr list) (nodeTy: IRType) : TypedExpr option =
    let intTy = IRTScalar ETInt64
    let boolTy = IRTScalar ETBool
    let mkAt (a: TypedExpr) k ty = mkTypedSpan k ty a.Span
    let wild (a: TypedExpr) = a.Kind.IsTExprWildcard || mentionsReservedName a
    let inRange (a: TypedExpr) (v: TypedExpr) (ext: TypedExpr) =
        [ mkAt a (TExprBinOp (Blade.Ast.Elementwise, Blade.Ast.OpLe, mkAt a (TExprLit (Blade.Ast.LitInt 0L)) intTy, v)) boolTy
          mkAt a (TExprBinOp (Blade.Ast.Elementwise, Blade.Ast.OpLt, v, ext)) boolTy ]
    let conj (a: TypedExpr) (conds: TypedExpr list) =
        conds |> List.reduce (fun l r -> mkAt a (TExprBinOp (Blade.Ast.Elementwise, Blade.Ast.OpAnd, l, r)) boolTy)
    match subscriptGuardCtx.Value, arr.Type with
    | Some ctx, ArrayElem at when at.IndexTypes.Length = 2 && at.IndexTypes.[0].IxKind = IxKGroupOuter
                                   // the GROUPED storage itself (its rows are a group's members), not
                                   // a per-group kernel's result over the same group axis
                                   && at.IndexTypes.[1].IxKind = IxKGroupMember
                                   && (idxs.Length = 1 || idxs.Length = 2) && arr.Kind.IsTExprVar
                                   && not (match arr.Kind with TExprVar (n, _, _) -> n.StartsWith "__" | _ -> true) ->
        match ctx.GroupOuter at.IndexTypes.[0].Id with
        | Some { Type = IRTGroupKeys (gko, _, enumVals) } when not (wild idxs.[0]) ->
            let i = idxs.[0]
            let gname = match arr.Kind with TExprVar (n, _, _) -> n | _ -> "the grouped value"
            let keyTag = gko.Tag |> Option.filter (fun t -> not (t.StartsWith "__"))
            let staticCount =
                match enumVals with
                | Some vs -> Some (int64 vs.Length)
                | None -> Blade.IRPrint.tryEvalIntIR gko.Extent
            let keyTyped =
                match IR.stripUnits i.Type, keyTag with
                | IRTIdxTagged (_, IRefNamed t), Some r -> t = r
                | _ -> false
            let isString = match IR.stripUnits i.Type with IRTIdxTagged (IRTScalar ETString, _) -> true | _ -> false
            // A string LABEL becomes its position (checked); an integer key
            // already is one (D2).
            let i1 = if keyTyped && isString then enumKeyOrdinal gko i |> Option.defaultValue i else i
            let litValue =
                match i.Kind with
                | TExprLit (Blade.Ast.LitInt n) -> Some n
                | TExprUnaryOp (Blade.Ast.OpNeg, { Kind = TExprLit (Blade.Ast.LitInt n) }) -> Some (-n)
                | _ -> None
            let outerProven =
                keyTyped
                || (match litValue, staticCount with
                    | Some v, Some n ->
                        if v < 0L || v >= n then
                            ctx.EnumKeyErrors.Add((i.Span, $"group position {v} is outside the groups of '{gname}' (0 .. {n - 1L}): a plain integer subscript of a grouped value is a group POSITION (a key is a value of the grouping's key type)"))
                        true
                    | _ -> false)
            let jOpt = if idxs.Length = 2 && not (wild idxs.[1]) then Some idxs.[1] else None
            if outerProven && jOpt.IsNone then Some (mkAt i (TExprIndex (arr, i1 :: List.tail idxs, None)) nodeTy)
            else
                let letOf (name: string) (vid: IRId) (ty: IRType) (value: TypedExpr) =
                    TStmtLet { Name = name; VarId = vid; Type = ty
                               Identity = None; IsMutable = false; Value = value
                               SubBindings = []; Destructure = DSPositional; PostChecks = []; ErasedType = None }
                let gvid = ctx.FreshId ()
                let gv = mkAt i (TExprVar ($"__gi{gvid}", gvid, None)) i1.Type
                // The group count: static, or the grouped value's own extent.
                let count =
                    match staticCount with
                    | Some n -> mkAt i (TExprLit (Blade.Ast.LitInt n)) intTy
                    | None ->
                        mkAt i (TExprTupleIndex (mkAt i (TExprExtents arr) (IRTTuple [ intTy; intTy ]),
                                                 mkAt i (TExprLit (Blade.Ast.LitInt 0L)) intTy)) intTy
                let outerConds = if outerProven then [] else inRange i gv count
                let check (a: TypedExpr) (conds: TypedExpr list) =
                    TStmtExpr (mkAt a (TExprConstraintCheck (conj a conds, "BL8006",
                                         $"index out of bounds: a position outside the groups of {gname} or outside its group's rows")) IRTUnit)
                match jOpt with
                | None ->
                    // A row read: the guarded group coordinate.
                    let guarded = mkAt i (TExprBlock ([ letOf $"__gi{gvid}" gvid i1.Type i1; check i outerConds ], Some gv)) i1.Type
                    Some (mkAt i (TExprIndex (arr, guarded :: List.tail idxs, None)) nodeTy)
                | Some j ->
                    // A cell read: both coordinates bound once, checked, read.
                    let gjid = ctx.FreshId ()
                    let gj = mkAt j (TExprVar ($"__gj{gjid}", gjid, None)) j.Type
                    let rowTy = mkArrayLike { at with IndexTypes = [ at.IndexTypes.[1] ] }
                    let rowLen = mkAt i (TExprExtents (mkAt i (TExprIndex (arr, [ gv ], None)) rowTy)) intTy
                    let stmts =
                        [ letOf $"__gi{gvid}" gvid i1.Type i1
                          letOf $"__gj{gjid}" gjid j.Type j
                          check i (outerConds @ inRange j gj rowLen) ]
                    Some (mkAt i (TExprBlock (stmts, Some (mkAt i (TExprIndex (arr, [ gv; gj ], None)) nodeTy))) nodeTy)
        | _ -> None
    | _ -> None

/// GROUP_BY READS (G1). A `group_by` result is ragged -- groups x the
/// group's rows -- and neither coordinate was checked: over two-row groups
/// `lambda(g) -> g(2)` read past its group, and `grouped(0, 5)` read row 5 of
/// group 0. The rules are formalism 3.10's, with D2's for keys:
///   * the GROUP coordinate. A value whose TYPE is the grouping's key type
///     (`Nat<R>` over an `EnumIdx R` or `Idx` grouping) is a KEY: an integer
///     key is already its position (D2), a string label is mapped to its
///     position (BL8006 for one the type does not declare). A plain integer
///     is a POSITION -- what `group_bucket` returns, and every position of a
///     dynamic grouping -- checked against the group count: a literal at
///     type checking when the count is static (BL4003), at run time
///     otherwise (BL8006);
///   * the ROW coordinate: checked at run time against the group's own
///     length, `extents(grouped(i))` -- the grouping's offsets, which the
///     grouped value's extents table carries (CodeGenExprSupport.
///     emitGroupedExtentsTable; the interpreter's rows know their lengths),
///     so the check holds wherever the value goes, a captured kernel
///     included (BL8006); a peeled row (`lambda(g) -> g(2)`, `let g0 =
///     grouped(0)`) against `extents(g)`.
/// Answers None when `arr` is not such a read.
let private groupReadSubscripts (arr: TypedExpr) (idxs: TypedExpr list) : TypedExpr list option =
    let intTy = IRTScalar ETInt64
    let boolTy = IRTScalar ETBool
    let mkAt (a: TypedExpr) k ty = mkTypedSpan k ty a.Span
    let wild (a: TypedExpr) = a.Kind.IsTExprWildcard || mentionsReservedName a
    let inRange (a: TypedExpr) (v: TypedExpr) (ext: TypedExpr) =
        [ mkAt a (TExprBinOp (Blade.Ast.Elementwise, Blade.Ast.OpLe, mkAt a (TExprLit (Blade.Ast.LitInt 0L)) intTy, v)) boolTy
          mkAt a (TExprBinOp (Blade.Ast.Elementwise, Blade.Ast.OpLt, v, ext)) boolTy ]
    let conj (a: TypedExpr) (conds: TypedExpr list) =
        conds |> List.reduce (fun l r -> mkAt a (TExprBinOp (Blade.Ast.Elementwise, Blade.Ast.OpAnd, l, r)) boolTy)
    match subscriptGuardCtx.Value, arr.Type with
    // A PEELED ROW: a group kernel's row parameter or a let-bound row.
    | Some _, ArrayElem at when at.IndexTypes.Length = 1 && at.IndexTypes.[0].IxKind = IxKGroupMember
                                 && idxs.Length = 1 && arr.Kind.IsTExprVar
                                 && not (match arr.Kind with TExprVar (n, _, _) -> n.StartsWith "__" | _ -> true)
                                 && not (wild idxs.[0]) ->
        let j = idxs.[0]
        Some [ guardIndex j (mkAt j (TExprExtents arr) intTy) "a position outside its group's rows" ]
    // A ROW READ OF THE GROUPED VALUE, `grouped(i)`: the group coordinate
    // (groupStorageRead judges a read of one CELL, `grouped(i, j)`).
    | Some _, ArrayElem at when at.IndexTypes.Length = 2 && idxs.Length = 1 ->
        groupStorageRead arr idxs (mkArrayLike { at with IndexTypes = [ at.IndexTypes.[1] ] })
        |> Option.bind (fun e ->
            match e.Kind with
            | TExprIndex (_, [ i' ], _) -> Some [ i' ]
            | _ -> None)
    | _ -> None

let private guardSubscripts (arr: TypedExpr) (idxs: TypedExpr list) : TypedExpr list =
    match groupReadSubscripts arr idxs with
    | Some guarded -> guarded
    | None -> guardSubscriptsPlain arr idxs

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
                     | None ->
                         // A RUNTIME extent (formalism 3.10's guarantee does
                         // not depend on the extent being static): compare
                         // against the binding the extent names, read at the
                         // call -- the named-function kernel's eta wrapper
                         // `lambda(__k) -> wt(__k)` over a key column included.
                         match ctx.IndexExtentVar tag with
                         | Some (name, vid, ty) ->
                             let ext = mkTypedSpan (TExprVar (name, vid, None)) ty a.Span
                             guardIndex a ext $"an argument outside {tag} (its extent, {name})"
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

/// The EDGES of an integer EnumIdx value (D2): it is a key represented as its
/// position. On the way IN, a literal typed `R` is folded to its key's
/// position (an unknown key is BL4003 at type checking,
/// TypeCheckValidate.collectUnknownEnumKeys). On the way OUT, anything that
/// OBSERVES the value -- arithmetic, a comparison, a numeric cast, an integer
/// parameter -- reads the key (posToKey). Subscripting a slot of `R` itself
/// reads the position directly.
let private intEnumEdges (e: TypedExpr) : TypedExpr =
    let observe (x: TypedExpr) =
        match intEnumKeysOfType x.Type with
        | Some (_, keys) -> posToKey keys x
        | None -> x
    let isR (x: TypedExpr) = (intEnumKeysOfType x.Type).IsSome
    let isRArray (x: TypedExpr) =
        match IR.stripUnits x.Type with
        | ArrayElem at -> (intEnumKeysOfType at.ElemType).IsSome
        | _ -> false
    let report (sp: Blade.Ast.Span) (msg: string) =
        match subscriptGuardCtx.Value with
        | Some ctx -> ctx.EnumKeyErrors.Add((sp, msg))
        | None -> ()
    match e.Kind with
    | TExprLit (Blade.Ast.LitInt v) ->
        (match intEnumKeysOfType e.Type with
         | Some (_, keys) ->
             (match keys |> List.tryFindIndex ((=) v) with
              | Some ord -> { e with Kind = TExprLit (Blade.Ast.LitInt (int64 ord)) }
              | None -> e)
         | None -> e)
    | TExprUnaryOp (Blade.Ast.OpNeg, { Kind = TExprLit (Blade.Ast.LitInt v) }) when isR e ->
        (match intEnumKeysOfType e.Type with
         | Some (_, keys) ->
             (match keys |> List.tryFindIndex ((=) (-v)) with
              | Some ord -> { e with Kind = TExprLit (Blade.Ast.LitInt (int64 ord)) }
              | None -> e)
         | None -> e)
    | TExprBinOp (Blade.Ast.Elementwise, op, l, r) when isR l || isR r ->
        (match op with
         | Blade.Ast.OpAdd | Blade.Ast.OpSub | Blade.Ast.OpMul | Blade.Ast.OpDiv | Blade.Ast.OpMod ->
             // Arithmetic whose RESULT was typed as the EnumIdx itself: the
             // operand was a kernel parameter still open when the body was
             // typed, and the kernel's result, its loop output and its binding
             // all carry that type -- a key plus one read as a value of R.
             if isR e then
                 let tag = (intEnumKeysOfType e.Type).Value |> fst
                 report e.Span
                     ($"arithmetic on a value of the EnumIdx type '{tag}' computes on its KEY, an Int64, but this result "
                      + $"was typed as '{tag}' itself: the operand is a kernel parameter typed before it met '{tag}'. "
                      + "Spell the key where it is computed on -- `Int64(c) + 1` -- or write the kernel as "
                      + "`method_for(range<R>) <@> lambda(r) -> ...` / `method_for(xs) <@> lambda(c) -> ...`, which types it as R.")
                 e
             else
             { e with Kind = TExprBinOp (Blade.Ast.Elementwise, op, observe l, observe r) }
         | Blade.Ast.OpEq | Blade.Ast.OpNeq | Blade.Ast.OpLt | Blade.Ast.OpLe | Blade.Ast.OpGt | Blade.Ast.OpGe ->
             { e with Kind = TExprBinOp (Blade.Ast.Elementwise, op, observe l, observe r) }
         | _ -> e)
    | TExprUnaryOp (Blade.Ast.OpNeg, x) when isR x && isR e ->
        report e.Span "negating a value of an EnumIdx type computes on its KEY: spell it `-Int64(c)`"
        e
    | TExprUnaryOp ((Blade.Ast.OpCast _ | Blade.Ast.OpNeg) as op, x) when isR x ->
        let ty = if isR e then IRTScalar ETInt64 else e.Type
        { e with Kind = TExprUnaryOp (op, observe x); Type = ty }
    // A cast of a whole ARRAY of EnumIdx values, a fold over them with an
    // arithmetic section, a display of them: each would read the positions
    // that represent the keys.
    | TExprUnaryOp (Blade.Ast.OpCast _, x) when isRArray x ->
        report e.Span "a cast of an array of EnumIdx values converts each KEY: map it, `method_for(xs) <@> lambda(c) -> Int64(c)`"
        e
    | TExprProdSum args when args |> List.exists isRArray ->
        report e.Span "prodsum over EnumIdx values computes on their KEYS: map them to Int64 first, `method_for(xs) <@> lambda(c) -> Int64(c)`"
        e
    | (TExprGram (l, r, _) | TExprMatmul (l, r)) when isRArray l || isRArray r ->
        report e.Span "a contraction over EnumIdx values computes on their KEYS: map them to Int64 first, `method_for(xs) <@> lambda(c) -> Int64(c)`"
        e
    | TExprGramApply (l, r, x) when isRArray l || isRArray r || isRArray x ->
        report e.Span "a contraction over EnumIdx values computes on their KEYS: map them to Int64 first, `method_for(xs) <@> lambda(c) -> Int64(c)`"
        e
    | TExprReduce (a, { Kind = TExprSection (Blade.Ast.OpAdd | Blade.Ast.OpSub | Blade.Ast.OpMul | Blade.Ast.OpDiv | Blade.Ast.OpMod | Blade.Ast.OpCaret) }, _)
        when isRArray a ->
        report e.Span "a fold over EnumIdx values computes on their KEYS: map them to Int64 first, `reduce(method_for(xs) <@> lambda(c) -> Int64(c), (+))`"
        e
    // A sort KEY that is a value of the EnumIdx: sorted by its key, which
    // the key kernel reads (posToKey) -- positions follow declaration order.
    | TExprSort (a, ({ Kind = TExprLambda li } as k)) when isR li.Body ->
        let keys = (intEnumKeysOfType li.Body.Type).Value |> snd
        let li' = { li with Body = posToKey keys li.Body; ReturnType = IRTScalar ETInt64 }
        { e with Kind = TExprSort (a, { k with Kind = TExprLambda li' }) }
    | TExprSort (_, k) when (match IR.stripUnits k.Type with
                             | FuncElem (_, ret) -> (intEnumKeysOfType ret).IsSome
                             | _ -> false) ->
        report e.Span "a sort key that is a value of an EnumIdx sorts by its KEY: spell it, `lambda(c) -> Int64(c)`"
        e
    | (TExprDisplayEmit (_, _, d, _, _) | TExprDisplayJson (_, d) | TExprDisplayNum d) when isRArray d || isR d ->
        report e.Span "displaying EnumIdx values: map them to their keys first, `method_for(xs) <@> lambda(c) -> Int64(c)`"
        e
    | TExprApp (f, args) ->
        (match IR.stripUnits f.Type with
         // A GENERIC parameter reached by an EnumIdx value only here (a named
         // function used as a kernel: its eta wrapper's parameter was open at
         // the call) -- the instance would compute on positions.
         | FuncElem (ps, _) when List.zip (List.truncate args.Length ps) (List.truncate ps.Length args)
                                 |> List.exists (fun (p, a) -> (IR.stripUnits p).IsIRTInfer && isR a) ->
             report e.Span "a generic function applied to values of an EnumIdx (here as a kernel) would compute on the positions that represent their keys: spell the key, `lambda(c) -> f(Int64(c))`"
             e
         | FuncElem (ps, _) when args |> List.exists isR ->
             let args' =
                 args |> List.mapi (fun i a ->
                     match List.tryItem i ps |> Option.map IR.stripUnits with
                     | Some (IRTScalar (ETInt64 | ETInt32 | ETFloat64 | ETFloat32)) when isR a -> observe a
                     | _ -> a)
             { e with Kind = TExprApp (f, args') }
         | _ -> e)
    | _ -> e

/// Zonk all types in a TypedExpr tree (bottom-up)
let rec zonkExpr (subst: Subst) (expr: TypedExpr) : TypedExpr =
    match subscriptGuardCtx.Value, expr.Kind with
    | Some ctx, _ when ctx.Retargets.Count > 0 && ctx.Retargets.ContainsKey expr ->
        zonkExpr subst ctx.Retargets.[expr]
    | Some ctx, TExprApp (f, _) when ctx.Subscripts.Count > 0 && ctx.Subscripts.ContainsKey f ->
        zonkExpr subst ctx.Subscripts.[f]
    | _ ->
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
        | TExprRandGen (k, key, pars, weights, address, dims, inl) ->
            TExprRandGen (k, z key, List.map z pars, weights |> Option.map (fun (w, n) -> (z w, n)), address |> Option.map (fun (s, o) -> (z s, z o)), dims, Option.map z inl)
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
            let args' = zs args
            // Inference can resolve an application to an array only after
            // the original call node was built (including virtual ranges).
            // Lowering treats it as indexing, so install the same guards here.
            let guarded =
                match f'.Type with
                | ArrayElem _ -> guardSubscripts f' (foldProductSubscripts f' args')
                | _ -> guardIndexArgs f' args'
            TExprApp (f', guarded)
        | TExprTupleIndex (t, i) -> TExprTupleIndex (z t, z i)
        | TExprPolyTail (p, drop) -> TExprPolyTail (z p, drop)
        // A CURRIED read of a grouped value, `grouped(i)(j)`, is judged as the
        // flat `grouped(i, j)` (G1): the row coordinate is checked against
        // group i's length, which only the pair knows.
        | TExprIndex (({ Kind = TExprIndex (b, [ i ], _) } as inner), [ j ], id)
                when (match b.Type with
                      | ArrayElem bat -> bat.IndexTypes.Length = 2 && bat.IndexTypes.[0].IxKind = IxKGroupOuter
                                         && bat.IndexTypes.[1].IxKind = IxKGroupMember
                      | _ -> false) && b.Kind.IsTExprVar ->
            let b' = z b
            match groupStorageRead b' [ z i; z j ] (zt expr.Type) with
            | Some e -> e.Kind
            | None ->
                let arr' = z inner
                TExprIndex (arr', guardSubscripts arr' [ z j ], id)
        // A CELL read of a grouped value, `grouped(i, j)` (G1).
        | TExprIndex (b, [ i; j ], id)
                when (match b.Type with
                      | ArrayElem bat -> bat.IndexTypes.Length = 2 && bat.IndexTypes.[0].IxKind = IxKGroupOuter
                                         && bat.IndexTypes.[1].IxKind = IxKGroupMember
                      | _ -> false) && b.Kind.IsTExprVar ->
            let b' = z b
            match groupStorageRead b' [ z i; z j ] (zt expr.Type) with
            | Some e -> e.Kind
            | None -> TExprIndex (b', guardSubscripts b' [ z i; z j ], id)
        | TExprIndex (arr, idxs, id) ->
            let arr' = z arr
            // Per-dimension coordinates of a product-framed compact group
            // fold to compound positions here, after the judgment has seen
            // every coordinate against its factor; then the ordinary guards.
            TExprIndex (arr', guardSubscripts arr' (foldProductSubscripts arr' (zs idxs)), id)
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
    let zonked = intEnumEdges { expr with Kind = kind; Type = zt expr.Type }
    // An index cast `(e : I)` whose operand was still OPEN when it was typed
    // got the checked conversion (TypeCheckInfer.inferIndexCast); when the
    // operand turned out to be a PROVEN index of I itself (a same-tag
    // ascription on a range-fed kernel parameter), the guard proves nothing
    // and is dropped -- the cast is the operand, retyped.
    match zonked.Kind, zonked.Type with
    | TExprBlock ([ TStmtLet tb; TStmtExpr { Kind = TExprConstraintCheck (_, "BL8006", _) } ], Some _),
      IRTIdxTagged (_, IRefNamed tag)
        when tb.Name.StartsWith indexCastBindingPrefix && isProvenIndex (Some tag) tb.Value ->
        { tb.Value with Type = zonked.Type }
    | _ -> zonked

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
