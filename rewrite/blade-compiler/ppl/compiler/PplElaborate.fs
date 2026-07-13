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
/// A moment-formed array must be a module-level `let`/`let static` with an
/// Array annotation (`Array<Elem like I1, ..., Ik, SampleIdx>`) whose index
/// extents resolve statically (directly or through type aliases) — the
/// binding-time contract: shape is compile-time, data rides through.
module Blade.Ppl.Elaborate

open Blade.Ast
open Blade.StaticEval

// ============================================================================
// AST construction helpers (mirroring MLElaborate.fs / Grad.fs style)
// ============================================================================

let private v (n: string) = ExprVar n
let private fLit (x: float) = ExprLit (LitFloat x)
let private addE a b = ExprBinOp (Elementwise, OpAdd, a, b)
let private divE a b = ExprBinOp (Elementwise, OpDiv, a, b)
let private mulE a b = ExprBinOp (Elementwise, OpMul, a, b)
let private subE a b = ExprBinOp (Elementwise, OpSub, a, b)
let private sLet n value = StmtLet { Pattern = PatVar n; Type = None; Value = value; Mutability = BindLet }
let private meanE arr n = divE (ExprReduce (arr, ExprSection OpAdd, None)) (fLit n)
let private prodsumE args = ExprApp (v "prodsum", args)
let private commWhere (names: string list) =
    Some { Commutativity = [names]; Parallel = []; TDims = []; Custom = [] }
/// Inline co-iteration pipeline over same-shape (packed included) arrays:
/// method_for(zip(a, b)) <@> lambda(u, w) -> body |> compute — the corpus-
/// blessed one-binding form (sql-set-ops/004).
let private zipMap2 (a: Expr) (b: Expr) (body: Expr) =
    ExprCompute (ExprBinOp (Elementwise, OpApply,
        ExprMethodFor [ExprZip [a; b]],
        ExprLambda ([{ Name = "__u"; Type = None }; { Name = "__w"; Type = None }], None, body)))
let private map1 (a: Expr) (body: Expr) =
    ExprCompute (ExprBinOp (Elementwise, OpApply,
        ExprMethodFor [a],
        ExprLambda ([{ Name = "__u"; Type = None }], None, body)))

// NOTE: "cumulant" is deliberately NOT a former name anymore — cumulant(d, k)
// is a checker-level projection on Dist-typed values (TypeCheck's
// inferCumulantProj, order guard as a type error), valid in any expression
// position, so elaboration must let it flow through untouched.
let private formerNames = set [ "moments"; "comoments"; "cumulants"; "independent"; "dist"; "dist_add"; "dist_scale"; "comoments_merge"; "mstate"; "mstate_merge"; "mstate_cumulants"; "mixed_cumulants"; "dist_affine"; "free_cumulants" ]

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

let private iLit (n: int) = ExprLit (LitInt (int64 n))

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
    ExprBinOp (Elementwise, OpDiv, poolRead pool s, ExprLit (LitFloat pool.N))

/// Emit the single-pass pool over a shared row list. `uniq` seeds binding
/// names (the former's output name keeps them unique per former); `rows` =
/// one slice expression per row position; `needed` = the multisets the
/// caller's cells will read (deduped/canonicalized here, deterministic
/// size-then-lex order). Returns the decls and the read handle.
let private poolDecls (span: Span) (uniq: string) (rows: Expr list)
    (needed: int list list) (n: float) : Located<Decl> list * PoolInfo =
    let mkDecl name value =
        { Value = DeclLet { Pattern = PatVar name; Type = None; Value = value; Mutability = BindLet }; Span = span }
    let rowName i = sprintf "__ppl_row_%s_%d" uniq i
    let rowDecls = rows |> List.mapi (fun i e -> mkDecl (rowName i) e)
    let lName = sprintf "__ppl_poolL_%s" uniq
    let lValue =
        match rows with
        | [_] -> ExprMethodFor [v (rowName 0)]
        | _ -> ExprMethodFor [ExprZip [ for i in 0 .. rows.Length - 1 -> v (rowName i) ]]
    let sets = needed |> List.map List.sort |> List.distinct |> List.sortBy (fun s -> (s.Length, s))
    let tag (s: int list) = s |> List.map string |> String.concat "_"
    let pName s = sprintf "__ppl_P_%s_%s" uniq (tag s)
    let kName s = sprintf "__ppl_poolk_%s_%s" uniq (tag s)
    let xName i = sprintf "__x%d" i
    let ps = [ for i in 0 .. rows.Length - 1 -> { Name = xName i; Type = None } ]
    let kDecls =
        sets |> List.map (fun s ->
            let body = s |> List.map (fun i -> v (xName i)) |> List.reduce mulE
            mkDecl (kName s) (ExprLambda (ps, None, body)))
    let applied = sets |> List.map (fun s -> ExprBinOp (Elementwise, OpApply, v lName, v (kName s)))
    let chain =
        match applied with
        | first :: rest -> rest |> List.fold (fun acc e -> ExprBinOp (Elementwise, OpFusion, acc, e)) first
        | [] -> failwith "poolDecls: empty multiset list"
    let outPat =
        match sets with
        | [one] -> PatVar (pName one)
        | _ -> PatTuple (sets |> List.map (pName >> PatVar))
    let outDecl = { Value = DeclLet { Pattern = outPat; Type = None
                                      Value = ExprReduce (chain, ExprSection OpAdd, None)
                                      Mutability = BindLet }; Span = span }
    let names = sets |> List.map (fun s -> (s, pName s)) |> Map.ofList
    (rowDecls @ [mkDecl lName lValue] @ kDecls @ [outDecl], { Names = names; N = n })

