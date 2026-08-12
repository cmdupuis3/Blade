// Generic test-running machinery: the full IR -> C++ -> compile -> run ->
// value-check pipeline for a single test, plus the category runners the
// suite and the CLI share. Extracted verbatim from Main.fs (audit §2.3).
module Blade.Tests.Runner

open System
open Blade
open System.IO
open Blade.IR
open Blade.Types
open Blade.Lowering
open Blade.CodeGen
open Blade.Build
open Blade.Tests.TestHarness
open Blade.Tests.Expect

// ============================================================================
// Test Runner
// ============================================================================

/// Result of a full test run (IR + C++ compilation + execution)
type FullTestResult = {
    TestName: string
    IRResult: Result<IRProgram, string>
    CppGenerated: bool
    CppFile: string option
    CompileResult: Result<string, string>  // Ok(exePath) or Error(message)
    RunResult: Result<int * string, string>  // Ok(exitCode, stdout) or Error(message)
    ValueCheckResult: Result<unit, string list>  // Ok() or Error(list of mismatches)
    HasExpectedValues: bool  // Whether the test had EXPECT comments
    AbortExpectation: string list  // "(aborts)" probes: expected output substrings from // ABORT: (all must match)
    /// "(rejects)" probes: the stage that MUST do the rejecting (// REJECT-AT:).
    /// Meaningless for non-probes; defaults to RejectAtLower there.
    RejectStage: RejectStage
    /// `// EXPECT:` lines that did not parse into a pin and are not excused as
    /// prose. Non-empty means the test asserts something the harness cannot
    /// check, which is a failure in its own right (see classifyWithDetail).
    MalformedExpectLines: string list
    /// Did the generated C++ actually contain an emitted `#error` guard?
    /// Captured at generation time (the source text is already in hand there)
    /// so a codegen-stage reject-probe can be verified without re-reading the
    /// file later, when it may have been overwritten by a re-run.
    EmittedErrorGuard: bool
    /// `// ERROR: BLxxxx [@ span]` pins from the source (Expect.parseDiagPins).
    /// A reject-probe carrying these asserts WHY it is refused, not merely that
    /// it is; see classifyWithDetailAs.
    DiagPins: DiagPin list
    /// `// ERROR-CONTAINS: <substring>` pins from the source. Each must be found
    /// in some produced diagnostic message (or in the raw refusal text).
    DiagContains: string list
    /// The CODED diagnostics the front end produced, when the program was
    /// refused AND the source carries pins. `lower` formats its error as a bare
    /// string with the BLxxxx code stripped out, so the codes have to come from
    /// a `lowerDiag` pass; that pass is paid only where a verdict depends on it
    /// (see runFsharpPipelineLocked's wantDiags). Empty otherwise — which is
    /// indistinguishable from "no pins to check", and that is fine: the pin
    /// checker returns "satisfied" for an unpinned source either way.
    ProducedDiags: Blade.Diagnostics.Diagnostic list
    /// `// WARN: BLxxxx` pins: the checker-warning CODES this test is expected
    /// to emit (Expect.parseWarnPins). Enforced in BOTH directions, so this is
    /// the only licence under which a warning may fire at all.
    WarnPins: string list
    /// `// WARN-CODEGEN: <substring>` pins: the codegen warnings this test is
    /// expected to emit, matched by message substring.
    WarnCodegenPins: string list
    /// The checker warnings the front end actually produced — CAPTURED by
    /// `Lowering.lowerCaptured` rather than printed. Empty after a parse error
    /// (the checker never ran); possibly NON-empty after a typecheck error,
    /// because the warning channels survive the checker's error path, which is
    /// why a "(rejects)" probe is held to its warnings too.
    CapturedWarnings: Blade.Diagnostics.Diagnostic list
    /// The codegen warnings `genSelfContainedProgramFromIR` returned. Empty
    /// unless C++ was actually generated.
    ProducedCodegenWarnings: string list
}

/// The IR-only dump lane (`blade test --ir-only`). Uses `lowerCaptured` so the
/// checker's warnings are not sprayed by `lower` from inside the pipeline;
/// this lane then prints them ATTRIBUTED, under the test's own header, because
/// it is an explicit inspection lane whose whole output is a dump. It is not
/// part of the default `blade test` run, so this cannot reintroduce the leak.
let testLower source =
    let (result, warnings) = lowerCaptured source
    match result with
    | Ok ir ->
        printfn "Lower: OK"
        for w in warnings do
            printfn "  [warn %s] %s" w.Code (w.Message.Replace("\r\n", " ").Replace("\n", " "))
        for m in ir.Modules do
            printfn "  Module: %s" m.Name
            printfn "  Functions: %d" m.Functions.Length
            printfn "  Bindings: %d" m.Bindings.Length
            
            // Build name context from all bindings
            let mutable names = Map.empty
            for f in m.Functions do
                printfn "    function %s" f.Name
                names <- Map.add f.Id f.Name names
            for b in m.Bindings do
                names <- Map.add b.Id b.Name names
            
            // Print bindings with name context
            for b in m.Bindings do
                printfn "    let %s = %s" b.Name (ppIRExprWithNames names 0 b.Value)
        Ok ir
    | Error e ->
        printfn "Lower: ERROR - %s" e
        Error e

/// Lock serializing the F# pipeline (lower → IR → genCpp). The lower and
/// codegen functions rely on module-level mutable struct-field caches
/// (`structFieldsCache` in IR.fs, `codegenStructFieldsCache` in CodeGen.fs)
/// that are not thread-safe. With `Array.Parallel.mapi` running tests
/// concurrently, two tests' lift/codegen phases can race on these caches
/// — e.g., two tests both define `struct Trace` with different fields,
/// and test A's codegen reads a cache that test B has already overwritten
/// with its version of Trace, so A's field lookups fail.
///
/// The fix is to serialize the F# pipeline phase per test. C++ compile
/// and run (external subprocesses) remain outside the lock, so the
/// expensive parallelism is preserved.
///
/// Proper long-term fix: thread the struct-field map through as an
/// explicit parameter rather than module-level mutable state. That's
/// a larger refactor touching every recursive type-inference call;
/// deferred until needed.
let private fsharpPipelineLock = obj()

/// Encapsulates the result of the F# pipeline phase (parse → IR → C++
/// source generation), so the caller can run compile/run outside the lock.
type private FsPipelineOutcome =
    /// The formatted refusal text, plus the CODED diagnostics behind it when the
    /// caller asked for them (wantDiags). The two are the same rejection seen
    /// through two renderers, never two different rejections.
    | FpIRError of string * Blade.Diagnostics.Diagnostic list
    | FpIRValidationError of string list
    | FpIROnly of IRProgram          // compileAndRun = false, no .cpp generated
    /// ir, srcFile, warnings, backend, emitted-#error-guard, codegen refusal
    /// diagnostics. The guard flag is read off the generated source HERE, while
    /// it is in memory, because a codegen-stage reject-probe's verdict depends
    /// on it. The diagnostics are the coded (BL7001) half of the same refusal
    /// -- drained from codegen's unhandled-node channel, spanned at the
    /// declaration -- so a `// ERROR: BL7001` pin on a codegen-stage probe is
    /// checked against a real diagnostic rather than scraped out of g++'s echo
    /// of the `#error` text.
    | FpCppGenerated of IRProgram * string * string list * BackendReq * bool * Blade.Diagnostics.Diagnostic list
    | FpGenError of IRProgram * string  // ir was valid but codegen threw

