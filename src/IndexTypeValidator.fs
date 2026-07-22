// Blade-DSL Index Type Role Validator
// Pre-typecheck pass enforcing the rules for where index types may appear.
//
// Index types in Blade are TYPE-LEVEL constructs. They describe array index
// domains and provide nominative identity for foreign-key relationships.
// They have value-level meaning only when (a) used as the element type of a
// foreign-key array (must be aliased), or (b) appearing in a static-context
// position where compile-time evaluation makes sense.
//
// This validator runs before TypeCheck.lowerTypeExpr and rejects index types
// in positions that have no meaningful interpretation. The IR-level lowering
// can then be safely simplified — once an AST passes validation, code paths
// in lowerTypeExpr that produce malformed IRTArray<Float64,...> shapes for
// index types in standalone position are unreachable.
//
// The position rules:
//   Array<T like ___>  index domain  : any index type
//   Array<___ like Y>  element type  : aliased index types only (foreign key)
//   type X = ___                     : any index type (alias body)
//   DepIdx<I, λ(i) -> ___> body      : statically-evaluable only
//   static function f(p: ___)        : aliased AND statically-evaluable
//   static function f() -> ___       : statically-evaluable
//   static-context tuple element     : statically-evaluable
//   anywhere else                    : rejected
//
// Aliased index types are a superset of anonymous: any context that admits an
// anonymous index type also admits an aliased one. The asymmetry runs the
// other way (foreign-key elements and static fn params accept aliased only).
//
// Round 1 scope: declaration-level type expressions. Expression-level
// annotations (lambda params, ExprTyped, inner StmtLet, PatTyped) are NOT
// yet validated. Those require walking all expressions and are deferred.

module Blade.IndexTypeValidator

open Blade.Ast

// ============================================================================
// Validation Errors
// ============================================================================

/// Validation error. The message is user-facing; DeclName provides context.
type ValidationError = {
    Message: string
    Span: Span
    DeclName: string
}

// ============================================================================
// Position Context
// ============================================================================

/// Where a TypeExpr appears in the AST. Determines which rules apply.
///
/// PosForbidden carries a role string used in the error message, e.g.
/// "regular function parameter", "struct field". The validator never tries
/// to be helpful in these positions — it just reports the role.
type Position =
    | PosArrayIndexDomain        // Array<T like ___>          : any index type
    | PosArrayElemType           // Array<___ like Y>          : aliased only
    | PosAliasBody               // type X = ___               : any
    | PosDepIdxBody              // DepIdx<I, λ(i) -> ___>     : static only
    | PosStaticFnParam           // static function f(p: ___)  : aliased + static
    | PosStaticFnReturn          // static function f() -> ___ : static
    | PosLetStaticAnno           // let static x: ___ = ...    : forbidden (direct);
                                 //                              tuple-elem becomes
                                 //                              PosStaticTupleElem
    | PosStaticTupleElem         // tuple element in any static-context position
    | PosForbidden of role: string

// ============================================================================
// Alias Environment
// ============================================================================

/// Maps alias names to their resolved body. Built incrementally as the
/// validator walks declarations top-down. Forward references to types not
/// yet declared resolve to None (treated conservatively as "not an index
/// type" and "not statically-evaluable" — Blade currently doesn't support
/// forward type references in alias chains anyway).
type AliasEnv = Map<string, TypeExpr>

// ============================================================================
// Index Type Detection
// ============================================================================

/// True iff `ty` is an index type, directly or via alias resolution.
let rec isIndexType (env: AliasEnv) (ty: TypeExpr) : bool =
    match ty with
    | TyIdx _ | TySymIdx _ | TyAntisymIdx _ | TyHermitianIdx _
    | TyBoundedIdx _ | TyEnumIdx _ | TyCompoundIdx _
    | TyDepIdx _ | TyRaggedIdx _ | TyRaggedIdxOpaque
    | TyIrrepsIdx _
    | TyEquivIdx _ -> true
    | TyNamed (n, _) ->
        match Map.tryFind n env with
        | Some body -> isIndexType env body
        | None -> false
    | TyConstrained (inner, _) -> isIndexType env inner
    | _ -> false

/// True iff `ty` is an aliased index type (a TyNamed resolving to an index).
let isAliasedIndexType (env: AliasEnv) (ty: TypeExpr) : bool =
    match ty with
    | TyNamed (n, _) ->
        match Map.tryFind n env with
        | Some body -> isIndexType env body
        | None -> false
    | _ -> false

