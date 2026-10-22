// Sole-Luna after root review. Bounded fixture creation + lower/validate/emit.
// Never compile/execute generated C++, run tests, or change production state.
open System
open System.IO
open System.Collections
open System.Security.Cryptography
open Microsoft.FSharp.Reflection
open Blade
open Blade.IR
open Blade.IRStorage
open Blade.CodeGenState
open Blade.ZarrProvider

let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length <> 2 then
    failwith "Usage: dotnet fsi --reference:<frozen Blade.dll> --exec probe.fsx <D09-census-inputs> <new-private-output>"
let bundle = Path.GetFullPath args.[0]
let output = Path.GetFullPath args.[1]
if Directory.Exists output || File.Exists output then failwith "D09 output must be new; never overwrite/delete retained evidence"
if output.Length > 160 || (output |> Seq.exists (fun c -> c < ' ' || c > '~')) then
    failwith "Use a short private ASCII output path (at most160 characters)"
let hash (path: string) : string = SHA256.HashData(File.ReadAllBytes path) |> Convert.ToHexString
let assembly = typeof<IRProgram>.Assembly
let assemblyPath = Path.GetFullPath assembly.Location
let expectedDll = "4F60DD09CCEA1970B7DCAF6201C477BE73DF71268DBCE31E7DC8EBDD28B4F54D"
if hash assemblyPath <> expectedDll then failwith "D09 requires the frozen D08-reviewed DLL; do not rebuild or substitute another assembly"
let cases : (string * string) list =
    [ ("01-materialized.blade", "out");
      ("02-terminal.blade", "term");
      ("03-gather.blade", "out");
      ("04-array-escape.blade", "out") ]
let sources =
    cases |> List.map (fun (file, demand) ->
        let path = Path.Combine(bundle,file)
        let text = File.ReadAllText path
        if text.Length > 4096 || text.Split([|"__D09_FIXTURE__"|],StringSplitOptions.None).Length <> 2 then
            failwith ("Expected one bounded fixture placeholder: " + file)
        file, demand, text)
Directory.CreateDirectory output |> ignore
let store = Path.Combine(output,"fixture.zarr")
let keys =
    ([ "BLADE_FP_REPRO"; "BLADE_FP_REASSOC"; "BLADE_FP_CONTRACT";
       "BLADE_BLAS"; "BLADE_LLVM"; "BLADE_PRINT"; "BLADE_TILE_CACHE"; "BLADE_OMP_THREADS" ]
     @ Blade.Optimize.optimizerGates)
    |> List.distinct
let saved = keys |> List.map (fun key -> key, Environment.GetEnvironmentVariable key)
let savedTiming = splitTimingModeEnabled ()
let savedTarget = splitTimingOnlyBinding ()
let savedBaseData = AppContext.GetData "APP_CONTEXT_BASE_DIRECTORY"

// Executed C05-snapshot-diagnostic walker: no collection/depth truncation.
// SrcLoc is a schema class whose complete payload is its Span. Unknown
// payload classes deliberately fail the artifact after primary reasons save.
let dumpFull (path: string) (value: obj) =
    use writer = new StreamWriter(path, false, System.Text.UTF8Encoding(false))
    let rec dump depth (v: obj) =
        let line text = writer.WriteLine(String.replicate depth "  " + text)
        if isNull v then line "null (including None/empty-list union representation)"
        else
            let t = v.GetType()
            if t = typeof<string> || t.IsPrimitive || t.IsEnum || t = typeof<decimal> then
                line (sprintf "%s = %A" t.FullName v)
            elif v :? SrcLoc then
                line t.FullName
                line "Span"
                dump (depth + 1) (box ((v :?> SrcLoc).Span))
            elif FSharpType.IsRecord t then
                line t.FullName
                Array.zip (FSharpType.GetRecordFields t) (FSharpValue.GetRecordFields v)
                |> Array.iter (fun (field, child) -> line (field.Name + ": " + field.PropertyType.ToString()); dump (depth + 1) child)
            elif FSharpType.IsTuple t then
                line t.FullName
                FSharpValue.GetTupleFields v |> Array.iter (dump (depth + 1))
            elif FSharpType.IsUnion t then
                let case, children = FSharpValue.GetUnionFields(v, t)
                line (t.FullName + "." + case.Name)
                Array.zip (case.GetFields()) children
                |> Array.iter (fun (field, child) -> line (field.Name + ": " + field.PropertyType.ToString()); dump (depth + 1) child)
            elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<System.Collections.Generic.KeyValuePair<_, _>> then
                line t.FullName
                dump (depth + 1) (t.GetProperty("Key").GetValue v)
                dump (depth + 1) (t.GetProperty("Value").GetValue v)
            elif v :? IEnumerable then
                line t.FullName
                for child in (v :?> IEnumerable) do dump (depth + 1) child
            else failwithf "unhandled exhaustive D09 dump type %s" t.FullName
    dump 0 value