/// `wantDiags`: also recover the CODED diagnostics for a refused program.
/// `lower` returns a formatted string with the BLxxxx code discarded
/// (TypeEnv.formatCompileError renders location + message only), so a test that
/// pins `// ERROR: BLxxxx` needs a second front-end pass through `lowerDiag`.
/// That pass runs HERE — inside the same lock, on the same large stack, so it
/// shares the serialization the module-level codegen caches require — and only
/// for a source that actually carries pins, so the unpinned majority of the
/// corpus pays nothing for it.
///
/// Warnings are CAPTURED here, not printed: `lowerCaptured` hands them back so
/// the verdict can hold them against the source's `// WARN:` pins. They come
/// from the FIRST front-end pass only — the `lowerDiag` pass below re-runs the
/// checker and therefore re-fills the same (AsyncLocal, reset-per-`typeCheck`)
/// warning channels, so draining after it would count every warning twice on
/// exactly the pinned reject-probes that can least afford it.
let private runFsharpPipelineLocked (source: string) (testName: string) (outputDir: string) (compileAndRun: bool) (wantDiags: bool) : FsPipelineOutcome * Blade.Diagnostics.Diagnostic list =
    lock fsharpPipelineLock (fun () ->
        let (irResult, capturedWarnings) = lowerCaptured source
        let outcome =
            match irResult with
            | Error e ->
                let diags =
                    if not wantDiags then []
                    else
                        // Same front end, same source, structured renderer. Both
                        // entry points refuse on the same three grounds (parse,
                        // typecheck, a throwing provider load), so this cannot
                        // report a rejection that `lower` did not also see.
                        // Resolving entry (lowerFileDiag, synthetic cwd-anchored
                        // path), matching lowerCaptured: a pinned probe that
                        // imports a STDLIB module (`import plot`) must be
                        // diagnosed against the same resolved program the first
                        // pass refused, or the pin compares against an
                        // unresolved-name artifact instead of the real error.
                        let entryPath =
                            System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(),
                                                   "__corpus_entry__.blade")
                        match fst (Lowering.lowerFileDiag entryPath source) with
                        | Error ds -> ds
                        | Ok _ -> []
                FpIRError (e, diags)
            | Ok ir ->
                match IR.validateIR ir with
                | Error validationErrors -> FpIRValidationError validationErrors
                | Ok ir ->
                    if not compileAndRun then FpIROnly ir
                    else
                        let safeName = sanitizeFileName testName
                        try
                            let (cppCode, codegenWarnings) = CodeGen.genSelfContainedProgramFromIR ir testName
                            // Backend requirement is inferred from the settled
                            // codegen output. CUDA codegen emits device kernels
                            // (.cu, compiled by nvcc); CPU codegen does not
                            // (.cpp, g++). The extension matches so the host
                            // toolchain recognizes device syntax.
                            let backendReq = inferBackendReq cppCode
                            let ext = match backendReq with RequiresCuda -> ".cu" | RequiresMpi | CpuOnly -> ".cpp"
                            let srcFile = Path.Combine(outputDir, safeName + ext)
                            File.WriteAllText(srcFile, cppCode)
                            // Codegen emits `#error "Blade codegen: ..."` when it
                            // deliberately refuses to render a construct. That
                            // directive — not the mere fact that g++ returned
                            // nonzero — is what a REJECT-AT: codegen probe pins.
                            let emittedErrorGuard = cppCode.Contains "#error"
                            // Drained here, in the same breath as the generation
                            // that produced them: the channel is reset at each
                            // genSelfContainedProgramFromIR entry, so leaving
                            // them would attribute this test's back-end hole to
                            // whichever test generated next on this flow.
                            let refusalDiags = CodeGen.takeUnhandledIRNodeDiagnostics ()
                            FpCppGenerated (ir, srcFile, codegenWarnings, backendReq, emittedErrorGuard, refusalDiags)
                        with ex ->
                            FpGenError (ir, sprintf "Generation failed: %s" ex.Message)
        outcome, capturedWarnings
    )

