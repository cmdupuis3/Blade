// Diagnostics corpus: .blade sources under tests/corpus/diagnostics that must
// FAIL to compile with pinned BLxxxx codes (and optionally spans / message
// substrings). Strict in both directions: every // ERROR: pin must match a
// distinct produced diagnostic, and every produced diagnostic must be claimed
// by some pin — so codes and spans cannot silently drift.
module Blade.Tests.DiagCorpus

open Blade
open Blade.Tests.TestHarness
open Blade.Tests.Expect

let runDiagCorpusTests () : BlockResult =
    printHeader "Diagnostics Corpus"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames : string list = []
    let check name ok detail =
        if ok then
            passed <- passed + 1
            resultLine Pass name detail
        else
            failed <- failed + 1
            failedNames <- failedNames @ [name]
            resultLine Fail name detail

    let matchesPin (p: DiagPin) (d: Diagnostics.Diagnostic) =
        d.Code = p.PinCode
        && (match p.PinStart with
            | Some (l, c) -> d.Span.StartLine = l && d.Span.StartCol = c
            | None -> true)
        && (match p.PinEnd with
            | Some (l, c) -> d.Span.EndLine = l && d.Span.EndCol = c
            | None -> true)

    for (name, source) in Corpus.category "diagnostics" do
        let (pins, contains) = parseDiagPins source
        let result, _sm = Lowering.lowerDiag None source
        match result with
        | Ok _ ->
            check name false "expected diagnostics but the source compiled cleanly"
        | Error diags ->
            // Greedy 1:1 matching, pins -> diagnostics.
            let mutable remaining = diags
            let mutable unmatchedPins : DiagPin list = []
            for p in pins do
                match remaining |> List.tryFindIndex (matchesPin p) with
                | Some i ->
                    remaining <- remaining |> List.indexed |> List.filter (fun (j, _) -> j <> i) |> List.map snd
                | None -> unmatchedPins <- unmatchedPins @ [p]
            let missingContains =
                contains |> List.filter (fun s -> not (diags |> List.exists (fun d -> d.Message.Contains s)))
            let ok =
                not pins.IsEmpty && unmatchedPins.IsEmpty && remaining.IsEmpty && missingContains.IsEmpty
            let detail =
                if ok then sprintf "%d diagnostic(s) as pinned" diags.Length
                else
                    [ if pins.IsEmpty then yield "file has no // ERROR: pins"
                      for p in unmatchedPins ->
                        sprintf "pin %s%s matched nothing" p.PinCode
                            (match p.PinStart with Some (l, c) -> sprintf " @ %d:%d" l c | None -> "")
                      for d in remaining ->
                        sprintf "UNPINNED %s @ %d:%d: %s" d.Code d.Span.StartLine d.Span.StartCol d.Message
                      for s in missingContains -> sprintf "no message contains '%s'" s ]
                    |> String.concat " ; "
            check name ok detail

    printFooter "Diagnostics Corpus" [sprintf "%d passed" passed; sprintf "%d failure(s)" failed]
    { Block = "Diagnostics Corpus"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }

/// Stage-6a equivariance-certificate suggestions (BL4011), pinned over the
/// whole `ml-equiv` corpus category, STRICT IN BOTH DIRECTIONS: every
/// `// SUGGEST: <message>` line must match a produced suggestion exactly, and
/// every produced suggestion must be claimed by a pin.
///
/// The both-directions rule is what makes an ABSENCE assertable, which the
/// value corpus cannot do (a suggestion is a warning; warnings change no value
/// and fail no value test). A file with no `// SUGGEST:` lines asserts SILENCE
/// — that is corpus 057's whole content, and, incidentally, a standing lock on
/// the zero-suggestion property of every pre-6a file in the category.
///
/// Suggestions are read off the `Equiv.CertSuggestions` side-channel rather
/// than scraped from a warning string, so the message text pinned here is the
/// same text the editor ghost-renders.
let runCertSuggestTests () : BlockResult =
    printHeader "Equiv Certificate Suggestions"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames : string list = []
    let check name ok detail =
        if ok then
            passed <- passed + 1
            resultLine Pass name detail
        else
            failed <- failed + 1
            failedNames <- failedNames @ [name]
            resultLine Fail name detail

    let pinsOf (source: string) =
        source.Replace("\r\n", "\n").Split('\n')
        |> Array.toList
        |> List.choose (fun (l: string) ->
            let t = l.Trim()
            if t.StartsWith "// SUGGEST: " then Some (t.Substring(12).Trim()) else None)

    for (name, source) in Corpus.category "ml-equiv" do
        let pins = pinsOf source
        // The pipeline is run for its side effect on the suggestion channel.
        // A source the checker REFUSES is fine here: the elaborator resets the
        // channel per program and only fills it when the equiv judgment found
        // nothing to report, so a reject-probe legitimately yields zero.
        let _ = Lowering.lowerDiag None source
        let produced = Blade.ML.Equiv.CertSuggestions.get () |> List.map fst
        let mutable remaining = produced
        let mutable unmatched : string list = []
        for p in pins do
            match remaining |> List.tryFindIndex ((=) p) with
            | Some i ->
                remaining <- remaining |> List.indexed |> List.filter (fun (j, _) -> j <> i) |> List.map snd
            | None -> unmatched <- unmatched @ [p]
        let ok = unmatched.IsEmpty && remaining.IsEmpty
        let detail =
            if ok then
                if pins.IsEmpty then "no suggestions (asserted)"
                else sprintf "%d suggestion(s) as pinned" pins.Length
            else
                [ for p in unmatched -> sprintf "PIN NOT PRODUCED: %s" p
                  for r in remaining -> sprintf "UNPINNED SUGGESTION: %s" r ]
                |> String.concat " ; "
        check name ok detail

    printFooter "Equiv Certificate Suggestions" [sprintf "%d passed" passed; sprintf "%d failure(s)" failed]
    { Block = "Equiv Certificate Suggestions"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
