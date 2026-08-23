// `blade doctor` -- the native-toolchain health report
// (docs/plans/plan-toolchain-packaging.md, Phase "doctor").
//
// Every probe here COMPILES AND RUNS a real program rather than trusting a
// PATH lookup, because the single most common setup failure on Windows is
// invisible to PATH probes: a second MinGW root shadowing MSYS2's UCRT64
// toolchain makes `g++ --version` succeed while every actual compile dies
// silently (exit 1, zero diagnostics). Only a real compile can tell the
// difference, so the g++ row is a compile+run of a hello program (with an
// OpenMP call, since Build always passes -fopenmp), and the BLAS/LAPACK/
// NetCDF rows link and execute one-call probes against the RESOLVED tier --
// the same `LinAlgPatterns.blasBuildFlags` expansion the compiler itself
// uses, so doctor cannot report healthy flags the build would not get.
//
// Dependent probes are gated on the g++ core: if hello does not compile and
// run, the BLAS/NetCDF/MPI rows report "skipped" instead of misattributing
// the toolchain failure to themselves.
//
// Compiled after Build.fs (probes, compileCpp, runExecutable) and before
// Cli.fs (the `doctor` verb dispatch).
module Blade.Doctor

open System
open System.IO
open System.Diagnostics
open System.Runtime.InteropServices

type CheckStatus =
    | StatusOk
    | StatusOff      // valid, deliberate "not configured" (e.g. BLAS default-off)
    | StatusWarn     // present but degraded (links, does not run; nvcc without cl)
    | StatusMissing  // optional dependency not present
    | StatusError    // configured or required, but broken

type CheckResult = {
    Key : string      // stable machine key ("gpp", "blas", ...)
    Title : string    // human row title
    Status : CheckStatus
    Detail : string   // one line
    Origin : string   // what configured it ("OPENBLAS_DIR [env]"), or "" for probes
}

let private statusLabel = function
    | StatusOk -> " OK "
    | StatusOff -> "OFF "
    | StatusWarn -> "WARN"
    | StatusMissing -> "MISS"
    | StatusError -> "FAIL"

let private statusJson = function
    | StatusOk -> "ok"
    | StatusOff -> "off"
    | StatusWarn -> "warn"
    | StatusMissing -> "missing"
    | StatusError -> "error"

/// "<KEY> [env]" / "<KEY> [toolchain.json]" / "" -- the provenance fragment
/// for a configuration-driven row.
let private originOf (key: string) : string =
    match Toolchain.getWithOrigin key with
    | Some (_, Toolchain.FromEnv) -> $"{key} [env]"
    | Some (_, Toolchain.FromFile) -> $"{key} [toolchain.json]"
    | None -> ""

/// Doctor's scratch directory (under the system temp; recreated per run).
let private scratchDir () =
    let d = Path.Combine(Path.GetTempPath(), "blade_doctor")
    Directory.CreateDirectory d |> ignore
    d

/// First line of a tool's version banner, for row details. Distinct from
/// Build.probeTool (bool) because doctor wants the text.
let private toolFirstLine (exe: string) (args: string) : string option =
    try
        let psi = ProcessStartInfo(exe, args)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        use proc = Process.Start(psi)
        let out = proc.StandardOutput.ReadToEnd()
        proc.StandardError.ReadToEnd() |> ignore
        proc.WaitForExit(10000) |> ignore
        if proc.ExitCode = 0 then
            out.Split('\n') |> Array.tryHead |> Option.map _.Trim()
        else None
    with _ -> None

/// Direct g++ compile for the BLAS/LAPACK probes. Build.compileCpp cannot
/// serve these: its flag assembly is sniff-driven (it looks for the shim
/// header includes), while doctor's probes call cblas/lapacke directly with
/// the tier expansion passed in explicitly.
let private gppCompile (compileFlags: string) (linkFlags: string) (src: string) (exe: string) : Result<unit, string> =
    try
        let args = $"-std=c++17{compileFlags} -o \"{exe}\" \"{src}\"{linkFlags}"
        let psi = ProcessStartInfo("g++", args)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        use proc = Process.Start(psi)
        let stdout = proc.StandardOutput.ReadToEnd()
        let stderr = proc.StandardError.ReadToEnd()
        if not (proc.WaitForExit(120000)) then
            (try proc.Kill() with _ -> ())
            Error "compile timed out after 120s"
        elif proc.ExitCode = 0 then Ok ()
        else
            let all = (stdout + "\n" + stderr).Trim()
            let firstLine = all.Split('\n') |> Array.tryFind (fun l -> l.Trim() <> "") |> Option.defaultValue $"exit {proc.ExitCode}, no output"
            Error (firstLine.Trim())
    with ex -> Error ex.Message