/// Run a full test: IR lowering + C++ generation + compilation + execution
let runFullTest (testName: string) (source: string) (outputDir: string) (compileAndRun: bool) : FullTestResult =
    // Parse expected values from source comments
    let expectedValues = parseExpectedValues source
    let abortExpectation = parseAbortExpectations source
    let rejectStage = parseRejectStage source

    // Reject-REASON pins. Present on a "(rejects)" probe, they turn "the
    // compiler refused this" into "the compiler refused this FOR THIS REASON",
    // which is the difference between a probe that guards a checker and a probe
    // that a stray parse error can keep green. Parsed for every test (the parse
    // is a line scan) so the summary can count which probes still lack them.
    let (diagPins, diagContains) = parseDiagPins source
    let wantDiags = not (diagPins.IsEmpty && diagContains.IsEmpty)

    // Warning pins. Same line scan, same "parsed for every test" rule as the
    // reject-reason pins above — but unlike them these are enforced in BOTH
    // directions (see warningPinMisses), because a warning nobody pinned is
    // exactly the thing that used to leak into the console un-attributed.
    let (warnPins, warnCodegenPins) = parseWarnPins source

    // Malformed-pin policy. Expect.parseMalformedExpectLines reports EVERY
    // `// EXPECT:` line it could not turn into a pin, including lines with no
    // `=` at all — it has no way to know which tests are allowed to write
    // prose there. The test NAME supplies that: a "(rejects)" probe never
    // reaches the value-checking stage, so many of them use `// EXPECT:` as
    // documentation of WHY the program is refused ("typecheck failure —
    // ragged operands support only ..."). Those lines carry no `=` and are
    // deliberate, so they are excused. Everything else stands: an `=`-bearing
    // line that failed to parse is a broken assertion anywhere, and a no-`=`
    // line on a NORMAL test is an assertion the author believed was being
    // checked but which had been silently dropped.
    let malformedExpectLines =
        let reported = parseMalformedExpectLines source
        if testName.EndsWith "(rejects)" then
            reported |> List.filter (fun line -> line.Contains "=")
        else reported

    // F# pipeline (lower + codegen) runs under a lock to avoid cache
    // races. C++ compile and run (below) stay outside the lock so they
    // parallelize freely across tests.
    //
    // Array.Parallel.mapi runs each test on a ~1 MB thread-pool thread, which
    // the deep AST/IR recursion (e.g. ppl jet elaboration) can overflow — so
    // the pipeline runs on a large-stack thread. The lock still serializes it,
    // so at most one such thread does the deep work at a time. See Runtime.fs.
    let (pipelineOutcome, capturedWarnings) =
        Blade.Runtime.runOnLargeStack (fun () ->
            runFsharpPipelineLocked source testName outputDir compileAndRun wantDiags)

    // Hoisted so every result-record branch below can record it uniformly:
    // only the generated-source branch can have seen a `#error` guard, and
    // "no C++ was produced" must read as "no guard", never as unknown.
    let emittedErrorGuard =
        match pipelineOutcome with
        | FpCppGenerated (_, _, _, _, guard, _) -> guard
        | _ -> false

    // Likewise hoisted: the front-end-rejection branch carries the checker's
    // coded diagnostics, and the generated-source branch carries codegen's own
    // (a back-end hole reported as BL7001). Every other branch must read as
    // "none produced".
    let producedDiags =
        match pipelineOutcome with
        | FpIRError (_, ds) -> ds
        | FpCppGenerated (_, _, _, _, _, ds) -> ds
        | _ -> []

    // Hoisted for the same reason: only the generated-source branch can carry
    // codegen warnings, and every other branch must read as "none produced"
    // rather than as unknown — a `// WARN-CODEGEN:` pin on a test that never
    // reached codegen must fail, not pass vacuously.
    let producedCodegenWarnings =
        match pipelineOutcome with
        | FpCppGenerated (_, _, ws, _, _, _) -> ws
        | _ -> []

    match pipelineOutcome with
    | FpIRError (e, _) ->
        { TestName = testName; IRResult = Error e; CppGenerated = false;
          CppFile = None; CompileResult = Error "IR failed"; RunResult = Error "IR failed";
          ValueCheckResult = Error ["IR failed"]; HasExpectedValues = not expectedValues.IsEmpty; AbortExpectation = abortExpectation
          RejectStage = rejectStage; MalformedExpectLines = malformedExpectLines
          EmittedErrorGuard = emittedErrorGuard
          DiagPins = diagPins; DiagContains = diagContains; ProducedDiags = producedDiags
          WarnPins = warnPins; WarnCodegenPins = warnCodegenPins
          CapturedWarnings = capturedWarnings; ProducedCodegenWarnings = producedCodegenWarnings }
    | FpIRValidationError validationErrors ->
        for e in validationErrors do printfn "  %s" e
        { TestName = testName; IRResult = Error (validationErrors |> String.concat "; "); CppGenerated = false;
          CppFile = None; CompileResult = Error "IR validation failed"; RunResult = Error "IR validation failed";
          ValueCheckResult = Error ["IR validation failed"]; HasExpectedValues = not expectedValues.IsEmpty; AbortExpectation = abortExpectation
          RejectStage = rejectStage; MalformedExpectLines = malformedExpectLines
          EmittedErrorGuard = emittedErrorGuard
          DiagPins = diagPins; DiagContains = diagContains; ProducedDiags = producedDiags
          WarnPins = warnPins; WarnCodegenPins = warnCodegenPins
          CapturedWarnings = capturedWarnings; ProducedCodegenWarnings = producedCodegenWarnings }
    | FpIROnly ir ->
        { TestName = testName; IRResult = Ok ir; CppGenerated = false;
          CppFile = None; CompileResult = Error "Skipped"; RunResult = Error "Skipped";
          ValueCheckResult = Error ["Skipped"]; HasExpectedValues = not expectedValues.IsEmpty; AbortExpectation = abortExpectation
          RejectStage = rejectStage; MalformedExpectLines = malformedExpectLines
          EmittedErrorGuard = emittedErrorGuard
          DiagPins = diagPins; DiagContains = diagContains; ProducedDiags = producedDiags
          WarnPins = warnPins; WarnCodegenPins = warnCodegenPins
          CapturedWarnings = capturedWarnings; ProducedCodegenWarnings = producedCodegenWarnings }
    | FpGenError (ir, msg) ->
        { TestName = testName; IRResult = Ok ir; CppGenerated = false;
          CppFile = None; CompileResult = Error msg;
          RunResult = Error "Generation failed"; ValueCheckResult = Error ["Generation failed"];
          HasExpectedValues = not expectedValues.IsEmpty; AbortExpectation = abortExpectation
          RejectStage = rejectStage; MalformedExpectLines = malformedExpectLines
          EmittedErrorGuard = emittedErrorGuard
          DiagPins = diagPins; DiagContains = diagContains; ProducedDiags = producedDiags
          WarnPins = warnPins; WarnCodegenPins = warnCodegenPins
          CapturedWarnings = capturedWarnings; ProducedCodegenWarnings = producedCodegenWarnings }
    | FpCppGenerated (ir, srcFile, _codegenWarnings, backendReq, _, _) ->
        // Codegen warnings are NOT printed here. They rode into
        // `producedCodegenWarnings` above and are judged against the source's
        // `// WARN-CODEGEN:` pins by the verdict; an expected one is silent and
        // an unexpected one surfaces in the failing test's detail line. Printing
        // them unconditionally put un-attributed text into a run whose tests all
        // passed, which is the leak this discipline removes.
        let caps = capabilities.Value

        // Step 3: Compile (outside lock — separate subprocess). The
        // toolchain is resolved from the inferred backend requirement
        // against the environment's capabilities; an unsatisfiable
        // requirement comes back as Error "Skipped: <reason>".
        let compileResult = compileForBackend caps backendReq srcFile outputDir

        match compileResult with
        | Error e ->
            // Both genuine compile failures and skips flow here; the
            // skip vs fail distinction is made downstream via isSkipError.
            let runErr = if isSkipError e then e else "Compile failed"
            { TestName = testName; IRResult = Ok ir; CppGenerated = true;
              CppFile = Some srcFile; CompileResult = Error e; RunResult = Error runErr;
              ValueCheckResult = Error [runErr]; HasExpectedValues = not expectedValues.IsEmpty; AbortExpectation = abortExpectation
              RejectStage = rejectStage; MalformedExpectLines = malformedExpectLines
              EmittedErrorGuard = emittedErrorGuard
              DiagPins = diagPins; DiagContains = diagContains; ProducedDiags = producedDiags
              WarnPins = warnPins; WarnCodegenPins = warnCodegenPins
              CapturedWarnings = capturedWarnings; ProducedCodegenWarnings = producedCodegenWarnings }
        | Ok exeFile ->
            // Step 4: Run — but a CUDA-requiring test on a GPU-less box can
            // compile yet not execute. Validate the compile, skip the run.
            if backendReq = RequiresCuda && not caps.HasGpu then
                { TestName = testName; IRResult = Ok ir; CppGenerated = true;
                  CppFile = Some srcFile; CompileResult = Ok exeFile;
                  RunResult = Error "Skipped: no GPU";
                  ValueCheckResult = Error ["Skipped: no GPU"];
                  HasExpectedValues = not expectedValues.IsEmpty; AbortExpectation = abortExpectation
                  RejectStage = rejectStage; MalformedExpectLines = malformedExpectLines
                  EmittedErrorGuard = emittedErrorGuard
                  DiagPins = diagPins; DiagContains = diagContains; ProducedDiags = producedDiags
                  WarnPins = warnPins; WarnCodegenPins = warnCodegenPins
                  CapturedWarnings = capturedWarnings; ProducedCodegenWarnings = producedCodegenWarnings }
            else
                let runResult = runExecutable exeFile

                // Step 5: Check values if run succeeded
                let valueCheckResult =
                    match runResult with
                    | Ok (0, output) ->
                        if expectedValues.IsEmpty then Ok ()
                        else checkExpectedValues expectedValues output
                    | Ok (code, _) -> Error [sprintf "Exit code %d" code]
                    | Error e -> Error [e]

                { TestName = testName; IRResult = Ok ir; CppGenerated = true;
                  CppFile = Some srcFile; CompileResult = Ok exeFile; RunResult = runResult;
                  ValueCheckResult = valueCheckResult; HasExpectedValues = not expectedValues.IsEmpty; AbortExpectation = abortExpectation
                  RejectStage = rejectStage; MalformedExpectLines = malformedExpectLines
                  EmittedErrorGuard = emittedErrorGuard
                  DiagPins = diagPins; DiagContains = diagContains; ProducedDiags = producedDiags
                  WarnPins = warnPins; WarnCodegenPins = warnCodegenPins
                  CapturedWarnings = capturedWarnings; ProducedCodegenWarnings = producedCodegenWarnings }

/// A test whose name ends in "(aborts)" is a runtime-abort probe: the CORRECT
/// outcome is that it compiles cleanly and then exits nonzero at runtime (a
/// constraint guard firing std::abort()). When the source pins a message via
/// `// ABORT: <substring>`, the merged stdout+stderr must contain it. Exit
/// codes are deliberately not pinned — abort() maps to different codes across
/// runtimes (MinGW: 3; MSVC: 0xC0000409). An IR or compile failure is a
/// genuine failure: the probe exercises the runtime guard, not the checker.
let isAbortProbe (result: FullTestResult) =
    result.TestName.EndsWith "(aborts)"

/// Did an abort-probe behave correctly? Compiled, ran, exited nonzero, and
/// printed the pinned abort message (when one is present).
let isExpectedAbort (result: FullTestResult) =
    match result.IRResult, result.CompileResult, result.RunResult with
    | Ok _, Ok _, Ok (code, output) when code <> 0 ->
        result.AbortExpectation |> List.forall (fun sub -> output.Contains sub)
    | _ -> false

/// Determine if a test result is a full pass
let isFullPass (result: FullTestResult) =
    match result.IRResult, result.CompileResult, result.RunResult with
    | Ok _, Ok _, Ok (0, _) -> true
    | _ -> false

/// Determine if IR passed (regardless of C++)
let isIRPass (result: FullTestResult) =
    match result.IRResult with Ok _ -> true | _ -> false

/// A test whose name ends in "(rejects)" is an intentional reject-probe: the
/// CORRECT outcome is that the compiler REFUSES it, at the stage pinned by
/// `// REJECT-AT:` (see Expect.RejectStage). Such a probe counts as PASSING
/// when it is refused there, and as FAILING when it slips through — which
/// keeps the grand-total "failed tests" list honest.
let isRejectProbe (result: FullTestResult) =
    result.TestName.EndsWith "(rejects)"

/// Does this reject-probe pin the REASON it must be refused for?
let hasRejectReasonPins (result: FullTestResult) =
    not (result.DiagPins.IsEmpty && result.DiagContains.IsEmpty)

/// Same pin/diagnostic relation Test_DiagCorpus uses: code must be equal, and a
/// pinned start/end position must be equal too when the pin gives one.
let private matchesDiagPin (p: DiagPin) (d: Blade.Diagnostics.Diagnostic) =
    d.Code = p.PinCode
    && (match p.PinStart with
        | Some (l, c) -> d.Span.StartLine = l && d.Span.StartCol = c
        | None -> true)
    && (match p.PinEnd with
        | Some (l, c) -> d.Span.EndLine = l && d.Span.EndCol = c
        | None -> true)