/// Row slices of a single-leading-axis array: A(0) .. A(d-1).
let private rowSlices (aName: string) (d: int) : Expr list =
    [ for i in 0 .. d - 1 -> ExprApp (v aName, [ExprLit (LitInt (int64 i))]) ]

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
            | PatVar name, Some (TyArray (elem, idxs)) -> Map.add name (elem, idxs) acc
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

// ============================================================================
// Misplaced-use detection (v1 contract: formers are decl-RHS only)
// ============================================================================

let rec private anyExpr (p: Expr -> bool) (e: Expr) : bool =
    if p e then true else
    let any = anyExpr p
    match e with
    | ExprBinOp (_, _, l, r) -> any l || any r
    | ExprUnaryOp (_, x) -> any x
    | ExprApp (f, args) -> any f || List.exists any args
    | ExprTupleIndex (t, i) -> any t || any i
    | ExprField (o, _) -> any o
    | ExprLambda (_, _, b) -> any b
    | ExprLet (bind, body) -> any bind.Value || any body
    | ExprMatch (s, cases) -> any s || (cases |> List.exists (fun c -> any c.Body || (c.Guard |> Option.map any |> Option.defaultValue false)))
    | ExprIf (c, t, f) -> any c || any t || any f
    | ExprTuple es | ExprArrayLit es | ExprZip es | ExprStack es | ExprSequence es -> List.exists any es
    | ExprBlock (stmts, fin) ->
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
    | ExprStruct (_, fields) -> fields |> List.exists (snd >> any)
    | ExprTyped (x, _) -> any x
    | ExprMethodFor arrays -> List.exists any arrays
    | ExprObjectFor k -> any k
    | ExprAlign (es, _) -> List.exists any es
    | ExprPure x | ExprCompute x | ExprRead x | ExprUnique x | ExprRank x | ExprExtents x -> any x
    | ExprGuard (c, b) -> any c || any b
    | ExprReplicate (c, b) -> any c || any b
    | ExprMask (a, pr) | ExprCompound (a, pr) | ExprGroupBy (a, pr)
    | ExprIntersect (a, pr) | ExprUnion (a, pr) | ExprContains (a, pr)
    | ExprSort (a, pr) | ExprGram (a, pr) -> any a || any pr
    | ExprReduce (a, k, i) -> any a || any k || (i |> Option.map any |> Option.defaultValue false)
    | ExprAssign (l, r) -> any l || any r
    | _ -> false

let private isFormerCallOf (activeNames: Set<string>) (e: Expr) =
    match e with
    | ExprApp (ExprVar n, _) -> Set.contains n activeNames
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
    match e with
    | ExprBinOp (m, OpAnd, l, r) ->
        let (il, le) = splitInvariant l
        let (ir, re) = splitInvariant r
        let residual =
            match le, re with
            | Some a, Some b -> Some (ExprBinOp (m, OpAnd, a, b))
            | Some a, None | None, Some a -> Some a
            | None, None -> None
        (il @ ir, residual)
    | ExprApp (ExprVar "indep", [ExprVar x; ExprVar y]) -> ([(x, y)], None)
    | other -> ([], Some other)

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
    | [ExprVar aName; kExpr] ->
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
                pd @ [ { Value = DeclLet { binding with Value = ExprArrayLit cells }; Span = span } ]
            | _ ->
                // Multiaxis / unresolvable leading extent: per-cell pipeline.
                let paramNames = [ for i in 1 .. k -> sprintf "__x%d" i ]
                let ps = paramNames |> List.map (fun p -> { Name = p; Type = Some (TyArray (elem, [fiber])) })
                let whereC = if k >= 2 then commWhere paramNames else None
                let body = divE (prodsumE (paramNames |> List.map v)) (fLit (float n))
                let lName = sprintf "__ppl_L_%s" outName
                let kName = sprintf "__ppl_k_%s" outName
                let mk value = { Pattern = PatVar ""; Type = None; Value = value; Mutability = BindLet }
                [ { Value = DeclLet { mk (ExprMethodFor (List.replicate k (v aName))) with Pattern = PatVar lName }; Span = span }
                  { Value = DeclLet { mk (ExprLambda (ps, whereC, body)) with Pattern = PatVar kName }; Span = span }
                  { Value = DeclLet { binding with Value = ExprCompute (ExprBinOp (Elementwise, OpApply, v lName, v kName)) }; Span = span } ]))
    | _ ->
        Error "moments expects moments(A, k): an annotated module-level array and a static order"

