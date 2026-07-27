/// The `where ml.perm_equiv(N)` discipline: a function carrying the
/// (normalized) `__ml_perm_equiv` conjunct is PROVED equivariant under the
/// symmetric group Sₙ acting by RELABELLING THE NODE AXIS of extent N — its
/// body may compose only relabelling-equivariant operations. The judgment is
/// an abstract interpretation over the surface AST, run by MLElaborate at the
/// same pass-1/pass-2 seam as the equiv and galilean judgments, where `ml.*`
/// op calls are still surface-visible and static extents resolve through the
/// SAME machinery elaboration uses (plan-transforms-as-types §3.6, §7 stage
/// 5a-iii).
///
/// THIS WAS THE THIRD COPY of the abstract-interpretation WALKER SHELL
/// (freeVars / patternVars / the let-block-if-match-assign-for folds /
/// judgeApp's callee dispatch), after MLEquiv.fs and MLGalilean.fs — written
/// out rather than guessed at so that §7's stage 5c could extract the shell
/// against THREE witnesses. That extraction has happened: the SYNTACTIC walk
/// (freeVars, patternVars, the left-to-right judge fold, the where-conjunct
/// pre-scan) now lives in MLCertShell.fs. What stayed here is what the three
/// disciplines DISAGREE about — the lattice, the signature classifier, the
/// judgment arms, the op table — including `judgeStmts` / `judgeAssign`,
/// whose shapes agree but whose every message and guard does not.
///
/// ---------------------------------------------------------------------------
/// THE LATTICE — ℕ-graded, and of OPPOSITE POLARITY to MLEquiv's at almost
/// every arm
/// ---------------------------------------------------------------------------
///   Pow k    — a flat N^k buffer that transforms as σ^{⊗k}: relabelling the
///              nodes by σ permutes its cells by σ acting on each of the k
///              axes. Pow 0 is the INVARIANT status (N^0 = 1 cell, or a
///              scalar, or anything the action fixes).
///   POpaque  — unclassifiable; rejected wherever it meets a status-relevant
///              position (op argument, return value).
///
/// The polarity table, which is the whole argument for a SIBLING lattice
/// rather than a `Rep of GroupSpec` payload on MLEquiv's:
///
///   pointwise nonlinearity on a rep    O(3): BL4008     Sₙ: LEGAL (Pow k)
///   elementwise product of two reps    O(3): BL4008     Sₙ: LEGAL (Pow k)
///   sum of two like reps               O(3): legal      Sₙ: legal
///   raw component read                 O(3): BL4008     Sₙ: legal in the
///                                                       MATH, deferred here
///                                                       (see below)
///
/// The first two lines are the headline: PERMUTATIONS COMMUTE WITH EVERY
/// POINTWISE MAP, because a permutation moves cells around without mixing
/// them, so anything applied cell-by-cell commutes with it. The Wigner action
/// of O(3) mixes cells within a block, so the same two lines are exactly what
/// MLEquiv forbids. One judgment cannot wear both.
///
/// ---------------------------------------------------------------------------
/// v1 KEYS ON FLAT BUFFERS (a deliberate delta from §3.6's "rank-k" prose)
/// ---------------------------------------------------------------------------
/// §3.6 describes Pow k as "rank-k, all axes node-covariant". The ops that
/// landed at 5a-ii consume FLAT ROW-MAJOR N^k buffers (the `_rows` house
/// precedent — `derive_perm_linear`'s x is one `Idx<N^K>` axis, its result one
/// `Idx<N^L>` axis), so the classifier keys on the FLAT EXTENT instead:
///
///     a signature parameter `Array<Float like Idx<M>>` is Pow k
///     iff M = N^k for some 0 ≤ k ≤ MLPermSpec.maxPositions
///
/// k is unique because N ≥ 2 makes the powers strictly increasing — which is
/// why N < 2 is REFUSED at the conjunct (at N = 1 every extent is 1 = N^k for
/// every k, so no rank is determined; the group is trivial and the certificate
/// would be vacuous anyway). M = 1 is Pow 0: N^0 = 1, the one-cell invariant
/// readout `derive_perm_linear(K, 0, N, ·, ·)` returns. Scalars are Pow 0.
///
/// RANK ≥ 2 ARRAYS AND NON-`Idx` INDEX TYPES ARE A HARD REJECT in a certified
/// signature (the MLEquiv.fs multi-index precedent) — v1 has one status per
/// VALUE, and a `batch × node` or `node × channel` array needs one per AXIS.
/// Per-axis status vectors are the named v2 shape, and the same upgrade is
/// what unlocks O(3)×Sₙ dual certificates (§3.6's two cross-referencing
/// deferrals).
///
/// THE EXTENT-KEYING CAVEAT. An `Idx<M>` whose M is NOT a power of N is
/// classified Pow 0 — invariant. Conversely an extent that is COINCIDENTALLY
/// N^k (a weight buffer of Bell(2) = 2 slots read at N = 2) classifies
/// COVARIANT, which is the conditional-theorem reading exactly as §3.6 states
/// it: the certificate says "IF this parameter transforms as σ^{⊗k} THEN the
/// result transforms as declared", so a coincidence makes the hypothesis false
/// for that caller rather than making the theorem wrong. Nominal keying
/// (`Nat<Node>`) is the named upgrade.
///
/// ---------------------------------------------------------------------------
/// COMPONENT ACCESS — the one place v1 is STRICTER than the mathematics
/// ---------------------------------------------------------------------------
/// §3.6 records that component access by a bound index is LEGAL for Sₙ (the
/// node basis is real, unlike the irreps basis). v1 has no loop-variable
/// tracking, so it cannot tell `x(i)` inside a `for i in 0..N` (which
/// reassembles equivariantly) from `x(0)` (which does not). It therefore
/// REJECTS every read out of a Pow k, k ≥ 1, with a message naming per-axis
/// tracking as the v2 lift. Reads out of a Pow 0 are fine — a value the action
/// fixes has components the action fixes. Nothing is lost in practice at 5a:
/// the WHOLE-ARRAY elementwise operators cover pointwise work, and
/// derive_perm_linear / derive_perm_bias / perm_matmul cover the rest.
///
/// ---------------------------------------------------------------------------
/// THE RULES
/// ---------------------------------------------------------------------------
///   +, -, *, / of two Pow k (same k)          -> Pow k   (pointwise)
///   Pow k with a Pow 0 (scalar broadcast)     -> Pow k   (the only Sₙ-fixed
///                                                        vectors ARE the
///                                                        constants, so a
///                                                        broadcast add is
///                                                        equivariant)
///   unary minus / a pointwise scalar builtin  -> status preserved
///   ml.derive_perm_linear(K, L, N, x, w)      -> Pow L   (x : Pow K, w : Pow 0)
///   ml.derive_perm_bias(L, N, b)              -> Pow L   (b : Pow 0)
///   ml.perm_matmul(N, a, b)                   -> Pow 2   (a, b : Pow 2)
///   every other ml.* op                       : all-Pow-0 args -> Pow 0
///   a certified callee                        : signature match, N must agree
///   an uncertified callee                     : all-Pow-0 args -> Pow 0;
///                                               a Pow k (k ≥ 1) escaping is a
///                                               reject (the MLEquiv precedent)
/// Violations are BL4012 at the offending expression's span.
module Blade.ML.Perm

