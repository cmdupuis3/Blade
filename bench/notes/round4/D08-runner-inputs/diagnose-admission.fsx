// Sole-Luna source-reviewed diagnostic: reuse attempt2's fixture and DLL.
// Lower/validate and emit text only; never compile/run native code or tests.
open System
open System.IO
open System.Security.Cryptography
open Blade.Types
open Blade.IR
open Blade.Optimize
open Blade.CodeGenState

let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length <> 2 then
    failwith "Usage: dotnet fsi --reference:<attempt2 Blade.dll> --exec diagnose-admission.fsx <attempt2-focused-cases> <new-private-diagnostic-directory>"
let caseRoot = Path.GetFullPath args.[0]
let output = Path.GetFullPath args.[1]
let store = Path.Combine(caseRoot, "input_n5")
if not (Directory.Exists store) then failwith "Missing preserved attempt2 input_n5 store"
if Directory.Exists output || File.Exists output then failwith "Diagnostic output must be a new private directory"
for path in [Path.Combine(caseRoot,"owned"); output; Path.Combine(output,"options.cpp"); Path.Combine(output,"options.exe")] do
    if path.Length >= 240 || (path |> Seq.exists (fun c -> c < ' ' || c > '~')) then
        failwith "Use shorter private ASCII diagnostic paths below 240 characters"
let assembly = typeof<IRProgram>.Assembly.Location
let hash (path: string) = SHA256.HashData(File.ReadAllBytes path) |> Convert.ToHexString
let expectedDll = "E27D59F80593A9E8DA752D8A2F4793C6FC72D7D1FA4963CD2100DF3D052BFE88"
if hash assembly <> expectedDll then failwith "Expected the preserved source-matching attempt2 DLL; do not rebuild for this probe"
Directory.CreateDirectory output |> ignore
let source =
    String.concat "\n"
        [ "import zarr as z";
          sprintf "let s = z.load(\"%s\")" (store.Replace('\\','/'));
          "let A = s.vars.A |> z.stream";
          "let p = (method_for(A) <@> lambda(x) -> x * x + 1.0) |> compute";
          "let t = reduce(p, (+), 5.0)";
          "let m = t / Float64(extents(p))";
          "let result = reduce(method_for(p) <@> lambda(v) -> (v - m) * (v - m), (+), 3.0)" ]
File.WriteAllText(Path.Combine(output, "input.blade"), source)
let policy : PrivateSpoolPolicy =
    { Root = Path.Combine(caseRoot, "owned")
      TrackedWorkspaceBytes = 1048576UL
      LogicalDiskBytes = 1048576UL
      BlockCells = 2UL }
let keys = ["BLADE_FP_REPRO";"BLADE_FP_REASSOC";"BLADE_FP_CONTRACT";"BLADE_PRINT";"BLADE_TILE_CACHE";"BLADE_LLVM";"BLADE_BLAS"]
let saved = keys |> List.map (fun key -> key, Environment.GetEnvironmentVariable key)
let savedTiming = splitTimingModeEnabled ()
let savedTarget = splitTimingOnlyBinding ()

