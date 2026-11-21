/// Math-module elaboration: dense linear algebra and tensor decompositions as compile-time source synthesis, mirroring the
/// ml/ppl elaborators.
///
/// Surface (reachable only through `import math [as <alias>]`; config ints are `let static` names or literals). The ops
/// read a STATIC shape (this pass runs before type inference and the generated routine is specialized to the extents),
/// which Blade.ElabShapes answers for every domain elaborator: a declared annotation (a let, a parameter, a function
/// return type, an ascription), an array literal / cast / static range / computed range map, the result of an elaborated
/// call (`m.eigh(S)`, `sp.fft(x)`, a call nested in the argument), or an alias, destructured component or projection of
/// any of those. A user-DECLARED element is judged as before #62 (the checker, the marker gates, the generated routine);
/// an element this pass inferred must be Float64.
///
///   m.matmul(A, B)                 -- m x k . k x n -> m x n
///   m.solve(A, b)                  -- A.x = b by partial-pivoted LU, A n x n, b n -> x n
///   m.lu(A)                        -- the factorization KEPT: (LU, piv), LU n x n Float64, piv n Int64
///   m.lu_solve(f, b) | m.lu_solve(LU, piv, b)      -- A.x = b from the factors, bitwise m.solve(A, b)
///   m.lu_solve_t(f, b) | m.lu_solve_t(LU, piv, b)  -- A^T.x = b from the same factors
///   m.svd(A) | m.svd(A, SWEEPS)    -- thin SVD, m >= n -> (U, S, V), S descending
///   m.eigh(S) | m.eigh(S, SWEEPS)  -- symmetric -> (Q, LAM), LAM descending
///   m.expm(A)                      -- matrix exponential, A n x n (scaled Taylor-18 + squarings)
///   m.unfold(X, MODE)              -- Kolda-Bader mode-n matricization
///   m.mode_product(X, U, MODE)     -- tensor x matrix along MODE
///   m.hosvd(X) | m.hosvd(X, R1..RN)-- Tucker/HOSVD -> (G, U1, ..., UN)
///
/// For each distinct (op, resolved shape/config) the elaborator synthesizes ONE Blade function (`__math_1`, ...) via
/// Blade.Math.Decls; call sites rewrite to the generated names, deduped by fingerprint.
///
/// Pipeline position: after ML/PPL elaboration, before Grad expansion, so generated bodies remain plain differentiable
/// Blade source. The surface shapes are registered (`registerShapes`) before ANY elaboration runs, so an earlier pass
/// (sgs, ppl) can read the shape of a math call it meets.
module Blade.Math.Elaborate

open Blade.Ast
open Blade.StaticEval
open Blade.ElabShapes
open Blade.Math.Decls

/// The steer appended to every "no static shape here" rejection.
let private steer =
    "math ops specialize the generated routine to the extents at compile time, so annotate the binding (`let x: Array<Float64 like Idx<n>, Idx<m>> = ...`) or ascribe the argument (`(e : Array<Float64 like Idx<n>, Idx<m>>)`)"

/// The static extents of an op's array argument. Its element follows ElabShapes.requireFloat64: a USER-declared element
/// is the op's to accept as before #62 (the checker, the marker gates -- TypeCheck.inferMatmul / inferSolve / inferLu /
/// inferEigh's classifyEigh -- and the generated routines judge it; a Hermitian Complex128 eigh is the synthesized
/// Jacobi with the LAPACK gate off), and an element this pass INFERRED must be Float64, refused here with the reason.
let private dimsOf (env: Env) (scope: Scope) (what: string) (e: Expr) : Result<int list, string> =
    arrayShape env scope what steer e |> Result.bind (fun (label, s) ->
        staticExtents what label s |> Result.bind (fun dims ->
            requireFloat64 env what label s |> Result.map (fun () -> dims)))

