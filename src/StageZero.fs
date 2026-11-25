// A STAGE OVER AN UNCOMPUTED GUARD OR CHOICE (the owner's decision of
// 2026-11-24, gap G335; formalism 10.4). `f <$> c` over an uncomputed guard or
// choice follows the laws: a stage over a false guard is the zero of the
// stage's result (10.4 left zero), a stage over a choice runs on each side and
// "first non-zero" is tested on the stage's RESULT (left distribution). Over
// the COMPUTED value the same stage maps the cells. The two readings differ
// exactly when the stage moves zero: `inc <$> guard(false, c)` is zeros while
// `inc <$> (guard(false, c) |> compute)` is ones; `pos <$> (a <|> b)` tests
// pos(a) where the computed choice tests a. That is the one program class in
// which `|> compute` changes a value with no write involved, so it is REFUSED
// here, before the lane split, in every lane (C++, LLVM, interpreter, REPL):
//
//   * over a GUARD the stage must provably map the bit-exact zero of its input
//     element type to the bit-exact zero of its result type (+0.0, the complex
//     zero with both parts +0.0, integer 0, false): a compile-time evaluation
//     of the stage at that zero, a pure scalar callable whose captures are
//     compile-time constants. `-z` maps +0.0 to -0.0, which prints differently,
//     so it is refused over a guard;
//   * over a CHOICE the stage must provably PRESERVE and REFLECT zero (f(v) is
//     zero exactly when v is zero), which one point cannot show: a closed
//     whitelist of stage bodies (stageKeepsZeroCells);
//   * a NEST (a guard of a choice, a choice of guards, a sequence or a bind
//     whose result is one of them) takes the CONJUNCTION of the requirements
//     of everything it contains.
//
// What cannot be decided is refused (fail closed); there is no run-time check.
// The rule only refuses: every program it accepts gives the same values under
// both readings, so no emitted code changes. Both intents keep an exact
// spelling: the stage INSIDE (`guard(p, f <$> c)`, `(f <$> a) <|> (f <$> b)`)
// or the stage over the computed VALUE (`f <$> (c |> compute)`).
//
// The walk is over the TYPED tree (the spans of the stage applications exist
// only there), with TypeCheckSupport.typedExprChildren -- total over the
// TypedExpr kinds -- as the child enumerator. It walks no types, no patterns
// and no side tables: a stage application is an expression, and every
// expression of the program is reached from a binding value, a function body
// or an impl method body (lambda bodies are children).
module Blade.StageZero

open Blade.Ast
open Blade.IR
open Blade.Types
open Blade.TypedAst

// ---------------------------------------------------------------------------
// Program tables: what a name denotes.
// ---------------------------------------------------------------------------

type private Tables = {
    /// let-bound values by binder id (top level, block statements, typed lets)
    Lets: System.Collections.Generic.Dictionary<IRId, TypedExpr>
    /// binders declared `let mut`
    Mutable: System.Collections.Generic.HashSet<IRId>
    /// every right-hand side assigned to a binder (`x = e`)
    Assigned: System.Collections.Generic.Dictionary<IRId, TypedExpr list>
    /// destructured sub-binders whose component is a known expression
    Components: System.Collections.Generic.Dictionary<IRId, TypedExpr>
    /// user functions by id
    Funcs: System.Collections.Generic.Dictionary<IRId, TypedFunctionDecl>
}

let private noteBinding (t: Tables) (b: TypedBinding) =
    t.Lets[b.VarId] <- b.Value
    if b.IsLetMut then t.Mutable.Add b.VarId |> ignore
    // `let (g, h) = (guard(..), ..)`: each sub-binder is its component
    match b.Value.Kind with
    | TExprTuple es ->
        b.SubBindings |> List.iteri (fun k (_, id, _) ->
            let slot =
                match b.Destructure with
                | DSPositional -> Some k
                | DSTupleAt (comps, _) -> List.tryItem k comps
                | DSConsRest -> None
            match slot |> Option.bind (fun s -> List.tryItem s es) with
            | Some e -> t.Components[id] <- e
            | None -> ())
    | _ -> ()

let private noteAssign (t: Tables) (lhs: TypedExpr) (rhs: TypedExpr) =
    match lhs.Kind with
    | TExprVar (_, id, _) ->
        let prev = match t.Assigned.TryGetValue id with | true, l -> l | _ -> []
        t.Assigned[id] <- rhs :: prev
    | _ -> ()

let rec private collectExpr (t: Tables) (e: TypedExpr) =
    match e.Kind with
    | TExprLet (_, id, v, _) -> t.Lets[id] <- v
    | TExprAssign (l, r) -> noteAssign t l r
    | TExprBlock (stmts, _) ->
        let rec stmt (s: TypedStmt) =
            match s with
            | TStmtLet b -> noteBinding t b
            | TStmtAssign (l, r) -> noteAssign t l r
            | TStmtExpr _ -> ()
            | TStmtForIn (_, _, _, _, body) -> body |> List.iter stmt
        stmts |> List.iter stmt
    | _ -> ()
    for c in Blade.TypeCheckSupport.typedExprChildren e do collectExpr t c

