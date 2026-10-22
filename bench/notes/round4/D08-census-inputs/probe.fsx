// Sole-Luna execution only, after root approves D08-design.md.
// Prepare one bounded fixture, lower/validate once, emit once. Never compile
// generated C++, evaluate the Blade program, invoke tests, or benchmark.
open System
open System.IO
open Microsoft.FSharp.Reflection
open Blade.IR

let kind (expr: IRExpr) : string =
    let case, _ = FSharpValue.GetUnionFields(expr, typeof<IRExpr>)
    case.Name

let describe (label: string) (expr: IRExpr) : unit =
    printfn "EXPR %s kind=%s" label (kind expr)
    printfn "%s" (Blade.IRPrint.ppIRExpr 0 expr)
    // This visits IR syntax/provenance; these are NOT execution/read counts.
    iterIRExpr (fun node ->
        match node with
        | IRLet (id, value, body) ->
            printfn "  LOCAL id=%d value=%s body=%s" id (kind value) (kind body)
        | IRVar (id, ty) ->
            printfn "  VAR id=%d type=%s" id (Blade.IRPrint.ppIRType ty)
        | IRApplyCombinator info ->
            printfn "  APPLY inputRanks=%A outputRank=%d coiteration=%b sdims=%A outputType=%s"
                info.KernelInputRanks info.KernelOutputRank info.IsCoIteration info.SDimsPerArray
                (Blade.IRPrint.ppIRType info.OutputType)
            printfn "    KERNEL %s" (Blade.IRPrint.ppIRExpr 0 info.Kernel)
            printfn "    ARRAYS %A" (info.Arrays |> List.map (Blade.IRPrint.ppIRExpr 0))
            printfn "    ARRAY_TYPES %A" (info.ArrayTypes |> List.map (mkArrayLike >> Blade.IRPrint.ppIRType))
            printfn "    SHARED_INDEX_IDS %A" (info.SharedIndexTypes |> List.map (fun ix -> ix.Id))
        | IRReduce (source, kernel, seed) ->
            printfn "  REDUCE source=%s kernel=%s seed=%A"
                (Blade.IRPrint.ppIRExpr 0 source) (Blade.IRPrint.ppIRExpr 0 kernel)
                (seed |> Option.map (Blade.IRPrint.ppIRExpr 0))
        | IRReduceCompute (source, kernel, seed) ->
            printfn "  REDUCE_COMPUTE source=%s kernel=%s seed=%s"
                (kind source) (Blade.IRPrint.ppIRExpr 0 kernel) (Blade.IRPrint.ppIRExpr 0 seed)
        | _ -> ()) expr

let printDecisions (stage: string) : unit =
    for decision in Blade.Effects.Decisions.drain () do
        printfn "DECISION %s %s" stage (Blade.Effects.Decisions.render decision)

let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length <> 2 then
    failwith "Usage: dotnet fsi --reference:<approved Blade.dll> --exec probe.fsx <bundle-directory> <new-private-output-directory>"

let bundle = Path.GetFullPath args.[0]
let output = Path.GetFullPath args.[1]
if Directory.Exists output || File.Exists output then
    failwith "Output must be a new private directory; probe never overwrites or deletes an existing case."

let sourcePath = Path.Combine(bundle, "01-statistic-barrier.blade")
let sourceText = File.ReadAllText sourcePath
let chunkRows : string array = File.ReadAllLines(Path.Combine(bundle, "chunks.tsv"))
if chunkRows.Length <> 3 then failwith "Expected exactly three authored chunk rows."
let chunks : (string * byte array) array =
    chunkRows |> Array.mapi (fun index row ->
        let columns = row.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
        if columns.Length <> 2 || columns.[0] <> string index || columns.[1].Length <> 32 then
            failwith "Unexpected fixed chunk descriptor."
        let payload = Convert.FromHexString columns.[1]
        if payload.Length <> 16 then failwith "Expected one padded two-cell binary64 chunk."
        columns.[0], payload)

let arrayDirectory = Path.Combine(output, "fixture.zarr", "A")
Directory.CreateDirectory arrayDirectory |> ignore
File.Copy(Path.Combine(bundle, "fixture.zgroup.json"), Path.Combine(output, "fixture.zarr", ".zgroup"))
File.Copy(Path.Combine(bundle, "fixture.zarray.json"), Path.Combine(arrayDirectory, ".zarray"))
File.Copy(Path.Combine(bundle, "fixture.zattrs.json"), Path.Combine(arrayDirectory, ".zattrs"))
for name, payload in chunks do File.WriteAllBytes(Path.Combine(arrayDirectory, name), payload)
File.WriteAllText(Path.Combine(output, "input.blade"), sourceText)

