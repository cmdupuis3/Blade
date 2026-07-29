// Command-line interface: argument parsing and command dispatch, plus the
// user-facing compile/run/check/emit commands. Extracted from Main.fs
// (audit Â§2.3) â€” Main.fs is now the entry point only.
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
    printfn "  check <file.edgi>                 Type-check only (no code generation)"
    printfn "  ide check --json <file.edgi>      Type-check and emit JSON diagnostics + binding types"
    printfn "                                    (machine-readable, for editor tooling)"
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
    printfn "  test cuda                         Run the CUDA kernel block standalone"
    printfn "  test mpi                          Run the MPI decomposition block standalone"
    printfn "  test netcdf                       Run the NetCDF provider block (needs libnetcdf + sample.nc)"
    printfn "  test zarr                         Run the Zarr provider block (hermetic; g++ for the e2e parts)"
    printfn "  test timing                       Run the differential timing block standalone"
    printfn "  test strict-pins                  Run the --strict-pins CLI gate block standalone"
    printfn "  test surfacing                    Run the warning-surfacing block standalone"
    printfn "  test diff-oracle [category]       Diff printed values against the pinned ./oracle build"
    printfn "  test interp [category]            Diff the tree-walking interpreter against the compiled binary"
    printfn ""
    printfn "Options:"
    printfn "  -o <path>      Output file path"
    printfn "  --verbose      Show IR and generated C++"
    printfn "  --strict-pins  Fail the build on unpinned confirm-and-pin deductions"
    printfn "                 (BL4010, normally warnings). For CI: forces the pin"
    printfn "                 decision into source. check / compile / emit / run."
    printfn "  --help         Show this help"
    printfn ""
    printfn "Examples:"
    printfn "  blade run myprogram.edgi"
    printfn "  blade emit myprogram.edgi -o myprogram.cpp"
    printfn "  blade compile myprogram.edgi -o myprogram"
    printfn "  blade check myprogram.edgi --strict-pins"
    printfn "  blade test"
    printfn "  blade test --omp --cuda --timing"

/// Â§6.1(b) strict mode. The stage-3/4 confirm-and-pin SUGGESTIONS (BL4010) are
/// warnings by default: the deduction proposes, storage stays DENSE, and
/// nothing changes until the user pins the annotation in source.
/// `--strict-pins` promotes every outstanding suggestion to a build ERROR, so
/// a CI build fails on an unpinned deduction that would change storage and the
/// pin decision has to be committed in source (the annotation is the durable
/// artifact â€” plan Â§6.1 option (b)).
///
/// Reads the `PinSuggestions` side-channel `typeCheck` fills â€” the structured
/// (message, kernel span) twin of the plain-string warnings â€” and renders each
/// through the ordinary diagnostic renderer, so a strict failure looks exactly
/// like any other `error[BL4010]:` with its source snippet. Returns None when
/// strict mode is off or nothing is outstanding. Deduplicated and in
/// deduction order (Â§6.6 determinism).
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
        let source = File.ReadAllText(filePath)
        let testName = Path.GetFileNameWithoutExtension(filePath)
        // Errors come back as coded, spanned Diagnostics and are rendered
        // here (rustc-style, with source snippets) into the string channel.
        let useColor = not Console.IsErrorRedirected
        match lowerDiag (Some filePath) source with
        | Error ds, sm ->
            // S1: a file that also has a hard error has still EARNED every
            // warning the checker produced before it failed. They rode
            // typeCheck's Ok-only payload before and were dropped here.
            printTypeCheckWarnings useColor (Some sm) false
            Error (Blade.Diagnostics.Render.renderAll useColor (Some sm) ds)
        | Ok (ir, _), sm ->
            // Strict mode fails here, before codegen: the pin suggestions
            // REPLACE their warning twins (which are therefore not printed).
            match strictPinFailure strictPins useColor (Some sm) with
            | Some rendered -> Error rendered
            | None ->
            printTypeCheckWarnings useColor (Some sm) false
            match IR.validateIR ir with
            | Error errs ->
                let ds =
                    errs |> List.map (fun s ->
                        Blade.Diagnostics.mkError "BL6001" Blade.Diagnostics.PhIRValidate Blade.Ast.noSpan s)
                Error (Blade.Diagnostics.Render.renderAll useColor (Some sm) ds)
            | Ok ir ->
                let (cppCode, warnings) = CodeGen.genSelfContainedProgramFromIR ir testName
                if verbose then
                    for w in warnings do
                        eprintfn "[Warning] %s" w
                Ok (cppCode, warnings)

/// Compile a .edgi file to an executable
let compileToExe (filePath: string) (outputPath: string option) (verbose: bool) (strictPins: bool) : Result<string, string> =
    match compileFile filePath verbose strictPins with
    | Error e -> Error e
    | Ok (cppCode, warnings) ->
        let baseName = Path.GetFileNameWithoutExtension(filePath)
        let dir = Path.GetDirectoryName(Path.GetFullPath(filePath))
        let dir = if String.IsNullOrEmpty dir then "." else dir
        // Infer backend from generated source: device kernels â†’ .cu + nvcc.
        let backendReq = inferBackendReq cppCode
        let ext = match backendReq with RequiresCuda -> ".cu" | RequiresMpi | CpuOnly -> ".cpp"
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

/// Run a .edgi file: compile and execute. `mpiRanks = Some n` switches on the
/// MPI emit gate for codegen (decomposed kernels + Init/Finalize + rank-0
/// printing), links -lmsmpi (via the mpi.h detection in compileCpp), and
/// launches under `mpiexec -n n`. None = the historical serial path (any
/// `where mpi` clause stays inert).
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

