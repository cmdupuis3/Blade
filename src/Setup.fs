// `blade setup` -- non-interactive environment bootstrap
// (docs/plans/plan-toolchain-packaging.md, Phase "setup").
//
// Every mode ends the same way: apply the candidate configuration to THIS
// process's environment, run the doctor's real compile+run probes against
// it, and only persist to blade.toolchain.json (Toolchain.activePath: the
// exact file the reader consults) when the probe verdict says the config
// works. Setup can therefore never record a configuration the compiler
// would not actually get -- the same no-two-copies rule the BLAS gate and
// its expansion follow.
//
// Modes (BLAS resolution is the configurable half; NetCDF/MPI/CUDA are
// probed and REPORTED but installed by the user or their package manager):
//
//   blade setup                     probe, then persist the env-supplied
//                                   config keys that are currently in force
//                                   (make today's working env durable)
//   blade setup --blas=source       clone OpenBLAS at the deps.json pin and
//                                   make/make-install it under --tools
//                                   (default ~/.blade/tools); idempotent,
//                                   --force rebuilds
//   blade setup --blas=prebuilt     point at an existing install:
//                                   --blas-dir DIR (an OpenBLAS prefix), or
//                                   the fully explicit --blas-include/
//                                   --blas-link/--lapack-link/--flavor
//                                   (the MKL/BLIS door)
//   blade setup --blas=system       verify bare -lopenblas on the default
//                                   search paths (package-manager install),
//                                   persist BLADE_BLAS=1
//   blade setup --blas=none         remove BLAS keys (tier falls to off)
//
// Long-running children (git clone, make) inherit the console -- their
// progress streams live rather than being captured and dumped.
//
// Compiled after Doctor.fs (verification) and before Cli.fs (dispatch).
module Blade.Setup

open System
open System.IO
open System.Diagnostics

// ---- options ----

type BlasMode =
    | BlasUnspecified
    | BlasSource
    | BlasPrebuilt
    | BlasSystem
    | BlasNoneMode

type SetupOptions = {
    Blas : BlasMode
    Tools : string option        // --tools DIR (source-build prefix root)
    Force : bool                 // --force (rebuild even if installed)
    Jobs : int option            // --jobs N (make parallelism)
    BlasDir : string option      // --blas-dir DIR (prebuilt: OpenBLAS prefix)
    BlasInclude : string option  // --blas-include DIRS
    BlasLink : string option     // --blas-link ARGS
    LapackLink : string option   // --lapack-link ARGS
    LapackInclude : string option// --lapack-include DIRS
    Flavor : string option       // --flavor openblas|mkl|generic
}

let private defaults = {
    Blas = BlasUnspecified; Tools = None; Force = false; Jobs = None
    BlasDir = None; BlasInclude = None; BlasLink = None
    LapackLink = None; LapackInclude = None; Flavor = None
}

/// Accepts both `--key=value` and `--key value`. Pure; unit-tested.
let parseArgs (argv: string list) : Result<SetupOptions, string> =
    // Normalize `--key=value` into `--key value` first, so one loop handles both.
    let normalized =
        argv
        |> List.collect (fun a ->
            if a.StartsWith "--" && a.Contains "=" then
                let i = a.IndexOf '='
                [a.Substring(0, i); a.Substring(i + 1)]
            else [a])
    let rec go acc args =
        match args with
        | [] -> Ok acc
        | "--blas" :: v :: tl ->
            (match v with
             | "source" -> go { acc with Blas = BlasSource } tl
             | "prebuilt" -> go { acc with Blas = BlasPrebuilt } tl
             | "system" -> go { acc with Blas = BlasSystem } tl
             | "none" -> go { acc with Blas = BlasNoneMode } tl
             | other -> Error $"--blas expects source|prebuilt|system|none, got '{other}'")
        | "--tools" :: v :: tl -> go { acc with Tools = Some v } tl
        | "--force" :: tl -> go { acc with Force = true } tl
        | "--jobs" :: v :: tl ->
            (match Int32.TryParse v with
             | true, n when n > 0 -> go { acc with Jobs = Some n } tl
             | _ -> Error $"--jobs expects a positive integer, got '{v}'")
        | "--blas-dir" :: v :: tl -> go { acc with BlasDir = Some v } tl
        | "--blas-include" :: v :: tl -> go { acc with BlasInclude = Some v } tl
        | "--blas-link" :: v :: tl -> go { acc with BlasLink = Some v } tl
        | "--lapack-link" :: v :: tl -> go { acc with LapackLink = Some v } tl
        | "--lapack-include" :: v :: tl -> go { acc with LapackInclude = Some v } tl
        | "--flavor" :: v :: tl ->
            (match v.ToLowerInvariant() with
             | "openblas" | "mkl" | "generic" -> go { acc with Flavor = Some (v.ToLowerInvariant()) } tl
             | other -> Error $"--flavor expects openblas|mkl|generic, got '{other}'")
        | [("--blas" | "--tools" | "--jobs" | "--blas-dir" | "--blas-include" | "--blas-link" | "--lapack-link" | "--lapack-include" | "--flavor") as flag] ->
            Error $"'{flag}' requires a value"
        | other :: _ -> Error $"unexpected argument '{other}'"
    go defaults normalized

