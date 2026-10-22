// Sole-Luna after root review. Invoke only the reviewed compiled D09-I1/I2 gate.
open System
open System.IO
open System.Security.Cryptography
open Blade.IR

let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length<>1 then failwith "Usage: dotnet fsi --reference:<fresh Blade.dll> --exec run-focused.fsx <new-private-short-ASCII-directory>"
let directory = Path.GetFullPath args.[0]
let assembly = typeof<IRProgram>.Assembly
let assemblyPath = Path.GetFullPath assembly.Location
let assemblyDirectory = Path.GetDirectoryName assemblyPath
let hash (path: string) = SHA256.HashData(File.ReadAllBytes path) |> Convert.ToHexString
let previousBaseData = AppContext.GetData "APP_CONTEXT_BASE_DIRECTORY"
printfn "ASSEMBLY %s SHA256 %s MVID %O" assemblyPath (hash assemblyPath) assembly.ManifestModule.ModuleVersionId
printfn "PRIVATE_CASE %s length=%d" directory directory.Length
let code =
    try
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY",assemblyDirectory + string Path.DirectorySeparatorChar)
        let observed = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)
        if not (String.Equals(observed,assemblyDirectory,StringComparison.OrdinalIgnoreCase)) then
            failwith "FSI host override did not resolve beside the reviewed fresh Blade.dll"
        printfn "BASE_DIRECTORY %s" AppContext.BaseDirectory
        for name in Blade.CodeGen.runtimeHeaderNames do
            let path = Path.Combine(observed,"cpp",name)
            if not (File.Exists path) then failwith ("Missing deployed runtime header: " + path)
            printfn "HEADER %s SHA256 %s" path (hash path)
        Console.Out.Flush ()
        Blade.Runtime.runOnLargeStack (fun () -> Blade.Tests.ZarrTests.runRank2StreamedValueBoundaryTests directory)
    finally
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY",previousBaseData)
exit code
