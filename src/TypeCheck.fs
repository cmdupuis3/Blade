// The type-checking driver and public surface: checkModule/checkProgram,
// the IdePartial side-channel, and typeCheck -- the pipeline entry point
// that runs the elaborators (Unfold, ML, Sgs, Ppl, Math, Rand, Spectra,
// Display, Grad, IndexTypeValidator) and then the checker. Keeps the
// module name Blade.TypeCheck so external callers are untouched; the two
// helpers below re-export the remaining cross-module surface.
module Blade.TypeCheck

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
open Blade.TypeCheckInfer
open Blade.TypeCheckValidate

// 12. Module and Program

/// The type names one `type` declaration introduces (a mutual group names
/// every member).
let private typeDeclNames (td: TypeDecl) : string list =
    match td with
    | TyDeclAlias (n, _, _) | TyDeclStruct (n, _, _, _, _) | TyDeclSum (n, _, _) -> [ n ]
    | TyDeclMutualGroup (members, _) -> members |> List.map fst

/// BL2009 across MODULES, for the names that are not module-qualified yet.
///
/// Values and functions of a non-main module are namespaced at lowering
/// (Lowering.qualifyModuleNames), but a TYPE's identity is still its bare
/// name: `IRTNamed "P"`, an index type's `IRefNamed "Cell"`. So two modules
/// each declaring `struct P` emitted two global C++ `struct P` (a g++
/// redefinition) and the call judgment could not tell them apart -- `A.fx(q)`
/// accepted B's `P` -- and a main-module `type Cell = Idx<5>` became the SAME
/// nominal index type as an imported module's `Cell = Idx<3>`, so a
/// provenance check that exists to keep them apart passed. Until type
/// identity is keyed by (module, name), one program may declare a type name
/// once. Variant constructors are global C++ names (enum members, ctor
/// functions) and their tag table is keyed by bare name too, so they get the
/// same rule -- within a module as well, since two sum types there sharing a
/// constructor collide identically.
let private crossModuleDeclErrors (env: TypeEnv) (program: Program) : CompileError list =
    let errors = ResizeArray<CompileError>()
    let seenTypes = System.Collections.Generic.Dictionary<string, string * Span>()
    let seenCtors = System.Collections.Generic.Dictionary<string, string * Span>()
    // The error-location side channel still holds the LAST expression the
    // checker visited (in whichever module ran last); cleared per refusal so
    // each one points at its own declaration.
    let refuse (span: Span) (err: TypeError) =
        resetCurrentStmtSpan ()
        errors.Add (locateError span env err)
    let siteOf (first: Span) (here: Span) =
        let where = $"line {first.StartLine}, column {first.StartCol}"
        match first.File, here.File with
        | Some f1, Some f2 when f1 <> f2 -> $"{f1}, {where}"
        | _ -> where
    for m in program.Modules do
        let modName = m.Name |> String.concat "."
        for d in m.Decls do
            match d.Value with
            | DeclType td ->
                for n in typeDeclNames td do
                    match seenTypes.TryGetValue n with
                    | true, (firstMod, firstSpan) when firstMod <> modName ->
                        refuse d.Span (DuplicateDecl ("type", n, siteOf firstSpan d.Span, Some firstMod))
                    | true, _ -> ()   // same module: checkModule's own BL2009
                    | _ -> seenTypes.[n] <- (modName, d.Span)
                match td with
                | TyDeclSum (_, _, variants) ->
                    for v in variants do
                        match seenCtors.TryGetValue v.Name with
                        | true, (firstMod, firstSpan) ->
                            let crossModule = if firstMod <> modName then Some firstMod else None
                            refuse d.Span (DuplicateDecl ("constructor", v.Name, siteOf firstSpan d.Span, crossModule))
                        | _ -> seenCtors.[v.Name] <- (modName, d.Span)
                | _ -> ()
            | _ -> ()
    List.ofSeq errors