// ---- per-OS install-command data (control flow stays OS-free) ----

/// The package-manager one-liner for a dependency, per OS. Data, not logic;
/// printed as a hint, never executed.
let packageHint (dep: string) : string =
    let table =
        match Platforms.os with
        | Platforms.Windows ->
            [ "openblas", "pacman -S mingw-w64-ucrt-x86_64-openblas   (MSYS2 UCRT64 shell)"
              "make",     "pacman -S make gcc-fortran mingw-w64-ucrt-x86_64-gcc-fortran   (MSYS2)"
              "netcdf",   "pacman -S mingw-w64-ucrt-x86_64-netcdf   (MSYS2 UCRT64 shell)"
              "mpi",      "install MS-MPI (msmpisetup.exe) + pacman -S mingw-w64-ucrt-x86_64-msmpi" ]
        | Platforms.Linux ->
            [ "openblas", "sudo apt install libopenblas-dev   (or: dnf install openblas-devel)"
              "make",     "sudo apt install make gfortran git"
              "netcdf",   "sudo apt install libnetcdf-dev"
              "mpi",      "sudo apt install libopenmpi-dev openmpi-bin" ]
        | Platforms.MacOS ->
            [ "openblas", "brew install openblas"
              "make",     "xcode-select --install && brew install gfortran"
              "netcdf",   "brew install netcdf"
              "mpi",      "brew install open-mpi" ]
    table |> List.tryFind (fun (k, _) -> k = dep) |> Option.map snd
    |> Option.defaultValue $"(no {dep} install hint for this OS)"

/// Default source-build root: ~/.blade/tools (OS-independent spelling).
let private toolsRoot (opts: SetupOptions) : string =
    match opts.Tools with
    | Some d -> d
    | None -> Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".blade", "tools")

// ---- deps.json (the pin manifest, shipped beside the binary) ----

/// The OpenBLAS git url + tag from deps.json. Setup reads the manifest so
/// there is exactly one copy of the pin.
let private openblasPin () : Result<string * string, string> =
    let path = Path.Combine(AppContext.BaseDirectory, "deps.json")
    try
        if not (File.Exists path) then Error $"deps.json not found beside the binary ({path})"
        else
            use doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText path)
            let deps = doc.RootElement.GetProperty("dependencies")
            let entry =
                deps.EnumerateArray()
                |> Seq.tryFind (fun e -> e.GetProperty("name").GetString() = "openblas")
            match entry with
            | Some e -> Ok (e.GetProperty("url").GetString(), e.GetProperty("tag").GetString())
            | None -> Error "deps.json has no 'openblas' entry"
    with ex -> Error $"deps.json unreadable: {ex.Message}"

// ---- toolchain.json writing ----

let private jsonEscape (s: string) =
    s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t")

let private readToolchainFile (path: string) : Map<string, string> =
    try
        if not (File.Exists path) then Map.empty
        else
            use doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText path)
            if doc.RootElement.ValueKind <> System.Text.Json.JsonValueKind.Object then Map.empty
            else
                doc.RootElement.EnumerateObject()
                |> Seq.choose (fun p ->
                    if p.Value.ValueKind = System.Text.Json.JsonValueKind.String
                    then Some (p.Name, p.Value.GetString())
                    else None)
                |> Map.ofSeq
    with _ -> Map.empty

