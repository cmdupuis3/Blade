// The precompiled prelude (docs/plans/plan-test-tiers.md, P6; Build.fs,
// BLADE_PCH): a generated program's leading block of #include lines is
// compiled once per flag set into a GCC precompiled header and handed to later
// compiles with `-include`.
//
// What this block pins, against a PRIVATE store (BLADE_PCH = a temp directory)
// and with the executable cache off so every compile is a real one:
//   - a block is precompiled at its SECOND sighting, not its first;
//   - later compiles use it, and the programs print what they print without it;
//   - a compile that fails WITH the prelude is re-run without it and succeeds
//     (the precompiled file is replaced by garbage and its header removed);
//   - after three such failures the prelude is dropped and its file deleted;
//   - BLADE_PCH=0 never touches the store.
// The counts come from Build.CompileStats.pchSnapshot, as differences.
// `blade test pch`; part of the default suite. Needs g++ (Skipped without it).
module Blade.Tests.PchTests

open System
open System.IO
open System.Diagnostics
open Blade
open Blade.Lowering
open Blade.Tests.TestHarness

let private pinEnv (name: string) (value: string) =
    let prior = Environment.GetEnvironmentVariable(name)
    Environment.SetEnvironmentVariable(name, value)
    { new IDisposable with
        member _.Dispose() = Environment.SetEnvironmentVariable(name, prior) }

let private runProc (exe: string) (timeoutMs: int) =
    let psi = ProcessStartInfo(exe, "")
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    use proc = Process.Start(psi)
    let o = Blade.Runtime.readToEndOffPool proc.StandardOutput
    let e = Blade.Runtime.readToEndOffPool proc.StandardError
    if proc.WaitForExit(timeoutMs) then Ok (proc.ExitCode, o.Result, e.Result)
    else
        (try proc.Kill(true) with _ -> ())
        Error "timed out"

