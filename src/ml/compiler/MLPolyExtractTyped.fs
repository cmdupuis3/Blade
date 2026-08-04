/// THE TYPED POLYNOMIAL EXTRACTOR, stage C2: a second front half for the
/// polynomial engine, producing the same normal form from a POST-ELABORATION
/// `TypedExpr` under `Blade.DeduceRep`'s vocabulary instead of from a surface
/// `FunctionDecl` under MLEquiv's. Not one line of either discharger is
/// reimplemented: `Blade.ML.PolyExtract.discharge` (finite word-set, over
/// Q[atoms]) and `Blade.ML.LieDischarge.discharge` (radical-vector so(3)
/// plus the pi0 -I identity) both consume a `PolyExtract.PolyForm` and a
/// list of actions built from a spec -- neither knows what an `Expr` or a
/// `TypedExpr` is.
///
/// WHY A SECOND EXTRACTOR AND NOT A SHARED ONE. The two IRs disagree about
/// what arithmetic IS. At the seam, `x * s + j` on arrays is an `ExprBinOp`
/// tree and the surface extractor's whole-array arms fire directly. By
/// typecheck the SAME source has been desugared into nested `TExprApply`
/// former applications over generated `method_for` loops with generated
/// kernel lambdas, and the `ExprBinOp` arm never sees it. The component-read
/// fragment -- the one the TYPED-EXEMPT corpus files actually need --
/// survives almost verbatim: `[0.0 - x(1), x(0)]` is still a `TExprArrayLit`
/// of `TExprBinOp` over `TExprIndex`. So the port keeps the normal form and
/// the value algebra, rewrites the walk, and adds ONE new arm (`TExprApply`)
/// for the shapes the desugaring creates.
///
/// SOUNDNESS POSTURE: `None` IS ALWAYS SAFE. A refusal costs recall; a WRONG
/// extraction is the only unsound outcome, since the discharge would then
/// certify a polynomial that is not the body. Every arm below refuses by
/// default, the entry points are total, and any escape reads as "the engine
/// has nothing to say" -- mirroring the seam's own discipline (MLEquiv's
/// `try ... with _ -> [ d ]`): a speculative second opinion may never turn a
/// compiling program into a crash.
///
/// THE FRAGMENT (the v1 surface fragment, plus the desugaring arm):
///   1. numeric literals, dyadic-exact (`PolyExtract.Rat.tryOfFloatExact`);
///   2. `TExprIndex` / `TExprApp` at a LITERAL offset into a rep-bound vector
///      or an invariant array;
///   3. scalar + - * and / by a nonzero constant, via the SHARED value algebra;
///   4. whole-array + - between equal-length vectors and invariant*array;
///   5. `TExprLet` and `TExprBlock` statement lets (no destructuring);
///   6. `TExprArrayLit` of scalar polynomials as the assembled return;
///   7. `TExprArrayNegate`, `TExprUnaryOp OpNeg`, `TExprCompute` (peeled);
///   8. `TExprApply` of a LAMBDA kernel over sources that all extract to
///      vectors of the same length -- the desugared whole-array arithmetic --
///      under the two guards in `extractApply`.
///
/// `TExprUnaryOp (OpMath _, _)` is REFUSED by name: post elaboration
/// `exp(v)` is a unary op node, not a call, so passing it through would put
/// a transcendental inside a polynomial normal form -- the same trap
/// DeduceRep documents at its own unary arm.
module Blade.ML.PolyExtractTyped

open Blade.Ast
open Blade.IR
open Blade.Types
open Blade.TypedAst
open Blade.DeduceRep

module PX = Blade.ML.PolyExtract
module LD = Blade.ML.LieDischarge
module MLS = Blade.ML.Spec
module PS = Blade.ML.PointSpec
module EM = Blade.ML.EquivMessages

