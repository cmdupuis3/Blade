/// PPL moment formers: moment/comoment tensors and declared independence,
/// elaborated to ordinary Blade source before type checking (pipeline stage
/// between ML-op elaboration and grad expansion — see TypeCheck.typeCheck).
///
/// Surface (call-shaped, recognized when the name is not user-bound; each
/// former must be the ENTIRE right-hand side of a top-level let):
///
///   moments(A, k)       raw comoment tensor of order k (static k >= 1):
///                         method_for(A, ..., A) <@> lambda(x1..xk)
///                           where comm(x1..xk) -> prodsum(x1..xk)/N
///                         |> compute
///                       Output: SymIdx<k, D> packed over A's fused leading
///                       axes; the LAST declared index of A is the sample
///                       (fiber) axis, its static extent is N.
///   comoments(A, 2)     central pair comoment (covariance), same shape with
///                       E[ab] - ma*mb as the kernel. Orders > 2 are
///                       deferred (subset-lattice expansion over prodsums).
///   comoments(X, Y)     central CROSS-covariance block between two arrays —
///                       rectangular (method_for(X, Y), no comm clause).
///   independent(X, Y)   declaration, written `let _ = independent(X, Y)`,
///                       consumed by this stage: a declared-independent pair's
///                       comoments(X, Y) elaborates to a literal zero block —
///                       the cross computation is never emitted. (Exact for
///                       central pair comoments; the cumulant tower will
///                       extend this to higher orders.)
///
/// A moment-formed array must be a module-level `let`/`let static` whose
/// shape is compile-time: either an Array annotation
/// (`Array<Elem like I1, ..., Ik, SampleIdx>`) whose index extents resolve
/// statically (directly or through type aliases), or — for COMPUTED arrays —
/// an un-annotated `method_for(range<...>) <@> kernel [|> compute]` RHS,
/// whose iteration space is the shape (one axis per dense range slot, halo
/// slots shrunk to their interior; see computedShapeOf). The binding-time
/// contract stands: shape is compile-time, data rides through.
module Blade.Ppl.Elaborate

open Blade.Ast
open Blade.StaticEval

// ============================================================================
// AST construction helpers (mirroring MLElaborate.fs / Grad.fs style)
// ============================================================================

let private v (n: string) = syn (ExprVar n)
let private fLit (x: float) = syn (ExprLit (LitFloat x))
let private addE a b = syn (ExprBinOp (Elementwise, OpAdd, a, b))
let private divE a b = syn (ExprBinOp (Elementwise, OpDiv, a, b))
let private mulE a b = syn (ExprBinOp (Elementwise, OpMul, a, b))
let private subE a b = syn (ExprBinOp (Elementwise, OpSub, a, b))
let private powE a b = syn (ExprBinOp (Elementwise, OpCaret, a, b))
let private sLet n value = StmtLet { Pattern = synPat (PatVar n); Type = None; Value = value; Mutability = BindLet }
let private meanE arr n = divE (syn (ExprReduce (arr, syn (ExprSection OpAdd), None))) (fLit n)
let private prodsumE args = syn (ExprApp (v "prodsum", args))
let private commWhere (names: string list) =
    Some { Commutativity = [names]; Parallel = []; TDims = []; Custom = [] }
// Full-span construction wrappers (stamp the ambient synthSpan the module
// expansion sets per user decl). Structural combinators only — the scalar
// helpers above already wrap.
let private appE f args = syn (ExprApp (f, args))
let private arrLitE (cells: Expr list) = syn (ExprArrayLit cells)
let private methodForE (arrs: Expr list) = syn (ExprMethodFor arrs)
let private lambdaE ps w body = syn (ExprLambda (ps, w, body))
let private applyE l k = syn (ExprBinOp (Elementwise, OpApply, l, k))
let private computeE e = syn (ExprCompute e)
let private reduceAddE e = syn (ExprReduce (e, syn (ExprSection OpAdd), None))
let private pvar n = synPat (PatVar n)
let private ptuple (ps: Pattern list) = synPat (PatTuple ps)
/// Inline co-iteration pipeline over same-shape (packed included) arrays:
/// method_for(zip(a, b)) <@> lambda(u, w) -> body |> compute — the corpus-
/// blessed one-binding form (sql-set-ops/004).
let private zipMap2 (a: Expr) (b: Expr) (body: Expr) =
    computeE (applyE
        (methodForE [syn (ExprZip [a; b])])
        (lambdaE [{ Name = "__u"; Type = None }; { Name = "__w"; Type = None }] None body))
let private map1 (a: Expr) (body: Expr) =
    computeE (applyE
        (methodForE [a])
        (lambdaE [{ Name = "__u"; Type = None }] None body))

// NOTE: "cumulant" is deliberately NOT a former name anymore — cumulant(d, k)
// is a checker-level projection on Dist-typed values (TypeCheck's
// inferCumulantProj, order guard as a type error), valid in any expression
// position, so elaboration must let it flow through untouched.
let private formerNames = set [ "moments"; "comoments"; "cumulants"; "independent"; "dist"; "dist_add"; "dist_scale"; "comoments_merge"; "mstate"; "mstate_merge"; "mstate_cumulants"; "mixed_cumulants"; "dist_affine"; "dist_jet"; "dist_jet_closed"; "dist_map"; "dist_map_closed"; "free_cumulants"; "dist_expect"; "dist_reweight"; "dist_mix"; "dist_atoms"; "dist_negativity" ]

// ============================================================================
// Partition lattice (the load-bearing combinatorics: cumulants are Möbius-
// weighted sums over set partitions; Bell(r) partitions, 2^r - 1 distinct
// blocks — each block's raw moment is bound once and shared)
// ============================================================================

let rec private factorial (n: int) : float =
    if n <= 1 then 1.0 else float n * factorial (n - 1)

/// All set partitions of [0 .. k-1] (size Bell(k)); blocks kept sorted.
let rec private setPartitions (k: int) : int list list list =
    if k = 0 then [ [] ]
    else
        setPartitions (k - 1)
        |> List.collect (fun p ->
            let el = k - 1
            let asSingleton = p @ [[el]]
            let inserted =
                p |> List.mapi (fun i _ ->
                    p |> List.mapi (fun j b -> if i = j then b @ [el] else b))
            asSingleton :: inserted)

/// Nonempty subsets of [0 .. k-1], each sorted.
let private nonemptySubsets (k: int) : int list list =
    [ 1 .. (1 <<< k) - 1 ]
    |> List.map (fun mask -> [ for i in 0 .. k - 1 do if mask &&& (1 <<< i) <> 0 then yield i ])

// ============================================================================
// The sufficient-statistic pool (the TRUE single-pass tower): every block
// moment at every cell of every order is the raw prodsum of a row-multiset,
// so ONE sweep over the sample axis filling P_S = Σ_t Π_{ℓ∈S} row_ℓ(t) for
// the needed multisets S replaces the per-cell-per-order prodsum loops
// (dist(A,4) at d=2 ran 114 sample-axis traversals for 14 distinct values).
// Emitted as pure combinator algebra — rows → zip → ONE shared method_for →
// one product kernel per multiset → <&!> chain → reduce((+)) — and each
// former's cells become straight-line arithmetic over the pool scalars (the
// proven mstate_merge cell-wise pattern). Applies to single-leading-axis
// sources with static extents; multiaxis moments/cumulants keep the
// per-cell pipeline path.
// ============================================================================

let private iLit (n: int) = syn (ExprLit (LitInt (int64 n)))

/// Canonical (non-decreasing) label tuples of rank p over dim d, lex order.
let private canonicalTuples (d: int) (p: int) : int list list =
    let rec go lo p =
        if p = 0 then [ [] ]
        else [ for x in lo .. d - 1 do for rest in go x (p - 1) -> x :: rest ]
    go 0 p

type private PoolInfo = {
    /// Canonical multiset (sorted row-position list) -> scalar binding name.
    Names: Map<int list, string>
    /// Static sample count (the raw-moment normalizer).
    N: float
}

/// The raw prodsum P_S.
let private poolRead (pool: PoolInfo) (s: int list) : Expr =
    v pool.Names.[List.sort s]

/// The raw moment E[Π_{ℓ∈S} x_ℓ] = P_S / N.
let private poolMoment (pool: PoolInfo) (s: int list) : Expr =
    divE (poolRead pool s) (fLit pool.N)

/// Emit the single-pass pool over a shared row list. `uniq` seeds binding
/// names (the former's output name keeps them unique per former); `rows` =
/// one slice expression per row position; `needed` = the multisets the
/// caller's cells will read (deduped/canonicalized here, deterministic
/// size-then-lex order). Returns the decls and the read handle.
let private poolDecls (span: Span) (uniq: string) (rows: Expr list)
    (needed: int list list) (n: float) : Located<Decl> list * PoolInfo =
    let mkDecl name value =
        { Value = DeclLet { Pattern = pvar name; Type = None; Value = value; Mutability = BindLet }; Span = span }
    let rowName i = sprintf "__ppl_row_%s_%d" uniq i
    let rowDecls = rows |> List.mapi (fun i e -> mkDecl (rowName i) e)
    let lName = sprintf "__ppl_poolL_%s" uniq
    let lValue =
        match rows with
        | [_] -> methodForE [v (rowName 0)]
        | _ -> methodForE [syn (ExprZip [ for i in 0 .. rows.Length - 1 -> v (rowName i) ])]
    let sets = needed |> List.map List.sort |> List.distinct |> List.sortBy (fun s -> (s.Length, s))
    let tag (s: int list) = s |> List.map string |> String.concat "_"
    let pName s = sprintf "__ppl_P_%s_%s" uniq (tag s)
    let kName s = sprintf "__ppl_poolk_%s_%s" uniq (tag s)
    let xName i = sprintf "__x%d" i
    let ps = [ for i in 0 .. rows.Length - 1 -> { Name = xName i; Type = None } ]
    let kDecls =
        sets |> List.map (fun s ->
            let body = s |> List.map (fun i -> v (xName i)) |> List.reduce mulE
            mkDecl (kName s) (lambdaE ps None body))
    let applied = sets |> List.map (fun s -> applyE (v lName) (v (kName s)))
    let chain =
        match applied with
        | first :: rest -> rest |> List.fold (fun acc e -> syn (ExprBinOp (Elementwise, OpFusion, acc, e))) first
        | [] -> failwith "poolDecls: empty multiset list"
    let outPat =
        match sets with
        | [one] -> pvar (pName one)
        | _ -> ptuple (sets |> List.map (fun s -> pvar (pName s)))
    let outDecl = { Value = DeclLet { Pattern = outPat; Type = None
                                      Value = reduceAddE chain
                                      Mutability = BindLet }; Span = span }
    let names = sets |> List.map (fun s -> (s, pName s)) |> Map.ofList
    (rowDecls @ [mkDecl lName lValue] @ kDecls @ [outDecl], { Names = names; N = n })

/// Row slices of a single-leading-axis array: A(0) .. A(d-1).
let private rowSlices (aName: string) (d: int) : Expr list =
    [ for i in 0 .. d - 1 -> appE (v aName) [iLit i] ]

/// The order-r cumulant cell at `labels`, straight-line over the pool:
/// Σ over set partitions π of [r]: (-1)^(|π|-1)(|π|-1)! · Π_B E[Π x_B].
let private cumulantCellExpr (pool: PoolInfo) (labels: int[]) (r: int) : Expr =
    let terms =
        setPartitions r |> List.map (fun p ->
            let b = p.Length
            let w = (if b % 2 = 1 then 1.0 else -1.0) * factorial (b - 1)
            p |> List.fold (fun acc blk ->
                mulE acc (poolMoment pool (blk |> List.map (fun pos -> labels.[pos])))) (fLit w))
    terms |> List.reduce addE

// ============================================================================
// Module context: array annotations, alias resolution, static extents
// ============================================================================

/// Array annotations in scope: name -> (element TypeExpr, index TypeExprs).
/// Only annotated module-level bindings participate — the formers read the
/// declared shape, they never infer one.
let private collectArrays (decls: Located<Decl> list) : Map<string, TypeExpr * TypeExpr list> =
    decls |> List.fold (fun acc d ->
        match d.Value with
        | DeclLet b | DeclStatic b ->
            match b.Pattern, b.Type with
            | { Kind = PatternKind.PatVar name }, Some (TyArray (elem, idxs)) -> Map.add name (elem, idxs) acc
            | _ -> acc
        | _ -> acc) Map.empty

let private collectAliases (decls: Located<Decl> list) : Map<string, TypeExpr> =
    decls |> List.fold (fun acc d ->
        match d.Value with
        | DeclType (TyDeclAlias (name, _, body)) -> Map.add name body acc
        | _ -> acc) Map.empty

/// Resolve an index TypeExpr to its static extent, following alias chains.
let rec private resolveExtent (aliases: Map<string, TypeExpr>) (statics: StaticEnv) (ty: TypeExpr) : int option =
    match ty with
    | TyIdx extent ->
        match evalExpr statics maxSteps extent with
        | Ok (SVInt n) -> Some (int n)
        | _ -> None
    | TyNamed (name, []) ->
        Map.tryFind name aliases |> Option.bind (resolveExtent aliases statics)
    | _ -> None

/// Shape inference for COMPUTED source arrays: an UN-annotated module-level
/// `let A = method_for(range<...>) <@> kernel [|> compute]` carries no
/// declared TyArray, but its iteration space IS its shape — one axis per
/// range slot, halo slots shrunk to their interior (the same dense arithmetic
/// as TypeCheck.haloSlotsOf: reach includes the implicit center 0, interior =
/// n - (hi - lo)). Only statically-resolvable dense slots qualify; compound /
/// unresolvable slots return None and keep the annotation-required contract.
/// The element type is Float64 — the formers' sample algebra is Float64
/// throughout, and an annotated binding always takes precedence.
let private computedShapeOf (aliases: Map<string, TypeExpr>) (statics: StaticEnv) (value: Expr) : (TypeExpr * TypeExpr list) option =
    // Interior extents of one halo slot: flat offsets -> [n - reach]; nested
    // per-axis form [[..],[..]] -> one shrunk extent per axis (same inner).
    let haloExtents (innerTy: TypeExpr) (offsetsExpr: Expr) : int list option =
        resolveExtent aliases statics innerTy |> Option.bind (fun n ->
            let asInt = function SVInt v -> Some (int v) | _ -> None
            let shrunk (offs: int list) =
                if List.isEmpty offs then None
                else
                    let lo = min 0 (List.min offs)
                    let hi = max 0 (List.max offs)
                    Some (n - (hi - lo))
            match evalExpr statics maxSteps offsetsExpr with
            | Ok (SVInt v) -> shrunk [int v] |> Option.map List.singleton
            | Ok (SVTuple vs) when not vs.IsEmpty ->
                let flat = vs |> List.map asInt
                if List.forall Option.isSome flat then
                    // Flat form: one axis.
                    shrunk (flat |> List.map Option.get) |> Option.map List.singleton
                else
                    // Nested per-axis form: every entry a non-empty int array.
                    let perAxis =
                        vs |> List.map (function
                            | SVTuple xs ->
                                let os = xs |> List.map asInt
                                if List.forall Option.isSome os && not os.IsEmpty
                                then shrunk (os |> List.map Option.get) else None
                            | _ -> None)
                    if List.forall Option.isSome perAxis
                    then Some (perAxis |> List.map Option.get) else None
            | _ -> None)
    let slotExtents (ty: TypeExpr) : int list option =
        match ty with
        | TyHalo (inner, offs) -> haloExtents inner offs
        | _ -> resolveExtent aliases statics ty |> Option.map List.singleton
    // Unwrap |> compute and walk down the <@> application to the loop source.
    let rec sourceOf (e: Expr) : Expr option =
        match e.Kind with
        | ExprKind.ExprCompute inner -> sourceOf inner
        | ExprKind.ExprBinOp (_, OpApply, l, _) -> sourceOf l
        | ExprKind.ExprMethodFor [src] -> Some src
        | _ -> None
    sourceOf value |> Option.bind (fun src ->
        let slots =
            match src.Kind with
            | ExprKind.ExprRange idxTys -> Some idxTys
            | ExprKind.ExprHalo (inner, offs) -> Some [TyHalo (inner, offs)]
            | _ -> None
        slots |> Option.bind (fun tys ->
            if List.isEmpty tys then None else
            let exts = tys |> List.map slotExtents
            if List.forall Option.isSome exts then
                let idxs = exts |> List.collect Option.get |> List.map (fun n -> TyIdx (iLit n))
                Some (TyNamed ("Float64", []), idxs)
            else None))

// ============================================================================
// Misplaced-use detection (v1 contract: formers are decl-RHS only)
// ============================================================================

let rec private anyExpr (p: Expr -> bool) (e: Expr) : bool =
    if p e then true else
    let any = anyExpr p
    match e.Kind with
    | ExprKind.ExprBinOp (_, _, l, r) -> any l || any r
    | ExprKind.ExprUnaryOp (_, x) -> any x
    | ExprKind.ExprApp (f, args) -> any f || List.exists any args
    | ExprKind.ExprTupleIndex (t, i) -> any t || any i
    | ExprKind.ExprField (o, _) -> any o
    | ExprKind.ExprLambda (_, _, b) -> any b
    | ExprKind.ExprLet (bind, body) -> any bind.Value || any body
    | ExprKind.ExprMatch (s, cases) -> any s || (cases |> List.exists (fun c -> any c.Body || (c.Guard |> Option.map any |> Option.defaultValue false)))
    | ExprKind.ExprIf (c, t, f) -> any c || any t || any f
    | ExprKind.ExprTuple es | ExprKind.ExprArrayLit es | ExprKind.ExprZip es | ExprKind.ExprStack es | ExprKind.ExprSequence es -> List.exists any es
    | ExprKind.ExprBlock (stmts, fin) ->
        let stmtAny s =
            let rec go s =
                match s with
                | StmtSpanned (inner, _) -> go inner
                | StmtLet b -> any b.Value
                | StmtAssign (l, _, r) -> any l || any r
                | StmtExpr x -> any x
                | StmtForIn (_, r, body) -> any r || List.exists go body
            go s
        List.exists stmtAny stmts || (fin |> Option.map any |> Option.defaultValue false)
    | ExprKind.ExprStruct (_, fields, spread) ->
        fields |> List.exists (snd >> any) || (spread |> Option.map any |> Option.defaultValue false)
    | ExprKind.ExprTyped (x, _) -> any x
    | ExprKind.ExprMethodFor arrays -> List.exists any arrays
    | ExprKind.ExprObjectFor k -> any k
    | ExprKind.ExprAlign (es, _) -> List.exists any es
    | ExprKind.ExprPure x | ExprKind.ExprCompute x | ExprKind.ExprRead x | ExprKind.ExprUnique x | ExprKind.ExprRank x | ExprKind.ExprExtents x -> any x
    | ExprKind.ExprGuard (c, b) -> any c || any b
    | ExprKind.ExprReplicate (c, b) -> any c || any b
    | ExprKind.ExprMask (a, pr) | ExprKind.ExprCompound (a, pr) | ExprKind.ExprGroupBy (a, pr)
    | ExprKind.ExprIntersect (a, pr) | ExprKind.ExprUnion (a, pr) | ExprKind.ExprContains (a, pr)
    | ExprKind.ExprSort (a, pr) | ExprKind.ExprGram (a, pr) -> any a || any pr
    | ExprKind.ExprReduce (a, k, i) -> any a || any k || (i |> Option.map any |> Option.defaultValue false)
    | ExprKind.ExprAssign (l, r) -> any l || any r
    | _ -> false

let private isFormerCallOf (activeNames: Set<string>) (e: Expr) =
    match e.Kind with
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar n }, _) -> Set.contains n activeNames
    | _ -> false

// ============================================================================
// Struct-declared independence (constrained records, formalism §17.13.2):
// `struct S { X: Array<...>, Y: Array<...> } where indep(X, Y)` — indep is a
// STATIC license, comm's sibling, not a runtime proposition. Its conjuncts
// are stripped from the construction-time validate() and consumed into the
// independence relation; any residual invariant stays runtime-checked.
// ============================================================================

/// Split a struct where-invariant into indep(...) conjuncts and the
/// residual runtime expression (None when indep was the whole invariant).
let rec private splitInvariant (e: Expr) : (string * string) list * Expr option =
    match e.Kind with
    | ExprKind.ExprBinOp (m, OpAnd, l, r) ->
        let (il, le) = splitInvariant l
        let (ir, re) = splitInvariant r
        let residual =
            match le, re with
            | Some a, Some b -> Some (inheritSpan e (ExprBinOp (m, OpAnd, a, b)))
            | Some a, None | None, Some a -> Some a
            | None, None -> None
        (il @ ir, residual)
    // Normalized from `where <alias>.indep(X, Y)` by stripQualified before
    // the core passes run.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "__ppl_indep" }, [{ Kind = ExprKind.ExprVar x }; { Kind = ExprKind.ExprVar y }]) -> ([(x, y)], None)
    | _ -> ([], Some e)

/// Deterministic alias binding name for a struct-field array path m.f —
/// formers iterate named bindings (method_for over a raw field access
/// doesn't reach codegen's binding-keyed loop machinery), so field paths
/// normalize to `let __ppl_arr_m_f = m.f` at first use.
let private aliasOf (m: string) (f: string) = sprintf "__ppl_arr_%s_%s" m f

// ============================================================================
// Former elaboration
// ============================================================================

