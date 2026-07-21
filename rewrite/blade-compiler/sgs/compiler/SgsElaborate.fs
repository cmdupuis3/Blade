/// sgs-module elaboration: the subgrid-closure field formers as compile-time
/// source synthesis, mirroring the math elaborator.
///
/// Surface (reachable only through `import sgs [as <alias>]`; the field
/// argument must be a module-level `let` with a full Array annotation —
/// the ops read the declared shape; W is a `let static` name or literal):
///
///   sgs.grad(U, DX)        -- (3, n, n, n) -> (3, 3, n, n, n): G(c,d,i,j,k)
///                             = d_d u_c, 2nd-order central diff, periodic
///   sgs.box_filter(U, W)   -- (3, n, n, n) -> (3, m, m, m), m = n/W: tile means
///   sgs.stress(U, W)       -- (3, n, n, n) -> (6, m, m, m): exact subgrid
///                             stress tau_ij = mean(u_i u_j|tile) - mean_i mean_j,
///                             packed in CartesianBridge.packPairs order
///
/// For each distinct (op, resolved shape/config) the elaborator synthesizes
/// ONE Blade function (`__sgs_1`, ...) via Blade.Sgs.Decls; call sites
/// rewrite to the generated names, deduped by fingerprint.
///
/// Pipeline position: AFTER ML elaboration (so `where ml.galilean` bodies
/// can be judged with surface `sgs.*` calls still visible at ML's seam) and
/// before PPL/Math/Grad — generated bodies remain plain differentiable
/// Blade source.
module Blade.Sgs.Elaborate

open Blade.Ast
open Blade.StaticEval
open Blade.Sgs.Decls

// ============================================================================
// Module-level context (the MathElaborate contract)
// ============================================================================

type private Ctx = {
    Arrays: Map<string, TypeExpr * TypeExpr list>
    Aliases: Map<string, TypeExpr>
    Statics: StaticEnv
}

let private collectArrays (decls: Located<Decl> list) : Map<string, TypeExpr * TypeExpr list> =
    decls |> List.fold (fun acc d ->
        match d.Value with
        | DeclLet b | DeclStatic b ->
            match b.Pattern.Kind, b.Type with
            | PatternKind.PatVar name, Some (TyArray (elem, idxs)) -> Map.add name (elem, idxs) acc
            | _ -> acc
        | _ -> acc) Map.empty

let private collectAliases (decls: Located<Decl> list) : Map<string, TypeExpr> =
    decls |> List.fold (fun acc d ->
        match d.Value with
        | DeclType (TyDeclAlias (name, _, body)) -> Map.add name body acc
        | _ -> acc) Map.empty

let rec private resolveExtent (ctx: Ctx) (ty: TypeExpr) : int option =
    match ty with
    | TyIdx extent ->
        match evalExpr ctx.Statics maxSteps extent with
        | Ok (SVInt n) -> Some (int n)
        | _ -> None
    | TyNamed (name, []) ->
        Map.tryFind name ctx.Aliases |> Option.bind (resolveExtent ctx)
    | _ -> None

/// The declared shape of the field argument: a plain variable naming an
/// annotated module-level let with statically known extents.
let private arrayShape (ctx: Ctx) (what: string) (e: Expr) : Result<string * int list, string> =
    match e.Kind with
    | ExprKind.ExprVar name ->
        match Map.tryFind name ctx.Arrays with
        | None ->
            Error (sprintf "%s: '%s' must be a module-level let with an Array<Float64 like Idx<...>, ...> annotation (sgs ops read the declared shape)" what name)
        | Some (_, idxs) ->
            let extents = idxs |> List.map (resolveExtent ctx)
            if extents |> List.forall Option.isSome then
                Ok (name, extents |> List.map Option.get)
            else
                Error (sprintf "%s: every axis extent of '%s' must be statically known (Idx<n> directly or through aliases)" what name)
    | _ ->
        Error (sprintf "%s: the field argument must be a plain variable naming an annotated module-level let (bind the expression first)" what)

let private staticInt (statics: StaticEnv) (what: string) (e: Expr) : Result<int, string> =
    match e.Kind with
    | ExprKind.ExprLit (LitInt n) -> Ok (int n)
    | ExprKind.ExprVar name ->
        match Map.tryFind name statics.Values with
        | Some (SVInt n) -> Ok (int n)
        | Some _ -> Error (sprintf "%s: expected a static int" what)
        | None -> Error (sprintf "%s: '%s' is not a `let static` binding (sgs op configs must be static)" what name)
    | _ -> Error (sprintf "%s: config argument must be a `let static` binding name or literal" what)

