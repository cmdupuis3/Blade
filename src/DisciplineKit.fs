/// DESIGN SKELETON for docs/design-discipline-as-data.md — stage C3's
/// "discipline as data" question, answered in types.
///
/// NOTHING HERE IS WIRED IN. No pass calls it, no checker references it, no
/// gate moves. It exists to make the design's central claim FALSIFIABLE in
/// the compiler that will host it: that the three equivariance-family
/// disciplines (equiv / galilean / perm) differ in the VALUES of a fixed
/// record of rule functions, not in the SHAPE of the walker that consumes
/// them. If that record cannot be written in F# without contortion, the
/// abstraction is not real; this file is the check that it can.
///
/// The design doc is the argument; this is the type-level receipt for its
/// §4 (the record) and §5 (the instances). Read them together.
///
/// WHAT THIS FILE DELIBERATELY DOES NOT DO
///   * It does not implement any discipline's rules. The three instances are
///     sketched in the doc, not built here — building them would mean moving
///     behaviour, which C3 explicitly defers.
///   * It does not implement the full walker. It implements the STRUCTURAL
///     fragment (`structuralArm`), which is the part of the claim that needs
///     proving: that those arms can be written once, generically, with no
///     mention of any discipline's payload. The rule arms are the part
///     nobody disputes is per-discipline.
///
/// Compile order: immediately after DeduceRep, before StaticEval — same
/// dependency set as DeduceRep (Ast/Types/IR/TypedAst), nothing upward.
module Blade.DisciplineKit

open Blade.Ast
open Blade.Types
open Blade.IR
open Blade.TypedAst

// ============================================================================
// 1. THE GENERIC STATUS LATTICE
// ============================================================================
//
// The common shape of MLEquiv's `Rep|Inv|Opaque`, MLGalilean's
// `BVar|BInv|BOpaque` and MLPerm's `Pow k|PowUnsized|POpaque`, plus
// DeduceRep's fourth element `TBottom` (the seam encodes it as `Error`).
//
//   SCov p  — the value MOVES under the action, in the manner recorded by the
//             discipline's payload `p`. The only status carrying a theorem.
//   SFix r  — the value is HELD FIXED. `r` is the discipline's refinement of
//             fixedness, load-bearing only where a rule asks for more than
//             "fixed" (equiv: provable 0-dimensionality, for the scaling
//             rule; perm: provable extent, for the broadcast rule; galilean:
//             nothing, so `unit`).
//   SOpaque — nothing established. Propagates, never manufactures a claim.
//   SBottom — the walker DECLINES. Deduction reads it as silence; checking
//             reads it as "abstain".
//
// PERM'S RE-ENCODING, and why it is faithful: MLPerm spells the invariant as
// `Pow 0`, so its covariant and fixed cases share a constructor. Under this
// shape it becomes `SCov k` for k >= 1 and `SFix Sized` for k = 0, with
// `PowUnsized` as `SFix Unsized`. Every MLPerm arm that matches `Pow 0` means
// "invariant" and every arm that matches `Pow k when k > 0` means "covariant"
// — the split is already there in the source, only spelled inside one
// constructor.
type Status<'Cov, 'Fix> =
    | SCov of 'Cov
    | SFix of 'Fix
    | SOpaque
    | SBottom

let isCov (s: Status<'C, 'F>) = match s with SCov _ -> true | _ -> false
let isFix (s: Status<'C, 'F>) = match s with SFix _ -> true | _ -> false

// ============================================================================
// 2. THE LATTICE ALGEBRA A DISCIPLINE MUST SUPPLY
// ============================================================================
//
// Two operations, because the generic walker's control-flow arms need to
// merge statuses and cannot know how two payloads combine.
//
//   JoinCov — two covariant statuses reached on different control-flow paths.
//             `None` = they do not agree, which the walker turns into SBottom.
//             equiv: spec equality. perm: rank equality. galilean: trivially
//             `Some ()`, since its payload is unit.
//   MeetFix — two fixed refinements reached on different paths. Total: the
//             discipline always has a weakest refinement to fall back on.
type LatticeOps<'Cov, 'Fix> = {
    JoinCov: 'Cov -> 'Cov -> 'Cov option
    MeetFix: 'Fix -> 'Fix -> 'Fix
    /// The weakest refinement — "fixed, nothing more established". Bound at
    /// every position where the walker must produce a fixed status without
    /// evidence about its shape (pattern bindings, field reads, tuple reads).
    FixTop: 'Fix
}