type private Ctx = {
    Arrays: Map<string, TypeExpr * TypeExpr list>
    Aliases: Map<string, TypeExpr>
    Statics: StaticEnv
    /// Unordered independence relation over array names.
    Indep: Set<string * string>
    /// Single-array pools already emitted this module (source array name →
    /// handle) — later formers over the same array REUSE the sweep.
    Pools: Map<string, PoolInfo> ref
    /// Pre-scanned maximal multiset size each source array needs across
    /// ALL its formers, so the first former emits one maximal pool.
    PoolMax: Map<string, int>
    /// Pool-path former outputs that are FLAT lex SymIdx<2, d>-shaped
    /// tensors (binding name → variable-axis extent d) — consumers like
    /// comoments_merge switch to cell-wise flat reads for these.
    FlatDims: Map<string, int> ref
}

/// The module's pool for a single-axis source array: reuse if already
/// emitted, else emit ONE maximal sweep (sizes 1..max over every former
/// that reads this array) at the first former's position and cache it.
let private acquirePool (ctx: Ctx) (span: Span) (aName: string) (d: int) (n: float) (selfMax: int)
    : Located<Decl> list * PoolInfo =
    match Map.tryFind aName ctx.Pools.Value with
    | Some pool -> ([], pool)
    | None ->
        let maxR = max selfMax (Map.tryFind aName ctx.PoolMax |> Option.defaultValue selfMax)
        let needed = [ for p in 1 .. maxR do yield! canonicalTuples d p ]
        let (pd, pool) = poolDecls span aName (rowSlices aName d) needed n
        ctx.Pools.Value <- Map.add aName pool ctx.Pools.Value
        (pd, pool)

let private indepKey (a: string) (b: string) = if a <= b then (a, b) else (b, a)

/// Shape info the formers need: leading axes, fiber axis, static N.
let private arrayShape (ctx: Ctx) (what: string) (name: string) : Result<TypeExpr * TypeExpr list * TypeExpr * int, string> =
    match Map.tryFind name ctx.Arrays with
    | None ->
        Error (sprintf "%s: '%s' must be a module-level let with an Array<Elem like ..., SampleIdx> annotation (the formers read the declared shape)" what name)
    | Some (_, idxs) when idxs.Length < 2 ->
        Error (sprintf "%s: '%s' needs at least one variable axis plus the sample axis (Array<Elem like VarIdx, SampleIdx>); a lone sample axis has no comoment structure" what name)
    | Some (elem, idxs) ->
        let fiber = List.last idxs
        let leading = idxs |> List.take (idxs.Length - 1)
        match resolveExtent ctx.Aliases ctx.Statics fiber with
        | None -> Error (sprintf "%s: '%s' sample axis extent must be statically known (Idx<n> directly or through aliases)" what name)
        | Some n -> Ok (elem, leading, fiber, n)

/// moments(A, k): raw order-k comoment former.
let private elabMoments (ctx: Ctx) (span: Span) (outName: string) (binding: Binding) (args: Expr list)
    : Result<Located<Decl> list, string> =
    match args with
    | [{ Kind = ExprKind.ExprVar aName }; kExpr] ->
        let k =
            match evalExpr ctx.Statics maxSteps kExpr with
            | Ok (SVInt n) when n >= 1L -> Ok (int n)
            | Ok (SVInt n) -> Error (sprintf "moments: order must be >= 1, got %d" n)
            | _ -> Error "moments: the order must be a compile-time integer (a literal, `let static`, or static-function call)"
        k |> Result.bind (fun k ->
        arrayShape ctx "moments" aName |> Result.map (fun (elem, leading, fiber, n) ->
            match leading with
            | [ix] when (resolveExtent ctx.Aliases ctx.Statics ix).IsSome ->
                // SINGLE-PASS PATH: μ_S = P_S / N straight from the
                // module's shared pool sweep.
                let d = (resolveExtent ctx.Aliases ctx.Statics ix).Value
                let (pd, pool) = acquirePool ctx span aName d (float n) k
                let cells = [ for labels in canonicalTuples d k -> poolMoment pool labels ]
                pd @ [ { Value = DeclLet { binding with Value = arrLitE cells }; Span = span } ]
            | _ ->
                // Multiaxis / unresolvable leading extent: per-cell pipeline.
                let paramNames = [ for i in 1 .. k -> sprintf "__x%d" i ]
                let ps = paramNames |> List.map (fun p -> { Name = p; Type = Some (TyArray (elem, [fiber])) })
                let whereC = if k >= 2 then commWhere paramNames else None
                let body = divE (prodsumE (paramNames |> List.map v)) (fLit (float n))
                let lName = sprintf "__ppl_L_%s" outName
                let kName = sprintf "__ppl_k_%s" outName
                let mk value = { Pattern = pvar ""; Type = None; Value = value; Mutability = BindLet }
                [ { Value = DeclLet { mk (methodForE (List.replicate k (v aName))) with Pattern = pvar lName }; Span = span }
                  { Value = DeclLet { mk (lambdaE ps whereC body) with Pattern = pvar kName }; Span = span }
                  { Value = DeclLet { binding with Value = computeE (applyE (v lName) (v kName)) }; Span = span } ]))
    | _ ->
        Error "moments expects moments(A, k): an annotated module-level array and a static order"

/// Central pair kernel body: E[ab] - ma*mb, spelled over reduce/prodsum
/// (both proven kernel-position primitives; no elementwise fiber algebra).
let private centralPairBody (n: float) =
    syn (ExprBlock (
        [ sLet "__ma" (meanE (v "__x1") n)
          sLet "__mb" (meanE (v "__x2") n) ],
        Some (subE (divE (prodsumE [v "__x1"; v "__x2"]) (fLit n)) (mulE (v "__ma") (v "__mb")))))

/// comoments(A, 2) same-array | comoments(X, Y) cross-block.
let private elabComoments (ctx: Ctx) (span: Span) (outName: string) (binding: Binding) (args: Expr list)
    : Result<Located<Decl> list, string> =
    let lName = sprintf "__ppl_L_%s" outName
    let kName = sprintf "__ppl_k_%s" outName
    let mkDecl pat value = { Value = DeclLet { Pattern = pvar pat; Type = None; Value = value; Mutability = BindLet }; Span = span }
    match args with
    // Same-array central comoment of static order (only 2 for now)
    | [{ Kind = ExprKind.ExprVar aName }; kExpr] when (match evalExpr ctx.Statics maxSteps kExpr with Ok (SVInt _) -> true | _ -> false) ->
        match evalExpr ctx.Statics maxSteps kExpr with
        | Ok (SVInt 2L) ->
            arrayShape ctx "comoments" aName |> Result.map (fun (elem, leading, fiber, n) ->
                match leading with
                | [ix] when (resolveExtent ctx.Aliases ctx.Statics ix).IsSome ->
                    // SINGLE-PASS PATH: C_ij = P_ij/N − (P_i/N)(P_j/N)
                    // straight off the module's shared pool.
                    let d = (resolveExtent ctx.Aliases ctx.Statics ix).Value
                    let (pd, pool) = acquirePool ctx span aName d (float n) 2
                    ctx.FlatDims.Value <- Map.add outName d ctx.FlatDims.Value
                    let cells =
                        [ for labels in canonicalTuples d 2 ->
                            match labels with
                            | [i; j] -> subE (poolMoment pool labels) (mulE (poolMoment pool [i]) (poolMoment pool [j]))
                            | _ -> fLit 0.0 ]
                    pd @ [ { Value = DeclLet { binding with Value = arrLitE cells }; Span = span } ]
                | _ ->
                    let ps = ["__x1"; "__x2"] |> List.map (fun p -> { Name = p; Type = Some (TyArray (elem, [fiber])) })
                    [ mkDecl lName (methodForE [v aName; v aName])
                      mkDecl kName (lambdaE ps (commWhere ["__x1"; "__x2"]) (centralPairBody (float n)))
                      { Value = DeclLet { binding with Value = computeE (applyE (v lName) (v kName)) }; Span = span } ])
        | _ ->
            Error "comoments: only order 2 (covariance) is supported so far; higher central orders await the subset-lattice expansion over prodsums"
    // Cross block between two distinct arrays
    | [{ Kind = ExprKind.ExprVar xName }; { Kind = ExprKind.ExprVar yName }] ->
        if xName = yName then
            Error "comoments(X, X): use comoments(X, 2) for the same-array (packed) form"
        else
        arrayShape ctx "comoments" xName |> Result.bind (fun (elemX, leadX, fibX, nX) ->
        arrayShape ctx "comoments" yName |> Result.bind (fun (elemY, leadY, fibY, nY) ->
            if nX <> nY then
                Error (sprintf "comoments: '%s' and '%s' sample axes disagree (%d vs %d)" xName yName nX nY)
            elif Set.contains (indepKey xName yName) ctx.Indep then
                // Declared independent: the central cross block is structurally
                // zero — emit the literal, never the loops. Needs both leading
                // extents statically (single leading axis each, v1).
                match leadX, leadY with
                | [ix], [iy] ->
                    match resolveExtent ctx.Aliases ctx.Statics ix, resolveExtent ctx.Aliases ctx.Statics iy with
                    | Some dx, Some dy ->
                        let zeros = arrLitE (List.replicate dx (arrLitE (List.replicate dy (fLit 0.0))))
                        Ok [ { Value = DeclLet { binding with Value = zeros }; Span = span } ]
                    | _ -> Error (sprintf "comoments: independent zero block needs static variable-axis extents for '%s' and '%s'" xName yName)
                | _ -> Error "comoments: independent zero blocks support one variable axis per array so far (multi-axis blocks deferred)"
            else
                match leadX, leadY with
                | [ixx], [ixy] when (resolveExtent ctx.Aliases ctx.Statics ixx).IsSome
                                     && (resolveExtent ctx.Aliases ctx.Statics ixy).IsSome ->
                    // SINGLE-PASS PATH: a JOINT pool over both arrays' rows
                    // (X rows at positions 0..dx-1, Y rows at dx..dx+dy-1;
                    // one sweep of the shared sample axis). Rectangular
                    // rank-2 output preserved via nested cells. Joint pools
                    // are per-former (keyed by the output name), not cached.
                    let dx = (resolveExtent ctx.Aliases ctx.Statics ixx).Value
                    let dy = (resolveExtent ctx.Aliases ctx.Statics ixy).Value
                    let rows = rowSlices xName dx @ rowSlices yName dy
                    let needed =
                        [ for i in 0 .. dx - 1 -> [i] ]
                        @ [ for j in 0 .. dy - 1 -> [dx + j] ]
                        @ [ for i in 0 .. dx - 1 do for j in 0 .. dy - 1 -> [i; dx + j] ]
                    let (pd, pool) = poolDecls span outName rows needed (float nX)
                    let cells =
                        arrLitE
                            [ for i in 0 .. dx - 1 ->
                                arrLitE
                                    [ for j in 0 .. dy - 1 ->
                                        subE (poolMoment pool [i; dx + j])
                                             (mulE (poolMoment pool [i]) (poolMoment pool [dx + j])) ] ]
                    Ok (pd @ [ { Value = DeclLet { binding with Value = cells }; Span = span } ])
                | _ ->
                    let px = { Name = "__x1"; Type = Some (TyArray (elemX, [fibX])) }
                    let py = { Name = "__x2"; Type = Some (TyArray (elemY, [fibY])) }
                    Ok [ mkDecl lName (methodForE [v xName; v yName])
                         mkDecl kName (lambdaE [px; py] None (centralPairBody (float nX)))
                         { Value = DeclLet { binding with Value = computeE (applyE (v lName) (v kName)) }; Span = span } ]))
    | _ ->
        Error "comoments expects comoments(A, 2) (same-array covariance) or comoments(X, Y) (cross block)"

// ============================================================================
// Cumulants: the partition-lattice expander
// ============================================================================

/// Order-r cumulant kernel over fiber params __x1..__xr:
///   κ_r = Σ over set partitions π of [r]:
///           (-1)^(|π|-1) (|π|-1)! · Π over blocks B: E[Π_{i∈B} x_i]
/// Each distinct block's raw moment E[Π x_B] = prodsum(x_B)/N is bound once
/// (2^r - 1 lets) and shared across the Bell(r) partition terms.
let private cumulantKernelBody (r: int) (n: float) : Expr =
    let blockName (s: int list) = "__m" + (s |> List.map (fun i -> string (i + 1)) |> String.concat "")
    let lets =
        nonemptySubsets r |> List.map (fun s ->
            sLet (blockName s) (divE (prodsumE (s |> List.map (fun i -> v (sprintf "__x%d" (i + 1))))) (fLit n)))
    let terms =
        setPartitions r |> List.map (fun p ->
            let b = p.Length
            let w = (if b % 2 = 1 then 1.0 else -1.0) * factorial (b - 1)
            p |> List.fold (fun acc blk -> mulE acc (v (blockName (List.sort blk)))) (fLit w))
    syn (ExprBlock (lets, Some (terms |> List.reduce addE)))

/// The proven three-decl former pipeline over ONE array:
/// L = method_for(A ×k); kernel = lambda over annotated fiber params
/// (comm for k >= 2); out = L <@> kernel |> compute.
let private formerPipeline (span: Span) (outName: string) (outBinding: Binding option)
    (aName: string) (elem: TypeExpr) (fiber: TypeExpr) (k: int) (body: Expr) : Located<Decl> list =
    let lName = sprintf "__ppl_L_%s" outName
    let kName = sprintf "__ppl_k_%s" outName
    let paramNames = [ for i in 1 .. k -> sprintf "__x%d" i ]
    let ps = paramNames |> List.map (fun p -> { Name = p; Type = Some (TyArray (elem, [fiber])) })
    let whereC = if k >= 2 then commWhere paramNames else None
    let outValue = computeE (applyE (v lName) (v kName))
    let outDecl =
        match outBinding with
        | Some b -> DeclLet { b with Value = outValue }
        | None -> DeclLet { Pattern = pvar outName; Type = None; Value = outValue; Mutability = BindLet }
    [ { Value = DeclLet { Pattern = pvar lName; Type = None; Value = methodForE (List.replicate k (v aName)); Mutability = BindLet }; Span = span }
      { Value = DeclLet { Pattern = pvar kName; Type = None; Value = lambdaE ps whereC body; Mutability = BindLet }; Span = span }
      { Value = outDecl; Span = span } ]

/// cumulants(A, r): the order-r joint cumulant tensor, SymIdx<r, D> packed.
let private elabCumulants (ctx: Ctx) (span: Span) (outName: string) (binding: Binding) (args: Expr list)
    : Result<Located<Decl> list, string> =
    match args with
    | [{ Kind = ExprKind.ExprVar aName }; rExpr] ->
        let r =
            match evalExpr ctx.Statics maxSteps rExpr with
            | Ok (SVInt n) when n >= 1L && n <= 6L -> Ok (int n)
            | Ok (SVInt n) -> Error (sprintf "cumulants: order must be in 1..6 (got %d) — Bell-number kernel growth beyond that needs the shared-subexpression pass" n)
            | _ -> Error "cumulants: the order must be a compile-time integer (a literal, `let static`, or static-function call)"
        r |> Result.bind (fun r ->
        arrayShape ctx "cumulants" aName |> Result.map (fun (elem, leading, fiber, n) ->
            match leading with
            | [ix] when (resolveExtent ctx.Aliases ctx.Statics ix).IsSome ->
                // SINGLE-PASS PATH: the module's pool sweep (shared across
                // formers over this array), κ_r cells as straight-line
                // partition sums over pool reads. ONE traversal of the
                // sample axis instead of one prodsum loop per block per
                // cell.
                let d = (resolveExtent ctx.Aliases ctx.Statics ix).Value
                let (pd, pool) = acquirePool ctx span aName d (float n) r
                let cells =
                    [ for labels in canonicalTuples d r ->
                        cumulantCellExpr pool (List.toArray labels) r ]
                pd @ [ { Value = DeclLet { binding with Value = arrLitE cells }; Span = span } ]
            | _ ->
                // Multiaxis / unresolvable leading extent: the per-cell
                // pipeline path (fused leading axes, kernel prodsum lets).
                formerPipeline span outName (Some binding) aName elem fiber r (cumulantKernelBody r (float n))))
    | _ ->
        Error "cumulants expects cumulants(A, r): an annotated module-level array and a static order"

// ============================================================================
// The Dist tower (v1): a dist is a COMPILE-TIME object — the binding is
// consumed and its cumulant components materialize as packed arrays. The
// exact laws of the tower are the elaboration rules:
//   dist(A, r)        κ_1..κ_r pipelines from data
//   dist_add(d1, d2)  per-order tensor addition — REQUIRES declared
//                     independence between every pair of source arrays
//                     (cumulants of a sum add exactly iff independent)
//   dist_scale(c, d)  κ_k scaled by c^k (multilinearity)
//   cumulant(d, k)    the order-k component, bound as an ordinary array
// ============================================================================

type private DistInfo = {
    Order: int
    /// Component binding name per order (index k-1 → κ_k array).
    Components: string list
    /// Underlying data arrays, for the independence requirement.
    Sources: Set<string>
    /// Variable-space dimension override: pushforward results carry their
    /// output dimension here (scalar jet results = Some 1); None = derive
    /// it from the single source array's annotation (distDim).
    Dim: int option
    /// Components stored FLAT (lex-canonical ArrayLits read by offset)
    /// instead of method_for-packed (logical multi-index reads).
    Flat: bool
}

let private distComponentName (dName: string) (k: int) = sprintf "__dist_%s_k%d" dName k

/// The binding that makes a dist a VALUE: `let d = __dist_pack(κ1, ..., κr)`.
/// The checker types the intrinsic as Dist<r, τ like axes> (nominal; erased
/// back to this same component tuple at zonk), so `d` is first-class — it
/// crosses function boundaries and cumulant(d, k) projects it anywhere.
let private distPackDecl (span: Span) (dName: string) (info: DistInfo) : Located<Decl> =
    { Value = DeclLet { Pattern = pvar dName; Type = None
                        Value = appE (v "__dist_pack") (info.Components |> List.map v)
                        Mutability = BindLet }
      Span = span }

let private elabDist (ctx: Ctx) (span: Span) (dName: string) (args: Expr list)
    : Result<Located<Decl> list * DistInfo, string> =
    match args with
    | [{ Kind = ExprKind.ExprVar aName }; rExpr] ->
        let r =
            match evalExpr ctx.Statics maxSteps rExpr with
            | Ok (SVInt n) when n >= 1L && n <= 6L -> Ok (int n)
            | _ -> Error "dist: the order must be a compile-time integer in 1..6"
        r |> Result.bind (fun r ->
        arrayShape ctx "dist" aName |> Result.map (fun (elem, _leading, fiber, n) ->
            // THE TOWER, fused: per order a loop object and a cumulant
            // kernel, then ONE compute over the <&>-chain, destructured
            // into the per-order components — a single deferred computation
            // owns the whole tower (the user-facing staged construction of
            // this same shape is the planned follow-up).
            let comps = [ for k in 1 .. r -> distComponentName dName k ]
            let lName k = sprintf "__ppl_L_%s_k%d" dName k
            let kName k = sprintf "__ppl_k_%s_k%d" dName k
            let stageDecls =
                [ for k in 1 .. r do
                    let paramNames = [ for i in 1 .. k -> sprintf "__x%d" i ]
                    let ps = paramNames |> List.map (fun p -> { Name = p; Type = Some (TyArray (elem, [fiber])) })
                    let whereC = if k >= 2 then commWhere paramNames else None
                    yield { Value = DeclLet { Pattern = pvar (lName k); Type = None; Value = methodForE (List.replicate k (v aName)); Mutability = BindLet }; Span = span }
                    yield { Value = DeclLet { Pattern = pvar (kName k); Type = None; Value = lambdaE ps whereC (cumulantKernelBody k (float n)); Mutability = BindLet }; Span = span } ]
            let applied = [ for k in 1 .. r -> applyE (v (lName k)) (v (kName k)) ]
            let fusedVal =
                match applied with
                | [one] -> computeE one
                | first :: restA -> computeE (restA |> List.fold (fun acc e -> syn (ExprBinOp (Elementwise, OpParallel, acc, e))) first)
                | [] -> computeE (v aName)  // unreachable: r >= 1
            let outPat =
                match comps with
                | [one] -> pvar one
                | _ -> ptuple (comps |> List.map pvar)
            let fusedDecl = { Value = DeclLet { Pattern = outPat; Type = None; Value = fusedVal; Mutability = BindLet }; Span = span }
            (stageDecls @ [fusedDecl], { Order = r; Components = comps; Sources = Set.singleton aName; Dim = None; Flat = false })))
    | _ ->
        Error "dist expects dist(A, r): an annotated module-level array and a static order"

