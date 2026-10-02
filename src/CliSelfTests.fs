// The `blade test` blocks that exercise the CLI surface itself (smoke, strict
// pins, surfacing, ide serve/eval/cells/references) plus the `blade test <key>`
// category dispatch. Split out of Cli.fs: compiles after CliCommands.fs (these
// blocks drive compileFile/checkFile/emitFile) and before Cli.fs (dispatchInner
// routes `test` verbs here).
module Blade.CliSelfTests

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
open Blade.Tests.Combinators
open Blade.Tests.Tuples
open Blade.Tests.RecursiveArrays
open Blade.Tests.Segments
open Blade.Tests.StackJoin
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
open Blade.CliCommands

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
                     record runTest TH.Fail $"exit {code}, output: {output.Trim()}")
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

    // --- `--print <names>`: which top-level bindings the program prints ---
    //
    // The CLI has always printed EVERY top-level binding, which is what makes
    // a program's own output dominate its cost on a large array (the scale run
    // of docs/plans/structural/04: 231 MB of stdout hid every saved chunk
    // read). The selection is a MODE, pinned process-wide (BLADE_PRINT), read
    // by codegen's print pass AND the interpreter's, so the two lanes print
    // one set and a differential run still compares like with like.
    //
    // Emission-only cases need no toolchain; the run cases skip without g++.
    let printSrc =
        "let x = 1 + 2 * 3\n\
         let y = x * 2\n\
         let z = [1.0, 2.0, 3.0]\n"
    let withPrint (v: string option) (f: unit -> unit) =
        let prior = System.Environment.GetEnvironmentVariable "BLADE_PRINT"
        System.Environment.SetEnvironmentVariable("BLADE_PRINT", (match v with Some s -> s | None -> null))
        try f () finally System.Environment.SetEnvironmentVariable("BLADE_PRINT", prior)
    let cppOf (label: string) (src: string) : Result<string, string> =
        match Blade.Lowering.lower src with
        | Error e -> Error e
        | Ok ir -> Ok (fst (Blade.CodeGen.genSelfContainedProgramFromIR ir label))
    let recordCase name cond detail = record name (if cond then TH.Pass else TH.Fail) detail
    (match cppOf "print_all" printSrc with
     | Error e -> record "print: default emission" TH.Fail e
     | Ok all ->
         recordCase "print: unset prints every binding"
             (all.Contains "\"x = \"" && all.Contains "\"y = \"" && all.Contains "\"z = [\"") ""
         withPrint (Some "y") (fun () ->
             match cppOf "print_y" printSrc with
             | Error e -> record "print: --print y emission" TH.Fail e
             | Ok sel ->
                 recordCase "print: a selection emits the named binding's print and no other"
                     (sel.Contains "\"y = \"" && not (sel.Contains "\"x = \"") && not (sel.Contains "\"z = [\"")) ""
                 // Everything ELSE about the program is untouched: a selection
                 // changes what is printed, never what is computed.
                 recordCase "print: a selection changes only the print block"
                     (sel.Contains "int64_t x = " && sel.Contains "int64_t y = ") "")
         withPrint (Some "y, z") (fun () ->
             match cppOf "print_yz" printSrc with
             | Error e -> record "print: multi-name selection" TH.Fail e
             | Ok sel ->
                 recordCase "print: names separate on commas and spaces"
                     (sel.Contains "\"y = \"" && sel.Contains "\"z = [\"" && not (sel.Contains "\"x = \"")) "")
         withPrint (Some "totl") (fun () ->
             // A typo must be LOUD: a silently empty print block looks exactly
             // like a program that computed nothing.
             match cppOf "print_typo" printSrc with
             | Error e -> record "print: an unknown name refuses" TH.Fail e
             | Ok sel ->
                 recordCase "print: an unknown name splices a refusal naming it and the real bindings"
                     (sel.Contains "#error" && sel.Contains "totl" && sel.Contains "x, y, z") ""))
    // A name that IS a binding but never prints (a deferred loop value) is
    // refused the same way: asked for by name, silence is the typo's twin.
    withPrint (Some "w") (fun () ->
        match cppOf "print_deferred" (printSrc + "let w = method_for(z) <@> lambda(v) -> v * 2.0\n") with
        | Error e -> record "print: a deferred binding refuses" TH.Fail e
        | Ok sel ->
            recordCase "print: selecting a never-materialized loop value splices a refusal"
                (sel.Contains "#error" && sel.Contains "deferred loop value" && sel.Contains "|> compute") "")
    // A binding holding a FUNCTION has no printed form in either lane. A
    // non-generic alias (`g`) printed nothing and exited 0; a generic alias
    // (`h`, removed by monomorphization) was reported as "not a top-level
    // binding", which it is. Both refuse by name, as a function, in both lanes.
    let fnSrc =
        "function sq(x: Float64) -> Float64 = x * x\n\
         function total(x: T^1) -> T^0 = reduce(x, (+))\n\
         let g = sq\n\
         let h = total\n\
         let r = g(3.0) + h([1.0, 2.0])\n"
    for (sel, what) in [ ("g", "a non-generic function alias"); ("h", "an eliminated generic function alias") ] do
        withPrint (Some sel) (fun () ->
            (match cppOf ("print_fn_" + sel) fnSrc with
             | Error e -> record $"print: selecting {what} refuses" TH.Fail e
             | Ok cpp ->
                 recordCase $"print: selecting {what} splices a function refusal"
                     (cpp.Contains "#error" && cpp.Contains $"{sel} is a function"
                      && not (cpp.Contains "not a top-level binding")) "")
            (match Blade.Lowering.lower fnSrc with
             | Error e -> record $"print: the interpreter refuses {what}" TH.Fail e
             | Ok ir ->
                 let r = Blade.Interp.Run.runProgram ir ("print_fn_interp_" + sel) Blade.Interp.Value.defaultLimits
                 recordCase $"print: the interpreter refuses {what} the same way"
                     (r.ExitCode <> 0 && r.Stderr.Contains $"{sel} is a function") r.Stderr))
    // The two lanes agree under one pin: the interpreter prints the same set.
    withPrint (Some "y") (fun () ->
        match Blade.Lowering.lower printSrc with
        | Error e -> record "print: interpreter honours the pin" TH.Fail e
        | Ok ir ->
            let r = Blade.Interp.Run.runProgram ir "print_interp" Blade.Interp.Value.defaultLimits
            let out = r.Stdout.Replace("\r\n", "\n")
            recordCase "print: the interpreter prints the same selection as codegen"
                (out.Contains "y = 14" && not (out.Contains "x = 7")) out)
    // End to end through the user-facing compile+run path.
    if not capabilities.Value.HasGpp then
        record "print: compiled run honours the selection" TH.Skip "requires g++, not found"
    else
        let pDir = Path.Combine(Path.GetTempPath(), "blade_print_sel_" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(pDir) |> ignore
        try
            let srcFile = Path.Combine(pDir, "psel.blade")
            File.WriteAllText(srcFile, printSrc)
            withPrint (Some "y") (fun () ->
                match compileToExe srcFile None false false with
                | Error e -> record "print: compiled run honours the selection" TH.Fail e
                | Ok exe ->
                    match runExecutable exe with
                    | Ok (0, out) ->
                        recordCase "print: compiled run honours the selection"
                            (out.Contains "y = 14" && not (out.Contains "x = 7") && not (out.Contains "z = ")) out
                    | Ok (code, out) -> record "print: compiled run honours the selection" TH.Fail $"exit {code}: {out}"
                    | Error e -> record "print: compiled run honours the selection" TH.Fail e)
        finally
            try Directory.Delete(pDir, true) with _ -> ()

    // --- One flag parser for every file verb ---
    //
    // Only `run` used to parse its flags; every other verb matched an exact
    // argv shape, so `check f --verbose`, `compile f --verbose`,
    // `emit f --verbose -o x` failed as "unrecognized command".
    let parses verb toks = parseVerbArgs verb toks
    recordCase "flags: check accepts --verbose after the file"
        (match parses "check" [ "f.blade"; "--verbose" ] with
         | Ok o -> o.File = Some "f.blade" && o.Verbose
         | Error _ -> false) ""
    recordCase "flags: emit takes --verbose and -o in any order"
        (match parses "emit" [ "--verbose"; "f.blade"; "-o"; "x.cpp" ], parses "emit" [ "f.blade"; "-o"; "x.cpp"; "--verbose" ] with
         | Ok a, Ok b -> a = b && a.Output = Some "x.cpp" && a.Verbose
         | _ -> false) ""
    recordCase "flags: compile takes --verbose before -o"
        (match parses "compile" [ "f.blade"; "--verbose"; "-o"; "out.exe" ] with
         | Ok o -> o.Output = Some "out.exe" && o.Verbose
         | Error _ -> false) ""
    recordCase "flags: ide check takes --json on either side of the file"
        (match parses "ide check" [ "f.blade"; "--json" ], parses "ide check" [ "--json"; "f.blade" ] with
         | Ok a, Ok b -> a.File = Some "f.blade" && b.File = Some "f.blade"
         | _ -> false) ""
    recordCase "flags: an unknown flag is named, with what the verb accepts"
        (match parses "check" [ "f.blade"; "--bogus" ] with
         | Error msg -> msg.Contains "--bogus" && msg.Contains "check" && msg.Contains "--verbose"
         | Ok _ -> false) ""
    recordCase "flags: a flag another verb owns is refused by this one"
        (match parses "check" [ "f.blade"; "-o"; "x" ], parses "run" [ "f.blade"; "--mpi"; "0" ] with
         | Error a, Error b -> a.Contains "-o" && b.Contains "positive"
         | _ -> false) ""
    recordCase "flags: ide check explains why it takes no --strict-pins"
        (match parses "ide check" [ "f.blade"; "--strict-pins" ] with
         | Error msg -> msg.Contains "--strict-pins" && msg.Contains "BL4010"
         | Ok _ -> false) ""

    // --- The help text: current file extension, every mode flag ---
    let help =
        let sw = new StringWriter()
        let prior = Console.Out
        Console.SetOut sw
        try printUsage () finally Console.SetOut prior
        sw.ToString()
    recordCase "help: speaks .blade, not the pre-rename .edgi" (not (help.Contains ".edgi") && help.Contains ".blade") ""
    recordCase "help: documents --print, --run-record and test <key>"
        (help.Contains "--print" && help.Contains "--run-record" && help.Contains "test <key>"
         && help.Contains "multifile" && help.Contains "opt-diff") ""

    // --- BL9002: generated code the C++ back end rejects is a Blade bug ---
    let cppPath = Path.Combine(Path.GetTempPath(), "blade-build", "prog-1-2-3", "prog.cpp")
    let gxx =
        $"Compilation failed (exit 1):\n{cppPath}: In function 'int main()':\n{cppPath}:12:8: error: redeclaration of 'double scale'\n{cppPath}:10:8: note: 'double scale' previously declared here\nCommand: g++ -O3"
    recordCase "ICE: a g++ error in the generated file is a back-end rejection"
        (backendRejection gxx cppPath = Some "error: redeclaration of 'double scale'") ""
    recordCase "ICE: an #error refusal keeps its own text (BL7004 channel / header gate)"
        ((backendRejection $"Compilation failed (exit 1):\n{cppPath}:3:2: error: #error BLAS gate off" cppPath).IsNone) ""
    recordCase "ICE: a link failure is a toolchain problem, not generated code"
        ((backendRejection "Compilation failed (exit 1):\nC:/msys64/ucrt64/bin/ld.exe: cannot find -lnetcdf\ncollect2.exe: error: ld returned 1 exit status" cppPath).IsNone) ""
    (let dir = Path.Combine(Path.GetTempPath(), "blade_ice_" + Guid.NewGuid().ToString("N"))
     Directory.CreateDirectory dir |> ignore
     try
         let r = reportBackendRejection "error: redeclaration of 'double scale'" gxx cppPath dir false
         recordCase "ICE: rendered as BL9002, naming the first error, the source and the kept log"
             (r.Contains "BL9002" && r.Contains "redeclaration of 'double scale'" && r.Contains cppPath
              && r.Contains "not in your program" && File.Exists(Path.Combine(dir, "backend-errors.log"))) (if r.Contains "BL9002" then "" else r)
     finally
         try Directory.Delete(dir, true) with _ -> ())

    // --- Builds happen in a private scratch directory, never beside the source ---
    //
    // `blade run` wrote the .cpp, ~15 runtime headers and the executable
    // beside the source and deleted "the headers it created" afterwards, so two
    // runs in one directory deleted each other's headers mid-compile, and every
    // run left an executable behind.
    let selfExe =
        [ Path.Combine(AppContext.BaseDirectory, "Blade.exe"); Path.Combine(AppContext.BaseDirectory, "Blade") ]
        |> List.tryFind File.Exists
    let spawn (cwd: string) (args: string list) : int * string * string =
        match selfExe with
        | None -> (-1, "", "no Blade executable beside the test assembly")
        | Some exe ->
            let psi = System.Diagnostics.ProcessStartInfo(exe)
            for a in args do psi.ArgumentList.Add a
            psi.WorkingDirectory <- cwd
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            use p = System.Diagnostics.Process.Start psi
            let o = Blade.Runtime.readToEndOffPool p.StandardOutput
            let e = Blade.Runtime.readToEndOffPool p.StandardError
            if p.WaitForExit 180000 then (p.ExitCode, o.Result, e.Result)
            else
                (try p.Kill() with _ -> ())
                (-1, o.Result, "timed out")
    let beside (dir: string) =
        Directory.GetFiles dir |> Array.map Path.GetFileName |> Array.sort |> List.ofArray
    if selfExe.IsNone then
        record "cli: bare `blade` prints usage" TH.Skip "no Blade executable beside the test assembly"
    else
        let dir = Path.Combine(Path.GetTempPath(), "blade_cli_verbs_" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            // A bare `blade` STARTED THE FULL SUITE in the current directory.
            let (code, out, _) = spawn dir []
            let ok = code = 0 && out.Contains "Usage:" && not (Directory.Exists(Path.Combine(dir, "generated_cpp_tests")))
            recordCase "cli: bare `blade` prints usage (and does not start the suite)" ok
                (if ok then "" else out.Substring(0, min 200 out.Length))
            File.WriteAllText(Path.Combine(dir, "v.blade"), "let x = 1 + 2 * 3\n")
            let (code, out, err) = spawn dir [ "check"; "v.blade"; "--verbose" ]
            recordCase "cli: `check f --verbose` is a check, not an unrecognized command"
                (code = 0 && out.Contains "OK") (out + err)
            let (code, _, err) = spawn dir [ "test"; "interp"; "no-such-category" ]
            let ok = code = 1 && err.Contains "unknown corpus category 'no-such-category'" && not (err.Contains "BL9001")
            recordCase "cli: `test interp <typo>` is a clean usage error, not BL9001" ok (if ok then "" else err)
            // ...but the guard must not refuse a lane's RESERVED words: it once
            // knew only `all` / `corpus` for llvm, and CI's `test llvm goldens`
            // step died on it. The goldens themselves are CI's step; this pins
            // only that the word gets past the guard.
            let (_, _, err) = spawn dir [ "test"; "llvm"; "goldens" ]
            recordCase "cli: `test llvm goldens` is not refused as an unknown category"
                (not (err.Contains "unknown corpus category")) err
            // `check` LOWERS: a construct that typechecks but has no lowering
            // rule where it sits is refused by check itself -- coded BL6002 and
            // spanned at the expression -- not first by `emit`, spanless.
            File.WriteAllText(Path.Combine(dir, "cf.blade"),
                "function f(x: Array<Float like Idx<5>>) -> Float = {\n"
                + "    let m = mask(x, lambda(q) -> q > 2.0)\n"
                + "    let c = compound(x, m)\n"
                + "    reduce(c, (+))\n"
                + "}\n"
                + "let a = [1.0, 2.0, 3.0, 4.0, 5.0]\n"
                + "let s = f(a)\n")
            let (code, out, err) = spawn dir [ "check"; "cf.blade" ]
            recordCase "cli: `check` reports a lowering refusal as spanned BL6002"
                (code <> 0 && err.Contains "BL6002" && err.Contains "cf.blade:3:13" && not (out.Contains "OK"))
                (out + err)
            // `plan` records the decisions only code generation makes (here the
            // packed storage and triangular nest `comm` buys for one array),
            // in the text form and in the JSON, whose field set is unchanged.
            File.WriteAllText(Path.Combine(dir, "cov.blade"),
                "type TimeIdx = Idx<2>\n"
                + "let A: Array<Float64 like Idx<2>, TimeIdx> = [[1.0, 2.0], [3.0, 4.0]]\n"
                + "let k = lambda(a: Array<Float64 like TimeIdx>, b: Array<Float64 like TimeIdx>) where comm(a, b) -> prodsum(a, b)\n"
                + "let r = method_for(A, A) <@> k |> compute\n")
            let (code, out, err) = spawn dir [ "plan"; "cov.blade" ]
            recordCase "cli: `plan` lists the storage and iteration decisions codegen makes"
                (code = 0 && out.Contains "[symmetric-storage v1] r" && out.Contains "[triangular-iteration v1] r"
                 && not (out.Contains "0 optimization decision(s)"))
                (out + err)
            let (code, out, err) = spawn dir [ "plan"; "cov.blade"; "--json" ]
            recordCase "cli: `plan --json` carries them in the unchanged decision schema"
                (code = 0 && out.Contains "\"rule\":\"symmetric-storage\"" && out.Contains "\"outcome\":\"applied\""
                 && out.Contains "\"version\":1" && out.Contains "\"evidence\":[" && out.Contains "\"inputs\":")
                (out + err)
        finally
            try Directory.Delete(dir, true) with _ -> ()
    if not capabilities.Value.HasGpp || selfExe.IsNone then
        record "build dir: run writes nothing beside the source" TH.Skip "requires g++ and the Blade executable"
    else
        let dir = Path.Combine(Path.GetTempPath(), "blade_cli_build_" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            File.WriteAllText(Path.Combine(dir, "a.blade"), "let x = 1 + 2 * 3\n")
            File.WriteAllText(Path.Combine(dir, "b.blade"), "let y = 40 + 2\n")
            // A header of the user's own that happens to share a runtime
            // header's name: no compile may touch it.
            File.WriteAllText(Path.Combine(dir, "index_types.h"), "// mine\n")
            // Two concurrent runs in ONE directory (the reported race), then
            // the directory must hold exactly what it held before.
            let t1 = System.Threading.Tasks.Task.Run(fun () -> spawn dir [ "run"; "a.blade" ])
            let t2 = System.Threading.Tasks.Task.Run(fun () -> spawn dir [ "run"; "b.blade" ])
            let (c1, o1, e1) = t1.Result
            let (c2, o2, e2) = t2.Result
            recordCase "build dir: two concurrent runs in one directory both succeed"
                (c1 = 0 && o1.Contains "x = 7" && c2 = 0 && o2.Contains "y = 42") (o1 + e1 + o2 + e2)
            recordCase "build dir: run leaves nothing beside the source"
                (beside dir = [ "a.blade"; "b.blade"; "index_types.h" ]) (String.concat ", " (beside dir))
            recordCase "build dir: a same-named file of the user's is untouched"
                (File.ReadAllText(Path.Combine(dir, "index_types.h")) = "// mine\n") ""
            // `compile` without -o still places the executable beside the
            // source (its documented output), and nothing else.
            let (c3, o3, e3) = spawn dir [ "compile"; "a.blade" ]
            let exeName = "a" + Platforms.exeExtension
            recordCase "build dir: compile places only the executable beside the source"
                (c3 = 0 && o3.Trim().EndsWith exeName
                 && beside dir = List.sort [ "a.blade"; "b.blade"; "index_types.h"; exeName ])
                (o3 + e3 + " | " + String.concat ", " (beside dir))
            // Flags after the file still work from a scratch build, and the
            // run record lands where it was asked for (not in the scratch dir).
            let (c4, o4, e4) = spawn dir [ "run"; "a.blade"; "--run-record"; "rr.json" ]
            recordCase "build dir: run --run-record writes the record where asked"
                (c4 = 0 && File.Exists(Path.Combine(dir, "rr.json"))) (o4 + e4)
        finally
            try Directory.Delete(dir, true) with _ -> ()

    // --- REPL / notebook: re-running a declaration cell replaces it ---
    //
    // The rebind key had no `struct` / `interface` / `impl` arm, so re-running
    // a struct cell APPENDED a second `struct P` (silently last-wins then, a
    // BL2009 refusal now) instead of replacing the first.
    recordCase "repl: struct, static struct, interface and impl cells have a rebind key"
        (Blade.ReplSession.bindingName "struct P { x: Float64 }" = Some "P"
         && Blade.ReplSession.bindingName "static struct Band { i: Int<min=0, max=3> }" = Some "Band"
         && Blade.ReplSession.bindingName "interface Shape {" = Some "Shape"
         && Blade.ReplSession.bindingName "impl Shape for Box {" = Some "impl Shape for Box") ""
    (let (src, _) =
        Blade.ReplSession.assembleCells
            [ "struct P { x: Float64 }"; "let p = P { x = 1.0 }"; "struct P { x: Float64, y: Float64 }" ]
     let count = src.Split([| "struct P" |], StringSplitOptions.None).Length - 1
     recordCase "repl: a re-run struct cell replaces the first declaration" (count = 1) (if count = 1 then "" else src))

    // --- Module namespacing: both lanes print what the pins say ---
    //
    // The interpreter differential gate covers single-file categories only,
    // so the two multi-module programs whose NAMES the namespacing changed
    // are run through the interpreter here and held to their own pins (the
    // compiled lane is held to the same pins by `blade test multifile`).
    for (testName, files) in Blade.Tests.Corpus.multiFileCategory "multifile" do
        if testName.StartsWith "Same-named values across modules" || testName.StartsWith "Same-named functions across modules" then
            let label = $"modules: interpreter honours the pins of '{testName}'"
            match Blade.Lowering.lowerMultiSource files with
            | Error e -> record label TH.Fail e
            | Ok ir ->
                let r = Blade.Interp.Run.runProgram ir "modules_interp" Blade.Interp.Value.defaultLimits
                let pins = files |> List.collect (fun (_, src) -> Blade.Tests.Expect.parseExpectedValues src)
                match Blade.Tests.Expect.checkExpectedValues pins r.Stdout with
                | Ok () when not pins.IsEmpty -> record label TH.Pass ""
                | Ok () -> record label TH.Fail "no pins found"
                | Error errs -> record label TH.Fail (String.concat "; " errs + " | " + r.Stdout)

    let count o = results |> Seq.filter (fun (_, r) -> r = o) |> Seq.length
    let passed, failed, skipped = count TH.Pass, count TH.Fail, count TH.Skip
    let failedNames = results |> Seq.filter (fun (_, r) -> r = TH.Fail) |> Seq.map fst |> List.ofSeq
    let parts =
        [ $"{passed} passed"; $"{failed} failed" ]
        @ (if skipped > 0 then [$"{skipped} skipped"] else [])
    TH.printFooter blockName parts
    { TH.BlockResult.Block = blockName
      Passed = passed
      Failed = failed
      Skipped = skipped
      FailedNames = failedNames }

/// Type-check a file without generating code

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
                   $"exit {code}, output: {out.Trim()}"

        // Strict: the same suggestion becomes an error and fails the build.
        let (code, out) = quietly (fun () -> checkFile unpinnedPath true)
        if code = 1 && out.Contains "BL4010" && out.Contains "where comm(a, b)" then
            record "check --strict-pins: unpinned deduction is a BL4010 error (exit 1)" TH.Pass ""
        else
            record "check --strict-pins: unpinned deduction is a BL4010 error (exit 1)" TH.Fail
                   $"exit {code}, output: {out.Trim()}"

        // The suggestion is ACTIONABLE: applying the proposed pin clears it.
        let (code, out) = quietly (fun () -> checkFile pinnedPath true)
        if code = 0 then
            record "check --strict-pins: the pinned twin passes" TH.Pass ""
        else
            record "check --strict-pins: the pinned twin passes" TH.Fail
                   $"exit {code}, output: {out.Trim()}"

        // The compile/emit/run lane (all three funnel through compileFile).
        let ((result: Result<string * string list, string>), _) =
            quietly (fun () -> compileFile unpinnedPath false true)
        match result with
        | Error e when e.Contains "BL4010" ->
            record "compile lane --strict-pins: fails before codegen" TH.Pass ""
        | Error e ->
            record "compile lane --strict-pins: fails before codegen" TH.Fail
                   ($"""wrong error: {(e.Replace("\n", " | "))}""")
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
        [ $"{passed} passed"; $"{failed} failed" ]
        @ (if skipped > 0 then [$"{skipped} skipped"] else [])
    TH.printFooter blockName parts
    { TH.BlockResult.Block = blockName
      Passed = passed
      Failed = failed
      Skipped = skipped
      FailedNames = failedNames }

/// The IR validator's USER-error backstops report their own code at the
/// command line. The cast-obligation sweep (IRValidate check 1c) finds a
/// generic function's float-to-integer cast at an instance only
/// monomorphization produced -- `outer` forwards its own open variable, so no
/// seam before it sees the Float64. The compile lane used to stamp every
/// validation message BL6001 ("IR validation error") with "BL3019:" buried in
/// the text; it now maps them through IRValidate.diagnosticOfValidationMessage.
/// (The corpus harness matches a pinned code anywhere in the refusal text, so
/// casts/032 cannot see the difference; this block drives `compileFile`.)
let private runValidationCodeTests () : TH.BlockResult =
    let blockName = "Validation Codes"
    TH.printHeader "IR-validation backstops report their own code (compile lane)"
    let results = ResizeArray<string * TH.Outcome>()
    let record name outcome detail =
        TH.resultLine outcome name detail
        results.Add((name, outcome))
    let src =
        "function toint(x: T^0) -> Int64 = Int64(x)\n\
         function applyG(g: (T) -> Int64, x: T) -> Int64 = g(x)\n\
         function outer(x: T^0) -> Int64 = applyG(toint, x)\n\
         let r = outer(2.5)\n"
    let tmpDir = Path.Combine(Path.GetTempPath(), "blade_validation_codes_" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmpDir) |> ignore
    let quietly (f: unit -> 'a) : 'a =
        let sw = new StringWriter()
        let (oldOut, oldErr) = (Console.Out, Console.Error)
        try
            Console.SetOut sw
            Console.SetError sw
            f ()
        finally
            Console.SetOut oldOut
            Console.SetError oldErr
    try
        let path = Path.Combine(tmpDir, "backstop.blade")
        File.WriteAllText(path, src)
        let (result: Result<string * string list, string>) = quietly (fun () -> compileFile path false false)
        let name = "compile lane: the cast backstop is error[BL3019], not BL6001"
        match result with
        | Error e when e.Contains "BL3019" && not (e.Contains "BL6001") && e.Contains "(in function 'toint')" ->
            record name TH.Pass ""
        | Error e -> record name TH.Fail (e.Replace("\n", " | "))
        | Ok _ -> record name TH.Fail "compiled instead of failing"
        // The mapping itself: a structural message stays BL6001.
        let d = Blade.IRValidate.diagnosticOfValidationMessage "[IR Validation] in binding 'r': unresolved type variable T?7 in expression"
        let name = "a structural validation message stays BL6001"
        if d.Code = "BL6001" then record name TH.Pass "" else record name TH.Fail $"got {d.Code}"
    finally
        try Directory.Delete(tmpDir, true) with _ -> ()
    let count o = results |> Seq.filter (fun (_, r) -> r = o) |> Seq.length
    let passed, failed, skipped = count TH.Pass, count TH.Fail, count TH.Skip
    let failedNames = results |> Seq.filter (fun (_, r) -> r = TH.Fail) |> Seq.map fst |> List.ofSeq
    TH.printFooter blockName [ $"{passed} passed"; $"{failed} failed" ]
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
                   $"exit {code}, json: {out.Trim()}"

        // 2. ...and so do the deduced facts (channel (f)) on that arm.
        let name = "ide check --json: deduced[] is populated on the error arm"
        if out.Contains "\"deduced\":[" && out.Contains "\"kind\":\"comm\"" then
            record name TH.Pass ""
        else
            record name TH.Fail $"json: {out.Trim()}"

        // 3. Control: the pinned twin is clean and claims nothing.
        let (code, out, _) = quietly2 (fun () -> Blade.Ide.ideCheck pinnedPath)
        let name = "ide check --json: the pinned twin yields no BL4010 (exit 0)"
        if code = 0 && not (out.Contains "BL4010") then
            record name TH.Pass ""
        else
            record name TH.Fail $"exit {code}, json: {out.Trim()}"

        // 4. `check`: warnings render as diagnostics on STDERR, keeping
        // stdout ("OK") pipeable.
        let (code, out, err) = quietly2 (fun () -> checkFile unpinnedPath false)
        let name = "check: the warning renders as warning[BL4010] on stderr, not stdout"
        if code = 0 && err.Contains "warning[BL4010]" && not (out.Contains "BL4010")
           && out.Contains "OK" then
            record name TH.Pass ""
        else
            record name TH.Fail
                   $"exit {code}, stdout: {out.Trim()}, stderr: {err.Trim()}"

        // 5. `check` on the erroring file still prints the warning (S1).
        let (code, _, err) = quietly2 (fun () -> checkFile errPath false)
        let name = "check: warnings print alongside the error instead of vanishing"
        if code = 1 && err.Contains "warning[BL4010]" && err.Contains "error[BL2001]" then
            record name TH.Pass ""
        else
            record name TH.Fail $"exit {code}, stderr: {err.Trim()}"

        // 6. The compile lane agrees (compile/emit/run all funnel here).
        let ((result: Result<string * string list, string>), _, err) =
            quietly2 (fun () -> compileFile errPath false false)
        let name = "compile lane: warnings print on the error arm too"
        match result with
        | Error _ when err.Contains "warning[BL4010]" -> record name TH.Pass ""
        | Error _ -> record name TH.Fail $"no warning on stderr: {err.Trim()}"
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
            record name TH.Fail $"rendered: {rendered.Trim()}"

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
                   ($"""codes drained: {(drained |> List.map _.Code |> String.concat ",")}""")
        let name = "typeCheckWarningDiagnostics: BL4014 survives --strict-pins"
        if hasBL4014 drainedStrict then record name TH.Pass ""
        else
            record name TH.Fail
                   ($"""codes drained: {(drainedStrict |> List.map _.Code |> String.concat ",")}""")

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
            record name TH.Fail $"deduced json: {deducedJson.Trim()}"
    finally
        try Directory.Delete(tmpDir, true) with _ -> ()
    let count o = results |> Seq.filter (fun (_, r) -> r = o) |> Seq.length
    let passed, failed, skipped = count TH.Pass, count TH.Fail, count TH.Skip
    let failedNames = results |> Seq.filter (fun (_, r) -> r = TH.Fail) |> Seq.map fst |> List.ofSeq
    let parts =
        [ $"{passed} passed"; $"{failed} failed" ]
        @ (if skipped > 0 then [$"{skipped} skipped"] else [])
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
        $"{{\"id\":{id},\"cmd\":\"check\",\"tier\":\"{tier}\",\"file\":\"{(esc file)}\",\"source\":\"{(esc source)}\"}}"
    let pingReq (id: int) = $"{{\"id\":{id},\"cmd\":\"ping\"}}"
    let renderReq (id: int) (session: string) (bindings: string list) (values: float list) =
        let bs = bindings |> List.map (fun b -> "\"" + b + "\"") |> String.concat ","
        let vs = values |> List.map (sprintf "%g") |> String.concat ","
        $"{{\"id\":{id},\"cmd\":\"render\",\"session\":\"{session}\","
        + $"\"bindings\":[{bs}],\"values\":[{vs}]}}"
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
                   && r.Contains "\"serve\":1" && r.Contains $"\"version\":\"{compilerVersion}\"" ->
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
            record name TH.Fail $"{raw.Split('\n').Length} newline-separated parts"

        // 4. Full tier: monomorphization adds `concreteType` only where it
        // actually knows more than the typed AST did. For an HM call it no
        // longer does: the call judgment instantiates the callee's signature
        // per call (docs/plans/plan-call-judgment.md), so the typed AST
        // already says `r: Int64` / `s: Float64` -- these used to read `T`,
        // the CALLEE's type variable leaking into the caller's binding, and
        // were upgraded here.
        let (code, responses, _) = drive [checkReq 12 "full" hmPath hmSource; shutdownReq]
        let fullBody = match responses with [r] -> r | _ -> ""
        let name = "check tier=full: HM value bindings are already concrete"
        if code = 0 && fullBody.Contains "\"tier\":\"full\""
           && fullBody.Contains "\"name\":\"r\",\"kind\":\"let\",\"line\":2,\"col\":1,\"type\":\"Int64\""
           && fullBody.Contains "\"name\":\"s\",\"kind\":\"let\",\"line\":3,\"col\":1,\"type\":\"Float64\"" then
            record name TH.Pass ""
        else
            record name TH.Fail $"exit {code}, response: {fullBody}"

        // 5. `concreteType` is emitted only as a genuinely different spelling:
        // a binding the typed AST already typed concretely gets none.
        let name = "full tier adds no concreteType where the type is already concrete"
        if not (fullBody.Contains "\"concreteType\"") then
            record name TH.Pass ""
        else
            record name TH.Fail $"response: {fullBody}"

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

        // 6b. Provider-read provenance, one binding per verb shape: `read`
        // and `stream` take the store view directly, `load_compound` takes
        // it first of two, and a `write` chases the named binding it
        // persists back to THAT binding's provenance. Hermetic: a real 2x2
        // int csv on disk, so the load typechecks and the fast tier keeps
        // its bindings; nothing here lowers, so csv's codegen-time
        // stream/load_compound refusals never fire.
        Blade.ProviderStatics.install ()
        let provStoreCsv = Path.Combine(tmpDir, "prov_store.csv")
        File.WriteAllText(provStoreCsv, "1,2\n3,4\n")
        let provReadSource =
            "import csv as c\n"
            + sprintf "let store = c.load(\"%s\")\n" (provStoreCsv.Replace('\\', '/'))
            + "let obs = store.vars.data |> c.read\n"
            + "let strm = c.stream(store.vars.data)\n"
            + "let cmp = c.load_compound(store.vars.data, store.vars.data)\n"
            + sprintf "let saved = c.write(\"%s\", obs)\n"
                      ((Path.Combine(tmpDir, "prov_out.csv")).Replace('\\', '/'))
        let (provJson, provReadCode) =
            Blade.Ide.ideCheckSource (Path.Combine(tmpDir, "provread.blade")) provReadSource
        // The literal `"<field>":{...}` object inside `bname`'s binding
        // (bounded by the next `"name":` key), or a marker when absent.
        let fieldOf (field: string) (bname: string) =
            let key = sprintf "\"name\":\"%s\"" bname
            let start = provJson.IndexOf key
            if start < 0 then "no-binding" else
            let next = provJson.IndexOf("\"name\":\"", start + key.Length)
            let seg = if next < 0 then provJson.Substring start else provJson.Substring(start, next - start)
            let pk = sprintf "\"%s\":" field
            let ps = seg.IndexOf pk
            if ps < 0 then "none" else
            let pe = seg.IndexOf('}', ps)
            seg.Substring(ps + pk.Length, pe - ps - pk.Length + 1)
        let provOf = fieldOf "providerRead"
        let writeOf = fieldOf "providerWrite"
        let expectedProv = "{\"store\":\"store\",\"member\":\"vars.data\"}"
        let name = "check payload: providerRead covers read/stream/load_compound"
        if provReadCode = 0 && provOf "obs" = expectedProv && provOf "strm" = expectedProv
           && provOf "cmp" = expectedProv then
            record name TH.Pass ""
        else
            record name TH.Fail
                   (sprintf "exit %d, obs=%s strm=%s cmp=%s" provReadCode
                            (provOf "obs") (provOf "strm") (provOf "cmp"))
        // A write faces the OPPOSITE direction: `saved` PERSISTS `obs`, so it
        // names where that array came from under `providerWrite` and carries
        // no `providerRead` at all.
        let name = "check payload: a write binding carries providerWrite, never providerRead"
        if provReadCode = 0 && writeOf "saved" = expectedProv && provOf "saved" = "none" then
            record name TH.Pass ""
        else
            record name TH.Fail
                   (sprintf "exit %d, saved providerWrite=%s providerRead=%s" provReadCode
                            (writeOf "saved") (provOf "saved"))
        let name = "check payload: providers[] describes the loaded csv store"
        if provJson.Contains "\"store\":\"store\",\"alias\":\"c\",\"provider\":\"csv\"" then
            record name TH.Pass ""
        else
            record name TH.Fail (provJson.Substring(0, min 400 provJson.Length))

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
            record name TH.Fail $"exit {srcCode} vs {cliCode}"

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
            record name TH.Fail $"exit {code}, json: {out.Trim()}"

        // THE RENDER FAST PATH. The camera stays in the CELL -- the notebook
        // keeps saying where the lens points -- and what the lane COMPILES is
        // that program with the camera erased into a run-time CSV read. The
        // erased source is the same bytes for every camera, which is the whole
        // mechanism: build once, re-run per gesture.
        //
        // Pinned on the erasure rather than end to end, because e2e costs a
        // g++ build and the risk lives here: a slot mapped to the wrong
        // binding renders a plausible picture of the wrong place.
        let camSrc =
            "import plot\n\nlet a = 1\n\nlet cam_cx = -0.5\n"
            + "let cam_cy = 0.25\nlet cam_r = 0.004\nlet cam_px = 256\n"
            + "\nlet z = cam_cx + cam_r\n"
        let name = "the camera erasure rewrites each binding to its own CSV slot"
        match Blade.IdeServe.cameraErasedSource camSrc ["cam_cx"; "cam_cy"; "cam_r"]
                                                @"C:\tmp\camera.csv" with
        | Ok out when out.StartsWith "import csv as __blade_cam_csv"
                      && out.Contains "let cam_cx = __blade_cam_slots(0, 0)"
                      && out.Contains "let cam_cy = __blade_cam_slots(0, 1)"
                      && out.Contains "let cam_r = __blade_cam_slots(0, 2)"
                      // Not named, so not erased: the resolution stays literal.
                      && out.Contains "let cam_px = 256"
                      // Forward slashes -- a Windows separator inside a Blade
                      // string literal would read as an escape.
                      && out.Contains "__blade_cam_csv.load(\"C:/tmp/camera.csv\")"
                      // The reader precedes the EARLIEST camera binding, and so
                      // precedes every use of the camera.
                      && out.IndexOf "__blade_cam_slots = " < out.IndexOf "let cam_cx =" ->
            record name TH.Pass ""
        | other -> record name TH.Fail (sprintf "%A" other)

        // ...and the refusals, which are the safety property. A camera name the
        // erasure cannot pin down exactly once would leave a literal standing
        // and render from a camera the caller never set -- silently, and to a
        // picture that looks entirely reasonable.
        let name = "the camera erasure refuses a name bound more than once"
        let dupSrc = "let cam_r = 0.1\n\nlet cam_r = 0.2\n"
        match Blade.IdeServe.cameraErasedSource dupSrc ["cam_r"] "c.csv" with
        | Error e when e.Contains "cam_r" && e.Contains "2 times" -> record name TH.Pass ""
        | other -> record name TH.Fail (sprintf "%A" other)

        let name = "the camera erasure refuses a name that is not bound at all"
        match Blade.IdeServe.cameraErasedSource ("let q = 1\n") ["cam_r"] "c.csv" with
        | Error e when e.Contains "cam_r" && e.Contains "0 times" -> record name TH.Pass ""
        | other -> record name TH.Fail (sprintf "%A" other)

        // The camera crosses as text, so it has to cross EXACTLY: a lens moved
        // by one ulp at a 1e-13 half-span is a different picture.
        let name = "a camera row round-trips its Float64s exactly"
        let camVals = [ -0.743643887037151; 0.131825904205330; 0.004 ]
        let camBack =
            (Blade.IdeServe.cameraCsvText camVals).Trim().Split(',')
            |> Array.map float |> List.ofArray
        if camBack = camVals then record name TH.Pass ""
        else record name TH.Fail (sprintf "%A" camBack)

        // A render re-runs the WHOLE program, so it re-emits every plot the
        // notebook has -- but a camera change can only alter the plots that read
        // the camera. Sending the rest replays the notebook into the panel: a
        // fixed tour under a stable id animates its entire descent again on
        // every zoom and lands back where it started, which is exactly how this
        // was reported.
        let name = "an unchanged frame is not re-sent"
        let same = [| "a"; "b"; "c" |]
        match Blade.IdeServe.changedFrameIndices same same with
        | [||] -> record name TH.Pass ""
        | other -> record name TH.Fail (sprintf "%A" other)

        // ...and the direction that must never be wrong. Holding back a frame
        // that DID move leaves a stale picture on screen, so a differing digest
        // and anything past the end of the previous run both count as changed.
        let name = "a moved frame is sent, and only that one"
        match Blade.IdeServe.changedFrameIndices [| "a"; "b"; "c" |] [| "a"; "B"; "c" |] with
        | [| 1 |] -> record name TH.Pass ""
        | other -> record name TH.Fail (sprintf "%A" other)

        let name = "frames past the previous run's end are always sent"
        match Blade.IdeServe.changedFrameIndices [| "a" |] [| "a"; "b"; "c" |] with
        | [| 1; 2 |] -> record name TH.Pass ""
        | other -> record name TH.Fail (sprintf "%A" other)

        // The first render of a session has nothing to compare against, and the
        // panel has not seen this executable's output: send all of it.
        let name = "the first render sends every frame"
        match Blade.IdeServe.changedFrameIndices [||] [| "a"; "b" |] with
        | [| 0; 1 |] -> record name TH.Pass ""
        | other -> record name TH.Fail (sprintf "%A" other)

        // Protocol refusals, and that the loop SURVIVES them: a bad render
        // request must not take the notebook's language server down with it.
        let (code, responses, _) =
            drive [ renderReq 1 "nb" ["cam_r"] [1.0; 2.0]; pingReq 2; shutdownReq ]
        let name = "render refuses a bindings/values mismatch and keeps serving"
        match responses with
        | [err; pong] when code = 0 && err.Contains "\"error\""
                           && err.Contains "1 bindings and 2 values"
                           && pong.Contains "\"ok\":true" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        let (code, responses, _) =
            drive [ renderReq 1 "ghost" ["cam_r"] [0.1]; pingReq 2; shutdownReq ]
        let name = "render on a session that has evaluated nothing says so"
        match responses with
        | [err; pong] when code = 0 && err.Contains "evaluated nothing to render"
                           && pong.Contains "\"ok\":true" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)
    finally
        Directory.SetCurrentDirectory entryDir
        try Directory.Delete(tmpDir, true) with _ -> ()
    let count o = results |> Seq.filter (fun (_, r) -> r = o) |> Seq.length
    let passed, failed, skipped = count TH.Pass, count TH.Fail, count TH.Skip
    let failedNames = results |> Seq.filter (fun (_, r) -> r = TH.Fail) |> Seq.map fst |> List.ofSeq
    let parts =
        [ $"{passed} passed"; $"{failed} failed" ]
        @ (if skipped > 0 then [$"{skipped} skipped"] else [])
    TH.printFooter blockName parts
    { TH.BlockResult.Block = blockName
      Passed = passed
      Failed = failed
      Skipped = skipped
      FailedNames = failedNames }

/// `blade ide surface` and the artifacts it feeds: the renderer's shape, the
/// serve lane's arm, and the checked-in protocol/ snapshots.
///
/// The FRESHNESS case is the point of the block. Everything else here is a
/// shape assertion; that one catches the failure that actually hurts -- a
/// compiler whose surface has moved without a regenerated surface.json, which
/// ships a quietly lying package to every downstream consumer.
///
/// A missing snapshot FAILS rather than skips. Blade.fsproj deploys both files
/// beside the binary precisely so this block cannot go vacuously green.
let private runSurfaceTests () : TH.BlockResult =
    let blockName = "Surface"
    TH.printHeader "ide surface (language surface, serve arm, committed snapshots)"
    let results = ResizeArray<string * TH.Outcome>()
    let record name outcome detail =
        TH.resultLine outcome name detail
        results.Add((name, outcome))
    /// Working tree first (a regenerated file takes effect without a rebuild),
    /// else the copy deployed beside the binary -- tests/Corpus.fs's precedent,
    /// for its reason. Returns the paths tried when nothing is found, because
    /// "which roots did you look in" is the whole diagnosis.
    let artifact (rel: string) : Result<string, string> =
        let candidates = [ Path.Combine(".", rel); Path.Combine(AppContext.BaseDirectory, rel) ]
        match candidates |> List.tryFind File.Exists with
        | Some p -> Ok p
        | None -> Error (candidates |> List.map Path.GetFullPath |> String.concat " ; ")
    /// Feed a whole conversation and split on the framing newline -- the same
    /// in-process seam runIdeServeTests drives.
    let drive (requests: string list) : int * string list * string =
        let input = new StringReader(String.concat "\n" requests + "\n")
        let output = new StringWriter()
        let code = Blade.IdeServe.serveLoop compilerVersion (input :> TextReader) (output :> TextWriter)
        let raw = output.ToString()
        let parts = raw.Split('\n') |> Array.toList
        (code, (parts |> List.filter (fun p -> p <> "")), raw)
    let surfaceJson = Blade.Ide.renderSurface compilerVersion
    let parsed = try Some (System.Text.Json.JsonDocument.Parse surfaceJson) with _ -> None

    // 1. It is JSON, and its envelope says which surface it is.
    let name = "renderSurface emits parseable JSON carrying version 1 and the compiler version"
    match parsed with
    | None -> record name TH.Fail (surfaceJson.Substring(0, min 200 surfaceJson.Length))
    | Some d ->
        let root = d.RootElement
        let ver = (try root.GetProperty("version").GetInt32() with _ -> -1)
        let cv = (try root.GetProperty("compilerVersion").GetString() with _ -> "")
        if ver = 1 && cv = compilerVersion then record name TH.Pass ""
        else record name TH.Fail $"version {ver}, compilerVersion '{cv}'"

    // Accessors over the parsed document; every later case reads through these,
    // so a missing field degrades to an empty list and a readable failure
    // rather than an exception that takes the whole block down.
    let rootOpt = parsed |> Option.map _.RootElement
    let strArrayIn (owner: System.Text.Json.JsonElement option) (field: string) : string list =
        match owner with
        | Some el ->
            (match el.TryGetProperty field with
             | true, a when a.ValueKind = System.Text.Json.JsonValueKind.Array ->
                 [ for x in a.EnumerateArray() -> defaultArg (Option.ofObj (x.GetString())) "" ]
             | _ -> [])
        | None -> []
    let strArray (field: string) = strArrayIn rootOpt field
    let objArray (field: string) (keys: string list) : string list list =
        match rootOpt with
        | Some el ->
            (match el.TryGetProperty field with
             | true, a when a.ValueKind = System.Text.Json.JsonValueKind.Array ->
                 [ for x in a.EnumerateArray() ->
                     keys |> List.map (fun k ->
                         match x.TryGetProperty k with
                         | true, v -> defaultArg (Option.ofObj (v.GetString())) ""
                         | _ -> "") ]
             | _ -> [])
        | None -> []
    let mathIntrinsic (kind: string) : string list =
        let owner =
            rootOpt |> Option.bind (fun el ->
                match el.TryGetProperty "mathIntrinsics" with
                | true, m -> Some m
                | _ -> None)
        strArrayIn owner kind

    // 2. Sentinels: one per list, chosen so a list going missing or arriving
    // re-ordered is caught without pinning contents that legitimately grow.
    let keywords = objArray "keywords" ["word"; "token"]
    let operators = strArray "operators"
    let builtins = strArray "builtins" |> Set.ofList
    let scalarTypes = strArray "scalarTypes"
    let builtinCalls = strArray "builtinCalls"
    // StaticEval's core table (knownBuiltinNames's first union member): the
    // names that are there no matter which registries have been installed.
    let coreBuiltins =
        [ "exp"; "log"; "log10"; "sqrt"; "sin"; "cos"; "tan"
          "sinh"; "cosh"; "tanh"; "asin"; "acos"; "atan"
          "floor"; "ceil"; "atan2"; "log_base"; "fma"
          "abs"; "min"; "max"; "length"; "prodsum" ]
    let name = "every list is present, ordered from its source of truth, and complete"
    let failures =
        [ if keywords |> List.tryHead <> Some ["let"; "KwLet"] then
            yield sprintf "keywords[0] = %A" (List.tryHead keywords)
          if keywords.Length <> Blade.Lexer.keywordEntries.Length then
            yield $"{keywords.Length} keywords, {Blade.Lexer.keywordEntries.Length} entries"
          if not (List.contains "<@>" operators) then yield "operators lacks <@>"
          if operators.Length <> Blade.Lexer.operatorEntries.Length then
            yield $"{operators.Length} operators, {Blade.Lexer.operatorEntries.Length} entries"
          if mathIntrinsic "binary" <> ["atan2"; "log_base"] then
            yield sprintf "binary intrinsics = %A" (mathIntrinsic "binary")
          if mathIntrinsic "ternary" <> ["fma"] then
            yield sprintf "ternary intrinsics = %A" (mathIntrinsic "ternary")
          if mathIntrinsic "unary" |> List.isEmpty then yield "unary intrinsics empty"
          if mathIntrinsic "complex" |> List.isEmpty then yield "complex intrinsics empty"
          if scalarTypes.Length <> 16 then yield $"{scalarTypes.Length} scalar types"
          if scalarTypes <> Blade.TypeCheck.builtinScalarNames then yield "scalarTypes != builtinScalarNames"
          for b in coreBuiltins do
            if not (Set.contains b builtins) then yield $"builtins lacks {b}"
          let staticOnly = strArray "staticOnlyBuiltins" |> Set.ofList
          for b in [ "min"; "max"; "length" ] do
            if not (Set.contains b staticOnly) then yield $"staticOnlyBuiltins lacks {b}"
          for b in [ "exp"; "abs"; "fma"; "atan2"; "prodsum" ] do
            if Set.contains b staticOnly then yield $"staticOnlyBuiltins lists run-time {b}"
          if not (Set.isSubset staticOnly builtins) then yield "staticOnlyBuiltins is not a subset of builtins"
          if not (List.contains "hermitian" builtinCalls) then yield "builtinCalls lacks hermitian"
          if not (List.contains "display.emit" builtinCalls) then yield "builtinCalls lacks display.emit" ]
    if List.isEmpty failures then record name TH.Pass ""
    else record name TH.Fail (String.concat "; " failures)

    // 3. The diagnostics registry travels whole and in order, each code carrying
    // the phase its band implies -- what lets a client title a BLxxxx without
    // shipping a copy of Diagnostics.fs.
    let diagnostics = objArray "diagnostics" ["code"; "title"; "phase"]
    let expected =
        Blade.Diagnostics.Codes.registryEntries |> List.map (fun (c, t) -> [c; t])
    let name = "diagnostics mirror registryEntries in order, with a non-empty phase each"
    let codesMatch = (diagnostics |> List.map (List.truncate 2)) = expected
    let phasesOk = diagnostics |> List.forall (fun e -> List.length e = 3 && e.[2] <> "")
    if codesMatch && phasesOk then
        record name TH.Pass $"{diagnostics.Length} codes"
    else
        record name TH.Fail
            (sprintf "%d emitted vs %d registered, phases ok: %b"
                     diagnostics.Length expected.Length phasesOk)

    // 4. The serve arm: same line plus the correlation id, an id-less request
    // is an error rather than an unframed response, and the loop survives both.
    let (code, responses, raw) =
        drive [ "{\"id\":5,\"cmd\":\"surface\"}"; "{\"cmd\":\"surface\"}"
                "{\"id\":6,\"cmd\":\"ping\"}"; "{\"cmd\":\"shutdown\"}" ]
    let name = "serve answers cmd surface with one framed line, requires an id, keeps going"
    match responses with
    | [dump; noId; pong] when code = 0
                              && dump.StartsWith "{\"id\":5,\"version\":1,\"compilerVersion\":"
                              && dump.Contains "\"keywords\":[{\"word\":\"let\""
                              && dump.Contains "\"diagnostics\":[{\"code\":\"BL0001\""
                              && noId.Contains "\"id\":null" && noId.Contains "surface"
                              && pong.Contains "\"id\":6" && raw.EndsWith "\n" ->
        record name TH.Pass ""
    | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

    // 5. FRESHNESS. The committed snapshot is what every consumer reads; this
    // is the only thing that keeps it honest. TrimEnd because the file carries
    // a trailing newline (and CRLF after a checkout on Windows) that the
    // renderer's single line does not.
    let name = "the committed protocol/surface.json matches this compiler's render"
    match artifact "protocol/surface.json" with
    | Error tried ->
        record name TH.Fail $"snapshot not found; looked in {tried}"
    | Ok path ->
        let onDisk = File.ReadAllText(path).TrimEnd('\r', '\n')
        if onDisk = surfaceJson then record name TH.Pass ""
        else
            // Report the first divergence: a 10 KB diff is unreadable, the
            // offset plus its neighbourhood names the field that moved.
            let at =
                Seq.zip onDisk surfaceJson
                |> Seq.tryFindIndex (fun (a, b) -> a <> b)
                |> Option.defaultValue (min onDisk.Length surfaceJson.Length)
            let ctx (s: string) = s.Substring(max 0 (at - 30), min 80 (s.Length - max 0 (at - 30)))
            record name TH.Fail
                ($"diverges at {at} (regenerate with `blade ide surface`)\n      file: {(ctx onDisk)}\n      live: {(ctx surfaceJson)}")

    // 6. The hand-authored knowledge base: the half of the package no generator
    // can produce, keyed by the SAME registry the surface carries so a new code
    // cannot ship undocumented. Every example path must exist AND mention its
    // code -- a stale path is worse than no example, because a client shows it.
    let kbDocPaths = ResizeArray<string>()
    let name = "protocol/data/diagnostics.json covers every registry code, with live examples"
    match artifact "protocol/data/diagnostics.json" with
    | Error tried ->
        record name TH.Fail $"knowledge base not found; looked in {tried}"
    | Ok path ->
        match (try Some (System.Text.Json.JsonDocument.Parse(File.ReadAllText path)) with _ -> None) with
        | None -> record name TH.Fail $"{path} is not JSON"
        | Some kb ->
            let entries =
                match kb.RootElement.TryGetProperty "codes" with
                | true, c when c.ValueKind = System.Text.Json.JsonValueKind.Object ->
                    [ for p in c.EnumerateObject() -> (p.Name, p.Value) ]
                | _ -> []
            let byCode = dict entries
            let strOf (el: System.Text.Json.JsonElement) (field: string) =
                match el.TryGetProperty field with
                | true, v when v.ValueKind = System.Text.Json.JsonValueKind.String ->
                    defaultArg (Option.ofObj (v.GetString())) ""
                | _ -> ""
            let listOf (el: System.Text.Json.JsonElement) (field: string) =
                match el.TryGetProperty field with
                | true, a when a.ValueKind = System.Text.Json.JsonValueKind.Array ->
                    [ for x in a.EnumerateArray() -> defaultArg (Option.ofObj (x.GetString())) "" ]
                | _ -> []
            for (_, e) in entries do kbDocPaths.AddRange(listOf e "docs")
            let problems =
                [ for (code, title) in Blade.Diagnostics.Codes.registryEntries do
                    match byCode.TryGetValue code with
                    | false, _ -> yield $"{code} absent"
                    | true, e ->
                        if strOf e "title" <> title then
                            yield $"""{code} title '{(strOf e "title")}' <> registry '{title}'"""
                        if strOf e "explanation" = "" then yield $"{code} has no explanation"
                        if strOf e "fix" = "" then yield $"{code} has no fix"
                        for ex in listOf e "examples" do
                            match artifact ex with
                            | Error _ -> yield $"{code} example missing: {ex}"
                            | Ok p ->
                                if not ((File.ReadAllText p).Contains code) then
                                    yield $"{code} example never mentions it: {ex}"
                  for (code, _) in entries do
                    if not (Blade.Diagnostics.Codes.isRegistered code) then
                        yield $"'{code}' is not a registered code" ]
            if List.isEmpty problems then record name TH.Pass $"{entries.Length} codes"
            else record name TH.Fail (problems |> List.truncate 6 |> String.concat "; ")

    // ...and its docs[] half, which points into docs/ -- repo-only, so this leg
    // alone skips when the suite runs from the deployed directory.
    let name = "knowledge-base docs[] paths resolve in the repo tree"
    if not (Directory.Exists "docs") then
        record name TH.Skip "no ./docs (running beside the binary)"
    else
        let missing = kbDocPaths |> Seq.distinct |> Seq.filter (File.Exists >> not) |> List.ofSeq
        if List.isEmpty missing then
            record name TH.Pass $"{kbDocPaths |> Seq.distinct |> Seq.length} paths"
        else record name TH.Fail (String.concat "; " missing)

    let count o = results |> Seq.filter (fun (_, r) -> r = o) |> Seq.length
    let passed, failed, skipped = count TH.Pass, count TH.Fail, count TH.Skip
    let failedNames = results |> Seq.filter (fun (_, r) -> r = TH.Fail) |> Seq.map fst |> List.ofSeq
    let parts =
        [ $"{passed} passed"; $"{failed} failed" ]
        @ (if skipped > 0 then [$"{skipped} skipped"] else [])
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
        $"{{\"id\":{id},\"cmd\":\"eval\",\"session\":\"{(esc session)}\",\"source\":\"{(esc source)}\"}}"
    let resetReq (id: int) (session: string) =
        $"{{\"id\":{id},\"cmd\":\"resetSession\",\"session\":\"{esc session}\"}}"
    let checkReq (id: int) (file: string) (source: string) =
        $"{{\"id\":{id},\"cmd\":\"check\",\"tier\":\"fast\",\"file\":\"{(esc file)}\",\"source\":\"{(esc source)}\"}}"
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
        // 1. The base case: a declaration is kept SILENTLY -- a cell displays
        // its "return value" (a trailing bare expression) and nothing else, so
        // the binding's value is read back by a later bare-identifier cell.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let x = 2"; evalReq 2 "nb" "x"; shutdownReq ]
        let name = "eval keeps a declaration silently; a bare identifier reads it back"
        match responses with
        | [decl; probe] when code = 0 && decl.Contains "\"id\":1" && decl.Contains "\"kept\":true"
                             && decl.Contains "\"exitCode\":0" && decl.Contains "\"lane\":\"interp\""
                             && decl.Contains "\"elapsedMs\":"
                             && decl.Contains "\"bindings\":[]"
                             && decl.Contains "\"diagnostics\":[]"
                             && probe.Contains "{\"name\":\"\",\"type\":\"Int64\",\"value\":\"2\"}" ->
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
        | [_; _; rebind; after] when code = 0
                                     && rebind.Contains "\"kept\":true"
                                     && rebind.Contains "\"bindings\":[]"
                                     && after.Contains "{\"name\":\"\",\"type\":\"Int64\",\"value\":\"50\"}" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 3b. The same rebind, but the cell OPENS WITH A COMMENT -- a prose
        // banner above the bindings, which is how a notebook cell is normally
        // written. `bindingName` used to read the raw snippet with a `^\s*`
        // anchor: `\s` spans newlines, so a BLANK-led declaration matched and a
        // COMMENT-led one did not. spliceDeclaration reads None as "no earlier
        // definition to supersede" and APPENDS, so the stale binding stayed put
        // and the rebind landed AFTER the dependent that had spliced in place --
        // which then read the old value. Nothing failed; the answer was wrong.
        //
        // Multi-binding, because that is the shape that shows the whole bug: `b`
        // (no comment above it) spliced correctly while `a` did not, so the
        // cell half-applied. 456 is the fixed answer; 756 was the bug.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let a = 1.0\nlet b = 2.0\nlet c = 3.0"
                    evalReq 2 "nb" "let s = a * 100.0 + b * 10.0 + c"
                    evalReq 3 "nb" "// the camera\nlet a = 4.0\nlet b = 5.0\nlet c = 6.0"
                    evalReq 4 "nb" "s"; shutdownReq ]
        let name = "a comment-led rebind still supersedes (every binding in the cell)"
        match responses with
        | [_; _; rebind; after] when code = 0
                                     && rebind.Contains "\"kept\":true"
                                     && after.Contains "\"value\":\"456.0\"" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 3c. DESTRUCTURING lets across cells. Every top-level `let (a, b) =`
        // used to lower to a binding named `_`, and the interpreter's session
        // memo is keyed by binding NAME -- so the second cell's destructure
        // adopted the FIRST cell's cached tuple ([1.5, 3.0] for pair(10.0)),
        // and a rebind of the same leaves, which the splice did not recognise
        // as a declaration, was appended and adopted the stale value too.
        // Now the binding is `_(a,b)`, the splice keys the cell by it, and the
        // memo drops it AND its leaves on a rebind. Three answers pin the three
        // fixes: the second pair, the rebound pair, and a dependent (17 = 7 + 10).
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "function pair(x: Float64) -> (Float64, Float64) = (x, x * 2.0)"
                    evalReq 2 "nb" "let (a, b) = pair(1.5)"
                    evalReq 3 "nb" "let (c, d) = pair(10.0)
[c, d]"
                    evalReq 4 "nb" "let s = a + c"
                    evalReq 5 "nb" "// the pair
let (a, b) = pair(7.0)
[a, b]"
                    evalReq 6 "nb" "s"; shutdownReq ]
        let name = "destructuring lets are distinct across cells and rebind in place"
        match responses with
        | [_; _; second; _; rebound; after] when code = 0
                                                 && second.Contains "\"value\":\"[10.0, 20.0]\""
                                                 && rebound.Contains "\"value\":\"[7.0, 14.0]\""
                                                 && after.Contains "\"value\":\"17.0\"" ->
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
            drive [ evalReq 1 "nb" "[1, 2, 3, 4, 5, 6, 7]"; shutdownReq ]
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
            record name TH.Fail $"{raw.Split('\n').Length} newline-separated parts"

        // 11. A function declaration is silent like any declaration; a bare
        // reference to it still carries the signature the REPL would have
        // echoed, answered from the declaration's own binding.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "function twice(p) = p * 2"; evalReq 2 "nb" "twice"; shutdownReq ]
        let name = "a bare function reference reports its signature and no value"
        match responses with
        | [decl; probe] when code = 0
                             && decl.Contains "\"kept\":true" && decl.Contains "\"bindings\":[]"
                             && probe.Contains "{\"name\":\"twice\",\"type\":\"(Float64) -> Float64\",\"value\":\"\"}" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 12. A :paste-shaped cell declares several names at once. All of them
        // bind -- silently. A later cell reads any of them back.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let m = 3\nlet n = m + 4"; evalReq 2 "nb" "n"; shutdownReq ]
        let name = "a multi-declaration cell is silent and binds every name"
        match responses with
        | [decls; probe] when code = 0
                              && decls.Contains "\"kept\":true" && decls.Contains "\"bindings\":[]"
                              && probe.Contains "{\"name\":\"\",\"type\":\"Int64\",\"value\":\"7\"}" ->
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
            drive [ evalReq 1 "nb" "let z = 1 / 0"; evalReq 2 "nb" "let ok = 6"
                    evalReq 3 "nb" "ok"; shutdownReq ]
        let name = "a runtime panic is not kept and names itself"
        match responses with
        | [panic; after; probe] when code = 0
                                     && panic.Contains "\"kept\":false" && panic.Contains "\"exitCode\":1"
                                     && panic.Contains "\"lane\":\"interp\"" && panic.Contains "\"bindings\":[]"
                                     && panic.Contains "\"stderr\":\"error[BL8013]"
                                     && panic.Contains "\"severity\":\"error\",\"line\":1,\"col\":1"
                                     && panic.Contains "integer division by zero"
                                     // The panic's CODE is the diagnostic's code,
                                     // and the message is not re-prefixed with it.
                                     && panic.Contains "\"code\":\"BL8013\""
                                     && not (panic.Contains "\"message\":\"error[")
                                     && after.Contains "\"kept\":true"
                                     && probe.Contains "{\"name\":\"\",\"type\":\"Int64\",\"value\":\"6\"}" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 14b. ...and when the guard carries a span it is POSITIONED: the
        // `  --> file:line` the panic prints is remapped onto the cell (a
        // struct constraint reports its `where` line, the cell's 4th), and a
        // span in an EARLIER cell says "elsewhere in session" rather than
        // squiggling this one. (A guard with no span -- 1 / 0 above -- stays
        // at 1:1, which is honest.)
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let a = 1.0\nstruct Pos {\n    v: Float64\n} where v >= 0.0\nlet p = Pos { v = a - 5.0 }"
                    evalReq 2 "nb2" "struct Pos {\n    v: Float64\n} where v >= 0.0"
                    evalReq 3 "nb2" "let p = Pos { v = -5.0 }"
                    shutdownReq ]
        let name = "a runtime panic is positioned at its cell line with its code"
        match responses with
        | [here; _; elsewhere] when code = 0
                       && here.Contains "\"kept\":false"
                       && here.Contains "\"severity\":\"error\",\"line\":4,"
                       && here.Contains "\"message\":\"Constraint violation in Pos\",\"code\":\"BL8001\""
                       && elsewhere.Contains "\"message\":\"elsewhere in session: Constraint violation in Pos\",\"code\":\"BL8001\"" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 15. MIXED CELLS. A notebook cell is prose-driven and routinely ends a
        // run of declarations with the expression that shows what they did.
        // Classified as one declaration the cell passed through whole and the
        // file grammar rejected its last line (BL1999 "Expected declaration");
        // classified as one expression its FIRST line would have been the thing
        // that failed. It is neither: it is three statements -- and the cell
        // displays exactly ONE value, its trailing expression's.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let t1 = 2\nlet t2 = 3\nt1 + t2"
                    evalReq 2 "nb" "t2"; shutdownReq ]
        let name = "a cell mixing declarations with a trailing expression runs whole"
        match responses with
        | [mixed; after] when code = 0
                              && mixed.Contains "\"kept\":true" && mixed.Contains "\"diagnostics\":[]"
                              && mixed.Contains "\"bindings\":[{\"name\":\"\",\"type\":\"Int64\",\"value\":\"5\"}]"
                              // The declarations JOINED the session; only the
                              // expression was transient.
                              && after.Contains "{\"name\":\"\",\"type\":\"Int64\",\"value\":\"3\"}" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 16. Interleaving: every statement runs where the user wrote it (the
        // mid-cell expression too -- its effects land), but only the FINAL
        // statement displays, and here the final statement is `r * 2`. A
        // whole-array `bindings` equality, because "exactly one echo" is the
        // property under test.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let p = 10\nlet q = p * 2\nq + 1\nlet r = q + p\nr * 2"
                    shutdownReq ]
        let name = "an interleaved cell displays only its final expression"
        let expectedOrder =
            "\"bindings\":[{\"name\":\"\",\"type\":\"Int64\",\"value\":\"60\"}]"
        match responses with
        | [r] when code = 0 && r.Contains "\"kept\":true" && r.Contains expectedOrder ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 17. The regression the split must not cause: an expression-only cell
        // still evaluates against the session without joining it. Only the
        // last expression displays; the earlier one runs for effect.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let e1 = 4"; evalReq 2 "nb" "e1 + 1\ne1 * 2"
                    evalReq 3 "nb" "e1 + 1\ne1 * 2"; shutdownReq ]
        let name = "an expression-only cell echoes its final value and joins nothing"
        let finalValue =
            "\"bindings\":[{\"name\":\"\",\"type\":\"Int64\",\"value\":\"8\"}]"
        match responses with
        | [_; first; again] when code = 0
                                 && first.Contains "\"kept\":true" && first.Contains finalValue
                                 // Re-running echoes again rather than diffing
                                 // to silence -- nothing was kept to diff against.
                                 && again.Contains finalValue ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 18. Re-running a mixed cell REPLACES its earlier contribution. Each
        // declaration supersedes its own predecessor in place, so nothing is
        // declared twice (which would not compile) and the expressions -- being
        // transient -- leave no second copy behind either.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let m = 1\nlet n = m + 1\nn * 10"
                    evalReq 2 "nb" "let m = 5\nlet n = m + 1\nn * 10"
                    evalReq 3 "nb" "n"; shutdownReq ]
        let name = "re-running a mixed cell replaces it instead of redeclaring it"
        match responses with
        | [first; again; after] when code = 0
                                     && first.Contains "{\"name\":\"\",\"type\":\"Int64\",\"value\":\"20\"}"
                                     && again.Contains "\"kept\":true"
                                     && again.Contains "\"diagnostics\":[]"
                                     && again.Contains "{\"name\":\"\",\"type\":\"Int64\",\"value\":\"60\"}"
                                     && after.Contains "{\"name\":\"\",\"type\":\"Int64\",\"value\":\"6\"}" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 19. THE reason a mixed cell is split into statements rather than
        // wrapped in place. `bindingName` reads a snippet's FIRST name, so a
        // whole-cell snippet holding `g1` and `g2` answers to `g1` alone -- and
        // a later cell rebinding `g1` would supersede the snippet entire,
        // taking `g2` down with it and leaving every downstream cell unbound.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let g1 = 1\nlet g2 = 2\ng1 + g2"
                    evalReq 2 "nb" "let mut g1 = 7"
                    evalReq 3 "nb" "g1 + g2"; shutdownReq ]
        let name = "rebinding one name of a mixed cell leaves its other names standing"
        match responses with
        | [_; rebind; after] when code = 0
                                  && rebind.Contains "\"kept\":true"
                                  && after.Contains "{\"name\":\"\",\"type\":\"Int64\",\"value\":\"9\"}" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 20. A call for EFFECT is a statement too. Its hidden wrapper is a
        // binding whose value is the call, so the call runs where it was
        // written and its `mut` write lands in the caller's buffer -- which the
        // read on the next line of the SAME cell has to see.
        let (code, responses, _) =
            drive [ evalReq 1 "nb"
                        ("let mut buf = [0.0, 0.0, 0.0]\n"
                         + "function fill(out: mut Array<Float like Idx<3>>) = {\n"
                         + "    out(0) += 1.5\n}\nfill(buf)\nbuf")
                    shutdownReq ]
        let name = "a call-for-effect statement mutates what the next statement reads"
        match responses with
        | [r] when code = 0 && r.Contains "\"kept\":true"
                   && r.Contains "{\"name\":\"\",\"type\":\"Array<Float64 like Idx<3>>\",\"value\":\"[1.5, 0.0, 0.0]\"}" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 21. The split's other half: a newline the PARSER goes on to skip is
        // not a statement boundary. A `where` clause on its own line is the
        // shape that proves it -- split there and both halves are nonsense.
        let (code, responses, _) =
            drive [ evalReq 1 "nb"
                        ("function csum(a: T^1, b: T^1)\nwhere comm(a, b) = {\n    a + b\n}\n"
                         + "csum([1.0, 2.0], [3.0, 4.0])")
                    shutdownReq ]
        let name = "a where clause on its own line does not start a new statement"
        match responses with
        | [r] when code = 0 && r.Contains "\"kept\":true"
                   && r.Contains "\"value\":\"[4.0, 6.0]\"" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 22. Same rule over a recursive array, whose `match`/`|` arms all sit
        // at depth 0 -- plus the name it binds. `let rec` is a `let` with a
        // modifier, and while `bindingNameRe` did not spell `rec` the notebook
        // echoed a binding literally called `rec`, with no type and no value.
        let (code, responses, _) =
            drive [ evalReq 1 "nb"
                        ("let rec seq: Array<Float like Idx<4>> =\n    match seq with\n"
                         + "    | zero -> zero\n    | zero :: s -> zero :: 1.0\n"
                         + "    | prefix :: n -> prefix :: prefix(n - 1) * 0.5 + 1.0\n"
                         + "reduce(seq, (+))")
                    evalReq 2 "nb" "seq"; shutdownReq ]
        let name = "a let rec statement stays whole and binds its own name"
        match responses with
        | [r; probe] when code = 0 && r.Contains "\"kept\":true"
                          && not (r.Contains "\"name\":\"rec\"")
                          && r.Contains "{\"name\":\"\",\"type\":\"Float64\",\"value\":\"6.125\"}"
                          // The rebindable name is `seq`, not `rec`: a later
                          // bare-identifier cell reads the array back.
                          && probe.Contains "{\"name\":\"\",\"type\":\"Array<Float64 like Idx<4>>\"" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 23. `()` subscripts an array in Blade (`[]` is tuple access), so an
        // element write is `arr(0) = ...`. Missing from the reassignment
        // pattern it read as a bare expression, was wrapped in a TRANSIENT
        // binding, and its write left the session with the wrapper.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let arr = [1.0, 2.0, 3.0]\narr(0) = 9.0"
                    evalReq 2 "nb" "arr"; shutdownReq ]
        let name = "an element write is a reassignment and persists in the session"
        match responses with
        | [write; after] when code = 0
                              && write.Contains "\"kept\":true"
                              // A reassignment is a statement: silent, and the
                              // write persists for the next cell to read.
                              && write.Contains "\"bindings\":[]"
                              && after.Contains "\"value\":\"[9.0, 2.0, 3.0]\"" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 24. A mixed cell that FAILS is still a rejection like any other: no
        // bindings claimed, the session untouched, and the diagnostic in the
        // CELL's coordinates -- which is what the per-statement placements are
        // for, since each statement is now its own snippet in the session file.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let keepme = 1"
                    evalReq 2 "nb" "let bad1 = 1\nlet bad2 = undefined_name_xyz + 1\nbad1"
                    evalReq 3 "nb" "keepme"; shutdownReq ]
        let name = "a failing mixed cell reports cell-local spans and keeps nothing"
        match responses with
        | [_; bad; after] when code = 0
                               && bad.Contains "\"kept\":false" && bad.Contains "\"bindings\":[]"
                               && bad.Contains "\"severity\":\"error\",\"line\":2,\"col\":12"
                               && not (bad.Contains "elsewhere in session")
                               && after.Contains "{\"name\":\"\",\"type\":\"Int64\",\"value\":\"1\"}" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 25. A cell containing only the NAME of a generic kernel. The cell
        // lowers to a function-VALUE binding, which was a reachability root for
        // dead-polymorph elimination -- so it kept the generic and everything
        // its body calls alive with no call site to pin the type vars, and the
        // cell came back with a spray of BL6001 "unresolved type variable"
        // errors naming `mean` and two lifted lambdas. A cell that echoes a
        // function must not be able to fail IR validation.
        //
        // What is pinned is the CONTRACT, not the spelling of the rendered
        // type: the cell succeeds and carries NO diagnostics. The bare
        // reference pins the type echo the checker produces, which is what a
        // client actually displays.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "from stats import mean"
                    evalReq 2 "nb" "function covariance(a: T^1, b: T^1) where comm(a, b) = { (a - mean(a)) * (b - mean(b)) }"
                    evalReq 3 "nb" "covariance"
                    evalReq 4 "nb" "let after = 6"
                    shutdownReq ]
        let name = "a bare reference to an unapplied generic echoes without diagnostics"
        match responses with
        | [_; decl; bare; after] when code = 0
                                      && decl.Contains "\"diagnostics\":[]"
                                      && bare.Contains "\"exitCode\":0"
                                      && bare.Contains "\"diagnostics\":[]"
                                      // stats.mean returns Float64 (it averages
                                      // Int rows in double), so `a - mean(a)` is a
                                      // row broadcast and the echo shows the shaped
                                      // signature with its Float64 result.
                                      && bare.Contains "{\"name\":\"covariance\",\"type\":\"(Array<T like Idx<_>>, Array<T like Idx<_>>) -> Array<Float64 like Idx<_>>\",\"value\":\"\"}"
                                      && not (bare.Contains "BL6001")
                                      // The session survives it: a later cell
                                      // still evaluates against the same state.
                                      && after.Contains "\"kept\":true" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 26. ...and it PRESENTS like the declaration it echoes. A bare
        // function reference is answered from the declaration's own binding --
        // its name, and the checker's rendering of the signature -- because
        // there is nothing else worth showing: the value has no printable form
        // and the transient wrapper's own IR type (`Arrow<T, T -> T>`) is the
        // engine's bookkeeping, not an answer to the question asked.
        //
        // Three spellings of the same request had three different answers.
        // `covariance` took the declaration path; `covariance // note` -- which
        // is how quickstart-1 section 10 writes it -- failed `identRe` against
        // the RAW text and fell through to the wrapper, reporting anonymously
        // and in raw IR; and `// note` ABOVE the name put the hidden `let it =`
        // on the comment line, where it swallowed the expression below and the
        // cell died with BL1999. All three now read as the declaration.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "function poly(a: T^1, b: T^1) where comm(a, b) = a + b"
                    evalReq 2 "nb" "poly"
                    evalReq 3 "nb" "poly   // : the commented spelling"
                    evalReq 4 "nb" "// a note above it\npoly"
                    evalReq 5 "nb" "function conc(x: Float64) -> Float64 = x + 1.0\nconc"
                    evalReq 6 "nb" "let plainval = 6\nplainval"
                    shutdownReq ]
        let name = "a bare function reference echoes the declaration, comments and all"
        let pretty = "{\"name\":\"poly\",\"type\":\"(T^1, T^1) -> T^1\",\"value\":\"\"}"
        match responses with
        | [_; bare; commented; noted; mixed; valueCell] when code = 0
                    && bare.Contains pretty && commented.Contains pretty
                    && noted.Contains pretty && noted.Contains "\"diagnostics\":[]"
                    // The same rule inside a MIXED cell, where the echo is one
                    // statement among several -- and reported ONCE, not twice,
                    // though two statements name it.
                    && mixed.Contains "\"bindings\":[{\"name\":\"conc\",\"type\":\"(Float64) -> Float64\",\"value\":\"\"}]"
                    // A bare identifier naming a VALUE keeps the anonymous
                    // echo: it has a printed value, which is the thing asked for.
                    && valueCell.Contains "{\"name\":\"\",\"type\":\"Int64\",\"value\":\"6\"}" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 27. STREAMED DISPLAY FRAMES (display-frames.md section 3). A cell
        // that runs for minutes wants its plot moving while it runs, so a
        // frame carrying the live-plot stream mime is forwarded the instant it
        // is produced as an out-of-band event line -- which the client parses
        // BEFORE the pending-id lookup, so it never settles the request.
        //
        // Three things are pinned, and the third is the one that costs if it
        // is wrong: the event line's exact shape, that a NON-stream frame is
        // untouched and still arrives in the response's `display` array, and
        // that a streamed frame is NOT also in that array. Section 3 forbids
        // delivering one frame twice, and the panel would draw it twice.
        let streamCell =
            "import display as d\nlet ok = d.emit_id(\"application/vnd.blade.plotstream.v1+json\", \"chan\", "
            + "\"{\\\"channel\\\":\\\"chan\\\",\\\"epoch\\\":-1,\\\"x\\\":[0],\\\"y\\\":[1]}\", "
            + "\"{\\\"stream\\\":true,\\\"backend\\\":\\\"plotly\\\"}\")"
        let plainCell =
            "import display as d\nlet png = d.emit(\"image/png\", \"AA==\")"
        let (code, responses, _) =
            drive [ evalReq 1 "nb" streamCell; evalReq 2 "nb" plainCell; shutdownReq ]
        let name = "a stream frame is forwarded live as an event line, not in display[]"
        let expectedEvent =
            "{\"event\":\"display\",\"id\":1,\"frame\":"
            + "{\"v\":1,\"mime\":\"application/vnd.blade.plotstream.v1+json\",\"encoding\":\"json\","
            + "\"data\":{\"channel\":\"chan\",\"epoch\":-1,\"x\":[0],\"y\":[1]},"
            + "\"meta\":{\"id\":\"chan\",\"stream\":true,\"backend\":\"plotly\"}}}"
        match responses with
        // The event precedes the response it belongs to: it is written as the
        // program runs, and the response only exists once the run is over.
        | [ev; streamed; replayEv; plain] when code = 0
                                               && ev = expectedEvent
                                               && streamed.Contains "\"id\":1" && streamed.Contains "\"kept\":true"
                                               && not (streamed.Contains "display")
                                               // A session re-runs every kept
                                               // cell, so eval 2 re-emits cell
                                               // 1's frame -- forwarded again
                                               // under ITS id. The id is
                                               // stable, so the panel merges
                                               // rather than appending.
                                               && replayEv.StartsWith "{\"event\":\"display\",\"id\":2,"
                                               && replayEv.Contains "\"id\":\"chan\""
                                               && plain.Contains "\"display\":[{\"v\":1,\"mime\":\"image/png\""
                                               && not (plain.Contains "plotstream") ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 28. THE SESSION MEMO across fresh domain elaboration. A cell whose
        // only novelty is a new `ml.*` op config makes MLElaborate splice its
        // generated function at the FRONT of the module, which shifts every
        // later declaration's lowering-pass SSA ids -- including the index-type
        // `Id`s inside an earlier ARRAY binding's type. The memo's type guard
        // compared those ids, so the whole session's arrays re-ran: measured at
        // 17 s/cell in a real notebook, and invisible from the outside, since a
        // memo hit and a recompute print the same value. `ad.grad` never showed
        // it because Grad splices each derivative beside its ROOT declaration
        // rather than at the front.
        //
        // Pinned on the adoption counts, because there is no other observable.
        // The last eval's tally is the one that survives to be read.
        let mlCell = "let static s2 = [(0, 0, 2)]\nlet lout = ml.linear(s2, s2, [2.0, 1.0, 0.0, 3.0], [5.0, 7.0])"
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "import ml as ml"
                    evalReq 2 "nb" "let base_a = [1.0, 2.0, 3.0]"
                    evalReq 3 "nb" mlCell
                    shutdownReq ]
        let adopted = Blade.Interp.Run.lastMemoAdopted
        let evaluated = Blade.Interp.Run.lastMemoEvaluated
        let name = "a new ml.* op in a later cell keeps the earlier binding's memo"
        match responses with
        | [_; _; ml] when code = 0 && ml.Contains "\"kept\":true" && ml.Contains "\"exitCode\":0"
                          // base_a adopted (initializer skipped); the ml cell's
                          // own two bindings are the only ones evaluated.
                          && adopted = 1 && evaluated = 2 ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, adopted %d, evaluated %d, responses: %A"
                                        code adopted evaluated responses)

        // ...and the value it kept is still the right one. Adoption is only
        // worth anything if the skipped initializer would have produced this.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "import ml as ml"
                    evalReq 2 "nb" "let base_a = [1.0, 2.0, 3.0]"
                    evalReq 3 "nb" mlCell
                    evalReq 4 "nb" "base_a"
                    shutdownReq ]
        let name = "a binding adopted across ml elaboration still reads back correctly"
        match responses with
        | [_; _; _; readback] when code = 0
                                   && readback.Contains "\"value\":\"[1.0, 2.0, 3.0]\"" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 29. The negative control for 28, and the line the fix must not
        // cross: a REBIND splices in place, so the cached snippet range no
        // longer matches and the whole memo goes. Shape agreement is not
        // permission to reuse a value whose defining text changed.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let base_a = [1.0, 2.0, 3.0]"
                    evalReq 2 "nb" "let derived = reduce(base_a, (+))"
                    evalReq 3 "nb" "let base_a = [4.0, 5.0, 6.0]"
                    shutdownReq ]
        let adopted = Blade.Interp.Run.lastMemoAdopted
        let name = "editing an earlier binding still drops the memo"
        match responses with
        | [_; _; rebind] when code = 0 && rebind.Contains "\"kept\":true" && adopted = 0 ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, adopted %d, responses: %A" code adopted responses)

        // ...and the dependent recomputes off the NEW value, not the cached one.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let base_a = [1.0, 2.0, 3.0]"
                    evalReq 2 "nb" "let derived = reduce(base_a, (+))"
                    evalReq 3 "nb" "let base_a = [4.0, 5.0, 6.0]"
                    evalReq 4 "nb" "derived"
                    shutdownReq ]
        let name = "a dependent of an edited binding recomputes from the new value"
        match responses with
        | [_; _; _; readback] when code = 0 && readback.Contains "\"value\":\"15.0\"" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 30. FRAME REPLAY. A binding that emits a display frame used to be
        // barred from the memo outright: it had to re-run so it could re-emit,
        // which made every plot in a session a fixed tax on EVERY later cell's
        // evaluation -- on a notebook that renders, most of the wall clock.
        // Now the frames travel in the memo beside the value and are replayed
        // in the binding's own position, so the plot costs nothing and the
        // run's frame sequence is the one it would have computed.
        //
        // Pinned on the adoption tally, because a memo hit and a recompute
        // produce the same frame -- which is the point, and the reason this
        // needs a counter to observe at all.
        let plotCell = "import display as d
