/// MEASUREMENT SCAFFOLDING FOR docs/census-galilean-layer.md — NOT A SHIPPED
/// DISCIPLINE.
///
/// WHAT THIS BLOCK IS. `docs/design-discipline-as-data.md` asks where each of
/// the three equivariance-family disciplines should CHECK and DEDUCE. Equiv has
/// three gates answering that question for itself (`rep-differential`,
/// `rep-check`, `rep-reject`). Galilean has none, and its answer does not follow
/// from equiv's: the two disciplines share a walker shape and share nothing
/// else. This block builds the smallest typed galilean judgment that can answer
/// "what would a typed walker conclude?" and runs it over every galilean
/// certificate in the corpus, in two censuses:
///
///   * the ACCEPTANCE census — over programs the seam ACCEPTS, what does the
///     typed side say about each certificate? (confirm / abstain / disagree)
///   * the REJECTION census — over programs the seam REFUSES, what would the
///     typed side have said if it were the checking authority? Reached by
///     R1's shadow-rewrite method (below).
///
/// WHAT THIS BLOCK IS NOT. `TypedGal` below is EXPERIMENTAL. It is defined
/// inside the test assembly on purpose: nothing in `src/` references it, no
/// production path can consume it, and it emits no diagnostic. It is a
/// measuring instrument, and the moment a decision is made about galilean's
/// layer it should be either promoted deliberately (with its own gate) or
/// deleted. Do not import it from src/.
///
/// THE SHADOW REWRITE, reused verbatim in method from tests/Test_RepRejectCensus.fs.
/// `Blade.ML.Elaborate.expand` runs BEFORE `checkProgram`, so a seam BL4009
/// makes `typeCheck` return Error and the typed walker never sees the program.
/// Rewriting `ml.galilean(` to a test-registered inert conjunct makes
/// `MLGalilean.buildCertTable` return an EMPTY table, which
/// `MLElaborate.expandModule` short-circuits (`if Map.isEmpty gcerts then []`),
/// so the seam falls silent and the program reaches typecheck carrying the
/// shadowed conjunct — which still names the velocity parameters.
///
/// CALIBRATION (obligation 3). R1 calibrated its out-of-band re-run against a
/// LIVE typed census. Galilean has no live typed census — there is no
/// production typed galilean site to compare against — so the calibration here
/// is the other available one, and it is the one that actually validates the
/// instrument: on every file the seam ACCEPTS, the typed verdicts computed from
/// the SHADOWED source must equal the verdicts computed from the UNSHADOWED
/// source, function for function. If shadowing changed what the typed side
/// sees, every rejection-census number would be meaningless.
///
/// OBLIGATIONS (the only things that turn this block red):
///   1. On every file the seam REFUSES on the galilean channel, typechecking
///      the UNSHADOWED source fails — i.e. the structural fact that the typed
///      walker never runs on a galilean-rejected program.
///   2. No galilean-rejected file yields a typed CONFIRM on a function the seam
///      NAMED as the offender. A typed confirm there is the alarming direction.
///   3. The shadow calibration above.
///   4. Non-vacuity: the rewrite fires, the reject set is non-empty, and the
///      typed side confirms something somewhere.
/// Everything else printed is CENSUS, recorded as [SKIP] lines.
module Blade.Tests.GalLayerCensus

open Blade
open Blade.Ast
open Blade.Types
open Blade.IR
open Blade.TypedAst
open Blade.Tests.TestHarness

// ============================================================================
// 1. THE EXPERIMENTAL TYPED GALILEAN JUDGMENT
// ============================================================================
//
// The typed twin of MLGalilean's {BVar, BInv, BOpaque} plus the `Error` the
// seam carries in a Result. Names are deliberately NOT the seam's, so a reader
// can never mistake this for the shipped lattice.
//
//   GVar    — the value shifts with the frame (U0-coefficient exactly 1).
//   GInv    — held fixed by the boost.
//   GOpaque — nothing established.
//   GBottom — the walker DECLINES (the seam's `Error`, diagnostic dropped).

type GStatus =
    | GVar
    | GInv
    | GOpaque
    | GBottom

/// A galilean signature. NOTE WHAT IS ABSENT: no types are consulted anywhere
/// in building this. Boost-variance is not a type property (MLGalilean.fs:17-20
/// — a velocity DIFFERENCE carries the velocity unit and is boost-INVARIANT),
/// so the classifier reads the conjunct's parameter NAME list, exactly as
/// `MLGalilean.buildCertTable` does. `Return` is always GInv: v1 certifies
/// boost-invariant results only.
type GalSigT = {
    Owner: string
    Params: (string * GStatus) list
    Return: GStatus
}

