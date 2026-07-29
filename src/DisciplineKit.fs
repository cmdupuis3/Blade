/// The DISCIPLINE KIT — the generic half of an equivariance-family judgment.
///
/// Stage 0 of docs/design-discipline-as-data.md. That document's finding, in
/// one line: the WALKER abstracts across equiv / galilean / perm and the RULES
/// do not, because the three actions are different algebraic structures (a
/// linear rep, an affine shift, a permutation matrix) whose arithmetic rules
/// have opposite polarity at nearly every arm.
///
/// ----------------------------------------------------------------------------
/// THE CRITERION FOR WHAT MAY LIVE HERE
/// ----------------------------------------------------------------------------
/// A rule belongs in this file IF AND ONLY IF ITS SOUNDNESS ARGUMENT QUANTIFIES
/// OVER ANY ACTION — i.e. the justification never names what the group does to
/// a value, only whether the value MOVES or is HELD FIXED. Two worked examples,
/// both of them arms below:
///
///   * The call rule's all-fixed fall-through: "when every argument is provably
///     fixed, nothing flowing in moves, and a deterministic map of fixed inputs
///     gives the same output in every frame." No step of that mentions a
///     representation, a boost or a permutation. It is generic.
///   * The if rule: "if the condition is fixed, the same branch is taken in
///     every frame, so the result's law is the branches' common law." Likewise.
///
/// And the counter-example that must NOT move here, however tempting its shape:
/// `Cov + Cov`. Its justification is "the action is LINEAR, so D(x+y) = Dx +
/// Dy" — which names the action, is true for equiv and perm, and is FALSE for
/// galilean, where adding two boost-variant values doubles the U0 coefficient
/// and is a reject. A rule whose argument names the action is a per-discipline
/// rule, and A GUARD IS A RULE.
///
/// ----------------------------------------------------------------------------
/// WHY THE STATUS TYPE IS ABSTRACT RATHER THAN THIS FILE'S OWN DU
/// ----------------------------------------------------------------------------
/// DELIBERATE DECISION (C3 stage 0). Do not "simplify" this by making the
/// generic code operate on `Status<'Cov,'Fix>` directly and having each
/// discipline abbreviate its own status to it. That was the first design and it
/// was rejected for a SAFETY reason, not a taste one.
///
/// DeduceRep's `RepStatusT` is a real F# discriminated union, and every match
/// over it in DeduceRep / MLPolyExtractTyped / TypeCheck is checked for
/// EXHAUSTIVENESS by the compiler. Re-expressing it as an abbreviation of a
/// generic DU would force its constructors to become partial active patterns at
/// roughly two hundred call sites, which silently switches that exhaustiveness
/// checking OFF — in exactly the 445-line walker whose whole risk is an arm
/// being dropped without either gate noticing. The compiler's incompleteness
/// warning is the main thing standing between this refactor and a silently lost
/// rule, and the refactor's entire value proposition is that it provably
/// changes nothing. Abstracting over the status instead keeps every
/// discipline's DU intact, keeps its matches exhaustive, and costs one record.
///
/// That record is 12 fields. That number is the honest price of this
/// abstraction, and it is the same objection MLCertShell.fs raised when it
/// declined to share `judgeStmts` at the elaboration seam ("six moving parts to
/// share twenty-odd lines, which is a worse trade than the copy"). The
/// objection was right there and is wrong here only because the quantity
/// changed: at the seam the shared surface was ~25 lines; here it is ~250,
/// including the single most soundness-critical arm in the walker (the
/// interprocedural call rule, which has already drifted between copies once —
/// see MLPerm.fs's stage-5c drift catalog, where two of four findings were
/// false ACCEPTS).
///
/// Compile order: after TypedAst, before DeduceRep — Ast/Types/IR/TypedAst
/// only, nothing upward.
module Blade.DisciplineKit

open Blade.Ast
open Blade.Types
open Blade.IR
open Blade.TypedAst

// ============================================================================
// 1. GENERIC AST HELPERS
// ============================================================================
//
// These quantify over no action at all — they are facts about the syntax tree.

/// A provably compile-time integer index, or None. `compute` is a scheduling
/// boundary and is peeled; anything else — a variable, an arithmetic
/// expression, a folded static this walker cannot see — is NOT a literal, and
/// the caller declines.
let rec staticIntOf (e: TypedExpr) : int option =
    match e.Kind with
    | TExprLit (LitInt n) -> Some (int n)
    | TExprCompute inner -> staticIntOf inner
    | _ -> None

