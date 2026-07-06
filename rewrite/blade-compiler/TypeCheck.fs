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

/// A compile error with source location and context stack
type CompileError = {
    Error: TypeError
    Span: Span
    Context: string list  // e.g., ["in function 'foo'"; "in let binding 'x'"]
}

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
    /// IDs of type variables that are HM-polymorphic at a function boundary.
    /// Such IDs are preserved by zonking (not defaulted to Float64) so the
    /// IR-phase HM monomorphization pass can substitute them at call sites.
    /// Populated lazily as LookupOrCreateTypeVar produces fresh IDs for
    /// names that prescanTypeVarNames previously registered.
    let mutable polymorphicIds : Set<int> = Set.empty

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
            // If this name was registered by prescanTypeVarNames, the fresh
            // ID is a function-boundary HM type variable. Track it so zonk
            // doesn't default it to Float64 — IR-phase HM monomorphization
            // will substitute it at call sites.
            if Set.contains name knownTypeVarNames then
                match tv with
                | IRTInfer id -> polymorphicIds <- Set.add id polymorphicIds
                | _ -> ()
            tv

    member this.LookupOrCreateTypeVar(name: string) : IRType =
        let key = name + "^0"
        match Map.tryFind key typeVarScope with
        | Some (tv, _) -> tv
        | None ->
            let tv = this.Fresh()
            typeVarScope <- Map.add key (tv, 0) typeVarScope
            if Set.contains name knownTypeVarNames then
                match tv with
                | IRTInfer id -> polymorphicIds <- Set.add id polymorphicIds
                | _ -> ()
            tv

    member _.IsPolymorphicId(id: int) : bool =
        Set.contains id polymorphicIds

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
        | IRTTuple ts -> IRTTuple (ts |> List.map this.Resolve)
        | IRTComputation t -> IRTComputation (this.Resolve t)
        | IRTLoop lt ->
            IRTLoop { lt with
                        ArrayTypes = lt.ArrayTypes |> List.map this.Resolve
                        KernelType = lt.KernelType |> Option.map this.Resolve }
        | IRTPoly (base', var) -> IRTPoly (this.Resolve base', var)
        | IRTUnitAnnotated (inner, units) -> IRTUnitAnnotated (this.Resolve inner, units)
        | IRTIdxTagged (inner, idxRef) -> IRTIdxTagged (this.Resolve inner, idxRef)
        | IRTArrow (slots, result, identity) ->
            let resolveSlot = function
                | SIdx idx -> SIdx idx
                | SIdxVirt idx -> SIdxVirt idx
                | SVal ty -> SVal (this.Resolve ty)
            IRTArrow (slots |> List.map resolveSlot, this.Resolve result, identity)
        | _ -> ty

    member this.ResolveIdx (idx: IRIndexType) = idx  // Index extents are IRExpr, not IRType

/// Occurs check: does inference variable `id` appear in `ty`?
let rec occursIn (id: int) (ty: IRType) : bool =
    match ty with
    | IRTInfer id2 -> id = id2
    | IRTTuple ts -> ts |> List.exists (occursIn id)
    | IRTComputation t -> occursIn id t
    | IRTPoly (base', _) -> occursIn id base'
    | IRTLoop lt ->
        (lt.ArrayTypes |> List.exists (occursIn id)) ||
        (lt.KernelType |> Option.map (occursIn id) |> Option.defaultValue false)
    | IRTUnitAnnotated (inner, _) -> occursIn id inner
    | IRTIdxTagged (inner, _) -> occursIn id inner
    | IRTArrow (slots, ret, _) ->
        let slotHit =
            slots |> List.exists (function
                | SVal ty -> occursIn id ty
                | SIdx _ | SIdxVirt _ -> false)
        slotHit || occursIn id ret
    | _ -> false

/// Walk an inference variable chain to find the leaf variable bound to a
/// concrete scalar (or unbound).  Returns its id so we can rebind it to
/// the promoted type.  Returns None if the type is not an inference chain.
let rec findLeafInferScalar (subst: Subst) (ty: IRType) : int option =
    match ty with
    | IRTInfer id ->
        match subst.TryFind id with
        | Some (IRTInfer _ as next) -> findLeafInferScalar subst next
        | Some (IRTScalar _) -> Some id   // bound to concrete scalar — rebindable
        | None -> Some id                  // unbound — bindable
        | _ -> None                        // bound to non-scalar — leave alone
    | _ -> None

/// Unify two types under the current substitution. Mutates `subst` to
/// bind inference variables as needed.
///
/// Normalization: both sides are passed through `normalize ToNested`
/// after resolve. This makes the §5.3 mixed-slot identity transparent
/// to the recursive cases below — they see the canonical (nested)
/// form regardless of which surface form the caller supplied.
///
/// What integrating normalize buys us:
///   - Concrete equivalent pair (no IRTInfer): structural `=` fires
///     and we short-circuit.
///   - Pair containing IRTInfer where one side is flat-mixed-slot and
///     the other is its split-nested equivalent: the recursive arrow
///     case (`IRTArrow s1 r1 vs IRTArrow s2 r2`) now sees matching
///     slot lengths because both sides have been split at kind
///     boundaries. Inner IRTInfer binds via the structural recursion.
///   - Genuine mismatches (different slot kinds at a position, different
///     index identities, different rank): still rejected — normalize
///     doesn't conflate distinct shapes.
///
/// What it does NOT yet bridge: the §5.2 uniform-kind array identity
/// (flat uniform vs nested uniform). That requires `ToFlat` mode,
/// which is reserved for the B-flat migration.
///
/// Numeric promotion: when two different numeric scalars meet, the
/// inference variable (if any) is rebound to the promoted type so that
/// downstream IR and codegen see the correct wider type.
let rec unify (subst: Subst) (t1: IRType) (t2: IRType) : TypeResult<unit> =
    let orig1 = t1
    let orig2 = t2
    let t1 = subst.Resolve t1 |> normalize ToNested
    let t2 = subst.Resolve t2 |> normalize ToNested
    // §5.3 fast path: post-normalization, structural equality holds
    // iff the two surface forms are equivalent under the mixed-slot
    // identity. Catches concrete-on-both-sides cases without entering
    // the recursive cases at all.
    if t1 = t2 then Ok ()
    else
    match t1, t2 with
    | IRTInfer id1, IRTInfer id2 when id1 = id2 -> Ok ()
    | IRTInfer id, ty | ty, IRTInfer id ->
        if occursIn id ty then Error (Other "Infinite type detected")
        else
            // Check arity invariant: T^k must unify with rank-k array
            match subst.GetArityConstraint(id) with
            | Some k when k > 0 ->
                match ty with
                | ArrayElem arr when arr.IndexTypes.Length = k ->
                    subst.Bind(id, ty); Ok ()
                | IRTInfer _ ->
                    // Binding two inference vars — defer invariant check
                    subst.Bind(id, ty); Ok ()
                | _ ->
                    Error (Other (sprintf "Type variable with arity %d requires a rank-%d array, got %A" k k ty))
            | _ ->
                subst.Bind(id, ty); Ok ()
    | IRTScalar e1, IRTScalar e2 when e1 = e2 -> Ok ()
    // Scalar unification.
    //
    // Phase 2 (post-Gap-2.5): two concrete-and-different primitives are a
    // real type error. Pre-fix, we accepted them via `promoteElemType` and
    // returned Ok, which silently lifted promotion to "type compatibility."
    // That made Probe E (Int64-annotated kernel param against Float64 source)
    // type-check — the C++ then truncated values silently.
    //
    // The promotion rebind path stays alive when at least one side is an
    // inference variable: that's the legitimate use (a fresh literal type
    // gets pinned to the wider type a context requires). When both sides
    // are concrete, promotion would rewrite types without binding anything,
    // which is the silent-acceptance failure mode.
    | IRTScalar e1, IRTScalar e2 ->
        match promoteElemType e1 e2 with
        | Some promoted ->
            let leaf1 = findLeafInferScalar subst orig1
            let leaf2 = findLeafInferScalar subst orig2
            match leaf1, leaf2 with
            | None, None ->
                // Both concrete, neither is an inference variable being pinned.
                // The promoteElemType call was just suggesting a "compatible
                // wider type" — which is a value-promotion fact (used by binop
                // result-type inference), not a type-equality fact. Refuse.
                Error (TypeMismatch (t1, t2))
            | _ ->
                let promotedTy = IRTScalar promoted
                leaf1 |> Option.iter (fun id -> subst.Bind(id, promotedTy))
                leaf2 |> Option.iter (fun id -> subst.Bind(id, promotedTy))
                Ok ()
        | None ->
            Error (TypeMismatch (t1, t2))
    | ArrayElem a1, ArrayElem a2 ->
        // ArrayElem matches IRTArrow with all-SIdx or all-SIdxVirt slots.
        // Rank must match, virtual/stored character must match.
        if a1.IndexTypes.Length <> a2.IndexTypes.Length || a1.IsVirtual <> a2.IsVirtual then
            Error (TypeMismatch (t1, t2))
        else
            // Per-index compatibility: named index types must match by name,
            // symmetry classes must be compatible.
            // NOTE: We intentionally do NOT compare extents here — extents are
            // runtime values in C++, not compile-time template parameters.
            //
            // Synthetic tags (those starting with "__") are internal structural
            // markers like "__raggedidx_inline", "__depidx_inner", "__group_member",
            // "__error_ragged_no_prior". These represent kinds of dimensions, not
            // nominal names, and must not be compared as "named index types are
            // nominative." Only user-named tags get the nominative treatment.
            let isSyntheticTag (t: string) = t.StartsWith("__")
            let indexMismatch =
                List.zip a1.IndexTypes a2.IndexTypes |> List.tryFind (fun (i1, i2) ->
                    // User-named index types are nominative: lat != lon even if both Idx<180>.
                    // Synthetic tags (starting with __) are structural and should not gate
                    // unification — they're set by lowering for internal bookkeeping.
                    match i1.Tag, i2.Tag with
                    | Some t1, Some t2 when t1 <> t2
                                            && not (isSyntheticTag t1)
                                            && not (isSyntheticTag t2) -> true
                    | _ ->
                        // Symmetry class must be compatible
                        i1.Symmetry <> i2.Symmetry && i1.Symmetry <> SymNone && i2.Symmetry <> SymNone)
            match indexMismatch with
            | Some _ -> Error (TypeMismatch (t1, t2))
            | None ->
                // Phase B5: recursive elem-type unification.
                // ElemType is IRType post-B2, so this falls through to the
                // existing unify logic. Inference vars bind; primitives
                // promote where compatible (IRTScalar e1, IRTScalar e2);
                // genuine mismatches error out (catching the silent
                // miscompile that Probe E demonstrated).
                unify subst a1.ElemType a2.ElemType
    | IRTTuple ts1, IRTTuple ts2 when ts1.Length = ts2.Length ->
        List.zip ts1 ts2 |> List.fold (fun acc (a, b) ->
            acc |> Result.bind (fun () -> unify subst a b)) (Ok ())
    | FuncElem (a1, r1), FuncElem (a2, r2) when a1.Length = a2.Length ->
        // FuncElem matches IRTArrow with all-SVal slots (the unified function form).
        // Slot-by-slot arg unification followed by return-type unification.
        List.zip a1 a2 |> List.fold (fun acc (a, b) ->
            acc |> Result.bind (fun () -> unify subst a b)) (Ok ())
        |> Result.bind (fun () -> unify subst r1 r2)
    | IRTUnit, IRTUnit -> Ok ()
    | IRTNamed n1, IRTNamed n2 when n1 = n2 -> Ok ()
    | IRTLoop l1, IRTLoop l2 when l1.Kind = l2.Kind -> Ok ()
    | IRTComputation t1, IRTComputation t2 -> unify subst t1 t2
    | IRTNat _, IRTNat _ -> Ok ()
    // IRTIdxTagged unification (parallel to IRTUnitAnnotated below):
    //   1. Inner types must unify (so an int64 tag won't accidentally
    //      match a float-tagged value, even if both have the same IdxRef).
    //   2. IdxRefs must be structurally equal by identity: named-vs-named
    //      requires name match; anon-vs-anon requires nominalId match
    //      (extent ignored — diagnostic carrier, not identity).
    //   Mixed named-vs-anon: never compatible.
    // The asymmetric arms (tagged-vs-other below) are intentionally
    // omitted: unlike IRTUnitAnnotated which freely strips units in cross
    // unification, IRTIdxTagged is strict — a plain int cannot flow to
    // Nat<I> without explicit casting. This enforces §4.18.3's "untyped
    // literal" rule at the type level.
    | IRTIdxTagged (inner1, r1), IRTIdxTagged (inner2, r2) ->
        let refMatch =
            match r1, r2 with
            | IRefNamed n1, IRefNamed n2 when n1 = n2 -> true
            | IRefAnon (id1, _), IRefAnon (id2, _) when id1 = id2 -> true
            | _ -> false
        if refMatch then unify subst inner1 inner2
        else Error (TypeMismatch (t1, t2))
    // IRTArrow: slot-by-slot unification. Slot kinds (SIdx/SIdxVirt/SVal)
    // must agree at each position; SIdx/SIdxVirt require matching index
    // identity (id and tag); SVal recurses through unify. The result
    // types must also unify. Identity field is ignored — it's metadata.
    | IRTArrow (s1, r1, _), IRTArrow (s2, r2, _) when s1.Length = s2.Length ->
        let unifySlot acc (sa, sb) =
            acc |> Result.bind (fun () ->
                match sa, sb with
                | SVal ta, SVal tb -> unify subst ta tb
                | SIdx ia, SIdx ib | SIdxVirt ia, SIdxVirt ib ->
                    if ia.Id = ib.Id && ia.Tag = ib.Tag then Ok ()
                    else Error (TypeMismatch (t1, t2))
                | _ -> Error (TypeMismatch (t1, t2)))
        List.zip s1 s2 |> List.fold unifySlot (Ok ())
        |> Result.bind (fun () -> unify subst r1 r2)
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


/// Collect free (unresolved) inference variable IDs in a type.
let rec freeInferVars (subst: Subst) (ty: IRType) : Set<int> =
    match subst.Resolve ty with
    | IRTInfer id -> Set.singleton id
    | IRTScalar _ | IRTUnit | IRTNamed _ | IRTNat _ -> Set.empty
    | IRTTuple ts -> ts |> List.map (freeInferVars subst) |> Set.unionMany
    | IRTComputation t -> freeInferVars subst t
    | IRTPoly (t, _) -> freeInferVars subst t
    | IRTLoop lt ->
        Set.union
            (lt.ArrayTypes |> List.map (freeInferVars subst) |> Set.unionMany)
            (lt.KernelType |> Option.map (freeInferVars subst) |> Option.defaultValue Set.empty)
    | IRTUnitAnnotated (inner, _) -> freeInferVars subst inner
    | IRTIdxTagged (inner, _) -> freeInferVars subst inner
    | IRTArrow (slots, ret, _) ->
        let slotVars =
            slots |> List.map (function
                | SVal ty -> freeInferVars subst ty
                | SIdx _ | SIdxVirt _ -> Set.empty)
            |> Set.unionMany
        Set.union slotVars (freeInferVars subst ret)
    | IRTGroupKeys _ -> Set.empty

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
    | TDIEnumIdx of name: string * idx: IRIndexType * values: IR.EnumValue list * body: TypeExpr

/// Exported bindings from a type-checked module, for cross-module imports
type TypeModuleExport = {
    Variables: Map<string, VarInfo>
    TypeDefs: Map<string, TypeDefInfo>
    VariantTags: Map<string, string * IRType option>
    Units: Map<string, IR.UnitSig>
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
    Units: Map<string, IR.UnitSig>
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

/// Wrap a TypeError with span and context into a CompileError
let locateError (span: Span) (env: TypeEnv) (err: TypeError) : CompileError =
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
        | ExprVar name -> IRParam (name, 0, IRTNat None)
        | ExprLit (LitInt n) -> IRLit (IRLitInt n)
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
    match expr with
    | ExprVar n when n = paramName -> subst
    | _ ->
        match evalConstExpr env expr with
        | Some k -> IRLit (IRLitInt k)
        | None ->
            match expr with
            | ExprVar name -> IRParam (name, 0, IRTNat None)
            | ExprLit (LitInt n) -> IRLit (IRLitInt n)
            | ExprBinOp (_mode, op, l, r) ->
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
            | ExprUnaryOp (OpNeg, e) ->
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
        | "Nat" -> IRTNat None
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
            | Some (TDIStruct (n, _, _)) -> IRTNamed n
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
                let underlying = IR.EnumValue.underlyingElemType values
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
                Tag = Some "__error_ragged_no_prior"
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
                          Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] })
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
                    Symmetry = SymSymmetric; Tag = None; Kind = SDimension; Dependencies = [] }
        mkArrayArrow [idx] (IRTScalar ETFloat64) None

    | TyAntisymIdx (rank, extent) ->
        let ext = lowerExtentExpr env extent
        let idx = { Id = env.Builder.FreshId(); Rank = rank; Extent = ext
                    Symmetry = SymAntisymmetric; Tag = None; Kind = SDimension; Dependencies = [] }
        mkArrayArrow [idx] (IRTScalar ETFloat64) None

    | TyHermitianIdx extent ->
        let ext = lowerExtentExpr env extent
        let idx = { Id = env.Builder.FreshId(); Rank = 2; Extent = ext
                    Symmetry = SymHermitian; Tag = None; Kind = SDimension; Dependencies = [] }
        mkArrayArrow [idx] (IRTScalar ETFloat64) None

    | TyBoundedIdx _ -> IRTScalar ETInt64

    | TyCompoundIdx _mask ->
        let idx = { Id = env.Builder.FreshId(); Rank = 1; Extent = IRParam ("compound", 0, IRTNat None)
                    Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] }
        mkArrayArrow [idx] (IRTScalar ETFloat64) None

    | TyEnumIdx valuesExpr ->
        // Determine underlying element type from values. All-string lowers to
        // ETString; otherwise (all-int, empty, or mixed-but-recoverable) ETInt64.
        // Mixed lists won't reach here in practice — the same extraction is
        // run by registerTypeDecl and surfaces a clean error when aliased; an
        // unaliased mixed-list slips through silently with ETInt64.
        let isAllString =
            match valuesExpr with
            | ExprArrayLit elems when not elems.IsEmpty ->
                elems |> List.forall (function ExprLit (LitString _) -> true | _ -> false)
            | _ -> false
        if isAllString then IRTScalar ETString else IRTScalar ETInt64

    | TyDepIdx _ | TyRaggedIdx _ | TyRaggedIdxOpaque ->
        // DepIdx/RaggedIdx in non-index position. Defensive fallback matching
        // the shape used for TyCompoundIdx, TyEquivIdx, etc. — wrap in a
        // single-index Array so the IR shape is consistent. Real iteration
        // happens via lowerIndexType, which produces an arity-2 IRIndexType
        // with the right Dependencies and Tag.
        let idx = lowerIndexType env 0 ty
        mkArrayArrow [idx] (IRTScalar ETFloat64) None

    | TyPoly inner ->
        // Fresh arity-variable name per Poly occurrence. See the TyNamed "Poly"
        // case above for rationale: packs are independent, so each Poly param
        // gets its own identifier in the type rep.
        let arityName = sprintf "r%d" (env.Builder.FreshId())
        IRTPoly (lowerTypeExpr env inner, arityName)
    | TyConstrained (inner, _) -> lowerTypeExpr env inner
    | TyEquivIdx (_dim, _group, _rep) ->
        let idx = { Id = env.Builder.FreshId(); Rank = 1; Extent = IRParam ("equiv", 0, IRTNat None)
                    Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] }
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
            let underlying = IR.EnumValue.underlyingElemType values
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
            match valuesExpr with
            | ExprArrayLit elems when not elems.IsEmpty ->
                elems |> List.forall (function ExprLit (LitString _) -> true | _ -> false)
            | _ -> false
        if isAllString then IRTScalar ETString else IRTScalar ETInt64
    | _ ->
        lowerTypeExpr env ty

