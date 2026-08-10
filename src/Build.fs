// Toolchain probing and C++/CUDA build orchestration for the Blade compiler:
// capability detection, compileCpp/compileCuda/compileCudaSplit, executable
// running, and the backend-requirement resolution shared by the CLI and the
// test harness.
module Blade.Build

open System
open System.IO
open System.Diagnostics
open System.Runtime.InteropServices

type Process = System.Diagnostics.Process
type ProcessStartInfo = System.Diagnostics.ProcessStartInfo

// Backend capability detection + toolchain resolution.
//
// CUDA is a backend *mode*: which test targets the device is decided during
// codegen, so by compile time the backend requirement is *inferred* from
// the generated output (presence of device kernels), not declared per-test.
// Capabilities are advertised once at startup and intersected against each
// test's inferred requirement; an unsatisfiable requirement is SKIPPED with
// a reason, not failed. The host-compiler choice is a per-(platform,
// backend) resolution, never a per-test axis.

// Shared host-compiler optimization flags: ONE value, consumed by
// compileCppWithExtra AND by every test block that shells out to g++ on its
// own (tests/Differential.fs, Benchmarks.fs, OmpTests.fs, AllocTests.fs,
// OrbWreathTests.fs) via `Build.optFlags`, since Build.fs compiles first in
// Blade.fsproj. Excludes `-std=`, a per-site property (orb_wreath_tests.cpp
// needs c++20).
//
// `-march=native` lets GCC use the build machine's full ISA. BLADE_MARCH
// makes that reproducible:
//   unset  -> `native` (default)
//   `off`  -> no -march flag (portable / cross-machine repro)
//   other  -> `-march=<value>` verbatim (e.g. `skylake`, `x86-64-v3`)
//
// `-ffp-contract` defaults to `fast` (FMA on). Contraction fuses a*b+c into
// one rounding, which breaks the BYTE-IDENTITY differential harnesses:
// src/Interp/Numerics.fs is bit-pinned to non-FMA scalar semantics. Those
// harnesses PIN `BLADE_FP_CONTRACT=off` for their own runs (tests/
// InterpDiff.fs, tests/DiffOracle.fs), since byte-identity is a property
// of the differential gates, not of user builds.
//   unset  -> `fast` (default: FMA on)
//   other  -> `-ffp-contract=<value>` verbatim (`fast` | `on` | `off`)
//
// These are FUNCTIONS, not module-level values, so a harness may set the
// env var mid-process and have it honored by the next compile -- a
// module-level `let` would freeze the default at first touch.
//
// The CUDA paths below stay at -O2: nvcc translates host flags for its host
// compiler (cl.exe on Windows) and -march does not pass through cleanly there.

/// The `-march=` fragment (leading space included, or "" when disabled).
let private marchFlag () =
    match System.Environment.GetEnvironmentVariable("BLADE_MARCH") with
    | null | "" -> " -march=native"
    | v when v.Trim().ToLowerInvariant() = "off" -> ""
    | v -> sprintf " -march=%s" (v.Trim())

/// The `-ffp-contract=` fragment (leading space included).
let private fpContractFlag () =
    match System.Environment.GetEnvironmentVariable("BLADE_FP_CONTRACT") with
    | null | "" -> " -ffp-contract=fast"
    | v -> sprintf " -ffp-contract=%s" (v.Trim())

/// Host-compiler optimization flags shared by every g++ invocation.
/// Currently `-O3 -march=native -ffp-contract=fast` by default (see the two
/// env vars above). Re-evaluated per call so harness env pins take effect.
let optFlags () = "-O3" + marchFlag () + fpContractFlag ()

type HostPlatform = PWindows | PLinux | PMacOS

type Capabilities = {
    Platform : HostPlatform
    HasGpp   : bool
    HasNvcc  : bool
    HasCl    : bool      // cl.exe on PATH (the host compiler nvcc drives on Windows)
    HasGpu   : bool      // a runnable CUDA device is present
}

/// Backend requirement inferred from generated source. `RequiresCuda` when
/// codegen emitted at least one device kernel; `RequiresMpi` when the program
/// includes <mpi.h> (needs the per-OS MPI link flag and mpiexec at run); `CpuOnly` otherwise.
type BackendReq = CpuOnly | RequiresCuda | RequiresMpi

