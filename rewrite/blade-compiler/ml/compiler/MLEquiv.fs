/// The `where ml.equiv(G)` discipline: a function carrying the (normalized)
/// `__ml_equiv` conjunct is PROVED equivariant by construction — its body
/// may compose only equivariance-preserving operations. The judgment is an
/// abstract interpretation over the surface AST, run by MLElaborate at the
/// seam between pass 1 (sizing normalization + resolveStatics) and pass 2
/// (op rewriting), where `ml.*` op calls are still surface-visible and
/// specs resolve through the SAME static machinery elaboration uses.
///
/// The certificate is a conditional theorem: IF every representation-typed
/// argument actually transforms as its declared IrrepsIdx spec (and, for
/// y_to, the coordinate scalars are the components of the standard vector),
/// and invariant arguments (weights included) are held fixed, THEN the
/// result transforms as its declared spec. Violations are BL4008 at the
/// offending expression's span.
///
/// Abstract value domain:
///   Rep spec — transforms as the block-diagonal rep described by spec;
///   Inv      — invariant (scalars, plain arrays, weights, norms);
///   Opaque   — unclassifiable; rejected wherever it meets a rep-relevant
///              position (op argument, return value).
module Blade.ML.Equiv

open Blade.Ast
open Blade.StaticEval
open Blade.ML.Spec

type Group =
    | O3
    | SO3

type RepStatus =
    | Rep of Spec
    | Inv
    | Opaque

type CertSig = {
    Group: Group
    /// Parameter name -> status, in declaration order.
    Params: (string * RepStatus) list
    Return: RepStatus
}

// ============================================================================
// Helpers
// ============================================================================

let private fuel = 100_000

let private bl4008 (span: Span) (msg: string) : Blade.Diagnostics.Diagnostic =
    Blade.Diagnostics.mkError "BL4008" (Blade.Diagnostics.Codes.phaseOfCode "BL4008") span msg

let private specStr (s: Spec) : string =
    s
    |> List.map (fun e -> sprintf "(%d, %d, %d)" e.L e.Parity e.Mult)
    |> String.concat ", "
    |> sprintf "[%s]"

let private statusStr (st: RepStatus) : string =
    match st with
    | Rep s -> sprintf "representation-typed (transforms as IrrepsIdx<%s>)" (specStr s)
    | Inv -> "invariant"
    | Opaque -> "unclassifiable"

let private groupStr (g: Group) = match g with O3 -> "O3" | SO3 -> "SO3"

/// Mirror of MLElaborate.staticArg (keep in sync): an ML op's static
/// argument is a `let static` binding name or an inline int literal.
let private staticArgValue (statics: StaticEnv) (e: Expr) : Result<StaticValue, string> =
    match e.Kind with
    | ExprKind.ExprLit (LitInt n) -> Ok (SVInt n)
    | ExprKind.ExprVar name ->
        match Map.tryFind name statics.Values with
        | Some sv -> Ok sv
        | None -> Error (sprintf "'%s' is not a `let static` binding" name)
    | _ -> Error "expected a `let static` binding name or literal"

let private specOfArg (statics: StaticEnv) (what: string) (e: Expr) : Result<Spec, string> =
    staticArgValue statics e
    |> Result.bind (Blade.ML.Statics.specOfStatic what)

/// Static-offset read admissibility: under O3 only (l=0, even) blocks hold
/// full invariants; under SO3 any l=0 block does (pseudoscalars are
/// SO(3)-invariant). l=0 blocks have dim 1, so block b spans
/// [start_b .. start_b + mult_b).
let private invariantOffsets (g: Group) (s: Spec) : Set<int> =
    let starts = blockStarts s
    [ for b in 0 .. s.Length - 1 do
        let e = s.[b]
        if e.L = 0 && (g = SO3 || e.Parity = 0) then
            yield! [ starts.[b] .. starts.[b] + e.Mult - 1 ] ]
    |> Set.ofList

/// All pattern variables of a pattern (for Inv destructuring).
let rec private patternVars (p: Pattern) : string list =
    match p.Kind with
    | PatternKind.PatVar n -> [ n ]
    | PatternKind.PatTuple ps -> ps |> List.collect patternVars
    | _ -> []

// ============================================================================
// Certified-signature table
// ============================================================================