/// The Windows PATH-shadow hint, appended to core-toolchain failures.
let private gppHint =
    match Platforms.os with
    | Platforms.Windows ->
        " (hint: ensure your MSYS2 UCRT64 bin directory, e.g. C:\\msys64\\ucrt64\\bin, precedes any other MinGW toolchain on PATH -- a shadowing mingw64 makes g++ fail with no diagnostics)"
    | _ -> ""

// ---- individual checks ----

let private checkDotnet () : CheckResult =
    { Key = "dotnet"; Title = ".NET"
      Status = StatusOk
      Detail = $"{RuntimeInformation.FrameworkDescription.Trim()} on {RuntimeInformation.OSDescription.Trim()}"
      Origin = "" }

/// The REQUIRED core: g++ must compile AND run a hello with an OpenMP call
/// (Build always passes -fopenmp, so this proves the OpenMP runtime too).
/// Returns the row plus the verdict the dependent probes gate on.
let private checkGpp () : CheckResult * bool =
    let version = toolFirstLine "g++" "--version" |> Option.defaultValue "g++"
    let dir = scratchDir ()
    let src = Path.Combine(dir, "doctor_hello.cpp")
    File.WriteAllText(src,
        "#include <cstdio>\n#include <omp.h>\nint main() { std::printf(\"blade-doctor-ok threads=%d\\n\", omp_get_max_threads()); return 0; }\n")
    let fail detail =
        { Key = "gpp"; Title = "g++ / OpenMP"; Status = StatusError; Detail = detail + gppHint; Origin = "" }, false
    match Build.compileCpp src dir with
    | Error e ->
        let firstLine = e.Split('\n') |> Array.tryFind (fun l -> l.Trim() <> "") |> Option.defaultValue e
        fail $"compile FAILED: {firstLine.Trim()}"
    | Ok exe ->
        match Build.runExecutable exe with
        | Ok (0, out) when out.Contains "blade-doctor-ok" ->
            let threads =
                let m = System.Text.RegularExpressions.Regex.Match(out, @"threads=(\d+)")
                if m.Success then m.Groups.[1].Value else "?"
            { Key = "gpp"; Title = "g++ / OpenMP"; Status = StatusOk
              Detail = $"{version} -- compiles and runs (OpenMP max threads {threads})"
              Origin = "" }, true
        | Ok (code, _) -> fail $"compiled but run failed (exit {code})"
        | Error e -> fail $"compiled but run failed: {e}"

/// The BLADE_LLVM lane's toolchain row.
///
/// COMPILES AND RUNS A REAL `.ll`, on the same principle as the g++ row: a
/// PATH probe cannot tell a clang that answers `--version` from one that
/// cannot consume textual IR, and "clang exists" is precisely the claim that
/// would be wrong on a mis-layered MSYS2 install. The probe program is the
/// smallest thing that exercises the whole path the lane uses -- a module with
/// no target triple (so `-Wno-override-module` gets exercised too), an
/// external C declaration, and a `main` whose output is checked.
let private checkLlvm () : CheckResult =
    let gate = if Build.llvmEnabled () then "; BLADE_LLVM on" else "; off by default (set BLADE_LLVM=1)"
    let origin =
        match Environment.GetEnvironmentVariable "BLADE_LLVM_CLANG" with
        | null | "" -> originOf "BLADE_LLVM"
        | _ -> "BLADE_LLVM_CLANG [env]"
    let row status detail =
        // Title fits renderText's 14-column pad; "g++ / OpenMP" is the sibling.
        { Key = "llvm"; Title = "clang / LLVM"; Status = status; Detail = detail; Origin = origin }
    match Build.resolveClang () with
    | None ->
        row StatusMissing "optional -- no clang found on PATH or at C:\\msys64\\clang64\\bin; needed only for BLADE_LLVM"
    | Some clang ->
        let version = toolFirstLine clang "--version" |> Option.defaultValue clang
        let dir = scratchDir ()
        let ll = Path.Combine(dir, "doctor_llvm.ll")
        let exe = Path.Combine(dir, "doctor_llvm" + Platforms.exeExtension)
        File.WriteAllText(ll,
            "@.m = private unnamed_addr constant [21 x i8] c\"blade-doctor-llvm-ok\\00\"\n\
             declare i32 @puts(ptr)\n\
             define i32 @main() {\n\
             entry:\n\
             \x20 %r = call i32 @puts(ptr @.m)\n\
             \x20 ret i32 0\n\
             }\n")
        let args = $"-O2 -Wno-override-module -o \"{exe}\" \"{ll}\""
        match Build.runProc clang args 120000 with
        | Error e ->
            let firstLine = e.Split('\n') |> Array.tryFind (fun l -> l.Trim() <> "") |> Option.defaultValue e
            row StatusError $"{clang} found, but compiling a .ll FAILED: {firstLine.Trim()}"
        | Ok () ->
            match Build.runExecutable exe with
            | Ok (0, out) when out.Contains "blade-doctor-llvm-ok" ->
                row StatusOk $"{version} -- compiles and runs textual LLVM IR{gate}"
            | Ok (code, _) -> row StatusWarn $"{clang} compiled a .ll but the binary failed (exit {code})"
            | Error e -> row StatusWarn $"{clang} compiled a .ll but the binary failed: {e}"

