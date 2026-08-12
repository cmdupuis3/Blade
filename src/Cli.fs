// Command-line interface: argument parsing and command dispatch, plus the
// user-facing compile/run/check/emit commands. Main.fs is the entry point only.
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
open Blade.Tests.Ppl
open Blade.Tests.Math
open Blade.Tests.Rand
open Blade.Tests.Spectra
open Blade.Tests.Fallback
open Blade.Tests.Sgs
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
    printfn "  run <file.edgi> --mpi <N>         ... with `where mpi` kernels decomposed across"
    printfn "                                    N ranks (compiled -lmsmpi, run under mpiexec)"
    printfn "  run <file.edgi> --memcheck        ... as a Debug+AddressSanitizer build that prints"
    printfn "                                    a BLADE-MEMCHECK allocator-stats line on stderr"
    printfn "                                    at exit (Windows: needs a vcvars64 environment;"
    printfn "                                    equivalently set BLADE_MEMCHECK=1)"
    printfn "  check <file.edgi>                 Type-check only (no code generation)"
    printfn "  ide check --json <file.edgi>      Type-check and emit JSON diagnostics + binding types"
    printfn "                                    (machine-readable, for editor tooling)"
    printfn "  ide serve                         Persistent editor daemon: NDJSON check requests on"
    printfn "                                    stdin, one JSON response line each on stdout"
    printfn "                                    (tier fast = typecheck; full = + monomorphization)"
    printfn "  repl                              Interactive session: each input recompiles and"
    printfn "                                    re-runs the accumulated program, printing new values"
    printfn "                                    with types; bare expressions evaluate and echo"
    printfn "  emit <file.edgi> [-o output.cpp]  Emit C++ source without compiling"
    printfn "  test                              Run full test suite (IR + C++ + run)"
    printfn "  test --omp                        ... including the OpenMP thread-coverage block"
    printfn "  test --cuda                       ... including the CUDA kernel block"
    printfn "                                    (Windows: run from the x64 Native Tools prompt"
    printfn "                                     so nvcc finds cl.exe)"
    printfn "  test --mpi                        ... including the MPI decomposition block"
    printfn "                                    (needs mingw msmpi + the MS-MPI runtime)"
    printfn "  test --timing                     ... including the differential timing block (slow)"
    printfn "  test --interp                     ... including the interpreter differential block (slow)"
    printfn "  test --diff-oracle                ... including the pinned-oracle differential block"
    printfn "                                    (skips cleanly without ./oracle/Blade.exe)"
    printfn "                                    (the --omp/--cuda/--timing/--mpi/--interp/"
    printfn "                                     --diff-oracle flags combine)"
    printfn "  test --ir-only                    Run IR-only tests (fast, no C++ compilation)"
    printfn "  test alloc                        Run C++ allocation-layout tests (contiguity/cardinality)"
    printfn "  test omp-pragma                   Run the OpenMP pragma-emission block standalone"
    printfn "  test omp-coverage                 Run the OpenMP thread-coverage block standalone"
    printfn "  test omp-reduce                   Run the comm-licensed parallel-reduction block standalone"
    printfn "  test linalg                       Run the blade_linalg dispatch-emission block standalone"
    printfn "  test lapack                       Run the blade_lapack eigensolver-dispatch block standalone"
    printfn "  test multifile                    Run the cross-module (multi-file) corpus standalone"
    printfn "  test module-resolve               Run the file-based module resolver + units.SI block"
    printfn "  test shapespec                    Run the shape-specialization reach block standalone"
    printfn "  test cuda                         Run the CUDA kernel block standalone"
    printfn "  test mpi                          Run the MPI decomposition block standalone"
    printfn "  test netcdf                       Run the NetCDF provider block (needs libnetcdf + sample.nc)"
    printfn "  test zarr                         Run the Zarr provider block (hermetic; g++ for the e2e parts)"
    printfn "  test timing                       Run the differential timing block standalone"
    printfn "  test strict-pins                  Run the --strict-pins CLI gate block standalone"
    printfn "  test surfacing                    Run the warning-surfacing block standalone"
    printfn "  test ide-serve                    Run the `ide serve` NDJSON protocol block standalone"
    printfn "  test ide-eval                     Run the notebook session-eval block standalone"
    printfn "  test ide-cells                    Run the notebook checkCells assembly block standalone"
    printfn "  test ide-references               Run the `references[]` navigation payload block standalone"
    printfn "  test diff-oracle [category]       Diff printed values against the pinned ./oracle build"
    printfn "  test interp [category]            Diff the tree-walking interpreter against the compiled binary"
    printfn ""
    printfn "Options:"
    printfn "  -o <path>      Output file path"
    printfn "  --verbose      Show IR and generated C++"
    printfn "  --strict-pins  Fail the build on unpinned confirm-and-pin deductions"
    printfn "                 (BL4010, normally warnings). For CI: forces the pin"
    printfn "                 decision into source. check / compile / emit / run."
    printfn "  --no-cache     Always run g++, ignoring the content-addressed"
    printfn "                 executable cache (%%LOCALAPPDATA%%\\Blade\\exe-cache;"
    printfn "                 BLADE_EXE_CACHE=0 / a path override it). compile / run / test."
    printfn "  --help         Show this help"
    printfn ""
    printfn "Examples:"
    printfn "  blade run myprogram.edgi"
    printfn "  blade emit myprogram.edgi -o myprogram.cpp"
    printfn "  blade compile myprogram.edgi -o myprogram"
    printfn "  blade check myprogram.edgi --strict-pins"
    printfn "  blade test"
    printfn "  blade test --omp --cuda --timing"

/// Strict-pins mode. Confirm-and-pin SUGGESTIONS (BL4010) are warnings by
/// default: the deduction proposes, storage stays DENSE, nothing changes
/// until the user pins the annotation in source. `--strict-pins` promotes
/// every outstanding suggestion to a build ERROR, so a CI build fails on an
/// unpinned deduction that would change storage.
///
/// Reads the `PinSuggestions` side-channel `typeCheck` fills (structured
/// (message, kernel span) twin of the plain-string warnings) and renders each
/// through the ordinary diagnostic renderer, so a strict failure looks like
/// any other `error[BL4010]:`. None when strict mode is off or nothing outstanding.
let private strictPinFailure (strictPins: bool) (useColor: bool)
                             (sm: Blade.Diagnostics.SourceMap option) : string option =
    if not strictPins then None
    else
        match Blade.TypeCheck.PinSuggestions.get () |> List.distinct with
        | [] -> None
        | suggestions ->
            let ds =
                suggestions |> List.map (fun (msg, span) ->
                    Blade.Diagnostics.mkError "BL4010" Blade.Diagnostics.PhConstraints span msg
                    |> Blade.Diagnostics.withNote
                        "--strict-pins: an unpinned deduction that would change storage fails the build. Add the pin shown above, or drop --strict-pins for the default dense-until-pinned behavior.")
            Some (Blade.Diagnostics.Render.renderAll useColor sm ds)

/// Compile a .edgi file to C++ source string
let compileFile (filePath: string) (verbose: bool) (strictPins: bool) : Result<string * string list, string> =
    if not (File.Exists filePath) then
        Error (sprintf "File not found: %s" filePath)
    else
        // Env-gated compiler perf counters (BLADE_PERF_COUNTERS=1); refreshed
        // here so the gate is live before the front end runs. See
        // docs/plan-compile-speed.md Stage 5.
        Blade.PerfCounters.refresh ()
        let source = File.ReadAllText(filePath)
        let testName = Path.GetFileNameWithoutExtension(filePath)
        // Env-gated phase timing (BLADE_PHASE_TIMING=1); see docs/plan-compile-speed.md.
        let timing = phaseTimingEnabled ()
        let swAll = System.Diagnostics.Stopwatch.StartNew()
        let sw = System.Diagnostics.Stopwatch.StartNew()
        let mark name =
            if timing then
                eprintfn "[phase] %s: %d ms" name sw.ElapsedMilliseconds
            sw.Restart()
        // Errors come back as coded, spanned Diagnostics, rendered rustc-style with source snippets.
        let useColor = not Console.IsErrorRedirected
        // `lowerFileDiag` resolves file-based imports (`import units.SI` ->
        // stdlib/units/SI.blade) first and lowers the whole set. With nothing
        // to resolve it IS `lowerDiag (Some filePath) source`.
        let lowered = lowerFileDiag filePath source
        mark "frontend-total(lowerFileDiag)"
        match lowered with
        | Error ds, sm ->
            // A file with a hard error has still EARNED every warning the checker produced before it failed.
            printTypeCheckWarnings useColor (Some sm) false
            Error (Blade.Diagnostics.Render.renderAll useColor (Some sm) ds)
        | Ok (ir, _), sm ->
            // Strict mode fails here, before codegen: the pin suggestions
            // REPLACE their warning twins (which are therefore not printed).
            match strictPinFailure strictPins useColor (Some sm) with
            | Some rendered -> Error rendered
            | None ->
            printTypeCheckWarnings useColor (Some sm) false
            let validated = IR.validateIR ir
            mark "validateIR"
            match validated with
            | Error errs ->
                let ds =
                    errs |> List.map (fun s ->
                        Blade.Diagnostics.mkError "BL6001" Blade.Diagnostics.PhIRValidate Blade.Ast.noSpan s)
                Error (Blade.Diagnostics.Render.renderAll useColor (Some sm) ds)
            | Ok ir ->
                let (cppCode, warnings) = CodeGen.genSelfContainedProgramFromIR ir testName
                mark "codegen"
                // A shape that reached codegen with no arm for it. Refuse HERE,
                // as a coded Blade diagnostic spanned at the declaration --
                // handing the emitted `BLADE_CODEGEN_ERROR_...` placeholder to
                // g++ instead reports a Blade back-end gap as an undeclared
                // C++ identifier. Deliberate codegen refusals are unaffected:
                // they keep their `#error` guard and their own wording.
                match CodeGen.takeUnhandledIRNodeDiagnostics () with
                | (_ :: _) as ds ->
                    Error (Blade.Diagnostics.Render.renderAll useColor (Some sm) ds)
                | [] ->
                // DELIBERATE refusals (BL7004): the same messages the emitted
                // `#error` directives carry, reported as coded diagnostics
                // before g++ ever runs. Gated on the generated source actually
                // carrying a marker -- a rendered-then-discarded refusal
                // records a message but splices nothing, and must not fail a
                // program whose translation unit is clean.
                match CodeGen.takeCodegenRefusalDiagnostics cppCode with
                | (_ :: _) as ds ->
                    Error (Blade.Diagnostics.Render.renderAll useColor (Some sm) ds)
                | [] ->
                mark "post-codegen-scans"
                if timing then
                    eprintfn "[phase] compileFile total: %d ms (cpp %d chars)" swAll.ElapsedMilliseconds cppCode.Length
                if Blade.PerfCounters.enabled then
                    // The IR walk is measurement-only, hence inside the gate.
                    Blade.PerfCounters.noteIRNodes (IR.countProgramNodes ir)
                    Blade.PerfCounters.report ()
                if verbose then
                    for w in warnings do
                        eprintfn "[Warning] %s" w
                else
                    // A `where cuda` kernel that fell back to the host prints
                    // WITHOUT --verbose: device emission is an explicit opt-in
                    // (`--cuda` / the CUDA test phase), so "you asked and did not
                    // get it" is the one codegen warning the user is entitled to
                    // see unprompted. Every other warning keeps its --verbose gate,
                    // and outside cuda emit mode this list is empty.
                    for w in warnings do
                        if w.StartsWith "[cuda] " then eprintfn "warning: %s" w
                Ok (cppCode, warnings)

/// Compile a .edgi file to an executable
let compileToExe (filePath: string) (outputPath: string option) (verbose: bool) (strictPins: bool) : Result<string, string> =
    match compileFile filePath verbose strictPins with
    | Error e -> Error e
    | Ok (cppCode, warnings) ->
        let baseName = Path.GetFileNameWithoutExtension(filePath)
        let dir = Path.GetDirectoryName(Path.GetFullPath(filePath))
        let dir = if String.IsNullOrEmpty dir then "." else dir
        // Infer backend from generated source: device kernels -> .cu + nvcc.
        let backendReq = inferBackendReq cppCode
        let ext = match backendReq with RequiresCuda -> ".cu" | RequiresMpi | CpuOnly -> ".cpp"
        let cppFile = Path.Combine(dir, baseName + ext)
        File.WriteAllText(cppFile, cppCode)
        // `blade run --cuda`: `where cuda` device kernels are collected
        // SEPARATELY from the host source (cudaKernelDefsCell), so
        // inferBackendReq -- which greps the host half -- cannot see them.
        // Write the .cu beside the host .cpp and split-compile (nvcc for the
        // .cu, host compiler for the .cpp, then link), exactly as the CUDA
        // test harness does. Without this, the host half's extern "C" launch
        // declarations dangle and the link fails.
        let cudaSplitFile =
            match CodeGen.getCudaFileContent () with
            | Some cu when ext = ".cpp" ->
                let cuFile = Path.Combine(dir, baseName + "_kernels.cu")
                File.WriteAllText(cuFile, cu)
                Some cuFile
            | _ -> None
        // Runtime headers are #include'd with plain quotes and no -I, so they
        // must sit next to the .cpp; record which ones we create so cleanup removes only our copies.
        let deployedHeaders =
            CodeGen.runtimeHeaderNames
            |> List.map (fun name -> Path.Combine(dir, name))
            |> List.filter (fun path -> not (File.Exists path))
        CodeGen.deployRuntimeHeaders dir
        if verbose then
            eprintfn "[Emit] %s" cppFile
        match (match cudaSplitFile with
               | Some cuFile -> compileCudaSplit cuFile cppFile dir
               // cppCode is the exact text just written to cppFile; handing it
               // over spares the backend sniffs a read-back of what we wrote.
               | None -> compileForBackendSource (Some cppCode) capabilities.Value backendReq cppFile dir) with
        | Error e ->
            Error (sprintf "Compilation failed:\n%s" e)
        | Ok exePath ->
            let finalPath =
                match outputPath with
                | Some out ->
                    let outFull = Path.GetFullPath(out)
                    if exePath <> outFull then
                        try File.Copy(exePath, outFull, true) with _ -> ()
                    outFull
                | None -> exePath
            // verbose keeps the intermediates so the source can be inspected/recompiled.
            if not verbose then
                try File.Delete(cppFile) with _ -> ()
                for h in deployedHeaders do
                    try File.Delete(h) with _ -> ()
            if verbose then
                eprintfn "[Compile] %s" finalPath
            Ok finalPath

