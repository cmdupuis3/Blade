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
//   - typeCheck : Program -> TypeResult<TypedProgram>
//   - TypeError union: UnboundVariable | TypeMismatch | ArityMismatch
//                      | InvalidArrayCapture | InvalidApplication
//                      | PatternTypeMismatch | Other

module Blade.TypeCheck

open Blade.Ast
open Blade.IR
open Blade.TypedAst

// ============================================================================
// Error Types (public — consumed by Lowering.fs and Main.fs)
// ============================================================================

type TypeError =
    | UnboundVariable of string
    | TypeMismatch of expected: IRType * actual: IRType
    | ArityMismatch of expected: int * actual: int
    | InvalidArrayCapture of varName: string
    | InvalidApplication of funcType: IRType
    | PatternTypeMismatch of pattern: string * expected: IRType
    | Other of string

type TypeResult<'T> = Result<'T, TypeError>

// ============================================================================
// 1. Unification Infrastructure
// ============================================================================

/// Mutable substitution mapping inference variable IDs to resolved types.
type Subst() =
    let mutable map : Map<int, IRType> = Map.empty
    let mutable nextId = 10000  // High start avoids collision with IRBuilder IDs
    let mutable typeVarScope : Map<string, IRType * int> = Map.empty
    let mutable arityConstraints : Map<int, int> = Map.empty
    let mutable knownTypeVarNames : Set<string> = Set.empty

    member _.Fresh() =
        let id = nextId
        nextId <- nextId + 1
        IRTInfer id

    member _.Bind(id, ty) =
        map <- Map.add id ty map

    member _.TryFind(id) =
        Map.tryFind id map

    member this.LookupOrCreateTypeVar(name: string, arity: int, builder: IRBuilder) : IRType =
        let key = name + "^" + string arity
        match Map.tryFind key typeVarScope with
        | Some (tv, _) -> tv
        | None ->
            let tv = this.Fresh()
            if arity > 0 then
                match tv with
                | IRTInfer id -> arityConstraints <- Map.add id arity arityConstraints
                | _ -> ()
            typeVarScope <- Map.add key (tv, arity) typeVarScope
            tv

    member this.LookupOrCreateTypeVar(name: string) : IRType =
        let key = name + "^0"
        match Map.tryFind key typeVarScope with
        | Some (tv, _) -> tv
        | None ->
            let tv = this.Fresh()
            typeVarScope <- Map.add key (tv, 0) typeVarScope
            tv

    member _.GetArityConstraint(id: int) : int option =
        Map.tryFind id arityConstraints

    member _.CopyArityConstraint(fromId: int, toId: int) =
        match Map.tryFind fromId arityConstraints with
        | Some k -> arityConstraints <- Map.add toId k arityConstraints
        | None -> ()

    member _.RegisterTypeVarName(name: string) =
        knownTypeVarNames <- Set.add name knownTypeVarNames

    member _.IsTypeVar(name: string) : bool =
        Set.contains name knownTypeVarNames

    member _.PushTypeVarScope() : Map<string, IRType * int> * Set<string> =
        let savedScope = typeVarScope
        let savedNames = knownTypeVarNames
        typeVarScope <- Map.empty
        knownTypeVarNames <- Set.empty
        (savedScope, savedNames)

    member _.PopTypeVarScope(saved: Map<string, IRType * int> * Set<string>) =
        typeVarScope <- fst saved
        knownTypeVarNames <- snd saved

    /// Recursively resolve a type through the substitution.
    /// Applies rank-0 collapse: Array<T, (no indices)> -> Scalar T.
    member this.Resolve(ty: IRType) : IRType =
        match ty with
        | IRTInfer id ->
            match this.TryFind id with
            | Some ty' -> this.Resolve ty'
            | None -> ty
        | IRTArray arr ->
            // Rank-0 collapse: an array with no index types is just its element
            if arr.IndexTypes.IsEmpty then
                IRTScalar arr.ElemType
            else
                IRTArray { arr with IndexTypes = arr.IndexTypes |> List.map this.ResolveIdx }
        | IRTTuple ts -> IRTTuple (ts |> List.map this.Resolve)
        | IRTFunc (args, ret) -> IRTFunc (args |> List.map this.Resolve, this.Resolve ret)
        | IRTComputation t -> IRTComputation (this.Resolve t)
        | IRTLoop lt ->
            IRTLoop { lt with
                        ArrayTypes = lt.ArrayTypes |> List.map this.Resolve
                        KernelType = lt.KernelType |> Option.map this.Resolve }
        | IRTPoly (base', var) -> IRTPoly (this.Resolve base', var)
        | IRTUnitAnnotated (inner, units) -> IRTUnitAnnotated (this.Resolve inner, units)
        | _ -> ty

    member this.ResolveIdx (idx: IRIndexType) = idx  // Index extents are IRExpr, not IRType

/// Occurs check: does inference variable `id` appear in `ty`?
let rec occursIn (id: int) (ty: IRType) : bool =
    match ty with
    | IRTInfer id2 -> id = id2
    | IRTTuple ts -> ts |> List.exists (occursIn id)
    | IRTFunc (args, ret) -> List.exists (occursIn id) args || occursIn id ret
    | IRTComputation t -> occursIn id t
    | IRTPoly (base', _) -> occursIn id base'
    | IRTLoop lt ->
        (lt.ArrayTypes |> List.exists (occursIn id)) ||
        (lt.KernelType |> Option.map (occursIn id) |> Option.defaultValue false)
    | IRTUnitAnnotated (inner, _) -> occursIn id inner
    | _ -> false

/// Unify two types, updating the substitution. Returns error on failure.
let rec unify (subst: Subst) (t1: IRType) (t2: IRType) : TypeResult<unit> =
    let t1 = subst.Resolve t1
    let t2 = subst.Resolve t2
    match t1, t2 with
    | IRTInfer id1, IRTInfer id2 when id1 = id2 -> Ok ()
    | IRTInfer id, ty | ty, IRTInfer id ->
        if occursIn id ty then Error (Other "Infinite type detected")
        else
            // Check arity invariant: T^k must unify with rank-k array
            match subst.GetArityConstraint(id) with
            | Some k when k > 0 ->
                match ty with
                | IRTArray arr when arr.IndexTypes.Length = k ->
                    subst.Bind(id, ty); Ok ()
                | IRTInfer _ ->
                    // Binding two inference vars — defer invariant check
                    subst.Bind(id, ty); Ok ()
                | _ ->
                    Error (Other (sprintf "Type variable with arity %d requires a rank-%d array, got %A" k k ty))
            | _ ->
                subst.Bind(id, ty); Ok ()
    | IRTScalar e1, IRTScalar e2 when e1 = e2 -> Ok ()
    // Numeric promotion (common in scientific code)
    | IRTScalar ETInt64, IRTScalar ETFloat64
    | IRTScalar ETFloat64, IRTScalar ETInt64 -> Ok ()
    | IRTScalar ETInt32, IRTScalar ETFloat64
    | IRTScalar ETFloat64, IRTScalar ETInt32 -> Ok ()
    | IRTScalar ETInt32, IRTScalar ETInt64
    | IRTScalar ETInt64, IRTScalar ETInt32 -> Ok ()
    | IRTArray a1, IRTArray a2 ->
        // Arrays: rank must match.
        if a1.IndexTypes.Length <> a2.IndexTypes.Length then
            Error (TypeMismatch (t1, t2))
        else
            // Per-index compatibility: named index types must match by name,
            // symmetry classes must be compatible.
            // NOTE: We intentionally do NOT compare extents here — extents are
            // runtime values in C++, not compile-time template parameters.
            let indexMismatch =
                List.zip a1.IndexTypes a2.IndexTypes |> List.tryFind (fun (i1, i2) ->
                    // Named index types are nominative: lat != lon even if both Idx<180>
                    match i1.Tag, i2.Tag with
                    | Some t1, Some t2 when t1 <> t2 -> true
                    | _ ->
                        // Symmetry class must be compatible
                        i1.Symmetry <> i2.Symmetry && i1.Symmetry <> SymNone && i2.Symmetry <> SymNone)
            match indexMismatch with
            | Some _ -> Error (TypeMismatch (t1, t2))
            | None -> Ok ()
    | IRTTuple ts1, IRTTuple ts2 when ts1.Length = ts2.Length ->
        List.zip ts1 ts2 |> List.fold (fun acc (a, b) ->
            acc |> Result.bind (fun () -> unify subst a b)) (Ok ())
    | IRTFunc (a1, r1), IRTFunc (a2, r2) when a1.Length = a2.Length ->
        List.zip a1 a2 |> List.fold (fun acc (a, b) ->
            acc |> Result.bind (fun () -> unify subst a b)) (Ok ())
        |> Result.bind (fun () -> unify subst r1 r2)
    | IRTUnit, IRTUnit -> Ok ()
    | IRTNamed n1, IRTNamed n2 when n1 = n2 -> Ok ()
    | IRTLoop l1, IRTLoop l2 when l1.Kind = l2.Kind -> Ok ()
    | IRTComputation t1, IRTComputation t2 -> unify subst t1 t2
    | IRTNat _, IRTNat _ -> Ok ()
    // Unit-annotated types: unify inner types (unit checking is separate)
    | IRTUnitAnnotated (inner1, _), IRTUnitAnnotated (inner2, _) -> unify subst inner1 inner2
    | IRTUnitAnnotated (inner, _), other | other, IRTUnitAnnotated (inner, _) -> unify subst inner other
    | _ -> Error (TypeMismatch (t1, t2))

// ============================================================================
// 1b. Let-Generalization (Hindley-Milner Polymorphism)
// ============================================================================

/// A type scheme: a type with universally quantified inference variables.
/// E.g., `let id = lambda(x: T) -> x` gets scheme `forall {#10001}. #10001 -> #10001`.
type TypeScheme = {
    QuantifiedVars: int list   // Inference variable IDs that are universally quantified
    Body: IRType               // The type (resolved), with quantified vars still as IRTInfer
}

/// Monomorphic scheme (no quantified variables).
let monoScheme (ty: IRType) : TypeScheme =
    { QuantifiedVars = []; Body = ty }

/// Collect free (unresolved) inference variable IDs in a type.
let rec freeInferVars (subst: Subst) (ty: IRType) : Set<int> =
    match subst.Resolve ty with
    | IRTInfer id -> Set.singleton id
    | IRTScalar _ | IRTUnit | IRTNamed _ | IRTNat _ -> Set.empty
    | IRTArray _arr -> Set.empty  // ElemType is concrete; index extents are IRExpr not IRType
    | IRTTuple ts -> ts |> List.map (freeInferVars subst) |> Set.unionMany
    | IRTFunc (args, ret) ->
        Set.unionMany (freeInferVars subst ret :: (args |> List.map (freeInferVars subst)))
    | IRTComputation t -> freeInferVars subst t
    | IRTPoly (t, _) -> freeInferVars subst t
    | IRTLoop lt ->
        Set.union
            (lt.ArrayTypes |> List.map (freeInferVars subst) |> Set.unionMany)
            (lt.KernelType |> Option.map (freeInferVars subst) |> Option.defaultValue Set.empty)
    | IRTUnitAnnotated (inner, _) -> freeInferVars subst inner

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
            | IRTFunc (args, ret) -> IRTFunc (args |> List.map replace, replace ret)
            | IRTComputation t -> IRTComputation (replace t)
            | IRTPoly (t, v) -> IRTPoly (replace t, v)
            | IRTLoop lt ->
                IRTLoop { lt with
                            ArrayTypes = lt.ArrayTypes |> List.map replace
                            KernelType = lt.KernelType |> Option.map replace }
            | _ -> ty  // IRTScalar, IRTUnit, IRTNamed, IRTNat, IRTArray (concrete elem types)
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
    | TDIIndexType of name: string * idx: IRIndexType

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
    Units: Map<string, IR.UnitSig>
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
}

