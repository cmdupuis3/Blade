// Provider registry: the seam between the core compiler and data providers.
// A provider is a Blade-surface MODULE (`import netcdf as nc`) implemented
// under providers/; this registry reifies the provider contract (metadata
// load, compile-time fold read, runtime C++ read/write emission) as one
// record per provider, keyed by surface module name. Core files dispatch
// through `tryFind` rather than naming any provider directly; specs are
// registered by ProviderStatics.install () ahead of every pipeline pass.
// Compiles after IR.fs, before the provider implementations that construct
// their ProviderSpec against these types; StaticEval stays registry-free.
module Blade.ProviderRegistry

open Blade.IR
open Blade.Types

/// Provider-neutral compile-time payload for the static fold: dimension
/// extents plus the row-major flat buffer (float or int64 per elem type).
type ProviderVarData = {
    DimLengths: int list
    Payload: ProviderPayload
}
and ProviderPayload =
    | PFloats of float[]
    | PInts of int64[]

/// Options for a packed (SymIdx/AntisymIdx) read emission.
type PackedReadOpts = {
    /// Emit the MPI-distributed read: each rank does its balanced chunk of
    /// I/O, then MPI_Allgatherv restores the full pool on every rank. Set
    /// only with MPI scaffolding and an unwindowed read.
    Distribute: bool
    /// Sub-simplex window [lo, hi): materialize only cells with every
    /// coordinate in [lo, hi); the declared arrType is the WINDOW type.
    Window: (int64 * int64) option
}

/// THE PROVIDER BOUNDARY RULE: a missing value never crosses a provider
/// boundary disguised as a number. One rule per stored variable, computed by
/// the variable's provider from compile-time metadata (`ProviderSpec.
/// MissingRuleOf`), says which cells are missing:
///
///   * a FLOATING variable: NaN and +-Inf, always (every provider -- a NaN
///     written into a store whose fill is finite is refused too);
///   * NetCDF adds its sentinels: `_FillValue`, `missing_value`, the
///     `valid_min` / `valid_max` / `valid_range` bounds (inclusive), and for a
///     floating variable with no `_FillValue` the type's default fill
///     (NC_FILL_FLOAT / NC_FILL_DOUBLE, i.e. never-written cells). An integer
///     variable is refused only on an EXPLICIT attribute: NC_FILL_INT and
///     friends are representable data;
///   * Zarr / Icechunk / CSV add nothing: a finite fill_value is a value.
///
/// A default read (`read`, `stream`, `load_compound`, `read_window`) of a
/// variable holding a missing cell refuses with BL8015 in every lane: the
/// emitted C++ (blade_rt::refuse_missing over each reader's destination
/// buffer, from `missingCpp`), the interpreter, and the static fold (BL4002,
/// the `let static` assertion,
/// carrying the same sentence) -- all from THIS record, through `isMissing`
/// and `missingCpp`, so the predicates cannot drift. The opt-in is the
/// MASKED read (`alias.read_present` / `alias.present`, ProviderDesugar),
/// which reads the raw cells under `MissingPolicy.allowing` and derives the
/// presence mask in-language from ProviderDesugar's spelling of this rule.
type MissingRule = {
    /// NaN and +-Inf are missing (every floating-point variable).
    NonFinite: bool
    /// Finite sentinels, compared after widening to double (a Float32
    /// `_FillValue` compares as the double libnetcdf widens it to).
    Sentinels: float list
    /// Inclusive valid range: a cell below Lo or above Hi is missing.
    Lo: float option
    Hi: float option
    /// An INTEGER variable's sentinels and range, compared as int64 in both
    /// predicates: a double cannot tell NC_FILL_INT64 (-9223372036854775806)
    /// from its neighbours. A rule is either floating (the fields above) or
    /// integral (these), never both.
    IntSentinels: int64 list
    IntLo: int64 option
    IntHi: int64 option
    /// What makes a cell missing, for the message ("NaN or infinite;
    /// _FillValue -999.0").
    Describe: string
}