open Blade.Ast
open Blade.StaticEval
// The walker shell (stage 5c): freeVars / patternVars / bindPatternVars /
// judgeEach / conjunctsOf — the syntactic walk shared verbatim with MLEquiv
// and MLGalilean. Every RULE below is this discipline's own.
open Blade.ML.CertShell

type PowStatus =
    /// Pow k = a flat N^k buffer transforming as σ^{⊗k}. Pow 0 = invariant.
    | Pow of int
    | POpaque

type PermSig = {
    /// The node-axis extent this certificate is about. Certificates do not
    /// transfer between extents (the MLEquiv group-mismatch precedent).
    N: int
    /// Parameter name -> status, in declaration order.
    Params: (string * PowStatus) list
    Return: PowStatus
}

// ============================================================================
// Helpers
// ============================================================================

let private fuel = 100_000

let private bl4012 (span: Span) (msg: string) : Blade.Diagnostics.Diagnostic =
    Blade.Diagnostics.mkError "BL4012" (Blade.Diagnostics.Codes.phaseOfCode "BL4012") span msg

let private statusStr (st: PowStatus) : string =
    match st with
    | Pow 0 -> "invariant (Pow 0 — fixed by every node relabelling)"
    | Pow k -> sprintf "node-covariant of rank %d (Pow %d — a flat N^%d buffer transforming as sigma^(x%d))" k k k k
    | POpaque -> "unclassifiable"

/// The rank cap: the same K + L bound the ops carry, so a classified rank is
/// always a rank some op could actually consume.
let private maxPow = Blade.ML.PermSpec.maxPositions

/// k with N^k = m, or None. N >= 2 (enforced at the conjunct) makes the powers
/// strictly increasing, so at most one k can match — that uniqueness is the
/// whole reason for the N < 2 refusal.
let private powClass (n: int64) (m: int64) : int option =
    let rec go (k: int) (acc: int64) =
        if acc = m then Some k
        elif k >= maxPow || acc > m then None
        else go (k + 1) (acc * n)
    if m < 1L then None else go 0 1L

/// Mirror of MLElaborate.staticArg / MLEquiv.staticArgValue (keep in sync): an
/// ML op's static argument is a `let static` binding name or an int literal.
let private staticArgValue (statics: StaticEnv) (e: Expr) : Result<StaticValue, string> =
    match e.Kind with
    | ExprKind.ExprLit (LitInt n) -> Ok (SVInt n)
    | ExprKind.ExprVar name ->
        match Map.tryFind name statics.Values with
        | Some sv -> Ok sv
        | None -> Error (sprintf "'%s' is not a `let static` binding" name)
    | _ -> Error "expected a `let static` binding name or literal"

// ============================================================================
// Certified-signature table
// ============================================================================

/// Type aliases of this module (one-level chase, mirroring MLEquiv).
let private aliasMapOf (decls: Located<Decl> list) : Map<string, TypeExpr> =
    decls
    |> List.fold (fun m d ->
        match d.Value with
        | DeclType (TyDeclAlias (n, [], body)) -> Map.add n body m
        | _ -> m) Map.empty