// ============================================================================
// Elaboration state
// ============================================================================

type private ElabState = {
    mutable Counter: int
    mutable Made: Map<string, string>
    mutable Decls: FunctionDecl list
}

let private fingerprint (op: string) (parts: obj) : string =
    sprintf "%s|%A" op parts

let private ensure (st: ElabState) (key: string) (make: string -> FunctionDecl) : string =
    match Map.tryFind key st.Made with
    | Some n -> n
    | None ->
        st.Counter <- st.Counter + 1
        let n = sprintf "__sgs_%d" st.Counter
        let decl = make n
        st.Made <- Map.add key n st.Made
        st.Decls <- st.Decls @ [ decl ]
        n

// ============================================================================
// Op elaboration
// ============================================================================

let private opList = "grad, box_filter, stress"

/// The (3, n, n, n) cubic-field shape every sgs former reads.
let private fieldShape (ctx: Ctx) (what: string) (uE: Expr) : Result<int, string> =
    arrayShape ctx what uE |> Result.bind (fun (_, dims) ->
        match dims with
        | [ 3; a; b; c ] when a = b && b = c -> Ok a
        | [ 3; _; _; _ ] -> Error (sprintf "%s: the field must be cubic (Idx<3> then three equal spatial extents) in v1" what)
        | _ -> Error (sprintf "%s: the field must be Array<Float64 like Idx<3>, Idx<n>, Idx<n>, Idx<n>> (component-first, space-last)" what))

let private tileArg (ctx: Ctx) (what: string) (n: int) (wE: Expr) : Result<int, string> =
    staticInt ctx.Statics (what + " W") wE |> Result.bind (fun w ->
        if w < 1 then Error (sprintf "%s: W must be >= 1" what)
        elif n % w <> 0 then Error (sprintf "%s: the tile width W = %d must divide the spatial extent n = %d" what w n)
        else Ok w)

let private elabOp (st: ElabState) (ctx: Ctx) (op: string) (args: Expr list) : Result<Expr, string> =
    match op, args with
    | "grad", [ uE; dxE ] ->
        fieldShape ctx "grad" uE |> Result.map (fun n ->
            let nm = ensure st (fingerprint "grad" (box n)) (fun nm -> gradDecl nm n)
            syn (ExprApp (syn (ExprVar nm), [ uE; dxE ])))
    | "grad", _ -> Error "grad: expected grad(U, DX) with DX the grid spacing"
    | "box_filter", [ uE; wE ] ->
        fieldShape ctx "box_filter" uE |> Result.bind (fun n ->
        tileArg ctx "box_filter" n wE |> Result.map (fun w ->
            let nm = ensure st (fingerprint "box_filter" (box (n, w))) (fun nm -> boxFilterDecl nm n w)
            syn (ExprApp (syn (ExprVar nm), [ uE ]))))
    | "box_filter", _ -> Error "box_filter: expected box_filter(U, W)"
    | "stress", [ uE; wE ] ->
        fieldShape ctx "stress" uE |> Result.bind (fun n ->
        tileArg ctx "stress" n wE |> Result.map (fun w ->
            let nm = ensure st (fingerprint "stress" (box (n, w))) (fun nm -> stressDecl nm n w)
            syn (ExprApp (syn (ExprVar nm), [ uE ]))))
    | "stress", _ -> Error "stress: expected stress(U, W)"
    | _ -> Error (sprintf "sgs: unknown op '%s' (available: %s)" op opList)

// ============================================================================
// Rewrite walker (same shape as MathElaborate.rewriteExpr)
// ============================================================================

let rec private rewriteExpr (st: ElabState) (ctx: Ctx) (aliases: Set<string>) (e: Expr)
    : Result<Expr, string> =
    let r = rewriteExpr st ctx aliases
    let rList es =
        es |> List.fold (fun acc x ->
            acc |> Result.bind (fun xs -> r x |> Result.map (fun x' -> xs @ [x'])))
            (Ok [])
    match e.Kind with
    // Qualified sgs op: `alias.grad(...)` -> generated specialized function.
    // Any alias-qualified call is claimed here so an unknown op gets a
    // steering error instead of an unbound-module type error downstream.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, op) }, args) when Set.contains alias aliases ->
        rList args |> Result.bind (fun args' -> elabOp st ctx op args')
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

