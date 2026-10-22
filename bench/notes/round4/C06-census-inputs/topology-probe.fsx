// SOURCE ONLY. Sole Luna runner executes after root reviews/fixes the binary.
// dotnet fsi --reference:<exact-Blade.dll> topology-probe.fsx <input> <early|final> <fresh-output-dir>
open System
open System.IO
open System.Collections
open Microsoft.FSharp.Reflection
open Blade
open Blade.IR

let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length <> 3 then failwith "expected <input.blade> <early|final> <fresh-output-directory>"
let path = Path.GetFullPath args.[0]
let stage = args.[1]
if stage <> "early" && stage <> "final" then failwith "expected early|final"
let outDir = Path.GetFullPath args.[2]
if Directory.Exists outDir then failwith "output directory must be fresh"
Directory.CreateDirectory outDir |> ignore
let utf8 = System.Text.UTF8Encoding(false)
let source = File.ReadAllText path
File.WriteAllText(Path.Combine(outDir, "source.blade"), source, utf8)
printfn "SOURCE %s\nASSEMBLY %s\nSTAGE %s" path typeof<IRProgram>.Assembly.Location stage
for key in ["BLADE_PRINT"; "BLADE_FP_REPRO"; "BLADE_FP_REASSOC"; "BLADE_FP_CONTRACT"; "BLADE_FUSION"; "BLADE_BLAS"; "BLADE_OMP_THREADS"; "BLADE_LLVM"] do
    printfn "ENV %s=%A" key (Environment.GetEnvironmentVariable key)

// Exhaustive reflected IR dump: unlike %A, no collection/depth truncation.
let dumpFull (path: string) (value: obj) =
    use writer = new StreamWriter(path, false, utf8)
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
            else failwithf "unhandled full-IR dump type %s" t.FullName
    dump 0 value


// Working C03/C04/C05 public prefix, before selected/Optimize/IRLift/CSE.
// Only single-module provider-free inputs are admitted. No private preflight
// reproduction; this is a phase probe, not an alternate lowering API.
let early () =
    match Parser.parseProgram source with
    | Error e -> failwithf "parse %d:%d %s" e.Line e.Col e.Message
    | Ok raw ->
        match TypeCheck.typeCheck raw with
        | Error errors -> failwith (errors |> List.map TypeEnv.formatCompileError |> String.concat "\n")
        | Ok (typed, builder, _) ->
            if typed.Modules.Length <> 1 || raw.Modules.Length <> 1 then failwith "single module required"
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
    let modules, fullValue =
        if stage = "early" then
            let modules = early ()
            printfn "PHASE public-prefix after lowerArrayBinOps; compound-free selected pass inert; before Optimize"
            modules, box modules
        else
            match Lowering.lower source with
            | Error e -> failwith e
            | Ok ir ->
                match IRValidate.validateIR ir with
                | Error errors -> failwith (String.concat "; " errors)
                | Ok valid ->
                    printfn "PHASE canonical post-optimization lowering, IRValidate OK"
                    valid.Modules, box valid
    if modules.Length <> 1 then failwith "single-module fixture required"
    for m in modules do
        if not m.ProviderReads.IsEmpty || not m.ProviderWrites.IsEmpty || not m.CompoundInits.IsEmpty
           || not m.RandomInits.IsEmpty || not m.SparseInits.IsEmpty then failwith "fixture outside provider-free plain census scope"
    // Complete payload, not structured %A: includes types/captures/side tables,
    // SrcLoc.Span, MethodFor/ApplyInfo metadata and every nested source scope.
    dumpFull (Path.Combine(outDir, "typed-ir.txt")) fullValue
    for m in modules do
        printfn "MODULE %s bindings=%d functions=%d" m.Name m.Bindings.Length m.Functions.Length
        let summarize scope expr =
            iterIRExpr (function
                | IRLet (id, value, _) -> printfn "LET scope=%s id=%d type=%s value=%s" scope id (IRPrint.ppIRType (typeOf value)) (IRPrint.ppIRExpr 0 value)
                | IRForRange (id, lo, hi, _) -> printfn "FOR scope=%s id=%d lo=%s hi=%s" scope id (IRPrint.ppIRExpr 0 lo) (IRPrint.ppIRExpr 0 hi)
                | IRAssign (target, value) -> printfn "ASSIGN scope=%s target=%s value=%s" scope (IRPrint.ppIRExpr 0 target) (IRPrint.ppIRExpr 0 value)
                | IRReduceCompute (comp, kernel, seed) -> printfn "FUSED_FOLD scope=%s comp=%s kernel=%s seed=%s" scope (IRPrint.ppIRExpr 0 comp) (IRPrint.ppIRExpr 0 kernel) (IRPrint.ppIRExpr 0 seed)
                | IRReduce (arr, kernel, seed) -> printfn "STORED_FOLD scope=%s arr=%s kernel=%s seeded=%b" scope (IRPrint.ppIRExpr 0 arr) (IRPrint.ppIRExpr 0 kernel) seed.IsSome
                | IRApplyCombinator info -> printfn "APPLY scope=%s arrays=%s inputRanks=%A output=%s kernel=%s" scope (info.Arrays |> List.map (IRPrint.ppIRExpr 0) |> String.concat "; ") info.KernelInputRanks (IRPrint.ppIRType info.OutputType) (IRPrint.ppIRExpr 0 info.Kernel)
                | _ -> ()) expr
        for b in m.Bindings do
            printfn "BINDING name=%s id=%d mutable=%b type=%s\nVALUE %s" b.Name b.Id b.IsMutable (IRPrint.ppIRType b.Type) (IRPrint.ppIRExpr 0 b.Value)
            for payload in Optimize.bindingPayload m b do summarize (sprintf "binding:%s#%d" b.Name b.Id) payload
        for f in m.Functions do
            printfn "CALLABLE name=%s id=%d captures=%d body=%s" f.Name f.Id f.Captures.Length (IRPrint.ppIRExpr 0 f.Body)
            for p in f.Params do printfn "PARAM callable=%d id=%d index=%d type=%s" f.Id p.VarId p.Index (IRPrint.ppIRType p.Type)
            summarize (sprintf "callable:%s#%d" f.Name f.Id) f.Body)