let private tierName = function
    | LinAlgPatterns.TierOff -> "off"
    | LinAlgPatterns.TierExplicit -> "explicit (BLADE_BLAS_LINK)"
    | LinAlgPatterns.TierOpenBlasDir -> "OpenBLAS prefix (OPENBLAS_DIR)"
    | LinAlgPatterns.TierSystem -> "system (-lopenblas)"

let private tierOrigin = function
    | LinAlgPatterns.TierOff -> originOf "BLADE_BLAS"
    | LinAlgPatterns.TierExplicit -> originOf "BLADE_BLAS_LINK"
    | LinAlgPatterns.TierOpenBlasDir -> originOf "OPENBLAS_DIR"
    | LinAlgPatterns.TierSystem -> originOf "BLADE_BLAS"

/// The "links but does not run" PATH hint for prefix-configured libraries:
/// the runnable shared libraries live under the prefix's runtime dirs, which
/// must be on PATH / LD_LIBRARY_PATH at execution time.
let private runtimeDirHint (prefixKey: string) : string =
    match Toolchain.get prefixKey with
    | Some prefix ->
        let dirs =
            Platforms.sharedLibRuntimeDirs
            |> List.map (fun d -> Path.Combine(prefix, d))
            |> String.concat ", "
        let pathVar =
            match Platforms.os with
            | Platforms.Windows -> "PATH"
            | Platforms.Linux -> "LD_LIBRARY_PATH"
            | Platforms.MacOS -> "DYLD_LIBRARY_PATH"
        $" (is {dirs} on {pathVar}?)"
    | None -> ""

let private checkBlas (gppOk: bool) : CheckResult =
    let tier = LinAlgPatterns.resolveBlasTier ()
    let row status detail =
        { Key = "blas"; Title = "BLAS"; Status = status; Detail = detail; Origin = tierOrigin tier }
    match tier with
    | LinAlgPatterns.TierOff ->
        row StatusOff "off (the default) -- Blade emits its own loops; configure OPENBLAS_DIR, BLADE_BLAS_LINK (MKL/BLIS), or BLADE_BLAS=1"
    | _ when not gppOk ->
        row StatusError "skipped: g++ core unhealthy"
    | _ ->
        let (compileHalf, linkHalf) = LinAlgPatterns.blasBuildFlags true false
        let dir = scratchDir ()
        let src = Path.Combine(dir, "doctor_blas.cpp")
        let exe = Path.Combine(dir, "doctor_blas" + Platforms.exeExtension)
        File.WriteAllText(src,
            "#if defined(BLADE_BLAS_MKL)\n#include <mkl_cblas.h>\n#else\n#include <cblas.h>\n#endif\n"
            + "#include <cstdio>\n"
            + "int main() {\n"
            + "    double a[1] = {2.0}, b[1] = {3.0}, c[1] = {0.0};\n"
            + "    cblas_dgemm(CblasRowMajor, CblasNoTrans, CblasNoTrans, 1, 1, 1, 1.0, a, 1, b, 1, 0.0, c, 1);\n"
            + "    std::printf(\"blade-doctor-blas %g\\n\", c[0]);\n"
            + "    return c[0] == 6.0 ? 0 : 1;\n"
            + "}\n")
        let flavor =
            match LinAlgPatterns.blasFlavor () with
            | LinAlgPatterns.FlavorMkl -> "mkl"
            | LinAlgPatterns.FlavorOpenBlas -> "openblas"
            | LinAlgPatterns.FlavorGeneric -> "generic"
        match gppCompile compileHalf linkHalf src exe with
        | Error e -> row StatusError $"tier {tierName tier} does not link: {e}"
        | Ok () ->
            match Build.runExecutable exe with
            | Ok (0, out) when out.Contains "blade-doctor-blas 6" ->
                row StatusOk $"tier {tierName tier}, flavor {flavor} -- links and runs (cblas_dgemm verified)"
            | _ ->
                row StatusWarn ($"""tier {(tierName tier)} links but does not run{(runtimeDirHint "OPENBLAS_DIR")}""")

