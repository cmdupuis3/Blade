/// The `where ml.galilean(u, ...)` discipline: a function carrying the
/// (normalized) `__ml_galilean` conjunct is PROVED invariant under a
/// constant Galilean boost of the listed parameters — its body may combine
/// boost-variant values only through boost-cancelling operations. The
/// judgment is an abstract interpretation over the surface AST, run by
/// MLElaborate at the same pass-1/pass-2 seam as the equiv judgment (sgs
/// elaborates AFTER ml, so surface `sgs.*` former calls are still visible
/// here and carry axiomatic rules).
///
/// The certificate is a conditional theorem: IF the listed parameters are
/// velocity-typed in the physical sense (every one shifts u -> u + U0 under
/// the SAME constant boost U0, componentwise for arrays) and all other
/// parameters are held fixed, THEN the result is unchanged. Scope is
/// honest: a CONSTANT boost only — rotations are ml.equiv's theorem; no
/// time-dependent boosts, no coordinate shift x -> x - U0 t.
///
/// Units are deliberately NOT the seed: a velocity DIFFERENCE still carries
/// the velocity unit but is boost-invariant — units track dimension, not
/// frame behavior. The conjunct names the boost-variant parameters (the
/// comm/indep precedent).
///
/// Abstract value domain (BVar tracks U0-coefficient EXACTLY 1):
///   BVar    — value = boosted quantity + boost-independent part;
///             indexing a BVar array yields BVar elements (boost-variance
///             is per-component and index-stable — the key structural
///             difference from the equiv judgment, where raw indexing is
///             the forbidden read);
///   BInv    — boost-invariant (differences of BVars, gradients, stresses,
///             constants, everything else);
///   BOpaque — unclassifiable; rejected where it matters.
///
/// v1 rules: BVar - BVar -> BInv (the central rule); BVar +/- BInv -> BVar;
/// everything that scales or nonlinearizes a BVar is BL4009 (documented v2:
/// rational U0-coefficient tracking would admit static-weight averages and
/// BVar-returning steppers). Certified functions must RETURN boost-
/// invariant values in v1.
///
/// Axiomatic op rules (surface-visible at this seam):
///   sgs.grad(U, DX)      : U any -> BInv   (difference weights sum to 0)
///   sgs.stress(U, W)     : U any -> BInv   (a central comoment)
///   sgs.box_filter(U, W) : U st  -> st     (weights sum to 1: preserves)
///   every ml.* op        : all-BInv args -> BInv; a BVar arg is a reject
/// Violations are BL4009 at the offending expression's span.
module Blade.ML.Galilean

open Blade.Ast

type BoostStatus =
    | BVar
    | BInv
    | BOpaque

type GalSig = {
    /// Parameter name -> status, in declaration order.
    Params: (string * BoostStatus) list
}

// ============================================================================
// Helpers
// ============================================================================

let private bl4009 (span: Span) (msg: string) : Blade.Diagnostics.Diagnostic =
    Blade.Diagnostics.mkError "BL4009" (Blade.Diagnostics.Codes.phaseOfCode "BL4009") span msg

let private statusStr (st: BoostStatus) : string =
    match st with
    | BVar -> "boost-variant (shifts with the frame velocity)"
    | BInv -> "boost-invariant"
    | BOpaque -> "unclassifiable"

let rec private patternVars (p: Pattern) : string list =
    match p.Kind with
    | PatternKind.PatVar n -> [ n ]
    | PatternKind.PatTuple ps -> ps |> List.collect patternVars
    | _ -> []