/// Every expression root of the program, with the binding it belongs to.
let private roots (program: TypedProgram) : TypedExpr list =
    [ for m in program.Modules do
        for d in m.Decls do
            match d with
            | TDeclLet b | TDeclStatic b ->
                yield b.Value
                for (_, pc) in b.PostChecks do yield pc
            | TDeclFunction f -> yield f.Body
            | TDeclImpl i -> for f in i.Methods do yield f.Body
            | TDeclType _ | TDeclInterface _ | TDeclUnit _ | TDeclImport _ -> () ]

let private buildTables (program: TypedProgram) : Tables =
    let t = {
        Lets = System.Collections.Generic.Dictionary()
        Mutable = System.Collections.Generic.HashSet()
        Assigned = System.Collections.Generic.Dictionary()
        Components = System.Collections.Generic.Dictionary()
        Funcs = System.Collections.Generic.Dictionary() }
    for m in program.Modules do
        for d in m.Decls do
            match d with
            | TDeclLet b | TDeclStatic b -> noteBinding t b
            | TDeclFunction f -> t.Funcs[f.FuncId] <- f
            | TDeclImpl i -> for f in i.Methods do t.Funcs[f.FuncId] <- f
            | TDeclType _ | TDeclInterface _ | TDeclUnit _ | TDeclImport _ -> ()
    for r in roots program do collectExpr t r
    t

/// The values a binder may hold: its let value and every value assigned to it.
let private valuesOf (t: Tables) (id: IRId) : TypedExpr list =
    let own =
        match t.Lets.TryGetValue id with
        | true, v -> [ v ]
        | _ ->
            match t.Components.TryGetValue id with
            | true, v -> [ v ]
            | _ -> []
    let assigned = match t.Assigned.TryGetValue id with | true, l -> l | _ -> []
    own @ assigned

// ---------------------------------------------------------------------------
// The operand: is it (through names, aliases and nests) an uncomputed guard
// or choice?
// ---------------------------------------------------------------------------

type private Reach = {
    Guard: bool
    Choice: bool
    /// how the operand reaches its guard / choice, for the message
    Forms: string list
}

let private noReach = { Guard = false; Choice = false; Forms = [] }
let private join (a: Reach) (b: Reach) =
    { Guard = a.Guard || b.Guard; Choice = a.Choice || b.Choice; Forms = a.Forms @ b.Forms }
let private joinAll (rs: Reach list) = rs |> List.fold join noReach

let private at (s: Span) = if s = noSpan then "" else $" at line {s.StartLine}:{s.StartCol}"

