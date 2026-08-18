// Body normalization for the sweeps: the pre-pass (pipelines, rec-arrays,
// reduces), bounded call inlining, parameter classification, and the AD-able-
// subset analysis/rejection checks (write-after-read, scalar overwrite, loop
// discipline).
module Blade.GradNormalize

open Blade.Ast
open Blade.GradCommon
open Blade.GradExpand
open Blade.GradFusion
open Blade.GradPackUnroll

/// The pre-pass proper: rewrite one function body's statements, expanding
/// recursive-array lets and hoisting reduces, threading an extent env so
/// reduce sources can recover their loop bound.
let internal preNormalizeBody (fname: string) (ctx: Ctx) (fd0: FunctionDecl) : Result<Expr, string> =
    // C7: pipelines first. Everything downstream -- the extent env, the
    // tangent walker, the reduce lowering -- then sees ordinary maps. A
    // pipeline the rewrite DECLINED is a refusal here rather than a
    // misleading BL5501 five seams later: the tangent walker has no other
    // rule to reach for.
    let fusedBody0, fuseDeclines = fuseFunctionBody ctx fd0
    // Then the grouped-peel lowering, which reads POST-fusion shapes and must
    // land before `hoistReduces` -- hoistReduces is what refuses the group
    // axis today, and it would refuse before ever seeing the peel.
    let fusedBody, gpDeclines = lowerGroupedPeels ctx fd0 fusedBody0
    let fd = { fd0 with Body = fusedBody }
    let declines = fuseDeclines @ gpDeclines
    if not (List.isEmpty declines) then err fname (List.head declines) else
    let paramExtents =
        fd.Params |> List.choose (fun p ->
            match p.Type with
            | Some t -> (match arrayLiteralExtents (resolveArrayTy ctx t) with Some (true, [n]) -> Some (p.Name, n) | _ -> None)
            | None -> None)
        |> Map.ofList
    let stmts0, finalOpt =
        match fd.Body.Kind with
        | ExprKind.ExprBlock (ss, fe) -> ss, fe
        | _ -> [], Some fd.Body
    // C7: rank-1 index types per named array, for the sort expansion's
    // `range<I>` and gather-lambda annotations. Seeded from the parameters
    // and extended by ANNOTATED locals -- the index type has to be declared
    // somewhere, since a sort's own result type does not carry it back.
    let paramIdxTys =
        fd.Params |> List.choose (fun p ->
            match p.Type |> Option.map (resolveTy ctx) with
            | Some (TyArray (_, [ity])) -> Some (p.Name, ity)
            | _ -> None)
        |> Map.ofList
    // Every name the SURFACE body binds: the sort expansion checks its
    // synthesized names against this before emitting them.
    let preBound = Set.union (surfaceBoundNames stmts0) (fd.Params |> List.map (fun p -> p.Name) |> Set.ofList)
    // A sort ANYWHERE other than directly as a `let` initializer has no
    // expansion site, so it is refused here where the message can say why
    // (rather than reaching the sweeps' generic "unsupported form").
    let noNestedSort (what: string) (e: Expr) : Result<unit, string> =
        if containsSort e then
            err fname (sprintf "differentiating `sort` requires it to be the whole initializer of a `let` (v1); %s contains a nested sort -- bind it with `let s = sort(...)` first" what)
        else Ok ()
    // ANNOTATED rank-1 array locals, collected in one pass over the surface
    // body rather than threaded through the fold: order does not matter here
    // (a sort can only name a binding already in scope), and over-collecting
    // only widens the key-closure refusal set, which is the safe direction.
    let idxTys =
        let rec collect (acc: Map<string, TypeExpr>) (ss: Stmt list) =
            ss |> List.fold (fun m s ->
                match unwrapStmt s with
                | StmtLet { Pattern = { Kind = PatternKind.PatVar nm }; Type = Some t } ->
                    (match resolveTy ctx t with
                     | TyArray (_, [ity]) -> Map.add nm ity m
                     | _ -> m)
                | StmtForIn (_, _, body) -> collect m body
                | _ -> m) acc
        collect paramIdxTys stmts0
    // Materialized index arrays already in the body -- see `SortPermForm`.
    let surfaceIotas =
        let rec collect (acc: Set<string>) (ss: Stmt list) =
            ss |> List.fold (fun m s ->
                match unwrapStmt s with
                | StmtLet { Pattern = { Kind = PatternKind.PatVar nm }; Value = IndexIota } -> Set.add nm m
                | StmtForIn (_, _, body) -> collect m body
                | _ -> m) acc
        collect Set.empty stmts0
    let rec goStmts (env: Map<string, int>) (ss: Stmt list) : Result<Map<string, int> * Stmt list, string> =
        ss |> List.fold (fun acc s ->
            acc |> Result.bind (fun (env, outp) ->
                match unwrapStmt s with
                // C7: the plumbing this pass itself emitted (recognized by
                // shape) rides through untouched -- a composition round
                // re-runs the pre-pass over an already-expanded body.
                | StmtLet { Value = SortPermForm surfaceIotas _ } -> Ok (env, outp @ [s])
                // C7: `let s = sort(A, key)` -- materialize the permutation
                // (and, in reverse mode, its inverse) BEFORE the unchanged
                // primal sort.
                | StmtLet ({ Value = { Kind = ExprKind.ExprSort (operand, key) }
                             Pattern = { Kind = PatternKind.PatVar nm } } as b) ->
                    expandSort fname ctx idxTys preBound nm operand key
                    |> Result.map (fun (plumbing, plan) ->
                        let env' =
                            match Map.tryFind plan.Src env with
                            | Some cnt -> Map.add nm cnt env
                            | None -> env
                        (env', outp @ plumbing @ [StmtLet b]))
                | StmtLet { Value = { Kind = ExprKind.ExprSort _ } } ->
                    err fname "differentiating `sort` requires it to bind a single name (v1)"
                | StmtLet { Value = { Kind = ExprKind.ExprRecArray def }; Type = Some annot; Pattern = { Kind = PatternKind.PatVar nm } } ->
                    expandRecArray fname ctx nm annot def
                    |> Result.map (fun (emitted, ext) -> (Map.add nm ext env, outp @ emitted))
                | StmtLet { Value = { Kind = ExprKind.ExprRecArray _ } } ->
                    err fname "recursive array must bind a single annotated name to be differentiable (v1)"
                | StmtLet ({ Pattern = { Kind = PatternKind.PatVar nm } } as b) ->
                    noNestedSort (sprintf "the initializer of '%s'" nm) b.Value |> Result.bind (fun () ->
                    hoistReduces fname ctx env b.Value |> Result.map (fun (pre, value') ->
                        let env' =
                            let byAnn =
                                match b.Type with
                                | Some t -> (match arrayLiteralExtents (resolveArrayTy ctx t) with Some (true, [n]) -> Some n | _ -> None)
                                | None -> None
                            let byLit = staticExtentOf ctx env value'
                            match (match byAnn with Some _ -> byAnn | None -> byLit) with
                            | Some cnt -> Map.add nm cnt env
                            | None -> env
                        (env', outp @ pre @ [StmtLet { b with Value = value' }])))
                | StmtLet b ->
                    noNestedSort "a let initializer" b.Value |> Result.bind (fun () ->
                    hoistReduces fname ctx env b.Value |> Result.map (fun (pre, value') ->
                        (env, outp @ pre @ [StmtLet { b with Value = value' }])))
                | StmtExpr ex ->
                    noNestedSort "this statement" ex |> Result.bind (fun () ->
                    hoistReduces fname ctx env ex |> Result.map (fun (pre, ex') ->
                        (env, outp @ pre @ [StmtExpr ex'])))
                | StmtAssign (lhs, op, rhs) ->
                    noNestedSort "this assignment" rhs |> Result.bind (fun () ->
                    hoistReduces fname ctx env rhs |> Result.map (fun (pre, rhs') ->
                        (env, outp @ pre @ [StmtAssign (lhs, op, rhs')])))
                | StmtForIn (var, range, body) ->
                    goStmts env body |> Result.map (fun (_, body') ->
                        (env, outp @ [StmtForIn (var, range, body')]))
                | StmtSpanned _ -> Ok (env, outp @ [s])))
            (Ok (env, []))
    goStmts paramExtents stmts0 |> Result.bind (fun (env, stmts') ->
        match finalOpt with
        | Some fe ->
            noNestedSort "the returned expression" fe |> Result.bind (fun () ->
            hoistReduces fname ctx env fe |> Result.map (fun (pre, fe') ->
                inheritSpan fd.Body (ExprBlock (stmts' @ pre, Some fe'))))
        | None ->
            Ok (inheritSpan fd.Body (ExprBlock (stmts', None))))

/// How deep call substitution may nest before the transform gives up. One
/// constant for BOTH inliners -- `normalizeBody`'s statement-level one and
/// `kernelCallBody`'s expression-level one -- because they cap the same
/// thing: a self-recursive callee has no finite substitution, and the cap is
/// the backstop for the chains the by-name recursion check cannot see.
let internal maxInlineDepth = 32

/// May this callee be substituted into differentiated code at all? One gate
/// for BOTH inliners -- `inlineCall`'s statement-level splice and
/// `kernelCallBody`'s expression-level substitution -- because the three
/// conditions are properties of the DECLARATION, not of the position it is
/// met in: a static function has no runtime body to differentiate, a
/// mut-parameter callee writes through its arguments (which neither sweep
/// tracks), and a call at the wrong arity has no parameter-to-argument
/// pairing to substitute. Each inliner keeps its own depth and recursion
/// caps, which ARE position-specific.
///
/// The messages are pinned by corpus `ERROR-CONTAINS` tests; they were
/// byte-identical in the two copies this replaces, which is exactly the
/// invariant a shared gate makes structural.
let internal checkInlinable (fname: string) (fd: FunctionDecl) (argCount: int)
    : Result<unit, string> =
    if fd.IsStatic then err fname (sprintf "cannot differentiate through static function '%s'" fd.Name)
    elif fd.Params |> List.exists (fun p -> p.Mutability = Mutable) then
        err fname (sprintf "cannot differentiate through '%s': mut-parameter functions are not inlinable (v1)" fd.Name)
    elif argCount <> fd.Params.Length then
        err fname (sprintf "'%s' called with %d arguments, expects %d" fd.Name argCount fd.Params.Length)
    else Ok ()

/// Normalize + inline a function body to the flat NStmt fragment:
/// all user calls inlined, all statements validated.
///
/// MEMOIZED per (callee, depth), which is what stops a helper CHAIN from
/// costing exponentially: `f` calling `g` twice, `g` calling `h` twice, is
/// four normalizations of `h` and 2^d at depth d, all of them producing the
/// same fragment. Every input that could make two normalizations of one
/// callee differ is fixed inside a single synthesis -- `Decls` (the memo
/// table is per-request, see `Ctx.NormMemo`), the top-level `fname` that
/// prefixes refusals, and `errMode` -- except `depth`, which is in the key:
/// it gates only the cap, so two calls at the SAME depth are interchangeable
/// while a deeper one must be allowed to hit the cap on its own.
///
/// Only successes are cached. Caching a refusal would be sound by the same
/// argument, but a refusal ends the synthesis anyway, so it would never be
/// read.
///
/// The arguments do not enter it: they bind AFTER normalization, through the
/// rename map `inlineCall` builds, which also gives the callee's binders a
/// call-site-unique `__in<N>_` prefix -- so one shared fragment cannot leak
/// a name from one site to another.
let rec internal normalizeBody (fname: string) (ctx: Ctx) (depth: int) (fd: FunctionDecl)
    : Result<NStmt list * Expr, string> =
    if depth > maxInlineDepth then
        err fname (sprintf "call inlining exceeded depth %d (recursive functions are not differentiable)" maxInlineDepth)
    else
    match ctx.NormMemo.TryGetValue ((fd.Name, depth)) with
    | true, hit -> Ok hit
    | _ ->
    normalizeBodyUncached fname ctx depth fd
    |> Result.map (fun r -> ctx.NormMemo.[(fd.Name, depth)] <- r; r)

and internal normalizeBodyUncached (fname: string) (ctx: Ctx) (depth: int) (fd: FunctionDecl)
    : Result<NStmt list * Expr, string> =
    // Lower the imperative-free surface constructs (recursive arrays, reduce)
    // into accumulation/construction statements before the NFor pipeline runs.
    preNormalizeBody fname ctx fd |> Result.bind (fun body' ->
    convertBody fname body' |> Result.bind (fun (stmts, finalE) ->
    // hoist calls inside the final expression too
    hoistCalls fname ctx finalE |> Result.bind (fun (finalHoist, finalE') ->
    let rec normStmts (ss: NStmt list) : Result<NStmt list, string> =
        ss |> traverseR (fun s ->
            match s with
            // DIRECT user-call let: the call is already in inlinable
            // position -- hoist only inside its ARGUMENTS, then inline.
            // (Hoisting the call itself would create `let tmp = f(..)`
            // and re-normalizing that let would hoist again, forever.)
            | NLet (name, isMut, { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar callee }, args) }) when Map.containsKey callee ctx.Decls ->
                let argsFolded =
                    args |> traverseR (hoistCalls fname ctx)
                    |> Result.map (fun pairs -> (pairs |> List.collect fst, pairs |> List.map snd))
                argsFolded |> Result.bind (fun (argHoists, args') ->
                normStmts argHoists |> Result.bind (fun argHoists' ->
                inlineCall fname ctx depth callee args' name isMut
                |> Result.map (fun inlined -> argHoists' @ inlined)))
            | NLet (name, isMut, value) ->
                hoistCalls fname ctx value |> Result.bind (fun (hoisted, value') ->
                // `hoisted` contains only direct-call lets, which the
                // arm above inlines without further hoisting.
                normStmts hoisted |> Result.map (fun hoisted' ->
                    hoisted' @ [NLet (name, isMut, value')]))
            | NAssign (lhs, rhs) ->
                hoistCalls fname ctx rhs |> Result.bind (fun (hoisted, rhs') ->
                normStmts hoisted |> Result.map (fun hoisted' ->
                    hoisted' @ [NAssign (lhs, rhs')]))
            | NFor (var, lo, hi, body) ->
                normStmts body |> Result.map (fun body' ->
                    [NFor (var, lo, hi, body')]))
        |> Result.map List.concat
    normStmts (stmts @ finalHoist) |> Result.map (fun ns -> (ns, finalE')))))

/// Inline `let target = callee(args)`: bind arguments to fresh param names,
/// splice the callee's own normalized body with all its binders renamed,
/// and bind the callee's final expression to `target` (or rename in place
/// when the final expression is just a local variable -- avoiding an array
/// alias, which the adjoint bookkeeping cannot track).
and internal inlineCall (fname: string) (ctx: Ctx) (depth: int)
                       (callee: string) (args: Expr list)
                       (target: string) (targetMut: bool)
    : Result<NStmt list, string> =
    let fd = ctx.Decls.[callee]
    checkInlinable fname fd args.Length |> Result.bind (fun () ->
    normalizeBody fname ctx (depth + 1) fd |> Result.bind (fun (calleeStmts, calleeFinal) ->
        let tag = fresh ctx "__in"
        // Param binding: plain-var arguments bind by RENAMING the callee
        // param to the caller's variable (no let) -- this is what routes
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
        // Rename refusals (binder capture) carry the mode prefix via `err`
        // so the expand boundary codes them BL5500/BL5501 correctly.
        let viaErr res = match res with Ok v -> Ok v | Error m -> err fname m
        viaErr (renameNStmts ren calleeStmts) |> Result.bind (fun renStmts ->
        viaErr (renameExpr ren calleeFinal) |> Result.bind (fun renFinal ->
        let paramLets =
            paramBinds |> List.choose (fun (_, target, argOpt) ->
                argOpt |> Option.map (fun a -> NLet (target, false, a)))
        match renFinal.Kind with
        | ExprKind.ExprVar localName when (localRen |> Map.exists (fun _ v2 -> v2 = localName)) ->
            // Final expr is a callee-local: rename that local to the target
            // name instead of emitting `let target = local` (array-alias).
            let ren2 = Map.ofList [(localName, target)]
            viaErr (renameNStmts ren2 renStmts) |> Result.map (fun renStmts2 -> paramLets @ renStmts2)
        | _ ->
            Ok (paramLets @ renStmts @ [NLet (target, targetMut, renFinal)])))))

// Classification: differentiable variables, array-ness

let internal isFloatTy (t: TypeExpr) : bool =
    match t with
    | TyFloat64 | TyFloat32 -> true
    | TyNamed (("Float" | "Float64" | "Float32"), []) -> true
    | _ -> false

type internal ParamClass =
    | DiffArray
    | DiffScalar
    | NonDiff

/// Classify one parameter. Unit-carrying Floats and complex types get
/// EXPLICIT refusals rather than the NonDiff fall-through: silently treating
/// `y: Float<meters>` as non-differentiable drops its partial from the
/// gradient with no diagnostic -- the same wrong-answer class as an
/// unknown-derivative intrinsic, and worse than refusing.
let internal classifyParam (fname: string) (ctx: Ctx) (p: ParamDecl) : Result<ParamClass, string> =
    match p.Type with
    | None -> err fname (sprintf "parameter '%s' must have a type annotation" p.Name)
    | Some t0 ->
        let refuseUnits (what: string) =
            err fname (sprintf "parameter '%s' %s: unit-carrying parameters are not differentiable (v1) -- a gradient's units are <loss>/<parameter>, which the grad ABI (buffer type = parameter type) cannot express; strip the unit at the call boundary or compute the unit-carrying part outside the differentiated function" p.Name what)
        let refuseComplex (what: string) =
            err fname (sprintf "parameter '%s' %s: complex parameters are not differentiable (v1); complex derivatives need a holomorphic/Wirtinger convention the AD subset does not define" p.Name what)
        let t = resolveTy ctx t0
        match t with
        | _ when isFloatTy t -> Ok DiffScalar
        | TyNamed (("Float" | "Float64" | "Float32"), _ :: _) ->
            // FORWARD mode supports units correctly for free: a tangent has
            // the primal's type verbatim, units included. Reverse cannot --
            // a gradient's units are <loss>/<param>, which the grad ABI
            // (buffer type = parameter type) cannot express.
            if errMode.Value = "jvp" then Ok DiffScalar else refuseUnits "carries units"
        | TyComplex64 | TyComplex128 | TyNamed (("Complex64" | "Complex128"), _) -> refuseComplex "is complex"
        | TyArray (elem, _) ->
            let el = resolveTy ctx elem
            if isFloatTy el then Ok DiffArray
            else
                (match el with
                 | TyNamed (("Float" | "Float64" | "Float32"), _ :: _) ->
                     if errMode.Value = "jvp" then Ok DiffArray else refuseUnits "is an array of unit-carrying Floats"
                 | TyComplex64 | TyComplex128 | TyNamed (("Complex64" | "Complex128"), _) -> refuseComplex "is a complex array"
                 | _ -> Ok NonDiff)
        | _ -> Ok NonDiff

/// Zero value matching an array literal's (or constant fill's) shape.
let rec internal zerosLikeLiteral (e: Expr) : Expr option =
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
/// literal extents in v1 -- callers allocate those buffers, so this is only
/// used for LOCAL d-vars, which come from literals instead).
let internal zerosOfType (fname: string) (t: TypeExpr) : Result<Expr, string> =
    let rec go t =
        match t with
        | _ when isFloatTy t -> Ok (fLit 0.0)
        | TyArray (elem, idxs) ->
            let folded =
                idxs |> traverseR (fun ix ->
                    match ix with
                    | TyIdx { Kind = ExprKind.ExprLit (LitInt n) } -> Ok (int n)
                    | _ -> err fname "differentiable arrays need literal Idx<n> extents (v1)")
            folded |> Result.bind (fun ns ->
                go elem |> Result.map (fun z ->
                    ns |> List.rev |> List.fold (fun inner n -> syn (ExprArrayLit (List.replicate n inner))) z))
        | _ -> err fname "cannot build a zero cotangent for this type"
    go t

/// Partials of the two-argument intrinsics, per operand -- the binary sibling
/// of `derivRule`, whose signature (name -> Expr -> Expr option) is
/// structurally unary and has nowhere to put a second operand. Returns
/// (d/dFirst, d/dSecond) as expressions of the FORWARD operands; both AD
/// modes consume it the same way (reverse multiplies by the cotangent,
/// forward by the operand tangents).
///   atan2(y, x): d/dy =  x/(x^2+y^2),  d/dx = -y/(x^2+y^2)
///   log_base(x, b) = log x / log b:
///                    d/dx = 1/(x log b),  d/db = -log x/(b (log b)^2)
let internal binaryDerivRule (name: string) (a: Expr) (b: Expr) : Expr * Expr =
    match name with
    | "atan2" ->
        let denom = add (mul a a) (mul b b)
        (div b denom, neg (div a denom))
    | _ ->  // log_base(x, b)
        let lb = call "log" [b]
        (div (fLit 1.0) (mul a lb),
         neg (div (call "log" [a]) (mul b (mul lb lb))))

// Taint analysis

/// Variables carrying differentiable (Float) dataflow, and which of those
/// are arrays. Two passes for loop-carried taint.
let internal analyze (fname: string) (ctx: Ctx)
                    (diffParams: Set<string>) (arrayParams: Set<string>)
                    (stmts: NStmt list) (finalE: Expr)
    : Result<Set<string> * Set<string>, string> =
    let mutable diff = diffParams
    let mutable arrays = arrayParams
    let touches (e: Expr) : Result<bool, string> =
        let mutable hit = false
        walkExpr fname ctx (fun n -> if Set.contains n diff then hit <- true) false e
        |> Result.map (fun () -> hit)
    let rec pass (ss: NStmt list) : Result<unit, string> =
        ss |> iterR (fun s ->
            match s with
            | NLet (name, _, value) ->
                (match value with
                 | { Kind = ExprKind.ExprArrayLit _ } | ConstFill _ -> arrays <- Set.add name arrays
                 | { Kind = ExprKind.ExprVar src } when Set.contains src arrays ->
                     arrays <- Set.add name arrays
                 // C1: a local bound to a linear combinator is an array
                 // if the form produces one -- without this its element
                 // reads would silently yield no tangent.
                 | _ when producesArray arrays value -> arrays <- Set.add name arrays
                 | _ -> ())
                // FLOAT array literals are differentiable carriers even
                // before any diff flow reaches them (their cotangents
                // must exist). Int-literal tables (index/offset data,
                // e.g. ML-elaboration path tables) are not -- their
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
            | NFor (_, _, _, body) -> pass body)
    pass stmts
    |> Result.bind (fun () -> pass stmts)     // second pass: loop-carried
    |> Result.bind (fun () -> touches finalE)
    |> Result.bind (fun finalTouches ->
        if not finalTouches then
            err fname "the returned value does not depend on any differentiable parameter"
        else Ok (diff, arrays))

// Restriction checks (accumulator discipline)

/// Names assigned (either form) anywhere in a statement list.
let rec internal assignedNames (stmts: NStmt list) : Set<string> =
    stmts |> List.fold (fun acc s ->
        match s with
        | NLet _ -> acc
        | NAssign ({ Kind = ExprKind.ExprVar n }, _) -> Set.add n acc
        | NAssign ({ Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar a }, _) }, _) -> Set.add a acc
        | NAssign _ -> acc
        | NFor (_, _, _, body) -> Set.union acc (assignedNames body)) Set.empty

/// `x = x + e` / `x = x - e` (and element forms), the additive-self
/// pattern, which the same-direction adjoint loop handles exactly.
/// Returns (sign, e) when matched: +1.0 for add; -1.0 for sub.
let internal additiveSelf (lhs: Expr) (rhs: Expr) : (float * Expr) option =
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
/// lhs occurrence is exempt (its derivative is 1 regardless of value).
let internal checkWriteAfterRead (fname: string) (ctx: Ctx) (stmts: NStmt list) : Result<unit, string> =
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
                | _ -> ()) false e
        |> Result.bind (fun () ->
            match bad with
            | Some n -> err fname (sprintf "'%s' is read here but written again later; the reverse sweep re-evaluates forward expressions at FINAL values, so read-then-rewrite of a mutable is not differentiable (bind a fresh let instead)" n)
            | None -> Ok ())
    stmts |> List.mapi (fun i s -> (i, s)) |> iterR (fun (i, s) ->
        match s with
        | NLet (_, _, value) -> checkExprAt i value
        | NAssign (lhs, rhs) ->
            (match additiveSelf lhs rhs with
             | Some (_, e) -> checkExprAt i e
             | None -> checkExprAt i rhs)
            |> Result.bind (fun () ->
                // index expressions of an element write
                match lhs.Kind with
                | ExprKind.ExprApp (_, idxs) -> idxs |> iterR (checkExprAt i)
                | _ -> Ok ())
        | NFor (_, lo, hi, body) ->
            checkExprAt i lo
            |> Result.bind (fun () -> checkExprAt i hi)
            |> Result.bind (fun () ->
                // expressions INSIDE the loop must not read vars written
                // after the loop either
                let rec checkBody ss =
                    ss |> iterR (fun s2 ->
                        match s2 with
                        | NLet (_, _, value) -> checkExprAt i value
                        | NAssign (l2, r2) ->
                            (match additiveSelf l2 r2 with
                             | Some (_, e) -> checkExprAt i e
                             | None -> checkExprAt i r2)
                        | NFor (_, l2, h2, b2) ->
                            checkExprAt i l2
                            |> Result.bind (fun () -> checkExprAt i h2)
                            |> Result.bind (fun () -> checkBody b2))
                checkBody body))

/// Non-additive reassignment of a differentiable SCALAR is rejected
/// everywhere: its adjoint needs the pre-statement value, which the
/// re-evaluating reverse sweep cannot see. (Array ELEMENT writes stay legal
/// as construction; their adjoints never read the overwritten value.)
let internal checkNoScalarOverwrite (fname: string) (diff: Set<string>) (stmts: NStmt list) : Result<unit, string> =
    let rec check ss =
        ss |> iterR (fun s ->
            match s with
            | NAssign (({ Kind = ExprKind.ExprVar x } as lhs), rhs) when Set.contains x diff ->
                (match additiveSelf lhs rhs with
                 | Some _ -> Ok ()
                 | None -> err fname (sprintf "non-additive reassignment of '%s' is not differentiable (the reverse sweep sees final values); bind a fresh `let` instead" x))
            | NFor (_, _, _, body) -> check body
            | _ -> Ok ())
    check stmts

/// Inside a loop, expressions may not READ accumulators mutated in the same
/// loop UNLESS the accumulator is DECLARED in that same loop body: the
/// adjoint loop replays the whole body, reconstructing loop-local values
/// per iteration, so reads of loop-local accumulators are exact.
/// Accumulators that OUTLIVE the loop have unrecoverable mid-iteration
/// values and stay read-banned. The additive-self lhs occurrence is exempt.
let internal checkLoopDiscipline (fname: string) (ctx: Ctx) (loops: NStmt list) : Result<unit, string> =
    let rec check (ss: NStmt list) (inLoop: bool) (loopAccums: Set<string>) : Result<unit, string> =
        ss |> iterR (fun s ->
            match s with
            | NLet (_, _, value) when inLoop ->
                let mutable bad = None
                walkExpr fname ctx (fun n -> if Set.contains n loopAccums && bad.IsNone then bad <- Some n) false value
                |> Result.bind (fun () ->
                    match bad with
                    | Some n -> err fname (sprintf "loop-body let reads accumulator '%s' mutated in the same loop (mid-iteration values are not recoverable; restructure)" n)
                    | None -> Ok ())
            | NLet _ -> Ok ()
            | NAssign (lhs, rhs) when inLoop ->
                (match additiveSelf lhs rhs with
                 | Some (_, e) ->
                     let mutable bad = None
                     walkExpr fname ctx (fun n -> if Set.contains n loopAccums && bad.IsNone then bad <- Some n) false e
                     |> Result.bind (fun () ->
                         match bad with
                         | Some n -> err fname (sprintf "accumulation reads accumulator '%s' mutated in the same loop; restructure" n)
                         | None -> Ok ())
                 | None ->
                     match lhs.Kind with
                     | ExprKind.ExprVar x ->
                         err fname (sprintf "loop-carried reassignment of '%s' is not additive (`%s = %s +/- e`); only additive accumulation is differentiable in loops (v1)" x x x)
                     | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar a }, _) ->
                         // plain element write inside a loop: allowed as
                         // construction, but the rhs may not read the
                         // array being written (array recurrence)
                         let mutable bad = false
                         walkExpr fname ctx (fun n -> if n = a then bad <- true) false rhs
                         |> Result.bind (fun () ->
                             if bad then err fname (sprintf "array recurrence on '%s' (element write whose rhs reads the same array) is not differentiable (v1)" a)
                             else Ok ())
                     | _ -> err fname "unsupported assignment target")
            | NAssign _ -> Ok ()
            | NFor (_, _, _, body) ->
                // loop-local declarations are replay-reconstructed --
                // exclude them from the read ban
                let declared = boundNames body |> Set.ofList
                let accums = Set.difference (assignedNames body) declared
                check body true (if inLoop then Set.union loopAccums accums else accums))
    check loops false Set.empty