// ----------------------------------------------------------------------------
// Interactive REPL (`blade repl`)
// ----------------------------------------------------------------------------
//
// Blade has no interpreter, but `blade run` semantics give REPL behavior for
// free: every top-level binding prints its value. The REPL accumulates a
// session program in a temp file; each submitted snippet re-compiles and
// re-runs the WHOLE session, but echoes ONLY the value of the snippet's LAST
// top-level binding â€” its "return value", as in a function body. Earlier
// bindings, and the many synthetic `__`-internal bindings that a single
// `ppl.dist`/module call expands into, stay hidden. Rebinding a top-level name
// replaces the earlier definition (duplicate lets are a C++ redeclaration
// error) so downstream snippets still see it; the echo then shows that
// snippet's own last value, recomputed.
//
// A snippet that is not a declaration is a bare EXPRESSION (`a`, `a + 1`) â€”
// the file-level "return a value by naming it" idiom. Top-level source only
// admits declarations, so the REPL wraps the expression in a transient
// binding (`let it = <expr>`), runs, echoes the value, and discards it: the
// session and the diff baseline stay untouched, so repeating the expression
// echoes again instead of diffing to silence. A bare identifier naming a
// session FUNCTION echoes its signature from the typechecker alone (functions
// aren't let-bindable just to print them).
//
// Output lines are type-annotated by an in-process parse+typecheck(+lower,
// for HM-monomorphized value types) of the same source â€” see ReplTypes.
//
// The compiled session runs with the REPL process's own working directory,
// so relative data paths (NetCDF.load("sample.nc")) resolve where the user
// launched the REPL â€” not in the session temp dir.

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

// ----------------------------------------------------------------------------
// REPL display: type-annotated echoes
// ----------------------------------------------------------------------------
//
// The compiled session prints raw `name = value` lines. The REPL joins those
// with an in-process parse+typecheck of the SAME source (the front half of
// `lower`; cheap next to the g++ invocation that just ran) to display types:
//
//   - primitives inline:                  a = Int64: 5
//   - all other types (arrays, tuples, functions) on the next line, tabbed:
//         v = [1, 2, 3]
//             Array<Int64, Idx<3>>
//   - function definitions echo their signature. Abstract (type-variable)
//     positions render with their source names (`T`, `T^2`); positions
//     inference bound to a concrete type render that concrete type
//     substituted into the same syntax.
module ReplTypes =
    open System.Collections.Generic
    open System.Text.RegularExpressions
    open Blade.Ast
    open Blade.Types
    open Blade.IR
    open Blade.TypedAst

    /// What the REPL knows about one top-level name.
    type Info =
        | RVal of IRType
        | RFunc of signature: string

    /// Render a function signature: `(Int64, T) -> T`. Concrete positions
    /// print concretely; abstract positions print their source type-variable
    /// names (fresh letters for inference-invented ones). The abstract-var
    /// recovery and naming live in Blade.Ide (shared with `ide check`'s
    /// hover types); this wraps them in the REPL's single-line format.
    let funcSig (src: FunctionDecl option) (tf: TypedFunctionDecl) : string =
        let seed =
            match src with
            | Some f when f.Params.Length = tf.Params.Length ->
                [ for (p, tp) in List.zip f.Params tf.Params do
                    match p.Type with
                    | Some ann -> yield! Blade.Ide.collectVarNames ann tp.Type
                    | None -> ()
                  match f.ReturnType with
                  | Some ann -> yield! Blade.Ide.collectVarNames ann tf.ReturnType
                  | None -> () ]
            | _ -> []
        let pp = Blade.Ide.abstractRenderer seed
        let ps = tf.Params |> List.map (fun p -> pp p.Type)
        sprintf "(%s) -> %s" (String.concat ", " ps) (pp tf.ReturnType)

    /// Build the top-level name -> display info map from an ALREADY-lowered
    /// session (Blade.Interp.Repl.LoweredSession) â€” the SAME front-end pass the
    /// interpreter runs on in compileRunEcho, so the candidate path never lowers
    /// twice. Value bindings prefer the LOWERED types: calls to HM-polymorphic
    /// functions monomorphize during lowering, so the typed AST can still carry
    /// T?n inference vars where the IR is concrete (`let r = id(3.5)` is Float64
    /// only in IR). Pure map assembly â€” no parse/typecheck/lower here.
    let sessionInfoOf (lowered: Blade.Interp.Repl.LoweredSession) : Map<string, Info> =
        let prog = lowered.Prog
        let tp = lowered.Typed
        let srcFuncs =
            [ for m in prog.Modules do
                for ld in m.Decls do
                    match ld.Value with
                    | DeclFunction f -> yield (f.Name, f)
                    | _ -> () ]
            |> Map.ofList
        let irTypes =
            Map.ofList
                [ for m in lowered.Ir.Modules do
                    for b in m.Bindings do
                        yield (b.Name, b.Type) ]
        let valTy (name: string) (fallback: IRType) =
            match Map.tryFind name irTypes with
            | Some t -> t
            | None -> fallback
        let mutable acc = Map.empty
        for m in tp.Modules do
            for d in m.Decls do
                match d with
                | TDeclLet b | TDeclStatic b ->
                    acc <- Map.add b.Name (RVal (valTy b.Name b.Type)) acc
                    for (n, _, t) in b.SubBindings do
                        acc <- Map.add n (RVal (valTy n t)) acc
                | TDeclFunction f ->
                    acc <- Map.add f.Name
                               (RFunc (funcSig (Map.tryFind f.Name srcFuncs) f)) acc
                | _ -> ()
        acc

    /// Parse + typecheck + lower session source (one pass) and return top-level
    /// name -> display info. Failures yield an empty map â€” values still print,
    /// just unannotated (shouldn't happen for source that just compiled
    /// successfully). Used for the bare-identifier "is this a session function?"
    /// probe on the CURRENT session; the candidate path reuses the interpreter's
    /// own LoweredSession via sessionInfoOf, so it never lowers twice.
    let sessionInfo (source: string) : Map<string, Info> =
        try
            match Blade.Interp.Repl.lowerSession None false source with
            | Error _ -> Map.empty
            | Ok lowered -> sessionInfoOf lowered
        with _ -> Map.empty

    /// Primitive = annotate inline ("Int64: 5"); everything else goes on the
    /// next line, tabbed.
    let rec isPrimitive (t: IRType) : bool =
        match t with
        | IRTScalar _ | IRTNat _ -> true
        | IRTIdxTagged (inner, _) | IRTUnitAnnotated (inner, _) -> isPrimitive inner
        | _ -> false

    let private eqLineRe = Regex(@"^([A-Za-z_][A-Za-z0-9_]*) = (.*)$", RegexOptions.Compiled)

    /// Rewrite one raw output line for display. `transient` is the synthetic
    /// binding a bare REPL expression was wrapped in â€” its name is stripped
    /// so the value echoes alone.
    let annotate (info: Map<string, Info>) (transient: string option) (line: string) : string =
        let m = eqLineRe.Match line
        if not m.Success then line
        else
            let name = m.Groups.[1].Value
            let value = m.Groups.[2].Value
            let isTransient = (transient = Some name)
            match Map.tryFind name info with
            | Some (RVal t) ->
                let tyStr = Blade.Ide.abstractRenderer [] t
                if isPrimitive t then
                    if isTransient then sprintf "%s: %s" tyStr value
                    else sprintf "%s = %s: %s" name tyStr value
                else
                    if isTransient then sprintf "%s\n\t%s" value tyStr
                    else sprintf "%s = %s\n\t%s" name value tyStr
            | Some (RFunc _) -> line
            | None -> if isTransient then value else line