/// Central pair kernel body: E[ab] - ma*mb, spelled over reduce/prodsum
/// (both proven kernel-position primitives; no elementwise fiber algebra).
let private centralPairBody (n: float) =
    ExprBlock (
        [ sLet "__ma" (meanE (v "__x1") n)
          sLet "__mb" (meanE (v "__x2") n) ],
        Some (subE (divE (prodsumE [v "__x1"; v "__x2"]) (fLit n)) (mulE (v "__ma") (v "__mb"))))

/// comoments(A, 2) same-array | comoments(X, Y) cross-block.
let private elabComoments (ctx: Ctx) (span: Span) (outName: string) (binding: Binding) (args: Expr list)
    : Result<Located<Decl> list, string> =
    let lName = sprintf "__ppl_L_%s" outName
    let kName = sprintf "__ppl_k_%s" outName
    let mkDecl pat value = { Value = DeclLet { Pattern = PatVar pat; Type = None; Value = value; Mutability = BindLet }; Span = span }
    match args with
    // Same-array central comoment of static order (only 2 for now)
    | [ExprVar aName; kExpr] when (match evalExpr ctx.Statics maxSteps kExpr with Ok (SVInt _) -> true | _ -> false) ->
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
                    pd @ [ { Value = DeclLet { binding with Value = ExprArrayLit cells }; Span = span } ]
                | _ ->
                    let ps = ["__x1"; "__x2"] |> List.map (fun p -> { Name = p; Type = Some (TyArray (elem, [fiber])) })
                    [ mkDecl lName (ExprMethodFor [v aName; v aName])
                      mkDecl kName (ExprLambda (ps, commWhere ["__x1"; "__x2"], centralPairBody (float n)))
                      { Value = DeclLet { binding with Value = ExprCompute (ExprBinOp (Elementwise, OpApply, v lName, v kName)) }; Span = span } ])
        | _ ->
            Error "comoments: only order 2 (covariance) is supported so far; higher central orders await the subset-lattice expansion over prodsums"
    // Cross block between two distinct arrays
    | [ExprVar xName; ExprVar yName] ->
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
                        let zeros = ExprArrayLit (List.replicate dx (ExprArrayLit (List.replicate dy (fLit 0.0))))
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
                        ExprArrayLit
                            [ for i in 0 .. dx - 1 ->
                                ExprArrayLit
                                    [ for j in 0 .. dy - 1 ->
                                        subE (poolMoment pool [i; dx + j])
                                             (mulE (poolMoment pool [i]) (poolMoment pool [dx + j])) ] ]
                    Ok (pd @ [ { Value = DeclLet { binding with Value = cells }; Span = span } ])
                | _ ->
                    let px = { Name = "__x1"; Type = Some (TyArray (elemX, [fibX])) }
                    let py = { Name = "__x2"; Type = Some (TyArray (elemY, [fibY])) }
                    Ok [ mkDecl lName (ExprMethodFor [v xName; v yName])
                         mkDecl kName (ExprLambda ([px; py], None, centralPairBody (float nX)))
                         { Value = DeclLet { binding with Value = ExprCompute (ExprBinOp (Elementwise, OpApply, v lName, v kName)) }; Span = span } ]))
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
    ExprBlock (lets, Some (terms |> List.reduce addE))

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
    let outValue = ExprCompute (ExprBinOp (Elementwise, OpApply, v lName, v kName))
    let outDecl =
        match outBinding with
        | Some b -> DeclLet { b with Value = outValue }
        | None -> DeclLet { Pattern = PatVar outName; Type = None; Value = outValue; Mutability = BindLet }
    [ { Value = DeclLet { Pattern = PatVar lName; Type = None; Value = ExprMethodFor (List.replicate k (v aName)); Mutability = BindLet }; Span = span }
      { Value = DeclLet { Pattern = PatVar kName; Type = None; Value = ExprLambda (ps, whereC, body); Mutability = BindLet }; Span = span }
      { Value = outDecl; Span = span } ]

/// cumulants(A, r): the order-r joint cumulant tensor, SymIdx<r, D> packed.
let private elabCumulants (ctx: Ctx) (span: Span) (outName: string) (binding: Binding) (args: Expr list)
    : Result<Located<Decl> list, string> =
    match args with
    | [ExprVar aName; rExpr] ->
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
                pd @ [ { Value = DeclLet { binding with Value = ExprArrayLit cells }; Span = span } ]
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
}

let private distComponentName (dName: string) (k: int) = sprintf "__dist_%s_k%d" dName k

/// The binding that makes a dist a VALUE: `let d = __dist_pack(κ1, ..., κr)`.
/// The checker types the intrinsic as Dist<r, τ like axes> (nominal; erased
/// back to this same component tuple at zonk), so `d` is first-class — it
/// crosses function boundaries and cumulant(d, k) projects it anywhere.
let private distPackDecl (span: Span) (dName: string) (info: DistInfo) : Located<Decl> =
    { Value = DeclLet { Pattern = PatVar dName; Type = None
                        Value = ExprApp (v "__dist_pack", info.Components |> List.map v)
                        Mutability = BindLet }
      Span = span }