// --- the kit instance -------------------------------------------------------
//
// DisciplineKit.StatusOps for galilean. Two fields are worth reading twice:
//
//   FixOfType  — CONSTANT. Galilean has no refinement of "fixed" (the design
//                doc's `'Fix = unit`), so the typed win equiv gets here
//                (`IRTScalar` is provably 0-dimensional, which its scaling rule
//                needs) buys galilean nothing: galilean has no scaling rule to
//                gate.
//   ClassifyTy — CONSTANT. This is the §4.2 finding, in code: there is no
//                per-type galilean classifier, so the one hook the kit offers
//                for reading a status off a type is answerable only by "GInv".
let private gOps : DisciplineKit.StatusOps<GStatus> = {
    Bottom = GBottom
    Opaque = GOpaque
    FixTop = GInv
    FixScalar = GInv
    IsCov = (fun s -> s = GVar)
    IsFix = (fun s -> s = GInv)
    IsBottom = (fun s -> s = GBottom)
    IsOpaque = (fun s -> s = GOpaque)
    // MLGalilean.judge's if/match arms: `if st = sf then Ok st else reject`.
    Join = (fun a b -> if a = b then Some a else None)
    // MLGalilean.judgeApp's certified arm: `if sa = pSt then Ok () else Error`.
    // An unclassifiable argument never satisfies a parameter — no parameter is
    // ever GOpaque, so the extra guard is belt-and-braces.
    ParamMatches = (fun p a -> p = a && (p = GVar || p = GInv))
    FixOfType = (fun _ -> GInv)
    ClassifyTy = (fun _ -> GInv)
}

/// MEASURED LIMITATION OF THE KIT, recorded here rather than worked around.
/// `CovAppliedAsCallee` receives ONLY the callee's status, never the argument
/// statuses. Galilean's component-read rule is "indexing a boost-variant array
/// yields boost-variant elements PROVIDED the indices are boost-invariant"
/// (MLGalilean.fs:401-406) — the proviso is not expressible through this hook.
/// It costs nothing HERE because at typecheck `u(i)` is a `TExprIndex`, which
/// is on the rules side and where the guard is applied properly; the hook only
/// fires for application syntax over a covariant binding, which the typed AST
/// does not produce for array reads. Recorded because a future port must not
/// assume the hook is sufficient.
let private gRules : DisciplineKit.StructRules<GStatus> = {
    CovAppliedAsCallee = (fun st -> st)
    // Galilean needs only the kernel body's status: MLGalilean.judgeFormerApply
    // returns `judge ctx env' body` with no conclusion guard at all. The three
    // arguments equiv needs (the node's own type, the elementwise-linear test,
    // whether a source moved) are all ignored, which is what "galilean and perm
    // need only the first" means in DisciplineKit.fs:236.
    FormerConclusion = (fun kSt _ _ _ -> kSt)
}

type GalCtx = {
    Certified: IRId -> GalSigT option
    Self: IRId
    Checking: bool
}

let private toCallSig (sg: GalSigT) : DisciplineKit.CallSig<unit, GStatus> =
    { CHyp = (); CParams = sg.Params |> List.map snd; CReturn = sg.Return }