/// Free variables of an expression that are NOT locally bound (the lambda
/// capture rule; mirror of MLEquiv.freeVars).
let rec private freeVars (bound: Set<string>) (e: Expr) : Set<string> =
    match e.Kind with
    | ExprKind.ExprVar n -> if Set.contains n bound then Set.empty else Set.singleton n
    | ExprKind.ExprLit _ -> Set.empty
    | ExprKind.ExprApp (f, args) ->
        Set.unionMany (freeVars bound f :: (args |> List.map (freeVars bound)))
    | ExprKind.ExprBinOp (_, _, l, r) -> Set.union (freeVars bound l) (freeVars bound r)
    | ExprKind.ExprUnaryOp (_, i) -> freeVars bound i
    | ExprKind.ExprTyped (i, _) -> freeVars bound i
    | ExprKind.ExprTuple es | ExprKind.ExprArrayLit es ->
        es |> List.map (freeVars bound) |> Set.unionMany
    | ExprKind.ExprDotDot (l, h) -> Set.union (freeVars bound l) (freeVars bound h)
    | ExprKind.ExprIf (c, t, f) ->
        Set.unionMany [ freeVars bound c; freeVars bound t; freeVars bound f ]
    | ExprKind.ExprLet (b, body) ->
        Set.union (freeVars bound b.Value) (freeVars (Set.union bound (Set.ofList (patternVars b.Pattern))) body)
    | ExprKind.ExprLambda (ps, _, body) ->
        freeVars (Set.union bound (Set.ofList (ps |> List.map (fun p -> p.Name)))) body
    | ExprKind.ExprBlock (stmts, fin) ->
        let mutable b = bound
        let mutable acc = Set.empty
        for s in stmts do
            match unwrapStmt s with
            | StmtLet binding ->
                acc <- Set.union acc (freeVars b binding.Value)
                b <- Set.union b (Set.ofList (patternVars binding.Pattern))
            | StmtExpr e2 -> acc <- Set.union acc (freeVars b e2)
            | StmtAssign (l, _, r) -> acc <- Set.union acc (Set.union (freeVars b l) (freeVars b r))
            | StmtForIn (v, range, body) ->
                acc <- Set.union acc (freeVars b range)
                let b2 = Set.add v b
                for s2 in body do
                    match unwrapStmt s2 with
                    | StmtExpr e2 -> acc <- Set.union acc (freeVars b2 e2)
                    | StmtLet binding -> acc <- Set.union acc (freeVars b2 binding.Value)
                    | StmtAssign (l, _, r) -> acc <- Set.union acc (Set.union (freeVars b2 l) (freeVars b2 r))
                    | _ -> ()
            | _ -> ()
        (match fin with Some fe -> Set.union acc (freeVars b fe) | None -> acc)
    | ExprKind.ExprField (i, _) -> freeVars bound i
    | ExprKind.ExprMatch (s, cases) ->
        Set.unionMany (freeVars bound s :: (cases |> List.map (fun c -> freeVars bound c.Body)))
    | _ -> Set.empty

// ============================================================================
// Certified-signature table
// ============================================================================

/// Pre-scan: every DeclFunction carrying a normalized ("__ml_galilean", args)
/// conjunct. The args NAME the boost-variant parameters; every other
/// parameter is boost-invariant. Errors are BL4009 at the decl.
let buildCertTable (decls: Located<Decl> list)
    : Result<Map<string, GalSig>, Blade.Diagnostics.Diagnostic> =
    decls
    |> List.fold (fun acc d ->
        acc |> Result.bind (fun table ->
            match d.Value with
            | DeclFunction fd ->
                let conjs =
                    fd.WhereClause
                    |> Option.map (fun w -> w.Custom)
                    |> Option.defaultValue []
                    |> List.filter (fun (n, _) -> n = "__ml_galilean")
                let fail msg = Error (bl4009 d.Span msg)
                match conjs with
                | [] -> Ok table
                | _ :: _ :: _ -> fail (sprintf "function '%s': duplicate galilean constraints — declare one, listing every boost-variant parameter" fd.Name)
                | [ (_, args) ] ->
                    if args.IsEmpty then
                        fail (sprintf "function '%s': galilean(...) must name at least one boost-variant (velocity) parameter" fd.Name)
                    else
                        let pNames = fd.Params |> List.map (fun p -> p.Name)
                        match args |> List.tryFind (fun a -> not (List.contains a pNames)) with
                        | Some bad ->
                            fail (sprintf "function '%s': galilean argument '%s' is not a parameter of this function" fd.Name bad)
                        | None ->
                            let ps =
                                fd.Params
                                |> List.map (fun p ->
                                    (p.Name, if List.contains p.Name args then BVar else BInv))
                            Ok (Map.add fd.Name { Params = ps } table)
            | _ -> Ok table))
        (Ok Map.empty)

