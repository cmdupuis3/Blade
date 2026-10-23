// Sole-Luna only, after root review and completion of the initial full suite.
open System
open System.IO
open System.Security.Cryptography
open Blade.IR

let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length <> 2 then failwith "Usage: run-targeted.fsx <checkout> <new-private-output-directory>"
let checkout = Path.GetFullPath args.[0]
let output = Path.GetFullPath args.[1]
if Directory.Exists output || File.Exists output then failwith "Preserve prior evidence: output must be new"
if output.Length > 160 then failwith "Use a short private output directory"
let assembly = typeof<IRProgram>.Assembly
let assemblyRoot = Path.GetDirectoryName assembly.Location
let previousBase = AppContext.GetData "APP_CONTEXT_BASE_DIRECTORY"
let previousCwd = Directory.GetCurrentDirectory()
let hash (path: string) = SHA256.HashData(File.ReadAllBytes path) |> Convert.ToHexString
printfn "ASSEMBLY %s SHA256 %s MVID %O" assembly.Location (hash assembly.Location) assembly.ManifestModule.ModuleVersionId
let code =
    try
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY",assemblyRoot + string Path.DirectorySeparatorChar)
        Directory.SetCurrentDirectory checkout
        if Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) <> assemblyRoot then
            failwith "FSI header root does not match reviewed DLL"
        for name in ["blade_run_record.hpp"; "blade_private_spool.hpp"] do
            let header = Path.Combine(assemblyRoot,"cpp",name)
            printfn "HEADER %s SHA256 %s" header (hash header)
        Console.Out.Flush()
        Blade.Runtime.runOnLargeStack (fun () ->
            let cases =
                [ ("Late Broadcast Over An Empty Array", "functions/194_late_broadcast_over_empty_array.blade");
                  ("Rand Poisson Large Lambda", "rand/018_poisson_large_lambda.blade");
                  ("Recursive Array Nested DP", "recursive-arrays/008_nested_dp.blade") ]
            let tests = cases |> List.map (fun (name, relative) ->
                name, File.ReadAllText(Path.Combine(checkout,"tests","corpus",relative)))
            let result = Blade.Tests.Runner.runTestCategoryFull "Final SDK macro repair: three exact corpus cases" tests output
            if result.Passed <> 3 || result.Failed <> 0 || result.Skipped <> 0 then 1
            else
                let diagnostics = Blade.Tests.DiagnosticsCore.runDiagnosticsCoreTests()
                if diagnostics.Failed = 0 && diagnostics.Skipped = 0 then 0 else 1)
    finally
        Directory.SetCurrentDirectory previousCwd
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY",previousBase)
exit code