/// The walker: DisciplineKit's structural arms first, galilean's own rules
/// second — the exact partition `DeduceRep.statusOf` uses.
///
/// `'Hyp = unit` AND `HypEq = always true`. This is a real difference from
/// equiv, not an abbreviation: equiv's hypothesis (the group) is SHARED across
/// functions, so an O3-certified callee inside an SO3 body must be refused.
/// Galilean's hypothesis is a set of the DECLARING function's own parameter
/// names; it means nothing at another function, so there is nothing to compare
/// and every certified callee's signature applies. MLGalilean's own header says
/// the same thing from the other side ("equiv's fold with ONE table — this
/// discipline has no groups to key by").
let galStatusOf (ctx: GalCtx) : Map<IRId, GStatus> -> TypedExpr -> GStatus =
    let wctx : DisciplineKit.WalkCtx<unit, GStatus> = {
        Ops = gOps
        Rules = gRules
        Hyp = ()
        HypEq = (fun _ _ -> true)
        Certified = (fun id -> ctx.Certified id |> Option.map toCallSig)
        // Checking consults no speculative summary.
        Speculative = (fun _ -> None)
        Self = ctx.Self
        DepHits = System.Collections.Generic.HashSet<IRId>()
        Checking = ctx.Checking
    }
    let rec go (env: Map<IRId, GStatus>) (expr: TypedExpr) : GStatus =
        match DisciplineKit.structuralArm wctx go env expr with
        | Some s -> s
        | None -> ruleArm env expr

    /// Galilean's OWN rules — the arms whose soundness argument names the
    /// action (an AFFINE SHIFT u -> u + U0). Every one of them differs from
    /// equiv's, and the differences are the polarity table.
    and ruleArm (env: Map<IRId, GStatus>) (expr: TypedExpr) : GStatus =
        let j = go env

        /// A uniformly boost-variant aggregate shifts componentwise and stays
        /// variant; mixing statuses loses the single U0-coefficient. This is
        /// the OPPOSITE of equiv, where a rep element in an aggregate declines.
        let aggOf (es: TypedExpr list) =
            let sts = es |> List.map j
            if sts |> List.exists ((=) GBottom) then GBottom
            else
                match sts with
                | [] -> GInv
                | s :: rest -> if rest |> List.forall ((=) s) then s else GBottom

        match expr.Kind with

        // A constant is the same number in every frame.
        | TExprLit _ -> GInv

        // The arithmetic table, MLGalilean.fs:179-197 arm for arm and in the
        // same ORDER (the seam's fall-through order is load-bearing: a
        // GVar/GOpaque pair reaches the GVar catch-all, not the GOpaque one).
        //
        // The one addition the typed side must make: `mode`. At the seam `x * y`
        // on two arrays is an ExprBinOp with no notion of cross-iteration; by
        // typecheck an outer-product form is a distinct mode whose result has a
        // higher rank than either operand. Nothing in this lattice has a rule
        // for that, so anything moving under a non-elementwise mode declines.
        | TExprBinOp (mode, op, l, r) ->
            let sl = j l
            let sr = j r
            (match sl, sr, op with
             | GBottom, _, _ | _, GBottom, _ -> GBottom
             | (GVar, _, _ | _, GVar, _) when mode <> Elementwise -> GBottom
             // THE rule of this lattice: the boost cancels in a difference.
             | GVar, GVar, OpSub -> GInv
             // Doubles the U0-coefficient. (Equiv ACCEPTS this — its action is
             // linear. Perm accepts it too.)
             | GVar, GVar, OpAdd -> GBottom
             | GVar, GVar, _ -> GBottom
             | GVar, GInv, (OpAdd | OpSub) -> GVar
             | GInv, GVar, OpAdd -> GVar
             // Carries U0-coefficient -1: refused in v1.
             | GInv, GVar, OpSub -> GBottom
             // Scaling scales the coefficient. (Equiv ACCEPTS scalar * rep.)
             | GVar, _, _ | _, GVar, _ -> GBottom
             | GInv, GInv, _ -> GInv
             | GOpaque, _, _ | _, GOpaque, _ -> GOpaque)

        // NEGATION IS A REJECT. Equiv passes it (-I commutes with every D) and
        // perm passes it (pointwise); galilean refuses it, because -1 * (u+U0)
        // shifts by -U0. One arm, three answers.
        | TExprUnaryOp (_, inner) -> (match j inner with GVar -> GBottom | s -> s)
        | TExprArrayNegate a -> (match j a with GVar -> GBottom | s -> s)
        | TExprArrayConjugate a -> (match j a with GVar -> GBottom | s -> s)

        // COMPONENT READS ARE LEGAL AND PRESERVE VARIANCE — the structural
        // opposite of equiv, where raw indexing into an l>0 block is the
        // forbidden read. Boost-variance is per-component and index-stable, so
        // an element of a velocity array is itself a velocity. The indices must
        // be boost-invariant (a frame-dependent index picks a different cell in
        // a different frame).
        | TExprIndex (arr, idxs, _) ->
            let idxOk () = idxs |> List.forall (fun i -> j i = GInv)
            (match j arr with
             | GBottom -> GBottom
             | GVar -> if idxOk () then GVar else GBottom
             | GInv -> if idxOk () then GInv else GBottom
             | GOpaque -> GOpaque)

        // A fold over boost-variant values SCALES the frame shift by the fold
        // length (documented v2), so it is refused rather than mis-certified.
        | TExprReduce (src, _, init) ->
            let ss = j src
            let si = match init with Some i -> j i | None -> GInv
            (match ss, si with
             | GBottom, _ | _, GBottom -> GBottom
             | GVar, _ | _, GVar -> GBottom
             | GInv, GInv -> GInv
             | _ -> GOpaque)

        | TExprTuple es | TExprStack es | TExprZip es -> aggOf es
        | TExprArrayLit (es, _) -> aggOf es
        | TExprJoin (es, _) -> aggOf es

        // Virtual arrays enumerate indices: frame-independent by nature.
        | TExprRange _ | TExprReverse _ | TExprBlocked _ | TExprDotDot _ -> GInv

        | _ -> GOpaque

    go

