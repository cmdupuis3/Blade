/// The abstract-interpretation WALKER SHELL shared by the three certification
/// disciplines — MLEquiv (`ml.equiv(G)`, O(3)/SO(3)), MLGalilean
/// (`ml.galilean(u, ...)`, constant boosts) and MLPerm (`ml.perm_equiv(N)`,
/// Sₙ node relabelling). Extracted at the third copy, not the second
/// (retired transforms-as-types plan §3.6's post-5a cleanup, §7 stage 5c).
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
///
/// THE EXTRACTION WAS BIT-NEUTRAL; THE FIX PASS AFTER IT IS NOT. Stage 5c
/// moved three byte-identical copies here and changed nothing. Having them in
/// one place is what made the three-way diff's catalog actionable, and
/// `freeVars` below now carries two of its findings (the missing former arms,
/// the one-level-deep `for` walk). Both were UNDER-approximations, and this
/// walk backs guards that reject on what they SEE, so both were false
/// accepts in every discipline that scans with it — MLEquiv's and
/// MLGalilean's lambda-capture rule as much as MLPerm's former scope. The
/// direction of the change is therefore the same for all three (strictly more
/// names visible, hence strictly more rejects), which is why one fix in the
/// shared walk is the right shape rather than three per-discipline patches.
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
///
/// EXHAUSTIVE BY CONSTRUCTION, and that is the whole point. This walk backs
/// SOUNDNESS GUARDS: a name it fails to report is a value the guards cannot
/// see, so a missing arm is a false ACCEPT rather than a missed diagnostic.
/// That is exactly what the stage-5c three-way diff caught (catalog finding
/// 1): `ExprMethodFor` / `ExprFor` / `ExprCompute` / `ExprReduce` fell into a
/// `| _ -> Set.empty` catch-all, so an array named ONLY in a former's source
/// list — `method_for(x) <@> lambda ...` — was invisible, and MLPerm's
/// former-scope scan cleared a node-covariant `x` as invariant.
///
/// The catch-all is therefore GONE. Every `ExprKind` case is listed, so a new
/// AST node is a COMPILE ERROR here instead of a silent hole, and the nodes
/// that genuinely read nothing (leaves, and the type-level-only virtual-array
/// forms whose extents resolve in the static environment) say so explicitly —
/// "nothing to read" is a decision on the record, not a default.
let rec freeVars (bound: Set<string>) (e: Expr) : Set<string> =
    let fv = freeVars bound
    let fvs (es: Expr list) = es |> List.map fv |> Set.unionMany
    let fvOpt (o: Expr option) = match o with Some x -> fv x | None -> Set.empty
    match e.Kind with
    // --- the binding forms: each extends `bound` over its own body ----------
    | ExprKind.ExprVar n -> if Set.contains n bound then Set.empty else Set.singleton n
    | ExprKind.ExprLet (b, body) ->
        Set.union (fv b.Value) (freeVars (Set.union bound (Set.ofList (patternVars b.Pattern))) body)
    | ExprKind.ExprLambda (ps, _, body) ->
        freeVars (Set.union bound (Set.ofList (ps |> List.map (fun p -> p.Name)))) body
    | ExprKind.ExprMatch (s, cases) ->
        // A case PATTERN binds over its arm — the same names `judge` binds
        // with `bindPatternVars`. (The pre-5c copies walked `c.Body` at the
        // outer scope, which reported a shadowing arm variable as free.)
        Set.unionMany
            (fv s :: (cases |> List.map (fun c ->
                freeVars (Set.union bound (Set.ofList (patternVars c.Pattern))) c.Body)))
    | ExprKind.ExprBlock (stmts, fin) ->
        let acc, scope = freeVarsStmts bound stmts
        Set.union acc (match fin with Some fe -> freeVars scope fe | None -> Set.empty)
    | ExprKind.ExprRecArray d ->
        // `let rec q = match q with | zero :: n -> zero :: SEED | p :: n -> ...`:
        // the recursive family, the prefix and the step ordinal are all bound
        // inside the arms that read them.
        let seed =
            match d.SeedArm with
            | Some (stepVar, se) -> freeVars (Set.union bound (Set.ofList [ d.Name; stepVar ])) se
            | None -> Set.empty
        Set.union seed
            (freeVars (Set.union bound (Set.ofList [ d.Name; d.PrefixVar; d.StepVar ])) d.SliceExpr)
    // --- the co-iteration formers: THE SOURCES ARE READ ---------------------
    // The arms whose absence was catalog finding 1. A former hands its kernel
    // the ELEMENTS of these expressions, so every name they mention is live
    // in the application's scope even though the kernel never spells it.
    | ExprKind.ExprMethodFor arrays -> fvs arrays
    | ExprKind.ExprObjectFor kernel -> fv kernel
    | ExprKind.ExprFor (src, _, kernel) ->
        // where-clause conjuncts carry Idents and TypeExprs, never values.
        let srcVars =
            match src with
            | ForArrays (arrays, inClause) -> Set.union (fvs arrays) (fvOpt inClause)
            | ForKernel k -> fv k
        Set.union srcVars (fvOpt kernel)
    // --- leaves: nothing to read --------------------------------------------
    // `ExprArity` names a Poly parameter but queries its ARITY, a static
    // property — no cell of it is read.
    | ExprKind.ExprLit _ | ExprKind.ExprWildcard | ExprKind.ExprQualified _
    | ExprKind.ExprNth | ExprKind.ExprZero | ExprKind.ExprSection _
    | ExprKind.ExprArity _ -> Set.empty
    // --- type-level only: `range<I>` / `reverse<I>` enumerate an INDEX type,
    //     whose extents resolve against the static environment rather than
    //     against any value binding.
    | ExprKind.ExprRange _ | ExprKind.ExprReverse _ -> Set.empty
    // --- exactly one subexpression ------------------------------------------
    | ExprKind.ExprUnaryOp (_, i) | ExprKind.ExprTyped (i, _) | ExprKind.ExprField (i, _)
    | ExprKind.ExprPure i | ExprKind.ExprCompute i | ExprKind.ExprRead i
    | ExprKind.ExprRank i | ExprKind.ExprUnique i | ExprKind.ExprExtents i
    | ExprKind.ExprDecompact (i, _) | ExprKind.ExprTranspose (i, _, _)
    | ExprKind.ExprReynolds (i, _) | ExprKind.ExprStatic i
    | ExprKind.ExprPartialApp (_, i, _) | ExprKind.ExprBlocked (_, i)
    | ExprKind.ExprHalo (_, i) -> fv i
    // --- exactly two -------------------------------------------------------
    | ExprKind.ExprBinOp (_, _, l, r) | ExprKind.ExprDotDot (l, r)
    | ExprKind.ExprTupleIndex (l, r) | ExprKind.ExprGuard (l, r)
    | ExprKind.ExprReplicate (l, r) | ExprKind.ExprMask (l, r)
    | ExprKind.ExprCompound (l, r) | ExprKind.ExprSparse (l, r) | ExprKind.ExprIntersect (l, r)
    | ExprKind.ExprUnion (l, r) | ExprKind.ExprContains (l, r)
    | ExprKind.ExprGroupBy (l, r) | ExprKind.ExprSort (l, r)
    | ExprKind.ExprGram (l, r) | ExprKind.ExprAssign (l, r) -> Set.union (fv l) (fv r)
    // --- lists --------------------------------------------------------------
    | ExprKind.ExprTuple es | ExprKind.ExprArrayLit es | ExprKind.ExprZip es
    | ExprKind.ExprStack es | ExprKind.ExprSequence es | ExprKind.ExprGroupKeys es
    | ExprKind.ExprJoin (es, _) -> fvs es
    // --- the rest -----------------------------------------------------------
    | ExprKind.ExprApp (f, args) -> Set.union (fv f) (fvs args)
    | ExprKind.ExprIf (c, t, f) -> Set.unionMany [ fv c; fv t; fv f ]
    | ExprKind.ExprReduce (src, kern, init) -> Set.unionMany [ fv src; fv kern; fvOpt init ]
    | ExprKind.ExprAlign (es, spec) ->
        let pad =
            match spec with
            | Some sp -> (match sp.Boundary with BndPad p -> fv p | _ -> Set.empty)
            | None -> Set.empty
        Set.union (fvs es) pad
    | ExprKind.ExprStruct (_, fields, spread) ->
        Set.union (fields |> List.map (snd >> fv) |> Set.unionMany) (fvOpt spread)

