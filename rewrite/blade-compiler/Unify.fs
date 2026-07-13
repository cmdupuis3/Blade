// Unification core: the error types, the mutable substitution, occursIn,
// unify, and type schemes' free-variable machinery (audit §4:
// Check/Unify.fs — "already self-contained in the prototype"). Extracted
// verbatim from TypeCheck.fs (Phase 3).
module Blade.Unify

open Blade.Ast
open Blade.IR
open Blade.Types

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
        | IRTDist (order, elem, axes) -> IRTDist (order, this.Resolve elem, axes)
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
    | IRTDist (_, elem, _) -> occursIn id elem
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
    // IRTDist unification: strict, like IRTIdxTagged — no asymmetric arms,
    // so a bare tuple of arrays never flows into a Dist (only the dist
    // construction intrinsic and dist-typed operators produce Dist values).
    //   1. Carried orders must be EQUAL (the order guard's foundation —
    //      combining or passing dists of different stochastic order is a
    //      type error, not a runtime one).
    //   2. Axes must agree positionally, with the same compatibility rule
    //      as ArrayElem index types: user-named tags are nominative,
    //      synthetic (__-prefixed) tags are structural, extents are NOT
    //      compared (runtime values), symmetry classes must be compatible.
    //   3. Element types unify recursively.
    | IRTDist (o1, e1, ax1), IRTDist (o2, e2, ax2) ->
        if o1 <> o2 || ax1.Length <> ax2.Length then
            Error (TypeMismatch (t1, t2))
        else
            let isSyntheticTag (t: string) = t.StartsWith("__")
            let axisMismatch =
                List.zip ax1 ax2 |> List.exists (fun (i1, i2) ->
                    match i1.Tag, i2.Tag with
                    | Some t1, Some t2 when t1 <> t2
                                            && not (isSyntheticTag t1)
                                            && not (isSyntheticTag t2) -> true
                    | _ ->
                        i1.Symmetry <> i2.Symmetry && i1.Symmetry <> SymNone && i2.Symmetry <> SymNone)
            if axisMismatch then Error (TypeMismatch (t1, t2))
            else unify subst e1 e2
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
    | IRTDist (_, elem, _) -> freeInferVars subst elem
    | IRTArrow (slots, ret, _) ->
        let slotVars =
            slots |> List.map (function
                | SVal ty -> freeInferVars subst ty
                | SIdx _ | SIdxVirt _ -> Set.empty)
            |> Set.unionMany
        Set.union slotVars (freeInferVars subst ret)
    | IRTGroupKeys _ -> Set.empty