// THE ABSTRACT VALUE AND THE BINARY ALGEBRA ARE MLPolyExtract'S, NOT A TWIN.
// `PX.Val`, `PX.Budget`, `PX.charge`, `PX.chargeVec` and `PX.binOp` are the
// part of the extractor that does not walk syntax, and this module CALLS
// them rather than restating them: the two walkers' value types are
// identical cell for cell, and the only thing that genuinely differs is the
// surrounding CONTEXT -- the surface walker folds `let static` reads via a
// `StaticEnv`, this one has no statics left by typecheck -- so the shared
// surface is keyed on a bare budget cell. A divergence between two copies
// would be a SOUNDNESS bug, not a recall difference (a wrong extraction
// makes the discharge certify a polynomial that is not the body).

/// The budget cell, under this module's old name so the walk below reads
/// unchanged. One per extraction, so a body cannot dodge the term cap by
/// spreading a blow-up over many components.
type private Ctx = PX.Budget

let private outside (msg: string) (span: Span) : Result<'a, PX.ExtractError> =
    Error (PX.OutsideFragment (msg, span))

let private constPoly (ctx: Ctx) (c: PX.Rat) =
    PX.charge ctx (PX.Poly.ofRat c) |> Result.map PX.VScalar

/// A provably compile-time integer offset. `TExprCompute` is a scheduling
/// boundary and is peeled; anything else (a variable, an arithmetic
/// expression, a fold this walker cannot see) is not a literal and the
/// caller declines -- by typecheck the `let static` folds have already
/// happened, so a surviving non-literal offset is genuinely dynamic.
let rec private staticOffset (e: TypedExpr) : int option =
    match e.Kind with
    | TExprLit (LitInt n) -> Some (int n)
    | TExprCompute inner -> staticOffset inner
    | _ -> None

/// The total number of scalar cells a type denotes, when every extent is a
/// literal. Used ONLY as a guard (see `extractApply`): `None` declines.
let private totalCells (resolve: IRType -> IRType) (ty: IRType) : int option =
    match resolve ty with
    | ArrayElem arr ->
        arr.IndexTypes
        |> List.fold (fun acc (ix: IRIndexType) ->
            acc |> Option.bind (fun n ->
                match ix.Extent with
                | IRLit (IRLitInt k) when k >= 0L -> Some (n * int k)
                | _ -> None)) (Some 1)
    | _ -> None

