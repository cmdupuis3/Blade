// Interpreter driver — Milestone M0.
//
// Wraps the tree-walking evaluator (Blade.Interp.Core) and value printer
// (Blade.Interp.Print) into a single process-like entry point, runProgram,
// whose (ExitCode, Stdout, Stderr) result the differential gate
// (tests/InterpDiff.fs) diffs against the compiled C++ binary produced by
// CodeGen. The driver owns only sequencing, output assembly, and the mapping
// of every failure mode to the gate's exit-code protocol; it evaluates and
// prints nothing itself.
//
// Compiled INSIDE Blade.fsproj AFTER Interp/{Value,CppFormat,Numerics,
// RandMirror}.fs and after Core/Print, so it references the concrete IR and
// the sibling interpreter modules directly.
module Blade.Interp.Run

open System.Text
open Blade.Types
open Blade.IR
open Blade.Interp.Value

/// The process-like result of one interpreter run — the triple the
/// differential gate compares, mirroring an OS process's exit code + streams.
type InterpResult =
    { ExitCode: int
      Stdout: string
      Stderr: string }

// ---------------------------------------------------------------------------
// Exit-code protocol (mirrors the C++ runtime plus a private interpreter lane):
//
//   0   — normal completion.
//   1   — InterpPanic: a Blade runtime guard fired. Matches blade_rt::panic,
//         which prints the diagnostic and std::exit(1) (cpp/blade_runtime.hpp).
//   125 — a feature the interpreter/printer does not implement yet
//         (Core.InterpUnsupported / Print.PrintUnsupported). A DISTINCT code so
//         the gate classifies SKIP-UNSUPPORTED apart from a real divergence.
//   70  — any other .NET exception escaping the run: an interpreter bug, not a
//         program fault (70 == BSD EX_SOFTWARE, "internal software error").
// ---------------------------------------------------------------------------

[<Literal>]
let ExitOk = 0

[<Literal>]
let ExitPanic = 1

[<Literal>]
let ExitUnsupported = 125

[<Literal>]
let ExitInterpBug = 70

/// Format an InterpPanic byte-for-byte like cpp/blade_runtime.hpp:29-41's
/// blade_rt::panic: an `error[CODE]: msg\n` line, then a `  --> file:line\n`
/// location line WHEN a span is carried (file present and line > 0), then the
/// Blade shadow-stack frames innermost-first (`frames` is already in that order
/// — Core.capturedFrames walks `depth-1 .. 0`). Each frame carries file=nullptr
/// and line=0 (CodeGen emits `BLADE_FRAME(name, nullptr, 0)`), so panic's
/// `if (stack[i].file && stack[i].line > 0)` guard is ALWAYS false: a frame line
/// is exactly `  at <name>\n`, with no ` (file:line)` suffix. InterpPanic's
/// fields map 1:1 onto panic's parameters; `frames` is read from InterpState at
/// catch time (see runProgram).
let private formatPanic (code: string) (msg: string) (file: string option) (line: int) (frames: string list) : string =
    let sb = StringBuilder()
    sb.Append("error[").Append(code).Append("]: ").Append(msg).Append('\n') |> ignore
    match file with
    | Some f when line > 0 ->
        sb.Append("  --> ").Append(f).Append(':').Append(line).Append('\n') |> ignore
    | _ -> ()
    for fn in frames do
        sb.Append("  at ").Append(fn).Append('\n') |> ignore
    sb.ToString()

/// Assemble the printable module for a (possibly multi-module) program. M0
/// corpus programs are single-module; for a merged multi-module program the
/// printer runs over one synthetic module carrying every binding in module
/// order, exactly as CodeGen.genSelfContainedProgramFromIR merges modules
/// (functions first, bindings concatenated in module order).
let private printableModule (program: IRProgram) : IRModule =
    match program.Modules with
    | [ single ] -> single
    | many ->
        { many.Head with
            Functions = many |> List.collect (fun m -> m.Functions)
            Bindings = many |> List.collect (fun m -> m.Bindings)
            MutableArrayLets = many |> List.fold (fun acc m -> Set.union acc m.MutableArrayLets) Set.empty }

