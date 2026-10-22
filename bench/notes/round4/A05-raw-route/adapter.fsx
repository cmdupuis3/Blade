// SOURCE ONLY. Sole Luna runs after root reviews this separate bundle.
// dotnet fsi --reference:<fresh-Blade.dll> --exec <adapter> <034|caller|later|035> <new-output> <early:0|1> <expected-DLL-SHA256>
// Lower/validate/paired emission only: never builds, compiles or evaluates Blade.
open System
open System.IO
open System.Collections
open System.Globalization
open System.Security.Cryptography
open Microsoft.FSharp.Reflection
open Blade
open Blade.IR
open Blade.Types

let sha (path: string) : string = File.ReadAllBytes path |> SHA256.HashData |> Convert.ToHexString
let args: string array = fsi.CommandLineArgs |> Array.skip 1
if args.Length <> 4 then failwith "Expected case, new-output, early0|1, expected-DLL-SHA256"
let caseName: string = args.[0]
let output: string = Path.GetFullPath args.[1]
let early: string = args.[2]
if early <> "0" && early <> "1" then failwith "Explicit early gate must be 0 or 1"
let repo: string = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../../../.."))
let relative, expectedSourceSha, corpus =
    match caseName with
    | "034" -> "tests/corpus/memfree/034_different_extent_early_release.blade", "EF7AB770D7E814267C44D2C9BC17AA1E5E86C999D3AF5EE337B982256824EDCE", true
    | "caller" -> "bench/notes/round4/A05-census-inputs/02_caller_owned.blade", "5BB3DB4AF63EBE4502DAD3EA38F52A6DFA32AA8430D06E527C1A9CC120717766", false
    | "later" -> "bench/notes/round4/A05-census-inputs/03_later_read.blade", "E50FE638ED1DAB2D0D0BBC7B8C6007C43F5370485D2974357BF8E5916D1C020E", false
    | "035" -> "tests/corpus/memfree/035_different_extent_failure_order.blade", "45C87B848AA766AFAC1A4A87C060B0C0ACAF885657097DF1CA2DF53B7738DBBF", true
    | _ -> failwith "Only the four original A05 inputs are authorized"
let sourcePath: string = Path.Combine(repo, relative)
let assembly: string = typeof<IRProgram>.Assembly.Location
if not (String.Equals(sha assembly, args.[3], StringComparison.OrdinalIgnoreCase)) then failwith "Loaded DLL differs from root-approved fresh identity"
if sha sourcePath <> expectedSourceSha then failwith "Original fixture differs from the frozen raw-route input"
if not (List.contains "BLADE_POOL_EARLY_RELEASE" Optimize.optimizerGates) then failwith "Loaded compiler lacks the A05 gate registry entry"
if File.Exists output || Directory.Exists output then failwith "Output must be a new private directory"

// Actual defaults for every optimizer/scheduler, then the *early release* gate.
// Pool reuse is explicitly on in BOTH arms; it is never the experiment proxy.
for key in Optimize.optimizerGates do Environment.SetEnvironmentVariable(key, null)
let pins: (string * string option) list =
    [ "BLADE_POOL_REUSE", Some "1"; "BLADE_POOL_EARLY_RELEASE", Some early
      "BLADE_FP_REASSOC", None; "BLADE_FP_REPRO", Some "1"; "BLADE_FP_CONTRACT", Some "off"
      "BLADE_BLAS", Some "off"; "BLADE_CUBLAS", None; "BLADE_OMP_THREADS", Some "1"; "OMP_NUM_THREADS", Some "1"
      "BLADE_PRINT", None; "BLADE_TILE_CACHE", None; "BLADE_TILE_CACHE_VERBOSE", None
      "BLADE_LLVM", Some "off"
      "BLADE_LLVM_FACTS", None; "BLADE_LLVM_BRICKS", None; "BLADE_SYRK_TILE", None
      "BLADE_EXE_CACHE", Some "0"; "BLADE_MEMCHECK", None; "BLADE_RUN_RECORD", None ]
for key, value in pins do Environment.SetEnvironmentVariable(key, Option.toObj value)
Directory.CreateDirectory output |> ignore
let utf8 = System.Text.UTF8Encoding(false)
let write (name: string) (value: string) : unit = File.WriteAllText(Path.Combine(output, name), value, utf8)
let path (name: string) : string = Path.Combine(output, name)
File.Copy(sourcePath, path "source.original.blade")
let original: string = File.ReadAllText sourcePath
let source: string =
    if not corpus then original
    else
        // Match Corpus.parseBladeFile exactly for these non-MODULE fixtures.
        let nl = original.IndexOf '\n'
        if nl < 0 || not (original.StartsWith "// TEST: ") then failwith "Original corpus header changed"
        let body = original.Substring(nl + 1)
        if body.StartsWith "// MODULE: " then failwith "A05 source unexpectedly became a module fixture"
        write "test-name.txt" (original.Substring(9, nl - 9).TrimEnd '\r')
        body
