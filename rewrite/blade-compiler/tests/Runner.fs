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
    | FpCppGenerated of IRProgram * string * string list * BackendReq  // ir, srcFile, warnings, backend
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
                        let ext = match backendReq with RequiresCuda -> ".cu" | CpuOnly -> ".cpp"
                        let srcFile = Path.Combine(outputDir, safeName + ext)
                        File.WriteAllText(srcFile, cppCode)
                        FpCppGenerated (ir, srcFile, codegenWarnings, backendReq)
                    with ex ->
                        FpGenError (ir, sprintf "Generation failed: %s" ex.Message)
    )

/// Run a full test: IR lowering + C++ generation + compilation + execution
let runFullTest (testName: string) (source: string) (outputDir: string) (compileAndRun: bool) : FullTestResult =
    // Parse expected values from source comments
    let expectedValues = parseExpectedValues source

    // F# pipeline (lower + codegen) runs under a lock to avoid cache
    // races. C++ compile and run (below) stay outside the lock so they
    // parallelize freely across tests.
    let pipelineOutcome = runFsharpPipelineLocked source testName outputDir compileAndRun

    match pipelineOutcome with
    | FpIRError e ->
        { TestName = testName; IRResult = Error e; CppGenerated = false;
          CppFile = None; CompileResult = Error "IR failed"; RunResult = Error "IR failed";
          ValueCheckResult = Error ["IR failed"]; HasExpectedValues = not expectedValues.IsEmpty }
    | FpIRValidationError validationErrors ->
        for e in validationErrors do printfn "  %s" e
        { TestName = testName; IRResult = Error (validationErrors |> String.concat "; "); CppGenerated = false;
          CppFile = None; CompileResult = Error "IR validation failed"; RunResult = Error "IR validation failed";
          ValueCheckResult = Error ["IR validation failed"]; HasExpectedValues = not expectedValues.IsEmpty }
    | FpIROnly ir ->
        { TestName = testName; IRResult = Ok ir; CppGenerated = false;
          CppFile = None; CompileResult = Error "Skipped"; RunResult = Error "Skipped";
          ValueCheckResult = Error ["Skipped"]; HasExpectedValues = not expectedValues.IsEmpty }
    | FpGenError (ir, msg) ->
        { TestName = testName; IRResult = Ok ir; CppGenerated = false;
          CppFile = None; CompileResult = Error msg;
          RunResult = Error "Generation failed"; ValueCheckResult = Error ["Generation failed"];
          HasExpectedValues = not expectedValues.IsEmpty }
    | FpCppGenerated (ir, srcFile, codegenWarnings, backendReq) ->
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
              ValueCheckResult = Error [runErr]; HasExpectedValues = not expectedValues.IsEmpty }
        | Ok exeFile ->
            // Step 4: Run — but a CUDA-requiring test on a GPU-less box can
            // compile yet not execute. Validate the compile, skip the run.
            if backendReq = RequiresCuda && not caps.HasGpu then
                { TestName = testName; IRResult = Ok ir; CppGenerated = true;
                  CppFile = Some srcFile; CompileResult = Ok exeFile;
                  RunResult = Error "Skipped: no GPU";
                  ValueCheckResult = Error ["Skipped: no GPU"];
                  HasExpectedValues = not expectedValues.IsEmpty }
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
                  ValueCheckResult = valueCheckResult; HasExpectedValues = not expectedValues.IsEmpty }

/// Print a full test result
let printFullTestResult (result: FullTestResult) (verbose: bool) (showFullError: bool) =
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
    
    // Fold the per-stage statuses into a single standardized result line.
    // Overall outcome: FAIL if any stage failed; SKIP if a stage skipped (and
    // none failed); otherwise PASS. The detail differs by outcome:
    //   PASS -> the list of stages that actually ran, e.g. "(IR,Gen,Compile,Run,Val)"
    //   FAIL -> the first failing stage and its kind, e.g. "Compile failed"
    //   SKIP -> the skipped stage, e.g. "Compile skipped"
    let stages =
        [ "IR", irStatus
          "Gen", cppStatus
          "Compile", compileStatus
          "Run", runStatus ]
        @ (if valueStatus = "" then [] else [ "Val", valueStatus ])
    let anyFail = stages |> List.exists (fun (_, s) -> s = "FAIL" || s.StartsWith "EXIT")
    let anySkip = stages |> List.exists (fun (_, s) -> s = "SKIP")
    // A reject-probe (name ends in "(rejects)") is SUPPOSED to fail. If it
    // does, that's a pass; if it unexpectedly succeeds, that's the failure.
    let isReject = result.TestName.EndsWith "(rejects)"
    let outcome =
        if isReject then
            if anyFail then Blade.Tests.TestHarness.Pass    // correctly rejected
            else Blade.Tests.TestHarness.Fail               // should have been rejected
        elif anyFail then Blade.Tests.TestHarness.Fail
        elif anySkip then Blade.Tests.TestHarness.Skip
        else Blade.Tests.TestHarness.Pass
    let detail =
        if isReject then
            if anyFail then
                let (stg, _) = stages |> List.find (fun (_, s) -> s = "FAIL" || s.StartsWith "EXIT")
                sprintf "correctly rejected at %s" stg
            else "expected rejection but it was accepted"
        else
            match outcome with
            | Blade.Tests.TestHarness.Pass ->
                // One-liner for passes (#3): the stages that ran, as the detail.
                sprintf "(%s)" (stages |> List.map fst |> String.concat ",")
            | Blade.Tests.TestHarness.Fail ->
                let (stg, st) = stages |> List.find (fun (_, s) -> s = "FAIL" || s.StartsWith "EXIT")
                if st.StartsWith "EXIT" then sprintf "%s %s" stg st
                else sprintf "%s failed" stg
            | Blade.Tests.TestHarness.Skip ->
                let (stg, _) = stages |> List.find (fun (_, s) -> s = "SKIP")
                sprintf "%s skipped" stg
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

