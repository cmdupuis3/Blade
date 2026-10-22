// SOURCE ONLY: sole Luna runner executes after root freezes the bundle.
// dotnet fsi --reference:<absolute-Blade.dll> --exec <this-file> <input> <early|final>
// early is the working C03/C04 bounded public-prefix recipe; final uses lowerCaptured.
open System
open System.IO
open Blade
open Blade.IR
open Blade.Types

if fsi.CommandLineArgs.Length <> 3 then failwith "expected <input.blade> <early|final>"
let path = Path.GetFullPath fsi.CommandLineArgs.[1]
let stage = fsi.CommandLineArgs.[2]
if stage <> "early" && stage <> "final" then failwith "expected early|final"
let source = File.ReadAllText path
printfn "SOURCE %s\nASSEMBLY %s\nSTAGE %s" path typeof<IRProgram>.Assembly.Location stage
for key in ["BLADE_FUSION"; "BLADE_FP_REPRO"; "BLADE_FP_CONTRACT"; "BLADE_BLAS"; "BLADE_PRINT"] do
    printfn "ENV %s=%A" key (Environment.GetEnvironmentVariable key)

let early () =
    match Parser.parseProgram source with
    | Error e -> failwithf "parse %d:%d %s" e.Line e.Col e.Message
    | Ok raw ->
        match TypeCheck.typeCheck raw with
        | Error errors -> failwith (errors |> List.map TypeEnv.formatCompileError |> String.concat "\n")
        | Ok (typed, builder, _) ->
            if typed.Modules.Length <> 1 || raw.Modules.Length <> 1 then failwith "single-module fixture required"
            let env = { Lowering.emptyTypedEnv() with Builder = builder }
            let m, _ = Lowering.lowerTypedModule env typed.Modules.Head (Some raw.Modules.Head.Decls)
            let m = IRMono.monomorphizeModule m builder
            let modules = IRMono.monomorphizeHMFunctionsModules [m] builder
            let modules = IRMono.shapeMonomorphizeModules modules builder
            let fields =
                modules |> List.collect (fun m -> m.Types)
                |> List.choose (function IRTDStruct (n, fs) -> Some (n, fs) | _ -> None)
                |> Map.ofList
            let funcs = modules |> List.collect (fun m -> m.Functions)
            modules |> List.map (fun m ->
                let m = IRMono.resolveTypedZerosModule (fun name -> Map.tryFind name fields) m
                let m = IRMono.adaptFloatLiteralsModule m
                let m = IRMono.adaptFoldOneSeedsModule m
                IRMono.refuseEnumKeyArithModule m
                IRMono.lowerArrayBinOpsModule m builder funcs)

Runtime.runOnLargeStack (fun () ->
    let modules =
        if stage = "early" then early ()
        else
            let result, diagnostics = Lowering.lowerCaptured source
            for d in diagnostics do printfn "DIAGNOSTIC %A" d
            match result with
            | Error e -> failwith e
            | Ok ir ->
                match IRValidate.validateIR ir with
                | Error errors -> failwith (String.concat "\n" errors)
                | Ok valid -> valid.Modules
    if modules.Length <> 1 then failwith "single-module diagnostic only"
    for m in modules do
        if not m.ProviderReads.IsEmpty || not m.ProviderWrites.IsEmpty || not m.CompoundInits.IsEmpty || not m.RandomInits.IsEmpty then
            failwith "provider/compound/random fixture outside diagnostic scope"
        printfn "MODULE %s refs=%A mutableArrayLets=%A" m.Name (Optimize.moduleReferencedIds m) m.MutableArrayLets
        for t in m.Types do printfn "DECLARED_TYPE %A" t
        for b in m.Bindings do
            printfn "BINDING %s id=%d const=%b mutable=%b\nTYPE %A\nVALUE %s" b.Name b.Id b.IsConst b.IsMutable b.Type (IRPrint.ppIRExpr 0 b.Value)
            for payload in Optimize.bindingPayload m b do printfn "FULL_BINDING_PAYLOAD %s" (IRPrint.ppIRExpr 0 payload)
        for f in m.Functions do
            // Do not structured-print the whole callable/body record. C04's
            // full-IR collector needed explicit SrcLoc.Span handling; here
            // bodies use the compiler's location-aware IRPrint path instead.
            printfn "CALLABLE id=%d name=%s source=%s" f.Id f.Name f.SourceName
            printfn "CALLABLE_FLAGS static=%b commutative=%b omp=%b cuda=%b cudaBlock=%d mpi=%b arityPoly=%b arityParam=%A repro=%b"
                f.IsStatic f.IsCommutative f.IsOmpParallel f.IsCudaKernel f.CudaBlockSize f.IsMpiParallel f.IsArityPoly f.ArityParam f.IsRepro
            printfn "CALLABLE_GROUPS comm=%A anticomm=%A parallel=%A signParities=%A" f.CommGroups f.AntisymGroups f.Parallelism f.SignParities
            printfn "CALLABLE_EFFECTS mutates=%b output=%b external=%b fail=%b unknown=%b"
                f.Effects.Mutates f.Effects.EmitsOutput f.Effects.ReadsExternal f.Effects.MayFail f.Effects.Unknown
            for p in f.Params do
                printfn "PARAM name=%s index=%d varId=%d type=%s\nPARAM_TYPE_PAYLOAD %A" p.Name p.Index p.VarId (IRPrint.ppIRType p.Type) p.Type
            for c in f.Captures do
                printfn "CAPTURE name=%s id=%d mutable=%b type=%s\nCAPTURE_TYPE_PAYLOAD %A" c.Name c.Id c.IsMutable (IRPrint.ppIRType c.Type) c.Type
            printfn "RETURN_TYPE %s\nRETURN_TYPE_PAYLOAD %A\nCALLABLE_BODY %s" (IRPrint.ppIRType f.RetType) f.RetType (IRPrint.ppIRExpr 0 f.Body)
        for KeyValue (id, value) in m.SparseInits do printfn "SPARSE_INIT %d %s" id (IRPrint.ppIRExpr 0 value)
        if Path.GetFileName(path).StartsWith "01_" then
            let parent = m.Bindings |> List.find (fun b -> b.Name = "S")
            let host = m.Bindings |> List.find (fun b -> b.Name = "total")
            match Optimize.storedArrayProof m parent with
            | Error why -> printfn "EXISTING_STORED_PROOF refused=%s" why
            | Ok (why, chain) -> printfn "EXISTING_STORED_PROOF admitted=%s chainIds=%A" why (chain |> List.map _.Id)
            match parent.Type, host.Type, host.Value with
            | ArrayElem a, IRTScalar ETFloat64,
              IRReduce (IRIndex (IRVar (sid, _), [IRTuple [IRLit IRLitUnit; IRLit (IRLitInt 1L)]], _), fold, Some (IRLit (IRLitFloat 7.0)))
                    when sid = parent.Id && a.IndexTypes.Length = 1 && a.IndexTypes.Head.IxKind = IxKSparse && a.IndexTypes.Head.Rank = 2 ->
                printfn "PRIMARY_DIRECT_TOPOLOGY_MATCH host=%d parent=%d fold=%s sparseInitializer=%b\nFOLD_TYPE_PAYLOAD %A"
                    host.Id parent.Id (IRPrint.ppIRExpr 0 fold) (m.SparseInits.ContainsKey parent.Id) (IR.typeOf fold)
            | _ -> failwithf "PRIMARY_TOPOLOGY_CHANGED: stop and review; do not broaden the proposed route\n%s" (IRPrint.ppIRExpr 0 host.Value))
