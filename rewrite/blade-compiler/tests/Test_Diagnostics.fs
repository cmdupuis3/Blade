// Diagnostics-core tests: renderer golden shapes (no color, deterministic),
// SourceMap file resolution, and the BLxxxx registry contract (well-formed,
// banded, unique). The corpus-driven diagnostics block (pinning codes/spans
// against real .blade sources) lives with the runner; this file covers the
// Diagnostics.fs machinery itself.
module Blade.Tests.DiagnosticsCore

open Blade.Ast
open Blade.Diagnostics
open Blade.Tests.TestHarness

let runDiagnosticsCoreTests () : BlockResult =
    printHeader "Diagnostics Core Tests"
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

    let span file sl sc el ec =
        { StartLine = sl; StartCol = sc; EndLine = el; EndCol = ec; File = file }

    // -- registry contract ------------------------------------------------
    let codes = Codes.registry |> Map.toList |> List.map fst
    check "registry codes are well-formed BLxxxx"
        (codes |> List.forall (fun c ->
            c.Length = 6 && c.StartsWith "BL" && c.Substring 2 |> Seq.forall System.Char.IsDigit))
        (sprintf "%d codes" codes.Length)
    check "registry titles are non-empty"
        (Codes.registry |> Map.toList |> List.forall (fun (_, t) -> t <> ""))
        ""
    check "elaborator codes registered"
        ([ "ml"; "ppl"; "math"; "rand"; "spectra"; "grad" ]
         |> List.forall (fun s -> Codes.isRegistered (Codes.elaboratorCode s)))
        ""
    check "constructor helpers emit registered codes"
        ([ (Codes.ice "x").Code; (Codes.iceCodegen "x").Code
           (Codes.backendLimit noSpan "x").Code ]
         |> List.forall Codes.isRegistered)
        ""

    // -- renderShort mirrors the legacy formatCompileError shape ----------
    let d1 =
        mkError "BL2001" PhResolve (span None 3 5 3 12) "Unbound variable: zz"
        |> withContext [ "in function 'f'" ]
    check "renderShort: line:col prefix, message, indented context"
        (Render.renderShort d1 = "3:5: Unbound variable: zz\n  in function 'f'")
        (sprintf "got: %s" (Render.renderShort d1))
    let d2 = mkError "BL9001" PhInternal noSpan "boom"
    check "renderShort: noSpan drops the location entirely"
        (Render.renderShort d2 = "boom")
        (sprintf "got: %s" (Render.renderShort d2))
    let d3 = mkError "BL2001" PhResolve (span (Some "a.blade") 2 1 2 3) "msg"
    check "renderShort: file-qualified location"
        (Render.renderShort d3 = "a.blade:2:1: msg")
        (sprintf "got: %s" (Render.renderShort d3))

    // -- render: header, arrow line, snippet, underline -------------------
    let sm = SourceMap.ofSources [ "a.blade", "let a = 1\nlet b = zz + 1\nlet c = 2" ]
    let d4 = mkError "BL2001" PhResolve (span (Some "a.blade") 2 9 2 11) "Unbound variable: zz"
    let rendered = Render.render false (Some sm) d4
    let lines = rendered.Split '\n'
    check "render: header line is error[CODE]: message"
        (lines.[0] = "error[BL2001]: Unbound variable: zz")
        (sprintf "got: %s" lines.[0])
    check "render: arrow line carries file:line:col"
        (lines.[1].Trim() = "--> a.blade:2:9")
        (sprintf "got: %s" lines.[1])
    check "render: snippet shows the offending source line"
        (rendered.Contains "2 | let b = zz + 1")
        (sprintf "got:\n%s" rendered)
    check "render: underline covers the span (2 carets at col 9)"
        (lines |> Array.exists (fun l -> l.EndsWith "        ^^"))
        (sprintf "got:\n%s" rendered)

    // -- render: File=None span resolves against a single-file map --------
    let d5 = mkError "BL2001" PhResolve (span None 2 9 2 11) "Unbound variable: zz"
    check "render: File=None finds the sole file in the SourceMap"
        ((Render.render false (Some sm) d5).Contains "let b = zz")
        ""
    let sm2 = SourceMap.addFile "b.blade" "other" sm
    check "render: File=None with a multi-file map degrades to no snippet"
        (not ((Render.render false (Some sm2) d5).Contains "let b = zz"))
        ""

    // -- render: degradation ----------------------------------------------
    check "render: no SourceMap still shows header + location"
        (let r = Render.render false None d4 in
         r.Contains "error[BL2001]" && r.Contains "a.blade:2:9" && not (r.Contains "let b"))
        ""
    check "render: noSpan renders header only"
        (Render.render false None d2 = "error[BL9001]: boom")
        (sprintf "got: %s" (Render.render false None d2))
    let d6 = d4 |> withNote "did you mean 'z'?"
    check "render: notes appear as '= note:' lines"
        ((Render.render false (Some sm) d6).Contains "= note: did you mean 'z'?")
        ""
    // Stale span past the end of the line must clamp, not throw.
    let d7 = mkError "BL2001" PhResolve (span (Some "a.blade") 2 40 2 60) "clamped"
    check "render: out-of-range columns clamp without throwing"
        ((Render.render false (Some sm) d7).Contains "let b")
        ""
    // Multi-line span underlines from start col to end of the first line.
    let d8 = mkError "BL2001" PhResolve (span (Some "a.blade") 2 9 3 4) "multi"
    check "render: multi-line span underlines to end of first line"
        ((Render.render false (Some sm) d8).Contains "^^^^^^")
        (sprintf "got:\n%s" (Render.render false (Some sm) d8))

    // -- severities --------------------------------------------------------
    let dw = { d4 with Severity = SevWarning }
    check "render: warning severity label"
        ((Render.render false (Some sm) dw).StartsWith "warning[BL2001]:")
        ""

    // -- color mode: ANSI present when enabled, absent when not -----------
    check "render: color mode emits ANSI escapes"
        ((Render.render true (Some sm) d4).Contains "[")
        ""
    check "render: plain mode emits no ANSI escapes"
        (not ((Render.render false (Some sm) d4).Contains "["))
        ""

    printFooter "Diagnostics Core" [sprintf "%d passed" passed; sprintf "%d failure(s)" failed]
    { Block = "Diagnostics Core"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
