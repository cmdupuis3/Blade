/// THE TYPED POLYNOMIAL EXTRACTOR — stage C2 of
/// docs/plan-equivariance-in-types.md ("Engine port: PolyExtract gets a
/// TypedExpr extractor (discharge — finite elements and the radical-vector Lie
/// identity — is already IR-agnostic; only extraction walks syntax)").
///
/// The plan's premise is CONFIRMED by construction here: not one line of the
/// two dischargers is reimplemented. `Blade.ML.PolyExtract.discharge` (the
/// finite word-set check over ℚ[atoms]) and `Blade.ML.LieDischarge.discharge`
/// (the radical-vector 𝔰𝔬(3) check plus the π₀ −I identity) both consume a
/// `PolyExtract.PolyForm` and a list of actions built from a spec — neither
/// knows what an `Expr` or a `TypedExpr` is. What this module adds is a SECOND
/// FRONT HALF: the same normal form, produced from a POST-ELABORATION
/// `TypedExpr` under `Blade.DeduceRep`'s vocabulary instead of from a surface
/// `FunctionDecl` under MLEquiv's.
///
/// ---------------------------------------------------------------------------
/// WHY A SECOND EXTRACTOR AND NOT A SHARED ONE
/// ---------------------------------------------------------------------------
/// The two IRs disagree about what arithmetic IS. At the seam, `x * s + j` on
/// arrays is an `ExprBinOp` tree and the surface extractor's whole-array arms
/// fire directly. By typecheck the SAME source has been desugared into nested
/// `TExprApply` former applications over generated `method_for` loops with
/// generated kernel lambdas (`__zl`, `__zr`, `__bx`), and the `ExprBinOp` arm
/// never sees it. Conversely the component-read fragment — the one the two
/// TYPED-EXEMPT corpus files actually need — survives almost verbatim:
/// `[0.0 - x(1), x(0)]` is still a `TExprArrayLit` of `TExprBinOp` over
/// `TExprIndex`. So the port is: keep the normal form and the value algebra,
/// rewrite the walk, and add ONE new arm (`TExprApply`) for the shapes the
/// desugaring creates.
///
/// ---------------------------------------------------------------------------
/// SOUNDNESS POSTURE — `None` IS ALWAYS SAFE
/// ---------------------------------------------------------------------------
/// A refusal costs recall; a WRONG extraction is the only unsound outcome,
/// because the discharge then certifies a polynomial that is not the body.
/// Every arm below therefore refuses by default: the walk is a closed-world
/// whitelist, `extractError` carries the reason for a diagnostic that nobody
/// currently prints, and the entry points are total (any escape reads as "the
/// engine has nothing to say"). This mirrors the seam's own discipline
/// (MLEquiv's `try ... with _ -> [ d ]`): a speculative second opinion may
/// never turn a compiling program into a crash.
///
/// ---------------------------------------------------------------------------
/// THE FRAGMENT (the v1 surface fragment, plus the desugaring arm)
/// ---------------------------------------------------------------------------
///   1. numeric literals, dyadic-exact (`PolyExtract.Rat.tryOfFloatExact`);
///   2. `TExprIndex` / `TExprApp` at a LITERAL offset into a rep-bound vector
///      or an invariant array;
///   3. scalar + - * and / by a nonzero constant, via the SHARED value algebra;
///   4. whole-array + - between equal-length vectors and invariant · array;
///   5. `TExprLet` and `TExprBlock` statement lets (no destructuring);
///   6. `TExprArrayLit` of scalar polynomials as the assembled return;
///   7. `TExprArrayNegate`, `TExprUnaryOp OpNeg`, `TExprCompute` (peeled);
///   8. `TExprApply` of a LAMBDA kernel over sources that all extract to
///      vectors of the same length — the desugared whole-array arithmetic —
///      under the two guards in `extractApply`.
///
/// `TExprUnaryOp (OpMath _, _)` is REFUSED explicitly and by name: post
/// elaboration `exp(v)` is a unary op node, not a call, and passing it through
/// would put a transcendental inside a polynomial normal form. That is the
/// same post-elaboration trap DeduceRep documents at its own unary arm.
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

// ---------------------------------------------------------------------------
// The abstract value — the typed twin of MLPolyExtract's private `Val`
// ---------------------------------------------------------------------------