let private elabDist (ctx: Ctx) (span: Span) (dName: string) (args: Expr list)
    : Result<Located<Decl> list * DistInfo, string> =
    match args with
    | [ExprVar aName; rExpr] ->
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
                    yield { Value = DeclLet { Pattern = PatVar (lName k); Type = None; Value = ExprMethodFor (List.replicate k (v aName)); Mutability = BindLet }; Span = span }
                    yield { Value = DeclLet { Pattern = PatVar (kName k); Type = None; Value = ExprLambda (ps, whereC, cumulantKernelBody k (float n)); Mutability = BindLet }; Span = span } ]
            let applied = [ for k in 1 .. r -> ExprBinOp (Elementwise, OpApply, v (lName k), v (kName k)) ]
            let fusedVal =
                match applied with
                | [one] -> ExprCompute one
                | first :: restA -> ExprCompute (restA |> List.fold (fun acc e -> ExprBinOp (Elementwise, OpParallel, acc, e)) first)
                | [] -> ExprCompute (v aName)  // unreachable: r >= 1
            let outPat =
                match comps with
                | [one] -> PatVar one
                | _ -> PatTuple (comps |> List.map PatVar)
            let fusedDecl = { Value = DeclLet { Pattern = outPat; Type = None; Value = fusedVal; Mutability = BindLet }; Span = span }
            (stageDecls @ [fusedDecl], { Order = r; Components = comps; Sources = Set.singleton aName })))
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
    | [ExprVar n1; ExprVar n2] ->
        match Map.tryFind n1 dists, Map.tryFind n2 dists with
        | Some d1, Some d2 when d1.Order = d2.Order ->
            let missing =
                [ for s1 in d1.Sources do
                    for s2 in d2.Sources do
                      if not (Set.contains (indepKey s1 s2) ctx.Indep) then yield (s1, s2) ]
            match missing with
            | (s1, s2) :: _ ->
                Error (sprintf "dist %s: cumulants combine only for independent distributions — declare independence of %s and %s (loose `let _ = independent(...)` or a struct `where indep(...)`)" opName s1 s2)
            | [] ->
                let decls =
                    [ for k in 1 .. d1.Order ->
                        let outN = distComponentName dName k
                        let contrib =
                            if weight k = 1.0 then v "__w"
                            else mulE (fLit (weight k)) (v "__w")
                        { Value = DeclLet { Pattern = PatVar outN; Type = None
                                            Value = zipMap2 (v d1.Components.[k - 1]) (v d2.Components.[k - 1]) (addE (v "__u") contrib)
                                            Mutability = BindLet }
                          Span = span } ]
                let info = { Order = d1.Order
                             Components = [ for k in 1 .. d1.Order -> distComponentName dName k ]
                             Sources = Set.union d1.Sources d2.Sources }
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
    | [cExpr; ExprVar dn] ->
        match Map.tryFind dn dists with
        | Some d ->
            // κ_k(c·X) = c^k κ_k(X): multilinearity, spelled as k repeated
            // multiplications so c may be any (pure) scalar expression.
            let decls =
                [ for k in 1 .. d.Order ->
                    let outN = distComponentName dName k
                    let scaled = List.replicate k cExpr |> List.fold mulE (v "__u")
                    { Value = DeclLet { Pattern = PatVar outN; Type = None
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
    | [ExprVar cA; ExprVar mA; nAExpr; ExprVar cB; ExprVar mB; nBExpr] ->
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
            let mkDecl pat value = { Value = DeclLet { Pattern = PatVar pat; Type = None; Value = value; Mutability = BindLet }; Span = span }
            // δ = mB − mA (lockstep over the mean vectors)
            let deltaDecl = mkDecl deltaN (zipMap2 (v mA) (v mB) (subE (v "__w") (v "__u")))
            match Map.tryFind cA ctx.FlatDims.Value, Map.tryFind cB ctx.FlatDims.Value with
            | Some dA, Some dB when dA = dB ->
                // FLAT INPUTS (pool-path comoments / earlier flat merges):
                // fully cell-wise merge — δδᵀ inlined per cell, flat lex
                // reads on both chunks (the mstate_merge house style).
                let d = dA
                let dRead i = ExprApp (v deltaN, [iLit i])
                let cells = canonicalTuples d 2
                let merged =
                    ExprArrayLit
                        [ for k in 0 .. cells.Length - 1 ->
                            let (i, j) = (match cells.[k] with [i; j] -> (i, j) | _ -> (0, 0))
                            addE
                                (divE (addE (mulE (fLit nA) (ExprApp (v cA, [iLit k])))
                                            (mulE (fLit nB) (ExprApp (v cB, [iLit k])))) (fLit n))
                                (mulE (fLit (nA * nB / (n * n))) (mulE (dRead i) (dRead j))) ]
                ctx.FlatDims.Value <- Map.add outName d ctx.FlatDims.Value
                [ deltaDecl
                  { Value = DeclLet { binding with Value = merged }; Span = span } ]
            | _ ->
                // δδᵀ as a packed symmetric outer square (scalar comm kernel)
                let ddL = mkDecl ddLN (ExprMethodFor [v deltaN; v deltaN])
                let ddK = mkDecl ddKN (ExprLambda ([{ Name = "__a"; Type = None }; { Name = "__b"; Type = None }],
                                                   commWhere ["__a"; "__b"],
                                                   mulE (v "__a") (v "__b")))
                let dd = mkDecl ddN (ExprCompute (ExprBinOp (Elementwise, OpApply, v ddLN, v ddKN)))
                // merged = (nA·CA + nB·CB)/n + (nA·nB/n²)·δδᵀ, three-way lockstep
                let body =
                    addE
                        (divE (addE (mulE (fLit nA) (v "__ca")) (mulE (fLit nB) (v "__cb"))) (fLit n))
                        (mulE (fLit (nA * nB / (n * n))) (v "__dd"))
                let merged =
                    ExprCompute (ExprBinOp (Elementwise, OpApply,
                        ExprMethodFor [ExprZip [v cA; v cB; v ddN]],
                        ExprLambda ([{ Name = "__ca"; Type = None }; { Name = "__cb"; Type = None }; { Name = "__dd"; Type = None }], None, body)))
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
    if info.Packed then ExprApp (v name, labels |> List.map iLit)
    else ExprApp (v name, [iLit (lexOffsetOf info.Dim p labels)])

// (centralSumKernelBody — the per-cell kernel expansion of the central
// comoment sum — was deleted with the single-pass pool: elabMState now
// generates the same inclusion–exclusion cell-wise over pool reads.)

let private elabMState (ctx: Ctx) (span: Span) (sName: string) (args: Expr list)
    : Result<Located<Decl> list * MStateInfo, string> =
    match args with
    | [ExprVar aName; rExpr] ->
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
                    let mkDecl name value = { Value = DeclLet { Pattern = PatVar name; Type = None; Value = value; Mutability = BindLet }; Span = span }
                    let meanDecl = mkDecl meanN (ExprArrayLit [ for i in 0 .. d - 1 -> poolMoment pool [i] ])
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
                            mkDecl (mN p) (ExprArrayLit cells) ]
                    Ok (pd @ [meanDecl] @ mDecls, { Order = r; Dim = d; N = float n; Mean = meanN; Ms = [ for p in 2 .. r -> mN p ]; Packed = false })
                | None -> Error "mstate: the variable-axis extent must be statically known"
            | _ -> Error "mstate: one variable axis per array so far (multi-axis states deferred)"))
    | _ ->
        Error "mstate expects mstate(A, r): an annotated module-level array and a static order in 2..6"