/// True iff `ty` is an anonymous (raw) index type, not aliased.
let isAnonymousIndexType (ty: TypeExpr) : bool =
    match ty with
    | TyIdx _ | TySymIdx _ | TyAntisymIdx _ | TyHermitianIdx _
    | TyBoundedIdx _ | TyEnumIdx _ | TyCompoundIdx _
    | TyDepIdx _ | TyRaggedIdx _ | TyRaggedIdxOpaque
    | TyIrrepsIdx _
    | TyEquivIdx _ -> true
    | _ -> false

/// True iff `ty` is KNOWN to be statically-evaluable. Default false (the
/// safer default per the design — runtime-evaluable is the default, static
/// is the special case). Resolves through alias chains.
///
/// Statically-evaluable: Idx, SymIdx, AntisymIdx, HermitianIdx, BoundedIdx,
/// EnumIdx, EquivIdx (all extent expressions trusted to reduce — the
/// existing lowering surfaces non-static extents downstream).
///
/// Runtime: RaggedIdx, RaggedIdxOpaque, CompoundIdx — these structurally
/// require runtime data.
///
/// DepIdx: static iff both outer and body are static.
let rec isKnownStatic (env: AliasEnv) (ty: TypeExpr) : bool =
    match ty with
    | TyIdx _ | TySymIdx _ | TyAntisymIdx _ | TyHermitianIdx _
    | TyBoundedIdx _ | TyEnumIdx _ | TyEquivIdx _ -> true
    | TyIrrepsIdx _ -> true  // spec is static by definition; extent folds to a literal
    | TyRaggedIdx _ | TyRaggedIdxOpaque | TyCompoundIdx _ -> false
    | TyDepIdx (outer, _, body) ->
        isKnownStatic env outer && isKnownStatic env body
    | TyNamed (n, _) ->
        match Map.tryFind n env with
        | Some body -> isKnownStatic env body
        | None -> false  // unknown — assume runtime
    | TyConstrained (inner, _) -> isKnownStatic env inner
    | _ -> false

// ============================================================================
// Position Rules
// ============================================================================

/// Compute the position to use for a tuple element. Static-context positions
/// promote to PosStaticTupleElem (which permits static-eval index types,
/// anonymous or aliased). Other positions inherit unchanged — a tuple inside
/// a regular-fn param remains forbidden, a tuple inside an array-elem stays
/// in PosArrayElemType, etc.
let tuplePositionFor (parent: Position) : Position =
    match parent with
    | PosStaticFnParam | PosStaticFnReturn
    | PosLetStaticAnno | PosAliasBody | PosDepIdxBody
    | PosStaticTupleElem -> PosStaticTupleElem
    | other -> other

/// Format a position for error messages.
let positionDescription (pos: Position) : string =
    match pos with
    | PosArrayIndexDomain -> "array index domain"
    | PosArrayElemType -> "array element type"
    | PosAliasBody -> "type alias body"
    | PosDepIdxBody -> "DepIdx body"
    | PosStaticFnParam -> "static function parameter"
    | PosStaticFnReturn -> "static function return type"
    | PosLetStaticAnno -> "let static binding annotation"
    | PosStaticTupleElem -> "tuple element in static-context"
    | PosForbidden role -> role

