/// Spectra-module elaboration: FFT and arity-polymorphic polyspectra as
/// compile-time source synthesis (the MathElaborate mold).
///
/// Surface (reachable only through `import spectra [as <alias>]`; array
/// arguments must be module-level `let`s with full Array annotations — the
/// ops read the declared shape):
///
///   sp.fft(x)             -- real x -> Array<Complex128 like Idx<n>> (unnormalized)
///   sp.ifft(X)            -- complex spectrum -> real signal (carries the 1/n)
///   sp.power(x)           -- |FFT(x)|² per bin (real)
///   sp.polyspec(x1,..,xk) -- order-k cross-polyspectrum, k = the CALL-SITE
///                            ARITY (2 cross-power, 3 bispectrum, 4 trispectrum):
///                            P(f_0..f_{k-2}) = X1(f_0)···X_{k-1}(f_{k-2})
///                                              · conj(Xk((Σf) mod n))
///
/// For each distinct (op, resolved shape/order) the elaborator synthesizes
/// ONE Blade function (`__spectra_1`, ...) via Blade.Spectra.Decls; call
/// sites rewrite to the generated names, deduped by fingerprint — every op
/// at one n shares a single generated FFT.
///
/// Pipeline position: after ML/PPL/Math/Rand elaboration, BEFORE Grad
/// expansion (TypeCheck.typeCheck).
module Blade.Spectra.Elaborate

open Blade.Ast
open Blade.StaticEval
open Blade.Spectra.Decls

// ============================================================================
// Module-level context (MathElaborate's contract)
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
            match b.Pattern, b.Type with
            | PatVar name, Some (TyArray (elem, idxs)) -> Map.add name (elem, idxs) acc
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

/// Element-type classes the ops care about.
type private ElemClass = ElemFloat | ElemComplex | ElemOther

let private classifyElem (ty: TypeExpr) : ElemClass =
    match ty with
    | TyFloat64 | TyNamed (("Float" | "Float64" | "Double"), []) -> ElemFloat
    | TyComplex128 | TyNamed ("Complex128", []) -> ElemComplex
    | _ -> ElemOther

/// The declared shape AND element class of an op's array argument.
let private arrayShape (ctx: Ctx) (what: string) (e: Expr) : Result<string * ElemClass * int list, string> =
    match e with
    | ExprVar name ->
        match Map.tryFind name ctx.Arrays with
        | None ->
            Error (sprintf "%s: '%s' must be a module-level let with a full Array<... like Idx<...>> annotation (spectra ops read the declared shape; rebind the value with an annotated let first)" what name)
        | Some (elem, idxs) ->
            let extents = idxs |> List.map (resolveExtent ctx)
            if extents |> List.forall Option.isSome then
                Ok (name, classifyElem elem, extents |> List.map Option.get)
            else
                Error (sprintf "%s: every axis extent of '%s' must be statically known (Idx<n> directly or through aliases)" what name)
    | _ ->
        Error (sprintf "%s: the array argument must be a plain variable naming an annotated module-level let (bind the expression first)" what)

/// A real rank-1 signal of static length.
let private realSignal (ctx: Ctx) (what: string) (e: Expr) : Result<int, string> =
    arrayShape ctx what e |> Result.bind (fun (name, elem, dims) ->
        match elem, dims with
        | ElemFloat, [n] -> Ok n
        | ElemFloat, _ -> Error (sprintf "%s: '%s' must be rank-1 (Array<Float64 like Idx<n>>)" what name)
        | _, _ -> Error (sprintf "%s: '%s' must have Float64 elements (a real signal)" what name))

// ============================================================================
// Elaboration state (fingerprint-deduped generated decls)
// ============================================================================