and lowerIndexType env (_position: int) (ty: TypeExpr) : IRIndexType =
    let id = env.Builder.FreshId()
    match ty with
    | TyIdx extent ->
        { Id = id; Rank = 1; Extent = lowerExtentExpr env extent
          Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] }
    | TySymIdx (rank, extent) ->
        { Id = id; Rank = rank; Extent = lowerExtentExpr env extent
          Symmetry = SymSymmetric; Tag = None; Kind = SDimension; Dependencies = [] }
    | TyAntisymIdx (rank, extent) ->
        { Id = id; Rank = rank; Extent = lowerExtentExpr env extent
          Symmetry = SymAntisymmetric; Tag = None; Kind = SDimension; Dependencies = [] }
    | TyHermitianIdx extent ->
        { Id = id; Rank = 2; Extent = lowerExtentExpr env extent
          Symmetry = SymHermitian; Tag = None; Kind = SDimension; Dependencies = [] }
    | TyEnumIdx valuesExpr ->
        let nValues =
            match valuesExpr with
            | ExprArrayLit elems -> int64 elems.Length
            | _ -> 0L
        { Id = id; Rank = 1; Extent = IRLit (IRLitInt nValues); Symmetry = SymNone
          Tag = None; Kind = SDimension; Dependencies = [] }
    | TyDepIdx (outerTy, _paramName, _bodyTy) ->
        // Single-slot context (e.g., type alias, range): return a placeholder
        // inner-only record. Two-record expansion happens at the array-index-
        // list construction site (lowerIndexTypeList). Single-slot use of
        // DepIdx is suspect — code paths that need the full DepIdx structure
        // (iteration, etc.) should route through lowerIndexTypeList instead.
        let outerIdx = lowerIndexType env _position outerTy
        { Id = id; Rank = 2; Extent = IRParam ("__depidx_inner", 0, IRTNat None)
          Symmetry = SymNone; Tag = Some "__depidx"
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
          Symmetry = SymNone; Tag = Some "__raggedidx"
          Kind = SDimension; Dependencies = [] }
    | TyRaggedIdxOpaque ->
        // Opaque-extent variant: rank-1, no lengths array, no outer position.
        // Used in kernel-parameter types where the extent is supplied by the
        // peel context. The Extent is a sentinel (IROpaqueExtent) rather than
        // a placeholder IRParam, so codegen can distinguish "extent unknown
        // because we haven't computed it yet" (IRParam) from "extent supplied
        // by surrounding loop binding" (IROpaqueExtent).
        { Id = id; Rank = 1; Extent = IROpaqueExtent
          Symmetry = SymNone; Tag = Some "__raggedidx_opaque"
          Kind = SDimension; Dependencies = [] }
    | TyNamed (name, _) ->
        match lookupTypeDef name env with
        | Some (TDIIndexType (_, idx, _)) -> { idx with Id = id }
        | Some (TDIEnumIdx (_, idx, _, _)) -> { idx with Id = id }
        | _ ->
            { Id = id; Rank = 1; Extent = IRParam (name, 0, IRTNat None); Symmetry = SymNone
              Tag = Some name; Kind = SDimension; Dependencies = [] }
    | TyCompoundIdx maskExpr ->
        // CompoundIdx<mask> -- masked product space (formalism 4.5). Rank = the
        // RANK of the mask array (its number of dimensions). The mask is a runtime
        // array value carried in IRCompoundMask for codegen; cardinality (popcount)
        // is computed at runtime by the emitted compound_index_t. Canonical surface
        // form is a named mask whose declared type yields the rank; other forms
        // fall back to a rank-1 degraded placeholder for now (no producer relies on
        // them yet). Nested matches are parenthesized to avoid outer-arm absorption.
        let maskIR, rank =
            match maskExpr with
            | ExprVar name ->
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
          Symmetry = SymNone; Tag = Some "__compoundidx"
          Kind = SDimension; Dependencies = [] }
    | _ ->
        { Id = id; Rank = 1; Extent = IRParam ("?", 0, IRTNat None); Symmetry = SymNone
          Tag = None; Kind = SDimension; Dependencies = [] }

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
        let outerWithTag = { outerIdx with Tag = Some "__depidx_outer" }
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
                        match funcDecl.Body with
                        | ExprBlock ([], Some e) -> e
                        | other -> other
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
            Tag = Some "__depidx_inner"
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
            Tag = Some "__raggedidx"
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
            Tag = Some "__raggedidx_opaque"
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
    | Some name when not (name.StartsWith("__")) ->
        IRTIdxTagged (IRTScalar ETInt64, IRefNamed name)
    | _ ->
        IRTScalar ETInt64

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
            | StmtForIn (varName, rangeExpr, bodyStmts) ->
                free <- Set.union free (collectFreeVars b rangeExpr)
                let innerBound = Set.add varName b
                for bodyStmt in bodyStmts do
                    match bodyStmt with
                    | StmtLet binding ->
                        free <- Set.union free (collectFreeVars innerBound binding.Value)
                    | StmtAssign (lhs, _, rhs) ->
                        free <- Set.union free (Set.union (collectFreeVars innerBound lhs) (collectFreeVars innerBound rhs))
                    | StmtExpr e ->
                        free <- Set.union free (collectFreeVars innerBound e)
                    | StmtForIn _ -> ()  // Nested not yet supported
        match finalExpr with
        | Some e -> Set.union free (collectFreeVars b e)
        | None -> free
    | ExprMethodFor arrays -> arrays |> List.map (collectFreeVars bound) |> Set.unionMany
    | ExprObjectFor kernel -> collectFreeVars bound kernel
    | ExprPure e | ExprCompute e | ExprRead e | ExprRank e -> collectFreeVars bound e
    | ExprGuard (c, b) -> Set.union (collectFreeVars bound c) (collectFreeVars bound b)
    | ExprMask (a, p) -> Set.union (collectFreeVars bound a) (collectFreeVars bound p)
    | ExprCompound (d, m) -> Set.union (collectFreeVars bound d) (collectFreeVars bound m)
    | ExprIntersect (a, b) -> Set.union (collectFreeVars bound a) (collectFreeVars bound b)
    | ExprUnion (a, b) -> Set.union (collectFreeVars bound a) (collectFreeVars bound b)
    | ExprUnique a -> collectFreeVars bound a
    | ExprContains (a, v) -> Set.union (collectFreeVars bound a) (collectFreeVars bound v)
    | ExprGroupBy (v, k) -> Set.union (collectFreeVars bound v) (collectFreeVars bound k)
    | ExprGroupKeys ks -> ks |> List.map (collectFreeVars bound) |> Set.unionMany
    | ExprSort (a, k) -> Set.union (collectFreeVars bound a) (collectFreeVars bound k)
    | ExprReduce (a, k) -> Set.union (collectFreeVars bound a) (collectFreeVars bound k)
    | ExprExtents a -> collectFreeVars bound a
    | ExprReynolds (k, _) -> collectFreeVars bound k
    | ExprField (e, _) -> collectFreeVars bound e
    | ExprTupleIndex (t, i) -> Set.union (collectFreeVars bound t) (collectFreeVars bound i)
    | ExprStruct (_, fields) -> fields |> List.map (snd >> collectFreeVars bound) |> Set.unionMany
    | ExprReplicate (n, b) -> Set.union (collectFreeVars bound n) (collectFreeVars bound b)
    | ExprDotDot (lo, hi) -> Set.union (collectFreeVars bound lo) (collectFreeVars bound hi)
    | ExprTyped (e, _) -> collectFreeVars bound e
    | ExprAssign (l, r) -> Set.union (collectFreeVars bound l) (collectFreeVars bound r)
    | ExprFor (src, _, kernelOpt) ->
        let srcFree = match src with
                      | ForArrays (arrs, inOpt) -> 
                          let arrFree = arrs |> List.map (collectFreeVars bound) |> Set.unionMany
                          let inFree = inOpt |> Option.map (collectFreeVars bound) |> Option.defaultValue Set.empty
                          Set.union arrFree inFree
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

    if isRaggedAtSecondLevel then
        // Build a RaggedIdx-typed array. Outer index has extent = number of
        // entries (rectangular at outer level). Inner index is RaggedIdx with
        // an IRRaggedLookup whose lengths reference is synthesized from the
        // literal's actual sub-array lengths (computed at codegen time).
        let n = List.length exprs
        let outerIdx = {
            Id = builder.FreshId(); Rank = 1; Extent = IRLit (IRLitInt (int64 n))
            Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = []
        }
        // The lengths reference is a synthetic IRParam — codegen recognizes
        // this and emits the lengths array inline from the literal structure.
        // The "__inline_lens" name is a sentinel that the codegen detects.
        let innerIdx = {
            Id = builder.FreshId(); Rank = 1
            Extent = IRRaggedLookup (IRParam ("__inline_lens", 0, IRTNat None))
            Symmetry = SymNone; Tag = Some "__raggedidx_inline"
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
              Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] })
        { ElemType = elemType; IndexTypes = indexTypes; IsVirtual = false; Identity = None }