/// The v2 pointer, attached to every signature-shape refusal.
let private v2Note =
    "v1 keys node-covariance on the FLAT extent of a SINGLE `Idx<>` axis (the Sn ops consume flat row-major N^k buffers — ml.derive_perm_linear's x is one Idx<N^K> axis), so a certified signature carries one status per VALUE. A multi-axis array needs one status per AXIS: per-axis status vectors are the named v2 shape, and the same upgrade is what unlocks O(3) x Sn dual certificates (plan-transforms-as-types §3.6, the two cross-referencing deferrals). Flatten the buffer, or leave the function uncertified"

/// Alias-chase budget: `type A = B` chains are followed, but never in a cycle.
let private aliasDepth = 8

/// Classify ONE index type of a certified signature's array parameter.
let rec private statusOfIndex (depth: int) (n: int) (aliases: Map<string, TypeExpr>) (statics: StaticEnv) (ix: TypeExpr)
    : Result<PowStatus, string> =
    match ix with
    | TyIdx extentE ->
        evalExpr statics fuel extentE
        |> Result.mapError (fun m -> sprintf "the Idx<> extent does not resolve statically (%s) — a perm-certified signature is classified by extent" m)
        |> Result.bind (fun sv ->
            match sv with
            | SVInt m ->
                // THE EXTENT-KEYING CAVEAT (§3.6): a non-power extent is
                // invariant, and a COINCIDENTAL N^k extent classifies
                // covariant — the conditional-theorem reading, not a bug.
                match powClass (int64 n) m with
                | Some k -> Ok (Pow k)
                | None -> Ok (Pow 0)
            | _ -> Error "the Idx<> extent must be a static int")
    | TyNamed (nm, []) when depth > 0 && (Map.containsKey nm aliases) ->
        statusOfIndex (depth - 1) n aliases statics (Map.find nm aliases)
    | TyNamed (nm, _) ->
        Error (sprintf "'%s' is not an `Idx<>` index type. %s" nm v2Note)
    | _ ->
        Error (sprintf "only plain `Idx<>` axes are classified in a perm-certified signature. %s" v2Note)

/// Classify a signature annotation. Certified functions must be fully
/// annotated; the rank-1 `Array<_ like Idx<M>>` shape is the only array form.
and private statusOfType (depth: int) (n: int) (aliases: Map<string, TypeExpr>) (statics: StaticEnv) (t: TypeExpr)
    : Result<PowStatus, string> =
    match t with
    | TyArray (_, idxs) ->
        match idxs with
        | [] -> Ok (Pow 0)
        | [ ix ] -> statusOfIndex depth n aliases statics ix
        | _ ->
            Error (sprintf "a rank-%d array cannot be classified in a perm-certified signature. %s" idxs.Length v2Note)
    | TyNamed (nm, []) when depth > 0 && (Map.containsKey nm aliases) ->
        // `type X = Idx<N>` / `type Y = Float` used bare in a parameter slot.
        statusOfType (depth - 1) n aliases statics (Map.find nm aliases)
    | TyNamed (_, _) -> Ok (Pow 0) // scalar primitives and non-index named types
    | TyIdx _ -> statusOfIndex depth n aliases statics t
    | TyInt32 | TyInt64 | TyFloat32 | TyFloat64 | TyBool | TyComplex128 -> Ok (Pow 0)
    | _ ->
        Error (sprintf "cannot classify this annotation in a perm-certified signature (supported: scalars and `Array<_ like Idx<M>>`). %s" v2Note)

/// The conjunct's single argument: an int literal (`ml.perm_equiv(4)`) or the
/// name of a `let static` binding (`ml.perm_equiv(NODES)`). N ≥ 2 is REQUIRED —
/// see powClass.
let private resolveN (statics: StaticEnv) (funcName: string) (args: string list) : Result<int, string> =
    match args with
    | [ a ] ->
        let raw =
            match System.Int32.TryParse a with
            | true, n -> Ok n
            | _ ->
                match Map.tryFind a statics.Values with
                | Some (SVInt n) -> Ok (int n)
                | Some _ -> Error (sprintf "function '%s': perm_equiv(%s) — '%s' is a `let static` binding but not an int; N is the node-axis extent" funcName a a)
                | None -> Error (sprintf "function '%s': perm_equiv(%s) — N must be an int literal or the name of a `let static` int binding (the node-axis extent)" funcName a)
        raw |> Result.bind (fun n ->
            if n < 2 then
                Error (sprintf "function '%s': perm_equiv(%d) — N must be >= 2. v1 classifies node-covariance by the FLAT extent of a parameter (Idx<M> is Pow k iff M = N^k), and only N >= 2 makes the powers N^0 < N^1 < ... strictly increasing, hence the rank unique; at N = 1 every extent is 1 = N^k for every k. S_1 is the trivial group, so the certificate would be vacuous in any case" funcName n)
            else Ok n)
    | _ ->
        Error (sprintf "function '%s': perm_equiv expects exactly one argument — the node-axis extent N, as in `where ml.perm_equiv(4)`" funcName)