// ---------------------------------------------------------------------------
// Random-fill bindings (rand.uniform / rand.normal — RandomInits/RandGen).
//
// Lowering records a `let A = rand.<kind>(key, shape)` binding with a unit
// placeholder Value and its RandGen(kind, keyIR) in IRModule.RandomInits
// (Lowering.fs ~L1676-1697). CodeGen materializes it at the binding's position
// in main() via genRandGenBinding (CodeGen.fs ~L8156-8184): allocate the dense
// pool, then ONE `blade_rand::<kind>(pool_base(A.data), card, (int64_t)(key))`
// call, where card = product of the array type's component extents (row-major
// flat pool), one draw per slot, filled in flat order. The interpreter mirrors
// that here so a rand-using program prints byte-for-byte like the compiled
// binary; RandMirror.draws reproduces the mt19937_64 stream bit-exactly.
// ---------------------------------------------------------------------------

/// Truncate a key value toward zero to int64, exactly as codegen's cast
/// `(int64_t)(key)` (mirrors Core.toI64, which is private to Core).
let private keyToInt64 (v: Value) : int64 =
    match v with
    | VInt n -> n
    | VInt32 n -> int64 n
    | VFloat f -> int64 f
    | VFloat32 f -> int64 (float f)
    | VBool b -> if b then 1L else 0L
    | VChar c -> int64 (int c)
    | _ -> 0L

/// Materialize a `rand.uniform` / `rand.normal` binding as CodeGen.genRandGenBinding
/// emits it. Component extents come from the binding's ArrayElem type (one entry
/// per rank component, all static IRLitInt — codegen `#error`s otherwise, so a
/// non-literal extent is an interpreter-unsupported dynamic-rand shape). card =
/// product of extents; the key IRExpr is evaluated in the ROOT env (it may
/// reference earlier bindings) and cast to int64; RandMirror draws `card` values
/// keyed by it. The flat SFloat pool is reshaped to the array's rank via
/// ArrayOps.mkDenseArray (flat leaf for rank<=1; SNested rows for rank>=2),
/// exactly as every other dense interpreter array is shaped.
let private materializeRandGen (state: Core.InterpState) (root: Env) (binding: IRBinding) (kind: string) (keyExpr: IRExpr) : Value =
    match binding.Type with
    | ArrayElem arrTy ->
        let extents =
            arrTy.IndexTypes
            |> List.collect (fun idx ->
                List.replicate idx.Rank
                    (match idx.Extent with
                     | IRLit (IRLitInt n) -> n
                     | _ -> raise (Core.InterpUnsupported "rand binding with a non-literal extent")))
        let card = extents |> List.fold (*) 1L
        let key = keyToInt64 (Core.evalExpr state root keyExpr)
        // .NET arrays are int-indexed, so the draw count is int-bounded exactly
        // as the pool it fills; card stays int64 to match codegen's `1L` fold.
        let data = RandMirror.draws kind key (int card)
        state.Cells <- state.Cells + card
        VArray (ArrayOps.mkDenseArray arrTy.ElemType arrTy.IndexTypes (Array.ofList extents) (SFloat data))
    | _ -> raise (Core.InterpUnsupported "rand binding is not an array type")

