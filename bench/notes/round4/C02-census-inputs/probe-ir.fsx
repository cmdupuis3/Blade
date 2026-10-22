// Runner execution only. Supply the existing C01 Blade.dll with --reference.
// Reads one fixture, lowers/validates it, and prints IR. No backend generation,
// C++ compilation, program evaluation, test or file mutation occurs here.
open System
open System.IO
open Microsoft.FSharp.Reflection
open Blade.IR

let kind (e: IRExpr) =
    let case, _ = FSharpValue.GetUnionFields(e, typeof<IRExpr>)
    case.Name

let showType (e: IRExpr) =
    try Blade.IRPrint.ppIRType (typeOf e)
    with ex -> "<type unavailable: " + ex.Message + ">"

let summary (label: string) (e: IRExpr) =
    printfn "EXPR %s :: %s" label (showType e)
    printfn "%s" (Blade.IRPrint.ppIRExpr 0 e)
    iterIRExpr (fun n ->
        match n with
        | IRLet (id, value, body) ->
            printfn "  LOCAL id=%d value=%s :: %s body=%s" id (kind value) (showType value) (kind body)
        | IRApplyCombinator info ->
            printfn "  APPLY inputRanks=%A outputRank=%d coiteration=%b sdims=%A outputType=%s"
                info.KernelInputRanks info.KernelOutputRank info.IsCoIteration info.SDimsPerArray
                (Blade.IRPrint.ppIRType info.OutputType)
            printfn "    kernel=%s arrays=%A sharedIndexIds=%A"
                (Blade.IRPrint.ppIRExpr 0 info.Kernel)
                (info.Arrays |> List.map (Blade.IRPrint.ppIRExpr 0))
                (info.SharedIndexTypes |> List.map (fun ix -> ix.Id))
        | IRReduce (source, kernel, seed) ->
            printfn "  REDUCE source=%s kernel=%s seed=%A"
                (Blade.IRPrint.ppIRExpr 0 source) (Blade.IRPrint.ppIRExpr 0 kernel)
                (seed |> Option.map (Blade.IRPrint.ppIRExpr 0))
        | IRReduceCompute (source, kernel, seed) ->
            printfn "  REDUCE_COMPUTE source=%s kernel=%s seed=%s"
                (kind source) (Blade.IRPrint.ppIRExpr 0 kernel) (Blade.IRPrint.ppIRExpr 0 seed)
        | _ -> ()) e

let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length <> 1 then
    eprintfn "Usage: dotnet fsi --reference:<C01 Blade.dll> --exec probe-ir.fsx <fixture.blade>"
    exit 2

let sourcePath = Path.GetFullPath args.[0]
printfn "SOURCE %s" sourcePath
printfn "ASSEMBLY %s" typeof<IRProgram>.Assembly.Location
for key in [ "BLADE_FP_REPRO"; "BLADE_FP_CONTRACT"; "BLADE_FUSION"; "BLADE_PRINT"; "BLADE_LLVM" ] do
    printfn "ENV %s=%A" key (Environment.GetEnvironmentVariable key)

Blade.Effects.Decisions.start ()
let lowered, diagnostics =
    Blade.Runtime.runOnLargeStack (fun () -> Blade.Lowering.lowerCaptured (File.ReadAllText sourcePath))
for d in diagnostics do printfn "DIAGNOSTIC %A" d
let decisions = Blade.Effects.Decisions.drain ()
for d in decisions do printfn "DECISION %s" (Blade.Effects.Decisions.render d)
match lowered with
| Error e ->
    printfn "LOWER_REFUSED %s" e
    exit 3
| Ok ir ->
    match Blade.IRValidate.validateIR ir with
    | Error errors ->
        for e in errors do printfn "IR_REFUSED %s" e
        exit 4
    | Ok ir ->
        printfn "VALIDATED"
        for m in ir.Modules do
            printfn "MODULE %s derivedOrigins=%A" m.Name m.DerivedFuncOrigins
            for b in m.Bindings do
                printfn "BINDING %s id=%d mutable=%b const=%b :: %s"
                    b.Name b.Id b.IsMutable b.IsConst (Blade.IRPrint.ppIRType b.Type)
                summary b.Name b.Value
            for f in m.Functions do
                printfn "CALLABLE %s id=%d source=%s comm=%b groups=%A omp=%b cuda=%b mpi=%b repro=%b"
                    f.Name f.Id f.SourceName f.IsCommutative f.CommGroups f.IsOmpParallel f.IsCudaKernel f.IsMpiParallel f.IsRepro
                printfn "  PARAMS %A" (f.Params |> List.map (fun p -> p.Name, p.VarId, p.Index, Blade.IRPrint.ppIRType p.Type))
                printfn "  CAPTURES %A" (f.Captures |> List.map (fun c -> c.Name, c.Id, Blade.IRPrint.ppIRType c.Type))
                summary f.Name f.Body
