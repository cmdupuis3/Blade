/// The `where ml.perm_equiv(N)` discipline: a function carrying the
/// (normalized) `__ml_perm_equiv` conjunct is PROVED equivariant under the
/// symmetric group S_n acting by RELABELLING THE NODE AXIS of extent N -- its
/// body may compose only relabelling-equivariant operations. The judgment is
/// an abstract interpretation over the surface AST, run by MLElaborate at the
/// same pass-1/pass-2 seam as the equiv and galilean judgments.
///
/// This is the third of three abstract-interpretation walkers, after
/// MLEquiv.fs and MLGalilean.fs. The syntactic walk shared by all three lives
/// in MLCertShell.fs; what stays here -- the lattice, the signature
/// classifier, the judgment arms, the op table -- is what the three
/// disciplines DISAGREE about.
///
/// Comparing the three walkers found four silent divergences, all now fixed.
/// Two were false ACCEPTS:
///   1. THIS FILE's former arm cleared a node-covariant array in a former's
///      source list, because it scanned names and `freeVars` had no arm for
///      `method_for`. It now JUDGES the sources (MLGalilean's
///      judgeFormerApply shape) and scans captures besides -- neither check
///      subsumes the other. corpus ml-equiv/045, 046.
///   2. MLEquiv had the same hole one lattice over, and worse: with no
///      OpApply arm at all, a former over a rep answered Opaque, and a READ
///      out of an Opaque binding answered Inv -- unsound. corpus ml-equiv/049.
/// The other two: element-write INDICES went unjudged here and in MLEquiv
/// while MLGalilean folded over them (corpus ml-equiv/047, 048), and the
/// shared `freeVars` descended only one level into `for` bodies.
///
/// THE MORAL: the copies drift in the GUARDS, not the rules -- every
/// divergence was a place one walker checked something the others did not,
/// never a disagreement about the same check. A guard only one copy has is a
/// bug in the other two until argued otherwise.
///
/// THE LATTICE -- graded by k, OPPOSITE POLARITY to MLEquiv's at almost every
/// arm:
///   Pow k    -- a flat N^k buffer transforming as sigma^(x)k: relabelling the
///              nodes permutes its cells along each of the k axes. Pow 0 is
///              the INVARIANT status.
///   POpaque  -- unclassifiable; rejected wherever it meets a status-relevant
///              position.
///
/// The polarity table is the whole argument for a SIBLING lattice rather than
/// a `Rep of GroupSpec` payload on MLEquiv's:
///   pointwise nonlinearity on a rep    O(3): BL4008     S_n: LEGAL (Pow k)
///   elementwise product of two reps    O(3): BL4008     S_n: LEGAL (Pow k)
///   sum of two like reps               O(3): legal      S_n: legal
///   raw component read                 O(3): BL4008     S_n: legal in the
///                                                        MATH, deferred here
/// A permutation moves cells without mixing them, so EVERY POINTWISE MAP
/// COMMUTES WITH IT, while the Wigner action of O(3) mixes cells within a
/// block -- exactly what MLEquiv forbids. One judgment cannot wear both.
///
/// FLAT-BUFFER KEYING: the ops here consume FLAT ROW-MAJOR N^k buffers
/// (`derive_perm_linear`'s x is one `Idx<N^K>` axis), so the classifier keys
/// on the flat extent: `Array<Float like Idx<M>>` is Pow k iff M = N^k for
/// some 0 <= k <= MLPermSpec.maxPositions. k is unique because N >= 2 makes
/// the powers strictly increasing (N < 2 is refused: at N = 1 every extent is
/// 1 = N^k for every k). M = 1 and scalars are Pow 0. RANK >= 2 ARRAYS AND
/// NON-`Idx` INDEX TYPES ARE A HARD REJECT: v1 has one status per VALUE, and
/// a `batch x node` array needs one per AXIS -- per-axis status vectors are
/// the named v2 shape, unlocking O(3) x S_n dual certificates too.
///
/// EXTENT-KEYING CAVEAT: an `Idx<M>` whose M is not a power of N is Pow 0; a
/// COINCIDENTALLY N^k extent classifies covariant -- the conditional-theorem
/// reading ("IF this parameter transforms as sigma^(x)k THEN the result
/// transforms as declared"), so a coincidence falsifies the hypothesis for
/// that caller rather than the theorem. Nominal keying (`Nat<Node>`) is the
/// named upgrade.
///
/// COMPONENT ACCESS is the one place v1 is STRICTER than the math: a bound
/// index read is LEGAL for S_n (the node basis is real), but v1 has no
/// loop-variable tracking, so it cannot tell `x(i)` inside `for i in 0..N`
/// (equivariant) from `x(0)` (not), and REJECTS every read out of a Pow k,
/// k >= 1. Nothing is lost in practice: whole-array elementwise operators and
/// derive_perm_linear / derive_perm_bias / perm_matmul cover the rest.
///
/// THE RULES:
///   +, -, *, / of two Pow k (same k)          -> Pow k   (pointwise)
///   Pow k with a Pow 0 (scalar broadcast)     -> Pow k   (only constants are S_n-fixed)
///   unary minus / a pointwise scalar builtin  -> status preserved
///   ml.derive_perm_linear(K, L, N, x, w)      -> Pow L   (x : Pow K, w : Pow 0)
///   ml.derive_perm_bias(L, N, b)              -> Pow L   (b : Pow 0)
///   ml.perm_matmul(N, a, b)                   -> Pow 2   (a, b : Pow 2)
///   every other ml.* op                       : all-Pow-0 args -> Pow 0
///   a certified callee                        : signature match, N must agree
///   an uncertified callee                     : all-Pow-0 args -> Pow 0; a
///                                               Pow k (k >= 1) escaping rejects
/// Violations are BL4012 at the offending expression's span.
module Blade.ML.Perm

open Blade.Ast
open Blade.StaticEval
// The walker shell (freeVars / patternVars / bindPatternVars / judgeEach /
// conjunctsOf) is shared verbatim with MLEquiv and MLGalilean.
open Blade.ML.CertShell