/// Merge two statuses reached on different control-flow paths. GENERIC: this
/// is DeduceRep.joinStatusT with the two payload comparisons lifted out, and
/// it is the same function MLGalilean inlines as `if st = sf` and MLPerm
/// inlines as `rest |> List.forall ((=) s)`.
let joinStatus (ops: LatticeOps<'C, 'F>) (a: Status<'C, 'F>) (b: Status<'C, 'F>)
    : Status<'C, 'F> option =
    match a, b with
    | SBottom, _ | _, SBottom -> None
    | SCov x, SCov y -> ops.JoinCov x y |> Option.map SCov
    | SFix x, SFix y -> Some (SFix (ops.MeetFix x y))
    | SOpaque, SOpaque -> Some SOpaque
    | _ -> None

let statusAgrees (ops: LatticeOps<'C, 'F>) a b = (joinStatus ops a b) |> Option.isSome

// ============================================================================
// 3. SIGNATURES AND PARAMETERS
// ============================================================================

/// A parameter as a discipline needs it: surface name (for rendering), BINDER
/// id (the walker's env key — the FuncSignParities discipline, so a shadowing
/// local cannot borrow a function's law), and ZONKED type.
type DParam = { PName: string; PId: IRId; PType: IRType }

/// A classified signature under one hypothesis. Generic over the hypothesis
/// so equiv can carry a group, perm an extent N, and galilean a velocity set.
///
/// `Return` IS PRESENT even though MLGalilean's `GalSig` has no return field:
/// galilean's v1 rule is that a certified function returns boost-invariant,
/// which is `SFix` — a fixed VALUE of this field, not a missing field. Making
/// it explicit is what lets one engine compare body-status against
/// return-status for all three.
type DSig<'Hyp, 'Cov, 'Fix> = {
    Owner: string
    Hyp: 'Hyp
    Params: (string * Status<'Cov, 'Fix>) list
    Return: Status<'Cov, 'Fix>
}

// ============================================================================
// 4. THE RULE TABLE — the polarity table, as data
// ============================================================================
//
// Every field here is a place where the three disciplines are MEASURED to
// disagree (design doc §3). The signatures are shared; the values are not.
// A discipline that wanted to share a value with another would just pass the
// same function.
type Rules<'Cov, 'Fix> = {
    /// A literal scalar. equiv/galilean/perm all answer "fixed", but at
    /// different refinements, and perm's literal AGGREGATE arm additionally
    /// consults the extent — hence `Aggregate` is separate.
    Literal: unit -> Status<'Cov, 'Fix>

    /// THE polarity arm. `Rep + Rep` is legal for equiv, a REJECT for
    /// galilean (it doubles the U0 coefficient), and legal for perm.
    /// `Rep * Rep` is a reject for equiv and legal for perm. No two of the
    /// three agree on this function.
    BinOp: BinOpMode -> BinOp -> Status<'Cov, 'Fix> -> Status<'Cov, 'Fix> -> Status<'Cov, 'Fix>

    /// Negation is the sharpest single disagreement: equiv PRESERVES a
    /// covariant status (-I commutes with every D), galilean REJECTS it
    /// (it flips the coefficient to -1), perm PRESERVES it (pointwise).
    UnaryOp: UnaryOp -> Status<'Cov, 'Fix> -> Status<'Cov, 'Fix>

    /// Reading a component. equiv refuses except at a static offset inside a
    /// trivial block; galilean ADMITS it and hands back a covariant element;
    /// perm refuses in v1 though the mathematics permits it. The `int option
    /// list` carries statically-known offsets, which is the only extra
    /// information any of the three asks for.
    IndexRead: Status<'Cov, 'Fix> -> int option list -> Status<'Cov, 'Fix>

    /// Folding. All three refuse a covariant source, for three different
    /// reasons, and perm additionally refuses an unsized one.
    Reduce: Status<'Cov, 'Fix> -> Status<'Cov, 'Fix> -> Status<'Cov, 'Fix>

    /// Packing values into a tuple/array literal/stack. equiv declines on any
    /// covariant element; galilean ACCEPTS a uniformly covariant aggregate;
    /// perm declines and additionally tests the cell count.
    Aggregate: Status<'Cov, 'Fix> list -> Status<'Cov, 'Fix>

    /// A virtual array (range/reverse/blocked). Fixed for equiv and galilean;
    /// for perm this is a RULE, not a constant — `range<Idx<N>>` IS the node
    /// index set and is fixed by no relabelling but the identity.
    Virtual: IRType -> Status<'Cov, 'Fix>

    /// The conclusion guard of a former application, after the kernel body
    /// has been walked. Arguments: the kernel's derived status, the status
    /// classified from the RESULT NODE'S OWN TYPE, and whether the kernel
    /// body is inside the componentwise-uniform-linear fragment. equiv needs
    /// all three; galilean and perm need only the first.
    FormerConclusion:
        Status<'Cov, 'Fix> -> Status<'Cov, 'Fix> -> bool -> Status<'Cov, 'Fix>
}

