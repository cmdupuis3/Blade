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
/// The BLAS explicit tier's keys (BLADE_BLAS_LINK / _INCLUDE / _FLAVOR,
/// BLADE_LAPACK_LINK / _INCLUDE) are here too: BLADE_BLAS_LINK alone selects
/// that tier. Clearing is not enough for the BLAS gate, though: an unset
/// variable falls back to blade.toolchain.json (Toolchain.get), which
/// `blade setup` writes, so the suite would still run BLAS-on wherever setup
/// had recorded a BLAS install. `suitePinnedKnobs` therefore pins BLADE_BLAS
/// OFF for the run, which outranks every tier key from either source.
///
/// Blocks that exercise a knob set and restore it in-process, which this clear
/// does not disturb. Deliberately NOT here: BLADE_MEMCHECK (an ASan corpus
/// run is a real workflow -- it changes the build profile, not what a correct
/// program prints), BLADE_EXE_CACHE (where binaries are cached, not what they
/// are; the cache keys on the emitted text), BLADE_STDLIB, the non-BLAS
/// toolchain locators (BLADE_LLVM_CLANG, BLADE_TOOLCHAIN_FILE, NETCDF_DIR,
/// MSMPI_BIN) and BLADE_INTERP_TIMEOUT_MS (a harness diagnostic).
let internal suiteClearedKnobs =
    [ "OPENBLAS_DIR"; "BLADE_BLAS"; "BLADE_CUBLAS"
      "BLADE_BLAS_LINK"; "BLADE_BLAS_INCLUDE"; "BLADE_BLAS_FLAVOR"; "BLADE_BLAS_PREFER"
      "BLADE_LAPACK_LINK"; "BLADE_LAPACK_INCLUDE"
      "BLADE_PRINT"; "BLADE_PRINT_BITS"; "BLADE_MEMBER_VERBOSE"; "BLADE_JOIN_REPORT"
      "BLADE_FP_REASSOC"; "BLADE_FP_REPRO"; "BLADE_FP_CONTRACT"; "BLADE_MARCH"
      "BLADE_OMP_THREADS"
      "BLADE_TILE_CACHE"; "BLADE_TILE_CACHE_VERBOSE"
      "BLADE_LLVM"; "BLADE_LLVM_BRICKS"; "BLADE_LLVM_FACTS"
      "BLADE_AD_HALO_GATHER"; "BLADE_SHAPE_SPEC_CAP"; "BLADE_RUN_RECORD"
      "BLADE_SYRK_TILE"; "BLADE_ANTISYM_TILE"; "BLADE_SIMPLEX_TILE"; "BLADE_ORBIT_FOLD"
      "BLADE_DOMAIN_DIAGONAL"; "BLADE_TILE_TUNE"; "BLADE_FOLD_LANES"; "BLADE_FOLD_ROW_SLOTS"
      "BLADE_ORBIT_SCATTER"; "BLADE_ORBIT_PULL"; "BLADE_JOIN_THREADS"
      "BLADE_POOL_RETAIN" ]
    @ Blade.Optimize.optimizerGates

/// Knobs `blade test` SETS (after the clear above) rather than leaves unset,
/// because unset falls back to blade.toolchain.json. Blocks that test BLAS set
/// BLADE_BLAS themselves, which overrides this in their scope. The BLAS
/// preference is pinned to `native` (today's default) for the same reason: a
/// machine whose toolchain file says `library` -- or `BLADE_BLAS_FLAVOR=mkl`,
/// under which `auto` resolves to `library` -- would otherwise flip every
/// BLAS-on dispatch pin. The preference's own cases set it in their scope.
/// BLADE_POOL_RETAIN is pinned `off` (the default policy) for the same reason:
/// a toolchain file that persisted the retention arm would otherwise put its
/// defines into every program the suite emits.
let internal suitePinnedKnobs = [ "BLADE_BLAS", "off"; "BLADE_BLAS_PREFER", "native"; "BLADE_POOL_RETAIN", "off" ]

/// A program the LLVM lane REFUSES (a Float32 scalar: that lane lowers Int64 /
/// Float64 / Bool only), so `blade plan` under BLADE_LLVM=1 falls back to the
/// C++ lane -- the cli case that pins the pool-retention scope of a fallback.
let private poolFallbackSource = "let x = Float32(1.5)\nlet y = x * Float32(2.0)\n"