/// Check the rules for an index type at a given position. Caller has already
/// confirmed `ty` is an index type via `isIndexType`.
let checkIndexTypeRules (env: AliasEnv) (declName: string) (span: Span)
                        (pos: Position) (ty: TypeExpr) : ValidationError list =
    let isAliased = isAliasedIndexType env ty
    let isStatic = isKnownStatic env ty
    let mkErr msg = [{ Message = msg; Span = span; DeclName = declName }]

    match pos with
    | PosArrayIndexDomain ->
        // Any index type allowed.
        []

    | PosArrayElemType ->
        if isAliased then []
        else mkErr (
            "Anonymous index types cannot be used as an array element type. "
            + "Foreign-key relationships require nominative identity: alias the index type "
            + "with `type X = ...` and use `Array<X like ...>`.")

    | PosAliasBody ->
        // Any index type allowed; the alias gives it a name.
        []

    | PosDepIdxBody ->
        if isStatic then []
        else mkErr (
            "Runtime-evaluable index types (RaggedIdx, CompoundIdx, etc.) "
            + "cannot appear in a DepIdx body. The body must be statically "
            + "reducible at compile time.")

    | PosStaticFnParam ->
        if not isAliased then
            mkErr (
                "Anonymous index types cannot be passed as function parameters. "
                + "Static functions accept aliased index types only — declare "
                + "`type X = ...` first and use the alias name.")
        elif not isStatic then
            mkErr (
                "Runtime-evaluable index types cannot be parameters of a "
                + "static function. Static functions are evaluated at compile "
                + "time and require statically-evaluable types.")
        else []

    | PosStaticFnReturn ->
        if isStatic then []
        else mkErr (
            "Runtime-evaluable index types cannot be the return type of a "
            + "static function. Static functions are evaluated at compile "
            + "time and require statically-evaluable types.")

    | PosLetStaticAnno ->
        // Direct annotation rejected. Tuple elements get tuplePositionFor →
        // PosStaticTupleElem, which has its own rule. So this case fires only
        // when the user annotated `let static x: Idx<3> = ...` directly.
        mkErr (
            "Index types cannot directly annotate a `let static` binding. "
            + "To introduce a new index type, use `type X = ...`. To bind "
            + "an integer value, use the underlying integer type or omit the "
            + "annotation.")

    | PosStaticTupleElem ->
        if isStatic then []
        else mkErr (
            "Runtime-evaluable index types cannot appear as elements of a "
            + "static-context tuple. Use a statically-evaluable index type.")

    | PosForbidden role ->
        mkErr (
            sprintf "Index types cannot appear as %s. " role
            + "Index types are type-level constructs; they are permitted only "
            + "as array index domains (`Array<T like ___>`), as foreign-key "
            + "element types (aliased: `Array<X like Y>`), in alias bodies "
            + "(`type X = ___`), in DepIdx bodies, or in static function "
            + "signatures.")

// ============================================================================
// Type Expression Walking
// ============================================================================

/// Validate a TypeExpr at a given position. Handles both the position-rule
/// check (for index types at this level) and recursion into composite types.
let rec validateTypeExpr (env: AliasEnv) (declName: string) (span: Span)
                         (pos: Position) (ty: TypeExpr) : ValidationError list =
    let positionErrs =
        if isIndexType env ty then checkIndexTypeRules env declName span pos ty
        else []
    let childErrs = validateChildren env declName span pos ty
    positionErrs @ childErrs

/// Recurse into composite types. Each child is validated at the appropriate
/// position derived from `parentPos` — most children inherit, but some types
/// (TyArray, TyDepIdx) introduce specific child positions.
and validateChildren (env: AliasEnv) (declName: string) (span: Span)
                     (parentPos: Position) (ty: TypeExpr) : ValidationError list =
    match ty with
    | TyArray (elemTy, indexTys) ->
        let elemErrs = validateTypeExpr env declName span PosArrayElemType elemTy
        let idxErrs =
            indexTys |> List.collect (fun t ->
                validateTypeExpr env declName span PosArrayIndexDomain t)
        elemErrs @ idxErrs

    | TyTuple tys ->
        let childPos = tuplePositionFor parentPos
        tys |> List.collect (fun t ->
            validateTypeExpr env declName span childPos t)

    | TyDepIdx (outer, _, body) ->
        // Outer is itself in array-index-domain semantics (it's the outer
        // dimension of the dependent shape). Body is static-context.
        let outerErrs = validateTypeExpr env declName span PosArrayIndexDomain outer
        let bodyErrs = validateTypeExpr env declName span PosDepIdxBody body
        outerErrs @ bodyErrs

    | TyConstrained (inner, _) ->
        // Constraints are orthogonal; inner inherits position.
        validateTypeExpr env declName span parentPos inner

    | TyPoly inner ->
        validateTypeExpr env declName span parentPos inner

    | TyFunc _ ->
        // Function types as type expressions (higher-order). Out of scope
        // for round 1; deferred until we have user-level use cases.
        []

    | TyAbstractArray _ ->
        // Abstract arrays (T^r) carry index types only via type-variable
        // arity. No structural index-type child.
        []

    | _ -> []

// ============================================================================
// Declaration Walking
// ============================================================================

/// Format a binding name from a pattern (best-effort, for context strings).
let private patternName (pat: Pattern) : string =
    match pat.Kind with
    | PatternKind.PatVar n -> n
    | PatternKind.PatTuple pats ->
        let names = pats |> List.choose (fun p -> match p.Kind with PatternKind.PatVar n -> Some n | _ -> None)
        sprintf "(%s)" (String.concat ", " names)
    | _ -> "_"

