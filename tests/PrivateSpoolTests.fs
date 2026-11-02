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
        { Root=ownedRoot; TrackedWorkspaceBytes=1048576UL; LogicalDiskBytes=1048576UL; BlockCells=2UL; ResidentBytes=0UL }
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
                    { ir with Modules = [{modul with Bindings=modul.Bindings |> List.map (fun b -> if b.Id=producer.Id then {b with Value=value} else b)}] }
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
                    { ir with Modules = [changedModule] }
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
                    { ir with Modules = [{modul with Functions=modul.Functions |> List.map (fun f -> if f.Id=badKernel.Id then badKernel else f)}]}
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
                rejects "D08 unit-bearing F64 cannot be stripped into this unitless route" { ir with Modules = [unitModule]}
                let badIndexType =
                    match producer.Type with
                    | ArrayElem at -> mkArrayLike {at with IndexTypes=[{at.IndexTypes.Head with Id=900001;Tag=None}]}
                    | _ -> failwith "producer array topology changed"
                let changedBarrier =
                    {barrier with Value=mapIRExpr (function IRVar(id,_) when id=producer.Id -> IRVar(id,badIndexType) | e -> e) barrier.Value}
                rejects "D08 barrier extent retains exact index provenance"
                    { ir with Modules = [{modul with Bindings=modul.Bindings |> List.map (fun b -> if b.Id=barrier.Id then changedBarrier else b)}]}
                let alias : IRBinding = {producer with Id=900002;Name="escape";Value=IRVar(producer.Id,producer.Type)}
                rejects "D08 private array alias escape refuses" { ir with Modules = [{modul with Bindings=modul.Bindings @ [alias]}]}
                let terminalAlias : IRBinding = {terminal with Id=900005;Name="terminal_escape";Value=IRVar(terminal.Id,terminal.Type)}
                rejects "D08 terminal demand beyond final print refuses hoist/lifetime changes"
                    { ir with Modules = [{modul with Bindings=modul.Bindings @ [terminalAlias]}]}
                let terminalFunction : IRCallable =
                    { plan.TransformKernel with
                        Id = 900006
                        Name = "terminal_capture"
                        SourceName = "terminal_capture"
                        Params = []
                        Body = IRVar(terminal.Id,terminal.Type)
                        Captures = [{plan.TransformKernel.Captures.Head with Id=terminal.Id;Name=terminal.Name;Type=terminal.Type}] }
                rejects "D08 namespace callable terminal capture cannot lose its hoisted declaration"
                    { ir with Modules = [{modul with Functions=modul.Functions @ [terminalFunction]}]}
                let assignment : IRBinding =
                    {Id=900003;Name="assignment";Type=IRTUnit;Value=IRAssign(IRVar(barrier.Id,barrier.Type),IRLit(IRLitFloat 0.0));IsConst=false;IsMutable=false}
                rejects "D08 actual assignment differs from a mutable capture flag"
                    { ir with Modules = [{modul with Bindings=modul.Bindings @ [assignment]}]}
                let ix = plan.SourceSpec.VarType.IndexTypes.Head
                rejects "D08 declared type extent demand is included"
                    { ir with Modules = [{modul with Types=modul.Types @ [IRTDIndexType("Hidden",{ix with Extent=IRExtent(IRVar(producer.Id,producer.Type),0)})]}]}
                rejects "D08 hidden initialization payload refuses"
                    { ir with Modules = [{modul with RandomInits=Map.add terminal.Id (FillModulus(IRVar(producer.Id,producer.Type))) modul.RandomInits}]}
                rejects "D08 foreign-module demand cannot be hidden by module assembly"
                    { ir with Modules = [modul;{modul with Name="Foreign";Bindings=[alias];Functions=[];ProviderReads=Map.empty}]}
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
                    { ir with Modules = [{modul with DerivedFuncOrigins=Map.add plan.Statistic.Id 900007 modul.DerivedFuncOrigins}]}
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

                // ---- E2 (round 4 wave 3): the declared grant, the selection, the run record ----
                let parse = Blade.CodeGenPrivateSpool.parseKnob
                for raw in [ null; ""; "  "; "0"; "off"; "OFF"; "false" ] do
                    check (sprintf "E2 knob %A grants nothing" raw) (parse raw = Ok None) (sprintf "%A" (parse raw))
                let rootText = ownedRoot
                (match parse (sprintf "root=%s,mem=1M,disk=2K" rootText) with
                 | Ok (Some p) ->
                    check "E2 knob parses root/mem/disk; block = the chunk edge (0); resident defaults to mem"
                        (p.Root = rootText && p.TrackedWorkspaceBytes = 1048576UL && p.LogicalDiskBytes = 2048UL
                         && p.BlockCells = 0UL && p.ResidentBytes = 1048576UL) (sprintf "%A" p)
                 | other -> check "E2 knob parses a valid grant" false (sprintf "%A" other))
                check "E2 knob resident=0 = never retain"
                    (match parse (sprintf " root=%s , mem=4096 , disk=1g , resident=0 " rootText) with
                     | Ok (Some p) -> p.ResidentBytes = 0UL && p.LogicalDiskBytes = 1073741824UL && p.TrackedWorkspaceBytes = 4096UL
                     | _ -> false) ""
                for bad in [ "on"; "1"; "root=relative,mem=1M,disk=1M"; sprintf "root=%s,mem=1M" rootText
                             "mem=1M,disk=1M"; sprintf "root=%s,mem=1Q,disk=1M" rootText
                             sprintf "root=%s,mem=0,disk=1M" rootText; sprintf "root=%s,mem=1M,disk=1M,bogus=1" rootText
                             sprintf "root=%s,mem=1M,disk=1M,mem=2M" rootText; sprintf "root=%s,mem=-1,disk=1M" rootText
                             sprintf "root=%s,mem=99999999999999999999G,disk=1M" rootText ] do
                    check (sprintf "E2 malformed knob %A is an error, never a silent resident grant" bad)
                        (Result.isError (parse bad)) (sprintf "%A" (parse bad))
                // Every value the parser accepts passes the API's validity
                // predicate (one predicate: nothing accepted can throw later).
                (match parse (sprintf "root=%s,mem=1M,disk=1M" rootText) with
                 | Ok (Some p) ->
                    check "E2 an accepted grant never throws in validatePolicy"
                        (try Blade.CodeGenPrivateSpool.validatePolicy p; true with _ -> false) ""
                 | _ -> check "E2 an accepted grant never throws in validatePolicy" false "parse failed")
                let savedKnob = Environment.GetEnvironmentVariable "BLADE_PRIVATE_SPOOL"
                let savedRecord = Environment.GetEnvironmentVariable "BLADE_RUN_RECORD"
                try
                    Environment.SetEnvironmentVariable("BLADE_PRIVATE_SPOOL", null)
                    check "E2 knob unset: knobPolicy grants nothing" (Blade.CodeGenPrivateSpool.knobPolicy () = Ok None) ""
                    let decided (f: unit -> string) =
                        Blade.Effects.Decisions.start ()
                        let text = f ()
                        let ds = Blade.Effects.Decisions.drain () |> List.filter (fun d -> d.Rule = "private-statistic-spool")
                        text, ds
                    let ir = lower (source good)
                    // RETAIN: the resident intermediate (40 bytes) is within the
                    // resident allowance -> the resident two-pass emission, recorded.
                    let retained, rds = decided (fun () -> emit { policy with BlockCells=0UL; ResidentBytes=1048576UL } ir)
                    check "E2 selection: within the resident allowance the resident route runs (no spool, p stored)"
                        (not (retained.Contains marker) && retained.Contains "Array<double, 1> p =") ""
                    check "E2 selection: the retain decision is recorded with the three candidates"
                        (rds |> List.exists (fun d ->
                            match d.Outcome with
                            | Blade.Effects.Declined why ->
                                why.StartsWith "retain:"
                                && d.Evidence |> List.exists (fun e -> e.StartsWith "replay: not proved")
                                && d.Evidence |> List.exists (fun e -> e.StartsWith "spool: D06")
                            | _ -> false)) (sprintf "%A" rds)
                    // SPOOL through the knob: resident=0 forces the spool at N = 5.
                    Environment.SetEnvironmentVariable("BLADE_PRIVATE_SPOOL", sprintf "root=%s,mem=1M,disk=1M,resident=0" ownedRoot)
                    let knobOptions =
                        match Blade.CodeGenPrivateSpool.knobPolicy () with
                        | Ok (Some p) -> { defaultProgramEmissionOptions with PrivateSpool = Some p }
                        | other -> failwith (sprintf "E2 knob did not parse: %A" other)
                    let spooled, sds =
                        decided (fun () -> Blade.CodeGen.genSelfContainedProgramFromIRWithOptions knobOptions ir "E2_knob" |> fst)
                    check "E2 knob grant: the spool route is taken, block resolved to the chunk edge"
                        (spooled.Contains marker && not (spooled.Contains "Array<double, 1> p =")
                         && spooled.Contains "blade_rr::note_private_spool(") ""
                    check "E2 knob grant: the applied record names the route and the candidates"
                        (sds |> List.exists (fun d ->
                            d.Outcome = Blade.Effects.Applied
                            && d.Evidence |> List.exists (fun e -> e.StartsWith "route: spool")
                            && d.Evidence |> List.exists (fun e -> e.StartsWith "replay: not proved"))) (sprintf "%A" sds)
                    let recordPath = Path.Combine(directory, "E2_knob.run.json")
                    Environment.SetEnvironmentVariable("BLADE_RUN_RECORD", recordPath)
                    nativeCheck "E2_knob_spool" spooled (fun code output -> code = 0 && resultLine output = Some "result = 382.0")
                    Environment.SetEnvironmentVariable("BLADE_RUN_RECORD", savedRecord)
                    if OperatingSystem.IsWindows() && Blade.Build.capabilities.Value.HasGpp then
                        let json = if File.Exists recordPath then File.ReadAllText recordPath else ""
                        let expected =
                            "\"private_spool\":[{\"site\":\"result\",\"route\":\"spool\",\"cells\":5,\"block_cells\":2,\"blocks\":3,"
                            + "\"payload_written\":40,\"payload_read\":40,\"logical_disk_high_water\":496,\"reserved_disk\":496,"
                            + "\"grant\":{\"root\":\"" + ownedRoot.Replace("\\", "\\\\") + "\",\"mem\":1048576,\"disk\":1048576}}]"
                        check "E2 run record: the private_spool member carries the disk high water and the grant"
                            (json.Contains expected && json.Contains "\"status\":{\"ok\":true}") json
                    // A resident run writes no private_spool member (additive, optional).
                    let residentRecord = Path.Combine(directory, "E2_resident.run.json")
                    Environment.SetEnvironmentVariable("BLADE_RUN_RECORD", residentRecord)
                    nativeCheck "E2_retain_resident" retained (fun code output -> code = 0 && resultLine output = Some "result = 382.0")
                    Environment.SetEnvironmentVariable("BLADE_RUN_RECORD", savedRecord)
                    if OperatingSystem.IsWindows() && Blade.Build.capabilities.Value.HasGpp then
                        let json = if File.Exists residentRecord then File.ReadAllText residentRecord else ""
                        check "E2 run record: a resident run carries no private_spool member"
                            (json.Length > 0 && not (json.Contains "\"private_spool\":")) json  // the member key; the program NAME is D08_private_spool
                    // --mpi on a non-MPI program would launch N spools under one grant.
                    try
                        Blade.CodeGen.setMpiEmitMode true
                        let mpiCpp, mds = decided (fun () -> Blade.CodeGen.genSelfContainedProgramFromIRWithOptions knobOptions ir "E2_mpi" |> fst)
                        check "E2 MPI emit mode declines the spool (recorded)"
                            (not (mpiCpp.Contains marker)
                             && mds |> List.exists (fun d -> match d.Outcome with Blade.Effects.Declined w -> w.Contains "MPI" | _ -> false)) (sprintf "%A" mds)
                    finally Blade.CodeGen.setMpiEmitMode false
                    // The default-options entry never sees the grant, even with the knob set.
                    let plain = Blade.CodeGen.genSelfContainedProgramFromIR ir "D08_private_spool" |> fst  // the retained emission's own program name
                    check "E2 the default-options entry ignores a set knob (only the CLI verbs thread it)"
                        (not (plain.Contains marker) && plain.Contains "Array<double, 1> p =") ""
                    check "E2 retain: the emission is byte-identical to the default (no-grant) emission"
                        (retained = plain) ""
                finally
                    Environment.SetEnvironmentVariable("BLADE_PRIVATE_SPOOL", savedKnob)
                    Environment.SetEnvironmentVariable("BLADE_RUN_RECORD", savedRecord)
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

