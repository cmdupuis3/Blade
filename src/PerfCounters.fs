// Compiler-internal performance counters (docs/plan-compile-speed.md Stage 5).
//
// Stage 5's candidate fixes (Subst.Resolve path compression, typeOf
// memoization, validateIR walk fusion) are all gated on data: the plan forbids
// implementing any of them without a measurement that clears its entry bar.
// Phase timing (BLADE_PHASE_TIMING) answers "which phase", but not "how many
// times did we walk an inference chain, and how long were they" -- that is
// what these counters exist for.
//
// Cost when disabled: `enabled` is a static bool; every instrumentation site
// is `if PerfCounters.enabled then ...`, i.e. one static-field load and a
// perfectly-predicted branch. No getenv, no allocation, no lock on the hot
// path. `refresh()` (which DOES read the environment) is called once per
// pipeline entry, never per node.
//
// Deliberately in front of the pipeline in Blade.fsproj (right after
// Types.fs) so both instrumented files -- IR.fs and Unify.fs, which are
// hundreds of entries apart in compile order -- can see it.
module Blade.PerfCounters

open System.Threading

/// Master gate. Set by `refresh()` from BLADE_PERF_COUNTERS; read at every
/// instrumentation site. Public and mutable on purpose: a function call or a
/// lazy would cost more than the counter it guards.
let mutable enabled = false

// Counter storage.
//
// An int64[] rather than a set of `let mutable` module values because
// `Interlocked.Increment(&slots.[i])` is a legal byref in F# while the address
// of a module-level mutable is not. The test runner compiles in parallel, so
// the interlocked form is what keeps a multi-file run's totals meaningful.
[<Literal>]
let private NSlots = 8

let private slots : int64[] = Array.zeroCreate NSlots

[<Literal>]
let private SlotResolveCalls = 0
[<Literal>]
let private SlotResolveChains = 1
[<Literal>]
let private SlotResolveHops = 2
[<Literal>]
let private SlotResolveMaxChain = 3
[<Literal>]
let private SlotTypeOfCalls = 4
[<Literal>]
let private SlotTypeOfCarried = 5
[<Literal>]
let private SlotIRNodes = 6
[<Literal>]
let private SlotTypeOfMemoHits = 7

let inline private bump (slot: int) = Interlocked.Increment(&slots.[slot]) |> ignore
let inline private add (slot: int) (n: int64) = Interlocked.Add(&slots.[slot], n) |> ignore

/// Monotone max under concurrency: CAS until the stored value dominates.
let private bumpMax (slot: int) (v: int64) =
    let mutable current = Volatile.Read(&slots.[slot])
    while v > current do
        let seen = Interlocked.CompareExchange(&slots.[slot], v, current)
        if seen = current then current <- v      // won: stored, loop exits
        else current <- seen                     // lost: retry against the winner

// -- Instrumentation API (each call site is already gated on `enabled`) ------

/// One `Subst.Resolve` invocation (including the structural recursion into
/// children, which is where the walk's real cost lives).
let resolveCall () = bump SlotResolveCalls

/// One completed inference-variable chain walk of `hops` indirections
/// (`hops = 0` means the variable was unbound / the chain was one link deep
/// with nothing to follow).
let resolveChain (hops: int) =
    bump SlotResolveChains
    if hops > 0 then
        add SlotResolveHops (int64 hops)
        bumpMax SlotResolveMaxChain (int64 hops)

/// One `IR.typeOf` invocation.
let typeOfCall () = bump SlotTypeOfCalls

/// A `typeOf` invocation answered by the `CarriedType` fast path (no
/// recursion into children).
let typeOfCarried () = bump SlotTypeOfCarried

/// A `typeOf` invocation answered out of the reconstruction memo.
let typeOfMemoHit () = bump SlotTypeOfMemoHits

/// Program size, for the "typeOf calls vs IR node count" ratio. Set once per
/// compile from the driver.
let noteIRNodes (n: int64) = add SlotIRNodes n

// -- Lifecycle ---------------------------------------------------------------

/// Re-read the environment gate. Called at pipeline entries (Cli.compileFile,
/// Lowering's lowerDiag/lowerFileDiag/lowerDiagMulti) rather than cached at
/// module init, matching the repo's read-env-per-call convention so tests can
/// flip the gate mid-process.
let refresh () =
    enabled <-
        match System.Environment.GetEnvironmentVariable "BLADE_PERF_COUNTERS" with
        | null | "" | "0" | "off" -> false
        | _ -> true

let reset () =
    for i in 0 .. NSlots - 1 do
        Volatile.Write(&slots.[i], 0L)

/// `[perf] name: value` lines on stderr, alongside `[phase]`. Only called
/// when the gate is on.
let report () =
    let get slot = Volatile.Read(&slots.[slot])
    let calls = get SlotResolveCalls
    let chains = get SlotResolveChains
    let hops = get SlotResolveHops
    let typeOfCalls = get SlotTypeOfCalls
    let carried = get SlotTypeOfCarried
    let nodes = get SlotIRNodes
    eprintfn "[perf] subst.resolve.calls: %d" calls
    eprintfn "[perf] subst.resolve.chains: %d" chains
    eprintfn "[perf] subst.resolve.hops: %d" hops
    eprintfn "[perf] subst.resolve.max_chain: %d" (get SlotResolveMaxChain)
    eprintfn "[perf] subst.resolve.hops_per_chain: %.3f"
        (if chains = 0L then 0.0 else float hops / float chains)
    eprintfn "[perf] ir.typeof.calls: %d" typeOfCalls
    eprintfn "[perf] ir.typeof.carried: %d" carried
    eprintfn "[perf] ir.typeof.recursive: %d" (typeOfCalls - carried)
    eprintfn "[perf] ir.typeof.memo_hits: %d" (get SlotTypeOfMemoHits)
    eprintfn "[perf] ir.nodes: %d" nodes
    eprintfn "[perf] ir.typeof.calls_per_node: %.3f"
        (if nodes = 0L then 0.0 else float typeOfCalls / float nodes)
