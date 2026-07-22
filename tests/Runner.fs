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
}

let testLower source =
    match lower source with
    | Ok ir ->
        printfn "Lower: OK"
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
    | FpIRError of string
    | FpIRValidationError of string list
    | FpIROnly of IRProgram          // compileAndRun = false, no .cpp generated
    /// ir, srcFile, warnings, backend, emitted-#error-guard. The guard flag is
    /// read off the generated source HERE, while it is in memory, because a
    /// codegen-stage reject-probe's verdict depends on it.
    | FpCppGenerated of IRProgram * string * string list * BackendReq * bool
    | FpGenError of IRProgram * string  // ir was valid but codegen threw

let private runFsharpPipelineLocked (source: string) (testName: string) (outputDir: string) (compileAndRun: bool) : FsPipelineOutcome =
    lock fsharpPipelineLock (fun () ->
        let irResult = lower source
        match irResult with
        | Error e -> FpIRError e
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
                        FpCppGenerated (ir, srcFile, codegenWarnings, backendReq, emittedErrorGuard)
                    with ex ->
                        FpGenError (ir, sprintf "Generation failed: %s" ex.Message)
    )

/// Run a full test: IR lowering + C++ generation + compilation + execution
let runFullTest (testName: string) (source: string) (outputDir: string) (compileAndRun: bool) : FullTestResult =
    // Parse expected values from source comments
    let expectedValues = parseExpectedValues source
    let abortExpectation = parseAbortExpectations source
    let rejectStage = parseRejectStage source

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
    let pipelineOutcome =
        Blade.Runtime.runOnLargeStack (fun () ->
            runFsharpPipelineLocked source testName outputDir compileAndRun)

    // Hoisted so every result-record branch below can record it uniformly:
    // only the generated-source branch can have seen a `#error` guard, and
    // "no C++ was produced" must read as "no guard", never as unknown.
    let emittedErrorGuard =
        match pipelineOutcome with
        | FpCppGenerated (_, _, _, _, guard) -> guard
        | _ -> false

    match pipelineOutcome with
    | FpIRError e ->
        { TestName = testName; IRResult = Error e; CppGenerated = false;
          CppFile = None; CompileResult = Error "IR failed"; RunResult = Error "IR failed";
          ValueCheckResult = Error ["IR failed"]; HasExpectedValues = not expectedValues.IsEmpty; AbortExpectation = abortExpectation
          RejectStage = rejectStage; MalformedExpectLines = malformedExpectLines
          EmittedErrorGuard = emittedErrorGuard }
    | FpIRValidationError validationErrors ->
        for e in validationErrors do printfn "  %s" e
        { TestName = testName; IRResult = Error (validationErrors |> String.concat "; "); CppGenerated = false;
          CppFile = None; CompileResult = Error "IR validation failed"; RunResult = Error "IR validation failed";
          ValueCheckResult = Error ["IR validation failed"]; HasExpectedValues = not expectedValues.IsEmpty; AbortExpectation = abortExpectation
          RejectStage = rejectStage; MalformedExpectLines = malformedExpectLines
          EmittedErrorGuard = emittedErrorGuard }
    | FpIROnly ir ->
        { TestName = testName; IRResult = Ok ir; CppGenerated = false;
          CppFile = None; CompileResult = Error "Skipped"; RunResult = Error "Skipped";
          ValueCheckResult = Error ["Skipped"]; HasExpectedValues = not expectedValues.IsEmpty; AbortExpectation = abortExpectation
          RejectStage = rejectStage; MalformedExpectLines = malformedExpectLines
          EmittedErrorGuard = emittedErrorGuard }
    | FpGenError (ir, msg) ->
        { TestName = testName; IRResult = Ok ir; CppGenerated = false;
          CppFile = None; CompileResult = Error msg;
          RunResult = Error "Generation failed"; ValueCheckResult = Error ["Generation failed"];
          HasExpectedValues = not expectedValues.IsEmpty; AbortExpectation = abortExpectation
          RejectStage = rejectStage; MalformedExpectLines = malformedExpectLines
          EmittedErrorGuard = emittedErrorGuard }
    | FpCppGenerated (ir, srcFile, codegenWarnings, backendReq, _) ->
        for w in codegenWarnings do
            printfn "  [CodeGen Warning] %s" w

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
              EmittedErrorGuard = emittedErrorGuard }
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
                  EmittedErrorGuard = emittedErrorGuard }
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
                  EmittedErrorGuard = emittedErrorGuard }

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
///  4. An abort-probe must compile, run, and exit nonzero with its pinned
///     `// ABORT:` message (isExpectedAbort).
///
///  5. A normal test must be a full pass and, when it carries pins, must pass
///     the value check. Compiles-clean-but-prints-the-wrong-numbers is a
///     failure; that class is the entire reason EXPECT checks exist.
let classifyWithDetail (result: FullTestResult) : Blade.Tests.TestHarness.Outcome * string =
    let stages = stageStatuses result
    let anyFail = stages |> List.exists (fun (_, s) -> s = "FAIL" || s.StartsWith "EXIT")
    let anySkip = stages |> List.exists (fun (_, s) -> s = "SKIP")
    let compileSkipped =
        match result.CompileResult with Error e when isSkipError e -> true | _ -> false

    if not result.MalformedExpectLines.IsEmpty then
        Blade.Tests.TestHarness.Fail,
        sprintf "unparseable EXPECT pin(s): %s"
            (result.MalformedExpectLines |> String.concat " | ")

    elif isRejectProbe result then
        match result.RejectStage with
        | RejectAtLower ->
            match result.IRResult with
            | Error _ -> Blade.Tests.TestHarness.Pass, "correctly rejected during lowering"
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
                        Blade.Tests.TestHarness.Pass, "correctly rejected by the emitted #error guard"
                    | Ok _ ->
                        Blade.Tests.TestHarness.Fail,
                        "#error guard emitted but the C++ compiled anyway"

    elif isAbortProbe result then
        if isExpectedAbort result then
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
        match lowerMultiSource sources with
        | Ok ir ->
            printfn "Lower: OK (%d modules)" ir.Modules.Length
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
        match lowerMultiSource sources with
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
                for w in codegenWarnings do
                    printfn "    [CodeGen Warning] %s" w
                // Same backend inference as the single-file pipeline:
                // .cu + nvcc when device kernels are emitted, else .cpp + g++.
                let backendReq = inferBackendReq cppCode
                let ext = match backendReq with RequiresCuda -> ".cu" | RequiresMpi | CpuOnly -> ".cpp"
                let cppFile = Path.Combine(outputDir, safeName + ext)
                File.WriteAllText(cppFile, cppCode)

                if gppAvailable then
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
        match lower source with
        | Error e ->
            printfn "  [IR:FAIL] %s" testName
            printfn "    Error: %s" e
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
            let safeName = sanitizeFileName testName
            let cppFile = Path.Combine(outputDir, safeName + ".cpp")
            try
                let (cppCode, codegenWarnings) = CodeGen.genSelfContainedProgramFromIR ir testName
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