/// Shared body of dist addition and subtraction: per-order combination
/// c_k = a_k + weight(k)·b_k. Addition is weight ≡ 1; subtraction is
/// weight k = (−1)^k (κ_k(−Y) = (−1)^k κ_k(Y)). Both are exact ONLY for
/// independent operands — demanded for every source-array pair.
let private elabDistCombine (opName: string) (weight: int -> float) (ctx: Ctx) (span: Span) (dName: string)
    (dists: Map<string, DistInfo>) (args: Expr list)
    : Result<Located<Decl> list * DistInfo, string> =
    match args with
    | [{ Kind = ExprKind.ExprVar n1 }; { Kind = ExprKind.ExprVar n2 }] ->
        match Map.tryFind n1 dists, Map.tryFind n2 dists with
        | Some d1, Some d2 when d1.Order = d2.Order ->
            let missing =
                [ for s1 in d1.Sources do
                    for s2 in d2.Sources do
                      if not (Set.contains (indepKey s1 s2) ctx.Indep) then yield (s1, s2) ]
            match missing with
            | (s1, s2) :: _ ->
                Error (sprintf "dist %s: cumulants combine only for independent distributions — declare independence of %s and %s (loose `let _ = ppl.independent(...)` or a struct `where ppl.indep(...)`)" opName s1 s2)
            | [] ->
                let decls =
                    [ for k in 1 .. d1.Order ->
                        let outN = distComponentName dName k
                        let contrib =
                            if weight k = 1.0 then v "__w"
                            else mulE (fLit (weight k)) (v "__w")
                        { Value = DeclLet { Pattern = pvar outN; Type = None
                                            Value = zipMap2 (v d1.Components.[k - 1]) (v d2.Components.[k - 1]) (addE (v "__u") contrib)
                                            Mutability = BindLet }
                          Span = span } ]
                let info = { Order = d1.Order
                             Components = [ for k in 1 .. d1.Order -> distComponentName dName k ]
                             Sources = Set.union d1.Sources d2.Sources
                             Dim = (if d1.Dim.IsSome then d1.Dim else d2.Dim)
                             Flat = d1.Flat || d2.Flat }
                Ok (decls, info)
        | Some d1, Some d2 ->
            Error (sprintf "dist %s: orders disagree (%d vs %d) — carry the same stochastic order on both sides" opName d1.Order d2.Order)
        | _ ->
            Error (sprintf "dist %s expects two previously declared dist(...) bindings" opName)
    | _ ->
        Error (sprintf "dist %s expects two dist operands" opName)

let private elabDistScale (ctx: Ctx) (span: Span) (dName: string)
    (dists: Map<string, DistInfo>) (args: Expr list)
    : Result<Located<Decl> list * DistInfo, string> =
    match args with
    | [cExpr; { Kind = ExprKind.ExprVar dn }] ->
        match Map.tryFind dn dists with
        | Some d ->
            // κ_k(c·X) = c^k κ_k(X): multilinearity, spelled as k repeated
            // multiplications so c may be any (pure) scalar expression.
            let decls =
                [ for k in 1 .. d.Order ->
                    let outN = distComponentName dName k
                    let scaled = List.replicate k cExpr |> List.fold mulE (v "__u")
                    { Value = DeclLet { Pattern = pvar outN; Type = None
                                        Value = map1 (v d.Components.[k - 1]) scaled
                                        Mutability = BindLet }
                      Span = span } ]
            let info = { d with Components = [ for k in 1 .. d.Order -> distComponentName dName k ] }
            Ok (decls, info)
        | None ->
            Error "dist_scale expects dist_scale(c, d) with a previously declared dist binding d"
    | _ ->
        Error "dist_scale expects dist_scale(c, d)"

// ============================================================================
// Streaming merge (clause 2 of the staging contract: a growing input is a
// stream, and the merge monoid IS the semantics of "the file got longer")
// ============================================================================

/// comoments_merge(cA, mA, nA, cB, mB, nB): combine two chunks' pair
/// comoments (population 1/n normalization) and means into the whole's —
/// the exact pooled-covariance identity (the k = 2 Pébay/Chan merge):
///   C = (nA·CA + nB·CB)/n + (nA·nB/n²)·δδᵀ,  δ = mB − mA,  n = nA + nB
/// Chunk sizes are static; the correction δδᵀ is a packed symmetric outer
/// square (the 012-corpus scalar product former), so the merged tensor has
/// the same SymIdx<2, D> storage as its inputs. Associative by the algebra.
let private elabComomentsMerge (ctx: Ctx) (span: Span) (outName: string) (binding: Binding) (args: Expr list)
    : Result<Located<Decl> list, string> =
    match args with
    | [{ Kind = ExprKind.ExprVar cA }; { Kind = ExprKind.ExprVar mA }; nAExpr; { Kind = ExprKind.ExprVar cB }; { Kind = ExprKind.ExprVar mB }; nBExpr] ->
        let staticN what e =
            match evalExpr ctx.Statics maxSteps e with
            | Ok (SVInt n) when n >= 1L -> Ok (float n)
            | _ -> Error (sprintf "comoments_merge: %s must be a compile-time chunk size >= 1" what)
        staticN "nA" nAExpr |> Result.bind (fun nA ->
        staticN "nB" nBExpr |> Result.map (fun nB ->
            let n = nA + nB
            let deltaN = sprintf "__ppl_delta_%s" outName
            let ddLN = sprintf "__ppl_ddL_%s" outName
            let ddKN = sprintf "__ppl_ddk_%s" outName
            let ddN = sprintf "__ppl_dd_%s" outName
            let mkDecl pat value = { Value = DeclLet { Pattern = pvar pat; Type = None; Value = value; Mutability = BindLet }; Span = span }
            // δ = mB − mA (lockstep over the mean vectors)
            let deltaDecl = mkDecl deltaN (zipMap2 (v mA) (v mB) (subE (v "__w") (v "__u")))
            match Map.tryFind cA ctx.FlatDims.Value, Map.tryFind cB ctx.FlatDims.Value with
            | Some dA, Some dB when dA = dB ->
                // FLAT INPUTS (pool-path comoments / earlier flat merges):
                // fully cell-wise merge — δδᵀ inlined per cell, flat lex
                // reads on both chunks (the mstate_merge house style).
                let d = dA
                let dRead i = appE (v deltaN) [iLit i]
                let cells = canonicalTuples d 2
                let merged =
                    arrLitE
                        [ for k in 0 .. cells.Length - 1 ->
                            let (i, j) = (match cells.[k] with [i; j] -> (i, j) | _ -> (0, 0))
                            addE
                                (divE (addE (mulE (fLit nA) (appE (v cA) [iLit k]))
                                            (mulE (fLit nB) (appE (v cB) [iLit k]))) (fLit n))
                                (mulE (fLit (nA * nB / (n * n))) (mulE (dRead i) (dRead j))) ]
                ctx.FlatDims.Value <- Map.add outName d ctx.FlatDims.Value
                [ deltaDecl
                  { Value = DeclLet { binding with Value = merged }; Span = span } ]
            | _ ->
                // δδᵀ as a packed symmetric outer square (scalar comm kernel)
                let ddL = mkDecl ddLN (methodForE [v deltaN; v deltaN])
                let ddK = mkDecl ddKN (lambdaE [{ Name = "__a"; Type = None }; { Name = "__b"; Type = None }]
                                               (commWhere ["__a"; "__b"])
                                               (mulE (v "__a") (v "__b")))
                let dd = mkDecl ddN (computeE (applyE (v ddLN) (v ddKN)))
                // merged = (nA·CA + nB·CB)/n + (nA·nB/n²)·δδᵀ, three-way lockstep
                let body =
                    addE
                        (divE (addE (mulE (fLit nA) (v "__ca")) (mulE (fLit nB) (v "__cb"))) (fLit n))
                        (mulE (fLit (nA * nB / (n * n))) (v "__dd"))
                let merged =
                    computeE (applyE
                        (methodForE [syn (ExprZip [v cA; v cB; v ddN])])
                        (lambdaE [{ Name = "__ca"; Type = None }; { Name = "__cb"; Type = None }; { Name = "__dd"; Type = None }] None body))
                [ deltaDecl; ddL; ddK; dd
                  { Value = DeclLet { binding with Value = merged }; Span = span } ]))
    | _ ->
        Error "comoments_merge expects comoments_merge(cA, mA, nA, cB, mB, nB): two chunks' pair comoments, means, and static sizes"

// (elabCumulantAccess was removed with the typed-Dist arc: cumulant(d, k)
// is now TypeCheck.inferCumulantProj — a checker-level projection on the
// Dist value bound by distPackDecl, with the order guard as a type error.)

// ============================================================================
// Arbitrary-order streaming state: the Pébay generalization of the k = 2
// merge (prototype 2's DERIVED kernels as a compile-time pass; see
// ppl/Streaming.fs — the oracle). State = (n static, mean vector, central
// comoment SUMS M_2..M_r). Merge, for every canonical entry S with
// δ = meanB − meanA, cA = −nB/n, cB = nA/n:
//   M'_S = Σ_{K⊆S, |S\K|≠1} M_{S\K}(A)·Π_{k∈K}(cA·δ_k)
//                          + M_{S\K}(B)·Π_{k∈K}(cB·δ_k)
// with M_∅ = n_side and M_single = 0 (pruned). Everything is static
// (d, r, chunk sizes), so merge and finalize generate CELL-WISE
// straight-line code: packed method_for tensors read logically
// (M(i, j, ...) — the CNS placement canonicalizes), merge-emitted flat
// arrays read at the lex offset of the sorted label tuple.
// ============================================================================

// (iLit and canonicalTuples moved up beside the pool machinery.)
let private lexOffsetOf (d: int) (p: int) (labels: int list) : int =
    canonicalTuples d p |> List.findIndex (fun t -> t = List.sort labels)

type private MStateInfo = {
    Order: int
    Dim: int
    /// Static observation count, carried through merges.
    N: float
    Mean: string
    /// M tensor binding name per rank p (index p-2), p = 2..Order.
    Ms: string list
    /// True: method_for-packed tensors (logical multi-index reads).
    /// False: merge-emitted flat arrays in lex cell order (offset reads).
    Packed: bool
}

let private mstateComponent (sName: string) (what: string) = sprintf "__mst_%s_%s" sName what

/// Read M_S for |S| >= 2 from a state, representation-aware.
let private mReadExpr (info: MStateInfo) (labels: int list) : Expr =
    let p = labels.Length
    let name = info.Ms.[p - 2]
    if info.Packed then appE (v name) (labels |> List.map iLit)
    else appE (v name) [iLit (lexOffsetOf info.Dim p labels)]

// (centralSumKernelBody — the per-cell kernel expansion of the central
// comoment sum — was deleted with the single-pass pool: elabMState now
// generates the same inclusion–exclusion cell-wise over pool reads.)

let private elabMState (ctx: Ctx) (span: Span) (sName: string) (args: Expr list)
    : Result<Located<Decl> list * MStateInfo, string> =
    match args with
    | [{ Kind = ExprKind.ExprVar aName }; rExpr] ->
        let r =
            match evalExpr ctx.Statics maxSteps rExpr with
            | Ok (SVInt x) when x >= 2L && x <= 6L -> Ok (int x)
            | _ -> Error "mstate: the order must be a compile-time integer in 2..6"
        r |> Result.bind (fun r ->
        arrayShape ctx "mstate" aName |> Result.bind (fun (elem, leading, fiber, n) ->
            match leading with
            | [ix] ->
                match resolveExtent ctx.Aliases ctx.Statics ix with
                | Some d ->
                    // SINGLE-PASS PATH: the module's shared pool sweep, then
                    // mean and every central comoment SUM as straight-line
                    // cells — M_S = Σ_{K⊆S} (−1)^{|K|} Π_{i∈K} μ_i · P_{S\K}
                    // with P_∅ = n (the same inclusion–exclusion the per-cell
                    // kernels expanded, minus their re-run prodsum loops and
                    // per-kernel mean recomputation). State components are
                    // FLAT lex ArrayLits (Packed = false), the same
                    // representation merge outputs already carry.
                    let (pd, pool) = acquirePool ctx span aName d (float n) r
                    let meanN = mstateComponent sName "mean"
                    let mN p = mstateComponent sName (sprintf "m%d" p)
                    let mkDecl name value = { Value = DeclLet { Pattern = pvar name; Type = None; Value = value; Mutability = BindLet }; Span = span }
                    let meanDecl = mkDecl meanN (arrLitE [ for i in 0 .. d - 1 -> poolMoment pool [i] ])
                    let mDecls =
                        [ for p in 2 .. r ->
                            let cells =
                                [ for labels in canonicalTuples d p ->
                                    let labArr = List.toArray labels
                                    let terms =
                                        [ for mask in 0 .. (1 <<< p) - 1 ->
                                            let inK = [ for i in 0 .. p - 1 do if (mask >>> i) &&& 1 = 1 then yield labArr.[i] ]
                                            let rest = [ for i in 0 .. p - 1 do if (mask >>> i) &&& 1 = 0 then yield labArr.[i] ]
                                            let sign = if inK.Length % 2 = 0 then 1.0 else -1.0
                                            let ps = if rest.IsEmpty then fLit (float n) else poolRead pool rest
                                            let muProd = inK |> List.fold (fun acc i -> mulE acc (poolMoment pool [i])) (fLit sign)
                                            mulE muProd ps ]
                                    terms |> List.reduce addE ]
                            mkDecl (mN p) (arrLitE cells) ]
                    Ok (pd @ [meanDecl] @ mDecls, { Order = r; Dim = d; N = float n; Mean = meanN; Ms = [ for p in 2 .. r -> mN p ]; Packed = false })
                | None -> Error "mstate: the variable-axis extent must be statically known"
            | _ -> Error "mstate: one variable axis per array so far (multi-axis states deferred)"))
    | _ ->
        Error "mstate expects mstate(A, r): an annotated module-level array and a static order in 2..6"

let private elabMStateMerge (ctx: Ctx) (span: Span) (outName: string)
    (mstates: Map<string, MStateInfo>) (args: Expr list)
    : Result<Located<Decl> list * MStateInfo, string> =
    match args with
    | [{ Kind = ExprKind.ExprVar sa }; { Kind = ExprKind.ExprVar sb }] ->
        match Map.tryFind sa mstates, Map.tryFind sb mstates with
        | Some a, Some b when a.Order = b.Order && a.Dim = b.Dim ->
            let n = a.N + b.N
            let cA = -b.N / n
            let cB = a.N / n
            let deltaN = mstateComponent outName "delta"
            let meanN = mstateComponent outName "mean"
            let mN p = mstateComponent outName (sprintf "m%d" p)
            let dRead lbl = appE (v deltaN) [iLit lbl]
            let mSide (info: MStateInfo) (labels: int list) =
                if labels.IsEmpty then fLit info.N else mReadExpr info labels
            let mkDecl name value = { Value = DeclLet { Pattern = pvar name; Type = None; Value = value; Mutability = BindLet }; Span = span }
            let deltaDecl = mkDecl deltaN (zipMap2 (v a.Mean) (v b.Mean) (subE (v "__w") (v "__u")))
            let meanDecl = mkDecl meanN (zipMap2 (v a.Mean) (v b.Mean) (addE (v "__u") (mulE (fLit (b.N / n)) (subE (v "__w") (v "__u")))))
            let mDecls =
                [ for p in 2 .. a.Order ->
                    let cells =
                        [ for labels in canonicalTuples a.Dim p ->
                            let labArr = List.toArray labels
                            let terms =
                                [ for mask in 0 .. (1 <<< p) - 1 do
                                    let inK = [ for i in 0 .. p - 1 do if (mask >>> i) &&& 1 = 1 then yield labArr.[i] ]
                                    let rest = [ for i in 0 .. p - 1 do if (mask >>> i) &&& 1 = 0 then yield labArr.[i] ]
                                    if rest.Length <> 1 then  // M_single = 0: pruned at elaboration
                                        let deltaProd (c: float) =
                                            inK |> List.fold (fun acc lbl -> mulE acc (dRead lbl)) (fLit (c ** float inK.Length))
                                        yield addE (mulE (mSide a rest) (deltaProd cA))
                                                   (mulE (mSide b rest) (deltaProd cB)) ]
                            terms |> List.reduce addE ]
                    mkDecl (mN p) (arrLitE cells) ]
            let info = { Order = a.Order; Dim = a.Dim; N = n; Mean = meanN; Ms = [ for p in 2 .. a.Order -> mN p ]; Packed = false }
            Ok ([deltaDecl; meanDecl] @ mDecls, info)
        | Some a, Some b ->
            Error (sprintf "mstate_merge: shapes disagree (order %d vs %d, dim %d vs %d)" a.Order b.Order a.Dim b.Dim)
        | _ ->
            Error "mstate_merge expects two previously declared mstate(...) bindings"
    | _ ->
        Error "mstate_merge expects mstate_merge(sA, sB)"

/// Freeze a state into cumulant tensors: central μ_p = M_p / n (μ_1 = 0),
/// then the partition formula restricted to partitions with NO singleton
/// blocks (a singleton block carries μ_1 = 0 and kills its term); κ_1 is
/// the mean. Destructuring surface: `let (k1, ..., kr) = mstate_cumulants(s)`.
let private elabMStateCumulants (ctx: Ctx) (span: Span) (binding: Binding)
    (mstates: Map<string, MStateInfo>) (args: Expr list)
    : Result<Located<Decl> list, string> =
    match args with
    | [{ Kind = ExprKind.ExprVar sn }] ->
        match Map.tryFind sn mstates with
        | Some s ->
            let compNames =
                match binding.Pattern.Kind with
                | PatternKind.PatTuple pats when pats.Length = s.Order ->
                    let names = pats |> List.map (fun p -> match p.Kind with PatternKind.PatVar nm -> Some nm | _ -> None)
                    if names |> List.forall Option.isSome then Ok (names |> List.map Option.get)
                    else Error "mstate_cumulants: destructure into plain names"
                | _ ->
                    Error (sprintf "mstate_cumulants: destructure the result — `let (k1, ..., k%d) = mstate_cumulants(%s)`" s.Order sn)
            compNames |> Result.map (fun names ->
                let mkDecl name value = { Value = DeclLet { Pattern = pvar name; Type = None; Value = value; Mutability = BindLet }; Span = span }
                let muE (labels: int list) = divE (mReadExpr s labels) (fLit s.N)
                let kDecl p name =
                    if p = 1 then mkDecl name (v s.Mean)
                    else
                        let cells =
                            [ for labels in canonicalTuples s.Dim p ->
                                let labArr = List.toArray labels
                                let parts =
                                    setPartitions p
                                    |> List.filter (fun pt -> pt |> List.forall (fun blk -> blk.Length >= 2))
                                let terms =
                                    [ for pt in parts ->
                                        let b = pt.Length
                                        let w = (if b % 2 = 1 then 1.0 else -1.0) * factorial (b - 1)
                                        pt |> List.fold (fun acc blk ->
                                            mulE acc (muE (blk |> List.map (fun pos -> labArr.[pos])))) (fLit w) ]
                                terms |> List.reduce addE ]
                        mkDecl name (arrLitE cells)
                names |> List.mapi (fun i nm -> kDecl (i + 1) nm))
        | None ->
            Error "mstate_cumulants expects a previously declared mstate(...) binding"
    | _ ->
        Error "mstate_cumulants expects mstate_cumulants(s)"

// ============================================================================
// The closing formers: moment reconstruction (Wick under closure), mixed
// cumulant blocks, affine pushforward, and the non-crossing (free) lattice.
// All cell-wise straight-line generation over static shapes, reading dist
// components with logical multi-index subscripts (packed, order >= 2) or
// plain subscripts (order 1 / flat outputs).
// ============================================================================

/// Read the order-q cumulant of an elaboration-registry dist at labels:
/// κ1 is a plain rank-1 array; κ_{q>=2} are method_for-packed (logical
/// reads) — unless the dist carries FLAT components (pushforward results),
/// which are lex-canonical ArrayLits read by offset.
let private distKappaRead (info: DistInfo) (labels: int list) : Expr =
    let q = labels.Length
    if info.Flat && q >= 2 then
        let d = defaultArg info.Dim 1
        appE (v info.Components.[q - 1]) [iLit (lexOffsetOf d q labels)]
    else
        appE (v info.Components.[q - 1]) (labels |> List.map iLit)

/// moments(d, k) on a dist binding: reconstruct the order-k RAW moment
/// tensor from carried cumulants — μ_S = Σ over set partitions of S:
/// Π_blocks κ_{|B|}(labels at B), with κ beyond the carried order treated
/// as ZERO (the dist's implied closure: order-2 ⇒ Gaussian ⇒ this IS
/// Wick's theorem, the sum over pairings-and-singletons). Exact when
/// k <= carried order; a documented truncation beyond it.
let private elabMomentsOfDist (ctx: Ctx) (span: Span) (binding: Binding)
    (info: DistInfo) (dim: int) (kExpr: Expr)
    : Result<Located<Decl> list, string> =
    match evalExpr ctx.Statics maxSteps kExpr with
    | Ok (SVInt kk) when kk >= 1L && kk <= 8L ->
        let k = int kk
        let cells =
            [ for labels in canonicalTuples dim k ->
                let labArr = List.toArray labels
                let parts =
                    setPartitions k
                    |> List.filter (fun pt -> pt |> List.forall (fun blk -> blk.Length <= info.Order))
                let terms =
                    [ for pt in parts ->
                        pt |> List.fold (fun acc blk ->
                            mulE acc (distKappaRead info (blk |> List.map (fun pos -> labArr.[pos])))) (fLit 1.0) ]
                match terms with
                | [] -> fLit 0.0   // every partition needs a block > carried order
                | _ -> terms |> List.reduce addE ]
        Ok [ { Value = DeclLet { binding with Value = arrLitE cells }; Span = span } ]
    | _ -> Error "moments: on a dist, the order must be a compile-time integer in 1..8"