let pic = d.emit(\"image/png\", \"AA==\")"
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let seed = [1.0, 2.0, 3.0]"
                    evalReq 2 "nb" plotCell
                    evalReq 3 "nb" "let tail_v = reduce(seed, (+))"
                    shutdownReq ]
        let adopted = Blade.Interp.Run.lastMemoAdopted
        let name = "a plot binding is adopted from the memo and its frame replayed"
        match responses with
        // seed and pic both adopted -- the emitter no longer forces a re-run --
        // and the replayed frame still reaches the response's display array.
        | [_; _; last] when code = 0 && last.Contains "\"kept\":true"
                           && adopted = 2
                           && last.Contains "\"display\":[{\"v\":1,\"mime\":\"image/png\"" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, adopted %d, responses: %A"
                                        code adopted responses)

        // ...and the line the replay must not cross. A cached PICTURE is as
        // stale as a cached value the moment the text above it changes: edit
        // the cell the plot reads and the memo must go, frames included, or
        // the panel shows a render of data no longer in the session. Pinned on
        // the tally rather than the frame, because `adopted = 0` says the
        // stronger thing -- nothing was reused, so nothing COULD be stale.
        let readingPlot =
            "import display as d
let total = reduce(seed, (+))
let shown = d.emit(\"image/png\", \"AA==\")"
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let seed = [1.0, 2.0, 3.0]"
                    evalReq 2 "nb" readingPlot
                    evalReq 3 "nb" "let seed = [10.0, 20.0, 30.0]"
                    shutdownReq ]
        let adopted = Blade.Interp.Run.lastMemoAdopted
        let name = "editing the data under a plot drops its cached frame too"
        match responses with
        | [_; _; rebind] when code = 0 && rebind.Contains "\"kept\":true" && adopted = 0 ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, adopted %d, responses: %A"
                                        code adopted responses)

        // ...and the replayed frame is the one the run would have computed,
        // not merely A frame: same id, same bytes, in the same position.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" plotCell; shutdownReq ]
        let firstRun = responses |> List.tryHead |> Option.defaultValue ""
        let (code2, responses2, _) =
            drive [ evalReq 1 "nb" plotCell
                    evalReq 2 "nb" "let unrelated = 1"
                    shutdownReq ]
        let name = "a replayed frame is byte-identical to the computed one"
        // Just the display array: the rest of the response legitimately
        // differs between the two evals (the second binds `unrelated`), and
        // the frame payload here carries no `]` of its own to confuse this.
        let frameOf (r: string) =
            let i = r.IndexOf "\"display\":["
            if i < 0 then "" else
            let j = r.IndexOf("]", i)
            if j < 0 then "" else r.Substring(i, j - i + 1)
        match responses2 with
        | [_; second] when code = 0 && code2 = 0
                           && frameOf firstRun <> ""
                           && frameOf second = frameOf firstRun ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d/%d, first %s, second %A"
                                        code code2 (frameOf firstRun) responses2)

        try Directory.Delete(tmpDir, true) with _ -> ()
    finally
        Directory.SetCurrentDirectory entryDir
    let count o = results |> Seq.filter (fun (_, r) -> r = o) |> Seq.length
    let passed, failed, skipped = count TH.Pass, count TH.Fail, count TH.Skip
    let failedNames = results |> Seq.filter (fun (_, r) -> r = TH.Fail) |> Seq.map fst |> List.ofSeq
    let parts =
        [ $"{passed} passed"; $"{failed} failed" ]
        @ (if skipped > 0 then [$"{skipped} skipped"] else [])
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
        let arr = cells |> List.map (fun c -> $"\"{esc c}\"") |> String.concat ","
        $"{{\"id\":{id},\"cmd\":\"checkCells\",\"tier\":\"{tier}\",\"file\":\"{(esc file)}\",\"cells\":[{arr}]}}"
    let evalReq (id: int) (session: string) (source: string) =
        $"{{\"id\":{id},\"cmd\":\"eval\",\"session\":\"{(esc session)}\",\"source\":\"{(esc source)}\"}}"
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
        else record name TH.Fail $"{diagCount body} diagnostics: {body}"

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
        // The HM binding is concrete in the typed AST itself now (the call
        // judgment instantiates `id` per call), so the full tier has nothing
        // to upgrade; what this pins is that the full tier still runs and
        // reports the concrete type.
        let name = "checkCells tier=full reports HM bindings concretely"
        if code = 0 && fullBody.Contains "\"tier\":\"full\""
           && fullBody.Contains "\"name\":\"r\"" && fullBody.Contains "\"type\":\"Int64\"" then
            record name TH.Pass ""
        else record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // 9. The malformed-request arms, in the same shape the other commands
        // answer with -- an old extension probing a new compiler reads these.
        let (code, responses, _) =
            drive [ "{\"cmd\":\"checkCells\",\"file\":\"a\",\"cells\":[]}"
                    "{\"id\":41,\"cmd\":\"checkCells\",\"cells\":[]}"
                    $"{{\"id\":42,\"cmd\":\"checkCells\",\"file\":\"{esc nbPath}\"}}"
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
        else record name TH.Fail ($"exit {code}, {(diagCount rebindBody)} diagnostics: {rebindBody}")

        // 10b. A cell that MIXES declarations with bare expressions. The eval
        // lane splits such a cell into statements; the check lane keeps it in
        // one contiguous window (the wire carries one window per cell) and
        // wraps each bare expression where it stands. Unwrapped, the assembled
        // source does not parse -- and one parse error is the answer for the
        // WHOLE notebook, so a single mixed cell used to blank every other
        // cell's hovers and squiggles.
        let mixedCells =
            [ "let mx = 2\nlet my = 3\nmx + my"
              "let mz = mx * my" ]
        let (code, responses, _) = drive [ cellsReq 71 "fast" nbPath mixedCells; shutdownReq ]
        let mixedBody = match responses with [r] -> r | _ -> ""
        let name = "a mixed declaration/expression cell parses and typechecks"
        match windowsOf mixedBody with
        | [ (s0, e0, Some wl, Some wc); _ ] when code = 0 && diagCount mixedBody = 0
                                                 // The wrapper sits on the
                                                 // cell's THIRD line, not its first.
                                                 && wl = s0 + 2 && wl <= e0 && wc > 0
                                                 && (bindingOf mixedBody "mz").IsSome
                                                 && (bindingOf mixedBody "__cell0").IsSome ->
            record name TH.Pass ""
        | ws -> record name TH.Fail (sprintf "exit %d, %d diagnostics, windows %A: %s"
                                             code (diagCount mixedBody) ws mixedBody)

        // ...and a cell with SEVERAL bare expressions takes one wrapper each,
        // numbered so they cannot collide. The window still reports only the
        // first (there is one wrap pair on the wire); what matters here is that
        // all of them parsed.
        let (code, responses, _) =
            drive [ cellsReq 72 "fast" nbPath [ "let ma = 1\nma + 1\nlet mb = 2\nmb + 1" ]
                    shutdownReq ]
        let multiBody = match responses with [r] -> r | _ -> ""
        let name = "several bare expressions in one cell each take their own wrapper"
        match windowsOf multiBody with
        | [ (s0, _, Some wl, Some _) ] when code = 0 && diagCount multiBody = 0
                                            && wl = s0 + 1
                                            && (bindingOf multiBody "__cell0_0").IsSome
                                            && (bindingOf multiBody "__cell0_1").IsSome ->
            record name TH.Pass ""
        | ws -> record name TH.Fail (sprintf "exit %d, %d diagnostics, windows %A: %s"
                                             code (diagCount multiBody) ws multiBody)

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
        [ $"{passed} passed"; $"{failed} failed" ]
        @ (if skipped > 0 then [$"{skipped} skipped"] else [])
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
            yield $"""{(r.GetProperty("name").GetString())} {(r.GetProperty("kind").GetString())} {defText} [{(String.concat " " uses)}]""" ]
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
            |> _.Replace("[", " ").Replace("]", " ").Split([|' '|], StringSplitOptions.RemoveEmptyEntries)
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
        [ $"{passed} passed"; $"{failed} failed" ]
        @ (if skipped > 0 then [$"{skipped} skipped"] else [])
    TH.printFooter blockName parts
    { TH.BlockResult.Block = blockName
      Passed = passed
      Failed = failed
      Skipped = skipped
      FailedNames = failedNames }