type PowStatus =
    /// Pow k = a flat N^k buffer transforming as sigma^(x)k. Pow 0 = invariant,
    /// a claim the judgment must EARN: see PowUnsized.
    | Pow of int
    /// Invariant-SHAPED, but the walker never established the extent, so
    /// `Pow 0` is not available (only all-cells-equal N^k buffers, k > 0, are
    /// S_n-fixed). Refused wherever fixedness is load-bearing.
    | PowUnsized
    | POpaque

type PermSig = {
    /// The node-axis extent this certificate is about; certificates do not
    /// transfer between extents.
    N: int
    /// Parameter name -> status, in declaration order.
    Params: (string * PowStatus) list
    Return: PowStatus
}

// Helpers

let private fuel = 100_000

let private bl4012 (span: Span) (msg: string) : Blade.Diagnostics.Diagnostic =
    Blade.Diagnostics.mkError "BL4012" (Blade.Diagnostics.Codes.phaseOfCode "BL4012") span msg

let private statusStr (st: PowStatus) : string =
    match st with
    | Pow 0 -> "invariant (Pow 0 -- fixed by every node relabelling)"
    | Pow k -> $"node-covariant of rank {k} (Pow {k} -- a flat N^{k} buffer transforming as sigma^(x{k}))"
    | PowUnsized -> "invariant-shaped but of unestablished extent (it cannot be claimed fixed: if its cell count lands in a node-power space, an arbitrary buffer there is not S_n-fixed)"
    | POpaque -> "unclassifiable"

/// The rank cap: the same K + L bound the ops carry.
let private maxPow = Blade.ML.PermSpec.maxPositions

/// k with N^k = m, or None. N >= 2 makes the powers strictly increasing, so
/// at most one k can match -- the whole reason for the N < 2 refusal.
let private powClass (n: int64) (m: int64) : int option =
    let rec go (k: int) (acc: int64) =
        if acc = m then Some k
        elif k >= maxPow || acc > m then None
        else go (k + 1) (acc * n)
    if m < 1L then None else go 0 1L

/// Mirror of MLElaborate.staticArg / MLEquiv.staticArgValue (keep in sync): an
/// ML op's static argument is a `let static` binding name or an int literal.
let private staticArgValue (statics: StaticEnv) (e: Expr) : Result<StaticValue, string> =
    match e.Kind with
    | ExprKind.ExprLit (LitInt n) -> Ok (SVInt n)
    | ExprKind.ExprVar name ->
        match Map.tryFind name statics.Values with
        | Some sv -> Ok sv
        | None -> Error $"'{name}' is not a `let static` binding"
    | _ -> Error "expected a `let static` binding name or literal"

// Certified-signature table

/// Type aliases of this module (one-level chase, mirroring MLEquiv).
let private aliasMapOf (decls: Located<Decl> list) : Map<string, TypeExpr> =
    decls
    |> List.fold (fun m d ->
        match d.Value with
        | DeclType (TyDeclAlias (n, [], body)) -> Map.add n body m
        | _ -> m) Map.empty

/// The v2 pointer, attached to every signature-shape refusal.
let private v2Note =
    "node-covariance is keyed on the FLAT extent of a SINGLE `Idx<>` axis (the Sn ops consume flat row-major N^k buffers -- ml.derive_perm_linear's x is one Idx<N^K> axis), so a certified signature carries one status per VALUE. A multi-axis array would need one status per AXIS, which is not tracked. Flatten the buffer, or leave the function uncertified"

/// Alias-chase budget: `type A = B` chains are followed, but never in a cycle.
let private aliasDepth = 8

/// Classify ONE index type of a certified signature's array parameter.
let rec private statusOfIndex (depth: int) (n: int) (aliases: Map<string, TypeExpr>) (statics: StaticEnv) (ix: TypeExpr)
    : Result<PowStatus, string> =
    match ix with
    | TyIdx extentE ->
        evalExpr statics fuel extentE
        |> Result.mapError (fun m -> $"the Idx<> extent does not resolve statically ({m}) -- a perm-certified signature is classified by extent")
        |> Result.bind (fun sv ->
            match sv with
            | SVInt m ->
                // Extent-keying caveat: a non-power extent is invariant, a
                // coincidental N^k extent classifies covariant (see header).
                match powClass (int64 n) m with
                | Some k -> Ok (Pow k)
                | None -> Ok (Pow 0)
            | _ -> Error "the Idx<> extent must be a static int")
    | TyNamed (nm, []) when depth > 0 && (Map.containsKey nm aliases) ->
        statusOfIndex (depth - 1) n aliases statics (Map.find nm aliases)
    | TyNamed (nm, _) ->
        Error $"'{nm}' is not an `Idx<>` index type. {v2Note}"
    | _ ->
        Error $"only plain `Idx<>` axes are classified in a perm-certified signature. {v2Note}"

/// Classify a signature annotation. Certified functions must be fully
/// annotated; the rank-1 `Array<_ like Idx<M>>` shape is the only array form.
and private statusOfType (depth: int) (n: int) (aliases: Map<string, TypeExpr>) (statics: StaticEnv) (t: TypeExpr)
    : Result<PowStatus, string> =
    match t with
    | TyArray (_, idxs) ->
        match idxs with
        | [] -> Ok (Pow 0)
        | [ ix ] -> statusOfIndex depth n aliases statics ix
        | _ ->
            Error $"a rank-{idxs.Length} array cannot be classified in a perm-certified signature. {v2Note}"
    | TyNamed (nm, []) when depth > 0 && (Map.containsKey nm aliases) ->
        // `type X = Idx<N>` / `type Y = Float` used bare in a parameter slot.
        statusOfType (depth - 1) n aliases statics (Map.find nm aliases)
    | TyNamed (_, _) -> Ok (Pow 0) // scalar primitives and non-index named types
    | TyIdx _ -> statusOfIndex depth n aliases statics t
    // `min=`/`max=` refine the VALUE and erase before codegen, so they cannot
    // move an extent. `depth` is not spent -- a bound is not an alias hop.
    | TyBounded (baseTy, _, _) -> statusOfType depth n aliases statics baseTy
    | TyInt32 | TyInt64 | TyFloat32 | TyFloat64 | TyBool | TyComplex128 -> Ok (Pow 0)
    | _ ->
        Error $"cannot classify this annotation in a perm-certified signature (supported: scalars and `Array<_ like Idx<M>>`). {v2Note}"