let getArrayType (env: TypeEnv) (expr: Expr) : IRArrayType =
    match expr with
    | ExprVar name ->
        match lookupVar name env with
        | Some info ->
            match info.Type with
            | ArrayElem arrTy -> arrTy
            | _ ->
                { ElemType = IRTScalar ETFloat64
                  IndexTypes = [{ Id = env.Builder.FreshId(); Rank = 1
                                  Extent = IRParam(name + "_n", 0, IRTNat None)
                                  Symmetry = SymNone; Tag = None; Kind = SDimension
                                  Dependencies = [] }]
                  IsVirtual = false; Identity = Some (AIDVariable name) }
        | None ->
            { ElemType = IRTScalar ETFloat64
              IndexTypes = [{ Id = env.Builder.FreshId(); Rank = 1
                              Extent = IRParam(name + "_n", 0, IRTNat None)
                              Symmetry = SymNone; Tag = None; Kind = SDimension
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
    match pat with
    | PatWildcard ->
        Ok { Kind = TPatWild; Type = expected; Bindings = [] }

    | PatVar name ->
        // Check if this name is a registered variant tag (enum constructor without data).
        // The parser can't distinguish `| North -> ...` (variant match) from `| x -> ...` (variable binding)
        // because it doesn't have type information. We resolve the ambiguity here.
        match Map.tryFind name env.VariantTags with
        | Some (parentName, None) ->
            // This is a no-payload variant constructor — treat as PatVariant
            checkPattern env expected (PatVariant (name, None))
        | Some (parentName, Some _) ->
            // Variant with payload but used without — treat as variable (may shadow)
            let varId = env.Builder.FreshId()
            Ok { Kind = TPatVar (name, varId); Type = expected
                 Bindings = [(name, varId, expected)] }
        | None ->
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
            Tag = None
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
            | _ -> Error (Other (sprintf "%s(): failed to bind array type after unification" opName)))
    | _ ->
        Error (Other (sprintf "%s() requires an array as argument" opName))

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
                    Some (Other (sprintf
                        "Array index tag mismatch: slot expects '%s' but argument has type '%s'."
                        tagName argName))
                | IRTIdxTagged (_, IRefAnon _) ->
                    Some (Other (sprintf
                        "Array index tag mismatch: slot expects named tag '%s' but argument is an anonymous index value."
                        tagName))
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
    | headSlot :: _ when headSlot.Tag = Some "__compoundidx" ->
        let k = headSlot.Rank
        match tArgs with
        | [] -> Ok ()  // no args consumed here (e.g. bare array value); nothing to check
        | firstArg :: _ ->
            // Wildcard compound indexing: the tuple is full-arity (k elements)
            // with some `_` marking FREE axes (B((a, _, c))). Recognized as a
            // distinct form; the position-aware residual type and codegen are a
            // later step, so gate it here with a clear message rather than let a
            // hole-typed wildcard flow on as if it were a pinned coordinate.
            let wildcardPositions =
                match firstArg.Kind with
                | TExprTuple elems ->
                    elems |> List.mapi (fun i e -> (i, e))
                          |> List.choose (fun (i, e) -> match e.Kind with TExprWildcard -> Some i | _ -> None)
                | _ -> []
            match wildcardPositions with
            | _ :: _ ->
                Error (Other (sprintf
                    "Wildcard compound indexing (free axis at position(s) %A on a rank-%d compound) is recognized but not yet fully implemented: the position-aware residual type and codegen are pending. Full indexing B((c0, ..., c%d)) is supported."
                    wildcardPositions k (k - 1)))
            | [] ->
              (match env.Subst.Resolve firstArg.Type with
               | IRTTuple tys when tys.Length >= 1 && tys.Length <= k -> Ok ()
                // 1 <= j <= k: full (j = k) or partial (j < k) index. The
                // residual type is computed by compoundResidualType; partial
                // (j < k) types correctly but its reconstitution codegen is
                // gated at lowering (phase 2).
               | IRTTuple tys ->
                   Error (Other (sprintf
                       "Compound index over-supplied: this array's compound axis has rank %d (mask is %d-dimensional), so it takes at most a %d-tuple like B((c0, ..., c%d)); got a %d-tuple."
                       k k k (k - 1) tys.Length))
               | _ when k = 1 -> Ok ()  // rank-1 compound: bare scalar index is fine
               | _ ->
                   // k >= 2 but the first arg is not a tuple. This is the flat
                   // currying form B(c0, c1, ...) or a single scalar -- reject and
                   // point at the canonical tuple form.
                   Error (Other (sprintf
                       "Compound index must be a single tuple: write B((c0, ..., cj)) with inner parentheses, not the flat form B(c0, ..., cj). A CompoundIdx<mask> axis of rank %d is indexed as one joint tuple, full or partial (formalism 4.5 / poly-indexing 5.4)."
                       k)))
    | _ -> Ok ()

/// The residual index-type fragment (formalism 4.5 currying table) that
/// REPLACES a rank-k compound slot after pinning its first j coordinates
/// (1 <= j <= k). Returns the list of index types the compound slot becomes:
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
            Symmetry = SymNone; Tag = None
            Kind = SDimension; Dependencies = [] } ]
    else
        // Residual CompoundIdx over the remaining k-j axes at the pinned prefix.
        // Tagged __compoundidx so it is treated uniformly with a top-level
        // compound (further indexable, further partial-indexable).
        [ { Id = fresh (); Rank = residualRank
            Extent = IRCompoundProject (parentIR, j)
            Symmetry = SymNone; Tag = Some "__compoundidx"
            Kind = SDimension; Dependencies = [] } ]