let rec private extractVal (ctx: Ctx) (resolve: IRType -> IRType) (env: Map<IRId, PX.Val>) (e: TypedExpr)
    : Result<PX.Val, PX.ExtractError> =
    let go = extractVal ctx resolve env
    match e.Kind with
    | TExprLit (LitInt n) -> constPoly ctx (PX.Rat.ofBigInt (bigint n))
    | TExprLit (LitFloat f) ->
        (match PX.Rat.tryOfFloatExact f with
         | Some r -> constPoly ctx r
         | None -> outside "a non-finite float literal is not a polynomial coefficient" e.Span)
    | TExprLit _ -> outside "only numeric literals are polynomial coefficients" e.Span

    // Only a bound name (parameter, let, kernel parameter) has a polynomial.
    // A free name -- a module global, a builtin, a function -- is refused:
    // guessing a value would be the unsound direction.
    | TExprVar (n, vid, _) ->
        (match Map.tryFind vid env with
         | Some v -> Ok v
         | None -> outside (sprintf "'%s' is not a parameter or a local binding of this body" n) e.Span)

    | TExprBinOp (Elementwise, op, l, r) ->
        go l |> Result.bind (fun vl -> go r |> Result.bind (fun vr -> PX.binOp ctx e.Span op vl vr))
    | TExprBinOp _ ->
        outside "outer-product operators are outside the polynomial fragment" e.Span

    // Negation is the linear map -I on scalars and on vectors alike.
    | TExprUnaryOp (OpNeg, inner) ->
        go inner |> Result.bind (fun v ->
            match v with
            | PX.VScalar p -> Ok (PX.VScalar (PX.Poly.neg p))
            | PX.VVec ps -> Ok (PX.VVec (ps |> Array.map PX.Poly.neg))
            | PX.VInvArr _ -> outside "an invariant array has no polynomial form -- read its cells at static indices" e.Span
            | PX.VOpaque n -> outside (sprintf "the shape of invariant '%s' is not decidable from its type" n) e.Span)
    // `OpMath` lands here: post-typecheck it is a UNARY OP rather than the
    // named call MLEquiv sees, so a transcendental must be refused explicitly
    // or it would ride through the `OpNeg` arm's shape.
    | TExprUnaryOp _ -> outside "this unary operator is outside the polynomial fragment" e.Span

    | TExprArrayNegate a ->
        go a |> Result.bind (fun v ->
            match v with
            | PX.VVec ps -> Ok (PX.VVec (ps |> Array.map PX.Poly.neg))
            | PX.VScalar p -> Ok (PX.VScalar (PX.Poly.neg p))
            | _ -> outside "whole-array negation needs an extracted vector" e.Span)

    // The arm composition refuses (see MLPolyExtract's header): an array
    // literal of scalar polynomials is the assembled rep-valued return.
    | TExprArrayLit (es, _) ->
        es
        |> List.fold (fun acc x ->
            acc |> Result.bind (fun ps ->
                go x |> Result.bind (fun v ->
                    match v with
                    | PX.VScalar p -> Ok (ps @ [ p ])
                    | _ -> outside "an array literal must be built from SCALAR polynomials -- nested aggregates are outside the fragment" x.Span)))
            (Ok [])
        |> Result.map (List.toArray >> PX.VVec)

    | TExprIndex (arr, idxs, _) -> extractIndex ctx resolve env e arr idxs
    // Application syntax over a bound array IS an index; a genuine call is a
    // v1 deferral, exactly as at the seam.
    | TExprApp (f, args) ->
        (match f.Kind with
         | TExprVar (_, vid, _) when (Map.containsKey vid env) -> extractIndex ctx resolve env e f args
         | _ -> outside "calls are outside the polynomial fragment" e.Span)

    | TExprLet (_, vid, value, body) ->
        go value |> Result.bind (fun v -> extractVal ctx resolve (Map.add vid v env) body)

    | TExprBlock (stmts, Some final) ->
        stmts
        |> List.fold (fun acc s ->
            acc |> Result.bind (fun env' ->
                match s with
                | TStmtLet b when b.SubBindings.IsEmpty && b.PostChecks.IsEmpty ->
                    extractVal ctx resolve env' b.Value |> Result.map (fun v -> Map.add b.VarId v env')
                | TStmtLet _ -> outside "destructuring bindings are outside the polynomial fragment" e.Span
                | _ -> outside "only `let` statements are admitted in a polynomial body" e.Span))
            (Ok env)
        |> Result.bind (fun env' -> extractVal ctx resolve env' final)
    | TExprBlock (_, None) -> outside "a polynomial body must end in an expression" e.Span

    // `compute` is a scheduling boundary, not a value transform.
    | TExprCompute inner -> go inner

    | TExprApply info -> extractApply ctx resolve env e info

    | _ -> outside "this expression is outside the polynomial fragment" e.Span

/// A static read of a bound vector or of an invariant array's cell. Shared by
/// the `TExprIndex` and application-syntax arms, which are the same operation
/// spelled two ways by the checker.
and private extractIndex (ctx: Ctx) (resolve: IRType -> IRType) (env: Map<IRId, PX.Val>)
                         (e: TypedExpr) (arr: TypedExpr) (idxs: TypedExpr list)
    : Result<PX.Val, PX.ExtractError> =
    match idxs with
    | [ idxE ] ->
        extractVal ctx resolve env arr |> Result.bind (fun v ->
            match v with
            | PX.VVec ps ->
                (match staticOffset idxE with
                 | Some i when i >= 0 && i < ps.Length -> Ok (PX.VScalar ps.[i])
                 | Some i -> outside (sprintf "index %d is outside a %d-component value" i ps.Length) e.Span
                 | None -> outside "indexing a representation-typed value needs a static offset" e.Span)
            | PX.VInvArr name ->
                (match staticOffset idxE with
                 | Some i -> PX.charge ctx (PX.Poly.ofMono (PX.Mono.invAtom { Name = name; Index = Some i })) |> Result.map PX.VScalar
                 | None -> outside (sprintf "indexing the invariant '%s' needs a static offset" name) e.Span)
            | PX.VScalar _ -> outside "a scalar cannot be indexed" e.Span
            | PX.VOpaque name ->
                outside (sprintf "the shape of invariant '%s' is not decidable from its type" name) e.Span)
    | _ -> outside "only single-offset reads are admitted in the polynomial fragment" e.Span

