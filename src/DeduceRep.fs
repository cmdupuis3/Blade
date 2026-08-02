/// Phase B of the retired equivariance-in-types plan: the typecheck-resident
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

/// WHY a walk declined, and where.
///
/// `TBottom` itself stays a payload-free lattice element ON PURPOSE. It is
/// compared by structural equality at a dozen sites, it is the value
/// `DisciplineKit.StatusOps.Bottom` hands to a walker shared with two other
/// disciplines, and deduction treats every decline as the same silence — so
/// putting a cause inside the constructor would ripple through the kit and
/// through two disciplines that do not want it, to no benefit for the thing
/// the lattice is FOR.
///
/// What was actually missing is narrower: the walker HAS ALREADY DECIDED at
/// each origination site, and the reason was going in the bin. So the reason
/// rides beside the lattice, in a first-write-wins slot on `RepCtx` — one slot
/// per walk, written by the site that ORIGINATES a decline and never by the
/// arms that merely propagate one upward, so what comes out is the DEEPEST
/// reason and not the shallowest.
///
/// THIS IS ANALYSIS, NOT CHECKING. Nothing reads this slot to decide a
/// verdict: it refines the abstention census's reason strings and is available
/// to tooling that wants to say why a function was not proposed. Every
/// accept/reject in the compiler is byte-for-byte what it was without it.
type DeclineCause = {
    /// One sentence, in the walker's own vocabulary. Not the seam's message —
    /// checking stays at the seam (retired rejection-parity census §7), and
    /// pretending otherwise would invite the two to be compared as if they
    /// were the same text.
    Why: string
    /// The offending sub-expression. `None` for the two rule sites the generic
    /// kit calls without one (`CovAppliedAsCallee`, `FormerConclusion`);
    /// widening the kit's `StructRules` to carry a span would change a record
    /// three disciplines share, which is not worth a diagnostic nicety.
    Where: Span option
}

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
/// THE CLASSIFIER'S CORE, with the TOpaque arms' reasons attached: `Error why`
/// IS `TOpaque`, and `why` is the sentence the arm's soundness comment already
/// carried in prose.
///
/// The split exists because a `TOpaque` means two different things depending on
/// where it lands. In an EXPRESSION position it is an absence — "nothing
/// established" — and propagates harmlessly; `classifyType` below is that
/// reading and is what every expression-position caller uses. In a SIGNATURE
/// position it is a REFUSAL: it skips the whole function, at a specific
/// parameter, for a specific reason the classifier had in hand and threw away
/// (retired rejection-parity census §3 family D). `classifySignature` reads the
/// reason; nothing else does, and no verdict anywhere depends on it.
let rec classifyTypeR (g: GroupT) (resolve: IRType -> IRType) (ty: IRType)
    : Result<RepStatusT, string> =
    match resolve ty with
    | IRTScalar _ -> Ok (TInv TInvScalar)
    | IRTUnitAnnotated (inner, _) -> classifyTypeR g resolve inner
    | IRTIdxTagged (inner, _) -> classifyTypeR g resolve inner
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
            | _ :: _, _ ->
                Error (sprintf "it carries an O(3) IrrepsIdx axis, but the certificate names the point group %s; reading an O(3) module as a %s-module needs a restriction this checker does not have" pg pg)
            | [], [ (gn, _) ] when gn <> pg ->
                Error (sprintf "its PgIrrepsIdx axis names point group %s while the certificate names %s, and certificates do not transfer between groups — this checker knows each registered group's frozen table and no map between two of them" gn pg)
            | [], [ (_, entries) ] when n = 1 -> Ok (TRep (TPgSpecT (pg, entries)))
            | [], [] -> Ok (TInv (TInvAgg (Some n)))
            | _ ->
                Error (sprintf "it is a multi-index array mixing a PgIrrepsIdx axis with %d other axis/axes, which is outside the v1 fragment" (n - List.length pgs))
        | GO3 | GSO3 ->
            match irreps with
            // No irreps axis: a plain (or pg-tagged) buffer is invariant.
            | [] -> Ok (TInv (TInvAgg (Some n)))
            | [ triples ] when n = 1 -> Ok (TRep (TO3Spec triples))
            // Multi-index arrays mixing an irreps axis with others are
            // outside the v1 fragment, exactly as at the seam.
            | _ ->
                Error (sprintf "it is a %d-index array carrying %d IrrepsIdx axis/axes; only a single-axis irreps array is inside the v1 fragment" n (List.length irreps))
    // A named type (struct/sum) is invariant but of unestablished shape, so
    // it may never scale a rep. Mirror of the seam's `TyNamed` arm.
    | IRTNamed _ -> Ok (TInv TInvShapeUnknown)
    // Everything else — inference vars, arity-polymorphic packs, tuples,
    // function-typed params, loops, dists — is unclassifiable. TOpaque in a
    // SIGNATURE position skips the function (see `classifySignature`); it is
    // never a claim.
    | _ -> Error "its type is not one the classifier reads (an inference variable, an arity-polymorphic pack, a tuple, a function type, a loop or a dist)"