/// `import sgs [as _]` — the module this layer owns.
let private isSgsImport (d: Located<Decl>) =
    match d.Value with
    | DeclImport (["sgs"], _) -> true
    | _ -> false

let private sgsAliasesOf (decls: Located<Decl> list) : Result<Set<string>, string> =
    decls |> List.fold (fun acc d ->
        acc |> Result.bind (fun set ->
            match d.Value with
            | DeclImport (["sgs"], ImportQualified aliasOpt) ->
                Ok (Set.add (aliasOpt |> Option.defaultValue "sgs") set)
            | DeclImport (["sgs"], ImportSelective _) ->
                Error "`sgs` supports only `import sgs [as <alias>]`; a selective `from sgs import ...` would reintroduce global names"
            | _ -> Ok set))
        (Ok Set.empty)

let private expandModule (decls: Located<Decl> list) : Result<Located<Decl> list, string> =
    sgsAliasesOf decls |> Result.bind (fun aliases ->
    // Import-gated: with no `import sgs`, this pass is a strict no-op.
    if Set.isEmpty aliases then Ok decls
    else
        let declsNoImport = decls |> List.filter (not << isSgsImport)
        match resolveStatics declsNoImport with
        | Error e -> Error (sprintf "sgs elaboration: static resolution failed: %s" e)
        | Ok (statics, _) ->
            let ctx = { Arrays = collectArrays declsNoImport
                        Aliases = collectAliases declsNoImport
                        Statics = statics }
            let st = { Counter = 0; Made = Map.empty; Decls = [] }
            let mapped =
                declsNoImport |> List.fold (fun acc d ->
                    acc |> Result.bind (fun out ->
                        Blade.Ast.synthSpan <- d.Span
                        let mapped =
                            match d.Value with
                            | DeclFunction fd ->
                                // Annotated array PARAMETERS join the shape
                                // context for this body — a galilean-certified
                                // function applies the formers to its own
                                // velocity parameter (the module-level-let
                                // contract still governs everything else).
                                let ctxF =
                                    { ctx with
                                        Arrays =
                                            fd.Params
                                            |> List.fold (fun m p ->
                                                match p.Type with
                                                | Some (TyArray (elem, idxs)) -> Map.add p.Name (elem, idxs) m
                                                | _ -> m) ctx.Arrays }
                                rewriteExpr st ctxF aliases fd.Body
                                |> Result.map (fun b -> DeclFunction { fd with Body = b })
                            | DeclLet binding ->
                                rewriteExpr st ctx aliases binding.Value
                                |> Result.map (fun v' -> DeclLet { binding with Value = v' })
                            | DeclStatic binding ->
                                rewriteExpr st ctx aliases binding.Value
                                |> Result.map (fun v' -> DeclStatic { binding with Value = v' })
                            | other -> Ok other
                        mapped |> Result.map (fun value -> out @ [{ d with Value = value }])))
                    (Ok [])
            mapped |> Result.map (fun decls' ->
                if st.Decls.IsEmpty then decls'
                else
                    let span = { StartLine = 0; StartCol = 0; EndLine = 0; EndCol = 0; File = None }
                    let gen = st.Decls |> List.map (fun fd -> { Value = DeclFunction fd; Span = span })
                    gen @ decls'))

let private expandStr (program: Program) : Result<Program, string> =
    program.Modules
    |> List.fold (fun acc m ->
        acc |> Result.bind (fun ms ->
            expandModule m.Decls |> Result.map (fun ds -> ms @ [{ m with Decls = ds }])))
        (Ok [])
    |> Result.map (fun ms -> { program with Modules = ms })

/// Boundary: string-errored internals -> coded diagnostics (BL5600).
let expand (program: Program) : Result<Program, Blade.Diagnostics.Diagnostic list> =
    Blade.Ast.synthSpan <- Blade.Ast.noSpan
    expandStr program
    |> Result.mapError (fun msg ->
        [ Blade.Diagnostics.mkError "BL5600" (Blade.Diagnostics.Codes.phaseOfCode "BL5600") Blade.Ast.synthSpan msg ])
