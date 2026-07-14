// Command-line interface: argument parsing and command dispatch, plus the
// user-facing compile/run/check/emit commands. Extracted from Main.fs
// (audit §2.3) — Main.fs is now the entry point only.
module Blade.Cli

open System
open System.IO
open Blade.Build
open Blade.Tests.Runner
open Blade.Tests.RunAll
open Blade.Tests.Basic
open Blade.Tests.Loops
open Blade.Tests.Symmetry
open Blade.Tests.Reynolds
open Blade.Tests.Arity
open Blade.Tests.Functions
open Blade.Tests.Structs
open Blade.Tests.SumTypes
open Blade.Tests.Interfaces
open Blade.Tests.Modules
open Blade.Tests.Guards
open Blade.Tests.Bracketed
open Blade.Tests.IndexTypes
open Blade.Tests.Mutability
open Blade.Tests.Static
open Blade.Tests.Units
open Blade.Tests.Sqlish
open Blade.Tests.Normalize
open Blade.Tests.Unify
open Blade.Tests.ValidateArrow
open Blade.Tests.ExprAttrs
open Blade.Tests.CodeGenSubst
open Blade.Tests.FuncArrays
open Blade.Lowering

module TH = Blade.Tests.TestHarness

let compilerVersion = "0.19.2"

let printUsage () =
    printfn "Blade Compiler v%s" compilerVersion
    printfn ""
    printfn "Usage: blade <command> [options]"
    printfn ""
    printfn "Commands:"
    printfn "  compile <file.edgi> [-o output]   Compile to C++ (and optionally to executable)"
    printfn "  run <file.edgi>                   Compile and run a Blade program"
    printfn "  check <file.edgi>                 Type-check only (no code generation)"
    printfn "  ide check --json <file.edgi>      Type-check and emit JSON diagnostics + binding types"
    printfn "                                    (machine-readable, for editor tooling)"
    printfn "  repl                              Interactive session: each input recompiles and"
    printfn "                                    re-runs the accumulated program, printing new values"
    printfn "  emit <file.edgi> [-o output.cpp]  Emit C++ source without compiling"
    printfn "  test                              Run full test suite (IR + C++ + run)"
    printfn "  test --omp                        ... including the OpenMP thread-coverage block"
    printfn "  test --cuda                       ... including the CUDA kernel block"
    printfn "                                    (Windows: run from the x64 Native Tools prompt"
    printfn "                                     so nvcc finds cl.exe)"
    printfn "  test --timing                     ... including the differential timing block (slow)"
    printfn "                                    (the --omp/--cuda/--timing flags can be combined)"
    printfn "  test --ir-only                    Run IR-only tests (fast, no C++ compilation)"
    printfn "  test alloc                        Run C++ allocation-layout tests (contiguity/cardinality)"
    printfn "  test omp-coverage                 Run the OpenMP thread-coverage block standalone"
    printfn "  test cuda                         Run the CUDA kernel block standalone"
    printfn "  test timing                       Run the differential timing block standalone"
    printfn "  test diff-oracle [category]       Diff printed values against the pinned ./oracle build"
    printfn ""
    printfn "Options:"
    printfn "  -o <path>      Output file path"
    printfn "  --verbose      Show IR and generated C++"
    printfn "  --help         Show this help"
    printfn ""
    printfn "Examples:"
    printfn "  blade run myprogram.edgi"
    printfn "  blade emit myprogram.edgi -o myprogram.cpp"
    printfn "  blade compile myprogram.edgi -o myprogram"
    printfn "  blade test"
    printfn "  blade test --omp --cuda --timing"

/// Compile a .edgi file to C++ source string
let compileFile (filePath: string) (verbose: bool) : Result<string * string list, string> =
    if not (File.Exists filePath) then
        Error (sprintf "File not found: %s" filePath)
    else
        let source = File.ReadAllText(filePath)
        let testName = Path.GetFileNameWithoutExtension(filePath)
        match lower source with
        | Error e -> Error e
        | Ok ir ->
            match IR.validateIR ir with
            | Error errs -> Error (errs |> String.concat "\n")
            | Ok ir ->
                let (cppCode, warnings) = CodeGen.genSelfContainedProgramFromIR ir testName
                if verbose then
                    for w in warnings do
                        eprintfn "[Warning] %s" w
                Ok (cppCode, warnings)