let private dispatchAppOrIndex (env: TypeEnv) (tFunc: TypedExpr) (tArgs: TypedExpr list) : TypeResult<TypedExpr> =
    match tFunc.Type with
    | ArrayElem arrTy when tArgs.Length <= arrTy.IndexTypes.Length ->
        validateCompoundIndex env arrTy tArgs
        |> Result.bind (fun () ->
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
                | h :: _ -> h.Tag = Some "__compoundidx"
                | [] -> false
            if headIsCompound then
                let headSlot = List.head arrTy.IndexTypes
                let trailingSlots = List.tail arrTy.IndexTypes
                let k = headSlot.Rank
                let firstArg = List.head tArgs
                // j = pinned coordinate count: tuple arity, or 1 for a scalar
                // (rank-1 compound). validateCompoundIndex already rejected the
                // malformed shapes (including the not-yet-implemented wildcard
                // form), so this is well-formed here.
                let j =
                    match env.Subst.Resolve firstArg.Type with
                    | IRTTuple tys -> tys.Length
                    | _ -> 1
                // Parent IR reference for the residual extent carrier. Available
                // when the array is a plain variable; other producers (field
                // access, chained residuals) are deferred to phase 2 along with
                // the reconstitution codegen, so a non-var parent still types via
                // the fragment but carries an IRLitUnit placeholder parent (the
                // phase-2 codegen gate catches the partial case regardless).
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
                if List.isEmpty finalSlots then
                    Ok (mkTyped (TExprIndex (tFunc, tArgs, identity)) arrTy.ElemType)
                else
                    Ok (mkTyped (TExprIndex (tFunc, tArgs, identity))
                                (mkArrayLike { arrTy with IndexTypes = finalSlots }))
            elif tArgs.Length = arrTy.IndexTypes.Length then
                Ok (mkTyped (TExprIndex (tFunc, tArgs, identity)) arrTy.ElemType)
            else
                let remaining = arrTy.IndexTypes |> List.skip tArgs.Length
                Ok (mkTyped (TExprIndex (tFunc, tArgs, identity))
                            (mkArrayLike { arrTy with IndexTypes = remaining }))))
    | FuncElem (_paramTys, retTy) ->
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
/// The walk is structural: visit every sub-expression, perform the tag
/// check at TExprIndex nodes, and short-circuit on the first error.
let rec private revalidateBodyTagChecks (env: TypeEnv) (expr: TypedExpr) : TypeResult<unit> =
    let children : TypedExpr list =
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
        | TExprFunctorMap (f, c) -> [f; c]
        | TExprCompose (_, l, r) -> [l; r]
        | TExprDotDot (lo, hi) -> [lo; hi]
        | TExprBlocked (_, bs) -> [bs]
        | TExprPure e | TExprCompute e | TExprRead e | TExprFillRandom e | TExprRank e
        | TExprExtents e | TExprReynolds (e, _) -> [e]
        | TExprGuard (c, b) -> [c; b]
        | TExprMask (a, p) | TExprIntersect (a, p) | TExprUnion (a, p)
        | TExprGroupBy (a, p) | TExprSort (a, p) | TExprReduce (a, p)
        | TExprCompound (a, p) -> [a; p]
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
            let stmtExprs =
                stmts |> List.collect (fun s ->
                    match s with
                    | TStmtLet b -> [b.Value]
                    | TStmtAssign (l, r) -> [l; r]
                    | TStmtExpr e -> [e]
                    | TStmtForIn (_, _, lo, hi, body) ->
                        let bodyExprs =
                            body |> List.collect (fun bs ->
                                match bs with
                                | TStmtLet b -> [b.Value]
                                | TStmtAssign (l, r) -> [l; r]
                                | TStmtExpr e -> [e]
                                | TStmtForIn _ -> [])  // nested for-in: skip (rare)
                        lo :: hi :: bodyExprs)
            stmtExprs @ Option.toList final
        | TExprAssign (l, r) -> [l; r]
        | TExprReplicate (c, b) -> [c; b]
        | TExprAlign (es, _) -> es
        | TExprPartialApp (_, a, _) -> [a]
    // Recurse into children first, short-circuiting on error.
    let childRes =
        children
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

let rec inferExpr (env: TypeEnv) (expr: Expr) : TypeResult<TypedExpr> =
    match expr with
    // ---- Literals ----
    | ExprLit lit -> Ok (mkTyped (TExprLit lit) (inferLiteralType lit))

    // ---- Wildcard hole ----
    // `_` in expression position. Not a value; carries a fresh hole type so it
    // passes through tuple inference (e.g. a free axis in a compound index
    // B((a, _, c))). The compound-index dispatch reads the wildcard positions;
    // any other context that reaches lowering with a TExprWildcard is an error.
    | ExprWildcard -> Ok (mkTyped TExprWildcard (env.Subst.Fresh()))

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
                            (mkFuncArrow [payloadTy] (IRTNamed parentName)))
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
    | ExprApp (ExprField (ExprVar alias, "load_compound"), [varE; maskE])
        when (match lookupVar alias env with
              | Some vi ->
                  (match env.Subst.Resolve(vi.Type) with
                   | IRTNamed pn -> pn.StartsWith("Providers.")
                   | _ -> false)
              | None -> false) ->
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

    | ExprApp (ExprField (ExprVar n, field), args)
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
                    let retTy = match funcType with FuncElem (_, ret) -> ret | _ -> env.Subst.Fresh()
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
    | ExprApp (func, args) ->
        inferExpr env func |> Result.bind (fun tFunc ->
        args |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tArgs ->
            dispatchAppOrIndex env tFunc tArgs))

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
    | ExprTupleIndex (tuple, index) ->
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
                                Some (Other (sprintf
                                    "Array index tag mismatch: slot expects '%s' but argument has type '%s'."
                                    tagName argName))
                            | IRTIdxTagged (_, IRefAnon _) ->
                                Some (Other (sprintf
                                    "Array index tag mismatch: slot expects named tag '%s' but argument is an anonymous index value."
                                    tagName))
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
    | ExprField (ExprVar n, field)
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
            Ok (mkTyped (TExprArrayLit (tElems, arrTy)) (mkArrayLike arrTy)))

    // ---- Block ----
    | ExprBlock (stmts, finalExpr) -> inferBlock env stmts finalExpr

    // ---- Loop constructs ----
    | ExprMethodFor arrays -> inferMethodFor env arrays
    | ExprObjectFor kernel -> inferObjectFor env kernel

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
    | ExprRange idxTys ->
        let idxs = idxTys |> List.map (lowerIndexType env 0)
        // Each listed index type becomes one virtual slot; downstream the slots
        // uncurry into nested loop levels. The element type is taken from the
        // innermost (last) index -- the value yielded at the deepest level --
        // which preserves single-index behavior (one slot -> that slot's tagged
        // element type).
        let elemType =
            match List.tryLast idxs with
            | Some i -> elemTypeForIterationIndex i
            | None -> IRTScalar ETInt64
        Ok (mkTyped (TExprRange idxs) (mkVirtualArrayArrow idxs elemType))
    | ExprDotDot (lo, hi) ->
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
                Tag = Some "__anon"
                Kind = SDimension
                Dependencies = []
            }
            // ExprDotDot has no index-type annotation, so element type
            // stays bare int64 (no name to tag with).
            Ok (mkTyped (TExprDotDot (tLo, tHi)) (mkVirtualArrayArrow [idx] (IRTScalar ETInt64)))))
    | ExprReverse idxTy ->
        let idx = lowerIndexType env 0 idxTy
        let elemType = elemTypeForIterationIndex idx
        Ok (mkTyped (TExprReverse idx) (mkVirtualArrayArrow [idx] elemType))
    | ExprBlocked (idxTy, blockSize) ->
        let idx = lowerIndexType env 0 idxTy
        inferExpr env blockSize |> Result.bind (fun tBS ->
            let elemType = elemTypeForIterationIndex idx
            Ok (mkTyped (TExprBlocked (idx, tBS)) (mkVirtualArrayArrow [idx] elemType)))

    // ---- Zip / Stack ----
    | ExprZip exprs ->
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
    | ExprRead e ->
        // |> read forces a deferred provider read; the result is the operand's
        // (possibly view-modified) array, so the type passes through unchanged.
        inferExpr env e |> Result.bind (fun tE ->
            Ok (mkTyped (TExprRead tE) tE.Type))
    | ExprGuard (cond, body) ->
        inferExpr env cond |> Result.bind (fun tC ->
        inferExpr env body |> Result.bind (fun tB ->
            Ok (mkTyped (TExprGuard (tC, tB)) tB.Type)))
    
    // mask(array, pred) — filter array by predicate, producing compacted array
    | ExprMask (array, pred) ->
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
                    // Result: 1D array with same element type, runtime-opaque extent
                    let resultIdx = {
                        Id = env.Builder.FreshId(); Rank = 1
                        Extent = IRParam ("__masked", 0, IRTNat None)
                        Symmetry = SymNone; Tag = None
                        Kind = SDimension; Dependencies = []
                    }
                    let resultType = mkArrayArrow [resultIdx] arrTy.ElemType None
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
    | ExprCompound (dense, mask) ->
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
    | ExprIntersect (a, b) | ExprUnion (a, b) ->
        let isIntersect = match expr with ExprIntersect _ -> true | _ -> false
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
                        Symmetry = SymNone; Tag = None
                        Kind = SDimension; Dependencies = []
                    }
                    let resultType = mkArrayArrow [resultIdx] arrTy.ElemType None
                    let texpr = if isIntersect then TExprIntersect (tA, tB) else TExprUnion (tA, tB)
                    Ok (mkTyped texpr resultType)))))

    // unique(A) — deduplicate, preserving first-occurrence order. Same
    // element type as input, dynamic extent (≤ input extent).
    | ExprUnique a ->
        inferExpr env a |> Result.bind (fun tA ->
            requireArrayArg env tA "unique" |> Result.bind (fun arrTy ->
                let resultIdx = {
                    Id = env.Builder.FreshId(); Rank = 1
                    Extent = IRParam ("__unique", 0, IRTNat None)
                    Symmetry = SymNone; Tag = None
                    Kind = SDimension; Dependencies = []
                }
                let resultType = mkArrayArrow [resultIdx] arrTy.ElemType None
                Ok (mkTyped (TExprUnique tA) resultType)))

    // contains(A, x) — membership test. Returns Bool. The value's type
    // must unify with the array's element type; mismatch (e.g., looking
    // for a Float64 in an Int64 array) is a hard error.
    | ExprContains (a, value) ->
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
    | ExprGroupKeys keys ->
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
                            Symmetry = SymNone; Tag = None
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
                                ({ idx with Id = env.Builder.FreshId(); Tag = Some name }, None)
                            | Some (TDIEnumIdx (_, idx, values, _)) ->
                                ({ idx with Id = env.Builder.FreshId(); Tag = Some name }, Some values)
                            | _ ->
                                ({ Id = env.Builder.FreshId(); Rank = 1
                                   Extent = IRParam ("__ngroups", 0, IRTNat None)
                                   Symmetry = SymNone; Tag = None
                                   Kind = SDimension; Dependencies = [] }, None)
                        | None ->
                            ({ Id = env.Builder.FreshId(); Rank = 1
                               Extent = IRParam ("__ngroups", 0, IRTNat None)
                               Symmetry = SymNone; Tag = None
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
                    Error (Other (
                        "group_keys: all key arrays must be rank-1 and share the same outer index (same length). " +
                        "Compound grouping requires each i-th element of every key array to refer to the same record."))
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
                        Tag = Some "__compoundidx_dynamic"
                        Kind = SDimension; Dependencies = []
                    }
                    let gkType = IRTGroupKeys (outerIdx, firstSource, None)
                    let tKeys = pairs |> List.map fst
                    Ok (mkTyped (TExprGroupKeys tKeys) gkType))

    // group_by(values, grouping) — apply GroupKeys to a values array
    // Result: rank-2 array (groups × members), with GroupIdx
    // Tags ("__group_outer", "__group_member") signal to codegen to use ragged peel.
    | ExprGroupBy (values, grouping) ->
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
                            Symmetry = SymNone; Tag = Some "__group_member"
                            Kind = SDimension; Dependencies = []
                        }
                        ({ outer with Id = env.Builder.FreshId(); Tag = Some "__group_outer" }, member_)
                    | _ ->
                        // Fallback: treat second arg as raw key array (backward compat)
                        let outer = {
                            Id = env.Builder.FreshId(); Rank = 1
                            Extent = IRParam ("__ngroups", 0, IRTNat None)
                            Symmetry = SymNone; Tag = Some "__group_outer"
                            Kind = SDimension; Dependencies = []
                        }
                        let member_ = {
                            Id = env.Builder.FreshId(); Rank = 1
                            Extent = IRParam ("__groupsz", 0, IRTNat None)
                            Symmetry = SymNone; Tag = Some "__group_member"
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
    | ExprSort (array, key) ->
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
                        Symmetry = SymNone; Tag = None
                        Kind = SDimension; Dependencies = []
                    }
                    let resultType = mkArrayArrow [resultIdx] arrTy.ElemType None
                    Ok (mkTyped (TExprSort (tArr, tKey)) resultType))))

    | ExprTranspose (array, d1, d2) ->
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
                        Error (Other (sprintf "transpose: axis %d is out of range for a rank-%d array (valid axes 0..%d)" d totalDims (totalDims - 1)))
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
                    Error (Other (sprintf "transpose: the two axes must differ (got [%d, %d]); swapping an axis with itself is the identity" d1 d2))
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
                                Error (Other (sprintf "transpose: swapping two dimensions within a single rectangular index group (rank %d) is not yet supported." ar1))
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

    | ExprGram (leftE, rightE) ->
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
                    Error (Other (sprintf "gram(A, B): both operands must be rank-2 (matrix) arrays; got rank-%d and rank-%d. gram contracts the trailing axis: A (m x n), B (p x n) -> m x p." lDims rDims))
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
                              Symmetry = sym; Tag = None; Kind = SDimension; Dependencies = [] }
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

    | ExprDecompact (array, d) ->
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
                        Error (Other (sprintf "decompact: dimension %d is out of range for a rank-%d array (valid dims 0..%d)" dd totalDims (totalDims - 1)))
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
                        Error (Other (sprintf "decompact: dimension %d is a plain (rank-1, non-symmetric) axis; there is nothing to decompact. decompact pulls a component out of a compact group (SymIdx/AntisymIdx/HermitianIdx)." d))
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
                        Error (Other (sprintf "decompact: only a compact group in the LAST index slot, optionally preceded by plain free Idx dimensions, is supported by codegen (the chained to-the-right peel shape). The array here has %d index slots with the compact group at slot %d." arrTy.IndexTypes.Length slot))
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
    | ExprReduce (array, kernel) ->
        inferExpr env array |> Result.bind (fun tArr ->
        inferExpr env kernel |> Result.bind (fun tKernel ->
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
                    Tag = None
                    Kind = SDimension
                    Dependencies = []
                }
                let freshArr = mkArrayArrow [freshIdx] elemType None
                unify env.Subst tArr.Type freshArr |> ignore
             | _ -> ())
            match env.Subst.Resolve(tArr.Type) with
            | ArrayElem arrTy when arrTy.IndexTypes.Length = 1 ->
                // Static guarantee: reject if we can prove the extent is 0.
                match tryEvalIntIR arrTy.IndexTypes.[0].Extent with
                | Some n when n <= 0L ->
                    Error (Other (sprintf "reduce() rejects statically empty arrays (extent = %d). Empty input has no defined reduction without an identity; a 3-arg form `reduce(arr, op, init)` is the planned solution." n))
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
                    kernelUnify |> Result.bind (fun () ->
                        // arrTy.ElemType is IRType post-B2; return directly.
                        let resultType = arrTy.ElemType
                        Ok (mkTyped (TExprReduce (tArr, tKernel)) resultType))
            | ArrayElem _ ->
                Error (Other "reduce() currently supports only rank-1 arrays (multi-rank reduction over the innermost dimension is deferred)")
            | _ ->
                Error (Other "reduce() requires an array as first argument")))

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
    | ExprExtents array ->
        inferExpr env array |> Result.bind (fun tArr ->
            requireArrayArg env tArr "extents" |> Result.bind (fun arrTy ->
                if arrTy.IndexTypes.Length = 1 then
                    Ok (mkTyped (TExprExtents tArr) (IRTScalar ETInt64))
                else
                    // Multi-rank: tuple of Int64s, one per dimension
                    let n = arrTy.IndexTypes.Length
                    let tupleTy = IRTTuple (List.replicate n (IRTScalar ETInt64))
                    Ok (mkTyped (TExprExtents tArr) tupleTy)))

    | ExprSequence exprs ->
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
                        Tag = Some "__seq"
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
    | ExprReplicate (count, body) ->
        inferExpr env count |> Result.bind (fun tC ->
        inferExpr env body |> Result.bind (fun tB ->
            // Extract count as literal integer
            let n =
                match tC.Kind with
                | TExprLit (LitInt v) -> Some (int v)
                | _ -> None
            match n with
            | None ->
                Error (Other "replicate count must be an integer literal")
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
                    Tag = Some "__seq"
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
                Ok (mkTyped (TExprReplicate (tC, tB)) resultType)
            | _ ->
                Error (Other (sprintf "replicate count must be >= 1, got %A" n))))

    // ---- Reynolds ----
    | ExprReynolds (kernel, isAntisym) ->
        inferExpr env kernel |> Result.bind (fun tK ->
            Ok (mkTyped (TExprReynolds (tK, isAntisym)) tK.Type))

    // ---- Type annotation ----
    | ExprTyped (e, tyAnno) ->
        // Route through bidirectional checkExpr so the annotation pushes
        // down into literal/constructor positions. The motivating case
        // is `(re, im) : Complex128`: a 2-tuple checked against Complex
        // produces a TExprComplexLit (preserving scalar nature) rather
        // than synthesizing as a tuple-of-floats and failing to unify.
        // For non-special-cased shapes, checkExpr falls through to
        // inferExpr + unify, preserving the prior plain-cast behavior.
        let annoTy = lowerTypeExpr env tyAnno
        checkExpr env annoTy e |> Result.map (fun tE ->
            { tE with Type = annoTy })

    // ---- Arity special forms ----
    | ExprArity paramName -> Ok (mkTyped (TExprArity paramName) (IRTScalar ETInt64))
    | ExprNth -> Ok (mkTyped (TExprLit (LitInt 0L)) (IRTScalar ETInt64))
    | ExprZero ->
        // zero gets a fresh type variable — unifies with int, float, bool context
        let ty = env.Subst.Fresh()
        Ok (mkTyped TExprZero ty)
    | ExprRank e ->
        inferExpr env e |> Result.bind (fun tE ->
            Ok (mkTyped (TExprRank tE) (IRTScalar ETInt64)))

    // ---- Struct construction ----
    | ExprStruct (name, fields) -> inferStructConstruction env name fields

    // ---- Sectioned operators ----
    | ExprSection op ->
        let paramTy = env.Subst.Fresh()
        Ok (mkTyped (TExprSection op) (mkFuncArrow [paramTy; paramTy] paramTy))
    | ExprPartialApp (op, arg, isLeft) ->
        inferExpr env arg |> Result.bind (fun tArg ->
            Ok (mkTyped (TExprPartialApp (op, tArg, isLeft))
                        (mkFuncArrow [tArg.Type] tArg.Type)))

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
                Ok (mkTyped (TExprAssign (tL, tR)) IRTUnit)))

    // ---- For expression ----
    | ExprFor (source, _constraints, kernelOpt) ->
        inferForExpr env source kernelOpt

    // ---- Align ----
    | ExprAlign (exprs, spec) ->
        exprs |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tExprs ->
            let ty = if tExprs.IsEmpty then IRTUnit else tExprs.[0].Type
            Ok (mkTyped (TExprAlign (tExprs, spec)) ty))

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
                    SharedIndexType = None
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

    | OpChoice | OpFallback ->
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            let _ = unify env.Subst tL.Type tR.Type
            Ok (mkTyped (TExprChoice (tL, tR)) tL.Type)))

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
            inferArithType mode op tL.Type tR.Type |> Result.map (fun resTy ->
                mkTyped (TExprBinOp (mode, op, tL, tR)) resTy)))

