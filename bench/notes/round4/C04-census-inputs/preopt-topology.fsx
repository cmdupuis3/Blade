// SOURCE ONLY: Luna executes after root selects an exact combined Blade.dll.
// dotnet fsi --reference:<absolute-Blade.dll> --exec <this-file> <input.blade> <role>
// Roles: partial | scalar | demanded | idiom. No optimize/codegen/run calls.
// Recipe: integration/bench/.build/C03-runner/preselected-probe-v4.fsx.
open System
open System.IO
open Blade
open Blade.IR
open Blade.Types

let fail code msg = eprintfn "%s %s" code msg; exit 3
if fsi.CommandLineArgs.Length <> 3 then fail "USAGE" "expected <input.blade> <partial|scalar|demanded|idiom>"
let sourcePath = Path.GetFullPath fsi.CommandLineArgs.[1]
let role = fsi.CommandLineArgs.[2]
if not (List.contains role ["partial"; "scalar"; "demanded"; "idiom"]) then fail "ROLE" role
let source = File.ReadAllText sourcePath
printfn "SOURCE %s" sourcePath
printfn "ASSEMBLY %s" typeof<IRProgram>.Assembly.Location
printfn "STAGE pre-selected/pre-optimize: public parse + typecheck + lowerTypedModule + mono/HM/shape + typed-zero/float/seed adaptation + lowerArrayBinOpsModule. Single-module/provider-free diagnostic only; private preflight checks are not reproduced. No lowerCaptured/optimizer/emitter/interpreter call."
match Parser.parseProgram source with
| Error e -> fail "PARSE_REFUSED" (sprintf "%d:%d %s" e.Line e.Col e.Message)
| Ok rawProgram ->
    match TypeCheck.typeCheck rawProgram with
    | Error errors -> errors |> List.iter (TypeEnv.formatCompileError >> eprintfn "%s"); exit 4
    | Ok (typedProgram, builder, _) ->
        if typedProgram.Modules.Length <> 1 || rawProgram.Modules.Length <> 1 then fail "MODULE_SHAPE" "expected a single source module"
        let env = { Lowering.emptyTypedEnv() with Builder = builder }
        let loweredModule, _exports = Lowering.lowerTypedModule env typedProgram.Modules.Head (Some rawProgram.Modules.Head.Decls)
        let loweredModule = IRMono.monomorphizeModule loweredModule builder
        let modules = IRMono.monomorphizeHMFunctionsModules [loweredModule] builder
        let modules = IRMono.shapeMonomorphizeModules modules builder
        let zeroStructFields =
            let allTypes = modules |> List.collect (fun m -> m.Types)
            let fields = allTypes |> List.choose (function IRTDStruct (n, fs) -> Some (n, fs) | _ -> None) |> Map.ofList
            fun name -> Map.tryFind name fields
        let programFuncs = modules |> List.collect (fun m -> m.Functions)
        let modules = modules |> List.map (fun m ->
            let m = IRMono.resolveTypedZerosModule zeroStructFields m
            let m = IRMono.adaptFloatLiteralsModule m
            let m = IRMono.adaptFoldOneSeedsModule m
            IRMono.refuseEnumKeyArithModule m
            IRMono.lowerArrayBinOpsModule m builder programFuncs)
        if modules.Length <> 1 then fail "MODULE_SHAPE" "specialization introduced another module"
        let m = modules.Head
        if not m.ProviderReads.IsEmpty || not m.ProviderWrites.IsEmpty || not m.CompoundInits.IsEmpty then
            fail "FIXTURE_SCOPE" "expected provider-free inputs with no compounds"
        printfn "MODULE %s role=%s bindings=%d functions=%d foreignRefs=empty-for-this-single-module-fixture" m.Name role m.Bindings.Length m.Functions.Length
        printfn "SELECTED_PASS_INERT no CompoundInits; this prefix equals the input to optimizeModule for these fixtures"
        for b in m.Bindings do
            printfn "BINDING name=%s id=%d type=%s" b.Name b.Id (IRPrint.ppIRType b.Type)
            printfn "TYPE_PAYLOAD %A" b.Type
            printfn "VALUE %s" (IRPrint.ppIRExpr 0 b.Value)
            printfn "VALUE_PAYLOAD %A" b.Value
        for f in m.Functions do
            printfn "CALLABLE id=%d name=%s source=%s" f.Id f.Name f.SourceName
            printfn "CALLABLE_PAYLOAD %A" f
        let sourceBinding = m.Bindings |> List.find (fun b -> b.Name = "M")
        let host = m.Bindings |> List.find (fun b -> b.Name = (if role = "scalar" then "total" else "rows"))
        let sourceAxes = match sourceBinding.Type with ArrayElem a -> a.IndexTypes | _ -> fail "SOURCE_TYPE" "M is not an array"
        if sourceAxes.Length <> 2 then fail "SOURCE_RANK" "M is not rank two"
        if role = "scalar" then
            printfn "SCALAR_CONTROL host=%d type=%s; inspect explicit accumulator/nest payload above (not a C04 admission)" host.Id (IRPrint.ppIRType host.Type)
        else
            match host.Type with
            | ArrayElem a when a.IndexTypes.Length = 1 && IRTotality.sameIndex a.IndexTypes.Head sourceAxes.[1] ->
                printfn "OUTPUT_C_IDENTITY_MATCH host=%d sourceC=%A outputC=%A" host.Id sourceAxes.[1] a.IndexTypes.Head
            | _ -> fail "OUTPUT_IDENTITY_MISMATCH" (IRPrint.ppIRType host.Type)
        if role = "partial" then
            match host.Value with
            | IRLet (tid, IRTranspose (IRVar (mid, _), 0, 1), IRCompute (IRApplyCombinator consumer)) when mid = sourceBinding.Id ->
                match consumer.Arrays, consumer.Kernel with
                | [IRVar (p, privateType)], IRVar (rowId, _) when p = tid && consumer.KernelInputRanks = [1] ->
                    match m.Functions |> List.tryFind (fun f -> f.Id = rowId) with
                    | Some row when row.Params.Length = 1 ->
                        match row.Body with
                        | IRReduce (IRVar (rid, rty), fold, Some seed) when rid = row.Params.Head.VarId && rty = row.Params.Head.Type ->
                            printfn "PARTIAL_TOPOLOGY_MATCH host=%d private=%d source=%d rowCallable=%d rowParam=%d" host.Id tid mid row.Id rid
                            printfn "PRIVATE_TYPE %A\nROW_TYPE %A\nFOLD %A\nSEED %A\nCONSUMER %A" privateType rty fold seed consumer
                        | _ -> fail "ROW_BODY_MISMATCH" (IRPrint.ppIRExpr 0 row.Body)
                    | _ -> fail "ROW_CALLABLE_MISMATCH" "missing or non-unary row kernel"
                | _ -> fail "CONSUMER_MISMATCH" (sprintf "%A" consumer)
            | _ -> fail "PARTIAL_TOPOLOGY_MISMATCH" (sprintf "%A" host.Value)
        else
            printfn "CONTROL_RECORDED role=%s host=%d; inspect full payload, no optimization claim" role host.Id
