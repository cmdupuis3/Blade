/// The abstract-interpretation WALKER SHELL shared by the three certification
/// disciplines — MLEquiv (`ml.equiv(G)`, O(3)/SO(3)), MLGalilean
/// (`ml.galilean(u, ...)`, constant boosts) and MLPerm (`ml.perm_equiv(N)`,
/// Sₙ node relabelling). Extracted at the third copy, not the second
/// (plan-transforms-as-types §3.6's post-5a cleanup, §7 stage 5c).
///
/// WHAT LIVES HERE is exactly what the three copies had VERBATIM in common:
/// the SYNTACTIC walk that no discipline gets to disagree about — which names
/// a pattern binds, which names an expression reads freely, how a list of
/// subexpressions is judged left to right with the first error winning, and
/// how a normalized where-conjunct is read off a function declaration. These
/// are properties of the AST, not of any group action, so a divergence
/// between two of them would be a bug in one of them.
///
/// WHAT DOES NOT LIVE HERE is every RULE: the status lattices, the signature
/// classifiers, the judgment arms, the op tables, the diagnostics. The three
/// lattices have OPPOSITE POLARITY at several arms (MLPerm's header tabulates
/// it), so `judge` / `judgeStmts` / `judgeAssign` stay per-discipline. Their
/// SHAPES do agree, but parameterizing `judgeStmts` would take a judge
/// callback, an assign callback, the invariant status value, a variance
/// predicate and two diagnostic constructors — six moving parts to share
/// twenty-odd lines, which is a worse trade than the copy. Shell = the walk;
/// module = the rules.
module Blade.ML.CertShell

open Blade.Ast

/// Judge a list of subexpressions left to right, collecting the statuses in
/// order. The FIRST error wins and nothing after it is judged: `Result.bind`
/// never runs its body once the accumulator is an `Error`. This is the
/// `judgeAll` local each walker defined identically inside `judgeApp` (and
/// again inline for aggregate literals and former sources).
let judgeEach (j: 'a -> Result<'b, 'e>) (xs: 'a list) : Result<'b list, 'e> =
    xs
    |> List.fold (fun acc a ->
        acc |> Result.bind (fun sts -> j a |> Result.map (fun s -> sts @ [ s ])))
        (Ok [])

/// All pattern variables of a pattern. Each discipline binds them at its own
/// invariant status when it destructures (see `bindPatternVars`).
let rec patternVars (p: Pattern) : string list =
    match p.Kind with
    | PatternKind.PatVar n -> [ n ]
    | PatternKind.PatTuple ps -> ps |> List.collect patternVars
    | _ -> []

/// Bind every variable of a pattern at one status — each discipline passes
/// its own invariant (`Inv` / `BInv` / `Pow 0`).
let bindPatternVars (st: 'st) (env: Map<string, 'st>) (p: Pattern) : Map<string, 'st> =
    List.fold (fun m v -> Map.add v st m) env (patternVars p)

/// Free variables of an expression that are NOT locally bound (the lambda
/// capture rule of every discipline, and MLPerm's former-scope scan).
let rec freeVars (bound: Set<string>) (e: Expr) : Set<string> =
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

/// The normalized where-conjuncts of one name carried by a function
/// declaration (`__ml_equiv` / `__ml_galilean` / `__ml_perm_equiv`) — the
/// pre-scan every `buildCertTable` opens with. Zero, one, or (the duplicate
/// case each discipline rejects with its own message) more.
let conjunctsOf (name: string) (fd: FunctionDecl) : (Ident * Ident list) list =
    fd.WhereClause
    |> Option.map (fun w -> w.Custom)
    |> Option.defaultValue []
    |> List.filter (fun (n, _) -> n = name)
