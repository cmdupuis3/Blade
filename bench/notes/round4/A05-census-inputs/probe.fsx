// SOURCE ONLY. Sole Luna may lower/emit after root freezes this bundle.
// dotnet fsi --reference:<reviewed-Blade.dll> --exec <probe> <source> <new-output> <reuse:0|1> <expected-DLL-SHA256> <lower|emit>
open System
open System.IO
open System.Security.Cryptography
open Blade
open Blade.IR
open Blade.Types

let sha (path: string) : string = File.ReadAllBytes path |> SHA256.HashData |> Convert.ToHexString
let args: string array = fsi.CommandLineArgs |> Array.skip 1
if args.Length <> 5 then failwith "Expected source, new-output, reuse0|1, expected-DLL-SHA256, lower|emit"
let sourcePath: string = Path.GetFullPath args.[0]
let output: string = Path.GetFullPath args.[1]
let reuse: string = args.[2]
let mode: string = args.[4]
if not (List.contains reuse ["0"; "1"]) || not (List.contains mode ["lower"; "emit"]) then failwith "Unknown arm"
let assembly: string = typeof<IRProgram>.Assembly.Location
if not (String.Equals(sha assembly, args.[3], StringComparison.OrdinalIgnoreCase)) then failwith "Loaded DLL differs from root-approved identity"
if File.Exists output || Directory.Exists output then failwith "Output must be a new private directory"

// All optimizer/scheduler gates take their ACTUAL DEFAULTS except the paired
// reuse gate. Do not inherit an isolated scheduler, CSE=0, or fusion-off arm.
for key in Optimize.optimizerGates do Environment.SetEnvironmentVariable(key, null)
let pins: (string * string option) list =
    [ "BLADE_POOL_REUSE", Some reuse
      "BLADE_FP_REASSOC", None; "BLADE_FP_REPRO", Some "1"; "BLADE_FP_CONTRACT", Some "off"
      "BLADE_BLAS", Some "off"; "BLADE_CUBLAS", None; "BLADE_OMP_THREADS", Some "1"; "OMP_NUM_THREADS", Some "1"
      "BLADE_PRINT", None; "BLADE_TILE_CACHE", None; "BLADE_TILE_CACHE_VERBOSE", None
      "BLADE_LLVM_FACTS", None; "BLADE_LLVM_BRICKS", None; "BLADE_SYRK_TILE", None
      "BLADE_EXE_CACHE", Some "0"; "BLADE_MEMCHECK", None; "BLADE_RUN_RECORD", None ]
for key, value in pins do Environment.SetEnvironmentVariable(key, Option.toObj value)
Directory.CreateDirectory output |> ignore
let write (name: string) (value: string) : unit = File.WriteAllText(Path.Combine(output, name), value)
let source: string = File.ReadAllText sourcePath
write "source.blade" source
write "identity.txt" ($"assembly={assembly}\nassemblySha={sha assembly}\nsourcePath={sourcePath}\nsourceSha={sha sourcePath}\nprobeSha={sha fsi.CommandLineArgs.[0]}\nreuse={reuse}\nmode={mode}\n")
let keys: string list = (Optimize.optimizerGates @ (pins |> List.map fst)) |> List.distinct
write "environment.txt" (keys |> List.map (fun key -> key + "=" + (Environment.GetEnvironmentVariable key |> Option.ofObj |> Option.defaultValue "<unset>")) |> String.concat "\n")
let decisions (name: string) : unit =
    write name (Effects.Decisions.drain () |> List.map (fun d -> Effects.Decisions.render d + "\n" + sprintf "%A" d) |> String.concat "\n")
let donors () : Map<IRId, IRId> * Map<IRId, IRId> = PoolReuseTable.donors (), PoolReuseTable.returnDonors ()