/// Type aliases of this module (one-level chase for `type X = IrrepsIdx<..>`
/// inside Array annotations, mirroring registerTypeDecl's transparency).
let private aliasMapOf (decls: Located<Decl> list) : Map<string, TypeExpr> =
    decls
    |> List.fold (fun m d ->
        match d.Value with
        | DeclType (TyDeclAlias (n, [], body)) -> Map.add n body m
        | _ -> m) Map.empty

let private parseGroup (funcName: string) (args: string list) : Result<Group, string> =
    match args with
    | [ "O3" ] -> Ok O3
    | [ "SO3" ] -> Ok SO3
    | [ g ] -> Error (sprintf "function '%s': equiv(%s) — unknown group '%s'; supported: O3, SO3" funcName g g)
    | _ -> Error (sprintf "function '%s': equiv expects exactly one group argument — equiv(O3) or equiv(SO3)" funcName)

/// Classify a signature annotation. Certified functions must be fully
/// annotated; Rep needs `Array<T like IrrepsIdx<spec>>` (directly or via a
/// one-level type alias), scalars and plain arrays are Inv.
let rec private statusOfType (aliases: Map<string, TypeExpr>) (statics: StaticEnv) (t: TypeExpr)
    : Result<RepStatus, string> =
    match t with
    | TyArray (_, idxs) ->
        let irreps = idxs |> List.choose (function TyIrrepsIdx s -> Some s | _ -> None)
        match irreps, idxs.Length with
        | [], _ -> Ok Inv
        | [ specExpr ], 1 ->
            evalExpr statics fuel specExpr
            |> Result.bind (Blade.ML.Statics.specOfStatic "equiv signature spec")
            |> Result.map Rep
        | _ ->
            Error "multi-index arrays mixing IrrepsIdx are not supported in equiv-certified signatures"
    | TyNamed (n, []) ->
        match Map.tryFind n aliases with
        | Some body -> statusOfType aliases statics (TyArray (TyNamed ("Float", []), [ body ]))
        | None -> Ok Inv // scalar primitives and non-rep named types are invariant
    | TyNamed (_, _) -> Ok Inv
    | TyIrrepsIdx specExpr ->
        // alias body position (`type X = IrrepsIdx<s>` chased above)
        evalExpr statics fuel specExpr
        |> Result.bind (Blade.ML.Statics.specOfStatic "equiv signature spec")
        |> Result.map Rep
    | TyInt32 | TyInt64 | TyFloat32 | TyFloat64 | TyBool | TyComplex128 -> Ok Inv
    | _ -> Error "cannot classify this annotation in an equiv-certified signature (supported: scalars, plain arrays, Array<_ like IrrepsIdx<spec>>)"

/// Pre-scan: every DeclFunction carrying a normalized ("__ml_equiv", [g])
/// conjunct gets a certified signature. Errors are BL4008 at the decl.
let buildCertTable (statics: StaticEnv) (decls: Located<Decl> list)
    : Result<Map<string, CertSig>, Blade.Diagnostics.Diagnostic> =
    let aliases = aliasMapOf decls
    let certDecls =
        decls
        |> List.choose (fun d ->
            match d.Value with
            | DeclFunction fd ->
                let conjs =
                    fd.WhereClause
                    |> Option.map (fun w -> w.Custom)
                    |> Option.defaultValue []
                    |> List.filter (fun (n, _) -> n = "__ml_equiv")
                match conjs with
                | [] -> None
                | cs -> Some (d.Span, fd, cs)
            | _ -> None)
    certDecls
    |> List.fold (fun acc (span, fd, conjs) ->
        acc |> Result.bind (fun table ->
            let fail msg = Error (bl4008 span msg)
            match conjs with
            | _ :: _ :: _ -> fail (sprintf "function '%s': duplicate equiv constraints — declare exactly one group" fd.Name)
            | [ (_, gArgs) ] ->
                match parseGroup fd.Name gArgs with
                | Error m -> fail m
                | Ok g ->
                    let paramSt =
                        fd.Params
                        |> List.fold (fun acc p ->
                            acc |> Result.bind (fun ps ->
                                match p.Type with
                                | None ->
                                    Error (sprintf "function '%s': an equiv-certified function must annotate every parameter and its return type ('%s' is unannotated)" fd.Name p.Name)
                                | Some t ->
                                    statusOfType aliases statics t
                                    |> Result.mapError (sprintf "function '%s', parameter '%s': %s" fd.Name p.Name)
                                    |> Result.map (fun st -> ps @ [ (p.Name, st) ])))
                            (Ok [])
                    match paramSt with
                    | Error m -> fail m
                    | Ok ps ->
                        match fd.ReturnType with
                        | None -> fail (sprintf "function '%s': an equiv-certified function must annotate its return type" fd.Name)
                        | Some rt ->
                            match statusOfType aliases statics rt
                                  |> Result.mapError (sprintf "function '%s', return type: %s" fd.Name) with
                            | Error m -> fail m
                            | Ok r ->
                                Ok (Map.add fd.Name { Group = g; Params = ps; Return = r } table)
            | [] -> Ok table))
        (Ok Map.empty)

