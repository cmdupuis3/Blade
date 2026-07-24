// Provider registry: the seam between the core compiler and data providers.
//
// A provider is a Blade-surface MODULE (`import netcdf as nc`, `import zarr
// as z`) whose implementation lives under providers/. This registry reifies
// the de-facto provider contract (metadata load, compile-time fold read,
// runtime C++ read/write emission) as one record per provider, keyed by the
// surface module name. Core files (TypeCheck, Lowering, CodeGen) dispatch
// through `tryFind` instead of naming any provider directly; the specs are
// registered by ProviderStatics.install (), which TypeCheck.typeCheck runs
// ahead of every pipeline pass.
//
// Layering: compiles after IR.fs and before the provider implementations,
// which construct their ProviderSpec against these types. StaticEval stays
// registry-free (its own layering rule) — ProviderStatics bridges the fold
// reader and the provider-name set into StaticEval's hooks.
module Blade.ProviderRegistry

open Blade.IR
open Blade.Types

/// Provider-neutral compile-time payload for the static fold: dimension
/// extents plus the row-major flat buffer. Float-coded variables arrive as
/// float[], every integer coding as int64[] — mirroring the elem-type
/// collapse each provider's dtype table performs.
type ProviderVarData = {
    DimLengths: int list
    Payload: ProviderPayload
}
and ProviderPayload =
    | PFloats of float[]
    | PInts of int64[]

/// Options for a packed (SymIdx/AntisymIdx) read emission.
type PackedReadOpts = {
    /// Emit the MPI-distributed read: each rank performs only the chunk I/O
    /// whose cells intersect its balanced flat-cell range, then an
    /// MPI_Allgatherv restores the full pool buffer on every rank (so
    /// downstream codegen is untouched). Set only when the program has MPI
    /// scaffolding (emit gate on + module uses mpi) and the read is not
    /// windowed.
    Distribute: bool
    /// Sub-simplex window [lo, hi) over the packed group's index interval:
    /// materialize only the cells with EVERY coordinate in [lo, hi) — a
    /// translated simplex of extent hi-lo. The declared arrType is the
    /// WINDOW type (leading extent hi-lo).
    Window: (int64 * int64) option
}

