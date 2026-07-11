// Toolchain probing and C++/CUDA build orchestration for the Blade compiler.
// Extracted verbatim from Main.fs (audit §2.3): capability detection,
// compileCpp/compileCuda/compileCudaSplit, executable running, and the
// backend-requirement resolution shared by the CLI and the test harness.
module Blade.Build

open System
open System.IO
open System.Diagnostics
open System.Runtime.InteropServices

type Process = System.Diagnostics.Process
type ProcessStartInfo = System.Diagnostics.ProcessStartInfo

/// Check if g++ is available and working properly
let checkGppAvailable () =
    // Just assume g++ is available - actual errors will be caught during compilation
    true

// ============================================================================
// Backend capability detection + toolchain resolution
//
// CUDA is a backend *mode* from Blade's POV: the choice of whether a test
// targets the device is determined during codegen. From the harness POV
// the generated source is already settled by the time we compile, so the
// backend requirement is *inferred* from the output (presence of device
// kernels) rather than declared per-test.
//
// The harness advertises environment capabilities once at startup; each
// test's inferred requirement is intersected against them. A test whose
// requirement the environment can't satisfy is SKIPPED with a reason, not
// failed. The host-compiler choice is a per-(platform, backend) resolution,
// never a per-test axis.
// ============================================================================

type HostPlatform = PWindows | PLinux | PMacOS

type Capabilities = {
    Platform : HostPlatform
    HasGpp   : bool
    HasNvcc  : bool
    HasCl    : bool      // cl.exe on PATH (the host compiler nvcc drives on Windows)
    HasGpu   : bool      // a runnable CUDA device is present
}

/// Backend requirement inferred from generated source. `RequiresCuda` when
/// codegen emitted at least one device kernel; `CpuOnly` otherwise.
type BackendReq = CpuOnly | RequiresCuda

/// Resolution of (capabilities, requirement) into a concrete compile action.
type CompilePlan =
    | UseGpp
    | UseNvcc                 // nvcc drives host compiler: cl.exe (Windows) / g++ (Linux)
    | SkipCompile of string   // human-readable reason

/// Probe whether a tool responds to a version/help query on PATH.
let private probeTool (exe: string) (args: string) : bool =
    try
        let psi = ProcessStartInfo(exe, args)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        use proc = Process.Start(psi)
        // Drain to avoid pipe deadlock; we only care that it launched + exited.
        proc.StandardOutput.ReadToEnd() |> ignore
        proc.StandardError.ReadToEnd() |> ignore
        proc.WaitForExit(10000) |> ignore
        proc.ExitCode = 0
    with _ -> false

/// Probe for a runnable CUDA device. `nvidia-smi -L` lists devices and exits
/// 0 with a non-empty list when at least one GPU is present. This is a proxy
/// for a real `cudaGetDeviceCount` probe but avoids compiling one.
let private probeGpu () : bool =
    try
        let psi = ProcessStartInfo("nvidia-smi", "-L")
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        use proc = Process.Start(psi)
        let out = proc.StandardOutput.ReadToEnd()
        proc.StandardError.ReadToEnd() |> ignore
        proc.WaitForExit(10000) |> ignore
        proc.ExitCode = 0 && out.Contains("GPU")
    with _ -> false

let detectCapabilities () : Capabilities =
    let platform =
        if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then PWindows
        elif RuntimeInformation.IsOSPlatform(OSPlatform.OSX) then PMacOS
        else PLinux
    {
        Platform = platform
        HasGpp   = probeTool "g++" "--version"
        HasNvcc  = probeTool "nvcc" "--version"
        HasCl    = (platform = PWindows) && probeTool "cl" "/?"
        HasGpu   = probeGpu ()
    }

/// Capabilities are environment-global; detect once, lazily.
let capabilities = lazy (detectCapabilities ())

