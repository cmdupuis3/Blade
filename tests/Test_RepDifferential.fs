/// B3 of docs/plan-equivariance-in-types.md: the differential gate between
/// the typed representation-status deduction (Blade.DeduceRep, producer of
/// TypedCertProposals) and the elaboration-seam inference (stage 6a,
/// producer of CertSuggestions/BL4011). Ship criterion for phase B: typed
/// recall ⊇ seam recall over the corpus, with ZERO false proposals (every
/// typed-only proposal must be accepted by the seam checker when tried as
/// a pinned hypothesis).
///
/// WHAT THIS BLOCK IS. Two independent deductions of the same theorem run
/// over the same 97 programs, and their proposals are compared per file.
/// The seam walker is the incumbent: it runs at the MLElaborate pass-1/2
/// seam, where the `ml.*` vocabulary is still visible, and it is the
/// CHECKING authority through all of phase B (§4 of the plan). The typed
/// walker is the challenger: it runs at typecheck, with unification closed
/// and the exact index types in hand. The gate says the challenger may
/// only ever propose MORE, never differently:
///
///   (a) RECALL   — every seam proposal is matched by a typed proposal
///                  with the same (owner, group).
///   (b) SOUNDNESS — every typed proposal is either seam-matched or
///                  declared as a deliberate WIN by a `// TYPED-SUGGEST:`
///                  pin in the file.
///   (c) STRICTNESS — those pins are exact both ways: a pin that produces
///                  nothing is as much a failure as an unpinned proposal.
///
/// Rendered signatures and dependency closures are compared LENIENTLY:
/// disagreement is printed on the file's detail line and reviewed, but
/// does not fail. The two sides render from different IRs and the plan
/// never promised the strings would agree — only the theorems.
///
/// SCOPE. Only the equiv discipline (BL4011). The galilean channel
/// (BL4014, GalCertSuggestions) is untouched here: it migrates to the
/// generic engine in phase C3, and pretending to gate it now would pin a
/// permanent zero.
module Blade.Tests.RepDifferential

open Blade
open Blade.Tests.TestHarness

// ============================================================================
// The shared vocabulary
// ============================================================================

/// One proposal, from either side, reduced to what the differential compares.
/// (Owner, Group) is the IDENTITY — the theorem being proposed. Signature and
/// Deps are the lenient payload.
type Proposal =
    { Owner: string
      Group: string
      /// Rendered signature summary; "" when the producer rendered none.
      Signature: string
      /// Dependency closure (unpinned helpers the proposal rests on), decl order.
      Deps: string list }

/// What a string on the seam's equiv channel turned out to be. The channel
/// carries two shapes, and only one of them is a DEDUCTION.
type SeamParse =
    /// `function 'f' judges equivariant under G: add 'where ml.equiv(G)' [...]`
    | SeamProposal of Proposal
    /// The E4 upgrade lint: `function 'f' is pinned ml.equiv(SO3) but judges
    /// under O3: ...`. This proposes EDITING a pin, not adding one — there is
    /// no uncertified function behind it, so the typed walker has nothing to
    /// match and the differential excludes it by shape.
    | SeamUpgradeLint
    /// Neither shape. Counted and reported; the summary asserts this is empty,
    /// because a silent shape drift would turn the whole gate vacuous.
    | SeamUnparseable of string

// ============================================================================
// Parsing the seam's message shape
// ============================================================================
//
// The producer is MLEquiv.inferCertificates:
//
//   sprintf "function '%s' judges equivariant under %s: add 'where ml.equiv(%s)'
//            [signature: %s]%s"
//
// with the optional tail " (also requires pinning: a, b)". The signature
// summary itself contains ']' (spec lists render as `IrrepsIdx<[(0, 0, 1)]>`),
// so the closure tail is split off FIRST and the signature is what remains
// before the final bracket — scanning for the first ']' would truncate every
// message the corpus actually produces.

let private closureMarker = " (also requires pinning: "

let private upTo (tok: string) (s: string) : (string * string) option =
    let i = s.IndexOf(tok, System.StringComparison.Ordinal)
    if i < 0 then None else Some (s.Substring(0, i), s.Substring(i + tok.Length))

let private afterPrefix (tok: string) (s: string) : string option =
    if s.StartsWith(tok, System.StringComparison.Ordinal) then Some (s.Substring tok.Length)
    else None

