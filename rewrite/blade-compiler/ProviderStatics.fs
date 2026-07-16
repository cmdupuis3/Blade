/// Provider-backed statics: the bridge that lets
/// `let static A = alias.read(sample.vars.A)` FOLD the store's payload at
/// compile time — staging contract clause 1 ("inputs are immutable values
/// within a program's scope; fold freely"). Layering: the providers compile
/// before StaticEval and neither may reference the other, so the reader is
/// REGISTERED into StaticEval's provider hook from here (installed by
/// TypeCheck.typeCheck, ahead of every resolveStatics pass — the PPL
/// elaboration's own statics inherit the fold for free).
///
/// This module is also the registry install point: each provider's
/// ProviderSpec (Blade.ProviderRegistry) is assembled/registered here, and
/// the registered name set is bridged into StaticEval so resolveStatics can
/// recognize `import netcdf as nc`-style provider imports.
///
/// Operational honesty is PROVENANCE, not freshness: each fold records
/// (path, variable, sha256) — "this executable = program · store@hash" —
/// and prints a provenance note at compile time. The hash log is also the
/// future memoization key for incremental compile-time folds.
module Blade.ProviderStatics

open Blade.StaticEval

/// (store path, variable name, sha256 hex) per compile-time fold.
let provenance = ResizeArray<string * string * string>()

let private hashCache = System.Collections.Concurrent.ConcurrentDictionary<string, string>()

/// SHA256 hex of a single file's bytes, memoized (the NetCDF fingerprint).
let private fileHash (path: string) : string =
    hashCache.GetOrAdd(path, fun p ->
        use sha = System.Security.Cryptography.SHA256.Create()
        sha.ComputeHash(System.IO.File.ReadAllBytes p)
        |> Array.map (sprintf "%02x")
        |> String.concat "")

/// Fold ceiling in elements. Beyond it the fold refuses with steering:
/// "large and closed" inputs belong to the runtime/streaming schedule
/// (a multi-million-element C++ literal would sink the C++ compiler),
/// per the fold/residualize/stream table in ppl/NOTES.md.
let foldCeiling = 65536

/// Shape a flat row-major buffer into nested SVTuples by dim extents
/// (a rank-0 variable folds to its bare scalar).
let shapeValue (lens: int list) (leaf: int -> StaticValue) : StaticValue =
    let rec go (lens: int list) (offset: int) : StaticValue * int =
        match lens with
        | [] -> (leaf offset, offset + 1)
        | n :: rest ->
            let mutable off = offset
            let items =
                [ for _ in 1 .. n ->
                    let (v, off') = go rest off
                    off <- off'
                    v ]
            (SVTuple items, off)
    fst (go lens 0)

// ============================================================================
// NetCDF ProviderSpec (surface module name: "netcdf")
// ============================================================================
// Assembled here from NetcdfProvider's public functions so the provider
// implementation file needs no registry knowledge of its own.

let private netcdfAdapt (d: Blade.NetcdfProvider.NcVarData) : Blade.ProviderRegistry.ProviderVarData =
    { DimLengths = d.DimLengths
      Payload =
        match d.Payload with
        | Blade.NetcdfProvider.NcFloats xs -> Blade.ProviderRegistry.PFloats xs
        | Blade.NetcdfProvider.NcInts xs -> Blade.ProviderRegistry.PInts xs }

let netcdfSpec : Blade.ProviderRegistry.ProviderSpec = {
    Name = "netcdf"
    LoadAsModule = Blade.NetcdfProvider.loadAsModule
    ReadVarData = fun path varName ->
        Blade.NetcdfProvider.readVarData path varName |> Result.map netcdfAdapt
    GenReadVar = Blade.NetcdfProvider.CppNetcdf.genReadVar
    GenReadPacked = None  // packed (SymIdx/AntisymIdx) NetCDF I/O: future arc
    GenReadCompoundVar = Some Blade.NetcdfProvider.CppNetcdf.genReadCompoundVar
    GenWriteVar = Blade.NetcdfProvider.CppNetcdf.genWriteVar
    GenStreamOpen = Some Blade.NetcdfProvider.CppNetcdf.genStreamOpen
    GenStreamFiber = Some Blade.NetcdfProvider.CppNetcdf.genStreamFiber
    Includes = Blade.NetcdfProvider.CppNetcdf.genIncludes
    VarDimNames = fun path varName ->
        try
            let file = Blade.NetcdfProvider.load path
            file.Vars
            |> List.tryFind (fun v -> v.Name = varName)
            |> Option.map (fun v -> v.Dims |> List.map (fun d -> d.Name))
        with _ -> None
    Fingerprint = fileHash
    VersionStamp = fun path ->
        try System.IO.File.GetLastWriteTimeUtc(path).Ticks
        with _ -> 0L
    LinkNeeds = "libnetcdf (NETCDF_DIR)"
}

// ============================================================================
// Provider-neutral fold bridge
// ============================================================================

/// Fold memoization: the pipeline runs several resolveStatics passes per
/// compilation (checkModule, the ML and PPL elaborations, lowering's
/// Phase 0) — the payload is read and provenance recorded ONCE. Keyed on
/// (provider, path, var, versionStamp) so a long-lived compiler process
/// re-reads when the store actually changed between compilations.
let private foldCache =
    System.Collections.Concurrent.ConcurrentDictionary<string * string * string * int64, Result<StaticValue, string>>()

let private readAndFoldUncached (provider: string) (path: string) (varName: string) : Result<StaticValue, string> =
    match Blade.ProviderRegistry.tryFind provider with
    | None ->
        Error (sprintf "provider '%s' is not registered — was ProviderStatics.install () run?" provider)
    | Some spec ->
        match spec.ReadVarData path varName with
        | Error e ->
            Error (sprintf "provider fold of '%s' from '%s' failed: %s" varName path e)
        | Ok data ->
            let count = data.DimLengths |> List.fold (*) 1
            if count > foldCeiling then
                Error (sprintf "'%s' has %d elements — beyond the %d-element fold ceiling; large closed inputs take the runtime schedule (bind with a plain `let ... |> %s.read`)" varName count foldCeiling provider)
            else
                let h = spec.Fingerprint path
                provenance.Add((path, varName, h))
                eprintfn "[provenance] folded %s from %s@%s" varName path (h.Substring(0, min 12 h.Length))
                match data.Payload with
                | Blade.ProviderRegistry.PFloats xs -> Ok (shapeValue data.DimLengths (fun i -> SVFloat xs.[i]))
                | Blade.ProviderRegistry.PInts xs -> Ok (shapeValue data.DimLengths (fun i -> SVInt xs.[i]))

let private readAndFold (provider: string) (path: string) (varName: string) : Result<StaticValue, string> =
    let stamp =
        match Blade.ProviderRegistry.tryFind provider with
        | Some spec -> spec.VersionStamp path
        | None -> 0L
    foldCache.GetOrAdd((provider, path, varName, stamp), fun _ -> readAndFoldUncached provider path varName)

/// Idempotent installation: register every provider spec, then bridge the
/// compile-time reader and the provider-name set into StaticEval's hooks.
let install () =
    Blade.ProviderRegistry.register netcdfSpec
    Blade.ProviderRegistry.register Blade.ZarrProvider.spec
    registerProviderReader readAndFold
    registerProviderNames (Blade.ProviderRegistry.names () |> Set.ofList)