/// Aliases bound to `sgs` (name-only knowledge — no project dependency on
/// the sgs elaborator; without `import sgs` the axioms are simply absent).
let sgsAliasesOf (decls: Located<Decl> list) : Set<string> =
    decls |> List.fold (fun set d ->
        match d.Value with
        | DeclImport (["sgs"], ImportQualified aliasOpt) ->
            Set.add (aliasOpt |> Option.defaultValue "sgs") set
        | _ -> set) Set.empty

// ============================================================================
// The judgment
// ============================================================================

type private Ctx = {
    FuncName: string
    /// ml-module aliases (every ml.* op is BInv-only).
    MlAliases: Set<string>
    /// sgs-module aliases (grad/stress/box_filter axioms).
    SgsAliases: Set<string>
    Certs: Map<string, GalSig>
}

let rec private judge (ctx: Ctx) (env: Map<string, BoostStatus>) (e: Expr)
    : Result<BoostStatus, Blade.Diagnostics.Diagnostic> =
    let reject msg = Error (bl4009 e.Span (sprintf "function '%s': %s" ctx.FuncName msg))
    let j = judge ctx env
    match e.Kind with
    | ExprKind.ExprLit _ -> Ok BInv
    | ExprKind.ExprArrayLit es | ExprKind.ExprTuple es ->
        // a uniformly boost-variant aggregate shifts componentwise and stays
        // BVar; mixing statuses inside one aggregate loses the coefficient.
        es
        |> List.fold (fun acc x ->
            acc |> Result.bind (fun sts -> j x |> Result.map (fun s -> sts @ [ s ])))
            (Ok [])
        |> Result.bind (fun sts ->
            match sts with
            | [] -> Ok BInv
            | s :: rest when rest |> List.forall ((=) s) -> Ok s
            | _ -> reject "an aggregate mixing boost-variant and boost-invariant elements has no single U0-coefficient — split it")
    | ExprKind.ExprVar n ->
        match Map.tryFind n env with
        | Some st -> Ok st
        | None -> Ok BInv // globals/constants/builtins: held fixed by the conditional theorem
    | ExprKind.ExprDotDot _ -> Ok BInv
    | ExprKind.ExprTyped (inner, _) -> j inner
    | ExprKind.ExprUnaryOp (_, inner) ->
        j inner |> Result.bind (fun si ->
            match si with
            | BInv -> Ok BInv
            | BVar -> reject "negating a boost-variant value flips its U0-coefficient to -1 — difference two velocities instead (v2's coefficient tracking will admit this)"
            | BOpaque -> Ok BOpaque)
    | ExprKind.ExprBinOp (_, op, l, r) ->
        j l |> Result.bind (fun sl ->
        j r |> Result.bind (fun sr ->
            match sl, sr, op with
            | BVar, BVar, OpSub -> Ok BInv // THE rule: the boost cancels
            | BVar, BVar, OpAdd ->
                reject "adding two boost-variant values doubles the U0-coefficient — subtract them (differences are boost-invariant) or average through sgs.box_filter"
            | BVar, BVar, _ ->
                reject "this operator is nonlinear in the frame velocity — take differences first"
            | BVar, BInv, (OpAdd | OpSub) -> Ok BVar
            | BInv, BVar, OpAdd -> Ok BVar
            | BInv, BVar, OpSub ->
                reject "invariant - velocity carries U0-coefficient -1 — write (velocity - invariant) or difference two velocities (v2's coefficient tracking will admit this)"
            | BVar, _, (OpMul | OpDiv) | _, BVar, (OpMul | OpDiv) ->
                reject "scaling a boost-variant value scales the U0-coefficient — only differences of velocities (and the sgs formers) are boost-invariant"
            | BVar, _, _ | _, BVar, _ ->
                reject "this operator does not preserve the U0-coefficient of a boost-variant value"
            | BInv, BInv, _ -> Ok BInv
            | BOpaque, _, _ | _, BOpaque, _ -> Ok BOpaque))
    | ExprKind.ExprIf (c, t, f) ->
        j c |> Result.bind (fun sc ->
            match sc with
            | BInv ->
                j t |> Result.bind (fun st ->
                j f |> Result.bind (fun sf ->
                    if st = sf then Ok st
                    else reject (sprintf "if branches disagree: then-branch is %s, else-branch is %s" (statusStr st) (statusStr sf))))
            | _ -> reject "an if condition inside a galilean-certified body must be boost-invariant — branching on a frame-dependent value makes the result frame-dependent")
    | ExprKind.ExprMatch (scrut, cases) ->
        j scrut |> Result.bind (fun ss ->
            match ss with
            | BInv ->
                cases
                |> List.fold (fun acc c ->
                    acc |> Result.bind (fun sts ->
                        judge ctx (List.fold (fun m v -> Map.add v BInv m) env (patternVars c.Pattern)) c.Body
                        |> Result.map (fun s -> sts @ [ s ])))
                    (Ok [])
                |> Result.bind (fun sts ->
                    match sts with
                    | [] -> Ok BInv
                    | s :: rest when rest |> List.forall ((=) s) -> Ok s
                    | _ -> reject "match arms disagree on their boost status")
            | _ -> reject "a match scrutinee inside a galilean-certified body must be boost-invariant")
    | ExprKind.ExprLet (binding, body) ->
        j binding.Value |> Result.bind (fun sv ->
            match binding.Pattern.Kind, sv with
            | PatternKind.PatVar n, _ -> judge ctx (Map.add n sv env) body
            | _, BInv -> judge ctx (List.fold (fun m v -> Map.add v BInv m) env (patternVars binding.Pattern)) body
            | _, _ -> reject "cannot destructure a boost-variant value in v1 — bind it whole")
    | ExprKind.ExprLambda (ps, _, lamBody) ->
        let captured = freeVars (Set.ofList (ps |> List.map (fun p -> p.Name))) lamBody
        let varCapture =
            captured |> Set.toList |> List.tryFind (fun n ->
                match Map.tryFind n env with Some BVar -> true | _ -> false)
        match varCapture with
        | Some n -> reject (sprintf "lambda captures boost-variant '%s' — factor velocity work into galilean-certified functions instead" n)
        | None -> Ok BInv
    | ExprKind.ExprAssign (l, r) ->
        judgeAssign ctx env e.Span l r |> Result.map (fun () -> BInv)
    | ExprKind.ExprBlock (stmts, finalE) ->
        judgeStmts ctx env stmts
        |> Result.bind (fun env' ->
            match finalE with
            | Some fe -> judge ctx env' fe
            | None -> Ok BInv)
    | ExprKind.ExprApp (f, args) -> judgeApp ctx env e f args
    | ExprKind.ExprField (_, _) -> Ok BOpaque
    | _ -> Ok BOpaque