write "source.blade" source
let compilerInputSha: string = sha (path "source.blade")
let adapterPath: string = Path.Combine(__SOURCE_DIRECTORY__, "adapter.fsx")
let adapterSha: string = sha adapterPath
write "identity.txt" ($"case={caseName}\nrepo={repo}\nassembly={assembly}\nassemblySha={sha assembly}\nadapter={adapterPath}\nadapterSha={adapterSha}\nsourcePath={sourcePath}\noriginalSha={sha sourcePath}\ncompilerInputSha={compilerInputSha}\nremovedTestHeader={corpus}\nearly={early}\npoolReuse=1\nprogramName=facts\n")
write "owner-schema.txt" "Per id, tuple slots: hotProducer, hotConsumer, cellLoop, cellFold, recLoop, recStep, epilogueProducer, epilogueJoin, compactionMask, compactionCompound, compactionFoldMap, compactionFolds, compactRowProducer, compactRowConsumer, membershipPlans. Full membership table, donation maps/current return and all existing orbit audits are separate files."
let keys: string list = (Optimize.optimizerGates @ (pins |> List.map fst)) |> List.distinct
write "environment.txt" (keys |> List.map (fun key -> key + "=" + (Environment.GetEnvironmentVariable key |> Option.ofObj |> Option.defaultValue "<unset>")) |> String.concat "\n")

// The established C04 full-IR dumper, limited here to the four fixed inputs.
// No %A collections/pretty-IR elisions, depth/length cap or silent fallback.
// Integer fields form an OVER-approximate query inventory (IRId aliases int),
// including every real id in types/captures/metadata, not just printed refs.
let ids = Collections.Generic.HashSet<IRId>()
let dumpFull (name: string) (value: obj) : unit =
    use writer = new StreamWriter(path name, false, utf8)
    let rec dump (depth: int) (v: obj) : unit =
        let line (text: string) : unit = writer.WriteLine(String.replicate depth "  " + text)
        if isNull v then line "null (None/empty-list union representation)"
        else
            let t: Type = v.GetType()
            if t = typeof<IRId> then ids.Add(unbox<IRId> v) |> ignore
            if t = typeof<string> then line ("string = " + System.Text.Json.JsonSerializer.Serialize<string>(unbox<string> v))
            elif t = typeof<float> then
                let number: float = unbox<float> v
                line ("float64 = " + number.ToString("R", CultureInfo.InvariantCulture) + " bits=" + (BitConverter.DoubleToInt64Bits number).ToString("X16"))
            elif t = typeof<float32> then
                let number: float32 = unbox<float32> v
                line ("float32 = " + number.ToString("R", CultureInfo.InvariantCulture) + " bits=" + (BitConverter.SingleToInt32Bits number).ToString("X8"))
            elif t.IsPrimitive || t.IsEnum || t = typeof<decimal> then line (t.FullName + " = " + Convert.ToString(v, CultureInfo.InvariantCulture))
            elif v :? SrcLoc then
                line "SrcLoc.Span"
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
            elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Collections.Generic.KeyValuePair<_, _>> then
                line t.FullName
                dump (depth + 1) (t.GetProperty("Key").GetValue v)
                dump (depth + 1) (t.GetProperty("Value").GetValue v)
            elif v :? IEnumerable then
                line t.FullName
                for child in (v :?> IEnumerable) do dump (depth + 1) child
            else failwith ("Unhandled exhaustive dump type: " + t.FullName)
    dump 0 value

let decisions (stem: string) : unit =
    let all = Effects.Decisions.drain ()
    dumpFull (stem + ".full.txt") (box all)
    write (stem + ".txt") (all |> List.map Effects.Decisions.render |> String.concat "\n")
