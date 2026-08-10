// blade.toolchain.json -- the durable, OS-neutral record of where this
// machine keeps Blade's native toolchain (docs/plan-toolchain-packaging.md).
// Written by `blade setup` (future phase), read here by the configuration
// gates that historically read only process environment variables
// (BLAS/LAPACK tiers, NETCDF_DIR, MSMPI_BIN).
//
// Precedence per key: process env var > toolchain file > absent. The env
// half is LIVE -- re-read on every call, because the test harnesses pin
// BLADE_* variables mid-process and expect the next consultation to honor
// them (the same rule that keeps `blasAvailable` and `Build.optFlags`
// functions rather than module-level values). The file half is cached per
// path: `blade setup` writes the file and exits, so a running process never
// needs to observe a mid-run edit, and gates are consulted from codegen's
// routing decisions where per-call file IO would be waste.
//
// File location: $BLADE_TOOLCHAIN_FILE if set (tests point this at temp
// files; the per-path cache is what makes that work in-process), else
// blade.toolchain.json beside the executable (AppContext.BaseDirectory --
// the same anchor the C++ runtime headers deploy from).
//
// Shape: one flat JSON object with string values, keys spelled EXACTLY like
// the env vars they mirror ({"OPENBLAS_DIR": "...", "BLADE_BLAS": "1"}).
// One vocabulary, two sources -- documentation for either is documentation
// for both, and `blade doctor` can report a value's origin by comparing.
module Blade.Toolchain

open System
open System.IO
open System.Text.Json

/// Parse the flat string->string object. Non-string members are skipped and
/// malformed JSON yields the empty map: a broken toolchain file must degrade
/// to "unconfigured", never crash `blade check`.
let private parseFile (path: string) : Map<string, string> =
    try
        if not (File.Exists path) then Map.empty
        else
            use doc = JsonDocument.Parse(File.ReadAllText path)
            if doc.RootElement.ValueKind <> JsonValueKind.Object then Map.empty
            else
                doc.RootElement.EnumerateObject()
                |> Seq.choose (fun p ->
                    if p.Value.ValueKind = JsonValueKind.String
                    then Some (p.Name, p.Value.GetString())
                    else None)
                |> Map.ofSeq
    with _ -> Map.empty

let private fileCache =
    System.Collections.Concurrent.ConcurrentDictionary<string, Map<string, string>>()

/// The file consulted for THIS process: $BLADE_TOOLCHAIN_FILE if set, else
/// beside the executable. Public so `blade setup` writes exactly where the
/// reader reads, and doctor can name the file it consulted.
let activePath () : string =
    match Environment.GetEnvironmentVariable "BLADE_TOOLCHAIN_FILE" with
    | null | "" -> Path.Combine(AppContext.BaseDirectory, "blade.toolchain.json")
    | p -> p

/// Drop the per-path parse cache. `blade setup` calls this after writing the
/// file (the cache exists because setup normally writes and EXITS; setup
/// itself is the one process that edits mid-run), as do tests.
let refresh () : unit =
    fileCache.Clear()

let private fileValues () : Map<string, string> =
    fileCache.GetOrAdd(activePath (), parseFile)

/// Which source supplied a configuration value -- `blade doctor` reports
/// this so a stale toolchain.json shadowed by an env var (or vice versa) is
/// visible instead of mysterious.
type Origin =
    | FromEnv
    | FromFile

/// Env-first lookup with provenance. Empty strings count as unset on BOTH
/// sides, matching the `null | ""` convention every existing env gate
/// already used.
let getWithOrigin (key: string) : (string * Origin) option =
    match Environment.GetEnvironmentVariable key with
    | null | "" ->
        fileValues ()
        |> Map.tryFind key
        |> Option.filter (fun v -> v <> "")
        |> Option.map (fun v -> (v, FromFile))
    | v -> Some (v, FromEnv)

/// Env-first lookup (the common form; provenance dropped).
let get (key: string) : string option =
    getWithOrigin key |> Option.map fst