/// Does this subtree read any of `ids`? CONSERVATIVE BY DESIGN, in the
/// `Deduce.usesVar` discipline: a node kind not enumerated here answers TRUE.
/// The only consumer treats "mentions nothing" as a licence, so guessing FALSE
/// would be the unsound direction; guessing TRUE merely forfeits recall.
let rec mentionsAnyId (ids: Set<IRId>) (e: TypedExpr) : bool =
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
let rec isElementwiseArith (ps: Set<IRId>) (e: TypedExpr) : bool =
    if not (mentionsAnyId ps e) then true
    else
        match e.Kind with
        | TExprLit _ | TExprVar _ -> true
        | TExprBinOp (_, _, l, r) -> isElementwiseArith ps l && isElementwiseArith ps r
        | TExprUnaryOp (_, i) -> isElementwiseArith ps i
        | TExprCompute i -> isElementwiseArith ps i
        | _ -> false

// ============================================================================
// 2. THE OFFERED STATUS SHAPE (for future instances; equiv supplies its own)
// ============================================================================
//
// The common shape of MLEquiv's `Rep|Inv|Opaque`, MLGalilean's
// `BVar|BInv|BOpaque` and MLPerm's `Pow k|PowUnsized|POpaque`, plus the fourth
// element the seam encodes as `Error`.
//
//   SCov p  — the value MOVES under the action, in the manner recorded by `p`.
//   SFix r  — the value is HELD FIXED; `r` refines fixedness where a rule needs
//             more than "fixed" (equiv: provable 0-dimensionality, for the
//             scaling rule; perm: provable extent; galilean: nothing, so unit).
//   SOpaque — nothing established; propagates, never manufactures a claim.
//   SBottom — the walker DECLINES.
//
// NOT USED BY THE EQUIV INSTANCE, which keeps its own `RepStatusT` DU for the
// exhaustiveness reason in the header. This lives here so stages 1/2/5 have a
// default to reach for, and as a worked demonstration that the shape the design
// doc describes can satisfy `StatusOps` below.
type Status<'Cov, 'Fix> =
    | SCov of 'Cov
    | SFix of 'Fix
    | SOpaque
    | SBottom

// ============================================================================
// 3. THE OPERATIONS THE GENERIC WALKER NEEDS ON A STATUS
// ============================================================================

/// Everything the structural arms below must be able to do to a status,
/// without knowing what it is.
///
/// `FixOfType` and `ClassifyTy` close over the type resolver (and, for the
/// latter, the hypothesis), so this record is built ONCE per walk rather than
/// once per node.
type StatusOps<'St> = {
    /// The walker DECLINES. Deduction reads it as silence, checking as abstain.
    Bottom: 'St
    /// Nothing established.
    Opaque: 'St
    /// Fixed, refinement unestablished — what every discipline binds pattern
    /// variables at (MLCertShell.bindPatternVars takes exactly this, one
    /// abstraction level down: `Inv` / `BInv` / `Pow 0`).
    FixTop: 'St
    /// Fixed AND provably 0-dimensional — a loop counter is an integer.
    FixScalar: 'St

    IsCov: 'St -> bool
    IsFix: 'St -> bool
    IsBottom: 'St -> bool
    IsOpaque: 'St -> bool

    /// Merge two statuses reached on different control-flow paths. `None` = the
    /// paths disagree, which every caller turns into Bottom.
    Join: 'St -> 'St -> 'St option

    /// Does an ARGUMENT status satisfy a stored PARAMETER status at a call?
    ///
    /// DELIBERATELY NOT `Join >> Option.isSome`, and the difference is
    /// load-bearing: equiv's `joinStatusT` accepts Opaque-against-Opaque (two
    /// control-flow paths that both established nothing still agree that
    /// nothing is established), but an OPAQUE ARGUMENT must never satisfy a
    /// parameter — that is the case where the certificate would have been doing
    /// work, and where a mismatch is a real loss of information.
    ParamMatches: 'St -> 'St -> bool

    /// "Fixed, with the refinement read off this type." The typed win over the
    /// seam's syntactic shape guessing.
    FixOfType: IRType -> 'St

    /// Classify a type under the current hypothesis — the former rule's
    /// type-agreement guard needs the status of the RESULT NODE'S OWN type.
    ClassifyTy: IRType -> 'St
}