let replLoop () : int =
    printfn "Blade REPL (v%s) â€” each submission echoes its last binding's (typed) value." compilerVersion
    printfn "A bare expression (e.g. `a`, `a + 1`) evaluates and echoes without joining the session."
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
    // value changes every run â€” exclude it from the output diff.
    let isTimingLine (l: string) =
        System.Text.RegularExpressions.Regex.IsMatch(l, @"completed in [0-9.eE+~-]+m?s\s*$")

    // A snippet is a declaration iff it opens with a declaration keyword;
    // anything else is a bare expression to evaluate and echo.
    let declRe =
        System.Text.RegularExpressions.Regex(
            @"^\s*(let|static|function|type|struct|interface|impl|unit|import|from|module)\b")
    let identRe =
        System.Text.RegularExpressions.Regex(@"^[A-Za-z_][A-Za-z0-9_]*$")

    // A reassignment `x = e` (or `x[i] = e`, `x.f = e`, `x += e`, â€¦): an lvalue
    // followed by an assignment operator. `=(?!=)` matches a single `=` but not
    // `==`; `<=`/`>=`/`!=` never match here because their `<`/`>`/`!` is not part
    // of the lvalue â€” so `b == 1`, `b <= 1`, `b != 1` stay bare expressions. The
    // leading identifier (group 1) is the ROOT variable to echo. Checked AFTER
    // declRe so `let â€¦` stays a declaration.
    let assignRe =
        System.Text.RegularExpressions.Regex(
            @"^\s*([A-Za-z_][A-Za-z0-9_]*)(?:\.[A-Za-z_][A-Za-z0-9_]*|\[[^\]]*\])*\s*(?:\+=|-=|\*=|/=|=(?!=))")

    // A raw run-output line is `name = value`; grab the leading name so we can
    // single out just the one binding we mean to echo.
    let outNameRe =
        System.Text.RegularExpressions.Regex(
            @"^([A-Za-z_][A-Za-z0-9_]*) = ",
            System.Text.RegularExpressions.RegexOptions.Compiled)

    /// Evaluate `candidate` and echo ONLY `targetName`'s value line â€” the
    /// submission's "return value" â€” type-annotated. Every earlier user binding,
    /// and every synthetic `__`-internal binding (a single `ppl.dist` expands
    /// into dozens), stays hidden.
    ///
    /// INTERP-FIRST (the payoff of the interpreter arc): the candidate lowers
    /// ONCE (Repl.lowerSession â€” shared with the type-annotation map below), then
    /// runs under the tree-walking interpreter. On a supported exit (0, or a
    /// Blade guard panic 1) its output is authoritative and NO g++ is invoked â€”
    /// a typical turn drops from ~1-5 s to <100 ms. If the interpreter cannot yet
    /// evaluate some node (125) or hits its own bug (70) it FALLS BACK to the
    /// historical g++ compile+run for this one input (a single notice on stderr),
    /// with identical filtering/annotation. A front-end/validate rejection is
    /// surfaced exactly as the old compileToExe Error arm did.
    ///
    /// `transient` is the synthetic name a bare expression was wrapped in (its
    /// prefix is stripped in display), else None. Returns Some (lines,
    /// printedCount, info) on a clean exit â€” info is the SAME LoweredSession's
    /// annotation map, so the caller reuses it without lowering again â€” or None
    /// (snippet must not be kept).
    let compileRunEcho (candidate: ResizeArray<string>) (targetName: string option) (transient: string option)
        : (string[] * int * Map<string, ReplTypes.Info>) option =
        let src = String.concat "\n\n" candidate + "\n"
        File.WriteAllText(srcPath, src)
        let useColor = not Console.IsErrorRedirected
        match Blade.Interp.Repl.lowerSession (Some srcPath) useColor src with
        | Error rendered ->
            // Front-end / validate rejection â€” identical to the old
            // compileToExe Error arm (both render the same diagnostics).
            eprintfn "%s" rendered
            eprintfn "[snippet not kept]"
            None
        | Ok lowered ->
            let info = ReplTypes.sessionInfoOf lowered
            let display l = ReplTypes.annotate info transient l
            // Given a process-like (code, stdout, stderr) triple from EITHER the
            // interpreter or the compiled fallback, filter to targetName and
            // echo â€” this is the historical tail of compileRunEcho, unchanged.
            let emit (code: int) (stdout: string) (stderr: string) =
                let lines =
                    stdout.Replace("\r\n", "\n").Split('\n')
                    |> Array.filter (fun l -> not (isTimingLine l))
                let mutable printed = 0
                match targetName with
                | Some tgt ->
                    lines
                    |> Array.tryFind (fun l ->
                        let m = outNameRe.Match l
                        m.Success && m.Groups.[1].Value = tgt)
                    |> Option.iter (fun l -> printfn "%s" (display l); printed <- 1)
                | None -> ()
                if stderr.Trim() <> "" then eprintfn "%s" (stderr.Trim())
                if code = 0 then Some (lines, printed, info)
                else
                    eprintfn "[exit %d â€” snippet not kept]" code
                    None
            // Historical g++ compile+run for this ONE input (the fallback lane).
            let viaCompiled () =
                match compileToExe srcPath None false false with
                | Error e ->
                    eprintfn "%s" e
                    eprintfn "[snippet not kept]"
                    None
                | Ok exePath ->
                    match runExeIn userCwd exePath with
                    | Error e ->
                        eprintfn "Runtime error: %s" e
                        eprintfn "[snippet not kept]"
                        None
                    | Ok (code, stdout, stderr) -> emit code stdout stderr
            match Blade.Interp.Repl.evalSession lowered "session" with
            | Blade.Interp.Repl.InterpDone r ->
                // Interpreter is authoritative (exit 0 or guard panic 1). Surface
                // the same TypeCheck warnings compileFile prints on the g++ path.
                printTypeCheckWarnings (not Console.IsErrorRedirected) None false
                emit r.ExitCode r.Stdout r.Stderr
            | Blade.Interp.Repl.InterpFellShort _ ->
                // The interpreter can't evaluate this input yet â€” one-time notice
                // so the user understands the latency spike, then the g++ path
                // (whose stdout the SAME targetName filter isolates â€” no
                // suppression regression). Warnings print via compileFile there.
                eprintfn "-- falling back to compiled evaluation for this input --"
                viaCompiled ()

    // Classification looks at the first non-comment, non-blank line so a
    // doc-commented declaration isn't mistaken for a bare expression.
    let classifyTarget (s: string) =
        s.Replace("\r\n", "\n").Split('\n')
        |> Array.tryFind (fun l ->
            let t = l.TrimStart()
            t <> "" && not (t.StartsWith "//"))
        |> Option.defaultValue ""

    let evaluate (snippet: string) =
        let trimmed = snippet.Trim()
        if trimmed = "" then () else
        if declRe.IsMatch (classifyTarget trimmed) then
            // Declaration: rebinding replaces the earlier definition IN PLACE
            // so snippets that referenced the name (defined later in the
            // session) still see it; the output diff then shows their
            // recomputed values.
            let candidate = ResizeArray(session)
            match bindingName trimmed with
            | Some name ->
                let idx = candidate.FindIndex(fun s -> bindingName s = Some name)
                if idx >= 0 then candidate.[idx] <- trimmed else candidate.Add trimmed
            | None -> candidate.Add trimmed
            // The submission's "return value" is its LAST top-level binding
            // (a :paste block may declare several); echo only that one.
            let lastTarget =
                trimmed.Replace("\r\n", "\n").Split('\n')
                |> Array.choose bindingName
                |> Array.tryLast
            match compileRunEcho candidate lastTarget None with
            | None -> ()
            | Some (lines, printed, info) ->
                let mutable printed = printed
                // A final function/type binding produces no run output â€” echo
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
                session.Clear()
                session.AddRange candidate
                lastLines <- lines
        elif assignRe.IsMatch (classifyTarget trimmed) then
            // Reassignment (`b = b + 1`, `b += 1`, â€¦): a bare assignment does not
            // parse at top level, but wrapping it in a hidden binding does â€” the
            // wrapper's value IS the ExprAssign, which mutates the target's
            // existing cell. Unlike the bare-expression path we KEEP the wrapper,
            // so the mutation persists into the re-run session. Successive
            // assignments append (never rebind-replace) under fresh __assignN
            // names, so `b = b + 1` twice accumulates 1->2->3. The echo targets
            // the ROOT variable, whose end-of-program auto-print already reflects
            // the final value (both backends print bindings after the whole body
            // runs), so no re-read is needed.
            let candidate = ResizeArray(session)
            let hidden =
                let inUse = candidate |> Seq.choose bindingName |> Set.ofSeq
                Seq.initInfinite (fun i -> if i = 0 then "__assign" else sprintf "__assign%d" i)
                |> Seq.find (fun n -> not (Set.contains n inUse))
            candidate.Add (sprintf "let %s = %s" hidden trimmed)
            let root = (assignRe.Match trimmed).Groups.[1].Value
            match compileRunEcho candidate (Some root) None with
            | None -> ()                                    // static/unknown/etc â†’ not kept
            | Some (lines, printed, _) ->
                if printed = 0 then printfn "(ok)"          // e.g. array reassign isn't auto-printed
                session.Clear()
                session.AddRange candidate
                lastLines <- lines
        else
            // Bare expression: `blade run` semantics only print top-level
            // BINDINGS, so wrap the expression in a transient one, run, and
            // echo its value â€” WITHOUT keeping it. The session (and diff
            // baseline) stay untouched, so re-entering the same expression
            // echoes again rather than diffing to silence.
            let curInfo = lazy (ReplTypes.sessionInfo (String.concat "\n\n" session + "\n"))
            let asFuncName =
                if identRe.IsMatch trimmed then
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
                let transient =
                    let inUse = session |> Seq.choose bindingName |> Set.ofSeq
                    Seq.initInfinite (fun i -> if i = 0 then "it" else sprintf "it%d" i)
                    |> Seq.find (fun n -> not (Set.contains n inUse))
                let candidate = ResizeArray(session)
                candidate.Add (sprintf "let %s = %s" transient trimmed)
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
            session.Clear()
            lastLines <- [||]
            printfn "(session cleared)"
        | line when buffer.Count = 0 && line.Trim() = ":show" ->
            // Hide the synthetic `let __assignâ€¦ = <reassignment>` wrappers the
            // assignment path appends â€” the user sees only what they typed.
            let visible =
                session
                |> Seq.filter (fun s -> bindingName s |> Option.forall (fun n -> not (n.StartsWith "__assign")))
                |> List.ofSeq
            if List.isEmpty visible then printfn "(empty session)"
            else printfn "%s" (String.concat "\n\n" visible)
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
/// so they cannot catch a compileToExe that forgets to â€” historically
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
let checkFile (filePath: string) (strictPins: bool) : int =
    if not (File.Exists filePath) then
        eprintfn "File not found: %s" filePath
        1
    else
        let source = File.ReadAllText(filePath)
        let useColor = not Console.IsErrorRedirected
        let sm = Blade.Diagnostics.SourceMap.ofSources [ filePath, source ]
        match Blade.Parser.parseProgramWithFile (Some filePath) source with
        | Error e ->
            eprintfn "%s" (Blade.Diagnostics.Render.render useColor (Some sm)
                               (Blade.Parser.diagnosticOfParseError (Some filePath) e))
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
                    // Strict mode (Â§6.1(b)): the pin suggestions ARE the
                    // failure. Their warning twins are dropped (same dedup rule
                    // as `blade ide check`), so each suggestion is reported
                    // exactly once, as an error. The old hand-rolled
                    // message-text Set is gone: the twins are BL4010 BY
                    // CONSTRUCTION — same code, same span, same text — so
                    // filtering on the code is the exact same filter, minus the
                    // string comparison.
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
            // Ship the runtime headers next to the emitted .cpp so it compiles
            // as-is (`g++ file.cpp`, no -I flag) â€” its `#include`s use plain
            // quotes and resolve relative to the source.
            let outDir = Path.GetDirectoryName(Path.GetFullPath(outPath))
            CodeGen.deployRuntimeHeaders (if String.IsNullOrEmpty outDir then "." else outDir)
            if verbose then
                eprintfn "[Emit] %s" outPath
            0
        | None ->
            printf "%s" cppCode
            0