/// Run `f` in the suite's pristine environment: every `suiteClearedKnobs`
/// variable unset, every `suitePinnedKnobs` variable pinned, the caller's
/// values restored afterwards. Says in one line which ambient values it is
/// ignoring (unless `quiet`).
let internal withSuiteEnvironment (quiet: bool) (f: unit -> 'a) : 'a =
    let keys = (suiteClearedKnobs @ List.map fst suitePinnedKnobs) |> List.distinct
    let saved = keys |> List.map (fun k -> (k, System.Environment.GetEnvironmentVariable k))
    let ambient = saved |> List.filter (fun (_, v) -> not (isNull v))
    if not quiet && not ambient.IsEmpty then
        let names = ambient |> List.map (fun (k, v) -> $"{k}={v}") |> String.concat " "
        eprintfn "test: ignoring ambient %s for this run (the suites assume the default emission; blocks that exercise a knob set it themselves)" names
    for (k, _) in ambient do System.Environment.SetEnvironmentVariable(k, null)
    for (k, v) in suitePinnedKnobs do System.Environment.SetEnvironmentVariable(k, v)
    try f ()
    finally
        for (k, v) in saved do System.Environment.SetEnvironmentVariable(k, v)

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

    // --- the suite environment: BLAS stays off whatever the shell or the
    // toolchain file says. Clearing alone left two ways in: the explicit
    // tier's own keys (BLADE_BLAS_LINK selects it) and blade.toolchain.json,
    // which an UNSET variable falls back to.
    (let blasCase = "suite environment: BLAS off despite ambient tier keys and a toolchain file"
     let tcFile = Path.Combine(Path.GetTempPath(), $"blade_suite_env_{Guid.NewGuid():N}.json")
     let pinned = [ "BLADE_TOOLCHAIN_FILE", tcFile; "BLADE_BLAS_LINK", "-lmkl_rt"; "BLADE_BLAS", "1" ]
     let prior = pinned |> List.map (fun (k, _) -> (k, Environment.GetEnvironmentVariable k))
     try
        File.WriteAllText(tcFile, "{\"BLADE_BLAS\": \"1\", \"OPENBLAS_DIR\": \"C:/opt/openblas\", \"BLADE_BLAS_LINK\": \"-lopenblas\"}")
        Blade.Toolchain.refresh ()
        for (k, v) in pinned do Environment.SetEnvironmentVariable(k, v)
        let inside =
            withSuiteEnvironment true (fun () ->
                (Blade.LinAlgPatterns.resolveBlasTier (), Blade.LinAlgPatterns.blasAvailable (),
                 Environment.GetEnvironmentVariable "BLADE_BLAS_LINK"))
        let restored = Environment.GetEnvironmentVariable "BLADE_BLAS_LINK" = "-lmkl_rt"
                       && Environment.GetEnvironmentVariable "BLADE_BLAS" = "1"
        match inside with
        | (Blade.LinAlgPatterns.TierOff, false, null) when restored -> record blasCase TH.Pass ""
        | (tier, avail, link) ->
            record blasCase TH.Fail $"tier {tier}, blasAvailable {avail}, BLADE_BLAS_LINK inside = {link}, restored after = {restored}"
     finally
        for (k, v) in prior do Environment.SetEnvironmentVariable(k, v)
        (try File.Delete tcFile with _ -> ())
        Blade.Toolchain.refresh ())

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
    // A TUPLE and a STRUCT print now (spelled like their literals), so
    // selecting one prints it; a binding of a kind with NO printed form (a
    // sum-type value) is refused by name in both lanes -- it used to emit an
    // empty print block and exit 0.
    let aggSrc =
        "struct P {\n    a: Float64,\n    b: Float64\n}\n\
         type Direction = North | South\n\
         let t = (1, 2.0)\n\
         let p = P { a = 1.0, b = 2.0 }\n\
         let d = South\n"
    for sel in [ "t"; "p" ] do
        withPrint (Some sel) (fun () ->
            match cppOf ("print_agg_" + sel) aggSrc with
            | Error e -> record $"print: selecting the aggregate {sel} prints it" TH.Fail e
            | Ok cpp ->
                recordCase $"print: selecting the aggregate {sel} prints it"
                    (cpp.Contains $"cout << \"{sel} = \";" && not (cpp.Contains "#error")) "")
    withPrint (Some "d") (fun () ->
        (match cppOf "print_agg_d" aggSrc with
         | Error e -> record "print: selecting a binding with no printed form refuses" TH.Fail e
         | Ok cpp ->
             recordCase "print: selecting a binding with no printed form splices a refusal"
                 (cpp.Contains "#error" && cpp.Contains "d : " && cpp.Contains "has no printed form") "")
        (match Blade.Lowering.lower aggSrc with
         | Error e -> record "print: the interpreter refuses a binding with no printed form" TH.Fail e
         | Ok ir ->
             let r = Blade.Interp.Run.runProgram ir "print_agg_interp" Blade.Interp.Value.defaultLimits
             recordCase "print: the interpreter refuses a binding with no printed form the same way"
                 (r.ExitCode <> 0 && r.Stderr.Contains "has no printed form") r.Stderr))
    // Elaborator-internal `__` bindings (`__ppl_cen_A`, `__dist_dx_k1`) are not
    // echoed by default -- a ppl program printed a screenful of them -- but a
    // bare expression's `__expr<n>` is the user's value and prints, and a
    // selected internal name prints.
    let internalSrc = "let __hidden = 1\nlet shown = 2\n3 + 4\n"
    (match cppOf "internal_names" internalSrc with
     | Error e -> record "print: elaborator-internal bindings are not echoed" TH.Fail e
     | Ok cpp ->
         recordCase "print: elaborator-internal bindings are not echoed (a bare expression still is)"
             (cpp.Contains "\"shown = \"" && not (cpp.Contains "\"__hidden = \"") && cpp.Contains "\"__expr") "")
    withPrint (Some "__hidden") (fun () ->
        match cppOf "internal_names_sel" internalSrc with
        | Error e -> record "print: a selected internal binding prints" TH.Fail e
        | Ok cpp -> recordCase "print: a selected internal binding prints" (cpp.Contains "\"__hidden = \"") "")
    (match Blade.Lowering.lower internalSrc with
     | Error e -> record "print: the interpreter hides internal bindings too" TH.Fail e
     | Ok ir ->
         let r = Blade.Interp.Run.runProgram ir "internal_names_interp" Blade.Interp.Value.defaultLimits
         let out = r.Stdout.Replace("\r\n", "\n")
         recordCase "print: the interpreter hides internal bindings too"
             (out.Contains "shown = 2" && not (out.Contains "__hidden") && out.Contains "__expr") out)
    // A runtime GUARD panic (here the computed-row extent check, BL8006) is
    // located at the declaration being emitted: it used to pass `nullptr, 0`,
    // which a notebook showed at 1:1. (No file here -- the source is a string
    // -- so the location is the line alone; `blade run` adds the file.)
    (match cppOf "guard_line" "let n = reduce([1, 2], (+))\nlet row = Float64(0..n)\nlet final: Array<Float64 like Idx<2>, Idx<4>> = [row, row]\n" with
     | Error e -> record "guards: a runtime guard panic carries its declaration's line" TH.Fail e
     | Ok cpp ->
         recordCase "guards: a runtime guard panic carries its declaration's line"
             (cpp.Contains "\"array literal extent mismatch\", nullptr, 3)" && not (cpp.Contains "\"array literal extent mismatch\", nullptr, 0)")) "")
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
    let rec spawn (cwd: string) (args: string list) : int * string * string = spawnWith [] cwd args
    /// `spawn` with extra environment for the CHILD only (null unsets), so a
    /// knob a case needs never leaks into this process's other blocks.
    and spawnWith (env: (string * string) list) (cwd: string) (args: string list) : int * string * string =
        match selfExe with
        | None -> (-1, "", "no Blade executable beside the test assembly")
        | Some exe ->
            let psi = System.Diagnostics.ProcessStartInfo(exe)
            for (k, v) in env do
                if isNull v then psi.Environment.Remove k |> ignore else psi.Environment.[k] <- v
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
            // BLADE_POOL_RETAIN (round-3 A4, gap G52): the plan's build line
            // names the runtime pool-retention policy -- the default, the
            // opt-in arm with its per-thread honesty, a malformed value read
            // as the default (with a warning line) -- and the JSON carries it.
            // Every case names its toolchain file (an isolated fixture): an
            // UNSET variable falls back to the file, and a machine whose
            // blade.toolchain.json persisted the arm must not move these pins.
            let emptyToolchain = Path.Combine(dir, "pool_tc_empty.json")
            File.WriteAllText(emptyToolchain, "{}")
            let armToolchain = Path.Combine(dir, "pool_tc_retain1.json")
            File.WriteAllText(armToolchain, "{\"BLADE_POOL_RETAIN\": \"1\"}")
            let poolEnv (file: string) (v: string) = [ ("BLADE_TOOLCHAIN_FILE", file); ("BLADE_POOL_RETAIN", v); ("BLADE_LLVM", null) ]
            let buildLine (out: string) =
                out.Replace("\r\n", "\n").Split('\n') |> Array.tryFind (fun l -> l.StartsWith "build: ") |> Option.defaultValue "<none>"
            let defaultLine = "build: pool retention: default (4 MB / 8 MB per thread)"
            let (codeU, outU, errU) = spawnWith (poolEnv emptyToolchain null) dir [ "plan"; "cov.blade" ]
            let (codeO, outO, errO) = spawnWith (poolEnv emptyToolchain "off") dir [ "plan"; "cov.blade" ]
            recordCase "cli: `plan` build line for an unset and an `off` default is exactly the plain default line (no provenance)"
                (codeU = 0 && codeO = 0 && buildLine outU = defaultLine && buildLine outO = defaultLine)
                ($"unset: '{buildLine outU}' / off: '{buildLine outO}' {errU}{errO}")
            let (code, out, err) = spawnWith (poolEnv emptyToolchain "1") dir [ "plan"; "cov.blade" ]
            recordCase "cli: `plan` build line names the BLADE_POOL_RETAIN=1 arm in the C++ lane, per thread"
                (code = 0 && buildLine out = "build: pool retention: BLADE_POOL_RETAIN=1 (env BLADE_POOL_RETAIN) -> 64 MB / 96 MB per thread (opt-in arm, C++ lane blade_pool; a released pool of 16 KB .. 64 MB stays committed until reuse, eviction, flush or thread exit, so T threads may retain T x 96 MB and the peak working set can rise without any reuse; it moves the release out of the timed region, it is not less work; the run record's policy.pool says which arm ran)")
                (buildLine out + err)
            let (code, out, err) = spawnWith (poolEnv armToolchain null) dir [ "plan"; "cov.blade" ]
            recordCase "cli: `plan` reads the arm from the toolchain file when the variable is unset"
                (code = 0 && (buildLine out).StartsWith "build: pool retention: BLADE_POOL_RETAIN=1 (blade.toolchain.json) -> 64 MB / 96 MB per thread")
                (buildLine out + err)
            let (code, out, err) = spawnWith (poolEnv armToolchain "off") dir [ "plan"; "cov.blade" ]
            recordCase "cli: `plan` lets env `off` override a toolchain file's arm"
                (code = 0 && buildLine out = defaultLine) (buildLine out + err)
            let (code, out, err) = spawnWith (poolEnv emptyToolchain "16:48") dir [ "plan"; "cov.blade"; "--json" ]
            recordCase "cli: `plan --json` carries an explicit retention pair, per-thread in the C++ lane"
                (code = 0 && out.Contains "\"build\":{\"pool_retain\":{\"arm\":\"pair\",\"setting\":\"16:48\""
                 && out.Contains "\"max_block\":16777216,\"budget\":50331648,\"budget_scope\":\"per-thread\",\"runtime\":\"cpp-blade_pool\"")
                (out + err)
            let (code, out, err) = spawnWith (poolEnv emptyToolchain "96:64") dir [ "plan"; "cov.blade" ]
            recordCase "cli: `plan` reads a malformed retention pair (max > budget) as the default, with a warning"
                (code = 0 && (buildLine out).StartsWith "build: pool retention: default (4 MB / 8 MB per thread) -- BLADE_POOL_RETAIN='96:64'"
                 && err.Contains "warning: BLADE_POOL_RETAIN='96:64'")
                (out + err)
            // The LLVM lane holds its pools in the shim allocator: ONE process
            // budget, an atexit flush, and no run record. The scope follows
            // the lane that BUILDS the program -- an LLVM refusal falls back to
            // the C++ lane, and so does the scope.
            if (Blade.Build.resolveClang ()).IsNone then
                record "cli: `plan` under BLADE_LLVM=1 scopes the pool budget per process" TH.Skip "no clang (the LLVM lane is unavailable)"
            else
                let llvmEnv (v: string) = [ ("BLADE_TOOLCHAIN_FILE", emptyToolchain); ("BLADE_POOL_RETAIN", v); ("BLADE_LLVM", "1") ]
                let (code, out, err) = spawnWith (llvmEnv "1") dir [ "plan"; "cov.blade" ]
                recordCase "cli: `plan` under BLADE_LLVM=1 scopes the arm to the shim's ONE process budget, no run record"
                    (code = 0 && out.Contains "lane: LLVM"
                     && (buildLine out).StartsWith "build: pool retention: BLADE_POOL_RETAIN=1 (env BLADE_POOL_RETAIN) -> 64 MB / 96 MB per process (opt-in arm, LLVM lane shim allocator: ONE 96 MB budget for the process"
                     && (buildLine out).Contains "atexit flush" && (buildLine out).Contains "writes NO run record"
                     && not ((buildLine out).Contains "per thread") && not ((buildLine out).Contains "T threads"))
                    (out + err)
                let (code, out, err) = spawnWith (llvmEnv "off") dir [ "plan"; "cov.blade" ]
                recordCase "cli: `plan` under BLADE_LLVM=1 names the default as the shim's process budget"
                    (code = 0 && out.Contains "lane: LLVM"
                     && buildLine out = "build: pool retention: default (4 MB / 8 MB per process, the LLVM lane's shim allocator)")
                    (buildLine out + err)
                let (code, out, err) = spawnWith (llvmEnv "16:48") dir [ "plan"; "cov.blade"; "--json" ]
                recordCase "cli: `plan --json` under BLADE_LLVM=1 says budget_scope process"
                    (code = 0 && out.Contains "\"lane\":\"llvm\""
                     && out.Contains "\"budget_scope\":\"process\",\"runtime\":\"llvm-shim\"" && not (out.Contains "per-thread"))
                    (out + err)
                File.WriteAllText(Path.Combine(dir, "pool_fallback.blade"), poolFallbackSource)
                let (code, out, err) = spawnWith (llvmEnv "1") dir [ "plan"; "pool_fallback.blade"; "--json" ]
                let (code2, out2, err2) = spawnWith (llvmEnv "1") dir [ "plan"; "pool_fallback.blade" ]
                recordCase "cli: `plan` under BLADE_LLVM=1 with an LLVM refusal keeps the C++ lane's per-thread scope"
                    (code = 0 && code2 = 0 && out.Contains "\"lane\":\"cpp\"" && out.Contains "\"budget_scope\":\"per-thread\",\"runtime\":\"cpp-blade_pool\""
                     && out2.Contains "but the LLVM lane refused" && (buildLine out2).Contains "96 MB per thread (opt-in arm, C++ lane blade_pool")
                    (out + err + out2 + err2)
            // LANE-CORRECT PLAN (bench gap G23 / G11). The same gram with the
            // BLAS gate forced on routes to syrk in the C++ lane; with
            // BLADE_LLVM=1 the LLVM lane takes the program, links no OpenBLAS
            // and opens no thread team, so the plan must say declined for both
            // -- never `[blas-routing v1] ...: applied`. The C++ twin (same
            // knobs minus BLADE_LLVM) is the control that keeps it non-vacuous.
            File.WriteAllText(Path.Combine(dir, "lane.blade"),
                "type V = Idx<7>\ntype S = Idx<11>\n"
                + "let x = method_for(range<V>, range<S>) <@> lambda(i, s) where omp(i: 1) -> Float64(i * 11 + s) * 0.25 |> compute\n"
                + "let g = method_for(x, x) <@> lambda(a: Array<Float64 like S>, b: Array<Float64 like S>) where comm(a, b) -> prodsum(a, b) |> compute\n")
            let laneEnv llvm = [ ("BLADE_BLAS", "1"); ("BLADE_LLVM", llvm); ("BLADE_OMP_THREADS", null); ("BLADE_FP_REPRO", null) ]
            let (code, out, err) = spawnWith (laneEnv null) dir [ "plan"; "lane.blade" ]
            recordCase "cli: `plan` (C++ lane) reports the BLAS route and the thread pragma applied"
                (code = 0 && out.Contains "[blas-routing v1] g" && out.Contains ": applied [Syrk" && not (out.Contains "lane: ")
                 && out.Contains "[omp v1] x" && not (out.Contains "the LLVM lane"))
                (out + err)
            if (Blade.Build.resolveClang ()).IsNone then
                record "cli: `plan` under BLADE_LLVM=1 reports the LLVM lane's decisions" TH.Skip "no clang (the LLVM lane is unavailable)"
            else
                let (code, out, err) = spawnWith (laneEnv "1") dir [ "plan"; "lane.blade" ]
                let lines = out.Split('\n') |> Array.map (_.Trim())
                let routeLines = lines |> Array.filter (fun (l: string) -> l.StartsWith "[blas-routing v1]")
                let ompLines = lines |> Array.filter (fun (l: string) -> l.StartsWith "[omp v1]")
                recordCase "cli: `plan` under BLADE_LLVM=1 reports the LLVM lane's decisions"
                    (code = 0 && out.Contains "lane: LLVM (BLADE_LLVM=1)"
                     && routeLines.Length > 0
                     && routeLines |> Array.forall (fun l -> l.Contains "declined -- the LLVM lane has no BLAS/LAPACK route")
                     && ompLines.Length > 0
                     && ompLines |> Array.forall (fun l ->
                            l.Contains "declined -- the LLVM lane has no thread-level form"
                            && l.Contains "[omp] requested but emitted serial"))
                    (out + err)
                let (code, out, err) = spawnWith (laneEnv "1") dir [ "plan"; "lane.blade"; "--json" ]
                recordCase "cli: `plan --json` under BLADE_LLVM=1 names the lane"
                    (code = 0 && out.Contains "\"lane\":\"llvm\"") (out + err)
                // THE ROW-SLOT FORM (G35) is the C++ lane's thread team; the
                // LLVM lane runs the same rows serially, and its plan says so
                // in its own words -- never the generic `reassoc nsz` line a
                // fold form it does not know would get. C++ twin as control.
                File.WriteAllText(Path.Combine(dir, "rowslots.blade"),
                    "type Var = Idx<521>\ntype Obs = Idx<3>\n"
                    + "let x = method_for(range<Var>, range<Obs>) <@> lambda(i, s) -> Float64((i * 7 + s * 3) % 11) * 0.37 |> compute\n"
                    + "let g = method_for(x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>) where comm(a, b) -> prodsum(a, b) |> compute\n"
                    + "let s = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> g(i, j) * g(i, j), (+)), lambda(u, v) where omp -> u + v, 0.0)\n")
                let slotEnvKnob llvm knob = [ ("BLADE_LLVM", llvm); ("BLADE_FP_REPRO", "1"); ("BLADE_OMP_THREADS", null); ("BLADE_FOLD_ROW_SLOTS", knob) ]
                let slotEnv llvm = slotEnvKnob llvm null
                // THE PRECEDENCE (round 3, W2-0a): this walk is one BOTH the row
                // slots and the ordered orbit scatter admit; by default the
                // scatter is taken and the row-slot record says it yielded.
                let (code, out, err) = spawnWith (slotEnv null) dir [ "plan"; "rowslots.blade" ]
                recordCase "cli: `plan` (C++ lane, BLADE_FP_REPRO=1) reports the ordered orbit scatter taken over the admitted row-slot form"
                    (code = 0 && out.Contains "applied [fold form: ordered orbit scatter (rank 2, extent 521, row groups of 4)"
                     && out.Contains ("declined -- row-slot form: " + Blade.OrbitFold.rowSlotsYieldedToScatter + " [" + Blade.OrbitFold.rowSlotsYieldedForm)
                     && out.Contains "the ordered orbit scatter runs serially (its chains thread only as a wavefront; the threaded complete-row form admitted but yielded"
                     && not (out.Contains "fold form: threaded complete rows (repro): 521"))
                    (out + err)
                // BLADE_FOLD_ROW_SLOTS=prefer: the round-2 order and its records.
                let (code, out, err) = spawnWith (slotEnvKnob null "prefer") dir [ "plan"; "rowslots.blade" ]
                recordCase "cli: `plan` (C++ lane, BLADE_FP_REPRO=1, BLADE_FOLD_ROW_SLOTS=prefer) reports the threaded complete-row form applied"
                    (code = 0 && out.Contains ": applied [fold form: threaded complete rows (repro): 521 row folds"
                     // G35 PRECEDENCE under prefer: the scatter the same walk admits is recorded as yielded
                     && out.Contains "declined -- ordered orbit scatter not taken: the threaded complete-row form (G35) is taken under the omp request (BLADE_FOLD_ROW_SLOTS=prefer")
                    (out + err)
                let (code, out, err) = spawnWith (slotEnv "1") dir [ "plan"; "rowslots.blade" ]
                // The LLVM lane (no threads, no row slots) is unchanged: its
                // emitter's own record says it scatters, and the C++ lane's
                // yielded row-slot record is restated in this lane's words.
                recordCase "cli: `plan` under BLADE_LLVM=1 restates the yielded row-slot form, and the LLVM emitter's own record says it scatters"
                    (code = 0 && out.Contains "declined -- the LLVM lane has no thread runtime (no threaded complete-row form); this lane's scatter record for the walk says which form it runs"
                     && out.Contains "applied [fold form: ordered orbit scatter (rank 2, extent 521, row groups of 4)"
                     && out.Contains "(recorded by the LLVM lane's emitter); C++ lane: applied"
                     && not (out.Contains "fold form: one accumulator chain with `reassoc nsz` (LLVM lane; the vectorizer picks the lanes); C++ lane fold form: ordered orbit scatter (repro"))
                    (out + err)
                let (code, out, err) = spawnWith (slotEnvKnob "1" "prefer") dir [ "plan"; "rowslots.blade" ]
                // ...and under prefer the C++ lane's row slots are restated as
                // the serial walk the LLVM lane scatters (both records say so).
                recordCase "cli: `plan` under BLADE_LLVM=1, BLADE_FOLD_ROW_SLOTS=prefer restates the row-slot form, and the LLVM emitter's own record says it scatters"
                    (code = 0 && out.Contains "declined -- the LLVM lane has no thread runtime: it runs this walk serially in the same association"
                     && out.Contains "applied [fold form: ordered orbit scatter (rank 2, extent 521, row groups of 4)"
                     && out.Contains "(recorded by the LLVM lane's emitter); C++ lane: declined -- ordered orbit scatter not taken: the threaded complete-row form (G35)"
                     && out.Contains "C++ lane: applied"
                     && not (out.Contains "fold form: one accumulator chain with `reassoc nsz` (LLVM lane; the vectorizer picks the lanes); C++ lane fold form: threaded complete rows"))
                    (out + err)
                // A walk the scatter declines (the body reads the mirror cell
                // too): the row slots are the C++ form by default, and the LLVM
                // lane's form is the serial walk.
                File.WriteAllText(Path.Combine(dir, "rowslots_walk.blade"),
                    File.ReadAllText(Path.Combine(dir, "rowslots.blade")).Replace("g(i, j) * g(i, j)", "g(i, j) * g(j, i)"))
                let (code, out, err) = spawnWith (slotEnv null) dir [ "plan"; "rowslots_walk.blade" ]
                recordCase "cli: `plan` (C++ lane, BLADE_FP_REPRO=1) keeps the threaded complete-row form where the scatter declines"
                    (code = 0 && out.Contains ": applied [fold form: threaded complete rows (repro): 521 row folds"
                     && not (out.Contains "admitted but yielded"))
                    (out + err)
                let (code, out, err) = spawnWith (slotEnv "1") dir [ "plan"; "rowslots_walk.blade" ]
                recordCase "cli: `plan` under BLADE_LLVM=1 restates the row-slot form as the LLVM lane's serial walk (scatter declined)"
                    (code = 0 && out.Contains "declined -- the LLVM lane has no thread runtime: it runs the serial walk"
                     && out.Contains "C++ lane: applied"
                     && not (out.Contains "fold form: one accumulator chain with `reassoc nsz` (LLVM lane; the vectorizer picks the lanes); C++ lane fold form: threaded complete rows"))
                    (out + err)
                // THE ROW-OWNED PULL (round 3, D-P3) is the C++ lane's thread
                // team over the rank-3 scatter's leaf chains: the C++ plan
                // says applied (form + omp record); under BLADE_LLVM=1 the
                // record is the LLVM emitter's own serial scatter (the C++
                // verdict trailing) and the team's omp record is declined;
                // the pull's decline (knob off) is restated as this lane's.
                File.WriteAllText(Path.Combine(dir, "pull3.blade"),
                    "type Var = Idx<13>\ntype Obs = Idx<2>\n"
                    + "let x = method_for(range<Var>, range<Obs>) <@> lambda(i, s) -> Float64((i * 3 + s * 5) % 7) - 2.0 |> compute\n"
                    + "let t = method_for(x, x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>, c: Array<Float64 like Obs>) where comm(a, b, c) -> prodsum(a, b, c) |> compute\n"
                    + "let s = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> reduce(method_for(range<Var>) <@> lambda(k) -> t(i, j, k) * t(i, j, k), (+)), (+)), lambda(u, v) where omp -> u + v, 0.0)\n")
                let pullEnv llvm knob = [ ("BLADE_LLVM", llvm); ("BLADE_FP_REPRO", "1"); ("BLADE_OMP_THREADS", null); ("BLADE_TILE_TUNE", "pull_min=0"); ("BLADE_ORBIT_PULL", knob) ]
                let (code, out, err) = spawnWith (pullEnv null null) dir [ "plan"; "pull3.blade" ]
                recordCase "cli: `plan` (C++ lane, BLADE_FP_REPRO=1) reports the row-owned pull applied with its team"
                    (code = 0 && out.Contains "row-owned pull on a thread team" && out.Contains "applied [omp parallel num_threads(T) + ascending row claims + team-completion barrier")
                    (out + err)
                let (code, out, err) = spawnWith (pullEnv "1" null) dir [ "plan"; "pull3.blade" ]
                recordCase "cli: `plan` under BLADE_LLVM=1 restates the pull as the LLVM emitter's serial scatter, its team declined"
                    (code = 0 && out.Contains "lane: LLVM"
                     && out.Contains "applied [fold form: ordered orbit scatter (rank 3, extent 13, row groups of 4, shared pair chains 91 of 169): one pass"
                     && out.Contains "(recorded by the LLVM lane's emitter); C++ lane: applied"
                     && not (out.Contains "row-owned pull on a thread team; ")
                     && out.Contains "declined -- the LLVM lane has no thread-level form"
                     && not (out.Contains "applied [omp parallel num_threads(T) + ascending row claims"))
                    (out + err)
                let (code, out, err) = spawnWith (pullEnv "1" "0") dir [ "plan"; "pull3.blade" ]
                recordCase "cli: `plan` under BLADE_LLVM=1 restates the pull's decline in this lane's words"
                    (code = 0 && out.Contains "declined -- the LLVM lane has no thread runtime (no row-owned pull form)"
                     && out.Contains "C++ lane: declined -- row-owned pull scatter not taken: disabled by BLADE_ORBIT_PULL=0"
                     && not (out.Contains "fold form: one accumulator chain with `reassoc nsz` (LLVM lane; the vectorizer picks the lanes); C++ lane fold form: row-owned pull"))
                    (out + err)
                // THE LLVM SCATTER RECORD IS THE EMITTER'S OWN: an unforced map
                // over a stored pool (C++: a deferred computation; LLVM: a
                // producer it would have to execute) is declined by each lane
                // in its own words, never translated into a taken scatter; a
                // Float32 strict walk the C++ lane scatters is not claimed for
                // a lane that cannot emit it.
                File.WriteAllText(Path.Combine(dir, "scatter_unforced.blade"),
                    "type Var = Idx<5>\ntype Obs = Idx<3>\n"
                    + "function radd(a: Float64, b: Float64) where repro -> Float64 = a + b\n"
                    + "let x = method_for(range<Var>, range<Obs>) <@> lambda(i, s) -> Float64((i * 7 + s * 3) % 11) * 0.37 |> compute\n"
                    + "let g = method_for(x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>) where comm(a, b) -> prodsum(a, b) |> compute\n"
                    + "let h = method_for(g) <@> lambda(v: Float64) -> v * 0.25 + 1.0\n"
                    + "let s = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> h(i, j), radd, 0.5), radd, 0.0)\n")
                let (code, out, err) = spawnWith [ ("BLADE_LLVM", "1"); ("BLADE_FP_REPRO", "1") ] dir [ "plan"; "scatter_unforced.blade" ]
                recordCase "cli: `plan` under BLADE_LLVM=1 reports the LLVM emitter's own scatter refusal for an unforced source"
                    (code = 0 && out.Contains "lane: LLVM"
                     && out.Contains "ordered orbit scatter not taken: the compact array is an unforced computation, not a stored pool (LLVM lane"
                     && not (out.Contains "applied [fold form: ordered orbit scatter"))
                    (out + err)
                File.WriteAllText(Path.Combine(dir, "scatter_f32.blade"),
                    "type Var = Idx<5>\ntype Obs = Idx<3>\n"
                    + "let x = method_for(range<Var>, range<Obs>) <@> lambda(i, s) -> Float32(Float64((i * 7 + s * 3) % 11) * 0.37) |> compute\n"
                    + "let g = method_for(x, x) <@> lambda(a: Array<Float32 like Obs>, b: Array<Float32 like Obs>) where comm(a, b) -> prodsum(a, b) |> compute\n"
                    + "let s = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> g(i, j) * g(i, j), (+)), (+))\n")
                let (code, out, err) = spawnWith [ ("BLADE_LLVM", null); ("BLADE_FP_REPRO", "1") ] dir [ "plan"; "scatter_f32.blade" ]
                recordCase "cli: `plan` (C++ lane) scatters a Float32 strict walk"
                    (code = 0 && out.Contains "applied [fold form: ordered orbit scatter (rank 2, extent 5") (out + err)
                let (code, out, err) = spawnWith [ ("BLADE_LLVM", "1"); ("BLADE_FP_REPRO", "1") ] dir [ "plan"; "scatter_f32.blade" ]
                recordCase "cli: `plan` under BLADE_LLVM=1 does not claim a Float32 scatter for the LLVM lane"
                    (code = 0
                     && (out.Contains "but the LLVM lane refused"
                         || out.Contains "ordered orbit scatter not taken: the accumulator or element type is not Float64 / Int64 (LLVM lane)")
                     && not (out.Contains "(recorded by the LLVM lane's emitter); C++ lane: applied"))
                    (out + err)                // THE TILE CORE (M6): a gram above the crossover, BLAS off,
                // materializes through the shared C core in the LLVM lane, and
                // the plan says so in this lane's words (not the C++ lane's
                // dsyrk_tri, which this executable does not contain).
                File.WriteAllText(Path.Combine(dir, "tile.blade"),
                    "type V = Idx<61>\ntype S = Idx<31>\n"
                    + "let x = method_for(range<V>, range<S>) <@> lambda(i, s) -> Float64(i * 11 + s) * 0.25 |> compute\n"
                    + "let g = method_for(x, x) <@> lambda(a: Array<Float64 like S>, b: Array<Float64 like S>) where comm(a, b) -> prodsum(a, b) |> compute\n")
                let tileEnv knob = [ ("BLADE_BLAS", "off"); ("BLADE_LLVM", "1"); ("BLADE_SYRK_TILE", knob); ("BLADE_FP_REPRO", null) ]
                let (code, out, err) = spawnWith (tileEnv null) dir [ "plan"; "tile.blade" ]
                recordCase "cli: `plan` under BLADE_LLVM=1 reports the syrk tile through the shared C core"
                    (code = 0 && out.Contains "fold form: syrk tile (shared C core) 6x8, KC=31"
                     && out.Contains "[microkernel v1] g @ "
                     && out.Contains ": applied [packed syrk tile (shared C core")
                    (out + err)
                let (code, out, err) = spawnWith (tileEnv "0") dir [ "plan"; "tile.blade" ]
                recordCase "cli: `plan` under BLADE_LLVM=1, BLADE_SYRK_TILE=0 reports the tile declined"
                    (code = 0 && out.Contains "declined -- BLADE_SYRK_TILE=0/off: the brick nest"
                     && not (out.Contains "fold form: syrk tile (shared C core)"))
                    (out + err)
                // THE ROW-CONTRACTION TILE CORES (M7 / M8): the antisym pair
                // and the rank-3 simplex through the same shared C core, in
                // the default AND the repro build (no reorder licence used),
                // declined by their knobs -- in this lane's words.
                File.WriteAllText(Path.Combine(dir, "anti.blade"),
                    "type P = Idx<61>\ntype C = Idx<2>\ntype S = Idx<401>\n"
                    + "let z = method_for(range<P>, range<C>, range<S>) <@> lambda(i, c, s) -> Float64(i * 3 + c + s) |> compute\n"
                    + "let l = method_for(z, z) <@> lambda(a: Array<Float64 like C, S>, b: Array<Float64 like C, S>) where anticomm(a, b) -> prodsum(a((0 : C)), b((1 : C))) - prodsum(b((0 : C)), a((1 : C))) |> compute\n")
                File.WriteAllText(Path.Combine(dir, "sym3.blade"),
                    "type V = Idx<29>\ntype S = Idx<67>\nlet static n = 67\n"
                    + "let x = method_for(range<V>, range<S>) <@> lambda(i, s) -> Float64((i * 7 + s * 3) % 5) - 2.0 |> compute\n"
                    + "let t = method_for(x, x, x) <@> lambda(a: Array<Float64 like S>, b: Array<Float64 like S>, c: Array<Float64 like S>) where comm(a, b, c) -> prodsum(a, b, c) / Float64(n) |> compute\n")
                let rowEnv (knob: string) (value: string) (repro: string) =
                    [ ("BLADE_BLAS", "off"); ("BLADE_LLVM", "1"); ("BLADE_FP_REPRO", repro)
                      ("BLADE_ANTISYM_TILE", null); ("BLADE_SIMPLEX_TILE", null); (knob, value) ]
                for repro in [ null; "1" ] do
                    let build = if isNull repro then "default" else "BLADE_FP_REPRO=1"
                    let (code, out, err) = spawnWith (rowEnv "BLADE_ANTISYM_TILE" null repro) dir [ "plan"; "anti.blade" ]
                    recordCase $"cli: `plan` under BLADE_LLVM=1 ({build}) reports the antisym tile through the shared C core"
                        (code = 0 && out.Contains "fold form: antisym tile (shared C core) sequential 6x8"
                         // the LLVM lane is the serial regime: anti_g's default is 1 there
                         && out.Contains ("whole chain per tile, " + Blade.CodeGenExprSupport.antisymSerialDefaultNote Blade.CodeGenExprSupport.antisymNoTeamRequested)
                         && out.Contains "[microkernel v1] l @ "
                         && out.Contains ": applied [antisym tile (shared C core")
                        (out + err)
                    let (code, out, err) = spawnWith (rowEnv "BLADE_SIMPLEX_TILE" null repro) dir [ "plan"; "sym3.blade" ]
                    recordCase $"cli: `plan` under BLADE_LLVM=1 ({build}) reports the simplex tile through the shared C core"
                        (code = 0 && out.Contains "fold form: simplex tile (shared C core) 6x8 with W panel, KC=67, live-accumulator edges, then / d per cell"
                         && out.Contains "[microkernel v1] t @ "
                         && out.Contains ": applied [simplex tile (shared C core")
                        (out + err)
                let (code, out, err) = spawnWith (rowEnv "BLADE_ANTISYM_TILE" "0" null) dir [ "plan"; "anti.blade" ]
                recordCase "cli: `plan` under BLADE_LLVM=1, BLADE_ANTISYM_TILE=0 reports the tile declined"
                    (code = 0 && out.Contains "declined -- BLADE_ANTISYM_TILE=0/off: the plain per-cell nest"
                     && not (out.Contains "fold form: antisym tile (shared C core)"))
                    (out + err)
                // THE ANTISYM ROW GROUPS, EXECUTED (corpus symmetry/132's
                // program -- which the corpus runs under the default G = 1 --
                // under the cache-budget policy, anti_g=0: row groups of 4, 4
                // and a ragged 2 at p = 61, n = 401): the same printed values
                // as the default build, and the NumPy-checked pins, both lanes.
                File.WriteAllText(Path.Combine(dir, "anti132.blade"),
                    "type P = Idx<61>\ntype C = Idx<2>\ntype S = Idx<401>\n"
                    + "let (ss, l02, l0_last, l_g0_cross, l_g1_mid, l_g2_far) = {\n"
                    + "    let z = method_for(range<P>, range<C>, range<S>) <@> lambda(i, c, s) -> Float64((i * 5 + c * 3 + s) % 7) - 3.0 |> compute\n"
                    + "    let l = method_for(z, z) <@> lambda(a: Array<Float64 like C, S>, b: Array<Float64 like C, S>) where anticomm(a, b) -> (prodsum(a((0 : C)), b((1 : C))) - prodsum(b((0 : C)), a((1 : C)))) * 0.5 |> compute\n"
                    + "    let total = reduce(method_for(range<P>) <@> lambda(i) -> reduce(method_for(range<P>) <@> lambda(j) -> l(i, j) * l(i, j), (+)), (+))\n"
                    + "    (total, l(0, 2), l(0, 60), l(23, 26), l(36, 41), l(47, 59))\n"
                    + "}\n")
                let values (out: string) =
                    out.Replace("\r\n", "\n").Split('\n') |> Array.filter (fun l -> l.Contains " = ") |> String.concat "; "
                for lane in [ null; "1" ] do
                    let laneName = if isNull lane then "C++" else "LLVM"
                    let env (tune: string) =
                        [ ("BLADE_BLAS", "off"); ("BLADE_LLVM", lane); ("BLADE_FP_REPRO", null)
                          ("BLADE_ANTISYM_TILE", null); ("BLADE_TILE_TUNE", tune); ("BLADE_EXE_CACHE", "0") ]
                    let (c0, o0, e0) = spawnWith (env null) dir [ "run"; "anti132.blade" ]
                    let (c1, o1, e1) = spawnWith (env "anti_g=0") dir [ "run"; "anti132.blade" ]
                    let v0, v1 = values o0, values o1
                    recordCase $"cli: antisym row groups of 4 (anti_g=0) print the default build's values ({laneName} lane)"
                        (c0 = 0 && c1 = 0 && v0 = v1
                         && v1.Contains "ss = 598045803.5" && v1.Contains "l02 = -600.5" && v1.Contains "l0_last = 202.5"
                         && v1.Contains "l_g0_cross = -199.0" && v1.Contains "l_g1_mid = 604.0" && v1.Contains "l_g2_far = 597.0")
                        ($"default: {v0} | anti_g=0: {v1} | " + e0 + e1)
                let (code, out, err) = spawnWith (rowEnv "BLADE_SIMPLEX_TILE" "0" null) dir [ "plan"; "sym3.blade" ]
                recordCase "cli: `plan` under BLADE_LLVM=1, BLADE_SIMPLEX_TILE=0 reports the tile declined"
                    (code = 0 && out.Contains "declined -- BLADE_SIMPLEX_TILE=0/off: the plain per-cell nest"
                     && not (out.Contains "fold form: simplex tile (shared C core)"))
                    (out + err)
                // THE COMPOUND REPRESENTATION (G9): a rank-1 `compound(dense,
                // mask)` is the compaction in both lanes (the plan line passes
                // through unchanged under BLADE_LLVM=1, which emits exactly
                // these compounds); a rank-2 mask keeps the hashed index.
                File.WriteAllText(Path.Combine(dir, "compound1.blade"),
                    "type I = Idx<9>\n"
                    + "let a: Array<Int64 like I> = [5, -1, 4, -2, 3, -3, 2, -4, 1]\n"
                    + "let keep = mask(a, lambda(v) -> v > 0)\n"
                    + "let y = compound(a, keep)\n"
                    + "let s = reduce(y, (+))\n")
                File.WriteAllText(Path.Combine(dir, "compound2.blade"),
                    "type R = Idx<2>\ntype C = Idx<3>\n"
                    + "let g: Array<Int64 like R, C> = [[1, 2, 3], [4, 5, 6]]\n"
                    + "let h = compound(g, mask(g, lambda(v) -> v % 2 == 0))\n")
                let compoundEnv llvm = [ ("BLADE_BLAS", "off"); ("BLADE_LLVM", llvm); ("BLADE_FP_REPRO", null) ]
                // compound1's let-bound mask takes compaction fusion
                // (Blade.Optimize.planCompactionFusion): the same
                // representation, counted in the mask pass and gathered in one
                // pass at the compound, and -- `s` only folds it -- its kept
                // positions elided (v2, T4b's demand census).
                let compactionLine =
                    ": applied [representation: rank-1 compaction (counted in the mask pass, gathered in one pass -- compaction fusion; no hash table; positions elided: the demand census found no consumer that reads them)"
                let hashedLine =
                    "declined -- hashed SparseIdx: a rank-2 mask has no closed-form rank, so compound_index_t<2> keeps one tuple->rank hash entry per present cell"
                for llvm in [ null; "1" ] do
                    let lane = if isNull llvm then "C++ lane" else "BLADE_LLVM=1"
                    let (code, out, err) = spawnWith (compoundEnv llvm) dir [ "plan"; "compound1.blade" ]
                    recordCase $"cli: `plan` ({lane}) reports the rank-1 compound as the compaction"
                        (code = 0 && out.Contains "[compound v1] y @ " && out.Contains compactionLine
                         && (isNull llvm || out.Contains "lane: LLVM (BLADE_LLVM=1)"))
                        (out + err)
                    // The rank-2 compound stays hashed; under BLADE_LLVM=1 the
                    // lane refuses the program (the plan is the C++ lane's,
                    // which is the executable that will run).
                    let (code, out, err) = spawnWith (compoundEnv llvm) dir [ "plan"; "compound2.blade" ]
                    recordCase $"cli: `plan` ({lane}) reports the rank-2 compound as the hashed index"
                        (code = 0 && out.Contains "[compound v1] h @ " && out.Contains hashedLine
                         && not (out.Contains compactionLine))
                        (out + err)
        finally
            try Directory.Delete(dir, true) with _ -> ()
    // --- COMPACTION FUSION, run (Blade.Optimize.planCompactionFusion) ---
    // The licensed fold form and the LLVM lane's arithmetic are invisible to
    // the repro-pinned differentials, so the knob is compared here, at run
    // time, over NON-DYADIC data (n = 1003: four 256-cell blocks and a tail):
    // full fusion, and the fold alone (B4a: a mask read between the mask and
    // the compound declines the compaction), in both lanes. Identity regime
    // (BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off): gate off and on print the SAME
    // checksum AND the same residual `checksum - floor(checksum)`, whose 15
    // printed digits resolve every bit of the checksum (the subtraction is
    // exact). Default build: within 1e-12 relative.
    if not capabilities.Value.HasGpp || selfExe.IsNone then
        record "compaction fusion: gate off vs on at run time" TH.Skip "requires g++ and the Blade executable"
    else
        let dir = Path.Combine(Path.GetTempPath(), "blade_cli_cfuse_" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            let body (between: string) =
                "type Ix = Idx<1003>\n"
                + "function gen(k: Int64) -> Float64 = {\n"
                + "    let h1 = ((k + 10000019) * 1103515245 + 12345) % 2147483648\n"
                + "    let h2 = (h1 * (h1 / 4096 + 1)) % 2147483648\n"
                + "    2.0 * (Float64(h2) / 2147483648.0)\n"
                + "}\n"
                + "let x = method_for(range<Ix>) <@> lambda(i) -> gen(Int64(i)) |> compute\n"
                + "let keep = mask(x, lambda(v) -> v > 1.0)\n"
                + between
                + "let y = compound(x, keep)\n"
                + "let checksum = reduce(y * y, (+))\n"
                + "let resid = checksum - floor(checksum)\n"
            File.WriteAllText(Path.Combine(dir, "full.blade"), body "")
            File.WriteAllText(Path.Combine(dir, "b4a.blade"), body "let k0 = keep(3)\n")
            // v2 with the kept positions DEMANDED (an alias: the census does
            // not follow it), so they are filled (in the mask pass under
            // `gather`).
            File.WriteAllText(Path.Combine(dir, "held.blade"),
                body "" + "let w = y\nlet held = reduce(w, (+))\n")
            // T4c: THREE companions over one mask (x itself, an Int64 column,
            // a Float64 column) share ONE selection index; an alias of the
            // last (the census does not follow it) keeps the shared positions,
            // a form both lanes take. The folds over two of them are the
            // self-product (fed in the joint pass under repro), the third a
            // plain sum.
            File.WriteAllText(Path.Combine(dir, "shared.blade"),
                (body "let c2 = method_for(range<Ix>) <@> lambda(i) -> (Int64(i) * 7) % 13 |> compute\nlet c3 = method_for(range<Ix>) <@> lambda(i) -> gen(Int64(i) + 77) |> compute\n")
                + "let y2 = compound(c2, keep)\n"
                + "let y3 = compound(c3, keep)\n"
                + "let w3 = y3\n"
                + "let t3 = reduce(y3 * y3, (+)) + Float64(reduce(y2, (+))) + reduce(w3, (+))\n"
                + "let both = checksum + t3\n")
            let valueOf (key: string) (out: string) =
                out.Split('\n') |> Array.tryPick (fun l ->
                    let t = l.Trim()
                    if t.StartsWith(key + " = ") then Some (t.Substring(key.Length + 3)) else None)
            // The settings compared: the gate off, the b4 mirror (round 2),
            // the v2 default (T4b), the opt-in mask-site gather (`gather`, T4a
            // + T4b), and `gather` with the selectivity cap at 0 (its two-pass
            // fallback).
            let settings = [ ("0", null); ("b4", null); ("1", null); ("unshared", null); ("gather", null); ("gather", "sel_cap=0") ]
            let label (gate: string, tune: string) = if isNull tune then gate else $"{gate}+{tune}"
            for llvm in [ null; "1" ] do
                for prog in [ "full"; "b4a"; "held"; "shared" ] do
                    let lane = if isNull llvm then "C++" else "LLVM"
                    let runWith (gate: string, tune: string) (repro: bool) =
                        let env =
                            [ ("BLADE_BLAS", "off"); ("BLADE_LLVM", llvm); ("BLADE_COMPACTION_FUSION", gate)
                              ("BLADE_TILE_TUNE", tune)
                              ("BLADE_FP_REPRO", (if repro then "1" else null))
                              ("BLADE_FP_CONTRACT", (if repro then "off" else null)) ]
                        spawnWith env dir [ "run"; $"{prog}.blade"; "--print"; (if prog = "shared" then "checksum,resid,both" else "checksum,resid"); "--verbose" ]
                    // `--verbose` names the emitted file on stderr: `.ll` is the LLVM lane.
                    let laneOk (o: string) = isNull llvm || o.Contains ".ll"
                    let key = if prog = "shared" then "both" else "checksum"
                    let ident = settings |> List.map (fun s -> let (c, o, e) = runWith s true in (s, c, valueOf key o, valueOf "resid" o, o + e))
                    let (_, _, k0, r0, _) = List.head ident
                    let same = ident |> List.forall (fun (_, c, k, r, oe) -> c = 0 && laneOk oe && k = k0 && r = r0 && r.IsSome)
                    let shown = ident |> List.map (fun (s, _, k, r, _) -> $"{label s}: {k} / {r}") |> String.concat "; "
                    recordCase $"cli: compaction fusion ({lane}, {prog}) identity regime: gate off == b4 == on == unshared == gather (and sel_cap=0), every bit"
                        same shown
                    let dflt = settings |> List.map (fun s -> let (c, o, e) = runWith s false in (s, c, valueOf key o, o + e))
                    let close =
                        match dflt with
                        | (_, c0, Some a, _) :: rest when c0 = 0 ->
                            let fa = Double.Parse a
                            rest |> List.forall (fun (_, c, k, oe) ->
                                match k with
                                | Some b when c = 0 && laneOk oe -> abs (fa - Double.Parse b) <= 1e-12 * abs fa
                                | _ -> false)
                        | _ -> false
                    let shownD = dflt |> List.map (fun (s, _, k, _) -> $"{label s}: {k}") |> String.concat "; "
                    recordCase $"cli: compaction fusion ({lane}, {prog}) default build: gate off vs b4 vs on vs unshared vs gather (and sel_cap=0) within 1e-12"
                        close shownD
            // A RUN-TIME source extent (C++ lane: the LLVM lane bakes range
            // extents): `gather`'s single pass runs under a run-time cap test,
            // and with sel_cap=0 that test fails, so the mask pass only counts and
            // the compound runs B4's pass into the same buffers and feeds the
            // same chain. Identity regime: every setting, every bit.
            File.WriteAllText(Path.Combine(dir, "dyn.blade"),
                "function gen(k: Int64) -> Float64 = {\n"
                + "    let h1 = ((k + 10000019) * 1103515245 + 12345) % 2147483648\n"
                + "    let h2 = (h1 * (h1 / 4096 + 1)) % 2147483648\n"
                + "    2.0 * (Float64(h2) / 2147483648.0)\n"
                + "}\n"
                + "let k = reduce([300, 400, 303], (+))\n"
                + "let ks = 0..k |> compute\n"
                + "let x = method_for(ks) <@> lambda(i) -> gen(i) |> compute\n"
                + "let keep = mask(x, lambda(v) -> v > 1.0)\n"
                + "let y = compound(x, keep)\n"
                + "let checksum = reduce(y * y, (+))\n"
                + "let resid = checksum - floor(checksum)\n"
                + "let m = extents(y)\n")
            // T4c over the same run-time axis: two companions (x and a second
            // column) share one selection; under `gather` the mask pass
            // gathers both while n is under the cap, else the first companion
            // runs the joint pass into the same buffers and chains.
            File.WriteAllText(Path.Combine(dir, "dyn2.blade"),
                "function gen(k: Int64) -> Float64 = {\n"
                + "    let h1 = ((k + 10000019) * 1103515245 + 12345) % 2147483648\n"
                + "    let h2 = (h1 * (h1 / 4096 + 1)) % 2147483648\n"
                + "    2.0 * (Float64(h2) / 2147483648.0)\n"
                + "}\n"
                + "let k = reduce([300, 400, 303], (+))\n"
                + "let ks = 0..k |> compute\n"
                + "let x = method_for(ks) <@> lambda(i) -> gen(i) |> compute\n"
                + "let c2 = method_for(ks) <@> lambda(i) -> gen(i + 5) |> compute\n"
                + "let keep = mask(x, lambda(v) -> v > 1.0)\n"
                + "let y = compound(x, keep)\n"
                + "let y2 = compound(c2, keep)\n"
                + "let checksum = reduce(y * y, (+)) + reduce(y2 * y2, (+))\n"
                + "let resid = checksum - floor(checksum)\n"
                + "let m = extents(y2)\n")
            let dynRunOf (prog: string) (gate: string, tune: string) =
                let env =
                    [ ("BLADE_BLAS", "off"); ("BLADE_LLVM", null); ("BLADE_COMPACTION_FUSION", gate); ("BLADE_TILE_TUNE", tune)
                      ("BLADE_FP_REPRO", "1"); ("BLADE_FP_CONTRACT", "off") ]
                let (c, o, e) = spawnWith env dir [ "run"; $"{prog}.blade"; "--print"; "checksum,resid,m" ]
                ((gate, tune), c, valueOf "checksum" o, valueOf "resid" o, valueOf "m" o, e)
            let dynSameOf (dyn: ((string * string) * int * string option * string option * string option * string) list) =
                let (_, _, dk, dr, dm, _) = List.head dyn
                dyn |> List.forall (fun (_, c, k, r, m, _) -> c = 0 && k = dk && r = dr && m = dm && r.IsSome && m.IsSome)
            let dyn = settings |> List.map (dynRunOf "dyn")
            let dynSame = dynSameOf dyn
            let dyn2 = settings |> List.map (dynRunOf "dyn2")
            let dyn2Same = dynSameOf dyn2
            // W1-4 review finding 7, the KNOB-OFF TWINS of corpus loops/537-539
            // and symmetry/137: a writer hidden in a side-table operand (a
            // random key, another compound's operand) between a producer and
            // its fold. With the schedule's knob on, the interval audit sees
            // the payload and declines; off, the two-pass form runs. Both
            // print the as-written value, in each regime the schedule exists.
            let hiddenBump = "function bump(a: mut Array<Float64 like I>) -> Int64 = {\n    a(0) = 9.0\n    7\n}\n"
            File.WriteAllText(Path.Combine(dir, "hb1.blade"),
                "import rand as r\ntype I = Idx<2>\n" + hiddenBump
                + "let x: Array<Float64 like I> = [1.0, 2.0]\nlet mut p = method_for(x) <@> lambda(v) -> v + 1.0 |> compute\n"
                + "let noise = r.uniform(bump(p), 1)\nlet s = reduce(method_for(p) <@> lambda(v) -> v * v, (+), 0.0)\n")
            File.WriteAllText(Path.Combine(dir, "hb1c.blade"),
                "type I = Idx<2>\nfunction bumped(a: mut Array<Float64 like I>) -> Array<Float64 like I> = {\n    a(0) = 9.0\n    let b = a + 0.0 |> compute\n    b\n}\n"
                + "let x: Array<Float64 like I> = [1.0, 2.0]\nlet other = mask(x, lambda(v) -> v > 0.5)\nlet mut p = method_for(x) <@> lambda(v) -> v + 1.0 |> compute\n"
                + "let zz = compound(bumped(p), other)\nlet s = reduce(method_for(p) <@> lambda(v) -> v * v, (+), 0.0)\n")
            File.WriteAllText(Path.Combine(dir, "hhot.blade"),
                "import rand as r\ntype I = Idx<2>\ntype J = Idx<2>\nfunction bump(a: mut Array<Float64 like I, J>) -> Int64 = {\n    a(0, 0) = 9.0\n    7\n}\n"
                + "let mut p = method_for(range<I, J>) <@> lambda(i, j) -> 1.0 |> compute\nlet noise = r.uniform(bump(p), 1)\n"
                + "let s = reduce(method_for(p) <@> lambda(row) -> prodsum(row, row), (+), 0.0)\n")
            File.WriteAllText(Path.Combine(dir, "hcrow.blade"),
                "import rand as r\ntype I = Idx<2>\nfunction bump(a: mut Array<Float64 like SymIdx<2, I>>) -> Int64 = {\n    a(0, 1) = 9.0\n    7\n}\n"
                + "let x: Array<Float64 like I> = [1.0, 2.0]\nlet mut p = method_for(x, x) <@> lambda(a, b) where comm(a, b) -> a + b |> compute\n"
                + "let noise = r.uniform(bump(p), 1)\n"
                + "let s = reduce(method_for(range<I>) <@> lambda(i) -> reduce(method_for(range<I>) <@> lambda(j) -> p(i, j), (+)), lambda(a, b) where omp -> a + b, 0.0)\n")
            for (prog, knob, regimes, want) in
                    [ ("hb1", "BLADE_CELL_EPILOGUE", [ true; false ], "90.0")
                      ("hb1c", "BLADE_CELL_EPILOGUE", [ true; false ], "90.0")
                      ("hhot", "BLADE_HOT_ROW", [ true; false ], "84.0")
                      ("hcrow", "BLADE_COMPACT_ROW_CLASSES", [ false ], "24.0") ] do
                for repro in regimes do
                    let runK (v: string) =
                        let env =
                            [ ("BLADE_BLAS", "off"); ("BLADE_LLVM", null); (knob, v)
                              ("BLADE_FP_REPRO", (if repro then "1" else null))
                              ("BLADE_FP_CONTRACT", (if repro then "off" else null)) ]
                        let (c, o, _) = spawnWith env dir [ "run"; $"{prog}.blade"; "--print"; "s" ]
                        (c, valueOf "s" o)
                    let (c1, s1) = runK null
                    let (c0, s0) = runK "0"
                    let build = if repro then "identity regime" else "default build"
                    recordCase $"cli: hidden side-table writer ({prog}, {build}): {knob} on == off == the as-written s = {want}"
                        (c1 = 0 && c0 = 0 && s1 = Some want && s0 = Some want) ($"on: {s1}; off: {s0}")
            recordCase "cli: compaction fusion (C++, run-time extent) identity regime: gate off == b4 == on == gather == gather over the cap (the guarded fallback), every bit"
                dynSame (dyn |> List.map (fun (s, c, k, r, m, e) -> $"{label s}: exit {c} {k} / {r} / {m} {e}") |> String.concat "; ")
            recordCase "cli: compaction fusion (C++, run-time extent, two companions sharing one selection) identity regime: gate off == b4 == on == unshared == gather == gather over the cap (the first companion's guarded joint pass), every bit"
                dyn2Same (dyn2 |> List.map (fun (s, c, k, r, m, e) -> $"{label s}: exit {c} {k} / {r} / {m} {e}") |> String.concat "; ")
            // dyn2's ROUTE (T4c fix round, review finding 3): the emitted C++
            // takes the shared selection -- by default one joint pass at `y`;
            // under `gather` the run-time cap comparison at the mask and the
            // first companion's guarded joint pass (its false edge) -- so the
            // value twins above compare the shared route, not two private ones.
            let emitWith (gate: string) =
                let env = [ ("BLADE_BLAS", "off"); ("BLADE_LLVM", null); ("BLADE_COMPACTION_FUSION", gate); ("BLADE_TILE_TUNE", null) ]
                let (c, o, e) = spawnWith env dir [ "emit"; "dyn2.blade" ]
                (c, o + e)
            let (cd, od) = emitWith null
            let (cg, og) = emitWith "gather"
            recordCase "cli: dyn2's emission takes the shared selection (one index, one joint pass at `y` over the run-time grid)"
                (cd = 0 && od.Contains "compound_index_t<1>* keep__sel = new compound_index_t<1>(\"keep__sel\", keep__sel_grid, keep__count, compound_index_t<1>::positions_elided)"
                 && od.Contains "keep__sel_q += (size_t)keep__sel_pool[keep__sel_i];")
                (if od.Length > 400 then od.Substring(0, 400) else od)
            recordCase "cli: dyn2 under `gather`: the run-time cap comparison at the mask, the guarded joint pass at the first companion"
                (cg = 0 && og.Contains "const bool keep__one = (size_t)(" && og.Contains "if (!keep__one) {"
                 && og.Contains "keep__sv0[keep__sel_q] = keep__sel_d0[keep__sel_i];" && og.Contains "keep__sv1[keep__sel_q] = keep__sel_d1[keep__sel_i];")
                (if og.Length > 400 then og.Substring(0, 400) else og)
            // T4c fix round (review findings 1-2): the NEWLY admitted singleton
            // shapes and the storage-proof probes, each run by default, gate
            // off, `unshared` and `gather`, in both lanes: the same printed
            // values, and -- for the failing twin -- the same first failure
            // (code and line). Integer-valued data: exact in every build.
            let t4cHead = "type Ix = Idx<1003>\nlet x = method_for(range<Ix>) <@> lambda(i) -> Float64((Int64(i) * 37) % 11) - 4.0 |> compute\nlet c = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 7) |> compute\n"
            let semiHead = "type Row = Idx<2003>\ntype Ev = Idx<101>\nlet elig = method_for(range<Ev>) <@> lambda(e) -> (Int64(e) * 199) % 20011 |> compute\nlet cust = method_for(range<Row>) <@> lambda(i) -> (Int64(i) * 7919 + 13) % 20011 |> compute\nlet amt = method_for(range<Row>) <@> lambda(i) -> Float64((Int64(i) * 31) % 1000) |> compute\n"
            let boomLine = "let z0 = Int64(floor(x(1)))\nlet boom = 10 / z0\n"
            let semiBoom = "let z0 = cust(0) - 13\nlet boom = 10 / z0\n"
            let twins =
                [ ("s_other", t4cHead + "let keep = mask(x, lambda(v) -> v > 1.0)\nlet y = compound(c, keep)\nlet chk = reduce(y * y, (+)) + Float64(extents(y))\n", "chk")
                  ("s_other_fail", t4cHead + "let keep = mask(x, lambda(v) -> v > 1.0)\n" + boomLine + "let y = compound(c, keep)\nlet chk = reduce(y * y, (+)) + Float64(extents(y))\n", "chk")
                  ("s_probe_src", semiHead + "let keep = mask(cust, lambda(c) -> contains(elig, c))\nlet y = compound(cust, keep)\nlet chk = Float64(reduce(y, (+))) + Float64(extents(y))\n", "chk")
                  ("s_probe_src_fail", semiHead + "let keep = mask(cust, lambda(c) -> contains(elig, c))\n" + semiBoom + "let y = compound(cust, keep)\nlet chk = Float64(reduce(y, (+))) + Float64(extents(y))\n", "chk")
                  ("s_semiagg", semiHead + "let keep = mask(cust, lambda(c) -> contains(elig, c))\nlet y = compound(amt, keep)\nlet chk = reduce(y, (+)) + Float64(extents(y))\n", "chk")
                  ("s_semiagg_fail", semiHead + "let keep = mask(cust, lambda(c) -> contains(elig, c))\n" + semiBoom + "let y = compound(amt, keep)\nlet chk = reduce(y, (+)) + Float64(extents(y))\n", "chk")
                  ("p_alias_deferred", "type I = Idx<3>\nlet x: Array<Float64 like I> = [1.0, 2.0, 3.0]\nlet lazy_col = method_for(x) <@> lambda(v) -> v + 10.0\nlet alias_col = lazy_col\nlet keep = mask(x, lambda(v) -> v > 0.0)\nlet y1 = compound(x, keep)\nlet y2 = compound(alias_col, keep)\nlet n = extents(y2)\nlet chk = reduce(y2, (+))\n", "n,chk")
                  ("p_alias_stored", "type I = Idx<3>\nlet x: Array<Float64 like I> = [1.0, 2.0, 3.0]\nlet col = method_for(x) <@> lambda(v) -> v + 10.0 |> compute\nlet alias_col = col\nlet keep = mask(x, lambda(v) -> v > 1.5)\nlet y1 = compound(x, keep)\nlet y2 = compound(alias_col, keep)\nlet chk = reduce(y2, (+)) + reduce(y1, (+))\n", "chk")
                  ("p_mask_src_deferred", "type Ix = Idx<13>\nlet x = method_for(range<Ix>) <@> lambda(i) -> Float64((Int64(i) * 3) % 13)\nlet c2 = method_for(range<Ix>) <@> lambda(i) -> Int64(i) * 10 |> compute\nlet c3 = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i)) * 0.5 |> compute\nlet keep = mask(x, lambda(v) -> v > 5.5)\nlet y2 = compound(c2, keep)\nlet y3 = compound(c3, keep)\nlet chk = Float64(reduce(y2, (+))) + reduce(y3, (+))\n", "chk")
                  ("p_mask_src_stored", "type Ix = Idx<13>\nlet x = method_for(range<Ix>) <@> lambda(i) -> Float64((Int64(i) * 3) % 13) |> compute\nlet c2 = method_for(range<Ix>) <@> lambda(i) -> Int64(i) * 10 |> compute\nlet c3 = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i)) * 0.5 |> compute\nlet keep = mask(x, lambda(v) -> v > 5.5)\nlet y2 = compound(c2, keep)\nlet y3 = compound(c3, keep)\nlet chk = Float64(reduce(y2, (+))) + reduce(y3, (+))\n", "chk") ]
            for (prog, src, prints) in twins do
                File.WriteAllText(Path.Combine(dir, prog + ".blade"), src)
            let firstFailure (out: string) =
                let m = Text.RegularExpressions.Regex.Match(out, @"error\[(BL\d{4})\][^\n]*(?:\n[^\n]*?\.blade:(\d+))?")
                if m.Success then Some (m.Groups.[1].Value + "@" + m.Groups.[2].Value) else None
            for llvm in [ null; "1" ] do
                let lane = if isNull llvm then "C++" else "LLVM"
                for (prog, _, prints) in twins do
                    let runT (gate: string) =
                        let env = [ ("BLADE_BLAS", "off"); ("BLADE_LLVM", llvm); ("BLADE_COMPACTION_FUSION", gate); ("BLADE_TILE_TUNE", null); ("BLADE_FP_REPRO", null); ("BLADE_FP_CONTRACT", null) ]
                        let (c, o, e) = spawnWith env dir [ "run"; $"{prog}.blade"; "--print"; prints ]
                        let vals = prints.Split(',') |> Array.map (fun k -> valueOf k o) |> List.ofArray
                        (gate, c, vals, firstFailure (o + "\n" + e))
                    let rs = [ null; "0"; "unshared"; "gather" ] |> List.map runT
                    let (_, c0, v0, f0) = List.head rs
                    let fails = prog.EndsWith "_fail"
                    let same = rs |> List.forall (fun (_, c, v, f) -> c = c0 && v = v0 && f = f0)
                    let shaped =
                        if fails then c0 <> 0 && (match f0 with Some f -> f.StartsWith "BL8013@" | None -> false)
                        else c0 = 0 && v0 |> List.forall Option.isSome
                    let what = if fails then "the same first failure (BL8013 at its own line)" else "the same values"
                    recordCase $"cli: T4c twins ({lane}, {prog}): default == gate off == unshared == gather -- {what}"
                        (same && shaped)
                        (rs |> List.map (fun (g, c, v, f) -> let gn = (if isNull g then "default" else g) in $"{gn}: exit {c} {v} {f}") |> String.concat "; ")
            // The empty unseeded fold aborts BL8003 at the FOLD's own line (5),
            // after the fused compaction (line 4), gate on and off, both
            // lanes, both builds (under repro the chain was fed in the pass).
            File.WriteAllText(Path.Combine(dir, "empty.blade"),
                "type Ix = Idx<1003>\n"
                + "let x = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i)) |> compute\n"
                + "let keep = mask(x, lambda(v) -> v > 5000.0)\n"
                + "let y = compound(x, keep)\n"
                + "let checksum = reduce(y * y, (+)) + Float64(extents(y))\n")
            for llvm in [ null; "1" ] do
                for repro in [ false; true ] do
                    let lane = if isNull llvm then "C++" else "LLVM"
                    let build = if repro then "repro" else "default"
                    let ok =
                        [ "0"; "b4"; "1"; "gather" ] |> List.forall (fun gate ->
                            let env =
                                [ ("BLADE_BLAS", "off"); ("BLADE_LLVM", llvm); ("BLADE_COMPACTION_FUSION", gate)
                                  ("BLADE_FP_REPRO", (if repro then "1" else null)) ]
                            let (code, out, err) = spawnWith env dir [ "run"; "empty.blade" ]
                            let all = out + err
                            code <> 0 && all.Contains "error[BL8003]" && all.Contains "empty.blade:5")
                    recordCase $"cli: compaction fusion ({lane}, {build}): the empty fold aborts BL8003 at its own line, gate off, b4, on and gather" ok ""
            // COMPACT ROW CLASSES (Blade.Optimize.planCompactRowClasses): the
            // licensed checksum of a compact producer's orbit fold is summed
            // per packed row and combined in row order, so its bits do not
            // depend on the team: OMP_NUM_THREADS 1 / 3 / 8 and the serial
            // build (BLADE_OMP_THREADS=1) print the same checksum AND exact
            // residual. Non-dyadic cells (exp); within 1e-12 of the gate-off
            // orbit pass.
            File.WriteAllText(Path.Combine(dir, "rows.blade"),
                "type Pts = Idx<101>\n"
                + "type Dim = Idx<2>\n"
                + "let pts = method_for(range<Pts>, range<Dim>) <@> lambda(i, d) -> Float64((Int64(i) * 37 + Int64(d) * 11) % 97) / 97.0 |> compute\n"
                + "let k = method_for(pts, pts) <@> lambda(a: Array<Float64 like Dim>, b: Array<Float64 like Dim>) where comm(a, b), omp(a: 1) -> {\n"
                + "    let dx = a((0 : Dim)) - b((0 : Dim))\n"
                + "    let dy = a((1 : Dim)) - b((1 : Dim))\n"
                + "    exp(-(dx * dx + dy * dy))\n"
                + "} |> compute\n"
                + "let ss = reduce(method_for(range<Pts>) <@> lambda(i) -> reduce(method_for(range<Pts>) <@> lambda(j) -> k(i, j) * k(i, j), (+)), lambda(u, v) where omp -> u + v, 0.0)\n"
                + "let resid = ss - floor(ss)\n")
            let rowsRun (env: (string * string) list) =
                let (code, out, err) = spawnWith ([ ("BLADE_BLAS", "off"); ("BLADE_FP_REPRO", null); ("BLADE_LLVM", null) ] @ env) dir [ "run"; "rows.blade"; "--print"; "ss,resid" ]
                (code, valueOf "ss" out, valueOf "resid" out, err)
            // OBSERVED teams: the omp-coverage instrumentation (codegen test
            // mode) prints each parallel region's actual team size; the
            // program prints every binding, so the stored producer cells are
            // compared too, not only the checksum. OMP_DYNAMIC=false and no
            // thread limit; a team the host cannot give is reported as such.
            let instrumented =
                CodeGen.setOmpTestMode true
                try compileToExe (Path.Combine(dir, "rows.blade")) (Some (Path.Combine(dir, "rows_instr.exe"))) false false
                finally CodeGen.setOmpTestMode false
            match instrumented with
            | Error e -> recordCase "cli: compact row classes: observed teams 1 / 3 / 8 give identical output" false e
            | Ok exe ->
                let envKeys = [ "OMP_NUM_THREADS"; "OMP_DYNAMIC"; "OMP_THREAD_LIMIT"; "BLADE_COMPACT_ROW_CLASSES" ]
                let prior = envKeys |> List.map (fun k -> (k, Environment.GetEnvironmentVariable k))
                let runT (t: string) =
                    Environment.SetEnvironmentVariable("OMP_NUM_THREADS", t)
                    Environment.SetEnvironmentVariable("OMP_DYNAMIC", "false")
                    Environment.SetEnvironmentVariable("OMP_THREAD_LIMIT", null)
                    match runExecutable exe with
                    | Ok (0, out) ->
                        let lines = out.Replace("\r\n", "\n").Split('\n')
                        let team =
                            lines |> Array.tryPick (fun l ->
                                let m = Text.RegularExpressions.Regex.Match(l, @"^\[omp-coverage\] region=k teamsz=(\d+)")
                                if m.Success then Some (int m.Groups.[1].Value) else None)
                        let values =
                            lines |> Array.filter (fun l -> not (l.StartsWith "[omp-coverage]") && not (l.Contains " completed in "))
                            |> String.concat "\n"
                        Some (team, values)
                    | _ -> None
                let runs =
                    try [ for t in [ 1; 3; 8 ] -> (t, runT (string t)) ]
                    finally for (k, v) in prior do Environment.SetEnvironmentVariable(k, v)
                let teamOf (r: (int option * string) option) = match r with Some (Some n, _) -> string n | _ -> "unobserved"
                let observed = runs |> List.map (fun (t, r) -> $"T={t}: team {teamOf r}") |> String.concat ", "
                let available = runs |> List.filter (fun (t, r) -> match r with Some (Some n, _) -> n = t | _ -> false)
                match runs with
                | (_, Some (Some 1, v1)) :: _ when available.Length = 3 ->
                    let same = runs |> List.forall (fun (_, r) -> match r with Some (_, v) -> v = v1 | None -> false)
                    recordCase "cli: compact row classes: observed teams 1 / 3 / 8 print identical output (stored cells and checksum)" same observed
                | _ when available.Length >= 2 ->
                    let vs = available |> List.choose (fun (_, r) -> match r with Some (_, v) -> Some v | None -> None)
                    record "cli: compact row classes: observed teams 1 / 3 / 8 print identical output (stored cells and checksum)"
                        (if vs |> List.forall ((=) vs.Head) then TH.Skip else TH.Fail) $"not every team was available ({observed}); the available ones agree: {(vs |> List.forall ((=) vs.Head))}"
                | _ ->
                    record "cli: compact row classes: observed teams 1 / 3 / 8 print identical output (stored cells and checksum)" TH.Skip $"teams unavailable on this host: {observed}"
            // The uninstrumented binary, requested teams and the serial build.
            let teams =
                [ for t in [ "1"; "3"; "8" ] -> rowsRun [ ("OMP_NUM_THREADS", t); ("OMP_DYNAMIC", "false"); ("BLADE_OMP_THREADS", null); ("BLADE_COMPACT_ROW_CLASSES", null) ] ]
                @ [ rowsRun [ ("OMP_NUM_THREADS", null); ("BLADE_OMP_THREADS", "1"); ("BLADE_COMPACT_ROW_CLASSES", null) ] ]
            let (_, s0, r0, _) = teams.Head
            let identical = teams |> List.forall (fun (c, s, r, _) -> c = 0 && s.IsSome && r.IsSome && s = s0 && r = r0)
            let shown = teams |> List.map (fun (c, s, r, e) -> $"{c}:{s}/{r} {e}") |> String.concat "; "
            recordCase "cli: compact row classes: T = 1 / 3 / 8 and the serial build print identical bits" identical shown
            let (c9, s9, _, e9) = rowsRun [ ("OMP_NUM_THREADS", "8"); ("BLADE_OMP_THREADS", null); ("BLADE_COMPACT_ROW_CLASSES", "0") ]
            let close =
                match s0, s9 with
                | Some a, Some b when c9 = 0 -> abs (Double.Parse a - Double.Parse b) <= 1e-12 * abs (Double.Parse a)
                | _ -> false
            recordCase "cli: compact row classes: within 1e-12 of the gate-off orbit pass" close ($"on: {s0}; off: {s9} {e9}")
            // BOTH LANES, gate off vs on (<= 1e-12 within each lane), and C++
            // vs LLVM at the same tolerance (their per-row trees differ: the
            // C++ lane's sq/sum<8> or eight lanes, the LLVM vectorizer's
            // own). Non-dyadic symmetric data with a NONZERO outer seed, and a
            // strict pool with a kernel of unknown parity (two classes plus
            // the zero class; the last row's class is empty); n = 101 leaves
            // row tails of every length.
            File.WriteAllText(Path.Combine(dir, "rows2.blade"),
                "type Pts = Idx<101>\n"
                + "type Dim = Idx<2>\n"
                + "let pts = method_for(range<Pts>, range<Dim>) <@> lambda(i, d) -> Float64((Int64(i) * 37 + Int64(d) * 11) % 97) / 97.0 |> compute\n"
                + "let k = method_for(pts, pts) <@> lambda(a: Array<Float64 like Dim>, b: Array<Float64 like Dim>) where comm(a, b) -> exp(-(a((0 : Dim)) - b((0 : Dim))) * (a((0 : Dim)) - b((0 : Dim))) - a((1 : Dim)) * b((1 : Dim))) |> compute\n"
                + "let ss = reduce(method_for(range<Pts>) <@> lambda(i) -> reduce(method_for(range<Pts>) <@> lambda(j) -> k(i, j) * k(i, j), (+)), (+), 0.37)\n"
                + "let w = method_for(pts, pts) <@> lambda(a: Array<Float64 like Dim>, b: Array<Float64 like Dim>) where anticomm(a, b) -> a((0 : Dim)) * b((1 : Dim)) - b((0 : Dim)) * a((1 : Dim)) |> compute\n"
                + "let sa = reduce(method_for(range<Pts>) <@> lambda(i) -> reduce(method_for(range<Pts>) <@> lambda(j) -> w(i, j) * w(i, j) + w(i, j), (+)), (+))\n")
            let runLane (llvm: string) (gate: string) =
                let (code, out, err) =
                    spawnWith [ ("BLADE_BLAS", "off"); ("BLADE_FP_REPRO", null); ("BLADE_LLVM", llvm); ("BLADE_COMPACT_ROW_CLASSES", gate) ]
                        dir [ "run"; "rows2.blade"; "--print"; "ss,sa"; "--verbose" ]
                let laneOk = isNull llvm || err.Contains ".ll" || out.Contains ".ll"
                (code = 0 && laneOk, valueOf "ss" out, valueOf "sa" out)
            let near (a: string option) (b: string option) =
                match a, b with
                | Some x, Some y -> let (fx, fy) = (Double.Parse x, Double.Parse y) in abs (fx - fy) <= 1e-12 * max (abs fx) 1.0
                | _ -> false
            let cOn = runLane null "1"
            let cOff = runLane null "0"
            let lOn = runLane "1" "1"
            let lOff = runLane "1" "0"
            let show ((ok, a, b): bool * string option * string option) = $"{ok}:{a}/{b}"
            for (lane, (okOn, ssOn, saOn), (okOff, ssOff, saOff)) in [ ("C++", cOn, cOff); ("LLVM", lOn, lOff) ] do
                recordCase $"cli: compact row classes ({lane}): gate off vs on within 1e-12 (symmetric + seed, strict unknown parity)"
                    (okOn && okOff && near ssOn ssOff && near saOn saOff)
                    ($"on {show (okOn, ssOn, saOn)}; off {show (okOff, ssOff, saOff)}")
            let (_, cSs, cSa) = cOn
            let (_, lSs, lSa) = lOn
            recordCase "cli: compact row classes: C++ vs LLVM within 1e-12" (near cSs lSs && near cSa lSa)
                ($"C++ {cSs}/{cSa}; LLVM {lSs}/{lSa}")
            // The bench shape itself (p = 2503), when the checkout is beside
            // the binary: gate off vs on within 1e-12 in both lanes.
            let pairSrc =
                let root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."))
                Path.Combine(root, "bench", "kernels", "pairkernel", "pairkernel.blade")
            if not (File.Exists pairSrc) then
                record "cli: compact row classes: pairkernel p=2503 gate off vs on within 1e-12" TH.Skip "bench/kernels/pairkernel/pairkernel.blade is not beside the binary"
            else
                File.Copy(pairSrc, Path.Combine(dir, "pairkernel.blade"), true)
                let runPk (llvm: string) (gate: string) =
                    let (code, out, err) =
                        spawnWith [ ("BLADE_BLAS", "off"); ("BLADE_FP_REPRO", null); ("BLADE_LLVM", llvm); ("BLADE_COMPACT_ROW_CLASSES", gate) ]
                            dir [ "run"; "pairkernel.blade"; "--print"; "checksum"; "--verbose" ]
                    let laneOk = isNull llvm || err.Contains ".ll" || out.Contains ".ll"
                    (code = 0 && laneOk, valueOf "checksum" out)
                for llvm in [ null; "1" ] do
                    let lane = if isNull llvm then "C++" else "LLVM"
                    let (ok1, v1) = runPk llvm "1"
                    let (ok0, v0) = runPk llvm "0"
                    recordCase $"cli: compact row classes ({lane}): pairkernel p=2503 gate off vs on within 1e-12" (ok1 && ok0 && near v1 v0) ($"on {v1}; off {v0}")
            // B4's fold-only LLVM product stays UNDECORATED in the default
            // build (no `contract`, so it cannot fuse into the add); with the
            // gate off the materialized map keeps its ordinary flags.
            let emitLl (gate: string) =
                let (code, out, _) =
                    spawnWith [ ("BLADE_BLAS", "off"); ("BLADE_FP_REPRO", null); ("BLADE_FP_CONTRACT", null); ("BLADE_LLVM", "1"); ("BLADE_COMPACTION_FUSION", gate) ]
                        dir [ "emit"; "b4a.blade" ]
                (code, out)
            let (eOn, llOn) = emitLl "1"
            let (eOff, llOff) = emitLl "0"
            let bare (ll: string) = Text.RegularExpressions.Regex.IsMatch(ll, @"= fmul double %")
            recordCase "cli: compaction fusion (LLVM, b4a) default build: the fold's self-product is an undecorated fmul"
                (eOn = 0 && eOff = 0 && bare llOn && not (bare llOff)) ""
        finally
            try Directory.Delete(dir, true) with _ -> ()
    // --- AGGREGATE-ONLY GROUPING (Blade.Optimize.planGroupAggregate, round 3 W3-1, gap G71) ---
    // (1) The key -> group map (blade_member.h, the C core both lanes run): a
    // g++-built driver streams keys of several shapes (few / many distinct,
    // Int64 extremes, a run of growth) and checks every answer against a
    // first-occurrence std::unordered_map reference, that a new group's record
    // is all-zero, that records written before a growth survive it, and that
    // an injected allocation failure is REPORTED (init 0 / group -1, the map
    // unchanged) and every free releases every allocation. (2) Run time: gate
    // off vs on, per lane, comparing the COMPLETE output (exit status, stdout
    // with every Float64's raw bits through BLADE_PRINT_BITS, the complete
    // stderr of an abort) with the route and the plan asserted -- every state
    // machine, signed zeros, a final-row singleton, nonzero seeds, the ordered
    // 3a+b fold, an empty group, the three failure orders (066 / 073 / 074),
    // the review's failing count-only / unused producers and its mixed-type
    // fold; and every arm of the CANCELLATION fixture with its exact expected
    // value (the licensed arm's chain changes today's C++ K-lane result).
    if not capabilities.Value.HasGpp || selfExe.IsNone then
        record "group aggregate: the C core and the knob at run time" TH.Skip "requires g++ and the Blade executable"
    else
        let dir = Path.Combine(Path.GetTempPath(), "blade_cli_gagg_" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            Blade.CodeGen.deployRuntimeHeaders dir
            let driver =
                String.concat "\n"
                    [ "#include <cstdio>"
                      "#include <cstdint>"
                      "#include <cstdlib>"
                      "#include <vector>"
                      "#include <unordered_map>"
                      "static unsigned long long g_fail = 0;"
                      "static int g_ord = 0, g_live = 0;"
                      "static void* t_malloc(size_t n) { int k = g_ord++; if (k < 64 && ((g_fail >> k) & 1ull)) return nullptr; void* p = std::malloc(n); if (p) g_live++; return p; }"
                      "static void* t_calloc(size_t n, size_t s) { int k = g_ord++; if (k < 64 && ((g_fail >> k) & 1ull)) return nullptr; void* p = std::calloc(n, s); if (p) g_live++; return p; }"
                      "static void t_free(void* p) { if (p) { g_live--; std::free(p); } }"
                      "#define BLADE_MEMBER_MALLOC(b) t_malloc(b)"
                      "#define BLADE_MEMBER_CALLOC(n, s) t_calloc((n), (s))"
                      "#define BLADE_MEMBER_FREE(p) t_free(p)"
                      "#include \"blade_member.h\""
                      "static uint64_t s_ = 0x2545F4914F6CDD1Dull;"
                      "static uint64_t nx() { s_ ^= s_ << 13; s_ ^= s_ >> 7; s_ ^= s_ << 17; return s_; }"
                      "int main() {"
                      "    const int64_t ex[] = {INT64_MIN, INT64_MAX, 0, -1, 1, INT64_MIN + 1, INT64_MAX - 1};"
                      "    const int distinct[] = {1, 2, 15, 16, 17, 1009, 100003};"
                      "    long rows = 0, bad = 0;"
                      "    for (int shape = 0; shape < 3; shape++)"
                      "    for (int di = 0; di < 7; di++) {"
                      "        const int d = distinct[di];"
                      "        std::vector<int64_t> pool((size_t)d);"
                      "        for (int i = 0; i < d; i++) pool[i] = shape == 0 ? (int64_t)i * 7919 + 13 : shape == 1 ? (int64_t)nx() : ex[i % 7] ^ (int64_t)(i / 7);"
                      "        blade_gkmap_i64 t;"
                      "        g_fail = 0; g_ord = 0; g_live = 0;"
                      "        if (!blade_gkmap_init_i64(&t, 3)) { bad++; continue; }"
                      "        std::unordered_map<int64_t, int64_t> ref;"
                      "        const int n = d * 3 + 11;"
                      "        for (int r = 0; r < n; r++) {"
                      "            int64_t k = pool[(size_t)(r < d ? r : (int)(nx() % (uint64_t)d))];"
                      "            auto it = ref.find(k);"
                      "            int64_t want = it == ref.end() ? (int64_t)ref.size() : it->second;"
                      "            if (it == ref.end()) ref.emplace(k, want);"
                      "            int64_t g = blade_gkmap_group_i64(&t, k);"
                      "            if (g != want) { bad++; if (bad < 5) std::printf(\"BAD group d=%d r=%d got %lld want %lld\\n\", d, r, (long long)g, (long long)want); continue; }"
                      "            blade_gk_slot* rec = blade_gkmap_record_i64(&t, g);"
                      "            if (rec[0].i == 0 && (rec[1].i != 0 || rec[2].d != 0.0)) bad++;"
                      "            rec[0].i++; rec[1].i += k; rec[2].d += 1.0;"
                      "            rows++;"
                      "        }"
                      "        if (blade_gkmap_count_i64(&t) != (int64_t)ref.size()) bad++;"
                      "        // every record survived every growth: count, key sum, float count"
                      "        for (auto& kv : ref) { blade_gk_slot* rec = blade_gkmap_record_i64(&t, kv.second); if (rec[0].i <= 0 || (double)rec[0].i != rec[2].d || (int64_t)((uint64_t)rec[0].i * (uint64_t)kv.first) != rec[1].i) bad++; }"
                      "        blade_gkmap_free_i64(&t);"
                      "        if (g_live != 0) { bad++; std::printf(\"BAD leak d=%d live=%d\\n\", d, g_live); }"
                      "    }"
                      "    // allocation failure: init's k-th allocation, then a growth's (table or records)"
                      "    long reported = 0;"
                      "    for (int k = 0; k < 4; k++) {"
                      "        blade_gkmap_i64 t; g_fail = 1ull << k; g_ord = 0; g_live = 0;"
                      "        if (blade_gkmap_init_i64(&t, 2)) bad++; else reported++;"
                      "        if (g_live != 0) bad++;"
                      "    }"
                      "    for (int k = 4; k < 12; k++) {"
                      "        blade_gkmap_i64 t; g_fail = 0; g_ord = 0; g_live = 0;"
                      "        if (!blade_gkmap_init_i64(&t, 2)) { bad++; continue; }"
                      "        g_fail = 1ull << k;"
                      "        int64_t last = 0, failedAt = -1;"
                      "        for (int64_t key = 0; key < 200; key++) { last = blade_gkmap_group_i64(&t, key * 31); if (last < 0) { failedAt = key; break; } }"
                      "        if (failedAt < 0) bad++; else {"
                      "            reported++;"
                      "            if (blade_gkmap_count_i64(&t) != failedAt) bad++;"
                      "            for (int64_t key = 0; key < failedAt; key++) if (blade_gkmap_group_i64(&t, key * 31) != key) bad++;"
                      "        }"
                      "        blade_gkmap_free_i64(&t);"
                      "        if (g_live != 0) bad++;"
                      "    }"
                      "    std::printf(\"GKMAP CORE: %ld rows, %ld failures reported, %ld bad\\n\", rows, reported, bad);"
                      "    return bad == 0 ? 0 : 1;"
                      "}"
                      "" ]
            let drv = Path.Combine(dir, "gkmap_core.cpp")
            File.WriteAllText(drv, driver)
            (match Build.compileCpp drv dir with
             | Error e -> recordCase "group aggregate: the key -> group map (blade_member.h) driver" false e
             | Ok exe ->
                 match runExecutable exe with
                 | Error e -> recordCase "group aggregate: the key -> group map (blade_member.h) driver" false e
                 | Ok (code, out) ->
                     let summary = out.Replace("\r\n", "\n").Split('\n') |> Array.tryFind (fun l -> l.StartsWith "GKMAP CORE:") |> Option.defaultValue out
                     recordCase "group aggregate: the key -> group map numbers groups by first occurrence, zeroes new records, keeps records across growth, reports every allocation failure and frees everything"
                         (code = 0 && summary.EndsWith " 12 failures reported, 0 bad") summary)
            // (2) RUN TIME. Every comparison below is of the COMPLETE output
            // -- stdout with BLADE_PRINT_BITS=1 (each Float64 followed by its
            // raw IEEE-754 bits, so the comparison is of bits, not of printed
            // digits), the exit status, and the full stderr for an abort --
            // between the gate OFF (`BLADE_GROUP_AGG=0`) and ON, per lane, with
            // the route asserted (the LLVM runs must take the `.ll` lane) and
            // the plan asserted applied (gate on) / disabled (gate off) by
            // `blade plan`, in the identity regime (BLADE_FP_REPRO=1
            // BLADE_FP_CONTRACT=off) unless named otherwise.
            let gen =
                "function gen(k: Int64, seed: Int64) -> Float64 = {\n"
                + "    let h1 = ((k + seed) * 1103515245 + 12345) % 2147483648\n"
                + "    let h2 = (h1 * (h1 / 4096 + 1)) % 2147483648\n"
                + "    2.0 * (Float64(h2) / 2147483648.0)\n"
                + "}\n"
            // a scalar fingerprint over random data, positional and dynamic
            let prog (keyType: string) (keyExpr: string) =
                "type Ix = Idx<3001>\ntype G = Idx<37>\n" + gen
                + $"function agg(k: Array<{keyType} like Ix>, x: Array<Float64 like Ix>) -> Float64 = {{\n"
                + "    let gk = group_keys(k)\n    let g = group_by(x, gk)\n"
                + "    let s = method_for(g) <@> lambda(r: Array<Float64 like RaggedIdx<_>>) -> reduce(r, (+)) |> compute\n"
                + "    let q = method_for(g) <@> lambda(r: Array<Float64 like RaggedIdx<_>>) -> prodsum(r, r) |> compute\n"
                + "    let t = method_for(g) <@> lambda(r: Array<Float64 like RaggedIdx<_>>) -> reduce(r, lambda(a, b) -> a * 0.75 + b, 0.5) |> compute\n"
                + "    prodsum(s, s) + reduce(q, (+)) + reduce(t, (+))\n}\n"
                + $"let keys = method_for(range<Ix>) <@> lambda(i) -> {keyExpr} |> compute\n"
                + "let xs = method_for(range<Ix>) <@> lambda(i) -> gen(Int64(i), 20000038) |> compute\n"
                + "let checksum = agg(keys, xs)\n"
            File.WriteAllText(Path.Combine(dir, "gpos.blade"), prog "G" "Int64(floor(gen(Int64(i), 10000019) * 18.5))")
            File.WriteAllText(Path.Combine(dir, "gdyn.blade"), prog "Int64" "7919 * Int64(floor(gen(Int64(i), 10000019) * 18.5)) + 13")
            // EVERY state printed per group: the four state machines; signed
            // zeros (an all -0.0 group, a -0.0 seed); a singleton at the FINAL
            // row (group 3); a nonzero seed; the ordered non-commutative Int64
            // fold, seeded and unseeded; cancellation inside a group (1e16,
            // -1e16, 1.0); a positional EMPTY group (4) for the seeded, prodsum
            // and count states. Positional, so both lanes route it.
            let statesBody (regimeKeys: string) (keyType: string) =
                "type Row = Idx<9>\n" + regimeKeys
                + $"function st(k: Array<{keyType} like Row>, x: Array<Float64 like Row>, w: Array<Int64 like Row>) = {{\n"
                + "    let gk = group_keys(k)\n    let g = group_by(x, gk)\n    let gw = group_by(w, gk)\n"
                + "    let t = method_for(g) <@> lambda(r: Array<Float64 like RaggedIdx<_>>) -> reduce(r, (+), 0.75) |> compute\n"
                + "    let z = method_for(g) <@> lambda(r: Array<Float64 like RaggedIdx<_>>) -> reduce(r, lambda(a, b) -> a + b, -0.0) |> compute\n"
                + "    let q = method_for(g) <@> lambda(r: Array<Float64 like RaggedIdx<_>>) -> prodsum(r, r) |> compute\n"
                + "    let n = method_for(g) <@> lambda(r: Array<Float64 like RaggedIdx<_>>) -> extents(r) |> compute\n"
                + "    let f = method_for(gw) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> reduce(r, lambda(a, b) -> 3 * a + b, 5) |> compute\n"
                + "    (t, z, q, n, f)\n}\n"
                + "let x: Array<Float64 like Row> = [-0.0, 1e16, -0.0, 0.1, -1e16, 0.2, -0.0, 1.0, -0.0]\n"
                + "let w: Array<Int64 like Row> = [1, 2, 3, 4, 5, 6, 7, 8, 9]\n"
                + "let (t, z, q, n, f) = st(keys, x, w)\n"
            File.WriteAllText(Path.Combine(dir, "gstates.blade"),
                statesBody "type G = Idx<5>\nlet keys: Array<G like Row> = [0, 1, 0, 2, 1, 2, 0, 1, 3]\n" "G")
            // the UNSEEDED machine over the same data (no empty group: an
            // unseeded empty group is the BL8003 fixture below)
            File.WriteAllText(Path.Combine(dir, "gunseeded.blade"),
                "type Row = Idx<9>\ntype G = Idx<4>\n"
                + "function un(k: Array<G like Row>, x: Array<Float64 like Row>, w: Array<Int64 like Row>) = {\n"
                + "    let gk = group_keys(k)\n    let g = group_by(x, gk)\n    let gw = group_by(w, gk)\n"
                + "    let u = method_for(g) <@> lambda(r: Array<Float64 like RaggedIdx<_>>) -> reduce(r, (+)) |> compute\n"
                + "    let f = method_for(gw) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> reduce(r, lambda(a, b) -> 3 * a + b) |> compute\n"
                + "    (u, f)\n}\n"
                + "let keys: Array<G like Row> = [0, 1, 0, 2, 1, 2, 0, 1, 3]\n"
                + "let x: Array<Float64 like Row> = [-0.0, 1e16, -0.0, 0.1, -1e16, 0.2, -0.0, 1.0, -0.0]\n"
                + "let w: Array<Int64 like Row> = [1, 2, 3, 4, 5, 6, 7, 8, 9]\n"
                + "let (u, f) = un(keys, x, w)\n")
            // the dynamic regime's arrays (the LLVM lane refuses a function's
            // dynamic-extent array result -- pre-existing -- so C++ only)
            File.WriteAllText(Path.Combine(dir, "gdynstates.blade"),
                statesBody "let keys: Array<Int64 like Row> = [7, 3, 7, 9, 3, 9, 7, 3, 11]\n" "Int64")
            File.WriteAllText(Path.Combine(dir, "gcancel.blade"),
                "type Row = Idx<12>\n"
                + "function agg(k: Array<Int64 like Row>, x: Array<Float64 like Row>) -> Float64 = {\n"
                + "    let gk = group_keys(k)\n    let g = group_by(x, gk)\n"
                + "    let s = method_for(g) <@> lambda(r: Array<Float64 like RaggedIdx<_>>) -> reduce(r, (+)) |> compute\n"
                + "    prodsum(s, s)\n}\n"
                + "let keys: Array<Int64 like Row> = [5, 5, 5, 5, 5, 5, 5, 5, 5, 6, 6, 6]\n"
                + "let xs: Array<Float64 like Row> = [1e16, -1e16, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 1e8, -1e8, 1.0]\n"
                + "let checksum = agg(keys, xs)\n")
            // COMPLETE-FAILURE fixtures (review finding 4): 066's unseeded
            // empty group (BL8003 at the peel), 073's positional key past the
            // last group (BL8006 at the grouping), 074's intervening BL8013 --
            // tuple results, so the LLVM lane routes them too.
            File.WriteAllText(Path.Combine(dir, "f066.blade"),
                "type Region = Idx<3>\ntype Row = Idx<4>\n"
                + "function agg(k: Array<Region like Row>, v: Array<Int64 like Row>) = {\n"
                + "    let gk = group_keys(k)\n    let g = group_by(v, gk)\n"
                + "    let n = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> extents(r) |> compute\n"
                + "    let s = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> reduce(r, lambda(a, b) -> 3 * a + b) |> compute\n"
                + "    (n, s)\n}\n"
                + "let keys: Array<Region like Row> = [0, 2, 0, 2]\nlet vals: Array<Int64 like Row> = [1, 2, 3, 4]\nlet (n, s) = agg(keys, vals)\n")
            File.WriteAllText(Path.Combine(dir, "f073.blade"),
                "type Region = Idx<3>\ntype Row = Idx<5>\n"
                + "function agg(k: Array<Region like Row>, v: Array<Int64 like Row>) = {\n"
                + "    let gk = group_keys(k)\n    let g = group_by(v, gk)\n"
                + "    let s = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> reduce(r, (+), 0) |> compute\n"
                + "    (s, s)\n}\n"
                + "let keys = method_for(range<Row>) <@> lambda(i) -> (Int64(i) * 2) % 5 |> compute\n"
                + "let vals: Array<Int64 like Row> = [1, 2, 3, 4, 5]\nlet (s, s2) = agg(keys, vals)\n")
            File.WriteAllText(Path.Combine(dir, "f074.blade"),
                "type Region = Idx<3>\ntype Row = Idx<4>\n"
                + "function agg(k: Array<Region like Row>, v: Array<Int64 like Row>, d: Int64) = {\n"
                + "    let gk = group_keys(k)\n    let g = group_by(v, gk)\n    let z = 10 / d\n"
                + "    let s = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> reduce(r, lambda(a, b) -> 3 * a + b) |> compute\n"
                + "    (z, s)\n}\n"
                + "let keys: Array<Region like Row> = [0, 2, 0, 2]\nlet vals: Array<Int64 like Row> = [1, 2, 3, 4]\nlet (z, s) = agg(keys, vals, 0)\n")
            // FINDING 1's producers: a COUNT-only and an UNUSED grouped array
            // whose producer FAILS (10 / 0). Module-level deferred (`lazy`):
            // declined (not proved stored), as written in both gates; the LLVM
            // lane refuses the deferred module read (pre-existing) and the C++
            // lane runs. Body producer (`|> compute`d at its own site, so
            // stored and admitted): the producer fails at its own statement,
            // before the grouping, in both gates and both lanes.
            let lazyHead = "type Two = Idx<2>\nlet den: Array<Int64 like Two> = [1, 0]\nlet lazy = method_for(den) <@> lambda(d) -> 10 / d\nlet k: Array<Int64 like Two> = [7, 7]\nlet v: Array<Int64 like Two> = [3, 4]\n"
            File.WriteAllText(Path.Combine(dir, "wcount.blade"),
                lazyHead + "function counts(key: Array<Int64 like Two>) -> Int64 = {\n    let gk = group_keys(key)\n    let g = group_by(lazy, gk)\n"
                + "    let n = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> extents(r) |> compute\n    reduce(n, (+))\n}\nlet total = counts(k)\n")
            File.WriteAllText(Path.Combine(dir, "wunused.blade"),
                lazyHead + "function sums(key: Array<Int64 like Two>, x: Array<Int64 like Two>) -> Int64 = {\n    let gk = group_keys(key)\n    let g = group_by(x, gk)\n    let unused = group_by(lazy, gk)\n"
                + "    let s = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> reduce(r, (+)) |> compute\n    reduce(s, (+))\n}\nlet total = sums(k, v)\n")
            let bodyHead = "type Two = Idx<2>\nlet den: Array<Int64 like Two> = [1, 0]\nlet k: Array<Int64 like Two> = [7, 7]\nlet v: Array<Int64 like Two> = [3, 4]\n"
            File.WriteAllText(Path.Combine(dir, "wbodycount.blade"),
                bodyHead + "function counts(key: Array<Int64 like Two>, dd: Array<Int64 like Two>) -> Int64 = {\n    let p = method_for(dd) <@> lambda(d) -> 10 / d |> compute\n    let gk = group_keys(key)\n    let g = group_by(p, gk)\n"
                + "    let n = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> extents(r) |> compute\n    reduce(n, (+))\n}\nlet total = counts(k, den)\n")
            File.WriteAllText(Path.Combine(dir, "wbodyunused.blade"),
                bodyHead + "function sums(key: Array<Int64 like Two>, x: Array<Int64 like Two>, dd: Array<Int64 like Two>) -> Int64 = {\n    let p = method_for(dd) <@> lambda(d) -> 10 / d |> compute\n    let gk = group_keys(key)\n    let g = group_by(x, gk)\n    let unused = group_by(p, gk)\n"
                + "    let s = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> reduce(r, (+)) |> compute\n    reduce(s, (+))\n}\nlet total = sums(k, v, den)\n")
            // FINDING 2's type witness: a singleton Int64 group of
            // 9007199254740993 and a multirow one, folded by a Float64-valued
            // kernel into an Int64 aggregate. The plan declines; each lane's
            // own outcome is identical in both gates (the C++ lane rejects the
            // emitted conversion, BL9002, and the LLVM lane rounds through
            // double -- a pre-existing ordinary-fold typing discrepancy,
            // recorded in gap G71, not fixed here).
            File.WriteAllText(Path.Combine(dir, "wtype.blade"),
                "type Row = Idx<3>\ntype Region = Idx<2>\n"
                + "function f(k: Array<Region like Row>, v: Array<Int64 like Row>) = {\n    let gk = group_keys(k)\n    let g = group_by(v, gk)\n"
                + "    let s = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> reduce(r, lambda(a, b) -> Float64(a) + Float64(b)) |> compute\n    (s, s)\n}\n"
                + "let keys: Array<Region like Row> = [0, 1, 1]\nlet vals: Array<Int64 like Row> = [9007199254740993, 9007199254740993, 2]\nlet (s, s2) = f(keys, vals)\n")
            let laneOf (llvm: string) = if isNull llvm then "C++" else "LLVM"
            let routedLl (oe: string) = oe.Contains ".ll" && not (oe.Contains "falling back to C++")
            let routeOk (llvm: string) (oe: string) = if isNull llvm then not (oe.Contains ".ll") else routedLl oe
            let envOf (llvm: string) (gate: string) (repro: bool) (extra: (string * string) list) =
                [ ("BLADE_BLAS", "off"); ("BLADE_LLVM", llvm); ("BLADE_GROUP_AGG", gate)
                  ("BLADE_FP_REPRO", (if repro then "1" else null))
                  ("BLADE_FP_CONTRACT", (if repro then "off" else null))
                  ("BLADE_PRINT_BITS", "1") ] @ extra
            /// (exit, stdout, stderr) of one run, the build's `--verbose` lines stripped from stderr
            let runIt (prog: string) (env: (string * string) list) =
                let (c, o, e) = spawnWith env dir [ "run"; $"{prog}.blade"; "--verbose" ]
                let strip (s: string) =
                    s.Replace("\r\n", "\n").Split('\n')
                    |> Array.filter (fun l -> not (l.StartsWith "[Emit]" || l.StartsWith "[Build]" || l.StartsWith "[cache]" || l.Contains " completed in "))
                    |> String.concat "\n"
                (c, strip o, strip e, o + e)
            let planned (prog: string) (fn: string) (env: (string * string) list) =
                let (_, o, e) = spawnWith env dir [ "plan"; $"{prog}.blade" ]
                let t = o + e
                if t.Contains $"[group-aggregate v1] {fn} @ " && t.Contains $"[group-aggregate v1] {fn} @ " then
                    let line = t.Replace("\r\n", "\n").Split('\n') |> Array.find (fun l -> l.Contains $"[group-aggregate v1] {fn} @ ")
                    if line.Contains ": applied [" then "applied" elif line.Contains "disabled by BLADE_GROUP_AGG" then "disabled" else "declined"
                else "no record"
            let planLine (prog: string) (fn: string) (env: (string * string) list) =
                let (_, o, e) = spawnWith env dir [ "plan"; $"{prog}.blade" ]
                (o + e).Replace("\r\n", "\n").Split('\n') |> Array.tryFind (fun l -> l.Contains $"[group-aggregate v1] {fn} @ ") |> Option.defaultValue ""
            /// Gate off vs on, one lane: identical exit, stdout (raw bits) and
            /// stderr; route; the plan applied on / disabled off (or the
            /// expected decline in both); an abort fixture also pins its
            /// expected code in BOTH gates, so a build failure (BL9002) or any
            /// other abort cannot stand in for the intended run-time one.
            let gateIdentity (prog: string) (fn: string) (llvm: string) (expectPlan: string) (expectExit: int -> bool) (expectCode: string option) (what: string) =
                let lane = laneOf llvm
                let (c0, o0, e0, oe0) = runIt prog (envOf llvm "0" true [])
                let (c1, o1, e1, oe1) = runIt prog (envOf llvm null true [])
                let p0 = planned prog fn (envOf llvm "0" true [])
                let p1 = planned prog fn (envOf llvm null true [])
                let planOk = if expectPlan = "applied" then p0 = "disabled" && p1 = "applied" else p1 = expectPlan
                let codeOk =
                    match expectCode with
                    | Some code -> (o0 + e0).Contains $"error[{code}]" && (o1 + e1).Contains $"error[{code}]" && not ((o1 + e1).Contains "error[BL9002]")
                    | None -> true
                let ok = c0 = c1 && expectExit c1 && o0 = o1 && e0 = e1 && routeOk llvm oe0 && routeOk llvm oe1 && planOk && codeOk
                let codeText = match expectCode with Some code -> $", code {code} in both gates" | None -> ""
                recordCase $"cli: group aggregate ({lane}, {prog}) {what}: gate off == on (exit, stdout with raw bits, complete stderr), route {lane}, plan {expectPlan}{codeText}"
                    ok ($"exit {c0}/{c1}; plan {p0}/{p1}; stdout same={o0 = o1}; stderr same={e0 = e1}; on: {o1.Trim()} {e1.Trim()}")
            for llvm in [ null; "1" ] do
                gateIdentity "gpos" "agg" llvm "applied" ((=) 0) None "random positional data, scalar fingerprint"
                gateIdentity "gdyn" "agg" llvm "applied" ((=) 0) None "random dynamic data, scalar fingerprint"
                gateIdentity "gstates" "st" llvm "applied" ((=) 0) None "every state per group (seeded 0.75 / -0.0, prodsum, count, 3a+b seeded; signed zeros; final-row singleton; empty group)"
                gateIdentity "gunseeded" "un" llvm "applied" ((=) 0) None "the unseeded machine per group (Float64 and the ordered 3a+b Int64; signed zeros; final-row singleton)"
                gateIdentity "f066" "agg" llvm "applied" ((<>) 0) (Some "BL8003") "complete failure: unseeded empty group, BL8003 at the peel"
                gateIdentity "f073" "agg" llvm "applied" ((<>) 0) (Some "BL8006") "complete failure: positional key past the last group, BL8006 at the grouping"
                gateIdentity "f074" "agg" llvm "applied" ((<>) 0) (Some "BL8013") "complete failure: an intervening BL8013 before the peel's BL8003"
                gateIdentity "wbodycount" "counts" llvm "applied" ((<>) 0) (Some "BL8013") "a failing (stored) producer of a COUNT-only grouped array fails at its own statement"
                gateIdentity "wbodyunused" "sums" llvm "applied" ((<>) 0) (Some "BL8013") "a failing (stored) producer of an UNUSED grouped array fails at its own statement"
            gateIdentity "gdynstates" "st" null "applied" ((=) 0) None "every state per group, the dynamic regime (C++ only: the LLVM lane refuses a function's dynamic-extent ARRAY result; gdyn's dynamic scalar and gcancel run both lanes)"
            // the deferred-producer forms decline, so both gates run the CSR;
            // the C++ lane (the LLVM lane refuses the deferred module read)
            for (prog, fn) in [ ("wcount", "counts"); ("wunused", "sums") ] do
                let (c0, o0, e0, _) = runIt prog (envOf null "0" true [])
                let (c1, o1, e1, _) = runIt prog (envOf null null true [])
                let p1 = planned prog fn (envOf null null true [])
                let (cl, _, _, ol) = runIt prog (envOf "1" null true [])
                recordCase $"cli: group aggregate ({prog}) a DEFERRED failing producer of a count-only / unused grouped array: declined, BL8013 in both gates (C++), the LLVM lane falls back"
                    (p1 = "declined" && c0 <> 0 && c0 = c1 && e0 = e1 && o0 = o1 && (o1 + e1).Contains "error[BL8013]" && cl <> 0 && ol.Contains "falling back to C++")
                    ($"plan {p1}; exit {c0}/{c1}; LLVM exit {cl}; {(o1 + e1).Trim()}")
            // THE MIXED-TYPE FOLD (first look's finding 2; second look's HIGH):
            // the plan declines for the TYPE reason, and each lane's ordinary
            // outcome is pinned, routed, identical in both gates -- the LLVM
            // lane exits 0 printing the singleton and the multirow group rounded
            // through double (9007199254740992, 9007199254740994: the
            // pre-existing ordinary-fold typing discrepancy), the C++ lane fails
            // with BL9002 in both gates (only the private build path normalized).
            let typeReason = "is not typed in the state's one scalar representation (Int64): parameters (Int64, Int64) -> Float64 over Int64 elements"
            let normBuild (s: string) =
                Text.RegularExpressions.Regex.Replace(s.Replace("\r\n", "\n"), @"[A-Za-z]:[\\/][^\s""']*blade-build[\\/][^\s""':]*", "<build>")
            for llvm in [ null; "1" ] do
                let lane = laneOf llvm
                let (c0, o0, e0, oe0) = runIt "wtype" (envOf llvm "0" true [])
                let (c1, o1, e1, oe1) = runIt "wtype" (envOf llvm null true [])
                let line = planLine "wtype" "f" (envOf llvm null true [])
                let declinedForType = line.Contains ": declined -- " && line.Contains typeReason
                let routed = routeOk llvm oe0 && routeOk llvm oe1
                let outcome =
                    if isNull llvm then
                        c0 <> 0 && c1 <> 0 && c0 = c1
                        && (o0 + e0).Contains "error[BL9002]" && (o1 + e1).Contains "error[BL9002]"
                        && normBuild (o0 + "\n--\n" + e0) = normBuild (o1 + "\n--\n" + e1)
                    else
                        c0 = 0 && c1 = 0 && o0 = o1 && e0 = e1
                        && o1.Contains "s = [9007199254740992, 9007199254740994]"
                        && o1.Contains "s2 = [9007199254740992, 9007199254740994]"
                let expectText = if isNull llvm then "BL9002 in both gates" else "exit 0, s = s2 = [9007199254740992, 9007199254740994] in both gates"
                recordCase $"cli: group aggregate ({lane}, wtype) a Float64-valued fold into an Int64 aggregate: declined for its types; routed {lane}; {expectText}; complete output identical across gates"
                    (declinedForType && routed && outcome)
                    (let errHead = (if e1.Length > 160 then e1.Substring(0, 160) else e1).Trim()
                     $"plan {line.Trim()}; exit {c0}/{c1}; routed {routed}; {o1.Trim()} {errHead}")
            // THE INLINE EFFECTFUL KEY PRODUCER (Codex second look at e6eadfa2,
            // BLOCKER): a top-level `group_keys` over an unforced inline map
            // whose kernel bumps a captured counter. Every lane evaluates the
            // producer once per CSR pass (pre-existing: calls = [6] over two
            // rows and three passes); the LLVM lane's C-core map route is taken
            // ONLY over a stored key pool, so this program keeps the linear scan
            // (an unknown key lands in bucket 0: sizes = [2, 0]) and never
            // numbers a group past the CSR it sized. The C++ lane refuses the
            // program (BL7004, pre-existing). Per lane, both gates, routed.
            File.WriteAllText(Path.Combine(dir, "winline.blade"),
                "type One = Idx<1>\ntype Two = Idx<2>\nlet mut calls: Array<Int64 like One> = [0]\nlet rows: Array<Int64 like Two> = [0, 0]\n"
                + "function next_key(c: mut Array<Int64 like One>, x: Int64) -> Int64 = {\n    c(0) = c(0) + 1\n    c(0)\n}\n"
                + "let gk = group_keys(method_for(rows) <@> lambda(x) -> next_key(calls, x))\nlet sizes = extents(gk)\n")
            for llvm in [ null; "1" ] do
                let lane = laneOf llvm
                let (c0, o0, e0, oe0) = runIt "winline" (envOf llvm "0" false [])
                let (c1, o1, e1, oe1) = runIt "winline" (envOf llvm null false [])
                let ok =
                    if isNull llvm then
                        c0 <> 0 && c1 <> 0 && (o0 + e0).Contains "error[BL7004]" && (o1 + e1).Contains "error[BL7004]" && o0 = o1 && e0 = e1
                    else
                        routedLl oe0 && routedLl oe1 && c0 = 0 && c1 = 0 && o0 = o1 && e0 = e1
                        && o1.Contains "sizes = [2, 0]" && o1.Contains "calls = [6]"
                let expectText = if isNull llvm then "BL7004 in both gates (the C++ lane refuses it, pre-existing)" else "routed, exit 0, sizes = [2, 0], calls = [6] in both gates (the linear scan: no group past the CSR)"
                recordCase $"cli: group keys ({lane}, winline) an inline writing key producer: {expectText}"
                    ok ($"exit {c0}/{c1}; {o1.Trim()} {e1.Trim()}")
            // THE CANCELLATION FIXTURE, every arm (review finding 6): exit 0,
            // the route, and the exact expected value with its bits.
            // prodsum(s, s) with s = (the 1e16 group's sum, 1.0): the chain
            // gives 2 for the first group (5.0 in all); today's C++ K-lane
            // form gives 0 (1.0); the LLVM lane's flagged recurrence gives 2.
            let five = "checksum = 5.0 #4014000000000000"
            let one = "checksum = 1.0 #3ff0000000000000"
            for llvm in [ null; "1" ] do
                let lane = laneOf llvm
                let arms =
                    [ ("strict, gate on", envOf llvm null true [], five, "applied")
                      ("strict, gate off", envOf llvm "0" true [], five, "disabled")
                      ("default build, gate on (declined)", envOf llvm null false [], (if isNull llvm then one else five), "declined")
                      ("default build, gate off", envOf llvm "0" false [], (if isNull llvm then one else five), "declined")
                      ("licensed arm", envOf llvm "licensed" false [], five, "applied") ]
                for (arm, env, want, wantPlan) in arms do
                    let (c, o, _, oe) = runIt "gcancel" env
                    let p = planned "gcancel" "agg" env
                    let got = o.Replace("\r\n", "\n").Split('\n') |> Array.tryFind (fun l -> l.StartsWith "checksum = ")
                    recordCase $"cli: group aggregate ({lane}) cancellation fixture [1e16, -1e16, 1, 0, 0, 0, 0, 0, 1], {arm}: exit 0, route {lane}, plan {wantPlan}, value {want}"
                        (c = 0 && routeOk llvm oe && p = wantPlan && got = Some want)
                        ($"exit {c}; plan {p}; got {got}")
        finally
            try Directory.Delete(dir, true) with _ -> ()
    // --- MEMBERSHIP HOISTING (Blade.Optimize.planMembershipHoist, round 3 B-T1) ---
    // (1) The C core itself (blade_member.h, the one text both lanes run): a
    // g++-built driver builds 220 sets -- empty, tiny, at the linear
    // crossover, dense, near the bitmap density bound, wide random 64-bit,
    // Int64 extremes, heavy duplicates -- under each of the four chooser
    // settings, checks every probe (each key, its +/-1 neighbours, the
    // extremes, random keys) against the naive scan and that every free
    // releases every allocation, and prints the representation each build
    // chose, which must be Optimize.memberChooseRep's PREFERRED answer (the
    // plan record's restatement of the chooser) since every allocation
    // succeeds. Then DETERMINISTIC ALLOCATOR-FAILURE INJECTION through the
    // header's allocation hooks (Codex diff review of W2-1, finding 3): the
    // bitmap's words failing (-> hash), then also the hash keys or its
    // occupancy failing (-> linear), a hash-preferred set's keys or occupancy
    // failing (-> linear): each answers the naive booleans, leaves nothing
    // allocated after its free (a half-made table freed at once), and REPORTS
    // the route -- preferred, actual and why -- in the struct and through
    // `blade_member_note_i64`'s stderr line (BLADE_MEMBER_VERBOSE=1). (2) One
    // program with a host of each shape, run in BOTH lanes under the knob
    // unset, off and forced linear / bitmap / hash: every run prints the same
    // output AND, under BLADE_MEMBER_VERBOSE=1, each lane's builds report
    // exactly the expected hosts and representations (none at all with the
    // knob off) -- a route assertion, not only a value one. (3) `blade plan`
    // names each forced representation.
    if not capabilities.Value.HasGpp || selfExe.IsNone then
        record "member hoist: the C core and the knob at run time" TH.Skip "requires g++ and the Blade executable"
    else
        let dir = Path.Combine(Path.GetTempPath(), "blade_cli_member_" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            Blade.CodeGen.deployRuntimeHeaders dir
            let driver =
                String.concat "\n"
                    [ "#include <cstdio>"
                      "#include <cstdint>"
                      "#include <cstdlib>"
                      "#include <vector>"
                      "// the header's allocation hooks: bit k of g_fail fails the k-th allocation of a build"
                      "static unsigned long long g_fail = 0;"
                      "static int g_ord = 0, g_live = 0;"
                      "static void* t_malloc(size_t n) { int k = g_ord++; if (k < 64 && ((g_fail >> k) & 1ull)) return nullptr; void* p = std::malloc(n); if (p) g_live++; return p; }"
                      "static void* t_calloc(size_t n, size_t s) { int k = g_ord++; if (k < 64 && ((g_fail >> k) & 1ull)) return nullptr; void* p = std::calloc(n, s); if (p) g_live++; return p; }"
                      "static void t_free(void* p) { if (p) { g_live--; std::free(p); } }"
                      "#define BLADE_MEMBER_MALLOC(b) t_malloc(b)"
                      "#define BLADE_MEMBER_CALLOC(n, s) t_calloc((n), (s))"
                      "#define BLADE_MEMBER_FREE(p) t_free(p)"
                      "#include \"blade_member.h\""
                      "static uint64_t s_ = 0x2545F4914F6CDD1Dull;"
                      "static uint64_t nx() { s_ ^= s_ << 13; s_ ^= s_ >> 7; s_ ^= s_ << 17; return s_; }"
                      "static int64_t wrapAdd(int64_t a, int64_t b) { return (int64_t)((uint64_t)a + (uint64_t)b); }"
                      "static long probe_all(const blade_member_i64& t, const std::vector<int64_t>& v, const std::vector<int64_t>& ps) {"
                      "    long bad = 0;"
                      "    for (size_t q = 0; q < ps.size(); q++) {"
                      "        int naive = 0;"
                      "        for (size_t i = 0; i < v.size(); i++) if (v[i] == ps[q]) { naive = 1; break; }"
                      "        if (blade_member_test_i64(&t, ps[q]) != naive) bad++;"
                      "    }"
                      "    return bad;"
                      "}"
                      "static std::vector<int64_t> probes_of(const std::vector<int64_t>& v) {"
                      "    const int64_t ex[] = {INT64_MIN, INT64_MAX, 0, -1, 1, INT64_MIN + 1, INT64_MAX - 1};"
                      "    std::vector<int64_t> ps;"
                      "    for (size_t i = 0; i < v.size(); i++) { ps.push_back(v[i]); ps.push_back(wrapAdd(v[i], 1)); ps.push_back(wrapAdd(v[i], -1)); }"
                      "    for (int k = 0; k < 7; k++) ps.push_back(ex[k]);"
                      "    for (int k = 0; k < 64; k++) ps.push_back((int64_t)nx());"
                      "    return ps;"
                      "}"
                      "int main() {"
                      "    const int sizes[] = {0, 1, 2, 15, 16, 17, 63, 64, 65, 211, 1009};"
                      "    const int64_t ex[] = {INT64_MIN, INT64_MAX, 0, -1, 1, INT64_MIN + 1, INT64_MAX - 1};"
                      "    long cases = 0, probes = 0, bad = 0;"
                      "    for (int shape = 0; shape < 5; shape++)"
                      "    for (int si = 0; si < 11; si++) {"
                      "        const int m = sizes[si];"
                      "        std::vector<int64_t> v((size_t)m);"
                      "        for (int i = 0; i < m; i++) {"
                      "            uint64_t r = nx();"
                      "            if (shape == 0) v[i] = (int64_t)(r % 101) - 50;"
                      "            else if (shape == 1) v[i] = (int64_t)(r % (uint64_t)(64 * (m + 1))) - 1000;"
                      "            else if (shape == 2) v[i] = (int64_t)r;"
                      "            else if (shape == 3) v[i] = ex[r % 7];"
                      "            else v[i] = (int64_t)(r % 3) * 1000003;"
                      "        }"
                      "        int64_t lo = 0, hi = 0;"
                      "        for (int i = 0; i < m; i++) { if (i == 0 || v[i] < lo) lo = v[i]; if (i == 0 || v[i] > hi) hi = v[i]; }"
                      "        std::vector<int64_t> ps = probes_of(v);"
                      "        for (int forced = 0; forced <= 3; forced++) {"
                      "            blade_member_i64 t;"
                      "            g_fail = 0; g_ord = 0;"
                      "            blade_member_build_i64(&t, m ? v.data() : nullptr, (int64_t)m, forced);"
                      "            std::printf(\"K %d %d %d %lld %lld %d %d\\n\", m, forced, t.kind, (long long)lo, (long long)hi, t.preferred, t.fallback);"
                      "            long b = probe_all(t, v, ps);"
                      "            probes += (long)ps.size();"
                      "            if (b) { bad += b; std::printf(\"BAD m=%d forced=%d kind=%d: %ld probes\\n\", m, forced, t.kind, b); }"
                      "            blade_member_free_i64(&t);"
                      "            if (t.kind != BLADE_MEMBER_NONE) { bad++; std::printf(\"BAD free did not reset\\n\"); }"
                      "            if (g_live != 0) { bad++; std::printf(\"BAD leak m=%d forced=%d live=%d\\n\", m, forced, g_live); g_live = 0; }"
                      "            cases++;"
                      "        }"
                      "    }"
                      "    std::printf(\"MEMBER CORE: %ld builds, %ld probes, %ld bad\\n\", cases, probes, bad);"
                      "    // ---- allocator-failure injection ----"
                      "#if defined(_WIN32)"
                      "    _putenv(\"BLADE_MEMBER_VERBOSE=1\");"
                      "#else"
                      "    setenv(\"BLADE_MEMBER_VERBOSE\", \"1\", 1);"
                      "#endif"
                      "    std::vector<int64_t> dense, wide;"
                      "    for (int i = 0; i < 17; i++) dense.push_back((int64_t)(i * 3) - 7);       // bitmap-preferred (span 49)"
                      "    for (int i = 0; i < 40; i++) wide.push_back((int64_t)(nx() | 1ull));      // hash-preferred (a wide span)"
                      "    struct F { const char* id; const std::vector<int64_t>* v; unsigned long long fail; };"
                      "    const F fs[] = { {\"F1\", &dense, 1ull}, {\"F2\", &dense, 3ull}, {\"F3\", &dense, 5ull},"
                      "                     {\"F4\", &wide, 1ull}, {\"F5\", &wide, 2ull}, {\"F6\", &wide, 0ull} };"
                      "    for (const F& c : fs) {"
                      "        std::vector<int64_t> ps = probes_of(*c.v);"
                      "        blade_member_i64 t;"
                      "        g_live = 0; g_ord = 0; g_fail = c.fail;"
                      "        blade_member_build_i64(&t, c.v->data(), (int64_t)c.v->size(), 0);"
                      "        int liveBuild = g_live;"
                      "        blade_member_note_i64(&t, c.id, \"s\");"
                      "        long b = probe_all(t, *c.v, ps);"
                      "        int pref = t.preferred, kind = t.kind, fb = t.fallback;"
                      "        blade_member_free_i64(&t);"
                      "        std::printf(\"F %s %d %d %d %d %d %ld\\n\", c.id, pref, kind, fb, liveBuild, g_live, b);"
                      "    }"
                      "    return bad == 0 ? 0 : 1;"
                      "}"
                      "" ]
            let drv = Path.Combine(dir, "member_core.cpp")
            File.WriteAllText(drv, driver)
            (match Build.compileCpp drv dir with
             | Error e -> recordCase "member hoist: the C core (blade_member.h) driver" false e
             | Ok exe ->
                 match runExecutable exe with
                 | Error e -> recordCase "member hoist: the C core (blade_member.h) driver" false e
                 | Ok (code, out) ->
                     let lines = out.Replace("\r\n", "\n").Split('\n')
                     let kinds =
                         lines |> Array.choose (fun l ->
                             let p = l.Split(' ')
                             if p.Length = 8 && p.[0] = "K" then
                                 Some (int64 p.[1], int p.[2], int p.[3], int64 p.[4], int64 p.[5], int p.[6], int p.[7])
                             else None)
                     let disagree =
                         kinds |> Array.filter (fun (m, forced, kind, lo, hi, pref, fb) ->
                             let want = Blade.Optimize.memberChooseRep m lo hi forced
                             want <> kind || want <> pref || fb <> 0)
                     let used = kinds |> Array.map (fun (_, _, k, _, _, _, _) -> k) |> Set.ofArray
                     let summary = lines |> Array.tryFind (fun l -> l.StartsWith "MEMBER CORE:") |> Option.defaultValue "(no summary)"
                     recordCase "member hoist: the C core (blade_member.h): every probe of every representation is integer == against every cell, every free releases every allocation"
                         (code = 0 && summary.EndsWith " 0 bad" && kinds.Length = 220) summary
                     recordCase "member hoist: the C core's chooser is Optimize.memberChooseRep's PREFERRED representation (the plan record's restatement), case for case, and with every allocation made the actual one is it"
                         (kinds.Length = 220 && disagree.Length = 0 && used = set [ 1; 2; 3 ])
                         ($"{disagree.Length} disagreements of {kinds.Length}; representations seen: {used}")
                     // (id, preferred, actual, fallback bits, live after build, report text)
                     let wantF =
                         [ ("F1", 2, 3, 1, 2, "preferred bitmap, actual hash -- the bitmap's words could not be allocated")
                           ("F2", 2, 1, 3, 0, "preferred bitmap, actual linear -- the bitmap's words, then the hash table, could not be allocated")
                           ("F3", 2, 1, 3, 0, "preferred bitmap, actual linear -- the bitmap's words, then the hash table, could not be allocated")
                           ("F4", 3, 1, 2, 0, "preferred hash, actual linear -- the hash table could not be allocated")
                           ("F5", 3, 1, 2, 0, "preferred hash, actual linear -- the hash table could not be allocated")
                           ("F6", 3, 3, 0, 2, "preferred hash, actual hash") ]
                     for (id, pref, kind, fb, liveB, report) in wantF do
                         let got =
                             lines |> Array.tryPick (fun l ->
                                 let p = l.Split(' ')
                                 if p.Length = 8 && p.[0] = "F" && p.[1] = id then Some (int p.[2], int p.[3], int p.[4], int p.[5], int p.[6], int64 p.[7]) else None)
                         let reported =
                             lines |> Array.exists (fun l ->
                                 let t = l.Replace("[stderr]: ", "")
                                 t.StartsWith $"[member] {id}: set s, M " && t.TrimEnd().EndsWith report)
                         recordCase $"member hoist: allocator-failure injection {id}: preferred {pref} -> actual {kind} (fallback {fb}), the naive booleans, nothing left allocated after free, the route reported"
                             (got = Some (pref, kind, fb, liveB, 0, 0L) && reported) ($"saw {got}; report found {reported}"))
            // (2) one program, a host of each shape, both lanes, every setting
            let prog =
                String.concat "\n"
                    [ "type Row = Idx<11>"
                      "type Ev = Idx<37>"
                      "type Big = Idx<211>"
                      "let customer: Array<Int64 like Row> = [3, 1, 3, 2, 1, 4, 3, 9, -5, 0, -5]"
                      "let small = [3, 3, 1, -5]"
                      "let evens = [0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 44, 46, 58, 60]"
                      "let wide = [9223372036854775807, -9223372036854775807 - 1, 0, -1, 1, 1000000007, -1000000007, 4611686018427387904, -4611686018427387904, 123456789012345, -98765432109876, 77, -77, 1 - 9223372036854775807, 9223372036854775806, 5, 5, 0]"
                      "let none = compound(customer, mask(customer, lambda(v) -> v > 100))"
                      "let picked = compound(customer, mask(customer, lambda(v) -> v % 3 == 0))"
                      "let k_small = mask(customer, lambda(c) -> contains(small, c))"
                      "let k_even = mask(customer, lambda(c) -> contains(evens, c + 1))"
                      "let k_wide = mask(customer, lambda(c) -> contains(wide, c * 1000000007))"
                      "let k_none = mask(customer, lambda(c) -> contains(none, c))"
                      "let k_pick = mask(customer, lambda(c) -> !contains(picked, c))"
                      "let elig = method_for(range<Ev>) <@> lambda(e) -> (Int64(e) - 18) * 1000003 |> compute"
                      "let cust = method_for(range<Big>) <@> lambda(i) -> ((Int64(i) * 7) % 61 - 30) * 1000003 |> compute"
                      "let flags = method_for(cust) <@> lambda(c) where omp -> contains(elig, c) |> compute"
                      "let fp = reduce(method_for(range<Big>) <@> lambda(i) -> if flags(i) then Int64(i) * 31 else 1, (+), 7)"
                      "" ]
            File.WriteAllText(Path.Combine(dir, "hosts.blade"), prog)
            // the printed VALUES only: `blade run` relays the program's stderr after
            // its stdout (`[stderr]: ...`), where the build reports go
            let strip (o: string) =
                o.Replace("\r\n", "\n").Split('\n')
                |> Array.filter (fun l -> not (l.Contains " completed in ") && l.Trim() <> "" && not (l.StartsWith "[stderr]") && not (l.StartsWith "[member] "))
                |> String.concat "\n"
            let memberReports (o: string) (e: string) =
                (o + "\n" + e).Replace("\r\n", "\n").Split('\n')
                |> Array.map (fun l -> if l.StartsWith "[stderr]: " then l.Substring 10 else l)
                |> Array.filter (fun l -> l.StartsWith "[member] ")
                |> List.ofArray
            // each setting's EXPECTED builds: host -> the representation it
            // prefers and builds (every allocation succeeds here); the knob
            // off builds nothing
            let hosts = [ "k_small"; "k_even"; "k_wide"; "k_none"; "k_pick"; "flags" ]
            let expectedReps (s: string) =
                match s with
                | null -> Some [ "linear"; "bitmap"; "hash"; "linear"; "linear"; "hash" ]
                | "linear" -> Some [ "linear"; "linear"; "linear"; "linear"; "linear"; "linear" ]
                | "bitmap" -> Some [ "bitmap"; "bitmap"; "hash"; "linear"; "bitmap"; "bitmap" ]
                | "hash" -> Some [ "hash"; "hash"; "hash"; "linear"; "hash"; "hash" ]
                | _ -> None
            let settings = [ null; "0"; "linear"; "bitmap"; "hash" ]
            let runs =
                [ for llvm in [ null; "1" ] do
                    for s in settings do
                        let env = [ ("BLADE_BLAS", "off"); ("BLADE_LLVM", llvm); ("BLADE_MEMBER_HOIST", s); ("BLADE_MEMBER_VERBOSE", "1") ]
                        let (c, o, e) = spawnWith env dir [ "run"; "hosts.blade"; "--verbose" ]
                        let lane = if isNull llvm then "C++" else "LLVM"
                        let laneOk = isNull llvm || e.Contains ".ll"
                        let reports = memberReports o e
                        let routeOk =
                            match expectedReps s with
                            | None -> reports.IsEmpty
                            | Some reps ->
                                reports.Length = hosts.Length
                                && List.forall2 (fun h r ->
                                        reports |> List.exists (fun l -> l.StartsWith $"[member] {h}: " && l.EndsWith $"preferred {r}, actual {r}")) hosts reps
                        yield (lane, (if isNull s then "unset" else s), c, strip o, laneOk, routeOk, reports.Length) ]
            let (_, _, c0, o0, _, _, _) = List.head runs
            let agree = runs |> List.forall (fun (_, _, c, o, laneOk, _, _) -> c = 0 && laneOk && o = o0)
            let routes = runs |> List.forall (fun (_, _, _, _, _, routeOk, _) -> routeOk)
            let shown =
                runs |> List.map (fun (l, s, c, o, laneOk, routeOk, n) ->
                    let verdict = if o = o0 then "same" else "DIFFERS"
                    $"{l}/{s}: exit {c}, lane {laneOk}, {verdict}, {n} build report(s), route {routeOk}") |> String.concat "; "
            recordCase "member hoist: both lanes, knob unset / 0 / linear / bitmap / hash -- every run prints the same output"
                (c0 = 0 && agree && o0.Contains "k_small = [true, true, true, false, true, false, true, false, true, false, true]") shown
            recordCase "member hoist: both lanes report every expected build (host, preferred = actual representation) under each setting, and none with the knob off"
                routes shown
            // (2b) ALIAS CHAINS (Codex diff review of W2-1, finding 2), gate on
            // and off, both lanes: a one- and a two-hop alias of a DEFERRED map
            // build nothing (no report: never a build ahead of the map's
            // materialization; the LLVM lane refuses a contains over an
            // unstored set and the program falls back to C++), a two-hop alias
            // of a literal builds exactly one structure with the gate on and
            // none with it off; every run prints the same values.
            File.WriteAllText(Path.Combine(dir, "alias_deferred.blade"),
                String.concat "\n"
                    [ "type Three = Idx<3>"
                      "type Row = Idx<5>"
                      "let src: Array<Int64 like Row> = [1, 4, 3, 9, 2]"
                      "let lazy = method_for(range<Three>) <@> lambda(i) -> Int64(i) + 1"
                      "let B = lazy"
                      "let C = B"
                      "let flags_b = method_for(src) <@> lambda(v) -> contains(B, v) |> compute"
                      "let flags_c = method_for(src) <@> lambda(v) -> contains(C, v) |> compute"
                      "" ])
            File.WriteAllText(Path.Combine(dir, "alias_stored.blade"),
                String.concat "\n"
                    [ "type Row = Idx<5>"
                      "let src: Array<Int64 like Row> = [1, 4, 3, 9, 2]"
                      "let stored = [4, 9, 7]"
                      "let S1 = stored"
                      "let S2 = S1"
                      "let flags_s = method_for(src) <@> lambda(v) -> contains(S2, v) |> compute"
                      "" ])
            for (prog, onReports) in [ ("alias_deferred", 0); ("alias_stored", 1) ] do
                let aliasRuns =
                    [ for llvm in [ null; "1" ] do
                        for gate in [ null; "0" ] do
                            let env = [ ("BLADE_BLAS", "off"); ("BLADE_LLVM", llvm); ("BLADE_MEMBER_HOIST", gate); ("BLADE_MEMBER_VERBOSE", "1") ]
                            let (c, o, e) = spawnWith env dir [ "run"; $"{prog}.blade"; "--verbose" ]
                            let reports = (memberReports o e).Length
                            let want = if isNull gate then onReports else 0
                            // the LLVM lane runs the stored chain itself; it refuses the deferred one
                            let laneOk =
                                if isNull llvm then true
                                elif prog = "alias_deferred" then e.Contains "llvm lane refused"
                                else e.Contains ".ll"
                            let lane = if isNull llvm then "C++" else "LLVM"
                            let g = if isNull gate then "on" else "off"
                            yield ($"{lane}/{g}", c, strip o, reports = want && laneOk, reports) ]
                let (_, ca, oa, _, _) = List.head aliasRuns
                let okAll = aliasRuns |> List.forall (fun (_, c, o, r, _) -> c = 0 && o = oa && r)
                let shownA = aliasRuns |> List.map (fun (k, c, _, r, n) -> $"{k}: exit {c}, {n} report(s), route {r}") |> String.concat "; "
                recordCase $"member hoist: {prog} -- gate on / off, both lanes: the same values, {onReports} build(s) with the gate on, none off"
                    (ca = 0 && okAll) shownA
            // (3) the plan names each forced representation
            for (knob, rep) in [ ("linear", "linear"); ("bitmap", "bitmap"); ("hash", "hash") ] do
                let (c, o, _) = spawnWith [ ("BLADE_MEMBER_HOIST", knob) ] dir [ "plan"; "hosts.blade" ]
                recordCase $"member hoist: `plan` under BLADE_MEMBER_HOIST={knob} names the forced (preferred) representation"
                    (c = 0 && o.Contains "[member-hoist v1] k_even" && o.Contains $"preferred representation: {rep} (span 61, M 20) -- forced by BLADE_MEMBER_HOIST={knob}") ""
        finally
            try Directory.Delete(dir, true) with _ -> ()
    // --- HASH EQUIJOIN (Blade.Optimize.planEquiJoin, round 3 W3-2, item T2) ---
    // (1) The join's C core (blade_join_* in blade_member.h): a g++-built
    // driver builds the CSR over right-key sets of several shapes -- empty,
    // one row, all equal (skew), a small span with negatives, a span at the
    // counting-sort bound, wide random 64-bit, Int64 extremes, heavy
    // duplicates -- with the positional flag off and on, and checks, for every
    // probe key (each right key, its +/-1 neighbours, the extremes, random
    // keys), that the bucket's rows are EXACTLY the right rows j holding that
    // key, ASCENDING (the naive scan's list: forward-filled, stable, bags kept),
    // and -1 exactly when the scan finds none; every free releases every
    // allocation; then each allocation of a build fails in turn (the header's
    // hooks): the build returns -1 with nothing left allocated. (2) The run
    // time: sql-joins-shaped programs COMPILED with BLADE_JOIN_REPORT=1 report
    // the form each build took (`[join] ...`: the counting sort for a small or
    // positional span, the hash for a wide one) and print the same values as
    // the gate off (the six as-written passes).
    if not capabilities.Value.HasGpp || selfExe.IsNone then
        record "hash join: the C core and the run-time route" TH.Skip "requires g++ and the Blade executable"
    else
        let dir = Path.Combine(Path.GetTempPath(), "blade_cli_join_" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            Blade.CodeGen.deployRuntimeHeaders dir
            let driver =
                String.concat "\n"
                    [ "#include <cstdio>"
                      "#include <cstdint>"
                      "#include <cstdlib>"
                      "#include <vector>"
                      "static int g_ord = 0, g_live = 0, g_failat = -1;"
                      "static void* t_malloc(size_t n) { int k = g_ord++; if (k == g_failat) return nullptr; void* p = std::malloc(n ? n : 1); if (p) g_live++; return p; }"
                      "static void* t_calloc(size_t n, size_t s) { int k = g_ord++; if (k == g_failat) return nullptr; void* p = std::calloc(n ? n : 1, s ? s : 1); if (p) g_live++; return p; }"
                      "static void t_free(void* p) { if (p) { g_live--; std::free(p); } }"
                      "#define BLADE_MEMBER_MALLOC(b) t_malloc(b)"
                      "#define BLADE_MEMBER_CALLOC(n, s) t_calloc((n), (s))"
                      "#define BLADE_MEMBER_FREE(p) t_free(p)"
                      "#include \"blade_member.h\""
                      "static uint64_t s_ = 0x9E3779B97F4A7C15ull;"
                      "static uint64_t nx() { s_ ^= s_ << 13; s_ ^= s_ >> 7; s_ ^= s_ << 17; return s_; }"
                      "static int64_t wrapAdd(int64_t a, int64_t b) { return (int64_t)((uint64_t)a + (uint64_t)b); }"
                      "static long check(const blade_join_csr_i64& t, const std::vector<int64_t>& rk) {"
                      "    const int64_t ex[] = {INT64_MIN, INT64_MAX, 0, -1, 1, INT64_MIN + 1, INT64_MAX - 1};"
                      "    std::vector<int64_t> ps;"
                      "    for (size_t i = 0; i < rk.size(); i++) { ps.push_back(rk[i]); ps.push_back(wrapAdd(rk[i], 1)); ps.push_back(wrapAdd(rk[i], -1)); }"
                      "    for (int k = 0; k < 7; k++) ps.push_back(ex[k]);"
                      "    for (int k = 0; k < 64; k++) ps.push_back((int64_t)nx());"
                      "    long bad = 0;"
                      "    if (t.off[t.nb] != (int64_t)rk.size()) bad++;"
                      "    for (size_t q = 0; q < ps.size(); q++) {"
                      "        std::vector<int64_t> naive;"
                      "        for (size_t j = 0; j < rk.size(); j++) if (rk[j] == ps[q]) naive.push_back((int64_t)j);"
                      "        int64_t b = blade_join_lookup_i64(&t, ps[q]);"
                      "        std::vector<int64_t> got;"
                      "        if (b >= 0) { if (b >= t.nb) { bad++; continue; } for (int64_t x = t.off[b]; x < t.off[b + 1]; x++) got.push_back(t.perm[x]); }"
                      "        if (got != naive) bad++;"
                      "    }"
                      "    return bad;"
                      "}"
                      "int main() {"
                      "    const int sizes[] = {0, 1, 2, 7, 64, 211, 1009};"
                      "    const int64_t ex[] = {INT64_MIN, INT64_MAX, 0, -1, 1, INT64_MIN + 1, INT64_MAX - 1};"
                      "    long builds = 0, bad = 0, direct = 0, hash = 0;"
                      "    for (int shape = 0; shape < 7; shape++)"
                      "    for (int si = 0; si < 7; si++) {"
                      "        const int m = sizes[si];"
                      "        std::vector<int64_t> rk((size_t)m);"
                      "        for (int i = 0; i < m; i++) {"
                      "            uint64_t r = nx();"
                      "            if (shape == 0) rk[i] = 5;"
                      "            else if (shape == 1) rk[i] = (int64_t)(r % 17) - 8;"
                      "            else if (shape == 2) rk[i] = (int64_t)(r % 4096);"
                      "            else if (shape == 3) rk[i] = (int64_t)r;"
                      "            else if (shape == 4) rk[i] = ex[r % 7];"
                      "            else if (shape == 5) rk[i] = (int64_t)(r % 3) * 1000003;"
                      "            else rk[i] = (int64_t)(r % 401);"
                      "        }"
                      "        for (int positional = 0; positional <= 1; positional++) {"
                      "            blade_join_csr_i64 t;"
                      "            g_ord = 0; g_live = 0; g_failat = -1;"
                      "            if (blade_join_build_i64(&t, m ? rk.data() : nullptr, (int64_t)m, positional) != 0) { bad++; std::printf(\"BAD build failed m=%d\\n\", m); continue; }"
                      "            if (t.kind == BLADE_JOIN_DIRECT) direct++; else if (t.kind == BLADE_JOIN_HASH) hash++;"
                      "            { int64_t lo = 0, hi = 0; for (int i = 0; i < m; i++) { if (i == 0 || rk[i] < lo) lo = rk[i]; if (i == 0 || rk[i] > hi) hi = rk[i]; }"
                      "              std::printf(\"K %d %d %d %lld %lld\\n\", m, positional, t.kind, (long long)lo, (long long)hi); }"
                      "            long b = check(t, rk);"
                      "            if (b) { bad += b; std::printf(\"BAD shape=%d m=%d positional=%d kind=%d: %ld\\n\", shape, m, positional, t.kind, b); }"
                      "            blade_join_free_i64(&t);"
                      "            if (t.kind != BLADE_JOIN_NONE || g_live != 0) { bad++; std::printf(\"BAD free m=%d live=%d\\n\", m, g_live); }"
                      "            builds++;"
                      "        }"
                      "    }"
                      "    // allocation failures: every allocation of a direct and of a hash build, in turn"
                      "    long fails = 0, failbad = 0;"
                      "    for (int form = 0; form < 2; form++) {"
                      "        std::vector<int64_t> rk;"
                      "        for (int i = 0; i < 97; i++) rk.push_back(form == 0 ? (int64_t)(i % 13) : (int64_t)nx());"
                      "        for (int k = 0; k < 8; k++) {"
                      "            blade_join_csr_i64 t;"
                      "            g_ord = 0; g_live = 0; g_failat = k;"
                      "            int rc = blade_join_build_i64(&t, rk.data(), (int64_t)rk.size(), 0);"
                      "            if (rc == 0) { if (g_ord <= k) { blade_join_free_i64(&t); break; } failbad++; blade_join_free_i64(&t); }"
                      "            else if (g_live != 0 || t.kind != BLADE_JOIN_NONE) failbad++;"
                      "            fails++;"
                      "        }"
                      "    }"
                      "    // the chooser's THRESHOLDS, below / at / above each, both positional flags:"
                      "    // the max(4 M, 4096) switch (its 4096 floor at M = 5, its 4 M branch at M = 1025"
                      "    // and 2000), the 2^20 positional force (M = 5, and M = 300000 where 4 M > 2^20),"
                      "    // the 2^26 span cap (M = 5, and M = 2^25 where 4 M > 2^26), M = 0, a negative lo,"
                      "    // the full Int64 span. blade_join_choose_i64 itself (no build: the shapes are too"
                      "    // large to allocate), compared with Optimize.joinChooseDirect line by line."
                      "    struct BC { long long m; long long lo; unsigned long long span_m1; };"
                      "    std::vector<BC> bc;"
                      "    const unsigned long long d[] = {0ull, 1ull, 2ull};"
                      "    for (int k = 0; k < 3; k++) {"
                      "        bc.push_back({5, 0, 4095ull + d[k]}); bc.push_back({5, -7, 4095ull + d[k]});"
                      "        bc.push_back({1025, 0, 4099ull + d[k]}); bc.push_back({2000, 0, 7999ull + d[k]});"
                      "        bc.push_back({5, 0, (1ull << 20) - 1 + d[k]}); bc.push_back({300000, 0, (1ull << 20) - 1 + d[k]});"
                      "        bc.push_back({5, 0, (1ull << 26) - 1 + d[k]}); bc.push_back({1ll << 25, 0, (1ull << 26) - 1 + d[k]});"
                      "    }"
                      "    bc.push_back({0, 0, 0}); bc.push_back({0, INT64_MIN, ~0ull}); bc.push_back({5, INT64_MIN, ~0ull}); bc.push_back({1, 42, 0});"
                      "    long nbc = 0;"
                      "    for (const BC& c : bc)"
                      "        for (int positional = 0; positional <= 1; positional++) {"
                      "            int64_t lo = (int64_t)c.lo; int64_t hi = (int64_t)((uint64_t)lo + c.span_m1);"
                      "            int kind = blade_join_choose_i64((int64_t)c.m, lo, hi, positional);"
                      "            std::printf(\"B %lld %d %d %lld %lld\\n\", c.m, positional, kind, (long long)lo, (long long)hi);"
                      "            nbc++;"
                      "        }"
                      "    std::printf(\"JOIN CORE: %ld builds (%ld counting sort, %ld hash), %ld bad; %ld injected failures, %ld bad\\n\", builds, direct, hash, bad, fails, failbad);"
                      "    return (bad == 0 && failbad == 0) ? 0 : 1;"
                      "}"
                      "" ]
            let drv = Path.Combine(dir, "join_core.cpp")
            File.WriteAllText(drv, driver)
            (match Build.compileCpp drv dir with
             | Error e -> recordCase "hash join: the C core (blade_join_*) driver" false e
             | Ok exe ->
                 match runExecutable exe with
                 | Error e -> recordCase "hash join: the C core (blade_join_*) driver" false e
                 | Ok (code, out) ->
                     let summary =
                         out.Replace("\r\n", "\n").Split('\n') |> Array.tryFind (fun l -> l.StartsWith "JOIN CORE:") |> Option.defaultValue "(no summary)"
                     let formsSeen = not (summary.Contains "(0 counting sort") && not (summary.Contains ", 0 hash)")
                     recordCase "hash join: the C core -- every bucket is exactly the right rows holding its key, ascending (forward-filled, bags kept), -1 exactly when none; both forms built; every free and every failed build releases everything"
                         (code = 0 && summary.Contains "98 builds" && summary.Contains " 0 bad; " && summary.EndsWith " 0 bad" && formsSeen) summary
                     // the plan record's restatement of the chooser
                     // (Optimize.joinChooseDirect) agrees with the C core, case for case
                     let kinds =
                         out.Replace("\r\n", "\n").Split('\n') |> Array.choose (fun l ->
                             let p = l.Split(' ')
                             if p.Length = 6 && p.[0] = "K" then Some (int64 p.[1], p.[2] = "1", int p.[3], int64 p.[4], int64 p.[5]) else None)
                     let disagree =
                         kinds |> Array.filter (fun (m, pos, kind, lo, hi) ->
                             let wantDirect = Blade.Optimize.joinChooseDirect m lo hi pos
                             (kind = 1) <> wantDirect)
                     recordCase "hash join: the C core's form chooser is Optimize.joinChooseDirect's (the plan record's restatement), case for case"
                         (kinds.Length = 98 && disagree.Length = 0) ($"{disagree.Length} disagreements of {kinds.Length}")
                     // ... and at every THRESHOLD (below / at / above), both flags
                     let bounds =
                         out.Replace("\r\n", "\n").Split('\n') |> Array.choose (fun l ->
                             let p = l.Split(' ')
                             if p.Length = 6 && p.[0] = "B" then Some (int64 p.[1], p.[2] = "1", int p.[3], int64 p.[4], int64 p.[5]) else None)
                     let bDisagree =
                         bounds |> Array.filter (fun (m, pos, kind, lo, hi) -> (kind = 1) <> Blade.Optimize.joinChooseDirect m lo hi pos)
                     // the thresholds really switch: each below / at pair differs where the rule says it must
                     let kindOf (m: int64) (pos: bool) (lo: int64) (spanM1: uint64) =
                         bounds |> Array.tryPick (fun (m', pos', kind, lo', hi') ->
                             if m' = m && pos' = pos && lo' = lo && uint64 hi' - uint64 lo' = spanM1 then Some kind else None)
                     let switches =
                         [ kindOf 5L false 0L 4095UL, kindOf 5L false 0L 4096UL                          // 4096 floor
                           kindOf 2000L false 0L 7999UL, kindOf 2000L false 0L 8000UL                    // the 4 M branch
                           kindOf 1025L false 0L 4099UL, kindOf 1025L false 0L 4100UL
                           kindOf 5L true 0L ((1UL <<< 20) - 1UL), kindOf 5L true 0L (1UL <<< 20)        // the positional force
                           kindOf (1L <<< 25) false 0L ((1UL <<< 26) - 1UL), kindOf (1L <<< 25) false 0L (1UL <<< 26) ] // the span cap
                         |> List.forall (fun (a, b) -> a = Some 1 && b = Some 2)
                     recordCase "hash join: the chooser's thresholds -- max(4 M, 4096) (its 4096 floor and its 4 M branch, M > 1024), the 2^20 positional force, the 2^26 span cap, M = 0, both positional flags -- below / at / above each: C core = Optimize.joinChooseDirect, and each switches DIRECT -> HASH exactly at its threshold"
                         (bounds.Length = 56 && bDisagree.Length = 0 && switches) ($"{bDisagree.Length} disagreements of {bounds.Length}; thresholds switch: {switches}"))
            // (2) the run-time route, gate on vs off
            let progs =
                [ ("hj_small",
                   [ "type L = Idx<7>"; "type R = Idx<5>"
                     "let lk: Array<Int64 like L> = [3, -1, 3, 8, 0, -1, 3]"
                     "let la: Array<Int64 like L> = [1, 2, 3, 4, 5, 6, 7]"
                     "let rk: Array<Int64 like R> = [-1, 3, 0, 3, -1]"
                     "let ra: Array<Int64 like R> = [10, 20, 30, 40, 50]"
                     "let rel = compound(method_for(la, ra) <@> lambda(l, r) -> l * 100 + r, mask(method_for(lk, rk) <@> lambda(l, r) -> l == r, lambda(e) -> e))"
                     "let n_rel = extents(rel)"
                     "let fp = reduce(rel, lambda(a, b) -> 3 * a + b, 0)" ],
                   "form counting sort, buckets 5; N 7, |J| 11")
                  ("hj_wide",
                   [ "type L = Idx<4>"; "type R = Idx<5>"
                     "let lk: Array<Int64 like L> = [1000000007, -5, 42, 1000000007]"
                     "let la: Array<Int64 like L> = [1, 2, 3, 4]"
                     "let rk: Array<Int64 like R> = [42, 1000000007, 7, 1000000007, -5]"
                     "let ra: Array<Int64 like R> = [10, 20, 30, 40, 50]"
                     "let rel = compound(method_for(la, ra) <@> lambda(l, r) -> l * 100 + r, mask(method_for(lk, rk) <@> lambda(l, r) -> l == r, lambda(e) -> e))"
                     "let n_rel = extents(rel)"
                     "let fp = reduce(rel, lambda(a, b) -> 3 * a + b, 0)" ],
                   "form hash, buckets 4; N 4, |J| 6")
                  ("hj_positional",
                   [ "type K = Idx<6>"; "type L = Idx<4>"; "type R = Idx<5>"
                     "let lk: Array<K like L> = [5, 0, 2, 5]"
                     "let la: Array<Int64 like L> = [1, 2, 3, 4]"
                     "let rk: Array<K like R> = [5, 1, 5, 0, 5]"
                     "let ra: Array<Int64 like R> = [10, 20, 30, 40, 50]"
                     "let rel = compound(method_for(la, ra) <@> lambda(l, r) -> l * 100 + r, mask(method_for(lk, rk) <@> lambda(l, r) -> l == r, lambda(e) -> e))"
                     "let n_rel = extents(rel)"
                     "let fp = reduce(rel, lambda(a, b) -> 3 * a + b, 0)" ],
                   "form counting sort, buckets 6; N 4, |J| 7") ]
            let strip (o: string) =
                o.Replace("\r\n", "\n").Split('\n')
                |> Array.filter (fun l -> not (l.Contains " completed in ") && l.Trim() <> "" && not (l.StartsWith "[stderr]") && not (l.StartsWith "[join] "))
                |> String.concat "\n"
            let joinReports (o: string) (e: string) =
                (o + "\n" + e).Replace("\r\n", "\n").Split('\n')
                |> Array.map (fun l -> if l.StartsWith "[stderr]: " then l.Substring 10 else l)
                |> Array.filter (fun l -> l.StartsWith "[join] ")
                |> List.ofArray
            // BLADE_JOIN_REPORT is a COMPILE-TIME gate (review W3-2 finding
            // 3): each program is COMPILED under one environment and its
            // executable RUN under another, so the two settings are pinned
            // independently -- compile-on / run-off reports, compile-off /
            // run-on (BLADE_JOIN_REPORT and BLADE_MEMBER_VERBOSE both set at
            // run time) does not; the gate off builds no CSR.
            let runExe (env: (string * string) list) (exe: string) =
                let psi = System.Diagnostics.ProcessStartInfo(exe)
                for (k, v) in env do
                    if isNull v then psi.Environment.Remove k |> ignore else psi.Environment.[k] <- v
                psi.WorkingDirectory <- dir
                psi.RedirectStandardOutput <- true
                psi.RedirectStandardError <- true
                psi.UseShellExecute <- false
                psi.CreateNoWindow <- true
                use p = System.Diagnostics.Process.Start psi
                let o = Blade.Runtime.readToEndOffPool p.StandardOutput
                let e = Blade.Runtime.readToEndOffPool p.StandardError
                if p.WaitForExit 120000 then (p.ExitCode, o.Result, e.Result) else ((try p.Kill() with _ -> ()); (-1, o.Result, "timed out"))
            let quiet = [ ("BLADE_JOIN_REPORT", null); ("BLADE_MEMBER_VERBOSE", null) ]
            let loud = [ ("BLADE_JOIN_REPORT", "1"); ("BLADE_MEMBER_VERBOSE", "1") ]
            for (nm, lines, wantForm) in progs do
                File.WriteAllText(Path.Combine(dir, nm + ".blade"), String.concat "\n" (lines @ [ "" ]))
                let compileTo (tag: string) (env: (string * string) list) =
                    let exe = Path.Combine(dir, $"{nm}_{tag}.exe")
                    let (c, _, e) = spawnWith ([ ("BLADE_BLAS", "off"); ("BLADE_EXE_CACHE", "0") ] @ env) dir [ "compile"; nm + ".blade"; "-o"; exe ]
                    if c = 0 && File.Exists exe then Ok exe else Error ($"compile {tag}: exit {c}: {e}")
                match compileTo "rep" [ ("BLADE_JOIN_REPORT", "1"); ("BLADE_MEMBER_VERBOSE", null) ],
                      compileTo "norep" [ ("BLADE_JOIN_REPORT", null); ("BLADE_MEMBER_VERBOSE", "1") ],
                      compileTo "off" [ ("BLADE_JOIN_REPORT", "1"); ("BLADE_HASH_JOIN", "0") ] with
                | Ok exeRep, Ok exeNoRep, Ok exeOff ->
                    let (c1, o1, e1) = runExe quiet exeRep     // compile-on / run-off
                    let (c2, o2, e2) = runExe loud exeNoRep    // compile-off / run-on
                    let (c3, o3, e3) = runExe loud exeOff      // the gate off
                    let r1, r2, r3 = joinReports o1 e1, joinReports o2 e2, joinReports o3 e3
                    let same = strip o1 = strip o2 && strip o2 = strip o3
                    let routeOk = (r1 = [ $"[join] rel: CSR over rk, M 5, {wantForm}" ]) && r2.IsEmpty && r3.IsEmpty
                    recordCase $"hash join: {nm} -- compiled with BLADE_JOIN_REPORT=1 and run with no switch it reports `{wantForm}`; compiled without it and run with BLADE_JOIN_REPORT=1 BLADE_MEMBER_VERBOSE=1 it reports nothing; the gate off builds no CSR; all three print the same values"
                        (c1 = 0 && c2 = 0 && c3 = 0 && same && routeOk) ($"compile-on/run-off: {r1}; compile-off/run-on: {r2.Length} report(s); gate off: {r3.Length}; values same: {same}")
                | a, b, c ->
                    let why = [ a; b; c ] |> List.choose (function Error e -> Some e | Ok _ -> None) |> String.concat " | "
                    recordCase $"hash join: {nm} -- compile-time report gate" false why
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

    // --- Provider store paths resolve against the SOURCE directory ---
    //
    // `blade run` executes the program with the source file's directory as its
    // working directory, but the compile-time metadata read opened a relative
    // store path against the COMPILER's cwd: `blade run examples/09_...` from
    // the repo root died BL2008 while the same program run from examples/
    // worked. Every verb now resolves a relative store path against the source
    // file's directory at compile time (ProviderRegistry.SourceBase), and the
    // emitted C++ still carries the path as the program wrote it.
    if selfExe.IsNone then
        record "provider paths: check from another directory" TH.Skip "no Blade executable beside the test assembly"
    else
        let dir = Path.Combine(Path.GetTempPath(), "blade_cli_srcrel_" + Guid.NewGuid().ToString("N"))
        let sub = Path.Combine(dir, "sub")
        Directory.CreateDirectory sub |> ignore
        try
            File.WriteAllText(Path.Combine(sub, "data.csv"), "1.0,2.0\n3.0,4.0\n")
            let prog (path: string) =
                "import csv as csvd\n"
                + $"let h = csvd.load(\"{path}\")\n"
                + "let M: Array<Float64 like Idx<2>, Idx<2>> = h.vars.data |> csvd.read\n"
                + "let s = reduce(reduce(M, (+)), (+))\n"
            File.WriteAllText(Path.Combine(sub, "p.blade"), prog "data.csv")
            File.WriteAllText(Path.Combine(sub, "q.blade"), prog "no_such_store.csv")
            File.WriteAllText(Path.Combine(sub, "r.blade"), prog (Path.Combine(sub, "data.csv").Replace('\\', '/')))
            // From the PARENT directory: the relative store is found beside the source.
            let (code, out, err) = spawn dir [ "check"; Path.Combine("sub", "p.blade") ]
            recordCase "provider paths: `check` resolves a relative store against the source directory"
                (code = 0 && out.Contains "OK" && not (err.Contains "BL2008")) (out + err)
            let (code, out, err) = spawn dir [ "plan"; Path.Combine("sub", "p.blade") ]
            recordCase "provider paths: `plan` resolves it too" (code = 0 && not (err.Contains "BL2008")) (out + err)
            // The C++ names the store exactly as written: run time resolves it
            // against the executable's working directory, as it always did.
            let (code, out, err) = spawn dir [ "emit"; Path.Combine("sub", "p.blade") ]
            let asWritten =
                out.Contains "\"data.csv\""
                && not (out.Contains "sub/data.csv" || out.Contains "sub\\data.csv" || out.Contains "sub\\\\data.csv")
            recordCase "provider paths: `emit` compiles and bakes the path as written"
                (code = 0 && asWritten)
                (if code <> 0 then out + err elif not asWritten then "the C++ does not name the store as written" else "")
            // An absolute store path is untouched.
            let (code, out, err) = spawn dir [ "check"; Path.Combine("sub", "r.blade") ]
            recordCase "provider paths: an absolute store path still resolves" (code = 0 && out.Contains "OK") (out + err)
            // A store that is not there is still BL2008 at the load site, and
            // the message names where it was looked for.
            let (code, out, err) = spawn dir [ "check"; Path.Combine("sub", "q.blade") ]
            recordCase "provider paths: a missing store is still BL2008, naming the resolved path"
                (code <> 0 && err.Contains "BL2008" && err.Contains "no_such_store.csv"
                 && err.Contains (Path.Combine(Path.GetFullPath sub, "no_such_store.csv")))
                (out + err)
            // With no source scope (the harness compiling a string), resolution
            // is the process working directory, as before.
            recordCase "provider paths: no scope leaves a relative path untouched"
                (Blade.ProviderRegistry.SourceBase.resolve "data/x.csv" = "data/x.csv"
                 && Blade.ProviderRegistry.SourceBase.within (Some sub) (fun () ->
                        Blade.ProviderRegistry.SourceBase.resolve "data.csv" = Path.Combine(Path.GetFullPath sub, "data.csv")
                        && Blade.ProviderRegistry.SourceBase.resolve "s3://bucket/x" = "s3://bucket/x")
                 && Blade.ProviderRegistry.SourceBase.current () = None)
                ""
            if capabilities.Value.HasGpp then
                let (code, out, err) = spawn dir [ "run"; Path.Combine("sub", "p.blade") ]
                recordCase "provider paths: `run` from another directory folds and runs"
                    (code = 0 && out.Contains "s = 10.0") (out + err)
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

    // --- An arrow parameter's array instance prints as its declaration ---
    //
    // A call handing an arrow parameter an array calls the declaration's ARRAY
    // INSTANCE, emitted as `walk__arrow1`; a generic declaration's
    // specialization is emitted as `divide_HM_<id>_<type>`. Neither is a name
    // the program wrote (formalism 4.3: the instance IS the declaration), so no
    // user-facing surface shows one: a panic's frames in either lane, `blade
    // plan`, the editor payload, the REPL's session names. The corpus pins the
    // frame by substring (functions/279-280, `// ABORT: at walk`), which
    // `at walk__arrow1` contains too -- the absence is asserted here.
    // The detail (a whole JSON payload, a panic) is shown only on failure.
    let expectCase name cond (detail: string) = recordCase name cond (if cond then "" else detail)
    // A lifted lambda's emitted `__lambda_<id>` (and the call-site wrappers
    // `__wrap_<id>` / partial-application parameters `__pa<id>`) are no name
    // the program wrote either: a lambda prints as the `let` binding it is
    // bound to, else `lambda in <scope>`; an operator function as its
    // section, `(/) in <scope>` (IR.anonymousCallableName).
    let internalName (s: string) =
        s.Contains "__arrow" || s.Contains "_HM_" || s.Contains "__lambda" || s.Contains "__wrap" || s.Contains "__pa"
    let frameLines (s: string) =
        s.Replace("\r\n", "\n").Split('\n')
        |> Array.filter (fun l -> l.StartsWith "  at ")
        |> Array.map (fun l -> l.TrimEnd())
        |> List.ofArray
    let arrowAbort =
        "type L = Idx<3>\n"
        + "let A: Array<Float64 like L> = [1.0, 2.0, 3.0]\n"
        + "function walk(c, n: Int64) -> Float64 = if n == 0 then c(0) else c(n) + walk(c, n - 1)\n"
        + "function outer(m: T^1) = walk(m, 5)\n"
        + "let s = outer(A)\n"
    let hmAbort =
        "function divide(x: T^0, y: T^0) = x / y\n"
        + "let a = divide(4.0, 2.0)\n"
        + "let i: Int64 = 1\n"
        + "let z: Int64 = 0\n"
        + "let b = divide(i, z)\n"
    // Lambdas: bound by a `let` (top level, nested, block-local), unbound
    // (a reduce kernel, a returned closure), an operator section, a partial
    // application bound by a `let`.
    let lambdaAborts =
        [ ("let-lambda",
           "let z: Int64 = 0\n"
           + "let f = lambda(x: Int64) -> x / z\n"
           + "function call_it(n: Int64) -> Int64 = f(n) + 1\n"
           + "let r = call_it(4)\n",
           [ "  at f"; "  at call_it" ])
          ("kernel-lambda",
           "let xs = [4, 2, 0]\n"
           + "let r = reduce(xs, lambda(a, b) -> a / b)\n",
           [ "  at lambda in r" ])
          ("returned-closure",
           "function mk(i: Int64) = lambda(j: Int64) -> j / i\n"
           + "let g = mk(0)\n"
           + "let r = g(3)\n",
           [ "  at lambda in mk" ])
          ("nested-lambda",
           "let z: Int64 = 0\n"
           + "let outer = lambda(x: Int64) -> {\n"
           + "    let inner = lambda(y: Int64) -> y / z\n"
           + "    inner(x) + 1\n"
           + "}\n"
           + "let r = outer(5)\n",
           [ "  at inner"; "  at outer" ])
          ("block-lambda",
           "type L = Idx<3>\n"
           + "let A: Array<Float64 like L> = [1.0, 2.0, 3.0]\n"
           + "function h(n: Int64) -> Float64 = {\n"
           + "    let pick = lambda(i: Int64) -> A(i)\n"
           + "    pick(n) + 1.0\n"
           + "}\n"
           + "let v = h(7)\n",
           [ "  at pick"; "  at h" ])
          ("section",
           "let xs = [4, 2, 0]\n"
           + "let r = reduce(xs, (/))\n",
           [ "  at (/) in r" ])
          ("partial-application",
           "function divide(a: Int64, b: Int64) -> Int64 = a / b\n"
           + "let g = divide(4)\n"
           + "let r = g(0)\n",
           [ "  at divide"; "  at g" ]) ]
    let expectedFrames =
        [ ("arrow", arrowAbort, [ "  at walk"; "  at outer" ]); ("hm", hmAbort, [ "  at divide" ]) ]
        @ lambdaAborts
    for (tag, src, frames) in expectedFrames do
        let shown = frames |> List.map (fun l -> l.Trim()) |> String.concat "; "
        let label = $"names: the interpreter's {tag} panic frames name the declaration ({shown})"
        match Blade.Lowering.lowerCaptured src with
        | Error e, _ -> record label TH.Fail e
        | Ok ir, _ ->
            let r = Blade.Interp.Run.runProgram ir $"names_{tag}" Blade.Interp.Value.defaultLimits
            expectCase label (frameLines r.Stderr = frames && not (internalName r.Stderr)) r.Stderr
    if not capabilities.Value.HasGpp || selfExe.IsNone then
        record "names: compiled panic frames name the declaration" TH.Skip "requires g++ and the Blade executable"
    else
        let dir = Path.Combine(Path.GetTempPath(), "blade_cli_names_" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            for (tag, src, frames) in expectedFrames do
                File.WriteAllText(Path.Combine(dir, $"{tag}.blade"), src)
                let (code, out, err) = spawn dir [ "run"; $"{tag}.blade" ]
                let shown = frames |> List.map (fun l -> l.Trim()) |> String.concat "; "
                expectCase $"names: the compiled {tag} panic frames name the declaration ({shown})"
                    // `run` relays the program's stderr on its own stdout.
                    (code <> 0 && frameLines (out + err) = frames && not (internalName (out + err))) (out + err)
        finally
            try Directory.Delete(dir, true) with _ -> ()
    // `blade plan`: a decision made inside an array instance is the origin's.
    if selfExe.IsNone then
        record "names: plan subjects name the declaration" TH.Skip "requires the Blade executable"
    else
        let dir = Path.Combine(Path.GetTempPath(), "blade_cli_names_plan_" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            File.WriteAllText(Path.Combine(dir, "p.blade"),
                "type L = Idx<3>\n"
                + "let A: Array<Float64 like L> = [1.0, 2.0, 3.0]\n"
                + "function centered(c) = {\n"
                + "    let m = (c(0) + c(1) + c(2)) / 3.0\n"
                + "    let m2 = (c(0) + c(1) + c(2)) / 3.0\n"
                + "    method_for(range<L>) <@> lambda(j) -> c(j) - m - m2\n"
                + "}\n"
                + "let r = centered(A) |> compute\n")
            let (code, out, err) = spawn dir [ "plan"; "p.blade" ]
            expectCase "names: a plan decision inside an array instance names the declaration"
                (code = 0 && out.Contains "[cse v2] centered:" && not (internalName (out + err))) (out + err)
            // Lambdas: a let-bound one is its binding (`k`), an unbound one
            // `lambda in e`, the checker's operator lifting of `a - mymean(a)`
            // the operator's function `(-) in c`; the hoist evidence names
            // the callee as written (`mymean`, not its specialization).
            File.WriteAllText(Path.Combine(dir, "l.blade"),
                "function mymean(row: Float64^1) -> Float64 = reduce(row, (+)) / Float64(extents(row))\n"
                + "let a = [1.0, 2.0, 3.0, 4.0]\n"
                + "let c = a - mymean(a)\n"
                + "let e = method_for(a) <@> lambda(x) -> x - mymean(a) |> compute\n"
                + "let k = lambda(x: Float64) -> {\n"
                + "    let p = x * 2.0 + 1.0\n"
                + "    let q = x * 2.0 + 1.0\n"
                + "    p + q\n"
                + "}\n"
                + "let kk = k(1.0)\n")
            let (code, out, err) = spawn dir [ "plan"; "l.blade" ]
            expectCase "names: plan subjects name a lambda by its binding, its scope, or its operator"
                (code = 0
                 && out.Contains "[invariant-hoist v1] (-) in c: applied [`mymean(a)` does not read the element"
                 && out.Contains "[invariant-hoist v1] lambda in e: applied [`mymean(a)` does not read the element"
                 && out.Contains "[cse v2] k: applied"
                 && not (internalName (out + err))) (out + err)
            // Closed-form enumerable domains (docs/plans/structural/06, bench
            // G4): `plan` names the ENUMERATION FORM per consumer -- the affine
            // nest's bounds in the struct's own field names, and whether the
            // consumer streams it (a fold) or fills an array (a map).
            File.WriteAllText(Path.Combine(dir, "d.blade"),
                "let static n = 13\n"
                + "let static w = 3\n"
                + "static struct Band {\n"
                + "    i: Int<min=0, max=n-1>,\n"
                + "    j: Int<min=0, max=n-1>\n"
                + "} where abs(i - j) <= w\n"
                + "let s = reduce(method_for(range<Band>) <@> lambda(i, j) -> i * j, (+))\n"
                + "let R = method_for(range<Band>) <@> lambda(i, j) -> i + j |> compute\n")
            let (code, out, err) = spawn dir [ "plan"; "d.blade" ]
            expectCase "domains: plan names the closed-form affine nest and its consumer"
                (code = 0
                 && out.Contains "[domain-enum v1] s @ 7:1: applied [enumeration of Band: closed-form affine nest (2 level(s): i in [0, 12], j in [max(0, i - 3), min(12, i + 3)]), 79 cell(s); consumer: streamed fold"
                 && out.Contains "[domain-enum v1] R @ 8:1: applied [enumeration of Band: closed-form affine nest"
                 && out.Contains "consumer: map (the fill streams the nest") (out + err)
            // WHERE `Float64(x)` of an Int64 takes the split (CodeGenExprSupport):
            // only the closed-form domain's cell code -- the band kernel's callee
            // `phi` -- gets the vectorizable split; the scalar generator `gen`
            // keeps the one-instruction plain conversion (bench loose-ends item
            // 1: the split everywhere cost the input generators +27%). The two
            // spellings are the same double, so only the emission is pinned.
            File.WriteAllText(Path.Combine(dir, "c.blade"),
                "let static n = 13\n"
                + "let static w = 3\n"
                + "type Nx = Idx<n>\n"
                + "static struct Band {\n"
                + "    i: Int64<min=0, max=n-1>,\n"
                + "    j: Int64<min=0, max=n-1>\n"
                + "} where abs(i - j) <= w\n"
                + "function gen(k: Int64) -> Float64 = Float64((k * 1103515245 + 12345) % 2147483648) / 2147483648.0\n"
                + "function phi(i: Int64, j: Int64) -> Float64 = Float64(i - j) / Float64(w + 1)\n"
                + "let x = method_for(range<Nx>) <@> lambda(k) -> gen(Int64(k)) |> compute\n"
                + "let s = reduce(method_for(range<Band>) <@> lambda(i, j) -> phi(i, j) * x((i : Nx)), (+))\n")
            let (code, out, err) = spawn dir [ "emit"; "c.blade" ]
            // A function's body follows its definition line: a file-scope
            // `double gen(...) {`, or -- for one that reads a module binding,
            // like `phi` reading `w` -- a main-local `... phi = [&](...) -> double {`.
            let cppLine (fn: string) =
                let lines = out.Split('\n')
                lines
                |> Array.tryFindIndex (fun l ->
                    let t = l.Trim()
                    t.EndsWith "{" && (t.StartsWith $"double {fn}(" || t.Contains $" {fn} = [&]("))
                |> Option.map (fun i -> lines.[i + 1 .. min (lines.Length - 1) (i + 4)] |> String.concat "\n")
                |> Option.defaultValue ""
            let phiCpp, genCpp = cppLine "phi", cppLine "gen"
            expectCase "conversion: C++ splits Float64(Int64) only in domain cell code (phi), plain cast in the scalar generator"
                (code = 0
                 && phiCpp.Contains "blade_arith::i64_to_f64(" && not (phiCpp.Contains "static_cast<double>(")
                 && genCpp.Contains "static_cast<double>(" && not (genCpp.Contains "i64_to_f64")) (out + err)
            let (code, out, err) = spawnWith [ ("BLADE_LLVM", "1") ] dir [ "emit"; "c.blade" ]
            let llBody (fn: string) =
                out.Split("\ndefine ")
                |> Array.tryFind (fun d -> d.Split('\n').[0].Contains $"@bl_{fn}_")
                |> Option.map (fun d -> d.Split("\n}").[0])
                |> Option.defaultValue ""
            let phiLl, genLl = llBody "phi", llBody "gen"
            expectCase "conversion: LLVM splits Float64(Int64) only in domain cell code (phi), plain sitofp in the scalar generator"
                (code = 0
                 && phiLl.Contains "lshr i64" && phiLl.Contains "sitofp i32" && not (phiLl.Contains "sitofp i64")
                 && genLl.Contains "sitofp i64" && not (genLl.Contains "lshr i64")) (out + err)
        finally
            try Directory.Delete(dir, true) with _ -> ()
    // The editor payload and the REPL: the instance is the same declaration,
    // so it is no extra binding, reference, call or session name.
    (let src =
        "type L = Idx<3>\n"
        + "let A: Array<Float64 like L> = [1.0, 2.0, 3.0]\n"
        + "let v = [10.0, 20.0, 30.0, 40.0]\n"
        + "function walk(c, n: Int64) -> Float64 = if n == 0 then c(0) else c(n) + walk(c, n - 1)\n"
        + "let s = walk(A, 2)\n"
        + "let sv = walk(v, 3)\n"
        + "let sf = walk(lambda(i) -> Float64(i), 3)\n"
        + "function twice(c) = reduce(method_for(range<L>) <@> lambda(j) -> walk(c, 1) * Float64(j), (+))\n"
        + "let tw = twice(A)\n"
     let occurrences (needle: string) (hay: string) = hay.Split([| needle |], StringSplitOptions.None).Length - 1
     let (json, code) = Blade.Ide.ideCheckSource (Path.Combine(Path.GetTempPath(), "names_ide.blade")) src
     expectCase "names: ide check lists no array instance (no `__arrow` binding, reference or call)"
         (code = 0 && not (internalName json)) json
     expectCase "names: ide check lists the declaration once, as a binding and as a reference"
         (occurrences "{\"name\":\"walk\",\"kind\":\"function\"," json = 2
          && occurrences "{\"name\":\"twice\",\"kind\":\"function\"," json = 2) json
     // A call that targets an instance (`walk(A, 2)`, `twice(A)`) is a use of
     // the declaration: find-references and rename see every call site.
     let refEntry (name: string) =
         let start = json.IndexOf("{\"name\":\"" + name + "\",\"kind\":\"function\",\"def\"")
         if start < 0 then "" else json.Substring(start, json.IndexOf("]}", start) - start)
     let walkRefs, twiceRefs = refEntry "walk", refEntry "twice"
     expectCase "names: a call of an array instance is a reference to the declaration"
         (walkRefs.Contains "{\"line\":5,\"col\":9," && walkRefs.Contains "{\"line\":6,\"col\":10,"
          && walkRefs.Contains "{\"line\":7,\"col\":10," && twiceRefs.Contains "{\"line\":9,\"col\":10,")
         (walkRefs + " | " + twiceRefs)
     let session = Blade.ReplSession.ReplTypes.sessionInfo src
     expectCase "names: the REPL's session names are the declarations, no array instance"
         (session.ContainsKey "walk" && session.ContainsKey "twice"
          && not (session |> Map.exists (fun k _ -> internalName k)))
         (session |> Map.toList |> List.map fst |> String.concat ", "))
    // Lambdas in the editor payload and the REPL's names: what the program
    // bound, never a lifted callable or a desugaring's parameter.
    (let src =
        "let z: Int64 = 2\n"
        + "let f = lambda(x: Int64) -> x / z\n"
        + "function mk(i: Int64) = lambda(j: Int64) -> j / i\n"
        + "let g = mk(2)\n"
        + "let xs = [4, 2, 1]\n"
        + "let r = reduce(xs, lambda(a, b) -> a / b)\n"
        + "let s = reduce(xs, (/))\n"
        + "function add(a: Int64, b: Int64) -> Int64 = a + b\n"
        + "let h = add(4)\n"
        + "let q = add(_, 1)\n"
        + "let w = 10 / xs\n"
     let (json, code) = Blade.Ide.ideCheckSource (Path.Combine(Path.GetTempPath(), "names_ide_lambdas.blade")) src
     expectCase "names: ide check of lambdas lists no internal name (`__lambda`, `__wrap`, `__pa`)"
         (code = 0 && not (internalName json)) json
     let session = Blade.ReplSession.ReplTypes.sessionInfo src
     expectCase "names: the REPL's session names for lambdas are their bindings"
         (session.ContainsKey "f" && session.ContainsKey "g" && session.ContainsKey "h" && session.ContainsKey "q"
          && not (session |> Map.exists (fun k _ -> internalName k)))
         (session |> Map.toList |> List.map fst |> String.concat ", "))

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
        "function mymean(row) = reduce(row, (+)) / Float64(extents(row))\n\
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
        "function mymean(row) = reduce(row, (+)) / Float64(extents(row))\n\
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
        "function mymean(row) = reduce(row, (+)) / Float64(extents(row))\n\
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

        // 6. A store that is not there. The checker refuses at the LOAD SITE
        // (BL2008, line 2 col 13) rather than leaving it for lowering to
        // report spanless; the bindings that did check still arrive (the
        // editor keeps its hovers), the tier stays "full", and the loop
        // survives. Hermetic: the store is missing on purpose, and the
        // message doubles as proof that the loop resolved the provider path
        // against the REQUEST file's directory.
        let provPath = Path.Combine(tmpDir, "prov.blade")
        let provSource =
            "import csv as csv\nlet store = csv.load(\"no_such_store.csv\")\nlet a = 1\n"
        let (code, responses, _) =
            drive [ checkReq 15 "full" provPath provSource; pingReq 16; shutdownReq ]
        let name = "full tier: a missing store is refused at its load site, payload and loop intact"
        match responses with
        | [broken; pong] when code = 0 && broken.Contains "\"tier\":\"full\""
                              && broken.Contains "\"code\":\"BL2008\""
                              && broken.Contains "\"line\":2,\"col\":13"
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

        // ---- What `blade check` reports, the editor reports ---------------
        // Each case below is a diagnostic the CLI printed and this payload
        // dropped, mis-placed, or could not correlate.
        let count (needle: string) (hay: string) =
            let mutable n = 0
            let mutable i = hay.IndexOf(needle, StringComparison.Ordinal)
            while i >= 0 do
                n <- n + 1
                i <- hay.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)
            n
        let oneCheck (tier: string) (file: string) (source: string) =
            match drive [checkReq 1 tier (Path.Combine(tmpDir, file)) source; shutdownReq] with
            | (0, [r], _) -> r
            | (code, responses, _) -> sprintf "<exit %d, responses %A>" code responses
        let head (s: string) = if s.Length > 500 then s.Substring(0, 500) else s

        // BL4010 is two things: the pin suggestion (which rides its own
        // channel as well as the warning log) and the constrained-domain
        // advisories (which ride the warning log alone). Skipping the code
        // wholesale to avoid a duplicate dropped the second kind entirely.
        let domainSource =
            "static struct Prod { i: Int<min=0, max=6>, j: Int<min=0, max=6> } where (i * j) % 3 == 1\n"
            + "let R = method_for(range<Prod>) <@> lambda(i, j) -> i * 10 + j |> compute\n"
        let body = oneCheck "fast" "domain.blade" domainSource
        let name = "a constrained-domain advisory (BL4010 off the warning log) reaches the payload, at its span"
        if count "\"code\":\"BL4010\"" body = 1 && body.Contains "not closed-form"
           && body.Contains "\"line\":2,\"col\":20" then record name TH.Pass ""
        else record name TH.Fail (head body)
        let body = oneCheck "fast" "warn.blade" warnSource
        let name = "a pin suggestion is still reported exactly once"
        if count "\"code\":\"BL4010\"" body = 1 then record name TH.Pass ""
        else record name TH.Fail (sprintf "%d BL4010 in %s" (count "\"code\":\"BL4010\"" body) (head body))

        // A lowering refusal carries a span, and the full tier used to throw
        // it away: the CLI pointed at the expression, the editor at 1:1.
        let lowerRefusal =
            "function f(x: Array<Float like Idx<5>>) -> Float = {\n"
            + "    let m = mask(x, lambda(q) -> q > 2.0)\n"
            + "    let c = compound(x, m)\n"
            + "    reduce(c, (+))\n"
            + "}\n"
            + "let a = [1.0, 2.0, 3.0, 4.0, 5.0]\n"
            + "let s = f(a)\n"
        let body = oneCheck "full" "lower.blade" lowerRefusal
        // This is also the file that TYPECHECKS but will not lower: the fast
        // half of the payload survives intact beside the lowering diagnostic.
        let name = "tier=full reports a lowering refusal (BL6002) at ITS span, not 1:1, bindings intact"
        if body.Contains "\"tier\":\"full\"" && body.Contains "\"code\":\"BL6002\""
           && body.Contains "\"line\":3,\"col\":13"
           && body.Contains "\"name\":\"f\"" && body.Contains "\"name\":\"s\"" then
            record name TH.Pass ""
        else record name TH.Fail (head body)

        // "Void" is not a more concrete spelling of a static tuple's type; it
        // is what lowering leaves behind once the static evaluator has
        // consumed the binding.
        let body = oneCheck "full" "statictuple.blade" "let static dims = (2, 3)\nlet n = 4\n"
        let name = "tier=full never upgrades a binding's type to Void"
        if body.Contains "\"name\":\"dims\"" && not (body.Contains "\"concreteType\":\"Void\"") then
            record name TH.Pass ""
        else record name TH.Fail (head body)

        // A store that cannot be read is reported AT THE LOAD, by the tier the
        // editor runs on every keystroke. It used to be reported nowhere on
        // the fast tier, and as whatever first needed the store's types on
        // the full one.
        let body =
            oneCheck "fast" "nostore.blade"
                "import zarr as z\nlet s = z.load(\"definitely_absent_store\")\nlet n = 1\n"
        let name = "a store that cannot be read is BL2008 at the load site, on the fast tier"
        if body.Contains "\"code\":\"BL2008\"" && body.Contains "\"line\":2,\"col\":9"
           && body.Contains "not a Zarr store" then record name TH.Pass ""
        else record name TH.Fail (head body)

        // `"\ud83d"` is valid JSON and invalid UTF-16 (what a JavaScript
        // client writes for a buffer cut mid-emoji). The check must be
        // ANSWERED, under its own id: an id-less error cannot be correlated,
        // and the client's request then sits until its timeout kills the
        // process and every session with it.
        let surrogateReq =
            "{\"id\":41,\"cmd\":\"check\",\"tier\":\"fast\",\"file\":\"" + esc (Path.Combine(tmpDir, "sur.blade"))
            + "\",\"source\":\"let s = \\\"\\ud83d\\\"\\nlet t = 1\\n\"}"
        let (code, responses, _) = drive [surrogateReq; pingReq 42; shutdownReq]
        let name = "a lone surrogate in `source` is checked and answered under the request's id"
        match responses with
        | [r; pong] when code = 0 && r.Contains "\"id\":41" && r.Contains "\"name\":\"t\""
                         && not (r.Contains "\"error\"") && pong.Contains "\"id\":42" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code (responses |> List.map head))

        // ---- Session directories of dead processes -------------------------
        // A killed daemon cannot remove its sessions' temp directories; the
        // next one to start does. Driven against a private root with a fake
        // liveness oracle: nothing here kills a process or waits a day.
        let sweepRoot = Path.Combine(tmpDir, "sweep")
        let mkSession (suffix: string) (owner: int option) (ageHours: float) =
            let dir = Path.Combine(sweepRoot, Blade.ReplSession.sessionDirPrefix + suffix)
            Directory.CreateDirectory dir |> ignore
            File.WriteAllText(Path.Combine(dir, "session.blade"), "let x = 1\n")
            (match owner with
             | Some pid -> File.WriteAllText(Path.Combine(dir, Blade.ReplSession.ownerMarkerName), string pid)
             | None -> ())
            Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddHours(-ageHours))
            dir
        let liveOwned = mkSession "live0001" (Some 111) 48.0
        let deadOwned = mkSession "dead0001" (Some 222) 0.0
        let oldUnmarked = mkSession "old00001" None 48.0
        let newUnmarked = mkSession "new00001" None 1.0
        let unrelated = Path.Combine(sweepRoot, "not-a-session")
        Directory.CreateDirectory unrelated |> ignore
        let removed =
            Blade.ReplSession.sweepStaleSessionDirs sweepRoot (fun pid -> pid = 111) DateTime.UtcNow
        let name = "the session sweep removes a dead owner's directory and an old unmarked one, nothing else"
        if removed = 2
           && Directory.Exists liveOwned && not (Directory.Exists deadOwned)
           && not (Directory.Exists oldUnmarked) && Directory.Exists newUnmarked
           && Directory.Exists unrelated then record name TH.Pass ""
        else
            record name TH.Fail
                (sprintf "removed %d; live %b dead %b old %b new %b other %b" removed
                    (Directory.Exists liveOwned) (Directory.Exists deadOwned)
                    (Directory.Exists oldUnmarked) (Directory.Exists newUnmarked) (Directory.Exists unrelated))
        let name = "a new session directory names this process as its owner"
        let probe = Blade.ReplSession.ReplSession(entryDir)
        (try
            let marker = Path.Combine(probe.SessionDir, Blade.ReplSession.ownerMarkerName)
            if File.Exists marker && (File.ReadAllText marker).Trim() = string Environment.ProcessId then
                record name TH.Pass ""
            else record name TH.Fail $"no owner marker under {probe.SessionDir}"
         finally probe.Cleanup())
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
        // (`p` is unannotated, so generic: the signature is `(T) -> T`.)
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "function twice(p) = p * 2"; evalReq 2 "nb" "twice"; shutdownReq ]
        let name = "a bare function reference reports its signature and no value"
        match responses with
        | [decl; probe] when code = 0
                             && decl.Contains "\"kept\":true" && decl.Contains "\"bindings\":[]"
                             && probe.Contains "{\"name\":\"twice\",\"type\":\"(T) -> T\",\"value\":\"\"}" ->
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
        // An arithmetic fault is POSITIONED: the panic names the operator's
        // line (`--> line 1`, cell coordinates), and the diagnostic is rebuilt
        // from it at that line (column 1 -- the panic carries no column).
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let z = 1 / 0"; evalReq 2 "nb" "let ok = 6"
                    evalReq 3 "nb" "ok"; shutdownReq ]
        let name = "a runtime panic is not kept and names itself"
        match responses with
        | [panic; after; probe] when code = 0
                                     && panic.Contains "\"kept\":false" && panic.Contains "\"exitCode\":1"
                                     && panic.Contains "\"lane\":\"interp\"" && panic.Contains "\"bindings\":[]"
                                     && panic.Contains "\"stderr\":\"error[BL8013]: integer division by zero\\n  --> line 1\""
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
        // squiggling this one.
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

        // 14c. ...and the STDERR text says the same thing the diagnostic does.
        // The panic prints `  --> <session file>:<session line>`: a temp path
        // the user never wrote and a line counted across every cell in the
        // session. Both are internal; the client is shown the cell's line.
        let name = "a runtime panic's stderr names the cell line, not the session file"
        match responses with
        | [here; _; elsewhere] when here.Contains "--> line 4"
                                    && elsewhere.Contains "--> elsewhere in session"
                                    && not (here.Contains "session.blade")
                                    && not (here.Contains Blade.ReplSession.sessionDirPrefix)
                                    && not (elsewhere.Contains "session.blade") ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "responses: %A" responses)

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

        // 31. A MIXED cell with a MID-CELL expression. The cell runs as
        // `[a; it = a; b]` but commits `[a; b]`, so the next candidate never
        // lined up with the run's own snippet list: the prefix test matched
        // through `a`, called `b` dropped, and recomputed it -- measured at
        // 20-25 s under the plot cell after a training cell. The memo now
        // knows which of its snippets were transient and skips them, so `seed`,
        // `a` and `b` are all adopted and only the new cell's binding runs.
        let midCell = "let a = reduce(seed, (+))\na\nlet b = a * 2.0"
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let seed = [1.0, 2.0, 3.0]"
                    evalReq 2 "nb" midCell
                    evalReq 3 "nb" "let c = b + 1.0"
                    shutdownReq ]
        let adopted = Blade.Interp.Run.lastMemoAdopted
        let evaluated = Blade.Interp.Run.lastMemoEvaluated
        let name = "a mid-cell expression does not drop the cell's later bindings from the memo"
        match responses with
        | [_; _; last] when code = 0 && last.Contains "\"kept\":true" && adopted = 3 && evaluated = 1 ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, adopted %d, evaluated %d, responses: %A"
                                        code adopted evaluated responses)

        // ...and the adopted values are the ones the cell computed.
        let (code, responses, _) =
            drive [ evalReq 1 "nb" "let seed = [1.0, 2.0, 3.0]"
                    evalReq 2 "nb" midCell
                    evalReq 3 "nb" "let c = b + 1.0\nc"
                    shutdownReq ]
        let name = "bindings adopted past a mid-cell expression read back correctly"
        match responses with
        | [_; _; readback] when code = 0 && readback.Contains "\"value\":\"13.0\"" ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, responses: %A" code responses)

        // ...and the line it must not cross: a mid-cell expression that WRITES
        // through a `mut` parameter may have changed what a later binding
        // read, so its run is not reusable and the memo goes, exactly as for
        // any other dropped snippet that is not provably mutation-free.
        let pokeCell =
            "let mut buf = [0.0, 0.0, 0.0]\n"
            + "function fill(out: mut Array<Float like Idx<3>>) = {\n"
            + "    out(0) += 1.5\n}\nfill(buf)\nlet after = buf(0)"
        let (code, responses, _) =
            drive [ evalReq 1 "nb" pokeCell
                    evalReq 2 "nb" "let d = after + 1.0"
                    shutdownReq ]
        let adopted = Blade.Interp.Run.lastMemoAdopted
        let name = "a mid-cell expression that writes through mut still drops the memo"
        match responses with
        | [_; last] when code = 0 && last.Contains "\"kept\":true" && adopted = 0 ->
            record name TH.Pass ""
        | _ -> record name TH.Fail (sprintf "exit %d, adopted %d, responses: %A" code adopted responses)

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
        "function mymean(row) = reduce(row, (+)) / Float64(extents(row))\n\
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

    // 13. bindings[] types read as the program would SPELL them. A deduced
    // symmetric result over a NAMED index printed as the bare base name
    // (`Array<Float64 like AssetIdx>`, class and rank dropped: a compact
    // record's Tag names its COMPONENT space, not the class), a named dense
    // slot as `Idx<AssetIdx>`, an arity-polymorphic former as `Void` (its
    // binding is emitted inert), and a `Poly<T^1>` parameter lost its rank.
    let typesOf (source: string) : Map<string, string> =
        let (json, _) = Blade.Ide.ideCheckSource "types.blade" source
        use doc = System.Text.Json.JsonDocument.Parse json
        [ for b in doc.RootElement.GetProperty("bindings").EnumerateArray() do
            if b.GetProperty("kind").GetString() <> "param" then
                yield (b.GetProperty("name").GetString(),
                       b.GetProperty("type").GetString().Replace("\n", "").Replace("    ", "")) ]
        |> Map.ofList
    let comomentSrc =
        String.concat "