// ============================================================================
// The judgment
// ============================================================================

type private Ctx = {
    Group: Group
    FuncName: string
    Aliases: Set<string>
    Statics: StaticEnv
    Certs: Map<string, CertSig>
}

/// Free variables of an expression that are NOT locally bound (used for the
/// lambda capture rule).
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

/// The scalar builtins a certified body may apply to invariants.
let private isKnownScalarBuiltin (n: string) =
    n.StartsWith "__ml_stat_"
    || List.contains n [ "exp"; "log"; "sqrt"; "sin"; "cos"; "tan"; "tanh"; "abs"; "floor"; "ceil"; "min"; "max"; "pow" ]

let rec private judge (ctx: Ctx) (env: Map<string, RepStatus>) (e: Expr)
    : Result<RepStatus, Blade.Diagnostics.Diagnostic> =
    let reject msg = Error (bl4008 e.Span (sprintf "function '%s': %s" ctx.FuncName msg))
    let j = judge ctx env
    match e.Kind with
    | ExprKind.ExprLit _ -> Ok Inv
    | ExprKind.ExprArrayLit es | ExprKind.ExprTuple es ->
        // constants and invariant aggregates are invariant; anything rep-
        // valued inside an aggregate loses its rep structure -> reject.
        es
        |> List.fold (fun acc x ->
            acc |> Result.bind (fun st ->
                j x |> Result.bind (fun sx ->
                    match sx with
                    | Inv -> Ok st
                    | Rep _ -> reject "a representation-typed value may not be packed into a literal aggregate — the aggregate does not transform as a rep"
                    | Opaque -> Ok Opaque)))
            (Ok Inv)
    | ExprKind.ExprVar n ->
        match Map.tryFind n env with
        | Some st -> Ok st
        | None -> Ok Inv // globals/constants/builtins: invariant by the conditional-theorem reading
    | ExprKind.ExprDotDot _ -> Ok Inv
    | ExprKind.ExprTyped (inner, _) -> j inner
    | ExprKind.ExprUnaryOp (_, inner) -> j inner
    | ExprKind.ExprBinOp (_, op, l, r) ->
        j l |> Result.bind (fun sl ->
        j r |> Result.bind (fun sr ->
            match sl, sr, op with
            | Rep s1, Rep s2, (OpAdd | OpSub) ->
                if s1 = s2 then Ok (Rep s1)
                else reject (sprintf "cannot add values of different representations — left transforms as IrrepsIdx<%s>, right as IrrepsIdx<%s>" (specStr s1) (specStr s2))
            | Rep _, Rep _, OpMul ->
                reject "elementwise product of representation-typed values is not equivariant — use ml.tensor_product, the Clebsch-Gordan-typed contraction"
            | Rep _, Rep _, _ ->
                reject "this operator is not equivariant on representation-typed values"
            | Rep s, Inv, OpMul | Inv, Rep s, OpMul -> Ok (Rep s)
            | Rep s, Inv, OpDiv -> Ok (Rep s)
            | (Rep _, Inv, _) | (Inv, Rep _, _) ->
                reject "mixing a representation-typed value with an invariant under this operator breaks equivariance (only scaling — * and / by an invariant — preserves the rep)"
            | Inv, Inv, _ -> Ok Inv
            | Opaque, _, _ | _, Opaque, _ -> Ok Opaque))
    | ExprKind.ExprIf (c, t, f) ->
        j c |> Result.bind (fun sc ->
            match sc with
            | Inv ->
                j t |> Result.bind (fun st ->
                j f |> Result.bind (fun sf ->
                    if st = sf then Ok st
                    else reject (sprintf "if branches disagree: then-branch is %s, else-branch is %s" (statusStr st) (statusStr sf))))
            | _ -> reject "an if condition inside an equiv-certified body must be invariant")
    | ExprKind.ExprMatch (scrut, cases) ->
        j scrut |> Result.bind (fun ss ->
            match ss with
            | Inv ->
                cases
                |> List.fold (fun acc c ->
                    acc |> Result.bind (fun sts ->
                        judge ctx (List.fold (fun m v -> Map.add v Inv m) env (patternVars c.Pattern)) c.Body
                        |> Result.map (fun s -> sts @ [ s ])))
                    (Ok [])
                |> Result.bind (fun sts ->
                    match sts with
                    | [] -> Ok Inv
                    | s :: rest when rest |> List.forall ((=) s) -> Ok s
                    | _ -> reject "match arms disagree on their representation status")
            | _ -> reject "a match scrutinee inside an equiv-certified body must be invariant")
    | ExprKind.ExprLet (binding, body) ->
        j binding.Value |> Result.bind (fun sv ->
            match binding.Pattern.Kind, sv with
            | PatternKind.PatVar n, _ -> judge ctx (Map.add n sv env) body
            | _, Inv -> judge ctx (List.fold (fun m v -> Map.add v Inv m) env (patternVars binding.Pattern)) body
            | _, _ -> reject "cannot destructure a representation-typed value — its components are basis-dependent")
    | ExprKind.ExprLambda (ps, _, lamBody) ->
        // v1: a lambda is admissible only when it never touches a rep —
        // free vars must all be non-Rep; it is then an invariant helper.
        let captured = freeVars (Set.ofList (ps |> List.map (fun p -> p.Name))) lamBody
        let repCapture =
            captured |> Set.toList |> List.tryFind (fun n ->
                match Map.tryFind n env with Some (Rep _) -> true | _ -> false)
        match repCapture with
        | Some n -> reject (sprintf "lambda captures representation-typed '%s' — factor rep work into equiv-certified functions instead" n)
        | None -> Ok Inv
    | ExprKind.ExprAssign (l, r) ->
        judgeAssign ctx env e.Span l r |> Result.map (fun () -> Inv)
    | ExprKind.ExprBlock (stmts, finalE) ->
        judgeStmts ctx env stmts
        |> Result.bind (fun env' ->
            match finalE with
            | Some fe -> judge ctx env' fe
            | None -> Ok Inv)
    | ExprKind.ExprApp (f, args) -> judgeApp ctx env e f args
    | ExprKind.ExprField (_, _) -> Ok Opaque
    | _ -> Ok Opaque

