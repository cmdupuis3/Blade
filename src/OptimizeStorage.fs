// Shared proofs of the optimizer: the module traversal roots, storage proofs, the closed total-scalar whitelists and the reference census (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeStorage

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis

/// A root of THE MODULE TRAVERSAL that the compaction census (T4b) and the
/// foreign-demand collection (`moduleReferencedIds`, which Lowering hands to
/// every other module's census) share: every executable expression of the
/// module -- binding values, the side-table operands executed at a binding's
/// site, function bodies -- every declared type, and every side table that
/// names a binding by id or by name.
type CompactionRoot =
    | RootBindingValue of IRBinding
    | RootSideOperand of IRBinding * string * IRExpr
    | RootFunctionBody of IRCallable
    | RootType of string * IRType
    | RootSourceRef of string * IRId
    | RootNameRef of string * string

let compactionRoots (callables: IRCallable list) (modul: IRModule) : CompactionRoot list =
    let byId = modul.Bindings |> List.map (fun b -> (b.Id, b)) |> Map.ofList
    let sideLabel (id: IRId) =
        if modul.CompoundInits.ContainsKey id then "a compound() operand"
        elif modul.SparseInits.ContainsKey id then "a sparse() operand"
        else "a random fill's key, parameter, weights or address"
    [ for td in modul.Types do
        match td with
        | IRTDAlias (n, t) -> yield RootType ($"type `{n}`", t)
        | IRTDStruct (n, fs) ->
            for (_, t) in fs do yield RootType ($"struct `{n}`", t)
        | IRTDVariant (n, vs) ->
            for (_, t) in vs do
                match t with Some t -> yield RootType ($"variant `{n}`", t) | None -> ()
        | IRTDIndexType (n, ix) | IRTDEnumIdx (n, ix, _) ->
            yield RootType ($"index type `{n}`", IRTArrow ([ SIdx ix ], IRTUnit, None))
      for b in modul.Bindings do
        yield RootBindingValue b
        for op in bindingSideOperands modul b.Id do yield RootSideOperand (b, sideLabel b.Id, op)
        yield RootType ($"`{b.Name}`", b.Type)
      for f in callables do
        yield RootFunctionBody f
        for p in f.Params do yield RootType ($"`{f.SourceName}`", p.Type)
        yield RootType ($"`{f.SourceName}`", f.RetType)
        for c in f.Captures do yield RootType ($"`{f.SourceName}`", c.Type)
      for KeyValue (wid, w) in modul.ProviderWrites do
        let who = match Map.tryFind wid byId with Some b -> $" (`{b.Name}`)" | None -> ""
        yield RootSourceRef ($"a provider write{who}", w.SourceId)
      for KeyValue (_, r) in modul.ProviderReads do
        match r.MaskName with
        | Some mn -> yield RootNameRef ("a provider read masked by", mn)
        | None -> () ]

/// Every binding / callable id this module's executable code, types and side
/// tables name (`compactionRoots` over the module's own functions): what
/// another module's census must treat as an uncertified use when one of them
/// is its compound.
let moduleReferencedIds (modul: IRModule) : Set<IRId> =
    let acc = System.Collections.Generic.HashSet<IRId>()
    let addExpr (e: IRExpr) =
        for id in collectVarRefsIR e do acc.Add id |> ignore
        for x in embeddedExtentExprs e do
            for id in collectVarRefsIR x do acc.Add id |> ignore
    let addTy (t: IRType) =
        for x in irTypeExtentExprs t do addExpr x
    for root in compactionRoots modul.Functions modul do
        match root with
        | RootBindingValue b -> addExpr b.Value
        | RootSideOperand (_, _, e) -> addExpr e
        | RootFunctionBody f ->
            addExpr f.Body
            for c in f.Captures do acc.Add c.Id |> ignore
        | RootType (_, t) -> addTy t
        | RootSourceRef (_, id) -> acc.Add id |> ignore
        | RootNameRef _ -> ()
    Set.ofSeq acc
