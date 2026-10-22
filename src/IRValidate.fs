// IR validation: the post-lowering structural validity sweep
// (validateModule / validateIR) and dead-polymorph elimination.
module Blade.IRValidate

open Blade.Types
open Blade.IR
open Blade.IRMono
open Blade.IRPrint

/// Validation error with context
type IRValidationError = {
    Message: string
    Context: string  // e.g. "in binding 'result'" or "in function 'covariance'"
}

/// Recursively collect all types from an IRExpr tree. The per-variant TYPE
/// contributions are enumerated in `own` (a contribution override with a
/// default, not a traversal); recursion into children is the canonical
/// ExprShape fold, so no variant's subtree can be silently skipped. (A bare
/// `| _ -> []` catchall would stop RECURSION at whichever variants it didn't
/// enumerate -- IRSlice, IRShift, IRMask, IRZip, ... -- hiding any unresolved
/// types below them from the validator.)
let collectTypesInExpr (expr: IRExpr) : IRType list =
    let rec go (e: IRExpr) : IRType list =
        let own =
            match e with
            | IRVar (_, ty) -> [ty]
            | IRParam (_, _, ty) -> [ty]
            | IRApp (_, _, retTy) -> [retTy]
            | IRArrayLit (_, arrTy) -> [mkArrayLike arrTy]
            | IRApplyCombinator info -> [info.OutputType]
            | IRComposeApply info -> [info.OutputType]
            | _ -> []
        own @ (childrenOf e |> List.collect go)
    go expr

/// Check if a type contains any unresolved IRTInfer
let rec containsInfer (ty: IRType) : int option =
    match ty with
    | IRTInfer id -> Some id
    | IRTTuple ts -> ts |> List.tryPick containsInfer
    | IRTComputation inner -> containsInfer inner
    | IRTUnitAnnotated (inner, _) -> containsInfer inner
    | IRTIdxTagged (inner, _) -> containsInfer inner
    | IRTPoly (inner, _) -> containsInfer inner
    | IRTArrow (slots, ret, _) ->
        let slotInfer =
            slots |> List.tryPick (function
                | SVal ty -> containsInfer ty
                | SIdx _ | SIdxVirt _ -> None)
        match slotInfer with
        | Some _ -> slotInfer
        | None -> containsInfer ret
    | _ -> None

// Dead-polymorph elimination (whole program, post-monomorphization)

/// Does this function still carry an unresolved type variable ANYWHERE -- its
/// params, its return type, or a type inside its body? The monomorphizers'
/// own boundary test is `hasTypeVarsInParams`, which reads the SIGNATURE only;
/// this is the test validation actually applies, so it is what "cannot be
/// emitted as C++" means.
let internal funcStillPolymorphic (f: IRFuncDef) : bool =
    let inSig =
        (f.Params |> List.exists (fun p -> (containsInfer p.Type).IsSome))
        || (containsInfer f.RetType).IsSome
    inSig || (collectTypesInExpr f.Body |> List.exists (fun t -> (containsInfer t).IsSome))

