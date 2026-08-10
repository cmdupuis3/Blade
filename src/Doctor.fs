// `blade doctor` -- the native-toolchain health report
// (docs/plan-toolchain-packaging.md, Phase "doctor").
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
    | Some (_, Toolchain.FromEnv) -> sprintf "%s [env]" key
    | Some (_, Toolchain.FromFile) -> sprintf "%s [toolchain.json]" key
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
            out.Split('\n') |> Array.tryHead |> Option.map (fun l -> l.Trim())
        else None
    with _ -> None

/// Direct g++ compile for the BLAS/LAPACK probes. Build.compileCpp cannot
/// serve these: its flag assembly is sniff-driven (it looks for the shim
/// header includes), while doctor's probes call cblas/lapacke directly with
/// the tier expansion passed in explicitly.
let private gppCompile (compileFlags: string) (linkFlags: string) (src: string) (exe: string) : Result<unit, string> =
    try
        let args = sprintf "-std=c++17%s -o \"%s\" \"%s\"%s" compileFlags exe src linkFlags
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
            let firstLine = all.Split('\n') |> Array.tryFind (fun l -> l.Trim() <> "") |> Option.defaultValue (sprintf "exit %d, no output" proc.ExitCode)
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
      Detail = sprintf "%s on %s" (RuntimeInformation.FrameworkDescription.Trim()) (RuntimeInformation.OSDescription.Trim())
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
        fail (sprintf "compile FAILED: %s" (firstLine.Trim()))
    | Ok exe ->
        match Build.runExecutable exe with
        | Ok (0, out) when out.Contains "blade-doctor-ok" ->
            let threads =
                let m = System.Text.RegularExpressions.Regex.Match(out, @"threads=(\d+)")
                if m.Success then m.Groups.[1].Value else "?"
            { Key = "gpp"; Title = "g++ / OpenMP"; Status = StatusOk
              Detail = sprintf "%s -- compiles and runs (OpenMP max threads %s)" version threads
              Origin = "" }, true
        | Ok (code, _) -> fail (sprintf "compiled but run failed (exit %d)" code)
        | Error e -> fail (sprintf "compiled but run failed: %s" e)

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
        sprintf " (is %s on %s?)" dirs pathVar
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
        | Error e -> row StatusError (sprintf "tier %s does not link: %s" (tierName tier) e)
        | Ok () ->
            match Build.runExecutable exe with
            | Ok (0, out) when out.Contains "blade-doctor-blas 6" ->
                row StatusOk (sprintf "tier %s, flavor %s -- links and runs (cblas_dgemm verified)" (tierName tier) flavor)
            | _ ->
                row StatusWarn (sprintf "tier %s links but does not run%s" (tierName tier) (runtimeDirHint "OPENBLAS_DIR"))

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
        | Error e -> row StatusError (sprintf "gate on but does not link: %s" e) origin
        | Ok () ->
            match Build.runExecutable exe with
            | Ok (0, out) when out.Contains "blade-doctor-lapack info=0" ->
                row StatusOk "links and runs (LAPACKE_dsyev verified)" origin
            | _ ->
                row StatusWarn (sprintf "links but does not run%s" (runtimeDirHint "OPENBLAS_DIR")) origin

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
            if configured then row StatusError (sprintf "NETCDF_DIR set but probe does not build: %s" (firstLine.Trim()))
            else row StatusMissing "optional -- not on default paths; set NETCDF_DIR or install libnetcdf"
        | Ok exe ->
            match Build.runExecutable exe with
            | Ok (0, out) when out.Contains "blade-doctor-netcdf" ->
                let ver = out.Replace("blade-doctor-netcdf", "").Trim().Split('\n').[0].Trim()
                row StatusOk (sprintf "links and runs (libnetcdf %s)" ver)
            | _ ->
                row StatusWarn (sprintf "links but does not run%s" (runtimeDirHint "NETCDF_DIR"))

let private checkMpi (gppOk: bool) : CheckResult =
    let row status detail origin =
        { Key = "mpi"; Title = "MPI"; Status = status; Detail = detail; Origin = origin }
    if not gppOk then row StatusMissing "skipped: g++ core unhealthy" ""
    else
        let linkOk = Build.hasMpiLink.Value
        let execPath = Build.mpiexecPath.Value
        let origin = originOf "MSMPI_BIN"
        match linkOk, execPath with
        | true, Some p -> row StatusOk (sprintf "links (%s); mpiexec: %s" Platforms.mpiLinkFlag p) origin
        | true, None -> row StatusWarn (sprintf "links (%s) but mpiexec not found -- %s" Platforms.mpiLinkFlag Platforms.mpiRuntimeHint) origin
        | false, Some p -> row StatusWarn (sprintf "mpiexec found (%s) but g++ cannot link %s -- install the dev package" p Platforms.mpiLinkFlag) origin
        | false, None -> row StatusMissing (sprintf "optional -- %s" Platforms.mpiRuntimeHint) origin

let private checkCuda () : CheckResult =
    let caps = Build.capabilities.Value
    let gate = if LinAlgPatterns.cublasAvailable () then "; BLADE_CUBLAS on" else ""
    let gpu = if caps.HasGpu then "GPU detected" else "no GPU detected"
    let row status detail =
        { Key = "cuda"; Title = "CUDA"; Status = status; Detail = detail; Origin = originOf "BLADE_CUBLAS" }
    if not caps.HasNvcc then
        row StatusMissing "optional -- nvcc not on PATH"
    elif Platforms.os = Platforms.Windows && not caps.HasCl then
        row StatusWarn (sprintf "nvcc present but cl.exe is not -- nvcc needs it as its host compiler on Windows (run from a VS x64 Native Tools prompt / vcvars64); %s%s" gpu gate)
    else
        row StatusOk (sprintf "nvcc + host compiler present; %s%s" gpu gate)

/// Setup-adjacent tools, presence-probed only (no compile half to verify):
/// what `blade setup --blas=source` and the proofs/ build would need.
let private checkTool (key: string) (exe: string) (purpose: string) : CheckResult =
    if Build.probeTool exe "--version" then
        let ver = toolFirstLine exe "--version" |> Option.defaultValue exe
        { Key = key; Title = exe; Status = StatusOk; Detail = ver; Origin = "" }
    else
        { Key = key; Title = exe; Status = StatusMissing; Detail = sprintf "optional -- %s" purpose; Origin = "" }

/// Run every check. Dependent probes gate on the g++ core so a broken
/// toolchain is reported once, at its cause.
let collectChecks () : CheckResult list =
    let dotnetRow = checkDotnet ()
    let (gppRow, gppOk) = checkGpp ()
    [ dotnetRow
      gppRow
      checkBlas gppOk
      checkLapack gppOk
      checkNetcdf gppOk
      checkMpi gppOk
      checkCuda ()
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
        let origin = if c.Origin = "" then "" else sprintf "  [%s]" c.Origin
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
