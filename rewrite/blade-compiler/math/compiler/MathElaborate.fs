/// Math-module elaboration: dense linear algebra and tensor decompositions
/// as compile-time source synthesis, mirroring the ml/ppl elaborators.
///
/// Surface (reachable only through `import math [as <alias>]`; array
/// arguments must be module-level `let`s with full Array annotations — the
/// ops read the declared shape; config ints are `let static` names or
/// literals):
///
///   m.matmul(A, B)                 -- m×k · k×n -> m×n
///   m.svd(A) | m.svd(A, SWEEPS)    -- thin SVD, m >= n -> (U, S, V), S descending
///   m.eigh(S) | m.eigh(S, SWEEPS)  -- symmetric -> (Q, LAM), LAM descending
///   m.unfold(X, MODE)              -- Kolda–Bader mode-n matricization
///   m.mode_product(X, U, MODE)     -- tensor × matrix along MODE
///   m.hosvd(X) | m.hosvd(X, R1..RN)-- Tucker/HOSVD -> (G, U1, ..., UN)
///
/// For each distinct (op, resolved shape/config) the elaborator synthesizes
/// ONE Blade function (`__math_1`, ...) via Blade.Math.Decls; call sites
/// rewrite to the generated names, deduped by fingerprint.
///
/// Pipeline position: after ML/PPL elaboration, BEFORE Grad expansion
/// (TypeCheck.typeCheck), so generated bodies remain plain differentiable
/// Blade source.
module Blade.Math.Elaborate

open Blade.Ast
open Blade.StaticEval
open Blade.Math.Decls

// ============================================================================
// Module-level context: annotated array shapes, aliases, statics
// ============================================================================

/// Shape/static context the op elaborations read. Only annotated
/// module-level bindings participate — the ops read the declared shape,
/// they never infer one (PplElaborate's contract).
type private Ctx = {
    Arrays: Map<string, TypeExpr * TypeExpr list>
    Aliases: Map<string, TypeExpr>
    Statics: StaticEnv
}

let private collectArrays (decls: Located<Decl> list) : Map<string, TypeExpr * TypeExpr list> =
    decls |> List.fold (fun acc d ->
        match d.Value with
        | DeclLet b | DeclStatic b ->
            match b.Pattern, b.Type with
            | PatVar name, Some (TyArray (elem, idxs)) -> Map.add name (elem, idxs) acc
            | _ -> acc
        | _ -> acc) Map.empty

let private collectAliases (decls: Located<Decl> list) : Map<string, TypeExpr> =
    decls |> List.fold (fun acc d ->
        match d.Value with
        | DeclType (TyDeclAlias (name, _, body)) -> Map.add name body acc
        | _ -> acc) Map.empty

/// Resolve an index TypeExpr to its static extent, following alias chains.
let rec private resolveExtent (ctx: Ctx) (ty: TypeExpr) : int option =
    match ty with
    | TyIdx extent ->
        match evalExpr ctx.Statics maxSteps extent with
        | Ok (SVInt n) -> Some (int n)
        | _ -> None
    | TyNamed (name, []) ->
        Map.tryFind name ctx.Aliases |> Option.bind (resolveExtent ctx)
    | _ -> None

/// The declared shape of an op's array argument: every axis extent must be
/// statically known. The argument must be a plain variable naming an
/// annotated module-level let.
let private arrayShape (ctx: Ctx) (what: string) (e: Expr) : Result<string * int list, string> =
    match e with
    | ExprVar name ->
        match Map.tryFind name ctx.Arrays with
        | None ->
            Error (sprintf "%s: '%s' must be a module-level let with an Array<Float64 like Idx<...>, ...> annotation (math ops read the declared shape)" what name)
        | Some (_, idxs) ->
            let extents = idxs |> List.map (resolveExtent ctx)
            if extents |> List.forall Option.isSome then
                Ok (name, extents |> List.map Option.get)
            else
                Error (sprintf "%s: every axis extent of '%s' must be statically known (Idx<n> directly or through aliases)" what name)
    | _ ->
        Error (sprintf "%s: the array argument must be a plain variable naming an annotated module-level let (bind the expression first)" what)

/// Resolve a static-argument expression: a plain variable naming a
/// `let static` binding, or an inline int literal (ml's staticArg contract).
let private staticArg (statics: StaticEnv) (what: string) (e: Expr) : Result<StaticValue, string> =
    match e with
    | ExprLit (LitInt n) -> Ok (SVInt n)
    | ExprVar name ->
        match Map.tryFind name statics.Values with
        | Some sv -> Ok sv
        | None -> Error (sprintf "%s: '%s' is not a `let static` binding (math op configs must be static)" what name)
    | _ -> Error (sprintf "%s: config argument must be a `let static` binding name or literal" what)