/// The provider contract. One record per provider, registered under the
/// surface module name ("netcdf", "zarr").
type ProviderSpec = {
    /// Surface module name and registry key: `import <Name> as <alias>`.
    Name: string
    /// Compile-time metadata -> IRModule (named index types + dims/vars
    /// structs). Args: builder, moduleName (the receiving binding's name),
    /// store path.
    LoadAsModule: IRBuilder -> string -> string -> IRModule
    /// Compile-time whole-payload read for the static fold.
    /// Args: store path, variable name.
    ReadVarData: string -> string -> Result<ProviderVarData, string>
    /// Runtime C++ dense reader. Args: path, varName, cppVarName, arrType.
    GenReadVar: string -> string -> string -> IRArrayType -> string list
    /// Runtime C++ PACKED (SymIdx/AntisymIdx) reader: emits code assembling
    /// the requested canonical flat pool (ascending-lex cells x trailing
    /// block, row-major; the window pool when opts.Window is set) into
    /// `<cppVarName>_flat` and nothing else — the codegen intercept
    /// performs the packed allocation (SYMM hoisting needs CodeGen's
    /// namespace-scope collector), copies the pool, and releases the
    /// buffer. None ⇒ the provider rejects packed reads AND writes loudly.
    /// Args: path, varName, cppVarName, arrType, opts.
    GenReadPacked: (string -> string -> string -> IRArrayType -> PackedReadOpts -> string list) option
    /// Runtime C++ compound (masked) reader; None means the provider
    /// rejects load_compound (loud error at the codegen intercept).
    /// Args: path, varName, maskName, cppVarName, varArrType, maskArrType.
    GenReadCompoundVar: (string -> string -> string -> string -> IRArrayType -> IRArrayType -> string list) option
    /// Runtime C++ dense writer. The emitted fragment reads the source
    /// array from an already-populated flat buffer named `<cppVarName>_flat`
    /// (the codegen write intercept emits the flatten prologue).
    /// Args: path, varName, cppVarName, arrType, dimNames.
    GenWriteVar: string -> string -> string -> IRArrayType -> string list -> string list
    /// STREAMED fiber reads (`alias.stream`): the pair emits (a) a hoisted
    /// prologue at the binding position — open handles, metadata checks,
    /// and `<cppVarName>_fiber_ext` (the rank-1 extents vector consuming
    /// nests use for fiber wrappers) — and (b) the in-nest per-fiber read
    /// filling a caller-designated DESTINATION buffer given the bound SITE
    /// index expressions (one per leading dense axis). Destination buffers
    /// are allocated by the consuming nest (one per kernel argument — a
    /// comm kernel binds several fibers of the same source concurrently,
    /// so a per-source buffer would be clobbered). None ⇒ the provider
    /// rejects `.stream` loudly.
    /// Open args: path, varName, cppVarName, arrType (the full var type).
    /// Fiber args: path, varName, cppVarName, destBufName, site index C++
    /// expressions, arrType. Read-only handles are left open for the
    /// program's lifetime (process exit closes them).
    GenStreamOpen: (string -> string -> string -> IRArrayType -> string list) option
    GenStreamFiber: (string -> string -> string -> string -> string list -> IRArrayType -> string list) option
    /// #include lines injected when a module has reads/writes from this
    /// provider. (Packed/simplex reads additionally get
    /// linearized_storage.hpp from the core include helper — that need is
    /// index-type-driven, not provider-specific.)
    Includes: unit -> string list
    /// Dimension names of a stored variable (store path, var name) — used
    /// so writing a provider-loaded array back out preserves its dimension
    /// names. None when the store carries none (writers fall back to
    /// synthesized dim<i>). Must not throw on unreadable stores.
    VarDimNames: string -> string -> string list option
    /// Content fingerprint of a store for fold provenance (sha256 hex).
    Fingerprint: string -> string
    /// Cheap change stamp for fold memoization (e.g. mtime ticks; multi-file
    /// stores take the max over their files).
    VersionStamp: string -> int64
    /// Documentation of link-time needs (Build.fs remains scan-based).
    LinkNeeds: string
}

let private registry =
    System.Collections.Concurrent.ConcurrentDictionary<string, ProviderSpec>()

/// Idempotent registration (last write wins), mirroring StaticEval's
/// builtin registry convention.
let register (spec: ProviderSpec) : unit =
    registry.[spec.Name] <- spec

let tryFind (name: string) : ProviderSpec option =
    match registry.TryGetValue name with
    | true, s -> Some s
    | _ -> None

/// Registered provider module names, sorted (for diagnostics and the
/// StaticEval name-set bridge).
let names () : string list =
    registry.Keys |> List.ofSeq |> List.sort

/// IDE side-channel: the provider IRModule built at each `let store =
/// alias.load(path)` site during type-checking, keyed by the store binding
/// name. Lets Ide.fs render dims/vars/index-type hovers by REUSING the module
/// typecheck already built, instead of re-opening the data file — a second
/// (possibly native) read of the same store is redundant and can crash.
/// AsyncLocal for the same reason as Ppl.Elaborate.IdeDists: the test suite
/// compiles programs in parallel and each flows through one async context.
module IdeStores =
    open System.Threading

    let private store = new AsyncLocal<Map<string, IRModule>>()
    let private modules () = match box store.Value with null -> Map.empty | _ -> store.Value

    /// Fresh compilation: cleared before an IDE check runs typeCheck.
    let reset () = store.Value <- Map.empty

    /// Record the module built at a provider load site (last write wins).
    let record (name: string) (pm: IRModule) =
        store.Value <- Map.add name pm (modules ())

    /// IDE-facing: the module recorded for a store binding, if any.
    let tryFind (name: string) : IRModule option = Map.tryFind name (modules ())