let private renderPin (p: DiagPin) =
    sprintf "%s%s" p.PinCode
        (match p.PinStart with Some (l, c) -> sprintf " @ %d:%d" l c | None -> "")

let private renderDiag (d: Blade.Diagnostics.Diagnostic) =
    sprintf "%s @ %d:%d" d.Code d.Span.StartLine d.Span.StartCol

let private clip (n: int) (s: string) =
    let one = s.Replace("\r\n", " ").Replace("\n", " ").Trim()
    if one.Length <= n then one else one.Substring(0, n) + "..."

/// The refusal EVIDENCE behind a reject-probe's verdict: the coded diagnostics
/// the front end produced (empty unless it was the front end that refused, and
/// unless the source carries pins) plus the raw text of whatever stage did the
/// refusing. The compile output is part of the text because that is where a
/// REJECT-AT: codegen probe's evidence lives — g++ echoes the emitted
/// `#error "Blade codegen: ..."` message, and that message IS the deliberate
/// refusal the probe pins. Skip messages are excluded: "no toolchain" is not a
/// rejection reason, and letting it match a pin would be the same vacuity in a
/// new place.
let private rejectEvidence (result: FullTestResult) : Blade.Diagnostics.Diagnostic list * string =
    let text =
        [ (match result.IRResult with Error e -> e | Ok _ -> "")
          (match result.CompileResult with Error e when not (isSkipError e) -> e | _ -> "") ]
        |> List.filter (fun s -> s <> "")
        |> String.concat "\n"
    (result.ProducedDiags, text)

/// Which of the source's reject-REASON pins the actual rejection does NOT
/// satisfy, each rendered as expected-vs-actual. Empty means satisfied — which
/// is also what an UNPINNED source returns, so every probe that carries no pins
/// keeps exactly its previous verdict.
///
/// One-directional on purpose. Test_DiagCorpus is strict in BOTH directions
/// (every produced diagnostic must be claimed by a pin) because the diagnostics
/// corpus exists to pin the diagnostic SET exactly. A value-corpus reject-probe
/// makes a narrower claim — this program is refused, and for this reason — so an
/// extra cascade diagnostic alongside the pinned one is not a failure here.
/// Pins are matched greedily 1:1 against the diagnostics, so two pins of the
/// same code require two such diagnostics.
///
/// Evidence channels, in order of strength:
///   * coded diagnostics (parse / typecheck / provider-load) — the only channel
///     carrying BLxxxx codes and spans, so this is where a code pin is really
///     checked;
///   * the raw refusal text — IR-validation messages and the emitted codegen
///     `#error` guard, neither of which carries a code today. A code pin can
///     only be satisfied from text if the code literally appears there; when it
///     does not, the pin is reported as unmatched rather than waved through,
///     because "the pinned code is unverifiable on this path" is exactly the
///     kind of silence this whole change exists to remove.
let private rejectReasonMisses (result: FullTestResult) : string list =
    if not (hasRejectReasonPins result) then []
    else
        let (diags, text) = rejectEvidence result
        let actual =
            if diags.IsEmpty then
                sprintf "no coded diagnostics were produced; refusal text: %s" (clip 200 text)
            else sprintf "produced %s" (diags |> List.map renderDiag |> String.concat ", ")
        let mutable remaining = diags
        let codeMisses = ResizeArray<string>()
        for p in result.DiagPins do
            match remaining |> List.tryFindIndex (matchesDiagPin p) with
            | Some i ->
                remaining <- remaining |> List.indexed |> List.filter (fun (j, _) -> j <> i) |> List.map snd
            | None ->
                if not (text.Contains p.PinCode) then
                    codeMisses.Add (sprintf "expected // ERROR: %s but %s" (renderPin p) actual)
        let containsMisses =
            result.DiagContains
            |> List.filter (fun s ->
                not (diags |> List.exists (fun d -> d.Message.Contains s)) && not (text.Contains s))
            |> List.map (fun s ->
                sprintf "expected the rejection to mention '%s' but %s" s actual)
        (List.ofSeq codeMisses) @ containsMisses

/// The first line of a possibly multi-line message, for a one-line detail.
let private firstLineOf (s: string) =
    let one = s.Replace("\r\n", "\n")
    match one.IndexOf '\n' with
    | -1 -> one.Trim()
    | i -> one.Substring(0, i).Trim()

/// THE warning-pin rule, and the ONLY place it is decided. Both the corpus
/// verdict (classifyWithDetailAs) and the multi-file runner call this, so the
/// two cannot drift into different notions of "this warning was expected".
///
/// Strict in BOTH directions, for both warning channels:
///
///   * every warning that FIRED must be covered by a pin. A warning nobody
///     pinned used to be printed straight to stderr from inside `lower` —
///     un-attributed (no file name, just `--> line:col`) and interleaved with
///     the progress lines of whatever else was running in parallel — which
///     made ~754 of them per run indistinguishable from noise. Requiring a pin
///     is what turns "the suite emits warnings" into "these named tests emit
///     these named warnings, on purpose".
///   * every pin must have FIRED. Without this half, a pin outlives the rule
///     that motivated it: delete the check that emits BL4010 and every
///     `// WARN: BL4010` in the corpus silently becomes a comment. This is the
///     same both-directions discipline Test_DiagCorpus applies to `// ERROR:`.
///
/// Count-insensitive: one pin licenses every warning of that code. See
/// Expect.parseWarnPins for why multiplicity is deliberately not pinned.
///
/// Note this returns MISSES, so an unpinned source with no warnings and a
/// pinned source whose pins all fired both come back empty — the overwhelming
/// majority of the corpus pays nothing and reads no differently than before.
let warningPinMisses (warnPins: string list) (codegenPins: string list)
                     (warnings: Blade.Diagnostics.Diagnostic list)
                     (codegenWarnings: string list) : string list =
    let pinnedCodes = Set.ofList warnPins
    let firedCodes = warnings |> List.map (fun d -> d.Code) |> Set.ofList
    let unpinned =
        warnings
        |> List.filter (fun d -> not (pinnedCodes.Contains d.Code))
        |> List.map (fun d -> sprintf "unpinned warning[%s]: %s" d.Code (firstLineOf d.Message))
        |> List.distinct
    let unfired =
        warnPins
        |> List.distinct
        |> List.filter (fun c -> not (firedCodes.Contains c))
        |> List.map (sprintf "expected // WARN: %s but no such warning fired")
    let unpinnedCodegen =
        codegenWarnings
        |> List.filter (fun w -> not (codegenPins |> List.exists (fun p -> w.Contains p)))
        |> List.map (fun w -> sprintf "unpinned codegen warning: %s" (firstLineOf w))
        |> List.distinct
    let unfiredCodegen =
        codegenPins
        |> List.distinct
        |> List.filter (fun p -> not (codegenWarnings |> List.exists (fun w -> w.Contains p)))
        |> List.map (sprintf "expected // WARN-CODEGEN: '%s' but no codegen warning matched")
    unpinned @ unfired @ unpinnedCodegen @ unfiredCodegen

/// The warning-pin misses for a full-pipeline test result.
///
/// The CODEGEN half goes vacuous when no C++ was generated, because there the
/// stage that produces those warnings never ran: on a toolchain-less box
/// `runFullTest` is called with compileAndRun = false and stops at FpIROnly, and
/// a `// WARN-CODEGEN:` pin would otherwise report "no codegen warning matched"
/// as a FAILURE on every such box. That is the same "the stage did not run, so
/// it did not fail" rule the codegen-stage reject-probe already follows (it
/// Skips rather than Fails when there is no source). The CHECKER half is
/// unconditional: the front end always runs, so its warnings are always earned.
let private resultWarningPinMisses (result: FullTestResult) : string list =
    if result.CppGenerated then
        warningPinMisses result.WarnPins result.WarnCodegenPins
                         result.CapturedWarnings result.ProducedCodegenWarnings
    else
        warningPinMisses result.WarnPins [] result.CapturedWarnings []