/// `--strict-pins` (Â§6.1(b)) regression block. The corpus harness runs .blade
/// files with fixed compiler options, so a FLAG's behavior cannot be expressed
/// as a corpus entry; this block drives the two CLI surfaces that own the gate
/// (checkFile, and compileFile â€” the lane compile/emit/run all funnel through)
/// against a temp file, in-process. No C++ toolchain involved: the gate fires
/// at typecheck, before codegen.
///
/// The two programs are the corpus twins functions/026 (unpinned â†’ suggestion)
/// and functions/029 (the suggested pin applied); kept inline so the flag test
/// stays readable and independent of corpus renumbering. Console output is
/// captured so a passing run stays quiet â€” and so the rendered text can be
/// asserted on (substring "BL4010", which survives ANSI coloring).
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
/// the load-bearing assertions drive `ide check --json` and the two console
/// streams, neither of which any corpus harness touches (the diagnostics corpus
/// calls `lowerDiag` directly and never renders; the value corpus compares
/// program OUTPUT, and a warning changes no value).
///
/// The regression this locks: warnings and pin suggestions used to ride
/// `typeCheck`'s Ok-only `string list`, so a file with ANY hard error silently
/// lost every nudge the checker had already earned — on the CLI (S1) and in the
/// editor JSON (S2). That is precisely the file an editor is looking at while
/// you type.
let private runSurfacingTests () : TH.BlockResult =
    let blockName = "Surfacing"
    TH.printHeader "Warning Surfacing (codes, streams, and survival of the error path)"
    let results = ResizeArray<string * TH.Outcome>()
    let record name outcome detail =
        TH.resultLine outcome name detail
        results.Add((name, outcome))
    // The strict-pins `unpinned` twin (earns a BL4010 storage suggestion)...
    let unpinned =
        "function mymean(row) = reduce(row, (+)) / extents(row)\n\
         function covariance(a, b) = mymean((a - mymean(a)) * (b - mymean(b)))\n\
         let data = [[1.0, 2.0, 3.0], [2.0, 4.0, 6.0]]\n\
         let result = object_for(covariance) <@> (data, data) |> compute\n"
    // ...plus an unrelated hard type error in a LATER declaration, so the
    // checker records the suggestion and THEN fails. Order matters: the
    // suggestion must already be on the channel when the error aborts.
    let errPlusWarn = unpinned + "let boom = nosuchthing + 1.0\n"
    let tmpDir = Path.Combine(Path.GetTempPath(), "blade_surfacing_" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmpDir) |> ignore
    /// Run `f` with stdout and stderr captured SEPARATELY — strict-pins' own
    /// `quietly` merges them into one writer, which cannot see which stream a
    /// line went to, and "warnings go to stderr so stdout stays pipeable" is
    /// exactly a claim about the split.
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

        // --- 1. ide check --json, ERROR path: the suggestion survives (S2).
        let (code, out, _) = quietly2 (fun () -> Blade.Ide.ideCheck errPath)
        let name = "ide check --json: BL4010 survives a file with a hard error"
        if code = 1 && out.Contains "\"severity\":\"error\"" && out.Contains "\"code\":\"BL4010\"" then
            record name TH.Pass ""
        else
            record name TH.Fail
                   (sprintf "exit %d, json: %s" code (out.Trim()))

        // --- 2. ...and so do the deduced facts (channel (f)) on that arm.
        let name = "ide check --json: deduced[] is populated on the error arm"
        if out.Contains "\"deduced\":[" && out.Contains "\"kind\":\"comm\"" then
            record name TH.Pass ""
        else
            record name TH.Fail (sprintf "json: %s" (out.Trim()))

        // --- 3. Control: the pinned twin is clean and claims nothing.
        let (code, out, _) = quietly2 (fun () -> Blade.Ide.ideCheck pinnedPath)
        let name = "ide check --json: the pinned twin yields no BL4010 (exit 0)"
        if code = 0 && not (out.Contains "BL4010") then
            record name TH.Pass ""
        else
            record name TH.Fail (sprintf "exit %d, json: %s" code (out.Trim()))

        // --- 4. `check`: warnings are rendered diagnostics, and they are on
        // STDERR. Both halves matter — the code proves the render, the empty
        // stdout proves `blade check` stays pipeable.
        let (code, out, err) = quietly2 (fun () -> checkFile unpinnedPath false)
        let name = "check: the warning renders as warning[BL4010] on stderr, not stdout"
        if code = 0 && err.Contains "warning[BL4010]" && not (out.Contains "BL4010")
           && out.Contains "OK" then
            record name TH.Pass ""
        else
            record name TH.Fail
                   (sprintf "exit %d, stdout: %s, stderr: %s" code (out.Trim()) (err.Trim()))

        // --- 5. `check` on the erroring file still prints the warning (S1).
        let (code, _, err) = quietly2 (fun () -> checkFile errPath false)
        let name = "check: warnings print alongside the error instead of vanishing"
        if code = 1 && err.Contains "warning[BL4010]" && err.Contains "error[BL2001]" then
            record name TH.Pass ""
        else
            record name TH.Fail (sprintf "exit %d, stderr: %s" code (err.Trim()))

        // --- 6. The compile lane agrees (compile/emit/run all funnel here).
        let ((result: Result<string * string list, string>), _, err) =
            quietly2 (fun () -> compileFile errPath false false)
        let name = "compile lane: warnings print on the error arm too"
        match result with
        | Error _ when err.Contains "warning[BL4010]" -> record name TH.Pass ""
        | Error _ -> record name TH.Fail (sprintf "no warning on stderr: %s" (err.Trim()))
        | Ok _ -> record name TH.Fail "compiled instead of failing"

        // --- 7-9. The stage-6a CERTIFICATE channels (BL4011's galilean twin
        // BL4014, and the structured CertFacts feed behind `deduced[]`).
        //
        // These three drive the DRAIN, not the producer: each stages a channel
        // entry by hand and asserts the surfacing code carries it to the right
        // place, then resets. The inference passes that fill these channels for
        // real live in the ML elaborator, which resets them on entry — so an
        // end-to-end "write a boost-invariant function, check it" assertion
        // cannot be written here; that direction is covered by the ml-equiv
        // corpus SUGGEST pins. What is genuinely at risk in THIS file is a
        // channel that gets filled and then read by nobody, and that is exactly
        // what these catch.
        let testSpan : Blade.Ast.Span =
            { StartLine = 2; StartCol = 1; EndLine = 2; EndCol = 9; File = None }

        // --- 7. The code renders. Channel-independent: the diagnostic is built
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

        // --- 8. GalCertSuggestions reaches the shared warning-diagnostic
        // assembly (the one every CLI lane prints through), and does so under
        // `skipPins` too: a certificate owns no storage decision, so
        // --strict-pins must not swallow it the way it swallows BL4010.
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

        // --- 9. CertFacts reaches `deduced[]` as STRUCTURED data, through the
        // real mapping and the real renderer. Both disciplines, because they
        // take the same renderer arm and a typo in either kind string would
        // silently drop `name` (the group) into the pair-fields branch.
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