/// The conjunct's single argument: an int literal or a `let static` name.
/// N >= 2 is required -- see powClass.
let private resolveN (statics: StaticEnv) (funcName: string) (args: string list) : Result<int, string> =
    match args with
    | [ a ] ->
        let raw =
            match System.Int32.TryParse a with
            | true, n -> Ok n
            | _ ->
                match Map.tryFind a statics.Values with
                | Some (SVInt n) -> Ok (int n)
                | Some _ -> Error $"function '{funcName}': perm_equiv({a}) -- '{a}' is a `let static` binding but not an int; N is the node-axis extent"
                | None -> Error $"function '{funcName}': perm_equiv({a}) -- N must be an int literal or the name of a `let static` int binding (the node-axis extent)"
        raw |> Result.bind (fun n ->
            if n < 2 then
                Error $"function '{funcName}': perm_equiv({n}) -- N must be >= 2. Node-covariance is classified by the FLAT extent of a parameter (Idx<M> is Pow k iff M = N^k), and only N >= 2 makes the powers N^0 < N^1 < ... strictly increasing, hence the rank unique; at N = 1 every extent is 1 = N^k for every k. S_1 is the trivial group, so the certificate would be vacuous in any case"
            else Ok n)
    | _ ->
        Error $"function '{funcName}': perm_equiv expects exactly one argument -- the node-axis extent N, as in `where ml.perm_equiv(4)`"

/// Pre-scan: every DeclFunction carrying a normalized ("__ml_perm_equiv", [N])
/// conjunct gets a certified signature. Errors are BL4012 at the decl.
let buildCertTable (statics: StaticEnv) (decls: Located<Decl> list)
    : Result<Map<string, PermSig>, Blade.Diagnostics.Diagnostic> =
    let aliases = aliasMapOf decls
    decls
    |> List.fold (fun acc d ->
        acc |> Result.bind (fun table ->
            match d.Value with
            | DeclFunction fd ->
                let conjs = conjunctsOf "__ml_perm_equiv" fd
                let fail msg = Error (bl4012 d.Span msg)
                match conjs with
                | [] -> Ok table
                | _ :: _ :: _ ->
                    fail $"function '{fd.Name}': duplicate perm_equiv constraints -- declare exactly one node-axis extent"
                | [ (_, args) ] ->
                    match resolveN statics fd.Name args with
                    | Error m -> fail m
                    | Ok n ->
                        let paramSt =
                            fd.Params
                            |> List.fold (fun acc p ->
                                acc |> Result.bind (fun ps ->
                                    match p.Type with
                                    | None ->
                                        Error $"function '{fd.Name}': a perm-certified function must annotate every parameter and its return type ('{p.Name}' is unannotated) -- the certificate is read off the extents"
                                    | Some t ->
                                        statusOfType aliasDepth n aliases statics t
                                        |> Result.mapError (sprintf "function '%s', parameter '%s': %s" fd.Name p.Name)
                                        |> Result.map (fun st -> ps @ [ (p.Name, st) ])))
                                (Ok [])
                        match paramSt with
                        | Error m -> fail m
                        | Ok ps ->
                            match fd.ReturnType with
                            | None -> fail $"function '{fd.Name}': a perm-certified function must annotate its return type"
                            | Some rt ->
                                match statusOfType aliasDepth n aliases statics rt
                                      |> Result.mapError (sprintf "function '%s', return type: %s" fd.Name) with
                                | Error m -> fail m
                                | Ok r -> Ok (Map.add fd.Name { N = n; Params = ps; Return = r } table)
            | _ -> Ok table))
        (Ok Map.empty)

// The judgment

/// The invariance evidence an UNCERTIFIED value of this type may claim. A
/// rank landing in the node-power space is NOT claimable, so it is unsized
/// rather than `Pow k`.
let private invEvidenceOfType (n: int) (aliases: Map<string, TypeExpr>) (statics: StaticEnv) (t: TypeExpr) : PowStatus =
    match statusOfType aliasDepth n aliases statics t with
    | Ok (Pow k) when k > 0 -> PowUnsized
    | Ok _ -> Pow 0
    | Error _ -> PowUnsized

/// The same judgement made from a known flat cell count (literal aggregates,
/// `range<Idx<M>>` iteration spaces).
let private invEvidenceOfCells (n: int) (cells: int64) : PowStatus =
    match powClass (int64 n) cells with
    | Some k when k > 0 -> PowUnsized
    | _ -> Pow 0

/// Module-level bindings: invariant by the conditional-theorem reading, but
/// their EXTENT decides whether that can be claimed as Pow 0 -- an
/// unannotated `let c = [0.0, 1.0, 2.0, 3.0]` at N = 4 is a constant in N^1,
/// not S_n-fixed.
let buildGlobals (n: int) (statics: StaticEnv) (decls: Located<Decl> list) : Map<string, PowStatus> =
    let aliases = aliasMapOf decls
    let evidence (b: Binding) =
        match b.Type with
        | Some t -> invEvidenceOfType n aliases statics t
        | None ->
            match b.Value.Kind with
            | ExprKind.ExprLit _ -> Pow 0
            | ExprKind.ExprArrayLit es | ExprKind.ExprTuple es -> invEvidenceOfCells n (int64 es.Length)
            | _ -> PowUnsized
    decls
    |> List.fold (fun m d ->
        match d.Value with
        | DeclLet b | DeclStatic b ->
            let st = evidence b
            patternVars b.Pattern
            |> List.fold (fun m2 nm ->
                match b.Pattern.Kind with
                | PatternKind.PatVar _ -> Map.add nm st m2
                | _ -> Map.add nm PowUnsized m2) m
        | _ -> m) Map.empty