let donors () = PoolReuseTable.donors (), PoolReuseTable.returnDonors (), PoolReuseTable.currentReturnDonor ()
let ownership (stage: string) : unit =
    dumpFull (stage + ".donors.full.txt") (box (donors ()))
    dumpFull (stage + ".membership.full.txt") (box (Optimize.MemberHoistTable.all ()))
    // Snapshot the id inventory before plans add further numeric fields.
    let queried: IRId array = ids |> Seq.sort |> Seq.toArray
    dumpFull (stage + ".queried-ids.full.txt") (box queried)
    let entries =
        queried |> Array.map (fun id ->
            id,
            (Optimize.HotRowTable.tryProducer id, Optimize.HotRowTable.tryConsumer id,
             Optimize.CellFoldTable.tryLoop id, Optimize.CellFoldTable.tryFoldLet id,
             Optimize.RecStepTable.tryLoop id, Optimize.RecStepTable.tryStep id,
             Optimize.EpilogueLegTable.tryProducer id, Optimize.EpilogueLegTable.tryJoin id,
             Optimize.CompactionTable.tryMask id, Optimize.CompactionTable.tryCompound id,
             Optimize.CompactionTable.tryFoldMap id, Optimize.CompactionTable.foldsOf id,
             Optimize.CompactRowTable.tryProducer id, Optimize.CompactRowTable.tryConsumer id,
             Optimize.MemberHoistTable.plansOf id))
    dumpFull (stage + ".owners.full.txt") (box entries)
    let audits =
        [ for consumer in queried do
            for input in queried do
                match Optimize.OrbitPullTable.tryAudit consumer input with
                | Some audit -> yield consumer, input, audit
                | None -> () ]
    dumpFull (stage + ".orbit.full.txt") (box audits)

let run () = Runtime.runOnLargeStack (fun () ->
    Effects.Decisions.start ()
    let lowered, diagnostics = Lowering.lowerCaptured source
    dumpFull "diagnostics.full.txt" (box diagnostics)
    decisions "lower-decisions"
    let ir = match lowered with Ok ir -> ir | Error why -> write "lower-refusal.txt" why; failwith why
    dumpFull "ir.lowered.full.txt" (box ir)
    match IRValidate.validateIR ir with
    | Error errors -> dumpFull "validation-errors.full.txt" (box errors); failwith "A05 validation failed"
    | Ok ir ->
        dumpFull "ir.validated.full.txt" (box ir)
        ownership "before"
        let originalDonors = donors ()
        Effects.Decisions.start ()
        let cpp, warnings = CodeGen.genSelfContainedProgramFromIR ir "facts"
        let cppDonors = donors ()
        let cppRefusals = CodeGenState.takeCodegenRefusalDiagnostics cpp
        write "arm.cpp" cpp
        dumpFull "cpp-warnings.full.txt" (box warnings)
        dumpFull "cpp-refusals.full.txt" (box cppRefusals)
        dumpFull "cpp-donors-immediate.full.txt" (box cppDonors)
        decisions "cpp-decisions"
        dumpFull "ir.after-cpp.full.txt" (box ir)
        ownership "after-cpp"
        let sameCpp = sha (path "ir.validated.full.txt") = sha (path "ir.after-cpp.full.txt")
        let sameCppDonors = originalDonors = cppDonors
        write "cpp-invariants.txt" ($"sameIrAfterCpp={sameCpp}\ndonorsUnchangedAfterCpp={sameCppDonors}\n")
        if not cppRefusals.IsEmpty || cpp.Contains "#error" || cpp.Contains "BLADE_CODEGEN_ERROR" then
            write "cpp-refusal.txt" "Settled C++ refusal/error marker: outputs preserved; LLVM was not invoked."
            failwith "C++ refused this route; stop before LLVM"
        if not (sameCpp && sameCppDonors) then failwith "C++ changed IR/donation state; stop before LLVM"
        Effects.Decisions.start ()
        let emitted = EmitLlvm.tryEmitProgramNamed "facts" ir
        let llvmDonors = donors ()
        dumpFull "llvm-donors-immediate.full.txt" (box llvmDonors)
        decisions "llvm-decisions"
        dumpFull "ir.after-llvm.full.txt" (box ir)
        ownership "after-llvm"
        let sameLlvm = sha (path "ir.validated.full.txt") = sha (path "ir.after-llvm.full.txt")
        let sameLlvmDonors = originalDonors = llvmDonors
        write "invariants.txt" ($"sameIrAfterCpp={sameCpp}\nsameIrAfterLlvm={sameLlvm}\ndonorsUnchangedAfterCpp={sameCppDonors}\ndonorsUnchangedAfterLlvm={sameLlvmDonors}\n")
        match emitted with
        | Ok ll -> write "arm.ll" ll
        | Error why -> write "llvm-refusal.txt" why; failwith why
        if not (sameCpp && sameLlvm && sameCppDonors && sameLlvmDonors) then failwith "IR/donation state changed across paired emission; stop for root review")

try run ()
finally
    let artifactHashes =
        Directory.GetFiles output |> Array.sort |> Array.filter (fun file -> Path.GetFileName file <> "artifact-sha256.txt")
        |> Array.map (fun file -> Path.GetFileName file + " " + sha file)
    write "artifact-sha256.txt" (String.concat "\n" artifactHashes)