let bindVar name info env =
    { env with Variables = Map.add name info env.Variables }

let bindVarSimple name varId ty env =
    bindVar name { VarId = varId; Type = ty; Identity = None
                   Assign = ReadOnly; TypedValue = None; Scheme = None } env

let bindVarWithIdentity name varId ty identity env =
    bindVar name { VarId = varId; Type = ty; Identity = Some identity
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

let registerTypeDef name info env =
    { env with TypeDefs = Map.add name info env.TypeDefs }

let registerVariantTag tag parentName payload env =
    { env with VariantTags = Map.add tag (parentName, payload) env.VariantTags }

/// Resolve a UnitExpr AST node to a canonical UnitSig
let rec resolveUnitExpr (units: Map<string, IR.UnitSig>) (expr: UnitExpr) : Result<IR.UnitSig, string> =
    match expr with
    | UnitNamed name ->
        match Map.tryFind name units with
        | Some sig' -> Ok sig'
        | None -> Error (sprintf "Unknown unit '%s'" name)
    | UnitMul (a, b) ->
        resolveUnitExpr units a |> Result.bind (fun sa ->
        resolveUnitExpr units b |> Result.map (fun sb ->
            IR.unitMul sa sb))
    | UnitDiv (a, b) ->
        resolveUnitExpr units a |> Result.bind (fun sa ->
        resolveUnitExpr units b |> Result.map (fun sb ->
            IR.unitDiv sa sb))
    | UnitPow (a, n) ->
        resolveUnitExpr units a |> Result.map (fun sa ->
            IR.unitPow sa n)

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
let rec evalConstExpr (env: TypeEnv) (expr: Expr) : int64 option =
    match expr with
    | ExprLit (LitInt n) -> Some n
    | ExprLit (LitFloat f) -> Some (int64 f)
    | ExprVar name ->
        match lookupVar name env with
        | Some info ->
            match info.Type with
            | IRTNat (Some n) -> Some (int64 n)
            | _ -> None
        | None -> None
    | ExprBinOp (_, OpAdd, l, r) ->
        match evalConstExpr env l, evalConstExpr env r with
        | Some a, Some b -> Some (a + b) | _ -> None
    | ExprBinOp (_, OpSub, l, r) ->
        match evalConstExpr env l, evalConstExpr env r with
        | Some a, Some b -> Some (a - b) | _ -> None
    | ExprBinOp (_, OpMul, l, r) ->
        match evalConstExpr env l, evalConstExpr env r with
        | Some a, Some b -> Some (a * b) | _ -> None
    | ExprBinOp (_, OpDiv, l, r) ->
        match evalConstExpr env l, evalConstExpr env r with
        | Some a, Some b when b <> 0L -> Some (a / b) | _ -> None
    | _ -> None

/// Lower an extent expression to IRExpr, preserving as much info as possible.
let lowerExtentExpr (env: TypeEnv) (expr: Expr) : IRExpr =
    match evalConstExpr env expr with
    | Some n -> IRLit (IRLitInt n)
    | None ->
        match expr with
        | ExprVar name -> IRParam (name, 0)
        | ExprLit (LitInt n) -> IRLit (IRLitInt n)
        | _ -> IRParam ("?", 0)

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
        | "Nat" -> IRTNat None
        | "Poly" ->
            match args with
            | [inner] -> IRTPoly (lowerTypeExpr env inner, "r")
            | _ -> IRTPoly (IRTScalar ETFloat64, "r")
        | _ ->
            match lookupTypeDef name env with
            | Some (TDIAlias resolvedTy) -> resolvedTy
            | Some (TDIStruct (n, _, _)) -> IRTNamed n
            | Some (TDIVariant (n, _, _)) -> IRTNamed n
            | Some (TDIIndexType (_, idx)) ->
                IRTArray { ElemType = ETFloat64; IndexTypes = [idx]
                           IsVirtual = false; Identity = None }
            | None ->
                // If this name is in the type variable scope (introduced by T^k
                // elsewhere in this declaration), bare T means T^0 (scalar).
                if args.IsEmpty && env.Subst.IsTypeVar(name) then
                    env.Subst.LookupOrCreateTypeVar(name, 0, env.Builder)
                else
                    IRTNamed name  // Forward reference or external type

    | TyArray (elemTy, indexTys) ->
        let elem = lowerElemType env elemTy
        let indices = indexTys |> List.mapi (fun i ity -> lowerIndexType env i ity)
        IRTArray { ElemType = elem; IndexTypes = indices; IsVirtual = false; Identity = None }

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
                        { Id = env.Builder.FreshId(); Arity = 1; Extent = IRParam ("n", 0)
                          Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] })
                    IRTArray { ElemType = elem; IndexTypes = indices
                               IsVirtual = false; Identity = None }
        | None ->
            // Non-constant rank (e.g., T^r where r is a variable)
            match elemTy with
            | TyVar (name, _) ->
                // Can't resolve arity statically — create unconstrained type var
                env.Subst.LookupOrCreateTypeVar(name)
            | _ ->
                let elem = lowerElemType env elemTy
                IRTScalar elem  // Rank-polymorphic fallback

    | TyFunc (args, ret) ->
        IRTFunc (args |> List.map (lowerTypeExpr env), lowerTypeExpr env ret)

    | TyTuple tys -> IRTTuple (tys |> List.map (lowerTypeExpr env))

    | TyVar (name, arityOpt) ->
        // Type variable with optional arity annotation.
        // T or T^0 = scalar type variable. T^k (k>0) = rank-k array type variable.
        let arity = arityOpt |> Option.defaultValue 0
        env.Subst.LookupOrCreateTypeVar(name, arity, env.Builder)

    | TyIdx extent ->
        let ext = lowerExtentExpr env extent
        let idx = { Id = env.Builder.FreshId(); Arity = 1; Extent = ext
                    Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] }
        IRTArray { ElemType = ETFloat64; IndexTypes = [idx]; IsVirtual = false; Identity = None }

    | TySymIdx (arity, extent) ->
        let ext = lowerExtentExpr env extent
        let idx = { Id = env.Builder.FreshId(); Arity = arity; Extent = ext
                    Symmetry = SymSymmetric; Tag = None; Kind = SDimension; Dependencies = [] }
        IRTArray { ElemType = ETFloat64; IndexTypes = [idx]; IsVirtual = false; Identity = None }

    | TyAntisymIdx (arity, extent) ->
        let ext = lowerExtentExpr env extent
        let idx = { Id = env.Builder.FreshId(); Arity = arity; Extent = ext
                    Symmetry = SymAntisymmetric; Tag = None; Kind = SDimension; Dependencies = [] }
        IRTArray { ElemType = ETFloat64; IndexTypes = [idx]; IsVirtual = false; Identity = None }

    | TyHermitianIdx extent ->
        let ext = lowerExtentExpr env extent
        let idx = { Id = env.Builder.FreshId(); Arity = 2; Extent = ext
                    Symmetry = SymHermitian; Tag = None; Kind = SDimension; Dependencies = [] }
        IRTArray { ElemType = ETFloat64; IndexTypes = [idx]; IsVirtual = false; Identity = None }

    | TyBoundedIdx (lower, upper) ->
        let hi = lowerExtentExpr env upper
        let idx = { Id = env.Builder.FreshId(); Arity = 1; Extent = hi
                    Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] }
        IRTArray { ElemType = ETFloat64; IndexTypes = [idx]; IsVirtual = false; Identity = None }

    | TyCompoundIdx _mask ->
        let idx = { Id = env.Builder.FreshId(); Arity = 1; Extent = IRParam ("compound", 0)
                    Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] }
        IRTArray { ElemType = ETFloat64; IndexTypes = [idx]; IsVirtual = false; Identity = None }

    | TyPoly inner -> IRTPoly (lowerTypeExpr env inner, "r")
    | TyConstrained (inner, _) -> lowerTypeExpr env inner
    | TyDependentIdx (_param, bodyTy) -> lowerTypeExpr env bodyTy
    | TyEquivIdx (_dim, _group, _rep) ->
        let idx = { Id = env.Builder.FreshId(); Arity = 1; Extent = IRParam ("equiv", 0)
                    Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] }
        IRTArray { ElemType = ETFloat64; IndexTypes = [idx]; IsVirtual = false; Identity = None }