/// The single-file corpus category a `blade test <key>` names, as (display
/// name, tests), or None. Aliases first (display names and the historical
/// spellings), then ANY corpus directory by its literal name. Reject-only
/// directories come back marked "(rejects)" (Corpus.categoryTests), the one
/// classification RunAll's default suite and the interpreter differential use.
/// The corpus-wiring block asserts every directory on disk resolves here.
let internal resolveCategoryKey (cat: string) : (string * (string * string) list) option =
        match cat.ToLower().TrimStart('-') with
        | "basic" -> Some ("Basic", basicTests)
        | "intrinsics" -> Some ("Intrinsics", intrinsicsTests)
        | "casts" -> Some ("Casts", castsTests)
        | "ad" -> Some ("AD", adTests)
        | "ad-jvp" | "adjvp" -> Some ("AD JVP", adJvpTests)
        | "ad-jvp-comb" | "adjvpcomb" -> Some ("AD JVP Combinators", adJvpCombTests)
        | "loops" -> Some ("Loops", loopTests)
        | "symmetry" -> Some ("Symmetry", symmetryTests)
        | "reynolds" -> Some ("Reynolds", reynoldsTests)
        | "arity" -> Some ("Arity", arityTests)
        | "functions" -> Some ("Functions", functionTests)
        | "structs" -> Some ("Structs", structTests)
        | "struct-aborts" | "structaborts" -> Some ("Struct Aborts", structAbortTests)
        | "struct-mutual" | "mutual" -> Some ("Struct Mutual", structMutualTests)
        | "sum-types" | "sumtypes" -> Some ("Sum Types", sumTypeTests)
        | "interfaces" -> Some ("Interfaces", interfaceTests)
        | "modules" -> Some ("Modules", moduleTests)
        | "guards" -> Some ("Guards", guardTests)
        | "guard-combinators" | "guardcombinators" -> Some ("Guard Combinators", guardCombinatorTests)
        | "zero-combinators" | "zerocombinators" -> Some ("Zero Combinators", zeroCombinatorTests)
        | "sequence-combinators" | "sequencecombinators" -> Some ("Sequence Combinators", sequenceCombinatorTests)
        | "replicate" -> Some ("Replicate", replicateTests)
        | "anon-ranges" | "anonranges" -> Some ("Anonymous Ranges", anonRangeTests)
        | "recursive-arrays" | "recursivearrays" -> Some ("Recursive Arrays", recursiveArrayTests)
        | "segments" -> Some ("Segments", segmentsTests)
        | "tuple-views" | "tupleviews" -> Some ("Tuple Views", tupleViewTests)
        | "bracketed" -> Some ("Bracketed", bracketedTests)
        // The `Tuple<N>` surface layer (docs/plan-tuples-vs-arg-packs.md
        // 6b). Mixed category: positives plus "(rejects)" probes, so no
        // asRejectProbes wrapper.
        | "tuples" -> Some ("Tuples", tupleTests)
        | "index-types" | "indextypes" -> Some ("Index Types", indexTypeTests)
        | "static" -> Some ("Static", staticTests)
        | "units" -> Some ("Units", unitTests)
        | "unit-errors" | "uniterrors" -> Some ("Unit Errors", Blade.Tests.Corpus.categoryTests "unit-errors")
        | "mutability" -> Some ("Mutability", mutabilityTests)
        | "mutability-errors" | "mutabilityerrors" -> Some ("Mutability Errors", Blade.Tests.Corpus.categoryTests "mutability-errors")
        | "func-arrays" | "funcarrays" | "fa" -> Some ("Func Arrays", funcArrayTests)
        | "ppl" -> Some ("PPL", pplTests)
        | "math" -> Some ("Math", mathTests)
        | "rand" -> Some ("Rand", randTests)
        | "display" -> Some ("Display", Blade.Tests.Display.displayTests)
        | "display-errors" | "displayerrors" ->
            Some ("Display Errors", Blade.Tests.Corpus.categoryTests "display-errors")
        | "spectra" -> Some ("Spectra", spectraTests)
        | "fallback" -> Some ("Fallback", fallbackTests)
        | "stack-join" | "stackjoin" -> Some ("Stack/Join", stackJoinTests)
        | "sgs" -> Some ("SGS", sgsTests)
        | "ml-ops" | "mlops" -> Some ("ML Ops", mlOpsTests)
        | "ml-e2e" | "mle2e" -> Some ("ML E2E", mlE2eTests)
        | "ml-equiv" | "mlequiv" | "equiv" -> Some ("ML Equiv", mlEquivTests)
        // The full sql-* union, DISCOVERED like RunAll's default suite (a hand
        // list here silently missed unique-contains/semijoins/v24d-probes once).
        | "sqlish" | "sql" ->
            Some ("SQL-ish",
                  Blade.Tests.Corpus.singleFileCategories ()
                  |> List.filter (fun d -> d.StartsWith "sql-")
                  |> List.collect Blade.Tests.Corpus.categoryTests)
        | "deferred-concrete" | "deferredconcrete" -> Some ("Deferred Concrete", Blade.Tests.RunAll.deferredConcreteTests)
        | "memfree" -> Some ("Mem Free", Blade.Tests.RunAll.memfreeTests)
        | "memfree-stress" | "memfreestress" -> Some ("Mem Free Stress", Blade.Tests.RunAll.memfreeStressTests)
        | "trees" -> Some ("Trees", Blade.Tests.RunAll.treeTests)
        // Every other corpus directory answers to its LITERAL name
        // (`blade test inference-probes`, `blade test sql-group-by`), so no
        // category can lack a standalone key: the aliases above are spellings,
        // not the definition of what exists. Multi-file directories are the
        // `multifile` arm's.
        | key when List.contains key (Blade.Tests.Corpus.singleFileCategories ()) ->
            Some (key, Blade.Tests.Corpus.categoryTests key)
        | _ -> None

