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

/// A static function definition (unevaluated — applied during evaluation)
type StaticFuncDef = {
    Name: string
    Params: string list
    Body: Expr
}

/// Environment for static evaluation
type StaticEnv = {
    Values: Map<string, StaticValue>
    Functions: Map<string, StaticFuncDef>
    /// Accumulates names of functions called during evaluation
    CalledFunctions: ref<Set<string>>
}

// ============================================================================
// Dependency Analysis
// ============================================================================

/// Collect all free variable names referenced in an expression.
/// Does NOT descend into type annotations (those are handled in Phase 4).
let rec collectFreeNames (expr: Expr) : Set<string> =
    match expr with
    | ExprLit _ -> Set.empty
    | ExprVar name -> Set.singleton name
    | ExprBinOp (_, _, l, r) -> Set.union (collectFreeNames l) (collectFreeNames r)
    | ExprUnaryOp (_, e) -> collectFreeNames e
    | ExprApp (f, args) ->
        Set.union (collectFreeNames f) (args |> List.map collectFreeNames |> Set.unionMany)
    | ExprIf (c, t, e) ->
        [c; t; e] |> List.map collectFreeNames |> Set.unionMany
    | ExprTuple es | ExprArrayLit es ->
        es |> List.map collectFreeNames |> Set.unionMany
    | ExprField (obj, _) -> collectFreeNames obj
    | ExprLet (binding, body) ->
        let valRefs = collectFreeNames binding.Value
        let boundName = match binding.Pattern with PatVar n -> Set.singleton n | _ -> Set.empty
        Set.union valRefs (Set.difference (collectFreeNames body) boundName)
    | ExprMatch (scrut, cases) ->
        let scrutRefs = collectFreeNames scrut
        let caseRefs = cases |> List.map (fun c ->
            let patBinds = collectPatternBindings c.Pattern
            let guardRefs = c.Guard |> Option.map collectFreeNames |> Option.defaultValue Set.empty
            let bodyRefs = collectFreeNames c.Body
            Set.union guardRefs (Set.difference bodyRefs patBinds)) |> Set.unionMany
        Set.union scrutRefs caseRefs
    | ExprBlock (stmts, finalExpr) ->
        let stmtRefs = stmts |> List.map collectStmtNames |> Set.unionMany
        let finalRefs = finalExpr |> Option.map collectFreeNames |> Option.defaultValue Set.empty
        Set.union stmtRefs finalRefs
    | ExprStruct (_, fields) ->
        fields |> List.map (snd >> collectFreeNames) |> Set.unionMany
    | ExprTyped (e, _) -> collectFreeNames e
    | ExprLambda (_, _, body) -> collectFreeNames body  // params are local
    | _ -> Set.empty  // conservative for loop/combinator forms

and collectPatternBindings (pat: Pattern) : Set<string> =
    match pat with
    | PatVar name -> Set.singleton name
    | PatTuple pats -> pats |> List.map collectPatternBindings |> Set.unionMany
    | PatVariant (_, Some p) -> collectPatternBindings p
    | PatStruct (_, fields) -> fields |> List.map (snd >> collectPatternBindings) |> Set.unionMany
    | PatGuarded (p, _) -> collectPatternBindings p
    | PatTyped (p, _) -> collectPatternBindings p
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

// ============================================================================
// Expression Evaluator
// ============================================================================

let maxSteps = 100_000