/// Compile a .edgi file to an executable
let compileToExe (filePath: string) (outputPath: string option) (verbose: bool) : Result<string, string> =
    match compileFile filePath verbose with
    | Error e -> Error e
    | Ok (cppCode, warnings) ->
        let baseName = Path.GetFileNameWithoutExtension(filePath)
        let dir = Path.GetDirectoryName(Path.GetFullPath(filePath))
        let dir = if String.IsNullOrEmpty dir then "." else dir
        // Infer backend from generated source: device kernels → .cu + nvcc.
        let backendReq = inferBackendReq cppCode
        let ext = match backendReq with RequiresCuda -> ".cu" | CpuOnly -> ".cpp"
        let cppFile = Path.Combine(dir, baseName + ext)
        File.WriteAllText(cppFile, cppCode)
        // The generated source `#include`s the C++ runtime headers with plain
        // quotes and compileForBackend passes no -I, so the headers must sit
        // next to the .cpp (the test runners deploy them into their output
        // dirs for the same reason). Record which ones we newly created so
        // cleanup removes only our copies, never a pre-existing file.
        let deployedHeaders =
            CodeGen.runtimeHeaderNames
            |> List.map (fun name -> Path.Combine(dir, name))
            |> List.filter (fun path -> not (File.Exists path))
        CodeGen.deployRuntimeHeaders dir
        if verbose then
            eprintfn "[Emit] %s" cppFile
        match compileForBackend capabilities.Value backendReq cppFile dir with
        | Error e ->
            Error (sprintf "Compilation failed:\n%s" e)
        | Ok exePath ->
            // If user specified output path, move the exe there
            let finalPath =
                match outputPath with
                | Some out ->
                    let outFull = Path.GetFullPath(out)
                    if exePath <> outFull then
                        try File.Copy(exePath, outFull, true) with _ -> ()
                    outFull
                | None -> exePath
            // Clean up intermediates (.cpp + the headers we deployed); verbose
            // keeps both so the generated source can be inspected/recompiled.
            if not verbose then
                try File.Delete(cppFile) with _ -> ()
                for h in deployedHeaders do
                    try File.Delete(h) with _ -> ()
            if verbose then
                eprintfn "[Compile] %s" finalPath
            Ok finalPath

/// Run a .edgi file: compile and execute
let runFile (filePath: string) (verbose: bool) : int =
    match compileToExe filePath None verbose with
    | Error e ->
        eprintfn "Error: %s" e
        1
    | Ok exePath ->
        match runExecutable exePath with
        | Error e ->
            eprintfn "Runtime error: %s" e
            1
        | Ok (exitCode, output) ->
            printf "%s" output
            exitCode

// ----------------------------------------------------------------------------
// Interactive REPL (`blade repl`)
// ----------------------------------------------------------------------------
//
// Blade has no interpreter, but `blade run` semantics give REPL behavior for
// free: every top-level binding prints its value. The REPL accumulates a
// session program in a temp file; each submitted snippet re-compiles and
// re-runs the WHOLE session, printing only output lines that are new or
// changed since the previous run. Rebinding a top-level name replaces the
// earlier definition (duplicate lets are a C++ redeclaration error), and the
// diff display then shows the updated values of everything downstream.
//
// The compiled session runs with the REPL process's own working directory,
// so relative data paths (NetCDF.load("sample.nc")) resolve where the user
// launched the REPL — not in the session temp dir.