/// Deliberately NOT a status lattice: every case carries actual polynomial
/// data, because the verdict comes from coefficients and not from a type.
type private TVal =
    | VScalar of PX.Poly
    /// An assembled vector of scalar polynomials: a rep parameter, an array
    /// literal, or anything the array arms built from those.
    | VVec of PX.Poly []
    /// An invariant ARRAY parameter: no polynomial of its own, only its
    /// statically-indexed cells, each an opaque atom.
    | VInvArr of string
    /// An invariant whose SHAPE the classifier could not decide. Every use
    /// leaves the fragment — modelling an array as a scalar atom would be
    /// unfaithful, and faithfulness is what the whole engine rests on.
    | VOpaque of string

type private Ctx = {
    /// Remaining term budget, shared across the whole extraction so a body
    /// cannot dodge the cap by spreading a blow-up over many components.
    mutable Budget: int
}

let private outside (msg: string) (span: Span) : Result<'a, PX.ExtractError> =
    Error (PX.OutsideFragment (msg, span))

/// Charge a freshly built polynomial against MLPolyExtract's caps — the SAME
/// two constants, read from that module rather than restated.
let private charge (ctx: Ctx) (p: PX.Poly) : Result<PX.Poly, PX.ExtractError> =
    let n = PX.Poly.terms p
    if n > PX.maxTerms then
        Error (PX.CapBreach (sprintf "the expanded form exceeded the %d-term cap" PX.maxTerms))
    elif PX.Poly.repDegree p > PX.maxRepDegree then
        Error (PX.CapBreach (sprintf "the body's degree in the representation components exceeds the degree-%d cap" PX.maxRepDegree))
    else
        ctx.Budget <- ctx.Budget - n
        if ctx.Budget < 0 then
            Error (PX.CapBreach (sprintf "the expanded form exceeded the %d-term cap" PX.maxTerms))
        else Ok p

let private constPoly (ctx: Ctx) (c: PX.Rat) = charge ctx (PX.Poly.ofRat c) |> Result.map VScalar

let private chargeVec (ctx: Ctx) (ps: PX.Poly []) : Result<TVal, PX.ExtractError> =
    ps
    |> Array.fold (fun acc p -> acc |> Result.bind (fun out -> charge ctx p |> Result.map (fun q -> out @ [ q ])))
        (Ok [])
    |> Result.map (List.toArray >> VVec)

/// The binary algebra, arm for arm the same as MLPolyExtract's `extractBinOp`.
/// It is restated rather than shared because that function is `private` there
/// and the two walkers carry different value types; the RULES are identical and
/// any change must be made in both (the divergence would be a soundness bug,
/// not a recall difference).
let private binOp (ctx: Ctx) (span: Span) (op: BinOp) (vl: TVal) (vr: TVal)
    : Result<TVal, PX.ExtractError> =
    let bad msg = outside msg span
    match vl, vr with
    | VOpaque n, _ | _, VOpaque n ->
        bad (sprintf "the shape of invariant '%s' is not decidable from its type" n)
    | VInvArr _, _ | _, VInvArr _ ->
        bad "an invariant array has no polynomial form — read its cells at static indices"
    | VScalar a, VScalar b ->
        match op with
        | OpAdd -> charge ctx (PX.Poly.add a b) |> Result.map VScalar
        | OpSub -> charge ctx (PX.Poly.sub a b) |> Result.map VScalar
        | OpMul -> charge ctx (PX.Poly.mul a b) |> Result.map VScalar
        | OpDiv ->
            // ℚ[atoms] has no inverses: the divisor must be a nonzero constant.
            match PX.Poly.asConstant b with
            | Some c when not (PX.Rat.isZero c) ->
                charge ctx (a |> Map.map (fun _ x -> PX.Rat.div x c)) |> Result.map VScalar
            | Some _ -> bad "division by zero"
            | None -> bad "division is admitted only by a nonzero constant — an invariant atom has no inverse in the coefficient ring"
        | _ -> bad "this operator is outside the polynomial fragment"
    | VVec a, VVec b ->
        if a.Length <> b.Length then
            bad (sprintf "whole-array arithmetic needs equal shapes (%d vs %d components)" a.Length b.Length)
        else
            match op with
            | OpAdd -> Array.map2 PX.Poly.add a b |> chargeVec ctx
            | OpSub -> Array.map2 PX.Poly.sub a b |> chargeVec ctx
            | _ -> bad "only + and - are admitted between two arrays"
    | VScalar s, VVec v | VVec v, VScalar s ->
        // The scalar factor must be INVARIANT in the polynomial sense —
        // rep-degree 0 — which is the composition rule's `Rep s, Inv, OpMul`
        // arm read over coefficients.
        match op with
        | OpMul when PX.Poly.repDegree s = 0 -> v |> Array.map (PX.Poly.mul s) |> chargeVec ctx
        | OpMul -> bad "scaling an array is admitted only by an INVARIANT scalar (rep-degree 0)"
        | _ -> bad "only invariant scaling is admitted between a scalar and an array"