and private judgeStmts (ctx: Ctx) (env: Map<string, RepStatus>) (stmts: Stmt list)
    : Result<Map<string, RepStatus>, Blade.Diagnostics.Diagnostic> =
    stmts
    |> List.fold (fun acc s ->
        acc |> Result.bind (fun env ->
            match unwrapStmt s with
            | StmtLet binding ->
                judge ctx env binding.Value |> Result.bind (fun sv ->
                    match binding.Pattern.Kind, sv with
                    | PatternKind.PatVar n, _ -> Ok (Map.add n sv env)
                    | _, Inv -> Ok (List.fold (fun m v -> Map.add v Inv m) env (patternVars binding.Pattern))
                    | _, _ ->
                        Error (bl4008 binding.Value.Span (sprintf "function '%s': cannot destructure a representation-typed value — its components are basis-dependent" ctx.FuncName)))
            | StmtExpr e2 -> judge ctx env e2 |> Result.map (fun _ -> env)
            | StmtAssign (l, _, r) -> judgeAssign ctx env l.Span l r |> Result.map (fun () -> env)
            | StmtForIn (v, range, body) ->
                judge ctx env range |> Result.bind (fun sr ->
                    match sr with
                    | Rep _ ->
                        Error (bl4008 range.Span (sprintf "function '%s': cannot iterate over a representation-typed value's components — they are basis-dependent" ctx.FuncName))
                    | _ ->
                        judgeStmts ctx (Map.add v Inv env) body |> Result.map (fun _ -> env))
            | _ -> Ok env))
        (Ok env)