/// Resolution of (capabilities, requirement) into a concrete compile action.
type CompilePlan =
    | UseGpp
    | UseNvcc                 // nvcc drives host compiler: cl.exe (Windows) / g++ (Linux)
    | SkipCompile of string   // human-readable reason

/// Probe whether a tool responds to a version/help query on PATH. Public:
/// `blade doctor` probes setup-adjacent tools (make, gfortran, git, coqc)
/// through the same helper rather than growing a twin.
let probeTool (exe: string) (args: string) : bool =
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

/// Marker-based tool probe: success = the tool launched and its combined
/// output contains `marker`, for tools whose exit codes are not
/// trustworthy as a presence signal (MS-MPI's mpiexec exits nonzero from a bare help query).
let private probeToolLoose (exe: string) (args: string) (marker: string) : bool =
    try
        let psi = ProcessStartInfo(exe, args)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        use proc = Process.Start(psi)
        let out = proc.StandardOutput.ReadToEnd()
        let err = proc.StandardError.ReadToEnd()
        proc.WaitForExit(10000) |> ignore
        (out + err).IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0
    with _ -> false

/// Probe for a runnable CUDA device: `nvidia-smi -L` lists devices and exits
/// 0 with a non-empty list when at least one GPU is present -- a proxy for a
/// real `cudaGetDeviceCount` probe that avoids compiling one.
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
        match Platforms.os with
        | Platforms.Windows -> PWindows
        | Platforms.MacOS -> PMacOS
        | Platforms.Linux -> PLinux
    {
        Platform = platform
        HasGpp   = probeTool "g++" "--version"
        HasNvcc  = probeTool "nvcc" "--version"
        HasCl    = (platform = PWindows) && probeTool "cl" "/?"
        HasGpu   = probeGpu ()
    }

/// Capabilities are environment-global; detect once, lazily.
let capabilities = lazy (detectCapabilities ())

/// Whether g++ is actually present and runnable on PATH. Delegates to the
/// same `probeTool "g++" "--version"` probe that resolveCompile/DiffOracle/
/// InterpDiff already consult (a hardcoded `true` would report a box
/// without g++ as a test FAILURE instead of a skip).
let checkGppAvailable () = capabilities.Value.HasGpp

/// Infer the backend requirement from generated source: CUDA codegen emits
/// `__global__`-qualified kernels, CPU codegen never does, so the inference
/// flips automatically once device kernels appear in the output.
let inferBackendReq (generatedSource: string) : BackendReq =
    if generatedSource.Contains("__global__") then RequiresCuda
    elif generatedSource.Contains("#include <mpi.h>") then RequiresMpi
    else CpuOnly

/// Resolve (capabilities, requirement) into a compile action. A test never
/// picks a compiler; it produces a BackendReq and this picks the toolchain.
/// MPI compiles with plain g++ (compileCpp appends the per-OS MPI link flag
/// when it sees the mpi.h include); a missing MPI import lib fails the g++
/// link loudly.
let resolveCompile (caps: Capabilities) (req: BackendReq) : CompilePlan =
    match req, caps.Platform with
    | CpuOnly, _ when not caps.HasGpp           -> SkipCompile "requires g++, not found"
    | CpuOnly, _                                -> UseGpp
    | RequiresMpi, _ when not caps.HasGpp       -> SkipCompile "requires g++, not found"
    | RequiresMpi, _                            -> UseGpp
    | RequiresCuda, _ when not caps.HasNvcc     -> SkipCompile "requires CUDA, nvcc not found"
    | RequiresCuda, PMacOS                      -> SkipCompile "CUDA unsupported on macOS"
    | RequiresCuda, PWindows when not caps.HasCl -> SkipCompile "requires CUDA, cl.exe not found (nvcc host compiler)"
    | RequiresCuda, _                           -> UseNvcc

/// Whether a Result error string denotes a skip (no-toolchain, no-GPU, etc.)
/// rather than a genuine failure. Skips never count against the pass total.
let isSkipError (e: string) =
    e = "Skipped" || e.StartsWith("Skipped:")

/// Run a subprocess, capturing combined output. Shared by the split-compile
/// steps and the device-shim build (`buildCublasDevice`); Ok () on exit 0, else Error with the captured output.
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