/// Pre-scan: every DeclFunction carrying a normalized ("__ml_perm_equiv", [N])
/// conjunct gets a certified signature. Errors are BL4012 at the decl.
let buildCertTable (statics: StaticEnv) (decls: Located<Decl> list)
    : Result<Map<string, PermSig>, Blade.Diagnostics.Diagnostic> =
    let aliases = aliasMapOf decls
    decls
    |> List.fold (fun acc d ->
        acc |> Result.bind (fun table ->
            match d.Value with
            | DeclFunction fd ->
                let conjs = conjunctsOf "__ml_perm_equiv" fd
                let fail msg = Error (bl4012 d.Span msg)
                match conjs with
                | [] -> Ok table
                | _ :: _ :: _ ->
                    fail (sprintf "function '%s': duplicate perm_equiv constraints — declare exactly one node-axis extent" fd.Name)
                | [ (_, args) ] ->
                    match resolveN statics fd.Name args with
                    | Error m -> fail m
                    | Ok n ->
                        let paramSt =
                            fd.Params
                            |> List.fold (fun acc p ->
                                acc |> Result.bind (fun ps ->
                                    match p.Type with
                                    | None ->
                                        Error (sprintf "function '%s': a perm-certified function must annotate every parameter and its return type ('%s' is unannotated) — the certificate is read off the extents" fd.Name p.Name)
                                    | Some t ->
                                        statusOfType aliasDepth n aliases statics t
                                        |> Result.mapError (sprintf "function '%s', parameter '%s': %s" fd.Name p.Name)
                                        |> Result.map (fun st -> ps @ [ (p.Name, st) ])))
                                (Ok [])
                        match paramSt with
                        | Error m -> fail m
                        | Ok ps ->
                            match fd.ReturnType with
                            | None -> fail (sprintf "function '%s': a perm-certified function must annotate its return type" fd.Name)
                            | Some rt ->
                                match statusOfType aliasDepth n aliases statics rt
                                      |> Result.mapError (sprintf "function '%s', return type: %s" fd.Name) with
                                | Error m -> fail m
                                | Ok r -> Ok (Map.add fd.Name { N = n; Params = ps; Return = r } table)
            | _ -> Ok table))
        (Ok Map.empty)

// ============================================================================
// The judgment
// ============================================================================

type private Ctx = {
    FuncName: string
    /// The node-axis extent of THIS function's certificate.
    N: int
    /// ml-module aliases (the op rules).
    MlAliases: Set<string>
    Statics: StaticEnv
    Certs: Map<string, PermSig>
}

/// THE POLARITY DEMO, stated once where the rule lives: a permutation moves
/// cells without mixing them, so EVERY POINTWISE MAP COMMUTES WITH IT.
/// `f(sigma . x) = sigma . f(x)` holds for any f applied cell-by-cell — which
/// is precisely the arm MLEquiv rejects with BL4008 ("nonlinearities act only
/// on invariants"), because the Wigner action mixes cells inside a block.
///
/// Blade's scalar intrinsics are SCALAR-ONLY (`exp(A)` on a whole array is a
/// type error with its own steering, corpus intrinsics/006), so this arm can
/// only be reached by a body that is going to fail type-checking for an
/// unrelated reason. It is here anyway, and it is the RIGHT verdict: the
/// lattice must not be the thing that rejects a pointwise map, or the polarity
/// would silently be MLEquiv's. The WRITABLE form of the same axiom is the
/// whole-array elementwise operators below (`h * h`, `h / (1.0 + h * h)`),
/// which are the lines corpus ml-equiv/039 pins as accepted and which are
/// BL4008 verbatim under `ml.equiv(O3)`.
let private isPointwiseBuiltin (n: string) =
    List.contains n [ "exp"; "log"; "sqrt"; "sin"; "cos"; "tan"; "tanh"; "abs"; "floor"; "ceil"; "min"; "max"; "pow" ]

/// The result rank of a pointwise application: every non-invariant argument
/// must agree on its rank (a pointwise map of an N-vector and an N^2-matrix is
/// not one map, it is a broadcast across two different spaces).
let private combinePointwise (sts: PowStatus list) : Result<PowStatus, string> =
    if sts |> List.exists ((=) POpaque) then Ok POpaque
    else
        let ranks = sts |> List.choose (function Pow k when k > 0 -> Some k | _ -> None) |> List.distinct
        match ranks with
        | [] -> Ok (Pow 0)
        | [ k ] -> Ok (Pow k)
        | _ ->
            Error (sprintf "this pointwise operation mixes node-covariant values of different ranks (%s) — a pointwise map runs cell-by-cell over ONE space; contract the ranks first (ml.derive_perm_linear) or broadcast through an invariant"
                       (ranks |> List.map (sprintf "Pow %d") |> String.concat " and "))