/// The lambda parameters a RANGE feeds, over a whole typed module (the one
/// place Zonk's subscript guard may trust a lambda parameter's index type --
/// SubscriptGuardCtx.RangeFedParams). A plain apply whose operand k is
/// `range<I>` / `0..n` feeds kernel parameter k; a single multi-slot
/// `range<Y, X>` feeds all of them. Collected BEFORE zonk because a kernel
/// lambda can be zonked (at its own `let`) before the apply that feeds it.
let rangeFedLambdaParams (modul: TypedModule) : System.Collections.Generic.HashSet<IRId> =
    let fed = System.Collections.Generic.HashSet<IRId>()
    let isRange (a: TypedExpr) =
        match a.Kind with
        | TExprRange _ | TExprDotDot _ -> true
        | _ -> false
    let rec walk (e: TypedExpr) =
        (match e.Kind with
         | TExprApply info when not info.IsComposeApply ->
             let operands =
                 info.Arrays |> List.collect (fun a ->
                     match a.Kind with
                     | TExprZip es -> es
                     | _ -> [ a ])
             let ps =
                 match info.Kernel.Kind with
                 | TExprLambda li | TExprReynolds ({ Kind = TExprLambda li }, _) -> li.Params
                 | _ -> []
             match operands with
             | [ { Kind = TExprRange ixs } ] when ixs.Length > 1 && ixs.Length = ps.Length ->
                 for p in ps do fed.Add p.VarId |> ignore
             | _ when operands.Length = ps.Length ->
                 List.iter2 (fun (p: TypedParam) a -> if isRange a then fed.Add p.VarId |> ignore) ps operands
             | _ -> ()
         | _ -> ())
        for c in typedExprChildren e do walk c
    for d in modul.Decls do
        for e in declExprs d do walk e
    fed

