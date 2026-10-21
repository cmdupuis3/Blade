// blade.toolchain.json -- the durable, OS-neutral record of where this
// machine keeps Blade's native toolchain (docs/plans/plan-toolchain-packaging.md).
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

/// The reading of an on/off switch. ONE grammar for every BLADE_* boolean
/// knob, trimmed and case-insensitive: `1` / `on` / `true` is on, `0` / `off`
/// / `false` is off, unset or empty (or all blanks) is unset, and anything
/// else is `SwitchOther` with the trimmed text, for the knobs that also take
/// a value (a cache directory). The knobs used to parse independently --
/// three accepted only an exact `1` or `on`, the cache knobs trimmed and
/// accepted `true`, and BLADE_MEMCHECK read anything but `0` as ON, so
/// `BLADE_MEMCHECK=off` turned memcheck on.
type Switch =
    | SwitchOn
    | SwitchOff
    | SwitchUnset
    | SwitchOther of string

/// Parse one switch value (`None` = unset).
let parseSwitch (value: string option) : Switch =
    match value with
    | None -> SwitchUnset
    | Some v ->
        let t = v.Trim()
        match t.ToLowerInvariant() with
        | "" -> SwitchUnset
        | "1" | "on" | "true" -> SwitchOn
        | "0" | "off" | "false" -> SwitchOff
        | _ -> SwitchOther t

/// The switch named `name`, read from the process environment PER CALL (never
/// cached: harnesses pin these mid-process).
let envSwitch (name: string) : Switch =
    parseSwitch (match Environment.GetEnvironmentVariable name with null -> None | v -> Some v)

/// A boolean knob: on or off as spelled, else `defaultOn` -- an unset knob
/// AND an unrecognized value both read as the default, so a typo never
/// flips a default-off knob on (or a default-on pass off).
let envFlag (name: string) (defaultOn: bool) : bool =
    match envSwitch name with
    | SwitchOn -> true
    | SwitchOff -> false
    | SwitchUnset | SwitchOther _ -> defaultOn

/// Does THIS BUILD keep the serial association of every fold
/// (`BLADE_FP_REPRO=1|on`, or the compatibility spelling `BLADE_FP_REASSOC=0|off`)?
/// The canonical predicate; `Blade.CodeGenState.fpReproEnabled` re-exports it
/// (its doc comment states the policy). It lives here so the optimizer
/// (which compiles before the emitters) reads the SAME predicate. Per call,
/// never cached.
let fpReproEnabled () : bool =
    envFlag "BLADE_FP_REPRO" false
    || envSwitch "BLADE_FP_REASSOC" = SwitchOff

/// One key of BLADE_TILE_TUNE, the SPEED-ONLY parameter list (`key=value`
/// pairs separated by commas, spaces or semicolons; unknown keys ignored, a
/// malformed value = `dflt`). The one parser: the emitters' keys
/// (`CodeGenExprSupport.tileTune`, which documents them) and the optimizer's
/// (`sel_cap`, Optimize.compactionSelCapBytes) both read it here. Per call,
/// never cached.
let tileTune (key: string) (dflt: int64) : int64 =
    match Environment.GetEnvironmentVariable "BLADE_TILE_TUNE" with
    | null -> dflt
    | v ->
        v.Split([| ','; ' '; ';' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.tryPick (fun kv ->
            match kv.Split('=') with
            | [| k; x |] when k.Trim().ToLowerInvariant() = key ->
                match Int64.TryParse(x.Trim()) with
                | true, n -> Some n
                | _ -> None
            | _ -> None)
        |> Option.defaultValue dflt

/// The threaded reduction join (bench gap G7, `CodeGenFusion.joinedFoldThreadPlan`):
/// `BLADE_JOIN_THREADS` unset / `1` / `on` = an explicitly requested, fully
/// licensed `<&!>` join opens one Path B team over its shared traversal;
/// `0` / `off` = today's serial (vectorized) join, for A/B. Emission-only.
/// Lives here so the sibling-fold pass (which compiles before the emitters)
/// reads the SAME predicate. Per call, never cached.
let joinThreadsEnabled () : bool =
    envFlag "BLADE_JOIN_THREADS" true

/// Does this build emit thread-level OpenMP constructs? `BLADE_OMP_THREADS`
/// is a THREAD COUNT with off-words: `1` (one thread) suppresses exactly as
/// `0` / `off` / `false` do, while `on` / `true` are no count at all and
/// leave emission on; unset = on. The canonical predicate;
/// `Blade.CodeGenState.ompThreadEmissionEnabled` re-exports it (its doc
/// comment states the policy). Per call, never cached.
let ompThreadEmissionEnabled () : bool =
    match envSwitch "BLADE_OMP_THREADS" with
    | SwitchOff -> false
    | SwitchOn ->
        // `1` reads as SwitchOn in the shared grammar; here it is the count 1.
        (Environment.GetEnvironmentVariable "BLADE_OMP_THREADS").Trim() <> "1"
    | _ -> true