let rec private judge (ctx: Ctx) (env: Map<string, PowStatus>) (e: Expr)
    : Result<PowStatus, Blade.Diagnostics.Diagnostic> =
    let reject msg = Error (bl4012 e.Span (sprintf "function '%s': %s" ctx.FuncName msg))
    let j = judge ctx env
    match e.Kind with
    | ExprKind.ExprLit _ -> Ok (Pow 0)
    | ExprKind.ExprArrayLit es | ExprKind.ExprTuple es ->
        // A literal aggregate is a CONSTANT. Constants are Sₙ-fixed — EXCEPT
        // that the only Sₙ-fixed vectors of a node-power space are the ones
        // with every cell equal, and v1 does not check cell equality. So a
        // literal whose extent lands in a node-power space is refused with a
        // pointer at the op whose whole job is equivariant constants.
        es
        |> judgeEach j
        |> Result.bind (fun sts ->
            match sts |> List.tryFind (fun s -> match s with Pow k -> k > 0 | _ -> false) with
            | Some st ->
                reject (sprintf "packing a %s into a literal aggregate loses its node-axis structure — the aggregate does not transform as a node power" (statusStr st))
            | None ->
                match powClass (int64 ctx.N) (int64 es.Length) with
                | Some k when k > 0 ->
                    reject (sprintf "a %d-cell literal aggregate lands in the node-power space N^%d, and an arbitrary constant there is NOT S_n-invariant (only the constants with every cell equal are). The complete space of equivariant constants is ml.derive_perm_bias(%d, %d, b)" es.Length k k ctx.N)
                | _ -> if sts |> List.exists ((=) POpaque) then Ok POpaque else Ok (Pow 0))
    | ExprKind.ExprVar n ->
        match Map.tryFind n env with
        | Some st -> Ok st
        | None -> Ok (Pow 0) // globals/constants/builtins: held fixed by the conditional-theorem reading
    | ExprKind.ExprDotDot _ -> Ok (Pow 0)
    | ExprKind.ExprTyped (inner, _) -> j inner
    | ExprKind.ExprUnaryOp (_, inner) ->
        // Pointwise, hence status-preserving — the polarity arm again.
        j inner
    // Former application must dispatch BEFORE the general binop arithmetic arm
    // (OpApply is a BinOp constructor).
    | ExprKind.ExprBinOp (_, OpApply, _, _) ->
        // The co-iteration formers hand a kernel the ELEMENTS of their sources,
        // and an element of a Pow k is a COMPONENT — exactly the read v1 defers
        // (see the header). So a former application is admissible only when
        // nothing node-covariant is in scope of it.
        let covariant =
            freeVars Set.empty e |> Set.toList |> List.tryFind (fun n ->
                match Map.tryFind n env with Some (Pow k) -> k > 0 | _ -> false)
        (match covariant with
         | Some n ->
             reject (sprintf "the kernel of this former would receive COMPONENTS of node-covariant '%s'; component access inside perm-certified bodies lands in v2 with per-axis tracking. Use the whole-array elementwise operators (pointwise maps commute with relabelling) or ml.derive_perm_linear" n)
         | None -> Ok (Pow 0))
    | ExprKind.ExprBinOp (_, op, l, r) ->
        j l |> Result.bind (fun sl ->
        j r |> Result.bind (fun sr ->
            match op with
            // +, -, *, / are all POINTWISE on flat buffers, so all four
            // preserve the rank — this is the arm where MLEquiv admits only
            // Rep +/- Rep and scaling by an invariant, and rejects Rep * Rep
            // outright (BL4008, "use ml.tensor_product"). Division is the same
            // pointwise argument as multiplication: a reciprocal is applied
            // cell-by-cell, and a Pow 0 denominator is a constant.
            | OpAdd | OpSub | OpMul | OpDiv ->
                combinePointwise [ sl; sr ]
                |> Result.mapError (fun m -> bl4012 e.Span (sprintf "function '%s': %s" ctx.FuncName m))
            | _ ->
                match sl, sr with
                | Pow 0, Pow 0 -> Ok (Pow 0)
                | POpaque, _ | _, POpaque -> Ok POpaque
                | _ ->
                    reject "this operator is not classified on node-covariant values in v1 — combine node powers with +, -, *, / (pointwise), or with ml.derive_perm_linear / ml.perm_matmul"))
    | ExprKind.ExprIf (c, t, f) ->
        j c |> Result.bind (fun sc ->
            match sc with
            | Pow 0 ->
                j t |> Result.bind (fun st ->
                j f |> Result.bind (fun sf ->
                    if st = sf then Ok st
                    else reject (sprintf "if branches disagree: then-branch is %s, else-branch is %s" (statusStr st) (statusStr sf))))
            | _ -> reject "an if condition inside a perm-certified body must be invariant — branching on a node-covariant value makes the result depend on the node labelling")
    | ExprKind.ExprMatch (scrut, cases) ->
        j scrut |> Result.bind (fun ss ->
            match ss with
            | Pow 0 ->
                cases
                |> judgeEach (fun c -> judge ctx (bindPatternVars (Pow 0) env c.Pattern) c.Body)
                |> Result.bind (fun sts ->
                    match sts with
                    | [] -> Ok (Pow 0)
                    | s :: rest when rest |> List.forall ((=) s) -> Ok s
                    | _ -> reject "match arms disagree on their node-covariance status")
            | _ -> reject "a match scrutinee inside a perm-certified body must be invariant")
    | ExprKind.ExprLet (binding, body) ->
        j binding.Value |> Result.bind (fun sv ->
            match binding.Pattern.Kind, sv with
            | PatternKind.PatVar n, _ -> judge ctx (Map.add n sv env) body
            | _, Pow 0 -> judge ctx (bindPatternVars (Pow 0) env binding.Pattern) body
            | _, _ -> reject "cannot destructure a node-covariant value in v1 — bind it whole")
    | ExprKind.ExprLambda (ps, _, lamBody) ->
        let captured = freeVars (Set.ofList (ps |> List.map (fun p -> p.Name))) lamBody
        let covCapture =
            captured |> Set.toList |> List.tryFind (fun n ->
                match Map.tryFind n env with Some (Pow k) -> k > 0 | _ -> false)
        match covCapture with
        | Some n -> reject (sprintf "lambda captures node-covariant '%s' — factor node work into perm-certified functions instead" n)
        | None -> Ok (Pow 0)
    | ExprKind.ExprAssign (l, r) ->
        judgeAssign ctx env e.Span l r |> Result.map (fun () -> Pow 0)
    | ExprKind.ExprBlock (stmts, finalE) ->
        judgeStmts ctx env stmts
        |> Result.bind (fun env' ->
            match finalE with
            | Some fe -> judge ctx env' fe
            | None -> Ok (Pow 0))
    | ExprKind.ExprApp (f, args) -> judgeApp ctx env e f args
    | ExprKind.ExprField (_, _) -> Ok POpaque
    // --- functional iteration (the post-imperative surface) ------------------
    // Virtual arrays enumerate indices: label-independent by nature.
    | ExprKind.ExprRange _ | ExprKind.ExprReverse _ | ExprKind.ExprHalo _ -> Ok (Pow 0)
    // compute is a scheduling boundary, not a value transform.
    | ExprKind.ExprCompute x -> judge ctx env x
    // A reduce over a Pow k IS invariant when the combiner is commutative (a
    // sum over all cells does not see the labelling) — but v1 does not analyse
    // the combiner, and the CERTIFIED spelling of that sum already exists: it
    // is ml.derive_perm_linear(K, 0, N, x, w), the invariant readout, whose
    // completeness is the theorem. So this arm refuses and points there.
    | ExprKind.ExprReduce (src, _, init) ->
        judge ctx env src |> Result.bind (fun ss ->
            (match init with
             | Some i -> judge ctx env i
             | None -> Ok (Pow 0)) |> Result.bind (fun si ->
                match ss, si with
                | Pow 0, Pow 0 -> Ok (Pow 0)
                | POpaque, _ | _, POpaque -> Ok POpaque
                | _ ->
                    Error (bl4012 e.Span (sprintf "function '%s': reduce over a node-covariant value is invariant only for a commutative combiner, which v1 does not check — the certified invariant readout is ml.derive_perm_linear(K, 0, %d, x, w), whose basis is COMPLETE (every S_n-invariant linear form on the node power is one weight setting)" ctx.FuncName ctx.N))))
    | _ -> Ok POpaque