/// Per-stage status strings, in pipeline order. "OK" / "FAIL" / "SKIP", plus
/// "EXIT(n)" for a nonzero run. The value stage is present only when the test
/// carries pins. Both the verdict and the printed detail read this one list.
let private stageStatuses (result: FullTestResult) : (string * string) list =
    let irStatus = match result.IRResult with Ok _ -> "OK" | Error _ -> "FAIL"
    let cppStatus = if result.CppGenerated then "OK" else "SKIP"
    let compileStatus = match result.CompileResult with Ok _ -> "OK" | Error e when isSkipError e -> "SKIP" | Error _ -> "FAIL"
    let runStatus =
        match result.RunResult with
        | Ok (0, _) -> "OK"
        | Ok (code, _) -> sprintf "EXIT(%d)" code
        | Error e when isSkipError e -> "SKIP"
        | Error _ -> "FAIL"
    let valueStatus =
        if not result.HasExpectedValues then ""
        else match result.ValueCheckResult with
             | Ok () -> "OK"
             | Error errs when errs |> List.exists isSkipError -> "SKIP"
             | Error _ -> "FAIL"
    [ "IR", irStatus
      "Gen", cppStatus
      "Compile", compileStatus
      "Run", runStatus ]
    @ (if valueStatus = "" then [] else [ "Val", valueStatus ])

/// THE verdict. This is the ONLY place a test's outcome is decided: the
/// per-test `[PASS]/[FAIL]/[SKIP]` line and the block roll-up both call it,
/// so the line and the totals cannot drift apart. (They did: a skipped
/// reject-probe printed [FAIL] while the roll-up counted it as a pass,
/// because the printer folded stage statuses and the roll-up ran a separate
/// `isCorrectOutcome` predicate.) Returns the outcome plus the human-readable
/// detail that explains it, so the explanation is derived from the same
/// decision rather than recomputed alongside it.
///
/// Rules, in priority order:
///
///  1. A malformed `// EXPECT:` line fails the test outright, whatever else
///     happened. The test asserts something the harness cannot evaluate; the
///     old behaviour (drop the pin) turned a broken assertion into no
///     assertion, and the test then passed on "compiled and exited 0" alone.
///
///  2. A SKIP is never a pass — for probes as much as for normal tests. This
///     is what closes the reject-probe hole: the old rule credited a probe
///     that failed for ANY reason, so on a box where the toolchain was down
///     every one of the 150 probes reported green while `Compiled: 0 / 921`.
///
///  3. A reject-probe is judged at the stage it pins, not on "did anything go
///     wrong". REJECT-AT: lower must be refused by parse/typecheck/lowering.
///     REJECT-AT: codegen must lower cleanly, emit a `#error` guard into the
///     generated source, and then fail the C++ compile — verifying the guard
///     is the whole point, since it is what distinguishes "our deliberate
///     refusal fired" from "we emitted garbage C++" and from "g++ is broken".
///
///  3b. ...and, when its source pins the REASON via `// ERROR: BLxxxx` /
///     `// ERROR-CONTAINS:`, the rejection that happened must be THAT rejection.
///     Pinning only the STAGE left a hole: ANY lowering `Error` credited a
///     RejectAtLower probe, so a typo that turned the probe into a parse error —
///     or a renamed intrinsic, or an unrelated checker tightening upstream of
///     the rule under test — kept the probe green while the checker it was
///     written to guard went completely untested. That is not hypothetical; it
///     was reproduced. A pinned probe now asserts a SPECIFIC diagnostic, and a
///     mismatch reports expected vs actual. Probes with no pins keep exactly
///     their old verdict, and the summary counts how many those are.
///
///  4. An abort-probe must compile, run, and exit nonzero with its pinned
///     `// ABORT:` message (isExpectedAbort).
///
///  5. A normal test must be a full pass and, when it carries pins, must pass
///     the value check. Compiles-clean-but-prints-the-wrong-numbers is a
///     failure; that class is the entire reason EXPECT checks exist.
///
///  `forceRejectProbe` applies rule 3 to a test whose NAME carries no
///  "(rejects)" marker, for the benefit of a corpus that is entirely negative
///  (InterpDiff.rejectOnlyCategories). It exists so that gate can reuse THIS
///  classifier instead of writing its own weaker rule, which is precisely how
///  the interp gate came to credit a reject-probe on any non-full-pass.
let classifyWithDetailAs (forceRejectProbe: bool) (result: FullTestResult) : Blade.Tests.TestHarness.Outcome * string =
    let stages = stageStatuses result
    let anyFail = stages |> List.exists (fun (_, s) -> s = "FAIL" || s.StartsWith "EXIT")
    let anySkip = stages |> List.exists (fun (_, s) -> s = "SKIP")
    let compileSkipped =
        match result.CompileResult with Error e when isSkipError e -> true | _ -> false
    let pinNote = if hasRejectReasonPins result then " (pinned reason matched)" else ""

    if not result.MalformedExpectLines.IsEmpty then
        Blade.Tests.TestHarness.Fail,
        sprintf "unparseable EXPECT pin(s): %s"
            (result.MalformedExpectLines |> String.concat " | ")

    elif not (resultWarningPinMisses result).IsEmpty then
        // Rule 1b, and deliberately AHEAD of the probe branches. A "(rejects)"
        // probe that is refused for its pinned reason is a Pass and returns
        // immediately, so a warning check placed after that branch would never
        // run on the very files most likely to warn — the checker's warning
        // channels survive its error path, so a refused program has still
        // EARNED whatever it emitted before the refusal. Checking here holds
        // every test to the same rule, whatever kind of test it is.
        Blade.Tests.TestHarness.Fail,
        String.concat " ; " (resultWarningPinMisses result)

    elif isRejectProbe result || forceRejectProbe then
        match result.RejectStage with
        | RejectAtLower ->
            match result.IRResult with
            | Error _ ->
                match rejectReasonMisses result with
                | [] ->
                    Blade.Tests.TestHarness.Pass,
                    sprintf "correctly rejected during lowering%s" pinNote
                | misses ->
                    Blade.Tests.TestHarness.Fail,
                    sprintf "rejected during lowering, but NOT for the pinned reason: %s"
                        (String.concat " ; " misses)
            | Ok _ ->
                Blade.Tests.TestHarness.Fail,
                "expected rejection during lowering, but the program lowered"
        | RejectAtCodegen ->
            match result.IRResult with
            | Error _ ->
                // Mis-pinned corpus entry (or a checker improvement that moved
                // the rejection earlier). Either way the pin no longer
                // describes reality, so say so instead of quietly passing.
                Blade.Tests.TestHarness.Fail,
                "pinned REJECT-AT: codegen but lowering rejected it -- re-pin as 'lower'"
            | Ok _ ->
                if not result.CppGenerated then
                    // No source at all: either the pipeline was run IR-only
                    // (no toolchain) or codegen threw. The first is a skip,
                    // the second a failure — a codegen CRASH is not the
                    // deliberate `#error` guard this probe pins.
                    if compileSkipped then
                        Blade.Tests.TestHarness.Skip, "codegen-stage probe: no C++ generated (toolchain unavailable)"
                    else
                        Blade.Tests.TestHarness.Fail, "expected an emitted #error guard, but codegen produced no source"
                elif not result.EmittedErrorGuard then
                    Blade.Tests.TestHarness.Fail,
                    "expected an emitted #error guard, but the generated C++ contains none"
                else
                    match result.CompileResult with
                    | Error e when isSkipError e ->
                        Blade.Tests.TestHarness.Skip, "#error guard emitted but not compiled (toolchain unavailable)"
                    | Error _ ->
                        // The guard-emitted + compile-failed logic above is
                        // untouched; the pins are an ADDITIONAL requirement,
                        // checked against the compile output (where g++ echoes
                        // the `#error "Blade codegen: ..."` text). Only
                        // // ERROR-CONTAINS: is really checkable on this path —
                        // codegen guards carry no BLxxxx code — and a code pin
                        // that cannot be found says so rather than passing.
                        match rejectReasonMisses result with
                        | [] ->
                            Blade.Tests.TestHarness.Pass,
                            sprintf "correctly rejected by the emitted #error guard%s" pinNote
                        | misses ->
                            Blade.Tests.TestHarness.Fail,
                            sprintf "the #error guard fired, but NOT for the pinned reason: %s"
                                (String.concat " ; " misses)
                    | Ok _ ->
                        Blade.Tests.TestHarness.Fail,
                        "#error guard emitted but the C++ compiled anyway"

    elif isAbortProbe result then
        // An abort-probe with no `// ABORT:` pin is the abort-side twin of an
        // unpinned reject-probe: `isExpectedAbort`'s `List.forall` over an empty
        // pin list is vacuously TRUE, so ANY nonzero exit satisfies it — a
        // segfault in unrelated generated code, a missing runtime DLL, an
        // uncaught C++ exception. The probe exists to assert that a SPECIFIC
        // guard fired, and without a pin it cannot. Every abort-probe in the
        // corpus carries one today, so this is a standing lock on that, not a
        // verdict change.
        if result.AbortExpectation.IsEmpty then
            Blade.Tests.TestHarness.Fail,
            "abort-probe has no // ABORT: pin -- any nonzero exit would satisfy it, so it asserts nothing"
        elif isExpectedAbort result then
            let code = match result.RunResult with Ok (c, _) -> c | _ -> 0
            Blade.Tests.TestHarness.Pass, sprintf "aborted as expected (exit %d)" code
        else
            let detail =
                match result.RunResult with
                | Ok (code, output) when code <> 0 ->
                    match result.AbortExpectation |> List.tryFind (fun sub -> not (output.Contains sub)) with
                    | Some sub -> sprintf "aborted (exit %d) but output lacks '%s'" code sub
                    | None -> sprintf "aborted as expected (exit %d)" code
                | Ok (0, _) -> "expected runtime abort but exited 0"
                | _ ->
                    match stages |> List.tryFind (fun (_, s) -> s = "FAIL" || s = "SKIP") with
                    | Some (stg, "SKIP") -> sprintf "%s skipped" stg
                    | Some (stg, _) -> sprintf "expected runtime abort but %s failed" stg
                    | None -> "expected runtime abort"
            if anySkip && not anyFail then Blade.Tests.TestHarness.Skip, detail
            else Blade.Tests.TestHarness.Fail, detail

    elif anyFail then
        let (stg, st) = stages |> List.find (fun (_, s) -> s = "FAIL" || s.StartsWith "EXIT")
        let detail = if st.StartsWith "EXIT" then sprintf "%s %s" stg st else sprintf "%s failed" stg
        Blade.Tests.TestHarness.Fail, detail
    elif anySkip then
        let (stg, _) = stages |> List.find (fun (_, s) -> s = "SKIP")
        Blade.Tests.TestHarness.Skip, sprintf "%s skipped" stg
    else
        // One-liner for passes (#3): the stages that ran, as the detail.
        Blade.Tests.TestHarness.Pass,
        sprintf "(%s)" (stages |> List.map fst |> String.concat ",")