/// A provably compile-time integer offset. `TExprCompute` is a scheduling
/// boundary and is peeled; ANYTHING ELSE — a variable, an arithmetic
/// expression, a fold this walker cannot see — is not a literal and the caller
/// declines. (The surface extractor could run the static evaluator here; by
/// typecheck the `let static` folds have already happened, so a surviving
/// non-literal offset is genuinely dynamic.)
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

// ---------------------------------------------------------------------------
// The walk
// ---------------------------------------------------------------------------

let rec private extractVal (ctx: Ctx) (resolve: IRType -> IRType) (env: Map<IRId, TVal>) (e: TypedExpr)
    : Result<TVal, PX.ExtractError> =
    let go = extractVal ctx resolve env
    match e.Kind with

    // --- literals ---------------------------------------------------------
    | TExprLit (LitInt n) -> constPoly ctx (PX.Rat.ofBigInt (bigint n))
    | TExprLit (LitFloat f) ->
        (match PX.Rat.tryOfFloatExact f with
         | Some r -> constPoly ctx r
         | None -> outside "a non-finite float literal is not a polynomial coefficient" e.Span)
    | TExprLit _ -> outside "only numeric literals are polynomial coefficients" e.Span

    // --- names ------------------------------------------------------------
    // Only a bound name (parameter, let, kernel parameter) has a polynomial.
    // A free name — a module global, a builtin, a function — is refused: the
    // normal form cannot account for it, and guessing a value would be the
    // unsound direction.
    | TExprVar (n, vid, _) ->
        (match Map.tryFind vid env with
         | Some v -> Ok v
         | None -> outside (sprintf "'%s' is not a parameter or a local binding of this body" n) e.Span)

    // --- arithmetic -------------------------------------------------------
    | TExprBinOp (Elementwise, op, l, r) ->
        go l |> Result.bind (fun vl -> go r |> Result.bind (fun vr -> binOp ctx e.Span op vl vr))
    | TExprBinOp _ ->
        outside "outer-product operators are outside the polynomial fragment" e.Span

    // Negation is the linear map -I on scalars and on vectors alike.
    | TExprUnaryOp (OpNeg, inner) ->
        go inner |> Result.bind (fun v ->
            match v with
            | VScalar p -> Ok (VScalar (PX.Poly.neg p))
            | VVec ps -> Ok (VVec (ps |> Array.map PX.Poly.neg))
            | VInvArr _ -> outside "an invariant array has no polynomial form — read its cells at static indices" e.Span
            | VOpaque n -> outside (sprintf "the shape of invariant '%s' is not decidable from its type" n) e.Span)
    // `OpMath` lands here. A transcendental has no polynomial normal form, and
    // by typecheck it is a UNARY OP rather than the named call MLEquiv sees —
    // so this refusal has to be explicit or a nonlinearity would ride through
    // the `OpNeg` arm's shape.
    | TExprUnaryOp _ -> outside "this unary operator is outside the polynomial fragment" e.Span

    | TExprArrayNegate a ->
        go a |> Result.bind (fun v ->
            match v with
            | VVec ps -> Ok (VVec (ps |> Array.map PX.Poly.neg))
            | VScalar p -> Ok (VScalar (PX.Poly.neg p))
            | _ -> outside "whole-array negation needs an extracted vector" e.Span)

    // --- assembled returns ------------------------------------------------
    // THE ARM COMPOSITION REFUSES (see MLPolyExtract's header): an array
    // literal of scalar polynomials is the assembled rep-valued return.
    | TExprArrayLit (es, _) ->
        es
        |> List.fold (fun acc x ->
            acc |> Result.bind (fun ps ->
                go x |> Result.bind (fun v ->
                    match v with
                    | VScalar p -> Ok (ps @ [ p ])
                    | _ -> outside "an array literal must be built from SCALAR polynomials — nested aggregates are outside the fragment" x.Span)))
            (Ok [])
        |> Result.map (List.toArray >> VVec)

    // --- reads ------------------------------------------------------------
    | TExprIndex (arr, idxs, _) -> extractIndex ctx resolve env e arr idxs
    // Application syntax over a bound array IS an index. A genuine CALL —
    // a certified callee, a builtin, anything with a non-array head — is a v1
    // deferral, exactly as at the seam.
    | TExprApp (f, args) ->
        (match f.Kind with
         | TExprVar (_, vid, _) when (Map.containsKey vid env) -> extractIndex ctx resolve env e f args
         | _ -> outside "calls are outside the v1 polynomial fragment" e.Span)

    // --- bindings ---------------------------------------------------------
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

    // --- the desugaring arm -----------------------------------------------
    | TExprApply info -> extractApply ctx resolve env e info

    | _ -> outside "this expression is outside the v1 polynomial fragment" e.Span