let keys : string list =
    [ "BLADE_FP_REPRO"; "BLADE_FP_REASSOC"; "BLADE_FP_CONTRACT";
      "BLADE_BLAS"; "BLADE_LLVM"; "BLADE_PRINT"; "BLADE_FUSION"; "BLADE_TILE_CACHE" ]
let prior = keys |> List.map (fun key -> key, Environment.GetEnvironmentVariable key)
let priorDirectory = Environment.CurrentDirectory
let status =
    try
        Environment.CurrentDirectory <- output
        Environment.SetEnvironmentVariable("BLADE_FP_REPRO", "1")
        Environment.SetEnvironmentVariable("BLADE_FP_REASSOC", null)
        Environment.SetEnvironmentVariable("BLADE_FP_CONTRACT", "off")
        Environment.SetEnvironmentVariable("BLADE_BLAS", "off")
        Environment.SetEnvironmentVariable("BLADE_LLVM", "off")
        Environment.SetEnvironmentVariable("BLADE_PRINT", "result")
        Environment.SetEnvironmentVariable("BLADE_FUSION", "on")
        Environment.SetEnvironmentVariable("BLADE_TILE_CACHE", null)
        printfn "ASSEMBLY %s" typeof<IRProgram>.Assembly.Location
        printfn "SOURCE %s" sourcePath
        printfn "PRIVATE_CASE %s" output
        printfn "FIXTURE N=5 C=2 disk=<f8 payload_bytes=48 values=[1;2;3;4;5] padded_tail=0"
        for key in keys do printfn "ENV %s=%A" key (Environment.GetEnvironmentVariable key)
        Blade.Effects.Decisions.start ()
        let lowered, diagnostics =
            Blade.Runtime.runOnLargeStack (fun () -> Blade.Lowering.lowerCaptured sourceText)
        for diagnostic in diagnostics do printfn "DIAGNOSTIC %A" diagnostic
        printDecisions "lower"
        match lowered with
        | Error error ->
            printfn "LOWER_REFUSED %s" error
            3
        | Ok ir ->
            match Blade.IRValidate.validateIR ir with
            | Error errors ->
                for error in errors do printfn "IR_REFUSED %s" error
                4
            | Ok validated ->
                printfn "VALIDATED"
                for modul in validated.Modules do
                    printfn "MODULE %s" modul.Name
                    printfn "MODULE_TYPES %A" modul.Types
                    printfn "PROVIDER_READS %A" modul.ProviderReads
                    printfn "PROVIDER_WRITES %A" modul.ProviderWrites
                    printfn "DERIVED_ORIGINS %A" modul.DerivedFuncOrigins
                    printfn "REFERENCED_IDS %A" (Blade.Optimize.moduleReferencedIds modul)
                    for position, binding in List.indexed modul.Bindings do
                        printfn "BINDING position=%d name=%s id=%d mutable=%b const=%b type=%s"
                            position binding.Name binding.Id binding.IsMutable binding.IsConst
                            (Blade.IRPrint.ppIRType binding.Type)
                        describe binding.Name binding.Value
                        for side in Blade.Optimize.bindingSideOperands modul binding.Id do
                            describe (binding.Name + ".side") side
                    for callable in modul.Functions do
                        printfn "CALLABLE %s id=%d source=%s comm=%b groups=%A omp=%b cuda=%b mpi=%b repro=%b"
                            callable.Name callable.Id callable.SourceName callable.IsCommutative callable.CommGroups
                            callable.IsOmpParallel callable.IsCudaKernel callable.IsMpiParallel callable.IsRepro
                        printfn "  PARAMS %A"
                            (callable.Params |> List.map (fun param -> param.Name, param.VarId, param.Index, Blade.IRPrint.ppIRType param.Type))
                        printfn "  CAPTURES %A"
                            (callable.Captures |> List.map (fun capture -> capture.Name, capture.Id, capture.IsMutable, Blade.IRPrint.ppIRType capture.Type))
                        printfn "  EFFECTS %A" callable.Effects
                        describe callable.Name callable.Body
                Blade.Effects.Decisions.start ()
                let cpp, warnings = Blade.CodeGen.genSelfContainedProgramFromIR validated "D08_statistic_barrier"
                for warning in warnings do printfn "EMIT_WARNING %s" warning
                printDecisions "emit"
                File.WriteAllText(Path.Combine(output, "baseline.cpp"), cpp)
                printfn "EMITTED baseline.cpp chars=%d" cpp.Length
                if cpp.Contains "#error" then
                    printfn "EMIT_CONTAINS_REFUSAL"
                    5
                else 0
    finally
        Environment.CurrentDirectory <- priorDirectory
        for key, value in prior do Environment.SetEnvironmentVariable(key, value)

exit status
