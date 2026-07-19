module Blade.StaticEval

open Blade.Ast

// ============================================================================
// Static Value Types
// ============================================================================

/// Compile-time evaluated values
type StaticValue =
    | SVInt of int64
    | SVFloat of float
    | SVBool of bool
    | SVString of string
    | SVUnit
    | SVTuple of StaticValue list
    /// A folded struct literal: the type name plus (field, value) pairs in
    /// DECLARATION order (when the struct registry is in scope — always the
    /// case for module-level folds). Keeping the name and field names lets
    /// splice-back emit a designated struct literal instead of a tuple, so
    /// runtime field access on a `let static` struct stays well-typed.
    | SVStruct of name: string * fields: (string * StaticValue) list

/// A static function definition (unevaluated — applied during evaluation)
type StaticFuncDef = {
    Name: string
    Params: string list
    Body: Expr
}

/// Struct constraint info for fold-time checks: field names in declaration
/// order plus the FULL conjunct list (declared where-conjuncts + desugared
/// field bounds, built with Ast.structConjuncts — the same helper the type
/// checker uses, so the two worlds cannot drift).
type StructStaticInfo = {
    Fields: string list
    Conjuncts: Expr list
}

/// Environment for static evaluation
type StaticEnv = {
    Values: Map<string, StaticValue>
    Functions: Map<string, StaticFuncDef>
    /// Accumulates names of functions called during evaluation
    CalledFunctions: ref<Set<string>>
    /// Provider-backed roots in scope: binding name (`sample` from
    /// `let sample = nc.load("f.nc")`) → (provider module name, store
    /// path). Consulted by the provider-read fold — staging contract
    /// clause 1: a closed input is an argument the program was applied
    /// to, so a `let static` read may fold its payload at compile time.
    ProviderRoots: Map<string, string * string>
    /// Constrained-struct registry for fold-time conjunct checks. Empty in
    /// contexts that never fold user struct literals (angle-bracket args).
    Structs: Map<string, StructStaticInfo>
}

// ============================================================================
// Dependency Analysis
// ============================================================================

/// Collect all free variable names referenced in an expression.
/// Does NOT descend into type annotations (those are handled in Phase 4).
let rec collectFreeNames (expr: Expr) : Set<string> =
    match expr.Kind with
    | ExprKind.ExprLit _ -> Set.empty
    | ExprKind.ExprVar name -> Set.singleton name
    | ExprKind.ExprBinOp (_, _, l, r) -> Set.union (collectFreeNames l) (collectFreeNames r)
    | ExprKind.ExprUnaryOp (_, e) -> collectFreeNames e
    | ExprKind.ExprApp (f, args) ->
        Set.union (collectFreeNames f) (args |> List.map collectFreeNames |> Set.unionMany)
    | ExprKind.ExprIf (c, t, e) ->
        [c; t; e] |> List.map collectFreeNames |> Set.unionMany
    | ExprKind.ExprTuple es | ExprKind.ExprArrayLit es ->
        es |> List.map collectFreeNames |> Set.unionMany
    | ExprKind.ExprField (obj, _) -> collectFreeNames obj
    | ExprKind.ExprLet (binding, body) ->
        let valRefs = collectFreeNames binding.Value
        let boundName = match binding.Pattern.Kind with PatternKind.PatVar n -> Set.singleton n | _ -> Set.empty
        Set.union valRefs (Set.difference (collectFreeNames body) boundName)
    | ExprKind.ExprMatch (scrut, cases) ->
        let scrutRefs = collectFreeNames scrut
        let caseRefs = cases |> List.map (fun c ->
            let patBinds = collectPatternBindings c.Pattern
            let guardRefs = c.Guard |> Option.map collectFreeNames |> Option.defaultValue Set.empty
            let bodyRefs = collectFreeNames c.Body
            Set.union guardRefs (Set.difference bodyRefs patBinds)) |> Set.unionMany
        Set.union scrutRefs caseRefs
    | ExprKind.ExprBlock (stmts, finalExpr) ->
        let stmtRefs = stmts |> List.map collectStmtNames |> Set.unionMany
        let finalRefs = finalExpr |> Option.map collectFreeNames |> Option.defaultValue Set.empty
        Set.union stmtRefs finalRefs
    | ExprKind.ExprStruct (_, fields, spread) ->
        let spreadRefs = spread |> Option.map collectFreeNames |> Option.defaultValue Set.empty
        Set.union spreadRefs (fields |> List.map (snd >> collectFreeNames) |> Set.unionMany)
    | ExprKind.ExprTyped (e, _) -> collectFreeNames e
    | ExprKind.ExprLambda (_, _, body) -> collectFreeNames body  // params are local
    | _ -> Set.empty  // conservative for loop/combinator forms