let private staticInt (statics: StaticEnv) (what: string) (e: Expr) : Result<int, string> =
    staticArg statics what e |> Result.bind (fun sv ->
        match sv with
        | SVInt n -> Ok (int n)
        | _ -> Error (sprintf "%s: expected a static int" what))

// ============================================================================
// Elaboration state (fingerprint-deduped generated decls)
// ============================================================================

type private ElabState = {
    mutable Counter: int
    /// (op, config fingerprint) -> generated function name
    mutable Made: Map<string, string>
    /// generated decls in creation order
    mutable Decls: FunctionDecl list
}

let private fingerprint (op: string) (parts: obj) : string =
    sprintf "%s|%A" op parts

let private ensure (st: ElabState) (key: string) (make: string -> Result<FunctionDecl, string>)
    : Result<string, string> =
    match Map.tryFind key st.Made with
    | Some n -> Ok n
    | None ->
        st.Counter <- st.Counter + 1
        let n = sprintf "__math_%d" st.Counter
        make n |> Result.map (fun decl ->
            st.Made <- Map.add key n st.Made
            st.Decls <- st.Decls @ [ decl ]
            n)

/// ensure for a TOTAL decl builder (no failure path) — used by the hosvd
/// orchestration, which ensures a batch of helpers per mode.
let private ensureT (st: ElabState) (key: string) (make: string -> FunctionDecl) : string =
    match ensure st key (fun n -> Ok (make n)) with
    | Ok n -> n
    | Error e -> failwith e // unreachable: make is total

// ============================================================================
// Op elaboration
// ============================================================================

let private opNames =
    Set.ofList [ "matmul"; "svd"; "eigh"; "eig"; "unfold"; "mode_product"; "hosvd" ]

let private opList = "matmul, svd, eigh, eig, unfold, mode_product, hosvd"

/// Optional trailing SWEEPS argument shared by svd/eigh.
let private sweepsArg (statics: StaticEnv) (what: string) (rest: Expr list) : Result<int, string> =
    match rest with
    | [] -> Ok defaultSweeps
    | [swE] ->
        staticInt statics (what + " SWEEPS") swE |> Result.bind (fun s ->
            if s >= 1 then Ok s else Error (sprintf "%s: SWEEPS must be >= 1" what))
    | _ -> Error (sprintf "%s: at most one SWEEPS argument" what)