/// THE POST-ELABORATION ARM. Whole-array arithmetic that the seam meets as
/// `ExprBinOp` arrives here as a former application over a generated kernel
/// lambda (`x * s` desugars to `compute(apply(method_for(x), lambda(__bx) ->
/// __bx * s))`). The rule: bind kernel parameter k to component i of source
/// k and re-extract the kernel body once per component, valid only under two
/// refusal-direction guards.
///
///  1. ITERATION SHAPE. A former may CROSS-ITERATE, where output cell (i, j)
///     reads sources at different positions, making componentwise reading
///     false. Admitted only when the checker marked it a co-iteration (zip)
///     AND the output type's total literal cell count equals the common
///     source length -- an outer product fails the second test past extent
///     1. Kernel arity is checked the same way off the apply's metadata: an
///     `object_for` kernel takes whole sub-arrays and a compose-apply puts
///     arrays in the kernel slot entirely, so neither reads "parameter k is
///     component i of source k" correctly; desugared operator arithmetic
///     always has ranks (0, ..., 0) -> 0 and `IsComposeApply` false.
///  2. UNIFORMITY. Every kernel parameter is bound to a SCALAR polynomial
///     for the position being built, so a kernel reading a position-
///     dependent value it was not handed (a captured array at a computed
///     offset, the loop index, another former) leaves the fragment rather
///     than being silently treated as uniform. A captured SCALAR is bound
///     in `env` and correctly reads as the same atom at every position.
///
/// An extracted body still has to pass the coefficientwise identity, a
/// strictly stronger obligation than any composition rule.
and private extractApply (ctx: Ctx) (resolve: IRType -> IRType) (env: Map<IRId, PX.Val>)
                         (e: TypedExpr) (info: TypedApplyInfo)
    : Result<PX.Val, PX.ExtractError> =
    if info.IsComposeApply
       || info.KernelOutputRank <> 0
       || (info.KernelInputRanks |> List.exists ((<>) 0)) then
        outside "only a rank-0 (elementwise) kernel reads its sources componentwise" e.Span
    else
    match info.Kernel.Kind with
    | TExprLambda lam when List.length lam.Params = List.length info.Arrays && not lam.Params.IsEmpty ->
        let srcs =
            info.Arrays
            |> List.fold (fun acc a ->
                acc |> Result.bind (fun out ->
                    extractVal ctx resolve env a |> Result.bind (fun v ->
                        match v with
                        | PX.VVec ps -> Ok (out @ [ ps ])
                        | _ -> outside "a former source outside the vector fragment is not extractable" a.Span)))
                (Ok [])
        srcs |> Result.bind (fun srcs ->
            let n = (List.head srcs).Length
            if srcs |> List.exists (fun (s: PX.Poly []) -> s.Length <> n) then
                outside "co-iterated sources of different lengths are outside the fragment" e.Span
            elif List.length srcs > 1 && not info.IsCoIteration then
                outside "a cross-iterating former does not read its sources componentwise" e.Span
            elif totalCells resolve e.Type <> Some n then
                outside "the former's output shape is not the componentwise image of its sources" e.Span
            else
                Seq.init n id
                |> Seq.fold (fun acc i ->
                    acc |> Result.bind (fun out ->
                        let kEnv =
                            List.zip lam.Params srcs
                            |> List.fold (fun m ((p: TypedParam), (s: PX.Poly [])) ->
                                Map.add p.VarId (PX.VScalar s.[i]) m) env
                        extractVal ctx resolve kEnv lam.Body |> Result.bind (fun v ->
                            match v with
                            | PX.VScalar p -> Ok (out @ [ p ])
                            | _ -> outside "a kernel must produce a SCALAR polynomial per position" lam.Body.Span)))
                    (Ok [])
                |> Result.map (List.toArray >> PX.VVec))
    | _ -> outside "only a lambda kernel is admitted in the polynomial fragment" e.Span