and collectPatternBindings (pat: Pattern) : Set<string> =
    match pat.Kind with
    | PatternKind.PatVar name -> Set.singleton name
    | PatternKind.PatTuple pats -> pats |> List.map collectPatternBindings |> Set.unionMany
    | PatternKind.PatVariant (_, Some p) -> collectPatternBindings p
    | PatternKind.PatStruct (_, fields) -> fields |> List.map (snd >> collectPatternBindings) |> Set.unionMany
    | PatternKind.PatGuarded (p, _) -> collectPatternBindings p
    | PatternKind.PatTyped (p, _) -> collectPatternBindings p
    | _ -> Set.empty

and collectStmtNames (stmt: Stmt) : Set<string> =
    match stmt with
    | StmtSpanned (inner, _) -> collectStmtNames inner
    | StmtLet binding -> collectFreeNames binding.Value
    | StmtAssign (lhs, _, rhs) -> Set.union (collectFreeNames lhs) (collectFreeNames rhs)
    | StmtExpr e -> collectFreeNames e
    | StmtForIn (_, range, body) ->
        Set.union (collectFreeNames range) (body |> List.map collectStmtNames |> Set.unionMany)

/// Topological sort: given a map of name → dependencies, return an evaluation order.
/// Returns Error with cycle members if a cycle exists.
let topoSort (deps: Map<string, Set<string>>) : Result<string list, string list> =
    let mutable result = []
    let mutable remaining = deps

    let mutable changed = true
    while changed && not remaining.IsEmpty do
        changed <- false
        // Find all nodes whose dependencies are fully resolved (not in remaining)
        let ready =
            remaining |> Map.filter (fun _ depSet ->
                depSet |> Set.forall (fun d -> not (Map.containsKey d remaining)))
        if not (Map.isEmpty ready) then
            changed <- true
            for KeyValue(name, _) in ready do
                result <- result @ [name]
                remaining <- Map.remove name remaining

    if remaining.IsEmpty then Ok result
    else Error (remaining |> Map.toList |> List.map fst)

// ============================================================================
// External builtin registry
// ============================================================================

/// Extension point: domain layers register additional static builtins here
/// (name -> evaluated args -> result). The evaluator consults the registry
/// only after its own builtin table misses, so core names cannot be
/// overridden. Current registrant: the ML module's sizing builtins
/// (ml/compiler/MLStatics.fs, installed by MLElaborate.expand).
let private externalBuiltins =
    System.Collections.Concurrent.ConcurrentDictionary<string, StaticValue list -> Result<StaticValue, string>>()

/// Register (idempotently — last write wins) an external static builtin.
let registerStaticBuiltin (name: string) (f: StaticValue list -> Result<StaticValue, string>) =
    externalBuiltins.[name] <- f

/// Names the static evaluator can call: the core builtin table (must match
/// evalBuiltin's arms) plus everything in the external registry. Used by
/// constraint validation to reject calls that could never fold.
let knownBuiltinNames () : Set<string> =
    let core =
        [ "exp"; "log"; "sqrt"; "sin"; "cos"; "tan"
          "sinh"; "cosh"; "tanh"; "asin"; "acos"; "atan"
          "floor"; "ceil"; "abs"; "min"; "max"; "length"; "prodsum" ]
    Set.union (Set.ofList core) (externalBuiltins.Keys |> Set.ofSeq)

/// Extension point: the provider layer registers its compile-time DATA
/// reader here ((providerName, storePath, varName) → folded value) — see
/// ProviderStatics.install. Kept behind a hook so this module stays free
/// of provider/IR dependencies (same layering rule as the builtin
/// registry above). When absent or failing, a `let static ... |> alias.read`
/// fails the fold assertion with the reader's message.
let mutable private providerReader : (string -> string -> string -> Result<StaticValue, string>) option = None

let registerProviderReader (f: string -> string -> string -> Result<StaticValue, string>) =
    providerReader <- Some f

/// Extension point: the set of registered provider MODULE names ("netcdf",
/// "zarr", ...), used by resolveStatics to recognize provider imports
/// (`import netcdf as nc`) without referencing the provider registry from
/// here (same layering rule as the reader hook above).
let mutable private providerModuleNames : Set<string> = Set.empty

let registerProviderNames (names: Set<string>) =
    providerModuleNames <- names

let isProviderModuleName (name: string) : bool =
    Set.contains name providerModuleNames

// ============================================================================
// Expression Evaluator
// ============================================================================

let maxSteps = 100_000

