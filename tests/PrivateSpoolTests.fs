// Focused options-only gate, also called by standalone Zarr. No CLI policy.
module Blade.Tests.PrivateSpoolTests

open System
open System.IO
open Blade.Types
open Blade.IR
open Blade.CodeGenState
open Blade.ZarrProvider
open Blade.Tests.TestHarness

let runPrivateSpoolTests (workDirectory: string) : int =
    printHeader "Private statistical-barrier spool (D08)"
    let mutable passed = 0
    let mutable failed = 0
    let mutable skipped = 0
    let check (label: string) (ok: bool) (detail: string) =
        if ok then
            passed <- passed + 1
            printfn "  PASS: %s" label
        else
            failed <- failed + 1
            printfn "  FAIL: %s -- %s" label detail
    let keys = ["BLADE_FP_REPRO";"BLADE_FP_REASSOC";"BLADE_FP_CONTRACT";"BLADE_PRINT";"BLADE_TILE_CACHE";"BLADE_LLVM";"BLADE_BLAS"]
    let saved = keys |> List.map (fun k -> k, Environment.GetEnvironmentVariable k)
    let savedTiming = splitTimingModeEnabled ()
    let savedTimingTarget = splitTimingOnlyBinding ()
    let directory = Path.GetFullPath workDirectory
    if Directory.Exists directory || File.Exists directory then invalidArg "workDirectory" "D08 requires a new private directory; retained evidence is never overwritten/deleted"
    Directory.CreateDirectory directory |> ignore
    let ownedRoot = Path.Combine(directory, "owned")
    Directory.CreateDirectory ownedRoot |> ignore
    let policy : PrivateSpoolPolicy =
        { Root=ownedRoot; TrackedWorkspaceBytes=1048576UL; LogicalDiskBytes=1048576UL; BlockCells=2UL }
    let source (store: string) =
        String.concat "\n"
            [ "import zarr as z";
              sprintf "let s = z.load(\"%s\")" (store.Replace('\\','/'));
              "let A = s.vars.A |> z.stream";
              "let p = (method_for(A) <@> lambda(x) -> x * x + 1.0) |> compute";
              "let t = reduce(p, (+), 5.0)";
              "let m = t / Float64(extents(p))";
              "let result = reduce(method_for(p) <@> lambda(v) -> (v - m) * (v - m), (+), 3.0)" ]
    let fixture (label: string) (values: float array) (fill: ZarrFill) (omit: int64 list list) =
        let store = Path.Combine(directory, "input_" + label)
        let vars : ZarrWrite.WriteVar list =
            [ {
                Name = "A"
                DimNames = Some ["x"]
                Shape = [int64 values.Length]
                Chunks = [2L]
                FillValue = fill
                Data = ZarrWrite.WF64 values
                OmitChunks = omit
                Blade = None
              } ]
        ZarrWrite.writeStoreV2 store vars
        store
    let lower (text: string) =
        match Blade.Lowering.lower text with
        | Ok ir -> ir
        | Error e -> failwith ("D08 fixture did not lower: " + e)
    let emit (p: PrivateSpoolPolicy) (ir: IRProgram) =
        Blade.CodeGen.genSelfContainedProgramFromIRWithOptions {PrivateSpool=Some p} ir "D08_private_spool" |> fst
    let marker = "// private-statistic-spool-v1:"
    let resultLine (output: string) = output.Split('\n') |> Array.tryFind (fun l -> l.Trim().StartsWith("result = ")) |> Option.map (fun value -> value.Trim())
    let rootEmpty () = Directory.GetFileSystemEntries(ownedRoot).Length = 0
    let run (label: string) (cpp: string) =
        let file = Path.Combine(directory, label + ".cpp")
        Blade.CodeGen.deployRuntimeHeaders directory
        File.WriteAllText(file, cpp)
        match Blade.Build.compileCpp file directory with
        | Error e ->
            File.WriteAllText(Path.Combine(directory,label + ".compile-error.log"),e)
            Error e
        | Ok exe ->
            File.WriteAllText(Path.Combine(directory,label + ".compile-result.txt"),"ok " + exe)
            let verdict = Blade.Build.runExecutable exe
            match verdict with
            | Ok (code,output) ->
                File.WriteAllText(Path.Combine(directory,label + ".stdout-stderr.log"),output)
                File.WriteAllText(Path.Combine(directory,label + ".exit.txt"),string code)
            | Error e -> File.WriteAllText(Path.Combine(directory,label + ".run-error.log"),e)
            verdict
    let inject (setup: string) (cpp: string) =
        "#define BLADE_PRIVATE_SPOOL_TESTING\n" + cpp.Replace(marker, setup + "\n" + marker)
    let nativeCheck (label: string) (cpp: string) (judge: int -> string -> bool) =
        if not (OperatingSystem.IsWindows()) || not Blade.Build.capabilities.Value.HasGpp then
            skipped <- skipped + 1
            printfn "  SKIP: %s -- requires native Windows and g++" label
        else
            match run label cpp with
            | Error e ->
                check (label + ": compile/run") false e
                failwith "D08 stopped at the first unexpected native build/run failure"
            | Ok (code, output) ->
                let ok = judge code output
                check label ok output
                if not ok then failwith "D08 stopped at the first unexpected native verdict"
                let retired = rootEmpty ()
                check (label + ": owned scratch retired on normal/exit/panic path") retired ownedRoot
                if not retired then failwith "D08 stopped with retained ownership evidence; do not start another native case"
    let counters (n: int) (cpp: string) =
        let payload = n * 8
        let blocks = n / 2 + (if n % 2 = 0 then 0 else 1)
        let disk = 72 + payload + 64 * blocks + 64 + 128
        let assertion =
            sprintf "const auto __test_stats = ps::counters(); if (__test_stats.payload_written != %dULL || __test_stats.payload_read != %dULL || __test_stats.logical_disk_high_water != %dULL || ps::reserved_disk() != 0) { std::cerr << \"D08_COUNTER_FAILURE\"; std::exit(93); }\n" payload payload disk
        let report =
            "std::cerr << \"D08_COUNTERS payload_written=\" << __test_stats.payload_written << \" payload_read=\" << __test_stats.payload_read << \" logical_disk_high_water=\" << __test_stats.logical_disk_high_water << \" aggregate_tracked_capacity=\" << (__ps_extra + __ps_plan.workspace_bytes) << \" reserved_disk=\" << ps::reserved_disk() << std::endl;\n"
        cpp.Replace("return __ps.pending;", assertion + report + "return __ps.pending;")
    let rejects (label: string) (ir: IRProgram) =
        let result = Blade.OptimizePrivateSpool.tryPlan policy (Some (Set.singleton "result")) ir
        check label (Result.isError result) (sprintf "%A" result)
    try
        try
            Environment.SetEnvironmentVariable("BLADE_FP_REPRO", "1")
            Environment.SetEnvironmentVariable("BLADE_FP_REASSOC", null)
            Environment.SetEnvironmentVariable("BLADE_FP_CONTRACT", "off")
            Environment.SetEnvironmentVariable("BLADE_PRINT", "result")
            Environment.SetEnvironmentVariable("BLADE_TILE_CACHE", null)
            Environment.SetEnvironmentVariable("BLADE_LLVM", "off")
            Environment.SetEnvironmentVariable("BLADE_BLAS", "off")
            setSplitTimingMode false
            setSplitTimingOnlyBinding None
            if not (OperatingSystem.IsWindows()) then
                skipped <- skipped + 1
                printfn "  SKIP: options policy requires this gate's Windows local ASCII root"
            else
                let good = fixture "n5" [|1.0;2.0;3.0;4.0;5.0|] (FillFloat 0.0) []
                let ir = lower (source good)
                let ordinary = Blade.CodeGen.genSelfContainedProgramFromIR ir "D08_resident" |> fst
                let cpp = emit policy ir
                check "D08 actually replaces the resident p pool and static run table"
                    (ordinary.Contains "Array<double, 1> p =" && ordinary.Contains "p__runs["
                     && cpp.Contains marker && not (cpp.Contains "Array<double, 1> p =")
                     && not (cpp.Contains "p_extents[") && not (cpp.Contains "p__runs[")
                     && not (cpp.Contains "_wbufv(") && cpp.Contains "retained private-spool decode workspace") ""
                if not (cpp.Contains marker) then failwith "D08 expected route was declined; stop before native cases"
                check "D08 complete protocol and capped source cursor emit before atomic scalar publication"
                    (cpp.Contains "__ps.writer.append" && cpp.Contains "__ps.writer.seal" && cpp.Contains "__ps.reader.next"
                     && cpp.Contains "__ps.reader.finish" && cpp.Contains "__ps_next = __ps_hi"
                     && cpp.IndexOf("aggregate workspace") < cpp.IndexOf("new (std::nothrow) double[")
                     && cpp.IndexOf("__ps.reader.finish") < cpp.IndexOf("return __ps.pending;")) ""
                // Exactly one normal window assembly site; this is a static
                // route check, not a claim about dynamic chunk open counts.
                let cbufDecls = cpp.Split('\n') |> Array.filter (fun l -> l.Contains "double* A_cbuf =")
                check "D08 no second provider traversal and normal per-window missing scan"
                    (cbufDecls.Length = 1 && cpp.Contains "Original normal window assembly/validation"
                     && not (cpp.Contains "rank1-zarr-joined-scalars")) (string cbufDecls.Length)
                nativeCheck "D08_n5" (counters 5 cpp) (fun code output -> code = 0 && resultLine output = Some "result = 382.0")
                nativeCheck "D08_resident_reference" ordinary (fun code output -> code = 0 && resultLine output = Some "result = 382.0")
                for label, values, omit, expected in
                    [ ("n1", [|1.0|], [], "result = 28.0");
                      ("n0", [||], [], "result = 3.0");
                      ("finite_fill", [|1.0;2.0;3.0;4.0;5.0|], [[2L]], "result = 182.0") ] do
                    let store = fixture label values (FillFloat 0.0) omit
                    let caseIr = lower (source store)
                    let generated = emit policy caseIr
                    check ("D08 " + label + ": route admitted") (generated.Contains marker) ""
                    nativeCheck ("D08_" + label) (counters values.Length generated)
                        (fun code output -> code = 0 && resultLine output = Some expected)
                let overflowStore = fixture "computed_inf" [|1e200;1e200|] (FillFloat 0.0) []
                let overflowIr = lower (source overflowStore)
                let overflowCpp = emit policy overflowIr
                check "D08 finite input/overflowing producer still uses the spool route" (overflowCpp.Contains marker) ""
                let checkedBits =
                    overflowCpp.Replace("const double __ps_term =",
                        "uint64_t __test_replay_bits = 0; std::memcpy(&__test_replay_bits, &__ps.window[__ps_i], sizeof __test_replay_bits); if (__test_replay_bits != 0x7ff0000000000000ULL) { std::cerr << \"D08_REPLAY_BITS_FAILURE\"; std::exit(94); } const double __ps_term =")
                nativeCheck "D08_computed_inf_replays_bits" (counters 2 checkedBits)
                    (fun code output ->
                        code=0 && not (output.Contains "BL8015")
                        && (resultLine output |> Option.exists (fun line ->
                            match Double.TryParse(line.Substring("result = ".Length), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
                            | true, value -> Double.IsNaN value
                            | _ -> false)))
                // Re-lower before retained-program proof mutations, restoring
                // the optimization tables belonging to this exact graph.
                let ir = lower (source good)
                let modul = ir.Modules.Head
                let producer = modul.Bindings |> List.find (fun b -> b.Name = "p")
                let barrier = modul.Bindings |> List.find (fun b -> b.Name = "m")
                let terminal = modul.Bindings |> List.find (fun b -> b.Name = "result")
                let plan =
                    match Blade.OptimizePrivateSpool.tryPlan policy (Some (Set.singleton "result")) ir with
                    | Ok p -> p
                    | Error e -> failwith ("D08 expected certificate: " + e)
                check "D08 observed mutable m capture flag is accepted only with no actual assignments"
                    (plan.TransformKernel.Captures.Head.IsMutable) ""
                let replaceProducer (value: IRExpr) : IRProgram =
                    { Modules=[{modul with Bindings=modul.Bindings |> List.map (fun b -> if b.Id=producer.Id then {b with Value=value} else b)}] }
                let pi = match producer.Value with IRCompute (IRApplyCombinator i) -> i | _ -> failwith "producer topology changed"
                let loop = match pi.Loop with IRMethodFor l -> l | _ -> failwith "method_for topology changed"
                let ri, replayFold, replaySeed =
                    match terminal.Value with
                    | IRReduceCompute (IRApplyCombinator info, fold, seed) -> info, fold, seed
                    | _ -> failwith "terminal topology changed"
                let sourceAt = plan.SourceSpec.VarType
                let producerAt = match producer.Type with ArrayElem at -> at | _ -> failwith "producer type changed"
                let replayAt = match ri.OutputType with ArrayElem at -> at | _ -> failwith "replay output type changed"
                check "D08 actual maps preserve separate source/producer/replay axis roles with fresh output IDs"
                    (producer.Type = pi.OutputType
                     && producerAt.IndexTypes.Head.Id <> sourceAt.IndexTypes.Head.Id
                     && replayAt.IndexTypes.Head.Id <> producerAt.IndexTypes.Head.Id
                     && producerAt.IndexTypes.Head = {sourceAt.IndexTypes.Head with Id=producerAt.IndexTypes.Head.Id}
                     && replayAt.IndexTypes.Head = {producerAt.IndexTypes.Head with Id=replayAt.IndexTypes.Head.Id}
                     && producerAt.Identity.IsNone && replayAt.Identity.IsNone) ""
                let replaceReplay (info: ApplyInfo) : IRProgram =
                    let value = IRReduceCompute (IRApplyCombinator info, replayFold, replaySeed)
                    let changedModule : IRModule =
                        { modul with
                            Bindings = modul.Bindings |> List.map (fun b -> if b.Id=terminal.Id then {b with Value=value} else b) }
                    { Modules = [changedModule] }
                let changedInput (info: ApplyInfo) (at: IRArrayType) : ApplyInfo =
                    let id = match info.Arrays with [IRVar (id,_)] -> id | _ -> failwith "unary input topology changed"
                    let arrays = [IRVar(id,mkArrayLike at)]
                    let oldLoop = match info.Loop with IRMethodFor value -> value | _ -> failwith "unary loop topology changed"
                    { info with
                        Arrays = arrays
                        ArrayTypes = [at]
                        Loop = IRMethodFor {oldLoop with Arrays=arrays;ArrayTypes=[at]} }
                let forgedSourceAt = {sourceAt with IndexTypes=[{sourceAt.IndexTypes.Head with Id=900009}]}
                rejects "D08 same-extent forged source axis cannot replace exact source binding provenance"
                    (replaceProducer (IRCompute(IRApplyCombinator (changedInput pi forgedSourceAt))))
                let forgedProducerType = mkArrayLike {producerAt with IndexTypes=[{producerAt.IndexTypes.Head with Id=900010}]}
                rejects "D08 producer binding must equal its own map output type"
                    (replaceProducer (IRCompute(IRApplyCombinator {pi with OutputType=forgedProducerType})))
                let forgedReplayInput = {producerAt with IndexTypes=[{producerAt.IndexTypes.Head with Id=900011}]}
                rejects "D08 same-extent replay input cannot substitute another producer axis"
                    (replaceReplay (changedInput ri forgedReplayInput))
                let forgedReplayOutput = mkArrayLike {replayAt with IndexTypes=[{replayAt.IndexTypes.Head with Tag=Some "forged_output"}]}
                rejects "D08 replay output may refresh only its input index ID, not its tag"
                    (replaceReplay {ri with OutputType=forgedReplayOutput})
                let namedReplayOutput = mkArrayLike {replayAt with Identity=Some (AIDVariable producer.Name)}
                rejects "D08 fresh replay output cannot inherit an input value identity"
                    (replaceReplay {ri with OutputType=namedReplayOutput})
                let forgedIdentities = [AIDVariable "other_source"]
                let forgedIdentityInfo : ApplyInfo =
                    { pi with
                        Identities = forgedIdentities
                        Loop = IRMethodFor {loop with Identities=forgedIdentities} }
                rejects "D08 coherent loop/apply identities must still name their actual source binding"
                    (replaceProducer (IRCompute(IRApplyCombinator forgedIdentityInfo)))
                let hiddenLoop = {pi with Loop=IRMethodFor {loop with Arrays=loop.Arrays @ [IRVar(producer.Id,producer.Type)]}}
                rejects "D08 hidden map-loop operand cannot disappear" (replaceProducer (IRCompute(IRApplyCombinator hiddenLoop)))
                rejects "D08 altered map metadata refuses" (replaceProducer (IRCompute(IRApplyCombinator {pi with ReynoldsSpeedup=2L})))
                let badArrow = IRVar(plan.ProducerKernel.Id, mkFuncArrow [IRTScalar ETInt64] (IRTScalar ETFloat64))
                rejects "D08 callable-reference arrow must match its resolved scalar signature"
                    (replaceProducer (IRCompute(IRApplyCombinator {pi with Kernel=badArrow})))
                let badKernel : IRCallable =
                    {plan.ProducerKernel with Body=mapIRExpr (function IRVar(id,_) -> IRVar(id,IRTScalar ETFloat32) | e -> e) plan.ProducerKernel.Body}
                rejects "D08 scalar variable payload type is certified"
                    {Modules=[{modul with Functions=modul.Functions |> List.map (fun f -> if f.Id=badKernel.Id then badKernel else f)}]}
                let unitType = IRTUnitAnnotated(IRTScalar ETFloat64, unitOfDims (Map.ofList ["meters",1]))
                let unitKernel : IRCallable =
                    { plan.ProducerKernel with
                        Params = [{plan.ProducerKernel.Params.Head with Type=unitType}]
                        Body = mapIRExpr (function IRVar(id,_) -> IRVar(id,unitType) | e -> e) plan.ProducerKernel.Body }
                let unitInfo = {pi with Kernel=IRVar(unitKernel.Id,mkFuncArrow [unitType] (IRTScalar ETFloat64))}
                let unitModule : IRModule =
                    { modul with
                        Functions = modul.Functions |> List.map (fun f -> if f.Id=unitKernel.Id then unitKernel else f)
                        Bindings = modul.Bindings |> List.map (fun b -> if b.Id=producer.Id then {b with Value=IRCompute(IRApplyCombinator unitInfo)} else b) }
                rejects "D08 unit-bearing F64 cannot be stripped into this unitless route" {Modules=[unitModule]}
                let badIndexType =
                    match producer.Type with
                    | ArrayElem at -> mkArrayLike {at with IndexTypes=[{at.IndexTypes.Head with Id=900001;Tag=None}]}
                    | _ -> failwith "producer array topology changed"
                let changedBarrier =
                    {barrier with Value=mapIRExpr (function IRVar(id,_) when id=producer.Id -> IRVar(id,badIndexType) | e -> e) barrier.Value}
                rejects "D08 barrier extent retains exact index provenance"
                    {Modules=[{modul with Bindings=modul.Bindings |> List.map (fun b -> if b.Id=barrier.Id then changedBarrier else b)}]}
                let alias : IRBinding = {producer with Id=900002;Name="escape";Value=IRVar(producer.Id,producer.Type)}
                rejects "D08 private array alias escape refuses" {Modules=[{modul with Bindings=modul.Bindings @ [alias]}]}
                let terminalAlias : IRBinding = {terminal with Id=900005;Name="terminal_escape";Value=IRVar(terminal.Id,terminal.Type)}
                rejects "D08 terminal demand beyond final print refuses hoist/lifetime changes"
                    {Modules=[{modul with Bindings=modul.Bindings @ [terminalAlias]}]}
                let terminalFunction : IRCallable =
                    { plan.TransformKernel with
                        Id = 900006
                        Name = "terminal_capture"
                        SourceName = "terminal_capture"
                        Params = []
                        Body = IRVar(terminal.Id,terminal.Type)
                        Captures = [{plan.TransformKernel.Captures.Head with Id=terminal.Id;Name=terminal.Name;Type=terminal.Type}] }
                rejects "D08 namespace callable terminal capture cannot lose its hoisted declaration"
                    {Modules=[{modul with Functions=modul.Functions @ [terminalFunction]}]}
                let assignment : IRBinding =
                    {Id=900003;Name="assignment";Type=IRTUnit;Value=IRAssign(IRVar(barrier.Id,barrier.Type),IRLit(IRLitFloat 0.0));IsConst=false;IsMutable=false}
                rejects "D08 actual assignment differs from a mutable capture flag"
                    {Modules=[{modul with Bindings=modul.Bindings @ [assignment]}]}
                let ix = plan.SourceSpec.VarType.IndexTypes.Head
                rejects "D08 declared type extent demand is included"
                    {Modules=[{modul with Types=modul.Types @ [IRTDIndexType("Hidden",{ix with Extent=IRExtent(IRVar(producer.Id,producer.Type),0)})]}]}
                rejects "D08 hidden initialization payload refuses"
                    {Modules=[{modul with RandomInits=Map.add terminal.Id (FillModulus(IRVar(producer.Id,producer.Type))) modul.RandomInits}]}
                rejects "D08 foreign-module demand cannot be hidden by module assembly"
                    {Modules=[modul;{modul with Name="Foreign";Bindings=[alias];Functions=[];ProviderReads=Map.empty}]}
                PoolReuseTable.recordReturn 900004 producer.Id
                rejects "D08 return-donor VALUE into the region is a storage conflict" ir
                PoolReuseTable.reset ()
                Blade.Optimize.MemberHoistTable.record {
                    HostId = 900008
                    HostName = "outside"
                    SetId = producer.Id
                    SetName = producer.Name
                    Kernels = Set.empty
                    Forced = 1
                    Admission = "test-only owner conflict"
                }
                rejects "D08 foreign membership owner cannot borrow the private producer" ir
                Blade.Optimize.MemberHoistTable.reset ()
                let changedModule = {modul with Bindings=modul.Bindings |> List.map (fun b -> if b.Id=barrier.Id then changedBarrier else b)}
                check "D08 stale certificate cannot be applied to rebuilt bindings"
                    (not (Blade.OptimizePrivateSpool.stillValid plan changedModule)) ""
                let changedKernelModule = {modul with Functions=modul.Functions |> List.map (fun f -> if f.Id=badKernel.Id then badKernel else f)}
                check "D08 stale certificate cannot be applied to changed kernel objects"
                    (not (Blade.OptimizePrivateSpool.stillValid plan changedKernelModule)) ""
                let staleCtx = {emptyContext () with StreamTerminalModule=Some changedModule}
                check "D08 stale certificate fails before any renderer/omission"
                    (Blade.CodeGenPrivateSpool.tryGenerate staleCtx plan |> Result.isError) ""
                check "D08 actual emitted binding sequence must preserve dominance and region adjacency"
                    (Blade.OptimizePrivateSpool.emissionOrderMatches plan modul.Bindings
                     && not (Blade.OptimizePrivateSpool.emissionOrderMatches plan (List.rev modul.Bindings))) ""
                let reorderedIr : IRProgram =
                    {Modules=[{modul with DerivedFuncOrigins=Map.add plan.Statistic.Id 900007 modul.DerivedFuncOrigins}]}
                let reorderedCpp = emit policy reorderedIr
                check "D08 derived-origin emission reordering cannot inherit omission authority"
                    (not (reorderedCpp.Contains marker) && reorderedCpp.Contains "Array<double, 1> p =") ""
                for selection in [null;"p,result";"t,result";"m,result"] do
                    Environment.SetEnvironmentVariable("BLADE_PRINT", selection)
                    let kept = emit policy ir
                    check "D08 demanded intermediate/default print retains resident p"
                        (not (kept.Contains marker) && kept.Contains "Array<double, 1> p =") (string selection)
                Environment.SetEnvironmentVariable("BLADE_PRINT", "result")
                for key, value, restored in
                    [ ("BLADE_FP_CONTRACT","fast","off"); ("BLADE_FP_REPRO","0","1") ] do
                    Environment.SetEnvironmentVariable(key,value)
                    let kept = emit policy ir
                    check "D08 nonrepro/contraction-fast keeps original materialization"
                        (not (kept.Contains marker) && kept.Contains "Array<double, 1> p =") key
                    Environment.SetEnvironmentVariable(key,restored)
                let mismatch = emit {policy with BlockCells=3UL} ir
                check "D08 chunk-policy mismatch preflight keeps all four bindings"
                    (not (mismatch.Contains marker) && mismatch.Contains "Array<double, 1> p =" && mismatch.Contains "double m =") ""
                let defaultAfter = Blade.CodeGen.genSelfContainedProgramFromIR ir "D08_none_after" |> fst
                check "D08 None after policy emission has no stale authorization"
                    (not (defaultAfter.Contains marker) && defaultAfter.Contains "Array<double, 1> p =") ""
                let _, low = Blade.CodeGen.genModule modul (IRBuilder())
                let _, setup, compute = Blade.CodeGen.genModuleSplit modul (IRBuilder())
                check "D08 generic module and split APIs have no policy capability"
                    (not ((String.concat "\n" (low @ setup @ compute)).Contains marker)) ""
                try
                    setSplitTimingMode true
                    setSplitTimingOnlyBinding (Some "result")
                    let splitCpp = emit policy ir
                    check "D08 explicit split target=result keeps resident timing placement"
                        (not (splitCpp.Contains marker) && splitCpp.Contains "Array<double, 1> p =" && splitCpp.Contains "double m =") ""
                finally
                    setSplitTimingMode false
                    setSplitTimingOnlyBinding None
                try
                    emit {policy with Root="relative"} ir |> ignore
                    check "D08 malformed policy refuses explicitly" false "unexpected success"
                with :? ArgumentException -> check "D08 malformed policy refuses explicitly" true ""
                let failureCases : (string * float array * ZarrFill * int64 list list * bool * string) list =
                    [ ("append_then_short", [|1.0;2.0;3.0;4.0;5.0|], FillFloat 0.0, [], true, "is short");
                      ("append_then_null", [|1.0;2.0;3.0;4.0;5.0|], FillNone, [[1L]], false, "fill_value is null");
                      ("append_then_missing", [|1.0;2.0;nan;4.0;5.0|], FillFloat 0.0, [], false, "BL8015");
                      ("earlier_missing_before_short", [|nan;2.0;3.0;4.0;5.0|], FillFloat 0.0, [], true, "BL8015") ]
                for label, values, fill, omit, short, expected in failureCases do
                    let store = fixture label values fill omit
                    let caseIr = lower (source store)
                    let generated = emit policy caseIr
                    check ("D08 " + label + ": route admitted before fault") (generated.Contains marker) ""
                    if short then File.WriteAllBytes(Path.Combine(store,"A","1"),[|0uy|])
                    let faulted = inject "blade_private_spool::testing::faults.write_error_at = 144;" generated
                    nativeCheck ("D08_" + label) faulted
                        (fun code output -> code<>0 && output.Contains expected && not (output.Contains "BL8016")
                                            && resultLine output=None
                                            && (label<>"append_then_missing" || output.Contains "first at [2]")
                                            && (label<>"earlier_missing_before_short" || output.Contains "first at [0]")
                                            && (label<>"earlier_missing_before_short" || not (output.Contains "is short")))
                let ir = lower (source good)
                let cpp = emit policy ir
                let protocolFailures : (string * string * string) list =
                    [ ("append", "write_error_at = 144", "append after original source drain");
                      ("seal", "flush = true", "seal");
                      ("footer", "read_error_at = 304", "replay validation");
                      ("late_close", "lock_close_after_success = true", "finish before publication") ]
                for label, fault, stage in protocolFailures do
                    let faulted = inject ("blade_private_spool::testing::faults." + fault + ";") cpp
                    nativeCheck ("D08_protocol_" + label) faulted
                        (fun code output -> code<>0 && output.Contains "BL8016" && output.Contains stage && resultLine output=None
                                            && output.Contains "cleanup_attempted=1")
                for label, limited in
                    [ ("aggregate_workspace", {policy with TrackedWorkspaceBytes=64UL});
                      ("serialized_disk", {policy with LogicalDiskBytes=495UL}) ] do
                    nativeCheck ("D08_quota_" + label) (emit limited ir)
                        (fun code output -> code<>0 && output.Contains "BL8016" && resultLine output=None
                                            && output.Contains "cleanup_attempted=0")
        with ex ->
            if failed = 0 then check "D08 focused gate completes" false (ex.ToString())
            else printfn "  D08 stopped: %s" (ex.ToString())
    finally
        PoolReuseTable.reset ()
        Blade.Optimize.MemberHoistTable.reset ()
        setSplitTimingMode savedTiming
        setSplitTimingOnlyBinding savedTimingTarget
        for key, value in saved do Environment.SetEnvironmentVariable(key,value)
    printFooter "Private statistical-barrier spool" [$"{passed} passed";$"{failed} failed";$"{skipped} skipped"]
    if failed=0 then 0 else 1
