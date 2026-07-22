/// grad() — reverse-mode automatic differentiation as an AST-level source
/// transform (pre-typecheck), surfaced through the `ad` module.
///
/// Surface form:  `import ad as ad` then `ad.grad(f)` where `f` is a
/// top-level function in the same module returning a Float scalar. The pass
/// is import-gated (no `import ad` → no-op; bare `grad(...)` is unbound —
/// same module rule as the ml/ppl surfaces). The expression rewrites to a
/// reference to a synthesized function `f__grad`, so
/// `ad.grad(f)(args..., buffers...)` and `let g = ad.grad(f)` both work.
/// The synthesized function:
///
///   function f__grad(p1, ..., pn,                     // originals
///                    __g_pA: mut <type of pA>, ...)   // one out-buffer per
///                                                     // Float-ARRAY param
///       -> Float                                      // the primal value
///       |  (Float, Float, ...)                        // + one gradient per
///                                                     //   Float SCALAR param
///
/// Gradient out-buffers are ACCUMULATED into (callers zero them; repeated
/// calls sum — PyTorch .grad semantics). Int / int-array params (edge lists,
/// sizes) are non-differentiable and get no buffer.
///
/// Why source-to-source, pre-typecheck (user decision, AD arc): the
/// generated derivative is itself type-checked, lowered, and code-generated
/// by the existing pipeline — including the symmetry system, so adjoints of
/// symmetric computations inherit triangular storage with no AD-specific
/// storage logic.
///
/// ## The AD-able subset (v1)
///
/// Differentiated function bodies may contain:
///   - `let x = e` / `let mut x = e` (single-name patterns)
///   - accumulation `x += e`, `x -= e`, `a(i...) += e`, `a(i...) -= e`
///     (and their spelled-out forms `x = x + e` etc.)
///   - general (re)assignment in STRAIGHT-LINE positions; inside loops only
///     the additive-self pattern is allowed on scalars (loop-carried
///     recurrences would need taping/reversal)
///   - element writes `a(i...) = e` (construction of fresh arrays)
///   - `for k in lo..hi { ... }` to any nesting depth (int bounds,
///     non-differentiable); the ADJOINT loop runs in the SAME direction,
///     which is exact for the accumulation subset
///   - scalar arithmetic + - * / (and `^` by an int literal), unary minus
///   - math intrinsics (exp/log/sqrt/trig/hyperbolic; floor/ceil have zero
///     derivative)
///   - array reads `a(i...)` at integer index expressions
///   - calls to other AD-able functions in the same module — INLINED
///     (recursion rejected by a depth cap)
///   - float array literals
///
/// Deliberately rejected (clean errors): if/match in differentiated code,
/// combinator computations, tuple-pattern lets, reads of an accumulator
/// from expressions inside the loop that mutates it, writes to an
/// accumulator after straight-line code has read it, array-alias lets,
/// callees with mut params.
///
/// Loop-body `let`s are recomputed inside the adjoint loop (recompute-based
/// tape); function-level `let`s stay in scope across the reverse sweep, so
/// nothing else is stored. The C(n+r-1, r) triangular-tape result then
/// falls out of storing intermediates in whatever (possibly symmetric)
/// arrays the forward pass already uses.
module Blade.Grad

open Blade.Ast

// ============================================================================
// Math intrinsics — the single source of truth (TypeCheck reads these too)
// ============================================================================

/// Scalar math intrinsics recognized as plain calls (`exp(x)`) when the name
/// is not user-bound. Unary, real-valued, rendered as std::<name> in C++.
/// Keep in sync with StaticEval.evalBuiltin and derivRule below.
let mathIntrinsics : Set<string> =
    Set.ofList [
        "exp"; "log"; "sqrt"
        "sin"; "cos"; "tan"
        "sinh"; "cosh"; "tanh"
        "asin"; "acos"; "atan"
        "floor"; "ceil"
    ]

let isMathIntrinsic (name: string) : bool = Set.contains name mathIntrinsics

/// Subset of the intrinsics that have std::complex overloads in <complex> and
/// so are permitted on complex operands (result is complex, same width). exp/
/// log/sqrt and the trig/hyperbolic families qualify; floor/ceil do not (no
/// complex overload — they stay real-only and reject complex). Differentiation
/// of complex intrinsics is out of scope (see the adjoint note in Grad).
let complexMathIntrinsics : Set<string> =
    Set.ofList [
        "exp"; "log"; "sqrt"
        "sin"; "cos"; "tan"
        "sinh"; "cosh"; "tanh"
        "asin"; "acos"; "atan"
    ]

let isComplexMathIntrinsic (name: string) : bool = Set.contains name complexMathIntrinsics

// ============================================================================
// Expression construction helpers
// ============================================================================

// Span-free derivative-synthesis builders: `syn` stamps each node with the
// ambient `synthSpan` (set to the differentiated decl's span in expand).
let private fLit (v: float) = syn (ExprLit (LitFloat v))
let private iLit (n: int64) = syn (ExprLit (LitInt n))
let private v (name: string) = syn (ExprVar name)
let private add a b = syn (ExprBinOp (Elementwise, OpAdd, a, b))
let private sub a b = syn (ExprBinOp (Elementwise, OpSub, a, b))
let private mul a b = syn (ExprBinOp (Elementwise, OpMul, a, b))
let private div a b = syn (ExprBinOp (Elementwise, OpDiv, a, b))
let private pow a b = syn (ExprBinOp (Elementwise, OpCaret, a, b))
let private neg a = syn (ExprUnaryOp (OpNeg, a))
let private call name args = syn (ExprApp (v name, args))

/// d/du of intrinsic(u), as a function of the FORWARD expression u.
/// Returns None for zero-derivative intrinsics (floor/ceil).
let private derivRule (name: string) (u: Expr) : Expr option =
    match name with
    | "exp" -> Some (call "exp" [u])
    | "log" -> Some (div (fLit 1.0) u)
    | "sqrt" -> Some (div (fLit 1.0) (mul (fLit 2.0) (call "sqrt" [u])))
    | "sin" -> Some (call "cos" [u])
    | "cos" -> Some (neg (call "sin" [u]))
    | "tan" -> Some (div (fLit 1.0) (mul (call "cos" [u]) (call "cos" [u])))
    | "sinh" -> Some (call "cosh" [u])
    | "cosh" -> Some (call "sinh" [u])
    | "tanh" -> Some (sub (fLit 1.0) (mul (call "tanh" [u]) (call "tanh" [u])))
    | "asin" -> Some (div (fLit 1.0) (call "sqrt" [sub (fLit 1.0) (mul u u)]))
    | "acos" -> Some (neg (div (fLit 1.0) (call "sqrt" [sub (fLit 1.0) (mul u u)])))
    | "atan" -> Some (div (fLit 1.0) (add (fLit 1.0) (mul u u)))
    | "floor" | "ceil" -> None
    | _ -> None

// ============================================================================
// Normalized statement model
// ============================================================================

/// The statement fragment the transform reasons over, after unwrapping
/// spans and desugaring. Assignments keep their surface Expr lhs (a plain
/// var or an element application).
type private NStmt =
    | NLet of name: string * isMut: bool * value: Expr
    | NAssign of lhs: Expr * rhs: Expr
    | NFor of var: string * lo: Expr * hi: Expr * body: NStmt list

type private Ctx = {
    /// Same-module user function declarations by name.
    Decls: Map<string, FunctionDecl>
    /// Fresh-suffix counter (shared across one expand run).
    mutable Fresh: int
}

let private fresh (ctx: Ctx) (prefix: string) : string =
    ctx.Fresh <- ctx.Fresh + 1
    sprintf "%s%d" prefix ctx.Fresh

let private err (fname: string) (msg: string) : Result<'a, string> =
    Error (sprintf "grad(%s): %s" fname msg)

// ============================================================================
// Body -> NStmt conversion
// ============================================================================