/// The marker ops use the same rule (kept as its own name: the marker routes are where the checker owns the element).
let private dimsOfMarker = dimsOf
/// A marker's array operands are emitted by codegen as stored arrays, never as an inline literal (no IRArrayLit operand),
/// so an inline array literal operand of a marker op is refused here with the fix. (Binding it to a compiler temporary
/// instead would need hygienic names and would move its evaluation ahead of the earlier operands.)
let private refuseLiteralOperands (env: Env) (scope: Scope) (what: string) (operands: Expr list) : Result<unit, string> =
    // As WRITTEN: a bare literal or a cast of one (an ascribed literal is the user's, judged as before #62).
    let rec isLiteral (e: Expr) =
        match e.Kind with
        | ExprKind.ExprArrayLit _ -> true
        // A numeric cast of one -- unless the name is a user function or a local (a cast name is shadowable).
        | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar ("Float64" | "Float32" | "Int64" | "Int32" as c) }, [ inner ])
                when (env.Funcs c).IsNone && (env.Values c).IsNone && not (Map.containsKey c scope) -> isLiteral inner
        | _ -> false
    if operands |> List.exists isLiteral then
        Error $"{what}: an inline array literal operand is not supported by this op -- bind it first (`let a: Array<Float64 like Idx<n>, Idx<m>> = [...]`)"
    else Ok ()
/// Resolve a static-argument expression: a plain variable naming a `let static` binding, or an inline int literal.
let private staticArg (statics: StaticEnv) (what: string) (e: Expr) : Result<StaticValue, string> =
    match e.Kind with
    | ExprKind.ExprLit (LitInt n) -> Ok (SVInt n)
    | ExprKind.ExprVar name ->
        match Map.tryFind name statics.Values with
        | Some sv -> Ok sv
        | None -> Error $"{what}: '{name}' is not a `let static` binding (math op configs must be static)"
    | _ -> Error $"{what}: config argument must be a `let static` binding name or literal"

let private staticInt (statics: StaticEnv) (what: string) (e: Expr) : Result<int, string> =
    staticArg statics what e |> Result.bind (fun sv ->
        match sv with
        | SVInt n -> Ok (int n)
        | _ -> Error $"{what}: expected a static int")

// Op elaboration

let private opList = "matmul, solve, lu, lu_solve, lu_solve_t, svd, eigh, eig, expm, unfold, mode_product, hosvd"

/// Optional trailing SWEEPS argument shared by svd/eigh.
let private sweepsArg (statics: StaticEnv) (what: string) (rest: Expr list) : Result<int, string> =
    match rest with
    | [] -> Ok defaultSweeps
    | [swE] ->
        staticInt statics (what + " SWEEPS") swE |> Result.bind (fun s ->
            if s >= 1 then Ok s else Error $"{what}: SWEEPS must be >= 1")
    | _ -> Error $"{what}: at most one SWEEPS argument"

