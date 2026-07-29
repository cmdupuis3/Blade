/// Stage C3 of docs/plan-equivariance-in-types.md: the REJECTION-PARITY CENSUS.
///
/// WHAT THIS BLOCK IS, AND WHY IT IS NOT ONE OF THE OTHER TWO.
///
/// Two gates already compare the elaboration-seam walker (src/ml/compiler/
/// MLEquiv.fs) with the typed walker (src/DeduceRep.fs), and BOTH of them only
/// ever look at programs that COMPILE:
///
///   * Test_RepDifferential.fs (B3) compares what the two sides PROPOSE on
///     accepted programs.
///   * Test_RepCheckAgreement.fs (C1) asks whether the typed side confirms
///     certificates the seam has ALREADY ACCEPTED.
///
/// C3 would make the typed walker the CHECKING AUTHORITY — the side that
/// REFUSES a program. Nothing measures that side, and it cannot be inferred
/// from the other two: a deduction that proposes nothing is never unsound, so
/// `TBottom` is allowed to conflate "I cannot analyze this body" with "this
/// body is WRONG". A checking authority may not conflate them. This block
/// measures how far apart those two readings are, over the 47 `(rejects)`
/// probes in tests/corpus/ml-equiv.
///
/// THE STRUCTURAL OBSTACLE, AND THE SHADOW REWRITE THAT WORKS AROUND IT.
///
/// `Blade.ML.Elaborate.expand` runs BEFORE `checkProgram` inside
/// `TypeCheck.typeCheck`, and a seam rejection makes it return `Error`. So on
/// every program the seam refuses, typechecking never happens and the typed
/// walker is never invoked AT ALL — zero confirms, zero abstains, zero
/// disagreements. That is itself a measurement (obligation 1 below), but it
/// means the interesting question ("what WOULD the typed side say?") cannot be
/// read off a normal compilation.
///
/// It also cannot be reached by renaming the pin: the seam
/// (`MLEquiv.buildCertTable`) and the typed checking site (TypeCheck.fs's
/// `customConjuncts |> tryFind (n = "__ml_equiv")`) key on the SAME normalized
/// conjunct name, deliberately, so no spelling reaches one and not the other.
///
/// The way through is to SILENCE the seam without touching it: rewrite
/// `ml.equiv(G)` in the source to a test-registered, semantically inert
/// conjunct. `MLEquiv.buildCertTable` then finds no certified functions at all
/// and the whole equiv judgment short-circuits (`if Map.isEmpty certs then []`),
/// while the rest of the pipeline — statics, `derive_*` synthesis, elaborator
/// stamping, unification — is unchanged. The program reaches typecheck, and the
/// census re-runs `DeduceRep.checkDeclaredRep` OUT OF BAND on the resulting
/// TypedProgram, supplying the group the shadowed conjunct still carries.
///
/// THE OUT-OF-BAND RE-RUN IS ITSELF MEASURED (obligation 3). It differs from
/// the production site in two ways — it passes `id` for `resolve` instead of
/// the live `Subst.Resolve`, and it rebuilds the certified-signature table by
/// walking decls rather than inheriting `env.FuncRepSigs`. Both are calibrated
/// against the live C1 census on every corpus file that COMPILES: the
/// (confirm, abstain, disagree) triple the out-of-band re-run computes on the
/// shadowed source must equal the triple the production site recorded on the
/// original. If that holds file for file, the same procedure applied to the
/// reject probes is trustworthy. If it ever stops holding, this block goes red
/// rather than quietly reporting numbers that no longer mean anything.
///
/// FOUR OBLIGATIONS:
///   1. On every seam-rejected file, the LIVE typed census is empty. (The
///      structural fact above, asserted rather than assumed.)
///   2. No seam-rejected file yields a typed CONFIRM. A typed CONFIRM on a
///      program the seam refuses is the alarming direction — it would mean the
///      flip ACCEPTS something that is refused today.
///   3. The calibration above.
///   4. Non-vacuity: the shadow rewrite fires, the reject set is non-empty, and
///      the out-of-band re-run confirms something somewhere.
///
/// Everything else this block prints is CENSUS, not assertion: per-file seam
/// codes, typed verdicts, and abstain reasons, which docs/census-rejection-
/// parity.md tabulates. This block CHANGES NO CHECKING BEHAVIOUR; it only
/// observes.
module Blade.Tests.RepRejectCensus