"
            [ "type AssetIdx = Idx<4>"
              "type DayIdx = Idx<6>"
              "let R: Array<Float64 like AssetIdx, DayIdx> = ["
              "    [1.0, -2.0, 3.0, 0.5, -1.5, 2.0], [0.5, 0.8, -0.2, 0.4, 0.1, 0.6],"
              "    [2.0, 1.0, 0.0, 1.0, 3.0, -1.0], [-1.0, 0.0, 1.5, 2.5, 0.5, 1.0]]"
              "function mymean(row: T^1) -> T^0 = reduce(row, (+)) / Float64(extents(row))"
              "function centered_prod(xs: Poly<T^1>) -> T^1 = {"
              "    match arity(xs) with"
              "    | 1 ->"
              "        let head :: tail = xs"
              "        head - mymean(head)"
              "    | _ ->"
              "        let head :: tail = xs"
              "        (head - mymean(head)) * centered_prod(tail)"
              "}"
              "function comoment(xs: Poly<T^1>) where comm(xs) -> T^0 = mymean(centered_prod(xs))"
              "let M = object_for(comoment)"
              "let C2 = M <@> (R, R) |> compute"
              "let C3 = M <@> (R, R, R) |> compute"
              "" ]
    let types = typesOf comomentSrc
    let expectType name binding (expected: string) =
        match Map.tryFind binding types with
        | Some t when t = expected -> record name TH.Pass ""
        | other -> record name TH.Fail (sprintf "%s: got %A, want %s" binding other expected)
    expectType "bindings[]: a deduced SymIdx over a named index keeps its class and rank"
        "C2" "Array<Float64 like SymIdx<2, AssetIdx>>"
    expectType "bindings[]: ...at every pack width" "C3" "Array<Float64 like SymIdx<3, AssetIdx>>"
    expectType "bindings[]: a named dense slot prints as its alias, not Idx<Alias>"
        "R" "Array<Float64 like AssetIdx, DayIdx>"
    expectType "bindings[]: an arity-polymorphic former is a loop object, not Void" "M" "ObjectLoop<_>"
    (match Map.tryFind "comoment" types with
     | Some t when t.StartsWith "(xs: Poly<T^1, " -> record "bindings[]: a Poly<T^1> parameter keeps its rank" TH.Pass ""
     | other -> record "bindings[]: a Poly<T^1> parameter keeps its rank" TH.Fail (sprintf "got %A" other))
    // ...and the spelling the display shows is one the checker accepts.
    let (json, code) =
        Blade.Ide.ideCheckSource "types.blade"
            (comomentSrc + "let D2: Array<Float64 like SymIdx<2, AssetIdx>> = C2\n")
    let name = "the displayed SymIdx<2, AssetIdx> spelling is accepted as an ascription"
    if code = 0 && json.Contains "\"diagnostics\":[]" then record name TH.Pass ""
    else record name TH.Fail json
    // Diagnostics share the printer: a mismatch names both index types.
    let (json, _) =
        Blade.Ide.ideCheckSource "types.blade"
            (comomentSrc + "let D3: Array<Float64 like SymIdx<2, DayIdx>> = C2\n")
    let name = "a type mismatch spells a named compact class, not its numeric extent"
    if json.Contains "expected Array<Float64 like SymIdx<2, DayIdx>>, got Array<Float64 like SymIdx<2, AssetIdx>>" then
        record name TH.Pass ""
    else record name TH.Fail json

    // 14. The printer's three readings of a `names` entry (IRPrint.ppIndexTypeIn):
    // extent text fills the extent slot, the record's own (component) name
    // fills it on a compact class, and any other name on a compact class is a
    // WHOLE-class alias (indexNameMap's `type MySym = SymIdx<2, 4>`).
    let symRec : Blade.IR.IRIndexType =
        { Id = 9001; Rank = 2; Extent = Blade.IR.IRLit (Blade.IR.IRLitInt 4L)
          Symmetry = Blade.Types.SymSymmetric; Tag = Some "AssetIdx"; IxKind = Blade.Types.IxKPlain
          Kind = Blade.Types.SDimension; Dependencies = [] }
    let pin name (actual: string) (expected: string) =
        if actual = expected then record name TH.Pass "" else record name TH.Fail $"got {actual}, want {expected}"
    pin "ppIndexType: a compact record's tag fills its extent slot"
        (Blade.IRPrint.ppIndexType symRec) "SymIdx<2, AssetIdx>"
    pin "ppIndexTypeIn: the record's own name fills the extent slot"
        (Blade.IRPrint.ppIndexTypeIn (Map.ofList [9001, "AssetIdx"]) symRec) "SymIdx<2, AssetIdx>"
    pin "ppIndexTypeIn: a whole-class alias prints bare"
        (Blade.IRPrint.ppIndexTypeIn (Map.ofList [9001, "MySym"]) symRec) "MySym"
    pin "ppIndexTypeIn: extent text stays in the extent slot"
        (Blade.IRPrint.ppIndexTypeIn (Map.ofList [9001, "_"]) { symRec with Tag = None }) "SymIdx<2, _>"
    pin "ppIndexTypeIn: extent text on a dense slot keeps the Idx<> form"
        (Blade.IRPrint.ppIndexTypeIn (Map.ofList [9001, "4"])
            { symRec with Rank = 1; Symmetry = Blade.Types.SymNone; Tag = None }) "Idx<4>"

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