let private elabMStateMerge (ctx: Ctx) (span: Span) (outName: string)
    (mstates: Map<string, MStateInfo>) (args: Expr list)
    : Result<Located<Decl> list * MStateInfo, string> =
    match args with
    | [ExprVar sa; ExprVar sb] ->
        match Map.tryFind sa mstates, Map.tryFind sb mstates with
        | Some a, Some b when a.Order = b.Order && a.Dim = b.Dim ->
            let n = a.N + b.N
            let cA = -b.N / n
            let cB = a.N / n
            let deltaN = mstateComponent outName "delta"
            let meanN = mstateComponent outName "mean"
            let mN p = mstateComponent outName (sprintf "m%d" p)
            let dRead lbl = ExprApp (v deltaN, [iLit lbl])
            let mSide (info: MStateInfo) (labels: int list) =
                if labels.IsEmpty then fLit info.N else mReadExpr info labels
            let mkDecl name value = { Value = DeclLet { Pattern = PatVar name; Type = None; Value = value; Mutability = BindLet }; Span = span }
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
                    mkDecl (mN p) (ExprArrayLit cells) ]
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
    | [ExprVar sn] ->
        match Map.tryFind sn mstates with
        | Some s ->
            let compNames =
                match binding.Pattern with
                | PatTuple pats when pats.Length = s.Order ->
                    let names = pats |> List.map (function PatVar nm -> Some nm | _ -> None)
                    if names |> List.forall Option.isSome then Ok (names |> List.map Option.get)
                    else Error "mstate_cumulants: destructure into plain names"
                | _ ->
                    Error (sprintf "mstate_cumulants: destructure the result — `let (k1, ..., k%d) = mstate_cumulants(%s)`" s.Order sn)
            compNames |> Result.map (fun names ->
                let mkDecl name value = { Value = DeclLet { Pattern = PatVar name; Type = None; Value = value; Mutability = BindLet }; Span = span }
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
                        mkDecl name (ExprArrayLit cells)
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
/// κ1 is a plain rank-1 array; κ_{q>=2} are method_for-packed (logical reads).
let private distKappaRead (info: DistInfo) (labels: int list) : Expr =
    let q = labels.Length
    ExprApp (v info.Components.[q - 1], labels |> List.map iLit)

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
        Ok [ { Value = DeclLet { binding with Value = ExprArrayLit cells }; Span = span } ]
    | _ -> Error "moments: on a dist, the order must be a compile-time integer in 1..8"

/// The dist's variable dimension, off its order-1 component's source array.
/// Registry-level v1: derived when the dist came from dist(A, r) over a
/// single-leading-axis array (the same constraint the streaming state has).
let private distDim (ctx: Ctx) (info: DistInfo) : Result<int, string> =
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
    | [ExprVar xName; ExprVar yName; pExpr; qExpr] ->
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
                        let zeros = ExprArrayLit (List.replicate cellCount (fLit 0.0))
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
                    Ok (pd @ [ { Value = DeclLet { binding with Value = ExprArrayLit cells }; Span = span } ])
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
                    let mkDecl name value = { Value = DeclLet { Pattern = PatVar name; Type = None; Value = value; Mutability = BindLet }; Span = span }
                    Ok [ mkDecl lName (ExprMethodFor ((List.replicate p (v xName)) @ (List.replicate q (v yName))))
                         mkDecl kName (ExprLambda (ps, whereC, cumulantKernelBody r (float nX)))
                         { Value = DeclLet { binding with Value = ExprCompute (ExprBinOp (Elementwise, OpApply, v lName, v kName)) }; Span = span } ]))))
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
    | [ExprVar wName; ExprVar dn] ->
        match Map.tryFind dn dists with
        | None -> Error "dist_affine expects dist_affine(W, d) with a previously declared dist binding d"
        | Some info ->
            distDim ctx info |> Result.bind (fun n ->
            match Map.tryFind wName ctx.Arrays with
            | Some (_, [im; inn]) ->
                match resolveExtent ctx.Aliases ctx.Statics im, resolveExtent ctx.Aliases ctx.Statics inn with
                | Some m, Some nCols when nCols = n ->
                    let compNames =
                        match binding.Pattern with
                        | PatTuple pats when pats.Length = info.Order ->
                            let names = pats |> List.map (function PatVar nm -> Some nm | _ -> None)
                            if names |> List.forall Option.isSome then Ok (names |> List.map Option.get)
                            else Error "dist_affine: destructure into plain names"
                        | _ -> Error (sprintf "dist_affine: destructure the result — `let (p1, ..., p%d) = dist_affine(%s, %s)`" info.Order wName dn)
                    compNames |> Result.map (fun names ->
                        let wRead i j = ExprApp (v wName, [iLit i; iLit j])
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
                            { Value = DeclLet { Pattern = PatVar nm; Type = None; Value = ExprArrayLit cells; Mutability = BindLet }; Span = span }))
                | Some _, Some nCols ->
                    Error (sprintf "dist_affine: W's column count (%d) must match the dist's dimension (%d)" nCols n)
                | _ -> Error "dist_affine: W's extents must be statically known"
            | _ -> Error "dist_affine: W must be an annotated module-level m×n array (Array<Elem like Idx<m>, Idx<n>>)")
    | _ ->
        Error "dist_affine expects dist_affine(W, d)"

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
    | [ExprVar aName; rExpr] ->
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
                        match binding.Pattern with
                        | PatTuple pats when pats.Length = r ->
                            let names = pats |> List.map (function PatVar nm -> Some nm | _ -> None)
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
                            if labels.Length = 1 then ExprApp (v fkNames.[0], labels |> List.map iLit)
                            else ExprApp (v fkNames.[labels.Length - 1], [iLit (lexOffsetOf d labels.Length labels)])
                        let fkDecl p nm =
                            if p = 1 then
                                { Value = DeclLet { Pattern = PatVar nm; Type = None
                                                    Value = ExprArrayLit [ for i in 0 .. d - 1 -> poolMoment pool [i] ]
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
                                { Value = DeclLet { Pattern = PatVar nm; Type = None; Value = ExprArrayLit cells; Mutability = BindLet }; Span = span }
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
                        Error (sprintf "call to '%s' requires indep(%s, %s): sources '%s' and '%s' are not declared independent — add `let _ = independent(%s, %s)` (or a struct/function `where indep(...)` license)" funcName a b s1 s2 s1 s2)
            | _ -> Error "indep expects exactly two arguments"
    }

    let register () = Blade.Constraints.registerConstraint "indep" indepHandler

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
                  sLet (kN k) (ExprApp (v "cumulant", [v dN; ExprLit (LitInt (int64 k))])) ]
            @ [ for k in 1 .. order ->
                  let scaled = List.replicate k c |> List.fold mulE (v "__u")
                  sLet (sN k) (map1 (v (kN k)) scaled) ]
        ExprBlock (stmts, Some (ExprApp (v "__dist_pack", [ for k in 1 .. order -> v (sN k) ])))

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
                  yield sLet (aN k) (ExprApp (v "cumulant", [v lN; ExprLit (LitInt (int64 k))]))
                  yield sLet (bN k) (ExprApp (v "cumulant", [v rN; ExprLit (LitInt (int64 k))])) ]
            @ [ for k in 1 .. order ->
                  let contrib =
                      if weight k = 1.0 then v "__w"
                      else mulE (fLit (weight k)) (v "__w")
                  sLet (sN k) (zipMap2 (v (aN k)) (v (bN k)) (addE (v "__u") contrib)) ]
        ExprBlock (stmts, Some (ExprApp (v "__dist_pack", [ for k in 1 .. order -> v (sN k) ])))