/// Run a compiled session exe with an explicit working directory, capturing
/// stdout/stderr separately (runExecutable pins cwd to the exe's dir, which
/// would break relative data paths for REPL sessions).
let private runExeIn (cwd: string) (exeFile: string) : Result<int * string * string, string> =
    try
        let psi = System.Diagnostics.ProcessStartInfo(Path.GetFullPath exeFile)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        psi.WorkingDirectory <- cwd
        use proc = System.Diagnostics.Process.Start(psi)
        let stdoutTask = proc.StandardOutput.ReadToEndAsync()
        let stderrTask = proc.StandardError.ReadToEndAsync()
        if proc.WaitForExit(60000) then
            Ok (proc.ExitCode, stdoutTask.Result, stderrTask.Result)
        else
            (try proc.Kill() with _ -> ())
            Error "Execution timed out after 60s"
    with ex ->
        Error (sprintf "Execution exception: %s" ex.Message)

let replLoop () : int =
    printfn "Blade REPL (v%s) — every top-level binding prints its value." compilerVersion
    printfn "Commands: :reset (clear session)  :show (print session)  :quit"
    printfn "Multi-line: unbalanced brackets continue on the next line, or use :paste ... :end"
    let sessionDir = Path.Combine(Path.GetTempPath(), "blade-repl-" + Guid.NewGuid().ToString("N").Substring(0, 8))
    Directory.CreateDirectory sessionDir |> ignore
    let srcPath = Path.Combine(sessionDir, "session.blade")
    let userCwd = Directory.GetCurrentDirectory()
    let session = ResizeArray<string>()
    let mutable lastLines : string[] = [||]

    // Top-level name a snippet (re)defines, for rebind replacement.
    let bindingNameRe =
        System.Text.RegularExpressions.Regex(
            @"^\s*(?:let\s+(?:mut\s+|static\s+)?|static\s+function\s+|function\s+|type\s+)([A-Za-z_][A-Za-z0-9_]*)")
    let bindingName (snippet: string) =
        let m = bindingNameRe.Match snippet
        if m.Success then Some m.Groups.[1].Value else None

    // The generated main prints a "<name> completed in Xs" timing line whose
    // value changes every run — exclude it from the output diff.
    let isTimingLine (l: string) =
        System.Text.RegularExpressions.Regex.IsMatch(l, @"completed in [0-9.eE+~-]+m?s\s*$")

    let evaluate (snippet: string) =
        let trimmed = snippet.Trim()
        if trimmed <> "" then
            let candidate = ResizeArray(session)
            // Rebinding replaces the earlier definition IN PLACE so snippets
            // that referenced the name (defined later in the session) still
            // see it; the output diff then shows their recomputed values.
            match bindingName trimmed with
            | Some name ->
                let idx = candidate.FindIndex(fun s -> bindingName s = Some name)
                if idx >= 0 then candidate.[idx] <- trimmed else candidate.Add trimmed
            | None -> candidate.Add trimmed
            File.WriteAllText(srcPath, String.concat "\n\n" candidate + "\n")
            match compileToExe srcPath None false with
            | Error e ->
                eprintfn "%s" e
                eprintfn "[snippet not kept]"
            | Ok exePath ->
                match runExeIn userCwd exePath with
                | Error e ->
                    eprintfn "Runtime error: %s" e
                    eprintfn "[snippet not kept]"
                | Ok (code, stdout, stderr) ->
                    let lines =
                        stdout.Replace("\r\n", "\n").Split('\n')
                        |> Array.filter (fun l -> not (isTimingLine l))
                    let mutable printed = 0
                    for i in 0 .. lines.Length - 1 do
                        if lines.[i].Trim() <> "" && (i >= lastLines.Length || lines.[i] <> lastLines.[i]) then
                            printfn "%s" lines.[i]
                            printed <- printed + 1
                    if stderr.Trim() <> "" then eprintfn "%s" (stderr.Trim())
                    if code = 0 then
                        if printed = 0 then printfn "(ok)"   // defs print nothing new
                        session.Clear()
                        session.AddRange candidate
                        lastLines <- lines
                    else
                        eprintfn "[exit %d — snippet not kept]" code

    let bracketBalance (text: string) =
        let mutable d = 0
        for c in text do
            match c with
            | '(' | '[' | '{' -> d <- d + 1
            | ')' | ']' | '}' -> d <- d - 1
            | _ -> ()
        d

    let buffer = ResizeArray<string>()
    let mutable pasteMode = false
    let mutable finished = false
    while not finished do
        Console.Write(if pasteMode || buffer.Count > 0 then "  ... " else "blade> ")
        Console.Out.Flush()
        // Strip BOM/zero-width characters some clients prepend to piped input
        // (a U+FEFF-prefixed `let` otherwise defeats rebind detection).
        let readLine () =
            match Console.ReadLine() with
            | null -> null
            | l -> l.Replace("\uFEFF", "").Replace("\u200B", "")
        match readLine () with
        | null -> finished <- true
        | line when pasteMode ->
            if line.Trim() = ":end" then
                pasteMode <- false
                evaluate (String.concat "\n" buffer)
                buffer.Clear()
            else buffer.Add line
        | line when buffer.Count = 0 && line.Trim() = ":paste" -> pasteMode <- true
        | line when buffer.Count = 0 && (line.Trim() = ":quit" || line.Trim() = ":q") -> finished <- true
        | line when buffer.Count = 0 && line.Trim() = ":reset" ->
            session.Clear()
            lastLines <- [||]
            printfn "(session cleared)"
        | line when buffer.Count = 0 && line.Trim() = ":show" ->
            if session.Count = 0 then printfn "(empty session)"
            else printfn "%s" (String.concat "\n\n" session)
        | line ->
            buffer.Add line
            if bracketBalance (String.concat "\n" buffer) <= 0 then
                evaluate (String.concat "\n" buffer)
                buffer.Clear()
    try Directory.Delete(sessionDir, true) with _ -> ()
    0