/// Elaborate one qualified op call. Arguments arrive already rewritten.
let private elabOp (st: ElabState) (ctx: Ctx) (op: string) (args: Expr list) : Result<Expr, string> =
    match op, args with
    | "matmul", [aE; bE] ->
        arrayShape ctx "matmul" aE |> Result.bind (fun (_, aDims) ->
        arrayShape ctx "matmul" bE |> Result.bind (fun (_, bDims) ->
            match aDims, bDims with
            | [m; k], [k2; n] when k = k2 ->
                ensure st (fingerprint "matmul" (box (m, k, n))) (fun nm -> Ok (matmulDecl nm m k n))
                |> Result.map (fun nm -> ExprApp (v nm, [aE; bE]))
            | [_; k], [k2; _] ->
                Error (sprintf "matmul: inner extents disagree (A is ..×%d, B is %d×..)" k k2)
            | _ -> Error "matmul: both arguments must be rank-2 (m×k · k×n)"))
    | "matmul", _ -> Error "matmul: expected matmul(A, B)"
    | "svd", (aE :: rest) ->
        sweepsArg ctx.Statics "svd" rest |> Result.bind (fun sweeps ->
        arrayShape ctx "svd" aE |> Result.bind (fun (_, dims) ->
            match dims with
            | [m; n] when m >= n ->
                ensure st (fingerprint "svd" (box (m, n, sweeps))) (fun nm -> Ok (svdDecl nm m n sweeps))
                |> Result.map (fun nm -> ExprApp (v nm, [aE]))
            | [m; n] ->
                Error (sprintf "svd: m < n unsupported in v1 (%d×%d); svd the transpose (transpose(A, [0, 1])) and swap U/V" m n)
            | _ -> Error "svd: the argument must be rank-2 (Array<Float64 like Idx<m>, Idx<n>>)"))
    | "svd", _ -> Error "svd: expected svd(A) or svd(A, SWEEPS)"
    | "eigh", (aE :: rest) ->
        sweepsArg ctx.Statics "eigh" rest |> Result.bind (fun sweeps ->
        arrayShape ctx "eigh" aE |> Result.bind (fun (_, dims) ->
            match dims with
            | [n; n2] when n = n2 ->
                ensure st (fingerprint "eigh" (box (n, sweeps))) (fun nm -> Ok (eighDecl nm n sweeps))
                |> Result.map (fun nm -> ExprApp (v nm, [aE]))
            | [n; n2] -> Error (sprintf "eigh: the argument must be square (got %d×%d); symmetry is assumed, not checked" n n2)
            | _ -> Error "eigh: the argument must be rank-2 square (Array<Float64 like Idx<n>, Idx<n>>, symmetric)"))
    | "eigh", _ -> Error "eigh: expected eigh(S) or eigh(S, SWEEPS)"
    | "eig", (aE :: rest) ->
        arrayShape ctx "eig" aE |> Result.bind (fun (_, dims) ->
            match dims with
            | [n; n2] when n = n2 ->
                let maxIterRes =
                    match rest with
                    | [] -> Ok (30 * n)
                    | [mE] ->
                        staticInt ctx.Statics "eig MAXITER" mE |> Result.bind (fun mi ->
                            if mi >= 1 then Ok mi else Error "eig: MAXITER must be >= 1")
                    | _ -> Error "eig: at most one MAXITER argument"
                maxIterRes |> Result.bind (fun maxIter ->
                    ensure st (fingerprint "eig" (box (n, maxIter))) (fun nm -> Ok (eigDecl nm n maxIter))
                    |> Result.map (fun nm -> ExprApp (v nm, [aE])))
            | [n; n2] -> Error (sprintf "eig: the argument must be square (got %d×%d)" n n2)
            | _ -> Error "eig: the argument must be rank-2 square (Array<Float64 like Idx<n>, Idx<n>>)")
    | "eig", _ -> Error "eig: expected eig(A) or eig(A, MAXITER) — returns (LRE, LIM) by descending modulus"
    | "unfold", [xE; modeE] ->
        staticInt ctx.Statics "unfold MODE" modeE |> Result.bind (fun mode ->
        arrayShape ctx "unfold" xE |> Result.bind (fun (_, dims) ->
            let r = dims.Length
            if r < 2 || r > 4 then
                Error (sprintf "unfold: tensor rank must be 2..4 in v1 (got rank %d); the generator is rank-generic — raise the cap when needed" r)
            elif mode < 0 || mode >= r then
                Error (sprintf "unfold: MODE must be in 0..%d for a rank-%d tensor (got %d)" (r - 1) r mode)
            else
                ensure st (fingerprint "unfold" (box (dims, mode))) (fun nm -> Ok (unfoldDecl nm dims mode))
                |> Result.map (fun nm -> ExprApp (v nm, [xE]))))
    | "unfold", _ -> Error "unfold: expected unfold(X, MODE) with a static MODE"
    | "mode_product", [xE; uE; modeE] ->
        staticInt ctx.Statics "mode_product MODE" modeE |> Result.bind (fun mode ->
        arrayShape ctx "mode_product" xE |> Result.bind (fun (_, dims) ->
        arrayShape ctx "mode_product" uE |> Result.bind (fun (_, uDims) ->
            let r = dims.Length
            if r < 2 || r > 4 then
                Error (sprintf "mode_product: tensor rank must be 2..4 in v1 (got rank %d)" r)
            elif mode < 0 || mode >= r then
                Error (sprintf "mode_product: MODE must be in 0..%d for a rank-%d tensor (got %d)" (r - 1) r mode)
            else
                match uDims with
                | [jOut; im] when im = dims.[mode] ->
                    ensure st (fingerprint "mode_product" (box (dims, mode, jOut)))
                        (fun nm -> Ok (modeProductDecl nm dims mode jOut))
                    |> Result.map (fun nm -> ExprApp (v nm, [xE; uE]))
                | [_; im] ->
                    Error (sprintf "mode_product: U's second extent must match the mode-%d extent (U is ..×%d, mode extent is %d)" mode im dims.[mode])
                | _ -> Error "mode_product: U must be rank-2 (Array<Float64 like Idx<j>, Idx<i_mode>>)")))
    | "mode_product", _ -> Error "mode_product: expected mode_product(X, U, MODE) with a static MODE"
    | "hosvd", (xE :: rankArgs) ->
        arrayShape ctx "hosvd" xE |> Result.bind (fun (_, dims) ->
            let r = dims.Length
            if r < 2 || r > 4 then
                Error (sprintf "hosvd: tensor rank must be 2..4 in v1 (got rank %d)" r)
            else
                let ranksRes =
                    if List.isEmpty rankArgs then Ok dims
                    elif rankArgs.Length <> r then
                        Error (sprintf "hosvd: expected %d truncation ranks for a rank-%d tensor (got %d); use hosvd(X) for the full decomposition" r r rankArgs.Length)
                    else
                        rankArgs |> List.fold (fun acc rE ->
                            acc |> Result.bind (fun rs ->
                                staticInt ctx.Statics "hosvd rank" rE |> Result.map (fun rk -> rs @ [rk])))
                            (Ok [])
                ranksRes |> Result.bind (fun ranks ->
                    if List.exists2 (fun rk ik -> rk < 1 || rk > ik) ranks dims then
                        Error (sprintf "hosvd: each truncation rank must be in 1..I_k (dims %A, ranks %A)" dims ranks)
                    else
                        // Per-mode helpers. The eigh fingerprint matches the
                        // m.eigh arm exactly, so hosvd and user eigh calls of
                        // the same shape share one generated function.
                        let gramNames =
                            [ for mode in 0 .. r - 1 ->
                                ensureT st (fingerprint "gram" (box (dims, mode))) (fun nm -> gramDecl nm dims mode) ]
                        let eighNames =
                            [ for mode in 0 .. r - 1 ->
                                ensureT st (fingerprint "eigh" (box (dims.[mode], defaultSweeps))) (fun nm -> eighDecl nm dims.[mode] defaultSweeps) ]
                        // Successive core shapes: mode n contracts against
                        // dims-with-earlier-modes-already-truncated.
                        let mutable curDims = dims
                        let mptNames =
                            [ for mode in 0 .. r - 1 ->
                                let dcur = curDims
                                let nm = ensureT st (fingerprint "mpt" (box (dcur, mode, ranks.[mode])))
                                            (fun nm -> modeProdTDecl nm dcur mode ranks.[mode])
                                curDims <- dcur |> List.mapi (fun k d -> if k = mode then ranks.[mode] else d)
                                nm ]
                        ensure st (fingerprint "hosvd" (box (dims, ranks)))
                            (fun nm -> Ok (hosvdDecl nm dims ranks gramNames eighNames mptNames))
                        |> Result.map (fun nm -> ExprApp (v nm, [xE]))))
    | "hosvd", _ -> Error "hosvd: expected hosvd(X) or hosvd(X, R1, ..., RN) with static ranks"
    | _ -> Error (sprintf "math: unknown op '%s' (available: %s)" op opList)