/// Total: every string on the channel classifies, none throws.
let parseSeamMessage (msg: string) : SeamParse =
    if msg.Contains "is pinned ml.equiv(SO3) but judges under O3" then SeamUpgradeLint
    else
        let parsed =
            afterPrefix "function '" msg
            |> Option.bind (upTo "' judges equivariant under ")
            |> Option.bind (fun (owner, r1) ->
                upTo ": add 'where ml.equiv(" r1 |> Option.map (fun (g, r2) -> (owner, g, r2)))
            |> Option.bind (fun (owner, g, r2) ->
                upTo "[signature: " r2 |> Option.map (fun (_, r3) -> (owner, g, r3)))
            |> Option.bind (fun (owner, g, r3) ->
                let (body, deps) =
                    match upTo closureMarker r3 with
                    | Some (head, tail) ->
                        let names =
                            (if tail.EndsWith ")" then tail.Substring(0, tail.Length - 1) else tail)
                                .Split(',')
                            |> Array.toList
                            |> List.map (fun (s: string) -> s.Trim())
                            |> List.filter (fun s -> s <> "")
                        (head, names)
                    | None -> (r3, [])
                if body.EndsWith "]" then
                    Some { Owner = owner
                           Group = g.Trim()
                           Signature = body.Substring(0, body.Length - 1)
                           Deps = deps }
                else None)
        match parsed with
        | Some p -> SeamProposal p
        | None -> SeamUnparseable msg

// ============================================================================
// Per-file directives
// ============================================================================
//
// Whole-line, trimmed, exact-prefix — the `// SUGGEST:` parser's convention
// (Test_DiagCorpus.fs), so a directive is greppable and cannot hide inside a
// line of prose. Two of them:
//
//   // TYPED-EXEMPT: engine
//     This file's seam proposals were discharged by the POLYNOMIAL/LIE ENGINE
//     (MLPolyExtract + the generator/Lie dischargers), which the typed walker
//     does not have in phase B — the engine port is C2, and until it lands the
//     typed walker's composition fragment cannot see these bodies as anything
//     but hand-indexed. Exempting the file suspends assertion (a) for it, and
//     ONLY (a): a typed proposal appearing in an exempt file still has to be
//     seam-matched or pinned.
//
//   // TYPED-SUGGEST: <owner>|<group>
//     Declares a TYPED-ONLY proposal — a recall WIN over the seam (§0.1's
//     partial annotation, §0.2's closed extents). Strict both directions
//     among typed-only proposals.
//
// The exemption is FILE-scoped, not owner-scoped. That is a deliberate
// simplification with a known cost: in an exempt file, a recall miss that has
// nothing to do with the engine would also be tolerated. It is affordable only
// because the two exempt files have exactly one loose function each; if a third
// engine file with a mixed body ever arrives, the directive should take an
// owner list.

type Directives =
    { /// Reason string of a `// TYPED-EXEMPT:` line, if any.
      Exempt: string option
      /// `// TYPED-SUGGEST:` pins as (owner, group).
      Pins: (string * string) list
      /// Directive lines that did not parse — a failure, never a silent skip.
      Malformed: string list }

/// Reasons a file may claim exemption from the recall assertion. Closed on
/// purpose: a typo'd reason must fail loudly rather than exempt silently.
let exemptReasons = [ "engine" ]

let parseDirectives (source: string) : Directives =
    let exemptTok = "// TYPED-EXEMPT:"
    let suggestTok = "// TYPED-SUGGEST:"
    let mutable exempt : string option = None
    let mutable pins : (string * string) list = []
    let mutable malformed : string list = []
    for line in source.Replace("\r\n", "\n").Split('\n') do
        let t = line.Trim()
        if t.StartsWith(exemptTok, System.StringComparison.Ordinal) then
            let v = t.Substring(exemptTok.Length).Trim()
            if v = "" then malformed <- malformed @ [ t ] else exempt <- Some v
        elif t.StartsWith(suggestTok, System.StringComparison.Ordinal) then
            let v = t.Substring(suggestTok.Length).Trim()
            let parts = v.Split('|') |> Array.map (fun (s: string) -> s.Trim())
            if parts.Length = 2 && parts.[0] <> "" && parts.[1] <> "" then
                pins <- pins @ [ (parts.[0], parts.[1]) ]
            else malformed <- malformed @ [ t ]
    { Exempt = exempt; Pins = pins; Malformed = malformed }