/// The expression-position reading: `classifyTypeR` with the reason dropped.
/// Byte-for-byte the classification it always was.
let classifyType (g: GroupT) (resolve: IRType -> IRType) (ty: IRType) : RepStatusT =
    match classifyTypeR g resolve ty with
    | Ok s -> s
    | Error _ -> TOpaque

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
    /// The FIRST decline this walk originated. See `DeclineCause`. Written only
    /// by `decline` below, read only after the walk, by nothing that decides a
    /// verdict. One cell per `RepCtx`, and a `RepCtx` is built fresh per
    /// (function, group hypothesis) attempt, so there is no cross-walk bleed.
    Decline: DeclineCause option ref
}

/// Answer `TBottom`, recording WHY if this walk has not already recorded a
/// deeper reason. FIRST WRITE WINS: the walk is a bottom-up fold, so the first
/// site to originate a decline is the innermost one, and the arms above it
/// propagate `TBottom` without calling this.
let private decline (ctx: RepCtx) (span: Span) (why: string) : RepStatusT =
    if (ctx.Decline.Value).IsNone then
        ctx.Decline.Value <- Some { Why = why; Where = Some span }
    TBottom

/// `decline` for the two rule sites the generic kit invokes without a span.
let private declineNoSpan (ctx: RepCtx) (why: string) : RepStatusT =
    if (ctx.Decline.Value).IsNone then
        ctx.Decline.Value <- Some { Why = why; Where = None }
    TBottom

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

// ----------------------------------------------------------------------------
// The kit instantiation: equiv's answers to the generic walker's questions
// ----------------------------------------------------------------------------
//
// `DisciplineKit.structuralArm` owns every arm whose soundness argument
// quantifies over ANY action (variables, control flow, binding forms, static
// selectors, closures, the interprocedural call rule, the former walk). It asks
// this module three kinds of question, and the three records below are the
// answers. Everything the kit does NOT own is `ruleArm`, further down — and
// those are exactly the arms whose justification names the action, which for
// this discipline is "the action is a block-diagonal LINEAR rep".