/// Assignments: whole-variable writes must preserve the target's status;
/// element writes into a rep are rejected (that is exactly the raw access
/// the discipline forbids); element writes into invariants need invariant
/// values.
and private judgeAssign (ctx: Ctx) (env: Map<string, RepStatus>) (span: Span) (l: Expr) (r: Expr)
    : Result<unit, Blade.Diagnostics.Diagnostic> =
    let fail msg = Error (bl4008 span (sprintf "function '%s': %s" ctx.FuncName msg))
    judge ctx env r |> Result.bind (fun sr ->
        match l.Kind with
        | ExprKind.ExprVar n ->
            match Map.tryFind n env with
            | Some st when st = sr -> Ok ()
            | Some st -> fail (sprintf "assignment changes '%s' from %s to %s — a mut binding must keep one representation status" n (statusStr st) (statusStr sr))
            | None -> Ok ()
        | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar n }, _) ->
            match Map.tryFind n env with
            | Some (Rep _) -> fail (sprintf "element-assignment into representation-typed '%s' writes a basis-dependent component — build reps only through equivariant ops" n)
            | _ ->
                match sr with
                | Rep _ -> fail "cannot store a representation-typed value into an array element"
                | _ -> Ok ()
        | _ ->
            match sr with
            | Rep _ -> fail "unsupported assignment target for a representation-typed value"
            | _ -> Ok ())