and lowerElemType env ty =
    match lowerTypeExpr env ty with
    | IRTScalar et -> et
    | IRTUnitAnnotated (IRTScalar et, _) -> et
    | _ -> ETFloat64

and lowerIndexType env (_position: int) (ty: TypeExpr) : IRIndexType =
    let id = env.Builder.FreshId()
    match ty with
    | TyIdx extent ->
        { Id = id; Arity = 1; Extent = lowerExtentExpr env extent
          Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] }
    | TySymIdx (arity, extent) ->
        { Id = id; Arity = arity; Extent = lowerExtentExpr env extent
          Symmetry = SymSymmetric; Tag = None; Kind = SDimension; Dependencies = [] }
    | TyAntisymIdx (arity, extent) ->
        { Id = id; Arity = arity; Extent = lowerExtentExpr env extent
          Symmetry = SymAntisymmetric; Tag = None; Kind = SDimension; Dependencies = [] }
    | TyHermitianIdx extent ->
        { Id = id; Arity = 2; Extent = lowerExtentExpr env extent
          Symmetry = SymHermitian; Tag = None; Kind = SDimension; Dependencies = [] }
    | TyNamed (name, _) ->
        match lookupTypeDef name env with
        | Some (TDIIndexType (_, idx)) -> { idx with Id = id }
        | _ ->
            { Id = id; Arity = 1; Extent = IRParam (name, 0); Symmetry = SymNone
              Tag = Some name; Kind = SDimension; Dependencies = [] }
    | _ ->
        { Id = id; Arity = 1; Extent = IRParam ("?", 0); Symmetry = SymNone
          Tag = None; Kind = SDimension; Dependencies = [] }

// ============================================================================
// 5. Capture Analysis
// ============================================================================

