// Pretty-printing of IR types/exprs, the callables-table builders, and
// expression attribute (effect/purity) inference.
module Blade.IRPrint

open Blade.Types
open Blade.IR

// Pretty Printing

let rec ppIRType = function
    | IRTScalar ETInt32 -> "Int32"
    | IRTScalar ETInt64 -> "Int64"
    | IRTScalar ETFloat32 -> "Float32"
    | IRTScalar ETFloat64 -> "Float64"
    | IRTScalar ETComplex64 -> "Complex64"
    | IRTScalar ETComplex128 -> "Complex128"
    | IRTScalar ETBool -> "Bool"
    | IRTScalar ETUnit -> "Void"
    | IRTScalar ETString -> "String"
    | IRTTuple ts ->
        sprintf "(%s)" (ts |> List.map ppIRType |> String.concat ", ")
    | IRTLoop lt ->
        match lt.Kind with
        | LKMethod -> sprintf "MethodLoop<%d>" (lt.Arity |> Option.defaultValue 0)
        | LKObject -> sprintf "ObjectLoop<%d>" (lt.Arity |> Option.defaultValue 0)
    | IRTComputation t -> sprintf "Computation<%s>" (ppIRType t)
    | IRTUnit -> "Void"
    | IRTPoly (base', var) -> sprintf "Poly<%s, %s>" (ppIRType base') var
    | IRTNat (Some n) -> sprintf "Nat<%d>" n
    | IRTNat None -> "Nat<?>"
    | IRTIdxTagged (inner, idxRef) ->
        // Conventional form: when the inner is the typical int64 backing,
        // render compactly as "Nat<I>" (parallel to "Float<meters>"); for
        // other inner types, show both ("(inner)<I>") to surface the
        // wrapper shape.
        let tagStr =
            match idxRef with
            | IRefNamed name -> name
            | IRefAnon (id, extent) ->
                let extentStr =
                    match extent with
                    | IRLit (IRLitInt n) -> sprintf "%d" n
                    | IRParam (name, _, _) -> name
                    | IRVar (vid, _) -> sprintf "v%d" vid
                    | _ -> "?"
                sprintf "Idx<%s>#%d" extentStr id
            | IRefAny -> "_"
        match inner with
        | IRTScalar ETInt64 | IRTScalar ETInt32 -> sprintf "Nat<%s>" tagStr
        | other -> sprintf "(%s)<%s>" (ppIRType other) tagStr
    | IRTDist (order, elem, axes) ->
        let axesStr = axes |> List.map ppIndexType |> String.concat ", "
        sprintf "Dist<%d, %s like %s>" order (ppIRType elem) axesStr
    | IRTNamed name -> name  // Named types print as themselves
    | IRTInfer id -> sprintf "T?%d" id
    // Type-argument rendering (ppUnitSigType, not ppUnitSig): a quantity
    // renders as its nominal name (`Float64<speed>`), a structural signature
    // as its dims, and a dims-cancelled structural signature (`speed/speed`,
    // `m/m`) as `<Unitless>` — display provenance only, distinct from a bare
    // type that never had units.
    | IRTUnitAnnotated (inner, units) -> sprintf "%s<%s>" (ppIRType inner) (ppUnitSigType units)
    | IRTGroupKeys (outerIdx, sourceIdx, _) -> sprintf "GroupKeys<%s, %s>" (ppIndexType outerIdx) (ppIndexType sourceIdx)
    | IRTArrow (slots, result, identity) ->
        // Renders the unified arrow form. For array-shaped arrows (all-SIdx
        // or all-SIdxVirt with non-empty slots), use the user-friendly
        // "Array<elem like indices>" rendering, which keeps error messages
        // recognizable. Other shapes (functions, mixed slots) get the
        // canonical "Arrow<...>" form.
        let isAllStored = not slots.IsEmpty && slots |> List.forall (function SIdx _ -> true | _ -> false)
        let isAllVirtual = not slots.IsEmpty && slots |> List.forall (function SIdxVirt _ -> true | _ -> false)
        if isAllStored || isAllVirtual then
            let indices =
                slots |> List.map (function
                    | SIdx i | SIdxVirt i -> ppIndexType i
                    | _ -> failwith "unreachable")
                |> String.concat ", "
            sprintf "Array<%s like %s>" (ppIRType result) indices
        else
            let slotStr =
                slots |> List.map (function
                    | SIdx idx -> sprintf "Idx<%s>" (ppIndexType idx)
                    | SIdxVirt idx -> sprintf "VirtIdx<%s>" (ppIndexType idx)
                    | SVal ty -> ppIRType ty)
                |> String.concat ", "
            let idStr =
                match identity with
                | Some _ -> " [id]"
                | None -> ""
            sprintf "Arrow<%s -> %s>%s" slotStr (ppIRType result) idStr