/// Infer the backend requirement from generated source. CUDA codegen emits
/// `__global__`-qualified kernels; CPU codegen never does. This keeps the
/// codegen signature untouched while the CUDA backend is built out — every
/// current test infers CpuOnly, and the inference flips automatically once
/// device kernels appear in the output.
let inferBackendReq (generatedSource: string) : BackendReq =
    if generatedSource.Contains("__global__") then RequiresCuda else CpuOnly

/// Resolve (capabilities, requirement) into a compile action. A test never
/// picks a compiler; it produces a BackendReq and this picks the toolchain.
let resolveCompile (caps: Capabilities) (req: BackendReq) : CompilePlan =
    match req, caps.Platform with
    | CpuOnly, _ when not caps.HasGpp           -> SkipCompile "requires g++, not found"
    | CpuOnly, _                                -> UseGpp
    | RequiresCuda, _ when not caps.HasNvcc     -> SkipCompile "requires CUDA, nvcc not found"
    | RequiresCuda, PMacOS                      -> SkipCompile "CUDA unsupported on macOS"
    | RequiresCuda, PWindows when not caps.HasCl -> SkipCompile "requires CUDA, cl.exe not found (nvcc host compiler)"
    | RequiresCuda, _                           -> UseNvcc

/// Whether a Result error string denotes a skip (no-toolchain, no-GPU, etc.)
/// rather than a genuine failure. Skips never count against the pass total.
let isSkipError (e: string) =
    e = "Skipped" || e.StartsWith("Skipped:")

/// Compile a C++ file with g++
let compileCpp (cppFile: string) (outputDir: string) : Result<string, string> =
    try
        let exeExt = if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then ".exe" else ".out"
        let exeFile = Path.ChangeExtension(cppFile, exeExt)
        
        // Use full paths
        let cppFullPath = Path.GetFullPath(cppFile)
        let exeFullPath = Path.GetFullPath(exeFile)
        
        // Enable OpenMP for parallel loops
        let ompFlag = "-fopenmp"

        // Backstop the Blade type system: any implicit float→integer narrowing
        // conversion in generated C++ should be a hard error, not a silent
        // truncation. Probe E's silent miscompile (Float64 → Int64) is exactly
        // this pattern.
        //
        // -Wnarrowing only catches brace-init narrowing (`int x{1.5};`); we
        //   need assignment-style coverage too (`int x = 1.5;`).
        // -Wfloat-conversion catches both, but only for float-vs-integer.
        // -Wconversion is broader but flags many legitimate cases (size_t loop
        //   counters compared with int literals, etc.) so we don't enable it.
        let safetyFlags = "-Werror=float-conversion -Werror=narrowing"

        // Provider programs (load_compound / load) emit `#include <netcdf.h>` and
        // call nc_*, so they need the netcdf header at compile and the library at
        // link. These are NOT on g++'s default search path in the common Windows
        // case: an official MSVC-built netCDF under Program Files, whose DLL is on
        // PATH (for the F# P/Invoke side) but whose include/lib g++ never sees,
        // and whose import lib is MSVC-format. Resolution:
        //   - NETCDF_DIR set: add -I<dir>\include, and link the DLL in <dir>\bin
        //     DIRECTLY (MinGW g++ links a Windows DLL by reading its export table
        //     -- robust against an MSVC .lib; the C API is ABI-compatible on x64).
        //     Fall back to -L<dir>\lib -lnetcdf if no DLL is found there.
        //   - NETCDF_DIR unset: bare -lnetcdf (works when netcdf is already on the
        //     default include/lib paths, e.g. an MSYS2 pacman install).
        // Link inputs go AFTER the source (linker order). Non-provider programs
        // add nothing.
        let needsNetcdf =
            try (File.ReadAllText cppFullPath).Contains "#include <netcdf.h>" with _ -> false
        let netcdfFlags =
            if not needsNetcdf then ""
            else
                (match System.Environment.GetEnvironmentVariable("NETCDF_DIR") with
                 | null | "" -> " -lnetcdf"
                 | dir ->
                     let incFlag = sprintf " -I\"%s\"" (Path.Combine(dir, "include"))
                     let binDir = Path.Combine(dir, "bin")
                     let dllPath = Path.Combine(binDir, "netcdf.dll")
                     let linkFlag =
                         if File.Exists dllPath then sprintf " \"%s\"" dllPath
                         else
                             let glob =
                                 try
                                     if Directory.Exists binDir then Directory.GetFiles(binDir, "netcdf*.dll") |> Array.tryHead
                                     else None
                                 with _ -> None
                             (match glob with
                              | Some p -> sprintf " \"%s\"" p
                              | None -> sprintf " -L\"%s\" -lnetcdf" (Path.Combine(dir, "lib")))
                     incFlag + linkFlag)

        let args = sprintf "-std=c++17 -O2 %s %s -o \"%s\" \"%s\"%s" ompFlag safetyFlags exeFullPath cppFullPath netcdfFlags
        
        let psi = ProcessStartInfo("g++", args)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        
        use proc = Process.Start(psi)
        // Read both streams asynchronously to prevent pipe deadlocks
        let stdoutTask = proc.StandardOutput.ReadToEndAsync()
        let stderrTask = proc.StandardError.ReadToEndAsync()
        
        if not (proc.WaitForExit(60000)) then
            try proc.Kill() with _ -> ()
            Error "Compilation timed out after 60s"
        else
        
        let stdout = stdoutTask.Result
        let stderr = stderrTask.Result
        
        // Combine all output
        let allOutput = 
            [if not (String.IsNullOrWhiteSpace stdout) then yield stdout
             if not (String.IsNullOrWhiteSpace stderr) then yield stderr]
            |> String.concat "\n"
        
        if proc.ExitCode = 0 then
            Ok exeFullPath
        else
            if String.IsNullOrWhiteSpace allOutput then
                Error (sprintf "Compilation failed (exit %d) with no output. Command: g++ %s" proc.ExitCode args)
            else
                Error (sprintf "Compilation failed (exit %d):\n%s\nCommand: g++ %s" proc.ExitCode allOutput args)
    with ex ->
        Error (sprintf "Compilation exception: %s\n%s" ex.Message ex.StackTrace)