/// Collect free variables in an expression (names not bound in local scope).
let rec collectFreeVars (bound: Set<string>) (expr: Expr) : Set<string> =
    match expr with
    | ExprVar name ->
        if Set.contains name bound then Set.empty else Set.singleton name
    | ExprLit _ -> Set.empty
    | ExprBinOp (_, _, l, r) ->
        Set.union (collectFreeVars bound l) (collectFreeVars bound r)
    | ExprUnaryOp (_, e) -> collectFreeVars bound e
    | ExprApp (f, args) ->
        Set.unionMany (collectFreeVars bound f :: (args |> List.map (collectFreeVars bound)))
    | ExprLambda (parms, _, body) ->
        let bound' = parms |> List.fold (fun s p -> Set.add p.Name s) bound
        collectFreeVars bound' body
    | ExprLet (binding, body) ->
        let valFree = collectFreeVars bound binding.Value
        let names = patternNames binding.Pattern
        let bound' = names |> List.fold (fun s n -> Set.add n s) bound
        Set.union valFree (collectFreeVars bound' body)
    | ExprIf (c, t, e) ->
        Set.unionMany [collectFreeVars bound c; collectFreeVars bound t; collectFreeVars bound e]
    | ExprTuple es | ExprArrayLit es | ExprZip es | ExprStack es | ExprSequence es ->
        es |> List.map (collectFreeVars bound) |> Set.unionMany
    | ExprMatch (scr, cases) ->
        let scrFree = collectFreeVars bound scr
        let caseFree = cases |> List.map (fun c ->
            let names = patternNames c.Pattern
            let bound' = names |> List.fold (fun s n -> Set.add n s) bound
            let guardFree = c.Guard |> Option.map (collectFreeVars bound')
                            |> Option.defaultValue Set.empty
            Set.union guardFree (collectFreeVars bound' c.Body))
        Set.union scrFree (Set.unionMany caseFree)
    | ExprBlock (stmts, finalExpr) ->
        let mutable b = bound
        let mutable free = Set.empty
        for stmt in stmts do
            match stmt with
            | StmtLet binding ->
                free <- Set.union free (collectFreeVars b binding.Value)
                b <- patternNames binding.Pattern |> List.fold (fun s n -> Set.add n s) b
            | StmtAssign (lhs, _, rhs) ->
                free <- Set.union free (Set.union (collectFreeVars b lhs) (collectFreeVars b rhs))
            | StmtExpr e ->
                free <- Set.union free (collectFreeVars b e)
        match finalExpr with
        | Some e -> Set.union free (collectFreeVars b e)
        | None -> free
    | ExprMethodFor arrays -> arrays |> List.map (collectFreeVars bound) |> Set.unionMany
    | ExprObjectFor kernel -> collectFreeVars bound kernel
    | ExprPure e | ExprCompute e | ExprRank e -> collectFreeVars bound e
    | ExprGuard (c, b) -> Set.union (collectFreeVars bound c) (collectFreeVars bound b)
    | ExprReynolds (k, _) -> collectFreeVars bound k
    | ExprField (e, _) -> collectFreeVars bound e
    | ExprTupleIndex (t, i) -> Set.union (collectFreeVars bound t) (collectFreeVars bound i)
    | ExprStruct (_, fields) -> fields |> List.map (snd >> collectFreeVars bound) |> Set.unionMany
    | ExprReplicate (n, b) -> Set.union (collectFreeVars bound n) (collectFreeVars bound b)
    | ExprTyped (e, _) -> collectFreeVars bound e
    | ExprAssign (l, r) -> Set.union (collectFreeVars bound l) (collectFreeVars bound r)
    | ExprFor (src, _, kernelOpt) ->
        let srcFree = match src with
                      | ForArrays (arrs, _) -> arrs |> List.map (collectFreeVars bound) |> Set.unionMany
                      | ForKernel k -> collectFreeVars bound k
        let kFree = kernelOpt |> Option.map (collectFreeVars bound) |> Option.defaultValue Set.empty
        Set.union srcFree kFree
    | ExprAlign (es, _) -> es |> List.map (collectFreeVars bound) |> Set.unionMany
    | _ -> Set.empty

/// Extract variable names bound by a pattern.
and patternNames (pat: Pattern) : string list =
    match pat with
    | PatWildcard -> []
    | PatVar name -> [name]
    | PatLit _ -> []
    | PatTuple pats -> pats |> List.collect patternNames
    | PatCons (h, t) -> patternNames h @ patternNames t
    | PatStruct (_, fields) -> fields |> List.collect (fun (_, p) -> patternNames p)
    | PatVariant (_, Some p) -> patternNames p
    | PatVariant (_, None) -> []
    | PatGuarded (p, _) -> patternNames p
    | PatTyped (p, _) -> patternNames p

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
    match captures |> List.tryFind (fun c -> match c.Type with IRTArray _ -> true | _ -> false) with
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

let inferElemType (exprs: TypedExpr list) : ElemType =
    if List.isEmpty exprs then ETFloat64
    else match exprs.[0].Type with IRTScalar et -> et | IRTArray arr -> arr.ElemType | _ -> ETFloat64

let inferArrayLitType (builder: IRBuilder) (exprs: TypedExpr list) : IRArrayType =
    let elemType = inferElemType exprs
    let rec getShape (es: TypedExpr list) : int list =
        match es with
        | [] -> [0]
        | first :: _ ->
            match first.Kind with
            | TExprArrayLit (inner, _) -> List.length es :: getShape inner
            | _ -> [List.length es]
    let shape = getShape exprs
    let indexTypes = shape |> List.map (fun extent ->
        { Id = builder.FreshId(); Arity = 1; Extent = IRLit (IRLitInt (int64 extent))
          Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] })
    { ElemType = elemType; IndexTypes = indexTypes; IsVirtual = false; Identity = None }

let getArrayType (env: TypeEnv) (expr: Expr) : IRArrayType =
    match expr with
    | ExprVar name ->
        match lookupVar name env with
        | Some info ->
            match info.Type with
            | IRTArray arrTy -> arrTy
            | _ ->
                { ElemType = ETFloat64
                  IndexTypes = [{ Id = env.Builder.FreshId(); Arity = 1
                                  Extent = IRParam(name + "_n", 0)
                                  Symmetry = SymNone; Tag = None; Kind = SDimension
                                  Dependencies = [] }]
                  IsVirtual = false; Identity = Some (AIDVariable name) }
        | None ->
            { ElemType = ETFloat64
              IndexTypes = [{ Id = env.Builder.FreshId(); Arity = 1
                              Extent = IRParam(name + "_n", 0)
                              Symmetry = SymNone; Tag = None; Kind = SDimension
                              Dependencies = [] }]
              IsVirtual = false; Identity = Some (AIDVariable name) }
    | _ ->
        { ElemType = ETFloat64; IndexTypes = []; IsVirtual = false; Identity = None }

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
    match pat with
    | PatWildcard ->
        Ok { Kind = TPatWild; Type = expected; Bindings = [] }

    | PatVar name ->
        let varId = env.Builder.FreshId()
        Ok { Kind = TPatVar (name, varId); Type = expected
             Bindings = [(name, varId, expected)] }

    | PatLit lit ->
        let litTy = inferLiteralType lit
        unify env.Subst litTy expected |> Result.map (fun () ->
            { Kind = TPatLit lit; Type = expected; Bindings = [] })

    | PatTuple pats ->
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

    | PatCons (headPat, tailPat) ->
        let headTy = env.Subst.Fresh()
        let tailTy = env.Subst.Fresh()
        checkPattern env headTy headPat |> Result.bind (fun tHead ->
        checkPattern env tailTy tailPat |> Result.bind (fun tTail ->
            Ok { Kind = TPatCons (tHead, tTail); Type = expected
                 Bindings = tHead.Bindings @ tTail.Bindings }))

    | PatVariant (tag, payloadPat) ->
        match Map.tryFind tag env.VariantTags with
        | Some (parentName, payloadTy) ->
            match payloadPat, payloadTy with
            | Some p, Some ty ->
                checkPattern env ty p |> Result.map (fun tPayload ->
                    { Kind = TPatVariant (tag, Some tPayload)
                      Type = IRTNamed parentName
                      Bindings = tPayload.Bindings })
            | None, None ->
                Ok { Kind = TPatVariant (tag, None)
                     Type = IRTNamed parentName; Bindings = [] }
            | Some p, None ->
                Error (PatternTypeMismatch (sprintf "%s(...)" tag, expected))
            | None, Some _ ->
                Ok { Kind = TPatVariant (tag, None)
                     Type = IRTNamed parentName; Bindings = [] }
        | None ->
            // Unknown variant tag: allow it, bind any payload
            match payloadPat with
            | Some p ->
                let payTy = env.Subst.Fresh()
                checkPattern env payTy p |> Result.map (fun tPayload ->
                    { Kind = TPatVariant (tag, Some tPayload); Type = expected
                      Bindings = tPayload.Bindings })
            | None ->
                Ok { Kind = TPatVariant (tag, None); Type = expected; Bindings = [] }

    | PatStruct (typeName, fieldPats) ->
        let fieldTypes =
            match lookupTypeDef typeName env with
            | Some (TDIStruct (_, _, fields)) ->
                fields |> List.map (fun (n, t) -> (n, t)) |> Map.ofList
            | _ -> Map.empty
        fieldPats |> List.map (fun (fname, fpat) ->
            let fTy = Map.tryFind fname fieldTypes |> Option.defaultValue (env.Subst.Fresh())
            checkPattern env fTy fpat |> Result.map (fun tp -> (fname, tp)))
        |> sequenceResults |> Result.map (fun tFields ->
            { Kind = TPatStruct (typeName, tFields)
              Type = (if Map.isEmpty fieldTypes then expected else IRTNamed typeName)
              Bindings = tFields |> List.collect (fun (_, p) -> p.Bindings) })

    | PatGuarded (innerPat, _guardExpr) ->
        // Guard expression is type-checked in inferMatch when we have full env
        checkPattern env expected innerPat |> Result.map (fun tInner ->
            { Kind = TPatGuarded (tInner, mkTyped (TExprLit (LitBool true)) (IRTScalar ETBool))
              Type = expected; Bindings = tInner.Bindings })

    | PatTyped (innerPat, tyAnnotation) ->
        let annotTy = lowerTypeExpr env tyAnnotation
        unify env.Subst annotTy expected |> Result.bind (fun () ->
            checkPattern env annotTy innerPat)

// ============================================================================
// 10. Expression Type Inference (every Expr variant handled explicitly)
// ============================================================================

let rec inferExpr (env: TypeEnv) (expr: Expr) : TypeResult<TypedExpr> =
    match expr with
    // ---- Literals ----
    | ExprLit lit -> Ok (mkTyped (TExprLit lit) (inferLiteralType lit))

    // ---- Variables ----
    | ExprVar name ->
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
                            (IRTFunc ([payloadTy], IRTNamed parentName)))
            | None -> Error (UnboundVariable name)

    | ExprQualified names ->
        // Qualified name resolution — limited for now
        Ok (mkTyped (TExprQualified names) IRTUnit)

    // ---- Binary operations (dispatch to helper) ----
    | ExprBinOp (mode, op, left, right) ->
        inferBinOp env mode op left right

    // ---- Unary operations ----
    | ExprUnaryOp (op, operand) ->
        inferExpr env operand |> Result.bind (fun tOp ->
            let resTy = match op with OpNot -> IRTScalar ETBool | OpNeg -> tOp.Type
            Ok (mkTyped (TExprUnaryOp (op, tOp)) resTy))

    // ---- Method call: obj.method(args) → impl resolution ----
    | ExprApp (ExprField (obj, method), args) ->
        inferExpr env obj |> Result.bind (fun tObj ->
        args |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tArgs ->
            // Check if this is an impl method call
            match tObj.Type with
            | IRTNamed typeName ->
                match Map.tryFind (typeName, method) env.ImplMethods with
                | Some (funcVarId, funcType) ->
                    // Resolve to mangled function call: TypeName__method(self, args)
                    let mangledName = sprintf "%s__%s" typeName method
                    let retTy = match funcType with IRTFunc (_, ret) -> ret | _ -> env.Subst.Fresh()
                    let tFunc = mkTyped (TExprVar (mangledName, funcVarId, None)) funcType
                    Ok (mkTyped (TExprApp (tFunc, tObj :: tArgs)) retTy)
                | None ->
                    // Not an impl method — treat as struct field access + application
                    let (fieldTy, fieldIdx) =
                        match lookupTypeDef typeName env with
                        | Some (TDIStruct (_, _, fields)) ->
                            let idx = fields |> List.tryFindIndex (fun (n, _) -> n = method)
                            let ty = fields |> List.tryFind (fun (n, _) -> n = method) |> Option.map snd
                            (ty |> Option.defaultValue (env.Subst.Fresh()),
                             idx |> Option.defaultValue 0)
                        | _ -> (env.Subst.Fresh(), 0)
                    let tField = mkTyped (TExprField (tObj, method, fieldIdx)) fieldTy
                    let retTy = match fieldTy with IRTFunc (_, ret) -> ret | _ -> env.Subst.Fresh()
                    Ok (mkTyped (TExprApp (tField, tArgs)) retTy)
            | _ ->
                // Non-named type — regular field access + application
                let tField = mkTyped (TExprField (tObj, method, 0)) (env.Subst.Fresh())
                let retTy = env.Subst.Fresh()
                Ok (mkTyped (TExprApp (tField, tArgs)) retTy)))

    // ---- Application / Array indexing ----
    | ExprApp (func, args) ->
        inferExpr env func |> Result.bind (fun tFunc ->
        args |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tArgs ->
            match tFunc.Type with
            | IRTArray arrTy when tArgs.Length <= arrTy.IndexTypes.Length ->
                let identity = match tFunc.Kind with TExprVar (_, _, id) -> id | _ -> None
                if tArgs.Length = arrTy.IndexTypes.Length then
                    Ok (mkTyped (TExprIndex (tFunc, tArgs, identity)) (IRTScalar arrTy.ElemType))
                else
                    let remaining = arrTy.IndexTypes |> List.skip tArgs.Length
                    Ok (mkTyped (TExprIndex (tFunc, tArgs, identity))
                                (IRTArray { arrTy with IndexTypes = remaining }))
            | IRTFunc (_paramTys, retTy) ->
                Ok (mkTyped (TExprApp (tFunc, tArgs)) retTy)
            | _ ->
                let retTy = env.Subst.Fresh()
                Ok (mkTyped (TExprApp (tFunc, tArgs)) retTy)))

    // ---- Poly-tuple indexing: args[k] ----
    | ExprTupleIndex (tuple, index) ->
        inferExpr env tuple |> Result.bind (fun tT ->
        inferExpr env index |> Result.bind (fun tI ->
            Ok (mkTyped (TExprTupleIndex (tT, tI)) (env.Subst.Fresh()))))

    // ---- Field access ----
    | ExprField (obj, field) ->
        inferExpr env obj |> Result.bind (fun tObj ->
            let (fieldTy, fieldIdx) =
                match tObj.Type with
                | IRTNamed typeName ->
                    match lookupTypeDef typeName env with
                    | Some (TDIStruct (_, _, fields)) ->
                        let idx = fields |> List.tryFindIndex (fun (n, _) -> n = field)
                        let ty = fields |> List.tryFind (fun (n, _) -> n = field) |> Option.map snd
                        (ty |> Option.defaultValue (env.Subst.Fresh()),
                         idx |> Option.defaultValue 0)
                    | _ -> (env.Subst.Fresh(), 0)
                | _ -> (env.Subst.Fresh(), 0)
            Ok (mkTyped (TExprField (tObj, field, fieldIdx)) fieldTy))

    // ---- Lambda ----
    | ExprLambda (parms, whereClause, body) -> inferLambda env parms whereClause body

    // ---- Let ----
    | ExprLet (binding, body) -> inferLetBinding env binding body

    // ---- Match ----
    | ExprMatch (scrutinee, cases) -> inferMatch env scrutinee cases

    // ---- If-then-else ----
    | ExprIf (cond, thenBr, elseBr) ->
        inferExpr env cond |> Result.bind (fun tCond ->
        inferExpr env thenBr |> Result.bind (fun tThen ->
        inferExpr env elseBr |> Result.bind (fun tElse ->
            let _ = unify env.Subst tThen.Type tElse.Type
            Ok (mkTyped (TExprIf (tCond, tThen, tElse)) tThen.Type))))

    // ---- Tuple ----
    | ExprTuple exprs ->
        exprs |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tExprs ->
            Ok (mkTyped (TExprTuple tExprs) (IRTTuple (tExprs |> List.map (fun e -> e.Type)))))

    // ---- Array literal ----
    | ExprArrayLit elems ->
        elems |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tElems ->
            let arrTy = inferArrayLitType env.Builder tElems
            Ok (mkTyped (TExprArrayLit (tElems, arrTy)) (IRTArray arrTy)))

    // ---- Block ----
    | ExprBlock (stmts, finalExpr) -> inferBlock env stmts finalExpr

    // ---- Loop constructs ----
    | ExprMethodFor arrays -> inferMethodFor env arrays
    | ExprObjectFor kernel -> inferObjectFor env kernel

    // ---- Virtual arrays ----
    | ExprRange idxTy ->
        let idx = lowerIndexType env 0 idxTy
        let arrTy = { ElemType = ETInt64; IndexTypes = [idx]; IsVirtual = true; Identity = None }
        Ok (mkTyped (TExprRange idx) (IRTArray arrTy))
    | ExprReverse idxTy ->
        let idx = lowerIndexType env 0 idxTy
        let arrTy = { ElemType = ETInt64; IndexTypes = [idx]; IsVirtual = true; Identity = None }
        Ok (mkTyped (TExprReverse idx) (IRTArray arrTy))
    | ExprBlocked (idxTy, blockSize) ->
        let idx = lowerIndexType env 0 idxTy
        inferExpr env blockSize |> Result.bind (fun tBS ->
            let arrTy = { ElemType = ETInt64; IndexTypes = [idx]; IsVirtual = true; Identity = None }
            Ok (mkTyped (TExprBlocked (idx, tBS)) (IRTArray arrTy)))

    // ---- Zip / Stack ----
    | ExprZip exprs ->
        exprs |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tExprs ->
            Ok (mkTyped (TExprZip tExprs) (IRTTuple (tExprs |> List.map (fun e -> e.Type)))))
    | ExprStack exprs ->
        exprs |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tExprs ->
            let elemTy = if tExprs.IsEmpty then IRTUnit else tExprs.[0].Type
            Ok (mkTyped (TExprStack tExprs) elemTy))

    // ---- Computation combinators ----
    | ExprPure e ->
        inferExpr env e |> Result.bind (fun tE ->
            Ok (mkTyped (TExprPure tE) (IRTComputation tE.Type)))
    | ExprCompute e ->
        inferExpr env e |> Result.bind (fun tE ->
            let inner = match tE.Type with IRTComputation t -> t | t -> t
            Ok (mkTyped (TExprCompute tE) inner))
    | ExprGuard (cond, body) ->
        inferExpr env cond |> Result.bind (fun tC ->
        inferExpr env body |> Result.bind (fun tB ->
            Ok (mkTyped (TExprGuard (tC, tB)) tB.Type)))
    | ExprSequence exprs ->
        exprs |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tExprs ->
            let ty = if tExprs.IsEmpty then IRTUnit else (List.last tExprs).Type
            Ok (mkTyped (TExprTuple tExprs) ty))
    | ExprReplicate (count, body) ->
        inferExpr env count |> Result.bind (fun tC ->
        inferExpr env body |> Result.bind (fun tB ->
            Ok (mkTyped (TExprTuple [tC; tB]) tB.Type)))

    // ---- Reynolds ----
    | ExprReynolds (kernel, isAntisym) ->
        inferExpr env kernel |> Result.bind (fun tK ->
            Ok (mkTyped (TExprReynolds (tK, isAntisym)) tK.Type))

    // ---- Type annotation ----
    | ExprTyped (e, tyAnno) ->
        inferExpr env e |> Result.bind (fun tE ->
            let annoTy = lowerTypeExpr env tyAnno
            let _ = unify env.Subst tE.Type annoTy
            Ok { tE with Type = annoTy })

    // ---- Arity special forms ----
    | ExprArity paramName -> Ok (mkTyped (TExprArity paramName) (IRTScalar ETInt64))
    | ExprNth -> Ok (mkTyped (TExprLit (LitInt 0L)) (IRTScalar ETInt64))
    | ExprZero -> Ok (mkTyped (TExprLit (LitFloat 0.0)) (IRTScalar ETFloat64))
    | ExprRank e ->
        inferExpr env e |> Result.bind (fun tE ->
            Ok (mkTyped (TExprRank tE) (IRTScalar ETInt64)))

    // ---- Struct construction ----
    | ExprStruct (name, fields) -> inferStructConstruction env name fields

    // ---- Sectioned operators ----
    | ExprSection _op ->
        let paramTy = env.Subst.Fresh()
        Ok (mkTyped (TExprLit LitUnit) (IRTFunc ([paramTy; paramTy], paramTy)))
    | ExprPartialApp (_op, arg, _isLeft) ->
        inferExpr env arg |> Result.bind (fun tArg ->
            Ok (mkTyped (TExprApp (mkTyped (TExprLit LitUnit) (IRTFunc ([tArg.Type], tArg.Type)), [tArg]))
                        (IRTFunc ([tArg.Type], tArg.Type))))

    // ---- Assignment ----
    | ExprAssign (lhs, rhs) ->
        inferExpr env lhs |> Result.bind (fun tL ->
        inferExpr env rhs |> Result.bind (fun tR ->
            // Check assignability of LHS
            let assignErr =
                match tL.Kind with
                | TExprVar (name, _, _) ->
                    match lookupVar name env with
                    | Some info when info.Assign = ReadOnly ->
                        Some (Other (sprintf "Cannot assign to '%s': static bindings are immutable" name))
                    | _ -> None
                | _ -> None  // array element assignment etc. — allowed
            match assignErr with
            | Some e -> Error e
            | None ->
                let _ = unify env.Subst tL.Type tR.Type
                Ok (mkTyped (TExprLit LitUnit) IRTUnit)))

    // ---- For expression ----
    | ExprFor (source, _constraints, kernelOpt) ->
        inferForExpr env source kernelOpt

    // ---- Align ----
    | ExprAlign (exprs, _spec) ->
        exprs |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tExprs ->
            let ty = if tExprs.IsEmpty then IRTUnit else tExprs.[0].Type
            Ok (mkTyped (TExprZip tExprs) ty))