/// Run the full suite, appending the CLI smoke block and the strict-pin block
/// (which live in this file â€” see runAllTestsFullWith's doc comment for why
/// they're passed in).
let private runFullSuite opts =
    runAllTestsFullWith [runCliSmokeTests; runStrictPinTests; runSurfacingTests] opts

/// Dispatch the `test` subcommand. `rest` is everything after "test".
let private dispatchTest (rest: string list) : int =
    // `--omp` / `--cuda` / `--timing` / `--mpi` / `--interp` / `--diff-oracle`
    // opt the corresponding blocks into the full suite; they may appear in any
    // order and combine. Each has a standalone arm below too; the flag form
    // exists so an opt-in block still lands in the grand total.
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
        // The --strict-pins CLI gate (Â§6.1(b)) standalone. In-process, no
        // toolchain; also part of the full suite.
        let failed = (runStrictPinTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "surfacing" ] ->
        // Warning/suggestion surfacing: codes, streams, and survival of the
        // checker's error path. In-process, no toolchain; also in the full suite.
        let failed = (runSurfacingTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "normalize" ] ->
        // IR-level F# unit tests for the type normalizer. Runs in-process,
        // no Blade source pipeline involved.
        let failed = (runNormalizeTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "unify" ] ->
        // TypeCheck-level F# unit tests for the unify Â§5.3 fast path.
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
        // F# unit tests for the canonical ExprShape traversal (Â§3.2):
        // childrenOf/rebuildWith round-trips, mapIRExpr identity, and
        // collectVarRefsIR completeness. No Blade source pipeline.
        let failed = (Blade.Tests.Shape.runShapeTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "diff-oracle" ] ->
        // Phase 4 differential gate: this binary vs the pinned ./oracle build
        // over the dense corpus slice â€” identical printed VALUES required.
        let failed = (Blade.Tests.DiffOracle.runDiffOracleTests "./oracle/Blade.exe" Blade.Tests.DiffOracle.denseSlice).Failed
        if failed = 0 then 0 else 1
    | [ "diff-oracle"; cat ] ->
        // Single corpus category against the pinned oracle.
        let failed = (Blade.Tests.DiffOracle.runDiffOracleTests "./oracle/Blade.exe" [cat]).Failed
        if failed = 0 then 0 else 1
    | [ "interp" ] ->
        // Interpreter differential gate: the tree-walking IR interpreter vs the
        // compiled binary over the supported corpus slice â€” byte-identical
        // normalized stdout required. Slice grows per interpreter milestone.
        let failed = (Blade.Tests.InterpDiff.runInterpDiffTests Blade.Tests.InterpDiff.currentSlice).Failed
        if failed = 0 then 0 else 1
    | [ "interp"; cat ] ->
        // Single corpus category through the interpreter differential gate.
        let failed = (Blade.Tests.InterpDiff.runInterpDiffTests [cat]).Failed
        if failed = 0 then 0 else 1
    | [ "spans" ] ->
        // Error-location tests (Â§3.4 / Phase 2 gate): deliberately broken
        // sources, asserting the reported line. No C++ pipeline.
        let failed = (Blade.Tests.Spans.runSpanTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "diagnostics" ] ->
        // Diagnostics core (renderer + registry) and the diagnostics corpus
        // (broken sources with pinned codes/spans). No C++ pipeline.
        let core = (Blade.Tests.DiagnosticsCore.runDiagnosticsCoreTests ()).Failed
        let corpus = (Blade.Tests.DiagCorpus.runDiagCorpusTests ()).Failed
        // Stage-6a BL4011 suggestions: pinned (and pinned-ABSENT) over the
        // ml-equiv corpus. A warning channel, so it has no home in the value
        // corpus; it rides here with the other coded-diagnostic assertions.
        let certSuggest = (Blade.Tests.DiagCorpus.runCertSuggestTests ()).Failed
        if core + corpus + certSuggest = 0 then 0 else 1
    | [ "rep-differential" ] | [ "repdifferential" ] ->
        // Phase-B3 deduction parity gate (plan-equivariance-in-types.md): the
        // typed rep-status deduction vs the stage-6a seam inference, proposal
        // by proposal over the ml-equiv corpus. In-process, no C++ pipeline;
        // also part of the full suite, where a red differential blocks the run.
        let failed = (Blade.Tests.RepDifferential.runRepDifferentialTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "rep-check" ] | [ "repcheck" ] ->
        // Phase-C1 declared-certificate agreement gate: the typed walker's
        // SECOND OPINION on every certificate the elaboration seam already
        // checked. Asserts zero disagreements over the ml-equiv corpus (a
        // disagreement is a compiler bug, not a user error), prints the
        // confirm/abstain split, and self-tests the disagree path and the C2
        // engine-hook slot. In-process, no C++ pipeline.
        let failed = (Blade.Tests.RepCheckAgreement.runRepCheckAgreementTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "oracles" ] ->
        // Phase 0.2 review block: the differential-harness oracles checked
        // against hand-computed / analytic values. No Blade source pipeline.
        let failed = (Blade.Tests.OracleReview.runOracleTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "sympower" ] | [ "sympower-tables" ] ->
        // Stage 2b-i review block: the T_{j,l} Sym-power occurrence tables
        // (SymPowerTables.fs) — exact rational kernel/Gram pins, the derived
        // realization phase rule, bit-pins, and the extended realCG
        // completeness pins. In-process, no Blade source pipeline.
        let failed = (Blade.Tests.SymPowerTablesReview.runSymPowerTablesTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "polyoracle" ] | [ "poly-oracle" ] ->
        // Stage 2b-iii oracle block: the Sym^k label basis checked against
        // isotypic projectors built by an independent Casimir-Lagrange route
        // (exact integer/rational, no SymPowerTables exact layer), plus the
        // k = 2 value-level M-pin vs stage 1. In-process, no C++ pipeline.
        let failed = (Blade.Tests.PolyOracleReview.runPolyOracleTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "lietables" ] | [ "lie-tables" ] ->
        // Stage 6c oracle block: the exact so(3) generator tables and the
        // radical-vector Lie discharger (MLLieDischarge.fs). THE EXP-PIN is
        // the keystone — float-assemble each table, exponentiate, and compare
        // against the real Wigner action fit from an INDEPENDENT
        // transcription of the solid harmonics, which is the only thing that
        // rules out convention drift between the symbolic tables and the
        // numeric action the shipped certificates are about. Then the exact
        // algebra (skew-symmetry per radical component, brackets, Casimir,
        // l <= 4), the known-answer verdicts (the triple-product triple, the
        // |x|^2·x thesis pin), three negative controls, and the
        // composition-vs-engine differential. In-process, no C++ pipeline.
        let failed = (Blade.Tests.LieTablesReview.runLieTablesTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "permspec" ] | [ "perm-spec" ] ->
        // Stage 5a-i review block: the Sn permutation-module counting layer
        // (MLPermSpec.fs) — RGS partition enumeration against the Stirling
        // recurrence and against an independently coded block-insertion
        // enumerator, the witness-unitriangularity certificate (strict half
        // tested explicitly: it is the numerical shadow of the Coq order
        // keystone), and the perm_weight_dim / perm_bias_dim sizing rules.
        // Pure integer, in-process, no Blade source pipeline.
        let failed = (Blade.Tests.PermSpecReview.runPermSpecTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "permoracle" ] | [ "perm-oracle" ] ->
        // Stage 5a-ii oracle block: the coarsening-indicator basis checked for
        // COMPLETENESS against the exact rational Reynolds projector
        // (1/N!)Sum_sigma M(sigma)^{tensor m} - B(B^T B)^-1 B^T = P_ref
        // ENTRYWISE over Q, with the Gram closed form N^b(join) predicted by an
        // independent union-find join. BigInteger fractions throughout: no
        // float, no tolerance, no rank decision. This is the half
        // BladePartition.v cites rather than proves. In-process, no C++
        // pipeline.
        let failed = (Blade.Tests.PermOracleReview.runPermOracleTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "structidx" ] | [ "struct-idx" ] ->
        // Stage C1 review block: the constrained-record COUNTING layer
        // (StructIdxSpec.fs) - box enumeration over the per-field INCLUSIVE
        // bounds with the two-route certificate (flat filter vs arrow-style
        // heads filter, compared as set AND as order, since order agreement is
        // what catches an offset bug), the CGm112 anchor and its 3/7/9
        // lo-sweep against an independent triple-loop dense count, the fence
        // and idx_card(R) end to end through resolveStatics, and the negative
        // controls: box cap, non-Int field, unbounded field, non-static
        // struct, and the fuel bomb with its witness cell. Also pins the
        // shared StaticEval fold budget (depth vs steps, the wide shallow
        // fold, the idx_card re-entrancy cycle). Pure integer, in-process,
        // no Blade source pipeline.
        let failed = (Blade.Tests.StructIdxSpecReview.runStructIdxSpecTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "structidxoracle" ] | [ "struct-idx-oracle" ] ->
        // Stage C1 oracle block: an INDEPENDENTLY CODED recursive per-field
        // enumerator over the same solution sets, compared against
        // StructIdxSpec.enumerateBox as SET and as ORDER (the two failure
        // modes reported separately), with hand-written lex tables so two
        // agreeing programs can still be caught being wrong together.
        let failed = (Blade.Tests.StructIdxOracle.runStructIdxOracleTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "pgspec" ] | [ "pg-spec" ] ->
        // Stage 5b-0 review block: the point-group counting layer
        // (MLPointSpec.fs) - the frozen {C4, D4} tables and their integrity
        // certificate (closure vs declared order, orthogonality, the FS
        // indicators nu = 2 - e, J^2 = -Id and J-generator commutation, the
        // R-Burnside trap sum d^2/e = |G|), the 9-vs-5 FS contrast that is
        // stage 5b's thesis, and the TWIN PIN of the generic e-weighted core
        // against MLSpec.homDim / homBlocks on a 15-spec sweep. Pure integer,
        // in-process, no Blade source pipeline.
        let failed = (Blade.Tests.PointSpecReview.runPointSpecTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "pgoracle" ] | [ "pg-oracle" ] ->
        // Stage 5b-0 oracle block: the emitted point-group Hom basis ([Id] at
        // FS-real cells, [Id, J] at FS-complex ones) checked for COMPLETENESS
        // against the exact rational Reynolds projector
        // (1/|G|)Sum_g rho_W(g) M rho_V(g)^T - B(B^T B)^-1 B^T = P_ref
        // ENTRYWISE over Q, with the Gram closed form d*I_e per cell. Three
        // negative controls run live: a dropped J column (trace deficit one per
        // affected cell), the naive e = 1 sizing formula, and a spurious
        // diag(1,-1) End column that dies at R90. BigInteger fractions
        // throughout: no float, no tolerance. In-process, no C++ pipeline.
        let failed = (Blade.Tests.PgOracleReview.runPgOracleTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "alloc" ] ->
        // Standalone C++ runtime-layout tests for the contiguous-backing
        // allocate<>. Compiles + runs cpp/alloc_layout_tests.cpp against the
        // shipped headers. Verifies contiguity/cardinality invariants the
        // value-checking Blade tests cannot catch. No Blade source pipeline.
        let failed = (Blade.Tests.AllocTests.runAllocLayoutTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "omp-pragma" ] ->
        // Pure codegen-string checks: a `where omp(...)` clause must reach the
        // generated C++ as a pragma for every kernel spelling, and for no
        // unannotated one. No toolchain needed, so this also runs in the
        // default suite (unlike omp-coverage below).
        let failed = (Blade.Tests.OmpTests.runOmpPragmaTests ()).Failed
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
    | [ "mpi" ] ->
        // MPI decomposition block standalone (differential vs serial oracle
        // under mpiexec -n 1/2/4). Skips cleanly when g++ / -lmsmpi /
        // mpiexec are absent.
        let failed = (Blade.Tests.MpiTests.runMpiTests ()).Failed
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
    | [ "zarr" ] ->
        // Zarr provider tests. Hermetic: fixtures are generated on the fly
        // (pure .NET file writes), so only the e2e compile+run blocks need
        // g++ (and skip without it). Kept out of the default aggregate like
        // netcdf; returns an exit code directly.
        Blade.Tests.ZarrTests.runZarrTests ()
    | [ "csv" ] ->
        // CSV provider tests. Fully hermetic: fixtures are plain-text files
        // written on the fly, so only the e2e compile+run blocks need g++
        // (and skip without it). Kept out of the default aggregate like
        // netcdf/zarr; returns an exit code directly.
        Blade.Tests.CsvTests.runCsvTests ()
    | [ "hybrid" ] ->
        // Mixed-parallelism tests (MixedParallelismPlan.md): order-table
        // parse checks + gate-off degradation run always; the mpi+omp
        // differentials need mpiexec and skip without it. Opt-in like
        // netcdf/zarr; returns an exit code directly.
        Blade.Tests.HybridTests.runHybridTests ()
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
            | "struct-aborts" | "structaborts" -> Some ("Struct Aborts", structAbortTests)
            | "struct-mutual" | "mutual" -> Some ("Struct Mutual", structMutualTests)
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
            | "ppl" -> Some ("PPL", pplTests)
            | "math" -> Some ("Math", mathTests)
            | "rand" -> Some ("Rand", randTests)
            | "spectra" -> Some ("Spectra", spectraTests)
            | "fallback" -> Some ("Fallback", fallbackTests)
            | "stack-join" | "stackjoin" -> Some ("Stack/Join", stackJoinTests)
            | "sgs" -> Some ("SGS", sgsTests)
            | "ml-ops" | "mlops" -> Some ("ML Ops", mlOpsTests)
            | "ml-e2e" | "mle2e" -> Some ("ML E2E", mlE2eTests)
            | "ml-equiv" | "mlequiv" | "equiv" -> Some ("ML Equiv", mlEquivTests)
            | "sqlish" | "sql" -> Some ("SQL-ish", foreignKeyTests @ maskTests @ setOpTests @ groupByTests @ sortTests @ reduceTests @ extentsTests @ extentsMultiRankTests @ regressionTests @ sqlCombinedTests)
            | "memfree" -> Some ("Mem Free", Blade.Tests.RunAll.memfreeTests)
            | "memfree-stress" | "memfreestress" -> Some ("Mem Free Stress", Blade.Tests.RunAll.memfreeStressTests)
            | _ -> None
        match categoryTests with
        | Some (name, tests) ->
            let r = runTestCategoryFull name tests "./generated_cpp_tests"
            if r.Failed = 0 then 0 else 1
        | None -> eprintfn "Unknown test category: %s" cat; 1
    | _ -> printUsage (); 1

