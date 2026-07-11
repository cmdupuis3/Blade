// Type-checking environment: schemes, instantiate/generalize, the TypeEnv
// record and its variable/type-definition registries (audit §4:
// Check/TypeEnv.fs). Extracted verbatim from TypeCheck.fs (Phase 3).
module Blade.TypeEnv

open Blade.Ast
open Blade.IR
open Blade.Types
open Blade.TypedAst
open Blade.Unify

/// Instantiate a type scheme: replace each quantified variable with a fresh
/// inference variable, so each use site gets independent type constraints.
let instantiate (subst: Subst) (scheme: TypeScheme) : IRType =
    if scheme.QuantifiedVars.IsEmpty then
        scheme.Body
    else
        let mapping =
            scheme.QuantifiedVars
            |> List.map (fun v ->
                let fresh = subst.Fresh()
                // Propagate arity constraints to the fresh variable
                match fresh with
                | IRTInfer freshId -> subst.CopyArityConstraint(v, freshId)
                | _ -> ()
                (v, fresh))
            |> Map.ofList
        let rec replace ty =
            match ty with
            | IRTInfer id ->
                Map.tryFind id mapping |> Option.defaultValue ty
            | IRTTuple ts -> IRTTuple (ts |> List.map replace)
            | IRTArrow (slots, ret, identity) ->
                let replaceSlot = function
                    | SIdx idx -> SIdx idx
                    | SIdxVirt idx -> SIdxVirt idx
                    | SVal t -> SVal (replace t)
                IRTArrow (slots |> List.map replaceSlot, replace ret, identity)
            | IRTComputation t -> IRTComputation (replace t)
            | IRTPoly (t, v) -> IRTPoly (replace t, v)
            | IRTLoop lt ->
                IRTLoop { lt with
                            ArrayTypes = lt.ArrayTypes |> List.map replace
                            KernelType = lt.KernelType |> Option.map replace }
            | _ -> ty  // IRTScalar, IRTUnit, IRTNamed, IRTNat (no inference vars to replace)
        replace scheme.Body

// ============================================================================
// 2. Type Environment
// ============================================================================

/// Variable assignability levels tracked during type checking.
/// Maps to binding forms: static → ReadOnly, let → Assignable, let mut → MutPassable
type Assignability =
    | ReadOnly      // static: not assignable, generalizable
    | Assignable    // let: assignable in scope, not passable to mut params
    | MutPassable   // let mut: assignable + passable to mut params

/// Variable binding information tracked during type checking.
type VarInfo = {
    VarId: IRId
    Type: IRType
    Identity: ArrayIdentity option
    Assign: Assignability
    /// The TypedExpr this variable was bound to, for <@> resolution.
    TypedValue: TypedExpr option
    /// If Some, this binding is polymorphic (let-generalized).
    /// Variable lookup instantiates fresh type variables from the scheme.
    Scheme: TypeScheme option
}

/// Registered type definition
type TypeDefInfo =
    | TDIAlias of IRType
    | TDIStruct of name: string * typeParams: string list * fields: (string * IRType) list
    | TDIVariant of name: string * typeParams: string list * variants: (string * IRType option) list
    | TDIIndexType of name: string * idx: IRIndexType * body: TypeExpr
    | TDIEnumIdx of name: string * idx: IRIndexType * values: EnumValue list * body: TypeExpr

/// Exported bindings from a type-checked module, for cross-module imports
type TypeModuleExport = {
    Variables: Map<string, VarInfo>
    TypeDefs: Map<string, TypeDefInfo>
    VariantTags: Map<string, string * IRType option>
    Units: Map<string, UnitSig>
    /// Static function ASTs from this module. Imported alongside Variables
    /// so eta-reduced DepIdx in an importing module can inline a static
    /// function defined in this one. The body is needed for the inlining;
    /// the TypeEnv-side StaticFunctions map is the consumer.
    StaticFunctions: Map<string, FunctionDecl>
}

/// Type checking environment
type TypeEnv = {
    Variables: Map<string, VarInfo>
    TypeDefs: Map<string, TypeDefInfo>
    /// Variant tag -> (parentTypeName, payloadType option)
    VariantTags: Map<string, string * IRType option>
    Subst: Subst
    Builder: IRBuilder
    OuterScope: Map<string, VarInfo>
    InPolyContext: bool
    CurrentCommGroups: int list list
    /// Interface name -> InterfaceDecl
    Interfaces: Map<string, InterfaceDecl>
    /// (typeName, methodName) -> (mangledFuncVarId, funcType)
    ImplMethods: Map<string * string, IRId * IRType>
    /// Unit name -> canonical UnitSig
    Units: Map<string, UnitSig>
    /// Context stack for error reporting, e.g. ["in function 'foo'"]
    Context: string list
    /// Exports from previously type-checked modules
    ModuleExports: Map<string, TypeModuleExport>
    /// Static function ASTs, populated during checkModule's pre-pass.
    /// Used by lowerIndexTypeList to inline eta-reduced DepIdx bodies —
    /// `DepIdx<O, f>` desugars to `lambda(i) -> Idx<f(i)>`, and the
    /// substitution into the inner extent requires access to f's body.
    StaticFunctions: Map<string, FunctionDecl>
    /// Non-fatal diagnostics accumulated during type-checking. The field
    /// is a mutable ResizeArray so functional updates (`{ env with ... }`)
    /// share the same collector — warnings emitted from any scope land
    /// in one place. Surfaced through `typeCheck`'s Ok return; runs even
    /// when the program also has hard errors (warnings + errors aren't
    /// mutually exclusive, but the current Ok-only plumbing skips them
    /// on the error path — extending to both paths is a future tweak).
    Warnings: ResizeArray<string>
}