/// The include line a cuBLAS-dispatching program carries, analogous to the
/// `blade_linalg.hpp` / `blade_lapack.hpp` sniffs below: codegen writes it
/// exactly when `LinAlgPatterns.resolveNodeRoute` picked the device backend.
let private cublasShimInclude = "#include \"blade_linalg_cuda.hpp\""

/// Builds the DEVICE half of a cuBLAS-dispatching program and returns the
/// link input the host compile needs. A separate translation unit is
/// required because g++ (mingw-w64 on Windows) cannot include
/// `<cuda_runtime.h>` / `<cublas_v2.h>`, so the shim's definitions are
/// compiled by nvcc and reached across an unmangled `extern "C"` boundary.
/// On Windows it is a shared library, not an object: nvcc drives cl.exe,
/// so a device object would carry the MSVC ABI against the host's MinGW
/// ABI -- linking them directly is the fragility `compileCudaSplit` warns
/// about. Instead the nvcc half is a self-contained DLL with dllexport'd
/// `extern "C"` entry points that MinGW links via the export table, so the
/// ABI boundary is the C-ABI call alone.
///
/// The `.cu` is generated here (just an `#include` of the deployed shim
/// header) since it is build plumbing with no per-program content. A
/// missing toolchain is a hard error, not a skip: codegen already emitted
/// `blade_cuda_*` calls under an explicitly-set `BLADE_CUBLAS`, so failing
/// loudly here mirrors `blade_linalg.hpp`'s `#error` guarantee.
let buildCublasDevice (cppFullPath: string) : Result<string, string> =
    let caps = capabilities.Value
    if not caps.HasNvcc then
        Error "cuBLAS dispatch was emitted (BLADE_CUBLAS is on) but nvcc was not found on PATH; \
               the device half of blade_linalg_cuda.hpp cannot be built"
    elif caps.Platform = PWindows && not caps.HasCl then
        Error "cuBLAS dispatch was emitted (BLADE_CUBLAS is on) but cl.exe was not found on PATH; \
               nvcc needs it as its host compiler on Windows (run from a VS x64 Native Tools prompt)"
    elif caps.Platform = PMacOS then
        Error "cuBLAS dispatch was emitted (BLADE_CUBLAS is on) but CUDA is unsupported on macOS"
    else
        let onWindows = caps.Platform = PWindows
        let srcDir = Path.GetDirectoryName(cppFullPath)
        let stem = Path.GetFileNameWithoutExtension(cppFullPath)
        let cuFile = Path.Combine(srcDir, stem + "_cublas.cu")
        let libExt = if onWindows then ".dll" else ".so"
        let libFile = Path.Combine(srcDir, stem + "_cublas" + libExt)
        let cuText =
            String.concat "\n"
                [ "// Generated by Blade.Build.buildCublasDevice -- the DEVICE translation unit"
                  "// for this program's cuBLAS dispatch. nvcc defines __CUDACC__, so the shim"
                  "// header below expands to its definitions rather than its host prototypes."
                  "#include \"blade_linalg_cuda.hpp\""
                  "" ]
        // A failed write is reported, not swallowed: nvcc would otherwise
        // compile whatever stale `.cu` happened to be there.
        match (try File.WriteAllText(cuFile, cuText); None with ex -> Some ex.Message) with
        | Some why -> Error (sprintf "could not write the cuBLAS device translation unit %s: %s" cuFile why)
        | None ->
        // /Zc:preprocessor: CCCL headers refuse MSVC's traditional
        // preprocessor (same rule as compileCudaSplit / compileCudaMpiHybrid).
        let sharedFlags =
            if onWindows then "-shared -Xcompiler /Zc:preprocessor"
            else "-shared -Xcompiler -fPIC"
        let args =
            sprintf "-std=c++17 -O2 %s -o \"%s\" \"%s\" -lcublas"
                sharedFlags libFile cuFile
        match runProc "nvcc" args 300000 with
        | Error e -> Error e
        | Ok () -> Ok libFile

