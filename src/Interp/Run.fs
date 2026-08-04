// Interpreter driver (Milestone M0).
//
// Wraps the tree-walking evaluator (Blade.Interp.Core) and value printer
// (Blade.Interp.Print) into a single process-like entry point, runProgram,
// whose (ExitCode, Stdout, Stderr) result the differential gate
// (tests/InterpDiff.fs) diffs against the compiled C++ binary produced by
// CodeGen. The driver owns only sequencing, output assembly, and the mapping
// of every failure mode to the gate's exit-code protocol; it evaluates and
// prints nothing itself.
//
// Compiled inside Blade.fsproj after Interp/{Value,CppFormat,Numerics,
// RandMirror}.fs and after Core/Print, so it references the concrete IR and
// the sibling interpreter modules directly.
module Blade.Interp.Run

open System.Text
open Blade.Types
open Blade.IR
open Blade.Interp.Value

/// The process-like result of one interpreter run -- the triple the
/// differential gate compares, mirroring an OS process's exit code + streams.
type InterpResult =
    { ExitCode: int
      Stdout: string
      Stderr: string }

// Exit-code protocol (mirrors the C++ runtime plus a private interpreter lane):
//   0   - normal completion.
//   1   - InterpPanic: a Blade runtime guard fired. Matches blade_rt::panic,
//         which prints the diagnostic and std::exit(1) (cpp/blade_runtime.hpp).
//   125 - a feature the interpreter/printer does not implement yet
//         (Core.InterpUnsupported / Print.PrintUnsupported). A distinct code so
//         the gate classifies SKIP-UNSUPPORTED apart from a real divergence.
//   70  - any other .NET exception escaping the run: an interpreter bug, not a
//         program fault (70 == BSD EX_SOFTWARE, "internal software error").

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
/// location line when a span is carried (file present and line > 0), then the
/// Blade shadow-stack frames innermost-first (`frames` is already in that
/// order -- Core.capturedFrames walks `depth-1 .. 0`). Each frame carries
/// file=nullptr and line=0 (`BLADE_FRAME(name, nullptr, 0)`), so panic's
/// `if (stack[i].file && stack[i].line > 0)` guard is always false: a frame
/// line is exactly `  at <name>\n`, no ` (file:line)` suffix.
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

// Random-fill bindings (rand.uniform / rand.normal, RandomInits/RandGen).
//
// Lowering records a `let A = rand.<kind>(key, shape)` binding with a unit
// placeholder Value and its RandGen(kind, keyIR) in IRModule.RandomInits
// (Lowering.fs ~L1676-1697). CodeGen materializes it at the binding's position
// via genRandGenBinding (CodeGen.fs ~L8156-8184): allocate the dense pool,
// then ONE `blade_rand::<kind>(pool_base(A.data), card, (int64_t)(key))` call
// (card = product of extents, row-major flat pool, one draw per slot). The
// interpreter mirrors this so output prints byte-for-byte like the compiled
// binary; RandMirror.draws reproduces the mt19937_64 stream bit-exactly.

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
/// per rank component, all static IRLitInt -- codegen `#error`s otherwise). card
/// = product of extents; the key IRExpr is evaluated in the ROOT env (it may
/// reference earlier bindings) and cast to int64; RandMirror draws `card`
/// values keyed by it. The flat SFloat pool is reshaped via ArrayOps.mkDenseArray,
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