/// THE verdict for a test classified by its own name (see classifyWithDetailAs
/// for the forced-reject-probe variant the interp gate needs).
let classifyWithDetail (result: FullTestResult) : Blade.Tests.TestHarness.Outcome * string =
    classifyWithDetailAs false result

/// The verdict alone. Everything that needs to bucket a result — the roll-up,
/// the summary counters — goes through here, never through an ad-hoc predicate.
let classify (result: FullTestResult) : Blade.Tests.TestHarness.Outcome =
    fst (classifyWithDetail result)

/// Print a full test result
let printFullTestResult (result: FullTestResult) (verbose: bool) (showFullError: bool) =
    let (outcome, detail) = classifyWithDetail result
    Blade.Tests.TestHarness.resultLine outcome result.TestName detail

    if verbose then
        match result.IRResult with
        | Error e -> printfn "    IR Error: %s" e
        | Ok _ -> ()
        
        match result.CompileResult with
        | Error e when not (isSkipError e) && e <> "IR failed" -> 
            printfn "    Compile Error:\n%s" e
            match result.CppFile with
            | Some f -> printfn "    Generated: %s" f
            | None -> ()
        | _ -> ()
        
        match result.RunResult with
        | Ok (code, output) when code <> 0 -> 
            printfn "    Run exited with code %d" code
            if not (String.IsNullOrWhiteSpace output) then
                if showFullError then
                    printfn "    Output:\n%s" output
                else
                    printfn "    Output: %s" (output.Split('\n').[0])
        | Error e when not (isSkipError e) && e <> "IR failed" && e <> "Compile failed" -> 
            printfn "    Run Error: %s" e
        | _ -> ()
        
        // Show value check errors
        match result.ValueCheckResult with
        | Error errors when not (errors |> List.exists isSkipError) && 
                           not (List.contains "IR failed" errors) &&
                           not (List.contains "Compile failed" errors) &&
                           not (List.contains "Generation failed" errors) ->
            for err in errors do
                printfn "    Value Error: %s" err
        | _ -> ()

/// Did the test behave correctly? A thin alias over `classify` so there is
/// exactly one definition of "correct" in the harness. Note a SKIP is not
/// correct and not incorrect — callers that care must ask `classify` directly
/// rather than reading `not (isCorrectOutcome r)` as "failed".
let isCorrectOutcome (result: FullTestResult) =
    classify result = Blade.Tests.TestHarness.Pass

/// Run test category with IR only
let runTestCategory name tests =
    printHeader (sprintf "Blade-DSL: %s Tests" name)
    printfn "Running %d tests...\n" (List.length tests)
    
    let mutable passed = 0
    let mutable failed = 0
    
    for (testName, source) in tests do
        printSubHeader testName
        match testLower source with
        | Ok _ ->
            printfn "PASSED"
            passed <- passed + 1
        | Error _ ->
            printfn "FAILED"
            failed <- failed + 1
    
    printHeader "Test Summary"
    printfn "Passed: %d" passed
    printfn "Failed: %d" failed
    printfn "Total:  %d" (passed + failed)
    
    if failed > 0 then
        printfn "\nSome tests failed."
        1
    else
        printfn "\nAll tests passed!"
        0

/// Run multi-file module tests (IR-only)
let runMultiFileTests (name: string) (tests: (string * (string * string) list) list) =
    printHeader (sprintf "Blade-DSL: %s Tests (Multi-File)" name)
    printfn "Running %d tests...\n" (List.length tests)
    
    let mutable passed = 0
    let mutable failed = 0
    
    for (testName, sources) in tests do
        printSubHeader testName
        // Captured, then printed attributed under this test's header — same
        // reasoning as testLower: an inspection lane, not part of the suite.
        let (lowered, warnings) = lowerMultiSourceCaptured sources
        match lowered with
        | Ok ir ->
            printfn "Lower: OK (%d modules)" ir.Modules.Length
            for w in warnings do
                printfn "  [warn %s] %s" w.Code (w.Message.Replace("\r\n", " ").Replace("\n", " "))
            for m in ir.Modules do
                printfn "  Module: %s — %d functions, %d bindings" m.Name m.Functions.Length m.Bindings.Length
            passed <- passed + 1
        | Error e ->
            printfn "FAILED: %s" e
            failed <- failed + 1
    
    printHeader "Test Summary"
    printfn "Passed: %d" passed
    printfn "Failed: %d" failed
    printfn "Total:  %d" (passed + failed)
    if failed > 0 then 1 else 0