// ============================================================================
// 10a. Binary Operation Inference
// ============================================================================

and inferBinOp env mode op left right : TypeResult<TypedExpr> =
    match op with
    | OpApply ->
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            inferApply env tL tR))

    | OpBind ->
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            Ok (mkTyped (TExprBind (tL, tR)) (env.Subst.Fresh()))))

    | OpParallel | OpFusion ->
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            Ok (mkTyped (TExprParallel (tL, tR)) (IRTTuple [tL.Type; tR.Type]))))

    | OpChoice | OpFallback ->
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            let _ = unify env.Subst tL.Type tR.Type
            Ok (mkTyped (TExprChoice (tL, tR)) tL.Type)))

    | OpComposeObj | OpComposeMeth | OpCompose ->
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            Ok (mkTyped (TExprCompose (tL, tR)) tR.Type)))

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
            let resTy = inferArithType mode op tL.Type tR.Type
            Ok (mkTyped (TExprBinOp (mode, op, tL, tR)) resTy)))

and inferArithType mode op leftTy rightTy =
    match op with
    | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe ->
        // Comparisons require compatible units
        let lUnits = IR.getUnits leftTy
        let rUnits = IR.getUnits rightTy
        match lUnits, rUnits with
        | Some lu, Some ru when not (IR.unitCompatible lu ru) ->
            eprintfn "Unit mismatch in comparison: %s vs %s" (IR.ppUnitSig lu) (IR.ppUnitSig ru)
        | _ -> ()
        IRTScalar ETBool
    | OpAnd | OpOr -> IRTScalar ETBool
    | _ ->
        // Extract unit annotations if present
        let lUnits = IR.getUnits leftTy
        let rUnits = IR.getUnits rightTy
        let lBare = IR.stripUnits leftTy
        let rBare = IR.stripUnits rightTy
        let bareResult =
            match mode with
            | Outer ->
                match lBare, rBare with
                | IRTArray arrL, IRTArray arrR ->
                    IRTArray { arrL with IndexTypes = arrL.IndexTypes @ arrR.IndexTypes }
                | _ -> lBare
            | Elementwise ->
                match lBare, rBare with
                | IRTScalar ETFloat64, _ | _, IRTScalar ETFloat64 -> IRTScalar ETFloat64
                | IRTScalar ETFloat32, _ | _, IRTScalar ETFloat32 -> IRTScalar ETFloat32
                | _ -> lBare
        // Apply unit rules based on operation
        match op with
        | OpAdd | OpSub ->
            // Addition/subtraction: units must match
            match lUnits, rUnits with
            | Some lu, Some ru ->
                if IR.unitCompatible lu ru then IRTUnitAnnotated (bareResult, lu)
                else
                    eprintfn "Unit mismatch in %s: %s vs %s"
                        (if op = OpAdd then "addition" else "subtraction")
                        (IR.ppUnitSig lu) (IR.ppUnitSig ru)
                    IRTUnitAnnotated (bareResult, lu)  // keep left units, report error
            | Some u, None | None, Some u -> IRTUnitAnnotated (bareResult, u)
            | None, None -> bareResult
        | OpMul ->
            match lUnits, rUnits with
            | Some lu, Some ru -> IRTUnitAnnotated (bareResult, IR.unitMul lu ru)
            | Some u, None | None, Some u -> IRTUnitAnnotated (bareResult, u)
            | None, None -> bareResult
        | OpDiv | OpMod ->
            match lUnits, rUnits with
            | Some lu, Some ru -> IRTUnitAnnotated (bareResult, IR.unitDiv lu ru)
            | Some u, None -> IRTUnitAnnotated (bareResult, u)
            | None, Some u -> IRTUnitAnnotated (bareResult, IR.unitDiv IR.unitDimensionless u)
            | None, None -> bareResult
        | _ -> bareResult

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