/// What the generic walker must be able to do to a `RepStatusT` without knowing
/// what one is. Built ONCE per walk (it closes over `ctx.Resolve` and
/// `ctx.Group`), not once per node.
let private repOps (ctx: RepCtx) : DisciplineKit.StatusOps<RepStatusT> = {
    Bottom = TBottom
    Opaque = TOpaque
    FixTop = TInv TInvShapeUnknown
    FixScalar = TInv TInvScalar
    IsCov = isRepT
    IsFix = isInvT
    IsBottom = (fun s -> s = TBottom)
    IsOpaque = (fun s -> s = TOpaque)
    Join = joinStatusT
    // NOT `joinStatusT >> Option.isSome`: that accepts TOpaque against TOpaque,
    // which is right for a control-flow merge and WRONG for a call argument —
    // an unclassifiable argument proves nothing and must not satisfy a
    // parameter. This is the seam's `applies` predicate, verbatim.
    ParamMatches =
        (fun pSt aSt ->
            match pSt, aSt with
            | TRep sp, TRep sa -> sp = sa
            | TInv _, TInv _ -> true
            | _ -> false)
    // The `nodeShape ()` of the pre-kit walker: invariant, with the shape read
    // off the node's own resolved type.
    FixOfType = (fun ty -> TInv (shapeOfType ctx.Resolve ty))
    ClassifyTy = (fun ty -> classifyType ctx.Group ctx.Resolve ty)
}

/// The two questions the kit's structural arms must ask the DISCIPLINE, because
/// their shape is shared and their verdict is not.
let private repStructRules (ctx: RepCtx) : DisciplineKit.StructRules<RepStatusT> = {
    // Application syntax over a rep-bound variable is a component read, same
    // verdict as TExprIndex: the components of an l>0 block are the
    // basis-dependent numbers this whole discipline exists to refuse. (Galilean
    // answers the opposite here — its elements are per-component boost-variant —
    // which is why this is a rule and not a structural verdict.)
    CovAppliedAsCallee =
        (fun _ ->
            declineNoSpan ctx
                "a representation-typed value is read at a component offset in application position, and the components of an l > 0 block are basis-dependent numbers")

    // The former-application conclusion. Reading a per-element kernel as a
    // whole-array operation is valid exactly when every step is COMPONENTWISE
    // UNIFORM AND LINEAR, which is why a `TRep` conclusion additionally requires
    // the kernel body to be inside `isElementwiseArith`. Within that fragment the
    // only rules that can produce TRep are Rep +/- Rep (componentwise addition
    // commutes with a block-diagonal D) and scalar*Rep (a scalar commutes with
    // every block), both of which hold elementwise iff they hold on the whole
    // array.
    //
    // SECOND GUARD, and the one only a TYPED walker can apply: the conclusion
    // must AGREE WITH THE NODE'S OWN TYPE (`outSt`). That settles for free two
    // questions the abstraction cannot answer on its own — whether the former
    // zips or CROSS-ITERATES (an outer product raises the rank, and a
    // multi-index array carrying an irreps axis classifies unclassifiable, so
    // the guard rejects it — the seam's `outerRep` rule, obtained from the
    // type), and whether the spec that comes out is the spec that went in.
    FormerConclusion =
        (fun kSt outSt elementwise anyRepSrc ->
            match kSt, outSt with
            | TBottom, _ -> TBottom
            | TRep a, TRep b when a = b && elementwise -> TRep a
            | TInv _, TInv sh -> TInv sh
            | _ ->
                if anyRepSrc then
                    declineNoSpan ctx
                        "a former reads a representation-typed source, but its kernel is not componentwise-uniform linear arithmetic whose conclusion matches the former's own type"
                else TOpaque)
}

/// Project a stored equiv signature into the shape the kit's call rule reads.
let private toCallSig (sg: RepSigT) : DisciplineKit.CallSig<GroupT, RepStatusT> =
    { CHyp = sg.Group; CParams = sg.Params |> List.map snd; CReturn = sg.Return }

let private repWalkCtx (ctx: RepCtx) : DisciplineKit.WalkCtx<GroupT, RepStatusT> = {
    Ops = repOps ctx
    Rules = repStructRules ctx
    Hyp = ctx.Group
    HypEq = (=)
    Certified = (fun id -> ctx.Certified id |> Option.map toCallSig)
    Speculative = (fun id -> ctx.Speculative id |> Option.map toCallSig)
    Self = ctx.Self
    DepHits = ctx.DepHits
    // Carried through UNCHANGED. The kit's call rule is the sole consumer, and
    // it is the sole reason this flag exists: in CHECKING mode the certified
    // callee's all-invariant fall-through must answer TOpaque (abstain) rather
    // than a definite status, because that fall-through is a rule the SEAM
    // checker does not have and a status derived through it would be a false
    // compiler-bug report in a mode whose entire purpose is agreeing with the
    // seam. In DEDUCTION the extra recall is the point.
    Checking = ctx.Checking
}