Runtime.runOnLargeStack (fun () ->
    Effects.Decisions.start ()
    let result, diagnostics = Lowering.lowerCaptured source
    write "diagnostics.txt" (diagnostics |> List.map (sprintf "%A") |> String.concat "\n")
    let ir =
        match result with
        | Error why -> failwith ("Lowering refused: " + why)
        | Ok ir ->
            match IRValidate.validateIR ir with
            | Error errors -> failwith (String.concat "\n" errors)
            | Ok valid -> valid
    decisions "lower-decisions.txt"
    let text = Text.StringBuilder()
    let line (s: string) : unit = text.AppendLine(s) |> ignore
    let ids = Collections.Generic.HashSet<IRId>()
    for m in ir.Modules do
        if not m.ProviderReads.IsEmpty || not m.ProviderWrites.IsEmpty || not m.CompoundInits.IsEmpty || not m.RandomInits.IsEmpty then
            failwith "Provider/compound/random input outside A05 census"
        line (sprintf "MODULE %s mutableArrayLets=%A refs=%A" m.Name m.MutableArrayLets (Optimize.moduleReferencedIds m))
        for t in m.Types do line (sprintf "DECLARED_TYPE %A" t)
        for b in m.Bindings do
            ids.Add b.Id |> ignore
            line (sprintf "BINDING %s id=%d const=%b mutable=%b\nTYPE %A\nVALUE %s" b.Name b.Id b.IsConst b.IsMutable b.Type (IRPrint.ppIRExpr 0 b.Value))
            for payload in Optimize.bindingPayload m b do line ("BINDING_PAYLOAD " + IRPrint.ppIRExpr 0 payload)
        for f in m.Functions do
            ids.Add f.Id |> ignore
            line (sprintf "CALLABLE id=%d name=%s source=%s static=%b omp=%b cuda=%b mpi=%b arityPoly=%b repro=%b" f.Id f.Name f.SourceName f.IsStatic f.IsOmpParallel f.IsCudaKernel f.IsMpiParallel f.IsArityPoly f.IsRepro)
            line (sprintf "GROUPS comm=%A anticomm=%A parallel=%A signParities=%A" f.CommGroups f.AntisymGroups f.Parallelism f.SignParities)
            line (sprintf "EFFECTS mutates=%b output=%b external=%b mayFail=%b unknown=%b" f.Effects.Mutates f.Effects.EmitsOutput f.Effects.ReadsExternal f.Effects.MayFail f.Effects.Unknown)
            for p in f.Params do
                ids.Add p.VarId |> ignore
                line (sprintf "PARAM %s index=%d id=%d TYPE_PAYLOAD %A" p.Name p.Index p.VarId p.Type)
            for c in f.Captures do line (sprintf "CAPTURE %s id=%d mutable=%b TYPE_PAYLOAD %A" c.Name c.Id c.IsMutable c.Type)
            line (sprintf "RETURN_TYPE_PAYLOAD %A\nBODY %s" f.RetType (IRPrint.ppIRExpr 0 f.Body))
            iterIRExpr (fun e ->
                for id in collectVarRefsIR e do ids.Add id |> ignore
                match e with
                | IRLet (id, value, _) ->
                    ids.Add id |> ignore
                    line (sprintf "LOCAL function=%d id=%d TYPE_PAYLOAD %A\nVALUE %s" f.Id id (typeOf value) (IRPrint.ppIRExpr 0 value))
                | IRVar (id, ty) -> line (sprintf "VAR id=%d TYPE_PAYLOAD %A" id ty)
                | IRApplyCombinator info ->
                    line (sprintf "APPLY_TYPES output=%A arrays=%A shared=%A tdims=%A" info.OutputType info.ArrayTypes info.SharedIndexTypes info.KernelTDims)
                    line (sprintf "APPLY_META inputRanks=%A outputRank=%d sdims=%A triangular=%A symcom=%A coiteration=%b reynolds=%b speedup=%d reynoldsSpeedup=%d identities=%A"
                        info.KernelInputRanks info.KernelOutputRank info.SDimsPerArray info.TriangularLevels info.SymcomStates info.IsCoIteration info.HasReynolds info.SpeedupFactor info.ReynoldsSpeedup info.Identities)
                    line ("APPLY_LOOP " + IRPrint.ppIRExpr 0 info.Loop)
                    line ("APPLY_KERNEL " + IRPrint.ppIRExpr 0 info.Kernel)
                | IRMethodFor info ->
                    line (sprintf "METHOD_TYPES arrays=%A shared=%A sdims=%A total=%d identities=%A" info.ArrayTypes info.SharedIndexTypes info.SDimsPerArray info.TotalSDims info.Identities)
                | IRBeginPoolScope _ | IRReleasePools _ -> line ("LIFETIME_EVENT " + IRPrint.ppIRExpr 0 e)
                | _ -> ()) f.Body
    write "topology.txt" (text.ToString())
    let owners =
        ids |> Seq.sort |> Seq.map (fun id ->
            sprintf "id=%d hotProducer=%b hotConsumer=%b cellLoop=%b cellFold=%b epilogueProducer=%b epilogueJoin=%b compactMask=%b compactCompound=%b compactFoldMap=%b compactRowProducer=%b compactRowConsumer=%b"
                id (Optimize.HotRowTable.tryProducer id |> Option.isSome) (Optimize.HotRowTable.tryConsumer id |> Option.isSome)
                (Optimize.CellFoldTable.tryLoop id |> Option.isSome) (Optimize.CellFoldTable.tryFoldLet id |> Option.isSome)
                (Optimize.EpilogueLegTable.tryProducer id |> Option.isSome) (Optimize.EpilogueLegTable.tryJoin id |> Option.isSome)
                (Optimize.CompactionTable.tryMask id |> Option.isSome) (Optimize.CompactionTable.tryCompound id |> Option.isSome)
                (Optimize.CompactionTable.tryFoldMap id |> Option.isSome) (Optimize.CompactRowTable.tryProducer id |> Option.isSome)
                (Optimize.CompactRowTable.tryConsumer id |> Option.isSome))
        |> String.concat "\n"
    write "scheduler-owners.txt" owners
    let before = donors ()
    write "donors-before.txt" (sprintf "%A" before)
    if mode = "emit" then
        // Same validated tree and live tables, C++ first, then LLVM. No relower.
        Effects.Decisions.start ()
        let cpp, warnings = CodeGen.genSelfContainedProgramFromIR ir "A05 census"
        write "arm.cpp" cpp
        write "cpp-warnings.txt" (String.concat "\n" warnings)
        decisions "cpp-decisions.txt"
        Effects.Decisions.start ()
        match EmitLlvm.tryEmitProgramNamed "A05 census" ir with
        | Error why ->
            write "llvm-refusal.txt" why
            decisions "llvm-decisions.txt"
        | Ok ll ->
            write "arm.ll" ll
            decisions "llvm-decisions.txt"
        write "donors-after.txt" (sprintf "%A\nunchanged=%b" (donors ()) (before = donors ())))