and private judgeStmts (ctx: Ctx) (env: Map<string, PowStatus>) (stmts: Stmt list)
    : Result<Map<string, PowStatus>, Blade.Diagnostics.Diagnostic> =
    stmts
    |> List.fold (fun acc s ->
        acc |> Result.bind (fun env ->
            match unwrapStmt s with
            | StmtLet binding ->
                judge ctx env binding.Value |> Result.bind (fun sv ->
                    match binding.Pattern.Kind, sv with
                    | PatternKind.PatVar n, _ -> Ok (Map.add n sv env)
                    | _, Pow 0 -> Ok (bindPatternVars (Pow 0) env binding.Pattern)
                    | _, _ ->
                        Error (bl4012 binding.Value.Span (sprintf "function '%s': cannot destructure a node-covariant value in v1 — bind it whole" ctx.FuncName)))
            | StmtExpr e2 -> judge ctx env e2 |> Result.map (fun _ -> env)
            | StmtAssign (l, _, r) -> judgeAssign ctx env l.Span l r |> Result.map (fun () -> env)
            | StmtForIn (v, range, body) ->
                judge ctx env range |> Result.bind (fun sr ->
                    match sr with
                    | Pow k when k > 0 ->
                        Error (bl4012 range.Span (sprintf "function '%s': cannot iterate a node-covariant value as a range" ctx.FuncName))
                    | _ ->
                        judgeStmts ctx (Map.add v (Pow 0) env) body |> Result.map (fun _ -> env))
            | _ -> Ok env))
        (Ok env)

/// Assignments: whole-variable writes must preserve the status; element writes
/// into a node power are the write-side twin of the component READ v1 defers.
and private judgeAssign (ctx: Ctx) (env: Map<string, PowStatus>) (span: Span) (l: Expr) (r: Expr)
    : Result<unit, Blade.Diagnostics.Diagnostic> =
    let fail msg = Error (bl4012 span (sprintf "function '%s': %s" ctx.FuncName msg))
    judge ctx env r |> Result.bind (fun sr ->
        match l.Kind with
        | ExprKind.ExprVar n ->
            match Map.tryFind n env with
            | Some st when st = sr -> Ok ()
            | Some st -> fail (sprintf "assignment changes '%s' from %s to %s — a mut binding must keep one status" n (statusStr st) (statusStr sr))
            | None -> Ok ()
        | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar n }, _) ->
            match Map.tryFind n env with
            | Some (Pow k) when k > 0 ->
                fail (sprintf "element-assignment into node-covariant '%s' writes ONE cell of a node power, which v1 cannot tell from an equivariant reassembly; per-axis tracking lands in v2. Build node powers with ml.derive_perm_linear / ml.derive_perm_bias / ml.perm_matmul, or with whole-array elementwise arithmetic" n)
            | _ ->
                match sr with
                | Pow k when k > 0 -> fail "cannot store a node-covariant value into an array element"
                | _ -> Ok ()
        | _ ->
            match sr with
            | Pow k when k > 0 -> fail "unsupported assignment target for a node-covariant value"
            | _ -> Ok ())

