// Root-approved sole-Luna emission diagnostic only. Never builds/compiles/runs
// generated programs or invokes any test category. Coder has not run this file.
open System
open System.IO
open System.Security.Cryptography

let fail (message: string) = failwith message
let sha (path: string) : string = File.ReadAllBytes path |> SHA256.HashData |> Convert.ToHexString
let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length <> 5 then fail "Expected: <033 source> <new output directory> <reuse0|1> <isolated|default> <positive|gather>"
let sourcePath, output, reuse, schedule, mode = Path.GetFullPath args.[0], Path.GetFullPath args.[1], args.[2], args.[3], args.[4]
if not (List.contains reuse ["0"; "1"]) || not (List.contains schedule ["isolated"; "default"]) || not (List.contains mode ["positive"; "gather"]) then
    fail "Unknown arm; no implicit gate fallback."
if mode = "gather" && (reuse <> "1" || schedule <> "isolated") then fail "Only isolated reuse1 gather diagnostic is authorized."
if File.Exists output || Directory.Exists output then fail "Output must be a new private directory."
if sha sourcePath <> "613343A9C3F9F9766A3E4BBA85BC3FC9CE39349EC7428C9A876D36796646DBBD" then fail "033 bytes differ from frozen fixture."
let assembly = typeof<Blade.IR.IRProgram>.Assembly.Location
if sha assembly <> "A7835B8E4849E32215ADBECD323EC482C2A0E83F4FCDA7567F3D2C26277E65A7" then fail "Loaded DLL is not the fresh reviewed retry1 build."

// Exactly defaultEmissionEnv + returnPins + samePins in LlvmTests.fs. Private
// TEMP/TMP and noncompiler environment/provenance are managed by Luna outside.
let pins =
    [ "BLADE_FP_REASSOC", None; "BLADE_LLVM_FACTS", None; "BLADE_LLVM_BRICKS", None; "BLADE_SYRK_TILE", None
      "BLADE_FP_REPRO", Some "1"; "BLADE_FP_CONTRACT", Some "off"; "BLADE_OMP_THREADS", Some "1"
      "BLADE_BLAS", Some "off"; "BLADE_CSE", Some "0"; "BLADE_POOL_REUSE", Some reuse
      "BLADE_HOT_ROW", (if schedule = "default" then None else Some "0")
      "BLADE_CELL_EPILOGUE", (if schedule = "default" then None else Some "off")
      "BLADE_EPILOGUE_LEGS", (if schedule = "default" then None else Some "0")
      "BLADE_COMPACTION_FUSION", Some "0"; "BLADE_COMPACT_ROW_CLASSES", Some "0" ]
for key, value in pins do Environment.SetEnvironmentVariable(key, Option.toObj value)
let original = File.ReadAllText(sourcePath).Replace("\r\n", "\n")
let needle = "p * 0.5 + bias"
let source =
    if mode = "positive" then original
    else
        if original.Split([|needle|], StringSplitOptions.None).Length <> 2 then fail "Gather replacement must match exactly once."
        original.Replace(needle, "method_for(range<Ix>) <@> lambda(i) -> p(i) * 0.5 + bias |> compute")
Directory.CreateDirectory output |> ignore
let write (name: string) (value: string) : unit = File.WriteAllText(Path.Combine(output, name), value)
write "source.blade" source
write "identity.txt" ($"assembly={assembly}\nassemblySha={sha assembly}\nsourcePath={sourcePath}\nsourceSha={sha sourcePath}\nreuse={reuse}\nschedule={schedule}\nmode={mode}\n")
write "environment.txt" (pins |> List.map (fun (k, v) -> k + "=" + defaultArg v "<unset>") |> String.concat "\n")
let decisions (name: string) : unit =
    let ds = Blade.Effects.Decisions.drain ()
    write name (ds |> List.map (fun d -> Blade.Effects.Decisions.render d + "\n" + sprintf "%A" d) |> String.concat "\n")
Blade.Effects.Decisions.start ()
match Blade.Lowering.lower source with
| Error error -> fail ("Lowering refused: " + error)
| Ok ir ->
    match Blade.IRValidate.validateIR ir with
    | Error errors -> fail ("Validation refused: " + String.concat "; " errors)
    | Ok ir ->
        decisions "lower-decisions.txt"
        write "program.ir.txt" (sprintf "%A" ir)
        let before = Blade.Types.PoolReuseTable.donors (), Blade.Types.PoolReuseTable.returnDonors ()
        write "donors-before.txt" (sprintf "%A" before)
        // Positive facts emit C++ first from this SAME tree; gather facts do
        // not. Preserve this ordering and any table handshake without relower.
        if mode = "positive" then
            Blade.Effects.Decisions.start ()
            let cpp, warnings = Blade.CodeGen.genSelfContainedProgramFromIR ir "facts"
            write "arm.cpp" cpp
            write "cpp-warnings.txt" (String.concat "\n" warnings)
            decisions "cpp-decisions.txt"
        Blade.Effects.Decisions.start ()
        match Blade.EmitLlvm.tryEmitProgramNamed "facts" ir with
        | Error why ->
            decisions "llvm-decisions.txt"
            write "llvm-refusal.txt" why
            fail ("LLVM emission refused: " + why)
        | Ok ll ->
            write "arm.ll" ll
            decisions "llvm-decisions.txt"
            let after = Blade.Types.PoolReuseTable.donors (), Blade.Types.PoolReuseTable.returnDonors ()
            write "donors-after.txt" (sprintf "%A\nunchanged=%b" after (before = after))