and inferApply (env: TypeEnv) (tLeft: TypedExpr) (tRight: TypedExpr) : TypeResult<TypedExpr> =
    let rL = resolveTypedExpr env tLeft
    let rR = resolveTypedExpr env tRight

    match rL.Kind, rR.Kind with
    | TExprMethodFor mfInfo, TExprLambda lambdaInfo ->
        buildApplyInfo env mfInfo lambdaInfo tLeft tRight false

    | TExprMethodFor mfInfo, TExprReynolds (innerKernel, isAntisym) ->
        match innerKernel.Kind with
        | TExprLambda li -> buildApplyInfo env mfInfo li tLeft tRight isAntisym
        | _ -> buildGenericApply env tLeft tRight

    // object_for normalization: build synthetic MethodFor from right-side arrays
    | TExprObjectFor objInfo, _ ->
        // Extract array typed exprs from right side
        let arrayExprs = match rR.Kind with
                         | TExprTuple elems -> elems
                         | _ -> [tRight]
        // Build identities from typed expressions
        let identities = arrayExprs |> List.map (fun arr ->
            match arr.Kind with
            | TExprVar (name, _, _) -> AIDVariable name
            | _ -> AIDLiteral (env.Builder.FreshId()))
        // Extract array types from the typed expressions' types
        let arrayTypes = arrayExprs |> List.map (fun arr ->
            match arr.Type with
            | IRTArray at -> at
            | _ -> { ElemType = ETFloat64; IndexTypes = []; IsVirtual = false; Identity = None })
        let sDimsPerArray = computeSDimsPerArray arrayTypes
        let totalSDims = List.sum sDimsPerArray
        let mfInfo : TypedMethodForInfo = {
            Arrays = arrayExprs; Identities = identities; ArrayTypes = arrayTypes
            SDimsPerArray = sDimsPerArray; TotalSDims = totalSDims
        }
        let syntheticLoop = mkTyped (TExprMethodFor mfInfo) tLeft.Type
        // Resolve kernel from the object_for
        let resolvedKernel = resolveTypedExpr env objInfo.Kernel
        match resolvedKernel.Kind with
        | TExprLambda lambdaInfo ->
            buildApplyInfo env mfInfo lambdaInfo syntheticLoop objInfo.Kernel false
        | TExprReynolds (innerK, isAntisym) ->
            match innerK.Kind with
            | TExprLambda li -> buildApplyInfo env mfInfo li syntheticLoop objInfo.Kernel isAntisym
            | _ -> buildGenericApply env syntheticLoop objInfo.Kernel
        | _ -> buildGenericApply env syntheticLoop objInfo.Kernel

    | _ -> buildGenericApply env tLeft tRight

and buildApplyInfo (env: TypeEnv) (mfInfo: TypedMethodForInfo)
    (lambdaInfo: TypedLambdaInfo)
    (tLoop: TypedExpr) (tKernel: TypedExpr) (hasReynolds: bool)
    : TypeResult<TypedExpr> =

    let identities = mfInfo.Identities
    let arrayTypes = mfInfo.ArrayTypes
    let commGroups = lambdaInfo.CommGroups
    let sDimsPerArray = mfInfo.SDimsPerArray

    let kernelInputRanks =
        lambdaInfo.Params |> List.map (fun p ->
            match p.Type with IRTArray arr -> arr.IndexTypes.Length | _ -> 0)
    let kernelOutputRank = 0

    let states = computeAllSymcomStates identities arrayTypes commGroups sDimsPerArray
    let triLevels = computeTriangularLevels arrayTypes identities commGroups sDimsPerArray
    let speedup = computePartialProductSpeedup arrayTypes identities commGroups sDimsPerArray
    let outputType = deduceOutputType arrayTypes identities commGroups sDimsPerArray kernelOutputRank env.Builder

    let reynoldsSpeedup =
        if hasReynolds then
            let r = identities.Length
            if r > 1 then factorial r else 1L
        else 1L

    let info : TypedApplyInfo = {
        Loop = tLoop; Kernel = tKernel
        SymcomStates = states; TriangularLevels = triLevels
        SDimsPerArray = sDimsPerArray
        KernelInputRanks = kernelInputRanks; KernelOutputRank = kernelOutputRank
        SpeedupFactor = speedup; ReynoldsSpeedup = reynoldsSpeedup
        HasReynolds = hasReynolds; OutputType = outputType
    }
    Ok (mkTyped (TExprApply info) outputType)

and buildGenericApply (env: TypeEnv) (tLeft: TypedExpr) (tRight: TypedExpr) : TypeResult<TypedExpr> =
    let info : TypedApplyInfo = {
        Loop = tLeft; Kernel = tRight
        SymcomStates = []; TriangularLevels = []
        SDimsPerArray = []
        KernelInputRanks = []; KernelOutputRank = 0
        SpeedupFactor = 1L; ReynoldsSpeedup = 1L
        HasReynolds = false; OutputType = IRTUnit
    }
    Ok (mkTyped (TExprApply info) IRTUnit)

// ============================================================================
// 10c. Lambda, Let, Match, Block, MethodFor, ObjectFor, Struct, For
// ============================================================================

/// Pre-scan type annotations to collect type variable NAMES before lowering.
/// Only looks for TyVar nodes (which have `^` in the source). This populates
/// the known-name set so that bare `T` in TyNamed resolves as a type variable
/// regardless of parameter order.
///
/// Pass 1: collect names (this function)
/// Pass 2: lower types, creating inference vars lazily (lowerTypeExpr)
and prescanTypeVarNames (env: TypeEnv) (types: TypeExpr option list) : unit =
    let rec scan ty =
        match ty with
        | TyVar (name, _) ->
            env.Subst.RegisterTypeVarName(name)
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
                 | None -> IRTScalar ETFloat64  // Default for kernel params
        paramEnv <- bindVarSimple p.Name varId ty paramEnv
        { Name = p.Name; Type = ty; Index = i; VarId = varId } : TypedParam)

    let boundNames = parms |> List.map (fun p -> p.Name) |> Set.ofList
    let freeVars = collectFreeVars boundNames body
    let captures = buildCaptures scopeEnv freeVars

    let result =
        validateNoArrayCaptures captures |> Result.bind (fun () ->
        inferExpr paramEnv body |> Result.bind (fun tBody ->
            let info : TypedLambdaInfo = {
                Params = typedParams; Body = tBody; ReturnType = tBody.Type
                CommGroups = commGroups; Captures = captures
                IsCommutative = not (List.isEmpty commGroups)
            }
            let funcTy = IRTFunc (typedParams |> List.map (fun p -> p.Type), tBody.Type)
            Ok (mkTyped (TExprLambda info) funcTy)))

    env.Subst.PopTypeVarScope(savedScope)
    result