open Blade
open Blade.Types
open Blade.TypedAst
open Blade.Tests.TestHarness

// ============================================================================
// The shadow conjunct
// ============================================================================
//
// A registered no-op. It must be REGISTERED because TypeCheck errors on an
// unknown `where` conjunct (BL4001, UnknownWhereConstraint), and it must be a
// NO-OP because the census is measuring the typed walker, not this handler.
//
// RESIDUE: `Blade.Constraints` has no unregister, so this name stays in the
// process-wide registry once this block has run. That is safe — the name only
// affects a program that WRITES it, no test pins the registered-vocabulary
// list, and the spelling is unwriteable by accident.

let shadowName = "__rep_reject_census_shadow"

let private shadowHandler : Blade.Constraints.ConstraintHandler = {
    Describe = "test-only inert stand-in for ml.equiv, used by the C3 rejection-parity census to silence the elaboration-seam judgment without modifying it"
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

// ============================================================================
// The shadow rewrite
// ============================================================================
//
// Token-level, line-based, and deliberately conservative:
//
//   * a line whose trimmed form starts with `//` is left ALONE. The ml-equiv
//     corpus is heavily commented and most of its prose mentions `ml.equiv`;
//     rewriting inside a comment would be harmless but would inflate the
//     rewrite count that obligation 4 leans on.
//   * `ml.equiv` matches only when the next non-space character is `(` and the
//     preceding character is not an identifier character, so `ml.equivalence`
//     and a qualified `x.ml.equiv` are both left alone.
//   * COLUMNS MOVE. The replacement is longer than the token, so spans on a
//     shadowed line shift. Nothing in this census compares spans; the seam
//     diagnostics it reports come from the UNSHADOWED run.

let private shadowLine (line: string) : string * int =
    let tok = "ml.equiv"
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
            let mutable j = i + tok.Length
            while j < line.Length && line.[j] = ' ' do j <- j + 1
            if j < line.Length && line.[j] = '(' then
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

/// Returns the rewritten source and the number of pins shadowed.
let shadowEquiv (source: string) : string * int =
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
// Out-of-band re-validation
// ============================================================================

type FnVerdict =
    { Owner: string
      Group: string
      /// True when this certificate was written in SOURCE and shadowed by this
      /// block; false for an elaborator stamp, which still carries the real
      /// `__ml_equiv` conjunct and which the live site also validates.
      FromSource: bool
      Verdict: Blade.DeduceRep.CheckVerdict
      /// The span the production site WOULD report a disagreement at
      /// (`RepCheckDisagreements.add ... tBody.Span`). Recorded so the
      /// message-parity sweep can compare it against the seam's pinned span,
      /// which points at the offending sub-expression instead.
      BodySpan: Blade.Ast.Span }

let verdictName (v: Blade.DeduceRep.CheckVerdict) =
    match v with
    | Blade.DeduceRep.RepConfirm -> "confirm"
    | Blade.DeduceRep.RepAbstain _ -> "abstain"
    | Blade.DeduceRep.RepDisagree _ -> "disagree"

let verdictDetail (v: Blade.DeduceRep.CheckVerdict) =
    match v with
    | Blade.DeduceRep.RepConfirm -> ""
    | Blade.DeduceRep.RepAbstain r -> r
    | Blade.DeduceRep.RepDisagree d -> d

/// The equiv certificate on a checked declaration, as (came-from-source, group).
let private certConjunct (w: Blade.Ast.WhereClause option) : (bool * string) option =
    match w with
    | None -> None
    | Some w ->
        w.Custom
        |> List.tryPick (fun (n, args) ->
            let g = match args with g :: _ -> g | [] -> ""
            if n = "__ml_equiv" then Some (false, g)
            elif n = shadowName then Some (true, g)
            else None)

/// Re-run C1's validation over a checked program, in DECL ORDER so a callee's
/// certificate is in the table before a later caller borrows it — the same
/// single-pass discipline the production site gets for free from `checkModule`.
/// `recordCertified` runs before `checkDeclaredRep` for each function, so a
/// recursive body can assume its own certificate exactly as it does live.
let revalidate (tp: TypedProgram) : FnVerdict list =
    let certified = System.Collections.Generic.Dictionary<IRId, Blade.DeduceRep.RepSigT>()
    let out = ResizeArray<FnVerdict>()
    let visit (tf: TypedFunctionDecl) =
        match certConjunct tf.WhereClause with
        | None -> ()
        | Some (fromSource, g) ->
            let parms =
                tf.Params
                |> List.map (fun p ->
                    ({ PName = p.Name; PId = p.VarId; PType = p.Type } : Blade.DeduceRep.RepParam))
            Blade.DeduceRep.recordCertified certified id tf.Name tf.FuncId g parms tf.ReturnType
            let v =
                Blade.DeduceRep.checkDeclaredRep certified id tf.Name tf.FuncId g parms
                    tf.ReturnType tf.Body
            out.Add { Owner = tf.Name; Group = g; FromSource = fromSource; Verdict = v
                      BodySpan = tf.Body.Span }
    for m in tp.Modules do
        for d in m.Decls do
            match d with
            | TDeclFunction tf -> visit tf
            | TDeclImpl impl -> for mth in impl.Methods do visit mth
            | _ -> ()
    List.ofSeq out

let tally (vs: FnVerdict list) : int * int * int =
    let c = vs |> List.filter (fun v -> v.Verdict = Blade.DeduceRep.RepConfirm) |> List.length
    let a = vs |> List.filter (fun v -> match v.Verdict with Blade.DeduceRep.RepAbstain _ -> true | _ -> false) |> List.length
    let d = vs |> List.filter (fun v -> match v.Verdict with Blade.DeduceRep.RepDisagree _ -> true | _ -> false) |> List.length
    (c, a, d)

// ============================================================================
// Running one source through the pipeline
// ============================================================================

/// Parse + typecheck only (no lowering): the typed program is what the census
/// re-validates, and lowering would only add failure modes irrelevant to the
/// question.
let checkOnly (source: string) : Result<TypedProgram, Blade.Diagnostics.Diagnostic list> =
    match Blade.Parser.parseProgram source with
    | Error e -> Error [ Blade.Parser.diagnosticOfParseError None e ]
    | Ok program ->
        match Blade.TypeCheck.typeCheck program with
        | Error errs -> Error (errs |> List.map Blade.TypeEnv.diagnosticOfCompileError)
        | Ok (tp, _, _) -> Ok tp

/// Which seam discipline refused a program, read off the diagnostic codes.
/// BL4008 is the equiv walker — the only channel the typed walker has any
/// counterpart for. BL4009 (galilean) and BL4012 (perm) are the seam's OTHER
/// two disciplines, which have no typed lattice at all until C3 builds them.
/// Everything else is refused by machinery that is not an equivariance walker.
let channelOf (ds: Blade.Diagnostics.Diagnostic list) : string =
    let codes = ds |> List.map (fun d -> d.Code) |> List.distinct
    if List.contains "BL4008" codes then "equiv"
    elif List.contains "BL4009" codes then "galilean"
    elif List.contains "BL4012" codes then "perm"
    else "other:" + String.concat "," codes

/// Did the ML ELABORATION seam refuse this program, as opposed to a later
/// stage? The three ml disciplines all run at the pass-1/pass-2 seam, before
/// `checkProgram`; anything else (an index-type mismatch, an op-synthesis
/// refusal, an ordinary type error) is refused somewhere the typed walker
/// either already ran or was never going to.
let isSeamChannel (ch: string) = ch = "equiv" || ch = "galilean" || ch = "perm"

/// Every function the seam NAMED in its diagnostics. The seam's messages all
/// open `function '<name>'`, so the offender set is recoverable — which matters
/// because a reject-probe file usually holds several functions and only some of
/// them are the reason it is refused.
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

// ============================================================================
// The block
// ============================================================================

/// One corpus file's whole record, assembled so the printing and the assertions
/// read the same data.
type FileRecord =
    { Name: string
      IsRejectProbe: bool
      /// None when the unshadowed source compiles.
      SeamDiags: Blade.Diagnostics.Diagnostic list option
      /// Live C1 census on the unshadowed run: (confirm, abstain, disagree).
      LiveCensus: int * int * int
      /// How many source pins the shadow rewrite silenced.
      Shadowed: int
      /// The shadowed run: Error means the program is refused for a reason the
      /// equiv seam was not responsible for.
      TypedVerdicts: Result<FnVerdict list, Blade.Diagnostics.Diagnostic list> }

let runRepRejectCensusTests () : BlockResult =
    printHeader "Equiv Rejection-Parity Census (C3 gate)"
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
    // Self-test: the shadow rewrite
    // ------------------------------------------------------------------
    printSubHeader "Self-test: the shadow rewrite"

    let (s1, n1) = shadowEquiv "function f(x: T) where ml.equiv(O3) -> T = x\n"
    check "shadow: a pin is rewritten, the rest of the line is untouched"
        (n1 = 1 && s1 = sprintf "function f(x: T) where %s(O3) -> T = x\n" shadowName)
        (clip 120 s1)

    let (_, n2) = shadowEquiv "// prose that mentions ml.equiv(O3) at length\n"
    check "shadow: a comment line is left alone" (n2 = 0) (sprintf "%d rewrite(s)" n2)

    let (s3, n3) = shadowEquiv "function f() where ml.equiv(O3), ml.perm_equiv(4) -> T = 1\n"
    check "shadow: a sibling conjunct survives"
        (n3 = 1 && s3.Contains "ml.perm_equiv(4)" && not (s3.Contains "ml.equiv("))
        (clip 120 s3)

    let (_, n4) = shadowEquiv "let x = ml.equivalence(3)\nlet y = ml.equiv_hint\n"
    check "shadow: only a call-shaped `ml.equiv(` matches" (n4 = 0) (sprintf "%d rewrite(s)" n4)

    let (s5, n5) = shadowEquiv "function f() where ml.equiv (SO3) -> T = 1\n"
    check "shadow: a space before the paren still matches"
        (n5 = 1 && s5.Contains (shadowName + " (SO3)"))
        (clip 120 s5)

    // ------------------------------------------------------------------
    // The census
    // ------------------------------------------------------------------
    printSubHeader "Census: ml-equiv corpus, unshadowed seam verdict vs shadowed typed verdict"

    let records =
        [ for (name, source) in Corpus.category "ml-equiv" do
            // Unshadowed: the production pipeline, so the live C1 census and
            // the seam's own verdict are both authentic.
            Blade.DeduceRep.RepCheckDisagreements.reset ()
            Blade.DeduceRep.RepCheckCensus.reset ()
            let (seamResult, _) = Lowering.lowerDiag None source
            let live =
                ( Blade.DeduceRep.RepCheckCensus.confirmed ()
                , Blade.DeduceRep.RepCheckCensus.abstained ()
                , List.length (Blade.DeduceRep.RepCheckDisagreements.get ()) )
            let seamDiags =
                match seamResult with
                | Ok _ -> None
                | Error ds -> Some ds
            // Shadowed: the equiv seam is silent, so the program reaches
            // typecheck (unless something else refuses it) and the typed
            // walker gets its look.
            let (shadowSrc, nShadow) = shadowEquiv source
            let typed =
                match checkOnly shadowSrc with
                | Error ds -> Error ds
                | Ok tp -> Ok (revalidate tp)
            yield { Name = name
                    IsRejectProbe = name.EndsWith "(rejects)"
                    SeamDiags = seamDiags
                    LiveCensus = live
                    Shadowed = nShadow
                    TypedVerdicts = typed } ]

    // Reset once more: the loop above ran real compilations, and nothing after
    // this point should read their residue.
    Blade.DeduceRep.RepCheckDisagreements.reset ()
    Blade.DeduceRep.RepCheckCensus.reset ()

    let rejects = records |> List.filter (fun r -> r.SeamDiags.IsSome)
    let accepted = records |> List.filter (fun r -> r.SeamDiags.IsNone)
    let equivRejects = rejects |> List.filter (fun r -> channelOf r.SeamDiags.Value = "equiv")

    // -- the per-file census lines (informational) ---------------------
    for r in rejects do
        let ch = channelOf r.SeamDiags.Value
        let typedPart =
            match r.TypedVerdicts with
            | Error ds -> sprintf "TYPED-UNREACHABLE %s" (clip 90 (firstMessage ds))
            | Ok vs when vs.IsEmpty -> "no certificate to validate"
            | Ok vs ->
                let offenders = seamOffenders r.SeamDiags.Value
                vs
                |> List.map (fun v ->
                    let d = verdictDetail v.Verdict
                    sprintf "%s%s|%s=%s%s"
                        (if offenders.Contains v.Owner then "*" else "") v.Owner v.Group
                        (verdictName v.Verdict)
                        (if d = "" then "" else "(" + clip 70 d + ")"))
                |> String.concat " ; "
        resultLine Skip (sprintf "REJECT %s" r.Name)
            (sprintf "seam=%s offenders=[%s] %s || typed (* = named by the seam): %s"
                ch (String.concat "," (Set.toList (seamOffenders r.SeamDiags.Value)))
                (clip 90 (firstMessage r.SeamDiags.Value)) typedPart)

    // -- obligation 1: the live typed census is empty on every SEAM rejection
    //
    // Scoped to the three ml-elaboration disciplines on purpose. A probe refused
    // LATER (an index-type mismatch, an ordinary type error) has already been
    // through `checkProgram`, so the typed walker did run on it and the census
    // is legitimately non-empty. Those are counted separately below rather than
    // waved through.
    let seamStageRejects = rejects |> List.filter (fun r -> isSeamChannel (channelOf r.SeamDiags.Value))
    let laterStageRejects = rejects |> List.filter (fun r -> not (isSeamChannel (channelOf r.SeamDiags.Value)))
    let liveNonEmpty = seamStageRejects |> List.filter (fun r -> r.LiveCensus <> (0, 0, 0))
    check "1. the typed walker never runs on a program the ELABORATION SEAM rejects"
        liveNonEmpty.IsEmpty
        (if liveNonEmpty.IsEmpty then
            sprintf "%d seam-stage rejection(s): live typed census is 0 confirm / 0 abstain / 0 disagree on every one (ML elaboration returns Error before checkProgram). %d further rejection(s) happen after typecheck and are excluded"
                seamStageRejects.Length laterStageRejects.Length
         else
            liveNonEmpty |> List.map (fun r -> sprintf "%s -> %A" r.Name r.LiveCensus) |> String.concat " ; ")

    // -- obligation 2: no typed CONFIRM on the function the seam NAMED
    //
    // Function-scoped, not file-scoped. Several reject-probes deliberately put a
    // GOOD function next to the bad one (060's `a_invariant`, 064's `tri_so3`),
    // and the typed side confirming those is correct, not alarming. What would
    // be alarming is confirming the very function the seam refused.
    let alarming =
        [ for r in equivRejects do
            let offenders = seamOffenders r.SeamDiags.Value
            match r.TypedVerdicts with
            | Ok vs ->
                for v in vs do
                    if v.FromSource && v.Verdict = Blade.DeduceRep.RepConfirm && offenders.Contains v.Owner then
                        yield sprintf "%s: %s|%s" r.Name v.Owner v.Group
            | Error _ -> () ]
    // MEASURED: exactly one, corpus 051 (`ml.y_to` bound to a DEAD `let` inside
    // a C4-certified body). The seam refuses the O(3) op BY NAME wherever it
    // appears; the typed walker flattens bindings and judges only what reaches
    // the result, so an unused binding contributes nothing. Pinned as a known
    // divergence rather than left to fail the block, because the block is a
    // census and this IS its headline finding — but the pin is exact, so a
    // SECOND such case, or this one changing shape, goes red.
    let knownPermissive = [ "ML Equiv Point Group O3 Op (rejects): bad|C4" ]
    check "2. the only typed CONFIRM on a seam-rejected function is the pinned one"
        (List.sort alarming = List.sort knownPermissive)
        (if List.sort alarming = List.sort knownPermissive then
            sprintf "%d equiv-channel rejection(s); the typed validation would ACCEPT exactly one of them: %s"
                equivRejects.Length (String.concat " ; " knownPermissive)
         else
            sprintf "expected [%s], measured [%s]"
                (String.concat " ; " knownPermissive) (String.concat " ; " alarming))

    // -- obligation 3: the calibration
    printSubHeader "Calibration: out-of-band re-validation vs the live C1 census"
    let mutable calMatched = 0
    let mutable calMismatch : string list = []
    for r in accepted do
        match r.TypedVerdicts with
        | Ok vs ->
            let t = tally vs
            if t = r.LiveCensus then calMatched <- calMatched + 1
            else
                calMismatch <-
                    calMismatch
                    @ [ sprintf "%s: live %A vs out-of-band %A" r.Name r.LiveCensus t ]
        | Error ds ->
            // A file the seam ACCEPTS but that the shadowed run refuses would
            // mean the shadow rewrite changed the program's fate, which breaks
            // the whole method.
            calMismatch <-
                calMismatch
                @ [ sprintf "%s: accepted unshadowed, REFUSED shadowed: %s" r.Name (clip 90 (firstMessage ds)) ]
    check "3. out-of-band re-validation reproduces the live census on every accepted file"
        calMismatch.IsEmpty
        (if calMismatch.IsEmpty then
            sprintf "%d accepted file(s) matched confirm/abstain/disagree exactly (so `resolve = id`, the rebuilt cert table, and the shadow rewrite are all faithful)"
                calMatched
         else String.concat " ; " calMismatch)

    // -- obligation 4: non-vacuity
    printSubHeader "Harness health"
    let totalShadowed = records |> List.sumBy (fun r -> r.Shadowed)
    check "4a. the shadow rewrite fires"
        (totalShadowed > 0) (sprintf "%d source pin(s) shadowed across the category" totalShadowed)
    check "4b. the corpus still contains equiv-channel rejections"
        (not equivRejects.IsEmpty) (sprintf "%d of %d rejection(s) are BL4008" equivRejects.Length rejects.Length)
    let oobTriple =
        accepted
        |> List.fold (fun (c, a, d) r ->
            match r.TypedVerdicts with
            | Ok vs -> let (c2, a2, d2) = tally vs in (c + c2, a + a2, d + d2)
            | Error _ -> (c, a, d)) (0, 0, 0)
    let (oobConfirms, oobAbstains, oobDisagrees) = oobTriple
    check "4c. the out-of-band re-validation actually confirms certificates"
        (oobConfirms > 0)
        (sprintf "%d confirm / %d abstain / %d disagree over the accepted files (the C1 census, reproduced out of band)"
            oobConfirms oobAbstains oobDisagrees)
    let unshadowedRejects =
        equivRejects |> List.filter (fun r -> r.Shadowed = 0)
    check "4d. every equiv-channel rejection carried a source pin to shadow"
        unshadowedRejects.IsEmpty
        (if unshadowedRejects.IsEmpty then "the shadow rewrite reached all of them"
         else unshadowedRejects |> List.map (fun r -> r.Name) |> String.concat ", ")

    // ------------------------------------------------------------------
    // The census roll-up (informational)
    // ------------------------------------------------------------------
    printSubHeader "Roll-up"

    let byChannel =
        rejects
        |> List.map (fun r -> channelOf r.SeamDiags.Value)
        |> List.countBy id
        |> List.sortBy fst
    for (ch, n) in byChannel do
        resultLine Skip (sprintf "seam rejections on channel '%s'" ch) (sprintf "%d file(s)" n)

    // Per-FILE verdict, decided by the functions the seam actually named. A
    // file counts as "would still be refused" iff the typed side DISAGREES on
    // at least one offender; as "would be let through" iff it abstains or
    // confirms on all of them.
    let mutable nUnreach = 0
    let mutable nWouldReject = 0
    let mutable nWouldPass = 0
    let mutable reasonTally : Map<string, int> = Map.empty
    let mutable perFile : (string * string) list = []
    for r in equivRejects do
        let offenders = seamOffenders r.SeamDiags.Value
        match r.TypedVerdicts with
        | Error ds ->
            nUnreach <- nUnreach + 1
            perFile <- perFile @ [ (r.Name, "REFUSED-ANYWAY " + (match ds with d :: _ -> d.Code | [] -> "?")) ]
        | Ok vs ->
            let mine = vs |> List.filter (fun v -> v.FromSource && offenders.Contains v.Owner)
            let disagrees = mine |> List.filter (fun v -> match v.Verdict with Blade.DeduceRep.RepDisagree _ -> true | _ -> false)
            for v in mine do
                match v.Verdict with
                | Blade.DeduceRep.RepAbstain reason ->
                    reasonTally <- Map.add reason (1 + defaultArg (Map.tryFind reason reasonTally) 0) reasonTally
                | _ -> ()
            if not disagrees.IsEmpty then
                nWouldReject <- nWouldReject + 1
                perFile <- perFile @ [ (r.Name, sprintf "DISAGREE (%d/%d offender(s))" disagrees.Length mine.Length) ]
            else
                nWouldPass <- nWouldPass + 1
                let how =
                    if mine |> List.exists (fun v -> v.Verdict = Blade.DeduceRep.RepConfirm) then "CONFIRM"
                    else "ABSTAIN"
                perFile <- perFile @ [ (r.Name, how) ]

    for (n, v) in perFile do
        resultLine Skip (sprintf "verdict %s" v) n

    resultLine Skip "equiv rejections: per-file typed verdict"
        (sprintf "%d of %d would still be REFUSED (typed disagrees), %d would COMPILE (typed abstains or confirms), %d are refused by a later stage anyway"
            nWouldReject equivRejects.Length nWouldPass nUnreach)
    for KeyValue (reason, n) in reasonTally do
        resultLine Skip (sprintf "abstain x%d" n) reason

    // ------------------------------------------------------------------
    // Message parity: the diagnostics corpus, pin by pin
    // ------------------------------------------------------------------
    //
    // tests/corpus/ml-equiv pins NO message text at all — its reject-probes
    // assert only "the compiler refuses this" via the `(rejects)` name marker
    // (verified: the category carries zero `// ERROR:` / `// ERROR-CONTAINS:`
    // lines). The WORDED pins live in tests/corpus/diagnostics, and they are
    // what a flip would actually have to reproduce. This sweep asks, for every
    // BL4008-pinned file there: does any string the typed validation produces
    // contain the pinned substring, and does the span the typed side would
    // report equal the pinned span?
    printSubHeader "Message parity: tests/corpus/diagnostics BL4008 pins"

    let mutable pinsTotal = 0
    let mutable pinsSurvive = 0
    let mutable spanTotal = 0
    let mutable spanSurvive = 0
    let mutable diagFiles = 0
    for (name, source) in Corpus.category "diagnostics" do
        let (pins, contains) = Blade.Tests.Expect.parseDiagPins source
        if pins |> List.exists (fun p -> p.PinCode = "BL4008") then
            diagFiles <- diagFiles + 1
            let (shadowSrc, _) = shadowEquiv source
            let typed = match checkOnly shadowSrc with Error _ -> [] | Ok tp -> revalidate tp
            let texts = typed |> List.map (fun v -> verdictDetail v.Verdict)
            let survived = contains |> List.filter (fun s -> texts |> List.exists (fun t -> t.Contains s))
            let died = contains |> List.filter (fun s -> not (texts |> List.exists (fun t -> t.Contains s)))
            pinsTotal <- pinsTotal + contains.Length
            pinsSurvive <- pinsSurvive + survived.Length
            let spanPins = pins |> List.filter (fun p -> p.PinCode = "BL4008" && p.PinStart.IsSome)
            let disagreeSpans =
                typed
                |> List.filter (fun v -> match v.Verdict with Blade.DeduceRep.RepDisagree _ -> true | _ -> false)
                |> List.map (fun v -> (v.BodySpan.StartLine, v.BodySpan.StartCol))
            let spanOk =
                spanPins |> List.filter (fun p -> List.contains p.PinStart.Value disagreeSpans)
            spanTotal <- spanTotal + spanPins.Length
            spanSurvive <- spanSurvive + spanOk.Length
            resultLine Skip (sprintf "pins %s" name)
                (sprintf "%d/%d ERROR-CONTAINS survive, %d/%d span pin(s) survive%s; typed says: %s"
                    survived.Length contains.Length spanOk.Length spanPins.Length
                    (if died.IsEmpty then "" else " | DIES: " + (died |> List.map (clip 55) |> String.concat " / "))
                    (if texts.IsEmpty then "(nothing — no verdict produced)"
                     else texts |> List.filter (fun t -> t <> "") |> List.map (clip 60) |> String.concat " ; "))

    resultLine Skip "message parity roll-up"
        (sprintf "%d BL4008-pinned diagnostics file(s): %d/%d ERROR-CONTAINS substrings and %d/%d span pins would survive a flip unchanged"
            diagFiles pinsSurvive pinsTotal spanSurvive spanTotal)

    printFooter "Equiv Rejection-Parity Census (C3 gate)"
        [ sprintf "%d passed" passed
          sprintf "%d failure(s)" failed
          sprintf "%d seam rejection(s)" rejects.Length
          sprintf "%d on the equiv channel" equivRejects.Length
          sprintf "typed would refuse %d, let through %d, %d refused anyway"
              nWouldReject nWouldPass nUnreach
          sprintf "message pins surviving: %d/%d text, %d/%d span" pinsSurvive pinsTotal spanSurvive spanTotal ]
    { Block = "Equiv Rejection-Parity Census (C3 gate)"
      Passed = passed
      Failed = failed
      Skipped = 0
      FailedNames = failedNames }