/// THE OPERAND CLASS, an exhaustive match over the typed forms a stage's
/// operand can take. A guard / choice is in scope; a name, an alias, a tuple
/// member, a let body, an `if` / `match` value (either arm), a sequence (any
/// element), a bind (its continuation's result), a `@>>` (its left operand,
/// the stage's input), `pure` and a stack of `<$>` stages are followed; a
/// call of a user function or lambda is followed into its result, and (fail
/// closed) a replicated body and both legs of `<&>` / `<&!>`; a computed
/// value (`|> compute`), a map application (the stage composes into its
/// kernel), a parameter and every stored array are out of scope. A `<|:>`
/// fallback is not in scope: `<$>` over an uncomputed fallback is refused in
/// every lane already (BL7004 in the compiled lanes, unsupported in the
/// interpreter), so it has no second reading.
let rec private reach (t: Tables) (seen: Set<IRId>) (e: TypedExpr) : Reach =
    let follow id what =
        if seen.Contains id then noReach
        else
            let r = valuesOf t id |> List.map (reach t (seen.Add id)) |> joinAll
            if r.Guard || r.Choice then { r with Forms = what :: r.Forms } else r
    match e.Kind with
    | TExprGuard (_, body) ->
        join { Guard = true; Choice = false; Forms = [ $"the guard{at e.Span}" ] } (reach t seen body)
    | TExprChoice (a, b) ->
        join { Guard = false; Choice = true; Forms = [ $"the choice{at e.Span}" ] }
             (join (reach t seen a) (reach t seen b))
    | TExprVar (name, id, _) -> follow id $"`{name}`"
    | TExprFunctorMap (_, c) -> reach t seen c
    | TExprCompose (OpComposeMeth, l, _) -> reach t seen l
    | TExprPure inner -> reach t seen inner
    | TExprSequence es -> es |> List.map (reach t seen) |> joinAll
    | TExprBind (_, k) -> reachResultOf t seen k
    | TExprIf (_, a, b) -> join (reach t seen a) (reach t seen b)
    | TExprMatch (_, cases) -> cases |> List.map (fun c -> reach t seen c.Body) |> joinAll
    | TExprLet (_, _, _, body) -> reach t seen body
    | TExprBlock (_, Some final) -> reach t seen final
    | TExprTupleIndex (tup, { Kind = TExprLit (LitInt k) }) -> reachMember t seen tup (int k)
    | TExprTupleIndex _ -> noReach
    | TExprApp (f, _) -> reachResultOf t seen f
    // fail closed: a replicated body, and the legs of a parallel / fused pair
    | TExprReplicate (_, body) -> reach t seen body
    | TExprParallel (a, b) | TExprFusion (a, b) -> join (reach t seen a) (reach t seen b)
    // `<$>` over an uncomputed `<|:>` fallback is refused in every lane already (no second reading)
    | TExprFallback _ -> noReach
    // a map application (the stage composes into its kernel), a computed value, a callable, a loop
    // object, a composed loop object or function, and every form whose value is stored cells or a
    // scalar: out of scope. Listed one by one ON PURPOSE (no default arm): a kind added to the typed
    // tree later is a compile error here until someone classifies it.
    | TExprApply _ | TExprCompute _ | TExprLambda _ | TExprSection _ | TExprPartialApp _
    | TExprMethodFor _ | TExprObjectFor _ | TExprCompose _ | TExprReynolds _ | TExprZero
    | TExprLit _ | TExprWildcard | TExprQualified _ | TExprBinOp _ | TExprUnaryOp _ | TExprPolyTail _
    | TExprField _ | TExprTuple _ | TExprComplexLit _ | TExprFma _ | TExprArrayLit _ | TExprRange _
    | TExprDotDot _ | TExprReverse _ | TExprZip _ | TExprStack _ | TExprJoin _ | TExprRead _
    | TExprFillRandom _ | TExprRandGen _ | TExprArity _ | TExprRank _ | TExprMask _ | TExprCompound _
    | TExprSparse _ | TExprIntersect _ | TExprUnion _ | TExprUnique _ | TExprContains _
    | TExprDisplayEmit _ | TExprDisplayJson _ | TExprDisplayNum _ | TExprDisplayStr _ | TExprGroupBy _
    | TExprGroupKeys _ | TExprGroupBucket _ | TExprSegments _ | TExprSegmentsGrid _ | TExprUngroupGrid _
    | TExprUngroup _ | TExprUngroupRows _ | TExprSort _ | TExprReduce _ | TExprProdSum _
    | TExprTranspose _ | TExprDecompact _ | TExprGram _ | TExprGramApply _ | TExprMatmul _ | TExprEigh _
    | TExprSolve _ | TExprLu _ | TExprLuSolve _ | TExprArrayNegate _ | TExprArrayConjugate _
    | TExprExtents _ | TExprStruct _ | TExprIndex _ | TExprBlock (_, None) | TExprAssign _ | TExprAlign _
    | TExprConstraintCheck _ | TExprBreakIf _ -> noReach

/// The result of applying a callable (a bind continuation, a called function).
and private reachResultOf (t: Tables) (seen: Set<IRId>) (f: TypedExpr) : Reach =
    match f.Kind with
    | TExprLambda info -> reach t seen info.Body
    | TExprVar (_, id, _) when not (seen.Contains id) ->
        match t.Funcs.TryGetValue id with
        | true, fd -> reach t (seen.Add id) fd.Body
        | _ -> valuesOf t id |> List.map (reachResultOf t (seen.Add id)) |> joinAll
    | _ -> noReach

and private reachMember (t: Tables) (seen: Set<IRId>) (tup: TypedExpr) (k: int) : Reach =
    match tup.Kind with
    | TExprTuple es -> (match List.tryItem k es with Some m -> reach t seen m | None -> noReach)
    | TExprVar (_, id, _) when not (seen.Contains id) ->
        valuesOf t id |> List.map (fun v -> reachMember t (seen.Add id) v k) |> joinAll
    | _ -> noReach

// ---------------------------------------------------------------------------
// The stage.
// ---------------------------------------------------------------------------

/// A stage resolved to its scalar callables, applied in order.
type private Stage = { Param: IRId; ParamType: IRType; Body: TypedExpr }

let rec private resolveStage (t: Tables) (seen: Set<IRId>) (f: TypedExpr) : Stage list option =
    match f.Kind with
    | TExprLambda { Params = [ p ]; Body = body } -> Some [ { Param = p.VarId; ParamType = p.Type; Body = body } ]
    | TExprVar (_, id, _) when not (seen.Contains id) ->
        match t.Funcs.TryGetValue id with
        | true, { Params = [ p ]; Body = body } -> Some [ { Param = p.VarId; ParamType = p.Type; Body = body } ]
        | true, _ -> None
        | _ ->
            // a name stands for ONE stage only when it is never reassigned
            if t.Mutable.Contains id || t.Assigned.ContainsKey id then None
            else
                match t.Lets.TryGetValue id with
                | true, v -> resolveStage t (seen.Add id) v
                | _ -> None
    // `f >> g`: f first, then g
    | TExprCompose (OpCompose, a, b) ->
        match resolveStage t seen a, resolveStage t seen b with
        | Some x, Some y -> Some (x @ y)
        | _ -> None
    | _ -> None

