// Platform abstraction (docs/plans/plan-toolchain-packaging.md, Phase 1).
//
// THE one module that knows which operating system this process runs on and
// how that OS spells toolchain artifacts: executable/object/shared-library
// extensions, the MPI link line, the C runtime's libm, and where a library
// install prefix keeps its runnable shared libraries. Everything else --
// Build.fs's flag assembly, LinAlgPatterns' BLAS tier expansion, the
// interpreter's libm binding, the future `blade doctor`/`blade setup` verbs --
// consults these values instead of branching on RuntimeInformation itself,
// so per-OS knowledge accumulates here as data rather than as control flow
// scattered across call sites.
//
// Deliberately dependency-free (System only, no Blade module) and compiled
// right after Runtime.fs, so every later module can reach it.
module Blade.Platforms

open System.IO
open System.Runtime.InteropServices

/// The three operating systems Blade's native toolchain path supports.
/// Build.fs's `HostPlatform` (the capabilities surface the test runner
/// consumes) mirrors this three-way split; `os` below is the single
/// detection site both derive from.
type Os =
    | Windows
    | Linux
    | MacOS

/// The current OS, detected once. Never changes mid-process, so a value
/// (unlike the env-var gates, which must be functions).
let os : Os =
    if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then Windows
    elif RuntimeInformation.IsOSPlatform(OSPlatform.OSX) then MacOS
    else Linux

/// Extension for executables Blade compiles (g++/nvcc output). `.out` rather
/// than empty on Unix so `Path.ChangeExtension` on the source path yields a
/// distinct file -- the long-standing Build.fs convention, now owned here.
let exeExtension : string =
    match os with Windows -> ".exe" | Linux | MacOS -> ".out"

/// Object-file extension (the split CUDA build compiles to objects first).
let objExtension : string =
    match os with Windows -> ".obj" | Linux | MacOS -> ".o"

/// Shared-library extension, for probe/search logic. Windows import-library
/// and MinGW direct-DLL-link conventions live in `findSharedLib` below.
let sharedLibExtension : string =
    match os with Windows -> ".dll" | Linux -> ".so" | MacOS -> ".dylib"

/// The library g++'s <cmath> resolves to at runtime, per platform -- the
/// exact library the interpreter must bind for bit-identical scalar math.
/// src/Interp/Numerics.fs registers a DllImportResolver mapping its logical
/// "blade_libm" import to this name: ucrt64 g++ forwards <cmath> to
/// ucrtbase.dll on Windows; glibc keeps libm separate (the .6 soname is the
/// stable ABI name -- a bare "libm.so" is a linker script on some distros
/// and not loadable); macOS bundles libm into libSystem. Byte-identity with
/// compiled binaries is a PER-PLATFORM property: the identity claim is
/// always against the local g++ output, which calls this same library.
let libmName : string =
    match os with
    | Windows -> "ucrtbase.dll"
    | Linux -> "libm.so.6"
    | MacOS -> "libSystem.dylib"

/// MPI link flag for the g++ line (no leading space; callers add spacing).
/// MS-MPI's import library on Windows (the MSYS2 `msmpi` package puts
/// mpi.h/libmsmpi.a on g++'s default search paths); the OpenMPI/MPICH
/// convention elsewhere.
let mpiLinkFlag : string =
    match os with Windows -> "-lmsmpi" | Linux | MacOS -> "-lmpi"

/// Human hint for a missing MPI runtime, matching `mpiLinkFlag`'s target.
let mpiRuntimeHint : string =
    match os with
    | Windows -> "install the MS-MPI runtime"
    | Linux | MacOS -> "install OpenMPI or MPICH"

/// Subdirectories of a library install prefix that hold its RUNNABLE shared
/// libraries -- what must be on PATH (Windows) / LD_LIBRARY_PATH (Linux) /
/// DYLD_LIBRARY_PATH (macOS) when a program linked against the prefix runs.
/// Doctor/setup messaging data; `findSharedLib` has its own (wider) search.
let sharedLibRuntimeDirs : string list =
    match os with Windows -> ["bin"] | Linux -> ["lib"; "lib64"] | MacOS -> ["lib"]

/// Locate a shared library under an install prefix by its `-l` stem
/// (e.g. "openblas" -> libopenblas.dll / libopenblas.so / libopenblas.dylib),
/// returning a full path suitable for handing DIRECTLY to the g++ link line:
/// MinGW links a DLL's export table in place of an import lib, and ld
/// accepts a .so/.dylib path verbatim (DT_NEEDED records the soname). The
/// conventional exact names are tried first -- both `lib<stem>` and the
/// MSVC-style bare `<stem>` on Windows (an MSVC-built netcdf ships
/// netcdf.dll, not libnetcdf.dll) -- then a wildcard sweep for decorated
/// variants (vendor-suffixed DLLs, libfoo.so.0, versioned dylibs).
/// None -> the caller falls back to `-L<prefix>/lib -l<stem>`.
let findSharedLib (prefix: string) (stem: string) : string option =
    let searchDirs =
        match os with Windows -> ["bin"; "lib"] | Linux -> ["lib"; "lib64"] | MacOS -> ["lib"]
    let exactNames =
        match os with
        | Windows -> [sprintf "lib%s.dll" stem; sprintf "%s.dll" stem]
        | Linux -> [sprintf "lib%s.so" stem]
        | MacOS -> [sprintf "lib%s.dylib" stem]
    let patterns =
        match os with
        | Windows -> [sprintf "*%s*.dll" stem]
        | Linux -> [sprintf "lib%s.so.*" stem]
        | MacOS -> [sprintf "lib%s.*.dylib" stem]
    let tryDir (d: string) =
        let dir = Path.Combine(prefix, d)
        if not (Directory.Exists dir) then None
        else
            match exactNames |> List.map (fun n -> Path.Combine(dir, n)) |> List.tryFind File.Exists with
            | Some p -> Some p
            | None ->
                patterns
                |> List.tryPick (fun pat ->
                    try Directory.GetFiles(dir, pat) |> Array.sort |> Array.tryHead
                    with _ -> None)
    searchDirs |> List.tryPick tryDir
