/// Phase B of docs/plan-equivariance-in-types.md: the typecheck-resident
/// representation-status deduction — the fourth lattice made typed. This
/// module is the typed sibling of Deduce.fs (parity/sign) and the eventual
/// successor of MLEquiv's elaboration-seam inference (stage 6a), which
/// remains the CHECKING and EMITTING authority through phase B: proposals
/// produced here ride the TypedCertProposals channel and are consumed ONLY
/// by the differential harness (tests/Test_RepDifferential.fs) until the
/// B3 parity gate holds (typed recall ⊇ seam recall, zero false proposals).
///
/// Compile order: after TypedAst/Deduce, before StaticEval — TypeCheck
/// (much later) can call in; nothing here may reference TypeEnv upward.
module Blade.DeduceRep

open Blade.Ast
open Blade.Types
open Blade.IR
open Blade.TypedAst

/// A typed-deduction certificate proposal — the structured twin of the
/// seam's suggestion strings, carrying what the differential needs to
/// compare: who, which group, the rendered signature (seam vocabulary,
/// so string comparison is meaningful), and the dependency closure.
type RepProposal = {
    Owner: string
    /// "O3" | "SO3" | "<g>" — the seam's groupStr vocabulary, which renders a
    /// point group as its BARE registry label ("C4", "D4"), never "Point C4"
    /// (MLEquiv.groupStr: `| Point n -> n`). The skeleton's original comment
    /// here said "Point <g>" and was wrong; the differential matches these
    /// strings against groupStr, so the bare label is the contract.
    Group: string
    /// Rendered like the seam's sigSummary, for differential comparison.
    Signature: string
    /// Dependency closure (unpinned helpers this proposal rests on), in
    /// decl order.
    Deps: string list
}

/// The internal channel between the typed walker (producer, TypeCheck
/// time) and the differential harness (consumer). NOT surfaced to users
/// in phase B — BL4011 stays the seam's to emit until the parity gate.
/// AsyncLocal, reset/add/get, the CertSuggestions lifecycle one phase
/// later.
module TypedCertProposals =
    let private slot = new System.Threading.AsyncLocal<(RepProposal * Span) list>()
    let reset () = slot.Value <- []
    let add (p: RepProposal) (span: Span) = slot.Value <- (p, span) :: slot.Value
    let get () : (RepProposal * Span) list =
        match box slot.Value with null -> [] | _ -> List.rev slot.Value

// ============================================================================
// The status lattice
// ============================================================================
//
// The typed twin of MLEquiv's {Rep, Inv of InvShape, Opaque}, plus ONE new
// element the seam does not need because it lives in a Result: `TBottom`.
//
//   TRep s   — the value transforms under the hypothesized group through
//              block-spec `s`. The only status that carries a THEOREM.
//   TInv sh  — the value is HELD FIXED by the action (the conditional
//              theorem's hypothesis). `sh` records provable 0-dimensionality,
//              which is load-bearing in exactly one rule (scaling a rep).
//   TOpaque  — nothing established. Propagates; never manufactures a claim.
//              This is MLEquiv's `Ok Opaque` verbatim.
//   TBottom  — the walker DECLINES: under this group hypothesis the body is
//              not judgeable, so no proposal. This is MLEquiv's `Error`
//              (a `reject`) with the diagnostic dropped: deduction proposes,
//              it never rejects, so a seam-rejection reads here as silence.
//
// PBottom discipline (Deduce.fs): no claim is never wrong. Every rule below
// that cannot PROVE its conclusion answers TBottom (or TOpaque where the seam
// answers Opaque), never a status.

/// WHICH block-spec family describes a value's transformation law. The two
/// cases never meet: a certificate names one group and `classifyType` admits
/// only that group's index family.
type RepSpecT =
    /// O(3)/SO(3) irreps, as (l, parity, mult) triples in spec order —
    /// exactly the payload `Types.mkIrrepsTag` serializes.
    | TO3Spec of (int * int * int) list
    /// Point-group irreps: registry group name plus (LABEL, mult) entries —
    /// exactly the payload `Types.mkPgIrrepsTag` serializes.
    | TPgSpecT of group: string * entries: (string * int) list

/// Shape refinement carried by `TInv`. `rep * c` is equivariant when `c` is a
/// SCALAR (a scalar commutes with every block of the action); `rep * w` for an
/// invariant ARRAY scales each component independently, and a diagonal matrix
/// with unequal entries does not commute with D^l. Mirror of MLEquiv.InvShape.
type InvShapeT =
    /// Provably 0-dimensional.
    | TInvScalar
    /// Provably an aggregate; `Some r` when the rank is known.
    | TInvAgg of rank: int option
    /// Shape not established — treated as non-scalar wherever scalarity is
    /// load-bearing.
    | TInvShapeUnknown

type RepStatusT =
    | TRep of RepSpecT
    | TInv of InvShapeT
    | TOpaque
    | TBottom

/// The group hypothesis. GROUP-LESS index types (plan A2, review decision 2):
/// the group is NOT in the type, so it is a hypothesis the candidate ladder
/// supplies and the walker carries.
type GroupT =
    | GO3
    | GSO3
    /// A registered point group, by MLPointSpec registry name (C4, D4).
    | GPoint of string

/// A function's certified-or-deduced rep signature: per-parameter status in
/// declaration order plus the return status, all classified from ZONKED types.
type RepSigT = {
    Owner: string
    Group: GroupT
    Params: (string * RepStatusT) list
    Return: RepStatusT
}

// ============================================================================
// Rendering — BYTE-COMPATIBLE with the seam, because the differential
// compares strings
// ============================================================================

/// Mirror of MLEquiv.groupStr. NOTE: the seam renders a point group as its
/// bare registry name (`C4`), not `Point C4` — the skeleton's field comment
/// says "the seam's groupStr vocabulary", and this is what that vocabulary
/// actually is.
let groupStrT (g: GroupT) : string =
    match g with GO3 -> "O3" | GSO3 -> "SO3" | GPoint n -> n

/// Parse a group name as written in an `__ml_equiv` conjunct argument.
let groupOfName (n: string) : GroupT =
    match n with "O3" -> GO3 | "SO3" -> GSO3 | other -> GPoint other

let private specStrT (s: (int * int * int) list) : string =
    s
    |> List.map (fun (l, p, m) -> sprintf "(%d, %d, %d)" l p m)
    |> String.concat ", "
    |> sprintf "[%s]"

let private pgSpecStrT (s: (string * int) list) : string =
    s
    |> List.map (fun (label, m) -> sprintf "(\"%s\", %d)" label m)
    |> String.concat ", "
    |> sprintf "[%s]"

/// Mirror of MLEquiv.repStr — the INDEX TYPE the user would have to write.
let repStrT (r: RepSpecT) : string =
    match r with
    | TO3Spec s -> sprintf "IrrepsIdx<%s>" (specStrT s)
    | TPgSpecT (g, s) -> sprintf "PgIrrepsIdx<%s, %s>" g (pgSpecStrT s)

/// Mirror of MLEquiv.sigSummary, phrase for phrase — this string is the
/// differential's comparison key.
let sigSummaryT (sg: RepSigT) : string =
    let one (n, st) =
        match st with
        | TRep r -> sprintf "%s transforms as %s" n (repStrT r)
        | TInv _ -> sprintf "%s invariant" n
        | _ -> sprintf "%s unclassifiable" n
    let ps =
        if sg.Params.IsEmpty then "(no parameters)"
        else sg.Params |> List.map one |> String.concat ", "
    let ret =
        match sg.Return with
        | TRep r -> repStrT r
        | TInv _ -> "invariant"
        | _ -> "unclassifiable"
    sprintf "%s -> %s" ps ret

// ============================================================================
// Lattice algebra (mirrors of MLEquiv's binShape / meetShape / joinStatus)
// ============================================================================

let private isInvT (s: RepStatusT) = match s with TInv _ -> true | _ -> false
let private isRepT (s: RepStatusT) = match s with TRep _ -> true | _ -> false