/// CORPUS WIRING: every directory under tests/corpus is consumed, by name, by
/// the lanes that claim to sweep the corpus. Part of the default suite.
///
/// A new corpus directory used to need three hand edits -- RunAll.allTests,
/// the key map above, and InterpDiff's slice -- and missing one was silent: the
/// directory simply never ran, and every summary line stayed green. The lists
/// are DERIVED from the directories on disk now; what is left to assert is
/// that the few hand-written facts still describe the disk: the exclusions
/// name real directories, every multi-file directory has a runner, and every
/// directory answers to `blade test <its-name>`.
let internal runCorpusWiringTests () : TH.BlockResult =
    let blockName = "Corpus Wiring"
    TH.printHeader "Corpus Wiring (every tests/corpus directory is consumed)"
    let results = ResizeArray<string * TH.Outcome>()
    let record name ok detail =
        let outcome = if ok then TH.Pass else TH.Fail
        TH.resultLine outcome name detail
        results.Add((name, outcome))
    let single = Blade.Tests.Corpus.singleFileCategories ()
    let multi = Blade.Tests.Corpus.multiFileCategories ()
    let onDisk = Set.ofList (single @ multi)
    let missing (names: string seq) = names |> Seq.filter (fun n -> not (onDisk.Contains n)) |> List.ofSeq
    let listed (xs: string list) = if xs.IsEmpty then "" else String.concat ", " xs
    record "the corpus root has categories" (single.Length > 0) $"{single.Length} single-file, {multi.Length} multi-file"
    // The default suite: single-file directories run in allTests unless
    // another block owns them; multi-file ones need a runner by name.
    let elsewhere = Blade.Tests.RunAll.corpusOwnedElsewhere |> List.map fst
    record "every directory another block owns exists" (missing elsewhere).IsEmpty (listed (missing elsewhere))
    let unrunMulti = multi |> List.filter (fun d -> not (List.contains d Blade.Tests.RunAll.multiFileCategoriesRun))
    record "every multi-file directory has a runner in the default suite" unrunMulti.IsEmpty
        (if unrunMulti.IsEmpty then "" else $"no runner for: {listed unrunMulti} (add it to RunAll.multiFileCategoriesRun and wire its block)")
    record "every multi-file runner names a directory" (missing Blade.Tests.RunAll.multiFileCategoriesRun).IsEmpty
        (listed (missing Blade.Tests.RunAll.multiFileCategoriesRun))
    // The one reject-only definition.
    record "every reject-only category exists" (missing Blade.Tests.Corpus.rejectOnlyCategories).IsEmpty
        (listed (missing Blade.Tests.Corpus.rejectOnlyCategories))
    // The interpreter differential: everything but its named exclusions.
    let interpExcluded = Blade.Tests.InterpDiff.interpExcluded |> List.map fst
    record "every interpreter-differential exclusion names a directory" (missing interpExcluded).IsEmpty
        (listed (missing interpExcluded))
    // `blade test <dir>`: every single-file directory resolves, to ITS OWN
    // tests (an alias wired to the wrong list would resolve to other names).
    let misKeyed =
        single |> List.filter (fun d ->
            match resolveCategoryKey d with
            | None -> true
            | Some (_, tests) ->
                let want = Blade.Tests.Corpus.categoryTests d |> List.map fst
                (tests |> List.map fst) <> want)
    record "every single-file directory answers to blade test <its-name>" misKeyed.IsEmpty
        (if misKeyed.IsEmpty then "" else $"unresolved or mis-wired: {listed misKeyed}")
    // Every diagnostic CODE a pin names is one the registry knows. A pin is
    // matched against what the compiler EMITS, so a code can be raised, pinned
    // and green for months while Diagnostics.Codes -- and with it `ide
    // surface` and the knowledge base -- has never heard of it (BL1003 and
    // BL1004 were). The registry-side tests cannot see that: they only walk
    // codes that are already registered.
    let codeRe = System.Text.RegularExpressions.Regex @"\bBL\d{4}\b"
    let sources =
        (single |> List.collect (fun d ->
            Blade.Tests.Corpus.category d |> List.map (fun (n, src) -> ($"{d}: {n}", src))))
        @ (multi |> List.collect (fun d ->
            Blade.Tests.Corpus.multiFileCategory d
            |> List.collect (fun (n, parts) -> parts |> List.map (fun (_, src) -> ($"{d}: {n}", src)))))
    let unregisteredPins =
        [ for (where, src) in sources do
            let errorCodes = Blade.Tests.Expect.parseDiagPins src |> fst |> List.map (fun p -> p.PinCode)
            let warnCodes = Blade.Tests.Expect.parseWarnPins src |> fst
            let abortCodes =
                Blade.Tests.Expect.parseAbortExpectations src
                |> List.collect (fun s -> [ for m in codeRe.Matches s -> m.Value ])
            for c in List.distinct (errorCodes @ warnCodes @ abortCodes) do
                if codeRe.IsMatch c && not (Blade.Diagnostics.Codes.isRegistered c) then
                    yield (c, where) ]
    record "every diagnostic code a corpus pin names is registered" unregisteredPins.IsEmpty
        (if unregisteredPins.IsEmpty then $"{sources.Length} files"
         else
            unregisteredPins
            |> List.groupBy fst
            |> List.map (fun (c, hits) -> $"{c} ({hits.Length} files, e.g. {snd hits.Head})")
            |> String.concat "; "
            |> fun s -> s + " -- add it to Diagnostics.Codes.registryEntries, protocol/data/diagnostics.json and regenerate protocol/surface.json")
    // The pin grammar every lane reads (the compiled run, the interpreter
    // differential, the diff oracles all go through Expect): a NESTED Bool
    // pin -- what a rank-2 comparison prints -- is an assertion, not a
    // malformed line, and it is judged on its elements against either
    // printed row form.
    let nestedBoolSrc = "// EXPECT: m = [[false, true], [false, false]]"
    let nestedPins = Blade.Tests.Expect.parseExpectedValues nestedBoolSrc
    record "a nested Bool pin parses to its row-major elements"
        (nestedPins = [ Blade.Tests.Expect.ExpectedArray1DBool ("m", [false; true; false; false]) ]
         && (Blade.Tests.Expect.parseMalformedExpectLines nestedBoolSrc).IsEmpty)
        $"%A{nestedPins}"
    let judge (out: string) = Blade.Tests.Expect.checkExpectedValues nestedPins out
    record "a nested Bool pin matches the nested and the flat printed forms"
        (judge "m = [[false, true], [false, false]]\n" = Ok ()
         && judge "m = [false, true, false, false]\n" = Ok ()) ""
    record "a nested Bool pin refuses a differing element and a differing count"
        ((match judge "m = [[false, true], [false, true]]\n" with Error _ -> true | Ok () -> false)
         && (match judge "m = [[false, true]]\n" with Error _ -> true | Ok () -> false)) ""
    let badNested = "// EXPECT: m = [[false, maybe], [true, false]]\n// EXPECT: n = [[true], []]"
    record "a nested Bool pin with a non-Bool leaf or an empty row is malformed, not dropped"
        ((Blade.Tests.Expect.parseMalformedExpectLines badNested).Length = 2
         && (Blade.Tests.Expect.parseExpectedValues badNested).IsEmpty) ""
    let count o = results |> Seq.filter (fun (_, r) -> r = o) |> Seq.length
    let passed, failed = count TH.Pass, count TH.Fail
    let failedNames = results |> Seq.filter (fun (_, r) -> r = TH.Fail) |> Seq.map fst |> List.ofSeq
    TH.printFooter blockName [ $"{passed} passed"; $"{failed} failed" ]
    { TH.BlockResult.Block = blockName
      Passed = passed
      Failed = failed
      Skipped = 0
      FailedNames = failedNames }