and private judgeApp (ctx: Ctx) (env: Map<string, PowStatus>) (e: Expr) (f: Expr) (args: Expr list)
    : Result<PowStatus, Blade.Diagnostics.Diagnostic> =
    let reject msg = Error (bl4012 e.Span (sprintf "function '%s': %s" ctx.FuncName msg))
    let judgeAll args = judgeEach (judge ctx env) args
    let requirePow (what: string) (k: int) (argE: Expr) =
        judge ctx env argE |> Result.bind (fun s ->
            if s = Pow k then Ok ()
            else
                Error (bl4012 argE.Span
                           (sprintf "function '%s': %s must be %s, but the argument is %s"
                                ctx.FuncName what (statusStr (Pow k)) (statusStr s))))
    let staticInt (what: string) (argE: Expr) : Result<int, Blade.Diagnostics.Diagnostic> =
        staticArgValue ctx.Statics argE
        |> Result.bind (fun sv -> match sv with SVInt n -> Ok (int n) | _ -> Error "must be a static int")
        |> Result.mapError (fun m -> bl4012 argE.Span (sprintf "function '%s': %s: %s" ctx.FuncName what m))
    /// The op's N must be THE certificate's N: one function body, one node
    /// axis. (The MLEquiv group-mismatch precedent, one lattice down.)
    let requireN (op: string) (n': int) (argE: Expr) =
        if n' = ctx.N then Ok ()
        else
            Error (bl4012 argE.Span
                       (sprintf "function '%s': ml.%s is called with N = %d, but this function is certified `ml.perm_equiv(%d)`. A certificate names ONE node axis — an S_%d-equivariant op proves nothing about S_%d relabellings. Match the extents, or split the body into two certified functions"
                            ctx.FuncName op n' ctx.N n' ctx.N))
    match f.Kind with
    // --- ml ops (surface-visible pre-rewrite) -------------------------------
    | ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, op) when Set.contains alias ctx.MlAliases ->
        (match op, args with
         | "derive_perm_linear", [ kE; lE; nE; xE; wE ] ->
             staticInt "derive_perm_linear K" kE |> Result.bind (fun k ->
             staticInt "derive_perm_linear L" lE |> Result.bind (fun l ->
             staticInt "derive_perm_linear N" nE |> Result.bind (fun n' ->
                 requireN "derive_perm_linear" n' nE |> Result.bind (fun () ->
                 requirePow "derive_perm_linear input" k xE |> Result.bind (fun () ->
                 requirePow "derive_perm_linear weight buffer" 0 wE |> Result.map (fun () ->
                     // The complete Hom_{Sn}(R^{N^K}, R^{N^L}) basis: the
                     // result IS a node power of rank L, by construction.
                     Pow l))))))
         | "derive_perm_bias", [ lE; nE; bE ] ->
             staticInt "derive_perm_bias L" lE |> Result.bind (fun l ->
             staticInt "derive_perm_bias N" nE |> Result.bind (fun n' ->
                 requireN "derive_perm_bias" n' nE |> Result.bind (fun () ->
                 requirePow "derive_perm_bias coefficient buffer" 0 bE |> Result.map (fun () ->
                     // The rep-INTRODUCTION form: the complete space of
                     // S_n-invariant constants in R^{N^L}.
                     Pow l))))
         | "perm_matmul", [ nE; aE; bE ] ->
             staticInt "perm_matmul N" nE |> Result.bind (fun n' ->
                 requireN "perm_matmul" n' nE |> Result.bind (fun () ->
                 requirePow "perm_matmul left factor" 2 aE |> Result.bind (fun () ->
                 requirePow "perm_matmul right factor" 2 bE |> Result.map (fun () ->
                     // (P A P^T)(P B P^T) = P (A B) P^T — the PPGN engine, the
                     // one bilinear shipped BY NAME rather than by synthesis.
                     Pow 2))))
         | ("derive_perm_linear" | "derive_perm_bias" | "perm_matmul"), _ ->
             reject (sprintf "%s: unrecognized call shape inside a perm-certified body" op)
         | _ ->
             // every other ml.* op (the O(3) surface included): its arguments
             // live in irreps space or in sizing space, neither of which
             // carries a node axis, so invariants in / invariant out.
             judgeAll args |> Result.bind (fun sts ->
                 if sts |> List.forall ((=) (Pow 0)) then Ok (Pow 0)
                 else reject (sprintf "ml.%s carries no S_n rule for node-covariant arguments — the node-axis ops are ml.derive_perm_linear, ml.derive_perm_bias and ml.perm_matmul" op)))
    // --- named callees ------------------------------------------------------
    | ExprKind.ExprVar fn ->
        match Map.tryFind fn ctx.Certs with
        | Some cert ->
            if cert.N <> ctx.N then
                reject (sprintf "call to '%s': it is certified for N = %d, this function for N = %d — certificates do not transfer between node-axis extents" fn cert.N ctx.N)
            elif List.length args <> List.length cert.Params then
                reject (sprintf "call to '%s': expected %d arguments" fn (List.length cert.Params))
            else
                (List.zip cert.Params args)
                |> List.fold (fun acc ((pName, pSt), argE) ->
                    acc |> Result.bind (fun () ->
                        match pSt with
                        | POpaque -> reject (sprintf "call to '%s': parameter '%s' is unclassifiable" fn pName)
                        | Pow k -> requirePow (sprintf "'%s' parameter '%s'" fn pName) k argE))
                    (Ok ())
                |> Result.map (fun () -> cert.Return)
        | None ->
            match Map.tryFind fn env with
            | Some (Pow k) when k > 0 ->
                // A read out of a node power. LEGAL in the mathematics when the
                // index is a bound loop variable (§3.6 — the node basis is
                // real, unlike the irreps basis MLEquiv guards); v1 has no
                // loop-variable tracking, so it refuses uniformly.
                reject (sprintf "component access into node-covariant '%s' inside a perm-certified body lands in v2 with per-axis tracking — v1 cannot tell a bound-index read (which reassembles equivariantly) from a fixed-offset one (which does not). Whole-array elementwise operators are pointwise, hence equivariant, and ml.derive_perm_linear(K, 0, %d, x, w) is the complete invariant readout" fn ctx.N)
            | _ ->
                judgeAll args |> Result.bind (fun sts ->
                    match sts |> List.tryFindIndex (fun s -> s <> Pow 0) with
                    | None -> Ok (Pow 0)
                    | Some i ->
                        // THE POLARITY ARM. A pointwise scalar builtin applied
                        // to a node power is EQUIVARIANT (see
                        // isPointwiseBuiltin's comment) — the exact opposite of
                        // MLEquiv's verdict on the same shape.
                        if isPointwiseBuiltin fn then
                            combinePointwise sts
                            |> Result.mapError (fun m -> bl4012 e.Span (sprintf "function '%s': %s" ctx.FuncName m))
                        else
                            Error (bl4012 args.[i].Span
                                       (sprintf "function '%s': a node-covariant value escapes to '%s', which carries no perm certificate — certify it with `where ml.perm_equiv(%d)` or pass only invariants"
                                            ctx.FuncName fn ctx.N)))
    | _ ->
        judgeAll args |> Result.bind (fun sts ->
            judge ctx env f |> Result.bind (fun sf ->
                if sf = Pow 0 && sts |> List.forall ((=) (Pow 0)) then Ok (Pow 0)
                else reject "cannot classify this call inside a perm-certified body"))