/// Shape of an elementwise binary combination (broadcast: scalar op aggregate
/// is the aggregate). Never claims scalar unless BOTH operands are scalar.
let private binShapeT (a: InvShapeT) (b: InvShapeT) : InvShapeT =
    match a, b with
    | TInvScalar, TInvScalar -> TInvScalar
    | TInvAgg r, TInvScalar | TInvScalar, TInvAgg r -> TInvAgg r
    | TInvAgg r1, TInvAgg r2 -> TInvAgg (if r1 = r2 then r1 else None)
    | _ -> TInvShapeUnknown

/// Meet of two shapes reached on different control-flow paths.
let private meetShapeT (a: InvShapeT) (b: InvShapeT) : InvShapeT =
    if a = b then a
    else
        match a, b with
        | TInvAgg _, TInvAgg _ -> TInvAgg None
        | _ -> TInvShapeUnknown

/// Merge two statuses reached on different control-flow paths. `None` = the
/// paths disagree, which the caller turns into TBottom (the seam rejects).
let private joinStatusT (a: RepStatusT) (b: RepStatusT) : RepStatusT option =
    match a, b with
    | TBottom, _ | _, TBottom -> None
    | TRep s1, TRep s2 when s1 = s2 -> Some (TRep s1)
    | TInv x, TInv y -> Some (TInv (meetShapeT x y))
    | TOpaque, TOpaque -> Some TOpaque
    | _ -> None

/// Statuses agree for certification purposes. Invariant SHAPE is deliberately
/// not part of the comparison — it exists only to decide the scaling rule.
let private statusAgreesT (a: RepStatusT) (b: RepStatusT) : bool =
    (joinStatusT a b) |> Option.isSome

// ============================================================================
// The TYPED CLASSIFIER — IRType -> RepStatusT
// ============================================================================
//
// The typed twin of MLEquiv.statusOfType, reading the block-spec tag off
// `IRIndexTypeG.Tag` through the `IrrepsTag`/`PgIrrepsTag` active patterns
// (Types.fs) rather than off a surface `TypeExpr`. That is the whole §0
// payoff: what unification propagated, this reads — so an UNANNOTATED
// parameter whose type closed at `Array<F like IrrepsIdx<S>>` classifies
// exactly like an annotated one.
//
// TAG FORMATS (verified against Types.mkIrrepsTag / Types.mkPgIrrepsTag):
//   "__irreps:<alias>:<l,p,m|l,p,m|...>"
//   "__pgirreps:<GROUP>:<alias>:<LABEL,mult|LABEL,mult|...>"
// The prefixes are disjoint as strings, so tag equality decides cross-member
// identity for free. The ALIAS NAME is deliberately dropped here: it is
// diagnostic sugar, not part of the transformation law, and the seam's
// `repStr` renders the long form too.

/// The O(3) spec riding an index slot's tag, or None.
let private irrepsOf (ix: IRIndexType) : (int * int * int) list option =
    match ix.Tag with
    | Some (IrrepsTag (_alias, triples)) -> Some triples
    | _ -> None

/// The point-group (group, spec) riding an index slot's tag, or None.
let private pgIrrepsOf (ix: IRIndexType) : (string * (string * int) list) option =
    match ix.Tag with
    | Some (PgIrrepsTag (group, _alias, entries)) -> Some (group, entries)
    | _ -> None

/// Provable 0-dimensionality of a type. `IRTScalar` IS 0-dimensional — that is
/// a fact the surface-syntax classifier had to guess at from builtin NAMES
/// (MLEquiv.isBuiltinScalarName) and the typed one simply reads. This is the
/// one place the typed lattice is deliberately STRONGER than the seam, and it
/// is sound in the strict direction: it can only turn "unknown shape" into
/// "provably scalar", and scalarity is only ever used to ADMIT scaling a rep
/// by something that provably commutes with every block of the action.
let rec private shapeOfType (resolve: IRType -> IRType) (ty: IRType) : InvShapeT =
    match resolve ty with
    | IRTScalar _ -> TInvScalar
    | IRTUnitAnnotated (inner, _) -> shapeOfType resolve inner
    | IRTIdxTagged (inner, _) -> shapeOfType resolve inner
    | ArrayElem arr -> TInvAgg (Some (List.length arr.IndexTypes))
    | _ -> TInvShapeUnknown

/// Classify a ZONKED type under a group hypothesis.
///
/// The group SELECTS the live index family: under O3/SO3 that is `IrrepsIdx`
/// and a `PgIrrepsIdx` buffer is an ordinary invariant (MLEquiv's 5b-ii
/// asymmetry, preserved verbatim); under `Point g` it is `PgIrrepsIdx<g, _>`
/// and both an `IrrepsIdx` slot and another group's `PgIrrepsIdx` are
/// refusals (TOpaque — the seam's `Error`, which skips the function).
let rec classifyType (g: GroupT) (resolve: IRType -> IRType) (ty: IRType) : RepStatusT =
    match resolve ty with
    | IRTScalar _ -> TInv TInvScalar
    | IRTUnitAnnotated (inner, _) -> classifyType g resolve inner
    | IRTIdxTagged (inner, _) -> classifyType g resolve inner
    | ArrayElem arr ->
        let idxs = arr.IndexTypes
        let n = List.length idxs
        let irreps = idxs |> List.choose irrepsOf
        let pgs = idxs |> List.choose pgIrrepsOf
        match g with
        | GPoint pg ->
            match irreps, pgs with
            // An O(3) irreps axis under a point-group certificate needs
            // `ml.restrict`'s branching rules (plan A3) to say anything:
            // until those land, decline rather than guess.
            | _ :: _, _ -> TOpaque
            | [], [ (gn, _) ] when gn <> pg -> TOpaque
            | [], [ (_, entries) ] when n = 1 -> TRep (TPgSpecT (pg, entries))
            | [], [] -> TInv (TInvAgg (Some n))
            | _ -> TOpaque
        | GO3 | GSO3 ->
            match irreps with
            // No irreps axis: a plain (or pg-tagged) buffer is invariant.
            | [] -> TInv (TInvAgg (Some n))
            | [ triples ] when n = 1 -> TRep (TO3Spec triples)
            // Multi-index arrays mixing an irreps axis with others are
            // outside the v1 fragment, exactly as at the seam.
            | _ -> TOpaque
    // A named type (struct/sum) is invariant but of unestablished shape, so
    // it may never scale a rep. Mirror of the seam's `TyNamed` arm.
    | IRTNamed _ -> TInv TInvShapeUnknown
    // Everything else — inference vars, arity-polymorphic packs, tuples,
    // function-typed params, loops, dists — is unclassifiable. TOpaque in a
    // SIGNATURE position skips the function (see `classifySignature`); it is
    // never a claim.
    | _ -> TOpaque

/// Is this type still open at decl close? Used ONLY for the skipped-
/// polymorphic accounting — the late/monomorphized tier is a named follow-up
/// (plan §2 "early/late split"), not this round.
let rec private isUnresolvedTy (resolve: IRType -> IRType) (ty: IRType) : bool =
    match resolve ty with
    | IRTInfer _ -> true
    | IRTPoly _ -> true
    | IRTUnitAnnotated (inner, _) -> isUnresolvedTy resolve inner
    | IRTIdxTagged (inner, _) -> isUnresolvedTy resolve inner
    | _ -> false

/// How many functions the EARLY tier declined because their signature was
/// still polymorphic at decl close, while carrying a rep-classifiable family
/// somewhere in the signature. Counted, never reported to the user: this is
/// the size of the late tier's inbox. AsyncLocal, reset beside the channel.
module SkippedPolymorphic =
    let private slot = new System.Threading.AsyncLocal<int>()
    let reset () = slot.Value <- 0
    let bump () = slot.Value <- slot.Value + 1
    let get () : int = slot.Value

// ============================================================================
// The candidate ladder (plan A2 — the group lives in the DEDUCTION, not the
// index type)
// ============================================================================

let private familiesOfTy (resolve: IRType -> IRType) (ty: IRType) : bool * Set<string> =
    match resolve ty with
    | ArrayElem arr ->
        arr.IndexTypes
        |> List.fold (fun (ir, pgs) ix ->
            match irrepsOf ix, pgIrrepsOf ix with
            | Some _, _ -> (true, pgs)
            | _, Some (gn, _) -> (ir, Set.add gn pgs)
            | _ -> (ir, pgs)) (false, Set.empty)
    | _ -> (false, Set.empty)