/// Compile a CUDA (.cu) file with nvcc. nvcc auto-selects the host compiler
/// (cl.exe on Windows, g++ on Linux). Host-side warning flags are passed
/// through with -Xcompiler. Mirrors compileCpp's subprocess machinery.
let compileCuda (cuFile: string) (outputDir: string) : Result<string, string> =
    try
        let exeExt = if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then ".exe" else ".out"
        let exeFile = Path.ChangeExtension(cuFile, exeExt)
        let cuFullPath = Path.GetFullPath(cuFile)
        let exeFullPath = Path.GetFullPath(exeFile)

        // Host-compiler passthrough for the narrowing safety net. nvcc's own
        // front-end doesn't accept -Werror=float-conversion, so route it to
        // the host compiler via -Xcompiler. (cl.exe uses different flag
        // spellings; on Windows we drop the g++-specific ones and rely on
        // nvcc/cl defaults — refine once a Windows CUDA box is exercised.)
        let hostWarn =
            if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then ""
            else "-Xcompiler -Werror=float-conversion,-Werror=narrowing"

        // -std=c++17 matches the CPU path. nvcc accepts it directly.
        let args = sprintf "-std=c++17 -O2 %s -o \"%s\" \"%s\"" hostWarn exeFullPath cuFullPath

        let psi = ProcessStartInfo("nvcc", args)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true

        use proc = Process.Start(psi)
        let stdoutTask = proc.StandardOutput.ReadToEndAsync()
        let stderrTask = proc.StandardError.ReadToEndAsync()

        if not (proc.WaitForExit(120000)) then
            try proc.Kill() with _ -> ()
            Error "CUDA compilation timed out after 120s"
        else

        let stdout = stdoutTask.Result
        let stderr = stderrTask.Result
        let allOutput =
            [if not (String.IsNullOrWhiteSpace stdout) then yield stdout
             if not (String.IsNullOrWhiteSpace stderr) then yield stderr]
            |> String.concat "\n"

        if proc.ExitCode = 0 then
            Ok exeFullPath
        else
            if String.IsNullOrWhiteSpace allOutput then
                Error (sprintf "CUDA compilation failed (exit %d) with no output. Command: nvcc %s" proc.ExitCode args)
            else
                Error (sprintf "CUDA compilation failed (exit %d):\n%s" proc.ExitCode allOutput)
    with ex ->
        Error (sprintf "CUDA compilation exception: %s\n%s" ex.Message ex.StackTrace)