/// Free variables of a STATEMENT LIST, threading the scope each `let` opens
/// along the sequence. Returns the names read freely, together with the scope
/// in force after the last statement — a block's final expression is judged
/// under the latter.
///
/// NESTED `for` STATEMENTS RECURSE (catalog finding 3). The copy this
/// replaces inlined ONE level of loop body and silently dropped whatever a
/// loop inside a loop read — the same false-accept shape as a missing
/// expression arm, in the same function. The surface language no longer has
/// the imperative `for` (the parser refuses it with BL1003), so no source
/// program reaches this today; the arm stays for synthesized bodies (Grad.fs,
/// MathDecls.fs) and so the walk is right by construction rather than by
/// reachability argument.
and freeVarsStmts (bound: Set<string>) (stmts: Stmt list) : Set<string> * Set<string> =
    stmts
    |> List.fold (fun (acc, scope) s ->
        match unwrapStmt s with
        | StmtLet binding ->
            (Set.union acc (freeVars scope binding.Value),
             Set.union scope (Set.ofList (patternVars binding.Pattern)))
        | StmtExpr e -> (Set.union acc (freeVars scope e), scope)
        | StmtAssign (l, _, r) ->
            (Set.unionMany [ acc; freeVars scope l; freeVars scope r ], scope)
        | StmtForIn (v, range, body) ->
            // the loop variable, and anything the body binds, are scoped to
            // the body: neither escapes into the rest of the sequence.
            let inner, _ = freeVarsStmts (Set.add v scope) body
            (Set.unionMany [ acc; freeVars scope range; inner ], scope)
        // `unwrapStmt` has already peeled the span wrapper.
        | StmtSpanned _ -> (acc, scope))
        (Set.empty, bound)

/// The normalized where-conjuncts of one name carried by a function
/// declaration (`__ml_equiv` / `__ml_galilean` / `__ml_perm_equiv`) — the
/// pre-scan every `buildCertTable` opens with. Zero, one, or (the duplicate
/// case each discipline rejects with its own message) more.
let conjunctsOf (name: string) (fd: FunctionDecl) : (Ident * Ident list) list =
    fd.WhereClause
    |> Option.map (fun w -> w.Custom)
    |> Option.defaultValue []
    |> List.filter (fun (n, _) -> n = name)