/// Fold a provider read's operand (`root.vars.A` / `root.dims.x`) through
/// the registered compile-time reader. Shared by the qualified-application
/// form (`alias.read(inner)`) and the legacy ExprRead node.
let private foldProviderRead (env: StaticEnv) (inner: Expr) : Result<StaticValue, string> =
    let resolved =
        match inner.Kind with
        | ExprKind.ExprField ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar root }, _) }, varName)
        | ExprKind.ExprField ({ Kind = ExprKind.ExprVar root }, varName) ->
            Map.tryFind root env.ProviderRoots
            |> Option.map (fun (provider, path) -> (provider, path, varName))
        | _ -> None
    match resolved, providerReader with
    | Some (provider, path, varName), Some reader -> reader provider path varName
    | Some _, None ->
        Error "Static evaluation: no compile-time provider reader is installed (provider data folds need the provider's runtime loadable by the compiler)"
    | None, _ ->
        Error "Static evaluation: `alias.read(...)` folds only over a provider-backed variable (root.vars.<name> where root = alias.load(\"store\"))"

let rec evalExpr (env: StaticEnv) (fuel: int) (expr: Expr) : Result<StaticValue, string> =
    if fuel <= 0 then
        Error "Static evaluation: step limit exceeded (possible infinite recursion)"
    else
    match expr.Kind with
    | ExprKind.ExprLit (LitInt n) -> Ok (SVInt n)
    | ExprKind.ExprLit (LitFloat f) -> Ok (SVFloat f)
    | ExprKind.ExprLit (LitBool b) -> Ok (SVBool b)
    | ExprKind.ExprLit (LitString s) -> Ok (SVString s)
    | ExprKind.ExprLit LitUnit -> Ok SVUnit

    | ExprKind.ExprVar name ->
        match Map.tryFind name env.Values with
        | Some v -> Ok v
        | None ->
            // Could be a static function used as a value (shouldn't happen normally)
            Error (sprintf "Static evaluation: undefined variable '%s'" name)

    | ExprKind.ExprBinOp (_, op, l, r) ->
        evalExpr env (fuel - 1) l |> Result.bind (fun lv ->
        evalExpr env (fuel - 1) r |> Result.bind (fun rv ->
            evalBinOp op lv rv))

    | ExprKind.ExprUnaryOp (op, e) ->
        evalExpr env (fuel - 1) e |> Result.bind (fun v ->
            match op, v with
            | OpNeg, SVInt n -> Ok (SVInt (-n))
            | OpNeg, SVFloat f -> Ok (SVFloat (-f))
            | OpNot, SVBool b -> Ok (SVBool (not b))
            | _ -> Error (sprintf "Static evaluation: cannot apply %A to %A" op v))

    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar fname }, args) ->
        match Map.tryFind fname env.Functions with
        | Some funcDef ->
            env.CalledFunctions.Value <- Set.add fname env.CalledFunctions.Value
            evalArgs env (fuel - 1) args |> Result.bind (fun argVals ->
                if argVals.Length <> funcDef.Params.Length then
                    Error (sprintf "Static function '%s' expects %d args, got %d"
                               fname funcDef.Params.Length argVals.Length)
                else
                    let bodyEnv =
                        (funcDef.Params, argVals) ||> List.zip
                        |> List.fold (fun e (p, v) ->
                            { e with Values = Map.add p v e.Values }) env
                    evalExpr bodyEnv (fuel - 1) funcDef.Body)
        | None ->
            // Try as a built-in static function
            evalBuiltin env fuel fname args

    // Provider payload fold: `alias.read(root.vars.A)` (equivalently
    // `root.vars.A |> alias.read`) where root is a provider-backed binding
    // (env.ProviderRoots). The registered reader (ProviderStatics) pulls
    // the data through the provider at compile time — the same value the
    // runtime read would produce, so folding is unobservable except in
    // cost (clause 1). Matched by the "read" field name; the operand's
    // root decides the provider, so a non-provider `alias.read(...)`
    // falls out with foldProviderRead's steering error.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar _alias }, "read") }, [inner]) ->
        foldProviderRead env inner

    | ExprKind.ExprApp (func, args) ->
        // Non-variable function position — try evaluating
        Error (sprintf "Static evaluation: unsupported function form in call")

    | ExprKind.ExprIf (cond, thenBr, elseBr) ->
        evalExpr env (fuel - 1) cond |> Result.bind (fun cv ->
            match cv with
            | SVBool true -> evalExpr env (fuel - 1) thenBr
            | SVBool false -> evalExpr env (fuel - 1) elseBr
            | _ -> Error "Static evaluation: if condition must be Bool")

    | ExprKind.ExprTuple es ->
        evalArgs env (fuel - 1) es |> Result.map SVTuple

    | ExprKind.ExprArrayLit es ->
        evalArgs env (fuel - 1) es |> Result.map SVTuple  // static arrays as tuples

    | ExprKind.ExprLet (binding, body) ->
        evalExpr env (fuel - 1) binding.Value |> Result.bind (fun v ->
            let env' = bindPattern env binding.Pattern v
            evalExpr env' (fuel - 1) body)

    | ExprKind.ExprMatch (scrutinee, cases) ->
        evalExpr env (fuel - 1) scrutinee |> Result.bind (fun sv ->
            evalMatch env (fuel - 1) sv cases)

    | ExprKind.ExprBlock (stmts, finalExpr) ->
        evalBlock env (fuel - 1) stmts finalExpr

    // Module-qualified static access (`M.k`): imported statics are seeded
    // into Values under their qualified name by checkModule's pre-pass
    // (TypeModuleExport.StaticValues) — consult that before treating the
    // field access as a structural read.
    | ExprKind.ExprField ({ Kind = ExprKind.ExprVar objName }, field) when Map.containsKey (sprintf "%s.%s" objName field) env.Values ->
        Ok env.Values.[sprintf "%s.%s" objName field]

    | ExprKind.ExprField (obj, field) ->
        evalExpr env (fuel - 1) obj |> Result.bind (fun ov ->
            match ov with
            | SVStruct (sname, sfields) ->
                match sfields |> List.tryFind (fun (fn, _) -> fn = field) with
                | Some (_, v) -> Ok v
                | None -> Error (sprintf "Static evaluation: struct %s has no field '%s'" sname field)
            | _ -> Error (sprintf "Static evaluation: field access '%s' not supported on static values" field))

    | ExprKind.ExprStruct (name, fields, spread) ->
        // Evaluate all field values — stored as an SVStruct (name + named
        // fields) so the folded value keeps nominal identity and splices
        // back as a designated struct literal. A `..base` spread folds the
        // base and inherits its missing fields by name. A CONSTRAINED struct
        // folding here is in the
        // compile-time world: run its conjuncts with the field values bound
        // by name, and fail the fold on violation (let-static assertion
        // semantics) instead of waiting for a runtime guard.
        let providedR =
            fields |> List.map (fun (fn, e) -> evalExpr env (fuel - 1) e |> Result.map (fun v -> (fn, v)))
            |> List.fold (fun acc r ->
                acc |> Result.bind (fun xs -> r |> Result.map (fun x -> xs @ [x]))) (Ok [])
        let fieldValsR =
            providedR |> Result.bind (fun provided ->
                match spread with
                | None -> Ok provided
                | Some baseExpr ->
                    match Map.tryFind name env.Structs with
                    | None -> Error (sprintf "Static evaluation: cannot fold '..' spread for struct %s (unknown field layout)" name)
                    | Some info ->
                        evalExpr env (fuel - 1) baseExpr |> Result.bind (fun bv ->
                            match bv with
                            | SVStruct (_, bfields) when bfields.Length = info.Fields.Length ->
                                let providedNames = provided |> List.map fst
                                let inherited =
                                    bfields |> List.filter (fun (fn, _) -> not (List.contains fn providedNames))
                                Ok (provided @ inherited)
                            | SVTuple bvals when bvals.Length = info.Fields.Length ->
                                let providedNames = provided |> List.map fst
                                let inherited =
                                    List.zip info.Fields bvals
                                    |> List.filter (fun (fn, _) -> not (List.contains fn providedNames))
                                Ok (provided @ inherited)
                            | _ -> Error (sprintf "Static evaluation: '..' spread base for struct %s did not fold to a %d-field struct" name info.Fields.Length)))
        fieldValsR
        |> Result.bind (fun fieldVals ->
            // Field order follows DECLARATION order when known (the spread
            // path requires it, and splice-back emits C++ designated
            // initializers which demand it); plain literals with an unknown
            // layout keep written order, matching the pre-spread behavior.
            let orderedFields =
                match Map.tryFind name env.Structs with
                | Some info when info.Fields.Length = fieldVals.Length
                              && (info.Fields |> List.forall (fun f -> fieldVals |> List.exists (fun (fn, _) -> fn = f))) ->
                    info.Fields |> List.map (fun f -> fieldVals |> List.find (fun (fn, _) -> fn = f))
                | _ -> fieldVals
            let result = SVStruct (name, orderedFields)
            match Map.tryFind name env.Structs with
            | Some info when not info.Conjuncts.IsEmpty ->
                let bodyEnv =
                    { env with Values = fieldVals |> List.fold (fun m (fn, v) -> Map.add fn v m) env.Values }
                let total = info.Conjuncts.Length
                let rec checkAll i cs =
                    match cs with
                    | [] -> Ok result
                    | (c: Expr) :: rest ->
                        // PPL license conjuncts (`__ppl_indep(...)`) are
                        // static licenses, not value predicates — present
                        // only at the pre-elaborator Unfold call site; skip.
                        let isPplLicense =
                            match c.Kind with
                            | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar f }, _) -> f.StartsWith "__ppl_"
                            | _ -> false
                        if isPplLicense then checkAll (i + 1) rest
                        else
                            match evalExpr bodyEnv (fuel - 1) c with
                            | Ok (SVBool true) -> checkAll (i + 1) rest
                            | Ok (SVBool false) ->
                                if total = 1 then Error (sprintf "Constraint violation in %s (static)" name)
                                else Error (sprintf "Constraint violation in %s (static, conjunct %d)" name i)
                            | Ok _ -> Error (sprintf "constraint of %s is not a boolean at compile time" name)
                            | Error why -> Error (sprintf "constraint of %s cannot fold: %s" name why)
                checkAll 1 info.Conjuncts
            | _ -> Ok result)

    | ExprKind.ExprRead inner ->
        // Legacy AST node (no longer produced by the parser); folds the
        // same way as the qualified-application form above.
        foldProviderRead env inner

    | _ ->
        Error (sprintf "Static evaluation: unsupported expression form")

