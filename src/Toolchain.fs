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

/// BLADE_POOL_RETAIN -- the RUNTIME allocator's retention policy (round-3 A4,
/// bench gap G52), an OPT-IN arm beside the default, never the default (the
/// user's decision, 2026-10-21). Both lane runtimes keep a cache of freed
/// pools committed for the next request of the same size: the C++ lane's
/// `blade_pool` (nested_array_utilities.hpp, per THREAD, thread_local) and
/// the LLVM lane's shim allocator (blade_llvm_shim.c, plain globals: that lane
/// has no threads, so its one cache is the process's). The policy is two
/// numbers -- the largest block retained and the retained-bytes budget:
///
///   unset / `0` / `off`   -> the default, 4 MB blocks / 8 MB budget, and the
///                            emission is byte-for-byte what it was (no define);
///   `1` / `on`            -> the measured probe's retention: 64 MB / 96 MB;
///   `<maxMB>:<budgetMB>`  -> an explicit pair for A/B sweeps, whole MB with
///                            0 < max <= budget <= 1048576;
///   anything else         -> the DEFAULT, with a warning line on stderr and
///                            the plan line naming the malformed value.
///
/// A non-default policy reaches the C++ lane as `#define BLADE_POOL_MAX_BLOCK`
/// / `BLADE_POOL_BUDGET` lines at the top of the generated .cpp (so the exe
/// cache keys on it through the emitted text), and the LLVM lane as the same
/// two `-D` defines on the shim's compile, which enter the shim object's
/// identity hash and through it `llvmExeCacheKey`. Env first, then the
/// toolchain-file key of the same name (`blade setup` persists an env value).
///
/// HONESTY (what the arm is and is not): retention RELOCATES the release of a
/// large pool out of the timed region (to its reuse, an eviction, or the
/// thread's exit -- the main thread's at process exit); it is not less total
/// work. The budget is PER THREAD in the C++ lane, so T threads may retain
/// T x budget. And the process's peak working set can RISE under the arm
/// without any page reuse (a retained A stays resident while an unrelated B
/// allocates) -- or stay where it was. Per call, never cached.
type PoolRetention =
    { /// "default" (unset, `0`/`off`, or malformed) | "retain" (`1`/`on`) | "pair"
      Arm: string
      /// As the plan and the emitted comment spell it: "default" | "0" | "1" | "<max>:<budget>"
      Setting: string
      MaxBlockMB: int64
      BudgetMB: int64
      /// "env BLADE_POOL_RETAIN" | "blade.toolchain.json" | "default"
      Source: string
      /// The unrecognized value that was read as the default, if any.
      Malformed: string option }
    member p.IsDefault = p.Arm = "default"
    member p.MaxBlockBytes = p.MaxBlockMB <<< 20
    member p.BudgetBytes = p.BudgetMB <<< 20

/// The default policy (MB) -- the runtime headers' own constants, restated.
let poolRetainDefaultMB = (4L, 8L)
/// `BLADE_POOL_RETAIN=1`: the probe arm measured in wave 3 (G52).
let poolRetainArmMB = (64L, 96L)
/// The largest budget a pair may name (1 TiB): keeps every shift in range.
let poolRetainLimitMB = 1048576L

/// Parse one BLADE_POOL_RETAIN value (None = unset). Pure; `source` is
/// carried into the record.
let parsePoolRetention (value: string option) (source: string) : PoolRetention =
    let (dMax, dBud) = poolRetainDefaultMB
    let mk arm setting mx bud malformed =
        { Arm = arm; Setting = setting; MaxBlockMB = mx; BudgetMB = bud; Source = source; Malformed = malformed }
    let dflt setting malformed = mk "default" setting dMax dBud malformed
    match value |> Option.map (fun v -> v.Trim()) with
    | None | Some "" -> dflt "default" None
    | Some v ->
        match v.ToLowerInvariant() with
        | "0" | "off" | "false" -> dflt "0" None
        | "1" | "on" | "true" -> (let (m, b) = poolRetainArmMB in mk "retain" "1" m b None)
        | _ ->
            match v.Split(':') with
            | [| a; b |] ->
                match Int64.TryParse(a.Trim()), Int64.TryParse(b.Trim()) with
                | (true, mx), (true, bud) when mx > 0L && mx <= bud && bud <= poolRetainLimitMB ->
                    mk "pair" $"{mx}:{bud}" mx bud None
                | _ -> dflt "default" (Some v)
            | _ -> dflt "default" (Some v)