/// A static read of a bound vector or of an invariant array's cell. Shared by
/// the `TExprIndex` and application-syntax arms, which are the same operation
/// spelled two ways by the checker.
and private extractIndex (ctx: Ctx) (resolve: IRType -> IRType) (env: Map<IRId, TVal>)
                         (e: TypedExpr) (arr: TypedExpr) (idxs: TypedExpr list)
    : Result<TVal, PX.ExtractError> =
    match idxs with
    | [ idxE ] ->
        extractVal ctx resolve env arr |> Result.bind (fun v ->
            match v with
            | VVec ps ->
                (match staticOffset idxE with
                 | Some i when i >= 0 && i < ps.Length -> Ok (VScalar ps.[i])
                 | Some i -> outside (sprintf "index %d is outside a %d-component value" i ps.Length) e.Span
                 | None -> outside "indexing a representation-typed value needs a static offset" e.Span)
            | VInvArr name ->
                (match staticOffset idxE with
                 | Some i -> charge ctx (PX.Poly.ofMono (PX.Mono.invAtom { Name = name; Index = Some i })) |> Result.map VScalar
                 | None -> outside (sprintf "indexing the invariant '%s' needs a static offset" name) e.Span)
            | VScalar _ -> outside "a scalar cannot be indexed" e.Span
            | VOpaque name ->
                outside (sprintf "the shape of invariant '%s' is not decidable from its type" name) e.Span)
    | _ -> outside "only single-offset reads are admitted in the v1 polynomial fragment" e.Span

/// THE POST-ELABORATION ARM. Whole-array arithmetic that the seam extractor
/// meets as `ExprBinOp` arrives here as a former application over a generated
/// kernel lambda: `x * s` is
///
///     compute(apply(method_for(x), lambda(__bx) -> __bx * s))
///
/// and `a + b` is the co-iterating two-source twin. The rule is the obvious
/// one — bind kernel parameter k to component i of source k and re-extract the
/// kernel body once per component — and its validity rests on two guards,
/// BOTH of which are refusals in the safe direction:
///
///  1. ITERATION SHAPE. A former may CROSS-ITERATE, in which case output cell
///     (i, j) reads sources at DIFFERENT positions and the componentwise
///     reading is simply false. So a multi-source apply is admitted only when
///     the checker marked it a co-iteration (a zip), and in every case the
///     output type's total literal cell count must equal the common source
///     length. An outer product fails the second test (n·m ≠ n) whenever
///     either extent exceeds 1, and the co-iteration flag rules out the
///     degenerate remainder. The KERNEL ARITY is checked the same way, off the
///     apply's own metadata: an `object_for` kernel takes whole SUB-ARRAYS
///     (`KernelInputRanks` above 0) and a compose-apply puts arrays in the
///     kernel slot entirely, and in neither case is "parameter k is component i
///     of source k" the right reading. Every desugared operator arithmetic form
///     the checker builds has ranks (0, …, 0) -> 0 and `IsComposeApply` false,
///     so refusing the rest costs no recall.
///  2. UNIFORMITY. Every kernel parameter is bound to a SCALAR polynomial for
///     the position being built, so a kernel that reads a position-dependent
///     value it was not handed (a captured array at a computed offset, the
///     loop index, another former) leaves the fragment in `extractVal` rather
///     than being silently treated as uniform. A captured SCALAR is bound in
///     `env` and reads as the same atom at every position, which is exactly
///     what it is.
///
/// Nothing here weakens the discharge: an extracted body still has to pass the
/// coefficientwise identity, which is a strictly stronger obligation than any
/// composition rule.
and private extractApply (ctx: Ctx) (resolve: IRType -> IRType) (env: Map<IRId, TVal>)
                         (e: TypedExpr) (info: TypedApplyInfo)
    : Result<TVal, PX.ExtractError> =
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
                        | VVec ps -> Ok (out @ [ ps ])
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
                                Map.add p.VarId (VScalar s.[i]) m) env
                        extractVal ctx resolve kEnv lam.Body |> Result.bind (fun v ->
                            match v with
                            | VScalar p -> Ok (out @ [ p ])
                            | _ -> outside "a kernel must produce a SCALAR polynomial per position" lam.Body.Span)))
                    (Ok [])
                |> Result.map (List.toArray >> VVec))
    | _ -> outside "only a lambda kernel is admitted in the v1 polynomial fragment" e.Span