and inferArithType mode op leftTy rightTy : TypeResult<IRType> =
    match op with
    | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe ->
        // Comparisons require compatible units
        let lUnits = IR.getUnits leftTy
        let rUnits = IR.getUnits rightTy
        match lUnits, rUnits with
        | Some lu, Some ru when not (IR.unitCompatible lu ru) ->
            Error (Other (sprintf "Unit mismatch in comparison: %s vs %s" (IR.ppUnitSig lu) (IR.ppUnitSig ru)))
        | _ -> Ok (IRTScalar ETBool)
    | OpAnd | OpOr -> Ok (IRTScalar ETBool)
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
                Some (Other (sprintf "Cross-nominal index-type arithmetic: cannot combine values of distinct index domains '%s' and '%s'." ln rn))
            | IRTIdxTagged (_, IRefNamed ln), IRTIdxTagged (_, IRefNamed rn) when ln <> rn ->
                Some (Other (sprintf "Cross-nominal index-type arithmetic: cannot combine values of distinct index domains '%s' and '%s'." ln rn))
            | IRTIdxTagged (_, IRefAnon (lid, _)), IRTIdxTagged (_, IRefAnon (rid, _)) when lid <> rid ->
                Some (Other (sprintf "Cross-nominal index-type arithmetic: cannot combine values of distinct anonymous index domains (#%d vs #%d)." lid rid))
            | l, r when isIndexType l || isIndexType r ->
                let n = if isIndexType l then indexTypeName l else indexTypeName r
                Some (Other (sprintf "Arithmetic on index type '%s' is not permitted. Index types are nominal labels — for value-level arithmetic on positions, use virtual array iteration (which produces plain ints); for new index types derived from arithmetic, type-level construction is a separate workstream not yet implemented." n))
            | _ -> None
        match indexArithErr with
        | Some err -> Error err
        | None ->
        let bareResult =
            match mode with
            | Outer ->
                match lBare, rBare with
                | ArrayElem arrL, ArrayElem arrR ->
                    mkArrayLike { arrL with IndexTypes = arrL.IndexTypes @ arrR.IndexTypes }
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
                if IR.unitCompatible lu ru then Ok (IRTUnitAnnotated (bareResult, lu))
                else
                    Error (Other (sprintf "Unit mismatch in %s: %s vs %s"
                        (if op = OpAdd then "addition" else "subtraction")
                        (IR.ppUnitSig lu) (IR.ppUnitSig ru)))
            | Some u, None | None, Some u -> Ok (IRTUnitAnnotated (bareResult, u))
            | None, None -> Ok bareResult
        | OpMul ->
            match lUnits, rUnits with
            | Some lu, Some ru -> Ok (IRTUnitAnnotated (bareResult, IR.unitMul lu ru))
            | Some u, None | None, Some u -> Ok (IRTUnitAnnotated (bareResult, u))
            | None, None -> Ok bareResult
        | OpDiv | OpMod ->
            match lUnits, rUnits with
            | Some lu, Some ru -> Ok (IRTUnitAnnotated (bareResult, IR.unitDiv lu ru))
            | Some u, None -> Ok (IRTUnitAnnotated (bareResult, u))
            | None, Some u -> Ok (IRTUnitAnnotated (bareResult, IR.unitDiv IR.unitDimensionless u))
            | None, None -> Ok bareResult
        | _ -> Ok bareResult

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
        buildApplyInfo env mfInfo.Arrays mfInfo.Identities mfInfo.ArrayTypes mfInfo.SDimsPerArray mfInfo.SharedIndexType lambdaInfo tLeft tRight false false

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
        buildApplyInfo env mfInfo.Arrays mfInfo.Identities mfInfo.ArrayTypes mfInfo.SDimsPerArray mfInfo.SharedIndexType lambdaInfo tLeft tRight false false

    | TExprMethodFor mfInfo, TExprReynolds (innerKernel, isReynoldsAntisym) ->
        let resolvedInner = resolveTypedExpr env innerKernel
        match resolvedInner.Kind with
        | TExprLambda li -> buildApplyInfo env mfInfo.Arrays mfInfo.Identities mfInfo.ArrayTypes mfInfo.SDimsPerArray mfInfo.SharedIndexType li tLeft tRight true isReynoldsAntisym
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
        buildApplyInfo env mfInfo.Arrays mfInfo.Identities mfInfo.ArrayTypes mfInfo.SDimsPerArray mfInfo.SharedIndexType lambdaInfo tLeft tZeroKernel false false

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
            | OpChoice | OpFallback ->
                // object_for(<|>) <@> (c1, c2, ...) → left-fold producing TExprChoice
                let folder (acc: TypedExpr) (elem: TypedExpr) : TypedExpr =
                    mkTyped (TExprChoice (acc, elem)) acc.Type
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

        let (flatArrays, sharedIdx) =
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
                let idx =
                    if allCoIter then
                        let minRank = arrTypes |> List.map (fun at -> at.IndexTypes.Length) |> List.min
                        if minRank > 0 then Some arrTypes.[0].IndexTypes.[0] else None
                    else None
                (arrays, idx)
            else (rawExprs, None)

        let identities = flatArrays |> List.map (fun arr ->
            match arr.Kind with
            | TExprVar (name, _, _) -> AIDVariable name
            | _ -> AIDLiteral (env.Builder.FreshId()))
        let arrayTypes = flatArrays |> List.map (fun arr ->
            match arr.Type with
            | ArrayElem at -> at
            | _ -> { ElemType = IRTScalar ETFloat64; IndexTypes = []; IsVirtual = false; Identity = None })
        let sDimsPerArray =
            if sharedIdx.IsSome then arrayTypes |> List.map (fun _ -> 1)
            else computeSDimsPerArray arrayTypes

        // Resolve kernel and build ApplyInfo with object_for as provenance
        let resolvedKernel = resolveTypedExpr env objInfo.Kernel
        match resolvedKernel.Kind with
        | TExprLambda lambdaInfo ->
            buildApplyInfo env flatArrays identities arrayTypes sDimsPerArray sharedIdx lambdaInfo tLeft objInfo.Kernel false false
        | TExprReynolds (innerK, isReynoldsAntisym) ->
            let resolvedInnerK = resolveTypedExpr env innerK
            match resolvedInnerK.Kind with
            | TExprLambda li ->
                buildApplyInfo env flatArrays identities arrayTypes sDimsPerArray sharedIdx li tLeft objInfo.Kernel true isReynoldsAntisym
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
            buildApplyInfo env flatArrays identities arrayTypes sDimsPerArray sharedIdx lambdaInfo tLeft (mkTyped (TExprLambda lambdaInfo) (IRTScalar elemType)) false false
        | _ -> Error (Other (sprintf "object_for kernel must be a lambda, reynolds, or zero, but got %A" (resolvedKernel.Kind.GetType().Name)))

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
            SharedIndexType = None
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
        let leftDesc = 
            match rL.Kind with
            | TExprVar (name, _, _) -> sprintf "variable '%s'" name
            | _ -> sprintf "%A" (rL.Kind.GetType().Name)
        Error (Other (sprintf "<@> requires method_for or object_for on the left side, but got %s" leftDesc))