/// Return annotations of every function, so a call to an uncertified helper
/// is classified by its declared extent instead of assumed fixed.
let buildReturnEvidence (n: int) (statics: StaticEnv) (decls: Located<Decl> list) : Map<string, PowStatus> =
    let aliases = aliasMapOf decls
    decls
    |> List.fold (fun m d ->
        match d.Value with
        | DeclFunction fd ->
            match fd.ReturnType with
            | Some t -> Map.add fd.Name (invEvidenceOfType n aliases statics t) m
            | None -> Map.add fd.Name PowUnsized m
        | _ -> m) Map.empty

type private Ctx = {
    FuncName: string
    /// The node-axis extent of THIS function's certificate.
    N: int
    /// ml-module aliases (the op rules).
    MlAliases: Set<string>
    Statics: StaticEnv
    Certs: Map<string, PermSig>
    /// Extent evidence for module-level bindings (see buildGlobals).
    Globals: Map<string, PowStatus>
    /// Extent evidence for uncertified callees' returns.
    Returns: Map<string, PowStatus>
}

/// Flat cell count of a `range<I1, ..., In>` iteration space: the product of
/// the axis extents. `None` when an axis does not resolve statically.
let private rangeCells (ctx: Ctx) (idxTypes: TypeExpr list) : int64 option =
    if idxTypes.IsEmpty then None
    else
        idxTypes
        |> List.fold (fun acc ix ->
            acc |> Option.bind (fun total ->
                match ix with
                | TyIdx extentE ->
                    (match evalExpr ctx.Statics fuel extentE with
                     | Ok (SVInt m) when m >= 1L -> Some (total * m)
                     | _ -> None)
                | _ -> None)) (Some 1L)

/// THE POLARITY DEMO: a permutation moves cells without mixing them, so
/// EVERY POINTWISE MAP COMMUTES WITH IT -- `f(sigma . x) = sigma . f(x)` for
/// any cell-by-cell f -- exactly the arm MLEquiv rejects with BL4008, because
/// the Wigner action mixes cells inside a block. Blade's scalar intrinsics
/// are SCALAR-ONLY (`exp(A)` is a type error, corpus intrinsics/006), so this
/// arm is reached only by an already-ill-typed body, but the lattice must not
/// be the thing that rejects a pointwise map, or the polarity would silently
/// be MLEquiv's. The WRITABLE form is the whole-array elementwise operators
/// below, the lines corpus ml-equiv/039 pins as accepted and which are
/// BL4008 verbatim under `ml.equiv(O3)`.
let private isPointwiseBuiltin (n: string) =
    List.contains n [ "exp"; "log"; "sqrt"; "sin"; "cos"; "tan"; "tanh"; "abs"; "floor"; "ceil"; "min"; "max"; "pow" ]

/// The result rank of a pointwise application: every non-invariant argument
/// must agree on its rank (a pointwise map of an N-vector and an N^2-matrix is
/// not one map, it is a broadcast across two different spaces).
let private combinePointwise (sts: PowStatus list) : Result<PowStatus, string> =
    if sts |> List.exists ((=) POpaque) then Ok POpaque
    else
        let ranks = sts |> List.choose (function Pow k when k > 0 -> Some k | _ -> None) |> List.distinct
        let unsized = sts |> List.exists ((=) PowUnsized)
        match ranks with
        | [] -> Ok (if unsized then PowUnsized else Pow 0)
        | _ when unsized ->
            Error "this pointwise operation combines a node power with a value whose extent the judgment never established. Broadcasting is sound only against something FIXED by relabelling, and an arbitrary buffer that lands in the node-power space is not (only the all-cells-equal ones are) -- annotate the operand's extent, or build the equivariant constants with ml.derive_perm_bias"
        | [ k ] -> Ok (Pow k)
        | _ ->
            Error (sprintf "this pointwise operation mixes node-covariant values of different ranks (%s) -- a pointwise map runs cell-by-cell over ONE space; contract the ranks first (ml.derive_perm_linear) or broadcast through an invariant"
                       (ranks |> List.map (sprintf "Pow %d") |> String.concat " and "))