and private judgeStmts (ctx: Ctx) (env: Map<string, BoostStatus>) (stmts: Stmt list)
    : Result<Map<string, BoostStatus>, Blade.Diagnostics.Diagnostic> =
    stmts
    |> List.fold (fun acc s ->
        acc |> Result.bind (fun env ->
            match unwrapStmt s with
            | StmtLet binding ->
                judge ctx env binding.Value |> Result.bind (fun sv ->
                    match binding.Pattern.Kind, sv with
                    | PatternKind.PatVar n, _ -> Ok (Map.add n sv env)
                    | _, BInv -> Ok (List.fold (fun m v -> Map.add v BInv m) env (patternVars binding.Pattern))
                    | _, _ ->
                        Error (bl4009 binding.Value.Span (sprintf "function '%s': cannot destructure a boost-variant value in v1 — bind it whole" ctx.FuncName)))
            | StmtExpr e2 -> judge ctx env e2 |> Result.map (fun _ -> env)
            | StmtAssign (l, _, r) -> judgeAssign ctx env l.Span l r |> Result.map (fun () -> env)
            | StmtForIn (v, range, body) ->
                judge ctx env range |> Result.bind (fun sr ->
                    match sr with
                    | BVar ->
                        Error (bl4009 range.Span (sprintf "function '%s': cannot iterate a boost-variant value as a range" ctx.FuncName))
                    | _ ->
                        judgeStmts ctx (Map.add v BInv env) body |> Result.map (fun _ -> env))
            | _ -> Ok env))
        (Ok env)