/// End-to-end CLI smoke test: compile and run a one-line .edgi from a FRESH
/// temp directory containing nothing but the source file. The test runners
/// deploy the C++ runtime headers into their own output dirs before compiling,
/// so they cannot catch a compileToExe that forgets to — historically
/// `blade run` failed with "nested_array_utilities.hpp: No such file or
/// directory" unless the source happened to sit next to the headers. This is
/// the only block that exercises the user-facing path from a bare directory.
let runCliSmokeTests () : TH.BlockResult =
    let blockName = "CLI Smoke"
    TH.printHeader "CLI Smoke Test (blade run from a fresh directory)"
    let results = ResizeArray<string * TH.Outcome>()
    let record name outcome detail =
        TH.resultLine outcome name detail
        results.Add((name, outcome))
    let runTest = "compile+run one-liner from fresh temp dir"
    if not capabilities.Value.HasGpp then
        record runTest TH.Skip "requires g++, not found"
    else
        let tmpDir = Path.Combine(Path.GetTempPath(), "blade_cli_smoke_" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tmpDir) |> ignore
        try
            let srcFile = Path.Combine(tmpDir, "smoke.edgi")
            File.WriteAllText(srcFile, "let x = 1 + 2 * 3\n")
            match compileToExe srcFile None false with
            | Error e ->
                record runTest TH.Fail (e.Replace("\n", " | "))
            | Ok exePath ->
                (match runExecutable exePath with
                 | Error e -> record runTest TH.Fail e
                 | Ok (0, output) when output.Contains "x = 7" ->
                     record runTest TH.Pass ""
                 | Ok (code, output) ->
                     record runTest TH.Fail (sprintf "exit %d, output: %s" code (output.Trim())))
                // Non-verbose compiles must clean up after themselves: the
                // intermediate .cpp and the deployed runtime headers go away,
                // leaving the directory with only source + executable.
                let leftovers =
                    Directory.GetFiles(tmpDir)
                    |> Array.map Path.GetFileName
                    |> Array.filter (fun f ->
                        f.EndsWith(".cpp") || f.EndsWith(".cu") || f.EndsWith(".hpp") || f.EndsWith(".h"))
                if Array.isEmpty leftovers then
                    record "no intermediates left behind" TH.Pass ""
                else
                    record "no intermediates left behind" TH.Fail (String.concat ", " leftovers)
        finally
            try Directory.Delete(tmpDir, true) with _ -> ()
    let count o = results |> Seq.filter (fun (_, r) -> r = o) |> Seq.length
    let passed, failed, skipped = count TH.Pass, count TH.Fail, count TH.Skip
    let failedNames = results |> Seq.filter (fun (_, r) -> r = TH.Fail) |> Seq.map fst |> List.ofSeq
    let parts =
        [ sprintf "%d passed" passed; sprintf "%d failed" failed ]
        @ (if skipped > 0 then [sprintf "%d skipped" skipped] else [])
    TH.printFooter blockName parts
    { TH.BlockResult.Block = blockName
      Passed = passed
      Failed = failed
      Skipped = skipped
      FailedNames = failedNames }