let rec private judge (ctx: Ctx) (env: Map<string, PowStatus>) (e: Expr)
    : Result<PowStatus, Blade.Diagnostics.Diagnostic> =
    let reject msg = Error (bl4012 e.Span $"function '{ctx.FuncName}': {msg}")
    let j = judge ctx env
    match e.Kind with
    | ExprKind.ExprLit _ -> Ok (Pow 0)
    | ExprKind.ExprArrayLit es | ExprKind.ExprTuple es ->
        // A literal is a CONSTANT, but the only S_n-fixed vectors of a
        // node-power space are the all-cells-equal ones, and v1 does not
        // check cell equality, so a literal landing there is refused.
        es
        |> judgeEach j
        |> Result.bind (fun sts ->
            match sts |> List.tryFind (fun s -> match s with Pow k -> k > 0 | _ -> false) with
            | Some st ->
                reject $"packing a {statusStr st} into a literal aggregate loses its node-axis structure -- the aggregate does not transform as a node power"
            | None ->
                match powClass (int64 ctx.N) (int64 es.Length) with
                | Some k when k > 0 ->
                    reject $"a {es.Length}-cell literal aggregate lands in the node-power space N^{k}, and an arbitrary constant there is NOT S_n-invariant (only the constants with every cell equal are). The complete space of equivariant constants is ml.derive_perm_bias({k}, {ctx.N}, b)"
                | _ -> if sts |> List.exists ((=) POpaque) then Ok POpaque else Ok (Pow 0))
    | ExprKind.ExprVar n ->
        match Map.tryFind n env with
        | Some st -> Ok st
        // globals/constants/builtins: "held fixed" is only claimable as Pow 0
        // when the extent says so -- see buildGlobals.
        | None ->
            match Map.tryFind n ctx.Globals with
            | Some st -> Ok st
            | None -> Ok PowUnsized
    | ExprKind.ExprDotDot _ -> Ok PowUnsized
    | ExprKind.ExprTyped (inner, _) -> j inner
    | ExprKind.ExprUnaryOp (_, inner) ->
        // Pointwise, hence status-preserving -- the polarity arm again.
        j inner
    // Former application must dispatch before the general binop arm (OpApply
    // is a BinOp constructor).
    | ExprKind.ExprBinOp (_, OpApply, loop, _) ->
        // A former's kernel receives the ELEMENTS of its sources -- the
        // component read v1 defers -- so it is admissible only when nothing
        // node-covariant is in scope. TWO CHECKS, neither subsuming the other
        // (catalog finding 1): the SOURCES are JUDGED (a source need not be a
        // name -- `method_for(ml.derive_perm_bias(1,N,b)) <@>` builds a Pow 1
        // from invariants alone, invisible to a name scan), and freeVars
        // covers what the kernel CAPTURES. Before the fix there was only the
        // scan, no former arm, so `method_for(x) <@> lambda ...` over
        // covariant `x` came back Pow 0 and satisfied a requirePow-0 weight
        // position -- corpus ml-equiv/045. A single-source former's RESULT
        // inherits its source's extent; a cross-product former (`<*>`) does
        // not -- its extent is the PRODUCT of two possibly non-power extents.
        let sources =
            match loop.Kind with
            | ExprKind.ExprMethodFor arrays -> arrays
            | ExprKind.ExprFor (ForArrays (arrays, _), _, _) -> arrays
            | _ -> []
        sources
        |> judgeEach (judge ctx env)
        |> Result.bind (fun srcSts ->
            match srcSts |> List.tryFindIndex (fun s -> match s with Pow k -> k > 0 | _ -> false) with
            | Some i ->
                Error (bl4012 sources.[i].Span
                           ($"function '{ctx.FuncName}': the kernel of this former would receive COMPONENTS of a source that is {(statusStr srcSts.[i])}; component access inside perm-certified bodies requires per-axis tracking, which is not implemented. Use the whole-array elementwise operators (pointwise maps commute with relabelling) or ml.derive_perm_linear"))
            | None ->
                let covariant =
                    freeVars Set.empty e |> Set.toList |> List.tryFind (fun n ->
                        match Map.tryFind n env with Some (Pow k) -> k > 0 | _ -> false)
                match covariant with
                | Some n ->
                    reject $"the kernel of this former would receive COMPONENTS of node-covariant '{n}'; component access inside perm-certified bodies requires per-axis tracking, which is not implemented. Use the whole-array elementwise operators (pointwise maps commute with relabelling) or ml.derive_perm_linear"
                | None ->
                    // Only a single source, itself proven fixed, transfers
                    // that proof to the result; anything else is unsized.
                    match srcSts with
                    | [ Pow 0 ] -> Ok (Pow 0)
                    | _ -> Ok PowUnsized)
    | ExprKind.ExprBinOp (_, op, l, r) ->
        j l |> Result.bind (fun sl ->
        j r |> Result.bind (fun sr ->
            match op with
            // +, -, *, / are all POINTWISE, so all four preserve the rank --
            // the arm where MLEquiv admits only Rep +/- Rep and scaling by an
            // invariant, rejecting Rep * Rep outright (BL4008).
            | OpAdd | OpSub | OpMul | OpDiv ->
                combinePointwise [ sl; sr ]
                |> Result.mapError (fun m -> bl4012 e.Span $"function '{ctx.FuncName}': {m}")
            | _ ->
                match sl, sr with
                | Pow 0, Pow 0 -> Ok (Pow 0)
                | POpaque, _ | _, POpaque -> Ok POpaque
                | _ ->
                    reject "this operator is not classified on node-covariant values -- combine node powers with +, -, *, / (pointwise), or with ml.derive_perm_linear / ml.perm_matmul"))
    | ExprKind.ExprIf (c, t, f) ->
        j c |> Result.bind (fun sc ->
            match sc with
            | Pow 0 ->
                j t |> Result.bind (fun st ->
                j f |> Result.bind (fun sf ->
                    if st = sf then Ok st
                    else reject $"if branches disagree: then-branch is {statusStr st}, else-branch is {statusStr sf}"))
            | _ -> reject "an if condition inside a perm-certified body must be invariant -- branching on a node-covariant value makes the result depend on the node labelling")
    | ExprKind.ExprMatch (scrut, cases) ->
        j scrut |> Result.bind (fun ss ->
            match ss with
            | Pow 0 ->
                cases
                |> judgeEach (fun c -> judge ctx (bindPatternVars (Pow 0) env c.Pattern) c.Body)
                |> Result.bind (fun sts ->
                    match sts with
                    | [] -> Ok (Pow 0)
                    | s :: rest when rest |> List.forall ((=) s) -> Ok s
                    | _ -> reject "match arms disagree on their node-covariance status")
            | _ -> reject "a match scrutinee inside a perm-certified body must be invariant")
    | ExprKind.ExprLet (binding, body) ->
        j binding.Value |> Result.bind (fun sv ->
            match binding.Pattern.Kind, sv with
            | PatternKind.PatVar n, _ -> judge ctx (Map.add n sv env) body
            | _, (Pow 0 | PowUnsized) -> judge ctx (bindPatternVars PowUnsized env binding.Pattern) body
            | _, _ -> reject "cannot destructure a node-covariant value -- bind it whole")
    | ExprKind.ExprLambda (ps, _, lamBody) ->
        let captured = freeVars (Set.ofList (ps |> List.map _.Name)) lamBody
        let covCapture =
            captured |> Set.toList |> List.tryFind (fun n ->
                match Map.tryFind n env with Some (Pow k) -> k > 0 | _ -> false)
        match covCapture with
        | Some n -> reject $"lambda captures node-covariant '{n}' -- factor node work into perm-certified functions instead"
        | None -> Ok (Pow 0)
    | ExprKind.ExprAssign (l, r) ->
        judgeAssign ctx env e.Span l r |> Result.map (fun () -> Pow 0)
    | ExprKind.ExprBlock (stmts, finalE) ->
        judgeStmts ctx env stmts
        |> Result.bind (fun env' ->
            match finalE with
            | Some fe -> judge ctx env' fe
            | None -> Ok (Pow 0))
    | ExprKind.ExprApp (f, args) -> judgeApp ctx env e f args
    | ExprKind.ExprField (_, _) -> Ok POpaque
    // Virtual arrays enumerate INDICES, label-independent only when the index
    // set is not a node axis: `range<Idx<N>>` IS the node index set. The
    // extent is read from the annotation, not assumed.
    | ExprKind.ExprRange idxTypes ->
        (match rangeCells ctx idxTypes with
         | Some cells -> Ok (invEvidenceOfCells ctx.N cells)
         | None -> Ok PowUnsized)
    | ExprKind.ExprReverse _ | ExprKind.ExprHalo _ -> Ok PowUnsized
    // compute is a scheduling boundary, not a value transform.
    | ExprKind.ExprCompute x -> judge ctx env x
    // A reduce over a Pow k IS invariant for a commutative combiner, but v1
    // does not analyse it; the certified spelling is ml.derive_perm_linear.
    | ExprKind.ExprReduce (src, _, init, _) ->
        judge ctx env src |> Result.bind (fun ss ->
            (match init with
             | Some i -> judge ctx env i
             | None -> Ok (Pow 0)) |> Result.bind (fun si ->
                match ss, si with
                | Pow 0, Pow 0 -> Ok (Pow 0)
                | POpaque, _ | _, POpaque -> Ok POpaque
                | (PowUnsized, _ | _, PowUnsized) ->
                    Error (bl4012 e.Span $"function '{ctx.FuncName}': reduce over a value of unestablished extent cannot be called invariant -- if the source lands in the node-power space its cells move with the labelling, and the combiner is not checked for commutativity. Annotate the source's extent, or use ml.derive_perm_linear(K, 0, {ctx.N}, x, w), the complete invariant readout")
                | _ ->
                    Error (bl4012 e.Span $"function '{ctx.FuncName}': reduce over a node-covariant value is invariant only for a commutative combiner, which is not checked -- the certified invariant readout is ml.derive_perm_linear(K, 0, {ctx.N}, x, w), whose basis is COMPLETE (every S_n-invariant linear form on the node power is one weight setting)")))
    | _ -> Ok POpaque

