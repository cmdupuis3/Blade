// Blade-DSL C++ Code Generation
// Transforms IR structures into C++ source code
// Generates complete, compilable C++ programs

module Blade.CodeGen

open Blade.IR

// ============================================================================
// Code Generation Context
// ============================================================================

/// Tracks information needed during code generation
type CodeGenContext = {
    /// Map from IR variable IDs to C++ variable names
    VarNames: Map<IRId, string>
    /// Current indentation level
    Indent: int
    /// Generated static declarations (symmetry vectors, extents)
    StaticDecls: string list
    /// Counter for generating unique names
    mutable NextTempId: int
    /// Set of struct type names that have where constraints (need validate() calls)
    ConstrainedStructs: Set<string>
    /// Map from C++ variable name to its tuple children names (for extents provenance)
    /// e.g., "_0" → ["_0_0", "_0_1"] means _0 is a pair whose components are _0_0 and _0_1
    TupleChildren: Map<string, string list>
    /// Map from IRId to deferred computation expressions (not yet materialized).
    /// Computations (L <@> f) are only materialized when |> compute forces them.
    /// Combinators like <&!> look through variables to find the original ApplyInfo for fusion.
    DeferredComputations: Map<IRId, IRExpr>
    /// Map from grouped-array C++ name to its source GroupKeys C++ name.
    /// Populated by genBinding for IRGroupBy; consulted by method_for codegen
    /// when peeling a ragged outer dimension (Tag = "__group_outer").
    GroupedArrays: Map<string, string>
    /// Accumulated code generation warnings (unsupported IR nodes, fallbacks, etc.)
    Warnings: string list ref
}

/// Module-level expression warnings collector.
/// exprToCpp is pure (returns string) so it can't use CodeGenContext directly.
/// This collects warnings from exprToCpp calls and is synced into CodeGenContext.
let exprWarnings : string list ref = ref []

/// Record an expression-level warning and return a C++ expression that causes a compile error.
let exprError (msg: string) : string =
    exprWarnings.Value <- exprWarnings.Value @ [msg]
    sprintf "BLADE_CODEGEN_ERROR_%s" (msg.Replace(" ", "_").Replace("'", "").Replace("(", "").Replace(")", "").Replace(",", "").Replace(":", "").Replace("\"", "").ToUpper())

/// Check if an expression is unit/void-typed (should not generate a value)
let isUnitExpr (expr: IRExpr) : bool =
    match expr with
    | IRLit IRLitUnit -> true
    | IRAssign _ -> true
    | IRForRange _ -> true
    | _ -> false

let emptyContext () = {
    VarNames = Map.empty
    Indent = 0
    StaticDecls = []
    NextTempId = 0
    ConstrainedStructs = Set.empty
    TupleChildren = Map.empty
    DeferredComputations = Map.empty
    GroupedArrays = Map.empty
    Warnings = ref []
}

let indent ctx = { ctx with Indent = ctx.Indent + 1 }
let indentStr ctx = String.replicate ctx.Indent "    "

/// Record a codegen warning and return a C++ #error directive.
/// This ensures the generated C++ will not compile silently.
let codegenError (ctx: CodeGenContext) (ind: string) (msg: string) : string list =
    ctx.Warnings.Value <- ctx.Warnings.Value @ [msg]
    [sprintf "%s#error \"Blade codegen: %s\"" ind (msg.Replace("\"", "'"))]

/// C++ reserved words and built-in type names that cannot be used as identifiers
let cppReservedWords = Set.ofList [
    // Types that conflict with Blade names
    "double"; "float"; "int"; "long"; "short"; "char"; "bool"; "void"; "auto"
    "signed"; "unsigned"; "const"; "volatile"; "static"; "extern"; "register"
    // Keywords
    "class"; "struct"; "enum"; "union"; "namespace"; "template"; "typename"
    "virtual"; "override"; "final"; "public"; "private"; "protected"
    "new"; "delete"; "this"; "return"; "if"; "else"; "for"; "while"; "do"
    "switch"; "case"; "break"; "continue"; "goto"; "default"; "try"; "catch"; "throw"
    "sizeof"; "alignof"; "decltype"; "typedef"; "using"; "operator"
    "true"; "false"; "nullptr"; "inline"; "constexpr"; "mutable"
]

/// Sanitize a name to avoid C++ reserved word conflicts
let sanitizeCppName (name: string) : string =
    if Set.contains name cppReservedWords then name + "_"
    else name

let addVarName id name ctx = 
    { ctx with VarNames = Map.add id (sanitizeCppName name) ctx.VarNames }


// ============================================================================
// Type Inference Helper (for code generation)
// ============================================================================

/// Collect all variable IDs referenced in an expression (for computing captures)
let rec collectVarRefs (expr: IRExpr) : Set<IRId> =
    match expr with
    | IRVar (id, _) -> Set.singleton id
    | IRLit _ -> Set.empty
    | IRParam _ -> Set.empty
    | IRBinOp (_, _, left, right) -> Set.union (collectVarRefs left) (collectVarRefs right)
    | IRUnaryOp (_, operand) -> collectVarRefs operand
    | IRIf (cond, thenBr, elseBr) -> 
        Set.unionMany [collectVarRefs cond; collectVarRefs thenBr; collectVarRefs elseBr]
    | IRLet (_, value, body) -> Set.union (collectVarRefs value) (collectVarRefs body)
    | IRLambda info -> 
        let paramIds = info.Params |> List.map (fun p -> p.VarId) |> Set.ofList
        Set.difference (collectVarRefs info.Body) paramIds
    | IRApp (func, args, _) -> 
        Set.unionMany (collectVarRefs func :: List.map collectVarRefs args)
    | IRTuple exprs -> Set.unionMany (List.map collectVarRefs exprs)
    | IRComplex (re, im) -> Set.union (collectVarRefs re) (collectVarRefs im)
    | IRTupleProj (e, _, _) -> collectVarRefs e
    | IRArrayLit (exprs, _) -> Set.unionMany (List.map collectVarRefs exprs)
    | IRIndex (arr, indices, _) -> 
        Set.unionMany (collectVarRefs arr :: List.map collectVarRefs indices)
    | IRFieldAccess (obj, _) -> collectVarRefs obj
    | IRStructLit (_, fields) -> Set.unionMany (fields |> List.map (snd >> collectVarRefs))
    | IRCompute inner -> collectVarRefs inner
    | IRMethodFor info -> Set.unionMany (List.map collectVarRefs info.Arrays)
    | IRObjectFor info -> 
        // InputRanks is int list, not IRExpr list - just collect from kernel
        collectVarRefs info.Kernel
    | IRApplyCombinator info -> 
        Set.unionMany [collectVarRefs info.Loop; collectVarRefs info.Kernel; Set.unionMany (List.map collectVarRefs info.Arrays)]
    | IRArity _ -> Set.empty
    | IRReynolds (inner, _) -> collectVarRefs inner
    | IRMatch (scrutinee, cases) ->
        let scrutineeRefs = collectVarRefs scrutinee
        let caseRefs = cases |> List.collect (fun c ->
            [collectVarRefs c.Body]
            @ (c.Guard |> Option.map collectVarRefs |> Option.toList)) |> Set.unionMany
        Set.union scrutineeRefs caseRefs
    | IRAssign (target, value) ->
        Set.union (collectVarRefs target) (collectVarRefs value)
    | IRForRange (vid, lo, hi, body) ->
        Set.unionMany [collectVarRefs lo; collectVarRefs hi; Set.remove vid (collectVarRefs body)]
    | IRGuard (cond, body) ->
        Set.union (collectVarRefs cond) (collectVarRefs body)
    | IRMask (arr, pred) ->
        Set.union (collectVarRefs arr) (collectVarRefs pred)
    | IRIntersect (a, b) | IRUnion (a, b) ->
        Set.union (collectVarRefs a) (collectVarRefs b)
    | IRGroupBy (v, k) ->
        Set.union (collectVarRefs v) (collectVarRefs k)
    | IRGroupKeys k -> collectVarRefs k
    | IRSort (arr, key) ->
        Set.union (collectVarRefs arr) (collectVarRefs key)
    | IRReduce (arr, kernel) ->
        Set.union (collectVarRefs arr) (collectVarRefs kernel)
    | IRExtent (arr, _) -> collectVarRefs arr
    | IRRaggedLookup l -> collectVarRefs l
    | IRSequence exprs ->
        Set.unionMany (List.map collectVarRefs exprs)
    | _ -> Set.empty

/// Struct fields cache used by `inferExprType` for `IRFieldAccess` resolution.
/// Populated at `genModule` entry; without it, field access types fall
/// through to IRTUnit and lifted bindings render as side-effect statements.
///
/// Thread-safety: the test runner uses `Array.Parallel.mapi` to compile
/// tests in parallel. A plain module-level mutable Dictionary races
/// between tasks (one test's `setCodegenStructFieldsCache` wipes another
/// concurrent test's state, producing IRTUnit lookups and broken codegen
/// for the affected test). Wrapping in `AsyncLocal<T>` and assigning a
/// fresh Dictionary per set call gives each task its own instance.
let private codegenStructFieldsCacheStorage =
    System.Threading.AsyncLocal<System.Collections.Generic.Dictionary<string, (string * IRType) list>>()

let private getCodegenStructFieldsCache () : System.Collections.Generic.Dictionary<string, (string * IRType) list> =
    let v = codegenStructFieldsCacheStorage.Value
    if isNull v then
        let fresh = System.Collections.Generic.Dictionary<string, (string * IRType) list>()
        codegenStructFieldsCacheStorage.Value <- fresh
        fresh
    else v

let setCodegenStructFieldsCache (types: IRTypeDef list) =
    // Create a fresh Dictionary per call — see note on
    // `structFieldsCacheStorage` in IR.fs for why we can't .Clear() a
    // shared instance under parallel execution.
    let cache = System.Collections.Generic.Dictionary<string, (string * IRType) list>()
    for td in types do
        match td with
        | IRTDStruct (name, fields, _) -> cache.[name] <- fields
        | _ -> ()
    codegenStructFieldsCacheStorage.Value <- cache

/// Infer type from an IR expression (simplified version for codegen)
let rec inferExprType (expr: IRExpr) : IRType =
    match expr with
    | IRArrayLit (_, arrTy) -> mkArrayLike arrTy
    | IRLit (IRLitInt _) -> IRTScalar ETInt64
    | IRLit (IRLitFloat _) -> IRTScalar ETFloat64
    | IRLit (IRLitBool _) -> IRTScalar ETBool
    | IRLit (IRLitString _) -> IRTScalar ETString
    | IRLit IRLitUnit -> IRTUnit
    | IRBinOp (_, op, left, right) ->
        match op with
        | IREq | IRNeq | IRLt | IRLe | IRGt | IRGe | IRAnd | IROr -> IRTScalar ETBool
        | _ ->
            match inferExprType left, inferExprType right with
            | IRTScalar e1, IRTScalar e2 ->
                IRTScalar (IR.promoteElemType e1 e2 |> Option.defaultValue e1)
            | lt, _ -> lt
    | IRUnaryOp (op, operand) ->
        match op with
        | IRNot -> IRTScalar ETBool
        | IRNeg -> inferExprType operand
    | IRIf (_, thenBr, _) -> inferExprType thenBr
    | IRCompute inner -> inferExprType inner
    | IRApplyCombinator info -> info.OutputType
    | IRLambda info -> 
        let argTypes = info.Params |> List.map (fun p -> p.Type)
        let retType = inferExprType info.Body
        mkFuncArrow argTypes retType
    | IRTuple exprs -> IRTTuple (exprs |> List.map inferExprType)
    | IRComplex (re, _) ->
        // Complex type derived from component width: Float32 → Complex64,
        // Float64 → Complex128. Reports as a scalar (NOT a tuple) — that's
        // the whole point of having a separate IRComplex node.
        match inferExprType re with
        | IRTScalar ETFloat32 -> IRTScalar ETComplex64
        | _ -> IRTScalar ETComplex128
    | IRTupleProj (e, i, isFlat) ->
        let parentTy = inferExprType e
        if isFlat then
            let leaves = IR.flattenTupleLeaves parentTy
            if i < leaves.Length then leaves.[i] else IRTUnit
        else
            match parentTy with
            | IRTTuple ts when i < ts.Length -> ts.[i]
            | _ -> IRTUnit
    | IRStructLit (typeName, _) -> IRTNamed typeName
    | IRApp (_, _, retType) -> retType
    | IRVar (_, ty) -> ty
    | IRParam (_, _, ty) -> ty
    | IRLet (_, _, body) -> inferExprType body
    | IRMatch (_, cases) ->
        match cases with
        | c :: _ -> inferExprType c.Body
        | [] -> IRTUnit
    | IRIndex (arr, indices, _) ->
        // Indexing peels dimensions; full indexing yields the element type.
        // Phase B2: arrTy.ElemType is IRType; return directly.
        match inferExprType arr with
        | ArrayElem arrTy when indices.Length >= arrTy.IndexTypes.Length -> arrTy.ElemType
        | ArrayElem arrTy -> mkArrayLike { arrTy with IndexTypes = arrTy.IndexTypes |> List.skip indices.Length }
        | t -> t
    | IRSequence exprs ->
        match exprs with
        | [] -> IRTUnit
        | _ ->
            // Sequence produces array with Idx<N> over element type
            let elemType = inferExprType (List.head exprs)
            match elemType with
            | ArrayElem arr ->
                // Array elements: prepend sequence dimension
                let seqIdx = { Id = 0; Arity = 1; Extent = IRLit (IRLitInt (int64 exprs.Length)); Symmetry = SymNone; Tag = Some "__seq"; Kind = SDimension; Dependencies = [] }
                mkArrayLike { arr with IndexTypes = seqIdx :: arr.IndexTypes }
            | IRTScalar et ->
                // Scalar elements: simple array
                let seqIdx = { Id = 0; Arity = 1; Extent = IRLit (IRLitInt (int64 exprs.Length)); Symmetry = SymNone; Tag = Some "__seq"; Kind = SDimension; Dependencies = [] }
                mkArrayArrow [seqIdx] (IRTScalar et) None
            | _ -> elemType
    | IRAssign _ -> IRTUnit
    | IRForRange _ -> IRTUnit
    | IRArity _ -> IRTScalar ETInt64
    | IRNth -> IRTScalar ETInt64
    | IRRank _ -> IRTScalar ETInt64
    | IRExtent _ -> IRTScalar ETInt64
    | IRRaggedLookup _ -> IRTScalar ETInt64
    | IROpaqueExtent -> IRTScalar ETInt64
    | IRRange _ -> IRTScalar ETInt64
    | IRFieldAccess (obj, field) ->
        // Phase D: resolve via codegenStructFieldsCache (async-local;
        // populated at genModule entry).
        //
        // Fallback to IR.fs's liftInferType: defensive backup in case
        // codegen's cache lookup misses an entry that IR.fs's cache has
        // (or vice versa). The historical motivation was the parallel
        // test-runner race condition on these caches (see Struct Array
        // With Array Field regression history) — fixed by making both
        // caches `AsyncLocal<T>`. The fallback remains as belt-and-suspenders
        // for the case where either cache is, for any future reason,
        // unpopulated when an IRFieldAccess is processed.
        let primary =
            match inferExprType obj with
            | IRTNamed structName ->
                let cache = getCodegenStructFieldsCache ()
                match cache.TryGetValue(structName) with
                | true, fields ->
                    match fields |> List.tryFind (fun (n, _) -> n = field) |> Option.map snd with
                    | Some ty -> Some ty
                    | None -> None
                | false, _ -> None
            | _ -> None
        match primary with
        | Some ty -> ty
        | None -> liftInferType expr
    | IRFunctorMap (f, c) ->
        // f <$> c: return type is f's return type
        match inferExprType f with
        | FuncElem (_, retTy) -> retTy
        | _ -> inferExprType c  // fallback: preserve computation type
    | IRBind (_, cont) ->
        // >>= : result type is continuation's return type
        match inferExprType cont with
        | FuncElem (_, retTy) -> retTy
        | t -> t
    | IRComposeMeth (_, right) ->
        // @>> : result type is right's type
        inferExprType right
    | IRPure e -> inferExprType e
    | IRParallel (l, r, _) -> IRTTuple [inferExprType l; inferExprType r]
    | IRFusion (l, r) -> IRTTuple [inferExprType l; inferExprType r]
    | IRChoice (l, _) -> inferExprType l
    | IRGuard (_, body) -> inferExprType body
    | IRMask (arr, _) -> inferExprType arr  // Same elem type, different extent
    | IRIntersect (a, _) | IRUnion (a, _) -> inferExprType a  // Same elem type, different extent
    | IRGroupBy (v, _) -> inferExprType v  // Placeholder: actual type is rank-2 ragged
    | IRGroupKeys _ -> IRTUnit  // GroupKeys is an opaque structure, not a runtime value with a simple type
    | IRSort (arr, _) -> inferExprType arr  // Same shape as input — sort preserves length and elem type
    | IRReduce (arr, _) ->
        // Reduces innermost dim by 1. For rank-1 input, result is a scalar.
        match inferExprType arr with
        | ArrayElem a when a.IndexTypes.Length = 1 -> a.ElemType  // IRType already
        | ArrayElem a ->
            // Multi-rank reduction: drop innermost index. (Not yet supported by
            // codegen; TypeCheck rejects rank>1 today, but keep this consistent.)
            mkArrayLike { a with IndexTypes = a.IndexTypes |> List.take (a.IndexTypes.Length - 1) }
        | t -> t
    | _ -> IRTUnit  // Remaining cases: loop objects, combinators — not runtime values

// ============================================================================
// C++ Type Mapping
// ============================================================================

/// Convert a primitive ElemType enum value to C++ type string.
/// Use this only when you have a raw `ElemType` value (e.g., from
/// promoteElemType). For array element types post-B2, use `elemTypeToCpp`
/// which takes the full IRType.
let primTypeToCpp = function
    | ETInt32 -> "int32_t"
    | ETInt64 -> "int64_t"
    | ETFloat32 -> "float"
    | ETFloat64 -> "double"
    | ETComplex64 -> "std::complex<float>"
    | ETComplex128 -> "std::complex<double>"
    | ETBool -> "bool"
    | ETUnit -> "void"
    | ETString -> "std::string"

/// Get rank (total dimensions) from array type
let arrayRank (arr: IRArrayType) = 
    arr.IndexTypes |> List.sumBy (fun i -> i.Arity)

/// Convert an IRType in array-element position to a C++ type string.
/// Phase B2: dispatches via active patterns.
///   - PrimElem / AnyPrimElem: render the primitive (with units erased).
///   - NamedElem: render the struct/sum's name. Codegen for nominal-typed
///     element arrays is still future work (the `promote<T, k>` template
///     handles them at the C++ level, but Blade-side support for
///     constructing/operating on them is incomplete).
///   - InferElem: codegen-time error. Should never reach here if typecheck
///     and zonking did their job.
///   - InvalidElem: hard error — these types have no value-level meaning.
///   - PolyElem: not implemented; specialization should have replaced it.
///   - Other (FuncElem, ArrayElem, TupleElem): delegate to irTypeToCpp,
///     which already renders these correctly. Codegen for arrays-of-these
///     is future work but the type system stops blocking them.
let rec elemTypeToCpp (ty: IRType) : string =
    match ty with
    // Tagged element types must route through irTypeToCpp BEFORE the
    // AnyPrimElem catch, because AnyPrimElem extracts the inner primitive
    // (e.g., int64) which loses the typedef alias name. irTypeToCpp's
    // IRTIdxTagged arm renders IRefNamed as the alias unconditionally.
    | IRTIdxTagged _ -> irTypeToCpp ty
    | AnyPrimElem et -> primTypeToCpp et
    | NamedElem "String" -> "std::string"
    | NamedElem name -> name
    | InferElem id ->
        exprWarnings.Value <- exprWarnings.Value @
            [sprintf "elemTypeToCpp: unresolved type variable T?%d in element position" id]
        sprintf "BLADE_UNRESOLVED_ELEM_TYPE_%d" id
    | PolyElem (_, var) ->
        exprWarnings.Value <- exprWarnings.Value @
            [sprintf "elemTypeToCpp: PolyElem<%s> in element position is not yet implemented" var]
        "BLADE_NOT_IMPLEMENTED_POLY_ELEM"
    | InvalidElem ->
        exprWarnings.Value <- exprWarnings.Value @
            [sprintf "elemTypeToCpp: invalid type in element position: %A" ty]
        "BLADE_INVALID_ELEM_TYPE"
    | _ ->
        // FuncElem / ArrayElem / TupleElem / other: delegate to irTypeToCpp.
        irTypeToCpp ty

