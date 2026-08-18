/// Phase C1 of the retired equivariance-in-types plan: the DECLARED-CERTIFICATE
/// AGREEMENT gate.
///
/// The typed walker (DeduceRep) now runs a second, independent judgment of
/// every equivariance certificate the elaboration seam has already checked.
/// The seam remains the checking authority (plan §4 invariant): this block does
/// not test whether certificates are correct — that is the seam's corpus. It
/// tests that the two judgments never CONTRADICT each other, because a
/// contradiction between two independent proofs of the same theorem is a bug in
/// the compiler, not in the user's program (the LieGuardFailure posture).
///
/// Three obligations, in the order the plan states them:
///   1. ZERO DISAGREEMENTS over the ml-equiv corpus. Every certified function
///      must CONFIRM or ABSTAIN.
///   2. The confirm/abstain SPLIT is counted and printed. The abstain count is
///      the shrinking target for C2's TypedExpr PolyExtract port: today's
///      abstentions are the bodies the composition fragment cannot judge.
///   3. The DISAGREE path is self-tested. A disagreeing program cannot live in
///      the corpus — by construction it would be a compiler bug — so the
///      disagreement is constructed artificially, both through the composition
///      fragment (a spec contradiction on a hand-built typed body) and through
///      the engine hook slot (a discharger that refutes).
module Blade.Tests.RepCheckAgreement

open Blade
open Blade.Types
open Blade.IR
open Blade.IRLoopStructure
open Blade.IRStorage
open Blade.IRLift
open Blade.IRMono
open Blade.IRPrint
open Blade.IRValidate
open Blade.TypedAst
open Blade.Tests.TestHarness

// ----------------------------------------------------------------------------
// Synthetic typed fixtures
// ----------------------------------------------------------------------------
//
// The only place in the test tree that hand-builds a TypedExpr. It exists
// because the disagreement path CANNOT be reached from source: any source
// program that reached it would be a compiler bug, which is the very thing this
// block asserts never happens. So the contradiction is assembled directly.
//
// The index record mirrors TypeCheck.lowerIndexType's irreps arm field for
// field (Tag from mkIrrepsTag, IxKind = IxKIrreps, Rank 1, SDimension), so the
// classifier sees exactly the shape production feeds it.

let private irrepsArrayTy (nextId: unit -> IRId) (triples: (int * int * int) list) : IRType =
    let total = triples |> List.sumBy (fun (l, _, m) -> m * (2 * l + 1))
    let ix : IRIndexType =
        { Id = nextId ()
          Rank = 1
          Extent = IRLit (IRLitInt (int64 total))
          Symmetry = SymNone
          Tag = Some (mkIrrepsTag None triples)
          IxKind = IxKIrreps
          Kind = SDimension
          Dependencies = [] }
    mkArrayArrow [ ix ] (IRTScalar ETFloat64) None

/// Run one validation against hand-built types. `resolve` is `id`: these types
/// are already closed, which is what the production site guarantees by running
/// after `closeDeducedRanks`.
let private validateSynthetic (paramTy: IRType) (retTy: IRType) (body: TypedExpr)
    : Blade.DeduceRep.CheckVerdict =
    let certified = System.Collections.Generic.Dictionary<IRId, Blade.DeduceRep.RepSigT>()
    let parms = [ ({ PName = "x"; PId = 1; PType = paramTy } : Blade.DeduceRep.RepParam) ]
    Blade.DeduceRep.checkDeclaredRep certified id "synthetic" 99 "O3" parms retTy body

let private verdictName (v: Blade.DeduceRep.CheckVerdict) =
    match v with
    | Blade.DeduceRep.RepConfirm -> "confirm"
    | Blade.DeduceRep.RepAbstain _ -> "abstain"
    | Blade.DeduceRep.RepDisagree _ -> "disagree"

// ----------------------------------------------------------------------------

