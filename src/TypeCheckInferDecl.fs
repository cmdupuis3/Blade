// The type checker's DECLARATION checkers, after the inference chain (cleanup round 2, K5): checkDecl and the
// checkers it dispatches to (let / static / type / impl / import / function declarations, checkFunctionDeclWith
// and checkFunctionDeclBody, the arrow and sharing variants), the type-declaration registrars
// (registerTypeDecl, registerAliasDecl, registerStructDecl, registerMutualGroupDecl) and the signature wrappers.
// They call into TypeCheckInfer's recursive core directly; nothing in the core calls them, so they sit after it.
// TypeCheck.fs (the driver) calls checkDecl, checkArrowVariant and checkSharingVariant.
module Blade.TypeCheckInferDecl

open Blade.Ast
open Blade.IR
open Blade.IRLoopStructure
open Blade.IRStorage
open Blade.IRLift
open Blade.IRMono
open Blade.IRPrint
open Blade.IRValidate
open Blade.Types
open Blade.TypedAst
open Blade.Unify
open Blade.TypeEnv
open Blade.Zonk
open Blade.TypeCheckIde
open Blade.TypeLower
open Blade.TypeCheckSupport
open Blade.TypedBuilder
open Blade.TypeCheckInferUnits
open Blade.TypeCheckInferApply
open Blade.TypeCheckInferEntries
open Blade.TypeCheckInferRecArray
open Blade.TypeCheckInfer

/// A function declaration's checked signature, handed from checkFunctionDeclWith to checkFunctionDeclBody
/// (its one call). Built from the bindings at the call; the callee binds every field back under its old name
/// in one destructuring let. A carrier, never compared (see ApplyOperands).
[<NoEquality; NoComparison; RequireQualifiedAccess>]
type FunctionDeclSig =
    { RetType: IRType
      FuncVarId: IRId
      ParamTypes: IRType list
      TypedParams: TypedParam list
      FixedParams: Map<int, IRType>
      Sharing: Map<int, int> option
      MutualReturnGroup: MutualGroupInfo option
      CustomConjuncts: (Ident * Ident list) list
      SigVarLo: int
      ShownName: string }

/// Runtime bound guards on a FUNCTION's bounded parameters (checked on entry)
/// and bounded return (checked on the value the body produces). The body is
/// rewritten to
///
///     { <param guards>; let __ret_f = <body>; <return guards>; __ret_f }
///
/// only when some guard exists, so an unbounded signature keeps its body node
/// untouched. `bodyEnv` has the parameters bound.
let rec internal wrapBoundedSignatureChecks (bodyEnv: TypeEnv) (funcDecl: FunctionDecl) (tBody: TypedExpr) : TypeResult<TypedExpr> =
    if funcDecl.IsStatic then Ok tBody else
    let span = tBody.Span
    let synAt k : Expr = { Kind = k; Span = span }
    let paramChecks =
        funcDecl.Params
        |> List.map (fun p ->
            match p.Type with
            | Some pty ->
                synthesizeBoundChecks bodyEnv (Some pty) $"parameter {p.Name} of {funcDecl.Name}" (synAt (ExprVar p.Name))
            | None -> Ok [])
        |> sequenceResults
        |> Result.map List.concat
    paramChecks |> Result.bind (fun pChecks ->
    let retName = $"__ret_{funcDecl.Name}"
    let retId = bodyEnv.Builder.FreshId()
    let retEnv = bindVarSimple retName retId tBody.Type bodyEnv
    (match funcDecl.ReturnType with
     | Some rty -> synthesizeBoundChecks retEnv (Some rty) $"the return value of {funcDecl.Name}" (synAt (ExprVar retName))
     | None -> Ok [])
    |> Result.map (fun rChecks ->
        if pChecks.IsEmpty && rChecks.IsEmpty then tBody
        else
            let body =
                if rChecks.IsEmpty then tBody
                else
                    let tb : TypedBinding = {
                        Name = retName; VarId = retId; Type = tBody.Type
                        Identity = None; IsMutable = false; IsLetMut = false; Value = tBody
                        SubBindings = []; Destructure = DSPositional; PostChecks = []; ErasedType = None }
                    mkTypedSpan (TExprBlock (TStmtLet tb :: (rChecks |> List.map TStmtExpr),
                                             Some (mkTypedSpan (TExprVar (retName, retId, None)) tBody.Type span)))
                                tBody.Type span
            if pChecks.IsEmpty then body
            else mkTypedSpan (TExprBlock (pChecks |> List.map TStmtExpr, Some body)) tBody.Type span))

/// Wrap a declared-return function body so the joint check runs at the
/// return -- the group's single verification point. The body becomes:
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
                (mname, slot, env.Builder.FreshId(), $"__mg{slot}", mTy))
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

// 11. Declaration Type Checking

and checkDecl (env: TypeEnv) (decl: Decl) : TypeResult<TypedDecl * TypeEnv> =
    // Fresh declaration: clear any statement span left over from the previous
    // decl's body so errors here can't inherit a stale location (section 3.4).
    resetCurrentStmtSpan ()
    match decl with
    | DeclLet binding ->
        checkLetDecl env binding

    | DeclStatic binding ->
        checkStaticDecl env binding

    | DeclFunction funcDecl -> checkFunctionDecl env funcDecl

    | DeclType typeDecl ->
        checkTypeDeclItem env typeDecl

    | DeclInterface ifaceDecl -> 
        let env' = { env with Interfaces = Map.add ifaceDecl.Name ifaceDecl env.Interfaces }
        Ok (TDeclInterface ifaceDecl, env')
    | DeclImpl implDecl -> 
        checkImplDecl env implDecl
    | DeclUnit unitDecl ->
        // registerUnit rejects both resolver failures at the declaration
        // site: a TERMINAL-quantity misuse (BL3011: `Unit x = speed * m` /
        // `Unit q: speed`) and an unknown name (BL3015: `Unit t = 2*pii*rad`).
        registerUnit env unitDecl
        |> Result.map (fun env' -> (TDeclUnit unitDecl, env'))
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
        checkImportDecl env qname style