// ============================================================================
// 2. SIGNATURE CLASSIFICATION — AND WHY IT CANNOT FAIL
// ============================================================================
//
// `DeduceRep.classifySignature` returns an OPTION and its `None` is a measured
// rejection family (R1's family D: two ml-equiv files are refused entirely at
// the classifier). Galilean's has no `None` to return. It reads parameter
// NAMES against the conjunct's argument list; every name either is or is not a
// parameter, and the "is not" case is a malformed conjunct the seam already
// refuses at `buildCertTable` before any body is walked.
//
// THE CONSEQUENCE FOR THE LAYER QUESTION: equiv's `certSigOf` refuses an
// unannotated parameter, so equiv's recall is gated on a fully annotated
// signature and the move to typecheck BUYS it the zonked types. Galilean has no
// such gate and nothing to buy. Its classifier is layer-INDEPENDENT: the same
// three lines produce the same answer at the seam and at typecheck.

/// The galilean certificate on a checked declaration, as
/// (came-from-a-shadowed-source-pin, velocity parameter names).
let galConjunct (shadowName: string) (w: Blade.Ast.WhereClause option) : (bool * string list) option =
    match w with
    | None -> None
    | Some w ->
        w.Custom
        |> List.tryPick (fun (n, args) ->
            if n = "__ml_galilean" then Some (false, args)
            elif n = shadowName then Some (true, args)
            else None)

let classifyGalSig (owner: string) (parms: TypedParam list) (vs: string list) : GalSigT =
    { Owner = owner
      Params = parms |> List.map (fun p -> (p.Name, if List.contains p.Name vs then GVar else GInv))
      Return = GInv }

type GalVerdict =
    | GConfirm
    | GAbstain of string
    | GDisagree of string

let verdictName (v: GalVerdict) =
    match v with GConfirm -> "confirm" | GAbstain _ -> "abstain" | GDisagree _ -> "disagree"

let verdictDetail (v: GalVerdict) =
    match v with GConfirm -> "" | GAbstain r -> r | GDisagree d -> d

/// Validate ONE declared galilean certificate against the typed walker.
/// Self-reference is ASSUMED, not proved — the assume-guarantee posture
/// `checkDeclaredRep` documents, and the posture `MLGalilean.judgeFunction` has
/// by construction (it judges against a table containing the function's own
/// certificate).
let checkDeclaredGal (certified: System.Collections.Generic.Dictionary<IRId, GalSigT>)
                     (owner: string) (parms: TypedParam list) (vs: string list)
                     (body: TypedExpr) : GalVerdict =
    try
        let pNames = parms |> List.map (fun p -> p.Name)
        if vs.IsEmpty then GAbstain "conjunct names no velocity parameter (seam refuses at buildCertTable)"
        elif vs |> List.exists (fun v -> not (List.contains v pNames)) then
            GAbstain "conjunct names a non-parameter (seam refuses at buildCertTable)"
        else
            let sg = classifyGalSig owner parms vs
            let ctx = {
                Certified = (fun id -> match certified.TryGetValue id with | true, s -> Some s | _ -> None)
                // No binder is "self": IRIds are non-negative.
                Self = System.Int32.MinValue
                Checking = true
            }
            let env =
                List.zip parms sg.Params
                |> List.fold (fun m ((p: TypedParam), (_, st)) -> Map.add p.VarId st m) Map.empty
            match galStatusOf ctx env body with
            | GInv -> GConfirm
            | GBottom -> GAbstain "walker declined (outside the galilean fragment)"
            | GOpaque -> GAbstain "nothing established for the body"
            | GVar ->
                GDisagree (sprintf "the typed walker derives BOOST-VARIANT for the body of '%s', but a galilean certificate asserts a boost-invariant result" owner)
    with _ -> GAbstain "validation raised"

/// Record a declaration's certificate in the interprocedural table BEFORE its
/// body is checked, so a recursive body may assume it.
let recordCertifiedGal (certified: System.Collections.Generic.Dictionary<IRId, GalSigT>)
                       (owner: string) (funcId: IRId) (parms: TypedParam list) (vs: string list) =
    certified.[funcId] <- classifyGalSig owner parms vs