/// The dist's variable dimension: an explicit override (pushforward
/// results) wins; otherwise derived off the order-1 component's source
/// array. Registry-level v1: derivation needs a dist(A, r) over a
/// single-leading-axis array (the same constraint the streaming state has).
let private distDim (ctx: Ctx) (info: DistInfo) : Result<int, string> =
    match info.Dim with
    | Some d -> Ok d
    | None ->
    match Set.toList info.Sources with
    | [one] ->
        match Map.tryFind one ctx.Arrays with
        | Some (_, idxs) when idxs.Length = 2 ->
            match resolveExtent ctx.Aliases ctx.Statics idxs.Head with
            | Some d -> Ok d
            | None -> Error "dist reconstruction: the source array's variable-axis extent must be statically known"
        | _ -> Error "dist reconstruction: the source array needs one variable axis plus the sample axis"
    | _ -> Error "dist reconstruction: supported for single-source dists so far (sums/scales of dists carry derived components; project with cumulant(d, k) instead)"

/// mixed_cumulants(X, Y, p, q): the (p, q) mixed joint-cumulant block —
/// method_for(X ×p, Y ×q) with per-array comm groups; the kernel is the
/// same partition sum over ALL p+q positions. Output is slot-major:
/// packed SymIdx<p, dX> outer, packed SymIdx<q, dY> inner. A declared
/// independent(X, Y) makes every mixed cumulant (p, q >= 1) EXACTLY zero
/// at every order — the block elaborates to a literal zero array.
let private elabMixedCumulants (ctx: Ctx) (span: Span) (outName: string) (binding: Binding) (args: Expr list)
    : Result<Located<Decl> list, string> =
    match args with
    | [{ Kind = ExprKind.ExprVar xName }; { Kind = ExprKind.ExprVar yName }; pExpr; qExpr] ->
        let staticOrd what e =
            match evalExpr ctx.Statics maxSteps e with
            | Ok (SVInt x) when x >= 1L && x <= 5L -> Ok (int x)
            | _ -> Error (sprintf "mixed_cumulants: %s must be a compile-time integer in 1..5" what)
        staticOrd "p" pExpr |> Result.bind (fun p ->
        staticOrd "q" qExpr |> Result.bind (fun q ->
        arrayShape ctx "mixed_cumulants" xName |> Result.bind (fun (elemX, leadX, fibX, nX) ->
        arrayShape ctx "mixed_cumulants" yName |> Result.bind (fun (_elemY, leadY, fibY, nY) ->
            if nX <> nY then
                Error (sprintf "mixed_cumulants: '%s' and '%s' sample axes disagree (%d vs %d)" xName yName nX nY)
            elif Set.contains (indepKey xName yName) ctx.Indep then
                // Structural sparsity at ALL orders: cumulants factor over
                // independent subalgebras, so any block touching both is 0.
                match leadX, leadY with
                | [ix], [iy] ->
                    match resolveExtent ctx.Aliases ctx.Statics ix, resolveExtent ctx.Aliases ctx.Statics iy with
                    | Some dx, Some dy ->
                        let cellCount =
                            (canonicalTuples dx p |> List.length) * (canonicalTuples dy q |> List.length)
                        let zeros = arrLitE (List.replicate cellCount (fLit 0.0))
                        Ok [ { Value = DeclLet { binding with Value = zeros }; Span = span } ]
                    | _ -> Error "mixed_cumulants: independent zero block needs static variable-axis extents"
                | _ -> Error "mixed_cumulants: independent zero blocks support one variable axis per array so far"
            else
                let r = p + q
                match leadX, leadY with
                | [ixx], [ixy] when (resolveExtent ctx.Aliases ctx.Statics ixx).IsSome
                                     && (resolveExtent ctx.Aliases ctx.Statics ixy).IsSome ->
                    // SINGLE-PASS PATH: one JOINT pool over both arrays'
                    // rows (X at positions 0..dx-1, Y at dx..dx+dy-1); the
                    // needed multisets are collected from the cells' actual
                    // partition blocks. Output stays slot-major flat (X
                    // canonical outer, Y canonical inner — lex, matching
                    // the packed emission's print order).
                    let dx = (resolveExtent ctx.Aliases ctx.Statics ixx).Value
                    let dy = (resolveExtent ctx.Aliases ctx.Statics ixy).Value
                    let rows = rowSlices xName dx @ rowSlices yName dy
                    let cellLabels =
                        [ for xl in canonicalTuples dx p do
                            for yl in canonicalTuples dy q ->
                              List.toArray (xl @ (yl |> List.map (fun j -> dx + j))) ]
                    let needed =
                        cellLabels |> List.collect (fun labArr ->
                            setPartitions r |> List.collect (fun pt ->
                                pt |> List.map (fun blk -> blk |> List.map (fun pos -> labArr.[pos]) |> List.sort)))
                    let (pd, pool) = poolDecls span outName rows needed (float nX)
                    let cells = [ for labArr in cellLabels -> cumulantCellExpr pool labArr r ]
                    Ok (pd @ [ { Value = DeclLet { binding with Value = arrLitE cells }; Span = span } ])
                | _ ->
                    let lName = sprintf "__ppl_L_%s" outName
                    let kName = sprintf "__ppl_k_%s" outName
                    let xParams = [ for i in 1 .. p -> sprintf "__x%d" i ]
                    let yParams = [ for i in p + 1 .. r -> sprintf "__x%d" i ]
                    let ps =
                        (xParams |> List.map (fun nm -> { Name = nm; Type = Some (TyArray (elemX, [fibX])) }))
                        @ (yParams |> List.map (fun nm -> { Name = nm; Type = Some (TyArray (elemX, [fibY])) }))
                    let commGroups = [ xParams; yParams ] |> List.filter (fun g -> g.Length >= 2)
                    let whereC =
                        if commGroups.IsEmpty then None
                        else Some { Commutativity = commGroups; Parallel = []; TDims = []; Custom = [] }
                    let mkDecl name value = { Value = DeclLet { Pattern = pvar name; Type = None; Value = value; Mutability = BindLet }; Span = span }
                    Ok [ mkDecl lName (methodForE ((List.replicate p (v xName)) @ (List.replicate q (v yName))))
                         mkDecl kName (lambdaE ps whereC (cumulantKernelBody r (float nX)))
                         { Value = DeclLet { binding with Value = computeE (applyE (v lName) (v kName)) }; Span = span } ]))))
    | _ ->
        Error "mixed_cumulants expects mixed_cumulants(X, Y, p, q): two annotated arrays and static per-array orders"

/// dist_affine(W, d): multilinearity under a linear map — κ'_k = W^⊗k κ_k,
/// the exact-linear case of the Faà di Bruno pushforward. W is an annotated
/// m×n module array read at runtime (W(i, j)); the contraction unrolls
/// cell-wise over the static shape. Destructuring surface:
/// `let (p1, ..., pr) = dist_affine(W, d)`.
let private elabDistAffine (ctx: Ctx) (span: Span) (binding: Binding)
    (dists: Map<string, DistInfo>) (args: Expr list)
    : Result<Located<Decl> list, string> =
    match args with
    | [{ Kind = ExprKind.ExprVar wName }; { Kind = ExprKind.ExprVar dn }] ->
        match Map.tryFind dn dists with
        | None -> Error "dist_affine expects dist_affine(W, d) with a previously declared dist binding d"
        | Some info ->
            distDim ctx info |> Result.bind (fun n ->
            match Map.tryFind wName ctx.Arrays with
            | Some (_, [im; inn]) ->
                match resolveExtent ctx.Aliases ctx.Statics im, resolveExtent ctx.Aliases ctx.Statics inn with
                | Some m, Some nCols when nCols = n ->
                    let compNames =
                        match binding.Pattern.Kind with
                        | PatternKind.PatTuple pats when pats.Length = info.Order ->
                            let names = pats |> List.map (fun p -> match p.Kind with PatternKind.PatVar nm -> Some nm | _ -> None)
                            if names |> List.forall Option.isSome then Ok (names |> List.map Option.get)
                            else Error "dist_affine: destructure into plain names"
                        | _ -> Error (sprintf "dist_affine: destructure the result — `let (p1, ..., p%d) = dist_affine(%s, %s)`" info.Order wName dn)
                    compNames |> Result.map (fun names ->
                        let wRead i j = appE (v wName) [iLit i; iLit j]
                        // all index tuples over [0, n)^k (order matters for the
                        // W factors; κ reads canonicalize the j-tuple)
                        let rec jTuples k = if k = 0 then [ [] ] else [ for j in 0 .. n - 1 do for rest in jTuples (k - 1) -> j :: rest ]
                        names |> List.mapi (fun ki nm ->
                            let k = ki + 1
                            let cells =
                                [ for iLabels in canonicalTuples m k ->
                                    let iArr = List.toArray iLabels
                                    let terms =
                                        [ for js in jTuples k ->
                                            let jArr = List.toArray js
                                            let wProd =
                                                [ 0 .. k - 1 ]
                                                |> List.fold (fun acc l -> mulE acc (wRead iArr.[l] jArr.[l])) (fLit 1.0)
                                            mulE wProd (distKappaRead info (List.sort js)) ]
                                    terms |> List.reduce addE ]
                            { Value = DeclLet { Pattern = pvar nm; Type = None; Value = arrLitE cells; Mutability = BindLet }; Span = span }))
                | Some _, Some nCols ->
                    Error (sprintf "dist_affine: W's column count (%d) must match the dist's dimension (%d)" nCols n)
                | _ -> Error "dist_affine: W's extents must be statically known"
            | _ -> Error "dist_affine: W must be an annotated module-level m×n array (Array<Elem like Idx<m>, Idx<n>>)")
    | _ ->
        Error "dist_affine expects dist_affine(W, d)"

/// dist_jet(d, q, g0, D1, ..., Ds): the FULL Faà di Bruno pushforward,
/// scalar output — Y = g(X) for a smooth g supplied as its degree-s jet AT
/// THE DIST'S MEAN: g0 = g(μ) (scalar expr) and D_k = g^(k)(μ), the rank-k
/// symmetric derivative tensor (a scalar expr when the dist is univariate;
/// otherwise a named C(d+k−1,k)-cell rank-1 array or an inline array
/// literal, cells in canonical lex order — the flat order packed tensors
/// print in). Y − g0 is the Taylor polynomial in Z = X − μ, so:
///   central moments of X  = partition sums over κ, every block size ≥ 2
///   raw moments of Y − g0 = multinomial over jet-degree compositions,
///                           derivative reads × central-moment reads
///   κ(Y)                  = univariate Möbius inversion; κ_1 shifts by g0
/// all emitted as straight-line scalar lets over the input dist's component
/// reads — derivative VALUES are runtime expressions, the contraction
/// structure is static (the dist_affine split, one degree higher). Exact
/// for polynomial g of degree ≤ s when q·s ≤ the carried order; the strict
/// form demands that budget, dist_jet_closed zero-fills cumulants beyond
/// it (the moments(d,k) closure convention: overlarge partition blocks are
/// dropped). Result: a univariate order-q dist, registered with inherited
/// sources and FLAT 1-cell components.
/// VECTOR OUTPUT (the mixed-block Faà di Bruno): g : R^dim → R^m supplied
/// as g0 = the m-cell vector of g_a(μ) and per-order COORDINATE-MAJOR flat
/// derivative slots — cell (a, tuple) of D_k at offset a·C(dim+k−1,k) +
/// lexOffsetOf. Joint raw moments of Y′ = Y − g0, E[∏_j Y′_{a_j}], expand
/// over ORDERED per-factor degree assignments (k_1..k_p) ∈ [1..s]^p with
/// weight ∏_j 1/k_j! (the univariate composition-multinomial is exactly
/// this sum with identical factors collected); factor j reads D_{k_j} of
/// coordinate a_j against the CENTRAL moments of all t = Σ k_j input
/// labels; the multivariate Möbius inversion over output positions returns
/// the JOINT output cumulant tensors, flat in canonical lex order over m
/// (κ_1 shifts back by g0). Registered Dim = m, Flat — distKappaRead's
/// flat multivariate reads and the cumulant flat-projection arm compose
/// unchanged. Differentially pinned against the prototype's
/// jetPushforwardVec (dump-jet J5/J6). Generated-code size: the κ pool /
/// central-moment discipline is shared with the scalar path; m multiplies
/// only the my/component decls by C(m+k−1,k) — the q·s ≤ 8 cap fences the
/// partition blowup exactly as before.
let private elabDistJetVec (closed: bool) (former: string) (ctx: Ctx) (span: Span) (dName: string)
    (info: DistInfo) (dim: int) (m: int) (qExpr: Expr) (g0Expr: Expr) (dArgs: Expr list)
    : Result<Located<Decl> list * DistInfo, string> =
            let qRes =
                match evalExpr ctx.Statics maxSteps qExpr with
                | Ok (SVInt x) when x >= 1L && x <= 6L -> Ok (int x)
                | _ -> Error (sprintf "%s: the output order q must be a compile-time integer in 1..6" former)
            qRes |> Result.bind (fun q ->
            let s = dArgs.Length
            let tMax = q * s
            if not closed && tMax > info.Order then
                Error (sprintf "%s: computing %d output cumulants through a degree-%d jet needs input order %d but the dist carries %d — insufficient stochastic order. Carry more or accept the truncation explicitly with %s_closed(...)" former q s tMax info.Order former)
            elif closed && tMax > 8 then
                Error (sprintf "%s: q·s = %d exceeds the generation bound (8) — lower the output order or the jet degree" former tMax)
            else
                let cellsOf k = canonicalTuples dim k |> List.length
                let g0Read (a: int) : Expr =
                    match g0Expr.Kind with
                    | ExprKind.ExprArrayLit cells -> cells.[a]
                    | _ -> appE g0Expr [iLit a]   // named rank-1 array of extent m
                let dReadOf (k: int) (dArg: Expr) : Result<(int -> int list -> Expr option), string> =
                    let want = m * cellsOf k
                    match dArg.Kind with
                    | ExprKind.ExprArrayLit cells when cells.Length = want ->
                        let cellArr = List.toArray cells
                        Ok (fun a labels ->
                            let cell = cellArr.[a * cellsOf k + lexOffsetOf dim k labels]
                            match cell.Kind with
                            | ExprKind.ExprLit (LitFloat 0.0) -> None
                            | _ -> Some cell)
                    | ExprKind.ExprArrayLit cells ->
                        Error (sprintf "%s: vector D%d needs %d cells (coordinate-major: m = %d outer × %d canonical cells over dim %d), got %d" former k want m (cellsOf k) dim cells.Length)
                    | ExprKind.ExprVar w ->
                        (match Map.tryFind w ctx.Arrays with
                         | Some (_, [ix]) when resolveExtent ctx.Aliases ctx.Statics ix = Some want ->
                             Ok (fun a labels -> Some (appE (v w) [iLit (a * cellsOf k + lexOffsetOf dim k labels)]))
                         | Some _ ->
                             Error (sprintf "%s: vector D%d ('%s') must be a rank-1 array of %d coordinate-major cells" former k w want)
                         | None ->
                             Error (sprintf "%s: vector D%d ('%s') must be an annotated module-level array or an inline array literal" former k w))
                    | _ ->
                        Error (sprintf "%s: with a vector g0 (m = %d), D%d must be a named array or an array literal of %d coordinate-major cells" former m k (m * cellsOf k))
                let dReadsRes =
                    dArgs
                    |> List.mapi (fun i a -> dReadOf (i + 1) a)
                    |> List.fold (fun acc r -> acc |> Result.bind (fun rs -> r |> Result.map (fun f -> rs @ [f])))
                                 (Ok [])
                dReadsRes |> Result.map (fun dReadFns ->
                    let dRead (k: int) (a: int) (labels: int list) = dReadFns.[k - 1] a labels
                    let mkDecl name value =
                        { Value = DeclLet { Pattern = pvar name; Type = None; Value = value; Mutability = BindLet }; Span = span }
                    let kappaName kk ci = sprintf "__ppl_jetk_%s_o%d_c%d" dName kk ci
                    let cmName t ci = sprintf "__ppl_jetcm_%s_t%d_c%d" dName t ci
                    let myName k ci = sprintf "__ppl_jetmy_%s_o%d_c%d" dName k ci
                    let partsOf t =
                        setPartitions t
                        |> List.filter (fun pt ->
                            pt |> List.forall (fun blk -> blk.Length >= 2 && blk.Length <= info.Order))
                    // κ pool + central moments: IDENTICAL to the scalar path
                    // (input-side, output-agnostic; every κ cell bound ONCE).
                    let neededKappa =
                        [ for t in 2 .. tMax do
                            for labels in canonicalTuples dim t do
                                let labArr = List.toArray labels
                                for pt in partsOf t do
                                    for blk in pt do
                                        yield (blk.Length, blk |> List.map (fun pos -> labArr.[pos]) |> List.sort) ]
                        |> List.distinct
                    let kappaDecls =
                        [ for (kk, sub) in neededKappa ->
                            mkDecl (kappaName kk (lexOffsetOf dim kk sub)) (distKappaRead info sub) ]
                    let kappaRead (sub: int list) =
                        v (kappaName sub.Length (lexOffsetOf dim sub.Length sub))
                    let cmDecls =
                        [ for t in 2 .. tMax do
                            yield! canonicalTuples dim t |> List.mapi (fun ci labels ->
                                let labArr = List.toArray labels
                                let terms =
                                    [ for pt in partsOf t ->
                                        pt |> List.fold (fun acc blk ->
                                            mulE acc (kappaRead (blk |> List.map (fun pos -> labArr.[pos]) |> List.sort))) (fLit 1.0) ]
                                let value = match terms with [] -> fLit 0.0 | _ -> terms |> List.reduce addE
                                mkDecl (cmName t ci) value) ]
                    let cmRead (labels: int list) =
                        v (cmName labels.Length (lexOffsetOf dim labels.Length labels))
                    let rec tuples (k: int) : int list list =
                        if k = 0 then [ [] ]
                        else [ for lab in 0 .. dim - 1 do for rest in tuples (k - 1) -> lab :: rest ]
                    // ordered per-factor degree assignments (k_1..k_p) ∈ [1..s]^p
                    let rec degAssigns (p: int) : int list list =
                        if p = 0 then [ [] ]
                        else [ for kj in 1 .. s do for rest in degAssigns (p - 1) -> kj :: rest ]
                    // joint raw moments of Y′ over canonical OUTPUT tuples
                    let myDecls =
                        [ for k in 1 .. q do
                            yield! canonicalTuples m k |> List.mapi (fun ci oLabels ->
                                let oArr = List.toArray oLabels
                                let terms =
                                    [ for degs in degAssigns k do
                                        let t = List.sum degs
                                        if t >= 2 then   // t = 1 ⇒ D_1·E[Z] = 0
                                            let w = degs |> List.fold (fun acc kj -> acc / factorial kj) 1.0
                                            // factor fi reads D_{k_fi} of coordinate oArr.[fi];
                                            // literal-zero derivative cells prune
                                            let rec go (fi: int) (ds: int list) : (Expr list * int list) list =
                                                match ds with
                                                | [] -> [ ([], []) ]
                                                | kj :: rest ->
                                                    let restA = go (fi + 1) rest
                                                    [ for tup in tuples kj do
                                                        match dRead kj oArr.[fi] tup with
                                                        | Some de ->
                                                            for (es, ls) in restA do
                                                                yield (de :: es, tup @ ls)
                                                        | None -> () ]
                                            let assigns = go 0 degs
                                            if not assigns.IsEmpty then
                                                let sum =
                                                    assigns
                                                    |> List.map (fun (des, ls) -> des |> List.fold mulE (cmRead ls))
                                                    |> List.reduce addE
                                                yield mulE (fLit w) sum ]
                                mkDecl (myName k ci) (match terms with [] -> fLit 0.0 | _ -> terms |> List.reduce addE)) ]
                    let myRead (sub: int list) =
                        v (myName sub.Length (lexOffsetOf m sub.Length sub))
                    // joint κ(Y) by multivariate Möbius over output positions;
                    // order-1 cells shift by g0
                    let compDecls =
                        [ for k in 1 .. q ->
                            let cells =
                                [ for oLabels in canonicalTuples m k ->
                                    let oArr = List.toArray oLabels
                                    let mobius =
                                        setPartitions k
                                        |> List.map (fun pt ->
                                            let b = pt.Length
                                            let w = (if b % 2 = 1 then 1.0 else -1.0) * factorial (b - 1)
                                            pt |> List.fold (fun acc blk ->
                                                mulE acc (myRead (blk |> List.map (fun pos -> oArr.[pos]) |> List.sort))) (fLit w))
                                        |> List.reduce addE
                                    if k = 1 then addE mobius (g0Read oArr.[0]) else mobius ]
                            mkDecl (distComponentName dName k) (arrLitE cells) ]
                    let outInfo = { Order = q
                                    Components = [ for k in 1 .. q -> distComponentName dName k ]
                                    Sources = info.Sources
                                    Dim = Some m
                                    Flat = true }
                    (kappaDecls @ cmDecls @ myDecls @ compDecls, outInfo)))