let private stageName (f: TypedExpr) : string =
    match f.Kind with
    | TExprVar (name, _, _) -> $"`{name}`"
    | TExprLambda _ -> $"the lambda{at f.Span}"
    | _ -> $"the stage{at f.Span}"

let rec private elemOf (ty: IRType) : ElemType option =
    match stripUnits ty with
    | IRTScalar et -> Some et
    | IRTIdxTagged (inner, _) -> elemOf inner
    | ArrayElem a -> elemOf a.ElemType
    | _ -> None

let private isInt et = et = ETInt32 || et = ETInt64
let private isFloat et = et = ETFloat32 || et = ETFloat64
let private isComplex et = et = ETComplex64 || et = ETComplex128

// ---------------------------------------------------------------------------
// Z2: the compile-time evaluation of a stage at the zero.
// ---------------------------------------------------------------------------

type private ZVal =
    | ZI of int64 * ElemType
    | ZF of float * ElemType
    | ZC of float * float * ElemType
    | ZB of bool

let private wrapInt et (n: int64) = if et = ETInt32 then int64 (int32 n) else n
let private roundTo et (x: float) = if et = ETFloat32 || et = ETComplex64 then float (float32 x) else x

let private zeroOf (et: ElemType) : ZVal option =
    match et with
    | ETInt32 | ETInt64 -> Some (ZI (0L, et))
    | ETFloat32 | ETFloat64 -> Some (ZF (0.0, et))
    | ETComplex64 | ETComplex128 -> Some (ZC (0.0, 0.0, et))
    | ETBool -> Some (ZB false)
    | ETUnit | ETString -> None

let private posZero (x: float) = System.BitConverter.DoubleToInt64Bits x = 0L

let private isExactZero (v: ZVal) =
    match v with
    | ZI (n, _) -> n = 0L
    | ZF (x, _) -> posZero x
    | ZC (r, i, _) -> posZero r && posZero i
    | ZB b -> not b

let private show (v: ZVal) =
    let f (x: float) =
        if posZero x then "0.0"
        elif x = 0.0 then "-0.0"
        // a float prints as a float: 1.0, never 1
        elif System.Double.IsFinite x && x = floor x && abs x < 1e15 then x.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
        else x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
    match v with
    | ZI (n, _) -> string n
    | ZF (x, _) -> f x
    | ZC (r, i, _) -> $"complex({f r}, {f i})"
    | ZB b -> if b then "true" else "false"

let private mathOne (name: string) (x: float) : float option =
    match name with
    | "exp" -> Some (exp x) | "log" -> Some (log x) | "log10" -> Some (log10 x)
    | "sqrt" -> Some (sqrt x) | "sin" -> Some (sin x) | "cos" -> Some (cos x)
    | "tan" -> Some (tan x) | "sinh" -> Some (sinh x) | "cosh" -> Some (cosh x)
    | "tanh" -> Some (tanh x) | "asin" -> Some (asin x) | "acos" -> Some (acos x)
    | "atan" -> Some (atan x) | "abs" -> Some (abs x) | "floor" -> Some (floor x)
    | "ceil" -> Some (ceil x) | "trunc" -> Some (truncate x)
    | _ -> None

let private intPow et (b: int64) (e: int64) : ZVal option =
    if e < 0L then None
    else
        let mutable acc = 1L
        let mutable bb = b
        let mutable ee = e
        while ee > 0L do
            if ee &&& 1L = 1L then acc <- acc * bb
            bb <- bb * bb
            ee <- ee >>> 1
        Some (ZI (wrapInt et acc, et))