/// Elaborate one qualified op call. Arguments arrive already rewritten (a nested elaborated call first).
let private elabOp (st: GenState) (env: Env) (scope: Scope) (op: string) (args: Expr list) : Result<Expr, string> =
    let statics = env.Statics
    // As written: a marker op refuses a BARE inline literal operand (refuseLiteralOperands); the synthesized routines take
    // Float64 arrays, so an inline float-literal argument takes that width there.
    let written = args
    let args = args |> List.map ascribeFloatLiteral
    match op, args with
    | "matmul", [aE; bE] ->
        refuseLiteralOperands env scope "matmul" written |> Result.bind (fun () ->
        dimsOfMarker env scope "matmul" aE |> Result.bind (fun aDims ->
        dimsOfMarker env scope "matmul" bE |> Result.bind (fun bDims ->
            match aDims, bDims with
            | [_m; k], [k2; _n] when k = k2 ->
                // matmul is not synthesized as a per-(m,k,n) Blade triple loop: it rewrites to the `__math_matmul` intrinsic
                // marker, which TypeCheck.inferMatmul types and codegen emits as one `blade_linalg::blade_matmul` call (the
                // shim resolves to cblas_dgemm or a native fallback depending on the build). svd/eigh/eig/unfold/
                // mode_product/hosvd stay synthesized. Shape validation above is unchanged: matmul still reads the
                // static shapes and rejects the same programs with the same messages -- only what it lowers to changed.
                Ok (syn (ExprApp (v "__math_matmul", [aE; bE])))
            | [_; k], [k2; _] ->
                Error $"matmul: inner extents disagree (A is ..x{k}, B is {k2}x..)"
            | _ -> Error "matmul: both arguments must be rank-2 (mxk * kxn)")))
    | "matmul", _ -> Error "matmul: expected matmul(A, B)"
    | "solve", [aE; bE] ->
        refuseLiteralOperands env scope "solve" written |> Result.bind (fun () ->
        dimsOfMarker env scope "solve" aE |> Result.bind (fun aDims ->
        dimsOfMarker env scope "solve" bE |> Result.bind (fun bDims ->
            match aDims, bDims with
            | [n; n2], [m] when n = n2 && m = n ->
                // Like matmul and UNLIKE svd/eigh/eig, solve is not synthesized as a per-n Blade routine: it rewrites to
                // the `__math_solve` intrinsic marker, which TypeCheck.inferSolve types and codegen emits as either its own
                // LU loop nest or one `blade_lapack::blade_solve` call. NOTE THE ABSENCE OF A GATE CHECK HERE, which is the
                // one place this arm differs from eigh's: eigh's marker exists only when `lapackAvailable ()` held, because
                // its non-dispatched arm is synthesized SOURCE that must be generated instead. Solve's non-dispatched arm
                // is emitted by codegen from the same node, so the node is unconditional and the gate is consulted one
                // level down, at emission. That keeps the surface's meaning independent of the build.
                Ok (syn (ExprApp (v "__math_solve", [aE; bE])))
            | [n; n2], _ when n <> n2 ->
                Error $"solve: A must be square (got {n}x{n2})"
            | [n; _], [m] ->
                Error $"solve: b's extent must match A's dimension (A is {n}x{n}, b is length {m})"
            | [_; _], _ ->
                Error "solve: b must be rank-1 (Array<Float64 like Idx<n>>)"
            | _ -> Error "solve: A must be rank-2 square (Array<Float64 like Idx<n>, Idx<n>>)")))
    | "solve", _ -> Error "solve: expected solve(A, b) -- A an n x n matrix, b a length-n vector, returning x with A.x = b"
    // FACTOR ONCE (plan-fortran-killer-2 section 6.2): `lu(A)` keeps the
    // partial-pivoted LU as a value -- an ordinary tuple (LU, piv) of two
    // fresh arrays -- and `lu_solve` / `lu_solve_t` apply it to any number of
    // right-hand sides (A x = b / A^T x = b) without refactoring. Both apply
    // forms take the tuple or its two halves: `m.lu_solve(f, b)` is rewritten
    // to `__math_lu_solve(f[0], f[1], b)` here, so the checker only ever sees
    // the three-array marker.
    | "lu", [aE] ->
        refuseLiteralOperands env scope "lu" written |> Result.bind (fun () ->
        dimsOfMarker env scope "lu" aE |> Result.bind (fun aDims ->
            match aDims with
            | [n; n2] when n = n2 -> Ok (syn (ExprApp (v "__math_lu", [aE])))
            | [n; n2] -> Error $"lu: A must be square (got {n}x{n2})"
            | _ -> Error "lu: A must be rank-2 square (Array<Float64 like Idx<n>, Idx<n>>)"))
    | "lu", _ -> Error "lu: expected lu(A) -- A an n x n matrix, returning the factorization (LU, piv)"
    | ("lu_solve" | "lu_solve_t"), [fE; bE] ->
        let marker = if op = "lu_solve" then "__math_lu_solve" else "__math_lu_solve_t"
        let proj (i: int64) = syn (ExprTupleIndex (fE, syn (ExprLit (LitInt i))))
        Ok (syn (ExprApp (v marker, [proj 0L; proj 1L; bE])))
    | ("lu_solve" | "lu_solve_t"), [luE; pivE; bE] ->
        let marker = if op = "lu_solve" then "__math_lu_solve" else "__math_lu_solve_t"
        Ok (syn (ExprApp (v marker, [luE; pivE; bE])))
    | ("lu_solve" | "lu_solve_t"), _ ->
        Error $"{op}: expected {op}(f, b) with f = lu(A), or {op}(LU, piv, b) -- b a length-n vector, returning x"
    | "expm", [aE] ->
        // Synthesized like svd/eigh (a plain Blade body: scaled Taylor-18 Horner plus squarings), so the interpreter
        // runs it unchanged and a later grad rule can inline it; the shape is the fingerprint.
        dimsOf env scope "expm" aE |> Result.bind (fun dims ->
            match dims with
            | [n; n2] when n = n2 ->
                ensure st (fingerprint "expm" (box n)) "expm" (fun nm -> Ok (expmDecl nm n))
                |> Result.map (fun nm -> syn (ExprApp (v nm, [aE])))
            | [n; n2] -> Error $"expm: the argument must be square (got {n}x{n2})"
            | _ -> Error "expm: the argument must be rank-2 (Array<Float64 like Idx<n>, Idx<n>>)")
    | "expm", _ -> Error "expm: expected expm(A) with A a square Float64 matrix"
    | "svd", (aE :: rest) ->
        sweepsArg statics "svd" rest |> Result.bind (fun sweeps ->
        dimsOf env scope "svd" aE |> Result.bind (fun dims ->
            match dims with
            | [m; n] when m >= n ->
                ensure st (fingerprint "svd" (box (m, n, sweeps))) "svd" (fun nm -> Ok (svdDecl nm m n sweeps))
                |> Result.map (fun nm -> syn (ExprApp (v nm, [aE])))
            | [m; n] ->
                Error $"svd: m < n unsupported ({m}x{n}); svd the transpose (transpose(A, [0, 1])) and swap U/V"
            | _ -> Error "svd: the argument must be rank-2 (Array<Float64 like Idx<m>, Idx<n>>)"))
    | "svd", _ -> Error "svd: expected svd(A) or svd(A, SWEEPS)"
    | "eigh", (aE :: rest) ->
        let markerRoute = List.isEmpty rest && Blade.LinAlgPatterns.lapackAvailable ()
        sweepsArg statics "eigh" rest |> Result.bind (fun sweeps ->
        (if markerRoute then dimsOfMarker else dimsOf) env scope "eigh" aE |> Result.bind (fun dims ->
            match dims with
            | [n; n2] when n = n2 ->
                // `eigh` routes to the `__math_eigh` intrinsic marker (TypeCheck.inferEigh types it, codegen emits one
                // `blade_lapack::blade_eigh_*` call) only when BOTH hold: LAPACK is available at build time
                // (`LinAlgPatterns.lapackAvailable ()`, the same predicate Build.fs uses for `-DBLADE_HAS_LAPACK` -- two
                // copies disagreeing would emit calls into a header that won't compile); and NO explicit SWEEPS argument
                // was given (a stated sweep budget requests the cyclic-Jacobi ALGORITHM, which LAPACK's blocked tridiagonal
                // reduction has no analogue of, so `m.eigh(S, 30)` keeps the synthesized path on any machine).
                //
                // With the gate off (the default) the synthesized path is untouched, byte for byte: same
                // `ensure`/fingerprint key, same `eighDecl`, same call expression. That is load-bearing -- the Jacobi
                // source is the single copy of this math, the one the interpreter and pinned-oracle differentials cover,
                // and an eigensolver's output is not unique (eigenvector signs, degenerate-subspace bases), so it stays
                // the verification truth. `interp` / `diff-oracle` must never run with the LAPACK gate set.
                if markerRoute then
                    refuseLiteralOperands env scope "eigh" (List.truncate 1 written) |> Result.map (fun () -> syn (ExprApp (v "__math_eigh", [aE])))
                else
                    ensure st (fingerprint "eigh" (box (n, sweeps))) "eigh" (fun nm -> Ok (eighDecl nm n sweeps))
                    |> Result.map (fun nm -> syn (ExprApp (v nm, [aE])))
            | [n; n2] -> Error $"eigh: the argument must be square (got {n}x{n2}); symmetry is assumed, not checked"
            | _ -> Error "eigh: the argument must be rank-2 square (Array<Float64 like Idx<n>, Idx<n>>, symmetric)"))
    | "eigh", _ -> Error "eigh: expected eigh(S) or eigh(S, SWEEPS)"
    | "eig", (aE :: rest) ->
        dimsOf env scope "eig" aE |> Result.bind (fun dims ->
            match dims with
            | [n; n2] when n = n2 ->
                let maxIterRes =
                    match rest with
                    | [] -> Ok (30 * n)
                    | [mE] ->
                        staticInt statics "eig MAXITER" mE |> Result.bind (fun mi ->
                            if mi >= 1 then Ok mi else Error "eig: MAXITER must be >= 1")
                    | _ -> Error "eig: at most one MAXITER argument"
                maxIterRes |> Result.bind (fun maxIter ->
                    ensure st (fingerprint "eig" (box (n, maxIter))) "eig" (fun nm -> Ok (eigDecl nm n maxIter))
                    |> Result.map (fun nm -> syn (ExprApp (v nm, [aE]))))
            | [n; n2] -> Error $"eig: the argument must be square (got {n}x{n2})"
            | _ -> Error "eig: the argument must be rank-2 square (Array<Float64 like Idx<n>, Idx<n>>)")
    | "eig", _ -> Error "eig: expected eig(A) or eig(A, MAXITER) -- returns (LRE, LIM) by descending modulus"
    | "unfold", [xE; modeE] ->
        staticInt statics "unfold MODE" modeE |> Result.bind (fun mode ->
        dimsOf env scope "unfold" xE |> Result.bind (fun dims ->
            let r = dims.Length
            if r < 2 || r > 4 then
                Error $"unfold: tensor rank must be 2..4 (got rank {r}); the generator is rank-generic -- raise the cap when needed"
            elif mode < 0 || mode >= r then
                Error $"unfold: MODE must be in 0..{r - 1} for a rank-{r} tensor (got {mode})"
            else
                ensure st (fingerprint "unfold" (box (dims, mode))) "unfold" (fun nm -> Ok (unfoldDecl nm dims mode))
                |> Result.map (fun nm -> syn (ExprApp (v nm, [xE])))))
    | "unfold", _ -> Error "unfold: expected unfold(X, MODE) with a static MODE"
    | "mode_product", [xE; uE; modeE] ->
        staticInt statics "mode_product MODE" modeE |> Result.bind (fun mode ->
        dimsOf env scope "mode_product" xE |> Result.bind (fun dims ->
        dimsOf env scope "mode_product" uE |> Result.bind (fun uDims ->
            let r = dims.Length
            if r < 2 || r > 4 then
                Error $"mode_product: tensor rank must be 2..4 (got rank {r})"
            elif mode < 0 || mode >= r then
                Error $"mode_product: MODE must be in 0..{r - 1} for a rank-{r} tensor (got {mode})"
            else
                match uDims with
                | [jOut; im] when im = dims.[mode] ->
                    ensure st (fingerprint "mode_product" (box (dims, mode, jOut))) "mode_product"
                        (fun nm -> Ok (modeProductDecl nm dims mode jOut))
                    |> Result.map (fun nm -> syn (ExprApp (v nm, [xE; uE])))
                | [_; im] ->
                    Error $"mode_product: U's second extent must match the mode-{mode} extent (U is ..x{im}, mode extent is {dims.[mode]})"
                | _ -> Error "mode_product: U must be rank-2 (Array<Float64 like Idx<j>, Idx<i_mode>>)")))
    | "mode_product", _ -> Error "mode_product: expected mode_product(X, U, MODE) with a static MODE"
    | "hosvd", (xE :: rankArgs) ->
        dimsOf env scope "hosvd" xE |> Result.bind (fun dims ->
            let r = dims.Length
            if r < 2 || r > 4 then
                Error $"hosvd: tensor rank must be 2..4 (got rank {r})"
            else
                let ranksRes =
                    if List.isEmpty rankArgs then Ok dims
                    elif rankArgs.Length <> r then
                        Error $"hosvd: expected {r} truncation ranks for a rank-{r} tensor (got {rankArgs.Length}); use hosvd(X) for the full decomposition"
                    else
                        rankArgs |> List.fold (fun acc rE ->
                            acc |> Result.bind (fun rs ->
                                staticInt statics "hosvd rank" rE |> Result.map (fun rk -> rs @ [rk])))
                            (Ok [])
                ranksRes |> Result.bind (fun ranks ->
                    if List.exists2 (fun rk ik -> rk < 1 || rk > ik) ranks dims then
                        Error (sprintf "hosvd: each truncation rank must be in 1..I_k (dims %A, ranks %A)" dims ranks)
                    else
                        // Per-mode helpers. The eigh fingerprint matches the m.eigh arm exactly, so hosvd and user eigh
                        // calls of the same shape share one generated function.
                        let gramNames =
                            [ for mode in 0 .. r - 1 ->
                                ensureT st (fingerprint "gram" (box (dims, mode))) "hosvd" (fun nm -> gramDecl nm dims mode) ]
                        let eighNames =
                            [ for mode in 0 .. r - 1 ->
                                ensureT st (fingerprint "eigh" (box (dims.[mode], defaultSweeps))) "eigh" (fun nm -> eighDecl nm dims.[mode] defaultSweeps) ]
                        // Successive core shapes: mode n contracts against dims-with-earlier-modes-already-truncated.
                        let mutable curDims = dims
                        let mptNames =
                            [ for mode in 0 .. r - 1 ->
                                let dcur = curDims
                                let nm = ensureT st (fingerprint "mpt" (box (dcur, mode, ranks.[mode]))) "hosvd"
                                            (fun nm -> modeProdTDecl nm dcur mode ranks.[mode])
                                curDims <- dcur |> List.mapi (fun k d -> if k = mode then ranks.[mode] else d)
                                nm ]
                        ensure st (fingerprint "hosvd" (box (dims, ranks))) "hosvd"
                            (fun nm -> Ok (hosvdDecl nm dims ranks gramNames eighNames mptNames))
                        |> Result.map (fun nm -> syn (ExprApp (v nm, [xE])))))
    | "hosvd", _ -> Error "hosvd: expected hosvd(X) or hosvd(X, R1, ..., RN) with static ranks"
    | _ -> Error $"math: unknown op '{op}' (available: {opList})"