/// Run the full suite, appending the CLI smoke block and the strict-pin block
/// (which live in this file -- see runAllTestsFullWith's doc comment for why they're passed in).
let internal runFullSuite opts =
    runAllTestsFullWith
        [runCliSmokeTests; runStrictPinTests; runValidationCodeTests; runSurfacingTests; runSurfaceTests
         runIdeServeTests; runIdeEvalTests; runIdeCellsTests; runIdeReferencesTests
         runCorpusWiringTests] opts

/// The environment knobs that change what the compiler EMITS, how the program
/// is BUILT, or what it PRINTS -- every one read per call from the process
/// environment, so a developer's shell reaches straight into a test run.
/// `blade test` clears them all for its duration (and restores them after).
///
/// The suites were written against the pristine default, and that is a
/// correctness matter, not a preference: corpus EXPECT pins are exact printed
/// values (BLAS routes and reassociated folds are licensed to differ in the
/// last ULP; FP contraction likewise), emission-shape tests assert the default
/// loop nests and pass set (BLADE_FUSION / BLADE_CSE / ... off changes the
/// emitted text), `interp` / `diff-oracle` must never run gate-on
/// (MathElaborate: the synthesized Jacobi is the verification truth), and
/// BLADE_PRINT would silently drop every binding a pin reads. A developer whose
/// shell carried OPENBLAS_DIR for notebook work saw ~8 reds that vanished when
/// run "clean" -- the same shape as every knob below.
///
/// Blocks that exercise a knob set and restore it in-process, which this clear
/// does not disturb. Deliberately NOT here: BLADE_MEMCHECK (an ASan corpus
/// run is a real workflow -- it changes the build profile, not what a correct
/// program prints), BLADE_EXE_CACHE (where binaries are cached, not what they
/// are; the cache keys on the emitted text), BLADE_STDLIB, the toolchain
/// locators (BLADE_LLVM_CLANG, BLADE_TOOLCHAIN_FILE, BLADE_*_INCLUDE/LINK)
/// and BLADE_INTERP_TIMEOUT_MS (a harness diagnostic).
let internal suiteClearedKnobs =
    [ "OPENBLAS_DIR"; "BLADE_BLAS"; "BLADE_CUBLAS"
      "BLADE_PRINT"
      "BLADE_FP_REASSOC"; "BLADE_FP_CONTRACT"; "BLADE_MARCH"
      "BLADE_OMP_THREADS"
      "BLADE_TILE_CACHE"; "BLADE_TILE_CACHE_VERBOSE"
      "BLADE_LLVM"; "BLADE_LLVM_BRICKS"; "BLADE_LLVM_FACTS"
      "BLADE_AD_HALO_GATHER"; "BLADE_SHAPE_SPEC_CAP"; "BLADE_RUN_RECORD" ]
    @ Blade.Optimize.optimizerGates

