// Sole-Luna execution after root source review; never run by the author.
// All API/policy/fault bridge code lives in the reviewed compiled test module.
open System
open System.IO
open System.Security.Cryptography
open Blade.IR

let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length <> 1 then
    failwith "Usage: dotnet fsi --reference:<new reviewed Blade.dll> --exec run-focused.fsx <new-private-ASCII-case-directory>"
let directory = Path.GetFullPath args.[0]
let loadedAssembly = typeof<IRProgram>.Assembly
let assemblyPath = Path.GetFullPath loadedAssembly.Location
let assemblyDirectory = Path.GetDirectoryName assemblyPath
let hash (path: string) : string = SHA256.HashData(File.ReadAllBytes path) |> Convert.ToHexString
let assemblyHash = hash assemblyPath
let baseDirectoryKey = "APP_CONTEXT_BASE_DIRECTORY"
let previousBaseData = AppContext.GetData baseDirectoryKey
let comparablePath (path: string) = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)
printfn "ASSEMBLY %s SHA256 %s" assemblyPath assemblyHash
printfn "ASSEMBLY_IDENTITY %s MVID %O" loadedAssembly.FullName loadedAssembly.ManifestModule.ModuleVersionId
printfn "BASE_DIRECTORY_BEFORE %s" AppContext.BaseDirectory
printfn "PRIVATE_CASE %s" directory
// .NET10 AppContext.BaseDirectory reads this public host-data key before
// its cached default. FSI otherwise points to the SDK/FSharp directory.
// https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/AppContext.cs#L23-L27
let code =
    try
        AppContext.SetData(baseDirectoryKey, assemblyDirectory + string Path.DirectorySeparatorChar)
        let observedBase = AppContext.BaseDirectory
        if not (String.Equals(comparablePath observedBase, comparablePath assemblyDirectory, StringComparison.OrdinalIgnoreCase)) then
            failwith "FSI host override did not resolve runtime headers beside the loaded Blade.dll"
        printfn "BASE_DIRECTORY_OBSERVED %s" observedBase
        if not (List.contains "blade_private_spool.hpp" Blade.CodeGen.runtimeHeaderNames) then
            failwith "Loaded Blade.dll predates private-spool header registration; a fresh source-matching build is required"
        let requiredHeaders = (Blade.CodeGen.runtimeHeaderNames @ ["blade_private_spool.hpp"]) |> List.distinct
        for name in requiredHeaders do
            let path = Path.Combine(observedBase,"cpp",name)
            if not (File.Exists path) then failwith ("Missing deployed runtime header: " + path)
            let headerHash = hash path
            printfn "HEADER %s SHA256 %s REGISTERED=%b" path headerHash (List.contains name Blade.CodeGen.runtimeHeaderNames)
            match name with
            | "blade_private_spool.hpp" when headerHash <> "26EA91E31EF33259C95EECE1E5391CBBDA13BD8EE3BA9694FA28F7B1E6EA0EAF" ->
                failwith "Deployed private-spool header differs from the reviewed D08 C++17 compatibility revision"
            | "blade_run_record.hpp" when headerHash <> "74DD8634B62156E13120300479CBBBC2E616A7D44CED3F35B2E213B78D5A6EDB" ->
                failwith "Deployed run-record header differs from the reviewed SDK-declaration compatibility revision"
            | "blade_runtime.hpp" when headerHash <> "DAB931642C6936222AB91EC888DBDDD52A553EC1D327BE1EB6E275B678C079DE" ->
                failwith "Deployed runtime header differs from accepted frozen D06 bytes"
            | _ -> ()
        if not (obj.ReferenceEquals(loadedAssembly,typeof<IRProgram>.Assembly)) || hash assemblyPath <> assemblyHash then
            failwith "Loaded Blade assembly identity changed during host setup"
        Console.Out.Flush ()
        Blade.Runtime.runOnLargeStack (fun () -> Blade.Tests.PrivateSpoolTests.runPrivateSpoolTests directory)
    finally
        AppContext.SetData(baseDirectoryKey, previousBaseData)
exit code