// ---------------------------------------------------------------------------
// The signature bridge: DeduceRep's vocabulary -> the extractor's
// ---------------------------------------------------------------------------

/// The ℝ-dimension of a rep payload. The O(3) arm rebuilds an `MLS.Spec` from
/// DeduceRep's `(l, parity, mult)` triples — the SAME payload
/// `Types.mkIrrepsTag` serializes, in the same order — and asks MLSpec for the
/// dimension rather than restating `mult * (2l + 1)`.
let private specOfTriples (triples: (int * int * int) list) : MLS.Spec =
    triples |> List.map (fun (l, p, m) -> { MLS.L = l; MLS.Parity = p; MLS.Mult = m })

let private repDim (r: RepSpecT) : int option =
    match r with
    | TO3Spec triples -> Some (MLS.totalDim (specOfTriples triples))
    | TPgSpecT (g, entries) ->
        try Some (PS.pgTotalDim (PS.pointGroup g) entries) with _ -> None

/// The classified signature the extractor needs, from a DeduceRep signature.
/// The invariant SHAPE mapping is where the typed side is STRICTLY BETTER than
/// the seam: MLEquiv's `invKind` has to guess an invariant's shape from the
/// spelling of its surface annotation (`Float`, `Float64`, ... — anything else
/// becomes `PInvOpaque`), while `TInvScalar` here is a PROVEN 0-dimensionality
/// read off `IRTScalar`. `TInvShapeUnknown` still becomes `PInvOpaque`, which
/// refuses every operation, so the improvement is recall-only.
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

/// The word set as `ElementAction`s: ALL |G| elements, the seam's
/// `Engine.pointActions` read off a `RepSigT` instead of a `CertSig`. A scalar
/// (invariant) return is an INVARIANCE claim, so the output action is the 1×1
/// identity — the trivial rep.
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

/// The 𝔰𝔬(3) generators (and, under O3 only, the −I parity bookkeeping) — the
/// seam's `Engine.o3Actions` read off a `RepSigT`. A scalar return is the
/// trivial rep: a 1×1 ZERO generator and parity EVEN, which is what makes
/// `-> Float` under equiv(O3) reject a pseudoscalar body while the same body
/// certifies under equiv(SO3).
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

// ---------------------------------------------------------------------------
// THE ENTRY POINTS
// ---------------------------------------------------------------------------

/// What the engine has to say about one typed body under one candidate
/// signature. `None` from the entry points below means "nothing" — no
/// applicable action set, an unclassifiable signature, an extraction refusal or
/// a cap breach — and is the overwhelmingly common answer.
type TypedEngineVerdict =
    /// The polynomial normal form satisfies the equivariance identity at every
    /// enumerated group element (point groups) or at every 𝔰𝔬(3) generator plus
    /// −I (O3/SO3). The certificate HOLDS.
    | EngineHolds
    /// The body IS a polynomial and it is NOT equivariant, with the offending
    /// element/generator, output component and monomial named. A DEDUCTION
    /// consumer treats this exactly like `None` (no proposal); it is
    /// distinguished because a CHECKING consumer (stage C1) wants to say so.
    | EngineRefutes of string