/// Run a .edgi file: compile and execute. `mpiRanks = Some n` switches on the
/// MPI emit gate (decomposed kernels + Init/Finalize + rank-0 printing),
/// links -lmsmpi, launches under `mpiexec -n n`. None = serial path.
let runFile (filePath: string) (verbose: bool) (mpiRanks: int option) (strictPins: bool) : int =
    match mpiRanks with
    | None ->
        match compileToExe filePath None verbose strictPins with
        | Error e ->
            eprintfn "%s" e
            1
        | Ok exePath ->
            match runExecutable exePath with
            | Error e ->
                eprintfn "Runtime error: %s" e
                1
            | Ok (exitCode, output) ->
                printf "%s" output
                exitCode
    | Some ranks ->
        CodeGen.setMpiEmitMode true
        try
            match compileToExe filePath None verbose strictPins with
            | Error e ->
                eprintfn "%s" e
                1
            | Ok exePath ->
                match runExecutableMpi ranks exePath with
                | Error e ->
                    eprintfn "Runtime error: %s" e
                    1
                | Ok (exitCode, output) ->
                    printf "%s" output
                    exitCode
        finally
            CodeGen.setMpiEmitMode false

// Interactive REPL (`blade repl`): `blade run` semantics give REPL behavior
// for free, since every top-level binding prints its value. The REPL
// accumulates a session program in a temp file; each submitted snippet
// re-compiles and re-runs the WHOLE session, but echoes ONLY the value of the
// snippet's LAST top-level binding. Earlier bindings, and the synthetic
// `__`-internal bindings a `ppl.dist`/module call expands into, stay hidden.
// Rebinding a top-level name replaces the earlier definition in place
// (duplicate lets are a C++ redeclaration error) so downstream snippets still see it.
//
// A bare EXPRESSION snippet (`a`, `a + 1`) gets wrapped in a transient
// binding (`let it = <expr>`), run, echoed, and discarded, so the session
// stays untouched and repeating it echoes again. A bare identifier naming a
// session FUNCTION echoes its signature from the typechecker alone.
//
// Output lines are type-annotated by an in-process parse+typecheck(+lower,
// for HM-monomorphized value types) of the same source -- see ReplTypes. The
// compiled session runs with the REPL process's own cwd, so relative data
// paths resolve where the user launched the REPL, not in the session temp dir.

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

/// The engine `blade repl` drives, shared with `ide serve`'s notebook lane:
/// the accumulating snippet list, rebind-in-place splicing, interp-first
/// evaluation and the ReplTypes annotation map all live in src/ReplSession.fs
/// (it has to compile before IdeServe.fs). What stays HERE is the interactive
/// front end -- prompts, classification branches, and every printed string.
module RS = Blade.ReplSession
module ReplTypes = Blade.ReplSession.ReplTypes

/// The g++ fallback lane, handed to the engine because `compileToExe` sits on
/// top of Build.fs and is unreachable from a file that compiles this early.
/// The two failure messages are composed exactly as the REPL has always
/// printed them, so the caller only has to add `[snippet not kept]`.
let private compiledReplLane (srcPath: string) (cwd: string) : Result<int * string * string, string> =
    match compileToExe srcPath None false false with
    | Error e -> Error e
    | Ok exePath ->
        match runExeIn cwd exePath with
        | Error e -> Error (sprintf "Runtime error: %s" e)
        | Ok triple -> Ok triple

let replLoop () : int =
    printfn "Blade REPL (v%s) -- each submission echoes its last binding's (typed) value." compilerVersion
    printfn "A bare expression (e.g. `a`, `a + 1`) evaluates and echoes without joining the session."
    printfn "Commands: :reset (clear session)  :show (print session)  :quit"
    printfn "Multi-line: unbalanced brackets continue on the next line, or use :paste ... :end"
    let engine = RS.ReplSession(Directory.GetCurrentDirectory())
    let session = engine.Snippets
    let mutable lastLines : string[] = [||]

    /// Evaluate `candidate` and echo ONLY `targetName`'s value line, type-
    /// annotated. Every earlier user binding, and every synthetic
    /// `__`-internal binding, stays hidden.
    ///
    /// The evaluation itself (one lowering, interpreter first, g++ only where
    /// the interpreter falls short) is the engine's; this is the PRINTING half
    /// of the old compileRunEcho, and every stream it writes to -- rendered
    /// diagnostics, the fallback notice, the annotated echo, `[snippet not
    /// kept]` -- is in the same order it always was.
    ///
    /// `transient` is the synthetic name a bare expression was wrapped in
    /// (stripped in display), else None. Returns Some (lines, printedCount,
    /// info) on a clean exit, or None if the snippet must not be kept.
    let compileRunEcho (candidate: ResizeArray<string>) (targetName: string option) (transient: string option)
        : (string[] * int * Map<string, ReplTypes.Info>) option =
        let useColor = not Console.IsErrorRedirected
        match engine.EvalCandidate(candidate, targetName, eprintfn "%s") with
        | RS.CandidateRejected (ds, sm) ->
            // Front-end / validate rejection: same diagnostics as compileToExe's Error arm.
            eprintfn "%s" (Blade.Diagnostics.Render.renderAll useColor (Some sm) ds)
            eprintfn "[snippet not kept]"
            None
        | RS.CandidateFailed msg ->
            eprintfn "%s" msg
            eprintfn "[snippet not kept]"
            None
        | RS.CandidateRan r ->
            // Interpreter lane: surface the same TypeCheck warnings the g++
            // path prints for itself, in the same place (before the echo).
            (match r.Warnings with
             | [] -> ()
             | ds -> eprintfn "%s" (Blade.Diagnostics.Render.renderAll useColor None ds))
            // Display frames (Blade-REPL/docs/display-frames.md section 4) go to
            // stdout, whole lines at column 0, BEFORE the echo and therefore
            // before the next prompt -- which is the frame terminator the
            // editor's REPL client scans for. This loop otherwise prints only
            // the echoed binding, so without this arm the pty channel would
            // carry no frames at all.
            //
            // Every accumulated snippet re-runs on every submission, so an
            // earlier submission's frames re-appear here. That is the spec's
            // section 10 replay contract: `meta.id`s are stable across re-runs,
            // so the panel merges them into the plots it already has. A human
            // reading a raw `blade repl` sees one long line per frame; the
            // editor strips them before the terminal ever shows them.
            for l in r.Lines do
                if l.StartsWith Blade.Display.Frame.Sentinel then printfn "%s" l
            let mutable printed = 0
            match r.Echo with
            | Some l -> printfn "%s" (ReplTypes.annotate r.Info transient l); printed <- 1
            | None -> ()
            if r.Stderr.Trim() <> "" then eprintfn "%s" (r.Stderr.Trim())
            if r.ExitCode = 0 then Some (r.Lines, printed, r.Info)
            else
                eprintfn "[exit %d -- snippet not kept]" r.ExitCode
                None

    let evaluate (snippet: string) =
        let trimmed = snippet.Trim()
        if trimmed = "" then () else
        if RS.declRe.IsMatch (RS.classifyTarget trimmed) then
            // Declaration: rebinding replaces the earlier definition IN PLACE
            // so later snippets referencing the name see the update.
            let (candidate, _) = engine.DeclarationCandidate trimmed
            // The submission's "return value" is its LAST top-level binding
            // (a :paste block may declare several); echo only that one.
            let lastTarget =
                trimmed.Replace("\r\n", "\n").Split('\n')
                |> Array.choose RS.bindingName
                |> Array.tryLast
            match compileRunEcho candidate lastTarget None with
            | None -> ()
            | Some (lines, printed, info) ->
                let mutable printed = printed
                // A final function/type binding produces no run output -- echo
                // its signature (abstract unless inference bound it concrete).
                match lastTarget with
                | Some name when printed = 0 ->
                    match Map.tryFind name info with
                    | Some (ReplTypes.RFunc s) ->
                        printfn "%s\n\t%s" name s
                        printed <- printed + 1
                    | _ -> ()
                | _ -> ()
                if printed = 0 then printfn "(ok)"   // defs print nothing new
                engine.Commit candidate
                lastLines <- lines
        elif RS.assignRe.IsMatch (RS.classifyTarget trimmed) then
            // Reassignment (`b = b + 1`, `b += 1`, etc.): the engine wraps it
            // in a hidden binding whose value IS the ExprAssign, and KEEPS the
            // wrapper so the mutation persists.
            let (candidate, _, _) = engine.AssignmentCandidate trimmed
            let root = (RS.assignRe.Match trimmed).Groups.[1].Value
            match compileRunEcho candidate (Some root) None with
            | None -> ()                                    // static/unknown/etc -> not kept
            | Some (lines, printed, _) ->
                if printed = 0 then printfn "(ok)"          // e.g. array reassign isn't auto-printed
                engine.Commit candidate
                lastLines <- lines
        else
            // Bare expression: `blade run` semantics only print top-level
            // BINDINGS, so wrap the expression in a transient one, run, and
            // echo its value WITHOUT keeping it, so re-entering the same
            // expression echoes again rather than diffing to silence.
            let curInfo = lazy (ReplTypes.sessionInfo (String.concat "\n\n" session + "\n"))
            let asFuncName =
                if RS.identRe.IsMatch trimmed then
                    match Map.tryFind trimmed curInfo.Value with
                    | Some (ReplTypes.RFunc s) -> Some s
                    | _ -> None
                else None
            match asFuncName with
            | Some s ->
                // A function can't be let-bound just to echo it; print its
                // signature straight from the typechecker.
                printfn "%s\n\t%s" trimmed s
            | None ->
                let (candidate, _, transient) = engine.ExpressionCandidate trimmed
                match compileRunEcho candidate (Some transient) (Some transient) with
                | None -> ()
                | Some (_, printed, info) ->
                    if printed = 0 then
                        // Nothing printable (unit, deferred computation,
                        // function value): show the type alone if known.
                        match Map.tryFind transient info with
                        | Some (ReplTypes.RVal t) -> printfn "\t%s" (Blade.Ide.ppType t)
                        | _ -> printfn "(ok)"

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
            engine.Reset()
            lastLines <- [||]
            printfn "(session cleared)"
        | line when buffer.Count = 0 && line.Trim() = ":show" ->
            // Hide the synthetic `let __assign... = <reassignment>` wrappers the assignment path appends.
            let visible =
                session
                |> Seq.filter (fun s -> RS.bindingName s |> Option.forall (fun n -> not (n.StartsWith "__assign")))
                |> List.ofSeq
            if List.isEmpty visible then printfn "(empty session)"
            else printfn "%s" (String.concat "\n\n" visible)
        | line ->
            buffer.Add line
            if bracketBalance (String.concat "\n" buffer) <= 0 then
                evaluate (String.concat "\n" buffer)
                buffer.Clear()
    engine.Cleanup()
    0

/// End-to-end CLI smoke test: compile and run a one-line .edgi from a FRESH
/// temp directory -- the only block exercising the user-facing path from a
/// bare directory (other runners pre-deploy runtime headers, masking a compileToExe that forgets to).
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
            match compileToExe srcFile None false false with
            | Error e ->
                record runTest TH.Fail (e.Replace("\n", " | "))
            | Ok exePath ->
                (match runExecutable exePath with
                 | Error e -> record runTest TH.Fail e
                 | Ok (0, output) when output.Contains "x = 7" ->
                     record runTest TH.Pass ""
                 | Ok (code, output) ->
                     record runTest TH.Fail (sprintf "exit %d, output: %s" code (output.Trim())))
                // Non-verbose compiles must clean up: only source + executable remain.
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
let checkFile (filePath: string) (strictPins: bool) : int =
    if not (File.Exists filePath) then
        eprintfn "File not found: %s" filePath
        1
    else
        let source = File.ReadAllText(filePath)
        let useColor = not Console.IsErrorRedirected
        // Same shape as compileFile's `lowerFileDiag`: parse the entry ONCE,
        // resolve file-based imports from that AST, then check the whole set as
        // ONE program. With nothing to resolve, the entry parse is the only
        // parse and `sources` is [(filePath, source)] -- byte for byte the
        // pre-module behavior, including the SourceMap key.
        match Blade.Parser.parseProgramWithFile (Some filePath) source with
        | Error e ->
            let sm = Blade.Diagnostics.SourceMap.ofSources [ filePath, source ]
            let d = Blade.Parser.diagnosticOfParseError (Some filePath) e
            eprintfn "%s" (Blade.Diagnostics.Render.render useColor (Some sm) d)
            1
        | Ok entryProgram ->
        let resolution =
            match entryProgram.Modules with
            | [ m ] -> Blade.ModuleResolve.resolveParsedEntry filePath source m
            | _ -> Blade.ModuleResolve.resolveEntry filePath source
        let sources =
            match resolution.Errors, resolution.Files with
            | [], [ _single ] -> [ filePath, source ]
            | _, files -> Blade.ModuleResolve.sourcesOf files
        let sm = Blade.Diagnostics.SourceMap.ofSources sources
        if not (List.isEmpty resolution.Errors) then
            eprintfn "%s" (Blade.Diagnostics.Render.renderAll useColor (Some sm) resolution.Errors)
            1
        else
        match (match resolution.Files with
               | [ _single ] -> Ok entryProgram
               | files -> Blade.ModuleResolve.parseResolvedFiles files) with
        | Error d ->
            eprintfn "%s" (Blade.Diagnostics.Render.render useColor (Some sm) d)
            1
        | Ok program ->
            match Blade.TypeCheck.typeCheck program with
            | Error errors ->
                // S1: warnings earned before the error are printed, not dropped.
                printTypeCheckWarnings useColor (Some sm) false
                let ds = errors |> List.map Blade.TypeEnv.diagnosticOfCompileError
                eprintfn "%s" (Blade.Diagnostics.Render.renderAll useColor (Some sm) ds)
                1
            | Ok _ ->
                match strictPinFailure strictPins useColor (Some sm) with
                | Some rendered ->
                    // Strict mode: the pin suggestions ARE the failure. Their
                    // warning twins are dropped (same dedup rule as `blade ide
                    // check`) so each is reported exactly once, as an error;
                    // the twins are BL4010 by construction so filtering on the code is exact.
                    printTypeCheckWarnings useColor (Some sm) true
                    eprintfn "%s" rendered
                    1
                | None ->
                    printTypeCheckWarnings useColor (Some sm) false
                    printfn "OK"
                    0