/// Type-check a file without generating code
let checkFile (filePath: string) : int =
    if not (File.Exists filePath) then
        eprintfn "File not found: %s" filePath
        1
    else
        let source = File.ReadAllText(filePath)
        match Blade.Parser.parseProgram source with
        | Error e ->
            eprintfn "Parse error at %d:%d: %s" e.Line e.Col e.Message
            1
        | Ok program ->
            match Blade.TypeCheck.typeCheck program with
            | Error errors ->
                for e in errors do
                    eprintfn "%s" (Blade.TypeEnv.formatCompileError e)
                1
            | Ok (_, _, warnings) ->
                for w in warnings do
                    printfn "[TypeCheck Warning] %s" w
                printfn "OK"
                0

/// Emit C++ source to file or stdout
let emitFile (filePath: string) (outputPath: string option) (verbose: bool) : int =
    match compileFile filePath verbose with
    | Error e ->
        eprintfn "Error: %s" e
        1
    | Ok (cppCode, _) ->
        match outputPath with
        | Some outPath ->
            File.WriteAllText(outPath, cppCode)
            // Ship the runtime headers next to the emitted .cpp so it compiles
            // as-is (`g++ file.cpp`, no -I flag) — its `#include`s use plain
            // quotes and resolve relative to the source.
            let outDir = Path.GetDirectoryName(Path.GetFullPath(outPath))
            CodeGen.deployRuntimeHeaders (if String.IsNullOrEmpty outDir then "." else outDir)
            if verbose then
                eprintfn "[Emit] %s" outPath
            0
        | None ->
            printf "%s" cppCode
            0

/// Run the full suite, appending the CLI smoke block (which lives in this
/// file — see runAllTestsFullWith's doc comment for why it's passed in).
let private runFullSuite opts = runAllTestsFullWith [runCliSmokeTests] opts