let emptyEnv () = {
    Variables = Map.empty
    TypeDefs = Map.empty
    VariantTags = Map.empty
    Subst = Subst()
    Builder = IRBuilder()
    OuterScope = Map.empty
    InPolyContext = false
    CurrentCommGroups = []
    Interfaces = Map.empty
    ImplMethods = Map.empty
    Units = Map.empty
    Context = []
    ModuleExports = Map.empty
    StaticFunctions = Map.empty
    Warnings = ResizeArray<string>()
}

/// Append a non-fatal diagnostic to the env's warnings collector.
/// The collector is shared by reference across all functional updates
/// of the env, so callsites don't need to thread anything through.
let emitWarning (env: TypeEnv) (msg: string) : unit =
    env.Warnings.Add(msg)

/// Push a context frame onto the environment
let pushContext (ctx: string) (env: TypeEnv) : TypeEnv =
    { env with Context = ctx :: env.Context }

/// Span of the statement currently being type-checked, per async flow
/// (audit §3.4, statement-granularity slice; expression granularity is
/// rewrite work). inferBlock stamps it as it unwraps each StmtSpanned;
/// locateError below prefers it over the caller-supplied (declaration)
/// span, so an error inside a multi-statement body points at the failing
/// STATEMENT rather than the declaration header. inferBlock skips the
/// remaining statements after the first error, so at error-location time
/// the last-stamped span belongs to the statement that failed. Reset at
/// every checkDecl entry so a span cannot leak across declarations.
let private currentStmtSpanStorage = System.Threading.AsyncLocal<Span>()

let setCurrentStmtSpan (s: Span) = currentStmtSpanStorage.Value <- s
let resetCurrentStmtSpan () = currentStmtSpanStorage.Value <- noSpan

let currentStmtSpan () : Span =
    match box currentStmtSpanStorage.Value with
    | null -> noSpan
    | _ -> currentStmtSpanStorage.Value

/// Wrap a TypeError with span and context into a CompileError. Prefers the
/// active statement-level span (see currentStmtSpanStorage) over the
/// caller's span, which is typically the enclosing declaration's.
let locateError (span: Span) (env: TypeEnv) (err: TypeError) : CompileError =
    let stmtSpan = currentStmtSpan ()
    let span = if stmtSpan.StartLine > 0 then stmtSpan else span
    { Error = err; Span = span; Context = env.Context }

/// Format a TypeError as a human-readable string
let formatTypeError (err: TypeError) : string =
    match err with
    | UnboundVariable name -> sprintf "Unbound variable: %s" name
    | TypeMismatch (exp, act) -> sprintf "Type mismatch: expected %A, got %A" exp act
    | ArityMismatch (exp, act) -> sprintf "Arity mismatch: expected %d args, got %d" exp act
    | InvalidArrayCapture name -> sprintf "Lambda cannot capture array '%s'" name
    | InvalidApplication funcTy -> sprintf "Cannot apply non-function type: %A" funcTy
    | PatternTypeMismatch (pat, ty) -> sprintf "Pattern '%s' incompatible with type %A" pat ty
    | Other msg -> msg

/// Format a CompileError with location and context
let formatCompileError (err: CompileError) : string =
    let loc =
        if err.Span.StartLine > 0 then
            match err.Span.File with
            | Some f -> sprintf "%s:%d:%d" f err.Span.StartLine err.Span.StartCol
            | None -> sprintf "%d:%d" err.Span.StartLine err.Span.StartCol
        else ""
    let msg = formatTypeError err.Error
    let context =
        err.Context
        |> List.rev
        |> List.map (sprintf "  %s")
        |> String.concat "\n"
    if loc = "" && context = "" then msg
    elif context = "" then sprintf "%s: %s" loc msg
    else sprintf "%s: %s\n%s" loc msg context

let bindVar name info (env: TypeEnv) =
    { env with Variables = Map.add name info env.Variables }

let bindVarSimple name varId ty env =
    bindVar name { VarId = varId; Type = ty; Identity = None
                   Assign = ReadOnly; TypedValue = None; Scheme = None } env


let bindVarFull name varId ty identity assign typedValue env =
    bindVar name { VarId = varId; Type = ty; Identity = identity
                   Assign = assign; TypedValue = typedValue; Scheme = None } env