and ppIndexType (idx: IRIndexType) =
    // Inline extent printing since ppIRExpr is defined later
    let extentStr =
        match idx.Extent with
        | IRLit (IRLitInt n) -> sprintf "%d" n
        | IRVar (id, _) -> sprintf "v%d" id
        | IRParam (name, _, _) -> name
        | _ -> "?"
    match idx with
    | IrrepsIdxLike rendered -> ppIrrepsPower idx rendered
    | PgIrrepsIdxLike rendered -> ppIrrepsPower idx rendered
    | _ ->
        match idx.Symmetry with
        | SymNone -> sprintf "Idx<%s>" extentStr
        | SymSymmetric -> sprintf "SymIdx<%d, %s>" idx.Rank extentStr
        | SymAntisymmetric -> sprintf "AntisymIdx<%d, %s>" idx.Rank extentStr
        | SymHermitian -> sprintf "HermitianIdx<%s>" extentStr
        // Round-trippable surface spelling: the level list IS the type, so a
        // diagnostic that showed only the rank would name a different class.
        | SymWreath -> sprintf "OrbIdx<%s, %s>" (ppOrbitLevels (orbitLevelsOf idx)) (ppExtentOf (orbitBaseExtent idx))

/// The extent-slot rendering shared by both index printers: the small set of
/// extent shapes a diagnostic can name, "?" for everything else. Factored out
/// because a wreath record's extent lives one level down (inside the
/// IROrbitClass marker) and both printers have to reach it the same way.
and ppExtentOf (e: IRExpr) =
    match e with
    | IRLit (IRLitInt n) -> sprintf "%d" n
    | IRVar (id, _) -> sprintf "v%d" id
    | IRParam (name, _, _) -> name
    | _ -> "?"

/// Render an irreps-identity record whose Symmetry/Rank make it a symmetric
/// POWER of that irreps space (`SymIdx<k, IrrepsIdx<s>>` -- what
/// deduceOutputType infers for a comm group over irreps-typed inputs). A
/// plain rank-1 irreps index prints as its own base form. Shared by both
/// index printers, and by both BLOCK-SPEC members (IrrepsIdxLike and
/// PgIrrepsIdxLike), since the power wrapper says nothing about which
/// member the base belongs to.
and ppIrrepsPower (idx: IRIndexType) (renderedBase: string) =
    match idx.Symmetry with
    | SymSymmetric -> sprintf "SymIdx<%d, %s>" idx.Rank renderedBase
    | SymAntisymmetric -> sprintf "AntisymIdx<%d, %s>" idx.Rank renderedBase
    | SymHermitian -> sprintf "HermitianIdx<%s>" renderedBase
    // No surface spelling takes a block-spec base under a wreath class (the
    // OrbIdx grammar's second argument is a SymIdxBase, so `OrbIdx<[...],
    // IrrepsIdx<s>>` parses, but nothing lowers a block-spec base into a
    // SymWreath record today). Render both halves rather than drop one.
    | SymWreath -> sprintf "OrbIdx<%s, %s>" (ppOrbitLevels (orbitLevelsOf idx)) renderedBase
    | SymNone -> renderedBase

/// Build a map from IRIndexType.Id -> type name from a module's IRTDIndexType defs
let indexNameMap (modul: IRModule) : Map<IRId, string> =
    modul.Types
    |> List.choose (function
        | IRTDIndexType (name, idx) -> Some (idx.Id, name)
        | IRTDEnumIdx (name, idx, _) -> Some (idx.Id, name)
        | _ -> None)
    |> Map.ofList

/// Context-aware pretty-printers that resolve named index types
let rec ppIRTypeIn (names: Map<IRId, string>) = function
    | ArrayElem arr ->
        let indices = arr.IndexTypes |> List.map (ppIndexTypeIn names) |> String.concat ", "
        // `like`, not a comma: this printer feeds the REPL's type echo and the
        // IDE tooltips, where the string is read AS SOURCE. `Array<T, I>` is
        // not the array spelling in any position -- it does not parse.
        sprintf "Array<%s like %s>" (ppIRTypeIn names arr.ElemType) indices
    | other -> ppIRType other