let private checkLapack (gppOk: bool) : CheckResult =
    let tier = LinAlgPatterns.resolveBlasTier ()
    let row status detail origin =
        { Key = "lapack"; Title = "LAPACK"; Status = status; Detail = detail; Origin = origin }
    if not (LinAlgPatterns.lapackAvailable ()) then
        match tier with
        | LinAlgPatterns.TierExplicit ->
            row StatusOff "off -- BLADE_LAPACK_LINK unset on the explicit tier; eigh/solve use the synthesized Jacobi path" (originOf "BLADE_BLAS_LINK")
        | _ ->
            row StatusOff "off (BLAS off) -- eigh/solve use the synthesized Jacobi path" ""
    elif not gppOk then
        row StatusError "skipped: g++ core unhealthy" ""
    else
        let (compileHalf, linkHalf) = LinAlgPatterns.blasBuildFlags true true
        let dir = scratchDir ()
        let src = Path.Combine(dir, "doctor_lapack.cpp")
        let exe = Path.Combine(dir, "doctor_lapack" + Platforms.exeExtension)
        File.WriteAllText(src,
            "#if defined(BLADE_BLAS_MKL)\n#include <mkl_lapacke.h>\n#else\n#include <lapacke.h>\n#endif\n"
            + "#include <cstdio>\n"
            + "int main() {\n"
            + "    double a[1] = {4.0}; double w[1] = {0.0};\n"
            + "    int info = LAPACKE_dsyev(LAPACK_ROW_MAJOR, 'N', 'U', 1, a, 1, w);\n"
            + "    std::printf(\"blade-doctor-lapack info=%d w=%g\\n\", info, w[0]);\n"
            + "    return (info == 0 && w[0] == 4.0) ? 0 : 1;\n"
            + "}\n")
        let origin =
            match tier with
            | LinAlgPatterns.TierExplicit -> originOf "BLADE_LAPACK_LINK"
            | _ -> tierOrigin tier
        match gppCompile compileHalf linkHalf src exe with
        | Error e -> row StatusError $"gate on but does not link: {e}" origin
        | Ok () ->
            match Build.runExecutable exe with
            | Ok (0, out) when out.Contains "blade-doctor-lapack info=0" ->
                row StatusOk "links and runs (LAPACKE_dsyev verified)" origin
            | _ ->
                row StatusWarn ($"""links but does not run{(runtimeDirHint "OPENBLAS_DIR")}""") origin

let private checkNetcdf (gppOk: bool) : CheckResult =
    let configured = (Toolchain.get "NETCDF_DIR").IsSome
    let origin = originOf "NETCDF_DIR"
    let row status detail =
        { Key = "netcdf"; Title = "NetCDF"; Status = status; Detail = detail; Origin = origin }
    if not gppOk then
        row (if configured then StatusError else StatusMissing) "skipped: g++ core unhealthy"
    else
        let dir = scratchDir ()
        let src = Path.Combine(dir, "doctor_netcdf.cpp")
        File.WriteAllText(src,
            "#include <netcdf.h>\n#include <cstdio>\nint main() { std::printf(\"blade-doctor-netcdf %s\\n\", nc_inq_libvers()); return 0; }\n")
        // Build.compileCpp's own sniff assembles the netcdf flags -- doctor
        // deliberately goes through the same path the compiler uses.
        match Build.compileCpp src dir with
        | Error e ->
            let firstLine = e.Split('\n') |> Array.tryFind (fun l -> l.Contains "error" || l.Contains "cannot find") |> Option.defaultValue "link failed"
            if configured then row StatusError $"NETCDF_DIR set but probe does not build: {firstLine.Trim()}"
            else row StatusMissing "optional -- not on default paths; set NETCDF_DIR or install libnetcdf"
        | Ok exe ->
            match Build.runExecutable exe with
            | Ok (0, out) when out.Contains "blade-doctor-netcdf" ->
                let ver = out.Replace("blade-doctor-netcdf", "").Trim().Split('\n').[0].Trim()
                row StatusOk $"links and runs (libnetcdf {ver})"
            | _ ->
                row StatusWarn ($"""links but does not run{(runtimeDirHint "NETCDF_DIR")}""")