and evalArgs env fuel (args: Expr list) : Result<StaticValue list, string> =
    args |> List.map (evalExpr env fuel) |> seqResults

and seqResults (results: Result<StaticValue, string> list) : Result<StaticValue list, string> =
    results |> List.fold (fun acc r ->
        match acc, r with
        | Ok xs, Ok x -> Ok (xs @ [x])
        | Error e, _ -> Error e
        | _, Error e -> Error e) (Ok [])

and bindPattern (env: StaticEnv) (pat: Pattern) (value: StaticValue) : StaticEnv =
    match pat.Kind with
    | PatternKind.PatVar name -> { env with Values = Map.add name value env.Values }
    | PatternKind.PatTuple pats ->
        match value with
        | SVTuple vs when vs.Length = pats.Length ->
            (pats, vs) ||> List.zip |> List.fold (fun e (p, v) -> bindPattern e p v) env
        // Positional destructure of a folded struct — the pre-SVStruct
        // behavior (structs folded as bare tuples), kept for compatibility.
        | SVStruct (_, fs) when fs.Length = pats.Length ->
            (pats, fs |> List.map snd) ||> List.zip |> List.fold (fun e (p, v) -> bindPattern e p v) env
        | _ -> env
    | PatternKind.PatStruct (_, fieldPats) ->
        match value with
        | SVStruct (_, fs) ->
            fieldPats |> List.fold (fun e (fn, p) ->
                match fs |> List.tryFind (fun (n, _) -> n = fn) with
                | Some (_, v) -> bindPattern e p v
                | None -> e) env
        | _ -> env
    | PatternKind.PatTyped (p, _) -> bindPattern env p value
    | PatternKind.PatWildcard -> env
    | _ -> env  // other patterns: no binding in static context