/// No cell is ever missing (integer CSV tables, integer Zarr arrays, an
/// integer NetCDF variable without a fill attribute).
let noMissingRule : MissingRule =
    { NonFinite = false; Sentinels = []; Lo = None; Hi = None
      IntSentinels = []; IntLo = None; IntHi = None; Describe = "" }

/// The floating-point floor of the rule: NaN and +-Inf.
let nonFiniteRule : MissingRule =
    { noMissingRule with NonFinite = true; Describe = "NaN or infinite" }

let isTrivialRule (r: MissingRule) =
    not r.NonFinite && r.Sentinels.IsEmpty && r.Lo.IsNone && r.Hi.IsNone
    && r.IntSentinels.IsEmpty && r.IntLo.IsNone && r.IntHi.IsNone

/// The F# predicate (interpreter, static fold).
let isMissing (r: MissingRule) (x: float) : bool =
    (r.NonFinite && not (System.Double.IsFinite x))
    || List.contains x r.Sentinels
    || (match r.Lo with Some lo -> x < lo | None -> false)
    || (match r.Hi with Some hi -> x > hi | None -> false)

/// The F# predicate over an INTEGER cell: the integral fields in int64,
/// then the floating ones (empty for an integer variable's rule).
let isMissingInt (r: MissingRule) (n: int64) : bool =
    List.contains n r.IntSentinels
    || (match r.IntLo with Some lo -> n < lo | None -> false)
    || (match r.IntHi with Some hi -> n > hi | None -> false)
    || isMissing r (float n)

/// An int64 as a C++ literal (INT64_MIN has no literal spelling).
let int64Cpp (n: int64) : string =
    if n = System.Int64.MinValue then "(-9223372036854775807LL - 1)" else $"{n}LL"

/// A double as a round-tripping literal (C++ and Blade source alike).
let ruleLiteral (f: float) : string =
    let s = f.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
    if s.Contains "." || s.Contains "E" || s.Contains "e" then s else s + ".0"

/// The C++ predicate over `x` (any arithmetic type), the twin of `isMissing`.
let missingCpp (r: MissingRule) (x: string) : string =
    let d = $"((double)({x}))"
    let terms =
        [ if r.NonFinite then yield $"!std::isfinite({d})"
          for s in r.Sentinels do yield $"{d} == {ruleLiteral s}"
          match r.Lo with Some lo -> yield $"{d} < {ruleLiteral lo}" | None -> ()
          match r.Hi with Some hi -> yield $"{d} > {ruleLiteral hi}" | None -> ()
          let i = $"((long long)({x}))"
          for s in r.IntSentinels do yield $"{i} == {int64Cpp s}"
          match r.IntLo with Some lo -> yield $"{i} < {int64Cpp lo}" | None -> ()
          match r.IntHi with Some hi -> yield $"{i} > {int64Cpp hi}" | None -> () ]
    match terms with
    | [] -> "false"
    | ts -> "(" + String.concat " || " ts + ")"

/// The refusal sentence, shared by the interpreter, the static fold and
/// (character for character) blade_rt::missing_report.
let missingMessage (var: string) (path: string) (r: MissingRule) (count: int64) (first: int64 list) : string =
    let coords = first |> List.map string |> String.concat ", "
    $"provider read of '{var}' from '{path}': {count} cell(s) hold a missing value (first at [{coords}]; missing here: {r.Describe}) -- read it with .read_present for the present cells as a compound array, or .present for the presence mask"

/// Scan a row-major payload of shape `dims`: None, or the refusal sentence.
let checkPayload (r: MissingRule) (var: string) (path: string) (dims: int list) (payload: ProviderPayload) : string option =
    if isTrivialRule r then None
    else
        let mutable count = 0L
        let mutable first = -1L
        let note (i: int) (x: float) =
            if isMissing r x then
                if first < 0L then first <- int64 i
                count <- count + 1L
        (match payload with
         | PFloats xs -> xs |> Array.iteri note
         | PInts xs ->
             xs |> Array.iteri (fun i n ->
                 if isMissingInt r n then
                     if first < 0L then first <- int64 i
                     count <- count + 1L))
        if count = 0L then None
        else
            let mutable rem = first
            let coords =
                [ for n in List.rev dims ->
                    let n = int64 n
                    if n > 0L then
                        let c = rem % n
                        rem <- rem / n
                        c
                    else 0L ]
                |> List.rev
            Some (missingMessage var path r count coords)