/// Assignments: whole-variable writes must preserve boost status; element
/// writes must match the container's status (a BInv element inside a BVar
/// array would break its uniform shift, and vice versa).
and private judgeAssign (ctx: Ctx) (env: Map<string, BoostStatus>) (span: Span) (l: Expr) (r: Expr)
    : Result<unit, Blade.Diagnostics.Diagnostic> =
    let fail msg = Error (bl4009 span (sprintf "function '%s': %s" ctx.FuncName msg))
    judge ctx env r |> Result.bind (fun sr ->
        match l.Kind with
        | ExprKind.ExprVar n ->
            match Map.tryFind n env with
            | Some st when st = sr -> Ok ()
            | Some st -> fail (sprintf "assignment changes '%s' from %s to %s — a mut binding must keep one boost status" n (statusStr st) (statusStr sr))
            | None -> Ok ()
        | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar n }, idxArgs) ->
            // element write: indices must be boost-invariant, the value must
            // match the container's status.
            idxArgs
            |> List.fold (fun acc a ->
                acc |> Result.bind (fun () ->
                    judge ctx env a |> Result.bind (fun si ->
                        if si = BInv then Ok ()
                        else fail "array indices must be boost-invariant")))
                (Ok ())
            |> Result.bind (fun () ->
                match Map.tryFind n env, sr with
                | Some BVar, BVar -> Ok ()
                | Some BVar, _ -> fail (sprintf "writing a non-boost-variant element into boost-variant '%s' breaks its uniform frame shift" n)
                | _, BVar -> fail "cannot store a boost-variant value into a boost-invariant container"
                | _, _ -> Ok ())
        | _ ->
            match sr with
            | BVar -> fail "unsupported assignment target for a boost-variant value"
            | _ -> Ok ())

and private judgeApp (ctx: Ctx) (env: Map<string, BoostStatus>) (e: Expr) (f: Expr) (args: Expr list)
    : Result<BoostStatus, Blade.Diagnostics.Diagnostic> =
    let reject msg = Error (bl4009 e.Span (sprintf "function '%s': %s" ctx.FuncName msg))
    let judgeAll args =
        args
        |> List.fold (fun acc a ->
            acc |> Result.bind (fun sts -> judge ctx env a |> Result.map (fun s -> sts @ [ s ])))
            (Ok [])
    match f.Kind with
    // --- sgs formers: the axiomatic rules ---------------------------------
    | ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, op) when Set.contains alias ctx.SgsAliases ->
        (match op, args with
         | "grad", [ uE; dxE ] ->
             judge ctx env uE |> Result.bind (fun su ->
             judge ctx env dxE |> Result.bind (fun sdx ->
                 if sdx <> BInv then reject "grad: the grid spacing must be boost-invariant"
                 elif su = BOpaque then reject "grad: cannot classify the field argument"
                 else Ok BInv)) // difference weights sum to 0: kills the boost
         | "stress", [ uE; wE ] ->
             judge ctx env uE |> Result.bind (fun su ->
             judge ctx env wE |> Result.bind (fun sw ->
                 if sw <> BInv then reject "stress: the tile width must be boost-invariant"
                 elif su = BOpaque then reject "stress: cannot classify the field argument"
                 else Ok BInv)) // a central comoment: boost-invariant by construction
         | "box_filter", [ uE; wE ] ->
             judge ctx env uE |> Result.bind (fun su ->
             judge ctx env wE |> Result.bind (fun sw ->
                 if sw <> BInv then reject "box_filter: the tile width must be boost-invariant"
                 else Ok su)) // weights sum to 1: preserves the boost status
         | _ ->
             judgeAll args |> Result.bind (fun sts ->
                 if sts |> List.forall ((=) BInv) then Ok BInv
                 else reject (sprintf "sgs.%s carries no galilean axiom for boost-variant arguments" op)))
    // --- ml ops: invariants in, invariants out ----------------------------
    | ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, op) when Set.contains alias ctx.MlAliases ->
        judgeAll args |> Result.bind (fun sts ->
            if sts |> List.forall ((=) BInv) then Ok BInv
            else reject (sprintf "ml.%s does not accept boost-variant arguments — velocities enter models only through boost-invariant combinations (differences, sgs.grad, sgs.stress)" op))
    // --- named callees ----------------------------------------------------
    | ExprKind.ExprVar fn ->
        match Map.tryFind fn ctx.Certs with
        | Some cert ->
            if List.length args <> List.length cert.Params then
                reject (sprintf "call to '%s': expected %d arguments" fn (List.length cert.Params))
            else
                (List.zip cert.Params args)
                |> List.fold (fun acc ((pName, pSt), argE) ->
                    acc |> Result.bind (fun () ->
                        judge ctx env argE |> Result.bind (fun sa ->
                            if sa = pSt then Ok ()
                            else
                                Error (bl4009 argE.Span
                                           (sprintf "function '%s': '%s' parameter '%s' is %s, but the argument is %s"
                                                ctx.FuncName fn pName (statusStr pSt) (statusStr sa))))))
                    (Ok ())
                |> Result.map (fun () -> BInv) // v1: certified functions return boost-invariant values
        | None ->
            match Map.tryFind fn env with
            | Some BVar ->
                // indexing a boost-variant array: per-component and
                // index-stable — the elements are themselves boost-variant.
                judgeAll args |> Result.bind (fun sts ->
                    if sts |> List.forall ((=) BInv) then Ok BVar
                    else reject (sprintf "indexing into boost-variant '%s' requires boost-invariant indices" fn))
            | Some BInv | None ->
                // uncertified callee / builtin / plain-array read: a function
                // of boost-invariant values is boost-invariant; a BVar
                // argument escapes the discipline.
                judgeAll args |> Result.bind (fun sts ->
                    match sts |> List.tryFindIndex (fun s -> s <> BInv) with
                    | None -> Ok BInv
                    | Some i ->
                        Error (bl4009 args.[i].Span
                                   (sprintf "function '%s': a boost-variant value escapes to '%s', which carries no galilean certificate — certify it with `where ml.galilean(...)` or pass only boost-invariant combinations (differences, sgs.grad, sgs.stress)" ctx.FuncName fn)))
            | Some BOpaque -> reject (sprintf "cannot classify the callee '%s'" fn)
    | _ ->
        judgeAll args |> Result.bind (fun sts ->
            judge ctx env f |> Result.bind (fun sf ->
                if sf = BInv && sts |> List.forall ((=) BInv) then Ok BInv
                else reject "cannot classify this call inside a galilean-certified body"))