let private checkMpi (gppOk: bool) : CheckResult =
    let row status detail origin =
        { Key = "mpi"; Title = "MPI"; Status = status; Detail = detail; Origin = origin }
    if not gppOk then row StatusMissing "skipped: g++ core unhealthy" ""
    else
        let linkOk = Build.hasMpiLink.Value
        let execPath = Build.mpiexecPath.Value
        let origin = originOf "MSMPI_BIN"
        match linkOk, execPath with
        | true, Some p -> row StatusOk $"links ({Platforms.mpiLinkFlag}); mpiexec: {p}" origin
        | true, None -> row StatusWarn $"links ({Platforms.mpiLinkFlag}) but mpiexec not found -- {Platforms.mpiRuntimeHint}" origin
        | false, Some p -> row StatusWarn $"mpiexec found ({p}) but g++ cannot link {Platforms.mpiLinkFlag} -- install the dev package" origin
        | false, None -> row StatusMissing $"optional -- {Platforms.mpiRuntimeHint}" origin

let private checkCuda () : CheckResult =
    let caps = Build.capabilities.Value
    let gate = if LinAlgPatterns.cublasAvailable () then "; BLADE_CUBLAS on" else ""
    let gpu = if caps.HasGpu then "GPU detected" else "no GPU detected"
    let row status detail =
        { Key = "cuda"; Title = "CUDA"; Status = status; Detail = detail; Origin = originOf "BLADE_CUBLAS" }
    if not caps.HasNvcc then
        row StatusMissing "optional -- nvcc not on PATH"
    elif Platforms.os = Platforms.Windows && not caps.HasCl then
        row StatusWarn $"nvcc present but cl.exe is not -- nvcc needs it as its host compiler on Windows (run from a VS x64 Native Tools prompt / vcvars64); {gpu}{gate}"
    else
        row StatusOk $"nvcc + host compiler present; {gpu}{gate}"

/// Setup-adjacent tools, presence-probed only (no compile half to verify):
/// what `blade setup --blas=source` and the proofs/ build would need.
let private checkTool (key: string) (exe: string) (purpose: string) : CheckResult =
    if Build.probeTool exe "--version" then
        let ver = toolFirstLine exe "--version" |> Option.defaultValue exe
        { Key = key; Title = exe; Status = StatusOk; Detail = ver; Origin = "" }
    else
        { Key = key; Title = exe; Status = StatusMissing; Detail = $"optional -- {purpose}"; Origin = "" }