/// Drop functions that are STILL POLYMORPHIC after monomorphization and are
/// unreachable from the program's roots (its module bindings).
///
/// WHY THIS IS NEEDED. `monomorphizeHMFunctions` drops an uninstantiated
/// generic and keeps whatever is still referenced ("orphan rescue"), but its
/// membership test -- `hasTypeVarsInParams` -- reads the SIGNATURE only. A
/// lambda LIFTED out of a never-called generic's body can easily have concrete
/// params while its body is full of the parent's type vars: it is then not an
/// hmFunc at all, so it survives in `Functions` unconditionally, is validated,
/// and reports BL6001 "unresolved type variable T?N in body" for a function
/// nothing can ever call. Worse, it and its siblings then keep EACH OTHER
/// alive: a call from the dead parent to another generic still registers as an
/// HM call site, so a spec (and its lambda clones) is built for a call that
/// will never run, and `specFuncs`/`cloneFuncs` are two of the four things the
/// orphan rescue seeds from -- so the abstract kernels get rescued as well.
/// Measured on `examples/lswosa.blade` with no driver: one uncalled generic
/// produced 11 BL6001s across three lifted lambdas. The corpus reduction
/// (functions/084) shows all three ingredients are needed -- unit-annotated
/// type vars, a call to another generic, and a grouped zip row map nested in a
/// per-element lambda -- which is why no simpler uncalled generic ever tripped
/// it and the gap survived this long.
///
/// The rule is REACHABILITY, not shape: roots are the module bindings (the
/// program proper), closed transitively through the bodies of functions that
/// survive. Reachability is computed over ALL functions, not just polymorphic
/// ones, so a concrete helper called only from a live abstract function keeps
/// that function alive.
///
/// SAFETY. Only a still-polymorphic function is ever dropped, and such a
/// function cannot be emitted as valid C++ regardless -- today it produces
/// either this BL6001 or an unresolved-type sentinel in the generated source.
/// So the choice is between refusing a program that has no error in it and
/// dropping code no call site can reach; a fully concrete unused function is
/// untouched, and emission order for everything kept is unchanged.
///
/// WHOLE-PROGRAM, because a binding in module A can be the only reference to a
/// function defined in module B; per-module reachability would drop it.
///
/// UNAPPLIED-GENERIC BINDINGS ARE ROOTS THAT MUST NOT BE. Reachability starts at
/// the module bindings, so a binding that merely NAMES a generic keeps it (and
/// everything its body calls) alive with nothing to pin the type vars. A REPL or
/// notebook cell containing the bare name of a generic kernel is exactly that
/// shape -- the cell lowers to `let __exprN = covariance`, eta-expanded to a
/// function VALUE -- and it sprayed 23 BL6001s naming `mean` and lifted lambdas,
/// for a cell whose only sin was echoing a function.
///
/// Such a binding is itself unrepresentable and is dropped first, for the same
/// reason the functions are: its C++ type cannot be written. Nothing downstream
/// loses anything, because the CONCRETE twin of this cell already emits no
/// output -- `let __exprN = plain` becomes a `std::function` local that is never
/// printed, since a function value has no printed form. So the fix makes the
/// generic behave exactly like the non-generic instead of inventing a
/// presentation, and the type echo a REPL shows for such a cell comes from the
/// type checker (which resolves it fine) and not from anything here.
///
/// A binding referenced by ANOTHER binding is kept regardless: consuming it
/// would be an application, which pins the vars, and if that somehow did not
/// happen the reference would dangle. Narrow on purpose -- the value must be a
/// FUNCTION type that still carries an inference var.
let eliminateDeadPolymorphs (program: IRProgram) : IRProgram =
    let allFuncs =
        program.Modules |> List.collect _.Functions
    if not (allFuncs |> List.exists funcStillPolymorphic) then program
    else
    let funcById = allFuncs |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    // Every id an expression names. `IRVar` is the one reference form that
    // matters here and it covers first-class kernel slots as well as calls --
    // the same walk the orphan rescue uses.
    let referencedIn (e: IRExpr) : Set<IRId> =
        let acc = System.Collections.Generic.HashSet<IRId>()
        mapIRExpr (fun n ->
            (match n with
             | IRVar (id, _) -> acc.Add id |> ignore
             | _ -> ())
            n) e |> ignore
        Set.ofSeq acc
    // A function-typed binding that still carries an inference var: see the
    // unapplied-generic note above. Kept anyway when some other binding names
    // it, so a reference can never be left dangling.
    let bindingIdsReferencedElsewhere =
        program.Modules
        |> List.collect _.Bindings
        |> List.map (fun b -> referencedIn b.Value)
        |> List.fold Set.union Set.empty
    let isUnappliedGenericBinding (b: IRBinding) =
        match b.Type with
        | FuncElem _ | IRTArrow _ ->
            (containsInfer b.Type).IsSome
            && not (Set.contains b.Id bindingIdsReferencedElsewhere)
        | _ -> false
    let liveBindingsOf (m: IRModule) =
        m.Bindings |> List.filter (isUnappliedGenericBinding >> not)

    let mutable reachable : Set<IRId> = Set.empty
    let mutable frontier =
        program.Modules
        |> List.collect (fun m -> liveBindingsOf m |> List.map _.Value)
        |> List.map referencedIn
        |> List.fold Set.union Set.empty
        |> Set.toList
    while not frontier.IsEmpty do
        let mutable next = Set.empty
        for id in frontier do
            if not (Set.contains id reachable) then
                reachable <- Set.add id reachable
                match Map.tryFind id funcById with
                | Some f -> next <- Set.union next (referencedIn f.Body)
                | None -> ()
        frontier <- next |> Set.filter (fun i -> not (Set.contains i reachable)) |> Set.toList
    { program with
        Modules =
            program.Modules
            |> List.map (fun m ->
                { m with
                    Bindings = liveBindingsOf m
                    Functions =
                        m.Functions
                        |> List.filter (fun f ->
                            Set.contains f.Id reachable || not (funcStillPolymorphic f)) }) }