/// Whether `cat` names a corpus directory `test <lane> <cat>` can run: any
/// single-file category, plus the lane's own extra spellings (`opt-diff`
/// runs the multi-file corpus too; `llvm` takes its reserved words --
/// `all`, `goldens`, `facts`, ... -- from `LlvmTests.llvmCategoryWords`).
let private corpusCategoryExists (lane: string) (cat: string) : bool =
    let extras =
        match lane with
        | "opt-diff" | "optdiff" -> [ "multifile" ]
        | _ -> []
    List.contains cat extras
    || (lane = "llvm" && Blade.Tests.LlvmTests.isLlvmCategoryWord cat)
    || (try List.contains cat (Blade.Tests.Corpus.singleFileCategories ()) with _ -> false)

/// Dispatch the `test` subcommand. `rest` is everything after "test".
///
/// Runs with every `suiteClearedKnobs` variable UNSET, restoring the caller's
/// values afterwards, and says in one line which ones it found set. `--print`
/// is refused outright: it selects which bindings a program prints, and the
/// corpus pins read the bindings it would drop.
let rec internal dispatchTest (rest: string list) : int =
    // Cli.fs strips `--print <names>` from argv (it is a mode for every verb)
    // and pins BLADE_PRINT before dispatch, so the flag is recognised from the
    // process command line rather than from `rest`.
    let argv = System.Environment.GetCommandLineArgs()
    let testVerbAt = argv |> Array.tryFindIndex (fun a -> a = "test")
    let printFlag =
        match testVerbAt with
        // Cli.fs strips the pair from ANY position, so look everywhere.
        | Some _ -> argv |> Array.contains "--print"
        | None -> false
    if printFlag then
        eprintfn "Error: --print is not valid on `blade test`: the corpus pins read every top-level binding, and --print would silence the ones it does not name."
        eprintfn "       Select a category instead (`blade test <category>`), or run one program with `blade run prog.blade --print a,b`."
        2
    else
    let saved =
        suiteClearedKnobs
        |> List.distinct
        |> List.choose (fun k ->
            match System.Environment.GetEnvironmentVariable k with
            | null -> None
            | v -> Some (k, v))
    if not saved.IsEmpty then
        let names = saved |> List.map (fun (k, v) -> $"{k}={v}") |> String.concat " "
        eprintfn "test: ignoring ambient %s for this run (the suites assume the default emission; blocks that exercise a knob set it themselves)" names
    for (k, _) in saved do System.Environment.SetEnvironmentVariable(k, null)
    try dispatchTestClean rest
    finally
        for (k, v) in saved do System.Environment.SetEnvironmentVariable(k, v)