let rec private convertStmts (fname: string) (stmts: Stmt list) : Result<NStmt list, string> =
    let folder acc stmt =
        acc |> Result.bind (fun converted ->
            match unwrapStmt stmt with
            | StmtSpanned _ -> err fname "internal: unwrapStmt left a span"
            | StmtLet binding ->
                match binding.Pattern.Kind with
                | PatternKind.PatVar name ->
                    let isMut = (binding.Mutability = BindMut)
                    Ok (converted @ [NLet (name, isMut, binding.Value)])
                | _ -> err fname "tuple/struct patterns in let are not differentiable (v1); bind names individually"
            | StmtExpr { Kind = ExprKind.ExprAssign (lhs, rhs) } ->
                Ok (converted @ [NAssign (lhs, rhs)])
            | StmtAssign (lhs, op, rhs) ->
                // Defensive: the parser emits ExprAssign, but normalize
                // StmtAssign if one arrives.
                let rhs' =
                    match op with
                    | AssignEq -> rhs
                    | AssignAdd -> add lhs rhs
                    | AssignSub -> sub lhs rhs
                    | AssignMul -> mul lhs rhs
                    | AssignDiv -> div lhs rhs
                Ok (converted @ [NAssign (lhs, rhs')])
            | StmtExpr _ ->
                err fname "bare expression statements are not supported in differentiated code"
            | StmtForIn (var, { Kind = ExprKind.ExprDotDot (lo, hi) }, body) ->
                convertStmts fname body |> Result.map (fun nbody ->
                    converted @ [NFor (var, lo, hi, nbody)])
            | StmtForIn _ ->
                err fname "for-in ranges must use the a..b form in differentiated code")
    stmts |> List.fold folder (Ok [])

/// A function body is either a block or a bare expression.
let private convertBody (fname: string) (body: Expr) : Result<NStmt list * Expr, string> =
    match body.Kind with
    | ExprKind.ExprBlock (stmts, Some finalE) ->
        convertStmts fname stmts |> Result.map (fun ns -> (ns, finalE))
    | ExprKind.ExprBlock (_, None) ->
        err fname "function body has no final expression (must return a Float)"
    | _ -> Ok ([], body)

// ============================================================================
// Expression validation + variable collection over the AD-able fragment
// ============================================================================

/// The constant-fill array constructor `replicate(N, pure(lit)) |> compute`
/// — the idiomatic replacement for hand-written N-element zero literals.
/// Combinators are otherwise rejected in differentiated code (v1), but a
/// literal fill computes nothing and reads nothing, so it is admitted as
/// an array-literal equivalent wherever ExprArrayLit initializers are.
/// Captures (count expr, fill literal).
let private (|ConstFill|_|) (e: Expr) =
    match e.Kind with
    | ExprKind.ExprCompute { Kind = ExprKind.ExprReplicate (cnt, { Kind = ExprKind.ExprPure { Kind = ExprKind.ExprLit lit } }) } -> Some (cnt, lit)
    | _ -> None

/// A ConstFill of the same count with the fill value zeroed.
let private zeroFill (cnt: Expr) : Expr =
    let re k = inheritSpan cnt k
    re (ExprCompute (re (ExprReplicate (cnt, re (ExprPure (re (ExprLit (LitFloat 0.0))))))))

/// Walk an expression, validating it stays inside the differentiable
/// fragment, and call `onVar` for every variable REFERENCE (not index
/// positions — those are int-typed and non-differentiable, but we still
/// visit them for taint bookkeeping of index vars; harmless).
let rec private walkExpr (fname: string) (ctx: Ctx) (onVar: string -> unit) (e: Expr) : Result<unit, string> =
    match e with
    | { Kind = ExprKind.ExprLit _ } -> Ok ()
    | { Kind = ExprKind.ExprVar name } -> onVar name; Ok ()
    | { Kind = ExprKind.ExprTyped (inner, _) } -> walkExpr fname ctx onVar inner
    | { Kind = ExprKind.ExprUnaryOp (OpNeg, inner) } -> walkExpr fname ctx onVar inner
    | { Kind = ExprKind.ExprUnaryOp (OpNot, inner) } -> walkExpr fname ctx onVar inner
    | { Kind = ExprKind.ExprUnaryOp _ } -> err fname "unsupported unary operator in differentiated code"
    | { Kind = ExprKind.ExprBinOp (_, _, l, r) } ->
        walkExpr fname ctx onVar l |> Result.bind (fun () -> walkExpr fname ctx onVar r)
    | { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar name }, args) } ->
        // intrinsic, user call (inlined earlier), or array read — all fine
        // structurally; recurse into arguments.
        args |> List.fold (fun acc a -> acc |> Result.bind (fun () -> walkExpr fname ctx onVar a))
                          (Ok (onVar name))
    | { Kind = ExprKind.ExprApp _ } -> err fname "only named calls and array reads are supported in differentiated code"
    | { Kind = ExprKind.ExprArrayLit elems } ->
        elems |> List.fold (fun acc a -> acc |> Result.bind (fun () -> walkExpr fname ctx onVar a)) (Ok ())
    | { Kind = ExprKind.ExprIf _ } -> err fname "if/else is not supported in differentiated code yet"
    | { Kind = ExprKind.ExprMatch _ } -> err fname "match is not supported in differentiated code"
    | { Kind = ExprKind.ExprBlock _ } -> err fname "nested block expressions are not supported in differentiated code"
    | { Kind = ExprKind.ExprLet _ } -> err fname "expression-level let is not supported in differentiated code"
    | ConstFill _ -> Ok ()   // literal fill: computes nothing, reads nothing
    | { Kind = ExprKind.ExprReduce _ } ->
        err fname "differentiable code supports straight-line arithmetic, additive `reduce`, and rank-1 recursive arrays (v1); this reduce could not be normalized (only `reduce(A, (+)[, init])` over an array variable or inline literal is differentiable)"
    | { Kind = ExprKind.ExprRecArray _ } ->
        err fname "differentiable code supports straight-line arithmetic, additive `reduce`, and rank-1 recursive arrays (v1); a recursive array is differentiable only as a top-level `let rec` binding in the body"
    | { Kind = ExprKind.ExprLambda _ } | { Kind = ExprKind.ExprMethodFor _ } | { Kind = ExprKind.ExprObjectFor _ } | { Kind = ExprKind.ExprCompute _ } | { Kind = ExprKind.ExprPure _ } ->
        err fname "differentiable code supports straight-line arithmetic, additive `reduce`, and rank-1 recursive arrays (v1); loop-object combinators (lambda/method_for/object_for/compute/pure) are not differentiable"
    | { Kind = ExprKind.ExprTuple _ } -> err fname "tuple values are not supported in differentiated code"
    | { Kind = ExprKind.ExprField _ } -> err fname "struct field access is not supported in differentiated code"
    | _ -> err fname "unsupported expression form in differentiated code"

// ============================================================================
// Renaming (for inlining)
// ============================================================================

/// Rename variable references and binders per `ren` (total map application:
/// names not in the map pass through). Only walks the AD-able fragment plus
/// the statement forms; used on ALREADY-VALIDATED callee bodies.
let rec private renameExpr (ren: Map<string, string>) (e: Expr) : Expr =
    let rn n = Map.tryFind n ren |> Option.defaultValue n
    let re k = inheritSpan e k
    match e.Kind with
    | ExprKind.ExprLit _ -> e
    | ExprKind.ExprVar name -> re (ExprVar (rn name))
    | ExprKind.ExprTyped (inner, t) -> re (ExprTyped (renameExpr ren inner, t))
    | ExprKind.ExprUnaryOp (op, inner) -> re (ExprUnaryOp (op, renameExpr ren inner))
    | ExprKind.ExprBinOp (m, op, l, r) -> re (ExprBinOp (m, op, renameExpr ren l, renameExpr ren r))
    | ExprKind.ExprApp (f, args) -> re (ExprApp (renameExpr ren f, args |> List.map (renameExpr ren)))
    | ExprKind.ExprArrayLit elems -> re (ExprArrayLit (elems |> List.map (renameExpr ren)))
    | ExprKind.ExprAssign (l, r) -> re (ExprAssign (renameExpr ren l, renameExpr ren r))
    | ExprKind.ExprDotDot (l, h) -> re (ExprDotDot (renameExpr ren l, renameExpr ren h))
    // constant-fill constructors ride through inlined callee bodies; the
    // count may reference renamed statics-in-scope, so rename inside
    | ExprKind.ExprCompute inner -> re (ExprCompute (renameExpr ren inner))
    | ExprKind.ExprReplicate (c, b) -> re (ExprReplicate (renameExpr ren c, renameExpr ren b))
    | ExprKind.ExprPure inner -> re (ExprPure (renameExpr ren inner))
    | _ -> e

let rec private renameNStmts (ren: Map<string, string>) (stmts: NStmt list) : NStmt list =
    stmts |> List.map (fun s ->
        match s with
        | NLet (n, m, e) ->
            let n' = Map.tryFind n ren |> Option.defaultValue n
            NLet (n', m, renameExpr ren e)
        | NAssign (l, r) -> NAssign (renameExpr ren l, renameExpr ren r)
        | NFor (var, lo, hi, body) ->
            let var' = Map.tryFind var ren |> Option.defaultValue var
            NFor (var', renameExpr ren lo, renameExpr ren hi, renameNStmts ren body))

/// All names BOUND anywhere in a statement list (lets + loop vars).
let rec private boundNames (stmts: NStmt list) : string list =
    stmts |> List.collect (fun s ->
        match s with
        | NLet (n, _, _) -> [n]
        | NAssign _ -> []
        | NFor (var, _, _, body) -> var :: boundNames body)

// ============================================================================
// ANF + inlining
// ============================================================================

/// Hoist user-function calls out of nested expression positions into
/// preceding lets, so calls only appear as direct `let x = f(args)` values.
/// Intrinsics and array reads stay in place.
let rec private hoistCalls (fname: string) (ctx: Ctx) (e: Expr) : Result<NStmt list * Expr, string> =
    let recurse = hoistCalls fname ctx
    let re k = inheritSpan e k
    match e.Kind with
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar name }, args) when Map.containsKey name ctx.Decls ->
        // hoist arguments first (post-order), then this call
        let folded =
            args |> List.fold (fun acc a ->
                acc |> Result.bind (fun (stmts, args') ->
                    recurse a |> Result.map (fun (s, a') -> (stmts @ s, args' @ [a']))))
                (Ok ([], []))
        folded |> Result.map (fun (stmts, args') ->
            let tmp = fresh ctx "__t"
            (stmts @ [NLet (tmp, false, re (ExprApp (re (ExprVar name), args')))], re (ExprVar tmp)))
    | ExprKind.ExprApp (f, args) ->
        let folded =
            args |> List.fold (fun acc a ->
                acc |> Result.bind (fun (stmts, args') ->
                    recurse a |> Result.map (fun (s, a') -> (stmts @ s, args' @ [a']))))
                (Ok ([], []))
        folded |> Result.map (fun (stmts, args') -> (stmts, re (ExprApp (f, args'))))
    | ExprKind.ExprBinOp (m, op, l, r) ->
        recurse l |> Result.bind (fun (sl, l') ->
        recurse r |> Result.map (fun (sr, r') -> (sl @ sr, re (ExprBinOp (m, op, l', r')))))
    | ExprKind.ExprUnaryOp (op, inner) ->
        recurse inner |> Result.map (fun (s, i') -> (s, re (ExprUnaryOp (op, i'))))
    | ExprKind.ExprTyped (inner, t) ->
        recurse inner |> Result.map (fun (s, i') -> (s, re (ExprTyped (i', t))))
    | ExprKind.ExprArrayLit elems ->
        let folded =
            elems |> List.fold (fun acc a ->
                acc |> Result.bind (fun (stmts, es) ->
                    recurse a |> Result.map (fun (s, e') -> (stmts @ s, es @ [e']))))
                (Ok ([], []))
        folded |> Result.map (fun (stmts, es) -> (stmts, re (ExprArrayLit es)))
    | _ -> Ok ([], e)

// ============================================================================
// Pre-normalization of the imperative-free surface constructs
// ============================================================================
//
// `for x in a..b {}` is being retired from the surface language; its
// replacements (recursive arrays, `reduce`) reach grad as ExprRecArray /
// ExprReduce nodes the NFor pipeline never learned to read. This pass is a
// SOURCE-TO-SOURCE rewrite over each differentiated function body that lowers
// those forms into the ACCUMULATION and CONSTRUCTION statement shapes grad
// already differentiates — additive-only, keeping the BL5500 discipline. It
// runs BEFORE convertStmts, so downstream sees only familiar for-in loops,
// element writes, and additive `+=`.

/// Float-ness of a type annotation (mirrors isFloatTy, needed earlier).
let private preIsFloatTy (t: TypeExpr) : bool =
    match t with
    | TyFloat64 | TyFloat32 -> true
    | TyNamed (("Float" | "Float64" | "Float32"), []) -> true
    | _ -> false

/// Read the static per-axis extents of an `Array<Float like Idx<n>, ...>`
/// annotation. Returns (elem-is-Float, extents) when every axis is a literal
/// `Idx<n>`; None otherwise (non-literal extents / non-array type). v1 admits
/// literal extents only — named/derived index extents are rejected upstream.
let private arrayLiteralExtents (t: TypeExpr) : (bool * int list) option =
    match t with
    | TyArray (elem, idxs) ->
        let exts =
            idxs |> List.map (fun ix ->
                match ix with
                | TyIdx { Kind = ExprKind.ExprLit (LitInt n) } -> Some (int n)
                | _ -> None)
        if not idxs.IsEmpty && exts |> List.forall Option.isSome then
            Some (preIsFloatTy elem, exts |> List.map Option.get)
        else None
    | _ -> None

/// Does `e` reference the variable `name` anywhere (over the arithmetic +
/// array-read fragment recursive-array slices live in)?
let rec private mentionsVar (name: string) (e: Expr) : bool =
    match e.Kind with
    | ExprKind.ExprVar n -> n = name
    | ExprKind.ExprLit _ -> false
    | ExprKind.ExprTyped (inner, _) | ExprKind.ExprUnaryOp (_, inner) -> mentionsVar name inner
    | ExprKind.ExprBinOp (_, _, l, r) | ExprKind.ExprDotDot (l, r) -> mentionsVar name l || mentionsVar name r
    | ExprKind.ExprApp (f, args) -> mentionsVar name f || List.exists (mentionsVar name) args
    | ExprKind.ExprArrayLit elems -> List.exists (mentionsVar name) elems
    | _ -> false

/// Substitute free references to `name` with the expression `repl` over the
/// same fragment (used to bind a recurrence's step ordinal / prefix name).
let rec private substVar (name: string) (repl: Expr) (e: Expr) : Expr =
    let re k = inheritSpan e k
    match e.Kind with
    | ExprKind.ExprVar n when n = name -> repl
    | ExprKind.ExprVar _ | ExprKind.ExprLit _ -> e
    | ExprKind.ExprTyped (inner, t) -> re (ExprTyped (substVar name repl inner, t))
    | ExprKind.ExprUnaryOp (op, inner) -> re (ExprUnaryOp (op, substVar name repl inner))
    | ExprKind.ExprBinOp (m, op, l, r) -> re (ExprBinOp (m, op, substVar name repl l, substVar name repl r))
    | ExprKind.ExprApp (f, args) -> re (ExprApp (substVar name repl f, args |> List.map (substVar name repl)))
    | ExprKind.ExprArrayLit elems -> re (ExprArrayLit (elems |> List.map (substVar name repl)))
    | ExprKind.ExprDotDot (l, h) -> re (ExprDotDot (substVar name repl l, substVar name repl h))
    | _ -> e

/// Expand a rank-1 recursive-array `let` into the buffer + element-write /
/// accumulation statements the NFor differentiation pipeline handles.
/// Returns the emitted statements and the buffer's leading extent (for the
/// ambient reduce-source extent env). The bound NAME is reused as the buffer
/// so downstream reads of it keep resolving.
///
///   * additive prefix recurrence `prefix :: prefix(n-1) + INC` (with a
///     `zero :: n` seed arm, INC prefix-free) -> a TRIANGULAR accumulation
///     `s(k) += INC[n:=m]` over `m in 1..k+1`. A same-direction adjoint loop
///     is wrong for a genuine scan, so the scan is unrolled into independent
///     scatter-adds — semantically identical, and exactly differentiable.
///   * prefix-free construction `prefix :: f(n)` -> direct element writes.
///   * anything else (nonlinear recurrence, rank >= 2, no seed) is rejected.
let private expandRecArray (fname: string) (ctx: Ctx)
                           (name: string) (annot: TypeExpr) (def: RecArrayDef)
    : Result<Stmt list * int, string> =
    match arrayLiteralExtents annot with
    | None ->
        err fname (sprintf "recursive array '%s': a differentiable recursive array needs an `Array<Float like Idx<n>>` annotation with a literal extent (v1)" name)
    | Some (false, _) ->
        err fname (sprintf "recursive array '%s': only Float-element recursive arrays are differentiable (v1)" name)
    | Some (true, [n]) ->
        let bufName = name
        let bufVar = syn (ExprVar bufName)
        let sAt (idxE: Expr) = syn (ExprApp (bufVar, [idxE]))
        let zeros = syn (ExprArrayLit (List.replicate n (fLit 0.0)))
        let bufLet = StmtLet { Mutability = BindMut; Pattern = synPat (PatVar bufName); Type = None; Value = zeros }
        let stepVar = def.StepVar
        let prefixVar = def.PrefixVar
        // `x` is exactly the immediate-predecessor read `prefix(stepVar - 1)`?
        let isPrevPrefixRead (x: Expr) =
            match x.Kind with
            | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar p }, [idx]) when p = prefixVar ->
                (match idx.Kind with
                 | ExprKind.ExprBinOp (_, OpSub, { Kind = ExprKind.ExprVar s }, { Kind = ExprKind.ExprLit (LitInt 1L) }) -> s = stepVar
                 | _ -> false)
            | _ -> false
        let hasPrefix (e: Expr) = mentionsVar prefixVar e
        // additive-prefix slice `prefix(n-1) + REST` / `REST + prefix(n-1)`,
        // REST prefix-free -> Some REST (the per-step increment).
        let additiveRest =
            match def.SliceExpr.Kind with
            | ExprKind.ExprBinOp (_, OpAdd, a, b) when isPrevPrefixRead a && not (hasPrefix b) -> Some b
            | ExprKind.ExprBinOp (_, OpAdd, a, b) when isPrevPrefixRead b && not (hasPrefix a) -> Some a
            | _ -> None
        match additiveRest, def.SeedArm with
        | Some rest, Some (seedStep, seedExpr) ->
            let seeded = substVar seedStep (iLit 0L) seedExpr
            let isZeroSeed = (match seeded.Kind with ExprKind.ExprLit (LitFloat 0.0) -> true | _ -> false)
            let kVar = fresh ctx "__rk"
            let mVar = fresh ctx "__rm"
            let restM = substVar stepVar (v mVar) rest
            let innerLoop =
                StmtForIn (mVar,
                           syn (ExprDotDot (iLit 1L, add (v kVar) (iLit 1L))),
                           [ StmtExpr (syn (ExprAssign (sAt (v kVar), add (sAt (v kVar)) restM))) ])
            let seedCarry =
                if isZeroSeed then []
                else [ StmtExpr (syn (ExprAssign (sAt (v kVar), add (sAt (v kVar)) seeded))) ]
            let outerLoop =
                StmtForIn (kVar, syn (ExprDotDot (iLit 1L, iLit (int64 n))), seedCarry @ [innerLoop])
            let seedWrite =
                if isZeroSeed then []
                else [ StmtExpr (syn (ExprAssign (sAt (iLit 0L), seeded))) ]
            Ok (bufLet :: (seedWrite @ [outerLoop]), n)
        | None, _ when not (hasPrefix def.SliceExpr) ->
            // Pure construction (no carried state): direct element writes.
            let loopStart, seedStmts =
                match def.SeedArm with
                | Some (seedStep, seedExpr) ->
                    1L, [ StmtExpr (syn (ExprAssign (sAt (iLit 0L), substVar seedStep (iLit 0L) seedExpr))) ]
                | None -> 0L, []
            let loop =
                StmtForIn (stepVar,
                           syn (ExprDotDot (iLit loopStart, iLit (int64 n))),
                           [ StmtExpr (syn (ExprAssign (sAt (v stepVar), def.SliceExpr))) ])
            Ok (bufLet :: (seedStmts @ [loop]), n)
        | _ ->
            err fname (sprintf "recursive array '%s' is not differentiable (v1): only additive prefix recurrences `prefix :: prefix(n-1) + <increment>` (with a `zero :: n` seed arm and a prefix-free increment) and prefix-free construction are supported" name)
    | Some (true, exts) ->
        err fname (sprintf "recursive array '%s': only rank-1 (scalar-slice) recursive arrays are differentiable (v1); a rank-%d recursive array is not supported" name exts.Length)

/// Rewrite additive `reduce(SRC, (+)[, init])` occurrences inside `e` into an
/// accumulator loop (returned as a prefix of statements) plus a reference to
/// the accumulator. Reject non-additive kernels and non-array-literal /
/// non-variable sources (deferred/former reductions) — the additive subset
/// matching grad v1. `extents` maps in-scope array names to their extents.
let rec private hoistReduces (fname: string) (ctx: Ctx) (extents: Map<string, int>) (e: Expr)
    : Result<Stmt list * Expr, string> =
    let re k = inheritSpan e k
    let recurse = hoistReduces fname ctx extents
    match e.Kind with
    | ExprKind.ExprReduce (src, kernel, initOpt) ->
        (match kernel.Kind with
         | ExprKind.ExprSection OpAdd -> Ok ()
         | ExprKind.ExprSection _ -> err fname "reduce in differentiated code supports only the additive kernel `(+)` (v1)"
         | _ -> err fname "reduce in differentiated code supports only the additive kernel `(+)`; lambda and product kernels are not differentiable (v1)")
        |> Result.bind (fun () ->
        recurse src |> Result.bind (fun (srcPre, src') ->
        let initE = match initOpt with Some ie -> ie | None -> fLit 0.0
        let accName = fresh ctx "__red"
        let accLet = StmtLet { Mutability = BindMut; Pattern = synPat (PatVar accName); Type = None; Value = initE }
        match src'.Kind with
        | ExprKind.ExprArrayLit elems ->
            // Inline literal: UNROLL to per-element scalar accumulations. A
            // read-loop over a bound literal would lose the cotangent — grad
            // treats array-literal lets as constant inits (nothing flows back
            // to the elements) — so keep each active element in scalar
            // accumulation position, which grad differentiates exactly.
            let adds =
                elems |> List.map (fun el ->
                    StmtExpr (syn (ExprAssign (v accName, add (v accName) el))))
            Ok (srcPre @ [accLet] @ adds, v accName)
        | ExprKind.ExprVar nm ->
            (match Map.tryFind nm extents with
             | Some cnt ->
                 let kVar = fresh ctx "__rik"
                 let readK = syn (ExprApp (syn (ExprVar nm), [v kVar]))
                 let loop =
                     StmtForIn (kVar,
                                syn (ExprDotDot (iLit 0L, iLit (int64 cnt))),
                                [ StmtExpr (syn (ExprAssign (v accName, add (v accName) readK))) ])
                 Ok (srcPre @ [accLet; loop], v accName)
             | None -> err fname (sprintf "reduce source '%s' has no statically-known extent in differentiated code; reduce over a param/let array with an `Idx<n>` extent or over an inline array literal (v1)" nm))
        | _ -> err fname "reduce in differentiated code requires an array-variable or inline-array-literal source; deferred/former reductions are not differentiable (v1)"))
    | ExprKind.ExprBinOp (m, op, l, r) ->
        recurse l |> Result.bind (fun (pl, l') ->
        recurse r |> Result.map (fun (pr, r') -> (pl @ pr, re (ExprBinOp (m, op, l', r')))))
    | ExprKind.ExprUnaryOp (op, inner) ->
        recurse inner |> Result.map (fun (p, i') -> (p, re (ExprUnaryOp (op, i'))))
    | ExprKind.ExprTyped (inner, t) ->
        recurse inner |> Result.map (fun (p, i') -> (p, re (ExprTyped (i', t))))
    | ExprKind.ExprAssign (l, r) ->
        recurse r |> Result.map (fun (pr, r') -> (pr, re (ExprAssign (l, r'))))
    | ExprKind.ExprApp (f, args) ->
        args |> List.fold (fun acc a ->
            acc |> Result.bind (fun (ps, args') ->
                recurse a |> Result.map (fun (p, a') -> (ps @ p, args' @ [a']))))
            (Ok ([], []))
        |> Result.map (fun (ps, args') -> (ps, re (ExprApp (f, args'))))
    | ExprKind.ExprArrayLit elems ->
        elems |> List.fold (fun acc a ->
            acc |> Result.bind (fun (ps, es) ->
                recurse a |> Result.map (fun (p, a') -> (ps @ p, es @ [a']))))
            (Ok ([], []))
        |> Result.map (fun (ps, es) -> (ps, re (ExprArrayLit es)))
    | _ -> Ok ([], e)

/// The pre-pass proper: rewrite one function body's statements, expanding
/// recursive-array lets and hoisting reduces, threading an extent env so
/// reduce sources can recover their loop bound.
let private preNormalizeBody (fname: string) (ctx: Ctx) (fd: FunctionDecl) : Result<Expr, string> =
    let paramExtents =
        fd.Params |> List.choose (fun p ->
            match p.Type with
            | Some t -> (match arrayLiteralExtents t with Some (true, [n]) -> Some (p.Name, n) | _ -> None)
            | None -> None)
        |> Map.ofList
    let stmts0, finalOpt =
        match fd.Body.Kind with
        | ExprKind.ExprBlock (ss, fe) -> ss, fe
        | _ -> [], Some fd.Body
    let rec goStmts (env: Map<string, int>) (ss: Stmt list) : Result<Map<string, int> * Stmt list, string> =
        ss |> List.fold (fun acc s ->
            acc |> Result.bind (fun (env, outp) ->
                match unwrapStmt s with
                | StmtLet { Value = { Kind = ExprKind.ExprRecArray def }; Type = Some annot; Pattern = { Kind = PatternKind.PatVar nm } } ->
                    expandRecArray fname ctx nm annot def
                    |> Result.map (fun (emitted, ext) -> (Map.add nm ext env, outp @ emitted))
                | StmtLet { Value = { Kind = ExprKind.ExprRecArray _ } } ->
                    err fname "recursive array must bind a single annotated name to be differentiable (v1)"
                | StmtLet ({ Pattern = { Kind = PatternKind.PatVar nm } } as b) ->
                    hoistReduces fname ctx env b.Value |> Result.map (fun (pre, value') ->
                        let env' =
                            let byAnn =
                                match b.Type with
                                | Some t -> (match arrayLiteralExtents t with Some (true, [n]) -> Some n | _ -> None)
                                | None -> None
                            let byLit =
                                match value'.Kind with
                                | ExprKind.ExprArrayLit elems -> Some elems.Length
                                | _ -> None
                            match (match byAnn with Some _ -> byAnn | None -> byLit) with
                            | Some cnt -> Map.add nm cnt env
                            | None -> env
                        (env', outp @ pre @ [StmtLet { b with Value = value' }]))
                | StmtLet b ->
                    hoistReduces fname ctx env b.Value |> Result.map (fun (pre, value') ->
                        (env, outp @ pre @ [StmtLet { b with Value = value' }]))
                | StmtExpr ex ->
                    hoistReduces fname ctx env ex |> Result.map (fun (pre, ex') ->
                        (env, outp @ pre @ [StmtExpr ex']))
                | StmtAssign (lhs, op, rhs) ->
                    hoistReduces fname ctx env rhs |> Result.map (fun (pre, rhs') ->
                        (env, outp @ pre @ [StmtAssign (lhs, op, rhs')]))
                | StmtForIn (var, range, body) ->
                    goStmts env body |> Result.map (fun (_, body') ->
                        (env, outp @ [StmtForIn (var, range, body')]))
                | StmtSpanned _ -> Ok (env, outp @ [s])))
            (Ok (env, []))
    goStmts paramExtents stmts0 |> Result.bind (fun (env, stmts') ->
        match finalOpt with
        | Some fe ->
            hoistReduces fname ctx env fe |> Result.map (fun (pre, fe') ->
                inheritSpan fd.Body (ExprBlock (stmts' @ pre, Some fe')))
        | None ->
            Ok (inheritSpan fd.Body (ExprBlock (stmts', None))))

/// Normalize + inline a function body to the flat NStmt fragment:
/// all user calls inlined, all statements validated.
let rec private normalizeBody (fname: string) (ctx: Ctx) (depth: int) (fd: FunctionDecl)
    : Result<NStmt list * Expr, string> =
    if depth > 32 then
        err fname "call inlining exceeded depth 32 (recursive functions are not differentiable)"
    else
    // Lower the imperative-free surface constructs (recursive arrays, reduce)
    // into accumulation/construction statements before the NFor pipeline runs.
    preNormalizeBody fname ctx fd |> Result.bind (fun body' ->
    convertBody fname body' |> Result.bind (fun (stmts, finalE) ->
    // hoist calls inside the final expression too
    hoistCalls fname ctx finalE |> Result.bind (fun (finalHoist, finalE') ->
    let rec normStmts (ss: NStmt list) : Result<NStmt list, string> =
        ss |> List.fold (fun acc s ->
            acc |> Result.bind (fun outStmts ->
                match s with
                // DIRECT user-call let: the call is already in inlinable
                // position — hoist only inside its ARGUMENTS, then inline.
                // (Hoisting the call itself would create `let tmp = f(..)`
                // and re-normalizing that let would hoist again, forever.)
                | NLet (name, isMut, { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar callee }, args) }) when Map.containsKey callee ctx.Decls ->
                    let argsFolded =
                        args |> List.fold (fun acc2 a ->
                            acc2 |> Result.bind (fun (stmts, args') ->
                                hoistCalls fname ctx a
                                |> Result.map (fun (s2, a') -> (stmts @ s2, args' @ [a']))))
                            (Ok ([], []))
                    argsFolded |> Result.bind (fun (argHoists, args') ->
                    normStmts argHoists |> Result.bind (fun argHoists' ->
                    inlineCall fname ctx depth callee args' name isMut
                    |> Result.map (fun inlined -> outStmts @ argHoists' @ inlined)))
                | NLet (name, isMut, value) ->
                    hoistCalls fname ctx value |> Result.bind (fun (hoisted, value') ->
                    // `hoisted` contains only direct-call lets, which the
                    // arm above inlines without further hoisting.
                    normStmts hoisted |> Result.map (fun hoisted' ->
                        outStmts @ hoisted' @ [NLet (name, isMut, value')]))
                | NAssign (lhs, rhs) ->
                    hoistCalls fname ctx rhs |> Result.bind (fun (hoisted, rhs') ->
                    normStmts hoisted |> Result.map (fun hoisted' ->
                        outStmts @ hoisted' @ [NAssign (lhs, rhs')]))
                | NFor (var, lo, hi, body) ->
                    normStmts body |> Result.map (fun body' ->
                        outStmts @ [NFor (var, lo, hi, body')])))
            (Ok [])
    normStmts (stmts @ finalHoist) |> Result.map (fun ns -> (ns, finalE')))))

/// Inline `let target = callee(args)`: bind arguments to fresh param names,
/// splice the callee's own normalized body with all its binders renamed,
/// and bind the callee's final expression to `target` (or rename in place
/// when the final expression is just a local variable — avoiding an array
/// alias, which the adjoint bookkeeping cannot track).
and private inlineCall (fname: string) (ctx: Ctx) (depth: int)
                       (callee: string) (args: Expr list)
                       (target: string) (targetMut: bool)
    : Result<NStmt list, string> =
    let fd = ctx.Decls.[callee]
    if fd.IsStatic then err fname (sprintf "cannot differentiate through static function '%s'" callee)
    elif fd.Params |> List.exists (fun p -> p.Mutability = Mutable) then
        err fname (sprintf "cannot differentiate through '%s': mut-parameter functions are not inlinable (v1)" callee)
    elif args.Length <> fd.Params.Length then
        err fname (sprintf "'%s' called with %d arguments, expects %d" callee args.Length fd.Params.Length)
    else
    normalizeBody fname ctx (depth + 1) fd |> Result.bind (fun (calleeStmts, calleeFinal) ->
        let tag = fresh ctx "__in"
        // Param binding: plain-var arguments bind by RENAMING the callee
        // param to the caller's variable (no let) — this is what routes
        // array cotangents straight to the caller's d-buffers and avoids
        // array-alias lets, which the adjoint bookkeeping rejects.
        // Non-var arguments (scalar expressions) bind through a fresh let.
        let paramBinds =
            List.zip fd.Params args
            |> List.map (fun (p, a) ->
                match a.Kind with
                | ExprKind.ExprVar argName -> (p.Name, argName, None)
                | _ -> (p.Name, sprintf "%s_%s" tag p.Name, Some a))
        let paramRen =
            paramBinds |> List.map (fun (pn, target, _) -> (pn, target)) |> Map.ofList
        let localRen =
            boundNames calleeStmts
            |> List.distinct
            |> List.map (fun n -> (n, sprintf "%s_%s" tag n))
            |> Map.ofList
        let ren = Map.fold (fun acc k v -> Map.add k v acc) paramRen localRen
        let renStmts = renameNStmts ren calleeStmts
        let renFinal = renameExpr ren calleeFinal
        let paramLets =
            paramBinds |> List.choose (fun (_, target, argOpt) ->
                argOpt |> Option.map (fun a -> NLet (target, false, a)))
        match renFinal.Kind with
        | ExprKind.ExprVar localName when (localRen |> Map.exists (fun _ v2 -> v2 = localName)) ->
            // Final expr is a callee-local: rename that local to the target
            // name instead of emitting `let target = local` (array-alias).
            let ren2 = Map.ofList [(localName, target)]
            Ok (paramLets @ renameNStmts ren2 renStmts)
        | _ ->
            Ok (paramLets @ renStmts @ [NLet (target, targetMut, renFinal)]))

// ============================================================================
// Classification: differentiable variables, array-ness
// ============================================================================

let private isFloatTy (t: TypeExpr) : bool =
    match t with
    | TyFloat64 | TyFloat32 -> true
    | TyNamed (("Float" | "Float64" | "Float32"), []) -> true
    | _ -> false

type private ParamClass =
    | DiffArray
    | DiffScalar
    | NonDiff

let private classifyParam (fname: string) (p: ParamDecl) : Result<ParamClass, string> =
    match p.Type with
    | None -> err fname (sprintf "parameter '%s' must have a type annotation" p.Name)
    | Some t when isFloatTy t -> Ok DiffScalar
    | Some (TyArray (elem, _)) when isFloatTy elem -> Ok DiffArray
    | Some _ -> Ok NonDiff

/// Zero value matching an array literal's (or constant fill's) shape.
let rec private zerosLikeLiteral (e: Expr) : Expr option =
    match e with
    | { Kind = ExprKind.ExprArrayLit elems } ->
        let zs = elems |> List.map (fun el ->
            match zerosLikeLiteral el with
            | Some z -> z
            | None -> fLit 0.0)
        Some (inheritSpan e (ExprArrayLit zs))
    | ConstFill (cnt, _) -> Some (zeroFill cnt)
    | _ -> None

/// Zero value for a differentiable param's declared type (arrays need
/// literal extents in v1 — callers allocate those buffers, so this is only
/// used for LOCAL d-vars, which come from literals instead).
let private zerosOfType (fname: string) (t: TypeExpr) : Result<Expr, string> =
    let rec go t =
        match t with
        | _ when isFloatTy t -> Ok (fLit 0.0)
        | TyArray (elem, idxs) ->
            let extents =
                idxs |> List.map (fun ix ->
                    match ix with
                    | TyIdx { Kind = ExprKind.ExprLit (LitInt n) } -> Ok (int n)
                    | _ -> err fname "differentiable arrays need literal Idx<n> extents (v1)")
            let folded =
                extents |> List.fold (fun acc r ->
                    acc |> Result.bind (fun ns -> r |> Result.map (fun n -> ns @ [n]))) (Ok [])
            folded |> Result.bind (fun ns ->
                go elem |> Result.map (fun z ->
                    ns |> List.rev |> List.fold (fun inner n -> syn (ExprArrayLit (List.replicate n inner))) z))
        | _ -> err fname "cannot build a zero cotangent for this type"
    go t

// ============================================================================
// Taint analysis
// ============================================================================

/// Variables carrying differentiable (Float) dataflow, and which of those
/// are arrays. Two passes for loop-carried taint.
let private analyze (fname: string) (ctx: Ctx)
                    (diffParams: Set<string>) (arrayParams: Set<string>)
                    (stmts: NStmt list) (finalE: Expr)
    : Result<Set<string> * Set<string>, string> =
    let mutable diff = diffParams
    let mutable arrays = arrayParams
    let touches (e: Expr) : Result<bool, string> =
        let mutable hit = false
        walkExpr fname ctx (fun n -> if Set.contains n diff then hit <- true) e
        |> Result.map (fun () -> hit)
    let rec pass (ss: NStmt list) : Result<unit, string> =
        ss |> List.fold (fun acc s ->
            acc |> Result.bind (fun () ->
                match s with
                | NLet (name, _, value) ->
                    (match value with
                     | { Kind = ExprKind.ExprArrayLit _ } | ConstFill _ -> arrays <- Set.add name arrays
                     | { Kind = ExprKind.ExprVar src } when Set.contains src arrays ->
                         arrays <- Set.add name arrays
                     | _ -> ())
                    // FLOAT array literals are differentiable carriers even
                    // before any diff flow reaches them (their cotangents
                    // must exist). Int-literal tables (index/offset data,
                    // e.g. ML-elaboration path tables) are not — their
                    // reads only ever appear in index and bound positions.
                    let rec isFloatLit (e: Expr) =
                        match e.Kind with
                        | ExprKind.ExprArrayLit es -> es |> List.forall isFloatLit
                        | ExprKind.ExprLit (LitFloat _) -> true
                        | _ -> false
                    touches value |> Result.map (fun t ->
                        if t then diff <- Set.add name diff
                        match value with
                        | { Kind = ExprKind.ExprArrayLit _ } when isFloatLit value -> diff <- Set.add name diff
                        | ConstFill (_, LitFloat _) -> diff <- Set.add name diff
                        | _ -> ())
                | NAssign (lhs, rhs) ->
                    touches rhs |> Result.map (fun t ->
                        if t then
                            match lhs.Kind with
                            | ExprKind.ExprVar n -> diff <- Set.add n diff
                            | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar a }, _) -> diff <- Set.add a diff
                            | _ -> ())
                | NFor (_, _, _, body) -> pass body))
            (Ok ())
    pass stmts
    |> Result.bind (fun () -> pass stmts)     // second pass: loop-carried
    |> Result.bind (fun () -> touches finalE)
    |> Result.bind (fun finalTouches ->
        if not finalTouches then
            err fname "the returned value does not depend on any differentiable parameter"
        else Ok (diff, arrays))

// ============================================================================
// Restriction checks (accumulator discipline)
// ============================================================================

/// Names assigned (either form) anywhere in a statement list.
let rec private assignedNames (stmts: NStmt list) : Set<string> =
    stmts |> List.fold (fun acc s ->
        match s with
        | NLet _ -> acc
        | NAssign ({ Kind = ExprKind.ExprVar n }, _) -> Set.add n acc
        | NAssign ({ Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar a }, _) }, _) -> Set.add a acc
        | NAssign _ -> acc
        | NFor (_, _, _, body) -> Set.union acc (assignedNames body)) Set.empty

/// `x = x + e` / `x = x - e` (and element forms) — the additive-self
/// pattern, which the same-direction adjoint loop handles exactly.
/// Returns (sign, e) when matched: +1.0 for add; -1.0 for sub.
let private additiveSelf (lhs: Expr) (rhs: Expr) : (float * Expr) option =
    let rec sameLhs (a: Expr) (b: Expr) =
        match a, b with
        | { Kind = ExprKind.ExprVar x }, { Kind = ExprKind.ExprVar y } -> x = y
        | { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar x }, ix) }, { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar y }, iy) } ->
            x = y && ix.Length = iy.Length && List.forall2 sameLhs ix iy
        | { Kind = ExprKind.ExprLit l1 }, { Kind = ExprKind.ExprLit l2 } -> l1 = l2
        | { Kind = ExprKind.ExprBinOp (_, o1, a1, b1) }, { Kind = ExprKind.ExprBinOp (_, o2, a2, b2) } ->
            o1 = o2 && sameLhs a1 a2 && sameLhs b1 b2
        | { Kind = ExprKind.ExprTyped (i1, _) }, i2 | i1, { Kind = ExprKind.ExprTyped (i2, _) } -> sameLhs i1 i2
        | _ -> false
    match rhs.Kind with
    | ExprKind.ExprBinOp (_, OpAdd, l, e) when sameLhs l lhs -> Some (1.0, e)
    | ExprKind.ExprBinOp (_, OpAdd, e, l) when sameLhs l lhs -> Some (1.0, e)
    | ExprKind.ExprBinOp (_, OpSub, l, e) when sameLhs l lhs -> Some (-1.0, e)
    | _ -> None

/// Straight-line ordering discipline: the adjoint sweep re-evaluates
/// forward expressions, which see each variable's FINAL value. That is only
/// sound when no differentiable expression reads a mutable variable that is
/// written again LATER in forward order. Whole loops count as one position
/// (intra-loop ordering is checkLoopDiscipline's job). The additive-self
/// lhs occurrence is exempt (its ∂ is 1 regardless of value).
let private checkWriteAfterRead (fname: string) (ctx: Ctx) (stmts: NStmt list) : Result<unit, string> =
    // last write position per var, loops opaque
    let lastWrite = System.Collections.Generic.Dictionary<string, int>()
    stmts |> List.iteri (fun i s ->
        let record n = lastWrite.[n] <- i
        match s with
        | NLet _ -> ()
        | NAssign ({ Kind = ExprKind.ExprVar n }, _) -> record n
        | NAssign ({ Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar a }, _) }, _) -> record a
        | NAssign _ -> ()
        | NFor (_, _, _, body) -> assignedNames body |> Set.iter record)
    let checkExprAt (i: int) (e: Expr) : Result<unit, string> =
        let mutable bad = None
        walkExpr fname ctx (fun n ->
            if bad.IsNone then
                match lastWrite.TryGetValue n with
                | true, j when j > i -> bad <- Some n
                | _ -> ()) e
        |> Result.bind (fun () ->
            match bad with
            | Some n -> err fname (sprintf "'%s' is read here but written again later; the reverse sweep re-evaluates forward expressions at FINAL values, so read-then-rewrite of a mutable is not differentiable (bind a fresh let instead)" n)
            | None -> Ok ())
    stmts |> List.mapi (fun i s -> (i, s)) |> List.fold (fun acc (i, s) ->
        acc |> Result.bind (fun () ->
            match s with
            | NLet (_, _, value) -> checkExprAt i value
            | NAssign (lhs, rhs) ->
                (match additiveSelf lhs rhs with
                 | Some (_, e) -> checkExprAt i e
                 | None -> checkExprAt i rhs)
                |> Result.bind (fun () ->
                    // index expressions of an element write
                    match lhs.Kind with
                    | ExprKind.ExprApp (_, idxs) ->
                        idxs |> List.fold (fun a ix -> a |> Result.bind (fun () -> checkExprAt i ix)) (Ok ())
                    | _ -> Ok ())
            | NFor (_, lo, hi, body) ->
                checkExprAt i lo
                |> Result.bind (fun () -> checkExprAt i hi)
                |> Result.bind (fun () ->
                    // expressions INSIDE the loop must not read vars written
                    // after the loop either
                    let rec checkBody ss =
                        ss |> List.fold (fun a s2 ->
                            a |> Result.bind (fun () ->
                                match s2 with
                                | NLet (_, _, value) -> checkExprAt i value
                                | NAssign (l2, r2) ->
                                    (match additiveSelf l2 r2 with
                                     | Some (_, e) -> checkExprAt i e
                                     | None -> checkExprAt i r2)
                                | NFor (_, l2, h2, b2) ->
                                    checkExprAt i l2
                                    |> Result.bind (fun () -> checkExprAt i h2)
                                    |> Result.bind (fun () -> checkBody b2)))
                            (Ok ())
                    checkBody body)))
        (Ok ())

/// Non-additive reassignment of a differentiable SCALAR is rejected
/// everywhere: its adjoint needs the pre-statement value, which the
/// re-evaluating reverse sweep cannot see. (Array ELEMENT writes stay legal
/// as construction; their adjoints never read the overwritten value.)
let private checkNoScalarOverwrite (fname: string) (diff: Set<string>) (stmts: NStmt list) : Result<unit, string> =
    let rec check ss =
        ss |> List.fold (fun acc s ->
            acc |> Result.bind (fun () ->
                match s with
                | NAssign (({ Kind = ExprKind.ExprVar x } as lhs), rhs) when Set.contains x diff ->
                    (match additiveSelf lhs rhs with
                     | Some _ -> Ok ()
                     | None -> err fname (sprintf "non-additive reassignment of '%s' is not differentiable (the reverse sweep sees final values); bind a fresh `let` instead" x))
                | NFor (_, _, _, body) -> check body
                | _ -> Ok ()))
            (Ok ())
    check stmts

/// Inside a loop, expressions may not READ accumulators mutated in the same
/// loop UNLESS the accumulator is DECLARED in that same loop body: the
/// adjoint loop replays the whole body, reconstructing loop-local values
/// per iteration, so reads of loop-local accumulators (e.g. a per-iteration
/// `pred` summed then consumed) are exact. Accumulators that OUTLIVE the
/// loop have unrecoverable mid-iteration values and stay read-banned. The
/// additive-self lhs occurrence is exempt.
let private checkLoopDiscipline (fname: string) (ctx: Ctx) (loops: NStmt list) : Result<unit, string> =
    let rec check (ss: NStmt list) (inLoop: bool) (loopAccums: Set<string>) : Result<unit, string> =
        ss |> List.fold (fun acc s ->
            acc |> Result.bind (fun () ->
                match s with
                | NLet (_, _, value) when inLoop ->
                    let mutable bad = None
                    walkExpr fname ctx (fun n -> if Set.contains n loopAccums && bad.IsNone then bad <- Some n) value
                    |> Result.bind (fun () ->
                        match bad with
                        | Some n -> err fname (sprintf "loop-body let reads accumulator '%s' mutated in the same loop (mid-iteration values are not recoverable; restructure)" n)
                        | None -> Ok ())
                | NLet _ -> Ok ()
                | NAssign (lhs, rhs) when inLoop ->
                    match additiveSelf lhs rhs with
                    | Some (_, e) ->
                        let mutable bad = None
                        walkExpr fname ctx (fun n -> if Set.contains n loopAccums && bad.IsNone then bad <- Some n) e
                        |> Result.bind (fun () ->
                            match bad with
                            | Some n -> err fname (sprintf "accumulation reads accumulator '%s' mutated in the same loop; restructure" n)
                            | None -> Ok ())
                    | None ->
                        match lhs.Kind with
                        | ExprKind.ExprVar x ->
                            err fname (sprintf "loop-carried reassignment of '%s' is not additive (`%s = %s ± e`); only additive accumulation is differentiable in loops (v1)" x x x)
                        | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar a }, _) ->
                            // plain element write inside a loop: allowed as
                            // construction, but the rhs may not read the
                            // array being written (array recurrence)
                            let mutable bad = false
                            walkExpr fname ctx (fun n -> if n = a then bad <- true) rhs
                            |> Result.bind (fun () ->
                                if bad then err fname (sprintf "array recurrence on '%s' (element write whose rhs reads the same array) is not differentiable (v1)" a)
                                else Ok ())
                        | _ -> err fname "unsupported assignment target"
                | NAssign _ -> Ok ()
                | NFor (_, _, _, body) ->
                    // loop-local declarations are replay-reconstructed —
                    // exclude them from the read ban
                    let declared = boundNames body |> Set.ofList
                    let accums = Set.difference (assignedNames body) declared
                    check body true (if inLoop then Set.union loopAccums accums else accums)))
            (Ok ())
    check loops false Set.empty

// ============================================================================
// The reverse sweep
// ============================================================================

let private dName (n: string) = "__g_" + n

type private RevCtx = {
    Fname: string
    Ctx: Ctx
    Diff: Set<string>
    Arrays: Set<string>
}

/// Emit `d += cot`-style accumulation onto a cotangent target.
let private accum (target: Expr) (cot: Expr) : NStmt =
    NAssign (target, add target cot)

/// Bind a nontrivial cotangent expression to a temp so the adjoints of the
/// operands reference a variable, not a duplicated tree. Returns the
/// prefix statement(s) and the expression to use as the cotangent.
let private bindCot (rc: RevCtx) (cot: Expr) : NStmt list * Expr =
    match cot.Kind with
    | ExprKind.ExprLit _ | ExprKind.ExprVar _ -> [], cot
    | _ ->
        let c = fresh rc.Ctx "__c"
        [NLet (c, false, cot)], inheritSpan cot (ExprVar c)

/// Adjoint statements for expression `e` with cotangent `cot`.
/// Every returned statement accumulates into a `__g_*` target.
let rec private adjointOf (rc: RevCtx) (e: Expr) (cot: Expr) : Result<NStmt list, string> =
    match e.Kind with
    | ExprKind.ExprLit _ -> Ok []
    | ExprKind.ExprTyped (inner, _) -> adjointOf rc inner cot
    | ExprKind.ExprVar x ->
        if Set.contains x rc.Diff then Ok [accum (v (dName x)) cot] else Ok []
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar a }, idxs) when Set.contains a rc.Arrays ->
        if Set.contains a rc.Diff then
            Ok [accum (inheritSpan e (ExprApp (v (dName a), idxs))) cot]
        else Ok []
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar name }, [u]) when isMathIntrinsic name
                                       && not (Map.containsKey name rc.Ctx.Decls) ->
        (match derivRule name u with
         | None -> Ok []   // floor/ceil: zero derivative
         | Some d ->
             let pre, c = bindCot rc cot
             adjointOf rc u (mul c d) |> Result.map (fun ss -> pre @ ss))
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar _ }, _) ->
        // array read of a non-diff array, or int-typed call — no adjoint
        Ok []
    | ExprKind.ExprUnaryOp (OpNeg, inner) -> adjointOf rc inner (neg cot)
    | ExprKind.ExprBinOp (_, OpAdd, l, r) ->
        adjointOf rc l cot |> Result.bind (fun sl ->
        adjointOf rc r cot |> Result.map (fun sr -> sl @ sr))
    | ExprKind.ExprBinOp (_, OpSub, l, r) ->
        adjointOf rc l cot |> Result.bind (fun sl ->
        adjointOf rc r (neg cot) |> Result.map (fun sr -> sl @ sr))
    | ExprKind.ExprBinOp (_, OpMul, l, r) ->
        let pre, c = bindCot rc cot
        adjointOf rc l (mul c r) |> Result.bind (fun sl ->
        adjointOf rc r (mul c l) |> Result.map (fun sr -> pre @ sl @ sr))
    | ExprKind.ExprBinOp (_, OpDiv, l, r) ->
        let pre, c = bindCot rc cot
        adjointOf rc l (div c r) |> Result.bind (fun sl ->
        adjointOf rc r (neg (div (mul c l) (mul r r))) |> Result.map (fun sr -> pre @ sl @ sr))
    | ExprKind.ExprBinOp (_, OpCaret, b, { Kind = ExprKind.ExprLit (LitInt n) }) when int n >= 0 ->
        // Constant natural exponent: emit the closed form directly rather
        // than routing through the general rule, which would leave a
        // pow(b, 2.0 - 1.0) in the output.
        let n' = int n
        if n' = 0 then Ok []
        else
            let dterm =
                if n' = 1 then fLit 1.0
                elif n' = 2 then mul (fLit 2.0) b
                else mul (fLit (float n')) (pow b (iLit (int64 (n' - 1))))
            adjointOf rc b (mul cot dterm)
    | ExprKind.ExprBinOp (_, OpCaret, b, e) ->
        // d(b^e) = e*b^(e-1) db  +  b^e*log(b) de.
        // The base term keeps the power form instead of the equivalent
        // e*b^e/b so that b = 0 stays finite. The log term is reachable
        // only when `e` is itself active — a constant exponent takes the
        // ExprLit/inactive-var path to Ok [] and never evaluates log(b),
        // so a negative base still differentiates.
        let pre, c = bindCot rc cot
        adjointOf rc b (mul c (mul e (pow b (sub e (fLit 1.0))))) |> Result.bind (fun sb ->
        adjointOf rc e (mul c (mul (pow b e) (call "log" [b]))) |> Result.map (fun se ->
            pre @ sb @ se))
    | ExprKind.ExprBinOp (_, (OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe | OpAnd | OpOr), _, _) ->
        Ok []   // boolean-valued: no adjoint
    | ExprKind.ExprBinOp (_, OpMod, _, _) -> Ok []  // int-valued
    | ExprKind.ExprArrayLit _ ->
        err rc.Fname "array literals may only appear as let initializers in differentiated code"
    | _ -> err rc.Fname "unsupported expression form in differentiated code (adjoint)"

/// Adjoint of one forward statement (statements arrive in REVERSE order).
let rec private adjointOfStmt (rc: RevCtx) (s: NStmt) : Result<NStmt list, string> =
    match s with
    | NLet (x, _, value) ->
        if not (Set.contains x rc.Diff) then Ok []
        else
            (match value with
             | { Kind = ExprKind.ExprArrayLit _ } | ConstFill _ -> Ok []   // literal/fill init: nothing flows back
             | _ -> adjointOf rc value (v (dName x)))
    | NAssign (lhs, rhs) ->
        (match additiveSelf lhs rhs with
         | Some (sign, e) ->
             let cotBase =
                 match lhs.Kind with
                 | ExprKind.ExprVar x when Set.contains x rc.Diff -> Some (v (dName x))
                 | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar a }, idxs) when Set.contains a rc.Diff ->
                     Some (inheritSpan lhs (ExprApp (v (dName a), idxs)))
                 | _ -> None
             match cotBase with
             | None -> Ok []
             | Some c ->
                 let cot = if sign < 0.0 then neg c else c
                 adjointOf rc e cot
         | None ->
             // general overwrite: save cotangent, zero it, then flow into rhs
             let target =
                 match lhs.Kind with
                 | ExprKind.ExprVar x when Set.contains x rc.Diff -> Some (v (dName x))
                 | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar a }, idxs) when Set.contains a rc.Diff ->
                     Some (inheritSpan lhs (ExprApp (v (dName a), idxs)))
                 | _ -> None
             match target with
             | None -> Ok []
             | Some t ->
                 let c = fresh rc.Ctx "__c"
                 adjointOf rc rhs (inheritSpan lhs (ExprVar c)) |> Result.map (fun flow ->
                     [NLet (c, false, t); NAssign (t, fLit 0.0)] @ flow))
    | NFor (var, lo, hi, body) ->
        // Same-direction adjoint loop: REPLAY THE WHOLE BODY (fresh
        // per-iteration values — including loop-local arrays filled by
        // interior construction loops), declare loop-local cotangents for
        // loop-local diff lets, then run the body's adjoints reversed.
        //
        // Replaying accumulations into variables that OUTLIVE the loop
        // corrupts them — soundly: by reverse-sweep order every adjoint
        // that reads such a variable's forward value has already run
        // (post-loop statements' adjoints precede this loop's), and the
        // discipline checks ban pre-loop reads. Lets-only replay is NOT
        // enough: a loop-local array built by an interior loop would read
        // back as its zero literal.
        let localLets = body |> List.choose (fun s ->
            match s with
            | NLet (n, _, _) when Set.contains n rc.Diff -> Some n
            | _ -> None)
        let replay = body
        let localCots =
            localLets |> List.map (fun n ->
                match body |> List.tryPick (fun s ->
                        match s with
                        // literal or constant-fill initializer: zerosLike
                        // yields the matching zero shape (None for other
                        // forms, so tryPick keeps them scalar-defaulted)
                        | NLet (m, _, init) when m = n -> zerosLikeLiteral init
                        | _ -> None) with
                | Some z -> NLet (dName n, true, z)
                | None -> NLet (dName n, true, fLit 0.0))
        let folded =
            List.rev body
            |> List.fold (fun acc s ->
                acc |> Result.bind (fun ss ->
                    adjointOfStmt rc s |> Result.map (fun s' -> ss @ s')))
                (Ok [])
        folded |> Result.map (fun bodyAdjoints ->
            [NFor (var, lo, hi, replay @ localCots @ bodyAdjoints)])

// ============================================================================
// NStmt -> Stmt conversion
// ============================================================================

let rec private toStmts (ns: NStmt list) : Stmt list =
    ns |> List.map (fun s ->
        match s with
        | NLet (n, isMut, value) ->
            StmtLet { Pattern = synPat (PatVar n)
                      Type = None
                      Value = value
                      Mutability = if isMut then BindMut else BindLet }
        | NAssign (lhs, rhs) -> StmtExpr (mkExpr (mergeSpan lhs.Span rhs.Span) (ExprAssign (lhs, rhs)))
        | NFor (var, lo, hi, body) -> StmtForIn (var, mkExpr (mergeSpan lo.Span hi.Span) (ExprDotDot (lo, hi)), toStmts body))

// ============================================================================
// Synthesize f__grad
// ============================================================================

let private gradSuffix = "__grad"

let private synthesize (ctx: Ctx) (fd: FunctionDecl) : Result<FunctionDecl, string> =
    let fname = fd.Name
    // Return type must be a Float scalar (checked syntactically; the
    // typechecker re-verifies the generated function anyway).
    match fd.ReturnType with
    | Some t when isFloatTy t -> Ok ()
    | Some _ -> err fname "grad requires a function returning Float (scalar loss)"
    | None -> err fname "grad requires an explicit `-> Float` return annotation"
    |> Result.bind (fun () ->
    // classify parameters
    let classesR =
        fd.Params |> List.fold (fun acc p ->
            acc |> Result.bind (fun cs ->
                classifyParam fname p |> Result.map (fun c -> cs @ [(p, c)])))
            (Ok [])
    classesR |> Result.bind (fun classes ->
    let diffParams =
        classes |> List.choose (fun (p, c) ->
            match c with DiffArray | DiffScalar -> Some p.Name | NonDiff -> None)
        |> Set.ofList
    let arrayParams =
        classes |> List.choose (fun (p, c) ->
            match c with DiffArray -> Some p.Name | _ -> None)
        |> Set.ofList
    let scalarDiff =
        classes |> List.choose (fun (p, c) ->
            match c with DiffScalar -> Some p.Name | _ -> None)
    if Set.isEmpty diffParams then
        err fname "no differentiable (Float or Float-array) parameters"
    else
    // normalize + inline
    normalizeBody fname ctx 0 fd |> Result.bind (fun (stmts, finalE) ->
    // validate every expression in the fragment
    let validateAll =
        let rec valStmts ss =
            ss |> List.fold (fun acc s ->
                acc |> Result.bind (fun () ->
                    match s with
                    | NLet (_, _, e) -> walkExpr fname ctx ignore e
                    | NAssign (l, r) ->
                        walkExpr fname ctx ignore l
                        |> Result.bind (fun () -> walkExpr fname ctx ignore r)
                    | NFor (_, lo, hi, body) ->
                        walkExpr fname ctx ignore lo
                        |> Result.bind (fun () -> walkExpr fname ctx ignore hi)
                        |> Result.bind (fun () -> valStmts body)))
                (Ok ())
        valStmts stmts |> Result.bind (fun () -> walkExpr fname ctx ignore finalE)
    validateAll |> Result.bind (fun () ->
    checkLoopDiscipline fname ctx stmts |> Result.bind (fun () ->
    checkWriteAfterRead fname ctx stmts |> Result.bind (fun () ->
    analyze fname ctx diffParams arrayParams stmts finalE |> Result.bind (fun (diff, arrays) ->
    checkNoScalarOverwrite fname diff stmts |> Result.bind (fun () ->
    let rc = { Fname = fname; Ctx = ctx; Diff = diff; Arrays = arrays }

    // cotangent declarations for function-level diff LOCALS (params' array
    // cotangents are mut parameters; scalar-param cotangents are locals)
    let localDecls =
        stmts |> List.choose (fun s ->
            match s with
            | NLet (n, _, value) when Set.contains n diff ->
                if Set.contains n arrays then
                    match value with
                    | { Kind = ExprKind.ExprArrayLit _ } | ConstFill _ ->
                        zerosLikeLiteral value |> Option.map (fun z -> NLet (dName n, true, z))
                    | _ -> None   // rejected below
                else Some (NLet (dName n, true, fLit 0.0))
            | _ -> None)
    // reject function-level diff array locals not initialized by literals
    let badArrayLocal =
        stmts |> List.tryPick (fun s ->
            match s with
            | NLet (n, _, value) when Set.contains n diff && Set.contains n arrays ->
                (match value with
                 | { Kind = ExprKind.ExprArrayLit _ } | ConstFill _ -> None
                 | { Kind = ExprKind.ExprVar _ } -> Some n   // alias — cotangent identity untrackable
                 | _ -> Some n)
            | _ -> None)
    match badArrayLocal with
    | Some n -> err fname (sprintf "differentiable array local '%s' must be initialized by an array literal (aliases are not differentiable in v1)" n)
    | None ->
    let scalarCots = scalarDiff |> List.map (fun p -> NLet (dName p, true, fLit 0.0))

    // seed: adjoint of the final expression with cotangent 1.0
    adjointOf rc finalE (fLit 1.0) |> Result.bind (fun seed ->
    // reverse sweep over the statements
    let folded =
        List.rev stmts
        |> List.fold (fun acc s ->
            acc |> Result.bind (fun ss ->
                adjointOfStmt rc s |> Result.map (fun s' -> ss @ s')))
            (Ok [])
    folded |> Result.map (fun reverse ->

    // assemble: forward + primal + cot decls + seed + reverse + return
    let primalName = "__primal"
    let fwd = toStmts stmts @ [ StmtLet { Pattern = synPat (PatVar primalName)
                                          Type = None
                                          Value = finalE
                                          Mutability = BindLet } ]
    let cotDecls = toStmts (localDecls @ scalarCots)
    let revStmts = toStmts (seed @ reverse)
    let retExpr =
        match scalarDiff with
        | [] -> v primalName
        | ss -> syn (ExprTuple (v primalName :: (ss |> List.map (fun p -> v (dName p)))))
    let retTy =
        match scalarDiff with
        | [] -> fd.ReturnType
        | ss -> Some (TyTuple ((Option.defaultValue TyFloat64 fd.ReturnType)
                               :: (ss |> List.map (fun _ -> TyNamed ("Float", [])))))
    let gradParams =
        fd.Params
        @ (classes |> List.choose (fun (p, c) ->
             match c with
             | DiffArray -> Some { Name = dName p.Name; Type = p.Type; Mutability = Mutable }
             | _ -> None))
    { Name = fname + gradSuffix
      TypeParams = fd.TypeParams
      Params = gradParams
      WhereClause = None
      ReturnType = retTy
      Body = inheritSpan fd.Body (ExprBlock (fwd @ cotDecls @ revStmts, Some retExpr))
      IsStatic = false }))))))))))

// ============================================================================
// Call-site rewriting + program expansion
// ============================================================================

/// Rewrite alias.grad(f) call sites in an expression; collect requested names.
let rec private rewriteExpr (requested: System.Collections.Generic.HashSet<string>)
                            (declNames: Set<string>) (aliases: Set<string>) (e: Expr) : Result<Expr, string> =
    let r = rewriteExpr requested declNames aliases
    let rList es =
        es |> List.fold (fun acc x ->
            acc |> Result.bind (fun xs -> r x |> Result.map (fun x' -> xs @ [x'])))
            (Ok [])
    let re k = inheritSpan e k
    match e.Kind with
    // Qualified: `alias.grad(f)` with alias bound by `import ad`. Bare
    // `grad(...)` is no longer recognized — the AD surface is a module,
    // not a language-wide name (same rule as the ml/ppl surfaces).
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, "grad") }, args) when Set.contains alias aliases ->
        (match args with
         | [{ Kind = ExprKind.ExprVar fname }] ->
             if Set.contains fname declNames then
                 requested.Add fname |> ignore
                 Ok (re (ExprVar (fname + gradSuffix)))
             else
                 Error (sprintf "grad: '%s' is not a top-level function in this module (grad differentiates same-module named functions)" fname)
         | [_] -> Error "grad: argument must be a named top-level function (e.g. ad.grad(loss))"
         | _ -> Error "grad: expects exactly one argument, the function to differentiate")
    | ExprKind.ExprLit _ | ExprKind.ExprVar _ -> Ok e
    | ExprKind.ExprApp (f, args) ->
        r f |> Result.bind (fun f' -> rList args |> Result.map (fun args' -> re (ExprApp (f', args'))))
    | ExprKind.ExprBinOp (m, op, l, rr) ->
        r l |> Result.bind (fun l' -> r rr |> Result.map (fun r' -> re (ExprBinOp (m, op, l', r'))))
    | ExprKind.ExprUnaryOp (op, inner) -> r inner |> Result.map (fun i -> re (ExprUnaryOp (op, i)))
    | ExprKind.ExprTyped (inner, t) -> r inner |> Result.map (fun i -> re (ExprTyped (i, t)))
    | ExprKind.ExprAssign (l, rr) ->
        r l |> Result.bind (fun l' -> r rr |> Result.map (fun r' -> re (ExprAssign (l', r'))))
    | ExprKind.ExprTuple es -> rList es |> Result.map (fun es' -> re (ExprTuple es'))
    | ExprKind.ExprArrayLit es -> rList es |> Result.map (fun es' -> re (ExprArrayLit es'))
    | ExprKind.ExprDotDot (l, h) ->
        r l |> Result.bind (fun l' -> r h |> Result.map (fun h' -> re (ExprDotDot (l', h'))))
    | ExprKind.ExprIf (c, t, f) ->
        r c |> Result.bind (fun c' ->
        r t |> Result.bind (fun t' ->
        r f |> Result.map (fun f' -> re (ExprIf (c', t', f')))))
    | ExprKind.ExprLet (binding, body) ->
        r binding.Value |> Result.bind (fun v' ->
        r body |> Result.map (fun b' -> re (ExprLet ({ binding with Value = v' }, b'))))
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
            | Some fe -> r fe |> Result.map (fun fe' -> re (ExprBlock (stmts', Some fe')))
            | None -> Ok (re (ExprBlock (stmts', None))))
    | ExprKind.ExprLambda (ps, w, body) -> r body |> Result.map (fun b -> re (ExprLambda (ps, w, b)))
    | ExprKind.ExprMatch (scrut, cases) ->
        r scrut |> Result.bind (fun s' ->
            cases |> List.fold (fun acc c ->
                acc |> Result.bind (fun cs ->
                    r c.Body |> Result.map (fun b -> cs @ [{ c with Body = b }])))
                (Ok [])
            |> Result.map (fun cs' -> re (ExprMatch (s', cs'))))
    | _ -> Ok e   // exotic containers: grad not expected inside

/// `import ad [as _]` — the module this transform is surfaced through.
let private isAdImport (d: Located<Decl>) =
    match d.Value with
    | DeclImport (["ad"], _) -> true
    | _ -> false

/// Aliases bound to `ad` in this decl list. Errors on a selective
/// `from ad import ...` (it would reintroduce a global name).
let private adAliasesOf (decls: Located<Decl> list) : Result<Set<string>, string> =
    decls |> List.fold (fun acc d ->
        acc |> Result.bind (fun set ->
            match d.Value with
            | DeclImport (["ad"], ImportQualified aliasOpt) ->
                Ok (Set.add (aliasOpt |> Option.defaultValue "ad") set)
            | DeclImport (["ad"], ImportSelective _) ->
                Error "`ad` supports only `import ad [as <alias>]`; a selective `from ad import ...` would reintroduce global names"
            | _ -> Ok set))
        (Ok Set.empty)

/// Expand alias.grad() over one module's declarations. Import-gated: with no
/// `import ad`, the pass is a no-op and bare `grad(...)` is simply unbound.
let private expandModule (decls: Located<Decl> list) : Result<Located<Decl> list, string> =
    adAliasesOf decls |> Result.bind (fun aliases ->
    if Set.isEmpty aliases then Ok decls
    else
    let decls = decls |> List.filter (not << isAdImport)
    let funcDecls =
        decls |> List.choose (fun d ->
            match d.Value with
            | DeclFunction fd -> Some (fd.Name, fd)
            | _ -> None)
        |> Map.ofList
    // Source span per differentiable function, for stamping synthSpan so the
    // syn-based derivative builders carry the differentiated decl's location.
    let funcSpans =
        decls |> List.choose (fun d ->
            match d.Value with
            | DeclFunction fd -> Some (fd.Name, d.Span)
            | _ -> None)
        |> Map.ofList
    let declNames = funcDecls |> Map.toSeq |> Seq.map fst |> Set.ofSeq
    let requested = System.Collections.Generic.HashSet<string>()
    // rewrite call sites everywhere
    let rewritten =
        decls |> List.fold (fun acc d ->
            acc |> Result.bind (fun ds ->
                let mapped =
                    match d.Value with
                    | DeclFunction fd ->
                        rewriteExpr requested declNames aliases fd.Body
                        |> Result.map (fun b -> DeclFunction { fd with Body = b })
                    | DeclLet binding ->
                        rewriteExpr requested declNames aliases binding.Value
                        |> Result.map (fun v' -> DeclLet { binding with Value = v' })
                    | DeclStatic binding ->
                        rewriteExpr requested declNames aliases binding.Value
                        |> Result.map (fun v' -> DeclStatic { binding with Value = v' })
                    | other -> Ok other
                mapped |> Result.map (fun value -> ds @ [{ d with Value = value }])))
            (Ok [])
    rewritten |> Result.bind (fun decls' ->
        if requested.Count = 0 then Ok decls'
        else
            let ctx = { Decls = funcDecls; Fresh = 0 }
            // synthesize each requested derivative once
            let synthesized =
                requested |> Seq.sort |> Seq.fold (fun acc fname ->
                    acc |> Result.bind (fun (made: Map<string, FunctionDecl>) ->
                        // Stamp the ambient synthesis span with this decl's
                        // span before building its derivative (syn/syn-based
                        // builders read it).
                        Blade.Ast.synthSpan <- (Map.tryFind fname funcSpans |> Option.defaultValue noSpan)
                        synthesize ctx funcDecls.[fname]
                        |> Result.map (fun gd -> Map.add fname gd made)))
                    (Ok Map.empty)
            Blade.Ast.synthSpan <- noSpan
            synthesized |> Result.map (fun made ->
                // splice each f__grad immediately after its source decl
                decls' |> List.collect (fun d ->
                    match d.Value with
                    | DeclFunction fd when Map.containsKey fd.Name made ->
                        [d; { d with Value = DeclFunction made.[fd.Name] }]
                    | _ -> [d]))))

/// Entry point: expand grad() across a program. Errors are compile errors.
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
        [ Blade.Diagnostics.mkError "BL5500" (Blade.Diagnostics.Codes.phaseOfCode "BL5500") Blade.Ast.synthSpan msg ])