/// Compile a C++ file with g++. `extraLinkInputs` are appended after the
/// source (linker order) -- e.g. the hybrid mpi+cuda build passes the
/// nvcc-built device DLL here (MinGW links DLL export tables directly).
let compileCppWithExtra (extraLinkInputs: string list) (cppFile: string) (outputDir: string) : Result<string, string> =
    try
        let exeExt = Platforms.exeExtension
        let exeFile = Path.ChangeExtension(cppFile, exeExt)
        
        let cppFullPath = Path.GetFullPath(cppFile)
        let exeFullPath = Path.GetFullPath(exeFile)
        
        let ompFlag = "-fopenmp"

        // Backstops the Blade type system: implicit float->integer narrowing
        // in generated C++ must be a hard error. -Wnarrowing alone only
        // catches brace-init, not assignment; -Wconversion is broader but
        // flags legitimate cases (size_t loop counters vs int literals).
        let safetyFlags = "-Werror=float-conversion -Werror=narrowing"

        // Provider programs emit `#include <netcdf.h>`, needing the netcdf
        // header at compile and library at link -- not on g++'s default
        // search path in the common Windows case (MSVC-built netCDF with
        // an MSVC-format import lib). Resolution (NETCDF_DIR comes through
        // Toolchain.get: process env first, blade.toolchain.json second):
        //   - NETCDF_DIR set: add -I<dir>/include, link the shared library
        //     Platforms.findSharedLib locates under the prefix (the direct
        //     DLL/.so path); falls back to -L<dir>/lib -lnetcdf.
        //   - NETCDF_DIR unset: bare -lnetcdf (default package-manager
        //     install: MSYS2 pacman, apt, brew).
        let needsNetcdf =
            try (File.ReadAllText cppFullPath).Contains "#include <netcdf.h>" with _ -> false

        // MPI programs include <mpi.h> and call the MPI C API -- the MPI
        // dev package puts the header/import lib on g++'s default search
        // paths in the supported installs (MSYS2 mingw-w64 `msmpi` on
        // Windows, OpenMPI/MPICH elsewhere), so the bare per-OS link flag
        // suffices (mirrors -lnetcdf above; Platforms.mpiLinkFlag owns the
        // spelling).
        let needsMpi =
            try (File.ReadAllText cppFullPath).Contains "#include <mpi.h>" with _ -> false
        let mpiFlags = if needsMpi then " " + Platforms.mpiLinkFlag else ""
        let netcdfFlags =
            if not needsNetcdf then ""
            else
                (match Toolchain.get "NETCDF_DIR" with
                 | None -> " -lnetcdf"
                 | Some dir ->
                     let incFlag = sprintf " -I\"%s\"" (Path.Combine(dir, "include"))
                     let linkFlag =
                         match Platforms.findSharedLib dir "netcdf" with
                         | Some lib -> sprintf " \"%s\"" lib
                         | None -> sprintf " -L\"%s\" -lnetcdf" (Path.Combine(dir, "lib"))
                     incFlag + linkFlag)

        // Linalg-dispatch programs emit `#include "blade_linalg.hpp"` and
        // call blade_linalg::* for gram/matmul/dot/gemv. The header is
        // BLAS-only (`#error`s without the define); codegen consults the
        // SAME gate this line does (`LinAlgPatterns.blasAvailable`), so
        // within one process "include present" and "gate on" cannot
        // disagree -- an emit and a compile with different gates fails
        // loudly at the `#error` rather than silently miscompiling.
        //
        // Gate semantics are the four TIERS of `LinAlgPatterns.resolveBlasTier`
        // (BLADE_BLAS=0 off / BLADE_BLAS_LINK explicit / OPENBLAS_DIR prefix /
        // BLADE_BLAS=1 bare-system); default-off since BLAS may differ in the
        // last ULP and the differentials demand byte-identical output. The
        // tier's EXPANSION into compile/link halves -- defines, include dirs,
        // library inputs, the MKL header-flavor define -- is
        // `LinAlgPatterns.blasBuildFlags`: one gate, one expansion, consumed
        // here.
        let cppTextForSniff = try File.ReadAllText cppFullPath with _ -> ""
        let usesLinalgShim = cppTextForSniff.Contains "#include \"blade_linalg.hpp\""
        let blasGateOn = Blade.LinAlgPatterns.blasAvailable ()
        // LAPACK gets its own sniff arm and define, so a BLAS-only program
        // stays distinguishable from a LAPACK-carrying one (same
        // #error-on-mismatch guarantee as BLAS). On the OpenBLAS tiers its
        // gate rides the BLAS resolution (LAPACKE is bundled); on the
        // explicit tier it requires BLADE_LAPACK_LINK -- see lapackAvailable.
        let usesLapackShim = cppTextForSniff.Contains "#include \"blade_lapack.hpp\""
        let lapackGateOn = Blade.LinAlgPatterns.lapackAvailable ()
        // Split into a COMPILE half (defines + -I, precedes the source) and
        // a LINK half (library inputs, follows it); DEFINES stay per-header
        // (`wantsBlas`/`wantsLapack` each gate on include-present AND its own flag).
        let wantsBlas = usesLinalgShim && blasGateOn
        let wantsLapack = usesLapackShim && lapackGateOn
        let (blasCompileFlags, blasLinkFlags) =
            if not (wantsBlas || wantsLapack) then ("", "")
            else Blade.LinAlgPatterns.blasBuildFlags wantsBlas wantsLapack

        // The DEVICE half. `blade_linalg_cuda.hpp`'s include line is written
        // by codegen exactly when a node route resolved to `CudaBlas`; the
        // resulting shared library joins `extraLinkInputs`, linked after
        // the source. Handled here rather than at each caller, so every
        // caller gets it without knowing the backend exists.
        let deviceBuild =
            if cppTextForSniff.Contains cublasShimInclude then
                buildCublasDevice cppFullPath |> Result.map (fun lib -> [lib])
            else Ok []
        match deviceBuild with
        | Error e -> Error (sprintf "cuBLAS device build failed:\n%s" e)
        | Ok deviceInputs ->

        let extraFlags = (extraLinkInputs @ deviceInputs) |> List.map (fun p -> sprintf " \"%s\"" (Path.GetFullPath p)) |> String.concat ""
        let args = sprintf "-std=c++17 %s %s %s%s -o \"%s\" \"%s\"%s%s%s%s" (optFlags ()) ompFlag safetyFlags blasCompileFlags exeFullPath cppFullPath extraFlags netcdfFlags mpiFlags blasLinkFlags
        
        let psi = ProcessStartInfo("g++", args)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        
        use proc = Process.Start(psi)
        // Read both streams asynchronously to prevent pipe deadlocks
        let stdoutTask = proc.StandardOutput.ReadToEndAsync()
        let stderrTask = proc.StandardError.ReadToEndAsync()
        
        // 300s: spectra-scale generated programs (rank-2 transforms, capped
        // at 65536 cells) can legitimately push g++ this long under -O3.
        if not (proc.WaitForExit(300000)) then
            try proc.Kill() with _ -> ()
            Error "Compilation timed out after 300s"
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
                Error (sprintf "Compilation failed (exit %d) with no output. Command: g++ %s" proc.ExitCode args)
            else
                Error (sprintf "Compilation failed (exit %d):\n%s\nCommand: g++ %s" proc.ExitCode allOutput args)
    with ex ->
        Error (sprintf "Compilation exception: %s\n%s" ex.Message ex.StackTrace)

