// ============================================================================
// TypeCheck.fs - Type Checking and Inference (Rewritten)
// ============================================================================
//
// Proper type checking with:
//   - Unification with substitution (inference variables resolve through constraints)
//   - Extent preservation (Idx<180> keeps extent=180, not placeholder 0)
//   - Pattern type checking (match arms produce correct bindings)
//   - Function and type declaration registration in environment
//   - Struct/sum type field resolution
//   - Symmetry analysis for <@> combinator applications
//   - Explicit errors for all unhandled forms (no silent fallthrough)
//
// Public API (consumed by Lowering.fs and Main.fs):
//   - typeCheck : Program -> Result<TypedProgram, CompileError list>
//   - TypeError union: UnboundVariable | TypeMismatch | ArityMismatch
//                      | InvalidArrayCapture | InvalidApplication
//                      | PatternTypeMismatch | Other

module Blade.TypeCheck

open Blade.Ast
open Blade.IR
open Blade.Types
open Blade.TypedAst
open Blade.Unify
open Blade.TypeEnv
open Blade.Zonk

let rec evalConstExpr (env: TypeEnv) (expr: Expr) : int64 option =
    match expr.Kind with
    | ExprKind.ExprLit (LitInt n) -> Some n
    | ExprKind.ExprLit (LitFloat f) -> Some (int64 f)
    | ExprKind.ExprVar name ->
        match lookupVar name env with
        | Some info ->
            match info.Type with
            | IRTNat (Some n) -> Some (int64 n)
            | _ -> None
        | None -> None
    | ExprKind.ExprBinOp (_, OpAdd, l, r) ->
        match evalConstExpr env l, evalConstExpr env r with
        | Some a, Some b -> Some (a + b) | _ -> None
    | ExprKind.ExprBinOp (_, OpSub, l, r) ->
        match evalConstExpr env l, evalConstExpr env r with
        | Some a, Some b -> Some (a - b) | _ -> None
    | ExprKind.ExprBinOp (_, OpMul, l, r) ->
        match evalConstExpr env l, evalConstExpr env r with
        | Some a, Some b -> Some (a * b) | _ -> None
    | ExprKind.ExprBinOp (_, OpDiv, l, r) ->
        match evalConstExpr env l, evalConstExpr env r with
        | Some a, Some b when b <> 0L -> Some (a / b) | _ -> None
    | _ -> None

/// Evaluate an expression to a compile-time int under the FULL static
/// contract (the replicate-count rule): a literal, a Nat-typed var, a
/// `let static` value, or a static-function call. Two tiers: the cheap
/// evalConstExpr first, then StaticEval against the StaticValues/
/// StaticFunctions maps populated by checkModule's pre-pass. Shared by the
/// Dist annotation order (lowerTypeExpr's TyDist arm) and the cumulant
/// projection order (inferCumulantProj).
let staticEnvOf (env: TypeEnv) : StaticEval.StaticEnv =
    { Values = env.StaticValues
      Functions =
        env.StaticFunctions
        |> Map.map (fun _ (fd: FunctionDecl) ->
            { StaticEval.Name = fd.Name
              StaticEval.Params = fd.Params |> List.map (fun p -> p.Name)
              StaticEval.Body = fd.Body })
      CalledFunctions = ref Set.empty
      ProviderRoots = Map.empty
      Structs = Map.empty }

let evalStaticIntExpr (env: TypeEnv) (expr: Expr) : int option =
    match evalConstExpr env expr with
    | Some n -> Some (int n)
    | None ->
        match StaticEval.evalExpr (staticEnvOf env) StaticEval.maxSteps expr with
        | Ok (StaticEval.SVInt v) -> Some (int v)
        | _ -> None

/// Evaluate an expression to its raw StaticValue under the same full static
/// contract as evalStaticIntExpr. For type arguments whose payload is
/// structured rather than an int (the IrrepsIdx spec: an array of triples,
/// which StaticEval folds to nested SVTuples).
let evalStaticValueExpr (env: TypeEnv) (expr: Expr) : Result<StaticEval.StaticValue, string> =
    StaticEval.evalExpr (staticEnvOf env) StaticEval.maxSteps expr

/// Dist provenance of a surface expression: the union of the provenance
/// sets of every variable reachable in it (conservative — an
/// over-approximated source set can only make independence HARDER to
/// prove, never easier, so union is sound). Empty means "unknown", which
/// consumers treat as un-provable rather than vacuously independent.
/// Sources of ground truth: module-level dist bindings (seeded from the
/// PPL elaboration state at checkDecl) and Dist-typed function parameters
/// (seeded with their license token at checkFunctionDecl).
let rec provenanceOfSurface (env: TypeEnv) (e: Expr) : Set<string> =
    let prov = provenanceOfSurface env
    let unionMany es = es |> List.map prov |> List.fold Set.union Set.empty
    match e.Kind with
    | ExprKind.ExprVar n ->
        (match lookupVar n env with
         | Some vi ->
             match env.Provenance.TryGetValue vi.VarId with
             | true, s -> s
             | _ -> Set.empty
         | None -> Set.empty)
    | ExprKind.ExprBinOp (_, _, l, r) -> Set.union (prov l) (prov r)
    | ExprKind.ExprUnaryOp (_, x) -> prov x
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "__ppl_cumulant" }, _) -> Set.empty   // a projected component is an ARRAY, not a dist
    | ExprKind.ExprApp (_, args) -> unionMany args            // call result: union of Dist-relevant args (conservative)
    | ExprKind.ExprTyped (x, _) -> prov x
    | ExprKind.ExprTuple es -> unionMany es
    | ExprKind.ExprIf (_, t, f) -> Set.union (prov t) (prov f)
    | ExprKind.ExprLet (b, body) -> Set.union (prov b.Value) (prov body)
    | ExprKind.ExprBlock (stmts, fin) ->
        let stmtProv s =
            let rec go s =
                match s with
                | StmtSpanned (inner, _) -> go inner
                | StmtLet b -> prov b.Value
                | StmtAssign (_, _, r) -> prov r
                | StmtExpr x -> prov x
                | StmtForIn (_, _, _) -> Set.empty
            go s
        Set.union (stmts |> List.map stmtProv |> List.fold Set.union Set.empty)
                  (fin |> Option.map prov |> Option.defaultValue Set.empty)
    | _ -> Set.empty

/// Lower an extent expression to IRExpr, preserving as much info as possible.
let lowerExtentExpr (env: TypeEnv) (expr: Expr) : IRExpr =
    match evalConstExpr env expr with
    | Some n -> IRLit (IRLitInt n)
    | None ->
        match expr.Kind with
        | ExprKind.ExprVar name -> IRParam (name, 0, IRTNat None)
        | ExprKind.ExprLit (LitInt n) -> IRLit (IRLitInt n)
        | _ -> IRParam ("?", 0, IRTNat None)

/// Lower an extent expression with one bound parameter substituted for an IR
/// expression. Used by DepIdx to substitute the lambda parameter (the outer
/// index variable) into the inner extent expression. Walks the AST recursively
/// so binary-op extents like `n - i` lower correctly.
///
/// This is more general than `lowerExtentExpr`, which falls through to a `?`
/// placeholder for anything beyond ExprLit and ExprVar. For DepIdx the inner
/// extent expression is consumed directly at the iteration-bound emission
/// site, so the expression structure must survive.
let rec substituteAndLowerExtent (env: TypeEnv) (paramName: Ident) (subst: IRExpr) (expr: Expr) : IRExpr =
    match expr.Kind with
    | ExprKind.ExprVar n when n = paramName -> subst
    | _ ->
        match evalConstExpr env expr with
        | Some k -> IRLit (IRLitInt k)
        | None ->
            match expr.Kind with
            | ExprKind.ExprVar name -> IRParam (name, 0, IRTNat None)
            | ExprKind.ExprLit (LitInt n) -> IRLit (IRLitInt n)
            | ExprKind.ExprBinOp (_mode, op, l, r) ->
                let l' = substituteAndLowerExtent env paramName subst l
                let r' = substituteAndLowerExtent env paramName subst r
                let irOpOpt =
                    match op with
                    | OpAdd -> Some IRAdd
                    | OpSub -> Some IRSub
                    | OpMul -> Some IRMul
                    | OpDiv -> Some IRDiv
                    | OpMod -> Some IRMod
                    | _ -> None
                match irOpOpt with
                | Some irOp -> IRBinOp (IRElementwise, irOp, l', r')
                | None -> IRParam ("?", 0, IRTNat None)
            | ExprKind.ExprUnaryOp (OpNeg, e) ->
                IRUnaryOp (IRNeg, substituteAndLowerExtent env paramName subst e)
            | _ ->
                IRParam ("?", 0, IRTNat None)

// ============================================================================
// 4. AST TypeExpr -> IRType (with extent preservation)
// ============================================================================

let rec lowerTypeExpr (env: TypeEnv) (ty: TypeExpr) : IRType =
    match ty with
    | TyInt32 -> IRTScalar ETInt32
    | TyInt64 -> IRTScalar ETInt64
    | TyFloat32 -> IRTScalar ETFloat32
    | TyFloat64 -> IRTScalar ETFloat64
    | TyComplex64 -> IRTScalar ETComplex64
    | TyComplex128 -> IRTScalar ETComplex128
    | TyBool -> IRTScalar ETBool
    | TyUnit -> IRTUnit
    | TyString -> IRTScalar ETString
    | TyChar -> IRTScalar ETInt32

    | TyNamed (name, args) ->
        // Helper: try to resolve a type arg as a unit annotation
        let tryResolveUnitArg baseType args =
            match args with
            | [TyNamed (unitName, [])] ->
                match Map.tryFind unitName env.Units with
                | Some unitSig -> IRTUnitAnnotated (baseType, unitSig)
                | None -> baseType  // not a unit, ignore
            | _ -> baseType
        match name with
        | "Int" | "Int32" -> tryResolveUnitArg (IRTScalar ETInt32) args
        | "Int64" -> tryResolveUnitArg (IRTScalar ETInt64) args
        | "Float" | "Float64" | "Double" -> tryResolveUnitArg (IRTScalar ETFloat64) args
        | "Float32" -> tryResolveUnitArg (IRTScalar ETFloat32) args
        | "Complex64" -> tryResolveUnitArg (IRTScalar ETComplex64) args
        | "Complex128" -> tryResolveUnitArg (IRTScalar ETComplex128) args
        | "Bool" -> IRTScalar ETBool
        | "Void" -> IRTUnit
        // Nat resolves a unit arg like the other numeric bases so
        // `Nat<angular_momentum>` carries its tag instead of silently
        // dropping it (non-unit args keep returning bare Nat, as before).
        | "Nat" -> tryResolveUnitArg (IRTNat None) args
        | "String" -> IRTScalar ETString
        | "Char" -> IRTScalar ETInt32
        | "Poly" ->
            // Each Poly occurrence gets its own arity variable name. Packs are
            // independent at the call site (different slots can have different
            // arities), so the type rep shouldn't claim they share one variable.
            // The name is generated fresh per occurrence; it's used by ppIRType
            // for diagnostics but doesn't drive any per-slot specialization logic
            // (that lives in the IR phase, keyed by parameter position).
            let arityName = sprintf "r%d" (env.Builder.FreshId())
            match args with
            | [inner] -> IRTPoly (lowerTypeExpr env inner, arityName)
            | _ -> IRTPoly (IRTScalar ETFloat64, arityName)
        | _ ->
            match lookupTypeDef name env with
            | Some (TDIAlias resolvedTy) -> resolvedTy
            | Some (TDIStruct (n, _, _, _)) -> IRTNamed n
            | Some (TDIVariant (n, _, _)) -> IRTNamed n
            | Some (TDIIndexType _) ->
                // Aliased index type in value position (function param,
                // struct field, let-binding annotation, etc.). Under Option C
                // this lowers to IRTIdxTagged (int64, IRefNamed name) — an
                // int64 tagged by the index type's nominal name. The codegen
                // emits `using <name> = int64_t;` so the C++ type carries
                // the alias for documentation; runtime backing is still int.
                IRTIdxTagged (IRTScalar ETInt64, IRefNamed name)
            | Some (TDIEnumIdx (_, _, values, _)) ->
                // Same shape, but the underlying type follows the values
                // list (all-string → ETString, else ETInt64). The C++
                // typedef emitted alongside resolves the alias to the
                // matching primitive.
                let underlying = EnumValue.underlyingElemType values
                IRTIdxTagged (IRTScalar underlying, IRefNamed name)
            | None ->
                // If this name is in the type variable scope (introduced by T^k
                // elsewhere in this declaration), bare T means T^0 (scalar).
                if args.IsEmpty && env.Subst.IsTypeVar(name) then
                    env.Subst.LookupOrCreateTypeVar(name, 0, env.Builder)
                else
                    IRTNamed name  // Forward reference or external type

    | TyArray (elemTy, indexTys) ->
        let elem = lowerElemType env elemTy
        // RaggedIdx requires at least one prior index in the index list to
        // iterate over. A 1-D Array<T like RaggedIdx<lens>> is malformed:
        // there's no prior position to provide the iteration that drives the
        // lengths-array lookup. The check is structural — first index can't
        // be a closed RaggedIdx.
        //
        // The opaque variant `RaggedIdx<_>` is exempted: it's specifically
        // designed for kernel-parameter types (`g: Array<T like RaggedIdx<_>>`)
        // representing a sub-array peeled from a parent ragged. There is no
        // lengths array to look up; the extent is supplied by the loop
        // binding's ExtentArrayRef at the peel point.
        let firstIsRagged =
            match indexTys with
            | TyRaggedIdx _ :: _ -> true
            | _ -> false
        if firstIsRagged then
            // Produce a degenerate IRTArray; the actual error reporting site
            // is the typechecker proper, not lowering. The placeholder lets
            // downstream lowering proceed to surface a clearer diagnostic
            // when the type appears in a function signature or let binding.
            // Emit a Tag that downstream phases can detect for error reporting.
            let placeholderIdx = {
                Id = env.Builder.FreshId(); Rank = 1
                Extent = IRParam ("__error_ragged_no_prior", 0, IRTNat None)
                Symmetry = SymNone
                Tag = Some "__error_ragged_no_prior"; IxKind = IxKErrorRaggedNoPrior
                Kind = SDimension; Dependencies = []
            }
            mkArrayArrow [placeholderIdx] elem None
        else
            // Index types are normally one IRIndexType per surface index, but
            // dependent forms like DepIdx produce TWO records (outer + inner with
            // Dependencies linking them). lowerIndexTypeList handles the expansion.
            let indices = indexTys |> List.mapi (fun i ity -> lowerIndexTypeList env i ity) |> List.concat
            // Phase D / nested-array normalization: when the element type
            // itself lowers to an IRTArray (i.e., the user wrote
            // `Array<Array<T like Idx<n>> like Idx<m>>` syntax), flatten
            // into the equivalent multi-Idx form
            // `Array<T like Idx<m>, Idx<n>>`. The two surface forms have
            // identical runtime behavior: indexing once peels one IndexType,
            // storage layout is `T**` either way, and all downstream
            // rank-N machinery (genArrayLiteral, print loops, the recursive
            // walker, etc.) keys off IndexTypes count rather than nesting
            // depth.
            //
            // Without this normalization, the inner-as-IRTArray form
            // produces a type with `arrayRank = 1` (counting only the outer
            // IndexTypes) but a literal whose `computeArrayDims` recurses
            // to depth 2. The mismatch malforms `extents[1] = {n, m}` (one
            // slot, two values) and breaks allocation since `allocate<>`
            // only sees the outer extent.
            //
            // Limited to the explicit `TyArray (TyArray, _)` syntactic
            // case at the user-facing surface; other producers of IRTArray
            // (e.g., function-return inference) don't compose nested
            // arrays at the type level. Inner record's Identity / IsVirtual
            // fields are reset on the flattened wrapper since the original
            // inner-array identity has been absorbed.
            match elem with
            | ArrayElem inner ->
                mkArrayArrow (indices @ inner.IndexTypes) inner.ElemType None
            | _ ->
                mkArrayArrow indices elem None

    | TyDist (orderExpr, elemTy, axesTys) ->
        // Typed dist tower: Dist<order, Elem like I1, ..., Ik>.
        // The order must be a compile-time integer >= 1 — a literal, a
        // `let static`, or a static-function call (the replicate-count
        // contract; same two-tier resolution as inferReplicate: cheap
        // evalConstExpr first, then the full StaticEval against the
        // checkModule pre-pass's StaticValues/StaticFunctions). Failure
        // lowers to the -1 SENTINEL, reported at the annotation-consumption
        // sites (inferLetBindingValue / checkFunctionDecl) alongside the
        // ragged no-prior check — lowerTypeExpr itself has no error channel.
        let order = evalStaticIntExpr env orderExpr
        let elem = lowerElemType env elemTy
        let axes = axesTys |> List.mapi (fun i ity -> lowerIndexTypeList env i ity) |> List.concat
        match order with
        | Some n when n >= 1 -> IRTDist (n, elem, axes)
        | _ -> IRTDist (-1, elem, axes)

    | TyAbstractArray (elemTy, rankExpr, _symmOpt) ->
        match evalConstExpr env rankExpr with
        | Some rank ->
            let r = int rank
            // Check if element type is a type variable
            let elemVarName =
                match elemTy with
                | TyVar (n, _) -> Some n
                | _ -> None
            match elemVarName with
            | Some name ->
                // Type variable with arity: route through type var scope
                env.Subst.LookupOrCreateTypeVar(name, r, env.Builder)
            | None ->
                if r = 0 then
                    lowerTypeExpr env elemTy  // Rank-0: just the scalar
                else
                    let elem = lowerElemType env elemTy
                    let indices = [0 .. r - 1] |> List.map (fun _ ->
                        { Id = env.Builder.FreshId(); Rank = 1; Extent = IRParam ("n", 0, IRTNat None)
                          Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] })
                    mkArrayArrow indices elem None
        | None ->
            // Non-constant rank (e.g., T^r where r is a variable)
            match elemTy with
            | TyVar (name, _) ->
                // Can't resolve arity statically — create unconstrained type var
                env.Subst.LookupOrCreateTypeVar(name)
            | _ ->
                // Phase B2: lowerElemType returns IRType; return directly.
                lowerElemType env elemTy  // Arity-polymorphic fallback

    | TyFunc (args, ret) ->
        mkFuncArrow (args |> List.map (lowerTypeExpr env)) (lowerTypeExpr env ret)

    | TyTuple tys -> IRTTuple (tys |> List.map (lowerTypeExpr env))

    | TyVar (name, arityOpt) ->
        // Type variable with optional arity annotation.
        // T or T^0 = scalar type variable. T^k (k>0) = rank-k array type variable.
        let arity = arityOpt |> Option.defaultValue 0
        env.Subst.LookupOrCreateTypeVar(name, arity, env.Builder)

    // Index types as standalone type expressions denote VALUE TYPES (the type
    // of an index value), NOT array types. An anonymous Idx<n> in a value
    // position (function parameter, struct field, let-binding annotation,
    // tuple element) represents an integer in [0, n) that compiles to a loop
    // bound at codegen time; it is not a Float-array indexed by Idx<n>.
    //
    // Lowered to IRTIdxTagged (IRTScalar ETInt64, IRefAnon (...)) — an int64
    // wrapped with a nominal anonymous tag (parallel to how Float<meters>
    // becomes IRTUnitAnnotated). The tag preserves "this is an anonymous
    // index" identity through the IR so inferArithType rejects arithmetic
    // on these values (matching the rejection rule for named index types).
    // At codegen, the IRTIdxTagged wrapper erases to int64_t (the runtime
    // backing). Named aliases (`type RegionIdx = Idx<n>`; `i: RegionIdx`)
    // route through the TyNamed branch above and lower to IRTIdxTagged
    // with IRefNamed — same shape, named tag.
    //
    // KNOWN GAP — higher-arity / dependent cases below (TySymIdx, TyAntisymIdx,
    // TyHermitianIdx, TyCompoundIdx, TyDepIdx | TyRaggedIdx | TyRaggedIdxOpaque,
    // TyEquivIdx) preserve the legacy IRTArray-with-Float64 shape pending a
    // formalism design pass. A SymIdx<r, n> as a value-level type could be
    // an ordered r-tuple of integers under a sort constraint, a type error,
    // or something else; the formalism doesn't currently say. No test
    // exercises these paths; the wrong shape is a dead-code latent bug.
    | TyIdx extent ->
        // Value-position TyIdx lowers to IRTIdxTagged wrapping int64.
        // Per Option C: index values are int64 tagged with a nominal
        // IdxRef. The fresh nominalId is the identity; the extent is
        // preserved on the IdxRef solely for diagnostics / pretty-print.
        let nominalId = env.Builder.FreshId()
        IRTIdxTagged (IRTScalar ETInt64,
                      IRefAnon (nominalId, lowerExtentExpr env extent))

    | TySymIdx (rank, extent) ->
        let ext = lowerExtentExpr env extent
        let idx = { Id = env.Builder.FreshId(); Rank = rank; Extent = ext
                    Symmetry = SymSymmetric; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
        mkArrayArrow [idx] (IRTScalar ETFloat64) None

    | TyAntisymIdx (rank, extent) ->
        let ext = lowerExtentExpr env extent
        let idx = { Id = env.Builder.FreshId(); Rank = rank; Extent = ext
                    Symmetry = SymAntisymmetric; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
        mkArrayArrow [idx] (IRTScalar ETFloat64) None

    | TyHermitianIdx extent ->
        let ext = lowerExtentExpr env extent
        let idx = { Id = env.Builder.FreshId(); Rank = 2; Extent = ext
                    Symmetry = SymHermitian; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
        mkArrayArrow [idx] (IRTScalar ETFloat64) None

    | TyBoundedIdx _ -> IRTScalar ETInt64

    | TyCompoundIdx _mask ->
        let idx = { Id = env.Builder.FreshId(); Rank = 1; Extent = IRParam ("compound", 0, IRTNat None)
                    Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
        mkArrayArrow [idx] (IRTScalar ETFloat64) None

    | TyEnumIdx valuesExpr ->
        // Determine underlying element type from values. All-string lowers to
        // ETString; otherwise (all-int, empty, or mixed-but-recoverable) ETInt64.
        // Mixed lists won't reach here in practice — the same extraction is
        // run by registerTypeDecl and surfaces a clean error when aliased; an
        // unaliased mixed-list slips through silently with ETInt64.
        let isAllString =
            match valuesExpr.Kind with
            | ExprKind.ExprArrayLit elems when not elems.IsEmpty ->
                elems |> List.forall (fun el -> match el.Kind with ExprKind.ExprLit (LitString _) -> true | _ -> false)
            | _ -> false
        if isAllString then IRTScalar ETString else IRTScalar ETInt64

    | TyDepIdx _ | TyRaggedIdx _ | TyRaggedIdxOpaque | TyIrrepsIdx _ ->
        // DepIdx/RaggedIdx/IrrepsIdx in non-index position. Defensive fallback
        // matching the shape used for TyCompoundIdx, TyEquivIdx, etc. — wrap
        // in a single-index Array so the IR shape is consistent. Real
        // iteration happens via lowerIndexType, which produces the correctly
        // tagged IRIndexType (Dependencies for the dependent forms, the
        // irreps identity tag for IrrepsIdx).
        let idx = lowerIndexType env 0 ty
        mkArrayArrow [idx] (IRTScalar ETFloat64) None

    | TyHalo _ ->
        // halo<Inner, [offs]> is legal only as a range<> slot (handled in the
        // ExprRange arm via haloSlotOf). In any other type position there is
        // no value meaning; degrade to the iteration value (int64) — the slot
        // path never routes here.
        IRTScalar ETInt64

    | TyPoly inner ->
        // Fresh arity-variable name per Poly occurrence. See the TyNamed "Poly"
        // case above for rationale: packs are independent, so each Poly param
        // gets its own identifier in the type rep.
        let arityName = sprintf "r%d" (env.Builder.FreshId())
        IRTPoly (lowerTypeExpr env inner, arityName)
    | TyConstrained (inner, _) -> lowerTypeExpr env inner
    | TyEquivIdx (_dim, _group, _rep) ->
        let idx = { Id = env.Builder.FreshId(); Rank = 1; Extent = IRParam ("equiv", 0, IRTNat None)
                    Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
        mkArrayArrow [idx] (IRTScalar ETFloat64) None

and lowerElemType env ty : IRType =
    // Phase B2: returns IRType directly. Primitives become IRTScalar et;
    // named index types in element position (foreign-key syntax) become
    // IRTIdxTagged + IRefNamed; user-defined types pass through as
    // IRTNamed; nested arrays pass through as IRTArray; etc.
    //
    // S4 fix: previously this function silently demoted struct-element
    // and nested-array types to ETFloat64. Now they propagate as their
    // actual IRType, unblocking the type system. (Codegen support for
    // non-primitive element types is future Phase D work.)
    //
    // Option C migration: previously the named-index cases produced
    // IRTScalar (ETIndexRef name) — now they produce IRTIdxTagged
    // wrapping the underlying primitive. This unifies element-position
    // and value-position encodings; ETIndexRef has been retired.
    match ty with
    | TyNamed (name, []) ->
        match lookupTypeDef name env with
        | Some (TDIIndexType _) ->
            IRTIdxTagged (IRTScalar ETInt64, IRefNamed name)
        | Some (TDIEnumIdx (_, _, values, _)) ->
            let underlying = EnumValue.underlyingElemType values
            IRTIdxTagged (IRTScalar underlying, IRefNamed name)
        | _ -> lowerTypeExpr env ty   // struct, sum, alias, type variable, etc.
    | TyIdx _ | TySymIdx _ | TyAntisymIdx _ | TyHermitianIdx _ ->
        // Raw index type syntax in element position (e.g., Array<Idx<3> like ...>)
        // Note: anonymous-tag preservation here is a separate (deferred)
        // refinement — currently collapses to bare int64 like before, losing
        // the index identity. The Option C migration scope was named tags.
        IRTScalar ETInt64
    | TyEnumIdx valuesExpr ->
        // Mirror of the value-position handling: all-string values → ETString,
        // otherwise → ETInt64. Same anonymous-tag-loss caveat as above.
        let isAllString =
            match valuesExpr.Kind with
            | ExprKind.ExprArrayLit elems when not elems.IsEmpty ->
                elems |> List.forall (fun el -> match el.Kind with ExprKind.ExprLit (LitString _) -> true | _ -> false)
            | _ -> false
        if isAllString then IRTScalar ETString else IRTScalar ETInt64
    | _ ->
        lowerTypeExpr env ty

and lowerIndexType env (_position: int) (ty: TypeExpr) : IRIndexType =
    let id = env.Builder.FreshId()
    match ty with
    | TyIdx extent ->
        { Id = id; Rank = 1; Extent = lowerExtentExpr env extent
          Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
    | TySymIdx (rank, extent) ->
        { Id = id; Rank = rank; Extent = lowerExtentExpr env extent
          Symmetry = SymSymmetric; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
    | TyAntisymIdx (rank, extent) ->
        { Id = id; Rank = rank; Extent = lowerExtentExpr env extent
          Symmetry = SymAntisymmetric; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
    | TyHermitianIdx extent ->
        { Id = id; Rank = 2; Extent = lowerExtentExpr env extent
          Symmetry = SymHermitian; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
    | TyEnumIdx valuesExpr ->
        let nValues =
            match valuesExpr.Kind with
            | ExprKind.ExprArrayLit elems -> int64 elems.Length
            | _ -> 0L
        { Id = id; Rank = 1; Extent = IRLit (IRLitInt nValues); Symmetry = SymNone
          Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
    | TyDepIdx (outerTy, _paramName, _bodyTy) ->
        // Single-slot context (e.g., type alias, range): return a placeholder
        // inner-only record. Two-record expansion happens at the array-index-
        // list construction site (lowerIndexTypeList). Single-slot use of
        // DepIdx is suspect — code paths that need the full DepIdx structure
        // (iteration, etc.) should route through lowerIndexTypeList instead.
        let outerIdx = lowerIndexType env _position outerTy
        { Id = id; Rank = 2; Extent = IRParam ("__depidx_inner", 0, IRTNat None)
          Symmetry = SymNone; Tag = Some "__depidx"; IxKind = IxKDep
          Kind = SDimension; Dependencies = [outerIdx.Id] }
    | TyRaggedIdx lengthsExpr ->
        // Single-record shape matching lowerIndexTypeList's unaliased TyRaggedIdx
        // case (line ~1004). Earlier this branch returned an arity-2 placeholder
        // copied from the TyDepIdx shape, which produced wrong-rank arrays when
        // used through a type alias (`type R = RaggedIdx<lens>` then `Array<...
        // R>`). The structural tag `__raggedidx` is preserved so codegen
        // predicates (isRaggedArrayType etc.) keep detecting the ragged form
        // through alias indirection.
        let lengthsIR = lowerExtentExpr env lengthsExpr
        { Id = id; Rank = 1; Extent = IRRaggedLookup lengthsIR
          Symmetry = SymNone; Tag = Some "__raggedidx"; IxKind = IxKRagged
          Kind = SDimension; Dependencies = [] }
    | TyRaggedIdxOpaque ->
        // Opaque-extent variant: rank-1, no lengths array, no outer position.
        // Used in kernel-parameter types where the extent is supplied by the
        // peel context. The Extent is a sentinel (IROpaqueExtent) rather than
        // a placeholder IRParam, so codegen can distinguish "extent unknown
        // because we haven't computed it yet" (IRParam) from "extent supplied
        // by surrounding loop binding" (IROpaqueExtent).
        { Id = id; Rank = 1; Extent = IROpaqueExtent
          Symmetry = SymNone; Tag = Some "__raggedidx_opaque"; IxKind = IxKRaggedOpaque
          Kind = SDimension; Dependencies = [] }
    | TyIrrepsIdx specExpr ->
        // IrrepsIdx<spec>: block-structured dense index over an irreps spec.
        // The spec resolves under the full static contract (like Dist's
        // order); extent = total_dim(spec) and EVERY cell is stored — flat
        // dense, no compression — so the record rides the ordinary dense
        // paths (SymNone). The block structure matters for IDENTITY, carried
        // in the Tag (mkIrrepsTag: spec equality = index-space identity;
        // Unify adds the spec-mismatch strictness arm). lowerIndexType has
        // no error channel, so a non-static/malformed spec lowers to the
        // marker record consumed by irTypeHasBadIrrepsSpec at let-binding /
        // function-signature sites (ragged-no-prior pattern), the failure
        // detail smuggled in the IRParam name.
        (match evalStaticValueExpr env specExpr
               |> Result.bind (Blade.ML.Statics.specOfStatic "IrrepsIdx") with
         | Ok spec ->
             let triples = spec |> List.map (fun e -> (e.L, e.Parity, e.Mult))
             { Id = id; Rank = 1
               Extent = IRLit (IRLitInt (int64 (Blade.ML.Spec.totalDim spec)))
               Symmetry = SymNone; Tag = Some (mkIrrepsTag None triples)
               IxKind = IxKIrreps; Kind = SDimension; Dependencies = [] }
         | Error detail ->
             // specOfStatic prefixes its own "IrrepsIdx: " (its `what`
             // label); the consumption-site diagnostic adds the same prefix,
             // so strip it here to avoid "IrrepsIdx: IrrepsIdx: ...".
             let detail =
                 if detail.StartsWith "IrrepsIdx: " then detail.Substring "IrrepsIdx: ".Length
                 else detail
             { Id = id; Rank = 1
               Extent = IRParam (detail, 0, IRTNat None)
               Symmetry = SymNone; Tag = Some "__error_irreps_bad_spec"
               IxKind = IxKErrorIrrepsBadSpec; Kind = SDimension; Dependencies = [] })
    | TyNamed (name, _) ->
        match lookupTypeDef name env with
        | Some (TDIIndexType (_, idx, _)) -> { idx with Id = id }
        | Some (TDIEnumIdx (_, idx, _, _)) -> { idx with Id = id }
        | _ ->
            { Id = id; Rank = 1; Extent = IRParam (name, 0, IRTNat None); Symmetry = SymNone
              Tag = Some name; IxKind = ixKindOfTag (Some name); Kind = SDimension; Dependencies = [] }
    | TyCompoundIdx maskExpr ->
        // CompoundIdx<mask> -- masked product space (formalism 4.5). Rank = the
        // RANK of the mask array (its number of dimensions). The mask is a runtime
        // array value carried in IRCompoundMask for codegen; cardinality (popcount)
        // is computed at runtime by the emitted compound_index_t. Canonical surface
        // form is a named mask whose declared type yields the rank; other forms
        // fall back to a rank-1 degraded placeholder for now (no producer relies on
        // them yet). Nested matches are parenthesized to avoid outer-arm absorption.
        let maskIR, rank =
            match maskExpr.Kind with
            | ExprKind.ExprVar name ->
                (match lookupVar name env with
                 | Some vi ->
                     let rank =
                         (match vi.Type with
                          | ArrayElem arr ->
                              // Enforce: a CompoundIdx mask must be Array<bool like ...>.
                              // Construction (popcount + flatten to std::vector<bool>) is
                              // cheap only for a boolean mask, so a non-bool (or non-array)
                              // mask is a hard type error here rather than a silent
                              // downstream miscompile. (A span-attributed diagnostic would
                              // be nicer, but lowerIndexType has no error channel today.)
                              (match arr.ElemType with
                               | IRTScalar ETBool -> ()
                               | other ->
                                   failwithf "CompoundIdx<%s>: mask must have bool element type (Array<bool like ...>); '%s' has element type %A" name name other)
                              arr.IndexTypes |> List.sumBy (fun ix -> ix.Rank)
                          | other ->
                              failwithf "CompoundIdx<%s>: mask must be an array (Array<bool like ...>); '%s' has type %A" name name other)
                     IRVar (vi.VarId, vi.Type), rank
                 | None -> lowerExtentExpr env maskExpr, 1)
            | _ -> lowerExtentExpr env maskExpr, 1
        { Id = id; Rank = rank; Extent = IRCompoundMask maskIR
          Symmetry = SymNone; Tag = Some "__compoundidx"; IxKind = IxKCompound
          Kind = SDimension; Dependencies = [] }
    | _ ->
        { Id = id; Rank = 1; Extent = IRParam ("?", 0, IRTNat None); Symmetry = SymNone
          Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }

/// Lower an index type to a list of IRIndexType records. Most types produce a
/// single-element list; dependent forms (DepIdx, RaggedIdx) produce two records
/// — outer + inner with Dependencies linking them.
///
/// Used at array-index-list construction sites where a multi-record expansion
/// is meaningful. Single-slot contexts (range, type alias) use lowerIndexType
/// directly and get a placeholder for dependent forms.
and lowerIndexTypeList (env: TypeEnv) (position: int) (ty: TypeExpr) : IRIndexType list =
    match ty with
    | TyDepIdx (outerTy, paramName, bodyTy) ->
        // Lower outer first to get its Id; that Id is the dependency target
        // for the inner extent's reference to the lambda parameter.
        let outerIdx = lowerIndexType env position outerTy
        let outerWithTag = { outerIdx with Tag = Some "__depidx_outer"; IxKind = IxKDepOuter }
        let outerVarRef = IRVar (outerWithTag.Id, IRTScalar ETInt64)
        // Extract the inner extent expression. Two body shapes are recognized:
        //   - Lambda form: `lambda(i) -> Idx<expr>` parses to `TyIdx expr`.
        //     Substitute paramName with outerVarRef in expr.
        //   - Eta-reduced form: `DepIdx<O, f>` parses to a synthesized
        //     TyNamed(funcName, [TyNamed(paramName, [])]) body. Inline by
        //     looking up f in StaticFunctions, taking its body Expr, and
        //     substituting f's param name (not paramName) with outerVarRef.
        // Anything else falls back to a runtime placeholder.
        let innerExtent =
            match bodyTy with
            | TyIdx e ->
                substituteAndLowerExtent env paramName outerVarRef e
            | TyNamed (funcName, [TyNamed (innerParam, [])]) when innerParam = paramName ->
                match Map.tryFind funcName env.StaticFunctions with
                | Some funcDecl when funcDecl.Params.Length = 1 ->
                    // Substitute the function's formal param with the outer
                    // iteration var. The function's body becomes the inner
                    // extent expression — its structure is fixed at compile
                    // time, but it evaluates per-iteration as outer walks.
                    //
                    // Peel a trivial block wrapper so `function f(x) = { e }`
                    // (parses to ExprBlock([], Some e)) reduces the same as
                    // the inline form `function f(x) = e`.
                    let funcParamName = funcDecl.Params.[0].Name
                    let bodyExpr =
                        match funcDecl.Body.Kind with
                        | ExprKind.ExprBlock ([], Some e) -> e
                        | _ -> funcDecl.Body
                    substituteAndLowerExtent env funcParamName outerVarRef bodyExpr
                | _ ->
                    IRParam ("__depidx_inner", 0, IRTNat None)
            | _ ->
                IRParam ("__depidx_inner", 0, IRTNat None)
        let innerIdx = {
            Id = env.Builder.FreshId()
            Rank = 1
            Extent = innerExtent
            Symmetry = SymNone
            Tag = Some "__depidx_inner"; IxKind = IxKDepInner
            Kind = SDimension
            Dependencies = [outerWithTag.Id]
        }
        [outerWithTag; innerIdx]
    | TyRaggedIdx lengthsExpr ->
        // RaggedIdx contributes a SINGLE record. Its inner extent is a
        // per-iteration lookup into the lengths array, indexed by the
        // current outer iteration's flat position. The lengths array's
        // shape conceptually mirrors the prior index dimensions of the
        // enclosing array (e.g., for Idx<M>, Idx<N>, RaggedIdx<lens>,
        // lens is internally M*N elements); the codegen handles the
        // flat-position computation so the user-facing type stays clean.
        //
        // RaggedIdx is "open" — it does NOT declare its own outer position;
        // it references the iteration over the prior index types in the
        // enclosing Array's index list. A 1-D `Array<T like RaggedIdx<lens>>`
        // is malformed (no prior index to iterate); RaggedIdx requires at
        // least one prior index. The malformedness check happens at the
        // TyArray level (see lowerTypeExpr), not here, since this function
        // doesn't see the surrounding context.
        let lengthsIR = lowerExtentExpr env lengthsExpr
        [{
            Id = env.Builder.FreshId()
            Rank = 1
            Extent = IRRaggedLookup lengthsIR
            Symmetry = SymNone
            Tag = Some "__raggedidx"; IxKind = IxKRagged
            Kind = SDimension
            Dependencies = []  // populated by the codegen iteration as needed
        }]
    | TyRaggedIdxOpaque ->
        // Opaque-extent variant — used in kernel-parameter types where the
        // extent is supplied by the surrounding peel context, not declared
        // up front. Single-record like the closed form, but the Extent is
        // IROpaqueExtent (a marker, no payload) and the Tag distinguishes it
        // from the closed form for downstream codegen routing.
        [{
            Id = env.Builder.FreshId()
            Rank = 1
            Extent = IROpaqueExtent
            Symmetry = SymNone
            Tag = Some "__raggedidx_opaque"; IxKind = IxKRaggedOpaque
            Kind = SDimension
            Dependencies = []
        }]
    | TyNamed (n, _) ->
        // For DepIdx aliases, recurse on the stored body so the multi-record
        // expansion runs at the use site. The catch-all path below would
        // retrieve the single-record placeholder that lowerIndexType stored
        // at registration time, which is structurally wrong for DepIdx
        // (genuinely two records: outer + inner with Dependencies linking).
        //
        // Other aliases (TyIdx, TySymIdx, TyRaggedIdx, etc.) are
        // structurally one record, so the stored idx is correct and the
        // catch-all retrieval works fine. We deliberately don't recurse on
        // those because static aliases have their alias name baked into
        // the stored idx's Tag (used as nominative identity for foreign
        // keys via ETIndexRef); re-walking would lose that.
        //
        // Chained aliases like `type B = A` where `type A = DepIdx<...>`
        // are not handled here — B's body in env is TyNamed("A"), and
        // registerTypeDecl currently routes that to TDIAlias, not
        // TDIIndexType. No test pressure yet.
        match lookupTypeDef n env with
        | Some (TDIIndexType (_, _, (TyDepIdx _ as body))) ->
            lowerIndexTypeList env position body
        | _ ->
            [lowerIndexType env position ty]
    | _ -> [lowerIndexType env position ty]

/// Decide the element type for a virtual array iterating over a given
/// index. The element values produced during iteration ARE positions
/// in the indexed space, so they should carry that space's tag when
/// it has a user-named identity. For anonymous and synthetic-tagged
/// indices, fall back to plain int64.
///
/// This is the hook for iteration-tagging (§4.18 indirect): a kernel
/// like `range<LatIdx> <@> lambda(i) -> A(i)` (where A's index is
/// LatIdx) typechecks under step 5's tag rule because i inherits
/// `Nat<LatIdx>` from this element type — no manual annotation needed.
let elemTypeForIterationIndex (idx: IRIndexType) : IRType =
    match idx.Tag with
    | Some name when name.StartsWith("__halowin|") ->
        // Halo window param: the kernel receives this as `w`, and w(o) neighbor
        // reads dispatch on the "__halowin|" tag (offsets + inner name encoded
        // in it). Carried as a tagged int index so it erases to int64 in C++.
        IRTIdxTagged (IRTScalar ETInt64, IRefNamed name)
    | Some name when not (name.StartsWith("__")) ->
        IRTIdxTagged (IRTScalar ETInt64, IRefNamed name)
    | _ ->
        IRTScalar ETInt64

// ============================================================================
// Co-iteration shape agreement (multi-axis co-iteration arc)
// ============================================================================

/// A plain dense rank-1 index record — the kind that can share a co-iteration
/// product axis. Mirrors the isPlainDense predicate used by the fallback
/// operator's operand checks.
let isPlainDenseIx (ix: IRIndexType) : bool =
    ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank <= 1

/// Structural agreement of two index records for co-iteration purposes.
/// Compares shape-bearing fields only — the nominal Id is EXCLUDED because
/// every occurrence of an index type gets a fresh Id; two Array<F64 like
/// Lat, Lon> annotations must agree.
let indexRecordsAgree (a: IRIndexType) (b: IRIndexType) : bool =
    a.Rank = b.Rank && a.Symmetry = b.Symmetry && a.IxKind = b.IxKind
    && a.Kind = b.Kind && a.Tag = b.Tag && a.Extent = b.Extent

/// Whole-shape agreement: same record count, records pairwise agree.
let indexShapesAgree (xs: IRIndexType list) (ys: IRIndexType list) : bool =
    xs.Length = ys.Length && List.forall2 indexRecordsAgree xs ys

/// A record list co-iteration can span: exactly one record (dense rank-1 OR
/// packed symmetric of any logical rank — walked as flat canonical cells), or
/// several records ALL plain dense (the product space). Mixed dense+packed
/// multi-record shapes are rejected — the packed record's triangular walk
/// cannot interleave a foreign dense axis.
let coIterableRecords (recs: IRIndexType list) : bool =
    recs.Length = 1 || recs |> List.forall isPlainDenseIx

/// Shared iteration records for a zip co-iteration, from the operands' array
/// types. Single-record operands (dense rank-1, packed symmetric) keep the
/// HISTORICAL first-record rule with no agreement check — byte-compatible
/// with every pre-existing zip. Multi-record operands (dense rank ≥ 2) span
/// the FULL product of records and require structural agreement + all-plain-
/// dense records (mixed dense/packed multi-axis rejects).
let zipSharedRecords (arrayTypes: IRArrayType list) : Result<IRIndexType list, TypeError> =
    match arrayTypes with
    | [] -> Ok []
    | first :: rest ->
        let shape0 = first.IndexTypes
        let minRank = arrayTypes |> List.map (fun at -> at.IndexTypes.Length) |> List.min
        if shape0.Length <= 1 || minRank <= 1 then
            // Historical single-record rule (first array's first record).
            Ok (if minRank > 0 then [shape0.Head] else [])
        elif not (rest |> List.forall (fun at -> indexShapesAgree at.IndexTypes shape0)) then
            Error (Other "co-iteration over multi-axis arrays requires all operands to have identical index shapes (same records: tags, extents, symmetry)")
        elif not (coIterableRecords shape0) then
            Error (Other "co-iteration spans one index record per operand (dense rank-1 or packed symmetric), or a product of plain-dense records; mixed dense/packed multi-axis shapes are not supported")
        else
            Ok shape0

/// halo<Inner, offsets> slot construction — shared by the expression form
/// (`method_for(halo<..>)`) and the range<> slot form (`range<halo<..>, ..>`).
/// The offsets payload is either
///   - a FLAT int array `[-1, 0, 1]`  -> ONE slot (a 1-D halo), or
///   - an array of per-axis int arrays `[[-1,0,1],[0],[-1,0,1]]` -> k slots
///     over the SAME inner index (arity = sub-array count), the n-D product
///     window written as one halo.
/// Each slot is the inner index SHRUNK to its interior (BndShrink: every
/// declared neighbor of every iterated center is in-bounds, so window reads
/// need no guards) and tagged "__halowin|<d|c>:<innerName>|<o1,o2,..>". The
/// center's start offset (max(0, -min offsets∪{0})) is re-derived from the
/// tag at loop-building time (IR mkElement via Types.haloStartOffsetOfTag) —
/// per-slot, which the single shared IRRange offset cannot express for
/// multi-slot ranges.
let haloSlotsOf (env: TypeEnv) (innerTy: TypeExpr) (offsetsExpr: Expr) : TypeResult<IRIndexType list> =
    let inner = lowerIndexType env 0 innerTy
    // One slot from one flat per-axis offset set.
    let slotOfInts (offsets: int list) : TypeResult<IRIndexType> =
        if List.isEmpty offsets then Error (Other "halo<...>: offsets array must be non-empty")
        elif inner.Rank <> 1 then
            Error (Other "halo<...>: the inner index must be rank-1 (n-D = per-axis offset arrays [[..],[..],..] or separate range<halo<..>, ..> slots)")
        else
        let offCsv = offsets |> List.map string |> String.concat ","
        match inner.Extent with
        | IRCompoundMask _ ->
            // Compound inner: ordinals walk the PRESENT cells, so "next"
            // is the next present cell (the hashed-index generalization).
            // The mask cardinality is runtime — the interior shrink can't
            // fold into the extent here; it rides the tag and is applied
            // at the loop bound (buildLoopNestCodeGen StrictOffset).
            // IxKCompound is KEPT so the cidx materialization and
            // cardinality-bound machinery still engage.
            Ok { inner with
                    Id = env.Builder.FreshId()
                    Extent = inner.Extent
                    Tag = Some (sprintf "__halowin|c:|%s" offCsv)
                    IxKind = IxKCompound }
        | _ ->
            // Dense inner. Reach includes the implicit center 0: w(0) is
            // always readable even when 0 is not in the declared set
            // (e.g. lag sets [-12,-24]).
            let lo = min 0 (List.min offsets)
            let hi = max 0 (List.max offsets)
            let shrink = int64 (-lo + hi)
            let shrunkExtent =
                match inner.Extent with
                | IRLit (IRLitInt n) -> IRLit (IRLitInt (n - shrink))
                | e -> IRBinOp (IRElementwise, IRSub, e, IRLit (IRLitInt shrink))
            let innerName =
                match inner.Tag with
                | Some n when not (n.StartsWith("__")) -> n
                | _ -> ""
            Ok { inner with
                    Id = env.Builder.FreshId()
                    Extent = shrunkExtent
                    Tag = Some (sprintf "__halowin|d:%s|%s" innerName offCsv)
                    IxKind = IxKPlain }
    match evalStaticValueExpr env offsetsExpr with
    | Error msg -> Error (Other (sprintf "halo<...>: offsets must be a compile-time int array (%s)" msg))
    | Ok sv ->
        let asInt = function StaticEval.SVInt n -> Some (int n) | _ -> None
        match sv with
        | StaticEval.SVInt n -> slotOfInts [int n] |> Result.map List.singleton
        | StaticEval.SVTuple vs when not vs.IsEmpty ->
            let flat = vs |> List.map asInt
            if List.forall Option.isSome flat then
                // Flat form: one axis.
                slotOfInts (flat |> List.map Option.get) |> Result.map List.singleton
            else
                // Nested form: every entry must be a non-empty int array;
                // each becomes one slot over the same inner index.
                let perAxis =
                    vs |> List.map (function
                        | StaticEval.SVTuple xs ->
                            let os = xs |> List.map asInt
                            if List.forall Option.isSome os && not os.IsEmpty
                            then Some (os |> List.map Option.get) else None
                        | _ -> None)
                if List.forall Option.isSome perAxis then
                    // (local sequencer: TypeCheck's sequenceResults is defined
                    // further down the file, out of scope here)
                    perAxis
                    |> List.map (Option.get >> slotOfInts)
                    |> List.fold (fun acc r ->
                        match acc, r with
                        | Ok xs, Ok x -> Ok (xs @ [x])
                        | Error e, _ -> Error e
                        | _, Error e -> Error e) (Ok [])
                else
                    Error (Other "halo<...>: offsets must be a flat int array [-1,0,1] or an array of per-axis int arrays [[-1,0,1],[0],[-1,0,1]] (no mixing, no empty axes)")
        | _ -> Error (Other "halo<...>: offsets must be a compile-time array of integer literals, e.g. [-1, 0, 1]")

// ============================================================================
// 5. Capture Analysis
// ============================================================================

/// Collect free variables in an expression (names not bound in local scope).
let rec collectFreeVars (bound: Set<string>) (expr: Expr) : Set<string> =
    match expr.Kind with
    | ExprKind.ExprVar name ->
        if Set.contains name bound then Set.empty else Set.singleton name
    | ExprKind.ExprLit _ -> Set.empty
    | ExprKind.ExprBinOp (_, _, l, r) ->
        Set.union (collectFreeVars bound l) (collectFreeVars bound r)
    | ExprKind.ExprUnaryOp (_, e) -> collectFreeVars bound e
    | ExprKind.ExprApp (f, args) ->
        Set.unionMany (collectFreeVars bound f :: (args |> List.map (collectFreeVars bound)))
    | ExprKind.ExprLambda (parms, _, body) ->
        let bound' = parms |> List.fold (fun s p -> Set.add p.Name s) bound
        collectFreeVars bound' body
    | ExprKind.ExprLet (binding, body) ->
        let valFree = collectFreeVars bound binding.Value
        let names = patternNames binding.Pattern
        let bound' = names |> List.fold (fun s n -> Set.add n s) bound
        Set.union valFree (collectFreeVars bound' body)
    | ExprKind.ExprIf (c, t, e) ->
        Set.unionMany [collectFreeVars bound c; collectFreeVars bound t; collectFreeVars bound e]
    | ExprKind.ExprTuple es | ExprKind.ExprArrayLit es | ExprKind.ExprZip es | ExprKind.ExprStack es | ExprKind.ExprSequence es ->
        es |> List.map (collectFreeVars bound) |> Set.unionMany
    | ExprKind.ExprMatch (scr, cases) ->
        let scrFree = collectFreeVars bound scr
        let caseFree = cases |> List.map (fun c ->
            let names = patternNames c.Pattern
            let bound' = names |> List.fold (fun s n -> Set.add n s) bound
            let guardFree = c.Guard |> Option.map (collectFreeVars bound')
                            |> Option.defaultValue Set.empty
            Set.union guardFree (collectFreeVars bound' c.Body))
        Set.union scrFree (Set.unionMany caseFree)
    | ExprKind.ExprBlock (stmts, finalExpr) ->
        let mutable b = bound
        let mutable free = Set.empty
        for stmt in stmts do
            match unwrapStmt stmt with
            | StmtSpanned _ -> ()  // unreachable: unwrapStmt strips the annotation
            | StmtLet binding ->
                free <- Set.union free (collectFreeVars b binding.Value)
                b <- patternNames binding.Pattern |> List.fold (fun s n -> Set.add n s) b
            | StmtAssign (lhs, _, rhs) ->
                free <- Set.union free (Set.union (collectFreeVars b lhs) (collectFreeVars b rhs))
            | StmtExpr e ->
                free <- Set.union free (collectFreeVars b e)
            | StmtForIn (varName, rangeExpr, bodyStmts) ->
                free <- Set.union free (collectFreeVars b rangeExpr)
                // Recurse over the body with the loop variable bound; nested
                // for-in loops recurse through the same walker. NOTE: like
                // the outer StmtLet case, lets inside the body extend the
                // bound set for SUBSEQUENT statements.
                let rec walkForBody (bound: Set<string>) (stmts: Stmt list) =
                    let mutable bb = bound
                    for bodyStmt in stmts do
                        match unwrapStmt bodyStmt with
                        | StmtSpanned _ -> ()  // unreachable: unwrapStmt strips the annotation
                        | StmtLet binding ->
                            free <- Set.union free (collectFreeVars bb binding.Value)
                            bb <- patternNames binding.Pattern |> List.fold (fun s n -> Set.add n s) bb
                        | StmtAssign (lhs, _, rhs) ->
                            free <- Set.union free (Set.union (collectFreeVars bb lhs) (collectFreeVars bb rhs))
                        | StmtExpr e ->
                            free <- Set.union free (collectFreeVars bb e)
                        | StmtForIn (v2, range2, body2) ->
                            free <- Set.union free (collectFreeVars bb range2)
                            walkForBody (Set.add v2 bb) body2
                walkForBody (Set.add varName b) bodyStmts
        match finalExpr with
        | Some e -> Set.union free (collectFreeVars b e)
        | None -> free
    | ExprKind.ExprMethodFor arrays -> arrays |> List.map (collectFreeVars bound) |> Set.unionMany
    | ExprKind.ExprObjectFor kernel -> collectFreeVars bound kernel
    | ExprKind.ExprPure e | ExprKind.ExprCompute e | ExprKind.ExprRead e | ExprKind.ExprRank e -> collectFreeVars bound e
    | ExprKind.ExprGuard (c, b) -> Set.union (collectFreeVars bound c) (collectFreeVars bound b)
    | ExprKind.ExprMask (a, p) -> Set.union (collectFreeVars bound a) (collectFreeVars bound p)
    | ExprKind.ExprCompound (d, m) -> Set.union (collectFreeVars bound d) (collectFreeVars bound m)
    | ExprKind.ExprIntersect (a, b) -> Set.union (collectFreeVars bound a) (collectFreeVars bound b)
    | ExprKind.ExprUnion (a, b) -> Set.union (collectFreeVars bound a) (collectFreeVars bound b)
    | ExprKind.ExprUnique a -> collectFreeVars bound a
    | ExprKind.ExprContains (a, v) -> Set.union (collectFreeVars bound a) (collectFreeVars bound v)
    | ExprKind.ExprGroupBy (v, k) -> Set.union (collectFreeVars bound v) (collectFreeVars bound k)
    | ExprKind.ExprGroupKeys ks -> ks |> List.map (collectFreeVars bound) |> Set.unionMany
    | ExprKind.ExprSort (a, k) -> Set.union (collectFreeVars bound a) (collectFreeVars bound k)
    | ExprKind.ExprReduce (a, k, i) ->
        let baseVars = Set.union (collectFreeVars bound a) (collectFreeVars bound k)
        match i with
        | Some e -> Set.union baseVars (collectFreeVars bound e)
        | None -> baseVars
    | ExprKind.ExprExtents a -> collectFreeVars bound a
    | ExprKind.ExprReynolds (k, _) -> collectFreeVars bound k
    | ExprKind.ExprField (e, _) -> collectFreeVars bound e
    | ExprKind.ExprTupleIndex (t, i) -> Set.union (collectFreeVars bound t) (collectFreeVars bound i)
    | ExprKind.ExprStruct (_, fields, spread) ->
        let spreadRefs = spread |> Option.map (collectFreeVars bound) |> Option.defaultValue Set.empty
        Set.union spreadRefs (fields |> List.map (snd >> collectFreeVars bound) |> Set.unionMany)
    | ExprKind.ExprReplicate (n, b) -> Set.union (collectFreeVars bound n) (collectFreeVars bound b)
    | ExprKind.ExprDotDot (lo, hi) -> Set.union (collectFreeVars bound lo) (collectFreeVars bound hi)
    | ExprKind.ExprTyped (e, _) -> collectFreeVars bound e
    | ExprKind.ExprAssign (l, r) -> Set.union (collectFreeVars bound l) (collectFreeVars bound r)
    | ExprKind.ExprFor (src, _, kernelOpt) ->
        let srcFree = match src with
                      | ForArrays (arrs, inOpt) -> 
                          let arrFree = arrs |> List.map (collectFreeVars bound) |> Set.unionMany
                          let inFree = inOpt |> Option.map (collectFreeVars bound) |> Option.defaultValue Set.empty
                          Set.union arrFree inFree
                      | ForKernel k -> collectFreeVars bound k
        let kFree = kernelOpt |> Option.map (collectFreeVars bound) |> Option.defaultValue Set.empty
        Set.union srcFree kFree
    | ExprKind.ExprAlign (es, _) -> es |> List.map (collectFreeVars bound) |> Set.unionMany
    | _ -> Set.empty

/// Extract variable names bound by a pattern.
and patternNames (pat: Pattern) : string list =
    match pat.Kind with
    | PatternKind.PatWildcard -> []
    | PatternKind.PatVar name -> [name]
    | PatternKind.PatLit _ -> []
    | PatternKind.PatTuple pats -> pats |> List.collect patternNames
    | PatternKind.PatCons (h, t) -> patternNames h @ patternNames t
    | PatternKind.PatStruct (_, fields) -> fields |> List.collect (fun (_, p) -> patternNames p)
    | PatternKind.PatVariant (_, Some p) -> patternNames p
    | PatternKind.PatVariant (_, None) -> []
    | PatternKind.PatGuarded (p, _) -> patternNames p
    | PatternKind.PatTyped (p, _) -> patternNames p

/// Build TypedVarInfo capture list from free variable names.
let buildCaptures (env: TypeEnv) (freeVars: Set<string>) : TypedVarInfo list =
    freeVars |> Set.toList |> List.choose (fun name ->
        let info = match Map.tryFind name env.OuterScope with
                   | Some i -> Some i
                   | None -> Map.tryFind name env.Variables
        info |> Option.map (fun i ->
            { Name = name; Type = i.Type; Identity = i.Identity
              IsMutable = (i.Assign <> ReadOnly); VarId = i.VarId }))

/// Validate no array captures in a lambda.
let validateNoArrayCaptures (captures: TypedVarInfo list) : TypeResult<unit> =
    match captures |> List.tryFind (fun c -> match c.Type with ArrayElem _ -> true | _ -> false) with
    | Some c -> Error (InvalidArrayCapture c.Name)
    | None -> Ok ()

// ============================================================================
// 6. Commutativity Extraction
// ============================================================================

let extractCommGroups (parms: LambdaParam list) (whereClause: WhereClause option) : int list list =
    match whereClause with
    | Some wc ->
        wc.Commutativity |> List.map (fun names ->
            names |> List.choose (fun name ->
                parms |> List.tryFindIndex (fun p -> p.Name = name)))
    | None -> []

// ============================================================================
// 7. Array Type Utilities
// ============================================================================

let inferElemType (exprs: TypedExpr list) : IRType =
    // S3 tag: relic for empty literal — defaults to Float64. Acceptable
    // because empty literals are rank-0 placeholders with no useful elem
    // type to infer. For non-empty: extract from the first expr's type.
    if List.isEmpty exprs then IRTScalar ETFloat64
    else
        match exprs.[0].Type with
        | ArrayElem arr -> arr.ElemType  // Already IRType
        | IRTScalar _ as t -> t          // Pass through
        | t -> t                          // Other types (Named, Tuple, etc.) — propagate

let inferArrayLitType (builder: IRBuilder) (exprs: TypedExpr list) : IRArrayType =
    let elemType = inferElemType exprs

    // Check for ragged structure at the second level. A ragged literal is one
    // where outer entries are themselves arrays whose lengths differ. When
    // detected, produce a RaggedIdx-typed result instead of a rectangular one.
    //
    // Note: we only check at the immediate inner level. Deeper raggedness
    // (rank-3+ with internal raggedness) is not yet supported; such literals
    // will produce wrong-shape output but no compile error.
    let isRaggedAtSecondLevel =
        match exprs with
        | [] -> false
        | first :: _ ->
            match first.Kind with
            | TExprArrayLit _ ->
                let innerLengths =
                    exprs |> List.map (fun e ->
                        match e.Kind with
                        | TExprArrayLit (inner, _) -> Some inner.Length
                        | _ -> None)
                // Ragged when lengths exist for all entries and differ
                match innerLengths |> List.choose id with
                | [] -> false
                | first :: rest -> rest |> List.exists (fun n -> n <> first)
            | _ -> false

    // Array-valued element expressions: when the outer literal's entries are
    // not bracket sub-literals but expressions whose TYPE is already an array
    // (computed rows bound to names, e.g. `method_for(..) |> compute`
    // results), the bracket contributes only the outer dimension — the inner
    // index structure comes from the elements' own array type. Without this,
    // getShape below (which walks TExprArrayLit nesting only) inferred a
    // rank-1 array of scalars, and codegen assigned Array wrappers into
    // scalar slots. Restricted to plain dense element index types (rank-1
    // per index, no symmetry/dependencies, not virtual); exotic element
    // shapes keep the previous behavior.
    let rowTypedElemArr =
        match exprs with
        | first :: _ ->
            match first.Kind, first.Type with
            | TExprArrayLit _, _ -> None
            | _, ArrayElem elemArr when
                not elemArr.IsVirtual
                && not elemArr.IndexTypes.IsEmpty
                && elemArr.IndexTypes |> List.forall (fun ix ->
                    ix.Rank = 1 && ix.Symmetry = SymNone && ix.Dependencies.IsEmpty) ->
                Some elemArr
            | _ -> None
        | [] -> None

    if rowTypedElemArr.IsSome then
        let elemArr = rowTypedElemArr.Value
        let outerIdx = {
            Id = builder.FreshId(); Rank = 1; Extent = IRLit (IRLitInt (int64 exprs.Length))
            Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = []
        }
        // Fresh Ids: the literal's dimensions are new index-space occurrences,
        // not the source rows' (mirrors the fresh-Id policy of the
        // rectangular branch below). Extent/Tag/Kind carry over.
        let innerIdxs = elemArr.IndexTypes |> List.map (fun ix -> { ix with Id = builder.FreshId() })
        { ElemType = elemArr.ElemType; IndexTypes = outerIdx :: innerIdxs; IsVirtual = false; Identity = None }
    elif isRaggedAtSecondLevel then
        // Build a RaggedIdx-typed array. Outer index has extent = number of
        // entries (rectangular at outer level). Inner index is RaggedIdx with
        // an IRRaggedLookup whose lengths reference is synthesized from the
        // literal's actual sub-array lengths (computed at codegen time).
        let n = List.length exprs
        let outerIdx = {
            Id = builder.FreshId(); Rank = 1; Extent = IRLit (IRLitInt (int64 n))
            Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = []
        }
        // The lengths reference is a synthetic IRParam — codegen recognizes
        // this and emits the lengths array inline from the literal structure.
        // The "__inline_lens" name is a sentinel that the codegen detects.
        let innerIdx = {
            Id = builder.FreshId(); Rank = 1
            Extent = IRRaggedLookup (IRParam ("__inline_lens", 0, IRTNat None))
            Symmetry = SymNone; Tag = Some "__raggedidx_inline"; IxKind = IxKRaggedInline
            Kind = SDimension; Dependencies = []
        }
        { ElemType = elemType; IndexTypes = [outerIdx; innerIdx]; IsVirtual = false; Identity = None }
    else
        // Rectangular: existing behavior — first sub-array's length defines all rows.
        let rec getShape (es: TypedExpr list) : int list =
            match es with
            | [] -> [0]
            | first :: _ ->
                match first.Kind with
                | TExprArrayLit (inner, _) -> List.length es :: getShape inner
                | _ -> [List.length es]
        let shape = getShape exprs
        let indexTypes = shape |> List.map (fun extent ->
            { Id = builder.FreshId(); Rank = 1; Extent = IRLit (IRLitInt (int64 extent))
              Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] })
        { ElemType = elemType; IndexTypes = indexTypes; IsVirtual = false; Identity = None }

let getArrayType (env: TypeEnv) (expr: Expr) : IRArrayType =
    match expr.Kind with
    | ExprKind.ExprVar name ->
        match lookupVar name env with
        | Some info ->
            match info.Type with
            | ArrayElem arrTy -> arrTy
            | _ ->
                { ElemType = IRTScalar ETFloat64
                  IndexTypes = [{ Id = env.Builder.FreshId(); Rank = 1
                                  Extent = IRParam(name + "_n", 0, IRTNat None)
                                  Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension
                                  Dependencies = [] }]
                  IsVirtual = false; Identity = Some (AIDVariable name) }
        | None ->
            { ElemType = IRTScalar ETFloat64
              IndexTypes = [{ Id = env.Builder.FreshId(); Rank = 1
                              Extent = IRParam(name + "_n", 0, IRTNat None)
                              Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension
                              Dependencies = [] }]
              IsVirtual = false; Identity = Some (AIDVariable name) }
    | _ ->
        { ElemType = IRTScalar ETFloat64; IndexTypes = []; IsVirtual = false; Identity = None }

// ============================================================================
// 8. Helpers
// ============================================================================

let sequenceResults (results: TypeResult<'a> list) : TypeResult<'a list> =
    let rec loop acc = function
        | [] -> Ok (List.rev acc)
        | Ok x :: rest -> loop (x :: acc) rest
        | Error e :: _ -> Error e
    loop [] results

/// Infer the type of a literal.
let inferLiteralType lit =
    match lit with
    | LitInt _ -> IRTScalar ETInt64
    | LitFloat _ -> IRTScalar ETFloat64
    | LitBool _ -> IRTScalar ETBool
    | LitString _ -> IRTScalar ETString
    | LitChar _ -> IRTScalar ETInt32
    | LitUnit -> IRTUnit

// ============================================================================
// 9. Pattern Type Checking
// ============================================================================

/// Type-check a pattern against an expected type. Returns a TypedPattern
/// whose Bindings list contains every (name, varId, type) introduced.
let rec checkPattern (env: TypeEnv) (expected: IRType) (pat: Pattern)
    : TypeResult<TypedPattern> =
    match pat.Kind with
    | PatternKind.PatWildcard ->
        Ok { Kind = TPatWild; Type = expected; Bindings = [] }

    | PatternKind.PatVar name ->
        // Check if this name is a registered variant tag (enum constructor without data).
        // The parser can't distinguish `| North -> ...` (variant match) from `| x -> ...` (variable binding)
        // because it doesn't have type information. We resolve the ambiguity here.
        match Map.tryFind name env.VariantTags with
        | Some (parentName, None) ->
            // This is a no-payload variant constructor — treat as PatVariant
            checkPattern env expected (inheritPatSpan pat (PatVariant (name, None)))
        | Some (parentName, Some _) ->
            // Variant with payload but used without — treat as variable (may shadow)
            let varId = env.Builder.FreshId()
            Ok { Kind = TPatVar (name, varId); Type = expected
                 Bindings = [(name, varId, expected)] }
        | None ->
            let varId = env.Builder.FreshId()
            Ok { Kind = TPatVar (name, varId); Type = expected
                 Bindings = [(name, varId, expected)] }

    | PatternKind.PatLit lit ->
        let litTy = inferLiteralType lit
        unify env.Subst litTy expected |> Result.map (fun () ->
            { Kind = TPatLit lit; Type = expected; Bindings = [] })

    | PatternKind.PatTuple pats ->
        match env.Subst.Resolve expected with
        | IRTTuple tys when tys.Length = pats.Length ->
            List.zip pats tys |> List.map (fun (p, t) -> checkPattern env t p)
            |> sequenceResults |> Result.map (fun tPats ->
                { Kind = TPatTuple tPats; Type = expected
                  Bindings = tPats |> List.collect (fun p -> p.Bindings) })
        | _ ->
            let tys = pats |> List.map (fun _ -> env.Subst.Fresh())
            let tupleTy = IRTTuple tys
            unify env.Subst tupleTy expected |> Result.bind (fun () ->
                List.zip pats tys |> List.map (fun (p, t) -> checkPattern env t p)
                |> sequenceResults |> Result.map (fun tPats ->
                    { Kind = TPatTuple tPats; Type = expected
                      Bindings = tPats |> List.collect (fun p -> p.Bindings) }))

    | PatternKind.PatCons (headPat, tailPat) ->
        let headTy = env.Subst.Fresh()
        let tailTy = env.Subst.Fresh()
        checkPattern env headTy headPat |> Result.bind (fun tHead ->
        checkPattern env tailTy tailPat |> Result.bind (fun tTail ->
            Ok { Kind = TPatCons (tHead, tTail); Type = expected
                 Bindings = tHead.Bindings @ tTail.Bindings }))

    | PatternKind.PatVariant (tag, payloadPat) ->
        match Map.tryFind tag env.VariantTags with
        | Some (parentName, payloadTy) ->
            let isEnum = isEnumType env parentName
            match payloadPat, payloadTy with
            | Some p, Some ty ->
                checkPattern env ty p |> Result.map (fun tPayload ->
                    { Kind = TPatVariant (tag, Some tPayload, isEnum)
                      Type = IRTNamed parentName
                      Bindings = tPayload.Bindings })
            | None, None ->
                Ok { Kind = TPatVariant (tag, None, isEnum)
                     Type = IRTNamed parentName; Bindings = [] }
            | Some p, None ->
                Error (PatternTypeMismatch (sprintf "%s(...)" tag, expected))
            | None, Some _ ->
                Ok { Kind = TPatVariant (tag, None, isEnum)
                     Type = IRTNamed parentName; Bindings = [] }
        | None ->
            // Unknown variant tag: allow it, bind any payload
            match payloadPat with
            | Some p ->
                let payTy = env.Subst.Fresh()
                checkPattern env payTy p |> Result.map (fun tPayload ->
                    { Kind = TPatVariant (tag, Some tPayload, false); Type = expected
                      Bindings = tPayload.Bindings })
            | None ->
                Ok { Kind = TPatVariant (tag, None, false); Type = expected; Bindings = [] }

    | PatternKind.PatStruct (typeName, fieldPats) ->
        let fieldTypes =
            match lookupTypeDef typeName env with
            | Some (TDIStruct (_, _, fields, _)) ->
                fields |> List.map (fun (n, t) -> (n, t)) |> Map.ofList
            | _ -> Map.empty
        fieldPats |> List.map (fun (fname, fpat) ->
            let fTy = Map.tryFind fname fieldTypes |> Option.defaultValue (env.Subst.Fresh())
            checkPattern env fTy fpat |> Result.map (fun tp -> (fname, tp)))
        |> sequenceResults |> Result.map (fun tFields ->
            { Kind = TPatStruct (typeName, tFields)
              Type = (if Map.isEmpty fieldTypes then expected else IRTNamed typeName)
              Bindings = tFields |> List.collect (fun (_, p) -> p.Bindings) })

    | PatternKind.PatGuarded (innerPat, _guardExpr) ->
        // Guard expression is type-checked in inferMatch when we have full env
        checkPattern env expected innerPat |> Result.map (fun tInner ->
            { Kind = TPatGuarded (tInner, mkTyped (TExprLit (LitBool true)) (IRTScalar ETBool))
              Type = expected; Bindings = tInner.Bindings })

    | PatternKind.PatTyped (innerPat, tyAnnotation) ->
        let annotTy = lowerTypeExpr env tyAnnotation
        unify env.Subst annotTy expected |> Result.bind (fun () ->
            checkPattern env annotTy innerPat)

// ============================================================================
// 10. Expression Type Inference (every Expr variant handled explicitly)
// ============================================================================

/// Phase C helper: drive type inference for special forms (extents, mask,
/// sort, intersect, union, group_keys, group_by) when the array argument
/// is unresolved (typically an unannotated kernel parameter).
///
/// The pre-Phase-C pattern was "inspect resolved type, reject if not an
/// IRTArray." That fails with `requires array` when the argument is an
/// unbound IRTInfer — even though the special form could provide the
/// constraint that determines what the inference variable should be.
///
/// This helper inverts the pattern: if the argument is unresolved,
/// synthesize a fresh IRTArray (rank 1, fresh elem type, fresh anonymous
/// index) and unify the argument with it. Subsequent uses of the argument
/// then see the array shape via substitution. If the argument is already
/// concrete-but-not-an-array, that's a real type error.
///
/// Centralizing this logic here (rather than duplicating across each
/// special form) keeps the inference machinery aggregated high in the IR
/// stream — useful for IDE/language-server tools that want to expose
/// statically evaluable types without triggering deep code paths.
///
/// Returns the resolved IRArrayType. The synthesized index has Tag=None
/// (matched as "synthetic" by unify, so it doesn't fail nominal checks
/// against real source-array tags).
let requireArrayArg (env: TypeEnv) (tArr: TypedExpr) (opName: string) : TypeResult<IRArrayType> =
    let resolved = env.Subst.Resolve(tArr.Type)
    match resolved with
    | ArrayElem arrTy -> Ok arrTy
    | IRTInfer _ ->
        let freshIdx = {
            Id = env.Builder.FreshId()
            Rank = 1
            Extent = IRParam (sprintf "__%s_inferred_n" opName, 0, IRTNat None)
            Symmetry = SymNone
            Tag = None; IxKind = IxKPlain
            Kind = SDimension
            Dependencies = []
        }
        let freshElem = env.Builder.FreshInferType()
        let freshArrType = {
            ElemType = freshElem
            IndexTypes = [freshIdx]
            IsVirtual = false
            Identity = None
        }
        unify env.Subst tArr.Type (mkArrayLike freshArrType)
        |> Result.bind (fun () ->
            // Re-resolve in case unification refined the elem type via
            // some other constraint already in the substitution.
            match env.Subst.Resolve(tArr.Type) with
            | ArrayElem a -> Ok a
            | _ -> Error (IntrinsicBindArrayFailed opName))
    | _ ->
        Error (IntrinsicNeedsArray opName)

/// Tag-check helper: validate that each index argument's nominal tag (if any)
/// agrees with the corresponding array slot's nominal tag. Slot tags starting
/// with "__" are internal synthetic markers and skipped. Untagged ints into
/// named slots are permissive (warning emitted, no error) — iteration-tagging
/// typically resolves these later.
///
/// Pulled out as a separate helper so the same logic can run BOTH at the
/// indexing call site (eager check via dispatchAppOrIndex) AND as a
/// post-unification pass over a kernel body (revalidateBodyTagChecks).
let private checkArrayIndexTags (env: TypeEnv) (arrTy: IRArrayType) (tArgs: TypedExpr list) : TypeResult<unit> =
    let tagMismatch =
        List.zip tArgs (arrTy.IndexTypes |> List.truncate tArgs.Length)
        |> List.tryPick (fun (tArg, idxType) ->
            match idxType.Tag with
            | Some tagName when not (tagName.StartsWith("__")) ->
                match env.Subst.Resolve tArg.Type with
                | IRTIdxTagged (_, IRefNamed argName)
                    when argName = tagName -> None
                | IRTIdxTagged (_, IRefNamed argName) ->
                    Some (IndexTagMismatchNamed (tagName, argName))
                | IRTIdxTagged (_, IRefAnon _) ->
                    Some (IndexTagMismatchAnon tagName)
                | IRTScalar (ETInt32 | ETInt64) ->
                    emitWarning env (sprintf
                        "Array indexed with untagged integer where slot expects tag '%s'. Consider an explicit cast like `(expr : %s)` or iterate via `range<%s>` to flow the tag automatically."
                        tagName tagName tagName)
                    None
                | _ -> None
            | _ -> None)
    match tagMismatch with
    | Some err -> Error err
    | None -> Ok ()

/// Dispatch a typed function/array expression with typed args into either
/// EnumIdx label subscript: a STRING LITERAL index into an axis whose index
/// type is a registered EnumIdx folds to its ordinal HERE, so lowering,
/// codegen, and the interpreter all see a plain constant subscript — the
/// CSV headered-column access idiom `obs.vars.data[i, "temp"]`. The folded
/// literal is retyped IRTIdxTagged so the nominal tag check accepts it.
/// Deliberately restricted to STRING literals: int-valued EnumIdx keys are
/// stored raw (foreign-key semantics, sql-foreign-keys corpus) and are not
/// position-folded. An unknown label is a type error naming the available
/// labels. Runtime (non-literal) label subscripts stay unsupported.
let private foldEnumIdxLabels (env: TypeEnv) (arrTy: IRArrayType) (tArgs: TypedExpr list) : TypeResult<TypedExpr list> =
    tArgs
    |> List.mapi (fun i a ->
        if i >= arrTy.IndexTypes.Length then Ok a
        else
            match a.Kind, arrTy.IndexTypes.[i].Tag with
            | TExprLit (LitString s), Some tagName ->
                (match Map.tryFind tagName env.TypeDefs with
                 | Some (TDIEnumIdx (_, _, values, _)) ->
                     (match values |> List.tryFindIndex ((=) (EVString s)) with
                      | Some ord ->
                          Ok { a with
                                Kind = TExprLit (LitInt (int64 ord))
                                Type = IRTIdxTagged (IRTScalar ETInt64, IRefNamed tagName) }
                      | None ->
                          let avail = values |> List.map (function EVString v -> v | EVInt n -> string n)
                          Error (EnumIdxUnknownLabel (tagName, s, avail)))
                 | _ -> Ok a)
            | _ -> Ok a)
    |> sequenceResults

/// TExprIndex (array indexing) or TExprApp (function call), with nominal
/// tag-checking on array slots. Shared between the general ExprApp handler
/// and the method-call (ExprField) handler so that indexing through a
/// struct-field array (`data.region(s)`) enforces the same tag discipline
/// as indexing through a plain variable.
///
/// Tag-check semantics: see checkArrayIndexTags above.
/// Index-arity validation for a CompoundIdx slot (formalism 4.5). When the
/// NEXT index slot to be consumed is a compound of Rank k, the coordinate that
/// fills it must be a single k-tuple `B((c0, ..., c_{k-1}))` -- the canonical
/// poly-index form (5.4), a joint linearize over the whole masked axis. This is
/// deliberately NOT the flat currying form `B(c0, c1)`: once currying could
/// collapse the compound one coordinate at a time, partial forms like
/// `B(c0)(_)(c2)` become ambiguous (phase 2, wildcards). Keeping the compound
/// axis a single tuple keeps that coherent.
///
/// Fires only when the head slot is compound; otherwise Ok (). Validates the
/// FIRST arg against the compound slot and lets any remaining args flow to the
/// trailing regular slots via the normal path (so `B((i,j), t)` is allowed:
/// tuple fills the compound, t fills a trailing Idx).
///
/// Rank-1 compound (k = 1): a masked 1-D index. A bare scalar arg `B(i)` is
/// accepted (the parser collapses a 1-tuple `(i)` to a bare expr anyway), as is
/// an explicit 1-tuple. k >= 2 requires the tuple form.
let private validateCompoundIndex (env: TypeEnv) (arrTy: IRArrayType) (tArgs: TypedExpr list) : TypeResult<unit> =
    match arrTy.IndexTypes with
    | headSlot :: _ when headSlot.IxKind = IxKCompound ->
        let k = headSlot.Rank
        match tArgs with
        | [] -> Ok ()  // no args consumed here (e.g. bare array value); nothing to check
        | firstArg :: _ ->
            // Wildcard compound indexing: a FULL-arity tuple (k elements) with
            // `_` marking FREE axes (formalism 4.5 currying table: B((a, _)),
            // B((_, b)), B((a, _, _)), B((a, _, c)), ...). The residual rank is
            // the wildcard count: 1 free axis degenerates to a dense Idx window
            // (or gather, when non-prefix); >= 2 free axes form a residual
            // CompoundIdx. Multiple wildcards are deliberately ALLOWED here --
            // unlike function partial application (6.2.3, single `_` only) --
            // because the 4.5 table requires them: B((a, _, _)) is the only way
            // to pin a single leading coordinate of a rank-3 compound, since
            // the 1-tuple `(a)` collapses to a bare scalar in the parser.
            let wildcardPositions =
                match firstArg.Kind with
                | TExprTuple elems ->
                    elems |> List.mapi (fun i e -> (i, e))
                          |> List.choose (fun (i, e) -> match e.Kind with TExprWildcard -> Some i | _ -> None)
                | _ -> []
            match firstArg.Kind, wildcardPositions with
            | TExprWildcard, _ ->
                // Bare `B(_)` (the parser collapses a 1-tuple `(_)` to the bare
                // hole). It pins nothing: on a rank-1 compound the "residual"
                // would be the whole array, and on rank >= 2 it is not even a
                // tuple. Reject rather than let the hole flow as a coordinate.
                Error (CompoundBareWildcard k)
            | _, (_ :: _) ->
                let tupleLen =
                    match firstArg.Kind with
                    | TExprTuple es -> es.Length
                    | _ -> 0
                if tupleLen <> k then
                    Error (CompoundWildcardArity (k, tupleLen))
                elif wildcardPositions.Length = k then
                    Error (CompoundAllFree k)
                else Ok ()
            | _, [] ->
              (match env.Subst.Resolve firstArg.Type with
               | IRTTuple tys when tys.Length >= 1 && tys.Length <= k -> Ok ()
                // 1 <= j <= k: full (j = k) or partial (j < k, leading-prefix)
                // index. The residual type is computed by compoundResidualType;
                // codegen reconstitutes it as a shared window (rank >= 2) or a
                // dense window (rank 1).
               | IRTTuple tys ->
                   Error (CompoundOverSupplied (k, tys.Length))
               | _ when k = 1 -> Ok ()  // rank-1 compound: bare scalar index is fine
               | _ ->
                   // k >= 2 but the first arg is not a tuple. This is the flat
                   // currying form B(c0, c1, ...) or a single scalar -- reject and
                   // point at the canonical tuple form.
                   Error (CompoundNeedsTuple k))
    | _ -> Ok ()

/// The residual index-type fragment (formalism 4.5 currying table) that
/// REPLACES a rank-k compound slot after pinning j of its coordinates
/// (1 <= j < k for a partial index; j = k full). For a short tuple the pinned
/// axes are the leading prefix; for a full-arity wildcard tuple they are the
/// non-`_` positions -- the residual SHAPE depends only on the count, so this
/// fragment is position-blind. The pinned POSITIONS are carried in the index
/// tuple itself (unit-literal sentinels at free axes, see dispatchAppOrIndex),
/// where codegen reads them to choose window (prefix) vs gather (scattered)
/// reconstitution. Returns the list of index types the compound slot becomes:
///
///   j = k       -> []            (compound fully consumed; trailing dims follow)
///   k - j = 1   -> [dense Idx]   (one free coord: a contiguous window of
///                                 present cells at the pinned prefix)
///   k - j >= 2  -> [CompoundIdx] (a residual masked product space over the
///                                 remaining k-j axes at the pinned prefix)
///
/// Both residual cases carry Extent = IRCompoundProject(parentIR, j) -- the
/// single carrier (Option X); placementOf reads the residual RANK to decide
/// dense (rank 1) vs tabulated (rank >= 2). `parentIR` is the parent compound
/// array's IR reference; the pinned coordinate VALUES live at the indexing
/// site, not in the type. Representation: shared data window, materialized
/// child index (the O(window) reconstitution is phase-2 codegen).
let private compoundResidualType (headSlot: IRIndexType) (parentIR: IRExpr) (j: int) (fresh: unit -> IRId) : IRIndexType list =
    let k = headSlot.Rank
    let residualRank = k - j
    if residualRank <= 0 then
        []  // j = k: fully consumed
    elif residualRank = 1 then
        // Dense residual Idx: one free coordinate, a contiguous [lo,hi) window.
        [ { Id = fresh (); Rank = 1
            Extent = IRCompoundProject (parentIR, j)
            Symmetry = SymNone; Tag = None; IxKind = IxKPlain
            Kind = SDimension; Dependencies = [] } ]
    else
        // Residual CompoundIdx over the remaining k-j axes at the pinned prefix.
        // Tagged __compoundidx so it is treated uniformly with a top-level
        // compound (further indexable, further partial-indexable).
        [ { Id = fresh (); Rank = residualRank
            Extent = IRCompoundProject (parentIR, j)
            Symmetry = SymNone; Tag = Some "__compoundidx"; IxKind = IxKCompound
            Kind = SDimension; Dependencies = [] } ]

let rec private dispatchAppOrIndex (env: TypeEnv) (tFunc: TypedExpr) (tArgs: TypedExpr list) : TypeResult<TypedExpr> =
    match tFunc.Type with
    | ArrayElem arrTy when tArgs.Length <= arrTy.IndexTypes.Length ->
        validateCompoundIndex env arrTy tArgs
        |> Result.bind (fun () ->
        foldEnumIdxLabels env arrTy tArgs
        |> Result.bind (fun tArgs ->
        checkArrayIndexTags env arrTy tArgs
        |> Result.bind (fun () ->
            let identity = match tFunc.Kind with TExprVar (_, _, id) -> id | _ -> None
            // Compound-head consumption: when the FIRST index slot is a compound
            // (formalism 4.5), the first argument (a j-tuple, or a scalar for a
            // rank-1 compound) consumes that ONE slot -- pinning j of its k
            // coordinates -- and the compound slot is REPLACED by its residual
            // fragment (compoundResidualType), not skipped positionally. Any
            // remaining args then consume the trailing regular slots as usual.
            // This differs from the plain positional path (one arg = one slot):
            // a compound is one slot of rank k filled by one k-tuple.
            let headIsCompound =
                match arrTy.IndexTypes with
                | h :: _ -> h.IxKind = IxKCompound
                | [] -> false
            if headIsCompound then
                let headSlot = List.head arrTy.IndexTypes
                let trailingSlots = List.tail arrTy.IndexTypes
                let k = headSlot.Rank
                let firstArg = List.head tArgs
                // Wildcard form (full-arity tuple with `_` marking free axes):
                // validateCompoundIndex has already enforced full arity and at
                // least one pinned coordinate. CONSUME the holes here by
                // rewriting each TExprWildcard element to a unit literal:
                //   * the wildcard-escape scan then correctly sees a consumed
                //     (hole-free) value at every value-forming boundary, and
                //   * lowering carries an unambiguous `IRLit IRLitUnit`
                //     sentinel at each free position inside the index tuple --
                //     unit is never a valid coordinate, so codegen reads the
                //     pinned/free axis split directly off the tuple. The
                //     extent carrier (IRCompoundProject) records only the
                //     pinned COUNT; positions live in the tuple itself.
                let wildPositions =
                    match firstArg.Kind with
                    | TExprTuple elems ->
                        elems |> List.mapi (fun i e -> (i, e))
                              |> List.choose (fun (i, e) -> match e.Kind with TExprWildcard -> Some i | _ -> None)
                    | _ -> []
                let firstArg =
                    if List.isEmpty wildPositions then firstArg
                    else
                        match firstArg.Kind with
                        | TExprTuple elems ->
                            let elems' =
                                elems |> List.map (fun e ->
                                    match e.Kind with
                                    | TExprWildcard -> { e with Kind = TExprLit LitUnit; Type = IRTUnit }
                                    | _ -> e)
                            { firstArg with
                                Kind = TExprTuple elems'
                                Type = IRTTuple (elems' |> List.map (fun e -> e.Type)) }
                        | _ -> firstArg
                let tArgs = firstArg :: List.tail tArgs
                // Trailing-dim wildcards: `B((...), _)` leaves the trailing
                // regular dim FREE -- semantically identical to omitting the
                // arg, because the compact layout is lex-sorted with the
                // trailing block innermost-contiguous, so a free trailing dim
                // is exactly the contiguous slice the shorter form already
                // denotes. Consume (drop) them here so the wildcard-escape
                // scan sees no unconsumed hole. They must form a contiguous
                // SUFFIX of the trailing args: a wildcard BEFORE a supplied
                // trailing index (B((...), _, t)) frees an INTERIOR dim,
                // which requires a data restructure and is rejected (multi-
                // trailing compounds are unsupported throughout anyway).
                let keptRemaining, interiorTrailingHole =
                    let isWild (e: TypedExpr) = match e.Kind with TExprWildcard -> true | _ -> false
                    let rec split acc seenWild args =
                        match args with
                        | [] -> (List.rev acc, false)
                        | e :: rest when isWild e -> split acc true rest
                        | e :: rest ->
                            if seenWild then (List.rev acc, true)
                            else split (e :: acc) false rest
                    split [] false (List.tail tArgs)
                let tArgs = firstArg :: keptRemaining
                // j = pinned coordinate count: tuple arity minus free axes for
                // the wildcard form; tuple arity for a short (prefix) tuple; 1
                // for a scalar (rank-1 compound). validateCompoundIndex already
                // rejected the malformed shapes, so this is well-formed here.
                let j =
                    if not (List.isEmpty wildPositions) then
                        (match firstArg.Kind with
                         | TExprTuple es -> es.Length
                         | _ -> k) - wildPositions.Length
                    else
                        match env.Subst.Resolve firstArg.Type with
                        | IRTTuple tys -> tys.Length
                        | _ -> 1
                // Parent IR reference for the residual extent carrier. Available
                // when the array is a plain variable; a non-var parent (field
                // access, inline chained residual) types via the fragment with
                // an IRLitUnit placeholder parent. The placeholder is harmless:
                // codegen reads the ACTUAL array expression at the IRIndex site
                // for reconstitution, and consults the extent carrier only for
                // pass-throughs (map/lift/var-collection) and tryEvalIntIR,
                // which returns None for it (dynamic extent, correct).
                let parentIR =
                    match tFunc.Kind with
                    | TExprVar (_, vid, _) -> IRVar (vid, tFunc.Type)
                    | _ -> IRLit IRLitUnit
                let residualFragment =
                    compoundResidualType headSlot parentIR j (fun () -> env.Builder.FreshId())
                // Remaining args after the compound tuple consume trailing slots.
                let remainingArgs = List.tail tArgs
                let trailingRemaining =
                    if remainingArgs.Length <= List.length trailingSlots then
                        trailingSlots |> List.skip remainingArgs.Length
                    else trailingSlots  // (validateCompoundIndex/arity guard covers over-supply)
                let finalSlots = residualFragment @ trailingRemaining
                if interiorTrailingHole then
                    Error (Other "A wildcard `_` among the trailing indices of a compound array must come AFTER all supplied trailing indices (a free interior trailing dimension would require restructuring the trailing blocks). Reorder, or leave the trailing dims free by omitting them.")
                elif List.isEmpty finalSlots then
                    Ok (mkTyped (TExprIndex (tFunc, tArgs, identity)) arrTy.ElemType)
                else
                    Ok (mkTyped (TExprIndex (tFunc, tArgs, identity))
                                (mkArrayLike { arrTy with IndexTypes = finalSlots }))
            elif tArgs.Length = arrTy.IndexTypes.Length then
                Ok (mkTyped (TExprIndex (tFunc, tArgs, identity)) arrTy.ElemType)
            else
                let remaining = arrTy.IndexTypes |> List.skip tArgs.Length
                Ok (mkTyped (TExprIndex (tFunc, tArgs, identity))
                            (mkArrayLike { arrTy with IndexTypes = remaining })))))
    | FuncElem (paramTys, retTy) ->
        // IrrepsIdx strictness at DIRECT APPLICATION: plain function calls do
        // not unify parameter types against argument types (historically the
        // args are checked structurally downstream), so the irreps identity
        // check — whose whole point is distinguishing SAME-EXTENT arrays —
        // would be skipped at exactly the seam it exists for. Check just the
        // irreps-vs-irreps index pairs here (both tags parse as IrrepsTag);
        // every other pairing keeps the historical looseness, so this arm is
        // dead code for non-irreps programs. Kernel application is covered
        // separately by buildApplyInfo's real unification.
        let irrepsClash =
            let n = min paramTys.Length tArgs.Length
            List.zip (List.truncate n paramTys) (List.truncate n tArgs)
            |> List.mapi (fun i pair -> (i, pair))
            |> List.tryPick (fun (i, (pTy, arg)) ->
                match env.Subst.Resolve pTy, env.Subst.Resolve arg.Type with
                | ArrayElem pa, ArrayElem aa when pa.IndexTypes.Length = aa.IndexTypes.Length ->
                    List.zip pa.IndexTypes aa.IndexTypes
                    |> List.tryPick (fun (pi, ai) ->
                        match pi.Tag, ai.Tag with
                        | Some (IrrepsTag _), Some (IrrepsTag _) when indexPairIncompatible pi ai ->
                            Some (i, pi, ai)
                        | _ -> None)
                | _ -> None)
        // Unit strictness at DIRECT APPLICATION, same seam as the irreps
        // check above: since args are not unified against params, unit
        // signatures would otherwise never meet at a call site. When BOTH
        // sides carry a signature (scalar position or array-element
        // position) they must agree; bare<->annotated stays permissive —
        // that is how unitful values are introduced from literals.
        let unitClash =
            let n = min paramTys.Length tArgs.Length
            let sigOf t =
                match env.Subst.Resolve t with
                | ArrayElem at -> IR.getUnits at.ElemType
                | resolved -> IR.getUnits resolved
            List.zip (List.truncate n paramTys) (List.truncate n tArgs)
            |> List.mapi (fun i (pTy, arg) -> (i, sigOf pTy, sigOf arg.Type))
            |> List.tryPick (fun (i, pu, au) ->
                match pu, au with
                | Some pu, Some au when not (unitCompatible pu au) -> Some (i, pu, au)
                | _ -> None)
        match irrepsClash, unitClash with
        | Some (i, pi, ai), _ ->
            Error (IrrepsIdxArgMismatch (i + 1, ppIndexType pi, ppIndexType ai))
        | None, Some (i, pu, au) ->
            Error (UnitMismatch (sprintf "argument %d" (i + 1), ppUnitSig pu, ppUnitSig au))
        | None, None ->
            // A Poly<T^r> pack param makes the arrow variadic — its declared
            // param count says nothing about legal call-site arg counts, so
            // arity accounting stands down (monomorphization owns the call).
            let isVariadic =
                paramTys |> List.exists (fun t ->
                    match env.Subst.Resolve t with IRTPoly _ -> true | _ -> false)
            if isVariadic then
                Ok (mkTyped (TExprApp (tFunc, tArgs)) retTy)
            elif tArgs.Length > paramTys.Length then
                // Curried over-application: this arrow consumes its declared
                // params; the remainder re-dispatches against the result
                // type — a function result curries on, an array result falls
                // into dimensional indexing. A scalar result makes the
                // surplus a plain arity error.
                let now, rest = List.splitAt paramTys.Length tArgs
                let head = mkTyped (TExprApp (tFunc, now)) retTy
                match env.Subst.Resolve retTy with
                | FuncElem _ | ArrayElem _ -> dispatchAppOrIndex env head rest
                | _ -> Error (ArityMismatch (paramTys.Length, tArgs.Length))
            elif tArgs.Length < paramTys.Length then
                // Under-application reaching dispatch is NOT partial
                // application: the ExprApp arm eta-expands 0 < k < n before
                // dispatching, so this is either a zero-arg call `f()` of an
                // n-ary function or an under-applied function-typed struct
                // field (deferred feature) — both genuine arity errors.
                Error (ArityMismatch (paramTys.Length, tArgs.Length))
            else
                Ok (mkTyped (TExprApp (tFunc, tArgs)) retTy)
    | _ ->
        let retTy = env.Subst.Fresh()
        Ok (mkTyped (TExprApp (tFunc, tArgs)) retTy)

/// Re-run the tag-check at every TExprIndex site reachable from `expr`.
/// Called after buildApplyInfo's kernel-parameter unification, when the
/// substitution may have pinned previously-unresolved inference variables
/// to nominally-tagged types. The original eager check (in
/// dispatchAppOrIndex) saw those variables as IRTInfer and let them
/// through; this post-pass catches the now-resolved mismatches.
///
/// Without this, indexing through an iteration-tagged kernel parameter —
/// e.g., `lambda(r) -> by_country(r)` where `r` is iterated from
/// `Array<RegionIdx like StationIdx>` but `by_country` expects CountryIdx —
/// would silently typecheck.
///
/// Structural child enumerator for a typed expression: the immediate
/// sub-expressions of a node. Total over TExpr kinds. Shared by the tag-check
/// revalidation walk and the wildcard-escape scan so the two never drift.
let private typedExprChildren (expr: TypedExpr) : TypedExpr list =
        match expr.Kind with
        | TExprLit _ | TExprVar _ | TExprQualified _ | TExprSection _
        | TExprWildcard
        | TExprZero | TExprRange _ | TExprReverse _ | TExprArity _ -> []
        | TExprUnaryOp (_, e) -> [e]
        | TExprBinOp (_, _, l, r) -> [l; r]
        | TExprApp (f, args) -> f :: args
        | TExprTupleIndex (t, i) -> [t; i]
        | TExprField (e, _, _) -> [e]
        | TExprLambda info -> [info.Body]
        | TExprLet (_, _, v, b) -> [v; b]
        | TExprMatch (s, cases) ->
            s :: (cases |> List.collect (fun c ->
                c.Body :: (Option.toList c.Guard)))
        | TExprIf (c, t, e) -> [c; t; e]
        | TExprTuple es | TExprArrayLit (es, _) | TExprZip es | TExprStack es
        | TExprSequence es -> es
        | TExprComplexLit (re, im) -> [re; im]
        | TExprMethodFor info -> info.Arrays
        | TExprObjectFor info -> [info.Kernel]
        | TExprApply info -> info.Loop :: info.Kernel :: info.Arrays
        | TExprBind (a, b) | TExprParallel (a, b) | TExprFusion (a, b)
        | TExprChoice (a, b) -> [a; b]
        | TExprFallback (a, b) -> [a; b]
        | TExprFunctorMap (f, c) -> [f; c]
        | TExprCompose (_, l, r) -> [l; r]
        | TExprDotDot (lo, hi) -> [lo; hi]
        | TExprBlocked (_, bs) -> [bs]
        | TExprPure e | TExprCompute e | TExprRead e | TExprFillRandom e | TExprRank e
        | TExprExtents e | TExprReynolds (e, _) -> [e]
        | TExprRandGen (_, key, _) -> [key]
        | TExprGuard (c, b) -> [c; b]
        | TExprMask (a, p) | TExprIntersect (a, p) | TExprUnion (a, p)
        | TExprGroupBy (a, p) | TExprSort (a, p)
        | TExprCompound (a, p) -> [a; p]
        | TExprReduce (a, p, i) -> [a; p] @ Option.toList i
        | TExprProdSum args -> args
        | TExprUnique a -> [a]
        | TExprTranspose (a, _, _) -> [a]
        | TExprDecompact (a, _) -> [a]
        | TExprGram (l, r, _) -> [l; r]
        | TExprArrayNegate a -> [a]
        | TExprArrayConjugate a -> [a]
        | TExprContains (a, v) -> [a; v]
        | TExprGroupKeys keys -> keys
        | TExprStruct (_, fields) -> fields |> List.map snd
        | TExprIndex (arr, idxs, _) -> arr :: idxs
        | TExprBlock (stmts, final) ->
            let rec stmtExprsOf (s: TypedStmt) : TypedExpr list =
                match s with
                | TStmtLet b -> [b.Value]
                | TStmtAssign (l, r) -> [l; r]
                | TStmtExpr e -> [e]
                | TStmtForIn (_, _, lo, hi, body) ->
                    lo :: hi :: (body |> List.collect stmtExprsOf)
            (stmts |> List.collect stmtExprsOf) @ Option.toList final
        | TExprAssign (l, r) -> [l; r]
        | TExprConstraintCheck (c, _) -> [c]
        | TExprReplicate (c, b) -> [c; b]
        | TExprAlign (es, _) -> es
        | TExprPartialApp (_, a, _) -> [a]

/// True if the typed expression contains an unconsumed wildcard hole anywhere
/// in its subtree. A wildcard is legitimate only as a compound-index coordinate,
/// where dispatchAppOrIndex consumes it into a residual node before it reaches a
/// value-forming boundary. Any TExprWildcard still present here has escaped into
/// a value (bound to a name, returned, nested in a non-consuming call) and is an
/// error. Local check: called at value-forming boundaries, not threaded through
/// the AST.
let rec private exprContainsWildcard (expr: TypedExpr) : bool =
    match expr.Kind with
    | TExprWildcard -> true
    | _ -> typedExprChildren expr |> List.exists exprContainsWildcard

/// The walk is structural: visit every sub-expression, perform the tag
/// check at TExprIndex nodes, and short-circuit on the first error.
let rec private revalidateBodyTagChecks (env: TypeEnv) (expr: TypedExpr) : TypeResult<unit> =
    // Recurse into children first, short-circuiting on error.
    let childRes =
        typedExprChildren expr
        |> List.fold (fun acc child ->
            acc |> Result.bind (fun () -> revalidateBodyTagChecks env child))
            (Ok ())
    childRes |> Result.bind (fun () ->
        match expr.Kind with
        | TExprIndex (arr, args, _) ->
            match env.Subst.Resolve arr.Type with
            | ArrayElem at when args.Length <= at.IndexTypes.Length ->
                checkArrayIndexTags env at args
            | _ -> Ok ()
        | _ -> Ok ())

/// Scalar math intrinsics — the canonical list lives in Grad.fs (which also
/// carries the derivative rules); StaticEval.evalBuiltin mirrors the same
/// names for static contexts.
let isMathIntrinsic (name: string) : bool = Blade.Grad.isMathIntrinsic name

/// Whitelist subset permitted on complex operands (has a std::complex overload).
let isComplexMathIntrinsic (name: string) : bool = Blade.Grad.isComplexMathIntrinsic name

/// A variable is a provider-module alias when it is bound opaque to a
/// registered provider's module name (`import netcdf as nc` binds
/// nc : IRTNamed "netcdf"). Returns the registry name for dispatch.
let providerAliasName (env: TypeEnv) (alias: string) : string option =
    match lookupVar alias env with
    | Some vi ->
        (match env.Subst.Resolve(vi.Type) with
         | IRTNamed pn when (Blade.ProviderRegistry.tryFind pn).IsSome -> Some pn
         | _ -> None)
    | None -> None

/// Entry for every expression: stamps the ambient expression span (for
/// error location, see TypeEnv.locateError) and back-fills the source span
/// onto the typed node so TypedExpr.Span is live (full-span AST).
let rec inferExpr (env: TypeEnv) (expr: Expr) : TypeResult<TypedExpr> =
    if expr.Span.StartLine > 0 then setCurrentExprSpan expr.Span
    match inferExprInner env expr with
    | Ok te when te.Span.StartLine = 0 && expr.Span.StartLine > 0 ->
        Ok { te with Span = expr.Span }
    | r -> r

and inferExprInner (env: TypeEnv) (expr: Expr) : TypeResult<TypedExpr> =
    match expr.Kind with
    // ---- Literals ----
    | ExprKind.ExprLit lit -> Ok (mkTyped (TExprLit lit) (inferLiteralType lit))

    // ---- Wildcard hole ----
    // `_` in expression position. Not a value; carries a fresh hole type so it
    // passes through tuple inference (e.g. a free axis in a compound index
    // B((a, _, c))). The compound-index dispatch reads the wildcard positions;
    // any other context that reaches lowering with a TExprWildcard is an error.
    | ExprKind.ExprWildcard -> Ok (mkTyped TExprWildcard (env.Subst.Fresh()))

    // ---- Static former marker ----
    // Produced by the parser, eliminated by the Unfold pass before
    // typechecking; reaching here means the pipeline skipped unfolding.
    | ExprKind.ExprStatic _ ->
        Error (Other "internal: static former survived unfolding (the Unfold pass did not run)")

    // ---- Variables ----
    | ExprKind.ExprVar name ->
        match lookupVar name env with
        | Some info ->
            // If this variable has a polymorphic scheme, instantiate it
            // so each use site gets fresh type variables.
            let useTy =
                match info.Scheme with
                | Some scheme -> instantiate env.Subst scheme
                | None -> info.Type
            Ok (mkTyped (TExprVar (name, info.VarId, info.Identity)) useTy)
        | None ->
            // Check variant constructors
            match Map.tryFind name env.VariantTags with
            | Some (parentName, None) ->
                Ok (mkTyped (TExprVar (name, env.Builder.FreshId(), None)) (IRTNamed parentName))
            | Some (parentName, Some payloadTy) ->
                Ok (mkTyped (TExprVar (name, env.Builder.FreshId(), None))
                            (mkFuncArrow [payloadTy] (IRTNamed parentName)))
            | None -> Error (UnboundVariable name)

    | ExprKind.ExprQualified names ->
        // Qualified name resolution — limited for now
        Ok (mkTyped (TExprQualified names) IRTUnit)

    // ---- Binary operations (dispatch to helper) ----
    | ExprKind.ExprBinOp (mode, op, left, right) ->
        inferBinOp env mode op left right

    // ---- Unary operations ----
    | ExprKind.ExprUnaryOp (op, operand) ->
        inferUnaryOp env op operand
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, "load_compound") }, [varE; maskE])
        when (providerAliasName env alias).IsSome ->
        inferExpr env varE |> Result.bind (fun tVar ->
        inferExpr env maskE |> Result.bind (fun tMask ->
            match env.Subst.Resolve(tVar.Type), env.Subst.Resolve(tMask.Type) with
            | ArrayElem varArr, ArrayElem maskArr ->
                (match compoundViewType (env.Builder.FreshId()) varArr maskArr (IRLit IRLitUnit) with
                 | Ok compoundTy ->
                     let aliasVi = (lookupVar alias env).Value
                     let tAlias = mkTyped (TExprVar (alias, aliasVi.VarId, aliasVi.Identity)) aliasVi.Type
                     let tField = mkTyped (TExprField (tAlias, "load_compound", 0)) compoundTy
                     Ok (mkTyped (TExprApp (tField, [tVar; tMask])) compoundTy)
                 | Error msg -> Error (Other msg))
            | _ -> Error (Other "load_compound expects two array arguments: the variable and an integer mask")))

    // ---- Provider read: alias.read(view) / view |> alias.read ----
    // Both spellings arrive as this application (the pipe desugars to an
    // application). The result is the operand's type unchanged; the typed
    // node is TExprRead, which lowering's tryPlainRead/tryCompoundRead
    // intercept to record the deferred ProviderReadSpec.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, "read") }, [operand])
        when (providerAliasName env alias).IsSome ->
        inferExpr env operand |> Result.bind (fun tE ->
            Ok (mkTyped (TExprRead tE) tE.Type))

    // ---- Streamed read: alias.stream(view) / view |> alias.stream ----
    // Types exactly like a read (the array type is unchanged) but keeps the
    // generic application shape so lowering records a STREAMED spec: no
    // materialization at the binding; consuming loop nests inline per-fiber
    // store reads at the S/T boundary instead.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, "stream") }, [operand])
        when (providerAliasName env alias).IsSome ->
        inferExpr env operand |> Result.bind (fun tE ->
            match env.Subst.Resolve tE.Type with
            | ArrayElem _ ->
                let aliasVi = (lookupVar alias env).Value
                let tAlias = mkTyped (TExprVar (alias, aliasVi.VarId, aliasVi.Identity)) aliasVi.Type
                let tField = mkTyped (TExprField (tAlias, "stream", 0)) tE.Type
                Ok (mkTyped (TExprApp (tField, [tE])) tE.Type)
            | _ -> Error (ProviderStreamNeedsVar alias))

    // ---- Windowed packed read: alias.read_window(view, lo, hi) ----
    // Materializes only the cells with every coordinate in [lo, hi): a
    // translated sub-simplex, typed with leading packed extent hi-lo.
    // Bounds are integer literals (the window is a compile-time shape).
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, "read_window") }, args)
        when (providerAliasName env alias).IsSome ->
        (match args with
         | [operand; { Kind = ExprKind.ExprLit (LitInt lo) }; { Kind = ExprKind.ExprLit (LitInt hi) }] ->
             inferExpr env operand |> Result.bind (fun tE ->
                 match env.Subst.Resolve tE.Type with
                 | ArrayElem at ->
                     (match at.IndexTypes with
                      | lead :: rest when lead.Symmetry <> SymNone && lead.Rank >= 2 ->
                          (match lead.Extent with
                           | IRLit (IRLitInt n) when lo >= 0L && lo < hi && hi <= n ->
                               let winIdx = { lead with Id = env.Builder.FreshId()
                                                        Extent = IRLit (IRLitInt (hi - lo)) }
                               let winTy = mkArrayLike { at with IndexTypes = winIdx :: rest }
                               let aliasVi = (lookupVar alias env).Value
                               let tAlias = mkTyped (TExprVar (alias, aliasVi.VarId, aliasVi.Identity)) aliasVi.Type
                               let tField = mkTyped (TExprField (tAlias, "read_window", 0)) winTy
                               let tLo = mkTyped (TExprLit (LitInt lo)) (IRTScalar ETInt64)
                               let tHi = mkTyped (TExprLit (LitInt hi)) (IRTScalar ETInt64)
                               Ok (mkTyped (TExprApp (tField, [tE; tLo; tHi])) winTy)
                           | IRLit (IRLitInt n) ->
                               Error (ProviderReadWindowBounds (alias, lo, hi, n))
                           | _ ->
                               Error (ProviderReadWindowLiteralExtent alias))
                      | _ ->
                          Error (ProviderReadWindowPacked alias))
                 | _ -> Error (ProviderReadWindowNeedsVar alias))
         | _ -> Error (ProviderReadWindowArgs alias))

    // ---- Provider write: alias.write("path", A) ----
    // The source must be a named array binding (the store variable takes
    // its name); the path must be a string literal (the store is created
    // at a compile-time-known location, mirroring alias.load). Types as
    // unit; the generic application node is kept — lowering's
    // tryProviderWrite intercepts the shape into a ProviderWriteSpec.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, "write") }, args)
        when (providerAliasName env alias).IsSome ->
        (match args with
         | [{ Kind = ExprKind.ExprLit (LitString path) }; valueE] ->
             (match valueE.Kind with
              | ExprKind.ExprVar _ ->
                  inferExpr env valueE |> Result.bind (fun tValue ->
                      match env.Subst.Resolve(tValue.Type) with
                      | ArrayElem _ ->
                          let aliasVi = (lookupVar alias env).Value
                          let tAlias = mkTyped (TExprVar (alias, aliasVi.VarId, aliasVi.Identity)) aliasVi.Type
                          let tField = mkTyped (TExprField (tAlias, "write", 0)) IRTUnit
                          let tPath = mkTyped (TExprLit (LitString path)) (IRTScalar ETString)
                          Ok (mkTyped (TExprApp (tField, [tPath; tValue])) IRTUnit)
                      | _ -> Error (ProviderWriteNeedsArray alias))
              | _ -> Error (ProviderWriteNamedBinding alias))
         | _ -> Error (ProviderWriteArgs alias))

    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar n }, field) }, args)
        when (lookupVar (sprintf "%s.%s" n field) env).IsSome ->
        let qualName = sprintf "%s.%s" n field
        let info = (lookupVar qualName env).Value
        let useTy =
            match info.Scheme with
            | Some scheme -> instantiate env.Subst scheme
            | None -> info.Type
        let tFunc = mkTyped (TExprVar (qualName, info.VarId, info.Identity)) useTy
        args |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tArgs ->
            let retTy =
                match useTy with
                | FuncElem (_, ret) -> ret
                | _ -> env.Subst.Fresh()
            Ok (mkTyped (TExprApp (tFunc, tArgs)) retTy))

    // ---- Method call: obj.method(args) → impl resolution ----
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField (obj, method) }, args) ->
        inferMethodCall env obj method args
    // ---- Scalar math intrinsics: exp(x), sqrt(x), ... ----
    // Surface form is a plain call; the name is rewritten to OpMath only
    // when it is NOT user-bound (a user `function exp(...)` or a local
    // binding named `exp` shadows the intrinsic). Scalar-only: mapping over
    // an array is a kernel's job, so an array operand is a type error with
    // steering. Result is Float64 (Float32 operands widen; Int operands are
    // promoted by the C++ overload set).
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar name }, [arg]) when isMathIntrinsic name && (lookupVar name env).IsNone ->
        inferExpr env arg |> Result.bind (fun tArg ->
            match env.Subst.Resolve tArg.Type with
            | ArrayElem _ ->
                Error (IntrinsicScalarOnly name)
            | IRTScalar (ETComplex64 | ETComplex128) ->
                // exp/log/sqrt and the trig/hyperbolic families have std::complex
                // overloads and preserve the complex type; floor/ceil have no
                // complex overload and stay rejected.
                if isComplexMathIntrinsic name then
                    Ok (mkTyped (TExprUnaryOp (OpMath name, tArg)) tArg.Type)
                else
                    Error (IntrinsicNotComplex name)
            | IRTScalar ETBool | IRTScalar ETString ->
                Error (IntrinsicNeedsNumeric name)
            | IRTInfer _ when isComplexMathIntrinsic name ->
                // Unresolved operand (e.g. an unannotated kernel/lambda
                // parameter): DEFER — the apply-site unification may later
                // bind it to a COMPLEX element type (exp/log/sqrt/trig
                // preserve complex), so pinning Float64 here would reject
                // complex kernels. The node carries the operand's type
                // variable; the kernel re-stamp in buildApplyInfo rewrites it
                // to the resolved result type (complex-preserving, else
                // Float64).
                Ok (mkTyped (TExprUnaryOp (OpMath name, tArg)) tArg.Type)
            | IRTInfer _ ->
                // floor/ceil have no complex overload — the operand really is
                // real; pin it to Float64, the intrinsic's natural domain.
                unify env.Subst tArg.Type (IRTScalar ETFloat64) |> Result.bind (fun () ->
                Ok (mkTyped (TExprUnaryOp (OpMath name, tArg)) (IRTScalar ETFloat64)))
            | resolvedArg ->
                // Unit propagation at scalar position, same table as the
                // kernel-body walk (unitRulesForUnaryOp): sqrt halves
                // all-even exponents, floor/ceil preserve, transcendentals
                // are dimensionless-out.
                let resTy =
                    match unitRulesForUnaryOp (OpMath name) (IR.getUnits resolvedArg) with
                    | Some u -> IRTUnitAnnotated (IRTScalar ETFloat64, u)
                    | None -> IRTScalar ETFloat64
                Ok (mkTyped (TExprUnaryOp (OpMath name, tArg)) resTy))

    // ---- abs(x): polymorphic numeric intrinsic ----
    // Deliberately NOT in mathIntrinsics (those are real-valued, typed
    // Float64, and carry derivative rules); abs preserves its operand's
    // numeric type and renders as std::abs, whose C++ overload set covers
    // int64 and double. Ubiquitous in dependent field bounds
    // (`in abs(l1 - l2) .. l1 + l2 + 1`).
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "abs" }, [arg]) when (lookupVar "abs" env).IsNone ->
        inferExpr env arg |> Result.bind (fun tArg ->
            match env.Subst.Resolve tArg.Type with
            | IRTScalar (ETInt32 | ETInt64 | ETFloat32 | ETFloat64) as sc ->
                Ok (mkTyped (TExprUnaryOp (OpMath "abs", tArg)) sc)
            | IRTScalar (ETComplex64 | ETComplex128) ->
                // abs of a complex is its real magnitude (std::abs(complex<T>)
                // returns T); type the result Float64 (IRMath "abs" reports
                // Float64 at the IR level, which is correct for both widths).
                Ok (mkTyped (TExprUnaryOp (OpMath "abs", tArg)) (IRTScalar ETFloat64))
            | IRTInfer _ ->
                Ok (mkTyped (TExprUnaryOp (OpMath "abs", tArg)) tArg.Type)
            | IRTUnitAnnotated (IRTScalar (ETInt32 | ETInt64 | ETFloat32 | ETFloat64), _) as t ->
                // abs preserves the operand's unit annotation (it previously
                // fell through to the error arm — a unitful scalar is a
                // perfectly good abs operand).
                Ok (mkTyped (TExprUnaryOp (OpMath "abs", tArg)) t)
            | other ->
                Error (AbsNeedsNumericScalar (ppIRType other)))

    // ---- real(z) / imag(z) / arg(z): complex component/phase accessors ----
    // Plain-call intrinsics (shadowable by a user binding, like abs). Require a
    // complex scalar operand — real/imag on a real value is trivially the
    // identity/zero and a likely mistake, so we steer instead. real/imag yield
    // the component width (Complex128 -> Float64, Complex64 -> Float32); arg is
    // a Float64 angle. Emit std::real/std::imag/std::arg via the generic unary
    // codegen arm.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar (("real" | "imag" | "arg") as name) }, [arg]) when (lookupVar name env).IsNone ->
        inferExpr env arg |> Result.bind (fun tArg ->
            let op = match name with
                     | "real" -> OpReal
                     | "imag" -> OpImag
                     | _ -> OpArg
            match env.Subst.Resolve tArg.Type with
            | IRTScalar ETComplex64 ->
                let resTy = if name = "arg" then IRTScalar ETFloat64 else IRTScalar ETFloat32
                Ok (mkTyped (TExprUnaryOp (op, tArg)) resTy)
            | IRTScalar ETComplex128 ->
                Ok (mkTyped (TExprUnaryOp (op, tArg)) (IRTScalar ETFloat64))
            | IRTInfer _ ->
                // Unresolved operand (unannotated kernel/lambda parameter):
                // DEFER the complex requirement — the apply-site unification
                // binds the param to the iterated element type. Result is
                // provisionally Float64 (the Complex128-operand answer); the
                // kernel re-stamp corrects the Complex64 width (-> Float32
                // components).
                Ok (mkTyped (TExprUnaryOp (op, tArg)) (IRTScalar ETFloat64))
            | ArrayElem _ ->
                Error (IntrinsicComplexScalarOnly name)
            | other ->
                Error (IntrinsicNeedsComplex (name, ppIRType other)))

    // ---- complex(re, im): complex literal constructor ----
    // The one way to construct a complex value (the earlier 2-tuple cast
    // form `(re, im) : Complex128` is retired — as a plain call this
    // composes under any operator without the precedence trap where
    // `a * (re, im) : T` bound the cast outside the multiply). Plain-call
    // intrinsic, shadowable like abs/real. Components must be float-typed
    // (no implicit int -> float promotion at construction time, same rule
    // as the retired form). Infers Complex128; checking against an
    // expected Complex64 adopts the narrow width (checkExpr arm).
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "complex" }, [reExpr; imExpr]) when (lookupVar "complex" env).IsNone ->
        checkExpr env (IRTScalar ETFloat64) reExpr |> Result.bind (fun tRe ->
        checkExpr env (IRTScalar ETFloat64) imExpr |> Result.map (fun tIm ->
            mkTyped (TExprComplexLit (tRe, tIm)) (IRTScalar ETComplex128)))
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "complex" }, args) when (lookupVar "complex" env).IsNone ->
        Error (ComplexArity args.Length)

    // ---- prodsum(x1, ..., xk): fused fiber product-sum ----
    // Σ_t Π_ℓ xℓ(t) over rank-1 arrays of equal extent — the k-fold
    // generalization of a dot product, and the comoment primitive the PPL
    // moment formers elaborate to. Surface form is a plain call,
    // shadowable like the math intrinsics. Empty extent folds to 0 (sum
    // identity), so no non-empty check.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "prodsum" }, args) when not args.IsEmpty && (lookupVar "prodsum" env).IsNone ->
        inferProdSum env args

    // ---- __dist_pack(κ1, ..., κr): typed-dist construction intrinsic ----
    // Compiler-internal (double-underscore reserved): emitted by the PPL
    // elaboration stage after it builds the fused cumulant tower, never
    // written by users. Packs the component arrays into a value of nominal
    // type Dist<r, τ like axes>; the typed node is a plain TExprTuple (the
    // representation a Dist erases to at zonk), only the TYPE is nominal.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "__dist_pack" }, args) when not args.IsEmpty ->
        inferDistPack env args

    // ---- __rand_uniform / __rand_normal(key, d1, ..., dn): rand module ----
    // Compiler-internal (double-underscore reserved): emitted by the `rand`
    // elaboration stage from `alias.uniform/normal(key, shape)`. `key` is an
    // Int64 stream key; the trailing args are the (elaborator-resolved) static
    // extents. Self-typed as a dense Float64 array of that shape — no annotation
    // needed. Lowering records (kind, key) in RandomInits; codegen emits
    // allocate<> + the runtime blade_rand fill.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar (("__rand_uniform" | "__rand_normal") as fn) }, (keyE :: dimArgs)) when not dimArgs.IsEmpty ->
        let kind = if fn = "__rand_uniform" then "uniform" else "normal"
        // Extents must be static ints (the elaborator resolves them to literals).
        let dimResults =
            dimArgs |> List.map (fun d ->
                match d.Kind with
                | ExprKind.ExprLit (LitInt n) when n > 0L -> Ok (int n)
                | ExprKind.ExprLit (LitInt n) -> Error (sprintf "rand.%s: shape extents must be positive (got %d)" kind n)
                | _ -> Error (sprintf "rand.%s: shape must be a static positive int (or list of them)" kind))
        match dimResults |> List.fold (fun acc r -> match acc, r with Ok xs, Ok x -> Ok (xs @ [x]) | Error e, _ -> Error e | _, Error e -> Error e) (Ok []) with
        | Error e -> Error (Other e)
        | Ok dims ->
            checkExpr env (IRTScalar ETInt64) keyE |> Result.map (fun tKey ->
                let indices =
                    dims |> List.map (fun n ->
                        { Id = env.Builder.FreshId(); Rank = 1; Extent = IRLit (IRLitInt (int64 n))
                          Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] })
                let arrTy = mkArrayArrow indices (IRTScalar ETFloat64) None
                mkTyped (TExprRandGen (kind, tKey, dims)) arrTy)

    // ---- cumulant(d, k): dist component projection, order-guarded ----
    // The order guard as a TYPE error (ppl/NOTES.md typed-Dist arc): k must
    // be a static int in 1..r where r is the dist's carried order. Works in
    // any expression position on any Dist-typed value — including function
    // parameters, which the elaboration-level registry could never see.
    // `cumulant` is part of the `ppl` module surface: the ppl elaborator
    // rewrites a qualified `p.cumulant(d, k)` to this internal marker, so a
    // bare `cumulant(...)` no longer resolves (import-gated, not language-wide).
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "__ppl_cumulant" }, [dExpr; kExpr]) when (lookupVar "__ppl_cumulant" env).IsNone ->
        inferCumulantProj env dExpr kExpr

    | ExprKind.ExprApp (func, args) ->
        inferExpr env func |> Result.bind (fun tFunc ->
        // Prefix partial application (formalism 6.2.3): applying an n-ary
        // FUNCTION to 0 < k < n args eta-expands to a lambda over the
        // residual params — lambda(__pa..) -> f(a1..ak, __pa..) — so the
        // residual value rides the entire existing lambda pipeline
        // (inferLambda captures, lowerTypedLambda lifting, std::function
        // value emission, resolveCallable kernel wrappers). The FuncElem
        // guard keeps arrays on their own dimensional-currying path below.
        // Bound args are inlined into the lambda body (each appears exactly
        // once), so they re-evaluate per call of the residual and their
        // free locals become ordinary lambda captures — the same semantics
        // as a user-written lambda. `func` is inferred a second time inside
        // the body; inference of an application head is pure, so the
        // discarded detection pass above costs nothing.
        // A Poly<T^r> pack param makes the arrow variadic: its declared
        // param count says nothing about legal call-site arg counts, so
        // both the placeholder desugar and the under-application
        // eta-expansion must stand down (monomorphization owns those calls).
        let hasPolyParam (paramTys: IRType list) =
            paramTys |> List.exists (fun t ->
                match env.Subst.Resolve t with IRTPoly _ -> true | _ -> false)
        match env.Subst.Resolve tFunc.Type with
        // Single `_` placeholder (formalism 6.2.3): one hole in an
        // otherwise-full application binds every other parameter and
        // leaves the hole's parameter free — f(_, b) ≡ lambda(x) -> f(x, b).
        // FuncElem-gated: array wildcard-indexing (the 4.5 currying table,
        // where MULTIPLE holes are legal) stays on the ArrayElem path.
        | FuncElem (paramTys, retTy) when not (hasPolyParam paramTys) && args |> List.exists (fun a -> match a.Kind with ExprKind.ExprWildcard -> true | _ -> false) ->
            let wildPositions =
                args |> List.mapi (fun i a -> (i, a))
                     |> List.choose (fun (i, a) -> match a.Kind with ExprKind.ExprWildcard -> Some i | _ -> None)
            if wildPositions.Length > 1 then
                Error (Other "function partial application takes a single `_` placeholder only (formalism 6.2.3) — bind the rest with prefix partial application or a lambda")
            elif args.Length <> paramTys.Length then
                Error (PlaceholderNeedsAllBound (args.Length, paramTys.Length))
            else
                let wildPos = wildPositions.Head
                let uid = env.Builder.FreshId()
                let name = sprintf "__pa%d_w" uid
                let newArgs = args |> List.mapi (fun i a -> if i = wildPos then inheritSpan a (ExprVar name) else a)
                inferLambda env [{ Name = name; Type = None }] None (inheritSpan func (ExprApp (func, newArgs)))
                |> Result.bind (fun tLam ->
                    unify env.Subst tLam.Type (mkFuncArrow [paramTys.[wildPos]] retTy)
                    |> Result.map (fun () -> tLam))
        | FuncElem (paramTys, retTy) when not (hasPolyParam paramTys) && not args.IsEmpty && args.Length < paramTys.Length ->
            let residual = paramTys |> List.skip args.Length
            let uid = env.Builder.FreshId()
            let names = residual |> List.mapi (fun i _ -> sprintf "__pa%d_%d" uid i)
            let lamParams = names |> List.map (fun n -> { Name = n; Type = None } : LambdaParam)
            let bodyApp = inheritSpan func (ExprApp (func, args @ (names |> List.map (fun n -> inheritSpan func (ExprVar n)))))
            inferLambda env lamParams None bodyApp
            |> Result.bind (fun tLam ->
                // Pin the residual param types to the callee's declared
                // ones: direct application keeps its historical looseness
                // (no param-vs-arg unification), so nothing else would
                // resolve the lambda's fresh param inference vars.
                unify env.Subst tLam.Type (mkFuncArrow residual retTy)
                |> Result.map (fun () -> tLam))
        | _ ->
            args |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tArgs ->
                // Call-site constraint DISCHARGE: if the callee declared custom
                // where-clause conjuncts (e.g. PPL's `indep(a, b)`), the caller
                // must prove them for the actual arguments — each registered
                // handler gets the callee's conjunct args plus a provenance
                // oracle mapping callee param names to the actuals' provenance.
                let dischargeErr =
                    match func.Kind with
                    | ExprKind.ExprVar fname ->
                        match env.FuncConstraints.TryGetValue fname with
                        | true, (paramNames, conjuncts) ->
                            let provOf (pname: string) : Set<string> =
                                match List.tryFindIndex ((=) pname) paramNames with
                                | Some i when i < args.Length -> provenanceOfSurface env args.[i]
                                | _ -> Set.empty
                            conjuncts |> List.tryPick (fun (cname, cargs) ->
                                Blade.Constraints.lookupConstraint cname
                                |> Option.bind (fun h ->
                                    match h.Discharge fname cargs provOf with
                                    | Ok () -> None
                                    | Error msg -> Some msg))
                        | _ -> None
                    | _ -> None
                match dischargeErr with
                | Some msg -> Error (Other msg)
                | None -> dispatchAppOrIndex env tFunc tArgs))

    // ---- Poly-tuple indexing OR array indexing (brackets) ----
    // `e[i]` is parsed as ExprTupleIndex regardless of e's type. Disambiguate
    // here: if e resolves to an IRTArray, this is conventional array
    // indexing and we route to TExprIndex (matching the function-call form
    // `e(i)` which goes through ExprApp's array branch at line 1518). If e
    // is a poly-pack (any other shape), keep TExprTupleIndex for IRPolyIndex
    // codegen.
    //
    // Pre-fix: arrays of named types (Phase D) would always render as
    // `std::get<n>(arr)` since no test before Phase D exercised bracket-
    // indexed reads on a real array.
    | ExprKind.ExprTupleIndex (tuple, index) ->
        inferTupleIndex env tuple index
    | ExprKind.ExprField ({ Kind = ExprKind.ExprVar n }, field)
        when (lookupVar (sprintf "%s.%s" n field) env).IsSome ->
        // Module-qualified value access (e.g. `let tau = Math.pi * 2.0`).
        // Same rationale as the qualified application case above.
        let qualName = sprintf "%s.%s" n field
        let info = (lookupVar qualName env).Value
        let useTy =
            match info.Scheme with
            | Some scheme -> instantiate env.Subst scheme
            | None -> info.Type
        Ok (mkTyped (TExprVar (qualName, info.VarId, info.Identity)) useTy)

    | ExprKind.ExprField (obj, field) ->
        inferExpr env obj |> Result.bind (fun tObj ->
            let (fieldTy, fieldIdx) =
                match tObj.Type with
                | IRTNamed typeName ->
                    match lookupTypeDef typeName env with
                    | Some (TDIStruct (_, _, fields, _)) ->
                        let idx = fields |> List.tryFindIndex (fun (n, _) -> n = field)
                        let ty = fields |> List.tryFind (fun (n, _) -> n = field) |> Option.map snd
                        (ty |> Option.defaultValue (env.Subst.Fresh()),
                         idx |> Option.defaultValue 0)
                    | _ -> (env.Subst.Fresh(), 0)
                | _ -> (env.Subst.Fresh(), 0)
            Ok (mkTyped (TExprField (tObj, field, fieldIdx)) fieldTy))

    // ---- Lambda ----
    | ExprKind.ExprLambda (parms, whereClause, body) -> inferLambda env parms whereClause body

    // ---- Let ----
    | ExprKind.ExprLet (binding, body) -> inferLetBinding env binding body

    // ---- Match ----
    | ExprKind.ExprMatch (scrutinee, cases) -> inferMatch env scrutinee cases

    // ---- If-then-else ----
    | ExprKind.ExprIf (cond, thenBr, elseBr) ->
        inferExpr env cond |> Result.bind (fun tCond ->
        inferExpr env thenBr |> Result.bind (fun tThen ->
        inferExpr env elseBr |> Result.bind (fun tElse ->
            let _ = unify env.Subst tThen.Type tElse.Type
            Ok (mkTyped (TExprIf (tCond, tThen, tElse)) tThen.Type))))

    // ---- Tuple ----
    | ExprKind.ExprTuple exprs ->
        exprs |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tExprs ->
            Ok (mkTyped (TExprTuple tExprs) (IRTTuple (tExprs |> List.map (fun e -> e.Type)))))

    // ---- Array literal ----
    | ExprKind.ExprArrayLit elems ->
        elems |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tElems ->
            let arrTy = inferArrayLitType env.Builder tElems
            Ok (mkTyped (TExprArrayLit (tElems, arrTy)) (mkArrayLike arrTy)))

    // ---- Block ----
    | ExprKind.ExprBlock (stmts, finalExpr) -> inferBlock env stmts finalExpr

    // ---- Loop constructs ----
    | ExprKind.ExprMethodFor arrays -> inferMethodFor env arrays
    | ExprKind.ExprObjectFor kernel -> inferObjectFor env kernel

    // ---- Virtual arrays ----
    // Iteration-tagging: when the source index type carries a user-named tag
    // (e.g., `range<LatIdx>`), the element type is wrapped as Nat<LatIdx>
    // rather than bare int64. Iterating the virtual array via method_for
    // then yields tagged values to the kernel — so `range<LatIdx> <@>
    // lambda(i) -> A(i)` (where A is `Array<T like LatIdx>`) typechecks
    // cleanly under step 5's tag rule without an annotation on i.
    // Anonymous index types (`Idx<5>`, etc., Tag=None) and synthetic tags
    // (prefixed "__") keep the bare int64 element type, matching gap 1's
    // asymmetric treatment of named vs anonymous element-position tags.
    | ExprKind.ExprRange idxTys ->
        // A TyHalo slot builds through haloSlotsOf (static-offset validation +
        // interior shrink + "__halowin|" tag) and may SPLICE several slots
        // (nested per-axis offsets); every other slot lowers as before. n-D
        // separable stencils are ranges of halo slots — one window per slot.
        (idxTys
         |> List.map (fun ty ->
             match ty with
             | TyHalo (innerTy, offsetsExpr) -> haloSlotsOf env innerTy offsetsExpr
             | _ -> Ok [lowerIndexType env 0 ty])
         |> sequenceResults)
        |> Result.map List.concat
        |> Result.bind (fun idxs ->
        // A CompoundIdx slot (masked product space, formalism 4.5) IS the whole
        // iteration space, so it cannot share a range<> with other index types.
        // Reject range<CompoundIdx<m>, J> HERE at typecheck (EXPECT: typecheck
        // failure) rather than letting it fall through and leak a codegen #error
        // plus a cascade of undeclared-variable errors into the generated C++.
        // A SOLE compound slot (idxs.Length = 1) is fine and passes through.
        let hasCompound =
            idxs |> List.exists (fun ix -> (match ix.Extent with IRCompoundMask _ -> true | _ -> false))
        if hasCompound && idxs.Length > 1 then
            Error (Other "range<CompoundIdx<m>, ...>: a compound range slot cannot be combined with other index types in one range<> (formalism 4.5)")
        else
        // Each listed index type becomes one virtual slot; downstream the slots
        // uncurry into nested loop levels. The element type is taken from the
        // innermost (last) index -- the value yielded at the deepest level --
        // which preserves single-index behavior (one slot -> that slot's tagged
        // element type).
        let elemType =
            match List.tryLast idxs with
            | Some i -> elemTypeForIterationIndex i
            | None -> IRTScalar ETInt64
        Ok (mkTyped (TExprRange idxs) (mkVirtualArrayArrow idxs elemType)))
    | ExprKind.ExprDotDot (lo, hi) ->
        inferExpr env lo |> Result.bind (fun tLo ->
        inferExpr env hi |> Result.bind (fun tHi ->
            // Compute extent from literals when possible
            let extentExpr =
                match tLo.Kind, tHi.Kind with
                | TExprLit (LitInt 0L), TExprLit (LitInt n) -> IRLit (IRLitInt n)
                | TExprLit (LitInt a), TExprLit (LitInt b) -> IRLit (IRLitInt (b - a))
                | _ -> IRLit (IRLitInt 0L)  // placeholder — Lowering computes actual extent
            let idx = {
                Id = env.Builder.FreshId()
                Rank = 1
                Extent = extentExpr
                Symmetry = SymNone
                Tag = Some "__anon"; IxKind = IxKPlain
                Kind = SDimension
                Dependencies = []
            }
            // ExprDotDot has no index-type annotation, so element type
            // stays bare int64 (no name to tag with).
            Ok (mkTyped (TExprDotDot (tLo, tHi)) (mkVirtualArrayArrow [idx] (IRTScalar ETInt64)))))
    | ExprKind.ExprReverse idxTy ->
        let idx = lowerIndexType env 0 idxTy
        let elemType = elemTypeForIterationIndex idx
        Ok (mkTyped (TExprReverse idx) (mkVirtualArrayArrow [idx] elemType))
    | ExprKind.ExprBlocked (idxTy, blockSize) ->
        let idx = lowerIndexType env 0 idxTy
        inferExpr env blockSize |> Result.bind (fun tBS ->
            let elemType = elemTypeForIterationIndex idx
            Ok (mkTyped (TExprBlocked (idx, tBS)) (mkVirtualArrayArrow [idx] elemType)))

    | ExprKind.ExprHalo (innerTy, offsetsExpr) ->
        // halo<Inner, offsets> in expression position — a range over the halo
        // slot(s): one slot for a flat offset array, k slots for the nested
        // per-axis form [[..],[..],..] (arity = sub-array count). All halo
        // semantics live in the slots (haloSlotsOf); per-slot center offsets
        // are re-derived from the tags at loop building.
        haloSlotsOf env innerTy offsetsExpr
        |> Result.map (fun slots ->
            mkTyped (TExprRange slots) (mkVirtualArrayArrow slots (elemTypeForIterationIndex (List.last slots))))

    // ---- Zip / Stack ----
    | ExprKind.ExprZip exprs ->
        inferZip env exprs
    | ExprKind.ExprStack exprs ->
        exprs |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tExprs ->
            let elemTy = if tExprs.IsEmpty then IRTUnit else tExprs.[0].Type
            Ok (mkTyped (TExprStack tExprs) elemTy))

    // ---- Computation combinators ----
    | ExprKind.ExprPure e ->
        inferExpr env e |> Result.bind (fun tE ->
            Ok (mkTyped (TExprPure tE) (IRTComputation tE.Type)))
    | ExprKind.ExprCompute e ->
        inferExpr env e |> Result.bind (fun tE ->
            let inner = match tE.Type with IRTComputation t -> t | t -> t
            Ok (mkTyped (TExprCompute tE) inner))
    | ExprKind.ExprRead e ->
        // |> read forces a deferred provider read; the result is the operand's
        // (possibly view-modified) array, so the type passes through unchanged.
        inferExpr env e |> Result.bind (fun tE ->
            Ok (mkTyped (TExprRead tE) tE.Type))
    | ExprKind.ExprGuard (cond, body) ->
        inferExpr env cond |> Result.bind (fun tC ->
        inferExpr env body |> Result.bind (fun tB ->
            Ok (mkTyped (TExprGuard (tC, tB)) tB.Type)))
    
    // mask(array, pred) — construct the Bool PRESENCE array over the source's
    // own index space: m(i) = pred(A(i)). mask is the predicate-driven mask
    // CONSTRUCTOR; compaction is compound(A, m) (formalism 4.5), iteration of
    // the filtered space is range<CompoundIdx<m>>, and positional composition
    // (WHERE p AND q) is elementwise Bool algebra on mask arrays. This
    // replaces the earlier value-filtering semantics (which returned the
    // packed values, discarding positions and making the selection
    // non-reusable across companion columns).
    //
    // The result type reuses the source's IRIndexType records VERBATIM (same
    // Ids/Tags) — index-space identity is what compoundViewType checks, so a
    // freshly-derived mask must provably live over A's space even when A's
    // indices are anonymous.
    | ExprKind.ExprMask (array, pred) ->
        inferMask env array pred
    | ExprKind.ExprCompound (dense, mask) ->
        inferExpr env dense |> Result.bind (fun tDense ->
        inferExpr env mask |> Result.bind (fun tMask ->
            match env.Subst.Resolve(tDense.Type), env.Subst.Resolve(tMask.Type) with
            | ArrayElem denseArr, ArrayElem maskArr ->
                (match compoundViewType (env.Builder.FreshId()) denseArr maskArr (IRLit IRLitUnit) with
                 | Ok compoundTy ->
                     Ok (mkTyped (TExprCompound (tDense, tMask)) compoundTy)
                 | Error msg -> Error (Other msg))
            | _ -> Error (Other "compound(dense, mask) expects two array arguments: a dense array and a bool mask covering its leading dimensions")))

    // intersect(A, B) / union(A, B) — set operations on arrays
    | ExprKind.ExprIntersect (a, b) | ExprKind.ExprUnion (a, b) ->
        let isIntersect = match expr.Kind with ExprKind.ExprIntersect _ -> true | _ -> false
        let opName = if isIntersect then "intersect" else "union"
        inferExpr env a |> Result.bind (fun tA ->
        inferExpr env b |> Result.bind (fun tB ->
            requireArrayArg env tA opName |> Result.bind (fun arrTy ->
                // Drive inference for the second array too — both should be
                // arrays of compatible elem type.
                requireArrayArg env tB opName |> Result.bind (fun _arrTyB ->
                    let resultIdx = {
                        Id = env.Builder.FreshId(); Rank = 1
                        Extent = IRParam ((if isIntersect then "__isect" else "__union"), 0, IRTNat None)
                        Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                        Kind = SDimension; Dependencies = []
                    }
                    let resultType = mkArrayArrow [resultIdx] arrTy.ElemType None
                    let texpr = if isIntersect then TExprIntersect (tA, tB) else TExprUnion (tA, tB)
                    Ok (mkTyped texpr resultType)))))

    // unique(A) — deduplicate, preserving first-occurrence order. Same
    // element type as input, dynamic extent (≤ input extent).
    | ExprKind.ExprUnique a ->
        inferExpr env a |> Result.bind (fun tA ->
            requireArrayArg env tA "unique" |> Result.bind (fun arrTy ->
                let resultIdx = {
                    Id = env.Builder.FreshId(); Rank = 1
                    Extent = IRParam ("__unique", 0, IRTNat None)
                    Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                    Kind = SDimension; Dependencies = []
                }
                let resultType = mkArrayArrow [resultIdx] arrTy.ElemType None
                Ok (mkTyped (TExprUnique tA) resultType)))

    // contains(A, x) — membership test. Returns Bool. The value's type
    // must unify with the array's element type; mismatch (e.g., looking
    // for a Float64 in an Int64 array) is a hard error.
    | ExprKind.ExprContains (a, value) ->
        inferExpr env a |> Result.bind (fun tA ->
        inferExpr env value |> Result.bind (fun tValue ->
            requireArrayArg env tA "contains" |> Result.bind (fun arrTy ->
                unify env.Subst tValue.Type arrTy.ElemType
                |> Result.bind (fun () ->
                    Ok (mkTyped (TExprContains (tA, tValue)) (IRTScalar ETBool))))))

    // group_keys(keys1, keys2, ...) — build CSR grouping structure.
    // Single key: existing single-keyed grouping (positional / EnumIdx /
    // dynamic-discovery cases).
    // Multi-key (≥2): compound grouping. Each (k1, k2, ...) tuple becomes
    // its own bucket. Discovery is dynamic regardless of any single key's
    // staticness — even if all components were Idx<N>, the compound shape
    // is determined by which tuples actually appear in the data.
    // Precondition: all key arrays share the same outer index (same length;
    // i-th element of each represents the same record).
    | ExprKind.ExprGroupKeys keys ->
        inferGroupKeys env keys
    | ExprKind.ExprGroupBy (values, grouping) ->
        inferGroupBy env values grouping
    | ExprKind.ExprSort (array, key) ->
        inferExpr env array |> Result.bind (fun tArr ->
        inferExpr env key |> Result.bind (fun tKey ->
            requireArrayArg env tArr "sort" |> Result.bind (fun arrTy ->
                if arrTy.IndexTypes.Length <> 1 then
                    Error (Other "sort() requires a rank-1 array (multi-rank sort not yet supported)")
                else
                    let srcIdx = arrTy.IndexTypes.[0]
                    // Fresh anonymous index, same extent as source. Sort doesn't
                    // change length, so the static extent (when known) propagates.
                    let resultIdx = {
                        Id = env.Builder.FreshId(); Rank = 1
                        Extent = srcIdx.Extent
                        Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                        Kind = SDimension; Dependencies = []
                    }
                    let resultType = mkArrayArrow [resultIdx] arrTy.ElemType None
                    Ok (mkTyped (TExprSort (tArr, tKey)) resultType))))

    | ExprKind.ExprTranspose (array, d1, d2) ->
        inferTranspose env array d1 d2
    | ExprKind.ExprGram (leftE, rightE) ->
        inferGram env leftE rightE
    | ExprKind.ExprDecompact (array, d) ->
        inferDecompact env array d
    | ExprKind.ExprReduce (array, kernel, init) ->
        inferReduce env array kernel init
    | ExprKind.ExprExtents array ->
        inferExtents env array
    | ExprKind.ExprSequence exprs ->
        inferSequence env exprs
    | ExprKind.ExprReplicate (count, body) ->
        inferReplicate env count body
    | ExprKind.ExprReynolds (kernel, isAntisym) ->
        inferExpr env kernel |> Result.bind (fun tK ->
            Ok (mkTyped (TExprReynolds (tK, isAntisym)) tK.Type))

    // ---- Type annotation ----
    | ExprKind.ExprTyped (e, tyAnno) ->
        // Route through bidirectional checkExpr so the annotation pushes
        // down into literal/constructor positions. The motivating case
        // is `complex(re, im) : Complex64`: the constructor checked
        // against a Complex width adopts it (and the retired 2-tuple
        // complex form gets its steering error there rather than a
        // generic unify failure). For non-special-cased shapes, checkExpr
        // falls through to inferExpr + unify, preserving plain-cast
        // behavior.
        let annoTy = lowerTypeExpr env tyAnno
        checkExpr env annoTy e |> Result.map (fun tE ->
            { tE with Type = annoTy })

    // ---- Arity special forms ----
    | ExprKind.ExprArity paramName -> Ok (mkTyped (TExprArity paramName) (IRTScalar ETInt64))
    | ExprKind.ExprNth -> Ok (mkTyped (TExprLit (LitInt 0L)) (IRTScalar ETInt64))
    | ExprKind.ExprZero ->
        // zero gets a fresh type variable — unifies with int, float, bool context
        let ty = env.Subst.Fresh()
        Ok (mkTyped TExprZero ty)
    | ExprKind.ExprRank e ->
        inferExpr env e |> Result.bind (fun tE ->
            Ok (mkTyped (TExprRank tE) (IRTScalar ETInt64)))

    // ---- Struct construction ----
    | ExprKind.ExprStruct (name, fields, spread) -> inferStructConstruction env name fields spread

    // ---- Sectioned operators ----
    | ExprKind.ExprSection op ->
        let paramTy = env.Subst.Fresh()
        Ok (mkTyped (TExprSection op) (mkFuncArrow [paramTy; paramTy] paramTy))
    | ExprKind.ExprPartialApp (op, arg, isLeft) ->
        inferExpr env arg |> Result.bind (fun tArg ->
            Ok (mkTyped (TExprPartialApp (op, tArg, isLeft))
                        (mkFuncArrow [tArg.Type] tArg.Type)))

    // ---- Assignment ----
    | ExprKind.ExprAssign (lhs, rhs) ->
        inferExpr env lhs |> Result.bind (fun tL ->
        // Bidirectional: check the RHS against the target's type so literals
        // adapt (Int64 literal into an Int32 field) as in every other
        // checked position.
        checkExpr env tL.Type rhs |> Result.bind (fun tR ->
            // Check assignability of LHS
            let assignErr =
                match tL.Kind with
                | TExprVar (name, _, _) ->
                    match lookupVar name env with
                    | Some info when info.Assign = ReadOnly ->
                        Some (ImmutableStaticAssign name)
                    | _ -> None
                | _ -> None  // array element assignment etc. — allowed
            match assignErr with
            | Some e -> Error e
            | None ->
                match unify env.Subst tL.Type tR.Type with
                | Ok () ->
                    let tAssign = mkTyped (TExprAssign (tL, tR)) IRTUnit
                    // Constrained-struct target: inline the guard after the
                    // store (whole-struct stores and field mutations alike).
                    structChecksForAssign env lhs tL |> Result.map (fun checks ->
                        if checks.IsEmpty then tAssign
                        else mkTyped (TExprBlock (TStmtExpr tAssign :: (checks |> List.map TStmtExpr), None)) IRTUnit)
                | Error _ -> Error (TypeMismatch (tL.Type, tR.Type))))

    // ---- For expression ----
    | ExprKind.ExprFor (source, _constraints, kernelOpt) ->
        inferForExpr env source kernelOpt

    // ---- Align ----
    | ExprKind.ExprAlign (exprs, spec) ->
        exprs |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tExprs ->
            let ty = if tExprs.IsEmpty then IRTUnit else tExprs.[0].Type
            Ok (mkTyped (TExprAlign (tExprs, spec)) ty))

// ============================================================================
// 10a. Binary Operation Inference
// ============================================================================


/// Detect and type the fused reduction terminal. Returns None when the
/// first argument is NOT a deferred computation (ordinary array reduce
/// proceeds). Some (Ok ...) types the fold: the child spliced into the
/// typed node is a CANONICAL left-nested fusion over the RESOLVED leaves
/// (one let-hop each, the <@> resolution rule), so lowering and codegen
/// never chase variable bindings. Result type: elem for a single apply,
/// left-nested scalar pairs for a tree (mirroring `|> compute`'s tuple
/// convention — nested pair TYPE, flat make_tuple VALUE, flat projections).
and tryInferReduceCompute (env: TypeEnv) (tArr: TypedExpr) (tKernel: TypedExpr) (tInitOpt: TypedExpr option) : TypeResult<TypedExpr> option =
    // Collect fusion leaves left-to-right, resolving each through bindings.
    // None = the root is not a deferred computation at all (fall through);
    // Some (Error _) = it IS deferred but malformed for a fused fold.
    let rec collect (t: TypedExpr) : Result<TypedExpr list, TypeError> option =
        let r = resolveTypedExpr env t
        match r.Kind with
        | TExprFusion (l, rgt) ->
            (match collect l, collect rgt with
             | Some (Ok ls), Some (Ok rs) -> Some (Ok (ls @ rs))
             | Some (Error e), _ | _, Some (Error e) -> Some (Error e)
             | _ ->
                Some (Error (Other "reduce() over a fused tree requires every <&!> leaf to be an unforced `method_for/object_for <@> kernel` application")))
        | TExprApply info when not info.IsComposeApply -> Some (Ok [r])
        | TExprApply _ ->
            Some (Error (Other "reduce() over a composed (>>@/@>>) application is not supported yet — force it with `|> compute` and reduce the resulting array"))
        | _ -> None
    match collect tArr with
    | None -> None
    | Some leavesR ->
        Some (
            leavesR |> Result.bind (fun leaves ->
            // Each leaf must produce plain (non-compact) cells: folding
            // canonical vs logical cells of symmetric storage differ, the
            // same ambiguity the array form rejects.
            let leafElem (leaf: TypedExpr) : Result<IRType, TypeError> =
                match leaf.Kind with
                | TExprApply info ->
                    (match env.Subst.Resolve info.OutputType with
                     | ArrayElem arr ->
                        let packed =
                            arr.IndexTypes |> List.exists (fun ix ->
                                match ix.Symmetry with SymNone -> false | _ -> true)
                        if packed then
                            Error (Other "reduce() over a computation with compact symmetric/antisymmetric/Hermitian output is not supported: folding the canonical cells and folding the logical (mirrored) cells differ. Force with `|> compute` and decompact(A, d) first for the logical fold.")
                        else Ok arr.ElemType
                     | IRTScalar _ as s -> Ok s
                     | _ -> Error (Other "reduce() over a deferred computation needs an array-producing kernel application"))
                | _ -> Error (Other "reduce(): internal — fusion leaf is not an apply")
            leaves |> List.map leafElem |> sequenceResults |> Result.bind (fun elems ->
            let elem0 = elems.Head
            elems.Tail
            |> List.fold (fun acc e -> acc |> Result.bind (fun () -> unify env.Subst e elem0)) (Ok ())
            |> Result.bind (fun () ->
            // Fold-kernel params and init share the leaves' element type
            // (same unification the array form performs).
            (match env.Subst.Resolve(tKernel.Type) with
             | FuncElem (paramTys, _) ->
                paramTys |> List.fold (fun acc pTy ->
                    acc |> Result.bind (fun () -> unify env.Subst pTy elem0)) (Ok ())
             | _ -> Ok ())
            |> Result.bind (fun () ->
            (match tInitOpt with
             | Some tInit -> unify env.Subst tInit.Type elem0
             | None -> Ok ())
            |> Result.bind (fun () ->
            // Seed: user's init, else the section's identity. A fused nest
            // cannot seed-with-first (no single first cell across a
            // multi-dim or multi-leaf nest), so everything else is an error.
            let seed : Result<TypedExpr, TypeError> =
                match tInitOpt with
                | Some tInit -> Ok tInit
                | None ->
                    let et = match env.Subst.Resolve elem0 with AnyPrimElem e -> e | _ -> ETFloat64
                    (match tKernel.Kind with
                     | TExprSection OpAdd ->
                        let lit = match et with ETInt32 | ETInt64 -> TExprLit (LitInt 0L) | _ -> TExprLit (LitFloat 0.0)
                        Ok (mkTyped lit elem0)
                     | TExprSection OpMul ->
                        let lit = match et with ETInt32 | ETInt64 -> TExprLit (LitInt 1L) | _ -> TExprLit (LitFloat 1.0)
                        Ok (mkTyped lit elem0)
                     | TExprSection _ ->
                        Error (Other "reduce() over a deferred computation requires an explicit init for this kernel (3-arg form `reduce(c, op, init)`) — only (+) and (*) carry implicit identities")
                     | _ ->
                        Error (Other "reduce() over a deferred computation requires an explicit init for a lambda kernel (3-arg form `reduce(c, op, init)`) — a fused fold cannot seed from its first element"))
            seed |> Result.map (fun tSeed ->
            let rebuilt =
                match leaves with
                | [one] -> one
                | first :: rest ->
                    rest |> List.fold (fun acc leaf ->
                        mkTyped (TExprFusion (acc, leaf)) (IRTTuple [acc.Type; leaf.Type])) first
                | [] -> tArr
            let resultType =
                match leaves with
                | [_] -> elem0
                | _ :: rest -> rest |> List.fold (fun acc _ -> IRTTuple [acc; elem0]) elem0
                | [] -> elem0
            mkTyped (TExprReduce (rebuilt, tKernel, Some tSeed)) resultType)))))))

and inferReduce (env: TypeEnv) array kernel (init: Expr option) : TypeResult<TypedExpr> =
    inferExpr env array |> Result.bind (fun tArr ->
    inferExpr env kernel |> Result.bind (fun tKernel ->
    (match init with
     | Some e -> inferExpr env e |> Result.map Some
     | None -> Ok None) |> Result.bind (fun tInitOpt ->
        // ---- Fused reduction terminal -------------------------------------
        // reduce over a DEFERRED computation (an unforced `L <@> k`, or an
        // <&!> fusion tree of them, possibly behind one let-hop) folds the
        // kernel over the computation's cells WITHOUT materializing arrays:
        // one loop nest, one scalar accumulator per fusion leaf (a tree
        // yields a tuple of scalars, mirroring `|> compute`'s tuple shape).
        // Semantically this is the fold stage of the loop-object composition
        // algebra — a binary-kernel object (object_for((+))) composed after
        // the map stages — typed here at the forcing site. (+)/(*) sections
        // seed with their identity; any other kernel REQUIRES the 3-arg
        // init: a fused nest cannot seed-with-first like the array fold.
        match tryInferReduceCompute env tArr tKernel tInitOpt with
        | Some result -> result
        | None ->
        // Drive type inference for unannotated kernel parameters: when
        // the array argument's resolved type is an unconstrained
        // inference variable (e.g., `lambda(g) -> reduce(g, (+))` with
        // no type annotation on `g`), bind it to a fresh rank-1 array.
        //
        // Element type is deduced from the kernel's first argument
        // type:
        //   1. If the kernel resolves to `FuncElem (paramTys, _)` and
        //      `paramTys.[0]` resolves to a concrete `IRTScalar et`,
        //      use `et`. Catches typed-arg kernels like
        //      `lambda(x: Int64, y: Int64) -> x + y` and untyped
        //      lambdas whose body has unified the params with a
        //      concrete elem type via literals or other constraints.
        //   2. Otherwise default to Float64. Operator sections like
        //      `(+)` always have fresh type variables for args, so
        //      they hit this path — which matches what
        //      lowerTypedSection emits in the IR (sections are
        //      hardcoded to Float64). Untyped lambdas with
        //      uninferable bodies also fall through here.
        //
        // The unification only fires when the resolved array type is
        // genuinely unconstrained; concrete non-array types fall
        // through to the existing error path. For non-Float64 source
        // data with a section kernel, an explicit annotation on the
        // kernel parameter is still required.
        // Phase B2: ElemType field is IRType. Helper returns IRType
        // directly; primitives are wrapped as IRTScalar at the
        // resolution point.
        //
        // Phase 2 (post-Gap-2.5): with strict scalar unify, we cannot
        // pin elem type to whatever the kernel's declared param type is —
        // operator sections like `(+)` have hardcoded Float64 in their
        // lowering, which would make `reduce(int_array, (+))` fail.
        // So when the kernel is a section default (or when its first
        // arg type is itself unresolved), we return a fresh inference
        // variable that gets pinned later by buildApplyInfo's
        // kernel-param unification against the actual source's per-row
        // type.
        //
        // We DO use a concrete kernel-arg type when the kernel was
        // declared with an explicit annotation (e.g., a user-written
        // `lambda(x: Int64) -> ...`). That's a real constraint from
        // the user; if it conflicts with the source array, that's a
        // genuine type error worth surfacing.
        let deduceElemFromKernel () : IRType =
            // Heuristic: if the kernel's first param is unresolved
            // (still an IRTInfer), the user didn't annotate it — so
            // we return that same inference variable (not a fresh one)
            // so subsequent inference flows through it: when
            // buildApplyInfo unifies the reduce's per-row type with
            // the source's elem type, the kernel's param type gets
            // pinned at the same time. If we returned a fresh var,
            // the section's existing T-var would never bind, leaving
            // an unresolved type variable in the kernel's lowered
            // form. If the first param IS concrete (annotated), we
            // pin to that and let unification surface mismatches.
            match env.Subst.Resolve(tKernel.Type) with
            | FuncElem (paramTys, _) when not paramTys.IsEmpty ->
                let resolved = env.Subst.Resolve(paramTys.[0])
                match resolved with
                | IRTScalar _ -> resolved          // annotated: pin to user's type
                | IRTInfer _ -> resolved           // unresolved: defer via the same var
                | _ -> env.Builder.FreshInferType()
            | _ -> env.Builder.FreshInferType()
        (match env.Subst.Resolve(tArr.Type) with
         | IRTInfer _ ->
            let elemType = deduceElemFromKernel ()
            let freshIdx = {
                Id = env.Builder.FreshId()
                Rank = 1
                Extent = IRParam ("__inferred_n", 0, IRTNat None)
                Symmetry = SymNone
                Tag = None; IxKind = IxKPlain
                Kind = SDimension
                Dependencies = []
            }
            let freshArr = mkArrayArrow [freshIdx] elemType None
            unify env.Subst tArr.Type freshArr |> ignore
         | _ -> ())
        match env.Subst.Resolve(tArr.Type) with
        | ArrayElem arrTy when arrTy.IndexTypes.Length = 1
                               && (match arrTy.IndexTypes.[0].Symmetry with
                                   | SymSymmetric | SymAntisymmetric | SymHermitian -> true
                                   | SymNone -> false) ->
            // Arc 3 hardening: a single SYMMETRY-CLASS record passes the
            // one-record check but is NOT a rank-1 axis — reduce would walk
            // extents[0] handing out row pointers (discovered via the
            // fill_random probe: compiled garbage). Also semantically
            // ambiguous (fold canonical cells vs logical cells). Reject with
            // guidance until a deliberate semantics is chosen.
            Error (Other "reduce() over compact symmetric/antisymmetric/Hermitian storage is not supported: folding the canonical cells and folding the logical (mirrored) cells differ. decompact(A, d) first for the logical fold.")
        | ArrayElem arrTy when arrTy.IndexTypes.Length = 1 ->
            // Static guarantee: reject if we can prove the extent is 0 AND no
            // init was supplied. With an init, the empty fold is defined (it
            // is simply init), so statically-empty inputs are legal.
            match tryEvalIntIR arrTy.IndexTypes.[0].Extent with
            | Some n when n <= 0L && tInitOpt.IsNone ->
                Error (ReduceEmptyArray n)
            | _ ->
                // Stage 3c.2 follow-on: unify the kernel's param types
                // with the array's element type. The kernel arrives as
                // an arrow `(α, β) → γ` where α, β are typically still
                // unresolved inference variables — operator sections
                // lower polymorphically per `inferExpr ExprSection`,
                // and unannotated 2-arg lambdas don't get their param
                // types pinned by the body alone for `+`/`-`/etc.
                // Without this unification, zonking defaults the
                // section's IRTInfer to Float64, which then bakes a
                // literal-Float64 signature into the lifted function
                // and breaks reduces over non-Float64 source arrays
                // (the lifted function returns Float64 even when the
                // accumulator is Int64). Pin both params to the
                // array's element type so the section's polymorphism
                // resolves correctly before zonking. Result type
                // stays whatever the kernel declared (Bool for
                // comparison sections, elemType for arithmetic).
                let kernelUnify =
                    match env.Subst.Resolve(tKernel.Type) with
                    | FuncElem (paramTys, _) ->
                        paramTys |> List.fold (fun acc pTy ->
                            acc |> Result.bind (fun () -> unify env.Subst pTy arrTy.ElemType))
                            (Ok ())
                    | _ -> Ok ()
                // The init seeds the accumulator, so it must share the
                // element type (same unification the kernel params get).
                let initUnify =
                    match tInitOpt with
                    | Some tInit -> unify env.Subst tInit.Type arrTy.ElemType
                    | None -> Ok ()
                kernelUnify |> Result.bind (fun () ->
                initUnify |> Result.bind (fun () ->
                    // arrTy.ElemType is IRType post-B2; return directly.
                    let resultType = arrTy.ElemType
                    Ok (mkTyped (TExprReduce (tArr, tKernel, tInitOpt)) resultType)))
        | ArrayElem _ ->
            Error (Other "reduce() currently supports only rank-1 arrays (multi-rank reduction over the innermost dimension is deferred)")
        | _ ->
            Error (Other "reduce() requires an array as first argument"))))

and inferProdSum (env: TypeEnv) (args: Expr list) : TypeResult<TypedExpr> =
    args |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tArgs ->
        // Every operand must be a rank-1 array over free (SymNone) storage;
        // element types unify across operands (the product's type).
        let rec go (elemTy: IRType option) (staticN: int64 option) (rest: TypedExpr list) : Result<IRType, TypeError> =
            match rest with
            | [] ->
                (match elemTy with
                 | Some e -> Ok e
                 | None -> Error (Other "prodsum() requires at least one array argument"))
            | t :: more ->
                match env.Subst.Resolve t.Type with
                | ArrayElem arrTy when arrTy.IndexTypes.Length = 1
                                       && (match arrTy.IndexTypes.[0].Symmetry with
                                           | SymSymmetric | SymAntisymmetric | SymHermitian -> true
                                           | SymNone -> false) ->
                    // Same ambiguity as reduce over compact storage: canonical
                    // vs logical cells differ. Mirror its guidance.
                    Error (Other "prodsum() over compact symmetric/antisymmetric/Hermitian storage is not supported: folding the canonical cells and folding the logical (mirrored) cells differ. decompact(A, d) first for the logical fold.")
                | ArrayElem arrTy when arrTy.IndexTypes.Length = 1 ->
                    // Provably mismatched static extents are an error now;
                    // unknown extents are trusted (codegen loops over the
                    // first operand's extent).
                    let thisN = tryEvalIntIR arrTy.IndexTypes.[0].Extent
                    (match staticN, thisN with
                     | Some a, Some b when a <> b ->
                         Error (ProdsumExtentMismatch (a, b))
                     | _ ->
                        let unifyElem =
                            match elemTy with
                            | Some e -> unify env.Subst e arrTy.ElemType
                            | None -> Ok ()
                        unifyElem |> Result.bind (fun () ->
                            go (Some (elemTy |> Option.defaultValue arrTy.ElemType))
                               (match staticN with Some _ -> staticN | None -> thisN)
                               more))
                | ArrayElem _ ->
                    Error (Other "prodsum() supports only rank-1 arrays (fibers); pass each operand's innermost slice")
                | _ ->
                    Error (Other "prodsum() requires array arguments")
        go None None tArgs |> Result.map (fun elemTy ->
            mkTyped (TExprProdSum tArgs) elemTy))

/// __dist_pack(κ1, ..., κr): construct a Dist<r, τ like axes> value from
/// its cumulant component arrays. Compiler-internal — the PPL elaboration
/// stage emits it after building the fused tower. κ_1 (the mean tensor over
/// the variable axes as-declared) fixes τ and the axes; the component count
/// fixes r. The typed node is a plain TExprTuple — the exact representation
/// the type erases to at zonk — so no new lowering or codegen path exists;
/// only the TYPE is nominal. Unification stays strict (Unify has no
/// tuple↔Dist coercion), so this intrinsic and Dist-typed operators are the
/// only producers of Dist values.
and inferDistPack (env: TypeEnv) (args: Expr list) : TypeResult<TypedExpr> =
    args |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tArgs ->
        match env.Subst.Resolve tArgs.Head.Type with
        | ArrayElem a1 ->
            let order = tArgs.Length
            Ok (mkTyped (TExprTuple tArgs) (IRTDist (order, a1.ElemType, a1.IndexTypes)))
        | t ->
            Error (Other (sprintf "__dist_pack: components must be arrays (κ_1 lowered to %s) — this intrinsic is emitted by the PPL elaboration stage, not written by hand" (ppIRType t))))

/// cumulant(d, k): the order-k component of a Dist value, as an ordinary
/// array. THE ORDER GUARD AS A TYPE ERROR: k must be a static int (the
/// replicate-count contract) in 1..r. Result type comes from
/// distComponentType, so the projection is fully typed at any expression
/// position — including on Dist-typed function parameters, where the old
/// elaboration-level registry could never reach.
and inferCumulantProj (env: TypeEnv) (dExpr: Expr) (kExpr: Expr) : TypeResult<TypedExpr> =
    inferExpr env dExpr |> Result.bind (fun tD ->
        match env.Subst.Resolve tD.Type with
        | IRTDist (order, elem, axes) ->
            (match evalStaticIntExpr env kExpr with
             | None ->
                 Error (Other "cumulant: the order must be a compile-time integer (a literal, `let static`, or static-function call)")
             | Some k when k < 1 ->
                 Error (CumulantOrderPositive k)
             | Some k when k > order ->
                 Error (CumulantOrderExceeds (k, order))
             | Some k ->
                 let compTy = distComponentType k elem axes
                 let idxLit = mkTyped (TExprLit (LitInt (int64 (k - 1)))) (IRTScalar ETInt64)
                 Ok (mkTyped (TExprTupleIndex (tD, idxLit)) compTy))
        | t ->
            Error (CumulantNeedsDist (ppIRType t)))

// extents(array) — extent(s) along each dimension as Int64.
// Rank-1 → scalar Int64 (the single dim's extent).
// Rank-N → tuple (Int64, Int64, ..., Int64) of length N (one per dim,
//          outermost first).
// The codegen prefers a static literal when the index type's extent
// expression is statically evaluable (extends to literal arithmetic via
// tryEvalIntIR), matching rank()'s static-when-possible behavior.
//
// Future direction: a homogeneous List<Int64> collection type would be a
// more natural return type for the multi-rank case. Tuples work because
// all components share Int64, but they're heterogeneous in the type
// system. Switching to List when one becomes available is a future-
// compat note, not a blocker.


and inferDecompact (env: TypeEnv) array d : TypeResult<TypedExpr> =
    inferExpr env array |> Result.bind (fun tArr ->
        requireArrayArg env tArr "decompact" |> Result.bind (fun arrTy ->
            // Resolve the logical dimension d to (slotIndex, slotArity,
            // posInSlot). A compact slot of arity r spans r consecutive
            // dimensions; posInSlot in [0, r) says which component within
            // the group d targets — that position decides peel-first /
            // peel-last / peel-middle.
            let totalDims = arrTy.IndexTypes |> List.sumBy (fun ix -> max 1 ix.Rank)
            let dimToSlot (dd: int) : Result<int * int * int, TypeError> =
                if dd < 0 || dd >= totalDims then
                    Error (DecompactDimRange (dd, totalDims))
                else
                    let rec walk slotIdx acc remaining =
                        match remaining with
                        | [] -> Error (Other (sprintf "decompact: dimension %d out of range (internal)" dd))
                        | (ix: IRIndexType) :: rest ->
                            let ar = max 1 ix.Rank
                            if dd < acc + ar then Ok (slotIdx, ar, dd - acc)
                            else walk (slotIdx + 1) (acc + ar) rest
                    walk 0 0 arrTy.IndexTypes
            dimToSlot d |> Result.bind (fun (slot, r, posInSlot) ->
                let ix = arrTy.IndexTypes.[slot]
                if r < 2 || ix.Symmetry = SymNone then
                    Error (DecompactPlainAxis d)
                else
                // Codegen scope: the compact group being decompacted must be
                // the LAST index slot, with any preceding slots being plain
                // free Idx singletons (arity-1, SymNone). This is exactly the
                // shape produced by a chained "to-the-right-only" peel
                // (decompact frees one dim at a time, accumulating free Idx
                // dims on the left while the residual group stays last), so
                // it enables composed full densification. The surrounding
                // free dims are emitted as an outer loop product wrapping the
                // existing last-slot fission scatter (their indices map
                // identically source->dest). Other arrangements (compact slot
                // not last, or non-singleton surrounding slots) remain
                // deferred.
                let leadingSlots = arrTy.IndexTypes |> List.take slot
                let groupIsLast = (slot = arrTy.IndexTypes.Length - 1)
                let leadingAllFreeSingletons =
                    leadingSlots |> List.forall (fun s -> (max 1 s.Rank) = 1 && s.Symmetry = SymNone)
                if not (groupIsLast && leadingAllFreeSingletons) then
                    Error (DecompactLastSlotOnly (arrTy.IndexTypes.Length, slot))
                elif leadingSlots.Length > 0 && ix.Symmetry <> SymSymmetric then
                    // Surrounding-dim wrapping is currently wired only for the
                    // symmetric fission scatter (the gather form). Antisym /
                    // Hermitian fission with leading free dims is not yet
                    // emitted, so reject rather than miscompile.
                    Error (Other "decompact: surrounding free dimensions are currently supported only for symmetric groups; antisymmetric/Hermitian groups with preceding free dimensions are not yet wired.")
                elif ix.Symmetry = SymHermitian && r >= 3 then
                    // Rank-2 Hermitian (the only Hermitian arrays a producer
                    // makes today, via gram) dissolves to a dense n×n with the
                    // lower triangle conjugated — handled below. Rank >= 3
                    // Hermitian has no producer yet and its compact-residual
                    // conjugate semantics aren't worked out, so reject.
                    Error (Other "decompact: rank >= 3 SymHermitian is not yet supported (no producer exists for rank >= 3 Hermitian arrays; gram produces rank-2 Hermitian, which decompacts to a dense conjugate-mirrored matrix).")
                else
                    // SYMMETRIC decompact: general fission, any rank, any cut
                    // (peel-first/last/middle), via the gather materializer.
                    // ANTISYMMETRIC decompact: rank 2 dissolves to dense n×n;
                    // rank >= 3 BOUNDARY cuts leave one residual antisym group,
                    // now allocatable via the per-group-strict mask
                    // (allocate_strict) with the sign applied lazily on read
                    // (canon_* transform). Codegen-supported, so not rejected.
                    // Build the replacement slots: left remainder (arity
                    // posInSlot) + extracted Idx + right remainder (arity
                    // r-1-posInSlot). Each remainder of arity a>=2 becomes a
                    // fresh SymIdx<a> of the SAME symmetry class (fresh Id =
                    // nominally distinct, since the two halves' symmetries are
                    // now independent relations); a=1 degenerates to a plain
                    // Idx; a=0 is omitted. For the v1 r=2 case this always
                    // yields two plain Idx (the group fully dissolves).
                    let mkRemainder (a: int) : IRIndexType list =
                        if a <= 0 then []
                        elif a = 1 then
                            [ { ix with Id = env.Builder.FreshId(); Rank = 1; Symmetry = SymNone } ]
                        else
                            [ { ix with Id = env.Builder.FreshId(); Rank = a } ]
                    let extracted =
                        { ix with Id = env.Builder.FreshId(); Rank = 1; Symmetry = SymNone }
                    let leftRem = mkRemainder posInSlot
                    let rightRem = mkRemainder (r - 1 - posInSlot)
                    let replacement = leftRem @ [extracted] @ rightRem
                    let newIndexTypes =
                        arrTy.IndexTypes
                        |> List.mapi (fun i s -> (i, s))
                        |> List.collect (fun (i, s) -> if i = slot then replacement else [s])
                    let resultType = mkArrayArrow newIndexTypes arrTy.ElemType None
                    Ok (mkTyped (TExprDecompact (tArr, d)) resultType))))

// reduce(array, kernel) — T/S reduction primitive.
// Consumes the innermost dimension via a binary kernel. For rank-1 input,
// produces a scalar of the element type. Multi-rank reduction is deferred.
//
// Empty-array policy (post-extents integration):
//   - If extent is statically known to be 0  → typecheck error (caller bug)
//   - If extent is statically known to be > 0 → typecheck OK; codegen emits
//                                              standard loop without guard
//   - If extent is dynamic                   → typecheck OK; codegen adds
//                                              a runtime guard that aborts
//                                              cleanly on empty
//
// A 3-arg form `reduce(arr, op, init)` for well-defined empty handling
// (returning init) is a future addition for the dynamic case if a use
// case demands silently-handled empties.


and inferGram (env: TypeEnv) leftE rightE : TypeResult<TypedExpr> =
    // gram(A, B) = A * B^H:  result[i][j] = sum_k A[i][k] * conj(B[j][k])
    // A : m x n, B : p x n (shared contracted dim n) -> result : m x p.
    // Element type: complex iff EITHER operand is complex (conj is the
    // identity on reals, so a real/complex mix still conjugates correctly).
    // Symmetry: when A and B are the SAME array (syntactically the same
    // variable -> conservative, never claims false symmetry), the result is
    // square (m = p) and SymHermitian (complex) / SymSymmetric (real),
    // computed by the triangular upper-half scatter. Otherwise it is a
    // general dense m x p array (SymNone).
    inferExpr env leftE |> Result.bind (fun tL ->
    inferExpr env rightE |> Result.bind (fun tR ->
        requireArrayArg env tL "gram" |> Result.bind (fun lTy ->
        requireArrayArg env tR "gram" |> Result.bind (fun rTy ->
            let lDims = lTy.IndexTypes |> List.sumBy (fun ix -> max 1 ix.Rank)
            let rDims = rTy.IndexTypes |> List.sumBy (fun ix -> max 1 ix.Rank)
            if lDims <> 2 || rDims <> 2 then
                Error (GramNeedsRank2 (lDims, rDims))
            else
                // Extents: outer (m / p) and inner contracted (n) per operand.
                let lOuter = lTy.IndexTypes.[0].Extent
                let lInner = lTy.IndexTypes.[1].Extent
                let rOuter = rTy.IndexTypes.[0].Extent
                let rInner = rTy.IndexTypes.[1].Extent
                // Static contracted-dim mismatch is a hard error; otherwise trust.
                let innerMismatch =
                    match tryEvalIntIR lInner, tryEvalIntIR rInner with
                    | Some a, Some b -> a <> b
                    | _ -> false
                if innerMismatch then
                    Error (Other "gram(A, B): the contracted (trailing) dimensions of A and B must match.")
                else
                    // Element type join: complex if either operand is complex.
                    let isComplexElem (t: IRType) =
                        match t with
                        | IRTScalar (ETComplex64 | ETComplex128) -> true
                        | _ -> false
                    let outElem =
                        if isComplexElem lTy.ElemType then lTy.ElemType
                        elif isComplexElem rTy.ElemType then rTy.ElemType
                        else lTy.ElemType
                    let isComplex = isComplexElem outElem
                    // Conservative same-array test: both bare vars, same name.
                    let sameArray =
                        match tL.Kind, tR.Kind with
                        | TExprVar (n1, _, _), TExprVar (n2, _, _) -> n1 = n2
                        | _ -> false
                    let freshSlot (ext: IRExpr) (sym: SymmetryClass) (rank: int) =
                        { Id = env.Builder.FreshId(); Rank = rank; Extent = ext
                          Symmetry = sym; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
                    let resultType =
                        if sameArray then
                            // Square m x m, compact group of arity 2 carrying the
                            // (anti-)symmetry: Hermitian (complex) or symmetric.
                            let sym = if isComplex then SymHermitian else SymSymmetric
                            let grp = { (freshSlot lOuter sym 2) with Extent = lOuter }
                            mkArrayArrow [grp] outElem None
                        else
                            // General dense m x p (two independent plain axes).
                            let s0 = freshSlot lOuter SymNone 1
                            let s1 = freshSlot rOuter SymNone 1
                            mkArrayArrow [s0; s1] outElem None
                    Ok (mkTyped (TExprGram (tL, tR, sameArray)) resultType)))))



and inferTranspose (env: TypeEnv) array d1 d2 : TypeResult<TypedExpr> =
    inferExpr env array |> Result.bind (fun tArr ->
        requireArrayArg env tArr "transpose" |> Result.bind (fun arrTy ->
            // Map a logical DIMENSION index to its INDEX-TYPE slot. A slot
            // of arity k occupies k consecutive dimensions; we walk the
            // slot list accumulating arities until the target dimension
            // falls inside a slot. Returns (slotIndex, slotArity, dimWithinSlot).
            // For the first cut every reachable slot is arity-1, so this is
            // identity — but writing it properly keeps the gate correct in
            // the presence of compound groups elsewhere in the array.
            let totalDims = arrTy.IndexTypes |> List.sumBy (fun ix -> max 1 ix.Rank)
            let dimToSlot (d: int) : Result<int * int * int, TypeError> =
                if d < 0 || d >= totalDims then
                    Error (TransposeAxisRange (d, totalDims))
                else
                    let rec walk slotIdx acc remaining =
                        match remaining with
                        | [] -> Error (Other (sprintf "transpose: axis %d out of range (internal)" d))
                        | (ix: IRIndexType) :: rest ->
                            let ar = max 1 ix.Rank
                            if d < acc + ar then Ok (slotIdx, ar, d - acc)
                            else walk (slotIdx + 1) (acc + ar) rest
                    walk 0 0 arrTy.IndexTypes
            if d1 = d2 then
                Error (TransposeAxesEqual (d1, d2))
            else
                dimToSlot d1 |> Result.bind (fun (slot1, ar1, _) ->
                dimToSlot d2 |> Result.bind (fun (slot2, ar2, _) ->
                    let ix1 = arrTy.IndexTypes.[slot1]
                    let ix2 = arrTy.IndexTypes.[slot2]
                    if slot1 = slot2 then
                        // Both dimensions lie INSIDE one index type — an intra-
                        // group swap. The index-type class decides the behavior
                        // (storage-preserving): symmetric -> identity, antisym ->
                        // whole-array negation, hermitian -> whole-array
                        // conjugation. No decompaction, no dense blow-up.
                        (match (behaviorOf ix1).TransposeWithin () with
                         | TIdentity ->
                            // A(i,j) = A(j,i): storage unchanged. Erase the
                            // transpose; the result IS the source array.
                            Ok tArr
                         | TNegatedCopy ->
                            Ok (mkTyped (TExprArrayNegate tArr) tArr.Type)
                         | TConjugatedCopy ->
                            Ok (mkTyped (TExprArrayConjugate tArr) tArr.Type)
                         | TDataMove ->
                            // A plain (SymNone) slot of arity >= 2 swapped
                            // within itself: a genuine dimensional swap inside a
                            // rectangular compound. Not yet emitted (the data-
                            // move materializer handles cross-slot rank-1 only).
                            Error (TransposeWithinGroup ar1)
                         | TRequiresDecompaction reason ->
                            Error (Other (sprintf "transpose: %s" reason)))
                    else
                        // Different slots. The structure-preserving case is two
                        // plain (arity-1 SymNone) axes -> physical data move.
                        // Anything else means one axis is bound in a compact
                        // group and the other is outside it: swapping them would
                        // break that group's symmetry. That is a structure-
                        // changing operation requiring explicit decompaction
                        // (decompact then transpose), not a silent transpose.
                        let plain ar (ix: IRIndexType) = ar = 1 && ix.Symmetry = SymNone
                        if plain ar1 ix1 && plain ar2 ix2 then
                            let swapped =
                                arrTy.IndexTypes
                                |> List.mapi (fun i ix ->
                                    if i = slot1 then arrTy.IndexTypes.[slot2]
                                    elif i = slot2 then arrTy.IndexTypes.[slot1]
                                    else ix)
                            let resultType = mkArrayArrow swapped arrTy.ElemType None
                            Ok (mkTyped (TExprTranspose (tArr, d1, d2)) resultType)
                        else
                            let culprit, cd, car, cix =
                                if not (plain ar1 ix1) then "first", d1, ar1, ix1 else "second", d2, ar2, ix2
                            Error (Other (sprintf "transpose: the %s axis (dim %d) is bound in a %A index group (rank %d), and the other axis is outside it. Swapping across a group boundary would decompose the group's symmetry. Decompact the axis first (decompact then transpose)." culprit cd cix.Symmetry car))))))



and inferGroupBy (env: TypeEnv) values grouping : TypeResult<TypedExpr> =
    inferExpr env values |> Result.bind (fun tVals ->
    inferExpr env grouping |> Result.bind (fun tGrouping ->
        requireArrayArg env tVals "group_by" |> Result.bind (fun arrTy ->
            // Extract group structure from GroupKeys type, or fall back for raw key arrays
            let (outerIdx, memberIdx) =
                match env.Subst.Resolve(tGrouping.Type) with
                | IRTGroupKeys (outer, _, _) ->
                    let member_ = {
                        Id = env.Builder.FreshId(); Rank = 1
                        Extent = IRParam ("__groupsz", 0, IRTNat None)
                        Symmetry = SymNone; Tag = Some "__group_member"; IxKind = IxKGroupMember
                        Kind = SDimension; Dependencies = []
                    }
                    ({ outer with Id = env.Builder.FreshId(); Tag = Some "__group_outer"; IxKind = IxKGroupOuter }, member_)
                | _ ->
                    // Fallback: treat second arg as raw key array (backward compat)
                    let outer = {
                        Id = env.Builder.FreshId(); Rank = 1
                        Extent = IRParam ("__ngroups", 0, IRTNat None)
                        Symmetry = SymNone; Tag = Some "__group_outer"; IxKind = IxKGroupOuter
                        Kind = SDimension; Dependencies = []
                    }
                    let member_ = {
                        Id = env.Builder.FreshId(); Rank = 1
                        Extent = IRParam ("__groupsz", 0, IRTNat None)
                        Symmetry = SymNone; Tag = Some "__group_member"; IxKind = IxKGroupMember
                        Kind = SDimension; Dependencies = []
                    }
                    (outer, member_)
            let resultType = mkArrayArrow [outerIdx; memberIdx] arrTy.ElemType None
            Ok (mkTyped (TExprGroupBy (tVals, tGrouping)) resultType))))

// sort(array, key) — stable sort by ascending key.
// Phase 1 (current): eager materialization. Result is a new physical array
// with the same element type and extent as the input, indexed by a fresh
// anonymous index (mask-style). The order property is NOT tracked in the
// type system yet — that's deferred to a future "key map chain" subsystem.
//
// The eventual direction is lazy: sort produces a chain handle recording
// (key_fn, permutation), with materialization deferred to first access.
// Rationale: deferring evaluation preserves optimization headroom — the
// compiler can analyze chains of operations before any layout is committed,
// enabling sort-skip, merge-style joins, and other rearrangements.
// Caching strategies are downstream of those analyses, not a substitute.


and inferGroupKeys (env: TypeEnv) keys : TypeResult<TypedExpr> =
    match keys with
    | [] ->
        Error (Other "group_keys requires at least one key array; got empty argument list")
    | [singleKey] ->
        // Existing single-key path, unchanged.
        inferExpr env singleKey |> Result.bind (fun tKeys ->
            requireArrayArg env tKeys "group_keys" |> Result.bind (fun arrTy ->
                let sourceIdx =
                    if arrTy.IndexTypes.Length > 0 then arrTy.IndexTypes.[0]
                    else {
                        Id = env.Builder.FreshId(); Rank = 1
                        Extent = IRParam ("__src", 0, IRTNat None)
                        Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                        Kind = SDimension; Dependencies = []
                    }
                let namedRef =
                    match arrTy.ElemType with
                    | IRTIdxTagged (_, IRefNamed name) -> Some name
                    | _ -> None
                let (outerIdx, enumValues) =
                    match namedRef with
                    | Some name ->
                        match lookupTypeDef name env with
                        | Some (TDIIndexType (_, idx, _)) ->
                            ({ idx with Id = env.Builder.FreshId(); Tag = Some name; IxKind = ixKindOfTag (Some name) }, None)
                        | Some (TDIEnumIdx (_, idx, values, _)) ->
                            ({ idx with Id = env.Builder.FreshId(); Tag = Some name; IxKind = ixKindOfTag (Some name) }, Some values)
                        | _ ->
                            ({ Id = env.Builder.FreshId(); Rank = 1
                               Extent = IRParam ("__ngroups", 0, IRTNat None)
                               Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                               Kind = SDimension; Dependencies = [] }, None)
                    | None ->
                        ({ Id = env.Builder.FreshId(); Rank = 1
                           Extent = IRParam ("__ngroups", 0, IRTNat None)
                           Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                           Kind = SDimension; Dependencies = [] }, None)
                let gkType = IRTGroupKeys (outerIdx, sourceIdx, enumValues)
                Ok (mkTyped (TExprGroupKeys [tKeys]) gkType)))
    | multipleKeys ->
        // Compound case: infer all keys, verify shared outer index,
        // build a GroupKeys result with dynamic compound outer.
        let inferAll =
            multipleKeys
            |> List.fold (fun accRes k ->
                accRes |> Result.bind (fun acc ->
                    inferExpr env k |> Result.bind (fun tk ->
                        requireArrayArg env tk "group_keys" |> Result.map (fun arrTy ->
                            acc @ [(tk, arrTy)]))))
                (Ok [])
        inferAll |> Result.bind (fun pairs ->
            // Precondition check: all key arrays must be rank-1 and
            // share an outer index. We compare extent expressions
            // structurally — Blade's typechecker is structural enough
            // that the same Idx<N> annotation produces equal Extent
            // values across multiple bindings.
            let firstSource =
                pairs |> List.head |> snd |> fun ty -> ty.IndexTypes.[0]
            let allShareOuter =
                pairs |> List.forall (fun (_, ty) ->
                    ty.IndexTypes.Length = 1
                    && ty.IndexTypes.[0].Extent = firstSource.Extent)
            if not allShareOuter then
                Error (GroupKeysRank1)
            else
                // Compound outer: dynamic extent, tagged so codegen
                // recognizes "this is compound-dynamic, dispatch via
                // tuple unordered_map". The tag name reserves
                // `__compoundidx_static` for the future mask-derived
                // path (TyCompoundIdx) which is statically evaluable
                // — that case would have Extent = IRLit (cardinality)
                // and a different codegen story. Component types are
                // not carried in IRTGroupKeys today; codegen recovers
                // them from the IRGroupKeys node's keys list at emit
                // time, and IDE tooltips would do the same.
                let outerIdx = {
                    Id = env.Builder.FreshId(); Rank = 1
                    Extent = IRParam ("__ngroups", 0, IRTNat None)
                    Symmetry = SymNone
                    Tag = Some "__compoundidx_dynamic"; IxKind = IxKCompoundDynamic
                    Kind = SDimension; Dependencies = []
                }
                let gkType = IRTGroupKeys (outerIdx, firstSource, None)
                let tKeys = pairs |> List.map fst
                Ok (mkTyped (TExprGroupKeys tKeys) gkType))

// group_by(values, grouping) — apply GroupKeys to a values array
// Result: rank-2 array (groups × members), with GroupIdx
// Tags ("__group_outer", "__group_member") signal to codegen to use ragged peel.


and inferReplicate (env: TypeEnv) count body : TypeResult<TypedExpr> =
    inferExpr env count |> Result.bind (fun tC ->
    inferExpr env body |> Result.bind (fun tB ->
        // The count must be compile-time known. Accept a bare literal, or any
        // statically evaluable integer expression (a `let static` value or a
        // static-function call), resolved via the same StaticEval the lowering
        // phase uses. env.StaticValues/StaticFunctions were populated in the
        // checkModule pre-pass.
        let n =
            match tC.Kind with
            | TExprLit (LitInt v) -> Some (int v)
            | _ ->
                let staticEnv : StaticEval.StaticEnv =
                    { Values = env.StaticValues
                      Functions =
                        env.StaticFunctions
                        |> Map.map (fun _ (fd: FunctionDecl) ->
                            { StaticEval.Name = fd.Name
                              StaticEval.Params = fd.Params |> List.map (fun p -> p.Name)
                              StaticEval.Body = fd.Body })
                      CalledFunctions = ref Set.empty
                      ProviderRoots = Map.empty
                      Structs = Map.empty }
                match StaticEval.evalExpr staticEnv StaticEval.maxSteps count with
                | Ok (StaticEval.SVInt v) -> Some (int v)
                | _ -> None
        // Normalize the resolved count to a literal in the typed tree, so the
        // lowering unroll (List.replicate n) sees a concrete factor regardless
        // of how the count was written at the source level.
        let litCount k = mkTyped (TExprLit (LitInt (int64 k))) tC.Type
        match n with
        | None ->
            Error (Other "replicate count must be a compile-time integer (a literal, `let static`, or static-function call)")
        | Some 1 ->
            // replicate(1, c) ≡ c
            Ok tB
        | Some n when n >= 2 ->
            let resolved = env.Subst.Resolve(tB.Type)
            // Create anonymous Idx<N> for the replicate dimension
            let seqIdx = {
                Id = env.Builder.FreshId()
                Rank = 1
                Extent = IRLit (IRLitInt (int64 n))
                Symmetry = SymNone
                Tag = Some "__seq"; IxKind = IxKSeq
                Kind = SDimension
                Dependencies = []
            }
            let resultType =
                match resolved with
                | ArrayElem arrTy ->
                    mkArrayLike { arrTy with IndexTypes = seqIdx :: arrTy.IndexTypes }
                | IRTScalar et ->
                    mkArrayArrow [seqIdx] (IRTScalar et) None
                | _ ->
                    // S3 tag: relic. Same as sequence fallback above.
                    mkArrayArrow [seqIdx] (IRTScalar ETFloat64) None
            Ok (mkTyped (TExprReplicate (litCount n, tB)) resultType)
        | _ ->
            Error (Other (sprintf "replicate count must be >= 1, got %A" n))))

// ---- Reynolds ----


and inferTupleIndex (env: TypeEnv) tuple index : TypeResult<TypedExpr> =
    inferExpr env tuple |> Result.bind (fun tT ->
    inferExpr env index |> Result.bind (fun tI ->
        match env.Subst.Resolve(tT.Type) with
        | ArrayElem arrTy ->
            // One bracket = one index dimension. Mirrors ExprApp's
            // tArgs.Length <= arrTy.IndexTypes.Length check.
            let identity = match tT.Kind with TExprVar (_, _, id) -> id | _ -> None
            // Tag check on the single index (same rule as ExprApp).
            let tagMismatch =
                match arrTy.IndexTypes with
                | [] -> None
                | idxType :: _ ->
                    match idxType.Tag with
                    | Some tagName when not (tagName.StartsWith("__")) ->
                        match env.Subst.Resolve tI.Type with
                        | IRTIdxTagged (_, IRefNamed argName) when argName = tagName -> None
                        | IRTIdxTagged (_, IRefNamed argName) ->
                            Some (IndexTagMismatchNamed (tagName, argName))
                        | IRTIdxTagged (_, IRefAnon _) ->
                            Some (IndexTagMismatchAnon tagName)
                        | IRTScalar (ETInt32 | ETInt64) ->
                            emitWarning env (sprintf
                                "Array indexed with untagged integer where slot expects tag '%s'. Consider an explicit cast like `(expr : %s)` or iterate via `range<%s>` to flow the tag automatically."
                                tagName tagName tagName)
                            None
                        | _ -> None
                    | _ -> None
            match tagMismatch with
            | Some err -> Error err
            | None ->
                if 1 = arrTy.IndexTypes.Length then
                    Ok (mkTyped (TExprIndex (tT, [tI], identity)) arrTy.ElemType)
                elif 1 < arrTy.IndexTypes.Length then
                    let remaining = arrTy.IndexTypes |> List.skip 1
                    Ok (mkTyped (TExprIndex (tT, [tI], identity))
                                (mkArrayLike { arrTy with IndexTypes = remaining }))
                else
                    Error (Other "array indexing: too many indices for array rank")
        | _ ->
            // Poly-pack / tuple indexing: result type is fresh — codegen
            // resolves via std::get based on flat-leaf paths.
            Ok (mkTyped (TExprTupleIndex (tT, tI)) (env.Subst.Fresh()))))

// ---- Field access ----


and inferUnaryOp (env: TypeEnv) op operand : TypeResult<TypedExpr> =
    inferExpr env operand |> Result.bind (fun tOp ->
        // OpConj (conj): result type equals operand type. Conjugate of a
        // complex is complex; of a real, the identity (real). Permissive,
        // mirroring OpNeg — no type guard. Codegen emits std::conj only for
        // complex element types; reals pass through unchanged.
        //
        // WHOLE-ARRAY conj: when the operand is an array (not a scalar), the
        // scalar IRUnaryOp(IRConj, _) path is wrong — it would emit a scalar
        // std::conj against an array value and fall through to a bare
        // passthrough (losing the Array<> wrapper and applying no conjugation).
        // Route it to TExprArrayConjugate, the whole-array eager conjugate
        // (same node the Hermitian-transpose TConjugatedCopy path uses), which
        // materializes a fresh same-shape array with a pool conjugation loop.
        // This is what makes `hermitian(A) = conj(transpose(A,[0,1]))` work,
        // and it fixes surface `conj(wholeArray)` generally.
        match op, tOp.Type with
        | OpConj, ArrayElem _ ->
            Ok (mkTyped (TExprArrayConjugate tOp) tOp.Type)
        | _ ->
            let resTy = match op with
                        | OpNot -> IRTScalar ETBool
                        | OpNeg -> tOp.Type
                        | OpConj -> tOp.Type
                        // OpReal/OpImag project a complex to its component
                        // width (identity on a real operand); OpArg is a real
                        // angle. Synthesized by the intrinsic intercept below.
                        | OpReal | OpImag ->
                            (match tOp.Type with
                             | IRTScalar ETComplex64 -> IRTScalar ETFloat32
                             | IRTScalar ETComplex128 -> IRTScalar ETFloat64
                             | other -> other)
                        | OpArg -> IRTScalar ETFloat64
                        // OpMath is synthesized by the ExprApp intrinsic
                        // intercept, never parsed as ExprUnaryOp — this arm
                        // is exhaustiveness only.
                        | OpMath _ -> IRTScalar ETFloat64
            Ok (mkTyped (TExprUnaryOp (op, tOp)) resTy))

// ---- Module-qualified value/function: `Math.pi`, `MathLib.double(x)` ----
// These two cases must precede the method-call and struct-field handlers
// because `Math` would otherwise be looked up as a value and fail. The
// DeclImport handler registers imported entries under qualified names
// (`Math.pi`, `MathLib.double`); when the form is `ExprVar n . field`
// and the qualified name resolves, we treat the access as a direct
// variable reference. Falls through to the existing struct/method
// handlers when the qualified name is not registered.

// ---- Provider compound read: alias.load_compound(var, mask) ----
// Rides the generic field-call shape (no new syntax). The mask is any
// integer array; compoundViewType validates full-dimension coverage and
// yields the compact Compound<T, RANK> view type. The maskIR carried in the
// type is a unit placeholder: codegen recovers the actual mask variable by
// name from the argument shape (ProviderReadSpec), not from the type.


and inferMethodCall (env: TypeEnv) obj method args : TypeResult<TypedExpr> =
    inferExpr env obj |> Result.bind (fun tObj ->
    args |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tArgs ->
        // Check if this is an impl method call
        match tObj.Type with
        | IRTNamed typeName ->
            match Map.tryFind (typeName, method) env.ImplMethods with
            | Some (funcVarId, funcType) ->
                // Resolve to mangled function call: TypeName__method(self, args)
                let mangledName = sprintf "%s__%s" typeName method
                let retTy = match funcType with FuncElem (_, ret) -> ret | _ -> env.Subst.Fresh()
                let tFunc = mkTyped (TExprVar (mangledName, funcVarId, None)) funcType
                Ok (mkTyped (TExprApp (tFunc, tObj :: tArgs)) retTy)
            | None ->
                // Not an impl method — treat as struct field access + application
                let (fieldTy, fieldIdx) =
                    match lookupTypeDef typeName env with
                    | Some (TDIStruct (_, _, fields, _)) ->
                        let idx = fields |> List.tryFindIndex (fun (n, _) -> n = method)
                        let ty = fields |> List.tryFind (fun (n, _) -> n = method) |> Option.map snd
                        (ty |> Option.defaultValue (env.Subst.Fresh()),
                         idx |> Option.defaultValue 0)
                    | _ -> (env.Subst.Fresh(), 0)
                let tField = mkTyped (TExprField (tObj, method, fieldIdx)) fieldTy
                // Route through dispatchAppOrIndex so array-typed fields
                // become TExprIndex (with tag-checking) rather than
                // TExprApp. Without this, `data.region(s)` would lower to
                // IRApp and emit a C++ function call against the
                // Array<T,N> wrapper, which has no operator().
                dispatchAppOrIndex env tField tArgs
        | _ ->
            // Non-named type — regular field access + application
            let tField = mkTyped (TExprField (tObj, method, 0)) (env.Subst.Fresh())
            let retTy = env.Subst.Fresh()
            Ok (mkTyped (TExprApp (tField, tArgs)) retTy)))

// ---- Application / Array indexing ----


and inferMask (env: TypeEnv) array pred : TypeResult<TypedExpr> =
    inferExpr env array |> Result.bind (fun tArr ->
    inferExpr env pred |> Result.bind (fun tPred ->
        requireArrayArg env tArr "mask" |> Result.bind (fun arrTy ->
            // The predicate is `Element → Bool`. inferExpr typed it
            // without knowing the element type, so its param starts as
            // a fresh IRTInfer. Without explicit unification here, the
            // var stays unbound and zonkType's default (Float64) kicks
            // in — which then breaks bodies that use integer ops on
            // int arrays (`x % 2`) or struct field access on struct
            // arrays (`p.a`). Unify the predicate's param with the
            // array's element type so zonking propagates the right
            // type into the standalone-lifted function signature and
            // the body's operator/field-access positions resolve
            // against the real element type.
            let unifyPredParam =
                match tPred.Kind with
                | TExprLambda info when info.Params.Length = 1 ->
                    unify env.Subst info.Params.[0].Type arrTy.ElemType
                | _ -> Ok ()
            unifyPredParam |> Result.bind (fun () ->
                let resultType = mkArrayArrow arrTy.IndexTypes (IRTScalar ETBool) None
                Ok (mkTyped (TExprMask (tArr, tPred)) resultType)))))

// compound(dense, mask) -- scatter a dense array into a CompoundIdx-typed
// compact array via a bool mask over the leading dims (formalism 4.5). The
// in-language analog of the provider's load_compound: same validation
// (compoundViewType checks the mask covers a leading prefix of dense's dims
// and yields the compact Compound<T, RANK> view type), but the dense source
// is a Blade array value rather than a NetCDF variable. The mask must be a
// bool array; compoundViewType already accepts ETBool masks. Lowering records
// (denseIR, maskIR) in CompoundInits; codegen materializes the index (P0,
// genCompoundIndexFromMask), scatters dense -> compact, and bundles a
// Compound<T, RANK> wrapper.


and inferZip (env: TypeEnv) exprs : TypeResult<TypedExpr> =
    exprs |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tExprs ->
        // Zip produces an array with tuple element type, shared prefix index space.
        // zip(A : T1^r1, B : T2^r2) -> Array<Tuple(T1,T2), min(r1,r2), shared_indices>
        let arrayTypes =
            tExprs |> List.choose (fun te ->
                match env.Subst.Resolve te.Type with
                | ArrayElem at -> Some at
                | _ -> None)
        if arrayTypes.Length = tExprs.Length && arrayTypes.Length >= 2 then
            // All inputs are arrays — build proper zip type
            let minRank = arrayTypes |> List.map (fun at -> at.IndexTypes.Length) |> List.min
            let sharedIndices = arrayTypes.[0].IndexTypes |> List.take minRank
            // Element types: for rank-equal arrays, the elem itself; for higher-rank, remaining slice
            let elemTypes =
                arrayTypes |> List.map (fun at ->
                    let extra = at.IndexTypes |> List.skip minRank
                    if extra.IsEmpty then at.ElemType
                    else mkArrayLike { at with IndexTypes = extra })
            let tupleElemType =
                match elemTypes with
                | [single] -> single  // degenerate: single-array zip
                | _ -> IRTTuple elemTypes
            // Infer a shared ElemType tag for the IRArrayType wrapper
            // We use ETFloat64 as placeholder since the real element is a tuple
            let zipArrayType =
                mkArrayArrow sharedIndices (IRTScalar ETFloat64) None  // placeholder; real elem is the tuple
            Ok (mkTyped (TExprZip tExprs) zipArrayType)
        else
            // Fallback: not all arrays, or fewer than 2 — return tuple type
            Ok (mkTyped (TExprZip tExprs) (IRTTuple (tExprs |> List.map (fun e -> e.Type)))))


and inferExtents (env: TypeEnv) array : TypeResult<TypedExpr> =
    inferExpr env array |> Result.bind (fun tArr ->
        requireArrayArg env tArr "extents" |> Result.bind (fun arrTy ->
            if arrTy.IndexTypes.Length = 1 then
                Ok (mkTyped (TExprExtents tArr) (IRTScalar ETInt64))
            else
                // extents() is static-first: it answers from the ARGUMENT
                // TYPE when possible (IRExtent emits a literal for
                // statically-evaluable extents). A ragged-family slot has
                // no scalar extent AT ALL -- its extent is a per-row
                // function of the outer position -- so the multi-rank
                // tuple form is statically ill-posed for such arrays
                // (the runtime fallback would read a meaningless 0
                // placeholder). Reject with guidance instead.
                let raggedFamilySlot =
                    arrTy.IndexTypes |> List.exists (fun ix ->
                        match ix.IxKind with
                        | IxKRagged | IxKRaggedInline | IxKRaggedOpaque
                        | IxKDepInner
                        | IxKGroupOuter | IxKGroupMember
                        | IxKCompound | IxKCompoundDynamic -> true
                        | _ -> false)
                if raggedFamilySlot then
                    Error (Other "extents() on a ragged, grouped, or multi-rank compound array has no scalar answer for the masked/ragged dimensions. Use extents(row) on a peeled or indexed row, the lengths array, or extents on a rank-1 compound (which is its cardinality).")
                else
                // Multi-rank: tuple of Int64s, one per dimension
                let n = arrTy.IndexTypes.Length
                let tupleTy = IRTTuple (List.replicate n (IRTScalar ETInt64))
                Ok (mkTyped (TExprExtents tArr) tupleTy)))



and inferSequence (env: TypeEnv) exprs : TypeResult<TypedExpr> =
    exprs |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tExprs ->
        match tExprs with
        | [] -> Ok (mkTyped (TExprSequence []) IRTUnit)
        | [single] -> Ok single  // sequence(c) ≡ c
        | _ ->
            // Unify all element types — sequence is homogeneous
            let elemType = (List.head tExprs).Type
            let unifyResults =
                tExprs |> List.tail |> List.fold (fun acc e ->
                    acc |> Result.bind (fun () -> unify env.Subst elemType e.Type)) (Ok ())
            unifyResults |> Result.bind (fun () ->
                let resolved = env.Subst.Resolve(elemType)
                let n = tExprs.Length
                // Create anonymous Idx<N> for the sequence dimension
                let seqIdx = {
                    Id = env.Builder.FreshId()
                    Rank = 1
                    Extent = IRLit (IRLitInt (int64 n))
                    Symmetry = SymNone
                    Tag = Some "__seq"; IxKind = IxKSeq
                    Kind = SDimension
                    Dependencies = []
                }
                // Result type: prepend Idx<N> to the element type
                let resultType =
                    match resolved with
                    | ArrayElem arrTy ->
                        // Array elements: Idx<N> × inner index types
                        mkArrayLike { arrTy with IndexTypes = seqIdx :: arrTy.IndexTypes }
                    | IRTScalar et ->
                        // Scalar elements: simple array Idx<N> → scalar
                        mkArrayArrow [seqIdx] (IRTScalar et) None
                    | _ ->
                        // S3 tag: relic. Fallback to Float64 array for non-array,
                        // non-scalar resolved types — should arguably be a typecheck
                        // error, but preserving prior behavior.
                        mkArrayArrow [seqIdx] (IRTScalar ETFloat64) None
                Ok (mkTyped (TExprSequence tExprs) resultType)))

and inferBinOp env mode op left right : TypeResult<TypedExpr> =
    match op with
    | OpApply ->
        // A bare named-function reference on the kernel side (the right
        // operand of <@>) is eta-expanded to lambda(__k..) -> f(__k..) so it
        // matches the existing TExprLambda kernel arm in inferApply.
        let rightResult =
            match etaExpandFunctionKernel env right with
            | Some r -> r
            | None -> inferExpr env right
        inferExpr env left |> Result.bind (fun tL ->
        rightResult |> Result.bind (fun tR ->
            inferApply env tL tR))

    | OpBind ->
        // >>= : Computation α × (α → Computation β) → Computation β
        // Result type is the return type of the continuation
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            // Path 1 follow-on: propagate type info from the computation
            // into the continuation. Without this, a continuation like
            // `lambda(arr) -> method_for(arr) <@> ...` has arr left as
            // IRTInfer (because the body's `method_for(arr)` doesn't
            // pin it), and codegen subsequently emits arr's type as
            // a default scalar — which doesn't match the array-typed
            // computation flowing in. Unify the continuation's first
            // param with α (the computation's element type, unwrapping
            // an explicit IRTComputation if present).
            let alpha =
                match tL.Type with
                | IRTComputation t -> t
                | t -> t
            let unifyResult =
                match tR.Type with
                | FuncElem (paramTys, _) when paramTys.Length >= 1 ->
                    unify env.Subst (List.head paramTys) alpha
                | _ -> Ok ()
            unifyResult |> Result.bind (fun () ->
                let resultType =
                    match tR.Type with
                    | FuncElem (_, retType) -> retType  // k : α → β, result is β
                    | _ -> tR.Type  // If not a function, use right's type directly
                Ok (mkTyped (TExprBind (tL, tR)) resultType))))

    | OpParallel ->
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            Ok (mkTyped (TExprParallel (tL, tR)) (IRTTuple [tL.Type; tR.Type]))))

    | OpFusion ->
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            Ok (mkTyped (TExprFusion (tL, tR)) (IRTTuple [tL.Type; tR.Type]))))

    | OpFunctor ->
        // <$> : (α → β) × Computation α → Computation β
        // f <$> c  transforms the result of computation c by applying f
        inferExpr env left |> Result.bind (fun tF ->
        inferExpr env right |> Result.bind (fun tC ->
            // Output type: same array shape, element type from f's return
            let outputType =
                match tF.Type, tC.Type with
                | FuncElem (_, IRTScalar et), ArrayElem arr ->
                    // Array with updated element type. Wrap et as IRTScalar
                    // since arr.ElemType is IRType post-B2.
                    mkArrayLike { arr with ElemType = IRTScalar et }
                | FuncElem (_, retTy), _ -> retTy
                | _ -> tC.Type  // fallback: preserve computation type
            Ok (mkTyped (TExprFunctorMap (tF, tC)) outputType)))

    | OpArrayProd ->
        // <*> : MethodLoop × MethodLoop → MethodLoop (concatenate array lists)
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            // Resolve both sides to find TExprMethodFor
            let rL = resolveTypedExpr env tL
            let rR = resolveTypedExpr env tR
            match rL.Kind, rR.Kind with
            | TExprMethodFor m1, TExprMethodFor m2 ->
                // Merge into single TExprMethodFor with concatenated arrays
                let merged : TypedMethodForInfo = {
                    Arrays = m1.Arrays @ m2.Arrays
                    Identities = m1.Identities @ m2.Identities
                    ArrayTypes = m1.ArrayTypes @ m2.ArrayTypes
                    SDimsPerArray = m1.SDimsPerArray @ m2.SDimsPerArray
                    TotalSDims = m1.TotalSDims + m2.TotalSDims
                    SharedIndexTypes = []
                }
                let loopTy = IRTLoop {
                    Kind = LKMethod
                    Arity = Some (m1.Arrays.Length + m2.Arrays.Length)
                    ArrayTypes = (m1.ArrayTypes @ m2.ArrayTypes) |> List.map mkArrayLike
                    KernelType = None
                }
                Ok (mkTyped (TExprMethodFor merged) loopTy)
            | _ ->
                // Fallback: produce BinOp for non-method_for operands
                let arity =
                    match tL.Type, tR.Type with
                    | IRTLoop l1, IRTLoop l2 -> 
                        match l1.Arity, l2.Arity with
                        | Some a, Some b -> Some (a + b)
                        | _ -> None
                    | _ -> None
                let loopTy = IRTLoop {
                    Kind = LKMethod; Arity = arity
                    ArrayTypes = []; KernelType = None
                }
                Ok (mkTyped (TExprBinOp (mode, op, tL, tR)) loopTy)))

    | OpChoice ->
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            let _ = unify env.Subst tL.Type tR.Type
            Ok (mkTyped (TExprChoice (tL, tR)) tL.Type)))

    // <|:> allocated-fallback (formalism 2.6): read A where its STORAGE holds
    // the cell, else B. Storage-keyed, unlike <|>'s value-keyed zero test —
    // an allocated zero survives fallback but not choice.
    //   * compound-left: the CompoundIdx mask IS the allocation record (absent
    //     cells have no storage). Result = the dense expansion: B's type, with
    //     A overlaid on present cells. B must be dense over the compound's
    //     underlying dims (+ trailing dims); extent agreement vs the runtime
    //     mask is guarded in generated code.
    //   * dense-left: allocation = the nested-pointer chain, checked per curry
    //     level in codegen (nullptr-robust reads; compiler-built arrays are
    //     fully allocated — partially-allocated arrays arrive via the
    //     C++-level partial-depth allocation API). Result = A's type.
    // Scalars/computations/loop objects reject (steer to <|>); symmetric left
    // rejects (symmetric allocation is not verifiable — v1).
    | OpFallback ->
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            let describe (t: IRType) =
                match env.Subst.Resolve t with
                | ArrayElem _ -> "an array"
                | IRTScalar _ -> "a scalar"
                | IRTComputation _ -> "a computation"
                | IRTLoop _ -> "a loop object"
                | _ -> "a non-array value"
            let isPlainDense (a: IRArrayType) =
                a.IndexTypes |> List.forall (fun ix ->
                    ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank <= 1)
            let hasSym (a: IRArrayType) =
                a.IndexTypes |> List.exists (fun ix -> ix.Symmetry <> SymNone)
            match env.Subst.Resolve tL.Type, env.Subst.Resolve tR.Type with
            | ArrayElem aL, ArrayElem aR ->
                if hasSym aL then Error FallbackSymmetricLeft
                elif not (isPlainDense aR) then
                    Error (FallbackRightNotDense
                            (if hasSym aR then "a symmetric array" else "a compound/non-dense array"))
                else
                    (match aL.IndexTypes with
                     | head :: trailing when head.IxKind = IxKCompound ->
                         // Compound-left: B spans the mask's underlying dims
                         // plus A's trailing regular dims.
                         let leftSpan = head.Rank + trailing.Length
                         let rightRank = aR.IndexTypes.Length
                         if rightRank <> leftSpan then
                             Error (FallbackRankMismatch (leftSpan, rightRank))
                         else
                             unify env.Subst aL.ElemType aR.ElemType
                             |> Result.map (fun () ->
                                 mkTyped (TExprFallback (tL, tR)) tR.Type)
                     | _ when isPlainDense aL ->
                         // Dense-left: same index space required outright.
                         unify env.Subst tL.Type tR.Type
                         |> Result.map (fun () ->
                             mkTyped (TExprFallback (tL, tR)) tL.Type)
                     | _ ->
                         Error (Other "<|:> left operand must be a plain dense array or a compound(A, mask) array; ragged/dynamic-compound left operands are not supported."))
            | _ ->
                Error (FallbackNeedsArrays (describe tL.Type, describe tR.Type))))

    | OpComposeMeth ->
        // @>> : Computation α × Computation β → Computation β
        // Result type is the right side's type
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            Ok (mkTyped (TExprCompose (op, tL, tR)) tR.Type)))

    | OpComposeObj ->
        // >>@ : ObjectLoop × ObjectLoop → ObjectLoop
        // Preserve as loop type so inferApply can handle application
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            // Result type: preserve right side's loop type (since g determines output shape)
            let resultType = 
                match tR.Type with
                | IRTLoop _ -> tR.Type
                | _ -> tL.Type
            Ok (mkTyped (TExprCompose (OpComposeObj, tL, tR)) resultType)))

    | OpCompose ->
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            // f >> g : (A → B) >> (B → C) = (A → C)
            match env.Subst.Resolve(tL.Type), env.Subst.Resolve(tR.Type) with
            | FuncElem (fArgs, fRet), FuncElem (gArgs, gRet) ->
                // Unify f's return type with g's parameter type(s)
                match gArgs with
                | [gArg] -> 
                    let _ = unify env.Subst fRet gArg
                    Ok (mkTyped (TExprCompose (op, tL, tR)) (mkFuncArrow fArgs gRet))
                | _ ->
                    // Multi-arg g: unify f's return (should be tuple) with g's args as tuple
                    let _ = unify env.Subst fRet (IRTTuple gArgs)
                    Ok (mkTyped (TExprCompose (op, tL, tR)) (mkFuncArrow fArgs gRet))
            | FuncElem _, _ ->
                eprintfn "Warning: right side of >> should be a function"
                Ok (mkTyped (TExprCompose (op, tL, tR)) tR.Type)
            | _, FuncElem (gArgs, gRet) ->
                // f might be a fresh/unresolved type — permissive
                Ok (mkTyped (TExprCompose (op, tL, tR)) (mkFuncArrow gArgs gRet))
            | _ ->
                // Both unresolved — permissive, return fresh
                Ok (mkTyped (TExprCompose (op, tL, tR)) (env.Subst.Fresh()))))

    | OpCons ->
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            let resTy = match tR.Type with
                         | IRTTuple ts -> IRTTuple (tL.Type :: ts)
                         | _ -> IRTTuple [tL.Type; tR.Type]
            Ok (mkTyped (TExprBinOp (mode, op, tL, tR)) resTy)))

    | _ ->
        // Arithmetic, comparison, logical
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            let lRes = env.Subst.Resolve tL.Type
            let rRes = env.Subst.Resolve tR.Type
            let isDist t = match t with IRTDist _ -> true | _ -> false
            if isDist lRes || isDist rRes then
                // Typed-Dist operator dispatch (checker-level; the surface
                // operand exprs are re-synthesized into the expansion, so
                // this works in any expression position — see inferDistBinOp).
                inferDistBinOp env op left right lRes rRes
            else
            // Elementwise op on TWO ARRAYS: re-synthesize as the zip
            // co-iteration pipeline — method_for(zip(l, r)) <@>
            // lambda(u, w) -> u op w |> compute — and re-infer
            // (synthesize-and-infer, the inferDistBinOp pattern). The
            // direct TExprBinOp lowering hand-rolled a flat rank-1
            // object_for loop, which mis-iterates symmetry-PACKED storage
            // (row pointers into a scalar kernel — silent miscompile) and
            // any rank > 1 operand; the co-iteration builder handles
            // packed, dense, and multi-rank uniformly. Outer mode ([+])
            // keeps its cross-iteration path.
            let bothArrays =
                (match lRes with ArrayElem _ -> true | _ -> false)
                && (match rRes with ArrayElem _ -> true | _ -> false)
            let isZipOp =
                match op with
                | OpAdd | OpSub | OpMul | OpDiv | OpMod | OpCaret
                | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe
                | OpAnd | OpOr -> true
                | _ -> false
            // Unit judgment for the synthesized kernel paths below happens
            // at THIS site, not in the kernel body: the lambda parameters
            // are still unresolved inference variables when the body's
            // binop is inferred (they only unify with the element types
            // later, in buildApplyInfo), so inferArithType's unit rules
            // see no units there. The operand ELEMENT units are visible
            // here — unitRulesForOp checks/composes them and the result
            // element type is stamped over the inferred pipeline type.
            let elemUnits t =
                match t with ArrayElem at -> IR.getUnits at.ElemType | _ -> None
            if mode = Elementwise && bothArrays && isZipOp then
                // Zip-able operand shapes: one index record per operand (dense
                // rank-1, or packed symmetry-class storage of any logical rank —
                // the co-iteration walks its flat canonical cells), or BOTH
                // operands multi-record all-plain-dense with structurally
                // matching shapes (dense rank ≥ 2 — the co-iteration spans the
                // full product of the shared records). Mismatched or mixed
                // dense/packed multi-axis shapes reject clearly rather than
                // letting codegen emit a loop-object error.
                let zipable =
                    match lRes, rRes with
                    | ArrayElem aL, ArrayElem aR ->
                        (aL.IndexTypes.Length = 1 && aR.IndexTypes.Length = 1)
                        || (aL.IndexTypes |> List.forall isPlainDenseIx
                            && aR.IndexTypes |> List.forall isPlainDenseIx
                            && indexShapesAgree aL.IndexTypes aR.IndexTypes)
                    | _ -> false
                if zipable then
                    match unitRulesForOp op (elemUnits lRes) (elemUnits rRes) with
                    | Error e -> Error e
                    | Ok resUnits ->
                        let sp = mergeSpan left.Span right.Span
                        let kbody = mkExpr sp (ExprBinOp (Elementwise, op, mkExpr sp (ExprVar "__zl"), mkExpr sp (ExprVar "__zr")))
                        let klam = mkExpr sp (ExprLambda ([{ Name = "__zl"; Type = None }; { Name = "__zr"; Type = None }], None, kbody))
                        let kzip = mkExpr sp (ExprMethodFor [mkExpr sp (ExprZip [left; right])])
                        let synth = mkExpr sp (ExprCompute (mkExpr sp (ExprBinOp (Elementwise, OpApply, kzip, klam))))
                        inferExpr env synth |> Result.map (stampElemUnits env resUnits)
                else
                    Error (Other "elementwise operators on multi-axis arrays require both operands to have matching plain-dense index shapes (same axis tags and extents); mixed dense/packed or mismatched shapes are not zip-able")
            else
            // Elementwise op on ARRAY <-> SCALAR (`A + a`, `2.0 / A`,
            // `A > t`): re-synthesize as a 1-param kernel map over the array
            // operand — method_for(A) <@> lambda(__bx) -> __bx op s |>
            // compute — the same synthesize-and-infer route as the both-array
            // zip above. Embedding the scalar operand's SURFACE expr in the
            // lambda body lets capture analysis see its variable references,
            // so the lifted kernel receives them as explicit capture params
            // and emits at file scope (forward-declared). The historical
            // lowering-side partial-application kernel embedded the lowered
            // scalar IR directly; a variable reference there was a free var,
            // which forced a main-local std::function emitted AFTER its use
            // site — invalid C++ (use before declaration).
            let scalarish t =
                match t with IRTScalar _ -> true | _ -> false
            let arrayScalar =
                match lRes, rRes with
                | ArrayElem _, r when scalarish (IR.stripUnits r) -> Some true    // array on left
                | l, ArrayElem _ when scalarish (IR.stripUnits l) -> Some false   // array on right
                | _ -> None
            match arrayScalar with
            | Some arrayOnLeft when mode = Elementwise && isZipOp ->
                // Same synthesis-site unit judgment as the zip path above:
                // the kernel param annotation deliberately strips units
                // (elemAnn below), so the body's binop checks nothing —
                // judge the array's ELEMENT units against the scalar
                // operand's units here, in operand order.
                let arrU = elemUnits (if arrayOnLeft then lRes else rRes)
                let scalU = IR.getUnits (if arrayOnLeft then rRes else lRes)
                let luB, ruB = if arrayOnLeft then (arrU, scalU) else (scalU, arrU)
                match unitRulesForOp op luB ruB with
                | Error e -> Error e
                | Ok resUnits ->
                let sp = mergeSpan left.Span right.Span
                let (arrExpr, body) =
                    if arrayOnLeft then (left, mkExpr sp (ExprBinOp (Elementwise, op, mkExpr sp (ExprVar "__bx"), right)))
                    else (right, mkExpr sp (ExprBinOp (Elementwise, op, left, mkExpr sp (ExprVar "__bx"))))
                // Annotate the kernel param with the array's element type:
                // at body-inference time an unannotated param is still an
                // unresolved infer var, and inferArithType's promotion rules
                // would fall back to the scalar side's type (`a * A` would
                // type Int64 elements for a double-computing body).
                let elemAnn =
                    match (if arrayOnLeft then lRes else rRes) with
                    | ArrayElem arr ->
                        match IR.stripUnits arr.ElemType with
                        | IRTScalar ETFloat64 -> Some TyFloat64
                        | IRTScalar ETFloat32 -> Some TyFloat32
                        | IRTScalar ETInt64 -> Some TyInt64
                        | IRTScalar ETInt32 -> Some TyInt32
                        | IRTScalar ETBool -> Some TyBool
                        | IRTScalar ETComplex64 -> Some TyComplex64
                        | IRTScalar ETComplex128 -> Some TyComplex128
                        | IRTScalar ETString -> Some TyString
                        | _ -> None
                    | _ -> None
                let synth =
                    mkExpr sp (ExprCompute (mkExpr sp (ExprBinOp (Elementwise, OpApply,
                        mkExpr sp (ExprMethodFor [arrExpr]),
                        mkExpr sp (ExprLambda ([{ Name = "__bx"; Type = elemAnn }], None, body))))))
                inferExpr env synth |> Result.map (stampElemUnits env resUnits)
            | _ ->
            inferArithType mode op tL.Type tR.Type |> Result.map (fun resTy ->
                mkTyped (TExprBinOp (mode, op, tL, tR)) resTy)))

/// Checker-level Dist operator dispatch (typed-Dist arc phase 4).
/// Scalar * Dist (either side) is κ_k(c·X) = c^k κ_k(X) — pure
/// multilinearity, exact with NO independence requirement — so it
/// dispatches in ANY expression position, including on Dist-typed function
/// parameters: the surface operand exprs are packed into the expansion
/// DistSynth.scaleExpr builds, and the whole block is re-inferred
/// (synthesize-and-infer). Dist ± Dist is exact ONLY for independent
/// operands; until function-boundary independence licenses land
/// (`where indep(...)`, the next phase), provenance is invisible in
/// checker positions, so +/− here steers to the module-level elaboration
/// path (which checks declared independence). dist * dist steers to the
/// Wick machinery message.
and inferDistBinOp (env: TypeEnv) (op: BinOp) (left: Expr) (right: Expr) (lTy: IRType) (rTy: IRType) : TypeResult<TypedExpr> =
    let isScalarish t =
        match t with
        | IRTScalar _ | IRTUnitAnnotated (IRTScalar _, _) | IRTInfer _ -> true
        | _ -> false
    match op, lTy, rTy with
    | OpMul, IRTDist _, IRTDist _ ->
        Error (Other "dist * dist is not defined: cumulants are additive under independent sums and multilinear under scalar scaling; products of random variables need the moment (Wick/Faà di Bruno) machinery")
    | OpMul, IRTDist (order, _, _), c when isScalarish c ->
        inferExpr env (Blade.Ppl.Elaborate.DistSynth.scaleExpr (env.Builder.FreshId()) right left order)
    | OpMul, c, IRTDist (order, _, _) when isScalarish c ->
        inferExpr env (Blade.Ppl.Elaborate.DistSynth.scaleExpr (env.Builder.FreshId()) left right order)
    | (OpAdd | OpSub), IRTDist (lo, _, _), IRTDist (ro, _, _) ->
        // Exact ONLY for independent operands: every cross pair of the two
        // provenance sets must be related under the declared relation ∪ the
        // active `where indep` licenses (PPL-owned state). Empty provenance
        // is un-provable, not vacuously independent.
        if lo <> ro then
            Error (DistOrderDisagree ((if op = OpAdd then "+" else "-"), lo, ro))
        else
        let provL = provenanceOfSurface env left
        let provR = provenanceOfSurface env right
        if Set.isEmpty provL || Set.isEmpty provR then
            Error (Other "dist + / -: cannot establish the operands' provenance — combine dist bindings (or expressions built from them) so independence of their sources can be verified")
        else
            let missing =
                [ for s1 in provL do
                    for s2 in provR do
                      if not (Blade.Ppl.Elaborate.Independence.isRelated s1 s2) then yield (s1, s2) ]
            match missing with
            | (s1, s2) :: _ ->
                // Token-shaped sources ("func.param") mean unlicensed
                // parameters — steer to the signature license, not to a
                // module-level declaration over internal token names.
                let steering =
                    if s1.Contains "." || s2.Contains "." then
                        "add a `where <alias>.indep(...)` license (with `import ppl as <alias>`) naming the two parameters to the enclosing function's signature"
                    else
                        sprintf "declare `let _ = ppl.independent(%s, %s)` (module level) or a struct `where ppl.indep(...)`" s1 s2
                Error (DistNotIndependent ((if op = OpAdd then "+" else "-"), s1, s2, steering))
            | [] ->
                let weight = if op = OpAdd then (fun _ -> 1.0) else (fun k -> if k % 2 = 0 then 1.0 else -1.0)
                inferExpr env (Blade.Ppl.Elaborate.DistSynth.combineExpr (env.Builder.FreshId()) weight left right lo)
    | _ ->
        Error (DistOpUndefined (ppIRType lTy, ppIRType rTy))

/// Unit rules for one binary op, shared by scalar arithmetic
/// (inferArithType) and the array kernel-synthesis paths in inferBinOp
/// (both-array zip, array<->scalar broadcast). The synthesized kernels
/// infer their bodies against unresolved parameter types — the params
/// only unify with the element types later, in buildApplyInfo — so the
/// unit judgment must happen at the synthesis site, where the operand
/// units are still visible. Returns the RESULT unit signature (None =
/// no annotation): +/−/comparison require agreement, * and / compose
/// signatures, everything else (^, &&, ||, ...) drops units.
and unitRulesForOp (op: BinOp) (lUnits: UnitSig option) (rUnits: UnitSig option) : TypeResult<UnitSig option> =
    match op with
    | OpAdd | OpSub ->
        match lUnits, rUnits with
        | Some lu, Some ru ->
            if unitCompatible lu ru then Ok (Some lu)
            else Error (UnitMismatch ((if op = OpAdd then "addition" else "subtraction"), ppUnitSig lu, ppUnitSig ru))
        | Some u, None | None, Some u -> Ok (Some u)
        | None, None -> Ok None
    | OpMul ->
        match lUnits, rUnits with
        | Some lu, Some ru -> Ok (Some (unitMul lu ru))
        | Some u, None | None, Some u -> Ok (Some u)
        | None, None -> Ok None
    | OpDiv | OpMod ->
        match lUnits, rUnits with
        | Some lu, Some ru -> Ok (Some (unitDiv lu ru))
        | Some u, None -> Ok (Some u)
        | None, Some u -> Ok (Some (unitDiv unitDimensionless u))
        | None, None -> Ok None
    | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe ->
        match lUnits, rUnits with
        | Some lu, Some ru when not (unitCompatible lu ru) ->
            Error (UnitMismatch ("comparison", ppUnitSig lu, ppUnitSig ru))
        | _ -> Ok None
    | _ -> Ok None

/// Overwrite the ELEMENT unit annotation of an array-typed result from a
/// synthesized kernel pipeline with the signature the unit rules computed.
/// Without this the kernel return type leaks the LEFT operand's unit
/// through * and / (meters * meters would stay meters). None strips —
/// comparisons produce Bool elements and ^ drops units like the scalar
/// path.
and stampElemUnits env (resUnits: UnitSig option) (t: TypedExpr) : TypedExpr =
    match env.Subst.Resolve t.Type with
    | ArrayElem arr ->
        let bare = IR.stripUnits arr.ElemType
        let elem =
            match resUnits with
            | Some u -> IRTUnitAnnotated (bare, u)
            | None -> bare
        { t with Type = mkArrayLike { arr with ElemType = elem } }
    | _ -> t

/// Unit rules for the unary and math-intrinsic ops, shared by
/// scalar-position intrinsic inference (the ExprApp intrinsic arms) and
/// the kernel-body walk below: negation, abs/floor/ceil and the complex
/// component projections preserve the signature; sqrt halves the
/// exponents when they are all even (sqrt(meters^2) = meters — an odd
/// exponent has no integer-signature representation, so the claim is
/// dropped); everything else (transcendentals, logical not, phase angle)
/// is dimensionless-out.
and unitRulesForUnaryOp (op: UnaryOp) (u: UnitSig option) : UnitSig option =
    match op with
    | OpNeg | OpConj | OpReal | OpImag -> u
    | OpNot | OpArg -> None
    | OpMath ("abs" | "floor" | "ceil") -> u
    | OpMath "sqrt" ->
        match u with
        | Some s ->
            let n = unitNormalize s
            if n |> Map.forall (fun _ ex -> ex % 2 = 0)
            then Some (n |> Map.map (fun _ ex -> ex / 2))
            else None
        | None -> None
    | OpMath _ -> None

/// Unit-only second pass over a KERNEL BODY, run by buildApplyInfo after
/// parameter unification has bound the parameter types. The body was
/// type-inferred while its params were still unresolved inference
/// variables, so the cached node types carry no unit information (or a
/// leaked left-operand annotation) — this walk recomputes the signature
/// bottom-up with the same per-op table the scalar path uses
/// (unitRulesForOp), so `t * t` over meters elements comes out meters^2
/// and a mismatched `a + b` over a hand-written zip REJECTS. `bound`
/// carries walk-computed signatures for kernel-local lets (their cached
/// var types are as stale as the intermediate nodes). Constructs the walk
/// doesn't model return None (no claim) — only op rules error.
and kernelBodyUnits (env: TypeEnv) (bound: Map<IRId, UnitSig option>) (e: TypedExpr) : TypeResult<UnitSig option> =
    let combineBranches context (a: UnitSig option) (b: UnitSig option) =
        match a, b with
        | Some ua, Some ub ->
            if unitCompatible ua ub then Ok (Some ua)
            else Error (UnitMismatch (context, ppUnitSig ua, ppUnitSig ub))
        | Some u, None | None, Some u -> Ok (Some u)
        | None, None -> Ok None
    let ofType (t: IRType) = IR.getUnits (env.Subst.Resolve t)
    let elemOfType (t: IRType) =
        match env.Subst.Resolve t with
        | ArrayElem at -> IR.getUnits at.ElemType
        | resolved -> IR.getUnits resolved
    // Walk a subtree only for its ERRORS (mismatched ops inside call args,
    // assignment right-hand sides, conditions), discarding the signature.
    let errorsOnly sub = kernelBodyUnits env bound sub |> Result.map ignore
    match e.Kind with
    | TExprLit _ | TExprComplexLit _ -> Ok None
    | TExprVar (_, varId, _) ->
        match Map.tryFind varId bound with
        | Some u -> Ok u
        | None -> Ok (ofType e.Type)
    | TExprBinOp (_, op, l, r) ->
        kernelBodyUnits env bound l |> Result.bind (fun lu ->
        kernelBodyUnits env bound r |> Result.bind (fun ru ->
        unitRulesForOp op lu ru))
    | TExprUnaryOp (op, inner) ->
        kernelBodyUnits env bound inner |> Result.map (unitRulesForUnaryOp op)
    | TExprIf (cond, thenBr, elseBr) ->
        errorsOnly cond |> Result.bind (fun () ->
        kernelBodyUnits env bound thenBr |> Result.bind (fun tu ->
        kernelBodyUnits env bound elseBr |> Result.bind (fun eu ->
        combineBranches "conditional branches" tu eu)))
    | TExprLet (_, varId, value, body) ->
        kernelBodyUnits env bound value |> Result.bind (fun vu ->
        kernelBodyUnits env (Map.add varId vu bound) body)
    | TExprBlock (stmts, finalOpt) ->
        let foldStmt acc stmt =
            acc |> Result.bind (fun (b: Map<IRId, UnitSig option>) ->
                match stmt with
                | TStmtLet binding ->
                    kernelBodyUnits env b binding.Value
                    |> Result.map (fun vu -> Map.add binding.VarId vu b)
                | TStmtAssign (_, rhs) ->
                    kernelBodyUnits env b rhs |> Result.map (fun _ -> b)
                | TStmtExpr sub ->
                    kernelBodyUnits env b sub |> Result.map (fun _ -> b)
                | TStmtForIn _ -> Ok b)
        stmts |> List.fold foldStmt (Ok bound) |> Result.bind (fun b ->
            match finalOpt with
            | Some f -> kernelBodyUnits env b f
            | None -> Ok None)
    | TExprApp (_, args) ->
        args |> List.fold (fun acc a ->
            acc |> Result.bind (fun () -> errorsOnly a)) (Ok ())
        |> Result.map (fun () -> None)
    | TExprIndex (arr, _, _) -> Ok (elemOfType arr.Type)
    | TExprReduce (arr, _, _) -> Ok (elemOfType arr.Type)
    | TExprField _ | TExprTupleIndex _ -> Ok (ofType e.Type)
    | _ -> Ok None

and inferArithType mode op leftTy rightTy : TypeResult<IRType> =
    // Elementwise boolean/comparison over two ARRAYS lifts to a Bool-element
    // array of the same shape (mask algebra: `above2 && below5`, `A < B`).
    // The lowering already synthesizes the object_for with a Bool kernel
    // (lowerTypedBinOp maps these ops and sets kernelRetType = ETBool), so only
    // the RESULT TYPE needs to become the array here. Scalars keep scalar Bool;
    // Outer mode and array<->scalar broadcast are intentionally out of scope for
    // now and also keep scalar Bool.
    let elementwiseBoolTy =
        match mode, IR.stripUnits leftTy, IR.stripUnits rightTy with
        | Elementwise, ArrayElem arrL, ArrayElem _ ->
            mkArrayLike { arrL with ElemType = IRTScalar ETBool }
        | Elementwise, ArrayElem arrL, _ ->
            // array <op> scalar broadcast (`A > 2.0`): result shape follows the array
            mkArrayLike { arrL with ElemType = IRTScalar ETBool }
        | Elementwise, _, ArrayElem arrR ->
            // scalar <op> array broadcast (`2.0 < A`): result shape follows the array
            mkArrayLike { arrR with ElemType = IRTScalar ETBool }
        | _ -> IRTScalar ETBool
    match op with
    | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe ->
        // Comparisons require compatible units (unitRulesForOp errors on
        // mismatch; the result carries no annotation)
        unitRulesForOp op (IR.getUnits leftTy) (IR.getUnits rightTy)
        |> Result.map (fun _ -> elementwiseBoolTy)
    | OpAnd | OpOr -> Ok elementwiseBoolTy
    | _ ->
        // Extract unit annotations if present
        let lUnits = IR.getUnits leftTy
        let rUnits = IR.getUnits rightTy
        let lBare = IR.stripUnits leftTy
        let rBare = IR.stripUnits rightTy
        // No arithmetic on index types (named OR anonymous). Per the
        // formalism's nominal-type discipline, index types are nominal
        // labels — arithmetic on them serves no useful purpose:
        // (1) value-level position arithmetic is reachable via virtual
        // array iteration, which produces plain ints; (2) type-level
        // construction of new index types from arithmetic is a separate
        // (deferred) workstream. So we simply reject. Post-Option-C, all
        // index references (named or anonymous, value or element position)
        // are represented uniformly as IRTIdxTagged.
        // Floats: by the same principle, index types are completely
        // incompatible with floating point — no `Idx + Float` either.
        let isIndexType t =
            match t with
            | IRTIdxTagged _ -> true
            | _ -> false
        let indexTypeName t =
            match t with
            | IRTIdxTagged (_, IRefNamed n) -> n
            | IRTIdxTagged (_, IRefAnon _) -> "<anonymous Idx>"
            | _ -> "?"
        let indexArithErr =
            match lBare, rBare with
            | IRTIdxTagged (_, IRefNamed ln), IRTIdxTagged (_, IRefNamed rn) when ln <> rn ->
                Some (CrossNominalIndexArith (ln, rn))
            | IRTIdxTagged (_, IRefNamed ln), IRTIdxTagged (_, IRefNamed rn) when ln <> rn ->
                Some (CrossNominalIndexArith (ln, rn))
            | IRTIdxTagged (_, IRefAnon (lid, _)), IRTIdxTagged (_, IRefAnon (rid, _)) when lid <> rid ->
                Some (CrossAnonIndexArith (lid, rid))
            | l, r when isIndexType l || isIndexType r ->
                let n = if isIndexType l then indexTypeName l else indexTypeName r
                Some (IndexTypeArithForbidden n)
            | _ -> None
        match indexArithErr with
        | Some err -> Error err
        | None ->
        // Dist operands: checker-level operator dispatch (per-order cumulant
        // combination — + adds, − flips odd orders, scalar * is c^k
        // multilinearity, all independence-gated) is the typed-Dist arc's
        // phase 4. Until it lands, module-level dist operators still go
        // through the elaboration rewrites (which never reach here); any
        // OTHER position — notably operators on Dist-typed function
        // parameters — must error with steering rather than fall through
        // to scalar promotion and silently type nonsense.
        match lBare, rBare with
        | IRTDist _, _ | _, IRTDist _ ->
            Error (Other "operators on Dist values are not yet typed in this position (checker-level dist operator dispatch is in progress): combine dists where they are constructed (module-level d1 + d2, d1 - d2, c * d), or project a component with cumulant(d, k)")
        | _ ->
        // (Both-array Elementwise ops never reach here anymore: inferBinOp
        // re-synthesizes them as the zip co-iteration pipeline, which
        // handles packed and multi-rank storage the plain lowering could
        // not.)
        // Element-type promotion for array<->scalar broadcast: same rule as
        // the scalar-scalar cases below.
        let promoteElem (elemTy: IRType) (scalarTy: IRType) =
            match IR.stripUnits elemTy, IR.stripUnits scalarTy with
            // Complex mixed with real (or mixed-width complex) widens to the
            // appropriate complex type — otherwise the element type would fall
            // back to the real side and the array would be typed real.
            | IRTScalar le, IRTScalar re
                when (match IR.promoteElemType le re with Some (ETComplex64 | ETComplex128) -> true | _ -> false) ->
                IRTScalar (IR.promoteElemType le re |> Option.get)
            | _ ->
                match elemTy, scalarTy with
                | IRTScalar ETFloat64, _ | _, IRTScalar ETFloat64 -> IRTScalar ETFloat64
                | IRTScalar ETFloat32, _ | _, IRTScalar ETFloat32 -> IRTScalar ETFloat32
                | _ -> elemTy
        let bareResult =
            match mode with
            | Outer ->
                match lBare, rBare with
                | ArrayElem arrL, ArrayElem arrR ->
                    mkArrayLike { arrL with IndexTypes = arrL.IndexTypes @ arrR.IndexTypes }
                | _ -> lBare
            | Elementwise ->
                match lBare, rBare with
                // Array <op> scalar / scalar <op> array broadcast (`A + a`,
                // `2.0 / A`): the result follows the array's shape; the
                // ELEMENT type follows scalar promotion against the other
                // operand. Historically these fell into the scalar rules
                // below and typed the whole result as a scalar (or as the
                // bare left type), which codegen then emitted as pointer
                // arithmetic on the Array wrapper. Lowering's broadcast
                // kernel path (lowerTypedBinOp) is the value-side pair of
                // this rule.
                | ArrayElem arrL, (IRTScalar _ as s) ->
                    mkArrayLike { arrL with ElemType = promoteElem arrL.ElemType s }
                | (IRTScalar _ as s), ArrayElem arrR ->
                    mkArrayLike { arrR with ElemType = promoteElem arrR.ElemType s }
                // Scalar complex promotion (mixed real/complex or mixed-width
                // complex): must precede the float rules so complex wins.
                | IRTScalar le, IRTScalar re
                    when (match IR.promoteElemType le re with Some (ETComplex64 | ETComplex128) -> true | _ -> false) ->
                    IRTScalar (IR.promoteElemType le re |> Option.get)
                | IRTScalar ETFloat64, _ | _, IRTScalar ETFloat64 -> IRTScalar ETFloat64
                | IRTScalar ETFloat32, _ | _, IRTScalar ETFloat32 -> IRTScalar ETFloat32
                | _ -> lBare
        // Apply unit rules based on operation (shared with the array
        // kernel-synthesis paths in inferBinOp)
        unitRulesForOp op lUnits rUnits |> Result.map (function
            | Some u -> IRTUnitAnnotated (bareResult, u)
            | None -> bareResult)

// ============================================================================
// 10b. <@> Application with Symmetry Analysis
// ============================================================================

/// Resolve a TypedExpr through variable bindings to find the underlying
/// method_for / object_for / lambda.
and resolveTypedExpr (env: TypeEnv) (texpr: TypedExpr) : TypedExpr =
    match texpr.Kind with
    | TExprVar (name, _, _) ->
        match lookupVar name env with
        | Some info -> info.TypedValue |> Option.defaultValue texpr
        | None -> texpr
    | _ -> texpr

/// Eta-expand a bare named-function reference used in KERNEL position:
///   lkm  ==>  lambda(__k0..__kn) -> lkm(__k0..__kn)
/// A top-level `function` is bound with TypedValue = None (bindVarSimple),
/// so resolveTypedExpr can never surface it as a TExprLambda — hence
/// `method_for(...) <@> lkm` and `object_for(lkm)` never match a kernel arm.
/// This mirrors the prefix partial-application eta-expansion (the FuncElem
/// arm of the ExprApp case) but for the 0-args case that path deliberately
/// excludes, so the synthesized lambda rides the entire existing lambda
/// pipeline (captures, lifting, std::function emission, kernel wrappers).
/// Returns None when `kernelExpr` is not a bare function reference — callers
/// then fall back to their ordinary `inferExpr env kernelExpr`. Gated to
/// kernel positions only, so bare function VALUES elsewhere are unaffected.
and etaExpandFunctionKernel (env: TypeEnv) (kernelExpr: Expr) : TypeResult<TypedExpr> option =
    match kernelExpr.Kind with
    | ExprKind.ExprVar name ->
        match lookupVar name env with
        // TypedValue = None is exactly the case resolveTypedExpr cannot turn
        // into a lambda: a top-level `function` (bindVarSimple) or a
        // function-typed parameter. A let-bound `lambda` carries
        // TypedValue = Some and MUST keep its existing resolve-at-apply path
        // (eta-wrapping it would turn the lambda into a std::function capture
        // and break compose chains like `object_for(f) >>@ object_for(g)`).
        | Some info when Option.isNone info.TypedValue ->
            match env.Subst.Resolve info.Type with
            | FuncElem (paramTys, retTy)
                    when not paramTys.IsEmpty
                         && not (paramTys |> List.exists (fun t -> match env.Subst.Resolve t with IRTPoly _ -> true | _ -> false)) ->
                let uid = env.Builder.FreshId()
                let names = paramTys |> List.mapi (fun i _ -> sprintf "__k%d_%d" uid i)
                let lamParams = names |> List.map (fun n -> { Name = n; Type = None } : LambdaParam)
                let bodyApp =
                    inheritSpan kernelExpr
                        (ExprApp (kernelExpr, names |> List.map (fun n -> inheritSpan kernelExpr (ExprVar n))))
                Some (
                    inferLambda env lamParams None bodyApp
                    |> Result.bind (fun tLam ->
                        // Pin the residual param types to the callee's declared
                        // ones: direct application keeps its historical looseness
                        // (no param-vs-arg unification), mirroring the prefix
                        // partial-application eta-expansion.
                        unify env.Subst tLam.Type (mkFuncArrow paramTys retTy)
                        |> Result.map (fun () -> tLam)))
            | _ -> None
        | Some _ -> None   // let-bound value (lambda etc.): use the existing path
        | None -> None
    | _ -> None

/// Pick the zero literal for an element type. ETIndexRef _ requires looking up
/// the alias in env.TypeDefs: a string-valued EnumIdx needs LitString "" (the
/// empty string is the natural zero for std::string), an int-valued EnumIdx
/// uses LitInt 0L. ETString itself uses LitString "". Other types follow the
/// obvious pattern. This is consulted by every site that synthesizes a "zero
/// kernel" for a method_for / object_for with `<@> zero` — the runtime value
/// is rarely meaningful semantically (no obvious string identity for fold),
/// but the literal kind must match the element type or codegen produces
/// invalid C++.
and zeroLitForElem (env: TypeEnv) (et: ElemType) : TypedExprKind =
    match et with
    | ETInt32 | ETInt64 -> TExprLit (LitInt 0L)
    | ETBool -> TExprLit (LitBool false)
    | ETString -> TExprLit (LitString "")
    | _ -> TExprLit (LitFloat 0.0)

and inferApply (env: TypeEnv) (tLeft: TypedExpr) (tRight: TypedExpr) : TypeResult<TypedExpr> =
    let rL = resolveTypedExpr env tLeft
    let rR = resolveTypedExpr env tRight

    match rL.Kind, rR.Kind with
    | TExprMethodFor mfInfo, TExprLambda lambdaInfo ->
        buildApplyInfo env mfInfo.Arrays mfInfo.Identities mfInfo.ArrayTypes mfInfo.SDimsPerArray mfInfo.SharedIndexTypes lambdaInfo tLeft tRight false false

    | TExprMethodFor mfInfo, TExprSection op ->
        // Synthesize a TypedLambdaInfo from the operator section
        let aId = env.Builder.FreshId()
        let bId = env.Builder.FreshId()
        let isComm = match op with
                     | OpAdd | OpMul | OpEq | OpNeq | OpAnd | OpOr -> true
                     | _ -> false
        let paramTy = IRTScalar ETFloat64
        let lambdaInfo : TypedLambdaInfo = {
            Params = [{ Name = "a"; Type = paramTy; Index = 0; VarId = aId }
                      { Name = "b"; Type = paramTy; Index = 1; VarId = bId }]
            Body = mkTyped (TExprBinOp (Elementwise, op,
                      mkTyped (TExprVar ("a", aId, None)) paramTy,
                      mkTyped (TExprVar ("b", bId, None)) paramTy)) paramTy
            ReturnType = paramTy
            CommGroups = if isComm then [[0; 1]] else []
            Captures = []; IsCommutative = isComm
            Parallel = []  // synthesized (operator section): no user clause
        }
        buildApplyInfo env mfInfo.Arrays mfInfo.Identities mfInfo.ArrayTypes mfInfo.SDimsPerArray mfInfo.SharedIndexTypes lambdaInfo tLeft tRight false false

    | TExprMethodFor mfInfo, TExprReynolds (innerKernel, isReynoldsAntisym) ->
        let resolvedInner = resolveTypedExpr env innerKernel
        match resolvedInner.Kind with
        | TExprLambda li -> buildApplyInfo env mfInfo.Arrays mfInfo.Identities mfInfo.ArrayTypes mfInfo.SDimsPerArray mfInfo.SharedIndexTypes li tLeft tRight true isReynoldsAntisym
        | _ -> Error (Other "reynolds() requires a lambda kernel, but the inner expression could not be resolved to a lambda")

    | TExprMethodFor mfInfo, TExprZero ->
        // M <@> zero: synthesize a lambda that returns 0 for each index point
        // Infer element type from first array, default to Float64.
        // Phase B2: at.ElemType is IRType. We extract the primitive scalar
        // for the literal-choice match below; non-primitive elem types
        // (struct/named) fall through to Float64 — S3 tag: relic, this
        // path semantically requires a primitive-typed array anyway since
        // `zero` produces literal 0/false values.
        let elemTypeIR =
            mfInfo.ArrayTypes |> List.tryHead
            |> Option.map (fun at -> at.ElemType)
            |> Option.defaultValue (IRTScalar ETFloat64)
        let elemType =
            match elemTypeIR with
            | AnyPrimElem et -> et
            | _ -> ETFloat64
        let paramTy =
            match elemTypeIR with
            | AnyPrimElem _ -> elemTypeIR
            | _ -> IRTScalar ETFloat64
        let zeroLit = zeroLitForElem env elemType
        // Create one parameter per array (all rank-0 element types)
        let nArrays = mfInfo.Arrays.Length
        let params_ = List.init nArrays (fun i ->
            let pid = env.Builder.FreshId()
            { Name = sprintf "__z%d" i; Type = paramTy; Index = i; VarId = pid })
        let lambdaInfo : TypedLambdaInfo = {
            Params = params_
            Body = mkTyped zeroLit paramTy
            ReturnType = paramTy
            CommGroups = []
            Captures = []; IsCommutative = true
            Parallel = []  // synthesized (zero kernel): no user clause
        }
        let tZeroKernel = mkTyped (TExprLambda lambdaInfo) (IRTScalar elemType)
        buildApplyInfo env mfInfo.Arrays mfInfo.Identities mfInfo.ArrayTypes mfInfo.SDimsPerArray mfInfo.SharedIndexTypes lambdaInfo tLeft tZeroKernel false false

    // object_for(<combinator>) <@> (c1, c2, ...) → left-fold or map+combine
    | TExprObjectFor objInfo, _ when
        (match objInfo.Kernel.Kind with TExprSection _ -> true | _ -> false) ->
        let op = match objInfo.Kernel.Kind with TExprSection op -> op | _ -> OpAdd
        // Extract elements from right side
        let elems = match rR.Kind with
                    | TExprTuple es -> es
                    | _ -> [tRight]
        if elems.Length < 2 then
            Error (Other "object_for(<combinator>) requires at least 2 arguments")
        else
            match op with
            | OpApply ->
                // object_for(<@>) <@> ((L1, f1), (L2, f2), ...)
                // Apply <@> to each (loop, kernel) pair, return tuple of computations
                let pairs = elems |> List.map (fun e ->
                    match e.Kind with
                    | TExprTuple [loop; kernel] -> Ok (loop, kernel)
                    | _ -> Error (Other "object_for(<@>) expects (loop, kernel) pairs"))
                pairs |> sequenceResults |> Result.bind (fun pairList ->
                    pairList |> List.map (fun (loop, kernel) -> inferApply env loop kernel)
                    |> sequenceResults
                    |> Result.map (fun computations ->
                        let types = computations |> List.map (fun c -> c.Type)
                        mkTyped (TExprTuple computations) (IRTTuple types)))
            | OpFunctor ->
                // object_for(<$>) <@> (f, c)  or  (f1, f2, ..., c)
                // Right-fold: f1 <$> (f2 <$> (... <$> c))
                let funcs : TypedExpr list = elems |> List.take (elems.Length - 1)
                let comp : TypedExpr = elems |> List.last
                let applyFmap (acc: TypedExpr) (f: TypedExpr) : TypedExpr =
                    let outputType =
                        match f.Type, acc.Type with
                        | FuncElem (_, IRTScalar et), ArrayElem arr ->
                            mkArrayLike { arr with ElemType = IRTScalar et }
                        | FuncElem (_, retTy), _ -> retTy
                        | _ -> acc.Type
                    mkTyped (TExprFunctorMap (f, acc)) outputType
                let result : TypedExpr = funcs |> List.rev |> List.fold applyFmap comp
                Ok result
            | OpChoice ->
                // object_for(<|>) <@> (c1, c2, ...) → left-fold producing TExprChoice
                let folder (acc: TypedExpr) (elem: TypedExpr) : TypedExpr =
                    mkTyped (TExprChoice (acc, elem)) acc.Type
                Ok (elems |> List.tail |> List.fold folder (List.head elems))
            | OpFallback ->
                // object_for(<|:>) <@> (A1, A2, ...) → left-fold of allocated-
                // fallback. Each intermediate is a fully-allocated dense array,
                // so only the FIRST operand's allocation (compound mask /
                // pointer chain) can defer to later ones — later dense results
                // never fall through. That is the correct left-fold semantics.
                let folder (acc: TypedExpr) (elem: TypedExpr) : TypedExpr =
                    mkTyped (TExprFallback (acc, elem)) elem.Type
                Ok (elems |> List.tail |> List.fold folder (List.head elems))
            | _ ->
                // Standard left-associative fold: (((c1 op c2) op c3) op ...)
                let folder (acc: TypedExpr) (elem: TypedExpr) =
                    let resTy = 
                        match op with
                        | OpParallel | OpFusion -> IRTTuple [acc.Type; elem.Type]
                        | _ -> acc.Type
                    let kind =
                        match op with
                        | OpParallel -> TExprParallel (acc, elem)
                        | OpFusion -> TExprFusion (acc, elem)
                        | _ -> TExprBinOp (Elementwise, op, acc, elem)
                    mkTyped kind resTy
                Ok (elems |> List.tail |> List.fold folder (List.head elems))

    // object_for <@> arrays: kernel-first application
    // Preserves TExprObjectFor as the loop provenance (no synthetic TExprMethodFor)
    // Detects zip() arguments and expands them into co-iteration groups
    | TExprObjectFor objInfo, _ ->
        // Extract array typed exprs from right side
        let rawExprs = match rR.Kind with
                       | TExprTuple elems -> elems
                       | _ -> [tRight]
        // Resolve variables to detect indirect zip (let Z = zip(A,B); ... <@> Z)
        let resolvedExprs = rawExprs |> List.map (resolveTypedExpr env)

        // Check if ANY argument is a zip — flatten zip children into co-iteration groups
        let hasZip = resolvedExprs |> List.exists (fun e ->
            match e.Kind with TExprZip _ -> true | _ -> false)

        let (flatArrays, sharedRecords) =
            if hasZip then
                let mutable arrays : TypedExpr list = []
                let mutable isCoIterGroup : bool list = []
                for expr in resolvedExprs do
                    match expr.Kind with
                    | TExprZip children ->
                        arrays <- arrays @ children
                        isCoIterGroup <- isCoIterGroup @ (children |> List.map (fun _ -> true))
                    | _ ->
                        arrays <- arrays @ [expr]
                        isCoIterGroup <- isCoIterGroup @ [false]
                let arrTypes = arrays |> List.map (fun a ->
                    match a.Type with
                    | ArrayElem at -> at
                    | _ -> { ElemType = IRTScalar ETFloat64; IndexTypes = []; IsVirtual = false; Identity = None })
                let allCoIter = isCoIterGroup |> List.forall id
                let recs =
                    if allCoIter then
                        // Shared records: the FULL common index shape when the
                        // operands agree and the shape is co-iterable (all plain
                        // dense, or a single packed record); the historical
                        // first-record-only fallback otherwise (buildApplyInfo's
                        // row-rank trim keeps row-mode kernels working either way).
                        match arrTypes with
                        | first :: rest when not first.IndexTypes.IsEmpty ->
                            let shape0 = first.IndexTypes
                            if rest |> List.forall (fun at -> indexShapesAgree at.IndexTypes shape0)
                               && coIterableRecords shape0 then shape0
                            else [shape0.Head]
                        | _ -> []
                    else []
                (arrays, recs)
            else (rawExprs, [])

        let identities = flatArrays |> List.map (fun arr ->
            match arr.Kind with
            | TExprVar (name, _, _) -> AIDVariable name
            | _ -> AIDLiteral (env.Builder.FreshId()))
        let arrayTypes = flatArrays |> List.map (fun arr ->
            match arr.Type with
            | ArrayElem at -> at
            | _ -> { ElemType = IRTScalar ETFloat64; IndexTypes = []; IsVirtual = false; Identity = None })
        // Real per-array S-dim counts in BOTH modes: the co-iteration case needs
        // them so buildApplyInfo's IRTInfer fallback computes the kernel slice
        // rank against the true array rank (a scalar kernel over rank-2 zips
        // must yield kR = 0 → full-product co-iteration, not a mis-trim).
        let sDimsPerArray = computeSDimsPerArray arrayTypes

        // Resolve kernel and build ApplyInfo with object_for as provenance
        let resolvedKernel = resolveTypedExpr env objInfo.Kernel
        match resolvedKernel.Kind with
        | TExprLambda lambdaInfo ->
            buildApplyInfo env flatArrays identities arrayTypes sDimsPerArray sharedRecords lambdaInfo tLeft objInfo.Kernel false false
        | TExprReynolds (innerK, isReynoldsAntisym) ->
            let resolvedInnerK = resolveTypedExpr env innerK
            match resolvedInnerK.Kind with
            | TExprLambda li ->
                buildApplyInfo env flatArrays identities arrayTypes sDimsPerArray sharedRecords li tLeft objInfo.Kernel true isReynoldsAntisym
            | _ -> Error (Other "reynolds() requires a lambda kernel, but the inner expression could not be resolved to a lambda")
        | TExprZero ->
            // object_for(zero) <@> arrays: synthesize zero-returning lambda
            // Phase B2: at.ElemType is IRType; extract primitive for literal choice.
            // Option C: preserve the wrapper (IRTIdxTagged/IRTUnitAnnotated)
            // in paramTy so the synthesized lambda's param type unifies with
            // the iteration's yielded type. Extract only the inner primitive
            // for zeroLitForElem.
            let elemTypeIR =
                arrayTypes |> List.tryHead
                |> Option.map (fun at -> at.ElemType)
                |> Option.defaultValue (IRTScalar ETFloat64)
            let elemType =
                match elemTypeIR with
                | AnyPrimElem et -> et
                | _ -> ETFloat64
            let paramTy =
                match elemTypeIR with
                | AnyPrimElem _ -> elemTypeIR
                | _ -> IRTScalar ETFloat64
            let zeroLit = zeroLitForElem env elemType
            let nArrays = flatArrays.Length
            let params_ = List.init nArrays (fun i ->
                let pid = env.Builder.FreshId()
                { Name = sprintf "__z%d" i; Type = paramTy; Index = i; VarId = pid })
            let lambdaInfo : TypedLambdaInfo = {
                Params = params_
                Body = mkTyped zeroLit paramTy
                ReturnType = paramTy
                CommGroups = []
                Captures = []; IsCommutative = true
                Parallel = []  // synthesized (object_for zero kernel): no clause
            }
            buildApplyInfo env flatArrays identities arrayTypes sDimsPerArray sharedRecords lambdaInfo tLeft (mkTyped (TExprLambda lambdaInfo) (IRTScalar elemType)) false false
        | _ -> Error (ObjectForKernel (resolvedKernel.Kind.GetType().Name))

    // Composed ObjectLoop: (o1 >>@ o2) <@> A
    | TExprCompose (OpComposeObj, _, _), _ ->
        let arrayExprs = match rR.Kind with
                         | TExprTuple elems -> elems
                         | _ -> [tRight]
        let outputType =
            match arrayExprs with
            | first :: _ -> first.Type
            | [] -> IRTUnit
        let info : TypedApplyInfo = {
            Loop = tLeft; Kernel = tRight
            Arrays = arrayExprs
            Identities = arrayExprs |> List.map (fun _ -> AIDLiteral (env.Builder.FreshId()))
            ArrayTypes = arrayExprs |> List.map (fun a ->
                match a.Type with
                | ArrayElem at -> at
                | _ -> { ElemType = IRTScalar ETFloat64; IndexTypes = []; IsVirtual = false; Identity = None })
            SharedIndexTypes = []
            SymcomStates = []; TriangularLevels = []
            SDimsPerArray = []
            KernelInputRanks = []; KernelOutputRank = 0
            KernelTDims = []
            SpeedupFactor = 1L; ReynoldsSpeedup = 1L
            HasReynolds = false; OutputType = outputType
            IsCoIteration = false
            IsComposeApply = true
        }
        Ok (mkTyped (TExprApply info) outputType)

    | _ ->
        // Name the real culprit. When the LEFT already is a valid
        // method_for/object_for, the unmatched operand is the RIGHT (the
        // kernel) — reporting the left here is what produced the historical
        // "requires method_for … but got TExprMethodFor" red herring.
        let describeKind (k: TypedExprKind) =
            match k with
            | TExprVar (name, _, _) -> sprintf "variable '%s'" name
            | _ -> k.GetType().Name.Replace("TExpr", "")
        match rL.Kind with
        | TExprMethodFor _ | TExprObjectFor _ ->
            Error (ChainOpBadKernel (describeKind rR.Kind))
        | _ ->
            Error (ChainOpNeedsMethodFor (describeKind rL.Kind))

and buildApplyInfo (env: TypeEnv)
    (arrays: TypedExpr list) (identities: ArrayIdentity list)
    (arrayTypes: IRArrayType list) (sDimsPerArray: int list)
    (sharedIndexTypes: IRIndexType list)
    (lambdaInfo: TypedLambdaInfo)
    (tLoop: TypedExpr) (tKernel: TypedExpr)
    (isReynolds: bool) (isReynoldsAntisym: bool)
    : TypeResult<TypedExpr> =

    let commGroups = lambdaInfo.CommGroups

    // Co-iteration INDEX-PARAM form: a co-iterated kernel may declare
    // N + R parameters — one value per co-iterated array plus the R shared
    // iteration indices — e.g. `for (uq, ph) in range<Y, X> <@>
    // lambda(zu, zp, i, j) -> ...`. The indices ride as a TRAILING synthetic
    // range<...> operand over the shared records: expandedRows then expands
    // it to one tagged Nat<...> param per slot (unifying + tag-checking the
    // index params exactly like an explicit range<> source), and the loop
    // builder's VirtualRange elements bind them to the loop indices. The
    // virtual operand is appended LAST so the value params keep their
    // positions. Values-only kernels (arity N) are untouched, as is every
    // non-co-iteration apply.
    let (arrays, identities, arrayTypes, sDimsPerArray) =
        let idxParamCount = sharedIndexTypes |> List.sumBy (fun r -> r.Rank)
        let alreadyVirtual = arrayTypes |> List.exists (fun at -> at.IsVirtual)
        if not sharedIndexTypes.IsEmpty
           && idxParamCount > 0
           && not alreadyVirtual
           && lambdaInfo.Params.Length = arrays.Length + idxParamCount then
            let elemT =
                match List.tryLast sharedIndexTypes with
                | Some i -> elemTypeForIterationIndex i
                | None -> IRTScalar ETInt64
            let vExpr = mkTyped (TExprRange sharedIndexTypes) (mkVirtualArrayArrow sharedIndexTypes elemT)
            let vAt =
                match vExpr.Type with
                | ArrayElem at -> at
                | _ -> { ElemType = elemT; IndexTypes = sharedIndexTypes; IsVirtual = true; Identity = None }
            (arrays @ [vExpr],
             identities @ [AIDLiteral (env.Builder.FreshId())],
             arrayTypes @ [vAt],
             sDimsPerArray @ [idxParamCount])
        else (arrays, identities, arrayTypes, sDimsPerArray)

    // Phase 2 (Gap 2.5 fix): resolve param types through Subst before
    // reading rank. A kernel param may have started as IRTInfer at lambda
    // creation but been refined during the kernel body's typecheck (e.g.,
    // reduce's kernel-arg deduction synthesizes a rank-1 IRTArray binding).
    // If we read p.Type directly here, we'd see the stale IRTInfer and
    // compute kRank = 0, which makes perRowType think the kernel takes a
    // scalar — leading to a shape mismatch when we later unify against
    // the real rank-N source per-row type.
    //
    // Path 1 fix (Stage 3c follow-on): when the param's resolved type is
    // STILL IRTInfer after body typechecking — i.e., the body didn't
    // structurally constrain the param's shape (e.g. `lambda(g) -> g(0)`,
    // where g(0) is ambiguous between array indexing and function
    // application) — fall back to the array-side rank: the kernel sees
    // a slice of rank (array rank − iterated S-dimensions). For a
    // typical `method_for(arr)` with one iterated outer dim, kRank
    // becomes (arrTy.rank - 1). This recovers the array shape
    // information that the body alone couldn't supply, and lets the
    // subsequent perRowType unification pin the param to the correct
    // Array<T, N> type rather than collapsing to the scalar element.
    let resolvedParamTypes =
        lambdaInfo.Params |> List.map (fun p -> env.Subst.Resolve p.Type)
    // Param rank: if the param's resolved type is an array, read its
    // rank directly. If the param is still IRTInfer — meaning the body
    // didn't structurally constrain the param's shape (e.g.
    // `lambda(g) -> g(0)`, where the application is ambiguous between
    // array indexing and function application) — fall back to the
    // array-side rank.
    //
    // The array-side rank isn't naively `(array rank − iterated
    // S-dimensions)`. For ragged-inner-dim arrays (group_by results,
    // ragged literals, depidx-inner shapes), `computeSDimsPerArray`
    // counts every SDimension equally — the inner ragged dim included.
    // But codegen's ragged-peel pass treats those inner dims as
    // KERNEL-SIDE (peeled into a per-row binding), not iterated. To
    // match codegen semantics here, we adjust: every index with a
    // ragged-family tag contributes to the kernel's rank, not to the
    // iterated count. The result is the kernel's effective rank as
    // codegen actually sees it.
    let isRaggedInnerKind (k: IxKind) : bool =
        isRaggedRowKind k || k = IxKErrorRaggedNoPrior
    // Does the kernel body use parameter `pname` AS AN ARRAY (indexed, applied,
    // or passed to an array combinator like reduce/extents/rank/arity)? This
    // distinguishes a CONSUMING kernel (e.g. `lambda(g) -> reduce(g, ...)` or
    // `lambda(g) -> g(0)`), whose param is a sub-array along a ragged inner dim,
    // from an ELEMENTWISE kernel (e.g. `lambda(e) -> e * 2.0`), whose param is a
    // scalar. We cannot rely on the param's resolved scalar type, because mixed
    // int/float arithmetic legitimately leaves an untyped scalar param flexible
    // (e.g. a range index `i` in `i * 2.0` stays Int64, promoted — NOT unifiable
    // to Float64). The structural use is the reliable signal.
    let paramUsedAsArray (pname: string) (body: TypedExpr) : bool =
        let isParamVar (e: TypedExpr) =
            match e.Kind with TExprVar (n, _, _) -> n = pname | _ -> false
        let rec walk (e: TypedExpr) : bool =
            let here =
                match e.Kind with
                | TExprIndex (arr, _, _) when isParamVar arr -> true
                | TExprApp (f, _) when isParamVar f -> true            // g(0) as application
                | TExprReduce (arr, _, _) when isParamVar arr -> true
                | TExprProdSum args when List.exists isParamVar args -> true
                | TExprExtents arr when isParamVar arr -> true
                | TExprRank arr when isParamVar arr -> true
                | TExprArity n when n = pname -> true
                | _ -> false
            here || childrenAny e
        and childrenAny (e: TypedExpr) : bool =
            match e.Kind with
            | TExprBinOp (_, _, l, r) -> walk l || walk r
            | TExprUnaryOp (_, x) -> walk x
            | TExprApp (f, args) -> walk f || List.exists walk args
            | TExprIndex (a, idxs, _) -> walk a || List.exists walk idxs
            | TExprReduce (a, k, i) ->
                walk a || walk k || (match i with Some e -> walk e | None -> false)
            | TExprProdSum args -> List.exists walk args
            | TExprExtents a | TExprRank a | TExprArrayNegate a
            | TExprArrayConjugate a | TExprUnique a -> walk a
            | TExprMask (a, p) -> walk a || walk p
            | TExprCompound (a, p) -> walk a || walk p
            | TExprSort (a, k) -> walk a || walk k
            | TExprIf (c, t, e2) -> walk c || walk t || walk e2
            | TExprBlock (_, Some fe) -> walk fe
            | TExprSequence es -> List.exists walk es
            | TExprTuple es -> List.exists walk es
            | TExprLet (_, _, v, b) -> walk v || walk b
            | _ -> false
        walk body
    let kernelInputRanks =
        resolvedParamTypes |> List.mapi (fun i t ->
            match t with
            | ArrayElem arr -> arr.IndexTypes.Length
            | IRTInfer _ when i < arrayTypes.Length && i < sDimsPerArray.Length ->
                let arrTy = arrayTypes.[i]
                let sDims = sDimsPerArray.[i]
                let raggedInnerCount =
                    arrTy.IndexTypes
                    |> List.filter (fun idx -> isRaggedInnerKind idx.IxKind)
                    |> List.length
                // Re-attribute ragged inner dims to the kernel side ONLY when the
                // param is structurally used as an array (consuming kernel). For
                // an elementwise scalar use, the inner dim is NOT consumed and
                // must stay on the iteration/output side -> kernel rank 0, so the
                // ragged/DepIdx inner dim propagates to the output.
                let pname =
                    if i < lambdaInfo.Params.Length then lambdaInfo.Params.[i].Name else ""
                if raggedInnerCount > 0 && not (paramUsedAsArray pname lambdaInfo.Body) then
                    0
                else
                    let trueIteratedDims = max 0 (sDims - raggedInnerCount)
                    max 0 (arrTy.IndexTypes.Length - trueIteratedDims)
            | _ -> 0)

    // Infer T-dimensions from the kernel's resolved return type (§9.2).
    // If the kernel returns an array, its index types become T-dimensions
    // in the output. If it returns a scalar, there are no T-dimensions.
    let (kernelTDims, kernelOutputRank) =
        let resolved = env.Subst.Resolve(lambdaInfo.ReturnType)
        match resolved with
        | ArrayElem arr ->
            let tDims = arr.IndexTypes |> List.map (fun idx -> { idx with Kind = TDimension })
            (tDims, tDims.Length)
        | _ -> ([], 0)

    // Mark each array's consumed fiber dimensions as T-dimensions (§9.2). The
    // kernel consumes its innermost irank(f,i) = kernelInputRanks.[i] dims as a
    // fiber argument (e.g. a TimeIdx fiber reduced inside the kernel). Those
    // dims are NOT part of the symmetric iteration grid: re-tagging them
    // Kind = TDimension makes every downstream consumer consistent at once —
    // computeSDimsPerArray (counts only SDimension) yields the grid count,
    // buildLoopLevelStructure (builds levels only for SDimension) emits grid-
    // depth loops and leaves the fiber as the kernel's array slice, and
    // deduceOutputType symmetrizes only the grid dims. For scalar kernels
    // irank = 0, so nothing is re-tagged and behavior is unchanged.
    let gridArrayTypes =
        arrayTypes |> List.mapi (fun i at ->
            let irank = if i < kernelInputRanks.Length then kernelInputRanks.[i] else 0
            if irank <= 0 then at
            else
                // The fiber is the innermost irank dims; mark them TDimension.
                let n = at.IndexTypes.Length
                let retagged =
                    at.IndexTypes |> List.mapi (fun j idx ->
                        if j >= n - irank then { idx with Kind = TDimension } else idx)
                { at with IndexTypes = retagged })

    let states = computeAllSymcomStates identities gridArrayTypes commGroups (computeSDimsPerArray gridArrayTypes)
    let triLevels = computeTriangularLevels gridArrayTypes identities commGroups (computeSDimsPerArray gridArrayTypes)
    let speedup = computePartialProductSpeedup gridArrayTypes identities commGroups (computeSDimsPerArray gridArrayTypes)

    // Phase 2 (Gap 2.5 fix): unify each kernel parameter with the source
    // array's per-row type. This catches mismatches like a String-typed
    // kernel param applied to a Float64 array, which previously silently
    // miscompiled because elem types weren't compared.
    //
    // Per-row type computation: for a source array of rank R and a
    // kernel param of rank K (K = 0 for scalar params, K = N for an
    // Array<T like ...N indices...> param), the kernel sees the array
    // sliced at the outer R-K dims. So the per-row type is:
    //   - kRank = 0:     the array's elem type itself (a scalar value)
    //   - kRank = R:     the whole array (degenerate, but allowed)
    //   - 0 < kRank < R: an array with the inner kRank dims preserved
    //
    // We zip params with arrays. Unification failures here are real
    // type errors and propagate out as TypeMismatch. Length mismatches
    // (arity errors) are caught earlier at the kernel-arity check, so
    // a defensive zip is sufficient.
    let perRowType (arrTy: IRArrayType) (kRank: int) : IRType =
        let r = arrTy.IndexTypes.Length
        if kRank <= 0 then
            arrTy.ElemType  // Scalar kernel param sees one element per iter.
        elif kRank >= r then
            mkArrayLike arrTy  // Kernel wants the whole array (degenerate).
        else
            let nOuter = r - kRank
            let innerDims = arrTy.IndexTypes |> List.skip nOuter
            mkArrayLike { arrTy with IndexTypes = innerDims }

    // Expand each source into its kernel-facing param row type(s). A REAL array
    // contributes ONE param (its per-row slice). A VIRTUAL source (range<...>)
    // contributes ONE param PER index-type slot -- the index value at that slot --
    // so range<I1, I2> presents (i1, i2) to the kernel. For a single-slot virtual
    // source this equals the old perRowType result (the arrow's ElemType), so the
    // existing single-index behavior is unchanged.
    let expandedRows =
        arrayTypes
        |> List.mapi (fun i at ->
            if at.IsVirtual then
                // One param per RANK SLOT: a multi-rank index type (e.g. SymIdx<2,N>,
                // or a CompoundIdx of mask-rank R) contributes Rank coordinate params,
                // per the rank rule (kernel index slots = iteration rank). For rank-1
                // (dense) indices this is one param per index type, unchanged from 1b.
                at.IndexTypes |> List.collect (fun idx -> List.replicate idx.Rank (elemTypeForIterationIndex idx))
            else
                let kRank = if i < kernelInputRanks.Length then kernelInputRanks.[i] else 0
                [perRowType at kRank])
        |> List.concat

    let kernelParamUnifyResult =
        if lambdaInfo.Params.Length = expandedRows.Length then
            // Use resolved types so the unify call sees the same shape we used
            // to compute kRank. (Reading param.Type directly could be stale.)
            (List.zip resolvedParamTypes expandedRows)
            |> List.fold (fun acc (paramTy, row) ->
                acc |> Result.bind (fun () -> unify env.Subst paramTy row))
                (Ok ())
        else
            Ok ()  // Arity mismatch handled elsewhere; don't double-report.

    match kernelParamUnifyResult with
    | Error e -> Error e
    | Ok () ->
        // Reject unsupported / miscompiling kernel-body shapes now that the
        // params are bound to the iterated element/row types.
        //
        // (1) Complex accessor on a ROW param: `real(z)`/`imag(z)`/`arg(z)`
        //     whose operand unified to an ARRAY (e.g. a zip row). Such an
        //     accessor was DEFERRED at body-inference time — the operand was
        //     still an unresolved infer var, so it typed as a scalar Float64
        //     without constraining the operand (see the IRTInfer arm of the
        //     accessor intrinsics). Lowering then synthesized an array<->scalar
        //     broadcast kernel embedding the uncaptured param, giving an IR
        //     dangling-VarId (BL6001). Re-check here and steer to a scalar-per-
        //     element map, exactly as the resolved-array operand already does.
        //
        // (2) Array-valued ELEMENTWISE kernel body: `ra * rb` between two row
        //     params re-synthesizes (inferBinOp both-array arm) into a nested
        //     compute(method_for(zip ...)) with output rank >= 1. In expression
        //     position codegen collapses it to (sum ra)(sum rb) — a silent
        //     miscompile. A bare array-param passthrough (`lambda(row) -> row`,
        //     Kind = TExprVar) is fine and untouched. Reject and steer to a
        //     scalar row reduction (prodsum/reduce).
        let rec findBadComplexAccessor (e: TypedExpr) : string option =
            match e.Kind with
            | TExprUnaryOp ((OpReal | OpImag | OpArg) as op, operand)
                    when (match env.Subst.Resolve operand.Type with ArrayElem _ -> true | _ -> false) ->
                Some (match op with OpReal -> "real" | OpImag -> "imag" | _ -> "arg")
            | _ -> typedExprChildren e |> List.tryPick findBadComplexAccessor
        let arrayValuedComputeBody =
            kernelOutputRank >= 1 &&
            (match lambdaInfo.Body.Kind with TExprCompute _ -> true | _ -> false)
        match findBadComplexAccessor lambdaInfo.Body with
        | Some name -> Error (IntrinsicComplexScalarOnly name)
        | None ->
        if arrayValuedComputeBody then
            Error (Other "array-valued elementwise kernel body is not supported inside a kernel; reduce the row to a scalar with prodsum or reduce, or compute the elementwise product at top level")
        else
        // After param-type unification, inference variables that flowed into
        // the body's TExprIndex sites may now resolve to nominally-tagged
        // types (e.g., `r` in `lambda(r) -> by_country(r)` is unified with
        // the iterated array's elem type `Nat<RegionIdx>`). Re-run the tag
        // check across the body so cross-tag indexing through kernel
        // parameters surfaces as a real type error rather than silently
        // typechecking. See revalidateBodyTagChecks for rationale.
        revalidateBodyTagChecks env lambdaInfo.Body
        |> Result.bind (fun () ->
        // Unit-only second pass over the kernel body, now that the params
        // are bound to the input element types (see kernelBodyUnits). The
        // computed signature replaces whatever annotation the return type
        // resolution leaked, and op mismatches inside the body reject here.
        kernelBodyUnits env Map.empty lambdaInfo.Body
        |> Result.bind (fun bodyUnits ->
        // Infer output element type from kernel return type, falling back to input arrays.
        // Returns IRType (Phase B2). Primitives are wrapped IRTScalar.
        let restampScalar (t: IRType) =
            match IR.stripUnits t with
            | IRTScalar _ as bare ->
                match bodyUnits with
                | Some u -> IRTUnitAnnotated (bare, u)
                | None -> bare
            | _ -> t
        // Post-unification COMPLEX re-stamp. The kernel body was typed while
        // its params were still unresolved inference variables, so scalar
        // promotion against a concrete real collapsed prematurely to the real
        // side: `lambda(z) -> z * 2.0` over a complex array stamped Float64
        // on the binop (and hence the return/output elem) because z had not
        // yet unified with Complex128. Now that the params ARE unified, redo
        // the promotion bottom-up with resolved operand types. Conservative
        // by construction: a node is re-stamped ONLY when the recomputed
        // element type is complex and the stamped one is a real scalar —
        // every non-complex kernel re-stamps to itself unchanged.
        let restampedBody =
            let elemOfType (ty: IRType) : ElemType option =
                match IR.stripUnits (env.Subst.Resolve ty) with
                | IRTScalar et -> Some et
                | _ -> None
            let isComplexElem = function ETComplex64 | ETComplex128 -> true | _ -> false
            let isRealScalar = function
                | Some (ETFloat32 | ETFloat64 | ETInt32 | ETInt64) -> true
                | _ -> false
            // Upgrade a node's scalar type, preserving a unit-annotation wrapper.
            let withElem (node: TypedExpr) (et: ElemType) : TypedExpr =
                let newTy =
                    match node.Type with
                    | IRTUnitAnnotated (_, u) -> IRTUnitAnnotated (IRTScalar et, u)
                    | _ -> IRTScalar et
                { node with Type = newTy }
            // Upgrade `node` iff its stamp is a real scalar and `computed` is
            // complex; otherwise keep it (byte-identical for real kernels).
            let maybeUpgrade (node: TypedExpr) (computed: ElemType option) : TypedExpr =
                match computed, elemOfType node.Type with
                | Some ce, cur when isComplexElem ce && isRealScalar cur -> withElem node ce
                | _ -> node
            let rec walk (t: TypedExpr) : TypedExpr =
                match t.Kind with
                | TExprBinOp (bmode, ((OpAdd | OpSub | OpMul | OpDiv | OpCaret) as bop), l, r) ->
                    let l2, r2 = walk l, walk r
                    let node = { t with Kind = TExprBinOp (bmode, bop, l2, r2) }
                    match elemOfType l2.Type, elemOfType r2.Type with
                    | Some le, Some re ->
                        match IR.promoteElemType le re with
                        | Some pe when isComplexElem pe -> maybeUpgrade node (Some pe)
                        | _ -> node
                    | _ -> node
                | TExprUnaryOp (((OpNeg | OpConj) as uop), e) ->
                    let e2 = walk e
                    let node = { t with Kind = TExprUnaryOp (uop, e2) }
                    maybeUpgrade node (elemOfType e2.Type)
                | TExprUnaryOp (OpMath name, e) ->
                    let e2 = walk e
                    let node = { t with Kind = TExprUnaryOp (OpMath name, e2) }
                    match name, elemOfType e2.Type with
                    // abs of a complex is the real magnitude: correct a
                    // deferred stamp (the operand's variable, now resolved
                    // complex) to Float64.
                    | "abs", Some (ETComplex64 | ETComplex128) ->
                        (match elemOfType node.Type with
                         | Some ETFloat64 -> node
                         | _ -> withElem node ETFloat64)
                    | "abs", _ -> node
                    // Transcendentals preserve a complex operand's type.
                    | _, Some ((ETComplex64 | ETComplex128) as ce) ->
                        maybeUpgrade node (Some ce)
                    // Deferred REAL operand (see the intrinsic's IRTInfer
                    // arm): the real intrinsics are Float64-valued.
                    | _, Some (ETInt32 | ETInt64 | ETFloat32 | ETFloat64) ->
                        (match elemOfType node.Type with
                         | Some ETFloat64 -> node
                         | _ -> withElem node ETFloat64)
                    | _ -> node
                | TExprUnaryOp (((OpReal | OpImag) as uop), e) ->
                    // Deferred-width correction: real/imag of a Complex64
                    // yield Float32 components (the deferred arm stamped the
                    // Complex128 answer, Float64).
                    let e2 = walk e
                    let node = { t with Kind = TExprUnaryOp (uop, e2) }
                    (match elemOfType e2.Type, elemOfType node.Type with
                     | Some ETComplex64, Some ETFloat64 -> withElem node ETFloat32
                     | _ -> node)
                | TExprIf (c, a, b) ->
                    let a2, b2 = walk a, walk b
                    let node = { t with Kind = TExprIf (c, a2, b2) }
                    match elemOfType a2.Type, elemOfType b2.Type with
                    | Some le, Some re -> maybeUpgrade node (IR.promoteElemType le re)
                    | _ -> node
                | TExprLet (name, vid, value, body) ->
                    let v2, b2 = walk value, walk body
                    let node = { t with Kind = TExprLet (name, vid, v2, b2) }
                    maybeUpgrade node (elemOfType b2.Type)
                | _ -> t
            walk lambdaInfo.Body
        let outputElemType =
            let resolved =
                let r = env.Subst.Resolve(lambdaInfo.ReturnType)
                // Adopt the re-stamped body's complex type when the collapse
                // hit the return type too.
                match IR.stripUnits r, IR.stripUnits restampedBody.Type with
                | IRTScalar (ETFloat32 | ETFloat64 | ETInt32 | ETInt64), (IRTScalar (ETComplex64 | ETComplex128) as ct) -> ct
                | _ -> r
            match resolved with
            | IRTScalar _ as t -> restampScalar t                  // stamp walk-computed units
            | ArrayElem arr -> arr.ElemType                         // already IRType
            | IRTUnitAnnotated (IRTScalar _, _) as t -> restampScalar t
            | IRTNamed _ as t -> t                                 // struct/sum rows: Array<Struct> output
            | IRTTuple _ as t -> t                                 // tuple rows: Array<(..,..)> output
            | _ ->
                // S3 tag: relic. Fall back to common element type of input arrays
                // when the return type is unresolved (IRTInfer) or has no
                // element-position meaning. The input arrays' elem types are
                // already IRType post-B2.
                arrayTypes |> List.tryPick (fun at -> Some at.ElemType)
                |> Option.defaultValue (IRTScalar ETFloat64)
                |> restampScalar
        // A kernel CONSUMES an inner dimension when it has an array-typed
        // parameter of rank > 0 (e.g. reduce over a ragged row). A purely
        // elementwise kernel (all scalar params) consumes nothing, so the
        // consumed-dim filter in deduceOutputType must NOT drop ragged/dep
        // inner dims — they propagate through the elementwise map.
        let kernelConsumesInner = kernelInputRanks |> List.exists (fun r -> r > 0)
        let outputType = deduceOutputType gridArrayTypes identities commGroups (computeSDimsPerArray gridArrayTypes) kernelTDims outputElemType isReynolds isReynoldsAntisym kernelConsumesInner env.Builder

        let reynoldsSpeedup =
            if isReynolds then
                let r = identities.Length
                if r > 1 then factorial r else 1L
            else 1L

        // After unification, the substitution holds the refined param
        // types but `lambdaInfo.Params[i].Type` still holds the original
        // (often IRTInfer) values — F# records are immutable. Downstream
        // zonking should resolve those through the substitution, but a
        // diagnostic from the Stage 3c filter-removal work showed lifted
        // lambdas getting `(double)` parameters where the substitution
        // had bound them to `Array<...>`. To remove any ambiguity, rebuild
        // the lambda info with explicitly-resolved param types, the
        // return type, and (for the value-flow side) the body's typed
        // expression. The resolved kernel then carries refined types
        // directly, independent of whether downstream zonking visits
        // every node.
        let resolvedLambdaInfo =
            { lambdaInfo with
                Params =
                    lambdaInfo.Params |> List.map (fun p ->
                        { p with Type = env.Subst.Resolve p.Type })
                // The complex re-stamp above must flow into the lifted lambda
                // too: with a stale Float64 stamp the lifted C++ function
                // would declare a double return around a std::complex body.
                Body = restampedBody
                ReturnType =
                    let r = env.Subst.Resolve lambdaInfo.ReturnType
                    match IR.stripUnits r, IR.stripUnits restampedBody.Type with
                    | IRTScalar (ETFloat32 | ETFloat64 | ETInt32 | ETInt64), (IRTScalar (ETComplex64 | ETComplex128) as ct) -> ct
                    | _ -> r }

        // Store the kernel with resolved types in the typed AST. Lowering
        // walks this typed lambda and emits a lifted IRCallable referenced
        // via IRVar(callable.Id) at the kernel slot.
        let resolvedKernel =
            let lambdaExpr = mkTyped (TExprLambda resolvedLambdaInfo) tKernel.Type
            if isReynolds then mkTyped (TExprReynolds (lambdaExpr, isReynoldsAntisym)) tKernel.Type
            else lambdaExpr

        // Co-iterated records = the leading (nRecords − kernelSliceRank) shared
        // records; the kernel consumes the trailing kernelSliceRank records as a
        // per-iteration slice. This one trim serves all three shapes uniformly:
        //   - scalar kernel (A op B, for (A,B) in range<I,J>): kR = 0 → the FULL
        //     product of records is co-iterated (multi-axis, the new case);
        //   - row-mode kernel (loops/085: lambda(ra: Array<X>, rb: Array<X>)):
        //     kR = 1 → only the outer record(s) co-iterate, the inner record
        //     rides into the kernel as a rank-1 slice (historical behavior);
        //   - single packed record (SymIdx co-iteration): kR = 0 → that record,
        //     walked as its Rank flat canonical levels (historical behavior).
        // Operand shape agreement is enforced where the records are collected
        // (zip arms / for-in), so kernelInputRanks.[0] is representative.
        let coIterSharedRecords =
            match sharedIndexTypes with
            | [] -> []
            | full ->
                let kR = kernelInputRanks |> List.tryHead |> Option.defaultValue 0
                let nShared = full.Length - kR
                if nShared <= 0 then [] else full |> List.truncate nShared
        let isCoIter = not (List.isEmpty coIterSharedRecords)
        // For co-iteration, output type spans the co-iterated records (not the
        // operands' outer product).
        let outputType =
            if isCoIter then mkArrayArrow coIterSharedRecords outputElemType None
            else outputType
        let info : TypedApplyInfo = {
            Loop = tLoop; Kernel = resolvedKernel
            Arrays = arrays; Identities = identities
            ArrayTypes = gridArrayTypes; SharedIndexTypes = coIterSharedRecords
            SymcomStates = states; TriangularLevels = triLevels
            // Grid S-dim count from the fiber-retagged array types (consumed
            // fiber dims are now TDimension, excluded from the count). Matches
            // SymcomStates/TriangularLevels and the grid-depth loop nest codegen
            // builds from ArrayTypes. Scalar kernels: irank=0, unchanged.
            SDimsPerArray = computeSDimsPerArray gridArrayTypes
            KernelInputRanks = kernelInputRanks; KernelOutputRank = kernelOutputRank
            KernelTDims = kernelTDims
            SpeedupFactor = speedup; ReynoldsSpeedup = reynoldsSpeedup
            HasReynolds = isReynolds; OutputType = outputType
            IsCoIteration = isCoIter  // derived: non-empty co-iterated records
            IsComposeApply = false
        }
        Ok (mkTyped (TExprApply info) outputType)))

// ============================================================================
// 10c. Lambda, Let, Match, Block, MethodFor, ObjectFor, Struct, For
// ============================================================================

/// Pre-scan type annotations to collect type variable NAMES before lowering.
/// Two sources of type-variable names:
///   1. Explicit `T^N` syntax (TyVar nodes from the parser) — always
///      a type variable, registered unconditionally.
///   2. Implicit bare `T` in type position (TyNamed without args) — treated
///      as a type variable IF the name isn't a registered type or builtin
///      scalar. This implements F#/OCaml-style implicit polymorphism:
///      `function f(x: T) -> T = x` introduces `T` as a fresh type var
///      without requiring `T^0` annotation, and the bare form composes
///      with explicit `T^N` (so `Poly<T^0>, T` works — both refer to
///      the same `T`).
///
/// The recursion walks all TypeExpr variants so type-var names are
/// collected from arbitrarily nested positions (`Array<T like Idx<n>>`,
/// `(T, U)`, `T -> U`, etc.).
///
/// Pass 1: collect names (this function)
/// Pass 2: lower types, creating inference vars lazily (lowerTypeExpr)
and prescanTypeVarNames (env: TypeEnv) (types: TypeExpr option list) : unit =
    let isBuiltinScalar name =
        match name with
        | "Int" | "Int32" | "Int64"
        | "Float" | "Float32" | "Float64" | "Double"
        | "Complex64" | "Complex128"
        | "Bool" | "Void" | "Nat" | "String" | "Char"
        | "Poly" | "Array" -> true
        | _ -> false
    let rec scan ty =
        match ty with
        | TyVar (name, _) ->
            env.Subst.RegisterTypeVarName(name)
        | TyNamed (name, args) ->
            // F#/OCaml-style implicit type vars: a bare name (no args) that
            // isn't a registered type or builtin scalar is an implicit type
            // variable. The check uses lookupTypeDef against the current
            // env, so types declared earlier in the same module are
            // correctly recognized as concrete (and not registered as vars).
            // Forward references to types declared later remain unsupported
            // in Blade — same convention as F#/OCaml.
            if args.IsEmpty
               && not (isBuiltinScalar name)
               && (lookupTypeDef name env).IsNone then
                env.Subst.RegisterTypeVarName(name)
            // Recurse into args regardless — `Array<T like Idx<n>>` has
            // `T` in a nested position.
            args |> List.iter scan
        | TyAbstractArray (elemTy, _, _) -> scan elemTy
        | TyFunc (args, ret) -> args |> List.iter scan; scan ret
        | TyTuple ts -> ts |> List.iter scan
        | TyArray (elemTy, idxTys) -> scan elemTy; idxTys |> List.iter scan
        | TyPoly inner -> scan inner
        | TyConstrained (inner, _) -> scan inner
        | _ -> ()
    types |> List.iter (Option.iter scan)

and inferLambda env parms whereClause body : TypeResult<TypedExpr> =
    let scopeEnv = enterScope env
    let commGroups = extractCommGroups parms whereClause

    // Fresh type variable scope for this lambda's type annotations.
    let savedScope = env.Subst.PushTypeVarScope()

    // Pre-scan: collect type variable names from all annotations.
    prescanTypeVarNames env (parms |> List.map (fun p -> p.Type))

    let mutable paramEnv = scopeEnv
    let typedParams = parms |> List.mapi (fun i p ->
        let varId = env.Builder.FreshId()
        let ty = match p.Type with
                 | Some t -> lowerTypeExpr env t
                 | None -> env.Subst.Fresh()  // Infer from usage
        paramEnv <- bindVarSimple p.Name varId ty paramEnv
        { Name = p.Name; Type = ty; Index = i; VarId = varId } : TypedParam)

    let boundNames = parms |> List.map (fun p -> p.Name) |> Set.ofList
    let freeVars = collectFreeVars boundNames body
    let captures = buildCaptures scopeEnv freeVars

    let result =
        inferExpr paramEnv body |> Result.bind (fun tBody ->
            // A lambda body is a value-forming boundary: reject a wildcard `_`
            // that escaped into it (its only legitimate role is a compound-index
            // coordinate), rather than letting it reach lowering.
            if exprContainsWildcard tBody then
                Error (Other
                    "wildcard `_` is not a value: it cannot be a lambda's body. It is only meaningful as a compound-index coordinate (e.g. B((a, _, c))).")
            else
            let info : TypedLambdaInfo = {
                Params = typedParams; Body = tBody; ReturnType = tBody.Type
                CommGroups = commGroups; Captures = captures
                IsCommutative = not (List.isEmpty commGroups)
                // Propagate the lambda's parallelization strategy (omp/cuda) from
                // its where-clause so lambda-level omp drives parallelization.
                Parallel = (match whereClause with Some wc -> wc.Parallel | None -> [])
            }
            let funcTy = mkFuncArrow (typedParams |> List.map (fun p -> p.Type)) tBody.Type
            Ok (mkTyped (TExprLambda info) funcTy))

    env.Subst.PopTypeVarScope(savedScope)
    result

// ---- Bidirectional checking ----
//
// checkExpr drives an expression to a known target type, pushing the
// expectation into literal/constructor positions. For everything else it
// falls through to inferExpr + unify, which preserves the existing single-
// pass behavior. Currently used by inferLetBinding when an annotation is
// present; same machinery would suit function arg checking later.
//
// Strict policy:
//  - Element count mismatch on Idx<N> against an array literal: error
//  - Heterogeneous numeric literals against a typed array: each element
//    is checked individually; promotion is not applied across elements
//  - No 0/false coercion, no float/int silent narrowing
//
and checkExpr (env: TypeEnv) (expected: IRType) (expr: Expr) : TypeResult<TypedExpr> =
    // Stamp the ambient expression span (mirrors inferExpr) so errors raised
    // here — and in recursive checkExpr calls on sub-expressions — anchor to
    // the innermost offending node (e.g. the specific array-literal row whose
    // length is wrong) instead of the enclosing declaration's whole span.
    if expr.Span.StartLine > 0 then setCurrentExprSpan expr.Span
    let resolved = env.Subst.Resolve expected
    match expr.Kind, resolved with

    // Numeric/scalar literals retype to the expected scalar (numbers stay numbers,
    // bools stay bools — no implicit 0<->false). The literal carries its source-
    // level value but its IRType matches the annotation.
    | ExprKind.ExprLit (LitInt _ as lit), IRTScalar et ->
        match et with
        | ETInt32 | ETInt64 ->
            Ok (mkTyped (TExprLit lit) (IRTScalar et))
        | _ ->
            Error (TypeMismatch (resolved, IRTScalar ETInt64))
    | ExprKind.ExprLit (LitInt _ as lit), IRTIdxTagged (IRTScalar (ETInt32 | ETInt64), _) ->
        // §4.18.3: untyped int literal acquires the index tag from annotation
        // context. `let i: Idx<3> = 0` works; the 0 becomes Nat<Idx<3>>.
        // Strict in the OTHER direction: a bare `Nat` value cannot flow to
        // Nat<I> position without explicit cast — but a LITERAL has no
        // pre-committed type, so context-driven typing applies here.
        Ok (mkTyped (TExprLit lit) resolved)
    | ExprKind.ExprLit (LitInt _ as lit), (IRTNat _ | IRTUnitAnnotated (IRTNat _, _)) ->
        // Same context-driven rule for Nat targets, unit-annotated or bare:
        // `l1: Nat<angular_momentum> = 1` retypes the literal to the target.
        Ok (mkTyped (TExprLit lit) resolved)
    | ExprKind.ExprLit (LitString _ as lit), IRTScalar et ->
        match et with
        | ETString ->
            Ok (mkTyped (TExprLit lit) (IRTScalar et))
        | _ ->
            Error (TypeMismatch (resolved, IRTScalar ETString))
    | ExprKind.ExprLit (LitString _ as lit), IRTIdxTagged (IRTScalar ETString, _) ->
        // §4.18.3 parallel for string-valued index tags (EnumIdx with
        // string values). Same context-driven coercion as the int case.
        Ok (mkTyped (TExprLit lit) resolved)
    | ExprKind.ExprLit (LitFloat _ as lit), IRTScalar et ->
        match et with
        | ETFloat32 | ETFloat64 -> Ok (mkTyped (TExprLit lit) (IRTScalar et))
        | _ -> Error (TypeMismatch (resolved, IRTScalar ETFloat64))
    | ExprKind.ExprLit (LitBool _ as lit), IRTScalar ETBool ->
        Ok (mkTyped (TExprLit lit) (IRTScalar ETBool))

    // Array literal: extract per-rank shape from the annotation and recurse.
    // Outer index supplies the literal's length; inner index types form the
    // element annotation. Elements are checked individually against this.
    | ExprKind.ExprArrayLit elems, ArrayElem arrTy when not arrTy.IndexTypes.IsEmpty ->
        let outerIdx = arrTy.IndexTypes.Head
        let innerIdxs = arrTy.IndexTypes.Tail
        // Length check: if extent is a literal, the count must match.
        let lengthOk =
            match outerIdx.Extent with
            | IRLit (IRLitInt n) -> int n = elems.Length
            | _ -> true  // dynamic / parametric extent: no static check
        if not lengthOk then
            let expectedN =
                match outerIdx.Extent with IRLit (IRLitInt n) -> int n | _ -> -1
            // Name the offending axis by its index tag; suppress synthetic
            // (__anon / __-prefixed) tags, which carry no user-facing name.
            let axisTag =
                match outerIdx.Tag with
                | Some t when not (t.StartsWith("__")) -> Some t
                | _ -> None
            Error (ArrayLitLength (elems.Length, expectedN, axisTag))
        else
            // Build the element annotation: just the elem type if no inner
            // index types, otherwise an array with the remaining index types.
            // arrTy.ElemType is IRType post-B2.
            let elemAnnot =
                if innerIdxs.IsEmpty then arrTy.ElemType
                else mkArrayLike { arrTy with IndexTypes = innerIdxs }
            elems |> List.map (checkExpr env elemAnnot) |> sequenceResults
            |> Result.map (fun tElems ->
                mkTyped (TExprArrayLit (tElems, arrTy)) (mkArrayLike arrTy))

    // fill_random(mod): internal random-fill array constructor. The result
    // array type/shape comes from the annotation (this bidirectional arm), so
    // it is only usable in an annotated position -- without one, `fill_random`
    // synthesizes as an unbound name. The modulus is the argument to
    // rand() % mod and must be an integer. Lowering records it in RandomInits;
    // codegen emits allocate<> + the runtime fill_random.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "fill_random" }, [modE]), ArrayElem _ ->
        checkExpr env (IRTScalar ETInt64) modE |> Result.map (fun tMod ->
            mkTyped (TExprFillRandom tMod) resolved)

    // Tuple literal: zip components against expected component types.
    | ExprKind.ExprTuple exprs, IRTTuple ts when exprs.Length = ts.Length ->
        List.zip exprs ts |> List.map (fun (e, t) -> checkExpr env t e) |> sequenceResults
        |> Result.map (fun tExprs ->
            mkTyped (TExprTuple tExprs) (IRTTuple (tExprs |> List.map (fun e -> e.Type))))

    // Complex literal construction checked against an expected Complex
    // width: `complex(re, im)` adopts the width (Complex64 components are
    // Float32, Complex128 components Float64), so
    // `let z: Complex64 = complex(a, b)` works without a distinct
    // narrow-width constructor. Produces TExprComplexLit (NOT TExprTuple)
    // — the tuple form would lower to IRTuple and lose the scalar nature,
    // flattening an N-element Complex array into N x 2 doubles.
    // TExprComplexLit lowers to IRComplex, a scalar IR node rendered as
    // std::complex<double>(re, im). Both components must be float-typed
    // (no implicit int → float promotion at literal construction time).
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "complex" }, [reExpr; imExpr]), IRTScalar (ETComplex64 | ETComplex128 as cet) when (lookupVar "complex" env).IsNone ->
        let componentTy =
            match cet with
            | ETComplex64 -> IRTScalar ETFloat32
            | _ -> IRTScalar ETFloat64
        checkExpr env componentTy reExpr |> Result.bind (fun tRe ->
        checkExpr env componentTy imExpr |> Result.map (fun tIm ->
            mkTyped (TExprComplexLit (tRe, tIm)) (IRTScalar cet)))

    // The retired 2-tuple complex-literal form gets a steering error
    // rather than a generic mismatch: `(re, im) : Complex128` (and the
    // let-annotation variant) was replaced by the complex(re, im)
    // constructor call, which composes cleanly inside larger expressions
    // (the cast form had a precedence trap: `a * (re, im) : T` bound the
    // cast OUTSIDE the multiply, and the bare tuple operand miscompiled).
    | ExprKind.ExprTuple [_; _], IRTScalar (ETComplex64 | ETComplex128) ->
        Error (Other "complex values are constructed with complex(re, im); the tuple form `(re, im) : Complex128` is no longer supported")

    // Default: synthesize, then unify. This is the path that handles variables,
    // function calls, complex expressions, and any case the special-cases miss.
    | _ ->
        inferExpr env expr |> Result.bind (fun tE ->
            match unify env.Subst tE.Type expected with
            | Ok () -> Ok tE
            | Error _ -> Error (TypeMismatch (expected, tE.Type)))

// ---- Shared helpers for both let paths (let-as-expression and top-level DeclLet) ----

/// Scan a lowered IRType for the `__error_ragged_no_prior` placeholder that
/// lowerTypeExpr plants when RaggedIdx appears as the FIRST index slot (no
/// prior index to drive the per-row lengths lookup, formalism 4.4). Lowering
/// can only produce a degenerate placeholder -- IT cannot Error -- so the
/// annotation consumers (let bindings, function signatures) call this and
/// surface the actual rejection. Without this check the placeholder used to
/// sail through silently.
and irTypeHasRaggedNoPrior (t: IRType) : bool =
    match t with
    | ArrayElem at ->
        (at.IndexTypes |> List.exists (fun ix -> ix.IxKind = IxKErrorRaggedNoPrior))
        || irTypeHasRaggedNoPrior at.ElemType
    | IRTTuple ts -> ts |> List.exists irTypeHasRaggedNoPrior
    | FuncElem (ps, r) -> (ps |> List.exists irTypeHasRaggedNoPrior) || irTypeHasRaggedNoPrior r
    | _ -> false

/// Detect the Dist order sentinel (lowerTypeExpr's TyDist arm lowers a
/// non-static or < 1 order to IRTDist(-1, ...) because it has no error
/// channel). Same consumption-site pattern as irTypeHasRaggedNoPrior:
/// let bindings and function signatures call this and surface the rejection.
and irTypeHasBadDistOrder (t: IRType) : bool =
    match t with
    | IRTDist (n, elem, _) -> n < 1 || irTypeHasBadDistOrder elem
    | ArrayElem at -> irTypeHasBadDistOrder at.ElemType
    | IRTTuple ts -> ts |> List.exists irTypeHasBadDistOrder
    | FuncElem (ps, r) -> (ps |> List.exists irTypeHasBadDistOrder) || irTypeHasBadDistOrder r
    | _ -> false

/// Detect the IrrepsIdx bad-spec marker (lowerIndexType's TyIrrepsIdx arm
/// plants IxKErrorIrrepsBadSpec when the spec is non-static or malformed,
/// smuggling the failure detail in the marker's IRParam extent). Same
/// consumption-site pattern as the two checks above, but returns the detail
/// so the diagnostic can say WHAT was wrong with the spec.
and irTypeBadIrrepsDetail (t: IRType) : string option =
    let detailOf (ix: IRIndexType) =
        if ix.IxKind = IxKErrorIrrepsBadSpec then
            match ix.Extent with
            | IRParam (detail, _, _) -> Some detail
            | _ -> Some "invalid spec"
        else None
    match t with
    | ArrayElem at ->
        (at.IndexTypes |> List.tryPick detailOf)
        |> Option.orElseWith (fun () -> irTypeBadIrrepsDetail at.ElemType)
    | IRTTuple ts -> ts |> List.tryPick irTypeBadIrrepsDetail
    | FuncElem (ps, r) ->
        (ps |> List.tryPick irTypeBadIrrepsDetail)
        |> Option.orElseWith (fun () -> irTypeBadIrrepsDetail r)
    | _ -> None

// inferLetBinding (let-as-expression in function bodies and blocks) and
// checkDecl/DeclLet (top-level let declarations) share their annotation handling
// and PatVar binding logic. Extracting them here keeps the two paths in sync;
// we previously had a bug where one path was updated for bidirectional checking
// and the other wasn't, silently regressing every top-level annotated let.
//
// The two paths still diverge afterward — the expression-form recurses into a
// body, the top-level builds a TypedBinding record and surfaces destructured
// sub-vars to Lowering — so we don't try to unify them entirely.

/// Resolve the value of a let binding: with annotation, drive the value via
/// bidirectional checking and store the annotation as the canonical type;
/// without, plain synthesis.
and inferLetBindingValue (env: TypeEnv) (binding: Binding) : TypeResult<TypedExpr> =
    // A let binding is a value-forming boundary. A wildcard `_` is a hole, not a
    // value: it is only meaningful as a compound-index coordinate (consumed by
    // dispatchAppOrIndex before it reaches here). If one survives into the bound
    // value, it has escaped and is an error — reject cleanly at typecheck rather
    // than let it reach lowering.
    let rejectEscapedWildcard (tv: TypedExpr) : TypeResult<TypedExpr> =
        if exprContainsWildcard tv then
            Error (Other
                "wildcard `_` is not a value: it can only appear as a compound-index coordinate (e.g. B((a, _, c))), not bound in a let. A tuple carrying a hole like (a, _, c) has no meaning on its own.")
        else Ok tv
    match binding.Type with
    | Some annot ->
        let annotTy = lowerTypeExpr env annot
        if irTypeHasRaggedNoPrior annotTy then
            Error (Other "RaggedIdx requires at least one prior index in the array's index list: the ragged extent is a per-row function of the OUTER iteration position (formalism 4.4), so there is nothing for a leading RaggedIdx to vary over. Add an outer index, e.g. Array<T like Idx<n>, RaggedIdx<lens>>.")
        elif irTypeHasBadDistOrder annotTy then
            Error (Other "Dist order must be a compile-time integer >= 1 (a literal, `let static`, or static-function call): Dist<order, Elem like I1, ..., Ik>")
        else
        let badIrreps = irTypeBadIrrepsDetail annotTy
        if badIrreps.IsSome then
            Error (IrrepsIdxSpec badIrreps.Value)
        else
        // Nested-function desugar (parseNestedFunction): `function f(x) -> T
        // = body` becomes a let of a lambda whose binding annotation is the
        // declared RETURN type — there is no surface TypeExpr spelling for
        // "function of unannotated params". Read it accordingly: infer the
        // lambda, then unify its return type with the annotation (a genuine
        // function-type annotation on a lambda still checks structurally
        // below).
        match binding.Value.Kind with
        | ExprKind.ExprLambda _ when (match annotTy with FuncElem _ -> false | _ -> true) ->
            inferExpr env binding.Value |> Result.bind (fun tv ->
                (match env.Subst.Resolve tv.Type with
                 | FuncElem (_, ret) -> unify env.Subst ret annotTy |> Result.map (fun () -> tv)
                 | _ -> Ok tv)
                |> Result.bind rejectEscapedWildcard)
        | _ ->
        checkExpr env annotTy binding.Value |> Result.bind (fun tv ->
            // Prefer the annotation as the canonical type — it can be more
            // specific than what the value synthesized to.
            rejectEscapedWildcard { tv with Type = annotTy })
    | None -> inferExpr env binding.Value |> Result.bind rejectEscapedWildcard

/// Bind the primary name of a let binding (single name or placeholder) with
/// let-generalization. Only ReadOnly bindings are generalized — assignable
/// bindings could be reassigned, making the original scheme unsound.
/// For destructuring patterns (tuple/cons/struct), the caller passes name="_"
/// and identity=None; the returned env then needs further extension with the
/// pattern's sub-names by the caller (which differs by path).
and bindLetPatVar (env: TypeEnv) (name: string) (identity: ArrayIdentity option)
                  (assign: Assignability) (tValue: TypedExpr) : IRId * TypeEnv =
    let varId = env.Builder.FreshId()
    let scheme =
        if assign <> ReadOnly then None
        else
            let s = generalize env.Subst env.Variables tValue.Type
            if s.QuantifiedVars.IsEmpty then None else Some s
    let env' =
        match scheme with
        | Some s -> bindVarPoly name varId tValue.Type identity assign (Some tValue) s env
        | None -> bindVarFull name varId tValue.Type identity assign (Some tValue) env
    (varId, env')

and inferLetBinding env binding body : TypeResult<TypedExpr> =
    // Bidirectional checking pushes annotations into literal/constructor
    // positions — see inferLetBindingValue. Then dispatch on the binding
    // pattern to bind names into the body's environment.
    let valueResult = inferLetBindingValue env binding
    valueResult |> Result.bind (fun tValue ->
        let assign = assignOfBindingMut binding.Mutability
        match binding.Pattern.Kind with
        | PatternKind.PatVar name ->
            let (varId, env') = bindLetPatVar env name (Some (AIDVariable name)) assign tValue
            inferExpr env' body |> Result.map (fun tBody ->
                mkTyped (TExprLet (name, varId, tValue, tBody)) tBody.Type)

        | PatternKind.PatTuple pats ->
            let mutable env' = env
            // Resolve type and determine binding list
            let resolvedTy = env.Subst.Resolve(tValue.Type)
            let typeList =
                match resolvedTy with
                | IRTTuple ts ->
                    if pats.Length = ts.Length then ts
                    else
                        let flat = IR.flattenTupleLeaves resolvedTy
                        if pats.Length = flat.Length then flat
                        else ts
                | _ -> []
            pats |> List.iteri (fun i p ->
                match p.Kind with
                | PatternKind.PatVar n ->
                    let vid = env.Builder.FreshId()
                    let eTy =
                        if i < typeList.Length then env.Subst.Resolve(typeList.[i])
                        else env.Subst.Fresh()
                    env' <- bindVarSimple n vid eTy env'
                | PatternKind.PatWildcard -> ()
                | _ ->
                    for n in patternNames p do
                        env' <- bindVarSimple n (env.Builder.FreshId()) (env.Subst.Fresh()) env')
            inferExpr env' body |> Result.map (fun tBody ->
                let name = pats |> List.tryPick (fun p -> match p.Kind with PatternKind.PatVar n -> Some n | _ -> None)
                           |> Option.defaultValue "_"
                mkTyped (TExprLet (name, env.Builder.FreshId(), tValue, tBody)) tBody.Type)

        | PatternKind.PatCons (headPat, tailPat) ->
            let mutable env' = env
            for n in patternNames headPat @ patternNames tailPat do
                env' <- bindVarSimple n (env.Builder.FreshId()) (env.Subst.Fresh()) env'
            inferExpr env' body |> Result.map (fun tBody ->
                let name = patternNames headPat |> List.tryHead |> Option.defaultValue "_"
                mkTyped (TExprLet (name, env.Builder.FreshId(), tValue, tBody)) tBody.Type)

        | _ ->
            let mutable env' = env
            for n in patternNames binding.Pattern do
                env' <- bindVarSimple n (env.Builder.FreshId()) (env.Subst.Fresh()) env'
            inferExpr env' body |> Result.map (fun tBody ->
                mkTyped (TExprLet ("_", env.Builder.FreshId(), tValue, tBody)) tBody.Type))

and inferMatch env scrutinee cases : TypeResult<TypedExpr> =
    inferExpr env scrutinee |> Result.bind (fun tScrutinee ->
        let resultTy = env.Subst.Fresh()
        cases |> List.map (fun case ->
            checkPattern env tScrutinee.Type case.Pattern |> Result.bind (fun tPat ->
                // Extend env with pattern bindings
                let mutable caseEnv = env
                for (name, varId, ty) in tPat.Bindings do
                    caseEnv <- bindVarSimple name varId ty caseEnv

                // Type-check guard
                let tGuard =
                    case.Guard |> Option.map (fun g ->
                        inferExpr caseEnv g |> Result.map Some)
                    |> Option.defaultValue (Ok None)

                tGuard |> Result.bind (fun guardOpt ->
                inferExpr caseEnv case.Body |> Result.bind (fun tBody ->
                    let _ = unify env.Subst tBody.Type resultTy
                    Ok ({ Pattern = tPat; Guard = guardOpt; Body = tBody } : TypedMatchCase)))))
        |> sequenceResults |> Result.map (fun tCases ->
            let resolvedTy = env.Subst.Resolve resultTy
            mkTyped (TExprMatch (tScrutinee, tCases)) resolvedTy))

and inferBlock env stmts finalExpr : TypeResult<TypedExpr> =
    let mutable curEnv = env
    let mutable err : TypeError option = None
    let mutable typedStmts : TypedStmt list = []

    for stmt in stmts do
        if err.IsNone then
            // Unwrap the parser's span annotation and stamp the statement's
            // location for error reporting (see currentStmtSpanStorage).
            let stmt =
                match stmt with
                | StmtSpanned (inner, sp) -> setCurrentStmtSpan sp; inner
                | s -> s
            match stmt with
            | StmtSpanned _ ->
                // Unreachable: the parser emits exactly one annotation layer,
                // stripped just above. Loud failure beats a skipped statement.
                failwith "inferBlock: nested StmtSpanned"
            | StmtLet binding ->
                // Shared annotation handler (inferLetBindingValue): block
                // lets previously called plain inferExpr and IGNORED the
                // annotation entirely — `let mut vy: Float<velocity> = 19.62`
                // bound vy at the bare synthesized type. Routing through the
                // shared handler gives blocks the same bidirectional
                // checking and annotation-as-canonical-type behavior as
                // top-level and expression-form lets.
                match inferLetBindingValue curEnv binding with
                | Ok tValue ->
                    let name = match binding.Pattern.Kind with PatternKind.PatVar n -> n | _ -> "_"
                    let varId = curEnv.Builder.FreshId()
                    let identity = match binding.Pattern.Kind with PatternKind.PatVar n -> Some (AIDVariable n) | _ -> None
                    let assign = assignOfBindingMut binding.Mutability
                    curEnv <- bindVarFull name varId tValue.Type identity assign (Some tValue) curEnv
                    // Dist provenance: in-block dist lets (e.g. `let h =
                    // 2.0 * d` on a licensed parameter) derive from the RHS.
                    (match curEnv.Subst.Resolve tValue.Type with
                     | IRTDist _ ->
                         let prov = provenanceOfSurface curEnv binding.Value
                         if not (Set.isEmpty prov) then curEnv.Provenance.[varId] <- prov
                     | _ -> ())
                    // Tuple destructuring in a block: bind the leaves AND
                    // record them as SubBindings so Lowering emits their
                    // projection lets (mirrors checkDecl's PatTuple path).
                    // Previously SubBindings was left empty here, so the
                    // leaf VarIds referenced by the rest of the block were
                    // never introduced in the IR — dangling VarId at
                    // validation for any in-body `let (x, y) = p`.
                    let mutable subBindings : (string * IRId * IRType) list = []
                    (match binding.Pattern.Kind with
                     | PatternKind.PatTuple pats ->
                        let resolvedTy = curEnv.Subst.Resolve(tValue.Type)
                        let typeList =
                            match resolvedTy with
                            | IRTTuple ts ->
                                if pats.Length = ts.Length then ts
                                else
                                    // Flat match: (x, y, z) against ((α,β), γ)
                                    let flat = IR.flattenTupleLeaves resolvedTy
                                    if pats.Length = flat.Length then flat else ts
                            | _ -> []
                        pats |> List.iteri (fun i p ->
                            match p.Kind with
                            | PatternKind.PatVar n ->
                                let eTy =
                                    if i < typeList.Length then curEnv.Subst.Resolve(typeList.[i])
                                    else curEnv.Subst.Fresh()
                                let subId = curEnv.Builder.FreshId()
                                subBindings <- subBindings @ [(n, subId, eTy)]
                                curEnv <- bindVarSimple n subId eTy curEnv
                            | _ ->
                                for n in patternNames p do
                                    let subId = curEnv.Builder.FreshId()
                                    let eTy = curEnv.Subst.Fresh()
                                    subBindings <- subBindings @ [(n, subId, eTy)]
                                    curEnv <- bindVarSimple n subId eTy curEnv)
                     | _ -> ())
                    // Mutual-group check-point (block-level twin of the
                    // top-level DeclLet hook).
                    let mutualChecks =
                        match mutualBindingObligation curEnv binding with
                        | Ok None -> []
                        | Ok (Some (group, memberToLeaf)) ->
                            match synthesizeMutualChecks curEnv group memberToLeaf with
                            | Ok checks -> checks
                            | Error e -> err <- Some e; []
                        | Error e -> err <- Some e; []
                    // Constrained-struct binding: check at every assignment.
                    let structChecks =
                        match binding.Pattern.Kind with
                        | PatternKind.PatVar n ->
                            match synthesizeStructChecks curEnv tValue.Type (mkExpr binding.Pattern.Span (ExprVar n)) with
                            | Ok cs -> cs |> List.map (fun c -> (curEnv.Builder.FreshId(), c))
                            | Error e -> err <- Some e; []
                        | _ -> []
                    let postChecks = mutualChecks @ structChecks
                    let tb : TypedBinding = {
                        Name = name; VarId = varId; Type = tValue.Type
                        Identity = identity; IsMutable = (assign <> ReadOnly); Value = tValue
                        SubBindings = subBindings |> List.map (fun (n, id, ty) -> (n, id, curEnv.Subst.Resolve ty))
                        PostChecks = postChecks
                    }
                    typedStmts <- typedStmts @ [TStmtLet tb]
                | Error e -> err <- Some e
            | StmtAssign (lhs, _, rhs) ->
                match inferExpr curEnv lhs, inferExpr curEnv rhs with
                | Ok tL, Ok tR ->
                    // Constrained-struct target: guard after the store.
                    let assignChecks () =
                        match structChecksForAssign curEnv lhs tL with
                        | Ok cs -> cs |> List.map TStmtExpr
                        | Error e -> err <- Some e; []
                    // Check assignability of LHS
                    match tL.Kind with
                    | TExprVar (name, _, _) ->
                        match lookupVar name curEnv with
                        | Some info when info.Assign = ReadOnly ->
                            err <- Some (ImmutableStaticAssign name)
                        | _ ->
                            let _ = unify curEnv.Subst tL.Type tR.Type
                            typedStmts <- typedStmts @ [TStmtAssign (tL, tR)] @ assignChecks ()
                    | _ ->
                        let _ = unify curEnv.Subst tL.Type tR.Type
                        typedStmts <- typedStmts @ [TStmtAssign (tL, tR)] @ assignChecks ()
                | Error e, _ | _, Error e -> err <- Some e
            | StmtExpr e ->
                match inferExpr curEnv e with
                | Ok tE -> typedStmts <- typedStmts @ [TStmtExpr tE]
                | Error e -> err <- Some e
            | StmtForIn (varName, rangeExpr, bodyStmts) ->
                match inferForIn curEnv varName rangeExpr bodyStmts with
                | Ok tStmt -> typedStmts <- typedStmts @ [tStmt]
                | Error e -> err <- Some e

    match err with
    | Some e -> Error e
    | None ->
        match finalExpr with
        | Some e -> inferExpr curEnv e |> Result.map (fun tF ->
            mkTyped (TExprBlock (typedStmts, Some tF)) tF.Type)
        | None -> Ok (mkTyped (TExprBlock (typedStmts, None)) IRTUnit)

/// Infer one for-in loop statement. Recursive so loops nest to any depth
/// (required by the ML-module layers and grad-generated adjoint loops).
/// The loop variable binds as Int64 in the body scope; body lets stay local
/// to the loop (they do NOT leak past it), matching block-scope rules.
and inferForIn (env: TypeEnv) (varName: string) (rangeExpr: Expr) (bodyStmts: Stmt list) : TypeResult<TypedStmt> =
    match rangeExpr.Kind with
    | ExprKind.ExprDotDot (lo, hi) ->
        match inferExpr env lo, inferExpr env hi with
        | Error e, _ | _, Error e -> Error e
        | Ok tLo, Ok tHi ->
            let varId = env.Builder.FreshId()
            let loopEnv = bindVarSimple varName varId (IRTScalar ETInt64) env
            let mutable bodyEnv = loopEnv
            let mutable bodyErr = None
            let mutable typedBodyStmts : TypedStmt list = []
            for bodyStmt in bodyStmts do
                if bodyErr.IsNone then
                    let bodyStmt =
                        match bodyStmt with
                        | StmtSpanned (inner, sp) -> setCurrentStmtSpan sp; inner
                        | s -> s
                    match bodyStmt with
                    | StmtSpanned _ ->
                        failwith "inferForIn: nested StmtSpanned"
                    | StmtLet binding ->
                        match inferExpr bodyEnv binding.Value with
                        | Ok tValue ->
                            let bName = match binding.Pattern.Kind with PatternKind.PatVar n -> n | _ -> "_"
                            let bId = bodyEnv.Builder.FreshId()
                            let assign = assignOfBindingMut binding.Mutability
                            bodyEnv <- bindVarFull bName bId tValue.Type None assign (Some tValue) bodyEnv
                            let tb : TypedBinding = {
                                Name = bName; VarId = bId; Type = tValue.Type
                                Identity = None; IsMutable = (assign <> ReadOnly); Value = tValue
                                SubBindings = []; PostChecks = []
                            }
                            typedBodyStmts <- typedBodyStmts @ [TStmtLet tb]
                        | Error e -> bodyErr <- Some e
                    | StmtAssign (lhs, _, rhs) ->
                        match inferExpr bodyEnv lhs, inferExpr bodyEnv rhs with
                        | Ok tL, Ok tR ->
                            let _ = unify bodyEnv.Subst tL.Type tR.Type
                            // Constrained-struct target: guard after the store.
                            let checks =
                                match structChecksForAssign bodyEnv lhs tL with
                                | Ok cs -> cs |> List.map TStmtExpr
                                | Error e -> bodyErr <- Some e; []
                            typedBodyStmts <- typedBodyStmts @ [TStmtAssign (tL, tR)] @ checks
                        | Error e, _ | _, Error e -> bodyErr <- Some e
                    | StmtExpr e ->
                        match inferExpr bodyEnv e with
                        | Ok tE -> typedBodyStmts <- typedBodyStmts @ [TStmtExpr tE]
                        | Error e -> bodyErr <- Some e
                    | StmtForIn (v2, range2, body2) ->
                        match inferForIn bodyEnv v2 range2 body2 with
                        | Ok tStmt -> typedBodyStmts <- typedBodyStmts @ [tStmt]
                        | Error e -> bodyErr <- Some e
            match bodyErr with
            | Some e -> Error e
            | None -> Ok (TStmtForIn (varName, varId, tLo, tHi, typedBodyStmts))
    | _ -> Error (Other "for-in range must use a..b syntax")

and inferMethodFor env arrays : TypeResult<TypedExpr> =
    // Detect method_for(zip(A, B, ...)) — expand zip into co-iteration
    match arrays with
    | [{ Kind = ExprKind.ExprZip zipExprs }] ->
        zipExprs |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tZipArrays ->
            let identities = zipExprs |> List.map (fun arr ->
                match arr.Kind with ExprKind.ExprVar name -> AIDVariable name | _ -> AIDLiteral (env.Builder.FreshId()))
            let arrayTypes = tZipArrays |> List.mapi (fun i ta ->
                match ta.Type with
                | ArrayElem at -> at
                | _ -> getArrayType env zipExprs.[i])
            // Shared iteration records. Single-record operands (dense rank-1 or
            // packed symmetric) keep the historical first-record rule unchecked.
            // MULTI-record operands (dense rank ≥ 2) co-iterate the FULL product
            // of records — all operands must agree structurally and every record
            // must be plain dense. buildApplyInfo trims the co-iterated prefix
            // by the kernel's slice rank, so row-mode kernels (loops/085) keep
            // receiving their inner-record slice.
            match zipSharedRecords arrayTypes with
            | Error e -> Error e
            | Ok sharedRecords ->
            // Real per-array S-dim counts: buildApplyInfo's IRTInfer fallback
            // computes the kernel slice rank as (records − sDims); a scalar
            // kernel over rank-2 operands must see kR = 0 (full product), which
            // the historical per-array 1 would mis-trim to row mode.
            let sDimsPerArray = computeSDimsPerArray arrayTypes
            let totalSDims = List.sum sDimsPerArray

            let info : TypedMethodForInfo = {
                Arrays = tZipArrays; Identities = identities; ArrayTypes = arrayTypes
                SDimsPerArray = sDimsPerArray; TotalSDims = totalSDims
                SharedIndexTypes = sharedRecords
            }
            let loopTy = IRTLoop {
                Kind = LKMethod; Arity = Some zipExprs.Length
                ArrayTypes = arrayTypes |> List.map mkArrayLike; KernelType = None
            }
            Ok (mkTyped (TExprMethodFor info) loopTy))
    | _ ->
    arrays |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tArrays ->
        // Also detect method_for(Z) where Z was bound to a zip
        match tArrays with
        | [single] when (match (resolveTypedExpr env single).Kind with TExprZip _ -> true | _ -> false) ->
            let resolved = resolveTypedExpr env single
            match resolved.Kind with
            | TExprZip zipExprs ->
                let identities = zipExprs |> List.map (fun te ->
                    match te.Kind with
                    | TExprVar (name, _, _) -> AIDVariable name
                    | _ -> AIDLiteral (env.Builder.FreshId()))
                let arrayTypes = zipExprs |> List.map (fun te ->
                    match te.Type with
                    | ArrayElem at -> at
                    | _ -> { ElemType = IRTScalar ETFloat64; IndexTypes = []; IsVirtual = false; Identity = None })
                match zipSharedRecords arrayTypes with
                | Error e -> Error e
                | Ok sharedRecords ->
                let sDimsPerArray = computeSDimsPerArray arrayTypes
                let totalSDims = List.sum sDimsPerArray
                let info : TypedMethodForInfo = {
                    Arrays = zipExprs; Identities = identities; ArrayTypes = arrayTypes
                    SDimsPerArray = sDimsPerArray; TotalSDims = totalSDims
                    SharedIndexTypes = sharedRecords
                }
                let loopTy = IRTLoop {
                    Kind = LKMethod; Arity = Some zipExprs.Length
                    ArrayTypes = arrayTypes |> List.map mkArrayLike; KernelType = None
                }
                Ok (mkTyped (TExprMethodFor info) loopTy)
            | _ -> failwith "unreachable"
        | _ ->
        let identities = arrays |> List.map (fun arr ->
            match arr.Kind with ExprKind.ExprVar name -> AIDVariable name | _ -> AIDLiteral (env.Builder.FreshId()))
        let arrayTypes = tArrays |> List.mapi (fun i ta ->
            match ta.Type with
            | ArrayElem at -> at
            | _ -> getArrayType env arrays.[i])
        let sDimsPerArray = computeSDimsPerArray arrayTypes
        let totalSDims = List.sum sDimsPerArray

        let info : TypedMethodForInfo = {
            Arrays = tArrays; Identities = identities; ArrayTypes = arrayTypes
            SDimsPerArray = sDimsPerArray; TotalSDims = totalSDims
            SharedIndexTypes = []
        }
        let loopTy = IRTLoop {
            Kind = LKMethod; Arity = Some arrays.Length
            ArrayTypes = arrayTypes |> List.map mkArrayLike; KernelType = None
        }
        Ok (mkTyped (TExprMethodFor info) loopTy))

and inferObjectFor env kernel : TypeResult<TypedExpr> =
    // A bare named-function reference used as an object_for kernel is
    // eta-expanded to lambda(__k..) -> f(__k..), so `object_for(lkm) <@> ...`
    // works symmetrically with `method_for(...) <@> lkm`.
    let kernelResult =
        match etaExpandFunctionKernel env kernel with
        | Some r -> r
        | None -> inferExpr env kernel
    kernelResult |> Result.bind (fun tKernel ->
        let (commGroups, inputRanks, outputRank) =
            match tKernel.Kind with
            | TExprLambda info ->
                let iRanks = info.Params |> List.map (fun p ->
                    match p.Type with ArrayElem arr -> arr.IndexTypes.Length | _ -> 0)
                (info.CommGroups, iRanks, 0)
            | _ -> ([], [], 0)
        let info : TypedObjectForInfo = {
            Kernel = tKernel; CommGroups = commGroups
            InputRanks = inputRanks; OutputRank = outputRank
        }
        let loopTy = IRTLoop {
            Kind = LKObject; Arity = Some inputRanks.Length
            ArrayTypes = []; KernelType = Some tKernel.Type
        }
        Ok (mkTyped (TExprObjectFor info) loopTy))

and inferStructConstruction env name fields (spread: Expr option) : TypeResult<TypedExpr> =
    match lookupTypeDef name env with
    | Some (TDIStruct (_, _, declFields, _)) ->
        let declNames = declFields |> List.map fst
        // Functional update: `S { f = v, ..base }` desugars the MISSING
        // fields to `base.f` reads and falls into the ordinary construction
        // path (dup/unknown/missing checks, per-field bidirectional check,
        // decl-order emission, assignment-site guards — all unchanged).
        let desugared =
            match spread with
            | None -> Ok fields
            | Some baseExpr ->
                // v1 base restriction: a variable / field path / typed wrap —
                // pure, so re-evaluating it once per copied field is safe and
                // no temp binding (with its IRId-ordering interactions) is
                // needed. Anything else: bind it with let first.
                let rec pureBase (e: Expr) =
                    match e.Kind with
                    | ExprKind.ExprVar _ -> true
                    | ExprKind.ExprField (o, _) -> pureBase o
                    | ExprKind.ExprTyped (i, _) -> pureBase i
                    | _ -> false
                if not (pureBase baseExpr) then Error (StructSpreadBase name)
                else
                    inferExpr env baseExpr |> Result.bind (fun tBase ->
                        let resolved =
                            match env.Subst.Resolve tBase.Type with
                            | IRTNamed n ->
                                // Chase one transparent-alias level so a base
                                // typed by an alias of this struct passes.
                                (match lookupTypeDef n env with
                                 | Some (TDIAlias (IRTNamed t)) -> IRTNamed t
                                 | _ -> IRTNamed n)
                            | other -> other
                        let acceptBase () =
                            let providedNames = fields |> List.map fst
                            let missing = declNames |> List.filter (fun f -> not (List.contains f providedNames))
                            if missing.IsEmpty then Error (StructSpreadRedundant name)
                            else
                                Ok (fields @ (missing |> List.map (fun f ->
                                    (f, inheritSpan baseExpr (ExprField (baseExpr, f))))))
                        match resolved with
                        | IRTNamed n when n = name -> acceptBase ()
                        | IRTInfer _ ->
                            // Unresolved base (e.g. a kernel param whose type
                            // unifies with the iterated array's elem type only
                            // later, in buildApplyInfo): the spread base of an
                            // `S { .. }` construction must BE an S, so bind
                            // the variable now and let the constraint flow
                            // back to the param.
                            (match unify env.Subst resolved (IRTNamed name) with
                             | Ok () -> acceptBase ()
                             | Error _ -> Error (StructSpreadNotStruct (name, ppIRType resolved)))
                        | other -> Error (StructSpreadNotStruct (name, ppIRType other)))
        desugared |> Result.bind (fun fields ->
        let providedNames = fields |> List.map fst
        let duplicate =
            providedNames |> List.countBy id
            |> List.tryPick (fun (n, count) -> if count > 1 then Some n else None)
        let unknown = providedNames |> List.tryFind (fun n -> not (List.contains n declNames))
        let missing = declNames |> List.tryFind (fun n -> not (List.contains n providedNames))
        match duplicate, unknown, missing with
        | Some d, _, _ -> Error (StructFieldDuplicate (name, d))
        | _, Some u, _ -> Error (StructNoField (name, u))
        | _, _, Some m -> Error (StructMissingField (name, m))
        | None, None, None ->
            fields |> List.map (fun (fname, fexpr) ->
                // Bidirectional: check the field expr against the declared
                // type so literals adapt (Int64 literal into an Int32 field)
                // exactly as they do in every other checked position.
                let eTy = declFields |> List.find (fun (n, _) -> n = fname) |> snd
                match checkExpr env eTy fexpr with
                | Ok tFE -> Ok (fname, tFE)
                | Error (TypeMismatch (exp, act)) ->
                    Error (StructFieldType (name, fname, ppIRType exp, ppIRType act))
                | Error e -> Error e)
            |> sequenceResults |> Result.map (fun tFields ->
                // Emit fields in DECLARATION order: C++ designated
                // initializers require it, and evaluation order becomes
                // deterministic regardless of the literal's field order.
                let ordered = declNames |> List.map (fun n -> tFields |> List.find (fun (fn, _) -> fn = n))
                mkTyped (TExprStruct (name, ordered)) (IRTNamed name)))
    | Some (TDIAlias (IRTNamed target)) when target <> name ->
        // Transparent alias naming a struct: construct through it.
        inferStructConstruction env target fields spread
    | _ ->
        Error (UnknownStructType name)

// ---- Mutual-group binding-site machinery -----------------------------------
// The joint check attaches exactly where a group's type-tuple is INTRODUCED:
// a function's declared `(P1, P2)` return (checked at the return site) or an
// annotated joint let (checked after the destructure). Detection runs on the
// SURFACE TypeExpr — members are transparent aliases, erased by lowerTypeExpr.

/// Deep-collect mutual-group member names appearing anywhere in a type
/// annotation (one entry per occurrence).
and mutualMemberNamesIn (env: TypeEnv) (t: TypeExpr) : string list =
    let rec walk t =
        match t with
        | TyNamed (n, args) ->
            (if env.MutualMembers.ContainsKey n then [n] else [])
            @ (args |> List.collect walk)
        | TyTuple ts -> ts |> List.collect walk
        | TyArray (e, idxs) -> walk e @ (idxs |> List.collect walk)
        | TyAbstractArray (e, _, _) -> walk e
        | TyFunc (args, ret) -> (args |> List.collect walk) @ walk ret
        | TyDepIdx (outer, _, body) -> walk outer @ walk body
        | TyConstrained (inner, _) -> walk inner
        | TyPoly inner -> walk inner
        | TyEquivIdx (_, g, r) -> walk g @ walk r
        | _ -> []
    walk t

/// Annotation side of the check-point rule. Ok None — no member names
/// anywhere. Ok (Some group) — a top-level tuple listing exactly one group's
/// full member set, each as a DIRECT element, exactly once (non-member
/// elements alongside are fine). Anything else is a compile error.
and tryMutualAnnotation (env: TypeEnv) (annot: TypeExpr) : TypeResult<MutualGroupInfo option> =
    let allOccurrences = mutualMemberNamesIn env annot
    if allOccurrences.IsEmpty then Ok None
    else
        let groupId = env.MutualMembers.[List.head allOccurrences]
        let group = env.MutualGroups.[groupId]
        let groupNames = group.Members |> List.map fst
        let describe = groupNames |> String.concat ", "
        match annot with
        | TyNamed (n, []) ->
            Error (MutualBindJointly (n, describe, (groupNames |> List.map (fun s -> s.ToLower()) |> String.concat ", ")))
        | TyTuple elems ->
            let directNames =
                elems |> List.choose (function
                    | TyNamed (n, []) when env.MutualMembers.ContainsKey n -> Some n
                    | _ -> None)
            if directNames.Length <> allOccurrences.Length then
                Error (MutualDirectElementsOnly describe)
            elif directNames |> List.exists (fun n -> env.MutualMembers.[n] <> groupId) then
                Error MutualMixedGroups
            elif (directNames |> List.distinct |> List.length) <> directNames.Length then
                Error (MutualDuplicateMember describe)
            elif Set.ofList directNames <> Set.ofList groupNames then
                Error (MutualIncompleteAnnotation describe)
            else Ok (Some group)
        | _ ->
            Error (MutualJointAnnotationOnly describe)

/// Rename member references in a decl-validated conjunct to a binding's leaf
/// variable names (bare scalar refs and field-path bases alike).
and renameMutualRefs (mapping: Map<string, string>) (e: Expr) : Expr =
    let r = renameMutualRefs mapping
    match e.Kind with
    | ExprKind.ExprVar n when mapping.ContainsKey n -> inheritSpan e (ExprVar mapping.[n])
    | ExprKind.ExprVar _ | ExprKind.ExprLit _ -> e
    | ExprKind.ExprField (o, f) -> inheritSpan e (ExprField (r o, f))
    | ExprKind.ExprApp (f, args) -> inheritSpan e (ExprApp (r f, args |> List.map r))
    | ExprKind.ExprBinOp (mode, op, l, rr) -> inheritSpan e (ExprBinOp (mode, op, r l, r rr))
    | ExprKind.ExprUnaryOp (op, i) -> inheritSpan e (ExprUnaryOp (op, r i))
    | ExprKind.ExprIf (c, t, f) -> inheritSpan e (ExprIf (r c, r t, r f))
    | ExprKind.ExprTuple es -> inheritSpan e (ExprTuple (es |> List.map r))
    | ExprKind.ExprArrayLit es -> inheritSpan e (ExprArrayLit (es |> List.map r))
    | ExprKind.ExprTyped (i, ty) -> inheritSpan e (ExprTyped (r i, ty))
    | _ -> e  // decl-time validation restricts conjuncts to the forms above

/// Synthesize the joint runtime checks at a binding site. `env` must already
/// have the leaf variables bound; the check IRIds are allocated HERE, after
/// the leaves' ids — module emission is id-ordered, so later ids run later.
and synthesizeMutualChecks (env: TypeEnv) (group: MutualGroupInfo) (memberToLeaf: Map<string, string>) : TypeResult<(IRId * TypedExpr) list> =
    group.Constraints |> List.map (fun conjunct ->
        let renamed = renameMutualRefs memberToLeaf conjunct
        inferExpr env renamed |> Result.map (fun tCond ->
            let checkId = env.Builder.FreshId()
            let msg = sprintf "Mutual constraint violation (%s)" group.GroupId
            (checkId, mkTypedSpan (TExprConstraintCheck (tCond, msg)) IRTUnit tCond.Span)))
    |> sequenceResults

/// Binding side of the check-point rule for an annotated let. Ok None — no
/// obligation here (no group named, or the RHS is a call to a function whose
/// declared return already carries the check). Ok (Some (group, member→leaf))
/// — synthesize checks after the destructure. Error — annotation/pattern
/// misuse (lone member, non-tuple pattern, arity mismatch).
and mutualBindingObligation (env: TypeEnv) (binding: Binding) : TypeResult<(MutualGroupInfo * Map<string, string>) option> =
    match binding.Type with
    | None -> Ok None
    | Some annot ->
        tryMutualAnnotation env annot |> Result.bind (function
            | None -> Ok None
            | Some group ->
                match binding.Pattern.Kind, annot with
                | PatternKind.PatTuple pats, TyTuple elems when
                        pats.Length = elems.Length &&
                        pats |> List.forall (fun p -> match p.Kind with PatternKind.PatVar _ -> true | _ -> false) ->
                    let memberToLeaf =
                        List.zip elems pats
                        |> List.choose (fun (t, p) ->
                            match t, p.Kind with
                            | TyNamed (n, []), PatternKind.PatVar leaf when group.Members |> List.exists (fun (m, _) -> m = n) ->
                                Some (n, leaf)
                            | _ -> None)
                        |> Map.ofList
                    // A call to a declared-return function was already checked
                    // at its return site — the single verification point.
                    let rec stripT (e: Expr) = match e.Kind with ExprKind.ExprTyped (i, _) -> stripT i | _ -> e
                    let alreadyChecked =
                        match (stripT binding.Value).Kind with
                        | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar f }, _) ->
                            match env.MutualReturnFuncs.TryGetValue f with
                            | true, gid -> gid = group.GroupId
                            | _ -> false
                        | _ -> false
                    if alreadyChecked then Ok None
                    else Ok (Some (group, memberToLeaf))
                | _ ->
                    let names = group.Members |> List.map fst |> String.concat ", "
                    Error (MutualBindTuple names))

/// Struct where-constraint checks for an assignment target: substitute each
/// field name with `<target>.<field>`, infer, and wrap as inlined guards.
/// Returns [] when the target's type is not a constrained struct. Fires at
/// every assignment of a constrained struct value — construction bindings,
/// whole-struct reassignment, and field mutation alike (the math runs only
/// in the generated C++).
and synthesizeStructChecks (env: TypeEnv) (targetTy: IRType) (targetSurface: Expr) : TypeResult<TypedExpr list> =
    match env.Subst.Resolve targetTy with
    | IRTNamed sname ->
        match lookupTypeDef sname env with
        | Some (TDIStruct (_, _, declFields, constraints)) when not constraints.IsEmpty ->
            let fieldNames = declFields |> List.map fst |> Set.ofList
            let rec subst (e: Expr) =
                match e.Kind with
                | ExprKind.ExprVar n when fieldNames.Contains n -> inheritSpan e (ExprField (targetSurface, n))
                | ExprKind.ExprVar _ | ExprKind.ExprLit _ -> e
                | ExprKind.ExprField (o, f) -> inheritSpan e (ExprField (subst o, f))
                | ExprKind.ExprApp (f, args) -> inheritSpan e (ExprApp (subst f, args |> List.map subst))
                | ExprKind.ExprBinOp (m, op, l, r) -> inheritSpan e (ExprBinOp (m, op, subst l, subst r))
                | ExprKind.ExprUnaryOp (op, i) -> inheritSpan e (ExprUnaryOp (op, subst i))
                | ExprKind.ExprIf (c, t, f) -> inheritSpan e (ExprIf (subst c, subst t, subst f))
                | ExprKind.ExprTuple es -> inheritSpan e (ExprTuple (es |> List.map subst))
                | ExprKind.ExprArrayLit es -> inheritSpan e (ExprArrayLit (es |> List.map subst))
                | ExprKind.ExprTyped (i, ty) -> inheritSpan e (ExprTyped (subst i, ty))
                | _ -> e
            constraints |> List.mapi (fun i c ->
                inferExpr env (subst c) |> Result.map (fun tCond ->
                    let msg =
                        if constraints.Length = 1 then sprintf "Constraint violation in %s" sname
                        else sprintf "Constraint violation in %s (conjunct %d)" sname (i + 1)
                    mkTypedSpan (TExprConstraintCheck (tCond, msg)) IRTUnit tCond.Span))
            |> sequenceResults
        | _ -> Ok []
    | _ -> Ok []

/// Struct checks for an assignment statement/expression: a field mutation
/// re-checks the OBJECT's constraints; any other target checks the assigned
/// value's own struct type.
and structChecksForAssign (env: TypeEnv) (lhsSurface: Expr) (tL: TypedExpr) : TypeResult<TypedExpr list> =
    match lhsSurface.Kind, tL.Kind with
    | ExprKind.ExprField (objSurface, _), TExprField (tObj, _, _) ->
        synthesizeStructChecks env tObj.Type objSurface
    | _ ->
        synthesizeStructChecks env tL.Type lhsSurface

/// Wrap a declared-return function body so the joint check runs at the
/// return — the group's single verification point. The body becomes:
///   let __mg_ret = <body>
///   let __mg<i> = __mg_ret.<i>   (one per member, at its annotation slot)
///   <conjunct checks>
///   __mg_ret
and wrapMutualReturnBody (env: TypeEnv) (retAnnot: TypeExpr) (group: MutualGroupInfo) (tBody: TypedExpr) : TypeResult<TypedExpr> =
    let retTy = env.Subst.Resolve tBody.Type
    let memberSlots =
        match retAnnot with
        | TyTuple elems ->
            elems |> List.mapi (fun i t -> (i, t))
            |> List.choose (fun (i, t) ->
                match t with
                | TyNamed (n, []) when group.Members |> List.exists (fun (m, _) -> m = n) -> Some (n, i)
                | _ -> None)
        | _ -> []
    if memberSlots.Length <> group.Members.Length then
        Error (MutualReturnTupleElements (group.Members |> List.map fst |> String.concat ", "))
    else
        let rid = env.Builder.FreshId()
        let retVar = mkTyped (TExprVar ("__mg_ret", rid, None)) retTy
        let leaves =
            memberSlots |> List.map (fun (mname, slot) ->
                let kind = group.Members |> List.find (fun (m, _) -> m = mname) |> snd
                let mTy = match kind with MMStruct s -> IRTNamed s | MMScalar t -> t
                (mname, slot, env.Builder.FreshId(), sprintf "__mg%d" slot, mTy))
        let mutable checkEnv = env
        for (_, _, leafId, leafName, mTy) in leaves do
            checkEnv <- bindVarSimple leafName leafId mTy checkEnv
        let mapping = leaves |> List.map (fun (m, _, _, leafName, _) -> (m, leafName)) |> Map.ofList
        synthesizeMutualChecks checkEnv group mapping |> Result.map (fun checks ->
            let intLit i = mkTyped (TExprLit (LitInt (int64 i))) (IRTScalar ETInt64)
            let checkStmts = checks |> List.map (fun (_, c) -> TStmtExpr c)
            let inner = mkTyped (TExprBlock (checkStmts, Some retVar)) retTy
            let withLeaves =
                List.foldBack (fun (_, slot, leafId, leafName, mTy) acc ->
                    let proj = mkTyped (TExprTupleIndex (retVar, intLit slot)) mTy
                    mkTyped (TExprLet (leafName, leafId, proj, acc)) retTy) leaves inner
            mkTyped (TExprLet ("__mg_ret", rid, tBody, withLeaves)) retTy)

and inferForExpr env source kernelOpt : TypeResult<TypedExpr> =
    match source with
    | ForArrays (arrays, Some inClause) ->
        // Co-iteration: for (A, B) in range<Idx<N>> <@> lambda(a, b) -> ...
        // All arrays share the iteration space from the in-clause
        arrays |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tArrays ->
        inferExpr env inClause |> Result.bind (fun tVirtual ->
            // The `in` clause supplies the shared iteration index, so it must
            // be a VIRTUAL array (range<...>, reverse<...>, blocked<...>) —
            // its type is an all-SIdxVirt arrow (ArrayElem's IsVirtual). A
            // stored array, zip, or loop object is rejected here: co-iterating
            // stored arrays is `for (A, B)` with no `in` clause (≡
            // method_for(A, B)); zipping them is method_for(zip(A, B)).
            let sharedRecordsRes =
                match env.Subst.Resolve(tVirtual.Type) with
                | ArrayElem at when at.IsVirtual && at.IndexTypes.Length > 0 ->
                    // ALL of the in-clause's slots become shared iteration
                    // records — `for (A, B) in range<Lat, Lon>` co-iterates the
                    // full Lat×Lon product space. Multi-slot spaces require
                    // every slot plain dense (a sole packed/compound slot is
                    // fine — its Rank levels walk the flat canonical cells).
                    if coIterableRecords at.IndexTypes then Ok at.IndexTypes
                    else Error (Other "for (...) in range<...>: a multi-slot iteration space must consist of plain dense index types (a packed or compound slot cannot share the product with other slots)")
                | resolved ->
                    Error (Other (sprintf "the `in` clause of `for (...) in <source>` must be a virtual array (range<...>, reverse<...>, or blocked<...>) — it supplies the shared iteration index; got %s. Drop the `in` clause to co-iterate stored arrays (for (A, B) ≡ method_for(A, B)), or use method_for(zip(A, B)) to zip them." (ppIRType resolved)))
            sharedRecordsRes |> Result.bind (fun sharedRecords ->

            let identities = arrays |> List.map (fun arr ->
                match arr.Kind with ExprKind.ExprVar name -> AIDVariable name | _ -> AIDLiteral (env.Builder.FreshId()))
            // For co-iteration, all arrays use the shared index space
            let arrayTypes = tArrays |> List.mapi (fun i ta ->
                match ta.Type with
                | ArrayElem at -> at
                | _ -> getArrayType env arrays.[i])
            // Real per-array S-dim counts (see the zip arms: the IRTInfer
            // fallback in buildApplyInfo needs true ranks to compute the
            // kernel slice rank when this loop reaches it via inferApply).
            let sDimsPerArray = computeSDimsPerArray arrayTypes
            let totalSDims = sDimsPerArray |> List.sum

            let mfInfo : TypedMethodForInfo = {
                Arrays = tArrays; Identities = identities; ArrayTypes = arrayTypes
                SDimsPerArray = sDimsPerArray; TotalSDims = totalSDims
                SharedIndexTypes = sharedRecords
            }
            let loopTy = IRTLoop {
                Kind = LKMethod; Arity = Some arrays.Length
                ArrayTypes = arrayTypes |> List.map mkArrayLike; KernelType = None
            }
            let tLoop = mkTyped (TExprMethodFor mfInfo) loopTy
            
            match kernelOpt with
            | Some kernel ->
                // Infer the kernel and build co-iteration ApplyInfo directly
                inferExpr env kernel |> Result.bind (fun tK ->
                    let resolvedKernel = resolveTypedExpr env tK
                    match resolvedKernel.Kind with
                    | TExprLambda lambdaInfo ->
                        // Kernel arity: N (one value param per co-iterated array)
                        // or N + R (values plus the R shared iteration indices).
                        // In the N + R form the in-clause virtual source rides
                        // along as a TRAILING operand — its per-slot params bind
                        // to the loop indices through the same VirtualRange
                        // element machinery the outer-product path uses for
                        // range<...> slots, so `for (uq, ph) in range<Y, X> <@>
                        // lambda(zu, zp, i, j) -> ...` gives the kernel both the
                        // co-iterated values and the (i, j) coordinates.
                        let nOperands = tArrays.Length
                        let idxSlotTypes =
                            sharedRecords |> List.collect (fun r -> List.replicate r.Rank (elemTypeForIterationIndex r))
                        let idxParamCount = idxSlotTypes.Length
                        let wantsIndices = lambdaInfo.Params.Length = nOperands + idxParamCount
                        if not wantsIndices && lambdaInfo.Params.Length <> nOperands then
                            Error (Other (sprintf "for (...) in co-iteration kernel takes %d parameter(s) (one per co-iterated array) or %d (values plus the %d shared iteration indices), got %d" nOperands (nOperands + idxParamCount) idxParamCount lambdaInfo.Params.Length))
                        else
                        // Bind the kernel params to their iterated types: value
                        // params to the operands' ELEMENT types, index params to
                        // the tagged Nat<...> slot types. The body was inferred
                        // against unresolved vars, so deferred intrinsics (e.g.
                        // imag(zp) on a complex operand) only resolve correctly
                        // once the params are unified here — the same post-body
                        // unification buildApplyInfo performs for <@> applies.
                        let paramUnifyResult =
                            let valueTypes = arrayTypes |> List.map (fun at -> at.ElemType)
                            let rowTypes = if wantsIndices then valueTypes @ idxSlotTypes else valueTypes
                            if lambdaInfo.Params.Length = rowTypes.Length then
                                List.zip lambdaInfo.Params rowTypes
                                |> List.fold (fun acc (p, row) ->
                                    acc |> Result.bind (fun () -> unify env.Subst (env.Subst.Resolve p.Type) row))
                                    (Ok ())
                            else Ok ()
                        match paramUnifyResult with
                        | Error e -> Error e
                        | Ok () ->
                        // Extended operand lists: the virtual source appended
                        // LAST so the real arrays keep their param positions.
                        let (exArrays, exIdentities, exTypes) =
                            if wantsIndices then
                                let vAt =
                                    match env.Subst.Resolve tVirtual.Type with
                                    | ArrayElem at -> at
                                    | _ -> { ElemType = IRTScalar ETInt64; IndexTypes = sharedRecords; IsVirtual = true; Identity = None }
                                (tArrays @ [tVirtual],
                                 identities @ [AIDLiteral (env.Builder.FreshId())],
                                 arrayTypes @ [vAt])
                            else (tArrays, identities, arrayTypes)
                        let exSDims = computeSDimsPerArray exTypes
                        let exTotalSDims = List.sum exSDims
                        let exMfInfo : TypedMethodForInfo = {
                            mfInfo with
                                Arrays = exArrays; Identities = exIdentities; ArrayTypes = exTypes
                                SDimsPerArray = exSDims; TotalSDims = exTotalSDims
                        }
                        // Carry the resolved param types into the stored kernel
                        // (records are immutable — the substitution refinements
                        // above don't rewrite lambdaInfo.Params in place).
                        let resolvedLambdaInfo =
                            { lambdaInfo with
                                Params = lambdaInfo.Params |> List.map (fun p -> { p with Type = env.Subst.Resolve p.Type }) }
                        let storedKernel = mkTyped (TExprLambda resolvedLambdaInfo) tK.Type
                        // Infer element type: prefer kernel return type, fall back to arrays.
                        // Phase B2: returns IRType.
                        let elemType =
                            let resolved = env.Subst.Resolve(lambdaInfo.ReturnType)
                            match resolved with
                            | IRTScalar _ as t -> t
                            | ArrayElem arr -> arr.ElemType
                            | IRTUnitAnnotated (IRTScalar _, _) as t -> t
                            | _ ->
                                match arrayTypes with
                                | at :: _ -> at.ElemType
                                | [] -> IRTScalar ETFloat64
                        // Output type: array with shared index structure + kernel T-dims
                        let (kernelTDims, kernelOutputRank) =
                            let resolved = env.Subst.Resolve(lambdaInfo.ReturnType)
                            match resolved with
                            | ArrayElem arr ->
                                let tDims = arr.IndexTypes |> List.map (fun idx -> { idx with Kind = TDimension })
                                (tDims, tDims.Length)
                            | _ -> ([], 0)
                        let outputIndexTypes = sharedRecords @ (kernelTDims |> List.map (fun idx -> { idx with Id = env.Builder.FreshId() }))
                        let outputType = mkArrayArrow outputIndexTypes elemType None
                        // Note: SymcomStates/TriangularLevels/SpeedupFactor are unused
                        // by the co-iteration codegen path — it derives loop structure
                        // directly from SharedIndexTypes
                        let info : TypedApplyInfo = {
                            Loop = mkTyped (TExprMethodFor exMfInfo) loopTy
                            Kernel = storedKernel
                            Arrays = exArrays; Identities = exIdentities
                            ArrayTypes = exTypes; SharedIndexTypes = sharedRecords
                            SymcomStates = List.replicate exTotalSDims SCNeither
                            TriangularLevels = List.replicate exTotalSDims false
                            SDimsPerArray = exSDims
                            KernelInputRanks = lambdaInfo.Params |> List.map (fun _ -> 0)
                            KernelOutputRank = kernelOutputRank
                            KernelTDims = kernelTDims
                            SpeedupFactor = 1L; ReynoldsSpeedup = 1L
                            HasReynolds = false; OutputType = outputType
                            IsCoIteration = true
                            IsComposeApply = false
                        }
                        Ok (mkTyped (TExprApply info) outputType)
                    | _ ->
                        // Fallback: treat as generic apply
                        inferApply env tLoop tK)
            | None -> Ok tLoop)))

    | ForArrays (arrays, None) ->
        // No in-clause: equivalent to method_for(arrays)
        inferMethodFor env arrays |> Result.bind (fun tLoop ->
            match kernelOpt with
            | Some kernel -> inferExpr env kernel |> Result.bind (fun tK -> inferApply env tLoop tK)
            | None -> Ok tLoop)
    | ForKernel kernel ->
        inferObjectFor env kernel |> Result.bind (fun tLoop ->
            match kernelOpt with
            | Some e -> inferExpr env e |> Result.bind (fun tA -> inferApply env tLoop tA)
            | None -> Ok tLoop)

// ============================================================================
// 11. Declaration Type Checking
// ============================================================================

and checkDecl (env: TypeEnv) (decl: Decl) : TypeResult<TypedDecl * TypeEnv> =
    // Fresh declaration: clear any statement span left over from the previous
    // decl's body so errors here can't inherit a stale location (§3.4).
    resetCurrentStmtSpan ()
    match decl with
    | DeclLet binding ->
        // Top-level let: shares value-resolution and primary-name binding with
        // inferLetBinding (the let-as-expression form). Diverges from there in
        // that we surface destructured sub-vars to Lowering and wrap in a
        // TypedBinding rather than recursing into a body expression.
        inferLetBindingValue env binding |> Result.bind (fun tValue ->
            let name = match binding.Pattern.Kind with PatternKind.PatVar n -> n | _ -> "_"
            // Provider load (e.g. `let sample = NetCDF.load("f.nc")`): resolve the
            // module's real struct type at compile time by reading the file
            // metadata, then register the dims/vars structs plus a top-level
            // module struct so field access like `sample.vars.temp` resolves to
            // the variable's real Array type rather than a fresh type var. This
            // mirrors the metadata read that Lowering.tryInvokeProvider performs;
            // the typed value SHAPE is left intact so the lowering-side provider
            // detection still fires. Ordinary (opaque) inference is the fallback
            // when the receiver is not a provider alias or the file can't be read.
            let (env, tValue) =
                match binding.Value.Kind with
                | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, "load") }, [{ Kind = ExprKind.ExprLit (LitString path) }]) ->
                    match providerAliasName env alias with
                    | None -> (env, tValue)
                    | Some pname ->
                        try
                            // Read the store metadata at compile time (the same read
                            // Lowering.tryInvokeProvider performs) and register the
                            // resulting struct types so `sample.vars.<v>` resolves.
                            let spec = (Blade.ProviderRegistry.tryFind pname).Value
                            let pm = spec.LoadAsModule env.Builder name path
                            let (envM, moduleTy) = registerProviderModule env name pm
                            (envM, { tValue with Type = moduleTy })
                        with _ -> (env, tValue)
                | _ -> (env, tValue)
            let identity = match binding.Pattern.Kind with PatternKind.PatVar n -> Some (AIDVariable n) | _ -> None
            let assign = assignOfBindingMut binding.Mutability
            let (varId, env') = bindLetPatVar env name identity assign tValue

            // Dist provenance seeding: a module-level dist binding gets its
            // source-array set from the PPL elaboration state (by name —
            // covers `let d = dist(A, r)` and the call-form combinators);
            // any other Dist-typed value derives conservatively from its
            // RHS (covers operator results: `let s = combine(dx, dy)` etc.).
            (match env.Subst.Resolve tValue.Type with
             | IRTDist _ ->
                 let prov =
                     match Blade.Ppl.Elaborate.Independence.distSources name with
                     | Some s -> s
                     | None -> provenanceOfSurface env binding.Value
                 if not (Set.isEmpty prov) then env.Provenance.[varId] <- prov
             | _ -> ())

            // Handle destructuring at top level — collect sub-bindings for Lowering
            let mutable subBindings : (string * IRId * IRType) list = []
            let env' =
                match binding.Pattern.Kind with
                | PatternKind.PatTuple pats ->
                    // Resolve and determine which type list to use for binding
                    let resolvedTy = env.Subst.Resolve(tValue.Type)
                    let typeList =
                        match resolvedTy with
                        | IRTTuple ts ->
                            if pats.Length = ts.Length then
                                // Structural match: (w, z) against ((α,β), γ) → w:(α,β), z:γ
                                ts
                            else
                                // Try flat match: (x, y, z) against ((α,β), γ) → x:α, y:β, z:γ
                                let flat = IR.flattenTupleLeaves resolvedTy
                                if pats.Length = flat.Length then flat
                                else ts  // Fall back to structural, let fresh vars handle overflow
                        | _ -> []
                    pats |> List.mapi (fun i p -> (i, p))
                    |> List.fold (fun e (i, p) ->
                        match p.Kind with
                        | PatternKind.PatVar n ->
                            let eTy =
                                if i < typeList.Length then env.Subst.Resolve(typeList.[i])
                                else env.Subst.Fresh()
                            let subId = env.Builder.FreshId()
                            subBindings <- subBindings @ [(n, subId, eTy)]
                            bindVarSimple n subId eTy e
                        | _ -> patternNames p |> List.fold (fun e2 n ->
                            let subId = env.Builder.FreshId()
                            let eTy = env.Subst.Fresh()
                            subBindings <- subBindings @ [(n, subId, eTy)]
                            bindVarSimple n subId eTy e2) e) env'
                | PatternKind.PatCons (h, t) ->
                    let e1 = patternNames h |> List.fold (fun e n ->
                        let subId = env.Builder.FreshId()
                        let eTy = env.Subst.Fresh()
                        subBindings <- subBindings @ [(n, subId, eTy)]
                        bindVarSimple n subId eTy e) env'
                    patternNames t |> List.fold (fun e n ->
                        let subId = env.Builder.FreshId()
                        let eTy = env.Subst.Fresh()
                        subBindings <- subBindings @ [(n, subId, eTy)]
                        bindVarSimple n subId eTy e) e1
                | PatternKind.PatStruct (structName, fieldPats) ->
                    // Look up struct field types from the type definition
                    let structFields =
                        match env.Subst.Resolve(tValue.Type) with
                        | IRTNamed sName ->
                            match Map.tryFind sName env.TypeDefs with
                            | Some (TDIStruct (_, _, fields, _)) -> fields
                            | _ -> []
                        | _ -> []
                    let fieldTypeMap = Map.ofList structFields
                    fieldPats |> List.fold (fun e (fieldName, pat) ->
                        match pat.Kind with
                        | PatternKind.PatVar n ->
                            let subId = env.Builder.FreshId()
                            let eTy = Map.tryFind fieldName fieldTypeMap
                                      |> Option.defaultWith (fun () -> env.Subst.Fresh())
                            subBindings <- subBindings @ [(n, subId, eTy)]
                            bindVarSimple n subId eTy e
                        | _ -> patternNames pat |> List.fold (fun e2 n ->
                            let subId = env.Builder.FreshId()
                            let eTy = env.Subst.Fresh()
                            subBindings <- subBindings @ [(n, subId, eTy)]
                            bindVarSimple n subId eTy e2) e) env'
                | _ -> env'

            // Mutual-group check-point: an annotation naming a group makes
            // this let the introduce-site (unless the RHS is a call already
            // checked at its declared return). Check IRIds allocate after
            // the sub-binding ids, so id-ordered emission runs them last.
            mutualBindingObligation env binding |> Result.bind (fun obligation ->
            let mutualChecksR =
                match obligation with
                | Some (group, memberToLeaf) -> synthesizeMutualChecks env' group memberToLeaf
                | None -> Ok []
            mutualChecksR |> Result.bind (fun mutualChecks ->
            // Constrained-struct binding: check at every assignment site.
            let structChecksR =
                match binding.Pattern.Kind with
                | PatternKind.PatVar n -> synthesizeStructChecks env' tValue.Type (mkExpr binding.Pattern.Span (ExprVar n))
                | _ -> Ok []
            structChecksR |> Result.map (fun structChecks ->
            let postChecks =
                mutualChecks @ (structChecks |> List.map (fun c -> (env.Builder.FreshId(), c)))
            let tb : TypedBinding = {
                Name = name; VarId = varId; Type = env.Subst.Resolve(tValue.Type)
                Identity = identity; IsMutable = (assign <> ReadOnly); Value = tValue
                SubBindings = subBindings |> List.map (fun (n, id, ty) -> (n, id, env.Subst.Resolve ty))
                PostChecks = postChecks
            }
            (TDeclLet tb, env')))))

    | DeclStatic binding ->
        // Use the shared annotation handler so `let static` bindings enforce
        // their type annotations the same way regular lets do. Without this,
        // an annotation like `let static x: Float<meters> = 100.0` was
        // silently dropped — the binding would carry the synthesized type
        // instead.
        //
        // RESOLVED-VALUE SHORTCUT: when StaticEval already reduced this
        // binding to a value (env.StaticValues, populated in checkModule),
        // type the binding from that VALUE rather than re-inferring the
        // original expression. Static-only builtins (sh_spec, total_dim,
        // tp_weight_dim, length, ...) have no runtime binding, so inferring
        // their call expressions would fail with "unbound variable" even
        // though the static evaluator handled them fine.
        let staticShortcut =
            match binding.Pattern.Kind with
            | PatternKind.PatVar n ->
                match Map.tryFind n env.StaticValues with
                | Some sv ->
                    let rec svToTyped (v: StaticEval.StaticValue) : TypedExpr =
                        match v with
                        | StaticEval.SVInt i -> mkTyped (TExprLit (LitInt i)) (IRTScalar ETInt64)
                        | StaticEval.SVFloat f -> mkTyped (TExprLit (LitFloat f)) (IRTScalar ETFloat64)
                        | StaticEval.SVBool b -> mkTyped (TExprLit (LitBool b)) (IRTScalar ETBool)
                        | StaticEval.SVString s -> mkTyped (TExprLit (LitString s)) (IRTScalar ETString)
                        | StaticEval.SVUnit -> mkTyped (TExprLit LitUnit) IRTUnit
                        | StaticEval.SVTuple vs ->
                            let ts = vs |> List.map svToTyped
                            mkTyped (TExprTuple ts) (IRTTuple (ts |> List.map (fun t -> t.Type)))
                        | StaticEval.SVStruct (sname, sfields) ->
                            // Fields are already in declaration order (the
                            // ExprStruct fold orders them via the struct
                            // registry), as C++ designated initializers require.
                            let tFields = sfields |> List.map (fun (fn, fv) -> (fn, svToTyped fv))
                            mkTyped (TExprStruct (sname, tFields)) (IRTNamed sname)
                    Some (svToTyped sv)
                | None -> None
            | _ -> None
        let inferred =
            match staticShortcut with
            | Some tv -> Ok tv
            | None -> inferLetBindingValue env binding
        inferred |> Result.bind (fun tValue ->
            let name = match binding.Pattern.Kind with PatternKind.PatVar n -> n | _ -> "_"
            // Reuse pre-pass varId if this static was already pre-registered
            let varId =
                match lookupVar name env with
                | Some existing ->
                    // Unify pre-pass type with checked type so forward references resolve
                    let _ = unify env.Subst existing.Type tValue.Type
                    existing.VarId
                | None -> env.Builder.FreshId()
            // Static bindings are ReadOnly and generalizable
            let scheme =
                let s = generalize env.Subst env.Variables tValue.Type
                if s.QuantifiedVars.IsEmpty then None else Some s
            let env' =
                match scheme with
                | Some s -> bindVarPoly name varId tValue.Type None ReadOnly (Some tValue) s env
                | None -> bindVarFull name varId tValue.Type None ReadOnly (Some tValue) env

            // Tuple destructuring for `let static (a, b) = expr`. Mirrors the
            // PatTuple branch of DeclLet — sub-bindings become individually
            // resolvable names downstream. PatCons / PatStruct destructuring
            // for static bindings is not yet covered (no test pressure yet).
            let mutable subBindings : (string * IRId * IRType) list = []
            let env'' =
                match binding.Pattern.Kind with
                | PatternKind.PatTuple pats ->
                    let resolvedTy = env.Subst.Resolve(tValue.Type)
                    let typeList =
                        match resolvedTy with
                        | IRTTuple ts ->
                            if pats.Length = ts.Length then ts
                            else
                                let flat = IR.flattenTupleLeaves resolvedTy
                                if pats.Length = flat.Length then flat else ts
                        | _ -> []
                    pats |> List.mapi (fun i p -> (i, p))
                    |> List.fold (fun e (i, p) ->
                        match p.Kind with
                        | PatternKind.PatVar n ->
                            let eTy =
                                if i < typeList.Length then env.Subst.Resolve(typeList.[i])
                                else env.Subst.Fresh()
                            let subId = env.Builder.FreshId()
                            subBindings <- subBindings @ [(n, subId, eTy)]
                            bindVarSimple n subId eTy e
                        | _ -> patternNames p |> List.fold (fun e2 n ->
                            let subId = env.Builder.FreshId()
                            let eTy = env.Subst.Fresh()
                            subBindings <- subBindings @ [(n, subId, eTy)]
                            bindVarSimple n subId eTy e2) e) env'
                | _ -> env'

            let tb : TypedBinding = {
                Name = name; VarId = varId; Type = env.Subst.Resolve(tValue.Type)
                Identity = None; IsMutable = false; Value = tValue
                SubBindings = subBindings |> List.map (fun (n, id, ty) -> (n, id, env.Subst.Resolve ty))
                PostChecks = []
            }
            Ok (TDeclStatic tb, env''))

    | DeclFunction funcDecl -> checkFunctionDecl env funcDecl

    | DeclType typeDecl ->
        registerTypeDecl env typeDecl |> Result.bind (fun env' ->
            let ttdResult =
                match typeDecl with
                | TyDeclAlias (name, typeParams, body) ->
                    // Distinguish index-type aliases (Idx, SymIdx, ..., EnumIdx) from
                    // ordinary type aliases. Both register in env.TypeDefs the same
                    // way; the typed-AST distinction is what survives into Lowering
                    // and CodeGen, where index aliases need different rendering than
                    // generic IRType aliases (using Name = int64_t; rather than a
                    // promote<>-based template expansion).
                    match Map.tryFind name env'.TypeDefs with
                    | Some (TDIIndexType (_, idx, _)) ->
                        Ok (TTDIndexType (name, idx))
                    | Some (TDIEnumIdx (_, idx, values, _)) ->
                        Ok (TTDEnumIdx (name, idx, values))
                    | _ ->
                        Ok (TTDAlias (name, typeParams, lowerTypeExpr env' body))
                | TyDeclStruct (name, typeParams, fields, _constraints) ->
                    // Constraint validation happened in registerTypeDecl;
                    // checks materialize per assignment site (PostChecks /
                    // TExprConstraintCheck), not on the type def.
                    let resolvedFields = fields |> List.map (fun f -> (f.Name, lowerTypeExpr env' f.Type))
                    Ok (TTDStruct (name, typeParams, resolvedFields))
                | TyDeclSum (name, typeParams, variants) ->
                    let resolvedVariants = variants |> List.map (fun v ->
                        (v.Name, v.Data |> Option.map (lowerTypeExpr env')))
                    Ok (TTDVariant (name, typeParams, resolvedVariants))
                | TyDeclMutualGroup (members, _) ->
                    // Constraint validation happened in registerTypeDecl; the
                    // typed decl just carries the member aliases for lowering.
                    Ok (TTDMutualGroup (members |> List.map (fun (mname, mty) ->
                        (mname, lowerTypeExpr env' mty))))
            ttdResult |> Result.map (fun ttd -> (TDeclType ttd, env')))

    | DeclInterface ifaceDecl -> 
        let env' = { env with Interfaces = Map.add ifaceDecl.Name ifaceDecl env.Interfaces }
        Ok (TDeclInterface ifaceDecl, env')
    | DeclImpl implDecl -> 
        // Resolve the concrete type name
        let typeName = 
            match implDecl.ForType with
            | TyNamed (name, _) -> Some name
            | _ -> None
        match typeName with
        | Some tName ->
            // Register all mangled method names first (enables mutual recursion within impl block)
            let mutable env' = env
            let methodIds = implDecl.Methods |> List.map (fun method ->
                let mangledName = sprintf "%s__%s" tName method.Name
                let selfType = IRTNamed tName
                let paramTypes = method.Params |> List.map (fun p ->
                    if p.Name = "self" && p.Type.IsNone then selfType
                    else match p.Type with Some t -> lowerTypeExpr env' t | None -> IRTScalar ETFloat64)
                let retType = match method.ReturnType with
                              | Some t -> lowerTypeExpr env' t
                              | None -> env'.Subst.Fresh()
                let funcType = mkFuncArrow paramTypes retType
                let funcVarId = env'.Builder.FreshId()
                env' <- bindVarSimple mangledName funcVarId funcType env'
                env' <- { env' with ImplMethods = Map.add (tName, method.Name) (funcVarId, funcType) env'.ImplMethods }
                (mangledName, funcVarId, paramTypes, retType))

            // Validate interface if specified
            match Map.tryFind implDecl.Interface env.Interfaces with
            | Some ifaceDecl ->
                let missing = ifaceDecl.Methods |> List.filter (fun ifaceMethod ->
                    not (implDecl.Methods |> List.exists (fun m -> m.Name = ifaceMethod.Name)))
                match missing with
                | _ :: _ ->
                    let names = missing |> List.map (fun m -> m.Name) |> String.concat ", "
                    Error (ImplMissingMethods (implDecl.Interface, tName, names))
                | [] -> Ok ()
            | None -> Ok ()
            |> Result.bind (fun () ->
                // Type-check each method body
                let selfType = IRTNamed tName
                let mutable typedMethods = []
                let mutable methodErr = None
                for (method, (mangledName, funcVarId, paramTypes, retType)) in List.zip implDecl.Methods methodIds do
                    if methodErr.IsNone then
                        let savedScope = env'.Subst.PushTypeVarScope()
                        let mutable bodyEnv = enterScope env'
                        let typedParams = method.Params |> List.mapi (fun i p ->
                            let varId = env'.Builder.FreshId()
                            let ty =
                                if p.Name = "self" && p.Type.IsNone then selfType
                                else paramTypes.[i]
                            bodyEnv <- bindVarSimple p.Name varId ty bodyEnv
                            { Name = p.Name; Type = ty; Index = i; VarId = varId } : TypedParam)
                        match inferExpr bodyEnv method.Body with
                        | Ok tBody ->
                            let _ = unify env'.Subst tBody.Type retType
                            let commGroups =
                                extractCommGroups
                                    (method.Params |> List.map (fun p -> { Name = p.Name; Type = p.Type } : LambdaParam))
                                    method.WhereClause
                            let tf : TypedFunctionDecl = {
                                Name = mangledName; FuncId = funcVarId
                                TypeParams = method.TypeParams
                                Params = typedParams; ReturnType = tBody.Type
                                WhereClause = method.WhereClause; Body = tBody
                                CommGroups = commGroups; IsStatic = false
                            }
                            typedMethods <- typedMethods @ [tf]
                        | Error e -> methodErr <- Some e
                        env'.Subst.PopTypeVarScope(savedScope)
                match methodErr with
                | Some e -> Error e
                | None ->
                    let timpl : TypedImplDecl = {
                        ForType = implDecl.ForType
                        TypeName = tName
                        Methods = typedMethods
                    }
                    Ok (TDeclImpl timpl, env'))
        | None ->
            // Can't resolve type name — pass through with empty methods
            let timpl : TypedImplDecl = {
                ForType = implDecl.ForType
                TypeName = "_"
                Methods = []
            }
            Ok (TDeclImpl timpl, env)
    | DeclUnit unitDecl ->
        let env' = registerUnit env unitDecl
        Ok (TDeclUnit unitDecl, env')
    | DeclImport (qname, style) when (not qname.IsEmpty) && qname.Head = "Providers" ->
        // The pre-module provider spelling (`import Providers.NetCDF as X`)
        // is a hard break: providers are ordinary modules now.
        let suggestion =
            match qname with
            | [_; sub] -> sub.ToLowerInvariant()
            | _ -> "netcdf"
        Error (ProviderImportByModule (suggestion, (Blade.ProviderRegistry.names () |> String.concat ", ")))
    | DeclImport ([pname], ImportSelective _) when (Blade.ProviderRegistry.tryFind pname).IsSome
                                                   && not (Map.containsKey pname env.ModuleExports) ->
        // Providers expose load/read/write through a qualified alias only;
        // there are no free-standing names to import selectively.
        Error (ProviderNoSelectiveImport pname)
    | DeclImport (qname, style) ->
        let fullName = String.concat "." qname
        let env' =
            match Map.tryFind fullName env.ModuleExports with
            | Some exports ->
                match style with
                | ImportQualified aliasOpt ->
                    let alias = aliasOpt |> Option.defaultValue (List.last qname)
                    // Register all exported variables as alias.name
                    let mutable e = env
                    for kv in exports.Variables do
                        let qualName = sprintf "%s.%s" alias kv.Key
                        e <- bindVar qualName kv.Value e
                    for kv in exports.TypeDefs do
                        e <- registerTypeDef kv.Key kv.Value e
                    for kv in exports.VariantTags do
                        e <- { e with VariantTags = Map.add kv.Key kv.Value e.VariantTags }
                    for kv in exports.Units do
                        e <- { e with Units = Map.add kv.Key kv.Value e.Units }
                    // Static functions: register under alias.name. Bare-name
                    // references to these would need parser support for
                    // dotted names in eta-reduced DepIdx position; for now,
                    // qualified-import use sites of static functions only
                    // work in expression contexts where dotted names parse.
                    for kv in exports.StaticFunctions do
                        let qualName = sprintf "%s.%s" alias kv.Key
                        e <- { e with StaticFunctions = Map.add qualName kv.Value e.StaticFunctions }
                    e
                | ImportSelective names ->
                    let mutable e = env
                    for name in names do
                        match Map.tryFind name exports.Variables with
                        | Some vi -> e <- bindVar name vi e
                        | None -> ()
                        match Map.tryFind name exports.TypeDefs with
                        | Some tdi -> e <- registerTypeDef name tdi e
                        | None -> ()
                        match Map.tryFind name exports.StaticFunctions with
                        | Some fd ->
                            e <- { e with StaticFunctions = Map.add name fd e.StaticFunctions }
                        | None -> ()
                    e
            | None ->
                // Provider or unknown module — bind alias as opaque so references type-check
                // (actual types resolved during lowering when provider runs)
                match style with
                | ImportQualified aliasOpt ->
                    let alias = aliasOpt |> Option.defaultValue (List.last qname)
                    let varId = env.Builder.FreshId()
                    bindVarSimple alias varId (IRTNamed (fullName)) env
                | ImportSelective _ -> env
        Ok (TDeclImport (qname, style), env')

and checkFunctionDecl (env: TypeEnv) (funcDecl: FunctionDecl) : TypeResult<TypedDecl * TypeEnv> =
    // Fresh type variable scope for this function's type annotations.
    let savedScope = env.Subst.PushTypeVarScope()

    // Pre-scan all parameter + return type annotations to register type variable names.
    let allAnnotations =
        (funcDecl.Params |> List.map (fun p -> p.Type))
        @ [funcDecl.ReturnType]
    prescanTypeVarNames env allAnnotations

    let paramTypes = funcDecl.Params |> List.map (fun p ->
        match p.Type with Some t -> lowerTypeExpr env t | None -> env.Subst.Fresh())
    let retType = match funcDecl.ReturnType with
                  | Some t -> lowerTypeExpr env t
                  | None -> env.Subst.Fresh()
    if (paramTypes |> List.exists irTypeHasRaggedNoPrior) || irTypeHasRaggedNoPrior retType then
        Error (RaggedIdxNeedsPrior funcDecl.Name)
    elif (paramTypes |> List.exists irTypeHasBadDistOrder) || irTypeHasBadDistOrder retType then
        Error (DistOrderCompileTime funcDecl.Name)
    else
    let badIrreps = (paramTypes @ [retType]) |> List.tryPick irTypeBadIrrepsDetail
    if badIrreps.IsSome then
        Error (IrrepsIdxSpecFn (funcDecl.Name, badIrreps.Value))
    else
    let funcType = mkFuncArrow paramTypes retType
    // Reuse pre-pass varId if this function was already pre-registered (static functions)
    // This ensures other functions' bodies reference the same varId
    let funcVarId =
        match lookupVar funcDecl.Name env with
        | Some existing -> existing.VarId
        | None -> env.Builder.FreshId()
    // Register function BEFORE body (enables recursion)
    let envWithFunc = bindVarSimple funcDecl.Name funcVarId funcType env

    // `x: mut T` params bind MutPassable so the body may assign into them
    // (gradient out-buffers). Array-typed only: the C++ ABI passes the
    // Array<> wrapper by value, which aliases the caller's DATA (shallow
    // pointer copy) — element writes land in the caller — but a scalar
    // passed by value would silently drop its writes.
    let mutParamErr =
        funcDecl.Params |> List.tryPick (fun p ->
            if p.Mutability = Mutable then
                let i = funcDecl.Params |> List.findIndex (fun q -> q.Name = p.Name)
                match env.Subst.Resolve paramTypes.[i] with
                | ArrayElem _ -> None
                | _ -> Some (MutParamNotArray (funcDecl.Name, p.Name))
            else None)
    match mutParamErr with
    | Some e ->
        env.Subst.PopTypeVarScope(savedScope)
        Error e
    | None ->

    // Mutual-group scans. Member types are forbidden in parameter positions
    // (alias transparency would silently erase the constraint); a declared
    // return tuple naming a full group makes this function the group's
    // introduce-site — checks emit at the return, annotated callers exempt.
    let mutualParamErr =
        funcDecl.Params |> List.tryPick (fun p ->
            match p.Type with
            | Some t ->
                match mutualMemberNamesIn env t with
                | [] -> None
                | n :: _ -> Some (MutualParamMemberType (funcDecl.Name, p.Name, n))
            | None -> None)
    match mutualParamErr with
    | Some e ->
        env.Subst.PopTypeVarScope(savedScope)
        Error e
    | None ->
    match (match funcDecl.ReturnType with Some t -> tryMutualAnnotation env t | None -> Ok None) with
    | Error e ->
        env.Subst.PopTypeVarScope(savedScope)
        Error e
    | Ok mutualReturnGroup ->
    (match mutualReturnGroup with
     | Some g -> env.MutualReturnFuncs.[funcDecl.Name] <- g.GroupId
     | None -> ())

    // Custom where-clause conjuncts (`where <name>(<args>)` for names the
    // grammar doesn't own): dispatch each through the Blade.Constraints
    // registry. Validate at the signature; record the function for
    // call-site discharge; the license scope opens around body checking
    // below. An unregistered name errors with the registered vocabulary.
    let paramNames = funcDecl.Params |> List.map (fun p -> p.Name)
    let customConjuncts =
        funcDecl.WhereClause |> Option.map (fun w -> w.Custom) |> Option.defaultValue []
    let conjunctErr =
        customConjuncts |> List.tryPick (fun (cname, cargs) ->
            match Blade.Constraints.lookupConstraint cname with
            | None ->
                // Module-owned keywords are registered under mangled names
                // ("__ppl_indep") and reached via a qualified conjunct that
                // the owning module's elaborator normalizes. A bare (or
                // wrongly-qualified) use of such a keyword gets a targeted
                // hint; the vocabulary list shows the module spelling.
                let bare = match cname.Split('.') with [| _; n |] -> n | _ -> cname
                if (Blade.Constraints.lookupConstraint ("__ppl_" + bare)).IsSome then
                    Some (PplConstraintNeedsImport (funcDecl.Name, bare))
                else
                let known =
                    Blade.Constraints.registeredConstraintNames ()
                    |> List.map (fun n ->
                        if n.StartsWith "__ppl_" then "ppl." + n.Substring 6
                        elif n.StartsWith "__ml_" then "ml." + n.Substring 5
                        else n)
                let vocab = if known.IsEmpty then "none registered" else String.concat ", " known
                Some (UnknownWhereConstraint (funcDecl.Name, cname, vocab))
            | Some h ->
                match h.Validate funcDecl.Name paramNames cargs with
                | Ok () -> None
                | Error msg -> Some (Other msg))
    match conjunctErr with
    | Some e ->
        env.Subst.PopTypeVarScope(savedScope)
        Error e
    | None ->
    if not customConjuncts.IsEmpty then
        env.FuncConstraints.[funcDecl.Name] <- (paramNames, customConjuncts)

    let mutable bodyEnv = enterScope envWithFunc
    let typedParams = funcDecl.Params |> List.mapi (fun i p ->
        let varId = env.Builder.FreshId()
        let assign = match p.Mutability with
                     | Mutable -> MutPassable
                     | _ -> ReadOnly
        bodyEnv <- bindVarFull p.Name varId paramTypes.[i] None assign None bodyEnv
        // Dist-typed parameters carry their license token as provenance —
        // the `where indep` handler licenses exactly these tokens for the
        // body, and call-site discharge maps them back to actuals.
        (match env.Subst.Resolve paramTypes.[i] with
         | IRTDist _ ->
             env.Provenance.[varId] <- Set.singleton (Blade.Constraints.paramProvenanceToken funcDecl.Name p.Name)
         | _ -> ())
        { Name = p.Name; Type = paramTypes.[i]; Index = i; VarId = varId } : TypedParam)

    // Open the license scope for the body; closed after `result` is
    // computed (both success and error paths flow past the exit below).
    for (cname, cargs) in customConjuncts do
        Blade.Constraints.lookupConstraint cname |> Option.iter (fun h -> h.EnterBody funcDecl.Name cargs)

    let result =
        // When a return type is annotated, drive the body bidirectionally
        // via checkExpr. This pushes the expected type into literal and
        // tuple-constructor positions so that e.g. `(4, 1)` retypes its
        // elements against `(StationIdx, Idx<3>)` (giving the literals
        // their named-index types directly) rather than synthesizing
        // `(Int64, Int64)` and then failing to unify against the named
        // tuple. Without an annotation, fall back to plain inference —
        // there's no expectation to push.
        let bodyResult =
            match funcDecl.ReturnType with
            | Some _ -> checkExpr bodyEnv retType funcDecl.Body
            | None -> inferExpr bodyEnv funcDecl.Body
        bodyResult |> Result.bind (fun tBody ->
            // A function body is a value-forming boundary: a wildcard `_` that
            // survives into it has escaped its only legitimate role (a compound-
            // index coordinate). Reject cleanly here rather than at lowering.
            if exprContainsWildcard tBody then
                Error (Other
                    "wildcard `_` is not a value: it cannot be a function's returned value. It is only meaningful as a compound-index coordinate (e.g. B((a, _, c))).")
            else
            // Belt-and-suspenders: even after checkExpr, run unify on the
            // synthesized body type vs the annotation. checkExpr's fall-
            // through case (line ~3082) is `inferExpr + unify` already,
            // and the special-cased shapes (literals, tuples) build a
            // TypedExpr whose .Type matches the expected type by
            // construction — so this unify is mostly a no-op when
            // checkExpr was used. When the bodyResult came from inferExpr
            // (no annotation), retType is itself a fresh inference var
            // and the unify just binds it. Either way we propagate the
            // result so genuine mismatches surface here rather than
            // exploding at codegen.
            unify env.Subst tBody.Type retType |> Result.bind (fun () ->
            // Declared-return introduce-site: wrap the body so the joint
            // check fires at the return (the single verification point).
            let wrappedBodyR =
                match mutualReturnGroup, funcDecl.ReturnType with
                | Some group, Some retAnnot -> wrapMutualReturnBody envWithFunc retAnnot group tBody
                | _ -> Ok tBody
            wrappedBodyR |> Result.bind (fun tBody ->
            let commGroups =
                extractCommGroups
                    (funcDecl.Params |> List.map (fun p -> { Name = p.Name; Type = p.Type } : LambdaParam))
                    funcDecl.WhereClause
            let resolvedParams = typedParams |> List.map (fun p ->
                { p with Type = env.Subst.Resolve(p.Type) } : TypedParam)
            let tf : TypedFunctionDecl = {
                Name = funcDecl.Name; FuncId = funcVarId
                TypeParams = funcDecl.TypeParams
                Params = resolvedParams; ReturnType = env.Subst.Resolve(retType)
                WhereClause = funcDecl.WhereClause; Body = tBody
                CommGroups = commGroups; IsStatic = funcDecl.IsStatic
            }
            Ok (TDeclFunction tf, envWithFunc))))

    // Close the license scope (error paths included — `result` has
    // materialized either way by this point).
    for (cname, cargs) in customConjuncts do
        Blade.Constraints.lookupConstraint cname |> Option.iter (fun h -> h.ExitBody funcDecl.Name cargs)

    env.Subst.PopTypeVarScope(savedScope)
    result

/// Where-clause predicate contract: a static function called from a
/// struct/mutual where-conjunct must have fully annotated params + return.
/// Its pre-pass type is created ONCE (shared tyvars across every call
/// site), so an unannotated predicate unified against one owner's field
/// types would silently over-constrain the next — annotations pin the
/// contract instead.
and private wherePredicateAnnotationCheck (env: TypeEnv) (owner: string) (conjuncts: Expr list) : TypeResult<unit> =
    let rec heads (e: Expr) : string list =
        match e.Kind with
        | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar f }, args) -> f :: (args |> List.collect heads)
        | ExprKind.ExprApp (h, args) -> heads h @ (args |> List.collect heads)
        | ExprKind.ExprBinOp (_, _, l, r) -> heads l @ heads r
        | ExprKind.ExprUnaryOp (_, i) -> heads i
        | ExprKind.ExprIf (c, t, f) -> heads c @ heads t @ heads f
        | ExprKind.ExprTuple es | ExprKind.ExprArrayLit es -> es |> List.collect heads
        | ExprKind.ExprTyped (i, _) -> heads i
        | ExprKind.ExprField (o, _) -> heads o
        | _ -> []
    let offender =
        conjuncts |> List.collect heads |> List.distinct
        |> List.tryPick (fun f ->
            match Map.tryFind f env.StaticFunctions with
            | Some fd when fd.ReturnType.IsNone
                        || fd.Params |> List.exists (fun p -> p.Type.IsNone) -> Some f
            | _ -> None)
    match offender with
    | Some f -> Error (WherePredicateUnannotated (owner, f))
    | None -> Ok ()

and registerTypeDecl (env: TypeEnv) (typeDecl: TypeDecl) : TypeResult<TypeEnv> =
    match typeDecl with
    | TyDeclAlias (name, _typeParams, body) ->
        // Chase one level of alias indirection: if the body is `TyNamed n`
        // where n is itself a registered TDIIndexType or TDIEnumIdx alias,
        // use n's stored body for our own registration. Each registration
        // step already stores its resolved body, so chains of any depth
        // flatten without transitive walking at lookup time.
        //
        // Without this, `type B = A` (where A is an index or enum alias)
        // falls to the generic `_ -> TDIAlias` branch and B is never
        // recognized as an index/enum alias — using B in array index lists
        // or foreign-key element positions then fails with placeholder
        // records.
        let chasedBody =
            match body with
            | TyNamed (referencedName, _) ->
                match Map.tryFind referencedName env.TypeDefs with
                | Some (TDIIndexType (_, _, refBody)) -> refBody
                | Some (TDIEnumIdx (_, _, _, refBody)) -> refBody
                | _ -> body
            | _ -> body
        let defInfoResult =
            match chasedBody with
            | TyIdx _ | TySymIdx _ | TyAntisymIdx _ | TyHermitianIdx _ | TyBoundedIdx _ ->
                let idx = lowerIndexType env 0 chasedBody
                Ok (TDIIndexType (name, { idx with Tag = Some name; IxKind = ixKindOfTag (Some name) }, chasedBody))
            | TyDepIdx _ | TyRaggedIdx _ | TyRaggedIdxOpaque ->
                let idx = lowerIndexType env 0 chasedBody
                Ok (TDIIndexType (name, idx, chasedBody))
            | TyIrrepsIdx _ ->
                // Nominative-alias rule: the alias name is FOLDED INTO the
                // irreps identity tag (mkIrrepsTag (Some name) ...), so two
                // aliases of the same spec are DISTINCT types while anonymous
                // IrrepsIdx<spec> unifies with either (Unify's name-permissive
                // rule). The plain-index arm's `Tag = Some name` overwrite
                // would drop the spec payload and break Tag<->IxKind
                // agreement. A bad-spec marker keeps its error tag so the
                // consumption-site check still fires through the alias.
                let idx = lowerIndexType env 0 chasedBody
                let named =
                    match idx.Tag with
                    | Some (IrrepsTag (_, triples)) ->
                        { idx with Tag = Some (mkIrrepsTag (Some name) triples) }
                    | _ -> idx
                Ok (TDIIndexType (name, named, chasedBody))
            | TyEnumIdx valuesExpr ->
                // Static-evaluate the array literal to extract values. Each
                // element must be either an int literal (with optional negation)
                // or a string literal. Mixed kinds are a type error — the two
                // backings (int64_t vs std::string) cannot share one EnumIdx.
                let raw =
                    match valuesExpr.Kind with
                    | ExprKind.ExprArrayLit elems ->
                        elems |> List.choose (fun e ->
                            match e.Kind with
                            | ExprKind.ExprLit (LitInt n) -> Some (EVInt n)
                            | ExprKind.ExprUnaryOp (OpNeg, { Kind = ExprKind.ExprLit (LitInt n) }) -> Some (EVInt (-n))
                            | ExprKind.ExprLit (LitString s) -> Some (EVString s)
                            | _ -> None)
                    | _ -> []
                let hasInt = raw |> List.exists (function EVInt _ -> true | _ -> false)
                let hasString = raw |> List.exists (function EVString _ -> true | _ -> false)
                if hasInt && hasString then
                    Error (EnumIdxMixedKinds name)
                else
                    let extent = int64 raw.Length
                    let idx = {
                        Id = env.Builder.FreshId(); Rank = 1
                        Extent = IRLit (IRLitInt extent)
                        Symmetry = SymNone; Tag = Some name; IxKind = ixKindOfTag (Some name)
                        Kind = SDimension; Dependencies = []
                    }
                    Ok (TDIEnumIdx (name, idx, raw, chasedBody))
            | _ -> Ok (TDIAlias (lowerTypeExpr env body))
        defInfoResult |> Result.map (fun defInfo -> registerTypeDef name defInfo env)

    | TyDeclStruct (name, typeParams, fields, constraints) ->
        // Mutual member types are forbidden as field types — alias
        // transparency would silently erase the constraint.
        let memberMisuse =
            fields |> List.tryPick (fun f ->
                match mutualMemberNamesIn env f.Type with
                | [] -> None
                | n :: _ -> Some (StructFieldMutualType (name, f.Name, n)))
        match memberMisuse with
        | Some e -> Error e
        | None ->
            let fieldTypes = fields |> List.map (fun f -> (f.Name, lowerTypeExpr env f.Type))
            // Field range refinements: SEQUENTIAL scoping — a bound may
            // reference only EARLIER fields and statics, and call only
            // statically-evaluable functions (the closed forms that lower
            // into type positions).
            let boundScopeErr =
                let callables =
                    Set.union (StaticEval.knownBuiltinNames ())
                              (env.StaticFunctions |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
                let rec firstErr priorFields flds =
                    match flds with
                    | [] -> None
                    | (f: FieldDecl) :: rest ->
                        let checkRefs (e: Expr) =
                            StaticEval.collectFreeNames e
                            |> Set.filter (fun n ->
                                not (Set.contains n priorFields)
                                && not (env.StaticValues.ContainsKey n)
                                && not (callables.Contains n))
                            |> Set.toList
                            |> List.tryHead
                            |> Option.map (fun bad ->
                                StructBoundScope (name, f.Name, bad))
                        let err =
                            match f.Bound with
                            | Some b -> [b.Lo; b.Hi] |> List.choose id |> List.tryPick checkRefs
                            | None -> None
                        match err with
                        | Some e -> Some e
                        | None -> firstErr (Set.add f.Name priorFields) rest
                firstErr Set.empty fields
            match boundScopeErr with
            | Some e -> Error e
            | None ->
            // Full conjunct list (declared where + desugared field bounds)
            // via the SHARED helper — StaticEval uses the same one for
            // fold-time checks, so the two worlds cannot drift.
            let allConstraints = structConjuncts fields constraints
            // Validate all conjuncts at declaration: fields bound, each
            // conjunct must typecheck to Bool. Hard errors — a malformed
            // constraint is a compile error, not a silently dropped check.
            let mutable conjEnv = env
            for (fn, ft) in fieldTypes do
                let fId = conjEnv.Builder.FreshId()
                conjEnv <- bindVarSimple fn fId ft conjEnv
            let conjCheck =
                allConstraints |> List.fold (fun acc c ->
                    acc |> Result.bind (fun () ->
                        match inferExpr conjEnv c with
                        | Ok tC ->
                            match unify conjEnv.Subst tC.Type (IRTScalar ETBool) with
                            | Ok () -> Ok ()
                            | Error _ ->
                                Error (StructWhereNotBool (name, ppIRType tC.Type))
                        | Error e ->
                            Error (StructWhereError (name, formatTypeError e))))
                    (Ok ())
            wherePredicateAnnotationCheck env name allConstraints |> Result.bind (fun () ->
            conjCheck |> Result.map (fun () ->
                registerTypeDef name (TDIStruct (name, typeParams, fieldTypes, allConstraints)) env))

    | TyDeclSum (name, typeParams, variants) ->
        let variantTypes = variants |> List.map (fun v ->
            (v.Name, v.Data |> Option.map (lowerTypeExpr env)))
        let env' = registerTypeDef name (TDIVariant (name, typeParams, variantTypes)) env
        Ok (variants |> List.fold (fun e v ->
            registerVariantTag v.Name name (v.Data |> Option.map (lowerTypeExpr env)) e) env')

    | TyDeclMutualGroup (members, constraints) ->
        // Members register as ordinary transparent aliases — unannotated use
        // of the underlying types stays completely unconstrained. The group
        // itself is a side registration consumed at binding sites.
        let groupId = members |> List.head |> fst
        let envAfterMembers =
            members |> List.fold (fun acc (mname, mty) ->
                acc |> Result.bind (fun e ->
                    if e.MutualMembers.ContainsKey mname || e.MutualGroups.ContainsKey mname then
                        Error (MutualMemberDupGroup mname)
                    else registerTypeDecl e (TyDeclAlias (mname, [], mty)))) (Ok env)
        envAfterMembers |> Result.bind (fun env1 ->
            // Resolve each member to a struct or scalar kind.
            let memberKindsR =
                members |> List.map (fun (mname, mty) ->
                    match lowerTypeExpr env1 mty with
                    | IRTNamed s ->
                        match Map.tryFind s env1.TypeDefs with
                        | Some (TDIStruct _) -> Ok (mname, MMStruct s)
                        | _ -> Error (MutualMemberNotStruct (mname, s))
                    | (IRTScalar _ | IRTUnitAnnotated _ | IRTIdxTagged _ | IRTNat _) as sc ->
                        Ok (mname, MMScalar sc)
                    | otherTy ->
                        Error (MutualMemberBadAlias (mname, ppIRType otherTy)))
                |> sequenceResults
            memberKindsR |> Result.bind (fun memberKindList ->
                let memberKinds = Map.ofList memberKindList
                let structFields s =
                    match Map.tryFind s env1.TypeDefs with
                    | Some (TDIStruct (_, _, fields, _)) -> fields |> List.map fst |> Set.ofList
                    | _ -> Set.empty
                // Position-sensitive reference validation: member field paths,
                // bare scalar members, folded statics. Call HEADS are open —
                // guards are inlined runtime code on the backend, so any
                // Bool-typed callable is legal (the conjunct typecheck below
                // rejects unknown/ill-typed callees); args still classify.
                let rec walkConjunct (e: Expr) =
                    match e.Kind with
                    | ExprKind.ExprLit _ -> Ok ()
                    | ExprKind.ExprField ({ Kind = ExprKind.ExprVar m }, f) when memberKinds.ContainsKey m ->
                        (match memberKinds.[m] with
                         | MMStruct s when (structFields s).Contains f -> Ok ()
                         | MMStruct s -> Error (MutualUnknownField (m, f, s))
                         | MMScalar _ -> Error (MutualScalarBare (m, f)))
                    | ExprKind.ExprVar m when memberKinds.ContainsKey m ->
                        (match memberKinds.[m] with
                         | MMScalar _ -> Ok ()
                         | MMStruct _ -> Error (MutualStructNeedsField m))
                    | ExprKind.ExprVar n ->
                        if env1.StaticValues.ContainsKey n then Ok ()
                        else Error (MutualUnknownIdent n)
                    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar _ }, args) ->
                        // A call argument may pass a WHOLE member (predicates
                        // take struct-typed params: conserved(P1, P2)); other
                        // args classify as usual.
                        let walkArg (a: Expr) =
                            match a.Kind with
                            | ExprKind.ExprVar m when memberKinds.ContainsKey m -> Ok ()
                            | _ -> walkConjunct a
                        args |> List.fold (fun acc a -> acc |> Result.bind (fun () -> walkArg a)) (Ok ())
                    | ExprKind.ExprBinOp (_, _, l, r) -> walkConjunct l |> Result.bind (fun () -> walkConjunct r)
                    | ExprKind.ExprUnaryOp (_, inner) -> walkConjunct inner
                    | ExprKind.ExprIf (c, t, f) -> walkAll [c; t; f]
                    | ExprKind.ExprTuple es | ExprKind.ExprArrayLit es -> walkAll es
                    | ExprKind.ExprTyped (inner, _) -> walkConjunct inner
                    | _ -> Error MutualUnsupportedExpr
                and walkAll es =
                    match es with
                    | [] -> Ok ()
                    | e :: rest -> walkConjunct e |> Result.bind (fun () -> walkAll rest)
                let refCheck =
                    constraints |> List.fold (fun acc c -> acc |> Result.bind (fun () -> walkConjunct c)) (Ok ())
                refCheck
                |> Result.bind (fun () -> wherePredicateAnnotationCheck env1 groupId constraints)
                |> Result.bind (fun () ->
                    // Typecheck each conjunct with members bound (struct
                    // members as their nominal type, scalars as themselves)
                    // and require Bool.
                    let mutable conjEnv = env1
                    for (mname, kind) in memberKindList do
                        let mTy = match kind with MMStruct s -> IRTNamed s | MMScalar t -> t
                        let mId = conjEnv.Builder.FreshId()
                        conjEnv <- bindVarSimple mname mId mTy conjEnv
                    let typeCheckAll =
                        constraints |> List.fold (fun acc c ->
                            acc |> Result.bind (fun () ->
                                match inferExpr conjEnv c with
                                | Ok tC ->
                                    match unify conjEnv.Subst tC.Type (IRTScalar ETBool) with
                                    | Ok () -> Ok ()
                                    | Error _ ->
                                        Error (MutualConstraintNotBool (groupId, ppIRType tC.Type))
                                | Error e ->
                                    Error (MutualConstraintError (groupId, formatTypeError e))))
                            (Ok ())
                    typeCheckAll |> Result.map (fun () ->
                        let info = { GroupId = groupId; Members = memberKindList; Constraints = constraints }
                        { env1 with
                            MutualGroups = Map.add groupId info env1.MutualGroups
                            MutualMembers =
                                memberKindList |> List.fold (fun m (mname, _) ->
                                    Map.add mname groupId m) env1.MutualMembers }))))

// ============================================================================
// 11b. Zonking — Final Type Resolution
// ============================================================================
// After type checking, some IRTInfer nodes may remain in the typed AST.
// These are either (a) solved but unresolved (the substitution knows the answer
// but nobody called Resolve on that particular node), or (b) genuinely ambiguous
// (no constraints were generated). Zonking walks the entire typed AST, resolves
// every type through the substitution, and defaults any remaining unknowns to
// Float64. After zonking, no IRTInfer should remain in the program.

/// Finalize a type: resolve through substitution, default unsolved to Float64.
// 12. Module and Program
// ============================================================================

// ============================================================================
// 11c. Type expression pre-validation
// ============================================================================
// Walks every TypeExpr in a module looking for inline `EnumIdx<[...]>` with
// mixed value kinds (ints and strings in the same list). The aliased case
// `type X = EnumIdx<[1, "two"]>` is caught by registerTypeDecl's per-decl
// validation; this pre-pass covers the inline cases that the aliased check
// can't see, e.g. `let x: Array<EnumIdx<[1, "two"]> like ...> = ...`.

let private isMixedEnumIdxValues (valuesExpr: Expr) : bool =
    match valuesExpr.Kind with
    | ExprKind.ExprArrayLit elems ->
        let isInt (e: Expr) =
            match e.Kind with
            | ExprKind.ExprLit (LitInt _) | ExprKind.ExprUnaryOp (OpNeg, { Kind = ExprKind.ExprLit (LitInt _) }) -> true
            | _ -> false
        let isString (e: Expr) = match e.Kind with ExprKind.ExprLit (LitString _) -> true | _ -> false
        let hasInt = elems |> List.exists isInt
        let hasString = elems |> List.exists isString
        hasInt && hasString
    | _ -> false

let rec private walkTypeExprForMixedEnumIdx (ty: TypeExpr) : Expr list =
    let here =
        match ty with
        | TyEnumIdx v when isMixedEnumIdxValues v -> [v]
        | _ -> []
    let children =
        match ty with
        | TyArray (elem, idxs) ->
            walkTypeExprForMixedEnumIdx elem @ (idxs |> List.collect walkTypeExprForMixedEnumIdx)
        | TyAbstractArray (elem, _, _) -> walkTypeExprForMixedEnumIdx elem
        | TyFunc (args, ret) ->
            (args |> List.collect walkTypeExprForMixedEnumIdx) @ walkTypeExprForMixedEnumIdx ret
        | TyTuple ts -> ts |> List.collect walkTypeExprForMixedEnumIdx
        | TyDepIdx (outer, _, body) ->
            walkTypeExprForMixedEnumIdx outer @ walkTypeExprForMixedEnumIdx body
        | TyConstrained (inner, _) -> walkTypeExprForMixedEnumIdx inner
        | TyPoly inner -> walkTypeExprForMixedEnumIdx inner
        | TyNamed (_, args) -> args |> List.collect walkTypeExprForMixedEnumIdx
        | TyEquivIdx (_, g, r) ->
            walkTypeExprForMixedEnumIdx g @ walkTypeExprForMixedEnumIdx r
        | _ -> []
    here @ children

/// Find all mixed-value TyEnumIdx inside a single declaration. Returns the
/// list of offending valuesExpr nodes (one per occurrence). The caller
/// converts each into a TypeError with the decl's span.
let collectMixedEnumIdxInDecl (decl: Decl) : Expr list =
    let walkOpt = function Some t -> walkTypeExprForMixedEnumIdx t | None -> []
    match decl with
    | DeclLet binding | DeclStatic binding ->
        walkOpt binding.Type
    | DeclFunction f ->
        (f.Params |> List.collect (fun p -> walkOpt p.Type))
        @ walkOpt f.ReturnType
    | DeclType (TyDeclAlias (_, _, body)) ->
        // The TyDeclAlias body when itself a TyEnumIdx is caught by
        // registerTypeDecl. We still walk deeper into the body for nested
        // inline forms (e.g., `type X = Array<EnumIdx<[1, "x"]> like ...>`).
        match body with
        | TyEnumIdx _ -> []  // already handled at the alias site
        | _ -> walkTypeExprForMixedEnumIdx body
    | DeclType (TyDeclStruct (_, _, fields, _)) ->
        fields |> List.collect (fun f -> walkTypeExprForMixedEnumIdx f.Type)
    | DeclType (TyDeclSum (_, _, variants)) ->
        variants |> List.collect (fun v -> walkOpt v.Data)
    | DeclType (TyDeclMutualGroup (members, _)) ->
        members |> List.collect (fun (_, mty) -> walkTypeExprForMixedEnumIdx mty)
    | DeclImpl impl ->
        impl.Methods |> List.collect (fun m ->
            (m.Params |> List.collect (fun p -> walkOpt p.Type))
            @ walkOpt m.ReturnType)
    | DeclInterface _ | DeclImport _ | DeclUnit _ -> []

// ============================================================================
// Cross-module static value visibility (checkModule's import-seeding pre-pass)
// ============================================================================
//
// KNOWN GAP being closed here: `let static k = 5` in module M wasn't visible
// to module Main's OWN static resolution -- `let static x = M.k + 1` failed
// the `let static` fold assertion with "does not evaluate at compile time",
// even though M.k is perfectly compile-time-known. Root cause: StaticEval.
// resolveStatics (StaticEval.fs) is a pure function of a module's OWN decls
// with no seed/environment parameter, so it can't learn what a DIFFERENT
// module folded its statics to. Extending its signature (a
// `resolveStaticsWith seedValues` variant) would be the clean fix, but
// StaticEval.fs is owned by concurrent work and off-limits here. Also ruled
// out: seeding alone (TypeEnv.StaticValues) does NOT fix the fold assertion,
// because resolveStatics never reads env.StaticValues -- it's called as
// `StaticEval.resolveStatics modul.Decls`, decls only. And even if it did,
// `M.k` parses as ExprField(ExprVar "M", "k") (Parser.fs's parsePostfix), and
// StaticEval.evalExpr's ExprField arm unconditionally errors ("field access
// not supported on static values") -- so seeding wouldn't help a qualified
// reference resolve even if resolveStatics *did* consult a seed.
//
// The fix that stays inside TypeCheck.fs: literally substitute resolved
// cross-module static references with their literal values in a COPY of the
// decls handed to resolveStatics, before it ever runs. `M.k` (qualified
// import) and a bare `k` (selective import) both become e.g. `ExprLit
// (LitInt 3L)`. Only the copy fed to resolveStatics is rewritten; the
// decls used for ordinary type-checking (checkModule's main loop below) are
// untouched, so `let v = M.k` keeps going through the existing ExprField
// (ExprVar n, field) qualified-value-access special case (inferExpr, binds
// to the real imported variable) rather than being replaced by a baked-in
// literal.

/// Render a folded StaticValue back into surface `Expr` literal form, for
/// splicing into another module's decls ahead of static resolution. The
/// TypedExpr analog of this conversion (checkDecl's DeclStatic
/// "RESOLVED-VALUE SHORTCUT") runs one stage later, on already-typed trees;
/// this one runs pre-typing, directly on the surface AST.
let rec private staticValueToImportExpr sp (v: StaticEval.StaticValue) : Expr =
    match v with
    | StaticEval.SVInt i -> mkExpr sp (ExprLit (LitInt i))
    | StaticEval.SVFloat f -> mkExpr sp (ExprLit (LitFloat f))
    | StaticEval.SVBool b -> mkExpr sp (ExprLit (LitBool b))
    | StaticEval.SVString s -> mkExpr sp (ExprLit (LitString s))
    | StaticEval.SVUnit -> mkExpr sp (ExprLit LitUnit)
    | StaticEval.SVTuple vs -> mkExpr sp (ExprTuple (vs |> List.map (staticValueToImportExpr sp)))
    | StaticEval.SVStruct (n, fs) ->
        mkExpr sp (ExprStruct (n, fs |> List.map (fun (fn, v) -> (fn, staticValueToImportExpr sp v)), None))

/// Substitute references to cross-module static values (keyed in `seed` as
/// "alias.name" for a qualified import, "name" for a selective one) with
/// their literal form. Shadow-aware for local binding forms (let/match/
/// lambda) that could plausibly appear inside a `let static` RHS or a
/// static function body, so a same-named local doesn't get clobbered.
/// Structured after StaticEval.collectFreeNames's case coverage (the set of
/// expression forms StaticEval.evalExpr actually supports; substitution
/// beyond that set is moot since resolveStatics would reject the form
/// regardless).
let rec private rewriteImportedStaticRefs (seed: Map<string, StaticEval.StaticValue>) (expr: Expr) : Expr =
    if Map.isEmpty seed then expr else
    let go = rewriteImportedStaticRefs seed
    let goWithout (boundNames: string list) (e: Expr) =
        let seed' = boundNames |> List.fold (fun (s: Map<string, StaticEval.StaticValue>) n -> Map.remove n s) seed
        rewriteImportedStaticRefs seed' e
    match expr.Kind with
    | ExprKind.ExprVar name ->
        match Map.tryFind name seed with
        | Some sv -> staticValueToImportExpr expr.Span sv
        | None -> expr
    | ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, field) ->
        match Map.tryFind (sprintf "%s.%s" alias field) seed with
        | Some sv -> staticValueToImportExpr expr.Span sv
        | None -> expr
    | ExprKind.ExprField (obj, field) -> inheritSpan expr (ExprField (go obj, field))
    | ExprKind.ExprBinOp (mode, op, l, r) -> inheritSpan expr (ExprBinOp (mode, op, go l, go r))
    | ExprKind.ExprUnaryOp (op, e) -> inheritSpan expr (ExprUnaryOp (op, go e))
    | ExprKind.ExprApp (f, args) -> inheritSpan expr (ExprApp (go f, args |> List.map go))
    | ExprKind.ExprIf (c, t, e) -> inheritSpan expr (ExprIf (go c, go t, go e))
    | ExprKind.ExprTuple es -> inheritSpan expr (ExprTuple (es |> List.map go))
    | ExprKind.ExprArrayLit es -> inheritSpan expr (ExprArrayLit (es |> List.map go))
    | ExprKind.ExprLet (binding, body) ->
        let bound = StaticEval.collectPatternBindings binding.Pattern |> Set.toList
        inheritSpan expr (ExprLet ({ binding with Value = go binding.Value }, goWithout bound body))
    | ExprKind.ExprMatch (scrut, cases) ->
        inheritSpan expr (ExprMatch (go scrut, cases |> List.map (fun c ->
            let bound = StaticEval.collectPatternBindings c.Pattern |> Set.toList
            { c with Guard = c.Guard |> Option.map (goWithout bound); Body = goWithout bound c.Body })))
    | ExprKind.ExprBlock (stmts, finalExpr) ->
        inheritSpan expr (ExprBlock (stmts |> List.map (rewriteImportedStaticRefsStmt seed), finalExpr |> Option.map go))
    | ExprKind.ExprStruct (name, fields, spread) -> inheritSpan expr (ExprStruct (name, fields |> List.map (fun (n, e) -> (n, go e)), spread |> Option.map go))
    | ExprKind.ExprTyped (e, t) -> inheritSpan expr (ExprTyped (go e, t))
    | ExprKind.ExprLambda (parms, whereClause, body) ->
        let bound = parms |> List.map (fun p -> p.Name)
        inheritSpan expr (ExprLambda (parms, whereClause, goWithout bound body))
    | _ -> expr
and private rewriteImportedStaticRefsStmt (seed: Map<string, StaticEval.StaticValue>) (stmt: Stmt) : Stmt =
    match stmt with
    | StmtSpanned (inner, span) -> StmtSpanned (rewriteImportedStaticRefsStmt seed inner, span)
    | StmtLet binding -> StmtLet { binding with Value = rewriteImportedStaticRefs seed binding.Value }
    | StmtAssign (lhs, op, rhs) -> StmtAssign (rewriteImportedStaticRefs seed lhs, op, rewriteImportedStaticRefs seed rhs)
    | StmtExpr e -> StmtExpr (rewriteImportedStaticRefs seed e)
    | StmtForIn (n, range, body) ->
        StmtForIn (n, rewriteImportedStaticRefs seed range, body |> List.map (rewriteImportedStaticRefsStmt seed))

/// Rewrite only the two decl shapes StaticEval.resolveStatics actually
/// consults (its Phase 1: `DeclStatic` and `DeclFunction ... IsStatic`) --
/// everything else (including plain `let`s and the DeclImport decls
/// themselves) passes through unchanged. A static function's own parameters
/// are excluded from the substitution seed (they're local, not the
/// cross-module reference).
let private seedImportedStaticsIntoDecls (seed: Map<string, StaticEval.StaticValue>) (decls: Located<Decl> list) : Located<Decl> list =
    if Map.isEmpty seed then decls else
    decls |> List.map (fun locDecl ->
        match locDecl.Value with
        | DeclStatic binding ->
            { locDecl with Value = DeclStatic { binding with Value = rewriteImportedStaticRefs seed binding.Value } }
        | DeclFunction fd when fd.IsStatic ->
            let paramNames = fd.Params |> List.map (fun p -> p.Name)
            let seed' = paramNames |> List.fold (fun (s: Map<string, StaticEval.StaticValue>) n -> Map.remove n s) seed
            { locDecl with Value = DeclFunction { fd with Body = rewriteImportedStaticRefs seed' fd.Body } }
        | _ -> locDecl)

/// Collect the StaticValues exported by this module's imports, keyed the
/// same way references to them actually parse: "alias.name" for a
/// qualified/aliased import (`M.k` parses as ExprField(ExprVar "M", "k")),
/// "name" for a selective import (`from M import k` brings in a bare
/// ExprVar "k"). Only modules already present in env.ModuleExports
/// (checked earlier in program order) contribute -- providers and
/// not-yet-checked modules are silently skipped, same as the existing
/// DeclImport handling in checkDecl.
let private importedStaticSeed (env: TypeEnv) (decls: Located<Decl> list) : Map<string, StaticEval.StaticValue> =
    decls
    |> List.fold (fun acc locDecl ->
        match locDecl.Value with
        | DeclImport (qname, style) ->
            let fullName = String.concat "." qname
            match Map.tryFind fullName env.ModuleExports with
            | Some exports ->
                match style with
                | ImportQualified aliasOpt ->
                    let alias = aliasOpt |> Option.defaultValue (List.last qname)
                    exports.StaticValues
                    |> Map.fold (fun acc2 k v -> Map.add (sprintf "%s.%s" alias k) v acc2) acc
                | ImportSelective names ->
                    names |> List.fold (fun acc2 n ->
                        match Map.tryFind n exports.StaticValues with
                        | Some v -> Map.add n v acc2
                        | None -> acc2) acc
            | None -> acc
        | _ -> acc) Map.empty

let checkModule (env: TypeEnv) (modul: ModuleDecl) : TypedModule * TypeEnv * CompileError list =
    // Resolve compile-time-known static VALUES up front (the same
    // StaticEval.resolveStatics the lowering phase runs as its Phase 0), so
    // type-checking can consult them — e.g. a `replicate` count written as a
    // `let static`/static-function call rather than a bare literal.
    // `let static` is an assertion — fold or fail loudly — so a decl that
    // doesn't statically evaluate is a compile error here, not a silent
    // demotion to a runtime binding (lambda statics excepted: they declare
    // functions). A circular dependency, previously swallowed, also lands
    // as an error on the first static decl.
    // Cross-module static import seeding (see the comment block above this
    // function for why this can't live in StaticEval.resolveStatics
    // itself). Seed env.StaticValues directly with the imported entries
    // (dotted "alias.name" / plain "name" keys) so other StaticValues
    // consumers (evalStaticIntExpr, inferReplicate, the DeclStatic
    // resolved-value shortcut below) see them, AND splice literal
    // substitutions into a copy of this module's OWN static decls so
    // resolveStatics's fold assertion can see through a `let static x =
    // M.k + 1`-shaped reference despite resolveStatics taking no seed
    // parameter.
    let crossModuleStaticSeed = importedStaticSeed env modul.Decls
    let env = { env with StaticValues = Map.fold (fun acc k v -> Map.add k v acc) env.StaticValues crossModuleStaticSeed }
    let declsForStaticResolution = seedImportedStaticsIntoDecls crossModuleStaticSeed modul.Decls
    let env, staticAssertErrors =
        match StaticEval.resolveStatics declsForStaticResolution with
        | Ok (se, failures) ->
            let env' = { env with StaticValues = Map.fold (fun acc k v -> Map.add k v acc) env.StaticValues se.Values }
            let errs =
                failures |> List.map (fun (f: StaticEval.StaticFailure) ->
                    let msg =
                        sprintf "`let static %s` does not evaluate at compile time: %s. `let static` asserts a compile-time value — use plain `let` for values computed at runtime."
                            (f.Names |> String.concat ", ") f.Reason
                    locateError f.Span env' (Other msg))
            env', errs
        | Error msg ->
            let span =
                modul.Decls
                |> List.tryPick (fun d -> match d.Value with DeclStatic _ -> Some d.Span | _ -> None)
                |> Option.defaultValue noSpan
            env, [locateError span env (Other msg)]
    // Pre-pass: register static functions and static values with placeholder types
    // so forward references and mutual recursion resolve correctly.
    let preEnv =
        modul.Decls |> List.fold (fun (e: TypeEnv) locDecl ->
            match locDecl.Value with
            | DeclFunction funcDecl when funcDecl.IsStatic ->
                let paramTypes = funcDecl.Params |> List.map (fun p ->
                    match p.Type with Some t -> lowerTypeExpr e t | None -> e.Subst.Fresh())
                let retType = match funcDecl.ReturnType with
                              | Some t -> lowerTypeExpr e t
                              | None -> e.Subst.Fresh()
                let funcType = mkFuncArrow paramTypes retType
                let funcVarId = e.Builder.FreshId()
                let e' = bindVarSimple funcDecl.Name funcVarId funcType e
                // Stash the AST so lowerIndexTypeList can inline the body when
                // this function appears in an eta-reduced DepIdx position.
                { e' with StaticFunctions = Map.add funcDecl.Name funcDecl e'.StaticFunctions }
            | DeclStatic binding ->
                let name = match binding.Pattern.Kind with PatternKind.PatVar n -> n | _ -> "_"
                let varId = e.Builder.FreshId()
                bindVarSimple name varId (e.Subst.Fresh()) e
            | _ -> e) env
    
    let mutable currentEnv = preEnv
    let mutable decls = []
    let mutable errors = []
    
    for d in modul.Decls do
        // Pre-validation: inline TyEnumIdx<[mixed values]> occurrences. The
        // alias-site check in registerTypeDecl catches `type X = EnumIdx<[...]>`
        // declarations but not inline embeddings like `let x: Array<EnumIdx<[1,
        // "two"]> like ...> = ...`. Each finding becomes an error attached to
        // the decl's span.
        let mixedFindings = collectMixedEnumIdxInDecl d.Value
        for _ in mixedFindings do
            let err = Other "Inline EnumIdx<[...]> has mixed value kinds (integer and string literals in the same list). The runtime backing must be one or the other (int64_t or std::string)."
            let ce = locateError d.Span currentEnv err
            errors <- ce :: errors

        let declName =
            match d.Value with
            | DeclLet b -> sprintf "in let binding '%s'" (match b.Pattern.Kind with PatternKind.PatVar n -> n | _ -> "_")
            | DeclStatic b -> sprintf "in static binding '%s'" (match b.Pattern.Kind with PatternKind.PatVar n -> n | _ -> "_")
            | DeclFunction f -> sprintf "in function '%s'" f.Name
            | DeclType td ->
                match td with
                | TyDeclAlias (n, _, _) | TyDeclStruct (n, _, _, _) | TyDeclSum (n, _, _) -> sprintf "in type '%s'" n
                | TyDeclMutualGroup (members, _) ->
                    sprintf "in mutual group '%s'" (members |> List.map fst |> String.concat ", ")
            | DeclInterface i -> sprintf "in interface '%s'" i.Name
            | DeclImpl impl -> sprintf "in impl for '%A'" impl.ForType
            | DeclImport (qn, _) -> sprintf "in import '%s'" (String.concat "." qn)
            | DeclUnit u -> sprintf "in unit '%s'" u.Name
        let envWithCtx = pushContext declName currentEnv
        match checkDecl envWithCtx d.Value with
        | Ok (td, env') ->
            decls <- td :: decls
            // Carry forward env' but restore original context (don't nest)
            currentEnv <- { env' with Context = currentEnv.Context }
        | Error err ->
            let ce = locateError d.Span currentEnv err
            errors <- ce :: errors
            // Continue with pre-failure env, but bind the failed decl's
            // name(s) to a FRESH inference var so downstream references
            // resolve to *some* type instead of erroring `Unbound variable`.
            // Without this, one bad annotation smears ~N spurious
            // Unbound-variable diagnostics across the rest of the module and
            // buries the real root cause. A fresh (unsolved) var unifies with
            // anything, so it silences the cascade without manufacturing a
            // second layer of false errors the way binding the *annotation*
            // type would. (Value bindings only — type/interface/impl/import/
            // unit decls don't produce value-scope names that cascade here.)
            let recoveryNames =
                match d.Value with
                | DeclLet b | DeclStatic b -> patternNames b.Pattern
                | DeclFunction f -> [f.Name]
                | _ -> []
            for n in recoveryNames do
                let varId = currentEnv.Builder.FreshId()
                currentEnv <- bindVarSimple n varId (currentEnv.Subst.Fresh()) currentEnv

    let typedModule = { Name = Some modul.Name; Decls = List.rev decls }
    // Zonk: resolve all IRTInfer through the substitution, default unsolved to Float64
    let zonked = zonkModule currentEnv.Subst typedModule
    (zonked, currentEnv, staticAssertErrors @ List.rev errors)

let checkProgram (program: Program) : TypedProgram * IRBuilder * CompileError list * string list =
    let env = emptyEnv ()
    let mutable modules = []
    let mutable allErrors = []
    let mutable moduleExports = Map.empty<string, TypeModuleExport>
    for modul in program.Modules do
        let envWithExports = { env with ModuleExports = moduleExports }
        let (tm, finalEnv, errs) = checkModule envWithExports modul
        modules <- tm :: modules
        allErrors <- allErrors @ errs
        // Build export from this module's checked environment
        let moduleName = modul.Name |> String.concat "."
        let export : TypeModuleExport = {
            Variables = finalEnv.Variables |> Map.filter (fun k _ -> not (k.Contains(".")))
            TypeDefs = finalEnv.TypeDefs |> Map.filter (fun k _ -> not (k.Contains(".")))
            VariantTags = finalEnv.VariantTags
            Units = finalEnv.Units
            StaticFunctions = finalEnv.StaticFunctions |> Map.filter (fun k _ -> not (k.Contains(".")))
            StaticValues = finalEnv.StaticValues |> Map.filter (fun k _ -> not (k.Contains(".")))
        }
        moduleExports <- Map.add moduleName export moduleExports
    // env.Warnings is shared by reference across all envWithExports updates
    // (mutable ResizeArray, not a Map), so all module-scope warnings
    // accumulate here.
    let warnings = env.Warnings |> Seq.toList
    ({ Modules = List.rev modules }, env.Builder, allErrors, warnings)

// ============================================================================
// 13. Public Entry Point
// ============================================================================

/// Type check a program. Returns the typed program, builder, and any
/// non-fatal warnings in the Ok case; or compile errors in the Error case.
/// (Warnings emitted before a hard error is encountered are currently
/// dropped on the Error path; that's a separate refinement.)
///
/// Pre-pass: IndexTypeValidator enforces the rules for where index types may
/// appear in declaration-level type expressions. Validation errors abort
/// compilation early — once an AST passes validation, downstream lowering
/// can assume index types only appear in their permitted positions.
let typeCheck (program: Program) : Result<TypedProgram * IRBuilder * string list, CompileError list> =
    // AST -> AST expansions, in order: ML-op elaboration first (so grad()
    // sees the generated functions as plain Blade source and can inline
    // them), then grad() expansion. Both synthesize ordinary declarations
    // that flow through validation, checking, lowering and codegen exactly
    // like user code.
    // Provider-backed statics: install the compile-time data reader before
    // ANY resolveStatics pass runs (the ML and PPL elaborations each run
    // their own; all inherit the fold through StaticEval's hook).
    Blade.ProviderStatics.install ()
    // Staged-former unfold FIRST: `static method_for/object_for/for`
    // argument lists elaborate to plain formers before any other stage
    // (ML/PPL/math/grad and the checker never see ExprStatic).
    match Blade.Unfold.expand program with
    | Error diags -> Error (diags |> List.map (compileErrorOfDiagnostic ["static unfold"]))
    | Ok program ->
    match Blade.ML.Elaborate.expand program with
    | Error diags -> Error (diags |> List.map (compileErrorOfDiagnostic ["ML elaboration"]))
    | Ok program ->
    // sgs runs AFTER ML so the (future) ml.galilean judgment sees surface
    // `sgs.*` op calls at ML's seam, and before PPL/Math/Grad so its
    // generated plain source flows through them untouched.
    match Blade.Sgs.Elaborate.expand program with
    | Error diags -> Error (diags |> List.map (compileErrorOfDiagnostic ["sgs elaboration"]))
    | Ok program ->
    match Blade.Ppl.Elaborate.expand program with
    | Error diags -> Error (diags |> List.map (compileErrorOfDiagnostic ["PPL elaboration"]))
    | Ok program ->
    match Blade.Math.Elaborate.expand program with
    | Error diags -> Error (diags |> List.map (compileErrorOfDiagnostic ["math elaboration"]))
    | Ok program ->
    match Blade.Rand.Elaborate.expand program with
    | Error diags -> Error (diags |> List.map (compileErrorOfDiagnostic ["rand elaboration"]))
    | Ok program ->
    match Blade.Spectra.Elaborate.expand program with
    | Error diags -> Error (diags |> List.map (compileErrorOfDiagnostic ["spectra elaboration"]))
    | Ok program ->
    match Blade.Grad.expand program with
    | Error diags -> Error (diags |> List.map (compileErrorOfDiagnostic ["grad expansion"]))
    | Ok program ->
    let validationErrors = IndexTypeValidator.validateProgram program
    if not validationErrors.IsEmpty then
        let compileErrors =
            validationErrors |> List.map (fun e ->
                { Error = Other e.Message; Span = e.Span; Context = [e.DeclName]; Code = Some "BL4003" })
        Error compileErrors
    else
        let (tp, builder, errors, warnings) = checkProgram program
        if errors.IsEmpty then Ok (tp, builder, warnings)
        else Error errors