/// Candidate groups for a signature, STRONGEST FIRST — the typed mirror of
/// MLEquiv.candidatesFor:
///   * any `IrrepsIdx` axis            -> O3, then SO3 (mixed signatures land
///     here too: a pg buffer classifies invariant under an O(3) certificate,
///     so the O(3) candidates subsume it);
///   * `PgIrrepsIdx<g, _>` and no `IrrepsIdx` -> `Point g`, and only when the
///     signature names exactly ONE group.
///
/// DEVIATION from the seam, deliberate: the seam re-checks `gn` against
/// `PointSpec.pointGroupNames`. A tag can only be in a zonked type if
/// `irTypeBadPgIrrepsDetail` (TypeCheck's signature fence) already validated
/// it against that registry, and `Blade.ML.PointSpec` compiles AFTER this
/// module, so the check is both unavailable and redundant here.
let candidatesFor (resolve: IRType -> IRType) (tys: IRType list) : GroupT list =
    let (ir, pgs) =
        tys
        |> List.fold (fun (a, b) t ->
            let (x, y) = familiesOfTy resolve t
            (a || x, Set.union b y)) (false, Set.empty)
    if ir then [ GO3; GSO3 ]
    else
        match Set.toList pgs with
        | [ gn ] -> [ GPoint gn ]
        | _ -> []

// ============================================================================
// Interprocedural summary tables
// ============================================================================

/// Speculative (deduced-this-pass) summaries, their dependency closures, and
/// the DECL ORDER they were deduced in — the typed twin of the seam's
/// spec/deps/order fold, keyed by BINDER IRId rather than by name (the
/// FuncSignParities discipline: a parameter shadowing a top-level function's
/// name must not borrow its law).
///
/// Deduced summaries are ANALYSIS ONLY: they flow to callers inside this
/// compilation unit and are never exported. Only source-written pins (which
/// land in the CERTIFIED table) license checking — plan §4, unchanged.
type RepSpecTable() =
    /// (groupStr, callee binder id) -> speculative signature
    member val Sigs = System.Collections.Generic.Dictionary<string * IRId, RepSigT>()
    /// (groupStr, callee binder id) -> that proposal's own dependency closure
    member val Deps = System.Collections.Generic.Dictionary<string * IRId, string list>()
    /// decl order per group: (groupStr, binder id, name)
    member val Order = System.Collections.Generic.List<string * IRId * string>()

/// The walker's hypothesis environment.
type private RepCtx = {
    Group: GroupT
    Resolve: IRType -> IRType
    /// Pinned (`where ml.equiv(G)`) or elaborator-stamped callee summaries.
    Certified: IRId -> RepSigT option
    /// This pass's speculative summaries under THIS group.
    Speculative: IRId -> RepSigT option
    /// The function being judged. NO SUMMARY PROVES ITSELF: any mention of
    /// this binder — call position or value position — declines.
    Self: IRId
    /// Binder ids whose SPECULATIVE summaries this walk actually consumed.
    DepHits: System.Collections.Generic.HashSet<IRId>
    /// CHECKING MODE (phase C1). False for deduction, true for validating a
    /// declared certificate. The walk is otherwise IDENTICAL — this flag exists
    /// only to make the walker refuse to produce a DEFINITE status at the one
    /// rule where it is knowingly more permissive than the seam checker, so
    /// that a documented divergence can never be reported as a compiler bug.
    /// Deduction's results are untouched by it (it is always false there).
    Checking: bool
}

// ============================================================================
// THE TRANSFER TABLE
// ============================================================================
//
// Bottom-up over TypedExpr under a fixed group hypothesis. Every rule carries
// its soundness note; this table is the B4 Coq obligation.
//
// These are the POST-ELABORATION twins of MLEquiv.judge (:522-669): no `ml.*`
// op survives to typecheck, so the ml-op arms of the seam are replaced
// wholesale by the CALL rule — `derive_*` / `tp` / `poly` emitters stamp the
// functions they synthesize with `where ml.equiv(G)` (plan A1), which puts
// them in the certified table, and the call rule consumes them as axioms.

/// The COMPONENTWISE-UNIFORM LINEAR fragment: literals, variable reads, and
/// arithmetic on them. See the `TExprApply` rule for why a kernel body must be
/// inside this fragment before a per-element walk may conclude `TRep`.
/// Offsets of an O(3) spec whose cells hold FULL invariants under `g` — the
/// typed twin of MLEquiv.invariantOffsets restricted to the O3 member.
///
/// Under O3 only (l = 0, parity EVEN) blocks qualify; under SO3 any l = 0 block
/// does, because a pseudoscalar is an SO(3) invariant (it flips only under an
/// improper rotation). An l = 0 block has dim 1 per copy, so block `b` spans
/// `[start_b .. start_b + mult_b)`.
///
/// Block arithmetic is reimplemented here rather than shared: MLSpec compiles
/// AFTER this module. It is three lines and byte-checked against MLSpec's
/// `dim`/`blockDim`/`blockStarts` — block dim is `mult * (2l + 1)`, accumulated
/// in spec order.
///
/// POINT GROUPS ARE A NAMED DEFERRAL. The pg reading is the same conditional
/// theorem one member over — the invariant cells are those of a TRIVIAL label —
/// but which labels are trivial is registry data in MLPointSpec, which is also
/// out of compile-order reach. Hardcoding a label table here would drift from
/// the registry, and a drifted trivial-label table is a false invariant, so a
/// pg spec declines instead.
let private invariantOffsetsT (g: GroupT) (spec: (int * int * int) list) : Set<int> =
    match g with
    | GPoint _ -> Set.empty
    | GO3 | GSO3 ->
        let mutable start = 0
        let acc = System.Collections.Generic.List<int>()
        for (l, p, m) in spec do
            if l = 0 && (g = GSO3 || p = 0) then
                for k in 0 .. m - 1 do acc.Add (start + k)
            start <- start + m * (2 * l + 1)
        Set.ofSeq acc

/// A provably compile-time integer index, or None. `compute` is a scheduling
/// boundary and is peeled; anything else — a variable, an arithmetic
/// expression, a folded static this walker cannot see — is NOT a literal, and
/// the caller declines.
let rec private staticIntOf (e: TypedExpr) : int option =
    match e.Kind with
    | TExprLit (LitInt n) -> Some (int n)
    | TExprCompute inner -> staticIntOf inner
    | _ -> None

/// Does this subtree read any of `ids`? CONSERVATIVE BY DESIGN, in the
/// `Deduce.usesVar` discipline: a node kind not enumerated here answers TRUE.
/// The only consumer treats "mentions nothing" as a licence, so guessing FALSE
/// would be the unsound direction; guessing TRUE merely forfeits recall.
let rec private mentionsAnyId (ids: Set<IRId>) (e: TypedExpr) : bool =
    let any = List.exists (mentionsAnyId ids)
    match e.Kind with
    | TExprLit _ | TExprWildcard | TExprZero -> false
    | TExprVar (_, vid, _) -> Set.contains vid ids
    | TExprBinOp (_, _, l, r) -> any [ l; r ]
    | TExprUnaryOp (_, i) -> mentionsAnyId ids i
    | TExprCompute i | TExprPure i | TExprRead i -> mentionsAnyId ids i
    | TExprIndex (a, idxs, _) -> any (a :: idxs)
    | TExprField (b, _, _) -> mentionsAnyId ids b
    | TExprTupleIndex (t, i) -> any [ t; i ]
    | TExprApp (f, args) -> any (f :: args)
    | TExprTuple es | TExprSequence es | TExprStack es | TExprZip es -> any es
    | TExprArrayLit (es, _) -> any es
    | TExprArrayNegate a | TExprArrayConjugate a -> mentionsAnyId ids a
    | TExprIf (c, t, f) -> any [ c; t; f ]
    // Everything else — lambdas, formers, reduces, blocks, matches — is
    // deliberately unenumerated: answering TRUE costs only recall.
    | _ -> true

