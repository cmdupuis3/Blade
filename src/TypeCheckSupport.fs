// Pre-inference support: lambda capture analysis (collectFreeVars, an
// exhaustive-match walker), capture building, comm/antisym group and OMP
// helpers, bidirectional pattern checking, application/index dispatch
// (dispatchAppOrIndex), array-argument validation, and the unit-transform
// plumbing.
module Blade.TypeCheckSupport

open Blade.Ast
open Blade.IR
open Blade.IRLoopStructure
open Blade.IRStorage
open Blade.IRLift
open Blade.IRMono
open Blade.IRPrint
open Blade.IRValidate
open Blade.Types
open Blade.TypedAst
open Blade.Unify
open Blade.TypeEnv
open Blade.Zonk
open Blade.TypeCheckIde
open Blade.TypeLower


// 5. Capture Analysis

/// Collect free variables in an expression (names not bound in local scope).
///
/// This is the CAPTURE ANALYSIS for lambdas: a name it fails to report is a
/// name that never rides the capture list, and the lifted kernel then emits a
/// dangling reference (BL6001). So the match is deliberately EXHAUSTIVE with
/// no wildcard arm -- a new ExprKind case must fail to compile (FS0025) rather
/// than silently join the dropped set, the same discipline `Unfold.mapExprPre`
/// follows. Over-reporting is safe (`buildCaptures` drops names that resolve
/// to no binding); under-reporting is the bug.
let rec collectFreeVars (bound: Set<string>) (expr: Expr) : Set<string> =
    match expr.Kind with
    | ExprKind.ExprVar name ->
        if Set.contains name bound then Set.empty else Set.singleton name
    | ExprKind.ExprLit _ -> Set.empty
    | ExprKind.ExprBinOp (_, _, l, r) ->
        Set.union (collectFreeVars bound l) (collectFreeVars bound r)
    | ExprKind.ExprUnaryOp (_, e) -> collectFreeVars bound e
    | ExprKind.ExprApp (f, args) ->
        Set.unionMany (collectFreeVars bound f :: (args |> List.map (collectFreeVars bound)))
    | ExprKind.ExprLambda (parms, _, body) ->
        let bound' = parms |> List.fold (fun s p -> Set.add p.Name s) bound
        // A default's free variables are captures too: at the kernel-apply
        // seam the default is spliced into the body as a body-entry let, so a
        // name it reads from the enclosing scope must ride the capture list.
        // Walked with every param bound, matching the top-level lambda checker
        // (a default may reference required params; BL3012 rejects references
        // to other DEFAULTED ones, so they can never be genuinely free here).
        // The where-clause holds only parameter idents -- comm/anticomm/omp
        // groups -- never expressions, so it contributes nothing.
        let defaultFree =
            parms
            |> List.choose (_.Default)
            |> List.map (collectFreeVars bound')
            |> List.fold Set.union Set.empty
        Set.union defaultFree (collectFreeVars bound' body)
    | ExprKind.ExprLet (binding, body) ->
        let valFree = collectFreeVars bound binding.Value
        let names = patternNames binding.Pattern
        let bound' = names |> List.fold (fun s n -> Set.add n s) bound
        Set.union valFree (collectFreeVars bound' body)
    | ExprKind.ExprIf (c, t, e) ->
        Set.unionMany [collectFreeVars bound c; collectFreeVars bound t; collectFreeVars bound e]
    | ExprKind.ExprTuple es | ExprKind.ExprArrayLit es | ExprKind.ExprZip es | ExprKind.ExprStack es | ExprKind.ExprSequence es ->
        es |> List.map (collectFreeVars bound) |> Set.unionMany
    | ExprKind.ExprJoin (es, _) ->
        es |> List.map (collectFreeVars bound) |> Set.unionMany
    | ExprKind.ExprMatch (scr, cases) ->
        let scrFree = collectFreeVars bound scr
        let caseFree = cases |> List.map (fun c ->
            let names = patternNames c.Pattern
            let bound' = names |> List.fold (fun s n -> Set.add n s) bound
            let guardFree = c.Guard |> Option.map (collectFreeVars bound')
                            |> Option.defaultValue Set.empty
            Set.union guardFree (collectFreeVars bound' c.Body))
        Set.union scrFree (Set.unionMany caseFree)
    | ExprKind.ExprBlock (stmts, finalExpr) ->
        let mutable b = bound
        let mutable free = Set.empty
        for stmt in stmts do
            match unwrapStmt stmt with
            | StmtSpanned _ -> ()  // unreachable: unwrapStmt strips the annotation
            | StmtLet binding ->
                free <- Set.union free (collectFreeVars b binding.Value)
                b <- patternNames binding.Pattern |> List.fold (fun s n -> Set.add n s) b
            | StmtAssign (lhs, _, rhs) ->
                free <- Set.union free (Set.union (collectFreeVars b lhs) (collectFreeVars b rhs))
            | StmtExpr e ->
                free <- Set.union free (collectFreeVars b e)
            | StmtForIn (varName, rangeExpr, bodyStmts) ->
                free <- Set.union free (collectFreeVars b rangeExpr)
                // Recurse over the body with the loop variable bound; nested
                // for-in loops recurse through the same walker. NOTE: like
                // the outer StmtLet case, lets inside the body extend the
                // bound set for SUBSEQUENT statements.
                let rec walkForBody (bound: Set<string>) (stmts: Stmt list) =
                    let mutable bb = bound
                    for bodyStmt in stmts do
                        match unwrapStmt bodyStmt with
                        | StmtSpanned _ -> ()  // unreachable: unwrapStmt strips the annotation
                        | StmtLet binding ->
                            free <- Set.union free (collectFreeVars bb binding.Value)
                            bb <- patternNames binding.Pattern |> List.fold (fun s n -> Set.add n s) bb
                        | StmtAssign (lhs, _, rhs) ->
                            free <- Set.union free (Set.union (collectFreeVars bb lhs) (collectFreeVars bb rhs))
                        | StmtExpr e ->
                            free <- Set.union free (collectFreeVars bb e)
                        | StmtForIn (v2, range2, body2) ->
                            free <- Set.union free (collectFreeVars bb range2)
                            walkForBody (Set.add v2 bb) body2
                walkForBody (Set.add varName b) bodyStmts
        match finalExpr with
        | Some e -> Set.union free (collectFreeVars b e)
        | None -> free
    | ExprKind.ExprMethodFor arrays -> arrays |> List.map (collectFreeVars bound) |> Set.unionMany
    | ExprKind.ExprObjectFor kernel -> collectFreeVars bound kernel
    | ExprKind.ExprPure e | ExprKind.ExprCompute e | ExprKind.ExprRead e | ExprKind.ExprRank e -> collectFreeVars bound e
    | ExprKind.ExprGuard (c, b) -> Set.union (collectFreeVars bound c) (collectFreeVars bound b)
    | ExprKind.ExprMask (a, p) -> Set.union (collectFreeVars bound a) (collectFreeVars bound p)
    | ExprKind.ExprCompound (d, m) -> Set.union (collectFreeVars bound d) (collectFreeVars bound m)
    | ExprKind.ExprSparse (v, k) -> Set.union (collectFreeVars bound v) (collectFreeVars bound k)
    | ExprKind.ExprIntersect (a, b) -> Set.union (collectFreeVars bound a) (collectFreeVars bound b)
    | ExprKind.ExprUnion (a, b) -> Set.union (collectFreeVars bound a) (collectFreeVars bound b)
    | ExprKind.ExprUnique a -> collectFreeVars bound a
    | ExprKind.ExprContains (a, v) -> Set.union (collectFreeVars bound a) (collectFreeVars bound v)
    | ExprKind.ExprGroupBy (v, k) -> Set.union (collectFreeVars bound v) (collectFreeVars bound k)
    | ExprKind.ExprGroupKeys ks -> ks |> List.map (collectFreeVars bound) |> Set.unionMany
    | ExprKind.ExprGroupBucket gk -> collectFreeVars bound gk
    | ExprKind.ExprSort (a, k) -> Set.union (collectFreeVars bound a) (collectFreeVars bound k)
    | ExprKind.ExprReduce (a, k, i, _) ->
        let baseVars = Set.union (collectFreeVars bound a) (collectFreeVars bound k)
        match i with
        | Some e -> Set.union baseVars (collectFreeVars bound e)
        | None -> baseVars
    | ExprKind.ExprExtents a -> collectFreeVars bound a
    | ExprKind.ExprReynolds (k, _) -> collectFreeVars bound k
    | ExprKind.ExprField (e, _) -> collectFreeVars bound e
    | ExprKind.ExprTupleIndex (t, i) -> Set.union (collectFreeVars bound t) (collectFreeVars bound i)
    | ExprKind.ExprStruct (_, fields, spread) ->
        let spreadRefs = spread |> Option.map (collectFreeVars bound) |> Option.defaultValue Set.empty
        Set.union spreadRefs (fields |> List.map (snd >> collectFreeVars bound) |> Set.unionMany)
    | ExprKind.ExprReplicate (n, b) -> Set.union (collectFreeVars bound n) (collectFreeVars bound b)
    | ExprKind.ExprDotDot (lo, hi) -> Set.union (collectFreeVars bound lo) (collectFreeVars bound hi)
    | ExprKind.ExprTyped (e, _) -> collectFreeVars bound e
    | ExprKind.ExprAssign (l, r) -> Set.union (collectFreeVars bound l) (collectFreeVars bound r)
    | ExprKind.ExprFor (src, _, kernelOpt) ->
        let srcFree = match src with
                      | ForArrays (arrs, inOpt) -> 
                          let arrFree = arrs |> List.map (collectFreeVars bound) |> Set.unionMany
                          let inFree = inOpt |> Option.map (collectFreeVars bound) |> Option.defaultValue Set.empty
                          Set.union arrFree inFree
                      | ForKernel k -> collectFreeVars bound k
        let kFree = kernelOpt |> Option.map (collectFreeVars bound) |> Option.defaultValue Set.empty
        Set.union srcFree kFree
    | ExprKind.ExprAlign (es, spec) ->
        // A BndPad boundary carries a pad EXPRESSION, which reads the
        // enclosing scope exactly like the aligned operands do.
        // Enumerated rather than wildcarded for the same reason the outer
        // match is: a future boundary mode carrying an expression must fail
        // to compile here instead of silently dropping its captures.
        let padFree =
            match spec with
            | None -> Set.empty
            | Some s ->
                match s.Boundary with
                | Ast.BoundaryMode.BndPad p -> collectFreeVars bound p
                | Ast.BoundaryMode.BndShrink
                | Ast.BoundaryMode.BndPeriodic
                | Ast.BoundaryMode.BndReflect -> Set.empty
        Set.union padFree (es |> List.map (collectFreeVars bound) |> Set.unionMany)
    | ExprKind.ExprTranspose (array, _, _) -> collectFreeVars bound array
    | ExprKind.ExprDecompact (array, _) -> collectFreeVars bound array
    | ExprKind.ExprGram (left, right) ->
        Set.union (collectFreeVars bound left) (collectFreeVars bound right)
    | ExprKind.ExprGramApply (left, right, vec) ->
        Set.unionMany [ collectFreeVars bound left; collectFreeVars bound right; collectFreeVars bound vec ]
    | ExprKind.ExprHalo (_, offsets) -> collectFreeVars bound offsets
    | ExprKind.ExprPartialApp (_, e, _) -> collectFreeVars bound e
    | ExprKind.ExprStatic e -> collectFreeVars bound e
    | ExprKind.ExprRecArray def ->
        // `let rec NAME = match NAME with | prefix :: n -> prefix :: SLICE`.
        // NAME is the family being defined -- a self-reference, not a capture
        // from an enclosing scope. PrefixVar and StepVar are the inductive
        // arm's binders; the seed arm carries its own step-var name instead
        // (its prefix position is the literal `zero`, which binds nothing).
        let selfBound = Set.add def.Name bound
        let seedFree =
            match def.SeedArm with
            | Some (seedStep, seedExpr) ->
                collectFreeVars (Set.add seedStep selfBound) seedExpr
            | None -> Set.empty
        let sliceBound = selfBound |> Set.add def.PrefixVar |> Set.add def.StepVar
        // The `while` guard scopes like the slice -- prefix/step are its
        // binders; anything else it reads is a genuine capture. Missing this
        // walk would silently drop guard captures from closure conversion.
        let guardFree =
            match def.Guard with
            | Some g -> collectFreeVars sliceBound g
            | None -> Set.empty
        Set.unionMany [ seedFree; guardFree; collectFreeVars sliceBound def.SliceExpr ]
    // ---- Leaves: nothing to walk, for a stated reason ----
    | ExprKind.ExprWildcard
    | ExprKind.ExprNth
    | ExprKind.ExprZero
    | ExprKind.ExprSection _ -> Set.empty
    // A qualified name (Module.Sub.name) never resolves to a LOCAL binding,
    // so it can never be a capture.
    | ExprKind.ExprQualified _ -> Set.empty
    // `arity(p)` is a compile-time query: monomorphization resolves it to an
    // integer literal (IR.IRArity), so the named param is never read as a
    // runtime value and never needs to ride the capture list.
    | ExprKind.ExprArity _ -> Set.empty
    // Type-level operands only. Extents written inside these TypeExprs must be
    // statically evaluable -- literals or `let static`, which lower as globals
    // rather than locals -- so no runtime value reaches them to be captured.
    // Same reasoning covers the TypeExpr halves of ExprBlocked / ExprHalo /
    // ExprTyped, whose expression halves are walked above.
    | ExprKind.ExprRange _
    | ExprKind.ExprReverse _ -> Set.empty

/// Extract variable names bound by a pattern.
/// Slot assignment for a tuple pattern's leaves, shared by every `let`
/// destructuring site (block statement, top-level decl, `let static`).
/// Returns one entry per BOUND name, in binding order, as
/// (name, patternPositionType, slot), plus the total slot count.
///
/// A pattern position that binds nothing -- `_`, a literal -- still consumes
/// its slot. Without that, the leaves after it compact onto the leading
/// components and read the wrong element (the `let (_, g) = f(x)` bug).
///
/// A compound position (a nested tuple) consumes one slot PER NAME it binds,
/// which is what makes the flat regime work: `let ((a,b), c) = ((1,2),3)`
/// assigns slots 0,1,2 and, being 3 slots wide against a 2-component
/// scrutinee, is projected as flat leaves by Lowering. Nested patterns are
/// still not destructured RECURSIVELY here (their names take fresh type
/// vars, per the callers' long-standing rule) -- so a nested pattern that
/// binds fewer names than its position has leaves, e.g. `((_, b), c)`,
/// remains as unsupported as it was before this helper existed.
and tuplePatternSlots (pats: Pattern list) : (string * int option * int) list * int =
    let mutable slot = 0
    let entries = ResizeArray<string * int option * int>()
    pats |> List.iteri (fun i p ->
        match p.Kind with
        | PatternKind.PatVar n ->
            entries.Add (n, Some i, slot)
            slot <- slot + 1
        | _ ->
            match patternNames p with
            | [] ->
                // binds nothing, but still covers a component
                slot <- slot + 1
            | names ->
                for n in names do
                    entries.Add (n, None, slot)
                    slot <- slot + 1)
    (List.ofSeq entries, slot)

and patternNames (pat: Pattern) : string list =
    match pat.Kind with
    | PatternKind.PatWildcard -> []
    | PatternKind.PatVar name -> [name]
    | PatternKind.PatLit _ -> []
    | PatternKind.PatTuple pats -> pats |> List.collect patternNames
    | PatternKind.PatCons (h, t) -> patternNames h @ patternNames t
    | PatternKind.PatStruct (_, fields) -> fields |> List.collect (fun (_, p) -> patternNames p)
    | PatternKind.PatVariant (_, Some p) -> patternNames p
    | PatternKind.PatVariant (_, None) -> []
    | PatternKind.PatGuarded (p, _) -> patternNames p
    | PatternKind.PatTyped (p, _) -> patternNames p

/// Build TypedVarInfo capture list from free variable names.
///
/// Two names are dropped: one that resolves to no binding at all (over-reporting
/// by `collectFreeVars` is deliberately safe), and one that resolves to a NAMED
/// function declaration. The latter lowers to an IRCallable at C++ global scope,
/// so the lambda body already emits its own -- possibly monomorphized -- name;
/// capturing it only mints a dead parameter that every call site then forwards
/// by SOURCE name, which no longer denotes anything once the callee is
/// monomorphized. One level of nesting hid this (the lambda is inlined into its
/// loop and the dead parameter goes with it); two levels made the outer lambda a
/// real call and the forwarded name a hard C++ error.
let buildCaptures (env: TypeEnv) (freeVars: Set<string>) : TypedVarInfo list =
    freeVars |> Set.toList |> List.choose (fun name ->
        let info = match Map.tryFind name env.OuterScope with
                   | Some i -> Some i
                   | None -> Map.tryFind name env.Variables
        info
        |> Option.filter (fun i -> not (env.DeclaredFuncIds.Contains i.VarId))
        |> Option.map (fun i ->
            { Name = name; Type = i.Type; Identity = i.Identity
              IsMutable = (i.Assign <> ReadOnly); VarId = i.VarId }))

// 6. Commutativity Extraction

/// Resolve one where-clause conjunct's parameter NAMES to parameter INDICES.
/// Names that match no parameter are dropped.
let internal groupsToIndices (parms: LambdaParam list) (groups: Ident list list) : int list list =
    groups |> List.map (fun names ->
        names |> List.choose (fun name ->
            parms |> List.tryFindIndex (fun p -> p.Name = name)))

let extractCommGroups (parms: LambdaParam list) (whereClause: WhereClause option) : int list list =
    match whereClause with
    | Some wc -> groupsToIndices parms wc.Commutativity
    | None -> []

/// `where anticomm(...)` groups, by parameter index -- the signed twin of
/// extractCommGroups. Kept as its own extractor (rather than folded into the
/// comm list) because the two declarations mean different things to the
/// stage-3 validators and to output storage; the consumers that only need
/// GROUPING concatenate them.
let extractAntisymGroups (parms: LambdaParam list) (whereClause: WhereClause option) : int list list =
    match whereClause with
    | Some wc -> groupsToIndices parms wc.Antisymmetry
    | None -> []

/// Rewrite a parallel-strategy list so its `omp(var: n)` variable names refer to
/// a WRAPPER lambda's parameter names instead of the original callee's.
///
/// Needed because a named function used in kernel position is eta-expanded into
/// `lambda(__k<uid>_0, ..) -> f(__k<uid>_0, ..)`, whose params are positionally
/// 1:1 with `f`'s but carry synthesized names -- while `Lowering.extractParallelism`
/// resolves an omp var to a param INDEX by NAME. Unmapped names (a var naming no
/// parameter -- see checkOmpVarNames) are passed through unchanged rather than
/// dropped, so this stays purely a renaming. `cuda`/`mpi` carry no variable
/// names and pass through untouched.
let remapParallelVars (calleeNames: string list) (wrapperNames: string list)
                      (strategies: ParallelStrategy list) : ParallelStrategy list =
    strategies |> List.map (function
        | Omp o ->
            Omp { o with
                    Vars = o.Vars |> List.map (fun (n, dims) ->
                        match List.tryFindIndex ((=) n) calleeNames with
                        | Some i when i < List.length wrapperNames -> (wrapperNames.[i], dims)
                        | _ -> (n, dims)) }
        | s -> s)

/// Warn when an `omp(v: n)` clause names a variable that is not a parameter.
///
/// Nothing rejects this at parse time: `Lowering.extractParallelism` resolves
/// names to param indices with `List.choose`, so an unmatched name is simply
/// DROPPED, leaving `IsOmpParallel = true` with an empty depth list. Since
/// `omp(a: n)` is a licence, that silently decides "parallelized at the
/// outermost level only" instead of erroring on the typo -- this warning is
/// what makes the typo visible. buildLoopNestCodeGen's outermost-level
/// fallback keeps the typo from silently serializing the whole nest.
let checkOmpVarNames (env: TypeEnv) (paramNames: string list)
                     (whereClause: WhereClause option) (owner: string) : unit =
    match whereClause with
    | Some wc ->
        wc.Parallel
        |> List.iter (function
            | Omp o ->
                o.Vars |> List.iter (fun (v, _) ->
                    if not (List.contains v paramNames) then
                        // BL4001 (constraint violation): a `where`-clause
                        // conjunct that names nothing. `noSpan` is honest here --
                        // checkOmpVarNames takes no span, and threading one in
                        // means editing its two callers; the render degrades to
                        // a header-only line.
                        emitWarning env "BL4001" noSpan
                            (sprintf "omp(%s: ...) on %s names no parameter (parameters: %s). The clause is ignored for that variable; parallelization falls back to the outermost loop level only."
                                     v owner
                                     (if List.isEmpty paramNames then "none"
                                      else String.concat ", " paramNames)))
            | _ -> ())
    | None -> ()

// Parallel-fold reorder licence (docs/plan-cpp-perf-exploitation.md section 2)
//
// `where ... omp` on a FOLD kernel asks codegen to split the reduced axis into
// per-thread chunks and combine the partials. That reassociates AND reorders, so
// the kernel must be commutative and associative. Two licences are accepted:
//
//   1. a declared `comm(a, b)` -- the user's word, already cross-checked against
//      body parity (CommContradictsBody rejects a provably antisymmetric body);
//   2. a BUILTIN body, which carries both properties outright and needs nothing
//      declared.
//
// The predicates below answer (2) at the surface/typed level. Codegen re-derives
// the same fact from the LOWERED body (CodeGen.foldKernelBuiltinOp) because that
// is what picks the emission path; these two must stay in step. They are
// deliberately narrow -- exactly `p <op> q` over the two parameters -- so
// "recognised builtin" means the same thing at both ends, and anything richer
// takes the `comm` route instead of silently drifting apart.

/// Commutative AND associative surface ops. The pair
/// `CodeGen.isCommutativeOp && CodeGen.isAssociativeOp` over the IR ops these
/// lower to (IRAdd/IRMul/IRAnd/IROr) -- comparison ops are commutative but not
/// associative, so they are not here.
let foldBuiltinCommAssocOp (op: BinOp) : bool =
    match op with
    | OpAdd | OpMul | OpAnd | OpOr -> true
    | _ -> false

/// Surface form: is `body` exactly `p0 <op> p1` (either order) over the
/// declaration's two parameters, for a comm+assoc builtin op? Used for NAMED
/// functions, whose bodies are invisible at the reduce seam (see
/// TypeEnv.FuncFoldBuiltin).
let isBuiltinFoldBodySurface (paramNames: string list) (body: Expr) : bool =
    match paramNames, body.Kind with
    | [p0; p1], ExprKind.ExprBinOp (_, op, l, r) when foldBuiltinCommAssocOp op ->
        (match l.Kind, r.Kind with
         | ExprKind.ExprVar a, ExprKind.ExprVar b ->
            (a = p0 && b = p1) || (a = p1 && b = p0)
         | _ -> false)
    | _ -> false

/// Typed form of the same predicate, for lambda kernels (whose TypedLambdaInfo
/// IS in hand at the reduce seam). Matches on parameter VarIds rather than
/// names, so shadowing cannot fake a licence.
let isBuiltinFoldBodyTyped (ps: TypedParam list) (body: TypedExpr) : bool =
    match ps, body.Kind with
    | [p0; p1], TExprBinOp (_, op, l, r) when foldBuiltinCommAssocOp op ->
        (match l.Kind, r.Kind with
         | TExprVar (_, ia, _), TExprVar (_, ib, _) ->
            (ia = p0.VarId && ib = p1.VarId) || (ia = p1.VarId && ib = p0.VarId)
         | _ -> false)
    | _ -> false

// 7. Array Type Utilities

let inferElemType (exprs: TypedExpr list) : IRType =
    // Empty literal defaults to Float64 -- empty literals are rank-0
    // placeholders with no useful elem type to infer. For non-empty:
    // extract from the first expr's type.
    if List.isEmpty exprs then IRTScalar ETFloat64
    else
        match exprs.[0].Type with
        | ArrayElem arr -> arr.ElemType  // Already IRType
        | IRTScalar _ as t -> t          // Pass through
        | t -> t                          // Other types (Named, Tuple, etc.) -- propagate

let inferArrayLitType (builder: IRBuilder) (exprs: TypedExpr list) : IRArrayType =
    let elemType = inferElemType exprs

    // Check for ragged structure at the second level. A ragged literal is one
    // where outer entries are themselves arrays whose lengths differ. When
    // detected, produce a RaggedIdx-typed result instead of a rectangular one.
    //
    // Note: we only check at the immediate inner level. Deeper raggedness
    // (rank-3+ with internal raggedness) is not yet supported; such literals
    // will produce wrong-shape output but no compile error.
    let isRaggedAtSecondLevel =
        match exprs with
        | [] -> false
        | first :: _ ->
            match first.Kind with
            | TExprArrayLit _ ->
                let innerLengths =
                    exprs |> List.map (fun e ->
                        match e.Kind with
                        | TExprArrayLit (inner, _) -> Some inner.Length
                        | _ -> None)
                // Ragged when lengths exist for all entries and differ
                match innerLengths |> List.choose id with
                | [] -> false
                | first :: rest -> rest |> List.exists (fun n -> n <> first)
            | _ -> false

    // Array-valued elements: when entries are expressions whose TYPE is
    // already an array (e.g. `method_for(..) |> compute` bound to a name,
    // not a bracket sub-literal), the bracket contributes only the outer
    // dimension -- inner index structure comes from the element's own
    // array type. Without this, getShape (which walks TExprArrayLit
    // nesting only) infers rank-1 scalars and codegen assigns Array
    // wrappers into scalar slots. Restricted to plain dense element index
    // types (rank-1, no symmetry/dependencies, not virtual).
    let rowTypedElemArr =
        match exprs with
        | first :: _ ->
            match first.Kind, first.Type with
            | TExprArrayLit _, _ -> None
            | _, ArrayElem elemArr when
                not elemArr.IsVirtual
                && not elemArr.IndexTypes.IsEmpty
                && elemArr.IndexTypes |> List.forall (fun ix ->
                    ix.Rank = 1 && ix.Symmetry = SymNone && ix.Dependencies.IsEmpty) ->
                Some elemArr
            | _ -> None
        | [] -> None

    // TRIANGULAR => SYMMETRIC: an unannotated nest whose rows are n, n-1,
    // ..., 1 IS the left-justified simplex of `SymIdx<2, n>`
    // (canon_left_justify's storage, cell for cell):
    //
    //     let B = [[3, 2, 1], [5, 4], [6]]     // Array<Int64 like SymIdx<2, 3>>
    //     B(1, 0) == B(0, 1) == 2
    //
    // RAGGED data of exactly this shape needs its annotation
    // (`Array<T like Idx<n>, RaggedIdx<lens>>`, corpus index-types/019); any
    // other row profile still infers rectangular or inline-ragged below.
    // Matched at ANY rank r (level seeded at p is n-p wide, p'=p+i), so
    // `[[[1,2,3],[4,5],[6]], [[7,8],[9]], [[10]]]` is `SymIdx<3, 3>`.
    //
    // INCLUSIVE only: the strict profile (n-1,...,1,0) is AntisymIdx's
    // storage, but antisymmetry is a SIGN claim no shape alone justifies.
    let triangularExtent =
        // The nest's depth, if every leaf sits at the same one and no level is
        // empty. A ragged/rectangular nest is rejected by the width walk below,
        // not here; this only establishes the r to walk against.
        let rec depthOf (e: TypedExpr) =
            match e.Kind with
            | TExprArrayLit ([], _) -> None
            | TExprArrayLit (cs, _) ->
                let ds = cs |> List.map depthOf
                match ds with
                | d :: rest when ds |> List.forall Option.isSome && rest |> List.forall (fun x -> x = d) ->
                    d |> Option.map (fun k -> k + 1)
                | _ -> None
            | _ -> Some 0
        // Width walk over one level's children: a level seeded at p holds
        // n - p of them, and child i seeds the next level at p + i. The leaf
        // level's children are the cells.
        let rec widthsOk (n: int) (rank: int) (depth: int) (seed: int) (cs: TypedExpr list) =
            cs.Length = n - seed
            && (depth = rank - 1
                || cs
                   |> List.mapi (fun i c ->
                       match c.Kind with
                       | TExprArrayLit (gs, _) -> widthsOk n rank (depth + 1) (seed + i) gs
                       | _ -> false)
                   |> List.forall id)
        let n = List.length exprs
        if n < 2 then None
        else
            // The literal is one level deeper than its rows.
            let rowDepths = exprs |> List.map depthOf
            match rowDepths with
            | d :: rest when rowDepths |> List.forall Option.isSome
                             && rest |> List.forall (fun x -> x = d) ->
                let rank = Option.get d + 1
                if rank >= 2 && widthsOk n rank 0 0 exprs then Some (rank, n) else None
            | _ -> None

    if triangularExtent.IsSome then
        let (rank, n) = triangularExtent.Value
        let symIdx = {
            Id = builder.FreshId(); Rank = rank; Extent = IRLit (IRLitInt (int64 n))
            Symmetry = SymSymmetric; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = []
        }
        { ElemType = elemType; IndexTypes = [symIdx]; IsVirtual = false; Identity = None }
    elif rowTypedElemArr.IsSome then
        let elemArr = rowTypedElemArr.Value
        let outerIdx = {
            Id = builder.FreshId(); Rank = 1; Extent = IRLit (IRLitInt (int64 exprs.Length))
            Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = []
        }
        // Fresh Ids: the literal's dimensions are new index-space occurrences,
        // not the source rows' (mirrors the fresh-Id policy of the
        // rectangular branch below). Extent/Tag/Kind carry over.
        let innerIdxs = elemArr.IndexTypes |> List.map (fun ix -> { ix with Id = builder.FreshId() })
        { ElemType = elemArr.ElemType; IndexTypes = outerIdx :: innerIdxs; IsVirtual = false; Identity = None }
    elif isRaggedAtSecondLevel then
        // Build a RaggedIdx-typed array. Outer index has extent = number of
        // entries (rectangular at outer level). Inner index is RaggedIdx with
        // an IRRaggedLookup whose lengths reference is synthesized from the
        // literal's actual sub-array lengths (computed at codegen time).
        let n = List.length exprs
        let outerIdx = {
            Id = builder.FreshId(); Rank = 1; Extent = IRLit (IRLitInt (int64 n))
            Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = []
        }
        // The lengths reference is a synthetic IRParam -- codegen recognizes
        // this and emits the lengths array inline from the literal structure.
        // The "__inline_lens" name is a sentinel that the codegen detects.
        let innerIdx = {
            Id = builder.FreshId(); Rank = 1
            Extent = IRRaggedLookup (IRParam ("__inline_lens", 0, IRTNat None))
            Symmetry = SymNone; Tag = Some "__raggedidx_inline"; IxKind = IxKRaggedInline
            Kind = SDimension; Dependencies = []
        }
        { ElemType = elemType; IndexTypes = [outerIdx; innerIdx]; IsVirtual = false; Identity = None }
    else
        // Rectangular: existing behavior -- first sub-array's length defines all rows.
        let rec getShape (es: TypedExpr list) : int list =
            match es with
            | [] -> [0]
            | first :: _ ->
                match first.Kind with
                | TExprArrayLit (inner, _) -> List.length es :: getShape inner
                | _ -> [List.length es]
        let shape = getShape exprs
        let indexTypes = shape |> List.map (fun extent ->
            { Id = builder.FreshId(); Rank = 1; Extent = IRLit (IRLitInt (int64 extent))
              Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] })
        { ElemType = elemType; IndexTypes = indexTypes; IsVirtual = false; Identity = None }

let getArrayType (env: TypeEnv) (expr: Expr) : IRArrayType =
    match expr.Kind with
    | ExprKind.ExprVar name ->
        match lookupVar name env with
        | Some info ->
            match info.Type with
            | ArrayElem arrTy -> arrTy
            | _ ->
                { ElemType = IRTScalar ETFloat64
                  IndexTypes = [{ Id = env.Builder.FreshId(); Rank = 1
                                  Extent = IRParam(name + "_n", 0, IRTNat None)
                                  Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension
                                  Dependencies = [] }]
                  IsVirtual = false; Identity = Some (AIDVariable name) }
        | None ->
            { ElemType = IRTScalar ETFloat64
              IndexTypes = [{ Id = env.Builder.FreshId(); Rank = 1
                              Extent = IRParam(name + "_n", 0, IRTNat None)
                              Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension
                              Dependencies = [] }]
              IsVirtual = false; Identity = Some (AIDVariable name) }
    | _ ->
        { ElemType = IRTScalar ETFloat64; IndexTypes = []; IsVirtual = false; Identity = None }

/// Read a loop operand's array SHAPE for a method_for / co-iteration former.
///
/// Resolve through Subst before matching (the same discipline buildApplyInfo
/// applies to kernel param types): an operand that is a COMPOUND expression --
/// `zip(A - m, B - m)`, the desugaring of `(A - m) * (B - m)` -- carries a type
/// that is still an unresolved IRTInfer var here, because only the substitution
/// knows it was unified with the rank-1 array the inner pipeline produces.
/// Matching the raw type let `ArrayElem` miss and dropped the operand to
/// `getArrayType`'s non-variable fallback, whose ZERO index records made
/// zipSharedRecords see minRank 0 and return no shared records -- collapsing the
/// co-iteration to rank 0, so an elementwise product of two compound operands
/// typed as a SCALAR and `reduce` over it rejected with "requires an array as
/// first argument". Naming the operands hid the bug, since getArrayType's
/// ExprVar arm recovers a rank-1 record from env.
///
/// The ELEMENT type keeps getArrayType's Float64 default whenever it is still
/// unresolved AND NOT HM-POLYMORPHIC. Codegen has no C++ spelling for a bare
/// inference var (it emits a BLADE_UNRESOLVED_ELEM_TYPE placeholder), so an
/// element nothing will ever pin has to default, and defaulting matches what
/// the getArrayType fallback already supplied on this path.
///
/// A DECLARATION's signature var is the one case that does get pinned:
/// `function f(xs: T^1)` -- once `requireArrayArgMinRank` gives `T` its shape,
/// the element is a polymorphic-marked var that IR-phase HM monomorphization
/// substitutes PER CALL SITE. Defaulting it here silently collapsed the
/// function to Float64 while its PARAMETER type stayed polymorphic, so a
/// specialization at Complex128 emitted `Array<complex<double>>` parameters
/// around a `double` loop body and `Array<double,1>` returns ("cannot convert
/// std::complex<double> to double in initialization").
///
/// INSIDE A LAMBDA BODY the marked var is kept too. A LAMBDA param may also be
/// written `T^1` (`lambda(r: T^1) -> ...`) and its var carries the same mark;
/// nothing monomorphizes a lambda, but the var is not stranded either -- it is
/// unified with the iterated ROW type moments later, in `buildApplyInfo`
/// (kernelParamUnifyResult), and Zonk re-resolves the ApplyInfo.ArrayTypes
/// snapshot through the final substitution (see the "ELEMENT TYPES TOO" note in
/// Zonk.fs), so a late binding reaches codegen. Defaulting here instead PINNED
/// the snapshot: a nested `row <@> f` inside `lambda(row: T^1)` handed `f`'s
/// param a Float64 row element, so a Complex128 named-function kernel refused
/// with BL3001 "expected Complex128, got Float64" at the OUTER apply -- while
/// the identical Float64 kernel compiled by coincidence (measured; the
/// unannotated-lambda spelling of the same kernel instead bound the param to
/// Float64 and died in g++ reading `double` off a complex row). An earlier form
/// of this arm defaulted every lambda-body element because the snapshot was
/// "never revisited"; that premise died when Zonk gained the ArrayTypes walk.
/// AN INDEX RECORD'S `Kind` IS A STATEMENT ABOUT ONE APPLY, NOT ABOUT THE VALUE.
/// `SDimension` means "this apply's grid iterates it"; `TDimension` means "this
/// apply's KERNEL contributed it" -- `kernelTDims` stamps the kernel's return
/// axes that way, and `deduceOutputType` bakes them into the result TYPE. The
/// resulting VALUE has no T-shaped axes: it is an array with n axes, and the
/// NEXT apply must iterate all of them.
///
/// `computeSDimsPerArray` counts only `SDimension`, so an operand carrying an
/// inherited `TDimension` record silently lost that axis at the next `<@>`.
/// Measured on `examples/lswosa.blade`: `ls_e` is the (freq x segment) grid a
/// grouped kernel returned, `transpose(ls_e, [0,1])` accepts it as rank 2, and
/// `ls_e <@> mag2` came back RANK 1 -- so `reduce(mod_avg, (+))` refused a value
/// that is an array (line 187). The same shape with a lambda kernel came back a
/// SCALAR, because the ragged-inner accounting in `kernelInputRanks` subtracts
/// the (undercounted) S-dims and re-attributes the difference to the kernel.
///
/// Unreachable before S3 -- an array-valued kernel return had no way to exist,
/// so no value ever carried a `TDimension` record into an operand slot. This
/// normalization runs BEFORE `buildApplyInfo` re-tags the fibers THIS apply
/// consumes, so the two compose: inherited kinds reset to S, then this apply
/// stamps its own T-dims.
let internal reSDimOperand (at: IRArrayType) : IRArrayType =
    if at.IndexTypes |> List.exists (fun ix -> ix.Kind <> SDimension) then
        { at with IndexTypes = at.IndexTypes |> List.map (fun ix -> { ix with Kind = SDimension }) }
    else at

let loopOperandArrayType (env: TypeEnv) (fallback: unit -> IRArrayType) (ty: IRType) : IRArrayType =
    match env.Subst.Resolve ty with
    | ArrayElem at0 ->
        let at = reSDimOperand at0
        match env.Subst.Resolve at.ElemType with
        | IRTInfer id when not (env.Subst.IsPolymorphicId id) ->
            { at with ElemType = IRTScalar ETFloat64 }
        | _ -> at
    | _ -> fallback ()

/// The `object_for(k) <@> X` orientation's operand-shape fallback, and the
/// TypedExpr twin of `getArrayType`'s `ExprVar` arm.
///
/// The two orientations of the SAME apply disagreed about an operand whose type
/// is still an unresolved inference var. `method_for` falls back through
/// `getArrayType`, which recovers a RANK-1 record (extent `<name>_n`, Identity =
/// the variable) for a named operand; `object_for` degraded to a RANK-0 record,
/// which types the whole apply SCALAR. Measured on
///
///     function g(ws: T^1, t: T^1) = {
///         ws <@> lambda(w) -> { let wt = (w * t) |> compute
///                               let c = sin <@> wt
///                               reduce(c, (+)) } }
///
/// `wt <@> sin` compiled and ran; the identical `sin <@> wt` emitted
/// `double __v7 = 0; __v7 += std::sin(__v6);` -- `genApplyCombinator`'s
/// SCALAR-output accumulation path, with no loop and a phantom element name
/// g++ rejects ("'__v6' was not declared in this scope"). Only the abstract
/// (`T^k`) spelling reaches it: with concrete `Array<..>` params the operand
/// resolves to an array and neither fallback runs.
///
/// The rank-1 claim is NOT a guess: `<@>` has nothing to iterate unless the
/// operand is an array, so rank >= 1 is pinned by the apply itself. It is also
/// the CHEAP half of that claim -- a local metadata record, NOT a `Bind`. The
/// var stays open, so this cannot spend a declaration's HM-polymorphic
/// signature/return var (the S1 regression: `materializeArityVar`'s two call-site
/// guards exist precisely because BINDING one collapses every specialization).
/// Restricted to a still-unresolved `IRTInfer`: a var that already resolved to a
/// concrete SCALAR is not an unknown, so it keeps the rank-0 degradation and its
/// existing diagnostic.
let objectForOperandFallback (env: TypeEnv) (t: TypedExpr) : IRArrayType =
    let rank0 = { ElemType = IRTScalar ETFloat64; IndexTypes = []; IsVirtual = false; Identity = None }
    match t.Kind with
    | TExprVar (name, _, _) ->
        (match env.Subst.Resolve t.Type with
         | IRTInfer _ ->
             { ElemType = IRTScalar ETFloat64
               IndexTypes = [ { Id = env.Builder.FreshId(); Rank = 1
                                Extent = IRParam (name + "_n", 0, IRTNat None)
                                Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                                Kind = SDimension; Dependencies = [] } ]
               IsVirtual = false; Identity = Some (AIDVariable name) }
         | _ -> rank0)
    | _ -> rank0

// 8. Helpers

let sequenceResults (results: TypeResult<'a> list) : TypeResult<'a list> =
    let rec loop acc = function
        | [] -> Ok (List.rev acc)
        | Ok x :: rest -> loop (x :: acc) rest
        | Error e :: _ -> Error e
    loop [] results

/// Infer the type of a literal.
let inferLiteralType lit =
    match lit with
    | LitInt _ -> IRTScalar ETInt64
    | LitFloat _ -> IRTScalar ETFloat64
    | LitBool _ -> IRTScalar ETBool
    | LitString _ -> IRTScalar ETString
    | LitChar _ -> IRTScalar ETInt32
    | LitUnit -> IRTUnit

/// Synthesize the type of a *value-position* literal. Numeric/bool literals
/// get a fresh, kind-seeded inference var (like `zero`) so they flex to
/// context -- lifting into `T`/`T^k` slots and pinning to the width a context
/// requires -- while the seed keeps an unpinned literal at its natural type
/// (`let x = 1` stays Int64). String/Char/Unit have no such flex and stay
/// concrete. NOTE: pattern literals keep `inferLiteralType` (they compare by
/// value), as do the explicit `checkExpr` literal arms.
let freshLiteralType (subst: Subst) lit =
    match lit with
    | LitInt _ -> subst.FreshLiteral ETInt64
    | LitFloat _ -> subst.FreshLiteral ETFloat64
    | LitBool _ -> subst.FreshLiteral ETBool
    | _ -> inferLiteralType lit

// 9. Pattern Type Checking

/// The declared constructors of a variant/enum type, in declaration order
/// (empty when `typeName` is not a variant type).
let variantConstructorsOf (env: TypeEnv) (typeName: string) : (string * bool) list =
    match Map.tryFind typeName env.TypeDefs with
    | Some (TDIVariant (_, _, variants)) -> variants |> List.map (fun (n, d) -> (n, d.IsSome))
    | _ -> []

/// The variant type a scrutinee of type `ty` resolves to, if any.
let scrutineeVariantType (env: TypeEnv) (ty: IRType) : string option =
    match env.Subst.Resolve ty with
    | IRTNamed n when not (List.isEmpty (variantConstructorsOf env n)) -> Some n
    | _ -> None

/// Constructor names a misspelled `name` plausibly meant: a case-insensitive
/// match, one transposition, or one character inserted/deleted/substituted.
let constructorSpellingCandidates (known: string list) (name: string) : string list =
    let close (a: string) (b: string) =
        if a.ToLowerInvariant() = b.ToLowerInvariant() then true
        elif abs (a.Length - b.Length) > 1 then false
        elif a.Length = b.Length then
            let diffs = Seq.zip a b |> Seq.filter (fun (x, y) -> x <> y) |> Seq.toList
            match diffs with
            | [] | [_] -> true
            | [(x1, y1); (x2, y2)] -> x1 = y2 && x2 = y1
            | _ -> false
        else
            let short, long = if a.Length < b.Length then a, b else b, a
            [0 .. long.Length - 1] |> List.exists (fun i -> long.Remove(i, 1) = short)
    known |> List.filter (fun k -> k <> name && close k name) |> List.distinct

/// The refusal for a constructor pattern that names no constructor: a typo'd
/// tag (`| Sqaure(w)`) or a Capitalized bare binder over a variant scrutinee
/// (`| Suoth ->`, which would bind a catch-all VARIABLE and kill every later
/// arm). `typeName` is the scrutinee's variant type when known.
let unknownConstructorError (env: TypeEnv) (name: string) (typeName: string option) (bare: bool) : TypeError =
    let known =
        match typeName with
        | Some t -> variantConstructorsOf env t |> List.map fst
        | None -> env.VariantTags |> Map.toList |> List.map fst |> List.sort
    let owner =
        match typeName with
        | Some t -> $"'{t}' has no constructor named {name}"
        | None -> $"{name} is not a constructor of any declared variant type"
    let listing =
        if List.isEmpty known then ""
        else
            let what = match typeName with Some _ -> "its constructors" | None -> "known constructors"
            let ks = String.concat ", " known in $" ({what}: {ks})"
    let suggestion =
        match constructorSpellingCandidates known name with
        | [] -> ""
        | cs -> let alts = String.concat " or " cs in $" Did you mean {alts}?"
    let why =
        if bare then
            $" A Capitalized name in a match over a variant type is read as a constructor -- as a variable it would match EVERYTHING and make every later arm dead. Fix the spelling, or use a lowercase binder (or `_`) if a catch-all is what you meant."
        else
            $" A constructor pattern `{name}(...)` must name a declared variant."
    UnknownConstructorPattern (name, $"pattern '{name}': {owner}{listing}.{suggestion}{why}")

/// Type-check a pattern against an expected type. Returns a TypedPattern
/// whose Bindings list contains every (name, varId, type) introduced.
let rec checkPattern (env: TypeEnv) (expected: IRType) (pat: Pattern)
    : TypeResult<TypedPattern> =
    match pat.Kind with
    | PatternKind.PatWildcard ->
        Ok { Kind = TPatWild; Type = expected; Bindings = [] }

    | PatternKind.PatVar name ->
        // Check if this name is a registered variant tag (enum constructor without data).
        // The parser can't distinguish `| North -> ...` (variant match) from `| x -> ...` (variable binding)
        // because it doesn't have type information. We resolve the ambiguity here.
        match Map.tryFind name env.VariantTags with
        | Some (parentName, None) ->
            // This is a no-payload variant constructor -- treat as PatVariant
            checkPattern env expected (inheritPatSpan pat (PatVariant (name, None)))
        | Some (parentName, Some _) ->
            // Variant with payload, used WITHOUT it. Treating this as a
            // variable binding (which it used to be) is a trap, not a
            // feature: `| Some -> ...` silently became an irrefutable
            // binder named Some, matched EVERYTHING, and killed every arm
            // after it -- `| None -> ...` below it was dead code with no
            // diagnostic. A payload-carrying constructor in pattern
            // position can only sensibly mean the variant test, so demand
            // the payload be spelled.
            Error (Other $"pattern '{name}': this constructor of '{parentName}' carries a payload, so a bare '{name}' here would not test the variant -- it would bind a fresh VARIABLE named {name} that matches everything (making every later arm dead). Match it as {name}(p) (or {name}(_) to ignore the payload); rename the binder if a variable is what you meant.")
        | None ->
            // A Capitalized name that is NOT a constructor, in a match over a
            // variant type, is a misspelled constructor far more often than a
            // binder (values are snake_case): `| Suoth -> 2` used to bind a
            // catch-all variable and silently kill `| East -> 3` below it.
            match scrutineeVariantType env expected with
            | Some typeName when name.Length > 0 && System.Char.IsUpper name.[0] ->
                setCurrentExprSpan pat.Span
                Error (unknownConstructorError env name (Some typeName) true)
            | _ ->
            let varId = env.Builder.FreshId()
            Ok { Kind = TPatVar (name, varId); Type = expected
                 Bindings = [(name, varId, expected)] }

    | PatternKind.PatLit lit ->
        // A pattern literal is an UNTYPED literal exactly as a value-position one
        // is, so it must adopt the scrutinee's numeric type instead of pinning it
        // to the literal's default. `match (a: Nat) with | 1 -> ...` otherwise
        // unified Int64 against Nat<?> and reported BL3001. The adopting cases
        // mirror `checkExpr`'s context-driven literal arms (sec. 4.18.3): an int
        // literal takes a Nat / unit-annotated Nat / index-tagged-int target.
        // Deliberately narrow -- every other combination still goes through
        // `unify`, so a float literal against a Nat scrutinee is the mismatch it
        // has always been, and no NON-literal ever flows across these edges.
        let adoptsExpected =
            match lit with
            | LitInt _ ->
                match env.Subst.Resolve expected with
                | IRTNat _
                | IRTUnitAnnotated (IRTNat _, _)
                | IRTIdxTagged (IRTScalar (ETInt32 | ETInt64), _) -> true
                | _ -> false
            | _ -> false
        if adoptsExpected then
            Ok { Kind = TPatLit lit; Type = expected; Bindings = [] }
        else
        let litTy = inferLiteralType lit
        unify env.Subst litTy expected |> Result.map (fun () ->
            { Kind = TPatLit lit; Type = expected; Bindings = [] })

    | PatternKind.PatTuple pats ->
        match env.Subst.Resolve expected with
        | IRTTuple tys when tys.Length = pats.Length ->
            List.zip pats tys |> List.map (fun (p, t) -> checkPattern env t p)
            |> sequenceResults |> Result.map (fun tPats ->
                { Kind = TPatTuple tPats; Type = expected
                  Bindings = tPats |> List.collect (_.Bindings) })
        | _ ->
            let tys = pats |> List.map (fun _ -> env.Subst.Fresh())
            let tupleTy = IRTTuple tys
            unify env.Subst tupleTy expected |> Result.bind (fun () ->
                List.zip pats tys |> List.map (fun (p, t) -> checkPattern env t p)
                |> sequenceResults |> Result.map (fun tPats ->
                    { Kind = TPatTuple tPats; Type = expected
                      Bindings = tPats |> List.collect (_.Bindings) }))

    | PatternKind.PatCons (headPat, tailPat) ->
        let headTy = env.Subst.Fresh()
        let tailTy = env.Subst.Fresh()
        checkPattern env headTy headPat |> Result.bind (fun tHead ->
        checkPattern env tailTy tailPat |> Result.bind (fun tTail ->
            Ok { Kind = TPatCons (tHead, tTail); Type = expected
                 Bindings = tHead.Bindings @ tTail.Bindings }))

    | PatternKind.PatVariant (tag, payloadPat) ->
        match Map.tryFind tag env.VariantTags with
        | Some (parentName, payloadTy) ->
            let isEnum = isEnumType env parentName
            // THE CONSTRUCTOR'S PARENT TYPE MUST BE THE SCRUTINEE'S. Nothing
            // used to relate the two: `match One with | Three -> ...` (One of
            // `type A`, Three of `type B`) checked clean and matched at
            // runtime, because emission compares constructor ORDINALS and
            // both are zero. A concrete nominal disagreement is refused here;
            // an open scrutinee type is bound to the parent so it flows.
            // Anything else (an enum-index scrutinee typed through its tag,
            // an alias) keeps the historical acceptance.
            let nominalClash =
                match env.Subst.Resolve expected with
                | IRTNamed other when other <> parentName -> Some other
                | _ -> None
            match nominalClash with
            | Some _ -> Error (PatternTypeMismatch (tag, expected))
            | None ->
            (match env.Subst.Resolve expected with
             | IRTInfer _ -> unify env.Subst (IRTNamed parentName) expected |> ignore
             | _ -> ())
            match payloadPat, payloadTy with
            | Some p, Some ty ->
                checkPattern env ty p |> Result.map (fun tPayload ->
                    { Kind = TPatVariant (tag, Some tPayload, isEnum)
                      Type = IRTNamed parentName
                      Bindings = tPayload.Bindings })
            | None, None ->
                Ok { Kind = TPatVariant (tag, None, isEnum)
                     Type = IRTNamed parentName; Bindings = [] }
            | Some p, None ->
                Error (PatternTypeMismatch ($"{tag}(...)", expected))
            | None, Some _ ->
                Ok { Kind = TPatVariant (tag, None, isEnum)
                     Type = IRTNamed parentName; Bindings = [] }
        | None ->
            // Unknown variant tag. This used to be ACCEPTED ("allow it, bind
            // any payload") and reached g++ as an undeclared constructor --
            // a typo like `| Sqaure(w)` failed as a C++ compile error, or in
            // an enum match compared a garbage ordinal. Refused here, naming
            // the scrutinee type's real constructors.
            setCurrentExprSpan pat.Span
            Error (unknownConstructorError env tag (scrutineeVariantType env expected) false)

    | PatternKind.PatStruct (typeName, fieldPats) ->
        let fieldTypes =
            match lookupTypeDef typeName env with
            | Some (TDIStruct (_, _, fields, _)) ->
                fields |> List.map (fun (n, t) -> (n, t)) |> Map.ofList
            | _ -> Map.empty
        // Same nominal rule as the variant arm: a struct pattern names a
        // TYPE, and a value of another struct type with the same field
        // spelling (`A { x = 12 }` against `B { x }`) is not an instance of
        // it. Refused on a concrete disagreement; an open scrutinee is bound.
        let nominalClash =
            if Map.isEmpty fieldTypes then None
            else
                match env.Subst.Resolve expected with
                | IRTNamed other when other <> typeName -> Some other
                | _ -> None
        match nominalClash with
        | Some _ -> Error (PatternTypeMismatch ($"{typeName} {{ ... }}", expected))
        | None ->
        (if not (Map.isEmpty fieldTypes) then
            match env.Subst.Resolve expected with
            | IRTInfer _ -> unify env.Subst (IRTNamed typeName) expected |> ignore
            | _ -> ())
        fieldPats |> List.map (fun (fname, fpat) ->
            let fTy = Map.tryFind fname fieldTypes |> Option.defaultValue (env.Subst.Fresh())
            checkPattern env fTy fpat |> Result.map (fun tp -> (fname, tp)))
        |> sequenceResults |> Result.map (fun tFields ->
            { Kind = TPatStruct (typeName, tFields)
              Type = (if Map.isEmpty fieldTypes then expected else IRTNamed typeName)
              Bindings = tFields |> List.collect (fun (_, p) -> p.Bindings) })

    | PatternKind.PatGuarded (innerPat, _guardExpr) ->
        // Guard expression is type-checked in inferMatch when we have full env
        checkPattern env expected innerPat |> Result.map (fun tInner ->
            { Kind = TPatGuarded (tInner, mkTyped (TExprLit (LitBool true)) (IRTScalar ETBool))
              Type = expected; Bindings = tInner.Bindings })

    | PatternKind.PatTyped (innerPat, tyAnnotation) ->
        let annotTy = lowerTypeExpr env tyAnnotation
        unify env.Subst annotTy expected |> Result.bind (fun () ->
            checkPattern env annotTy innerPat)

/// Coverage warnings for a checked match, both STATIC facts about the arm
/// list (no value analysis):
///
///   BL3022  an arm no value can reach: it follows an unguarded catch-all
///           (`_` / a binder), or an unguarded arm that already took every
///           value of the same constructor (or, over a variant type, all of
///           them).
///   BL3023  a match over a variant/enum type that names neither every
///           constructor nor a catch-all: a missing constructor aborts at
///           run time (BL8002).
///
/// Only unguarded arms count as covering -- a guard may fall through.
let matchCoverageWarnings (env: TypeEnv) (scrutineeTy: IRType) (scrutineeSpan: Span)
                          (cases: MatchCase list) (tCases: TypedMatchCase list) : unit =
    let rec irrefutable (p: TypedPattern) =
        match p.Kind with
        | TPatWild | TPatVar _ -> true
        | TPatTuple ps -> List.forall irrefutable ps
        | _ -> false
    let variantType = scrutineeVariantType env scrutineeTy
    let constructors = variantType |> Option.map (variantConstructorsOf env >> List.map fst) |> Option.defaultValue []
    let mutable caughtAll = false
    let mutable covered = Set.empty<string>
    for (case, tCase) in List.zip cases tCases do
        let unguarded = tCase.Guard.IsNone
        let span = case.Pattern.Span
        if caughtAll then
            emitWarning env "BL3022" span
                "unreachable match arm: an earlier arm already matches every value that could reach this one (an unguarded `_` / variable binder, or every constructor of the type). Remove the arm, or move it above the arm that shadows it."
        else
            match tCase.Pattern.Kind with
            | TPatVariant (tag, payload, _) when Set.contains tag covered ->
                ignore payload
                emitWarning env "BL3022" span
                    $"unreachable match arm: an earlier unguarded arm already matches every {tag} value, so this one can never be selected. Remove it, or move it above the arm that shadows it."
            | TPatVariant (tag, payload, _) when unguarded && (payload |> Option.forall irrefutable) ->
                covered <- Set.add tag covered
                if not (List.isEmpty constructors) && constructors |> List.forall (fun c -> Set.contains c covered) then
                    caughtAll <- true
            | _ when unguarded && irrefutable tCase.Pattern -> caughtAll <- true
            | _ -> ()
    match variantType with
    | Some typeName when not caughtAll ->
        let missing = constructors |> List.filter (fun c -> not (Set.contains c covered))
        if not (List.isEmpty missing) then
            emitWarning env "BL3023" scrutineeSpan
                $"""non-exhaustive match on '{typeName}': no arm matches {String.concat ", " missing}. A value of an unmatched constructor aborts the program at run time (BL8002). Add an arm for each, or a `_ ->` catch-all."""
    | _ -> ()

// 10. Expression Type Inference (every Expr variant handled explicitly)

/// Drive type inference for special forms (extents, mask, sort, intersect,
/// union, group_keys, group_by) when the array argument is unresolved
/// (typically an unannotated kernel parameter). Rejecting non-arrays on the
/// resolved type fails with "requires array" on an unbound IRTInfer even
/// though the special form could supply the constraint; this helper
/// inverts that by synthesizing a fresh IRTArray (rank 1, fresh elem type,
/// fresh anonymous index, Tag=None so it passes as "synthetic") and
/// unifying the argument with it. Concrete-but-not-an-array is still a
/// real type error. Centralized here rather than duplicated per special
/// form for IDE/language-server tooling.
///
/// `requireArrayArg` generalizes to a MINIMUM rank (`minRank` rank-1
/// slots): ops needing rank >= 2 (gram, transpose) must use this, since
/// rank-1 synthesis always failed on an unannotated argument otherwise.
///
/// DEFERRED: `decompact` is NOT switched over -- its demand is a
/// compact-GROUP slot (Rank >= 2, non-SymNone), not a rank COUNT, and needs
/// a symmetry-carrying synthesis.
let requireArrayArgMinRank (env: TypeEnv) (tArr: TypedExpr) (opName: string) (minRank: int) : TypeResult<IRArrayType> =
    let resolved = env.Subst.Resolve(tArr.Type)
    match resolved with
    | ArrayElem arrTy -> Ok arrTy
    | IRTInfer vid ->
        // RANK PIN. The caret on a `T^k` parameter is an EXACT rank claim,
        // but it lives in the SUBSTITUTION (`Subst.LookupOrCreateTypeVar`
        // records it in `arityConstraints`), not in the type: the var itself
        // is a bare `IRTInfer` that says nothing about rank. Synthesizing
        // rank-1 for every var therefore refused every abstract parameter of
        // rank >= 2 at the unify below -- `extents(x)` on a `T^2` param died
        // with "a `^2` type variable is a rank-2 array, but this position
        // supplies Array<..>" (rank 1): the checker refusing a shape it had
        // itself just minted. Read the pin.
        //
        // The ARITY PIN ONLY, deliberately -- NOT `GetRankLowerBound`, the
        // stage-2 deduced bound sitting right beside it. The two are different
        // kinds of fact and this seam is where the difference is enforced: the
        // caret is a DECLARATION, so synthesizing its rank is honouring what
        // the author wrote, while a rank lower bound is accumulated EVIDENCE
        // from other call sites, and `unify`'s rankBoundViolation exists to
        // CHECK the synthesis against it. Synthesizing from the bound instead
        // makes that check vacuous: functions/037 (`z` collects rank 1 from
        // `total(z)`, rank 2 from `tot2(z)`, then meets `extents(z)`) is a
        // genuine contradiction reported as BL3009, the dedicated
        // rank-deduction code, and reading the bound here demoted it to a
        // BL3001 rank mismatch pointing at an unrelated call.
        //
        // MAX rather than the pin outright: an op demanding rank >= minRank
        // over a var pinned BELOW it (gram on a `T^1`) keeps synthesizing
        // minRank and keeps failing at unify -- that refusal is correct, and
        // its message already names both ranks.
        let pinnedRank =
            match env.Subst.GetArityConstraint vid with
            | Some k when k > 0 -> k
            | _ -> 0
        let k = max (max 1 minRank) pinnedRank
        let freshIdx i =
            // The minted extent name carries the index record's own fresh id.
            // These names are IDENTITY, not just display: shape
            // monomorphization (IRMono.shapeMonomorphizeModules) bakes call-site
            // extents into a spec BY NAME, so two distinct inference vars
            // sharing a display name (`__method_for_inferred_n` minted once
            // for a caller's param and once inside its callee) would be baked
            // to ONE value -- measured: a grouped pipeline whose spec baked
            // the callee's freqs-length T-dim (4) to the caller's input
            // length (6), overflowing every row copy at runtime.
            let idxId = env.Builder.FreshId()
            { Id = idxId
              Rank = 1
              Extent =
                IRParam ((if k = 1 then $"__{opName}_inferred_n_{idxId}"
                          else $"__{opName}_inferred_n{i}_{idxId}"),
                         0, IRTNat None)
              Symmetry = SymNone
              Tag = None; IxKind = IxKPlain
              Kind = SDimension
              Dependencies = []
            }
        // ELEMENT POLYMORPHISM SURVIVES THE SHAPE. This synthesis is where a
        // `T^k` DECLARATION parameter used to lose its HM element
        // polymorphism: giving `T` its shape rewrites it to `Array<E, ..>`,
        // and with `E` a plain fresh var zonk defaulted `E` to whatever the
        // first call site wanted, so the function existed at exactly one
        // element type (`function variance(x: T^1) = { let n = extents(x);
        // ... }` over a real and then a complex series failed in g++ with
        // "could not convert Array<complex<double>> to Array<double>").
        //
        // Fix: when the var being given its shape is itself a signature var
        // (IsPolymorphicId -- i.e. it came from a declared type-var NAME),
        // mint `E` in the SUBSTITUTION's id space and carry the mark onto it.
        // Zonk then leaves `E` open, `hasTypeVarsInParams` still sees the
        // function as HM, and IR-phase monomorphization specializes it per
        // call site exactly as it did before the shape was known. Subst.Bind
        // propagates the mark on var-to-var binds so a deferral inside the
        // body cannot drop it.
        //
        // Non-signature vars (an unannotated kernel parameter, a synthesized
        // intermediate) keep the plain builder-minted var: they are
        // monomorphic by construction and marking them would keep genuinely
        // dead vars alive through zonk.
        let freshElem =
            if env.Subst.IsPolymorphicId vid then
                let e = env.Subst.Fresh()
                (match e with
                 | IRTInfer eid -> env.Subst.MarkPolymorphic eid
                 | _ -> ())
                e
            else env.Builder.FreshInferType()
        let freshArrType = {
            ElemType = freshElem
            IndexTypes = List.init k freshIdx
            IsVirtual = false
            Identity = None
        }
        unify env.Subst tArr.Type (mkArrayLike freshArrType)
        |> Result.bind (fun () ->
            // Re-resolve in case unification refined the elem type via
            // some other constraint already in the substitution.
            match env.Subst.Resolve(tArr.Type) with
            | ArrayElem a -> Ok a
            | _ -> Error (IntrinsicBindArrayFailed opName))
    | _ ->
        Error (IntrinsicNeedsArray opName)

let requireArrayArg (env: TypeEnv) (tArr: TypedExpr) (opName: string) : TypeResult<IRArrayType> =
    requireArrayArgMinRank env tArr opName 1

/// S1 (docs/plan-kernel-body-materialization.md, M-B): materialize a
/// caret-shorthand `T^k` operand into the rank-k array it is already pinned to
/// be.
///
/// A `T^k` parameter is an arity-constrained inference VAR, not an IRTArray
/// (Subst.LookupOrCreateTypeVar): the caret pins the RANK and the array shape
/// is only built when some demand supplies it. Every array INTRINSIC issues
/// that demand (`requireArrayArg`; see inferProdSum's note for the long
/// version) -- but the two seams that make an array-valued INTERMEDIATE
/// (an elementwise binop, and a nested `<@>` whose operand is the var) both
/// gate their array-producing arms on the operand RESOLVING to an array, so
/// against an unmaterialized var they fall to the scalar arm and the
/// intermediate is scalar-typed. Every array consumer downstream then honestly
/// refuses a value that IS an array.
///
/// This is NOT a guess. Unify.fs's arity invariant already says an arity-k var
/// can bind to nothing but a rank-k array (anything else is an outright type
/// error there), so supplying the shape early can only pre-compute a binding
/// unification would have been forced into later. Errors are therefore
/// swallowed: the demand is an optimization of WHEN, never of WHETHER, and a
/// failure leaves the var exactly as it was for the pre-existing diagnostic to
/// report. Non-arity vars are left alone -- an unannotated kernel parameter is
/// one of those and may still resolve to a scalar.
///
/// THE DEMAND IS NOT FREE, and both of its call-site restrictions were paid for
/// in regressions. Binding a `T^k` var SPENDS it: in a named function's
/// declaration body that var is the HM-polymorphic signature var the
/// monomorphizer specializes on, so an unguarded demand collapses the function
/// to one specialization -- or worse, hands codegen a synthetic shape whose
/// invented `__..._inferred_n` extent nothing ever declares. Measured:
///
///   * `function packsum1(A: Poly<T^1>) -> T^1` folding `head + packsum1(tail)`
///     lost every `_HM_..._arr_double__r1s0e2` suffix across arity/019, 020,
///     021, 022, 024, 025, 026, 028; the recursive call vanished from the
///     emitted C++ and the surviving loop read `arr0.extents[0]` off an
///     undeclared name. Guard: the binop seam demands only when the OTHER
///     operand pins the shape (there, both were unresolved).
///   * `function dbl(xs: T^1) = xs <@> lambda(x) -> x + x |> compute`, applied
///     to a Float array and then an Int array, collapsed to ONE specialization
///     ("could not convert Array<long long int> to Array<double>"). Guard: the
///     loop-former seam demands only inside a LAMBDA body, where the var is
///     monomorphic anyway -- `buildApplyInfo` unifies a kernel param with the
///     iterated row type moments later, so pre-computing can only agree with
///     what unification was about to do.
///
/// Both guards live at the call sites, since they differ per seam.
let materializeArityVar (env: TypeEnv) (tArg: TypedExpr) (opName: string) : unit =
    match env.Subst.Resolve tArg.Type with
    | IRTInfer vid ->
        (match env.Subst.GetArityConstraint vid with
         | Some k when k >= 1 -> requireArrayArgMinRank env tArg opName k |> ignore
         | _ -> ())
    | _ -> ()

/// Tag-check helper: validate that each index argument's nominal tag (if any)
/// agrees with the corresponding array slot's nominal tag. Slot tags starting
/// with "__" are internal synthetic markers and skipped. Untagged ints into
/// named slots are permissive (warning emitted, no error) -- iteration-tagging
/// typically resolves these later.
///
/// Pulled out as a separate helper so the same logic can run BOTH at the
/// indexing call site (eager check via dispatchAppOrIndex) AND as a
/// post-unification pass over a kernel body (revalidateBodyTagChecks).
/// The index slot each POSITIONAL argument aligns to. One slot per arg --
/// except a compound head, whose rank-k axis consumes k FLAT subscripts, so
/// it repeats k times (mirroring the SymIdx-style flat consumption). Sparse
/// heads stay 1:1 (one tuple arg fills the slot).
let internal slotPerArg (arrTy: IRArrayType) : IRIndexType list =
    match arrTy.IndexTypes with
    | h :: rest when h.IxKind = IxKCompound -> List.replicate (max 1 h.Rank) h @ rest
    | l -> l

/// Is this subscript target a COMPILER-SYNTHESIZED buffer? Desugarings that
/// build their own scratch arrays (`let rec`'s `__rec_x` / `__slice_x` /
/// `__seed_x` / `__lag<k>_x`, rank-k `reduce`'s `__rksrc<uid>`) walk them with
/// their own generated counters, which are plain Int64 -- the desugarer owns
/// both the buffer and the walk, so the index space is correct by
/// construction. The untagged-index NOTE below is advice to the AUTHOR ("cast,
/// or iterate via range<Tag>"), and on a `__`-named buffer there is no source
/// spelling that could take it: a `let rec` with ZERO subscripts in the source
/// still drew one note per tagged axis. Same rule, and the same reason, as the
/// `tagName.StartsWith "__"` guard below and BL4010's `__of13_0` guard: a
/// diagnostic the user cannot act on is noise. The ERROR arms are unaffected
/// -- only the advisory warning is suppressed, and only on names the surface
/// grammar reserves for the compiler.
let internal isSynthesizedBuffer (tArr: TypedExpr) : bool =
    match tArr.Kind with
    | TExprVar (name, _, _) -> name.StartsWith "__"
    | _ -> false

/// The INDEX twin of `isSynthesizedBuffer`: a subscript that mentions a
/// compiler-reserved `__` name -- a desugarer's loop ordinal, possibly plus
/// the author's own offset arithmetic -- into a USER array. The reverse-mode
/// sweep reads the user's `Array<Float like H>` at `__hi3 + 2L` (or at
/// `__mi7 + (1L + one)`, when the author's window offset was a name) in a
/// loop it built itself; the author wrote neither the loop nor the
/// subscript, and the cast the note recommends has no source position to
/// land on. The surface grammar reserves `__` names for the compiler, so a
/// subscript mentioning one is never the author's. Only the advisory
/// warning is affected; the ERROR arms (a differently-tagged index) are not.
let internal isSynthesizedIndex (tArg: TypedExpr) : bool =
    let rec mentionsReserved (e: TypedExpr) : bool =
        match e.Kind with
        | TExprVar (name, _, _) -> name.StartsWith "__"
        | TExprBinOp (_, _, l, r) -> mentionsReserved l || mentionsReserved r
        | TExprUnaryOp (_, x) -> mentionsReserved x
        | TExprApp (f, args) -> mentionsReserved f || args |> List.exists mentionsReserved
        | TExprIf (c, t, f) -> mentionsReserved c || mentionsReserved t || mentionsReserved f
        | _ -> false
    mentionsReserved tArg

/// Render a swap counterexample from `Deduce.witnessSwapAsymmetry` for the
/// BL4013 `CommContradictsWitness` message.
let commWitnessText (p1: string) (p2: string) (x: float) (y: float) (u: float) (v: float) : string =
    let g (f: float) = f.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)
    $"at {p1} = {g x}, {p2} = {g y} the body is {g u}, but with the two exchanged ({p1} = {g y}, {p2} = {g x}) it is {g v}"

/// The arity-general rendering (Deduce.witnessSwapLawFailure): the other
/// parameters' fixed values are named, and an anticomm witness says what the
/// law required (the negation). `others` pairs each other parameter's NAME
/// with its value. With no other parameters and `negate = false` this is
/// exactly commWitnessText.
let swapWitnessText (p1: string) (p2: string) (others: (string * float) list) (negate: bool)
                    (x: float) (y: float) (u: float) (v: float) : string =
    let g (f: float) = f.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)
    let fixedPart =
        if List.isEmpty others then ""
        else
            let parts = others |> List.map (fun (n, c) -> $"{n} = {g c}") |> String.concat ", "
            $" (with {parts})"
    let required = if negate then $", not {g (-u)}" else ""
    $"at {p1} = {g x}, {p2} = {g y}{fixedPart} the body is {g u}, but with the two exchanged ({p1} = {g y}, {p2} = {g x}) it is {g v}{required}"

/// The integer value of a LITERAL subscript: `3`, `-1`, and `(3 : I)` (the
/// ascription retypes the literal node and leaves it a literal).
let internal subscriptLiteralValue (a: TypedExpr) : int64 option =
    match a.Kind with
    | TExprLit (LitInt v) -> Some v
    | TExprUnaryOp (OpNeg, { Kind = TExprLit (LitInt v) }) -> Some (-v)
    | _ -> None

/// Is this slot's tag a registered EnumIdx? Its int keys are stored RAW
/// (foreign-key semantics), so an int subscript there is a KEY, not a
/// position, and neither the class nor the range rule applies.
let internal slotIsEnumIdx (env: TypeEnv) (ix: IRIndexType) : bool =
    match ix.Tag with
    | Some t -> (match Map.tryFind t env.TypeDefs with Some (TDIEnumIdx _) -> true | _ -> false)
    | None -> false

/// The static extent a POSITION subscript into this slot must stay below:
/// only a plain, dense, one-coordinate slot has one (a compact group's
/// coordinates, a compound/sparse/tree/ragged slot and an EnumIdx key space
/// all count something else).
let internal subscriptStaticExtent (env: TypeEnv) (ix: IRIndexType) : int64 option =
    // A `__`-tagged (compiler-kind) slot's extent may be a placeholder that
    // folds later (an anonymous `0..n` range reads 0 while typed), so only a
    // user-visible slot is range-checked.
    let synthetic = match ix.Tag with Some t -> t.StartsWith "__" | None -> false
    if ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank <= 1 && not (slotIsEnumIdx env ix)
       && not synthetic
    then Blade.IRPrint.tryEvalIntIR ix.Extent
    else None

/// The static range of ONE flat coordinate of a compact group (SymIdx /
/// AntisymIdx / HermitianIdx, rank >= 2): every coordinate of `S(i, j)`
/// lies in `0 .. n-1` whatever the order (the read canonicalizes; an
/// antisymmetric diagonal reads its structural zero). Compact reads are not
/// run-time guarded (formalism 3.10: compact slots keep their own
/// discipline), so a literal coordinate is judged here instead.
let internal compactCoordExtent (ix: IRIndexType) : int64 option =
    let synthetic = match ix.Tag with Some t -> t.StartsWith "__" | None -> false
    match ix.Symmetry with
    | SymSymmetric | SymAntisymmetric | SymHermitian when ix.Rank >= 2 && ix.IxKind = IxKPlain && not synthetic ->
        Blade.IRPrint.tryEvalIntIR ix.Extent
    | _ -> None

/// The subscript judgment's CLASS and LITERAL rules for one subscript into
/// one slot (formalism 3.10):
///   * CLASS: a subscript is an integer or an index value. Float, Bool,
///     Complex and String are refused (they reached g++ as a `double`
///     subscript, or silently read `A(true)` as A(1)). An inference variable
///     is not judged yet; it is MARKED so zonk defaults it to Int64 rather
///     than Float64, and the post-zonk sweep judges what it became.
///   * LITERAL: an integer literal (`3`, `-1`, `(3 : I)`) is a known
///     position, checked against the slot's static extent here; a negative
///     literal is refused against any slot.
/// Sparse and EnumIdx slots are keyed, not positional, and stand down, as
/// does a `__`-named (compiler-synthesized) buffer's own walk.
let internal subscriptClassOrRangeError (env: TypeEnv) (synthetic: bool) (ix: IRIndexType) (tArg: TypedExpr) : TypeError option =
    // Keyed or window slots are not positions: a SparseIdx key, an EnumIdx
    // key, and a halo window's signed OFFSET (`w(-1)`) all stand down.
    let keyed =
        ix.IxKind = IxKSparse || slotIsEnumIdx env ix
        || (match ix.Tag with Some t -> t.StartsWith haloWinTagPrefix | None -> false)
    // A halo WINDOW read `w(o)` is the neighbour's position, resolved by the
    // halo machinery itself (BL3016 / BL8009 guard it); its node type is not
    // what codegen reads, and inside a generic body it may still be an
    // unconstrained (Float64-defaulted) variable.
    let windowRead =
        match tArg.Kind with
        | TExprApp (f, _) | TExprIndex (f, _, _) ->
            (match env.Subst.Resolve f.Type with
             | IRTIdxTagged (_, IRefNamed t) -> t.StartsWith haloWinTagPrefix
             | _ -> false)
        | _ -> false
    if keyed || windowRead || tArg.Kind.IsTExprWildcard then None
    else
        let classErr =
            match IR.stripUnits (env.Subst.Resolve tArg.Type) with
            | IRTInfer vid ->
                if (env.Subst.GetArityConstraint vid).IsNone then env.Subst.MarkIndexDefault vid
                None
            | IRTScalar (ETFloat32 | ETFloat64 | ETBool | ETComplex64 | ETComplex128 | ETString) as t ->
                Some (SubscriptNotIntegral (ppIRType t))
            // A tuple is a SparseIdx key (keyed, stood down above) or a
            // CompoundIdx's refused spelling (CompoundTupleForm); into a
            // positional slot it reached g++ as a std::tuple subscript
            // (BL9002) and the interpreter read cell 0.
            | IRTTuple _ when ix.IxKind = IxKPlain -> Some (SubscriptTupleForm (ppIndexType ix))
            | _ -> None
        match classErr with
        | Some e -> Some e
        // A compiler-synthesized buffer's own walk is the desugarer's to keep
        // in range (a `let rec` prefix read past the prefix is CLAMPED and
        // zeroed there), so the literal rule stands down on it.
        | None when synthetic -> None
        | None ->
            match subscriptLiteralValue tArg with
            | Some v when v < 0L && ix.IxKind = IxKPlain
                          && not (match ix.Tag with Some t -> t.StartsWith "__" | None -> false) ->
                Some (SubscriptOutOfRange (v, None, ppIndexType ix))
            | Some v ->
                (match subscriptStaticExtent env ix |> Option.orElse (compactCoordExtent ix) with
                 | Some ext when v >= ext -> Some (SubscriptOutOfRange (v, Some ext, ppIndexType ix))
                 | _ -> None)
            | None -> None

/// THE CHECKED CONVERSION of a plain integer into a named index type I with
/// static extent n (formalism 3.10): the value is bound once and guarded
/// `0 <= v < n` at run time (BL8006), yielding a `Nat<I>`. An ordinary typed
/// block around an ordinary constraint check, so both backends (and every
/// IR pass) already know it. Shared by the cast `(e : I)` and the call
/// judgment's integer-into-index-parameter coercion.
let internal checkedIndexConversion (env: TypeEnv) (tE: TypedExpr) (annoTy: IRType)
                                    (tag: string) (n: int64) (span: Span) : TypedExpr =
    let ckName = $"{indexCastBindingPrefix}{env.Builder.FreshId()}"
    let ckId = env.Builder.FreshId()
    let intTy = IRTScalar ETInt64
    let boolTy = IRTScalar ETBool
    let mk k ty = mkTypedSpan k ty span
    let ckVar = mk (TExprVar (ckName, ckId, None)) tE.Type
    let cond =
        mk (TExprBinOp (Elementwise, OpAnd,
                        mk (TExprBinOp (Elementwise, OpLe, mk (TExprLit (LitInt 0L)) intTy, ckVar)) boolTy,
                        mk (TExprBinOp (Elementwise, OpLt, ckVar, mk (TExprLit (LitInt n)) intTy)) boolTy)) boolTy
    let msg = $"index cast out of range: the value is outside {tag} (0 .. {n - 1L})"
    let tb : TypedBinding = {
        Name = ckName; VarId = ckId; Type = tE.Type
        Identity = None; IsMutable = false; Value = tE
        SubBindings = []; Destructure = DSPositional; PostChecks = []; ErasedType = None }
    mk (TExprBlock ([ TStmtLet tb; TStmtExpr (mk (TExprConstraintCheck (cond, "BL8006", msg)) IRTUnit) ],
                    Some (mk (TExprVar (ckName, ckId, None)) annoTy)))
       annoTy

/// Arithmetic on an index value is a POSITION -- a plain integer, whatever
/// type the node took from an open operand (formalism 3.10, "Positions and
/// casts") -- so an index cast of one is the integer conversion, never the
/// foreign-tag refusal.
let internal isIndexPositionExpr (t: TypedExpr) : bool =
    match t.Kind with
    | TExprBinOp (_, (OpAdd | OpSub | OpMul | OpDiv | OpMod), _, _)
    | TExprUnaryOp (OpNeg, _) -> true
    | _ -> false

/// The named integer index type a parameter type demands, with its static
/// extent: `Nat<I>` / `I` where I is a registered plain dense index type.
let internal namedIndexParam (env: TypeEnv) (t: IRType) : (string * int64 option) option =
    match IR.stripUnits (env.Subst.Resolve t) with
    | IRTIdxTagged (IRTScalar (ETInt32 | ETInt64), IRefNamed tag) ->
        (match Map.tryFind tag env.TypeDefs with
         | Some (TDIIndexType (_, idx, _)) when idx.IxKind = IxKPlain && idx.Symmetry = SymNone ->
             Some (tag, Blade.IRPrint.tryEvalIntIR idx.Extent)
         | Some (TDIEnumIdx _) -> None
         | _ -> Some (tag, None))
    | _ -> None

/// A plain integer ARGUMENT meeting an index PARAMETER `Nat<I>` (the call
/// judgment allows untagged into tagged -- a literal `at(2)` is the common
/// case) passes through the same two doors as the cast `(e : I)`: a literal
/// is range-checked at compile time, a computed integer becomes the checked
/// conversion. Without it the parameter's index type promised a bound
/// nothing established.
let internal coerceIndexArgs (env: TypeEnv) (paramTys: IRType list) (tArgs: TypedExpr list) : TypeResult<TypedExpr list> =
    let n = min paramTys.Length tArgs.Length
    tArgs
    |> List.mapi (fun i a ->
        if i >= n then Ok a
        else
            match namedIndexParam env paramTys.[i] with
            | None -> Ok a
            | Some (tag, ext) ->
                match subscriptLiteralValue a, ext with
                | Some v, _ when v < 0L -> Error (SubscriptOutOfRange (v, None, tag))
                | Some v, Some e when v >= e -> Error (SubscriptOutOfRange (v, Some e, tag))
                | Some _, _ -> Ok a
                | None, Some e ->
                    (match IR.stripUnits (env.Subst.Resolve a.Type) with
                     | IRTScalar (ETInt32 | ETInt64) when not (isSynthesizedIndex a) ->
                         Ok (checkedIndexConversion env a (env.Subst.Resolve paramTys.[i]) tag e a.Span)
                     | _ -> Ok a)
                | None, None -> Ok a)
    |> sequenceResults

/// Pair each subscript with the index slot it addresses, for the subscript
/// judgment. One slot per argument, except: a COMPOUND head consumes k flat
/// coordinates (replicated k times) -- unless the arguments were already
/// PACKED into the head's tuple (the typed node dispatch builds), in which
/// case the tuple pairs with the head; a SPARSE head takes one tuple; and a
/// rank-k COMPACT group (SymIdx / AntisymIdx / HermitianIdx) read with its
/// k flat coordinates pairs each coordinate with the group.
let internal subscriptSlotPairs (arrTy: IRArrayType) (args: TypedExpr list) : (TypedExpr * IRIndexType) list =
    let zipTrunc (xs: TypedExpr list) (ys: IRIndexType list) =
        let n = min xs.Length ys.Length
        List.zip (List.truncate n xs) (List.truncate n ys)
    match arrTy.IndexTypes, args with
    | h :: rest, a0 :: more when (h.IxKind = IxKCompound || h.IxKind = IxKSparse) && a0.Kind.IsTExprTuple ->
        (a0, h) :: zipTrunc more rest
    | ixs, _ ->
        let isCompact (ix: IRIndexType) =
            ix.Rank >= 2
            && (match ix.Symmetry with
                | SymSymmetric | SymAntisymmetric | SymHermitian -> true
                | SymNone | SymWreath -> false)
        let flatCompact =
            args.Length > ixs.Length
            && args.Length = (ixs |> List.sumBy (fun ix -> max 1 ix.Rank))
        let slots =
            ixs |> List.collect (fun ix ->
                if ix.IxKind = IxKCompound then List.replicate (max 1 ix.Rank) ix
                elif flatCompact && isCompact ix then List.replicate ix.Rank ix
                else [ ix ])
        zipTrunc args slots

let internal checkArrayIndexTags (env: TypeEnv) (tArr: TypedExpr) (arrTy: IRArrayType) (tArgs: TypedExpr list) : TypeResult<unit> =
    let synthetic = isSynthesizedBuffer tArr
    let tagMismatch =
        subscriptSlotPairs arrTy tArgs
        |> List.tryPick (fun (tArg, idxType) ->
            // THE SUBSCRIPT JUDGMENT (formalism 3.10), class and literal rules
            // first; the nominal tag rule below is the third.
            match subscriptClassOrRangeError env synthetic idxType tArg with
            | Some err -> Some err
            | None ->
            // An integer LITERAL proved in range against a static extent needs
            // no advice: the compiler has just checked the very thing the
            // untagged-integer note warns about.
            // Likewise a literal coordinate of a compact group (SymIdx<2, X>),
            // range-checked per coordinate by the class/literal rule above.
            if (subscriptLiteralValue tArg).IsSome
               && ((subscriptStaticExtent env idxType).IsSome || (compactCoordExtent idxType).IsSome) then None else
            match idxType.Tag with
            | Some tagName when not (tagName.StartsWith("__")) ->
                match env.Subst.Resolve tArg.Type with
                | IRTIdxTagged (_, IRefNamed argName)
                    when argName = tagName -> None
                | IRTIdxTagged (_, IRefNamed argName) ->
                    Some (IndexTagMismatchNamed (tagName, argName))
                | IRTIdxTagged (_, IRefAnon _) ->
                    Some (IndexTagMismatchAnon tagName)
                // A `Base<_>` parameter declined to constrain the tag, so it
                // carries no more guarantee than an untagged int -- warn with
                // the same text rather than erroring, keeping the wildcard
                // usable as the documented escape hatch for raveled indices.
                | IRTIdxTagged (_, IRefAny)
                | IRTScalar (ETInt32 | ETInt64) ->
                    // BL4003 (index type violation) -- the warning twin of this
                    // very site: the ERROR branch two cases up raises
                    // IndexTagMismatchNamed, which is already BL4003.
                    if not synthetic && not (isSynthesizedIndex tArg) then
                        emitWarning env "BL4003" tArg.Span ($"Array indexed with untagged integer where slot expects tag '{tagName}'. Consider an explicit cast like `(expr : {tagName})` or iterate via `range<{tagName}>` to flow the tag automatically.")
                    None
                | _ -> None
            | _ -> None)
    match tagMismatch with
    | Some err -> Error err
    | None -> Ok ()

/// EnumIdx label subscript: a STRING LITERAL index into an axis whose index
/// type is a registered EnumIdx folds to its ordinal HERE, so lowering,
/// codegen, and the interpreter all see a plain constant subscript -- the
/// CSV headered-column access idiom `obs.vars.data[i, "temp"]`. The folded
/// literal is retyped IRTIdxTagged so the nominal tag check accepts it.
/// Restricted to STRING literals: int-valued EnumIdx keys are stored raw
/// (foreign-key semantics, sql-foreign-keys corpus) and not position-folded.
/// An unknown label is a type error naming the available labels; runtime
/// (non-literal) label subscripts stay unsupported.
let internal foldEnumIdxLabels (env: TypeEnv) (arrTy: IRArrayType) (tArgs: TypedExpr list) : TypeResult<TypedExpr list> =
    let slots = slotPerArg arrTy |> Array.ofList
    tArgs
    |> List.mapi (fun i a ->
        if i >= slots.Length then Ok a
        else
            match a.Kind, slots.[i].Tag with
            | TExprLit (LitString s), Some tagName ->
                (match Map.tryFind tagName env.TypeDefs with
                 | Some (TDIEnumIdx (_, _, values, _)) ->
                     (match values |> List.tryFindIndex ((=) (EVString s)) with
                      | Some ord ->
                          Ok { a with
                                Kind = TExprLit (LitInt (int64 ord))
                                Type = IRTIdxTagged (IRTScalar ETInt64, IRefNamed tagName) }
                      | None ->
                          let avail = values |> List.map (function EVString v -> v | EVInt n -> string n)
                          Error (EnumIdxUnknownLabel (tagName, s, avail)))
                 | _ -> Ok a)
            | _ -> Ok a)
    |> sequenceResults

/// Index-arity validation for a CompoundIdx slot (formalism 4.5): when the
/// NEXT slot is a compound of Rank k, the coordinate filling it must be a
/// single k-tuple `B((c0, ..., c_{k-1}))` (the canonical poly-index form,
/// 5.4) -- NOT the flat currying form `B(c0, c1)`, since currying one
/// coordinate at a time would make partial forms like `B(c0)(_)(c2)`
/// ambiguous with wildcards. Fires only when the head slot is compound;
/// validates the FIRST arg against it and lets remaining args flow to
/// trailing regular slots (`B((i,j), t)` is allowed). Rank-1 compound
/// (k=1) also accepts a bare scalar `B(i)` (the parser collapses a 1-tuple
/// to a bare expr); k >= 2 requires the tuple form.
///
/// Keyed on the head slot's IxKind:
///   COMPOUND -- FLAT positional subscripts like SymIdx: B(c0,...,c(k-1),t).
///       Full-arity only; tuple/wildcard/partial forms are rejected (moved
///       to SparseIdx). Also owns flat-count accounting (under/over-supply).
///   SPARSE -- one TUPLE per sparse axis (3.5 currying): S((a,b)), S((a,_)),
///       short prefix tuples; wildcards mark free axes.
let internal validateTabulatedIndex (env: TypeEnv) (arrTy: IRArrayType) (tArgs: TypedExpr list) : TypeResult<unit> =
    match arrTy.IndexTypes with
    | headSlot :: trailingSlots when headSlot.IxKind = IxKCompound ->
        let k = headSlot.Rank
        (match tArgs with
         | [] -> Ok ()  // bare array value; nothing to check
         | args ->
             let isWild (e: TypedExpr) = e.Kind.IsTExprWildcard
             let firstIsTuple =
                 args.Head.Kind.IsTExprTuple
                 || (env.Subst.Resolve args.Head.Type).IsIRTTuple
             if firstIsTuple then Error (CompoundTupleForm k)
             elif args |> List.exists isWild then Error (CompoundTupleForm k)
             elif args.Length < k then Error (CompoundUnderSupplied (k, args.Length))
             elif args.Length > k + trailingSlots.Length then Error (CompoundOverSupplied (k, args.Length))
             else Ok ())
    | headSlot :: _ when headSlot.IxKind = IxKSparse ->
        let k = headSlot.Rank
        match tArgs with
        | [] -> Ok ()  // no args consumed here (e.g. bare array value); nothing to check
        | firstArg :: _ ->
            // Wildcard sparse indexing: a FULL-arity tuple with `_` marking
            // FREE axes (3.5 currying: S((a,_)), S((_,b)), S((a,_,_)), ...).
            // Residual rank = wildcard count (1 free -> dense Idx gather;
            // >=2 -> residual SparseIdx). Multiple wildcards are ALLOWED
            // here, unlike function partial application (6.2.3, single `_`
            // only): S((a,_,_)) is the only way to pin a single leading
            // coordinate of a rank-3 sparse, since `(a)` collapses to a
            // bare scalar in the parser.
            let wildcardPositions =
                match firstArg.Kind with
                | TExprTuple elems ->
                    elems |> List.mapi (fun i e -> (i, e))
                          |> List.choose (fun (i, e) -> match e.Kind with TExprWildcard -> Some i | _ -> None)
                | _ -> []
            match firstArg.Kind, wildcardPositions with
            | TExprWildcard, _ ->
                // Bare `S(_)` (the parser collapses a 1-tuple `(_)` to the bare
                // hole). It pins nothing: on a rank-1 head the "residual"
                // would be the whole array, and on rank >= 2 it is not even a
                // tuple. Reject rather than let the hole flow as a coordinate.
                Error (SparseBareWildcard k)
            | _, (_ :: _) ->
                let tupleLen =
                    match firstArg.Kind with
                    | TExprTuple es -> es.Length
                    | _ -> 0
                if tupleLen <> k then
                    Error (SparseWildcardArity (k, tupleLen))
                elif wildcardPositions.Length = k then
                    Error (SparseAllFree k)
                else Ok ()
            | _, [] ->
              (match env.Subst.Resolve firstArg.Type with
               | IRTTuple tys when tys.Length >= 1 && tys.Length <= k -> Ok ()
                // 1 <= j <= k: full (j = k) or partial (j < k, leading-prefix)
                // index. The residual type is computed by tabulatedResidualType;
                // codegen reconstitutes it as a gather.
               | IRTTuple tys ->
                   Error (SparseOverSupplied (k, tys.Length))
               | _ when k = 1 -> Ok ()  // rank-1 head: bare scalar index is fine
               | _ ->
                   // k >= 2 but the first arg is not a tuple. This is the flat
                   // currying form S(c0, c1, ...) or a single scalar -- reject and
                   // point at the canonical tuple form.
                   Error (SparseNeedsTuple k))
    | _ -> Ok ()

/// The residual index-type fragment (formalism 4.5 currying table) that
/// REPLACES a rank-k compound slot after pinning j of its coordinates.
/// Pinned axes are the leading prefix (short tuple) or non-`_` positions
/// (full-arity wildcard tuple) -- shape depends only on the count, not
/// position. Pinned POSITIONS live in the index tuple itself (unit-literal
/// sentinels at free axes); codegen reads them to pick window vs gather.
///
///   j = k       -> []            (compound fully consumed; trailing dims follow)
///   k - j = 1   -> [dense Idx]   (contiguous window of present cells)
///   k - j >= 2  -> [CompoundIdx] (residual masked product over k-j axes)
///
/// Both residual cases carry Extent = IRCompoundProject(parentIR, j);
/// placementOf reads the residual RANK to pick dense vs tabulated.
let internal tabulatedResidualType (headSlot: IRIndexType) (parentIR: IRExpr) (j: int) (fresh: unit -> IRId) : IRIndexType list =
    let k = headSlot.Rank
    let residualRank = k - j
    if residualRank <= 0 then
        []  // j = k: fully consumed
    elif residualRank = 1 then
        // Dense residual Idx: for a compound head, a contiguous [lo,hi)
        // window (prefix) or gather (scattered); for sparse, always a
        // gathered dense copy in key order.
        [ { Id = fresh (); Rank = 1
            Extent = IRCompoundProject (parentIR, j)
            Symmetry = SymNone; Tag = None; IxKind = IxKPlain
            Kind = SDimension; Dependencies = [] } ]
    else
        // Residual keeps the PARENT's kind (partial compound stays compound,
        // partial sparse stays sparse), so it's further indexable/partial-
        // indexable just like a top-level one.
        let tag, kind =
            match headSlot.IxKind with
            | IxKSparse -> Some "__sparseidx", IxKSparse
            | _ -> Some "__compoundidx", IxKCompound
        [ { Id = fresh (); Rank = residualRank
            Extent = IRCompoundProject (parentIR, j)
            Symmetry = SymNone; Tag = tag; IxKind = kind
            Kind = SDimension; Dependencies = [] } ]

/// Rank as CODEGEN will see it -- the `k` in `Array<elem, k>`, 0 for a
/// scalar -- when the type is concrete enough to know. `None` means
/// "unknown, stand down" (inference vars, poly packs, funcs, structs,
/// tuples, loops, dists); callers compare two `Some` ranks only, so an
/// unresolved type never manufactures a mismatch.
///
/// SINGLE SOURCE OF TRUTH for the direct-application rank check: the eager
/// check in `dispatchAppOrIndex`'s FuncElem arm and the post-unification
/// sweep `collectAppRankErrors` both call this, since the first cannot see
/// through an open variable and the two must agree on what a rank is.
let concreteRankOf (subst: Subst) (ty: IRType) : int option =
    let rec go t =
        match subst.Resolve t with
        | ArrayElem a -> Some (a.IndexTypes |> List.sumBy (fun i -> max 1 i.Rank))
        | IRTScalar _ -> Some 0
        | IRTUnitAnnotated (inner, _) -> go inner
        | IRTIdxTagged (inner, _) -> go inner
        | _ -> None
    go ty

/// Pair each argument with the parameter it binds, 1:1 and positional,
/// truncated to the shorter list: (0-based parameter position, param type,
/// arg type).
///
/// Stays 1:1 under the width schema (docs/plan-tuples-vs-arg-packs.md 6c), and
/// that is the design, not an omission: `regroupArgsByWidth` runs FIRST at the
/// FuncElem arm and hands every check below an argument list already regrouped
/// to one node per parameter. So the pairing rule lives in exactly one place,
/// and each per-pair check here keeps comparing one parameter against one
/// value. If a future change moves regrouping later, this is the function that
/// has to learn about widths instead.
let appArgPairs (paramTys: IRType list) (argTys: IRType list)
                : (int * IRType * IRType) list =
    let n = min paramTys.Length argTys.Length
    List.zip (List.truncate n paramTys) (List.truncate n argTys)
    |> List.mapi (fun i (pTy, aTy) -> (i, pTy, aTy))

/// WIDTH SCHEMA at the DIRECT-CALL seam (docs/plan-tuples-vs-arg-packs.md 6c).
/// The argument list is a list of NODES -- one per written argument, nesting
/// preserved -- matched greedily against the parameter list read as a width
/// schema. A `Tuple<k>` parameter (declared `Tuple<k>` or `(T1, .., Tk)`; both
/// lower to `IRTTuple`) prefers one k-wide tuple node and otherwise regroups k
/// consecutive nodes, so `addPair(b, c)`, `addPair((b, c))` and
/// `let t = b, c; addPair(t)` are the same call.
///
/// Deliberately one-directional. The reverse -- expanding a tuple ARGUMENT into
/// k scalar arguments for k scalar parameters -- is NOT done, because `f(t)` on
/// a 2-parameter `f` is already partial application (Parser/ExprApp eta-expands
/// it), and the language cannot have both readings. Every arm below fires only
/// where the plain pairing has no reading at all, so it can turn an error into
/// a call but never redirect one that already type-checks. That is also why
/// 6c rule 3's `f((a, b)) == f(a, b)` holds at the OPERAND seam but not here:
/// at a call, the left-hand side is partial application and already means
/// something. Reported as a deviation rather than silently.
let regroupArgsByWidth (env: TypeEnv) (paramTys: IRType list) (tArgs: TypedExpr list)
                       : TypedExpr list =
    let subst = env.Subst
    let widthOf (t: IRType) =
        match subst.Resolve t with
        | IRTTuple ts when ts.Length >= 2 -> ts.Length
        | _ -> 1
    /// The components of a single tuple-typed argument, seen through any number
    /// of alias hops (the same fixpoint `resolveTypedExprDeep` applies at the
    /// operand seam, inlined because that one lives in the later `and` group).
    let tupleParts (e: TypedExpr) : TypedExpr list option =
        let rec chase (fuel: int) (x: TypedExpr) =
            if fuel <= 0 then x
            else
                match x.Kind with
                | TExprVar (name, _, _) ->
                    (match lookupVar name env with
                     | Some info ->
                         (match info.TypedValue with
                          | Some v when not (System.Object.ReferenceEquals(v, x)) -> chase (fuel - 1) v
                          | _ -> x)
                     | None -> x)
                | _ -> x
        match (chase 64 e).Kind with
        | TExprTuple es when es.Length >= 2 -> Some es
        | _ -> None
    let widths = paramTys |> List.map widthOf
    // Greedy left-to-right fold over a NODE list (6c rule 2): a `Tuple<k>`
    // parameter PREFERS one tuple node of top-level width k, and otherwise
    // regroups k consecutive nodes. Nesting is preserved either way -- the
    // regrouped tuple keeps whatever the nodes were. `None` when the schema
    // does not fit these nodes.
    let fold (nodes: TypedExpr list) : TypedExpr list option =
        let mutable rest = nodes
        let mutable ok = true
        let out =
            widths
            |> List.map (fun w ->
                if not ok then []
                elif w = 1 then
                    match rest with
                    | a :: t -> rest <- t; [a]
                    | [] -> ok <- false; []
                else
                    match rest with
                    | a :: t when (match subst.Resolve a.Type with
                                   | IRTTuple ts -> ts.Length = w
                                   | _ -> false) ->
                        rest <- t; [a]
                    | _ when rest.Length >= w ->
                        let taken = rest |> List.truncate w
                        rest <- rest |> List.skip w
                        [ { Kind = TExprTuple taken
                            Type = IRTTuple (taken |> List.map (fun (a: TypedExpr) -> a.Type))
                            Span = (List.head taken).Span } ]
                    | _ -> ok <- false; [])
            |> List.concat
        if ok && List.isEmpty rest then Some out else None
    if List.sum widths = paramTys.Length then tArgs        // no tuple parameter
    elif tArgs.Length = paramTys.Length then tArgs
        // Already 1:1 -- and this IS 6c rule 3's precedence (a): a lone
        // `Tuple<m>` parameter facing a lone m-tuple argument DIRECT-BINDS, so
        // `lam(((a,b),(c,d)))` against `lambda(r: Tuple<2>)` gives r the pair
        // of pairs rather than splicing it into two.
    else
        match fold tArgs with
        | Some out -> out
        | None ->
            // Precedence (b): the ONE-LEVEL SPLICE. A whole argument list that
            // is a single tuple node opens once and the schema is re-offered
            // the components -- `f(((a,b),(c,d)))` against
            // `f(p: Tuple<2>, q: Tuple<2>)`. Tried only AFTER the unspliced
            // fold, so the direct match always wins; and it can only rescue a
            // call that had no reading at all, never redirect one that did.
            let spliced =
                match tArgs with
                | [single] ->
                    (match tupleParts single with
                     | Some parts -> parts
                     | None -> tArgs)
                | _ -> tArgs
            if spliced.Length = tArgs.Length then tArgs
            else match fold spliced with
                 | Some out -> out
                 | None -> tArgs

/// The rank comparison itself, over a parameter list and an argument list:
/// the first position whose two ranks are both known and disagree, as
/// (0-based position, param rank, arg rank, param type, arg type).
///
/// Two arms stand down entirely rather than compete with a better
/// diagnostic:
///   * A `Poly<T^r>` pack param makes the arrow variadic -- positional
///     pairing is meaningless there (monomorphization owns the call).
///   * UNDER-application. Fewer args than params is an arity error, and the
///     arity message names the real defect; a rank complaint about the args
///     that ARE present would bury it. (OVER-application still checks the
///     prefix this arrow consumes -- the surplus re-dispatches against the
///     result type and gets its own pass.)
let firstArgRankClash (subst: Subst) (paramTys: IRType list) (argTys: IRType list)
                      : (int * int * int * IRType * IRType) option =
    let isVariadic =
        paramTys |> List.exists (fun t -> (subst.Resolve t).IsIRTPoly)
    if isVariadic || argTys.Length < paramTys.Length then None
    else
        appArgPairs paramTys argTys
        |> List.tryPick (fun (i, pTy, aTy) ->
            match concreteRankOf subst pTy, concreteRankOf subst aTy with
            | Some pr, Some ar when pr <> ar -> Some (i, pr, ar, pTy, aTy)
            | _ -> None)

/// The ABSTRACT-PARAMETER conflict: two argument positions that teach the
/// SAME open signature type variable two incompatible types.
///
/// `function add0(a: T^0, b: T^0)` declares ONE variable in two positions, so
/// `add0(A, s)` asks `T` to be both `Array<Float64 like Idx<3>>` and
/// `Float64`. Nothing refused it. The DECLARATION's `T` is never bound by a
/// call (the call judgment unifies an instantiated COPY -- see
/// `instantiateOpenVars`), so on the declaration `T` stays an open `IRTInfer`
/// and a rank check stands down on it: `concreteRankOf` DECLINES an open
/// variable. The eager seam's judgment also refuses these (its copy of `T`
/// is taught by the first argument); this predicate keeps the wording and
/// is what the post-zonk sweep runs on arguments still open at the seam.
/// IR-phase
/// monomorphization then took the FIRST teaching and silently discarded the
/// rest (`IRMono.unifyParamWithArg`'s "inconsistent" arm, whose comment said
/// the IR validator would catch it -- it does not), emitting a specialization
/// whose parameters all wear the first argument's type against a call site
/// that hands it the others verbatim. g++ rejected it. That is a typecheck
/// ESCAPE: `blade check` clean, then a C++ error carrying no BL code at all.
///
/// This is the refusal that closes it, and it is deliberately the exact
/// MIRROR of what monomorphization would drop -- the same structural walk, so
/// "the specializer would lose this binding" and "the typechecker refuses"
/// are one predicate rather than two that can drift apart.
///
/// COMPATIBILITY is judged by what the emitted monomorph would accept, not by
/// type equality: the specialization is built from the FIRST teaching, so a
/// later argument is fine exactly when it flows into that signature without
/// conversion. Equal types, and scalars that WIDEN -- `add0(2.5, 3)` is
/// `double add0(double, double)` fed an int64, which C++ promotes and which
/// works today; `add0(3, 2.5)` is `int64 add0(int64, int64)` fed a double,
/// which `-Werror=float-conversion` rejects, and so does this. Anything not
/// determined here (an argument still open, a shape this walk does not model)
/// stands down rather than guessing, the same discipline as its neighbours.
///
/// Reported as (first teaching's position, conflicting position, first type,
/// conflicting type), all 0-based.
let firstAbstractVarConflict (subst: Subst) (paramTys: IRType list) (argTys: IRType list)
                             : (int * int * IRType * IRType) option =
    // Peel the wrappers that are transparent to a monomorph's C++ signature:
    // a unit annotation and an index tag are both erased by codegen, so
    // neither can make two teachings genuinely different shapes.
    let rec peel (t: IRType) =
        match subst.Resolve t with
        | IRTUnitAnnotated (inner, _) -> peel inner
        | IRTIdxTagged (inner, _) -> peel inner
        | r -> r
    let rec compatible (first: IRType) (later: IRType) : bool =
        let f = peel first
        let l = peel later
        if f = l then true
        else
            match f, l with
            | ArrayElem fa, ArrayElem la ->
                fa.IndexTypes.Length = la.IndexTypes.Length
                && compatible fa.ElemType la.ElemType
            // A rank disagreement is the g++-fatal one: an `Array<double, 1>`
            // parameter cannot be handed a `double`, in either direction.
            | ArrayElem _, _ | _, ArrayElem _ -> false
            | IRTScalar fe, IRTScalar le -> promoteElemType fe le = Some fe
            // Not determined here, or a shape this walk does not model.
            | _ -> true
    // Same two stand-downs as `firstArgRankClash`, for the same reasons: a
    // variadic `Poly<T^r>` pack makes positional pairing meaningless
    // (monomorphization owns those calls), and under-application is an arity
    // error whose own message must not be buried.
    let isVariadic = paramTys |> List.exists (fun t -> (subst.Resolve t).IsIRTPoly)
    if isVariadic || argTys.Length < paramTys.Length then None
    else
        // What each argument position teaches, in `unifyParamWithArg`'s walk
        // order -- but KEEPING every teaching instead of the first, because
        // the discarded ones ARE the defect.
        let teachings = System.Collections.Generic.List<int * int * IRType>()
        let rec learn (pos: int) (pTy: IRType) (aTy: IRType) =
            match subst.Resolve pTy, subst.Resolve aTy with
            | IRTInfer n, t -> teachings.Add((n, pos, t))
            | ArrayElem pa, ArrayElem aa -> learn pos pa.ElemType aa.ElemType
            | IRTTuple pts, IRTTuple ats when pts.Length = ats.Length ->
                List.zip pts ats |> List.iter (fun (p, a) -> learn pos p a)
            | IRTUnitAnnotated (pi, _), _ -> learn pos pi aTy
            | _, IRTUnitAnnotated (ai, _) -> learn pos pTy ai
            | IRTIdxTagged (pi, _), IRTIdxTagged (ai, _) -> learn pos pi ai
            | _ -> ()
        appArgPairs paramTys argTys |> List.iter (fun (i, pTy, aTy) -> learn i pTy aTy)
        let seen = System.Collections.Generic.Dictionary<int, int * IRType>()
        teachings
        |> Seq.tryPick (fun (varId, pos, ty) ->
            match subst.Resolve ty with
            // An argument whose own type is still open teaches nothing: it
            // cannot conflict, and pinning it here would be a guess.
            | IRTInfer _ -> None
            | resolved ->
                match seen.TryGetValue varId with
                | true, (firstPos, firstTy) ->
                    if compatible firstTy resolved then None
                    else Some (firstPos, pos, firstTy, resolved)
                | _ ->
                    seen.[varId] <- (pos, resolved)
                    None)

/// The message for a `firstAbstractVarConflict` verdict. Lives beside the
/// predicate so the eager seam (`dispatchAppOrIndex`) and the post-zonk sweep
/// (`collectAppRankErrors`) cannot word the same refusal two ways. Routed
/// through `Other` (BL3999), the channel the sibling caret-arity refusal
/// already uses -- this is the same family of judgement about what a `T^k`
/// annotation claims.
let abstractVarConflictMessage (subst: Subst) (callee: string)
                               (firstPos: int) (conflictPos: int)
                               (firstTy: IRType) (conflictTy: IRType) : string =
    let rankOf t = concreteRankOf subst t |> Option.defaultValue -1
    let r1 = rankOf firstTy
    let r2 = rankOf conflictTy
    let tail =
        if r1 >= 1 && r2 >= 1 then
            "two arrays of different ranks share no iteration space, so nothing here can deduce the "
            + "output rank -- reshape one of them, or spell the iteration you want with "
            + "`method_for(...) <@> ...`."
        elif r1 >= 1 || r2 >= 1 then
            "a scalar BROADCASTS across a rank-0 parameter list -- `f(A, 2.0)` iterates A and lifts the "
            + "scalar, exactly as `atan2(A, 2.0)` does -- but a `T^k` parameter with k >= 1 is an ARRAY "
            + "by declaration, so a scalar in that position is the wrong shape. Pass an array of the "
            + "declared rank, drop the caret where the value is an element, or give that parameter its "
            + "own concrete type (`b: Float`)."
        else
            "the specialization is built from the FIRST argument's type, so a later argument that would "
            + "have to NARROW into it is refused -- widen the earlier argument, or cast at the call site."
    $"arguments {firstPos + 1} and {conflictPos + 1} of {callee} disagree about the same abstract "
    + $"parameter: the signature spells ONE type variable in both positions, and argument "
    + $"{firstPos + 1} makes it {(ppIRType (subst.Resolve firstTy))} while argument {conflictPos + 1} "
    + $"makes it {(ppIRType (subst.Resolve conflictTy))}. " + tail

/// The synthetic base dimension standing for parameter `i`'s unit while
/// `funcUnitTransform` probes a function body. `Unit` declarations are ordinary
/// identifiers and a `UnitSig`'s dims are keyed by plain strings, so a name no
/// surface syntax can produce is a free variable of the unit algebra: whatever
/// exponent it carries OUT of the body is the exponent the body applies to that
/// argument. Nothing but the probe ever sees these -- `funcUnitTransform`
/// removes them from the residual before recording it.
let unitProbeBase (i: int) : string = $"__unit_probe_{i}"

/// The probe's EQUALITY collector (see TypeEnv.FuncUnitEqualities). Active
/// only while `funcUnitTransform` walks a body; `kernelBodyUnits` hands it
/// the two sides of every unit-equality op (`+ - %`, comparisons, atan2,
/// branch agreement, a transcendental's dimensionless argument) that
/// mentions a probe base, instead of failing on it. Per-thread, like every
/// other piece of checker state that is not on the env.
let unitProbeEqualities =
    new System.Threading.ThreadLocal<ResizeArray<UnitSig * UnitSig> option ref>(fun () -> ref None)

let internal unitSigMentionsProbe (u: UnitSig) : bool =
    (unitNormalize u).Dims |> Map.exists (fun k _ -> k.StartsWith "__unit_probe_")

/// Record `a = b` if a probe is running and either side is probe-dependent;
/// false (nothing recorded) otherwise, so the caller keeps its own verdict.
let internal recordProbeEquality (a: UnitSig) (b: UnitSig) : bool =
    match unitProbeEqualities.Value.Value with
    | Some acc when unitSigMentionsProbe a || unitSigMentionsProbe b ->
        acc.Add((a, b))
        true
    | _ -> false

/// Instantiate one side of a recorded equality at a call: every probe base
/// `__unit_probe_i ^ e` becomes `argUnits[i] ^ e`. None when a contributing
/// argument is BARE (no unit signature) -- a bare value makes no claim, so
/// the equation is not judged (the transform's all-or-nothing rule).
let internal instantiateProbeSig (sigU: UnitSig) (argUnits: UnitSig option list) : UnitSig option =
    let n = unitNormalize sigU
    let prefix = "__unit_probe_"
    let probes =
        n.Dims |> Map.toList |> List.choose (fun (k, e) ->
            if k.StartsWith prefix then
                match System.Int32.TryParse (k.Substring prefix.Length) with
                | true, i -> Some (i, e)
                | _ -> None
            else None)
    let residual = { n with Nominal = None; Dims = n.Dims |> Map.filter (fun k _ -> not (k.StartsWith prefix)) }
    probes |> List.fold (fun acc (i, e) ->
        acc |> Option.bind (fun u ->
            List.tryItem i argUnits |> Option.flatten |> Option.map (fun au -> unitMul u (unitPow au e))))
        (Some residual)

/// The first recorded equality a call's argument units VIOLATE, instantiated
/// (both sides), or None.
let internal unitEqualityClash (eqs: (UnitSig * UnitSig) list) (argUnits: UnitSig option list) : (UnitSig * UnitSig) option =
    eqs |> List.tryPick (fun (a, b) ->
        match instantiateProbeSig a argUnits, instantiateProbeSig b argUnits with
        | Some x, Some y when not (unitCompatible x y) || not (unitSameScale x y) -> Some (x, y)
        | _ -> None)

/// A recorded transform, applied: `residual * PROD_i argUnits[i] ^ exponents[i]`.
/// Shared by the two consumers -- the call site (`unitStampedReturn`) and the
/// nested-call arm of `kernelBodyUnits`, which is how one generic calling
/// another composes -- so the rule cannot drift between them.
///
/// ALL OR NOTHING: a BARE argument in a position with a non-zero exponent
/// abandons the whole claim, returning None rather than reading the absence as
/// dimensionless. Reading it as dimensionless would be a real claim, and would
/// start rejecting the ordinary mixing of unit-free values that every
/// unannotated program does.
///
/// A PURE PASS-THROUGH keeps its argument's signature VERBATIM, nominal layer
/// included. Routing it through unitMul/unitPow yields the same dims and scale
/// but drops the Nominal -- multiplicative composition drops a quantity on
/// purpose (a quantity is an identity, not a factor) -- and a function that
/// neither multiplies nor divides has composed nothing. `mean` of a `Speed` row
/// is still a `Speed`.
let applyUnitTransform (exponents: int list) (residual: UnitSig)
                       (argUnits: UnitSig option list) : UnitSig option =
    let contributing =
        exponents |> List.mapi (fun i e -> (i, e)) |> List.filter (fun (_, e) -> e <> 0)
    let argAt i = List.tryItem i argUnits |> Option.flatten
    match contributing with
    | [ (i, 1) ] when (unitNormalize residual).Dims.IsEmpty
                      && unitSameScale residual unitDimensionless -> argAt i
    | _ ->
        contributing
        |> List.fold (fun acc (i, e) ->
            acc |> Option.bind (fun u ->
                argAt i |> Option.map (fun au -> unitMul u (unitPow au e))))
            (Some residual)

/// A callee's recorded transform, by the name the call site writes. A
/// module-qualified call (`stats.mean(x)`) finds its own callee under the
/// `alias.name` key the import registers (TypeModuleExport.Callees) -- NOT by
/// dropping the alias, which used to hand a callee with no recorded transform
/// the transform of whichever same-named function another module declared.
let lookupUnitTransform (env: TypeEnv) (fid: IRId) : (int list * UnitSig) option =
    match env.FuncUnitTransform.TryGetValue fid with
    | true, t -> Some t
    | _ -> None

/// Carry a generic call's DEDUCED return unit, derived from its ARGUMENTS.
///
/// A `T^1 -> T^0` signature shares ONE inference variable between the
/// parameter's ELEMENT and the return -- measured, both are the same `IRTInfer`
/// id: `lowerTypeExpr` mints `T^1` and `T^0` under separate typeVarScope keys,
/// and checking the BODY against the declared return is what ties them. Direct
/// application never binds that DECLARATION variable (the call judgment below
/// unifies an instantiated copy, with units stripped), so the caller's unit
/// never reaches it, and every unit rule (`unitRulesForOpWith`,
/// `unitRulesForUnaryOp`, ascription) read `IR.getUnits` off a result carrying
/// no signature.
/// Measured: over a `Float<meters>` row and a `Float<seconds>` scalar,
/// `mean(x) + t` was ACCEPTED, while the same clash on a direct element read
/// `x((0 : Idx<3>)) + t` correctly gave BL3006. The ascription and arithmetic
/// seams were never the problem; the result type simply arrived bare.
///
/// PROPAGATING THE ARGUMENT'S UNIT UNCHANGED IS THE WRONG FIX, and measurably
/// so: `mean` preserves its row's unit but `variance` SQUARES it, so a
/// pass-through stamp turns `let v: Float<area> = variance(x)` -- correct, and
/// documented as correct in `stdlib/stats.blade` -- into a BL3006. What
/// transfers is the EXPONENT the body derives, recorded per declaration in
/// `FuncUnitTransform` (see `funcUnitTransform`) and applied here.
///
/// Stamp the unit onto the RESULT NODE -- `IRTUnitAnnotated` over the still-open
/// variable, exactly the shape a written `T<u>^0` already lowers to
/// (lowerTypeExpr's `TyAbstractArray` arm) -- rather than BINDING the variable.
/// Binding is what must not happen: a `function` declaration's type is created
/// once with variables SHARED across every call site, so binding at the first
/// caller would both collapse the function to that caller's unit and defeat the
/// HM element polymorphism `requireArrayArgMinRank`'s polymorphic mark exists to
/// protect. A node stamp is per-call-site by construction, so two callers at
/// different units each get their own answer.
///
/// SILENT WHEN ANYTHING IS UNKNOWN. No recorded transform (the body used a
/// construct the unit walk does not model), a BARE argument in a position whose
/// exponent is non-zero, or an argument whose element type is still an
/// inference variable because the CALLER is generic too: each leaves the result
/// unstamped and the pre-existing behaviour intact. A bare argument
/// deliberately does NOT read as dimensionless -- claiming dimensionless would
/// start rejecting the ordinary mixing of unit-free values that every
/// unannotated program does.
///
/// Rank-preserving: a `-> T^1` return stamps the result array's ELEMENT, the
/// same place `stampElemUnits` writes for synthesized kernel pipelines. A return
/// that already carries a signature (a written `-> Float<meters>`, or `T<u>^0`)
/// is left alone, so an explicit annotation still wins.
/// The call judgment unifies an INSTANTIATED copy of the signature with units
/// stripped, so a call whose result the arguments determine gets a concrete
/// result type (`resultRet`) -- still carrying no unit, since units are judged
/// by their own rule. `unitStampedReturnOnto` therefore decides "deduced" on
/// the DECLARED return (`declRet`) and stamps the transform's unit onto the
/// result the judgment chose. `unitStampedReturn` is the one-type spelling.
let internal unitStampedReturnOnto (env: TypeEnv) (callee: IRId option)
                                  (tArgs: TypedExpr list) (declRet: IRType) (resultRet: IRType) : IRType =
    // The element UNIT of a type, at the depth the unit lives: the type itself
    // for a scalar position, its `ElemType` for an array.
    let elemUnits (t: IRType) : UnitSig option =
        match env.Subst.Resolve t with
        | ArrayElem at -> IR.getUnits (env.Subst.Resolve at.ElemType)
        | r -> IR.getUnits r
    let resolvedDecl = env.Subst.Resolve declRet
    let deduced =
        // Only a DEDUCED return is in scope: one whose element is still an open
        // variable, or an unannotated one the body made concrete (a recorded
        // transform exists only for those -- funcUnitTransform). A declared
        // concrete return either carries its own signature or legitimately has
        // none.
        match resolvedDecl with
        | ArrayElem at -> (env.Subst.Resolve at.ElemType).IsIRTInfer
        | IRTInfer _ -> true
        | _ -> (callee |> Option.bind (lookupUnitTransform env)).IsSome
    let resolvedRet = env.Subst.Resolve resultRet
    if not deduced || (elemUnits resolvedDecl).IsSome || (elemUnits resolvedRet).IsSome then resultRet
    else
        match callee |> Option.bind (lookupUnitTransform env) with
        | None -> resultRet
        | Some (exponents, residual) ->
            match applyUnitTransform exponents residual
                      (tArgs |> List.map (fun a -> elemUnits a.Type)) with
            | None -> resultRet
            | Some u ->
                match resolvedRet with
                | ArrayElem at ->
                    mkArrayLike { at with ElemType = IRTUnitAnnotated (env.Subst.Resolve at.ElemType, u) }
                | r -> IRTUnitAnnotated (r, u)

let internal unitStampedReturn (env: TypeEnv) (callee: IRId option)
                              (tArgs: TypedExpr list) (retTy: IRType) : IRType =
    unitStampedReturnOnto env callee tArgs retTy retTy

// ---------------------------------------------------------------------------
// THE CALL JUDGMENT (docs/plans/plan-call-judgment.md)
//
// One judgment for "these arguments meet these parameters", shared by the
// unqualified and the module-qualified application seams (the qualified
// spelling is rewritten onto the unqualified path, so there is ONE caller).
// Its core is ordinary HM application: INSTANTIATE the callee's signature
// (fresh copies of every open variable in it), then UNIFY the copy against
// the arguments. A `function` declaration's type is bound once
// (`bindVarSimple`, no scheme), so its variables are shared by every call
// site -- which is exactly why the copy, and never the declaration, is what
// a call binds: the declaration stays open for IR-phase monomorphization,
// and each call learns its own instance.
// ---------------------------------------------------------------------------

/// Replace inference variables per `mapping`, through EVERY wrapper -- unlike
/// `TypeEnv.instantiate`'s walk, units and index tags are descended too, since
/// a `T<u>^0` parameter is a unit-annotated variable.
let rec internal substInferVars (mapping: Map<int, IRType>) (ty: IRType) : IRType =
    let go = substInferVars mapping
    match ty with
    | IRTInfer id -> Map.tryFind id mapping |> Option.defaultValue ty
    | IRTTuple ts -> IRTTuple (ts |> List.map go)
    | IRTArrow (slots, ret, identity) ->
        let slot = function
            | SVal t -> SVal (go t)
            | s -> s
        IRTArrow (slots |> List.map slot, go ret, identity)
    | IRTComputation t -> IRTComputation (go t)
    | IRTPoly (t, v) -> IRTPoly (go t, v)
    | IRTDist (order, elem, axes) -> IRTDist (order, go elem, axes)
    | IRTLoop lt ->
        IRTLoop { lt with
                    ArrayTypes = lt.ArrayTypes |> List.map go
                    KernelType = lt.KernelType |> Option.map go }
    | IRTUnitAnnotated (inner, u) -> IRTUnitAnnotated (go inner, u)
    | IRTIdxTagged (inner, r) -> IRTIdxTagged (go inner, r)
    | _ -> ty

/// INSTANTIATE: copy the open variables of `tys` (resolved) that `quantified`
/// admits to fresh ones -- for a declared function, the ones its declaration
/// minted (TypeEnv.FuncSigVarRange), which is exactly what HM would
/// quantify; a variable shared with the environment is left alone.
/// The per-variable invariants that give a signature variable its meaning
/// travel with the copy -- the `T^k` exact-rank pin, a deduced rank lower
/// bound, a literal's value class -- so the copy refuses what the original
/// would. The polymorphic MARK does not travel: it tells zonk to keep a
/// DECLARATION's variable open for monomorphization, and a copy is never one.
/// Returns the copied types and the set of fresh ids.
let internal instantiateOpenVarsWithMap (subst: Subst) (quantified: int -> bool) (tys: IRType list)
                                        : IRType list * Set<int> * Map<int, int> =
    let resolved = tys |> List.map subst.Resolve
    let free =
        resolved |> List.map (freeInferVars subst) |> Set.unionMany |> Set.filter quantified
    if free.IsEmpty then (resolved, Set.empty, Map.empty)
    else
        let pairs =
            free |> Set.toList |> List.map (fun v ->
                match subst.Fresh() with
                | IRTInfer fid ->
                    subst.CopyArityConstraint(v, fid)
                    subst.CopyRankLowerBound(v, fid)
                    subst.CopyLiteralDefault(v, fid)
                    (v, fid)
                | _ -> failwith "unreachable: Subst.Fresh returns IRTInfer")
        let mapping = pairs |> List.map (fun (v, fid) -> (v, IRTInfer fid)) |> Map.ofList
        (resolved |> List.map (substInferVars mapping), pairs |> List.map snd |> Set.ofList, Map.ofList pairs)

let internal instantiateOpenVars (subst: Subst) (quantified: int -> bool) (tys: IRType list)
                                 : IRType list * Set<int> =
    let (copied, ids, _) = instantiateOpenVarsWithMap subst quantified tys
    (copied, ids)

/// Which open variables a call through `tFunc` may instantiate, and whether
/// the callee's declaration is FINISHED. Only a declared function quantifies
/// anything (the ids its declaration minted, TypeEnv.FuncSigVarRange); a
/// lambda, a function-typed parameter or a curried head quantifies nothing.
/// `closed` is false for a recursive call inside the declaration's own body,
/// whose signature is still being inferred.
///
/// Only POLYMORPHIC-marked variables are quantified: they are the ones zonk
/// keeps open and IR-phase monomorphization specializes per call site. An
/// unannotated parameter's plain variable is not generic at all -- zonk
/// defaults it (Float64) and ONE body is emitted -- so a per-call instance of
/// it would type the call (`Int64`) differently from the callable it invokes
/// (`double twice(double)`, static/011). Those stay uninstantiated and
/// unbound, exactly as before.
///
/// The key a callable's DEFAULTS and where-conjuncts are recorded under
/// (TypeEnv.FuncDefaults / FuncDefaultCaptures / FuncConstraints): a declared
/// function's binder id; for a `let`-bound LAMBDA, the id of its first
/// parameter -- program-unique, and known where the defaults are recorded
/// (the lambda is typed before its binding's id is minted, on four different
/// let paths). Either way it is reached from the binding a call head
/// RESOLVES to, so a local `f` shadowing a defaults-carrying `f` is never
/// filled from the outer one's defaults (they used to be keyed by name).
let internal defaultsKeyOfVar (vi: VarInfo) : IRId =
    match vi.TypedValue with
    | Some { Kind = TExprLambda li } when not li.Params.IsEmpty -> li.Params.Head.VarId
    | _ -> vi.VarId

let internal defaultsKeyOfName (env: TypeEnv) (name: string) : IRId option =
    lookupVar name env |> Option.map defaultsKeyOfVar

/// The DECLARATION binder a call head names: the head's own id, or -- for a
/// `let` ALIAS of a declared function (`let g = total`), which shares the
/// declaration's type, variables included -- the id the alias chain ends at.
let internal calleeDeclId (env: TypeEnv) (tFunc: TypedExpr) : IRId option =
    let rec declId (fuel: int) (name: string) (vid: IRId) : IRId =
        if env.FuncSigVarRange.ContainsKey vid || fuel <= 0 then vid
        else
            match lookupVar name env with
            | Some info ->
                (match info.TypedValue with
                 | Some { Kind = TExprVar (n2, vid2, _) } when vid2 <> vid -> declId (fuel - 1) n2 vid2
                 | _ -> vid)
            | None -> vid
    match tFunc.Kind with
    | TExprVar (name, vid0, _) -> Some (declId 8 name vid0)
    | _ -> None

/// `calleeDeclId` for a SURFACE callee name (the partial-application seam
/// sees the call head before it is typed).
let internal declIdOfName (env: TypeEnv) (name: string) : IRId option =
    match lookupVar name env with
    | Some vi -> calleeDeclId env (mkTyped (TExprVar (name, vi.VarId, None)) vi.Type)
    | None -> None

let internal calleeQuantifier (env: TypeEnv) (tFunc: TypedExpr) : (int -> bool) * bool =
    // A `let` ALIAS of a declared function quantifies the declaration's
    // variables (calleeDeclId) -- or calls through the alias would escape
    // the judgment.
    match calleeDeclId env tFunc with
    | Some vid ->
        (match env.FuncSigVarRange.TryGetValue vid with
         | true, (lo, hi) ->
             (fun v -> v >= lo && v < hi && env.Subst.IsPolymorphicId v), hi <> System.Int32.MaxValue
         | _ -> (fun _ -> false), false)
    | _ -> (fun _ -> false), false

/// The numeric CAST legality rule (formalism 2.4), over a concrete source
/// element: None = legal, Some message = the BL3019 refusal. The shared rule
/// behind the generic-cast judgments (a cast whose operand is a declaration's
/// signature variable is judged per instance, not at the declaration).
let internal castLegality (name: string) (target: ElemType) (src: ElemType) (rounded: bool) : string option =
    let isInt = function ETInt32 | ETInt64 -> true | _ -> false
    let isFloat = function ETFloat32 | ETFloat64 -> true | _ -> false
    let isComplex = function ETComplex64 | ETComplex128 -> true | _ -> false
    match src with
    | ETBool | ETString | ETUnit ->
        Some $"{name}() expects a numeric operand; got {ppIRType (IRTScalar src)}."
    | _ when isComplex src && not (isComplex target) ->
        Some ($"{name}() cannot cast a complex value: project a real component first -- "
              + "real(z), imag(z), abs(z), or arg(z).")
    | _ when isFloat src && isInt target && not rounded ->
        Some ($"{name}() would truncate a float: spell the rounding at the cast site -- "
              + $"{name}(floor(x)) or {name}(ceil(x)).")
    | _ -> None

/// The GENERIC OBLIGATIONS of a callee (TypeEnv.GenericObligation), judged
/// against one instance: `instanceOf v` is the concrete element the call
/// gives declaration variable `v` (None = still open: not judged here).
let internal judgeGenericObligations (env: TypeEnv) (fname: string) (obligations: GenericObligation list)
                                     (instanceOf: int -> ElemType option) : TypeError option =
    let rootOf (vid: int) =
        match env.Subst.Resolve (IRTInfer vid) |> IR.stripUnits with
        | IRTInfer r -> Some r
        | _ -> None
    let isInt = function ETInt32 | ETInt64 -> true | _ -> false
    obligations |> List.tryPick (fun ob ->
        let vid = match ob with GOCast (v, _, _, _) | GOFractionalMath (v, _) | GOPromotion (v, _, _, _) -> v
        match rootOf vid |> Option.bind instanceOf with
        | None -> None
        | Some src ->
            match ob with
            | GOCast (_, name, target, rounded) ->
                castLegality name target src rounded
                |> Option.map (fun msg ->
                    InvalidCast ($"'{fname}' casts a value of its generic parameter type, and this call makes "
                                 + $"that type {ppIRType (IRTScalar src)}: " + msg))
            | GOFractionalMath (_, name) when isInt src ->
                Some (InvalidCast ($"'{fname}' applies {name}() to a value of its generic parameter type and "
                                   + $"returns it as that type, and this call makes that type {ppIRType (IRTScalar src)}: "
                                   + $"{name} of an integer is a Float64, which an {ppIRType (IRTScalar src)} result "
                                   + "would truncate. Convert the argument first -- Float64(x), or Float64(xs) "
                                   + "for an array."))
            | GOFractionalMath _ -> None
            | GOPromotion (_, opName, partner, literal) ->
                // A float LITERAL adapts to a narrow partner (inferBinOp's
                // adaptFloatLit): `x32 + 1.0` stays Float32, `z64 * 2.0`
                // stays Complex64. A typed partner promotes as written.
                let partner' =
                    if literal && (src = ETFloat32 || src = ETComplex64) then ETFloat32 else partner
                match IR.promoteElemType src partner' with
                | Some r when r = src -> None
                | r ->
                    let srcS = ppIRType (IRTScalar src)
                    let what =
                        match r with
                        | Some r -> $"{srcS} {opName} {ppIRType (IRTScalar partner')} is {ppIRType (IRTScalar r)}, which the {srcS}-typed result would truncate"
                        | None -> $"{opName} is not defined between {srcS} and {ppIRType (IRTScalar partner')}"
                    Some (InvalidCast ($"'{fname}' applies {opName} between a value of its generic parameter type and a "
                                       + $"{ppIRType (IRTScalar partner)}, typing the result as the parameter's type, and this "
                                       + $"call makes that type {srcS}: {what}. Convert the argument first -- Float64(x), or "
                                       + "Float64(xs) for an array.")))

/// The element a (resolved) type carries, when it is concrete -- an array's
/// own element included: a generic obligation on `x: T` judges the ELEMENT an
/// array instance gives `T` (`sqrt(xs)` over Int64 elements truncates just as
/// `sqrt(n)` does).
let internal concreteElemOf (subst: Subst) (t: IRType) : ElemType option =
    let rec go (t: IRType) =
        match IR.stripUnits (subst.Resolve t) with
        | IRTScalar et -> Some et
        | IRTIdxTagged (inner, _) ->
            (match IR.stripUnits (subst.Resolve inner) with IRTScalar et -> Some et | _ -> None)
        | ArrayElem arr -> go arr.ElemType
        | _ -> None
    go t

/// GENERIC OBLIGATIONS at a call, judged against the call judgment's
/// instantiation: `mapping` is its declaration-variable -> copy map, and a
/// copy the arguments made concrete is judged. A copy still open (an
/// argument open at the call, an eta-wrapped kernel) is left to the
/// post-zonk sweep (TypeCheckValidate.collectGenericObligationErrors) and,
/// for casts, the post-monomorphization one (IRValidate).
let internal genericObligationClash (env: TypeEnv) (tFunc: TypedExpr) (mapping: Map<int, int>) : TypeError option =
    match calleeDeclId env tFunc with
    | None -> None
    | Some fid ->
        match env.FuncGenericObligations.TryGetValue fid with
        | true, obs ->
            let fname = match tFunc.Kind with TExprVar (n, _, _) -> n | _ -> "the callee"
            judgeGenericObligations env fname obs (fun r ->
                Map.tryFind r mapping |> Option.bind (fun copy -> concreteElemOf env.Subst (IRTInfer copy)))
        | _ -> None

/// A declared generic function named as a VALUE -- an argument to a
/// higher-order parameter (`apply(toint, 2.5)`), a composition operand (`sq
/// >> toint`) -- is used at the type its position fixes, and IR
/// monomorphization specializes it there (IRMono.hmValueRefRewrite). Its
/// GENERIC OBLIGATIONS are judged against that use, as at a call: a cast of
/// the signature variable that the use makes a float-to-integer truncation
/// (or a complex projection) is BL3019 here, at the reference, rather than the
/// IR validator's unlocated backstop after monomorphization. Side-effect free:
/// the instance is read off the declaration's type against `useTy` one-sidedly
/// (the declaration's variables are never bound by a use).
let internal genericValueObligationClash (env: TypeEnv) (fnRef: TypedExpr) (useTy: IRType) : TypeError option =
    match fnRef.Kind, calleeDeclId env fnRef with
    | TExprVar (fname, _, _), Some fid ->
        (match env.FuncGenericObligations.TryGetValue fid with
         | true, obs when not obs.IsEmpty ->
             let rec learn (p: IRType) (a: IRType) (m: Map<int, IRType>) =
                 match IR.stripUnits (env.Subst.Resolve p), IR.stripUnits (env.Subst.Resolve a) with
                 | IRTInfer r, at -> if m.ContainsKey r then m else Map.add r at m
                 | ArrayElem pa, ArrayElem aa -> learn pa.ElemType aa.ElemType m
                 | IRTTuple pts, IRTTuple ats when pts.Length = ats.Length ->
                     List.fold2 (fun m pt at -> learn pt at m) m pts ats
                 | FuncElem (pps, pr), FuncElem (aps, ar) when pps.Length = aps.Length ->
                     learn pr ar (List.fold2 (fun m pt at -> learn pt at m) m pps aps)
                 | _ -> m
             let inst = learn fnRef.Type useTy Map.empty
             judgeGenericObligations env fname obs (fun r ->
                 Map.tryFind r inst |> Option.bind (concreteElemOf env.Subst))
         | _ -> None)
    | _ -> None

/// A generic body calling a generic callee with its OWN signature variables:
/// the callee's obligations are then the caller's too (`function r2(x: T^0)
/// -> T^0 = r(x)` over `r(x: T^0) -> T^0 = sqrt(x)`), since whatever instance
/// reaches the caller reaches the callee. The call judgment cannot decide
/// them here (the instance is open), so they are re-recorded against the
/// caller's variable -- read off the declared parameters against the
/// arguments, as monomorphization will -- and judged at the caller's calls.
let internal propagateGenericObligations (env: TypeEnv) (tFunc: TypedExpr)
                                         (paramTys: IRType list) (tArgs: TypedExpr list) : unit =
    match env.CurrentGenericObligations, calleeDeclId env tFunc with
    | Some acc, Some fid ->
        (match env.FuncGenericObligations.TryGetValue fid with
         | true, obs ->
             let rec learn (p: IRType) (a: IRType) (m: Map<int, IRType>) =
                 match IR.stripUnits (env.Subst.Resolve p), IR.stripUnits (env.Subst.Resolve a) with
                 | IRTInfer r, at -> if m.ContainsKey r then m else Map.add r at m
                 | ArrayElem pa, ArrayElem aa -> learn pa.ElemType aa.ElemType m
                 | IRTTuple pts, IRTTuple ats when pts.Length = ats.Length ->
                     List.fold2 (fun m pt at -> learn pt at m) m pts ats
                 | _ -> m
             let n = min paramTys.Length tArgs.Length
             let inst =
                 List.fold2 (fun m p (a: TypedExpr) -> learn p a.Type m) Map.empty
                     (List.truncate n paramTys) (List.truncate n tArgs)
             let callerVar (v: int) =
                 match env.Subst.Resolve (IRTInfer v) |> IR.stripUnits with
                 | IRTInfer r ->
                     (match Map.tryFind r inst |> Option.map IR.stripUnits with
                      | Some (IRTInfer c) when env.Subst.IsPolymorphicId c && c <> r -> Some c
                      | _ -> None)
                 | _ -> None
             for ob in obs do
                 match ob with
                 | GOCast (v, h, t, rd) ->
                     callerVar v |> Option.iter (fun c -> acc.Add(GOCast (c, h, t, rd)))
                 | GOFractionalMath (v, nm) ->
                     callerVar v |> Option.iter (fun c -> acc.Add(GOFractionalMath (c, nm)))
                 | GOPromotion (v, o, p, lit) ->
                     callerVar v |> Option.iter (fun c -> acc.Add(GOPromotion (c, o, p, lit)))
         | _ -> ())
    | _ -> ()

/// Strip unit annotations at every depth (array elements, tuple components,
/// function slots). The call judgment compares SHAPES with units removed --
/// units are judged by their own rule (`unitClash`), and letting unify's
/// unit arm see them would bind a generic `T` to `Float<m>`, giving
/// `variance` an `m` result where its recorded transform says `m^2`.
let rec internal stripUnitsDeep (subst: Subst) (ty: IRType) : IRType =
    let go = stripUnitsDeep subst
    match subst.Resolve ty with
    | IRTUnitAnnotated (inner, _) -> go inner
    | IRTTuple ts -> IRTTuple (ts |> List.map go)
    | IRTArrow (slots, ret, identity) ->
        let slot = function
            | SVal t -> SVal (go t)
            | s -> s
        IRTArrow (slots |> List.map slot, go ret, identity)
    | IRTIdxTagged (inner, r) -> IRTIdxTagged (go inner, r)
    | IRTComputation t -> IRTComputation (go t)
    | IRTDist (order, elem, axes) -> IRTDist (order, go elem, axes)
    | t -> t

/// A numeric LITERAL argument (optionally negated). A literal synthesizes its
/// natural type (`3` is Int64) but, like at every other checking seam, adapts
/// to the scalar it is written against -- `f(3)` into an `Int32` or a
/// `Float32` parameter is the literal meaning that type, not a narrowing.
let internal numericLiteralKind (a: TypedExpr) : ElemType option =
    let rec go (e: TypedExpr) =
        match e.Kind with
        | TExprLit (LitInt _) -> Some ETInt64
        | TExprLit (LitFloat _) -> Some ETFloat64
        | TExprUnaryOp (_, inner) -> go inner
        | _ -> None
    go a

/// The LITERAL SHAPE of an argument: a numeric literal leaf, a tuple of
/// shapes (a tuple literal's components adapt one by one: `h((1.5, 2.0))` into
/// `(Float32, Float32)` is two literals meaning Float32, not a narrowing of a
/// `(Float64, Float64)` value), or nothing.
type internal ArgLit =
    | ALNone
    | ALLeaf of ElemType
    | ALTuple of ArgLit list

let rec internal argLiteralShape (a: TypedExpr) : ArgLit =
    match a.Kind with
    | TExprTuple es -> ALTuple (es |> List.map argLiteralShape)
    | _ ->
        match numericLiteralKind a with
        | Some et -> ALLeaf et
        | None -> ALNone

/// Coarse head kind of a RESOLVED type, for the judgment's fallback: two
/// different known kinds never agree; `None` (an open, deferred or otherwise
/// unmodelled head) stands down.
let internal headKindOf (t: IRType) : string option =
    match t with
    | IRTScalar _ -> Some "scalar"
    | ArrayElem _ -> Some "array"
    | FuncElem _ -> Some "function"
    | IRTTuple _ -> Some "tuple"
    | IRTNamed _ -> Some "named"
    | IRTDist _ -> Some "dist"
    | _ -> None

/// THE PER-ARGUMENT JUDGMENT: does an argument of type `a` meet a parameter
/// of (instantiated) type `p`? `None` = agrees; `Some e` = refused, with the
/// most specific error unify produced (the caller wraps it).
///
/// Unification with exactly the coercions the emitted C++ call performs and
/// the language licenses at a call:
///   * UNITS are transparent (stripped; `unitClash` owns them);
///   * a concrete SCALAR argument WIDENS into a concrete scalar parameter it
///     promotes to (`Int64` -> `Float64`; never narrowing, and never inside
///     an array or a function type, where no element conversion exists) --
///     and a numeric literal adapts to any numeric parameter of its kind;
///   * an UNTAGGED integer meets a `Nat<I>` parameter (the cast / bounds
///     story belongs to the index seam, not here); a DIFFERENT tag refuses --
///     `Nat<Lon>` into `Nat<Lat>` is the nominal rule kernel application
///     already enforces;
///   * the virtual/stored character of an array is not compared (a range
///     passed to an array parameter is materialized by the call).
///
/// ONLY THE COPIES BIND. `copies` is the set of instantiated variables; the
/// judgment binds nothing else -- not the declaration (never instantiated
/// into this call), not a variable the callee shares with its environment
/// (a lambda's or a function-typed parameter's), and not the CALLER's: an
/// argument that is still OPEN at a position is not judged there (binding a
/// caller's variable from a callee's signature is inference this seam has
/// never done; the post-zonk sweep judges it once it is closed), and a
/// fallback unify is attempted only on a closed argument.
let rec internal argPairClash (subst: Subst) (copies: Set<int>) (widen: bool) (litKind: ArgLit)
                              (p: IRType) (a: IRType) : TypeError option =
    // A copy an EARLIER argument already taught: this argument must meet
    // what the first one taught, by the same judgment -- tags, element types
    // (no widening inside an array) and, via the extent pass over the
    // resolved copies in the caller, static extents. The monomorph is built
    // from the FIRST teaching, so a later argument that disagrees on any of
    // them is handed to a specialization of the wrong shape: `second(B5, A3)`
    // over `(a: T^1, b: T^1) -> T^1 = b` typed a 3-cell value Idx<5>, and
    // `second0((1 : Lat), (4 : Lon))` made a Lat index of 4.
    // (`firstAbstractVarConflict` still runs first and keeps its wording for
    // the rank / narrowing conflicts it models.)
    match p with
    | IRTInfer pid when copies.Contains pid && (subst.TryFind pid).IsSome ->
        argPairClash subst copies widen litKind (subst.Resolve p) a
    | _ ->
    // Resolve the parameter's HEAD only, so its children still show which
    // positions are copies (the check above, one level down).
    let rec headResolve (t: IRType) =
        match t with
        | IRTInfer id ->
            (match subst.TryFind id with
             | Some t' -> headResolve t'
             | None -> t)
        | _ -> t
    let p = headResolve p
    let a = subst.Resolve a
    let recur = argPairClash subst copies
    let mismatch () = Some (TypeMismatch (p, a))
    let viaUnify (x: IRType) (y: IRType) =
        match unify subst x y with
        | Ok () -> None
        | Error e -> Some e
    match p, a with
    | IRTUnitAnnotated (pi, _), _ -> recur widen litKind pi a
    | _, IRTUnitAnnotated (ai, _) -> recur widen litKind p ai
    // Deferred / variadic forms have their own seams (a loop object reaching
    // an array parameter is materialized at the call; a pack is
    // monomorphization's).
    | _, (IRTLoop _ | IRTPoly _) | IRTPoly _, _ -> None
    | _, IRTComputation ai -> recur widen litKind p ai
    | IRTComputation pi, _ -> recur widen litKind pi a
    | _, IRTInfer _ -> None
    // An instantiated signature variable binds to the argument's shape --
    // which is what makes two positions sharing one variable agree. Any other
    // open parameter variable is not this call's to bind.
    | IRTInfer pid, _ when copies.Contains pid ->
        match subst.GetArityConstraint pid, a with
        // A caret claims RANK; unify's pin counts index SLOTS. A compact
        // group (`SymIdx<2, n>`) is one slot of rank 2, so a packed array
        // meets a `T^2` parameter by rank -- what the direct seam always
        // accepted (`concreteRankOf`). Accepted without binding.
        | Some k, ArrayElem aa when aa.IndexTypes.Length <> k && concreteRankOf subst a = Some k -> None
        | _ -> viaUnify p (stripUnitsDeep subst a)
    | IRTInfer _, _ -> None
    | IRTIdxTagged (pi, IRefAny), _ -> recur widen litKind pi (stripTagAnnotation a)
    | IRTIdxTagged (pi, r1), IRTIdxTagged (ai, r2) ->
        let same =
            match r1, r2 with
            | IRefNamed n1, IRefNamed n2 -> n1 = n2
            | IRefAnon (i1, _), IRefAnon (i2, _) -> i1 = i2
            | _ -> false
        if same then recur widen litKind pi ai else mismatch ()
    | IRTIdxTagged (pi, _), _ -> recur widen litKind pi a
    | _, IRTIdxTagged (ai, _) -> recur widen litKind p ai
    | IRTScalar pe, IRTScalar ae ->
        let isInt e = (e = ETInt32 || e = ETInt64)
        if pe = ae then None
        elif widen && promoteElemType ae pe = Some pe then None
        // Integer to integer in either width: the emitted call converts
        // implicitly (no -Werror covers it), and a recurrence index (Int64)
        // handed to an `i: Int` (Int32) parameter is an established idiom.
        // What stays refused is the float -> integer narrowing g++ rejects.
        elif widen && isInt pe && isInt ae then None
        else
            match litKind, pe with
            | ALLeaf ETInt64, (ETInt32 | ETInt64 | ETFloat32 | ETFloat64 | ETComplex64 | ETComplex128) when widen -> None
            | ALLeaf ETFloat64, (ETFloat32 | ETFloat64 | ETComplex64 | ETComplex128) when widen -> None
            | _ -> mismatch ()
    | ArrayElem pa, ArrayElem aa ->
        if pa.IndexTypes.Length <> aa.IndexTypes.Length then mismatch ()
        else
            let bad =
                List.zip pa.IndexTypes aa.IndexTypes
                |> List.indexed
                |> List.tryFind (fun (_, (pi, ai)) -> indexPairIncompatible pi ai)
            match bad with
            // Tree identity has its own, more precise refusal (BL4003, the
            // post-zonk `collectAppTreeErrors` sweep names both tree types
            // and why equal leaf counts do not make them one space).
            | Some (_, (pi, ai)) when (match pi.Tag, ai.Tag with
                                      | Some (TreeTag _), _ | _, Some (TreeTag _) -> true
                                      | _ -> false) -> None
            | Some (slot, (pi, ai)) when indexRankDiffers pi ai ->
                Some (IndexRankMismatch ($"index slot {slot}",
                                         ppIndexType pi, max 1 pi.Rank,
                                         ppIndexType ai, max 1 ai.Rank))
            | Some _ -> mismatch ()
            | None -> recur false ALNone pa.ElemType aa.ElemType
    | IRTTuple ps, IRTTuple args ->
        if ps.Length <> args.Length then mismatch ()
        else
            let comps =
                match litKind with
                | ALTuple ls when ls.Length = ps.Length -> ls
                | _ -> List.replicate ps.Length ALNone
            List.zip3 ps args comps |> List.tryPick (fun (x, y, l) -> recur widen l x y)
    | FuncElem (pps, pr), FuncElem (aps, ar) ->
        if pps.Length <> aps.Length then mismatch ()
        else
            (List.zip pps aps |> List.tryPick (fun (x, y) -> recur false ALNone x y))
            |> Option.orElseWith (fun () -> recur false ALNone pr ar)
    | IRTNamed n1, IRTNamed n2 -> if n1 = n2 then None else mismatch ()
    | _ ->
        match headKindOf p, headKindOf a with
        | Some k1, Some k2 when k1 <> k2 -> mismatch ()
        // Two Dists: order and axes have their own seams (the ppl formers);
        // the call keeps the class-only judgment it always had.
        | Some "dist", Some "dist" -> None
        | Some _, Some _ when Set.isEmpty (freeInferVars subst a) -> viaUnify p a
        | _ -> None

/// SHARED STATIC-EXTENT REFINEMENT. Extents are deliberately NOT part of type
/// identity (unify never compares them -- extents are shape-monomorphized at
/// codegen instead), so agreement is a refinement checked AFTER a successful
/// unify, and it is checked by this one predicate at every seam where a value
/// meets a declared shape: call arguments (BL3016 `ExtentArgMismatch`) and
/// ascriptions -- `let`, annotated returns, match arms and block finals --
/// plus `if`/`match` branches against each other (BL3016
/// `ExtentAscribeMismatch`).
///
/// Codegen treats a LITERAL extent as ground truth: it bakes it into
/// subscripts, loop bounds and allocations (`copy_n` of the declared count),
/// so a disagreement is a memory error, not a naming quarrel. Literal vs
/// literal only (through `tryEvalIntIR`), equal slot counts only (a rank clash
/// is unify's); a symbolic, ragged or runtime extent reads `.extents[d]` and
/// stands down. Returns (0-based slot, expected extent, actual extent).
let internal staticExtentClash (subst: Subst) (expected: IRType) (actual: IRType)
                               : (int * int64 * int64) option =
    match subst.Resolve expected, subst.Resolve actual with
    | ArrayElem ea, ArrayElem aa when ea.IndexTypes.Length = aa.IndexTypes.Length ->
        List.zip ea.IndexTypes aa.IndexTypes
        |> List.indexed
        |> List.tryPick (fun (d, (ei, ai)) ->
            match tryEvalIntIR ei.Extent, tryEvalIntIR ai.Extent with
            | Some e, Some a when e <> a -> Some (d, e, a)
            | _ -> None)
    | _ -> None

/// APPLYING A VALUE THAT TAKES NO ARGUMENTS: the head's RESOLVED type, when it
/// is a scalar, a plain Nat, or an index value under a USER-named tag, and the
/// head is a user-written, spanned variable. Such a head was routed to the
/// dispatcher's fresh-var catch-all (it may be an open var that later becomes
/// an array or a function), and codegen emitted `x(0L)` on a `double` -- a g++
/// error. Narrow on purpose: an index value under a COMPILER tag is callable by
/// design (a halo window `w(1)`, `Nat<__halowin|..>`), and a synthesized head's
/// stamp is its desugarer's contract. Judged where the head is concrete: here
/// at the call, and after a kernel's parameters meet their operands
/// (buildApplyInfo) -- never post-zonk, where a zonk-DEFAULTED Float64 (an
/// unreadable provider store's placeholder) would be a false refusal.
let internal nonCallableHead (subst: Subst) (f: TypedExpr) : IRType option =
    let userHead =
        f.Span.StartLine > 0
        && (match f.Kind with
            | TExprVar (nm, _, _) -> not (nm.StartsWith "__")
            | _ -> false)
    if not userHead then None
    else
        let t = IR.stripUnits (subst.Resolve f.Type)
        match t with
        | IRTScalar _ | IRTNat _ -> Some t
        | IRTIdxTagged (_, IRefNamed tag) when not (tag.StartsWith "__") -> Some t
        | _ -> None

/// What discharging a body's co-iteration obligations found wrong. Positions
/// are DECLARED parameter positions, 0-based.
type internal CoIterVerdict =
    /// Two parameters' literal extents disagree on the shared axis.
    | CoIterExtentArgs of posA: int * posB: int * extA: int64 * extB: int64
    /// A parameter's literal extent disagrees with an array the body fixes.
    | CoIterExtentBody of pos: int * ext: int64 * bodyExt: int64
    /// Two parameters are handed fibers over different NAMED index types.
    | CoIterNameArgs of posA: int * posB: int * headA: IRIndexType * headB: IRIndexType
    /// A parameter's fiber is named differently from an array the body fixes.
    | CoIterNameBody of pos: int * head: IRIndexType * bodyHead: IRIndexType

/// Discharge `FuncCoIterObligations` entries against the types the
/// parameters are actually handed -- a call's arguments, or (buildApplyInfo)
/// the fibers a kernel application binds. ONE judgment for both seams, so a
/// body over `T^1` parameters refuses `cov(R(0), H(0))` and
/// `method_for(R, H) <@> cov` for the same reason.
///
/// The two halves mirror `TypeLower.zipHeadClash`, the check the same zip
/// gets when its operands are concrete where it is written, in its order:
/// a literal-vs-literal EXTENT disagreement first (BL3016: the walk is out of
/// bounds), then NOMINAL identity on the leading record
/// (`indexNamesCoIterable`, BL3999: in bounds, but two different index
/// spaces walked as one). Every obligation's extent half is judged before any
/// name half, so the memory error is the one reported when both exist. A
/// position whose type is not (yet) an array is not judged -- the status quo,
/// never an invented refusal.
let internal coIterVerdict (env: TypeEnv) (obs: CoIterObligation list)
                           (typeAt: int -> IRType option) : CoIterVerdict option =
    let headAt (i: int) =
        typeAt i |> Option.bind (fun t ->
            match env.Subst.Resolve t with
            | ArrayElem aa -> List.tryHead aa.IndexTypes
            | _ -> None)
    let extentVerdict (ob: CoIterObligation) =
        let known =
            ob.Positions |> List.choose (fun i ->
                headAt i |> Option.bind (fun h -> tryEvalIntIR h.Extent) |> Option.map (fun e -> (i, e)))
        match known with
        | [] -> None
        | (i0, e0) :: rest ->
            // Argument vs ARGUMENT first: both sides are call-site positions,
            // the more actionable report.
            match rest |> List.tryFind (fun (_, e) -> e <> e0) with
            | Some (j, ej) -> Some (CoIterExtentArgs (i0, j, e0, ej))
            | None ->
                ob.BodyExtents |> List.tryFind (fun l -> l <> e0)
                |> Option.map (fun l -> CoIterExtentBody (i0, e0, l))
    let nameVerdict (ob: CoIterObligation) =
        let heads = ob.Positions |> List.choose (fun i -> headAt i |> Option.map (fun h -> (i, h)))
        // EVERY pair, not each against the first: an unnamed first argument
        // co-iterates with either name, which says nothing about the other two.
        let pairs =
            heads |> List.mapi (fun k (i, hi) ->
                heads |> List.skip (k + 1) |> List.map (fun (j, hj) -> (i, hi, j, hj)))
            |> List.concat
        match pairs |> List.tryFind (fun (_, hi, _, hj) -> not (indexNamesCoIterable hi hj)) with
        | Some (i, hi, j, hj) -> Some (CoIterNameArgs (i, j, hi, hj))
        | None ->
            heads |> List.tryPick (fun (i, h) ->
                ob.BodyHeads
                |> List.tryFind (fun b -> not (indexNamesCoIterable h b))
                |> Option.map (fun b -> CoIterNameBody (i, h, b)))
    match obs |> List.tryPick extentVerdict with
    | Some v -> Some v
    | None -> obs |> List.tryPick nameVerdict

/// The display name of a leading record in a co-iteration refusal (the same
/// decoding `zipHeadClash` prints with).
let internal coIterHeadName (h: IRIndexType) : string =
    displayTagName (defaultArg h.Tag "<unnamed>")

// ---------------------------------------------------------------------------
// ARROW APPLICATIONS -- applying a still-open variable (TypeEnv.ArrowApplication)
// ---------------------------------------------------------------------------

/// Is the variable `vid` one of the parameters of the declaration whose body is
/// being checked (TypeEnv.CurrentSignature)?
let internal isCurrentDeclParam (env: TypeEnv) (vid: int) : bool =
    match List.rev env.CurrentSignature with
    | _ret :: ps ->
        ps |> List.exists (fun p ->
            match env.Subst.Resolve p with
            | IRTInfer pid -> pid = vid
            | _ -> false)
    | [] -> false

/// Could this argument be an INDEX -- an integer, an index value, `_`, or a
/// still-open variable (a parameter used only as a subscript is an index,
/// pinSubscriptParams)? An application whose arguments all could is an ARROW
/// application: the head may be an array or a function. Any other argument
/// (a Float, a tuple, an array, a function) can only be a function's.
let internal arrowArgIndexLike (env: TypeEnv) (a: TypedExpr) : bool =
    let isIntElem (et: ElemType) =
        match et with
        | ETInt32 | ETInt64 -> true
        | _ -> false
    match a.Kind with
    | TExprWildcard -> true
    | _ ->
        match IR.stripUnits (env.Subst.Resolve a.Type) with
        | IRTScalar et -> isIntElem et
        | IRTIdxTagged _ | IRTNat _ -> true
        | IRTInfer aid ->
            (match env.Subst.GetLiteralDefault aid with
             | Some et -> isIntElem et
             | None -> true)
        | _ -> false

/// Is this (argument) type INT-VALUED -- an integer, an index value, or an
/// integer literal still open? Such an argument is a subscript in an array
/// reading, and in a function reading needs a slot that takes an integer.
let internal arrowIntValued (subst: Subst) (t: IRType) : bool =
    let isInt (t: IRType) =
        match IR.stripUnits t with
        | IRTScalar (ETInt32 | ETInt64) -> true
        | _ -> false
    match IR.stripUnits (subst.Resolve t) with
    | IRTScalar (ETInt32 | ETInt64) -> true
    | IRTIdxTagged (inner, _) -> isInt inner
    | IRTNat _ -> true
    | IRTInfer aid ->
        (match subst.GetLiteralDefault aid with
         | Some (ETInt32 | ETInt64) -> true
         | _ -> false)
    | _ -> false

/// The FUNCTION reading of an arrow application: "`c` can be either, as long as
/// its argument is int-valued". Each int-valued argument needs a slot that takes
/// an integer: an OPEN slot (an unannotated lambda parameter) is made one --
/// the argument's own integer type, Int64 for a literal -- and a slot of any
/// other concrete type is refused: a function of a Float64 is not an arrow over
/// an index, and the call would silently convert. Returns the offending
/// argument position and the slot's type.
let internal arrowFuncSlotClash (subst: Subst) (slots: IRType list) (argTys: IRType list) : (int * IRType) option =
    List.zip slots argTys
    |> List.indexed
    |> List.tryPick (fun (j, (slot, argTy)) ->
        if not (arrowIntValued subst argTy) then None
        else
            match IR.stripUnits (subst.Resolve slot) with
            | IRTInfer _ ->
                let target =
                    match IR.stripUnits (subst.Resolve argTy) with
                    | IRTInfer _ -> IRTScalar ETInt64
                    | t -> t
                (match unify subst slot target with
                 | Ok () -> None
                 | Error _ -> Some (j, subst.Resolve slot))
            | t when arrowIntValued subst t -> None
            | t -> Some (j, t))

/// The wording of what an arrow parameter must be, for a refusal at the call.
let internal arrowExpectation (n: int) : string =
    let args = if n = 1 then "an integer index" else $"{n} integer indices"
    $"an array of rank {n} or a function of {args} (the body applies it to {args})"

/// Structural child enumerator for a typed expression: the immediate
/// sub-expressions of a node, total over TExpr kinds. Shared by the
/// tag-check revalidation walk and the wildcard-escape scan so the two
/// never drift.
// Public: Ide.fs walks the zonked typed tree with this to collect builtin
// call-site instantiations (calls[] in `ide check --json`).
let typedExprChildren (expr: TypedExpr) : TypedExpr list =
        match expr.Kind with
        | TExprLit _ | TExprVar _ | TExprQualified _ | TExprSection _
        | TExprWildcard
        | TExprZero | TExprRange _ | TExprReverse _ | TExprArity _ -> []
        | TExprUnaryOp (_, e) -> [e]
        | TExprBinOp (_, _, l, r) -> [l; r]
        | TExprApp (f, args) -> f :: args
        | TExprTupleIndex (t, i) -> [t; i]
        | TExprPolyTail (p, _) -> [p]
        | TExprField (e, _, _) -> [e]
        | TExprLambda info -> [info.Body]
        | TExprLet (_, _, v, b) -> [v; b]
        | TExprMatch (s, cases) ->
            s :: (cases |> List.collect (fun c ->
                c.Body :: (Option.toList c.Guard)))
        | TExprIf (c, t, e) -> [c; t; e]
        | TExprTuple es | TExprArrayLit (es, _) | TExprZip es | TExprStack es
        | TExprSequence es -> es
        | TExprJoin (es, _) -> es
        | TExprComplexLit (re, im) -> [re; im]
        | TExprFma (a, b, c) -> [a; b; c]
        | TExprMethodFor info -> info.Arrays
        | TExprObjectFor info -> [info.Kernel]
        | TExprApply info -> info.Loop :: info.Kernel :: info.Arrays
        | TExprBind (a, b) | TExprParallel (a, b) | TExprFusion (a, b)
        | TExprChoice (a, b) -> [a; b]
        | TExprFallback (a, b) -> [a; b]
        | TExprFunctorMap (f, c) -> [f; c]
        | TExprCompose (_, l, r) -> [l; r]
        | TExprDotDot (lo, hi) -> [lo; hi]
        | TExprPure e | TExprCompute e | TExprRead e | TExprFillRandom e | TExprRank e
        | TExprExtents e | TExprReynolds (e, _) -> [e]
        | TExprRandGen (_, key, pars, weights, address, _) -> (key :: pars) @ (address |> Option.map (fun (s, o) -> [s; o]) |> Option.defaultValue []) @ (weights |> Option.map fst |> Option.toList)
        | TExprGuard (c, b) -> [c; b]
        | TExprMask (a, p) | TExprIntersect (a, p) | TExprUnion (a, p)
        | TExprGroupBy (a, p) | TExprSort (a, p)
        | TExprCompound (a, p) | TExprSparse (a, p) -> [a; p]
        | TExprReduce (a, p, i) -> [a; p] @ Option.toList i
        | TExprProdSum args -> args
        | TExprUnique a -> [a]
        | TExprTranspose (a, _, _) -> [a]
        | TExprDecompact (a, _) -> [a]
        | TExprGram (l, r, _) -> [l; r]
        | TExprGramApply (l, r, x) -> [l; r; x]
        | TExprMatmul (l, r) -> [l; r]
        | TExprEigh a -> [a]
        | TExprLu a -> [a]
        | TExprLuSolve (l, p, b, _) -> [l; p; b]
        | TExprSolve (a, b) -> [a; b]
        | TExprArrayNegate a -> [a]
        | TExprArrayConjugate a -> [a]
        | TExprContains (a, v) -> [a; v]
        | TExprDisplayEmit (_, _, d, _, idOpt) -> d :: Option.toList idOpt
        | TExprDisplayJson (_, d) -> [d]
        | TExprDisplayNum d -> [d]
        | TExprDisplayStr d -> [d]
        | TExprGroupKeys keys -> keys
        | TExprGroupBucket gk -> [gk]
        | TExprSegments _ -> []
        | TExprUngroup (g, _) -> [g]
        | TExprUngroupRows (rows, _, _) -> rows
        | TExprSegmentsGrid _ -> []
        | TExprUngroupGrid (g, _, _) -> [g]
        | TExprStruct (_, fields) -> fields |> List.map snd
        | TExprIndex (arr, idxs, _) -> arr :: idxs
        | TExprBlock (stmts, final) ->
            let rec stmtExprsOf (s: TypedStmt) : TypedExpr list =
                match s with
                | TStmtLet b -> [b.Value]
                | TStmtAssign (l, r) -> [l; r]
                | TStmtExpr e -> [e]
                | TStmtForIn (_, _, lo, hi, body) ->
                    lo :: hi :: (body |> List.collect stmtExprsOf)
            (stmts |> List.collect stmtExprsOf) @ Option.toList final
        | TExprAssign (l, r) -> [l; r]
        | TExprConstraintCheck (c, _, _) -> [c]
        | TExprBreakIf c -> [c]
        | TExprReplicate (c, b) -> [c; b]
        | TExprAlign (es, _) -> es
        | TExprPartialApp (_, a, _) -> [a]

/// The LAMBDA an argument is -- a literal, or a `let`-bound one named by the
/// argument (the binding the name RESOLVES to) -- if it is one.
let internal argLambdaInfo (env: TypeEnv) (arg: TypedExpr) : TypedLambdaInfo option =
    match arg.Kind with
    | TExprLambda li -> Some li
    | TExprVar (name, vid, _) ->
        match lookupVar name env with
        | Some vi when vi.VarId = vid ->
            (match vi.TypedValue with
             | Some { Kind = TExprLambda li } -> Some li
             | _ -> None)
        | _ -> None
    | _ -> None

/// Does a lambda body use the parameter `pv` ONLY as the head of an
/// application (`v(0)`, recorded while it was open -- TypeEnv.ArrowApplication),
/// or not at all? Such a parameter can still become an array or a function: the
/// applications are typed when it is bound. Any other use (`v * 2.0`, `v` as an
/// argument or the result) was typed while it was open as a single VALUE, and
/// the body would not be re-typed.
let internal lambdaParamOnlyApplied (pv: IRId) (body: TypedExpr) : bool =
    let rec ok (e: TypedExpr) =
        match e.Kind with
        | TExprApp ({ Kind = TExprVar (_, id, _) }, args) when id = pv -> args |> List.forall ok
        | TExprVar (_, id, _) when id = pv -> false
        | _ -> typedExprChildren e |> List.forall ok
    ok body

/// AN ARROW PARAMETER AT A CALL: the callee's ArrowObligations
/// (TypeEnv.FuncArrowObligations), typed against the call judgment's
/// instantiation (`copyMap`, declaration variable -> copy). The argument decides
/// the arrow's kind:
///   * an ARRAY whose coordinate count is the argument count (the body reads an
///     element -- a call site neither broadcasts nor reduces rank), subscripted
///     by int-valued instances: the application's result is its element;
///   * a FUNCTION of that many arguments, each int-valued argument met by a slot
///     that takes an integer (arrowFuncSlotClash): the result is its return;
///   * still OPEN (the caller's own parameter, a kernel's eta parameter): the
///     application is re-recorded on it (PendingArrowApps), so whatever reaches
///     the caller's variable decides -- and a caller's parameter becomes an arrow
///     parameter of the caller in turn;
///   * anything else (a scalar, a tuple, ...) is refused at the argument.
/// Variables of the obligations the signature does not mention (the
/// application's result, when the body consumes it) get copies of their own;
/// a declaration variable that is not generic (an integer literal's) is read at
/// its default, never bound by a call. Returns the refusal's argument position.
let internal arrowObligationClash (env: TypeEnv) (tFunc: TypedExpr) (paramTys: IRType list)
                                  (tArgs: TypedExpr list) (quantified: int -> bool)
                                  (copyMap: Map<int, int>) : (int * TypeError) option =
    match calleeDeclId env tFunc with
    | None -> None
    | Some fid ->
    match env.FuncArrowObligations.TryGetValue fid with
    | true, obs when not obs.IsEmpty ->
        let subst = env.Subst
        let calleeDesc =
            match tFunc.Kind with
            | TExprVar (name, _, _) -> $"'{name}'"
            | _ -> "this function"
        let mutable mapping = copyMap |> Map.map (fun _ c -> IRTInfer c)
        let obVars =
            obs
            |> List.collect (fun ob -> IRTInfer ob.Head :: ob.Result :: ob.Args)
            |> List.map (freeInferVars subst)
            |> Set.unionMany
            |> Set.filter quantified
        for v in obVars do
            if not (mapping.ContainsKey v) then
                match subst.Fresh() with
                | IRTInfer c ->
                    subst.CopyArityConstraint(v, c)
                    subst.CopyRankLowerBound(v, c)
                    subst.CopyLiteralDefault(v, c)
                    mapping <- Map.add v (IRTInfer c) mapping
                | _ -> ()
        let inst (t: IRType) = substInferVars mapping (subst.Resolve t)
        // An instance argument still open as a declaration literal: read at its
        // default (an integer literal subscript is an Int64).
        let atDefault (t: IRType) =
            match IR.stripUnits (subst.Resolve t) with
            | IRTInfer aid ->
                (match subst.GetLiteralDefault aid with
                 | Some et -> IRTScalar et
                 | None -> subst.Resolve t)
            | r -> r
        let rootOf (t: IRType) =
            match subst.Resolve t with
            | IRTInfer r -> Some r
            | _ -> None
        obs |> List.tryPick (fun ob ->
            let headRoot = rootOf (IRTInfer ob.Head)
            let pos =
                paramTys
                |> List.tryFindIndex (fun p -> headRoot.IsSome && rootOf p = headRoot)
                |> Option.defaultValue 0
            let n = ob.Args.Length
            let headInst = inst (IRTInfer ob.Head)
            let argInsts = ob.Args |> List.map inst
            let resInst = inst ob.Result
            let refuse (actual: IRType) =
                Some (pos, ArgTypeMismatch (pos + 1, calleeDesc, arrowExpectation n, ppIRType (subst.Resolve actual)))
            match IR.stripUnits (subst.Resolve headInst) with
            | IRTInfer aid ->
                // Open at the call. Link the copy to the argument that reaches it
                // and leave the application pending on it.
                (if pos < tArgs.Length then
                     match subst.Resolve (List.item pos tArgs).Type with
                     | IRTInfer argId when argId <> aid ->
                         if isCurrentDeclParam env argId then subst.MarkPolymorphic argId
                         unify subst (IRTInfer aid) (IRTInfer argId) |> ignore
                     | _ -> ())
                let span = if pos < tArgs.Length then (List.item pos tArgs).Span else tFunc.Span
                let synth (name: string) (t: IRType) = mkTypedSpan (TExprVar (name, 0, None)) t span
                env.PendingArrowApps.Add
                    { Head = synth "__arrow" headInst
                      Args = argInsts |> List.map (synth "__arrow_arg")
                      Result = resInst
                      Span = span
                      Written = false }
                None
            | ArrayElem a ->
                let coords =
                    a.IndexTypes |> List.sumBy (fun ix ->
                        match ix.Symmetry with
                        | SymSymmetric | SymAntisymmetric | SymHermitian when ix.Rank >= 2 -> ix.Rank
                        | _ -> 1)
                if coords <> n then
                    Some (pos, ArgRankMismatch (pos + 1, n, coords,
                                                $"read element-wise by the body, which applies it to {n} index argument(s)",
                                                ppIRType (subst.Resolve headInst)))
                else
                    // Each subscript must be an index: an integer (any open
                    // instance is read so), and a NAMED index value only into
                    // an axis of that name (the nominal rule, formalism 3.10).
                    let plainAxes =
                        a.IndexTypes |> List.forall (fun ix -> ix.Rank <= 1)
                    let badArg =
                        argInsts |> List.indexed |> List.tryPick (fun (j, t) ->
                            match IR.stripUnits (subst.Resolve t) with
                            | IRTInfer _ -> None
                            | IRTIdxTagged (_, IRefNamed nm) when plainAxes && not (nm.StartsWith "__") ->
                                (match (List.item j a.IndexTypes).Tag with
                                 | Some tag when not (tag.StartsWith "__") && tag <> nm -> Some t
                                 | _ -> None)
                            | _ when not (arrowIntValued subst t) -> Some t
                            | _ -> None)
                    match badArg with
                    | Some bad ->
                        Some (pos, ArgTypeMismatch (pos + 1, calleeDesc,
                                                    $"an array the body can subscript with what it passes at this call ({ppIRType (subst.Resolve bad)}) -- "
                                                    + "a subscript is an integer index, and a named index value only indexes an axis of its own name",
                                                    ppIRType (subst.Resolve headInst)))
                    | None ->
                        match unify subst resInst a.ElemType with
                        | Ok () -> None
                        | Error e -> Some (pos, e)
            | FuncElem (slots, ret) ->
                if slots.Length <> n then refuse headInst
                else
                    match arrowFuncSlotClash subst slots argInsts with
                    | Some _ -> refuse headInst
                    | None ->
                        let copyIds =
                            mapping |> Map.toSeq |> Seq.choose (fun (_, t) -> match t with IRTInfer c -> Some c | _ -> None) |> Set.ofSeq
                        let lambdaArg =
                            if pos < tArgs.Length then argLambdaInfo env (List.item pos tArgs) else None
                        let slotErr =
                            List.zip slots argInsts |> List.indexed |> List.tryPick (fun (j, (slot, argTy)) ->
                                match IR.stripUnits (subst.Resolve argTy) with
                                // Neither this call's copy nor a literal: the
                                // declaration's own, never bound by a call.
                                | IRTInfer aid when (subst.GetLiteralDefault aid).IsNone && not (copyIds.Contains aid) -> None
                                // An OPEN slot (an unannotated lambda parameter)
                                // is made an array or a function here only when
                                // the lambda's body used it as nothing but an
                                // application head (lambdaParamOnlyApplied): its
                                // applications are still pending and are typed
                                // against it once it is bound. Any other use was
                                // typed while it was open, as a value, and would
                                // not be re-typed.
                                | ArrayElem _ | IRTArrow _ as a when (match IR.stripUnits (subst.Resolve slot) with
                                                                      | IRTInfer _ -> true
                                                                      | _ -> false) ->
                                    let onlyApplied =
                                        lambdaArg |> Option.exists (fun li ->
                                            match List.tryItem j li.Params with
                                            | Some p ->
                                                subst.Resolve p.Type = subst.Resolve slot
                                                && lambdaParamOnlyApplied p.VarId li.Body
                                            | None -> false)
                                    // Bound at its SHAPE, as arrowBindErr binds a
                                    // lambda's arrow parameter: an unnamed literal
                                    // axis becomes an unknown one.
                                    let shape =
                                        match a with
                                        | ArrayElem at ->
                                            mkArrayLike { at with
                                                            IndexTypes =
                                                                at.IndexTypes |> List.map (fun ix ->
                                                                    match ix.Tag, ix.Extent with
                                                                    | None, IRLit (IRLitInt _) when ix.IxKind = IxKPlain && ix.Symmetry = SymNone ->
                                                                        { ix with Extent = mkAbstractAxisExtent (env.Builder.FreshId()) }
                                                                    | _ -> ix) }
                                        | t -> t
                                    if onlyApplied && (unify subst (IR.stripUnits (subst.Resolve slot)) shape).IsOk then None
                                    else
                                    Some ($"a function whose parameter is declared {ppIRType a} (it is applied to "
                                          + $"one here; annotate the passed function's parameter -- an unannotated one is typed as a single value)")
                                | _ ->
                                    match unify subst (IR.stripUnits (subst.Resolve slot)) (IR.stripUnits (atDefault argTy)) with
                                    | Ok () -> None
                                    | Error _ ->
                                        if argInsts |> List.forall (arrowIntValued subst) then Some (arrowExpectation n)
                                        else
                                            let passed = argInsts |> List.map (atDefault >> ppIRType) |> String.concat ", "
                                            Some $"a function taking what the body passes it at this call ({passed})")
                        match slotErr with
                        | Some expectation ->
                            Some (pos, ArgTypeMismatch (pos + 1, calleeDesc, expectation, ppIRType (subst.Resolve headInst)))
                        | None ->
                            match unify subst resInst ret with
                            | Error e -> Some (pos, e)
                            | Ok () ->
                                // The body was typed reading ONE VALUE from the
                                // application -- an array instance's element --
                                // and is emitted once per instance with that
                                // reading (its arithmetic on the result is
                                // scalar). A function returning an array or a
                                // function would hand that body a different
                                // shape: refused, exactly as the array reading
                                // refuses a rank above the argument count.
                                match IR.stripUnits (subst.Resolve resInst) with
                                | ArrayElem _ | IRTArrow _ ->
                                    Some (pos, ArgTypeMismatch (pos + 1, calleeDesc,
                                                                $"a function returning a single value (the body reads one value from it, as from an array of rank {n})",
                                                                ppIRType (subst.Resolve headInst)))
                                | _ -> None
            | t -> refuse t)
    | _ -> None

// ---------------------------------------------------------------------------
// ARRAY INSTANCES of arrow parameters (TypeEnv.ArrowVariant)
//
// arrowObligationClash judges a call against the GENERIC declaration, whose
// application of an arrow parameter is a call. When the call hands that
// parameter an ARRAY, the call is re-targeted at an ARRAY INSTANCE: the same
// declaration checked again with the parameter typed as that array, so the
// application is an ordinary subscript in it -- typed as one (pinSubscriptParams
// pins an index parameter to the array's index type), guarded as one by zonk
// (an unproven position into a named axis, a literal past an unknown extent:
// BL8006), judged as one by the post-zonk sweeps, lowered as one. Nothing is
// rewritten after zonk. A function, or a still-open argument, keeps the generic
// declaration.
// ---------------------------------------------------------------------------

/// The checker of array instances (TypeCheckInfer.checkArrowVariant): given the
/// callee binder and the parameter types a call fixes, the head naming the
/// instance. A forward reference -- an instance is a declaration re-checked,
/// which the checker's rec-chain owns -- installed by TypeCheck.checkModule
/// before any declaration is checked.
let internal arrowVariantHook : (TypeEnv -> IRId -> Map<int, IRType> -> TypeResult<TypedExpr>) ref =
    ref (fun _ _ _ -> Error (Other "internal: the arrow-instance checker is not installed (TypeCheck.checkModule installs it)"))

/// An array argument's type as an ARRAY INSTANCE's parameter type: the
/// argument's own type -- its element, index names and extents, so a literal
/// subscript is judged at compile time against a known extent exactly as in
/// the IR specialization it replaces -- except that an UNKNOWN axis (an
/// abstract annotation's, mkAbstractAxisExtent) is a fresh unknown of the
/// instance's own: the caller's axis identity names the CALLER's axis, and
/// one instance serves every caller. `identity` mints the fresh identities.
let rec internal arrowInstanceParamType (identity: unit -> int) (t: IRType) : IRType =
    match t with
    | IRTUnitAnnotated (inner, u) -> IRTUnitAnnotated (arrowInstanceParamType identity inner, u)
    | ArrayElem at ->
        let ixs =
            at.IndexTypes |> List.map (fun ix ->
                if isAbstractAxisExtent ix.Extent then { ix with Extent = mkAbstractAxisExtent (identity ()) }
                else ix)
        mkArrayLike { at with IndexTypes = ixs }
    | t -> t

/// The key an array instance is cached under (TypeEnv.ArrowVariants): the
/// origin binder and each fixed parameter's type, with every unknown axis and
/// every generic variable of the caller's spelled alike -- the instance is
/// generic in them (checkArrowVariant re-mints them as its own).
let internal arrowInstanceKey (subst: Subst) (origin: IRId) (fixedTys: Map<int, IRType>) : string =
    let norm (t: IRType) =
        let t = subst.Resolve t
        let mapping =
            freeInferVars subst t |> Set.toList
            |> List.choose (fun v -> if subst.IsPolymorphicId v then Some (v, IRTInfer -1) else None)
            |> Map.ofList
        // An unnamed axis's own identity names the ARGUMENT's axis, not a
        // shape: two arrays of one shape share an instance.
        let rec anonAxes (t: IRType) =
            match t with
            | IRTUnitAnnotated (inner, u) -> IRTUnitAnnotated (anonAxes inner, u)
            | ArrayElem at ->
                mkArrayLike { at with
                                IndexTypes = at.IndexTypes |> List.map (fun ix -> if ix.Tag.IsNone then { ix with Id = 0 } else ix) }
            | t -> t
        substInferVars mapping t |> arrowInstanceParamType (fun () -> 0) |> anonAxes
    let parts = fixedTys |> Map.toList |> List.map (fun (i, t) -> $"{i}=%A{norm t}")
    $"""{origin}|{String.concat ";" parts}"""

/// The ARRAY INSTANCE a call needs: the callee's arrow parameters
/// (TypeEnv.FuncArrowObligations) this call hands an ARRAY, each with its
/// argument's type. None when there is none, or -- with `settled` -- when such
/// an argument still holds a variable that is neither the caller's generic one
/// nor an open literal's: what binds it later decides the instance, and the
/// module-end pass (TypeCheck.checkModule) re-targets the call then.
let internal arrowArrayArgs (env: TypeEnv) (tFunc: TypedExpr) (tArgs: TypedExpr list) (settled: bool)
                            : (IRId * Map<int, IRType>) option =
    match calleeDeclId env tFunc with
    | None -> None
    | Some fid ->
    match env.FuncArrowObligations.TryGetValue fid with
    | true, obs when not obs.IsEmpty && env.ArrowDeclSources.ContainsKey fid ->
        let subst = env.Subst
        let rootOf (t: IRType) =
            match subst.Resolve t with
            | IRTInfer r -> Some r
            | _ -> None
        let paramTys =
            match IR.stripUnits (subst.Resolve tFunc.Type) with
            | FuncElem (ps, _) -> ps
            | _ -> []
        let positions =
            obs
            |> List.choose (fun ob ->
                let h = rootOf (IRTInfer ob.Head)
                paramTys |> List.tryFindIndex (fun p -> h.IsSome && rootOf p = h))
            |> List.distinct
            |> List.filter (fun i -> i < tArgs.Length)
        let arrays =
            positions |> List.choose (fun i ->
                let t = subst.Resolve (List.item i tArgs).Type
                match IR.stripUnits t with
                | ArrayElem _ -> Some (i, t)
                | _ -> None)
        let unsettled =
            settled
            && arrays |> List.exists (fun (_, t) ->
                freeInferVars subst t
                |> Set.exists (fun v -> not (subst.IsPolymorphicId v) && (subst.GetLiteralDefault v).IsNone))
        if arrays.IsEmpty || unsettled then None
        else Some (fid, Map.ofList arrays)
    | _ -> None

let rec internal dispatchAppOrIndex (env: TypeEnv) (tFunc: TypedExpr) (tArgs: TypedExpr list) : TypeResult<TypedExpr> =
    // MATCH ON THE RESOLVED HEAD, when the head is a bare inference var.
    //
    // `tFunc.Type` is whatever the head node was stamped with where it was
    // BUILT. For a call to a function with a DEDUCED (unannotated) return
    // type, that is the var the checker minted at the call site -- the real
    // array only ever appears in the substitution. Every arm below needs a
    // concrete `ArrayElem` / `FuncElem` shape, so a var head missed all of
    // them and fell into the catch-all, which mints a FRESH var and returns
    // Ok. That vacuity is why
    //
    //     function grid(ts: Array<Float64 like Idx<4>>) = ...rank 2...
    //     let cells = grid(tim)
    //     let row0 = cells(0)
    //
    // typed `row0` as a SCALAR: `check` passed, and codegen -- which sees
    // the post-monomorphization rank-2 binding type -- emitted
    // `double row0 = Array<double,1>{ cells.data[0L], ... }`. Annotating
    // either the function's return or the binding hid it, because then the
    // head was already concrete here.
    //
    // Narrow ON PURPOSE: only a bare `IRTInfer` head is resolved. A head that
    // already matched an arm keeps matching that arm with the same binding,
    // so this can only convert "fell into the vacuous catch-all" into "routed
    // properly" -- and a head that is still open after Resolve reaches the
    // same catch-all it always did.
    let headTy =
        match tFunc.Type with
        | IRTInfer _ -> env.Subst.Resolve tFunc.Type
        | t -> t
    match headTy with
    // SUBSCRIPTING A WREATH ARRAY, handled FIRST and EXPLICITLY -- because
    // of the catch-all's vacuity, not tidiness. A depth-2 OrbIdx record is
    // ONE index slot spanning prod(ri) raw axes, so `W(i,j,k,l)` has 4 args
    // against 1 slot: the arity guard below is FALSE, no other arm matches,
    // and without an arm here the catch-all mints a fresh inference var and
    // returns Ok, letting a bogus subscript reach codegen/interp as a
    // plausible dense read into a flat pool. Every exit below is explicit.
    //
    // THE RULE: a sole wreath slot takes exactly `Rank` = prod(ri) FLAT
    // scalar subscripts (like a rank-k SymIdx group) and yields the ELEMENT
    // type, per docs/plan-orbidx-decompaction.md section 2's
    //     dense[t] = chi(t) * pool[orbRank(canon(t))],  0 on the zero set --
    // both VALUES, not errors. A wrong ARITY (incl. a partial read, which
    // has no residual class) is OrbitSubscriptArity; a wreath slot combined
    // with other index slots has no pool layout, so it gets the generic
    // storage refusal.
    | ArrayElem arrTy when
        not (List.isEmpty tArgs)
        && arrTy.IndexTypes |> List.exists (fun ix -> ix.Symmetry = SymWreath) ->
        (match arrTy.IndexTypes with
         | [ ix ] when ix.Symmetry = SymWreath ->
             let levels = ppOrbitLevels (orbitLevelsOf ix)
             let axes = max 1 ix.Rank
             if tArgs.Length <> axes then
                 Error (OrbitSubscriptArity (levels, axes, tArgs.Length))
             elif tArgs |> List.exists _.Kind.IsTExprWildcard then
                 // A `_` marks a FREE axis; freeing one axis of a wreath is the
                 // partial read again, in the other spelling. Same verdict, and
                 // the arity message's partial-read sentence is the right one:
                 // a hole is a coordinate not supplied.
                 Error (OrbitSubscriptArity (levels, axes, axes - (tArgs |> List.filter _.Kind.IsTExprWildcard |> List.length)))
             else
                 // The nominal tag check runs for uniformity, not because it
                 // can currently fire: `mkWreathIndexRecord` stamps the
                 // "__orbidx" KIND sentinel as the record's Tag, and the check
                 // skips every "__" tag. It is here so that if a wreath class
                 // ever carries its base's tag through (section "DEFERRED: a
                 // BLOCK-SPEC base under a depth >= 2 class"), this door already
                 // enforces it instead of being the one subscript form that
                 // silently does not.
                 checkArrayIndexTags env tFunc arrTy tArgs
                 |> Result.map (fun () ->
                     let identity = match tFunc.Kind with TExprVar (_, _, id) -> id | _ -> None
                     mkTyped (TExprIndex (tFunc, tArgs, identity)) arrTy.ElemType)
         | _ ->
             let ix = arrTy.IndexTypes |> List.find (fun ix -> ix.Symmetry = SymWreath)
             Error (OrbitStorageUnsupported (ppOrbitLevels (orbitLevelsOf ix),
                                             "array subscript of a wreath group combined with other index slots")))
    // TREE SUBSCRIPT AT A STATIC PATH -- the P3/P4 read primitive.
    //
    // `T((c0, c1, ...))` names a complete root-to-leaf path, and because the
    // shape is static and rides the Tag, the leaf offset is a COMPILE-TIME
    // CONSTANT. So this arm builds no tree-shaped IR node at all: it folds the
    // path to its offset and hands back an ORDINARY rank-1 subscript. That is
    // what makes both back ends need nothing -- the C++ lane falls through
    // compoundRead / wreathRead / lazyCompactRead / densePartialSubview to
    // `rawSubscript` (T[k] -> data[k]) and the interpreter's `indexArray` takes
    // its dense peel. They are byte-identical by construction, because both
    // consume the same IRLit.
    //
    // A BARE SCALAR `T(c)` is the one-element path -- the parser has no 1-tuple
    // form, so `T((0))` is a parenthesized scalar and arrives here the same
    // way. It is self-limiting rather than special-cased: the fold accepts it
    // only when the root's c-th child is a LEAF, and otherwise produces the
    // "ends at internal node" message, which is the right answer.
    //
    // Everything NOT a static complete path refuses here: a non-literal
    // coordinate (dynamic paths), a wildcard (partial views), a path that stops
    // at an internal node (a subtree), an over-long or out-of-arity path (a
    // domain error TreeRank names by STEP).
    | ArrayElem arrTy when
        not (List.isEmpty tArgs)
        && arrTy.IndexTypes |> List.exists (fun ix -> ix.IxKind = IxKTree) ->
        (match arrTy.IndexTypes with
         | [ ix ] when ix.IxKind = IxKTree ->
             let rendered = match ix with TreeIdxLike r -> r | _ -> "TreeIdx<?>"
             // Recover the degree sequence from the Tag -- the ONE place the
             // shape lives; it is deliberately not in Extent, which holds the
             // leaf COUNT so that two coincidentally-equal cardinalities stay
             // distinct types.
             (match ix.Tag with
              | Some (TreeTag (_, degrees)) ->
                  let litOf (a: TypedExpr) =
                      match a.Kind with
                      | TExprLit (LitInt n) when n >= 0L && n <= 1000000L -> Some (int n)
                      | _ -> None
                  let pathOf (args: TypedExpr list) : Result<int list, string> =
                      match args with
                      | [ { Kind = TExprTuple comps } ] ->
                          if comps |> List.exists _.Kind.IsTExprWildcard then
                              Error "a wildcard `_` frees a coordinate, which is a PARTIAL path"
                          else
                              let ls = comps |> List.map litOf
                              if not ls.IsEmpty && ls |> List.forall Option.isSome
                              then Ok (ls |> List.map Option.get)
                              else Error "every child selector must be a compile-time integer literal"
                      | [ single ] ->
                          (match litOf single with
                           | Some c -> Ok [ c ]
                           | None ->
                               if single.Kind.IsTExprWildcard then
                                   Error "a wildcard `_` frees a coordinate, which is a PARTIAL path"
                               else
                                   Error "every child selector must be a compile-time integer literal")
                      | _ ->
                          Error "a tree read takes ONE argument -- the whole path as a single tuple, as in T((0, 1)) with inner parentheses"
                  (match pathOf tArgs with
                   | Error why -> Error (TreeIdxUnsupported (rendered, $"this array subscript ({why})"))
                   | Ok path ->
                       (match Blade.TreeRank.treeTables degrees
                              |> Result.bind (fun t -> Blade.TreeRank.treeForwardChecked t path) with
                        | Error detail ->
                            // TreeRank names the failing STEP, the arity it
                            // violated, or the internal node it stopped at.
                            Error (TreeIdxPath (rendered, detail))
                        | Ok off ->
                            // The read, as a plain literal subscript. Span
                            // carried from the original argument so diagnostics
                            // and tooling still point at the path as written.
                            let offLit =
                                { (List.head tArgs) with
                                    Kind = TExprLit (LitInt (int64 off))
                                    Type = IRTScalar ETInt64 }
                            let identity = match tFunc.Kind with TExprVar (_, _, id) -> id | _ -> None
                            Ok (mkTyped (TExprIndex (tFunc, [ offLit ], identity)) arrTy.ElemType)))
              | _ ->
                  // Kind says tree but the tag is missing or unparseable -- a
                  // state validateIR rejects. Refuse rather than fold against
                  // nothing.
                  Error (TreeIdxUnsupported (rendered, "this array subscript (the shape is not recoverable from the index type)")))
         | _ ->
             let ix = arrTy.IndexTypes |> List.find (fun ix -> ix.IxKind = IxKTree)
             let rendered = match ix with TreeIdxLike r -> r | _ -> "TreeIdx<?>"
             // A tree slot beside other slots is the hybrid form. Free in the
             // TYPE, but the read needs the trailing subscripts to survive the
             // path fold, which is a residual-view shape -- later, with the
             // partial paths.
             Error (TreeIdxUnsupported (rendered, "an array subscript of a tree slot combined with other index slots")))
    // FULL-ARITY READ OF A COMPACT GROUP -- same hole the wreath arm above
    // closes. A rank-k compact slot (SymIdx/AntisymIdx/HermitianIdx) is ONE
    // index record spanning k dims and takes k FLAT subscripts, so `A(i,j)`
    // presents 2 args against 1 slot: the next arm's arity guard is FALSE,
    // and without this arm the read reaches the catch-all, mints a FRESH
    // inference var, and succeeds at a type nothing downstream constrains
    // -- defaulting to Float64 (a complex read needed a hand-written
    // `: Complex128` to build at all, corpus index-types/168). Answer at
    // the element type, exact arity only: a SHORT read still routes to the
    // arm below (whole slots), an over-supplied one still falls through.
    | ArrayElem arrTy when
        not (List.isEmpty tArgs)
        && tArgs.Length > arrTy.IndexTypes.Length
        && tArgs.Length = (arrTy.IndexTypes |> List.sumBy (fun ix -> max 1 ix.Rank))
        && arrTy.IndexTypes |> List.exists (fun ix ->
               ix.Rank >= 2 &&
               (match ix.Symmetry with
                | SymSymmetric | SymAntisymmetric | SymHermitian -> true
                | SymNone | SymWreath -> false)) ->
        if tArgs |> List.exists _.Kind.IsTExprWildcard then
            // A hole frees ONE axis of the group, and a partially-read compact
            // group has no residual class -- the same refusal the wreath arm
            // gives a partial read, and the reason `decompact` exists.
            Error (Other "a wildcard `_` frees one axis of a compact (SymIdx / AntisymIdx / HermitianIdx) group, and a partially-read compact group has no residual class. Supply every coordinate of the group, or decompact(A, d) first and read the freed axis there.")
        else
            foldEnumIdxLabels env arrTy tArgs
            |> Result.bind (fun tArgs ->
            checkArrayIndexTags env tFunc arrTy tArgs
            |> Result.map (fun () ->
                let identity = match tFunc.Kind with TExprVar (_, _, id) -> id | _ -> None
                mkTyped (TExprIndex (tFunc, tArgs, identity)) arrTy.ElemType))
    | ArrayElem arrTy when
        // A compound head takes FLAT subscripts (k for the axis + one per
        // trailing dim), so its arg budget exceeds the slot count -- and it
        // ALWAYS routes here so validateTabulatedIndex owns the flat-count
        // accounting (no silent fresh-var fallthrough for compound heads).
        (match arrTy.IndexTypes with
         | h :: _ when h.IxKind = IxKCompound -> true
         | _ -> tArgs.Length <= arrTy.IndexTypes.Length) ->
        validateTabulatedIndex env arrTy tArgs
        |> Result.bind (fun () ->
        foldEnumIdxLabels env arrTy tArgs
        |> Result.bind (fun tArgs ->
        checkArrayIndexTags env tFunc arrTy tArgs
        |> Result.bind (fun () ->
            let identity = match tFunc.Kind with TExprVar (_, _, id) -> id | _ -> None
            // Tabulated-head consumption: the FIRST slot is one rank-k axis
            // filled by ONE k-tuple at the IR level (classify/compoundRead
            // read the pinned/free split off it).
            //   COMPOUND: flat surface B(c0,...,c(k-1),t...) -- pack the
            //       first k args into a synthetic tuple; j = k always.
            //   SPARSE: tuple surface S((a,_)) arrives as-is; j <= k.
            // Remaining args consume the trailing regular slots.
            let headIsTabulated =
                match arrTy.IndexTypes with
                | h :: _ -> h.IxKind = IxKCompound || h.IxKind = IxKSparse
                | [] -> false
            if headIsTabulated then
                let headSlot = List.head arrTy.IndexTypes
                let trailingSlots = List.tail arrTy.IndexTypes
                let k = headSlot.Rank
                // Flat compound packing: first k args -> one synthetic full
                // tuple (k >= 2; a rank-1 axis keeps its bare scalar arg,
                // which the existing scalar path already consumes as j = 1).
                let tArgs =
                    if headSlot.IxKind = IxKCompound && k >= 2 && tArgs.Length >= k then
                        let coords = tArgs |> List.truncate k
                        let packed =
                            { (List.head coords) with
                                Kind = TExprTuple coords
                                Type = IRTTuple (coords |> List.map (_.Type)) }
                        packed :: (tArgs |> List.skip k)
                    else tArgs
                let firstArg = List.head tArgs
                // Wildcard form (full-arity tuple, `_` = free axes):
                // validateTabulatedIndex already enforced full arity + >=1
                // pinned coordinate. CONSUME the holes by rewriting each
                // TExprWildcard to a unit literal, so the wildcard-escape
                // scan sees a hole-free value and codegen reads the
                // pinned/free split directly off the tuple (unit is never a
                // valid coordinate); IRCompoundProject records only the count.
                let wildPositions =
                    match firstArg.Kind with
                    | TExprTuple elems ->
                        elems |> List.mapi (fun i e -> (i, e))
                              |> List.choose (fun (i, e) -> match e.Kind with TExprWildcard -> Some i | _ -> None)
                    | _ -> []
                let firstArg =
                    if List.isEmpty wildPositions then firstArg
                    else
                        match firstArg.Kind with
                        | TExprTuple elems ->
                            let elems' =
                                elems |> List.map (fun e ->
                                    match e.Kind with
                                    | TExprWildcard -> { e with Kind = TExprLit LitUnit; Type = IRTUnit }
                                    | _ -> e)
                            { firstArg with
                                Kind = TExprTuple elems'
                                Type = IRTTuple (elems' |> List.map (_.Type)) }
                        | _ -> firstArg
                let tArgs = firstArg :: List.tail tArgs
                // Trailing-dim wildcards: `B((...), _)` leaves the trailing
                // dim FREE, identical to omitting the arg (lex-sorted,
                // trailing-innermost layout). Dropped here so the
                // wildcard-escape scan sees no unconsumed hole; must form a
                // contiguous SUFFIX -- a wildcard BEFORE a supplied trailing
                // index frees an INTERIOR dim, which needs a data restructure
                // and is rejected.
                let keptRemaining, interiorTrailingHole =
                    let isWild (e: TypedExpr) = e.Kind.IsTExprWildcard
                    let rec split acc seenWild args =
                        match args with
                        | [] -> (List.rev acc, false)
                        | e :: rest when isWild e -> split acc true rest
                        | e :: rest ->
                            if seenWild then (List.rev acc, true)
                            else split (e :: acc) false rest
                    split [] false (List.tail tArgs)
                let tArgs = firstArg :: keptRemaining
                // j = pinned coordinate count: tuple arity minus free axes for
                // the wildcard form; tuple arity for a short (prefix) tuple; 1
                // for a scalar (rank-1 compound). validateTabulatedIndex already
                // rejected the malformed shapes, so this is well-formed here.
                let j =
                    if not (List.isEmpty wildPositions) then
                        (match firstArg.Kind with
                         | TExprTuple es -> es.Length
                         | _ -> k) - wildPositions.Length
                    else
                        match env.Subst.Resolve firstArg.Type with
                        | IRTTuple tys -> tys.Length
                        | _ -> 1
                // Parent IR reference for the residual extent carrier: a
                // plain variable, or IRLitUnit placeholder for a non-var
                // parent (field access, chained residual) -- harmless since
                // codegen reads the ACTUAL array expr at the IRIndex site;
                // the carrier is only consulted for pass-throughs and
                // tryEvalIntIR (which returns None for it, correctly).
                let parentIR =
                    match tFunc.Kind with
                    | TExprVar (_, vid, _) -> IRVar (vid, headTy)
                    | _ -> IRLit IRLitUnit
                let residualFragment =
                    tabulatedResidualType headSlot parentIR j (fun () -> env.Builder.FreshId())
                // Remaining args after the compound tuple consume trailing slots.
                let remainingArgs = List.tail tArgs
                let trailingRemaining =
                    if remainingArgs.Length <= List.length trailingSlots then
                        trailingSlots |> List.skip remainingArgs.Length
                    else trailingSlots  // (validateTabulatedIndex/arity guard covers over-supply)
                let finalSlots = residualFragment @ trailingRemaining
                if interiorTrailingHole then
                    Error (Other "A wildcard `_` among the trailing indices of a compound array must come AFTER all supplied trailing indices (a free interior trailing dimension would require restructuring the trailing blocks). Reorder, or leave the trailing dims free by omitting them.")
                elif List.isEmpty finalSlots then
                    Ok (mkTyped (TExprIndex (tFunc, tArgs, identity)) arrTy.ElemType)
                else
                    Ok (mkTyped (TExprIndex (tFunc, tArgs, identity))
                                (mkArrayLike { arrTy with IndexTypes = finalSlots }))
            else
                // COORDINATES, NOT RECORDS. A compact group (`SymIdx<2, n>`,
                // AntisymIdx, HermitianIdx) is ONE record spanning `Rank`
                // coordinates, so the supplied count is walked through the
                // records in flat coordinate order rather than one per
                // record: `S(1)` over a rank-2 symmetric result used to count
                // one argument against one record and answer "scalar", and
                // the emitted `double x = S[1L]` was a row pointer. A count
                // that lands INSIDE a group has no residual class (the same
                // refusal the wildcard arm above gives); one that lands on a
                // record boundary keeps the records after it as the view.
                let rec walk (coords: int) (recs: IRIndexType list) : Result<IRIndexType list, IRIndexType> =
                    match recs with
                    | [] -> Ok []
                    | _ when coords = 0 -> Ok recs
                    | ix :: rest ->
                        let compact =
                            ix.Rank >= 2 &&
                            (match ix.Symmetry with
                             | SymSymmetric | SymAntisymmetric | SymHermitian -> true
                             | SymNone | SymWreath -> false)
                        // Only a compact group spans several coordinates
                        // here; every other record keeps the one-argument-
                        // per-record accounting it always had (wreath
                        // partial reads are refused by their own arm above).
                        let span = if compact then ix.Rank else 1
                        if coords >= span then walk (coords - span) rest
                        else Error ix
                match walk tArgs.Length arrTy.IndexTypes with
                | Error ix ->
                    Error (Other (sprintf "a partial read of a compact (SymIdx / AntisymIdx / HermitianIdx) group has no residual class: %d coordinate(s) were supplied but the group spans %d. Supply every coordinate of the group, or decompact(A, d) first and read the freed axis there." tArgs.Length ix.Rank))
                | Ok [] ->
                    Ok (mkTyped (TExprIndex (tFunc, tArgs, identity)) arrTy.ElemType)
                | Ok remaining ->
                    Ok (mkTyped (TExprIndex (tFunc, tArgs, identity))
                                (mkArrayLike { arrTy with IndexTypes = remaining })))))
    | FuncElem (paramTys, retTy) ->
        // WIDTH SCHEMA first, so every check below (and the arity accounting,
        // and the emitted TExprApp) sees the regrouped list: `g(b, c)` against
        // `g(t: Tuple<2>)` is one argument, the pair. No-op unless the callee
        // declares a tuple parameter AND the flat pairing does not fit, so the
        // ordinary call path is untouched.
        let tArgs = regroupArgsByWidth env paramTys tArgs
        // AN ARROW PARAMETER MEETS ITS ARGUMENT. A parameter the callee's body
        // applied while it was open (`let u = lambda(c) -> c(0) + 1.0`; see
        // TypeEnv.ArrowApplication) is still an open, non-generic variable:
        // the CONTEXT decides it, and this argument is that context. An array
        // or a function is bound to it and the body's applications are typed
        // against it now (dischargeArrowApp), so the call's result is the real
        // one; anything else cannot be applied and is refused here, at the
        // argument. A lambda is ONE body (not generic), so its first such call
        // decides for every later one -- a later argument of the other kind
        // meets the ordinary judgment below and is refused. (A DECLARATION's
        // arrow parameter is generic and judged per call: arrowObligationClash.)
        let arrowBindErr =
            let n = min paramTys.Length tArgs.Length
            List.zip (List.truncate n paramTys) (List.truncate n tArgs)
            |> List.indexed
            |> List.tryPick (fun (i, (pTy, arg)) ->
                match env.Subst.Resolve pTy with
                | IRTInfer pid when not (env.Subst.IsPolymorphicId pid) ->
                    let apps =
                        env.PendingArrowApps |> List.ofSeq |> List.filter (fun a ->
                            match env.Subst.Resolve a.Head.Type with
                            | IRTInfer h -> h = pid
                            | _ -> false)
                    if apps.IsEmpty then None
                    else
                        let desc =
                            match tFunc.Kind with
                            | TExprVar (name, _, _) -> $"'{name}'"
                            | _ -> "this function"
                        let nArgs = apps |> List.map (fun a -> a.Args.Length) |> List.max
                        let indexShaped = apps |> List.forall (fun a -> a.Args |> List.forall (arrowArgIndexLike env))
                        // The function reading's int-valued rule, said at the
                        // argument that brought the function.
                        let slotClash =
                            match IR.stripUnits (env.Subst.Resolve arg.Type) with
                            | FuncElem (slots, _) ->
                                apps |> List.exists (fun a ->
                                    slots.Length = a.Args.Length
                                    && (arrowFuncSlotClash env.Subst slots (a.Args |> List.map (_.Type))).IsSome)
                            | _ -> false
                        match IR.stripUnits (env.Subst.Resolve arg.Type) with
                        | IRTInfer _ -> None
                        | FuncElem _ as t when slotClash ->
                            Some (i, ArgTypeMismatch (i + 1, desc, arrowExpectation nArgs, ppIRType t))
                        | ArrayElem _ | FuncElem _ ->
                            // An array binds at its SHAPE, not its length: the
                            // lambda is one body, and a later call with another
                            // length is as legal as it is for a `Float64^1`
                            // parameter (an unnamed literal axis becomes an
                            // unknown one, read from the argument at run time;
                            // a NAMED index type keeps its extent -- it is part
                            // of the name's identity).
                            let bound =
                                match env.Subst.Resolve arg.Type with
                                | ArrayElem at ->
                                    let ixs =
                                        at.IndexTypes |> List.map (fun ix ->
                                            match ix.Tag, ix.Extent with
                                            | None, IRLit (IRLitInt _) when ix.IxKind = IxKPlain && ix.Symmetry = SymNone ->
                                                { ix with Extent = mkAbstractAxisExtent (env.Builder.FreshId()) }
                                            | _ -> ix)
                                    mkArrayLike { at with IndexTypes = ixs }
                                | t -> t
                            match unify env.Subst pTy bound with
                            | Error e -> Some (i, e)
                            | Ok () ->
                                for a in apps do
                                    env.PendingArrowApps.RemoveAll(fun x -> obj.ReferenceEquals(x, a)) |> ignore
                                apps |> List.tryPick (fun a ->
                                    match dischargeArrowApp env a with
                                    | Error e -> Some (i, e)
                                    | Ok () ->
                                        // The body consumed the application as
                                        // ONE VALUE while the head was open (its
                                        // arithmetic was typed scalar): a partial
                                        // read of a higher-rank array, or a
                                        // function returning an array, would hand
                                        // that body a shape it was not typed for.
                                        match IR.stripUnits (env.Subst.Resolve a.Result) with
                                        | ArrayElem _ | IRTArrow _ ->
                                            let expectation =
                                                if indexShaped then
                                                    arrowExpectation nArgs + " and reading a single value from it"
                                                else "a function returning a single value"
                                            Some (i, ArgTypeMismatch (i + 1, desc, expectation, ppIRType (env.Subst.Resolve arg.Type)))
                                        | _ -> None)
                        | t ->
                            let expectation =
                                if indexShaped then arrowExpectation nArgs
                                else "a function (the body applies it to arguments)"
                            Some (i, ArgTypeMismatch (i + 1, desc, expectation, ppIRType t))
                | _ -> None)
        match arrowBindErr with
        | Some (i, e) ->
            if i < tArgs.Length && (List.item i tArgs).Span.StartLine > 0 then
                setCurrentExprSpan (List.item i tArgs).Span
            Error e
        | None ->
        // THE CALL JUDGMENT's ladder (docs/plans/plan-call-judgment.md). The
        // core step -- instantiate the signature, unify the copy against the
        // arguments -- is the SIXTH check below; the checks ahead of it exist
        // because each names its defect more precisely than a unify failure
        // would, and the ones after it are refinements unify does not model
        // (extents, co-iteration). Each catches a mismatch g++ rejects that
        // Blade would otherwise typecheck clean -- except the extent checks,
        // which g++ accepts and which fault at RUNTIME:
        //   irrepsClash  - BLOCK-SPEC (irreps/point-group) pairs must match
        //                  identity, not just extent.
        //   rankClash    - a rank-k compact slot is k emitted dims but ONE
        //                  slot; SymIdx<2,4> vs Idx<10> looks equal otherwise.
        //   unitClash    - unit signatures never meet at a call site
        //                  otherwise; both-sided signatures must agree.
        //   argRankClash - slot-COUNT (rankClash is per-component within a
        //                  slot); collectAppRankErrors re-runs it post-zonk
        //                  for arguments still open here.
        let irrepsClash =
            let n = min paramTys.Length tArgs.Length
            List.zip (List.truncate n paramTys) (List.truncate n tArgs)
            |> List.mapi (fun i pair -> (i, pair))
            |> List.tryPick (fun (i, (pTy, arg)) ->
                match env.Subst.Resolve pTy, env.Subst.Resolve arg.Type with
                | ArrayElem pa, ArrayElem aa when pa.IndexTypes.Length = aa.IndexTypes.Length ->
                    List.zip pa.IndexTypes aa.IndexTypes
                    |> List.tryPick (fun (pi, ai) ->
                        match pi.Tag, ai.Tag with
                        | Some (BlockSpecTag _), Some (BlockSpecTag _) when indexPairIncompatible pi ai ->
                            Some (i, pi, ai)
                        | _ -> None)
                | _ -> None)
        let rankClash =
            let n = min paramTys.Length tArgs.Length
            List.zip (List.truncate n paramTys) (List.truncate n tArgs)
            |> List.mapi (fun i pair -> (i, pair))
            |> List.tryPick (fun (i, (pTy, arg)) ->
                match env.Subst.Resolve pTy, env.Subst.Resolve arg.Type with
                | ArrayElem pa, ArrayElem aa when pa.IndexTypes.Length = aa.IndexTypes.Length ->
                    List.zip pa.IndexTypes aa.IndexTypes
                    |> List.mapi (fun slot pair -> (slot, pair))
                    |> List.tryPick (fun (slot, (pi, ai)) ->
                        if indexRankDiffers pi ai then Some (i, slot, pi, ai) else None)
                | _ -> None)
        // POINT THE CARET AT THE ARGUMENT THE MESSAGE NAMES. `currentExprSpan`
        // is stamped by `inferExpr` on entry to EVERY node and the last stamp
        // wins (TypeEnv.locateError), so by the time these checks run it holds
        // the LAST argument inferred -- the text said "argument 4" while the
        // caret underlined argument 8 (`examples/lswosa.blade`'s BL3010, and
        // every other argument-indexed refusal below shares the defect). Each
        // check already knows the offending index; re-stamp before building the
        // error so the two agree. No-op when the argument carries no span
        // (synthesized nodes), which leaves the previous behaviour intact.
        let atArg (i: int) =
            if i >= 0 && i < tArgs.Length then
                let s = (List.item i tArgs).Span
                if s.StartLine > 0 then setCurrentExprSpan s
        let unitClash =
            let n = min paramTys.Length tArgs.Length
            // (sig, concrete): `concrete` is false while the (element) type is
            // still an unresolved inference variable — the strict quantity
            // check below must not reject a type that simply isn't KNOWN yet
            // (e.g. a kernel body calling a helper before params are bound).
            let sigOf t =
                match env.Subst.Resolve t with
                | ArrayElem at ->
                    let e = env.Subst.Resolve at.ElemType
                    (IR.getUnits e, not e.IsIRTInfer)
                | IRTInfer _ -> (None, false)
                | resolved -> (IR.getUnits resolved, true)
            let describeArg (au: UnitSig option) =
                match au with
                | None -> "bare (it carries no unit signature)"
                | Some a ->
                    match a.Nominal with
                    | Some qn -> $"the quantity '{qn}'"
                    | None -> $"structurally dimensioned ({ppUnitSig a})"
            List.zip (List.truncate n paramTys) (List.truncate n tArgs)
            |> List.mapi (fun i (pTy, arg) -> (i, sigOf pTy, sigOf arg.Type))
            |> List.tryPick (fun (i, (pu, _), (au, aConcrete)) ->
                match pu, au with
                | Some pu, Some au when not (unitCompatible pu au) ->
                    atArg i
                    Some (UnitMismatch ($"argument {i + 1}", ppUnitSig pu, ppUnitSig au))
                // Convertible but at a different MAGNITUDE. Argument passing
                // is a seam that does not (yet) insert a factor, so name the
                // difference instead of handing the callee a raw number in
                // the wrong magnitude.
                | Some pu, Some au when not (unitSameScale pu au) ->
                    atArg i
                    Some (Other (sprintf
                            "argument %d expects %s but got %s: same dimensions, magnitudes differing by the factor %s"
                            (i + 1) (ppUnitSig pu) (ppUnitSig au)
                            (ppUnitScale (unitConversionFactor au pu))))
                // STRICT quantity slots (BL3010): a parameter declared with a
                // QUANTITY (Nominal = Some) rejects any CONCRETE argument not
                // carrying that nominal — bare and structurally-dimensioned
                // args alike; the caller must ascribe. Structural (None)
                // parameters keep the permissive behavior above exactly.
                | Some pu, _ when pu.Nominal.IsSome
                                  && aConcrete
                                  && (match au with
                                      | Some a -> a.Nominal <> pu.Nominal
                                      | None -> true) ->
                    atArg i
                    Some (QuantityArgMismatch (i + 1, pu.Nominal.Value, describeArg au))
                | _ -> None)
        // ONE LEVEL INTO A TUPLE ARGUMENT. Every check above reads
        // `IR.getUnits` / `ArrayElem` off the argument AS A WHOLE, and both
        // answer "nothing here" for an `IRTTuple` -- so with a COMPONENT-TYPED
        // tuple parameter (`Tuple<U^1, T<time>^1>`, or the identical written
        // `(U^1, T<time>^1)`) a wrong unit or a wrong shape INSIDE a component
        // was invisible at this seam and surfaced as a g++ failure. Written
        // component types exist precisely so that they can be checked, so they
        // get the same two judgements their top-level twins get.
        //
        // Folded into `unitClash` rather than added as a fifth clash so the
        // arm structure below is untouched; the top-level verdict still wins.
        //
        // ONE level only (6c's one-level structural rule), and only when the
        // widths already agree -- a width disagreement is unify's ordinary
        // equal-length `IRTTuple` refusal and reads better from there. Both
        // sides must be CONCRETE: a `Tuple<N>` parameter's element slots are
        // fresh inference variables, so the width-only spelling keeps exactly
        // today's behaviour and only the written spelling gains the check.
        let unitClash =
            match unitClash with
            | Some _ -> unitClash
            | None ->
                let n = min paramTys.Length tArgs.Length
                let shapeOf t =
                    match env.Subst.Resolve t with
                    | ArrayElem at -> (at.IndexTypes.Length, env.Subst.Resolve at.ElemType)
                    | r -> (0, r)
                // "Concrete" must see through the UNIT wrapper. A `T<day>^1`
                // annotation produces an inference variable wearing a unit
                // (`IRTUnitAnnotated(IRTInfer _, day)`), and a bare `IRTInfer`
                // test reads that as concrete -- so the rank comparison below
                // fired against a rank inference had not determined yet and
                // rejected a correct program. The identical program without
                // units passed, which is the tell: units must not decide
                // whether a type is known, only what it measures.
                let isConcrete t = not (IR.stripUnits (env.Subst.Resolve t)).IsIRTInfer
                List.zip (List.truncate n paramTys) (List.truncate n tArgs)
                |> List.mapi (fun i (pTy, arg) -> (i, pTy, arg))
                |> List.tryPick (fun (i, pTy, arg) ->
                    match env.Subst.Resolve pTy, env.Subst.Resolve arg.Type with
                    | IRTTuple pcs, IRTTuple acs when pcs.Length = acs.Length ->
                        List.zip pcs acs
                        |> List.mapi (fun j (pc, ac) -> (j, pc, ac))
                        |> List.tryPick (fun (j, pc, ac) ->
                            let where = $"argument {i + 1}, component {j + 1}"
                            let (pr, pe) = shapeOf pc
                            let (ar, ae) = shapeOf ac
                            if not (isConcrete (env.Subst.Resolve pc)) then None
                            else
                            match IR.getUnits pe, IR.getUnits ae with
                            | Some pu, Some au when not (unitCompatible pu au) ->
                                atArg i
                                Some (UnitMismatch (where, ppUnitSig pu, ppUnitSig au))
                            | _ when isConcrete ae && pr <> ar ->
                                atArg i
                                Some (Other (sprintf
                                        "%s: the parameter component is declared %s (rank %d) but the argument component is %s (rank %d). A call site performs no conversion between these -- pass a value of the declared type, or change the declared component type."
                                        where (ppIRType (env.Subst.Resolve pc)) pr
                                        (ppIRType (env.Subst.Resolve ac)) ar))
                            | _ -> None)
                    | _ -> None)
        // THE CALLEE'S UNIT EQUALITIES (TypeEnv.FuncUnitEqualities): a generic
        // body that adds, compares or branches between two parameters demands
        // their units agree, and the call is where the units are known.
        // `add(x: T^0, y: T^0) = x + y` applied to meters and seconds was
        // accepted (and typed assignable to anything) before this.
        let unitClash =
            match unitClash with
            | Some _ -> unitClash
            | None ->
                let calleeName = match tFunc.Kind with TExprVar (nm, _, _) -> Some nm | _ -> None
                match calleeName |> Option.bind (fun nm ->
                        match calleeDeclId env tFunc |> Option.map (fun fid -> env.FuncUnitEqualities.TryGetValue fid) with
                        | Some (true, eqs) when not eqs.IsEmpty -> Some (nm, eqs)
                        | _ -> None) with
                | None -> None
                | Some (nm, eqs) ->
                    let argUnits =
                        tArgs |> List.map (fun a ->
                            match env.Subst.Resolve a.Type with
                            | ArrayElem at -> IR.getUnits (env.Subst.Resolve at.ElemType)
                            | r -> IR.getUnits r)
                    unitEqualityClash eqs argUnits
                    |> Option.map (fun (x, y) ->
                        UnitMismatch ($"the arguments of '{nm}', whose body combines them as one quantity", ppUnitSig x, ppUnitSig y))
        // A Poly<T^r> pack param makes the arrow variadic -- its declared
        // param count says nothing about legal call-site arg counts, so
        // arity accounting stands down (monomorphization owns the call).
        let isVariadic =
            paramTys |> List.exists (fun t -> (env.Subst.Resolve t).IsIRTPoly)
        let argRankClash = firstArgRankClash env.Subst paramTys (tArgs |> List.map (_.Type))
        // The callee NAME through the application spine, plus how many arguments
        // earlier groups already consumed -- which is what turns a DECLARED
        // parameter position into a position in THIS group, so a curried
        // `f(a)(b)` reaches the check too. Shared by the two checks that read a
        // name-keyed table of the callee's declaration: mutClash (write
        // permission) and coIterClash (co-iteration extent agreement).
        let rec appRootAndOffset (t: TypedExpr) : (string * int) option =
            match t.Kind with
            | TExprVar (name, _, _) -> Some (name, 0)
            | TExprApp (f, args) ->
                appRootAndOffset f |> Option.map (fun (n, off) -> (n, off + List.length args))
            | _ -> None
        // The DECLARATION the application spine's root names (binder id, through
        // `let` aliases), which keys the callee's recorded facts.
        let rootDeclId : IRId =
            let rec rootOf (t: TypedExpr) =
                match t.Kind with
                | TExprVar _ -> Some t
                | TExprApp (f, _) -> rootOf f
                | _ -> None
            rootOf tFunc |> Option.bind (calleeDeclId env) |> Option.defaultValue -1
        // mutClash (BL4005) - WRITE PERMISSION, the one check here about the
        // caller's binding form rather than its type. A `mut` parameter writes
        // back into the caller's array, so the caller must hold write access
        // to grant it: formalism 2.7 lists only `let mut x = e` as passable to
        // a `mut` param. Nothing enforced that, and the damage was
        // shape-dependent rather than merely absent -- a plain `let` passed
        // directly WAS mutated in some call shapes and silently was not in
        // others, so the binding form promised one thing and the program did
        // whichever the call shape happened to produce.
        //
        // Keyed on the callee NAME through the application spine, so a curried
        // call reaches it: `f(a)(b)` arrives here with `f(a)` as the head, and
        // `appRootAndOffset` recovers both the root name and how many
        // arguments earlier groups already consumed, which is what turns a
        // declared position into a position in THIS group.
        //
        // Forwarding one `mut` parameter into another is exactly the case that
        // must keep working: a `mut` param binds MutPassable, so it satisfies
        // the same predicate a `let mut` binding does, and no special case is
        // needed. `__`-prefixed callees and arguments are exempt (synthesized
        // buffers, e.g. grad()'s out-buffer ABI).
        let mutClash =
            match appRootAndOffset tFunc with
            | Some (fname, offset) when not (fname.StartsWith "__") ->
                (match env.MutParamPositions.TryGetValue rootDeclId with
                 | true, positions ->
                     positions
                     |> List.tryPick (fun declPos ->
                         let i = declPos - offset
                         if i < 0 || i >= tArgs.Length then None
                         else
                             match (List.item i tArgs).Kind with
                             | TExprVar (aname, _, _) when aname.StartsWith "__" -> None
                             | TExprVar (aname, _, _) ->
                                 (match lookupVar aname env with
                                  | Some info when info.Assign = MutPassable -> None
                                  | Some info when info.Assign = ReadOnly ->
                                      Some (i, fname, declPos, $"'{aname}' is a `let static` or a non-`mut` parameter")
                                  | Some _ -> Some (i, fname, declPos, $"'{aname}' is a plain `let`")
                                  // Not a tracked binding (an import or a
                                  // builtin): no permission to reason about,
                                  // so leave it to the checks that do.
                                  | None -> None)
                             // Anything that is not a NAME has no storage the
                             // caller could observe a write through: the
                             // callee would write into a temporary.
                             | _ -> Some (i, fname, declPos, "a computed expression has no binding to write back into"))
                 | _ -> None)
            | _ -> None
        // extentClash (BL3016) - consumed LAST (see the arm below), and the
        // only check here whose failure is a MEMORY error rather than a g++
        // rejection or a discipline violation. Codegen treats
        // a parameter's LITERAL extent as ground truth: it bakes it into the
        // emitted subscripts, loop bounds and result allocations, and never
        // consults the argument's runtime extent. So `Idx<2>` into an `Idx<4>`
        // parameter emits reads two doubles past the allocation -- in the
        // DECLARATIVE path (`method_for(w) <@> ...`) exactly as in the
        // raw-index path, and worse there, since the over-long result array
        // carries the garbage out as a value.
        //
        // Literal-vs-literal only, ranks equal. A symbolic extent (`Idx<n>`,
        // ragged/compound/opaque) emits a runtime `.extents[d]` read and is
        // already correct, so it keeps the historical looseness -- as does an
        // argument still unresolved here.
        // The comparison itself is the SHARED refinement `staticExtentClash`,
        // the same predicate every ascription seam calls.
        let extentClash =
            let n = min paramTys.Length tArgs.Length
            List.zip (List.truncate n paramTys) (List.truncate n tArgs)
            |> List.mapi (fun i pair -> (i, pair))
            |> List.tryPick (fun (i, (pTy, arg)) ->
                staticExtentClash env.Subst pTy arg.Type
                |> Option.map (fun (d, pe, ae) -> (i, d, pe, ae)))
        // coIterClash (BL3016) - the CALL-SITE half of the zip agreement
        // obligation, and the second memory error on this ladder.
        //
        // `TypeLower.zipHeadClash` refuses a mismatched zip at the zip, but
        // only literal-vs-literal. A callee whose parameters are abstract
        // (`T^1`) has no extents there, so `zip(a, b)` was accepted
        // unconditionally -- and the co-iteration nest bounds EVERY level by
        // operand 1 while every operand peels at every level (IRStorage), so
        // the longer argument's extent is walked over the shorter one's
        // storage. `addup(q6, p3)` on such a body returned a number computed
        // from three doubles past the end of `p`.
        //
        // The callee's body is invisible here, so the obligation rides
        // `FuncCoIterObligations` (checkFunctionDecl). Literal-vs-literal only, on
        // the SHARED (leading) axis, matching every sibling extent check on
        // this ladder: a symbolic extent reads `.extents[d]` at runtime and
        // keeps the historical looseness.
        //
        // The same obligation carries the NOMINAL half (BL3999,
        // `coIterVerdict`): `cov(R(0), H(0))` over a DayIdx and an HourIdx row
        // of equal extent is in bounds, but walks two index spaces as one --
        // the refusal `R(0) * H(0)` gets at top level. buildApplyInfo
        // discharges the same verdict for `method_for(R, H) <@> cov`.
        let coIterClash =
            match appRootAndOffset tFunc with
            | Some (fname, offset) ->
                (match env.FuncCoIterObligations.TryGetValue rootDeclId with
                 | true, obs ->
                     // THE WHOLE APPLICATION SPINE. When a multi-group
                     // application reaches this seam as `TExprApp(TExprApp(f,
                     // [a]), [b])`, the obligation relates a position in the
                     // EARLIER group to one in this group, and rebasing the
                     // positions into this group alone dropped the earlier
                     // arguments. The earlier arguments are recovered from the
                     // spine and declared positions index the flattened list.
                     // NOTE the surface curried call `f(a)(b)` does NOT arrive
                     // this way: the partial application `f(a)` is desugared
                     // to a lambda before this seam, and inside it `b`'s slot
                     // is still an open parameter, so -- like the let-bound
                     // `let h = f(a); h(b)` -- it is the runtime guard's
                     // (BL8011, functions/128 and /131).
                     let rec spineArgs (t: TypedExpr) : TypedExpr list =
                         match t.Kind with
                         | TExprApp (f, args) -> spineArgs f @ args
                         | _ -> []
                     let allArgs = spineArgs tFunc @ tArgs
                     // The span to blame is an argument of THIS group; a
                     // clash partner in an earlier group is named by its
                     // declared position in the message only.
                     let hereIdx (declPos: int) = max 0 (declPos - offset)
                     // (blamed argument, refusal, is-the-EXTENT-half): the
                     // extent half outranks the judgment's own type clash
                     // below, the name half does not.
                     coIterVerdict env obs (fun i -> List.tryItem i allArgs |> Option.map (_.Type))
                     |> Option.map (function
                         | CoIterExtentArgs (a, b, ea, eb) ->
                             (hereIdx b, CoIterArgExtentMismatch (fname, a + 1, b + 1, ea, eb), true)
                         | CoIterExtentBody (a, ea, l) ->
                             (hereIdx a, CoIterBodyExtentMismatch (fname, a + 1, ea, l), true)
                         | CoIterNameArgs (a, b, ha, hb) ->
                             (hereIdx b,
                              CoIterIndexMismatch ($"arguments {a + 1} and {b + 1} of '{fname}'", $"'{fname}'",
                                                   coIterHeadName ha, coIterHeadName hb, ppExtentOf ha.Extent),
                              false)
                         | CoIterNameBody (a, h, bh) ->
                             (hereIdx a,
                              CoIterIndexMismatch ($"argument {a + 1} of '{fname}' and an array its body fixes", $"'{fname}'",
                                                   coIterHeadName h, coIterHeadName bh, ppExtentOf h.Extent),
                              false))
                 | _ -> None)
            | None -> None
        // Consumed ahead of the type clashes: a write-permission violation is
        // about the caller's BINDING FORM, so it stands whatever the types do,
        // and reporting it first keeps a `let` that also needs a cast from
        // being told about the cast instead of the real problem.
        // mutAliasClash (BL4005) - the Fortran no-alias rule, the second half
        // of write permission. A binding passed to a `mut` parameter must not
        // ALSO reach the callee through another parameter as an array (the
        // same name, or a view of it): the callee's writes through the `mut`
        // alias are then visible mid-call through the other, so the result
        // depends on the order the body happens to read and write in --
        // `f(x, x)` with `b(0) = a(0) + 1` after `a(0) = 5` read 5 through `b`.
        // A scalar read of the binding (`x(0)`) is evaluated before the call
        // and is not an alias. `__` names are synthesized and exempt, like
        // mutClash.
        let mutAliasClash =
            match appRootAndOffset tFunc with
            | Some (fname, offset) when not (fname.StartsWith "__") ->
                (match env.MutParamPositions.TryGetValue rootDeclId with
                 | true, positions ->
                     let rec root (t: TypedExpr) =
                         match t.Kind with
                         | TExprVar (n, _, _) -> Some n
                         | TExprIndex (b, _, _) -> root b
                         | _ -> None
                     let isArray (t: TypedExpr) =
                         match env.Subst.Resolve t.Type with
                         | ArrayElem _ -> true
                         | _ -> false
                     positions |> List.tryPick (fun declPos ->
                         let i = declPos - offset
                         if i < 0 || i >= tArgs.Length then None
                         else
                             match (List.item i tArgs).Kind with
                             | TExprVar (aname, _, _) when not (aname.StartsWith "__") ->
                                 tArgs |> List.indexed |> List.tryPick (fun (j, other) ->
                                     if j <> i && isArray other && root other = Some aname then
                                         Some (j, fname, i, aname)
                                     else None)
                             | _ -> None)
                 | _ -> None)
            | _ -> None
        match mutClash with
        | Some (i, fname, declPos, got) ->
            atArg i
            Error (MutArgNotPassable (fname, declPos + 1, got))
        | None ->
        match mutAliasClash with
        | Some (j, fname, i, aname) ->
            atArg j
            Error (MutArgAliased (fname, i + 1, j + 1, aname))
        | None ->
        match irrepsClash, rankClash, unitClash, argRankClash with
        | Some (i, pi, ai), _, _, _ ->
            atArg i
            // O(3) member gets a named message; pg-vs-pg or a cross-member
            // pair gets the family-level twin naming the discipline instead.
            (match pi.Tag, ai.Tag with
             | Some (IrrepsTag _), Some (IrrepsTag _) ->
                 Error (IrrepsIdxArgMismatch (i + 1, ppIndexType pi, ppIndexType ai))
             | _ ->
                 Error (BlockSpecArgMismatch (i + 1, ppIndexType pi, ppIndexType ai)))
        | None, Some (i, slot, pi, ai), _, _ ->
            atArg i
            Error (IndexRankMismatch ($"argument {i + 1}, index slot {slot}",
                                      ppIndexType pi, max 1 pi.Rank,
                                      ppIndexType ai, max 1 ai.Rank))
        | None, None, Some unitErr, _ ->
            Error unitErr
        | None, None, None, Some (i, pr, ar, pTy, aTy) ->
            atArg i
            Error (ArgRankMismatch (i + 1, pr, ar,
                                    ppIRType (env.Subst.Resolve pTy),
                                    ppIRType (env.Subst.Resolve aTy)))
        | None, None, None, None ->
            // The FIFTH check: the ABSTRACT-PARAMETER conflict. Ahead of the
            // element-class one because it is the only check here that can
            // see an OPEN parameter at all -- every other check on this
            // ladder stands down on an unresolved `T^k`, which is precisely
            // how these calls used to reach g++ unjudged. See
            // firstAbstractVarConflict.
            let calleeDesc =
                match tFunc.Kind with
                | TExprVar (name, _, _) -> $"'{name}'"
                | _ -> "this function"
            match firstAbstractVarConflict env.Subst paramTys (tArgs |> List.map (_.Type)) with
            | Some (firstPos, conflictPos, firstTy, conflictTy) ->
                atArg conflictPos
                Error (Other (abstractVarConflictMessage env.Subst calleeDesc
                                                         firstPos conflictPos firstTy conflictTy))
            | None ->
            // The SIXTH check, last because every one above it names the
            // defect more precisely: THE CALL JUDGMENT proper -- instantiate
            // the signature, unify the copy against each argument
            // (`argPairClash`, with the call's licensed coercions). This is
            // what refuses a dense array into a `SymIdx` parameter, `Nat<Lon>`
            // into `Nat<Lat>`, `Float64` into `Int64`, struct `Q` into `P`, a
            // tuple into a scalar, `Array<Int64>` into `Array<Float64>` --
            // every shape the hand-added checks above never modelled.
            //
            // Same two stand-downs as every positional check (see
            // `firstArgRankClash`): a variadic `Poly` pack, and
            // under-application. Over-application judges the prefix this
            // arrow consumes.
            //
            // WHAT is instantiated: for a DECLARED function, the variables its
            // declaration minted (FuncSigVarRange) -- what HM quantifies. A
            // lambda, a function-typed parameter or a curried head quantifies
            // nothing: its open variables belong to its environment, so the
            // judgment compares only what is already concrete there (binding
            // them would pin a lambda to its first call's types while its
            // emitted body kept the zonk default).
            // A `__`-named callee is SYNTHESIZED by an elaborator (math, ppl,
            // grad) whose calls are the elaborator's contract, not the user's
            // -- e.g. the eigh gate-off path hands a complex matrix to a
            // helper spelled over Float64 and relies on C++ templating. Same
            // `__` exemption as mutClash.
            let synthesizedCallee =
                match appRootAndOffset tFunc with
                | Some (fname, _) -> fname.StartsWith "__"
                | None -> false
            let judged : (int * TypeError) option * IRType option * IRType list =
                if isVariadic || tArgs.Length < paramTys.Length || synthesizedCallee then (None, None, [])
                else
                    let quantified, closedDecl = calleeQuantifier env tFunc
                    let copied, copyIds, copyMap = instantiateOpenVarsWithMap env.Subst quantified (paramTys @ [retTy])
                    let pCopies = List.truncate paramTys.Length copied
                    let retCopy = List.last copied
                    // A FUNCTION ARGUMENT'S OPEN PARAMETER MEETS ITS DECLARED
                    // SLOT -- `let g = lambda(v) -> v(0) * 2.0; ap(g, xs)` with
                    // `ap(f: (Float64^1) -> Float64, ..)`. A lambda literal
                    // written AT the call is checked against the slot before its
                    // body is typed (ExprApp seeds it); a `let`-bound one was
                    // typed at its binding, and a parameter its body left open
                    // is the caller's to bind: the HM application rule, as for
                    // an open argument (below), one level in. Without it the
                    // judgment compared only what was concrete, the open
                    // parameter kept its zonk default (or its applications the
                    // function reading), and g++ was handed a lambda of the
                    // wrong type (BL9002). The lambda is one body, so this use
                    // decides for every later one. Only a plain monomorphic
                    // variable is bound (never a declaration's signature
                    // variable, a literal's, or a subscript's), units stripped
                    // as everywhere in the judgment (they are unitClash's). A
                    // generic slot (`(T^1) -> T`) binds it to this call's copy,
                    // which the other arguments then decide -- so a seeded
                    // literal's own copy is linked to the judgment's too.
                    //
                    // A slot that is (or may become) an ARRAY or a FUNCTION binds
                    // it only when the body used it as nothing but an
                    // application head (lambdaParamOnlyApplied): any other use
                    // (`lambda(v) -> v * 2.0`) was typed as a single value while
                    // it was open, so a declared array slot is refused at the
                    // argument -- annotate the parameter -- and an argument that
                    // is not a recognizable lambda is left as before.
                    let openSlotRefusal : (int * TypeError) option ref = ref None
                    let openSlotVars =
                        appArgPairs pCopies (tArgs |> List.map (_.Type))
                        |> List.collect (fun (i, pTy, aTy) ->
                            match env.Subst.Resolve pTy, env.Subst.Resolve aTy with
                            | FuncElem (pSlots, _), FuncElem (aSlots, _) when pSlots.Length = aSlots.Length ->
                                let li = argLambdaInfo env (List.item i tArgs)
                                List.zip pSlots aSlots |> List.indexed |> List.choose (fun (j, (ps, a)) ->
                                    match env.Subst.Resolve a with
                                    | IRTInfer aid when not (env.Subst.IsPolymorphicId aid)
                                                       && (env.Subst.GetLiteralDefault aid).IsNone
                                                       && not (env.Subst.IsIndexDefault aid) ->
                                        let target = stripUnitsDeep env.Subst (env.Subst.Resolve ps)
                                        // The lambda parameter this slot is, if the
                                        // argument is a lambda we can see: is it
                                        // used only as an application head?
                                        let onlyApplied =
                                            li |> Option.bind (fun li ->
                                                List.tryItem j li.Params
                                                |> Option.filter (fun p -> env.Subst.Resolve p.Type = IRTInfer aid)
                                                |> Option.map (fun p -> lambdaParamOnlyApplied p.VarId li.Body))
                                        // Some true: an array or a function; Some
                                        // false: open, so it may become one.
                                        let shaped =
                                            match target with
                                            | ArrayElem _ | IRTArrow _ -> Some true
                                            | IRTInfer t when (env.Subst.GetArityConstraint t |> Option.exists (fun k -> k >= 1))
                                                              || (env.Subst.GetRankLowerBound t |> Option.exists (fun k -> k >= 1)) -> Some true
                                            | IRTInfer _ -> Some false
                                            | _ -> None
                                        let bind () =
                                            if target = IRTInfer aid then None
                                            else
                                                // Its recorded applications, taken
                                                // while it is still the head's root.
                                                let apps =
                                                    env.PendingArrowApps |> List.ofSeq |> List.filter (fun app ->
                                                        match env.Subst.Resolve app.Head.Type with
                                                        | IRTInfer h -> h = aid
                                                        | _ -> false)
                                                match unify env.Subst (IRTInfer aid) target with
                                                | Ok () -> Some (i, apps)
                                                | Error _ -> None
                                        match shaped, onlyApplied with
                                        | None, _ -> bind ()
                                        | Some _, Some true -> bind ()
                                        | Some true, Some false ->
                                            if openSlotRefusal.Value.IsNone then
                                                let desc =
                                                    match tFunc.Kind with
                                                    | TExprVar (name, _, _) -> $"'{name}'"
                                                    | _ -> "this function"
                                                // A generic slot reads as its rank,
                                                // not as a bare type variable.
                                                let slotDesc =
                                                    match target with
                                                    | IRTInfer t ->
                                                        let k =
                                                            env.Subst.GetArityConstraint t
                                                            |> Option.orElse (env.Subst.GetRankLowerBound t)
                                                            |> Option.defaultValue 1
                                                        $"an array of rank {k}"
                                                    | t -> ppIRType t
                                                openSlotRefusal.Value <-
                                                    Some (i, ArgTypeMismatch (i + 1, desc,
                                                            $"a function whose parameter {j + 1} is {slotDesc} (the passed lambda's "
                                                            + "unannotated parameter is used as a single value in its body, which was typed "
                                                            + "before this call: annotate the parameter)",
                                                            ppIRType (env.Subst.Resolve aTy)))
                                            None
                                        | _ -> None
                                    | _ -> None)
                            | _ -> [])
                    let clash =
                        openSlotRefusal.Value |> Option.orElse (
                        appArgPairs pCopies (tArgs |> List.map (_.Type))
                        |> List.tryPick (fun (i, pTy, aTy) ->
                            let arg = List.item i tArgs
                            argPairClash env.Subst copyIds true (argLiteralShape arg) pTy aTy
                            |> Option.map (fun e -> (i, e)))
                        // The callee's GENERIC CASTS, judged against the
                        // instance the arguments just built.
                        |> Option.orElse (genericObligationClash env tFunc copyMap |> Option.map (fun e -> (0, e)))
                        // The callee's ARROW PARAMETERS, decided by the
                        // arguments the judgment just bound their copies to:
                        // an array or a function (or still open), each
                        // application's result typed against it.
                        |> Option.orElse (arrowObligationClash env tFunc paramTys tArgs quantified copyMap)
                        // ...and those of a generic function passed AS an
                        // argument, at the parameter type it meets.
                        |> Option.orElse (
                            tArgs |> List.indexed |> List.tryPick (fun (i, a) ->
                                if i >= pCopies.Length then None
                                else genericValueObligationClash env a (List.item i pCopies)
                                     |> Option.map (fun e -> (i, e))))
                        // ...and the applications a bound open parameter's body
                        // recorded on it while it was open
                        // (TypeEnv.ArrowApplication), typed now that the
                        // arguments have decided it, refused at this argument.
                        // One still open (a generic slot no argument reached)
                        // stays pending for the body end.
                        |> Option.orElse (
                            openSlotVars |> List.tryPick (fun (i, recorded) ->
                                let apps =
                                    recorded |> List.filter (fun app ->
                                        match IR.stripUnits (env.Subst.Resolve app.Head.Type) with
                                        | IRTInfer _ -> false
                                        | _ -> env.PendingArrowApps.Exists(fun x -> obj.ReferenceEquals(x, app)))
                                for app in apps do
                                    env.PendingArrowApps.RemoveAll(fun x -> obj.ReferenceEquals(x, app)) |> ignore
                                apps |> List.tryPick (fun app ->
                                    match dischargeArrowApp env app with
                                    | Error e -> Some (i, e)
                                    | Ok () -> None))))
                    propagateGenericObligations env tFunc paramTys tArgs
                    // Extents are NOT part of type identity, and a `T^1` shared
                    // by two parameters admits arrays of different lengths
                    // (the monomorph reads a `T^k` parameter's extents at run
                    // time; loops/150 passes a 9- and a 4-cell array). But a
                    // copy taught by the FIRST argument carries that
                    // argument's LITERAL extents, so a later argument of a
                    // different length means the instance cannot say what
                    // length the result has.
                    let extentsDisagree =
                        appArgPairs pCopies (tArgs |> List.map (_.Type))
                        |> List.exists (fun (_, pTy, aTy) -> (staticExtentClash env.Subst pTy aTy).IsSome)
                    // An ARROW PARAMETER of the callee still OPEN at this call
                    // (the caller's own parameter: arrowObligationClash
                    // re-recorded the application on it, so the caller's
                    // parameter becomes an arrow parameter in turn). The
                    // call's result is the INSTANCE's -- the copy, which the
                    // re-recorded application's result is part of and which
                    // the caller's settlement makes the caller's generic
                    // variable. The declared return would tie the CALLER's
                    // result to the callee's own signature variable, which
                    // every instance of the caller then binds (a second
                    // element type was refused, or left unresolved).
                    let arrowOpen =
                        match calleeDeclId env tFunc with
                        | Some fid ->
                            (match env.FuncArrowObligations.TryGetValue fid with
                             | true, obs ->
                                 obs |> List.exists (fun ob ->
                                     match env.Subst.Resolve (IRTInfer ob.Head) with
                                     | IRTInfer h ->
                                         (match Map.tryFind h copyMap with
                                          | Some c ->
                                              (match env.Subst.Resolve (IRTInfer c) with
                                               | IRTInfer _ -> true
                                               | _ -> false)
                                          | None -> false)
                                     | _ -> false)
                             | _ -> false)
                        | None -> false
                    // The call's RESULT is the instantiated return when the
                    // arguments determined it completely (no copy left open);
                    // otherwise the declared return, as before -- a return the
                    // arguments do not reach (a deduced return still being
                    // inferred inside its own body) must not be decoupled.
                    // Nor is a RECURSIVE call's result instantiated: inside the
                    // body the declaration is still being inferred, and a
                    // concrete result there would flow back into its own
                    // signature through the body/return unify.
                    // (An arrow parameter still open: the instance's, above.)
                    let instRet =
                        match clash with
                        | Some _ -> None
                        | None when closedDecl && arrowOpen -> Some (env.Subst.Resolve retCopy)
                        | None when not closedDecl && not (Set.isEmpty copyIds) -> None
                        // ...so the result keeps the declared (symbolic)
                        // return rather than claim the first argument's
                        // length over a value that may have another's.
                        | None when extentsDisagree -> None
                        | None ->
                            // AN OPEN ARGUMENT MEETS ITS PARAMETER'S COPY -- the
                            // HM application rule, for the one kind of open
                            // argument that is the caller's own to bind: a plain
                            // monomorphic variable (a lambda parameter, the eta
                            // wrapper `lambda(__k) -> mean(__k)` a named-function
                            // kernel becomes). Without it the result fell back to
                            // the DECLARED return, and whatever later resolved the
                            // caller's variable (the kernel's row) bound the
                            // DECLARATION through it: `method_for(B) <@> mean` over
                            // Float64 rows pinned `mean` to Float64, and every later
                            // `mean(int_row)` was refused (or, called earlier, died
                            // in g++ against the one emitted `double mean(..)`).
                            // A declaration's own signature variable (polymorphic)
                            // and a literal's width-flexible variable are never
                            // bound here: the first is IR monomorphization's, the
                            // second adapts to its parameter on its own terms.
                            // Not inside a named function DECLARATION body: there an open
                            // argument is typically tied to the declaration's own
                            // signature (a pack element `head` of `a: Poly<T^1>`), and
                            // binding it to a non-generic copy collapsed the declaration to
                            // one instance (arity/024, /026, /031).
                            //
                            // Inside a declaration body the argument that IS the
                            // caller's to link is its OWN signature variable
                            // (polymorphic, reachable from its signature --
                            // TypeEnv.CurrentSignature): `function m2(x: T) -> T
                            // = idg(x)`. The HM rule: the COPY is bound to the
                            // caller's variable (never the other way), so the
                            // result is expressed in the caller's variables and
                            // is generalized with them. Only where the two
                            // already have the same SHAPE (variable against
                            // variable, arrays element-wise): an unshaped `T^k`
                            // is never shaped here, since shaping a parameter
                            // mid-body re-routes every array expression typed
                            // after it (a deferred binop became a zip pipeline
                            // the symmetry deduction cannot see through --
                            // ad-jvp-comb/085's BL4010 vanished -- and a Poly
                            // pack element lost its tail, arity/026, /058). That
                            // shape (`m2(row: T^1) = tot(row) / ..` with tot's
                            // parameter already an array) is specialized per
                            // instance by IR monomorphization instead
                            // (IRMono.specializeHMFunction's inner-call learning).
                            // Never another declaration's variable leaked in
                            // through an unlinked call's declared return, nor a
                            // lambda annotation's `T^k` minted in the body.
                            let isOwn (aid: int) =
                                env.Subst.IsPolymorphicId aid
                                && (env.CurrentSignature
                                    |> List.exists (fun t -> (freeInferVars env.Subst (env.Subst.Resolve t)).Contains aid))
                            let rec linkOwn (p: IRType) (a: IRType) : bool =
                                let p = stripUnitsDeep env.Subst (env.Subst.Resolve p)
                                let a = stripUnitsDeep env.Subst (env.Subst.Resolve a)
                                match p, a with
                                | IRTInfer pid, IRTInfer aid when copyIds.Contains pid && isOwn aid ->
                                    (match unify env.Subst (IRTInfer pid) (IRTInfer aid) with
                                     | Ok () -> true
                                     | Error _ -> false)
                                | ArrayElem pa, ArrayElem aa when pa.IndexTypes.Length = aa.IndexTypes.Length ->
                                    linkOwn pa.ElemType aa.ElemType
                                | IRTTuple ps, IRTTuple xs when ps.Length = xs.Length ->
                                    List.fold2 (fun acc x y -> linkOwn x y || acc) false ps xs
                                | _ -> false
                            let linked =
                                if Set.isEmpty copyIds then false
                                elif env.CurrentGenericObligations.IsSome then
                                    appArgPairs pCopies (tArgs |> List.map (_.Type))
                                    |> List.fold (fun acc (_, pTy, aTy) ->
                                        linkOwn pTy aTy || acc) false
                                else
                                    appArgPairs pCopies (tArgs |> List.map (_.Type))
                                    |> List.fold (fun acc (_, pTy, aTy) ->
                                        match env.Subst.Resolve aTy with
                                        | IRTInfer aid when not (env.Subst.IsPolymorphicId aid)
                                                           && (env.Subst.GetLiteralDefault aid).IsNone
                                                           && not (env.Subst.IsIndexDefault aid) ->
                                            (match unify env.Subst (IRTInfer aid) (stripUnitsDeep env.Subst pTy) with
                                             | Ok () -> true
                                             | Error _ -> acc)
                                        | _ -> acc) false
                            let r = env.Subst.Resolve retCopy
                            let openCopies = Set.intersect (freeInferVars env.Subst r) copyIds
                            if Set.isEmpty openCopies then Some r
                            else
                                // Linked: every copy the return still mentions is
                                // reachable from a parameter copy, i.e. from the
                                // arguments -- the caller's variables now carry it.
                                let paramFree =
                                    pCopies |> List.map (fun t -> freeInferVars env.Subst (env.Subst.Resolve t))
                                    |> Set.unionMany
                                if linked && Set.isSubset openCopies paramFree then Some r else None
                    (clash, instRet, pCopies)
            let (judgedClash, judgedRet, judgedCopies) = judged
            match judgedClash, coIterClash with
            // A co-iteration extent disagreement is the more specific story
            // when both fire (two arguments over different named axes walked
            // as one index space): report it, as before the judgment existed.
            // The NAME half does not outrank the judgment: a body whose
            // parameters share one signature variable is already told
            // "declared DayIdx, got HourIdx" there.
            | Some _, Some (i, err, true) ->
                atArg i
                Error err
            | Some (i, e), _ ->
                atArg i
                (match e with
                 | IndexRankMismatch (site, pTy, pr, aTy, ar) ->
                     Error (IndexRankMismatch ($"argument {i + 1}, {site}", pTy, pr, aTy, ar))
                 // ...and an arrow parameter's refusal (arrowObligationClash),
                 // which already names the argument and what it had to be.
                 | ExtentArgMismatch _ | InvalidCast _ | ArgTypeMismatch _ | ArgRankMismatch _ -> Error e
                 | _ ->
                     // An open declared parameter (`T^1`) reads as a
                     // variable id; show what THIS call's instance of it was
                     // taught by the earlier argument instead.
                     let pTy =
                         match env.Subst.Resolve (List.item i paramTys), e with
                         | IRTInfer _, _ when i < judgedCopies.Length -> env.Subst.Resolve (List.item i judgedCopies)
                         | declared, _ -> declared
                     let aTy = env.Subst.Resolve (List.item i tArgs).Type
                     // An index NAME is part of the type, and `ppIRType`
                     // spells it (`Lat`, not `Idx<3>`), so two axes that differ
                     // only by name never read "declared X but got X".
                     let pp1, pp2 = ppIRType pTy, ppIRType aTy
                     // A CONCRETE-ELEMENT caret parameter (`Float64^2`) is
                     // dense: its axes are the unnamed, unknown-extent plain
                     // axes TypeLower's TyAbstractArray arm mints. Handed a
                     // compact-storage argument, say so and name `T^k`
                     // rather than leave a bare "declared X but got Y".
                     let isAbstractDenseAxis (ix: IRIndexType) =
                         ix.IxKind = IxKPlain && ix.Tag.IsNone && ix.Symmetry = SymNone && ix.Rank = 1
                         && (match ix.Extent with IRParam ("?", _, _) -> true | _ -> false)
                     let isCompactAxis (ix: IRIndexType) =
                         ix.Rank > 1 || ix.Symmetry <> SymNone
                         || (match ix.IxKind with IxKCompound | IxKCompoundDynamic | IxKSparse -> true | _ -> false)
                     match IR.stripUnits pTy, IR.stripUnits aTy with
                     | ArrayElem pa, ArrayElem aa
                             when not pa.IndexTypes.IsEmpty
                                  && pa.IndexTypes |> List.forall isAbstractDenseAxis
                                  && aa.IndexTypes |> List.exists isCompactAxis ->
                         Error (DenseCaretCompactArg (i + 1, calleeDesc, pa.IndexTypes.Length, pp1, pp2))
                     | _ ->
                     Error (ArgTypeMismatch (i + 1, calleeDesc, pp1, pp2)))
            | None, _ ->
            // The SEVENTH check, after element class because a wrong-class
            // argument that is also the wrong length should be reported as
            // the class error. See `extentClash` above for why this one is a
            // memory error rather than a typing disagreement.
            match extentClash with
            | Some (i, d, pe, ae) ->
                atArg i
                Error (ExtentArgMismatch (i + 1, d + 1, pe, ae))
            | None ->
            // The EIGHTH check, after the param-vs-arg extent one because that
            // is the more local story: an argument disagreeing with its own
            // parameter's declared extent should be told about the parameter,
            // not about the OTHER argument it is co-iterated with. Blamed on
            // the second (shorter or longer) argument, since the walk takes its
            // bound from the first.
            match coIterClash with
            | Some (i, err, _) ->
                atArg i
                Error err
            | None ->
            // Rank propagation (the INFERENCE half of argRankClash's
            // CHECKING): impose the callee param's rank as a LOWER BOUND on
            // still-unresolved argument vars, so an unannotated caller param
            // learns the callee's rank demand instead of the typechecker
            // staying quiet and codegen emitting ill-typed C++.
            (let n = min paramTys.Length tArgs.Length
             List.zip (List.truncate n paramTys) (List.truncate n tArgs)
             |> List.iter (fun (pTy, arg) ->
                 let calleeRank =
                     match env.Subst.Resolve pTy with
                     | ArrayElem pa -> pa.IndexTypes.Length
                     | IRTInfer pid -> env.Subst.GetRankLowerBound(pid) |> Option.defaultValue 0
                     | _ -> 0
                 if calleeRank > 0 then
                     match env.Subst.Resolve arg.Type with
                     | IRTInfer aid -> env.Subst.AddRankLowerBound(aid, calleeRank)
                     | _ -> ()))
            // The DEDUCED return's unit, built from the arguments'. See
            // `unitStampedReturn`: the judgment unifies with units STRIPPED
            // (they are `unitClash`'s), so the unit a generic body derives is
            // applied from the callee's recorded transform, onto whichever
            // result the judgment chose.
            let retTy =
                unitStampedReturnOnto env
                    (calleeDeclId env tFunc)
                    tArgs retTy (judgedRet |> Option.defaultValue retTy)
            match coerceIndexArgs env paramTys tArgs with
            | Error e -> Error e
            | Ok tArgs ->
            if isVariadic then
                Ok (mkTyped (TExprApp (tFunc, tArgs)) retTy)
            elif tArgs.Length > paramTys.Length then
                // Curried over-application: this arrow consumes its declared
                // params; the remainder re-dispatches against the result
                // type (function curries on, array falls into indexing,
                // scalar is a plain arity error).
                let now, rest = List.splitAt paramTys.Length tArgs
                let head = mkTyped (TExprApp (tFunc, now)) retTy
                match env.Subst.Resolve retTy with
                | FuncElem _ | ArrayElem _ -> dispatchAppOrIndex env head rest
                | _ -> Error (ArityMismatch (paramTys.Length, tArgs.Length))
            elif tArgs.Length < paramTys.Length then
                // Not partial application: ExprApp eta-expands 0 < k < n
                // before dispatching, so this is `f()` on an n-ary function
                // or an under-applied struct field -- genuine arity errors.
                // "Too few" means fewer than the REQUIRED params: for a
                // defaults-carrying callee (whose fills the desugar already
                // handled for required <= k < total) report the required
                // count, not the full param count.
                let expectedMin =
                    match tFunc.Kind with
                    | TExprVar (name, _, _) ->
                        (match defaultsKeyOfName env name |> Option.map env.FuncDefaults.TryGetValue with
                         | Some (true, ps) ->
                             ps |> List.takeWhile (fun (_, _, d) -> Option.isNone d) |> List.length
                         | _ -> paramTys.Length)
                    | _ -> paramTys.Length
                Error (ArityMismatch (expectedMin, tArgs.Length))
            else
                // An ARRAY handed to an arrow parameter: the call is the call
                // of the declaration's ARRAY INSTANCE (see the section note
                // above arrowVariantHook), judged as an ordinary call of it.
                match arrowArrayArgs env tFunc tArgs true with
                | None -> Ok (mkTyped (TExprApp (tFunc, tArgs)) retTy)
                | Some (fid, arrays) ->
                    arrowVariantHook.Value env fid arrays
                    |> Result.bind (fun head -> dispatchAppOrIndex env { head with Span = tFunc.Span } tArgs)
                    |> Result.bind (fun t -> unify env.Subst t.Type retTy |> Result.map (fun () -> t))
    // SUBSCRIPTING A `T^k` PARAMETER the body has not yet given a shape --
    // `function first(m: T^1) -> T^0 = m(0)`. The caret is an exact rank
    // claim, but it lives in the substitution (an arity-k var), so the head
    // matched no array arm and fell to the catch-all below: `m(0)` was typed
    // as a CALL with a fresh result. The emitted C++ then called an array
    // (`m(0L)`, BL9002), and with a `T^0` return nothing tied that result to
    // the parameter's element, so it reached IR validation unresolved
    // (BL6001). An arity-k var (k >= 1) can bind to nothing but a rank-k
    // array (Unify's arity invariant), so this issues the same shape demand
    // every array intrinsic does (`extents(m)`, `reduce(m, ..)`) -- whose
    // element var stays polymorphic, so HM specialization still makes one
    // copy per element type -- and re-dispatches to the indexing arms.
    | IRTInfer vid when not (List.isEmpty tArgs)
                        && (env.Subst.GetArityConstraint vid |> Option.exists (fun k -> k >= 1)) ->
        let k = (env.Subst.GetArityConstraint vid).Value
        requireArrayArgMinRank env tFunc "subscript" k
        |> Result.bind (fun _ ->
            match env.Subst.Resolve tFunc.Type with
            | ArrayElem _ -> dispatchAppOrIndex env tFunc tArgs
            | t -> Error (InvalidApplication t))
    // APPLYING A STILL-OPEN VARIABLE -- `function first(c) = c(0) + 1.0`,
    // `let u = lambda(c) -> c(0) + 1.0`. Only arrays and functions take
    // arguments (formalism 4.3), and an array IS a function of its indices
    // (dimensional currying), so the application says the head is an ARROW --
    // not which kind. That is decided by what reaches the head, which the body
    // does not see: the argument a call passes, the row a kernel iterates.
    // So the application is RECORDED (TypeEnv.ArrowApplication) with a fresh
    // result, and typed once the head is known (dischargeArrowApp): at a call
    // that passes it (the FuncElem arm's arrow-parameter binding), at the end
    // of the declaration whose body typed it, and before zonk.
    //
    //   * A parameter of the DECLARATION being checked, applied to index-like
    //     arguments (an integer, an index value, `_`, an open var): it becomes
    //     an ARROW PARAMETER -- a generic variable, like `T`, whose kind each
    //     call decides (arrowObligationClash). A function keeps this
    //     declaration, whose application is a call; an array is handed to
    //     its ARRAY INSTANCE (arrowVariantHook), checked with the parameter
    //     typed as that array, where the application is a subscript. Its
    //     result is generic too, and so is an open parameter used as the
    //     argument (`pick(c, i) = c(i)`: the array's index type at an array
    //     instance, the function's slot type at a function instance).
    //   * The same parameter applied to an argument that cannot be an index (a
    //     Float, a tuple, an array, a function) can only be a function: it is
    //     the function from the argument types to a fresh result, at once.
    //   * Any other open variable -- a lambda's parameter, a kernel's row
    //     before buildApplyInfo binds it: recorded, discharged when bound.
    //
    // This used to fall to the catch-all below, which added no evidence: the
    // head zonked to Float64, so an array argument was refused ("expects a
    // scalar") and a scalar one died in g++ calling a double (BL9002).
    | IRTInfer vid when not (List.isEmpty tArgs)
                        && (env.Subst.GetArityConstraint vid).IsNone
                        && (isCurrentDeclParam env vid || not (env.Subst.IsPolymorphicId vid)) ->
        let ownParam = isCurrentDeclParam env vid
        let indexLike = tArgs |> List.forall (arrowArgIndexLike env)
        if ownParam && not indexLike then
            let arrow = mkFuncArrow (tArgs |> List.map (_.Type)) (env.Subst.Fresh())
            unify env.Subst tFunc.Type arrow
            |> Result.bind (fun () ->
                match env.Subst.Resolve tFunc.Type with
                | FuncElem _ -> dispatchAppOrIndex env tFunc tArgs
                | t -> Error (InvalidApplication t))
        else
            let r = env.Subst.Fresh()
            if ownParam then
                // Generic: kept open by zonk and instantiated per call.
                env.Subst.MarkPolymorphic vid
                (match r with IRTInfer rid -> env.Subst.MarkPolymorphic rid | _ -> ())
            if indexLike then
                for a in tArgs do
                    match env.Subst.Resolve a.Type with
                    | IRTInfer aid when ownParam && isCurrentDeclParam env aid ->
                        env.Subst.MarkPolymorphic aid
                    | IRTInfer aid when (env.Subst.GetLiteralDefault aid).IsNone
                                        && not (env.Subst.IsPolymorphicId aid) ->
                        // A subscript if nothing else pins it (formalism 3.10).
                        env.Subst.MarkIndexDefault aid
                    | _ -> ()
            env.PendingArrowApps.Add { Head = tFunc; Args = tArgs; Result = r; Span = tFunc.Span; Written = true }
            Ok (mkTyped (TExprApp (tFunc, tArgs)) r)
    | _ ->
        match nonCallableHead env.Subst tFunc with
        | Some t when not (List.isEmpty tArgs) -> Error (InvalidApplication t)
        | _ ->
        let retTy = env.Subst.Fresh()
        Ok (mkTyped (TExprApp (tFunc, tArgs)) retTy)

/// Type a recorded ARROW APPLICATION (TypeEnv.ArrowApplication) against what its
/// head has become. A head nothing ever reached is read as a FUNCTION of the
/// argument types ("`c` as a function is generally a safer bet"); an array or a
/// function head types the application exactly as if it had been known when it
/// was written -- the same dispatch, partial reads and call judgment included --
/// after the function reading's int-valued rule (arrowFuncSlotClash); and the
/// application's recorded result meets the real one. Anything else is the
/// refusal a known head would have met (InvalidApplication).
and internal dischargeArrowApp (env: TypeEnv) (app: ArrowApplication) : TypeResult<unit> =
    let argTys = app.Args |> List.map (_.Type)
    match IR.stripUnits (env.Subst.Resolve app.Head.Type) with
    | IRTInfer _ ->
        unify env.Subst app.Head.Type (mkFuncArrow argTys app.Result)
    | FuncElem (slots, _) when slots.Length = argTys.Length
                               && (arrowFuncSlotClash env.Subst slots argTys).IsSome ->
        let (j, slot) = (arrowFuncSlotClash env.Subst slots argTys).Value
        Error (ArgTypeMismatch (j + 1, "the applied function", ppIRType slot,
                                "an integer index (an applied parameter read with an integer index is an array, "
                                + "or a function OF AN INTEGER: a call never converts an index to another type)"))
    | ArrayElem _ ->
        // An ARRAY: the application IS a subscript (dimensional currying), and
        // the application the body holds is replaced by the subscript typed
        // here (TypeEnv.ArrowSubscripts) -- so zonk guards it, the post-zonk
        // subscript judgment checks it, and Lowering lowers it, exactly like a
        // subscript the program spelled against a known array.
        dispatchAppOrIndex env app.Head app.Args
        |> Result.bind (fun t ->
            // (Not when the typed form is still an application of this head
            // -- nothing to put in its place.)
            let selfApp =
                match t.Kind with
                | TExprApp (h, _) -> obj.ReferenceEquals(h, app.Head)
                | _ -> false
            if app.Written && not selfApp then env.ArrowSubscripts.[app.Head] <- t
            unify env.Subst t.Type app.Result)
    | IRTArrow _ ->
        dispatchAppOrIndex env app.Head app.Args
        |> Result.bind (fun t -> unify env.Subst t.Type app.Result)
    // A scalar, a tuple, a user index value: nothing to apply. (Said here
    // rather than left to dispatch, whose catch-all does not judge a
    // synthesized `__` head -- a kernel's eta parameter.)
    | IRTScalar _ | IRTNat _ | IRTTuple _ as t -> Error (InvalidApplication t)
    | IRTIdxTagged (_, IRefNamed tag) as t when not (tag.StartsWith "__") -> Error (InvalidApplication t)
    // Anything else (a halo window read `w(o)`, which Lowering owns) keeps
    // what the application was typed with while open.
    | _ -> Ok ()

/// Discharge every pending arrow application whose head is no longer open
/// (or, with `all`, every one -- an open head then takes the function reading),
/// each refusal located at its application. Returns the refusals.
let internal settleArrowApps (env: TypeEnv) (all: bool) : (Span * TypeError) list =
    let pending = env.PendingArrowApps |> List.ofSeq
    let ready =
        pending |> List.filter (fun app ->
            all || (match IR.stripUnits (env.Subst.Resolve app.Head.Type) with
                    | IRTInfer _ -> false
                    | _ -> true))
    for app in ready do
        env.PendingArrowApps.RemoveAll(fun a -> obj.ReferenceEquals(a, app)) |> ignore
    ready |> List.choose (fun app ->
        match dischargeArrowApp env app with
        | Ok () -> None
        | Error e -> Some (app.Span, e))

/// The END OF A DECLARATION's body: its pending applications whose head is
/// still one of its own parameters become that parameter's ARROW OBLIGATIONS
/// (TypeEnv.FuncArrowObligations) -- the parameter, the application's result,
/// and an open parameter used as its argument stay generic and are judged per
/// call; every pending application whose head is known now is typed. The
/// first refusal is returned, with its span stamped for locateError.
let internal settleDeclArrowApps (env: TypeEnv) (funcVarId: IRId) (paramTypes: IRType list) : TypeResult<unit> =
    let rootOf (t: IRType) =
        match env.Subst.Resolve t with
        | IRTInfer r -> Some r
        | _ -> None
    let paramRoots = paramTypes |> List.choose rootOf |> Set.ofList
    let mine =
        env.PendingArrowApps |> List.ofSeq |> List.filter (fun app ->
            match rootOf app.Head.Type with
            | Some h -> paramRoots.Contains h
            | None -> false)
    for app in mine do
        env.PendingArrowApps.RemoveAll(fun a -> obj.ReferenceEquals(a, app)) |> ignore
    let obligations =
        mine |> List.map (fun app ->
            let h = (rootOf app.Head.Type).Value
            env.Subst.MarkPolymorphic h
            rootOf app.Result |> Option.iter env.Subst.MarkPolymorphic
            for a in app.Args do
                match rootOf a.Type with
                | Some aid when paramRoots.Contains aid -> env.Subst.MarkPolymorphic aid
                | _ -> ()
            { Head = h; Args = app.Args |> List.map (_.Type); Result = app.Result })
    env.FuncArrowObligations.Remove funcVarId |> ignore
    if not obligations.IsEmpty then
        env.FuncArrowObligations.[funcVarId] <- obligations
    match settleArrowApps env false with
    | [] -> Ok ()
    | (span, e) :: _ ->
        setCurrentExprSpan span
        Error e

/// THE MODULE-END PASS of array instances (the section note above
/// arrowVariantHook). A call that handed an arrow parameter a value still OPEN
/// when it was judged -- a lambda's parameter, a kernel's eta parameter -- that
/// an ARRAY reached since (the lambda's call, the kernel's row) is re-targeted
/// now: the declaration's array instance, re-judged in the module's
/// environment, REPLACES the call before zonk (TypeEnv.ArrowRetargets). Walks
/// `roots` (and nothing a replacement already covers); returns the refusals,
/// each located at its call.
let internal retargetArrowCalls (env: TypeEnv) (roots: TypedExpr list) : (Span * TypeError) list =
    let errors = ResizeArray<Span * TypeError>()
    let rec walk (e: TypedExpr) =
        (match e.Kind with
         | TExprApp (f, args) when not (env.ArrowRetargets.ContainsKey e) ->
             (match arrowArrayArgs env f args false with
              | Some (fid, arrays) ->
                  // Located as the judgment at the call would be: its last
                  // argument is the last expression it typed.
                  resetCurrentStmtSpan ()
                  setCurrentExprSpan (match List.tryLast args with Some a -> a.Span | None -> e.Span)
                  let r =
                      arrowVariantHook.Value env fid arrays
                      |> Result.bind (fun head -> dispatchAppOrIndex env { head with Span = f.Span } args)
                      |> Result.bind (fun t -> unify env.Subst t.Type e.Type |> Result.map (fun () -> t))
                  match r with
                  | Ok t -> env.ArrowRetargets.[e] <- t
                  | Error err ->
                      let at = currentExprSpan ()
                      errors.Add((if at.StartLine > 0 then at else e.Span), err)
              | None -> ())
         | _ -> ())
        typedExprChildren e |> List.iter walk
    roots |> List.iter walk
    List.ofSeq errors

/// An UNANNOTATED function parameter that its body uses directly as a
/// SUBSCRIPT (`function g(i) = A(i)`) and that nothing else pinned is an
/// INDEX, and is pinned to one once the body is typed (formalism 3.10):
///   * every such use indexes the same NAMED index type I  ->  `Nat<I>`, so
///     a call `g((3 : Lon))` or a kernel use over `range<Lon>` meets the
///     nominal rule at the call judgment instead of reading A out of bounds;
///   * the uses index only untagged slots  ->  `Int64` (a `g(1.5)` call is
///     then the argument mismatch it is, not a `double` subscript in g++);
///   * the uses disagree on the tag  ->  `Int64` (the untagged-integer
///     advice at each use still applies).
/// Pinned AFTER the body so arithmetic on the parameter inside it was typed
/// while it was open, exactly as today. Only a plain open variable is pinned:
/// a `T^k`, a polymorphic signature variable, or one with a rank bound (it
/// is an array somewhere) is left alone.
let internal pinSubscriptParams (env: TypeEnv) (unannotated: bool list) (parms: TypedParam list) (body: TypedExpr) : unit =
    let candidates =
        List.zip unannotated parms
        |> List.choose (fun (un, p) ->
            match un, env.Subst.Resolve p.Type with
            | true, IRTInfer vid when (env.Subst.GetArityConstraint vid).IsNone
                                      && not (env.Subst.IsPolymorphicId vid)
                                      && (env.Subst.GetRankLowerBound vid).IsNone -> Some (p.VarId, vid)
            | _ -> None)
    if not candidates.IsEmpty then
        let ids = candidates |> List.map fst |> Set.ofList
        let uses = System.Collections.Generic.Dictionary<IRId, string option list>()
        let rec walk (e: TypedExpr) =
            (match e.Kind with
             | TExprIndex (arr, args, _) ->
                 (match env.Subst.Resolve arr.Type with
                  | ArrayElem at ->
                      subscriptSlotPairs at args
                      |> List.iter (fun (a, ix) ->
                          match a.Kind with
                          | TExprVar (_, id, _) when ids.Contains id ->
                              let tag =
                                  match ix.Tag with
                                  | Some t when not (t.StartsWith "__") && ix.IxKind = IxKPlain
                                                && not (slotIsEnumIdx env ix) -> Some t
                                  | _ -> None
                              let prev = match uses.TryGetValue id with | true, l -> l | _ -> []
                              uses.[id] <- tag :: prev
                          | _ -> ())
                  | _ -> ())
             | _ -> ())
            typedExprChildren e |> List.iter walk
        walk body
        for (pid, vid) in candidates do
            match uses.TryGetValue pid with
            | true, tags when not tags.IsEmpty ->
                // Still open? (a use may have pinned it meanwhile)
                (match env.Subst.Resolve (IRTInfer vid) with
                 | IRTInfer v2 ->
                     let named = tags |> List.choose id |> List.distinct
                     let pinned =
                         match named with
                         | [ t ] -> IRTIdxTagged (IRTScalar ETInt64, IRefNamed t)
                         | _ -> IRTScalar ETInt64
                     unify env.Subst (IRTInfer v2) pinned |> ignore
                 | _ -> ())
            | _ -> ()

/// Warn when a function-level `omp(p: n)` is being read as a licence for a
/// loop the function generates INTERNALLY over `p`.
///
/// THE RULE (owner, 2026-08-08): an `omp` clause licenses the EXTERNAL S-dims
/// an argument contributes -- the CALLER's co-iteration over that parameter
/// when the function is used in kernel position (`object_for(f) <@> (..)`,
/// `method_for(..) <@> f`, where the clause is surfaced onto the eta wrapper
/// from `FuncParallel`). It says nothing about loops the body itself builds:
/// those belong to the kernel of the apply that builds them, and are licensed
/// by a clause on THAT kernel.
///
/// The two are indistinguishable in the emitted C++ -- "asked for omp on the
/// inner loop and got serial" and "never asked" are byte-identical -- so the
/// misconception is silent, which is what this diagnostic is for. It fires
/// only on the actionable shape: the licensed parameter is an OPERAND of an
/// apply inside the body AND that apply's kernel carries no parallel clause of
/// its own. A body that already spells the inner licence is left alone (the
/// function-level clause is then doing its real, external job), as is a
/// parameter the body never iterates.
let checkOmpInternalLoop (env: TypeEnv) (paramNames: string list)
                         (whereClause: WhereClause option) (owner: string)
                         (body: TypedExpr) : unit =
    let licensed =
        match whereClause with
        | Some wc ->
            wc.Parallel |> List.collect (function
                | Omp o -> o.Vars |> List.map fst |> List.filter (fun v -> List.contains v paramNames)
                | _ -> [])
        | None -> []
    if not (List.isEmpty licensed) then
        // An apply whose kernel declares no parallel strategy: the only shape
        // where surfacing the misconception is actionable.
        // One-hop binding resolution, spelled locally: `resolveTypedExpr` lives
        // in the inference rec-chain far below this point, and this walk needs
        // nothing more than its `let`-bound-value hop (`let k = lambda ..` in
        // the kernel slot).
        let resolveHop (e: TypedExpr) =
            match e.Kind with
            | TExprVar (name, _, _) ->
                (match lookupVar name env with
                 | Some info -> info.TypedValue |> Option.defaultValue e
                 | None -> e)
            | _ -> e
        let unlicensedApplyOver (pname: string) (e: TypedExpr) =
            match e.Kind with
            | TExprApply info ->
                let namesParam (a: TypedExpr) =
                    match a.Kind with
                    | TExprVar (n, _, _) -> n = pname
                    | _ -> false
                let kernelAsksParallel =
                    match (resolveHop info.Kernel).Kind with
                    | TExprLambda li -> not (List.isEmpty li.Parallel)
                    | TExprVar (fn, _, _) ->
                        (match env.FuncParallel.TryGetValue fn with
                         | true, (_, s) -> not (List.isEmpty s)
                         | _ -> false)
                    | _ -> false
                if (info.Arrays |> List.exists namesParam) && not kernelAsksParallel
                then Some e.Span else None
            | _ -> None
        let rec findFirst (pname: string) (e: TypedExpr) : Span option =
            match unlicensedApplyOver pname e with
            | Some sp -> Some sp
            | None -> typedExprChildren e |> List.tryPick (findFirst pname)
        licensed |> List.iter (fun v ->
            match findFirst v body with
            | Some sp ->
                // BL4001 (constraint violation), the same class as the
                // names-no-parameter warning: a `where` conjunct that does not
                // mean what it was written to mean.
                emitWarning env "BL4001" sp
                    ($"omp({v}: ...) on {owner} licenses the CALLER's iteration over `{v}` (the S-dims an argument contributes when this is used as a kernel), not the loop over `{v}` built inside this body. That loop is licensed by a clause on its OWN kernel -- write `{v} <@> lambda(..) where omp(..) -> ..`. As written the inner loop is emitted SERIAL.")
            | None -> ())

/// The co-iterations this body performs over its own PARAMETERS: the agreement
/// obligation a zip carries when an operand is an abstract parameter and there
/// is nothing at the zip to compare. Recorded in
/// `TypeEnv.FuncCoIterObligations`, discharged on the call-site ladder by
/// `CoIterArgExtentMismatch` (two arguments disagree) or
/// `CoIterBodyExtentMismatch` (an argument disagrees with a literal the body
/// fixes).
///
/// `TypeLower.zipHeadClash` already refuses a mismatched zip -- but only
/// LITERAL vs LITERAL. A body over `T^1` parameters has no extents there, so
/// `zip(a, b)` was accepted unconditionally and the nest (bounded by operand 1,
/// every operand peeled at every level -- IRStorage's co-iteration arm) walked
/// the longer argument's extent over the shorter one's storage.
///
/// Two sources, unioned:
///   * a zip DIRECTLY over parameters. That is the `TExprMethodFor` node with
///     non-empty SharedIndexTypes, which every co-iterating surface form
///     resynthesizes to (`zip(a, b) <@> k`, `method_for(zip(a, b))`, `a + b`).
///   * a CALL to an ALREADY-obligated function passing this body's own
///     parameters into its obligated positions, so the obligation travels up a
///     forwarding chain (`outer(x, y) = addup(x, y)` inherits addup's). One
///     forward pass suffices: a body sees only names bound before it, and
///     mutual recursion is rejected (BL2001).
///
/// Each entry is the parameter positions walked plus the literal leading
/// extents and NAMED leading records of the co-iteration's other operands:
/// all of those must end up agreeing (`coIterVerdict`). An entry
/// needs at least one PARAMETER -- nothing else defers to the call site -- and
/// a second operand to disagree with, so `zip(a, a)` records nothing (it agrees
/// with itself). Both rules make this under-report rather than over-report: a
/// missed obligation is the status quo, an invented one is a false refusal.
let coIterObligations (env: TypeEnv) (paramIds: IRId list)
                      (body: TypedExpr) : CoIterObligation list =
    let posOf (e: TypedExpr) =
        match e.Kind with
        | TExprVar (_, vid, _) -> List.tryFindIndex ((=) vid) paramIds
        | _ -> None
    let resolvedArray (t: IRType) =
        match env.Subst.Resolve t with
        | ArrayElem aa -> Some aa
        | _ -> None
    let isArray (e: TypedExpr) = (resolvedArray e.Type).IsSome
    // The operators an ELEMENTWISE binop re-synthesizes as a zip over when
    // both operands are arrays (the elementwise arm of inferBinOp).
    let isZipOp (op: BinOp) =
        match op with
        | OpAdd | OpSub | OpMul | OpDiv | OpMod | OpCaret
        | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe | OpAnd | OpOr -> true
        | _ -> false
    // The obligation is "the walk takes its bound from OPERAND 1, so a shorter
    // later operand is read past its end". That is true of a zip, where
    // `zipSharedRecords` returns operand 1's OWN head record -- and false of
    // `for (A, B) in range<I>`, which shares the RANGE's records and is bounded
    // by the range: a 6-array passed there is walked 3 wide and merely has its
    // tail ignored, which is a different question (and memory-safe). Both
    // shapes build the same node, so tell them apart by whether the shared head
    // IS operand 1's head; claiming an out-of-bounds read for the range form
    // would be a refusal whose stated reason is untrue.
    let boundByFirstOperand (shared: IRIndexType list) (types: IRArrayType list) =
        match shared, types with
        | s :: _, at0 :: _ ->
            (match at0.IndexTypes with
             | h0 :: _ -> h0.Id = s.Id
             | [] -> false)
        | _ -> false
    // The body's lets, by binder id, so a co-iterated operand named through
    // one (`let c = a - ma; mean(c * d)`) still traces to its parameter. A
    // binder that is ever ASSIGNED is left out: its value at the zip is not
    // the one it was bound to. (`IsMutable` cannot say this: a block `let` is
    // reassignable in its own scope, so every one of them carries it.)
    let lets = System.Collections.Generic.Dictionary<IRId, TypedExpr>()
    let assigned = System.Collections.Generic.HashSet<IRId>()
    let rec collect (e: TypedExpr) =
        (match e.Kind with
         | TExprLet (_, vid, v, _) -> lets.[vid] <- v
         | TExprAssign ({ Kind = TExprVar (_, vid, _) }, _) -> assigned.Add vid |> ignore
         | TExprBlock (stmts, _) ->
             stmts |> List.iter (fun s ->
                 match s with
                 | TStmtLet b when List.isEmpty b.SubBindings -> lets.[b.VarId] <- b.Value
                 | TStmtAssign ({ Kind = TExprVar (_, vid, _) }, _) -> assigned.Add vid |> ignore
                 | _ -> ())
         | _ -> ())
        typedExprChildren e |> List.iter collect
    collect body
    // The parameters whose LEADING AXIS this operand's leading axis is. A
    // parameter is its own; an elementwise map keeps its operand's -- `a - ma`
    // re-synthesizes as `compute(method_for(a) <@> lambda(__bx) -> __bx - ma)`,
    // a covariance body zips two of THOSE, and reading bare parameters only
    // let `mean((a - ma) * (b - mb))` co-iterate a DayIdx fiber with an
    // HourIdx one, at a call and as a kernel alike. A zip's result keeps the
    // axis all its operands share. Anything else (a reduction, a slice, a
    // reshape, a call) answers [] -- under-reporting is the status quo, an
    // invented origin would be a false refusal.
    let rec originsOf (fuel: int) (e: TypedExpr) : int list =
        if fuel <= 0 then [] else
        let go = originsOf (fuel - 1)
        match e.Kind with
        | TExprVar (_, vid, _) ->
            (match posOf e with
             | Some k -> [k]
             | None ->
                 match lets.TryGetValue vid with
                 | true, v when not (assigned.Contains vid) -> go v
                 | _ -> [])
        | TExprCompute x | TExprArrayNegate x | TExprArrayConjugate x -> go x
        | TExprLet (_, _, _, b) -> go b
        | TExprBlock (_, Some fin) -> go fin
        | TExprMethodFor mfi ->
            loopOrigins go mfi.Arrays mfi.ArrayTypes mfi.SharedIndexTypes mfi.SDimsPerArray
        | TExprApply ai when not ai.IsComposeApply ->
            loopOrigins go ai.Arrays ai.ArrayTypes ai.SharedIndexTypes ai.SDimsPerArray
        | TExprBinOp (Elementwise, op, l, r) when isZipOp op && isArray e ->
            (if isArray l then go l else []) @ (if isArray r then go r else [])
        | _ -> []
    and loopOrigins (go: TypedExpr -> int list) (arrays: TypedExpr list) (types: IRArrayType list)
                    (shared: IRIndexType list) (sDims: int list) : int list =
        match arrays, sDims with
        // A single-operand map iterating at least its leading axis: the
        // result's leading axis IS the operand's (S-dims come first).
        | [a], [s] when s >= 1 && List.isEmpty shared -> go a
        | _ :: _ :: _, _ when boundByFirstOperand shared types -> arrays |> List.collect go
        | _ -> []
    let originsOfOperand = originsOf 16
    // One co-iteration, split into the PARAMETER positions it walks and the
    // literal leading extents / leading records of its other operands. A
    // parameter zipped against a CONCRETE array is the same hole with one side
    // already known -- `function wsum(a: T^1) = reduce(zip(a, weights3) <@>
    // (*), (+))` walked `a`'s extent over `weights3` and summed three doubles
    // past its end -- so the body's own literals travel with the obligation,
    // and so do its NAMED records (a `DayIdx` array the body fixes refuses an
    // `HourIdx` argument). Only named records ride: an unnamed one co-iterates
    // with anything (`indexNamesCoIterable`).
    let obligationOf (operands: (TypedExpr * IRArrayType option) list) =
        let withOrigins = operands |> List.map (fun (o, t) -> (originsOfOperand o, t))
        let ps = withOrigins |> List.collect fst |> List.distinct |> List.sort
        let others = withOrigins |> List.filter (fst >> List.isEmpty) |> List.choose snd
        let heads = others |> List.choose (fun t -> List.tryHead t.IndexTypes)
        let lits = heads |> List.choose (fun ix -> tryEvalIntIR ix.Extent) |> List.distinct
        let named = heads |> List.filter (fun ix -> ix.Tag.IsSome) |> List.distinctBy (fun ix -> ix.Tag, ix.IxKind)
        // Needs a PARAMETER (nothing else defers) and a second walked operand
        // for it to disagree with. `zip(a, a)` -- or `zip(a, a - mean(a))` --
        // gives one position and nothing else, and agrees with itself.
        if List.isEmpty ps || List.length ps + List.length lits + List.length named < 2 then None
        else Some { Positions = ps; BodyExtents = lits; BodyHeads = named }
    let atNode (e: TypedExpr) =
        match e.Kind with
        | TExprMethodFor mfi when not (List.isEmpty mfi.SharedIndexTypes)
                                  && mfi.Arrays.Length >= 2
                                  && mfi.Arrays.Length = mfi.ArrayTypes.Length
                                  && boundByFirstOperand mfi.SharedIndexTypes mfi.ArrayTypes ->
            obligationOf (List.zip mfi.Arrays (mfi.ArrayTypes |> List.map Some)) |> Option.toList
        // An elementwise binop over two ARRAYS that is still a binop node: a
        // kernel body inferred while its parameters were open (`lambda(a, b)
        // -> reduce(a * b, (+))`) keeps the node and is lowered as the zip it
        // is once the application binds them. Judged on the RESOLVED operand
        // types, which is why this table is also derived after a kernel's
        // parameters meet their operands (buildApplyInfo).
        | TExprBinOp (Elementwise, op, l, r) when isZipOp op && isArray l && isArray r ->
            obligationOf [ (l, resolvedArray l.Type); (r, resolvedArray r.Type) ] |> Option.toList
        // Forwarding. Only the DIRECT `f(a, b)` head is read: a curried head
        // would need the declared position rebased by the earlier groups'
        // width (mutClash's appRootAndOffset), and guessing it wrong would
        // blame the wrong argument. Missing it just leaves the status quo.
        | TExprApp (f, args) ->
            (match f.Kind with
             | TExprVar _ ->
                 (match env.FuncCoIterObligations.TryGetValue (calleeDeclId env f |> Option.defaultValue -1) with
                  | true, obs ->
                      obs |> List.choose (fun ob ->
                          let mapped = ob.Positions |> List.choose (fun k -> List.tryItem k args)
                          // An argument that traces to one of OUR parameters
                          // keeps deferring; one whose type is already
                          // concrete DISCHARGES into a literal / named record
                          // every other operand of that co-iteration must
                          // match.
                          let withOrigins = mapped |> List.map (fun a -> (originsOfOperand a, a))
                          let ps' = withOrigins |> List.collect fst |> List.distinct |> List.sort
                          let concreteHeads =
                              withOrigins
                              |> List.filter (fst >> List.isEmpty)
                              |> List.choose (fun (_, a) -> resolvedArray a.Type |> Option.bind (fun t -> List.tryHead t.IndexTypes))
                          let allLits =
                              (ob.BodyExtents @ (concreteHeads |> List.choose (fun ix -> tryEvalIntIR ix.Extent)))
                              |> List.distinct
                          let allNamed =
                              (ob.BodyHeads @ (concreteHeads |> List.filter (fun ix -> ix.Tag.IsSome)))
                              |> List.distinctBy (fun ix -> ix.Tag, ix.IxKind)
                          if List.isEmpty ps' || List.length ps' + List.length allLits + List.length allNamed < 2 then None
                          else Some { Positions = ps'; BodyExtents = allLits; BodyHeads = allNamed })
                  | _ -> [])
             | _ -> [])
        | _ -> []
    let rec walk (e: TypedExpr) =
        atNode e @ (typedExprChildren e |> List.collect walk)
    walk body |> List.distinct

/// True if any node in the typed subtree still has an UNRESOLVED type (an
/// inference variable, possibly under a unit-annotation wrapper).
///
/// NOT the right predicate for unit provisionality -- use
/// `typedExprHasProvisionalUnits` below. This one stops at a let-bound var
/// (resolved type, no children) and so cannot see an unresolved param inside
/// that var's defining expression, which is how the SAME bug was reported four
/// separate times before the chasing version replaced it at both deferral
/// sites. Currently unused; kept as the plain structural query it is.
let rec typedExprHasUnresolvedType (env: TypeEnv) (expr: TypedExpr) : bool =
    let rec tyUnresolved (t: IRType) =
        match env.Subst.Resolve t with
        | IRTInfer _ -> true
        | IRTUnitAnnotated (inner, _) | IRTIdxTagged (inner, _) -> tyUnresolved inner
        | _ -> false
    tyUnresolved expr.Type
    || (typedExprChildren expr |> List.exists (typedExprHasUnresolvedType env))

/// THE deferral trigger, shared by every "is this unit signature still
/// PROVISIONAL?" site. `typedExprHasUnresolvedType` answers a strictly weaker
/// question -- "does some NODE here still have an inference-variable type" --
/// and misses the shape that produced three separate bug reports: a value
/// reached through a LET, whose cached type is perfectly resolved and
/// nonetheless provisional.
///
///     let w = two_pi * fq        // fq unresolved => w types BARE Float64
///     cos(w * t_zero)            // judged 1 * day = day, rejects; but once
///                                // fq binds, w is 1/day and it cancels
///
/// `w`'s node is resolved and `TExprVar` has no children, so the walk stops
/// there and never sees `fq`. Chase let-bound vars into their defining
/// expression (`VarInfo.TypedValue`, matched on VarId so shadowing cannot
/// redirect the chase); a visited set plus a fuel cap keep a self-referential
/// binding from looping.
///
/// Deliberately still a walk over the SUBTREE, not a blanket "we are inside a
/// lambda": `cos(tz)` on a resolved dimensioned capture depends on nothing
/// provisional and must keep rejecting where it is written, because a lambda
/// that is never `<@>`-applied never runs a second pass to catch it.
let typedExprHasProvisionalUnits (env: TypeEnv) (expr: TypedExpr) : bool =
    // A let-bound var's defining expression is chased at most once per walk
    // (re-chased only with MORE fuel than it was explored with, so the fuel
    // cut answers exactly as before). Without the memo every PATH to a
    // binding re-walked its whole definition, and a chain of lifted bindings
    // (`X2 = X1 * 2.0 * M1 / (M1 + M2)`, `X3 = X2 * ...`) reaches the root
    // along exponentially many paths: an apply's operands are children twice
    // (the apply's arrays and its loop former's). Let definitions form a DAG
    // -- a body sees only names bound before it -- so no path revisits its own
    // var and the per-path `seen` set the walk used to carry is subsumed.
    let explored = System.Collections.Generic.Dictionary<IRId, int>()
    let rec go (fuel: int) (e: TypedExpr) : bool =
        if fuel <= 0 then false
        else
            let rec tyUnresolved (t: IRType) =
                match env.Subst.Resolve t with
                | IRTInfer _ -> true
                | IRTUnitAnnotated (inner, _) | IRTIdxTagged (inner, _) -> tyUnresolved inner
                | _ -> false
            tyUnresolved e.Type
            || (match e.Kind with
                | TExprVar (name, varId, _) ->
                    (match explored.TryGetValue varId with
                     | true, f when f >= fuel - 1 -> false
                     | _ ->
                         explored.[varId] <- fuel - 1
                         match lookupVar name env with
                         | Some info when info.VarId = varId ->
                             (match info.TypedValue with
                              | Some v -> go (fuel - 1) v
                              | None -> false)
                         | _ -> false)
                | _ -> false)
            || (typedExprChildren e |> List.exists (go (fuel - 1)))
    go 64 expr

/// True if the typed expression contains an unconsumed wildcard hole anywhere
/// in its subtree. A wildcard is legitimate only as a compound-index coordinate,
/// where dispatchAppOrIndex consumes it into a residual node before it reaches a
/// value-forming boundary. Any TExprWildcard still present here has escaped into
/// a value (bound to a name, returned, nested in a non-consuming call) and is an
/// error. Local check: called at value-forming boundaries, not threaded through
/// the AST.
let rec internal exprContainsWildcard (expr: TypedExpr) : bool =
    match expr.Kind with
    | TExprWildcard -> true
    | _ -> typedExprChildren expr |> List.exists exprContainsWildcard

/// Re-run the tag-check at every TExprIndex site reachable from `expr`,
/// after buildApplyInfo's kernel-parameter unification pins previously-open
/// inference variables to nominally-tagged types (the eager check in
/// dispatchAppOrIndex saw them as IRTInfer and let them through). Without
/// this, indexing through an iteration-tagged kernel parameter -- e.g.
/// `lambda(r) -> by_country(r)` where `r` iterates `Array<RegionIdx like
/// StationIdx>` but `by_country` expects CountryIdx -- would silently
/// typecheck. Walks structurally, short-circuiting on the first error.
let rec internal revalidateBodyTagChecks (env: TypeEnv) (expr: TypedExpr) : TypeResult<unit> =
    // Recurse into children first, short-circuiting on error.
    let childRes =
        typedExprChildren expr
        |> List.fold (fun acc child ->
            acc |> Result.bind (fun () -> revalidateBodyTagChecks env child))
            (Ok ())
    childRes |> Result.bind (fun () ->
        match expr.Kind with
        | TExprIndex (arr, args, _) ->
            match env.Subst.Resolve arr.Type with
            | ArrayElem at when args.Length <= at.IndexTypes.Length ->
                checkArrayIndexTags env arr at args
            | _ -> Ok ()
        | _ -> Ok ())

/// Scalar math intrinsics -- the canonical list lives in Grad.fs (which also
/// carries the derivative rules); StaticEval.evalBuiltin mirrors the same
/// names for static contexts.
let isMathIntrinsic (name: string) : bool = Blade.Grad.isMathIntrinsic name

/// Whitelist subset permitted on complex operands (has a std::complex overload).
let isComplexMathIntrinsic (name: string) : bool = Blade.Grad.isComplexMathIntrinsic name

/// Every intrinsic reachable as a PLAIN CALL: the Grad-listed scalar
/// intrinsics plus the ones with their own inferExpr arms (abs, which
/// preserves its operand's numeric type, and the complex accessors
/// real/imag/arg). All are arity-1 -- which is what lets
/// etaExpandFunctionKernel wrap one in kernel position without a declared
/// signature to read the arity from.
let isUnaryIntrinsic (name: string) : bool =
    isMathIntrinsic name || name = "abs" || name = "real" || name = "imag" || name = "arg"

/// BINARY plain-call intrinsics (atan2, log_base). Kept out of
/// `isUnaryIntrinsic` on purpose: that predicate is what tells
/// `etaExpandFunctionKernel` the arity is 1, so a binary name listed there
/// would eta-expand to a one-parameter lambda and then fail arity inside its
/// own body. The canonical list lives in Grad.fs beside the adjoint rules.
let isBinaryIntrinsic (name: string) : bool = Blade.Grad.isBinaryMathIntrinsic name

/// Conservative effect summary of a TYPED body (Blade.Effects; plan-fortran-
/// killer-2.md section 3 step 3). Every node contributes its OWN effect and
/// the join of its children's, over `typedExprChildren` (exhaustive over the
/// typed grammar, so no node's subtree is skipped):
///
///   - a call's head, peeled through a curried spine, is a declared function
///     (FuncEffects by binder id; the function being summarized reads as pure
///     for its own recursive calls -- assume-guarantee, sound for a join
///     lattice), a directly applied lambda (its body), or anything else --
///     a lambda-valued variable, a higher-order parameter, a qualified name
///     -- which is Unknown. Intrinsic calls never reach here as TExprApp: the
///     checker rewrites them to TExprUnaryOp / TExprBinOp / TExprFma.
///   - an apply combinator's kernel is called per cell, so it contributes
///     like a call head (its lambda body is also a child).
///   - assignment statements and expressions: Mutates. Display nodes:
///     EmitsOutput. A provider read: ReadsExternal.
///   - MayFail: indexing (BL8006), reduce (BL8003), solve/eigh (BL8007),
///     match (BL8002), constraint checks and guards (BL8001), lgamma/digamma
///     (BL8008). Honest rather than useful: nearly every array body may
///     fail, which is why REPEATABLE admits it and MOVABLE does not.
let effectsOfBody (env: TypeEnv) (selfId: IRId option) (body: TypedExpr) : Blade.Effects.EffectSummary =
    let rec stmtMutates (s: TypedStmt) : bool =
        match s with
        | TStmtAssign _ -> true
        | TStmtForIn (_, _, _, _, inner) -> inner |> List.exists stmtMutates
        | TStmtLet _ | TStmtExpr _ -> false
    let rec calleeSummary (head: TypedExpr) : Blade.Effects.EffectSummary =
        match head.Kind with
        | TExprApp (f, _) -> calleeSummary f
        | TExprVar (_, vid, _) when selfId = Some vid -> Blade.Effects.noEffects
        | TExprVar (_, vid, _) when env.DeclaredFuncIds.Contains vid ->
            (match env.FuncEffects.TryGetValue vid with
             | true, s -> s
             | _ -> Blade.Effects.unknown)
        | TExprLambda info -> go info.Body
        | _ -> Blade.Effects.unknown
    and go (e: TypedExpr) : Blade.Effects.EffectSummary =
        let own =
            match e.Kind with
            | TExprApp (f, _) -> calleeSummary f
            | TExprApply info -> calleeSummary info.Kernel
            | TExprObjectFor info -> calleeSummary info.Kernel
            | TExprAssign _ -> Blade.Effects.mutates
            | TExprBlock (stmts, _) when stmts |> List.exists stmtMutates -> Blade.Effects.mutates
            | TExprDisplayEmit _ | TExprDisplayJson _ | TExprDisplayNum _ | TExprDisplayStr _ ->
                Blade.Effects.emitsOutput
            | TExprRead _ -> Blade.Effects.readsExternal
            | TExprUnaryOp (OpMath ("lgamma" | "digamma"), _) -> Blade.Effects.mayFail
            // Integer `/` and `%` by zero abort (IR.irNodeMayAbort's twin). A
            // provably floating-point division cannot; anything not yet known
            // to be floating answers yes.
            | TExprBinOp (_, (OpDiv | OpMod), _, _) ->
                let rec floating (t: IRType) =
                    match IR.stripUnits (env.Subst.Resolve t) with
                    | IRTScalar (ETFloat32 | ETFloat64 | ETComplex64 | ETComplex128) -> true
                    | ArrayElem at -> floating at.ElemType
                    | _ -> false
                if floating e.Type then Blade.Effects.noEffects else Blade.Effects.mayFail
            // `^` with a non-literal exponent (the integer-power domain
            // checks, BL8013/BL8014) and a float -> integer cast (NaN /
            // out-of-range) abort too -- IR.irNodeMayAbort's other twins.
            | TExprBinOp (_, OpCaret, _, r) when not r.Kind.IsTExprLit -> Blade.Effects.mayFail
            | TExprUnaryOp (OpCast name, x) when
                    (match Blade.Types.castTargetOf name with
                     | Some (ETInt32 | ETInt64) ->
                         let rec floating (t: IRType) =
                             match IR.stripUnits (env.Subst.Resolve t) with
                             | IRTScalar (ETFloat32 | ETFloat64) -> true
                             | ArrayElem at -> floating at.ElemType
                             | _ -> false
                         floating x.Type
                     | _ -> false) -> Blade.Effects.mayFail
            | TExprIndex _ | TExprTupleIndex _ | TExprReduce _ | TExprSolve _ | TExprEigh _
            | TExprLu _ | TExprLuSolve _
            | TExprMatch _ | TExprConstraintCheck _ | TExprGuard _ -> Blade.Effects.mayFail
            | _ -> Blade.Effects.noEffects
        typedExprChildren e |> List.fold (fun acc c -> Blade.Effects.join acc (go c)) own
    go body

/// Rejection message shared by the two orientations of the same unimplemented
/// shape: a `zip(...)` sitting beside other arrays in ONE operand pack
/// (`object_for(k) <@> (A, zip(B, C))`, `method_for(A, zip(B, C)) <@> k`).
/// A zip should contribute ONE axis and deliver k values to the kernel; the
/// object_for path instead flattened it into the pack (a silent extra outer
/// axis) and the method_for path carried it to codegen unmaterialized.
/// Single-operand zip application is the supported co-iteration form.
let zipInMultiArrayPackMsg =
    "zip cannot appear as one operand of a multi-array loop; co-iterating a zip inside an outer loop is not yet supported -- hoist the zip to its own <@>, or pass the zipped arrays as separate operands"

/// A variable is a provider-module alias when it is bound opaque to a
/// registered provider's module name (`import netcdf as nc` binds
/// nc : IRTNamed "netcdf"). Returns the registry name for dispatch.
let providerAliasName (env: TypeEnv) (alias: string) : string option =
    match lookupVar alias env with
    | Some vi ->
        (match env.Subst.Resolve(vi.Type) with
         | IRTNamed pn when (Blade.ProviderRegistry.tryFind pn).IsSome -> Some pn
         | _ -> None)
    | None -> None

/// Stamp a source expression's span onto a node built by calling a former
/// builder (inferObjectFor / inferMethodFor) DIRECTLY -- those bypass
/// inferExpr, so they miss its span back-fill and would return noSpan.
/// Consumers that need a location: the stage-3/4 BL4010 pin suggestion (which
/// otherwise has nothing to anchor on) and the editor's call-site walk.
/// Existing spans are never overwritten.
let stampSynthSpan (src: Expr) (te: TypedExpr) : TypedExpr =
    if te.Span.StartLine = 0 && src.Span.StartLine > 0 then { te with Span = src.Span } else te

/// May an elementwise kernel INHERIT the compact storage class of the array it
/// maps over? Each compact class stores one triangle and reconstructs the
/// other through a mirror involution applied to the VALUE, so a map keeps
/// the class exactly when it COMMUTES with that involution:
///
///   SymSymmetric      mirror = identity      -> any map preserves it
///   SymAntisymmetric  mirror = negation      -> needs a sign-ODD kernel
///   SymHermitian      mirror = conjugation   -> needs a conj-commuting kernel
///
/// The only UNARY seam asking this (the parity engine handles binary
/// pair-swap deduction elsewhere); `deduceOutputType`'s rank-0 elementwise
/// arm and the `<$>` functor arm copy the input's Symmetry through for ANY
/// kernel otherwise, which for an antisymmetric input is a silent
/// miscompile: the result stores a strict upper triangle and every mirrored
/// read applies NegateOnSwap, so `C <@> (v * v)` reads out(1,0) as -out(0,1)
/// when the truth is +out(0,1).
///
/// No third answer ("demote to symmetric/dense"): ITERATION is fixed by the
/// INPUT record, so an antisymmetric input drives the STRICT simplex and
/// writes C(n,r) cells -- neither the diagonal a symmetric map would need
/// nor the full square a dense one would is a shape that nest can fill.
/// Refusing is the only sound answer; an unprovable kernel is an error with
/// a decompact-first fix, not a downgrade.
///
/// `argPos` is the kernel parameter receiving the compact array's cells;
/// `signParities`/`conjCommutes` are its per-parameter summaries. `wreathLevels`
/// is the class's level list when cls = SymWreath: a wreath's mirror is the
/// PRODUCT of its per-level characters, so the answer depends on levels, not
/// the class name alone -- the one place in this file where that is true.
let compactClassInheritError
        (cls: SymmetryClass)
        (wreathLevels: (int * bool) list)
        (argPos: int)
        (paramName: string)
        (signParities: Blade.Deduce.SignParity list)
        (conjCommutes: bool list) : TypeError option =
    match cls with
    | SymNone | SymSymmetric -> None
    // An all-'+' wreath's mirror is the identity, exactly like SymSymmetric, so
    // any map preserves it. A single '-' level anywhere makes the mirror carry a
    // negation, and then the SAME sign-ODD certificate the depth-1
    // antisymmetric case needs applies -- an inner '-' level must not silently
    // skip BL4015 just because the outer class is spelled differently.
    // (A wreath output is currently refused at the storage boundary before
    // this can fire; the gate is here so it is RIGHT if/when that refusal
    // lifts.)
    | SymWreath when wreathLevels |> List.forall snd -> None
    | SymWreath ->
        (match List.tryItem argPos signParities with
         | Some Blade.Deduce.SOdd -> None
         | Some Blade.Deduce.SEven ->
             Some (AntisymMapNotOdd (paramName, "provably sign-EVEN (f(-x) = f(x))"))
         | _ -> Some (AntisymMapNotOdd (paramName, "of UNKNOWN sign parity")))
    | SymAntisymmetric ->
        (match List.tryItem argPos signParities with
         | Some Blade.Deduce.SOdd -> None
         | Some Blade.Deduce.SEven ->
             Some (AntisymMapNotOdd (paramName, "provably sign-EVEN (f(-x) = f(x))"))
         | _ -> Some (AntisymMapNotOdd (paramName, "of UNKNOWN sign parity")))
    | SymHermitian ->
        (match List.tryItem argPos conjCommutes with
         | Some true -> None
         | _ -> Some (HermitianMapNotReal paramName))

/// The QUANTITY nominal a surface type annotation names, when it names one:
/// a bare quantity (`levels`) or a tagged base (`Float<speed>`,
/// `Int64<levels>`, `String<title>`, ...). Structural units (Nominal = None)
/// and non-unit names answer None.
let internal surfaceTypeQuantity (env: TypeEnv) (ty: TypeExpr) : string option =
    let quantityOf name =
        match Map.tryFind name env.Units with
        | Some (s: UnitSig) -> s.Nominal
        | None -> None
    match ty with
    | TyNamed (q, []) -> quantityOf q
    | TyNamed (_, [TyNamed (q, [])]) -> quantityOf q
    | _ -> None

/// The QUANTITY nominal a call ARGUMENT carries, judged at surface level:
/// an ascription (`20 : levels`, `v : Float<speed>`) or a variable whose
/// (resolved) type already carries the nominal. Compound expressions and
/// call results are NOT probed -- they route positionally unless ascribed.
let internal argQuantityTag (env: TypeEnv) (a: Expr) : string option =
    match a.Kind with
    | ExprKind.ExprTyped (_, ty) -> surfaceTypeQuantity env ty
    | ExprKind.ExprVar n ->
        (match lookupVar n env with
         | Some vi ->
             (match IR.getUnits (env.Subst.Resolve vi.Type) with
              | Some u -> u.Nominal
              | None -> None)
         | None -> None)
    | _ -> None

/// Well-formedness check for unit annotations in TYPE position -- the
/// annotation twin of registerUnit's rules, raising the SAME two codes:
///   BL3011: a quantity name inside a COMPOUND unit expression
///     (`Float<speed/second>`, `Float<speed^2>`). The nominal layer is
///     exactly one level deep, so only a LONE quantity name
///     (`Float<speed>`) may appear in a type argument.
///   BL3015: a name that resolves to no unit at all (`Float<meter/secnd>`).
///     Only a numeric LITERAL may appear in a unit expression undeclared;
///     an identifier must already name something. Without this the
///     annotation degrades to the BARE type, so the value silently carries
///     no unit and every later check on it passes.
/// Both are checked only where the parser already committed to unit syntax
/// (`TyUnitExpr` -- see isUnitExprArg: a lone name and `name^INT` are NOT
/// claimed, since they collide with `Float<speed>` and `T^2`). Descends
/// aggregate component positions the way boundedAggregateError does.
/// Lowering itself stays TOTAL (it degrades to the bare base type), so the
/// annotation CONSUMERS -- ascriptions, let annotations, function signatures
/// -- call this to surface the error.
/// Scalar bases whose type argument is a UNIT (or index-tag) slot -- mirrors
/// the dispatch in lowerTypeExpr. These constructors take no GENERIC
/// parameter, which is what makes the slot checkable: the only things that
/// can inhabit it are a unit, a quantity, an index-type or enum tag
/// (`Nat<LatIdx>`), and the tag wildcard `_`. A name that is none of those is
/// a misspelling, so here -- and ONLY here -- `name` and `name^INT` can be
/// rejected without colliding with `Float<speed>` and `T^2`.
/// (`Char`/`Void` take no argument at all, so they are absent.)
let internal unitSlotBases =
    Set.ofList
        [ "Int"; "Int32"; "Int64"; "Float"; "Float64"; "Double"; "Float32"
          "Complex64"; "Complex128"; "Bool"; "Nat"; "String" ]

/// The refusal text for `Row^k` where `Row` is already an array type.
let internal caretOnArrayAliasMessage (name: string) (k: int) : string =
    $"`{name}^{k}`: `{name}` is already an array type, and a caret does not build arrays of arrays -- spell the shape you mean with `Array<Elem like I, ...>` (or `Elem^r` over the element type, with the combined rank)"

let rec internal unitAnnoError (env: TypeEnv) (ty: TypeExpr) : TypeError option =
    let quantityIn name =
        match Map.tryFind name env.Units with
        | Some (s: UnitSig) -> s.Nominal
        | None -> None
    let rec inUnitExpr (ue: UnitExpr) : TypeError option =
        match ue with
        | UnitNamed n ->
            match Map.tryFind n env.Units with
            | Some (s: UnitSig) ->
                s.Nominal |> Option.map (fun q -> QuantityTerminal (q, unitAnnoContext))
            | None when unitScaleConstants.ContainsKey n -> None
            | None ->
                Some (UnknownUnitName (n, unitAnnoContext, unitSpellingCandidates env.Units n))
        | UnitMul (a, b) | UnitDiv (a, b) ->
            inUnitExpr a |> Option.orElseWith (fun () -> inUnitExpr b)
        | UnitPow (a, _) -> inUnitExpr a
        // A magnitude names nothing, so neither rule can fire on it.
        | UnitOne | UnitScaleLit _ -> None
    match ty with
    | TyUnitExpr ue -> inUnitExpr ue
    // A caret on a DECLARED type (TypeLower.isDeclaredCaretHead) builds that
    // type's array. Two spellings have no such array, and lowering (which has
    // no error channel) stands down on both, so they are refused here, where
    // every annotation consumer looks: a head that is ITSELF an array type
    // (the caret does not nest arrays), and a non-literal rank (a variable
    // rank belongs to a type variable, as for `Float64^r`).
    | TyVar (name, Some k) when k >= 1 && isDeclaredArrayAlias env name ->
        Some (CaretHeadSpelling (caretOnArrayAliasMessage name k))
    | TyAbstractArray (TyVar (name, _), rankE, _) when isDeclaredCaretHead env name ->
        (match rankE.Kind with
         | ExprKind.ExprLit (LitInt k) when k >= 1L && isDeclaredArrayAlias env name ->
             Some (CaretHeadSpelling (caretOnArrayAliasMessage name (int k)))
         | ExprKind.ExprLit (LitInt _) -> None
         | _ ->
             Some (CaretHeadSpelling
                     $"`{name}^...` needs an integer literal rank: `{name}` names a declared type, so the caret builds an array of it (`{name}^1` is a rank-1 array of {name}); a variable rank belongs to a type variable -- write `T^r` for any element"))
    | TyVar (name, Some _) ->
        // `Float<speed^2>` parses as a rank-marked type var; a quantity name
        // there is the power spelling of the same terminality violation.
        // BL3015 is NOT raised from this arm -- reached from an arbitrary
        // position, an unresolvable name here is the `T^2` type-variable
        // spelling. It is raised from the unitSlotBases arm below, which
        // knows the slot cannot hold a type variable.
        quantityIn name |> Option.map (fun q -> QuantityTerminal (q, unitAnnoContext))
    | TyNamed (ctor, args) when unitSlotBases.Contains ctor ->
        args |> List.tryPick (fun arg ->
            match arg with
            | (TyNamed (argName, []) | TyVar (argName, Some _))
                    when not (Map.containsKey argName env.Units)
                         && (lookupTypeDef argName env).IsNone ->
                // `Float<secnd>`, `Float<secnd^2>`, `Int32<secnd>`: the name
                // resolves to no unit, quantity, index type, or enum, and the
                // slot admits nothing else. Any typedef at all suppresses
                // this -- the conservative direction. `Nat<_>` parses as
                // TyWildcard and never matches here.
                Some (UnknownUnitName
                        (argName, unitAnnoContext, unitSpellingCandidates env.Units argName))
            | _ -> unitAnnoError env arg)
    // CARET-FREE `T<u>` (owner ruling, 2026-08-09): `T<u>` IS `T<u>^0`, so a
    // misspelled unit under it must be the same BL3015 the caret arm below
    // raises -- otherwise the equivalence holds for programs that type and
    // breaks for programs that do not, which is the harder half to notice.
    // Same head test as that arm, routed through the same `unitSlotBases`
    // logic, so all three spellings (`Float<secnd>`, `T<secnd>`, `T<secnd>^0`)
    // answer identically.
    | TyNamed (ctor, (_ :: _ as args))
            when not (isConcreteTypeBaseName ctor)
                 && (lookupTypeDef ctor env).IsNone ->
        unitAnnoError env (TyNamed ("Float", args))
    | TyNamed (_, args) -> args |> List.tryPick (unitAnnoError env)
    // `T<u>^k` (array-expression plan bug #8): under a caret the head is a
    // type VARIABLE, so its argument slot admits nothing but a unit -- the
    // same situation as a `unitSlotBases` base, with the variable standing in
    // for it. Routed through that arm verbatim (substituting a base name that
    // owns a unit slot) so the two spellings cannot drift: an unresolvable
    // name is BL3015 here just as in `Float<secnd>`. A concrete head keeps
    // its ordinary reading and recurses normally.
    | TyAbstractArray ((TyNamed (ctor, (_ :: _ as args))), _, _)
            when not (isConcreteTypeBaseName ctor)
                 && (lookupTypeDef ctor env).IsNone ->
        unitAnnoError env (TyNamed ("Float", args))
    | TyAbstractArray (elem, _, _) -> unitAnnoError env elem
    | TyBounded (b, _, _) -> unitAnnoError env b
    | TyArray (elem, _) -> unitAnnoError env elem
    | TyDist (_, elem, _) -> unitAnnoError env elem
    | TyTuple ts -> ts |> List.tryPick (unitAnnoError env)
    | TyFunc (args, ret) ->
        (args |> List.tryPick (unitAnnoError env))
        |> Option.orElseWith (fun () -> unitAnnoError env ret)
    | TyConstrained (inner, _) -> unitAnnoError env inner
    | TyPoly inner -> unitAnnoError env inner
    | _ -> None

/// Only the caret-on-a-declared-type refusals of `unitAnnoError`, for the
/// annotation sites that do not run the unit checks (lambda parameters).
let rec internal caretHeadError (env: TypeEnv) (ty: TypeExpr) : TypeError option =
    match ty with
    | TyVar _ | TyAbstractArray (TyVar _, _, _) ->
        (match unitAnnoError env ty with
         | Some (CaretHeadSpelling _ as e) -> Some e
         | _ -> None)
    | TyAbstractArray (e, _, _) | TyBounded (e, _, _) | TyArray (e, _) | TyDist (_, e, _)
    | TyConstrained (e, _) | TyPoly e -> caretHeadError env e
    | TyTuple ts -> ts |> List.tryPick (caretHeadError env)
    | TyFunc (args, ret) ->
        (args |> List.tryPick (caretHeadError env))
        |> Option.orElseWith (fun () -> caretHeadError env ret)
    | TyNamed (_, args) -> args |> List.tryPick (caretHeadError env)
    | _ -> None

/// DEFAULT PARAMETER FILL (surface call-site desugar). A call omitting
/// trailing DEFAULTED parameters rewrites into an ordinary full-arity call
/// BEFORE any typing happens, so codegen and the interpreter only ever see
/// plain calls and lets -- absence resolves statically, nothing option-like
/// exists at runtime.
///
///   f(a0, a1)  with  f(x, y, s: T = d)   ==>   f(a0, a1, (d : T))
///
/// A fill is ascribed with the parameter's declared annotation, which makes
/// a defaulted QUANTITY slot an INTRODUCTION position: the ascription mints
/// the slot's nominal onto the default (with the dims check ascription
/// carries), so no BL3010 fires for the API author's own default while
/// caller-supplied bare arguments are still rejected.
///
/// When a fill references required params (the `auto_levels(z)` pattern),
/// the call wraps in a block that binds each REFERENCED supplied argument
/// exactly once -- first to a fresh temp (so every argument expression still
/// evaluates in CALLER scope; a later arg may use a name that collides with
/// an earlier param), then to the parameter's own name, which is the name
/// the default expression references:
///
///   { let __dflt7_0 = a0; let z = __dflt7_0; f(z, (auto_levels(z) : T)) }
///
/// Defaults evaluate at call entry, left-to-right, seeing the caller's
/// evaluated required-argument values.
///
/// FACTORY BY-NOMINAL ROUTING. When the callee's defaulted trailing group
/// contains QUANTITY-typed slots (Nominal = Some, distinct per slot --
/// BL3013), a trailing argument carrying a quantity nominal (an ascription
/// `20 : levels` / `v : Float<speed>`, or a variable whose type already
/// carries the nominal) routes TO THAT SLOT regardless of its position among
/// the trailing args. Untagged trailing args keep positional fill and must
/// PRECEDE every tagged one (an untagged straggler after a tag is ambiguous
/// -- BL3014); a tag matching no slot and a slot supplied twice are BL3014
/// too. A callee with no quantity slots keeps pure positional semantics --
/// quantity-typed VALUES may legitimately flow into structural slots.
///
/// Returns None when this call needs no rewriting (unknown callee, no
/// defaults, full-arity in declared order, fewer than required args, or a
/// `_` placeholder -- partial application owns those); Some (Error e) when
/// routing itself is invalid.
/// The binding identity of every free name a parameter-default list reads in
/// the scope it is DECLARED in: free name -> VarId. Recorded next to
/// `FuncDefaults` (TypeEnv.FuncDefaultCaptures) so `tryFillDefaultArgs` can
/// refuse a splice whose call site binds one of those names differently.
/// The callable's own parameters are excluded -- a default may read the
/// REQUIRED ones, and those are bound by the splice itself -- and a name with
/// no binding here (an intrinsic, a function registered outside `Variables`)
/// records nothing, so it is never checked.
let internal defaultCaptureIdentities (env: TypeEnv) (parms: (string * Expr option) list) : Map<string, IRId> =
    let own = parms |> List.map fst |> Set.ofList
    parms
    |> List.choose snd
    |> List.map (collectFreeVars own)
    |> List.fold Set.union Set.empty
    |> Set.toList
    |> List.choose (fun n ->
        match Map.tryFind n env.Variables with
        | Some vi -> Some (n, vi.VarId)
        | None -> None)
    |> Map.ofList

let internal tryFillDefaultArgs (env: TypeEnv) (callSpan: Span) (func: Expr) (args: Expr list) : TypeResult<Expr> option =
    let calleeName =
        match func.Kind with
        | ExprKind.ExprVar n -> n
        | ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, fname) -> alias + "." + fname
        | _ -> "lambda"
    // (lookup key, param infos). FuncDefaultCaptures is consulted under the
    // same key, so the two tables cannot disagree about which declaration a
    // call resolved to.
    // Keyed by the binding the head RESOLVES to (defaultsKeyOfName), so the
    // defaults consulted are those of the callable this call actually names.
    let byName (name: string) =
        match defaultsKeyOfName env name with
        | Some key ->
            (match env.FuncDefaults.TryGetValue key with
             | true, ps -> Some (Some key, ps)
             | _ -> None)
        | None -> None
    let paramInfos =
        match func.Kind with
        | ExprKind.ExprVar name -> byName name
        // Module-QUALIFIED callee (`a.f(...)`): the import binds `alias.name`
        // to the declaration's own id, so it resolves to that module's `f`
        // even when another imported module also declares one. The bare
        // field name stays as the fallback for callees with no module export
        // behind them (a provider alias, a let-bound lambda).
        | ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, fname) ->
            byName (alias + "." + fname) |> Option.orElseWith (fun () -> byName fname)
        // Immediately-applied lambda literal: its params are right here.
        | ExprKind.ExprLambda (parms, _, _) when parms |> List.exists (_.Default.IsSome) ->
            Some (None, parms |> List.map (fun p -> (p.Name, p.Type, p.Default)))
        | _ -> None
    match paramInfos with
    | None -> None
    | Some (defaultsKey, ps) ->
        let total = ps.Length
        let required = ps |> List.takeWhile (fun (_, _, d) -> Option.isNone d) |> List.length
        let k = args.Length
        let hasWildcard =
            args |> List.exists _.Kind.IsExprWildcard
        if hasWildcard || k < required then None
        else
        let trailingSlots = ps |> List.skip required   // all defaulted (trailing rule)
        let slotQs =
            trailingSlots |> List.map (fun (_, tyOpt, _) -> tyOpt |> Option.bind (surfaceTypeQuantity env))
        let trailingArgs = if k > required then args |> List.skip required else []
        let argTags =
            // Tags engage only when the callee HAS quantity slots (see doc).
            if slotQs |> List.exists Option.isSome
            then trailingArgs |> List.map (argQuantityTag env)
            else trailingArgs |> List.map (fun _ -> None)
        let anyTagged = argTags |> List.exists Option.isSome
        // TAGS THAT ONLY CONFIRM THE POSITIONAL READING ARE A NO-OP, and saying
        // so is what makes THIS FUNCTION IDEMPOTENT. A partial call is rewritten
        // to full arity and re-inferred, which lands back here -- and the
        // rewrite appends the missing defaults UNTAGGED, because a slot with no
        // quantity (`n_segments: Float64 = 10.0`) has no tag to carry. On the
        // second pass those appended arguments read as untagged stragglers
        // after the user's tagged one and the straggler check below refused a
        // call it had itself just built: `lswosa((s, t), freqs, (0.0 : time))`
        // -- three arguments -- was reported as "argument 4 has no quantity tag"
        // (examples/lswosa.blade could not be called at all).
        //
        // The test is exact rather than a re-entry flag: routing has nothing to
        // do when every TAGGED trailing argument already sits in the slot
        // carrying its quantity and the call is complete. Reordering calls
        // (`plot(1.0, 3.0: cmap, 2.0: levels)`, functions/049) disagree with
        // their positions and still route; the genuine ambiguity
        // (`plot(1.0, 3.0: cmap, 2.0)`, functions/053) still refuses. Full
        // arity is required because a SHORT call still needs this function to
        // fill its defaults.
        let tagsConfirmPositions =
            argTags
            |> List.indexed
            |> List.forall (fun (j, t) ->
                match t with
                | Some q -> j < slotQs.Length && slotQs.[j] = Some q
                | None -> true)
        if k >= total && (not anyTagged || tagsConfirmPositions) then None
        else
        // Per trailing slot, in DECLARED order: Some suppliedArg | None (use default).
        let assembled : TypeResult<Expr option list> =
            if not anyTagged then
                Ok [ for j in 0 .. trailingSlots.Length - 1 ->
                        if j < trailingArgs.Length then Some (List.item j trailingArgs) else None ]
            else
                let firstTag = argTags |> List.findIndex Option.isSome
                let straggler =
                    argTags
                    |> List.mapi (fun i t -> (i, t))
                    |> List.tryPick (fun (i, t) -> if i > firstTag && t.IsNone then Some i else None)
                match straggler with
                | Some i -> Error (FactoryAmbiguousMix (calleeName, required + i + 1))
                | None ->
                    let nPos = firstTag   // untagged prefix fills leading trailing slots positionally
                    if nPos > trailingSlots.Length then
                        Error (ArityMismatch (total, k))
                    else
                    let assignments = System.Collections.Generic.Dictionary<int, Expr>()
                    for j in 0 .. nPos - 1 do assignments.[j] <- List.item j trailingArgs
                    let mutable err = None
                    trailingArgs
                    |> List.skip nPos
                    |> List.iteri (fun i a ->
                        if err.IsNone then
                            let tag = (List.item (nPos + i) argTags).Value
                            match slotQs |> List.tryFindIndex (fun q -> q = Some tag) with
                            | None ->
                                err <- Some (FactoryUnknownTag (calleeName, tag, slotQs |> List.choose id))
                            | Some j ->
                                if assignments.ContainsKey j then
                                    let (slotName, _, _) = List.item j trailingSlots
                                    err <- Some (FactoryDupFill (calleeName, tag, slotName))
                                else
                                    assignments.[j] <- a)
                    match err with
                    | Some e -> Error e
                    | None ->
                        Ok [ for j in 0 .. trailingSlots.Length - 1 ->
                                match assignments.TryGetValue j with
                                | true, a -> Some a
                                | _ -> None ]
        match assembled with
        | Error e -> Some (Error e)
        | Ok slotAssign ->
        // Nothing to do (every slot supplied, already in declared order):
        // return None so the re-entry after a rewrite terminates.
        let isIdentity =
            slotAssign.Length = trailingArgs.Length
            && List.forall2
                (fun s a -> match s with Some x -> System.Object.ReferenceEquals(x, a) | None -> false)
                slotAssign trailingArgs
        if isIdentity then None
        else
        let mkFill (slot: string * TypeExpr option * Expr option) =
            let (_, tyOpt, dfltOpt) = slot
            let d = Option.get dfltOpt   // every trailing slot has a default (trailing rule)
            match tyOpt with
            | Some ty -> { d with Kind = ExprKind.ExprTyped (d, ty) }
            | None -> d
        let slotExprsAndFills =
            List.zip trailingSlots slotAssign
            |> List.map (fun (slot, assigned) ->
                match assigned with
                | Some a -> (a, false)
                | None -> (mkFill slot, true))
        let finalTrailing = slotExprsAndFills |> List.map fst
        let fills = slotExprsAndFills |> List.filter snd |> List.map fst
        let requiredArgs = args |> List.truncate required
        let requiredNames = ps |> List.truncate required |> List.map (fun (n, _, _) -> n)
        // DECLARATION-SITE MEANING. A filled default is re-inferred HERE, so
        // a free name it reads resolves in the caller's scope. That is only
        // the declaration's meaning when the caller binds that name to the
        // SAME thing; a caller parameter or local of the same spelling
        // (`function g(k) = f()` against `function f(x = k)`) is a different
        // binding, and the splice used to read it silently. Identities were
        // recorded when the callable was declared (FuncDefaultCaptures);
        // required-parameter references are the splice's own business and
        // are excluded, exactly as the recording excluded them.
        let shadowed =
            match defaultsKey |> Option.map env.FuncDefaultCaptures.TryGetValue with
            | Some (true, captures) when not (Map.isEmpty captures) ->
                List.zip trailingSlots slotAssign
                |> List.tryPick (fun ((slotName, _, dflt), assigned) ->
                    match assigned, dflt with
                    | None, Some d ->
                        collectFreeVars (Set.ofList requiredNames) d
                        |> Set.toList
                        |> List.tryPick (fun n ->
                            match Map.tryFind n captures, Map.tryFind n env.Variables with
                            | Some declId, Some vi when vi.VarId <> declId -> Some (slotName, n)
                            | _ -> None)
                    | _ -> None)
            | _ -> None
        match shadowed with
        | Some (slotName, n) -> Some (Error (DefaultParamShadowed (calleeName, slotName, n)))
        | None ->
        // Only fills can reference params, and only REQUIRED ones (scope rule).
        let referencedNames =
            let free =
                fills
                |> List.map (collectFreeVars Set.empty)
                |> List.fold Set.union Set.empty
            requiredNames |> List.filter (fun n -> Set.contains n free) |> Set.ofList
        if Set.isEmpty referencedNames then
            Some (Ok (mkExpr callSpan (ExprKind.ExprApp (func, requiredArgs @ finalTrailing))))
        else
            let uid = env.Builder.FreshId()
            let refIdx =
                requiredNames
                |> List.mapi (fun i n -> (i, n))
                |> List.filter (fun (_, n) -> Set.contains n referencedNames)
            let mkLet name (value: Expr) =
                StmtLet { Mutability = BindLet
                          Pattern = { Kind = PatVar name; Span = value.Span }
                          Type = None
                          Value = value }
            let tempName i = $"__dflt{uid}_{i}"
            let tempStmts = refIdx |> List.map (fun (i, _) -> mkLet (tempName i) (List.item i requiredArgs))
            let aliasStmts =
                refIdx |> List.map (fun (i, n) ->
                    mkLet n (mkExpr (List.item i requiredArgs).Span (ExprKind.ExprVar (tempName i))))
            let callRequired =
                requiredArgs |> List.mapi (fun i a ->
                    if refIdx |> List.exists (fun (j, _) -> j = i)
                    then mkExpr a.Span (ExprKind.ExprVar (List.item i requiredNames))
                    else a)
            let call = mkExpr callSpan (ExprKind.ExprApp (func, callRequired @ finalTrailing))
            Some (Ok (mkExpr callSpan (ExprKind.ExprBlock (tempStmts @ aliasStmts, Some call))))

/// CHAINED FACTORY SUGAR: `f(x, y)(a : q1)(b : q2)` flattens into the single
/// call `f(x, y, a : q1, b : q2)` BEFORE dispatch, when the base callee is a
/// defaults-carrying function (env.FuncDefaults) and EVERY argument of every
/// trailing application is quantity-tagged. Comma groups and chains are
/// equivalent (`f(x)(a : q1, b : q2)` too). Genuine arrow over-application
/// never matches -- a curried function carries no defaults entry, or its
/// trailing args are not quantity-tagged -- so the existing curry/eta paths
/// keep those shapes exactly.
let internal tryFlattenFactoryChain (env: TypeEnv) (func: Expr) (args: Expr list) : (Expr * Expr list) option =
    match func.Kind with
    | ExprKind.ExprApp _ ->
        let rec collect (f: Expr) (groups: Expr list list) =
            match f.Kind with
            | ExprKind.ExprApp (inner, innerArgs) -> collect inner (innerArgs :: groups)
            | _ -> (f, groups)
        let baseFn, groups = collect func [args]
        let hasDefaults (name: string) =
            match defaultsKeyOfName env name with
            | Some key -> env.FuncDefaults.ContainsKey key
            | None -> false
        let isDefaultsCallee =
            match baseFn.Kind with
            | ExprKind.ExprVar name -> hasDefaults name
            // Module-qualified base (`plot.contourf(...)(...)`): the
            // `alias.name` binding a qualified import makes, else the bare
            // name (see tryFillDefaultArgs).
            | ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, fname) ->
                hasDefaults (alias + "." + fname) || hasDefaults fname
            | _ -> false
        if not isDefaultsCallee then None
        else
            let trailingGroups = List.tail groups
            let allTagged =
                trailingGroups
                |> List.forall (fun g ->
                    not (List.isEmpty g)
                    && g |> List.forall (fun a -> (argQuantityTag env a).IsSome))
            if List.isEmpty trailingGroups || not allTagged then None
            else Some (baseFn, List.concat groups)
    | _ -> None

/// The variable an assignment target bottoms out in, walking through element
/// and field access: `a(i).f = v` roots at `a`.
let rec internal assignRootName (t: TypedExpr) : string option =
    match t.Kind with
    | TExprVar (name, _, _) -> Some name
    | TExprIndex (b, _, _) -> assignRootName b
    | TExprField (b, _, _) -> assignRootName b
    | _ -> None

/// Whether an assignment target may be written, shared by the two sites that
/// check assignments (expression position and block-statement position) so
/// they cannot drift. `None` = the store is allowed.
///
/// Both rules close paths that used to be SILENT -- they typechecked, lowered,
/// and then mutated nothing:
///
///  1. Rebinding a whole array (`a = <array expr>`) names a NEW array; it does
///     not write into `a`'s storage. For a `mut` array PARAMETER that is a
///     silent wrong answer: the C++ ABI passes the `Array<>` wrapper by value
///     (its data pointer aliases the caller, which is what makes ELEMENT
///     writes land), so a rebind is invisible to the caller -- the emitted
///     body was literally `void bump(Array<double,1> a) { }`. `a(i) = v` is
///     the supported form and is untouched (formalism 2.7; corpus
///     functions/019).
///
///     Restricted to PARAMETERS on purpose. Rebinding a `let mut` BINDING
///     whole is a real, deliberately-tested feature with rebind semantics --
///     the name is repointed at the new array and existing aliases and views
///     keep reading the old buffer (memfree/015, 016 pin exactly that). Both
///     forms bind `MutPassable`, so the parameter set on the env is what
///     separates them.
///  2. A write THROUGH an index or field needs the same permission as a bare
///     store. The old check matched only a bare `TExprVar`, with an explicit
///     "array element assignment etc. -- allowed" fall-through, so writing
///     into a non-`mut` parameter (which binds ReadOnly) or a `let static`
///     array was accepted and then dropped.
///
/// `__`-prefixed targets are exempt from BOTH rules: those names are
/// compiler-synthesized buffers, and one of them (the leading-axis fold's row
/// accumulator) assigns a whole array on purpose. Neither rule is about
/// generated code -- same `__` gate the BL4003 buffer checks use.
let internal assignTargetError (env: TypeEnv) (tL: TypedExpr) : TypeError option =
    match assignRootName tL with
    | Some name when not (name.StartsWith "__") ->
        let isWholeArrayRebind =
            match tL.Kind with
            | TExprVar _ -> Set.contains name env.MutArrayParams
            | _ -> false
        if isWholeArrayRebind then
            Some (MutAssignRefused (name,
                    "assigning a whole array rebinds the name, it does not write the array's storage, "
                    + "so a `mut` parameter's caller would see nothing change. Write the elements "
                    + "(`a(i) = ...`), or return the new array and rebind at the call site."))
        else
            // 3. A write from INSIDE a PARALLEL kernel body (`where omp(...)`
            //    / `cuda`, or anything nested in one) to a binding that lives
            //    OUTSIDE it (a captured `let mut`, a module global): the cells
            //    run concurrently, so the store is a data race (the emitted
            //    loop was `#pragma omp parallel for simd` around `g[0] = g[0]
            //    + ...`). Only the body's own bindings and its `mut`
            //    PARAMETERS may be written there. Matched by VarId, so a
            //    body-local binding that SHADOWS an outer name is its own.
            //    A SERIAL body keeps its write channel: named functions that
            //    update a module-level `let mut` are a pinned feature
            //    (memfree/004, 015, 016; functions/135, 139 pin the CSE
            //    barrier it needs).
            let capturedWrite =
                env.InParallelBody
                && env.InCallableBody
                && (match lookupVar name env, Map.tryFind name env.OuterScope with
                    | Some cur, Some outer -> cur.VarId = outer.VarId
                    | _ -> false)
            if capturedWrite then
                Some (MutAssignRefused (name,
                        "it is bound outside this parallel kernel's body, whose cells run "
                        + "concurrently under `omp`/`cuda`, so the store is a data race. Pass the "
                        + "array as a `mut` parameter (`a: mut Array<...>`), or return the value "
                        + "and bind it outside (a reduction: `reduce`, or a fused `<&!>` fold)."))
            else
            match lookupVar name env with
            | Some info when info.Assign = ReadOnly ->
                // A bare store onto a `let static` keeps its own long-standing
                // wording (pinned by diagnostics/007); only writes THROUGH an
                // index or field reach the new, more specific message.
                match tL.Kind with
                | TExprVar _ -> Some (ImmutableStaticAssign name)
                | _ ->
                    Some (MutAssignRefused (name,
                            "it is not writable here. Parameters are read-only unless declared "
                            + "`mut` (`x: mut Array<...>`), and `let static` is immutable everywhere."))
            | _ -> None
    | _ -> None

/// A provider binding files coordinate variables under `<mod>__dims` and data
/// variables under `<mod>__vars` (NetcdfProvider.ncFileToModule's
/// `isCoordinateVar` split; ZarrProvider's `isCoordinateArr` twin). Spelling
/// the wrong section is the common mistake -- `sample.vars.xdim` for a
/// coordinate -- so when the missing name IS declared on the sibling section,
/// name the accessor that works. Derived from the STRUCT NAME alone, so it
/// needs no provider knowledge here and stays quiet ("") for user structs.
let internal providerSectionSteering (env: TypeEnv) (structName: string) (field: string) : string =
    let sibling =
        if structName.EndsWith "__vars" then Some (structName.Substring(0, structName.Length - 6), "dims")
        elif structName.EndsWith "__dims" then Some (structName.Substring(0, structName.Length - 6), "vars")
        else None
    match sibling with
    | Some (baseName, other) ->
        match lookupTypeDef $"{baseName}__{other}" env with
        | Some (TDIStruct (_, _, fields, _)) when fields |> List.exists (fun (n, _) -> n = field) ->
            $" -- it is declared on {baseName}__{other}, so the accessor is `{baseName}.{other}.{field}`"
        | _ -> ""
    | None -> ""

/// Resolve a field ACCESS (`v.f`, `v.f(i)`) against a named type.
///
///   `Ok (Some (ty, idx))` -- the name is a declared field.
///   `Error (StructFieldUnknown ...)` -- the type IS a struct and the name is
///       not one of its fields (BL3018).
///   `Ok None` -- NO VERDICT. Every other named type reaches field syntax too
///       (aliases, variants, provider index types), and there the historical
///       fresh-type-variable fallback is still the right answer. Only a
///       resolved struct has the field list needed to refuse.
///
/// Before this existed both call sites defaulted a miss to `Fresh(), 0`, so
/// `sample.vars.xdim` typechecked into an array of unknown extent and failed
/// (if at all) in the provider emitter, describing a symptom.
let internal structFieldAccess (env: TypeEnv) (typeName: string) (field: string)
                               : Result<(IRType * int) option, TypeError> =
    match lookupTypeDef typeName env with
    | Some (TDIStruct (_, _, fields, _)) ->
        match fields |> List.tryFindIndex (fun (n, _) -> n = field) with
        | Some idx -> Ok (Some (snd fields.[idx], idx))
        | None ->
            Error (StructFieldUnknown (typeName, field,
                                       fields |> List.map fst,
                                       providerSectionSteering env typeName field))
    | _ -> Ok None