and buildApplyInfo (env: TypeEnv)
    (arrays: TypedExpr list) (identities: ArrayIdentity list)
    (arrayTypes: IRArrayType list) (sDimsPerArray: int list)
    (sharedIndexType: IRIndexType option)
    (lambdaInfo: TypedLambdaInfo)
    (tLoop: TypedExpr) (tKernel: TypedExpr)
    (isReynolds: bool) (isReynoldsAntisym: bool)
    : TypeResult<TypedExpr> =

    let commGroups = lambdaInfo.CommGroups

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
    let isRaggedInnerTag (tag: string option) : bool =
        match tag with
        | Some t ->
            t.StartsWith "__raggedidx"
            || t.StartsWith "__depidx_inner"
            || t.StartsWith "__group_member"
            || t = "__error_ragged_no_prior"
        | None -> false
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
                | TExprReduce (arr, _) when isParamVar arr -> true
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
            | TExprReduce (a, k) -> walk a || walk k
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
                    |> List.filter (fun idx -> isRaggedInnerTag idx.Tag)
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
        // After param-type unification, inference variables that flowed into
        // the body's TExprIndex sites may now resolve to nominally-tagged
        // types (e.g., `r` in `lambda(r) -> by_country(r)` is unified with
        // the iterated array's elem type `Nat<RegionIdx>`). Re-run the tag
        // check across the body so cross-tag indexing through kernel
        // parameters surfaces as a real type error rather than silently
        // typechecking. See revalidateBodyTagChecks for rationale.
        revalidateBodyTagChecks env lambdaInfo.Body
        |> Result.bind (fun () ->
        // Infer output element type from kernel return type, falling back to input arrays.
        // Returns IRType (Phase B2). Primitives are wrapped IRTScalar.
        let outputElemType =
            let resolved = env.Subst.Resolve(lambdaInfo.ReturnType)
            match resolved with
            | IRTScalar _ as t -> t                                // pass through
            | ArrayElem arr -> arr.ElemType                         // already IRType
            | IRTUnitAnnotated (IRTScalar _, _) as t -> t          // preserve unit annotation
            | _ ->
                // S3 tag: relic. Fall back to common element type of input arrays.
                // The input arrays' elem types are already IRType post-B2.
                arrayTypes |> List.tryPick (fun at -> Some at.ElemType)
                |> Option.defaultValue (IRTScalar ETFloat64)
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
                ReturnType = env.Subst.Resolve lambdaInfo.ReturnType }

        // Store the kernel with resolved types in the typed AST. Lowering
        // walks this typed lambda and emits a lifted IRCallable referenced
        // via IRVar(callable.Id) at the kernel slot.
        let resolvedKernel =
            let lambdaExpr = mkTyped (TExprLambda resolvedLambdaInfo) tKernel.Type
            if isReynolds then mkTyped (TExprReynolds (lambdaExpr, isReynoldsAntisym)) tKernel.Type
            else lambdaExpr

        let isCoIter = sharedIndexType.IsSome
        // For co-iteration, output type is array with shared index (not outer product)
        let outputType =
            if isCoIter then
                match sharedIndexType with
                | Some sharedIdx ->
                    mkArrayArrow [sharedIdx] outputElemType None
                | None -> outputType
            else outputType
        let info : TypedApplyInfo = {
            Loop = tLoop; Kernel = resolvedKernel
            Arrays = arrays; Identities = identities
            ArrayTypes = gridArrayTypes; SharedIndexType = sharedIndexType
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
            IsCoIteration = isCoIter
            IsComposeApply = false
        }
        Ok (mkTyped (TExprApply info) outputType))

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
    let resolved = env.Subst.Resolve expected
    match expr, resolved with

    // Numeric/scalar literals retype to the expected scalar (numbers stay numbers,
    // bools stay bools — no implicit 0<->false). The literal carries its source-
    // level value but its IRType matches the annotation.
    | ExprLit (LitInt _ as lit), IRTScalar et ->
        match et with
        | ETInt32 | ETInt64 ->
            Ok (mkTyped (TExprLit lit) (IRTScalar et))
        | _ ->
            Error (TypeMismatch (resolved, IRTScalar ETInt64))
    | ExprLit (LitInt _ as lit), IRTIdxTagged (IRTScalar (ETInt32 | ETInt64), _) ->
        // §4.18.3: untyped int literal acquires the index tag from annotation
        // context. `let i: Idx<3> = 0` works; the 0 becomes Nat<Idx<3>>.
        // Strict in the OTHER direction: a bare `Nat` value cannot flow to
        // Nat<I> position without explicit cast — but a LITERAL has no
        // pre-committed type, so context-driven typing applies here.
        Ok (mkTyped (TExprLit lit) resolved)
    | ExprLit (LitString _ as lit), IRTScalar et ->
        match et with
        | ETString ->
            Ok (mkTyped (TExprLit lit) (IRTScalar et))
        | _ ->
            Error (TypeMismatch (resolved, IRTScalar ETString))
    | ExprLit (LitString _ as lit), IRTIdxTagged (IRTScalar ETString, _) ->
        // §4.18.3 parallel for string-valued index tags (EnumIdx with
        // string values). Same context-driven coercion as the int case.
        Ok (mkTyped (TExprLit lit) resolved)
    | ExprLit (LitFloat _ as lit), IRTScalar et ->
        match et with
        | ETFloat32 | ETFloat64 -> Ok (mkTyped (TExprLit lit) (IRTScalar et))
        | _ -> Error (TypeMismatch (resolved, IRTScalar ETFloat64))
    | ExprLit (LitBool _ as lit), IRTScalar ETBool ->
        Ok (mkTyped (TExprLit lit) (IRTScalar ETBool))

    // Array literal: extract per-rank shape from the annotation and recurse.
    // Outer index supplies the literal's length; inner index types form the
    // element annotation. Elements are checked individually against this.
    | ExprArrayLit elems, ArrayElem arrTy when not arrTy.IndexTypes.IsEmpty ->
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
            Error (Other (sprintf "Array literal has %d elements, but annotation expects %d" elems.Length expectedN))
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
    | ExprApp (ExprVar "fill_random", [modE]), ArrayElem _ ->
        checkExpr env (IRTScalar ETInt64) modE |> Result.map (fun tMod ->
            mkTyped (TExprFillRandom tMod) resolved)

    // Tuple literal: zip components against expected component types.
    | ExprTuple exprs, IRTTuple ts when exprs.Length = ts.Length ->
        List.zip exprs ts |> List.map (fun (e, t) -> checkExpr env t e) |> sequenceResults
        |> Result.map (fun tExprs ->
            mkTyped (TExprTuple tExprs) (IRTTuple (tExprs |> List.map (fun e -> e.Type))))

    // Complex literal construction. A 2-tuple checked against Complex64
    // or Complex128 — typically `(re, im) : Complex128` — produces a
    // TExprComplexLit (NOT TExprTuple) typed as the scalar Complex type.
    // The TExprTuple form would lower to IRTuple and lose the scalar
    // nature, leading downstream code to potentially flatten the "tuple"
    // into the surrounding array's shape (an N-element Complex array
    // becoming an N x 2 doubles array). TExprComplexLit lowers to
    // IRLitComplex, a scalar IR node that codegen renders as
    // std::complex<double>(re, im). Per the design rule, both components
    // must be float-typed (no implicit int → float promotion at literal
    // construction time) — this is enforced by checkExpr recursing into
    // each component with the corresponding float type.
    | ExprTuple [reExpr; imExpr], IRTScalar (ETComplex64 | ETComplex128 as cet) ->
        let componentTy =
            match cet with
            | ETComplex64 -> IRTScalar ETFloat32
            | _ -> IRTScalar ETFloat64
        checkExpr env componentTy reExpr |> Result.bind (fun tRe ->
        checkExpr env componentTy imExpr |> Result.map (fun tIm ->
            mkTyped (TExprComplexLit (tRe, tIm)) (IRTScalar cet)))

    // Default: synthesize, then unify. This is the path that handles variables,
    // function calls, complex expressions, and any case the special-cases miss.
    | _ ->
        inferExpr env expr |> Result.bind (fun tE ->
            match unify env.Subst tE.Type expected with
            | Ok () -> Ok tE
            | Error _ -> Error (TypeMismatch (expected, tE.Type)))