// Provider reads (`let A = view |> alias.read` over a netcdf/zarr var).
//
// Lowering records a deferred provider read in IRModule.ProviderReads (keyed by
// the receiving binding's IRId; IR.fs ProviderReadSpec) and CodeGen materializes
// it at the binding's position via genProviderReadBinding (CodeGen.fs ~L8296):
// dispatched on the registered ProviderSpec (Blade.ProviderRegistry), it emits
// the provider's runtime C++ reader (nc_get_var_* / zarr fstream chunk reads).
//
// The interpreter mirrors that in-process: at the binding's position (exactly
// like RandomInits), it invokes the registered F# provider's compile-time
// whole-payload reader -- ProviderSpec.ReadVarData, the same entry point the
// static fold (ProviderStatics.readAndFold) uses -- and shapes the result into
// a dense BladeArray of the variable's declared type.
//
// CWD ASYMMETRY (load-bearing gotcha). spec.FilePath is the store path baked
// as given in the source. The compiled binary resolves it against its own cwd
// (the exe's directory at runtime); the interpreter resolves it against the
// compiler process cwd. A relative path reads identical bytes on both sides
// only when the fixture is staged at both locations (the two-copy scheme
// NetcdfTests/ZarrTests use). An absolute path is cwd-independent and always
// agrees. ReadVarData is called with the path verbatim (no rewriting).
//
// SCOPE: only dense whole-variable reads are mirrored. The packed
// (SymIdx/AntisymIdx) arm needs compact-pool storage and ReadVarData refuses
// packed vars; the compound (load_compound mask) arm produces a
// Compound<T,rank>; windowed reads are packed sub-simplices; a streamed read
// is never materialized (consuming nests inline fibers). Each raises
// InterpUnsupported so the whole program SKIP-classifies rather than risk
// wrong bytes. Provider writes (alias.write) are a side effect, gated by the caller.