/// Whether `cat` names a corpus directory `test <lane> <cat>` can run: any
/// single-file category, plus the lane's own extra spellings (`opt-diff`
/// runs the multi-file corpus too; `interp` walks EVERY multi-file directory,
/// as its no-category form does; `llvm` takes its reserved words -- `all`,
/// `goldens`, `facts`, ... -- from `LlvmTests.llvmCategoryWords`).
let private corpusCategoryExists (lane: string) (cat: string) : bool =
    let extras () =
        match lane with
        | "opt-diff" | "optdiff" -> [ "multifile" ]
        | "interp" -> (try Blade.Tests.Corpus.multiFileCategories () with _ -> [])
        | _ -> []
    List.contains cat (extras ())
    || (lane = "llvm" && Blade.Tests.LlvmTests.isLlvmCategoryWord cat)
    || (try List.contains cat (Blade.Tests.Corpus.singleFileCategories ()) with _ -> false)

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
    // ...and its expected-unsupported set names real tests (a renamed test
    // would otherwise fail the differential as an unexpected skip, but a
    // DELETED one would leave a dead entry behind).
    let allTestNames =
        single |> List.collect (fun d -> Blade.Tests.Corpus.categoryTests d |> List.map fst) |> Set.ofList
    let deadUnsupported =
        Blade.Tests.InterpDiff.interpExpectedUnsupported |> List.map fst
        |> List.filter (fun n -> not (allTestNames.Contains n))
    record "every expected interp-unsupported entry names a corpus test" deadUnsupported.IsEmpty
        (listed deadUnsupported)
    // `test interp <dir>` accepts every directory it walks, multi-file ones
    // included (it used to refuse `multifile` as an unknown category).
    let interpRefused = (single @ multi) |> List.filter (fun d -> not (corpusCategoryExists "interp" d))
    record "every corpus directory is accepted by `test interp <dir>`" interpRefused.IsEmpty (listed interpRefused)
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