let private applyUpdates (baseMap: Map<string, string>) (updates: (string * string option) list) : Map<string, string> =
    updates
    |> List.fold (fun (m: Map<string, string>) (k, v) ->
        match v with
        | Some value -> Map.add k value m
        | None -> Map.remove k m) baseMap

let private writeMapTo (path: string) (m: Map<string, string>) : unit =
    let body =
        m
        |> Map.toList
        |> List.map (fun (k, v) -> $"  \"{jsonEscape k}\": \"{jsonEscape v}\"")
        |> String.concat ",\n"
    File.WriteAllText(path, "{\n" + body + "\n}\n")

/// Merge `updates` into the toolchain file at Toolchain.activePath and
/// refresh the reader cache. `None` removes a key. Existing keys not named
/// in `updates` survive -- setup composes with hand-edits.
let writeToolchain (updates: (string * string option) list) : string =
    let path = Toolchain.activePath ()
    writeMapTo path (applyUpdates (readToolchainFile path) updates)
    Toolchain.refresh ()
    path

// ---- process running (console-inherited, for git/make) ----

/// Run a tool with the console inherited so its progress streams live.
/// No timeout: these are user-visible long builds, interruptible by ^C.
let private runLive (dir: string option) (exe: string) (args: string) : Result<unit, string> =
    try
        let psi = ProcessStartInfo(exe, args)
        psi.UseShellExecute <- false
        match dir with Some d -> psi.WorkingDirectory <- d | None -> ()
        printfn "  > %s %s%s" exe args (match dir with Some d -> $"   (in {d})" | None -> "")
        use proc = Process.Start(psi)
        proc.WaitForExit()
        if proc.ExitCode = 0 then Ok ()
        else Error $"{exe} exited with {proc.ExitCode}"
    with ex -> Error $"{exe} failed to start: {ex.Message}"

// ---- verification (doctor is the arbiter) ----

let private printDoctorTable (checks: Doctor.CheckResult list) =
    for c in checks do
        printfn "  [%s] %-14s %s" (match c.Status with
                                   | Doctor.StatusOk -> " OK "
                                   | Doctor.StatusOff -> "OFF "
                                   | Doctor.StatusWarn -> "WARN"
                                   | Doctor.StatusMissing -> "MISS"
                                   | Doctor.StatusError -> "FAIL") c.Title c.Detail

/// Verify-then-persist: the shared tail of every configuring mode. Returns
/// the exit code. Persists ONLY when the blas row verdict is Ok.
///
/// The candidate is probed in ISOLATION: a `None` in `updates` must shadow
/// a file-supplied key too (a toolchain.json OPENBLAS_DIR would otherwise
/// leak into a --blas=system probe and the wrong tier would be judged), so
/// the candidate configuration -- current file contents plus the same
/// updates -- is materialized as its own temp toolchain file and
/// BLADE_TOOLCHAIN_FILE points there for the probe's duration. The process
/// env gets the same updates, so neither source can resurrect a removed key.
let private verifyAndPersist (updates: (string * string option) list) (persistKeys: (string * string option) list) : int =
    let priorFileVar = Environment.GetEnvironmentVariable "BLADE_TOOLCHAIN_FILE"
    let realPath = Toolchain.activePath ()
    let candidatePath =
        Path.Combine(Path.GetTempPath(), $"blade_setup_candidate_{Process.GetCurrentProcess().Id}.json")
    writeMapTo candidatePath (applyUpdates (readToolchainFile realPath) updates)
    for (k, v) in updates do
        Environment.SetEnvironmentVariable(k, (match v with Some s -> s | None -> null))
    Environment.SetEnvironmentVariable("BLADE_TOOLCHAIN_FILE", candidatePath)
    Toolchain.refresh ()
    let checks = Doctor.collectChecks ()
    let blasRow = checks |> List.find (fun c -> c.Key = "blas")
    Environment.SetEnvironmentVariable("BLADE_TOOLCHAIN_FILE", priorFileVar)
    Toolchain.refresh ()
    (try File.Delete candidatePath with _ -> ())
    printDoctorTable checks
    printfn ""
    match blasRow.Status with
    | Doctor.StatusOk ->
        let path = writeToolchain persistKeys
        printfn "verified; persisted to %s" path
        0
    | _ ->
        printfn "NOT persisted: the BLAS probe did not pass (%s)" blasRow.Detail
        1

