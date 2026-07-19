/// `rand`-module elaboration: rewrites `alias.uniform/normal(key, shape)` into
/// the compiler-internal builtins `__rand_uniform` / `__rand_normal`, which the
/// type-checker self-types (dense Float64 array of the static shape) and codegen
/// materializes via the blade_rand runtime.
///
/// Surface (reachable only through `import rand [as <alias>]`):
///
///   rand.uniform(key, n)          -- rank-1 Array<Float64 like Idx<n>> ~ U[0,1)
///   rand.uniform(key, [m, n])     -- rank-2, row-major
///   rand.normal(key, n)           -- N(0,1) via Box-Muller
///
/// `key` is an Int64 stream key (same key => same draws). `shape` is a static
/// int or a list of static ints (`let static` names or literals). Unlike the
/// math module this pass synthesizes no Blade source — a counter-free RNG is not
/// expressible in Blade (no unsigned/bitwise ops), so the RNG lives in the C++
/// runtime and this pass only rewrites the call.
///
/// Pipeline position: after Math elaboration, BEFORE Grad expansion — rand
/// output is not differentiable, so Grad sees only the settled opaque builtin.
module Blade.Rand.Elaborate

open Blade.Ast
open Blade.StaticEval

let private v (name: string) : Expr = syn (ExprVar name)

/// Resolve a static-int argument: an int literal or a `let static` name.
let private staticInt (statics: StaticEnv) (what: string) (e: Expr) : Result<int, string> =
    match e.Kind with
    | ExprKind.ExprLit (LitInt n) -> Ok (int n)
    | ExprKind.ExprVar name ->
        match Map.tryFind name statics.Values with
        | Some (SVInt n) -> Ok (int n)
        | Some _ -> Error (sprintf "%s: '%s' is not a static int" what name)
        | None -> Error (sprintf "%s: '%s' is not a `let static` binding (rand shapes must be static)" what name)
    | _ -> Error (sprintf "%s: shape must be a static int or list of static ints" what)

/// Resolve a shape argument to its list of positive extents.
let private resolveShape (statics: StaticEnv) (what: string) (shapeE: Expr) : Result<int list, string> =
    let dims =
        match shapeE.Kind with
        | ExprKind.ExprArrayLit elems -> elems
        | _ -> [ shapeE ]
    dims
    |> List.fold (fun acc d ->
        acc |> Result.bind (fun xs ->
            staticInt statics what d |> Result.bind (fun n ->
                if n > 0 then Ok (xs @ [n])
                else Error (sprintf "%s: shape extents must be positive (got %d)" what n))))
        (Ok [])

/// Elaborate one qualified rand op. `keyE` is passed through verbatim (already
/// recursively rewritten); the shape becomes trailing int-literal args.
let private elabOp (statics: StaticEnv) (op: string) (args: Expr list) : Result<Expr, string> =
    let build fn keyE shapeE =
        resolveShape statics (sprintf "rand.%s" op) shapeE
        |> Result.map (fun dims ->
            syn (ExprApp (v fn, keyE :: (dims |> List.map (fun n -> syn (ExprLit (LitInt (int64 n))))))))
    match op, args with
    | "uniform", [keyE; shapeE] -> build "__rand_uniform" keyE shapeE
    | "normal",  [keyE; shapeE] -> build "__rand_normal"  keyE shapeE
    | ("uniform" | "normal"), _ ->
        Error (sprintf "rand.%s: expected rand.%s(key, shape) where shape is a static int or list of static ints" op op)
    | _ -> Error (sprintf "rand: unknown op '%s' (available: uniform, normal)" op)

// ============================================================================
// Rewrite walker (same shape as MathElaborate.rewriteExpr)
// ============================================================================