/// The policy in force for this compile: env BLADE_POOL_RETAIN, else the
/// toolchain-file key, else the default. Per call, never cached.
let poolRetention () : PoolRetention =
    match getWithOrigin "BLADE_POOL_RETAIN" with
    | Some (v, FromEnv) -> parsePoolRetention (Some v) "env BLADE_POOL_RETAIN"
    | Some (v, FromFile) -> parsePoolRetention (Some v) "blade.toolchain.json"
    | None -> parsePoolRetention None "default"

/// The preprocessor defines a NON-default policy compiles both runtimes with
/// (name, bytes); empty for the default, so the default build is untouched.
let poolRetentionDefines (p: PoolRetention) : (string * int64) list =
    if p.IsDefault then []
    else [ "BLADE_POOL_MAX_BLOCK", p.MaxBlockBytes; "BLADE_POOL_BUDGET", p.BudgetBytes ]

/// `blade plan`'s build line for the policy.
/// Which runtime holds the retained pools -- the lane that BUILDS the program
/// (`blade plan` passes the lane its own verdict selected; an LLVM refusal or
/// fallback is the C++ lane). It decides the budget's SCOPE: the C++ lane's
/// `blade_pool` is thread_local (one budget per thread), the LLVM shim's cache
/// is plain globals (one budget for the process; that lane has no threads).
type PoolLane =
    | PoolLaneCpp
    | PoolLaneLlvm

/// The budget scope as the plan JSON and the C++ run record spell it.
let poolBudgetScope (lane: PoolLane) : string =
    match lane with
    | PoolLaneCpp -> "per-thread"
    | PoolLaneLlvm -> "process"

/// `blade plan`'s build line for the policy in the lane that builds the
/// program. A VALID default (unset or `0`/`off`) prints no provenance; a
/// non-default policy names its setting and source; a malformed value keeps
/// its diagnostic.
let describePoolRetention (lane: PoolLane) (p: PoolRetention) : string =
    let scope = match lane with PoolLaneCpp -> "per thread" | PoolLaneLlvm -> "per process"
    if p.IsDefault then
        let (m, b) = poolRetainDefaultMB
        let laneNote = match lane with PoolLaneCpp -> "" | PoolLaneLlvm -> ", the LLVM lane's shim allocator"
        let why =
            match p.Malformed with
            | Some v -> $" -- BLADE_POOL_RETAIN='{v}' ({p.Source}) is not 1/on, 0/off or <maxMB>:<budgetMB> with 0 < max <= budget, read as the default"
            | None -> ""
        $"pool retention: default ({m} MB / {b} MB {scope}{laneNote}){why}"
    else
        let head = $"pool retention: BLADE_POOL_RETAIN={p.Setting} ({p.Source}) -> {p.MaxBlockMB} MB / {p.BudgetMB} MB {scope} "
        match lane with
        | PoolLaneCpp ->
            head
            + $"(opt-in arm, C++ lane blade_pool; a released pool of 16 KB .. {p.MaxBlockMB} MB stays committed until reuse, eviction, flush or thread exit, "
            + $"so T threads may retain T x {p.BudgetMB} MB and the peak working set can rise without any reuse; it moves the release out of the timed region, "
            + "it is not less work; the run record's policy.pool says which arm ran)"
        | PoolLaneLlvm ->
            head
            + $"(opt-in arm, LLVM lane shim allocator: ONE {p.BudgetMB} MB budget for the process, no threads; a released pool of 16 KB .. {p.MaxBlockMB} MB stays committed until reuse, eviction or the atexit flush, "
            + "so the peak working set can rise without any reuse; it moves the release out of the timed region, it is not less work; "
            + "this lane writes NO run record: this line and the shim object's identity, captured beside a measurement, are build evidence, not an observed runtime record)"

/// Coverage mode is shared by admission and rendering. A retained inline
/// must not be admitted when the instrumentation renderer owns its nest.
let private ompCoverageMode = System.Threading.AsyncLocal<bool ref>()
let ompCoverageModeCell () =
    if isNull (box ompCoverageMode.Value) then ompCoverageMode.Value <- ref false
    ompCoverageMode.Value
let setOmpCoverageMode on = ompCoverageMode.Value <- ref on

let private poolRetentionWarned = System.Collections.Concurrent.ConcurrentDictionary<string, bool>()

/// The warning line for a malformed value, once per value per process.
let warnPoolRetention (p: PoolRetention) : unit =
    match p.Malformed with
    | Some v when poolRetentionWarned.TryAdd(v, true) ->
        let (m, b) = poolRetainDefaultMB
        eprintfn "warning: BLADE_POOL_RETAIN='%s' (%s) is not 1/on, 0/off or <maxMB>:<budgetMB> with 0 < max <= budget <= %d; the default pool retention (%d MB / %d MB per thread) is used" v p.Source poolRetainLimitMB m b
    | _ -> ()