/// Run multi-file module tests with full C++ pipeline
let runMultiFileTestsFull (name: string) (tests: (string * (string * string) list) list) (outputDir: string) =
    printHeader (sprintf "Blade-DSL: %s Tests (Multi-File, Full C++ Pipeline)" name)
    
    let gppAvailable = checkGppAvailable ()
    if not gppAvailable then
        printfn "WARNING: g++ not available.\n"
    
    if not (Directory.Exists outputDir) then
        Directory.CreateDirectory outputDir |> ignore
    
    // Write runtime header file once
    CodeGen.deployRuntimeHeaders outputDir
    
    let mutable passed = 0
    let mutable failed = 0
    let mutable skipped = 0
    let mutable failedNames = []

    for (testName, sources) in tests do
        // Warning pins are the UNION over the member sources. A cross-module
        // program is typechecked as one program, so the warning it earns cannot
        // be attributed to one file, and the pin that licenses it is equally at
        // home in whichever member motivated it.
        let (warnPins, warnCodegenPins) =
            let pairs = sources |> List.map (snd >> parseWarnPins)
            (pairs |> List.collect fst, pairs |> List.collect snd)
        let (lowered, capturedWarnings) = lowerMultiSourceCaptured sources
        match lowered with
        | Error e ->
            Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail testName (sprintf "lower: %s" e)
            failed <- failed + 1
            failedNames <- failedNames @ [testName]
        | Ok ir ->
            match IR.validateIR ir with
            | Error validationErrors ->
                let joined = String.concat "; " validationErrors
                Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail testName (sprintf "IR validation: %s" joined)
                failed <- failed + 1
                failedNames <- failedNames @ [testName]
            | Ok ir ->
            let safeName = sanitizeFileName testName
            try
                let (cppCode, codegenWarnings) = CodeGen.genSelfContainedProgramFromIR ir testName
                // Same backend inference as the single-file pipeline:
                // .cu + nvcc when device kernels are emitted, else .cpp + g++.
                let backendReq = inferBackendReq cppCode
                let ext = match backendReq with RequiresCuda -> ".cu" | RequiresMpi | CpuOnly -> ".cpp"
                let cppFile = Path.Combine(outputDir, safeName + ext)
                File.WriteAllText(cppFile, cppCode)

                // Same warning-pin rule as the single-file verdict, from the
                // same function, so the two lanes cannot disagree about what
                // counts as an expected warning. Checked before the compile
                // because an unpinned warning fails the test either way and
                // saying so costs nothing.
                let warnMisses =
                    warningPinMisses warnPins warnCodegenPins capturedWarnings codegenWarnings
                if not warnMisses.IsEmpty then
                    Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail testName
                        (String.concat " ; " warnMisses)
                    failed <- failed + 1
                    failedNames <- failedNames @ [testName]
                elif gppAvailable then
                    match compileForBackend capabilities.Value backendReq cppFile outputDir with
                    | Error e when isSkipError e ->
                        Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Skip testName e
                        skipped <- skipped + 1
                    | Error e ->
                        Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail testName (sprintf "compile: %s" e)
                        failed <- failed + 1
                        failedNames <- failedNames @ [testName]
                    | Ok exeFile ->
                        match runExecutable exeFile with
                        | Ok (0, output) ->
                            // Parse expected values from the LAST source (Main module)
                            let mainSource = sources |> List.last |> snd
                            let expectedValues = parseExpectedValues mainSource
                            // Same rule as the single-file runner: a pin that
                            // does not parse is a failure, not a silently
                            // skipped assertion. No multi-file test is a probe,
                            // so there is no prose exemption to apply here.
                            let malformed = parseMalformedExpectLines mainSource
                            if not malformed.IsEmpty then
                                Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail testName
                                    (sprintf "unparseable EXPECT pin(s): %s" (String.concat " | " malformed))
                                failed <- failed + 1
                                failedNames <- failedNames @ [testName]
                            elif expectedValues.IsEmpty then
                                Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Pass testName "no EXPECT"
                                passed <- passed + 1
                            else
                                match checkExpectedValues expectedValues output with
                                | Ok () ->
                                    Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Pass testName ""
                                    passed <- passed + 1
                                | Error msgs ->
                                    Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail testName (sprintf "values: %s" (String.concat "; " msgs))
                                    failed <- failed + 1
                                    failedNames <- failedNames @ [testName]
                        | Ok (code, output) ->
                            Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail testName (sprintf "exit %d: %s" code output)
                            failed <- failed + 1
                            failedNames <- failedNames @ [testName]
                        | Error e ->
                            Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail testName (sprintf "run: %s" e)
                            failed <- failed + 1
                            failedNames <- failedNames @ [testName]
                else
                    Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Skip testName "no g++"
                    skipped <- skipped + 1
            with ex ->
                Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail testName (sprintf "gen: %s" ex.Message)
                failed <- failed + 1
                failedNames <- failedNames @ [testName]

    Blade.Tests.TestHarness.printFooter name [sprintf "%d passed" passed; sprintf "%d failed" failed; sprintf "%d skipped" skipped]
    { Block = name; Passed = passed; Failed = failed; Skipped = skipped; FailedNames = failedNames }