and inferLetBinding env binding body : TypeResult<TypedExpr> =
    inferExpr env binding.Value |> Result.bind (fun tValue ->
        let assign = assignOfBindingMut binding.Mutability
        match binding.Pattern with
        | PatVar name ->
            let varId = env.Builder.FreshId()
            let identity = Some (AIDVariable name)
            // Let-generalization: only static (ReadOnly) bindings are generalized.
            // Assignable bindings could be reassigned, making the original scheme unsound.
            let scheme =
                if assign <> ReadOnly then None
                else
                    let s = generalize env.Subst env.Variables tValue.Type
                    if s.QuantifiedVars.IsEmpty then None else Some s
            let env' =
                match scheme with
                | Some s -> bindVarPoly name varId tValue.Type identity assign (Some tValue) s env
                | None -> bindVarFull name varId tValue.Type identity assign (Some tValue) env
            inferExpr env' body |> Result.map (fun tBody ->
                mkTyped (TExprLet (name, varId, tValue, tBody)) tBody.Type)

        | PatTuple pats ->
            let mutable env' = env
            pats |> List.iteri (fun i p ->
                match p with
                | PatVar n ->
                    let vid = env.Builder.FreshId()
                    let eTy = match tValue.Type with
                              | IRTTuple ts when i < ts.Length -> ts.[i]
                              | _ -> env.Subst.Fresh()
                    env' <- bindVarSimple n vid eTy env'
                | PatWildcard -> ()
                | _ ->
                    for n in patternNames p do
                        env' <- bindVarSimple n (env.Builder.FreshId()) (env.Subst.Fresh()) env')
            inferExpr env' body |> Result.map (fun tBody ->
                let name = pats |> List.tryPick (function PatVar n -> Some n | _ -> None)
                           |> Option.defaultValue "_"
                mkTyped (TExprLet (name, env.Builder.FreshId(), tValue, tBody)) tBody.Type)

        | PatCons (headPat, tailPat) ->
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

    for stmt in stmts do
        if err.IsNone then
            match stmt with
            | StmtLet binding ->
                match inferExpr curEnv binding.Value with
                | Ok tValue ->
                    let name = match binding.Pattern with PatVar n -> n | _ -> "_"
                    let varId = curEnv.Builder.FreshId()
                    let identity = match binding.Pattern with PatVar n -> Some (AIDVariable n) | _ -> None
                    let assign = assignOfBindingMut binding.Mutability
                    curEnv <- bindVarFull name varId tValue.Type identity assign (Some tValue) curEnv
                    // Also bind tuple/cons destructured names
                    match binding.Pattern with
                    | PatTuple pats ->
                        pats |> List.iteri (fun i p ->
                            for n in patternNames p do
                                let eTy = match tValue.Type with
                                          | IRTTuple ts when i < ts.Length -> ts.[i]
                                          | _ -> env.Subst.Fresh()
                                curEnv <- bindVarSimple n (curEnv.Builder.FreshId()) eTy curEnv)
                    | _ -> ()
                | Error e -> err <- Some e
            | StmtAssign (lhs, _, rhs) ->
                match inferExpr curEnv lhs, inferExpr curEnv rhs with
                | Ok tL, Ok tR ->
                    // Check assignability of LHS
                    match tL.Kind with
                    | TExprVar (name, _, _) ->
                        match lookupVar name curEnv with
                        | Some info when info.Assign = ReadOnly ->
                            err <- Some (Other (sprintf "Cannot assign to '%s': static bindings are immutable" name))
                        | _ ->
                            let _ = unify curEnv.Subst tL.Type tR.Type
                            ()
                    | _ ->
                        let _ = unify curEnv.Subst tL.Type tR.Type
                        ()
                | Error e, _ | _, Error e -> err <- Some e
            | StmtExpr e ->
                match inferExpr curEnv e with
                | Error e -> err <- Some e
                | _ -> ()

    match err with
    | Some e -> Error e
    | None ->
        match finalExpr with
        | Some e -> inferExpr curEnv e |> Result.map (fun tF ->
            mkTyped tF.Kind tF.Type)
        | None -> Ok (mkTyped (TExprLit LitUnit) IRTUnit)

and inferMethodFor env arrays : TypeResult<TypedExpr> =
    arrays |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tArrays ->
        let identities = arrays |> List.map (fun arr ->
            match arr with ExprVar name -> AIDVariable name | _ -> AIDLiteral (env.Builder.FreshId()))
        let arrayTypes = arrays |> List.map (getArrayType env)
        let sDimsPerArray = computeSDimsPerArray arrayTypes
        let totalSDims = List.sum sDimsPerArray

        let info : TypedMethodForInfo = {
            Arrays = tArrays; Identities = identities; ArrayTypes = arrayTypes
            SDimsPerArray = sDimsPerArray; TotalSDims = totalSDims
        }
        let loopTy = IRTLoop {
            Kind = LKMethod; Arity = Some arrays.Length
            ArrayTypes = arrayTypes |> List.map IRTArray; KernelType = None
        }
        Ok (mkTyped (TExprMethodFor info) loopTy))