// Result shapes: the surface ops (through their own scratch elaboration) and the intrinsic markers

let private markerOperand (env: Env) (scope: Scope) (e: Expr) : Result<ArrayShape, string> =
    match shapeOf env scope e with
    | Ok (VArray s) -> Ok s
    | Ok _ -> Error "its operand is not an array"
    | Error why -> Error why

let private dense (elem: TypeExpr) (idxs: TypeExpr list) (extents: int option list) : Shaped =
    Ok (VArray { Elem = ElemDeclared elem; Idxs = idxs; Extents = extents; UserElem = false })

let private float64Ty = TyNamed ("Float64", [])

/// Register the math surface ops and markers with the shared shape oracle. Idempotent; TypeCheck runs it (with every
/// domain's) before the FIRST elaboration pass, so a pass that runs before math can read a math call's result shape.
let registerShapes () =
    registerDomain "math" (scratchShape "__math_scratch_" elabOp)
    registerMarker "__math_matmul" "the result of the nested matmul call" (fun env scope args ->
        match args with
        | [ aE; bE ] ->
            markerOperand env scope aE |> Result.bind (fun a ->
            markerOperand env scope bE |> Result.bind (fun b ->
                match a.Idxs, b.Idxs with
                | [ i0; _ ], [ _; j1 ] ->
                    let elem = match a.Elem with ElemDeclared t -> t | _ -> float64Ty
                    dense elem [ i0; j1 ] [ a.Extents.[0]; b.Extents.[1] ]
                | _ -> Error "matmul's operands are not rank-2"))
        | _ -> Error "matmul expects two operands")
    registerMarker "__math_solve" "the result of the nested solve call" (fun env scope args ->
        match args with
        | [ _; bE ] -> markerOperand env scope bE |> Result.bind (fun b -> dense float64Ty b.Idxs b.Extents)
        | _ -> Error "solve expects two operands")
    for marker in [ "__math_lu_solve"; "__math_lu_solve_t" ] do
        let label = if marker = "__math_lu_solve" then "lu_solve" else "lu_solve_t"
        registerMarker marker $"the result of the nested {label} call" (fun env scope args ->
            match args with
            | [ _; _; bE ] -> markerOperand env scope bE |> Result.bind (fun b -> dense float64Ty b.Idxs b.Extents)
            | _ -> Error $"{label} expects three operands")
    registerMarker "__math_lu" "the result of the nested lu call" (fun env scope args ->
        match args with
        | [ aE ] ->
            markerOperand env scope aE |> Result.bind (fun a ->
                match a.Idxs with
                | [ i0; _ ] ->
                    Ok (VTuple [ dense float64Ty a.Idxs a.Extents
                                 dense (TyNamed ("Int64", [])) [ i0 ] [ a.Extents.[0] ] ])
                | _ -> Error "lu's operand is not rank-2")
        | _ -> Error "lu expects one operand")
    registerMarker "__math_eigh" "the result of the nested eigh call" (fun env scope args ->
        match args with
        | [ aE ] ->
            markerOperand env scope aE |> Result.bind (fun a ->
                match a.Idxs with
                | [ i0; _ ] ->
                    let elem = match a.Elem with ElemDeclared t -> t | _ -> float64Ty
                    Ok (VTuple [ dense elem a.Idxs a.Extents; dense float64Ty [ i0 ] [ a.Extents.[0] ] ])
                | _ -> Error "eigh's operand is not a dense rank-2 matrix")
        | _ -> Error "eigh expects one operand")

/// Entry point: elaborate math ops across a program (after ML/PPL elaboration, before Grad expansion). The boundary turns
/// a string-errored internal into one coded BL5200 diagnostic at the offending declaration.
let expand (program: Program) : Result<Program, Blade.Diagnostics.Diagnostic list> =
    registerShapes ()
    expandDomain "math" "__math_" "BL5200" elabOp program