let private evalBin (op: BinOp) (a: ZVal) (b: ZVal) : ZVal option =
    let cmp (c: int) =
        match op with
        | OpEq -> Some (ZB (c = 0)) | OpNeq -> Some (ZB (c <> 0))
        | OpLt -> Some (ZB (c < 0)) | OpLe -> Some (ZB (c <= 0))
        | OpGt -> Some (ZB (c > 0)) | OpGe -> Some (ZB (c >= 0))
        | _ -> None
    match a, b with
    | ZI (x, et), ZI (y, _) ->
        match op with
        | OpAdd -> Some (ZI (wrapInt et (x + y), et))
        | OpSub -> Some (ZI (wrapInt et (x - y), et))
        | OpMul -> Some (ZI (wrapInt et (x * y), et))
        | OpDiv -> if y = 0L then None elif y = -1L then Some (ZI (wrapInt et (0L - x), et)) else Some (ZI (wrapInt et (x / y), et))
        | OpMod -> if y = 0L then None elif y = -1L then Some (ZI (0L, et)) else Some (ZI (wrapInt et (x % y), et))
        | OpCaret -> intPow et x y
        | _ -> cmp (compare x y)
    | ZF (x, et), ZF (y, _) ->
        match op with
        | OpAdd -> Some (ZF (roundTo et (x + y), et))
        | OpSub -> Some (ZF (roundTo et (x - y), et))
        | OpMul -> Some (ZF (roundTo et (x * y), et))
        | OpDiv -> Some (ZF (roundTo et (x / y), et))
        | OpCaret -> Some (ZF (roundTo et (System.Math.Pow (x, y)), et))
        | OpMath2 "atan2" -> Some (ZF (roundTo et (System.Math.Atan2 (x, y)), et))
        // a NaN compares false both ways; `compare` would order it
        | OpEq -> Some (ZB (x = y)) | OpNeq -> Some (ZB (x <> y))
        | OpLt -> Some (ZB (x < y)) | OpLe -> Some (ZB (x <= y))
        | OpGt -> Some (ZB (x > y)) | OpGe -> Some (ZB (x >= y))
        | _ -> None
    | ZC (xr, xi, et), ZC (yr, yi, _) ->
        match op with
        | OpAdd -> Some (ZC (roundTo et (xr + yr), roundTo et (xi + yi), et))
        | OpSub -> Some (ZC (roundTo et (xr - yr), roundTo et (xi - yi), et))
        | OpMul -> Some (ZC (roundTo et (xr * yr - xi * yi), roundTo et (xr * yi + xi * yr), et))
        | OpEq -> Some (ZB (xr = yr && xi = yi)) | OpNeq -> Some (ZB (xr <> yr || xi <> yi))
        | _ -> None
    | ZB x, ZB y ->
        match op with
        | OpAnd -> Some (ZB (x && y)) | OpOr -> Some (ZB (x || y))
        | OpEq -> Some (ZB (x = y)) | OpNeq -> Some (ZB (x <> y))
        | _ -> None
    | _ -> None

let private castTo (target: ElemType) (v: ZVal) : ZVal option =
    match v, target with
    | ZI (n, _), (ETInt32 | ETInt64) -> Some (ZI (wrapInt target n, target))
    | ZI (n, _), (ETFloat32 | ETFloat64) -> Some (ZF (roundTo target (float n), target))
    | ZI (n, _), (ETComplex64 | ETComplex128) -> Some (ZC (roundTo target (float n), 0.0, target))
    | ZF (x, _), (ETFloat32 | ETFloat64) -> Some (ZF (roundTo target x, target))
    | ZF (x, _), (ETComplex64 | ETComplex128) -> Some (ZC (roundTo target x, 0.0, target))
    | ZF (x, _), (ETInt32 | ETInt64) ->
        // a NaN / out-of-range conversion panics (BL8014): not decided here
        let lo, hi = if target = ETInt32 then -2147483648.0, 2147483647.0 else -9223372036854775808.0, 9223372036854775807.0
        if System.Double.IsNaN x || x < lo || x >= hi + 1.0 then None
        else Some (ZI (int64 (truncate x), target))
    | ZC (r, i, _), (ETComplex64 | ETComplex128) -> Some (ZC (roundTo target r, roundTo target i, target))
    | ZB b, (ETInt32 | ETInt64) -> Some (ZI ((if b then 1L else 0L), target))
    | ZB b, (ETFloat32 | ETFloat64) -> Some (ZF ((if b then 1.0 else 0.0), target))
    | ZB b, (ETComplex64 | ETComplex128) -> Some (ZC ((if b then 1.0 else 0.0), 0.0, target))
    | _ -> None