// ---- Shared helpers for both let paths (let-as-expression and top-level DeclLet) ----
//
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
    match binding.Type with
    | Some annot ->
        let annotTy = lowerTypeExpr env annot
        checkExpr env annotTy binding.Value |> Result.map (fun tv ->
            // Prefer the annotation as the canonical type — it can be more
            // specific than what the value synthesized to.
            { tv with Type = annotTy })
    | None -> inferExpr env binding.Value

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
        match binding.Pattern with
        | PatVar name ->
            let (varId, env') = bindLetPatVar env name (Some (AIDVariable name)) assign tValue
            inferExpr env' body |> Result.map (fun tBody ->
                mkTyped (TExprLet (name, varId, tValue, tBody)) tBody.Type)

        | PatTuple pats ->
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
                match p with
                | PatVar n ->
                    let vid = env.Builder.FreshId()
                    let eTy =
                        if i < typeList.Length then env.Subst.Resolve(typeList.[i])
                        else env.Subst.Fresh()
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
    let mutable typedStmts : TypedStmt list = []

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
                    let tb : TypedBinding = {
                        Name = name; VarId = varId; Type = tValue.Type
                        Identity = identity; IsMutable = (assign <> ReadOnly); Value = tValue
                        SubBindings = []
                    }
                    typedStmts <- typedStmts @ [TStmtLet tb]
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
                            typedStmts <- typedStmts @ [TStmtAssign (tL, tR)]
                    | _ ->
                        let _ = unify curEnv.Subst tL.Type tR.Type
                        typedStmts <- typedStmts @ [TStmtAssign (tL, tR)]
                | Error e, _ | _, Error e -> err <- Some e
            | StmtExpr e ->
                match inferExpr curEnv e with
                | Ok tE -> typedStmts <- typedStmts @ [TStmtExpr tE]
                | Error e -> err <- Some e
            | StmtForIn (varName, rangeExpr, bodyStmts) ->
                // Extract lo/hi from ExprDotDot range expression
                let loHiResult =
                    match rangeExpr with
                    | ExprDotDot (lo, hi) ->
                        match inferExpr curEnv lo, inferExpr curEnv hi with
                        | Ok tL, Ok tH -> Ok (tL, tH)
                        | Error e, _ | _, Error e -> Error e
                    | _ ->
                        Error (Other "for-in range must use a..b syntax")
                match loHiResult with
                | Ok (tLo, tHi) ->
                    // Bind loop variable as Int64
                    let varId = curEnv.Builder.FreshId()
                    let loopEnv = bindVarSimple varName varId (IRTScalar ETInt64) curEnv
                    // Infer body statements
                    let mutable bodyEnv = loopEnv
                    let mutable bodyErr = None
                    let mutable typedBodyStmts : TypedStmt list = []
                    for bodyStmt in bodyStmts do
                        if bodyErr.IsNone then
                            match bodyStmt with
                            | StmtLet binding ->
                                match inferExpr bodyEnv binding.Value with
                                | Ok tValue ->
                                    let bName = match binding.Pattern with PatVar n -> n | _ -> "_"
                                    let bId = bodyEnv.Builder.FreshId()
                                    let assign = assignOfBindingMut binding.Mutability
                                    bodyEnv <- bindVarFull bName bId tValue.Type None assign (Some tValue) bodyEnv
                                    let tb : TypedBinding = {
                                        Name = bName; VarId = bId; Type = tValue.Type
                                        Identity = None; IsMutable = (assign <> ReadOnly); Value = tValue
                                        SubBindings = []
                                    }
                                    typedBodyStmts <- typedBodyStmts @ [TStmtLet tb]
                                | Error e -> bodyErr <- Some e
                            | StmtAssign (lhs, _, rhs) ->
                                match inferExpr bodyEnv lhs, inferExpr bodyEnv rhs with
                                | Ok tL, Ok tR ->
                                    let _ = unify bodyEnv.Subst tL.Type tR.Type
                                    typedBodyStmts <- typedBodyStmts @ [TStmtAssign (tL, tR)]
                                | Error e, _ | _, Error e -> bodyErr <- Some e
                            | StmtExpr e ->
                                match inferExpr bodyEnv e with
                                | Ok tE -> typedBodyStmts <- typedBodyStmts @ [TStmtExpr tE]
                                | Error e -> bodyErr <- Some e
                            | StmtForIn _ ->
                                bodyErr <- Some (Other "Nested for-in loops not yet supported")
                    match bodyErr with
                    | Some e -> err <- Some e
                    | None ->
                        typedStmts <- typedStmts @ [TStmtForIn (varName, varId, tLo, tHi, typedBodyStmts)]
                | Error e -> err <- Some e

    match err with
    | Some e -> Error e
    | None ->
        match finalExpr with
        | Some e -> inferExpr curEnv e |> Result.map (fun tF ->
            mkTyped (TExprBlock (typedStmts, Some tF)) tF.Type)
        | None -> Ok (mkTyped (TExprBlock (typedStmts, None)) IRTUnit)

and inferMethodFor env arrays : TypeResult<TypedExpr> =
    // Detect method_for(zip(A, B, ...)) — expand zip into co-iteration
    match arrays with
    | [ExprZip zipExprs] ->
        zipExprs |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tZipArrays ->
            let identities = zipExprs |> List.map (fun arr ->
                match arr with ExprVar name -> AIDVariable name | _ -> AIDLiteral (env.Builder.FreshId()))
            let arrayTypes = tZipArrays |> List.mapi (fun i ta ->
                match ta.Type with
                | ArrayElem at -> at
                | _ -> getArrayType env zipExprs.[i])
            // Shared index type: intersection of prefix indices (use first array's indices,
            // with extent = min of all arrays at that position)
            let minRank = arrayTypes |> List.map (fun at -> at.IndexTypes.Length) |> List.min
            let sharedIdx =
                if minRank > 0 then Some arrayTypes.[0].IndexTypes.[0]
                else None
            let sDimsPerArray = arrayTypes |> List.map (fun _ -> 1)  // Each contributes 1 s-dim (shared)
            let totalSDims = List.sum sDimsPerArray

            let info : TypedMethodForInfo = {
                Arrays = tZipArrays; Identities = identities; ArrayTypes = arrayTypes
                SDimsPerArray = sDimsPerArray; TotalSDims = totalSDims
                SharedIndexType = sharedIdx
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
                let minRank = arrayTypes |> List.map (fun at -> at.IndexTypes.Length) |> List.min
                let sharedIdx =
                    if minRank > 0 then Some arrayTypes.[0].IndexTypes.[0]
                    else None
                let sDimsPerArray = arrayTypes |> List.map (fun _ -> 1)
                let totalSDims = List.sum sDimsPerArray
                let info : TypedMethodForInfo = {
                    Arrays = zipExprs; Identities = identities; ArrayTypes = arrayTypes
                    SDimsPerArray = sDimsPerArray; TotalSDims = totalSDims
                    SharedIndexType = sharedIdx
                }
                let loopTy = IRTLoop {
                    Kind = LKMethod; Arity = Some zipExprs.Length
                    ArrayTypes = arrayTypes |> List.map mkArrayLike; KernelType = None
                }
                Ok (mkTyped (TExprMethodFor info) loopTy)
            | _ -> failwith "unreachable"
        | _ ->
        let identities = arrays |> List.map (fun arr ->
            match arr with ExprVar name -> AIDVariable name | _ -> AIDLiteral (env.Builder.FreshId()))
        let arrayTypes = tArrays |> List.mapi (fun i ta ->
            match ta.Type with
            | ArrayElem at -> at
            | _ -> getArrayType env arrays.[i])
        let sDimsPerArray = computeSDimsPerArray arrayTypes
        let totalSDims = List.sum sDimsPerArray

        let info : TypedMethodForInfo = {
            Arrays = tArrays; Identities = identities; ArrayTypes = arrayTypes
            SDimsPerArray = sDimsPerArray; TotalSDims = totalSDims
            SharedIndexType = None
        }
        let loopTy = IRTLoop {
            Kind = LKMethod; Arity = Some arrays.Length
            ArrayTypes = arrayTypes |> List.map mkArrayLike; KernelType = None
        }
        Ok (mkTyped (TExprMethodFor info) loopTy))