// ============================================================================
// The differential itself
// ============================================================================

type Verdict =
    { Failures: string list
      /// Lenient observations: signature/deps drift, stale exemptions.
      Notes: string list
      Seam: int
      Typed: int
      Matched: int
      Exempted: int
      Wins: int }

let private keyOf (p: Proposal) = (p.Owner, p.Group)

/// Pure: the whole assertion matrix, with no channel or corpus involved, so
/// the self-test can drive every arm of it directly.
let diffFile (dirs: Directives) (seam: Proposal list) (typed: Proposal list) : Verdict =
    // Greedy 1:1 on (owner, group), the DiagCorpus pin-matching precedent —
    // multiset semantics, so a repeated theorem needs a repeated counterpart.
    let mutable pool = typed |> List.indexed
    let mutable pairs : (Proposal * Proposal) list = []
    let mutable missed : Proposal list = []
    for s in seam do
        match pool |> List.tryFind (fun (_, t) -> keyOf t = keyOf s) with
        | Some (i, t) ->
            pool <- pool |> List.filter (fun (j, _) -> j <> i)
            pairs <- pairs @ [ (s, t) ]
        | None -> missed <- missed @ [ s ]
    // Typed-only proposals against the TYPED-SUGGEST pins, same discipline.
    let mutable pinPool = dirs.Pins |> List.indexed
    let mutable wins = 0
    let mutable unpinned : Proposal list = []
    for (_, t) in pool do
        match pinPool |> List.tryFind (fun (_, k) -> k = keyOf t) with
        | Some (i, _) ->
            pinPool <- pinPool |> List.filter (fun (j, _) -> j <> i)
            wins <- wins + 1
        | None -> unpinned <- unpinned @ [ t ]

    let mutable failures : string list = []
    let add f = failures <- failures @ [ f ]
    for m in dirs.Malformed do
        add (sprintf "MALFORMED DIRECTIVE: %s" m)
    match dirs.Exempt with
    | Some r when not (List.contains r exemptReasons) ->
        add (sprintf "UNKNOWN TYPED-EXEMPT reason '%s' (known: %s)" r (String.concat ", " exemptReasons))
    | _ -> ()
    if dirs.Exempt.IsNone then
        for m in missed do
            add (sprintf "RECALL MISS: seam proposes '%s' under %s, typed does not" m.Owner m.Group)
    for u in unpinned do
        add (sprintf "FALSE POSITIVE: typed proposes '%s' under %s with no seam match and no TYPED-SUGGEST pin"
                 u.Owner u.Group)
    for (_, (o, g)) in pinPool do
        add (sprintf "PIN NOT PRODUCED: TYPED-SUGGEST %s|%s" o g)

    let notes =
        [ for (s, t) in pairs do
            if t.Signature <> "" && t.Signature <> s.Signature then
                yield sprintf "SIG DRIFT '%s': seam [%s] vs typed [%s]" s.Owner s.Signature t.Signature
            if t.Deps <> s.Deps then
                yield sprintf "DEPS DRIFT '%s': seam [%s] vs typed [%s]" s.Owner
                          (String.concat ", " s.Deps) (String.concat ", " t.Deps)
          if dirs.Exempt.IsSome && missed.IsEmpty then
            yield "STALE TYPED-EXEMPT: nothing to exempt (the typed walker now matches every seam proposal here)" ]

    { Failures = failures
      Notes = notes
      Seam = seam.Length
      Typed = typed.Length
      Matched = pairs.Length
      Exempted = (if dirs.Exempt.IsSome then missed.Length else 0)
      Wins = wins }

// ============================================================================
// Reading the two channels
// ============================================================================

/// Classify everything the seam's equiv channel produced for the program that
/// was just lowered. Galilean is NOT read (phase C3 owns it).
let readSeam () : SeamParse list =
    Blade.ML.Equiv.CertSuggestions.get () |> List.map fst |> List.map parseSeamMessage

