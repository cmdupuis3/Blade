// Diagnostics-core tests: renderer golden shapes (no color, deterministic),
// SourceMap file resolution, and the BLxxxx registry contract (well-formed,
// banded, unique). The corpus-driven diagnostics block (pinning codes/spans
// against real .blade sources) lives with the runner; this file covers the
// Diagnostics.fs machinery itself.
//
// Plus one RUNTIME-diagnostic tripwire at the end: the source-tree invariant
// that keeps the Blade call stack in a runtime panic complete. See the section
// comment there — it guards a codegen optimization, not the renderer.
module Blade.Tests.DiagnosticsCore

open System
open System.IO
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

    // -- build-output provenance -------------------------------------------
    // A diagnostic landing in the stdlib copy deployed beside the binary is
    // almost never a defect in the user's program: it means that copy and the
    // compiler are out of step. The raw message gives no hint of that -- it
    // names a path the user never wrote and cannot fix by editing -- so the
    // renderer synthesizes a note from the span.
    //
    // The SCOPE is the delicate half, which is what the negative cases are for.
    // "Anywhere under AppContext.BaseDirectory" is the obvious predicate and is
    // wrong: tests/corpus is deployed there too, so a suite run from the output
    // directory would decorate every golden diagnostic with an irrelevant note.
    let deployed rel = Path.Combine(AppContext.BaseDirectory, rel)
    check "buildOutputNote fires inside the deployed stdlib copy"
        ((buildOutputNote (span (Some (deployed (Path.Combine("stdlib", "units", "SI.blade")))) 3 1 3 4)).IsSome)
        "the case that produced a bogus error pointing into bin/"
    check "buildOutputNote is silent on the deployed corpus"
        ((buildOutputNote (span (Some (deployed (Path.Combine("tests", "corpus", "units", "077.blade")))) 3 1 3 4)).IsNone)
        "goldens must not acquire a note when the suite runs from bin/"
    check "buildOutputNote is silent on a user's own file"
        ((buildOutputNote (span (Some (Path.Combine("C:", "work", "prog.blade"))) 1 1 1 2)).IsNone
         && (buildOutputNote (span None 1 1 1 2)).IsNone)
        "ordinary diagnostics are untouched, spanless ones too"

    // -- registry contract ------------------------------------------------
    // Read the source LIST, not the Map: Map.ofList keeps the last entry for a
    // repeated key, so a double-booked code is already invisible by the time
    // it reaches Codes.registry (BL4007 carried two unrelated titles this way).
    let codes = Codes.registryEntries |> List.map fst
    check "registry codes are well-formed BLxxxx"
        (codes |> List.forall (fun c ->
            c.Length = 6 && c.StartsWith "BL" && c.Substring 2 |> Seq.forall System.Char.IsDigit))
        ($"{codes.Length} codes")
    check "registry titles are non-empty"
        (Codes.registryEntries |> List.forall (fun (_, t) -> t <> ""))
        ""
    let dupCodes =
        codes
        |> List.countBy id
        |> List.filter (fun (_, n) -> n > 1)
        |> List.map fst
    check "registry codes are unique (no code claimed twice)"
        (List.isEmpty dupCodes)
        (if List.isEmpty dupCodes then $"{codes.Length} codes"
         else $"""duplicated: {(String.concat ", " dupCodes)}""")
    check "every registry entry survives into the lookup Map"
        (codes.Length = Map.count Codes.registry)
        ($"{codes.Length} entries, {(Map.count Codes.registry)} map keys")

    // -- banding ------------------------------------------------------------
    // The band digit IS the phase: BL0xxx lex, BL1xxx parse, ... BL9xxx
    // internal. The expected mapping is written out HERE rather than read back
    // from Diagnostics.fs, so this is a real cross-check of phaseOfCode (and
    // of every code's placement) rather than a restatement of the source.
    let expectedPhase (code: string) : Phase option =
        match code.[2] with
        | '0' -> Some PhLex
        | '1' -> Some PhParse
        | '2' -> Some PhResolve
        | '3' -> Some PhTypes
        | '4' -> Some PhConstraints
        | '5' ->
            // The elaborator band carries a per-code stage name.
            match code with
            | "BL5000" -> Some (PhElaborate "ml")
            | "BL5100" -> Some (PhElaborate "ppl")
            | "BL5200" -> Some (PhElaborate "math")
            | "BL5300" -> Some (PhElaborate "rand")
            | "BL5400" -> Some (PhElaborate "spectra")
            | "BL5500" -> Some (PhElaborate "grad")
            | "BL5501" -> Some (PhElaborate "grad")   // jvp runs inside the grad pass
            | "BL5502" -> Some (PhElaborate "grad")   // the pack unroller runs there too
            | "BL5600" -> Some (PhElaborate "sgs")
            | "BL5700" -> Some (PhElaborate "display")
            | _ -> None      // an unlisted BL5xxx is a banding gap, not a pass
        | '6' -> Some PhIRValidate
        | '7' -> Some PhBackend
        | '8' -> Some PhRuntime
        | '9' -> Some PhInternal
        | _ -> None
    let bandMisfits =
        codes |> List.filter (fun c ->
            match expectedPhase c with
            | Some want -> Codes.phaseOfCode c <> want
            | None -> true)
    check "registry codes are banded (phaseOfCode agrees with the band digit for every code)"
        (List.isEmpty bandMisfits)
        (if List.isEmpty bandMisfits then $"{codes.Length} codes across 10 bands"
         else bandMisfits
              |> List.map (fun c -> sprintf "%s -> %A" c (Codes.phaseOfCode c))
              |> String.concat ", ")
    // Bands stay CONTIGUOUS in the source list: all codes are 6 chars, so
    // ascending string order is ascending numeric order. A new code appended at
    // the end of the file instead of inside its band breaks this.
    check "registry entries are listed in ascending code order (bands contiguous)"
        (codes = List.sort codes)
        (let firstBreak =
            codes |> List.pairwise |> List.tryFind (fun (a, b) -> a >= b)
         match firstBreak with
         | Some (a, b) -> $"out of order at {a} -> {b}"
         | None -> "ascending")
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
        ($"got: {(Render.renderShort d1)}")
    let d2 = mkError "BL9001" PhInternal noSpan "boom"
    check "renderShort: noSpan drops the location entirely"
        (Render.renderShort d2 = "boom")
        ($"got: {(Render.renderShort d2)}")
    let d3 = mkError "BL2001" PhResolve (span (Some "a.blade") 2 1 2 3) "msg"
    check "renderShort: file-qualified location"
        (Render.renderShort d3 = "a.blade:2:1: msg")
        ($"got: {(Render.renderShort d3)}")

    // -- render: header, arrow line, snippet, underline -------------------
    let sm = SourceMap.ofSources [ "a.blade", "let a = 1\nlet b = zz + 1\nlet c = 2" ]
    let d4 = mkError "BL2001" PhResolve (span (Some "a.blade") 2 9 2 11) "Unbound variable: zz"
    let rendered = Render.render false (Some sm) d4
    let lines = rendered.Split '\n'
    check "render: header line is error[CODE]: message"
        (lines.[0] = "error[BL2001]: Unbound variable: zz")
        ($"got: {lines.[0]}")
    check "render: arrow line carries file:line:col"
        (lines.[1].Trim() = "--> a.blade:2:9")
        ($"got: {lines.[1]}")
    check "render: snippet shows the offending source line"
        (rendered.Contains "2 | let b = zz + 1")
        ($"got:\n{rendered}")
    check "render: underline covers the span (2 carets at col 9)"
        (lines |> Array.exists (fun l -> l.EndsWith "        ^^"))
        ($"got:\n{rendered}")

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
        ($"got: {(Render.render false None d2)}")
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
    // Line 2 is `let b = zz + 1` (14 cols) and the span starts at col 9, so the
    // underline is cols 9..14 = EXACTLY 6 carets. The anchored form (8 leading
    // spaces, matching the col-9 start, then the carets) is what pins that:
    // a bare `.Contains "^^^^^^"` passes for any run of 6 or more, i.e. for an
    // off-by-one or an unclamped span that ran past the line.
    let d8 = mkError "BL2001" PhResolve (span (Some "a.blade") 2 9 3 4) "multi"
    let rendered8 = Render.render false (Some sm) d8
    check "render: multi-line span underlines to end of first line (exactly 6 carets at col 9)"
        (rendered8.Split '\n' |> Array.exists (fun l -> l.EndsWith "        ^^^^^^"))
        ($"got:\n{rendered8}")

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

    // -- panic containment (shadow-frame elision tripwire) ------------------
    // `blade_rt::panic` is the ONLY reader of the BLADE_FRAME shadow stack, and
    // `blade_rt::Scope` its only writer, so codegen is free to omit the frame
    // from an emitted body it can prove never panics (CodeGen's
    // resolveShadowFrames). That proof is TEXTUAL: it inspects the calls the
    // emitted body makes, and is blind to the inline and template runtime code
    // a call-free body still executes anyway — Array::operator[] in
    // nested_array_types.hpp, the nested-array and orbit/wreath utilities, the
    // linalg/lapack shims, rand_runtime.hpp. If any of those ever gained a
    // panic, kernels compiled without a frame would panic FRAMELESS: every
    // BL-panic trace through such a kernel silently loses a link in its call
    // chain, and no value test can see it (the values are unchanged; only the
    // stack print degrades). So the invariant is: panic is defined in
    // blade_runtime.hpp and called only from generated code.
    //
    // The check is the grep the invariant is written as — no src/cpp file but
    // blade_runtime.hpp contains `blade_rt::panic`. It is stated at both ends
    // already (the header comment above the panic definition, and the doc
    // comment on codegen's frame-elision helper); this is what enforces it.
    let cppRoots =
        [ Path.Combine(".", "src", "cpp")               // source tree, from the repo root
          Path.Combine(AppContext.BaseDirectory, "cpp") ]  // copy deployed next to the binary
    match cppRoots |> List.tryFind Directory.Exists with
    | None ->
        check "runtime C++ sources located for the panic-containment scan" false
            ($"""no cpp directory found; looked in {(cppRoots |> List.map Path.GetFullPath |> String.concat " ; ")}""")
    | Some root ->
        let sources =
            Directory.GetFiles root
            |> Array.filter (fun p ->
                match (Path.GetExtension p).ToLowerInvariant() with
                | ".hpp" | ".h" | ".cpp" | ".cu" | ".cuh" -> true
                | _ -> false)
            |> Array.sort
            |> Array.toList
        // Anti-vacuity: a scan that found nothing — wrong root, renamed or
        // un-deployed headers — would "pass" by looking at zero files. Pin the
        // headers whose inline code a frameless kernel body actually runs, so
        // the grep below has to have covered them.
        let mustScan =
            [ "blade_runtime.hpp"; "nested_array_types.hpp"; "nested_array_utilities.hpp"
              "orbit_wreath_utilities.hpp"; "blade_linalg.hpp"; "blade_linalg_views.hpp"
              "blade_lapack.hpp"; "linearized_storage.hpp"; "rand_runtime.hpp"
              "index_types.h" ]
        let scanned = sources |> List.map Path.GetFileName |> Set.ofList
        let unscanned = mustScan |> List.filter (fun f -> not (scanned.Contains f))
        check "panic-containment scan covers every runtime header an emitted body can reach"
            (List.isEmpty unscanned)
            (if List.isEmpty unscanned then $"{sources.Length} files under {root}"
             else $"""not found under {root}: {(String.concat ", " unscanned)} (the scan below would pass vacuously)""")
        let offenders =
            sources
            |> List.filter (fun p -> Path.GetFileName p <> "blade_runtime.hpp")
            |> List.choose (fun p ->
                let hits =
                    File.ReadAllLines p
                    |> Array.indexed
                    |> Array.filter (fun (_, line) -> line.Contains "blade_rt::panic")
                    |> Array.map (fun (i, _) -> string (i + 1))
                if hits.Length = 0 then None
                else Some ($"""{(Path.GetFileName p)}:{(String.concat "," hits)}"""))
        check "blade_rt::panic is reached only from generated code (no runtime header but blade_runtime.hpp)"
            (List.isEmpty offenders)
            (if List.isEmpty offenders then
                $"{sources.Length} file(s) scanned under {root}"
             else
                sprintf "panic now reachable from %s. This BREAKS SHADOW-FRAME ELISION: codegen omits BLADE_FRAME from emitted bodies it proves cannot reach a panic, and that proof only looks at the calls the body makes — inline/template runtime code is invisible to it. Those kernels would now panic with no frame pushed, so every BL-panic trace through one drops a link in its Blade call stack (values unchanged, diagnostics silently degraded). Fix: keep the panic in blade_runtime.hpp behind a call the generated body makes textually, or teach codegen to stop eliding the frame."
                    (String.concat "; " offenders))

    printFooter "Diagnostics Core" [$"{passed} passed"; $"{failed} failure(s)"]
    { Block = "Diagnostics Core"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