// ---- modes ----

/// Bare `blade setup`: make today's working env durable. Persists the
/// configuration keys that are currently supplied BY ENV (getWithOrigin =
/// FromEnv) -- the machine facts that would vanish with the shell -- and
/// prints the doctor table so their health is visible. Nothing is invented:
/// keys the env does not carry are left untouched.
let private setupPersistCurrent () : int =
    let keys =
        [ "OPENBLAS_DIR"; "NETCDF_DIR"; "MSMPI_BIN"; "BLADE_BLAS"
          "BLADE_BLAS_LINK"; "BLADE_BLAS_INCLUDE"; "BLADE_LAPACK_LINK"
          "BLADE_LAPACK_INCLUDE"; "BLADE_BLAS_FLAVOR" ]
    let fromEnv =
        keys
        |> List.choose (fun k ->
            match Toolchain.getWithOrigin k with
            | Some (v, Toolchain.FromEnv) -> Some (k, Some v)
            | _ -> None)
    let checks = Doctor.collectChecks ()
    printDoctorTable checks
    printfn ""
    if fromEnv.IsEmpty then
        printfn "no env-supplied toolchain keys to persist (file untouched: %s)" (Toolchain.activePath ())
    else
        let path = writeToolchain fromEnv
        printfn "persisted %s to %s"
            (fromEnv |> List.map fst |> String.concat ", ") path
    if Doctor.isHealthy checks then 0 else 1

let private setupNone () : int =
    let path =
        writeToolchain
            [ "BLADE_BLAS", None; "BLADE_BLAS_LINK", None; "BLADE_BLAS_INCLUDE", None
              "BLADE_LAPACK_LINK", None; "BLADE_LAPACK_INCLUDE", None
              "BLADE_BLAS_FLAVOR", None; "OPENBLAS_DIR", None ]
    printfn "BLAS keys removed from %s (tier falls to off; Blade emits its own loops)" path
    0

let private setupSystem () : int =
    printfn "verifying bare -lopenblas on the default search paths..."
    let code =
        verifyAndPersist
            [ "BLADE_BLAS", Some "1"; "BLADE_BLAS_LINK", None; "OPENBLAS_DIR", None ]
            [ "BLADE_BLAS", Some "1" ]
    if code <> 0 then
        printfn "install it with:  %s" (packageHint "openblas")
    code

let private setupPrebuilt (opts: SetupOptions) : int =
    match opts.BlasDir, opts.BlasLink with
    | Some dir, _ ->
        // An OpenBLAS-shaped prefix: the OPENBLAS_DIR tier.
        if not (Directory.Exists dir) then
            eprintfn "Error: --blas-dir %s does not exist" dir
            1
        else
            verifyAndPersist
                [ "OPENBLAS_DIR", Some dir; "BLADE_BLAS_LINK", None; "BLADE_BLAS", None ]
                [ "OPENBLAS_DIR", Some dir ]
    | None, Some link ->
        // Fully explicit: the vendor-neutral tier (MKL, BLIS, ...).
        let sets =
            [ "BLADE_BLAS_LINK", Some link
              "BLADE_BLAS_INCLUDE", opts.BlasInclude
              "BLADE_LAPACK_LINK", opts.LapackLink
              "BLADE_LAPACK_INCLUDE", opts.LapackInclude
              "BLADE_BLAS_FLAVOR", opts.Flavor
              "OPENBLAS_DIR", None; "BLADE_BLAS", None ]
        let persists = sets |> List.filter (fun (k, _) -> k <> "OPENBLAS_DIR" && k <> "BLADE_BLAS")
        verifyAndPersist sets persists
    | None, None ->
        eprintfn "Error: --blas=prebuilt needs --blas-dir DIR (an OpenBLAS prefix) or --blas-link ARGS [--blas-include DIRS --lapack-link ARGS --flavor mkl|generic]"
        1

