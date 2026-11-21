// The provider boundary rule (BL8015) and the masked read
// (`alias.read_present` / `alias.present`): shared machinery for the
// per-provider blocks (`blade test netcdf|zarr|csv|icechunk`), which call
// `bothLanes` with their own programs and fixtures.
//
// Every program here runs in BOTH lanes -- compiled (g++) and interpreted --
// and is judged in both: a value pin must appear in both stdouts, a refusal
// must be BL8015 in both with the SAME sentence (the compiled
// blade_rt::missing_report and the interpreter's ProviderRegistry.
// missingMessage are twins). `predicateParity` holds the rule's two
// predicates -- ProviderRegistry.isMissing (F#) and missingCpp (the emitted
// C++) -- to one answer on a common sample set.
module Blade.Tests.ProviderBoundaryTests

open System
open System.IO
open Blade
open Blade.Build
open Blade.Tests.TestHarness

/// One program, both lanes.
type LaneRun =
    { Cpp: string
      /// None: the compile was skipped (no g++ / toolchain piece missing).
      Compiled: Result<int * string, string> option
      InterpExit: int
      InterpOut: string
      InterpErr: string }

/// Copy a file or a whole directory (relative to the harness cwd) to the
/// same relative path under `outDir`, where the compiled exe runs.
let stage (outDir: string) (rel: string) =
    let dst = Path.Combine(outDir, rel)
    if Directory.Exists rel then
        let rec copyDir (src: string) (dst: string) =
            Directory.CreateDirectory dst |> ignore
            for f in Directory.GetFiles src do File.Copy(f, Path.Combine(dst, Path.GetFileName f), true)
            for d in Directory.GetDirectories src do copyDir d (Path.Combine(dst, Path.GetFileName d))
        copyDir rel dst
    elif File.Exists rel then
        Directory.CreateDirectory(Path.GetDirectoryName dst) |> ignore
        File.Copy(rel, dst, true)

/// Lower `src`, run it compiled (cwd = `outDir`, where `fixtures` are staged)
/// and interpreted (cwd = the harness's, where the fixtures already are).
let bothLanes (outDir: string) (name: string) (fixtures: string list) (src: string) : Result<LaneRun, string> =
    Blade.ProviderStatics.install ()
    match Blade.Lowering.lower src with
    | Error e -> Error e
    | Ok ir ->
        Directory.CreateDirectory outDir |> ignore
        for f in fixtures do stage outDir f
        let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir name
        CodeGen.deployRuntimeHeaders outDir
        let file = Path.Combine(outDir, name + ".cpp")
        File.WriteAllText(file, cpp)
        let compiled =
            match compileCpp file outDir with
            | Error e when isSkipError e -> None
            | Error e -> Some (Error ($"compile: {e}"))
            | Ok exe -> Some (runExecutable exe)
        let r = Blade.Interp.Run.runProgram ir name Blade.Interp.Value.defaultLimits
        Ok { Cpp = cpp; Compiled = compiled; InterpExit = r.ExitCode; InterpOut = r.Stdout; InterpErr = r.Stderr }

/// The refusal sentence (the text after `error[BL8015]: `) in an output.
let refusalLine (text: string) : string option =
    text.Replace("\r\n", "\n").Split('\n')
    |> Array.tryPick (fun l ->
        let i = l.IndexOf "error[BL8015]: "
        if i >= 0 then Some (l.Substring(i + "error[BL8015]: ".Length).Trim()) else None)

let private clip (s: string) = if s.Length > 400 then s.Substring(0, 400) + "..." else s

/// Both lanes exit 0 and print every needle.
let expectValues (check: string -> bool -> string -> unit) (label: string) (r: LaneRun) (needles: string list) =
    match r.Compiled with
    | None -> printfn "  SKIP %s (compiled lane): toolchain unavailable" label
    | Some (Error e) -> check ($"{label}: compiled lane runs") false (clip e)
    | Some (Ok (code, out)) ->
        check ($"{label}: compiled lane exits 0") (code = 0) (clip out)
        for n in needles do
            check ($"{label}: compiled prints `{n}`") (out.Contains n) (clip out)
    check ($"{label}: interpreter exits 0") (r.InterpExit = 0) (clip r.InterpErr)
    for n in needles do
        check ($"{label}: interpreter prints `{n}`") (r.InterpOut.Contains n) (clip (r.InterpOut + r.InterpErr))

/// Both lanes refuse with BL8015, the same sentence, containing every needle.
let expectRefusal (check: string -> bool -> string -> unit) (label: string) (r: LaneRun) (needles: string list) =
    let interpLine = refusalLine r.InterpErr
    check ($"{label}: interpreter refuses BL8015") (r.InterpExit <> 0 && interpLine.IsSome) (clip (r.InterpOut + r.InterpErr))
    for n in needles do
        check ($"{label}: interpreter sentence names `{n}`")
            (interpLine |> Option.exists (fun l -> l.Contains n)) (defaultArg interpLine "(no BL8015 line)")
    match r.Compiled with
    | None -> printfn "  SKIP %s (compiled lane): toolchain unavailable" label
    | Some (Error e) -> check ($"{label}: compiled lane runs") false (clip e)
    | Some (Ok (code, out)) ->
        let line = refusalLine out
        check ($"{label}: compiled refuses BL8015 (nonzero exit)") (code <> 0 && line.IsSome) (clip out)
        check ($"{label}: the two lanes' sentences are identical") (line.IsSome && line = interpLine)
            (sprintf "compiled: %A | interp: %A" line interpLine)