/// The compile-time value of a scalar expression, or None when it cannot be
/// decided (a run-time capture, an effect, a panic, a form not listed).
let rec private evalZ (t: Tables) (env: Map<IRId, ZVal>) (fuel: int ref) (depth: int) (e: TypedExpr) : ZVal option =
    fuel.Value <- fuel.Value - 1
    if fuel.Value < 0 || depth > 64 then None else
    let ev = evalZ t env fuel depth
    match e.Kind with
    | TExprLit lit ->
        match lit, elemOf e.Type with
        | LitInt n, Some et when isInt et -> Some (ZI (wrapInt et n, et))
        | LitInt n, Some et when isFloat et -> Some (ZF (roundTo et (float n), et))
        | LitFloat x, Some et when isFloat et -> Some (ZF (roundTo et x, et))
        | LitFloat x, Some et when isComplex et -> Some (ZC (roundTo et x, 0.0, et))
        | LitBool b, _ -> Some (ZB b)
        | _ -> None
    | TExprVar (_, id, _) ->
        match Map.tryFind id env with
        | Some v -> Some v
        | None -> constantOf t fuel depth id
    | TExprBinOp (Elementwise, op, a, b) ->
        match ev a with
        | None -> None
        | Some va ->
            // `&&` / `||` short-circuit as every lane evaluates them
            match op, va with
            | OpAnd, ZB false -> Some (ZB false)
            | OpOr, ZB true -> Some (ZB true)
            | _ -> ev b |> Option.bind (evalBin op va)
    | TExprUnaryOp (op, a) ->
        match op, ev a with
        | OpNeg, Some (ZI (n, et)) -> Some (ZI (wrapInt et (0L - n), et))
        | OpNeg, Some (ZF (x, et)) -> Some (ZF (-x, et))
        | OpNeg, Some (ZC (r, i, et)) -> Some (ZC (-r, -i, et))
        | OpNot, Some (ZB b) -> Some (ZB (not b))
        | OpConj, Some (ZC (r, i, et)) -> Some (ZC (r, -i, et))
        | OpConj, Some ((ZF _ | ZI _) as v) -> Some v
        | OpReal, Some (ZC (r, _, et)) -> Some (ZF (r, (if et = ETComplex64 then ETFloat32 else ETFloat64)))
        | OpImag, Some (ZC (_, i, et)) -> Some (ZF (i, (if et = ETComplex64 then ETFloat32 else ETFloat64)))
        | OpReal, Some ((ZF _) as v) -> Some v
        | OpMath "abs", Some (ZI (n, et)) -> Some (ZI (wrapInt et (abs n), et))
        | OpMath name, Some (ZF (x, et)) -> mathOne name x |> Option.map (fun r -> ZF (roundTo et r, et))
        | OpCast name, Some v -> castTargetOf name |> Option.bind (fun target -> castTo target v)
        | _ -> None
    | TExprIf (c, a, b) ->
        match ev c with
        | Some (ZB true) -> ev a
        | Some (ZB false) -> ev b
        | _ -> None
    | TExprLet (_, id, v, body) ->
        ev v |> Option.bind (fun x -> evalZ t (Map.add id x env) fuel depth body)
    | TExprBlock (stmts, Some final) ->
        let rec go env (ss: TypedStmt list) =
            match ss with
            | [] -> evalZ t env fuel depth final
            | TStmtLet b :: rest when List.isEmpty b.SubBindings ->
                evalZ t env fuel depth b.Value |> Option.bind (fun x -> go (Map.add b.VarId x env) rest)
            | _ -> None
        go env stmts
    | TExprComplexLit (re, im) ->
        match ev re, ev im, elemOf e.Type with
        | Some (ZF (r, _)), Some (ZF (i, _)), Some et when isComplex et -> Some (ZC (roundTo et r, roundTo et i, et))
        | _ -> None
    | TExprFma (a, b, c) ->
        match ev a, ev b, ev c with
        | Some (ZF (x, et)), Some (ZF (y, _)), Some (ZF (z, _)) -> Some (ZF (roundTo et (System.Math.FusedMultiplyAdd (x, y, z)), et))
        | _ -> None
    | TExprApp (f, args) ->
        match resolveStageLike t f with
        | Some (ps, body) when ps.Length = args.Length ->
            let vals = args |> List.map ev
            if vals |> List.forall Option.isSome then
                let env' = List.zip ps (vals |> List.map Option.get) |> List.fold (fun m (p, v) -> Map.add p v m) env
                evalZ t env' fuel (depth + 1) body
            else None
        | _ -> None
    | _ -> None

/// A callee the evaluator may enter: a user function or a never-reassigned
/// let-bound lambda.
and private resolveStageLike (t: Tables) (f: TypedExpr) : (IRId list * TypedExpr) option =
    match f.Kind with
    | TExprLambda info -> Some (info.Params |> List.map _.VarId, info.Body)
    | TExprVar (_, id, _) ->
        match t.Funcs.TryGetValue id with
        | true, fd -> Some (fd.Params |> List.map _.VarId, fd.Body)
        | _ ->
            if t.Mutable.Contains id || t.Assigned.ContainsKey id then None
            else
                match t.Lets.TryGetValue id with
                | true, ({ Kind = TExprLambda _ } as v) -> resolveStageLike t v
                | _ -> None
    | _ -> None

/// A capture's compile-time value: a binder never reassigned (not `let mut`,
/// no `x = e` anywhere) whose value is itself a compile-time constant.
and private constantOf (t: Tables) (fuel: int ref) (depth: int) (id: IRId) : ZVal option =
    if t.Mutable.Contains id || t.Assigned.ContainsKey id then None
    else
        match t.Lets.TryGetValue id with
        | true, v -> evalZ t Map.empty fuel (depth + 1) v
        | _ -> None

type private Z2 =
    | Z2Holds
    | Z2Moves of ZVal * ZVal         // the zero and what the stage maps it to
    | Z2Undecided

let private z2 (t: Tables) (stages: Stage list) (inputElem: ElemType option) : Z2 =
    match inputElem |> Option.bind zeroOf with
    | None -> Z2Undecided
    | Some zero ->
        let fuel = ref 10_000
        let result =
            stages |> List.fold (fun acc s ->
                acc |> Option.bind (fun v -> evalZ t (Map.ofList [ (s.Param, v) ]) fuel 0 s.Body)) (Some zero)
        match result with
        | Some v when isExactZero v -> Z2Holds
        | Some v -> Z2Moves (zero, v)
        | None -> Z2Undecided

// ---------------------------------------------------------------------------
// Z3: the stages that cannot change which cells are zero.
// ---------------------------------------------------------------------------