/// Materialize a deferred provider read as CodeGen.genProviderReadBinding's DENSE
/// arm does, but in-process (see the section header for the cwd asymmetry and
/// gated non-dense arms). Extents come from the payload's own DimLengths; the
/// element/index types come from the spec's VarType. Narrow element types
/// widen into the wide store (Float32 -> SFloat, Int32 -> SInt); Print
/// narrows back at format time, matching `cout << (float)` / `(int32_t)`.
let private materializeProviderRead (state: Core.InterpState) (binding: IRBinding) (spec: ProviderReadSpec) : Value =
    if spec.Streamed then
        raise (Core.InterpUnsupported "streamed provider read (.stream -- per-fiber reads not interpreted)")
    elif spec.MaskName.IsSome then
        raise (Core.InterpUnsupported "compound (load_compound) provider read (M2.7 compound family)")
    elif spec.Window.IsSome then
        raise (Core.InterpUnsupported "windowed packed provider read (read_window)")
    // Ahead of the packed gate (which a wreath group also trips) so the wreath
    // arm owns it: a wreath array is a flat pool, not an Array-with-a-skeleton,
    // and `mkDenseArray` below would shape it as a dense prod(ri)-axis tensor.
    // Materialized here from the provider's canonical-pool reader, cell for
    // cell. A provider with no wreath pools (ReadWreathPool = None) still
    // refuses, and it FAILS rather than SKIPs: InterpUnsupported would
    // classify as SKIP-UNSUPPORTED and let a divergence hide behind it.
    elif spec.VarType.IndexTypes |> List.exists (fun ix -> ix.Symmetry = SymWreath) then
        let ix = spec.VarType.IndexTypes |> List.find (fun ix -> ix.Symmetry = SymWreath)
        let levels = Blade.IR.orbitLevelsOf ix
        let refuse (why: string) =
            failwith (Blade.IR.orbitStorageUnsupported
                          (sprintf "provider read of '%s' (%s)" spec.VarName why) levels)
        match Blade.ProviderRegistry.tryFind spec.Provider with
        | None -> refuse (sprintf "provider '%s' is not registered" spec.Provider)
        | Some pspec ->
        match pspec.ReadWreathPool with
        | None -> refuse (sprintf "provider '%s' stores no OrbIdx pools" spec.Provider)
        | Some readPool ->
            if spec.VarType.IndexTypes.Length <> 1 then
                refuse "a wreath group combined with other index groups has no pool layout"
            let n =
                match Blade.IR.orbitBaseExtent ix with
                | IRLit (IRLitInt v) -> v
                | _ -> refuse "a wreath class needs a compile-time extent"
            match readPool spec.FilePath spec.VarName with
            | Error e ->
                // Unlike the dense arm below, this is NOT a SKIP: the compiled
                // side reads the same store from the same path, so a failure
                // here is a real disagreement, not an un-interpreted feature.
                failwithf "provider read of '%s' from '%s': %s" spec.VarName spec.FilePath e
            | Ok data ->
                // The pool arrives as ONE axis of `cardinality` cells; allocWreath
                // sizes the store from cellCountChecked (the same fold that
                // validated shape[0] at metadata parse), so a length disagreement
                // here is the store lying about its own class.
                let arr = ArrayOps.allocWreath spec.VarType.ElemType spec.VarType.IndexTypes levels n
                let cells = ArrayOps.wreathCellCount arr
                let got = data.DimLengths |> List.fold (*) 1
                if got <> cells then
                    failwithf "provider read of '%s': the store's pool holds %d cells but OrbIdx%s at extent %d has %d"
                              spec.VarName got (Blade.IR.ppOrbitLevels levels) n cells
                let put (i: int) (v: Value) = ArrayOps.wreathWriteAt arr (int64 i) v
                (match ArrayOps.elemThrough spec.VarType.ElemType, data.Payload with
                 | Some (ETFloat64 | ETFloat32), Blade.ProviderRegistry.PFloats xs -> xs |> Array.iteri (fun i x -> put i (VFloat x))
                 | Some (ETFloat64 | ETFloat32), Blade.ProviderRegistry.PInts xs -> xs |> Array.iteri (fun i x -> put i (VFloat (float x)))
                 | Some (ETInt64 | ETInt32), Blade.ProviderRegistry.PInts xs -> xs |> Array.iteri (fun i x -> put i (VInt x))
                 | Some (ETInt64 | ETInt32), Blade.ProviderRegistry.PFloats xs -> xs |> Array.iteri (fun i x -> put i (VInt (int64 x)))
                 | _ ->
                     raise (Core.InterpUnsupported
                             (sprintf "provider read of '%s' into a non-numeric element type" spec.VarName)))
                state.Cells <- state.Cells + int64 cells
                VArray arr
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
/// order into the root env (keyed by the binding's globally-unique IRId -- the
/// SSA scoping discipline in Interp/Value.fs), then print. Raising evaluators
/// propagate out to runProgram's handler.
let private execProgram (state: Core.InterpState) (merged: IRModule) (program: IRProgram) (testName: string) : InterpResult =
    let root = envNew ()
    // Function bodies may reference module-level bindings (emitted as
    // main-local capturing lambdas in C++) -- expose the root scope to call
    // frames before any binding evaluates.
    state.Global <- Some root
    for m in program.Modules do
        for b in m.Bindings do
            // Defer-aware: a deferred combinator binding stores VDeferred (no
            // eager force); a method_for/object_for binding stores VLoopObj;
            // everything else evaluates eagerly, mirroring CodeGen.genBinding.
            // RandomInits / ProviderReads / ProviderWrites bindings are
            // placeholders intercepted here at their position in the binding
            // sequence (so a key expr referencing an earlier binding resolves
            // against the root env, as the C++ counterpart reads earlier
            // main()-locals); the intercept order mirrors CodeGen.genBinding's
            // dispatch (ProviderReads, ProviderWrites, RandomInits, CompoundInits).
            let v =
                match Map.tryFind b.Id m.ProviderReads with
                | Some spec ->
                    materializeProviderRead state b spec
                | None ->
                match Map.tryFind b.Id m.ProviderWrites with
                | Some _ ->
                    // alias.write("path", A): a filesystem side effect. The
                    // interpreter never writes (side-effect policy -- flag-gated
                    // later), so the whole program SKIP-classifies.
                    raise (Core.InterpUnsupported "provider write (alias.write -- side effect; flag-gated later)")
                | None ->
                match Map.tryFind b.Id m.RandomInits with
                | Some (RandGen (kind, keyExpr)) ->
                    materializeRandGen state root b kind keyExpr
                | Some (FillModulus _) ->
                    // fill_random(mod) fills with C `rand() % mod`: nondeterministic
                    // and NOT mirrored by RandMirror (only the deterministic
                    // mt19937_64 uniform/normal streams are), so no byte-parity is
                    // possible -- classify SKIP-UNSUPPORTED.
                    raise (Core.InterpUnsupported "fill_random(mod) (C rand()%mod is nondeterministic)")
                | None ->
                    // A CompoundInits binding (compound(A, m) / load_compound)
                    // materializes its compact buffer + rank-to-tuple table here, at
                    // its position in sequence, exactly as genCompoundInitBinding
                    // scatters present cells at the binding's site in main().
                    match Map.tryFind b.Id m.CompoundInits with
                    | Some (denseExpr, maskExpr) ->
                        Loops.materializeCompoundBinding state root b denseExpr maskExpr
                    | None ->
                        // A SparseInits binding (sparse(values, keys)) bundles
                        // the rank-1 values buffer with the key table here, at
                        // its position, mirroring genSparseInitBinding.
                        match Map.tryFind b.Id m.SparseInits with
                        | Some valuesExpr ->
                            Loops.materializeSparseBinding state root b valuesExpr
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
    Print.printBindings testName lookup state.ForcedDeferred merged sb

    // state.Err collects any non-fatal interpreter diagnostics -> stderr.
    { ExitCode = ExitOk; Stdout = sb.ToString(); Stderr = state.Err.ToString() }