and private judgeStmts (ctx: Ctx) (env: Map<string, PowStatus>) (stmts: Stmt list)
    : Result<Map<string, PowStatus>, Blade.Diagnostics.Diagnostic> =
    stmts
    |> List.fold (fun acc s ->
        acc |> Result.bind (fun env ->
            match unwrapStmt s with
            | StmtLet binding ->
                judge ctx env binding.Value |> Result.bind (fun sv ->
                    match binding.Pattern.Kind, sv with
                    | PatternKind.PatVar n, _ -> Ok (Map.add n sv env)
                    | _, (Pow 0 | PowUnsized) -> Ok (bindPatternVars PowUnsized env binding.Pattern)
                    | _, _ ->
                        Error (bl4012 binding.Value.Span $"function '{ctx.FuncName}': cannot destructure a node-covariant value -- bind it whole"))
            | StmtExpr e2 -> judge ctx env e2 |> Result.map (fun _ -> env)
            | StmtAssign (l, _, r) -> judgeAssign ctx env l.Span l r |> Result.map (fun () -> env)
            | StmtForIn (v, range, body) ->
                judge ctx env range |> Result.bind (fun sr ->
                    match sr with
                    | Pow k when k > 0 ->
                        Error (bl4012 range.Span $"function '{ctx.FuncName}': cannot iterate a node-covariant value as a range")
                    | _ ->
                        judgeStmts ctx (Map.add v PowUnsized env) body |> Result.map (fun _ -> env))
            | _ -> Ok env))
        (Ok env)

/// Assignments: whole-variable writes must preserve the status; element writes
/// into a node power are the write-side twin of the component READ v1 defers.
and private judgeAssign (ctx: Ctx) (env: Map<string, PowStatus>) (span: Span) (l: Expr) (r: Expr)
    : Result<unit, Blade.Diagnostics.Diagnostic> =
    let fail msg = Error (bl4012 span $"function '{ctx.FuncName}': {msg}")
    judge ctx env r |> Result.bind (fun sr ->
        match l.Kind with
        | ExprKind.ExprVar n ->
            match Map.tryFind n env with
            | Some st when st = sr -> Ok ()
            | Some st -> fail $"assignment changes '{n}' from {statusStr st} to {statusStr sr} -- a mut binding must keep one status"
            | None -> Ok ()
        | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar n }, idxArgs) ->
            // element write: INDICES are judged first (catalog finding 2 --
            // this copy used to walk past them, so `a(x(0)) = v` went
            // unchecked on WRITE while `let z = x(0)` was rejected on READ).
            idxArgs
            |> List.fold (fun acc a ->
                acc |> Result.bind (fun () ->
                    judge ctx env a |> Result.bind (fun si ->
                        match si with
                        | Pow 0 -> Ok ()
                        | PowUnsized ->
                            Error (bl4012 a.Span
                                       ($"function '{ctx.FuncName}': an array index must be invariant inside a perm-certified body, and this one is invariant-shaped but of unestablished extent -- the judgment cannot rule out that the cell it selects moves with the node labelling"))
                        | Pow _ ->
                            Error (bl4012 a.Span
                                       ($"function '{ctx.FuncName}': an array index must be invariant inside a perm-certified body, but this one is {(statusStr si)} -- the cell it selects moves with the node labelling"))
                        | POpaque ->
                            Error (bl4012 a.Span
                                       ($"function '{ctx.FuncName}': an array index must be invariant inside a perm-certified body, and this one is unclassifiable -- the judgment cannot rule out that the cell it selects moves with the node labelling. Index with a static offset or a value the judgment can see")))))
                (Ok ())
            |> Result.bind (fun () ->
                match Map.tryFind n env with
                | Some (Pow k) when k > 0 ->
                    fail $"element-assignment into node-covariant '{n}' writes ONE cell of a node power, which cannot be told from an equivariant reassembly without per-axis tracking. Build node powers with ml.derive_perm_linear / ml.derive_perm_bias / ml.perm_matmul, or with whole-array elementwise arithmetic"
                | _ ->
                    match sr with
                    | Pow k when k > 0 -> fail "cannot store a node-covariant value into an array element"
                    | _ -> Ok ())
        | _ ->
            match sr with
            | Pow k when k > 0 -> fail "unsupported assignment target for a node-covariant value"
            | _ -> Ok ())

