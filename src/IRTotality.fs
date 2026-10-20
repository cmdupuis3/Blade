// The CLOSED purity/totality whitelist over IR bodies, lane-neutral: the ONE
// predicate both the C++ emitters (G35 row slots, G7 threaded joins:
// CodeGenBinding) and the optimizer (the sibling-fold pass's threaded-join
// admission: Optimize) apply, so a pre-codegen decision cannot admit a body the
// emitter later refuses. Parameterized by the callable resolver and the
// deferred-computation lookup each side owns.
module Blade.IRTotality

open Blade.Types
open Blade.IR

// THE ADMISSION is a CLOSED whitelist, because a team evaluates cells
// concurrently: `blade_rt::panic` is first-arriving-thread-wins
// (blade_runtime.hpp), so a body that can panic would report a racy failing
// coordinate, and a body that writes anything would race outright. Admitted:
// literals; level coordinates; immutable scalar and array reads; float
// `+ - * /` (IEEE, total) and integer `+ - *` (wrapping, total, formalism
// section 2.4); comparisons and `&&`/`||`; negation; libm intrinsics other
// than lgamma/digamma; `atan2`/`log_base`; casts other than float -> int;
// `if`, `let`, fma; subscripts PROVEN in range (every coordinate a level
// coordinate of exactly the subscripted index type -- a compact pool's group
// or a dense dimension); nested folds over `range<I>` sweeps of static extent;
// calls of resolved callables whose bodies pass the same whitelist. Anything
// else -- integer `/` `%`, a possibly negative integer `^`, a float -> int
// cast, lgamma/digamma, an unproven subscript, a write, an unresolved call,
// any array-level form -- declines with its name.

/// Literal integer arithmetic (the narrow evaluator IRPrint.tryEvalIntIR
/// implements; repeated here because this module compiles before IRPrint).
let rec evalInt (expr: IRExpr) : int64 option =
    match expr with
    | IRLit (IRLitInt n) -> Some n
    | IRBinOp (_, op, l, r, _) ->
        match evalInt l, evalInt r with
        | Some lv, Some rv ->
            match op with
            | IRAdd -> Some (lv + rv)
            | IRSub -> Some (lv - rv)
            | IRMul -> Some (lv * rv)
            | IRDiv when rv <> 0L -> Some (lv / rv)
            | IRMod when rv <> 0L -> Some (lv % rv)
            | _ -> None
        | _ -> None
    | IRUnaryOp (IRNeg, e) -> evalInt e |> Option.map (fun n -> -n)
    | _ -> None

/// The element type of a scalar (possibly index-tagged) IR type.
let rec scalarElem (t: IRType) : ElemType option =
    match t with
    | IRTScalar et -> Some et
    | IRTIdxTagged (inner, _) -> scalarElem inner
    | IRTUnitAnnotated (inner, _) -> scalarElem inner
    | _ -> None

let isFloat (et: ElemType) = (et = ETFloat64 || et = ETFloat32)
let isInt (et: ElemType) = (et = ETInt64 || et = ETInt32)
let isReal (et: ElemType) = isFloat et || isInt et

/// The unary math intrinsics audited TOTAL on real operands (libm in every
/// lane: a NaN or an infinity, never a panic). lgamma / digamma are refused
/// before this set is consulted; a name not listed declines.
let totalMath : Set<string> =
    Set.ofList [ "exp"; "log"; "log10"; "sqrt"; "sin"; "cos"; "tan"; "sinh"; "cosh"; "tanh"
                 "asin"; "acos"; "atan"; "floor"; "ceil"; "abs" ]

/// Two index records name the same index space: equal tags (or the same
/// anonymous record) and the same static extent.
let sameIndex (a: IRIndexType) (b: IRIndexType) : bool =
    let tagsAgree =
        match a.Tag, b.Tag with
        | Some x, Some y -> x = y
        | None, None -> a.Id = b.Id
        | _ -> false
    tagsAgree
    && (match evalInt a.Extent, evalInt b.Extent with
        | Some x, Some y -> x = y
        | _ -> false)