// A refutation is rendered by `Blade.ML.EquivMessages` — the SAME four
// constructors the seam calls, not a shorter twin of them.
//
// This module used to carry its own abbreviated `renderFinite` / `renderLie` /
// `renderInversion`, on the reasoning that the long form was the seam's
// user-facing text and hand-copying it here would guarantee drift. The
// diagnosis was right and the remedy was not: two copies of different lengths
// drift exactly as readily as two copies of the same length, and the shorter
// one silently became a second, worse answer to the same question. The text now
// lives in one module that both front halves consume, so there is no paired
// maintenance obligation left to honour — a change to the wording is a change
// to `MLEquivMessages.fs`, and both sides get it.
//
// The three failure records reach here in exactly the shape those constructors
// take (`PX.DischargeFailure`, `LD.LieFailure`, `LD.InversionFailure`), which
// is why the sharing is a call and not an adapter.

/// Extract a typed body to the shared normal form under a DeduceRep signature.
/// Public so a future consumer (a checking-side C1 diagnostic, a test) can see
/// WHY extraction refused; the deduction hook only needs `engineVerdict`.
///
/// `parms` supplies the binder ids the walker keys its environment by — the
/// same `RepParam` list `DeduceRep.deduceFunctionRep` already holds — and MUST
/// be in the same order as `sg.Params`, which is how that function builds it.
let extractTyped (resolve: IRType -> IRType) (parms: RepParam list) (sg: RepSigT) (body: TypedExpr)
    : Result<PX.PolyForm, PX.ExtractError> =
    match polySigOf sg with
    | None -> Error (PX.OutsideFragment ("the signature does not classify for the polynomial engine", body.Span))
    | Some psig when List.length psig.Params <> List.length parms ->
        Error (PX.OutsideFragment ("internal: parameter list and classified signature disagree in length", body.Span))
    | Some psig ->
        let ctx = { Budget = PX.maxTerms }
        let env =
            List.zip parms psig.Params
            |> List.fold (fun acc ((p: RepParam), (name, kind)) ->
                match kind with
                | PX.PRep n -> Map.add p.PId (VVec (Array.init n (fun i -> PX.Poly.ofMono (PX.Mono.repVar name i)))) acc
                | PX.PInvArray -> Map.add p.PId (VInvArr name) acc
                | PX.PInvScalar -> Map.add p.PId (VScalar (PX.Poly.ofMono (PX.Mono.invAtom { Name = name; Index = None }))) acc
                | PX.PInvOpaque -> Map.add p.PId (VOpaque name) acc)
                Map.empty
        extractVal ctx resolve env body
        |> Result.bind (fun v ->
            match v, psig.ReturnDim with
            | VScalar p, None -> Ok ({ PX.Components = [| p |] } : PX.PolyForm)
            | VVec ps, Some n when ps.Length = n -> Ok ({ PX.Components = ps } : PX.PolyForm)
            | VVec ps, Some n ->
                outside (sprintf "the body assembles %d components but the return has %d" ps.Length n) body.Span
            | VVec _, None -> outside "the body is an array but the return is a scalar" body.Span
            | VScalar _, Some _ -> outside "the body is a scalar but the return is a representation-typed array" body.Span
            | (VInvArr _ | VOpaque _), _ -> outside "the body is an invariant with no polynomial form" body.Span)

/// THE STITCH POINT (stage C2). Run the polynomial engine on a TYPED body
/// under a candidate DeduceRep signature, and return what it has to say.
///
///   * `None`             — the engine has nothing to say. No applicable action
///                          set, an unclassifiable signature, an extraction
///                          refusal, or a cap breach. This is not evidence
///                          either way and a caller must fall through to
///                          whatever it would have done without the engine.
///   * `Some EngineHolds` — the certificate `ml.equiv(sg.Group)` is DISCHARGED
///                          for this body at this signature.
///   * `Some (EngineRefutes msg)` — the body is a polynomial and the identity
///                          fails; `msg` names the element/generator, the
///                          output component and the first offending
///                          coefficient.
///
/// TOTAL. Every escape from the point-group registry, the spec decoders or the
/// substitution reads as `None` — the seam's discipline, for the seam's reason:
/// a speculative second opinion may never turn a compiling program into a
/// crash. `LieDischarge.LieGuardFailure` (the post-accept float guard, a
/// compiler-bug assert rather than a decoder escape) is re-raised, exactly as
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
/// body? A refutation and a refusal are both `false` — deduction proposes, it
/// never rejects, so anything short of a discharge is silence.
let engineDischarges (resolve: IRType -> IRType) (parms: RepParam list) (sg: RepSigT) (body: TypedExpr) : bool =
    match engineVerdict resolve parms sg body with
    | Some EngineHolds -> true
    | _ -> false