and evalMatch env fuel (scrutinee: StaticValue) (cases: MatchCase list) : Result<StaticValue, string> =
    match cases with
    | [] -> Error "Static evaluation: no matching case in match expression"
    | case :: rest ->
        match tryMatchPattern scrutinee case.Pattern with
        | Some bindings ->
            let env' = bindings |> List.fold (fun e (n, v) -> { e with Values = Map.add n v e.Values }) env
            // Check guard if present
            match case.Guard with
            | Some guard ->
                evalExpr env' fuel guard |> Result.bind (fun gv ->
                    match gv with
                    | SVBool true -> evalExpr env' fuel case.Body
                    | SVBool false -> evalMatch env fuel scrutinee rest
                    | _ -> Error "Static evaluation: match guard must be Bool")
            | None ->
                evalExpr env' fuel case.Body
        | None ->
            evalMatch env fuel scrutinee rest

and tryMatchPattern (value: StaticValue) (pat: Pattern) : (string * StaticValue) list option =
    match pat.Kind with
    | PatternKind.PatWildcard -> Some []
    | PatternKind.PatVar name -> Some [(name, value)]
    | PatternKind.PatLit lit ->
        let matches =
            match lit, value with
            | LitInt n, SVInt m -> n = m
            | LitFloat f, SVFloat g -> f = g
            | LitBool a, SVBool b -> a = b
            | LitString a, SVString b -> a = b
            | _ -> false
        if matches then Some [] else None
    | PatternKind.PatTuple pats ->
        let elems =
            match value with
            | SVTuple vs -> Some vs
            // Positional match against a folded struct (pre-SVStruct compat).
            | SVStruct (_, fs) -> Some (fs |> List.map snd)
            | _ -> None
        match elems with
        | Some vs when vs.Length = pats.Length ->
            let results = (pats, vs) ||> List.zip |> List.map (fun (p, v) -> tryMatchPattern v p)
            if results |> List.forall Option.isSome then
                Some (results |> List.choose id |> List.concat)
            else None
        | _ -> None
    | PatternKind.PatStruct (pname, fieldPats) ->
        match value with
        | SVStruct (sname, fs) when pname = sname ->
            let results =
                fieldPats |> List.map (fun (fn, p) ->
                    fs |> List.tryFind (fun (n, _) -> n = fn)
                       |> Option.bind (fun (_, v) -> tryMatchPattern v p))
            if results |> List.forall Option.isSome then
                Some (results |> List.choose id |> List.concat)
            else None
        | _ -> None
    | PatternKind.PatVariant (tag, payloadPat) ->
        // For static evaluation of sum types — match on tag name
        // This is a simplified approach; full variant matching would need
        // the static value to carry a tag
        None
    | _ -> None