// ============================================================================
// 3. THE SHADOW REWRITE
// ============================================================================

let shadowName = "__gal_layer_census_shadow"

let private shadowHandler : Blade.Constraints.ConstraintHandler = {
    Describe = "test-only inert stand-in for ml.galilean, used by the galilean layer census to silence the elaboration-seam judgment without modifying it"
    Validate = fun _ _ _ -> Ok ()
    EnterBody = fun _ _ -> ()
    ExitBody = fun _ _ -> ()
    Discharge = fun _ _ _ -> Ok ()
}

let mutable private shadowRegistered = false

let ensureShadowRegistered () =
    if not shadowRegistered then
        shadowRegistered <- true
        Blade.Constraints.registerConstraint shadowName shadowHandler

/// Token-level and line-based, in Test_RepRejectCensus's discipline: comment
/// lines are left alone (the galilean corpus is heavily commented and most of
/// its prose says `ml.galilean`), and the token matches only when followed by
/// `(` at an identifier boundary.
let private shadowLine (line: string) : string * int =
    let tok = "ml.galilean"
    let sb = System.Text.StringBuilder()
    let mutable i = 0
    let mutable n = 0
    while i < line.Length do
        let boundaryOk =
            i = 0 || not (System.Char.IsLetterOrDigit line.[i - 1] || line.[i - 1] = '_' || line.[i - 1] = '.')
        let matches =
            boundaryOk
            && i + tok.Length <= line.Length
            && System.String.CompareOrdinal(line, i, tok, 0, tok.Length) = 0
        if matches then
            let mutable jx = i + tok.Length
            while jx < line.Length && line.[jx] = ' ' do jx <- jx + 1
            if jx < line.Length && line.[jx] = '(' then
                sb.Append(shadowName) |> ignore
                i <- i + tok.Length
                n <- n + 1
            else
                sb.Append(line.[i]) |> ignore
                i <- i + 1
        else
            sb.Append(line.[i]) |> ignore
            i <- i + 1
    (sb.ToString(), n)

let shadowGalilean (source: string) : string * int =
    let lines = source.Replace("\r\n", "\n").Split('\n')
    let mutable total = 0
    let out =
        lines
        |> Array.map (fun line ->
            if line.TrimStart().StartsWith "//" then line
            else
                let (l, n) = shadowLine line
                total <- total + n
                l)
    (String.concat "\n" out, total)

// ============================================================================
// 4. RUNNING A SOURCE THROUGH THE PIPELINE
// ============================================================================

type FnVerdict =
    { Owner: string
      /// True when this certificate was written in SOURCE and shadowed by this
      /// block; false when it still carries the real `__ml_galilean` — which,
      /// on a shadowed run, means an SgsElaborate STAMP.
      FromSource: bool
      /// True when the declaration is compiler-generated (`__sgs_N`).
      Generated: bool
      Velocities: string list
      Verdict: GalVerdict }

let checkOnly (source: string) : Result<TypedProgram, Blade.Diagnostics.Diagnostic list> =
    match Blade.Parser.parseProgram source with
    | Error e -> Error [ Blade.Parser.diagnosticOfParseError None e ]
    | Ok program ->
        match Blade.TypeCheck.typeCheck program with
        | Error errs -> Error (errs |> List.map Blade.TypeEnv.diagnosticOfCompileError)
        | Ok (tp, _, _) -> Ok tp

/// Re-run the experimental validation over a checked program, in DECL ORDER so
/// a callee's certificate is in the table before a later caller borrows it.
let revalidate (tp: TypedProgram) : FnVerdict list =
    let certified = System.Collections.Generic.Dictionary<IRId, GalSigT>()
    let out = ResizeArray<FnVerdict>()
    let visit (tf: TypedFunctionDecl) =
        match galConjunct shadowName tf.WhereClause with
        | None -> ()
        | Some (fromSource, vs) ->
            recordCertifiedGal certified tf.Name tf.FuncId tf.Params vs
            let v = checkDeclaredGal certified tf.Name tf.Params vs tf.Body
            out.Add { Owner = tf.Name
                      FromSource = fromSource
                      Generated = tf.Name.StartsWith "__"
                      Velocities = vs
                      Verdict = v }
    for m in tp.Modules do
        for d in m.Decls do
            match d with
            | TDeclFunction tf -> visit tf
            | TDeclImpl impl -> for mth in impl.Methods do visit mth
            | _ -> ()
    List.ofSeq out