/// THE EXTERNAL STABLE SORT (round 4 wave 4 D1, ST14, rule `external-sort
/// v1`; src/cpp/blade_external_sort.hpp): a grant-bound sort site spills runs
/// to the D06 spool and merges them with bounded fan-in. Pinned here: the
/// route is TAKEN (emission + record + run record), the output is the resident
/// sort's byte for byte (NaN keys, signed zeros, ties), the resident selection
/// under the resident allowance, and the fault paths (write / read error, a
/// corrupted block, the disk quota, the fan-in refusal) end in BL8016 with the
/// owned scratch retired.
let runExternalSortTests (workDirectory: string) : int =
    printHeader "External stable sort (ST14)"
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
    let keys = ["BLADE_PRINT";"BLADE_LLVM";"BLADE_SORT_FORM";"BLADE_RUN_RECORD"]
    let saved = keys |> List.map (fun k -> k, Environment.GetEnvironmentVariable k)
    let directory = Path.GetFullPath workDirectory
    if Directory.Exists directory || File.Exists directory then invalidArg "workDirectory" "ST14 requires a new private directory"
    Directory.CreateDirectory directory |> ignore
    let ownedRoot = Path.Combine(directory, "owned")
    Directory.CreateDirectory ownedRoot |> ignore
    let policy : PrivateSpoolPolicy =
        { Root=ownedRoot; TrackedWorkspaceBytes=16384UL; LogicalDiskBytes=4194304UL; BlockCells=0UL; ResidentBytes=0UL }
    let source (n: int) =
        String.concat "\n"
            [ sprintf "type N = Idx<%d>" n
              "let z: Float64 = 0.0"
              "let k = method_for(range<N>) <@> lambda(i) -> if i % 97 == 0 then z / z else (if i % 89 == 0 then -0.0 else Float64((i * 7919) % 1013) - 500.0) |> compute"
              "let perm = sort(range<N>, lambda(i) -> k(i))"
              "let vals = method_for(range<N>) <@> lambda(i) -> Float64((i * 31) % 211) |> compute"
              "let byval = sort(vals, lambda(x) -> -x)"
              "let fp = reduce(method_for(perm) <@> lambda(i) -> Int64(i), lambda(a, b) -> a * 31 + b, 7)" ]
    let lower (text: string) =
        match Blade.Lowering.lower text with
        | Ok ir -> ir
        | Error e -> failwith ("ST14 fixture did not lower: " + e)
    let decided (f: unit -> string) =
        Blade.Effects.Decisions.start ()
        let text = f ()
        let ds = Blade.Effects.Decisions.drain () |> List.filter (fun d -> d.Rule = "external-sort")
        text, ds
    let emit (p: PrivateSpoolPolicy) (ir: IRProgram) =
        decided (fun () -> Blade.CodeGen.genSelfContainedProgramFromIRWithOptions {PrivateSpool=Some p} ir "ST14_external_sort" |> fst)
    let marker = "// external-sort v1 (ST14): `perm`"
    let rootEmpty () = Directory.GetFileSystemEntries(ownedRoot).Length = 0
    let native = OperatingSystem.IsWindows() && Blade.Build.capabilities.Value.HasGpp
    let run (label: string) (cpp: string) =
        let file = Path.Combine(directory, label + ".cpp")
        Blade.CodeGen.deployRuntimeHeaders directory
        File.WriteAllText(file, cpp)
        match Blade.Build.compileCpp file directory with
        | Error e -> Error e
        | Ok exe -> Blade.Build.runExecutable exe
    let nativeCheck (label: string) (cpp: string) (judge: int -> string -> bool) =
        if not native then
            skipped <- skipped + 1
            printfn "  SKIP: %s -- requires native Windows and g++" label
            None
        else
            match run label cpp with
            | Error e -> check (label + ": compile/run") false e; None
            | Ok (code, output) ->
                check label (judge code output) output
                check (label + ": owned scratch retired") (rootEmpty ()) ownedRoot
                Some output
    let inject (setup: string) (cpp: string) =
        "#define BLADE_PRIVATE_SPOOL_TESTING\n" + cpp.Replace(marker, setup + "\n" + marker)
    let failedWith8016 (code: int) (output: string) = code <> 0 && output.Contains "BL8016"
    // the program's own wall-time line differs run to run
    let values (output: string) =
        output.Split('\n') |> Array.filter (fun l -> not (l.Contains " completed in ")) |> String.concat "\n"
    try
        try
            Environment.SetEnvironmentVariable("BLADE_PRINT", null)
            Environment.SetEnvironmentVariable("BLADE_LLVM", "off")
            Environment.SetEnvironmentVariable("BLADE_SORT_FORM", null)
            Environment.SetEnvironmentVariable("BLADE_RUN_RECORD", null)
            let ir = lower (source 5001)
            let resident = Blade.CodeGen.genSelfContainedProgramFromIR ir "ST14_external_sort" |> fst
            let cpp, ds = emit policy ir
            check "ST14 route: both sites take the grant-bound form"
                (cpp.Contains marker && cpp.Contains "// external-sort v1 (ST14): `byval`" && cpp.Contains "es::sort("
                 && cpp.Contains "#include \"blade_external_sort.hpp\"" && not (resident.Contains "es::sort(")) ""
            check "ST14 record: applied for perm and byval"
                (["perm"; "byval"] |> List.forall (fun s -> ds |> List.exists (fun d -> d.Subject = s && d.Outcome = Blade.Effects.Applied))) (sprintf "%A" ds)
            // the reference: the ordinary resident emission (no grant)
            let reference = nativeCheck "ST14_resident_reference" resident (fun code _ -> code = 0)
            let recordPath = Path.Combine(directory, "ST14.run.json")
            Environment.SetEnvironmentVariable("BLADE_RUN_RECORD", recordPath)
            let external = nativeCheck "ST14_external_runs" cpp (fun code _ -> code = 0)
            Environment.SetEnvironmentVariable("BLADE_RUN_RECORD", null)
            match reference, external with
            | Some r, Some e ->
                check "ST14 the external output equals the resident output byte for byte (NaN keys, signed zeros, ties)" (values r = values e) ""
                let json = if File.Exists recordPath then File.ReadAllText recordPath else ""
                check "ST14 run record: both sites spooled (route taken), status ok"
                    (json.Contains "\"site\":\"perm\"" && json.Contains "\"site\":\"byval\"" && json.Contains "\"status\":{\"ok\":true}") json
            | _ -> ()
            // the resident selection: within the resident allowance, no spool
            let residentPolicy = { policy with ResidentBytes = 1048576UL }
            let retained, _ = emit residentPolicy ir
            let retainedRecord = Path.Combine(directory, "ST14_retained.run.json")
            Environment.SetEnvironmentVariable("BLADE_RUN_RECORD", retainedRecord)
            let kept = nativeCheck "ST14_resident_selection" retained (fun code _ -> code = 0)
            Environment.SetEnvironmentVariable("BLADE_RUN_RECORD", null)
            (match reference, kept with
             | Some r, Some k ->
                check "ST14 the resident selection prints the same values" (values r = values k) ""
                let json = if File.Exists retainedRecord then File.ReadAllText retainedRecord else ""
                check "ST14 the resident selection writes no private_spool member" (json.Length > 0 && not (json.Contains "\"private_spool\":")) json
             | _ -> ())
            // fault paths: every one is BL8016 with the scratch retired
            for label, setup in
                [ "write_error", "blade_private_spool::testing::faults.write_error_at = 700;"
                  "read_error", "blade_private_spool::testing::faults.read_error_at = 900;"
                  "corrupt_block", "blade_external_sort::testing::corrupt_block = 2;"
                  "short_transfers", "blade_private_spool::testing::faults.max_transfer = 7;" ] do
                let faulted = inject (sprintf "static const bool __st14_fault = (%s true);" (setup.TrimEnd(';') + ",")) cpp
                if label = "short_transfers" then
                    nativeCheck ("ST14_" + label + "_still_exact") faulted (fun code output -> code = 0 && (match reference with Some r -> values r = values output | None -> true)) |> ignore
                else
                    nativeCheck ("ST14_" + label + "_is_BL8016") faulted failedWith8016 |> ignore
            let quota, _ = emit { policy with LogicalDiskBytes = 1024UL } ir
            nativeCheck "ST14_disk_quota_is_BL8016" quota failedWith8016 |> ignore
            let wide, _ = emit policy (lower (source 200001))
            nativeCheck "ST14_fan_in_beyond_the_allowance_is_BL8016" wide failedWith8016 |> ignore
            // sort-form off: the key is not cached, so no external form (recorded)
            Environment.SetEnvironmentVariable("BLADE_SORT_FORM", "0")
            let offIr = lower (source 5001)
            let offCpp, offDs = emit policy offIr
            Environment.SetEnvironmentVariable("BLADE_SORT_FORM", null)
            check "ST14 BLADE_SORT_FORM=0: the key is not cached, the external form declines (recorded)"
                (not (offCpp.Contains "es::sort(")
                 && offDs |> List.exists (fun d -> d.Subject = "perm" && (match d.Outcome with Blade.Effects.Declined w -> w.Contains "not cached" | _ -> false))) (sprintf "%A" offDs)
            // the default-options entry never sees a grant
            check "ST14 no grant: the emission is the ordinary sort" (not (resident.Contains "external-sort v1")) ""
        with ex -> check "ST14 focused gate completes" false (ex.ToString())
    finally
        for key, value in saved do Environment.SetEnvironmentVariable(key, value)
    printFooter "External stable sort" [$"{passed} passed";$"{failed} failed";$"{skipped} skipped"]
    if failed = 0 then 0 else 1