let private elabDistJet (closed: bool) (former: string) (ctx: Ctx) (span: Span) (dName: string)
    (dists: Map<string, DistInfo>) (args: Expr list)
    : Result<Located<Decl> list * DistInfo, string> =
    match args with
    | { Kind = ExprKind.ExprVar dn } :: qExpr :: g0Expr :: dArgs when not dArgs.IsEmpty ->
        match Map.tryFind dn dists with
        | None -> Error (sprintf "%s expects %s(d, q, g0, D1, ..., Ds) with a previously declared dist binding d" former former)
        | Some info ->
            distDim ctx info |> Result.bind (fun dim ->
            // VECTOR MODE is keyed off g0's shape: an m-cell (m ≥ 2) array
            // literal or a named annotated rank-1 array of static extent m
            // means g : R^dim → R^m with joint output cumulants; a scalar
            // g0 expression keeps the univariate path byte-identical.
            let vecM =
                match g0Expr.Kind with
                | ExprKind.ExprArrayLit cells -> Some cells.Length
                | ExprKind.ExprVar w ->
                    (match Map.tryFind w ctx.Arrays with
                     | Some (_, [ix]) -> resolveExtent ctx.Aliases ctx.Statics ix
                     | _ -> None)
                | _ -> None
            match vecM with
            | Some m when m >= 2 ->
                elabDistJetVec closed former ctx span dName info dim m qExpr g0Expr dArgs
            | _ ->
            let qRes =
                match evalExpr ctx.Statics maxSteps qExpr with
                | Ok (SVInt x) when x >= 1L && x <= 6L -> Ok (int x)
                | _ -> Error (sprintf "%s: the output order q must be a compile-time integer in 1..6" former)
            qRes |> Result.bind (fun q ->
            let s = dArgs.Length
            let tMax = q * s
            if not closed && tMax > info.Order then
                Error (sprintf "%s: computing %d output cumulants through a degree-%d jet needs input order %d but '%s' carries %d — insufficient stochastic order. Carry more (dist(A, %d)) or accept the truncation explicitly with %s_closed(...)" former q s tMax dn info.Order (min tMax 6) former)
            elif closed && tMax > 8 then
                Error (sprintf "%s: q·s = %d exceeds the generation bound (8) — lower the output order or the jet degree" former tMax)
            else
                // Per-degree derivative read at a label tuple. Inline
                // literals and univariate scalar exprs SPLICE (literal-zero
                // cells prune their terms); named arrays read at the flat
                // canonical offset, values at runtime.
                let cellsOf k = canonicalTuples dim k |> List.length
                let dReadOf (k: int) (dArg: Expr) : Result<(int list -> Expr option), string> =
                    if dim = 1 then
                        match dArg.Kind with
                        | ExprKind.ExprLit (LitFloat 0.0) -> Ok (fun _ -> None)
                        | _ -> Ok (fun _ -> Some dArg)
                    else
                        match dArg.Kind with
                        | ExprKind.ExprArrayLit cells when cells.Length = cellsOf k ->
                            let cellArr = List.toArray cells
                            Ok (fun labels ->
                                let cell = cellArr.[lexOffsetOf dim k labels]
                                match cell.Kind with
                                | ExprKind.ExprLit (LitFloat 0.0) -> None
                                | _ -> Some cell)
                        | ExprKind.ExprArrayLit cells ->
                            Error (sprintf "%s: D%d needs %d cells in canonical lex order over dim %d, got %d" former k (cellsOf k) dim cells.Length)
                        | ExprKind.ExprVar w ->
                            (match Map.tryFind w ctx.Arrays with
                             | Some (_, [ix]) when resolveExtent ctx.Aliases ctx.Statics ix = Some (cellsOf k) ->
                                 Ok (fun labels -> Some (appE (v w) [iLit (lexOffsetOf dim k labels)]))
                             | Some _ ->
                                 Error (sprintf "%s: D%d ('%s') must be a rank-1 array of %d cells (canonical lex order over dim %d)" former k w (cellsOf k) dim)
                             | None ->
                                 Error (sprintf "%s: D%d ('%s') must be an annotated module-level array or an inline array literal" former k w))
                        | _ ->
                            Error (sprintf "%s: with a %d-dimensional dist, D%d must be a named array or an array literal (scalar-expr jets need a univariate dist)" former dim k)
                let dReadsRes =
                    dArgs
                    |> List.mapi (fun i a -> dReadOf (i + 1) a)
                    |> List.fold (fun acc r -> acc |> Result.bind (fun rs -> r |> Result.map (fun f -> rs @ [f])))
                                 (Ok [])
                dReadsRes |> Result.map (fun dReadFns ->
                    let dRead (k: int) (labels: int list) = dReadFns.[k - 1] labels
                    let mkDecl name value =
                        { Value = DeclLet { Pattern = pvar name; Type = None; Value = value; Mutability = BindLet }; Span = span }
                    let kappaName kk ci = sprintf "__ppl_jetk_%s_o%d_c%d" dName kk ci
                    let cmName t ci = sprintf "__ppl_jetcm_%s_t%d_c%d" dName t ci
                    let myName m = sprintf "__ppl_jetmy_%s_m%d" dName m
                    let partsOf t =
                        setPartitions t
                        |> List.filter (fun pt ->
                            pt |> List.forall (fun blk -> blk.Length >= 2 && blk.Length <= info.Order))
                    // Every κ cell the partition sums touch, bound ONCE as a
                    // scalar let (packed logical reads carry heavy canonical-
                    // placement codegen — repeating them per partition term
                    // blows the generated C++ up; ppl's pool discipline).
                    let neededKappa =
                        [ for t in 2 .. tMax do
                            for labels in canonicalTuples dim t do
                                let labArr = List.toArray labels
                                for pt in partsOf t do
                                    for blk in pt do
                                        yield (blk.Length, blk |> List.map (fun pos -> labArr.[pos]) |> List.sort) ]
                        |> List.distinct
                    let kappaDecls =
                        [ for (kk, sub) in neededKappa ->
                            mkDecl (kappaName kk (lexOffsetOf dim kk sub)) (distKappaRead info sub) ]
                    let kappaRead (sub: int list) =
                        v (kappaName sub.Length (lexOffsetOf dim sub.Length sub))
                    // central moments of X, orders 2..q·s: partition sums
                    // over the bound κ cells, singleton blocks excluded
                    // (κ_1(Z) = 0), overlarge blocks dropped only under the
                    // explicit closure
                    let cmDecls =
                        [ for t in 2 .. tMax do
                            yield! canonicalTuples dim t |> List.mapi (fun ci labels ->
                                let labArr = List.toArray labels
                                let terms =
                                    [ for pt in partsOf t ->
                                        pt |> List.fold (fun acc blk ->
                                            mulE acc (kappaRead (blk |> List.map (fun pos -> labArr.[pos]) |> List.sort))) (fLit 1.0) ]
                                let value = match terms with [] -> fLit 0.0 | _ -> terms |> List.reduce addE
                                mkDecl (cmName t ci) value) ]
                    let cmRead (labels: int list) =
                        v (cmName labels.Length (lexOffsetOf dim labels.Length labels))
                    // raw moments of Y' = Y − g0: multinomial over the
                    // ordered ways m factors distribute over jet degrees
                    let rec compositions (m: int) (t: int) : int list list =
                        if t = 1 then [ [ m ] ]
                        else [ for first in 0 .. m do for rest in compositions (m - first) (t - 1) -> first :: rest ]
                    let rec tuples (k: int) : int list list =
                        if k = 0 then [ [] ]
                        else [ for lab in 0 .. dim - 1 do for rest in tuples (k - 1) -> lab :: rest ]
                    let myDecls =
                        [ for m in 1 .. q ->
                            let terms =
                                [ for comp in compositions m s do
                                    let t = comp |> List.mapi (fun i c -> (i + 1) * c) |> List.sum
                                    if t >= 2 then   // t = 1 ⇒ D_1·E[Z] = 0
                                        let w =
                                            (factorial m, List.indexed comp)
                                            ||> List.fold (fun acc (i, c) ->
                                                acc / factorial c / (factorial (i + 1) ** float c))
                                        let degs = [ for (i, c) in List.indexed comp do for _ in 1 .. c -> i + 1 ]
                                        // all label assignments, factor by factor;
                                        // literal-zero derivative cells prune
                                        let rec go (ds: int list) : (Expr list * int list) list =
                                            match ds with
                                            | [] -> [ ([], []) ]
                                            | k :: rest ->
                                                let restA = go rest
                                                [ for tup in tuples k do
                                                    match dRead k tup with
                                                    | Some de ->
                                                        for (es, ls) in restA do
                                                            yield (de :: es, tup @ ls)
                                                    | None -> () ]
                                        let assigns = go degs
                                        if not assigns.IsEmpty then
                                            let sum =
                                                assigns
                                                |> List.map (fun (des, ls) -> des |> List.fold mulE (cmRead ls))
                                                |> List.reduce addE
                                            yield mulE (fLit w) sum ]
                            mkDecl (myName m) (match terms with [] -> fLit 0.0 | _ -> terms |> List.reduce addE) ]
                    // κ_m(Y') by univariate Möbius inversion; κ_1 shifts by g0
                    let compDecls =
                        [ for m in 1 .. q ->
                            let mobius =
                                setPartitions m
                                |> List.map (fun pt ->
                                    let b = pt.Length
                                    let w = (if b % 2 = 1 then 1.0 else -1.0) * factorial (b - 1)
                                    pt |> List.fold (fun acc blk -> mulE acc (v (myName blk.Length))) (fLit w))
                                |> List.reduce addE
                            let value = if m = 1 then addE mobius g0Expr else mobius
                            mkDecl (distComponentName dName m) (arrLitE [ value ]) ]
                    let outInfo = { Order = q
                                    Components = [ for k in 1 .. q -> distComponentName dName k ]
                                    Sources = info.Sources
                                    Dim = Some 1
                                    Flat = true }
                    (kappaDecls @ cmDecls @ myDecls @ compDecls, outInfo))))
    | _ ->
        Error (sprintf "%s expects %s(d, q, g0, D1, ..., Ds): a dist binding, a static output order, g(μ), and the derivative tensors at the mean" former former)

// ============================================================================
// Tower Bayes: three LOW-LEVEL conditioning primitives, univariate v1.
// Everything is straight-line scalar generation over the dist's component
// reads, mirroring the jet pushforward's conventions (flat 1-cell output
// components, registry-level composition, cumulant(d, k) projection).
//
//   dist_expect(d, c0, ..., cq)    E[c0 + c1 X + ... + cq X^q]: a scalar.
//                                  Coefficients are arbitrary (pure) scalar
//                                  expressions — runtime observations
//                                  included. Exact for q <= carried order;
//                                  beyond it, the moments(d, k) closure
//                                  convention (overlarge partition blocks
//                                  drop). This is the model-evidence /
//                                  normalizer primitive.
//   dist_reweight(d, c0, ..., cq)  the tower of X under the reweighted law
//                                  dm' = (c0 + ... + cq x^q) dm / Z —
//                                  BAYES with a polynomial likelihood
//                                  (posterior = reweighted prior). EXACT
//                                  and order-accounted: each posterior
//                                  moment consumes q extra input moments,
//                                  so an order-r prior yields an order-
//                                  (r - q) posterior. Conditioning is not
//                                  free: evidence spends stochastic order.
//   dist_mix(w1, d1, w2, d2)       the normalized mixture (w1 m1 + w2 m2)
//                                  / (w1 + w2): raw moments mix linearly;
//                                  cumulants do NOT (the between-branch
//                                  variance appears in k2) — which is the
//                                  point of having it. No independence
//                                  demanded: mixing is always lawful.
//
// The compositions ARE the calculus: conditioning on a finite-support
// variable = dist_reweight by its Lagrange indicator polynomial;
// disintegrate-then-dist_mix (weights = dist_expect evidences) is the law
// of total probability, an exact round trip; sequential Bayes = chained
// dist_reweight, the order budget falling with each observation.
// ============================================================================

/// Bind the raw moments m_1..m_top of a univariate registered dist as
/// scalar lets (partition sums over the carried cumulants, the
/// moments(d, k) reconstruction at dim 1). Exact for j <= carried order;
/// the shared closure convention beyond. Returns decls and a reader
/// (m_0 reads as the literal 1).
let private rawMomentDecls (span: Span) (tag: string) (info: DistInfo) (top: int)
    : Located<Decl> list * (int -> Expr) =
    let mName j = sprintf "__ppl_tb_%s_m%d" tag j
    let decls =
        [ for j in 1 .. top ->
            let parts =
                setPartitions j
                |> List.filter (fun pt -> pt |> List.forall (fun blk -> blk.Length <= info.Order))
            let terms =
                [ for pt in parts ->
                    pt |> List.fold (fun acc blk ->
                        mulE acc (distKappaRead info (List.replicate blk.Length 0))) (fLit 1.0) ]
            let value = match terms with [] -> fLit 0.0 | _ -> terms |> List.reduce addE
            { Value = DeclLet { Pattern = pvar (mName j); Type = None; Value = value; Mutability = BindLet }
              Span = span } ]
    let read j = if j = 0 then fLit 1.0 else v (mName j)
    (decls, read)

/// kappa_1..kappa_r FLAT 1-cell component decls for dName from a raw-moment
/// reader, by univariate Moebius inversion over set partitions.
let private mobiusComponentDecls (span: Span) (dName: string) (r: int) (mRead: int -> Expr)
    : Located<Decl> list =
    [ for m in 1 .. r ->
        let value =
            setPartitions m
            |> List.map (fun pt ->
                let b = pt.Length
                let w = (if b % 2 = 1 then 1.0 else -1.0) * factorial (b - 1)
                pt |> List.fold (fun acc blk -> mulE acc (mRead blk.Length)) (fLit w))
            |> List.reduce addE
        { Value = DeclLet { Pattern = pvar (distComponentName dName m); Type = None
                            Value = arrLitE [ value ]; Mutability = BindLet }
          Span = span } ]

let private univariateOnly (former: string) (ctx: Ctx) (info: DistInfo) : Result<unit, string> =
    distDim ctx info |> Result.bind (fun dim ->
        if dim = 1 then Ok ()
        else Error (sprintf "%s: univariate dists only so far — marginalize or push forward (dist_map/dist_affine) first, then condition" former))

let private elabDistExpect (ctx: Ctx) (span: Span) (binding: Binding)
    (dists: Map<string, DistInfo>) (args: Expr list)
    : Result<Located<Decl> list, string> =
    match args, binding.Pattern with
    | { Kind = ExprKind.ExprVar dn } :: coeffs, { Kind = PatternKind.PatVar outName } when not coeffs.IsEmpty ->
        match Map.tryFind dn dists with
        | None -> Error "dist_expect expects dist_expect(d, c0, ..., cq) with a previously declared dist binding d"
        | Some info ->
            univariateOnly "dist_expect" ctx info |> Result.bind (fun () ->
            let q = coeffs.Length - 1
            if q > 8 then Error "dist_expect: polynomial degree exceeds the generation bound (8)"
            else
                let mDecls, mRead = rawMomentDecls span ("ex_" + outName) info q
                let value =
                    coeffs
                    |> List.mapi (fun j c -> if j = 0 then c else mulE c (mRead j))
                    |> List.reduce addE
                Ok (mDecls @ [ { Value = DeclLet { binding with Value = value }; Span = span } ]))
    | _ ->
        Error "dist_expect expects dist_expect(d, c0, ..., cq): a dist binding and the polynomial's coefficients (constant first)"

let private elabDistReweight (ctx: Ctx) (span: Span) (dName: string)
    (dists: Map<string, DistInfo>) (args: Expr list)
    : Result<Located<Decl> list * DistInfo, string> =
    match args with
    | { Kind = ExprKind.ExprVar dn } :: coeffs when not coeffs.IsEmpty ->
        match Map.tryFind dn dists with
        | None -> Error "dist_reweight expects dist_reweight(d, c0, ..., cq) with a previously declared dist binding d"
        | Some info ->
            univariateOnly "dist_reweight" ctx info |> Result.bind (fun () ->
            let q = coeffs.Length - 1
            let rOut = info.Order - q
            if rOut < 1 then
                Error (sprintf "dist_reweight: a degree-%d weight consumes %d orders of the tower and '%s' carries only %d — insufficient stochastic order. Carry more (dist(A, %d)) or lower the weight degree." q q dn info.Order (min (q + 1) 6))
            else
                let mDecls, mRead = rawMomentDecls span ("rw_" + dName) info info.Order
                let zName = sprintf "__ppl_tb_rw_%s_Z" dName
                let zVal =
                    coeffs
                    |> List.mapi (fun c cf -> if c = 0 then cf else mulE cf (mRead c))
                    |> List.reduce addE
                let zDecl = { Value = DeclLet { Pattern = pvar zName; Type = None; Value = zVal; Mutability = BindLet }; Span = span }
                let wName j = sprintf "__ppl_tb_rw_%s_w%d" dName j
                let wDecls =
                    [ for j in 1 .. rOut ->
                        let num =
                            coeffs
                            |> List.mapi (fun c cf -> mulE cf (mRead (j + c)))
                            |> List.reduce addE
                        { Value = DeclLet { Pattern = pvar (wName j); Type = None
                                            Value = divE num (v zName); Mutability = BindLet }
                          Span = span } ]
                let wRead j = if j = 0 then fLit 1.0 else v (wName j)
                let compDecls = mobiusComponentDecls span dName rOut wRead
                let outInfo = { Order = rOut
                                Components = [ for k in 1 .. rOut -> distComponentName dName k ]
                                Sources = info.Sources
                                Dim = Some 1
                                Flat = true }
                Ok (mDecls @ (zDecl :: wDecls) @ compDecls, outInfo))
    | _ ->
        Error "dist_reweight expects dist_reweight(d, c0, ..., cq): a dist binding and the weight polynomial's coefficients (constant first)"

let private elabDistMix (ctx: Ctx) (span: Span) (dName: string)
    (dists: Map<string, DistInfo>) (args: Expr list)
    : Result<Located<Decl> list * DistInfo, string> =
    match args with
    | [w1; { Kind = ExprKind.ExprVar n1 }; w2; { Kind = ExprKind.ExprVar n2 }] ->
        match Map.tryFind n1 dists, Map.tryFind n2 dists with
        | Some d1, Some d2 ->
            univariateOnly "dist_mix" ctx d1 |> Result.bind (fun () ->
            univariateOnly "dist_mix" ctx d2 |> Result.bind (fun () ->
            let rOut = min d1.Order d2.Order
            let w1Name = sprintf "__ppl_tb_mx_%s_w1" dName
            let w2Name = sprintf "__ppl_tb_mx_%s_w2" dName
            let wsName = sprintf "__ppl_tb_mx_%s_ws" dName
            let bind n vl = { Value = DeclLet { Pattern = pvar n; Type = None; Value = vl; Mutability = BindLet }; Span = span }
            let m1Decls, m1Read = rawMomentDecls span ("mxa_" + dName) d1 rOut
            let m2Decls, m2Read = rawMomentDecls span ("mxb_" + dName) d2 rOut
            let mixName j = sprintf "__ppl_tb_mx_%s_m%d" dName j
            let mixDecls =
                [ for j in 1 .. rOut ->
                    bind (mixName j)
                        (divE (addE (mulE (v w1Name) (m1Read j)) (mulE (v w2Name) (m2Read j)))
                              (v wsName)) ]
            let mixRead j = if j = 0 then fLit 1.0 else v (mixName j)
            let compDecls = mobiusComponentDecls span dName rOut mixRead
            let outInfo = { Order = rOut
                            Components = [ for k in 1 .. rOut -> distComponentName dName k ]
                            Sources = Set.union d1.Sources d2.Sources
                            Dim = Some 1
                            Flat = true }
            Ok ([ bind w1Name w1; bind w2Name w2; bind wsName (addE (v w1Name) (v w2Name)) ]
               @ m1Decls @ m2Decls @ mixDecls @ compDecls, outInfo)))
        | _ ->
            Error "dist_mix expects dist_mix(w1, d1, w2, d2) with two previously declared dist bindings"
    | _ ->
        Error "dist_mix expects dist_mix(w1, d1, w2, d2): two weights (any pure scalar expressions) and two dist bindings"

// ============================================================================
// Signed atomic towers: quasi-distributions as first-class values.
//   dist_atoms(r, x1, w1, ..., xk, wk)  the order-r tower of the atomic
//                                       measure sum_i w_i delta(x_i),
//                                       normalized by sum w_i. Weights MAY
//                                       BE NEGATIVE: the constructor is
//                                       deliberately sign-agnostic, so
//                                       non-classical towers (negative
//                                       variance included) are carryable
//                                       values. Subsumes the point-tower
//                                       renewal (all-positive weights).
//   dist_negativity(d, x1, ..., xs)     the L1 negativity of d READ AS a
//                                       quasi-distribution on the claimed
//                                       support {x_1..x_s}: cells by
//                                       Lagrange indicators (exact when
//                                       s - 1 <= carried order; demanded),
//                                       N = sum max(0, -cell). Zero iff
//                                       the tower is a genuine probability
//                                       on that support — the classical
//                                       wall as a meter reading.
// ============================================================================