/// Emit C++ source to file or stdout
let emitFile (filePath: string) (outputPath: string option) (verbose: bool) (strictPins: bool) : int =
    match compileFile filePath verbose strictPins with
    | Error e ->
        eprintfn "%s" e
        1
    | Ok (cppCode, _) ->
        match outputPath with
        | Some outPath ->
            File.WriteAllText(outPath, cppCode)
            // Ship the runtime headers next to the emitted .cpp so `g++ file.cpp` compiles as-is (no -I flag needed).
            let outDir = Path.GetDirectoryName(Path.GetFullPath(outPath))
            CodeGen.deployRuntimeHeaders (if String.IsNullOrEmpty outDir then "." else outDir)
            if verbose then
                eprintfn "[Emit] %s" outPath
            0
        | None ->
            printf "%s" cppCode
            0

/// `--strict-pins` regression block. A corpus entry can't express a FLAG's
/// behavior, so this drives checkFile and compileFile (which compile/emit/run
/// funnel through) against a temp file in-process. Uses corpus twins
/// functions/026 (unpinned -> suggestion) / functions/029 (pin applied), inline so the test survives corpus renumbering.
let private runStrictPinTests () : TH.BlockResult =
    let blockName = "Strict Pins"
    TH.printHeader "Strict Pin Mode (--strict-pins: unpinned deduction = build failure)"
    let results = ResizeArray<string * TH.Outcome>()
    let record name outcome detail =
        TH.resultLine outcome name detail
        results.Add((name, outcome))
    let unpinned =
        "function mymean(row) = reduce(row, (+)) / extents(row)\n\
         function covariance(a, b) = mymean((a - mymean(a)) * (b - mymean(b)))\n\
         let data = [[1.0, 2.0, 3.0], [2.0, 4.0, 6.0]]\n\
         let result = object_for(covariance) <@> (data, data) |> compute\n"
    let pinned = unpinned.Replace("function covariance(a, b) =",
                                  "function covariance(a, b) where comm(a, b) =")
    let tmpDir = Path.Combine(Path.GetTempPath(), "blade_strict_pins_" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmpDir) |> ignore
    /// Run `f` with stdout/stderr captured; returns (result, captured text).
    let quietly (f: unit -> 'a) : 'a * string =
        let sw = new StringWriter()
        let (oldOut, oldErr) = (Console.Out, Console.Error)
        try
            Console.SetOut sw
            Console.SetError sw
            let r = f ()
            (r, sw.ToString())
        finally
            Console.SetOut oldOut
            Console.SetError oldErr
    try
        let unpinnedPath = Path.Combine(tmpDir, "unpinned.edgi")
        let pinnedPath = Path.Combine(tmpDir, "pinned.edgi")
        File.WriteAllText(unpinnedPath, unpinned)
        File.WriteAllText(pinnedPath, pinned)

        // Default behavior is UNCHANGED: the deduction is a warning, exit 0.
        let (code, out) = quietly (fun () -> checkFile unpinnedPath false)
        if code = 0 && out.Contains "where comm(a, b)" then
            record "check: default surfaces the suggestion as a warning (exit 0)" TH.Pass ""
        else
            record "check: default surfaces the suggestion as a warning (exit 0)" TH.Fail
                   (sprintf "exit %d, output: %s" code (out.Trim()))

        // Strict: the same suggestion becomes an error and fails the build.
        let (code, out) = quietly (fun () -> checkFile unpinnedPath true)
        if code = 1 && out.Contains "BL4010" && out.Contains "where comm(a, b)" then
            record "check --strict-pins: unpinned deduction is a BL4010 error (exit 1)" TH.Pass ""
        else
            record "check --strict-pins: unpinned deduction is a BL4010 error (exit 1)" TH.Fail
                   (sprintf "exit %d, output: %s" code (out.Trim()))

        // The suggestion is ACTIONABLE: applying the proposed pin clears it.
        let (code, out) = quietly (fun () -> checkFile pinnedPath true)
        if code = 0 then
            record "check --strict-pins: the pinned twin passes" TH.Pass ""
        else
            record "check --strict-pins: the pinned twin passes" TH.Fail
                   (sprintf "exit %d, output: %s" code (out.Trim()))

        // The compile/emit/run lane (all three funnel through compileFile).
        let ((result: Result<string * string list, string>), _) =
            quietly (fun () -> compileFile unpinnedPath false true)
        match result with
        | Error e when e.Contains "BL4010" ->
            record "compile lane --strict-pins: fails before codegen" TH.Pass ""
        | Error e ->
            record "compile lane --strict-pins: fails before codegen" TH.Fail
                   (sprintf "wrong error: %s" (e.Replace("\n", " | ")))
        | Ok _ ->
            record "compile lane --strict-pins: fails before codegen" TH.Fail "compiled instead of failing"

        let (result, _) = quietly (fun () -> compileFile unpinnedPath false false)
        match result with
        | Ok _ -> record "compile lane default: unaffected (still compiles)" TH.Pass ""
        | Error e ->
            record "compile lane default: unaffected (still compiles)" TH.Fail
                   (e.Replace("\n", " | "))
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

/// Warning/suggestion SURFACING, end to end. Not expressible in the corpus:
/// drives `ide check --json` and the two console streams, which no corpus
/// harness touches (the diagnostics corpus never renders; the value corpus
/// compares OUTPUT, and a warning changes no value). Locks warnings/pin
/// suggestions surviving a file with a hard error, on both the CLI (S1) and editor JSON (S2).
let private runSurfacingTests () : TH.BlockResult =
    let blockName = "Surfacing"
    TH.printHeader "Warning Surfacing (codes, streams, and survival of the error path)"
    let results = ResizeArray<string * TH.Outcome>()
    let record name outcome detail =
        TH.resultLine outcome name detail
        results.Add((name, outcome))
    // The strict-pins `unpinned` twin (earns a BL4010 storage suggestion).
    let unpinned =
        "function mymean(row) = reduce(row, (+)) / extents(row)\n\
         function covariance(a, b) = mymean((a - mymean(a)) * (b - mymean(b)))\n\
         let data = [[1.0, 2.0, 3.0], [2.0, 4.0, 6.0]]\n\
         let result = object_for(covariance) <@> (data, data) |> compute\n"
    // Plus an unrelated hard type error in a LATER declaration: the checker
    // must record the suggestion before it fails on the later error.
    let errPlusWarn = unpinned + "let boom = nosuchthing + 1.0\n"
    let tmpDir = Path.Combine(Path.GetTempPath(), "blade_surfacing_" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmpDir) |> ignore
    /// Run `f` with stdout and stderr captured SEPARATELY, so "warnings go to
    /// stderr, stdout stays pipeable" can actually be asserted.
    let quietly2 (f: unit -> 'a) : 'a * string * string =
        let (swOut, swErr) = (new StringWriter(), new StringWriter())
        let (oldOut, oldErr) = (Console.Out, Console.Error)
        try
            Console.SetOut swOut
            Console.SetError swErr
            let r = f ()
            (r, swOut.ToString(), swErr.ToString())
        finally
            Console.SetOut oldOut
            Console.SetError oldErr
    try
        let unpinnedPath = Path.Combine(tmpDir, "unpinned.edgi")
        let errPath = Path.Combine(tmpDir, "err_plus_warn.edgi")
        let pinnedPath = Path.Combine(tmpDir, "pinned.edgi")
        File.WriteAllText(unpinnedPath, unpinned)
        File.WriteAllText(errPath, errPlusWarn)
        File.WriteAllText(pinnedPath,
                          unpinned.Replace("function covariance(a, b) =",
                                           "function covariance(a, b) where comm(a, b) ="))

        // 1. ide check --json, ERROR path: the suggestion survives (S2).
        let (code, out, _) = quietly2 (fun () -> Blade.Ide.ideCheck errPath)
        let name = "ide check --json: BL4010 survives a file with a hard error"
        if code = 1 && out.Contains "\"severity\":\"error\"" && out.Contains "\"code\":\"BL4010\"" then
            record name TH.Pass ""
        else
            record name TH.Fail
                   (sprintf "exit %d, json: %s" code (out.Trim()))

        // 2. ...and so do the deduced facts (channel (f)) on that arm.
        let name = "ide check --json: deduced[] is populated on the error arm"
        if out.Contains "\"deduced\":[" && out.Contains "\"kind\":\"comm\"" then
            record name TH.Pass ""
        else
            record name TH.Fail (sprintf "json: %s" (out.Trim()))

        // 3. Control: the pinned twin is clean and claims nothing.
        let (code, out, _) = quietly2 (fun () -> Blade.Ide.ideCheck pinnedPath)
        let name = "ide check --json: the pinned twin yields no BL4010 (exit 0)"
        if code = 0 && not (out.Contains "BL4010") then
            record name TH.Pass ""
        else
            record name TH.Fail (sprintf "exit %d, json: %s" code (out.Trim()))

        // 4. `check`: warnings render as diagnostics on STDERR, keeping
        // stdout ("OK") pipeable.
        let (code, out, err) = quietly2 (fun () -> checkFile unpinnedPath false)
        let name = "check: the warning renders as warning[BL4010] on stderr, not stdout"
        if code = 0 && err.Contains "warning[BL4010]" && not (out.Contains "BL4010")
           && out.Contains "OK" then
            record name TH.Pass ""
        else
            record name TH.Fail
                   (sprintf "exit %d, stdout: %s, stderr: %s" code (out.Trim()) (err.Trim()))

        // 5. `check` on the erroring file still prints the warning (S1).
        let (code, _, err) = quietly2 (fun () -> checkFile errPath false)
        let name = "check: warnings print alongside the error instead of vanishing"
        if code = 1 && err.Contains "warning[BL4010]" && err.Contains "error[BL2001]" then
            record name TH.Pass ""
        else
            record name TH.Fail (sprintf "exit %d, stderr: %s" code (err.Trim()))

        // 6. The compile lane agrees (compile/emit/run all funnel here).
        let ((result: Result<string * string list, string>), _, err) =
            quietly2 (fun () -> compileFile errPath false false)
        let name = "compile lane: warnings print on the error arm too"
        match result with
        | Error _ when err.Contains "warning[BL4010]" -> record name TH.Pass ""
        | Error _ -> record name TH.Fail (sprintf "no warning on stderr: %s" (err.Trim()))
        | Ok _ -> record name TH.Fail "compiled instead of failing"

        // 7-9. The CERTIFICATE channels (BL4011's galilean twin BL4014, and
        // the CertFacts feed behind `deduced[]`). Test the DRAIN, not the
        // producer: stage a channel entry by hand, assert it surfaces, reset
        // -- catches a channel filled and then read by nobody.
        let testSpan : Blade.Ast.Span =
            { StartLine = 2; StartCol = 1; EndLine = 2; EndCol = 9; File = None }

        // 7. The code renders. Channel-independent: the diagnostic is built
        // directly, so this holds even with both inference passes absent.
        let galMsg =
            "function 'drift' judges boost-invariant with velocity parameter(s) u: \
             add 'where ml.galilean(u)'"
        let rendered =
            Blade.Diagnostics.Render.renderAll false None
                [ Blade.Diagnostics.mkWarning "BL4014" Blade.Diagnostics.PhConstraints
                                              testSpan galMsg ]
        let name = "BL4014 renders as a warning with its code"
        if rendered.Contains "warning[BL4014]" && rendered.Contains "boost-invariant" then
            record name TH.Pass ""
        else
            record name TH.Fail (sprintf "rendered: %s" (rendered.Trim()))

        // 8. GalCertSuggestions reaches the shared warning-diagnostic assembly
        // and survives `skipPins`: a certificate owns no storage decision, so
        // --strict-pins must not swallow it like it swallows BL4010.
        Blade.ML.Galilean.GalCertSuggestions.reset ()
        Blade.ML.Galilean.GalCertSuggestions.add galMsg testSpan
        let drained = Blade.Lowering.typeCheckWarningDiagnostics false
        let drainedStrict = Blade.Lowering.typeCheckWarningDiagnostics true
        Blade.ML.Galilean.GalCertSuggestions.reset ()
        let hasBL4014 (ds: Blade.Diagnostics.Diagnostic list) =
            ds |> List.exists (fun d -> d.Code = "BL4014" && d.Message.Contains "boost-invariant")
        let name = "typeCheckWarningDiagnostics: GalCertSuggestions surfaces as BL4014"
        if hasBL4014 drained then record name TH.Pass ""
        else
            record name TH.Fail
                   (sprintf "codes drained: %s"
                            (drained |> List.map (fun d -> d.Code) |> String.concat ","))
        let name = "typeCheckWarningDiagnostics: BL4014 survives --strict-pins"
        if hasBL4014 drainedStrict then record name TH.Pass ""
        else
            record name TH.Fail
                   (sprintf "codes drained: %s"
                            (drainedStrict |> List.map (fun d -> d.Code) |> String.concat ","))

        // 9. CertFacts reaches `deduced[]` as STRUCTURED data through the real
        // mapping and renderer. Both disciplines share a renderer arm, so a
        // typo in either kind string would silently drop `name` (the group).
        Blade.ML.Equiv.CertFacts.reset ()
        Blade.ML.Equiv.CertFacts.add
            { Owner = "rotate"; Discipline = "equiv"; Group = "O3"; Deps = ["helper"; "inner"] }
            testSpan
        Blade.ML.Equiv.CertFacts.add
            { Owner = "drift"; Discipline = "galilean"; Group = "u,v"; Deps = [] }
            testSpan
        let deducedJson = Blade.Ide.deducedJsonForTests ()
        Blade.ML.Equiv.CertFacts.reset ()
        let name = "ide deduced[]: CertFacts surface with kind, owner, group and deps"
        if deducedJson.Contains "\"kind\":\"equiv\"" && deducedJson.Contains "\"owner\":\"rotate\""
           && deducedJson.Contains "\"name\":\"O3\"" && deducedJson.Contains "\"left\":\"helper,inner\""
           && deducedJson.Contains "\"kind\":\"galilean\"" && deducedJson.Contains "\"name\":\"u,v\"" then
            record name TH.Pass ""
        else
            record name TH.Fail (sprintf "deduced json: %s" (deducedJson.Trim()))
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

/// `blade ide serve`, driven IN-PROCESS through `serveLoop`'s TextReader /
/// TextWriter seam -- no spawn, no g++, no editor. What is under test is the
/// PROTOCOL (framing, id/tier echo, error containment) and the daemon's
/// hardest promise: that nothing leaks from one request into the next, since
/// the compiler's side-channels were written for a process that exits.
let private runIdeServeTests () : TH.BlockResult =
    let blockName = "IdeServe"
    TH.printHeader "ide serve (NDJSON protocol, tiers, and per-request isolation)"
    let results = ResizeArray<string * TH.Outcome>()
    let record name outcome detail =
        TH.resultLine outcome name detail
        results.Add((name, outcome))
    let esc = Blade.Ide.jsonEscape
    let checkReq (id: int) (tier: string) (file: string) (source: string) =
        sprintf "{\"id\":%d,\"cmd\":\"check\",\"tier\":\"%s\",\"file\":\"%s\",\"source\":\"%s\"}"
                id tier (esc file) (esc source)
    let pingReq (id: int) = sprintf "{\"id\":%d,\"cmd\":\"ping\"}" id
    let shutdownReq = "{\"cmd\":\"shutdown\"}"
    /// Feed a whole conversation and split the transcript on the framing
    /// newline. The trailing "" is the proof that the LAST response was
    /// newline-terminated too; anything else in the tail would be an unframed
    /// write. Returns (exit code, responses, raw transcript).
    let drive (requests: string list) : int * string list * string =
        let input = new StringReader(String.concat "\n" requests + "\n")
        let output = new StringWriter()
        let code = Blade.IdeServe.serveLoop compilerVersion (input :> TextReader) (output :> TextWriter)
        let raw = output.ToString()
        let parts = raw.Split('\n') |> Array.toList
        (code, (parts |> List.filter (fun p -> p <> "")), raw)
    // An HM-polymorphic value binding: the typed AST keeps `T` for both lets
    // (the scheme is only instantiated per call site), while monomorphization
    // during lowering resolves them. Exactly the fast/full split.
    let hmSource = "function id(x: T) -> T = x\nlet r = id(42)\nlet s = id(3.5)\n"
    // Earns a BL4010 pin suggestion plus a `covariance` binding -- the marks
    // whose ABSENCE proves the next request started clean.
    let warnSource =
        "function mymean(row) = reduce(row, (+)) / extents(row)\n\
         function covariance(a, b) = mymean((a - mymean(a)) * (b - mymean(b)))\n\
         let data = [[1.0, 2.0, 3.0], [2.0, 4.0, 6.0]]\n\
         let result = object_for(covariance) <@> (data, data) |> compute\n"
    let tmpDir = Path.Combine(Path.GetTempPath(), "blade_ideserve_" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmpDir) |> ignore
    // serveLoop chdirs per request (provider relative paths) and restores on
    // exit; belt-and-braces here so a regression in that restore cannot
    // contaminate every later block in the suite.
    let entryDir = Directory.GetCurrentDirectory()
    try
        let hmPath = Path.Combine(tmpDir, "hm.blade")
        let warnPath = Path.Combine(tmpDir, "warn.blade")
        let cleanPath = Path.Combine(tmpDir, "clean.blade")

        // 1. ping: the capability probe the extension uses to choose the serve
        // lane over the one-shot lane.
        let (code, responses, _) = drive [pingReq 7; shutdownReq]
        let name = "ping answers with ok/serve/version and echoes the id"
        match responses with
        | [r] when code = 0 && r.Contains "\"id\":7" && r.Contains "\"ok\":true"
                   && r.Contains "\"serve\":1" && r.Contains (sprintf "\"version\":\"%s\"" compilerVersion) ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 2. Fast tier: today's payload, on the BUFFER. `hmPath` is never
        // written to disk, so bindings can only have come from `source`.
        let (code, responses, raw) = drive [checkReq 11 "fast" hmPath hmSource; shutdownReq]
        let fastBody = match responses with [r] -> r | _ -> ""
        let name = "check tier=fast: id/tier echoed, bindings from the unsaved buffer"
        if code = 0 && not (File.Exists hmPath)
           && fastBody.Contains "\"id\":11" && fastBody.Contains "\"tier\":\"fast\""
           && fastBody.Contains "\"diagnostics\":[]" && fastBody.Contains "\"name\":\"r\""
           && not (fastBody.Contains "concreteType") then
            record name TH.Pass ""
        else
            record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 3. Framing: one \n-terminated line per response, and the payload's
        // own multi-line function signatures escaped INTO it, not through it.
        let name = "each response is exactly one newline-terminated line"
        if raw.EndsWith "\n" && raw.Split('\n').Length = 2 && fastBody.Contains "\\n" then
            record name TH.Pass ""
        else
            record name TH.Fail (sprintf "%d newline-separated parts" (raw.Split('\n').Length))

        // 4. Full tier: monomorphization upgrades both HM values, and only
        // where it actually knows more than the typed AST did.
        let (code, responses, _) = drive [checkReq 12 "full" hmPath hmSource; shutdownReq]
        let fullBody = match responses with [r] -> r | _ -> ""
        let name = "check tier=full: HM value bindings gain concreteType"
        if code = 0 && fullBody.Contains "\"tier\":\"full\""
           && fullBody.Contains "\"concreteType\":\"Int64\""
           && fullBody.Contains "\"concreteType\":\"Float64\"" then
            record name TH.Pass ""
        else
            record name TH.Fail (sprintf "exit %d, response: %s" code fullBody)

        // 5. `type` is never rewritten in place: the client wants both, and
        // decides which to show.
        let name = "full tier keeps the fast `type` beside the upgrade"
        if fullBody.Contains "\"name\":\"r\",\"kind\":\"let\",\"line\":2,\"col\":1,\"type\":\"T\",\"concreteType\":\"Int64\"" then
            record name TH.Pass ""
        else
            record name TH.Fail (sprintf "response: %s" fullBody)

        // 6. A file that TYPECHECKS but will not lower. The fast half of the
        // payload must survive intact (the editor keeps its hovers), the tier
        // stays "full", and the lowering failure arrives as a real diagnostic
        // -- `blade run` would report exactly this. Hermetic: the store is
        // missing on purpose, and the message doubles as proof that the loop
        // resolved the provider path against the REQUEST file's directory.
        let provPath = Path.Combine(tmpDir, "prov.blade")
        let provSource =
            "import csv as csv\nlet store = csv.load(\"no_such_store.csv\")\nlet a = 1\n"
        let (code, responses, _) =
            drive [ checkReq 15 "full" provPath provSource; pingReq 16; shutdownReq ]
        let name = "full tier: a lowering failure joins diagnostics, payload and loop intact"
        match responses with
        | [broken; pong] when code = 0 && broken.Contains "\"tier\":\"full\""
                              && broken.Contains "\"code\":\"BL6002\""
                              && broken.Contains "no_such_store.csv"
                              && broken.Contains "\"name\":\"a\""
                              && broken.Contains (Path.GetFileName tmpDir)
                              && pong.Contains "\"id\":16" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 7. A parse error is data, not an incident: diagnostics come back and
        // the loop takes the next request.
        let (code, responses, _) = drive [checkReq 13 "fast" hmPath "let ="; pingReq 14; shutdownReq]
        let name = "a parse error yields diagnostics and the loop survives it"
        match responses with
        | [bad; pong] when code = 0 && bad.Contains "\"id\":13"
                           && bad.Contains "\"severity\":\"error\"" && bad.Contains "\"bindings\":[]"
                           && pong.Contains "\"id\":14" && pong.Contains "\"ok\":true" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 8. THE daemon test. `warnSource` leaves a BL4010 suggestion, a
        // `covariance` binding and a kernel behind; the next request is a
        // different file and must inherit none of it.
        let (code, responses, _) =
            drive [ checkReq 21 "fast" warnPath warnSource
                    checkReq 22 "fast" cleanPath "let a = 1\n"
                    shutdownReq ]
        let name = "consecutive checks of different files share no state"
        match responses with
        | [first; second] when code = 0
                               && first.Contains "BL4010" && first.Contains "\"name\":\"covariance\""
                               && second.Contains "\"id\":22" && second.Contains "\"diagnostics\":[]"
                               && not (second.Contains "BL4010")
                               && not (second.Contains "covariance")
                               && second.Contains "\"kernels\":[]" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 9. Malformed input: an error line, correlated where possible, and a
        // loop that keeps going.
        let (code, responses, _) =
            drive [ "{not json}"; "{\"id\":31,\"cmd\":\"fly\"}"; "{\"id\":32,\"cmd\":\"check\"}"
                    pingReq 33; shutdownReq ]
        let name = "malformed and unknown requests answer with errors, never crash"
        match responses with
        | [junk; unknown; incomplete; pong] when code = 0
                                                 && junk.Contains "\"id\":null" && junk.Contains "\"error\""
                                                 && unknown.Contains "\"id\":31" && unknown.Contains "fly"
                                                 && incomplete.Contains "\"id\":32" && incomplete.Contains "\"error\""
                                                 && pong.Contains "\"id\":33" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 10. Both exits: the verb stops reading immediately, and a closed
        // stdin is the same clean 0.
        let (code, responses, _) = drive [shutdownReq; pingReq 41]
        let name = "shutdown exits 0 and leaves the trailing request unread"
        if code = 0 && responses.IsEmpty then record name TH.Pass ""
        else record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        let (code, responses, _) = drive [pingReq 42]
        let name = "stdin EOF exits 0 after answering everything it read"
        match responses with
        | [r] when code = 0 && r.Contains "\"id\":42" -> record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 11. The refactor's own invariant: `ide check --json` still prints
        // exactly what `ideCheckSource` returns, so the extension's one-shot
        // fallback lane is unaffected by the serve work.
        File.WriteAllText(hmPath, hmSource)
        let (json, srcCode) = Blade.Ide.ideCheckSource hmPath hmSource
        let (swOut, oldOut) = (new StringWriter(), Console.Out)
        let cliCode = try Console.SetOut swOut; Blade.Ide.ideCheck hmPath finally Console.SetOut oldOut
        let name = "ide check --json still prints ideCheckSource's payload verbatim"
        if srcCode = cliCode && swOut.ToString().TrimEnd('\r', '\n') = json then
            record name TH.Pass ""
        else
            record name TH.Fail (sprintf "exit %d vs %d" srcCode cliCode)

        // 12. ...including the missing-file arm, which lives only in the
        // printing wrapper now.
        let (code, out, _) =
            let (swOut, swErr) = (new StringWriter(), new StringWriter())
            let (oldOut, oldErr) = (Console.Out, Console.Error)
            try
                Console.SetOut swOut
                Console.SetError swErr
                let r = Blade.Ide.ideCheck (Path.Combine(tmpDir, "nope.blade"))
                (r, swOut.ToString(), swErr.ToString())
            finally
                Console.SetOut oldOut
                Console.SetError oldErr
        let name = "ide check --json on a missing file still emits JSON and exit 1"
        if code = 1 && out.Contains "File not found" && out.Contains "\"bindings\":[]" then
            record name TH.Pass ""
        else
            record name TH.Fail (sprintf "exit %d, json: %s" code (out.Trim()))
    finally
        Directory.SetCurrentDirectory entryDir
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

/// The notebook lane: `ide serve`'s `eval` / `resetSession` commands, driven
/// through the same in-process `serveLoop` seam the block above uses. Every
/// case here rides the INTERPRETER, so the block needs no g++ and no spawn.
///
/// What is under test is REPL SEMANTICS on a structured wire: that a session
/// accumulates, that rebinding splices in place so dependents recompute, that
/// a rejected candidate leaves the session exactly as it was, that two
/// sessions sharing a name share nothing else, and that diagnostics arrive in
/// the CELL's coordinates rather than the assembled session file's -- the one
/// piece of arithmetic a notebook cannot do for itself.
let private runIdeEvalTests () : TH.BlockResult =
    let blockName = "IdeEval"
    TH.printHeader "ide serve eval (session semantics, bindings, cell-local diagnostics)"
    let results = ResizeArray<string * TH.Outcome>()
    let record name outcome detail =
        TH.resultLine outcome name detail
        results.Add((name, outcome))
    let esc = Blade.Ide.jsonEscape
    let evalReq (id: int) (session: string) (source: string) =
        sprintf "{\"id\":%d,\"cmd\":\"eval\",\"session\":\"%s\",\"source\":\"%s\"}"
                id (esc session) (esc source)
    let resetReq (id: int) (session: string) =
        sprintf "{\"id\":%d,\"cmd\":\"resetSession\",\"session\":\"%s\"}" id (esc session)
    let checkReq (id: int) (file: string) (source: string) =
        sprintf "{\"id\":%d,\"cmd\":\"check\",\"tier\":\"fast\",\"file\":\"%s\",\"source\":\"%s\"}"
                id (esc file) (esc source)
    let shutdownReq = "{\"cmd\":\"shutdown\"}"
    /// One conversation, one serveLoop, one sessions dictionary -- so every
    /// scenario below has to send its whole story in a single call.
    let drive (requests: string list) : int * string list * string =
        let input = new StringReader(String.concat "\n" requests + "\n")
        let output = new StringWriter()
        let code = Blade.IdeServe.serveLoop compilerVersion (input :> TextReader) (output :> TextWriter)
        let raw = output.ToString()
        let parts = raw.Split('\n') |> Array.toList
        (code, (parts |> List.filter (fun p -> p <> "")), raw)
    let entryDir = Directory.GetCurrentDirectory()
    try
        // 1. The base case: a declaration is kept, and its binding comes back
        // with the IR-concrete type and the printed value beside it.
        let (code, responses, _) = drive [ evalReq 1 "nb" "let x = 2"; shutdownReq ]
        let name = "eval keeps a declaration and echoes its typed binding"
        match responses with
        | [r] when code = 0 && r.Contains "\"id\":1" && r.Contains "\"kept\":true"
                   && r.Contains "\"exitCode\":0" && r.Contains "\"lane\":\"interp\""
                   && r.Contains "\"elapsedMs\":"
                   && r.Contains "{\"name\":\"x\",\"type\":\"Int64\",\"value\":\"2\"}"
                   && r.Contains "\"diagnostics\":[]" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 2. A bare expression evaluates against the session without joining
        // it, and reports under the EMPTY name -- the transient wrapper's own
        // name is an implementation detail the client never sees.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let x = 2"; evalReq 2 "nb" "x + 1"
                    evalReq 3 "nb" "x + 1"; shutdownReq ]
        let name = "a bare expression echoes under the empty name and is not kept"
        match responses with
        | [_; first; again] when code = 0
                                 && first.Contains "\"kept\":true"
                                 && first.Contains "{\"name\":\"\",\"type\":\"Int64\",\"value\":\"3\"}"
                                 // Not joining the session is what lets the
                                 // same expression echo twice instead of
                                 // diffing to silence the second time.
                                 && again.Contains "{\"name\":\"\",\"type\":\"Int64\",\"value\":\"3\"}" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 3. THE session test. Rebinding `x` replaces the earlier snippet IN
        // PLACE, so the dependent expression recomputes rather than seeing a
        // shadowed duplicate (which would not even compile).
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let x = 2"; evalReq 2 "nb" "let y = x * 10"
                    evalReq 3 "nb" "let x = 5"; evalReq 4 "nb" "y"; shutdownReq ]
        let name = "rebinding a name splices in place and dependents recompute"
        match responses with
        | [_; before; rebind; after] when code = 0
                                          && before.Contains "\"value\":\"20\""
                                          && rebind.Contains "{\"name\":\"x\",\"type\":\"Int64\",\"value\":\"5\"}"
                                          && after.Contains "{\"name\":\"\",\"type\":\"Int64\",\"value\":\"50\"}" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 4. Two notebooks, one serve process, the same name in both: the
        // sessions are keyed independently or this whole design is unusable.
        let (code, responses, _) =
            drive [ evalReq 1 "nbA" "let x = 2"; evalReq 2 "nbB" "let x = 99"
                    evalReq 3 "nbA" "x"; evalReq 4 "nbB" "x"; shutdownReq ]
        let name = "two sessions with clashing names do not leak into each other"
        match responses with
        | [_; _; a; b] when code = 0
                            && a.Contains "{\"name\":\"\",\"type\":\"Int64\",\"value\":\"2\"}"
                            && b.Contains "{\"name\":\"\",\"type\":\"Int64\",\"value\":\"99\"}" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 5. resetSession is the notebook's "restart kernel": every prior
        // binding goes, and an unknown key is a no-op rather than an error
        // (restart fires before the first cell has ever run).
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let x = 2"; resetReq 2 "nb"; evalReq 3 "nb" "x"
                    resetReq 4 "never-seen"; shutdownReq ]
        let name = "resetSession clears the session and tolerates unknown keys"
        match responses with
        | [_; ok; gone; unknown] when code = 0
                                      && ok = "{\"id\":2,\"ok\":true}"
                                      && gone.Contains "\"kept\":false"
                                      && gone.Contains "Unbound variable: x"
                                      && unknown = "{\"id\":4,\"ok\":true}" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 6. A rejected candidate is data, not damage: diagnostics come back
        // in the CELL's coordinates (the error is on the submission's SECOND
        // line, four lines into the assembled session file), no bindings are
        // claimed, and the session evaluates afterwards exactly as before.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let x = 2"
                    evalReq 2 "nb" "let bad = 1\nlet worse = undefined_name_xyz"
                    evalReq 3 "nb" "x + 1"; shutdownReq ]
        let name = "a rejected snippet reports cell-local spans and leaves the session intact"
        match responses with
        | [_; bad; after] when code = 0
                               && bad.Contains "\"kept\":false" && bad.Contains "\"bindings\":[]"
                               && bad.Contains "\"severity\":\"error\",\"line\":2,\"col\":13"
                               && bad.Contains "Unbound variable: undefined_name_xyz"
                               && not (bad.Contains "elsewhere in session")
                               && after.Contains "\"kept\":true"
                               && after.Contains "{\"name\":\"\",\"type\":\"Int64\",\"value\":\"3\"}" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 7. Sessions share the loop with the editor's own checking. A check
        // in between must neither see the session nor disturb it -- each eval
        // re-lowers from its own snippet list, and typeCheck resets its
        // AsyncLocal channels on the way in.
        let tmpDir = Path.Combine(Path.GetTempPath(), "blade_ideeval_" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tmpDir) |> ignore
        let otherPath = Path.Combine(tmpDir, "other.blade")
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let x = 2"
                    checkReq 2 otherPath "let unrelated = 41 + 1\n"
                    evalReq 3 "nb" "x + 1"; shutdownReq ]
        let name = "a check interleaved between two evals disturbs neither"
        match responses with
        | [_; checked_; after] when code = 0
                                    && checked_.Contains "\"id\":2" && checked_.Contains "\"tier\":\"fast\""
                                    && checked_.Contains "\"diagnostics\":[]"
                                    && checked_.Contains "\"name\":\"unrelated\""
                                    && not (checked_.Contains "\"name\":\"x\"")
                                    && after.Contains "{\"name\":\"\",\"type\":\"Int64\",\"value\":\"3\"}" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 8. The remap's hard case. A rebind splices MID-session, so the
        // failure it causes can land in a LATER snippet -- a position with no
        // meaning in this cell. Those clamp to 1:1 and say where they really
        // came from, instead of squiggling an innocent line.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let xs = [1.0, 2.0, 3.0]"
                    evalReq 2 "nb" "let tot = reduce(xs, (+))"
                    evalReq 3 "nb" "let xs = 1.0"
                    evalReq 4 "nb" "tot"; shutdownReq ]
        let name = "a rebind that breaks a LATER snippet clamps and says so"
        match responses with
        | [_; _; broken; after] when code = 0
                                     && broken.Contains "\"kept\":false"
                                     && broken.Contains "\"line\":1,\"col\":1,\"endLine\":1,\"endCol\":1"
                                     && broken.Contains "elsewhere in session: reduce()"
                                     // ...and the session still holds the ARRAY.
                                     && after.Contains "{\"name\":\"\",\"type\":\"Float64\",\"value\":\"6.0\"}" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 9. The display cap travels with the value: a notebook shows what the
        // REPL shows, five entries per bracket level and then `...`.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let v = [1, 2, 3, 4, 5, 6, 7]"; shutdownReq ]
        let name = "binding values carry the REPL's display elision"
        match responses with
        | [r] when code = 0 && r.Contains "\"value\":\"[1, 2, 3, 4, 5, ...]\"" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 10. Framing, on a response whose own content contains newlines: the
        // multi-line submission escapes INTO the line, never through it.
        let (code, responses, raw) =
            drive [ evalReq 1 "nb" "let a = 1\nlet b = undefined_name_xyz"; shutdownReq ]
        let name = "an eval response is exactly one newline-terminated line"
        if code = 0 && responses.Length = 1 && raw.EndsWith "\n" && raw.Split('\n').Length = 2 then
            record name TH.Pass ""
        else
            record name TH.Fail (sprintf "%d newline-separated parts" (raw.Split('\n').Length))

        // 11. A function declaration has no run output at all; its binding
        // still carries the signature the REPL would have echoed.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "function twice(p) = p * 2"; evalReq 2 "nb" "twice"; shutdownReq ]
        let name = "a function binding reports its signature and no value"
        match responses with
        | [decl; probe] when code = 0
                             && decl.Contains "{\"name\":\"twice\",\"type\":\"(Float64) -> Float64\",\"value\":\"\"}"
                             && probe.Contains "{\"name\":\"twice\",\"type\":\"(Float64) -> Float64\",\"value\":\"\"}" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 12. A :paste-shaped cell declares several names at once. All of them
        // are bindings this cell made, in source order.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let m = 3\nlet n = m + 4"; shutdownReq ]
        let name = "a multi-declaration cell reports every name it bound"
        match responses with
        | [r] when code = 0
                   && r.Contains "\"bindings\":[{\"name\":\"m\",\"type\":\"Int64\",\"value\":\"3\"},{\"name\":\"n\",\"type\":\"Int64\",\"value\":\"7\"}]" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 13. Malformed eval requests answer like every other malformed
        // request -- and an UNKNOWN cmd still errors, which is the capability
        // probe both sides of the extension/compiler skew rely on.
        let (code, responses, _) =
            drive [ "{\"id\":31,\"cmd\":\"eval\",\"session\":\"nb\"}"
                    "{\"id\":32,\"cmd\":\"eval\",\"source\":\"let x = 1\"}"
                    "{\"id\":33,\"cmd\":\"resetSession\"}"
                    "{\"id\":34,\"cmd\":\"evaluate\",\"session\":\"nb\",\"source\":\"let x = 1\"}"
                    evalReq 35 "nb" "let x = 1"; shutdownReq ]
        let name = "incomplete eval requests error without stopping the loop"
        match responses with
        | [noSource; noSession; noKey; unknownCmd; good] when
                code = 0
                && noSource.Contains "\"id\":31" && noSource.Contains "requires a \\\"source\\\""
                && noSession.Contains "\"id\":32" && noSession.Contains "requires a \\\"session\\\""
                && noKey.Contains "\"id\":33" && noKey.Contains "requires a \\\"session\\\""
                && unknownCmd.Contains "\"id\":34" && unknownCmd.Contains "evaluate"
                && good.Contains "\"kept\":true" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 14. A runtime guard is a real program fault, not a rejection: the
        // interpreter's output is authoritative (no g++ is consulted), the
        // snippet is still not kept, and the panic reaches the client as a
        // diagnostic as well as on stderr -- a client that builds its error
        // card from the first diagnostic would otherwise have nothing to say.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let z = 1 / 0"; evalReq 2 "nb" "let ok = 6"; shutdownReq ]
        let name = "a runtime panic is not kept and names itself"
        match responses with
        | [panic; after] when code = 0
                              && panic.Contains "\"kept\":false" && panic.Contains "\"exitCode\":1"
                              && panic.Contains "\"lane\":\"interp\"" && panic.Contains "\"bindings\":[]"
                              && panic.Contains "\"stderr\":\"error[BL8007]"
                              && panic.Contains "\"severity\":\"error\",\"line\":1,\"col\":1"
                              && panic.Contains "integer division or modulo by zero"
                              && after.Contains "{\"name\":\"ok\",\"type\":\"Int64\",\"value\":\"6\"}" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        try Directory.Delete(tmpDir, true) with _ -> ()
    finally
        Directory.SetCurrentDirectory entryDir
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