/// Judge one certified function: seed the env from the conjunct, require
/// the body boost-invariant (v1 — boost-variant returns are the documented
/// v2 extension alongside coefficient tracking).
let judgeFunction (certs: Map<string, GalSig>) (mlAliases: Set<string>) (sgsAliases: Set<string>)
                  (fd: FunctionDecl)
    : Blade.Diagnostics.Diagnostic list =
    match Map.tryFind fd.Name certs with
    | None -> []
    | Some cert ->
        let ctx = { FuncName = fd.Name; MlAliases = mlAliases; SgsAliases = sgsAliases; Certs = certs }
        let env = cert.Params |> List.fold (fun m (n, st) -> Map.add n st m) Map.empty
        match judge ctx env fd.Body with
        | Error d -> [ d ]
        | Ok BInv -> []
        | Ok st ->
            [ bl4009 fd.Body.Span
                  (sprintf "function '%s': the body is %s — a galilean-certified function must return a boost-invariant value in v1 (velocity-returning steppers are future work)" fd.Name (statusStr st)) ]

// ============================================================================
// Constraint-registry handler
// ============================================================================

/// `galilean(u, ...)` is a callee-side theorem: Validate re-checks the
/// conjunct shape (the elaborator has already judged the body by the time
/// checkFunctionDecl runs), the license scope is unused, and call sites
/// carry no obligation.
let private galileanHandler : Blade.Constraints.ConstraintHandler = {
    Describe = "galilean(u, ...) — certifies the function invariant under a constant Galilean boost of the listed velocity parameters; the ML elaborator proves the body combines them only boost-invariantly"
    Validate = fun funcName paramNames args ->
        if args.IsEmpty then
            Error (sprintf "function '%s': galilean(...) must name at least one boost-variant (velocity) parameter" funcName)
        else
            match args |> List.tryFind (fun a -> not (List.contains a paramNames)) with
            | Some bad -> Error (sprintf "function '%s': galilean argument '%s' is not a parameter of this function" funcName bad)
            | None -> Ok ()
    EnterBody = fun _ _ -> ()
    ExitBody = fun _ _ -> ()
    Discharge = fun _ _ _ -> Ok ()
}

let mutable private registered = false

let register () =
    if not registered then
        registered <- true
        Blade.Constraints.registerConstraint "__ml_galilean" galileanHandler