// The signature bridge: DeduceRep's vocabulary -> the extractor's.

/// The R-dimension of a rep payload. The O(3) arm rebuilds an `MLS.Spec` from
/// DeduceRep's `(l, parity, mult)` triples (the same payload
/// `Types.mkIrrepsTag` serializes, in the same order) and asks MLSpec for the
/// dimension rather than restating `mult * (2l + 1)`.
let private specOfTriples (triples: (int * int * int) list) : MLS.Spec =
    triples |> List.map (fun (l, p, m) -> { MLS.L = l; MLS.Parity = p; MLS.Mult = m })

let private repDim (r: RepSpecT) : int option =
    match r with
    | TO3Spec triples -> Some (MLS.totalDim (specOfTriples triples))
    | TPgSpecT (g, entries) ->
        try Some (PS.pgTotalDim (PS.pointGroup g) entries) with _ -> None

/// The classified signature the extractor needs, from a DeduceRep signature.
/// The invariant SHAPE mapping is where the typed side beats the seam:
/// MLEquiv's `invKind` guesses shape from the spelling of a surface
/// annotation, while `TInvScalar` here is a PROVEN 0-dimensionality read off
/// `IRTScalar` (`TInvShapeUnknown` still becomes `PInvOpaque`, so the
/// improvement is recall-only).
let private polySigOf (sg: RepSigT) : PX.PolySig option =
    let ps =
        sg.Params
        |> List.fold (fun acc (n, st) ->
            acc |> Option.bind (fun out ->
                match st with
                | TRep r -> repDim r |> Option.map (fun d -> out @ [ (n, PX.PRep d) ])
                | TInv TInvScalar -> Some (out @ [ (n, PX.PInvScalar) ])
                | TInv (TInvAgg _) -> Some (out @ [ (n, PX.PInvArray) ])
                | TInv TInvShapeUnknown -> Some (out @ [ (n, PX.PInvOpaque) ])
                | TOpaque | TBottom -> None)) (Some [])
    ps |> Option.bind (fun ps ->
        match sg.Return with
        | TRep r -> repDim r |> Option.map (fun d -> PX.mkSig ps (Some d))
        | TInv _ -> Some (PX.mkSig ps None)
        | TOpaque | TBottom -> None)

/// The word set as `ElementAction`s: ALL |G| elements, read off a `RepSigT`
/// instead of a `CertSig`. A scalar (invariant) return is an INVARIANCE
/// claim, so the output action is the 1x1 identity -- the trivial rep.
let private pointActions (gn: string) (sg: RepSigT) : PX.ElementAction list option =
    let grp = PS.pointGroup gn
    let pgOf (r: RepSpecT) = match r with TPgSpecT (_, s) -> Some s | TO3Spec _ -> None
    let repParams = sg.Params |> List.choose (fun (n, st) -> match st with TRep r -> Some (n, pgOf r) | _ -> None)
    if repParams |> List.exists (snd >> Option.isNone) then None
    else
        let outSpec =
            match sg.Return with
            | TRep r -> pgOf r
            | TInv _ -> Some []
            | _ -> None
        match outSpec with
        | None -> None
        | Some outS ->
            PS.groupElements grp
            |> List.map (fun el ->
                let inMats =
                    repParams |> List.map (fun (n, s) -> (n, PS.pgElementMatrix grp (Option.get s) el)) |> Map.ofList
                let outMat =
                    match sg.Return with
                    | TInv _ -> [| [| 1 |] |]
                    | _ -> PS.pgElementMatrix grp outS el
                PX.mkAction (PS.wordName grp el.Word) inMats outMat)
            |> Some