and ppIndexTypeIn (names: Map<IRId, string>) (idx: IRIndexType) =
    let nominal = Map.tryFind idx.Id names
    let extentStr =
        match nominal with
        | Some name -> name
        // A wreath record's extent is one level down, inside the IROrbitClass
        // marker; `orbitBaseExtent` is the identity on every other record, so
        // this one call covers both.
        | None -> ppExtentOf (orbitBaseExtent idx)
    match idx with
    | IrrepsIdxLike rendered -> ppIrrepsPower idx rendered
    | PgIrrepsIdxLike rendered -> ppIrrepsPower idx rendered
    | _ ->
        match idx.Symmetry with
        // A plain alias keeps the documented `Idx<Lat>` form: that type's one
        // slot IS the extent, and the alias stands for exactly that extent.
        | SymNone -> sprintf "Idx<%s>" extentStr
        // An alias of a COMPACT class names the WHOLE class, whose argument
        // slots are (rank, extent) -- slots a name does not fill. Routing it
        // through the extent slot produced `SymIdx<2, MySym>`, which reads as
        // "extent = MySym" and does not parse. The bare name IS the surface
        // spelling of this type (`Array<Int32 like MySym>`), so print that.
        | _ when nominal.IsSome -> nominal.Value
        | SymSymmetric -> sprintf "SymIdx<%d, %s>" idx.Rank extentStr
        | SymAntisymmetric -> sprintf "AntisymIdx<%d, %s>" idx.Rank extentStr
        | SymHermitian -> sprintf "HermitianIdx<%s>" extentStr
        | SymWreath -> sprintf "OrbIdx<%s, %s>" (ppOrbitLevels (orbitLevelsOf idx)) extentStr

let ppSymcomState = function
    | SCNeither -> "Neither"
    | SCSymmetric -> "Symmetric"
    | SCCommutative -> "Commutative"
    | SCBoth -> "Both"

let ppBinOp = function
    | IRAdd -> "+"
    | IRSub -> "-"
    | IRMul -> "*"
    | IRDiv -> "/"
    | IRMod -> "%"
    | IRCaret -> "^"
    | IREq -> "=="
    | IRNeq -> "!="
    | IRLt -> "<"
    | IRLe -> "<="
    | IRGt -> ">"
    | IRGe -> ">="
    | IRAnd -> "&&"
    | IROr -> "||"
    | IRMath2 name -> name   // call-shaped; ppIRExpr renders it infix-ish, which
                             // is only ever read by IR dumps

let ppBinOpWithMode mode op =
    let opStr = ppBinOp op
    match mode with
    | IRElementwise -> opStr
    | IROuter -> sprintf "[%s]" opStr

let ppUnaryOp = function
    | IRNeg -> "-"
    | IRNot -> "!"
    | IRConj -> "conj"
    | IRReal -> "real"
    | IRImag -> "imag"
    | IRArg -> "arg"
    | IRMath name -> name