/// The COMPONENTWISE-UNIFORM LINEAR fragment, relative to a kernel's parameter
/// ids: literals, variable reads, and arithmetic on them — PLUS any subtree
/// that mentions no kernel parameter at all.
///
/// That second clause is what admits a captured scalar read like `q(0)` beside
/// the element being scaled. SOUNDNESS: a subtree that reads no kernel
/// parameter has the same value at every iteration position, so it is a genuine
/// loop CONSTANT; if its type is scalar it is one number for the whole array,
/// which is exactly the premise the scaling rule needs. A subtree that DOES
/// read a kernel parameter — `q(a)` — varies per position, and is admitted only
/// through the arithmetic cases, which is what stops a position-varying value
/// from passing itself off as a scalar multiplier (the `x * w` false
/// certificate, one level deeper).
let rec private isElementwiseArith (ps: Set<IRId>) (e: TypedExpr) : bool =
    if not (mentionsAnyId ps e) then true
    else
        match e.Kind with
        | TExprLit _ | TExprVar _ -> true
        | TExprBinOp (_, _, l, r) -> isElementwiseArith ps l && isElementwiseArith ps r
        | TExprUnaryOp (_, i) -> isElementwiseArith ps i
        | TExprCompute i -> isElementwiseArith ps i
        | _ -> false