/// Judge one certified function. Empty list = certificate holds.
let judgeFunction (certs: Map<string, PermSig>) (statics: StaticEnv) (mlAliases: Set<string>)
                  (fd: FunctionDecl)
    : Blade.Diagnostics.Diagnostic list =
    match Map.tryFind fd.Name certs with
    | None -> []
    | Some cert ->
        let ctx = { FuncName = fd.Name; N = cert.N; MlAliases = mlAliases; Statics = statics; Certs = certs }
        let env = cert.Params |> List.fold (fun m (n, st) -> Map.add n st m) Map.empty
        match judge ctx env fd.Body with
        | Error d -> [ d ]
        | Ok st ->
            if st = cert.Return then []
            else
                [ bl4012 fd.Body.Span
                      (sprintf "function '%s': the body is %s but the declared return type says %s — the certificate requires them to agree" fd.Name (statusStr st) (statusStr cert.Return)) ]

// ============================================================================
// Constraint-registry handler
// ============================================================================

/// `perm_equiv(N)` is a callee-side theorem: Validate re-checks the conjunct
/// shape (the elaborator has already judged the body by the time
/// checkFunctionDecl runs, and it is the elaborator that resolves a `let
/// static` N against the static environment), the license scope is unused, and
/// call sites carry no obligation.
let private permHandler : Blade.Constraints.ConstraintHandler = {
    Describe = "perm_equiv(N) — certifies the function equivariant under the symmetric group S_N relabelling a node axis of extent N; the ML elaborator proves the body composes only relabelling-equivariant operations"
    Validate = fun funcName _ args ->
        match args with
        | [ a ] ->
            match System.Int32.TryParse a with
            | true, n when n < 2 ->
                Error (sprintf "function '%s': perm_equiv(%d) — N must be >= 2 (the node-axis extent; S_1 is trivial and the flat extent keying needs strictly increasing powers)" funcName n)
            | _ -> Ok () // an int >= 2, or a `let static` name the elaborator resolves
        | _ ->
            Error (sprintf "function '%s': perm_equiv expects exactly one argument — the node-axis extent N, as in `where ml.perm_equiv(4)`" funcName)
    EnterBody = fun _ _ -> ()
    ExitBody = fun _ _ -> ()
    Discharge = fun _ _ _ -> Ok ()
}

let mutable private registered = false

let register () =
    if not registered then
        registered <- true
        Blade.Constraints.registerConstraint "__ml_perm_equiv" permHandler