/// Run a subprocess, capturing combined output. Shared by the split-compile
/// steps. Returns Ok () on exit 0, else Error with the captured output.
let runProc (exe: string) (args: string) (timeoutMs: int) : Result<unit, string> =
    try
        let psi = ProcessStartInfo(exe, args)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        use proc = Process.Start(psi)
        let outT = proc.StandardOutput.ReadToEndAsync()
        let errT = proc.StandardError.ReadToEndAsync()
        if not (proc.WaitForExit(timeoutMs)) then
            (try proc.Kill() with _ -> ())
            Error (sprintf "%s timed out" exe)
        else
            let combined =
                [ if not (String.IsNullOrWhiteSpace outT.Result) then yield outT.Result
                  if not (String.IsNullOrWhiteSpace errT.Result) then yield errT.Result ]
                |> String.concat "\n"
            if proc.ExitCode = 0 then Ok ()
            else Error (sprintf "%s failed (exit %d):\n%s\nCommand: %s %s" exe proc.ExitCode combined exe args)
    with ex -> Error (sprintf "%s exception: %s" exe ex.Message)

/// Compile a CUDA program split across two files, per the chosen separation:
/// nvcc compiles the .cu (device kernels) to an object, g++ compiles the .cpp
/// (host program — no CUDA syntax, only an extern "C" prototype) to an object,
/// then the two objects are linked (with nvcc, which resolves the CUDA runtime
/// automatically). The extern "C" launch wrapper is the unmangled boundary
/// symbol both compilers agree on. Returns the exe path.
let compileCudaSplit (cuFile: string) (cppFile: string) (outputDir: string) : Result<string, string> =
    let onWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
    let exeExt = if onWindows then ".exe" else ".out"
    let cuFull = Path.GetFullPath(cuFile)
    let cppFull = Path.GetFullPath(cppFile)
    let objExt = if onWindows then ".obj" else ".o"
    let cuObj = Path.ChangeExtension(cuFull, ".cu" + objExt)
    let cppObj = Path.ChangeExtension(cppFull, ".cpp" + objExt)
    let exeFull = Path.GetFullPath(Path.Combine(outputDir, Path.GetFileNameWithoutExtension(cppFile) + exeExt))
    if onWindows then
        // Windows: pure MSVC toolchain, nvcc-orchestrated. nvcc drives cl.exe as
        // the host compiler for BOTH the .cu device code and the .cpp host code,
        // then links. This keeps a SINGLE C++ ABI (MSVC) across both objects —
        // no MinGW/g++ in the CUDA path, so no cross-ABI link fragility. (The
        // extern "C" launch wrapper would link across ABIs, but matching the
        // host toolchain on both halves is the robust native-Windows setup.)
        // Requires cl.exe on PATH — run from the VS x64 Native Tools prompt.
        // No OpenMP here: the rank-1 cuda host half has no parallel host loop,
        // so we avoid the MSVC /openmp vs g++ -fopenmp flag-spelling divergence.
        let nvccCu  = sprintf "-std=c++17 -O2 -c -o \"%s\" \"%s\"" cuObj cuFull
        let nvccCpp = sprintf "-std=c++17 -O2 -c -o \"%s\" \"%s\"" cppObj cppFull
        let nvccLink = sprintf "-std=c++17 -O2 -o \"%s\" \"%s\" \"%s\"" exeFull cuObj cppObj
        match runProc "nvcc" nvccCu 120000 with
        | Error e -> Error e
        | Ok () ->
            match runProc "nvcc" nvccCpp 120000 with
            | Error e -> Error e
            | Ok () ->
                match runProc "nvcc" nvccLink 120000 with
                | Error e -> Error e
                | Ok () -> Ok exeFull
    else
        // Linux: nvcc compiles the .cu (host code via g++), g++ compiles the
        // .cpp; both share the g++ ABI, so the split + link is safe. Host
        // warning passthrough mirrors compileCpp's safety net.
        let nvccCu = sprintf "-std=c++17 -O2 -c -o \"%s\" \"%s\"" cuObj cuFull
        let gppCpp = sprintf "-std=c++17 -O2 -fopenmp -Werror=float-conversion -Werror=narrowing -c -o \"%s\" \"%s\"" cppObj cppFull
        let nvccLink = sprintf "-std=c++17 -O2 -Xcompiler -fopenmp -o \"%s\" \"%s\" \"%s\"" exeFull cuObj cppObj
        match runProc "nvcc" nvccCu 120000 with
        | Error e -> Error e
        | Ok () ->
            match runProc "g++" gppCpp 60000 with
            | Error e -> Error e
            | Ok () ->
                match runProc "nvcc" nvccLink 120000 with
                | Error e -> Error e
                | Ok () -> Ok exeFull