/// One plain `range<I>` operand of static extent: its index record.
let rangeIx (arr: IRExpr) : IRIndexType option =
    match arr with
    | IRRange ([ ix ], None) when ix.Rank = 1 && ix.Symmetry = SymNone && ix.IxKind = IxKPlain
                                  && (evalInt ix.Extent).IsSome -> Some ix
    | _ -> None

/// Saturating add/mul for the work count (a gate, never an exact census).
let private sat (x: int64) = if x < 0L || x > (System.Int64.MaxValue / 4L) then System.Int64.MaxValue / 4L else x
let addSat (a: int64) (b: int64) = sat (a + b)
let mulSat (a: int64) (b: int64) =
    if a = 0L || b = 0L then 0L
    elif a > (System.Int64.MaxValue / 4L) / b then System.Int64.MaxValue / 4L
    else sat (a * b)

/// The whitelist walk: Ok (inner fold steps) or Error (first refusal).
/// `resolve` resolves a callable reference; `deferredOf` looks a deferred
/// computation up by id (None: not one this side can see) and `isDeferred`
/// says whether an id IS one. `levels` maps a level coordinate (by VarId, and
/// by parameter name for the positional `IRParam` spelling) to its index
/// record; `bound` are let-bound and callee-parameter scalars; `visiting`
/// guards recursion through calls.
let rec walk (resolve: IRExpr -> IRCallable option) (deferredOf: IRId -> IRExpr option) (isDeferred: IRId -> bool)
             (levels: Map<IRId, IRIndexType>) (levelNames: Map<string, IRIndexType>)
             (bound: Set<IRId>) (boundNames: Set<string>) (visiting: Set<IRId>) (e: IRExpr) : Result<int64, string> =
    let go = walk resolve deferredOf isDeferred levels levelNames bound boundNames visiting
    let all (xs: IRExpr list) =
        xs |> List.fold (fun acc x ->
            match acc with
            | Error _ -> acc
            | Ok n -> (match go x with Ok m -> Ok (addSat n m) | Error why -> Error why)) (Ok 0L)
    let levelOf (x: IRExpr) =
        match x with
        | IRVar (id, _) -> Map.tryFind id levels
        | IRParam (nm, _, _) -> Map.tryFind nm levelNames
        | _ -> None
    let elemOf (x: IRExpr) = try scalarElem (typeOf x) with _ -> None
    match e with
    | IRLit _ -> Ok 0L
    | IRVar (id, _) when levels.ContainsKey id || bound.Contains id -> Ok 0L
    | IRVar (id, _) when isDeferred id -> Error "the body reads a deferred computation (its evaluation is not audited)"
    | IRVar (_, ty) ->
        (match scalarElem ty, ty with
         | Some _, _ -> Ok 0L
         | None, ArrayElem _ -> Ok 0L
         | _ -> Error "the body reads a value that is neither a scalar nor an array")
    | IRParam (nm, _, _) when levelNames.ContainsKey nm || boundNames.Contains nm -> Ok 0L
    | IRParam _ -> Error "the body reads a parameter the row-slot form cannot place"
    | IRBinOp (_, op, a, b, _) ->
        (match elemOf a, elemOf b with
         | Some ea, Some eb ->
             let refuse why = Error $"body may panic ({why})"
             let ok () = all [ a; b ]
             (match op with
              | IRAdd | IRSub | IRMul when (isFloat ea && isFloat eb) || (isInt ea && isInt eb) -> ok ()
              | IRDiv when isFloat ea && isFloat eb -> ok ()
              | IRDiv -> refuse "integer /"
              | IRMod -> refuse "%"
              | IRCaret when isReal ea && isReal eb ->
                  (match b with
                   | IRLit (IRLitFloat _ | IRLitFloat32 _) -> ok ()
                   | IRLit (IRLitInt n) when n >= 0L -> ok ()
                   | _ when isFloat eb -> ok ()
                   | _ -> refuse "integer ^ with a possibly negative exponent")
              | IREq | IRNeq | IRLt | IRLe | IRGt | IRGe | IRAnd | IROr -> ok ()
              // the binary math NAMES are enumerated: only these two are audited total
              | IRMath2 ("atan2" | "log_base") when isReal ea && isReal eb -> ok ()
              | _ -> Error $"the body applies `{(operatorFunctionName op)}` outside the row-slot whitelist")
         | _ -> Error "the body has array-level arithmetic (only scalar arithmetic is admitted)")
    | IRUnaryOp (op, a) ->
        // Every admitted constructor ENUMERATED, each on a real scalar
        // operand (bool for `!`); anything else -- complex accessors,
        // unaudited names, a non-scalar operand -- declines.
        let operand = elemOf a
        let real = (match operand with Some et -> isReal et | None -> false)
        let refuseOp () = Error $"the body applies `{(unaryFunctionName op)}` outside the row-slot whitelist"
        (match op with
         | IRMath ("lgamma" | "digamma" as f) -> Error $"body may panic ({f})"
         | IRCast ((ETInt64 | ETInt32), _) when (match operand with Some et -> not (isInt et) | None -> true) ->
             Error "body may panic (float -> int cast)"
         | IRNeg when real -> go a
         | IRNot when operand = Some ETBool -> go a
         | IRMath f when real && totalMath.Contains f -> go a
         | IRCast ((ETFloat64 | ETFloat32 | ETInt64 | ETInt32), _) when real -> go a
         | _ -> refuseOp ())
    | IRIf (c, a, b) -> all [ c; a; b ]
    | IRFma (a, b, c) -> all [ a; b; c ]
    | IRLet (id, v, body) ->
        (match go v with
         | Error why -> Error why
         | Ok n ->
             match walk resolve deferredOf isDeferred levels levelNames (bound.Add id) boundNames visiting body with
             | Ok m -> Ok (addSat n m)
             | Error why -> Error why)
    | IRIndex (IRVar (g, gty), idxs, _) when not (isDeferred g) ->
        let proven =
            match gty with
            | ArrayElem at when not at.IsVirtual ->
                (match at.IndexTypes with
                 // a compact pool: every coordinate a level of the group's index space
                 | [ gix ] when gix.Rank >= 2 ->
                     idxs.Length = gix.Rank
                     && idxs |> List.forall (fun x ->
                         match levelOf x with
                         | Some lix -> sameIndex gix lix
                         | None -> false)
                 // a dense array: a FULL subscript, dimension by dimension
                 | ixs when ixs.Length = idxs.Length && ixs |> List.forall (fun ix -> ix.Rank = 1) ->
                     List.forall2 (fun (ix: IRIndexType) x ->
                         match levelOf x with
                         | Some lix -> sameIndex ix lix
                         | None -> false) ixs idxs
                 | _ -> false)
            | _ -> false
        if proven then Ok 0L
        else Error "body may panic (a subscript not proven in range: BL8006)"
    | IRReduceCompute (comp, kernel, seed) ->
        (match kernel, seed with
         | IRTuple _, _ | _, IRTuple _ -> Error "the body joins several folds (a tuple fold is outside the row-slot whitelist)"
         | _ ->
         let comp' =
             match comp with
             | IRVar (id, _) -> deferredOf id |> Option.defaultValue comp
             | _ -> comp
         match comp' with
         | IRApplyCombinator inf when not inf.HasReynolds && inf.KernelOutputRank = 0 ->
             let ixs = inf.Arrays |> List.map rangeIx
             if ixs |> List.exists Option.isNone || ixs.IsEmpty then
                 Error "an inner fold is not over plain range<I> sweeps of static extent"
             else
             let ixs = ixs |> List.map Option.get
             match resolve inf.Kernel, resolve kernel with
             | Some mapCl, Some foldCl when mapCl.Params.Length = ixs.Length && foldCl.Params.Length = 2 ->
                 if visiting.Contains mapCl.Id || visiting.Contains foldCl.Id then
                     Error "the body recurses (a recursive callee is outside the row-slot whitelist)"
                 else
                 let cells =
                     ixs |> List.fold (fun acc ix -> mulSat acc (defaultArg (evalInt ix.Extent) 0L)) 1L
                 let levels' = List.fold2 (fun (m: Map<IRId, IRIndexType>) (p: IRParam) ix -> Map.add p.VarId ix m) levels mapCl.Params ixs
                 let names' = List.fold2 (fun (m: Map<string, IRIndexType>) (p: IRParam) ix -> Map.add p.Name ix m) levelNames mapCl.Params ixs
                 let visiting' = visiting.Add mapCl.Id
                 match walk resolve deferredOf isDeferred levels' names' bound boundNames visiting' mapCl.Body with
                 | Error why -> Error why
                 | Ok inner ->
                     let foldBound = foldCl.Params |> List.fold (fun (s: Set<IRId>) p -> s.Add p.VarId) bound
                     let foldNames = foldCl.Params |> List.fold (fun (s: Set<string>) p -> s.Add p.Name) boundNames
                     match walk resolve deferredOf isDeferred levels levelNames foldBound foldNames (visiting.Add foldCl.Id) foldCl.Body, go seed with
                     | Error why, _ | _, Error why -> Error why
                     | Ok _, Ok s -> Ok (addSat (mulSat cells (max 1L inner)) s)
             | _ -> Error "an inner fold's map or fold kernel is not a resolved callable"
         | _ -> Error "an inner fold is not over an unforced method_for(range<I>) application")
    | IRApp (f, args, _) ->
        (match resolve f with
         | Some cl when visiting.Contains cl.Id -> Error "the body recurses (a recursive callee is outside the row-slot whitelist)"
         | Some cl when cl.Params.Length = args.Length ->
             (match all args with
              | Error why -> Error why
              | Ok n ->
                  let cb = cl.Params |> List.fold (fun (s: Set<IRId>) p -> s.Add p.VarId) bound
                  let cn = cl.Params |> List.fold (fun (s: Set<string>) p -> s.Add p.Name) boundNames
                  match walk resolve deferredOf isDeferred levels levelNames cb cn (visiting.Add cl.Id) cl.Body with
                  | Ok m -> Ok (addSat n m)
                  | Error why -> Error why)
         | _ -> Error "the body calls a function that does not resolve to a callable")
    | other -> Error $"the body contains a form outside the row-slot whitelist ({other.GetType().Name})"