// ---------------------------------------------------------------------------
// Provider reads (M6 — `let A = view |> alias.read` over a netcdf/zarr var).
//
// Lowering records a deferred provider read in IRModule.ProviderReads (keyed by
// the receiving binding's IRId; IR.fs ProviderReadSpec) and CodeGen materializes
// it at the binding's position via genProviderReadBinding (CodeGen.fs ~L8296):
// dispatched on the registered ProviderSpec (Blade.ProviderRegistry), it emits
// the provider's RUNTIME C++ reader (nc_get_var_* / zarr fstream chunk reads).
//
// The interpreter mirrors that in-process: at the binding's position (exactly
// like RandomInits), it invokes the registered F# provider's compile-time
// whole-payload reader — ProviderSpec.ReadVarData, the SAME entry point the
// static fold (ProviderStatics.readAndFold) uses — to load the data the compiled
// binary reads at runtime, and shapes it into a dense BladeArray of the
// variable's declared type. A downstream `method_for(A)` then sees the
// materialized array just as the compiled main() sees the read's C++ local.
//
// CWD ASYMMETRY (the load-bearing gotcha). spec.FilePath is the store path baked
// AS GIVEN in the source. The compiled binary resolves it against ITS cwd (the
// exe's own directory at runtime); the interpreter resolves it against the
// COMPILER process cwd. So a RELATIVE path reads identical bytes on both sides
// only when the fixture is staged at BOTH locations — the two-copy scheme
// NetcdfTests/ZarrTests use (compiler-cwd copy for compile-time metadata +
// exe-dir copy for the runtime read). An ABSOLUTE path is cwd-independent and
// always agrees. ReadVarData is called with the path VERBATIM (no rewriting):
// the interpreter reads whatever sits at that path relative to its own cwd,
// which for the two-copy scheme is the compiler-cwd copy — byte-identical to the
// exe-dir copy the binary reads.
//
// SCOPE: only DENSE whole-variable reads are mirrored. The packed
// (SymIdx/AntisymIdx) arm needs compact-pool storage AND ReadVarData REFUSES
// packed vars (StaticValue/ProviderVarData has no packed carrier); the compound
// (load_compound mask) arm produces a Compound<T,rank> (M2.7 compound family);
// windowed reads are packed sub-simplices; a streamed read is NEVER materialized
// (consuming nests inline fibers). Each of those raises InterpUnsupported so the
// whole program SKIP-classifies rather than risk wrong bytes. Provider WRITES
// (alias.write) are a side effect and are gated by the caller (side-effect
// policy — flag-gated later).
// ---------------------------------------------------------------------------

/// Materialize a deferred provider read as CodeGen.genProviderReadBinding's DENSE
/// arm does, but in-process (see the section header for the cwd asymmetry and the
/// gated non-dense arms). Extents come from the payload's own DimLengths (the
/// ground truth of what was read); the element type / index types come from the
/// spec's VarType (the array type GenReadVar reads into). Narrow element types
/// widen into the wide store exactly as the interpreter's storage model requires
/// (Float32 -> SFloat, Int32 -> SInt); Print narrows back to the declared width
/// at format time, matching `cout << (float)` / `cout << (int32_t)`.
let private materializeProviderRead (state: Core.InterpState) (binding: IRBinding) (spec: ProviderReadSpec) : Value =
    if spec.Streamed then
        raise (Core.InterpUnsupported "streamed provider read (.stream — per-fiber reads not interpreted)")
    elif spec.MaskName.IsSome then
        raise (Core.InterpUnsupported "compound (load_compound) provider read (M2.7 compound family)")
    elif spec.Window.IsSome then
        raise (Core.InterpUnsupported "windowed packed provider read (read_window)")
    elif spec.VarType.IndexTypes |> List.exists (fun ix -> ix.Symmetry <> SymNone && ix.Rank >= 2) then
        raise (Core.InterpUnsupported "packed (symmetric/antisymmetric) provider read")
    else
        match Blade.ProviderRegistry.tryFind spec.Provider with
        | None ->
            raise (Core.InterpUnsupported (sprintf "provider '%s' is not registered (ProviderStatics.install)" spec.Provider))
        | Some pspec ->
            match pspec.ReadVarData spec.FilePath spec.VarName with
            | Error e ->
                // The provider could not read this variable in-process (missing
                // store relative to the compiler cwd, a packed var ReadVarData
                // refuses, a corrupt chunk, ...). No faithful image -> SKIP rather
                // than diverge; the caller classifies SKIP-UNSUPPORTED.
                raise (Core.InterpUnsupported (sprintf "provider read of '%s' from '%s': %s" spec.VarName spec.FilePath e))
            | Ok data ->
                let arrTy = spec.VarType
                let extents = data.DimLengths |> List.map int64 |> Array.ofList
                let store =
                    match ArrayOps.elemThrough arrTy.ElemType, data.Payload with
                    | Some (ETFloat64 | ETFloat32), Blade.ProviderRegistry.PFloats xs -> SFloat xs
                    | Some (ETFloat64 | ETFloat32), Blade.ProviderRegistry.PInts xs -> SFloat (xs |> Array.map float)
                    | Some (ETInt64 | ETInt32), Blade.ProviderRegistry.PInts xs -> SInt xs
                    | Some (ETInt64 | ETInt32), Blade.ProviderRegistry.PFloats xs -> SInt (xs |> Array.map int64)
                    | _ ->
                        raise (Core.InterpUnsupported
                                (sprintf "provider read of '%s' into a non-numeric element type" spec.VarName))
                let card = extents |> Array.fold (fun acc e -> acc * e) 1L
                state.Cells <- state.Cells + card
                VArray (ArrayOps.mkDenseArray arrTy.ElemType arrTy.IndexTypes extents store)