/// Dispatch the `test` subcommand. `rest` is everything after "test".
let private dispatchTest (rest: string list) : int =
    // `--omp` / `--cuda` / `--timing` opt the corresponding blocks into the
    // full suite; they may appear in any order and combine.
    let isSuiteFlag f = f = "--omp" || f = "--cuda" || f = "--timing"
    match rest with
    | [] -> runFullSuite defaultFullSuiteOptions
    | flags when flags |> List.forall isSuiteFlag ->
        runFullSuite { IncludeOmp = List.contains "--omp" flags
                       IncludeCuda = List.contains "--cuda" flags
                       IncludeTiming = List.contains "--timing" flags }
    | [ "--ir-only" ] -> runAllTests ()
    | [ "--gen" ] -> runAllTestsGenOnly ()
    | [ "normalize" ] ->
        // IR-level F# unit tests for the type normalizer. Runs in-process,
        // no Blade source pipeline involved.
        let failed = (runNormalizeTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "unify" ] ->
        // TypeCheck-level F# unit tests for the unify §5.3 fast path.
        // Constructs IRType values directly and calls unify; no Blade
        // source pipeline.
        let failed = (runUnifyTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "validate-arrow" ] ->
        // IR-level F# unit tests for the validateArrowShape gate at
        // mkVirtualArrayArrow entry. Constructs IRType values directly;
        // no Blade source pipeline.
        let failed = (runValidateArrowTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "type-structure" ] ->
        // Type-level structural assertions on lowered Blade source: asserts the
        // deduced IR type (rank, per-group arity+symmetry, element type) of named
        // bindings via Blade's own matchesTypePattern relation. No codegen/run.
        let failed = (Blade.Tests.TypeStructure.runTypeStructureTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "attrs" ] ->
        // Phase B: IR-level F# unit tests for the exprAttrs bottom-up
        // attribute computation. Constructs IR fragments directly and
        // compares actual vs. expected attribute sets. No Blade source
        // pipeline.
        let failed = (runAttrsTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "subst" ] ->
        // Phase C Step 2: F# unit tests for the contains-substitution
        // mechanism in exprToCpp. Constructs IR fragments, renders with
        // populated and empty SubstMaps, asserts on the resulting C++
        // string. No Blade source pipeline.
        let failed = (runCodeGenSubstTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "shape" ] ->
        // F# unit tests for the canonical ExprShape traversal (§3.2):
        // childrenOf/rebuildWith round-trips, mapIRExpr identity, and
        // collectVarRefsIR completeness. No Blade source pipeline.
        let failed = (Blade.Tests.Shape.runShapeTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "diff-oracle" ] ->
        // Phase 4 differential gate: this binary vs the pinned ./oracle build
        // over the dense corpus slice — identical printed VALUES required.
        let failed = (Blade.Tests.DiffOracle.runDiffOracleTests "./oracle/Blade.exe" Blade.Tests.DiffOracle.denseSlice).Failed
        if failed = 0 then 0 else 1
    | [ "diff-oracle"; cat ] ->
        // Single corpus category against the pinned oracle.
        let failed = (Blade.Tests.DiffOracle.runDiffOracleTests "./oracle/Blade.exe" [cat]).Failed
        if failed = 0 then 0 else 1
    | [ "spans" ] ->
        // Error-location tests (§3.4 / Phase 2 gate): deliberately broken
        // sources, asserting the reported line. No C++ pipeline.
        let failed = (Blade.Tests.Spans.runSpanTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "oracles" ] ->
        // Phase 0.2 review block: the differential-harness oracles checked
        // against hand-computed / analytic values. No Blade source pipeline.
        let failed = (Blade.Tests.OracleReview.runOracleTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "alloc" ] ->
        // Standalone C++ runtime-layout tests for the contiguous-backing
        // allocate<>. Compiles + runs cpp/alloc_layout_tests.cpp against the
        // shipped headers. Verifies contiguity/cardinality invariants the
        // value-checking Blade tests cannot catch. No Blade source pipeline.
        let failed = (Blade.Tests.AllocTests.runAllocLayoutTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "omp-coverage" ] ->
        // OpenMP thread-coverage: generate representative loop programs with
        // codegen test-mode instrumentation, compile -fopenmp, run with forced
        // threads, verify emitted pragmas form genuine parallel regions.
        let failed = (Blade.Tests.OmpTests.runOmpCoverageTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "cli" ] ->
        // CLI smoke: compile+run a one-line .edgi from a fresh temp directory
        // via the user-facing compileToExe path (runtime-header deployment).
        let failed = (runCliSmokeTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "cuda" ] ->
        // CUDA kernel block standalone (differential vs host-loop oracle).
        // Skips cleanly when nvcc/GPU absent; on Windows run from the
        // x64 Native Tools prompt so nvcc finds cl.exe.
        let failed = (Blade.Tests.CudaTests.runCudaTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "timing" ] ->
        // Differential timing: measure the (r!)^d speedup of comm-annotation
        // and symmetric-type forms vs their dense equivalents. Reports ratios;
        // warns (never fails) on a slow ratio. Requires g++.
        let failed = (Blade.Tests.Benchmarks.runDifferentialTimingTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "netcdf" ] ->
        // NetCDF provider tests. Tests 1-6 run against a mock NcFile (pure,
        // always run). Tests 7-8 ("Live Load", "Blade Program Import") need
        // sample.nc in the working dir + libnetcdf, else they SKIP. Returns an
        // exit code directly (not a BlockResult like the other blocks).
        Blade.Tests.NetcdfTests.runNetcdfTests ()
    | [ cat ] ->
        // Test a specific category: blade test basic, blade test loops, etc.
        let categoryTests =
            match cat.ToLower().TrimStart('-') with
            | "basic" -> Some ("Basic", basicTests)
            | "loops" -> Some ("Loops", loopTests)
            | "symmetry" -> Some ("Symmetry", symmetryTests)
            | "reynolds" -> Some ("Reynolds", reynoldsTests)
            | "arity" -> Some ("Arity", arityTests)
            | "functions" -> Some ("Functions", functionTests)
            | "structs" -> Some ("Structs", structTests)
            | "sumtypes" -> Some ("Sum Types", sumTypeTests)
            | "interfaces" -> Some ("Interfaces", interfaceTests)
            | "modules" -> Some ("Modules", moduleTests)
            | "guards" -> Some ("Guards", guardTests)
            | "bracketed" -> Some ("Bracketed", bracketedTests)
            | "indextypes" -> Some ("Index Types", indexTypeTests)
            | "static" -> Some ("Static", staticTests)
            | "units" -> Some ("Units", unitTests)
            | "mutability" -> Some ("Mutability", mutabilityTests)
            | "funcarrays" | "fa" -> Some ("Func Arrays", funcArrayTests)
            | "sqlish" | "sql" -> Some ("SQL-ish", foreignKeyTests @ maskTests @ setOpTests @ groupByTests @ sortTests @ reduceTests @ extentsTests @ extentsMultiRankTests @ regressionTests @ sqlCombinedTests)
            | _ -> None
        match categoryTests with
        | Some (name, tests) ->
            let r = runTestCategoryFull name tests "./generated_cpp_tests"
            if r.Failed = 0 then 0 else 1
        | None -> eprintfn "Unknown test category: %s" cat; 1
    | _ -> printUsage (); 1