/// A callee's stored signature, projected into what the call rule needs. The
/// discipline projects its own signature record into this at the lookup
/// closure, so the kit never sees a discipline's signature type.
type CallSig<'Hyp, 'St> = {
    CHyp: 'Hyp
    CParams: 'St list
    CReturn: 'St
}

/// The two places a structural arm must ask the discipline a question. Both are
/// arms whose SHAPE is shared and whose VERDICT is not.
type StructRules<'St> = {
    /// A covariant binding used in APPLICATION position — `x(i)` where `x`
    /// moves. This is a component read, and the three disciplines disagree
    /// flatly: equiv declines (components of an l>0 block are basis-dependent),
    /// galilean returns a covariant element (boost-variance is per-component and
    /// index-stable), perm declines in v1 though the mathematics permits it.
    CovAppliedAsCallee: 'St -> 'St

    /// The conclusion guard of a former application, after the kernel body has
    /// been walked generically. Arguments, in order:
    ///   * the kernel body's derived status,
    ///   * the status classified from the RESULT NODE'S OWN type,
    ///   * whether the kernel body is inside the componentwise-uniform-linear
    ///     fragment (`isElementwiseArith`),
    ///   * whether any source array was covariant.
    /// equiv needs all four; galilean and perm need only the first.
    FormerConclusion: 'St -> 'St -> bool -> bool -> 'St
}

/// The walker's environment, generic over the hypothesis and the status.
type WalkCtx<'Hyp, 'St> = {
    Ops: StatusOps<'St>
    Rules: StructRules<'St>
    Hyp: 'Hyp
    HypEq: 'Hyp -> 'Hyp -> bool
    /// Pinned (`where ml.*`) or elaborator-stamped callee summaries.
    Certified: IRId -> CallSig<'Hyp, 'St> option
    /// This pass's speculative summaries under THIS hypothesis.
    Speculative: IRId -> CallSig<'Hyp, 'St> option
    /// The function being judged. In DEDUCTION no summary proves itself; in
    /// CHECKING self-reference is ASSUMED (assume-guarantee) and this is a
    /// sentinel no binder matches.
    Self: IRId
    /// Binder ids whose SPECULATIVE summaries this walk actually consumed.
    DepHits: System.Collections.Generic.HashSet<IRId>
    /// CHECKING MODE. False for deduction, true for validating a declared
    /// certificate. The walk is otherwise IDENTICAL — this flag exists only to
    /// make the walker refuse to produce a DEFINITE status at the one rule
    /// where it is knowingly more permissive than the seam checker, so that a
    /// documented divergence can never be reported as a compiler bug.
    Checking: bool
}

// ============================================================================
// 4. THE STRUCTURAL FRAGMENT
// ============================================================================