/// A numeric literal (a negated one included) and its magnitude.
let private literalValue (e: TypedExpr) : float option =
    match e.Kind with
    | TExprLit (LitFloat x) -> Some x
    | TExprLit (LitInt n) -> Some (float n)
    | TExprUnaryOp (OpNeg, { Kind = TExprLit (LitFloat x) }) -> Some (-x)
    | TExprUnaryOp (OpNeg, { Kind = TExprLit (LitInt n) }) -> Some (-(float n))
    | _ -> None

/// `v * c` is zero exactly when v is: a Float (or complex) product by a
/// finite c with |c| >= 1 cannot underflow; an Integer product by an ODD c is
/// a bijection modulo 2^n, so wrapping never reaches 0.
let private productKeepsZeroCells (operand: TypedExpr) (c: TypedExpr) : bool =
    match literalValue c, elemOf operand.Type with
    | Some x, Some et when isFloat et || isComplex et ->
        not (System.Double.IsNaN x || System.Double.IsInfinity x) && abs x >= 1.0
    | Some x, Some et when isInt et -> abs (System.Math.IEEERemainder (x, 2.0)) = 1.0
    | _ -> false

/// `v / c` is zero exactly when v is: a Float (or complex) quotient by
/// 0 < |c| <= 1 cannot underflow; an Integer quotient only by +1 / -1 (any
/// other divisor truncates a small cell to 0).
let private quotientKeepsZeroCells (operand: TypedExpr) (c: TypedExpr) : bool =
    match literalValue c, elemOf operand.Type with
    | Some x, Some et when isFloat et || isComplex et -> x <> 0.0 && abs x <= 1.0
    | Some x, Some et when isInt et -> x = 1.0 || x = -1.0
    | _ -> false

/// A cast that can produce neither a zero from a non-zero nor a non-zero
/// from a zero: widening, a real to a complex of at least its width, a Bool
/// to a number (false -> 0, true -> 1), the same type.
let private castKeepsZeroCells (source: ElemType option) (target: ElemType option) : bool =
    match source, target with
    | Some s, Some d when s = d -> true
    | Some ETInt32, Some ETInt64 -> true
    | Some (ETInt32 | ETInt64), Some (ETFloat32 | ETFloat64 | ETComplex64 | ETComplex128) -> true
    | Some ETFloat32, Some (ETFloat64 | ETComplex64 | ETComplex128) -> true
    | Some ETFloat64, Some ETComplex128 -> true
    | Some ETComplex64, Some ETComplex128 -> true
    | Some ETBool, Some (ETInt32 | ETInt64 | ETFloat32 | ETFloat64 | ETComplex64 | ETComplex128) -> true
    | _ -> false

/// THE WHITELIST (Z3): one exhaustive match over the stage body's forms in its
/// parameter x. Each admitted form is named; every other form -- any form
/// added to the language later included -- is refused by the default arm ON
/// PURPOSE, until someone proves it keeps zero cells zero and non-zero cells
/// non-zero and lists it here.
let rec private keepsZeroCells (t: Tables) (depth: int) (x: IRId) (e: TypedExpr) : bool =
    let keeps = keepsZeroCells t depth x
    match e.Kind with
    // the parameter itself
    | TExprVar (_, id, _) -> id = x
    // a braced body is its final expression
    | TExprBlock ([], Some final) -> keeps final
    // v * c and c * v
    | TExprBinOp (Elementwise, OpMul, a, c) when (literalValue c).IsSome ->
        productKeepsZeroCells a c && keeps a
    | TExprBinOp (Elementwise, OpMul, c, a) when (literalValue c).IsSome ->
        productKeepsZeroCells a c && keeps a
    // v / c
    | TExprBinOp (Elementwise, OpDiv, a, c) when (literalValue c).IsSome ->
        quotientKeepsZeroCells a c && keeps a
    // -v
    | TExprUnaryOp (OpNeg, a) -> keeps a
    // conj(v)
    | TExprUnaryOp (OpConj, a) -> keeps a
    // a cast that cannot create or remove a zero
    | TExprUnaryOp (OpCast name, a) ->
        castKeepsZeroCells (elemOf a.Type) (castTargetOf name) && keeps a
    // f(v) for a named stage f that is itself on the list: a composition
    | TExprApp (f, [ a ]) when depth < 16 ->
        match resolveStage t Set.empty f with
        | Some ss -> stageKeepsZeroCellsAt t (depth + 1) ss && keeps a
        | None -> false
    | _ -> false

and private stageKeepsZeroCellsAt (t: Tables) (depth: int) (stages: Stage list) : bool =
    stages |> List.forall (fun s ->
        (elemOf s.ParamType).IsSome && keepsZeroCells t depth s.Param s.Body)

let private stageKeepsZeroCells (t: Tables) (stages: Stage list) : bool = stageKeepsZeroCellsAt t 0 stages

// ---------------------------------------------------------------------------
// The judgment.
// ---------------------------------------------------------------------------