/// The so(3) generators (and, under O3 only, the -I parity bookkeeping),
/// read off a `RepSigT`. A scalar return is the trivial rep -- a 1x1 ZERO
/// generator and parity EVEN -- which is why `-> Float` under equiv(O3)
/// rejects a pseudoscalar body that certifies under equiv(SO3).
let private o3Actions (g: GroupT) (sg: RepSigT)
    : (LD.LieGenerator list * LD.InversionCheck option) option =
    let specOf (r: RepSpecT) = match r with TO3Spec s -> Some (specOfTriples s) | TPgSpecT _ -> None
    let repParams = sg.Params |> List.choose (fun (n, st) -> match st with TRep r -> Some (n, specOf r) | _ -> None)
    if repParams |> List.exists (snd >> Option.isNone) then None
    else
        let reps = repParams |> List.map (fun (n, s) -> (n, Option.get s))
        let outSpec =
            match sg.Return with
            | TRep r -> specOf r |> Option.map Some
            | TInv _ -> Some None
            | _ -> None
        match outSpec with
        | None -> None
        | Some outS ->
            let gens =
                LD.axes
                |> List.map (fun ax ->
                    { LD.Name = LD.axisName ax
                      LD.InMats = reps |> List.map (fun (n, s) -> (n, LD.specGenerator ax s)) |> Map.ofList
                      LD.OutMat =
                        match outS with
                        | Some s -> LD.specGenerator ax s
                        | None -> [| [| LD.Radical.zero |] |] })
            let inv =
                match g with
                | GO3 ->
                    Some { LD.InPar = reps |> List.map (fun (n, s) -> (n, LD.specParity s)) |> Map.ofList
                           LD.OutPar = match outS with Some s -> LD.specParity s | None -> [| 0 |] }
                | _ -> None
            Some (gens, inv)

/// What the engine has to say about one typed body under one candidate
/// signature. `None` means "nothing" -- no applicable action set, an
/// unclassifiable signature, an extraction refusal or a cap breach -- and is
/// the overwhelmingly common answer.
type TypedEngineVerdict =
    /// Satisfies the equivariance identity at every enumerated group element
    /// (point groups) or at every so(3) generator plus -I (O3/SO3). HOLDS.
    | EngineHolds
    /// A polynomial that is NOT equivariant, with the offending
    /// element/generator, output component and monomial named. A DEDUCTION
    /// consumer treats this like `None`; a CHECKING consumer (stage C1)
    /// wants to say so, hence the distinct case.
    | EngineRefutes of string

// Rendered by `Blade.ML.EquivMessages`, the SAME four constructors the seam
// calls, not a shorter twin -- two copies of user-facing text drift as
// readily as two copies of different lengths, so the wording lives in one
// module both front halves consume. The three failure records reach here in
// the shape those constructors take (`PX.DischargeFailure`, `LD.LieFailure`,
// `LD.InversionFailure`), so the sharing is a call, not an adapter.