/// Top-level command dispatch (the body of the old Main.fs entry point).
let dispatch (args: string[]) : int =
    // Share the compiler version with the test-harness output helpers so every
    // block header reads "(vX.Y.Z)" consistently, including standalone runs.
    Blade.Tests.TestHarness.version <- compilerVersion
    match args with
    // ---- User-facing commands ----
    | [| "run"; file |] -> runFile file false
    | [| "run"; file; "--verbose" |] -> runFile file true

    | [| "compile"; file |] ->
        match compileToExe file None false with
        | Ok path -> printfn "%s" path; 0
        | Error e -> eprintfn "Error: %s" e; 1
    | [| "compile"; file; "-o"; output |] ->
        match compileToExe file (Some output) false with
        | Ok path -> printfn "%s" path; 0
        | Error e -> eprintfn "Error: %s" e; 1

    | [| "emit"; file |] -> emitFile file None false
    | [| "emit"; file; "-o"; output |] -> emitFile file (Some output) false
    | [| "emit"; file; "--verbose" |] -> emitFile file None true
    | [| "emit"; file; "-o"; output; "--verbose" |] -> emitFile file (Some output) true

    | [| "check"; file |] -> checkFile file

    | [| "repl" |] -> replLoop ()

    // ---- Editor tooling (JSON on stdout; see Ide.fs) ----
    | [| "ide"; "check"; "--json"; file |]
    | [| "ide"; "check"; file; "--json" |]
    | [| "ide"; "check"; file |] -> Blade.Ide.ideCheck file

    // ---- Test commands ----
    | _ when args.Length >= 1 && args.[0] = "test" ->
        dispatchTest (args.[1..] |> Array.toList)

    // ---- Legacy flags (backward compat) ----
    | [||] -> runFullSuite defaultFullSuiteOptions
    | [| "--full" |] -> runFullSuite defaultFullSuiteOptions
    | [| "--help" |] -> printUsage (); 0
    | _ -> printUsage (); 1