let diagnostic () =
    use log = new StreamWriter(Path.Combine(output, "admission.txt"), false)
    log.AutoFlush <- true
    let line (text: string) =
        log.WriteLine text
        printfn "%s" text
        Console.Out.Flush ()
    let section (name: string) (body: unit -> unit) =
        line ("BEGIN " + name)
        try body () with error -> line ("SECTION_ERROR " + name + " " + error.ToString())
        line ("END " + name)
    let decisions (stage: string) =
        for decision in Blade.Effects.Decisions.drain () do
            line ("DECISION " + stage + " " + Blade.Effects.Decisions.render decision)
    let planner (stage: string) (ir: IRProgram) =
        match Blade.OptimizePrivateSpool.tryPlan policy (Some (Set.singleton "result")) ir with
        | Error reason -> line ("PLANNER " + stage + " REFUSED " + reason)
        | Ok plan -> line (sprintf "PLANNER %s ACCEPTED source=%d p=%d t=%d m=%d result=%d" stage plan.Source.Id plan.Producer.Id plan.Statistic.Id plan.Barrier.Id plan.Terminal.Id)
    let tables (stage: string) (ir: IRProgram) =
        line ("TABLES " + stage)
        for KeyValue(key,value) in PoolReuseTable.donors () do line (sprintf "DONOR %d %d" key value)
        for KeyValue(key,value) in PoolReuseTable.returnDonors () do line (sprintf "RETURN_DONOR %d %d" key value)
        line (sprintf "CURRENT_RETURN_DONOR %A" (PoolReuseTable.currentReturnDonor ()))
        for p in MemberHoistTable.all () do line (sprintf "MEMBER host=%d set=%d kernels=%A" p.HostId p.SetId (Set.toList p.Kernels))
        for modul in ir.Modules do
            let ids = (modul.Bindings |> List.map _.Id) @ (modul.Functions |> List.map _.Id) |> Set.ofList
            line (sprintf "OWNER_CONFLICT_ALL_BINDING_CALLABLE_IDS %b" (Blade.OptimizePrivateSpool.ownerConflict ids))
            for id in ids do
                line (sprintf "OWNER id=%d hotP=%b hotC=%b cellL=%b cellF=%b recL=%b recS=%b epiP=%b epiJ=%b member=%d mask=%b compound=%b foldMap=%b folds=%d compactP=%b compactC=%b return=%b"
                    id (HotRowTable.tryProducer id).IsSome (HotRowTable.tryConsumer id).IsSome
                    (CellFoldTable.tryLoop id).IsSome (CellFoldTable.tryFoldLet id).IsSome
                    (RecStepTable.tryLoop id).IsSome (RecStepTable.tryStep id).IsSome
                    (EpilogueLegTable.tryProducer id).IsSome (EpilogueLegTable.tryJoin id).IsSome (MemberHoistTable.plansOf id).Length
                    (CompactionTable.tryMask id).IsSome (CompactionTable.tryCompound id).IsSome
                    (CompactionTable.tryFoldMap id).IsSome (CompactionTable.foldsOf id).Length
                    (CompactRowTable.tryProducer id).IsSome (CompactRowTable.tryConsumer id).IsSome (PoolReuseTable.tryReturnDonor id).IsSome)
                for other in ids do
                    match OrbitPullTable.tryAudit id other with
                    | Some audit -> line (sprintf "ORBIT consumer=%d source=%d %A" id other audit)
                    | None -> ()
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
        line ("ASSEMBLY " + assembly + " SHA256 " + hash assembly)
        line ("FIXTURE " + store)
        for path in [store; policy.Root; output; Path.Combine(output,"options.cpp"); Path.Combine(output,"options.exe")] do
            line (sprintf "PATH_LENGTH %d ASCII=%b %s" path.Length (path |> Seq.forall (fun c -> c >= ' ' && c <= '~')) path)
        line "PROJECTED_EXE_PATH_ONLY no executable is generated or launched by this diagnostic"
        for file in Directory.GetFiles(store, "*", SearchOption.AllDirectories) |> Array.sort do
            line ("FIXTURE_FILE " + file + " SHA256 " + hash file)
        for key in keys do line (sprintf "ENV %s=%A" key (Environment.GetEnvironmentVariable key))
        line (sprintf "POLICY root=%s workspace=%d disk=%d block=%d fpSupported=%b" policy.Root policy.TrackedWorkspaceBytes policy.LogicalDiskBytes policy.BlockCells (Blade.CodeGenPrivateSpool.fpSupported ()))
        Blade.Effects.Decisions.start ()
        match Blade.Lowering.lower source with
        | Error error -> line ("LOWER_REFUSED " + error); decisions "lower"; 3
        | Ok ir ->
            line "LOWER_OK"
            // Durable planner reason/decisions/current tables precede every
            // rich IR pretty-printer, %A payload dump or optional serialization.
            planner "after-lower" ir
            decisions "lower"
            section "tables-after-lower" (fun () -> tables "after-lower" ir)
            match Blade.IRValidate.validateIR ir with
            | Error errors ->
                for error in errors do line ("VALIDATE_REFUSED " + error)
                4
            | Ok validated ->
                line (sprintf "VALIDATE_OK sameProgramObject=%b" (obj.ReferenceEquals(ir,validated)))
                // Match the focused test: same original lowered graph, resident
                // whole-program emit first, then options emit; validation audits
                // it without replacing that graph or resetting ownership tables.
                Blade.Effects.Decisions.start ()
                let ordinary, ordinaryWarnings = Blade.CodeGen.genSelfContainedProgramFromIR ir "D08_resident"
                decisions "resident-emit"
                planner "after-resident-emit" ir
                section "tables-after-resident-emit" (fun () -> tables "after-resident-emit" ir)
                Blade.Effects.Decisions.start ()
                let cpp, warnings = Blade.CodeGen.genSelfContainedProgramFromIRWithOptions {PrivateSpool=Some policy} ir "D08_private_spool"
                decisions "options-emit"
                planner "after-options-emit" ir
                section "tables-after-options-emit" (fun () -> tables "after-options-emit" ir)
                line (sprintf "EMISSION admitted=%b residentPool=%b normalPool=%b" (cpp.Contains "// private-statistic-spool-v1:") (cpp.Contains "Array<double, 1> p =") (ordinary.Contains "Array<double, 1> p ="))
                for warning in ordinaryWarnings @ warnings do line ("EMIT_WARNING " + warning)
                File.WriteAllText(Path.Combine(output, "resident.cpp"), ordinary)
                File.WriteAllText(Path.Combine(output, "options.cpp"), cpp)
                for modul in ir.Modules do
                    line (sprintf "MODULE %s bindings=%d functions=%d reads=%d writes=%d random=%d compound=%d sparse=%d mutableArrays=%d" modul.Name modul.Bindings.Length modul.Functions.Length modul.ProviderReads.Count modul.ProviderWrites.Count modul.RandomInits.Count modul.CompoundInits.Count modul.SparseInits.Count modul.MutableArrayLets.Count)
                    for position, binding in List.indexed modul.Bindings do
                        section (sprintf "binding-%d-%s-%d" position binding.Name binding.Id) (fun () ->
                            line (sprintf "BINDING_TYPE %A" binding.Type)
                            line (sprintf "BINDING_VALUE %A" binding.Value))
                    for callable in modul.Functions do
                        section (sprintf "callable-%s-%d" callable.Name callable.Id) (fun () -> line (sprintf "%A" callable))
                    section "declared-types" (fun () -> line (sprintf "%A" modul.Types))
                    section "provider-reads" (fun () -> line (sprintf "%A" modul.ProviderReads))
                    section "derived-origins" (fun () -> line (sprintf "%A" modul.DerivedFuncOrigins))
                    for position, root in List.indexed (compactionRoots modul.Functions modul) do
                        section (sprintf "demand-root-%d" position) (fun () -> line (sprintf "%A" root))
                line "DIAGNOSTIC_COMPLETE no native compilation/execution requested"
                0
    finally
        setSplitTimingMode savedTiming
        setSplitTimingOnlyBinding savedTarget
        for key, value in saved do Environment.SetEnvironmentVariable(key,value)

exit (Blade.Runtime.runOnLargeStack diagnostic)