/// Execute a program: build state, evaluate every top-level binding in module
/// order into the root env (keyed by the binding's globally-unique IRId — the
/// SSA scoping discipline in Interp/Value.fs), then print. Raising evaluators
/// propagate out to runProgram's handler.
let private execProgram (state: Core.InterpState) (merged: IRModule) (program: IRProgram) (testName: string) : InterpResult =
    let root = envNew ()
    // Function bodies may reference module-level bindings (emitted as
    // main-local capturing lambdas in C++) — expose the root scope to call
    // frames before any binding evaluates.
    state.Global <- Some root
    for m in program.Modules do
        for b in m.Bindings do
            // Defer-aware: a deferred combinator binding stores VDeferred (no
            // eager force); a method_for/object_for binding stores VLoopObj;
            // everything else evaluates eagerly. Mirrors CodeGen.genBinding so a
            // deferred binding is never materialized here (Print skips it).
            //
            // A RandomInits binding is a rand-fill placeholder (unit Value); it
            // is intercepted HERE, at its position in the binding sequence, so a
            // key expr referencing an earlier binding resolves against the root
            // env just as its C++ counterpart reads earlier main()-locals
            // (CodeGen.genBinding dispatches genRandGenBinding in-sequence too).
            //
            // A ProviderReads / ProviderWrites binding is likewise a deferred
            // placeholder intercepted at its position. The intercept ORDER mirrors
            // CodeGen.genBinding's dispatch (ProviderReads, ProviderWrites,
            // RandomInits, CompoundInits), so a read and its downstream consumers
            // land exactly as they do in the compiled main().
            let v =
                match Map.tryFind b.Id m.ProviderReads with
                | Some spec ->
                    materializeProviderRead state b spec
                | None ->
                match Map.tryFind b.Id m.ProviderWrites with
                | Some _ ->
                    // alias.write("path", A): a filesystem side effect. The
                    // interpreter never writes (side-effect policy — flag-gated
                    // later), so the whole program SKIP-classifies.
                    raise (Core.InterpUnsupported "provider write (alias.write — side effect; flag-gated later)")
                | None ->
                match Map.tryFind b.Id m.RandomInits with
                | Some (RandGen (kind, keyExpr)) ->
                    materializeRandGen state root b kind keyExpr
                | Some (FillModulus _) ->
                    // fill_random(mod) fills with C `rand() % mod`: nondeterministic
                    // and NOT mirrored by RandMirror (only the deterministic
                    // mt19937_64 uniform/normal streams are), so no byte-parity is
                    // possible — classify SKIP-UNSUPPORTED.
                    raise (Core.InterpUnsupported "fill_random(mod) (C rand()%mod is nondeterministic)")
                | None ->
                    // A CompoundInits binding (compound(A, m) / load_compound)
                    // materializes its compact buffer + rank↔tuple table here, at
                    // its position in sequence, exactly as genCompoundInitBinding
                    // scatters present cells at the binding's site in main().
                    match Map.tryFind b.Id m.CompoundInits with
                    | Some (denseExpr, maskExpr) ->
                        Loops.materializeCompoundBinding state root b denseExpr maskExpr
                    | None ->
                        Core.evalBinding state root b
            envBind root b.Id v |> ignore

    // Resolve a binding id to its computed value for the printer. Print decides
    // which bindings render and in what order/format (iostream parity), and
    // emits the leading "<name> completed in 0s" timing line (the gate strips
    // timing lines on both sides).
    let lookup (id: IRId) : Value option =
        match envTryFind root id with
        | Some cell -> Some cell.V
        | None -> None

    let sb = StringBuilder()
    Print.printBindings testName lookup merged sb

    // state.Err collects any non-fatal interpreter diagnostics -> stderr.
    { ExitCode = ExitOk; Stdout = sb.ToString(); Stderr = state.Err.ToString() }