let runPchTests () : BlockResult =
    let blockName = "Precompiled Prelude"
    printHeader "Precompiled prelude (BLADE_PCH): built at the second sighting, never changes a verdict"
    let caps = Build.capabilities.Value
    if not caps.HasGpp then
        printfn "Skipped: g++ not found (cannot compile the programs)."
        { Block = blockName; Passed = 0; Failed = 0; Skipped = 1; FailedNames = [] }
    elif Platforms.os <> Platforms.Windows then
        printfn "Skipped: the precompiled prelude is built on Windows only."
        { Block = blockName; Passed = 0; Failed = 0; Skipped = 1; FailedNames = [] }
    else
    let work = Path.Combine(Path.GetTempPath(), "blade-pch-" + Guid.NewGuid().ToString("N"))
    let store = Path.Combine(work, "store")
    Directory.CreateDirectory(work) |> ignore
    CodeGen.deployRuntimeHeaders work
    let mutable passed = 0
    let mutable failed = 0
    let mutable names = []
    let fail (n: string) (detail: string) =
        failed <- failed + 1
        names <- names @ [ n ]
        resultLine Fail n detail
    let pass (n: string) (detail: string) =
        passed <- passed + 1
        resultLine Pass n detail
    use _exeCache = pinEnv "BLADE_EXE_CACHE" "0"
    use _blas = pinEnv "BLADE_BLAS" "0"
    use _llvm = pinEnv "BLADE_LLVM" null
    use _mem = pinEnv "BLADE_MEMCHECK" null
    use _pch = pinEnv "BLADE_PCH" store

    let mutable stemN = 0
    /// Emit program number k (prints `v = 3 * k`), compile it, run it:
    /// (the run's stdout, the change in (uses, builds, retries)).
    let build (k: int) : Result<string * (int * int * int), string> =
        try
            match lower $"let v = {k} + {2 * k}\n" with
            | Error e -> Error ("lower: " + e)
            | Ok ir ->
                stemN <- stemN + 1
                let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir $"pch_{stemN}"
                CodeGen.takeUnhandledIRNodeDiagnostics () |> ignore
                let file = Path.Combine(work, $"pch_{stemN}.cpp")
                File.WriteAllText(file, cpp)
                let (u0, b0, _, r0) = Build.CompileStats.pchSnapshot ()
                match Build.compileCppSource (Some cpp) file work with
                | Error e -> Error ("compile: " + (if e.Length > 400 then e.Substring(0, 400) else e))
                | Ok exe ->
                    let (u1, b1, _, r1) = Build.CompileStats.pchSnapshot ()
                    match runProc exe 120000 with
                    | Ok (0, out, _) -> Ok (out.Replace("\r\n", "\n"), (u1 - u0, b1 - b0, r1 - r0))
                    | Ok (code, out, err) -> Error ($"exit {code}: {out} {err}")
                    | Error e -> Error e
        with ex -> Error ("raised: " + ex.Message)
    let gchFiles () =
        if Directory.Exists store then Directory.GetFiles(store, "blade_prelude.hpp.gch", SearchOption.AllDirectories)
        else [||]
    /// One compile, judged: the program printed `v = 3k` and the counters moved
    /// by exactly `want`; `also` is a further condition on the store.
    let expect (name: string) (k: int) (want: int * int * int) (also: unit -> string option) =
        match build k with
        | Error e -> fail name e
        | Ok (out, got) ->
            if not (out.Contains $"v = {3 * k}") then fail name ($"printed: {out.Trim()}")
            elif got <> want then fail name ($"(uses, builds, retries) moved by {got}, expected {want}")
            else
                match also () with
                | Some problem -> fail name problem
                | None -> pass name ($"(uses, builds, retries) +{want}")

    expect "first sighting: nothing is precompiled, the block is only noted" 1 (0, 0, 0) (fun () ->
        if (gchFiles ()).Length <> 0 then Some "a precompiled file exists after one compile"
        elif not (Directory.Exists store) || (Directory.GetDirectories store).Length <> 1 then
            Some "the first sighting left no key directory"
        else None)
    expect "second sighting: the prelude is built and this compile uses it" 2 (1, 1, 0) (fun () ->
        if (gchFiles ()).Length = 1 then None else Some ($"{(gchFiles ()).Length} precompiled files, expected 1"))
    expect "third compile: reused, not rebuilt" 3 (1, 0, 0) (fun () ->
        if (gchFiles ()).Length = 1 then None else Some ($"{(gchFiles ()).Length} precompiled files, expected 1"))

    // BLADE_PCH=0: the same program compiles and prints the same, and neither
    // the counters nor the store move.
    let offCase () =
        let name = "BLADE_PCH=0: the plain compile, the store untouched"
        let stamps () = gchFiles () |> Array.map (fun f -> File.GetLastWriteTimeUtc f)
        let before = stamps ()
        use _off = pinEnv "BLADE_PCH" "0"
        match build 3 with
        | Error e -> fail name e
        | Ok (out, got) ->
            if not (out.Contains "v = 9") then fail name ($"printed: {out.Trim()}")
            elif got <> (0, 0, 0) then fail name ($"the counters moved by {got}")
            elif stamps () <> before then fail name "the store changed"
            else pass name "printed v = 9; counters and store unchanged"
    offCase ()

    // Break the prelude: garbage for the precompiled file, and no header behind
    // it. g++ cannot use either, so the compile WITH it fails; the re-run
    // without it is the verdict.
    match gchFiles () with
    | [| gch |] ->
        File.WriteAllBytes(gch, Array.zeroCreate 4096)
        File.Delete (gch.Substring(0, gch.Length - 4))
    | _ -> ()
    let stillThere () =
        if (gchFiles ()).Length = 1 then None else Some "the broken file was removed too early"
    expect "a broken prelude: the compile is re-run without it and succeeds (1 of 3)" 4 (0, 0, 1) stillThere
    expect "a broken prelude: again (2 of 3)" 5 (0, 0, 1) stillThere
    expect "a broken prelude: the third failure drops it and deletes its file" 6 (0, 0, 1) (fun () ->
        if (gchFiles ()).Length = 0 then None else Some "the broken file is still in the store")
    expect "a dropped prelude is not offered again in this process" 7 (0, 0, 0) (fun () ->
        if (gchFiles ()).Length = 0 then None else Some "the prelude was rebuilt in the process that dropped it")

    (try Directory.Delete(work, true) with _ -> ())
    printFooter blockName [ $"{passed} passed"; $"{failed} failed" ]
    { Block = blockName; Passed = passed; Failed = failed; Skipped = 0; FailedNames = names }