/// One stage applied over a reach of a guard / choice: its verdict.
type StageZeroHit = {
    Span: Span
    Stage: string
    Operand: string
    OverGuard: bool
    OverChoice: bool
    /// None: accepted; Some reason: refused
    Refusal: string option
}

let private judgeOne (t: Tables) (span: Span) (f: TypedExpr) (c: TypedExpr) : StageZeroHit option =
    let r = reach t Set.empty c
    if not (r.Guard || r.Choice) then None
    else
        let stage = stageName f
        let what = if r.Guard && r.Choice then "guard / choice" elif r.Guard then "guard" else "choice"
        let names = r.Forms |> List.filter _.StartsWith("`")
        let sites = r.Forms |> List.filter (fun s -> not (s.StartsWith "`"))
        let siteText =
            match sites with
            | [] -> $"the {what}"
            | [ s ] -> s
            | s :: rest -> s + " (holding " + String.concat ", " rest + ")"
        // `ch` (the choice at line 15:10, uncomputed) / the uncomputed guard at line 15:17
        let operand =
            match names with
            | n :: _ -> $"{n} ({siteText}, uncomputed)"
            | [] -> if siteText.StartsWith "the " then "the uncomputed " + siteText.Substring 4 else siteText
        let stages = resolveStage t Set.empty f
        let guardReason =
            if not r.Guard then None
            else
                match stages with
                | None -> Some $"{stage} is not one scalar function the compiler can evaluate at the zero"
                | Some ss ->
                    match z2 t ss (elemOf c.Type) with
                    | Z2Holds -> None
                    | Z2Moves (zero, v) when (match v with ZF (y, _) -> y = 0.0 | ZC (yr, yi, _) -> yr = 0.0 && yi = 0.0 | _ -> false) ->
                        Some $"{stage} maps the zero {show zero} to {show v}: the sign of the zero is observable (it prints {show v}), and where the guard is false the cell is {show zero}"
                    | Z2Moves (zero, v) ->
                        Some $"{stage} maps the zero {show zero} to {show v}, not to the zero of its result"
                    | Z2Undecided ->
                        Some $"{stage} cannot be evaluated at the zero at compile time (it reads a run-time value, or a form the compiler does not evaluate)"
        let choiceReason =
            if not r.Choice then None
            else
                match stages with
                | Some ss when stageKeepsZeroCells t ss -> None
                | _ -> Some $"{stage} is not a stage that provably keeps zero cells zero and non-zero cells non-zero"
        let refusal =
            match guardReason, choiceReason with
            | None, None -> None
            | g, ch ->
                let reasons = [ g; ch ] |> List.choose id |> String.concat "; and "
                let fName = match f.Kind with TExprVar (n, _, _) -> n | _ -> "f"
                let cName = match c.Kind with TExprVar (n, _, _) -> n | _ -> "c"
                let inside =
                    if r.Guard && r.Choice then $"inside the guard and on each side of the choice, `guard(p, {fName} <$> c)` / `({fName} <$> a) <|> ({fName} <$> b)`"
                    elif r.Guard then $"inside the guard, `guard(p, {fName} <$> c)`"
                    else $"on each side of the choice, `({fName} <$> a) <|> ({fName} <$> b)`"
                Some ($"the stage {stage} applied with `<$>` to {operand} is refused: its result would depend on whether the {what} is evaluated before the stage (formalism 10.4), because {reasons}. "
                      + $"Write the stage {inside}, or stage the computed value: `let v = {cName} |> compute`, then `{fName} <$> v`.")
        Some { Span = span; Stage = stage; Operand = operand; OverGuard = r.Guard; OverChoice = r.Choice; Refusal = refusal }

/// Every stage application of the program over an uncomputed guard or choice,
/// in source order, with its verdict.
let stageZeroHits (program: TypedProgram) : StageZeroHit list =
    let t = buildTables program
    let hits = System.Collections.Generic.List<StageZeroHit>()
    let seenSpans = System.Collections.Generic.HashSet<Span * string>()
    let rec walk (enclosing: Span) (e: TypedExpr) =
        let span = if e.Span = noSpan then enclosing else e.Span
        match e.Kind with
        | TExprFunctorMap (f, c) ->
            match judgeOne t span f c with
            // an array instance re-checks its origin's source: one verdict per site
            | Some h when seenSpans.Add ((h.Span, h.Stage)) -> hits.Add h
            | _ -> ()
        | _ -> ()
        for child in Blade.TypeCheckSupport.typedExprChildren e do walk span child
    for r in roots program do walk r.Span r
    List.ofSeq hits

/// THE REFUSAL (BL4022, at the stage application's span): the first stage of
/// the program, in source order, that fails its requirement.
let refuseZeroMovingStages (program: TypedProgram) : unit =
    match stageZeroHits program |> List.tryFind (fun h -> h.Refusal.IsSome) with
    | Some h ->
        raise (Blade.Diagnostics.BladeDiagnosticException
                (Blade.Diagnostics.mkError "BL4022" Blade.Diagnostics.PhConstraints h.Span h.Refusal.Value))
    | None -> ()