let tally (vs: FnVerdict list) : int * int * int =
    let c = vs |> List.filter (fun v -> v.Verdict = GConfirm) |> List.length
    let a = vs |> List.filter (fun v -> match v.Verdict with GAbstain _ -> true | _ -> false) |> List.length
    let d = vs |> List.filter (fun v -> match v.Verdict with GDisagree _ -> true | _ -> false) |> List.length
    (c, a, d)

/// Which seam discipline refused a program, read off the diagnostic codes.
let channelOf (ds: Blade.Diagnostics.Diagnostic list) : string =
    let codes = ds |> List.map (fun d -> d.Code) |> List.distinct
    if List.contains "BL4009" codes then "galilean"
    elif List.contains "BL4008" codes then "equiv"
    elif List.contains "BL4012" codes then "perm"
    else "other:" + String.concat "," codes

/// Every function the seam NAMED in its diagnostics. MLGalilean's messages all
/// open `function '<name>'`.
let seamOffenders (ds: Blade.Diagnostics.Diagnostic list) : Set<string> =
    let tok = "function '"
    ds
    |> List.choose (fun d ->
        let i = d.Message.IndexOf(tok, System.StringComparison.Ordinal)
        if i < 0 then None
        else
            let rest = d.Message.Substring(i + tok.Length)
            let j = rest.IndexOf '\''
            if j < 0 then None else Some (rest.Substring(0, j)))
    |> Set.ofList

let private firstMessage (ds: Blade.Diagnostics.Diagnostic list) =
    match ds with
    | d :: _ -> sprintf "[%s] %s" d.Code d.Message
    | [] -> "(no diagnostic)"

let private clip (n: int) (s: string) =
    if s.Length <= n then s else s.Substring(0, n) + "..."

/// Does this source carry a galilean pin at all (outside comments)?
let mentionsGalileanPin (source: string) : bool =
    let (_, n) = shadowGalilean source
    n > 0

// ============================================================================
// 5. THE BLOCK
// ============================================================================

type FileRecord =
    { Category: string
      Name: string
      /// None when the source compiles.
      SeamDiags: Blade.Diagnostics.Diagnostic list option
      /// How many source pins the shadow rewrite silenced.
      Shadowed: int
      /// Typed verdicts from the UNSHADOWED source (empty when it does not
      /// typecheck).
      PlainVerdicts: Result<FnVerdict list, Blade.Diagnostics.Diagnostic list>
      /// Typed verdicts from the SHADOWED source.
      ShadowVerdicts: Result<FnVerdict list, Blade.Diagnostics.Diagnostic list> }

let private categories = [ "ml-equiv"; "sgs"; "diagnostics" ]