/// The walker: the kit's structural arms first, this discipline's rules second.
///
/// CURRIED ON PURPOSE — `repWalkCtx ctx` is built once per walk and closed over
/// by the recursive knot, rather than rebuilt at every node. Every call site
/// still reads `statusOf ctx env expr`.
///
/// THE PARTITION IS EXACT AND DISJOINT. `structuralArm` answers `Some` for
/// exactly: TExprVar (both arms), TExprIf, TExprMatch, TExprLet, TExprBlock,
/// TExprSequence, TExprAssign, TExprTupleIndex, TExprField, TExprCompute,
/// TExprLambda, TExprApp, TExprApply. `ruleArm` answers for exactly the rest:
/// literals, binary and unary arithmetic, whole-array negate/conjugate,
/// indexing, reduction, aggregate construction, virtual arrays, and the
/// catch-all. No node kind is handled by both, so first-match-wins semantics
/// are preserved from the single-match version this replaces.
let private statusOf (ctx: RepCtx) : Map<IRId, RepStatusT> -> TypedExpr -> RepStatusT =
    let wctx = repWalkCtx ctx
    let rec go (env: Map<IRId, RepStatusT>) (expr: TypedExpr) : RepStatusT =
        match DisciplineKit.structuralArm wctx go env expr with
        | Some s -> s
        | None -> ruleArm env expr

    /// Equiv's OWN rules — the arms whose soundness argument names the action.
    /// Every one of them would be wrong for at least one of the other two
    /// disciplines (retired discipline-as-data design note §3.2's polarity table).
    and ruleArm (env: Map<IRId, RepStatusT>) (expr: TypedExpr) : RepStatusT =
        let j = go env
        /// Shape read off the node's own (resolved) type — the typed win over
        /// the seam's syntactic shape guessing.
        let nodeShape () = shapeOfType ctx.Resolve expr.Type
        /// Decline HERE, recording why. Used only where this arm ORIGINATES a
        /// decline; an arm that merely forwards a sub-expression's `TBottom`
        /// writes plain `TBottom` so the deeper cause survives.
        let dcl (why: string) = decline ctx expr.Span why

        /// An aggregate constructor. SOUNDNESS: packing a rep into a literal
        /// aggregate loses its block structure — the aggregate does not
        /// transform as the rep — so a rep element declines. All-invariant
        /// elements make an invariant aggregate. (Galilean's aggregate rule is
        /// the OPPOSITE: a uniformly boost-variant aggregate stays variant.)
        let aggOf (es: TypedExpr list) =
            let sts = es |> List.map j
            if sts |> List.exists ((=) TBottom) then TBottom
            elif sts |> List.exists isRepT then
                dcl "a representation-typed value is packed into a literal aggregate, which loses its block structure — the aggregate does not transform as the representation"
            elif sts |> List.exists ((=) TOpaque) then TOpaque
            else TInv (TInvAgg None)

        match expr.Kind with

        // --- literals -----------------------------------------------------
        // SOUNDNESS: a constant does not move under any group action.
        | TExprLit _ -> TInv TInvScalar

        // --- arithmetic ---------------------------------------------------
        | TExprBinOp (mode, op, l, r) ->
            let sl = j l
            let sr = j r
            (match sl, sr, op with
             | TBottom, _, _ | _, TBottom, _ -> TBottom
             // SOUNDNESS: the outer-product form cross-iterates, so the result
             // has a HIGHER RANK than either operand and cannot be the rep it
             // was built from, whatever the operator.
             | (TRep _, _, _ | _, TRep _, _) when mode <> Elementwise ->
                 dcl "the outer-product form cross-iterates, so its result has a higher rank than either operand and cannot be the representation it was built from"
             // SOUNDNESS (Rep +/- Rep): the action is LINEAR, so D(x+y) = Dx +
             // Dy. Requires IDENTICAL specs — different specs are different D's
             // and the sum transforms under neither. THIS IS THE ARM THAT MAKES
             // THE RULES PER-DISCIPLINE: galilean rejects it outright (adding
             // two boost-variant values doubles the U0 coefficient).
             | TRep s1, TRep s2, (OpAdd | OpSub) ->
                 if s1 = s2 then TRep s1
                 else
                     dcl (sprintf "adding or subtracting two representations with DIFFERENT laws (%s and %s): the sum transforms under neither" (repStrT s1) (repStrT s2))
             // Elementwise product of two reps is the Clebsch-Gordan
             // contraction's job (ml.tensor_product), not a pointwise multiply:
             // decline. (Perm ADMITS this one — a permutation commutes with
             // every pointwise map.)
             | TRep _, TRep _, _ ->
                 dcl "an elementwise product of two representation-typed values is not equivariant — that contraction is the Clebsch-Gordan one, not a pointwise multiply"
             // SOUNDNESS (scalar scaling): a SCALAR commutes with every block of
             // the action, so D(cx) = cD(x). An invariant ARRAY of the same
             // extent scales each component independently — a diagonal matrix
             // with unequal entries does not commute with D^l — so scalarity
             // must be PROVEN; an unestablished shape declines.
             | TRep s, TInv TInvScalar, (OpMul | OpDiv) -> TRep s
             | TInv TInvScalar, TRep s, OpMul -> TRep s
             | (TRep _, TInv _, _) | (TInv _, TRep _, _) ->
                 dcl "only a provably SCALAR invariant may scale a representation-typed value under * or /, because a scalar is the only invariant that commutes with every block of the action"
             | TInv shl, TInv shr, _ ->
                 TInv (if mode = Elementwise then binShapeT shl shr else TInvShapeUnknown)
             // Nothing established on one side: nothing claimed for the result.
             | TOpaque, _, _ | _, TOpaque, _ -> TOpaque)

        // SOUNDNESS: only a LINEAR unary op transports a rep. Negation is the
        // linear map -I, which commutes with every D, so `-x` transforms exactly
        // as `x`. Everything else applied to a rep is refused.
        //
        // `OpMath` IS THE TRAP HERE, and it is a post-elaboration trap the seam
        // never faces: at the surface `exp(x)` is an `ExprApp` of a named
        // builtin, which MLEquiv routes through its nonlinearity rejection.
        // TypeCheck rewrites whitelisted math names into
        // `TExprUnaryOp (OpMath "exp", _)`, so a blanket "a unary op passes its
        // operand's status through" — which reads correct against the seam's
        // unary arm — silently CERTIFIES a nonlinearity applied to rep
        // components. (Probe: `method_for(x) <@> lambda(v) -> exp(v)` proposed
        // equivariant before this arm was split.) A nonlinearity acts only on
        // invariants; gate reps with ml.gated or extract invariants with
        // ml.scalars/ml.norms.
        //
        // THE SPLIT IS LOAD-BEARING AND MUST NOT BE COLLAPSED. Perm passes ALL
        // unary ops through (pointwise maps commute with relabelling) and
        // galilean rejects even negation (it flips the U0 coefficient to -1), so
        // this pair of arms is three different functions across the three
        // disciplines and belongs here rather than in the kit.
        | TExprUnaryOp (OpNeg, inner) -> j inner
        | TExprUnaryOp (_, inner) ->
            (match j inner with
             | TRep _ ->
                 dcl "a NONLINEAR unary operator is applied to a representation-typed value; only a linear map transports a representation (gate it with ml.gated, or extract invariants with ml.scalars / ml.norms)"
             | s -> s)

        // SOUNDNESS: whole-array negation is the linear map -I, which commutes
        // with every D.
        | TExprArrayNegate a -> j a

        // Complex conjugation does NOT commute with a complex representation in
        // general (it conjugates the matrix): decline on a rep, pass through
        // otherwise.
        | TExprArrayConjugate a ->
            (match j a with
             | TRep _ ->
                 dcl "complex conjugation of a representation-typed value conjugates the representation matrix, so it does not commute with the action in general"
             | s -> s)

        // --- reads --------------------------------------------------------
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
        // SO3, since a pseudoscalar is fixed by every proper rotation — is acted
        // on by the identity, so the cell at that offset holds the SAME number in
        // every frame. That is precisely what `TInv` asserts, and the result is a
        // single cell, hence `TInvScalar`.
        //
        // The offset must be a LITERAL this walker can see. A computed index
        // would have to be proven to land in a trivial block, and an index this
        // walker cannot evaluate could land anywhere: decline.
        //
        // TO3Spec ONLY, with the point-group deferral intact — see
        // `invariantOffsetsT`: which pg labels are trivial is registry data in
        // MLPointSpec, which is out of compile-order reach, and a drifted
        // trivial-label table is a FALSE INVARIANT.
        | TExprIndex (arr, idxs, _) ->
            (match j arr with
             | TBottom -> TBottom
             | TRep (TO3Spec spec) ->
                 (match idxs with
                  | [ i ] ->
                      (match DisciplineKit.staticIntOf i with
                       | Some k when Set.contains k (invariantOffsetsT ctx.Group spec) ->
                           TInv TInvScalar
                       | Some k ->
                           dcl (sprintf "raw indexing at offset %d reads a basis-dependent component of an l > 0 block of %s" k (repStrT (TO3Spec spec)))
                       | None ->
                           dcl "indexing a representation-typed value needs a LITERAL offset this walker can place inside a trivial block; a computed index could land anywhere")
                  | _ ->
                      dcl "only a single-offset read into a representation-typed value is modelled")
             // Point-group reps: see `invariantOffsetsT` — the trivial-LABEL
             // table is out of compile-order reach, so no offset is claimed.
             | TRep _ ->
                 dcl "raw indexing into a point-group representation: which labels are trivial is registry data this module cannot reach in compile order, so no offset is claimed invariant"
             | TOpaque -> TOpaque
             | TInv _ ->
                 if idxs |> List.forall (fun i -> isInvT (j i))
                 then TInv (nodeShape ())
                 else
                     dcl "an invariant aggregate is indexed by a selector that is not itself invariant")

        // --- reduction ----------------------------------------------------
        // SOUNDNESS (the polarity note): a fold over a rep sums BASIS-DEPENDENT
        // COMPONENTS. The sum of the components of an l>0 vector is not a
        // rotational invariant (the norm is), so a rep source declines rather
        // than being mis-certified invariant.
        | TExprReduce (src, _, init) ->
            let ss = j src
            let si = match init with Some i -> j i | None -> TInv TInvScalar
            (match ss, si with
             | TBottom, _ | _, TBottom -> TBottom
             | TRep _, _ | _, TRep _ ->
                 dcl "a reduction folds over the BASIS-DEPENDENT components of a representation; the sum of the components of an l > 0 value is not an invariant (the norm is)"
             | TInv _, TInv _ -> TInv TInvScalar
             | _ -> TOpaque)

        // --- aggregates and virtual arrays --------------------------------
        | TExprTuple es | TExprStack es | TExprZip es -> aggOf es
        | TExprArrayLit (es, _) -> aggOf es
        | TExprJoin (es, _) -> aggOf es

        // SOUNDNESS: virtual arrays ENUMERATE INDICES, and an index carries no
        // rep structure. (Perm cannot say this unconditionally — `range<Idx<N>>`
        // IS its node index set — which is why virtual arrays are a rule.)
        | TExprRange _ | TExprReverse _ | TExprBlocked _ -> TInv (TInvAgg None)
        | TExprDotDot _ -> TInv (TInvAgg None)

        // --- everything else ----------------------------------------------
        // Nothing established. TOpaque propagates and can never manufacture a
        // Rep claim: every rule above that PRODUCES a rep requires a rep INPUT,
        // and every rule that produces an invariant requires invariant inputs.
        // The one path by which an unmodelled node could have become a claim —
        // flowing into a call as the callee — is closed by the kit's callee
        // guard.
        | _ -> TOpaque

    go