type private ElabState = {
    mutable Counter: int
    mutable Made: Map<string, string>
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
        let n = sprintf "__spectra_%d" st.Counter
        make n |> Result.map (fun decl ->
            st.Made <- Map.add key n st.Made
            st.Decls <- st.Decls @ [ decl ]
            n)

/// ensure for a TOTAL decl builder — used to mint the shared per-n FFT
/// before the ops that orchestrate it.
let private ensureT (st: ElabState) (key: string) (make: string -> FunctionDecl) : string =
    match ensure st key (fun n -> Ok (make n)) with
    | Ok n -> n
    | Error e -> failwith e // unreachable: make is total

let private ensureFft (st: ElabState) (n: int) : string =
    ensureT st (fingerprint "fft" (box n)) (fun nm -> fftDecl nm n)

// ============================================================================
// Op elaboration
// ============================================================================

let private opList = "fft, ifft, power, polyspec"

/// v1 cap on the polyspectrum output (the zeros literal and the C++
/// initializer both scale with n^(k-1); steer before g++ meets a
/// million-element initializer).
let private maxOutCells = 65536

let private elabOp (st: ElabState) (ctx: Ctx) (op: string) (args: Expr list) : Result<Expr, string> =
    match op, args with
    | "fft", [xE] ->
        realSignal ctx "fft" xE |> Result.bind (fun n ->
            Ok (ensureFft st n) |> Result.map (fun nm -> ExprApp (ExprVar nm, [xE])))
    | "fft", _ -> Error "fft: expected fft(X) — the unnormalized forward DFT of a real signal"
    | "ifft", [xE] ->
        arrayShape ctx "ifft" xE |> Result.bind (fun (name, elem, dims) ->
            match elem, dims with
            | ElemComplex, [n] ->
                ensure st (fingerprint "ifft" (box n)) (fun nm -> Ok (ifftDecl nm n))
                |> Result.map (fun nm -> ExprApp (ExprVar nm, [xE]))
            | ElemComplex, _ -> Error (sprintf "ifft: '%s' must be rank-1 (Array<Complex128 like Idx<n>>)" name)
            | _, _ -> Error (sprintf "ifft: '%s' must have Complex128 elements (ifft takes the complex spectrum; rebind the fft result with a full Array<Complex128 like Idx<n>> annotation first)" name))
    | "ifft", _ -> Error "ifft: expected ifft(X) — real inverse synthesis of a complex spectrum (carries the 1/n)"
    | "power", [xE] ->
        realSignal ctx "power" xE |> Result.bind (fun n ->
            let fftName = ensureFft st n
            ensure st (fingerprint "power" (box n)) (fun nm -> Ok (powerDecl nm n fftName))
            |> Result.map (fun nm -> ExprApp (ExprVar nm, [xE])))
    | "power", _ -> Error "power: expected power(X) — |FFT(X)|² per bin"
    | "polyspec", args when args.Length >= 2 && args.Length <= 4 ->
        let k = args.Length
        args
        |> List.fold (fun acc a ->
            acc |> Result.bind (fun ns ->
                realSignal ctx "polyspec" a |> Result.map (fun n -> ns @ [n]))) (Ok [])
        |> Result.bind (fun ns ->
            match List.distinct ns with
            | [n] ->
                let cells = pown n (k - 1)
                if cells > maxOutCells then
                    Error (sprintf "polyspec: the order-%d output at n=%d has %d cells; v1 caps the output at %d (the generated initializers scale with it) — reduce n or the order" k n cells maxOutCells)
                else
                    let fftName = ensureFft st n
                    ensure st (fingerprint "polyspec" (box (n, k))) (fun nm -> Ok (polyspecDecl nm n k fftName))
                    |> Result.map (fun nm -> ExprApp (ExprVar nm, args))
            | _ ->
                Error (sprintf "polyspec: all signals must share one static length (got %s)"
                               (ns |> List.map string |> String.concat ", ")))
    | "polyspec", args ->
        Error (sprintf "polyspec: the argument count IS the polyspectrum order and must be 2..4 in v1 (got %d) — k=2 cross-power, k=3 bispectrum, k=4 trispectrum; the generator is order-generic, raise the cap when needed" args.Length)
    | _ -> Error (sprintf "spectra: unknown op '%s' (available: %s)" op opList)

// ============================================================================
// Rewrite walker (MathElaborate's shape)
// ============================================================================

let rec private rewriteExpr (st: ElabState) (ctx: Ctx) (aliases: Set<string>) (e: Expr)
    : Result<Expr, string> =
    let r = rewriteExpr st ctx aliases
    let rList es =
        es |> List.fold (fun acc x ->
            acc |> Result.bind (fun xs -> r x |> Result.map (fun x' -> xs @ [x'])))
            (Ok [])
    match e with
    // Qualified spectra op: `alias.fft(...)` -> generated specialized
    // function. Any alias-qualified call is claimed here so an unknown op
    // gets a steering error instead of an unbound-module type error.
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

let private isSpectraImport (d: Located<Decl>) =
    match d.Value with
    | DeclImport (["spectra"], _) -> true
    | _ -> false

let private spectraAliasesOf (decls: Located<Decl> list) : Result<Set<string>, string> =
    decls |> List.fold (fun acc d ->
        acc |> Result.bind (fun set ->
            match d.Value with
            | DeclImport (["spectra"], ImportQualified aliasOpt) ->
                Ok (Set.add (aliasOpt |> Option.defaultValue "spectra") set)
            | DeclImport (["spectra"], ImportSelective _) ->
                Error "`spectra` supports only `import spectra [as <alias>]`; a selective `from spectra import ...` would reintroduce global names"
            | _ -> Ok set))
        (Ok Set.empty)

let private expandModule (decls: Located<Decl> list) : Result<Located<Decl> list, string> =
    spectraAliasesOf decls |> Result.bind (fun aliases ->
    // Import-gated: with no `import spectra`, this pass is a strict no-op —
    // a user's own `fft`/`power` functions are never touched.
    if Set.isEmpty aliases then Ok decls
    else
        let declsNoImport = decls |> List.filter (not << isSpectraImport)
        match resolveStatics declsNoImport with
        | Error e -> Error (sprintf "spectra elaboration: static resolution failed: %s" e)
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
                    // Generated functions are self-contained: splice at the
                    // FRONT so every use site sees them defined (the shared
                    // fft precedes its power/polyspec callers by ensure order).
                    let span = { StartLine = 0; StartCol = 0; EndLine = 0; EndCol = 0; File = None }
                    let gen = st.Decls |> List.map (fun fd -> { Value = DeclFunction fd; Span = span })
                    gen @ decls'))

/// Entry point: elaborate spectra ops across a program (after ML/PPL/Math/
/// Rand elaboration, before Grad expansion).
let expand (program: Program) : Result<Program, string> =
    program.Modules
    |> List.fold (fun acc m ->
        acc |> Result.bind (fun ms ->
            expandModule m.Decls |> Result.map (fun ds -> ms @ [{ m with Decls = ds }])))
        (Ok [])
    |> Result.map (fun ms -> { program with Modules = ms })