/// THE POSITIVE STORAGE PROOF of a top-level array binding (Codex diff review
/// of W2-1, finding 2; shared since W2-2's fix round with compaction fusion's
/// shared selection, whose joint pass reads every source's pool by name): the
/// cells must EXIST as a stored pool at the binding's own site. Membership
/// hoisting applies it to a probed set, the shared selection to the mask
/// source and every companion source. Returns how the cells are stored and
/// the alias chain (the named binding first, the root last).
let storedArrayProof (modul: IRModule) : IRBinding -> Result<string * IRBinding list, string> =
    let arr = List.toArray modul.Bindings
    let indexOf = arr |> Array.mapi (fun i b -> (b.Id, i)) |> Map.ofArray
    let isPlaceholder (b: IRBinding) =
        modul.CompoundInits.ContainsKey b.Id || modul.SparseInits.ContainsKey b.Id || modul.RandomInits.ContainsKey b.Id
    // THE POSITIVE STORAGE PROOF of a set at the build point (Codex diff
    // review of W2-1, finding 2): the set's cells must EXIST, as a stored
    // pool, before the host's first statement -- so its value must be one of
    // the forms that are materialized at their own site (an array literal, a
    // `|> compute`, a compound, an eager set operation, a call's array
    // result, a non-streamed provider read), reached through any chain of
    // top-level let ALIASES (`let B = A`, each hop an earlier binding;
    // `isDeferredForm` of an alias's own value -- an IRVar -- proves nothing
    // about the cells: the C++ lane forces and deep-copies an ASSIGNABLE
    // alias of a deferred map at the alias's site, the ordinary `let B =
    // lazy` case, but propagates the deferral through a non-assignable one,
    // so v1 proves the root and declines a deferred root either way). A virtual
    // array (a range), a deferred map (directly or through an alias), a
    // streamed read and every other form are not proved stored. Returns how
    // the cells are stored and the chain (the named set first, the root
    // last).
    let rec storageOf (seen: Set<IRId>) (b: IRBinding) : Result<string * IRBinding list, string> =
        let isVirtual =
            match stripUnits b.Type with
            | ArrayElem at -> at.IsVirtual
            | _ -> false
        if isVirtual then Error $"`{b.Name}` is a virtual array (it has no stored cells)"
        else
        match Map.tryFind b.Id modul.ProviderReads with
        | Some r when r.Streamed -> Error $"`{b.Name}` is a streamed provider read (never materialized)"
        | Some _ -> Ok ("a provider read, materialized at its site", [ b ])
        | None ->
        if modul.CompoundInits.ContainsKey b.Id then Ok ("a compound, built at its site", [ b ])
        elif isPlaceholder b then Error $"`{b.Name}` is a side-table value v1 does not prove stored"
        else
        match b.Value with
        | IRVar (aid, _) ->
            match Map.tryFind aid indexOf, Map.tryFind b.Id indexOf with
            | Some ai, Some bi when ai < bi && not (seen.Contains aid) ->
                match storageOf (seen.Add b.Id) arr.[ai] with
                | Ok (how, chain) -> Ok ($"an alias of `{arr.[ai].Name}`, {how}", b :: chain)
                | Error why -> Error $"`{b.Name}` is an alias of `{arr.[ai].Name}`, and {why}"
            | _ -> Error $"`{b.Name}` is an alias of a value that is not an earlier top-level binding"
        | v when isDeferredForm v -> Error $"`{b.Name}` is a deferred computation"
        | IRArrayLit _ -> Ok ("an array literal", [ b ])
        | IRCompute _ -> Ok ("a `|> compute`", [ b ])
        | IRMask _ | IRSort _ | IRUnique _ | IRIntersect _ | IRUnion _ -> Ok ("an eager set operation", [ b ])
        | IRApp _ -> Ok ("a call's array result", [ b ])
        | _ -> Error $"`{b.Name}`'s value is not a form v1 proves stored (an array literal, `|> compute`, a compound, an eager set operation, a call's result or a provider read, through aliases)"
    storageOf Set.empty

/// A PURE, TOTAL scalar body over the parameters `ps`: literals, the
/// parameters, arithmetic that cannot trap (no integer `/` `%` `^`, no
/// float->int cast), comparisons, logic, `if`. Anything else -- a call, a
/// capture, an intrinsic that may be domain-checked -- is not judged (false).
/// ONE closed whitelist, shared by compaction fusion's predicate admission
/// (one parameter) and the hash equijoin's kernel admission (two).
let totalScalarOver (ps: IRParam list) (e: IRExpr) : bool =
    let isP (x: IRExpr) =
        ps |> List.exists (fun p ->
            match x with
            | IRVar (v, _) -> v = p.VarId
            | IRParam (_, i, _) -> i = p.Index
            | _ -> false)
    let isFloatTy (t: IRType) =
        match stripUnits t with
        | IRTScalar (ETFloat64 | ETFloat32) -> true
        | _ -> false
    let rec go (e: IRExpr) =
        match e with
        | _ when isP e -> true
        | IRLit _ -> true
        | IRBinOp (_, op, l, r, _) ->
            let opOk =
                match op with
                | IRAdd | IRSub | IRMul | IREq | IRNeq | IRLt | IRLe | IRGt | IRGe | IRAnd | IROr -> true
                | IRDiv -> isFloatTy (typeOf l) && isFloatTy (typeOf r)
                | _ -> false
            opOk && go l && go r
        | IRUnaryOp ((IRNeg | IRNot), x) -> go x
        | IRIf (c, t, f) -> go c && go t && go f
        | _ -> false
    go e