let checkModule (env: TypeEnv) (modul: ModuleDecl) : TypedModule * TypeEnv * CompileError list =
    // Fresh module: drop any span the PREVIOUS module's decl loop left in the
    // side-channel. The static-assertion errors below are raised before this
    // module's first `checkDecl` (which is where the per-decl reset lives), so
    // without this they would be located in the previous module's coordinates.
    // `typeCheck` resets on entry too; this covers module-to-module inside one
    // compilation, and callers that reach checkProgram by another route.
    resetCurrentStmtSpan ()
    // Fresh module: the NAME-KEYED callee-fact tables are shared by reference
    // across every module of the program (one emptyEnv), and a declaration only
    // ever SETS its entry. So a previous module's `f` (with a `mut` parameter,
    // defaults, a unit transform...) used to stay under the bare key `f` and
    // judge -- and be exported as -- THIS module's same-named `f`, which has
    // none of them: a false BL4005 on `B.f(arr)` because `A.f` wrote through
    // its first argument. This module's own declarations re-populate the bare
    // keys; imports re-register theirs from the per-module snapshot
    // (TypeModuleExport.Callees / Defaults) under `alias.name` or the selected
    // name. Nothing outside the checker reads these tables.
    // (The `mut`-position, co-iteration and unit tables are keyed by binder
    // id -- program-unique, so they need neither the clear nor a snapshot.)
    env.FuncConstraints.Clear()
    env.FuncDefaults.Clear()
    env.FuncDefaultCaptures.Clear()
    // Resolve compile-time-known static VALUES up front (the same
    // StaticEval.resolveStatics the lowering phase runs), so type-checking
    // can consult them (e.g. a `replicate` count written as `let static`).
    // `let static` is an assertion -- fold or fail loudly, not a silent
    // demotion to a runtime binding (lambda statics excepted). A circular
    // dependency, which would otherwise be silently swallowed, also lands
    // as an error on the first static decl.
    // Cross-module static import seeding (see the comment above this
    // function): seed env.StaticValues with imported entries so other
    // StaticValues consumers see them, AND splice literal substitutions
    // into a copy of this module's OWN static decls so resolveStatics's
    // fold assertion can see through a `let static x = M.k + 1` reference.
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
                        $"""`let static {(f.Names |> String.concat ", ")}` does not evaluate at compile time: {f.Reason}. `let static` asserts a compile-time value -- use plain `let` for values computed at runtime."""
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
                e.DeclaredFuncIds.Add funcVarId |> ignore
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
    // Module-scope names already declared, first span each (BL2009), one
    // map per namespace: values (`let`/`static`/`function` share one -- the
    // bool marks a function, which keeps the function-vs-function message),
    // types, units.
    let mutable declaredValues : Map<string, Span * bool> = Map.empty
    let mutable declaredTypes : Map<string, Span> = Map.empty
    let mutable declaredUnits : Map<string, Span> = Map.empty

    for d in modul.Decls do
        // BL2009 -- duplicate top-level `function` name. Without this the
        // later declaration silently rebinds the name (checkFunctionDecl's
        // bindVarSimple), and a call matching the FIRST signature dies with a
        // rank/type mismatch blaming the caller. Refuse here, at module decl
        // level, so nested `function`s (desugared to block lets, checked in
        // inferBlock) keep their legal shadowing of outer names, and imports
        // (DeclImport, not DeclFunction) cannot trip it. The duplicate decl is
        // NOT checked: references keep resolving against the first declaration,
        // so the refusal is the only diagnostic instead of the root cause plus
        // downstream mismatch noise. Prerequisite for same-name clause
        // dispatch (plan-match-statements.md §5 R1).
        //
        // The same refusal covers every other top-level namespace: a second
        // `let x` (or a `let x` beside a `function x`), a second `type`/
        // `struct`/sum type `T`, a second `Unit u`. A top-level re-`let`
        // passed the checker and then died in g++ as a redeclaration, while
        // the interpreter SHADOWED it -- two lanes, two meanings; a duplicate
        // type silently let the last declaration win. Wildcard `_` binds
        // nothing and never clashes.
        let firstSiteOf (firstSpan: Span) =
            let where = $"line {firstSpan.StartLine}, column {firstSpan.StartCol}"
            match firstSpan.File, d.Span.File with
            | Some f1, Some f2 when f1 <> f2 -> $"{f1}, {where}"
            | _ -> where
        let claim (names: string list) (table: Map<string, Span>) (kind: string) =
            match names |> List.tryPick (fun n -> Map.tryFind n table |> Option.map (fun sp -> (n, sp))) with
            | Some (n, sp) -> Some (DuplicateDecl (kind, n, firstSiteOf sp, None)), table
            | None -> None, (names |> List.fold (fun t n -> Map.add n d.Span t) table)
        let duplicateOf =
            match d.Value with
            | DeclFunction f ->
                match Map.tryFind f.Name declaredValues with
                | Some (firstSpan, true) -> Some (DuplicateFunctionDecl (f.Name, firstSiteOf firstSpan))
                | Some (firstSpan, false) -> Some (DuplicateDecl ("value", f.Name, firstSiteOf firstSpan, None))
                | None ->
                    declaredValues <- Map.add f.Name (d.Span, true) declaredValues
                    None
            | DeclLet b | DeclStatic b ->
                let names = patternNames b.Pattern |> List.filter (fun n -> n <> "_")
                match names |> List.tryPick (fun n -> Map.tryFind n declaredValues |> Option.map (fun (sp, _) -> (n, sp))) with
                | Some (n, sp) -> Some (DuplicateDecl ("value", n, firstSiteOf sp, None))
                | None ->
                    for n in names do declaredValues <- Map.add n (d.Span, false) declaredValues
                    None
            | DeclType td ->
                let err, table = claim (typeDeclNames td) declaredTypes "type"
                declaredTypes <- table
                err
            | DeclUnit u ->
                let err, table = claim [ u.Name ] declaredUnits "unit"
                declaredUnits <- table
                err
            | _ -> None
        match duplicateOf with
        | Some dupErr ->
            // The duplicate decl skips checkDecl, whose per-decl reset would
            // otherwise clear the PREVIOUS decl's expression span -- without
            // this, locateError's precision order picks that stale span and
            // the refusal points into the FIRST declaration's body.
            resetCurrentStmtSpan ()
            let ce = locateError d.Span currentEnv dupErr
            errors <- ce :: errors
        | None ->

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
            | DeclLet b -> $"""in let binding '{(match b.Pattern.Kind with PatternKind.PatVar n -> n | _ -> "_")}'"""
            | DeclStatic b -> $"""in static binding '{(match b.Pattern.Kind with PatternKind.PatVar n -> n | _ -> "_")}'"""
            | DeclFunction f -> $"in function '{f.Name}'"
            | DeclType td ->
                match td with
                | TyDeclAlias (n, _, _) | TyDeclStruct (n, _, _, _, _) | TyDeclSum (n, _, _) -> $"in type '{n}'"
                | TyDeclMutualGroup (members, _) ->
                    $"""in mutual group '{(members |> List.map fst |> String.concat ", ")}'"""
            | DeclInterface i -> $"in interface '{i.Name}'"
            | DeclImpl impl -> $"in impl for '{Blade.StructIdxFence.typeExprLabel impl.ForType}'"
            | DeclImport (qn, _) -> $"""in import '{(String.concat "." qn)}'"""
            | DeclUnit u -> $"in unit '{u.Name}'"
        let envWithCtx = pushContext declName currentEnv
        // A user error the type lowering found where it has no error channel
        // (TypeEnv.TypeErrorRaised) is this declaration's error like any other.
        let checked, raisedCode =
            try checkDecl envWithCtx d.Value, None
            with TypeErrorRaised (te, code) -> Error te, code
        match checked with
        | Ok (td, env') ->
            decls <- td :: decls
            // Carry forward env' but restore original context (don't nest)
            currentEnv <- { env' with Context = currentEnv.Context }
        | Error err ->
            let ce = { locateError d.Span currentEnv err with Code = raisedCode }
            errors <- ce :: errors
            // Continue with pre-failure env, but bind the failed decl's
            // name(s) to a FRESH inference var so downstream references
            // resolve to *some* type instead of erroring `Unbound variable`.
            // Without this, one bad annotation smears ~N spurious
            // Unbound-variable diagnostics across the rest of the module and
            // buries the real root cause. A fresh (unsolved) var unifies with
            // anything, so it silences the cascade without manufacturing a
            // second layer of false errors the way binding the *annotation*
            // type would. (Value bindings only -- type/interface/impl/import/
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
    // The zonk walk also retypes index POSITIONS and guards unproven
    // subscripts into named index types (Zonk.fs, SUBSCRIPT POSITIONS AND
    // GUARDS); its context carries the builder for guard bindings.
    let zonked =
        let saved = subscriptGuardCtx.Value
        subscriptGuardCtx.Value <-
            Some { FreshId = (fun () -> currentEnv.Builder.FreshId())
                   Positions = System.Collections.Generic.HashSet<IRId>()
                   DataVars = System.Collections.Generic.HashSet<IRId>()
                   RangeFedParams = rangeFedLambdaParams typedModule
                   EnumLabels = fun tag ->
                       match Map.tryFind tag currentEnv.TypeDefs with
                       | Some (TDIEnumIdx (_, _, values, _)) when EnumValue.allString values ->
                           Some (values |> List.choose (function EVString s -> Some s | _ -> None))
                       | _ -> None
                   IndexExtent = fun tag ->
                       match Map.tryFind tag currentEnv.TypeDefs with
                       | Some (TDIIndexType (_, idx, _)) when idx.IxKind = IxKPlain && idx.Symmetry = SymNone ->
                           tryEvalIntIR idx.Extent
                       | _ -> None }
        try zonkModule currentEnv.Subst typedModule
        finally subscriptGuardCtx.Value <- saved
    // Late direct-application rank check, on the zonked tree -- see
    // collectAppRankErrors. Suppressed when the module already has errors:
    // a failed decl binds its name to a fresh var (the cascade guard above),
    // and calls through that var would report rank noise on top of the real
    // root cause.
    let rankErrors =
        if List.isEmpty errors && List.isEmpty staticAssertErrors then
            zonked.Decls |> List.collect declExprs
                         |> List.collect (collectAppRankErrors currentEnv.Subst)
        else []
    // Tree-slot laundering and same-rank tree-shape mismatch at direct
    // applications -- the safety half of P5's open signature door, sharing this
    // sweep's zonked-module reasoning and its cascade suppression (a failed decl
    // binds a fresh var, and calls through it would report tree noise over the
    // real root cause). See collectAppTreeErrors: both refusals were measured to
    // pass silently before it existed.
    let treeArgErrors =
        if List.isEmpty errors && List.isEmpty staticAssertErrors then
            zonked.Decls |> List.collect declExprs
                         |> List.collect (collectAppTreeErrors currentEnv.Subst)
        else []
    // The subscript judgment's post-zonk half (collectSubscriptErrors): the
    // class and nominal rules over every subscript and every index-typed
    // call argument, now that no kernel parameter is still open. Same
    // cascade suppression as the rank sweep.
    let subscriptErrors =
        if List.isEmpty errors && List.isEmpty staticAssertErrors then
            zonked.Decls |> List.collect declExprs
                         |> List.collect (collectSubscriptErrors currentEnv)
        else []
    // Misplaced provider writes: structural, inference-independent (an
    // unresolved receiver simply fails the IRTNamed match), so unlike the rank
    // sweep it runs even when the module already has errors.
    let writeErrors =
        zonked.Decls |> List.collect declWriteRoots
                     |> List.collect (fun (nested, e) -> collectMisplacedProviderWrites currentEnv.Subst nested e)
    // group_keys escapes: structural like the write sweep (IRTGroupKeys is
    // minted in exactly one place and never inferred), so it runs even when
    // the module already has errors.
    let groupKeysErrors =
        zonked.Decls |> List.collect declGroupKeysRoots
                     |> List.collect (fun (pos, e) -> collectGroupKeysEscapes currentEnv.Subst pos e)
    (zonked, currentEnv, staticAssertErrors @ List.rev errors @ rankErrors @ treeArgErrors @ subscriptErrors @ writeErrors @ groupKeysErrors)

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
            // Snapshot NOW: the tables are shared by reference and name-keyed,
            // so the next module's `f` would overwrite this module's entry.
            Defaults =
                finalEnv.FuncDefaults
                |> Seq.filter (fun kv -> not (kv.Key.Contains(".")) && Map.containsKey kv.Key finalEnv.Variables)
                |> Seq.map (fun kv -> (kv.Key, kv.Value))
                |> Map.ofSeq
            DefaultCaptures =
                finalEnv.FuncDefaultCaptures
                |> Seq.filter (fun kv -> not (kv.Key.Contains(".")) && Map.containsKey kv.Key finalEnv.Variables)
                |> Seq.map (fun kv -> (kv.Key, kv.Value))
                |> Map.ofSeq
            // Same snapshot, same reason, for the declaration facts the call
            // judgment reads by name (TypeModuleExport.Callees).
            Callees =
                let snap (d: System.Collections.Generic.Dictionary<string, 'v>) : Map<string, 'v> =
                    d
                    |> Seq.filter (fun kv -> not (kv.Key.Contains(".")) && Map.containsKey kv.Key finalEnv.Variables)
                    |> Seq.map (fun kv -> (kv.Key, kv.Value))
                    |> Map.ofSeq
                { Constraints = snap finalEnv.FuncConstraints }
        }
        moduleExports <- Map.add moduleName export moduleExports
    allErrors <- allErrors @ crossModuleDeclErrors env program
    // env.Warnings is shared by reference across all envWithExports updates
    // (mutable ResizeArray, not a Map), so all module-scope warnings
    // accumulate here.
    let warnings = env.Warnings |> Seq.toList
    ({ Modules = List.rev modules }, env.Builder, allErrors, warnings)

// 13. Public Entry Point

/// Type check a program. Returns the typed program, builder, and any
/// non-fatal warnings in the Ok case; or compile errors in the Error case.
/// (Warnings emitted before a hard error is encountered are currently
/// dropped on the Error path; that's a separate refinement.)
///
/// Pre-pass: IndexTypeValidator enforces the rules for where index types may
/// appear in declaration-level type expressions. Validation errors abort
/// compilation early -- once an AST passes validation, downstream lowering
/// can assume index types only appear in their permitted positions.
/// IDE side-channel: the partial typed program + builder from the most recent
/// checkProgram. typeCheck discards these when the checker reports errors, but
/// editor tooling (Ide.fs) still wants bindings/types for the parts that DID
/// check, so a file with errors keeps its hovers. Reset at the top of typeCheck
/// and recorded after checkProgram; None means the pre-check pipeline failed
/// (no typed program produced). AsyncLocal, like ProviderRegistry.IdeStores.
module IdePartial =
    let private slot = new System.Threading.AsyncLocal<(TypedProgram * IRBuilder) option>()
    let reset () = slot.Value <- None
    let record (tp: TypedProgram) (b: IRBuilder) = slot.Value <- Some (tp, b)
    let get () : (TypedProgram * IRBuilder) option =
        match box slot.Value with null -> None | _ -> slot.Value

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
    // A fresh fold log for this program (the run record's `content`-identity
    // inputs; see ProviderStatics.resetFoldLog).
    Blade.ProviderStatics.resetFoldLog ()
    // The constrained-index counting layer's `idx_card(R)` builtin, on the
    // same footing and for the same reason: registered before ANY
    // resolveStatics pass, so every elaboration's own statics can size
    // against it.
    Blade.StructIdxSpec.install ()
    // The `__ad_body` conjunct Grad stamps on synthesized derivatives (its
    // body scope vetoes implicit unit-scale conversions -- Constraints.fs).
    Blade.Constraints.registerAdBody ()
    IdePartial.reset ()
    PinSuggestions.reset ()
    WarningLog.reset ()
    DeducedFacts.reset ()
    // Error-location side-channel: AsyncLocal, so without this a second
    // compilation in one process inherits the FIRST one's last-stamped span.
    // `checkDecl` resets it per declaration, but errors raised before any
    // declaration is checked -- the elaborations below, and `checkModule`'s
    // `let static` fold assertion -- run before that and would otherwise be
    // located in the previous source's coordinates. The real exposure is the
    // long-lived `blade ide check` path, not just the test host.
    resetCurrentStmtSpan ()
    // Phase B typed rep-deduction channel: same lifecycle as the facts channel
    // beside it. The per-module SUMMARY tables need no reset -- they hang off
    // the TypeEnv that `emptyEnv ()` builds fresh below -- but the proposal
    // channel and the skipped-polymorphic tally are AsyncLocal and would
    // otherwise accumulate across compilations in one process (the test host).
    Blade.DeduceRep.TypedCertProposals.reset ()
    Blade.DeduceRep.SkippedPolymorphic.reset ()
    // The rep-check's two channels, same lifecycle: the disagreement list must not
    // carry across compilations (it becomes compile errors), and the census
    // must count THIS program's certified decls, not the test host's history.
    Blade.DeduceRep.RepCheckDisagreements.reset ()
    Blade.DeduceRep.RepCheckCensus.reset ()
    // Register the typed polynomial engine as DeduceRep's discharger.
    // DeduceRep compiles at index 29 and cannot name Blade.ML.PolyExtractTyped
    // (index 120), so the dependency is inverted through the hook slot, tied
    // here where both are visible. Registered at every `typeCheck` entry
    // (idempotent) so the production adapter is ALWAYS installed for a real
    // compilation even if a test cleared the slot; `EngineDischarge.clear ()`
    // stays usable for test isolation.
    //
    // LIEGUARDFAILURE IS CONVERTED, NOT SWALLOWED: `engineVerdict` deliberately
    // RE-RAISES `LieDischarge.LieGuardFailure` (the post-accept float guard, a
    // compiler-bug assert, not a decoder refusal). Left alone it would be
    // eaten by an outer try/with and lost silently. Catching it HERE and
    // returning a refutation preserves its meaning: in CHECKING it surfaces
    // as a disagreement (correct for an internal guard trip); in DEDUCTION
    // it is simply a decline. No other exception gets this treatment.
    Blade.DeduceRep.EngineDischarge.register (fun resolve parms sg body ->
        try
            match Blade.ML.PolyExtractTyped.engineVerdict resolve parms sg body with
            | Some Blade.ML.PolyExtractTyped.EngineHolds ->
                Some Blade.DeduceRep.EngineConfirms
            | Some (Blade.ML.PolyExtractTyped.EngineRefutes msg) ->
                Some (Blade.DeduceRep.EngineRefutes msg)
            | None -> None
        with Blade.ML.LieDischarge.LieGuardFailure msg ->
            Some (Blade.DeduceRep.EngineRefutes
                    $"the Lie-discharge post-accept guard tripped while validating this body: {msg}"))
    IdeDeductions.reset ()
    // Provider checkout desugar BEFORE EVERYTHING: `repo.checkout("v1.0",
    // ic.tag)` becomes the ordinary load shape `ic.load("path@tag:v1.0")`,
    // so typecheck, every elaborator's own `resolveStatics`, and
    // `checkModule`'s provider-roots scan all see a form they already
    // handle. It has to be first because Unfold, immediately below, is
    // itself the earliest `resolveStatics` consumer -- and StaticEval's miss
    // would be SILENT ("not foldable"), not an error. No-op, and
    // reference-equal, for programs with no `import icechunk`.
    match Blade.ProviderDesugar.expand program with
    | Error diags -> Error (diags |> List.map (compileErrorOfDiagnostic ["provider checkout desugar"]))
    | Ok program ->
    // Staged-former unfold FIRST of the elaborations: `static
    // method_for/object_for/for` argument lists elaborate to plain formers
    // before any other stage (ML/PPL/math/grad and the checker never see
    // ExprStatic).
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
    // display LAST of the module elaborations: a frame is a side effect on an
    // already-elaborated payload, so nothing downstream needs to see the
    // surface `alias.emit(...)` call.
    match Blade.Display.Elaborate.expand program with
    | Error diags -> Error (diags |> List.map (compileErrorOfDiagnostic ["display elaboration"]))
    | Ok program ->
    // C7: fuse `>>@` / `@>>` / `<$>` pipelines into ordinary maps for EVERY
    // program, not just differentiated ones. The proved Compose-Apply
    // identity (formalism.md 10.3), so it cannot change an answer -- and it
    // is what makes three-stage chains, multi-operand pipelines, and
    // function-body `let p = o1 >>@ o2` / `f <$> c` / `c1 @>> c2` emit at
    // all. Shapes it declines fall through to the IRComposeApply path
    // unchanged. Runs BEFORE grad expansion so AD inherits fused bodies.
    let program = Blade.Grad.fuseProgram program
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
        IdePartial.record tp builder
        // Certificate suggestions (BL4011 equivariance, BL4014 galilean) ride
        // the ordinary warning channel like the BL4010 storage pins: plain
        // strings here (what the CLI prints), structured (message, span)
        // pairs in Equiv.CertSuggestions / Galilean.GalCertSuggestions (what
        // the editor ghost-renders). Appended AFTER the checker's own --
        // equiv then galilean -- the same order
        // `Lowering.typeCheckWarningDiagnostics` assembles, so strings and
        // diagnostics stay parallel.
        let warnings =
            warnings
            @ (Blade.ML.Equiv.CertSuggestions.get () |> List.map fst)
            @ (Blade.ML.Galilean.GalCertSuggestions.get () |> List.map fst)
        // Drain the declared-certificate agreement channel. An entry here
        // means the typed walker and the seam checker reached CONTRADICTORY
        // judgments about the same certified body -- not the user's fault,
        // since the seam already accepted the program. Surfaces as an
        // INTERNAL COMPILER ERROR (BL9004) and stops the build: a compiler
        // that knows two of its own judgments disagree must not quietly
        // emit code. Abstentions are silent by construction and never reach
        // here.
        let repIce =
            Blade.DeduceRep.RepCheckDisagreements.get ()
            |> List.map (fun (owner, detail, span) ->
                { Error =
                    Other $"internal compiler error: equivariance certificate validation disagrees with the elaboration checker for '{owner}': {detail}. This is a bug in the Blade compiler, not in your program -- please report it (the certificate itself was accepted by the checking authority; only the typed second opinion dissents)"
                  Span = span
                  Context = [ "equiv certificate validation" ]
                  Code = Some "BL9004" })
        let errors = errors @ repIce
        if errors.IsEmpty then Ok (tp, builder, warnings)
        else Error errors

// Re-exported so Ide.fs/Cli keep their single Blade.TypeCheck surface for
// these two; the AsyncLocal channel MODULES cannot be re-exported (module
// abbreviations are file-private in F#), so their consumers open
// Blade.TypeCheckIde directly.
let typedExprChildren = TypeCheckSupport.typedExprChildren
let builtinScalarNames = TypeLower.builtinScalarNames