/// Validate a single IRModule, returning a list of errors
let validateModule (externalIds: Set<IRId>) (modul: IRModule) : IRValidationError list =
    let errors = ResizeArray<IRValidationError>()
    let addError ctx msg = errors.Add({ Message = msg; Context = ctx })

    // checkApplyInfo (below) resolves kernel slots through `resolveCallable`,
    // which needs the CallablesTable installed in the AsyncLocal analysis
    // context; install it via buildCallablesTableForModule (so let-bound
    // kernel references resolve through their alias) and restore the prior
    // context on exit so the validator doesn't leak state.
    let savedCtx = setCallablesContext (buildCallablesTableForModule modul)

    // Track all defined IDs (bindings + functions). External Ids come from
    // other modules visible via imports; without import metadata in
    // IRModule the validator can't cheaply distinguish "imported and used"
    // from "unrelated module's Id that happens to match", so it accepts all
    // program Ids as in-scope.
    let moduleIds =
        let bindIds = modul.Bindings |> List.map _.Id |> Set.ofList
        let funcIds = modul.Functions |> List.map _.Id |> Set.ofList
        Set.unionMany [bindIds; funcIds; externalIds]

    // Tag/IxKind agreement: the two encodings must never diverge -- a
    // construction that stamps a sentinel Tag without the matching IxKind
    // (or vice versa) is exactly the valid-but-wrong hazard this field
    // exists to kill. ixKindOfTag maps sentinels to kinds and everything
    // else to IxKPlain, so equality enforces both directions.
    let rec indexTypesOfType (ty: IRType) : IRIndexType list =
        match ty with
        | IRTArrow (slots, ret, _) ->
            (slots |> List.collect (function
                | SIdx ix | SIdxVirt ix -> [ix]
                | SVal t -> indexTypesOfType t))
            @ indexTypesOfType ret
        | IRTTuple ts -> ts |> List.collect indexTypesOfType
        | IRTComputation t | IRTPoly (t, _)
        | IRTUnitAnnotated (t, _) | IRTIdxTagged (t, _) -> indexTypesOfType t
        | _ -> []
    let checkKindAgreement ctx (ty: IRType) =
        for ix in indexTypesOfType ty do
            if ixKindOfTag ix.Tag <> ix.IxKind then
                addError ctx (sprintf "index type Tag/IxKind disagree: Tag=%A IxKind=%A (index id %d)" ix.Tag ix.IxKind ix.Id)

    // --- Check 1: No unresolved IRTInfer in binding types ---
    for b in modul.Bindings do
        let ctx = $"in binding '{b.Name}'"
        match containsInfer b.Type with
        | Some id -> addError ctx $"unresolved type variable T?{id} in declared type"
        | None -> ()
        checkKindAgreement ctx b.Type
        // Also check types inside the expression tree
        for ty in collectTypesInExpr b.Value do
            match containsInfer ty with
            | Some id -> addError ctx $"unresolved type variable T?{id} in expression"
            | None -> ()
            checkKindAgreement ctx ty

    // --- Check 1b: No unresolved IRTInfer in function types ---
    for f in modul.Functions do
        let ctx = $"in function '{f.SourceName}'"
        match containsInfer f.RetType with
        | Some id -> addError ctx $"unresolved type variable T?{id} in return type"
        | None -> ()
        checkKindAgreement ctx f.RetType
        for p in f.Params do
            match containsInfer p.Type with
            | Some id -> addError ctx $"unresolved type variable T?{id} in param '{p.Name}'"
            | None -> ()
            checkKindAgreement ctx p.Type
        for ty in collectTypesInExpr f.Body do
            match containsInfer ty with
            | Some id -> addError ctx $"unresolved type variable T?{id} in body"
            | None -> ()
            checkKindAgreement ctx ty

    // --- Check 1c: numeric cast legality, after monomorphization ---
    // A cast whose operand was a SIGNATURE variable of a generic function
    // (`Float64(reduce(row, (+)))` over `row: T^1`) is legal or not per
    // instance; the call judgment judges every call it can see
    // (TypeCheckSupport.genericObligationClash), and this is the backstop for the
    // seams it cannot (an eta-wrapped kernel whose argument was open): a
    // specialized body casting a complex value to a real one, or a float to
    // an integer without a visible floor/ceil, is refused here instead of
    // compiling to a C++ error (complex) or a silent truncation (float).
    let rec checkCasts (ctx: string) (e: IRExpr) =
        (match e with
         | IRUnaryOp (IRCast (target, _), operand) ->
             let src =
                 match typeOf operand with
                 | IRTScalar et | IRTUnitAnnotated (IRTScalar et, _) | IRTIdxTagged (IRTScalar et, _) -> Some et
                 | _ -> None
             let rounded =
                 match operand with
                 | IRUnaryOp (IRMath ("floor" | "ceil"), _) -> true
                 | _ -> false
             let isInt = function ETInt32 | ETInt64 -> true | _ -> false
             let isFloat = function ETFloat32 | ETFloat64 -> true | _ -> false
             let isComplex = function ETComplex64 | ETComplex128 -> true | _ -> false
             (match src with
              | Some s when isComplex s && not (isComplex target) ->
                  addError ctx $"BL3019: a cast to {ppIRType (IRTScalar target)} of a complex value (a generic function instantiated at {ppIRType (IRTScalar s)}): project a real component first -- real(z), imag(z), abs(z), or arg(z)"
              | Some s when isFloat s && isInt target && not rounded ->
                  addError ctx $"BL3019: a cast to {ppIRType (IRTScalar target)} of a {ppIRType (IRTScalar s)} value (a generic function instantiated at a float) would truncate: spell the rounding at the cast site -- floor(x) or ceil(x)"
              | _ -> ())
         | _ -> ())
        let (ExprShape (children, _)) = e
        children |> List.iter (checkCasts ctx)
    for b in modul.Bindings do checkCasts $"in binding '{b.Name}'" b.Value
    // A specialization's C++ name (`toint_HM_10000_double`) is not a name the
    // program wrote: the message names the declaration it was cloned from.
    for f in modul.Functions do checkCasts $"in function '{f.SourceName}'" f.Body

    // --- Check 2: No dangling VarId references ---
    // Scope threads through every binder via BinderShape (IRLet's body,
    // IRForRange's body, a match case's guard and body) and recursion is the
    // ExprShape fold, so no variant's subtree is skipped -- the old hand list
    // stopped at IRGram, IRMatmul, IRSolve, IRSlice, IRShift, IRMask, IRZip,
    // IRStack, IRJoin, IRGroupBy, IRCurry, IRReverse, IRContains, ... and
    // never looked below them.
    let rec checkScope (scope: Set<IRId>) (ctx: string) (expr: IRExpr) =
        match expr with
        | IRVar (id, _) ->
            if not (Set.contains id scope) then
                addError ctx $"dangling VarId reference: v{id}"
        | BinderShape (free, scopes) ->
            free |> List.iter (checkScope scope ctx)
            for (bound, kids) in scopes do
                let inner = Set.union scope bound
                kids |> List.iter (checkScope inner ctx)
        | ExprShape (children, _) -> children |> List.iter (checkScope scope ctx)

    // A module binding sees the functions (any module's), the bindings of
    // OTHER modules (imports; ids are program-unique), and the bindings of
    // this module declared BEFORE it -- not itself, not a later one: the
    // bindings emit in order as locals of one scope, so a forward or self
    // reference is a use before definition. (The scope used to be seeded
    // with every binding id, which made that check vacuous.)
    let ownBindingIds = modul.Bindings |> List.map _.Id |> Set.ofList
    let mutable cumulativeScope = Set.difference moduleIds ownBindingIds
    for b in modul.Bindings do
        let ctx = $"in binding '{b.Name}'"
        checkScope cumulativeScope ctx b.Value
        cumulativeScope <- Set.add b.Id cumulativeScope

    for f in modul.Functions do
        let ctx = $"in function '{f.SourceName}'"
        let paramIds = f.Params |> List.map _.VarId |> Set.ofList
        // Lifted lambdas live in module.Functions with their captures in
        // `f.Captures` (separate from `f.Params`). The captures' Ids
        // reference the enclosing source-level var; the lambda's body
        // references those Ids directly. Because the lambda is its own
        // top-level function, the enclosing function's params aren't in
        // scope at the validator's `for f in modul.Functions` loop; we
        // have to add the function's own Captures' Ids to the visible
        // scope so the body's references resolve.
        let captureIds = f.Captures |> List.map _.Id |> Set.ofList
        let funcScope = Set.unionMany [moduleIds; paramIds; captureIds]
        checkScope funcScope ctx f.Body

    // --- Check 2b: inline allocation lifetime regions ---
    // Unit lets make the events sequence points. The begin binder is a real
    // IR id and the release's scope/pools are normal IRVar children, so every
    // canonical substitution and freshening sees the ownership references.
    let hasLifetimeEvents =
        (modul.Bindings |> List.exists (fun b -> containsPoolLifetimeEvent b.Value))
        || (modul.Functions |> List.exists (fun f -> containsPoolLifetimeEvent f.Body))
    if hasLifetimeEvents then
        let nodeTypes e =
            match e with
            | IRVar (_, ty) | IRParam (_, _, ty) | IRApp (_, _, ty) -> [ty]
            | IRArrayLit (_, at) -> [mkArrayLike at]
            | IRMethodFor info -> info.ArrayTypes |> List.map mkArrayLike
            | IRApplyCombinator info -> info.OutputType :: (info.ArrayTypes |> List.map mkArrayLike)
            | IRComposeApply info -> [info.OutputType]
            | IRZero ty | IRUninit ty -> [ty]
            | _ -> []
        let rec typeRefs ty =
            let indices xs = xs |> List.map (fun ix -> Set.union (Set.ofList ix.Dependencies) (exprRefs ix.Extent)) |> Set.unionMany
            match ty with
            | IRTArrow (slots, ret, _) ->
                Set.union (typeRefs ret)
                    (slots |> List.map (function SVal t -> typeRefs t | SIdx ix | SIdxVirt ix -> indices [ix]) |> Set.unionMany)
            | IRTTuple ts -> ts |> List.map typeRefs |> Set.unionMany
            | IRTLoop lt -> (lt.ArrayTypes @ Option.toList lt.KernelType) |> List.map typeRefs |> Set.unionMany
            | IRTComputation t | IRTPoly (t, _) | IRTUnitAnnotated (t, _) -> typeRefs t
            | IRTIdxTagged (t, IRefAnon (_, extent)) -> Set.union (typeRefs t) (exprRefs extent)
            | IRTIdxTagged (t, _) -> typeRefs t
            | IRTDist (_, t, axes) -> Set.union (typeRefs t) (indices axes)
            | IRTGroupKeys (outer, source, _) -> indices [outer; source]
            | IRTScalar _ | IRTUnit | IRTNat _ | IRTNamed _ | IRTInfer _ -> Set.empty
        and exprRefs e =
            let own = match e with IRVar (id, _) -> Set.singleton id | _ -> Set.empty
            let carried = nodeTypes e |> List.map typeRefs |> Set.unionMany
            let structural = childrenOf e |> List.map exprRefs |> Set.unionMany
            let axes =
                match e with
                | IRRange (xs, _) -> xs
                | IRApplyCombinator info -> info.SharedIndexTypes @ info.KernelTDims
                | IRMethodFor info -> info.SharedIndexTypes
                | _ -> []
            let embedded = axes |> List.map (fun ix -> Set.union (Set.ofList ix.Dependencies) (exprRefs ix.Extent)) |> Set.unionMany
            Set.unionMany [own; carried; structural; embedded]
        let initialDependencies =
            modul.Functions
            |> List.map (fun f ->
                let attrs = exprAttrs f.Body
                let ps = f.Params |> List.map _.VarId |> Set.ofList
                (f.Id, Set.union (f.Captures |> List.map _.Id |> Set.ofList)
                    (Set.difference (exprRefs f.Body) (Set.union ps attrs.BoundVars))))
            |> Map.ofList
        let storageDependencies (v: IRExpr) =
            // Scalar results are snapshots. Keep dependencies of carried extents
            // separately: an array/view/deferred value may read both its expression
            // operands and the expressions embedded in its type/loop metadata.
            if Blade.Optimize.holdsNoStorage (typeOf v) then
                nodeTypes v |> List.map typeRefs |> Set.unionMany
            else exprRefs v
        let referenceClosure (deps: Map<IRId, Set<IRId>>) (id: IRId) =
            let rec loop pending seen =
                match pending with
                | [] -> seen
                | x :: rest when Set.contains x seen -> loop rest seen
                | x :: rest ->
                    let next = Map.tryFind x deps |> Option.defaultValue Set.empty |> Set.toList
                    loop (next @ rest) (Set.add x seen)
            loop [id] Set.empty
        let checkLifetimes ctx deps expr =
            let mutable hasEvent = false
            iterIRExpr (function IRBeginPoolScope _ | IRReleasePools _ -> hasEvent <- true | _ -> ()) expr
            let checkRef released deps id =
                let dead = Set.intersect released (referenceClosure deps id)
                if not dead.IsEmpty then
                    let ids = dead |> Seq.map (sprintf "v%d") |> String.concat ", "
                    addError ctx $"reference to released inline pool: {ids} (through v{id})"
            let rec value released deps e =
                for ty in nodeTypes e do
                    for id in typeRefs ty do checkRef released deps id
                let axes =
                    match e with
                    | IRRange (xs, _) -> xs
                    | IRApplyCombinator info -> info.SharedIndexTypes @ info.KernelTDims
                    | IRMethodFor info -> info.SharedIndexTypes
                    | _ -> []
                for ix in axes do
                    for id in Set.union (Set.ofList ix.Dependencies) (exprRefs ix.Extent) do checkRef released deps id
                match e with
                | IRVar (id, _) -> checkRef released deps id
                | IRLet _ -> spine [] released deps e
                | IRBeginPoolScope _ | IRReleasePools _ ->
                    addError ctx "inline lifetime event must be a unit let statement"
                | ExprShape (children, _) -> children |> List.iter (value released deps)
            and spine (regions: (IRId * Map<IRId, IRType>) list) released deps e =
                match e with
                | IRLet (sid, IRBeginPoolScope _, rest) ->
                    spine ((sid, Map.empty) :: regions) released deps rest
                | IRLet (_, IRReleasePools (scope, pools, _), rest) ->
                    let ids = pools |> List.choose (function IRVar (id, _) -> Some id | _ -> None)
                    let named = Set.ofList ids
                    if ids.Length <> pools.Length || ids.Length <> named.Count then
                        addError ctx "inline release pools must be distinct binding references"
                    let remaining =
                        match scope, regions with
                        | IRVar (sid, IRTUnit), (owner, owned) :: parents when sid = owner ->
                            let ownedIds = owned |> Map.toSeq |> Seq.map fst |> Set.ofSeq
                            if named <> ownedIds then
                                addError ctx "inline release names pools not owned by its inline region, or omits an owned pool"
                            for pool in pools do
                                match pool with
                                | IRVar (pid, pty) when Map.tryFind pid owned = Some pty -> ()
                                | _ -> addError ctx "inline release pool type disagrees with its owned allocation"
                            parents
                        | _ ->
                            addError ctx "inline release has no matching innermost begin event"
                            regions
                    spine remaining (Set.union released named) deps rest
                | IRLet (id, v, rest) ->
                    value released deps v
                    let regions' =
                        match regions, v with
                        | (sid, owned) :: parents, IRCompute (IRApplyCombinator _)
                            when (match stripUnits (typeOf v) with ArrayElem _ -> true | _ -> false) ->
                            (sid, Map.add id (typeOf v) owned) :: parents
                        | _ -> regions
                    let deps' = Map.add id (storageDependencies v) deps
                    spine regions' released deps' rest
                | tail ->
                    value released deps tail
                    if not regions.IsEmpty then addError ctx "inline pool scope has no release event"
            if hasEvent then spine [] Set.empty deps expr
        let mutable lifetimeDeps = initialDependencies
        for b in modul.Bindings do
            checkLifetimes $"in binding '{b.Name}'" lifetimeDeps b.Value
            let deps = Set.union (typeRefs b.Type) (if Blade.Optimize.holdsNoStorage b.Type then Set.empty else exprRefs b.Value)
            lifetimeDeps <- Map.add b.Id deps lifetimeDeps
        for f in modul.Functions do
            checkLifetimes $"in function '{f.SourceName}'" lifetimeDeps f.Body

    // --- Check 3: ApplyInfo consistency ---
    let checkApplyInfoNode (ctx: string) (expr: IRExpr) =
        match expr with
        | IRApplyCombinator info ->
            if info.Arrays.Length <> info.ArrayTypes.Length then
                addError ctx $"ApplyInfo: Arrays.Length={info.Arrays.Length} != ArrayTypes.Length={info.ArrayTypes.Length}"
            if info.Arrays.Length <> info.Identities.Length then
                addError ctx $"ApplyInfo: Arrays.Length={info.Arrays.Length} != Identities.Length={info.Identities.Length}"
            if info.SDimsPerArray.Length <> info.Arrays.Length && info.SDimsPerArray.Length <> 0 then
                addError ctx $"ApplyInfo: SDimsPerArray.Length={info.SDimsPerArray.Length} != Arrays.Length={info.Arrays.Length}"
            // Canonical apply: Kernel slot is a callable reference, either
            // IRVar(id, _) or IRReynolds(IRVar(id, _), _); `resolveKernel`
            // peels any Reynolds wrapper. `info.Loop = IRObjectFor _` can
            // only arise from canonical `object_for(g) <@> A` (the
            // slot-inverted compose case routes through IRComposeApply), so
            // it also unambiguously implies a callable kernel. Skip the
            // check when Loop is IRVar (let-bound; could resolve to either
            // shape, and the binding env isn't available here) -- codegen
            // retains its own resolution for that case.
            let kernelSlotIsCallable =
                match info.Loop with
                | IRMethodFor _ | IRObjectFor _ -> true
                | _ -> false
            if kernelSlotIsCallable then
                match resolveKernel info.Kernel with
                | Some rk ->
                    let lInfo = rk.Callable
                    if lInfo.Params.Length <> info.KernelInputRanks.Length then
                        addError ctx $"ApplyInfo: kernel params={lInfo.Params.Length} != KernelInputRanks.Length={info.KernelInputRanks.Length}"
                    // Verify CommGroup indices are in range
                    for cg in lInfo.CommGroups do
                        for idx in cg do
                            if idx < 0 || idx >= lInfo.Params.Length then
                                addError ctx $"CommGroup index {idx} out of range [0, {lInfo.Params.Length})"
                | None ->
                    // Identify the structural form to make the error
                    // actionable for whoever introduced the malformed
                    // IR. Shape names match the IRExpr discriminator so
                    // a grep against the constructor finds the producer.
                    let (inner, desc) = peelReynolds info.Kernel
                    let shapeDesc =
                        match inner with
                        | IRVar (id, _) ->
                            $"IRVar(v{id}) [id resolves in neither CallablesTable nor synthetic registry]"
                        | IRLit _ -> "IRLit [literal in kernel slot]"
                        | IRBinOp _ -> "IRBinOp [unlifted operator expression]"
                        | IRApp _ -> "IRApp [unlifted application]"
                        | IRZero _ -> "IRZero [zero placeholder; should have been synthesized to a callable]"
                        | IRReynolds _ -> "IRReynolds [nested Reynolds wrapper, not supported]"
                        | _ -> "non-callable expression"
                    let prefix =
                        if desc.HasReynolds then "ApplyInfo: IRReynolds inner is"
                        else "ApplyInfo: kernel slot is"
                    addError ctx $"{prefix} {shapeDesc}"
        | IRComposeApply info ->
            // Compose-apply: InputArrays threaded through a composed
            // object chain. Composition should resolve to IRComposeObj
            // (possibly through a let-binding); InputArrays must be
            // non-empty (you can't apply a compose to nothing).
            if info.InputArrays.IsEmpty then
                addError ctx "ComposeApplyInfo: InputArrays is empty"
            match info.Composition with
            | IRComposeObj _ | IRVar _ -> ()   // expected shapes
            | other ->
                let shapeName =
                    match other with
                    | IRLit _ -> "IRLit"
                    | IRObjectFor _ -> "IRObjectFor [single object, not composed]"
                    | IRMethodFor _ -> "IRMethodFor [should be IRApplyCombinator, not IRComposeApply]"
                    | _ -> "non-compose expression"
                addError ctx $"ComposeApplyInfo: Composition is {shapeName}; expected IRComposeObj or IRVar"
        | _ -> ()   // node-local: only the two apply records carry this metadata

    // --- Check 4: No empty match arms ---
    let checkEmptyMatchNode (ctx: string) (expr: IRExpr) =
        match expr with
        | IRMatch (_, []) -> addError ctx "empty match expression (no cases)"
        | _ -> ()   // node-local: only a match can have zero cases

    // Checks 3 and 4 are node-local; iterIRExpr (the ExprShape fold) visits
    // every node of every binding AND function body -- the old hand-written
    // descents skipped IRIf / IRMatch / IRApp / IRReduce / ... (check 3) and
    // function bodies entirely (check 4).
    let everyNode (ctx: string) (e: IRExpr) =
        iterIRExpr (fun n ->
            checkApplyInfoNode ctx n
            checkEmptyMatchNode ctx n) e
    for b in modul.Bindings do
        everyNode $"in binding '{b.Name}'" b.Value
    for f in modul.Functions do
        everyNode $"in function '{f.SourceName}'" f.Body

    // Restore the prior AnalysisContext so the validator doesn't
    // leak its installed CallablesTable to subsequent passes.
    restoreAnalysisContext savedCtx
    errors |> Seq.toList