and checkLetDecl (env: TypeEnv) (binding: Binding) : TypeResult<TypedDecl * TypeEnv> =
    // Top-level let: shares value-resolution and primary-name binding with
    // inferLetBinding (the let-as-expression form). Diverges from there in
    // that we surface destructured sub-vars to Lowering and wrap in a
    // TypedBinding rather than recursing into a body expression.
    inferLetBindingValue env binding |> Result.bind (fun tValue ->
        // A destructuring binding's own name is synthesized from its leaves,
        // `_(a,b)`: the interpreter's session memo is keyed by binding NAME,
        // and the old placeholder `_` made every top-level destructure in a
        // session share one key -- the second cell's `let (c, d) = ...`
        // adopted the FIRST cell's cached tuple (measured: every later
        // destructure echoed the first one's values). The leaf set is what
        // identifies the binding to the session (a rebind of the same
        // leaves supersedes by name exactly as a plain `let` does). `(`
        // cannot occur in a user name, so `_(` is the recognizer codegen
        // and the interpreter use to give the parent its `__tup_N` C++ name.
        let name =
            match binding.Pattern.Kind with
            | PatternKind.PatVar n -> n
            | _ -> "_(" + String.concat "," (patternNames binding.Pattern) + ")"
    // Provider load (e.g. `let sample = NetCDF.load("f.nc")`): resolve the
    // module's real struct type at compile time by reading the file
    // metadata, then register the dims/vars structs plus a top-level
    // module struct so field access like `sample.vars.temp` resolves to
    // the variable's real Array type rather than a fresh type var. This
    // mirrors the metadata read that Lowering.tryInvokeProvider performs;
    // the typed value SHAPE is left intact so the lowering-side provider
    // detection still fires. Ordinary (opaque) inference is the fallback
    // when the receiver is not a provider alias or the file can't be read.
        let mutable providerLoadError : TypeError option = None
        let (env, tValue) =
            match binding.Value.Kind with
            | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, "load") }, [{ Kind = ExprKind.ExprLit (LitString path) }]) ->
                match providerAliasName env alias with
                | None -> (env, tValue)
                | Some pname ->
                    // A native-library load failure is NOT a store problem:
                    // no store this provider names can ever resolve in this
                    // process, and the opaque fallback would leave every
                    // `<store>.vars.<v>` at a fresh var whose element type
                    // defaults (Float64), dying far downstream as a baffling
                    // type mismatch. Park BL2007 for the load site instead
                    // (surfaced right below; same idiom as patternError).
                    let parkNativeFailure (detail: string) =
                        setCurrentExprSpan binding.Value.Span
                        providerLoadError <- Some (ProviderNativeLoadFailure (pname, path, detail))
                        (env, tValue)
                    // The STORE's own refusal: BL2008 at the load site, for
                    // EVERY provider. A provider that names its refusals
                    // (`Types.ProviderResolutionError`: icechunk's typo'd or
                    // ambiguous ref, missing/corrupt repo, bad spec byte,
                    // Offline, tombstone, ...) and one that simply throws
                    // from its metadata read (zarr / netcdf / csv: file not
                    // found, not a store, unreadable header) land here alike.
                    //
                    // The read below is the SAME read Lowering performs, so
                    // a store that fails here cannot lower either: nothing
                    // that compiled before is refused now. What changes is
                    // WHERE the failure is reported. Falling back to the
                    // opaque type instead left every `<store>.vars.<v>` at
                    // a fresh variable, and the first thing to need a real
                    // type from it (a static extent, an element type) died
                    // with an error about ITSELF -- a BL5400 inside an FFT
                    // kernel, a BL3999 two hundred lines down -- while the
                    // actual cause, a path resolved from the wrong
                    // directory, was never mentioned. A program with no
                    // such dependent got past typecheck and the editor's
                    // fast tier reported nothing at all.
                    let parkStoreFailure (detail: string) =
                        setCurrentExprSpan binding.Value.Span
                        providerLoadError <- Some (ProviderStoreUnresolvable (pname, path, detail))
                        (env, tValue)
                    try
                        // Read the store metadata at compile time (the same read
                        // Lowering.tryInvokeProvider performs) and register the
                        // resulting struct types so `sample.vars.<v>` resolves.
                        let spec = (Blade.ProviderRegistry.tryFind pname).Value
                        let pm = spec.LoadAsModule env.Builder name path
                        // Record for the IDE hover path (Ide.collectProviderStores)
                        // so it never has to re-open the store.
                        Blade.ProviderRegistry.IdeStores.record name pm
                        // and the dimension chunk edges `Chunked<name.index.d, store>` inherits
                        Blade.ProviderRegistry.DimChunks.record name pname path
                        let (envM, moduleTy) = registerProviderModule env name pm
                        // The C++ global names this load emits (bare in every
                        // module): checkProgram refuses a second origin of one.
                        for td in pm.Types do
                            let entry =
                                match td with
                                // kind: `struct`, or `alias:<target>` for a
                                // `using` (equal targets may be redeclared).
                                | IRTDStruct (n, _) -> Some (n, "struct")
                                | IRTDEnumIdx (n, _, values) ->
                                    let target = match EnumValue.underlyingElemType values with ETString -> "string" | _ -> "int64"
                                    Some (n, $"alias:{target}")
                                | IRTDIndexType (n, _) -> Some (n, "alias:int64")
                                | IRTDVariant (n, _) -> Some (n, "type")
                                | IRTDAlias (n, _) -> Some (n, "alias:?")
                            match entry with
                            | Some (n, kind) ->
                                let row = (n, kind, name, defaultArg env.ModuleQualifier "", binding.Value.Span)
                                if not (env.ProviderEmittedTypes |> Seq.exists (fun (n2, _, b2, m2, _) -> n2 = n && b2 = name && m2 = defaultArg env.ModuleQualifier "")) then
                                    env.ProviderEmittedTypes.Add row
                            | None -> ()
                        (envM, { tValue with Type = moduleTy })
                    with
                    | :? System.DllNotFoundException as dex -> parkNativeFailure dex.Message
                    | :? System.TypeInitializationException as tix when (tix.InnerException :? System.DllNotFoundException) ->
                        parkNativeFailure tix.InnerException.Message
                    | :? Blade.Types.ProviderResolutionError as pex -> parkStoreFailure pex.Message
                    | ex -> parkStoreFailure ex.Message
            | _ -> (env, tValue)
        if providerLoadError.IsSome then Error providerLoadError.Value else
        let identity = match binding.Pattern.Kind with PatternKind.PatVar n -> Some (AIDVariable n) | _ -> None
        let assign = assignOfBindingMut binding.Mutability
        let (varId, env') = bindLetPatVar env name identity assign tValue
        // Top-level pure deferred alias classes (formalism 10.2 / 8.3):
        // a root map or an alias of one joins the class whose members are
        // one array for the identity groups (loopOperandIdentity).
        (match binding.Pattern.Kind with
         | PatternKind.PatVar _ ->
             env.ModuleLetBinders.Add varId |> ignore
             let surfaceVar = match binding.Value.Kind with ExprKind.ExprVar s -> Some s | _ -> None
             recordDeferredAliasMember env (binding.Mutability = BindLet) binding.Type.IsSome
                                       surfaceVar varId name tValue
             // ... and top-level STORED alias classes (plan 2.1, second
             // step): a never-written `let B = A` over a stored array.
             recordStoredAliasMember env binding.Mutability binding.Type.IsSome
                                     surfaceVar varId name tValue
         | _ -> ())

        // A module-level let-bound lambda is a callee like a declared
        // function: record the outside bindings it writes (#17, the
        // parallel-body race) and names (N16, the mut-argument alias).
        (match tValue.Kind with
         | TExprLambda info ->
             (match outerWritesOfBody env env info.Body with
              | [] -> env.FuncOuterWrites.Remove varId |> ignore
              | ws -> env.FuncOuterWrites.[varId] <- ws)
             (match outerReadsOfBody env env info.Body with
              | [] -> env.FuncOuterReads.Remove varId |> ignore
              | rs -> env.FuncOuterReads.[varId] <- rs)
             // D12: a pure, non-recursive module-level lambda is a body
             // the comm witness may evaluate through.
             recordWitnessBody env varId (info.Params |> List.map (_.VarId)) false
                               (effectsOfBody env (Some varId) info.Body) info.Body
         | _ -> ())

        // Dist provenance seeding: a module-level dist binding gets its
        // source-array set from the PPL elaboration state (by name --
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

        // Handle destructuring at top level -- collect sub-bindings for Lowering
        let mutable subBindings : (string * IRId * IRType) list = []
        // Shape tag handed to Lowering (see TypedAst.DestructureShape). Only
        // the PatCons arm below moves it off DSPositional.
        let mutable destructure = DSPositional
        // A destructuring pattern that cannot be satisfied by the scrutinee's
        // real type is a type ERROR, but the env-building fold below is a pure
        // expression with nowhere to return one from. Park it here and surface
        // it just before the binding is assembled; silently binding fresh type
        // vars instead is exactly how `head :: tail` would miscompile.
        let mutable patternError : TypeError option = None
        let env' =
            match binding.Pattern.Kind with
            | PatternKind.PatTuple pats ->
                // Neither reading covers the names: park the error (the
                // fold below is a pure expression with nowhere to return
                // one from) rather than letting fresh vars paper over the
                // overflow, which used to check OK and die in g++.
                (match tupleDestructureArityError env pats tValue.Type with
                 | Some e -> patternError <- Some e
                 | None -> ())
                // Resolve and determine which type list to use for binding
                let resolvedTy = env.Subst.Resolve(tValue.Type)
                let typeList =
                    match resolvedTy with
                    | IRTTuple ts ->
                        if pats.Length = ts.Length then
                            // Structural match: (w, z) against ((alpha,beta), gamma) -> w:(alpha,beta), z:gamma
                            ts
                        else
                            // Try flat match: (x, y, z) against ((alpha,beta), gamma) -> x:alpha, y:beta, z:gamma
                            let flat = IR.flattenTupleLeaves resolvedTy
                            if pats.Length = flat.Length then flat
                            else ts  // arity already parked above
                    | _ -> []
                let (entries, slots) = tuplePatternSlots pats
                destructure <- DSTupleAt (entries |> List.map (fun (_, _, s) -> s), slots)
                entries
                |> List.fold (fun e (n, posOpt, _) ->
                    let eTy =
                        match posOpt with
                        | Some i when i < typeList.Length -> env.Subst.Resolve(typeList.[i])
                        | _ -> env.Subst.Fresh()
                    let subId = env.Builder.FreshId()
                    subBindings <- subBindings @ [(n, subId, eTy)]
                    bindVarSimple n subId eTy e) env'
            | PatternKind.PatCons (h, t) ->
                // `let head :: tail = tup` splits an n-tuple into element 0
                // and the REMAINDER (`tail` is the (n-1)-tuple, not the
                // single next element). Flatten/typing/reject rules live
                // in consDestructureLeaves, shared with the block-scoped,
                // `let static` and expression-position forms so the four
                // sites cannot drift apart.
                match consDestructureLeaves env tValue.Type h t with
                | Error e ->
                    patternError <- Some e
                    env'
                | Ok leaves ->
                    destructure <- DSConsRest
                    leaves
                    |> List.fold (fun e (n, ty) ->
                        let subId = env.Builder.FreshId()
                        subBindings <- subBindings @ [(n, subId, ty)]
                        bindVarSimple n subId ty e) env'
            | PatternKind.PatStruct (structName, fieldPats) ->
                // Look up struct field types from the type definition
                // (shared with the expression-position PatStruct arm so
                // both read the field list from the same place).
                let structFields = structFieldTypesOf env tValue.Type
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

        // Surface a destructuring pattern that the scrutinee's type cannot
        // satisfy (see patternError above) before anything else is built --
        // the sub-binding ids are already allocated at this point, but a
        // failed decl never reaches Lowering so they simply go unused.
        if patternError.IsSome then Error patternError.Value else
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
            | PatternKind.PatVar n ->
                // Plus the bounded-primitive annotation guards (section 2.4) --
                // see synthesizeBoundChecks: bounds live only on the
                // surface type, never on tValue.Type.
                let subject = mkExpr binding.Pattern.Span (ExprVar n)
                synthesizeStructChecks env' tValue.Type subject
                |> Result.bind (fun sc ->
                    synthesizeBoundChecks env' binding.Type n subject
                    |> Result.map (fun bc -> sc @ bc))
            | _ -> Ok []
        structChecksR |> Result.map (fun structChecks ->
        let postChecks =
            mutualChecks @ (structChecks |> List.map (fun c -> (env.Builder.FreshId(), c)))
        // A DEFERRED former (arity-polymorphic object_for over a bare
        // poly kernel, Arity = None: pack width unknown until <@>) has no
        // standalone runtime value and cannot compose (>>@ needs a
        // concrete loop) -- every <@> use rebuilds it inline (inferApply).
        // Emit the binding INERT so it never reaches IR validation
        // carrying the unresolved element type; the env still holds the
        // real former (TypedValue) for <@> resolution. Concrete-arity
        // formers (Arity = Some n) stay real bindings even with a
        // generic element type: codegen's compose path chases their
        // IRVar back to the real IRObjectFor, so neutralizing them would
        // break `(o1 >>@ o2) <@> A`.
        let isDeferredFormer =
            match env.Subst.Resolve tValue.Type with
            | IRTLoop { Kind = LKObject; Arity = None } -> true
            | _ -> false
        let tb : TypedBinding = {
            Name = name; VarId = varId
            Type = (if isDeferredFormer then IRTUnit else env.Subst.Resolve(tValue.Type))
            Identity = identity; IsMutable = (assign <> ReadOnly); IsLetMut = (assign = MutPassable)
            Value = (if isDeferredFormer then mkTyped (TExprLit LitUnit) IRTUnit else tValue)
            SubBindings = subBindings |> List.map (fun (n, id, ty) -> (n, id, env.Subst.Resolve ty))
            Destructure = destructure
            PostChecks = postChecks
            ErasedType = (if isDeferredFormer then Some (env.Subst.Resolve tValue.Type) else None)
        }
        (TDeclLet tb, env')))))

and checkStaticDecl (env: TypeEnv) (binding: Binding) : TypeResult<TypedDecl * TypeEnv> =
    // Shared annotation handler so `let static` bindings enforce type
    // annotations like regular lets (`let static x: Float<meters> =
    // 100.0` would otherwise carry the synthesized type, dropping the
    // annotation silently).
    //
    // RESOLVED-VALUE SHORTCUT: when StaticEval already reduced this
    // binding to a value (env.StaticValues), type from that VALUE
    // rather than re-inferring the expression -- static-only builtins
    // (sh_spec, total_dim, tp_weight_dim, length, ...) have no runtime
    // binding, so inferring their call expressions would fail with
    // "unbound variable" even though the static evaluator handled them.
    let staticShortcut =
        match binding.Pattern.Kind with
        // A LITERAL-ONLY value is typed by the literal rules like any let
        // (round 6: annotation or default -- the same answer StaticEval's
        // width gives), so its literals are in the typed tree.
        | PatternKind.PatVar _ when StaticEval.isLitOnly binding.Value -> None
        | PatternKind.PatVar n ->
            match Map.tryFind n env.StaticValues with
            | Some sv ->
                // A Float32 / Int32 static is typed at its folded width
                // (round 6: the literal rules, annotation or default).
                let width =
                    match staticWidths.TryGetValue n with
                    | true, w -> Some w
                    | _ -> None
                let rec svToTyped (v: StaticEval.StaticValue) : TypedExpr =
                    match v with
                    | StaticEval.SVInt32 i -> mkTyped (TExprLit (LitInt (int64 i))) (IRTScalar ETInt32)
                    | StaticEval.SVFloat32 f -> mkTyped (TExprLit (LitFloat (float f))) (IRTScalar ETFloat32)
                    | StaticEval.SVInt i when width = Some ETInt32 -> mkTyped (TExprLit (LitInt i)) (IRTScalar ETInt32)
                    | StaticEval.SVFloat f when width = Some ETFloat32 -> mkTyped (TExprLit (LitFloat f)) (IRTScalar ETFloat32)
                    | StaticEval.SVInt i -> mkTyped (TExprLit (LitInt i)) (IRTScalar ETInt64)
                    | StaticEval.SVFloat f -> mkTyped (TExprLit (LitFloat f)) (IRTScalar ETFloat64)
                    | StaticEval.SVBool b -> mkTyped (TExprLit (LitBool b)) (IRTScalar ETBool)
                    | StaticEval.SVString s -> mkTyped (TExprLit (LitString s)) (IRTScalar ETString)
                    | StaticEval.SVUnit -> mkTyped (TExprLit LitUnit) IRTUnit
                    | StaticEval.SVTuple vs ->
                        let ts = vs |> List.map svToTyped
                        mkTyped (TExprTuple ts) (IRTTuple (ts |> List.map (_.Type)))
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
        // A destructuring binding's own name is synthesized from its leaves,
        // `_(a,b)`: the interpreter's session memo is keyed by binding NAME,
        // and the old placeholder `_` made every top-level destructure in a
        // session share one key -- the second cell's `let (c, d) = ...`
        // adopted the FIRST cell's cached tuple (measured: every later
        // destructure echoed the first one's values). The leaf set is what
        // identifies the binding to the session (a rebind of the same
        // leaves supersedes by name exactly as a plain `let` does). `(`
        // cannot occur in a user name, so `_(` is the recognizer codegen
        // and the interpreter use to give the parent its `__tup_N` C++ name.
        let name =
            match binding.Pattern.Kind with
            | PatternKind.PatVar n -> n
            | _ -> "_(" + String.concat "," (patternNames binding.Pattern) + ")"
    // Reuse the pre-pass varId if pre-registered -- but ONLY for a
    // plain `let static x = ...`. checkModule's pre-pass registers
    // static values with placeholder types so a FORWARD reference
    // resolves (`let static a = b + 1` before `let static b = 2`
    // needs `b` bound at a placeholder varId the real decl adopts),
    // keyed by pattern (PatVar -> real name, else synthetic "_").
    // For a DESTRUCTURING static, reusing that entry is wrong: every
    // destructured static registers under the SAME key "_", so a
    // second one's `lookupVar "_"` would find the FIRST one's varId,
    // the two would share one IRId, and `unify` below would weld
    // their unrelated types together. Fresh id whenever destructured.
        let preRegistered =
            match binding.Pattern.Kind with
            | PatternKind.PatVar _ -> lookupVar name env
            | _ -> None
        let varId =
            match preRegistered with
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

        // Tuple, cons and struct destructuring for `let static (a, b)`
        // / `let static head :: tail` / `let static Point { x, y }`.
        // Mirrors DeclLet's branches. Division of labour with StaticEval:
        // its bindPattern folds PatVar/PatTuple/PatStruct leaves into
        // env.StaticValues as compile-time constants, but has no PatCons
        // case, so cons leaves get no static value and Lowering's
        // TDeclStatic path projects them out of the primary binding
        // instead (subBindingValue) -- which is what DSConsRest tells it.
        let mutable subBindings : (string * IRId * IRType) list = []
        let mutable destructure = DSPositional
        // Same parking spot as DeclLet's: the env-building fold is a pure
        // expression with nowhere to return an error from, so a
        // non-splittable cons pattern is surfaced just before the binding
        // is assembled.
        let mutable patternError : TypeError option = None
        let env'' =
            match binding.Pattern.Kind with
            | PatternKind.PatTuple pats ->
                (match tupleDestructureArityError env pats tValue.Type with
                 | Some e -> patternError <- Some e
                 | None -> ())
                let resolvedTy = env.Subst.Resolve(tValue.Type)
                let typeList =
                    match resolvedTy with
                    | IRTTuple ts ->
                        if pats.Length = ts.Length then ts
                        else
                            let flat = IR.flattenTupleLeaves resolvedTy
                            if pats.Length = flat.Length then flat else ts
                    | _ -> []
                let (entries, slots) = tuplePatternSlots pats
                destructure <- DSTupleAt (entries |> List.map (fun (_, _, s) -> s), slots)
                entries
                |> List.fold (fun e (n, posOpt, _) ->
                    let eTy =
                        match posOpt with
                        | Some i when i < typeList.Length -> env.Subst.Resolve(typeList.[i])
                        | _ -> env.Subst.Fresh()
                    let subId = env.Builder.FreshId()
                    subBindings <- subBindings @ [(n, subId, eTy)]
                    bindVarSimple n subId eTy e) env'
            | PatternKind.PatCons (h, t) ->
                match consDestructureLeaves env tValue.Type h t with
                | Error e ->
                    patternError <- Some e
                    env'
                | Ok leaves ->
                    destructure <- DSConsRest
                    leaves
                    |> List.fold (fun e (n, ty) ->
                        let subId = env.Builder.FreshId()
                        subBindings <- subBindings @ [(n, subId, ty)]
                        bindVarSimple n subId ty e) env'
            | PatternKind.PatStruct (_, fieldPats) ->
                // `let static Point { x, y } = Point { x = 3, y = 4 }`.
                // StaticEval.bindPattern already folds each leaf to a
                // compile-time value (what Lowering's TDeclStatic path
                // prefers); the sub-binding recorded here gives that
                // constant its name, IRId and type. Field types come
                // from structFieldTypesOf (same declared-field list
                // DeclLet reads), matched BY NAME so a missing/extra
                // field can't shift another leaf's projection.
                let fieldTypeMap = Map.ofList (structFieldTypesOf env tValue.Type)
                fieldPats |> List.fold (fun e (fieldName, p) ->
                    match p.Kind with
                    | PatternKind.PatVar n ->
                        let subId = env.Builder.FreshId()
                        let eTy =
                            Map.tryFind fieldName fieldTypeMap
                            |> Option.defaultWith (fun () -> env.Subst.Fresh())
                        subBindings <- subBindings @ [(n, subId, eTy)]
                        bindVarSimple n subId eTy e
                    | _ -> patternNames p |> List.fold (fun e2 n ->
                        let subId = env.Builder.FreshId()
                        let eTy = env.Subst.Fresh()
                        subBindings <- subBindings @ [(n, subId, eTy)]
                        bindVarSimple n subId eTy e2) e) env'
            | _ -> env'

        if patternError.IsSome then Error patternError.Value else
        let tb : TypedBinding = {
            Name = name; VarId = varId; Type = env.Subst.Resolve(tValue.Type)
            Identity = None; IsMutable = false; IsLetMut = false; Value = tValue
            SubBindings = subBindings |> List.map (fun (n, id, ty) -> (n, id, env.Subst.Resolve ty))
            Destructure = destructure
            PostChecks = []; ErasedType = None
        }
        Ok (TDeclStatic tb, env''))

and checkTypeDeclItem (env: TypeEnv) (typeDecl: TypeDecl) : TypeResult<TypedDecl * TypeEnv> =
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
                // The typed declaration carries the IDENTITY (D11): what
                // the back ends emit and every use's type names.
                let ident = declIdentity env' name
                match Map.tryFind ident env'.TypeDefs with
                | Some (TDIIndexType (_, idx, _)) ->
                    Ok (TTDIndexType (ident, idx))
                | Some (TDIEnumIdx (_, idx, values, _)) ->
                    Ok (TTDEnumIdx (ident, idx, values))
                | _ ->
                    Ok (TTDAlias (ident, typeParams, lowerTypeExpr env' body))
            | TyDeclStruct (name, typeParams, fields, _constraints, _isStatic) ->
                // Constraint validation happened in registerTypeDecl;
                // checks materialize per assignment site (PostChecks /
                // TExprConstraintCheck), not on the type def.
                let resolvedFields = fields |> List.map (fun f -> (f.Name, lowerTypeExpr env' f.Type))
                Ok (TTDStruct (declIdentity env' name, typeParams, resolvedFields))
            | TyDeclSum (name, typeParams, variants) ->
                let resolvedVariants = variants |> List.map (fun v ->
                    (declIdentity env' v.Name, v.Data |> Option.map (lowerTypeExpr env')))
                Ok (TTDVariant (declIdentity env' name, typeParams, resolvedVariants))
            | TyDeclMutualGroup (members, _) ->
                // Constraint validation happened in registerTypeDecl; the
                // typed decl just carries the member aliases for lowering,
                // each under its IDENTITY (D11, as TTDAlias above): module
                // A's `P1` is `A.P1`, emitted `A__P1`, so an importer's own
                // group member `P1` cannot redeclare the C++ alias.
                Ok (TTDMutualGroup (members |> List.map (fun (mname, mty) ->
                    (declIdentity env' mname, lowerTypeExpr env' mty))))
        ttdResult |> Result.map (fun ttd -> (TDeclType ttd, env')))

and checkImplDecl (env: TypeEnv) (implDecl: ImplDecl) : TypeResult<TypedDecl * TypeEnv> =
    // Resolve the concrete type name
    let typeName = 
        match implDecl.ForType with
        | TyNamed (name, _) -> Some name
        | _ -> None
    match typeName with
    | Some tName ->
        // Register all mangled method names first (enables mutual recursion within impl block)
        let mutable env' = env
        // The implemented type's IDENTITY (D11): `impl .. for P` in
        // module A is A's `P`; the method keys and the receiver's type use
        // it, the mangled name its source spelling (qualifyModuleNames
        // prefixes a non-main module's functions anyway).
        let tIdent =
            match lookupTypeDef tName env with
            | Some (TDIStruct (i, _, _, _)) | Some (TDIVariant (i, _, _)) -> i
            | _ -> tName
        let methodIds = implDecl.Methods |> List.map (fun method ->
            let mangledName = $"{Blade.Types.TypeOrigins.sourceName tIdent}__{method.Name}"
            let selfType = IRTNamed tIdent
            let paramTypes = method.Params |> List.map (fun p ->
                if p.Name = "self" && p.Type.IsNone then selfType
                else match p.Type with Some t -> lowerTypeExpr env' t | None -> IRTScalar ETFloat64)
            let retType = match method.ReturnType with
                          | Some t -> lowerTypeExpr env' t
                          | None -> env'.Subst.Fresh()
            let funcType = mkFuncArrow paramTypes retType
            let funcVarId = env'.Builder.FreshId()
            env' <- bindVarSimple mangledName funcVarId funcType env'
            env' <- { env' with ImplMethods = Map.add (tIdent, method.Name) (funcVarId, funcType) env'.ImplMethods }
            (mangledName, funcVarId, paramTypes, retType))

        // Validate interface if specified
        match Map.tryFind implDecl.Interface env.Interfaces with
        | Some ifaceDecl ->
            let missing = ifaceDecl.Methods |> List.filter (fun ifaceMethod ->
                not (implDecl.Methods |> List.exists (fun m -> m.Name = ifaceMethod.Name)))
            match missing with
            | _ :: _ ->
                let names = missing |> List.map (_.Name) |> String.concat ", "
                Error (ImplMissingMethods (implDecl.Interface, tName, names))
            | [] -> Ok ()
        | None -> Ok ()
        |> Result.bind (fun () ->
            // Type-check each method body
            let selfType = IRTNamed tIdent
            let mutable typedMethods = []
            let mutable methodErr = None
            for (method, (mangledName, funcVarId, paramTypes, retType)) in List.zip implDecl.Methods methodIds do
                if methodErr.IsNone then
                    let savedScope = env'.Subst.PushTypeVarScope()
                    let mutable bodyEnv = enterCallableBody env'
                    let typedParams = method.Params |> List.mapi (fun i p ->
                        let varId = env'.Builder.FreshId()
                        let ty =
                            if p.Name = "self" && p.Type.IsNone then selfType
                            else paramTypes.[i]
                        bodyEnv <- bindVarSimple p.Name varId ty bodyEnv
                        { Name = p.Name; Type = ty; Index = i; VarId = varId; Default = None; NameSpan = p.NameSpan } : TypedParam)
                    // The body must MEET the declared return: this unify's
                    // result used to be discarded, so `-> Float = "nope"`
                    // type-checked and died in g++.
                    let bodyAgainstRet =
                        inferExpr bodyEnv method.Body |> Result.bind (fun tBody ->
                            match unify env'.Subst tBody.Type retType with
                            | Ok () -> Ok tBody
                            | Error _ ->
                                Error (TypeMismatch (env'.Subst.Resolve retType, env'.Subst.Resolve tBody.Type)))
                    match bodyAgainstRet with
                    | Ok tBody ->
                        let commGroups =
                            extractCommGroups
                                (method.Params |> List.map (fun p -> { Name = p.Name; Type = p.Type; Default = None; NameSpan = p.NameSpan } : LambdaParam))
                                method.WhereClause
                        // N11: an impl method's `where comm/anticomm` gets the
                        // same validation as a top-level declaration's -- the
                        // names must be parameters (BL4001) and the body must
                        // not contradict the claim (BL4013).
                        let antisymGroups =
                            extractAntisymGroups
                                (method.Params |> List.map (fun p -> { Name = p.Name; Type = p.Type; Default = None; NameSpan = p.NameSpan } : LambdaParam))
                                method.WhereClause
                        checkSymmetryVarNames env' (method.Params |> List.map (_.Name))
                                              method.WhereClause $"method '{tName}.{method.Name}'"
                        let symErr =
                            if List.isEmpty commGroups && List.isEmpty antisymGroups then None
                            else
                                let signResolver (calleeId: IRId) =
                                    match env'.FuncSignParities.TryGetValue calleeId with
                                    | true, ps -> Some ps
                                    | _ -> None
                                let deducedPairs = Blade.Deduce.deduceAdjacentPairs signResolver typedParams tBody
                                declSymmetryContradiction env' typedParams deducedPairs commGroups antisymGroups tBody
                        symErr |> Option.iter (fun e -> methodErr <- Some e)
                        let effects = effectsOfBody env' (Some funcVarId) tBody
                        env'.FuncEffects.[funcVarId] <- effects
                        (match outerWritesOfBody env' env' tBody with
                         | [] -> env'.FuncOuterWrites.Remove funcVarId |> ignore
                         | ws -> env'.FuncOuterWrites.[funcVarId] <- ws)
                        (match outerReadsOfBody env' env' tBody with
                         | [] -> env'.FuncOuterReads.Remove funcVarId |> ignore
                         | rs -> env'.FuncOuterReads.[funcVarId] <- rs)
                        let tf : TypedFunctionDecl = {
                            Name = mangledName; SourceName = mangledName; FuncId = funcVarId
                            TypeParams = method.TypeParams
                            Params = typedParams; ReturnType = tBody.Type
                            WhereClause = method.WhereClause; Body = tBody
                            CommGroups = commGroups; IsStatic = false
                            NameSpan = method.NameSpan
                            Effects = effects
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
        // Can't resolve type name -- pass through with empty methods
        let timpl : TypedImplDecl = {
            ForType = implDecl.ForType
            TypeName = "_"
            Methods = []
        }
        Ok (TDeclImpl timpl, env)

and checkImportDecl (env: TypeEnv) (qname: QualifiedName) (style: ImportStyle) : TypeResult<TypedDecl * TypeEnv> =
    let fullName = String.concat "." qname
    // `from M import x` naming nothing M exports used to bind nothing and
    // say nothing, so the program failed later as an unrelated unbound
    // name -- or silently resolved `x` to a same-named later binding.
    let missingSelective =
        match Map.tryFind fullName env.ModuleExports, style with
        | Some exports, ImportSelective names ->
            names |> List.tryFind (fun name ->
                not (Map.containsKey name exports.Variables
                     || Map.containsKey name exports.TypeDefs
                     || Map.containsKey name exports.TypeNames
                     || Map.containsKey name exports.CtorNames
                     || Map.containsKey name exports.StaticFunctions
                     || Map.containsKey name exports.StaticValues
                     || Map.containsKey name exports.Units
                     || Map.containsKey name exports.VariantTags))
            |> Option.map (fun name ->
                let exported =
                    Seq.append (Map.keys exports.Variables) (Map.keys exports.TypeNames)
                    |> Seq.filter (fun k -> not (k.StartsWith "__") && not (k.Contains "."))
                    |> Seq.distinct |> Seq.sort |> Seq.truncate 12
                    |> String.concat ", "
                (name, exported))
        | _ -> None
    match missingSelective with
    | Some (name, exported) -> Error (ImportNameMissing (fullName, name, exported))
    | None ->
    let env' =
        match Map.tryFind fullName env.ModuleExports with
        | Some exports ->
            match style with
            | ImportQualified aliasOpt ->
                let alias = aliasOpt |> Option.defaultValue (List.last qname)
                // Register all exported variables as alias.name
                let mutable e = env
                for kv in exports.Variables do
                    let qualName = $"{alias}.{kv.Key}"
                    e <- bindVar qualName kv.Value e
                // TYPES (D11): every identity the module's API carries,
                // under its identity; the module's own type and
                // constructor names under `alias.name` and -- by the
                // scope rule (bindImportedTypeName) -- bare.
                for kv in exports.TypeDefs do
                    e <- registerTypeDef kv.Key kv.Value e
                for kv in exports.VariantTags do
                    e <- { e with VariantTags = Map.add kv.Key kv.Value e.VariantTags }
                for kv in exports.TypeNames do
                    match Map.tryFind kv.Value e.TypeDefs with
                    | Some info -> e <- registerTypeDef $"{alias}.{kv.Key}" info e
                    | None -> ()
                    e <- bindImportedTypeName kv.Key kv.Value false e
                for kv in exports.CtorNames do
                    match Map.tryFind kv.Value e.VariantTags with
                    | Some tag -> e <- { e with VariantTags = Map.add $"{alias}.{kv.Key}" tag e.VariantTags }
                    | None -> ()
                    e <- bindImportedCtorName kv.Key kv.Value false e
                for kv in exports.Units do
                    e <- { e with Units = Map.add kv.Key kv.Value e.Units }
                // Static functions: register under alias.name. Bare-name
                // references to these would need parser support for
                // dotted names in eta-reduced DepIdx position; for now,
                // qualified-import use sites of static functions only
                // work in expression contexts where dotted names parse.
                for kv in exports.StaticFunctions do
                    let qualName = $"{alias}.{kv.Key}"
                    e <- { e with StaticFunctions = Map.add qualName kv.Value e.StaticFunctions }
                // (Defaults, where-conjuncts and the other callee facts are
                // keyed by binder id: `alias.name` binds the declaration's
                // own id, so nothing needs re-registering.)
                e
            | ImportSelective names ->
                let mutable e = env
                // Every identity the module's API carries (D11), so a
                // named import's fields / constructors resolve.
                for kv in exports.TypeDefs do
                    if Blade.Types.TypeOrigins.isQualified kv.Key then e <- registerTypeDef kv.Key kv.Value e
                for kv in exports.VariantTags do
                    if Blade.Types.TypeOrigins.isQualified kv.Key then
                        e <- { e with VariantTags = Map.add kv.Key kv.Value e.VariantTags }
                for name in names do
                    match Map.tryFind name exports.Variables with
                    | Some vi -> e <- bindVar name vi e
                    | None -> ()
                    match Map.tryFind name exports.TypeNames, Map.tryFind name exports.TypeDefs with
                    | Some ident, _ -> e <- bindImportedTypeName name ident true e
                    | None, Some tdi -> e <- registerTypeDef name tdi e
                    | None, None -> ()
                    // A named constructor binds too (it used to pass the
                    // missing-name check and bind nothing).
                    match Map.tryFind name exports.CtorNames with
                    | Some ident -> e <- bindImportedCtorName name ident true e
                    | None -> ()
                    match Map.tryFind name exports.StaticFunctions with
                    | Some fd ->
                        e <- { e with StaticFunctions = Map.add name fd e.StaticFunctions }
                    | None -> ()
                    // Units, the same way the QUALIFIED arm above imports
                    // them (unit names have no qualified spelling -- an
                    // annotation is `Float<newton>`, never `Float<SI.newton>`
                    // -- so the only difference between the two arms is
                    // ALL of them vs the ones named). Without this,
                    // `from units.SI import newton` silently binds nothing
                    // and `Float<newton>` degrades to no annotation at all.
                    match Map.tryFind name exports.Units with
                    | Some us -> e <- { e with Units = Map.add name us e.Units }
                    | None -> ()
                e
        | None ->
            // Provider or unknown module -- bind alias as opaque so references type-check
            // (actual types resolved during lowering when provider runs)
            match style with
            | ImportQualified aliasOpt ->
                let alias = aliasOpt |> Option.defaultValue (List.last qname)
                let varId = env.Builder.FreshId()
                bindVarSimple alias varId (IRTNamed (fullName)) env
            | ImportSelective _ -> env
    Ok (TDeclImport (qname, style), env')

and checkFunctionDecl (env: TypeEnv) (funcDecl: FunctionDecl) : TypeResult<TypedDecl * TypeEnv> =
    checkFunctionDeclWith Map.empty (fun _ _ -> ()) None None env funcDecl

/// checkFunctionDecl, with some parameters' types FIXED (`fixedParams`,
/// position -> type) -- an ARRAY INSTANCE of an arrow-parameter declaration
/// (TypeEnv.ArrowVariant, checkArrowVariant) -- and `onBound` told the binder
/// id and function type once they exist, before the body is checked (so the
/// instance is reachable from its own recursive calls). `instanceOf` is an
/// array instance's ORIGIN name: the instance checks under its unique internal
/// name (`funcDecl.Name` -- the name-keyed callee tables below must not
/// overwrite the origin's entries), but it is the origin's declaration, so
/// every message names the origin and so does the result's SourceName.
/// `sharing` is a SHARING INSTANCE's partition (relevant parameter position ->
/// class; TypeEnv.SharingDeclSources, checkSharingVariant): each such
/// parameter carries its class's identity in the body, and the instance is
/// recorded as no sharing source of its own. None for every other check.
and checkFunctionDeclWith (fixedParams: Map<int, IRType>) (onBound: IRId -> IRType -> unit)
                          (instanceOf: string option) (sharing: Map<int, int> option)
                          (env: TypeEnv) (funcDecl: FunctionDecl) : TypeResult<TypedDecl * TypeEnv> =
    let shownName = defaultArg instanceOf funcDecl.Name
    // Fresh type variable scope for this function's type annotations.
    let savedScope = env.Subst.PushTypeVarScope()
    // Every inference variable minted from here on is THIS declaration's
    // (see TypeEnv.FuncSigVarRange; the call judgment instantiates them).
    let sigVarLo = env.Subst.NextId
    // (The callee-fact tables are keyed by this declaration's binder id,
    // funcVarId below, so no same-named binding's entry can leak into it.)

    // Pre-scan all parameter + return type annotations to register type variable names.
    let allAnnotations =
        (funcDecl.Params |> List.map (_.Type))
        @ [funcDecl.ReturnType]
    prescanTypeVarNames env allAnnotations

    // An UNANNOTATED parameter is an anonymous type variable -- exactly a
    // `x: T` with a name nobody wrote (formalism 4.3): generic, its type
    // decided per call by the argument (calleeQuantifier instantiates it,
    // IR monomorphization emits one body per instance), never defaulted.
    // So `function inc(x) = x + 1` is `inc(6) = 7` (Int64) and
    // `inc(2.5) = 3.5`; a body demand an instance would truncate (`x + 1.0`
    // at an Int64 argument) is refused at that call, as for `x: T`.
    let paramTypes = funcDecl.Params |> List.map (fun p ->
        match p.Type with
        | Some t -> lowerTypeExpr env t
        | None ->
            let v = env.Subst.Fresh()
            (match v with IRTInfer id -> env.Subst.MarkPolymorphic id | _ -> ())
            v)
    let retType = match funcDecl.ReturnType with
                  | Some t -> lowerTypeExpr env t
                  | None -> env.Subst.Fresh()
    // A FIXED parameter meets its type. The caller's generic variables in it
    // are re-minted as this declaration's own (generic, minted after
    // sigVarLo), so the instance is generic in them exactly as a `T^k`
    // parameter is.
    let fixedInst = System.Collections.Generic.Dictionary<int, IRType>()
    let fixedErr =
        fixedParams |> Map.toList |> List.tryPick (fun (i, t) ->
            if i >= paramTypes.Length then None
            else
                let t = env.Subst.Resolve t
                let mapping =
                    freeInferVars env.Subst t |> Set.toList
                    |> List.filter env.Subst.IsPolymorphicId
                    |> List.map (fun v ->
                        let f = env.Subst.Fresh()
                        (match f with
                         | IRTInfer fid ->
                             env.Subst.CopyArityConstraint(v, fid)
                             env.Subst.CopyRankLowerBound(v, fid)
                             env.Subst.CopyLiteralDefault(v, fid)
                             env.Subst.MarkPolymorphic fid
                         | _ -> ())
                        (v, f))
                    |> Map.ofList
                let inst = substInferVars mapping t
                match unify env.Subst paramTypes.[i] inst with
                | Ok () ->
                    fixedInst.[i] <- inst
                    None
                | Error e -> Some e)
    // The fixed parameter IS the array: a declared array type with an
    // unnamed axis (`m: Float64^1`) unifies with the argument's named one
    // without becoming it, and the instance exists to read the NAMED axis.
    let paramTypes =
        paramTypes |> List.mapi (fun i p ->
            match fixedInst.TryGetValue i with
            | true, inst ->
                (match p with
                 | IRTInfer _ -> p          // bound to the array by the unify
                 | _ -> inst)
            | _ -> p)
    if fixedErr.IsSome then
        env.Subst.PopTypeVarScope(savedScope)
        Error fixedErr.Value
    elif (paramTypes |> List.exists irTypeHasRaggedNoPrior) || irTypeHasRaggedNoPrior retType then
        Error (RaggedIdxNeedsPrior shownName)
    elif (paramTypes |> List.exists irTypeHasBadDistOrder) || irTypeHasBadDistOrder retType then
        Error (DistOrderCompileTime shownName)
    // Parameters are exactly where the wildcard belongs, so only the RETURN
    // type is scanned here (irTypeHasTagWildcard likewise skips a functional
    // parameter's own slots, keeping `f: (Nat<_>) -> Float64` legal).
    elif irTypeHasTagWildcard retType then
        Error (TagWildcardNotParam $"function '{shownName}' return type")
    else
    let badAxis = (paramTypes @ [retType]) |> List.tryPick irTypeUnknownAxisPath
    if badAxis.IsSome then
        Error (UnknownAxisPathErr (unknownAxisPathMessage badAxis.Value))
    else
    let badIrreps = (paramTypes @ [retType]) |> List.tryPick irTypeBadIrrepsDetail
    if badIrreps.IsSome then
        Error (IrrepsIdxSpecFn (shownName, badIrreps.Value))
    else
    let badPgIrreps = (paramTypes @ [retType]) |> List.tryPick irTypeBadPgIrrepsDetail
    if badPgIrreps.IsSome then
        Error (PgIrrepsIdxSpecFn (shownName, badPgIrreps.Value))
    else
    let badTree = (paramTypes @ [retType]) |> List.tryPick irTypeBadTreeDetail
    if badTree.IsSome then
        Error (TreeIdxShapeFn (shownName, badTree.Value))
    else
    // NO tree door here as of P5, where it was deleted. P3/P4 kept it on an ABI
    // argument -- "the shape rides the CALLER's Tag, and the signature does not
    // transport it" -- and P3's own result is what answers that: nothing about a
    // tree is decided at run time. The callee receives an Array<T,1>, and a path
    // read inside the callee folds against the CALLEE'S OWN DECLARED tree type,
    // which is concrete, sits in paramTypes, and is exactly what the fold reads.
    // There is nothing to transport.
    //
    // What replaces the door is narrower and lives post-zonk:
    // TypeCheckValidate's collectAppTreeErrors refuses a tree-slotted ARGUMENT
    // reaching a parameter that does not itself declare a tree slot (an abstract
    // `T^r`, or a plain array of compatible rank), and refuses a same-rank
    // SHAPE mismatch between two tree-slotted sides -- the seam direct
    // application leaves open because it does not unify plain-call args. The
    // BL4021 bad-shape gate above still runs first.
    // Both parameters AND the return type: either one names an array whose
    // storage would have to exist. (Unlike the tag wildcard, which is LEGAL in
    // parameter position, there is no position where a wreath array can be
    // handled -- a caller would have had to allocate it.)
    let wreath = (paramTypes @ [retType]) |> List.tryPick irTypeWreathLevels
    if wreath.IsSome then
        Error (OrbitStorageUnsupported (wreath.Value, $"function '{shownName}'"))
    else
    // Bounded PARAMETERS and RETURNS are unguarded today (synthesizeBoundChecks
    // is let-annotation-only), so a bounded aggregate here is silently dropped
    // rather than mis-lowered. Reject it anyway: the annotation is meaningless
    // in both readings, and a bound that quietly evaporates is worse than one
    // that is refused.
    let badBound =
        (funcDecl.Params |> List.tryPick (fun p ->
            p.Type |> Option.bind (boundedAggregateError env $"parameter '{p.Name}' of function '{shownName}'")))
        |> Option.orElseWith (fun () ->
            funcDecl.ReturnType |> Option.bind (boundedAggregateError env $"the return type of function '{shownName}'"))
    if badBound.IsSome then
        Error badBound.Value
    else
    // A quantity name inside a COMPOUND unit annotation on a param or the
    // return type is terminal (BL3011) -- surface-checked here, since
    // lowering degrades to the bare base rather than erroring.
    let badUnitAnno =
        (funcDecl.Params |> List.tryPick (fun p -> p.Type |> Option.bind (unitAnnoError env)))
        |> Option.orElseWith (fun () ->
            funcDecl.ReturnType |> Option.bind (unitAnnoError env))
    if badUnitAnno.IsSome then
        Error badUnitAnno.Value
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
    // Open-ended while the body is checked (a recursive call instantiates
    // whatever the declaration has minted so far); closed below.
    env.FuncSigVarRange.[funcVarId] <- (sigVarLo, System.Int32.MaxValue)
    onBound funcVarId funcType
    // ...and record the binder as a named function, so a lambda that calls it
    // does not drag it onto its capture list (see DeclaredFuncIds).
    env.DeclaredFuncIds.Add funcVarId |> ignore
    // A re-check of the same binder (the static pre-pass registers the id
    // first) starts from no recorded facts.
    env.MutParamPositions.Remove funcVarId |> ignore
    env.FuncCoIterObligations.Remove funcVarId |> ignore
    env.FuncUnitTransform.Remove funcVarId |> ignore
    env.FuncUnitEqualities.Remove funcVarId |> ignore

    // `x: mut T` params bind MutPassable so the body may assign into them
    // (gradient out-buffers). Array-typed only: the C++ ABI passes the
    // Array<> wrapper by value, which aliases the caller's DATA (shallow
    // pointer copy) -- element writes land in the caller -- but a scalar
    // passed by value would silently drop its writes.
    let mutParamErr =
        funcDecl.Params |> List.tryPick (fun p ->
            if p.Mutability = Mutable then
                let i = funcDecl.Params |> List.findIndex (fun q -> q.Name = p.Name)
                match env.Subst.Resolve paramTypes.[i] with
                | ArrayElem _ -> None
                | _ -> Some (MutParamNotArray (shownName, p.Name))
            else None)
    match mutParamErr with
    | Some e ->
        env.Subst.PopTypeVarScope(savedScope)
        Error e
    | None ->

    // Mutual-group scans. Member types are forbidden in parameter positions
    // (alias transparency would silently erase the constraint); a declared
    // return tuple naming a full group makes this function the group's
    // introduce-site -- checks emit at the return, annotated callers exempt.
    let mutualParamErr =
        funcDecl.Params |> List.tryPick (fun p ->
            match p.Type with
            | Some t ->
                match mutualMemberNamesIn env t with
                | [] -> None
                | n :: _ -> Some (MutualParamMemberType (shownName, p.Name, n))
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
     | Some g -> env.MutualReturnFuncs.[funcVarId] <- g.GroupId
     | None -> env.MutualReturnFuncs.Remove funcVarId |> ignore)

    // Custom where-clause conjuncts (`where <name>(<args>)` for names the
    // grammar doesn't own): dispatch each through the Blade.Constraints
    // registry. Validate at the signature; record the function for
    // call-site discharge; the license scope opens around body checking
    // below. An unregistered name errors with the registered vocabulary.
    let paramNames = funcDecl.Params |> List.map (_.Name)
    let customConjuncts =
        funcDecl.WhereClause |> Option.map (_.Custom) |> Option.defaultValue []
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
                    Some (PplConstraintNeedsImport (shownName, bare))
                else
                let known =
                    Blade.Constraints.registeredConstraintNames ()
                    |> List.map (fun n ->
                        if n.StartsWith "__ppl_" then "ppl." + n.Substring 6
                        elif n.StartsWith "__ml_" then "ml." + n.Substring 5
                        else n)
                let vocab = if known.IsEmpty then "none registered" else String.concat ", " known
                Some (UnknownWhereConstraint (shownName, cname, vocab))
            | Some h ->
                match h.Validate shownName paramNames cargs with
                | Ok () -> None
                | Error msg -> Some (ConstraintErr msg))
    match conjunctErr with
    | Some e ->
        env.Subst.PopTypeVarScope(savedScope)
        Error e
    | None ->
    if not customConjuncts.IsEmpty then
        env.FuncConstraints.[funcVarId] <- (paramNames, customConjuncts)

    let genericObligations = ResizeArray<GenericObligation>()
    env.FuncGenericObligations.Remove funcVarId |> ignore
    let mutable bodyEnv = enterCallableBody envWithFunc
    bodyEnv <- { bodyEnv with CurrentGenericObligations = Some genericObligations
                              CurrentSignature = paramTypes @ [retType] }
    if funcDecl.WhereClause |> Option.map (fun w -> not w.Parallel.IsEmpty) |> Option.defaultValue false then
        bodyEnv <- { bodyEnv with InParallelBody = true }
    let typedParams = funcDecl.Params |> List.mapi (fun i p ->
        let varId = env.Builder.FreshId()
        let assign = match p.Mutability with
                     | Mutable -> MutPassable
                     | _ -> ReadOnly
        bodyEnv <- bindVarFull p.Name varId paramTypes.[i] None assign None bodyEnv
        // Dist-typed parameters carry their license token as provenance --
        // the `where indep` handler licenses exactly these tokens for the
        // body, and call-site discharge maps them back to actuals.
        (match env.Subst.Resolve paramTypes.[i] with
         | IRTDist _ ->
             env.Provenance.[varId] <- Set.singleton (Blade.Constraints.paramProvenanceToken (declIdentity env funcDecl.Name) p.Name)
         | _ -> ())
        { Name = p.Name; Type = paramTypes.[i]; Index = i; VarId = varId; Default = None; NameSpan = p.NameSpan } : TypedParam)
    // A SHARING INSTANCE (checkSharingVariant): each relevant parameter is
    // its partition class's array for every identity question of the body
    // (TypeCheckSupport.loopOperandIdentity), spelled as written for records.
    (match sharing with
     | Some cls ->
         for tp in typedParams do
             match Map.tryFind tp.Index cls with
             | Some c -> env.SharingParamIdentities.[tp.VarId] <- AIDAlias (AIDParameter (funcDecl.Name, c), tp.Name)
             | None -> ()
     | None -> ())

    // Which of those params a whole-array rebind must be refused on. Every
    // surviving `mut` param is array-typed (MutParamNotArray rejected the rest
    // above), and `let mut` LOCALS must keep their rebind semantics, so the
    // set is exactly the declared `mut` parameters' BINDER IDS (#51: by name,
    // a block-local `let mut a` shadowing a `mut` parameter `a` was refused a
    // whole-array rebind). See TypeEnv.MutArrayParams / assignTargetError.
    bodyEnv <- { bodyEnv with
                   MutArrayParams =
                       List.zip funcDecl.Params typedParams
                       |> List.filter (fun (p, _) -> p.Mutability = Mutable)
                       |> List.map (fun (_, tp) -> tp.VarId)
                       |> Set.ofList }

    // ---- Parameter defaults (BL3012 rules + declaration-time typing) ----
    // Trailing rule; required-params-only scope rule; then each default
    // type-checked against its param's declared (lowered) type with the
    // params in scope, so a bad default errors HERE even if never called.
    // A quantity-typed slot's default is an INTRODUCTION position: checkExpr
    // against the annotated type is the permissive direction (the call-site
    // desugar re-ascribes the surface default, minting the nominal). Call
    // sites re-type the SURFACE exprs, so the typed results are discarded.
    let defaultsErr =
        match funcDecl.Params |> List.tryFindIndex (_.Default.IsSome) with
        | None -> None
        | Some fd ->
            let firstDefaultedName = (List.item fd funcDecl.Params).Name
            let orderErr =
                funcDecl.Params
                |> List.mapi (fun i p -> (i, p))
                |> List.tryPick (fun (i, p) ->
                    if i > fd && p.Default.IsNone
                    then Some (DefaultParamOrder ($"function '{shownName}'", p.Name, firstDefaultedName))
                    else None)
            match orderErr with
            | Some e -> Some e
            | None ->
                let defaultedNames =
                    funcDecl.Params |> List.filter (_.Default.IsSome)
                    |> List.map (_.Name) |> Set.ofList
                let scopeErr =
                    funcDecl.Params |> List.tryPick (fun p ->
                        match p.Default with
                        | Some d ->
                            let bad = Set.intersect (collectFreeVars Set.empty d) defaultedNames
                            if Set.isEmpty bad then None
                            else Some (DefaultParamScope ($"function '{shownName}'", p.Name, Set.minElement bad))
                        | None -> None)
                match scopeErr with
                | Some e -> Some e
                | None ->
                // FACTORY rule (BL3013): quantity-typed defaulted slots must
                // carry DISTINCT quantities -- by-nominal call-site routing
                // (`f(x, 3 : levels)`) needs each nominal to name exactly one
                // slot. Judged on the LOWERED param types, so `Float<speed>`
                // and a bare `speed` annotation agree.
                let dupQuantityErr =
                    let slots =
                        funcDecl.Params
                        |> List.mapi (fun i p -> (i, p))
                        |> List.choose (fun (i, p) ->
                            match p.Default with
                            | Some _ ->
                                (match IR.getUnits (env.Subst.Resolve paramTypes.[i]) with
                                 | Some u -> u.Nominal |> Option.map (fun q -> (q, p.Name))
                                 | None -> None)
                            | None -> None)
                    slots
                    |> List.groupBy fst
                    |> List.tryPick (fun (q, members) ->
                        match members with
                        | (_, p1) :: (_, p2) :: _ ->
                            Some (FactoryDupQuantityDecl ($"function '{shownName}'", q, p1, p2))
                        | _ -> None)
                match dupQuantityErr with
                | Some e -> Some e
                | None ->
                    funcDecl.Params
                    |> List.mapi (fun i p -> (i, p))
                    |> List.tryPick (fun (i, p) ->
                        match p.Default with
                        | Some d ->
                            (match checkExpr bodyEnv paramTypes.[i] d with
                             | Ok _ -> None
                             | Error e -> Some e)
                        | None -> None)
    match defaultsErr with
    | Some e ->
        env.Subst.PopTypeVarScope(savedScope)
        Error e
    | None ->
    // Register the surface param list (name, annotation, default) so call
    // sites can fill omitted trailing args -- BEFORE the body is checked,
    // so recursive calls inside the body may omit them too.
    if funcDecl.Params |> List.exists (_.Default.IsSome) then
        env.FuncDefaults.[funcVarId] <- (funcDecl.Params |> List.map (fun p -> (p.Name, p.Type, p.Default)))
        // Binding identity of every free name the defaults read HERE, so the
        // call-site splice can tell the declaration-site `k` from a caller's
        // parameter of the same spelling (TypeEnv.FuncDefaultCaptures). Own
        // parameters are excluded (they are the required args, bound at the
        // splice); a name with no binding here (a function registered
        // elsewhere, an intrinsic) records nothing and is not checked.
        env.FuncDefaultCaptures.[funcVarId] <-
            defaultCaptureIdentities env (funcDecl.Params |> List.map (fun p -> (p.Name, p.Default)))

    // Register which parameter positions are `mut`, for the call-site write
    // -permission check (dispatchAppOrIndex's FuncElem arm). Registered here,
    // alongside FuncDefaults and BEFORE the body is checked, so a recursive
    // call inside the body is held to it too -- which is what makes the
    // mut-forwarding chain check itself rather than being assumed.
    let mutPositions =
        funcDecl.Params |> List.mapi (fun i p -> (i, p))
        |> List.filter (fun (_, p) -> p.Mutability = Mutable)
        |> List.map fst
    if not mutPositions.IsEmpty then
        env.MutParamPositions.[funcVarId] <- mutPositions

    // Open the license scope for the body; closed after `result` is
    // computed (both success and error paths flow past the exit below).
    for (cname, cargs) in customConjuncts do
        Blade.Constraints.lookupConstraint cname |> Option.iter (fun h -> h.EnterBody (declIdentity env funcDecl.Name) cargs)

    let result  = checkFunctionDeclBody env
                      { FunctionDeclSig.RetType = retType; FunctionDeclSig.FuncVarId = funcVarId
                        FunctionDeclSig.ParamTypes = paramTypes; FunctionDeclSig.TypedParams = typedParams
                        FunctionDeclSig.FixedParams = fixedParams; FunctionDeclSig.Sharing = sharing
                        FunctionDeclSig.MutualReturnGroup = mutualReturnGroup
                        FunctionDeclSig.CustomConjuncts = customConjuncts; FunctionDeclSig.SigVarLo = sigVarLo
                        FunctionDeclSig.ShownName = shownName }
                      funcDecl bodyEnv genericObligations envWithFunc

    // Close the license scope (error paths included -- `result` has
    // materialized either way by this point).
    for (cname, cargs) in customConjuncts do
        Blade.Constraints.lookupConstraint cname |> Option.iter (fun h -> h.ExitBody (declIdentity env funcDecl.Name) cargs)

    env.Subst.PopTypeVarScope(savedScope)
    result

and checkFunctionDeclBody (env: TypeEnv) (declSig: FunctionDeclSig) (funcDecl: FunctionDecl) (bodyEnv: TypeEnv) (genericObligations: ResizeArray<GenericObligation>) (envWithFunc: TypeEnv)  =
    let { FunctionDeclSig.RetType = retType; FunctionDeclSig.FuncVarId = funcVarId
          FunctionDeclSig.ParamTypes = paramTypes; FunctionDeclSig.TypedParams = typedParams
          FunctionDeclSig.FixedParams = fixedParams; FunctionDeclSig.Sharing = sharing
          FunctionDeclSig.MutualReturnGroup = mutualReturnGroup
          FunctionDeclSig.CustomConjuncts = customConjuncts; FunctionDeclSig.SigVarLo = sigVarLo
          FunctionDeclSig.ShownName = shownName } = declSig
    // `repro` is a runtime-emission property; a static function evaluates
    // at compile time in the compiler's own arithmetic, where there is no
    // emitted body for the attribute to govern. Refuse rather than let the
    // clause silently mean nothing.
    if funcDecl.IsStatic
       && (funcDecl.WhereClause |> Option.map (_.Repro) |> Option.defaultValue false) then
        Error (WhereClauseErr $"function '{shownName}': `where repro` cannot apply to a `static function` -- a static function evaluates at compile time (in the compiler's own arithmetic), so there is no emitted body for the reproducibility attribute to govern")
    else
    // When a return type is annotated, drive the body bidirectionally
    // via checkExpr. This pushes the expected type into literal and
    // tuple-constructor positions so that e.g. `(4, 1)` retypes its
    // elements against `(StationIdx, Idx<3>)` (giving the literals
    // their named-index types directly) rather than synthesizing
    // `(Int64, Int64)` and then failing to unify against the named
    // tuple. Without an annotation, fall back to plain inference --
    // there's no expectation to push.
    let bodyResult =
        match funcDecl.ReturnType with
        // A declared DENSE array return fixes the body's elaboration too
        // (plan 2.3; checkUnderDeclaredLayout).
        | Some _ -> checkUnderDeclaredLayout bodyEnv retType funcDecl.Body
        | None -> inferExpr bodyEnv funcDecl.Body
    bodyResult |> Result.bind (fun tBody ->
        // A function body is a value-forming boundary: a wildcard `_` that
        // survives into it has escaped its only legitimate role (a compound-
        // index coordinate). Reject cleanly here rather than at lowering.
        if exprContainsWildcard tBody then
            Error (WildcardErr
                "wildcard `_` is not a value: it cannot be a function's returned value. It is only meaningful as a compound-index coordinate (e.g. B((a, _, c))).")
        else
        // Belt-and-suspenders: even after checkExpr, run unify on the
        // synthesized body type vs the annotation. checkExpr's fall-
        // through case (line ~3082) is `inferExpr + unify` already,
        // and the special-cased shapes (literals, tuples) build a
        // TypedExpr whose .Type matches the expected type by
        // construction -- so this unify is mostly a no-op when
        // checkExpr was used. When the bodyResult came from inferExpr
        // (no annotation), retType is itself a fresh inference var
        // and the unify just binds it. Either way we propagate the
        // result so genuine mismatches surface here rather than
        // exploding at codegen.
        //
        // An UNANNOTATED parameter the body uses as a SUBSCRIPT is an
        // index: see pinSubscriptParams (formalism 3.10).
        //
        // ARROW PARAMETERS (TypeEnv.ArrowObligation): applications of a
        // parameter the body left open become its per-call obligations;
        // every other application whose head is known by now is typed.
        // (First, R10: an open-argument link whose argument is this
        // declaration's own generic variable -- a kernel cell over a
        // generic array parameter -- moves the parameter's deferred
        // arithmetic to that variable, judged per instance.)
        moveOpenArgDemandsToGenerics env genericObligations
        settleDeclArrowApps env funcVarId paramTypes |> Result.bind (fun () ->
        // ...and its ARRAY INSTANCES are checked from this source, in
        // this environment (checkArrowVariant); an instance's own entry
        // is re-pointed at its origin there. So are those of a
        // declaration reading an UNNAMED axis of an array parameter at a
        // computed position (noteAxisSubscriptParams): a call passing a
        // NAMED axis there is the call of such an instance, whose read is
        // guarded or proven against the name.
        if funcDecl.IsStatic then env.FuncAxisSubscriptParams.Remove funcVarId |> ignore
        else noteAxisSubscriptParams env funcVarId typedParams tBody
        // A parameter declared at a SYMBOLIC rank (`x: T^k`) that the body
        // gave a rank (TypeEnv.FuncRankGenericParams): a call at another
        // rank is the call of an instance checked at the argument's type.
        env.FuncRankGenericParams.Remove funcVarId |> ignore
        // (Not with a parameter PACK: its one declared index spans several
        // call arguments, so the index would read the wrong argument.)
        (if not funcDecl.IsStatic
            && not (typedParams |> List.exists (fun tp -> (env.Subst.Resolve tp.Type).IsIRTPoly)) then
            let symbolicRank (t: TypeExpr option) =
                match t with
                | Some (TyAbstractArray (_, rankE, _)) ->
                    (match rankE.Kind with ExprKind.ExprLit (LitInt _) -> false | _ -> true)
                | _ -> false
            let decided =
                List.zip funcDecl.Params typedParams
                |> List.choose (fun (p, tp) ->
                    if symbolicRank p.Type && not (fixedParams.ContainsKey tp.Index) then
                        match IR.stripUnits (env.Subst.Resolve tp.Type) with
                        | ArrayElem at -> Some (tp.Index, at.IndexTypes.Length)
                        | _ -> None
                    else None)
            if not decided.IsEmpty then env.FuncRankGenericParams.[funcVarId] <- Map.ofList decided)
        env.ArrowDeclSources.Remove funcVarId |> ignore
        if env.FuncArrowObligations.ContainsKey funcVarId
           || env.FuncAxisSubscriptParams.ContainsKey funcVarId
           || env.FuncRankGenericParams.ContainsKey funcVarId then
            env.ArrowDeclSources.[funcVarId] <- (funcDecl, envWithFunc, fixedParams, funcVarId)
        // IDENTITY SENSITIVITY (plan 2.2): which parameters' sharing a
        // call's partition decides, and whether this declaration may be
        // specialized on it. A sharing instance is no source of its own.
        if sharing.IsNone then
            noteSharingRelevance env envWithFunc funcVarId funcDecl typedParams retType tBody
        pinSubscriptParams env (funcDecl.Params |> List.map (fun p -> p.Type.IsNone)) typedParams tBody
        unify env.Subst tBody.Type retType |> Result.bind (fun () ->
        // The wreath gate again, on the RESOLVED return type. The one at
        // the signature above sees only DECLARED annotations; an
        // unannotated function whose body deduces a wreath class (a comm
        // tie over a repeated compact parameter) gets its return type from
        // this unify, so the declared-side check never saw it. Returning a
        // wreath is not supported: the C++ ABI hands arrays back as an
        // `Array<T,N>` wrapper, and a wreath pool is a flat `T*` with no
        // extents -- the callee would return a wrapper describing a
        // skeleton that does not exist. Produce the class at the use site
        // instead (the corpus's deduced cases all do).
        match irTypeWreathLevels (env.Subst.Resolve retType) with
        | Some levels ->
            Error (OrbitStorageUnsupported
                     (levels, $"function '{shownName}' returns a deduced wreath class"))
        | None ->
        // Declared-return introduce-site: wrap the body so the joint
        // check fires at the return (the single verification point).
        let wrappedBodyR =
            match mutualReturnGroup, funcDecl.ReturnType with
            | Some group, Some retAnnot -> wrapMutualReturnBody envWithFunc retAnnot group tBody
            | _ -> Ok tBody
        wrappedBodyR |> Result.bind (fun tBody ->
        // #17: a parallel function body may not reach an outer write
        // through a call (the direct, lexical write is assignTargetError's).
        match (if bodyEnv.InParallelBody then parallelCalleeWriteError env (typedParams |> List.map (_.VarId)) tBody else None) with
        | Some e -> Error e
        | None ->
        let commGroups =
            extractCommGroups
                (funcDecl.Params |> List.map (fun p -> { Name = p.Name; Type = p.Type; Default = None; NameSpan = p.NameSpan } : LambdaParam))
                funcDecl.WhereClause
        // Register the function's comm groups so a later kernel-use site
        // (etaExpandFunctionKernel / deferred-former eta) can surface them
        // onto the synthesized wrapper lambda -- otherwise `where comm` on a
        // named function is dropped and the loop emits dense storage.
        if not (List.isEmpty commGroups) then
            env.FuncCommGroups.[funcVarId] <- commGroups
        // Same for `where anticomm(...)` -- its own side-channel, since a
        // where-clause attribute that no surfacing site re-attaches is
        // dropped by the kernel-position eta wrapper (silently falling
        // back to dense storage).
        let antisymGroups =
            extractAntisymGroups
                (funcDecl.Params |> List.map (fun p -> { Name = p.Name; Type = p.Type; Default = None; NameSpan = p.NameSpan } : LambdaParam))
                funcDecl.WhereClause
        if not (List.isEmpty antisymGroups) then
            env.FuncAntisymGroups.[funcVarId] <- antisymGroups
        // Which parameter positions this body CO-ITERATES. Registered for
        // the same reason as the tables above -- the callee's BODY is
        // invisible at the call site -- but for a soundness obligation
        // rather than a codegen license: a zip over two `T^1` parameters
        // has no extents to compare in the body, so the agreement it
        // needs can only be checked where the arguments are concrete.
        // Consumed by the call-site ladder's coIterClash.
        let coIterObs =
            coIterObligations env (typedParams |> List.map (_.VarId)) tBody
        if not (List.isEmpty coIterObs) then
            env.FuncCoIterObligations.[funcVarId] <- coIterObs
        // Register the function's parallel strategies for the same reason,
        // paired with its param NAMES: an `omp(a: n)` var is resolved by
        // name against the callable's params (Lowering.extractParallelism),
        // and the eta wrapper renames them, so the surfacing site needs the
        // originals to remap by position.
        (match funcDecl.WhereClause with
         | Some wc when not (List.isEmpty wc.Parallel) ->
             env.FuncParallel.[funcVarId] <-
                 (funcDecl.Params |> List.map (_.Name), wc.Parallel)
         | _ -> ())
        // Fold-kernel builtin-body bit, for the parallel-fold reorder
        // licence (checkFoldOmpLicense). Recorded here for the same reason
        // as the tables above: a named function's BODY is invisible at the
        // `reduce(xs, f)` seam, which sees only a `TExprVar`.
        if isBuiltinFoldBodySurface (funcDecl.Params |> List.map (_.Name))
                                    funcDecl.Body then
            env.FuncFoldBuiltin.[funcVarId] <- true
        // An `omp(v: n)` naming no parameter is silently dropped downstream.
        checkOmpVarNames env (funcDecl.Params |> List.map (_.Name))
                         funcDecl.WhereClause $"function '{shownName}'"
        checkSymmetryVarNames env (funcDecl.Params |> List.map (_.Name))
                              funcDecl.WhereClause $"function '{shownName}'"
        // ... and an `omp(p: n)` read as a licence for a loop this body
        // builds over `p` itself licenses nothing (the clause is about the
        // EXTERNAL S-dims `p` contributes to a CALLER's nest).
        checkOmpInternalLoop env (funcDecl.Params |> List.map (_.Name))
                             funcDecl.WhereClause $"function '{shownName}'"
                             tBody
        // Stage 3 (symmetry deduction, early tier): summarize the
        // adjacent-pair swap parity of this fixed-arity function's body
        // and record it for kernel-position uses -- buildApplyInfo
        // consults the summary when the kernel is an eta-expanded
        // wrapper around this function. Poly packs are late-tier work
        // (their pairs only exist per materialized arity): skipped.
        let polyParams =
            typedParams |> List.filter (fun p -> (env.Subst.Resolve p.Type).IsIRTPoly)
        // Interprocedural SIGN-LINEARITY summary, recorded BEFORE the pair
        // deduction of this same body so a helper is available to its
        // callers (decl order) -- `mymean(row) = reduce(row,(+))/extents(row)`
        // is odd in `row`, which is what lets a caller's `mymean(x - y)`
        // deduce PNeg instead of PBottom. Fixed-arity (non-Poly) only: a
        // pack has no per-position summary to index by argument. Keyed by
        // funcVarId -- the id every other body's reference to this function
        // resolves to -- so a shadowing parameter cannot borrow the law.
        let signResolver (calleeId: IRId) =
            match env.FuncSignParities.TryGetValue calleeId with
            | true, ps -> Some ps
            | _ -> None
        if List.isEmpty polyParams && not (List.isEmpty typedParams) then
            env.FuncSignParities.[funcVarId] <-
                Blade.Deduce.deduceSignParities signResolver typedParams tBody
        let deducedPairs =
            if not (List.isEmpty polyParams) then []
            else Blade.Deduce.deduceAdjacentPairs signResolver typedParams tBody
        if not (List.isEmpty deducedPairs) then
            env.FuncDeducedPairs.[funcVarId] <-
                (typedParams |> List.map (_.Name), deducedPairs)
            IdeDeductions.addPairs (declIdentity env funcDecl.Name)
                (typedParams |> List.map (_.Name)) deducedPairs
        // Stage-3 LATE TIER: pack symmetry for arity-polymorphic kernels.
        // The forall -arity AC-fold template first (the head::tail recursion
        // itself -- packprod, comoment_prod); failing that, the
        // compositional wrapper walk (comoment = mean(prod(a))), which
        // resolves callees through PackDeducedComm in decl order.
        // PInv-or-PBottom only: packs never claim PNeg, so this table
        // fuels suggestions and can produce no false errors.
        (match polyParams with
         | [packP] ->
             let packSummary =
                 match Blade.Deduce.deducePackFold funcDecl.Name packP.Name packP.VarId tBody with
                 | Blade.Deduce.PInv -> Blade.Deduce.PInv
                 | _ ->
                     let resolver (fid: IRId) =
                         match env.PackDeducedComm.TryGetValue fid with
                         | true, (_, p) -> Some p
                         | _ -> None
                     Blade.Deduce.packParityOf resolver packP.VarId tBody
             if packSummary = Blade.Deduce.PInv then
                 env.PackDeducedComm.[funcVarId] <- (packP.Name, packSummary)
                 Blade.TypeEnv.DeducedFacts.add
                     (Blade.TypeEnv.DeducedPackComm (funcDecl.Name, packP.Name)) tBody.Span
                 IdeDeductions.addPack (declIdentity env funcDecl.Name) packP.Name packSummary
         | _ -> ())
        // RECORDING ONLY (channel (f)) -- everything below this point in the
        // decl's deduction is unchanged; these two loops only WRITE to the
        // IDE side-channel. Ranks are read here rather than after the close
        // 60 lines down because the close resolves the var: once `unify`
        // has run, `Resolve pt` is an array and the bound is invisible.
        // FunctionDecl has no Span field and no decl span is in scope, so
        // tBody.Span is the tightest honest anchor.
        let pairDeclared i =
            (commGroups @ antisymGroups)
            |> List.exists (fun g -> List.contains i g && List.contains (i + 1) g)
        List.indexed deducedPairs
        |> List.iter (fun (i, par) ->
            if (par = Blade.Deduce.PInv || par = Blade.Deduce.PNeg)
               && i + 1 < typedParams.Length
               && not (pairDeclared i) then
                Blade.TypeEnv.DeducedFacts.add
                    (Blade.TypeEnv.DeducedPairSym
                        (funcDecl.Name, typedParams.[i].Name, typedParams.[i + 1].Name, i,
                         par = Blade.Deduce.PNeg))
                    tBody.Span)
        // Ranks the body FORCED on unannotated params -- read off the very
        // same bounds the decl-close block below consumes (same Resolve,
        // same arity guard, same GetRankLowerBound), so what the editor
        // reports and what the checker closes agree by construction.
        paramTypes |> List.iteri (fun i pt ->
            match env.Subst.Resolve pt with
            | IRTInfer id when (env.Subst.GetArityConstraint id).IsNone ->
                (match env.Subst.GetRankLowerBound(id) with
                 | Some k when k > 0 ->
                     Blade.TypeEnv.DeducedFacts.add
                         (Blade.TypeEnv.DeducedRank
                             (funcDecl.Name, funcDecl.Params.[i].Name, i, k))
                         tBody.Span
                 | _ -> ())
            | _ -> ())
        // Declared comm on a named function whose body is PROVABLY
        // antisymmetric in a declared adjacent pair is the
        // silent-corruption case section 4 exists for. A named function cannot
        // be a reynolds kernel (reynolds requires a lambda), so no
        // iteration license can apply here -- hard error. PBottom stays
        // trusted (the escape hatch when the analysis is too weak).
        // The declared-anticomm twin is the same check with the parities
        // exchanged: a body provably INVARIANT under a pair declared
        // antisymmetric would be stored on a strict simplex that has no
        // diagonal and negates half its reads.
        let bodyElemIsComplex =
            let rec complexElem (t: IRType) =
                match IR.stripUnits (env.Subst.Resolve t) with
                | IRTScalar (ETComplex64 | ETComplex128) -> true
                | ArrayElem arr -> complexElem arr.ElemType
                | _ -> false
            complexElem tBody.Type
        let commContradiction =
            declSymmetryContradiction env typedParams deducedPairs commGroups antisymGroups tBody
            // The pack twin (N2): a pack is ONE declared parameter, so the
            // pairs above never reach inside it. Its pairs exist per arity:
            // unroll the body at small arities (Deduce.witnessPackLawFailure)
            // and witness the expanded group's pairs. Skipped when the
            // all-arity template already PROVED the pack invariant (and for
            // anticomm, where packs never deduce PNeg, the same evaluator
            // also checks the diagonal).
            |> Option.orElseWith (fun () ->
                match polyParams with
                | [packP] when not bodyElemIsComplex ->
                    let packIndex = typedParams |> List.findIndex (fun p -> p.VarId = packP.VarId)
                    let elemTy =
                        match env.Subst.Resolve packP.Type with
                        | IRTPoly (bt, _) -> env.Subst.Resolve bt
                        | other -> other
                    let isIntTy (t: IRType) =
                        match IR.stripUnits t with
                        | IRTScalar (ETInt32 | ETInt64) -> true
                        | IRTIdxTagged (IRTScalar (ETInt32 | ETInt64), _) -> true
                        | _ -> false
                    let provedInv =
                        Blade.Deduce.deducePackFold funcDecl.Name packP.Name packP.VarId tBody = Blade.Deduce.PInv
                    let packWitness (groups: int list list) (negate: bool)
                                    (mk: string * string * string -> TypeError) =
                        Blade.Deduce.witnessPackLawFailure (witnessInlineFn env) funcDecl.Name packP.Name packP.VarId packIndex
                            (typedParams |> List.map (_.VarId)) elemTy groups negate (isIntTy elemTy) tBody
                        |> Option.map (fun (k, p, q, expanded, w) ->
                            let nameAt (pos: int) =
                                if pos >= packIndex && pos < packIndex + k then $"{packP.Name}[{pos - packIndex}]"
                                else
                                    let decl = if pos < packIndex then pos else pos - k + 1
                                    typedParams.[decl].Name
                            let (a, b) = (nameAt p, nameAt q)
                            let named =
                                w.Fixed |> List.choose (fun (id, c) ->
                                    expanded |> List.tryFindIndex (fun x -> x = id)
                                    |> Option.map (fun pos -> (nameAt pos, c)))
                            let text = $"applied to {k} operands, " + swapWitnessText a b named negate w
                            match w.Failure with
                            | Blade.Deduce.DiagonalNonzero _ -> AnticommDiagonalNonzero (a, b, text)
                            | Blade.Deduce.SwapLawFails _ -> mk (a, b, text))
                    (if provedInv then None
                     else packWitness commGroups false CommContradictsWitness)
                    |> Option.orElseWith (fun () ->
                        packWitness antisymGroups true AnticommContradictsWitness)
                | _ -> None)
        match commContradiction with
        | Some e -> Error e
        | None ->
        // Close the body-only rank deduction (stage 2): a param whose
        // type is still an unresolved inference var but carries a rank
        // lower bound (accumulated from the body's builtin pins and
        // direct-call demands, max-joined) is pinned to a fresh rank-k
        // array with a free element type -- the minimum rank the body
        // forces IS the cell rank. Params with no bound stay fully
        // generic; params under a `T^k` annotation are skipped (governed
        // by their exact arity constraint). Lives in Zonk.fs so this
        // DECLARED-param site and zonk's auto-close (for lambda params,
        // no decl site) build the same array and cannot drift.
        Blade.Zonk.closeDeducedRanks env.Subst env.Builder funcDecl.Name paramTypes
        let resolvedParams = typedParams |> List.map (fun p ->
            { p with Type = env.Subst.Resolve(p.Type) } : TypedParam)
        let resolvedRet = env.Subst.Resolve(retType)
        // REPRESENTATION-STATUS deduction -- the fourth lattice, beside the
        // parity deduction above.
        //
        // It sits HERE rather than beside deduceSignParities on purpose:
        // deducing from CLOSED types requires the rank deduction to have
        // already closed at `closeDeducedRanks` two lines up. Above this
        // point an unannotated parameter is still an IRTInfer and would
        // classify unclassifiable.
        //
        // `Subst.Resolve`, deliberately NOT `Zonk.zonkType`: zonk DEFAULTS
        // an unsolved variable to Float64, which would present a still-open
        // parameter as a provable invariant SCALAR -- a false shape, and
        // shape is load-bearing in the scaling rule. Resolve leaves it
        // IRTInfer, which classifies unclassifiable and skips the function.
        //
        // PROPOSALS ONLY: this channel is read by the differential harness
        // alone -- no BL4011, no warning, no CertSuggestion. The MLElaborate
        // seam remains the user-facing emitter and the checking authority.
        let repResolve (t: IRType) = env.Subst.Resolve t
        let repParams =
            resolvedParams
            |> List.map (fun p ->
                ({ PName = p.Name; PId = p.VarId; PType = p.Type } : Blade.DeduceRep.RepParam))
        (match customConjuncts |> List.tryFind (fun (n, _) -> n = "__ml_equiv") with
         | Some (_, gargs) ->
             // Pinned or elaborator-stamped: record the declared signature
             // as an axiom for later callers. Conjuncts are read uniformly,
             // so a synthesized function stamped by the ML elaborator lands
             // here by the same path a user's `where ml.equiv(O3)` does.
             let declaredGroup = (match gargs with g :: _ -> g | [] -> "")
             Blade.DeduceRep.recordCertified env.FuncRepSigs repResolve
                 funcDecl.Name funcVarId declaredGroup repParams resolvedRet
             // THE SECOND OPINION. The seam checker has already
             // ruled on this body and remains the authority; this
             // walk changes no verdict and gates no code. Only a DISAGREE
             // is recorded, and a disagreement between two independent
             // judgments of the same theorem is a compiler bug, not a user
             // error -- the LieGuardFailure posture. `recordCertified` ran
             // FIRST on purpose, so a recursive body can assume its own
             // declared certificate (assume-guarantee), exactly as the
             // seam's judgeFunction does.
             (match Blade.DeduceRep.checkDeclaredRep env.FuncRepSigs repResolve
                        funcDecl.Name funcVarId declaredGroup repParams resolvedRet tBody with
              | Blade.DeduceRep.RepConfirm ->
                  Blade.DeduceRep.RepCheckCensus.recordConfirm funcDecl.Name
              | Blade.DeduceRep.RepAbstain reason ->
                  Blade.DeduceRep.RepCheckCensus.recordAbstain funcDecl.Name reason
              | Blade.DeduceRep.RepDisagree detail ->
                  Blade.DeduceRep.RepCheckDisagreements.add funcDecl.Name detail tBody.Span)
         | None ->
             // Unpinned: deduce. Silence is the overwhelmingly common
             // outcome (no rep family in the signature -> no candidates).
             match Blade.DeduceRep.deduceFunctionRep env.FuncRepSigs env.FuncRepSpec
                       repResolve funcDecl.Name funcVarId repParams resolvedRet tBody with
             | Some prop -> Blade.DeduceRep.TypedCertProposals.add prop tBody.Span
             | None -> ())
        funcUnitTransform env funcVarId funcDecl.ReturnType.IsNone resolvedParams resolvedRet tBody
        // The effect summary (Blade.Effects): computed here, with every
        // earlier declaration's summary in FuncEffects and this
        // function's own recursive calls read as pure, then published
        // for later declarations and grafted onto the IR callable.
        let effects = effectsOfBody env (Some funcVarId) tBody
        env.FuncEffects.[funcVarId] <- effects
        recordWitnessBody env funcVarId (typedParams |> List.map (_.VarId))
                          (not (List.isEmpty polyParams)) effects tBody
        (match outerWritesOfBody env env tBody with
         | [] -> env.FuncOuterWrites.Remove funcVarId |> ignore
         | ws -> env.FuncOuterWrites.[funcVarId] <- ws)
        env.FuncSigVarRange.[funcVarId] <- (sigVarLo, env.Subst.NextId)
        (match outerReadsOfBody env env tBody with
         | [] -> env.FuncOuterReads.Remove funcVarId |> ignore
         | rs -> env.FuncOuterReads.[funcVarId] <- rs)
        // A fractional-math demand matters only when the T-typed result
        // reaches the RETURN as T: `-> T^0 = sqrt(x)` truncates at an
        // integer instance, `-> Float64 = sqrt(x) * 2.0` and
        // `Float64(sqrt(x))` do not (the emitted C++ computes the
        // intermediate in double either way). Kept only when the
        // operand's variable still appears in the resolved return -- or the
        // return is still open at all (a generic call's declared return,
        // through which a propagated demand flows).
        // An ELEMENT-CAST result (`Float64(sqrt(x))` over `x: T`) is open
        // only in its shape; its element is the cast's target, so it
        // carries no T-typed value to the return.
        // R3: EVERY observation in the finished body of a value whose type
        // is (or is the element of) one of the declaration's signature
        // variables files GOKeyObserve under it. The inline judgments
        // record only what they can see while a node is typed, and a
        // kernel lambda's parameter is still open then (it meets the
        // variable at the apply, later); here every unification of the
        // body is done. The call judgment hands such a parameter an array
        // of integer EnumIdx values as its keys (observeEnumKeyArrayArgs);
        // an instance at the EnumIdx itself would compute on positions
        // (the post-monomorphization whitelist refuses what slips by).
        let () =
            let polyRoot (t: IRType) =
                let pv (u: IRType) =
                    match IR.stripUnits (env.Subst.Resolve u) with
                    | IRTInfer v when env.Subst.IsPolymorphicId v -> Some v
                    | _ -> None
                match IR.stripUnits (env.Subst.Resolve t) with
                | ArrayElem at -> pv at.ElemType
                | u -> pv u
            let note (what: string) (x: TypedExpr) =
                polyRoot x.Type |> Option.iter (fun v -> genericObligations.Add(GOKeyObserve (v, what)))
            let rec hasLitPat (p: TypedPattern) =
                match p.Kind with
                | TPatLit _ -> true
                | TPatTuple ps -> ps |> List.exists hasLitPat
                | TPatCons (h, t) -> hasLitPat h || hasLitPat t
                | TPatVariant (_, Some q, _) -> hasLitPat q
                | TPatStruct (_, flds) -> flds |> List.exists (snd >> hasLitPat)
                | TPatGuarded (q, _) -> hasLitPat q
                | _ -> false
            let rec walk (e: TypedExpr) =
                (match e.Kind with
                 | TExprBinOp (_, (OpAdd | OpSub | OpMul | OpDiv | OpMod | OpCaret | OpLt | OpLe | OpGt | OpGe), l, r) ->
                     note "an operator" l; note "an operator" r
                 | TExprBinOp (_, (OpEq | OpNeq), l, r) when polyRoot l.Type <> polyRoot r.Type ->
                     note "an equality with another type" l; note "an equality with another type" r
                 | TExprUnaryOp ((OpNeg | OpCast _ | OpMath _), x) -> note "a unary operation" x
                 | TExprProdSum args -> args |> List.iter (note "prodsum")
                 | TExprReduce (a, { Kind = TExprSection _ }, _) -> note "a section fold" a
                 | TExprGram (l, r, _) | TExprMatmul (l, r) -> note "a contraction" l; note "a contraction" r
                 | TExprGramApply (l, r, x) -> note "a contraction" l; note "a contraction" r; note "a contraction" x
                 | TExprSort (_, k) ->
                     (match env.Subst.Resolve k.Type with
                      | FuncElem (_, ret) ->
                          (match IR.stripUnits (env.Subst.Resolve ret) with
                           | IRTInfer v when env.Subst.IsPolymorphicId v -> genericObligations.Add(GOKeyObserve (v, "sort key"))
                           | _ -> ())
                      | _ -> ())
                 | TExprMatch (scr, cases) when cases |> List.exists (fun c -> hasLitPat c.Pattern) ->
                     note "a literal pattern" scr
                 | _ -> ())
                typedExprChildren e |> List.iter walk
            walk tBody
        let retVars =
            freeInferVars env.Subst (env.Subst.Resolve resolvedRet)
            |> Set.filter (fun v -> (env.Subst.GetElemCast v).IsNone)
        let kept =
            genericObligations
            |> Seq.filter (fun ob ->
                match ob with
                | GOCast _ -> true
                // A promotion demand matters wherever the T-typed value
                // lands, not only at the return: `let y = x + 1.0` is a
                // T-typed local an integer instance would truncate. Kept
                // while the variable is still open.
                | GOPromotion (v, _, _, _)
                | GOLiteralFit (v, _, _)
                | GOBroadcastElem (v, _, _, _)
                | GONumericOperand (v, _, _)
                | GOBoolOperand (v, _)
                | GOKeyObserve (v, _)
                | GOPowBase (v, _) ->
                    (match env.Subst.Resolve (IRTInfer v) |> IR.stripUnits with
                     | IRTInfer _ -> true
                     | _ -> false)
                // Two variables still distinct and open: a call can still
                // give them two elements.
                | GOElemAgree (v, w, _) ->
                    (match env.Subst.Resolve (IRTInfer v) |> IR.stripUnits,
                           env.Subst.Resolve (IRTInfer w) |> IR.stripUnits with
                     | IRTInfer a, IRTInfer b -> a <> b
                     | _ -> false)
                // Base and exponent both still open: a call decides both.
                // (One side settled by the body itself -- `Float64(x) ^ n`
                // -- is no longer this obligation's shape.)
                | GOPowExponent (v, w) ->
                    (match env.Subst.Resolve (IRTInfer v) |> IR.stripUnits,
                           env.Subst.Resolve (IRTInfer w) |> IR.stripUnits with
                     | IRTInfer a, IRTInfer b -> a <> b
                     | _ -> false)
                | GOFractionalMath (v, _) ->
                    match env.Subst.Resolve (IRTInfer v) |> IR.stripUnits with
                    | IRTInfer r -> retVars.Contains r || not retVars.IsEmpty
                    | _ -> false)
            |> List.ofSeq
        if not kept.IsEmpty then
            env.FuncGenericObligations.[funcVarId] <- kept
        // Bounded PARAMETERS and RETURN (`x: Sal`, `-> Float64<min=0.0>`,
        // through aliases too): runtime guards at entry and at the return,
        // the same BL8001 guards a bounded `let` gets. Wrapped LAST, after
        // every deduction has read the unguarded body. A guard's run-time
        // message names the function as the program wrote it.
        match wrapBoundedSignatureChecks bodyEnv { funcDecl with Name = shownName } tBody with
        | Error e -> Error e
        | Ok tBodyGuarded ->
        let tf : TypedFunctionDecl = {
            Name = funcDecl.Name; SourceName = shownName; FuncId = funcVarId
            TypeParams = funcDecl.TypeParams
            Params = resolvedParams; ReturnType = resolvedRet
            WhereClause = funcDecl.WhereClause; Body = tBodyGuarded
            CommGroups = commGroups; IsStatic = funcDecl.IsStatic
            NameSpan = funcDecl.NameSpan
            Effects = effects
        }
        Ok (TDeclFunction tf, envWithFunc)))))

/// THE ARRAY INSTANCE of an arrow-parameter declaration (TypeEnv.ArrowVariant;
/// the section note above TypeCheckSupport.arrowVariantHook): the declaration
/// `fid` (or the instance `fid` is, whose fixed parameters it keeps) checked
/// again from its source, in the environment it was checked in, with the
/// parameters in `arrays` FIXED to the arrays a call passes (an unknown axis
/// and the caller's generic variables re-minted as the instance's own).
/// Made once per key (TypeCheckSupport.arrowInstanceKey) and registered before
/// its body is checked, so its recursive calls reach it. Its warnings were the
/// source's to give (the declaration's own check gave them); a refusal is
/// returned. The checked declaration waits in TypeEnv.ArrowVariantDecls for
/// TypeCheck.checkModule to place it after its origin. Returns the head that
/// names it -- spelled with the origin's name, so diagnostics read as the
/// source does.
and checkArrowVariant (env: TypeEnv) (fid: IRId) (arrays: Map<int, IRType>) : TypeResult<TypedExpr> =
    match env.ArrowDeclSources.TryGetValue fid with
    | false, _ -> Error (InternalErr "internal: an arrow-parameter declaration's array instance was asked for, but its source was not recorded")
    | true, (srcDecl, srcEnv, baseFixed, origin) ->
    let fixedTys =
        arrays |> Map.fold (fun acc i t ->
            Map.add i (arrowInstanceParamType (fun () -> env.Builder.FreshId()) (env.Subst.Resolve t)) acc) baseFixed
    let key = arrowInstanceKey env.Subst origin fixedTys
    let head (v: ArrowVariant) = mkTyped (TExprVar (srcDecl.Name, v.VarId, None)) v.FuncType
    match env.ArrowVariants.TryGetValue key with
    | true, v -> Ok (head v)
    | _ ->
    let n = (env.ArrowVariants.Values |> Seq.filter (fun v -> v.Origin = origin) |> Seq.length) + 1
    let declName = $"{srcDecl.Name}__arrow{n}"
    // The caller's error location: the instance's check stamps spans inside
    // the source body, and a refusal of the CALL (re-judged against the
    // instance) must point where it would against any declaration.
    let spans0 = saveSpanCells ()
    let warnings0 = env.Warnings.Count
    let warningLog0 = Blade.TypeEnv.WarningLog.get ()
    let pins0 = Blade.TypeCheckIde.PinSuggestions.get ()
    let facts0 = Blade.TypeEnv.DeducedFacts.get ()
    // The editor's deduction side channel (kernel rows, deduced pairs): the
    // instance's body is the origin's source, already recorded by the
    // origin's check, so the instance adds nothing an editor would see.
    let idePairs0 = IdeDeductions.pairs.Value
    let idePacks0 = IdeDeductions.packs.Value
    let ideKernels0 = IdeDeductions.kernels.Value
    // Checked under its unique internal name (the C++ definition's), but it
    // is the origin's declaration: its messages and its SourceName say the
    // origin's name (TypedFunctionDecl.SourceName).
    let checkedR =
        checkFunctionDeclWith fixedTys
            (fun vid fty ->
                env.ArrowVariants.[key] <- { DeclName = declName; VarId = vid; FuncType = fty; Origin = origin })
            (Some srcDecl.Name) None
            srcEnv { srcDecl with Name = declName }
    env.Warnings.RemoveRange(warnings0, env.Warnings.Count - warnings0)
    Blade.TypeEnv.WarningLog.reset ()
    for d in warningLog0 do Blade.TypeEnv.WarningLog.add d
    Blade.TypeCheckIde.PinSuggestions.reset ()
    for (m, sp) in pins0 do Blade.TypeCheckIde.PinSuggestions.add m sp
    Blade.TypeEnv.DeducedFacts.reset ()
    for (f, sp) in facts0 do Blade.TypeEnv.DeducedFacts.add f sp
    IdeDeductions.pairs.Value <- idePairs0
    IdeDeductions.packs.Value <- idePacks0
    IdeDeductions.kernels.Value <- ideKernels0
    match checkedR with
    | Error e ->
        env.ArrowVariants.Remove key |> ignore
        Error e
    | Ok (decl, _) ->
        restoreSpanCells spans0
        let v = env.ArrowVariants.[key]
        (match env.ArrowDeclSources.TryGetValue v.VarId with
         | true, _ -> env.ArrowDeclSources.[v.VarId] <- (srcDecl, srcEnv, fixedTys, origin)
         | _ -> ())
        env.ArrowVariantDecls.Add((origin, decl))
        Ok (head v)

/// THE SHARING INSTANCE of an identity-sensitive declaration (plan 2.2;
/// TypeEnv.SharingDeclSources; the section note above
/// TypeCheckSupport.sharingVariantHook): the declaration `fid` checked again
/// from its source, in the environment it was checked in, with each relevant
/// parameter carrying the identity of its class in `cls` (relevant position ->
/// class, first-occurrence normalized), so `method_for(x, y)` over a [0,0]
/// call is the identity group `method_for(x, x)` is -- packed and triangular
/// under a comm kernel -- and the instance's return type is its own. Made once
/// per (origin, partition); registered before the body is checked. Its
/// warnings were the generic declaration's to give; a refusal is returned (the
/// caller then keeps the generic declaration). The checked declaration waits
/// in TypeEnv.ArrowVariantDecls, placed after its origin. `blade plan` records
/// `[sharing-partition v1] <f>: applied [partition [0,0] of (x, y); ...]`.
and checkSharingVariant (env: TypeEnv) (fid: IRId) (cls: Map<int, int>) : TypeResult<TypedExpr> =
    match env.SharingDeclSources.TryGetValue fid with
    | false, _ -> Error (InternalErr "internal: a sharing instance was asked for, but its source was not recorded")
    | true, (srcDecl, srcEnv, rel, _) ->
    let partition = sharingPartitionText rel cls
    let key = $"{fid}|{partition}"
    let head (v: ArrowVariant) = mkTyped (TExprVar (srcDecl.Name, v.VarId, None)) v.FuncType
    let names = rel |> List.map (fun p -> (List.item p srcDecl.Params).Name) |> String.concat ", "
    let evidence (declName: string) =
        [ $"partition {partition} of ({names})"
          $"instance {declName}: each class is one array for the identity groups of its body"
          "the all-distinct partition keeps the generic declaration" ]
    match env.SharingVariants.TryGetValue key with
    | true, v ->
        sharingRecord srcDecl Blade.Effects.Applied (evidence v.DeclName)
        Ok (head v)
    | _ ->
    let n = (env.SharingVariants.Values |> Seq.filter (fun v -> v.Origin = fid) |> Seq.length) + 1
    let declName = $"{srcDecl.Name}__share{n}"
    let spans0 = saveSpanCells ()
    let warnings0 = env.Warnings.Count
    let warningLog0 = Blade.TypeEnv.WarningLog.get ()
    let pins0 = Blade.TypeCheckIde.PinSuggestions.get ()
    let facts0 = Blade.TypeEnv.DeducedFacts.get ()
    let idePairs0 = IdeDeductions.pairs.Value
    let idePacks0 = IdeDeductions.packs.Value
    let ideKernels0 = IdeDeductions.kernels.Value
    let checkedR =
        checkFunctionDeclWith Map.empty
            (fun vid fty ->
                env.SharingVariants.[key] <- { DeclName = declName; VarId = vid; FuncType = fty; Origin = fid })
            (Some srcDecl.Name) (Some cls)
            srcEnv { srcDecl with Name = declName }
    env.Warnings.RemoveRange(warnings0, env.Warnings.Count - warnings0)
    Blade.TypeEnv.WarningLog.reset ()
    for d in warningLog0 do Blade.TypeEnv.WarningLog.add d
    Blade.TypeCheckIde.PinSuggestions.reset ()
    for (m, sp) in pins0 do Blade.TypeCheckIde.PinSuggestions.add m sp
    Blade.TypeEnv.DeducedFacts.reset ()
    for (f, sp) in facts0 do Blade.TypeEnv.DeducedFacts.add f sp
    IdeDeductions.pairs.Value <- idePairs0
    IdeDeductions.packs.Value <- idePacks0
    IdeDeductions.kernels.Value <- ideKernels0
    restoreSpanCells spans0
    match checkedR with
    | Error e ->
        env.SharingVariants.Remove key |> ignore
        Error e
    | Ok (decl, _) ->
        let v = env.SharingVariants.[key]
        env.ArrowVariantDecls.Add((fid, decl))
        sharingRecord srcDecl Blade.Effects.Applied (evidence declName)
        Ok (head v)

/// Where-clause predicate contract: a static function called from a
/// struct/mutual where-conjunct must have fully annotated params + return.
/// Its pre-pass type is created ONCE (shared tyvars across every call
/// site), so an unannotated predicate unified against one owner's field
/// types would silently over-constrain the next -- annotations pin the
/// contract instead.
and internal wherePredicateAnnotationCheck (env: TypeEnv) (owner: string) (conjuncts: Expr list) : TypeResult<unit> =
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
                        || fd.Params |> List.exists (_.Type.IsNone) -> Some f
            | _ -> None)
    match offender with
    | Some f -> Error (WherePredicateUnannotated (owner, f))
    | None -> Ok ()

and registerTypeDecl (env: TypeEnv) (typeDecl: TypeDecl) : TypeResult<TypeEnv> =
    match typeDecl with
    | TyDeclAlias (srcName, _typeParams, body) ->
        registerAliasDecl env srcName body

    | TyDeclStruct (srcName, typeParams, fields, constraints, isStatic) ->
        registerStructDecl env srcName fields isStatic constraints typeParams

    | TyDeclSum (srcName, typeParams, variants) ->
        // The sum type and each constructor get the module's identity (D11):
        // `A.Color` / `A.Red`, emitted `A__Red`, its tag the hash of that.
        let name = declIdentity env srcName
        let variantTypes = variants |> List.map (fun v ->
            (declIdentity env v.Name, v.Data |> Option.map (lowerTypeExpr env)))
        let env' = registerDeclaredTypeDef srcName name (TDIVariant (name, typeParams, variantTypes)) env
        Ok (variants |> List.fold (fun e v ->
            registerDeclaredCtor v.Name (declIdentity env v.Name) name (v.Data |> Option.map (lowerTypeExpr env)) e) env')

    | TyDeclMutualGroup (members, constraints) ->
        registerMutualGroupDecl env members constraints

and registerAliasDecl (env: TypeEnv) (srcName: Ident) (body: TypeExpr) : TypeResult<TypeEnv> =
    // D11: `name` is the declaration's IDENTITY (`A.Cell` in a non-main
    // module `A`, the bare name in main) -- what an index alias's Tag and
    // the registered definition carry; `srcName` is how this module's
    // own source spells it (a scope key).
    let name = declIdentity env srcName
    // Keep the SURFACE body: TDIAlias below stores a lowered IRType, and
    // lowering erases min=/max=, so this is the only record of a bound
    // written inside an alias (see TypeEnv.SurfaceAliases).
    let env = { env with SurfaceAliases = Map.add srcName body env.SurfaceAliases }
    // Chase one level of alias indirection: if the body is `TyNamed n`
    // where n is itself a registered TDIIndexType or TDIEnumIdx alias,
    // use n's stored body for our own registration. Each registration
    // step already stores its resolved body, so chains of any depth
    // flatten without transitive walking at lookup time.
    //
    // Without this, `type B = A` (where A is an index or enum alias)
    // falls to the generic `_ -> TDIAlias` branch and B is never
    // recognized as an index/enum alias -- using B in array index lists
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
    /// The referenced definition's own index RECORD, when this alias
    /// chased one. `chasedBody` keeps the referenced SURFACE body, and for
    /// a PROVIDER-registered axis (`<binding>.index.<dim>`) that body is a
    /// synthesized `TyIdx <extent>` -- `registerProviderModule` stores the
    /// real record beside it and a bare extent expression as the body,
    /// because there is no surface syntax for "the axis this store minted".
    /// Re-lowering that body therefore produced an anonymous `Idx<n>` and
    /// dropped the `__icaxis|`/`__icpool|` identity on the floor, which is
    /// how `type L = ck.index.lat` came to LAUNDER a diverged axis. Kept
    /// here so the index arm below can adopt the record instead of
    /// rebuilding it from a body that never described it.
    let chasedRecord =
        match body with
        | TyNamed (referencedName, _) ->
            match Map.tryFind referencedName env.TypeDefs with
            | Some (TDIIndexType (_, refIdx, _)) -> Some refIdx
            | _ -> None
        | _ -> None
    /// Adopt the chased record for a PROVIDER axis only. Every other
    /// alias keeps re-lowering its body: that is what makes
    /// `type S = SymIdx<2, n>` and its chains behave identically whether
    /// they were written inline or chased, and narrowing the change to
    /// records the provider minted keeps it to the defect it fixes.
    let indexRecordFor (env: TypeEnv) (bodyTy: TypeExpr) : IRIndexType =
        match chasedRecord with
        | Some r when (match r.Tag with
                       | Some t -> isProviderAxisTag t
                       | None -> false) -> r
        | _ -> lowerIndexType env 0 bodyTy
    // `type A = Chunked<I, spec>` (docs/plans/structural/07 §2.1, §3.1):
    // A IS I -- the alias adopts I's record verbatim, so `Nat<A>` and
    // `Nat<I>` unify and every plain-axis walker is untouched -- and the
    // SEGMENTATION is registered beside it (env.Segmentations), read only
    // by `segments(A)`. The spec is judged here, once:
    //   - a literal edge K: a regular grid over I's static extent;
    //   - `store`: the provider's own grid (a later piece; refused with
    //     a steer today);
    //   - `[[s, f], ..]`: stores tiling I (the file level; a later piece;
    //     refused with a steer today).
    let chunked : TypeResult<(IRIndexType * Segmentation) option> =
        match chasedBody with
        | TyChunked (inner, spec) ->
            let innerRecord =
                match inner with
                | TyNamed (n, []) ->
                    (match Map.tryFind n env.TypeDefs with
                     | Some (TDIIndexType (_, idx, _)) -> Ok idx
                     | _ -> Error (SegmentedDomainErr $"Chunked<{n}, ..>: '{n}' is not a registered index type; declare it first (`type {n} = Idx<n>` or a store axis)"))
                | TyIdx _ -> Ok (lowerIndexType env 0 inner)
                | _ -> Error (SegmentedDomainErr "Chunked<I, ..>: the inner type must name an index type (an `Idx<n>` alias or a store axis)")
            innerRecord |> Result.bind (fun idx ->
                let extent =
                    match idx.Extent with
                    | IRLit (IRLitInt n) -> Some n
                    | e -> tryEvalIntIR e
                match extent with
                | None ->
                    Error (SegmentedDomainErr $"Chunked<..>: the axis '{name}' segments must have a static extent; its extent is not known at compile time")
                | Some n ->
                    let levels =
                        match spec.Kind with
                        | ExprKind.ExprLit (LitInt k) when k >= 1L && k <= n -> Ok [ SegRegular k ]
                        | ExprKind.ExprLit (LitInt k) ->
                            Error (SegmentedDomainErr $"Chunked<.., {k}>: the chunk edge must be between 1 and the axis extent {n}")
                        | ExprKind.ExprVar "store" ->
                            // provider inheritance: the inner must be a store axis
                            // `<binding>.index.<dim>`, whose chunk edge the load
                            // site recorded (ProviderRegistry.DimChunks)
                            (match inner with
                             | TyNamed (path, []) when path.Split('.').Length >= 3 && path.Split('.').[1] = "index" ->
                                 let parts = path.Split('.')
                                 let binding = parts.[0]
                                 let dim = String.concat "." (parts |> Array.skip 2)
                                 (match Blade.ProviderRegistry.DimChunks.tryFind binding dim with
                                  | Some edge when edge >= 1L && edge <= n -> Ok [ SegRegular edge ]
                                  | Some edge -> Error (SegmentedDomainErr $"Chunked<{path}, store>: the store chunks '{dim}' at {edge}, outside 1..{n}")
                                  | None when Blade.ProviderRegistry.DimChunks.hasStore binding ->
                                      Error (SegmentedDomainErr $"Chunked<{path}, store>: the store does not chunk '{dim}' uniformly across its variables (or does not chunk it at all); write the edge as a literal")
                                  | None ->
                                      Error (SegmentedDomainErr $"Chunked<{path}, store>: '{binding}' is not a store binding whose provider exposes chunk edges (zarr does); write the edge as a literal"))
                             | _ -> Error (SegmentedDomainErr "Chunked<I, store> inherits the chunk grid of a STORE axis: the inner must be `<store>.index.<dim>`"))
                        | ExprKind.ExprArrayLit entries ->
                            // the FILE level (§2.7, decisions D1/D5): stores tiling the
                            // dimension named by the inner store axis, in order, each
                            // with its own chunking -- a literal edge, `store` (that
                            // file's grid), or none. Every fact comes from the modules
                            // the load sites recorded; no store is re-opened.
                            let dimName =
                                match inner with
                                | TyNamed (path, []) when path.Split('.').Length >= 3 && path.Split('.').[1] = "index" ->
                                    Ok (String.concat "." (path.Split('.') |> Array.skip 2))
                                | _ -> Error (SegmentedDomainErr "Chunked<I, [[store, chunking], ..]>: the inner must be a store axis `<store>.index.<dim>` naming the dimension the stores tile")
                            dimName |> Result.bind (fun dim ->
                                let parseEntry (e: Expr) : TypeResult<string * Expr option> =
                                    match e.Kind with
                                    | ExprKind.ExprArrayLit [ { Kind = ExprKind.ExprVar s }; f ] -> Ok (s, Some f)
                                    | ExprKind.ExprArrayLit [ { Kind = ExprKind.ExprVar s } ] -> Ok (s, None)
                                    | ExprKind.ExprVar s -> Ok (s, None)
                                    | _ -> Error (SegmentedDomainErr "Chunked<I, [..]>: each entry is `[store, chunking]` (chunking a literal edge or `store`) or a bare store name")
                                let resolveEntry (s: string, f: Expr option) : TypeResult<string * int64 * int64 option> =
                                    let extent =
                                        match Blade.ProviderRegistry.IdeStores.tryFind s with
                                        | None -> Error (SegmentedDomainErr $"Chunked<I, [..]>: '{s}' is not a data-provider store binding in scope")
                                        | Some pm ->
                                            (match pm.Types |> List.tryPick (function IRTDIndexType (n, idx) when n = dim -> Some idx | _ -> None) with
                                             | Some idx ->
                                                 (match idx.Extent with
                                                  | IRLit (IRLitInt n) -> Ok n
                                                  | _ -> Error (SegmentedDomainErr $"Chunked<I, [..]>: the store '{s}' has no static extent for '{dim}'"))
                                             | None -> Error (SegmentedDomainErr $"Chunked<I, [..]>: the store '{s}' has no dimension '{dim}'"))
                                    extent |> Result.bind (fun ext ->
                                        match f with
                                        | None -> Ok (s, ext, None)
                                        | Some { Kind = ExprKind.ExprLit (LitInt k) } when k >= 1L && k <= ext -> Ok (s, ext, Some k)
                                        | Some { Kind = ExprKind.ExprLit (LitInt k) } ->
                                            Error (SegmentedDomainErr $"Chunked<I, [..]>: the chunk edge {k} for '{s}' is outside 1..{ext}")
                                        | Some { Kind = ExprKind.ExprVar "store" } ->
                                            (match Blade.ProviderRegistry.DimChunks.tryFind s dim with
                                             | Some k -> Ok (s, ext, Some k)
                                             | None -> Error (SegmentedDomainErr $"Chunked<I, [..]>: the store '{s}' does not chunk '{dim}' uniformly (or its provider exposes no chunk edges); write its edge as a literal"))
                                        | Some _ -> Error (SegmentedDomainErr "Chunked<I, [..]>: a file's chunking is a literal edge or `store`"))
                                entries |> List.map parseEntry
                                |> List.fold (fun acc r -> acc |> Result.bind (fun xs -> r |> Result.map (fun x -> xs @ [ x ]))) (Ok [])
                                |> Result.bind (fun parsed ->
                                    if parsed.IsEmpty then Error (SegmentedDomainErr "Chunked<I, [..]>: at least one store is needed")
                                    else
                                        parsed |> List.map resolveEntry
                                        |> List.fold (fun acc r -> acc |> Result.bind (fun xs -> r |> Result.map (fun x -> xs @ [ x ]))) (Ok [])
                                        |> Result.map (fun files -> [ SegFiles files ])))
                        | _ -> Error (SegmentedDomainErr "Chunked<I, spec>: the spec must be a literal chunk edge, `store`, or a list of [store, chunking] pairs")
                    levels |> Result.map (fun lv ->
                        match lv with
                        | [ SegFiles files ] ->
                            // a file-segmented axis is a NEW logical axis over the summed
                            // extent, named by the alias; the inner only named the dimension
                            let total = files |> List.sumBy (fun (_, ext, _) -> ext)
                            let axis = {
                                Id = env.Builder.FreshId(); Rank = 1
                                Extent = IRLit (IRLitInt total)
                                Symmetry = SymNone; Tag = Some name; IxKind = ixKindOfTag (Some name)
                                Kind = SDimension; Dependencies = []
                            }
                            Some (axis, { Alias = name; Source = axis; Extent = total; Levels = lv })
                        | _ ->
                            Some (idx, { Alias = name; Source = idx; Extent = n; Levels = lv })))
        | _ -> Ok None
    let defInfoResult =
        match chasedBody with
        | TyChunked (inner, _) ->
            chunked |> Result.map (fun r ->
                match r with
                | Some (idx, _) -> TDIIndexType (name, idx, inner)
                | None -> TDIAlias (lowerTypeExpr env chasedBody))
        // LeafIdx/NodeIdx join the PLAIN-index arm deliberately, NOT the
        // TyTreeIdx arm below: they lower to an ordinary rank-1 dense
        // record, so `type Leaves = LeafIdx<crystal>` wants the ordinary
        // nominative overwrite that `type R = Idx<5>` gets. That is what
        // gives a user who wants nominal safety over a leaf axis the same
        // tool every other Idx alias has, while the anonymous form stays
        // interchangeable with `Idx<n>` -- which is the entire point of the
        // derived axes.
        | TyIdx _ | TySymIdx _ | TyAntisymIdx _ | TyOrbIdx _ | TyHermitianIdx _ | TyBoundedIdx _
        | TyLeafIdx _ | TyNodeIdx _ ->
            let idx = indexRecordFor env chasedBody
            // An extent is a COUNT. The literal spellings are refused by
            // the IndexTypeValidator; this catches a computed one (`let
            // static n = 0 - 4; type I = Idx<n>`), which folds only here.
            match (match idx.Extent with IROrbitClass _ -> None | e -> Blade.IRPrint.tryEvalIntIR e) with
            | Some v when v < 0L ->
                Error (IndexTypeErr $"type '{name}': an index type's extent is a count of positions and must be non-negative, but it folds to {v}.")
            | _ ->
            // Nominative-alias rule: the alias name BECOMES the identity
            // tag. Two exceptions, both reachable only from stage 3's
            // `type S = SymIdx<k, IrrepsIdx<spec>>` (no legacy form of
            // these index types can produce an irreps tag, so this is
            // behaviour-preserving for everything that shipped before):
            //   - an irreps identity: fold the name INTO the tag exactly
            //     as the TyIrrepsIdx arm below does, rather than
            //     overwriting it -- otherwise aliasing silently drops the
            //     spec payload and breaks Tag<->IxKind agreement;
            //   - a bad-spec ERROR marker: keep it, so the consumption-site
            //     diagnostic still fires through the alias.
            let named =
                match idx.Tag with
                | Some (IrrepsTag (_, triples)) ->
                    { idx with Tag = Some (mkIrrepsTag (Some name) triples) }
                | _ when idx.IxKind = IxKErrorIrrepsBadSpec -> idx
                // The tree twin, reachable now that LeafIdx/NodeIdx route
                // here: a malformed degree sequence lowered to the shared
                // bad-shape marker, and overwriting its Tag with the alias
                // name would drop the error kind AND the smuggled detail,
                // turning `type L = LeafIdx<[2, 0]>` from a BL4021 into a
                // plain index alias over a placeholder extent.
                | _ when idx.IxKind = IxKErrorTreeBadShape -> idx
                // A depth >= 2 wreath record keeps its "__orbidx" sentinel.
                // Here Tag IS the kind channel (the IR validator enforces
                // Tag<->IxKind agreement) and there is no parameterized tag
                // format for a level list the way mkIrrepsTag has one for a
                // spec, so overwriting it with the alias name would break
                // agreement and lose the kind. NOMINATIVE aliasing of a
                // wreath class is therefore deferred WITH its storage:
                // `type R = OrbIdx<[...], n>` names the class for
                // readability but mints no distinct identity. Depth <= 1
                // normalizes to a Sym/Antisym/plain record and takes the
                // ordinary nominative path below, exactly like
                // `type S = SymIdx<2, n>`.
                | _ when idx.IxKind = IxKOrbit -> idx
                // A MULTI-RANK compact record (SymIdx/AntisymIdx/
                // HermitianIdx, Rank >= 2) keeps the tag of its COMPONENT
                // space. The fourth carve-out of the same species as the
                // three above: on a multi-rank record `Tag` is not a
                // spare name field, it is the identity of the space the
                // slot's values inhabit -- one record has one `Tag`
                // (IRIndexTypeG.Tag, "name (index space matching)"), and
                // `elemTypeForIterationIndex` hands that tag to EVERY
                // component param. Overwriting it with the group's name
                // typed both params of `type S2 = SymIdx<2, I3>` as
                // `Nat<S2>` -- a type no component index of S2 can
                // inhabit -- so indexing the I3-tagged array they
                // actually range over was a hard BL4003, while the
                // identical program spelled inline merely warned.
                // Whether the group happens to be NAMED is not a semantic
                // distinction; this is docs/formalism.md 7.3's stated
                // minimal fix, and with it both spellings check silently.
                //
                // Like the wreath carve-out above, a multi-rank alias
                // therefore names the class for readability but mints no
                // distinct nominal identity. That is the honest trade:
                // the alternative is a second tag field on every index
                // record to carry group identity and component identity
                // apart, which is a type-system change, not a fix.
                | _ when idx.Rank >= 2 -> idx
                // A PROVIDER-MINTED axis keeps its provenance tag. Fifth
                // carve-out of the same species as the four above: on a
                // `__icaxis|`/`__icpool|` record the Tag is not a spare
                // name field, it is the axis's IDENTITY -- which repo,
                // which snapshot's version of the dim -- and the alias is
                // a local SPELLING of that identity, not a second one.
                //
                // Overwriting it broke the rule both ways. Laundering:
                // ascribing two DIVERGED checkouts' arrays to one
                // `type L = ck1.index.lat` re-tagged both `L`, so
                // arithmetic the axis tag exists to refuse co-iterated
                // clean. False refusal: within ONE checkout, an aliased
                // array beside the raw one carried `L` against
                // `__icaxis|lat@...` and earned a BL3999 the identical
                // Zarr program (untagged axes) never sees. Keeping the tag
                // settles both -- same axis, same tag; different axis,
                // different tag -- and `Ide.indexNamesOf` already decodes
                // the dim name out of it, so the alias still prints as a
                // name.
                //
                // Same trade as the wreath and multi-rank arms: the alias
                // names the axis for readability and mints no distinct
                // nominal identity. For a provider axis that costs
                // nothing -- an identity minted from a Blade alias could
                // not answer to the store that owns the axis.
                | _ when (match idx.Tag with
                          | Some t -> isProviderAxisTag t
                          | None -> false) -> idx
                | _ -> { idx with Tag = Some name; IxKind = ixKindOfTag (Some name) }
            Ok (TDIIndexType (name, named, chasedBody))
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
        | TyPgIrrepsIdx _ ->
            // THE STAGE-3 ALIAS FIX, REPLAYED for the pg tag (section 7's 5b-i
            // checklist item). Identical reasoning one member over: the
            // nominative-alias rule folds the alias name INTO the
            // pg-irreps identity tag (mkPgIrrepsTag group (Some name) ...)
            // rather than overwriting Tag with the bare name, so two
            // aliases of the same (group, spec) are DISTINCT types while
            // anonymous PgIrrepsIdx<G, spec> unifies with either. The
            // plain-index arm's `Tag = Some name` overwrite would drop the
            // group and the spec payload AND break the Tag<->IxKind
            // agreement the IR validator enforces. A bad-spec marker keeps
            // its error tag so the consumption-site check still fires
            // through the alias.
            let idx = lowerIndexType env 0 chasedBody
            let named =
                match idx.Tag with
                | Some (PgIrrepsTag (group, _, entries)) ->
                    { idx with Tag = Some (mkPgIrrepsTag group (Some name) entries) }
                | _ -> idx
            Ok (TDIIndexType (name, named, chasedBody))
        | TyTreeIdx _ ->
            // Nominative-alias rule, replayed for the tree tag (the
            // IrrepsIdx/PgIrrepsIdx discipline verbatim): the alias name is
            // FOLDED INTO the identity tag (mkTreeTag (Some name) degs), so
            // two aliases of the same shape are DISTINCT types while an
            // anonymous TreeIdx<s> unifies with either. The plain-index
            // arm's `Tag = Some name` overwrite would drop the degree
            // sequence AND break the Tag<->IxKind agreement the IR
            // validator enforces. A bad-shape marker keeps its error tag so
            // the consumption-site diagnostic still fires through the alias
            // -- that is the T1 alias-laundering gate.
            let idx = lowerIndexType env 0 chasedBody
            let named =
                match idx.Tag with
                | Some (TreeTag (_, degs)) ->
                    { idx with Tag = Some (mkTreeTag (Some name) degs) }
                | _ -> idx
            Ok (TDIIndexType (name, named, chasedBody))
        | TyEnumIdx valuesExpr ->
            // Static-evaluate the array literal to extract values. Each
            // element must be either an int literal (with optional negation)
            // or a string literal. Mixed kinds are a type error -- the two
            // backings (int64_t vs std::string) cannot share one EnumIdx.
            let raw =
                match valuesExpr.Kind with
                | ExprKind.ExprArrayLit elems ->
                    elems |> List.choose (fun e ->
                        match e.Kind with
                        | ExprKind.ExprLit (LitInt n) -> Some (EVInt n)
                        | AstNegIntLit n -> Some (EVInt (-n))
                        | ExprKind.ExprLit (LitString s) -> Some (EVString s)
                        | _ -> None)
                | _ -> []
            let hasInt = raw |> List.exists _.IsEVInt
            let hasString = raw |> List.exists _.IsEVString
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
        | _ ->
            // `type B = Field<min=0.0, max=1.0>` hides the bound in the
            // alias BODY, where no use site can see it -- the annotation
            // `x: B` carries no TyBounded at all, so today the bound is
            // silently dropped. Catch it at the declaration.
            match boundedAggregateError env $"type alias '{name}'" body with
            | Some e -> Error e
            | None -> Ok (TDIAlias (lowerTypeExpr env body))
    defInfoResult |> Result.bind (fun defInfo ->
        chunked |> Result.map (fun seg ->
            let env' = registerDeclaredTypeDef srcName name defInfo env
            match seg with
            | Some (_, s) ->
                match s.Levels with
                | [SegRegular k] -> Blade.Types.SegmentTable.recordUniform name s.Extent k
                | _ -> Blade.Types.SegmentTable.record name (segmentationOffsets s)
                { env' with Segmentations = Map.add srcName s env'.Segmentations }
            | None -> env'))

and registerStructDecl (env: TypeEnv) (srcName: Ident) (fields: FieldDecl list) (isStatic: bool) (constraints: Expr list) (typeParams: Ident list) : TypeResult<TypeEnv> =
    let name = declIdentity env srcName
    // Mutual member types are forbidden as field types -- alias
    // transparency would silently erase the constraint.
    let memberMisuse =
        fields |> List.tryPick (fun f ->
            match mutualMemberNamesIn env f.Type with
            | [] -> None
            | n :: _ -> Some (StructFieldMutualType (name, f.Name, n)))
    // A struct field STORES a value, so its declared type has to name a
    // real tag; a wildcard there would erase the discipline (see
    // irTypeHasTagWildcard).
    // A field's bounds do NOT survive as a TyBounded -- the parser
    // normalizes BOTH spellings (`f: T<min=a, max=b>` and `f: T in lo ..
    // hi`) into `FieldDecl.Bound` and leaves `f.Type` bare. So the field
    // check reads the Bound slot and classifies the field's own type,
    // rather than walking for a TyBounded that is no longer there.
    // Without it, `structConjuncts` desugars the bound into a conjunct and
    // the failure surfaces as "where-constraint must be a boolean
    // expression, got Array<Bool like ...>" -- a constraint the user never
    // wrote.
    let boundedAggregateField =
        fields |> List.tryPick (fun f ->
            let site = $"struct {name}, field '{f.Name}'"
            let fromBound =
                if f.Bound.IsSome then
                    boundedAggregateNoun env f.Type
                    |> Option.map (fun noun -> BoundsOnAggregate (site, noun, "the field's value"))
                else None
            fromBound
            |> Option.orElseWith (fun () -> boundedAggregateError env site f.Type))
    let wildcardField =
        fields |> List.tryPick (fun f ->
            if irTypeHasTagWildcard (lowerTypeExpr env f.Type)
            then Some (TagWildcardNotParam $"struct {name}, field '{f.Name}'")
            else None)
    // `static struct` DECLARES the static-eligibility fence instead of
    // leaving it to be inferred at each use: every field type must be a
    // shape StaticEval can carry as a StaticValue. Ordinary structs skip
    // this entirely -- nothing about them changes.
    let staticFieldErr =
        if not isStatic then None
        else
            let rec why (t: TypeExpr) : string option =
                match t with
                | TyInt32 | TyInt64 | TyFloat32 | TyFloat64
                | TyBool | TyString | TyChar | TyUnit -> None
                | TyBounded (b, _, _) -> why b
                | TyTuple ts -> ts |> List.tryPick why
                // A width-only `Tuple<N>` has INFERRED element types, so
                // the static world has no shape to carry. The generic
                // catch-all below would say "index and other structured
                // types", which misnames it.
                | TyTupleWidth _ ->
                    Some "`Tuple<N>` leaves its element types inferred -- write them out as `(T1, T2)`"
                | TyNamed (n, _) ->
                    match n with
                    | "Int32" | "Int64" | "Float64"
                    | "Float32" | "Bool" | "String" | "Char" | "Nat" | "Void" -> None
                    | _ when env.StaticStructs.Contains n -> None
                    | _ when (match lookupTypeDef n env with Some (TDIStruct _) -> true | _ -> false) ->
                        Some $"'{n}' is a struct declared without `static`"
                    | _ when (match lookupTypeDef n env with Some (TDIVariant _) -> true | _ -> false) ->
                        Some $"'{n}' is a sum type, which the static world cannot represent"
                    | _ when (match lookupTypeDef n env with Some (TDIAlias _) -> true | _ -> false) ->
                        // Aliases are transparent in the value world but the
                        // surface type is all we have here; be explicit.
                        Some $"'{n}' is a type alias -- write the underlying primitive"
                    | _ -> Some $"type '{n}' is not a static shape"
                | TyArray _ | TyAbstractArray _ -> Some "array types are not statically evaluable"
                | TyFunc _ -> Some "function types are not statically evaluable"
                | TyPoly _ -> Some "arity-polymorphic packs are not statically evaluable"
                | TyVar _ -> Some "type variables are not statically evaluable"
                | _ -> Some "index and other structured types are not statically evaluable"
            fields |> List.tryPick (fun f ->
                why f.Type |> Option.map (fun w -> StaticStructField (name, f.Name, w)))
    // Bounds that CROSS, decided statically: `min=` strictly above
    // `max=`. Only the INCLUSIVE spelling is checked -- `in lo .. hi` is
    // pre-existing syntax whose empty case (`in 0 .. 0`) has always been
    // accepted, and an empty solution set is a warning-class event per
    // the constrained-index plan, not an error. Only fires when both
    // ends fold; a bound referencing an earlier field is not decidable
    // here and is left to the conjuncts.
    let invertedBoundErr =
        fields |> List.tryPick (fun f ->
            match f.Bound with
            | Some { Lo = Some lo; Hi = Some hi; HiInclusive = true } ->
                let fold e =
                    match evalStaticValueExpr env e with
                    | Ok (StaticEval.SVInt v) -> Some v
                    | _ -> None
                match fold lo, fold hi with
                | Some l, Some h when l > h ->
                    Some (BoundsInverted ($"struct {name}, field '{f.Name}'", string l, string h))
                | _ -> None
            | _ -> None)
    match memberMisuse |> Option.orElse wildcardField
                       |> Option.orElse boundedAggregateField
                       |> Option.orElse staticFieldErr
                       |> Option.orElse invertedBoundErr with
    | Some e -> Error e
    | None ->
        let env = if isStatic then { env with StaticStructs = Set.add srcName (Set.add name env.StaticStructs) } else env
        // The static evaluator's own record of this struct (the same
        // shape StaticEval.resolveStatics builds), so `staticEnvOf`
        // carries it to the index fence and the enumeration route.
        let env =
            { env with
                StructStatics =
                    Map.add srcName
                        ({ StaticEval.StructStaticInfo.Fields = fields |> List.map (_.Name)
                           Conjuncts = structConjuncts fields constraints
                           FieldDecls = fields
                           Declared = constraints
                           IsStatic = isStatic } : StaticEval.StructStaticInfo)
                        env.StructStatics }
        let fieldTypes = fields |> List.map (fun f -> (f.Name, lowerTypeExpr env f.Type))
        // Field range refinements: SEQUENTIAL scoping -- a bound may
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
        // via the SHARED helper -- StaticEval uses the same one for
        // fold-time checks, so the two worlds cannot drift.
        //
        // A `let static` INTEGER named in a conjunct (`max=n-1`, `abs(i -
        // j) <= w`) is a compile-time constant, so it is typed as the
        // LITERAL it folds to: `let static w = 2` is Int64, and as a value
        // it met an `Int<...>` (Int32) field with BL4001 although the
        // literal `2` in the same place compiled (bench gap G17, the style
        // guide's own band spelling). The conversion is CHECKED at compile
        // time: a constant outside Int32's range when the struct has an
        // Int32 field is refused (BL4001) with the fix -- widen the fields
        // to Int64<...>. Field names shadow statics.
        let staticOverflow : string option ref = ref None
        let allConstraints =
            let fieldNames = fields |> List.map (_.Name) |> Set.ofList
            let hasInt32Field =
                fields |> List.exists (fun f ->
                    match lowerTypeExpr env f.Type with
                    | IRTScalar ETInt32 -> true
                    | _ -> false)
            let fits (v: int64) =
                not hasInt32Field || (v >= int64 System.Int32.MinValue && v <= int64 System.Int32.MaxValue)
            let senv = lazy (Blade.TypeLower.staticEnvOf env)
            let asLiteral (e: Expr) (v: int64) =
                if v < 0L then inheritSpan e (ExprUnaryOp (OpNeg, inheritSpan e (ExprLit (LitInt (-v)))))
                else inheritSpan e (ExprLit (LitInt v))
            let rec inlineStatics (e: Expr) : Expr =
                // A FIELD-FREE subexpression naming a static (`n - 1`, `w`)
                // folds whole: `n - 1` as two literals would still default
                // to Int64 under the arithmetic.
                let free = StaticEval.collectFreeNames e
                // Only an Int32-field struct needs it: elsewhere the static's
                // own Int64 already matches, and nothing else changes.
                let namesStatic =
                    hasInt32Field
                    && not (Set.isEmpty free)
                    && free |> Set.forall (fun n -> not (fieldNames.Contains n))
                    && free |> Set.exists (fun n -> env.StaticValues.ContainsKey n)
                let folded =
                    if not namesStatic then None
                    else
                        match StaticEval.evalExpr senv.Value StaticEval.maxSteps e with
                        | Ok (StaticEval.SVInt v) when fits v -> Some (asLiteral e v)
                        | Ok (StaticEval.SVInt v) ->
                            if staticOverflow.Value.IsNone then
                                let names = String.concat ", " (Set.toList free)
                                staticOverflow.Value <- Some $"the static bound {v} (from {names}) does not fit the Int32 field type -- a static integer in a constraint converts to its field's type only when it fits; declare the fields Int64<min=..., max=...>"
                            None
                        | _ -> None
                match folded with
                | Some lit -> lit
                | None ->
                match e.Kind with
                | ExprKind.ExprVar _ | ExprKind.ExprLit _ -> e
                | ExprKind.ExprField (o, f) -> inheritSpan e (ExprField (inlineStatics o, f))
                | ExprKind.ExprApp (f, args) -> inheritSpan e (ExprApp (f, args |> List.map inlineStatics))
                | ExprKind.ExprBinOp (m, op, l, r) -> inheritSpan e (ExprBinOp (m, op, inlineStatics l, inlineStatics r))
                | ExprKind.ExprUnaryOp (op, i) -> inheritSpan e (ExprUnaryOp (op, inlineStatics i))
                | ExprKind.ExprIf (c, t, f) -> inheritSpan e (ExprIf (inlineStatics c, inlineStatics t, inlineStatics f))
                | ExprKind.ExprTuple es -> inheritSpan e (ExprTuple (es |> List.map inlineStatics))
                | ExprKind.ExprTyped (i, ty) -> inheritSpan e (ExprTyped (inlineStatics i, ty))
                | _ -> e
            structConjuncts fields constraints |> List.map inlineStatics
        match staticOverflow.Value with
        | Some why -> Error (StructWhereError (name, why))
        | None ->
        // Validate all conjuncts at declaration: fields bound, each
        // conjunct must typecheck to Bool. Hard errors -- a malformed
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
            registerDeclaredTypeDef srcName name (TDIStruct (name, typeParams, fieldTypes, allConstraints)) env))

and registerMutualGroupDecl (env: TypeEnv) (members: (Ident * TypeExpr) list) (constraints: Expr list) : TypeResult<TypeEnv> =
    // Members register as ordinary transparent aliases -- unannotated use
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
            // bare scalar members, folded statics. Call HEADS are open --
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
                | _ -> Error (MutualUnsupportedExpr ())
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

// 11b. Zonking -- Final Type Resolution