/// Convert IR type to C++ type string. Phase B2: mutually recursive with
/// `elemTypeToCpp` because the array element type is now an arbitrary IRType.
and irTypeToCpp = function
    | IRTScalar et -> primTypeToCpp et
    | IRTTuple ts -> sprintf "std::tuple<%s>" (ts |> List.map irTypeToCpp |> String.concat ", ")
    | IRTUnit -> "void"
    | IRTLoop lt ->
        match lt.Kind with
        | LKMethod -> "BLADE_ERROR_METHOD_LOOP_TYPE"
        | LKObject -> "BLADE_ERROR_OBJECT_LOOP_TYPE"
    | IRTComputation t -> irTypeToCpp t  // Computation<T> erases to T at runtime
    | IRTPoly (base', _) -> 
        // After monomorphization, IRTPoly should not reach codegen.
        // If it does, fall back to the base type.
        irTypeToCpp base'
    | IRTNat _ -> "size_t"
    | IRTIdxTagged (inner, idxRef) ->
        // Parallel to IRTUnitAnnotated: tag is a typecheck-time invariant,
        // erased at codegen. For IRefNamed, render the typedef alias
        // unconditionally — a `using <name> = ...;` is emitted alongside
        // the type declaration, so the alias is in scope. For IRefAnon
        // there's no alias to use; render the inner type directly.
        match idxRef with
        | IRefNamed name -> name
        | IRefAnon _ -> irTypeToCpp inner
    | IRTNamed "String" -> "std::string"  // Blade String → C++ std::string
    | IRTNamed name -> name  // Named types (structs, etc.) use their name directly
    | IRTInfer n ->
        exprWarnings.Value <- exprWarnings.Value @ [sprintf "unresolved type variable _%d reached codegen" n]
        sprintf "BLADE_UNRESOLVED_TYPE_%d" n
    | IRTUnitAnnotated (inner, _) -> irTypeToCpp inner  // Units erase at codegen
    | IRTGroupKeys _ -> "void*"  // GroupKeys is an opaque runtime structure
    | IRTArrow (slots, result, identity) ->
        // Three shapes possible:
        //   - all-SVal (including empty slots, which means nullary function):
        //     renders as std::function<RetType(ArgType1, ArgType2, ...)>.
        //     Inside the std::function<> template, parameter and result
        //     types that are themselves arrays must render as the WRAPPER
        //     form (`Array<T, N>`), because that's the form the underlying
        //     function declarations use (per genFuncDef's ArrayElem branch).
        //     Helper `arrowSlotTypeForFuncSig` enforces this by using
        //     the wrapper form for array-typed slots and recursing through
        //     `irTypeToCpp` for everything else.
        //   - all-SIdx or all-SIdxVirt with non-empty slots: array-shaped
        //     arrow, renders as `promote<elem, rank>::type` — the raw
        //     nested-pointer form. This is what consumers of array
        //     bindings expect: indexing into Array<T,N> or Ragged<T>
        //     (via their operator[]) returns the raw pointer, not the
        //     wrapper. Bindings of the form `let row = arr(i)` therefore
        //     get a `promote<T, N-1>::type` declaration that accepts the
        //     `operator[]` return value directly.
        //     The wrapper form `Array<T, N>` is used at allocation sites
        //     (genArrayLiteral, etc.) where it's spelled out explicitly,
        //     and in function signatures (genFuncDef) where the wrapper
        //     IS the calling convention.
        //   - mixed slot kinds: not yet expressible by language surface; sentinel.
        let isAllSVal = slots |> List.forall (function SVal _ -> true | _ -> false)
        let isAllStored = slots |> List.forall (function SIdx _ -> true | _ -> false)
        let isAllVirtual = slots |> List.forall (function SIdxVirt _ -> true | _ -> false)
        if isAllSVal then
            let paramTypes =
                slots |> List.map (function
                    | SVal t -> arrowSlotTypeForFuncSig t
                    | _ -> failwith "unreachable — guarded by isAllSVal")
            let paramList = String.concat ", " paramTypes
            sprintf "std::function<%s(%s)>" (arrowSlotTypeForFuncSig result) paramList
        elif (isAllStored || isAllVirtual) && not slots.IsEmpty then
            // Reconstruct an IRArrayType view for rendering
            let indexTypes =
                slots |> List.map (function
                    | SIdx i | SIdxVirt i -> i
                    | _ -> failwith "unreachable")
            let arr = {
                ElemType = result
                IndexTypes = indexTypes
                IsVirtual = isAllVirtual
                Identity = identity
            }
            sprintf "promote<%s, %d>::type" (elemTypeToCpp arr.ElemType) (arrayRank arr)
        else
            exprWarnings.Value <- exprWarnings.Value @ ["IRTArrow with mixed slot kinds reached codegen (no language construct produces these yet)"]
            "BLADE_UNSUPPORTED_ARROW_TYPE"

/// Render a type for use inside a std::function<...> signature (i.e. as a
/// parameter or return type of the function). Array types render as the
/// wrapper form (`Array<T, N>`) to match what function declarations use
/// at the call boundary; everything else delegates to `irTypeToCpp`.
///
/// Without this helper, std::function<> templates would be filled with
/// the raw-pointer form (`promote<T, N>::type`), which doesn't match the
/// wrapper-form return type that `genFuncDef` emits for array-returning
/// functions. The mismatch would block `funcs[i] = arrayReturningFunc;`
/// assignments because the function-pointer signature wouldn't be
/// convertible to the std::function<...> target.
and arrowSlotTypeForFuncSig (ty: IRType) : string =
    // Note: this helper sits inside the elemTypeToCpp/irTypeToCpp
    // recursion group, so it cannot forward-reference helpers defined
    // later in the file (isRaggedArrayType, isDepIdxArrayType). For
    // ragged or DepIdx array types appearing inside a function signature,
    // the generic Array<T, N> rendering here would mismatch genFuncDef's
    // Ragged<T> rendering at the declaration site. No current test
    // exercises that combination; revisit if a future one does.
    match ty with
    | ArrayElem arr ->
        sprintf "Array<%s, %d>" (elemTypeToCpp arr.ElemType) (arrayRank arr)
    | _ -> irTypeToCpp ty


/// Extract the element type from an expression that should be array-shaped or scalar.
/// On failure (type not array/scalar after upstream inference), record a codegen
/// warning and emit a `#error` line as the rendered code. The point is to fail
/// loudly rather than silently emit code that might miscompile — e.g., indexing
/// with a float, or narrowing int64 keys to double in a sort comparator.
/// Returns (elemType as IRType, optional error code). Phase B2.
let inferElemTypeStrict (ctx: CodeGenContext) (ind: string) (expr: IRExpr) (opName: string) : IRType * string list =
    match inferExprType expr with
    | ArrayElem arr -> (arr.ElemType, [])
    | IRTScalar _ as t -> (t, [])
    | t ->
        let msg = sprintf "%s: could not determine element type from expression (got %A) — likely a typechecker or IR bug" opName t
        let errLines = codegenError ctx ind msg
        // Sentinel: the #error makes the C++ refuse to compile, so the
        // Float64 we return is never actually exercised in valid output.
        (IRTScalar ETFloat64, errLines)


// ============================================================================
// C++ Expression Generation
// ============================================================================

/// Convert binary operator to C++ string
let binOpToCpp = function
    | IRAdd -> "+" | IRSub -> "-" | IRMul -> "*" | IRDiv -> "/"
    | IRMod -> "%" | IRCaret -> "pow"  // Special handling needed
    | IREq -> "==" | IRNeq -> "!=" 
    | IRLt -> "<" | IRLe -> "<=" | IRGt -> ">" | IRGe -> ">="
    | IRAnd -> "&&" | IROr -> "||"

/// Convert unary operator to C++ string
let unaryOpToCpp = function
    | IRNeg -> "-"
    | IRNot -> "!"

/// Quote a Blade string value as a C++ string literal. Escapes the minimal
/// set that would otherwise break the surrounding "..." token: backslash,
/// double-quote, and the four common control characters. Other characters
/// pass through, including UTF-8 multibyte sequences (which are valid as
/// raw bytes inside a C++ "..." literal).
let escapeStringLit (s: string) : string =
    let sb = System.Text.StringBuilder()
    sb.Append('"') |> ignore
    for c in s do
        match c with
        | '\\' -> sb.Append("\\\\") |> ignore
        | '"'  -> sb.Append("\\\"") |> ignore
        | '\n' -> sb.Append("\\n") |> ignore
        | '\r' -> sb.Append("\\r") |> ignore
        | '\t' -> sb.Append("\\t") |> ignore
        | '\000' -> sb.Append("\\0") |> ignore
        | _ -> sb.Append(c) |> ignore
    sb.Append('"') |> ignore
    sb.ToString()

/// Simplified exprToCpp that doesn't recurse into complex IR nodes
/// Used for kernel bodies in inline generation
let rec exprToCppSimple (names: Map<IRId, string>) (expr: IRExpr) : string =
    match expr with
    | IRLit (IRLitInt n) -> sprintf "%dL" n
    | IRLit (IRLitFloat f) -> sprintf "%g" f
    | IRLit (IRLitBool b) -> if b then "true" else "false"
    | IRLit (IRLitString s) -> sprintf "std::string(%s)" (escapeStringLit s)
    | IRLit IRLitUnit -> "((void)0)"
    | IRVar (id, _) -> Map.tryFind id names |> Option.defaultValue (sprintf "__v%d" id)
    | IRParam (name, _, _) -> name
    | IRBinOp (_, op, l, r) ->
        let lStr = exprToCppSimple names l
        let rStr = exprToCppSimple names r
        if op = IRCaret then sprintf "pow(%s, %s)" lStr rStr
        else sprintf "(%s %s %s)" lStr (binOpToCpp op) rStr
    | IRUnaryOp (op, e) -> sprintf "%s(%s)" (unaryOpToCpp op) (exprToCppSimple names e)
    | IRGuard (cond, body) ->
        sprintf "(%s ? %s : 0.0)" (exprToCppSimple names cond) (exprToCppSimple names body)
    | other -> sprintf "BLADE_UNSUPPORTED_EXPR_%s" (other.GetType().Name.ToUpper())

/// Convert IRLit to C++ literal string
let litToCpp (lit: IRLit) : string =
    match lit with
    | IRLitInt n -> sprintf "%dL" n
    | IRLitFloat f -> sprintf "%g" f
    | IRLitBool b -> if b then "true" else "false"
    | IRLitString s -> sprintf "std::string(%s)" (escapeStringLit s)
    | IRLitUnit -> "((void)0)"  // Valid C++ no-op; should be elided by callers

/// Detect whether an IRArrayType represents a ragged array (any RaggedIdx
/// variant). True when at least one IndexType has a ragged tag:
///   - __raggedidx_inline   : inferred from a ragged literal
///   - __raggedidx          : closed RaggedIdx<lens> from a type annotation
///   - __raggedidx_opaque   : opaque RaggedIdx<_> (kernel param / sub-array)
/// All three share the property that the array shape carries a per-row
/// lengths companion at codegen time.
///
/// Defined here (before exprToCpp) because IRApp's call-site emission needs
/// it to decide whether to pass an `_lens` companion argument. Other ragged-
/// aware sites (genArrayLiteral, print path) live further down and use the
/// same predicate.
let isRaggedArrayType (arrTy: IRArrayType) : bool =
    arrTy.IndexTypes |> List.exists (fun idx ->
        match idx.Tag with
        | Some "__raggedidx_inline" | Some "__raggedidx" | Some "__raggedidx_opaque" -> true
        | _ -> false)

/// Detect whether an IRArrayType represents a DepIdx array — outer Idx plus
/// an inner record whose Extent is a function of the outer iteration index.
/// Recognized by the `__depidx_inner` tag on a non-first index. Once
/// allocated (lens computed from formula at construction), the runtime
/// layout matches a ragged array — same `_lens` / `_offsets` / row-pointer
/// companions — so iteration-time predicates treat both as "has row-lengths
/// companion" via `isRaggedArrayType OR isDepIdxArrayType`. The literal
/// allocation path differs (lens come from the formula, not from literal
/// structure), so genArrayLiteral keeps a separate branch.
let isDepIdxArrayType (arrTy: IRArrayType) : bool =
    arrTy.IndexTypes |> List.exists (fun idx ->
        idx.Tag = Some "__depidx_inner")

/// Evaluate a DepIdx inner-extent formula for a specific outer index value.
/// The formula is an IRExpr referencing the outer record's Id via IRVar; we
/// substitute the concrete integer `i` for that Id and fold constants. Returns
/// None if the expression contains anything we can't reduce statically (free
/// variables, IRParam, non-arithmetic ops). Used at construction time to
/// produce the `_lens` table for DepIdx-annotated literals; in that context a
/// None result means the formula is dynamic and we'd need runtime evaluation
/// (deferred — for now we error out).
let rec evalDepIdxExtent (outerId: IRId) (i: int) (expr: IRExpr) : int option =
    match expr with
    | IRLit (IRLitInt n) -> Some (int n)
    | IRVar (vid, _) when vid = outerId -> Some i
    | IRBinOp (_, op, l, r) ->
        match evalDepIdxExtent outerId i l, evalDepIdxExtent outerId i r with
        | Some a, Some b ->
            match op with
            | IRAdd -> Some (a + b)
            | IRSub -> Some (a - b)
            | IRMul -> Some (a * b)
            | IRDiv when b <> 0 -> Some (a / b)
            | IRMod when b <> 0 -> Some (a % b)
            | _ -> None
        | _ -> None
    | IRUnaryOp (IRNeg, e) -> evalDepIdxExtent outerId i e |> Option.map (fun n -> -n)
    | _ -> None

/// Convert IRExpr to C++ expression string
let rec exprToCpp (names: Map<IRId, string>) (expr: IRExpr) : string =
    match expr with
    | IRLit lit -> litToCpp lit
    | IRVar (id, _) -> 
        match Map.tryFind id names with
        | Some name -> name
        | None -> sprintf "__v%d" id
    | IRParam (name, _, _) -> name
    | IRBinOp (_, op, l, r) ->
        let lStr = exprToCpp names l
        let rStr = exprToCpp names r
        if op = IRCaret then
            sprintf "pow(%s, %s)" lStr rStr
        else
            sprintf "(%s %s %s)" lStr (binOpToCpp op) rStr
    | IRUnaryOp (op, e) ->
        sprintf "%s(%s)" (unaryOpToCpp op) (exprToCpp names e)
    | IRIf (cond, thenBr, elseBr) ->
        sprintf "(%s ? %s : %s)" 
            (exprToCpp names cond) 
            (exprToCpp names thenBr) 
            (exprToCpp names elseBr)
    | IRTuple exprs ->
        sprintf "std::make_tuple(%s)" (exprs |> List.map (exprToCpp names) |> String.concat ", ")
    | IRComplex (re, im) ->
        // Determine width from the component type. checkExpr enforces
        // that Complex128 components are Float64 and Complex64 are
        // Float32, so we inspect either component (they match) and pick
        // the corresponding C++ template instantiation.
        let cppType =
            match inferExprType re with
            | IRTScalar ETFloat32 -> "std::complex<float>"
            | _ -> "std::complex<double>"  // Float64 default
        sprintf "%s(%s, %s)" cppType (exprToCpp names re) (exprToCpp names im)
    | IRTupleProj (e, i, isFlat) ->
        if not isFlat then
            sprintf "std::get<%d>(%s)" i (exprToCpp names e)
        else
            // Flat projection into potentially nested tuple — compute navigation path
            let parentTy = inferExprType e
            let rec findPath (ty: IRType) (targetFlat: int) : int list =
                match ty with
                | IRTTuple ts ->
                    let mutable offset = 0
                    let mutable found = None
                    for idx in 0 .. ts.Length - 1 do
                        if found.IsNone then
                            let count = IR.flattenTupleLeaves ts.[idx] |> List.length
                            if targetFlat < offset + count then
                                match ts.[idx] with
                                | IRTTuple _ -> found <- Some (idx :: findPath ts.[idx] (targetFlat - offset))
                                | _ -> found <- Some [idx]
                            offset <- offset + count
                    found |> Option.defaultValue [i]
                | _ -> [i]
            let path = findPath parentTy i
            path |> List.fold (fun acc idx -> sprintf "std::get<%d>(%s)" idx acc) (exprToCpp names e)
    | IRFieldAccess (obj, field) ->
        sprintf "%s.%s" (exprToCpp names obj) field
    | IRStructLit (typeName, fields) ->
        let fieldInits = fields |> List.map (fun (fname, e) -> 
            sprintf ".%s = %s" fname (exprToCpp names e)) |> String.concat ", "
        sprintf "%s { %s }" typeName fieldInits
    | IRIndex (arr, indices, _) ->
        let arrStr = exprToCpp names arr
        let idxStr = indices |> List.map (fun i -> sprintf "[%s]" (exprToCpp names i)) |> String.concat ""
        sprintf "%s%s" arrStr idxStr
    | IRApp (func, args, _) ->
        // v24c: function signatures take Array<T,N> / Ragged<T> wrappers
        // natively, one argument per Blade param. Array args pass through
        // as-is (the wrapper carries its own shape via .extents/.lens/
        // .offsets); no companion-arg synthesis. Non-array args render
        // through exprToCpp normally.
        let funcStr = exprToCpp names func
        let argStrs =
            args |> List.collect (fun a ->
                let argStr = exprToCpp names a
                match a, inferExprType a with
                | (IRVar _ | IRParam _), ArrayElem _ -> [argStr]
                | _ -> [argStr])
        sprintf "%s(%s)" funcStr (argStrs |> String.concat ", ")
    | IRLet (id, value, body) ->
        // For inline let expressions, we need statement context
        let names' = Map.add id (sprintf "__v%d" id) names
        if isUnitExpr value then
            // Unit-valued binding: skip the auto declaration
            if isUnitExpr body then
                "((void)0)"
            else
                exprToCpp names' body
        else
            // Phase C lift pass produces IRLet bindings whose value can be
            // an inline form (mask/sort/intersect/union). These can't be
            // rendered as a single C++ expression — they need a multi-
            // statement materialization sequence. Detect that case and emit
            // an IIFE with the materialization as its prelude. The variable
            // `__v<id>` and `__v<id>_extents` come into scope for the body.
            //
            // For all other values (scalars, function calls, IRApplyCombinator
            // results, etc.), the existing "auto __v = ..." form is correct.
            let inlineElemTypeStr (form: IRExpr) =
                // Silent fallback. The lift pass should produce types that
                // resolve via inferExprType; if not, "double" is wrong but
                // the g++ narrowing backstop will surface mismatches.
                let arrExpr =
                    match form with
                    | IRMask (a, _) | IRSort (a, _)
                    | IRIntersect (a, _) | IRUnion (a, _) -> a
                    | _ -> form
                match inferExprType arrExpr with
                | ArrayElem a -> elemTypeToCpp a.ElemType
                | _ -> "double"
            match materializeInlineForm names (sprintf "__v%d" id) (inlineElemTypeStr value) value with
            | Some preludeStmts ->
                let bodyStr =
                    if isUnitExpr body then "((void)0)"
                    else exprToCpp names' body
                let prelude = preludeStmts |> String.concat " "
                sprintf "([&]() { %s return %s; }())" prelude bodyStr
            | None ->
                let valStr = exprToCpp names value
                match body with
                | IRLit IRLitUnit ->
                    sprintf "([&]() { auto __v%d = %s; }())" id valStr
                | _ ->
                    let bodyStr = exprToCpp names' body
                    sprintf "([&]() { auto __v%d = %s; return %s; }())" id valStr bodyStr
    | IRLambda info ->
        // Generate C++ lambda
        let paramList = info.Params |> List.map (fun p -> sprintf "%s %s" (irTypeToCpp p.Type) p.Name) |> String.concat ", "
        let names' = info.Params |> List.fold (fun m p -> Map.add p.VarId p.Name m) names
        if isUnitExpr info.Body then
            sprintf "[&](%s) { }" paramList
        else
            let bodyStr = exprToCpp names' info.Body
            sprintf "[&](%s) { return %s; }" paramList bodyStr
    | IRMethodFor _ -> exprError "loop object used as value"
    | IRObjectFor _ -> exprError "loop object used as value"
    | IRApplyCombinator _ -> 
        exprError "unevaluated computation used as value - use |> compute"
    | IRCompute inner -> 
        // compute forces evaluation of a lazy computation
        match inner with
        | IRApplyCombinator info -> genApplyCombinatorExpr names info
        | _ -> exprToCpp names inner  // For non-combinator compute, just evaluate
    | IRPure e -> exprToCpp names e     // pure wraps value
    | IRRank arr -> 
        // Rank is known statically from the type
        let rank = match inferExprType arr with
                   | ArrayElem at -> arrayRank at
                   | _ -> 0
        sprintf "%dL" rank
    | IRExtent (arr, dim) ->
        // Statically resolved when the index type's extent expression is a
        // literal-arithmetic value (Idx<5>, Idx<n+1> with n compile-time, etc.)
        // — emit as a compile-time literal eligible for use in static contexts.
        // Falls back to a runtime read from <name>_extents[dim] for genuinely
        // dynamic extents (mask, group_by groups, sort outputs derived from
        // those, etc.).
        match inferExprType arr with
        | ArrayElem at when dim < at.IndexTypes.Length ->
            match tryEvalIntIR at.IndexTypes.[dim].Extent with
            | Some n -> sprintf "%dL" n
            | None ->
                let arrName = exprToCpp names arr
                sprintf "(int64_t)(%s.extents[%d])" arrName dim
        | _ ->
            // Should be unreachable — typecheck rejects non-arrays. Surface a
            // visible #error rather than emit garbage if the IR is malformed.
            "/* extents: argument is not an array (typechecker bug) */"

    | IRReduce (arrExpr, kernelExpr) ->
        // Inline reduction as an IIFE. Mirrors the genBinding form's loop but
        // wraps it in `[&]() { ... }()` so it can appear in expression context
        // — kernel bodies (lambda(g) -> reduce(g)) and arithmetic
        // (x + reduce(arr) / count). Capture-by-reference picks up arr,
        // arr_extents, and any names referenced by the kernel body.
        //
        // Empty-array policy matches the genBinding form: skip the runtime
        // guard when the extent is statically proven > 0 (typecheck has
        // already rejected statically-empty inputs); emit the guard for
        // dynamic extents (mask results, group_by groups, etc.).
        let arrStr = exprToCpp names arrExpr
        let elemType =
            match inferExprType arrExpr with
            | ArrayElem a -> a.ElemType
            | _ -> IRTScalar ETFloat64  // Fallback; typecheck enforces array input
        let elemStr = elemTypeToCpp elemType
        let isStaticallyNonEmpty =
            match inferExprType arrExpr with
            | ArrayElem at when at.IndexTypes.Length >= 1 ->
                match tryEvalIntIR at.IndexTypes.[at.IndexTypes.Length - 1].Extent with
                | Some n -> n > 0L
                | None -> false
            | _ -> false
        match kernelExpr with
        | IRLambda lInfo when lInfo.Params.Length = 2 ->
            // Use IIFE-local names "__r_a" and "__r_b" for the kernel params.
            // C++ scoping makes these collision-free even for nested reduces.
            let aName = "__r_a"
            let bName = "__r_b"
            let kNames =
                names
                |> Map.add lInfo.Params.[0].VarId aName
                |> Map.add lInfo.Params.[1].VarId bName
                |> fun m -> lInfo.Captures |> List.fold (fun acc c -> Map.add c.Id c.Name acc) m
            let kStr = exprToCpp kNames lInfo.Body
            // v24d-1: consumers read shape via `.extents` member.
            let guard =
                if isStaticallyNonEmpty then ""
                else sprintf "if (%s.extents[0] == 0) { std::cerr << \"reduce: empty array, no reduction possible\" << std::endl; std::abort(); } " arrStr
            sprintf "[&]() { %sauto __op = [&](%s %s, %s %s) { return %s; }; %s __r = %s[0]; for (size_t __ri = 1; __ri < %s.extents[0]; __ri++) { __r = __op(__r, %s[__ri]); } return __r; }()"
                guard elemStr aName elemStr bName kStr elemStr arrStr arrStr arrStr
        | _ ->
            "/* reduce: non-lambda kernel inline (typechecker or IR bug) */"
    | IRArity (Some n, _) -> sprintf "%d" n
    | IRArity (None, paramName) -> 
        // Arity of poly pack - use tuple_size on the named parameter
        sprintf "std::tuple_size_v<std::decay_t<decltype(%s)>>" paramName
    | IRBind (comp, cont) ->
        // Monadic bind - comp >>= cont
        sprintf "%s(%s)" (exprToCpp names cont) (exprToCpp names comp)
    | IRReynolds (kernel, isAntisym) ->
        // Reynolds operator wraps kernel
        exprError "reynolds wrapper in expression position"
    | IRZip arrs ->
        // In expression context (e.g. inside a kernel body), zip produces a tuple
        sprintf "std::make_tuple(%s)" (arrs |> List.map (exprToCpp names) |> String.concat ", ")
    | IRStack arrs ->
        exprError "stack not yet implemented in codegen"
    | IRSlice (arr, dim, start, stop) ->
        exprError "slice not yet implemented in codegen"
    | IRCurry (arr, idx, resultRank) ->
        sprintf "%s[%s]" (exprToCpp names arr) (exprToCpp names idx)
    | IRTupleCons (head, tail) ->
        sprintf "std::tuple_cat(std::make_tuple(%s), %s)" (exprToCpp names head) (exprToCpp names tail)
    | IRTupleDecons tuple ->
        exprToCpp names tuple  // Decons is handled by projection
    | IRMatch (scrutinee, cases) ->
        // Generate nested ternary for match expressions
        let scrut = exprToCpp names scrutinee
        let rec genCase (cases: IRMatchCase list) : string =
            match cases with
            | [] -> "([&]() -> double { std::cerr << \"Blade: non-exhaustive match\" << std::endl; std::abort(); return 0; }())"
            | [case] ->
                // Last case - assume it matches (wildcard or variable)
                // But if there's a guard, we must still check it.
                let abortExpr = "([&]() -> double { std::cerr << \"Blade: non-exhaustive match\" << std::endl; std::abort(); return 0; }())"
                let wrapGuard (bodyStr: string) (names': Map<IRId, string>) : string =
                    match case.Guard with
                    | Some guard ->
                        let guardStr = exprToCpp names' guard
                        sprintf "(%s ? %s : %s)" guardStr bodyStr abortExpr
                    | None -> bodyStr
                match case.Pattern with
                | IRPatVar varId ->
                    // Bind variable and evaluate body (only if variable is used)
                    let varUsed =
                        (collectVarRefs case.Body).Contains varId ||
                        (case.Guard |> Option.map (fun g -> (collectVarRefs g).Contains varId) |> Option.defaultValue false)
                    if varUsed then
                        let varName = sprintf "__match_%d" varId
                        let names' = Map.add varId varName names
                        let bodyStr = exprToCpp names' case.Body
                        let guardedBody = wrapGuard bodyStr names'
                        sprintf "[&]() { auto %s = %s; return %s; }()" varName scrut guardedBody
                    else
                        wrapGuard (exprToCpp names case.Body) names
                | IRPatWild ->
                    wrapGuard (exprToCpp names case.Body) names
                | IRPatLit lit ->
                    let litStr = litToCpp lit
                    let bodyStr = wrapGuard (exprToCpp names case.Body) names
                    sprintf "(%s == %s ? %s : %s)" scrut litStr bodyStr abortExpr
                | IRPatVariant (ctorName, tag, innerOpt, isEnum) ->
                    // Last variant case — extract payload and evaluate body
                    match innerOpt with
                    | Some (IRPatVar varId) ->
                        let varName = sprintf "__match_%d" varId
                        let names' = Map.add varId varName names
                        let extractExpr = sprintf "std::get<%s_T>(%s).value" ctorName scrut
                        let bodyStr = exprToCpp names' case.Body
                        let guardedBody = wrapGuard bodyStr names'
                        sprintf "[&]() { auto %s = %s; return %s; }()" varName extractExpr guardedBody
                    | _ ->
                        wrapGuard (exprToCpp names case.Body) names
                | IRPatTuple innerPats ->
                    // Last tuple case — bind each element
                    let bindings =
                        innerPats |> List.mapi (fun idx pat ->
                            match pat with
                            | IRPatVar varId -> Some (varId, sprintf "__match_%d" varId, idx)
                            | _ -> None)
                        |> List.choose id
                    let bindingDecls = bindings |> List.map (fun (_, name, idx) ->
                        sprintf "auto %s = std::get<%d>(%s)" name idx scrut) |> String.concat "; "
                    let names' = bindings |> List.fold (fun acc (id, name, _) -> Map.add id name acc) names
                    let bodyStr = exprToCpp names' case.Body
                    let guardedBody = wrapGuard bodyStr names'
                    sprintf "[&]() { %s; return %s; }()" bindingDecls guardedBody
                | _ ->
                    wrapGuard (exprToCpp names case.Body) names
            | case :: rest ->
                let restStr = genCase rest
                match case.Pattern with
                | IRPatLit lit ->
                    let litStr = litToCpp lit
                    let bodyStr = 
                        match case.Guard with
                        | Some guard -> 
                            let guardStr = exprToCpp names guard
                            sprintf "(%s ? %s : %s)" guardStr (exprToCpp names case.Body) restStr
                        | None -> exprToCpp names case.Body
                    sprintf "(%s == %s ? %s : %s)" scrut litStr bodyStr restStr
                | IRPatVar varId ->
                    let varUsed =
                        (collectVarRefs case.Body).Contains varId ||
                        (case.Guard |> Option.map (fun g -> (collectVarRefs g).Contains varId) |> Option.defaultValue false)
                    if varUsed then
                        let varName = sprintf "__match_%d" varId
                        match case.Guard with
                        | Some guard ->
                            // Variable pattern with guard, variable used
                            let guardStr = exprToCppWithVar names varId varName guard
                            let bodyStr = exprToCppWithVar names varId varName case.Body
                            sprintf "[&]() { auto %s = %s; return %s ? %s : %s; }()" varName scrut guardStr bodyStr restStr
                        | None ->
                            // Variable pattern without guard - always matches, variable used
                            let bodyStr = exprToCppWithVar names varId varName case.Body
                            sprintf "[&]() { auto %s = %s; return %s; }()" varName scrut bodyStr
                    else
                        match case.Guard with
                        | Some guard ->
                            // Variable unused, but has guard
                            let guardStr = exprToCpp names guard
                            let bodyStr = exprToCpp names case.Body
                            sprintf "(%s ? %s : %s)" guardStr bodyStr restStr
                        | None ->
                            // Variable unused, no guard - always matches (like wildcard)
                            exprToCpp names case.Body
                | IRPatWild ->
                    match case.Guard with
                    | Some guard ->
                        let guardStr = exprToCpp names guard
                        let bodyStr = exprToCpp names case.Body
                        sprintf "(%s ? %s : %s)" guardStr bodyStr restStr
                    | None ->
                        // Wildcard without guard - always matches
                        exprToCpp names case.Body
                | IRPatTuple innerPats ->
                    // Tuple pattern - bind each element
                    let rec collectVarBindings (pats: IRPattern list) (idx: int) : (IRId * string) list =
                        match pats with
                        | [] -> []
                        | IRPatVar varId :: rest ->
                            let varName = sprintf "__match_%d" varId
                            (varId, varName) :: collectVarBindings rest (idx + 1)
                        | _ :: rest -> collectVarBindings rest (idx + 1)
                    
                    let bindings = collectVarBindings innerPats 0
                    let bindingDecls = bindings |> List.mapi (fun idx (_, name) ->
                        sprintf "auto %s = std::get<%d>(%s)" name idx scrut) |> String.concat "; "
                    
                    // Extend names map with bindings
                    let names' = bindings |> List.fold (fun acc (id, name) -> Map.add id name acc) names
                    
                    match case.Guard with
                    | Some guard ->
                        let guardStr = exprToCpp names' guard
                        let bodyStr = exprToCpp names' case.Body
                        sprintf "[&]() { %s; return %s ? %s : %s; }()" bindingDecls guardStr bodyStr restStr
                    | None ->
                        let bodyStr = exprToCpp names' case.Body
                        sprintf "[&]() { %s; return %s; }()" bindingDecls bodyStr
                | IRPatVariant (ctorName, tag, innerOpt, isEnum) ->
                    // Variant pattern - check variant type and optionally bind inner value
                    let checkExpr =
                        if isEnum then sprintf "%s == %s" scrut ctorName
                        else sprintf "std::holds_alternative<%s_T>(%s)" ctorName scrut
                    
                    match innerOpt with
                    | Some (IRPatVar varId) ->
                        // Variant with inner value binding
                        let varName = sprintf "__match_%d" varId
                        let names' = Map.add varId varName names
                        let extractExpr = sprintf "std::get<%s_T>(%s).value" ctorName scrut
                        let bodyStr = exprToCpp names' case.Body
                        sprintf "(%s ? [&]() { auto %s = %s; return %s; }() : %s)" checkExpr varName extractExpr bodyStr restStr
                    | Some _ ->
                        // Other inner patterns - fallback
                        let bodyStr = exprToCpp names case.Body
                        sprintf "(%s ? %s : %s)" checkExpr bodyStr restStr
                    | None ->
                        // Variant without inner value
                        let bodyStr = exprToCpp names case.Body
                        sprintf "(%s ? %s : %s)" checkExpr bodyStr restStr
                | _ ->
                    // Unsupported pattern - fallback
                    sprintf "(true ? %s : %s)" (exprToCpp names case.Body) restStr
        genCase cases
    | IRNth -> exprError "nth keyword not supported in expression position"
    | IRZero -> "0"
    | IRPolyIndex (pack, idx) ->
        // For static index, use std::get; otherwise runtime indexing
        match idx with
        | IRLit (IRLitInt n) -> sprintf "std::get<%d>(%s)" n (exprToCpp names pack)
        | _ -> sprintf "%s[%s]" (exprToCpp names pack) (exprToCpp names idx)
    | IRParallel (a, b, _) ->
        exprError "parallel combinator in expression position"
    | IRFusion (a, b) ->
        exprError "fusion combinator in expression position"
    | IRChoice (a, b) ->
        let aStr = exprToCpp names a
        let bStr = exprToCpp names b
        sprintf "(%s != 0 ? %s : %s)" aStr aStr bStr
    | IRGuard (cond, body) ->
        // guard(p, c) → p ? c : 0 (type-appropriate zero)
        let condStr = exprToCpp names cond
        let bodyStr = exprToCpp names body
        let zeroStr =
            match inferExprType body with
            | IRTScalar ETBool -> "false"
            | IRTScalar ETInt64 | IRTScalar ETInt32 -> "0L"
            | IRTIdxTagged (IRTScalar (ETInt64 | ETInt32), _) -> "0L"
            | _ -> "0.0"
        sprintf "(%s ? %s : %s)" condStr bodyStr zeroStr
    | IRCompose (f, g) ->
        // f >> g = [&](auto... args) { return g(f(args...)); }
        let fStr = exprToCpp names f
        let gStr = exprToCpp names g
        sprintf "[&](auto... __args) { return %s(%s(__args...)); }" gStr fStr
    | IRComposeObj (f, g) ->
        exprError "compose_obj in expression position"
    | IRComposeMeth (f, g) ->
        exprError "compose_meth in expression position"
    | IRArrayProduct (a, b) ->
        exprError "array_product in expression position"
    | IRFunctorMap (f, c) ->
        exprError "functor_map in expression position"
    | IRAssign (target, value) ->
        let targetStr =
            match target with
            | LVVar id -> Map.tryFind id names |> Option.defaultValue (sprintf "__v%d" id)
            | LVIndex (arr, idxs) ->
                let arrStr = exprToCpp names arr
                let idxStr = idxs |> List.map (fun i -> sprintf "[%s]" (exprToCpp names i)) |> String.concat ""
                sprintf "%s%s" arrStr idxStr
            | LVField (obj, f) -> sprintf "%s.%s" (exprToCpp names obj) f
            | LVOther e -> exprError "invalid assignment target"
        sprintf "%s = %s" targetStr (exprToCpp names value)
    | IRForRange (vid, lo, hi, body) ->
        exprError "for-range loop in expression position"
    | IROpaqueExtent ->
        // IROpaqueExtent is a marker that lives inside an IRIndexType.Extent
        // slot; it should never reach exprToCpp directly. If it does, the
        // loop builder failed to substitute the surrounding context for the
        // sub-array binding (the ExtentArrayRef path). Surface a visible
        // error rather than silently emit the wrong value.
        exprError "opaque-extent marker reached expression rendering — kernel-param sub-array was not bound to a concrete extent at the peel point (codegen routing bug)"
    | other -> exprError (sprintf "unsupported IR node: %s" (other.GetType().Name))

/// Generate inline combinator application as an IIFE expression.
/// This is used when L <@> f appears in expression context (not as a let
/// binding's RHS or a function-body return). The let-binding and return
/// cases route through genApplyCombinator (the statement form) which uses
/// the full LoopNestCodeGen machinery and handles every shape uniformly.
///
/// LIMITATION (carried forward from the original implementation): this
/// function only supports the 2-array Cartesian-sum-reduce shape inline.
/// Other shapes (1-array, 3+ arrays, array output, anything with
/// commutativity/Reynolds/co-iteration) emit a BLADE_CODEGEN_ERROR sentinel
/// and crash the C++ build. The principled fix is to make this a thin
/// wrapper around genApplyCombinator's statement output:
///   `[&]() { <statements>; return <name>; }()`.
/// That delegation is deferred — no current test exercises an inline
/// non-2-array combinator, so the cleanup waits for one. Bindings and
/// returns already go through genApplyCombinator, which is where the real
/// machinery lives.
and genApplyCombinatorExpr (names: Map<IRId, string>) (info: ApplyInfo) : string =
    // Extract array info
    let arrayNames = 
        info.Arrays |> List.mapi (fun i arr ->
            match arr with
            | IRVar (id, _) -> Map.tryFind id names |> Option.defaultValue (sprintf "arr%d" i)
            | IRParam (name, _, _) -> name
            | _ -> sprintf "arr%d" i)
    
    // Extract kernel
    let kernelExpr = 
        match info.Kernel with
        | IRLambda lInfo -> 
            let paramNames = lInfo.Params |> List.map (fun p -> p.Name)
            let bodyStr = 
                let names' = lInfo.Params |> List.fold (fun m p -> Map.add p.VarId p.Name m) names
                exprToCpp names' lInfo.Body
            (paramNames, bodyStr)
        | IRReynolds (IRLambda lInfo, _) ->
            let paramNames = lInfo.Params |> List.map (fun p -> p.Name)
            let bodyStr = 
                let names' = lInfo.Params |> List.fold (fun m p -> Map.add p.VarId p.Name m) names
                exprToCpp names' lInfo.Body
            (paramNames, bodyStr)
        | _ -> ([], exprError "kernel is not a lambda in inline combinator expression")
    
    let (paramNames, kernelBody) = kernelExpr
    
    // Infer output element type from ApplyInfo
    let elemTypeStr =
        match info.OutputType with
        | IRTScalar et -> primTypeToCpp et
        | ArrayElem arr -> elemTypeToCpp arr.ElemType
        | t -> irTypeToCpp t
    
    // For scalar output (simple accumulation), generate inline loop
    // This is a simplified version - full version would use LoopNestCodeGen
    if arrayNames.Length = 2 && paramNames.Length = 2 then
        let arr1 = arrayNames.[0]
        let arr2 = arrayNames.[1]
        let p1 = paramNames.[0]
        let p2 = paramNames.[1]
        // Generate as IIFE with nested loops
        // Use _extents arrays for size (works with both top-level arrays and function params)
        sprintf "([&]() { %s __result = 0; for (size_t __i0 = 0; __i0 < %s.extents[0]; __i0++) { %s %s = %s[__i0]; for (size_t __i1 = 0; __i1 < %s.extents[0]; __i1++) { %s %s = %s[__i1]; __result += %s; } } return __result; }())"
            elemTypeStr arr1 elemTypeStr p1 arr1 arr2 elemTypeStr p2 arr2 kernelBody
    else
        // Fallback - can't generate inline code
        exprError (sprintf "inline combinator not supported for %d arrays" arrayNames.Length)

/// Convert IRExpr to C++ with an additional variable binding
and exprToCppWithVar (names: Map<IRId, string>) (varId: IRId) (varName: string) (expr: IRExpr) : string =
    let names' = Map.add varId varName names
    exprToCpp names' expr

/// Generate the C++ statements that materialize an inline form (IRMask,
/// IRIntersect, IRUnion, IRSort) into `varName` and `varName + "_extents"`.
///
/// Returns statements WITHOUT leading indentation; callers add their own
/// prefix (per-line `ind` for genBinding/genFuncBody, space-joining for
/// inline IIFE inside exprToCpp).
///
/// `elemTypeStr` is the C++ element type of the form's result array, as
/// already resolved by the caller. Different callers have different needs
/// for type-resolution strictness:
///   - genBinding uses inferElemTypeStrict (emits #error on unresolvable)
///   - genFuncBody / exprToCpp IIFE use silent fallback (lift pass should
///     guarantee resolvable types upstream, but no #error scaffolding here)
/// Pulling elem-type resolution out of the helper keeps it format-neutral
/// and lets each caller surface errors appropriately for its context.
///
/// Mutually recursive with exprToCpp so it can render nested predicate
/// and key bodies. Returns None for forms outside this set.
and materializeInlineForm (names: Map<IRId, string>) (varName: string) (elemTypeStr: string) (form: IRExpr) : string list option =
    match form with
    | IRMask (arrExpr, predExpr) ->
        let arrName = exprToCpp names arrExpr
        // Predicate must be a single-param lambda (TypeCheck enforces).
        let (predParamName, predStr) =
            match predExpr with
            | IRLambda lInfo when lInfo.Params.Length = 1 ->
                let pName = sprintf "__%s_x" varName
                let predNames =
                    names
                    |> Map.add lInfo.Params.[0].VarId pName
                    |> fun m -> lInfo.Captures |> List.fold (fun acc c -> Map.add c.Id c.Name acc) m
                (pName, exprToCpp predNames lInfo.Body)
            | _ -> (sprintf "__%s_x" varName, "true")
        Some [
            sprintf "size_t %s__count = 0;" varName
            sprintf "for (size_t __mi = 0; __mi < %s.extents[0]; __mi++) {" arrName
            sprintf "    %s %s = %s[__mi];" elemTypeStr predParamName arrName
            sprintf "    if (%s) %s__count++;" predStr varName
            "}"
            sprintf "size_t %s_extents[1] = {%s__count};" varName varName
            sprintf "Array<%s, 1> %s = { new %s[%s__count], %s_extents };" elemTypeStr varName elemTypeStr varName varName
            sprintf "size_t %s__fill = 0;" varName
            sprintf "for (size_t __mi = 0; __mi < %s.extents[0]; __mi++) {" arrName
            sprintf "    %s %s = %s[__mi];" elemTypeStr predParamName arrName
            sprintf "    if (%s) { %s[%s__fill++] = %s; }" predStr varName varName predParamName
            "}"
        ]
    | IRIntersect (aExpr, bExpr) ->
        let aName = exprToCpp names aExpr
        let bName = exprToCpp names bExpr
        Some [
            sprintf "std::set<%s> %s__set;" elemTypeStr varName
            sprintf "for (size_t __si = 0; __si < %s.extents[0]; __si++) %s__set.insert(%s[__si]);" bName varName bName
            sprintf "size_t %s__count = 0;" varName
            sprintf "for (size_t __si = 0; __si < %s.extents[0]; __si++) {" aName
            sprintf "    if (%s__set.count(%s[__si])) %s__count++;" varName aName varName
            "}"
            sprintf "size_t %s_extents[1] = {%s__count};" varName varName
            sprintf "Array<%s, 1> %s = { new %s[%s__count], %s_extents };" elemTypeStr varName elemTypeStr varName varName
            sprintf "size_t %s__fill = 0;" varName
            sprintf "for (size_t __si = 0; __si < %s.extents[0]; __si++) {" aName
            sprintf "    if (%s__set.count(%s[__si])) %s[%s__fill++] = %s[__si];" varName aName varName varName aName
            "}"
        ]
    | IRUnion (aExpr, bExpr) ->
        let aName = exprToCpp names aExpr
        let bName = exprToCpp names bExpr
        Some [
            sprintf "std::set<%s> %s__set;" elemTypeStr varName
            sprintf "for (size_t __si = 0; __si < %s.extents[0]; __si++) %s__set.insert(%s[__si]);" aName varName aName
            sprintf "size_t %s__extra = 0;" varName
            sprintf "for (size_t __si = 0; __si < %s.extents[0]; __si++) {" bName
            sprintf "    if (!%s__set.count(%s[__si])) %s__extra++;" varName bName varName
            "}"
            sprintf "size_t %s__total = %s.extents[0] + %s__extra;" varName aName varName
            sprintf "size_t %s_extents[1] = {%s__total};" varName varName
            sprintf "Array<%s, 1> %s = { new %s[%s__total], %s_extents };" elemTypeStr varName elemTypeStr varName varName
            sprintf "for (size_t __si = 0; __si < %s.extents[0]; __si++) %s[__si] = %s[__si];" aName varName aName
            sprintf "size_t %s__fill = %s.extents[0];" varName aName
            sprintf "for (size_t __si = 0; __si < %s.extents[0]; __si++) {" bName
            sprintf "    if (!%s__set.count(%s[__si])) %s[%s__fill++] = %s[__si];" varName bName varName varName bName
            "}"
        ]
    | IRSort (arrExpr, keyExpr) ->
        let arrName = exprToCpp names arrExpr
        let (keyParamId, keyBody, keyCaptures) =
            match keyExpr with
            | IRLambda lInfo when lInfo.Params.Length = 1 ->
                (lInfo.Params.[0].VarId, lInfo.Body, lInfo.Captures)
            | _ -> (0, IRLit (IRLitInt 0L), [])
        let keyParamName = sprintf "__%s_x" varName
        let keyNames =
            names
            |> Map.add keyParamId keyParamName
            |> fun m -> keyCaptures |> List.fold (fun acc c -> Map.add c.Id c.Name acc) m
        let keyStr = exprToCpp keyNames keyBody
        Some [
            sprintf "auto %s__key = [&](%s %s) { return %s; };" varName elemTypeStr keyParamName keyStr
            sprintf "size_t* %s__perm = new size_t[%s.extents[0]];" varName arrName
            sprintf "for (size_t __pi = 0; __pi < %s.extents[0]; __pi++) %s__perm[__pi] = __pi;" arrName varName
            sprintf "std::stable_sort(%s__perm, %s__perm + %s.extents[0], [&](size_t __a, size_t __b) {" varName varName arrName
            sprintf "    return %s__key(%s[__a]) < %s__key(%s[__b]);" varName arrName varName arrName
            "});"
            sprintf "size_t %s_extents[1] = {%s.extents[0]};" varName arrName
            sprintf "Array<%s, 1> %s = { new %s[%s.extents[0]], %s_extents };" elemTypeStr varName elemTypeStr arrName varName
            sprintf "for (size_t __si = 0; __si < %s.extents[0]; __si++) %s[__si] = %s[%s__perm[__si]];" arrName varName arrName varName
        ]
    | _ -> None

/// Convert IRExpr to C++ using context
let exprToCppCtx (ctx: CodeGenContext) (expr: IRExpr) : string =
    exprToCpp ctx.VarNames expr

// ============================================================================
// Loop Nest Code Generation
// ============================================================================

/// Generate the element binding expression for a single array at a loop level
/// Returns (cppCode, newPeeledName) where newPeeledName is used for subsequent levels
let genElementBindingNew (level: LoopIndexBinding) (elem: ElementBinding) (currentName: string) 
    : string * string =
    match elem.Virtual with
    | VirtualRange offset ->
        // range<I>: kernel param gets the loop index, plus offset if present
        let valueExpr =
            match offset with
            | None -> level.IndexName
            | Some (IRLit (IRLitInt n)) -> sprintf "(%s + %dL)" level.IndexName n
            | Some off -> sprintf "(%s + %s)" level.IndexName (exprToCpp Map.empty off)
        let code = sprintf "size_t %s = %s;" elem.ParamName valueExpr
        (code, elem.ParamName)
    | VirtualReverse ->
        // reverse<I>: kernel param gets (extent - 1 - i)
        let extentStr =
            match level.Extent with
            | IRLit (IRLitInt n) -> sprintf "%d" n
            | _ -> sprintf "%s.extents[%d]" elem.ArrayName elem.DimIndex
        let code = sprintf "size_t %s = (%s - 1 - %s);" elem.ParamName extentStr level.IndexName
        (code, elem.ParamName)
    | RealArray ->
        // After indexing once, remaining rank decreases
        let levelsConsumed = elem.ArityComponent + 1  // How many levels of this array consumed so far
        let resultRank = elem.ArrayRank - levelsConsumed
        let elemTypeStr = elemTypeToCpp elem.ArrayElemType
        
        // For left-justified iteration, array index = current + sum of dependencies
        let arrayIndex = 
            if level.BoundDependencies.IsEmpty then
                level.IndexName
            else
                let deps = level.BoundDependencies |> List.map (sprintf "__i%d") |> String.concat " + "
                sprintf "%s + %s" level.IndexName deps
        
        let newName = sprintf "%s__%s" currentName level.IndexName
        let code =
            if resultRank <= 0 then
                // Scalar leaf: peel returns the element value directly.
                sprintf "%s %s = %s[%s];" elemTypeStr newName currentName arrayIndex
            else
                // Sub-array peel: construct a wrapper so the sub still
                // carries shape information (.extents shifted one level
                // deeper). The wrapper's data pointer comes from indexing
                // the parent's data; the extents pointer is parent's
                // extents+1. Indexing transparency works through operator[].
                sprintf "Array<%s, %d> %s = { %s.data[%s], %s.extents + 1 };" 
                    elemTypeStr resultRank newName currentName arrayIndex currentName
        (code, newName)

/// Generate a for-loop header with optional OpenMP pragma
/// Bounds are computed as: extent - sum of all dependency indices
let genForLoopHeader (binding: LoopIndexBinding) : string =
    let pragma = if binding.IsParallel then "#pragma omp parallel for\n    " else ""
    let extentStr = 
        match binding.Extent with
        | IRLit (IRLitInt n) -> sprintf "%d" n
        | _ -> sprintf "%s.extents[%d]" binding.ExtentArrayRef binding.ExtentDimRef
    
    // Compute bound subtraction from dependencies
    let subtraction =
        if binding.BoundDependencies.IsEmpty && binding.StrictOffset = 0 then ""
        else 
            let depParts = binding.BoundDependencies |> List.map (sprintf "__i%d")
            let offsetParts = if binding.StrictOffset > 0 then [sprintf "%d" binding.StrictOffset] else []
            depParts @ offsetParts |> String.concat " - " |> sprintf " - %s"
    
    sprintf "%sfor (size_t %s = 0; %s < %s%s; %s++) {" 
        pragma
        binding.IndexName 
        binding.IndexName 
        extentStr
        subtraction
        binding.IndexName

/// Generate complete loop nest as C++ code
/// Tracks peeled names across levels and generates element bindings for all arrays at each level
/// Generate all permutations of a list of integers
let rec permutations (items: int list) : int list list =
    match items with
    | [] -> [[]]
    | _ ->
        items |> List.collect (fun x ->
            let rest = items |> List.filter (fun i -> i <> x)
            permutations rest |> List.map (fun p -> x :: p))

/// Count inversions to get permutation sign (+1 for even, -1 for odd)

/// Is this binary operation commutative? (a op b) = (b op a)
let isCommutativeOp (op: IRBinOp) : bool =
    match op with
    | IRAdd | IRMul | IREq | IRNeq | IRAnd | IROr -> true
    | _ -> false

/// Is this binary operation associative? (a op b) op c = a op (b op c)
/// Only ops that are BOTH commutative and associative get flattened.
let isAssociativeOp (op: IRBinOp) : bool =
    match op with
    | IRAdd | IRMul | IRAnd | IROr -> true
    | _ -> false

/// Flatten nested applications of the same commutative+associative op into a list of operands.
/// E.g. (a * b) * c → [a; b; c]
let rec flattenAssocOp (mode: IRBinOpMode) (op: IRBinOp) (expr: IRExpr) : IRExpr list =
    match expr with
    | IRBinOp (m, o, l, r) when o = op && m = mode ->
        flattenAssocOp mode op l @ flattenAssocOp mode op r
    | _ -> [expr]

/// Generate a canonical string key for an IR expression under a given name mapping.
/// Commutative binary operations have their children sorted by canonical key,
/// and associative+commutative chains are flattened and sorted, so that e.g.
/// (a * b) * c and c * (b * a) produce the same key.
/// Used for Reynolds permutation deduplication.
let rec canonicalKey (nameMap: Map<int, string>) (expr: IRExpr) : string =
    match expr with
    | IRVar (id, _) ->
        Map.tryFind id nameMap |> Option.defaultValue (sprintf "v%d" id)
    | IRParam (name, _, _) ->
        sprintf "p:%s" name
    | IRLit lit ->
        match lit with
        | IRLitInt n -> string n
        | IRLitFloat f -> sprintf "%g" f
        | IRLitBool b -> if b then "true" else "false"
        | IRLitString s -> sprintf "\"%s\"" s
        | IRLitUnit -> "()"
    | IRBinOp (mode, op, l, r) when isCommutativeOp op && isAssociativeOp op ->
        let operands = flattenAssocOp mode op expr
        let keys = operands |> List.map (canonicalKey nameMap) |> List.sort
        sprintf "(%A/%A %s)" mode op (keys |> String.concat " ")
    | IRBinOp (mode, op, l, r) when isCommutativeOp op ->
        let lk = canonicalKey nameMap l
        let rk = canonicalKey nameMap r
        let children = [lk; rk] |> List.sort
        sprintf "(%A/%A %s %s)" mode op children.[0] children.[1]
    | IRBinOp (mode, op, l, r) ->
        sprintf "(%A/%A %s %s)" mode op (canonicalKey nameMap l) (canonicalKey nameMap r)
    | IRUnaryOp (op, inner) ->
        sprintf "(u%A %s)" op (canonicalKey nameMap inner)
    | IRApp (func, args, _) ->
        let fk = canonicalKey nameMap func
        let ak = args |> List.map (canonicalKey nameMap) |> String.concat ","
        sprintf "(call %s [%s])" fk ak
    | IRIf (cond, thn, els) ->
        sprintf "(if %s %s %s)" (canonicalKey nameMap cond) (canonicalKey nameMap thn) (canonicalKey nameMap els)
    | IRLet (id, value, body) ->
        sprintf "(let v%d=%s in %s)" id (canonicalKey nameMap value) (canonicalKey nameMap body)
    | IRTupleProj (tup, idx, _) ->
        sprintf "(proj %d %s)" idx (canonicalKey nameMap tup)
    | IRTuple elems ->
        let ek = elems |> List.map (canonicalKey nameMap) |> String.concat ","
        sprintf "(tuple %s)" ek
    | IRComplex (re, im) ->
        sprintf "(complex %s %s)" (canonicalKey nameMap re) (canonicalKey nameMap im)
    | IRFieldAccess (obj, field) ->
        sprintf "(field %s %s)" (canonicalKey nameMap obj) field
    | IRStructLit (name, fields) ->
        let fk = fields |> List.map (fun (f, e) -> sprintf "%s=%s" f (canonicalKey nameMap e)) |> String.concat ","
        sprintf "(struct %s {%s})" name fk
    | IRMatch (scrutinee, cases) ->
        let sk = canonicalKey nameMap scrutinee
        let ck = cases |> List.map (fun c -> sprintf "%A->%s" c.Pattern (canonicalKey nameMap c.Body)) |> String.concat "|"
        sprintf "(match %s [%s])" sk ck
    | IRLambda info ->
        let pk = info.Params |> List.map (fun p -> sprintf "%s:%d" p.Name p.VarId) |> String.concat ","
        sprintf "(fn [%s] %s)" pk (canonicalKey nameMap info.Body)
    | IRIndex (arr, indices, _) ->
        let ak = canonicalKey nameMap arr
        let ik = indices |> List.map (canonicalKey nameMap) |> String.concat ","
        sprintf "(idx %s [%s])" ak ik
    | IRArrayLit (elems, _) ->
        let ek = elems |> List.map (canonicalKey nameMap) |> String.concat ","
        sprintf "(arrlit [%s])" ek
    | IRExtent (arr, dim) ->
        sprintf "(extent %s %d)" (canonicalKey nameMap arr) dim
    | IRRank arr ->
        sprintf "(rank %s)" (canonicalKey nameMap arr)
    | IRPolyIndex (pack, idx) ->
        sprintf "(polyidx %s %s)" (canonicalKey nameMap pack) (canonicalKey nameMap idx)
    | IRNth -> "nth"
    | IRZero -> "zero"
    | IRSlice (arr, dim, start, stop) ->
        sprintf "(slice %s %d %s %s)" (canonicalKey nameMap arr) dim (canonicalKey nameMap start) (canonicalKey nameMap stop)
    | IRCurry (arr, idx, rank) ->
        sprintf "(curry %s %s %d)" (canonicalKey nameMap arr) (canonicalKey nameMap idx) rank
    | IRTranspose (arr, perm) ->
        sprintf "(transpose %s %A)" (canonicalKey nameMap arr) perm
    | IRAssign (lhs, rhs) ->
        sprintf "(assign %s %s)" (canonicalKey nameMap lhs) (canonicalKey nameMap rhs)
    | IRForRange (vid, lo, hi, body) ->
        sprintf "(for v%d %s %s %s)" vid (canonicalKey nameMap lo) (canonicalKey nameMap hi) (canonicalKey nameMap body)
    | _ ->
        // Combinators, compute, reynolds, etc. — won't appear in kernel bodies.
        // Use unique repr to prevent false dedup.
        sprintf "(opaque %d %A)" (expr.GetHashCode()) (expr.GetType().Name)
let permSign (perm: int list) : int =
    let mutable inv = 0
    for i in 0 .. perm.Length - 2 do
        for j in i + 1 .. perm.Length - 1 do
            if perm.[i] > perm.[j] then inv <- inv + 1
    if inv % 2 = 0 then 1 else -1

/// Reynolds kernel codegen result: C++ expression + dedup statistics.
type ReynoldsResult = {
    CppExpr: string
    TotalPerms: int
    UniqueTerms: int
}

/// Generate the kernel expression string, applying Reynolds permutation sum if needed.
/// For non-Reynolds kernels, just returns `exprToCpp nameMap kernelExpr`.
/// For Reynolds kernels, generates the sum over all permutations of the kernel parameters,
/// deduplicating structurally equivalent permutations via canonical keys.

let genKernelExprWithReynolds
    (kernelExpr: IRExpr)
    (kernelParams: IRParam list)
    (hasReynolds: bool)
    (isAntisymmetric: bool)
    (nameMap: Map<int, string>)
    (paramFinalNames: Map<int, string>) : ReynoldsResult =
    if hasReynolds && kernelParams.Length >= 2 then
        let n = kernelParams.Length
        let paramCppNames =
            kernelParams |> List.map (fun p ->
                Map.tryFind p.VarId paramFinalNames
                |> Option.defaultValue (sprintf "__p%d" p.VarId))
        let allPerms = permutations [0 .. n - 1]
        let totalPerms = allPerms.Length
        // For each permutation, generate:
        //   - canonical key (for grouping — commutative ops normalized)
        //   - C++ expression (for actual emission)
        //   - sign
        let permData =
            allPerms |> List.map (fun perm ->
                let permNameMap =
                    kernelParams |> List.mapi (fun i p ->
                        (p.VarId, paramCppNames.[perm.[i]]))
                    |> List.fold (fun acc (vid, name) -> Map.add vid name acc) nameMap
                let sign = permSign perm
                let key = canonicalKey permNameMap kernelExpr
                let cppExpr = exprToCpp permNameMap kernelExpr
                (key, sign, cppExpr))
        // Group by canonical key to deduplicate equivalent permutations.
        // For symmetric Reynolds: identical keys accumulate multiplicity.
        // For antisymmetric Reynolds: identical keys accumulate net sign (may cancel to 0).
        let grouped =
            permData
            |> List.groupBy (fun (key, _, _) -> key)
            |> List.choose (fun (_key, group) ->
                let representativeCpp = let (_, _, cpp) = group.Head in cpp
                if isAntisymmetric then
                    let netSign = group |> List.sumBy (fun (_, s, _) -> s)
                    if netSign = 0 then None
                    else Some (netSign, representativeCpp)
                else
                    Some (group.Length, representativeCpp))
        let uniqueTerms = grouped.Length
        // Build the sum expression with multiplicity coefficients
        let formatTerm coeff expr =
            match isAntisymmetric with
            | true ->
                if abs coeff = 1 then
                    if coeff > 0 then expr else sprintf "(-%s)" expr
                else sprintf "(%d * %s)" coeff expr
            | false ->
                if coeff = 1 then expr else sprintf "(%d * %s)" coeff expr
        let sumExpr =
            grouped |> List.mapi (fun i (coeff, expr) ->
                let term = formatTerm coeff expr
                if i = 0 then term
                elif isAntisymmetric && coeff < 0 then
                    sprintf " - %s" (formatTerm (abs coeff) expr)
                else sprintf " + %s" term)
            |> String.concat ""
        let cppExpr =
            if grouped.IsEmpty then
                "0.0"  // Complete cancellation (e.g. antisymmetrization of symmetric kernel)
            else
                sprintf "(%s)" sumExpr
        { CppExpr = cppExpr; TotalPerms = totalPerms; UniqueTerms = uniqueTerms }
    else
        { CppExpr = exprToCpp nameMap kernelExpr; TotalPerms = 1; UniqueTerms = 1 }

let genLoopNest (codeGen: LoopNestCodeGen) (outerNames: Map<int, string>) (indent: int) : string list =
    let ind n = String.replicate n "    "
    let mutable lines = []
    let mutable depth = indent
    
    // Track current peeled name for each array position
    let mutable currentNames : Map<int, string> = 
        codeGen.InputArrayNames |> List.mapi (fun i n -> (i, n)) |> Map.ofList
    
    // Track final peeled name for each param VarId (for kernel body substitution)
    let mutable paramFinalNames : Map<int, string> = Map.empty
    
    // Generate nested loops with element bindings
    for binding in codeGen.Bindings do
        // Generate the loop header
        lines <- lines @ [ind depth + genForLoopHeader binding]
        depth <- depth + 1
        
        // Generate element bindings for all arrays at this level
        for elem in binding.Elements do
            let currentName = 
                Map.tryFind elem.ArrayPosition currentNames 
                |> Option.defaultValue elem.ArrayName
            let (code, newName) = genElementBindingNew binding elem currentName
            lines <- lines @ [ind depth + code]
            currentNames <- Map.add elem.ArrayPosition newName currentNames
            // Record mapping for kernel body
            match elem.Virtual with
            | VirtualRange _ | VirtualReverse ->
                paramFinalNames <- Map.add elem.ParamVarId elem.ParamName paramFinalNames
            | RealArray ->
                paramFinalNames <- Map.add elem.ParamVarId newName paramFinalNames
    
    // Build name map for kernel body from final peeled names
    // Start from outer scope, then overlay kernel params (kernel params take priority)
    let nameMap = paramFinalNames |> Map.fold (fun acc k v -> Map.add k v acc) outerNames
    let nameMap =
        codeGen.Captures
        |> List.fold (fun acc c -> Map.add c.Id c.Name acc) nameMap
    
    // Generate kernel assignment (with Reynolds permutation sum if applicable).
    // The shape of the assignment depends on output type:
    //   - Array output: indexed slot assignment, `name[i][j]... = kernelBody;`.
    //     Each loop binding contributes one bracketed index. This is the standard
    //     case for `method_for(...) <@> kernel` returning a tensor.
    //   - Scalar output: sum accumulation, `name += kernelBody;`. The loop nest
    //     still iterates over input dimensions, but the kernel result is summed
    //     into a scalar accumulator declared by the caller (genApplyCombinator).
    //     Used when the function's return type is scalar even though the
    //     `<@>` kernel produces a per-iteration value (the Cartesian-sum-reduce
    //     pattern that the old hand-written IIFE handled for the 2-array case).
    let outputIdx =
        match codeGen.OutputType with
        | IRTScalar _ -> ""
        | _ ->
            codeGen.Bindings
            |> List.map (fun b -> sprintf "[%s]" b.IndexName)
            |> String.concat ""
    let assignOp =
        match codeGen.OutputType with
        | IRTScalar _ -> "+="
        | _ -> "="

    let reynoldsResult = genKernelExprWithReynolds codeGen.KernelExpr codeGen.KernelParams codeGen.HasReynolds codeGen.IsAntisymmetric nameMap paramFinalNames
    if codeGen.HasReynolds && reynoldsResult.UniqueTerms < reynoldsResult.TotalPerms then
        lines <- lines @ [ind depth + sprintf "// Reynolds: %d/%d perms unique (dedup %dx)" reynoldsResult.UniqueTerms reynoldsResult.TotalPerms (reynoldsResult.TotalPerms / max 1 reynoldsResult.UniqueTerms)]
    lines <- lines @ [ind depth + sprintf "%s%s %s %s;" codeGen.OutputName outputIdx assignOp reynoldsResult.CppExpr]

    // Close all loops
    for _ in codeGen.Bindings do
        depth <- depth - 1
        lines <- lines @ [ind depth + "}"]

    lines


// ============================================================================
// Symmetry Vector Generation
// ============================================================================

/// Generate C++ static constexpr array for symmetry vector
let genSymmVecDecl (name: string) (symmVec: int list) : string =
    if symmVec.IsEmpty then
        sprintf "static constexpr const size_t* %s = nullptr;" name
    else
        let values = symmVec |> List.map string |> String.concat ", "
        sprintf "static constexpr const size_t %s[%d] = {%s};" name symmVec.Length values


// ============================================================================
// Array Allocation Generation
// ============================================================================


// ============================================================================
// Function Template Generation
// ============================================================================

/// Generate template parameter list for a combinator function
let genTemplateParams (inputCount: int) (hasOutput: bool) : string =
    let inputs = 
        [0 .. inputCount - 1] 
        |> List.collect (fun i -> 
            [sprintf "typename ITYPE%d" (i+1)
             sprintf "const size_t IRANK%d" (i+1)
             sprintf "const size_t* ISYM%d" (i+1)])
    let output =
        if hasOutput then
            ["typename OTYPE"; "const size_t ORANK"; "const size_t* OSYM"]
        else []
    inputs @ output |> String.concat ", "

/// Generate function parameter list
let genFunctionParams (inputNames: string list) (outputName: string) : string =
    let inputs =
        inputNames |> List.mapi (fun i name ->
            [sprintf "typename promote<ITYPE%d, IRANK%d>::type %s" (i+1) (i+1) name
             sprintf "const size_t %s_extents[IRANK%d]" name (i+1)])
        |> List.concat
    let output =
        [sprintf "typename promote<OTYPE, ORANK>::type %s" outputName
         sprintf "const size_t %s_extents[ORANK]" outputName]
    inputs @ output |> String.concat ",\n    "

// ============================================================================
// Complete Function Generation
// ============================================================================

/// Generate a complete C++ function from LoopNestCodeGen
let genFunction (codeGen: LoopNestCodeGen) (funcName: string) : string list =
    let inputCount = codeGen.InputArrayNames.Length
    
    // Template declaration
    let templateParams = genTemplateParams inputCount true
    let funcParams = genFunctionParams codeGen.InputArrayNames codeGen.OutputName
    
    // Function signature
    let signature = 
        [sprintf "template<%s>" templateParams
         sprintf "void %s(" funcName
         sprintf "    %s) {" funcParams]
    
    // Body with loop nest
    let body = genLoopNest codeGen Map.empty 1
    
    // Close
    let close = ["}"]
    
    signature @ body @ close

/// Generate header includes
let genIncludes () : string list =
    ["#include <cstdint>"
     "#include <cstdlib>"  // for rand()
     "#include <cmath>"
     "#include <complex>"
     "#include <functional>"
     "#include <tuple>"
     "#include <variant>"
     "#include <string>"
     "#include <iostream>"
     "#include <iomanip>"
     "#include <chrono>"
     "#include <algorithm>"  // std::stable_sort (used by sort())
     "#include <numeric>"    // std::iota (used by sort())
     "#include <unordered_map>"  // group_keys Case 3 (dynamic ngroups via hash discovery)
     "// Note: OpenMP disabled for portability"
     "// #include <omp.h>"
     "#include \"nested_array_utilities.cpp\""
     "using namespace nested_array_utilities;"
     "using std::cout;"
     "using std::endl;"
     ""
     "#define TIME std::chrono::high_resolution_clock::now()"
     "#define TIME_DIFF std::chrono::duration_cast<std::chrono::nanoseconds>(end - start).count()"
     ""]

// ============================================================================
// C++ runtime headers
// ============================================================================
//
// The Blade C++ runtime lives in cpp/*.hpp at the source root and is read
// from disk at codegen time, not embedded in the F# binary as string
// literals. Blade.fsproj copies the cpp/ directory into the build output
// via <CopyToOutputDirectory>, so AppContext.BaseDirectory + "cpp" resolves
// to the correct location regardless of where dotnet run is invoked from.
//
// The generated C++ test output picks up the headers when Main.fs writes
// them into each test's output directory alongside the .cpp file (the
// existing pattern; see Main.fs's writes of headerFile / arrayTypesHeaderFile).
// g++ then resolves `#include "nested_array_utilities.hpp"` relative to
// the .cpp file's directory — no -I flag needed, no build-output paths
// leaked into the C++ compile line.

/// Resolve the path of a runtime header file shipped in the cpp/ directory
/// next to the compiler binary. Used by both genRuntimeHeader and
/// genRuntimeArrayTypesHeader; centralized here so the AppContext.BaseDirectory
/// and "cpp" subpath assumptions live in one place.
let private cppRuntimeHeaderPath (filename: string) : string =
    System.IO.Path.Combine(System.AppContext.BaseDirectory, "cpp", filename)

/// Read a Blade C++ runtime header from disk. Fails loudly if the build
/// hasn't copied cpp/ into the output directory — this is a configuration
/// error rather than a compiler bug, so the message points at .fsproj.
let private readCppRuntimeHeader (filename: string) : string =
    let path = cppRuntimeHeaderPath filename
    if not (System.IO.File.Exists path) then
        failwithf
            "C++ runtime header not found at: %s\n\
             The build should copy cpp/%s into the output directory.\n\
             Check that Blade.fsproj contains a <None Include=\"cpp/%s\">\n\
             item with <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>."
            path filename filename
    System.IO.File.ReadAllText path

/// Generate the runtime header file content (read from cpp/nested_array_utilities.hpp).
/// Main.fs writes the result alongside each test's generated .cpp so
/// `#include "nested_array_utilities.hpp"` resolves at g++ time.
let genRuntimeHeader () : string =
    readCppRuntimeHeader "nested_array_utilities.hpp"

/// Generate the array-types runtime header (read from cpp/nested_array_types.hpp).
/// Phase D / v24 wrapper structs (Array<T,N>, Ragged<T>). Same emission
/// pattern as genRuntimeHeader.
let genRuntimeArrayTypesHeader () : string =
    readCppRuntimeHeader "nested_array_types.hpp"

/// Generate includes that reference external header
let genIncludesExternal () : string list =
    ["#include <cstdint>"
     "#include <cstdlib>"
     "#include <cmath>"
     "#include <complex>"
     "#include <functional>"
     "#include <tuple>"
     "#include <variant>"
     "#include <string>"
     "#include <iostream>"
     "#include <iomanip>"
     "#include <chrono>"
     "#include <set>"
     "#include <algorithm>"  // std::stable_sort (used by sort())
     "#include <numeric>"    // std::iota (used by sort())
     "#include <unordered_map>"  // group_keys Case 3 (dynamic ngroups via hash discovery)
     "#include <omp.h>"
     "#include \"nested_array_utilities.hpp\""
     "#include \"nested_array_types.hpp\""
     "using std::cout;"
     "using std::endl;"
     ""
     "#define TIME std::chrono::high_resolution_clock::now()"
     "#define TIME_DIFF std::chrono::duration_cast<std::chrono::nanoseconds>(end - start).count()"
     ""]


// ============================================================================
// Full Program Generation
// ============================================================================

/// Generate a complete C++ program from multiple LoopNestCodeGen
let genProgram (functions: (string * LoopNestCodeGen) list) : string =
    let includes = genIncludes ()
    
    let funcCode = 
        functions 
        |> List.collect (fun (name, cg) -> genFunction cg name @ [""])
    
    (includes @ funcCode) |> String.concat "\n"

// ============================================================================
// Array Literal Generation
// ============================================================================

/// Extract float values from array literal for initialization
let rec extractLiteralValues (expr: IRExpr) : float list =
    match expr with
    | IRLit (IRLitFloat f) -> [f]
    | IRLit (IRLitInt n) -> [float n]
    | IRLit (IRLitBool b) -> [if b then 1.0 else 0.0]
    | IRUnaryOp (IRNeg, IRLit (IRLitFloat f)) -> [-f]
    | IRUnaryOp (IRNeg, IRLit (IRLitInt n)) -> [float -n]
    | IRArrayLit (elements, _) -> elements |> List.collect extractLiteralValues
    | _ -> []

/// Compute dimensions of an array literal
let rec computeArrayDims (expr: IRExpr) : int list =
    match expr with
    | IRArrayLit (elements, _) ->
        let thisLen = elements.Length
        match elements with
        | first :: _ -> thisLen :: computeArrayDims first
        | [] -> [0]
    | _ -> []

/// Compute per-row lengths for a ragged literal. Returns the inner sub-array
/// length for each outer entry. For [[1,2,3], [4,5], [6,7,8,9]] returns [3; 2; 4].
/// For non-ragged or non-nested input, returns the empty list.
let computeRaggedRowLengths (elements: IRExpr list) : int list =
    elements |> List.choose (fun e ->
        match e with
        | IRArrayLit (inner, _) -> Some inner.Length
        | _ -> None)

// ============================================================================
// Allocation Tracking (preparatory infrastructure for future garbage collection)
// ============================================================================
//
// Blade currently leaks all heap allocations on program exit (programs are
// compute-and-print-and-exit; OS reclaims memory). A future round will add
// scope-aware allocation tracking so that arrays allocated inside a function
// can be freed when the function returns (or its enclosing scope ends).
//
// To keep the door open without doing the full refactor now, new allocation
// sites should route through `emitHeapAlloc` and `emitStackAlloc` helpers
// below. These currently emit plain C++ allocation but will eventually:
//   1. Record the allocation in a per-scope arena.
//   2. Emit a `delete[]` or arena-cleanup call when the scope exits.
//   3. Distinguish "owned" from "borrowed" arrays for return-value handling.
//
// Existing allocation sites (rectangular `genArrayLiteral`, `mask`, `sort`,
// `group_keys`, etc.) currently emit `new`/`allocate<>` directly. They should
// be migrated to these helpers in a follow-up round; the current code path
// is correct (just leaky) and migration can be incremental.

/// Emit a heap allocation. Currently just emits `new T[n]`. The wrapper exists
/// so a future garbage-collection scheme can record the allocation without
/// touching every call site.
let emitHeapAlloc (cppType: string) (sizeExpr: string) : string =
    sprintf "new %s[%s]" cppType sizeExpr

/// Emit a stack-allocated array (fixed-size, lives in enclosing scope).
/// Returns the C++ declaration text. The name and size are baked into the
/// declaration; this helper exists for symmetry with emitHeapAlloc and so a
/// future scope tracker can detect stack-allocated arrays as a separate
/// category from heap arrays.
let emitStackAlloc (cppType: string) (varName: string) (sizeExpr: string) : string =
    sprintf "%s %s[%s];" cppType varName sizeExpr

/// Generate code to allocate and initialize an array from literal values
let genArrayLiteral (ctx: CodeGenContext) (varName: string) (elements: IRExpr list) (arrType: IRArrayType) : string list =
    let ind = indentStr ctx
    let elemType = elemTypeToCpp arrType.ElemType
    let rank = arrayRank arrType
    
    // Ragged literal path: detected when the array type has a RaggedIdx-tagged
    // inner index. Emits offsets table, flat backing buffer, and a row-pointer
    // table, all populated from the literal's structure.
    //
    // Storage layout (current): all stack-allocated. The flat backing buffer
    // and row-pointer table live in the enclosing function's scope. This
    // works when the ragged literal doesn't need to outlive its declaration
    // scope (e.g., construct-print-exit in main()). If a ragged literal is
    // ever returned from a function or stored into a longer-lived binding,
    // this path needs upgrading to heap allocation:
    //   - new T[total] for the flat backing
    //   - new T*[n] for the row pointers (each pointing into the flat buffer)
    // That upgrade is local to this case and doesn't affect the type system.
    // DepIdx-annotated literal path: detected when the array type has a
    // DepIdx-inner-tagged index. The lens table comes from evaluating the
    // formula stored in the inner record's Extent (e.g., `Idx<3 - i>`) for
    // each i; the literal's row data is verified against those lens. Once
    // computed, the runtime layout is identical to the ragged path
    // (`_lens`/`_offsets`/flat backing/row pointers) so all downstream
    // codegen — iteration, kernel-param peel, function-param calling
    // convention, print — uses the same machinery.
    //
    // Static-formula limitation: evalDepIdxExtent reduces arithmetic with
    // the outer's IRVar substituted for `i`. Formulas that reference free
    // variables or runtime values fall through the None case and surface as
    // a codegen error here. Runtime-extent formulas are deferred work.
    if isDepIdxArrayType arrType then
        // Find the outer record (its IRId is the one substituted for `i` in
        // the inner extent) and the inner record (carries the formula).
        let outerOpt =
            arrType.IndexTypes |> List.tryFind (fun idx -> idx.Tag = Some "__depidx_outer")
        let innerOpt =
            arrType.IndexTypes |> List.tryFind (fun idx -> idx.Tag = Some "__depidx_inner")
        match outerOpt, innerOpt with
        | Some outer, Some inner ->
            let outerExtentOpt = tryEvalIntIR outer.Extent
            match outerExtentOpt with
            | None ->
                [sprintf "%s#error \"Blade codegen: DepIdx outer extent is not a compile-time integer for binding '%s'\""
                    ind varName]
            | Some n ->
                // Evaluate the inner formula for each i in [0..n).
                let lenResults =
                    [0 .. (int n) - 1]
                    |> List.map (fun i -> evalDepIdxExtent outer.Id i inner.Extent)
                if lenResults |> List.exists Option.isNone then
                    [sprintf "%s#error \"Blade codegen: DepIdx inner extent formula not statically evaluable for binding '%s' (runtime-extent formulas are not yet supported)\""
                        ind varName]
                else
                    let lens = lenResults |> List.map Option.get
                    // Verify literal row counts match the formula-computed lens.
                    let actualRowLengths = computeRaggedRowLengths elements
                    let mismatch =
                        actualRowLengths.Length <> lens.Length ||
                        List.zip actualRowLengths lens |> List.exists (fun (a, b) -> a <> b)
                    if mismatch then
                        let expected = lens |> List.map string |> String.concat ", "
                        let actual = actualRowLengths |> List.map string |> String.concat ", "
                        [sprintf "%s#error \"Blade codegen: DepIdx literal row lengths [%s] do not match formula-computed lens [%s] for binding '%s'\""
                            ind actual expected varName]
                    else
                        let total = lens |> List.sum
                        let allValues = extractLiteralValues (IRArrayLit (elements, arrType))
                        if allValues.Length <> total then
                            [sprintf "%s#error \"Blade codegen: DepIdx literal value count (%d) does not match sum of formula-computed lens (%d) for binding '%s'\""
                                ind allValues.Length total varName]
                        else
                            // Layout is identical to ragged from here on.
                            let nRows = lens.Length
                            let lensList = lens |> List.map string |> String.concat ", "
                            let offsets = lens |> List.scan (fun acc len -> acc + len) 0
                            let offsetsList = offsets |> List.map string |> String.concat ", "
                            let flatValues = allValues |> List.map (sprintf "%g") |> String.concat ", "
                            let extentsDecl = sprintf "%sstatic constexpr const size_t %s_extents[1] = {%d};" ind varName nRows
                            let lensDecl = sprintf "%sstatic constexpr const size_t %s_lens[%d] = {%s};" ind varName nRows lensList
                            let offsetsDecl = sprintf "%sstatic constexpr const size_t %s_offsets[%d] = {%s};" ind varName (nRows + 1) offsetsList
                            let flatDecl = sprintf "%s%s %s__flat[%d] = {%s};" ind elemType varName total flatValues
                            // Row pointer array (stack-allocated). The Ragged
                            // wrapper holds a pointer to this array.
                            let rowPtrsDecl = sprintf "%s%s* %s__rows[%d];" ind elemType varName nRows
                            let rowPtrsInit =
                                [ sprintf "%sfor (size_t __ri = 0; __ri < %d; __ri++) {" ind nRows
                                  // Reads from the static-constexpr global declared above; the wrapper isn't yet constructed.
                                  sprintf "%s    %s__rows[__ri] = &%s__flat[%s_offsets[__ri]];" ind varName varName varName
                                  sprintf "%s}" ind ]
                            // Wrap into Ragged<T>: data + extents + lens + offsets.
                            let wrapperDecl = sprintf "%sRagged<%s> %s = { %s__rows, %s_extents, %s_lens, %s_offsets };" 
                                                ind elemType varName varName varName varName varName
                            [extentsDecl; lensDecl; offsetsDecl; flatDecl; rowPtrsDecl] @ rowPtrsInit @ [wrapperDecl]
        | _ ->
            [sprintf "%s#error \"Blade codegen: DepIdx array type missing outer or inner record for binding '%s' (typechecker bug)\""
                ind varName]
    elif isRaggedArrayType arrType then
        let rowLengths = computeRaggedRowLengths elements
        let n = rowLengths.Length
        let total = rowLengths |> List.sum
        // Flat list of all element values, in row-major order
        let allValues = extractLiteralValues (IRArrayLit (elements, arrType))
        if allValues.Length <> total then
            // Sanity check: number of leaf values must match sum of row lengths.
            [sprintf "%s#error \"Blade codegen: ragged literal value count (%d) does not match sum of row lengths (%d) for binding '%s'\""
                ind allValues.Length total varName]
        else
            let lensList = rowLengths |> List.map string |> String.concat ", "
            let offsets =
                rowLengths |> List.scan (fun acc len -> acc + len) 0
            let offsetsList = offsets |> List.map string |> String.concat ", "
            let flatValues = allValues |> List.map (sprintf "%g") |> String.concat ", "
            let extentsDecl = sprintf "%sstatic constexpr const size_t %s_extents[1] = {%d};" ind varName n
            let lensDecl = sprintf "%sstatic constexpr const size_t %s_lens[%d] = {%s};" ind varName n lensList
            let offsetsDecl = sprintf "%sstatic constexpr const size_t %s_offsets[%d] = {%s};" ind varName (n + 1) offsetsList
            let flatDecl = sprintf "%s%s %s__flat[%d] = {%s};" ind elemType varName total flatValues
            // Row pointer array (stack-allocated, separate name from the
            // wrapper so they don't collide). The Ragged<T> wrapper bundles
            // it with the lens/offsets/extents.
            let rowPtrsDecl = sprintf "%s%s* %s__rows[%d];" ind elemType varName n
            let rowPtrsInit =
                [ sprintf "%sfor (size_t __ri = 0; __ri < %d; __ri++) {" ind n
                  // Reads from the static-constexpr `<name>_offsets` global
                  // declared just above. The Ragged wrapper itself isn't yet
                  // in scope — it's constructed AFTER this loop runs.
                  sprintf "%s    %s__rows[__ri] = &%s__flat[%s_offsets[__ri]];" ind varName varName varName
                  sprintf "%s}" ind ]
            let wrapperDecl = sprintf "%sRagged<%s> %s = { %s__rows, %s_extents, %s_lens, %s_offsets };" 
                                ind elemType varName varName varName varName varName
            [extentsDecl; lensDecl; offsetsDecl; flatDecl; rowPtrsDecl] @ rowPtrsInit @ [wrapperDecl]
    else
        // Rectangular path: existing behavior.
        let dims = computeArrayDims (IRArrayLit (elements, arrType))
        if dims.IsEmpty then
            [sprintf "%s// Empty array literal" ind]
        else
            // Generate extents declaration
            let extentsValues = dims |> List.map string |> String.concat ", "
            let extentsDecl = sprintf "%sstatic constexpr const size_t %s_extents[%d] = {%s};" 
                                ind varName rank extentsValues
            
            // Generate allocation as Array<T,N> wrapper. Single brace-init
            // bundles the data pointer (from allocate<>) with the extents
            // pointer (the static-constexpr global emitted above).
            let allocDecl = sprintf "%sArray<%s, %d> %s = { allocate<typename promote<%s, %d>::type, nullptr>(%s_extents), %s_extents };" 
                                ind elemType rank varName elemType rank varName varName
            
            // Generate initialization
            let values = extractLiteralValues (IRArrayLit (elements, arrType))
            
            // Decide between fast scalar path and per-element expression path.
            // The fast path uses `%g` formatting and is correct only when all
            // elements extracted to a complete float list (length matches the
            // dim product). Struct literals, computed values, or any element
            // that extractLiteralValues returns nothing for falls into the
            // expression path, which renders via exprToCpp (handles
            // IRStructLit, IRApp, etc. uniformly).
            //
            // Pre-Phase-D, the fast path was the only path: when any element
            // wasn't a scalar literal, no init was emitted at all (silent
            // miscompile reading uninitialized memory).
            let totalExpected = dims |> List.fold (*) 1
            let useFastScalarPath = values.Length = totalExpected && totalExpected > 0
            
            // Generalize over rank N. The two paths diverge only in how each
            // leaf is rendered:
            //   - fast path: %g-formatted floats from extractLiteralValues,
            //     in row-major order
            //   - per-element path: walk the nested IRArrayLit, render each
            //     leaf via exprToCpp (covers struct lits, computed values)
            // Both produce assignments of the form `name[i₀][i₁]...[iₙ₋₁] = E;`.
            //
            // Pattern follows extractLiteralValues / computeArrayDims — recurse
            // through IRArrayLit nesting, treating non-IRArrayLit nodes as
            // leaves. The old rank-1 / rank-2 / TODO-for-higher dispatch is
            // gone; rank-3+ now works at parity with rank-1 and rank-2.
            let rec enumerateIndexPaths (ds: int list) : int list list =
                match ds with
                | [] -> [[]]
                | n :: rest ->
                    let tails = enumerateIndexPaths rest
                    [for i in 0 .. n - 1 do
                        for t in tails do
                            yield i :: t]
            let rec walkLeaves (idxPath: int list) (e: IRExpr) : (int list * IRExpr) list =
                match e with
                | IRArrayLit (children, _) ->
                    children
                    |> List.mapi (fun i c -> walkLeaves (idxPath @ [i]) c)
                    |> List.concat
                | leaf -> [(idxPath, leaf)]
            let formatIndexPath (path: int list) : string =
                path |> List.map (sprintf "[%d]") |> String.concat ""
            
            let initCode =
                if useFastScalarPath then
                    // Row-major enumeration of (i₀,…,iₙ₋₁) tuples zipped with
                    // the flat value list. extractLiteralValues already walks
                    // in row-major order, so the alignment is exact.
                    let paths = enumerateIndexPaths dims
                    List.zip paths values |> List.map (fun (path, v) ->
                        sprintf "%s%s%s = %g;" ind varName (formatIndexPath path) v)
                else
                    // Per-element path: walk the nested IRArrayLit. Index path
                    // accumulates as we descend; leaves render via exprToCpp.
                    walkLeaves [] (IRArrayLit (elements, arrType))
                    |> List.map (fun (path, leaf) ->
                        sprintf "%s%s%s = %s;" ind varName (formatIndexPath path) (exprToCpp ctx.VarNames leaf))
            
            [extentsDecl; allocDecl] @ initCode

/// Generate code for a scalar binding
let genScalarBinding (ctx: CodeGenContext) (name: string) (value: IRExpr) (ty: IRType) : string list =
    let ind = indentStr ctx
    // Defense for upstream type-inference cache misses: when the binding's
    // declared type is IRTInfer or IRTUnit but the value isn't actually
    // unit-valued, re-derive the type from the value via inferExprType.
    // This catches cases where the lift pass's structFieldsCache may have
    // missed an IRFieldAccess and labeled the binding IRTUnit.
    let resolvedTy = 
        match ty with 
        | IRTInfer _ -> inferExprType value 
        | IRTUnit when not (isUnitExpr value) ->
            let inferred = inferExprType value
            if inferred = IRTUnit then ty else inferred
        | t -> t
    // The v24d-1 auto-fallback (which emitted `auto <name> = <expr>;` when
    // a binding's resolvedTy was IRTUnit and the RHS was a shape-bearing
    // expression like IRMask/IRSort/IRVar/IRFieldAccess/IRIntersect/IRUnion)
    // was removed after probe testing confirmed those RHS shapes always
    // resolve to a non-IRTUnit type by this point. If a future regression
    // ever reaches this branch with a shape-bearing IRTUnit binding, the
    // expression-statement form below would produce invalid C++ — that's
    // intentional: a regression should surface as a compile error rather
    // than be papered over with auto-deduction.
    match resolvedTy with
    | IRTUnit ->
        if isUnitExpr value then 
            []
        else
            // Genuinely unit-valued: emit as expression statement
            let valueStr = exprToCppCtx ctx value
            [sprintf "%s%s;" ind valueStr]
    | _ ->
        // v24b: array-typed bindings render as Array<T,N> / Ragged<T>
        // wrappers when the RHS itself produces a wrapper. For RHSes that
        // produce bare pointers (IRIndex peeling a sub-array, IRApp where
        // the function still returns T* per v24c-deferred signatures),
        // render bare to avoid a brace-init mismatch.
        //
        // Producers that yield wrappers (post-v24b): IRFieldAccess,
        // IRArrayLit (via genArrayLiteral, but those go through a
        // different binding path), IRVar that resolves to a wrapper,
        // IRMask/IRSort/IRIntersect/IRUnion (via materializeInlineForm).
        // IRApp is also included — function calls returning IRTArray emit
        // `Array<T, N>` at the function-decl level (genFuncDef:4181), so
        // their let-bound results must use the same wrapper type, not the
        // raw `promote<T, N>::type` storage pointer that would lose
        // `.extents` and silently decay to the data pointer.
        let producesWrapper =
            match value with
            | IRFieldAccess _ -> true
            | IRVar _ -> true                // assume wrapper (most producers migrated)
            | IRMask _ | IRSort _ | IRIntersect _ | IRUnion _ -> true
            | IRApp _ -> true                // function-call returns wrapped Array
            | _ -> false
        let cppType =
            match resolvedTy with
            | ArrayElem arr when producesWrapper && (isRaggedArrayType arr || isDepIdxArrayType arr) ->
                sprintf "Ragged<%s>" (elemTypeToCpp arr.ElemType)
            | ArrayElem arr when producesWrapper ->
                sprintf "Array<%s, %d>" (elemTypeToCpp arr.ElemType) (arrayRank arr)
            | _ -> irTypeToCpp resolvedTy
        let valueStr = exprToCppCtx ctx value
        [sprintf "%s%s %s = %s;" ind cppType name valueStr]

// ============================================================================
// Loop Application Code Generation
// ============================================================================

/// Build a simple (no symmetry) ApplyInfo for applying a unary kernel to arrays.
/// Used by >>@ and @>> to construct stage-2 pipeline applications.
let defaultIndexType () = { Id = 0; Arity = 1; Extent = IRLit (IRLitInt 0); Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] }
/// Build a default IRArrayType. Phase B2: takes an IRType for the elem type.
let defaultArrayType (et: IRType) = { ElemType = et; IndexTypes = [defaultIndexType ()]; IsVirtual = false; Identity = None }

let buildSimpleApplyInfo (arrays: IRExpr list) (kernel: IRExpr) (outputType: IRType) : ApplyInfo =
    let arrayTypes = arrays |> List.map (fun a -> 
        match inferExprType a with 
        | ArrayElem arr -> arr 
        | _ -> defaultArrayType (IRTScalar ETFloat64))
    let identities = arrays |> List.mapi (fun i _ -> AIDLiteral i)
    let sDims = arrayTypes |> List.map arrayRank
    let totalSDims = List.sum sDims
    {
        Loop = IRMethodFor {
            Arrays = arrays
            Identities = identities
            ArrayTypes = arrayTypes
            SDimsPerArray = sDims
            TotalSDims = totalSDims
            SharedIndexType = None
        }
        Kernel = kernel
        Arrays = arrays
        Identities = identities
        ArrayTypes = arrayTypes
        SharedIndexType = None
        SymcomStates = List.replicate totalSDims SCNeither
        TriangularLevels = List.replicate totalSDims false
        SDimsPerArray = sDims
        KernelInputRanks = List.replicate (List.length arrays) 0
        KernelOutputRank = 0
        KernelTDims = []
        SpeedupFactor = 1L
        ReynoldsSpeedup = 1L
        HasReynolds = false
        OutputType = outputType
        IsCoIteration = false
    }

/// Generate the complete code for a combinator application (L <@> f)
let genApplyCombinator (ctx: CodeGenContext) (name: string) (info: ApplyInfo) (builder: IRBuilder) : string list =
    let ind = indentStr ctx
    
    // ============================================================================
    // Special case: ragged peel for grouped arrays.
    // ============================================================================
    // Triggered when method_for is applied to a single grouped array
    // (recognized by Tag = "__group_outer" on its first index type).
    // This bypasses the generic loop-nest builder for two reasons:
    //   1. The inner extent is ragged (per group, from gk__offsets).
    //   2. The kernel param ('g' in lambda(g) -> g(0)) has unresolved type at
    //      typecheck time, so kernelInputRanks=[0] and the loop builder would
    //      otherwise try to iterate both dims of the rank-2 grouped array.
    // We also rewrite IRApp(g, args) -> IRIndex(g, args) on the kernel body so
    // that 'g(0)' renders as 'g_local[0]' rather than 'g_local(0)'.
    // Generalized ragged peel: handles two source patterns
    //   (a) group_by output: outer index tagged __group_outer; lengths derived
    //       from the corresponding group_keys array via ctx.GroupedArrays.
    //   (b) ragged literal: inner index tagged __raggedidx_inline; lengths in
    //       arr_lens, offsets in arr_offsets (emitted by genArrayLiteral).
    // In both cases, the same loop structure works: outer loop over rows,
    // sub-array binding for the kernel param, kernel body executed per row.
    let tryRaggedPeel () : string list option =
        if info.Arrays.Length <> 1 then None
        else
            let arrType = info.ArrayTypes.[0]
            let isGroupedOuter =
                match arrType.IndexTypes with
                | outer :: _ -> outer.Tag = Some "__group_outer"
                | _ -> false
            // Detect ragged-or-DepIdx input: at least 2 IndexTypes, and the
            // *inner* (any non-first) carries any of the ragged tags or the
            // DepIdx-inner tag. Covers ragged literals (__raggedidx_inline),
            // function-param closed form (__raggedidx), function-param opaque
            // form (__raggedidx_opaque), and DepIdx-allocated arrays
            // (__depidx_inner — runtime layout matches ragged once allocated).
            // All want the peel codegen path: outer iteration over rows,
            // sub-array binding for the kernel.
            let isRaggedLiteral =
                arrType.IndexTypes.Length >= 2 &&
                arrType.IndexTypes |> List.skip 1 |> List.exists (fun idx ->
                    match idx.Tag with
                    | Some "__raggedidx_inline"
                    | Some "__raggedidx"
                    | Some "__raggedidx_opaque"
                    | Some "__depidx_inner" -> true
                    | _ -> false)
            if not isGroupedOuter && not isRaggedLiteral then None
            else
                let arrExpr = info.Arrays.[0]
                let arrName = exprToCppCtx ctx arrExpr
                // Resolve "lengths source" — where to read each row's length
                // and offset. For group_by, this is the group_keys metadata
                // (gk__offsets, gk__ngroups). For ragged literals, it's the
                // array's own _offsets/_lens emitted at construction.
                let lengthsSource =
                    if isGroupedOuter then
                        match Map.tryFind arrName ctx.GroupedArrays with
                        | Some gkName ->
                            Some (sprintf "%s__ngroups" gkName,
                                  sprintf "%s__offsets[__g + 1] - %s__offsets[__g]" gkName gkName)
                        | None -> None
                    else
                        // Ragged literal: lens/extents are co-emitted with
                        // the array. The outer count is in arr_extents[0];
                        // each row's length is in arr_lens[__g].
                        Some (sprintf "%s.extents[0]" arrName,
                              sprintf "%s.lens[__g]" arrName)
                match lengthsSource with
                | None -> None
                | Some (ngroupsExpr, perRowLenExpr) ->
                    match info.Kernel with
                    | IRLambda lInfo when lInfo.Params.Length = 1 ->
                        let param = lInfo.Params.[0]
                        // Rewrite g(args) -> IRIndex(g, args, None) in the kernel body.
                        let rewriter e =
                            match e with
                            | IRApp (IRVar (id, ty), args, _) when id = param.VarId ->
                                IRIndex (IRVar (id, ty), args, None)
                            | _ -> e
                        let body = mapIRExpr rewriter lInfo.Body
                        // Element type of the inner sub-array (for the param binding type).
                        let arrElemStr = elemTypeToCpp arrType.ElemType
                        // Element type of the OUTPUT (per-row result).
                        // Phase B2: all branches return IRType.
                        let outElem =
                            match info.OutputType with
                            | ArrayElem a -> a.ElemType
                            | IRTScalar _ as t -> t
                            | _ ->
                                match inferExprType body with
                                | IRTScalar _ as t -> t
                                | ArrayElem a -> a.ElemType
                                | _ -> arrType.ElemType
                        let outElemStr = elemTypeToCpp outElem
                        // Sub-array binding name.
                        let subName = sprintf "%s__sub" name
                        let nameMap0 = Map.add param.VarId subName ctx.VarNames
                        let nameMap =
                            lInfo.Captures
                            |> List.fold (fun m c -> Map.add c.Id c.Name m) nameMap0
                        let bodyStr = exprToCpp nameMap body
                        let originLabel =
                            if isGroupedOuter then "grouped array" else "ragged literal"
                        let code = [
                            sprintf "%s// ragged peel over %s '%s'" ind originLabel arrName
                            sprintf "%ssize_t %s_extents[1] = {%s};" ind name ngroupsExpr
                            sprintf "%sArray<%s, 1> %s = { new %s[%s], %s_extents };" ind outElemStr name outElemStr ngroupsExpr name
                            sprintf "%sfor (size_t __g = 0; __g < %s; __g++) {" ind ngroupsExpr
                            // v24d-1: peeled row is wrapper-typed so the kernel
                            // can iterate via .extents and the wrapper's
                            // operator[]. The local _extents declaration
                            // remains as the wrapper's extents pointer source.
                            sprintf "%s    size_t %s_extents[1] = {%s};" ind subName perRowLenExpr
                            sprintf "%s    Array<%s, 1> %s = { %s[__g], %s_extents };" ind arrElemStr subName arrName subName
                            sprintf "%s    %s[__g] = %s;" ind name bodyStr
                            sprintf "%s}" ind
                        ]
                        Some code
                    | _ -> None
    
    let raggedResult = tryRaggedPeel ()
    if raggedResult.IsSome then raggedResult.Value
    else
    
    // Pre-materialize any inline array expressions (mask, intersect, union, etc.)
    // These need to be bound to temporary variables before the loop nest can reference them.
    let mutable preCode = []
    let mutable tempCtx = ctx
    let materializedArrays =
        info.Arrays |> List.mapi (fun i arr ->
            match arr with
            | IRVar (id, _) -> 
                let name = Map.tryFind id tempCtx.VarNames |> Option.defaultValue (sprintf "arr%d" i)
                (name, arr)
            | IRRange _ -> (sprintf "__range%d" i, arr)
            | IRVirtualReverse _ -> (sprintf "__rev%d" i, arr)
            | IRBlocked _ -> (sprintf "__blk%d" i, arr)
            | IRMask _ | IRIntersect _ | IRUnion _ ->
                // Auto-materialize: when a method_for receives an inline form
                // as one of its arrays, generate a temporary binding before
                // the loop nest. The Phase C lift pass deliberately leaves
                // these in IRMethodFor.Arrays slots (treated as a "blessed
                // position"), routing them through this path instead.
                //
                // Strict elem-type inference (with #error on unresolvable)
                // happens here; the shared `materializeInlineForm` helper
                // emits the C++ template.
                let tmpName = sprintf "%s__tmp%d" name i
                let tmpId = builder.FreshId()
                let tmpType = inferExprType arr
                let (elemET, autoMaterErr) = inferElemTypeStrict tempCtx ind arr "auto-materialize"
                let elemStr = elemTypeToCpp elemET
                preCode <- preCode @ autoMaterErr
                let matStmts =
                    match materializeInlineForm tempCtx.VarNames tmpName elemStr arr with
                    | Some s -> s
                    | None -> []
                let code = matStmts |> List.map (fun s -> ind + s)
                preCode <- preCode @ code
                tempCtx <- addVarName tmpId tmpName tempCtx
                (tmpName, IRVar (tmpId, tmpType))
            | IRSort _ | IRGroupKeys _ | IRGroupBy _ ->
                // Per design decision: these operations require let-binding.
                // Auto-materializing them inline would require duplicating their
                // codegen here (mask/intersect/union do it because they predate
                // the let-only convention), and we deliberately stopped paying
                // that cost. Surface a clear error instead of emitting bad C++.
                let opName =
                    match arr with
                    | IRSort _ -> "sort"
                    | IRGroupKeys _ -> "group_keys"
                    | IRGroupBy _ -> "group_by"
                    | _ -> "?"
                let errCode = codegenError ctx ind (sprintf "'%s' must be let-bound before use in method_for; e.g. let s = %s(...) then method_for(s)" opName opName)
                preCode <- preCode @ errCode
                (sprintf "arr%d" i, arr)
            | _ -> (sprintf "arr%d" i, arr))
    
    let arrayNames = materializedArrays |> List.map fst
    let updatedArrays = materializedArrays |> List.map snd
    let info = { info with Arrays = updatedArrays }
    
    if arrayNames.IsEmpty then
        codegenError ctx ind (sprintf "no arrays in method_for for '%s' — kernel cannot be applied" name)
    else
        // Build LoopNestCodeGen (handles both outer product and co-iteration)
        let codeGen = buildLoopNestCodeGen info arrayNames name builder
        
        // Get output rank and type info
        let outputRank = 
            match codeGen.OutputType with
            | ArrayElem arr -> arrayRank arr
            | IRTScalar _ -> 0
            | _ -> 0
        
        let outputElemType =
            match codeGen.OutputType with
            | ArrayElem arr -> elemTypeToCpp arr.ElemType
            | IRTScalar et -> primTypeToCpp et
            | t -> irTypeToCpp t

        // Branch on output shape. Array output gets the full ceremony
        // (symmetry vector, extents declaration, allocation, then loop nest
        // with indexed assignments). Scalar output gets a single scalar
        // accumulator initialized to zero, then the same loop nest with
        // `+=` accumulation (genLoopNest detects this via codeGen.OutputType).
        // The scalar branch handles the Cartesian-sum-reduce pattern that
        // the old hand-written 2-array IIFE special-cased — now generalized
        // to any number of input arrays through the shared LoopNestCodeGen
        // machinery (commutativity, Reynolds, etc. all carry through).
        match codeGen.OutputType with
        | IRTScalar _ ->
            // Scalar accumulator: declare initialized to 0, then run the
            // loop nest which accumulates into it via genLoopNest's `+=`.
            let scalarDecl = sprintf "%s%s %s = 0;" ind outputElemType name
            let loopCode = genLoopNest codeGen tempCtx.VarNames tempCtx.Indent
            preCode @ [scalarDecl; ""] @ loopCode
        | _ ->
            // Array output: symmetry vector, extents, allocation, loop nest.
            let symmVecName = sprintf "%s_symm" name
            let symmVecDecl =
                if codeGen.OutputSymmVec.IsEmpty then
                    sprintf "%sstatic constexpr const size_t* %s = nullptr;" ind symmVecName
                else
                    let values = codeGen.OutputSymmVec |> List.map string |> String.concat ", "
                    sprintf "%sstatic constexpr const size_t %s[%d] = {%s};" ind symmVecName codeGen.OutputSymmVec.Length values

            // Generate extent computation
            let extentsName = sprintf "%s_extents" name
            let extentsDecl = sprintf "%ssize_t* %s = new size_t[%d];" ind extentsName outputRank

            // Fill extents from loop level info
            let extentsFill =
                codeGen.Bindings |> List.mapi (fun i b ->
                    match b.Extent with
                    | IRLit (IRLitInt n) ->
                        sprintf "%s%s[%d] = %s;" ind extentsName i (sprintf "%d" n)
                    | _ ->
                        sprintf "%s%s[%d] = %s.extents[%d];" ind extentsName i b.ExtentArrayRef b.ExtentDimRef)

            // Generate allocation as Array<T,N> wrapper. extentsName here is
            // a runtime-allocated `size_t*` (not a static constexpr); the
            // wrapper just stores a pointer to it, so the same brace-init
            // pattern works.
            let allocDecl = sprintf "%sArray<%s, %d> %s = { allocate<typename promote<%s, %d>::type, %s>(%s), %s };"
                                ind outputElemType outputRank name outputElemType outputRank symmVecName extentsName extentsName

            // Generate loop nest
            let loopCode = genLoopNest codeGen tempCtx.VarNames tempCtx.Indent

            // Combine all (prepend any pre-materialized temporaries)
            preCode @ [symmVecDecl; ""; extentsDecl] @ extentsFill @ [""; allocDecl; ""] @ loopCode

/// Generate a fused loop nest with multiple kernel assignments.
/// All kernels share the same loop structure (bindings), only differ in kernel expr and output.
/// Each extra kernel tuple: (outName, kernelExpr, params, captures, hasReynolds, isAntisymmetric)
let genFusedLoopNest (codeGen: LoopNestCodeGen) (extraKernels: (string * IRExpr * IRParam list * CaptureInfo list * bool * bool) list) (outerNames: Map<int, string>) (indent: int) : string list =
    let ind n = String.replicate n "    "
    let mutable lines = []
    let mutable depth = indent
    
    // Track current peeled name for each array position
    let mutable currentNames : Map<int, string> = 
        codeGen.InputArrayNames |> List.mapi (fun i n -> (i, n)) |> Map.ofList
    let mutable paramFinalNames : Map<int, string> = Map.empty
    
    // Generate nested loops with element bindings (same as genLoopNest)
    for binding in codeGen.Bindings do
        lines <- lines @ [ind depth + genForLoopHeader binding]
        depth <- depth + 1
        for elem in binding.Elements do
            let currentName = 
                Map.tryFind elem.ArrayPosition currentNames 
                |> Option.defaultValue elem.ArrayName
            let (code, newName) = genElementBindingNew binding elem currentName
            lines <- lines @ [ind depth + code]
            currentNames <- Map.add elem.ArrayPosition newName currentNames
            match elem.Virtual with
            | VirtualRange _ | VirtualReverse ->
                paramFinalNames <- Map.add elem.ParamVarId elem.ParamName paramFinalNames
            | RealArray ->
                paramFinalNames <- Map.add elem.ParamVarId newName paramFinalNames
    
    // Output index expression (shared by all kernels)
    let outputIdx = 
        codeGen.Bindings 
        |> List.map (fun b -> sprintf "[%s]" b.IndexName)
        |> String.concat ""
    
    // First kernel assignment (uses primary LoopNestCodeGen's Reynolds info)
    let nameMap0 = paramFinalNames |> Map.fold (fun acc k v -> Map.add k v acc) outerNames
    let nameMap0 = codeGen.Captures |> List.fold (fun acc c -> Map.add c.Id c.Name acc) nameMap0
    let reynoldsResult0 = genKernelExprWithReynolds codeGen.KernelExpr codeGen.KernelParams codeGen.HasReynolds codeGen.IsAntisymmetric nameMap0 paramFinalNames
    if codeGen.HasReynolds && reynoldsResult0.UniqueTerms < reynoldsResult0.TotalPerms then
        lines <- lines @ [ind depth + sprintf "// Reynolds: %d/%d perms unique (dedup %dx)" reynoldsResult0.UniqueTerms reynoldsResult0.TotalPerms (reynoldsResult0.TotalPerms / max 1 reynoldsResult0.UniqueTerms)]
    lines <- lines @ [ind depth + sprintf "%s%s = %s;" codeGen.OutputName outputIdx reynoldsResult0.CppExpr]
    
    // Additional kernel assignments
    // Each extra kernel has its own param VarIds that need mapping to the same peeled names.
    // We bridge via position: primary param[i].VarId → peeledName, extra param[i].VarId → same peeledName.
    for (outName, kernelExpr, extraParams, captures, hasReynolds, isAntisym) in extraKernels do
        let extraParamFinalNames =
            extraParams |> List.mapi (fun i p ->
                match codeGen.KernelParams |> List.tryItem i with
                | Some primaryParam ->
                    match Map.tryFind primaryParam.VarId paramFinalNames with
                    | Some peeledName -> Some (p.VarId, peeledName)
                    | None -> None
                | None -> None)
            |> List.choose id
            |> Map.ofList
        let nameMap = extraParamFinalNames |> Map.fold (fun acc k v -> Map.add k v acc) outerNames
        let nameMap = captures |> List.fold (fun acc c -> Map.add c.Id c.Name acc) nameMap
        let reynoldsResult = genKernelExprWithReynolds kernelExpr extraParams hasReynolds isAntisym nameMap extraParamFinalNames
        if hasReynolds && reynoldsResult.UniqueTerms < reynoldsResult.TotalPerms then
            lines <- lines @ [ind depth + sprintf "// Reynolds: %d/%d perms unique (dedup %dx)" reynoldsResult.UniqueTerms reynoldsResult.TotalPerms (reynoldsResult.TotalPerms / max 1 reynoldsResult.UniqueTerms)]
        lines <- lines @ [ind depth + sprintf "%s%s = %s;" outName outputIdx reynoldsResult.CppExpr]
    
    // Close all loops
    for _ in codeGen.Bindings do
        depth <- depth - 1
        lines <- lines @ [ind depth + "}"]
    
    lines


/// Generate C++ code for inline object_for application (e.g., A [+] B)
let genObjectForApplication (ctx: CodeGenContext) (name: string) (objInfo: ObjectForInfo) (arrays: IRExpr list) (builder: IRBuilder) : string list =
    let ind = indentStr ctx
    
    // Get array names
    let arrayNames = arrays |> List.mapi (fun i arr ->
        match arr with
        | IRVar (id, _) -> Map.tryFind id ctx.VarNames |> Option.defaultValue (sprintf "arr%d" i)
        | _ -> sprintf "arr%d" i)
    
    // Get kernel info
    let (kernelParams, kernelBody) =
        match objInfo.Kernel with
        | IRLambda lInfo -> (lInfo.Params, lInfo.Body)
        | _ -> ([], IRLit IRLitUnit)
    
    // Infer element type from kernel params
    let elemTypeStr =
        kernelParams |> List.tryPick (fun p ->
            match p.Type with IRTScalar et -> Some (primTypeToCpp et) | ArrayElem arr -> Some (elemTypeToCpp arr.ElemType) | _ -> None)
        |> Option.defaultValue (match kernelParams with p :: _ -> irTypeToCpp p.Type | [] -> "void")
    
    // For outer product (InputRanks = [1; 1], OutputRank = 2), generate nested loops
    // For elementwise (InputRanks = [0; 0], OutputRank = 1), generate single loop
    
    match objInfo.InputRanks, arrayNames with
    | [1; 1], [arrA; arrB] ->
        // Outer product: result[i][j] = kernel(A[i], B[j])
        let extentsDecl = sprintf "%ssize_t %s_extents[2] = {%s.extents[0], %s.extents[0]};" ind name arrA arrB
        let allocDecl = sprintf "%sArray<%s, 2> %s = { allocate<promote<%s, 2>::type>(%s_extents), %s_extents };" ind elemTypeStr name elemTypeStr name name
        
        // Build name map for kernel body
        let bodyNames = 
            kernelParams 
            |> List.mapi (fun i p -> (p.VarId, sprintf "%s_%s" (if i = 0 then arrA else arrB) "__i"))
            |> Map.ofList
            |> Map.fold (fun acc k v -> Map.add k v acc) ctx.VarNames
        
        let kernelStr = exprToCpp bodyNames kernelBody
        
        let loopCode = [
            sprintf "%sfor (size_t __i0 = 0; __i0 < %s.extents[0]; __i0++) {" ind arrA
            sprintf "%s    %s %s___i = %s[__i0];" ind elemTypeStr arrA arrA
            sprintf "%s    for (size_t __i1 = 0; __i1 < %s.extents[0]; __i1++) {" ind arrB
            sprintf "%s        %s %s___i = %s[__i1];" ind elemTypeStr arrB arrB
            sprintf "%s        %s[__i0][__i1] = %s;" ind name kernelStr
            sprintf "%s    }" ind
            sprintf "%s}" ind
        ]
        
        [extentsDecl; allocDecl; ""] @ loopCode
        
    | [0; 0], [arrA; arrB] ->
        // Elementwise: result[i] = kernel(A[i], B[i])
        let extentsDecl = sprintf "%ssize_t %s_extents[1] = {%s.extents[0]};" ind name arrA
        let allocDecl = sprintf "%sArray<%s, 1> %s = { allocate<promote<%s, 1>::type>(%s_extents), %s_extents };" ind elemTypeStr name elemTypeStr name name
        
        // Build name map for kernel body
        let bodyNames = 
            kernelParams 
            |> List.mapi (fun i p -> (p.VarId, sprintf "%s___i" (if i = 0 then arrA else arrB)))
            |> Map.ofList
            |> Map.fold (fun acc k v -> Map.add k v acc) ctx.VarNames
        
        let kernelStr = exprToCpp bodyNames kernelBody
        
        let loopCode = [
            sprintf "%sfor (size_t __i0 = 0; __i0 < %s.extents[0]; __i0++) {" ind arrA
            sprintf "%s    %s %s___i = %s[__i0];" ind elemTypeStr arrA arrA
            sprintf "%s    %s %s___i = %s[__i0];" ind elemTypeStr arrB arrB
            sprintf "%s    %s[__i0] = %s;" ind name kernelStr
            sprintf "%s}" ind
        ]
        
        [extentsDecl; allocDecl; ""] @ loopCode
        
    | _ ->
        // Unsupported configuration
        codegenError ctx ind (sprintf "unsupported object_for configuration for '%s'" name)

// ============================================================================
// Binding Generation
// ============================================================================

/// Unroll an IRLet chain into a list of (varId, valueExpr) statements and a final return expression.
/// e.g., IRLet(id1, v1, IRLet(id2, v2, body)) → statements=[(id1,v1), (id2,v2)], return=body
let rec unrollLetChain (expr: IRExpr) : (IRId * IRExpr) list * IRExpr =
    match expr with
    | IRLet (id, value, body) ->
        let (rest, final) = unrollLetChain body
        ((id, value) :: rest, final)
    | _ -> ([], expr)

// ============================================================================
// Recursive Parallel/Fusion Tree Helpers
// ============================================================================

/// Recursively generate code for a parallel composition tree (<&>).
/// Each leaf IRApplyCombinator gets its own independent loop nest.
/// Returns (code_lines, result_variable_name, tupleChildrenMap).
/// tupleChildrenMap tracks pair structure for nested tuple destructuring.
let rec genParallelTree (ctx: CodeGenContext) (name: string) (expr: IRExpr) (builder: IRBuilder) : string list * string * Map<string, string list> =
    let ind = indentStr ctx
    // Collect all leaf expressions from the parallel/fusion tree
    let rec collectLeaves (e: IRExpr) : IRExpr list =
        match e with
        | IRParallel (left, right, _) | IRFusion (left, right) ->
            collectLeaves left @ collectLeaves right
        | IRVar (id, _) ->
            match Map.tryFind id ctx.DeferredComputations with
            | Some deferred -> collectLeaves deferred
            | None -> [e]
        | _ -> [e]
    let leaves = collectLeaves expr
    match leaves with
    | [single] ->
        // Single leaf — generate directly, no tuple wrapping
        match single with
        | IRApplyCombinator info ->
            let code = genApplyCombinator ctx name info builder
            (code, name, Map.empty)
        | IRVar (id, _) ->
            let existingName = Map.tryFind id ctx.VarNames |> Option.defaultValue name
            ([], existingName, Map.empty)
        | _ ->
            let code = genScalarBinding ctx name single (inferExprType single)
            (code, name, Map.empty)
    | _ ->
        // Multiple leaves — generate each, assemble flat tuple
        let leafNames = leaves |> List.mapi (fun i _ -> sprintf "%s_%d" name i)
        let allCode =
            (leaves, leafNames) ||> List.map2 (fun leaf leafName ->
                match leaf with
                | IRApplyCombinator info ->
                    genApplyCombinator ctx leafName info builder @ [""]
                | IRVar (id, _) ->
                    let existingName = Map.tryFind id ctx.VarNames |> Option.defaultValue leafName
                    if existingName <> leafName then
                        [sprintf "%sauto& %s = %s;" ind leafName existingName; ""]
                    else []
                | _ ->
                    genScalarBinding ctx leafName leaf (inferExprType leaf) @ [""])
            |> List.concat
        let tupleLine = sprintf "%sauto %s = std::make_tuple(%s);" ind name (leafNames |> String.concat ", ")
        let childMap = Map.ofList [name, leafNames]
        (allCode @ [tupleLine], name, childMap)

/// Collect all leaf expressions from a fusion tree in left-to-right order.
let rec collectFusionLeaves (expr: IRExpr) : IRExpr list =
    match expr with
    | IRFusion (left, right) -> collectFusionLeaves left @ collectFusionLeaves right
    | _ -> [expr]

/// Build nested std::make_pair expression matching the tree structure of an IRFusion/IRParallel.
/// Consumes names from the list in left-to-right order.
let rec buildPairTree (expr: IRExpr) (names: string list) : string * string list =
    match expr with
    | IRFusion (left, right) | IRParallel (left, right, _) ->
        let (leftStr, names') = buildPairTree left names
        let (rightStr, names'') = buildPairTree right names'
        (sprintf "std::make_pair(%s, %s)" leftStr rightStr, names'')
    | _ ->
        match names with
        | n :: rest -> (n, rest)
        | [] -> (exprError "internal: no names left in buildPairTree", [])

/// Generate code for N-way mandatory fusion (<&!>).
/// Collects all leaf ApplyCombinators, generates a single fused loop nest,
/// then builds nested pair tree for the result.
/// Build named pair tree from an IRExpr tree structure and a list of leaf names.
/// Generates named intermediate std::make_pair variables for each internal node.
/// Uses __p_ prefix for intermediates to avoid collision with leaf names.
/// Returns (code_lines, result_name, tupleChildrenMap).
let genFusionTree (ctx: CodeGenContext) (name: string) (expr: IRExpr) (builder: IRBuilder) : string list * string * Map<string, string list> =
    let ind = indentStr ctx
    let rawLeaves = collectFusionLeaves expr
    
    // Resolve IRVar leaves through DeferredComputations
    let leaves = rawLeaves |> List.map (fun leaf ->
        match leaf with
        | IRVar (id, _) ->
            match Map.tryFind id ctx.DeferredComputations with
            | Some deferred -> deferred
            | None -> leaf
        | _ -> leaf)
    
    // Extract ApplyInfo from each leaf
    let infos = leaves |> List.choose (fun e ->
        match e with
        | IRApplyCombinator info -> Some info
        | _ -> None)
    
    if infos.Length < 2 || infos.Length <> leaves.Length then
        // Not all leaves are ApplyCombinators — fall back to parallel generation
        genParallelTree ctx name expr builder
    else
        // Generate output names for each leaf
        let leafNames = infos |> List.mapi (fun i _ -> sprintf "%s_%d" name i)
        
        // Use first info for loop structure
        let primaryInfo = infos.[0]
        let primaryName = leafNames.[0]
        
        // Extract array names from the shared loop
        let arrayNames = 
            primaryInfo.Arrays |> List.mapi (fun i arr ->
                match arr with
                | IRVar (id, _) -> 
                    Map.tryFind id ctx.VarNames |> Option.defaultValue (sprintf "arr%d" i)
                | IRRange _ -> sprintf "__range%d" i
                | IRVirtualReverse _ -> sprintf "__rev%d" i
                | IRBlocked _ -> sprintf "__blk%d" i
                | _ -> sprintf "arr%d" i)
        
        if arrayNames.IsEmpty then
            (codegenError ctx ind (sprintf "no arrays in method_for for parallel/sequence '%s'" name), name, Map.empty)
        else
            // Build primary LoopNestCodeGen
            let codeGenPrimary = buildLoopNestCodeGen primaryInfo arrayNames primaryName builder
            
            // Extract kernel info for all additional leaves
            let extraKernels = 
                (List.tail infos, List.tail leafNames) ||> List.map2 (fun info outName ->
                    let (kParams, kBody, _commGroups, captures, hasReynolds, isAntisym) =
                        match info.Kernel with
                        | IRLambda lInfo -> (lInfo.Params, lInfo.Body, lInfo.CommGroups, lInfo.Captures, false, false)
                        | IRReynolds (IRLambda lInfo, isAnti) -> (lInfo.Params, lInfo.Body, lInfo.CommGroups, lInfo.Captures, true, isAnti)
                        | _ -> ([], IRLit IRLitUnit, [], [], false, false)
                    (outName, kBody, kParams, captures, hasReynolds, isAntisym))
            
            // Generate symm vec + extents + allocation for each output
            let allCodeGens = infos |> List.mapi (fun i info ->
                buildLoopNestCodeGen info arrayNames leafNames.[i] builder)
            
            let declCode = allCodeGens |> List.mapi (fun i cg ->
                let lname = leafNames.[i]
                let symmVecName = sprintf "%s_symm" lname
                let symmVecDecl = genSymmVecDecl symmVecName cg.OutputSymmVec
                let outputRank = match cg.OutputType with ArrayElem arr -> arrayRank arr | _ -> 0
                let outputElemType = match cg.OutputType with ArrayElem arr -> elemTypeToCpp arr.ElemType | IRTScalar et -> primTypeToCpp et | t -> irTypeToCpp t
                let extentsName = sprintf "%s_extents" lname
                let extentsDecl = sprintf "%ssize_t* %s = new size_t[%d];" ind extentsName outputRank
                let extentsFill = 
                    cg.Bindings |> List.mapi (fun j b ->
                        match b.Extent with
                        | IRLit (IRLitInt n) -> sprintf "%s%s[%d] = %d;" ind extentsName j n
                        | _ -> sprintf "%s%s[%d] = %s.extents[%d];" ind extentsName j b.ExtentArrayRef b.ExtentDimRef)
                let allocDecl = sprintf "%sArray<%s, %d> %s = { allocate<typename promote<%s, %d>::type, %s>(%s), %s };" 
                                    ind outputElemType outputRank lname outputElemType outputRank symmVecName extentsName extentsName
                [symmVecDecl] @ [extentsDecl] @ extentsFill @ [allocDecl]) |> List.concat
            
            // Generate single fused loop nest
            let loopCode = genFusedLoopNest codeGenPrimary extraKernels ctx.VarNames ctx.Indent
            
            // Build flat tuple from leaf names
            let tupleLine = sprintf "%sauto %s = std::make_tuple(%s);" ind name (leafNames |> String.concat ", ")
            let childrenMap = Map.ofList [name, leafNames]
            
            (declCode @ [""] @ loopCode @ [""] @ [tupleLine], name, childrenMap)

/// Compute the number of flat leaves for a type (recursing into nested tuples).
let rec tupleLeafCount (ty: IRType) : int =
    match ty with
    | IRTTuple ts -> ts |> List.sumBy tupleLeafCount
    | _ -> 1

/// For a tuple type, compute the flat child range [start, start+count) for each top-level element.
/// E.g. ((α,β), γ) → [(0, 2); (2, 1)] meaning element 0 spans flat indices 0..1, element 1 is flat index 2.
let tupleLeafRanges (ty: IRType) : (int * int) list =
    match ty with
    | IRTTuple ts ->
        let mutable offset = 0
        ts |> List.map (fun t ->
            let count = tupleLeafCount t
            let range = (offset, count)
            offset <- offset + count
            range)
    | _ -> [(0, 1)]

/// Generate C++ code for an IR binding
let rec genBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    let ind = indentStr ctx
    // Make anonymous tuple binding names unique to avoid C++ redefinition errors
    let name = if binding.Name = "_" then sprintf "__tup_%d" binding.Id else binding.Name
    
    match binding.Value with
    | IRMask (arrExpr, predExpr) ->
        // mask(array, pred): eager compaction — scan, count, allocate, fill.
        // Strict elem-type inference (emits #error if unresolvable) and
        // predicate-lambda validation happen here at the call site; the
        // shared `materializeInlineForm` helper just emits the C++ template.
        let (elemET, elemErrCode) = inferElemTypeStrict ctx ind arrExpr "mask"
        let elemStr = elemTypeToCpp elemET
        let predErrCode =
            match predExpr with
            | IRLambda lInfo when lInfo.Params.Length = 1 -> []
            | _ -> codegenError ctx ind "mask: predicate must be a single-parameter lambda; got something else (typechecker or IR bug)"
        let matStmts =
            match materializeInlineForm ctx.VarNames name elemStr binding.Value with
            | Some s -> s
            | None -> []  // Unreachable: helper supports IRMask
        let code = elemErrCode @ predErrCode @ [sprintf "%s// mask: count + compact" ind] @ (matStmts |> List.map (fun s -> ind + s))
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')
    
    | IRIntersect (aExpr, bExpr) ->
        // intersect(A, B): elements present in both arrays.
        let (elemET, elemErrCode) = inferElemTypeStrict ctx ind aExpr "intersect"
        let elemStr = elemTypeToCpp elemET
        let matStmts =
            match materializeInlineForm ctx.VarNames name elemStr binding.Value with
            | Some s -> s
            | None -> []
        let code = elemErrCode @ [sprintf "%s// intersect: build set from B, scan A" ind] @ (matStmts |> List.map (fun s -> ind + s))
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')
    
    | IRUnion (aExpr, bExpr) ->
        // union(A, B): all elements from A, plus elements from B not in A.
        let (elemET, elemErrCode) = inferElemTypeStrict ctx ind aExpr "union"
        let elemStr = elemTypeToCpp elemET
        let matStmts =
            match materializeInlineForm ctx.VarNames name elemStr binding.Value with
            | Some s -> s
            | None -> []
        let code = elemErrCode @ [sprintf "%s// union: all of A, plus elements from B not in A" ind] @ (matStmts |> List.map (fun s -> ind + s))
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')
    
    | IRGroupKeys keys ->
        // group_keys: build CSR offsets + permutation from a key array.
        // Two supported cases: ETIndexRef -> Idx<N> (Case 1, positional) and
        // ETIndexRef -> EnumIdx<[...]> (Case 2, reverse lookup).
        // Case 3 (dynamic, no ETIndexRef) errors out: we don't yet build hash-based dispatch.
        let keysName = exprToCppCtx ctx keys
        let (elemType, keysElemErrCode) = inferElemTypeStrict ctx ind keys "group_keys"
        let elemStr = elemTypeToCpp elemType
        match binding.Type with
        | IRTGroupKeys (outerIdx, _, enumValuesOpt) ->
            let ngroupsOpt =
                match outerIdx.Extent with
                | IRLit (IRLitInt n) -> Some (int n)
                | _ -> None
            match ngroupsOpt, enumValuesOpt with
            | None, _ ->
                // Case 3 — dynamic ngroups via hash discovery. Builds a key →
                // bucket-index map in a single discovery pass, then reuses the
                // map for counts/offsets/permutation. Bucket indices are
                // assigned in first-occurrence order: the bucket for a key is
                // its position in the sequence of distinct keys as encountered
                // walking the input left-to-right.
                //
                // Replaces an earlier max-key-scan implementation that only
                // worked for dense [0..N) keys; sparse keys (e.g. [101, 205,
                // 307]) caused max-scan to allocate one bucket per integer in
                // [0..max], almost all empty, with the wrong semantics.
                //
                // For monotonic dense keys (the historical Case 3 pattern,
                // e.g. [0,1,2,0,1,2]) the hash and max-scan paths produce
                // identical bucket orderings, so existing tests are unchanged.
                // Non-monotonic dense keys differ (hash uses first-occurrence
                // order, max-scan used numeric order); users who want numeric
                // ordering should annotate with `Idx<N>` to opt into Case 1.
                let code = keysElemErrCode @ [
                    sprintf "%s// group_keys: dynamic ngroups (hash discovery, %s keys)" ind elemStr
                    sprintf "%sstd::unordered_map<%s, size_t> %s__lookup;" ind elemStr name
                    sprintf "%ssize_t %s__ngroups = 0;" ind name
                    sprintf "%sfor (size_t __ki = 0; __ki < %s.extents[0]; __ki++) {" ind keysName
                    sprintf "%s    %s __k = %s[__ki];" ind elemStr keysName
                    sprintf "%s    if (%s__lookup.find(__k) == %s__lookup.end()) %s__lookup[__k] = %s__ngroups++;" ind name name name name
                    sprintf "%s}" ind
                    sprintf "%ssize_t* %s__counts = new size_t[%s__ngroups]();" ind name name
                    sprintf "%sfor (size_t __ki = 0; __ki < %s.extents[0]; __ki++) {" ind keysName
                    sprintf "%s    %s__counts[%s__lookup[%s[__ki]]]++;" ind name name keysName
                    sprintf "%s}" ind
                    sprintf "%ssize_t* %s__offsets = new size_t[%s__ngroups + 1];" ind name name
                    sprintf "%s%s__offsets[0] = 0;" ind name
                    sprintf "%sfor (size_t __gi = 0; __gi < %s__ngroups; __gi++) %s__offsets[__gi + 1] = %s__offsets[__gi] + %s__counts[__gi];" ind name name name name
                    sprintf "%ssize_t* %s__fill = new size_t[%s__ngroups]();" ind name name
                    sprintf "%ssize_t* %s__perm = new size_t[%s.extents[0]];" ind name keysName
                    sprintf "%sfor (size_t __ki = 0; __ki < %s.extents[0]; __ki++) {" ind keysName
                    sprintf "%s    size_t __g = %s__lookup[%s[__ki]];" ind name keysName
                    sprintf "%s    %s__perm[%s__offsets[__g] + %s__fill[__g]++] = __ki;" ind name name name
                    sprintf "%s}" ind
                    sprintf "%ssize_t %s_extents[1] = {%s__ngroups};" ind name name
                    sprintf "%svoid* %s = nullptr; // gk: state in %s__ngroups, %s__offsets, %s__perm" ind name name name name
                ]
                let ctx' = addVarName binding.Id name ctx
                (code, ctx')
            | Some ngroups, None ->
                // Case 1: positional bucketing. keys[i] in [0, ngroups).
                let code = keysElemErrCode @ [
                    sprintf "%s// group_keys: %d groups, positional buckets (Idx<N> keys)" ind ngroups
                    sprintf "%ssize_t %s__ngroups = %d;" ind name ngroups
                    sprintf "%ssize_t %s__counts[%d] = {0};" ind name ngroups
                    sprintf "%sfor (size_t __ki = 0; __ki < %s.extents[0]; __ki++) {" ind keysName
                    sprintf "%s    %s__counts[%s[__ki]]++;" ind name keysName
                    sprintf "%s}" ind
                    sprintf "%ssize_t %s__offsets[%d];" ind name (ngroups + 1)
                    sprintf "%s%s__offsets[0] = 0;" ind name
                    sprintf "%sfor (size_t __gi = 0; __gi < %d; __gi++) %s__offsets[__gi + 1] = %s__offsets[__gi] + %s__counts[__gi];" ind ngroups name name name
                    sprintf "%ssize_t %s__fill[%d] = {0};" ind name ngroups
                    sprintf "%ssize_t* %s__perm = new size_t[%s.extents[0]];" ind name keysName
                    sprintf "%sfor (size_t __ki = 0; __ki < %s.extents[0]; __ki++) {" ind keysName
                    sprintf "%s    size_t __g = (size_t)%s[__ki];" ind keysName
                    sprintf "%s    %s__perm[%s__offsets[__g] + %s__fill[__g]++] = __ki;" ind name name name
                    sprintf "%s}" ind
                    sprintf "%ssize_t %s_extents[1] = {%s__ngroups};" ind name name
                    sprintf "%svoid* %s = nullptr; // gk: state in %s__ngroups, %s__offsets, %s__perm" ind name name name name
                ]
                let ctx' = addVarName binding.Id name ctx
                (code, ctx')
            | Some ngroups, Some values ->
                // Case 2: EnumIdx — keys are arbitrary integers OR strings;
                // map them via inline dispatch. Each value rendered to its
                // C++ literal form (int suffix `LL` or `std::string("...")`).
                // The comparison op is `==` for both kinds — works on
                // int64_t and std::string in C++.
                let bucketLambda =
                    let renderVal v =
                        match v with
                        | IR.EVInt n -> sprintf "%dLL" n
                        | IR.EVString s -> escapeStringLit s
                    let cases =
                        values
                        |> List.mapi (fun i v ->
                            sprintf "if (__v == %s) return (size_t)%d;" (renderVal v) i)
                        |> String.concat " "
                    sprintf "auto %s__bucket = [](%s __v) -> size_t { %s return (size_t)0; };" name elemStr cases
                let code = keysElemErrCode @ [
                    sprintf "%s// group_keys: %d groups, EnumIdx reverse lookup" ind ngroups
                    sprintf "%ssize_t %s__ngroups = %d;" ind name ngroups
                    sprintf "%s%s" ind bucketLambda
                    sprintf "%ssize_t %s__counts[%d] = {0};" ind name ngroups
                    sprintf "%sfor (size_t __ki = 0; __ki < %s.extents[0]; __ki++) {" ind keysName
                    sprintf "%s    %s__counts[%s__bucket(%s[__ki])]++;" ind name name keysName
                    sprintf "%s}" ind
                    sprintf "%ssize_t %s__offsets[%d];" ind name (ngroups + 1)
                    sprintf "%s%s__offsets[0] = 0;" ind name
                    sprintf "%sfor (size_t __gi = 0; __gi < %d; __gi++) %s__offsets[__gi + 1] = %s__offsets[__gi] + %s__counts[__gi];" ind ngroups name name name
                    sprintf "%ssize_t %s__fill[%d] = {0};" ind name ngroups
                    sprintf "%ssize_t* %s__perm = new size_t[%s.extents[0]];" ind name keysName
                    sprintf "%sfor (size_t __ki = 0; __ki < %s.extents[0]; __ki++) {" ind keysName
                    sprintf "%s    size_t __g = %s__bucket(%s[__ki]);" ind name keysName
                    sprintf "%s    %s__perm[%s__offsets[__g] + %s__fill[__g]++] = __ki;" ind name name name
                    sprintf "%s}" ind
                    sprintf "%ssize_t %s_extents[1] = {%s__ngroups};" ind name name
                    sprintf "%svoid* %s = nullptr; // gk: state in %s__ngroups, %s__offsets, %s__perm" ind name name name name
                ]
                let ctx' = addVarName binding.Id name ctx
                (code, ctx')
        | _ ->
            let ctx' = addVarName binding.Id name ctx
            (codegenError ctx ind (sprintf "group_keys binding '%s' has wrong inferred type (expected IRTGroupKeys)" name), ctx')
    
    | IRGroupBy (vals, gk) ->
        // group_by: per-group nested pointer allocation. Each grouped[g] is a
        // separately-allocated buffer of size offsets[g+1] - offsets[g], holding
        // the values for group g in the order discovered by the keys scan.
        // Layout matches normal rank-2 nested arrays so dimensional currying
        // (kernel taking a sub-array) works without touching the loop builder.
        // Outer extent = gk__ngroups; inner is ragged. Track grouped → gk so
        // future ragged-aware iteration can recover offsets.
        //
        // v24d-1: wrap the outer pointer-array in Array<T*, 1>. The wrapper's
        // .extents points at the 2-element local size_t array {ngroups, 0};
        // .extents[0] = ngroups, .extents[1] reads 0 (placeholder for the
        // ragged inner). Element type T* keeps `grouped[g]` as a bare row
        // pointer for downstream peeling. Print's inner-loop bound of 0
        // means no values printed, matching prior behavior.
        let valsName = exprToCppCtx ctx vals
        let gkName = exprToCppCtx ctx gk
        let (elemType, elemErrCode) = inferElemTypeStrict ctx ind vals "group_by"
        let elemStr = elemTypeToCpp elemType
        let code = elemErrCode @ [
            sprintf "%s// group_by: per-group nested allocation, group-contiguous via gk__perm" ind
            sprintf "%ssize_t %s_extents[2] = {%s__ngroups, 0}; // inner extent is ragged" ind name gkName
            sprintf "%sArray<%s*, 1> %s = { new %s*[%s__ngroups], %s_extents };" ind elemStr name elemStr gkName name
            sprintf "%sfor (size_t __g = 0; __g < %s__ngroups; __g++) {" ind gkName
            sprintf "%s    size_t __sz = %s__offsets[__g + 1] - %s__offsets[__g];" ind gkName gkName
            sprintf "%s    %s[__g] = new %s[__sz];" ind name elemStr
            sprintf "%s    for (size_t __k = 0; __k < __sz; __k++) {" ind
            sprintf "%s        %s[__g][__k] = %s[%s__perm[%s__offsets[__g] + __k]];" ind name valsName gkName gkName
            sprintf "%s    }" ind
            sprintf "%s}" ind
        ]
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with GroupedArrays = Map.add name gkName ctx'.GroupedArrays }
        (code, ctx')
    
    | IRSort (arrExpr, keyExpr) ->
        // sort(array, key): stable ascending sort by key.
        //
        // Phase 1 (current): eager materialization. Construct a permutation via
        // std::stable_sort with a comparator that calls the user's key function,
        // then write the permuted elements into a fresh contiguous buffer.
        //
        // Phase 2 (future): lazy chain handle. The result would be a handle
        // recording (key_fn, permutation, source_pointer); materialization would
        // be deferred to first access. Long chains of sorts and other rearrange-
        // ments can then be analyzed by the compiler before any layout commits,
        // enabling sort-skip, merge-style joins, and other optimizations.
        // Materialization caching (memoize-on-first-access) sits downstream of
        // those analyses, not as a substitute for them.
        let (elemET, elemErrCode) = inferElemTypeStrict ctx ind arrExpr "sort"
        let elemStr = elemTypeToCpp elemET
        // Validate single-param key lambda. Helper falls back to a 0L key
        // (preserving input order); the #error here surfaces the IR bug
        // before the silently-wrong sort runs.
        let keyErrCode =
            match keyExpr with
            | IRLambda lInfo when lInfo.Params.Length = 1 -> []
            | _ -> codegenError ctx ind "sort: key must be a single-parameter lambda; got something else (typechecker or IR bug)"
        let matStmts =
            match materializeInlineForm ctx.VarNames name elemStr binding.Value with
            | Some s -> s
            | None -> []
        let code = elemErrCode @ keyErrCode @ [sprintf "%s// sort: stable_sort on permutation, eager materialization" ind] @ (matStmts |> List.map (fun s -> ind + s))
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')
    
    | IRReduce (arrExpr, kernelExpr) ->
        // reduce(array, op): T/S reduction. Consumes the innermost dim by a
        // binary kernel, producing a scalar (rank-1 input only for now).
        //
        // Empty-array handling (post-extents integration):
        //   - Static extent > 0: standard loop, no runtime check
        //     (typecheck already proved non-emptiness)
        //   - Dynamic extent: emit a runtime guard that aborts cleanly on
        //     empty rather than reading uninitialized memory from arr[0]
        //   - Static extent = 0: typecheck rejects before reaching here
        //
        // Section kernels like (+) are lowered to lambdas during Lowering, so
        // we only handle IRLambda here.
        let arrName = exprToCppCtx ctx arrExpr
        let (elemType, elemErrCode) = inferElemTypeStrict ctx ind arrExpr "reduce"
        let elemStr = elemTypeToCpp elemType
        
        // Decide whether to emit a runtime extent check based on whether
        // the array's innermost-dim extent is statically known.
        let isStaticallyNonEmpty =
            match inferExprType arrExpr with
            | ArrayElem at when at.IndexTypes.Length >= 1 ->
                match tryEvalIntIR at.IndexTypes.[at.IndexTypes.Length - 1].Extent with
                | Some n -> n > 0L
                | None -> false
            | _ -> false
        
        let (kParams, kBody, kCaptures, kErrCode) =
            match kernelExpr with
            | IRLambda lInfo when lInfo.Params.Length = 2 ->
                (lInfo.Params, lInfo.Body, lInfo.Captures, [])
            | _ ->
                let errLines = codegenError ctx ind "reduce: kernel must be a binary lambda or operator section (typechecker or IR bug if not)"
                ([], IRLit (IRLitInt 0L), [], errLines)
        
        let aParamName = sprintf "__%s_a" name
        let bParamName = sprintf "__%s_b" name
        let kNames =
            ctx.VarNames
            |> (fun m -> if kParams.Length >= 1 then Map.add kParams.[0].VarId aParamName m else m)
            |> (fun m -> if kParams.Length >= 2 then Map.add kParams.[1].VarId bParamName m else m)
            |> fun m -> kCaptures |> List.fold (fun acc c -> Map.add c.Id c.Name acc) m
        let kStr = exprToCpp kNames kBody
        
        let guardLines =
            if isStaticallyNonEmpty then []
            else [
                sprintf "%s// reduce: dynamic extent — runtime non-emptiness guard" ind
                sprintf "%sif (%s.extents[0] == 0) { std::cerr << \"reduce: empty array, no reduction possible\" << std::endl; std::abort(); }" ind arrName
            ]
        
        let code = elemErrCode @ kErrCode @ guardLines @ [
            sprintf "%s// reduce: accumulator loop, eager" ind
            sprintf "%sauto %s__op = [&](%s %s, %s %s) { return %s; };" ind name elemStr aParamName elemStr bParamName kStr
            sprintf "%s%s %s = %s[0];" ind elemStr name arrName
            sprintf "%sfor (size_t __ri = 1; __ri < %s.extents[0]; __ri++) {" ind arrName
            sprintf "%s    %s = %s__op(%s, %s[__ri]);" ind name name name arrName
            sprintf "%s}" ind
        ]
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')
    
    | IRArrayLit (elements, arrType) ->
        let code = genArrayLiteral ctx name elements arrType
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')
    
    | IRApplyCombinator info ->
        // Defer: computation not materialized until |> compute or combinator forces it
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred computation>" ind name], ctx')
    
    | IRParallel _ | IRFusion _ ->
        // Defer: computation combinator not materialized until |> compute
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred computation combinator>" ind name], ctx')
    
    | IRFunctorMap _ ->
        // Defer: functor map not materialized until |> compute
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred functor map>" ind name], ctx')
    
    | IRZip _ ->
        // Defer: zip is a lazy array combinator, absorbed by method_for or materialized by |> compute
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred zip>" ind name], ctx')
    
    | IRChoice (left, right) ->
        // Only defer when children are computation-level (not scalar)
        let isCompExpr e = match e with IRApplyCombinator _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ | IRComposeObj _ | IRComposeMeth _ | IRBind _ | IRGuard _ | IRSequence _ -> true | IRVar _ -> true | _ -> false
        if isCompExpr left || isCompExpr right then
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
            ([sprintf "%s// %s = <deferred choice>" ind name], ctx')
        else
            // Scalar choice: generate directly
            let code = genScalarBinding ctx name binding.Value binding.Type
            let ctx' = addVarName binding.Id name ctx
            (code, ctx')
    
    | IRGuard (_, body) ->
        // Guard wrapping a computation: defer for later materialization via |> compute
        // Recurse through nested guards to check if the leaf body is a computation
        let rec leafIsComputation e =
            match e with
            | IRGuard (_, inner) -> leafIsComputation inner
            | IRApplyCombinator _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ | IRComposeObj _ | IRComposeMeth _ | IRBind _ | IRSequence _ -> true
            | IRVar (id, _) -> Map.containsKey id ctx.DeferredComputations
            | _ -> false
        if leafIsComputation body then
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
            ([sprintf "%s// %s = <deferred guard>" ind name], ctx')
        else
            // Scalar guard: generate directly
            let code = genScalarBinding ctx name binding.Value binding.Type
            let ctx' = addVarName binding.Id name ctx
            (code, ctx')
    
    | IRSequence elems ->
        // Defer: sequence is a flat n-ary parallel, materialized by |> compute
        let isCompExpr e = match e with IRApplyCombinator _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ | IRComposeObj _ | IRComposeMeth _ | IRBind _ | IRGuard _ | IRSequence _ -> true | IRVar _ -> true | _ -> false
        if elems |> List.exists isCompExpr then
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
            ([sprintf "%s// %s = <deferred sequence>" ind name], ctx')
        else
            // All scalars: generate as tuple
            let code = genScalarBinding ctx name binding.Value binding.Type
            let ctx' = addVarName binding.Id name ctx
            (code, ctx')
    
    | IRCompute inner ->
        // Compute unwraps - handle the inner expression
        // Recursive resolver: peels IRFunctorMap wrappers, resolves IRVar through deferred,
        // and handles IRComposeMeth by extracting right's kernel as a functor wrapper.
        // Returns (innerExpr, wrapperFunctions) where wrappers are innermost-first
        let rec resolveComputation (expr: IRExpr) (wrappers: IRExpr list) : IRExpr * IRExpr list =
            match expr with
            | IRVar (id, _) ->
                match Map.tryFind id ctx.DeferredComputations with
                | Some deferred -> resolveComputation deferred wrappers
                | None -> (expr, wrappers)
            | IRFunctorMap (f, inner) ->
                resolveComputation inner (f :: wrappers)
            | IRGuard (cond, body) ->
                // Resolve through guard: push wrappers into the body
                let (innerResolved, innerWrappers) = resolveComputation body wrappers
                (IRGuard (cond, innerResolved), innerWrappers)
            | IRComposeMeth (left, right) ->
                // @>> : c1 @>> c2 means "at each index, apply c2's kernel to c1's result"
                // Only fold into wrappers if kernel is an inlinable IRLambda
                let rec extractInlinableKernel e =
                    match e with
                    | IRVar (id, _) ->
                        match Map.tryFind id ctx.DeferredComputations with
                        | Some d -> extractInlinableKernel d
                        | None -> None
                    | IRApplyCombinator info -> 
                        match info.Kernel with
                        | IRLambda _ -> Some info.Kernel  // Only fold if kernel is inline lambda
                        | _ -> None
                    | IRFunctorMap (f, inner) ->
                        match extractInlinableKernel inner with
                        | Some k -> Some (IRCompose (k, f))
                        | None -> None
                    | _ -> None
                match extractInlinableKernel right with
                | Some kernel -> resolveComputation left (kernel :: wrappers)
                | None -> (expr, wrappers)  // fallback: leave for IRComposeMeth handler
            | _ -> (expr, wrappers)
        
        let (resolved, functorWrappers) = resolveComputation inner []
        
        // Compose functor wrappers into ApplyInfo kernel if present
        // f <$> (L <@> g) → L <@> (f ∘ g)
        // Wraps kernel body: λparams → f(g(params))
        let applyFunctorWrappers (info: ApplyInfo) (wrappers: IRExpr list) : ApplyInfo =
            if wrappers.IsEmpty then info
            else
                // Beta-reduce: substitute wrapper's parameter with inner body
                // f <$> (L <@> g) where f = λx → h(x)
                // becomes L <@> λparams → h(g(params))
                let betaReduce (wrapper: IRExpr) (body: IRExpr) : IRExpr =
                    match wrapper with
                    | IRLambda wInfo when wInfo.Params.Length = 1 ->
                        // Substitute param VarId with body expression
                        let paramId = wInfo.Params.[0].VarId
                        let rec subst (expr: IRExpr) =
                            match expr with
                            | IRVar (id, _) when id = paramId -> body
                            | IRVar _ | IRLit _ | IRParam _ -> expr
                            | IRBinOp (m, op, l, r) -> IRBinOp (m, op, subst l, subst r)
                            | IRUnaryOp (op, e) -> IRUnaryOp (op, subst e)
                            | IRIf (c, t, e) -> IRIf (subst c, subst t, subst e)
                            | IRApp (f, args, rt) -> IRApp (subst f, args |> List.map subst, rt)
                            | IRIndex (a, idxs, ty) -> IRIndex (subst a, idxs |> List.map subst, ty)
                            | IRTuple es -> IRTuple (es |> List.map subst)
                            | IRComplex (re, im) -> IRComplex (subst re, subst im)
                            | IRTupleProj (e, i, flat) -> IRTupleProj (subst e, i, flat)
                            | IRFieldAccess (e, f) -> IRFieldAccess (subst e, f)
                            | IRLet (id, v, b) -> IRLet (id, subst v, subst b)
                            | _ -> expr  // For complex nodes, leave as-is
                        subst wInfo.Body
                    | _ ->
                        // Can't beta-reduce, fall back to IRApp (IIFE in C++)
                        let retTy = match wrapper with
                                    | IRLambda li -> inferExprType li.Body
                                    | _ -> IRTScalar ETFloat64
                        IRApp (wrapper, [body], retTy)
                
                let wrappedKernel =
                    match info.Kernel with
                    | IRLambda lInfo ->
                        // Apply wrappers innermost first (list is already innermost-first from peeling)
                        let wrappedBody =
                            wrappers |> List.fold (fun body wrapper ->
                                betaReduce wrapper body
                            ) lInfo.Body
                        IRLambda { lInfo with Body = wrappedBody }
                    | IRReynolds (IRLambda lInfo, isAnti) ->
                        let wrappedBody =
                            wrappers |> List.fold (fun body wrapper ->
                                betaReduce wrapper body
                            ) lInfo.Body
                        IRReynolds (IRLambda { lInfo with Body = wrappedBody }, isAnti)
                    | other -> other
                // Update output type from outermost wrapper's return type
                let newOutputType =
                    match wrappers |> List.tryHead with
                    | Some (IRLambda li) -> inferExprType li.Body
                    | _ -> info.OutputType
                // If output is an array, update the element type
                let adjustedOutputType =
                    match info.OutputType, newOutputType with
                    | ArrayElem arr, IRTScalar et -> mkArrayLike { arr with ElemType = IRTScalar et }
                    | _ -> newOutputType
                { info with Kernel = wrappedKernel; OutputType = adjustedOutputType }
        
        match resolved with
        | IRApplyCombinator info ->
            // Check if Loop is a composed ObjectLoop (>>@)
            let resolvedLoop =
                match info.Loop with
                | IRVar (id, _) -> Map.tryFind id ctx.DeferredComputations |> Option.defaultValue info.Loop
                | other -> other
            match resolvedLoop with
            | IRComposeObj (obj1, obj2) ->
                // >>@ applied to arrays: (object_for(f) >>@ object_for(g)) <@> A
                // Resolve obj1/obj2 through deferred variables
                let rec resolveObj e =
                    match e with
                    | IRVar (id, _) -> Map.tryFind id ctx.DeferredComputations |> Option.map resolveObj |> Option.defaultValue e
                    | _ -> e
                let rObj1 = resolveObj obj1
                let rObj2 = resolveObj obj2
                let kernel1 = match rObj1 with IRObjectFor o -> o.Kernel | _ -> rObj1
                let kernel2 = match rObj2 with IRObjectFor o -> o.Kernel | _ -> rObj2
                let arrays = match info.Kernel with IRTuple elems -> elems | other -> [other]
                
                // Get C++ names for kernels (they're emitted as auto lambdas)
                let kernelName1 = match kernel1 with IRVar (id, _) -> Map.tryFind id ctx.VarNames | _ -> None
                let kernelName2 = match kernel2 with IRVar (id, _) -> Map.tryFind id ctx.VarNames | _ -> None
                
                // Get array info
                let arrName = 
                    match arrays with 
                    | [IRVar (id, _)] -> Map.tryFind id ctx.VarNames |> Option.defaultValue "arr0" 
                    | _ -> "arr0"
                let arrRank = 
                    match arrays with 
                    | [a] -> (match inferExprType a with ArrayElem arr -> arrayRank arr | _ -> 1) 
                    | _ -> 1
                // AUDIT TODO: hardcoded `double` here (and in nearby combinator
                // codegen at lines ~2727, ~2791, ~2889) — these were not
                // converted to the strict-error pattern used by the SQL ops.
                // Each may genuinely fire in some legitimate path (especially
                // dimensional currying / kernels returning arrays); needs
                // case-by-case audit before tightening to errors.
                let elemType = "double"
                
                match kernelName1, kernelName2 with
                | Some k1, Some k2 ->
                    // Both kernels are named C++ lambdas — generate function-call loops
                    let s1Name = sprintf "%s__s1" name
                    let s1Code = [
                        sprintf "%sstatic constexpr const size_t* %s_symm = nullptr;" ind s1Name
                        sprintf "%sconst size_t* %s_extents = %s.extents;" ind s1Name arrName
                        sprintf "%sArray<%s, %d> %s = { allocate<typename promote<%s, %d>::type, %s_symm>(%s_extents), %s_extents };" ind elemType arrRank s1Name elemType arrRank s1Name s1Name s1Name
                        sprintf "%sfor (size_t __i0 = 0; __i0 < %s.extents[0]; __i0++) {" ind arrName
                        sprintf "%s    %s[__i0] = %s(%s[__i0]);" ind s1Name k1 arrName
                        sprintf "%s}" ind
                    ]
                    let s2Code = [
                        sprintf "%sstatic constexpr const size_t* %s_symm = nullptr;" ind name
                        sprintf "%sconst size_t* %s_extents = %s.extents;" ind name s1Name
                        sprintf "%sArray<%s, %d> %s = { allocate<typename promote<%s, %d>::type, %s_symm>(%s_extents), %s_extents };" ind elemType arrRank name elemType arrRank name name name
                        sprintf "%sfor (size_t __i0 = 0; __i0 < %s.extents[0]; __i0++) {" ind s1Name
                        sprintf "%s    %s[__i0] = %s(%s[__i0]);" ind name k2 s1Name
                        sprintf "%s}" ind
                    ]
                    let ctx' = addVarName binding.Id name ctx
                    (s1Code @ [""] @ s2Code, ctx')
                | _ ->
                    // Fallback: kernels are inline lambdas, use ApplyInfo path
                    let s1Name = sprintf "%s__s1" name
                    let s1Id = builder.FreshId()
                    let s1ElemType : IRType =
                        match kernel1 with 
                        | IRLambda li ->
                            match inferExprType li.Body with
                            | IRTScalar _ as t -> t
                            | t -> t  // pass through (could be array, named, etc.)
                        | _ -> IRTScalar ETFloat64
                    let inputArrayTypes = arrays |> List.map (fun a -> 
                        match inferExprType a with 
                        | ArrayElem arr -> arr 
                        | _ -> defaultArrayType (IRTScalar ETFloat64))
                    let totalInputDims = inputArrayTypes |> List.sumBy arrayRank
                    let s1Type = mkArrayArrow [for _ in 1..totalInputDims -> defaultIndexType ()] s1ElemType None
                    let s1Info = buildSimpleApplyInfo arrays kernel1 s1Type
                    let s1Binding = { Id = s1Id; Name = s1Name; Type = s1Type; Value = IRCompute (IRApplyCombinator s1Info); IsConst = true; IsMutable = false }
                    let (code1, ctx1) = genBinding ctx s1Binding builder
                    
                    let s2OutputType =
                        match binding.Type with
                        | IRTUnit ->
                            match s1Type with
                            | ArrayElem arr -> mkArrayLike { arr with ElemType = s1ElemType }
                            | _ -> mkArrayLike (defaultArrayType s1ElemType)
                        | other -> other
                    let s2Info = buildSimpleApplyInfo [IRVar(s1Id, s1Type)] kernel2 s2OutputType
                    let code2 = genApplyCombinator ctx1 name s2Info builder
                    let ctx2 = addVarName binding.Id name ctx1
                    (code1 @ [""] @ code2, ctx2)
            
            | _ ->
                // Normal apply
                let info' = applyFunctorWrappers info functorWrappers
                let code = genApplyCombinator ctx name info' builder
                let ctx' = addVarName binding.Id name ctx
                (code, ctx')
        
        | IRComposeMeth (left, right) ->
            // @>> : sequential composition — compute left, feed result to right's kernel
            // Stage 1: materialize left computation
            let s1Name = sprintf "%s__s1" name
            let s1Id = builder.FreshId()
            
            // Resolve left through deferred
            let rec resolveDeferred e =
                match e with
                | IRVar (id, _) -> 
                    match Map.tryFind id ctx.DeferredComputations with
                    | Some d -> resolveDeferred d
                    | None -> e
                | _ -> e
            let resolvedLeft = resolveDeferred left
            let resolvedRight = resolveDeferred right

            // Extract right's kernel
            let rightKernel = 
                match resolvedRight with 
                | IRApplyCombinator info -> info.Kernel 
                | _ -> resolvedRight
            let rightKernelName = 
                match rightKernel with 
                | IRVar (id, _) -> Map.tryFind id ctx.VarNames 
                | _ -> None
            
            // Materialize left as stage 1
            let s1Type = inferExprType resolvedLeft
            let s1Binding = { Id = s1Id; Name = s1Name; Type = s1Type; Value = IRCompute resolvedLeft; IsConst = true; IsMutable = false }
            let (code1, ctx1) = genBinding ctx s1Binding builder
            
            match rightKernelName with
            | Some kName ->
                // Right kernel is a named function — generate element-wise function-call loop
                let arrRank = match s1Type with ArrayElem arr -> arrayRank arr | _ -> 1
                let elemType = match s1Type with ArrayElem arr -> elemTypeToCpp arr.ElemType | _ -> "double"
                let s2Code = [
                    sprintf "%sstatic constexpr const size_t* %s_symm = nullptr;" ind name
                    sprintf "%sconst size_t* %s_extents = %s.extents;" ind name s1Name
                    sprintf "%sArray<%s, %d> %s = { allocate<typename promote<%s, %d>::type, %s_symm>(%s_extents), %s_extents };" ind elemType arrRank name elemType arrRank name name name
                    sprintf "%sfor (size_t __i0 = 0; __i0 < %s.extents[0]; __i0++) {" ind s1Name
                    sprintf "%s    %s[__i0] = %s(%s[__i0]);" ind name kName s1Name
                    sprintf "%s}" ind
                ]
                let ctx2 = addVarName binding.Id name ctx1
                (code1 @ [""] @ s2Code, ctx2)
            | None ->
                // Right kernel is inline lambda — use buildSimpleApplyInfo path
                let s2Info = buildSimpleApplyInfo [IRVar(s1Id, s1Type)] rightKernel binding.Type
                let code2 = genApplyCombinator ctx1 name s2Info builder
                let ctx2 = addVarName binding.Id name ctx1
                (code1 @ [""] @ code2, ctx2)
        
        | IRBind (comp, cont) ->
            // Monadic bind: c >>= k
            // Stage 1: materialize comp
            let s1Name = sprintf "%s__s1" name
            let s1Id = builder.FreshId()
            
            // Resolve comp through deferred
            let rec resolveDeferred e =
                match e with
                | IRVar (id, _) -> 
                    match Map.tryFind id ctx.DeferredComputations with
                    | Some d -> resolveDeferred d
                    | None -> e
                | _ -> e
            let resolvedComp = resolveDeferred comp
            
            let s1Type = inferExprType resolvedComp
            let s1Binding = { Id = s1Id; Name = s1Name; Type = s1Type; Value = IRCompute resolvedComp; IsConst = true; IsMutable = false }
            let (code1, ctx1) = genBinding ctx s1Binding builder
            
            // Resolve continuation to IRLambda
            let resolvedCont = resolveDeferred cont
            match resolvedCont with
            | IRLambda lInfo when lInfo.Params.Length >= 1 ->
                // Bind lambda parameter to stage 1 result
                let param = lInfo.Params.[0]
                let ctx2 = addVarName param.VarId s1Name ctx1
                
                // Generate code for lambda body as a computation
                let bodyBinding = { Id = binding.Id; Name = name; Type = binding.Type; Value = IRCompute lInfo.Body; IsConst = true; IsMutable = false }
                let (code2, ctx3) = genBinding ctx2 bodyBinding builder
                (code1 @ [""] @ code2, ctx3)
            | _ ->
                // Fallback: continuation not resolvable to lambda — generate function call
                let contName = match cont with IRVar (id, _) -> Map.tryFind id ctx.VarNames | _ -> None
                match contName with
                | Some kName ->
                    let code = [sprintf "%sauto %s = %s(%s);" ind name kName s1Name]
                    let ctx' = addVarName binding.Id name ctx1
                    (code1 @ [""] @ code, ctx')
                | None ->
                    let code = genScalarBinding ctx1 name (IRApp(cont, [IRVar(s1Id, s1Type)], binding.Type)) binding.Type
                    let ctx' = addVarName binding.Id name ctx1
                    (code1 @ [""] @ code, ctx')

        | IRParallel _ ->
            // Parallel composition: recursively generate independent loops, combine as nested pairs
            let (code, _, childrenMap) = genParallelTree ctx name resolved builder
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with TupleChildren = Map.fold (fun acc k v -> Map.add k v acc) ctx'.TupleChildren childrenMap }
            (code, ctx')
        
        | IRFusion _ ->
            // Mandatory fusion: single fused loop nest with all kernels
            let (code, _, childrenMap) = genFusionTree ctx name resolved builder
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with TupleChildren = Map.fold (fun acc k v -> Map.add k v acc) ctx'.TupleChildren childrenMap }
            (code, ctx')
        
        | IRChoice (left, right) ->
            // Computation-level choice: materialize both sides, element-wise combine
            // result[i] = (lhs[i] != 0) ? lhs[i] : rhs[i]
            // If functor wrappers present: f <$> (c1 <|> c2) ≡ (f <$> c1) <|> (f <$> c2)
            let wrapSide side =
                if functorWrappers.IsEmpty then side
                else functorWrappers |> List.fold (fun acc w -> IRFunctorMap(w, acc)) side
            let left' = wrapSide left
            let right' = wrapSide right
            let nameL = sprintf "%s__lhs" name
            let nameR = sprintf "%s__rhs" name
            let idL = builder.FreshId()
            let idR = builder.FreshId()
            let bindingL = { Id = idL; Name = nameL; Type = binding.Type; Value = IRCompute left'; IsConst = true; IsMutable = false }
            let bindingR = { Id = idR; Name = nameR; Type = binding.Type; Value = IRCompute right'; IsConst = true; IsMutable = false }
            let (codeL, ctxL) = genBinding ctx bindingL builder
            let (codeR, ctxR) = genBinding ctxL bindingR builder
            
            let ind = indentStr ctx
            let rank = match binding.Type with ArrayElem arr -> arrayRank arr | _ -> 0
            let elemType = match binding.Type with ArrayElem arr -> elemTypeToCpp arr.ElemType | _ -> "double"
            
            if rank = 0 then
                // Scalar choice
                let code = [sprintf "%s%s %s = (%s != 0) ? %s : %s;" ind elemType name nameL nameL nameR]
                let ctx' = addVarName binding.Id name ctxR
                (codeL @ [""] @ codeR @ [""] @ code, ctx')
            else
                // Array choice: allocate result, element-wise combine.
                // v24d-1: read the source's shape via the wrapper's
                // .extents member; populate name_extents alias for the
                // allocate<> template (which still takes a const size_t*).
                let symmVecName = sprintf "%s_symm" nameL     // reuse left's symmetry
                let extentsAlias = sprintf "%sconst size_t* %s_extents = %s.extents;" ind name nameL
                let symmAlias = sprintf "%sstatic constexpr const size_t* %s_symm = %s;" ind name symmVecName
                let allocDecl = sprintf "%sArray<%s, %d> %s = { allocate<typename promote<%s, %d>::type, %s_symm>(%s_extents), %s_extents };" 
                                    ind elemType rank name elemType rank name name name
                
                // Generate nested loops for element-wise choice
                let mutable loopLines = []
                let mutable depth = ctx.Indent
                let indD d = String.replicate d "    "
                for i in 0 .. rank - 1 do
                    let bound = sprintf "%s.extents[%d]" name i
                    loopLines <- loopLines @ [sprintf "%sfor (size_t __i%d = 0; __i%d < %s; __i%d++) {" (indD depth) i i bound i]
                    depth <- depth + 1
                
                let idxStr = [for i in 0 .. rank - 1 -> sprintf "[__i%d]" i] |> String.concat ""
                let lhsElem = sprintf "%s%s" nameL idxStr
                let rhsElem = sprintf "%s%s" nameR idxStr
                loopLines <- loopLines @ [sprintf "%s%s%s = (%s != 0) ? %s : %s;" (indD depth) name idxStr lhsElem lhsElem rhsElem]
                
                for _ in 0 .. rank - 1 do
                    depth <- depth - 1
                    loopLines <- loopLines @ [sprintf "%s}" (indD depth)]
                
                let ctx' = addVarName binding.Id name ctxR
                (codeL @ [""] @ codeR @ [""] @ [extentsAlias; symmAlias; allocDecl; ""] @ loopLines, ctx')
        
        | IRGuard (cond, body) ->
            // guard(p, c) |> compute: conditionally execute computation
            // Strategy: wrap the kernel body with the guard condition
            // guard(cond, L <@> f) → L <@> (λargs → cond ? f(args) : 0)
            // This allocates the array always but fills with zeros when false
            let isComputation =
                match body with
                | IRApplyCombinator _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ -> true
                | IRVar (id, _) -> Map.containsKey id ctx.DeferredComputations
                | _ -> false
            if isComputation then
                // Resolve the inner computation
                let resolvedBody =
                    match body with
                    | IRVar (id, _) -> Map.tryFind id ctx.DeferredComputations |> Option.defaultValue body
                    | _ -> body
                match resolvedBody with
                | IRApplyCombinator info ->
                    // Wrap kernel: λparams → cond ? kernel_body : 0
                    let wrappedKernel =
                        match info.Kernel with
                        | IRLambda lInfo ->
                            let zeroVal =
                                match inferExprType lInfo.Body with
                                | IRTScalar ETBool -> IRLit (IRLitBool false)
                                | IRTScalar ETInt64 | IRTScalar ETInt32 -> IRLit (IRLitInt 0L)
                                | IRTIdxTagged (IRTScalar (ETInt64 | ETInt32), _) -> IRLit (IRLitInt 0L)
                                | _ -> IRLit (IRLitFloat 0.0)
                            IRLambda { lInfo with Body = IRIf (cond, lInfo.Body, zeroVal) }
                        | other -> other  // Can't wrap non-lambda kernels
                    let guardedInfo = { info with Kernel = wrappedKernel }
                    // Apply any functor wrappers
                    let finalInfo = applyFunctorWrappers guardedInfo functorWrappers
                    let code = genApplyCombinator ctx name finalInfo builder
                    let ctx' = addVarName binding.Id name ctx
                    (code, ctx')
                | _ ->
                    // Non-apply computation (parallel, fusion, etc.) — fall back to scalar guard
                    let guardExpr = IRGuard (cond, body)
                    let code = genScalarBinding ctx name guardExpr binding.Type
                    let ctx' = addVarName binding.Id name ctx
                    (code, ctx')
            else
                // Scalar guard: treat as scalar expression via exprToCpp
                let guardExpr = IRGuard (cond, body)
                let code = genScalarBinding ctx name guardExpr binding.Type
                let ctx' = addVarName binding.Id name ctx
                (code, ctx')
        
        | IRSequence elems ->
            // Homogeneous n-ary parallel: each child produces same type
            // Result is array indexed by Idx<N> containing the child results
            // IMPORTANT: each child generates against the original ctx, not accumulated,
            // to prevent one child's output from contaminating another's array resolution.
            let n = elems.Length
            let childNames = elems |> List.mapi (fun i _ -> sprintf "%s_%d" name i)
            let (allCode, mergedVarNames) =
                (elems, childNames) ||> List.map2 (fun elem childName ->
                    let wrappedElem =
                        if functorWrappers.IsEmpty then elem
                        else functorWrappers |> List.fold (fun acc w -> IRFunctorMap(w, acc)) elem
                    let childType =
                        match wrappedElem with
                        | IRApplyCombinator info -> info.OutputType
                        | _ -> inferExprType wrappedElem
                    let childBinding = { Id = builder.FreshId(); Name = childName; Type = childType; Value = IRCompute wrappedElem; IsConst = true; IsMutable = false }
                    genBinding ctx childBinding builder)
                |> List.fold (fun (accCode, accNames) (code, newCtx) ->
                    (accCode @ code @ [""], Map.fold (fun a k v -> Map.add k v a) accNames newCtx.VarNames)
                ) ([], ctx.VarNames)
            // Determine child element type and rank
            let childType = inferExprType (List.head elems)
            let (childElemType, childRank) =
                match childType with
                | ArrayElem arr -> (elemTypeToCpp arr.ElemType, arrayRank arr)
                | IRTScalar et -> (primTypeToCpp et, 0)
                | _ -> ("double", 0)
            let outerRank = childRank + 1
            // Build extents array: [N, child_extents...]
            let extentsEntries =
                [sprintf "%d" n]
                @ [for d in 0 .. childRank - 1 -> sprintf "%s.extents[%d]" (List.head childNames) d]
            let extentsDecl = sprintf "%ssize_t %s_extents[%d] = {%s};" ind name outerRank (extentsEntries |> String.concat ", ")
            // Allocate pointer array (for array children) or value array (for scalar children),
            // wrapped in Array<T,N>. The `new` allocation produces the underlying data; the
            // wrapper ties it together with the freshly emitted name_extents.
            let allocDecl =
                if childRank > 0 then
                    sprintf "%sArray<%s, %d> %s = { new %s*[%d], %s_extents };" ind childElemType outerRank name childElemType n name
                else
                    sprintf "%sArray<%s, 1> %s = { new %s[%d], %s_extents };" ind childElemType name childElemType n name
            let assignLines =
                childNames |> List.mapi (fun i cn ->
                    sprintf "%s%s[%d] = %s;" ind name i cn)
            let ctx' = { ctx with VarNames = Map.add binding.Id name mergedVarNames }
            (allCode @ [extentsDecl; allocDecl] @ assignLines, ctx')
        
        | _ ->
            // Other compute expressions - treat as scalar
            let code = genScalarBinding ctx name resolved binding.Type
            let ctx' = addVarName binding.Id name ctx
            (code, ctx')
    
    | IRMethodFor _ ->
        // method_for creates a loop object - no runtime code needed
        // Just track the variable name for later use
        let ctx' = addVarName binding.Id name ctx
        ([sprintf "%s// %s = method_for(...) [loop object]" ind name], ctx')
    
    | IRObjectFor _ ->
        // object_for creates a loop object - no runtime code needed
        let ctx' = addVarName binding.Id name ctx
        ([sprintf "%s// %s = object_for(...) [loop object]" ind name], ctx')
    
    | IRApp (IRObjectFor objInfo, args, _) ->
        // Inline application of object_for - need to expand to loop nest
        // This handles cases like: let added = A [+] B
        // Convert to an ApplyCombinator-like structure and generate
        let arrays = 
            match args with
            | [IRTuple elems] -> elems
            | _ -> args
        let code = genObjectForApplication ctx name objInfo arrays builder
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')
    
    | IRLambda info ->
        // Check if body is a computation expression (e.g., method_for(arr) <@> f)
        // Such lambdas are continuation lambdas for >>= — they return computations,
        // not values, so they can't be rendered as simple C++ lambdas.
        // The bind handler inlines their body through DeferredComputations.
        let isCompBody =
            match info.Body with
            | IRApplyCombinator _ | IRBind _ | IRComposeMeth _ | IRComposeObj _
            | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ -> true
            | _ -> false
        if isCompBody then
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
            ([sprintf "%s// %s = <continuation lambda (deferred for >>=)>" ind name], ctx')
        else
        // Generate C++ lambda for named functions
        let paramList = info.Params |> List.map (fun p -> 
            sprintf "%s %s" (irTypeToCpp p.Type) p.Name) |> String.concat ", "
        // Build name map with params and captures
        let bodyNames = info.Params |> List.fold (fun m p -> Map.add p.VarId p.Name m) Map.empty
        let bodyNames = info.Captures |> List.fold (fun m c -> Map.add c.Id c.Name m) bodyNames
        let bodyStr = exprToCpp bodyNames info.Body
        // Return type from binding annotation or inferred from body
        let retType = 
            match binding.Type with
            | FuncElem (_, ret) -> irTypeToCpp ret
            | _ -> irTypeToCpp (inferExprType info.Body)
        // Build std::function type: std::function<RetType(P1Type, P2Type, ...)>
        let paramTypeList = info.Params |> List.map (fun p -> irTypeToCpp p.Type) |> String.concat ", "
        let funcType = sprintf "std::function<%s(%s)>" retType paramTypeList
        let code =
            if isUnitExpr info.Body then
                [sprintf "%s%s %s = [&](%s) { %s; };" ind funcType name paramList bodyStr]
            else
                [sprintf "%s%s %s = [&](%s) -> %s { return %s; };" ind funcType name paramList retType bodyStr]
        let ctx' = addVarName binding.Id name ctx
        // Also store IR for bind (>>=) continuation resolution
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        (code, ctx')
    
    | IRStructLit (typeName, _) ->
        // Struct construction - emit binding then validate() if constrained
        let code = genScalarBinding ctx name binding.Value binding.Type
        let validateCode =
            if ctx.ConstrainedStructs.Contains typeName then
                [sprintf "%s%s.validate();" ind name]
            else []
        let ctx' = addVarName binding.Id name ctx
        (code @ validateCode, ctx')
    
    | IRTupleProj (parentExpr, projIdx, isFlat) ->
        // Check if parent is a deferred computation tuple — if so, project and defer
        let parentDeferred =
            match parentExpr with
            | IRVar (pid, _) -> Map.tryFind pid ctx.DeferredComputations
            | _ -> None
        match parentDeferred with
        | Some (IRTuple elems) when projIdx < elems.Length ->
            // Parent is a deferred tuple — project out the element and defer it
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with DeferredComputations = Map.add binding.Id elems.[projIdx] ctx'.DeferredComputations }
            ([sprintf "%s// %s = <deferred computation (tuple proj)>" ind name], ctx')
        | Some (IRParallel _ | IRFusion _) ->
            // Parent is a deferred combinator — defer the projection too
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
            ([sprintf "%s// %s = <deferred computation (proj of combinator)>" ind name], ctx')
        | _ ->
            // Tuple projection — resolve through TupleChildren map
            let parentName =
                match parentExpr with
                | IRVar (pid, _) -> Map.tryFind pid ctx.VarNames |> Option.defaultValue "_"
                | _ -> "_"
            let parentType = inferExprType parentExpr
            let flatChildren =
                match Map.tryFind parentName ctx.TupleChildren with
                | Some children -> children
                | None -> []

            if isFlat then
                // Flat projection: projIdx is a flat leaf index
                if projIdx < flatChildren.Length then
                    let sourceName = flatChildren.[projIdx]
                    let code = [sprintf "%sauto& %s = %s;" ind name sourceName]
                    let extentsAlias =
                        match IR.stripUnits binding.Type with
                        | ArrayElem _ ->
                            [sprintf "%sconst size_t* %s_extents = %s.extents;" ind name sourceName]
                        | _ -> []
                    let ctx' = addVarName binding.Id name ctx
                    let ctx' =
                        match Map.tryFind sourceName ctx'.TupleChildren with
                        | Some children -> { ctx' with TupleChildren = Map.add name children ctx'.TupleChildren }
                        | None -> ctx'
                    (code @ extentsAlias, ctx')
                else
                    let code = genScalarBinding ctx name binding.Value binding.Type
                    let ctx' = addVarName binding.Id name ctx
                    (code, ctx')
            else
                // Structural projection: projIdx is a type-level index
                let ranges = tupleLeafRanges parentType
                let (flatStart, leafCount) =
                    if projIdx < ranges.Length then ranges.[projIdx]
                    else (projIdx, 1)

                if leafCount > 1 && flatChildren.Length > 0 && flatStart + leafCount <= flatChildren.Length then
                    // Sub-tuple: synthesize from flat children range
                    let subChildren = flatChildren.[flatStart .. flatStart + leafCount - 1]
                    let tupleLine = sprintf "%sauto %s = std::make_tuple(%s);" ind name (subChildren |> String.concat ", ")
                    let ctx' = addVarName binding.Id name ctx
                    let ctx' = { ctx' with TupleChildren = Map.add name subChildren ctx'.TupleChildren }
                    ([tupleLine], ctx')

                elif flatStart < flatChildren.Length then
                    // Single leaf at computed position
                    let sourceName = flatChildren.[flatStart]
                    let code = [sprintf "%sauto& %s = %s;" ind name sourceName]
                    let extentsAlias =
                        match IR.stripUnits binding.Type with
                        | ArrayElem _ ->
                            [sprintf "%sconst size_t* %s_extents = %s.extents;" ind name sourceName]
                        | _ -> []
                    let ctx' = addVarName binding.Id name ctx
                    let ctx' =
                        match Map.tryFind sourceName ctx'.TupleChildren with
                        | Some children -> { ctx' with TupleChildren = Map.add name children ctx'.TupleChildren }
                        | None -> ctx'
                    (code @ extentsAlias, ctx')

                else
                    // No TupleChildren — fall back to std::get
                    let code = genScalarBinding ctx name binding.Value binding.Type
                    let ctx' = addVarName binding.Id name ctx
                    (code, ctx')
    
    | IRVar (srcId, _) ->
        // Check if source is deferred — propagate deferral
        match Map.tryFind srcId ctx.DeferredComputations with
        | Some deferred ->
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with DeferredComputations = Map.add binding.Id deferred ctx'.DeferredComputations }
            ([sprintf "%s// %s = <deferred computation (alias)>" ind name], ctx')
        | None ->
            // Variable reference — may be aliasing a tuple, propagate children
            let srcName = Map.tryFind srcId ctx.VarNames |> Option.defaultValue ""
            let hasTupleChildren = Map.containsKey srcName ctx.TupleChildren
            // Use auto& when source has flat TupleChildren to avoid type mismatch
            let code =
                if hasTupleChildren then
                    [sprintf "%sauto& %s = %s;" ind name srcName]
                else
                    genScalarBinding ctx name binding.Value binding.Type
            let ctx' = addVarName binding.Id name ctx
            let ctx' =
                match Map.tryFind srcName ctx'.TupleChildren with
                | Some children -> { ctx' with TupleChildren = Map.add name children ctx'.TupleChildren }
                | None -> ctx'
            (code, ctx')

    | IRBind (comp, cont) ->
        // Monadic bind: defer if comp is a deferred computation
        let isCompDeferred =
            match comp with
            | IRVar (id, _) -> Map.containsKey id ctx.DeferredComputations
            | IRApplyCombinator _ | IRParallel _ | IRFusion _ | IRFunctorMap _ -> true
            | _ -> false
        if isCompDeferred then
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
            ([sprintf "%s// %s = <deferred bind>" ind name], ctx')
        else
            // Scalar bind: cont(comp)
            let code = genScalarBinding ctx name binding.Value binding.Type
            let ctx' = addVarName binding.Id name ctx
            (code, ctx')

    | IRTuple _ | IRComplex _ | IRFieldAccess _ | IRLit _ | IRBinOp _ | IRUnaryOp _ | IRIf _ | IRApp _ | IRParam _ | IRMatch _
    | IRPure _ | IRIndex _ | IRExtent _ ->
        // Check if it's a tuple of deferred computations
        match binding.Value with
        | IRTuple elems when elems |> List.forall (fun e ->
            match e with
            | IRApplyCombinator _ | IRParallel _ | IRFusion _ | IRFunctorMap _ -> true
            | IRVar (id, _) -> Map.containsKey id ctx.DeferredComputations
            | _ -> false) ->
            // All elements are computations — defer the whole tuple
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
            ([sprintf "%s// %s = <deferred computation tuple>" ind name], ctx')
        | IRFieldAccess _ when (match binding.Type with ArrayElem _ -> true | _ -> false) ->
            // Phase D / v24b: when the value is a struct field of array
            // v24d-1: when the value is a struct field of array type,
            // the field itself is already an Array<T,N> / Ragged<T>
            // wrapper (per genStructDef field rendering). Assigning it
            // to a wrapper-typed binding copies the wrapper, which
            // carries its shape via .extents. No companion alias needed.
            let dataCode = genScalarBinding ctx name binding.Value binding.Type
            let ctx' = addVarName binding.Id name ctx
            (dataCode, ctx')
        | _ ->
            // Scalar expressions including tuples, field access, match, bind, pure
            let code = genScalarBinding ctx name binding.Value binding.Type
            let ctx' = addVarName binding.Id name ctx
            (code, ctx')
    
    | IRCompose _ ->
        // Function composition: uses generic lambdas (auto... args)
        let valueStr = exprToCppCtx ctx binding.Value
        let code = [sprintf "%sauto %s = %s;" ind name valueStr]
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')

    | IRComposeObj _ ->
        // Defer: ObjectLoop composition, materialized when applied via <@>
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred compose_obj>" ind name], ctx')

    | IRComposeMeth _ ->
        // Defer: computation composition, materialized when |> compute
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred compose_meth>" ind name], ctx')
    
    | IRLet _ ->
        // Block expression: unroll the IRLet chain into sequential bindings
        let (lets, finalExpr) = unrollLetChain binding.Value
        let (allCode, foldCtx) =
            lets |> List.fold (fun (accCode, accCtx) (id, value) ->
                let tempName = sprintf "__v%d" id
                let tempBinding = {
                    Id = id; Name = tempName; Type = inferExprType value
                    Value = value; IsConst = true; IsMutable = false
                }
                let (code, ctx') = genBinding accCtx tempBinding builder
                (accCode @ code, ctx')
            ) ([], ctx)
        // Generate the final named binding
        let finalBinding = {
            Id = binding.Id; Name = name; Type = binding.Type
            Value = finalExpr; IsConst = binding.IsConst; IsMutable = binding.IsMutable
        }
        let (finalCode, finalCtx) = genBinding foldCtx finalBinding builder
        (allCode @ finalCode, finalCtx)

    | IRAssign _ ->
        // Assignment expression: generate as statement
        let code = [sprintf "%s%s;" ind (exprToCppCtx ctx binding.Value)]
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')

    | IRForRange (vid, lo, hi, body) ->
        // Imperative for-range loop
        let loStr = exprToCppCtx ctx lo
        let hiStr = exprToCppCtx ctx hi
        let varName = sprintf "__k%d" vid
        let innerCtx = addVarName vid varName ctx
        // Unroll the body IRLet chain into statements
        let (bodyLets, _bodyFinal) = unrollLetChain body
        let (bodyCode, _) =
            bodyLets |> List.fold (fun (accCode, accCtx) (id, value) ->
                let tempName = sprintf "__v%d" id
                let tempBinding = {
                    Id = id; Name = tempName; Type = inferExprType value
                    Value = value; IsConst = true; IsMutable = false
                }
                let (code, ctx') = genBinding { accCtx with Indent = ctx.Indent + 1 } tempBinding builder
                (accCode @ code, { ctx' with Indent = ctx.Indent })
            ) ([], innerCtx)
        let code =
            [sprintf "%sfor (size_t %s = %s; %s < %s; %s++) {" ind varName loStr varName hiStr varName]
            @ bodyCode
            @ [sprintf "%s}" ind]
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')
    
    | other ->
        let ctx' = addVarName binding.Id name ctx
        let nodeType = other.GetType().Name
        (codegenError ctx ind (sprintf "unsupported expression for binding '%s' (IR node: %s)" name nodeType), ctx')

// ============================================================================
// Module Generation
// ============================================================================

/// Generate a function body as a list of C++ statements.
/// Unrolls IRLet chains into sequential variable declarations with a final return.
let genFuncBody (ctx: CodeGenContext) (builder: IRBuilder) (names: Map<IRId, string>) (indent: string) (body: IRExpr) : string list =
    // Deep unroll: flatten all nested IRLet chains into a flat list
    let rec deepUnroll (expr: IRExpr) : (IRId * IRExpr) list * IRExpr =
        match expr with
        | IRLet (id, value, body) ->
            // Check if value itself contains nested IRLets
            let (innerLets, innerFinal) = deepUnroll value
            let (restLets, restFinal) = deepUnroll body
            // If value had nested lets, emit those first, then bind the final value
            match innerLets with
            | [] -> ((id, value) :: restLets, restFinal)
            | _ -> (innerLets @ [(id, innerFinal)] @ restLets, restFinal)
        | _ -> ([], expr)
    let (lets, retExpr) = deepUnroll body
    let mutable currentNames = names
    // Indentation for genApplyCombinator emissions: the function body lives one
    // level deeper than the function declaration's ctx.Indent.
    let bodyIndent = ctx.Indent + 1
    let stmts = lets |> List.collect (fun (id, value) ->
        let varName = sprintf "__v%d" id
        match value with
        | IRForRange (vid, lo, hi, forBody) ->
            let loopVar = sprintf "__k%d" vid
            let loStr = exprToCpp currentNames lo
            let hiStr = exprToCpp currentNames hi
            let innerNames = Map.add vid loopVar currentNames
            let (bodyLets, _) = deepUnroll forBody
            let mutable bodyNames = innerNames
            let bodyStmts = bodyLets |> List.collect (fun (bid, bval) ->
                let bName = sprintf "__v%d" bid
                match bval with
                | IRAssign (target, v) ->
                    let targetStr =
                        match target with
                        | LVVar tid -> Map.tryFind tid bodyNames |> Option.defaultValue (sprintf "__v%d" tid)
                        | _ -> exprToCpp bodyNames target
                    bodyNames <- Map.add bid bName bodyNames
                    [sprintf "%s    %s = %s;" indent targetStr (exprToCpp bodyNames v)]
                | _ ->
                    let valStr = exprToCpp bodyNames bval
                    bodyNames <- Map.add bid bName bodyNames
                    [sprintf "%s    auto %s = %s;" indent bName valStr])
            currentNames <- Map.add id varName currentNames
            [sprintf "%sfor (size_t %s = %s; %s < %s; %s++) {" indent loopVar loStr loopVar hiStr loopVar]
            @ bodyStmts
            @ [sprintf "%s}" indent]
        | IRAssign (target, v) ->
            let targetStr =
                match target with
                | LVVar tid -> Map.tryFind tid currentNames |> Option.defaultValue (sprintf "__v%d" tid)
                | _ -> exprToCpp currentNames target
            currentNames <- Map.add id varName currentNames
            [sprintf "%s%s = %s;" indent targetStr (exprToCpp currentNames v)]
        | IRLit IRLitUnit ->
            // Skip unit literals (side effects already emitted)
            currentNames <- Map.add id varName currentNames
            []
        | IRMethodFor _ | IRObjectFor _ ->
            // Loop objects are compile-time only — they're resolved when <@> is processed
            currentNames <- Map.add id varName currentNames
            []
        | IRApplyCombinator _ ->
            // Unevaluated computations — deferred until |> compute forces them
            currentNames <- Map.add id varName currentNames
            []
        | IRCompute (IRApplyCombinator info) ->
            // Function-body let-binding of `method_for(...) <@> kernel |> compute`.
            // Use the statement-form genApplyCombinator, which emits the full
            // sequence (extents declaration, allocation, loop nest) with
            // `varName_extents` etc. as proper C++ identifiers — so any
            // downstream operation that uses the companion-array convention
            // (reduce's inline IIFE, extents() runtime read, etc.) finds them.
            //
            // The inline genApplyCombinatorExpr can't preserve that convention
            // through an IIFE boundary (companion arrays would be lambda-local,
            // not visible in the enclosing scope) and only handles the 2-array
            // accumulation form anyway. Routing through genApplyCombinator here
            // mirrors what genBinding does at the module level.
            let bodyCtx = { ctx with VarNames = currentNames; Indent = bodyIndent }
            let code = genApplyCombinator bodyCtx varName info builder
            currentNames <- Map.add id varName currentNames
            code
        | IRMask _ | IRIntersect _ | IRUnion _ | IRSort _ ->
            // Phase C lift pass can place an inline form as a let value at
            // function-body level. The same materialization helper used by
            // exprToCpp's IRLet (for kernel-body IIFEs) produces format-
            // neutral statement lines; here we emit them with the function
            // body's indent rather than space-joined inline.
            let arrExpr =
                match value with
                | IRMask (a, _) | IRSort (a, _)
                | IRIntersect (a, _) | IRUnion (a, _) -> a
                | _ -> value
            let elemStr =
                match inferExprType arrExpr with
                | ArrayElem a -> elemTypeToCpp a.ElemType
                | _ -> "double"
            match materializeInlineForm currentNames varName elemStr value with
            | Some matStmts ->
                currentNames <- Map.add id varName currentNames
                matStmts |> List.map (fun s -> indent + s)
            | None ->
                // Defensive: shouldn't fire for the patterns we matched.
                let valStr = exprToCpp currentNames value
                currentNames <- Map.add id varName currentNames
                [sprintf "%sauto %s = %s;" indent varName valStr]
        | _ ->
            let valStr = exprToCpp currentNames value
            currentNames <- Map.add id varName currentNames
            [sprintf "%sauto %s = %s;" indent varName valStr])
    if isUnitExpr retExpr then
        stmts  // Void function: no return statement needed
    else
        // If the return expression is `compute(applyCombinator)`, synthesize
        // an internal let binding so the statement-form genApplyCombinator
        // emits the full sequence (extents/alloc + loop nest for array
        // output, scalar accumulator + loop nest for scalar output) and we
        // return the bound name. This unifies both shapes through the same
        // LoopNestCodeGen machinery; without it, exprToCpp would route
        // through the inline expression-form genApplyCombinatorExpr — which
        // is still a hardcoded 2-array IIFE special case kept for inline
        // expression contexts that lack a surrounding statement scope (a
        // separate cleanup will fold that into a wrapper around this path).
        match retExpr with
        | IRCompute (IRApplyCombinator info) ->
            let retVarName = sprintf "__ret%d" (builder.FreshId())
            let bodyCtx = { ctx with VarNames = currentNames; Indent = ctx.Indent + 1 }
            let combCode = genApplyCombinator bodyCtx retVarName info builder
            stmts @ combCode @ [sprintf "%sreturn %s;" indent retVarName]
        | IRArrayLit (elements, arrType) ->
            // Array literal as return value: lift to a local binding, then
            // return. `genArrayLiteral` is the statement-form generator for
            // IRArrayLit (extents-table + allocate-call + per-element init);
            // exprToCpp has no inline form for it, so without this lift the
            // return falls through to the unsupported-IR-node sentinel.
            //
            // The lifted binding gets a synthetic __retN name so it can't
            // collide with user names. Return-by-value of the Array<T,N>
            // wrapper copies the pointer; the underlying buffer (allocated
            // via allocate<>) lives on the heap so the caller receives a
            // valid array.
            let retVarName = sprintf "__ret%d" (builder.FreshId())
            let bodyCtx = { ctx with VarNames = currentNames; Indent = ctx.Indent + 1 }
            let arrayCode = genArrayLiteral bodyCtx retVarName elements arrType
            stmts @ arrayCode @ [sprintf "%sreturn %s;" indent retVarName]
        | _ ->
            let retStr = exprToCpp currentNames retExpr
            stmts @ [sprintf "%sreturn %s;" indent retStr]

let genFuncDef (ctx: CodeGenContext) (builder: IRBuilder) (funcDef: IRFuncDef) : string list * CodeGenContext =
    let ind = indentStr ctx
    let bodyInd = ind + "    "
    
    // v24d-1: array parameters are wrappers. Body reads shape via
    // `<name>.extents[d]`, `<name>.lens[d]`, `<name>.offsets[d]`. No need
    // for separate body-level aliases — the wrapper IS the binding.
    let paramList = 
        funcDef.Params 
        |> List.map (fun p -> 
            match p.Type with
            | ArrayElem arr when isRaggedArrayType arr || isDepIdxArrayType arr ->
                sprintf "Ragged<%s> %s" (elemTypeToCpp arr.ElemType) p.Name
            | ArrayElem arr ->
                sprintf "Array<%s, %d> %s" (elemTypeToCpp arr.ElemType) (arrayRank arr) p.Name
            | _ ->
                sprintf "%s %s" (irTypeToCpp p.Type) p.Name)
        |> String.concat ", "
    
    // Use declared return type, or infer from body as fallback
    let retType = 
        match funcDef.RetType with
        | IRTInfer _ -> irTypeToCpp (inferExprType funcDef.Body)  // Should not happen with typed IR
        | ArrayElem arr when isRaggedArrayType arr || isDepIdxArrayType arr ->
            sprintf "Ragged<%s>" (elemTypeToCpp arr.ElemType)
        | ArrayElem arr ->
            sprintf "Array<%s, %d>" (elemTypeToCpp arr.ElemType) (arrayRank arr)
        | t -> irTypeToCpp t
    
    // Build name map with params on top of module-level names
    let bodyNames = funcDef.Params |> List.fold (fun m p -> Map.add p.VarId p.Name m) ctx.VarNames
    
    // Generate proper C++ function
    let safeName = sanitizeCppName funcDef.Name
    let bodyStmts = genFuncBody ctx builder bodyNames bodyInd funcDef.Body
    let code =
        [sprintf "%s%s %s(%s) {" ind retType safeName paramList]
        @ bodyStmts
        @ [sprintf "%s}" ind]
    
    let ctx' = addVarName funcDef.Id funcDef.Name ctx
    (code, ctx')

/// Generate a function as a C++ lambda (for functions that capture module-level bindings)
let genFuncDefAsLambda (ctx: CodeGenContext) (funcDef: IRFuncDef) : string list * CodeGenContext =
    let ind = indentStr ctx
    
    // v24c: array params are wrappers; same approach as genFuncDef.
    let paramList = 
        funcDef.Params 
        |> List.map (fun p -> 
            match p.Type with
            | ArrayElem arr when isRaggedArrayType arr || isDepIdxArrayType arr ->
                sprintf "Ragged<%s> %s" (elemTypeToCpp arr.ElemType) p.Name
            | ArrayElem arr ->
                sprintf "Array<%s, %d> %s" (elemTypeToCpp arr.ElemType) (arrayRank arr) p.Name
            | _ ->
                sprintf "%s %s" (irTypeToCpp p.Type) p.Name)
        |> String.concat ", "
    
    let retType = 
        match funcDef.RetType with
        | IRTInfer _ -> irTypeToCpp (inferExprType funcDef.Body)
        | ArrayElem arr when isRaggedArrayType arr || isDepIdxArrayType arr ->
            sprintf "Ragged<%s>" (elemTypeToCpp arr.ElemType)
        | ArrayElem arr ->
            sprintf "Array<%s, %d>" (elemTypeToCpp arr.ElemType) (arrayRank arr)
        | t -> irTypeToCpp t
    
    let bodyNames = funcDef.Params |> List.fold (fun m p -> Map.add p.VarId p.Name m) ctx.VarNames
    let bodyStr = exprToCpp bodyNames funcDef.Body
    let safeName = sanitizeCppName funcDef.Name
    // std::function type with one param type per Blade param (no companion args).
    let paramTypeList =
        funcDef.Params
        |> List.map (fun p ->
            match p.Type with
            | ArrayElem arr when isRaggedArrayType arr || isDepIdxArrayType arr ->
                sprintf "Ragged<%s>" (elemTypeToCpp arr.ElemType)
            | ArrayElem arr ->
                sprintf "Array<%s, %d>" (elemTypeToCpp arr.ElemType) (arrayRank arr)
            | _ -> irTypeToCpp p.Type)
        |> String.concat ", "
    let funcType = sprintf "std::function<%s(%s)>" retType paramTypeList
    let code =
        if isUnitExpr funcDef.Body then
            [sprintf "%s%s %s = [&](%s) { %s; };" ind funcType safeName paramList bodyStr]
        else
            [sprintf "%s%s %s = [&](%s) -> %s { return %s; };" ind funcType safeName paramList retType bodyStr]
    let ctx' = addVarName funcDef.Id funcDef.Name ctx
    (code, ctx')

/// Generate C++ code for an entire IR module.
/// Returns (functionDefs, bindingCode) — functions go outside main(), bindings inside.
let genModule (modul: IRModule) (builder: IRBuilder) : string list * string list =
    // Phase D / companion-array gap: populate the codegen-side struct fields
    // cache so inferExprType can resolve IRFieldAccess result types.
    setCodegenStructFieldsCache modul.Types
    
    let ctx0 = emptyContext ()
    
    // Collect constrained struct names from type definitions
    let constrainedNames = 
        modul.Types |> List.choose (function
            | IRTDStruct (name, _, Some _) -> Some name
            | _ -> None)
        |> Set.ofList
    let ctx0 = { ctx0 with ConstrainedStructs = constrainedNames }
    
    // First pass: register ALL names (both bindings and functions) in context
    let ctx0 =
        modul.Bindings |> List.fold (fun c b -> addVarName b.Id b.Name c) ctx0
    let ctx0 =
        modul.Functions |> List.fold (fun c f -> addVarName f.Id f.Name c) ctx0
    
    // Build a combined list of items with their "order" based on ID
    // Lower IDs were created earlier in the source
    let bindingItems = modul.Bindings |> List.map (fun b -> (b.Id, Choice1Of2 b))
    let funcItems = modul.Functions |> List.map (fun f -> (f.Id, Choice2Of2 f))
    let allItems = bindingItems @ funcItems |> List.sortBy fst
    
    // Build set of all function IDs (to exclude from free var checks)
    let funcIds = modul.Functions |> List.map (fun f -> f.Id) |> Set.ofList
    
    // Generate in ID order (approximates source order)
    // First, collect file-scope functions to generate forward declarations
    let hasFreeVarsCheck (funcDef: IRFuncDef) (c: CodeGenContext) =
        let paramIds = funcDef.Params |> List.map (fun p -> p.VarId) |> Set.ofList
        let freeVars = Set.difference (collectVarRefs funcDef.Body) (Set.union paramIds funcIds)
        freeVars |> Set.exists (fun id -> Map.containsKey id c.VarNames)
    
    let fileScopeFuncs =
        allItems |> List.choose (fun (_, item) ->
            match item with
            | Choice2Of2 funcDef when not (hasFreeVarsCheck funcDef ctx0) -> Some funcDef
            | _ -> None)
    
    // Generate forward declarations for all file-scope functions.
    // v24c: signature uses Array<T,N> / Ragged<T> wrappers, single arg per
    // Blade param. Must match genFuncDef.
    let forwardDecls =
        fileScopeFuncs |> List.map (fun funcDef ->
            let paramList = 
                funcDef.Params 
                |> List.map (fun p -> 
                    match p.Type with
                    | ArrayElem arr when isRaggedArrayType arr || isDepIdxArrayType arr ->
                        sprintf "Ragged<%s> %s" (elemTypeToCpp arr.ElemType) p.Name
                    | ArrayElem arr ->
                        sprintf "Array<%s, %d> %s" (elemTypeToCpp arr.ElemType) (arrayRank arr) p.Name
                    | _ ->
                        sprintf "%s %s" (irTypeToCpp p.Type) p.Name)
                |> String.concat ", "
            let retType = 
                match funcDef.RetType with
                | IRTInfer _ -> irTypeToCpp (inferExprType funcDef.Body)
                | ArrayElem arr when isRaggedArrayType arr || isDepIdxArrayType arr ->
                    sprintf "Ragged<%s>" (elemTypeToCpp arr.ElemType)
                | ArrayElem arr ->
                    sprintf "Array<%s, %d>" (elemTypeToCpp arr.ElemType) (arrayRank arr)
                | t -> irTypeToCpp t
            let safeName = sanitizeCppName funcDef.Name
            sprintf "%s %s(%s);" retType safeName paramList)
    let forwardDecls = if forwardDecls.IsEmpty then [] else forwardDecls @ [""]
    
    let (funcCode, bindCode, finalCtx) =
        allItems |> List.fold (fun (fc, bc, c) (_, item) ->
            match item with
            | Choice1Of2 binding ->
                let (code, c') = genBinding c binding builder
                (fc, bc @ code @ [""], c')
            | Choice2Of2 funcDef ->
                if hasFreeVarsCheck funcDef c then
                    let (code, c') = genFuncDefAsLambda c funcDef
                    (fc, bc @ code @ [""], c')
                else
                    let (code, c') = genFuncDef c builder funcDef
                    (fc @ code @ [""], bc, c')
        ) (forwardDecls, [], ctx0)
    
    // Merge context warnings into module-level collector
    exprWarnings.Value <- exprWarnings.Value @ finalCtx.Warnings.Value
    
    (funcCode, bindCode)

/// Generate a complete C++ program with main() from an IR module
let genStructDef (name: string) (fields: (string * IRType) list) (invariant: StructConstraintInfo option) : string list =
    let fieldLines = fields |> List.map (fun (fname, fty) ->
        // Array-typed fields render as Array<T,N> / Ragged<T> wrappers
        // (v24b refactor) so the field carries its shape with it. Other
        // types use the standard irTypeToCpp rendering.
        let cppTy =
            match fty with
            | ArrayElem arr when isRaggedArrayType arr || isDepIdxArrayType arr ->
                sprintf "Ragged<%s>" (elemTypeToCpp arr.ElemType)
            | ArrayElem arr ->
                sprintf "Array<%s, %d>" (elemTypeToCpp arr.ElemType) (arrayRank arr)
            | _ -> irTypeToCpp fty
        sprintf "    %s %s;" cppTy fname)
    let validateLines =
        match invariant with
        | Some info ->
            // Build name map: IRVar IDs in invariant -> field names
            let nameMap = info.FieldBindings |> List.fold (fun m (fname, fid) -> Map.add fid fname m) Map.empty
            let constraintStr = exprToCpp nameMap info.Expr
            ["    void validate() const {"
             sprintf "        if (!(%s)) {" constraintStr
             sprintf "            std::cerr << \"Constraint violation in %s\" << std::endl;" name
             "            std::abort();"
             "        }"
             "    }"]
        | None -> []
    [sprintf "struct %s {" name]
    @ fieldLines
    @ validateLines
    @ ["};"
       ""]

/// Generate type definitions for a module
let genTypeDefs (modul: IRModule) : string list =
    modul.Types |> List.collect (function
        | IRTDStruct (name, fields, invariant) -> genStructDef name fields invariant
        | IRTDVariant (name, variants) ->
            // Check if any variant has data
            let hasData = variants |> List.exists (fun (_, d) -> d.IsSome)
            if hasData then
                // Tagged union using std::variant
                // Generate wrapper structs for each variant (with _T suffix to avoid name clash)
                let variantStructs = variants |> List.collect (fun (vname, data) ->
                    match data with
                    | Some ty -> 
                        [sprintf "struct %s_T { %s value; };" vname (irTypeToCpp ty)]
                    | None -> 
                        [sprintf "struct %s_T {};" vname]
                )
                // Generate the variant type alias
                let variantTypes = variants |> List.map (fun (v, _) -> v + "_T") |> String.concat ", "
                let variantAlias = sprintf "using %s = std::variant<%s>;" name variantTypes
                // Generate constructor functions for variants with data
                let ctorFuncs = variants |> List.collect (fun (vname, data) ->
                    match data with
                    | Some ty ->
                        [sprintf "inline %s %s(%s v) { return %s_T{v}; }" name vname (irTypeToCpp ty) vname]
                    | None ->
                        [sprintf "const %s %s = %s_T{};" name vname vname]
                )
                variantStructs @ [variantAlias] @ ctorFuncs @ [""]
            else
                // Simple enum - use plain enum for unscoped names
                [sprintf "enum %s { %s };" name 
                    (variants |> List.map fst |> String.concat ", ")
                 ""]
        | IRTDAlias (name, ty) ->
            [sprintf "using %s = %s;" name (irTypeToCpp ty); ""]
        | IRTDIndexType (name, _) ->
            // Emit a typedef for the index type so foreign-key element types
            // (IRTIdxTagged (_, IRefNamed name)) can render as the alias
            // rather than bare int64_t. The alias is transparent —
            // int64_t-compatible — but makes generated C++ self-documenting
            // and leaves a hook for future strong typing.
            [sprintf "using %s = int64_t;" name; ""]
        | IRTDEnumIdx (name, _, values) ->
            // EnumIdx alias: render as the underlying runtime type. All-int
            // values → int64_t; all-string values → std::string. The chosen
            // C++ type must match what the Case 2 reverse-lookup dispatch
            // and any keys array stored under this type expect.
            let underlying = IR.EnumValue.underlyingElemType values
            [sprintf "using %s = %s;" name (primTypeToCpp underlying); ""]
    )

/// Generate code to print a scalar value
let genPrintScalar (name: string) : string list =
    [sprintf "    cout << \"%s = \" << %s << endl;" name name]

/// Generate code to print an array value (flattened for easy parsing)
let genPrintArrayFlat (name: string) (rank: int) : string list =
    let firstVar = sprintf "%s__first" name
    if rank < 1 then
        [sprintf "    cout << \"%s = <rank-0>\" << endl;" name]
    elif rank <= 3 then
        // Ranks 1-3: flat comma-separated output
        let loopVars = [| "i"; "j"; "k" |]
        let opens = [
            sprintf "    cout << \"%s = [\";" name
            sprintf "    bool %s = true;" firstVar ]
        let loops =
            [ for d in 0 .. rank - 1 ->
                sprintf "    %sfor (size_t %s = 0; %s < %s.extents[%d]; %s++) {"
                    (String.replicate d "    ") loopVars.[d] loopVars.[d] name d loopVars.[d] ]
        let inner =
            let ind = "    " + String.replicate rank "    "
            let idx = loopVars.[0..rank-1] |> Array.map (sprintf "[%s]") |> String.concat ""
            [ sprintf "%sif (!%s) cout << \", \";" ind firstVar
              sprintf "%s%s = false;" ind firstVar
              sprintf "%scout << %s%s;" ind name idx ]
        let closes =
            [ for d in rank - 1 .. -1 .. 0 ->
                sprintf "    %s}" (String.replicate d "    ") ]
        let finish = [ sprintf "    cout << \"]\" << endl;" ]
        opens @ loops @ inner @ closes @ finish
    elif rank = 4 then
        // Rank 4: 2D grid of 2D blocks
        // Print as: name[i][j] = [ row0; row1; ... ] for each (i,j)
        [
            sprintf "    cout << \"%s (\" << %s.extents[0] << \"x\" << %s.extents[1] << \"x\" << %s.extents[2] << \"x\" << %s.extents[3] << \"):\" << endl;"
                name name name name name
            sprintf "    for (size_t i = 0; i < %s.extents[0]; i++) {" name
            sprintf "        for (size_t j = 0; j < %s.extents[1]; j++) {" name
            sprintf "            cout << \"  %s[\" << i << \"][\" << j << \"] = [\";" name
            sprintf "            bool %s = true;" firstVar
            sprintf "            for (size_t k = 0; k < %s.extents[2]; k++) {" name
            sprintf "                for (size_t l = 0; l < %s.extents[3]; l++) {" name
            sprintf "                    if (!%s) cout << \", \";" firstVar
            sprintf "                    %s = false;" firstVar
            sprintf "                    cout << %s[i][j][k][l];" name
            "                }"
            "            }"
            sprintf "            cout << \"]\" << endl;"
            "        }"
            "    }"
        ]
    else
        // Rank 5+: just print total size and first few elements
        [
            sprintf "    cout << \"%s = <rank-%d array>\" << endl;" name rank
        ]

/// Generate print loop for arrays with per-dimension symmetry awareness.
/// Expands IRIndexType list into per-dimension loop structure:
///   - SymIdx<k,n>: k dims, first is free range, rest subtract prior vars in group
///   - Idx<n>: 1 dim, free range
/// This correctly handles mixed symmetry (e.g. SymIdx<2> + Idx).
let genPrintArraySymAware (name: string) (indexTypes: IRIndexType list) : string list =
    // Expand index types into per-dimension info: (loopVar, dimIdx, offsetVars)
    // offsetVars = list of loop vars to subtract from extent (empty for free dims)
    let loopVarNames = [| "i"; "j"; "k"; "l"; "m"; "n_"; "p"; "q" |]
    let dims =
        indexTypes |> List.fold (fun (acc, dimIdx) idx ->
            let arity = max 1 idx.Arity
            let isSym = idx.Symmetry = SymSymmetric || idx.Symmetry = SymAntisymmetric
            let groupDims =
                [0 .. arity - 1] |> List.map (fun a ->
                    let loopVar = if dimIdx + a < loopVarNames.Length then loopVarNames.[dimIdx + a] else sprintf "d%d" (dimIdx + a)
                    let offsets =
                        if isSym && a > 0 then
                            [0 .. a - 1] |> List.map (fun prev -> loopVarNames.[dimIdx + prev])
                        else []
                    (loopVar, dimIdx + a, offsets))
            (acc @ groupDims, dimIdx + arity)
        ) ([], 0) |> fst
    let rank = dims.Length
    if rank < 1 || rank > 8 then
        [sprintf "    cout << \"%s = <rank-%d array>\" << endl;" name rank]
    else
        let firstVar = sprintf "%s__first" name
        let opens = [
            sprintf "    cout << \"%s = [\";" name
            sprintf "    bool %s = true;" firstVar ]
        let loops =
            dims |> List.map (fun (loopVar, dimIdx, offsets) ->
                let indent = "    " + String.replicate (dims |> List.findIndex (fun (v,_,_) -> v = loopVar)) "    "
                let bound =
                    match offsets with
                    | [] -> sprintf "%s.extents[%d]" name dimIdx
                    | _ ->
                        let sub = offsets |> String.concat " - "
                        sprintf "%s.extents[%d] - %s" name dimIdx sub
                sprintf "%sfor (size_t %s = 0; %s < %s; %s++) {" indent loopVar loopVar bound loopVar)
        let innerIndent = "    " + String.replicate rank "    "
        let idx = dims |> List.map (fun (v,_,_) -> sprintf "[%s]" v) |> String.concat ""
        let inner = [
            sprintf "%sif (!%s) cout << \", \";" innerIndent firstVar
            sprintf "%s%s = false;" innerIndent firstVar
            sprintf "%scout << %s%s;" innerIndent name idx ]
        let closes =
            [for d in rank - 1 .. -1 .. 0 ->
                sprintf "    %s}" (String.replicate d "    ")]
        let finish = [sprintf "    cout << \"]\" << endl;"]
        opens @ loops @ inner @ closes @ finish

/// Compute which binding IDs are deferred (no C++ code generated).
/// A binding is deferred if it's a computation that hasn't been materialized via |> compute.
let computeDeferredIds (bindings: IRBinding list) : Set<int> =
    let isDeferred (ids: Set<int>) (e: IRExpr) =
        match e with
        | IRApplyCombinator _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ | IRComposeObj _ | IRComposeMeth _ | IRBind _ | IRZip _ | IRSequence _ -> true
        | IRVar (id, _) -> Set.contains id ids
        | _ -> false
    bindings |> List.fold (fun ids b ->
        let shouldDefer =
            match b.Value with
            | IRApplyCombinator _ | IRParallel _ | IRFusion _ -> true
            | IRZip _ -> true
            | IRComposeObj _ -> true
            | IRBind (comp, _) -> isDeferred ids comp
            | IRComposeMeth (left, right) -> isDeferred ids left || isDeferred ids right
            | IRFunctorMap (_, inner) -> isDeferred ids inner
            | IRChoice (left, right) -> isDeferred ids left || isDeferred ids right
            | IRGuard (_, body) ->
                let rec leafIsDeferred e =
                    match e with
                    | IRGuard (_, inner) -> leafIsDeferred inner
                    | _ -> isDeferred ids e
                leafIsDeferred body
            | IRSequence elems -> elems |> List.exists (isDeferred ids)
            | IRTuple elems -> elems |> List.forall (isDeferred ids)
            | IRTupleProj (IRVar (pid, _), _, _) -> Set.contains pid ids
            | IRVar (srcId, _) -> Set.contains srcId ids
            | _ -> false
        if shouldDefer then Set.add b.Id ids else ids
    ) Set.empty

let genPrintStatements (modul: IRModule) : string list =
    let deferredIds = computeDeferredIds modul.Bindings
    modul.Bindings |> List.collect (fun b ->
        let isPrintable = 
            if Set.contains b.Id deferredIds then false
            else
            match b.Value with
            | IRCompute (IRApplyCombinator _) -> true
            | IRCompute (IRParallel _) -> true
            | IRCompute (IRFusion _) -> true
            | IRCompute (IRVar _) -> true
            | IRCompute (IRFunctorMap _) -> true
            | IRCompute (IRChoice _) -> true
            | IRCompute (IRComposeMeth _) -> true
            | IRCompute (IRBind _) -> true
            | IRCompute (IRGuard _) -> true
            | IRCompute (IRSequence _) -> true
            | IRCompute _ | IRMethodFor _ | IRObjectFor _ | IRLambda _ -> false
            | _ -> true
        
        let hasSymmetry =
            match IR.stripUnits b.Type with
            | ArrayElem arr ->
                arr.IndexTypes |> List.exists (fun idx ->
                    idx.Symmetry = SymSymmetric || idx.Symmetry = SymAntisymmetric)
            | _ -> false
        
        if isPrintable then
            match IR.stripUnits b.Type with
            | IRTScalar (ETFloat64 | ETFloat32 | ETInt64 | ETInt32 | ETBool | ETComplex64 | ETComplex128 | ETString) ->
                genPrintScalar b.Name
            | IRTIdxTagged _ ->
                // Tagged int prints same as int
                genPrintScalar b.Name
            | ArrayElem arrType ->
                // Phase D: arrays of named (struct) types — cout's operator<<
                // isn't defined for user types. For rank-1 arrays of structs,
                // emit a per-field print loop driven by the IRTDStruct's
                // declared field list (looked up from modul.Types). Format:
                //   name = [{f1: V, f2: V}, {f1: V, f2: V}, ...]
                // Higher ranks fall back to a skip comment for now;
                // value-checks via scalar field reads still work in either
                // case.
                match arrType.ElemType with
                | FuncElem _ ->
                    // Arrays of functions have no general `operator<<`.
                    // std::function isn't streamable, and a generic print
                    // would need either signature-specific formatting or
                    // address-printing — neither is meaningful for testing.
                    // Skip with a diagnostic comment so the surrounding
                    // value-check on scalar results derived from calls
                    // (e.g. `let r = funcs(1)(5.0)`) still runs.
                    [sprintf "    // (array '%s' of function values not auto-printed; std::function isn't streamable)" b.Name]
                | IRTNamed structName ->
                    let rank = arrayRank arrType
                    let structFields =
                        modul.Types |> List.tryPick (fun td ->
                            match td with
                            | IRTDStruct (n, fs, _) when n = structName -> Some fs
                            | _ -> None)
                    match structFields, rank with
                    | Some fields, 1 when not (List.isEmpty fields) ->
                        let firstVar = sprintf "%s__first" b.Name
                        let fieldPrints =
                            fields |> List.mapi (fun i (fname, _) ->
                                let prefix = if i = 0 then "" else ", "
                                sprintf "        cout << \"%s%s: \" << %s[i].%s;" prefix fname b.Name fname)
                        [
                            sprintf "    cout << \"%s = [\";" b.Name
                            sprintf "    bool %s = true;" firstVar
                            sprintf "    for (size_t i = 0; i < %s.extents[0]; i++) {" b.Name
                            sprintf "        if (!%s) cout << \", \";" firstVar
                            sprintf "        %s = false;" firstVar
                            sprintf "        cout << \"{\";"
                        ]
                        @ fieldPrints
                        @ [
                            sprintf "        cout << \"}\";"
                            "    }"
                            sprintf "    cout << \"]\" << endl;"
                        ]
                    | _ ->
                        // Struct not found in module Types, or rank > 1, or
                        // no fields — emit diagnostic comment and skip.
                        [sprintf "    // (array '%s' of struct '%s' not auto-printed; access individual fields via %s[i].field)" b.Name structName b.Name]
                | _ ->
                let rank = arrayRank arrType
                // Distinguish three cases for ragged-tagged bindings, based on
                // what C++ metadata exists for each:
                //
                //   (a) Ragged literal binding: Value is IRArrayLit with ragged
                //       shape. genArrayLiteral emitted both _lens and _extents.
                //       Use the ragged print loop.
                //
                //   (b) Ragged-peel output: Value is IRApplyCombinator whose
                //       codegen path emitted _extents but not _lens. The
                //       runtime shape is rank-1 rectangular (one scalar per
                //       outer iteration). Use flat print.
                //
                //   (c) Sub-view binding: Value is IRIndex or similar (e.g.,
                //       r(1)). Neither _lens nor _extents was emitted; print
                //       would reference undefined names. Skip.
                let isRaggedLiteralBinding =
                    (isRaggedArrayType arrType || isDepIdxArrayType arrType) &&
                    (match b.Value with
                     | IRArrayLit _ -> true
                     | _ -> false)
                // Look through IRCompute wrappers (from |> compute) to find
                // the underlying combinator. Also check |> bind continuations
                // and other materialization wrappers as needed.
                let rec unwrapMaterialization (e: IRExpr) : IRExpr =
                    match e with
                    | IRCompute inner -> unwrapMaterialization inner
                    | _ -> e
                let isRaggedPeelOutput =
                    isRaggedArrayType arrType &&
                    (match unwrapMaterialization b.Value with
                     | IRApplyCombinator _ -> true
                     | _ -> false)
                if isRaggedLiteralBinding then
                    // Ragged: iterate using offsets table. Print as flat
                    // comma-separated values across all rows; this matches
                    // the validation framework's expectation of a flat list.
                    let firstVar = sprintf "%s__first" b.Name
                    [
                        sprintf "    cout << \"%s = [\";" b.Name
                        sprintf "    bool %s = true;" firstVar
                        sprintf "    for (size_t __ri = 0; __ri < %s.extents[0]; __ri++) {" b.Name
                        sprintf "        for (size_t __rj = 0; __rj < %s.lens[__ri]; __rj++) {" b.Name
                        sprintf "            if (!%s) cout << \", \";" firstVar
                        sprintf "            %s = false;" firstVar
                        sprintf "            cout << %s[__ri][__rj];" b.Name
                        "        }"
                        "    }"
                        "    cout << \"]\" << endl;"
                    ]
                elif isRaggedPeelOutput then
                    // Peel output is rank-1 rectangular at runtime; flat print
                    // works regardless of the type-level rank/tag.
                    genPrintArrayFlat b.Name 1
                elif isRaggedArrayType arrType then
                    // Sub-view binding: no metadata to drive a print loop.
                    // Skip rather than emit broken code. Scalar derivations
                    // from the sub-view still print normally.
                    [sprintf "    // (sub-view of ragged array '%s' not printed; metadata not propagated)" b.Name]
                elif hasSymmetry && rank >= 2 && rank <= 8 then
                    genPrintArraySymAware b.Name arrType.IndexTypes
                else
                    genPrintArrayFlat b.Name rank
            | IRTTuple _ -> []
            | IRTNamed _ -> []
            | IRTUnit -> []
            | _ -> []
        else []
    )

/// Assemble the main() function wrapper around binding code and print statements.
let genMainWrapper (testName: string) (bodyIndented: string list) (printCode: string list) : string list =
    ["int main() {"
     "    cout << std::setprecision(15);"
     "    cout << std::boolalpha;"
     "    auto start = TIME;"
     ""]
    @ bodyIndented
    @ [""
       "    auto end = TIME;"
       "    double elapsed = 1e-9 * TIME_DIFF;"
       sprintf "    cout << \"%s completed in \" << elapsed << \"s\" << endl;" testName
       ""
       "    // Print results for verification"]
    @ printCode
    @ [""
       "    return 0;"
       "}"]

/// Generate a C++ program (uses external runtime header)
/// Generate print statements for all bindings in a module.
/// Shared by genSelfContainedProgram and genProgramWithExternalRuntime.
let genMainProgram (modul: IRModule) (testName: string) : string =
    exprWarnings.Value <- []
    let builder = IRBuilder()
    
    let includes = genIncludes ()
    let (funcDefs, bindCode) = genModule modul builder
    
    let bodyIndented = bindCode |> List.map (fun s -> "    " + s)
    let mainFunc = genMainWrapper testName bodyIndented []
    
    (includes @ [""] @ funcDefs @ mainFunc) |> String.concat "\n"

/// Generate a complete C++ program from an IR program (all modules)
let genProgramFromIR (program: IRProgram) (testName: string) : string =
    match program.Modules with
    | [] -> "// Empty program\nint main() { return 0; }\n"
    | [modul] -> genMainProgram modul testName
    | modules ->
        let merged = {
            Name = "merged"
            Types = modules |> List.collect (fun m -> m.Types)
            Functions = modules |> List.collect (fun m -> m.Functions)
            Bindings = modules |> List.collect (fun m -> m.Bindings)
            StaticFunctionUsage = Map.empty
        }
        genMainProgram merged testName

/// Generate C++ struct definition from IRTDStruct
let genSelfContainedProgram (modul: IRModule) (testName: string) : string =
    let builder = IRBuilder()
    
    let includes = genIncludesExternal ()
    let typeDefs = genTypeDefs modul
    let (funcDefs, bindCode) = genModule modul builder
    
    let bodyIndented = bindCode |> List.map (fun s -> "    " + s)
    let printCode = genPrintStatements modul
    let mainFunc = genMainWrapper testName bodyIndented printCode
    
    (includes @ typeDefs @ [""] @ funcDefs @ mainFunc) |> String.concat "\n"

/// Generate a C++ program with external runtime header
/// Returns (mainFileContent, headerFileContent)
let genProgramWithExternalRuntime (modul: IRModule) (testName: string) : string * string =
    let builder = IRBuilder()
    
    let includes = genIncludesExternal ()
    let typeDefs = genTypeDefs modul
    let (funcDefs, bindCode) = genModule modul builder
    
    let bodyIndented = bindCode |> List.map (fun s -> "    " + s)
    let printCode = genPrintStatements modul
    let mainFunc = genMainWrapper testName bodyIndented printCode
    
    let mainFile = (includes @ typeDefs @ [""] @ funcDefs @ mainFunc) |> String.concat "\n"
    let headerFile = genRuntimeHeader ()
    (mainFile, headerFile)

/// Generate a self-contained C++ program from an IR program
let genSelfContainedProgramFromIR (program: IRProgram) (testName: string) : string * string list =
    // Reset module-level expression warnings
    exprWarnings.Value <- []
    let code =
        match program.Modules with
        | [] -> "// Empty program\nint main() { return 0; }\n"
        | [modul] -> genSelfContainedProgram modul testName
        | modules ->
            // Multi-module: merge all modules into one for code generation
            // Functions and bindings from earlier modules come first
            let merged = {
                Name = "merged"
                Types = modules |> List.collect (fun m -> m.Types)
                Functions = modules |> List.collect (fun m -> m.Functions)
                Bindings = modules |> List.collect (fun m -> m.Bindings)
                StaticFunctionUsage = modules |> List.fold (fun acc m -> 
                    Map.fold (fun a k v -> Map.add k v a) acc m.StaticFunctionUsage) Map.empty
            }
            genSelfContainedProgram merged testName
    (code, exprWarnings.Value)