/// G7: one callable audited with its parameters bound as plain values, except
/// those listed in `levels` (a parameter bound to a plain `range<I>` operand,
/// whose subscripts into that index space are then proven).
let auditCallable (resolve: IRExpr -> IRCallable option) (deferredOf: IRId -> IRExpr option) (isDeferred: IRId -> bool)
                  (cl: IRCallable) (levels: (IRParam * IRIndexType) list) : Result<unit, string> =
    let lv = levels |> List.map (fun (p, ix) -> (p.VarId, ix)) |> Map.ofList
    let ln = levels |> List.map (fun (p, ix) -> (p.Name, ix)) |> Map.ofList
    let free = cl.Params |> List.filter (fun p -> not (lv.ContainsKey p.VarId))
    let b = free |> List.map (fun p -> p.VarId) |> Set.ofList
    let n = free |> List.map (fun p -> p.Name) |> Set.ofList
    match walk resolve deferredOf isDeferred lv ln b n (Set.singleton cl.Id) cl.Body with
    | Ok _ -> Ok ()
    | Error why -> Error why

/// G7: a map application's kernel audited, each parameter whose operand is a
/// plain `range<I>` bound to that level.
let auditApply (resolve: IRExpr -> IRCallable option) (deferredOf: IRId -> IRExpr option) (isDeferred: IRId -> bool)
               (info: ApplyInfo) : Result<unit, string> =
    match resolve info.Kernel with
    | Some cl ->
        let levels =
            if cl.Params.Length <> info.Arrays.Length then []
            else List.zip cl.Params info.Arrays |> List.choose (fun (p, a) -> rangeIx a |> Option.map (fun ix -> (p, ix)))
        auditCallable resolve deferredOf isDeferred cl levels
    | None -> Error "does not resolve to a callable (its effects cannot be audited)"