/// `dispatchTest` after the environment is made pristine.
and internal dispatchTestClean (rest: string list) : int =
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
    | [ "validation-codes" ] | [ "validationcodes" ] ->
        // IR-validation backstops keep their own code at the CLI. In-process,
        // no toolchain; also part of the full suite.
        let failed = (runValidationCodeTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "corpus-wiring" ] | [ "corpuswiring" ] ->
        // Every tests/corpus directory is consumed by the default suite, the
        // interpreter differential and a `blade test <dir>` key. In-process,
        // no toolchain; also part of the full suite.
        let failed = (runCorpusWiringTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "docs" ] ->
        // Every ```blade block in CLAUDE.md and docs/: checked through the
        // corpus front end, run when it pins values, refused when tagged
        // `rejects`; `sketch` blocks are skipped (tests/DocTests.fs).
        let failed = (Blade.Tests.DocTests.runDocTests None).Failed
        if failed = 0 then 0 else 1
    | [ "docs"; page ] ->
        // Only the pages whose repo-relative path contains `page`.
        let failed = (Blade.Tests.DocTests.runDocTests (Some page)).Failed
        if failed = 0 then 0 else 1
    | [ "surfacing" ] ->
        // Warning/suggestion surfacing: codes, streams, and survival of the checker's error path.
        let failed = (runSurfacingTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "surface" ] ->
        // The language-surface dump: renderer shape, the serve arm, and the
        // committed protocol/ snapshots (freshness + the diagnostics KB).
        let failed = (runSurfaceTests ()).Failed
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
        let tierFailed = (Blade.Tests.LinAlgTests.runBlasTierTests ()).Failed
        if emitFailed + probeFailed + tierFailed = 0 then 0 else 1
    | [ "doctor" ] ->
        // Structural pins for the doctor's rows and JSON shape; the probes
        // run for real but machine-dependent statuses are not asserted.
        let failed = (Blade.Tests.DoctorTests.runDoctorTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "setup" ] ->
        // `blade setup`'s pure halves: argument parsing + the toolchain-file
        // merge/remove roundtrip. No network, no git, no make.
        let failed = (Blade.Tests.SetupTests.runSetupTests ()).Failed
        if failed = 0 then 0 else 1
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
    | [ "flatpath" ] | [ "flat-path" ] ->
        // Which index tags reach the flat elementwise path and which decline.
        // Pure lowering + codegen, no toolchain.
        let failed = (Blade.Tests.FlatPathTests.runFlatPathTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "access" ] ->
        // The halo access record and the reverse-mode halo route
        // differential: gather vs scatter, byte-for-byte on the same
        // programs. Emission pins run everywhere; the differential needs g++.
        let failed = (Blade.Tests.AccessTests.runAccessTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "rand-mirror" ] ->
        // The RNG mirror's Philox4x32-10 generator against Random123's
        // published known-answer vectors, and the `_at` address identities.
        let failed = (Blade.Tests.RandMirrorTests.runRandMirrorTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "optimize" ] ->
        // The semantic-equivalence layer's emission pins: freeze-idiom
        // recognition derives the break without the abort, `while` keeps
        // both, non-absorbing guards decline. Pure lowering + codegen.
        let failed = (Blade.Tests.OptimizeTests.runOptimizeTests ()).Failed
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
    | [ "gr-render" ] ->
        // The GR render lane: renderPlot's frame bytes and argument rules, and
        // the worker protocol against a fake helper the block writes itself.
        // No GR needed -- the one case that wants a real gr-render skips.
        let failed = (Blade.Tests.GrRender.runGrRenderTests ()).Failed
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
    // The differential lanes that take a LITERAL tests/corpus/<dir> name:
    // a name that is no such directory is a usage error, reported as one --
    // it used to escape as an exception from the corpus loader and surface as
    // BL9001, an "internal compiler error" for a typo.
    | [ ("diff-oracle" | "interp" | "opt-diff" | "optdiff" | "llvm") as lane; cat ]
            when not (corpusCategoryExists lane cat) ->
        let known =
            try Blade.Tests.Corpus.singleFileCategories () |> String.concat ", "
            with ex -> $"(the corpus itself was not found: {ex.Message})"
        eprintfn "error: unknown corpus category '%s' for `test %s` -- it takes a directory name under tests/corpus: %s" cat lane known
        1
    | [ "diff-oracle"; cat ] ->
        // Single corpus category against the pinned oracle.
        let failed = (Blade.Tests.DiffOracle.runDiffOracleTests "./oracle/Blade.exe" [cat]).Failed
        if failed = 0 then 0 else 1
    | [ "opt-diff" ] | [ "optdiff" ] ->
        // Optimizer differential: every corpus program (single-file
        // categories + multifile) with every Blade.Optimize gate OFF vs ON.
        // Emission-only for programs the optimizer leaves untouched; the
        // rest are compiled and run both ways (needs g++). Standalone only.
        let failed = (Blade.Tests.OptDiff.runOptDiffTests (Blade.Tests.Corpus.singleFileCategories ()) true).Failed
        if failed = 0 then 0 else 1
    | [ ("opt-diff" | "optdiff"); cat ] ->
        // One corpus category (the LITERAL tests/corpus/<dir> name).
        let failed =
            if cat = "multifile" then (Blade.Tests.OptDiff.runOptDiffTests [] true).Failed
            else (Blade.Tests.OptDiff.runOptDiffTests [ cat ] false).Failed
        if failed = 0 then 0 else 1
    | [ "interp" ] ->
        // Interpreter differential gate: tree-walking IR interpreter vs the
        // compiled binary over the supported corpus slice -- byte-identical normalized stdout required.
        let failed = (Blade.Tests.InterpDiff.runInterpDiffTests (Blade.Tests.InterpDiff.currentSlice ())).Failed
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
    | [ "treerank" ] | [ "tree-rank" ] ->
        // TreeIdx shape validation, the derived preorder tables, and the
        // path <-> leaf-offset pair (src/TreeRank.fs), pinned against a
        // brute-force recursion over an independent nested-shape ADT as SET and ORDER, plus subtree contiguity.
        let failed = (Blade.Tests.TreeRankReview.runTreeRankTests ()).Failed
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
            + (Blade.Tests.CudaTests.runCublasFailureExitTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "mpi" ] ->
        // MPI decomposition block (differential vs serial oracle under
        // mpiexec -n 1/2/4). Skips without g++ / -lmsmpi / mpiexec.
        let failed = (Blade.Tests.MpiTests.runMpiTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "llvm" ] ->
        // The BLADE_LLVM lane: byte-pinned .ll emission (no toolchain needed)
        // plus the llvm-vs-C++ stdout differential over the scalar/functions/
        // loops corpora. STANDALONE ONLY -- deliberately absent from
        // isSuiteFlag and from FullSuiteOptions, so no combination of `blade
        // test` flags can fold it into the default suite: it spawns two native
        // compilers per corpus file. Skips cleanly without clang or g++.
        Blade.Tests.LlvmTests.runLlvmTests ()
    | [ "llvm-bench" ] | [ "llvmbench" ] ->
        // The two-lane benchmark: codegen speed (IRProgram -> executable, per
        // lane) and runtime over four non-power-of-two shapes. Standalone for
        // the same reason `llvm` is, only more so -- it spawns hundreds of
        // compiler and executable invocations. Never fails on a slow ratio;
        // fails only on a refusal, a build error or a value disagreement.
        Blade.Tests.LlvmTests.runLlvmBench ()
    | [ "llvm"; cat ] ->
        // The same differential over ONE corpus directory. Like
        // `test interp <dir>`, this takes the LITERAL tests/corpus/<dir> name
        // (`blade test llvm index-types`), not a dispatch alias key.
        Blade.Tests.LlvmTests.runLlvmCategory cat
    | [ "--llvm-backend" ] | [ "llvm-backend" ] ->
        // The ordinary suite with the CORPUS driven through the BLADE_LLVM lane
        // instead of the C++ emitter.
        //
        // Distinct from `blade test llvm`, and the distinction is the point.
        // That is a two-lane DIFFERENTIAL: it needs both compilers, and asks
        // whether the lanes agree. This runs ONE lane and judges it against the
        // corpus's own `// EXPECT:` pins -- no g++, one compiler per file, and
        // an answer about the LLVM lane that does not depend on the C++ lane
        // being right. Weaker per program (a pin covers only what it names) and
        // stronger in reach.
        //
        // A program the lane refuses SKIPS. It is never handed to the C++
        // emitter: `blade run`'s fallback is exactly wrong here, because it
        // would count C++ coverage as the LLVM lane's.
        match resolveClang () with
        | None ->
            eprintfn "test --llvm-backend: no clang found (set BLADE_LLVM_CLANG, or install C:\\msys64\\clang64)."
            eprintfn "      An error, not a skip: without clang EVERY corpus test would skip and the run would look green."
            1
        | Some clang ->
            printfn "Corpus back end: LLVM (clang: %s)" clang
            printfn "A program the lane refuses is SKIPPED, never handed to the C++ emitter, so the totals below are this lane's own.\n"
            setCorpusBackend LlvmBackend
            try runFullSuite defaultFullSuiteOptions
            finally setCorpusBackend CppBackend
    | [ "--llvm-backend"; cat ] | [ cat; "--llvm-backend" ] ->
        // The same back end over ONE category. This is the form you want while
        // growing the lane -- the whole suite is a long way to find out that a
        // single category still refuses everything -- and the form CI wants,
        // since a scoped lane fails fast and names what it covered.
        match resolveClang () with
        | None ->
            eprintfn "test --llvm-backend: no clang found (set BLADE_LLVM_CLANG, or install C:\\msys64\\clang64)."
            1
        | Some clang ->
            printfn "Corpus back end: LLVM (clang: %s), category: %s" clang cat
            printfn ""
            setCorpusBackend LlvmBackend
            try dispatchTestClean [ cat ]
            finally setCorpusBackend CppBackend
    | [ "--llvm" ] ->
        // Deliberately NOT a member of isSuiteFlag. Spelling it like one is a
        // reasonable guess given --omp/--cuda/--mpi, so say why it isn't
        // instead of letting it fall through to "Unknown test category".
        eprintfn "test: --llvm is not a full-suite flag -- the llvm lane spawns two native compilers per corpus file, so it is standalone only."
        eprintfn "      Run 'blade test llvm' (or 'blade test llvm all' for every corpus category,"
        eprintfn "      'blade test llvm <corpus-dir>' / 'blade test llvm goldens' / 'blade test llvm-bench')."
        1
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
    | [ "icechunk" ] ->
        // Icechunk provider tests. Fully hermetic; fixture repos are generated
        // on the fly by IcechunkWrite; only the e2e compile+run block needs
        // g++ and skips without it.
        Blade.Tests.IcechunkTests.runIcechunkTests ()
    | [ "run-record" ] | [ "runrecord" ] ->
        // Input manifests + run records (plan-fortran-killer-2 section 7).
        // Also in the default suite (RunAll.fs yields `runRecord`).
        let failed = (Blade.Tests.RunRecordTests.runRunRecordTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "provider-desugar" ] | [ "providerdesugar" ] ->
        // The icechunk checkout desugar (src/ProviderDesugar.fs). Pure
        // in-process AST rewrite; no toolchain, no fixtures, never skips.
        // Also in the default suite (RunAll.fs yields `providerDesugar`),
        // unlike the store-backed provider lanes.
        Blade.Tests.ProviderDesugarTests.runProviderDesugarTests ()
    | [ "hybrid" ] ->
        // Mixed-parallelism tests: order-table parse + gate-off degradation
        // run always; mpi+omp differentials need mpiexec and skip without it.
        Blade.Tests.HybridTests.runHybridTests ()
    | [ cat ] ->
        // Test a specific category: blade test basic, blade test loops, etc.
        let categoryTests = resolveCategoryKey cat
        match categoryTests with
        | Some (name, tests) ->
            let r = runTestCategoryFull name tests "./generated_cpp_tests"
            if r.Failed = 0 then 0 else 1
        | None -> eprintfn "Unknown test category: %s" cat; 1
    | _ -> usageFailure ($"""unrecognized test invocation: test {(String.concat " " rest)}""")

/// Top-level command dispatch.