/// Read the typed walker's structured proposals off its internal channel.
let readTyped () : Proposal list =
    Blade.DeduceRep.TypedCertProposals.get ()
    |> List.map (fun (p: Blade.DeduceRep.RepProposal, _span) ->
        { Owner = p.Owner; Group = p.Group; Signature = p.Signature; Deps = p.Deps })

// ============================================================================
// The block
// ============================================================================

/// A synthetic program for the end-to-end self-test: 053's uncertified layer,
/// which the seam proposes under O3. Deliberately NOT a corpus file — the
/// self-test must keep passing while the corpus files churn.
let private selfTestSource = """
import ml as ml
let static SIN = [(0, 0, 2), (1, 1, 1)]
let static SOUT = [(0, 0, 1), (1, 1, 2)]
let static WD = ml.hom_dim(SIN, SOUT)

function layer_loose(x: Array<Float like IrrepsIdx<SIN>>, w: Array<Float like Idx<WD>>)
                     -> Array<Float like IrrepsIdx<SOUT>> =
    ml.derive_linear(SIN, SOUT, w, x)

let wv = [0.5, 0.0 - 1.0, 2.0, 0.25]
let xv = [1.0, 2.0, 3.0, 0.0 - 1.0, 0.5]
let loose = layer_loose(xv, wv)
"""