and private judgeApp (ctx: Ctx) (env: Map<string, PowStatus>) (e: Expr) (f: Expr) (args: Expr list)
    : Result<PowStatus, Blade.Diagnostics.Diagnostic> =
    let reject msg = Error (bl4012 e.Span $"function '{ctx.FuncName}': {msg}")
    let judgeAll args = judgeEach (judge ctx env) args
    let requirePow (what: string) (k: int) (argE: Expr) =
        judge ctx env argE |> Result.bind (fun s ->
            if s = Pow k then Ok ()
            elif s = PowUnsized && k = 0 then
                Error (bl4012 argE.Span
                           ($"function '{ctx.FuncName}': {what} must be invariant, and this argument is invariant-SHAPED but of unestablished extent -- the op's theorem holds only if those cells do not move when the nodes are relabelled, and a buffer that lands in the node-power space does move. Annotate the extent, or build the equivariant constants with ml.derive_perm_bias"))
            else
                Error (bl4012 argE.Span
                           ($"function '{ctx.FuncName}': {what} must be {(statusStr (Pow k))}, but the argument is {(statusStr s)}")))
    let staticInt (what: string) (argE: Expr) : Result<int, Blade.Diagnostics.Diagnostic> =
        staticArgValue ctx.Statics argE
        |> Result.bind (fun sv -> match sv with SVInt n -> Ok (int n) | _ -> Error "must be a static int")
        |> Result.mapError (fun m -> bl4012 argE.Span $"function '{ctx.FuncName}': {what}: {m}")
    /// The op's N must be THE certificate's N: one function body, one node axis.
    let requireN (op: string) (n': int) (argE: Expr) =
        if n' = ctx.N then Ok ()
        else
            Error (bl4012 argE.Span
                       ($"function '{ctx.FuncName}': ml.{op} is called with N = {n'}, but this function is certified `ml.perm_equiv({ctx.N})`. A certificate names ONE node axis -- an S_{n'}-equivariant op proves nothing about S_{ctx.N} relabellings. Match the extents, or split the body into two certified functions"))
    match f.Kind with
    // ml ops (surface-visible pre-rewrite)
    | ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, op) when Set.contains alias ctx.MlAliases ->
        (match op, args with
         | "derive_perm_linear", [ kE; lE; nE; xE; wE ] ->
             staticInt "derive_perm_linear K" kE |> Result.bind (fun k ->
             staticInt "derive_perm_linear L" lE |> Result.bind (fun l ->
             staticInt "derive_perm_linear N" nE |> Result.bind (fun n' ->
                 requireN "derive_perm_linear" n' nE |> Result.bind (fun () ->
                 requirePow "derive_perm_linear input" k xE |> Result.bind (fun () ->
                 requirePow "derive_perm_linear weight buffer" 0 wE |> Result.map (fun () ->
                     // The complete Hom_{Sn}(R^{N^K}, R^{N^L}) basis.
                     Pow l))))))
         | "derive_perm_bias", [ lE; nE; bE ] ->
             staticInt "derive_perm_bias L" lE |> Result.bind (fun l ->
             staticInt "derive_perm_bias N" nE |> Result.bind (fun n' ->
                 requireN "derive_perm_bias" n' nE |> Result.bind (fun () ->
                 requirePow "derive_perm_bias coefficient buffer" 0 bE |> Result.map (fun () ->
                     // The rep-INTRODUCTION form: S_n-invariant constants in R^{N^L}.
                     Pow l))))
         | "perm_matmul", [ nE; aE; bE ] ->
             staticInt "perm_matmul N" nE |> Result.bind (fun n' ->
                 requireN "perm_matmul" n' nE |> Result.bind (fun () ->
                 requirePow "perm_matmul left factor" 2 aE |> Result.bind (fun () ->
                 requirePow "perm_matmul right factor" 2 bE |> Result.map (fun () ->
                     // (P A P^T)(P B P^T) = P (A B) P^T -- shipped BY NAME.
                     Pow 2))))
         | ("derive_perm_linear" | "derive_perm_bias" | "perm_matmul"), _ ->
             reject $"{op}: unrecognized call shape inside a perm-certified body"
         | _ ->
             // every other ml.* op: its arguments live in irreps or sizing
             // space, neither carrying a node axis, so invariants in/out.
             judgeAll args |> Result.bind (fun sts ->
                 if sts |> List.forall ((=) (Pow 0)) then Ok (Pow 0)
                 elif sts |> List.forall (fun st -> st = Pow 0 || st = PowUnsized) then Ok PowUnsized
                 else reject $"ml.{op} carries no S_n rule for node-covariant arguments -- the node-axis ops are ml.derive_perm_linear, ml.derive_perm_bias and ml.perm_matmul"))
    // named callees
    | ExprKind.ExprVar fn ->
        match Map.tryFind fn ctx.Certs with
        | Some cert ->
            if cert.N <> ctx.N then
                reject $"call to '{fn}': it is certified for N = {cert.N}, this function for N = {ctx.N} -- certificates do not transfer between node-axis extents"
            elif List.length args <> List.length cert.Params then
                reject $"call to '{fn}': expected {List.length cert.Params} arguments"
            else
                (List.zip cert.Params args)
                |> List.fold (fun acc ((pName, pSt), argE) ->
                    acc |> Result.bind (fun () ->
                        match pSt with
                        | POpaque -> reject $"call to '{fn}': parameter '{pName}' is unclassifiable"
                        // Unreachable from a signature (statusOfType answers Pow
                        // or Error); stated so a future classifier can't slip past.
                        | PowUnsized -> reject $"call to '{fn}': parameter '{pName}' has no established extent"
                        | Pow k -> requirePow $"'{fn}' parameter '{pName}'" k argE))
                    (Ok ())
                |> Result.map (fun () -> cert.Return)
        | None ->
            match Map.tryFind fn env with
            | Some (Pow k) when k > 0 ->
                // A read out of a node power, LEGAL for a bound loop-variable
                // index; v1 has no such tracking, so it refuses uniformly.
                reject $"component access into node-covariant '{fn}' inside a perm-certified body requires per-axis tracking, which is not implemented: a bound-index read (which reassembles equivariantly) cannot be told from a fixed-offset one (which does not). Whole-array elementwise operators are pointwise, hence equivariant, and ml.derive_perm_linear(K, 0, {ctx.N}, x, w) is the complete invariant readout"
            | _ ->
                judgeAll args |> Result.bind (fun sts ->
                    match sts |> List.tryFindIndex (fun s -> s <> Pow 0 && s <> PowUnsized) with
                    | None ->
                        // Fixedness is claimable only with an extent behind it:
                        // an indexed read of a bound array is one cell (a
                        // scalar); a helper call uses its declared return type.
                        let unsizedArg = sts |> List.exists ((=) PowUnsized)
                        (match Map.tryFind fn env with
                         // A cell of a buffer not itself fixed isn't either.
                         | Some (Pow 0) -> Ok (Pow 0)
                         | Some _ -> Ok PowUnsized
                         | None ->
                             // Not a local: a module array (Globals) or a helper.
                             match Map.tryFind fn ctx.Globals with
                             | Some (Pow 0) -> Ok (Pow 0)
                             | Some _ -> Ok PowUnsized
                             | None ->
                                 match Map.tryFind fn ctx.Returns with
                                 | Some st when not unsizedArg -> Ok st
                                 | Some _ -> Ok PowUnsized
                                 | None -> Ok PowUnsized)
                    | Some i ->
                        // THE POLARITY ARM: a pointwise builtin on a node power
                        // is EQUIVARIANT -- opposite of MLEquiv's verdict.
                        if isPointwiseBuiltin fn then
                            combinePointwise sts
                            |> Result.mapError (fun m -> bl4012 e.Span $"function '{ctx.FuncName}': {m}")
                        else
                            Error (bl4012 args.[i].Span
                                       ($"function '{ctx.FuncName}': a node-covariant value escapes to '{fn}', which carries no perm certificate -- certify it with `where ml.perm_equiv({ctx.N})` or pass only invariants")))
    | _ ->
        judgeAll args |> Result.bind (fun sts ->
            judge ctx env f |> Result.bind (fun sf ->
                let inv st = st = Pow 0 || st = PowUnsized
                if inv sf && sts |> List.forall inv then Ok PowUnsized
                else reject "cannot classify this call inside a perm-certified body"))