/// Bind a polymorphic (let-generalized) variable.
let bindVarPoly name varId ty identity assign typedValue scheme env =
    bindVar name { VarId = varId; Type = ty; Identity = identity
                   Assign = assign; TypedValue = typedValue
                   Scheme = Some scheme } env

let lookupVar name env = Map.tryFind name env.Variables
let lookupTypeDef name env = Map.tryFind name env.TypeDefs

/// Convert AST BindingMut to Assignability.
let assignOfBindingMut = function
    | BindConst -> ReadOnly    // static / let const → immutable
    | BindLet -> Assignable    // let → assignable in scope
    | BindMut -> MutPassable   // let mut → assignable + mut-passable

let enterScope env =
    { env with OuterScope = Map.foldBack Map.add env.Variables env.OuterScope }

let registerTypeDef name info (env: TypeEnv) =
    { env with TypeDefs = Map.add name info env.TypeDefs }

/// Register a loaded provider module's struct types into the type-check env and
/// return the binding's module-struct type. The provider's emitted dims/vars
/// structs are registered as-is; a synthetic top-level struct named `name` is
/// added so the loaded binding carries `.dims` / `.vars` fields, letting field
/// access (e.g. `sample.vars.temp`) resolve to a variable's real Array type.
/// Pure (no file IO) so it can be unit-tested with a mock module; reading the
/// file metadata that produces `pm` is the caller's concern.
let registerProviderModule (env: TypeEnv) (name: string) (pm: IRModule) : TypeEnv * IRType =
    let envS =
        pm.Types |> List.fold (fun e td ->
            match td with
            | IRTDStruct (n, fields, _) -> registerTypeDef n (TDIStruct (n, [], fields)) e
            | _ -> e) env
    let moduleStruct = TDIStruct (name, [], [("dims", IRTNamed "dims"); ("vars", IRTNamed "vars")])
    (registerTypeDef name moduleStruct envS, IRTNamed name)


/// Check if a variant type is a pure enum (all constructors have no data)
let isEnumType (env: TypeEnv) (parentName: string) : bool =
    match Map.tryFind parentName env.TypeDefs with
    | Some (TDIVariant (_, _, variants)) -> variants |> List.forall (fun (_, d) -> d.IsNone)
    | _ -> false

let registerVariantTag tag parentName payload (env: TypeEnv) =
    { env with VariantTags = Map.add tag (parentName, payload) env.VariantTags }

/// Resolve a UnitExpr AST node to a canonical UnitSig
let rec resolveUnitExpr (units: Map<string, UnitSig>) (expr: UnitExpr) : Result<UnitSig, string> =
    match expr with
    | UnitNamed name ->
        match Map.tryFind name units with
        | Some sig' -> Ok sig'
        | None -> Error (sprintf "Unknown unit '%s'" name)
    | UnitMul (a, b) ->
        resolveUnitExpr units a |> Result.bind (fun sa ->
        resolveUnitExpr units b |> Result.map (fun sb ->
            unitMul sa sb))
    | UnitDiv (a, b) ->
        resolveUnitExpr units a |> Result.bind (fun sa ->
        resolveUnitExpr units b |> Result.map (fun sb ->
            unitDiv sa sb))
    | UnitPow (a, n) ->
        resolveUnitExpr units a |> Result.map (fun sa ->
            unitPow sa n)

/// Register a unit declaration in the environment
let registerUnit (env: TypeEnv) (decl: UnitDecl) : TypeEnv =
    let sig' =
        match decl.Definition with
        | None | Some UnitBase ->
            // Base unit: canonical form is {name: 1}
            Map.ofList [(decl.Name, 1)]
        | Some (UnitDerived expr) ->
            match resolveUnitExpr env.Units expr with
            | Ok resolved -> resolved
            | Error msg ->
                eprintfn "Unit error: %s" msg
                Map.ofList [(decl.Name, 1)]  // fallback to base unit
    { env with Units = Map.add decl.Name sig' env.Units }

// ============================================================================
// 2b. Generalization (needs VarInfo defined above)
// ============================================================================

/// Collect free inference vars across all variable types in scope.
let freeInferVarsInEnv (subst: Subst) (vars: Map<string, VarInfo>) : Set<int> =
    vars |> Map.fold (fun acc _ info ->
        Set.union acc (freeInferVars subst info.Type)) Set.empty

/// Generalize a type: quantify over inference vars free in the type
/// but NOT free in the environment.
let generalize (subst: Subst) (envVars: Map<string, VarInfo>) (ty: IRType) : TypeScheme =
    let envFree = freeInferVarsInEnv subst envVars
    let tyFree = freeInferVars subst (subst.Resolve ty)
    let quantified = Set.difference tyFree envFree |> Set.toList
    { QuantifiedVars = quantified; Body = subst.Resolve ty }

// ============================================================================
// 3. Constant Expression Evaluation (for index extents)
// ============================================================================

/// Try to evaluate an AST expression to a compile-time int64.