/// Validate an entire IR program.
/// Pre-collects all defined Ids across modules so cross-module references
/// (selective imports of values/functions) don't appear dangling within
/// individual module validation passes.
let validateIR (program: IRProgram) : Result<IRProgram, string list> =
    let allIds =
        program.Modules |> List.collect (fun m ->
            (m.Bindings |> List.map _.Id) @
            (m.Functions |> List.map _.Id))
        |> Set.ofList
    let allErrors =
        program.Modules |> List.collect (validateModule allIds)
    if allErrors.IsEmpty then
        Ok program
    else
        let messages = allErrors |> List.map (fun e -> $"[IR Validation] {e.Context}: {e.Message}")
        Error messages

/// The diagnostic one `validateIR` message stands for. A structural defect of
/// the IR is BL6001 -- a compiler invariant, reported as the validator's own
/// band. But a check here can also find a USER error no earlier seam could see
/// (the cast-obligation backstop, check 1c: a generic function's cast judged
/// at an instance monomorphization produced); such a message names its code
/// as a `BLxxxx: ` prefix, and the diagnostic carries THAT code, with the
/// prefix and the "[IR Validation]" tag dropped from its text. Every consumer
/// of validateIR (the CLI, the REPL, the IDE server) maps messages through
/// this, so a BL3019 is not reported as BL6001 "IR validation" with "BL3019:"
/// buried in its text.
let diagnosticOfValidationMessage (s: string) : Blade.Diagnostics.Diagnostic =
    let m = System.Text.RegularExpressions.Regex.Match(
                s, @"^\[IR Validation\] (.*?): (BL\d{4}): (.*)$",
                System.Text.RegularExpressions.RegexOptions.Singleline)
    if m.Success then
        let code = m.Groups.[2].Value
        Blade.Diagnostics.mkError code (Blade.Diagnostics.Codes.phaseOfCode code) Blade.Ast.noSpan
            $"{m.Groups.[3].Value} ({m.Groups.[1].Value})"
    else
        Blade.Diagnostics.mkError "BL6001" Blade.Diagnostics.PhIRValidate Blade.Ast.noSpan s

/// The code alone (see diagnosticOfValidationMessage), for consumers that
/// carry (code, message) pairs.
let codeOfValidationMessage (s: string) : string = (diagnosticOfValidationMessage s).Code