/// Judge one certified function. Empty list = certificate holds.
let judgeFunction (certs: Map<string, PermSig>) (statics: StaticEnv) (mlAliases: Set<string>)
                  (decls: Located<Decl> list) (fd: FunctionDecl)
    : Blade.Diagnostics.Diagnostic list =
    match Map.tryFind fd.Name certs with
    | None -> []
    | Some cert ->
        let ctx = { FuncName = fd.Name; N = cert.N; MlAliases = mlAliases; Statics = statics; Certs = certs
                    Globals = buildGlobals cert.N statics decls
                    Returns = buildReturnEvidence cert.N statics decls }
        let env = cert.Params |> List.fold (fun m (n, st) -> Map.add n st m) Map.empty
        match judge ctx env fd.Body with
        | Error d -> [ d ]
        | Ok st ->
            if st = cert.Return then []
            else
                [ bl4012 fd.Body.Span
                      $"function '{fd.Name}': the body is {statusStr st} but the declared return type says {statusStr cert.Return} -- the certificate requires them to agree" ]

// Constraint-registry handler

/// `perm_equiv(N)` is a callee-side theorem: Validate re-checks the conjunct
/// shape (the elaborator has already judged the body, and resolves `let
/// static` N against the static environment); call sites carry no obligation.
let private permHandler : Blade.Constraints.ConstraintHandler = {
    Describe = "perm_equiv(N) -- certifies the function equivariant under the symmetric group S_N relabelling a node axis of extent N; the ML elaborator proves the body composes only relabelling-equivariant operations"
    Validate = fun funcName _ args ->
        match args with
        | [ a ] ->
            match System.Int32.TryParse a with
            | true, n when n < 2 ->
                Error $"function '{funcName}': perm_equiv({n}) -- N must be >= 2 (the node-axis extent; S_1 is trivial and the flat extent keying needs strictly increasing powers)"
            | _ -> Ok () // an int >= 2, or a `let static` name the elaborator resolves
        | _ ->
            Error $"function '{funcName}': perm_equiv expects exactly one argument -- the node-axis extent N, as in `where ml.perm_equiv(4)`"
    EnterBody = fun _ _ -> ()
    ExitBody = fun _ _ -> ()
    Discharge = fun _ _ _ -> Ok ()
}

let mutable private registered = false

let register () =
    if not registered then
        registered <- true
        Blade.Constraints.registerConstraint "__ml_perm_equiv" permHandler