/// Compile a generated source file according to its backend requirement,
/// resolved against the environment's capabilities. Returns the existing
/// `Result<exePath, message>` shape; a skip is reported as
/// `Error "Skipped: <reason>"` so downstream skip handling recognizes it.
let compileForBackend (caps: Capabilities) (req: BackendReq) (srcFile: string) (outputDir: string) : Result<string, string> =
    match resolveCompile caps req with
    | UseGpp          -> compileCpp srcFile outputDir
    | UseNvcc         -> compileCuda srcFile outputDir
    | SkipCompile why -> Error ("Skipped: " + why)


/// Run a compiled executable
let runExecutable (exeFile: string) : Result<int * string, string> =
    try
        let exeFullPath = Path.GetFullPath(exeFile)
        let psi = ProcessStartInfo(exeFullPath)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        psi.WorkingDirectory <- Path.GetDirectoryName(exeFullPath)
        
        use proc = Process.Start(psi)
        // Read both streams asynchronously to avoid deadlocks
        let stdoutTask = proc.StandardOutput.ReadToEndAsync()
        let stderrTask = proc.StandardError.ReadToEndAsync()
        
        if proc.WaitForExit(30000) then
            let stdout = stdoutTask.Result
            let stderr = stderrTask.Result
            let output = if String.IsNullOrEmpty(stderr) then stdout else stdout + "\n[stderr]: " + stderr
            Ok (proc.ExitCode, output)
        else
            try proc.Kill() with _ -> ()
            Error "Execution timed out after 30s"
    with ex ->
        Error (sprintf "Execution exception: %s" ex.Message)

/// Sanitize a test name for use as a filename (cross-platform)
let sanitizeFileName (name: string) : string =
    // Replace characters that are invalid in Windows filenames
    // Use readable names for logical operators
    name
        .Replace("&&", "_and_")
        .Replace("||", "_or_")
        .Replace(" ", "_")
        .Replace(":", "")
        .Replace("/", "_")
        .Replace("\\", "_")
        .Replace("(", "")
        .Replace(")", "")
        .Replace("|", "_")
        .Replace("&", "_")
        .Replace("+", "_")
        .Replace(",", "_")
        .Replace("<", "_")
        .Replace(">", "_")
        .Replace("\"", "")
        .Replace("*", "_")
        .Replace("?", "_")