/// Validate a single declaration. Returns (errors, updated alias env).
/// Type aliases extend the env so subsequent declarations see them.
let validateDecl (env: AliasEnv) (decl: Located<Decl>) : ValidationError list * AliasEnv =
    let span = decl.Span
    match decl.Value with
    | DeclType (TyDeclAlias (name, _, body)) ->
        let declName = sprintf "in type alias '%s'" name
        let errs = validateTypeExpr env declName span PosAliasBody body
        let newEnv = Map.add name body env
        (errs, newEnv)

    | DeclType (TyDeclStruct (name, _, fields, _invariant)) ->
        let declName = sprintf "in struct '%s'" name
        let errs =
            fields |> List.collect (fun f ->
                validateTypeExpr env declName span (PosForbidden "struct fields") f.Type)
        (errs, env)

    | DeclType (TyDeclSum (name, _, variants)) ->
        let declName = sprintf "in sum type '%s'" name
        let errs =
            variants |> List.collect (fun v ->
                match v.Data with
                | Some t ->
                    validateTypeExpr env declName span (PosForbidden "variant payloads") t
                | None -> [])
        (errs, env)

    | DeclType (TyDeclMutualGroup (members, _)) ->
        // Each member body validates like a type-alias body and extends the
        // alias env like one.
        members |> List.fold (fun (accErrs, accEnv) (mname, mty) ->
            let declName = sprintf "in mutual-group member '%s'" mname
            let errs = validateTypeExpr accEnv declName span PosAliasBody mty
            (accErrs @ errs, Map.add mname mty accEnv)) ([], env)

    | DeclFunction f ->
        let kind = if f.IsStatic then "static function" else "function"
        let declName = sprintf "in %s '%s'" kind f.Name
        let paramPos =
            if f.IsStatic then PosStaticFnParam
            else PosForbidden "regular function parameters"
        let returnPos =
            if f.IsStatic then PosStaticFnReturn
            else PosForbidden "regular function return types"
        let paramErrs =
            f.Params |> List.collect (fun p ->
                match p.Type with
                | Some t -> validateTypeExpr env declName span paramPos t
                | None -> [])
        let returnErrs =
            match f.ReturnType with
            | Some t -> validateTypeExpr env declName span returnPos t
            | None -> []
        (paramErrs @ returnErrs, env)

    | DeclLet binding ->
        let declName = sprintf "in let binding '%s'" (patternName binding.Pattern)
        let errs =
            match binding.Type with
            | Some t ->
                validateTypeExpr env declName span (PosForbidden "let binding annotations") t
            | None -> []
        (errs, env)

    | DeclStatic binding ->
        let declName = sprintf "in let static binding '%s'" (patternName binding.Pattern)
        let errs =
            match binding.Type with
            | Some t ->
                validateTypeExpr env declName span PosLetStaticAnno t
            | None -> []
        (errs, env)

    | DeclInterface i ->
        let declName = sprintf "in interface '%s'" i.Name
        let errs =
            i.Methods |> List.collect (fun m ->
                let paramErrs =
                    m.Params |> List.collect (fun p ->
                        match p.Type with
                        | Some t ->
                            validateTypeExpr env declName span
                                (PosForbidden "interface method parameters") t
                        | None -> [])
                let retErrs =
                    validateTypeExpr env declName span
                        (PosForbidden "interface method return types") m.ReturnType
                paramErrs @ retErrs)
        (errs, env)

    | DeclImpl _ ->
        // Impl blocks contain function decls; those will be validated when
        // they're processed individually. Not yet covered for round 1.
        ([], env)

    | DeclUnit _ | DeclImport _ ->
        ([], env)

// ============================================================================
// Module / Program Entry
// ============================================================================

/// Validate a module. Walks declarations in source order, accumulating the
/// alias environment as types are declared.
let validateModule (modul: ModuleDecl) : ValidationError list =
    let mutable env : AliasEnv = Map.empty
    let mutable allErrs = []
    for decl in modul.Decls do
        let (errs, env') = validateDecl env decl
        allErrs <- allErrs @ errs
        env <- env'
    allErrs

/// Validate a program. Each module is validated independently; alias
/// environments don't currently cross module boundaries (a future extension
/// once cross-module index type aliasing has a concrete use case).
let validateProgram (program: Program) : ValidationError list =
    program.Modules |> List.collect validateModule