/// `checkCells`: the notebook lane's CHECK half, driven in-process like the
/// rest. The extension no longer assembles a notebook itself -- it ships the
/// ordered cell sources and reads back one window per cell -- so what is
/// under test is the assembly this repo now owns. The load-bearing case is a
/// REBIND: the later definition has to govern every downstream cell, because
/// the alternative (keeping the earlier one, which is what the extension's
/// own copy used to do) types the whole tail of a notebook off a superseded
/// literal. Plus the invariants a client cannot check for itself: one window
/// per cell, windows that never overlap, and a check that commits nothing to
/// an eval session.
let private runIdeCellsTests () : TH.BlockResult =
    let blockName = "IdeCells"
    TH.printHeader "ide serve checkCells (notebook assembly, per-cell windows)"
    let results = ResizeArray<string * TH.Outcome>()
    let record name outcome detail =
        TH.resultLine outcome name detail
        results.Add((name, outcome))
    let esc = Blade.Ide.jsonEscape
    let cellsReq (id: int) (tier: string) (file: string) (cells: string list) =
        let arr = cells |> List.map (fun c -> sprintf "\"%s\"" (esc c)) |> String.concat ","
        sprintf "{\"id\":%d,\"cmd\":\"checkCells\",\"tier\":\"%s\",\"file\":\"%s\",\"cells\":[%s]}"
                id tier (esc file) arr
    let evalReq (id: int) (session: string) (source: string) =
        sprintf "{\"id\":%d,\"cmd\":\"eval\",\"session\":\"%s\",\"source\":\"%s\"}"
                id (esc session) (esc source)
    let shutdownReq = "{\"cmd\":\"shutdown\"}"
    let drive (requests: string list) : int * string list * string =
        let input = new StringReader(String.concat "\n" requests + "\n")
        let output = new StringWriter()
        let code = Blade.IdeServe.serveLoop compilerVersion (input :> TextReader) (output :> TextWriter)
        let raw = output.ToString()
        let parts = raw.Split('\n') |> Array.toList
        (code, (parts |> List.filter (fun p -> p <> "")), raw)
    // Structural readers rather than substring matching: the point of these
    // assertions is the ARITHMETIC (which window a binding landed in), and a
    // literal JSON fragment would pin the layout instead of the property.
    let intProp (e: System.Text.Json.JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v -> Some (v.GetInt32())
        | _ -> None
    let strProp (e: System.Text.Json.JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = System.Text.Json.JsonValueKind.String -> Some (v.GetString())
        | _ -> None
    /// windows[] as (startLine, endLine, wrapLine option, wrapCol option).
    let windowsOf (json: string) =
        use doc = System.Text.Json.JsonDocument.Parse json
        match doc.RootElement.TryGetProperty "windows" with
        | true, ws ->
            [ for w in ws.EnumerateArray() ->
                (defaultArg (intProp w "startLine") 0, defaultArg (intProp w "endLine") 0,
                 intProp w "wrapLine", intProp w "wrapCol") ]
        | _ -> []
    /// A named binding's (line, type), if the payload reported one.
    let bindingOf (json: string) (name: string) =
        use doc = System.Text.Json.JsonDocument.Parse json
        match doc.RootElement.TryGetProperty "bindings" with
        | true, bs ->
            bs.EnumerateArray()
            |> Seq.tryFind (fun b -> strProp b "name" = Some name)
            |> Option.map (fun b -> (defaultArg (intProp b "line") 0, defaultArg (strProp b "type") ""))
        | _ -> None
    let diagCount (json: string) =
        use doc = System.Text.Json.JsonDocument.Parse json
        match doc.RootElement.TryGetProperty "diagnostics" with
        | true, ds -> ds.GetArrayLength()
        | _ -> -1
    let inWindow (startL, endL, _, _) line = line >= startL && line <= endL
    let tmpDir = Path.Combine(Path.GetTempPath(), "blade_idecells_" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmpDir) |> ignore
    let entryDir = Directory.GetCurrentDirectory()
    try
        let nbPath = Path.Combine(tmpDir, "demo.bladenb")
        // The sample notebook's own shape, and the one the extension got
        // wrong: a bare-expression cell sits BETWEEN two definitions of `xs`,
        // so neither "drop the earlier" nor "drop the later" is enough on its
        // own -- the later text has to take the earlier one's place.
        let demoCells =
            [ "let xs = [1.0, 2.0, 3.0]"
              "reduce(xs, (+)) |> compute"
              "let xs = [10.0, 20.0, 30.0, 40.0]"
              "let xloop = method_for(xs, xs)" ]

        // 1. The shape contract: one window per cell, in cell order.
        let (code, responses, _) = drive [ cellsReq 1 "fast" nbPath demoCells; shutdownReq ]
        let body = match responses with [r] -> r | _ -> ""
        let wins = windowsOf body
        let name = "checkCells echoes id/tier and returns one window per cell"
        if code = 0 && body.Contains "\"id\":1" && body.Contains "\"tier\":\"fast\""
           && List.length wins = List.length demoCells then
            record name TH.Pass ""
        else
            record name TH.Fail (sprintf "exit %d, %d windows for %d cells: %A"
                                         code (List.length wins) (List.length demoCells) wins)

        // 2. Windows partition the assembled source: a payload entry can
        // belong to at most one cell, or the client's fan-out would show the
        // same diagnostic twice.
        let name = "windows are well-formed and never overlap"
        let ordered = wins |> List.map (fun (s, e, _, _) -> (s, e)) |> List.sortBy fst
        let wellFormed = ordered |> List.forall (fun (s, e) -> s >= 1 && e >= s)
        let disjoint =
            ordered |> List.pairwise |> List.forall (fun ((_, e1), (s2, _)) -> s2 > e1)
        if wellFormed && disjoint then record name TH.Pass ""
        else record name TH.Fail (sprintf "%A" ordered)

        // 3. THE test. The rebind wins, so `xs` is the four-element literal
        // and it is reported inside the cell that WROTE it -- cell 2, not the
        // superseded cell 0.
        let name = "a rebound name is governed by the later definition"
        match bindingOf body "xs", wins with
        | Some (line, ty), _ when ty.Contains "Idx<4>" && not (ty.Contains "Idx<3>")
                                  && inWindow (List.item 2 wins) line
                                  && not (inWindow (List.item 0 wins) line) ->
            record name TH.Pass ""
        | b, _ -> record name TH.Fail (sprintf "xs binding %A, windows %A" b wins)

        // 4. The in-between use has to BIND -- that is the whole reason the
        // later text moves up rather than the earlier one surviving.
        let name = "the assembled source typechecks: the in-between use is not unbound"
        if diagCount body = 0 then record name TH.Pass ""
        else record name TH.Fail (sprintf "%d diagnostics: %s" (diagCount body) body)

        // 5. A bare-expression cell cannot stand at top level in the file
        // grammar, so it carries a synthetic binding -- and the client needs
        // the prefix width to shift that line's columns back.
        let name = "a bare-expression cell is wrapped and reports wrapLine/wrapCol"
        match List.item 1 wins with
        | (s, _, Some wl, Some wc) when wl = s && wc > 0 && (bindingOf body "__cell1").IsSome ->
            record name TH.Pass ""
        | w -> record name TH.Fail (sprintf "cell 1 window %A" w)

        // 6. ...and only that cell does. A declaration cell is already legal.
        let name = "declaration cells carry no wrapper"
        let declWins = [ List.item 0 wins; List.item 2 wins; List.item 3 wins ]
        if declWins |> List.forall (fun (_, _, wl, wc) -> wl.IsNone && wc.IsNone) then
            record name TH.Pass ""
        else record name TH.Fail (sprintf "%A" declWins)

        // 7. Isolation, the promise `check` already makes: checkCells fires on
        // every keystroke, so committing anything to a session would corrupt
        // the notebook the user is actually running.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let x = 2"
                    cellsReq 2 "fast" nbPath [ "let unrelated = 9" ]
                    evalReq 3 "nb" "x"; shutdownReq ]
        let name = "checkCells commits nothing to an eval session"
        match responses with
        | [_; checkBody; after] when code = 0
                                     && checkBody.Contains "\"name\":\"unrelated\""
                                     && not (checkBody.Contains "\"name\":\"x\"")
                                     && after.Contains "{\"name\":\"\",\"type\":\"Int64\",\"value\":\"2\"}" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 8. The full tier reaches monomorphization here exactly as it does
        // for a single file -- a notebook is where HM values are most common.
        let (code, responses, _) =
            drive [ cellsReq 4 "full" nbPath [ "function id(x: T) -> T = x"; "let r = id(42)" ]; shutdownReq ]
        let fullBody = match responses with [r] -> r | _ -> ""
        let name = "checkCells tier=full upgrades HM bindings with concreteType"
        if code = 0 && fullBody.Contains "\"tier\":\"full\"" && fullBody.Contains "\"concreteType\":\"Int64\"" then
            record name TH.Pass ""
        else record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 9. The malformed-request arms, in the same shape the other commands
        // answer with -- an old extension probing a new compiler reads these.
        let (code, responses, _) =
            drive [ "{\"cmd\":\"checkCells\",\"file\":\"a\",\"cells\":[]}"
                    "{\"id\":41,\"cmd\":\"checkCells\",\"cells\":[]}"
                    sprintf "{\"id\":42,\"cmd\":\"checkCells\",\"file\":\"%s\"}" (esc nbPath)
                    shutdownReq ]
        let name = "checkCells rejects a missing id, file, or cells array"
        match responses with
        | [noId; noFile; noCells] when code = 0
                                       && noId.Contains "\"id\":null" && noId.Contains "requires an integer"
                                       && noFile.Contains "\"id\":41" && noFile.Contains "requires a \\\"file\\\""
                                       && noCells.Contains "\"id\":42" && noCells.Contains "requires a \\\"cells\\\"" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 10. `Unit` is the one declaration keyword the lexer capitalises, and
        // ReplSession.declRe used to spell it `unit` -- so a unit-declaration
        // cell missed the declaration lane and got wrapped in `let __cellN = `,
        // which cannot parse. It only ever showed up once `import units.SI`
        // moved OUT of the cell, because the import matched declRe for it.
        let unitCells =
            [ "import units.SI"
              "Unit day = 86400 * second"
              "let t: T<day>^0 = 1.5" ]
        let (code, responses, _) = drive [ cellsReq 61 "fast" nbPath unitCells; shutdownReq ]
        let unitBody = match responses with [r] -> r | _ -> ""
        let name = "a Unit-declaration cell is a declaration, not a wrapped expression"
        match windowsOf unitBody with
        | [_; (_, _, None, None); _] when code = 0 && diagCount unitBody = 0 ->
            record name TH.Pass ""
        | ws -> record name TH.Fail (sprintf "exit %d, %d diagnostics, windows %A: %s"
                                             code (diagCount unitBody) ws unitBody)

        // ...and a re-run of that cell has to REPLACE its earlier text, the way
        // every other declaration does. Appending a second `Unit day` would
        // redeclare it.
        let (code, responses, _) =
            drive [ cellsReq 62 "fast" nbPath
                        [ "import units.SI"; "Unit day = 86400 * second"
                          "Unit day = 43200 * second" ]
                    shutdownReq ]
        let rebindBody = match responses with [r] -> r | _ -> ""
        let name = "a rebound Unit declaration supersedes the earlier one"
        if code = 0 && diagCount rebindBody = 0 then record name TH.Pass ""
        else record name TH.Fail (sprintf "exit %d, %d diagnostics: %s"
                                          code (diagCount rebindBody) rebindBody)

        // 11. An empty notebook is a real state (a fresh .bladenb) and must
        // answer like any other, not fault.
        let (code, responses, _) = drive [ cellsReq 51 "fast" nbPath []; shutdownReq ]
        let name = "an empty cell list answers with an empty windows array"
        match responses with
        | [r] when code = 0 && r.Contains "\"id\":51" && windowsOf r = [] -> record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        try Directory.Delete(tmpDir, true) with _ -> ()
    finally
        Directory.SetCurrentDirectory entryDir
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

/// The `references[]` array behind go-to-definition, find-all-references and
/// rename, driven through `ideCheckSource` in-process (no file on disk, no
/// toolchain). What is really under test is the JOIN: an entry is one BINDER,
/// so two shadowing `x`s have to come back as two entries with DISJOINT use
/// lists, and every span has to be the name TOKEN rather than the declaration
/// wrapped around it -- rename rewrites these spans literally.
let private runIdeReferencesTests () : TH.BlockResult =
    let blockName = "IdeReferences"
    TH.printHeader "ide references (definition/use spans, shadowing, name tokens)"
    let results = ResizeArray<string * TH.Outcome>()
    let record name outcome detail =
        TH.resultLine outcome name detail
        results.Add((name, outcome))
    /// One flat line per entry -- "name kind def [uses]" -- which is exactly
    /// the information a navigation provider consumes, and short enough that
    /// the expectations below can be whole-list equalities.
    let refsOf (source: string) : string list =
        let (json, _) = Blade.Ide.ideCheckSource "refs.blade" source
        use doc = System.Text.Json.JsonDocument.Parse json
        let spanText (e: System.Text.Json.JsonElement) =
            sprintf "%d:%d-%d:%d"
                (e.GetProperty("line").GetInt32()) (e.GetProperty("col").GetInt32())
                (e.GetProperty("endLine").GetInt32()) (e.GetProperty("endCol").GetInt32())
        [ for r in doc.RootElement.GetProperty("references").EnumerateArray() do
            let def = r.GetProperty "def"
            let defText =
                if def.ValueKind = System.Text.Json.JsonValueKind.Null then "null" else spanText def
            let uses = r.GetProperty("uses").EnumerateArray() |> Seq.map spanText |> List.ofSeq
            yield sprintf "%s %s %s [%s]"
                    (r.GetProperty("name").GetString()) (r.GetProperty("kind").GetString())
                    defText (String.concat " " uses) ]
    let expect name (source: string) (expected: string list) =
        let actual = refsOf source
        if actual = expected then record name TH.Pass ""
        else record name TH.Fail (sprintf "got %A" actual)

    // 1. The base case: a value binding, and both of its uses on the next line.
    expect "a let binding reports its name token and every use"
        "let x = 10\nlet y = x + x\n"
        [ "x value 1:5-1:6 [2:9-2:10 2:13-2:14]"
          "y value 2:5-2:6 []" ]

    // 2. THE test. Same name, two binders: the module-level `x` is never read,
    // and the one shadowing it inside the function owns the only use. Keyed by
    // name instead of IRId, this would be one entry with a merged use list and
    // rename would corrupt the file.
    expect "a shadowed name yields two entries with disjoint uses"
        "let x = 1\nfunction shadow(p) = {\n    let x = p + 1\n    x * 2\n}\n"
        [ "x value 1:5-1:6 []"
          "shadow function 2:10-2:16 []"
          "p param 2:17-2:18 [3:13-3:14]"
          "x local 3:9-3:10 [4:5-4:6]" ]

    // 3. Function name and parameters, all from the parser's name tokens (the
    // decl's own span covers signature and body together and is useless here).
    expect "function and parameter definitions are name tokens, not declarations"
        "function scale(a, k) = a * k\n"
        [ "scale function 1:10-1:15 []"
          "a param 1:16-1:17 [1:24-1:25]"
          "k param 1:19-1:20 [1:28-1:29]" ]

    // 4. A binding inside a function body is "local", and its use resolves to
    // it rather than to anything at module level.
    expect "a function-body let is kind \"local\""
        "function body(n) = {\n    let acc = n + 1\n    acc * acc\n}\n"
        [ "body function 1:10-1:14 []"
          "n param 1:15-1:16 [2:15-2:16]"
          "acc local 2:9-2:12 [3:5-3:8 3:11-3:14]" ]

    // 5. Kernel parameters: a lambda can sit anywhere in an expression, so
    // these come from a full-tree sweep rather than the declaration walk.
    expect "lambda kernel parameters are reported like any other param"
        "let data = [[1.0, 2.0], [3.0, 4.0]]\n\
         let out = object_for(lambda(u, w) -> u * w) <@> (data, data) |> compute\n"
        [ "data value 1:5-1:9 [2:50-2:54 2:56-2:60]"
          "out value 2:5-2:8 []"
          "u param 2:29-2:30 [2:38-2:39]"
          "w param 2:32-2:33 [2:42-2:43]" ]

    // 6. `type` names have no IRId and nothing ever refers to one through a
    // variable node, so they are def-only entries located in the source text.
    expect "a type declaration is a def-only entry of kind \"type\""
        "type Small = Idx<4>\nlet g = 1\n"
        [ "Small type 1:6-1:11 []"
          "g value 2:5-2:6 []" ]

    // 7. Nothing compiler-generated leaks. The elaborators stamp the WHOLE
    // declaration's span onto every node they synthesize, so a phantom shows
    // up as a span wider than its own identifier -- the check below is exactly
    // that: every span is one line and exactly as wide as the name.
    let broadSource =
        "function mymean(row) = reduce(row, (+)) / extents(row)\n\
         function covariance(a, b) = mymean((a - mymean(a)) * (b - mymean(b)))\n\
         let data = [[1.0, 2.0, 3.0], [2.0, 4.0, 6.0]]\n\
         let result = object_for(covariance) <@> (data, data) |> compute\n"
    let broad = refsOf broadSource
    let name = "no synthesized names and no declaration-wide phantom spans"
    let widthOk (line: string) =
        // "name kind L:C-L:C [L:C-L:C ...]"
        let parts = line.Split(' ')
        let nameLen = parts.[0].Length
        let spans =
            line.Substring(line.IndexOf(parts.[2]))
            |> fun s -> s.Replace("[", " ").Replace("]", " ").Split([|' '|], StringSplitOptions.RemoveEmptyEntries)
        spans
        |> Array.forall (fun sp ->
            match sp.Split([|':'; '-'|]) with
            | [| l1; c1; l2; c2 |] -> l1 = l2 && int c2 - int c1 = nameLen
            | _ -> false)
    if not broad.IsEmpty
       && broad |> List.forall (fun l -> not (l.StartsWith "__"))
       && broad |> List.forall widthOk then
        record name TH.Pass ""
    else
        record name TH.Fail (sprintf "got %A" broad)

    // 8. A file with a type error still navigates: the checker's PARTIAL typed
    // program feeds references exactly as it already feeds bindings and calls.
    expect "a type error still yields references for the parts that checked"
        "let good = 5\nfunction useit(v) = v + good\nlet bad: Int64 = \"nope\"\n"
        [ "good value 1:5-1:9 [2:25-2:29]"
          "useit function 2:10-2:15 []"
          "v param 2:16-2:17 [2:21-2:22]" ]

    // 9. A binding nobody reads is still renameable, so it still gets an entry.
    expect "an unused binding keeps an entry with an empty use list"
        "let orphan = 42\n"
        [ "orphan value 1:5-1:11 []" ]

    // 10. `let rec` used to stamp the whole `match ... with` block onto its
    // pattern; a rename over that span would have eaten the declaration.
    expect "a `let rec` definition is the name token, not the whole declaration"
        "type Step = Idx<5>\n\
         let rec q: Array<Float64 like Step> = match q with\n\
         | zero -> zero\n\
         | prefix :: n -> prefix :: 1.0\n\
         let out = q\n"
        [ "Step type 1:6-1:10 []"
          "q value 2:9-2:10 [5:11-5:12]"
          "out value 5:5-5:8 []" ]

    // 11. An interface-impl method reaches the typed AST MANGLED (`Box__scale`),
    // which is not text that appears anywhere in the file; the name is taken
    // from the span instead, or rename would paste the mangling into the source.
    expect "an impl method is reported under its written name, not its mangled one"
        // Assembled line by line: the indentation is load-bearing for the
        // expected columns, and F#'s string continuations would eat it.
        (String.concat "\n"
            [ "interface Scalable {"
              "    function scale(self, factor: Float64) -> Float64"
              "}"
              "struct Box {"
              "    width: Float64,"
              "    height: Float64"
              "}"
              "impl Scalable for Box {"
              "    function scale(self, factor: Float64) -> Float64 = self.width * factor"
              "}"
              "" ])
        [ "Box type 4:8-4:11 []"
          "scale function 9:14-9:19 []"
          "self param 9:20-9:24 [9:56-9:60]"
          "factor param 9:26-9:32 [9:69-9:75]" ]

    // 12. The `bindings[]` companion change: `endLine`/`endCol` close the
    // DECLARATION span that `line`/`col` already opened, appended last so the
    // leading field run every existing client matches on is byte-identical.
    let (json, _) = Blade.Ide.ideCheckSource "refs.blade" "let x = 10\n"
    let name = "bindings[] gained end corners without disturbing the leading fields"
    if json.Contains "\"name\":\"x\",\"kind\":\"let\",\"line\":1,\"col\":1,\"type\":\"Int64\""
       && json.Contains "\"endLine\":1,\"endCol\":11" then
        record name TH.Pass ""
    else
        record name TH.Fail json

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

/// Run the full suite, appending the CLI smoke block and the strict-pin block
/// (which live in this file -- see runAllTestsFullWith's doc comment for why they're passed in).
let private runFullSuite opts =
    runAllTestsFullWith
        [runCliSmokeTests; runStrictPinTests; runSurfacingTests
         runIdeServeTests; runIdeEvalTests; runIdeCellsTests; runIdeReferencesTests] opts

/// Dispatch the `test` subcommand. `rest` is everything after "test".
let private dispatchTest (rest: string list) : int =
    // `--omp` / `--cuda` / `--timing` / `--mpi` / `--interp` / `--diff-oracle`
    // opt the corresponding blocks into the full suite, in any combination;
    // each also has a standalone arm below.
    let isSuiteFlag f =
        f = "--omp" || f = "--cuda" || f = "--timing" || f = "--mpi"
        || f = "--interp" || f = "--diff-oracle"
    match rest with
    | [] -> runFullSuite defaultFullSuiteOptions
    | flags when flags |> List.forall isSuiteFlag ->
        runFullSuite { IncludeOmp = List.contains "--omp" flags
                       IncludeCuda = List.contains "--cuda" flags
                       IncludeTiming = List.contains "--timing" flags
                       IncludeMpi = List.contains "--mpi" flags
                       IncludeInterpDiff = List.contains "--interp" flags
                       IncludeDiffOracle = List.contains "--diff-oracle" flags }
    | [ "--ir-only" ] -> runAllTests ()
    | [ "--gen" ] -> runAllTestsGenOnly ()
    | [ "strict-pins" ] | [ "strictpins" ] ->
        // The --strict-pins CLI gate standalone. In-process, no toolchain; also part of the full suite.
        let failed = (runStrictPinTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "surfacing" ] ->
        // Warning/suggestion surfacing: codes, streams, and survival of the checker's error path.
        let failed = (runSurfacingTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "ide-serve" ] | [ "ideserve" ] ->
        // The NDJSON daemon protocol, driven in-process. No toolchain, no spawn.
        let failed = (runIdeServeTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "ide-eval" ] | [ "ideeval" ] ->
        // The notebook lane: session semantics over NDJSON, interpreter only.
        let failed = (runIdeEvalTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "ide-cells" ] | [ "idecells" ] ->
        // The notebook lane's check half: assembly + per-cell windows.
        let failed = (runIdeCellsTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "ide-references" ] | [ "idereferences" ] | [ "ide-refs" ] ->
        // The navigation payload: definition/use spans, shadowing, name tokens.
        let failed = (runIdeReferencesTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "linalg" ] ->
        // gram/matmul/dot/gemv route to blade_linalg:: when the BLAS gate is
        // on, else Blade's own loops; shim inclusion, routing policy table.
        // Plus the runtime contiguity probe (needs g++): n=2 packed-symmetric
        // must be REFUSED, not handed to BLAS past its pool.
        let emitFailed = (Blade.Tests.LinAlgTests.runLinAlgEmissionTests ()).Failed
        let probeFailed = (Blade.Tests.LinAlgTests.runLinAlgProbeTests ()).Failed
        if emitFailed + probeFailed = 0 then 0 else 1
    | [ "multifile" ] ->
        // The cross-module corpus (tests/corpus/multifile), standalone. Also
        // part of the full suite; broken out because it is the only slice that
        // exercises `lowerMultiSource` and therefore the only one that can see
        // a cross-module shape specialization.
        let failed = (runMultiFileTestsFull "Multi-File Modules" multiFileTests "./generated_cpp_tests").Failed
        if failed = 0 then 0 else 1
    | [ "module-resolve" ] | [ "moduleresolve" ] | [ "modres" ] ->
        // File-based module resolution + stdlib/units/SI.blade: search path,
        // transitive walk, cycle/duplicate/missing refusals, and the
        // byte-identity claim for a file with no imports. Needs real files, so
        // it writes a scratch tree under TEMP; front-end only apart from one
        // value case that skips without g++.
        let failed = (Blade.Tests.ModuleResolveTests.runModuleResolveTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "shapespec" ] | [ "shape-spec" ] ->
        // Which call sites earn a shape-specialized copy and which decline.
        // Pure lowering + codegen, no toolchain.
        let failed = (Blade.Tests.ShapeSpecTests.runShapeSpecTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "lapack" ] ->
        // math.eigh routes to blade_lapack::blade_eigh_{packed,dense}_{s,d,c,z}
        // when the LAPACK gate is on with no explicit sweeps budget, else the
        // cyclic-Jacobi source; complex tuple typing; BLAS/LAPACK dependency
        // separation; inferEigh rejections (e.g. complex-symmetric).
        let failed = (Blade.Tests.LapackTests.runLapackEmissionTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "normalize" ] ->
        // IR-level F# unit tests for the type normalizer. No Blade source pipeline.
        let failed = (runNormalizeTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "display-frames" ] ->
        // Display-frame BYTES + both channels (REPL sentinel line, `ide serve`
        // display array). Drives the interpreter and the session engine
        // directly -- no g++, no editor.
        let failed = (Blade.Tests.Display.runDisplayTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "unify" ] ->
        // TypeCheck-level F# unit tests for the unify fast path: constructs
        // IRType values directly and calls unify. No Blade source pipeline.
        let failed = (runUnifyTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "validate-arrow" ] ->
        // IR-level F# unit tests for the validateArrowShape gate at
        // mkVirtualArrayArrow entry. No Blade source pipeline.
        let failed = (runValidateArrowTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "type-structure" ] ->
        // Type-level structural assertions on lowered Blade source: deduced IR
        // type (rank, per-group arity+symmetry, elem type) via matchesTypePattern. No codegen/run.
        let failed = (Blade.Tests.TypeStructure.runTypeStructureTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "attrs" ] ->
        // IR-level F# unit tests for the exprAttrs bottom-up attribute
        // computation. No Blade source pipeline.
        let failed = (runAttrsTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "subst" ] ->
        // F# unit tests for the contains-substitution mechanism in exprToCpp:
        // renders IR fragments with populated and empty SubstMaps. No Blade source pipeline.
        let failed = (runCodeGenSubstTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "shape" ] ->
        // F# unit tests for the canonical ExprShape traversal:
        // childrenOf/rebuildWith round-trips, mapIRExpr identity, and
        // collectVarRefsIR completeness. No Blade source pipeline.
        let failed = (Blade.Tests.Shape.runShapeTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "diff-oracle" ] ->
        // Differential gate: this binary vs the pinned ./oracle build over
        // the dense corpus slice -- identical printed VALUES required.
        let failed = (Blade.Tests.DiffOracle.runDiffOracleTests "./oracle/Blade.exe" Blade.Tests.DiffOracle.denseSlice).Failed
        if failed = 0 then 0 else 1
    | [ "diff-oracle"; cat ] ->
        // Single corpus category against the pinned oracle.
        let failed = (Blade.Tests.DiffOracle.runDiffOracleTests "./oracle/Blade.exe" [cat]).Failed
        if failed = 0 then 0 else 1
    | [ "interp" ] ->
        // Interpreter differential gate: tree-walking IR interpreter vs the
        // compiled binary over the supported corpus slice -- byte-identical normalized stdout required.
        let failed = (Blade.Tests.InterpDiff.runInterpDiffTests Blade.Tests.InterpDiff.currentSlice).Failed
        if failed = 0 then 0 else 1
    | [ "interp"; cat ] ->
        // Single corpus category through the interpreter differential gate.
        let failed = (Blade.Tests.InterpDiff.runInterpDiffTests [cat]).Failed
        if failed = 0 then 0 else 1
    | [ "spans" ] ->
        // Error-location tests: deliberately broken sources, asserting the reported line. No C++ pipeline.
        let failed = (Blade.Tests.Spans.runSpanTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "diagnostics" ] ->
        // Diagnostics core (renderer + registry) and the diagnostics corpus
        // (broken sources with pinned codes/spans). No C++ pipeline.
        let core = (Blade.Tests.DiagnosticsCore.runDiagnosticsCoreTests ()).Failed
        let corpus = (Blade.Tests.DiagCorpus.runDiagCorpusTests ()).Failed
        // BL4011 suggestions: pinned (and pinned-ABSENT) over the ml-equiv corpus.
        let certSuggest = (Blade.Tests.DiagCorpus.runCertSuggestTests ()).Failed
        if core + corpus + certSuggest = 0 then 0 else 1
    | [ "rep-differential" ] | [ "repdifferential" ] ->
        // Deduction parity gate: the typed rep-status deduction vs the seam
        // inference, proposal by proposal over the ml-equiv corpus. In-process, no C++ pipeline; also part of the full suite.
        let failed = (Blade.Tests.RepDifferential.runRepDifferentialTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "rep-check" ] | [ "repcheck" ] ->
        // Declared-certificate agreement gate: the typed walker's SECOND
        // OPINION on every certificate the elaboration seam already checked.
        // Zero disagreements over the ml-equiv corpus (else a compiler bug).
        let failed = (Blade.Tests.RepCheckAgreement.runRepCheckAgreementTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "rep-reject" ] | [ "repreject" ] ->
        // Rejection-parity census: the only gate that looks at REFUSED
        // programs. For every ml-equiv reject-probe, measures what the typed
        // walker would say by shadowing the `ml.equiv` pin so it reaches typecheck.
        let failed = (Blade.Tests.RepRejectCensus.runRepRejectCensusTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "gal-layer" ] | [ "gallayer" ] ->
        // MEASUREMENT ONLY: what a typecheck-resident walker would conclude
        // about every galilean certificate in the corpus, accepted and
        // refused alike. Changes no checking behaviour; not in the full suite.
        let failed = (Blade.Tests.GalLayerCensus.runGalLayerCensusTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "perm-layer" ] | [ "permlayer" ] ->
        // MEASUREMENT ONLY: the PERM discipline's layer question, plus the
        // first perm INFERENCE experiment (writes the pin back into the
        // source, runs the shipped seam checker). Not in the full suite.
        let failed = (Blade.Tests.PermLayerCensus.runPermLayerCensusTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "oracles" ] ->
        // Differential-harness oracles checked against hand-computed / analytic values.
        let failed = (Blade.Tests.OracleReview.runOracleTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "orbrank" ] | [ "orb-rank" ] ->
        // OrbIdx cardinality fold, canonicalizer, segment-peeled traversal
        // stream, and rank/unrank pair (src/OrbRank.fs), pinned against
        // brute-force canonicalization as SET and ORDER (a read->write roundtrip can't catch an order mismatch).
        let failed = (Blade.Tests.OrbRankReview.runOrbRankTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "sympower" ] | [ "sympower-tables" ] ->
        // T_{j,l} Sym-power occurrence tables (SymPowerTables.fs): exact
        // rational kernel/Gram pins, the realization phase rule, realCG completeness.
        let failed = (Blade.Tests.SymPowerTablesReview.runSymPowerTablesTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "polyoracle" ] | [ "poly-oracle" ] ->
        // Sym^k label basis checked against isotypic projectors from an
        // independent Casimir-Lagrange route (exact integer/rational).
        let failed = (Blade.Tests.PolyOracleReview.runPolyOracleTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "lietables" ] | [ "lie-tables" ] ->
        // Exact so(3) generator tables and the radical-vector Lie discharger
        // (MLLieDischarge.fs): assemble/exponentiate each table, compare
        // against the real Wigner action fit from solid harmonics, plus exact
        // algebra (skew-symmetry, brackets, Casimir) and negative controls.
        let failed = (Blade.Tests.LieTablesReview.runLieTablesTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "permspec" ] | [ "perm-spec" ] ->
        // Sn permutation-module counting layer (MLPermSpec.fs): RGS partition
        // enumeration vs the Stirling recurrence and an independent
        // enumerator, witness-unitriangularity, perm_weight/bias_dim sizing.
        let failed = (Blade.Tests.PermSpecReview.runPermSpecTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "permoracle" ] | [ "perm-oracle" ] ->
        // Coarsening-indicator basis checked for COMPLETENESS against the
        // exact rational Reynolds projector over Q; Gram closed form from an
        // independent union-find join. BigInteger fractions, no float/tolerance.
        let failed = (Blade.Tests.PermOracleReview.runPermOracleTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "structidx" ] | [ "struct-idx" ] ->
        // Constrained-record COUNTING layer (StructIdxSpec.fs): box
        // enumeration over per-field INCLUSIVE bounds with a two-route
        // certificate (flat filter vs arrow-style heads filter, set AND
        // order), the CGm112 anchor sweep, idx_card(R) via resolveStatics.
        let failed = (Blade.Tests.StructIdxSpecReview.runStructIdxSpecTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "structidxoracle" ] | [ "struct-idx-oracle" ] ->
        // Independently coded recursive per-field enumerator over the same
        // solution sets, compared against StructIdxSpec.enumerateBox as SET and ORDER.
        let failed = (Blade.Tests.StructIdxOracle.runStructIdxOracleTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "pgspec" ] | [ "pg-spec" ] ->
        // Point-group counting layer (MLPointSpec.fs): frozen {C4, D4} tables
        // and their integrity certificate (closure vs declared order,
        // orthogonality, FS indicators, R-Burnside trap sum), 9-vs-5 FS
        // contrast, generic e-weighted core vs MLSpec.homDim/homBlocks (15-spec sweep).
        let failed = (Blade.Tests.PointSpecReview.runPointSpecTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "pgoracle" ] | [ "pg-oracle" ] ->
        // Emitted point-group Hom basis checked for COMPLETENESS against the
        // exact rational Reynolds projector over Q, Gram closed form d*I_e
        // per cell, three negative controls. BigInteger fractions throughout.
        let failed = (Blade.Tests.PgOracleReview.runPgOracleTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "alloc" ] ->
        // Standalone C++ runtime-layout tests for the contiguous-backing
        // allocate<>: contiguity/cardinality invariants value-checking Blade tests cannot catch.
        let failed = (Blade.Tests.AllocTests.runAllocLayoutTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "orbwreath" ] | [ "orb-wreath" ] ->
        // Standalone C++ wreath-class storage tests: segment-peeled traversal
        // order, cardinality fold, rank/unrank bijection, canon signs, overflow walls.
        let failed = (Blade.Tests.OrbWreathTests.runOrbWreathTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "omp-pragma" ] ->
        // Codegen-string checks: `where omp(...)` reaches C++ as a pragma for
        // every kernel spelling, none for unannotated ones. No toolchain needed.
        let failed = (Blade.Tests.OmpTests.runOmpPragmaTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "omp-reduce" ] ->
        // Comm-licensed parallel reductions: compile omp and serial spellings
        // of the same fold, diff values; Path-B determinism, collapse(2) gates. Needs g++.
        let failed = (Blade.Tests.OmpTests.runOmpReduceTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "omp-coverage" ] ->
        // OpenMP thread-coverage: generate loop programs with codegen
        // test-mode instrumentation, compile -fopenmp, run with forced
        // threads, verify emitted pragmas form genuine parallel regions.
        let failed = (Blade.Tests.OmpTests.runOmpCoverageTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "cli" ] ->
        // CLI smoke: compile+run a one-line .edgi via the user-facing compileToExe path.
        let failed = (runCliSmokeTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "cuda" ] ->
        // CUDA kernel block (differential vs host-loop oracle) plus cuBLAS
        // swap-table verification. Skips without nvcc/GPU; on Windows run from x64 Native Tools prompt.
        let failed =
            (Blade.Tests.CudaTests.runCudaTests ()).Failed
            + (Blade.Tests.CudaTests.runCublasSwapTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "mpi" ] ->
        // MPI decomposition block (differential vs serial oracle under
        // mpiexec -n 1/2/4). Skips without g++ / -lmsmpi / mpiexec.
        let failed = (Blade.Tests.MpiTests.runMpiTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "timing" ] ->
        // Differential timing: (r!)^d speedup of comm-annotation and
        // symmetric-type forms vs dense. Warns (never fails) on a slow ratio.
        let failed = (Blade.Tests.Benchmarks.runDifferentialTimingTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "netcdf" ] ->
        // NetCDF provider tests 1-6 run against a mock NcFile. Tests 7-8 need
        // sample.nc + libnetcdf, else SKIP.
        Blade.Tests.NetcdfTests.runNetcdfTests ()
    | [ "zarr" ] ->
        // Zarr provider tests. Hermetic (fixtures generated on the fly); only
        // the e2e compile+run blocks need g++ and skip without it.
        Blade.Tests.ZarrTests.runZarrTests ()
    | [ "csv" ] ->
        // CSV provider tests. Fully hermetic; only the e2e compile+run blocks
        // need g++ and skip without it.
        Blade.Tests.CsvTests.runCsvTests ()
    | [ "hybrid" ] ->
        // Mixed-parallelism tests: order-table parse + gate-off degradation
        // run always; mpi+omp differentials need mpiexec and skip without it.
        Blade.Tests.HybridTests.runHybridTests ()
    | [ cat ] ->
        // Test a specific category: blade test basic, blade test loops, etc.
        // The two "-errors" corpora are ENTIRELY negative (every source is
        // meant to be refused) but their `// TEST:` names carry no "(rejects)"
        // marker for the runner to classify on -- mark them here.
        let asRejectProbes (tests: (string * string) list) =
            tests
            |> List.map (fun (name, source) ->
                (if name.EndsWith "(rejects)" then name else name + " (rejects)"), source)
        let categoryTests =
            match cat.ToLower().TrimStart('-') with
            | "basic" -> Some ("Basic", basicTests)
            | "intrinsics" -> Some ("Intrinsics", intrinsicsTests)
            | "ad" -> Some ("AD", adTests)
            | "loops" -> Some ("Loops", loopTests)
            | "symmetry" -> Some ("Symmetry", symmetryTests)
            | "reynolds" -> Some ("Reynolds", reynoldsTests)
            | "arity" -> Some ("Arity", arityTests)
            | "functions" -> Some ("Functions", functionTests)
            | "structs" -> Some ("Structs", structTests)
            | "struct-aborts" | "structaborts" -> Some ("Struct Aborts", structAbortTests)
            | "struct-mutual" | "mutual" -> Some ("Struct Mutual", structMutualTests)
            | "sumtypes" -> Some ("Sum Types", sumTypeTests)
            | "interfaces" -> Some ("Interfaces", interfaceTests)
            | "modules" -> Some ("Modules", moduleTests)
            | "guards" -> Some ("Guards", guardTests)
            | "bracketed" -> Some ("Bracketed", bracketedTests)
            // The `Tuple<N>` surface layer (docs/plan-tuples-vs-arg-packs.md
            // 6b). Mixed category: positives plus "(rejects)" probes, so no
            // asRejectProbes wrapper.
            | "tuples" -> Some ("Tuples", tupleTests)
            | "indextypes" -> Some ("Index Types", indexTypeTests)
            | "static" -> Some ("Static", staticTests)
            | "units" -> Some ("Units", unitTests)
            | "unit-errors" | "uniterrors" -> Some ("Unit Errors", asRejectProbes unitErrorTests)
            | "mutability" -> Some ("Mutability", mutabilityTests)
            | "mutability-errors" | "mutabilityerrors" -> Some ("Mutability Errors", asRejectProbes mutabilityErrorTests)
            | "funcarrays" | "fa" -> Some ("Func Arrays", funcArrayTests)
            | "ppl" -> Some ("PPL", pplTests)
            | "math" -> Some ("Math", mathTests)
            | "rand" -> Some ("Rand", randTests)
            | "display" -> Some ("Display", Blade.Tests.Display.displayTests)
            | "display-errors" | "displayerrors" ->
                Some ("Display Errors", asRejectProbes Blade.Tests.Display.displayErrorTests)
            | "spectra" -> Some ("Spectra", spectraTests)
            | "fallback" -> Some ("Fallback", fallbackTests)
            | "stack-join" | "stackjoin" -> Some ("Stack/Join", stackJoinTests)
            | "sgs" -> Some ("SGS", sgsTests)
            | "ml-ops" | "mlops" -> Some ("ML Ops", mlOpsTests)
            | "ml-e2e" | "mle2e" -> Some ("ML E2E", mlE2eTests)
            | "ml-equiv" | "mlequiv" | "equiv" -> Some ("ML Equiv", mlEquivTests)
            | "sqlish" | "sql" -> Some ("SQL-ish", foreignKeyTests @ maskTests @ setOpTests @ groupByTests @ sortTests @ reduceTests @ extentsTests @ extentsMultiRankTests @ regressionTests @ sqlCombinedTests)
            | "deferred-concrete" | "deferredconcrete" -> Some ("Deferred Concrete", Blade.Tests.RunAll.deferredConcreteTests)
            | "memfree" -> Some ("Mem Free", Blade.Tests.RunAll.memfreeTests)
            | "memfree-stress" | "memfreestress" -> Some ("Mem Free Stress", Blade.Tests.RunAll.memfreeStressTests)
            | _ -> None
        match categoryTests with
        | Some (name, tests) ->
            let r = runTestCategoryFull name tests "./generated_cpp_tests"
            if r.Failed = 0 then 0 else 1
        | None -> eprintfn "Unknown test category: %s" cat; 1
    | _ -> printUsage (); 1

/// Top-level command dispatch.
let private dispatchInner (args: string[]) : int =
    // Share the compiler version with the test-harness output helpers so every
    // block header reads "(vX.Y.Z)" consistently, including standalone runs.
    Blade.Tests.TestHarness.version <- compilerVersion
    // The REPL engine compiles long before the toolchain driver it needs for
    // its g++ fallback; hand it over once, here, so every front end that
    // reaches an unsupported node from this process gets the same lane.
    Blade.ReplSession.installCompiledLane compiledReplLane
    // `--strict-pins` is a build MODE, not a positional argument, and only
    // means anything for the four verbs that own a typecheck. Strip it from
    // the argv the verb patterns match on so every arm shape accepts it in
    // any position; left in place for every other verb (unknown argument there).
    let strictPinVerb =
        args.Length >= 1 &&
        (match args.[0] with
         | "check" | "compile" | "emit" | "run" -> true
         | _ -> false)
    let strictPins = strictPinVerb && Array.contains "--strict-pins" args
    let args = if strictPins then args |> Array.filter (fun a -> a <> "--strict-pins") else args
    // `--no-cache` is likewise a MODE, not a positional argument: it disables
    // the content-addressed executable cache for the verbs that own a g++
    // invocation. Like `--memcheck`, it travels as a process-level env pin
    // rather than a parameter, because the gate is read in Build.fs -- five
    // test blocks and the REPL reach that compile without passing through any
    // CLI-shaped option record. Stripped from argv so every verb pattern
    // accepts it in any position.
    let noCacheVerb =
        args.Length >= 1 &&
        (match args.[0] with
         | "compile" | "run" | "test" -> true
         | _ -> false)
    if noCacheVerb && Array.contains "--no-cache" args then
        System.Environment.SetEnvironmentVariable("BLADE_EXE_CACHE", "0")
    let args = if noCacheVerb then args |> Array.filter (fun a -> a <> "--no-cache") else args
    match args with
    // User-facing commands.
    // `run <file> [--verbose] [--mpi N] [--memcheck]` -- flags in any order
    // after the file.
    | _ when args.Length >= 2 && args.[0] = "run" ->
        let rest = args.[1..] |> Array.toList
        let mutable verbose = false
        let mutable mpiRanks = None
        let mutable file = None
        let mutable bad = None
        let rec parse toks =
            match toks with
            | [] -> ()
            | "--verbose" :: tl ->
                verbose <- true
                // Build.fs's executable cache reports `[cache] hit/store
                // <hash8>` on stderr under this pin (it has no verbose
                // parameter of its own; see exeCacheVerbose).
                System.Environment.SetEnvironmentVariable("BLADE_EXE_CACHE_VERBOSE", "1")
                parse tl
            | "--cuda" :: tl ->
                // Flip device-kernel emission for `where cuda` licences --
                // the same gate the CUDA test harness sets (setCudaEmitMode).
                // Downstream is untouched: emitted __global__ kernels make the
                // backend inference pick .cu + the nvcc split-compile path.
                // Off by default so a licence alone never changes the compile
                // toolchain out from under a plain `blade run`.
                CodeGen.setCudaEmitMode true
                parse tl
            | "--memcheck" :: tl ->
                // A process-level pin rather than a parameter: CodeGen (the
                // blade_memcheck.hpp include), Build (the Debug+ASan compile
                // profile and the longer run timeout) all read BLADE_MEMCHECK
                // at their own sites, and exporting the variable directly is
                // the equivalent harness spelling.
                System.Environment.SetEnvironmentVariable("BLADE_MEMCHECK", "1")
                parse tl
            | "--mpi" :: n :: tl ->
                (match System.Int32.TryParse n with
                 | true, v when v > 0 -> mpiRanks <- Some v; parse tl
                 | _ -> bad <- Some (sprintf "--mpi expects a positive rank count, got '%s'" n))
            | ["--mpi"] -> bad <- Some "--mpi requires a rank count (e.g. run prog.blade --mpi 4)"
            | f :: tl when file.IsNone && not (f.StartsWith "--") -> file <- Some f; parse tl
            | f :: _ -> bad <- Some (sprintf "unexpected argument '%s'" f)
        parse rest
        match bad, file with
        | Some msg, _ -> eprintfn "Error: %s" msg; 1
        | None, None -> printUsage (); 1
        | None, Some f -> runFile f verbose mpiRanks strictPins

    | [| "compile"; file |] ->
        match compileToExe file None false strictPins with
        | Ok path -> printfn "%s" path; 0
        | Error e -> eprintfn "%s" e; 1
    | [| "compile"; file; "-o"; output |] ->
        match compileToExe file (Some output) false strictPins with
        | Ok path -> printfn "%s" path; 0
        | Error e -> eprintfn "%s" e; 1

    | [| "emit"; file |] -> emitFile file None false strictPins
    | [| "emit"; file; "-o"; output |] -> emitFile file (Some output) false strictPins
    | [| "emit"; file; "--verbose" |] -> emitFile file None true strictPins
    | [| "emit"; file; "-o"; output; "--verbose" |] -> emitFile file (Some output) true strictPins

    | [| "check"; file |] -> checkFile file strictPins

    | [| "repl" |] -> replLoop ()

    // Editor tooling (JSON on stdout; see Ide.fs).
    | [| "ide"; "check"; "--json"; file |]
    | [| "ide"; "check"; file; "--json" |]
    | [| "ide"; "check"; file |] -> Blade.Ide.ideCheck file

    // The same payload, served: one long-lived process, NDJSON both ways.
    | [| "ide"; "serve" |] -> Blade.IdeServe.serve compilerVersion

    | _ when args.Length >= 1 && args.[0] = "test" ->
        dispatchTest (args.[1..] |> Array.toList)

    // Backward-compat flags.
    | [||] -> runFullSuite defaultFullSuiteOptions
    | [| "--full" |] -> runFullSuite defaultFullSuiteOptions
    | [| "--help" |] -> printUsage (); 0
    | _ -> printUsage (); 1

/// Top-level error boundary: turns any escaping exception into a rendered
/// diagnostic on stderr (exit 1) instead of a raw .NET stack trace. A typed
/// BladeDiagnosticException renders as itself; any other exception becomes a
/// BL9001 internal compiler error (.NET stack shown only under --verbose).
/// Existing eprintfn error paths inside dispatchInner are untouched.
let dispatch (args: string[]) : int =
    let verbose = args |> Array.contains "--verbose"
    try
        dispatchInner args
    with
    | Blade.Diagnostics.BladeDiagnosticException d ->
        let useColor = not System.Console.IsErrorRedirected
        eprintfn "%s" (Blade.Diagnostics.Render.render useColor None d)
        1
    | ex ->
        let d = Blade.Diagnostics.Codes.ice ex.Message
        let useColor = not System.Console.IsErrorRedirected
        eprintfn "%s" (Blade.Diagnostics.Render.render useColor None d)
        if verbose then eprintfn "%s" (ex.ToString())
        1