// ============================================================================
// Module expansion
// ============================================================================

let private expandModule (decls: Located<Decl> list) : Result<Located<Decl> list, string> =
    // User definitions shadow the formers entirely (same rule as ML ops
    // and the math intrinsics).
    let declNames =
        decls |> List.choose (fun d ->
            match d.Value with
            | DeclFunction fd -> Some fd.Name
            | _ -> None)
        |> Set.ofList
    let active n = not (Set.contains n declNames)
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
                | DeclType (TyDeclStruct (sname, tps, fields, Some inv)) ->
                    let (pairs, residual) = splitInvariant inv
                    if pairs.IsEmpty then d
                    else
                        structIndep <- Map.add sname pairs structIndep
                        { d with Value = DeclType (TyDeclStruct (sname, tps, fields, residual)) }
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
                | DeclLet { Pattern = PatVar iname; Value = ExprStruct (sname, _) } when Map.containsKey sname structFields ->
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
                        match a with
                        | ExprField (ExprVar m, f) when Map.containsKey m instances ->
                            let al = aliasOf m f
                            let newDecls =
                                if Set.contains al emittedAliases then []
                                else
                                    emittedAliases <- Set.add al emittedAliases
                                    [ { Value = DeclLet { Pattern = PatVar al; Type = None; Value = a; Mutability = BindLet }; Span = d.Span } ]
                            (acc @ [v al], ads @ newDecls)
                        | other -> (acc @ [other], ads)) ([], [])
                match d.Value with
                | DeclLet ({ Value = ExprApp (ExprVar n, args) } as b) when Set.contains n formerNames && active n ->
                    let (args', aliasDecls) = normArgs args
                    aliasDecls @ [ { d with Value = DeclLet { b with Value = ExprApp (ExprVar n, args') } } ]
                | _ -> [d])
        // Pass 1: consume `let _ = independent(X, Y)` declarations.
        let mutable indep = structPairIndep
        let mutable rest = []
        let mutable err = None
        for d in decls do
            match d.Value with
            | DeclLet { Value = ExprApp (ExprVar "independent", args) } when active "independent" ->
                match args with
                | [ExprVar x; ExprVar y] when x <> y ->
                    indep <- Set.add (indepKey x y) indep
                | [ExprVar x; ExprVar y] when x = y ->
                    if err.IsNone then err <- Some (sprintf "independent(%s, %s): an array is not independent of itself" x y)
                | _ ->
                    if err.IsNone then err <- Some "independent expects two array names (or struct fields): `let _ = independent(X, Y)`"
            | _ -> rest <- rest @ [d]
        match err with
        | Some e -> Error e
        | None ->
        let arrays = aliasArrays |> Map.fold (fun acc k s -> Map.add k s acc) (collectArrays rest)
        // Pre-scan: the maximal multiset size each source array needs across
        // ALL its single-array formers in this module, so the first former
        // emits ONE maximal pool the rest reuse (cross-former single-pass).
        // Names that turn out to be dist bindings or ineligible arrays are
        // harmless here — the entry is only consulted on the pool path.
        let poolMax =
            rest |> List.choose (fun d ->
                match d.Value with
                | DeclLet { Value = ExprApp (ExprVar f, [ExprVar a; kExpr]) }
                    when (List.contains f ["moments"; "cumulants"; "free_cumulants"; "mstate"; "comoments"]) && active f ->
                    (match evalExpr statics maxSteps kExpr with
                     | Ok (SVInt k) when k >= 1L && k <= 8L -> Some (a, int k)
                     | _ -> None)
                | _ -> None)
            |> List.fold (fun m (a, k) -> Map.add a (max k (defaultArg (Map.tryFind a m) 0)) m) Map.empty
        let ctx = { Arrays = arrays; Aliases = collectAliases rest; Statics = statics; Indep = indep
                    Pools = ref Map.empty; PoolMax = poolMax; FlatDims = ref Map.empty }
        // Pass 2: rewrite decl-RHS former calls, threading the dist registry
        // (dist bindings are compile-time objects: consumed here, their
        // cumulant components materialize as ordinary array decls).
        let expanded =
            rest |> List.fold (fun acc d ->
                acc |> Result.bind (fun (ds, dists, mstates) ->
                    match d.Value with
                    // moments(d, k) on a DIST binding: reconstruction (κ→μ,
                    // Wick under the carried-order closure) — dispatched by
                    // registry membership, ahead of the data-array form.
                    | DeclLet ({ Pattern = PatVar _; Value = ExprApp (ExprVar "moments", [ExprVar dn; kExpr]) } as b) when active "moments" && Map.containsKey dn dists ->
                        let info = dists.[dn]
                        distDim ctx info
                        |> Result.bind (fun dim -> elabMomentsOfDist ctx d.Span b info dim kExpr)
                        |> Result.map (fun nds -> (ds @ nds, dists, mstates))
                    | DeclLet ({ Pattern = PatVar outName; Value = ExprApp (ExprVar "moments", args) } as b) when active "moments" ->
                        elabMoments ctx d.Span outName b args |> Result.map (fun nds -> (ds @ nds, dists, mstates))
                    | DeclLet ({ Pattern = PatVar outName; Value = ExprApp (ExprVar "mixed_cumulants", args) } as b) when active "mixed_cumulants" ->
                        elabMixedCumulants ctx d.Span outName b args |> Result.map (fun nds -> (ds @ nds, dists, mstates))
                    | DeclLet ({ Value = ExprApp (ExprVar "dist_affine", args) } as b) when active "dist_affine" ->
                        elabDistAffine ctx d.Span b dists args |> Result.map (fun nds -> (ds @ nds, dists, mstates))
                    | DeclLet ({ Value = ExprApp (ExprVar "free_cumulants", args) } as b) when active "free_cumulants" ->
                        elabFreeCumulants ctx d.Span b args |> Result.map (fun nds -> (ds @ nds, dists, mstates))
                    | DeclLet ({ Pattern = PatVar outName; Value = ExprApp (ExprVar "comoments", args) } as b) when active "comoments" ->
                        elabComoments ctx d.Span outName b args |> Result.map (fun nds -> (ds @ nds, dists, mstates))
                    | DeclLet ({ Pattern = PatVar outName; Value = ExprApp (ExprVar "cumulants", args) } as b) when active "cumulants" ->
                        elabCumulants ctx d.Span outName b args |> Result.map (fun nds -> (ds @ nds, dists, mstates))
                    | DeclLet ({ Pattern = PatVar outName; Value = ExprApp (ExprVar "comoments_merge", args) } as b) when active "comoments_merge" ->
                        elabComomentsMerge ctx d.Span outName b args |> Result.map (fun nds -> (ds @ nds, dists, mstates))
                    | DeclLet { Pattern = PatVar dName; Value = ExprApp (ExprVar "dist", args) } when active "dist" ->
                        elabDist ctx d.Span dName args |> Result.map (fun (nds, info) -> (ds @ nds @ [distPackDecl d.Span dName info], Map.add dName info dists, mstates))
                    | DeclLet { Pattern = PatVar dName; Value = ExprApp (ExprVar "dist_add", args) } when active "dist_add" ->
                        elabDistCombine "+" (fun _ -> 1.0) ctx d.Span dName dists args |> Result.map (fun (nds, info) -> (ds @ nds @ [distPackDecl d.Span dName info], Map.add dName info dists, mstates))
                    // Dist OPERATORS (+ / − / scalar *) flow through
                    // untouched: dists are VALUES (distPackDecl), and the
                    // checker's inferDistBinOp dispatches operators in any
                    // expression position — module decl-RHS included —
                    // gated on the independence state this module exports
                    // (Independence.addDeclared/addSources below). The
                    // elaboration-level operator rewrites this replaced
                    // lived here until the typed-Dist arc's phase 5.
                    | DeclLet { Pattern = PatVar dName; Value = ExprApp (ExprVar "dist_scale", args) } when active "dist_scale" ->
                        elabDistScale ctx d.Span dName dists args |> Result.map (fun (nds, info) -> (ds @ nds @ [distPackDecl d.Span dName info], Map.add dName info dists, mstates))
                    // Streaming state formers (clause 2 of the staging
                    // contract, arbitrary order): mstate/mstate_merge bind
                    // compile-time state objects; mstate_cumulants freezes
                    // one into destructured cumulant tensors.
                    | DeclLet { Pattern = PatVar sName; Value = ExprApp (ExprVar "mstate", args) } when active "mstate" ->
                        elabMState ctx d.Span sName args |> Result.map (fun (nds, info) -> (ds @ nds, dists, Map.add sName info mstates))
                    | DeclLet { Pattern = PatVar outName; Value = ExprApp (ExprVar "mstate_merge", args) } when active "mstate_merge" ->
                        elabMStateMerge ctx d.Span outName mstates args |> Result.map (fun (nds, info) -> (ds @ nds, dists, Map.add outName info mstates))
                    | DeclLet ({ Value = ExprApp (ExprVar "mstate_cumulants", args) } as b) when active "mstate_cumulants" ->
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
                Error "moments/comoments must be the entire right-hand side of a top-level let (moments(A, k) as a nested expression is deferred); independent(X, Y) must be a top-level `let _ = independent(X, Y)` declaration"
            else
                // Export this module's independence state for the checker:
                // the declared relation gates Dist ± dispatch and call-site
                // discharge; dist sources seed value provenance.
                Independence.addDeclared indep
                Independence.addSources (dists |> Map.map (fun _ info -> info.Sources))
                Ok ds)

/// Entry point: elaborate PPL formers across a program. Runs after ML-op
/// elaboration and before grad expansion, so grad() differentiates the
/// generated pipelines as plain Blade source.
let expand (program: Program) : Result<Program, string> =
    // Register the `indep` where-clause handler (idempotent) and start a
    // fresh independence state for this compilation — expand always runs
    // before checkProgram in the same async flow, so the checker sees this
    // program's declared relation and dist sources.
    Independence.register ()
    Independence.reset ()
    program.Modules
    |> List.fold (fun acc m ->
        acc |> Result.bind (fun ms ->
            expandModule m.Decls |> Result.map (fun ds -> ms @ [{ m with Decls = ds }])))
        (Ok [])
    |> Result.map (fun ms -> { program with Modules = ms })