let rec private rewriteExpr (statics: StaticEnv) (aliases: Set<string>) (e: Expr) : Result<Expr, string> =
    let r = rewriteExpr statics aliases
    let rList es =
        es |> List.fold (fun acc x ->
            acc |> Result.bind (fun xs -> r x |> Result.map (fun x' -> xs @ [x'])))
            (Ok [])
    match e.Kind with
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, op) }, args) when Set.contains alias aliases ->
        rList args |> Result.bind (fun args' -> elabOp statics op args')
    | ExprKind.ExprLit _ | ExprKind.ExprVar _ -> Ok e
    | ExprKind.ExprApp (f, args) ->
        r f |> Result.bind (fun f' -> rList args |> Result.map (fun args' -> inheritSpan e (ExprApp (f', args'))))
    | ExprKind.ExprBinOp (m, op, l, rr) ->
        r l |> Result.bind (fun l' -> r rr |> Result.map (fun r' -> inheritSpan e (ExprBinOp (m, op, l', r'))))
    | ExprKind.ExprUnaryOp (op, inner) -> r inner |> Result.map (fun i -> inheritSpan e (ExprUnaryOp (op, i)))
    | ExprKind.ExprTyped (inner, t) -> r inner |> Result.map (fun i -> inheritSpan e (ExprTyped (i, t)))
    | ExprKind.ExprAssign (l, rr) ->
        r l |> Result.bind (fun l' -> r rr |> Result.map (fun r' -> inheritSpan e (ExprAssign (l', r'))))
    | ExprKind.ExprTuple es -> rList es |> Result.map (fun es' -> inheritSpan e (ExprTuple es'))
    | ExprKind.ExprArrayLit es -> rList es |> Result.map (fun es' -> inheritSpan e (ExprArrayLit es'))
    | ExprKind.ExprDotDot (l, h) ->
        r l |> Result.bind (fun l' -> r h |> Result.map (fun h' -> inheritSpan e (ExprDotDot (l', h'))))
    | ExprKind.ExprIf (c, t, f) ->
        r c |> Result.bind (fun c' ->
        r t |> Result.bind (fun t' ->
        r f |> Result.map (fun f' -> inheritSpan e (ExprIf (c', t', f')))))
    | ExprKind.ExprLet (binding, body) ->
        r binding.Value |> Result.bind (fun v' ->
        r body |> Result.map (fun b' -> inheritSpan e (ExprLet ({ binding with Value = v' }, b'))))
    | ExprKind.ExprBlock (stmts, finalE) ->
        let rec rStmt (s: Stmt) : Result<Stmt, string> =
            match s with
            | StmtSpanned (inner, sp) -> rStmt inner |> Result.map (fun i -> StmtSpanned (i, sp))
            | StmtLet binding -> r binding.Value |> Result.map (fun v' -> StmtLet { binding with Value = v' })
            | StmtExpr e2 -> r e2 |> Result.map StmtExpr
            | StmtAssign (l, op, rr) ->
                r l |> Result.bind (fun l' -> r rr |> Result.map (fun r' -> StmtAssign (l', op, r')))
            | StmtForIn (var, range, body) ->
                r range |> Result.bind (fun range' ->
                    body |> List.fold (fun acc bs ->
                        acc |> Result.bind (fun ss -> rStmt bs |> Result.map (fun s' -> ss @ [s'])))
                        (Ok [])
                    |> Result.map (fun body' -> StmtForIn (var, range', body')))
        stmts |> List.fold (fun acc s ->
            acc |> Result.bind (fun ss -> rStmt s |> Result.map (fun s' -> ss @ [s'])))
            (Ok [])
        |> Result.bind (fun stmts' ->
            match finalE with
            | Some fe -> r fe |> Result.map (fun fe' -> inheritSpan e (ExprBlock (stmts', Some fe')))
            | None -> Ok (inheritSpan e (ExprBlock (stmts', None))))
    | ExprKind.ExprLambda (ps, w, body) -> r body |> Result.map (fun b -> inheritSpan e (ExprLambda (ps, w, b)))
    | ExprKind.ExprMatch (scrut, cases) ->
        r scrut |> Result.bind (fun s' ->
            cases |> List.fold (fun acc c ->
                acc |> Result.bind (fun cs ->
                    r c.Body |> Result.map (fun b -> cs @ [{ c with Body = b }])))
                (Ok [])
            |> Result.map (fun cs' -> inheritSpan e (ExprMatch (s', cs'))))
    | _ -> Ok e

// ============================================================================
// Gating + program expansion
// ============================================================================

let private isRandImport (d: Located<Decl>) =
    match d.Value with
    | DeclImport (["rand"], _) -> true
    | _ -> false

let private randAliasesOf (decls: Located<Decl> list) : Result<Set<string>, string> =
    decls |> List.fold (fun acc d ->
        acc |> Result.bind (fun set ->
            match d.Value with
            | DeclImport (["rand"], ImportQualified aliasOpt) ->
                Ok (Set.add (aliasOpt |> Option.defaultValue "rand") set)
            | DeclImport (["rand"], ImportSelective _) ->
                Error "`rand` supports only `import rand [as <alias>]`; a selective `from rand import ...` would reintroduce global names"
            | _ -> Ok set))
        (Ok Set.empty)

let private expandModule (decls: Located<Decl> list) : Result<Located<Decl> list, string> =
    randAliasesOf decls |> Result.bind (fun aliases ->
    // Import-gated: with no `import rand`, this pass is a strict no-op.
    if Set.isEmpty aliases then Ok decls
    else
        let declsNoImport = decls |> List.filter (not << isRandImport)
        match resolveStatics declsNoImport with
        | Error e -> Error (sprintf "rand elaboration: static resolution failed: %s" e)
        | Ok (statics, _) ->
            declsNoImport |> List.fold (fun acc d ->
                acc |> Result.bind (fun out ->
                    // Stamp the user decl's span so every syn-built node
                    // attributes to this declaration's source line.
                    Blade.Ast.synthSpan <- d.Span
                    let mapped =
                        match d.Value with
                        | DeclFunction fd ->
                            rewriteExpr statics aliases fd.Body
                            |> Result.map (fun b -> DeclFunction { fd with Body = b })
                        | DeclLet binding ->
                            rewriteExpr statics aliases binding.Value
                            |> Result.map (fun v' -> DeclLet { binding with Value = v' })
                        | DeclStatic binding ->
                            rewriteExpr statics aliases binding.Value
                            |> Result.map (fun v' -> DeclStatic { binding with Value = v' })
                        | other -> Ok other
                    mapped |> Result.map (fun value -> out @ [{ d with Value = value }])))
                (Ok []))

/// Entry point: elaborate rand ops across a program (after Math elaboration,
/// before Grad expansion).
let private expandStr (program: Program) : Result<Program, string> =
    program.Modules
    |> List.fold (fun acc m ->
        acc |> Result.bind (fun ms ->
            expandModule m.Decls |> Result.map (fun ds -> ms @ [{ m with Decls = ds }])))
        (Ok [])
    |> Result.map (fun ms -> { program with Modules = ms })

/// Boundary: string-errored internals -> coded diagnostics. The span is the
/// ambient synthSpan -- stamped per-decl by expandStr, so a mid-elaboration
/// failure points at the offending declaration.
let expand (program: Program) : Result<Program, Blade.Diagnostics.Diagnostic list> =
    Blade.Ast.synthSpan <- Blade.Ast.noSpan
    expandStr program
    |> Result.mapError (fun msg ->
        [ Blade.Diagnostics.mkError "BL5300" (Blade.Diagnostics.Codes.phaseOfCode "BL5300") Blade.Ast.synthSpan msg ])