and private judgeApp (ctx: Ctx) (env: Map<string, RepStatus>) (e: Expr) (f: Expr) (args: Expr list)
    : Result<RepStatus, Blade.Diagnostics.Diagnostic> =
    let reject msg = Error (bl4008 e.Span (sprintf "function '%s': %s" ctx.FuncName msg))
    let judgeAll args =
        args
        |> List.fold (fun acc a ->
            acc |> Result.bind (fun sts -> judge ctx env a |> Result.map (fun s -> sts @ [ s ])))
            (Ok [])
    let requireRep (what: string) (expected: Spec) (argE: Expr) =
        judge ctx env argE |> Result.bind (fun s ->
            match s with
            | Rep sp when sp = expected -> Ok ()
            | Rep sp ->
                Error (bl4008 argE.Span (sprintf "function '%s': %s expects a value transforming as IrrepsIdx<%s>, got IrrepsIdx<%s>" ctx.FuncName what (specStr expected) (specStr sp)))
            | Inv ->
                Error (bl4008 argE.Span (sprintf "function '%s': %s expects a representation-typed value (transforming as IrrepsIdx<%s>) — an invariant here would not co-rotate with the inputs" ctx.FuncName what (specStr expected)))
            | Opaque ->
                Error (bl4008 argE.Span (sprintf "function '%s': cannot classify the argument to %s" ctx.FuncName what)))
    let requireInv (what: string) (argE: Expr) =
        judge ctx env argE |> Result.bind (fun s ->
            match s with
            | Inv -> Ok ()
            | Rep _ ->
                Error (bl4008 argE.Span (sprintf "function '%s': %s must be invariant, but the argument is representation-typed — extract invariants with ml.scalars/ml.norms or contract with ml.tensor_product" ctx.FuncName what))
            | Opaque ->
                Error (bl4008 argE.Span (sprintf "function '%s': cannot classify the argument to %s" ctx.FuncName what)))
    match f.Kind with
    // --- qualified ML ops (surface-visible pre-rewrite) -------------------
    | ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, op) when Set.contains alias ctx.Aliases ->
        let specArg what e =
            specOfArg ctx.Statics what e
            |> Result.mapError (fun m -> bl4008 e.Span (sprintf "function '%s': %s: %s" ctx.FuncName what m))
        match op, args with
        | "y_to", [ lmaxE; xE; yE; zE ] ->
            requireInv "y_to coordinate x" xE |> Result.bind (fun () ->
            requireInv "y_to coordinate y" yE |> Result.bind (fun () ->
            requireInv "y_to coordinate z" zE |> Result.bind (fun () ->
                match staticArgValue ctx.Statics lmaxE with
                | Ok (SVInt lmax) when lmax >= 0L -> Ok (Rep (shSpec (int lmax)))
                | _ -> reject "y_to: lmax must be a static int")))
        | "tensor_product", [ cfgE; xE; yE; wE ] ->
            staticArgValue ctx.Statics cfgE
            |> Result.bind (Blade.ML.Statics.cfgOfStatic "tensor_product")
            |> Result.mapError (fun m -> bl4008 cfgE.Span (sprintf "function '%s': tensor_product: %s" ctx.FuncName m))
            |> Result.bind (fun cfg ->
                requireRep "tensor_product input 1" cfg.Spec1 xE |> Result.bind (fun () ->
                requireRep "tensor_product input 2" cfg.Spec2 yE |> Result.bind (fun () ->
                requireInv "tensor_product weight buffer" wE |> Result.map (fun () ->
                    Rep cfg.SpecOut))))
        | "linear", [ sInE; sOutE; wE; xE ] ->
            specArg "linear specIn" sInE |> Result.bind (fun si ->
            specArg "linear specOut" sOutE |> Result.bind (fun so ->
                requireInv "linear weight buffer" wE |> Result.bind (fun () ->
                requireRep "linear input" si xE |> Result.map (fun () -> Rep so))))
        | "gated", [ specE; xE ] ->
            specArg "gated spec" specE |> Result.bind (fun spec ->
                if spec.IsEmpty || spec.Head.L <> 0 then
                    reject "gated: the first block must be scalars (L=0) — the gates are read from it"
                elif ctx.Group = O3 && spec.Head.Parity <> 0 then
                    reject "gated under equiv(O3): the gate block must be (l=0, even) — pseudoscalar gates flip under improper rotations, breaking O(3) equivariance (SO3 admits them)"
                else
                    requireRep "gated input" spec xE |> Result.map (fun () -> Rep spec))
        | "scalars", [ specE; xE ] ->
            specArg "scalars spec" specE |> Result.bind (fun spec ->
                if ctx.Group = O3 && spec |> List.exists (fun en -> en.L = 0 && en.Parity = 1) then
                    reject "scalars under equiv(O3): the spec has (l=0, odd) blocks — pseudoscalars flip under improper rotations and are not O(3) invariants (SO3 admits them)"
                else
                    requireRep "scalars input" spec xE |> Result.map (fun () -> Inv))
        | "norms", [ specE; xE ] ->
            specArg "norms spec" specE |> Result.bind (fun spec ->
                requireRep "norms input" spec xE |> Result.map (fun () -> Inv))
        | "derive_linear", [ sInE; sOutE; wE; xE ] ->
            specArg "derive_linear specIn" sInE |> Result.bind (fun si ->
            specArg "derive_linear specOut" sOutE |> Result.bind (fun so ->
                requireInv "derive_linear weight buffer" wE |> Result.bind (fun () ->
                requireRep "derive_linear input" si xE |> Result.map (fun () -> Rep so))))
        | "derive_tp", [ s1E; s2E; xE; yE; wE ] ->
            specArg "derive_tp spec1" s1E |> Result.bind (fun s1 ->
            specArg "derive_tp spec2" s2E |> Result.bind (fun s2 ->
                requireRep "derive_tp input 1" s1 xE |> Result.bind (fun () ->
                requireRep "derive_tp input 2" s2 yE |> Result.bind (fun () ->
                requireInv "derive_tp weight buffer" wE |> Result.map (fun () ->
                    Rep (tpSpec s1 s2))))))
        | ("derive_linear" | "derive_tp"), _ ->
            reject (sprintf "%s: inside an equiv-certified body use the full call form — the 2-argument binding form is for uncertified assembly code" op)
        | ("linear_rows" | "gated_rows"), _ ->
            reject (sprintf "%s is not admitted in equiv-certified bodies (row-stacked buffers are not representation spaces); apply the single-vector op per row" op)
        | ("tensor_product" | "linear" | "gated" | "scalars" | "norms" | "y_to"), _ ->
            reject (sprintf "%s: unrecognized call shape inside an equiv-certified body" op)
        | _ ->
            // other alias members (sizing etc. — normalized already, so an
            // unnormalized leftover is unknown): invariant if args are.
            judgeAll args |> Result.bind (fun sts ->
                if sts |> List.forall ((=) Inv) then Ok Inv
                else reject (sprintf "ml.%s is not an equivariance-preserving operation on representation-typed values" op))
    // --- named callees ----------------------------------------------------
    | ExprKind.ExprVar fn ->
        match Map.tryFind fn ctx.Certs with
        | Some cert ->
            if cert.Group <> ctx.Group then
                reject (sprintf "call to '%s': it is certified for %s, this function for %s — certificates do not transfer between groups" fn (groupStr cert.Group) (groupStr ctx.Group))
            elif List.length args <> List.length cert.Params then
                reject (sprintf "call to '%s': expected %d arguments" fn (List.length cert.Params))
            else
                (List.zip cert.Params args)
                |> List.fold (fun acc ((pName, pSt), argE) ->
                    acc |> Result.bind (fun () ->
                        match pSt with
                        | Rep sp -> requireRep (sprintf "'%s' parameter '%s'" fn pName) sp argE
                        | Inv -> requireInv (sprintf "'%s' parameter '%s'" fn pName) argE
                        | Opaque -> reject (sprintf "call to '%s': parameter '%s' is unclassifiable" fn pName)))
                    (Ok ())
                |> Result.map (fun () -> cert.Return)
        | None ->
            match Map.tryFind fn env with
            | Some (Rep spec) ->
                // indexing into a rep: admissible only at a static offset
                // inside an invariant (l=0) block.
                (match args with
                 | [ iE ] ->
                     (match evalExpr ctx.Statics fuel iE with
                      | Ok (SVInt i) when Set.contains (int i) (invariantOffsets ctx.Group spec) -> Ok Inv
                      | Ok (SVInt _) ->
                          Error (bl4008 e.Span (sprintf "function '%s': raw indexing into an l>0 (or, under O3, parity-odd) component of '%s' reads a basis-dependent number — extract invariants with ml.scalars/ml.norms or contract with ml.tensor_product" ctx.FuncName fn))
                      | _ ->
                          Error (bl4008 e.Span (sprintf "function '%s': indexing into representation-typed '%s' requires a static offset inside an invariant (l=0) block" ctx.FuncName fn)))
                 | _ -> reject (sprintf "unsupported access into representation-typed '%s'" fn))
            | _ ->
                // uncertified callee (builtin, helper, plain array, lambda):
                // a function of invariants is invariant — every argument
                // must be Inv, and reps must not escape.
                judgeAll args |> Result.bind (fun sts ->
                    match sts |> List.tryFindIndex (fun s -> s <> Inv) with
                    | None -> Ok Inv
                    | Some i ->
                        let argE = args.[i]
                        if isKnownScalarBuiltin fn then
                            Error (bl4008 argE.Span (sprintf "function '%s': applying '%s' to a representation-typed value is not equivariant — nonlinearities act only on invariants (ml.gated gates reps; ml.scalars/ml.norms extract invariants)" ctx.FuncName fn))
                        else
                            Error (bl4008 argE.Span (sprintf "function '%s': representation-typed value escapes to '%s', which carries no equiv certificate — certify it with `where ml.equiv(%s)` or pass only invariants" ctx.FuncName fn (groupStr ctx.Group))))
    | _ ->
        // computed callee over reps: not admissible in v1
        judgeAll args |> Result.bind (fun sts ->
            judge ctx env f |> Result.bind (fun sf ->
                if sf = Inv && sts |> List.forall ((=) Inv) then Ok Inv
                else reject "cannot classify this call inside an equiv-certified body"))