/// Compile a C++ file with g++ (no extra link inputs).
let compileCpp (cppFile: string) (outputDir: string) : Result<string, string> =
    compileCppWithExtra [] cppFile outputDir

/// Compile a CUDA (.cu) file with nvcc. nvcc auto-selects the host compiler
/// (cl.exe on Windows, g++ on Linux). Host-side warning flags are passed
/// through with -Xcompiler. Mirrors compileCpp's subprocess machinery.
let compileCuda (cuFile: string) (outputDir: string) : Result<string, string> =
    try
        let exeExt = Platforms.exeExtension
        let exeFile = Path.ChangeExtension(cuFile, exeExt)
        let cuFullPath = Path.GetFullPath(cuFile)
        let exeFullPath = Path.GetFullPath(exeFile)

        // Host-compiler passthrough for the narrowing safety net: nvcc's own
        // front-end doesn't accept -Werror=float-conversion, so route it via
        // -Xcompiler (cl.exe uses different flag spellings, so Windows drops
        // the g++-specific ones and relies on nvcc/cl defaults).
        let hostWarn =
            if Platforms.os = Platforms.Windows then
                // CCCL (thrust/complex.h) refuses MSVC's traditional
                // preprocessor; the conforming one is safe for all generated
                // CUDA code, so it is passed unconditionally.
                "-Xcompiler /Zc:preprocessor"
            else "-Xcompiler -Werror=float-conversion,-Werror=narrowing"

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