let runRepCheckAgreementTests () : BlockResult =
    printHeader "Equiv Certificate Agreement (C1)"
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

    // ========================================================================
    // 1 + 2. The corpus sweep: zero disagreements, and the confirm/abstain split
    // ========================================================================
    printSubHeader "ml-equiv corpus: every certified body confirms or abstains"
    let mutable totConfirm = 0
    let mutable totAbstain = 0
    let mutable totDisagree = 0
    let mutable filesWithCerts = 0
    let mutable genConfirm = 0
    let mutable genAbstain = 0
    let mutable reasonTally : Map<string, int> = Map.empty

    for (name, source) in Corpus.category "ml-equiv" do
        // Reset before the run, not after: `lowerDiag` drives the real
        // typeCheck, which resets these channels itself, but an exception on a
        // deliberately-failing corpus file could otherwise leave the previous
        // file's census in place and double-count it.
        Blade.DeduceRep.RepCheckDisagreements.reset ()
        Blade.DeduceRep.RepCheckCensus.reset ()
        let _ = Lowering.lowerDiag None source
        let disagreements = Blade.DeduceRep.RepCheckDisagreements.get ()
        let confirms = Blade.DeduceRep.RepCheckCensus.confirmed ()
        let abstains = Blade.DeduceRep.RepCheckCensus.abstained ()
        for r in Blade.DeduceRep.RepCheckCensus.abstainReasons () do
            reasonTally <- Map.add r (1 + (defaultArg (Map.tryFind r reasonTally) 0)) reasonTally
        totConfirm <- totConfirm + confirms
        totAbstain <- totAbstain + abstains
        genConfirm <- genConfirm + Blade.DeduceRep.RepCheckCensus.generatedConfirmed ()
        genAbstain <- genAbstain + Blade.DeduceRep.RepCheckCensus.generatedAbstained ()
        totDisagree <- totDisagree + List.length disagreements
        // Only files that actually certify something are worth a line; the rest
        // would be 60 vacuous PASSes.
        if confirms + abstains + List.length disagreements > 0 then
            filesWithCerts <- filesWithCerts + 1
            let detail =
                if disagreements.IsEmpty then
                    sprintf "%d confirm, %d abstain" confirms abstains
                else
                    disagreements
                    |> List.map (fun (owner, d, _) -> sprintf "%s: %s" owner d)
                    |> String.concat " | "
            check name disagreements.IsEmpty detail

    check "corpus: zero disagreements overall"
        (totDisagree = 0)
        (sprintf "%d certified decl(s) over %d file(s): %d confirm, %d abstain, %d DISAGREE"
            (totConfirm + totAbstain + totDisagree) filesWithCerts totConfirm totAbstain totDisagree)

    // A validation that abstains on everything would satisfy obligation 1
    // vacuously. This is the guard against the whole gate silently going dark —
    // e.g. a classifier regression that makes every signature unclassifiable.
    check "corpus: the validation actually confirms certificates"
        (totConfirm > 0)
        (sprintf "%d confirmed" totConfirm)

    printSubHeader "abstention census (C2's shrinking target)"
    for KeyValue (reason, n) in reasonTally do
        resultLine Skip (sprintf "abstain x%d" n) reason
    resultLine Skip "abstain by origin"
        (sprintf "%d elaborator-generated (C2's direct target), %d source-written"
            genAbstain (totAbstain - genAbstain))
    resultLine Skip "confirm by origin"
        (sprintf "%d elaborator-generated, %d source-written"
            genConfirm (totConfirm - genConfirm))

    // ========================================================================
    // 3. The disagree path, constructed artificially
    // ========================================================================
    printSubHeader "self-test: the disagreement path"
    let mutable idc = 1000
    let nextId () = idc <- idc + 1; idc
    let specA = [ (0, 0, 1); (1, 1, 1) ]     // dim 4
    let specB = [ (1, 1, 1) ]                 // dim 3 — a DIFFERENT law
    let tyA = irrepsArrayTy nextId specA
    let tyB = irrepsArrayTy nextId specB
    // The body is the parameter itself, so the walker derives exactly specA.
    let bodyIdent = mkTyped (TExprVar ("x", 1, None)) tyA

    // (a) declared return agrees with the derived law -> CONFIRM.
    let vConfirm = validateSynthetic tyA tyA bodyIdent
    check "self-test: matching spec confirms"
        (vConfirm = Blade.DeduceRep.RepConfirm)
        (verdictName vConfirm)

    // (b) declared return is a DIFFERENT representation -> DISAGREE. This is
    // the one contradiction shape C1 reports: both judgments committed to a
    // definite transformation law for the same body, and they differ.
    let vDisagree = validateSynthetic tyA tyB bodyIdent
    check "self-test: spec contradiction disagrees"
        (match vDisagree with Blade.DeduceRep.RepDisagree _ -> true | _ -> false)
        (verdictName vDisagree)
    check "self-test: the disagreement names both laws"
        (match vDisagree with
         | Blade.DeduceRep.RepDisagree d -> d.Contains "IrrepsIdx<[(0, 0, 1), (1, 1, 1)]>" && d.Contains "IrrepsIdx<[(1, 1, 1)]>"
         | _ -> false)
        (match vDisagree with Blade.DeduceRep.RepDisagree d -> d | _ -> "")

    // (c) a body the walker cannot judge abstains — and abstention is silence,
    // never disagreement. This is the property that keeps engine-discharged
    // bodies safe until C2 lands.
    let bodyOpaque = mkTyped TExprWildcard tyA
    let vAbstain = validateSynthetic tyA tyA bodyOpaque
    check "self-test: unjudgeable body abstains"
        (match vAbstain with Blade.DeduceRep.RepAbstain _ -> true | _ -> false)
        (verdictName vAbstain)

    // ========================================================================
    // 4. The engine hook slot (the C2 stitch point)
    // ========================================================================
    printSubHeader "self-test: engine hook slot"
    // The corpus sweep above ran real compilations, and `typeCheck` installs
    // the production adapter (PolyExtractTyped) at every entry. So the live
    // invariant to assert is not "empty" but "STITCHED": if this goes red, the
    // C2 engine has been disconnected from the walker.
    check "hook: the production engine adapter is registered after a compilation"
        (Blade.DeduceRep.EngineDischarge.isRegistered ()) ""

    // A discharger that CONFIRMS turns an abstention into a confirmation —
    // which is exactly how the engine shrinks the abstain count.
    Blade.DeduceRep.EngineDischarge.register (fun _r _p _sg _body -> Some Blade.DeduceRep.EngineConfirms)
    let vHookOk = validateSynthetic tyA tyA bodyOpaque
    check "hook: EngineConfirms upgrades an abstention to confirm"
        (vHookOk = Blade.DeduceRep.RepConfirm)
        (verdictName vHookOk)

    // A discharger that REFUTES a body the seam certified is itself a
    // compiler-bug signal, and surfaces as a disagreement.
    Blade.DeduceRep.EngineDischarge.register (fun _r _p _sg _body -> Some (Blade.DeduceRep.EngineRefutes "synthetic refutation"))
    let vHookNo = validateSynthetic tyA tyA bodyOpaque
    check "hook: EngineRefutes surfaces as a disagreement"
        (match vHookNo with Blade.DeduceRep.RepDisagree d -> d.Contains "synthetic refutation" | _ -> false)
        (verdictName vHookNo)

    // `None` means NOT APPLICABLE and must leave the verdict at abstain.
    Blade.DeduceRep.EngineDischarge.register (fun _r _p _sg _body -> None)
    let vHookNa = validateSynthetic tyA tyA bodyOpaque
    check "hook: None leaves the verdict at abstain"
        (match vHookNa with Blade.DeduceRep.RepAbstain _ -> true | _ -> false)
        (verdictName vHookNa)

    // A discharger that throws may not crash the compilation.
    Blade.DeduceRep.EngineDischarge.register (fun _r _p _sg _body -> failwith "boom")
    let vHookBoom = validateSynthetic tyA tyA bodyOpaque
    check "hook: a throwing discharger degrades to abstain"
        (match vHookBoom with Blade.DeduceRep.RepAbstain _ -> true | _ -> false)
        (verdictName vHookBoom)

    // The hook receives the PARAMETER LIST alongside the signature — the widened
    // slot C2 needs, since a rep parameter's binder id and type are not
    // recoverable from `RepSigT.Params`. Asserting it here keeps the contract
    // from silently narrowing again.
    Blade.DeduceRep.EngineDischarge.register (fun _r parms sg _body ->
        if List.length parms = List.length sg.Params
           && (List.map (fun (p: Blade.DeduceRep.RepParam) -> p.PName) parms) = (List.map fst sg.Params)
        then Some Blade.DeduceRep.EngineConfirms else None)
    let vHookParms = validateSynthetic tyA tyA bodyOpaque
    check "hook: receives RepParams positionally aligned with sg.Params"
        (vHookParms = Blade.DeduceRep.RepConfirm)
        (verdictName vHookParms)

    // The hook must NOT be able to override a composition verdict: it is
    // consulted only where composition declined.
    Blade.DeduceRep.EngineDischarge.register (fun _r _p _sg _body -> Some Blade.DeduceRep.EngineConfirms)
    let vHookNoOverride = validateSynthetic tyA tyB bodyIdent
    check "hook: cannot override a composition disagreement"
        (match vHookNoOverride with Blade.DeduceRep.RepDisagree _ -> true | _ -> false)
        (verdictName vHookNoOverride)

    // Clearing is how a test isolates itself; the next real compilation
    // re-registers the production adapter, so this leaves no lasting hole.
    Blade.DeduceRep.EngineDischarge.clear ()
    check "hook: clear() empties the slot for test isolation"
        (not (Blade.DeduceRep.EngineDischarge.isRegistered ())) ""

    // Leave no residue for the blocks that run after this one.
    Blade.DeduceRep.RepCheckDisagreements.reset ()
    Blade.DeduceRep.RepCheckCensus.reset ()

    printFooter "Equiv Certificate Agreement (C1)"
        [ sprintf "%d passed" passed
          sprintf "%d failure(s)" failed
          sprintf "%d confirm" totConfirm
          sprintf "%d abstain" totAbstain
          sprintf "%d disagree" totDisagree ]
    { Block = "Equiv Certificate Agreement (C1)"
      Passed = passed
      Failed = failed
      Skipped = 0
      FailedNames = failedNames }