/// Judge one certified function. Empty list = certificate holds.
let judgeFunction (group: Group) (certs: Map<string, CertSig>) (statics: StaticEnv)
                  (aliases: Set<string>) (fd: FunctionDecl)
    : Blade.Diagnostics.Diagnostic list =
    match Map.tryFind fd.Name certs with
    | None -> []
    | Some cert ->
        let ctx = { Group = group; FuncName = fd.Name; Aliases = aliases; Statics = statics; Certs = certs }
        let env = cert.Params |> List.fold (fun m (n, st) -> Map.add n st m) Map.empty
        match judge ctx env fd.Body with
        | Error d -> [ d ]
        | Ok st ->
            if st = cert.Return then []
            else
                [ bl4008 fd.Body.Span
                      (sprintf "function '%s': the body is %s but the declared return type is %s — the certificate requires them to agree" fd.Name (statusStr st) (statusStr cert.Return)) ]

// ============================================================================
// Constraint-registry handler
// ============================================================================

/// `equiv(G)` is a callee-side theorem: Validate re-checks the conjunct
/// shape (the elaborator has already judged the body by the time
/// checkFunctionDecl runs), the license scope is unused, and call sites
/// carry no obligation.
let private equivHandler : Blade.Constraints.ConstraintHandler = {
    Describe = "equiv(G) — certifies the function equivariant under G (O3 or SO3); the ML elaborator proves the body composes only equivariance-preserving operations"
    Validate = fun funcName _ args ->
        parseGroup funcName args |> Result.map ignore
    EnterBody = fun _ _ -> ()
    ExitBody = fun _ _ -> ()
    Discharge = fun _ _ _ -> Ok ()
}

let mutable private registered = false

let register () =
    if not registered then
        registered <- true
        Blade.Constraints.registerConstraint "__ml_equiv" equivHandler