/// Run a lowered program under the tree-walking interpreter, mapping each
/// outcome onto the exit-code protocol above. The whole run executes on the
/// large stack (Runtime.fs) -- the same worker the compile pipeline uses --
/// because deep recursion arrives in later milestones; catching on that worker
/// thread means no exception ever crosses back to the caller.
let runProgram (program: IRProgram) (testName: string) (limits: InterpLimits) : InterpResult =
    Blade.Runtime.runOnLargeStack (fun () ->
        // Build the interpreter state OUTSIDE execProgram but capture it in a ref
        // the panic handler can read: on an escaping InterpPanic we render the
        // shadow-stack frames still live in the state (evalCall never pops on
        // the exception path). (`ref None` guards makeState itself throwing.)
        let stateRef : Core.InterpState option ref = ref None
        try
            // One merged module drives the callables table AND printing, exactly
            // as CodeGen.genSelfContainedProgramFromIR merges modules for main().
            let merged = printableModule program
            // Install the module's callables into the AsyncLocal AnalysisContext
            // on THIS worker thread so buildLoopNestCodeGen can resolve kernels
            // (via resolveKernel/resolveCallable in Interp/Loops.fs). AsyncLocal
            // does not flow from makeState's private table, so it must be set
            // here and restored on exit. Harmless for a pure-scalar run.
            let savedCtx = Blade.IR.setCallablesContext (Blade.IR.buildCallablesTableForModule merged)
            try
                let state = Core.makeState merged limits
                // Wire the M2 loop/array backend (Interp/Loops.fs).
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
        // Array layer's own "not yet interpreted" signal: ArrayOps compiles
        // before Core, so it raises its own ArrayOpUnsupported instead, which
        // must SKIP-classify identically (Interp/ArrayOps.fs CONTRACT NOTE (2)).
        | ArrayOps.ArrayOpUnsupported feature ->
            { ExitCode = ExitUnsupported; Stdout = ""; Stderr = sprintf "interp-unsupported: %s" feature }
        | Print.PrintUnsupported feature ->
            { ExitCode = ExitUnsupported; Stdout = ""; Stderr = sprintf "interp-unsupported: %s" feature }
        | ex ->
            { ExitCode = ExitInterpBug; Stdout = ""; Stderr = sprintf "interp-error: %s" ex.Message })