// ============================================================================
// 5. MODE — how a hypothesis is seeded
// ============================================================================

/// Which passing hypotheses become proposals.
///
/// equiv takes the FIRST passer of a strongest-first ladder (O3 before SO3):
/// they are competing strengths and the weaker one would make the dependency
/// closure dishonest. galilean proposes EVERY passer: `galilean(u)` and
/// `galilean(v)` are independent true claims and suppressing either would
/// hide a theorem. That difference is one field, not one engine each.
type Selection =
    | FirstPasser
    | EveryPasser

/// How the hypothesis space is generated for one signature. This is plan
/// §D2's "mode", made concrete against three witnesses rather than two.
///
///   SignatureSeeded — the hypotheses are read off the TYPES (equiv's
///     candidatesFor: an IrrepsIdx axis seeds [O3; SO3]). An empty list is
///     the non-vacuity gate: no rep family, no candidates, silence.
///   ClauseSeeded — the hypothesis is NOT recoverable from the signature and
///     must be searched or supplied. galilean searches parameter subsets;
///     perm cannot search at all (see the doc's §3.2 on N-ambiguity), so its
///     generator returns [] and it checks only what is pinned.
///
/// Both are the SAME function type. The distinction is documentary, which is
/// the point: a mode is a value, not a code path.
type HypothesisMode<'Hyp> =
    | SignatureSeeded of ((IRType -> IRType) -> DParam list -> IRType -> 'Hyp list)
    | ClauseSeeded of ((IRType -> IRType) -> DParam list -> IRType -> 'Hyp list)

let hypothesesOf (m: HypothesisMode<'H>) =
    match m with
    | SignatureSeeded f -> f
    | ClauseSeeded f -> f

// ============================================================================
// 6. THE DISCIPLINE RECORD
// ============================================================================

/// One discipline, entirely as data.
///
/// THE ONE SHAPE CHANGE FROM PLAN §2, and it is forced by galilean:
/// `ClassifySig` classifies a WHOLE SIGNATURE, not one type. §2 specifies
/// "Classifier: IRType -> status", which is right for equiv (read the irreps
/// tag) and for perm (read the extent), and IMPOSSIBLE for galilean, whose
/// boost-variance is not a property of any type — a velocity and a velocity
/// DIFFERENCE have the same type, deliberately (MLGalilean's header: "units
/// track dimension, not frame behavior"). Galilean classifies by consulting
/// its hypothesis, which names parameters positionally. Lifting the
/// classifier to the signature admits all three; keeping it at the type
/// admits two.
type Discipline<'Hyp, 'Cov, 'Fix> = {
    // --- claim vocabulary ---------------------------------------------------
    /// The normalized where-conjunct: "__ml_equiv" | "__ml_galilean" |
    /// "__ml_perm_equiv". Already uniform across the three (MLCertShell's
    /// `conjunctsOf` reads all of them).
    ConjunctName: string
    /// The suggestion code this discipline proposes under: BL4011 / BL4014 /
    /// (perm has none today — see the doc's staged plan).
    SuggestCode: string
    /// The `deduced[]` discriminator: "equiv" | "galilean" | "perm".
    FactKind: string
    /// Render a hypothesis as it appears in a pin and in `deduced[].name`.
    RenderHyp: 'Hyp -> string
    /// Read a hypothesis back from a conjunct's arguments. `None` = malformed,
    /// which the caller reads as "not this discipline's business".
    ParseHyp: string list -> 'Hyp option

    // --- lattice ------------------------------------------------------------
    Lattice: LatticeOps<'Cov, 'Fix>

    // --- classification -----------------------------------------------------
    /// Classify a signature under a hypothesis. `None` when any position is
    /// unclassifiable — the `certSigOf -> Error` path that keeps
    /// Propose subset-of Check-accept.
    ClassifySig:
        'Hyp -> (IRType -> IRType) -> string -> DParam list -> IRType
            -> DSig<'Hyp, 'Cov, 'Fix> option
    /// Classify one type, for the arms that need a node's own type (the
    /// former-conclusion guard, virtual arrays, free-variable shapes).
    ClassifyType: 'Hyp -> (IRType -> IRType) -> IRType -> Status<'Cov, 'Fix>
    /// A signature with nothing covariant proposes nothing: `equiv(G)` on a
    /// scalar helper is vacuously true and would be noise with a theorem's
    /// face on.
    IsVacuous: DSig<'Hyp, 'Cov, 'Fix> -> bool

    // --- mode ---------------------------------------------------------------
    Mode: HypothesisMode<'Hyp>
    Select: Selection

    // --- rules --------------------------------------------------------------
    Rules: Rules<'Cov, 'Fix>
}

// ============================================================================
// 7. THE WALKER CONTEXT
// ============================================================================

/// The hypothesis environment, generic over the discipline. Structurally
/// DeduceRep's `RepCtx` with the group replaced by an arbitrary hypothesis.
type DCtx<'Hyp, 'Cov, 'Fix> = {
    Disc: Discipline<'Hyp, 'Cov, 'Fix>
    Hyp: 'Hyp
    Resolve: IRType -> IRType
    /// Pinned or elaborator-stamped callee summaries.
    Certified: IRId -> DSig<'Hyp, 'Cov, 'Fix> option
    /// This pass's speculative summaries under THIS hypothesis.
    Speculative: IRId -> DSig<'Hyp, 'Cov, 'Fix> option
    /// The function being judged. In DEDUCTION no summary proves itself; in
    /// CHECKING self-reference is assumed (assume-guarantee) and this is set
    /// to a sentinel no binder matches.
    Self: IRId
    DepHits: System.Collections.Generic.HashSet<IRId>
    /// Checking mode: refuse a DEFINITE status at any rule knowingly more
    /// permissive than the incumbent checker, so a documented divergence can
    /// never be reported as a compiler bug.
    Checking: bool
}

// ============================================================================
// 8. THE STRUCTURAL FRAGMENT — the claim, proved
// ============================================================================
//
// These arms are written ONCE, generically, mentioning no discipline's
// payload. They are the arms all three seam checkers implement identically up
// to their own spelling of "fixed" — MEASURED by reading MLEquiv.judge,
// MLGalilean.judge and MLPerm.judge side by side (design doc §3.1).
//
// `structuralArm` returns `None` for the node kinds the RULES own. That
// `None` is the abstraction boundary, stated in one place and checkable by
// reading one function.

/// Does the interprocedural summary `sg` apply to a call with these argument
/// statuses? GENERIC: hypothesis equality, arity, then positional agreement.
/// All three checkers implement exactly this, with `=` on their own statuses.
let sigApplies (ops: LatticeOps<'C, 'F>) (hypEq: 'H -> 'H -> bool)
               (ctxHyp: 'H) (sg: DSig<'H, 'C, 'F>) (argSts: Status<'C, 'F> list) : bool =
    hypEq sg.Hyp ctxHyp
    && List.length sg.Params = List.length argSts
    && (List.zip (sg.Params |> List.map snd) argSts
        |> List.forall (fun (p, a) ->
            match p, a with
            | SCov x, SCov y -> (ops.JoinCov x y) |> Option.isSome
            | SFix _, SFix _ -> true
            | _ -> false))

/// The structural arms. `judge` is the caller's recursive walk (tied back by
/// the full engine); `None` means "this node kind belongs to the rules".
///
/// NOTE the `bindAt` parameter: every discipline binds pattern variables at
/// its own weakest FIXED status (MLCertShell.bindPatternVars already takes
/// exactly this, one abstraction level down — `Inv` / `BInv` / `Pow 0`).
/// Here it is `SFix ops.FixTop`, computed rather than passed, which is the
/// small generalization the shell could not make because it had no lattice.
let structuralArm
        (ctx: DCtx<'H, 'C, 'F>)
        (hypEq: 'H -> 'H -> bool)
        (judge: Map<IRId, Status<'C, 'F>> -> TypedExpr -> Status<'C, 'F>)
        (env: Map<IRId, Status<'C, 'F>>)
        (expr: TypedExpr)
    : Status<'C, 'F> option =

    let ops = ctx.Disc.Lattice
    let j = judge env
    let fixTop = SFix ops.FixTop

    match expr.Kind with

    // --- variables ---------------------------------------------------------
    // No summary proves itself (deduction); a bound variable carries its
    // status; a free one is fixed by the conditional-theorem reading — a
    // module-level constant is the same value in every frame.
    | TExprVar (_, vid, _) when vid = ctx.Self -> Some SBottom
    | TExprVar (_, vid, _) ->
        Some (match Map.tryFind vid env with
              | Some st -> st
              | None -> ctx.Disc.ClassifyType ctx.Hyp ctx.Resolve expr.Type |> function
                        | SCov _ -> fixTop   // a fixed buffer does not move
                        | s -> s)

    // --- control flow ------------------------------------------------------
    // If the condition is FIXED the same branch is taken in every frame, so
    // the result's law is the branches' common law. A condition that MOVES
    // selects different branches in different frames and proves nothing.
    // Identical in all three checkers.
    | TExprIf (c, t, f) ->
        Some (match j c with
              | SFix _ -> (match joinStatus ops (j t) (j f) with Some s -> s | None -> SBottom)
              | _ -> SBottom)

    // The same rule, n-ary. Pattern-bound variables enter fixed at the
    // weakest refinement: destructuring a moving value exposes components,
    // which every discipline refuses (equiv: basis-dependent; galilean:
    // "bind it whole"; perm: "bind it whole").
    | TExprMatch (scrut, cases) ->
        Some (match j scrut with
              | SFix _ ->
                  let armSts =
                      cases |> List.map (fun c ->
                          let env' =
                              c.Pattern.Bindings
                              |> List.fold (fun m (_, vid, _) -> Map.add vid fixTop m) env
                          judge env' c.Body)
                  (match armSts with
                   | [] -> Some fixTop
                   | s :: rest ->
                       rest |> List.fold (fun acc s2 -> acc |> Option.bind (fun a -> joinStatus ops a s2)) (Some s))
                  |> Option.defaultValue SBottom
              | _ -> SBottom)

    // --- binding -----------------------------------------------------------
    | TExprLet (_, vid, value, body) ->
        Some (match j value with
              | SBottom -> SBottom
              | sv -> judge (Map.add vid sv env) body)

    | TExprSequence es ->
        let sts = es |> List.map j
        Some (if sts |> List.exists ((=) SBottom) then SBottom
              else match List.tryLast sts with Some s -> s | None -> fixTop)

    // --- static selectors --------------------------------------------------
    // A field name is a static selector, so the base alone decides.
    | TExprField (baseE, _, _) ->
        Some (match j baseE with
              | SFix _ -> fixTop
              | SBottom -> SBottom
              | _ -> SOpaque)

    | TExprTupleIndex (baseE, idxE) ->
        Some (match j baseE, j idxE with
              | SFix _, SFix _ -> fixTop
              | SBottom, _ | _, SBottom -> SBottom
              | _ -> SOpaque)

    // `compute` is a scheduling boundary, not a value transform.
    | TExprCompute x -> Some (j x)

    // --- closures ----------------------------------------------------------
    // With nothing moving in scope a closure is an ordinary fixed helper;
    // capturing a moving value declines. All three checkers do exactly this
    // (they differ only in reaching for `freeVars` vs `Captures`).
    | TExprLambda info ->
        let envHasCov = env |> Map.exists (fun _ st -> isCov st)
        Some (if not envHasCov then fixTop
              else
                  let capturesCov =
                      info.Captures
                      |> List.exists (fun c -> match Map.tryFind c.VarId env with Some (SCov _) -> true | _ -> false)
                  if capturesCov then SBottom else SOpaque)

    // --- calls -------------------------------------------------------------
    // THE INTERPROCEDURAL RULE, and the longest arm in DeduceRep (104 lines).
    // It mentions no payload at all: certified table, then speculative table,
    // then the all-fixed fall-through. Its soundness argument is
    // discipline-independent — when every argument is provably FIXED, nothing
    // flowing in moves, and a deterministic map of fixed inputs is fixed in
    // every frame, whatever the action is.
    | TExprApp (f, args) ->
        let argSts = args |> List.map j
        let allFixedRule () =
            if argSts |> List.forall isFix
            then ctx.Disc.ClassifyType ctx.Hyp ctx.Resolve expr.Type |> function
                 | SCov _ -> fixTop
                 | s -> s
            else SBottom
        Some (match f.Kind with
              | TExprVar (_, fid, _) when fid = ctx.Self -> SBottom
              | TExprVar (_, fid, _) ->
                  (match Map.tryFind fid env with
                   // Application syntax over a moving binding is a component
                   // read: hand it to the rules, which is what makes this a
                   // rules call rather than a structural verdict. (This is the
                   // arm where galilean and equiv part company — galilean
                   // returns a moving element, equiv declines.)
                   | Some (SCov p) ->
                       ctx.Disc.Rules.IndexRead (SCov p) (args |> List.map (fun _ -> None))
                   // A callee whose own status is unknown or declined cannot
                   // be taken for a fixed function. LOAD-BEARING: without it a
                   // value produced by an unmodelled node would take the
                   // uncertified path and hand back a FIXED status.
                   | Some SOpaque | Some SBottom -> SBottom
                   | Some (SFix _) | None ->
                       let resolved =
                           match ctx.Certified fid with
                           | Some s -> Some (s, false)
                           | None -> ctx.Speculative fid |> Option.map (fun s -> (s, true))
                       match resolved with
                       | Some (sg, speculative) ->
                           if sigApplies ops hypEq ctx.Hyp sg argSts then
                               (if speculative then ctx.DepHits.Add fid |> ignore)
                               sg.Return
                           elif ctx.Checking then SOpaque
                           else allFixedRule ()
                       | None -> allFixedRule ())
              | _ ->
                  if isFix (j f) && argSts |> List.forall isFix then fixTop else SBottom)

    // --- everything else belongs to the RULES ------------------------------
    // Literals, arithmetic, unary ops, indexing, reduction, aggregates,
    // virtual arrays, former application. Eight families; the design doc's
    // §3 tabulates how the three disciplines disagree at every one of them.
    | _ -> None