let private elabDistAtoms (ctx: Ctx) (span: Span) (dName: string) (args: Expr list)
    : Result<Located<Decl> list * DistInfo, string> =
    match args with
    | rExpr :: rest when rest.Length >= 2 && rest.Length % 2 = 0 ->
        let rRes =
            match evalExpr ctx.Statics maxSteps rExpr with
            | Ok (SVInt n) when n >= 1L && n <= 6L -> Ok (int n)
            | _ -> Error "dist_atoms: the order must be a compile-time integer in 1..6"
        rRes |> Result.map (fun r ->
            let k = rest.Length / 2
            let xName i = sprintf "__ppl_tb_at_%s_x%d" dName i
            let wName i = sprintf "__ppl_tb_at_%s_w%d" dName i
            let bind n vl = { Value = DeclLet { Pattern = pvar n; Type = None; Value = vl; Mutability = BindLet }; Span = span }
            let bindDecls =
                [ for i in 0 .. k - 1 do
                    yield bind (xName i) rest.[2 * i]
                    yield bind (wName i) rest.[2 * i + 1] ]
            let wsName = sprintf "__ppl_tb_at_%s_ws" dName
            let wsDecl = bind wsName ([ for i in 0 .. k - 1 -> v (wName i) ] |> List.reduce addE)
            let mName j = sprintf "__ppl_tb_at_%s_m%d" dName j
            let mDecls =
                [ for j in 1 .. r ->
                    let terms =
                        [ for i in 0 .. k - 1 ->
                            List.replicate j (v (xName i)) |> List.fold mulE (v (wName i)) ]
                    bind (mName j) (divE (terms |> List.reduce addE) (v wsName)) ]
            let mRead j = if j = 0 then fLit 1.0 else v (mName j)
            let compDecls = mobiusComponentDecls span dName r mRead
            let info = { Order = r
                         Components = [ for kk in 1 .. r -> distComponentName dName kk ]
                         Sources = Set.empty
                         Dim = Some 1
                         Flat = true }
            (bindDecls @ (wsDecl :: mDecls) @ compDecls, info))
    | _ ->
        Error "dist_atoms expects dist_atoms(r, x1, w1, ..., xk, wk): a static order and support/weight pairs (weights may be negative — quasi-distributions are values here)"

let private elabDistNegativity (ctx: Ctx) (span: Span) (binding: Binding)
    (dists: Map<string, DistInfo>) (args: Expr list)
    : Result<Located<Decl> list, string> =
    match args, binding.Pattern with
    | { Kind = ExprKind.ExprVar dn } :: xs, { Kind = PatternKind.PatVar outName } when xs.Length >= 2 ->
        match Map.tryFind dn dists with
        | None -> Error "dist_negativity expects dist_negativity(d, x1, ..., xs) with a previously declared dist binding d"
        | Some info ->
            univariateOnly "dist_negativity" ctx info |> Result.bind (fun () ->
            let sPts = xs.Length
            if sPts - 1 > info.Order then
                Error (sprintf "dist_negativity: reading %d cells needs the degree-%d Lagrange indicators but '%s' carries order %d — insufficient stochastic order. Carry more, or claim fewer support points." sPts (sPts - 1) dn info.Order)
            else
                let xName i = sprintf "__ppl_tb_ng_%s_x%d" outName i
                let bind n vl = { Value = DeclLet { Pattern = pvar n; Type = None; Value = vl; Mutability = BindLet }; Span = span }
                let xDecls = [ for i in 0 .. sPts - 1 -> bind (xName i) xs.[i] ]
                let mDecls, mRead = rawMomentDecls span ("ng_" + outName) info (sPts - 1)
                let cellName j = sprintf "__ppl_tb_ng_%s_c%d" outName j
                let cellDecls =
                    [ for j in 0 .. sPts - 1 ->
                        let others = [ for kk in 0 .. sPts - 1 do if kk <> j then yield v (xName kk) ]
                        // coefficients of prod (m - x_k), degree ascending
                        let coeffs =
                            others |> List.fold (fun (acc: Expr list) xk ->
                                let n = acc.Length
                                [ for i in 0 .. n ->
                                    let lower = if i >= 1 then Some acc.[i - 1] else None
                                    let upper = if i <= n - 1 then Some (mulE xk acc.[i]) else None
                                    match lower, upper with
                                    | Some l, Some u -> subE l u
                                    | Some l, None -> l
                                    | None, Some u -> subE (fLit 0.0) u
                                    | None, None -> fLit 0.0 ]) [ fLit 1.0 ]
                        let den = others |> List.fold (fun acc xk -> mulE acc (subE (v (xName j)) xk)) (fLit 1.0)
                        let num = coeffs |> List.mapi (fun c ce -> mulE ce (mRead c)) |> List.reduce addE
                        bind (cellName j) (divE num den) ]
                let negTerm j =
                    let c = v (cellName j)
                    divE (subE (appE (v "sqrt") [ mulE c c ]) c) (fLit 2.0)
                let total = [ for j in 0 .. sPts - 1 -> negTerm j ] |> List.reduce addE
                Ok (xDecls @ mDecls @ cellDecls
                    @ [ { Value = DeclLet { binding with Value = total }; Span = span } ]))
    | _ ->
        Error "dist_negativity expects dist_negativity(d, x1, ..., xs): a dist binding and at least two support points"

// ============================================================================
// dist_map: the symbolic front-end over dist_jet — differentiate a lambda
// at elaboration time, evaluate the derivatives at the runtime mean, and
// delegate to the jet pushforward. A polynomial's derivative chain
// terminates in structural zeros (finite jet = EXACT pushforward); any
// other map needs an explicit truncation degree — the approximation is a
// modeling choice the program must own.
// ============================================================================

/// Structural constant folding — enough for polynomial derivative chains
/// to terminate in literal zeros (0·e, e·0, 0±e drop; literals fold).
let rec private simplifyExpr (e: Expr) : Expr =
    match e.Kind with
    | ExprKind.ExprBinOp (m, op, a0, b0) ->
        let a = simplifyExpr a0
        let b = simplifyExpr b0
        (match op, a.Kind, b.Kind with
         | OpAdd, ExprKind.ExprLit (LitFloat 0.0), _ -> b
         | OpAdd, _, ExprKind.ExprLit (LitFloat 0.0) -> a
         | OpSub, _, ExprKind.ExprLit (LitFloat 0.0) -> a
         | OpMul, ExprKind.ExprLit (LitFloat 0.0), _ -> fLit 0.0
         | OpMul, _, ExprKind.ExprLit (LitFloat 0.0) -> fLit 0.0
         | OpMul, ExprKind.ExprLit (LitFloat 1.0), _ -> b
         | OpMul, _, ExprKind.ExprLit (LitFloat 1.0) -> a
         | OpDiv, ExprKind.ExprLit (LitFloat 0.0), _ -> fLit 0.0
         | OpDiv, _, ExprKind.ExprLit (LitFloat 1.0) -> a
         | _, ExprKind.ExprLit (LitFloat x), ExprKind.ExprLit (LitFloat y) ->
             (match op with
              | OpAdd -> fLit (x + y)
              | OpSub -> fLit (x - y)
              | OpMul -> fLit (x * y)
              | OpDiv when y <> 0.0 -> fLit (x / y)
              | _ -> inheritSpan e (ExprBinOp (m, op, a, b)))
         | _ -> inheritSpan e (ExprBinOp (m, op, a, b)))
    | ExprKind.ExprApp (f, args) -> inheritSpan e (ExprApp (f, args |> List.map simplifyExpr))
    | _ -> e

let private isZeroE (e: Expr) = match e.Kind with ExprKind.ExprLit (LitFloat 0.0) -> true | _ -> false

let rec private containsVar (n: string) (e: Expr) : bool =
    match e.Kind with
    | ExprKind.ExprVar m -> m = n
    | ExprKind.ExprBinOp (_, _, a, b) -> containsVar n a || containsVar n b
    | ExprKind.ExprApp (f, args) -> containsVar n f || args |> List.exists (containsVar n)
    | _ -> false

/// ∂e/∂param over the supported grammar: arithmetic and exp/log/sqrt/
/// sin/cos of the coordinates. Any subtree not mentioning the parameter is
/// a constant (array reads and other opaque calls included).
let rec private diffExpr (param: string) (e: Expr) : Result<Expr, string> =
    if not (containsVar param e) then Ok (fLit 0.0)
    else
        match e.Kind with
        | ExprKind.ExprVar _ -> Ok (fLit 1.0)   // containsVar ⇒ it IS the param
        | ExprKind.ExprBinOp (_, OpAdd, a, b) ->
            diffExpr param a |> Result.bind (fun da ->
            diffExpr param b |> Result.map (fun db -> addE da db))
        | ExprKind.ExprBinOp (_, OpSub, a, b) ->
            diffExpr param a |> Result.bind (fun da ->
            diffExpr param b |> Result.map (fun db -> subE da db))
        | ExprKind.ExprBinOp (_, OpMul, a, b) ->
            diffExpr param a |> Result.bind (fun da ->
            diffExpr param b |> Result.map (fun db -> addE (mulE da b) (mulE a db)))
        | ExprKind.ExprBinOp (_, OpDiv, a, b) ->
            diffExpr param a |> Result.bind (fun da ->
            diffExpr param b |> Result.map (fun db ->
                divE (subE (mulE da b) (mulE a db)) (mulE b b)))
        | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "exp" }, [a]) ->
            diffExpr param a |> Result.map (fun da -> mulE da (appE (v "exp") [a]))
        | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "log" }, [a]) ->
            diffExpr param a |> Result.map (fun da -> divE da a)
        | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "sqrt" }, [a]) ->
            diffExpr param a |> Result.map (fun da -> divE da (mulE (fLit 2.0) (appE (v "sqrt") [a])))
        | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "sin" }, [a]) ->
            diffExpr param a |> Result.map (fun da -> mulE da (appE (v "cos") [a]))
        | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "cos" }, [a]) ->
            diffExpr param a |> Result.map (fun da -> subE (fLit 0.0) (mulE da (appE (v "sin") [a])))
        | ExprKind.ExprBinOp (_, OpCaret, b, e) ->
            // d(b^e) = e*b^(e-1)*b' + b^e*log(b)*e'. The base term keeps the
            // power form (not e*b^e/b) so b=0 stays finite; the log term is
            // pruned by simplifyExpr whenever e is constant (e'=0), which is
            // also what keeps a negative base differentiable. The output is
            // itself +/-/*/^ over the coordinates, so higher-order jet levels
            // re-differentiate it in-grammar.
            diffExpr param b |> Result.bind (fun db ->
            diffExpr param e |> Result.map (fun de ->
                let baseTerm = mulE (mulE e (powE b (subE e (fLit 1.0)))) db
                let expTerm  = mulE (mulE (powE b e) (appE (v "log") [b])) de
                addE baseTerm expTerm))
        | _ ->
            Error "dist_map: cannot differentiate the map — supported: +, -, *, /, ^ and exp/log/sqrt/sin/cos of the coordinates (opaque subterms are fine when they don't mention a coordinate)"

let rec private substVars (map: Map<string, Expr>) (e: Expr) : Expr =
    match e.Kind with
    | ExprKind.ExprVar n -> (match Map.tryFind n map with Some r -> r | None -> e)
    | ExprKind.ExprBinOp (m, op, a, b) -> inheritSpan e (ExprBinOp (m, op, substVars map a, substVars map b))
    | ExprKind.ExprApp (f, args) -> inheritSpan e (ExprApp (substVars map f, args |> List.map (substVars map)))
    | _ -> e

/// dist_map body normalization, applied before differentiation: unwrap
/// trivial `{ expr }` function bodies and inline full-arity calls to
/// same-module top-level functions — transitively, so helper reuse like
/// `function v(m) = ...` / `function ptot(m) = v(m) + ...` presents
/// diffExpr with one closed expression in the coordinates. The budget
/// counts function EXPANSIONS (not expression nodes) and breaks recursive
/// helper cycles. Like the partial-application path, substitution is the
/// binder-free substVars: helper bodies are expected to be closed over
/// their own parameters (a module-level free variable in a helper that
/// collides with a map coordinate would be captured).
let private normalizeMapBody (former: string) (funcs: Map<string, FunctionDecl>) (body: Expr) : Result<Expr, string> =
    let rec unwrap (e: Expr) : Expr =
        match e.Kind with
        | ExprKind.ExprBlock ([], Some r) -> unwrap r
        | _ -> e
    let rec goList (n: int) (acc: Expr list) (es: Expr list) : Result<int * Expr list, string> =
        match es with
        | [] -> Ok (n, List.rev acc)
        | x :: rest -> go n x |> Result.bind (fun (n2, x') -> goList n2 (x' :: acc) rest)
    and go (n: int) (e: Expr) : Result<int * Expr, string> =
        let e = unwrap e
        match e.Kind with
        | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar f }, args) when Map.containsKey f funcs ->
            let fd = funcs.[f]
            if args.Length <> fd.Params.Length then
                Error (sprintf "%s: helper '%s' is called with %d argument(s) but takes %d" former f args.Length fd.Params.Length)
            elif n <= 0 then
                Error (sprintf "%s: the map's helper-call chain is too deep — is a helper function recursive?" former)
            else
                goList (n - 1) [] args |> Result.bind (fun (n2, args') ->
                    let bindMap = List.zip (fd.Params |> List.map (fun p -> p.Name)) args' |> Map.ofList
                    go n2 (substVars bindMap (unwrap fd.Body)))
        | ExprKind.ExprBinOp (m, op, a, b) ->
            go n a |> Result.bind (fun (n2, a') ->
                go n2 b |> Result.map (fun (n3, b') -> (n3, inheritSpan e (ExprBinOp (m, op, a', b')))))
        | ExprKind.ExprApp (f, args) ->
            goList n [] args |> Result.map (fun (n2, args') -> (n2, inheritSpan e (ExprApp (f, args'))))
        | ExprKind.ExprTuple es ->
            goList n [] es |> Result.map (fun (n2, es') -> (n2, inheritSpan e (ExprTuple es')))
        | _ -> Ok (n, e)
    go 256 body |> Result.map snd

/// dist_map(d, q, lambda(x...) -> e) / dist_map(d, q, s, lambda(x...) -> e):
/// derive the jet symbolically — the lambda takes one coordinate per dist
/// dimension; derivative tensors come from repeated symbolic
/// differentiation, evaluated at the runtime mean (reads of κ_1, bound
/// once); the pushforward itself is dist_jet's. Without s the derivative
/// tower must terminate (polynomial, exact); with s it truncates there.
let private elabDistMap (closed: bool) (ctx: Ctx) (span: Span) (dName: string)
    (funcs: Map<string, FunctionDecl>) (dists: Map<string, DistInfo>) (args: Expr list)
    : Result<Located<Decl> list * DistInfo, string> =
    let former = if closed then "dist_map_closed" else "dist_map"
    // The map slot: an inline lambda, a same-module top-level function
    // name, or a prefix partial application of one. Named/partial forms
    // inline the function body here at elaboration time (this pass runs
    // BEFORE typechecking, so the checker's eta-expansion never sees this
    // call) — the residual params become the jet coordinates, and the
    // bound prefix args are constants w.r.t. diffExpr since differentiation
    // is by coordinate name only.
    let asCoordLambda (e: Expr) : Result<(LambdaParam list * Expr) option, string> =
        match e.Kind with
        | ExprKind.ExprLambda (ps, None, body) -> Ok (Some (ps, body))
        | ExprKind.ExprVar f ->
            match Map.tryFind f funcs with
            | Some fd ->
                let ps = fd.Params |> List.map (fun p -> { Name = p.Name; Type = None } : LambdaParam)
                Ok (Some (ps, fd.Body))
            | None -> Ok None
        | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar f }, prefixArgs) when Map.containsKey f funcs ->
            let fd = funcs.[f]
            if prefixArgs.Length >= fd.Params.Length then
                Error (sprintf "%s: partial application of '%s' supplies %d of its %d parameter(s) — leave at least one free to map over"
                        former f prefixArgs.Length fd.Params.Length)
            else
                // Capture avoidance with the binder-free substVars, in two
                // steps: rename the residual params to reserved coordinate
                // names FIRST, so no variable inside the bound-arg
                // expressions can collide with a coordinate afterwards.
                let k = prefixArgs.Length
                let bound = fd.Params |> List.truncate k
                let residual = fd.Params |> List.skip k
                let freshNames = residual |> List.mapi (fun i _ -> sprintf "__ppl_pa_%s_%d" dName i)
                let renameMap =
                    List.zip (residual |> List.map (fun p -> p.Name)) (freshNames |> List.map v)
                    |> Map.ofList
                let bindMap =
                    List.zip (bound |> List.map (fun p -> p.Name)) prefixArgs
                    |> Map.ofList
                let body = fd.Body |> substVars renameMap |> substVars bindMap
                let ps = freshNames |> List.map (fun n -> { Name = n; Type = None } : LambdaParam)
                Ok (Some (ps, body))
        | _ -> Ok None
    let parseErr =
        sprintf "%s expects %s(d, q, f) or %s(d, q, s, f) where f is lambda(x...) -> expr, a same-module top-level function name, or a prefix partial application of one"
            former former former
    let parsed =
        match args with
        | [{ Kind = ExprKind.ExprVar dn }; qExpr; fArg] ->
            asCoordLambda fArg |> Result.bind (function
                | Some (ps, body) ->
                    normalizeMapBody former funcs body
                    |> Result.map (fun body' -> (dn, qExpr, None, ps, body'))
                | None -> Error parseErr)
        | [{ Kind = ExprKind.ExprVar dn }; qExpr; sExpr; fArg] ->
            asCoordLambda fArg |> Result.bind (function
                | Some (ps, body) ->
                    normalizeMapBody former funcs body
                    |> Result.map (fun body' -> (dn, qExpr, Some sExpr, ps, body'))
                | None -> Error parseErr)
        | _ -> Error parseErr
    parsed |> Result.bind (fun (dn, qExpr, sOpt, ps, body) ->
    match Map.tryFind dn dists with
    | None -> Error (sprintf "%s: '%s' must be a previously declared dist binding" former dn)
    | Some info ->
        distDim ctx info |> Result.bind (fun dim ->
        if ps.Length <> dim then
            Error (sprintf "%s: the lambda takes %d coordinate(s) but '%s' is %d-dimensional" former ps.Length dn dim)
        else
        let sRes =
            match sOpt with
            | None -> Ok None
            | Some e ->
                match evalExpr ctx.Statics maxSteps e with
                | Ok (SVInt x) when x >= 1L && x <= 8L -> Ok (Some (int x))
                | _ -> Error (sprintf "%s: the truncation degree s must be a compile-time integer in 1..8" former)
        sRes |> Result.bind (fun sOpt ->
        let paramNames = ps |> List.map (fun (p: LambdaParam) -> p.Name)
        // VECTOR MODE: a tuple-valued body lambda(x, y) -> (e1, ..., em)
        // (m ≥ 2) means g : R^dim → R^m — one symbolic jet per output
        // component, delegated to the vector dist_jet (joint output
        // cumulants). A single-expression body keeps the scalar path.
        let comps =
            match body.Kind with
            | ExprKind.ExprTuple es when es.Length >= 2 -> es
            | _ -> [body]
        // level k for one component: canonical tuple (i1 ≤ ... ≤ ik) → ∂^k f,
        // symbolic in the coordinates; each cell differentiates the
        // (k−1)-level cell of its tail by x_{i1} (Schwarz symmetry makes
        // canonical tuples enough)
        let levelFrom (fbody: Expr) (prev: Map<int list, Expr>) (k: int) : Result<Map<int list, Expr>, string> =
            canonicalTuples dim k
            |> List.fold (fun acc t ->
                acc |> Result.bind (fun m ->
                    let parent = if k = 1 then fbody else prev.[List.tail t]
                    diffExpr paramNames.[List.head t] parent
                    |> Result.map (fun de -> Map.add t (simplifyExpr de) m)))
                (Ok Map.empty)
        let allZero (m: Map<int list, Expr>) = m |> Map.forall (fun _ e -> isZeroE e)
        let rec grow (fbody: Expr) (acc: Map<int list, Expr> list) (k: int) : Result<Map<int list, Expr> list, string> =
            let prev = match acc with [] -> Map.empty | h :: _ -> h
            levelFrom fbody prev k |> Result.bind (fun lv ->
                match sOpt with
                | Some s -> if k >= s then Ok (List.rev (lv :: acc)) else grow fbody (lv :: acc) (k + 1)
                | None ->
                    if allZero lv then Ok (List.rev acc)   // the polynomial terminated at degree k−1
                    elif k >= 8 then Error (sprintf "%s: the map is not polynomial (its derivatives never vanish) — own the truncation with an explicit degree: %s(d, q, s, lambda(x...) -> expr)" former former)
                    else grow fbody (lv :: acc) (k + 1))
        let towersRes =
            comps |> List.fold (fun acc cb ->
                acc |> Result.bind (fun ts -> grow cb [] 1 |> Result.map (fun lv -> ts @ [lv]))) (Ok [])
        towersRes |> Result.bind (fun towers ->
        let sPer = towers |> List.map List.length
        let s = List.max sPer
        if s = 0 then
            Error (sprintf "%s: the map is constant in the coordinates — there is no jet to push" former)
        else
        // the mean components, bound once; coordinates substitute to them
        let muName i = sprintf "__ppl_jetmu_%s_%d" dName i
        let muDecls =
            [ for i in 0 .. dim - 1 ->
                { Value = DeclLet { Pattern = pvar (muName i); Type = None
                                    Value = appE (v info.Components.[0]) [iLit i]
                                    Mutability = BindLet }
                  Span = span } ]
        let subst = Map.ofList [ for i in 0 .. dim - 1 -> (paramNames.[i], v (muName i)) ]
        let atMean e = simplifyExpr (substVars subst e)
        let g0, dArgs =
            match comps with
            | [single] ->
                let levels = towers.Head
                let dArgs =
                    [ for k in 1 .. s ->
                        let lv = levels.[k - 1]
                        if dim = 1 then atMean lv.[List.replicate k 0]
                        else arrLitE [ for t in canonicalTuples dim k -> atMean lv.[t] ] ]
                (atMean single, dArgs)
            | _ ->
                // vector: g0 = the m atMean cells; D_k = coordinate-major
                // flat cells, zero-filled where a shorter component tower
                // has no level k (its polynomial terminated earlier)
                let g0 = arrLitE (comps |> List.map atMean)
                let dArgs =
                    [ for k in 1 .. s ->
                        arrLitE
                            [ for (a, tower) in List.indexed towers do
                                for t in canonicalTuples dim k do
                                    yield (if k <= sPer.[a] then atMean tower.[k - 1].[t] else fLit 0.0) ] ]
                (g0, dArgs)
        elabDistJet closed former ctx span dName dists (v dn :: qExpr :: g0 :: dArgs)
        |> Result.map (fun (nds, outInfo) -> (muDecls @ nds, outInfo))))))