/// Determine if a test result is a full pass
let isFullPass (result: FullTestResult) =
    match result.IRResult, result.CompileResult, result.RunResult with
    | Ok _, Ok _, Ok (0, _) -> true
    | _ -> false

/// Determine if IR passed (regardless of C++)
let isIRPass (result: FullTestResult) =
    match result.IRResult with Ok _ -> true | _ -> false

/// A test whose name ends in "(rejects)" is an intentional reject-probe: the
/// CORRECT outcome is that it fails IR (or otherwise refuses to compile). We
/// treat such a test as PASSING when it does fail, and as FAILING only if it
/// unexpectedly slips through. This keeps the grand-total "failed tests" list
/// honest — intentional rejects that behave correctly are not failures.
let isRejectProbe (result: FullTestResult) =
    result.TestName.EndsWith "(rejects)"

/// Did the test behave correctly? For a normal test, that's a full pass. For a
/// reject-probe, that's correctly NOT passing (it was supposed to be rejected).
let isCorrectOutcome (result: FullTestResult) =
    if isRejectProbe result then not (isFullPass result)
    else isFullPass result

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
                let ext = match backendReq with RequiresCuda -> ".cu" | CpuOnly -> ".cpp"
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
                            if expectedValues.IsEmpty then
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
    
    // Summary
    let irPassed = results |> List.filter isIRPass |> List.length
    let irFailed = results.Length - irPassed
    let fullPassed = results |> List.filter isFullPass |> List.length
    let compiled = results |> List.filter (fun r -> match r.CompileResult with Ok _ -> true | _ -> false) |> List.length
    let generated = results |> List.filter (fun r -> r.CppGenerated) |> List.length

    // A test is "skipped" if either its compile or its run was skipped
    // (no toolchain, or no GPU for a CUDA-requiring test). Skips are not
    // failures and must not deflate the pass totals.
    let isSkipped (r: FullTestResult) =
        (match r.CompileResult with Error e when isSkipError e -> true | _ -> false) ||
        (match r.RunResult with Error e when isSkipError e -> true | _ -> false)
    let skipped = results |> List.filter isSkipped |> List.length

    // Tests whose codegen inferred the CUDA backend (emitted device
    // kernels → .cu source). Reported separately so the CPU/CUDA split
    // is visible at a glance.
    let cudaTests =
        results |> List.filter (fun r ->
            match r.CppFile with Some f -> f.EndsWith(".cu") | None -> false) |> List.length
    
    // Count value check results (only for tests that have expected values
    // AND weren't skipped — a skipped test has no output to check).
    let testsWithExpected = results |> List.filter (fun r -> r.HasExpectedValues && not (isSkipped r))
    let valuesPassed = testsWithExpected |> List.filter (fun r -> 
        match r.ValueCheckResult with Ok () -> true | _ -> false) |> List.length

    let caps = capabilities.Value
    let platformStr = match caps.Platform with PWindows -> "Windows" | PLinux -> "Linux" | PMacOS -> "macOS"
    
    printHeader "Test Summary"
    printfn "Environment:  %s | g++:%b nvcc:%b cl:%b gpu:%b"
        platformStr caps.HasGpp caps.HasNvcc caps.HasCl caps.HasGpu
    printfn "IR Lowering:  %d passed, %d failed" irPassed irFailed
    printfn "C++ Generated: %d / %d  (CUDA backend: %d)" generated results.Length cudaTests
    if gppAvailable then
        printfn "Compiled:     %d / %d" compiled results.Length
        printfn "Full Pass:    %d / %d (IR + Compile + Run)" fullPassed results.Length
        if skipped > 0 then
            printfn "Skipped:      %d (toolchain/GPU unavailable)" skipped
        if testsWithExpected.Length > 0 then
            printfn "Value Check:  %d / %d" valuesPassed testsWithExpected.Length
    else
        printfn "Generated files in: %s" (Path.GetFullPath outputDir)
    printfn "Total Tests:  %d" results.Length

    // Build the BlockResult for the grand-total roll-up using reject-aware
    // classification: a "(rejects)" probe that correctly fails counts as a
    // pass; only genuinely-wrong outcomes are failures and appear in the list.
    // Skipped tests (no toolchain/GPU) are neither.
    let nonSkipped = results |> List.filter (fun r -> not (isSkipped r))
    let correctResults = nonSkipped |> List.filter isCorrectOutcome
    let failedResults  = nonSkipped |> List.filter (fun r -> not (isCorrectOutcome r))
    { Block = name; Passed = correctResults.Length; Failed = failedResults.Length;
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