/// Extract a typed body to the shared normal form under a DeduceRep
/// signature. Public so a future consumer can see WHY extraction refused;
/// the deduction hook only needs `engineVerdict`. `parms` supplies the
/// binder ids the walker keys its environment by -- the same `RepParam`
/// list `DeduceRep.deduceFunctionRep` holds -- and MUST match `sg.Params`'s order.
let extractTyped (resolve: IRType -> IRType) (parms: RepParam list) (sg: RepSigT) (body: TypedExpr)
    : Result<PX.PolyForm, PX.ExtractError> =
    match polySigOf sg with
    | None -> Error (PX.OutsideFragment ("the signature does not classify for the polynomial engine", body.Span))
    | Some psig when List.length psig.Params <> List.length parms ->
        Error (PX.OutsideFragment ("internal: parameter list and classified signature disagree in length", body.Span))
    | Some psig ->
        let ctx = PX.mkBudget ()
        let env =
            List.zip parms psig.Params
            |> List.fold (fun acc ((p: RepParam), (name, kind)) ->
                match kind with
                | PX.PRep n -> Map.add p.PId (PX.VVec (Array.init n (fun i -> PX.Poly.ofMono (PX.Mono.repVar name i)))) acc
                | PX.PInvArray -> Map.add p.PId (PX.VInvArr name) acc
                | PX.PInvScalar -> Map.add p.PId (PX.VScalar (PX.Poly.ofMono (PX.Mono.invAtom { Name = name; Index = None }))) acc
                | PX.PInvOpaque -> Map.add p.PId (PX.VOpaque name) acc)
                Map.empty
        extractVal ctx resolve env body
        |> Result.bind (fun v ->
            match v, psig.ReturnDim with
            | PX.VScalar p, None -> Ok ({ PX.Components = [| p |] } : PX.PolyForm)
            | PX.VVec ps, Some n when ps.Length = n -> Ok ({ PX.Components = ps } : PX.PolyForm)
            | PX.VVec ps, Some n ->
                outside (sprintf "the body assembles %d components but the return has %d" ps.Length n) body.Span
            | PX.VVec _, None -> outside "the body is an array but the return is a scalar" body.Span
            | PX.VScalar _, Some _ -> outside "the body is a scalar but the return is a representation-typed array" body.Span
            | (PX.VInvArr _ | PX.VOpaque _), _ -> outside "the body is an invariant with no polynomial form" body.Span)

/// THE STITCH POINT (stage C2). Run the polynomial engine on a TYPED body
/// under a candidate DeduceRep signature. `None` -- nothing to say (no
/// applicable action set, an unclassifiable signature, an extraction
/// refusal, or a cap breach); not evidence either way, so the caller falls
/// through to whatever it would have done without the engine.
/// `Some EngineHolds` -- `ml.equiv(sg.Group)` is DISCHARGED for this body.
/// `Some (EngineRefutes msg)` -- the identity fails; `msg` names the
/// element/generator, output component and first offending coefficient.
///
/// TOTAL: every escape from the point-group registry, the spec decoders or
/// the substitution reads as `None`. `LieDischarge.LieGuardFailure` (a
/// compiler-bug assert, not a decoder escape) is re-raised, exactly as
/// `MLEquiv.judgeFunction` re-raises it.
let engineVerdict (resolve: IRType -> IRType) (parms: RepParam list) (sg: RepSigT) (body: TypedExpr)
    : TypedEngineVerdict option =
    try
        match extractTyped resolve parms sg body with
        | Error _ -> None
        | Ok form ->
            match sg.Group with
            | GPoint gn ->
                match pointActions gn sg with
                | None -> None
                | Some actions ->
                    match PX.discharge form actions with
                    | Ok () -> Some EngineHolds
                    | Error (PX.DischargeCap _) -> None
                    | Error (PX.GeneratorCheck f) -> Some (EngineRefutes (EM.failureMessage sg.Owner gn f))
            | GO3 | GSO3 ->
                match o3Actions sg.Group sg with
                | None -> None
                | Some (gens, inv) ->
                    match LD.discharge form gens inv with
                    | Ok () -> Some EngineHolds
                    | Error (LD.DischargeCap _) -> None
                    | Error (LD.GeneratorCheck f) ->
                        Some (EngineRefutes (EM.lieFailureMessage sg.Owner (groupStrT sg.Group) f))
                    | Error (LD.ParityCheck f) -> Some (EngineRefutes (EM.inversionFailureMessage sg.Owner f))
    with
    | LD.LieGuardFailure _ -> reraise ()
    | _ -> None

/// The one-bit form, for the deduction hook: did the engine DISCHARGE this
/// body? A refutation and a refusal are both `false` -- deduction proposes,
/// it never rejects, so anything short of a discharge is silence.
let engineDischarges (resolve: IRType -> IRType) (parms: RepParam list) (sg: RepSigT) (body: TypedExpr) : bool =
    match engineVerdict resolve parms sg body with
    | Some EngineHolds -> true
    | _ -> false