// ============================================================================
// Free cumulants: the SAME moment↔cumulant machinery summed over the
// NON-CROSSING partition lattice (Catalan combinatorics) instead of all set
// partitions — the transform underlying free probability. Computed by the
// triangular recursion fk_p = μ_p − Σ over non-crossing π ≠ full-block of
// Π fk_{|B|}: each rank's cells read raw-moment tensors (packed pipelines)
// and LOWER-rank fk tensors (flat, emitted earlier). fk_1..fk_3 coincide
// with classical cumulants (all partitions of ≤3 elements are non-crossing);
// rank 4 is where the lattices first diverge.
// ============================================================================

let private isNonCrossing (partition: int list list) : bool =
    let blockOf = partition |> List.mapi (fun i blk -> blk |> List.map (fun x -> (x, i))) |> List.concat |> Map.ofList
    let n = partition |> List.sumBy List.length
    let crossing =
        Seq.exists (fun (a, b, c, d) ->
            blockOf.[a] = blockOf.[c] && blockOf.[b] = blockOf.[d] && blockOf.[a] <> blockOf.[b])
            (seq { for a in 0 .. n - 4 do
                     for b in a + 1 .. n - 3 do
                       for c in b + 1 .. n - 2 do
                         for d in c + 1 .. n - 1 -> (a, b, c, d) })
    not crossing

let private elabFreeCumulants (ctx: Ctx) (span: Span) (binding: Binding) (args: Expr list)
    : Result<Located<Decl> list, string> =
    match args with
    | [{ Kind = ExprKind.ExprVar aName }; rExpr] ->
        let r =
            match evalExpr ctx.Statics maxSteps rExpr with
            | Ok (SVInt x) when x >= 1L && x <= 6L -> Ok (int x)
            | _ -> Error "free_cumulants: the order must be a compile-time integer in 1..6"
        r |> Result.bind (fun r ->
        arrayShape ctx "free_cumulants" aName |> Result.bind (fun (elem, leading, fiber, n) ->
            match leading with
            | [ix] ->
                match resolveExtent ctx.Aliases ctx.Statics ix with
                | None -> Error "free_cumulants: the variable-axis extent must be statically known"
                | Some d ->
                    let compNames =
                        match binding.Pattern.Kind with
                        | PatternKind.PatTuple pats when pats.Length = r ->
                            let names = pats |> List.map (fun p -> match p.Kind with PatternKind.PatVar nm -> Some nm | _ -> None)
                            if names |> List.forall Option.isSome then Ok (names |> List.map Option.get)
                            else Error "free_cumulants: destructure into plain names"
                        | _ -> Error (sprintf "free_cumulants: destructure the result — `let (f1, ..., f%d) = free_cumulants(%s, %d)`" r aName r)
                    compNames |> Result.map (fun fkNames ->
                        // raw moments μ_S = P_S / N straight from the
                        // module's shared pool sweep (was one packed
                        // pipeline per rank — r separate traversals).
                        let (pd, pool) = acquirePool ctx span aName d (float n) r
                        let muDecls = pd
                        let muRead (labels: int list) = poolMoment pool labels
                        // fk tensors ascending; fk_1 = μ_1; flat lex reads on
                        // earlier fk outputs
                        let fkRead (kIdx: int) (labels: int list) =
                            if labels.Length = 1 then appE (v fkNames.[0]) (labels |> List.map iLit)
                            else appE (v fkNames.[labels.Length - 1]) [iLit (lexOffsetOf d labels.Length labels)]
                        let fkDecl p nm =
                            if p = 1 then
                                { Value = DeclLet { Pattern = pvar nm; Type = None
                                                    Value = arrLitE [ for i in 0 .. d - 1 -> poolMoment pool [i] ]
                                                    Mutability = BindLet }; Span = span }
                            else
                                let cells =
                                    [ for labels in canonicalTuples d p ->
                                        let labArr = List.toArray labels
                                        let ncParts =
                                            setPartitions p
                                            |> List.filter (fun pt -> pt.Length > 1 && isNonCrossing pt)
                                        let subtracted =
                                            [ for pt in ncParts ->
                                                pt |> List.fold (fun acc blk ->
                                                    mulE acc (fkRead p (blk |> List.map (fun pos -> labArr.[pos]) |> List.sort))) (fLit 1.0) ]
                                        match subtracted with
                                        | [] -> muRead labels
                                        | _ -> subE (muRead labels) (subtracted |> List.reduce addE) ]
                                { Value = DeclLet { Pattern = pvar nm; Type = None; Value = arrLitE cells; Mutability = BindLet }; Span = span }
                        muDecls @ (fkNames |> List.mapi (fun i nm -> fkDecl (i + 1) nm)))
            | _ -> Error "free_cumulants: one variable axis per array so far"))
    | _ ->
        Error "free_cumulants expects free_cumulants(A, r)"

// ============================================================================
// Independence: per-compilation state + the `indep` constraint handler
// ============================================================================

/// PPL-owned independence state, consumed by the checker's Dist machinery:
/// the DECLARED relation (loose `let _ = independent(X, Y)` + struct
/// `where indep` licenses, exported by expandModule), the LICENSE stack
/// (pairs opened around function-body checking by the registered `indep`
/// where-clause handler), and module-dist SOURCE sets (dist binding name →
/// underlying arrays, for checker-side provenance seeding). All state is
/// AsyncLocal — the test suite checks programs in parallel and each
/// compilation flows through one async context (expand → check).
module Independence =
    open System.Threading

    let private declaredStore = new AsyncLocal<Set<string * string>>()
    let private licenseStore = new AsyncLocal<(string * string) list>()
    let private sourcesStore = new AsyncLocal<Map<string, Set<string>>>()

    let private declared () = match box declaredStore.Value with null -> Set.empty | _ -> declaredStore.Value
    let private licenses () = match box licenseStore.Value with null -> [] | _ -> licenseStore.Value
    let private sources () = match box sourcesStore.Value with null -> Map.empty | _ -> sourcesStore.Value

    let key (a: string) (b: string) = if a <= b then (a, b) else (b, a)

    /// Fresh compilation: expand() calls this once per program.
    let reset () =
        declaredStore.Value <- Set.empty
        licenseStore.Value <- []
        sourcesStore.Value <- Map.empty

    let addDeclared (pairs: Set<string * string>) =
        declaredStore.Value <- Set.union (declared ()) pairs

    let addSources (m: Map<string, Set<string>>) =
        sourcesStore.Value <- m |> Map.fold (fun acc k v -> Map.add k v acc) (sources ())

    /// Checker-facing: source arrays of a module-level dist binding.
    let distSources (name: string) : Set<string> option = Map.tryFind name (sources ())

    /// Checker-facing: is the pair independent under declared ∪ licenses?
    let isRelated (a: string) (b: string) : bool =
        let k = key a b
        Set.contains k (declared ()) || List.contains k (licenses ())

    let pushLicense (a: string) (b: string) =
        licenseStore.Value <- key a b :: licenses ()

    let popLicense (a: string) (b: string) =
        let k = key a b
        let rec removeFirst = function
            | [] -> []
            | x :: rest -> if x = k then rest else x :: removeFirst rest
        licenseStore.Value <- removeFirst (licenses ())

    /// The `indep(a, b)` where-clause handler: declaring it on a function
    /// PROMOTES the function to a PPL function — the body checks under the
    /// license (a, b treated as independent), and every CALL SITE must
    /// prove independence of the actual arguments' sources.
    let private indepHandler : Blade.Constraints.ConstraintHandler = {
        Describe = "indep(a, b) — declares two Dist-valued parameters independent for the function body; call sites must prove independence of the actuals' sources"
        Validate = fun funcName paramNames args ->
            match args with
            | [a; b] when a = b ->
                Error (sprintf "function '%s': indep(%s, %s) — a value is not independent of itself" funcName a b)
            | [a; b] ->
                let missing = [a; b] |> List.filter (fun n -> not (List.contains n paramNames))
                if missing.IsEmpty then Ok ()
                else Error (sprintf "function '%s': where indep(%s, %s) must name function parameters (unknown: %s)" funcName a b (String.concat ", " missing))
            | _ ->
                Error (sprintf "function '%s': indep expects exactly two parameter names — indep(a, b)" funcName)
        EnterBody = fun funcName args ->
            match args with
            | [a; b] -> pushLicense (Blade.Constraints.paramProvenanceToken funcName a)
                                    (Blade.Constraints.paramProvenanceToken funcName b)
            | _ -> ()
        ExitBody = fun funcName args ->
            match args with
            | [a; b] -> popLicense (Blade.Constraints.paramProvenanceToken funcName a)
                                   (Blade.Constraints.paramProvenanceToken funcName b)
            | _ -> ()
        Discharge = fun funcName args provOf ->
            match args with
            | [a; b] ->
                let pa = provOf a
                let pb = provOf b
                if Set.isEmpty pa || Set.isEmpty pb then
                    Error (sprintf "call to '%s': cannot establish provenance for the dist argument bound to '%s' — pass a dist binding (or an expression built from dists) so independence of its sources can be verified" funcName (if Set.isEmpty pa then a else b))
                else
                    let missing =
                        [ for s1 in pa do
                            for s2 in pb do
                              if not (isRelated s1 s2) then yield (s1, s2) ]
                    match missing with
                    | [] -> Ok ()
                    | (s1, s2) :: _ when s1 = s2 ->
                        Error (sprintf "call to '%s' requires indep(%s, %s): both arguments carry source '%s' — a value is not independent of itself; pass dists built from disjoint sources" funcName a b s1)
                    | (s1, s2) :: _ ->
                        Error (sprintf "call to '%s' requires indep(%s, %s): sources '%s' and '%s' are not declared independent — add `let _ = ppl.independent(%s, %s)` (or a struct/function `where ppl.indep(...)` license)" funcName a b s1 s2 s1 s2)
            | _ -> Error "indep expects exactly two arguments"
    }

    // Registered under the internal (normalized) name: the surface spelling
    // is the qualified `where <alias>.indep(...)` with `import ppl`, which
    // the ppl elaborator normalizes to "__ppl_indep" before checking. A bare
    // `where indep(...)` therefore no longer resolves (the checker's
    // unknown-constraint diagnostic points at the module spelling).
    let register () = Blade.Constraints.registerConstraint "__ppl_indep" indepHandler

/// IDE-facing dist registry: every dist binding this module elaborates,
/// mapped to its carried order and component bindings. The packed formers
/// (dist/dist_add/dist_scale) leave a `__dist_pack` decl under the user's
/// name, but the flat ones (dist_jet/dist_map/dist_reweight/dist_mix/
/// dist_atoms) are register-only — the name is ERASED from the program, so
/// `ide check` sees no binding for it and can offer no hover. The components
/// always survive, so recording (order, components) is enough for the IDE to
/// rebuild `Dist<order, elem like axes>` from κ_1's inferred type. Consumed
/// by Ide.fs; AsyncLocal for the same reason as Independence.
module IdeDists =
    open System.Threading

    let private store = new AsyncLocal<Map<string, int * string list>>()
    let private dists () = match box store.Value with null -> Map.empty | _ -> store.Value

    /// Fresh compilation: expand() calls this once per program.
    let reset () = store.Value <- Map.empty

    let add (m: Map<string, int * string list>) =
        store.Value <- m |> Map.fold (fun acc k v -> Map.add k v acc) (dists ())

    /// IDE-facing: carried order and component bindings of a dist name.
    let tryFind (name: string) : (int * string list) option = Map.tryFind name (dists ())

    /// IDE-facing: every registered dist, as (name, order, components).
    let entries () : (string * int * string list) list =
        dists () |> Map.toList |> List.map (fun (n, (o, cs)) -> (n, o, cs))

// ============================================================================
// Checker-facing synthesis (typed-Dist operator dispatch)
// ============================================================================

/// Surface expansions for TypeCheck's Dist operator dispatch
/// (inferDistBinOp): the checker SYNTHESIZES these block expressions and
/// re-infers them (synthesize-and-infer), so dist operators work in any
/// expression position — notably on Dist-typed function parameters, which
/// the elaboration-level registry rewrites above can never see. The
/// expansion rules live here so they stay next to the elaboration rules
/// they mirror. NOTE: the synthesized code calls `cumulant` and
/// `__dist_pack`, both checker intrinsics; a user shadowing `cumulant`
/// disables dist operators in checker positions (documented edge).
module DistSynth =
    /// c * d (either side): κ_k(c·X) = c^k κ_k(X) — multilinearity, exact
    /// with NO independence requirement, hence dispatchable anywhere.
    ///   { let __dsd = d
    ///     let __dsk<k> = cumulant(__dsd, k)          per order
    ///     let __dss<k> = map1(__dsk<k>, c^k · __u)   per order
    ///     __dist_pack(__dss1, ..., __dssr) }
    /// `uniq` disambiguates nested synthesized expansions.
    ///
    /// The scalar expr `c` is spliced INTO each kernel body verbatim (k
    /// copies), NOT bound to a synthesized block-local: inlined kernels
    /// render captured block-locals by NAME while the block emission names
    /// them by id (`__v<id>`), so a `let __dsc = c` capture dangles in the
    /// generated C++. Splicing keeps c's own variable references, which
    /// render under the same rules as user-written kernel captures.
    let scaleExpr (uniq: int) (c: Expr) (d: Expr) (order: int) : Expr =
        let dN = sprintf "__dsd_%d" uniq
        let kN k = sprintf "__dsk_%d_%d" uniq k
        let sN k = sprintf "__dss_%d_%d" uniq k
        let stmts =
            [ sLet dN d ]
            @ [ for k in 1 .. order ->
                  sLet (kN k) (appE (v "__ppl_cumulant") [v dN; iLit k]) ]
            @ [ for k in 1 .. order ->
                  let scaled = List.replicate k c |> List.fold mulE (v "__u")
                  sLet (sN k) (map1 (v (kN k)) scaled) ]
        syn (ExprBlock (stmts, Some (appE (v "__dist_pack") [ for k in 1 .. order -> v (sN k) ])))

    /// l ± r for independent dists: per-order c_k = a_k + weight(k)·b_k —
    /// addition is weight ≡ 1; subtraction is weight k = (−1)^k
    /// (κ_k(−Y) = (−1)^k κ_k(Y), so odd orders subtract, even orders add).
    /// The caller (TypeCheck.inferDistBinOp) verifies the independence
    /// condition BEFORE synthesizing; weights are literals, so the kernels
    /// capture nothing (no block-local-capture hazard).
    ///   { let __dcl = l; let __dcr = r
    ///     let __dka<k> = cumulant(__dcl, k); __dkb<k> = cumulant(__dcr, k)
    ///     let __dks<k> = zipMap2(__dka<k>, __dkb<k>, __u + w_k·__w)
    ///     __dist_pack(__dks1, ..., __dksr) }
    let combineExpr (uniq: int) (weight: int -> float) (l: Expr) (r: Expr) (order: int) : Expr =
        let lN = sprintf "__dcl_%d" uniq
        let rN = sprintf "__dcr_%d" uniq
        let aN k = sprintf "__dka_%d_%d" uniq k
        let bN k = sprintf "__dkb_%d_%d" uniq k
        let sN k = sprintf "__dks_%d_%d" uniq k
        let stmts =
            [ sLet lN l; sLet rN r ]
            @ [ for k in 1 .. order do
                  yield sLet (aN k) (appE (v "__ppl_cumulant") [v lN; iLit k])
                  yield sLet (bN k) (appE (v "__ppl_cumulant") [v rN; iLit k]) ]
            @ [ for k in 1 .. order ->
                  let contrib =
                      if weight k = 1.0 then v "__w"
                      else mulE (fLit (weight k)) (v "__w")
                  sLet (sN k) (zipMap2 (v (aN k)) (v (bN k)) (addE (v "__u") contrib)) ]
        syn (ExprBlock (stmts, Some (appE (v "__dist_pack") [ for k in 1 .. order -> v (sN k) ])))

// ============================================================================
// Module expansion
// ============================================================================

/// `import ppl [as _]` — the module this layer owns.
let private isPplImport (d: Located<Decl>) =
    match d.Value with
    | DeclImport (["ppl"], _) -> true
    | _ -> false

/// Aliases bound to `ppl` in this decl list. Errors on a selective
/// `from ppl import ...`, which would reintroduce the global names the module
/// system is meant to remove.
let private pplAliasesOf (decls: Located<Decl> list) : Result<Set<string>, string> =
    decls |> List.fold (fun acc d ->
        acc |> Result.bind (fun set ->
            match d.Value with
            | DeclImport (["ppl"], ImportQualified aliasOpt) ->
                Ok (Set.add (aliasOpt |> Option.defaultValue "ppl") set)
            | DeclImport (["ppl"], ImportSelective _) ->
                Error "`ppl` supports only `import ppl [as <alias>]`; a selective `from ppl import ...` would reintroduce global names"
            | _ -> Ok set))
        (Ok Set.empty)

/// Normalize the qualified ppl surface to the internal forms the passes below
/// (and the type-checker) recognize: `alias.<former>(...)` -> bare
/// `<former>(...)`, and `alias.cumulant(...)` -> `__ppl_cumulant(...)` (the
/// projection marker TypeCheck matches). A missed position leaves an
/// `ExprField` that fails to type-check, so this need not be exhaustive.
let rec private stripQualified (aliases: Set<string>) (e: Expr) : Expr =
    let r = stripQualified aliases
    let rStmt s =
        let rec go s =
            match s with
            | StmtLet b -> StmtLet { b with Value = r b.Value }
            | StmtAssign (l, op, rr) -> StmtAssign (r l, op, r rr)
            | StmtExpr e2 -> StmtExpr (r e2)
            | StmtForIn (var, range, body) -> StmtForIn (var, r range, List.map go body)
            | StmtSpanned (inner, sp) -> StmtSpanned (go inner, sp)
        go s
    match e.Kind with
    | ExprKind.ExprField ({ Kind = ExprKind.ExprVar a }, name)
        when Set.contains a aliases
             && (name = "cumulant" || name = "indep" || Set.contains name formerNames) ->
        match name with
        | "cumulant" -> inheritSpan e (ExprVar "__ppl_cumulant")
        // `indep` appears qualified in struct where-invariants
        // (`where p.indep(X, Y)`); normalize to the registered internal
        // constraint name (splitInvariant and the checker match it).
        | "indep" -> inheritSpan e (ExprVar "__ppl_indep")
        | _ -> inheritSpan e (ExprVar name)
    | ExprKind.ExprApp (f, args) -> inheritSpan e (ExprApp (r f, List.map r args))
    | ExprKind.ExprBinOp (m, op, a, b) -> inheritSpan e (ExprBinOp (m, op, r a, r b))
    | ExprKind.ExprUnaryOp (op, a) -> inheritSpan e (ExprUnaryOp (op, r a))
    | ExprKind.ExprTyped (a, t) -> inheritSpan e (ExprTyped (r a, t))
    | ExprKind.ExprAssign (l, rr) -> inheritSpan e (ExprAssign (r l, r rr))
    | ExprKind.ExprTuple es -> inheritSpan e (ExprTuple (List.map r es))
    | ExprKind.ExprArrayLit es -> inheritSpan e (ExprArrayLit (List.map r es))
    | ExprKind.ExprDotDot (a, b) -> inheritSpan e (ExprDotDot (r a, r b))
    | ExprKind.ExprIf (c, t, f) -> inheritSpan e (ExprIf (r c, r t, r f))
    | ExprKind.ExprLet (b, body) -> inheritSpan e (ExprLet ({ b with Value = r b.Value }, r body))
    | ExprKind.ExprLambda (ps, w, body) -> inheritSpan e (ExprLambda (ps, w, r body))
    | ExprKind.ExprMatch (s, cases) ->
        inheritSpan e (ExprMatch (r s, cases |> List.map (fun c -> { c with Body = r c.Body })))
    | ExprKind.ExprBlock (stmts, fin) -> inheritSpan e (ExprBlock (List.map rStmt stmts, Option.map r fin))
    | _ -> e

/// Normalize a qualified constraint-conjunct name (`"<alias>.indep"` from the
/// parser's dotted where-clause arm) to the registered internal name.
let private stripConjunctName (aliases: Set<string>) (cname: string) : string =
    match cname.Split('.') with
    | [| a; "indep" |] when Set.contains a aliases -> "__ppl_indep"
    | _ -> cname