/// Does the stored signature `sg` apply to a call with these argument statuses?
/// Hypothesis equality, then arity, then positional agreement.
let sigApplies (ctx: WalkCtx<'Hyp, 'St>) (sg: CallSig<'Hyp, 'St>) (argSts: 'St list) : bool =
    ctx.HypEq sg.CHyp ctx.Hyp
    && List.length sg.CParams = List.length argSts
    && (List.zip sg.CParams argSts |> List.forall (fun (p, a) -> ctx.Ops.ParamMatches p a))

/// The structural arms of the walker, written once for every discipline.
///
/// `judge` is the caller's full recursive walk, tied back by the discipline;
/// `None` means "this node kind belongs to the RULES", and that `None` IS the
/// abstraction boundary — stated in one place, checkable by reading one
/// function. The node kinds it declines are exactly: literals, arithmetic
/// (binary and unary), whole-array negate/conjugate, indexing, reduction,
/// aggregate construction, and virtual arrays.
let structuralArm
        (ctx: WalkCtx<'Hyp, 'St>)
        (judge: Map<IRId, 'St> -> TypedExpr -> 'St)
        (env: Map<IRId, 'St>)
        (expr: TypedExpr)
    : 'St option =

    let ops = ctx.Ops
    let j = judge env

    match expr.Kind with

    // --- variables --------------------------------------------------------
    // A parameter carries its classified status. A FREE variable (module
    // global, builtin, constant) is fixed by the conditional-theorem reading —
    // the theorem quantifies over the action on the PARAMETERS, and a
    // module-level constant is the same value in every frame — with its
    // refinement read off its type. NOTE this is deliberately fixed even when
    // the global's own TYPE would classify as moving: a fixed buffer does not
    // transform, and calling it covariant would be the unsound direction.
    | TExprVar (_, vid, _) when vid = ctx.Self -> Some ops.Bottom
    | TExprVar (_, vid, _) ->
        Some (match Map.tryFind vid env with
              | Some st -> st
              | None -> ops.FixOfType expr.Type)

    // --- control flow -----------------------------------------------------
    // SOUNDNESS: if the condition is fixed, the SAME branch is taken in every
    // frame, so the result's law is the branches' common law. A condition that
    // moves with the frame selects different branches in different frames and
    // proves nothing.
    | TExprIf (c, t, f) ->
        Some (if ops.IsFix (j c) then
                  (match ops.Join (j t) (j f) with
                   | Some s -> s
                   | None -> ops.Bottom)
              else ops.Bottom)

    // Same rule, n-ary. Pattern-bound variables enter fixed at the weakest
    // refinement (destructuring a moving value is refused: its components are
    // basis-dependent for equiv, and "bind it whole" for the other two).
    | TExprMatch (scrut, cases) ->
        Some (if ops.IsFix (j scrut) then
                  let armSts =
                      cases
                      |> List.map (fun c ->
                          let env' =
                              c.Pattern.Bindings
                              |> List.fold (fun m (_, vid, _) -> Map.add vid ops.FixTop m) env
                          judge env' c.Body)
                  (match armSts with
                   | [] -> ops.FixTop
                   | s :: rest ->
                       match rest |> List.fold (fun acc s2 -> acc |> Option.bind (fun a -> ops.Join a s2)) (Some s) with
                       | Some joined -> joined
                       | None -> ops.Bottom)
              else ops.Bottom)

    // --- binding forms ----------------------------------------------------
    // The binding-descent problem, solved by ENVIRONMENT THREADING rather than
    // by Deduce.flattenBindings: this walker carries an env (the seam's
    // design), so inlining bindings first would be a no-op preprocessing pass
    // — and it is strictly more general, since flatten declines to inline a
    // non-rewritable or over-budget value and leaves a residual `let` that a
    // binding-free walker then bottoms out on.
    | TExprLet (_, vid, value, body) ->
        Some (let sv = j value
              if ops.IsBottom sv then ops.Bottom
              else judge (Map.add vid sv env) body)

    | TExprBlock (stmts, final) ->
        let rec go (envAcc: Map<IRId, 'St>) (ss: TypedStmt list) : Map<IRId, 'St> option =
            match ss with
            | [] -> Some envAcc
            | TStmtLet b :: rest ->
                let sv = judge envAcc b.Value
                if ops.IsBottom sv then None
                // Destructuring a moving value exposes its components.
                elif ops.IsCov sv && not b.SubBindings.IsEmpty then None
                else
                    let e1 = Map.add b.VarId sv envAcc
                    let e2 =
                        b.SubBindings
                        |> List.fold (fun m (_, vid, _) -> Map.add vid ops.FixTop m) e1
                    go e2 rest
            | TStmtExpr x :: rest ->
                if ops.IsBottom (judge envAcc x) then None else go envAcc rest
            | TStmtAssign (l, r) :: rest ->
                if ops.IsFix (judge envAcc l) && ops.IsFix (judge envAcc r)
                then go envAcc rest else None
            | TStmtForIn (_, vid, lo, hi, body) :: rest ->
                // A loop counter is an integer: fixed scalar. The body is
                // checked in a scope that does not escape.
                if not (ops.IsFix (judge envAcc lo)) || not (ops.IsFix (judge envAcc hi)) then None
                else
                    match go (Map.add vid ops.FixScalar envAcc) body with
                    | None -> None
                    | Some _ -> go envAcc rest
        Some (match go env stmts with
              | None -> ops.Bottom
              | Some env' ->
                  match final with
                  | Some fe -> judge env' fe
                  | None -> ops.FixTop)

    | TExprSequence es ->
        let sts = es |> List.map j
        Some (if sts |> List.exists ops.IsBottom then ops.Bottom
              else match List.tryLast sts with Some s -> s | None -> ops.FixTop)

    // SOUNDNESS: writing a fixed value into a fixed destination cannot move
    // anything the action fixes. Anything moving on either side needs the
    // seam's judgeAssign analysis, which v1 does not port: decline.
    | TExprAssign (l, r) ->
        Some (if ops.IsFix (j l) && ops.IsFix (j r) then ops.FixTop else ops.Bottom)

    // --- static selectors -------------------------------------------------
    | TExprTupleIndex (baseE, idxE) ->
        let sb = j baseE
        let si = j idxE
        Some (if ops.IsFix sb && ops.IsFix si then ops.FixTop
              elif ops.IsBottom sb || ops.IsBottom si then ops.Bottom
              else ops.Opaque)

    // A field name is a STATIC selector, so the base alone decides.
    | TExprField (baseE, _, _) ->
        let sb = j baseE
        Some (if ops.IsFix sb then ops.FixTop
              elif ops.IsBottom sb then ops.Bottom
              else ops.Opaque)

    // `compute` is a scheduling boundary, not a value transform.
    | TExprCompute x -> Some (j x)

    // --- lambdas ----------------------------------------------------------
    // v1, and deliberately weaker than the seam's arm: a lambda body is not
    // walked (its parameters have no classified status, and `Captures` is the
    // only handle on what it closes over). With nothing moving in scope the
    // closure is an ordinary fixed helper; with something moving in scope it is
    // Opaque unless it demonstrably captures it, in which case it declines.
    // Opaque here is safe because the callee guard below refuses to call an
    // Opaque value.
    | TExprLambda info ->
        let envHasCov = env |> Map.exists (fun _ st -> ops.IsCov st)
        Some (if not envHasCov then ops.FixTop
              else
                  let capturesCov =
                      info.Captures
                      |> List.exists (fun c ->
                          match Map.tryFind c.VarId env with
                          | Some st -> ops.IsCov st
                          | None -> false)
                  if capturesCov then ops.Bottom else ops.Opaque)

    // --- calls ------------------------------------------------------------
    // The interprocedural rule. A call resolves by the callee's BINDER IRId —
    // the id every reference to a top-level function carries in its `TExprVar`
    // payload — against, in order:
    //   1. the CERTIFIED table (a source-written pin, or an elaborator stamp on
    //      a synthesized function, which is provable by construction): trusted
    //      as an axiom, exactly as the seam trusts it;
    //   2. this pass's SPECULATIVE table under the same hypothesis: consumed at
    //      suggestion strength, and RECORDED as a dependency so the proposal can
    //      name the pins it rests on.
    // When the stored signature does NOT apply — a hypothesis mismatch, an
    // arity mismatch, or an argument whose status does not match the stored
    // parameter status — the call FALLS THROUGH to the all-fixed rule rather
    // than declining outright.
    //
    // SOUNDNESS of the fall-through, AND THE REASON THIS ARM IS GENERIC: a
    // certificate is a statement about what happens to values that MOVE. When
    // every argument is provably fixed, nothing flowing in moves, and the
    // callee is a deterministic map: the same inputs in every frame give the
    // same output in every frame, so the result is fixed no matter which group
    // (if any) the callee is certified for. That argument names no action.
    //
    // KNOWN DIVERGENCE from the seam checker, accepted and documented rather
    // than special-cased: the seam refuses a cross-hypothesis CERTIFIED call in
    // BOTH directions, even when every argument is fixed — a coarser rule than
    // this one. `Checking` is what keeps that divergence from ever being
    // reported as a compiler bug; see below.
    | TExprApp (f, args) ->
        let argSts = args |> List.map j
        /// The all-fixed rule, shared by the uncertified-callee arm and by the
        /// certified arm's fall-through. The refinement comes from the node's
        /// own type, i.e. the callee's return type read under the CURRENT
        /// hypothesis.
        let allFixedRule () =
            if argSts |> List.forall ops.IsFix then ops.FixOfType expr.Type else ops.Bottom
        Some (match f.Kind with
              | TExprVar (_, fid, _) when fid = ctx.Self -> ops.Bottom
              | TExprVar (_, fid, _) ->
                  (match Map.tryFind fid env with
                   | Some st when ops.IsCov st ->
                       // Application syntax over a moving binding is a
                       // component read — the discipline's call.
                       ctx.Rules.CovAppliedAsCallee st
                   // A callee whose own status is unknown or declined cannot be
                   // taken for a fixed function. THIS GUARD IS LOAD-BEARING:
                   // without it a value produced by a node the rules do not
                   // model (Opaque) would take the uncertified-callee path below
                   // and hand back a FIXED status — the shape of the false
                   // accept MLEquiv documents at its `judgeFormerApply`
                   // (corpus ml-equiv/049).
                   | Some st when ops.IsOpaque st || ops.IsBottom st -> ops.Bottom
                   | _ ->
                       let resolved =
                           match ctx.Certified fid with
                           | Some s -> Some (s, false)
                           | None -> ctx.Speculative fid |> Option.map (fun s -> (s, true))
                       match resolved with
                       | Some (sg, speculative) ->
                           if sigApplies ctx sg argSts then
                               (if speculative then ctx.DepHits.Add fid |> ignore)
                               sg.CReturn
                           // THE ONE MODE-SENSITIVE RULE. The fall-through below
                           // is the documented divergence from the seam. In
                           // DEDUCTION that extra recall is the point. In
                           // CHECKING it must not produce a definite status: the
                           // whole purpose of that mode is to agree with the
                           // seam, and a status derived through a rule the seam
                           // does not have is exactly the shape of a FALSE
                           // compiler-bug report. Opaque here means the
                           // validation abstains, which is always safe.
                           elif ctx.Checking then ops.Opaque
                           else allFixedRule ()
                       | None ->
                           // Uncertified callee (builtin, plain helper, array
                           // read through application syntax). SOUNDNESS: a
                           // function of fixed values is fixed. A moving
                           // argument would ESCAPE into a body that carries no
                           // certificate saying what happens to it: decline. An
                           // unclassifiable argument proves nothing either.
                           allFixedRule ())
              | _ ->
                  // Computed callee: admissible only when nothing moving is in
                  // play at all.
                  if ops.IsFix (j f) && argSts |> List.forall ops.IsFix
                  then ops.FixTop
                  else ops.Bottom)

    // --- former application -----------------------------------------------
    //
    // THIS IS THE ARM THE MOVE TO TYPECHECK MAKES NECESSARY. At the seam,
    // `x + y` on two arrays is an `ExprBinOp` and the arithmetic rule fires
    // directly. By typecheck it has ALREADY been desugared into a former
    // application — `method_for(x, y) <@> lambda(a, b) -> a + b` — so without
    // this arm the entire arithmetic fragment of a discipline is invisible and
    // the typed lattice deduces essentially nothing on arrays.
    //
    // THE WALK is generic: bind the kernel's parameters to the statuses of the
    // SOURCE ARRAYS (not to "component" statuses) and walk the kernel body.
    // THE CONCLUSION is not, so it is `Rules.FormerConclusion` — the guard that
    // decides whether reading a per-element kernel as a whole-array operation
    // was valid is a statement about the action.
    | TExprApply info ->
        let srcSts = info.Arrays |> List.map j
        let anyCovSrc = srcSts |> List.exists ops.IsCov
        if srcSts |> List.exists ops.IsBottom then Some ops.Bottom
        else
            Some (match info.Kernel.Kind with
                  | TExprLambda lam when List.length lam.Params = List.length srcSts ->
                      // A kernel parameter inherits its SOURCE's status VERBATIM
                      // — emphatically NOT the refinement of its own (element)
                      // type. The whole point of the whole-array reading is that
                      // a kernel parameter drawn from a fixed ARRAY is a
                      // different number at every position, so it may not scale
                      // a moving value even though each individual element is
                      // 0-dimensional.
                      let kEnv =
                          List.zip lam.Params srcSts
                          |> List.fold (fun m ((p: TypedParam), st) -> Map.add p.VarId st m) env
                      let kSt = judge kEnv lam.Body
                      let outSt = ops.ClassifyTy expr.Type
                      let elementwise =
                          isElementwiseArith
                              (lam.Params |> List.map (fun p -> p.VarId) |> Set.ofList)
                              lam.Body
                      ctx.Rules.FormerConclusion kSt outSt elementwise anyCovSrc
                  | _ -> if anyCovSrc then ops.Bottom else ops.Opaque)

    // --- everything else belongs to the RULES ------------------------------
    // Literals, arithmetic, unary ops, whole-array negate/conjugate, indexing,
    // reduction, aggregate construction, virtual arrays — and the catch-all,
    // which is the discipline's to own because a discipline may model a node
    // kind this one does not.
    | _ -> None