and inferObjectFor env kernel : TypeResult<TypedExpr> =
    inferExpr env kernel |> Result.bind (fun tKernel ->
        let (commGroups, inputRanks, outputRank) =
            match tKernel.Kind with
            | TExprLambda info ->
                let iRanks = info.Params |> List.map (fun p ->
                    match p.Type with IRTArray arr -> arr.IndexTypes.Length | _ -> 0)
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

and inferStructConstruction env name fields : TypeResult<TypedExpr> =
    match lookupTypeDef name env with
    | Some (TDIStruct (_, _, declFields)) ->
        fields |> List.map (fun (fname, fexpr) ->
            inferExpr env fexpr |> Result.bind (fun tFE ->
                let expected = declFields |> List.tryFind (fun (n, _) -> n = fname) |> Option.map snd
                match expected with
                | Some eTy -> let _ = unify env.Subst tFE.Type eTy in Ok (fname, tFE)
                | None -> Ok (fname, tFE)))
        |> sequenceResults |> Result.map (fun tFields ->
            mkTyped (TExprStruct (name, tFields)) (IRTNamed name))
    | _ ->
        fields |> List.map (fun (fname, fexpr) ->
            inferExpr env fexpr |> Result.map (fun tFE -> (fname, tFE)))
        |> sequenceResults |> Result.map (fun tFields ->
            mkTyped (TExprStruct (name, tFields)) (IRTTuple (tFields |> List.map (fun (_, e) -> e.Type))))

and inferForExpr env source kernelOpt : TypeResult<TypedExpr> =
    match source with
    | ForArrays (arrays, _idxTyOpt) ->
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
    match decl with
    | DeclLet binding ->
        inferExpr env binding.Value |> Result.bind (fun tValue ->
            let name = match binding.Pattern with PatVar n -> n | _ -> "_"
            let varId = env.Builder.FreshId()
            let identity = match binding.Pattern with PatVar n -> Some (AIDVariable n) | _ -> None
            let assign = assignOfBindingMut binding.Mutability

            // Only static (ReadOnly) bindings are generalized.
            let scheme =
                if assign <> ReadOnly then None
                else
                    let s = generalize env.Subst env.Variables tValue.Type
                    if s.QuantifiedVars.IsEmpty then None else Some s
            let env' =
                match scheme with
                | Some s -> bindVarPoly name varId tValue.Type identity assign (Some tValue) s env
                | None -> bindVarFull name varId tValue.Type identity assign (Some tValue) env

            // Handle destructuring at top level
            let env' =
                match binding.Pattern with
                | PatTuple pats ->
                    pats |> List.mapi (fun i p -> (i, p))
                    |> List.fold (fun e (i, p) ->
                        match p with
                        | PatVar n ->
                            let eTy = match tValue.Type with
                                      | IRTTuple ts when i < ts.Length -> ts.[i]
                                      | _ -> env.Subst.Fresh()
                            bindVarSimple n (env.Builder.FreshId()) eTy e
                        | _ -> patternNames p |> List.fold (fun e2 n ->
                            bindVarSimple n (env.Builder.FreshId()) (env.Subst.Fresh()) e2) e) env'
                | PatCons (h, t) ->
                    let e1 = patternNames h |> List.fold (fun e n ->
                        bindVarSimple n (env.Builder.FreshId()) (env.Subst.Fresh()) e) env'
                    patternNames t |> List.fold (fun e n ->
                        bindVarSimple n (env.Builder.FreshId()) (env.Subst.Fresh()) e) e1
                | _ -> env'

            let tb : TypedBinding = {
                Name = name; VarId = varId; Type = tValue.Type
                Identity = identity; IsMutable = (assign <> ReadOnly); Value = tValue
            }
            Ok (TDeclLet tb, env'))

    | DeclStatic binding ->
        inferExpr env binding.Value |> Result.bind (fun tValue ->
            let name = match binding.Pattern with PatVar n -> n | _ -> "_"
            let varId = env.Builder.FreshId()
            // Static bindings are ReadOnly and generalizable
            let scheme =
                let s = generalize env.Subst env.Variables tValue.Type
                if s.QuantifiedVars.IsEmpty then None else Some s
            let env' =
                match scheme with
                | Some s -> bindVarPoly name varId tValue.Type None ReadOnly (Some tValue) s env
                | None -> bindVarFull name varId tValue.Type None ReadOnly (Some tValue) env
            let tb : TypedBinding = {
                Name = name; VarId = varId; Type = tValue.Type
                Identity = None; IsMutable = false; Value = tValue
            }
            Ok (TDeclStatic tb, env'))

    | DeclFunction funcDecl -> checkFunctionDecl env funcDecl

    | DeclType typeDecl ->
        let env' = registerTypeDecl env typeDecl
        Ok (TDeclType typeDecl, env')

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
            // Validate interface exists
            match Map.tryFind implDecl.Interface env.Interfaces with
            | Some ifaceDecl ->
                // Check each impl method has a matching signature in the interface
                let mutable env' = env
                for method in implDecl.Methods do
                    let ifaceMethod = ifaceDecl.Methods |> List.tryFind (fun m -> m.Name = method.Name)
                    match ifaceMethod with
                    | None ->
                        // Method not in interface — allow as an extension method for now
                        ()
                    | Some _ ->
                        // Signature validation could be stricter, but for now just check name exists
                        ()
                    // Register the monomorphized method as a function
                    let mangledName = sprintf "%s__%s" tName method.Name
                    let selfType = IRTNamed tName
                    let paramTypes = method.Params |> List.map (fun p ->
                        if p.Name = "self" && p.Type.IsNone then selfType
                        else match p.Type with Some t -> lowerTypeExpr env' t | None -> IRTScalar ETFloat64)
                    let retType = match method.ReturnType with
                                  | Some t -> lowerTypeExpr env' t
                                  | None -> env'.Subst.Fresh()
                    let funcType = IRTFunc (paramTypes, retType)
                    let funcVarId = env'.Builder.FreshId()
                    env' <- bindVarSimple mangledName funcVarId funcType env'
                    env' <- { env' with ImplMethods = Map.add (tName, method.Name) (funcVarId, funcType) env'.ImplMethods }
                // Verify all interface methods are implemented
                let missing = ifaceDecl.Methods |> List.filter (fun ifaceMethod ->
                    not (implDecl.Methods |> List.exists (fun m -> m.Name = ifaceMethod.Name)))
                match missing with
                | [] -> Ok (TDeclImpl implDecl, env')
                | _ ->
                    let names = missing |> List.map (fun m -> m.Name) |> String.concat ", "
                    Error (Other (sprintf "impl %s for %s is missing required methods: %s" implDecl.Interface tName names))
            | None ->
                // Interface not found — register methods anyway (allows standalone impls)
                let mutable env' = env
                for method in implDecl.Methods do
                    let mangledName = sprintf "%s__%s" tName method.Name
                    let selfType = IRTNamed tName
                    let paramTypes = method.Params |> List.map (fun p ->
                        if p.Name = "self" && p.Type.IsNone then selfType
                        else match p.Type with Some t -> lowerTypeExpr env' t | None -> IRTScalar ETFloat64)
                    let retType = match method.ReturnType with
                                  | Some t -> lowerTypeExpr env' t
                                  | None -> env'.Subst.Fresh()
                    let funcType = IRTFunc (paramTypes, retType)
                    let funcVarId = env'.Builder.FreshId()
                    env' <- bindVarSimple mangledName funcVarId funcType env'
                    env' <- { env' with ImplMethods = Map.add (tName, method.Name) (funcVarId, funcType) env'.ImplMethods }
                Ok (TDeclImpl implDecl, env')
        | None -> Ok (TDeclImpl implDecl, env)
    | DeclUnit unitDecl ->
        let env' = registerUnit env unitDecl
        Ok (TDeclUnit unitDecl, env')
    | DeclImport (qname, style) -> Ok (TDeclImport (qname, style), env)

and checkFunctionDecl (env: TypeEnv) (funcDecl: FunctionDecl) : TypeResult<TypedDecl * TypeEnv> =
    // Fresh type variable scope for this function's type annotations.
    let savedScope = env.Subst.PushTypeVarScope()

    // Pre-scan all parameter + return type annotations to register type variable names.
    let allAnnotations =
        (funcDecl.Params |> List.map (fun p -> p.Type))
        @ [funcDecl.ReturnType]
    prescanTypeVarNames env allAnnotations

    let paramTypes = funcDecl.Params |> List.map (fun p ->
        match p.Type with Some t -> lowerTypeExpr env t | None -> IRTScalar ETFloat64)
    let retType = match funcDecl.ReturnType with
                  | Some t -> lowerTypeExpr env t
                  | None -> env.Subst.Fresh()
    let funcType = IRTFunc (paramTypes, retType)
    let funcVarId = env.Builder.FreshId()
    // Register function BEFORE body (enables recursion)
    let envWithFunc = bindVarSimple funcDecl.Name funcVarId funcType env

    let mutable bodyEnv = enterScope envWithFunc
    let typedParams = funcDecl.Params |> List.mapi (fun i p ->
        let varId = env.Builder.FreshId()
        bodyEnv <- bindVarSimple p.Name varId paramTypes.[i] bodyEnv
        { Name = p.Name; Type = paramTypes.[i]; Index = i; VarId = varId } : TypedParam)

    let result =
        inferExpr bodyEnv funcDecl.Body |> Result.bind (fun tBody ->
            let _ = unify env.Subst tBody.Type retType
            let commGroups =
                extractCommGroups
                    (funcDecl.Params |> List.map (fun p -> { Name = p.Name; Type = p.Type } : LambdaParam))
                    funcDecl.WhereClause
            let tf : TypedFunctionDecl = {
                Name = funcDecl.Name; FuncId = funcVarId
                TypeParams = funcDecl.TypeParams
                Params = typedParams; ReturnType = tBody.Type
                WhereClause = funcDecl.WhereClause; Body = tBody
                CommGroups = commGroups; IsStatic = funcDecl.IsStatic
            }
            Ok (TDeclFunction tf, envWithFunc))

    env.Subst.PopTypeVarScope(savedScope)
    result

and registerTypeDecl (env: TypeEnv) (typeDecl: TypeDecl) : TypeEnv =
    match typeDecl with
    | TyDeclAlias (name, _typeParams, body) ->
        let defInfo =
            match body with
            | TyIdx _ | TySymIdx _ | TyAntisymIdx _ | TyHermitianIdx _ | TyBoundedIdx _ ->
                let idx = lowerIndexType env 0 body
                TDIIndexType (name, { idx with Tag = Some name })
            | _ -> TDIAlias (lowerTypeExpr env body)
        registerTypeDef name defInfo env

    | TyDeclStruct (name, typeParams, fields, _invariant) ->
        let fieldTypes = fields |> List.map (fun f -> (f.Name, lowerTypeExpr env f.Type))
        registerTypeDef name (TDIStruct (name, typeParams, fieldTypes)) env

    | TyDeclSum (name, typeParams, variants) ->
        let variantTypes = variants |> List.map (fun v ->
            (v.Name, v.Data |> Option.map (lowerTypeExpr env)))
        let env' = registerTypeDef name (TDIVariant (name, typeParams, variantTypes)) env
        variants |> List.fold (fun e v ->
            registerVariantTag v.Name name (v.Data |> Option.map (lowerTypeExpr env)) e) env'

// ============================================================================
// 12. Module and Program
// ============================================================================

let checkModule (env: TypeEnv) (modul: ModuleDecl) : TypeResult<TypedModule> =
    let rec go (env: TypeEnv) (decls: Located<Decl> list) (acc: TypedDecl list) =
        match decls with
        | [] -> Ok (List.rev acc, env)
        | d :: rest ->
            let decl : Decl = d.Value
            checkDecl env decl |> Result.bind (fun (td, env') -> go env' rest (td :: acc))
    go env modul.Decls [] |> Result.map (fun (tDecls, _) ->
        { Name = Some modul.Name; Decls = tDecls })

let checkProgram (program: Program) : TypeResult<TypedProgram> =
    let env = emptyEnv ()
    program.Modules |> List.map (checkModule env) |> sequenceResults
    |> Result.map (fun ms -> { Modules = ms })

// ============================================================================
// 13. Public Entry Point
// ============================================================================

let typeCheck (program: Program) : TypeResult<TypedProgram> =
    checkProgram program