let probe () =
    use log = new StreamWriter(Path.Combine(output,"probe.txt"), false)
    log.AutoFlush <- true
    let line (text: string) =
        log.WriteLine text
        printfn "%s" text
        Console.Out.Flush ()
    let mutable artifactError = false
    let section (name: string) (body: unit -> unit) =
        line ("BEGIN " + name)
        try body () with error ->
            artifactError <- true
            line ("ARTIFACT_ERROR " + name + " " + error.ToString())
        line ("END " + name)
    let diagnostic (stage: string) (d: Blade.Diagnostics.Diagnostic) =
        line (sprintf "DIAGNOSTIC %s code=%s span=%d:%d-%d:%d message=%s" stage d.Code
            d.Span.StartLine d.Span.StartCol d.Span.EndLine d.Span.EndCol d.Message)
    let decisions (stage: string) =
        for d in Blade.Effects.Decisions.drain () do
            line ("DECISION " + stage + " " + Blade.Effects.Decisions.render d)
    let save (name: string) (text: string) =
        let path = Path.Combine(output,name)
        File.WriteAllText(path,text)
        line ("SAVED " + path + " SHA256 " + hash path)
    let saveFull (name: string) (value: obj) =
        let path = Path.Combine(output,name)
        dumpFull path value
        line ("SAVED_EXHAUSTIVE " + path + " SHA256 " + hash path)
    let projections (label: string) (modul: IRModule) =
        let previous = setCallablesContext (Blade.IRPrint.buildCallablesTableForModule modul)
        try
            let names = modul.Bindings |> List.map (fun b -> b.Id,b.Name) |> Map.ofList
            for binding in modul.Bindings do
                let mutable index = 0
                iterIRExpr (function
                    | IRApplyCombinator info ->
                        let site = sprintf "%s.%s.apply%d" label binding.Name index
                        index <- index + 1
                        section (site + ".initial-cg-projection") (fun () ->
                            let arrayNames =
                                info.Arrays |> List.mapi (fun position -> function
                                    | IRVar(id,_) -> Map.tryFind id names |> Option.defaultValue (sprintf "__v%d" id)
                                    | _ -> sprintf "__diagnostic_operand%d" position)
                            line "PROJECTION_ONLY initial buildLoopNestCodeGen + routeKernelBodyThroughCall; reconstructed objects, not final emitted certificate/owner/slab/fold state"
                            let cg =
                                Blade.IRStorage.buildLoopNestCodeGen info arrayNames ("__d09_" + binding.Name) (IRBuilder())
                                |> Blade.CodeGenExprSupport.routeKernelBodyThroughCall info
                            saveFull (site + ".initial-cg.txt") (box cg))
                    | _ -> ()) binding.Value
        finally restoreAnalysisContext previous
    try
        for key in Blade.Optimize.optimizerGates do Environment.SetEnvironmentVariable(key,null)
        Environment.SetEnvironmentVariable("BLADE_FP_REPRO","1")
        Environment.SetEnvironmentVariable("BLADE_FP_REASSOC",null)
        Environment.SetEnvironmentVariable("BLADE_FP_CONTRACT","off")
        Environment.SetEnvironmentVariable("BLADE_BLAS","off")
        Environment.SetEnvironmentVariable("BLADE_LLVM","off")
        Environment.SetEnvironmentVariable("BLADE_TILE_CACHE",null)
        Environment.SetEnvironmentVariable("BLADE_OMP_THREADS","1")
        setSplitTimingMode false
        setSplitTimingOnlyBinding None
        let assemblyDirectory = Path.GetDirectoryName assemblyPath
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY",assemblyDirectory + string Path.DirectorySeparatorChar)
        if Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) <> assemblyDirectory.TrimEnd(Path.DirectorySeparatorChar) then
            failwith "FSI host base did not resolve beside the frozen Blade.dll"
        line ("ASSEMBLY " + assemblyPath + " SHA256 " + hash assemblyPath)
        line (sprintf "ASSEMBLY_IDENTITY %s MVID=%O" assembly.FullName assembly.ManifestModule.ModuleVersionId)
        line ("BASE_DIRECTORY " + AppContext.BaseDirectory)
        line (sprintf "PRIVATE_OUTPUT %s length=%d" output output.Length)
        line ("OPTIMIZER_GATES_DEFAULTED " + String.concat "," Blade.Optimize.optimizerGates)
        let vars : ZarrWrite.WriteVar list =
            [ for name, offset in [("A",0); ("B",100)] do
                yield {
                    Name = name
                    DimNames = Some ["row";"col"]
                    Shape = [5L;3L]
                    Chunks = [2L;2L]
                    FillValue = FillFloat 0.0
                    Data = ZarrWrite.WF64 [| for i in 0..4 do for j in 0..2 -> float (offset + 10*i + j) |]
                    OmitChunks = []
                    Blade = None
                } ]
        ZarrWrite.writeStoreV2 store vars
        let payloads =
            [ for name in ["A";"B"] do
                for rowChunk in 0..2 do
                    for colChunk in 0..1 do
                        yield Path.Combine(store,name,sprintf "%d.%d" rowChunk colChunk) ]
        if payloads.Length <> 12 then failwith "Fixture catalog must contain exactly12 payload files"
        for path in payloads do
            if FileInfo(path).Length <> 32L then failwith ("Expected one padded32-byte chunk: " + path)
            line ("CHUNK " + path + " bytes=32 SHA256 " + hash path + " HEX " + Convert.ToHexString(File.ReadAllBytes path))
        let allFiles = Directory.GetFiles(store,"*",SearchOption.AllDirectories) |> Array.sort
        if allFiles.Length <> 17 then failwith "Expected12 payload + five metadata files only"
        for path in allFiles |> Array.filter (fun p -> not (List.contains p payloads)) do
            if FileInfo(path).Length > 2048L then failwith "Unexpectedly large fixture metadata"
            line ("METADATA " + path + " SHA256 " + hash path + " TEXT " + File.ReadAllText path)
        for file,demand,template in sources do
            let label = Path.GetFileNameWithoutExtension file
            line ("CASE " + label + " PRINT_DEMAND " + demand)
            Environment.SetEnvironmentVariable("BLADE_PRINT",demand)
            for key in keys @ ["OPENBLAS_DIR"; "TEMP"; "TMP"] do
                line (sprintf "ENV %s=%A" key (Environment.GetEnvironmentVariable key))
            let source = template.Replace("__D09_FIXTURE__",store.Replace('\\','/'))
            save file source
            Blade.Effects.Decisions.start ()
            let lowered, warnings = Blade.Lowering.lowerCaptured source
            for d in warnings do diagnostic (label + ".lower") d
            decisions (label + ".lower")
            match lowered with
            | Error reason -> line ("LOWER_REFUSED " + label + " " + reason)
            | Ok ir ->
                line ("LOWER_OK " + label)
                match Blade.IRValidate.validateIR ir with
                | Error errors ->
                    for reason in errors do line ("VALIDATE_REFUSED " + label + " " + reason)
                | Ok validated ->
                    line (sprintf "VALIDATE_OK %s sameProgramObject=%b" label (obj.ReferenceEquals(ir,validated)))
                    Blade.Effects.Decisions.start ()
                    try
                        try
                            let cpp, emitWarnings = Blade.CodeGen.genSelfContainedProgramFromIR validated ("D09_" + label.Replace('-','_'))
                            save (label + ".cpp") cpp
                            for warning in emitWarnings do line ("EMIT_WARNING " + label + " " + warning)
                            line (sprintf "EMIT_OK %s hasErrorDirective=%b rowBands=%b runTable=%b" label
                                (cpp.Contains "#error") (cpp.Contains "elementwise over segments") (cpp.Contains "__runs["))
                        with
                        | Blade.Diagnostics.BladeDiagnosticException d -> diagnostic (label + ".emit-refused") d
                        | error ->
                            artifactError <- true
                            line ("EMIT_EXCEPTION " + label + " " + error.ToString())
                    finally decisions (label + ".emit")
                // Reasons and decisions are durable BEFORE any rich IR formatting.
                section (label + ".full-ir") (fun () -> saveFull (label + ".typed-ir.txt") (box ir))
                for modulePosition,modul in List.indexed ir.Modules do
                    let moduleSite = sprintf "%s.module%d" label modulePosition
                    line (sprintf "MODULE %s name=%s reads=%d writes=%d bindings=%d functions=%d" label modul.Name modul.ProviderReads.Count modul.ProviderWrites.Count modul.Bindings.Length modul.Functions.Length)
                    section (label + ".specs-types") (fun () ->
                        saveFull (moduleSite + ".provider-reads.txt") (box modul.ProviderReads)
                        saveFull (moduleSite + ".provider-writes.txt") (box modul.ProviderWrites)
                        saveFull (moduleSite + ".declared-types.txt") (box modul.Types)
                        saveFull (moduleSite + ".derived-origins.txt") (box modul.DerivedFuncOrigins)
                        saveFull (moduleSite + ".referenced-ids.txt") (box (Blade.Optimize.moduleReferencedIds modul)))
                    for position,binding in List.indexed modul.Bindings do
                        section (sprintf "%s.binding.%d.%s" label position binding.Name) (fun () ->
                            let bindingSite = sprintf "%s.binding%d" moduleSite position
                            saveFull (bindingSite + ".txt") (box binding)
                            saveFull (bindingSite + ".side-operands.txt") (box (Blade.Optimize.bindingSideOperands modul binding.Id)))
                    for callable in modul.Functions do
                        section (sprintf "%s.callable.%s.%d" label callable.Name callable.Id) (fun () ->
                            saveFull (sprintf "%s.callable%d.txt" moduleSite callable.Id) (box callable))
                    for index,root in List.indexed (Blade.Optimize.compactionRoots modul.Functions modul) do
                        section (sprintf "%s.demand-root.%d" label index) (fun () ->
                            saveFull (sprintf "%s.demand-root%d.txt" moduleSite index) (box root))
                    section (label + ".projections") (fun () -> projections label modul)
            line ("CASE_COMPLETE " + label)
        line "PROBE_COMPLETE four observations; no generated program compiled or executed; exit0 is not optimization/runtime acceptance"
        if artifactError then 2 else 0
    finally
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY",savedBaseData)
        setSplitTimingMode savedTiming
        setSplitTimingOnlyBinding savedTarget
        for key,value in saved do Environment.SetEnvironmentVariable(key,value)

exit (Blade.Runtime.runOnLargeStack probe)