/// Compiles a CUDA program split across two files: nvcc compiles the .cu
/// (device kernels) to an object, g++ compiles the .cpp (host program -- no
/// CUDA syntax, only an extern "C" prototype) to an object, then nvcc links
/// both (resolving the CUDA runtime automatically). The extern "C" launch
/// wrapper is the unmangled boundary symbol both compilers agree on.
let compileCudaSplit (cuFile: string) (cppFile: string) (outputDir: string) : Result<string, string> =
    let onWindows = Platforms.os = Platforms.Windows
    let exeExt = Platforms.exeExtension
    let cuFull = Path.GetFullPath(cuFile)
    let cppFull = Path.GetFullPath(cppFile)
    let objExt = Platforms.objExtension
    let cuObj = Path.ChangeExtension(cuFull, ".cu" + objExt)
    let cppObj = Path.ChangeExtension(cppFull, ".cpp" + objExt)
    let exeFull = Path.GetFullPath(Path.Combine(outputDir, Path.GetFileNameWithoutExtension(cppFile) + exeExt))
    if onWindows then
        // Windows: pure MSVC toolchain, nvcc-orchestrated -- nvcc drives
        // cl.exe as the host compiler for both halves, then links, keeping
        // a single C++ ABI (no cross-ABI link fragility). Requires cl.exe
        // on PATH. No OpenMP here: the rank-1 cuda host half has no
        // parallel loop. /Zc:preprocessor (CCCL refuses MSVC's traditional
        // preprocessor) applies to the .cu compile only.
        let nvccCu  = sprintf "-std=c++17 -O2 -Xcompiler /Zc:preprocessor -c -o \"%s\" \"%s\"" cuObj cuFull
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
        // .cpp; both share the g++ ABI, so the split + link is safe.
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

/// Hybrid mpi+cuda build: the .cu becomes a self-contained MSVC DLL (nvcc
/// -shared drives cl.exe; the launch wrappers are dllexport'd extern "C"),
/// and the host .cpp takes the g++ path (-fopenmp, -lmsmpi) linking the DLL
/// directly (MinGW reads DLL export tables, as for netcdf.dll) so no
/// MS-MPI SDK import lib or cross-ABI object link is needed.
let compileCudaMpiHybrid (cuFile: string) (cppFile: string) (outputDir: string) : Result<string, string> =
    let caps = capabilities.Value
    if not caps.HasNvcc then Error "Skipped: requires CUDA, nvcc not found"
    elif caps.Platform = PWindows && not caps.HasCl then Error "Skipped: requires CUDA, cl.exe not found (nvcc host compiler)"
    elif not caps.HasGpp then Error "Skipped: requires g++, not found"
    else
        let cuFull = Path.GetFullPath cuFile
        let dllExt = if caps.Platform = PWindows then ".dll" else ".so"
        let dllFull = Path.Combine(Path.GetFullPath outputDir, Path.GetFileNameWithoutExtension cuFile + "_cuda" + dllExt)
        // /Zc:preprocessor on Windows: CCCL (thrust/complex.h) refuses MSVC's
        // traditional preprocessor (see compileCudaSplit).
        let sharedFlags =
            if caps.Platform = PWindows then "-shared -Xcompiler /Zc:preprocessor"
            else "-shared -Xcompiler -fPIC"
        let nvccArgs = sprintf "-std=c++17 -O2 %s -o \"%s\" \"%s\"" sharedFlags dllFull cuFull
        match runProc "nvcc" nvccArgs 180000 with
        | Error e -> Error e
        | Ok () -> compileCppWithExtra [dllFull] cppFile outputDir

/// Compiles a generated source file according to its backend requirement,
/// resolved against the environment's capabilities. A skip is reported as
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
        
        // 120s: simulation-scale examples (thousands of spectral steps) can
        // legitimately run long; corpus tests still finish well under a second.
        if proc.WaitForExit(120000) then
            let stdout = stdoutTask.Result
            let stderr = stderrTask.Result
            let output = if String.IsNullOrEmpty(stderr) then stdout else stdout + "\n[stderr]: " + stderr
            Ok (proc.ExitCode, output)
        else
            try proc.Kill() with _ -> ()
            Error "Execution timed out after 120s"
    with ex ->
        Error (sprintf "Execution exception: %s" ex.Message)

// MPI launch support (mpiexec resolution + wrapped execution)