let rec evalExpr (env: StaticEnv) (fuel: int) (expr: Expr) : Result<StaticValue, string> =
    if fuel <= 0 then
        Error "Static evaluation: step limit exceeded (possible infinite recursion)"
    else
    match expr with
    | ExprLit (LitInt n) -> Ok (SVInt n)
    | ExprLit (LitFloat f) -> Ok (SVFloat f)
    | ExprLit (LitBool b) -> Ok (SVBool b)
    | ExprLit (LitString s) -> Ok (SVString s)
    | ExprLit LitUnit -> Ok SVUnit

    | ExprVar name ->
        match Map.tryFind name env.Values with
        | Some v -> Ok v
        | None ->
            // Could be a static function used as a value (shouldn't happen normally)
            Error (sprintf "Static evaluation: undefined variable '%s'" name)

    | ExprBinOp (_, op, l, r) ->
        evalExpr env (fuel - 1) l |> Result.bind (fun lv ->
        evalExpr env (fuel - 1) r |> Result.bind (fun rv ->
            evalBinOp op lv rv))

    | ExprUnaryOp (op, e) ->
        evalExpr env (fuel - 1) e |> Result.bind (fun v ->
            match op, v with
            | OpNeg, SVInt n -> Ok (SVInt (-n))
            | OpNeg, SVFloat f -> Ok (SVFloat (-f))
            | OpNot, SVBool b -> Ok (SVBool (not b))
            | _ -> Error (sprintf "Static evaluation: cannot apply %A to %A" op v))

    | ExprApp (ExprVar fname, args) ->
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

    | ExprApp (func, args) ->
        // Non-variable function position — try evaluating
        Error (sprintf "Static evaluation: unsupported function form in call")

    | ExprIf (cond, thenBr, elseBr) ->
        evalExpr env (fuel - 1) cond |> Result.bind (fun cv ->
            match cv with
            | SVBool true -> evalExpr env (fuel - 1) thenBr
            | SVBool false -> evalExpr env (fuel - 1) elseBr
            | _ -> Error "Static evaluation: if condition must be Bool")

    | ExprTuple es ->
        evalArgs env (fuel - 1) es |> Result.map SVTuple

    | ExprArrayLit es ->
        evalArgs env (fuel - 1) es |> Result.map SVTuple  // static arrays as tuples

    | ExprLet (binding, body) ->
        evalExpr env (fuel - 1) binding.Value |> Result.bind (fun v ->
            let env' = bindPattern env binding.Pattern v
            evalExpr env' (fuel - 1) body)

    | ExprMatch (scrutinee, cases) ->
        evalExpr env (fuel - 1) scrutinee |> Result.bind (fun sv ->
            evalMatch env (fuel - 1) sv cases)

    | ExprBlock (stmts, finalExpr) ->
        evalBlock env (fuel - 1) stmts finalExpr

    | ExprField (obj, field) ->
        evalExpr env (fuel - 1) obj |> Result.bind (fun ov ->
            Error (sprintf "Static evaluation: field access '%s' not supported on static values" field))

    | ExprStruct (name, fields) ->
        // Evaluate all field values — store as tuple for now
        fields |> List.map (fun (_, e) -> evalExpr env (fuel - 1) e)
        |> seqResults
        |> Result.map SVTuple

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
    match pat with
    | PatVar name -> { env with Values = Map.add name value env.Values }
    | PatTuple pats ->
        match value with
        | SVTuple vs when vs.Length = pats.Length ->
            (pats, vs) ||> List.zip |> List.fold (fun e (p, v) -> bindPattern e p v) env
        | _ -> env
    | PatTyped (p, _) -> bindPattern env p value
    | PatWildcard -> env
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
    match pat with
    | PatWildcard -> Some []
    | PatVar name -> Some [(name, value)]
    | PatLit lit ->
        let matches =
            match lit, value with
            | LitInt n, SVInt m -> n = m
            | LitFloat f, SVFloat g -> f = g
            | LitBool a, SVBool b -> a = b
            | LitString a, SVString b -> a = b
            | _ -> false
        if matches then Some [] else None
    | PatTuple pats ->
        match value with
        | SVTuple vs when vs.Length = pats.Length ->
            let results = (pats, vs) ||> List.zip |> List.map (fun (p, v) -> tryMatchPattern v p)
            if results |> List.forall Option.isSome then
                Some (results |> List.choose id |> List.concat)
            else None
        | _ -> None
    | PatVariant (tag, payloadPat) ->
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
    match expr with
    | ExprLambda _ -> true
    | ExprTyped (e, _) -> isLambdaExpr e
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

    for locDecl in decls do
        match locDecl.Value with
        | DeclFunction fd when fd.IsStatic ->
            staticFuncs <- Map.add fd.Name {
                Name = fd.Name
                Params = fd.Params |> List.map (fun p -> p.Name)
                Body = fd.Body
            } staticFuncs
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
        let mutable env = { Values = Map.empty; Functions = staticFuncs; CalledFunctions = calledRef }
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