let runRepDifferentialTests () : BlockResult =
    printHeader "Rep Deduction Differential"
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

    // Nothing may be inherited from whatever ran before this block.
    Blade.DeduceRep.TypedCertProposals.reset ()

    // ------------------------------------------------------------------
    // SELF-TEST: the matcher, proven against hand-staged inputs.
    //
    // Every assertion in this section is producer-INDEPENDENT: the typed
    // proposals it matches are staged by hand, so it says the same thing
    // about the harness whether the typed walker is a stub or shipped.
    //
    // THE LEAK INVARIANT. Staging is only meaningful on an empty channel:
    // once the real walker exists, every `lowerDiag` fills the typed
    // channel, and a staged proposal landing on top of a real one turns a
    // matcher arm into a surplus-proposal failure that has nothing to do
    // with the arm being tested. So: NO STAGING WITHOUT A PRECEDING RESET
    // THAT FOLLOWS THE LAST `lowerDiag`. The arms below stage nothing at
    // all (they call `diffFile` directly on hand-built lists), the channel
    // section resets immediately before it stages, and the end-to-end case
    // stages nothing by design — it is the real walker's output that is
    // being read there.
    // ------------------------------------------------------------------
    printSubHeader "Self-test: matcher"

    let prop o g s d = { Owner = o; Group = g; Signature = s; Deps = d }
    let noDirs = { Exempt = None; Pins = []; Malformed = [] }

    // 1. The plain deduction shape (corpus 053's message, verbatim).
    let msg053 =
        "function 'layer_loose' judges equivariant under O3: add 'where ml.equiv(O3)' "
        + "[signature: x transforms as IrrepsIdx<[(0, 0, 2), (1, 1, 1)]>, w invariant "
        + "-> IrrepsIdx<[(0, 0, 1), (1, 1, 2)]>]"
    match parseSeamMessage msg053 with
    | SeamProposal p ->
        let ok =
            p.Owner = "layer_loose" && p.Group = "O3" && p.Deps.IsEmpty
            && p.Signature = "x transforms as IrrepsIdx<[(0, 0, 2), (1, 1, 1)]>, w invariant "
                             + "-> IrrepsIdx<[(0, 0, 1), (1, 1, 2)]>"
        check "seam parse: deduction shape" ok
            (if ok then "owner, group, and the bracket-bearing signature survive"
             else sprintf "owner='%s' group='%s' sig='%s'" p.Owner p.Group p.Signature)
    | other -> check "seam parse: deduction shape" false (sprintf "classified as %A" other)

    // 2. The dependency-closure tail, and the point-group vocabulary.
    let msgClosure =
        "function 'chain' judges equivariant under C4: add 'where ml.equiv(C4)' "
        + "[signature: x transforms as PgIrrepsIdx<C4, [(\"E\", 1)]> -> PgIrrepsIdx<C4, [(\"E\", 1)]>]"
        + " (also requires pinning: layer1, layer2)"
    match parseSeamMessage msgClosure with
    | SeamProposal p ->
        let ok = p.Owner = "chain" && p.Group = "C4" && p.Deps = [ "layer1"; "layer2" ]
                 && p.Signature.EndsWith "PgIrrepsIdx<C4, [(\"E\", 1)]>"
        check "seam parse: closure tail and point group" ok
            (if ok then "deps split, signature not truncated by the tail"
             else sprintf "group='%s' deps=[%s] sig='%s'" p.Group (String.concat ", " p.Deps) p.Signature)
    | other -> check "seam parse: closure tail and point group" false (sprintf "classified as %A" other)

    // 3. The upgrade lint is EXCLUDED by shape, not by accident.
    let msgLint =
        "function 'layer_weak' is pinned ml.equiv(SO3) but judges under O3: "
        + "the stronger certificate is available"
    check "seam parse: upgrade lint excluded"
        (match parseSeamMessage msgLint with SeamUpgradeLint -> true | _ -> false)
        "the E4 lint proposes an edit, not a pin, so it is not a deduction"

    // 4. Negative control: a message that starts like a proposal and is not one
    //    (the galilean phrasing) must not be silently read as a deduction.
    check "seam parse: unrecognized shape is reported, not dropped"
        (match parseSeamMessage "function 'shear' judges boost-invariant with velocity parameter(s) u" with
         | SeamUnparseable _ -> true | _ -> false)
        "an unparseable string classifies as such, and the summary asserts none occur"

    // 5. Directive parsing.
    let dirSource =
        "// TEST: x\n// TYPED-EXEMPT: engine\n   // TYPED-SUGGEST: scale_partial|O3\n"
        + "// a normal comment\n// TYPED-SUGGEST: helper|C4\nfunction f() = 1\n"
    let dirs = parseDirectives dirSource
    check "directives: exempt and pins parse"
        (dirs.Exempt = Some "engine"
         && dirs.Pins = [ ("scale_partial", "O3"); ("helper", "C4") ]
         && dirs.Malformed.IsEmpty)
        "whole-line, trimmed, exact-prefix; indentation and ordinary comments are transparent"

    let badDirs = parseDirectives "// TYPED-SUGGEST: no_pipe_here\n// TYPED-EXEMPT:\n"
    check "directives: malformed lines are failures, not skips"
        (badDirs.Pins.IsEmpty && badDirs.Exempt.IsNone && badDirs.Malformed.Length = 2)
        "a pin without <owner>|<group> and an empty exemption both land in Malformed"

    // 6. The assertion matrix, arm by arm.
    let seamA = [ prop "f" "O3" "x transforms as R -> R" [] ]
    let typedA = [ prop "f" "O3" "x transforms as R -> R" [] ]
    let vA = diffFile noDirs seamA typedA
    check "diff: agreement passes"
        (vA.Failures.IsEmpty && vA.Matched = 1 && vA.Wins = 0 && vA.Notes.IsEmpty)
        "same owner, same group, same rendering"

    let vB = diffFile noDirs seamA []
    check "diff: recall miss fails"
        (vB.Failures.Length = 1 && vB.Failures.Head.StartsWith "RECALL MISS" && vB.Matched = 0)
        "a seam proposal the typed walker does not make is the gate's whole point"

    let vC = diffFile { noDirs with Exempt = Some "engine" } seamA []
    check "diff: TYPED-EXEMPT suspends recall"
        (vC.Failures.IsEmpty && vC.Exempted = 1)
        "engine-discharged proposals are out of scope until the C2 port"

    let vC2 = diffFile { noDirs with Exempt = Some "typo" } seamA []
    check "diff: unknown exemption reason fails"
        (vC2.Failures |> List.exists (fun f -> f.StartsWith "UNKNOWN TYPED-EXEMPT"))
        "the reason vocabulary is closed, so a typo cannot exempt silently"

    let vD = diffFile noDirs [] typedA
    check "diff: unpinned typed-only proposal is a false positive"
        (vD.Failures.Length = 1 && vD.Failures.Head.StartsWith "FALSE POSITIVE")
        "zero false proposals is half the ship criterion"

    let vE = diffFile { noDirs with Pins = [ ("f", "O3") ] } [] typedA
    check "diff: pinned typed-only proposal is a WIN"
        (vE.Failures.IsEmpty && vE.Wins = 1 && vE.Matched = 0)
        "a declared recall win over the seam"

    let vF = diffFile { noDirs with Pins = [ ("g", "SO3") ] } [] []
    check "diff: a pin that produces nothing fails"
        (vF.Failures.Length = 1 && vF.Failures.Head.StartsWith "PIN NOT PRODUCED")
        "pins are strict in both directions"

    // A win pin must not launder a DIFFERENT theorem: same owner, wrong group
    // is both a recall miss and a false positive, and neither is forgiven.
    let vG = diffFile { noDirs with Pins = [ ("f", "SO3") ] } seamA [ prop "f" "SO3" "" [] ]
    check "diff: group disagreement is not laundered by a pin"
        (vG.Failures |> List.exists (fun f -> f.StartsWith "RECALL MISS"))
        "seam O3 vs typed SO3 leaves the seam proposal unmatched"

    let vH =
        diffFile noDirs
            [ prop "f" "O3" "x transforms as R -> R" [ "h" ] ]
            [ prop "f" "O3" "x: Rep -> Rep" [] ]
    check "diff: signature and deps drift are lenient"
        (vH.Failures.IsEmpty && vH.Matched = 1 && vH.Notes.Length = 2
         && vH.Notes |> List.exists (fun n -> n.StartsWith "SIG DRIFT")
         && vH.Notes |> List.exists (fun n -> n.StartsWith "DEPS DRIFT"))
        "two IRs render differently; the theorem is what must agree"

    // ------------------------------------------------------------------
    // SELF-TEST: the channels, end to end.
    // ------------------------------------------------------------------
    printSubHeader "Self-test: channels"

    // The typed channel: staged by hand, read back through the same accessor
    // the corpus loop uses, then reset — the CertSuggestions lifecycle.
    Blade.DeduceRep.TypedCertProposals.reset ()
    let span0 : Blade.Ast.Span = Blade.Ast.noSpan
    Blade.DeduceRep.TypedCertProposals.add
        { Owner = "staged_a"; Group = "O3"; Signature = "x transforms as R -> R"; Deps = [] } span0
    Blade.DeduceRep.TypedCertProposals.add
        { Owner = "staged_b"; Group = "C4"; Signature = ""; Deps = [ "staged_a" ] } span0
    let staged = readTyped ()
    check "typed channel: add/get round-trip in decl order"
        (staged |> List.map (fun p -> (p.Owner, p.Group)) = [ ("staged_a", "O3"); ("staged_b", "C4") ]
         && (staged |> List.item 1).Deps = [ "staged_a" ])
        "the reader maps RepProposal onto the differential's vocabulary unchanged"
    Blade.DeduceRep.TypedCertProposals.reset ()
    check "typed channel: reset clears" (readTyped ()).IsEmpty
        "so a corpus file can never inherit its predecessor's proposals"

    // BOTH channels, live, on one synthetic program, with NOTHING staged.
    //
    // This is the only producer-DEPENDENT assertion in the block, and it is
    // deliberately so: it runs the real pipeline over a program neither corpus
    // sweep owns and asserts that the two walkers reach the same theorem about
    // it. The earlier version of this case staged a typed proposal by hand to
    // stand in for the missing producer; once the producer shipped, that stand-
    // in became a SECOND proposal against one seam proposal and read as a
    // surplus. Removing the staging is not a weakening — the same assertion now
    // rests on the real walker, which is what it was always pretending to test.
    Blade.DeduceRep.TypedCertProposals.reset ()
    let _ = Lowering.lowerDiag None selfTestSource
    let selfSeam = readSeam ()
    let selfProposals = selfSeam |> List.choose (function SeamProposal p -> Some p | _ -> None)
    let selfTyped = readTyped ()
    let seamOk =
        selfProposals |> List.map (fun p -> (p.Owner, p.Group)) = [ ("layer_loose", "O3") ]
    check "seam channel: a live program's proposal parses" seamOk
        (if seamOk then "one O3 proposal for 'layer_loose', off the live BL4011 channel"
         else sprintf "read %d message(s): %s" selfSeam.Length
                  (selfSeam |> List.map (sprintf "%A") |> String.concat " ; "))
    let vLive = diffFile noDirs selfProposals selfTyped
    let renderTyped (ps: Proposal list) =
        if ps.IsEmpty then "none"
        else ps |> List.map (fun p -> sprintf "%s|%s" p.Owner p.Group) |> String.concat ", "
    check "end-to-end: both walkers agree on a live program"
        (seamOk && vLive.Failures.IsEmpty && vLive.Matched = 1)
        (if seamOk && vLive.Failures.IsEmpty && vLive.Matched = 1 then
            sprintf "seam and typed both propose 'layer_loose' under O3%s"
                (if vLive.Notes.IsEmpty then "" else " (" + String.concat " ; " vLive.Notes + ")")
         else
            sprintf "typed proposed: %s ; %s" (renderTyped selfTyped)
                (String.concat " ; " vLive.Failures))
    Blade.DeduceRep.TypedCertProposals.reset ()

    // ------------------------------------------------------------------
    // THE GATE: the whole ml-equiv corpus, file by file.
    // ------------------------------------------------------------------
    printSubHeader "Differential: ml-equiv corpus"

    let mutable totSeam = 0
    let mutable totTyped = 0
    let mutable totMatched = 0
    let mutable totExempt = 0
    let mutable totWins = 0
    let mutable totLint = 0
    let mutable unparsedAll : string list = []

    for (name, source) in Corpus.category "ml-equiv" do
        let dirs = parseDirectives source
        // Defensive reset: the typed producer resets on entry, but a file that
        // never reaches typecheck (a reject-probe) would otherwise be read
        // against its predecessor's leftovers.
        Blade.DeduceRep.TypedCertProposals.reset ()
        // Run for the side effect on both channels. A source the checker
        // REFUSES is fine: the elaborator clears the channel per program and
        // only fills it when the judgment found something, so a reject-probe
        // legitimately yields zero on both sides.
        let _ = Lowering.lowerDiag None source
        let parses = readSeam ()
        let seam = parses |> List.choose (function SeamProposal p -> Some p | _ -> None)
        let lint = parses |> List.filter (function SeamUpgradeLint -> true | _ -> false) |> List.length
        let unparsed = parses |> List.choose (function SeamUnparseable m -> Some m | _ -> None)
        let typed = readTyped ()
        let v = diffFile dirs seam typed
        totSeam <- totSeam + v.Seam
        totTyped <- totTyped + v.Typed
        totMatched <- totMatched + v.Matched
        totExempt <- totExempt + v.Exempted
        totWins <- totWins + v.Wins
        totLint <- totLint + lint
        unparsedAll <- unparsedAll @ unparsed
        let summary =
            [ yield sprintf "seam %d / typed %d, %d matched" v.Seam v.Typed v.Matched
              if v.Exempted > 0 then yield sprintf "%d exempt (engine)" v.Exempted
              if v.Wins > 0 then yield sprintf "%d typed-only win(s)" v.Wins
              if lint > 0 then yield sprintf "%d upgrade lint(s) excluded" lint
              for u in unparsed -> sprintf "UNCLASSIFIED SEAM MESSAGE: %s" u
              yield! v.Notes ]
            |> String.concat " ; "
        let detail =
            if v.Failures.IsEmpty then summary
            else (v.Failures @ [ summary ]) |> String.concat " ; "
        check name v.Failures.IsEmpty detail
    Blade.DeduceRep.TypedCertProposals.reset ()

    // Harness-health assertions. Both guard against a SILENT gate: if the seam
    // message shape drifts, every file parses zero proposals, every recall
    // assertion is vacuously satisfied, and the block goes green while checking
    // nothing at all.
    check "gate is not vacuous: the seam channel produced deduction-shaped proposals"
        (totSeam > 0) (sprintf "%d seam proposal(s) across the category" totSeam)
    check "every seam message classified as a deduction or as the upgrade lint"
        unparsedAll.IsEmpty
        (if unparsedAll.IsEmpty then sprintf "%d deduction(s), %d lint(s)" totSeam totLint
         else unparsedAll |> String.concat " ; ")

    printFooter "Rep Deduction Differential"
        [ sprintf "%d passed" passed
          sprintf "%d failure(s)" failed
          sprintf "seam %d" totSeam
          sprintf "typed %d" totTyped
          sprintf "%d matched" totMatched
          sprintf "%d exempt" totExempt
          sprintf "%d win(s)" totWins ]
    { Block = "Rep Deduction Differential"
      Passed = passed
      Failed = failed
      Skipped = 0
      FailedNames = failedNames }