/// Run a lowered program under the tree-walking interpreter, mapping each
/// outcome onto the exit-code protocol above. The whole run executes on the
/// large stack (Runtime.fs) — the same worker the compile pipeline uses —
/// because deep recursion arrives in later milestones; catching on that worker
/// thread means no exception ever crosses back to the caller.
let runProgram (program: IRProgram) (testName: string) (limits: InterpLimits) : InterpResult =
    Blade.Runtime.runOnLargeStack (fun () ->
        // Build the interpreter state OUTSIDE execProgram but capture it in a ref
        // the panic handler can read: on an escaping InterpPanic we render the
        // shadow-stack frames still live in the state (evalCall never pops on the
        // exception path), which is how the frames reach formatPanic. (`ref None`
        // guards the rare case where makeState itself throws before assignment.)
        let stateRef : Core.InterpState option ref = ref None
        try
            // One merged module drives the callables table AND printing, exactly
            // as CodeGen.genSelfContainedProgramFromIR merges modules for main().
            let merged = printableModule program
            // Install the module's callables into the AsyncLocal AnalysisContext on
            // THIS worker thread so the M2 loop backend's buildLoopNestCodeGen can
            // resolve kernels (it reads the context via resolveKernel/resolveCallable
            // — Interp/Loops.fs). AsyncLocal does not flow from makeState's private
            // table, so it must be set here, inside runOnLargeStack's worker, and
            // restored on exit. Harmless for a pure-scalar run (no nest is built).
            let savedCtx = Blade.IR.setCallablesContext (Blade.IR.buildCallablesTableForModule merged)
            try
                let state = Core.makeState merged limits
                // Wire the M2 loop/array backend (Interp/Loops.fs). Written so the
                // real Loops.evalArrayNode / Loops.force satisfy it verbatim.
                let hooks : Core.InterpHooks =
                    { EvalArrayNode = Loops.evalArrayNode
                      Force = Loops.force }
                state.Hooks <- Some hooks
                stateRef.Value <- Some state
                execProgram state merged program testName
            finally
                Blade.IR.restoreAnalysisContext savedCtx
        with
        | InterpPanic (code, msg, file, line) ->
            let frames = match stateRef.Value with Some st -> Core.capturedFrames st | None -> []
            { ExitCode = ExitPanic; Stdout = ""; Stderr = formatPanic code msg file line frames }
        | Core.InterpUnsupported feature ->
            { ExitCode = ExitUnsupported; Stdout = ""; Stderr = sprintf "interp-unsupported: %s" feature }
        // Array layer's own "not yet interpreted" signal. ArrayOps compiles BEFORE
        // Core, so it cannot raise Core.InterpUnsupported; it raises its own
        // ArrayOpUnsupported, which must SKIP-classify identically (ExitUnsupported)
        // — see Interp/ArrayOps.fs CONTRACT NOTE (2).
        | ArrayOps.ArrayOpUnsupported feature ->
            { ExitCode = ExitUnsupported; Stdout = ""; Stderr = sprintf "interp-unsupported: %s" feature }
        | Print.PrintUnsupported feature ->
            { ExitCode = ExitUnsupported; Stdout = ""; Stderr = sprintf "interp-unsupported: %s" feature }
        | ex ->
            { ExitCode = ExitInterpBug; Stdout = ""; Stderr = sprintf "interp-error: %s" ex.Message })