/// Top-level command dispatch (the body of the old Main.fs entry point).
let private dispatchInner (args: string[]) : int =
    // Share the compiler version with the test-harness output helpers so every
    // block header reads "(vX.Y.Z)" consistently, including standalone runs.
    Blade.Tests.TestHarness.version <- compilerVersion
    // `--strict-pins` (Â§6.1(b)) is a build MODE, not a positional argument, and
    // it only means anything for the four verbs that own a typecheck. Strip it
    // out of the argv the verb patterns match on so every existing arm shape
    // (`compile f -o out`, `emit f -o out --verbose`, `run f --mpi 4`) accepts
    // it in any position without a combinatorial explosion of match patterns.
    // Left in place for every other verb, where it stays an unknown argument.
    let strictPinVerb =
        args.Length >= 1 &&
        (match args.[0] with
         | "check" | "compile" | "emit" | "run" -> true
         | _ -> false)
    let strictPins = strictPinVerb && Array.contains "--strict-pins" args
    let args = if strictPins then args |> Array.filter (fun a -> a <> "--strict-pins") else args
    match args with
    // ---- User-facing commands ----
    // `run <file> [--verbose] [--mpi N]` â€” flags in any order after the file.
    | _ when args.Length >= 2 && args.[0] = "run" ->
        let rest = args.[1..] |> Array.toList
        let mutable verbose = false
        let mutable mpiRanks = None
        let mutable file = None
        let mutable bad = None
        let rec parse toks =
            match toks with
            | [] -> ()
            | "--verbose" :: tl -> verbose <- true; parse tl
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

/// Top-level error boundary. Runs the real dispatch and turns any escaping
/// exception into a rendered diagnostic on stderr (exit 1) instead of a raw
/// .NET stack trace: a typed BladeDiagnosticException renders as itself, any
/// other exception becomes a BL9001 internal compiler error (the .NET stack is
/// shown only under --verbose). Successful and existing eprintfn error paths
/// inside dispatchInner are untouched â€” this only catches what used to crash.
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