/// ProviderRegistry.isMissing and the C++ missingCpp agree on every sample
/// under every rule shape: NaN / +-Inf, finite sentinels (a Float32
/// _FillValue compared after widening, a sentinel exactly at a bound),
/// inclusive range endpoints, an integer sentinel.
let predicateParity (check: string -> bool -> string -> unit) (outDir: string) =
    let open' = Blade.ProviderRegistry.nonFiniteRule
    let f32fill = float -999.9f
    let rules =
        [ "non-finite", open'
          "netcdf-like", { open' with Sentinels = [ -999.0; f32fill ]; Lo = Some 0.0; Hi = Some 100.0; Describe = "x" }
          "integer sentinel", { Blade.ProviderRegistry.noMissingRule with Sentinels = [ -9.0 ]; Describe = "x" }
          "range only", { Blade.ProviderRegistry.noMissingRule with Lo = Some (-1.5); Describe = "x" } ]
    let samples =
        [ nan; infinity; -infinity; -999.0; f32fill; -999.9; 0.0; 100.0; 100.00000000000001
          -1e-300; 50.0; -9.0; -1.5; -1.5000000000000002 ]
    let cppOf (x: float) =
        if Double.IsNaN x then "std::nan(\"\")"
        elif Double.IsPositiveInfinity x then "INFINITY"
        elif Double.IsNegativeInfinity x then "(-INFINITY)"
        else Blade.ProviderRegistry.ruleLiteral x
    let expected =
        rules |> List.map (fun (_, r) ->
            (samples |> List.map (fun x -> if Blade.ProviderRegistry.isMissing r x then "1" else "0") |> String.concat "")
            // A Float32 cell holding the Float32 fill: widened by the cast in the predicate.
            + (if Blade.ProviderRegistry.isMissing r (float -999.9f) then "1" else "0"))
    let body =
        [ "#include <cmath>"
          "#include <cstdio>"
          "int main() {"
          $"""    double xs[] = {{ {(samples |> List.map cppOf |> String.concat ", ")} }};"""
          "    float f32 = -999.9f;" ]
        @ [ for (_, r) in rules do
                yield "    for (double x : xs) std::printf(\"%d\", (int)(" + Blade.ProviderRegistry.missingCpp r "x" + "));"
                yield "    std::printf(\"%d\\n\", (int)(" + Blade.ProviderRegistry.missingCpp r "f32" + "));" ]
        @ [ "    return 0;"; "}" ]
    Directory.CreateDirectory outDir |> ignore
    let file = Path.Combine(outDir, "boundary_predicate_probe.cpp")
    File.WriteAllText(file, String.concat "\n" body + "\n")
    match compileCpp file outDir with
    | Error e when isSkipError e -> printfn "  SKIP boundary predicate parity: %s" e
    | Error e -> check "boundary predicate parity: probe compiles" false (clip e)
    | Ok exe ->
        match runExecutable exe with
        | Ok (0, out) ->
            let lines = out.Replace("\r\n", "\n").Split('\n') |> Array.filter (fun l -> l.Trim() <> "") |> List.ofArray
            List.zip3 (rules |> List.map fst) expected (lines @ List.replicate (max 0 (rules.Length - lines.Length)) "")
            |> List.truncate rules.Length
            |> List.iter (fun (name, want, got) ->
                check ($"boundary predicate parity ({name}): isMissing = missingCpp on every sample")
                    (want = got.Trim()) ($"F# {want} vs C++ {got.Trim()}"))
        | Ok (code, out) -> check "boundary predicate parity: probe runs" false ($"exit {code}: {(clip out)}")
        | Error e -> check "boundary predicate parity: probe runs" false e
    // INTEGER rules compare as int64 in both predicates: NC_FILL_INT64 is
    // missing, its double-rounded neighbours are not.
    let irule =
        { Blade.ProviderRegistry.noMissingRule with
            IntSentinels = [ -9223372036854775806L; -9L ]; IntHi = Some 1000L; Describe = "x" }
    let isamples = [ -9223372036854775806L; -9223372036854775807L; -9223372036854775805L; -9L; 0L; 1000L; 1001L; Int64.MinValue ]
    let iwant = isamples |> List.map (fun n -> if Blade.ProviderRegistry.isMissingInt irule n then "1" else "0") |> String.concat ""
    let ys = isamples |> List.map Blade.ProviderRegistry.int64Cpp |> String.concat ", "
    let ibody =
        [ "#include <cstdio>"
          "int main() {"
          "    long long ys[] = { " + ys + " };"
          "    for (long long y : ys) std::printf(\"%d\", (int)(" + Blade.ProviderRegistry.missingCpp irule "y" + "));"
          "    std::printf(\"\\n\");"
          "    return 0;"
          "}" ]
    let ifile = Path.Combine(outDir, "boundary_predicate_probe_int.cpp")
    File.WriteAllText(ifile, String.concat "\n" ibody + "\n")
    match compileCpp ifile outDir with
    | Error e when isSkipError e -> printfn "  SKIP boundary predicate parity (integer): %s" e
    | Error e -> check "boundary predicate parity (integer): probe compiles" false (clip e)
    | Ok exe ->
        match runExecutable exe with
        | Ok (0, out) ->
            check "boundary predicate parity (integer, int64-exact): isMissingInt = missingCpp"
                (out.Trim() = iwant) ($"F# {iwant} vs C++ {out.Trim()}")
        | Ok (code, out) -> check "boundary predicate parity (integer): probe runs" false ($"exit {code}: {(clip out)}")
        | Error e -> check "boundary predicate parity (integer): probe runs" false e