/// Run test category with full C++ compilation and execution
let runTestCategoryFull (name: string) (tests: (string * string) list) (outputDir: string) =
    printHeader (sprintf "Blade-DSL: %s Tests (Full C++ Pipeline)" name)
    
    // Check g++ availability
    let gppAvailable = checkGppAvailable ()
    if not gppAvailable then
        printfn "WARNING: g++ not available or not working properly."
        printfn "This often happens on Windows due to MinGW DLL issues."
        printfn "C++ compilation will be skipped. Files will still be generated.\n"
        printfn "To fix, try:"
        printfn "  1. Reinstall MinGW-w64 from https://winlibs.com/"
        printfn "  2. Use WSL (Windows Subsystem for Linux)"
        printfn "  3. Use Visual Studio's cl.exe compiler\n"
    else
        printfn "g++ found and working. Will compile and run generated C++.\n"
    
    // Ensure output directory exists
    if not (Directory.Exists outputDir) then
        Directory.CreateDirectory outputDir |> ignore
    
    // Write runtime header file once
    CodeGen.deployRuntimeHeaders outputDir
    
    printfn "Running %d tests...\n" (List.length tests)
    
    let testArray = tests |> Array.ofList
    let total = testArray.Length
    
    let results = 
        // Ordered output buffer: collect results and print in original order
        let resultBuffer = System.Collections.Concurrent.ConcurrentDictionary<int, string>()
        let mutable nextToPrint = 0
        let printLock = obj()
        
        testArray
        |> Array.Parallel.mapi (fun idx (testName, source) ->
            let result = runFullTest testName source outputDir gppAvailable
            let status =
                match result.CompileResult with
                | Ok _ -> "ok"
                | Error e when e = "IR failed" -> "IR fail"
                | Error e when isSkipError e -> "skip"
                | Error _ -> "compile fail"
            let line = sprintf "[%d/%d] %s... %s" (idx + 1) total testName status
            
            // Buffer this result and flush any sequential completions
            resultBuffer.[idx] <- line
            lock printLock (fun () ->
                while resultBuffer.ContainsKey(nextToPrint) do
                    let msg = resultBuffer.[nextToPrint]
                    resultBuffer.TryRemove(nextToPrint) |> ignore
                    eprintfn "%s" msg
                    nextToPrint <- nextToPrint + 1)
            
            result)
        |> Array.toList
    
    // Find first compile failure to show full error (skips are not failures).
    // Reject-probes (name ends in "(rejects)") are EXPECTED to fail compilation
    // and are already counted as passes, so they must not be surfaced here — this
    // diagnostic is for UNEXPECTED compile failures only.
    let firstCompileFailure = 
        results |> List.tryFind (fun r -> 
            not (r.TestName.EndsWith "(rejects)") &&
            (match r.CompileResult with 
             | Error e when not (isSkipError e) && e <> "IR failed" -> true 
             | _ -> false))
    
    // Print results (brief for most, full for first failure)
    printfn ""
    let mutable shownFullError = false
    for result in results do
        let showFull = 
            not shownFullError && 
            (Some result = firstCompileFailure)
        if showFull then shownFullError <- true
        printFullTestResult result true showFull
    
    // If there was a compile failure, show the full error output
    match firstCompileFailure with
    | Some failure ->
        printfn "\n========== First Compile Failure: %s ==========" failure.TestName
        match failure.CompileResult with
        | Error e ->
            printfn "\nFull compiler output:"
            printfn "%s" e
        | _ -> ()
        match failure.CppFile with
        | Some cppFile -> printfn "\nGenerated file: %s" cppFile
        | None -> ()
    | None -> ()
    
    // Summary.
    //
    // Every count below is derived from ONE classification pass, so no line of
    // this block can contradict another or contradict the grand total. The
    // summary used to print "IR Lowering: 775 passed, 146 failed" for a run
    // whose grand total said "0 failed", with nothing to explain that the 146
    // were deliberate rejections — two true numbers that could not be
    // reconciled by a reader. The probe population is now stated outright and
    // the IR line says which rejections were expected.
    let verdicts = results |> List.map (fun r -> r, classify r)
    let countWhere pred = verdicts |> List.filter (snd >> pred) |> List.length
    let passed  = countWhere (fun v -> v = Blade.Tests.TestHarness.Pass)
    let failed  = countWhere (fun v -> v = Blade.Tests.TestHarness.Fail)
    let skipped = countWhere (fun v -> v = Blade.Tests.TestHarness.Skip)
    let failedResults = verdicts |> List.filter (fun (_, v) -> v = Blade.Tests.TestHarness.Fail) |> List.map fst
    let isSkippedVerdict (r: FullTestResult) = classify r = Blade.Tests.TestHarness.Skip

    let irPassed = results |> List.filter isIRPass |> List.length
    let irFailed = results.Length - irPassed
    let fullPassed = results |> List.filter isFullPass |> List.length
    let compiled = results |> List.filter (fun r -> match r.CompileResult with Ok _ -> true | _ -> false) |> List.length
    let generated = results |> List.filter (fun r -> r.CppGenerated) |> List.length

    // Probe population. A reject-probe that lowers is NOT counted as an
    // expected rejection here — only one that actually got refused at the
    // stage it pins, which is exactly the classifier's Pass condition for a
    // RejectAtLower probe.
    let rejectProbes = results |> List.filter isRejectProbe
    let abortProbes = results |> List.filter isAbortProbe
    let rejectAtLower = rejectProbes |> List.filter (fun r -> r.RejectStage = RejectAtLower)
    let rejectAtCodegen = rejectProbes |> List.filter (fun r -> r.RejectStage = RejectAtCodegen)
    let expectedIrRejections =
        rejectAtLower |> List.filter (fun r -> not (isIRPass r)) |> List.length
    let unexpectedIrFailures = irFailed - expectedIrRejections

    // Reject-probes that pin only the STAGE, not the REASON. Such a probe passes
    // on "the compiler refused this", a condition an UNRELATED refusal satisfies
    // just as well as the rule the probe was written to guard — so it can sit
    // green over a checker that no longer runs. That is a real, reproduced
    // failure mode, not a theoretical one, and it is invisible in a pass/fail
    // tally, so the remaining exposure is stated as a number until every probe
    // carries a `// ERROR:` pin.
    let unpinnedRejectProbes =
        rejectProbes |> List.filter (fun r -> not (hasRejectReasonPins r)) |> List.length

    // Tests carrying an EXPECT line the harness could not parse. Reported so
    // a corpus authoring error is visible as such rather than as a mysterious
    // failure in an unrelated stage.
    let malformedPinTests = results |> List.filter (fun r -> not r.MalformedExpectLines.IsEmpty) |> List.length

    // Tests whose codegen inferred the CUDA backend (emitted device
    // kernels → .cu source). Reported separately so the CPU/CUDA split
    // is visible at a glance.
    let cudaTests =
        results |> List.filter (fun r ->
            match r.CppFile with Some f -> f.EndsWith(".cu") | None -> false) |> List.length

    // Count value check results (only for tests that have expected values
    // AND weren't skipped — a skipped test has no output to check).
    let testsWithExpected = results |> List.filter (fun r -> r.HasExpectedValues && not (isSkippedVerdict r))
    let valuesPassed = testsWithExpected |> List.filter (fun r ->
        match r.ValueCheckResult with Ok () -> true | _ -> false) |> List.length

    let caps = capabilities.Value
    let platformStr = match caps.Platform with PWindows -> "Windows" | PLinux -> "Linux" | PMacOS -> "macOS"

    printHeader "Test Summary"
    printfn "Environment:  %s | g++:%b nvcc:%b cl:%b gpu:%b"
        platformStr caps.HasGpp caps.HasNvcc caps.HasCl caps.HasGpu
    printfn "IR Lowering:  %d lowered, %d rejected (%d expected, %d unexpected)"
        irPassed irFailed expectedIrRejections unexpectedIrFailures
    if not rejectProbes.IsEmpty || not abortProbes.IsEmpty then
        printfn "Probes:       %d reject (%d lower, %d codegen), %d abort"
            rejectProbes.Length rejectAtLower.Length rejectAtCodegen.Length abortProbes.Length
    if unpinnedRejectProbes > 0 then
        printfn "Unpinned:     %d reject-probe(s) lack // ERROR: pins -- they assert THAT the compiler refused them, not WHY"
            unpinnedRejectProbes
    printfn "C++ Generated: %d / %d  (CUDA backend: %d)" generated results.Length cudaTests
    if gppAvailable then
        printfn "Compiled:     %d / %d" compiled results.Length
        printfn "Full Pass:    %d / %d (IR + Compile + Run)" fullPassed results.Length
        if testsWithExpected.Length > 0 then
            printfn "Value Check:  %d / %d" valuesPassed testsWithExpected.Length
    else
        printfn "Generated files in: %s" (Path.GetFullPath outputDir)
    if malformedPinTests > 0 then
        printfn "Bad EXPECT:   %d test(s) with an unparseable pin (counted as failures)" malformedPinTests
    // The block's own verdict line — the same three numbers this block
    // contributes to the grand total, so the two are checkable by eye.
    printfn "Verdict:      %d passed, %d failed, %d skipped" passed failed skipped
    printfn "Total Tests:  %d" results.Length

    // The BlockResult IS the verdict tally: same classifier, same numbers as
    // the "Verdict:" line above and as every per-test [PASS]/[FAIL]/[SKIP].
    { Block = name; Passed = passed; Failed = failed;
      Skipped = skipped; FailedNames = failedResults |> List.map (fun r -> r.TestName) }

/// Run tests with C++ generation only (no compilation)
let runTestCategoryGenOnly (name: string) (tests: (string * string) list) (outputDir: string) =
    printHeader (sprintf "Blade-DSL: %s Tests (Generate C++ Only)" name)
    
    // Ensure output directory exists
    if not (Directory.Exists outputDir) then
        Directory.CreateDirectory outputDir |> ignore
    
    // Write runtime header file once
    CodeGen.deployRuntimeHeaders outputDir
    
    printfn "Generating C++ for %d tests to %s...\n" (List.length tests) (Path.GetFullPath outputDir)
    
    let mutable irPassed = 0
    let mutable irFailed = 0
    let mutable generated = 0
    
    for (testName, source) in tests do
        // Captured rather than sprayed from inside `lower`; this lane prints
        // them attributed below, next to the test they belong to. Like
        // --ir-only this is an explicit inspection lane, not part of the
        // default `blade test` run.
        let (lowered, warnings) = lowerCaptured source
        match lowered with
        | Error e ->
            printfn "  [IR:FAIL] %s" testName
            printfn "    Error: %s" e
            for w in warnings do
                printfn "    [warn %s] %s" w.Code (w.Message.Replace("\r\n", " ").Replace("\n", " "))
            irFailed <- irFailed + 1
        | Ok ir ->
            match IR.validateIR ir with
            | Error validationErrors ->
                printfn "  [IR:FAIL] %s (validation)" testName
                for e in validationErrors do
                    printfn "    %s" e
                irFailed <- irFailed + 1
            | Ok ir ->
            irPassed <- irPassed + 1
            for w in warnings do
                printfn "    [warn %s] %s" w.Code (w.Message.Replace("\r\n", " ").Replace("\n", " "))
            let safeName = sanitizeFileName testName
            let cppFile = Path.Combine(outputDir, safeName + ".cpp")
            try
                let (cppCode, codegenWarnings) = CodeGen.genSelfContainedProgramFromIR ir testName
                // Kept: this is the generate-and-inspect lane (`blade test
                // --gen`), whose entire output is per-test detail, and it is
                // not part of the default `blade test` run. The suite lanes'
                // copies of this print are gone in favour of `// WARN-CODEGEN:`.
                for w in codegenWarnings do
                    printfn "  [CodeGen Warning] %s" w
                File.WriteAllText(cppFile, cppCode)
                printfn "  [IR:OK] [Gen:OK] %s -> %s" testName (Path.GetFileName cppFile)
                generated <- generated + 1
            with ex ->
                printfn "  [IR:OK] [Gen:FAIL] %s" testName
                printfn "    Error: %s" ex.Message
    
    printHeader "Test Summary"
    printfn "IR Lowering:   %d passed, %d failed" irPassed irFailed
    printfn "C++ Generated: %d / %d" generated (irPassed + irFailed)
    printfn "Output folder: %s" (Path.GetFullPath outputDir)
    
    if irFailed > 0 then 1 else 0