/// JOIN TO AGGREGATE, EXECUTED (round 4 wave 4 D2/D3, rule `join-aggregate
/// v1`): the plan fires only under a print selection, which the corpus
/// harness never sets, so this gate compiles and runs each fixture with the
/// gate on and off under `BLADE_PRINT` and compares the printed values (raw
/// float bits under BLADE_PRINT_BITS for the reproducible float fixture).
let runJoinAggregateNativeTests (workDirectory: string) : int =
    printHeader "Join to aggregate, executed (D2/D3)"
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
    let keys = ["BLADE_PRINT";"BLADE_JOIN_AGG";"BLADE_JOIN_SELECTION";"BLADE_FP_REPRO";"BLADE_FP_CONTRACT";"BLADE_PRINT_BITS";"BLADE_LLVM"]
    let saved = keys |> List.map (fun k -> k, Environment.GetEnvironmentVariable k)
    let directory = Path.GetFullPath workDirectory
    Directory.CreateDirectory directory |> ignore
    let native = OperatingSystem.IsWindows() && Blade.Build.capabilities.Value.HasGpp
    let sources n (keyKernel: string) (elem: string) (amounts: string * string) (seed: string) =
        String.concat "\n"
            [ sprintf "type LeftRow = Idx<%d>" (fst n)
              sprintf "type RightRow = Idx<%d>" (snd n)
              sprintf "let left_key: Array<Int64 like LeftRow> = [%s]" (String.concat ", " [ for i in 0 .. fst n - 1 -> string ((i * 7) % 5) ])
              sprintf "let left_amount: Array<%s like LeftRow> = [%s]" elem (fst amounts)
              sprintf "let right_key: Array<Int64 like RightRow> = [%s]" (String.concat ", " [ for j in 0 .. snd n - 1 -> string ((j * 3) % 5) ])
              sprintf "let right_amount: Array<%s like RightRow> = [%s]" elem (snd amounts)
              sprintf "let matched = compound(method_for(left_amount, right_amount) <@> lambda(l, r) -> l * r, mask(method_for(left_key, right_key) <@> lambda(l, r) -> %s, lambda(e) -> e))" keyKernel
              "let match_count = extents(matched)"
              sprintf "let total = reduce(matched, (+), %s)" seed
              sprintf "let fp = reduce(matched, lambda(a, b) -> a * %s + b)" (if elem = "Int64" then "3" else "3.0") ]
    let ints n = String.concat ", " [ for i in 1 .. n -> string (i * 11 % 17 - 8) ]
    let floats n = String.concat ", " [ for i in 1 .. n -> sprintf "%d.25" (i * 13 % 19 - 9) ]
    let cases =
        [ "int_plain", sources (9, 11) "l == r" "Int64" (ints 9, ints 11) "0", [], "match_count,total,fp"
          "int_residual", sources (9, 11) "l == r && l > 1" "Int64" (ints 9, ints 11) "0", [], "match_count,total,fp"
          "int_residual_empty", sources (4, 3) "l == r && l > 100" "Int64" (ints 4, ints 3) "7", [], "match_count,total"
          "float_repro", sources (9, 11) "l == r" "Float64" (floats 9, floats 11) "0.0",
              [ ("BLADE_FP_REPRO", "1"); ("BLADE_FP_CONTRACT", "off"); ("BLADE_PRINT_BITS", "1") ], "match_count,total"
          // H1 / ST15: join -> payload predicate -> strict aggregate; the
          // selection (mask over J, compound over it) is never stored either
          "st15_named", sources (9, 11) "l == r" "Int64" (ints 9, ints 11) "0" + "\nlet keep = mask(matched, lambda(v) -> v > 2)\nlet big = compound(matched, keep)\nlet stotal = reduce(big, (+), 0)\nlet sfp = reduce(big, lambda(a, b) -> a * 3 + b)", [], "match_count,total,stotal,sfp"
          "st15_inline_residual", sources (9, 11) "l == r && l > 1" "Int64" (ints 9, ints 11) "0" + "\nlet big = compound(matched, mask(matched, lambda(v) -> v < 0))\nlet stotal = reduce(big, lambda(a, b) -> a * 2 - b, 5)", [], "total,stotal"
          "st15_rejects_all", sources (9, 11) "l == r" "Int64" (ints 9, ints 11) "0" + "\nlet keep = mask(matched, lambda(v) -> v > 100000)\nlet big = compound(matched, keep)\nlet sfp = reduce(big, lambda(a, b) -> a * 3 + b)", [], "match_count,sfp"
          "st15_float_repro", sources (9, 11) "l == r" "Float64" (floats 9, floats 11) "0.0" + "\nlet keep = mask(matched, lambda(v) -> v > 0.0)\nlet big = compound(matched, keep)\nlet stotal = reduce(big, (+), 0.0)",
              [ ("BLADE_FP_REPRO", "1"); ("BLADE_FP_CONTRACT", "off"); ("BLADE_PRINT_BITS", "1") ], "total,stotal" ]
    let run (label: string) (cpp: string) =
        let file = Path.Combine(directory, label + ".cpp")
        Blade.CodeGen.deployRuntimeHeaders directory
        File.WriteAllText(file, cpp)
        match Blade.Build.compileCpp file directory with
        | Error e -> Error e
        | Ok exe -> Blade.Build.runExecutable exe
    try
        try
            Environment.SetEnvironmentVariable("BLADE_LLVM", "off")
            // the selection census (H1, opt-in) for the st15 cases; no other case has a mask over J
            Environment.SetEnvironmentVariable("BLADE_JOIN_SELECTION", "1")
            for (label, src, env, selection) in cases do
                for (k, v) in env do Environment.SetEnvironmentVariable(k, v)
                Environment.SetEnvironmentVariable("BLADE_PRINT", selection)
                let emitWith (gate: string) =
                    Environment.SetEnvironmentVariable("BLADE_JOIN_AGG", gate)
                    match Blade.Lowering.lower src with
                    | Error e -> failwith ($"{label} did not lower: {e}")
                    | Ok ir -> Blade.CodeGen.genSelfContainedProgramFromIR ir ("JA_" + label) |> fst
                let on = emitWith "1"
                let off = emitWith "0"
                check $"JA {label}: gate on stores no relation, gate off does"
                    (not (on.Contains "matched_compact") && on.Contains "join-aggregate v1" && off.Contains "matched_compact"
                     && (not (label.StartsWith "st15") || (not (on.Contains "big_compact") && off.Contains "big_compact"))) ""
                if not native then
                    skipped <- skipped + 1
                    printfn "  SKIP: JA %s native run -- requires native Windows and g++" label
                else
                    match run ("JA_" + label + "_on") on, run ("JA_" + label + "_off") off with
                    | Ok (c1, o1), Ok (c2, o2) ->
                        // the empty relation: the unseeded fp raises the SAME BL8003 (text, site) in both arms
                        let values (o: string) = o.Split('\n') |> Array.filter (fun l -> not (l.Contains " completed in ")) |> String.concat "\n"
                        check $"JA {label}: the same printed values gate on and off" (c1 = c2 && values o1 = values o2 && ((c1 = 0 && (values o1).Contains "total = ") || (c1 <> 0 && (values o1).Contains "BL8003"))) (o1 + "\n---\n" + o2)
                    | Error e, _ | _, Error e -> check $"JA {label}: compile/run" false e
                for (k, _) in env do Environment.SetEnvironmentVariable(k, null)
        with ex -> check "JA gate completes" false (ex.ToString())
    finally
        for key, value in saved do Environment.SetEnvironmentVariable(key, value)
    printFooter "Join to aggregate, executed" [$"{passed} passed";$"{failed} failed";$"{skipped} skipped"]
    if failed = 0 then 0 else 1