// ============================================================================
// Signature classification and the deduction driver
// ============================================================================

/// A parameter as the deduction needs it: surface name (for the rendered
/// signature), BINDER id (the walker's env key), and ZONKED type.
type RepParam = { PName: string; PId: IRId; PType: IRType }

/// WHICH signature position refused, and why. The census's "cheapest close in
/// the whole survey": `classifySignature` already knew both and collapsed them
/// to a bare `None`.
type SigRefusal = {
    /// "parameter 'x'" or "the return type".
    Position: string
    /// `classifyTypeR`'s reason, phrased to follow the position: "…, because
    /// <Why>".
    Why: string
}

/// The rendered one-liner, for an abstain reason or a tooling tooltip.
let sigRefusalStr (r: SigRefusal) : string = sprintf "%s does not classify: %s" r.Position r.Why

/// Classify a whole signature under a group hypothesis. `Error` when ANY
/// position is unclassifiable — the seam's `certSigOf -> Error` path, which
/// keeps Propose ⊆ Check-accept: a proposal the checker would refuse at the
/// signature is worse than no proposal.
///
/// PARAMETERS BEFORE THE RETURN, first failure wins — the same order the
/// `||` short-circuit gave when this returned an option, so which position is
/// blamed is not a new decision.
let classifySignature (g: GroupT) (resolve: IRType -> IRType) (owner: string)
                      (parms: RepParam list) (retTy: IRType) : Result<RepSigT, SigRefusal> =
    let ps = parms |> List.map (fun p -> (p.PName, classifyTypeR g resolve p.PType))
    match ps |> List.tryPick (fun (n, r) -> match r with Error w -> Some (n, w) | Ok _ -> None) with
    | Some (n, w) -> Error { Position = sprintf "parameter '%s'" n; Why = w }
    | None ->
        match classifyTypeR g resolve retTy with
        | Error w -> Error { Position = "the return type"; Why = w }
        | Ok r ->
            Ok { Owner = owner
                 Group = g
                 Params = ps |> List.map (fun (n, r) -> (n, (match r with Ok s -> s | Error _ -> TOpaque)))
                 Return = r }

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
    | Ok sg -> certified.[funcId] <- sg
    | Error _ -> ()

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
///
/// The slot carries `resolve` and the PARAMETER LIST alongside the signature
/// because an extractor needs both and neither is recoverable from `RepSigT`:
/// `sg.Params` holds (name, status) pairs, but binding a rep parameter to a
/// polynomial's variable vector needs the parameter's BINDER ID and its type,
/// which only `RepParam` carries — and every type reaching the extractor has to
/// go through the same `resolve` the walker used, or the two disagree about
/// what a parameter is. `parms` is in `sg.Params` order, positionally.
///
/// The group is deliberately NOT a separate argument: it already rides in
/// `sg.Group`, and passing it twice would let a caller desynchronize the
/// hypothesis from the signature it is judging.
type EngineHook =
    (IRType -> IRType) -> RepParam list -> RepSigT -> TypedExpr -> EngineVerdict option

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
    ///
    /// ONE ESCAPE IS DELIBERATELY NOT SWALLOWED HERE, because it is converted
    /// before it ever reaches this wrapper: `LieDischarge.LieGuardFailure` is a
    /// compiler-bug assert, not a decoder escape, and the registered adapter
    /// catches it specifically and returns `EngineRefutes`. See the adapter in
    /// TypeCheck. Everything else — registry misses, spec-decoder throws — is a
    /// legitimate "the engine has nothing to say".
    let tryDischarge (resolve: IRType -> IRType) (parms: RepParam list)
                     (sg: RepSigT) (body: TypedExpr) : EngineVerdict option =
        match hook with
        | None -> None
        | Some h -> (try h resolve parms sg body with _ -> None)

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
        | Error e ->
            RepAbstain (sprintf "signature not classifiable by the typed classifier: %s" (sigRefusalStr e))
        | Ok sg ->
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
                Decline = ref None
            }
            let bodySt = statusOf ctx (
                List.zip parms sg.Params
                |> List.fold (fun m (p, (_, st)) -> Map.add p.PId st m) Map.empty) body
            let engineFallback (why: string) =
                match EngineDischarge.tryDischarge resolve parms sg body with
                | Some EngineConfirms -> RepConfirm
                | Some (EngineRefutes d) -> RepDisagree d
                | None -> RepAbstain why
            // A DEFINITE-BUT-MISMATCHED composition status is the shape this
            // module has always declared untrustworthy — "reachable through a
            // modeling gap C2 is meant to close". C2 has now landed, so those
            // arms consult it too, but they honour ONLY a discharge: an
            // `EngineRefutes` there stacks a distrusted composition verdict on
            // top of a refutation, which is not the clean single-source signal a
            // compiler-bug report needs. Where composition has NO opinion
            // (TBottom/TOpaque) the engine is the sole judge and its refutation
            // does stand alone — that is `engineFallback`.
            let engineUpgradeOnly (why: string) =
                match EngineDischarge.tryDischarge resolve parms sg body with
                | Some EngineConfirms -> RepConfirm
                | Some (EngineRefutes _) | None -> RepAbstain why
            // The decline's CAUSE, when a rule originated one. Nothing chooses
            // a verdict from this — the arm below is `engineFallback` either
            // way — it only makes the abstention census say which rule
            // declined instead of lumping every decline under one bucket.
            // Empty when the decline came out of the generic kit, which has no
            // cause channel (see `DeclineCause`).
            let declineReason () =
                match ctx.Decline.Value with
                | Some c -> sprintf "walker declined: %s" c.Why
                | None -> "walker declined (outside the composition fragment)"
            match bodySt with
            | TBottom -> engineFallback (declineReason ())
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
                 | _ -> engineUpgradeOnly "derived a representation where the declaration is invariant")
            | _ -> engineUpgradeOnly "derived an invariant where the declaration is a representation"
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
            | Error _ -> None
            | Ok sg when isVacuous sg -> None
            | Ok sg ->
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
                    Decline = ref None
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
                if statusAgreesT bodySt sg.Return then Some (gs, sg, ctx.DepHits)
                else
                    // COMPOSITION FIRST, ENGINE SECOND — the seam's flow (§7),
                    // preserved exactly: the engine is consulted only where the
                    // composition walk DECLINED, never to overturn a verdict
                    // composition already reached. It sits INSIDE `attempt`, so
                    // the strongest-first ladder still governs: an O3 engine
                    // discharge is found and proposed before SO3 is ever tried.
                    //
                    // A REFUTATION IS JUST A DECLINE HERE. Deduction proposes;
                    // it never rejects, so "the body is a polynomial and it is
                    // not equivariant" and "the engine has nothing to say" are
                    // the same silence. Only the CHECKING path, where a
                    // certificate has actually been declared, treats a
                    // refutation as the compiler-bug signal it is.
                    match EngineDischarge.tryDischarge resolve parms sg body with
                    | Some EngineConfirms -> Some (gs, sg, ctx.DepHits)
                    | Some (EngineRefutes _) | None -> None

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