/// Apply stripQualified to every expression-bearing decl (function
/// where-clause conjunct names and struct where-invariants included).
let private stripDecl (aliases: Set<string>) (d: Located<Decl>) : Located<Decl> =
    let s = stripQualified aliases
    let value =
        match d.Value with
        | DeclFunction fd ->
            let w' =
                fd.WhereClause
                |> Option.map (fun w ->
                    { w with Custom = w.Custom |> List.map (fun (n, args) -> (stripConjunctName aliases n, args)) })
            DeclFunction { fd with Body = s fd.Body; WhereClause = w' }
        | DeclLet b -> DeclLet { b with Value = s b.Value }
        | DeclStatic b -> DeclStatic { b with Value = s b.Value }
        | DeclType (TyDeclStruct (sname, tps, fields, conjuncts)) ->
            DeclType (TyDeclStruct (sname, tps, fields, conjuncts |> List.map s))
        | other -> other
    { d with Value = value }

let private expandModuleCore (decls: Located<Decl> list) : Result<Located<Decl> list, string> =
    // User definitions shadow the formers entirely (same rule as ML ops
    // and the math intrinsics).
    let declNames =
        decls |> List.choose (fun d ->
            match d.Value with
            | DeclFunction fd -> Some fd.Name
            | _ -> None)
        |> Set.ofList
    let active n = not (Set.contains n declNames)
    // Same-module top-level functions by name (grad's declNames precedent):
    // dist_map accepts a named function or a prefix partial application of
    // one in its map slot and inlines the body at elaboration time.
    let funcDecls : Map<string, FunctionDecl> =
        decls |> List.choose (fun d ->
            match d.Value with
            | DeclFunction fd -> Some (fd.Name, fd)
            | _ -> None)
        |> Map.ofList
    match resolveStatics decls with
    | Error e -> Error (sprintf "PPL elaboration: static resolution failed: %s" e)
    // Fold failures are the type-checker's to report (assertion semantics).
    | Ok (statics, _) ->
        // Pass 0.5: strip indep(...) conjuncts out of struct where-invariants
        // (static licenses, not runtime propositions); residual invariants
        // keep their construction-time validate().
        let mutable structIndep : Map<string, (string * string) list> = Map.empty
        let decls =
            decls |> List.map (fun d ->
                match d.Value with
                | DeclType (TyDeclStruct (sname, tps, fields, conjuncts)) when not conjuncts.IsEmpty ->
                    // Per-conjunct split: an indep(...) conjunct is consumed
                    // as a static license; `&&`-joined forms inside a single
                    // conjunct still split recursively. Residual conjuncts
                    // stay runtime-checked.
                    let (pairs, residuals) =
                        conjuncts |> List.fold (fun (ps, rs) c ->
                            let (cp, cr) = splitInvariant c
                            (ps @ cp, rs @ Option.toList cr)) ([], [])
                    if pairs.IsEmpty then d
                    else
                        structIndep <- Map.add sname pairs structIndep
                        { d with Value = DeclType (TyDeclStruct (sname, tps, fields, residuals)) }
                | _ -> d)
        // Array-typed struct fields and struct-typed instances: each
        // instance contributes alias-named array shapes and, per the
        // struct's declared indep pairs, instance-scoped independence.
        let structFields =
            decls |> List.fold (fun acc d ->
                match d.Value with
                | DeclType (TyDeclStruct (sname, _, fields, _)) ->
                    let arrFields =
                        fields |> List.choose (fun f ->
                            match f.Type with
                            | TyArray (e, ix) -> Some (f.Name, (e, ix))
                            | _ -> None)
                        |> Map.ofList
                    Map.add sname arrFields acc
                | _ -> acc) Map.empty
        let instances =
            decls |> List.fold (fun acc d ->
                match d.Value with
                | DeclLet { Pattern = { Kind = PatternKind.PatVar iname }; Value = { Kind = ExprKind.ExprStruct (sname, _, _) } } when Map.containsKey sname structFields ->
                    Map.add iname sname acc
                | _ -> acc) Map.empty
        let aliasArrays =
            instances |> Map.fold (fun acc iname sname ->
                match Map.tryFind sname structFields with
                | Some fs -> fs |> Map.fold (fun a fName shape -> Map.add (aliasOf iname fName) shape a) acc
                | None -> acc) Map.empty
        let structPairIndep =
            instances |> Map.fold (fun acc iname sname ->
                match Map.tryFind sname structIndep with
                | Some pairs -> pairs |> List.fold (fun s (fa, fb) -> Set.add (indepKey (aliasOf iname fa) (aliasOf iname fb)) s) acc
                | None -> acc) Set.empty
        // Pass 0.8: normalize struct-field arguments of former calls to
        // alias bindings (`let __ppl_arr_m_f = m.f`), inserted before first
        // use — codegen's loop machinery iterates named bindings, not raw
        // field accesses.
        let mutable emittedAliases = Set.empty
        let decls =
            decls |> List.collect (fun d ->
                let normArgs (args: Expr list) : Expr list * Located<Decl> list =
                    args |> List.fold (fun (acc, ads) a ->
                        match a.Kind with
                        | ExprKind.ExprField ({ Kind = ExprKind.ExprVar m }, f) when Map.containsKey m instances ->
                            let al = aliasOf m f
                            let newDecls =
                                if Set.contains al emittedAliases then []
                                else
                                    emittedAliases <- Set.add al emittedAliases
                                    [ { Value = DeclLet { Pattern = mkPat d.Span (PatVar al); Type = None; Value = a; Mutability = BindLet }; Span = d.Span } ]
                            (acc @ [mkExpr d.Span (ExprVar al)], ads @ newDecls)
                        | _ -> (acc @ [a], ads)) ([], [])
                match d.Value with
                | DeclLet ({ Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar n }, args) } } as b) when Set.contains n formerNames && active n ->
                    let (args', aliasDecls) = normArgs args
                    aliasDecls @ [ { d with Value = DeclLet { b with Value = mkExpr d.Span (ExprApp (mkExpr d.Span (ExprVar n), args')) } } ]
                | _ -> [d])
        // Pass 1: consume `let _ = independent(X, Y)` declarations.
        let mutable indep = structPairIndep
        let mutable rest = []
        let mutable err = None
        for d in decls do
            match d.Value with
            | DeclLet { Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "independent" }, args) } } when active "independent" ->
                match args with
                | [{ Kind = ExprKind.ExprVar x }; { Kind = ExprKind.ExprVar y }] when x <> y ->
                    indep <- Set.add (indepKey x y) indep
                | [{ Kind = ExprKind.ExprVar x }; { Kind = ExprKind.ExprVar y }] when x = y ->
                    if err.IsNone then err <- Some (sprintf "independent(%s, %s): an array is not independent of itself" x y)
                | _ ->
                    if err.IsNone then err <- Some "independent expects two array names (or struct fields): `let _ = ppl.independent(X, Y)`"
            | _ -> rest <- rest @ [d]
        match err with
        | Some e -> Error e
        | None ->
        let aliases = collectAliases rest
        // Inferred shapes for UN-annotated computed arrays (method_for over
        // range/halo slots) seed the map; explicit annotations and struct-
        // field aliases override them.
        let inferredArrays =
            rest |> List.fold (fun acc d ->
                match d.Value with
                | DeclLet b | DeclStatic b ->
                    match b.Pattern, b.Type with
                    | { Kind = PatternKind.PatVar name }, None ->
                        match computedShapeOf aliases statics b.Value with
                        | Some shape -> Map.add name shape acc
                        | None -> acc
                    | _ -> acc
                | _ -> acc) Map.empty
        let arrays =
            collectArrays rest
            |> Map.fold (fun acc k s -> Map.add k s acc) inferredArrays
            |> fun annotated -> aliasArrays |> Map.fold (fun acc k s -> Map.add k s acc) annotated
        // Pre-scan: the maximal multiset size each source array needs across
        // ALL its single-array formers in this module, so the first former
        // emits ONE maximal pool the rest reuse (cross-former single-pass).
        // Names that turn out to be dist bindings or ineligible arrays are
        // harmless here — the entry is only consulted on the pool path.
        let poolMax =
            rest |> List.choose (fun d ->
                match d.Value with
                | DeclLet { Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar f }, [{ Kind = ExprKind.ExprVar a }; kExpr]) } }
                    when (List.contains f ["moments"; "cumulants"; "free_cumulants"; "mstate"; "comoments"]) && active f ->
                    (match evalExpr statics maxSteps kExpr with
                     | Ok (SVInt k) when k >= 1L && k <= 8L -> Some (a, int k)
                     | _ -> None)
                | _ -> None)
            |> List.fold (fun m (a, k) -> Map.add a (max k (defaultArg (Map.tryFind a m) 0)) m) Map.empty
        let ctx = { Arrays = arrays; Aliases = aliases; Statics = statics; Indep = indep
                    Pools = ref Map.empty; PoolMax = poolMax; FlatDims = ref Map.empty }
        // Pass 2: rewrite decl-RHS former calls, threading the dist registry
        // (dist bindings are compile-time objects: consumed here, their
        // cumulant components materialize as ordinary array decls).
        let expanded =
            rest |> List.fold (fun acc d ->
                acc |> Result.bind (fun (ds, dists, mstates) ->
                    // Stamp the user decl's span so every syn-built node
                    // (former-generated decls) attributes to this decl's line.
                    Blade.Ast.synthSpan <- d.Span
                    match d.Value with
                    // moments(d, k) on a DIST binding: reconstruction (κ→μ,
                    // Wick under the carried-order closure) — dispatched by
                    // registry membership, ahead of the data-array form.
                    | DeclLet ({ Pattern = { Kind = PatternKind.PatVar _ }; Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "moments" }, [{ Kind = ExprKind.ExprVar dn }; kExpr]) } } as b) when active "moments" && Map.containsKey dn dists ->
                        let info = dists.[dn]
                        distDim ctx info
                        |> Result.bind (fun dim -> elabMomentsOfDist ctx d.Span b info dim kExpr)
                        |> Result.map (fun nds -> (ds @ nds, dists, mstates))
                    | DeclLet ({ Pattern = { Kind = PatternKind.PatVar outName }; Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "moments" }, args) } } as b) when active "moments" ->
                        elabMoments ctx d.Span outName b args |> Result.map (fun nds -> (ds @ nds, dists, mstates))
                    | DeclLet ({ Pattern = { Kind = PatternKind.PatVar outName }; Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "mixed_cumulants" }, args) } } as b) when active "mixed_cumulants" ->
                        elabMixedCumulants ctx d.Span outName b args |> Result.map (fun nds -> (ds @ nds, dists, mstates))
                    | DeclLet ({ Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "dist_affine" }, args) } } as b) when active "dist_affine" ->
                        elabDistAffine ctx d.Span b dists args |> Result.map (fun nds -> (ds @ nds, dists, mstates))
                    | DeclLet ({ Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "free_cumulants" }, args) } } as b) when active "free_cumulants" ->
                        elabFreeCumulants ctx d.Span b args |> Result.map (fun nds -> (ds @ nds, dists, mstates))
                    | DeclLet ({ Pattern = { Kind = PatternKind.PatVar outName }; Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "comoments" }, args) } } as b) when active "comoments" ->
                        elabComoments ctx d.Span outName b args |> Result.map (fun nds -> (ds @ nds, dists, mstates))
                    | DeclLet ({ Pattern = { Kind = PatternKind.PatVar outName }; Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "cumulants" }, args) } } as b) when active "cumulants" ->
                        elabCumulants ctx d.Span outName b args |> Result.map (fun nds -> (ds @ nds, dists, mstates))
                    | DeclLet ({ Pattern = { Kind = PatternKind.PatVar outName }; Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "comoments_merge" }, args) } } as b) when active "comoments_merge" ->
                        elabComomentsMerge ctx d.Span outName b args |> Result.map (fun nds -> (ds @ nds, dists, mstates))
                    | DeclLet { Pattern = { Kind = PatternKind.PatVar dName }; Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "dist" }, args) } } when active "dist" ->
                        elabDist ctx d.Span dName args |> Result.map (fun (nds, info) -> (ds @ nds @ [distPackDecl d.Span dName info], Map.add dName info dists, mstates))
                    | DeclLet { Pattern = { Kind = PatternKind.PatVar dName }; Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "dist_add" }, args) } } when active "dist_add" ->
                        elabDistCombine "+" (fun _ -> 1.0) ctx d.Span dName dists args |> Result.map (fun (nds, info) -> (ds @ nds @ [distPackDecl d.Span dName info], Map.add dName info dists, mstates))
                    // Dist OPERATORS (+ / − / scalar *) flow through
                    // untouched: dists are VALUES (distPackDecl), and the
                    // checker's inferDistBinOp dispatches operators in any
                    // expression position — module decl-RHS included —
                    // gated on the independence state this module exports
                    // (Independence.addDeclared/addSources below). The
                    // elaboration-level operator rewrites this replaced
                    // lived here until the typed-Dist arc's phase 5.
                    | DeclLet { Pattern = { Kind = PatternKind.PatVar dName }; Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "dist_scale" }, args) } } when active "dist_scale" ->
                        elabDistScale ctx d.Span dName dists args |> Result.map (fun (nds, info) -> (ds @ nds @ [distPackDecl d.Span dName info], Map.add dName info dists, mstates))
                    // The Faà di Bruno pushforward: a univariate order-q
                    // dist with FLAT 1-cell components. Registered (it
                    // composes with moments-on-dist/dist_affine/further
                    // jets) but NOT packed: __dist_pack's erasure type
                    // declares SymIdx-packed components, and flat ArrayLits
                    // aren't — the same wart dist_affine documents. The
                    // packed-literal arc upgrades this to a first-class
                    // typed Dist; until then cumulant(d, k) on flat dists
                    // projects at elaboration (arm below).
                    | DeclLet { Pattern = { Kind = PatternKind.PatVar dName }; Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "dist_jet" }, args) } } when active "dist_jet" ->
                        elabDistJet false "dist_jet" ctx d.Span dName dists args |> Result.map (fun (nds, info) -> (ds @ nds, Map.add dName info dists, mstates))
                    | DeclLet { Pattern = { Kind = PatternKind.PatVar dName }; Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "dist_jet_closed" }, args) } } when active "dist_jet_closed" ->
                        elabDistJet true "dist_jet_closed" ctx d.Span dName dists args |> Result.map (fun (nds, info) -> (ds @ nds, Map.add dName info dists, mstates))
                    // dist_map: the symbolic front-end — same registration
                    // and flat-component representation as dist_jet.
                    | DeclLet { Pattern = { Kind = PatternKind.PatVar dName }; Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "dist_map" }, args) } } when active "dist_map" ->
                        elabDistMap false ctx d.Span dName funcDecls dists args |> Result.map (fun (nds, info) -> (ds @ nds, Map.add dName info dists, mstates))
                    | DeclLet { Pattern = { Kind = PatternKind.PatVar dName }; Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "dist_map_closed" }, args) } } when active "dist_map_closed" ->
                        elabDistMap true ctx d.Span dName funcDecls dists args |> Result.map (fun (nds, info) -> (ds @ nds, Map.add dName info dists, mstates))
                    // Tower Bayes: dist_expect is a scalar projection;
                    // dist_reweight / dist_mix register flat univariate
                    // dists exactly like the jet results above (composable
                    // with moments-on-dist, further reweights, and
                    // cumulant(d, k) via the flat-projection arm below).
                    | DeclLet ({ Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "dist_expect" }, args) } } as b) when active "dist_expect" ->
                        elabDistExpect ctx d.Span b dists args |> Result.map (fun nds -> (ds @ nds, dists, mstates))
                    | DeclLet { Pattern = { Kind = PatternKind.PatVar dName }; Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "dist_reweight" }, args) } } when active "dist_reweight" ->
                        elabDistReweight ctx d.Span dName dists args |> Result.map (fun (nds, info) -> (ds @ nds, Map.add dName info dists, mstates))
                    | DeclLet { Pattern = { Kind = PatternKind.PatVar dName }; Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "dist_mix" }, args) } } when active "dist_mix" ->
                        elabDistMix ctx d.Span dName dists args |> Result.map (fun (nds, info) -> (ds @ nds, Map.add dName info dists, mstates))
                    // Signed atomic towers: quasi-dists as registered
                    // values; negativity as a scalar meter.
                    | DeclLet { Pattern = { Kind = PatternKind.PatVar dName }; Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "dist_atoms" }, args) } } when active "dist_atoms" ->
                        elabDistAtoms ctx d.Span dName args |> Result.map (fun (nds, info) -> (ds @ nds, Map.add dName info dists, mstates))
                    | DeclLet ({ Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "dist_negativity" }, args) } } as b) when active "dist_negativity" ->
                        elabDistNegativity ctx d.Span b dists args |> Result.map (fun nds -> (ds @ nds, dists, mstates))
                    // cumulant(d, k) on a FLAT registry dist (a pushforward
                    // result): no packed value exists for the checker's
                    // Dist-typed projection to see, so project here — the
                    // order guard is an elaboration error with the checker
                    // arm's steering. DELETE when packed literals land and
                    // jet results __dist_pack like everything else.
                    | DeclLet ({ Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "__ppl_cumulant" }, [{ Kind = ExprKind.ExprVar dn }; kExpr]) } } as b)
                        when Map.containsKey dn dists && dists.[dn].Flat ->
                        let info = dists.[dn]
                        (match evalExpr ctx.Statics maxSteps kExpr with
                         | Ok (SVInt k) when k >= 1L && int k <= info.Order ->
                             Ok (ds @ [ { d with Value = DeclLet { b with Value = mkExpr d.Span (ExprVar info.Components.[int k - 1]) } } ], dists, mstates)
                         | Ok (SVInt k) ->
                             Error (sprintf "cumulant: order %d exceeds the dist's carried order %d — insufficient stochastic order. Construct with a higher order or project a carried component." k info.Order)
                         | _ ->
                             Error "cumulant: the order must be a compile-time integer (a literal, `let static`, or static-function call)")
                    // Streaming state formers (clause 2 of the staging
                    // contract, arbitrary order): mstate/mstate_merge bind
                    // compile-time state objects; mstate_cumulants freezes
                    // one into destructured cumulant tensors.
                    | DeclLet { Pattern = { Kind = PatternKind.PatVar sName }; Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "mstate" }, args) } } when active "mstate" ->
                        elabMState ctx d.Span sName args |> Result.map (fun (nds, info) -> (ds @ nds, dists, Map.add sName info mstates))
                    | DeclLet { Pattern = { Kind = PatternKind.PatVar outName }; Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "mstate_merge" }, args) } } when active "mstate_merge" ->
                        elabMStateMerge ctx d.Span outName mstates args |> Result.map (fun (nds, info) -> (ds @ nds, dists, Map.add outName info mstates))
                    | DeclLet ({ Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "mstate_cumulants" }, args) } } as b) when active "mstate_cumulants" ->
                        elabMStateCumulants ctx d.Span b mstates args |> Result.map (fun nds -> (ds @ nds, dists, mstates))
                    // cumulant(d, k) flows through untouched: it is a
                    // checker-level projection on the Dist-typed value that
                    // distPackDecl binds (TypeCheck.inferCumulantProj — the
                    // order guard is a type error there).
                    | _ -> Ok (ds @ [d], dists, mstates)))
                (Ok ([], Map.empty, Map.empty))
        // Pass 3: any surviving former reference is misplaced — the v1
        // contract is decl-RHS only, and `independent` only as a consumed
        // declaration. Fail with guidance rather than letting typecheck
        // report an unbound name.
        expanded |> Result.bind (fun (ds, dists, _mstates) ->
            let activeFormers = formerNames |> Set.filter active
            let misplaced =
                ds |> List.exists (fun d ->
                    let check e = anyExpr (isFormerCallOf activeFormers) e
                    match d.Value with
                    | DeclFunction fd -> check fd.Body
                    | DeclLet b | DeclStatic b -> check b.Value
                    | _ -> false)
            if misplaced then
                Error "moments/comoments must be the entire right-hand side of a top-level let (moments(A, k) as a nested expression is deferred); independent(X, Y) must be a top-level `let _ = ppl.independent(X, Y)` declaration"
            else
                // Export this module's independence state for the checker:
                // the declared relation gates Dist ± dispatch and call-site
                // discharge; dist sources seed value provenance.
                Independence.addDeclared indep
                Independence.addSources (dists |> Map.map (fun _ info -> info.Sources))
                // Every dist, packed or not — Ide.fs only consults this for
                // names the typed program has no binding under, so the
                // packed ones' real types keep winning.
                IdeDists.add (dists |> Map.map (fun _ info -> (info.Order, info.Components)))
                Ok ds)

/// Import-gated wrapper. With no `import ppl` in the module, PPL elaboration
/// is a no-op — bare former names are left unbound (a normal type error),
/// never rewritten. With an alias in scope, the qualified surface
/// (`ppl.moments(...)`, `ppl.cumulant(...)`) is normalized to the internal
/// forms the core passes and the checker recognize, then the core runs
/// unchanged. The `import ppl` decl itself is consumed here.
let private expandModule (decls: Located<Decl> list) : Result<Located<Decl> list, string> =
    pplAliasesOf decls |> Result.bind (fun aliases ->
        if Set.isEmpty aliases then Ok decls
        else
            decls
            |> List.filter (not << isPplImport)
            |> List.map (stripDecl aliases)
            |> expandModuleCore)

/// Entry point: elaborate PPL formers across a program. Runs after ML-op
/// elaboration and before grad expansion, so grad() differentiates the
/// generated pipelines as plain Blade source.
let private expandStr (program: Program) : Result<Program, string> =
    // Register the `indep` where-clause handler (idempotent) and start a
    // fresh independence state for this compilation — expand always runs
    // before checkProgram in the same async flow, so the checker sees this
    // program's declared relation and dist sources.
    Independence.register ()
    Independence.reset ()
    IdeDists.reset ()
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
        [ Blade.Diagnostics.mkError "BL5100" (Blade.Diagnostics.Codes.phaseOfCode "BL5100") Blade.Ast.synthSpan msg ])