and evalBlock env fuel (stmts: Stmt list) (finalExpr: Expr option) : Result<StaticValue, string> =
    match stmts with
    | [] ->
        match finalExpr with
        | Some e -> evalExpr env fuel e
        | None -> Ok SVUnit
    | StmtSpanned (inner, _) :: rest ->
        // Span annotations are transparent to static evaluation.
        evalBlock env fuel (inner :: rest) finalExpr
    | StmtLet binding :: rest ->
        evalExpr env fuel binding.Value |> Result.bind (fun v ->
            let env' = bindPattern env binding.Pattern v
            evalBlock env' fuel rest finalExpr)
    | StmtExpr e :: rest ->
        evalExpr env fuel e |> Result.bind (fun _ ->
            evalBlock env fuel rest finalExpr)
    | StmtAssign _ :: rest ->
        evalBlock env fuel rest finalExpr
    | StmtForIn _ :: rest ->
        evalBlock env fuel rest finalExpr  // Skip for-in loops in static eval

/// Built-in static functions (abs, min, max, length, etc.)
and evalBuiltin env fuel (name: string) (args: Expr list) : Result<StaticValue, string> =
    evalArgs env fuel args |> Result.bind (fun argVals ->
        // Scalar math intrinsics: same whitelist as TypeCheck.mathIntrinsics
        // (runtime form renders std::<name>); int operands promote to float.
        let asFloat = function SVInt n -> Some (float n) | SVFloat f -> Some f | _ -> None
        let mathFns : Map<string, float -> float> =
            Map.ofList [
                "exp", exp; "log", log; "sqrt", sqrt
                "sin", sin; "cos", cos; "tan", tan
                "sinh", sinh; "cosh", cosh; "tanh", tanh
                "asin", asin; "acos", acos; "atan", atan
                "floor", floor; "ceil", ceil
            ]
        match name, argVals with
        | _, [v] when (Map.containsKey name mathFns) && (asFloat v).IsSome ->
            Ok (SVFloat (mathFns.[name] (asFloat v).Value))
        | "abs", [SVInt n] -> Ok (SVInt (abs n))
        | "abs", [SVFloat f] -> Ok (SVFloat (abs f))
        | "min", [SVInt a; SVInt b] -> Ok (SVInt (min a b))
        | "max", [SVInt a; SVInt b] -> Ok (SVInt (max a b))
        | "min", [SVFloat a; SVFloat b] -> Ok (SVFloat (min a b))
        | "max", [SVFloat a; SVFloat b] -> Ok (SVFloat (max a b))
        | "length", [SVTuple xs] -> Ok (SVInt (int64 xs.Length))
        | "prodsum", (SVTuple _ :: _) when argVals |> List.forall (function SVTuple _ -> true | _ -> false) ->
            // Static mirror of the runtime prodsum intrinsic: Σ_t Π_ℓ xℓ(t)
            // over equal-length static arrays (arrays fold as SVTuple).
            let tuples = argVals |> List.map (function SVTuple xs -> xs | _ -> [])
            let n = tuples.Head.Length
            if tuples |> List.exists (fun t -> t.Length <> n) then
                Error "prodsum: static operands must share one length"
            else
                let asF = function SVInt i -> Ok (float i) | SVFloat f -> Ok f | v -> Error (sprintf "prodsum: non-numeric static element %A" v)
                let folded =
                    [0 .. n - 1] |> List.fold (fun acc t ->
                        acc |> Result.bind (fun s ->
                            tuples |> List.fold (fun p tup -> p |> Result.bind (fun pv -> asF tup.[t] |> Result.map (fun x -> pv * x))) (Ok 1.0)
                            |> Result.map (fun prod -> s + prod))) (Ok 0.0)
                folded |> Result.map SVFloat
        | _ ->
            // External registry (domain layers, e.g. the ML module's sizing
            // builtins — see registerStaticBuiltin). Consulted after the
            // core table misses so core names cannot be overridden.
            match externalBuiltins.TryGetValue name with
            | true, f -> f argVals
            | _ -> Error (sprintf "Static evaluation: unknown function '%s' or wrong arguments" name))

/// Evaluate binary operations with type promotion
and evalBinOp (op: BinOp) (lv: StaticValue) (rv: StaticValue) : Result<StaticValue, string> =
    // Promote int to float if mixed
    let lv', rv' =
        match lv, rv with
        | SVInt a, SVFloat _ -> SVFloat (float a), rv
        | SVFloat _, SVInt b -> lv, SVFloat (float b)
        | _ -> lv, rv
    match op, lv', rv' with
    // Integer arithmetic
    | OpAdd, SVInt a, SVInt b -> Ok (SVInt (a + b))
    | OpSub, SVInt a, SVInt b -> Ok (SVInt (a - b))
    | OpMul, SVInt a, SVInt b -> Ok (SVInt (a * b))
    | OpDiv, SVInt a, SVInt b when b <> 0L -> Ok (SVInt (a / b))
    | OpDiv, SVInt _, SVInt _ -> Error "Static evaluation: division by zero"
    | OpMod, SVInt a, SVInt b when b <> 0L -> Ok (SVInt (a % b))
    | OpMod, SVInt _, SVInt _ -> Error "Static evaluation: modulo by zero"
    // Float arithmetic
    | OpAdd, SVFloat a, SVFloat b -> Ok (SVFloat (a + b))
    | OpSub, SVFloat a, SVFloat b -> Ok (SVFloat (a - b))
    | OpMul, SVFloat a, SVFloat b -> Ok (SVFloat (a * b))
    | OpDiv, SVFloat a, SVFloat b -> Ok (SVFloat (a / b))
    // Integer comparisons
    | OpEq,  SVInt a, SVInt b -> Ok (SVBool (a = b))
    | OpNeq, SVInt a, SVInt b -> Ok (SVBool (a <> b))
    | OpLt,  SVInt a, SVInt b -> Ok (SVBool (a < b))
    | OpLe,  SVInt a, SVInt b -> Ok (SVBool (a <= b))
    | OpGt,  SVInt a, SVInt b -> Ok (SVBool (a > b))
    | OpGe,  SVInt a, SVInt b -> Ok (SVBool (a >= b))
    // Float comparisons
    | OpEq,  SVFloat a, SVFloat b -> Ok (SVBool (a = b))
    | OpNeq, SVFloat a, SVFloat b -> Ok (SVBool (a <> b))
    | OpLt,  SVFloat a, SVFloat b -> Ok (SVBool (a < b))
    | OpLe,  SVFloat a, SVFloat b -> Ok (SVBool (a <= b))
    | OpGt,  SVFloat a, SVFloat b -> Ok (SVBool (a > b))
    | OpGe,  SVFloat a, SVFloat b -> Ok (SVBool (a >= b))
    // Boolean
    | OpAnd, SVBool a, SVBool b -> Ok (SVBool (a && b))
    | OpOr,  SVBool a, SVBool b -> Ok (SVBool (a || b))
    // String equality
    | OpEq,  SVString a, SVString b -> Ok (SVBool (a = b))
    | OpNeq, SVString a, SVString b -> Ok (SVBool (a <> b))
    | _ -> Error (sprintf "Static evaluation: cannot apply %A to %A and %A" op lv rv)

// ============================================================================
// Static Resolution — Main Entry Point
// ============================================================================

/// A `let static` declaration whose right-hand side did not evaluate at
/// compile time. `let static` is an assertion — fold or fail loudly — so
/// the type-checker turns these into compile errors. A bare `let` remains
/// free to stage its work at runtime; only the annotated form demands
/// folding.
type StaticFailure = {
    /// Names bound by the declaration's pattern (one for `let static x`,
    /// several for tuple destructuring).
    Names: string list
    /// The evaluator's reason for the failure.
    Reason: string
    /// The declaration's source span.
    Span: Span
}

/// One `let static` declaration collected in Phase 1, carrying what Phase 3
/// needs to evaluate it once and report a failure against source.
type private PendingStatic = {
    Id: int
    Pattern: Pattern
    Names: string list
    Expr: Expr
    Span: Span
}

/// A lambda-valued `let static` declares a function (the marker means
/// immutability there), not a foldable value — the fold assertion skips it.
let rec private isLambdaExpr (expr: Expr) : bool =
    match expr.Kind with
    | ExprKind.ExprLambda _ -> true
    | ExprKind.ExprTyped (e, _) -> isLambdaExpr e
    | _ -> false

/// Resolve all static declarations in a module.
/// Returns the environment of folded values (tuple-destructured statics
/// bind their leaf names) plus one StaticFailure per `let static` whose
/// right-hand side did not evaluate. The Error case is reserved for a
/// circular dependency among static values.
let resolveStatics (decls: Located<Decl> list) : Result<StaticEnv * StaticFailure list, string> =
    // Phase 1: Collect static function definitions and static value decls
    let mutable staticFuncs : Map<string, StaticFuncDef> = Map.empty
    let mutable pendingRev : PendingStatic list = []

    let mutable structInfos : Map<string, StructStaticInfo> = Map.empty

    for locDecl in decls do
        match locDecl.Value with
        | DeclFunction fd when fd.IsStatic ->
            staticFuncs <- Map.add fd.Name {
                Name = fd.Name
                Params = fd.Params |> List.map (fun p -> p.Name)
                Body = fd.Body
            } staticFuncs
        | DeclType (TyDeclStruct (sname, _, sfields, sconstraints)) ->
            // Full pre-scan (struct/static decl order is irrelevant): the
            // fold-time conjunct list mirrors the checker's via the shared
            // Ast.structConjuncts helper.
            structInfos <- Map.add sname {
                Fields = sfields |> List.map (fun f -> f.Name)
                Conjuncts = structConjuncts sfields sconstraints
            } structInfos
        | DeclStatic binding ->
            // Any pattern that binds at least one name participates; a
            // pure-wildcard static asserts nothing observable.
            let names = collectPatternBindings binding.Pattern |> Set.toList
            if not names.IsEmpty then
                pendingRev <- { Id = List.length pendingRev
                                Pattern = binding.Pattern
                                Names = names
                                Expr = binding.Value
                                Span = locDecl.Span } :: pendingRev
        | _ -> ()

    let pending = List.rev pendingRev
    let staticNames = pending |> List.collect (fun pd -> pd.Names) |> Set.ofList

    // Provider-backed roots: `import netcdf as nc` provider-module aliases
    // (recognized against the registered provider-name set) plus the
    // bindings that load through them (`let sample = nc.load("file")`),
    // giving the provider-read fold its name → (provider, path) map. Both
    // plain and static load bindings are recognized.
    let providerAliases =
        decls |> List.fold (fun acc d ->
            match d.Value with
            | DeclImport ([pname], ImportQualified aliasOpt) when isProviderModuleName pname ->
                let alias = aliasOpt |> Option.defaultValue pname
                Map.add alias pname acc
            | _ -> acc) Map.empty
    let providerRoots =
        decls |> List.fold (fun acc d ->
            match d.Value with
            | DeclLet { Pattern = { Kind = PatternKind.PatVar root }; Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, "load") }, [{ Kind = ExprKind.ExprLit (LitString path) }]) } }
            | DeclStatic { Pattern = { Kind = PatternKind.PatVar root }; Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, "load") }, [{ Kind = ExprKind.ExprLit (LitString path) }]) } }
                when Map.containsKey alias providerAliases ->
                Map.add root (providerAliases.[alias], path) acc
            | _ -> acc) Map.empty

    // Phase 2: Dependency graph over bound names — a destructured decl's
    // names share the decl's dependencies — and topological sort.
    let deps =
        pending
        |> List.collect (fun pd ->
            let refs = collectFreeNames pd.Expr
            // Only dependencies on OTHER static values (not functions, not
            // names bound by this same declaration)
            let declDeps = Set.difference (Set.intersect refs staticNames) (Set.ofList pd.Names)
            pd.Names |> List.map (fun n -> (n, declDeps)))
        |> Map.ofList

    match topoSort deps with
    | Error cycle ->
        Error (sprintf "Static evaluation: circular dependency among: %s"
                   (cycle |> String.concat ", "))
    | Ok evalOrder ->
        // Phase 3: Evaluate each declaration once, in dependency order.
        // Duplicate names across decls: Map.ofList keeps the last decl,
        // matching the pre-assertion shadowing behavior.
        let nameToDecl =
            pending
            |> List.collect (fun pd -> pd.Names |> List.map (fun n -> (n, pd)))
            |> Map.ofList
        let calledRef = ref Set.empty
        let mutable env = { Values = Map.empty; Functions = staticFuncs; CalledFunctions = calledRef; ProviderRoots = providerRoots; Structs = structInfos }
        let mutable failures : StaticFailure list = []
        let mutable evaluated = Set.empty

        for name in evalOrder do
            match Map.tryFind name nameToDecl with
            | Some pd when not (Set.contains pd.Id evaluated) ->
                evaluated <- Set.add pd.Id evaluated
                if isLambdaExpr pd.Expr then
                    ()  // function definition, lowered as an ordinary closure
                else
                    match evalExpr env maxSteps pd.Expr with
                    | Ok value ->
                        env <- bindPattern env pd.Pattern value
                    | Error reason ->
                        failures <- failures @ [{ Names = pd.Names; Reason = reason; Span = pd.Span }]
            | _ -> ()

        Ok (env, failures)

/// Convert a StaticValue to a printable string (for debugging)
let rec ppStaticValue (v: StaticValue) : string =
    match v with
    | SVInt n -> string n
    | SVFloat f -> sprintf "%g" f
    | SVBool b -> if b then "true" else "false"
    | SVString s -> sprintf "\"%s\"" s
    | SVUnit -> "()"
    | SVTuple vs -> sprintf "(%s)" (vs |> List.map ppStaticValue |> String.concat ", ")
    | SVStruct (n, fs) ->
        sprintf "%s { %s }" n (fs |> List.map (fun (fn, v) -> sprintf "%s = %s" fn (ppStaticValue v)) |> String.concat ", ")