// ============================================================================
// Rewrite walker (same shape as MLElaborate.rewriteExpr)
// ============================================================================

let rec private rewriteExpr (st: ElabState) (ctx: Ctx) (aliases: Set<string>) (e: Expr)
    : Result<Expr, string> =
    let r = rewriteExpr st ctx aliases
    let rList es =
        es |> List.fold (fun acc x ->
            acc |> Result.bind (fun xs -> r x |> Result.map (fun x' -> xs @ [x'])))
            (Ok [])
    match e with
    // Qualified math op: `alias.svd(...)` -> generated specialized function.
    // Any alias-qualified call is claimed here so an unknown op gets a
    // steering error instead of an unbound-module type error downstream.
    | ExprApp (ExprField (ExprVar alias, op), args) when Set.contains alias aliases ->
        rList args |> Result.bind (fun args' -> elabOp st ctx op args')
    | ExprLit _ | ExprVar _ -> Ok e
    | ExprApp (f, args) ->
        r f |> Result.bind (fun f' -> rList args |> Result.map (fun args' -> ExprApp (f', args')))
    | ExprBinOp (m, op, l, rr) ->
        r l |> Result.bind (fun l' -> r rr |> Result.map (fun r' -> ExprBinOp (m, op, l', r')))
    | ExprUnaryOp (op, inner) -> r inner |> Result.map (fun i -> ExprUnaryOp (op, i))
    | ExprTyped (inner, t) -> r inner |> Result.map (fun i -> ExprTyped (i, t))
    | ExprAssign (l, rr) ->
        r l |> Result.bind (fun l' -> r rr |> Result.map (fun r' -> ExprAssign (l', r')))
    | ExprTuple es -> rList es |> Result.map ExprTuple
    | ExprArrayLit es -> rList es |> Result.map ExprArrayLit
    | ExprDotDot (l, h) ->
        r l |> Result.bind (fun l' -> r h |> Result.map (fun h' -> ExprDotDot (l', h')))
    | ExprIf (c, t, f) ->
        r c |> Result.bind (fun c' ->
        r t |> Result.bind (fun t' ->
        r f |> Result.map (fun f' -> ExprIf (c', t', f'))))
    | ExprLet (binding, body) ->
        r binding.Value |> Result.bind (fun v' ->
        r body |> Result.map (fun b' -> ExprLet ({ binding with Value = v' }, b')))
    | ExprBlock (stmts, finalE) ->
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
            | Some fe -> r fe |> Result.map (fun fe' -> ExprBlock (stmts', Some fe'))
            | None -> Ok (ExprBlock (stmts', None)))
    | ExprLambda (ps, w, body) -> r body |> Result.map (fun b -> ExprLambda (ps, w, b))
    | ExprMatch (scrut, cases) ->
        r scrut |> Result.bind (fun s' ->
            cases |> List.fold (fun acc c ->
                acc |> Result.bind (fun cs ->
                    r c.Body |> Result.map (fun b -> cs @ [{ c with Body = b }])))
                (Ok [])
            |> Result.map (fun cs' -> ExprMatch (s', cs')))
    | other -> Ok other

// ============================================================================
// Gating + program expansion
// ============================================================================

/// `import math [as _]` — the module this layer owns.
let private isMathImport (d: Located<Decl>) =
    match d.Value with
    | DeclImport (["math"], _) -> true
    | _ -> false

/// Aliases bound to `math` in this decl list. Errors on a selective
/// `from math import ...`, which would reintroduce the global names the
/// module system is meant to remove.
let private mathAliasesOf (decls: Located<Decl> list) : Result<Set<string>, string> =
    decls |> List.fold (fun acc d ->
        acc |> Result.bind (fun set ->
            match d.Value with
            | DeclImport (["math"], ImportQualified aliasOpt) ->
                Ok (Set.add (aliasOpt |> Option.defaultValue "math") set)
            | DeclImport (["math"], ImportSelective _) ->
                Error "`math` supports only `import math [as <alias>]`; a selective `from math import ...` would reintroduce global names"
            | _ -> Ok set))
        (Ok Set.empty)

let private expandModule (decls: Located<Decl> list) : Result<Located<Decl> list, string> =
    mathAliasesOf decls |> Result.bind (fun aliases ->
    // Import-gated: with no `import math`, this pass is a strict no-op — a
    // user's own `svd`/`matmul` functions are never touched.
    if Set.isEmpty aliases then Ok decls
    else
        let declsNoImport = decls |> List.filter (not << isMathImport)
        // Fold failures are the type-checker's to report; elaboration only
        // needs the successfully folded environment.
        match resolveStatics declsNoImport with
        | Error e -> Error (sprintf "math elaboration: static resolution failed: %s" e)
        | Ok (statics, _) ->
            let ctx = { Arrays = collectArrays declsNoImport
                        Aliases = collectAliases declsNoImport
                        Statics = statics }
            let st = { Counter = 0; Made = Map.empty; Decls = [] }
            let mapped =
                declsNoImport |> List.fold (fun acc d ->
                    acc |> Result.bind (fun out ->
                        let mapped =
                            match d.Value with
                            | DeclFunction fd ->
                                rewriteExpr st ctx aliases fd.Body
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
                    // Generated functions are self-contained (no captures):
                    // splice them at the FRONT so every use site (top-level
                    // lets included) sees them defined.
                    let span = { StartLine = 0; StartCol = 0; EndLine = 0; EndCol = 0; File = None }
                    let gen = st.Decls |> List.map (fun fd -> { Value = DeclFunction fd; Span = span })
                    gen @ decls'))

/// Entry point: elaborate math ops across a program (after ML/PPL
/// elaboration, before Grad expansion).
let expand (program: Program) : Result<Program, string> =
    program.Modules
    |> List.fold (fun acc m ->
        acc |> Result.bind (fun ms ->
            expandModule m.Decls |> Result.map (fun ds -> ms @ [{ m with Decls = ds }])))
        (Ok [])
    |> Result.map (fun ms -> { program with Modules = ms })