/// WHICH stdlib the compiler will actually read, and whether any other root on
/// its search path disagrees with that one.
///
/// The second half is the point. Every other root here is a SHADOWED copy of
/// the same modules -- in a checkout, the one Blade.fsproj deploys beside the
/// binary -- and when a shadowed copy diverges, the compiler's behaviour
/// depends on which binary and which working directory you happened to use.
/// That is invisible from the outside and it does not announce itself: the
/// failure it eventually produces is an ordinary-looking error inside the copy
/// (Diagnostics.buildOutputNote annotates those), or no error at all, just an
/// edit that seems not to have taken. Naming the divergence up front is
/// cheaper than either.
///
/// A pure filesystem read, so unlike the probes below it needs no toolchain and
/// never gates on gppOk.
let private checkStdlib () : CheckResult =
    let row status detail =
        let origin =
            match Environment.GetEnvironmentVariable "BLADE_STDLIB" with
            | null | "" -> ""
            | _ -> "BLADE_STDLIB [env]"
        { Key = "stdlib"; Title = "stdlib"; Status = status; Detail = detail; Origin = origin }
    match ModuleResolve.stdlibRoots () with
    | [] ->
        row StatusError "no stdlib root found -- `import units.SI` cannot resolve; set BLADE_STDLIB"
    | winner :: shadowed ->
        let relativeModules (root: string) =
            try
                Directory.GetFiles(root, "*.blade", SearchOption.AllDirectories)
                |> Array.map (fun f ->
                    f.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                |> Array.sort
            with _ -> [||]
        // Line endings normalized before comparing: the source can arrive CRLF
        // through .gitattributes while a copy is byte-for-byte LF (or the
        // reverse), and that difference is not a divergence anyone can act on.
        let contentOf (p: string) =
            try Some ((File.ReadAllText p).Replace("\r\n", "\n")) with _ -> None
        let modules = relativeModules winner
        let divergent =
            [ for root in shadowed do
                for m in modules do
                    match contentOf (Path.Combine(winner, m)), contentOf (Path.Combine(root, m)) with
                    | Some a, Some b when a <> b -> yield (root, m)
                    | _ -> () ]
        match divergent with
        | [] ->
            let tail =
                match shadowed with
                | [] -> ""
                | _ -> $", {List.length shadowed} shadowed root(s) in agreement"
            row StatusOk $"{winner} -- {modules.Length} module(s){tail}"
        | (root, m) :: rest ->
            let more = if List.isEmpty rest then "" else $" and {List.length rest} more"
            row StatusWarn
                ($"{winner} answers, but {root} has a DIFFERENT {m}{more} -- rebuild to refresh the deployed copy")

/// Run every check. Dependent probes gate on the g++ core so a broken
/// toolchain is reported once, at its cause.
let collectChecks () : CheckResult list =
    let dotnetRow = checkDotnet ()
    let (gppRow, gppOk) = checkGpp ()
    [ dotnetRow
      checkStdlib ()
      gppRow
      checkBlas gppOk
      checkLapack gppOk
      checkNetcdf gppOk
      checkMpi gppOk
      checkCuda ()
      // Not gated on gppOk: the LLVM lane has no g++ in it at all.
      checkLlvm ()
      checkTool "make" "make" "needed for `blade setup --blas=source`"
      checkTool "gfortran" "gfortran" "needed for OpenBLAS's LAPACK half under --blas=source"
      checkTool "git" "git" "needed for `blade setup --blas=source` (clone)"
      checkTool "coq" "coqc" "needed only to re-verify proofs/" ]

/// Healthy = the required core (g++ compiles and runs) is up. Everything
/// else is optional and reports without failing the exit code.
let isHealthy (checks: CheckResult list) : bool =
    checks |> List.exists (fun c -> c.Key = "gpp" && c.Status = StatusOk)

let private jsonEscape (s: string) =
    s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t")

/// One flat JSON object; consumed by the VS Code extension and CI.
let renderJson (checks: CheckResult list) (healthy: bool) : string =
    let osName =
        match Platforms.os with
        | Platforms.Windows -> "windows"
        | Platforms.Linux -> "linux"
        | Platforms.MacOS -> "macos"
    let rows =
        checks
        |> List.map (fun c ->
            sprintf "{\"key\":\"%s\",\"title\":\"%s\",\"status\":\"%s\",\"detail\":\"%s\",\"origin\":\"%s\"}"
                (jsonEscape c.Key) (jsonEscape c.Title) (statusJson c.Status) (jsonEscape c.Detail) (jsonEscape c.Origin))
        |> String.concat ","
    sprintf "{\"os\":\"%s\",\"arch\":\"%s\",\"healthy\":%b,\"checks\":[%s]}"
        osName (RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()) healthy rows

let private renderText (checks: CheckResult list) (healthy: bool) =
    let osName =
        match Platforms.os with
        | Platforms.Windows -> "Windows"
        | Platforms.Linux -> "Linux"
        | Platforms.MacOS -> "macOS"
    printfn "Blade doctor -- %s (%O)" osName (RuntimeInformation.ProcessArchitecture)
    printfn ""
    for c in checks do
        let origin = if c.Origin = "" then "" else $"  [{c.Origin}]"
        printfn "  [%s] %-14s %s%s" (statusLabel c.Status) c.Title c.Detail origin
    printfn ""
    if healthy then printfn "core toolchain healthy (g++ compiles and runs)"
    else printfn "CORE TOOLCHAIN UNHEALTHY: `blade run`/`blade test` will not work until g++ does"

/// The verb. Exit 0 iff the required core is healthy; optional rows never
/// fail the exit code (CI gates on the core, reads the rest from --json).
let runDoctor (asJson: bool) : int =
    let checks = collectChecks ()
    let healthy = isHealthy checks
    if asJson then printfn "%s" (renderJson checks healthy)
    else renderText checks healthy
    if healthy then 0 else 1