let private setupSource (opts: SetupOptions) : int =
    match openblasPin () with
    | Error e -> eprintfn "Error: %s" e; 1
    | Ok (url, tag) ->
        let root = toolsRoot opts
        let srcDir = Path.Combine(root, "OpenBLAS-src")
        let prefix = Path.Combine(root, "openblas")
        let installed = (Platforms.findSharedLib prefix "openblas").IsSome
        if installed && not opts.Force then
            printfn "OpenBLAS already installed at %s (--force to rebuild); verifying..." prefix
            verifyAndPersist
                [ "OPENBLAS_DIR", Some prefix; "BLADE_BLAS_LINK", None; "BLADE_BLAS", None ]
                [ "OPENBLAS_DIR", Some prefix ]
        else
        // Tool preconditions, reported all at once with install hints.
        let missing =
            [ (if Build.probeTool "git" "--version" then None else Some "git")
              (if Build.probeTool "make" "--version" then None else Some "make") ]
            |> List.choose id
        if not missing.IsEmpty then
            eprintfn "Error: --blas=source needs %s on PATH" (String.concat " and " missing)
            eprintfn "  %s" (packageHint "make")
            1
        else
        let hasFortran = Build.probeTool "gfortran" "--version"
        if not hasFortran then
            printfn "WARNING: gfortran not found -- building with NOFORTRAN=1 (no LAPACK half; eigh/solve stay on the synthesized Jacobi path)"
            printfn "  for the full build: %s" (packageHint "make")
        Directory.CreateDirectory(root) |> ignore
        let cloneStep =
            if Directory.Exists srcDir then
                printfn "source already cloned at %s" srcDir
                Ok ()
            else
                runLive None "git" $"clone --depth 1 --branch {tag} {url} \"{srcDir}\""
        let result =
            cloneStep
            |> Result.bind (fun () ->
                let jobs = opts.Jobs |> Option.defaultValue Environment.ProcessorCount
                let fortranFlag = if hasFortran then "" else " NOFORTRAN=1"
                runLive (Some srcDir) "make" $"-j{jobs}{fortranFlag}")
            |> Result.bind (fun () ->
                runLive (Some srcDir) "make" $"install PREFIX=\"{prefix}\"")
        match result with
        | Error e ->
            eprintfn "Error: OpenBLAS source build failed: %s" e
            1
        | Ok () ->
            verifyAndPersist
                [ "OPENBLAS_DIR", Some prefix; "BLADE_BLAS_LINK", None; "BLADE_BLAS", None ]
                [ "OPENBLAS_DIR", Some prefix ]

let printSetupUsage () =
    printfn "Usage: blade setup [--blas=source|prebuilt|system|none] [options]"
    printfn ""
    printfn "  (no args)            probe, then persist the env-supplied config that is"
    printfn "                       currently in force to %s" (Toolchain.activePath ())
    printfn "  --blas=source        clone + build OpenBLAS from the deps.json pin"
    printfn "                       [--tools DIR] [--jobs N] [--force]  (needs git, make;"
    printfn "                       gfortran for the LAPACK half)"
    printfn "  --blas=prebuilt      use an existing install: --blas-dir DIR (OpenBLAS"
    printfn "                       prefix), or --blas-link ARGS [--blas-include DIRS]"
    printfn "                       [--lapack-link ARGS] [--lapack-include DIRS]"
    printfn "                       [--flavor openblas|mkl|generic]   (the MKL door)"
    printfn "  --blas=system        verify a package-manager -lopenblas; persist BLADE_BLAS=1"
    printfn "  --blas=none          remove BLAS configuration (Blade emits its own loops)"
    printfn ""
    printfn "Every configuring mode verifies with the doctor probes BEFORE persisting;"
    printfn "a config that does not compile+link+run is reported and not written."

/// The verb.
let runSetup (argv: string list) : int =
    if argv |> List.contains "--help" then
        printSetupUsage ()
        0
    else
    match parseArgs argv with
    | Error e ->
        eprintfn "Error: %s" e
        printSetupUsage ()
        1
    | Ok opts ->
        match opts.Blas with
        | BlasUnspecified -> setupPersistCurrent ()
        | BlasNoneMode -> setupNone ()
        | BlasSystem -> setupSystem ()
        | BlasPrebuilt -> setupPrebuilt opts
        | BlasSource -> setupSource opts