/// The masked read's escape from the rule: emitters consult `refusing ()`,
/// and codegen generates the hidden raw read of `alias.read_present` /
/// `alias.present` inside `allowing`. Ambient (AsyncLocal) rather than one
/// more argument on every Gen* signature: only that one hidden read ever
/// sets it, and it is scoped to the emission of that one binding. NOTHING
/// ELSE MAY SET IT: any other `allowing` caller would let a missing value
/// cross the boundary as a number. (The emitted text differs between the two
/// modes, so the executable cache keys on it for free.)
module MissingPolicy =
    let private allow = new System.Threading.AsyncLocal<bool>()
    let allowing (f: unit -> 'a) : 'a =
        let saved = allow.Value
        allow.Value <- true
        try f () finally allow.Value <- saved
    let refusing () = not allow.Value

/// A C++ string literal body.
let private cppStr (s: string) = s.Replace("\\", "\\\\").Replace("\"", "\\\"")

/// The refusal over a FILLED destination buffer `buf` of `count` cells whose
/// row-major shape is `ext` (C++ expressions), placed at `origin` inside the
/// variable, under fixed leading coordinates `lead`. Empty when the rule is
/// trivial or the read is the masked read's raw half.
let refuseScanCpp (r: MissingRule) (var: string) (path: string) (buf: string) (count: string)
                  (lead: string list) (origin: string list) (ext: string list) : string list =
    if isTrivialRule r || not (MissingPolicy.refusing ()) then []
    else
        let lst (xs: string list) = "{" + (xs |> List.map (fun e -> $"(long long)({e})") |> String.concat ", ") + "}"
        let pred = missingCpp r "__bm"
        [ $"blade_rt::refuse_missing({buf}, (size_t)({count}), [](auto __bm) {{ return {pred}; }}, \"{cppStr var}\", \"{cppStr path}\", \"{cppStr r.Describe}\", {lst lead}, {lst origin}, {lst ext});" ]

/// In-loop counting for readers that fill a destination out of row-major
/// order (chunked assembly): declare a tally, note each cell as it lands,
/// report after the loops. All three empty when the rule is inactive.
let missingActive (r: MissingRule) = not (isTrivialRule r) && MissingPolicy.refusing ()
let tallyDeclCpp (r: MissingRule) (v: string) : string list =
    if missingActive r then [ $"blade_rt::missing_tally {v}_miss;" ] else []
let tallyNoteCpp (r: MissingRule) (v: string) (value: string) (flatIdx: string) : string =
    if missingActive r then $" if ({missingCpp r value}) {v}_miss.note({flatIdx});" else ""
let tallyReportCpp (r: MissingRule) (v: string) (var: string) (path: string) (ext: string list) : string list =
    if missingActive r then
        let lst (xs: string list) = "{" + (xs |> List.map (fun e -> $"(long long)({e})") |> String.concat ", ") + "}"
        [ $"if ({v}_miss.count) blade_rt::missing_report({v}_miss.count, {v}_miss.first, \"{cppStr var}\", \"{cppStr path}\", \"{cppStr r.Describe}\", {{}}, {{}}, {lst ext});" ]
    else []

/// The provider contract. One record per provider, registered under the
/// surface module name ("netcdf", "zarr").
type ProviderSpec = {
    /// Surface module name and registry key: `import <Name> as <alias>`.
    Name: string
    /// Compile-time metadata -> IRModule (named index types + dims/vars
    /// structs). Args: builder, moduleName (receiving binding's name), store path.
    LoadAsModule: IRBuilder -> string -> string -> IRModule
    /// Compile-time whole-payload read for the static fold (args: store path, variable name).
    ReadVarData: string -> string -> Result<ProviderVarData, string>
    /// The variable's missing-value rule (the provider boundary rule, see
    /// MissingRule), from compile-time metadata. Args: store path, variable
    /// name. Must not throw on an unreadable store (answer noMissingRule:
    /// the read itself reports the store).
    MissingRuleOf: string -> string -> MissingRule
    /// Runtime C++ dense reader. Args: path, varName, cppVarName, arrType.
    GenReadVar: string -> string -> string -> IRArrayType -> string list
    /// Runtime C++ PACKED (SymIdx/AntisymIdx) reader: emits code assembling
    /// the canonical flat pool (ascending-lex cells x trailing block,
    /// row-major, or the window pool) into `<cppVarName>_flat` only -- the
    /// codegen intercept does allocation, copy, and release. None rejects
    /// packed reads and writes loudly. Args: path, varName, cppVarName, arrType, opts.
    GenReadPacked: (string -> string -> string -> IRArrayType -> PackedReadOpts -> string list) option
    /// ITERATED-WREATH (OrbIdx depth >= 2) pool capability plus its F#-side
    /// canonical-pool reader. Presence is the provider's wreath flag at every
    /// seam (C++ read via GenReadPacked, C++ write via GenWriteVar, and the
    /// interpreter's materialization all gate on it). None means the
    /// provider refuses wreath arrays loudly everywhere (every provider but
    /// zarr; see providers/ZarrTriangularSpec.md spec_version 2). Returns
    /// DimLengths = `[cardinality]` (the pool as one axis) with the payload
    /// in ascending-lex canonical order. Args: store path, variable name.
    ReadWreathPool: (string -> string -> Result<ProviderVarData, string>) option
    /// Runtime C++ compound (masked) reader; None rejects load_compound
    /// loudly. Args: path, varName, maskName, cppVarName, varArrType, maskArrType.
    GenReadCompoundVar: (string -> string -> string -> string -> IRArrayType -> IRArrayType -> string list) option
    /// Runtime C++ dense writer: reads the source array from an already-
    /// populated flat buffer `<cppVarName>_flat` (write intercept emits the
    /// flatten prologue). Args: path, varName, cppVarName, arrType, dimNames.
    GenWriteVar: string -> string -> string -> IRArrayType -> string list -> string list
    /// STREAMED fiber reads (`alias.stream`): emits (a) a hoisted prologue --
    /// open handles, metadata checks, `<cppVarName>_fiber_ext` -- and (b) the
    /// in-nest per-fiber read filling a caller DESTINATION buffer given the
    /// bound SITE index expressions (one per leading dense axis; allocated
    /// per kernel argument, since a comm kernel binds several fibers
    /// concurrently). None rejects `.stream` loudly. Open args: path,
    /// varName, cppVarName, arrType. Fiber args: adds destBufName and site
    /// index expressions. Handles are read-only, left open for the program's lifetime.
    GenStreamOpen: (string -> string -> string -> IRArrayType -> string list) option
    GenStreamFiber: (string -> string -> string -> string -> string list -> IRArrayType -> string list) option
    /// PER-SEGMENT streamed reads of a RANK-1 dense variable (docs/plans/
    /// structural/07 §3.4): `group_by(A, segments(X))` over `A = s.vars.A |>
    /// alias.stream` reads each run's cells straight into that group's row --
    /// no whole-array buffer ever exists. Open args: path, varName,
    /// cppVarName, arrType (the prologue: metadata probe, chunk scratch
    /// buffer, fill value). Rows args: path, varName, cppVarName, destination
    /// pointer expression, lo and hi C++ expressions (the run [lo, hi) in
    /// the variable's own ordinals), arrType. None: rank-1 streams refuse.
    GenStreamRowsOpen: (string -> string -> string -> IRArrayType -> string list) option
    GenStreamRows: (string -> string -> string -> string -> string -> string -> IRArrayType -> string list) option
    /// The natural block of a streamed variable along its LEADING axis --
    /// its chunk edge there -- which a streamed fold or run loop walks one
    /// block at a time (args: path, varName).
    StreamRowsBlock: (string -> string -> int64) option
    /// A rectangular WINDOW of a dense variable of any rank, read into
    /// `dest` row-major over the window's shape: one file per chunk the
    /// window intersects, the intersection copied. Args: path, varName,
    /// cppVarName, dest pointer expression, per-dimension (lo, hi) C++
    /// expressions, arrType. The tile gather of a rank-2 streamed variable
    /// and the row bands of its run loop are both this.
    GenStreamWindow: (string -> string -> string -> string -> (string * string) list -> IRArrayType -> string list) option
    /// #include lines injected when a module reads/writes via this provider
    /// (packed/simplex reads also pull linearized_storage.hpp separately).
    Includes: unit -> string list
    /// Dimension names of a stored variable, so writing a provider-loaded
    /// array back out preserves them. None when the store carries none
    /// (writers fall back to synthesized dim<i>); must not throw on unreadable stores.
    VarDimNames: string -> string -> string list option
    /// Content fingerprint of a store for fold provenance (sha256 hex).
    Fingerprint: string -> string
    /// Cheap change stamp for fold memoization (e.g. mtime ticks; multi-file stores take the max over their files).
    VersionStamp: string -> int64
    /// Documentation of link-time needs (Build.fs remains scan-based).
    LinkNeeds: string
}

/// The directory a program's RELATIVE store paths resolve against at
/// COMPILE time. `blade run` executes the compiled program with the SOURCE
/// file's directory as its working directory, so a `let s =
/// csv.load("data/x.csv")` opens `<source dir>/data/x.csv` at run time; the
/// compile-time metadata and fold reads must open the same file, wherever the
/// compiler was launched from. The CLI verbs (check / emit / compile / run /
/// plan) scope a compilation with `within (Some <source dir>)`; with no scope
/// a relative path resolves against the process working directory as before
/// (the test harness, the REPL and notebook lanes, and `ide serve`, which
/// moves its own working directory to the request file's folder).
///
/// Only the compile-time READERS see the resolved path (`resolving` below):
/// the IR keeps the path the program wrote (Lowering stamps `FilePath` from
/// the source literal), so the emitted C++ and the run record name it as
/// written and resolve it at run time exactly as before. AsyncLocal, like the
/// side channels below, since the test suite compiles programs in parallel.
module SourceBase =
    let private slot = new System.Threading.AsyncLocal<string>()

    /// The scoped base directory, if a compilation set one.
    let current () : string option =
        match slot.Value with
        | null | "" -> None
        | d -> Some d

    /// Run `f` with `dir` as the base (None clears it), restoring the
    /// previous base afterwards.
    let within (dir: string option) (f: unit -> 'a) : 'a =
        let saved = slot.Value
        slot.Value <- (match dir with Some d -> System.IO.Path.GetFullPath d | None -> null)
        try f () finally slot.Value <- saved

    /// A store path as the compile-time readers must open it: a relative path
    /// joined to the scoped base; an absolute path, a URL-like `scheme://`
    /// location, an empty path, or any path with no scope set, untouched. A
    /// suffix the path carries (icechunk's `@branch:main`) rides along.
    let resolve (path: string) : string =
        match current () with
        | None -> path
        | Some dir ->
            if System.String.IsNullOrEmpty path || path.Contains "://"
               || System.IO.Path.IsPathRooted path then path
            else System.IO.Path.Combine(dir, path)

/// The spec as registered: every compile-time reader opens the store at
/// `SourceBase.resolve path`. The runtime emitters (`Gen*`) and `Includes`
/// are untouched -- they render the path the program wrote into the C++.
let private resolving (spec: ProviderSpec) : ProviderSpec =
    let r = SourceBase.resolve
    { spec with
        LoadAsModule = fun builder moduleName path -> spec.LoadAsModule builder moduleName (r path)
        ReadVarData = fun path varName -> spec.ReadVarData (r path) varName
        MissingRuleOf = fun path varName -> spec.MissingRuleOf (r path) varName
        ReadWreathPool = spec.ReadWreathPool |> Option.map (fun f -> fun path varName -> f (r path) varName)
        StreamRowsBlock = spec.StreamRowsBlock |> Option.map (fun f -> fun path varName -> f (r path) varName)
        VarDimNames = fun path varName -> spec.VarDimNames (r path) varName
        Fingerprint = fun path -> spec.Fingerprint (r path)
        VersionStamp = fun path -> spec.VersionStamp (r path) }

let private registry =
    System.Collections.Concurrent.ConcurrentDictionary<string, ProviderSpec>()

/// Idempotent registration (last write wins), mirroring StaticEval's builtin
/// registry convention. The stored spec resolves relative store paths for its
/// compile-time readers (`resolving`).
let register (spec: ProviderSpec) : unit =
    registry.[spec.Name] <- resolving spec

let tryFind (name: string) : ProviderSpec option =
    match registry.TryGetValue name with
    | true, s -> Some s
    | _ -> None

/// Registered provider module names, sorted (diagnostics + StaticEval name-set bridge).
let names () : string list =
    registry.Keys |> List.ofSeq |> List.sort

/// The chunk edge of each dimension of a loaded store, by store BINDING
/// name (docs/plans/structural/07 §3.1): what `type A = Chunked<s.index.d,
/// store>` inherits. A provider that knows its chunk grid registers a
/// reader (store path -> dim -> edge, dims chunked uniformly across the
/// store's variables only); the load site records the answer beside the
/// IDE module so type registration never re-opens the store. Same
/// AsyncLocal discipline as IdeStores (parallel test compilation).
module DimChunks =
    open System.Threading

    let private readers =
        System.Collections.Concurrent.ConcurrentDictionary<string, string -> Map<string, int64>>()
    let private table = new AsyncLocal<Map<string, Map<string, int64>>>()
    let private entries () = match box table.Value with null -> Map.empty | _ -> table.Value

    /// A provider's reader: store path -> (dimension -> chunk edge).
    let registerReader (provider: string) (f: string -> Map<string, int64>) =
        readers.[provider] <- f

    let reset () = table.Value <- Map.empty

    /// Record the edges for a store binding, if its provider has a reader.
    let record (binding: string) (provider: string) (path: string) =
        match readers.TryGetValue provider with
        | true, f ->
            let edges = try f (SourceBase.resolve path) with _ -> Map.empty
            table.Value <- Map.add binding edges (entries ())
        | _ -> ()

    /// The edge of `dim` in the store bound to `binding`, if recorded.
    let tryFind (binding: string) (dim: string) : int64 option =
        match Map.tryFind binding (entries ()) with
        | Some edges -> Map.tryFind dim edges
        | None -> None

    /// Whether the binding's provider recorded anything at all (to tell
    /// "unchunked dimension" from "provider has no chunk reader").
    let hasStore (binding: string) : bool = Map.containsKey binding (entries ())

/// IDE side-channel: the provider IRModule built at each `let store =
/// alias.load(path)` site, keyed by the store binding name. Lets Ide.fs
/// render dims/vars/index-type hovers by reusing the module already built
/// instead of re-opening the (possibly native) data file. AsyncLocal since
/// the test suite compiles programs in parallel, each in its own async context.
module IdeStores =
    open System.Threading

    let private store = new AsyncLocal<Map<string, IRModule>>()
    let private modules () = match box store.Value with null -> Map.empty | _ -> store.Value

    /// Fresh compilation: cleared before an IDE check runs typeCheck.
    let reset () =
        store.Value <- Map.empty
        DimChunks.reset ()
        Blade.Types.SegmentTable.reset ()

    /// Record the module built at a provider load site (last write wins).
    let record (name: string) (pm: IRModule) =
        store.Value <- Map.add name pm (modules ())

    /// IDE-facing: the module recorded for a store binding, if any.
    let tryFind (name: string) : IRModule option = Map.tryFind name (modules ())