/// Dispatch the `test` subcommand. `rest` is everything after "test".
///
/// Runs in `withSuiteEnvironment`: every `suiteClearedKnobs` variable UNSET and
/// BLADE_BLAS pinned off, the caller's values restored afterwards; says in one
/// line which ones it found set. `--print`
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
    withSuiteEnvironment false (fun () -> dispatchTestClean rest)

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
        let tileFailed = (Blade.Tests.LinAlgTests.runSyrkTileTests ()).Failed
                         + (Blade.Tests.LinAlgTests.runRowContractionTileTests ()).Failed
        let tierFailed = (Blade.Tests.LinAlgTests.runBlasTierTests ()).Failed
        if emitFailed + probeFailed + tileFailed + tierFailed = 0 then 0 else 1
    | [ "row-tile" ] | [ "rowtile" ] | [ "antisym-tile" ] | [ "simplex-tile" ] ->
        // The antisym-pair and rank-3 simplex tiles alone: emission routes,
        // raw-bit vs the serial forms (contract=off) and the one-fma chains
        // (contract=fast), vfmadd census.
        let failed = (Blade.Tests.LinAlgTests.runRowContractionTileTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "syrk-tile" ] | [ "syrktile" ] ->
        // The packed-triangle SYRK tile alone: raw-bit vs the serial prodsum
        // (contract=off) and the one-fma chain (contract=fast), vfmadd census.
        let failed = (Blade.Tests.LinAlgTests.runSyrkTileTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "fold-lanes" ] | [ "foldlanes" ] ->
        // The explicit K-lane vector fold forms (review M9/M14): raw-bit vs
        // the scalar model of the lane tree in both contraction modes, the
        // Path B partition threaded vs serial-within-chunk, vfmadd census.
        let failed = (Blade.Tests.LinAlgTests.runFoldLanesTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "doctor" ] ->
        // Structural pins for the doctor's rows and JSON shape; the probes
        // run for real but machine-dependent statuses are not asserted.
        let failed = (Blade.Tests.DoctorTests.runDoctorTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "closure-capture" ] ->
        // Which capture each closure site emits: a closure VALUE copies
        // (`[=] ... mutable`), a kernel wrapper and a closure over a
        // reassigned binding keep `[&]`. Pure codegen string checks.
        let failed = (Blade.Tests.Functions.runClosureCaptureEmissionTests ()).Failed
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
    | [ "instance-guards" ] | [ "instanceguards" ] ->
        // Which caret-parameter reads carry a BL8006 guard: the array
        // instance at a named axis does, a proven position and an anonymous
        // caller do not. Pure lowering + codegen, no toolchain.
        let failed = (Blade.Tests.IndexTypes.runInstanceGuardEmissionTests ()).Failed
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
    | [ "cell-epilogue" ] | [ "cellepilogue" ] ->
        // Round-2 cell epilogues (B1 / B2r): the licensed and threaded forms
        // by raw bits, NEW vs BLADE_CELL_EPILOGUE=0, both contraction modes,
        // fixed team sizes; the LLVM lane's parity under the identity regime.
        let failed = (Blade.Tests.CellEpilogueTests.runCellEpilogueTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "rec-step" ] | [ "recstep" ] ->
        // Wave 3 (E-G5): the threaded `let rec` step -- the threaded build
        // vs the threads-off build by raw bits (trajectory, scalar, observed
        // team) at fixed team sizes, licensed and repro.
        let failed = (Blade.Tests.RecStepTests.runRecStepTests ()).Failed
        if failed = 0 then 0 else 1
    | [ "orbit-scatter" ] | [ "orbitscatter" ] ->
        // The ordered orbit scatter executed in both lanes against walk-forced
        // twins under BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off: finite-value
        // identity, signed zero, NaN-equivalence, at every scatter_g and with
        // the knob off.
        let failed = (Blade.Tests.OrbitScatterTests.runOrbitScatterTests ()).Failed
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
        // The toolchain-free buffer/emission checks ride along (they also run
        // in the full suite), so a targeted `test cuda` reaches them.
        let failed =
            (Blade.Tests.CudaTests.runBufferTypeTests ()).Failed
            + (Blade.Tests.CudaTests.runCudaTests ()).Failed
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