let runGalLayerCensusTests () : BlockResult =
    printHeader "Galilean Layer Census (checking-authority measurement)"
    ensureShadowRegistered ()
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames : string list = []
    let check name ok detail =
        if ok then
            passed <- passed + 1
            resultLine Pass name detail
        else
            failed <- failed + 1
            failedNames <- failedNames @ [ name ]
            resultLine Fail name detail

    // ------------------------------------------------------------------
    printSubHeader "Self-test: the shadow rewrite"

    let (s1, n1) = shadowGalilean "function f(u: Float) where ml.galilean(u) -> Float = u\n"
    check "shadow: a pin is rewritten, the rest of the line is untouched"
        (n1 = 1 && s1 = sprintf "function f(u: Float) where %s(u) -> Float = u\n" shadowName)
        (clip 120 s1)

    let (_, n2) = shadowGalilean "// prose that mentions ml.galilean(u) at length\n"
    check "shadow: a comment line is left alone" (n2 = 0) (sprintf "%d rewrite(s)" n2)

    let (s3, n3) = shadowGalilean "function f() where ml.equiv(O3), ml.galilean(v, w) -> T = 1\n"
    check "shadow: a sibling conjunct survives"
        (n3 = 1 && s3.Contains "ml.equiv(O3)" && not (s3.Contains "ml.galilean("))
        (clip 120 s3)

    let (_, n4) = shadowGalilean "let x = ml.galileanish(3)\nlet y = ml.galilean_hint\n"
    check "shadow: only a call-shaped `ml.galilean(` matches" (n4 = 0) (sprintf "%d rewrite(s)" n4)

    // ------------------------------------------------------------------
    printSubHeader "Census: corpus sweep"

    let records =
        [ for cat in categories do
            for (name, source) in Corpus.category cat do
                if mentionsGalileanPin source || source.Contains "sgs." then
                    let (seamResult, _) = Lowering.lowerDiag None source
                    let seamDiags =
                        match seamResult with
                        | Ok _ -> None
                        | Error ds -> Some ds
                    let plain =
                        match checkOnly source with
                        | Error ds -> Error ds
                        | Ok tp -> Ok (revalidate tp)
                    let (shadowSrc, nShadow) = shadowGalilean source
                    let shadowed =
                        match checkOnly shadowSrc with
                        | Error ds -> Error ds
                        | Ok tp -> Ok (revalidate tp)
                    yield { Category = cat
                            Name = name
                            SeamDiags = seamDiags
                            Shadowed = nShadow
                            PlainVerdicts = plain
                            ShadowVerdicts = shadowed } ]

    let accepted = records |> List.filter (fun r -> r.SeamDiags.IsNone)
    let rejected = records |> List.filter (fun r -> r.SeamDiags.IsSome)
    let galRejected =
        rejected |> List.filter (fun r ->
            match r.SeamDiags with Some ds -> channelOf ds = "galilean" | None -> false)

    // ------------------------------------------------------------------
    printSubHeader "ACCEPTANCE census — files the seam accepts"

    for r in accepted do
        match r.PlainVerdicts with
        | Error ds ->
            resultLine Skip (sprintf "%s / %s" r.Category r.Name)
                (sprintf "seam OK but checkOnly failed: %s" (clip 100 (firstMessage ds)))
        | Ok vs ->
            let (c, a, d) = tally vs
            resultLine Skip (sprintf "%s / %s" r.Category r.Name)
                (sprintf "%d certs: %dC %dA %dD | %s" (List.length vs) c a d
                    (vs
                     |> List.map (fun v ->
                         sprintf "%s%s=%s%s" v.Owner (if v.Generated then "*" else "")
                             (verdictName v.Verdict)
                             (match v.Verdict with
                              | GAbstain rr -> sprintf "(%s)" (clip 46 rr)
                              | GDisagree dd -> sprintf "(%s)" (clip 46 dd)
                              | _ -> ""))
                     |> String.concat "; "))

    let acceptedVerdicts =
        accepted |> List.collect (fun r -> match r.PlainVerdicts with Ok vs -> vs | Error _ -> [])
    let (accC, accA, accD) = tally acceptedVerdicts
    let srcV = acceptedVerdicts |> List.filter (fun v -> not v.Generated)
    let genV = acceptedVerdicts |> List.filter (fun v -> v.Generated)
    let (sC, sA, sD) = tally srcV
    let (gC, gA, gD) = tally genV
    resultLine Skip "ACCEPTANCE TOTAL"
        (sprintf "%d certs: %d confirm / %d abstain / %d disagree" (List.length acceptedVerdicts) accC accA accD)
    resultLine Skip "  source-written certs"
        (sprintf "%d: %d confirm / %d abstain / %d disagree" (List.length srcV) sC sA sD)
    resultLine Skip "  generated (SgsElaborate stamps)"
        (sprintf "%d: %d confirm / %d abstain / %d disagree" (List.length genV) gC gA gD)

    let abstainHistogram (vs: FnVerdict list) =
        vs
        |> List.choose (fun v -> match v.Verdict with GAbstain r -> Some r | _ -> None)
        |> List.countBy id
        |> List.sortByDescending snd
    for (reason, n) in abstainHistogram acceptedVerdicts do
        resultLine Skip "  abstain reason" (sprintf "%d x %s" n reason)

    // ------------------------------------------------------------------
    printSubHeader "REJECTION census — files the seam refuses"

    for r in rejected do
        let ds = Option.defaultValue [] r.SeamDiags
        let ch = channelOf ds
        let offenders = seamOffenders ds
        let typedStr =
            match r.ShadowVerdicts with
            | Error e -> sprintf "shadowed run refused by %s" (clip 80 (firstMessage e))
            | Ok vs ->
                if vs.IsEmpty then "(no galilean certs survived to typecheck)"
                else
                    vs
                    |> List.map (fun v ->
                        sprintf "%s%s%s=%s%s" (if Set.contains v.Owner offenders then "*" else "")
                            v.Owner (if v.Generated then "~" else "")
                            (verdictName v.Verdict)
                            (match v.Verdict with
                             | GAbstain rr -> sprintf "(%s)" (clip 46 rr)
                             | GDisagree dd -> sprintf "(%s)" (clip 46 dd)
                             | _ -> ""))
                    |> String.concat "; "
        resultLine Skip (sprintf "%s / %s" r.Category r.Name)
            (sprintf "[%s] %s || typed: %s" ch (clip 90 (firstMessage ds)) typedStr)

    // The number that decides the layer: for each galilean-channel rejection,
    // does the typed side REFUSE the offender (disagree) or LET IT THROUGH
    // (confirm / abstain / not reached)?
    let offenderVerdicts =
        [ for r in galRejected do
            let offenders = seamOffenders (Option.defaultValue [] r.SeamDiags)
            match r.ShadowVerdicts with
            | Error e -> yield (r.Name, "(none)", "refused-by-later-stage", clip 60 (firstMessage e))
            | Ok vs ->
                let hits = vs |> List.filter (fun v -> Set.contains v.Owner offenders)
                if hits.IsEmpty then
                    yield (r.Name, "(none)", "no-cert-reached-typecheck", "")
                else
                    for v in hits do
                        yield (r.Name, v.Owner, verdictName v.Verdict, clip 60 (verdictDetail v.Verdict)) ]
    for (f, o, verdict, detail) in offenderVerdicts do
        resultLine Skip "  offender verdict" (sprintf "%s :: %s -> %s %s" f o verdict detail)
    let stillRefused =
        offenderVerdicts |> List.filter (fun (_, _, v, _) -> v = "disagree" || v = "refused-by-later-stage")
    resultLine Skip "REJECTION TOTAL"
        (sprintf "%d galilean-channel reject files, %d offender verdicts, %d would still be refused"
            (List.length galRejected) (List.length offenderVerdicts) (List.length stillRefused))

    // ------------------------------------------------------------------
    printSubHeader "Obligations"

    // 1. The structural fact: a galilean-channel rejection never reaches the
    //    typed walker.
    let liveEmpty =
        galRejected |> List.forall (fun r ->
            match r.PlainVerdicts with Error _ -> true | Ok vs -> vs.IsEmpty)
    check "1. no galilean-rejected file reaches typecheck with any certificate"
        liveEmpty
        (sprintf "%d galilean-channel rejections" (List.length galRejected))

    // 2. The alarming direction.
    let alarming = offenderVerdicts |> List.filter (fun (_, _, v, _) -> v = "confirm")
    check "2. no typed CONFIRM on a function the seam named as a galilean offender"
        alarming.IsEmpty
        (if alarming.IsEmpty then "none"
         else alarming |> List.map (fun (f, o, _, _) -> sprintf "%s::%s" f o) |> String.concat ", ")

    // 3. The calibration: on accepted files, shadowing must not change what the
    //    typed side sees.
    let calibrationMismatches =
        [ for r in accepted do
            match r.PlainVerdicts, r.ShadowVerdicts with
            | Ok a, Ok b ->
                let key (v: FnVerdict) = (v.Owner, verdictName v.Verdict)
                let ka = a |> List.map key |> List.sort
                let kb = b |> List.map key |> List.sort
                if ka <> kb then yield (r.Name, ka, kb)
            | Ok a, Error e -> yield (r.Name, a |> List.map (fun v -> (v.Owner, verdictName v.Verdict)), [ ("<shadowed run refused>", clip 60 (firstMessage e)) ])
            | Error _, _ -> () ]
    check "3. calibration: shadowing changes no typed verdict on an accepted file"
        calibrationMismatches.IsEmpty
        (if calibrationMismatches.IsEmpty then sprintf "%d accepted files agree" (List.length accepted)
         else calibrationMismatches |> List.map (fun (n, _, _) -> n) |> String.concat ", ")

    // 4. Non-vacuity.
    let totalShadowed = records |> List.sumBy (fun r -> r.Shadowed)
    check "4. non-vacuity: the rewrite fires, rejections exist, something confirms"
        (totalShadowed > 0 && not galRejected.IsEmpty && accC > 0)
        (sprintf "%d pins shadowed, %d galilean rejections, %d typed confirms" totalShadowed (List.length galRejected) accC)

    printFooter "Galilean Layer Census"
        [ sprintf "%d passed" passed
          sprintf "%d failure(s)" failed
          sprintf "%d file(s) swept" (List.length records)
          sprintf "acceptance: %d certs, %d confirm / %d abstain / %d disagree"
              (List.length acceptedVerdicts) accC accA accD
          sprintf "rejection: %d galilean-channel file(s), %d offender verdict(s), %d still refused"
              (List.length galRejected) (List.length offenderVerdicts) (List.length stillRefused) ]
    { Block = "Galilean Layer Census"
      Passed = passed
      Failed = failed
      Skipped = 0
      FailedNames = failedNames }
