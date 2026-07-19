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
open Blade.Tests.Ppl
open Blade.Tests.Math
open Blade.Tests.Rand
open Blade.Tests.Spectra
open Blade.Tests.Fallback
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
    printfn "                                    (the --omp/--cuda/--timing/--mpi flags combine)"
    printfn "  test --ir-only                    Run IR-only tests (fast, no C++ compilation)"
    printfn "  test alloc                        Run C++ allocation-layout tests (contiguity/cardinality)"
    printfn "  test omp-coverage                 Run the OpenMP thread-coverage block standalone"
    printfn "  test cuda                         Run the CUDA kernel block standalone"
    printfn "  test mpi                          Run the MPI decomposition block standalone"
    printfn "  test netcdf                       Run the NetCDF provider block (needs libnetcdf + sample.nc)"
    printfn "  test zarr                         Run the Zarr provider block (hermetic; g++ for the e2e parts)"
    printfn "  test timing                       Run the differential timing block standalone"
    printfn "  test diff-oracle [category]       Diff printed values against the pinned ./oracle build"
    printfn "  test interp [category]            Diff the tree-walking interpreter against the compiled binary"
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
        // Errors come back as coded, spanned Diagnostics and are rendered
        // here (rustc-style, with source snippets) into the string channel.
        let useColor = not Console.IsErrorRedirected
        match lowerDiag (Some filePath) source with
        | Error ds, sm -> Error (Blade.Diagnostics.Render.renderAll useColor (Some sm) ds)
        | Ok (ir, tcWarnings), sm ->
            for w in tcWarnings do
                eprintfn "[TypeCheck Warning] %s" w
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
let compileToExe (filePath: string) (outputPath: string option) (verbose: bool) : Result<string, string> =
    match compileFile filePath verbose with
    | Error e -> Error e
    | Ok (cppCode, warnings) ->
        let baseName = Path.GetFileNameWithoutExtension(filePath)
        let dir = Path.GetDirectoryName(Path.GetFullPath(filePath))
        let dir = if String.IsNullOrEmpty dir then "." else dir
        // Infer backend from generated source: device kernels → .cu + nvcc.
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
let runFile (filePath: string) (verbose: bool) (mpiRanks: int option) : int =
    match mpiRanks with
    | None ->
        match compileToExe filePath None verbose with
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
            match compileToExe filePath None verbose with
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
// top-level binding — its "return value", as in a function body. Earlier
// bindings, and the many synthetic `__`-internal bindings that a single
// `ppl.dist`/module call expands into, stay hidden. Rebinding a top-level name
// replaces the earlier definition (duplicate lets are a C++ redeclaration
// error) so downstream snippets still see it; the echo then shows that
// snippet's own last value, recomputed.
//
// A snippet that is not a declaration is a bare EXPRESSION (`a`, `a + 1`) —
// the file-level "return a value by naming it" idiom. Top-level source only
// admits declarations, so the REPL wraps the expression in a transient
// binding (`let it = <expr>`), runs, echoes the value, and discards it: the
// session and the diff baseline stay untouched, so repeating the expression
// echoes again instead of diffing to silence. A bare identifier naming a
// session FUNCTION echoes its signature from the typechecker alone (functions
// aren't let-bindable just to print them).
//
// Output lines are type-annotated by an in-process parse+typecheck(+lower,
// for HM-monomorphized value types) of the same source — see ReplTypes.
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
    /// session (Blade.Interp.Repl.LoweredSession) — the SAME front-end pass the
    /// interpreter runs on in compileRunEcho, so the candidate path never lowers
    /// twice. Value bindings prefer the LOWERED types: calls to HM-polymorphic
    /// functions monomorphize during lowering, so the typed AST can still carry
    /// T?n inference vars where the IR is concrete (`let r = id(3.5)` is Float64
    /// only in IR). Pure map assembly — no parse/typecheck/lower here.
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
    /// name -> display info. Failures yield an empty map — values still print,
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
    /// binding a bare REPL expression was wrapped in — its name is stripped
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
    printfn "Blade REPL (v%s) — each submission echoes its last binding's (typed) value." compilerVersion
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
    // value changes every run — exclude it from the output diff.
    let isTimingLine (l: string) =
        System.Text.RegularExpressions.Regex.IsMatch(l, @"completed in [0-9.eE+~-]+m?s\s*$")

    // A snippet is a declaration iff it opens with a declaration keyword;
    // anything else is a bare expression to evaluate and echo.
    let declRe =
        System.Text.RegularExpressions.Regex(
            @"^\s*(let|static|function|type|struct|interface|impl|unit|import|from|module)\b")
    let identRe =
        System.Text.RegularExpressions.Regex(@"^[A-Za-z_][A-Za-z0-9_]*$")

    // A raw run-output line is `name = value`; grab the leading name so we can
    // single out just the one binding we mean to echo.
    let outNameRe =
        System.Text.RegularExpressions.Regex(
            @"^([A-Za-z_][A-Za-z0-9_]*) = ",
            System.Text.RegularExpressions.RegexOptions.Compiled)

    /// Evaluate `candidate` and echo ONLY `targetName`'s value line — the
    /// submission's "return value" — type-annotated. Every earlier user binding,
    /// and every synthetic `__`-internal binding (a single `ppl.dist` expands
    /// into dozens), stays hidden.
    ///
    /// INTERP-FIRST (the payoff of the interpreter arc): the candidate lowers
    /// ONCE (Repl.lowerSession — shared with the type-annotation map below), then
    /// runs under the tree-walking interpreter. On a supported exit (0, or a
    /// Blade guard panic 1) its output is authoritative and NO g++ is invoked —
    /// a typical turn drops from ~1-5 s to <100 ms. If the interpreter cannot yet
    /// evaluate some node (125) or hits its own bug (70) it FALLS BACK to the
    /// historical g++ compile+run for this one input (a single notice on stderr),
    /// with identical filtering/annotation. A front-end/validate rejection is
    /// surfaced exactly as the old compileToExe Error arm did.
    ///
    /// `transient` is the synthetic name a bare expression was wrapped in (its
    /// prefix is stripped in display), else None. Returns Some (lines,
    /// printedCount, info) on a clean exit — info is the SAME LoweredSession's
    /// annotation map, so the caller reuses it without lowering again — or None
    /// (snippet must not be kept).
    let compileRunEcho (candidate: ResizeArray<string>) (targetName: string option) (transient: string option)
        : (string[] * int * Map<string, ReplTypes.Info>) option =
        let src = String.concat "\n\n" candidate + "\n"
        File.WriteAllText(srcPath, src)
        let useColor = not Console.IsErrorRedirected
        match Blade.Interp.Repl.lowerSession (Some srcPath) useColor src with
        | Error rendered ->
            // Front-end / validate rejection — identical to the old
            // compileToExe Error arm (both render the same diagnostics).
            eprintfn "%s" rendered
            eprintfn "[snippet not kept]"
            None
        | Ok lowered ->
            let info = ReplTypes.sessionInfoOf lowered
            let display l = ReplTypes.annotate info transient l
            // Given a process-like (code, stdout, stderr) triple from EITHER the
            // interpreter or the compiled fallback, filter to targetName and
            // echo — this is the historical tail of compileRunEcho, unchanged.
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
                    eprintfn "[exit %d — snippet not kept]" code
                    None
            // Historical g++ compile+run for this ONE input (the fallback lane).
            let viaCompiled () =
                match compileToExe srcPath None false with
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
                for w in lowered.Warnings do eprintfn "[TypeCheck Warning] %s" w
                emit r.ExitCode r.Stdout r.Stderr
            | Blade.Interp.Repl.InterpFellShort _ ->
                // The interpreter can't evaluate this input yet — one-time notice
                // so the user understands the latency spike, then the g++ path
                // (whose stdout the SAME targetName filter isolates — no
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
                // A final function/type binding produces no run output — echo
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
        else
            // Bare expression: `blade run` semantics only print top-level
            // BINDINGS, so wrap the expression in a transient one, run, and
            // echo its value — WITHOUT keeping it. The session (and diff
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
                let ds = errors |> List.map Blade.TypeEnv.diagnosticOfCompileError
                eprintfn "%s" (Blade.Diagnostics.Render.renderAll useColor (Some sm) ds)
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
        eprintfn "%s" e
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
    let isSuiteFlag f = f = "--omp" || f = "--cuda" || f = "--timing" || f = "--mpi"
    match rest with
    | [] -> runFullSuite defaultFullSuiteOptions
    | flags when flags |> List.forall isSuiteFlag ->
        runFullSuite { IncludeOmp = List.contains "--omp" flags
                       IncludeCuda = List.contains "--cuda" flags
                       IncludeTiming = List.contains "--timing" flags
                       IncludeMpi = List.contains "--mpi" flags }
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
    | [ "interp" ] ->
        // Interpreter differential gate: the tree-walking IR interpreter vs the
        // compiled binary over the supported corpus slice — byte-identical
        // normalized stdout required. Slice grows per interpreter milestone.
        let failed = (Blade.Tests.InterpDiff.runInterpDiffTests Blade.Tests.InterpDiff.currentSlice).Failed
        if failed = 0 then 0 else 1
    | [ "interp"; cat ] ->
        // Single corpus category through the interpreter differential gate.
        let failed = (Blade.Tests.InterpDiff.runInterpDiffTests [cat]).Failed
        if failed = 0 then 0 else 1
    | [ "spans" ] ->
        // Error-location tests (§3.4 / Phase 2 gate): deliberately broken
        // sources, asserting the reported line. No C++ pipeline.
        let failed = (Blade.Tests.Spans.runSpanTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "diagnostics" ] ->
        // Diagnostics core (renderer + registry) and the diagnostics corpus
        // (broken sources with pinned codes/spans). No C++ pipeline.
        let core = (Blade.Tests.DiagnosticsCore.runDiagnosticsCoreTests ()).Failed
        let corpus = (Blade.Tests.DiagCorpus.runDiagCorpusTests ()).Failed
        if core + corpus = 0 then 0 else 1
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
            | "ml-ops" | "mlops" -> Some ("ML Ops", mlOpsTests)
            | "ml-e2e" | "mle2e" -> Some ("ML E2E", mlE2eTests)
            | "ml-equiv" | "mlequiv" | "equiv" -> Some ("ML Equiv", mlEquivTests)
            | "sqlish" | "sql" -> Some ("SQL-ish", foreignKeyTests @ maskTests @ setOpTests @ groupByTests @ sortTests @ reduceTests @ extentsTests @ extentsMultiRankTests @ regressionTests @ sqlCombinedTests)
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
    match args with
    // ---- User-facing commands ----
    // `run <file> [--verbose] [--mpi N]` — flags in any order after the file.
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
        | None, Some f -> runFile f verbose mpiRanks

    | [| "compile"; file |] ->
        match compileToExe file None false with
        | Ok path -> printfn "%s" path; 0
        | Error e -> eprintfn "%s" e; 1
    | [| "compile"; file; "-o"; output |] ->
        match compileToExe file (Some output) false with
        | Ok path -> printfn "%s" path; 0
        | Error e -> eprintfn "%s" e; 1

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

/// Top-level error boundary. Runs the real dispatch and turns any escaping
/// exception into a rendered diagnostic on stderr (exit 1) instead of a raw
/// .NET stack trace: a typed BladeDiagnosticException renders as itself, any
/// other exception becomes a BL9001 internal compiler error (the .NET stack is
/// shown only under --verbose). Successful and existing eprintfn error paths
/// inside dispatchInner are untouched — this only catches what used to crash.
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