and inferObjectFor env kernel : TypeResult<TypedExpr> =
    inferExpr env kernel |> Result.bind (fun tKernel ->
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
    | ForArrays (arrays, Some inClause) ->
        // Co-iteration: for (A, B) in range<Idx<N>> <@> lambda(a, b) -> ...
        // All arrays share the iteration space from the in-clause
        arrays |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tArrays ->
        inferExpr env inClause |> Result.bind (fun tVirtual ->
            // Extract the shared index type from the virtual array
            let sharedIdx =
                match tVirtual.Type with
                | ArrayElem at when at.IndexTypes.Length > 0 -> at.IndexTypes.[0]
                | _ -> { Id = env.Builder.FreshId(); Rank = 1
                         Extent = IRLit (IRLitInt 1L); Symmetry = SymNone
                         Tag = None; Kind = SDimension; Dependencies = [] }
            
            let identities = arrays |> List.map (fun arr ->
                match arr with ExprVar name -> AIDVariable name | _ -> AIDLiteral (env.Builder.FreshId()))
            // For co-iteration, all arrays use the shared index type
            let arrayTypes = tArrays |> List.mapi (fun i ta ->
                match ta.Type with
                | ArrayElem at -> at
                | _ -> getArrayType env arrays.[i])
            let sDimsPerArray = arrayTypes |> List.map (fun _ -> 1)  // Each contributes 1 s-dim (shared)
            let totalSDims = sDimsPerArray |> List.sum

            let mfInfo : TypedMethodForInfo = {
                Arrays = tArrays; Identities = identities; ArrayTypes = arrayTypes
                SDimsPerArray = sDimsPerArray; TotalSDims = totalSDims
                SharedIndexType = Some sharedIdx
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
                        let outputIndexTypes = [sharedIdx] @ (kernelTDims |> List.map (fun idx -> { idx with Id = env.Builder.FreshId() }))
                        let outputType = mkArrayArrow outputIndexTypes elemType None
                        // Note: SymcomStates/TriangularLevels/SpeedupFactor are unused
                        // by the co-iteration codegen path — it derives loop structure
                        // directly from SharedIndexType
                        let info : TypedApplyInfo = {
                            Loop = mkTyped (TExprMethodFor mfInfo) loopTy
                            Kernel = resolvedKernel
                            Arrays = tArrays; Identities = identities
                            ArrayTypes = arrayTypes; SharedIndexType = Some sharedIdx
                            SymcomStates = List.replicate totalSDims SCNeither
                            TriangularLevels = List.replicate totalSDims false
                            SDimsPerArray = sDimsPerArray
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
            | None -> Ok tLoop))
    
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
    match decl with
    | DeclLet binding ->
        // Top-level let: shares value-resolution and primary-name binding with
        // inferLetBinding (the let-as-expression form). Diverges from there in
        // that we surface destructured sub-vars to Lowering and wrap in a
        // TypedBinding rather than recursing into a body expression.
        inferLetBindingValue env binding |> Result.bind (fun tValue ->
            let name = match binding.Pattern with PatVar n -> n | _ -> "_"
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
                match binding.Value with
                | ExprApp (ExprField (ExprVar alias, "load"), [ExprLit (LitString path)]) ->
                    let isProvider =
                        match lookupVar alias env with
                        | Some vi ->
                            (match env.Subst.Resolve(vi.Type) with
                             | IRTNamed pn -> pn.StartsWith("Providers.")
                             | _ -> false)
                        | None -> false
                    if not isProvider then (env, tValue)
                    else
                        try
                            // Read the file metadata at compile time (the same read
                            // Lowering.tryInvokeProvider performs) and register the
                            // resulting struct types so `sample.vars.<v>` resolves.
                            let pm = Blade.NetcdfProvider.loadAsModule env.Builder name path
                            let (envM, moduleTy) = registerProviderModule env name pm
                            (envM, { tValue with Type = moduleTy })
                        with _ -> (env, tValue)
                | _ -> (env, tValue)
            let identity = match binding.Pattern with PatVar n -> Some (AIDVariable n) | _ -> None
            let assign = assignOfBindingMut binding.Mutability
            let (varId, env') = bindLetPatVar env name identity assign tValue

            // Handle destructuring at top level — collect sub-bindings for Lowering
            let mutable subBindings : (string * IRId * IRType) list = []
            let env' =
                match binding.Pattern with
                | PatTuple pats ->
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
                        match p with
                        | PatVar n ->
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
                | PatCons (h, t) ->
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
                | PatStruct (structName, fieldPats) ->
                    // Look up struct field types from the type definition
                    let structFields =
                        match env.Subst.Resolve(tValue.Type) with
                        | IRTNamed sName ->
                            match Map.tryFind sName env.TypeDefs with
                            | Some (TDIStruct (_, _, fields)) -> fields
                            | _ -> []
                        | _ -> []
                    let fieldTypeMap = Map.ofList structFields
                    fieldPats |> List.fold (fun e (fieldName, pat) ->
                        match pat with
                        | PatVar n ->
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

            let tb : TypedBinding = {
                Name = name; VarId = varId; Type = env.Subst.Resolve(tValue.Type)
                Identity = identity; IsMutable = (assign <> ReadOnly); Value = tValue
                SubBindings = subBindings |> List.map (fun (n, id, ty) -> (n, id, env.Subst.Resolve ty))
            }
            Ok (TDeclLet tb, env'))

    | DeclStatic binding ->
        // Use the shared annotation handler so `let static` bindings enforce
        // their type annotations the same way regular lets do. Without this,
        // an annotation like `let static x: Float<meters> = 100.0` was
        // silently dropped — the binding would carry the synthesized type
        // instead.
        inferLetBindingValue env binding |> Result.bind (fun tValue ->
            let name = match binding.Pattern with PatVar n -> n | _ -> "_"
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
                match binding.Pattern with
                | PatTuple pats ->
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
                        match p with
                        | PatVar n ->
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
            }
            Ok (TDeclStatic tb, env''))

    | DeclFunction funcDecl -> checkFunctionDecl env funcDecl

    | DeclType typeDecl ->
        registerTypeDecl env typeDecl |> Result.bind (fun env' ->
            let ttd =
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
                        TTDIndexType (name, idx)
                    | Some (TDIEnumIdx (_, idx, values, _)) ->
                        TTDEnumIdx (name, idx, values)
                    | _ ->
                        TTDAlias (name, typeParams, lowerTypeExpr env' body)
                | TyDeclStruct (name, typeParams, fields, invariant) ->
                    let resolvedFields = fields |> List.map (fun f -> (f.Name, lowerTypeExpr env' f.Type))
                    let tInvariant =
                        invariant |> Option.bind (fun expr ->
                            // Bind field names for invariant checking
                            let mutable invEnv = env'
                            for f in fields do
                                let fId = invEnv.Builder.FreshId()
                                invEnv <- bindVarSimple f.Name fId (lowerTypeExpr env' f.Type) invEnv
                            match inferExpr invEnv expr with
                            | Ok tE -> Some tE
                            | Error _ -> None)
                    TTDStruct (name, typeParams, resolvedFields, tInvariant)
                | TyDeclSum (name, typeParams, variants) ->
                    let resolvedVariants = variants |> List.map (fun v ->
                        (v.Name, v.Data |> Option.map (lowerTypeExpr env')))
                    TTDVariant (name, typeParams, resolvedVariants)
            Ok (TDeclType ttd, env'))

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
                    Error (Other (sprintf "impl %s for %s is missing required methods: %s" implDecl.Interface tName names))
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
    let funcType = mkFuncArrow paramTypes retType
    // Reuse pre-pass varId if this function was already pre-registered (static functions)
    // This ensures other functions' bodies reference the same varId
    let funcVarId =
        match lookupVar funcDecl.Name env with
        | Some existing -> existing.VarId
        | None -> env.Builder.FreshId()
    // Register function BEFORE body (enables recursion)
    let envWithFunc = bindVarSimple funcDecl.Name funcVarId funcType env

    let mutable bodyEnv = enterScope envWithFunc
    let typedParams = funcDecl.Params |> List.mapi (fun i p ->
        let varId = env.Builder.FreshId()
        bodyEnv <- bindVarSimple p.Name varId paramTypes.[i] bodyEnv
        { Name = p.Name; Type = paramTypes.[i]; Index = i; VarId = varId } : TypedParam)

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
            Ok (TDeclFunction tf, envWithFunc)))

    env.Subst.PopTypeVarScope(savedScope)
    result

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
                Ok (TDIIndexType (name, { idx with Tag = Some name }, chasedBody))
            | TyDepIdx _ | TyRaggedIdx _ | TyRaggedIdxOpaque ->
                let idx = lowerIndexType env 0 chasedBody
                Ok (TDIIndexType (name, idx, chasedBody))
            | TyEnumIdx valuesExpr ->
                // Static-evaluate the array literal to extract values. Each
                // element must be either an int literal (with optional negation)
                // or a string literal. Mixed kinds are a type error — the two
                // backings (int64_t vs std::string) cannot share one EnumIdx.
                let raw =
                    match valuesExpr with
                    | ExprArrayLit elems ->
                        elems |> List.choose (fun e ->
                            match e with
                            | ExprLit (LitInt n) -> Some (IR.EVInt n)
                            | ExprUnaryOp (OpNeg, ExprLit (LitInt n)) -> Some (IR.EVInt (-n))
                            | ExprLit (LitString s) -> Some (IR.EVString s)
                            | _ -> None)
                    | _ -> []
                let hasInt = raw |> List.exists (function IR.EVInt _ -> true | _ -> false)
                let hasString = raw |> List.exists (function IR.EVString _ -> true | _ -> false)
                if hasInt && hasString then
                    Error (Other (sprintf "EnumIdx '%s' has mixed value kinds: integer and string literals in the same EnumIdx<[...]> aren't allowed. The runtime backing must be one or the other (int64_t or std::string)." name))
                else
                    let extent = int64 raw.Length
                    let idx = {
                        Id = env.Builder.FreshId(); Rank = 1
                        Extent = IRLit (IRLitInt extent)
                        Symmetry = SymNone; Tag = Some name
                        Kind = SDimension; Dependencies = []
                    }
                    Ok (TDIEnumIdx (name, idx, raw, chasedBody))
            | _ -> Ok (TDIAlias (lowerTypeExpr env body))
        defInfoResult |> Result.map (fun defInfo -> registerTypeDef name defInfo env)

    | TyDeclStruct (name, typeParams, fields, _invariant) ->
        let fieldTypes = fields |> List.map (fun f -> (f.Name, lowerTypeExpr env f.Type))
        Ok (registerTypeDef name (TDIStruct (name, typeParams, fieldTypes)) env)

    | TyDeclSum (name, typeParams, variants) ->
        let variantTypes = variants |> List.map (fun v ->
            (v.Name, v.Data |> Option.map (lowerTypeExpr env)))
        let env' = registerTypeDef name (TDIVariant (name, typeParams, variantTypes)) env
        Ok (variants |> List.fold (fun e v ->
            registerVariantTag v.Name name (v.Data |> Option.map (lowerTypeExpr env)) e) env')

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
let rec zonkType (subst: Subst) (ty: IRType) : IRType =
    let resolved = subst.Resolve ty
    match resolved with
    | IRTInfer n ->
        // Function-boundary HM type variables survive zonking — IR-phase
        // monomorphization will substitute them at call sites. Genuinely
        // unresolved (non-boundary) inference vars still default to Float64
        // for backwards compatibility with underconstrained local lets.
        if subst.IsPolymorphicId(n) then resolved
        else IRTScalar ETFloat64
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
        | TExprReduce (a, k) -> TExprReduce (z a, z k)
        | TExprTranspose (a, d1, d2) -> TExprTranspose (z a, d1, d2)
        | TExprDecompact (a, d) -> TExprDecompact (z a, d)
        | TExprGram (l, r, s) -> TExprGram (z l, z r, s)
        | TExprArrayNegate a -> TExprArrayNegate (z a)
        | TExprArrayConjugate a -> TExprArrayConjugate (z a)
        | TExprExtents a -> TExprExtents (z a)
        | TExprZero -> TExprZero
        | TExprReplicate (c, b) -> TExprReplicate (z c, z b)
        | TExprAssign (l, r) -> TExprAssign (z l, z r)
        | TExprPartialApp (op, arg, isL) -> TExprPartialApp (op, z arg, isL)
        // Ternary
        | TExprIf (c, t, e) -> TExprIf (z c, z t, z e)
        // Indexing
        | TExprApp (f, args) -> TExprApp (z f, zs args)
        | TExprTupleIndex (t, i) -> TExprTupleIndex (z t, z i)
        | TExprIndex (arr, idxs, id) -> TExprIndex (z arr, zs idxs, id)
        | TExprField (obj, fld, idx) -> TExprField (z obj, fld, idx)
        // Collections
        | TExprTuple es -> TExprTuple (zs es)
        | TExprComplexLit (re, im) -> TExprComplexLit (z re, z im)
        | TExprArrayLit (es, arrTy) -> TExprArrayLit (zs es, arrTy)
        | TExprZip es -> TExprZip (zs es)
        | TExprStack es -> TExprStack (zs es)
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
                            SharedIndexType = info.SharedIndexType |> Option.map (zonkIndexType subst)
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
        SubBindings = b.SubBindings |> List.map (fun (n, id, ty) -> (n, id, zt ty)) }

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
    | TTDStruct (n, tp, flds, inv) ->
        TTDStruct (n, tp, flds |> List.map (fun (fn, ft) -> (fn, zt ft)),
                   inv |> Option.map (zonkExpr subst))
    | TTDVariant (n, tp, vs) ->
        TTDVariant (n, tp, vs |> List.map (fun (vn, vt) -> (vn, vt |> Option.map zt)))
    | TTDIndexType _ | TTDEnumIdx _ ->
        // Index aliases carry concrete extents (literal int) and (for EnumIdx)
        // concrete value lists. No inference variables to resolve, pass through.
        td

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
    match valuesExpr with
    | ExprArrayLit elems ->
        let isInt e =
            match e with
            | ExprLit (LitInt _) | ExprUnaryOp (OpNeg, ExprLit (LitInt _)) -> true
            | _ -> false
        let isString e = match e with ExprLit (LitString _) -> true | _ -> false
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
    | DeclImpl impl ->
        impl.Methods |> List.collect (fun m ->
            (m.Params |> List.collect (fun p -> walkOpt p.Type))
            @ walkOpt m.ReturnType)
    | DeclInterface _ | DeclImport _ | DeclUnit _ -> []

let checkModule (env: TypeEnv) (modul: ModuleDecl) : TypedModule * TypeEnv * CompileError list =
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
                let name = match binding.Pattern with PatVar n -> n | _ -> "_"
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
            | DeclLet b -> sprintf "in let binding '%s'" (match b.Pattern with PatVar n -> n | _ -> "_")
            | DeclStatic b -> sprintf "in static binding '%s'" (match b.Pattern with PatVar n -> n | _ -> "_")
            | DeclFunction f -> sprintf "in function '%s'" f.Name
            | DeclType td ->
                match td with
                | TyDeclAlias (n, _, _) | TyDeclStruct (n, _, _, _) | TyDeclSum (n, _, _) -> sprintf "in type '%s'" n
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
            // Continue with pre-failure env — the failed decl is skipped
    
    let typedModule = { Name = Some modul.Name; Decls = List.rev decls }
    // Zonk: resolve all IRTInfer through the substitution, default unsolved to Float64
    let zonked = zonkModule currentEnv.Subst typedModule
    (zonked, currentEnv, List.rev errors)

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
    let validationErrors = IndexTypeValidator.validateProgram program
    if not validationErrors.IsEmpty then
        let compileErrors =
            validationErrors |> List.map (fun e ->
                { Error = Other e.Message; Span = e.Span; Context = [e.DeclName] })
        Error compileErrors
    else
        let (tp, builder, errors, warnings) = checkProgram program
        if errors.IsEmpty then Ok (tp, builder, warnings)
        else Error errors