let rec private statusOf (ctx: RepCtx) (env: Map<IRId, RepStatusT>) (expr: TypedExpr) : RepStatusT =
    let j = statusOf ctx env
    /// Shape read off the node's own (resolved) type — the typed win over the
    /// seam's syntactic shape guessing.
    let nodeShape () = shapeOfType ctx.Resolve expr.Type

    /// An aggregate constructor. SOUNDNESS: packing a rep into a literal
    /// aggregate loses its block structure — the aggregate does not transform
    /// as the rep — so a rep element declines. All-invariant elements make an
    /// invariant aggregate.
    let aggOf (es: TypedExpr list) =
        let sts = es |> List.map j
        if sts |> List.exists ((=) TBottom) then TBottom
        elif sts |> List.exists isRepT then TBottom
        elif sts |> List.exists ((=) TOpaque) then TOpaque
        else TInv (TInvAgg None)

    /// An assignment, as statement or expression. SOUNDNESS: writing an
    /// invariant value into an invariant destination cannot move anything the
    /// action fixes. Any rep on either side needs the seam's judgeAssign
    /// analysis, which v1 does not port: decline.
    let assignOk (lhs: TypedExpr) (rhs: TypedExpr) =
        isInvT (j lhs) && isInvT (j rhs)

    match expr.Kind with

    // --- literals ---------------------------------------------------------
    // SOUNDNESS: a constant does not move under any group action.
    | TExprLit _ -> TInv TInvScalar

    // --- variables --------------------------------------------------------
    // A parameter carries its classified status. A FREE variable (module
    // global, builtin, constant) is invariant by the conditional-theorem
    // reading — the theorem quantifies over the action on the PARAMETERS, and
    // a module-level constant is the same value in every frame — with its
    // shape read off its type. NOTE this is deliberately Inv even when the
    // global's own TYPE carries an irreps tag: a fixed buffer does not
    // transform, and calling it Rep would be the unsound direction.
    | TExprVar (_, vid, _) when vid = ctx.Self -> TBottom
    | TExprVar (_, vid, _) ->
        (match Map.tryFind vid env with
         | Some st -> st
         | None -> TInv (nodeShape ()))

    // --- arithmetic -------------------------------------------------------
    | TExprBinOp (mode, op, l, r) ->
        let sl = j l
        let sr = j r
        (match sl, sr, op with
         | TBottom, _, _ | _, TBottom, _ -> TBottom
         // SOUNDNESS: the outer-product form cross-iterates, so the result has
         // a HIGHER RANK than either operand and cannot be the rep it was
         // built from, whatever the operator.
         | (TRep _, _, _ | _, TRep _, _) when mode <> Elementwise -> TBottom
         // SOUNDNESS (Rep ± Rep): the action is LINEAR, so D(x+y) = Dx + Dy.
         // Requires IDENTICAL specs — different specs are different D's and
         // the sum transforms under neither.
         | TRep s1, TRep s2, (OpAdd | OpSub) -> if s1 = s2 then TRep s1 else TBottom
         // Elementwise product of two reps is the Clebsch-Gordan contraction's
         // job (ml.tensor_product), not a pointwise multiply: decline.
         | TRep _, TRep _, _ -> TBottom
         // SOUNDNESS (scalar scaling): a SCALAR commutes with every block of
         // the action, so D(cx) = cD(x). An invariant ARRAY of the same extent
         // scales each component independently — a diagonal matrix with
         // unequal entries does not commute with D^l — so scalarity must be
         // PROVEN; an unestablished shape declines.
         | TRep s, TInv TInvScalar, (OpMul | OpDiv) -> TRep s
         | TInv TInvScalar, TRep s, OpMul -> TRep s
         | (TRep _, TInv _, _) | (TInv _, TRep _, _) -> TBottom
         | TInv shl, TInv shr, _ ->
             TInv (if mode = Elementwise then binShapeT shl shr else TInvShapeUnknown)
         // Nothing established on one side: nothing claimed for the result.
         | TOpaque, _, _ | _, TOpaque, _ -> TOpaque)

    // SOUNDNESS: only a LINEAR unary op transports a rep. Negation is the
    // linear map -I, which commutes with every D, so `-x` transforms exactly
    // as `x`. Everything else applied to a rep is refused.
    //
    // `OpMath` IS THE TRAP HERE, and it is a post-elaboration trap the seam
    // never faces: at the surface `exp(x)` is an `ExprApp` of a named builtin,
    // which MLEquiv routes through its nonlinearity rejection. TypeCheck
    // rewrites whitelisted math names into `TExprUnaryOp (OpMath "exp", _)`,
    // so a blanket "a unary op passes its operand's status through" — which
    // reads correct against the seam's unary arm — silently CERTIFIES a
    // nonlinearity applied to rep components. (Probe: `method_for(x) <@>
    // lambda(v) -> exp(v)` proposed equivariant before this arm was split.)
    // A nonlinearity acts only on invariants; gate reps with ml.gated or
    // extract invariants with ml.scalars/ml.norms.
    | TExprUnaryOp (OpNeg, inner) -> j inner
    | TExprUnaryOp (_, inner) ->
        (match j inner with
         | TRep _ -> TBottom
         | s -> s)

    // SOUNDNESS: whole-array negation is the linear map -I, which commutes
    // with every D.
    | TExprArrayNegate a -> j a

    // Complex conjugation does NOT commute with a complex representation in
    // general (it conjugates the matrix): decline on a rep, pass through
    // otherwise.
    | TExprArrayConjugate a -> (match j a with TRep _ -> TBottom | s -> s)

    // --- control flow -----------------------------------------------------
    // SOUNDNESS: if the condition is invariant, the SAME branch is taken in
    // every frame, so the result's law is the branches' common law. A
    // condition that moves with the frame selects different branches in
    // different frames and proves nothing.
    | TExprIf (c, t, f) ->
        (match j c with
         | TInv _ ->
             (match joinStatusT (j t) (j f) with
              | Some s -> s
              | None -> TBottom)
         | _ -> TBottom)

    // Same rule, n-ary. Pattern-bound variables enter as invariants of
    // unestablished shape (destructuring a rep is refused: its components are
    // basis-dependent).
    | TExprMatch (scrut, cases) ->
        (match j scrut with
         | TInv _ ->
             let armSts =
                 cases
                 |> List.map (fun c ->
                     let env' =
                         c.Pattern.Bindings |> List.fold (fun m (_, vid, _) -> Map.add vid (TInv TInvShapeUnknown) m) env
                     statusOf ctx env' c.Body)
             (match armSts with
              | [] -> TInv TInvShapeUnknown
              | s :: rest ->
                  match rest |> List.fold (fun acc s2 -> acc |> Option.bind (fun a -> joinStatusT a s2)) (Some s) with
                  | Some joined -> joined
                  | None -> TBottom)
         | _ -> TBottom)

    // --- binding forms ----------------------------------------------------
    // The binding-descent problem, solved by ENVIRONMENT THREADING rather than
    // by Deduce.flattenBindings: this walker carries an env (the seam's
    // design), so inlining bindings first would be a no-op preprocessing pass
    // — and it is strictly more general, since flatten declines to inline a
    // non-rewritable or over-budget value and leaves a residual `let` that a
    // binding-free walker then bottoms out on.
    | TExprLet (_, vid, value, body) ->
        (match j value with
         | TBottom -> TBottom
         | sv -> statusOf ctx (Map.add vid sv env) body)

    | TExprBlock (stmts, final) ->
        let rec go (envAcc: Map<IRId, RepStatusT>) (ss: TypedStmt list) : Map<IRId, RepStatusT> option =
            match ss with
            | [] -> Some envAcc
            | TStmtLet b :: rest ->
                (match statusOf ctx envAcc b.Value with
                 | TBottom -> None
                 // Destructuring a rep exposes basis-dependent components.
                 | TRep _ when not b.SubBindings.IsEmpty -> None
                 | sv ->
                     let e1 = Map.add b.VarId sv envAcc
                     let e2 =
                         b.SubBindings
                         |> List.fold (fun m (_, vid, _) -> Map.add vid (TInv TInvShapeUnknown) m) e1
                     go e2 rest)
            | TStmtExpr x :: rest ->
                if statusOf ctx envAcc x = TBottom then None else go envAcc rest
            | TStmtAssign (l, r) :: rest ->
                if isInvT (statusOf ctx envAcc l) && isInvT (statusOf ctx envAcc r)
                then go envAcc rest else None
            | TStmtForIn (_, vid, lo, hi, body) :: rest ->
                // A loop counter is an integer: invariant scalar. The body is
                // checked in a scope that does not escape.
                if not (isInvT (statusOf ctx envAcc lo)) || not (isInvT (statusOf ctx envAcc hi)) then None
                else
                    match go (Map.add vid (TInv TInvScalar) envAcc) body with
                    | None -> None
                    | Some _ -> go envAcc rest
        (match go env stmts with
         | None -> TBottom
         | Some env' ->
             match final with
             | Some fe -> statusOf ctx env' fe
             | None -> TInv TInvShapeUnknown)

    | TExprSequence es ->
        let sts = es |> List.map j
        if sts |> List.exists ((=) TBottom) then TBottom
        else (match List.tryLast sts with Some s -> s | None -> TInv TInvShapeUnknown)

    | TExprAssign (l, r) -> if assignOk l r then TInv TInvShapeUnknown else TBottom

    // --- reads ------------------------------------------------------------
    // SOUNDNESS: `Inv` means the value is HELD FIXED. A value that does not
    // move has no part that moves, so a component of an invariant aggregate
    // picked by an invariant selector is invariant.
    //
    // A REP base declines IN GENERAL: its components are the basis-dependent
    // numbers this whole discipline exists to refuse — reading component k of
    // an l>0 block gives a number that changes with the frame.
    //
    // THE ONE EXCEPTION is a STATIC offset landing inside a trivial block.
    // SOUNDNESS: an (l = 0, even) block under O3 — or any l = 0 block under
    // SO3, since a pseudoscalar is fixed by every proper rotation — is acted on
    // by the identity, so the cell at that offset holds the SAME number in
    // every frame. That is precisely what `TInv` asserts, and the result is a
    // single cell, hence `TInvScalar`.
    //
    // The offset must be a LITERAL this walker can see. A computed index would
    // have to be proven to land in a trivial block, and an index this walker
    // cannot evaluate could land anywhere: decline.
    | TExprIndex (arr, idxs, _) ->
        (match j arr with
         | TBottom -> TBottom
         | TRep (TO3Spec spec) ->
             (match idxs with
              | [ i ] ->
                  (match staticIntOf i with
                   | Some k when Set.contains k (invariantOffsetsT ctx.Group spec) ->
                       TInv TInvScalar
                   | _ -> TBottom)
              | _ -> TBottom)
         // Point-group reps: see `invariantOffsetsT` — the trivial-LABEL table
         // is out of compile-order reach, so no offset is claimed.
         | TRep _ -> TBottom
         | TOpaque -> TOpaque
         | TInv _ ->
             if idxs |> List.forall (fun i -> isInvT (j i))
             then TInv (nodeShape ())
             else TBottom)

    | TExprTupleIndex (baseE, idxE) ->
        (match j baseE, j idxE with
         | TInv _, TInv _ -> TInv TInvShapeUnknown
         | TBottom, _ | _, TBottom -> TBottom
         | _ -> TOpaque)

    // A field name is a STATIC selector, so the base alone decides.
    | TExprField (baseE, _, _) ->
        (match j baseE with
         | TInv _ -> TInv TInvShapeUnknown
         | TBottom -> TBottom
         | _ -> TOpaque)

    // --- reduction --------------------------------------------------------
    // SOUNDNESS (the polarity note): a fold over a rep sums BASIS-DEPENDENT
    // COMPONENTS. The sum of the components of an l>0 vector is not a
    // rotational invariant (the norm is), so a rep source declines rather than
    // being mis-certified invariant.
    | TExprReduce (src, _, init) ->
        let ss = j src
        let si = match init with Some i -> j i | None -> TInv TInvScalar
        (match ss, si with
         | TBottom, _ | _, TBottom -> TBottom
         | TRep _, _ | _, TRep _ -> TBottom
         | TInv _, TInv _ -> TInv TInvScalar
         | _ -> TOpaque)

    // --- calls ------------------------------------------------------------
    // The interprocedural rule (B2). A call resolves by the callee's BINDER
    // IRId — the id every reference to a top-level function carries in its
    // `TExprVar` payload — against, in order:
    //   1. the CERTIFIED table (a source-written `where ml.equiv(G)` pin, or
    //      an elaborator stamp on a synthesized function, which is provable by
    //      construction): trusted as an axiom, exactly as the seam trusts it;
    //   2. this pass's SPECULATIVE table under the same group: consumed at
    //      suggestion strength, and RECORDED as a dependency so the proposal
    //      can name the pins it rests on.
    // When the stored signature does NOT apply — a group mismatch, an arity
    // mismatch, or an argument whose status does not match the stored parameter
    // status — the call FALLS THROUGH to the all-invariant rule rather than
    // declining outright.
    //
    // SOUNDNESS of the fall-through: a certificate is a statement about what
    // happens to values that TRANSFORM. When every argument is provably
    // invariant, nothing flowing in transforms, and the callee is a
    // deterministic map: the same inputs in every frame give the same output in
    // every frame, so the result is invariant no matter which group (if any)
    // the callee is certified for. The certificate is simply irrelevant to that
    // conclusion. Any `TRep` or `TOpaque` argument still declines, exactly as
    // before — that is the case where the certificate WOULD have been doing
    // work, and where a mismatch is a real loss of information.
    //
    // This is what recovers `ml.derive_pg_linear` under an O3 hypothesis: post-
    // elaboration it is a C4-STAMPED generated callee, and under O3 both its
    // arguments (the pg buffer, the weights) classify invariant.
    //
    // KNOWN DIVERGENCE, accepted and documented rather than special-cased: the
    // seam's checker refuses a cross-group CERTIFIED call in BOTH directions,
    // even when every argument is invariant — a coarser rule than this one. So
    // an O3-certified body calling a C4-certified USER function on invariants
    // is now a typed-only proposal whose pinned twin the seam checker would
    // reject: a false positive by B3's letter. No corpus file has that shape
    // (052 is a whole-file reject, so inference never runs there), and the
    // differential's false-positive assertion is the standing guard. Gating
    // this on generated-callee NAMES was rejected as the worse fix.
    | TExprApp (f, args) ->
        let argSts = args |> List.map j
        /// The all-invariant rule, shared by the uncertified-callee arm and by
        /// the certified arm's fall-through. Shape comes from the node's own
        /// type, i.e. the callee's return type read under the CURRENT
        /// hypothesis.
        let allInvRule () =
            if argSts |> List.forall isInvT then TInv (nodeShape ()) else TBottom
        (match f.Kind with
         | TExprVar (_, fid, _) when fid = ctx.Self -> TBottom
         | TExprVar (_, fid, _) ->
             (match Map.tryFind fid env with
              // Application syntax over a rep-bound variable is a component
              // read, same verdict as TExprIndex.
              | Some (TRep _) -> TBottom
              // A callee whose own status is unknown or declined cannot be
              // taken for an invariant function. THIS GUARD IS LOAD-BEARING:
              // without it a value produced by a node this table does not model
              // (TOpaque) would take the uncertified-callee path below and hand
              // back an INVARIANT — the shape of the false accept MLEquiv
              // documents at its `judgeFormerApply` (corpus ml-equiv/049).
              | Some TOpaque | Some TBottom -> TBottom
              | Some (TInv _) | None ->
                  let resolved =
                      match ctx.Certified fid with
                      | Some s -> Some (s, false)
                      | None -> ctx.Speculative fid |> Option.map (fun s -> (s, true))
                  match resolved with
                  | Some (sg, speculative) ->
                      let applies =
                          sg.Group = ctx.Group
                          && List.length sg.Params = List.length args
                          && (List.zip (sg.Params |> List.map snd) argSts
                              |> List.forall (fun (pSt, aSt) ->
                                  match pSt, aSt with
                                  | TRep sp, TRep sa -> sp = sa
                                  | TInv _, TInv _ -> true
                                  | _ -> false))
                      if applies then
                          if speculative then ctx.DepHits.Add fid |> ignore
                          sg.Return
                      // THE ONE MODE-SENSITIVE RULE (phase C1). The fall-through
                      // below is the documented divergence from the seam, whose
                      // checker refuses a cross-group CERTIFIED call in BOTH
                      // directions even on invariant arguments. In DEDUCTION
                      // that extra recall is the point. In CHECKING it must not
                      // produce a definite status: the whole purpose of that
                      // mode is to agree with the seam, and a status derived
                      // through a rule the seam does not have is exactly the
                      // shape of a FALSE compiler-bug report. TOpaque here means
                      // the validation abstains, which is always safe.
                      elif ctx.Checking then TOpaque
                      else allInvRule ()
                  | None ->
                      // Uncertified callee (builtin, plain helper, array read
                      // through application syntax). SOUNDNESS: a function of
                      // invariants is invariant — the same inputs in every
                      // frame give the same output in every frame. A rep
                      // argument would ESCAPE into a body that carries no
                      // certificate saying what happens to it: decline. An
                      // unclassifiable argument proves nothing either.
                      allInvRule ())
         | _ ->
             // Computed callee: admissible only when nothing rep-typed is in
             // play at all.
             if isInvT (j f) && argSts |> List.forall isInvT
             then TInv TInvShapeUnknown
             else TBottom)

    // --- former application: THE post-elaboration arithmetic rule ----------
    //
    // THIS IS THE ARM THE MOVE TO TYPECHECK MAKES NECESSARY. At the seam,
    // `x + y` on two arrays is an `ExprBinOp` and the Rep ± Rep rule fires
    // directly. By typecheck it has ALREADY been desugared into a former
    // application — `method_for(x, y) <@> lambda(a, b) -> a + b` — so without
    // this arm the entire arithmetic fragment of the discipline is invisible
    // and the typed lattice deduces essentially nothing on arrays.
    //
    // The rule: bind the kernel's parameters to the statuses of the SOURCE
    // ARRAYS (not to "component" statuses) and walk the kernel body. That
    // abstraction — reading a per-element kernel as a whole-array operation —
    // is valid exactly when every step is COMPONENTWISE UNIFORM AND LINEAR,
    // which is why a `TRep` conclusion additionally requires the kernel body
    // to be inside `isElementwiseArith`. Within that fragment the only rules
    // that can produce TRep are Rep ± Rep (componentwise addition commutes
    // with a block-diagonal D) and scalar·Rep (a scalar commutes with every
    // block), both of which hold elementwise iff they hold on the whole array.
    //
    // SECOND GUARD, and the one only a TYPED walker can apply: the conclusion
    // must AGREE WITH THE NODE'S OWN TYPE. This settles for free two questions
    // the abstraction cannot answer on its own — whether the former zips or
    // CROSS-ITERATES (an outer product raises the rank, and a multi-index
    // array carrying an irreps axis classifies unclassifiable, so the guard
    // rejects it — the seam's `outerRep` rule, obtained from the type), and
    // whether the spec that comes out is the spec that went in.
    //
    // NOTE a deliberate divergence from the seam, reported to the coordinator:
    // MLEquiv's judgeFormerApply REJECTS every `loop <@> kernel` over a rep,
    // on the grounds that a former hands its kernel basis-dependent COMPONENTS.
    // That rejection is sound at the SURFACE, where an explicitly written
    // former is the only thing that shape can be; it is not available here,
    // because desugared `x + y` is byte-identical to a hand-written former by
    // the time this walker runs. The rule above is the mathematically correct
    // reading of both.
    | TExprApply info ->
        let srcSts = info.Arrays |> List.map j
        let anyRepSrc = srcSts |> List.exists isRepT
        if srcSts |> List.exists ((=) TBottom) then TBottom
        else
            (match info.Kernel.Kind with
             | TExprLambda lam when List.length lam.Params = List.length srcSts ->
                 // A kernel parameter inherits its SOURCE's status VERBATIM —
                 // emphatically NOT the shape of its own (element) type. The
                 // whole point of the whole-array reading is that a kernel
                 // parameter drawn from an invariant ARRAY is a different
                 // number at every position, so it may not scale a rep even
                 // though each individual element is 0-dimensional. Refining
                 // the bound shape to `TInvScalar` off the element type
                 // resurrects exactly the false certificate the seam's
                 // `nonScalarScale` arm exists to refuse (probe: `x * w` for an
                 // invariant `w` of the same extent scales each component of an
                 // irrep block independently, and a diagonal matrix with
                 // unequal entries does not commute with D^l).
                 let kEnv =
                     List.zip lam.Params srcSts
                     |> List.fold (fun m ((p: TypedParam), st) -> Map.add p.VarId st m) env
                 let kSt = statusOf ctx kEnv lam.Body
                 let outSt = classifyType ctx.Group ctx.Resolve expr.Type
                 (match kSt, outSt with
                  | TBottom, _ -> TBottom
                  | TRep a, TRep b when
                        a = b
                        && isElementwiseArith
                             (lam.Params |> List.map (fun p -> p.VarId) |> Set.ofList)
                             lam.Body -> TRep a
                  | TInv _, TInv sh -> TInv sh
                  | _ -> if anyRepSrc then TBottom else TOpaque)
             | _ -> if anyRepSrc then TBottom else TOpaque)

    // --- aggregates and virtual arrays ------------------------------------
    | TExprTuple es | TExprStack es | TExprZip es -> aggOf es
    | TExprArrayLit (es, _) -> aggOf es
    | TExprJoin (es, _) -> aggOf es

    // SOUNDNESS: virtual arrays ENUMERATE INDICES, and an index carries no rep
    // structure.
    | TExprRange _ | TExprReverse _ | TExprBlocked _ -> TInv (TInvAgg None)
    | TExprDotDot _ -> TInv (TInvAgg None)

    // `compute` is a scheduling boundary, not a value transform.
    | TExprCompute x -> j x

    // --- lambdas ----------------------------------------------------------
    // v1, and deliberately weaker than the seam's arm: a lambda body is not
    // walked (its parameters have no classified status, and `Captures` is the
    // only handle on what it closes over). With no rep in scope the closure is
    // an ordinary invariant helper; with a rep in scope it is TOpaque unless
    // it demonstrably captures one, in which case it declines. TOpaque here is
    // safe because the callee guard above refuses to call an TOpaque value.
    | TExprLambda info ->
        let envHasRep = env |> Map.exists (fun _ st -> isRepT st)
        if not envHasRep then TInv TInvShapeUnknown
        else
            let capturesRep =
                info.Captures
                |> List.exists (fun c -> match Map.tryFind c.VarId env with Some (TRep _) -> true | _ -> false)
            if capturesRep then TBottom else TOpaque

    // --- everything else --------------------------------------------------
    // Nothing established. TOpaque propagates and can never manufacture a Rep
    // claim: every rule above that PRODUCES a rep requires a rep INPUT, and
    // every rule that produces an invariant requires invariant inputs. The one
    // path by which an unmodelled node could have become a claim — flowing
    // into a call as the callee — is closed by the callee guard.
    | _ -> TOpaque

// ============================================================================
// Signature classification and the deduction driver
// ============================================================================

/// A parameter as the deduction needs it: surface name (for the rendered
/// signature), BINDER id (the walker's env key), and ZONKED type.
type RepParam = { PName: string; PId: IRId; PType: IRType }

/// Classify a whole signature under a group hypothesis. `None` when ANY
/// position is unclassifiable — the seam's `certSigOf -> Error` path, which
/// keeps Propose ⊆ Check-accept: a proposal the checker would refuse at the
/// signature is worse than no proposal.
let classifySignature (g: GroupT) (resolve: IRType -> IRType) (owner: string)
                      (parms: RepParam list) (retTy: IRType) : RepSigT option =
    let ps = parms |> List.map (fun p -> (p.PName, classifyType g resolve p.PType))
    let r = classifyType g resolve retTy
    if (ps |> List.exists (fun (_, s) -> s = TOpaque)) || r = TOpaque then None
    else Some { Owner = owner; Group = g; Params = ps; Return = r }

/// THE NON-VACUITY FILTER (the seam's, verbatim in intent): a signature with
/// nothing rep-typed proposes nothing. `equiv(G)` on a scalar helper is
/// vacuously true and says nothing about any group action, so proposing it
/// would be noise with a theorem's face on.
let private isVacuous (sg: RepSigT) : bool =
    not ((sg.Params |> List.exists (snd >> isRepT)) || isRepT sg.Return)

/// Record a CERTIFIED signature — one carrying a source-written
/// `where ml.equiv(G)` pin, or an elaborator stamp on a synthesized function
/// (plan A1). Both arrive as the same `__ml_equiv` conjunct, so this reads
/// conjuncts uniformly and gets stamped functions for free.
///
/// Trust, not proof, for the TABLE: the summary a caller borrows is the
/// DECLARED one, in phase C1 exactly as in phase B. What C1 adds is a SECOND
/// OPINION on the declaring body (`checkDeclaredRep`), which never changes what
/// this table hands out — the seam remains the checking authority (plan §4).
/// A pin whose signature does not classify is simply not recorded, so a caller
/// falls through to the uncertified-callee rule rather than borrowing a law
/// this module could not read.
let recordCertified (certified: System.Collections.Generic.Dictionary<IRId, RepSigT>)
                    (resolve: IRType -> IRType) (owner: string) (funcId: IRId)
                    (groupName: string) (parms: RepParam list) (retTy: IRType) : unit =
    let g = groupOfName groupName
    match classifySignature g resolve owner parms retTy with
    | Some sg -> certified.[funcId] <- sg
    | None -> ()

// ============================================================================
// PHASE C1 — declared-certificate VALIDATION
// ============================================================================
//
// The typed walker runs a SECOND, INDEPENDENT judgment of a theorem the seam
// checker has already accepted. It has no authority: it cannot reject a
// program the seam accepted, and it cannot accept one the seam rejected. Its
// only output is an agreement signal, and only DISAGREEMENT is surfaced —
// as an INTERNAL COMPILER ERROR, the LieGuardFailure posture. When two
// independent judgments of the same theorem contradict each other, that is a
// bug in the compiler, not in the user's program.
//
// ABSTENTION IS THE DEFAULT AT EVERY UNCERTAIN BOUNDARY. A false DISAGREE on a
// body the seam legitimately certified is the failure mode that matters here:
// it would turn a working program into a compiler-bug report. Silence is never
// disagreement.

/// The outcome of validating one declared certificate.
type CheckVerdict =
    /// The walker derived a status consistent with the declaration.
    | RepConfirm
    /// The walker declined to judge. NOT a disagreement — this is what keeps
    /// engine-discharged bodies (and every other modeling gap) safe until the
    /// C2 extractor lands. Carries a short reason, for the abstain census.
    | RepAbstain of reason: string
    /// The walker derived a DEFINITE status that contradicts the declaration.
    | RepDisagree of detail: string

// ----------------------------------------------------------------------------
// ENGINE HOOK SLOT (for the C2 agent's TypedExpr PolyExtract port)
// ----------------------------------------------------------------------------

/// What an external discharger may conclude about a body the composition
/// fragment could not judge.
type EngineVerdict =
    /// The body discharges: the declared certificate holds.
    | EngineConfirms
    /// The body provably does NOT satisfy the declared certificate. Since the
    /// seam already accepted it, this is a compiler-bug signal, and it surfaces
    /// exactly as a composition-derived disagreement does.
    | EngineRefutes of detail: string

/// The registered discharger. `None` from the hook means NOT APPLICABLE — the
/// body is outside the engine's fragment — and leaves the verdict at abstain.
type EngineHook = GroupT -> RepSigT -> TypedExpr -> EngineVerdict option

/// The slot itself, in the `Blade.Constraints.registerConstraint` shape: a
/// process-wide mutable holding at most one discharger. Empty until C2 fills
/// it, and while empty every body the composition fragment cannot judge
/// abstains — which is precisely the posture C1 ships with.
module EngineDischarge =
    let mutable private hook : EngineHook option = None
    let register (h: EngineHook) : unit = hook <- Some h
    let clear () : unit = hook <- None
    let isRegistered () : bool = hook.IsSome
    /// Total by construction: a second opinion may never crash a compiling
    /// program, so any escape from the discharger reads as "not applicable".
    let tryDischarge (g: GroupT) (sg: RepSigT) (body: TypedExpr) : EngineVerdict option =
        match hook with
        | None -> None
        | Some h -> (try h g sg body with _ -> None)

// ----------------------------------------------------------------------------
// The disagreement channel and the agreement census
// ----------------------------------------------------------------------------

/// Disagreements found this compilation, as (owner, detail, span). TypeCheck
/// drains this into the compile-error list so a contradiction stops the build
/// loudly. AsyncLocal, reset beside the proposal channel.
module RepCheckDisagreements =
    let private slot = new System.Threading.AsyncLocal<(string * string * Span) list>()
    let reset () = slot.Value <- []
    let add (owner: string) (detail: string) (span: Span) =
        slot.Value <- (owner, detail, span) :: slot.Value
    let get () : (string * string * Span) list =
        match box slot.Value with null -> [] | _ -> List.rev slot.Value

/// Confirm/abstain census for the compilation, so the agreement test block can
/// report the split. The abstain count is C2's shrinking target.
module RepCheckCensus =
    /// Elaborator-synthesized decls carry the generated-name prefix. Splitting
    /// the census on it is what makes the abstain number ACTIONABLE: a
    /// generated body is a CG loop nest, which is exactly the shape C2's
    /// TypedExpr PolyExtract port is built to judge, whereas a source-written
    /// abstention points at a gap in the composition fragment itself.
    let private isGenerated (owner: string) = owner.StartsWith "__ml_"
    let private confirms = new System.Threading.AsyncLocal<int>()
    let private abstains = new System.Threading.AsyncLocal<int>()
    let private genConfirms = new System.Threading.AsyncLocal<int>()
    let private genAbstains = new System.Threading.AsyncLocal<int>()
    let private reasons = new System.Threading.AsyncLocal<string list>()
    let reset () =
        confirms.Value <- 0
        abstains.Value <- 0
        genConfirms.Value <- 0
        genAbstains.Value <- 0
        reasons.Value <- []
    let recordConfirm (owner: string) =
        confirms.Value <- confirms.Value + 1
        if isGenerated owner then genConfirms.Value <- genConfirms.Value + 1
    let recordAbstain (owner: string) (reason: string) =
        abstains.Value <- abstains.Value + 1
        if isGenerated owner then genAbstains.Value <- genAbstains.Value + 1
        reasons.Value <- reason :: (match box reasons.Value with null -> [] | _ -> reasons.Value)
    let confirmed () : int = confirms.Value
    let abstained () : int = abstains.Value
    /// Of `confirmed ()` / `abstained ()`, how many were elaborator-generated.
    let generatedConfirmed () : int = genConfirms.Value
    let generatedAbstained () : int = genAbstains.Value
    /// Abstention reasons, most recent first — a histogram source for the
    /// report, not a stable ordering.
    let abstainReasons () : string list =
        match box reasons.Value with null -> [] | _ -> reasons.Value

/// Validate ONE declared certificate against the typed walker.
///
/// The declared group comes from the conjunct; the declared SIGNATURE is this
/// module's own classification of the zonked signature under that group — the
/// same classification `recordCertified` stores and every caller borrows, so
/// checking and the interprocedural table cannot drift apart.
///
/// SELF-REFERENCE IS ASSUMED, NOT PROVED, and that is correct here where it
/// would be wrong in deduction: validating a declared certificate is an
/// assume-guarantee obligation (assume the theorem, verify the body preserves
/// it), which is exactly what the seam's `judgeFunction` does by putting the
/// function's own cert in the table it judges against. Deduction must refuse
/// self-reference because there no theorem has been declared to assume.
///
/// DISAGREEMENT IS DELIBERATELY NARROW — see the report. Only a definite
/// `TRep` on both sides with DIFFERENT specs is reported. Every other mismatch
/// shape (definite-invariant against declared-rep, or the reverse) is reachable
/// through a modeling gap C2 is meant to close — component-assembled rep bodies,
/// engine discharge — so those abstain. A spec contradiction is the one shape no
/// known divergence can produce: every rule that yields `TRep s` requires a rep
/// input and preserves the spec exactly, and the former rule additionally
/// cross-checks its conclusion against the node's own type.
let checkDeclaredRep (certified: System.Collections.Generic.Dictionary<IRId, RepSigT>)
                     (resolve: IRType -> IRType)
                     (owner: string) (funcId: IRId) (groupName: string)
                     (parms: RepParam list) (retTy: IRType)
                     (body: TypedExpr) : CheckVerdict =
    try
        let g = groupOfName groupName
        match classifySignature g resolve owner parms retTy with
        | None -> RepAbstain "signature not classifiable by the typed classifier"
        | Some sg ->
            let ctx = {
                Group = g
                Resolve = resolve
                // The declaring function's OWN certificate is visible here
                // (recordCertified ran first), which is what makes the
                // assume-guarantee reading work for a recursive body.
                Certified =
                    (fun id -> match certified.TryGetValue id with | true, s -> Some s | _ -> None)
                // Checking consults no speculative summary: a declared
                // certificate must stand on pins and axioms, never on another
                // function's unwritten proposal.
                Speculative = (fun _ -> None)
                // No binder is "self" for the walk — see the assume-guarantee
                // note above. IRIds are non-negative, so this never matches.
                Self = System.Int32.MinValue
                DepHits = System.Collections.Generic.HashSet<IRId>()
                Checking = true
            }
            let bodySt = statusOf ctx (
                List.zip parms sg.Params
                |> List.fold (fun m (p, (_, st)) -> Map.add p.PId st m) Map.empty) body
            let engineFallback (why: string) =
                match EngineDischarge.tryDischarge g sg body with
                | Some EngineConfirms -> RepConfirm
                | Some (EngineRefutes d) -> RepDisagree d
                | None -> RepAbstain why
            match bodySt with
            | TBottom -> engineFallback "walker declined (outside the composition fragment)"
            | TOpaque -> engineFallback "nothing established for the body"
            | _ when statusAgreesT bodySt sg.Return -> RepConfirm
            | TRep bs ->
                (match sg.Return with
                 | TRep ds ->
                     RepDisagree (
                         sprintf "the typed walker derives %s for the body of '%s', but the declared certificate's return transforms as %s"
                             (repStrT bs) owner (repStrT ds))
                 // Derived rep against a declared invariant: reachable through a
                 // modeling gap, so abstain rather than accuse.
                 | _ -> RepAbstain "derived a representation where the declaration is invariant")
            | _ -> RepAbstain "derived an invariant where the declaration is a representation"
    with _ ->
        // Totality, in the seam's discipline: a second opinion may never turn a
        // compiling program into a crash.
        RepAbstain "validation raised"

/// The early-tier deduction for ONE function, at decl close, from ZONKED
/// signature types. Returns the proposal to publish, or None for silence.
///
/// Candidate ladder strongest-first, and only the STRONGEST passer is
/// proposed: pinning O3 is what the user would actually write, and an SO3
/// caller of an O3-pinned callee is a body the checker rejects, so recording
/// the weaker one would make the dependency closure dishonest.
let deduceFunctionRep (certified: System.Collections.Generic.Dictionary<IRId, RepSigT>)
                      (spec: RepSpecTable)
                      (resolve: IRType -> IRType)
                      (owner: string) (funcId: IRId)
                      (parms: RepParam list) (retTy: IRType)
                      (body: TypedExpr) : RepProposal option =
    let sigTys = (parms |> List.map (fun p -> p.PType)) @ [ retTy ]
    let candidates = candidatesFor resolve sigTys
    if candidates.IsEmpty then None
    elif sigTys |> List.exists (isUnresolvedTy resolve) then
        // EARLY TIER ONLY (plan §2's early/late split, v1 = early). A
        // signature still open at decl close is the late tier's business:
        // count it and stay silent. NOTE the ordering — the ladder ran FIRST,
        // so this counts only functions that DO carry a rep family somewhere
        // and were lost to polymorphism, not every generic helper in the file.
        SkippedPolymorphic.bump ()
        None
    else
        /// One candidate attempt: hypothesize the group, classify the ZONKED
        /// signature, and walk the body against it.
        let attempt (g: GroupT) : (string * RepSigT * System.Collections.Generic.HashSet<IRId>) option =
            match classifySignature g resolve owner parms retTy with
            | None -> None
            | Some sg when isVacuous sg -> None
            | Some sg ->
                let gs = groupStrT g
                let ctx = {
                    Group = g
                    Resolve = resolve
                    Certified =
                        (fun id -> match certified.TryGetValue id with | true, s -> Some s | _ -> None)
                    Speculative =
                        (fun id -> match spec.Sigs.TryGetValue ((gs, id)) with | true, s -> Some s | _ -> None)
                    Self = funcId
                    DepHits = System.Collections.Generic.HashSet<IRId>()
                    // Deduction, not validation: the C1 mode flag is off, so
                    // every rule behaves exactly as it did in phase B.
                    Checking = false
                }
                // Parameters enter the body under their classified statuses —
                // the hypothesis of the conditional theorem.
                let bodyEnv =
                    List.zip parms sg.Params
                    |> List.fold (fun m (p, (_, st)) -> Map.add p.PId st m) Map.empty
                let bodySt = statusOf ctx bodyEnv body
                // The certificate holds iff the body's law AGREES with the
                // return position's. TBottom never agrees (joinStatusT refuses
                // it), so a declined walk is silence.
                if statusAgreesT bodySt sg.Return then Some (gs, sg, ctx.DepHits) else None

        // TOTAL BY CONSTRUCTION, in the seam's discipline: a speculative second
        // opinion may never turn a compiling program into a crash, so any
        // escape (a length mismatch in a malformed kernel, anything a future
        // arm gets wrong) reads as "no proposal" rather than a compiler crash.
        let attempt g = try attempt g with _ -> None
        match candidates |> List.tryPick attempt with
        | None -> None
        | Some (gs, sg, hits) ->
            // The dependency closure: which SPECULATIVE pins this proposal
            // rests on. Direct deps are the speculative callees the walk
            // actually consumed; the closure adds each of those proposals' own
            // deps (already computed — decl order guarantees it). Rendered in
            // DECL order, not alphabetically: it reads as the order the pins
            // would be written in.
            let orderG =
                spec.Order |> Seq.filter (fun (g2, _, _) -> g2 = gs) |> Seq.toList
            let closure =
                hits
                |> Seq.collect (fun id ->
                    let self =
                        orderG |> List.tryPick (fun (_, i, n) -> if i = id then Some n else None)
                    let theirs =
                        match spec.Deps.TryGetValue ((gs, id)) with
                        | true, ds -> ds
                        | _ -> []
                    (Option.toList self) @ theirs)
                |> Seq.distinct
                |> Set.ofSeq
            let ordered = orderG |> List.map (fun (_, _, n) -> n) |> List.filter (fun n -> closure.Contains n)
            // Record the speculative summary so LATER declarations in this
            // same pass can rest on it — the interprocedural threading twin,
            // single-pass in decl order, no fixpoint (a forward call simply
            // resolves to nothing and the walk declines, which is silence,
            // which is correct).
            spec.Sigs.[(gs, funcId)] <- sg
            spec.Deps.[(gs, funcId)] <- ordered
            spec.Order.Add ((gs, funcId, owner))
            Some { Owner = owner; Group = gs; Signature = sigSummaryT sg; Deps = ordered }