/// Locates mpiexec. The MS-MPI installer updates the MACHINE-scope PATH,
/// which already-running processes never see, so a bare PATH lookup is the
/// last resort. Probe order: process-env MSMPI_BIN -> machine-scope
/// MSMPI_BIN -> the well-known install path -> bare "mpiexec" (marker-
/// probed; MS-MPI's exit codes are untrustworthy). Lazy: resolved once.
let mpiexecPath : Lazy<string option> =
    lazy (
        let mpiexecName = if Platforms.os = Platforms.Windows then "mpiexec.exe" else "mpiexec"
        let fromEnv (scope: EnvironmentVariableTarget option) =
            try
                let v =
                    match scope with
                    | Some s -> Environment.GetEnvironmentVariable("MSMPI_BIN", s)
                    | None -> (match Toolchain.get "MSMPI_BIN" with Some d -> d | None -> null)
                match v with
                | null | "" -> None
                | d -> Some (Path.Combine(d, mpiexecName))
            with _ -> None
        let onWindows = Platforms.os = Platforms.Windows
        [ fromEnv None
          (if onWindows then fromEnv (Some EnvironmentVariableTarget.Machine) else None)
          (if onWindows then Some @"C:\Program Files\Microsoft MPI\Bin\mpiexec.exe" else None)
          Some "mpiexec" ]
        |> List.choose id
        |> List.tryFind (fun exe ->
            if Path.IsPathRooted exe then File.Exists exe
            else probeToolLoose exe "-help" "mpi"))

/// Whether g++ can compile+link an MPI program (Platforms.mpiLinkFlag
/// resolvable -- the MSYS2 msmpi package on Windows, the OpenMPI/MPICH dev
/// package elsewhere): one real link probe in a temp dir, lazy so ordinary
/// invocations never pay for it.
let hasMpiLink : Lazy<bool> =
    lazy (
        try
            let dir = Path.Combine(Path.GetTempPath(), "blade_mpi_probe")
            Directory.CreateDirectory(dir) |> ignore
            let src = Path.Combine(dir, "mpi_probe.cpp")
            File.WriteAllText(src,
                "#include <mpi.h>\nint main(int argc, char** argv){ MPI_Init(&argc,&argv); MPI_Finalize(); return 0; }\n")
            let exe = Path.Combine(dir, "mpi_probe" + Platforms.exeExtension)
            let psi = ProcessStartInfo("g++", sprintf "-std=c++17 \"%s\" %s -o \"%s\"" src Platforms.mpiLinkFlag exe)
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            use proc = Process.Start(psi)
            proc.StandardOutput.ReadToEnd() |> ignore
            proc.StandardError.ReadToEnd() |> ignore
            proc.WaitForExit(30000) |> ignore
            proc.ExitCode = 0
        with _ -> false)

/// Run a compiled MPI executable under `mpiexec -n <ranks>`. Same
/// stream/timeout discipline as runExecutable; mpiexec propagates a failing
/// rank's exit code. 60s timeout (multi-process startup is slower than a bare exe).
let runExecutableMpi (ranks: int) (exeFile: string) : Result<int * string, string> =
    match mpiexecPath.Value with
    | None -> Error (sprintf "mpiexec not found (%s)" Platforms.mpiRuntimeHint)
    | Some mpiexec ->
        try
            let exeFullPath = Path.GetFullPath(exeFile)
            let psi = ProcessStartInfo(mpiexec, sprintf "-n %d \"%s\"" ranks exeFullPath)
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            psi.WorkingDirectory <- Path.GetDirectoryName(exeFullPath)
            use proc = Process.Start(psi)
            let stdoutTask = proc.StandardOutput.ReadToEndAsync()
            let stderrTask = proc.StandardError.ReadToEndAsync()
            if proc.WaitForExit(60000) then
                let stdout = stdoutTask.Result
                let stderr = stderrTask.Result
                let output = if String.IsNullOrEmpty(stderr) then stdout else stdout + "\n[stderr]: " + stderr
                Ok (proc.ExitCode, output)
            else
                try proc.Kill() with _ -> ()
                Error "Execution timed out after 60s (mpiexec)"
        with ex ->
            Error (sprintf "Execution exception: %s" ex.Message)

/// Sanitize a test name for use as a filename (cross-platform).
let sanitizeFileName (name: string) : string =
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