/// Pretty print IR expressions with optional name mapping for variables
let rec ppIRExprWithNames (names: Map<int, string>) indent (expr: IRExpr) =
    let pp = ppIRExprWithNames names 0
    let ind = String.replicate indent "  "
    match expr with
    | IRLit (IRLitInt n) -> sprintf "%d" n
    | IRLit (IRLitFloat f) -> sprintf "%f" f
    | IRLit (IRLitBool b) -> if b then "true" else "false"
    | IRLit (IRLitString s) -> sprintf "\"%s\"" s
    | IRLit IRLitUnit -> "()"
    | IRVar (id, _) -> 
        match Map.tryFind id names with
        | Some name -> name
        | None -> sprintf "v%d" id
    | IRParam (name, _, _) -> name
    | IRBinOp (mode, op, a, b) ->
        sprintf "(%s %s %s)" (pp a) (ppBinOpWithMode mode op) (pp b)
    | IRUnaryOp (op, a) ->
        sprintf "(%s%s)" (ppUnaryOp op) (pp a)
    | IRTuple es ->
        sprintf "(%s)" (es |> List.map pp |> String.concat ", ")
    | IRComplex (re, im) ->
        sprintf "complex(%s, %s)" (pp re) (pp im)
    | IRTupleProj (e, i, _) ->
        sprintf "%s.%d" (pp e) i
    | IRIf (c, t, e) ->
        sprintf "if %s then %s else %s" (pp c) (pp t) (pp e)
    | IRLet (id, v, b) ->
        // Add the let-bound name to mapping for body
        let names' = Map.add id (sprintf "v%d" id) names
        sprintf "let v%d = %s in\n%s%s" id (pp v) ind (ppIRExprWithNames names' indent b)
    | IRMethodFor info ->
        let arrs = info.Arrays |> List.map pp |> String.concat ", "
        let sdims = info.SDimsPerArray |> List.map string |> String.concat "," 
        sprintf "method_for(%s) [sdims=[%s], total=%d]" arrs sdims info.TotalSDims
    | IRObjectFor info ->
        let iranks = info.InputRanks |> List.map string |> String.concat ","
        sprintf "object_for(%s) [comm=%A, iranks=[%s], orank=%d]" 
            (pp info.Kernel) info.CommGroups iranks info.OutputRank
    | IRApplyCombinator info ->
        let states = info.SymcomStates |> List.map ppSymcomState |> String.concat ", "
        let triLevels = info.TriangularLevels |> List.map string |> String.concat ","
        let reynoldsStr = if info.HasReynolds then sprintf ", reynolds=%d perms" info.ReynoldsSpeedup else ""
        let outputStr = 
            match info.OutputType with
            | IRTUnit -> ""
            | t -> sprintf ", out=%s" (ppIRType t)
        sprintf "(%s <@> %s) [states=%s, tri=[%s], speedup=%dx%s%s]" 
            (pp info.Loop) (pp info.Kernel) states triLevels info.SpeedupFactor reynoldsStr outputStr
    | IRComposeApply info ->
        let arrs = info.InputArrays |> List.map pp |> String.concat ", "
        let outputStr = 
            match info.OutputType with
            | IRTUnit -> ""
            | t -> sprintf ", out=%s" (ppIRType t)
        sprintf "(%s <@> [%s]) [compose-apply%s]" (pp info.Composition) arrs outputStr
    | IRCompute c ->
        sprintf "(%s |> compute)" (pp c)
    | IRReynolds (k, isAntisym) ->
        let symStr = if isAntisym then ", Antisymmetric" else ""
        sprintf "reynolds(%s%s)" (pp k) symStr
    | IRPure e ->
        sprintf "pure(%s)" (pp e)
    | IRParallel (a, b, depth) ->
        sprintf "(%s <&> %s) [fusion=%A]" (pp a) (pp b) depth
    | IRFusion (a, b) ->
        sprintf "(%s <&!> %s)" (pp a) (pp b)
    | IRBind (c, k) ->
        sprintf "(%s >>= %s)" (pp c) (pp k)
    | IRFunctorMap (f, c) ->
        sprintf "(%s <$> %s)" (pp f) (pp c)
    | IRIndex (arr, idxs, _) ->
        sprintf "%s(%s)" (pp arr) (idxs |> List.map pp |> String.concat ", ")
    | IRCurry (arr, idx, rank) ->
        sprintf "%s(%s) [->rank %d]" (pp arr) (pp idx) rank
    | IRApp (f, args, _) ->
        sprintf "%s(%s)" (pp f) (args |> List.map pp |> String.concat ", ")
    | IRZip arrs ->
        sprintf "zip(%s)" (arrs |> List.map pp |> String.concat ", ")
    | IRStack arrs ->
        sprintf "stack(%s)" (arrs |> List.map pp |> String.concat ", ")
    | IRArity (None, name) -> sprintf "arity(%s)" name
    | IRArity (Some n, name) -> sprintf "arity(%s=%d)" name n
    | IRNth -> "nth"
    | IRZero -> "zero"
    | IRRank arr -> sprintf "rank(%s)" (pp arr)
    | IRPolyIndex (pack, idx) -> sprintf "%s[%s]" (pp pack) (pp idx)
    | IRPolyTail (pack, drop) -> sprintf "%s[%d..]" (pp pack) drop
    | IRChoice (a, b) ->
        sprintf "(%s <|> %s)" (pp a) (pp b)
    | IRFallback (a, b) ->
        sprintf "(%s <|:> %s)" (pp a) (pp b)
    | IRCompose (f, g) ->
        sprintf "(%s >> %s)" (pp f) (pp g)
    | IRComposeObj (f, g) ->
        sprintf "(%s >>@ %s)" (pp f) (pp g)
    | IRComposeMeth (f, g) ->
        sprintf "(%s @>> %s)" (pp f) (pp g)
    | IRConstraintCheck (c, msg, _) ->
        sprintf "check(%s, \"%s\")" (pp c) msg
    | IRAssign (target, v) ->
        let targetStr =
            match target with
            | LVVar id ->
                match Map.tryFind id names with
                | Some name -> name
                | None -> sprintf "v%d" id
            | LVIndex (arr, idxs) ->
                let arrStr = pp arr
                let idxStr = idxs |> List.map pp |> String.concat ", "
                sprintf "%s[%s]" arrStr idxStr
            | LVField (obj, f) -> sprintf "%s.%s" (pp obj) f
            | LVOther e -> pp e
        sprintf "%s <- %s" targetStr (pp v)
    | IRForRange (vid, lo, hi, body) ->
        let varName = Map.tryFind vid names |> Option.defaultValue (sprintf "v%d" vid)
        sprintf "for %s in %s..%s { %s }" varName (pp lo) (pp hi) (pp body)
    | _ -> "<expr>"

/// Default pretty printer (no name context)
let ppIRExpr indent expr = ppIRExprWithNames Map.empty indent expr


// IR Validator -- catches malformed IR between lowering and codegen

/// Attempt to statically evaluate an IRExpr to an int64, for resolving
/// extent expressions to compile-time literals (e.g. derived extents like
/// `Idx<n+1>`); anything more general returns None. Intentionally narrow --
/// StaticEval.fs already provides a full evaluator over the surface AST; the
/// use cases here (extents() inspection, reduce()'s non-emptiness check)
/// only need arithmetic over int literals.
let rec tryEvalIntIR (expr: IRExpr) : int64 option =
    match expr with
    | IRLit (IRLitInt n) -> Some n
    | IRBinOp (_, op, l, r) ->
        match tryEvalIntIR l, tryEvalIntIR r with
        | Some lv, Some rv ->
            match op with
            | IRAdd -> Some (lv + rv)
            | IRSub -> Some (lv - rv)
            | IRMul -> Some (lv * rv)
            | IRDiv when rv <> 0L -> Some (lv / rv)
            | IRMod when rv <> 0L -> Some (lv % rv)
            | _ -> None
        | _ -> None
    | IRUnaryOp (IRNeg, e) ->
        tryEvalIntIR e |> Option.map (fun n -> -n)
    | _ -> None

// AnalysisContext -- unified callable-walking for cross-procedural analysis
//
// `exprAttrs` walks an expression tree to compute attributes (FreeVars,
// BoundVars, IsPure); its IRApp arm follows IRVar(fId) through the
// CallablesTable, substitutes the callee's params with the call's args, and
// walks the body, so free variables from inside a callee surface to the
// caller's analysis. `Visited` short-circuits recursion (mutual and direct
// self-recursion stop on re-entry). CallablesTable is set once per module at
// codegen entry; Visited is augmented/restored at each IRApp boundary by
// `withVisited`; both live in one AsyncLocal record.
//
// This gives a mask predicate's exprAttrs walk visibility into every
// reachable contains (direct, through inline lambdas, through function
// calls up to recursion); whether codegen can substitute set.count for a
// given probe is a separate reachability check on the rendered tree.
//
// (CallablesTable, AnalysisContext, analysisCtxStorage, currentAnalysisCtx,
// setCallablesContext, restoreAnalysisContext, withVisited, and
// resolveCallable were moved earlier in the file, to before
// buildLoopNestCodeGen, because that builder needs to resolve IRVar-typed
// kernels through the CallablesTable.)

/// Build a CallablesTable from a module's function list. Codegen calls
/// this at module entry and installs the result via setCallablesContext.
let buildCallablesTable (funcs: IRCallable list) : CallablesTable =
    funcs |> List.map (fun f -> (f.Id, f)) |> Map.ofList

/// Build a CallablesTable from a full module, including alias entries
/// for let-bindings that reference lifted callables.
///
/// Motivation: when `let f = lambda(...)` lowers, the lambda
/// gets lifted to module.Functions with callableId, and the binding's
/// value is `IRVar(callableId, funcType)`. The binding itself has a
/// FRESH `bindingId` distinct from `callableId`. Subsequent references
/// to `f` lower as `IRVar(bindingId, _)`, NOT `IRVar(callableId, _)` --
/// they go through the binding's identity, not the callable's.
///
/// Without alias entries, `resolveCallable(IRVar(bindingId, _))` returns
/// None because the binding id isn't in the function table. Consumers
/// then fall to their non-callable fallback, which for the loop nest
/// kernel-extraction site means an empty body (rendered as
/// `((void)0)` in the generated C++).
///
/// This helper walks both top-level bindings AND nested IRLet
/// expressions (a `let f = lambda(...) in body` inside a block becomes
/// `IRLet(f.Id, ..., body)` inside the enclosing binding's value).
/// Every alias of the form `bindingId = IRVar(callableId, _)` where
/// callableId resolves in the base table adds `bindingId -> callable`
/// to the alias map. Multiple hops are followed transitively
/// (`let g = f` where `f` itself aliases a callable resolves `g` to the
/// same callable). The result is the base table with all aliases merged.
let buildCallablesTableForModule (modul: IRModule) : CallablesTable =
    let baseTable = buildCallablesTable modul.Functions
    let aliases = System.Collections.Generic.Dictionary<IRId, IRId>()
    // Side-effecting visitor: at every IRLet, record bindingId -> targetId
    // if the value is a direct IRVar reference. `iterIRExpr` walks the whole
    // tree in the same post-order `mapIRExpr` used, so a repeated binding id
    // still resolves to the same (last-written) target.
    let visitor (e: IRExpr) : unit =
        match e with
        | IRLet (bindingId, IRVar (targetId, _), _) ->
            aliases.[bindingId] <- targetId
        | _ -> ()
    let walk (e: IRExpr) : unit = iterIRExpr visitor e
    // Walk top-level binding values; also record alias if a top-level
    // binding's value is a direct IRVar (handles `let f = lambda(...)`
    // at module scope).
    modul.Bindings |> List.iter (fun b ->
        (match b.Value with
         | IRVar (targetId, _) -> aliases.[b.Id] <- targetId
         | _ -> ())
        walk b.Value)
    // Walk function bodies (nested IRLets there too).
    modul.Functions |> List.iter (fun f -> walk f.Body)
    // Resolve transitive aliases (bindingId -> targetId -> ...) with a
    // fixed step bound. Well-formed IR has fresh ids per binding so
    // cycles are structurally impossible; the bound is defensive.
    let resolveTransitive (startId: IRId) : IRId =
        let mutable curr = startId
        let mutable steps = 0
        while steps < 32 && aliases.ContainsKey(curr) do
            curr <- aliases.[curr]
            steps <- steps + 1
        curr
    // For each alias, follow transitively; if the final target is a
    // real callable, add the binding id -> callable entry.
    let mutable result = baseTable
    for kvp in aliases do
        let finalId = resolveTransitive kvp.Key
        match Map.tryFind finalId baseTable with
        | Some callable -> result <- Map.add kvp.Key callable result
        | None -> ()
    result

// (resolveCallable was moved earlier in the file -- see the analysisCtx
// block before buildLoopNestCodeGen -- so that the loop-nest builder
// can call it for IRVar-typed kernels.)

// ExprAttrs -- bottom-up attribute computation for IR expressions
//
// A single bottom-up pass that computes
//   FreeVars  -- IRIds referenced from outside this expression's binders
//   BoundVars -- IRIds introduced inside (by IRLet, lambda params, etc.)
//   IsPure    -- no observable side effects
// for any IRExpr.
//
// This does NOT drive any rewrite. It exists so that future passes (a
// general hoist, then LICM/CSE) can consume a uniform, audited source of
// "what does this expression depend on?".
//
// Design notes:
//   - No memoization: a correctness foundation, not a hot path. Add a
//     reference-keyed cache if profiling later shows this dominating.
//   - IsPure is true for all native Blade IR EXCEPT IRDisplayEmit, the one
//     construct with observable I/O (it writes a display frame to stdout).
//     Nothing consumes IsPure yet; the flag is set correctly so that when a
//     hoist/LICM/CSE pass arrives it cannot silently move, merge or drop a
//     frame emission.
//   - Exhaustive by construction: only semantically special variants have
//     explicit arms (IRVar contributes a free var; IRApp follows resolvable
//     callees; BinderShape variants scope their bound ids). Everything else
//     merges its children's attrs via the canonical ExprShape fold.

type ExprAttrs = {
    FreeVars:  Set<IRId>
    BoundVars: Set<IRId>
    IsPure:    bool
}

let internal emptyAttrs : ExprAttrs =
    { FreeVars = Set.empty; BoundVars = Set.empty; IsPure = true }

let internal mergeAttrs (a: ExprAttrs) (b: ExprAttrs) : ExprAttrs =
    { FreeVars  = Set.union a.FreeVars  b.FreeVars
      BoundVars = Set.union a.BoundVars b.BoundVars
      IsPure    = a.IsPure && b.IsPure }

let internal mergeMany (xs: ExprAttrs list) : ExprAttrs =
    List.fold mergeAttrs emptyAttrs xs

let rec exprAttrs (expr: IRExpr) : ExprAttrs =
    match expr with
    // -- Variable reference: the one FreeVars source --
    | IRVar (id, _) ->
        { emptyAttrs with FreeVars = Set.singleton id }

    // -- The one IMPURE construct: display.emit writes a frame to stdout, so
    //    a future hoist/CSE/dead-binding pass must not move it, merge two of
    //    them, or drop one whose value is unused. This is the "future impure
    //    construct" the header anticipated; the payload's own attrs still
    //    merge in (it can reference bindings like anything else).
    | IRDisplayEmit (_, _, data, _) ->
        { exprAttrs data with IsPure = false }

    | IRApp (f, args, _) ->
        let baseAttrs = mergeMany (exprAttrs f :: List.map exprAttrs args)
        // Unified cross-procedural analysis: if the called function is a
        // direct IRVar reference and resolvable in the current
        // CallablesTable, walk its body with parameter substitution.
        // This treats named functions the same way the IR tree walker
        // already treats inline lambdas -- both are "callables whose
        // body we walk." Recursion is bounded by the visited set in
        // AnalysisContext, which is augmented at every function-body
        // walk and restored afterwards.
        //
        // The walked body's probes will have Node references pointing
        // at IRContains nodes inside the function body, not in the
        // caller's tree. The mask renderer's reachability check in
        // codegen filters those out before adding to its substitution
        // map, so unreachable probes don't generate unused preamble.
        // They remain visible in the analysis for diagnostic or
        // future-use purposes.
        match f with
        | IRVar (fId, _) ->
            let ctx = currentAnalysisCtx ()
            match Map.tryFind fId ctx.Callables with
            | Some callable when not (Set.contains fId ctx.Visited) ->
                // Substitute formal params with actual args. Lengths
                // should match in well-typed IR; defensively truncate.
                let parms = callable.Params
                let body = callable.Body
                let n = min args.Length parms.Length
                let mapping =
                    List.zip (List.truncate n parms) (List.truncate n args)
                    |> List.map (fun (p, a) -> (p.VarId, a))
                    |> Map.ofList
                let body' = substituteIRVars mapping body
                let bodyAttrs = withVisited fId (fun () -> exprAttrs body')
                mergeAttrs baseAttrs bodyAttrs
            | _ -> baseAttrs
        | _ -> baseAttrs

    // -- Binders: scoped children lose their bound ids, which surface in
    //    BoundVars instead. One arm covers IRLet, IRForRange, and IRMatch
    //    via BinderShape -- a new binding variant needs exactly one
    //    BinderShape case to get correct scoping here. (IRLet's value
    //    arrives in the free part: a reference to the let-id inside its
    //    own value is ill-formed IR, and NOT subtracting it there keeps
    //    such a bug visible as a free var at the outer level.)
    | BinderShape (free, scopes) ->
        let freeAttrs = free |> List.map exprAttrs |> mergeMany
        let scopeAttrs =
            scopes |> List.map (fun (bound, parts) ->
                let a = parts |> List.map exprAttrs |> mergeMany
                { FreeVars  = Set.difference a.FreeVars bound
                  BoundVars = Set.union a.BoundVars bound
                  IsPure    = a.IsPure })
        mergeMany (freeAttrs :: scopeAttrs)

    // -- Everything else: merge over the canonical children --
    | ExprShape (children, _) ->
        children |> List.map exprAttrs |> mergeMany