/// THE REFERENCE CENSUS of one module binding `x` (round 4 wave 5, lane D:
/// stream-mask-replay v1, sort-terminal v1): every root of the module
/// traversal (`compactionRoots`: binding values, side-table operands,
/// function bodies and capture ids, declared / binding / parameter / capture
/// types, provider write sources, masked reads by name), each reference
/// counted through the type-carried extents too (`embeddedExtentExprs`,
/// `irTypeExtentExprs`). Returns (root label, count) for every root naming x;
/// a binding's value root is labelled `` `name` ``, a side operand
/// `` `name` (label) ``, a type `the type of ...`.
let referenceCensus (callables: IRCallable list) (modul: IRModule) (x: IRId) (xName: string) : (string * int) list =
    let refsIn (e: IRExpr) =
        let mutable n = 0
        iterIRExpr (fun m -> match m with IRVar (id, _) when id = x -> n <- n + 1 | _ -> ()) e
        n
    let refsDeep (e: IRExpr) = refsIn e + (embeddedExtentExprs e |> List.sumBy refsIn)
    let refsTy (t: IRType) = irTypeExtentExprs t |> List.sumBy refsDeep
    [ for root in compactionRoots callables modul do
        match root with
        | RootBindingValue b -> yield ($"`{b.Name}`", refsDeep b.Value)
        | RootSideOperand (b, label, e) -> yield ($"`{b.Name}` ({label})", refsDeep e)
        | RootFunctionBody f ->
            yield ($"function `{f.SourceName}`", refsDeep f.Body + (f.Captures |> List.filter (fun c -> c.Id = x) |> List.length))
        | RootType (who, t) -> yield ($"the type of {who}", refsTy t)
        | RootSourceRef (who, id) -> yield (who, (if id = x then 1 else 0))
        | RootNameRef (who, nm) -> yield (who, (if nm = xName then 1 else 0)) ]
    |> List.filter (fun (_, n) -> n > 0)

/// THE ALLOCATION-FREE SCALAR CERTIFICATE (D2 / G143, Codex design review
/// rounds 1-2): a closed whitelist over an expression whose every node is a
/// real or Bool scalar that evaluates with no allocation, no call and no
/// failure -- non-string literals, the allowed variables (`okVar`), unary
/// negation / not, the `totalScalarOver` binary operator set (integer
/// `+ - *` wrap, float `+ - * /`, comparisons, `&&` `||`), and `if` of those.
/// A string literal (a `std::string`), a call, a tuple, an array, a let
/// block or a cast is refused.
let allocFreeScalar (okVar: IRExpr -> bool) (e: IRExpr) : bool =
    let rec realOrBool (t: IRType) =
        match stripUnits t with
        | IRTScalar (ETFloat64 | ETFloat32 | ETInt64 | ETInt32 | ETBool) -> true
        // an index label is judged by its inner type (a tagged Int64 is an Int64)
        | IRTIdxTagged (inner, _) -> realOrBool inner
        | _ -> false
    let isFloatTy (t: IRType) =
        match stripUnits t with
        | IRTScalar (ETFloat64 | ETFloat32) -> true
        | _ -> false
    let rec go (e: IRExpr) =
        let tyOk = (try realOrBool (typeOf e) with _ -> false)
        tyOk &&
        match e with
        | IRLit (IRLitInt _ | IRLitFloat _ | IRLitFloat32 _ | IRLitBool _) -> true
        | IRVar _ | IRParam _ -> okVar e
        | IRBinOp (_, op, l, r, _) ->
            let opOk =
                match op with
                | IRAdd | IRSub | IRMul | IREq | IRNeq | IRLt | IRLe | IRGt | IRGe | IRAnd | IROr -> true
                | IRDiv -> isFloatTy (typeOf l) && isFloatTy (typeOf r)
                | _ -> false
            opOk && go l && go r
        | IRUnaryOp ((IRNeg | IRNot), x) -> go x
        | IRIf (c, t, f) -> go c && go t && go f
        | _ -> false
    go e
