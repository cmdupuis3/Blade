// Blade-DSL C++ Code Generation
// Transforms IR structures into C++ source code
// Generates complete, compilable C++ programs

module Blade.CodeGen

open Blade.IR
open Blade.Types
open Blade.EmitCpp
open Blade.ReynoldsCore

// ============================================================================
// Runtime diagnostics emission helpers (Stage 6)
// ============================================================================

/// Escape a string for embedding inside a C++ double-quoted string literal
/// (backslashes and double quotes only — control chars are not expected in
/// Blade identifiers or spans).
let private cppStrEscape (s: string) : string =
    s.Replace("\\", "\\\\").Replace("\"", "\\\"")

/// Render a Blade source span as the trailing `(file, line)` argument pair for
/// a `blade_rt::panic(...)` call. Absent/empty file -> `nullptr`; a
/// zero/negative start line -> `0`. panic degrades gracefully on either.
let private panicSpanArgs (span: Blade.Ast.Span) : string =
    let fileArg =
        match span.File with
        | Some f when f <> "" -> sprintf "\"%s\"" (cppStrEscape f)
        | _ -> "nullptr"
    let lineArg = if span.StartLine > 0 then string span.StartLine else "0"
    sprintf "%s, %s" fileArg lineArg

// ============================================================================
// Code Generation Context
// ============================================================================

/// Tracks information needed during code generation
type CodeGenContext = {
    /// Map from IR variable IDs to C++ variable names
    VarNames: Map<IRId, string>
    /// Current indentation level
    Indent: int
    /// Generated static declarations (symmetry vectors, extents)
    StaticDecls: string list
    /// Map from C++ variable name to its tuple children names (for extents provenance)
    /// e.g., "_0" â†’ ["_0_0", "_0_1"] means _0 is a pair whose components are _0_0 and _0_1
    TupleChildren: Map<string, string list>
    /// Map from IRId to deferred computation expressions (not yet materialized).
    /// Computations (L <@> f) are only materialized when |> compute forces them.
    /// Combinators like <&!> look through variables to find the original ApplyInfo for fusion.
    DeferredComputations: Map<IRId, IRExpr>
    /// Deferred provider reads, keyed by the receiving binding's IRId, lifted
    /// from IRModule.ProviderReads. Consumed by genBinding to emit the
    /// provider's reader (registry-dispatched genReadVar / genReadCompoundVar).
    ProviderReads: Map<IRId, ProviderReadSpec>
    /// Deferred provider writes, keyed by the write binding's IRId, lifted
    /// from IRModule.ProviderWrites. Consumed by genBinding to emit a flatten
    /// prologue + the provider's writer (registry-dispatched genWriteVar).
    ProviderWrites: Map<IRId, ProviderWriteSpec>
    /// STREAMED provider reads whose prologue has been emitted, keyed by the
    /// binding's cpp name. Consuming loop nests look inputs up here: a hit
    /// means "no materialized array exists — inline a fiber read at the S/T
    /// boundary instead of peeling".
    StreamedArrays: Map<string, ProviderReadSpec>
    /// Deferred random-fill constructors, keyed by the receiving binding's IRId,
    /// lifted from IRModule.RandomInits. Consumed by genBinding to emit
    /// allocate<> + a pool fill. Value is a RandomFillSpec (fill_random modulus,
    /// or a rand.uniform/normal key).
    RandomInits: Map<IRId, RandomFillSpec>
    /// Deferred compound-construction constructors (compound(dense, mask)), keyed
    /// by the receiving binding's IRId, lifted from IRModule.CompoundInits.
    /// Consumed by genBinding to emit P0 index materialization + a dense->compact
    /// scatter. Value is (loweredDense, loweredMask).
    CompoundInits: Map<IRId, IRExpr * IRExpr>
    /// Map from grouped-array C++ name to its source GroupKeys C++ name.
    /// Populated by genBinding for IRGroupBy; consulted by method_for codegen
    /// when peeling a ragged outer dimension (Tag = "__group_outer").
    GroupedArrays: Map<string, string>
    /// Block-level `let mut` bindings of ARRAY type, lifted from
    /// IRModule.MutableArrayLets. Consulted by genVarAliasBinding (and
    /// genFuncBody's let unroller): a mut binding whose initializer is an
    /// existing array deep-copies the storage (fresh alloc + pool copy)
    /// instead of binding the Array wrapper by value, which would share the
    /// data pointer and let mutations corrupt the source array.
    MutableArrayLets: Set<IRId>
    /// Accumulated code generation warnings (unsupported IR nodes, fallbacks, etc.)
    Warnings: string list ref
}

/// Module-level expression warnings collector. exprToCpp is pure (returns
/// string) so it can't use CodeGenContext directly. This collects warnings
/// from exprToCpp calls and is synced into CodeGenContext.
///
/// Thread-safety: like the struct caches, this is wrapped in `AsyncLocal<T>`
/// so each parallel test task gets its own ref cell. With a shared
/// `ref []` and parallel test execution, appends from one task would
/// interleave with resets from another, losing or duplicating warnings.
/// Each task's `exprWarningsCell()` returns its own per-flow ref instance.
let private exprWarningsStorage =
    System.Threading.AsyncLocal<string list ref>()

let exprWarningsCell () : string list ref =
    let v = exprWarningsStorage.Value
    // Box to obj before isNull: F# enforces non-nullability on its own
    // `Ref<T>` record type, even though the CLR-level default is null
    // when AsyncLocal hasn't been assigned. The Dictionary-based caches
    // don't need this dance because Dictionary is a BCL class that F#
    // treats as nullable.
    if isNull (box v) then
        let fresh = ref []
        exprWarningsStorage.Value <- fresh
        fresh
    else v

/// Collector for CUDA kernel definitions destined for the .cu file. genCudaKernel
/// appends each __global__ kernel + extern "C" wrapper here; the program
/// assembler reads it to produce the .cu (only when non-empty). Mirrors
/// exprWarningsCell (AsyncLocal per-flow ref) so a deep emission site contributes
/// to a module-level collection without threading a return value everywhere.
let private cudaKernelDefsStorage =
    System.Threading.AsyncLocal<string list ref>()

let cudaKernelDefsCell () : string list ref =
    let v = cudaKernelDefsStorage.Value
    if isNull (box v) then
        let fresh = ref []
        cudaKernelDefsStorage.Value <- fresh
        fresh
    else v

/// Collector for symmetry-vector array declarations that must live at NAMESPACE
/// scope, not function-local. Genuinely symmetric outputs need a real symm array
/// (e.g. `{1,1}`) passed to allocate<> as a non-type template argument. MSVC
/// (C2131) refuses to treat the ADDRESS of a function-local `static constexpr`
/// array as a constant expression, so the decl must be hoisted out of main() to
/// file scope, where its address IS a core constant. (Rectangular outputs dodge
/// this by passing nullptr; symmetric ones cannot â€” they need the actual array.)
/// Mirrors cudaKernelDefsCell. Reset at program assembly, emitted in the preamble.
let private symmDeclsStorage =
    System.Threading.AsyncLocal<string list ref>()

let symmDeclsCell () : string list ref =
    let v = symmDeclsStorage.Value
    if isNull (box v) then
        let fresh = ref []
        symmDeclsStorage.Value <- fresh
        fresh
    else v

/// Append a namespace-scope symm-array decl to the hoist collector (idempotent
/// per distinct name â€” avoids duplicate definitions if the same output symm is
/// referenced twice). The returned name is what the allocate<> call site uses as
/// its template argument; because the decl is now file-scope, its address is a
/// valid constant expression under MSVC.
let hoistSymmDecl (name: string) (symmVec: int list) : string =
    let cell = symmDeclsCell ()
    let values = symmVec |> List.map string |> String.concat ", "
    let decl = sprintf "static constexpr const size_t %s[%d] = {%s};" name symmVec.Length values
    if not (List.contains decl cell.Value) then
        cell.Value <- cell.Value @ [decl]
    name

/// Emit the right-hand side of an output array allocation from a backend-neutral
/// AllocSpec (allocRoutineFor / classifyOutputStorage), not the
/// kernel's Reynolds flag. Returns either:
///   Ok rhs    â€” the `{ allocate...(extents), extents }` brace-init expression
///               the Array<T,N> wrapper is initialized from, or
///   Error msg â€” a diagnostic for a shape with no representable allocator; the
///               call site emits a `#error` line so the TU fails loudly rather
///               than silently mis-allocating.
///
///   AllocDense        -> allocate<promote<T,R>::type, nullptr>(extents)
///   AllocSymmetric    -> allocate<promote<T,R>::type, SYMM>(extents)   (SYMM hoisted)
///   AllocAntisymmetric-> allocate<promote<T,R>::type, {1,..}, false>(extents)
///                        (all-grouped mask + DIAGONALS=false = strict simplex;
///                         unified with the symmetric path â€” antisym is a strict
///                         symmetric grouping. The standalone allocate_antisym in
///                         the runtime header is retained for C++ testing only.)
///
/// `symmArg` is the already-resolved SYMM template argument for the
/// dense/symmetric path ("nullptr" or a hoisted vec name). The antisymmetric
/// path hoists its own all-ones mask (the strict simplex is one group spanning
/// all dims) and passes DIAGONALS=false.
///
/// AllocUnsupported has no representable allocator in the current runtime
/// (allocate_antisym applies the strict shrink at every depth, so it cannot
/// express antisym-plus-free-dimension). It cannot be triggered by a type
/// annotation today (each annotation yields a single index group); the Error
/// path guards against a future front-end change introducing it.
let private emitAllocRhs
        (spec: AllocSpec)
        (elemType: string) (rank: int) (symmArg: string) (extentsName: string)
        : Result<string, string> =
    match spec with
    | AllocAntisymmetric ->
        // Antisymmetric storage is the unified recurrence with a single
        // all-grouped mask {1,1,...} and DIAGONALS=false (strict simplex, no
        // diagonal). This is byte-identical to the former allocate_antisym
        // (verified at ranks 2-4) but flows through the same allocate<> path as
        // symmetric â€” antisym is "a symmetric grouping that happens to be
        // strict". (allocate_antisym is retained in the runtime header for
        // standalone C++ testing but is no longer emitted by Blade.)
        let allOnes = List.replicate rank 1
        let maskName = hoistSymmDecl (sprintf "%s_anti" extentsName) allOnes
        Ok (sprintf "{ allocate<typename promote<%s, %d>::type, %s, false>(%s), %s }"
                elemType rank maskName extentsName extentsName)
    | AllocDense | AllocSymmetric ->
        Ok (sprintf "{ allocate<typename promote<%s, %d>::type, %s>(%s), %s }"
                elemType rank symmArg extentsName extentsName)
    | AllocPerGroupStrict strictVec ->
        // Mixed strictness across groups: emit allocate_strict<T, SYMM, STRICT>.
        // symmArg here MUST be the compact-grouped SYMM mask (antisym grouped
        // like symmetric) â€” the caller builds it via buildSymmVecWithStrict so
        // SYMM and STRICT align position-for-position. Sign is handled lazily on
        // read (canon_*), never baked into storage.
        let strictName = hoistSymmDecl (sprintf "%s_strict" extentsName) strictVec
        Ok (sprintf "{ allocate_strict<typename promote<%s, %d>::type, %s, %s>(%s), %s }"
                elemType rank symmArg strictName extentsName extentsName)
    | AllocUnsupported reason ->
        Error (sprintf "Blade codegen: unsupported antisymmetric output storage â€” %s" reason)

/// Module-level OpenMP test-mode flag. When set, parallel loop nests emit
/// additional thread-coverage instrumentation: each parallel region records
/// which OpenMP threads actually executed iterations, and prints a parseable
/// line after the loop. This lets the test harness verify that the emitted
/// pragmas produce GENUINE parallel regions (the runtime honored them), not
/// just that they are syntactically present.
///
/// This is OFF by default and is toggled ON only by the test harness â€” never
/// in user-facing codegen. The instrumentation would otherwise pollute normal
/// program output and add overhead.
///
/// Thread-safety: AsyncLocal (like exprWarnings above) so parallel test tasks
/// don't race on the flag. Each test flow sets its own value.
let private ompTestModeStorage =
    System.Threading.AsyncLocal<bool ref>()

let ompTestModeCell () : bool ref =
    let v = ompTestModeStorage.Value
    if isNull (box v) then
        let fresh = ref false
        ompTestModeStorage.Value <- fresh
        fresh
    else v

/// Set/clear OpenMP test-mode for the current async flow (called by the harness).
let setOmpTestMode (on: bool) : unit =
    (ompTestModeCell ()).Value <- on

/// Split-timing mode: when on, the generated main() emits TWO timing
/// checkpoints â€” one around input-data setup ("Input Allocation took <t>s")
/// and one around the computation ("<name> completed in <t>s"), instead of a
/// single whole-body clock. The differential-timing harness uses this so the
/// reported compute time excludes input allocation (which the archaic Blade
/// prototype showed can be a large, non-trivial fraction of the total). Default
/// OFF so every other test's single "completed in" line (which value-checks
/// parse around) is unchanged. AsyncLocal for the same reason as ompTestMode:
/// the parallel test runner must not race on the flag.
let private splitTimingModeStorage =
    System.Threading.AsyncLocal<bool ref>()

let splitTimingModeCell () : bool ref =
    let v = splitTimingModeStorage.Value
    if isNull (box v) then
        let fresh = ref false
        splitTimingModeStorage.Value <- fresh
        fresh
    else v

let setSplitTimingMode (on: bool) : unit =
    (splitTimingModeCell ()).Value <- on

let splitTimingModeEnabled () : bool =
    (splitTimingModeCell ()).Value

/// Optional refinement of split-timing: when set to Some name, the compute
/// clock starts immediately before the binding with that NAME (everything
/// before it â€” producers, decompact chains, any setup â€” is attributed to the
/// "input allocation" phase, and only that binding onward is timed). This
/// isolates a single final kernel's runtime from all the work that prepares
/// its inputs. When None, split-timing falls back to the default "first
/// compute binding starts the clock" behavior. Dependency-safe: bindings are
/// emitted in strict ID order, so everything the named binding reads is
/// already emitted (in the setup phase) before the clock starts.
let private splitTimingOnlyBindingStorage =
    System.Threading.AsyncLocal<string option ref>()

let private splitTimingOnlyBindingCell () : string option ref =
    let v = splitTimingOnlyBindingStorage.Value
    if isNull (box v) then
        let fresh = ref None
        splitTimingOnlyBindingStorage.Value <- fresh
        fresh
    else v

let setSplitTimingOnlyBinding (name: string option) : unit =
    (splitTimingOnlyBindingCell ()).Value <- name

let splitTimingOnlyBinding () : string option =
    (splitTimingOnlyBindingCell ()).Value


/// Query whether OpenMP test-mode instrumentation should be emitted.
let ompTestModeEnabled () : bool =
    (ompTestModeCell ()).Value

/// CUDA emission gate. CUDA kernel codegen (genCudaKernel / genCudaKernelSimplicial)
/// emits an `extern "C"` launch declaration + call into the host .cpp and a
/// `__global__` kernel into a separate .cu. That only links when the .cu is
/// compiled and linked alongside the .cpp â€” which happens ONLY in the dedicated
/// CUDA test phase. During ORDINARY (host-only) compilation of the main corpus,
/// the .cu is never built, so emitting a launch call produces an undefined-symbol
/// link error. This flag gates kernel emission so the `cuda` clause stays inert
/// (host fallback) during normal codegen and only becomes active in the CUDA
/// phase. Default OFF (AsyncLocal, like ompTestMode, so parallel test flows don't
/// race). Mirrors the pre-existing "cuda clause is inert pre-flip" invariant.
let private cudaEmitModeStorage =
    System.Threading.AsyncLocal<bool ref>()

let cudaEmitModeCell () : bool ref =
    let v = cudaEmitModeStorage.Value
    if isNull (box v) then
        let fresh = ref false
        cudaEmitModeStorage.Value <- fresh
        fresh
    else v

/// Enable/disable actual CUDA kernel emission (called by the CUDA test phase).
let setCudaEmitMode (on: bool) : unit =
    (cudaEmitModeCell ()).Value <- on

/// Query whether CUDA kernels should actually be emitted (vs host fallback).
let cudaEmitModeEnabled () : bool =
    (cudaEmitModeCell ()).Value

/// MPI emission gate. When ON, kernels with `where mpi` get their iteration
/// domain decomposed across ranks (SPMD: slab/flat-range local loop +
/// MPI_Allgatherv restoring the full output on all ranks), the program gains
/// MPI_Init/Finalize + rank-0 print guards, and `#include <mpi.h>` â€” which
/// links only with -lmsmpi and runs meaningfully only under mpiexec. During
/// ORDINARY compilation the flag is OFF, so the `mpi` clause stays inert
/// (serial host loop) and the default suite never needs an MPI toolchain.
/// Same AsyncLocal pattern as cudaEmitMode (parallel test flows don't race).
let private mpiEmitModeStorage =
    System.Threading.AsyncLocal<bool ref>()

let mpiEmitModeCell () : bool ref =
    let v = mpiEmitModeStorage.Value
    if isNull (box v) then
        let fresh = ref false
        mpiEmitModeStorage.Value <- fresh
        fresh
    else v

/// Enable/disable MPI decomposition emission (`blade run --mpi N`, MPI tests).
let setMpiEmitMode (on: bool) : unit =
    (mpiEmitModeCell ()).Value <- on

/// Query whether MPI decomposition should actually be emitted (vs serial).
let mpiEmitModeEnabled () : bool =
    (mpiEmitModeCell ()).Value

/// The MPI datatype constant for an element type, or None when the type has
/// no direct MPI datatype (bool, complex, structs) â€” such outputs are not
/// MPI-eligible in v1 (the Allgatherv needs a native datatype).
let mpiDatatypeOf (et: ElemType) : string option =
    match et with
    | ETFloat64 -> Some "MPI_DOUBLE"
    | ETFloat32 -> Some "MPI_FLOAT"
    | ETInt64 -> Some "MPI_LONG_LONG"
    | ETInt32 -> Some "MPI_INT"
    | _ -> None

/// Whether any callable in the module requested MPI decomposition. A PURE
/// module predicate (not an emission-time cell) because program assembly
/// computes includes and printCode BEFORE genModule runs. Lifted lambdas land
/// in module.Functions, so both kernel forms (lambda / top-level fn) are seen.
let moduleUsesMpi (modul: IRModule) : bool =
    modul.Functions |> List.exists (fun f -> f.IsMpiParallel)

/// Whether any kernel is the MPI-outer/OpenMP-inner hybrid (`where mpi,
/// omp(...)`). Drives the thread-aware MPI init: hybrid ranks host an OMP
/// team, so main() must request MPI_THREAD_FUNNELED (only the main thread
/// makes MPI calls — every Allgatherv is outside the omp region). Pure-mpi
/// modules keep plain MPI_Init byte-identically.
let moduleHybridMpiOmp (modul: IRModule) : bool =
    modul.Functions |> List.exists (fun f -> f.IsMpiParallel && f.IsOmpParallel)

/// Whether the program CURRENTLY being assembled has MPI scaffolding
/// (emit gate on AND the module uses mpi) — i.e. MPI_Init has run and the
/// __blade_mpi_rank/size globals exist by the time bindings execute.
/// Set by the program generators alongside their mpiOn computation;
/// consumed by the provider-I/O intercepts (distributed packed reads,
/// rank-0 write guards). AsyncLocal for the same reason as the emit gate:
/// parallel test tasks must not race.
let private mpiProgramOnStorage =
    System.Threading.AsyncLocal<bool ref>()

let private mpiProgramOnCell () : bool ref =
    let v = mpiProgramOnStorage.Value
    if isNull (box v) then
        let fresh = ref false
        mpiProgramOnStorage.Value <- fresh
        fresh
    else v

let setMpiProgramOn (on: bool) : unit =
    (mpiProgramOnCell ()).Value <- on

let mpiProgramOn () : bool =
    (mpiProgramOnCell ()).Value


/// Record an expression-level warning and return a C++ expression that causes a compile error.
let exprError (msg: string) : string =
    let cell = exprWarningsCell ()
    cell.Value <- cell.Value @ [msg]
    sprintf "BLADE_CODEGEN_ERROR_%s" (msg.Replace(" ", "_").Replace("'", "").Replace("(", "").Replace(")", "").Replace(",", "").Replace(":", "").Replace("\"", "").ToUpper())

// ----------------------------------------------------------------------------
// Substitution map for Phase C contains-aware mask rendering
// ----------------------------------------------------------------------------
//
// When a mask renderer hoists a contains-set build into its preamble, it
// registers each hoisted IRContains node here (keyed by object reference)
// alongside the C++ name of the precomputed set. As exprToCpp walks the
// predicate body to produce the C++ string for the count/fill loops'
// `if (...)` clauses, the IRContains arm consults this map first: a hit
// emits `<set>.count(<value>)`; a miss falls through to the original
// linear-scan IIFE.
//
// Reference equality is essential. Two structurally-equal IRContains
// nodes can appear at distinct positions (e.g. once inside a predicate
// where the build is hoistable, once elsewhere where it isn't). The map
// uses object identity to distinguish them. The substitution map is
// produced and consumed within a single rendering pass over the same IR
// tree â€” no transformations happen between â€” so references are stable.
//
// Linear search is fine: per-mask probe counts are small (typically 1â€“3).
type SubstMap = (IRExpr * string) list

let private emptySubst : SubstMap = []

let private trySubst (subst: SubstMap) (node: IRExpr) : string option =
    subst
    |> List.tryFind (fun (n, _) -> System.Object.ReferenceEquals(n, node))
    |> Option.map snd

/// Check if an expression is unit/void-typed (should not generate a value)
let isUnitExpr (expr: IRExpr) : bool =
    match expr with
    | IRLit IRLitUnit -> true
    | IRAssign _ -> true
    | IRForRange _ -> true
    | _ -> false

let emptyContext () = {
    VarNames = Map.empty
    Indent = 0
    StaticDecls = []
    TupleChildren = Map.empty
    DeferredComputations = Map.empty
    ProviderReads = Map.empty
    ProviderWrites = Map.empty
    StreamedArrays = Map.empty
    RandomInits = Map.empty
    CompoundInits = Map.empty
    GroupedArrays = Map.empty
    MutableArrayLets = Set.empty
    Warnings = ref []
}

let indent ctx = { ctx with Indent = ctx.Indent + 1 }
let indentStr ctx = String.replicate ctx.Indent "    "

/// Record a codegen warning and return a C++ #error directive.
/// This ensures the generated C++ will not compile silently.
let codegenError (ctx: CodeGenContext) (ind: string) (msg: string) : string list =
    ctx.Warnings.Value <- ctx.Warnings.Value @ [msg]
    [sprintf "%s#error \"Blade codegen: %s\"" ind (msg.Replace("\"", "'"))]

/// C++ reserved words and built-in type names that cannot be used as identifiers
let cppReservedWords = Set.ofList [
    // Types that conflict with Blade names
    "double"; "float"; "int"; "long"; "short"; "char"; "bool"; "void"; "auto"
    "signed"; "unsigned"; "const"; "volatile"; "static"; "extern"; "register"
    // Keywords
    "class"; "struct"; "enum"; "union"; "namespace"; "template"; "typename"
    "virtual"; "override"; "final"; "public"; "private"; "protected"
    "new"; "delete"; "this"; "return"; "if"; "else"; "for"; "while"; "do"
    "switch"; "case"; "break"; "continue"; "goto"; "default"; "try"; "catch"; "throw"
    "sizeof"; "alignof"; "decltype"; "typedef"; "using"; "operator"
    "true"; "false"; "nullptr"; "inline"; "constexpr"; "mutable"
]

/// Sanitize a name to avoid C++ reserved word conflicts
let sanitizeCppName (name: string) : string =
    if Set.contains name cppReservedWords then name + "_"
    else name

let addVarName id name ctx = 
    { ctx with VarNames = Map.add id (sanitizeCppName name) ctx.VarNames }


// ============================================================================
// Type Inference Helper (for code generation)
// ============================================================================

// (The duplicate collectVarRefs walker that lived here is gone â€” audit
// Â§3.2 [now]. Capture computation and match-case usage checks now call
// IR.collectVarRefsIR, the canonical ExprShape-based collector.)

/// Struct-fields registry for `IRFieldAccess` type resolution. ONE cache now
/// (half of audit Â§2.4's duplicated-cache hazard fixed): this forwards to
/// IR.fs's AsyncLocal cache â€” the same registry the lift pass populates â€” so
/// both population points (liftInlineFormsModule entry and genModule entry)
/// fill the same cache from the same module's Types, and a pass reordering
/// or new entry point can no longer leave codegen consulting an empty
/// duplicate. Kept under its historical name; it is just IR.setStructFieldsCache.
let setCodegenStructFieldsCache (types: IRTypeDef list) = IR.setStructFieldsCache types

// (Synthetic sentinel index IDs and the compound partial-index
// classification â€” CompoundIndexForm, classifyCompoundIndexTuple,
// synthSlotId* â€” moved to IR.fs beside the canonical typeOf (audit Â§2.2);
// they resolve here via `open Blade.IR`.)

/// Infer type from an IR expression â€” thin alias of the canonical
/// reconstruction (audit Â§2.2): the full derivation lives in IR.typeOf,
/// shared with the lift pass, so codegen and lift can never diverge on an
/// expression's type again. Kept under its historical name to avoid
/// churning ~90 call sites; new code should call IR.typeOf directly.
let inferExprType (expr: IRExpr) : IRType = IR.typeOf expr

// ============================================================================
// C++ Type Mapping
// ============================================================================

/// Convert a primitive ElemType enum value to C++ type string.
/// Use this only when you have a raw `ElemType` value (e.g., from
/// promoteElemType). For array element types post-B2, use `elemTypeToCpp`
/// which takes the full IRType.
let primTypeToCpp = function
    | ETInt32 -> "int32_t"
    | ETInt64 -> "int64_t"
    | ETFloat32 -> "float"
    | ETFloat64 -> "double"
    | ETComplex64 -> "std::complex<float>"
    | ETComplex128 -> "std::complex<double>"
    | ETBool -> "bool"
    | ETUnit -> "void"
    | ETString -> "std::string"

/// Get rank (total dimensions) from array type
let arrayRank (arr: IRArrayType) = 
    arr.IndexTypes |> List.sumBy (fun i -> i.Rank)

/// Convert an IRType in array-element position to a C++ type string.
/// Dispatches via active patterns:
///   - PrimElem / AnyPrimElem: render the primitive (with units erased).
///   - NamedElem: render the struct/sum's name. Codegen for nominal-typed
///     element arrays is still future work (the `promote<T, k>` template
///     handles them at the C++ level, but Blade-side support for
///     constructing/operating on them is incomplete).
///   - InferElem: codegen-time error. Should never reach here if typecheck
///     and zonking did their job.
///   - InvalidElem: hard error â€” these types have no value-level meaning.
///   - PolyElem: not implemented; specialization should have replaced it.
///   - Other (FuncElem, ArrayElem, TupleElem): delegate to irTypeToCpp,
///     which already renders these correctly. Codegen for arrays-of-these
///     is future work but the type system stops blocking them.
let rec elemTypeToCpp (ty: IRType) : string =
    match ty with
    // Tagged element types must route through irTypeToCpp BEFORE the
    // AnyPrimElem catch, because AnyPrimElem extracts the inner primitive
    // (e.g., int64) which loses the typedef alias name. irTypeToCpp's
    // IRTIdxTagged arm renders IRefNamed as the alias unconditionally.
    | IRTIdxTagged _ -> irTypeToCpp ty
    | AnyPrimElem et -> primTypeToCpp et
    | NamedElem "String" -> "std::string"
    | NamedElem name -> name
    | InferElem id ->
        let cell = exprWarningsCell ()
        cell.Value <- cell.Value @
            [sprintf "elemTypeToCpp: unresolved type variable T?%d in element position" id]
        sprintf "BLADE_UNRESOLVED_ELEM_TYPE_%d" id
    | PolyElem (_, var) ->
        let cell = exprWarningsCell ()
        cell.Value <- cell.Value @
            [sprintf "elemTypeToCpp: PolyElem<%s> in element position is not yet implemented" var]
        "BLADE_NOT_IMPLEMENTED_POLY_ELEM"
    | InvalidElem ->
        let cell = exprWarningsCell ()
        cell.Value <- cell.Value @
            [sprintf "elemTypeToCpp: invalid type in element position: %A" ty]
        "BLADE_INVALID_ELEM_TYPE"
    | _ ->
        // FuncElem / ArrayElem / TupleElem / other: delegate to irTypeToCpp.
        irTypeToCpp ty

/// Convert IR type to C++ type string. Mutually recursive with
/// `elemTypeToCpp` because the array element type is itself an IRType.
and irTypeToCpp = function
    | IRTScalar et -> primTypeToCpp et
    | IRTTuple ts ->
        // Array-shaped elements render as the WRAPPER form (Array<T, N>),
        // not the raw promote<>::type pointer: a std::tuple is a value
        // boundary exactly like a function signature (which already uses
        // the wrapper via arrowSlotTypeForFuncSig), and the wrapper's
        // implicit conversion to the raw pointer means a raw-element tuple
        // silently DROPS extents when a wrapper flows in â€” anything
        // downstream needing `.extents` (auto-print, loop bounds) then
        // breaks. arrowSlotTypeForFuncSig delegates non-array elements
        // back to irTypeToCpp, so scalar/nested-tuple elements render as
        // before.
        sprintf "std::tuple<%s>" (ts |> List.map arrowSlotTypeForFuncSig |> String.concat ", ")
    | IRTUnit -> "void"
    | IRTLoop lt ->
        match lt.Kind with
        | LKMethod -> "BLADE_ERROR_METHOD_LOOP_TYPE"
        | LKObject -> "BLADE_ERROR_OBJECT_LOOP_TYPE"
    | IRTComputation t -> irTypeToCpp t  // Computation<T> erases to T at runtime
    | IRTPoly (base', _) -> 
        // After monomorphization, IRTPoly should not reach codegen.
        // If it does, fall back to the base type.
        irTypeToCpp base'
    | IRTNat _ -> "size_t"
    | IRTIdxTagged (inner, idxRef) ->
        // Parallel to IRTUnitAnnotated: tag is a typecheck-time invariant,
        // erased at codegen. For IRefNamed, render the typedef alias
        // unconditionally â€” a `using <name> = ...;` is emitted alongside
        // the type declaration, so the alias is in scope. For IRefAnon
        // there's no alias to use; render the inner type directly.
        match idxRef with
        // Compound-inner halo window: the param is a POINTER into the
        // materialized compound index's contiguous rank_to_tuple table at the
        // center cell, so w(o) neighbor reads are param-local pointer
        // arithmetic — valid inside lifted standalone kernel functions where
        // no nest-scope alias could reach. v1 is rank-1 masks (array size 1).
        | IRefNamed name when name.StartsWith("__halowin|c:") -> "const std::array<size_t, 1>*"
        // Internal ("__"-prefixed) tags — e.g. a dense halo window — are
        // compiler-synthesized and have no `using` alias, so they must
        // erase to the raw inner type rather than leaking the tag as a C++
        // type name. User aliases (no "__") render their emitted typedef.
        | IRefNamed name when name.StartsWith("__") -> irTypeToCpp inner
        | IRefNamed name -> name
        | IRefAnon _ -> irTypeToCpp inner
    | IRTDist _ ->
        // Dist<r, Ï„> is erased at lowering (a Dist value lowers to the tuple
        // of its packed cumulant component arrays); reaching codegen means
        // the erasure was skipped.
        let cell = exprWarningsCell ()
        cell.Value <- cell.Value @ ["irTypeToCpp: IRTDist reached codegen â€” Dist erasure was skipped at lowering"]
        "BLADE_ERROR_DIST_TYPE"
    | IRTNamed "String" -> "std::string"  // Blade String â†’ C++ std::string
    | IRTNamed name -> name  // Named types (structs, etc.) use their name directly
    | IRTInfer n ->
        let cell = exprWarningsCell ()
        cell.Value <- cell.Value @ [sprintf "unresolved type variable _%d reached codegen" n]
        sprintf "BLADE_UNRESOLVED_TYPE_%d" n
    | IRTUnitAnnotated (inner, _) -> irTypeToCpp inner  // Units erase at codegen
    | IRTGroupKeys _ -> "void*"  // GroupKeys is an opaque runtime structure
    | IRTArrow (slots, result, identity) ->
        // Three shapes possible:
        //   - all-SVal (including empty slots, which means nullary function):
        //     renders as std::function<RetType(ArgType1, ArgType2, ...)>.
        //     Inside the std::function<> template, parameter and result
        //     types that are themselves arrays must render as the WRAPPER
        //     form (`Array<T, N>`), because that's the form the underlying
        //     function declarations use (per genFuncDef's ArrayElem branch).
        //     Helper `arrowSlotTypeForFuncSig` enforces this by using
        //     the wrapper form for array-typed slots and recursing through
        //     `irTypeToCpp` for everything else.
        //   - all-SIdx or all-SIdxVirt with non-empty slots: array-shaped
        //     arrow, renders as `promote<elem, rank>::type` â€” the raw
        //     nested-pointer form. This is what consumers of array
        //     bindings expect: indexing into Array<T,N> or Ragged<T>
        //     (via their operator[]) returns the raw pointer, not the
        //     wrapper. Bindings of the form `let row = arr(i)` therefore
        //     get a `promote<T, N-1>::type` declaration that accepts the
        //     `operator[]` return value directly.
        //     The wrapper form `Array<T, N>` is used at allocation sites
        //     (genArrayLiteral, etc.) where it's spelled out explicitly,
        //     and in function signatures (genFuncDef) where the wrapper
        //     IS the calling convention.
        //   - mixed slot kinds: not yet expressible by language surface; sentinel.
        let isAllSVal = slots |> List.forall (function SVal _ -> true | _ -> false)
        let isAllStored = slots |> List.forall (function SIdx _ -> true | _ -> false)
        let isAllVirtual = slots |> List.forall (function SIdxVirt _ -> true | _ -> false)
        if isAllSVal then
            let paramTypes =
                slots |> List.map (function
                    | SVal t -> arrowSlotTypeForFuncSig t
                    | _ -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.iceCodegen "unreachable â€” guarded by isAllSVal")))
            let paramList = String.concat ", " paramTypes
            sprintf "std::function<%s(%s)>" (arrowSlotTypeForFuncSig result) paramList
        elif (isAllStored || isAllVirtual) && not slots.IsEmpty then
            // Reconstruct an IRArrayType view for rendering
            let indexTypes =
                slots |> List.map (function
                    | SIdx i | SIdxVirt i -> i
                    | _ -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.iceCodegen "unreachable")))
            let arr = {
                ElemType = result
                IndexTypes = indexTypes
                IsVirtual = isAllVirtual
                Identity = identity
            }
            sprintf "promote<%s, %d>::type" (elemTypeToCpp arr.ElemType) (arrayRank arr)
        else
            let cell = exprWarningsCell ()
            cell.Value <- cell.Value @ ["IRTArrow with mixed slot kinds reached codegen (no language construct produces these yet)"]
            "BLADE_UNSUPPORTED_ARROW_TYPE"

/// Render a type for use inside a std::function<...> signature (i.e. as a
/// parameter or return type of the function). Array types render as the
/// wrapper form (`Array<T, N>`) to match what function declarations use
/// at the call boundary; everything else delegates to `irTypeToCpp`.
///
/// Without this helper, std::function<> templates would be filled with
/// the raw-pointer form (`promote<T, N>::type`), which doesn't match the
/// wrapper-form return type that `genFuncDef` emits for array-returning
/// functions. The mismatch would block `funcs[i] = arrayReturningFunc;`
/// assignments because the function-pointer signature wouldn't be
/// convertible to the std::function<...> target.
and arrowSlotTypeForFuncSig (ty: IRType) : string =
    // Note: this helper sits inside the elemTypeToCpp/irTypeToCpp
    // recursion group, so it cannot forward-reference helpers defined
    // later in the file (isRaggedArrayType, isDepIdxArrayType,
    // isCompoundArrayType). For ragged, DepIdx, or compound array types
    // appearing inside a function signature, the generic Array<T, N>
    // rendering here would mismatch the wrapper rendering at the
    // declaration site (Ragged<T> / Compound<T, RANK>). No current test
    // exercises that combination; revisit if a future one does.
    match ty with
    | ArrayElem arr ->
        sprintf "Array<%s, %d>" (elemTypeToCpp arr.ElemType) (arrayRank arr)
    | _ -> irTypeToCpp ty


/// Extract the element type from an expression that should be array-shaped or scalar.
/// On failure (type not array/scalar after upstream inference), record a codegen
/// warning and emit a `#error` line as the rendered code. The point is to fail
/// loudly rather than silently emit code that might miscompile â€” e.g., indexing
/// with a float, or narrowing int64 keys to double in a sort comparator.
/// Returns (elemType as IRType, optional error code).
let inferElemTypeStrict (ctx: CodeGenContext) (ind: string) (expr: IRExpr) (opName: string) : IRType * string list =
    match inferExprType expr with
    | ArrayElem arr -> (arr.ElemType, [])
    | IRTScalar _ as t -> (t, [])
    | t ->
        let msg = sprintf "%s: could not determine element type from expression (got %A) â€” likely a typechecker or IR bug" opName t
        let errLines = codegenError ctx ind msg
        // Sentinel: the #error makes the C++ refuse to compile, so the
        // Float64 we return is never actually exercised in valid output.
        (IRTScalar ETFloat64, errLines)


/// Variant of inferElemTypeStrict for ctx-less callers (e.g., inside
/// `exprToCpp` or other pure expression-rendering helpers). Mirrors the
/// "peel array combinator wrappers and infer the elem type" pattern that
/// inline-form codegen uses. On failure, records a warning into the
/// AsyncLocal exprWarnings collector and returns a sentinel C++ type
/// string that won't compile â€” so a regression at this layer surfaces
/// at the C++ compile step rather than silently emitting `double` and
/// hoping for the best.
///
/// The sentinel string is intentionally not a valid C++ identifier
/// fragment in context: `BLADE_UNRESOLVED_INLINE_ELEM_TYPE`. The g++
/// "unknown type name" error pinpoints the site precisely, while the
/// warning collected here gives the high-level reason.
let inferInlineElemTypeStr (opName: string) (form: IRExpr) : string =
    let arrExpr =
        match form with
        // IRMask deliberately NOT extracted: its result elem is Bool (the
        // presence array), independent of the source elem -- fall through to
        // `form` so inferExprType's IRMask arm answers.
        | IRSort (a, _)
        | IRIntersect (a, _) | IRUnion (a, _) -> a
        | IRUnique a -> a
        | _ -> form
    match inferExprType arrExpr with
    | ArrayElem a -> elemTypeToCpp a.ElemType
    | IRTScalar et -> primTypeToCpp et
    | t ->
        let cell = exprWarningsCell ()
        cell.Value <- cell.Value @
            [sprintf "%s: could not determine element type from inline form (got %A) â€” likely a typechecker or IR bug" opName t]
        "BLADE_UNRESOLVED_INLINE_ELEM_TYPE"


// ============================================================================
// C++ Expression Generation
// ============================================================================

/// Convert binary operator to C++ string
let binOpToCpp = function
    | IRAdd -> "+" | IRSub -> "-" | IRMul -> "*" | IRDiv -> "/"
    | IRMod -> "%" | IRCaret -> "pow"  // Special handling needed
    | IREq -> "==" | IRNeq -> "!=" 
    | IRLt -> "<" | IRLe -> "<=" | IRGt -> ">" | IRGe -> ">="
    | IRAnd -> "&&" | IROr -> "||"

/// Convert unary operator to C++ string
let unaryOpToCpp = function
    | IRNeg -> "-"
    | IRNot -> "!"
    | IRConj -> "std::conj"   // function-call form; exprToCppCore/exprToCppSimple
                              // special-case IRConj for the complex-vs-real
                              // decision (real conj is the identity)
    // std::real/std::imag/std::arg are <complex> free functions with C++11
    // arithmetic-type overloads (std::real(double)=x, std::imag(double)=0,
    // std::arg(double)=0/pi), so they render through the generic unary arm on
    // both complex and real operands with no special-casing.
    | IRReal -> "std::real"
    | IRImag -> "std::imag"
    | IRArg -> "std::arg"
    | IRMath name -> "std::" + name  // function-call form via the generic
                                     // `op(expr)` unary arm

/// True iff a type's underlying scalar is a complex element type. Used to
/// decide whether conj must emit std::conj (complex) or is the identity (real).
let rec isComplexType (t: IRType) : bool =
    match t with
    | IRTScalar (ETComplex64 | ETComplex128) -> true
    | IRTIdxTagged (inner, _) -> isComplexType inner
    | IRTUnitAnnotated (inner, _) -> isComplexType inner
    | _ -> false

/// Project an IRType to its underlying scalar element type, if any.
let rec scalarElemOf (t: IRType) : ElemType option =
    match t with
    | IRTScalar et -> Some et
    | IRTIdxTagged (inner, _) -> scalarElemOf inner
    | IRTUnitAnnotated (inner, _) -> scalarElemOf inner
    | _ -> None

/// Coerce a rendered scalar operand so it matches the C++ std::complex operator
/// overload set for a complex-typed binop result. std::complex's arithmetic
/// operators are same-type only: `complex<double> * 2` and `complex<double> *
/// floatVar` both fail template deduction (T deduces to two types), and
/// `complex<double> ⊕ complex<float>` has no overload. So any operand whose
/// element type is not exactly the result's component type is cast: integers
/// and mismatched-width floats to the component real type; a narrower complex
/// is widened via std::complex's converting constructor. A matching-width real
/// float or same-width complex passes through unchanged.
let coerceComplexOperand (resultElem: ElemType) (operandElem: ElemType) (rendered: string) : string =
    match resultElem with
    | ETComplex128 ->
        match operandElem with
        | ETComplex128 | ETFloat64 -> rendered
        | ETComplex64 -> sprintf "std::complex<double>(%s)" rendered
        | ETFloat32 | ETInt64 | ETInt32 -> sprintf "(double)(%s)" rendered
        | _ -> rendered
    | ETComplex64 ->
        match operandElem with
        | ETComplex64 | ETFloat32 -> rendered
        | ETInt64 | ETInt32 -> sprintf "(float)(%s)" rendered
        | _ -> rendered
    | _ -> rendered

/// Emit a binop, inserting complex-operand coercions when the promoted result
/// is complex. `renderBin` is the caller's fallback (`(l op r)`), used verbatim
/// for the common non-complex path so nothing changes there.
let emitBinOpWithComplexCoercion
        (op: IRBinOp) (l: IRExpr) (r: IRExpr) (lStr: string) (rStr: string)
        (inferTy: IRExpr -> IRType) (binToCpp: IRBinOp -> string) : string =
    match scalarElemOf (inferTy l), scalarElemOf (inferTy r) with
    | Some le, Some re ->
        match promoteElemType le re with
        | Some ((ETComplex64 | ETComplex128) as resElem) ->
            let lC = coerceComplexOperand resElem le lStr
            let rC = coerceComplexOperand resElem re rStr
            sprintf "(%s %s %s)" lC (binToCpp op) rC
        | _ -> sprintf "(%s %s %s)" lStr (binToCpp op) rStr
    | _ -> sprintf "(%s %s %s)" lStr (binToCpp op) rStr

/// Render a float as a C++ double literal. Two invariants the old
/// `sprintf "%g"` violated (silently, at every literal site):
///   1. ROUND-TRIP precision â€” %g truncates to 6 significant digits, so
///      0.6931471805599453 became 0.693147 in the generated C++ (fatal for
///      CG coefficients and any test pinned finer than 1e-6).
///   2. FLOAT SPELLING â€” %g renders 2.0 as the bare token `2`, an int
///      literal in C++, so `2.0 / 3.0` compiled to integer division `2 / 3`
///      and evaluated to 0.
/// "R" on .NET 7 is shortest-round-trip; invariant culture guards against
/// decimal-comma locales; the suffix check restores the `.0` spelling.
let floatToCppLiteral (f: float) : string =
    if System.Double.IsNaN f then "std::numeric_limits<double>::quiet_NaN()"
    elif System.Double.IsPositiveInfinity f then "std::numeric_limits<double>::infinity()"
    elif System.Double.IsNegativeInfinity f then "(-std::numeric_limits<double>::infinity())"
    else
        let s = f.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
        if s.Contains "." || s.Contains "e" || s.Contains "E" then s else s + ".0"

/// Quote a Blade string value as a C++ string literal. Escapes the minimal
/// set that would otherwise break the surrounding "..." token: backslash,
/// double-quote, and the four common control characters. Other characters
/// pass through, including UTF-8 multibyte sequences (which are valid as
/// raw bytes inside a C++ "..." literal).
let escapeStringLit (s: string) : string =
    let sb = System.Text.StringBuilder()
    sb.Append('"') |> ignore
    for c in s do
        match c with
        | '\\' -> sb.Append("\\\\") |> ignore
        | '"'  -> sb.Append("\\\"") |> ignore
        | '\n' -> sb.Append("\\n") |> ignore
        | '\r' -> sb.Append("\\r") |> ignore
        | '\t' -> sb.Append("\\t") |> ignore
        | '\000' -> sb.Append("\\0") |> ignore
        | _ -> sb.Append(c) |> ignore
    sb.Append('"') |> ignore
    sb.ToString()

/// Simplified exprToCpp that doesn't recurse into complex IR nodes
/// Used for kernel bodies in inline generation
let rec exprToCppSimple (names: Map<IRId, string>) (expr: IRExpr) : string =
    match expr with
    | IRLit (IRLitInt n) -> sprintf "%dL" n
    | IRLit (IRLitFloat f) -> floatToCppLiteral f
    | IRLit (IRLitBool b) -> if b then "true" else "false"
    | IRLit (IRLitString s) -> sprintf "std::string(%s)" (escapeStringLit s)
    | IRLit IRLitUnit -> "((void)0)"
    | IRVar (id, _) -> Map.tryFind id names |> Option.defaultValue (sprintf "__v%d" id)
    | IRParam (name, _, _) -> name
    | IRBinOp (_, op, l, r) ->
        let lStr = exprToCppSimple names l
        let rStr = exprToCppSimple names r
        if op = IRCaret then sprintf "pow(%s, %s)" lStr rStr
        else emitBinOpWithComplexCoercion op l r lStr rStr inferExprType binOpToCpp
    | IRUnaryOp (IRConj, e) ->
        let inner = exprToCppSimple names e
        if isComplexType (inferExprType e) then sprintf "std::conj(%s)" inner
        else inner
    | IRUnaryOp (op, e) -> sprintf "%s(%s)" (unaryOpToCpp op) (exprToCppSimple names e)
    | IRGuard (cond, body) ->
        sprintf "(%s ? %s : 0.0)" (exprToCppSimple names cond) (exprToCppSimple names body)
    | other -> sprintf "BLADE_UNSUPPORTED_EXPR_%s" (other.GetType().Name.ToUpper())

/// Convert IRLit to C++ literal string
let litToCpp (lit: IRLit) : string =
    match lit with
    | IRLitInt n -> sprintf "%dL" n
    | IRLitFloat f -> floatToCppLiteral f
    | IRLitBool b -> if b then "true" else "false"
    | IRLitString s -> sprintf "std::string(%s)" (escapeStringLit s)
    | IRLitUnit -> "((void)0)"  // Valid C++ no-op; should be elided by callers

/// Detect whether an IRArrayType represents a ragged array (any RaggedIdx
/// variant). True when at least one IndexType has a ragged tag:
///   - __raggedidx_inline   : inferred from a ragged literal
///   - __raggedidx          : closed RaggedIdx<lens> from a type annotation
///   - __raggedidx_opaque   : opaque RaggedIdx<_> (kernel param / sub-array)
/// All three share the property that the array shape carries a per-row
/// lengths companion at codegen time.
///
/// Defined here (before exprToCpp) because IRApp's call-site emission needs
/// it to decide whether to pass an `_lens` companion argument. Other ragged-
/// aware sites (genArrayLiteral, print path) live further down and use the
/// same predicate.
let isRaggedArrayType (arrTy: IRArrayType) : bool =
    arrTy.IndexTypes |> List.exists (fun idx -> isRaggedFamilyKind idx.IxKind)

/// A rank-1 value whose single axis is a RAGGED-FAMILY inner dimension: a
/// peeled/indexed row of a ragged literal (__raggedidx*), a DepIdx-allocated
/// array (__depidx_inner), or a group_by result (__group_member). All three
/// share the same runtime row shape -- a pointer plus a per-row length --
/// and are represented as `RaggedRow<T>` when bound (`.len`, operator[]).
/// This is the ONE predicate for "does this rank-1 operand carry its length
/// inline as .len rather than via .extents", used consistently by the
/// sub-view binding emission, reduce (both forms), IRExtent, and print, so
/// the accessor never disagrees with the declared type.
let isRaggedRowType (arrTy: IRArrayType) : bool =
    arrTy.IndexTypes.Length = 1 && isRaggedRowKind arrTy.IndexTypes.[0].IxKind

/// Detect whether an IRArrayType represents a DepIdx array â€” outer Idx plus
/// an inner record whose Extent is a function of the outer iteration index.
/// Recognized by the `__depidx_inner` tag on a non-first index. Once
/// allocated (lens computed from formula at construction), the runtime
/// layout matches a ragged array â€” same `_lens` / `_offsets` / row-pointer
/// companions â€” so iteration-time predicates treat both as "has row-lengths
/// companion" via `isRaggedArrayType OR isDepIdxArrayType`. The literal
/// allocation path differs (lens come from the formula, not from literal
/// structure), so genArrayLiteral keeps a separate branch.
let isDepIdxArrayType (arrTy: IRArrayType) : bool =
    arrTy.IndexTypes |> List.exists (fun idx ->
        idx.IxKind = IxKDepInner)

/// Detect whether an IRArrayType is a CompoundIdx<mask> array -- a masked
/// product space (formalism 4.5) whose valid-tuple set is tabulated at runtime
/// (popcount of the mask) and rendered as `Compound<T, RANK>`, accessed by
/// whole-tuple gather rather than a peel chain.
///
/// Matches the `__compoundidx` tag (from TyCompoundIdx lowering) by EXACT
/// equality, deliberately NOT a prefix test: `__compoundidx_dynamic` is an
/// unrelated feature (the group_by compound-key outer index, a CSR / tuple-hash
/// structure with its own codegen) and must not be rendered as Compound<T,RANK>.
let isCompoundArrayType (arrTy: IRArrayType) : bool =
    arrTy.IndexTypes |> List.exists (fun idx ->
        idx.IxKind = IxKCompound)

/// Render an array type as its C++ type string. Handles four cases:
///   * CompoundIdx<mask>: `Compound<T, RANK>` -- a masked product space whose
///     valid tuples are tabulated at runtime; accessed by whole-tuple gather,
///     with RANK the mask dimensionality. Checked first; its tag is disjoint
///     from the ragged/dep-idx tags, so the ordering is for clarity only.
///   * Rank-1 ragged/dep-idx â†’ `RaggedRow<T>` (a peeled-row slice â€” produced
///     when the kernel side of a `method_for` over a 2D ragged peels the
///     inner-ragged dim into the kernel's binding; the runtime layout is
///     a row pointer + length).
///   * Rank â‰¥ 2 ragged/dep-idx â†’ `Ragged<T>` (full multi-row container).
///   * Otherwise â†’ `Array<T, N>` (uniform shape).
///
/// The rank-1 distinction matters because `Ragged<T>::operator[]` returns
/// `RaggedRow<T>`, not `T`. A lambda whose param IS a peeled row must
/// declare the row type directly so `g[0]` resolves to the element. This
/// stays consistent with user-written annotations: at the source level
/// rank-1 ragged is malformed (no prior index to drive the lookup) and
/// surfaces as the `__error_ragged_no_prior` placeholder; the only rank-1
/// ragged types Lowering hands to codegen come from kernel-side peeling.
let cppArrayTypeStr (arr: IRArrayType) : string =
    if isCompoundArrayType arr then
        // Compound<T, RANK>: a masked product space. RANK is the mask's
        // dimensionality, carried on the compound index type's Rank (a generic
        // "dimensions spanned" -- a rank here, not a symmetric arity). Read off
        // the compound index type directly rather than via arrayRank so that a
        // future surrounding-dims form would not fold extra axes into RANK.
        let rank =
            arr.IndexTypes
            |> List.tryFind (fun idx -> idx.IxKind = IxKCompound)
            |> Option.map (fun idx -> idx.Rank)
            |> Option.defaultValue (arrayRank arr)
        sprintf "Compound<%s, %d>" (elemTypeToCpp arr.ElemType) rank
    elif isRaggedArrayType arr || isDepIdxArrayType arr then
        if arr.IndexTypes.Length = 1 then
            sprintf "RaggedRow<%s>" (elemTypeToCpp arr.ElemType)
        else
            sprintf "Ragged<%s>" (elemTypeToCpp arr.ElemType)
    else
        sprintf "Array<%s, %d>" (elemTypeToCpp arr.ElemType) (arrayRank arr)

/// P0 (compound-index materialization keystone): emit the C++ that builds a
/// `compound_index_t<RANK>` from a Blade bool mask VALUE, independent of any
/// provider. This is the extraction of the index-construction step that used to
/// live only inside the NetCDF provider's genReadCompoundVar (welded to a
/// NetCDF dense-read + scatter); pulled out here so any source-level compound
/// producer -- a scatter over a Blade dense array, a range<CompoundIdx> driver,
/// a fill_random-built compound -- can materialize the index the same way.
///
/// Inputs:
///   maskName : the C++ variable name of a `nested_array_utilities::Array<bool,
///              RANK>` mask already in scope (its present cells select the valid
///              tuples).
///   rank     : RANK (the mask's dimensionality == the compound's leading rank).
///   idxName  : base name for the emitted index variable.
///
/// Emits (in order):
///   std::array<size_t, RANK> <idxName>_extents = { <maskName>.extents[0], ... };
///   size_t <idxName>_grid = <maskName>.extents[0] * ... ;
///   bool* <idxName>_pool = nested_array_utilities::pool_base(<maskName>.data);
///   std::vector<bool> <idxName>_maskvec(<idxName>_pool, <idxName>_pool + <idxName>_grid);
///   compound_index_t<RANK>* <idxName> = new compound_index_t<RANK>("<idxName>", <idxName>_extents, <idxName>_maskvec);
///
/// Flattening relies on allocate<>'s single-contiguous-pool invariant: a
/// rectangular mask's pool_base gives the row-major (DFS) flat buffer that
/// compound_index_t's enumerate() also walks, so the mask bit order matches the
/// index's tuple enumeration. Returns (emitted lines, the index variable name).
/// The index is heap-allocated (matches the provider); the caller owns bundling
/// it into a Compound<T,RANK> wrapper (P0b) once it has a compact data buffer.
let genCompoundIndexFromMask (maskName: string) (rank: int) (idxName: string) : string list * string =
    let extentTerms = [ for d in 0 .. rank - 1 -> sprintf "%s.extents[%d]" maskName d ]
    let extentsInit = String.concat ", " extentTerms
    let gridExpr = String.concat " * " extentTerms
    let lines =
        [ sprintf "std::array<size_t, %d> %s_extents = { %s };" rank idxName extentsInit
          sprintf "size_t %s_grid = %s;" idxName gridExpr
          sprintf "bool* %s_pool = nested_array_utilities::pool_base(%s.data);" idxName maskName
          sprintf "std::vector<bool> %s_maskvec(%s_pool, %s_pool + %s_grid);" idxName idxName idxName idxName
          sprintf "compound_index_t<%d>* %s = new compound_index_t<%d>(\"%s\", %s_extents, %s_maskvec);"
                  rank idxName rank idxName idxName idxName ]
    (lines, idxName)

/// Wrapper-emission helper. Takes an IRCallable and produces a local
/// C++ closure that mediates between the lifted function's signature
/// (regular params + capture params) and the consumer's expected
/// callable shape (regular params only). The wrapper is what lets
/// `IRVar(callable.Id, funcType)` stand in at any consumer site that
/// needs to call the callable as if it had only its regular params.
///
/// Shape:
///     auto __wrap_<id>_<suffix> = [&](P1 p1, P2 p2) { return <fnName>(p1, p2, c1, c2); };
///
/// The wrapper takes only the callable's regular params. Captures are
/// hidden from the wrapper's signature and forwarded into the lifted
/// function call from the surrounding scope via the `[&]` capture chain.
/// The `[&]` form binds capture-named identifiers by reference, matching
/// the `T& cap_name` capture-param signature that `genFuncDef` emits on
/// the lifted function side. As long as the wrapper is emitted in a scope
/// where the capture-named variables are visible (typically the same
/// scope where the original `lambda(...)` literal appeared), the chain
/// closes correctly.
///
/// Return-type rendering uses `auto`. This avoids two complications: (1)
/// computing the return type explicitly for callables whose RetType is
/// IRTInfer or a synthesized shape, and (2) handling void returns â€”
/// since C++14, `return voidFn()` in an `auto`-returning context deduces
/// to `void` and is legal. Consumers that need a specific signature
/// (e.g., an `std::function<T(P)>`-typed slot) can still wrap the wrapper.
///
/// The wrapper name combines the callable id with a caller-supplied
/// suffix. Same callable referenced from multiple consumer sites at the
/// same C++ scope would otherwise produce duplicate names (since the
/// callable id alone isn't unique-per-use). Callers pass a suffix that
/// disambiguates â€” typically the let binding's name or a fresh counter
/// from the IR builder. Pass empty string when the caller guarantees a
/// single emission per scope.
///
/// Returns (code lines, wrapper name). Callers prepend the code lines to
/// the enclosing block and use the wrapper name wherever they'd
/// previously have inlined the lambda body.
let genCallableWrapper (suffix: string) (callable: IRCallable) : string list * string =
    let safeName = sanitizeCppName callable.Name
    let wrapperName =
        if suffix = "" then sprintf "__wrap_%d" callable.Id
        else sprintf "__wrap_%d_%s" callable.Id suffix
    let paramSig =
        callable.Params
        |> List.map (fun p ->
            match p.Type with
            | ArrayElem arr -> sprintf "%s %s" (cppArrayTypeStr arr) p.Name
            | _ -> sprintf "%s %s" (irTypeToCpp p.Type) p.Name)
        |> String.concat ", "
    let regularArgs = callable.Params |> List.map (fun p -> p.Name)
    let captureArgs = callable.Captures |> List.map (fun c -> c.Name)
    let allArgs = (regularArgs @ captureArgs) |> String.concat ", "
    let code =
        [sprintf "auto %s = [&](%s) { return %s(%s); };" wrapperName paramSig safeName allArgs]
    (code, wrapperName)

/// Evaluate a DepIdx inner-extent formula for a specific outer index value.
/// The formula is an IRExpr referencing the outer record's Id via IRVar; we
/// substitute the concrete integer `i` for that Id and fold constants. Returns
/// None if the expression contains anything we can't reduce statically (free
/// variables, IRParam, non-arithmetic ops). Used at construction time to
/// produce the `_lens` table for DepIdx-annotated literals; in that context a
/// None result means the formula is dynamic and we'd need runtime evaluation
/// (deferred â€” for now we error out).
let rec evalDepIdxExtent (outerId: IRId) (i: int) (expr: IRExpr) : int option =
    match expr with
    | IRLit (IRLitInt n) -> Some (int n)
    | IRVar (vid, _) when vid = outerId -> Some i
    | IRBinOp (_, op, l, r) ->
        match evalDepIdxExtent outerId i l, evalDepIdxExtent outerId i r with
        | Some a, Some b ->
            match op with
            | IRAdd -> Some (a + b)
            | IRSub -> Some (a - b)
            | IRMul -> Some (a * b)
            | IRDiv when b <> 0 -> Some (a / b)
            | IRMod when b <> 0 -> Some (a % b)
            | _ -> None
        | _ -> None
    | IRUnaryOp (IRNeg, e) -> evalDepIdxExtent outerId i e |> Option.map (fun n -> -n)
    | _ -> None

/// Convert IRExpr to C++ expression string
let rec exprToCppCore (subst: SubstMap) (names: Map<IRId, string>) (expr: IRExpr) : string =
    match expr with
    | IRLit lit -> litToCpp lit
    | IRVar (id, _) ->
        // A reference to a lifted callable WITH captures cannot stand bare
        // in value position: the lifted C++ function carries trailing
        // capture params, so the bare name neither converts to a
        // std::function of the surface arity nor direct-calls correctly.
        // Close the captures with an inline forwarding closure — the same
        // [&]-by-name chain as genCallableWrapper/genVarAliasBinding, valid
        // in any scope where the captured locals are visible (i.e. wherever
        // the lambda literal / partial application appeared). Capture-free
        // callables keep the bare name (a plain function converts fine).
        // The `callable.Id = id` guard matters: resolveCallable also sees
        // THROUGH let-aliases, but an alias var is already a std::function
        // of the surface arity with captures closed (genVarAliasBinding) —
        // render it by name; only the direct lifted-callable reference
        // needs the closure.
        match resolveCallable expr with
        | Some callable when callable.Id = id && not (List.isEmpty callable.Captures) ->
            let safeName = sanitizeCppName callable.Name
            let paramSig =
                callable.Params
                |> List.map (fun p ->
                    match p.Type with
                    | ArrayElem arr -> sprintf "%s %s" (cppArrayTypeStr arr) p.Name
                    | _ -> sprintf "%s %s" (irTypeToCpp p.Type) p.Name)
                |> String.concat ", "
            let allArgs =
                (callable.Params |> List.map (fun p -> p.Name))
                @ (callable.Captures |> List.map (fun c -> c.Name))
                |> String.concat ", "
            sprintf "[&](%s) { return %s(%s); }" paramSig safeName allArgs
        | _ ->
            match Map.tryFind id names with
            | Some name -> name
            | None -> sprintf "__v%d" id
    | IRParam (name, _, _) -> name
    | IRHaloUnhash (w, off) ->
        // halo window read over a masked domain: coordinate of the present
        // cell at (center + off). The window param is a pointer into the
        // compound index's contiguous rank_to_tuple table at the center
        // (genElementBindingNew's compound-halo arm), so a signed subscript
        // IS the ordinal step — self-contained in lifted kernel functions.
        // No int64 cast: the coordinate is size_t — the Array wrapper's exact
        // operator[] type; a cast would make the wrapper-vs-raw-pointer
        // subscript overloads ambiguous.
        let wS = exprToCppCore subst names w
        sprintf "%s[(%dL)][0]" wS off
    | IRBinOp (_, op, l, r) ->
        let lStr = exprToCppCore subst names l
        let rStr = exprToCppCore subst names r
        if op = IRCaret then
            sprintf "pow(%s, %s)" lStr rStr
        else
            emitBinOpWithComplexCoercion op l r lStr rStr inferExprType binOpToCpp
    | IRUnaryOp (IRConj, e) ->
        // conj is std::conj on complex operands; the identity on reals
        // (mathematically conj(x)=x for real x, and std::conj(double) would
        // wrongly promote to std::complex<double>, so emit the operand bare).
        let inner = exprToCppCore subst names e
        if isComplexType (inferExprType e) then sprintf "std::conj(%s)" inner
        else inner
    | IRUnaryOp (op, e) ->
        sprintf "%s(%s)" (unaryOpToCpp op) (exprToCppCore subst names e)
    | IRIf (cond, thenBr, elseBr) ->
        sprintf "(%s ? %s : %s)" 
            (exprToCppCore subst names cond) 
            (exprToCppCore subst names thenBr) 
            (exprToCppCore subst names elseBr)
    | IRTuple exprs ->
        sprintf "std::make_tuple(%s)" (exprs |> List.map (exprToCppCore subst names) |> String.concat ", ")
    | IRComplex (re, im) ->
        // Determine width from the component type. checkExpr enforces
        // that Complex128 components are Float64 and Complex64 are
        // Float32, so we inspect either component (they match) and pick
        // the corresponding C++ template instantiation.
        let cppType =
            match inferExprType re with
            | IRTScalar ETFloat32 -> "std::complex<float>"
            | _ -> "std::complex<double>"  // Float64 default
        sprintf "%s(%s, %s)" cppType (exprToCppCore subst names re) (exprToCppCore subst names im)
    | IRTupleProj (e, i, isFlat) ->
        if not isFlat then
            sprintf "std::get<%d>(%s)" i (exprToCppCore subst names e)
        else
            // Flat projection into potentially nested tuple â€” compute navigation path
            let parentTy = inferExprType e
            let rec findPath (ty: IRType) (targetFlat: int) : int list =
                match ty with
                | IRTTuple ts ->
                    let mutable offset = 0
                    let mutable found = None
                    for idx in 0 .. ts.Length - 1 do
                        if found.IsNone then
                            let count = IR.flattenTupleLeaves ts.[idx] |> List.length
                            if targetFlat < offset + count then
                                match ts.[idx] with
                                | IRTTuple _ -> found <- Some (idx :: findPath ts.[idx] (targetFlat - offset))
                                | _ -> found <- Some [idx]
                            offset <- offset + count
                    found |> Option.defaultValue [i]
                | _ -> [i]
            let path = findPath parentTy i
            path |> List.fold (fun acc idx -> sprintf "std::get<%d>(%s)" idx acc) (exprToCppCore subst names e)
    | IRFieldAccess (obj, field) ->
        sprintf "%s.%s" (exprToCppCore subst names obj) field
    | IRStructLit (typeName, fields) ->
        let fieldInits = fields |> List.map (fun (fname, e) -> 
            sprintf ".%s = %s" fname (exprToCppCore subst names e)) |> String.concat ", "
        sprintf "%s { %s }" typeName fieldInits
    | IRIndex (arr, indices, _) ->
        // Carousel substitution (reference equality, see SubstMap): a dense
        // halo window read hoisted to a rotating local renders as that local.
        (match trySubst subst expr with
         | Some local -> local
         | None -> renderIndexExpr subst names arr indices)
    | IRApp (func, args, _) ->
        // Function signatures take Array<T,N> / Ragged<T> wrappers
        // natively, one argument per Blade param. Array args pass through
        // as-is (the wrapper carries its own shape via .extents/.lens/
        // .offsets); no companion-arg synthesis. Non-array args render
        // through exprToCpp normally.
        // A callee that IS a lifted callable with captures (direct
        // reference, not a let-alias: resolveCallable sees through aliases,
        // but an alias var is already a std::function with captures closed
        // — call that by name) is called directly with the capture args
        // appended, since the lifted signature is regular params + capture
        // params.
        let funcStr, captureArgs =
            match func, resolveCallable func with
            | IRVar (fid, _), Some callable when callable.Id = fid && not (List.isEmpty callable.Captures) ->
                (sanitizeCppName callable.Name,
                 callable.Captures |> List.map (fun c -> c.Name))
            | _ -> (exprToCppCore subst names func, [])
        let argStrs =
            args |> List.collect (fun a ->
                let argStr = exprToCppCore subst names a
                match a, inferExprType a with
                | (IRVar _ | IRParam _), ArrayElem _ -> [argStr]
                | _ -> [argStr])
        sprintf "%s(%s)" funcStr (argStrs @ captureArgs |> String.concat ", ")
    | IRLet (id, value, body) ->
        renderLetExpr subst names id value body
    | IRMethodFor _ -> exprError "loop object used as value"
    | IRObjectFor _ -> exprError "loop object used as value"
    | IRApplyCombinator _ | IRComposeApply _ ->
        exprError "unevaluated computation used as value - use |> compute"
    | IRReduceCompute _ ->
        // Statement-shaped (declares accumulators + a loop nest); no IIFE
        // form yet. Reached only if a fused reduce lands in expression
        // position â€” bind it to a `let` first.
        exprError "reduce over a deferred computation must be bound to a let (expression position is not supported yet)"
    | IRCompute inner -> 
        // compute forces evaluation of a lazy computation
        match inner with
        | IRApplyCombinator info -> genApplyCombinatorExpr subst names info
        | _ -> exprToCppCore subst names inner  // For non-combinator compute, just evaluate
    | IRPure e -> exprToCppCore subst names e     // pure wraps value
    | IRRank arr -> 
        // Rank is known statically from the type
        let rank = match inferExprType arr with
                   | ArrayElem at -> arrayRank at
                   | _ -> 0
        sprintf "%dL" rank
    | IRExtent (arr, dim) ->
        renderExtentExpr subst names arr dim
    | IRReduce (arrExpr, kernelExpr, initExpr) ->
        renderReduceExpr subst names arrExpr kernelExpr initExpr
    | IRProdSum args ->
        // Fused product-sum Î£_t Î _â„“ argâ„“[t]: one loop, one accumulator,
        // rendered as an IIFE so it composes in any expression position â€”
        // most importantly inside method_for kernels, where the moment
        // formers' fiber kernels land. Bound comes from the first operand
        // (TypeCheck rejects provably mismatched static extents).
        let argStrs = args |> List.map (exprToCppCore subst names)
        let elemStr =
            match inferExprType (List.head args) with
            | ArrayElem at -> elemTypeToCpp at.ElemType
            | t -> elemTypeToCpp t
        let product = argStrs |> List.map (fun a -> sprintf "%s[__pt]" a) |> String.concat " * "
        sprintf "[&]() { %s __ps = 0; for (size_t __pt = 0; __pt < %s.extents[0]; __pt++) { __ps += %s; } return __ps; }()"
            elemStr (List.head argStrs) product
    | IRContains (arrExpr, valueExpr) ->
        // Linear-scan membership test as an IIFE returning bool. (A prior
        // hoist-set fusion for contains-inside-mask has been removed; every
        // contains now renders here as a linear scan.)
        let arrStr = exprToCppCore subst names arrExpr
        let valStr = exprToCppCore subst names valueExpr
        // A rank-1 compound operand (filtered set) scans its compact buffer:
        // bound = cardinality, elements via .data[i]. Dense stays .extents/[].
        let isR1Compound =
            match inferExprType arrExpr with
            | ArrayElem at -> isCompoundArrayType at && at.IndexTypes.Length = 1
            | _ -> false
        let bound = if isR1Compound then sprintf "%s.idx->cardinality" arrStr else sprintf "%s.extents[0]" arrStr
        let elemAt = if isR1Compound then sprintf "%s.data[__ci]" arrStr else sprintf "%s[__ci]" arrStr
        sprintf "[&]() { for (size_t __ci = 0; __ci < %s; __ci++) { if (%s == %s) return true; } return false; }()"
            bound elemAt valStr

    | IRArity (Some n, _) -> sprintf "%d" n
    | IRArity (None, paramName) -> 
        // Arity of poly pack - use tuple_size on the named parameter
        sprintf "std::tuple_size_v<std::decay_t<decltype(%s)>>" paramName
    | IRBind (comp, cont) ->
        // Monadic bind - comp >>= cont
        sprintf "%s(%s)" (exprToCppCore subst names cont) (exprToCppCore subst names comp)
    | IRReynolds (kernel, isAntisym) ->
        // Reynolds operator wraps kernel
        exprError "reynolds wrapper in expression position"
    | IRZip arrs ->
        // In expression context (e.g. inside a kernel body), zip produces a tuple
        sprintf "std::make_tuple(%s)" (arrs |> List.map (exprToCppCore subst names) |> String.concat ", ")
    | IRStack arrs ->
        exprError "stack not yet implemented in codegen"
    | IRSlice (arr, dim, start, stop) ->
        exprError "slice not yet implemented in codegen"
    | IRCurry (arr, idx, resultRank) ->
        sprintf "%s[%s]" (exprToCppCore subst names arr) (exprToCppCore subst names idx)
    | IRTupleCons (head, tail) ->
        sprintf "std::tuple_cat(std::make_tuple(%s), %s)" (exprToCppCore subst names head) (exprToCppCore subst names tail)
    | IRTupleDecons tuple ->
        exprToCppCore subst names tuple  // Decons is handled by projection
    | IRMatch (scrutinee, cases) ->
        renderMatchExpr subst names scrutinee cases
    | IRNth -> exprError "nth keyword not supported in expression position"
    | IRZero -> "0"
    | IRPolyIndex (pack, idx) ->
        // For static index, use std::get; otherwise runtime indexing
        match idx with
        | IRLit (IRLitInt n) -> sprintf "std::get<%d>(%s)" n (exprToCppCore subst names pack)
        | _ -> sprintf "%s[%s]" (exprToCppCore subst names pack) (exprToCppCore subst names idx)
    | IRParallel (a, b, _) ->
        exprError "parallel combinator in expression position"
    | IRFusion (a, b) ->
        exprError "fusion combinator in expression position"
    | IRChoice (a, b) ->
        let aStr = exprToCppCore subst names a
        let bStr = exprToCppCore subst names b
        sprintf "(%s != 0 ? %s : %s)" aStr aStr bStr
    | IRFallback _ ->
        exprError "<|:> (allocated-fallback) in expression position — it combines whole arrays; bind it and materialize with |> compute"
    | IRGuard (cond, body) ->
        // guard(p, c) â†’ p ? c : 0 (type-appropriate zero)
        let condStr = exprToCppCore subst names cond
        let bodyStr = exprToCppCore subst names body
        let zeroStr =
            match inferExprType body with
            | IRTScalar ETBool -> "false"
            | IRTScalar ETInt64 | IRTScalar ETInt32 -> "0L"
            | IRTIdxTagged (IRTScalar (ETInt64 | ETInt32), _) -> "0L"
            | _ -> "0.0"
        sprintf "(%s ? %s : %s)" condStr bodyStr zeroStr
    | IRCompose (f, g) ->
        // f >> g = [&](auto... args) { return g(f(args...)); }
        let fStr = exprToCppCore subst names f
        let gStr = exprToCppCore subst names g
        sprintf "[&](auto... __args) { return %s(%s(__args...)); }" gStr fStr
    | IRComposeObj (f, g) ->
        exprError "compose_obj in expression position"
    | IRComposeMeth (f, g) ->
        exprError "compose_meth in expression position"
    | IRArrayProduct (a, b) ->
        exprError "array_product in expression position"
    | IRFunctorMap (f, c) ->
        exprError "functor_map in expression position"
    | IRConstraintCheck (cond, message, span) ->
        // Expression-position fallback: a portable IIFE so the guard still
        // fires if it lands somewhere other than a statement slot.
        sprintf "([&](){ if (!(%s)) { blade_rt::panic(\"BL8001\", \"%s\", %s); } return 0; })()"
            (exprToCppCore subst names cond) message (panicSpanArgs span)
    | IRAssign (target, value) ->
        let targetStr =
            match target with
            | LVVar id -> Map.tryFind id names |> Option.defaultValue (sprintf "__v%d" id)
            | LVIndex (arr, idxs) ->
                let arrStr = exprToCppCore subst names arr
                let idxStr = idxs |> List.map (fun i -> sprintf "[%s]" (exprToCppCore subst names i)) |> String.concat ""
                sprintf "%s%s" arrStr idxStr
            | LVField (obj, f) -> sprintf "%s.%s" (exprToCppCore subst names obj) f
            | LVOther e -> exprError "invalid assignment target"
        sprintf "%s = %s" targetStr (exprToCppCore subst names value)
    | IRForRange (vid, lo, hi, body) ->
        exprError "for-range loop in expression position"
    | IROpaqueExtent ->
        // IROpaqueExtent is a marker that lives inside an IRIndexType.Extent
        // slot; it should never reach exprToCpp directly. If it does, the
        // loop builder failed to substitute the surrounding context for the
        // sub-array binding (the ExtentArrayRef path). Surface a visible
        // error rather than silently emit the wrong value.
        exprError "opaque-extent marker reached expression rendering â€” kernel-param sub-array was not bound to a concrete extent at the peel point (codegen routing bug)"
    | other -> exprError (sprintf "unsupported IR node: %s" (other.GetType().Name))

/// Generate inline combinator application as an IIFE expression.
/// This is used when L <@> f appears in expression context (not as a let
/// binding's RHS or a function-body return). The let-binding and return
/// cases route through genApplyCombinator (the statement form) which uses
/// the full LoopNestCodeGen machinery and handles every shape uniformly.
///
/// LIMITATION (carried forward from the original implementation): this
/// function only supports the 2-array Cartesian-sum-reduce shape inline.
/// Other shapes (1-array, 3+ arrays, array output, anything with
/// commutativity/Reynolds/co-iteration) emit a BLADE_CODEGEN_ERROR sentinel
/// and crash the C++ build. The principled fix is to make this a thin
/// wrapper around genApplyCombinator's statement output:
///   `[&]() { <statements>; return <name>; }()`.
/// That delegation is deferred â€” no current test exercises an inline
/// non-2-array combinator, so the cleanup waits for one. Bindings and
/// returns already go through genApplyCombinator, which is where the real
/// machinery lives.

and renderIndexExpr (subst: SubstMap) (names: Map<IRId, string>) arr indices : string =
    let arrStr = exprToCppCore subst names arr
    // STEP 3 (lazy sign-on-read): if `arr` is an array whose SOLE index slot
    // is a compact group (Symmetric/Antisymmetric/Hermitian, arity >= 2) and
    // the indices fully cover that group, the access may be NON-CANONICAL
    // (e.g. A[5][2] on an upper-triangular store). Emit a fold-fetch-transform
    // read instead of a raw subscript: sort the index tuple (tracking parity),
    // detect implicit-zero (strict diagonal), left-justify to storage coords,
    // fetch, and apply the class's read transform (identity / negate-on-swap /
    // conjugate-on-swap). The behavior descriptors come from IIndexTypeBehavior
    // so the read path never branches on symmetry class inline.
    //
    // This fires ONLY for compact-group random access â€” the case nothing
    // currently produces canonically-guaranteed. Plain/rectangular reads, and
    // (for now) all iteration reads, keep the raw subscript. Iteration reads
    // are migrated to skip the fold in step 4 (they are canonical by
    // construction); folding them here would be correct but is deferred to keep
    // the bulk-compute hot path zero-overhead per the formalism cost model.
    let rawSubscript () =
        let idxStr = indices |> List.map (fun i -> sprintf "[%s]" (exprToCppCore subst names i)) |> String.concat ""
        sprintf "%s%s" arrStr idxStr
    let lazyCompactRead () : string option =
        match inferExprType arr with
        | ArrayElem arrTy ->
            // Generalized lazy read: the index-type list may contain multiple
            // compact groups (e.g. an interior antisym decompact result
            // AntisymIdx<2> -> Idx -> AntisymIdx<2>) interleaved with plain
            // freed slots. Each compact group folds INDEPENDENTLY (its own
            // canon_fold / zero-guard / left-justify / transform); plain slots
            // pass their index through. The fetch subscript interleaves folded
            // coords and plain indices in slot order; the read value chains
            // canon_transform over every group's parity. Fires only when at
            // least one slot is a compact group AND the indices fully cover
            // the whole index list; otherwise raw subscript.
            let slots = arrTy.IndexTypes
            let totalRank = slots |> List.sumBy (fun s -> max 1 s.Rank)
            let anyCompact = slots |> List.exists (fun s -> s.Symmetry <> SymNone && (max 1 s.Rank) >= 2)
            if anyCompact && indices.Length = totalRank then
                let elemTypeStr = irTypeToCpp arrTy.ElemType
                let idxStrs = indices |> List.map (fun i -> exprToCppCore subst names i) |> Array.ofList
                // Walk slots, consuming arity indices each. For compact groups
                // emit fold-locals; collect (fetchSubParts, transformChain).
                let sb = System.Text.StringBuilder()
                let mutable cursor = 0
                let mutable groupNum = 0
                let mutable fetchParts = []      // C++ subscript pieces in slot order
                let mutable transforms = []      // (parityVar, tfStr) per compact group
                let mutable ok = true
                for s in slots do
                    let a = max 1 s.Rank
                    let these = [ for j in 0 .. a - 1 -> idxStrs.[cursor + j] ]
                    cursor <- cursor + a
                    if s.Symmetry <> SymNone && a >= 2 then
                        let beh = behaviorFor s.Symmetry
                        let strictArg =
                            match beh.Canonicalize () with
                            | CanonSortStrict -> "true"
                            | CanonSort | CanonNone -> "false"
                        let tf =
                            match beh.ReadTransform () with
                            | TfIdentity -> "nested_array_utilities::ReadTransform::Identity"
                            | TfNegateOnSwap -> "nested_array_utilities::ReadTransform::NegateOnSwap"
                            | TfConjugateOnSwap -> "nested_array_utilities::ReadTransform::ConjugateOnSwap"
                        let g = groupNum
                        groupNum <- groupNum + 1
                        sb.Append(sprintf "std::array<size_t,%d> __g%d = { %s }; " a g (String.concat ", " these)) |> ignore
                        sb.Append(sprintf "bool __z%d; int __p%d = nested_array_utilities::canon_fold<%d>(__g%d, %s, __z%d); " g g a g strictArg g) |> ignore
                        sb.Append(sprintf "if (__z%d) return %s(); " g elemTypeStr) |> ignore
                        sb.Append(sprintf "auto __c%d = nested_array_utilities::canon_left_justify<%d>(__g%d, %s); " g a g strictArg) |> ignore
                        for j in 0 .. a - 1 do
                            fetchParts <- fetchParts @ [ sprintf "[__c%d[%d]]" g j ]
                        transforms <- transforms @ [ (sprintf "__p%d" g, tf) ]
                    elif s.Symmetry <> SymNone && a = 1 then
                        // arity-1 compact (e.g. SymIdx<1> = Idx): no fold.
                        fetchParts <- fetchParts @ [ sprintf "[%s]" these.[0] ]
                    else
                        // plain slot(s): pass each index through directly.
                        for t in these do fetchParts <- fetchParts @ [ sprintf "[%s]" t ]
                if not ok then None
                else
                    let fetch = String.concat "" fetchParts
                    // chain transforms: v0 = transform(base, p0); v1 = transform(v0,p1); ...
                    let body = System.Text.StringBuilder()
                    body.Append(sb.ToString()) |> ignore
                    body.Append(sprintf "%s __v = %s%s; " elemTypeStr arrStr fetch) |> ignore
                    match transforms with
                    | [] ->
                        // No real fold happened (shouldn't reach: anyCompact true) â€” return raw.
                        body.Append("return __v;") |> ignore
                    | _ ->
                        let mutable prev = "__v"
                        transforms |> List.iteri (fun i (pv, tf) ->
                            let outv = sprintf "__tv%d" i
                            body.Append(sprintf "%s %s = nested_array_utilities::canon_transform<%s>(%s, %s, %s); " elemTypeStr outv elemTypeStr prev pv tf) |> ignore
                            prev <- outv)
                        body.Append(sprintf "return %s;" prev) |> ignore
                    Some (sprintf "([&]() -> %s { %s }())" elemTypeStr (body.ToString()))
            else None
        | _ -> None
    // Compound tuple indexing (formalism 4.5): when `arr` is a
    // Compound<T,RANK> and the first index is a tuple, the tuple's coords
    // gather through the compound's linearize rather than a peel chain.
    //   full (j = k):
    //     - no trailing dims        -> arr({coords})            : scalar
    //     - trailing dims, all given -> arr({coords}, trail)    : scalar
    //     - trailing dims remain     -> arr.row({coords})       : T* sub-view
    //   partial (j < k): the residual is reconstituted by one of the four
    //     runtime helpers (window / gather x dense / compound), dispatched
    //     on whether the pinned axes form a leading prefix and on the
    //     residual rank -- see the CompoundPartial arm. Trailing regular
    //     dims combined with a partial read remain gated (hard error).
    let compoundRead () : string option =
        match inferExprType arr with
        | ArrayElem arrTy when isCompoundArrayType arrTy ->
            // Rank-1 compound scalar sugar: `C(i)` on a rank-1 compound
            // (the filtered-set case) is the 1-tuple read `C((i))` --
            // there is no way to even WRITE a 1-tuple literal at the
            // surface, so the scalar spelling is the canonical one.
            // Normalize to the tuple path; without this it fell to the
            // raw-subscript peel (`C[i]`), which Compound cannot compile.
            let k1 =
                arrTy.IndexTypes
                |> List.tryFind (fun ix -> ix.IxKind = IxKCompound)
                |> Option.map (fun ix -> ix.Rank)
            let indices =
                match k1, indices with
                | Some 1, first :: rest when (match first with IRTuple _ -> false | _ -> true) ->
                    IRTuple [first] :: rest
                | _ -> indices
            match indices with
            | (IRTuple coords) :: trailingIdxs ->
                let k =
                    arrTy.IndexTypes
                    |> List.tryFind (fun ix -> ix.IxKind = IxKCompound)
                    |> Option.map (fun ix -> ix.Rank)
                    |> Option.defaultValue coords.Length
                let trailingDims =
                    match arrTy.IndexTypes with
                    | _ :: rest -> rest
                    | [] -> []
                match classifyCompoundIndexTuple k coords with
                | CompoundPartial (pinned, freePos) ->
                    // Partial compound indexing (formalism 4.5). The pinned
                    // coordinates (a leading prefix for a short tuple;
                    // arbitrary axes for a full-arity wildcard tuple with
                    // `IRLit IRLitUnit` sentinels at the free positions)
                    // are removed; the residual spans the free axes. Four
                    // reconstitution shapes, all pure expressions:
                    //   prefix,    rank >= 2 : make_partial_compound --
                    //     contiguous shared-window residual, no data copy.
                    //   prefix,    rank == 1 : make_partial_window --
                    //     dense Idx window sharing the parent data
                    //     (Array<T,1> with a heap-allocated extent).
                    //   scattered, rank >= 2 : make_partial_compound_gather
                    //     -- deep-copy gather into a fresh Compound<T,RR>.
                    //   scattered, rank == 1 : make_partial_gather_dense --
                    //     deep-copy gather into a fresh Array<T,1>.
                    let j = pinned.Length
                    let residualRank = freePos.Length
                    if not (List.isEmpty trailingIdxs) then
                        raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf "Partial compound indexing combined with a SUPPLIED trailing index is not yet supported; leave the trailing dim free (omit it or write `_`), or index the residual separately (let r = B((...)); r(...)).")))
                    elif trailingDims.Length > 1 then
                        raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf "Partial compound indexing with %d trailing dimensions is not supported (multi-trailing compounds are unsupported throughout: the wrapper stores only the trailing-stride product, not per-dim extents)." trailingDims.Length)))
                    else
                        // One free trailing dim rides along at zero data cost
                        // on the shared paths: the compact layout is lex-
                        // sorted with the trailing block innermost, so a
                        // residual COMPOUND (rank >= 2) keeps the parent's
                        // trailing_stride through the SAME helpers, and a
                        // rank-1 prefix residual becomes a contiguous rank-2
                        // window (make_partial_window_trail: shared data,
                        // fresh row table only). Scattered pins still
                        // gather, now copying whole trailing blocks.
                        let hasTrail = not (List.isEmpty trailingDims)
                        let elemStr = elemTypeToCpp arrTy.ElemType
                        // (size_t) casts: coordinate exprs are int64-typed at
                        // the Blade level; a bare int64 VARIABLE inside a
                        // std::array<size_t,J> brace-init is a narrowing
                        // error (literals are exempt as constant exprs).
                        let pinnedVals = pinned |> List.map (fun (_, c) -> sprintf "(size_t)(%s)" (exprToCppCore subst names c))
                        let pinnedArr = sprintf "std::array<size_t, %d>{%s}" j (String.concat ", " pinnedVals)
                        let isPrefix = (pinned |> List.map fst) = [0 .. j - 1]
                        if isPrefix && residualRank >= 2 then
                            Some (sprintf "nested_array_utilities::make_partial_compound<%s, %d, %d>(%s, %s)"
                                          elemStr k j arrStr pinnedArr)
                        elif isPrefix then
                            let fn = if hasTrail then "make_partial_window_trail" else "make_partial_window"
                            Some (sprintf "nested_array_utilities::%s<%s, %d, %d>(%s, %s)"
                                          fn elemStr k j arrStr pinnedArr)
                        else
                            let posArr =
                                sprintf "std::array<size_t, %d>{%s}" j
                                    (pinned |> List.map (fst >> string) |> String.concat ", ")
                            if residualRank >= 2 then
                                Some (sprintf "nested_array_utilities::make_partial_compound_gather<%s, %d, %d>(%s, %s, %s)"
                                              elemStr k j arrStr pinnedArr posArr)
                            else
                                let fn = if hasTrail then "make_partial_gather_dense_trail" else "make_partial_gather_dense"
                                Some (sprintf "nested_array_utilities::%s<%s, %d, %d>(%s, %s, %s)"
                                              fn elemStr k j arrStr pinnedArr posArr)
                | CompoundFull ->
                    // j = k: full index. Build the coord array literal.
                    // (size_t) casts, same rationale as the partial path:
                    // int64-typed coordinate VARIABLES (e.g. lifted-lambda
                    // params) are a narrowing error in a std::array<size_t>
                    // brace-init; literals and size_t loop vars were fine,
                    // which is why this path survived until a kernel body
                    // was lifted with int64_t params.
                    let coordStrs = coords |> List.map (fun c -> sprintf "(size_t)(%s)" (exprToCppCore subst names c))
                    let coordArr = sprintf "std::array<size_t, %d>{%s}" k (String.concat ", " coordStrs)
                    if List.isEmpty trailingDims then
                        // No trailing dims: scalar cell.
                        Some (sprintf "%s(%s)" arrStr coordArr)
                    elif trailingIdxs.Length >= trailingDims.Length then
                        // Trailing dims fully supplied: scalar via operator()
                        // with the (single) trailing offset. Multi-trailing is
                        // not yet supported (trailing_stride is a product, not
                        // per-dim), matching the rest of the compound codegen;
                        // the first trailing index is the offset.
                        let trailStr =
                            match trailingIdxs with
                            | t :: _ -> exprToCppCore subst names t
                            | [] -> "0"
                        Some (sprintf "%s(%s, %s)" arrStr coordArr trailStr)
                    else
                        // Trailing dims remain unindexed: sub-view base pointer.
                        // Any partially-supplied trailing indices then subscript
                        // the returned T* in slot order.
                        let restSubs =
                            trailingIdxs
                            |> List.map (fun i -> sprintf "[%s]" (exprToCppCore subst names i))
                            |> String.concat ""
                        Some (sprintf "%s.row(%s)%s" arrStr coordArr restSubs)
            | _ -> None  // compound array but first index isn't a tuple (shouldn't reach: TypeCheck enforces the tuple form)
        | _ -> None
    match compoundRead () with
    | Some code -> code
    | None ->
        (match lazyCompactRead () with
         | Some code -> code
         | None -> rawSubscript ())


and renderMatchExpr (subst: SubstMap) (names: Map<IRId, string>) scrutinee cases : string =
    // Generate nested ternary for match expressions
    let scrut = exprToCppCore subst names scrutinee
    let rec genCase (cases: IRMatchCase list) : string =
        match cases with
        | [] -> "([&]() -> double { blade_rt::panic(\"BL8002\", \"Blade: non-exhaustive match\", nullptr, 0); return 0; }())"
        | [case] ->
            // Last case - assume it matches (wildcard or variable)
            // But if there's a guard, we must still check it.
            let abortExpr = "([&]() -> double { blade_rt::panic(\"BL8002\", \"Blade: non-exhaustive match\", nullptr, 0); return 0; }())"
            let wrapGuard (bodyStr: string) (names': Map<IRId, string>) : string =
                match case.Guard with
                | Some guard ->
                    let guardStr = exprToCppCore subst names' guard
                    sprintf "(%s ? %s : %s)" guardStr bodyStr abortExpr
                | None -> bodyStr
            match case.Pattern with
            | IRPatVar varId ->
                // Bind variable and evaluate body (only if variable is used)
                let varUsed =
                    (collectVarRefsIR case.Body).Contains varId ||
                    (case.Guard |> Option.map (fun g -> (collectVarRefsIR g).Contains varId) |> Option.defaultValue false)
                if varUsed then
                    let varName = sprintf "__match_%d" varId
                    let names' = Map.add varId varName names
                    let bodyStr = exprToCppCore subst names' case.Body
                    let guardedBody = wrapGuard bodyStr names'
                    sprintf "[&]() { auto %s = %s; return %s; }()" varName scrut guardedBody
                else
                    wrapGuard (exprToCppCore subst names case.Body) names
            | IRPatWild ->
                wrapGuard (exprToCppCore subst names case.Body) names
            | IRPatLit lit ->
                let litStr = litToCpp lit
                let bodyStr = wrapGuard (exprToCppCore subst names case.Body) names
                sprintf "(%s == %s ? %s : %s)" scrut litStr bodyStr abortExpr
            | IRPatVariant (ctorName, tag, innerOpt, isEnum) ->
                // Last variant case â€” extract payload and evaluate body
                match innerOpt with
                | Some (IRPatVar varId) ->
                    let varName = sprintf "__match_%d" varId
                    let names' = Map.add varId varName names
                    let extractExpr = sprintf "std::get<%s_T>(%s).value" ctorName scrut
                    let bodyStr = exprToCppCore subst names' case.Body
                    let guardedBody = wrapGuard bodyStr names'
                    sprintf "[&]() { auto %s = %s; return %s; }()" varName extractExpr guardedBody
                | _ ->
                    wrapGuard (exprToCppCore subst names case.Body) names
            | IRPatTuple innerPats ->
                // Last tuple case â€” bind each element
                let bindings =
                    innerPats |> List.mapi (fun idx pat ->
                        match pat with
                        | IRPatVar varId -> Some (varId, sprintf "__match_%d" varId, idx)
                        | _ -> None)
                    |> List.choose id
                let bindingDecls = bindings |> List.map (fun (_, name, idx) ->
                    sprintf "auto %s = std::get<%d>(%s)" name idx scrut) |> String.concat "; "
                let names' = bindings |> List.fold (fun acc (id, name, _) -> Map.add id name acc) names
                let bodyStr = exprToCppCore subst names' case.Body
                let guardedBody = wrapGuard bodyStr names'
                sprintf "[&]() { %s; return %s; }()" bindingDecls guardedBody
            | _ ->
                wrapGuard (exprToCppCore subst names case.Body) names
        | case :: rest ->
            let restStr = genCase rest
            match case.Pattern with
            | IRPatLit lit ->
                let litStr = litToCpp lit
                let bodyStr = 
                    match case.Guard with
                    | Some guard -> 
                        let guardStr = exprToCppCore subst names guard
                        sprintf "(%s ? %s : %s)" guardStr (exprToCppCore subst names case.Body) restStr
                    | None -> exprToCppCore subst names case.Body
                sprintf "(%s == %s ? %s : %s)" scrut litStr bodyStr restStr
            | IRPatVar varId ->
                let varUsed =
                    (collectVarRefsIR case.Body).Contains varId ||
                    (case.Guard |> Option.map (fun g -> (collectVarRefsIR g).Contains varId) |> Option.defaultValue false)
                if varUsed then
                    let varName = sprintf "__match_%d" varId
                    match case.Guard with
                    | Some guard ->
                        // Variable pattern with guard, variable used
                        let guardStr = exprToCppWithVarCore subst names varId varName guard
                        let bodyStr = exprToCppWithVarCore subst names varId varName case.Body
                        sprintf "[&]() { auto %s = %s; return %s ? %s : %s; }()" varName scrut guardStr bodyStr restStr
                    | None ->
                        // Variable pattern without guard - always matches, variable used
                        let bodyStr = exprToCppWithVarCore subst names varId varName case.Body
                        sprintf "[&]() { auto %s = %s; return %s; }()" varName scrut bodyStr
                else
                    match case.Guard with
                    | Some guard ->
                        // Variable unused, but has guard
                        let guardStr = exprToCppCore subst names guard
                        let bodyStr = exprToCppCore subst names case.Body
                        sprintf "(%s ? %s : %s)" guardStr bodyStr restStr
                    | None ->
                        // Variable unused, no guard - always matches (like wildcard)
                        exprToCppCore subst names case.Body
            | IRPatWild ->
                match case.Guard with
                | Some guard ->
                    let guardStr = exprToCppCore subst names guard
                    let bodyStr = exprToCppCore subst names case.Body
                    sprintf "(%s ? %s : %s)" guardStr bodyStr restStr
                | None ->
                    // Wildcard without guard - always matches
                    exprToCppCore subst names case.Body
            | IRPatTuple innerPats ->
                // Tuple pattern - bind each element
                let rec collectVarBindings (pats: IRPattern list) (idx: int) : (IRId * string) list =
                    match pats with
                    | [] -> []
                    | IRPatVar varId :: rest ->
                        let varName = sprintf "__match_%d" varId
                        (varId, varName) :: collectVarBindings rest (idx + 1)
                    | _ :: rest -> collectVarBindings rest (idx + 1)
                
                let bindings = collectVarBindings innerPats 0
                let bindingDecls = bindings |> List.mapi (fun idx (_, name) ->
                    sprintf "auto %s = std::get<%d>(%s)" name idx scrut) |> String.concat "; "
                
                // Extend names map with bindings
                let names' = bindings |> List.fold (fun acc (id, name) -> Map.add id name acc) names
                
                match case.Guard with
                | Some guard ->
                    let guardStr = exprToCppCore subst names' guard
                    let bodyStr = exprToCppCore subst names' case.Body
                    sprintf "[&]() { %s; return %s ? %s : %s; }()" bindingDecls guardStr bodyStr restStr
                | None ->
                    let bodyStr = exprToCppCore subst names' case.Body
                    sprintf "[&]() { %s; return %s; }()" bindingDecls bodyStr
            | IRPatVariant (ctorName, tag, innerOpt, isEnum) ->
                // Variant pattern - check variant type and optionally bind inner value
                let checkExpr =
                    if isEnum then sprintf "%s == %s" scrut ctorName
                    else sprintf "std::holds_alternative<%s_T>(%s)" ctorName scrut
                
                match innerOpt with
                | Some (IRPatVar varId) ->
                    // Variant with inner value binding
                    let varName = sprintf "__match_%d" varId
                    let names' = Map.add varId varName names
                    let extractExpr = sprintf "std::get<%s_T>(%s).value" ctorName scrut
                    let bodyStr = exprToCppCore subst names' case.Body
                    sprintf "(%s ? [&]() { auto %s = %s; return %s; }() : %s)" checkExpr varName extractExpr bodyStr restStr
                | Some _ ->
                    // Other inner patterns - fallback
                    let bodyStr = exprToCppCore subst names case.Body
                    sprintf "(%s ? %s : %s)" checkExpr bodyStr restStr
                | None ->
                    // Variant without inner value
                    let bodyStr = exprToCppCore subst names case.Body
                    sprintf "(%s ? %s : %s)" checkExpr bodyStr restStr
            | _ ->
                // Unsupported pattern - fallback
                sprintf "(true ? %s : %s)" (exprToCppCore subst names case.Body) restStr
    genCase cases


and renderReduceExpr (subst: SubstMap) (names: Map<IRId, string>) arrExpr kernelExpr (initExpr: IRExpr option) : string =
    // Inline reduction as an IIFE. Mirrors the genBinding form's loop but
    // wraps it in `[&]() { ... }()` so it can appear in expression context
    // â€” kernel bodies (lambda(g) -> reduce(g)) and arithmetic
    // (x + reduce(arr) / count). Capture-by-reference picks up arr,
    // arr_extents, and any names referenced by the kernel body.
    //
    // Empty-array policy matches the genBinding form: skip the runtime
    // guard when the extent is statically proven > 0 (typecheck has
    // already rejected statically-empty inputs); emit the guard for
    // dynamic extents (mask results, group_by groups, etc.).
    let arrStr = exprToCppCore subst names arrExpr
    let elemType =
        match inferExprType arrExpr with
        | ArrayElem a -> a.ElemType
        | _ -> IRTScalar ETFloat64  // Fallback; typecheck enforces array input
    let elemStr = elemTypeToCpp elemType
    let isStaticallyNonEmpty =
        match inferExprType arrExpr with
        | ArrayElem at when at.IndexTypes.Length >= 1 ->
            match tryEvalIntIR at.IndexTypes.[at.IndexTypes.Length - 1].Extent with
            | Some n -> n > 0L
            | None -> false
        | _ -> false
    // A compound array's present cells live in a flat compact buffer
    // (`.data`, length `.size()` = cardinality * trailing_stride), so a
    // reduction walks that buffer directly -- there is no `.extents` or
    // operator[]. This reduces over ALL present values (the cardinality
    // cells for an all-dims mask, spanning the trailing block for a partial
    // mask). cardinality is a runtime value, so the empty guard always emits.
    let isCompound =
        match inferExprType arrExpr with
        | ArrayElem at -> isCompoundArrayType at
        | _ -> false
    // A reduce operand can also be a peeled ragged/dep-idx row, which lowers
    // to RaggedRow<T>. RaggedRow exposes its length as `.len` (a bare size_t),
    // NOT `.extents[0]` â€” and it is indexed `g[i]` via its operator[]. Detect
    // it so the length bound uses `.len`; the default `%s[%s]` access already
    // works for RaggedRow.
    // Only a RANK-1 ragged/dep-idx operand is a RaggedRow<T> (with an inline
    // `.len`). A rank-2+ Ragged<T> has `.extents`/`.lens`, not `.len`, so it
    // must fall through to the default `.extents[0]`. Same predicate the peel
    // emission and IRExtent use, so the length accessor stays consistent with
    // the operand's actual C++ type.
    let isRagged =
        match inferExprType arrExpr with
        | ArrayElem at -> isRaggedRowType at
        | _ -> false
    let reduceAccAt (i: string) =
        if isCompound then sprintf "%s.data[%s]" arrStr i else sprintf "%s[%s]" arrStr i
    let reduceBound =
        if isCompound then sprintf "(%s.idx->cardinality * %s.trailing_stride)" arrStr arrStr
        elif isRagged then sprintf "%s.len" arrStr
        else sprintf "%s.extents[0]" arrStr
    let reduceNonEmpty = isStaticallyNonEmpty && not isCompound && not isRagged
    // Reduce-kernel resolution via `resolveCallable`. The fold
    // kernel emits as a local wrapper closure inside the IIFE; the
    // fold loop invokes the wrapper on `(acc, arr[__ri])`. The
    // wrapper lives inside the IIFE's `[&]() { ... }()` scope, so
    // name collisions across multiple reduces at the same outer
    // scope are structurally avoided â€” each IIFE is its own block.
    //
    // This migration was reverted earlier in 3c.2 because operator
    // sections (`(+)`, `(*)`, ...) lowered with hardcoded Float64
    // param/return types, and the wrapper-based path exposed the
    // resulting signature mismatch on non-Float64 source arrays
    // (e.g., `reduce(int_array, (+))` triggered -Wfloat-conversion
    // when the Float64 return assigned back to an Int64
    // accumulator). The section lowering was subsequently fixed:
    // `lowerTypedSection` now reads the section's resolved type
    // from the typed expression, and `inferReduce` unifies the
    // kernel's params with the array element type before zonking.
    // With sections honest, wrappers carry the right signature and
    // the migration is safe.
    match resolveCallable kernelExpr with
    | Some callable when callable.Params.Length = 2 ->
        let (wrapperCode, wname) = genCallableWrapper "" callable
        let wrapperStr = wrapperCode |> String.concat " "
        match initExpr with
        | Some initE ->
            // 3-arg form: the accumulator seeds from init and the loop covers
            // ALL elements. The empty fold is defined (it is init), so no
            // emptiness guard is needed for any extent, static or dynamic.
            let initStr = exprToCppCore subst names initE
            sprintf "[&]() { %s %s __r = %s; for (size_t __ri = 0; __ri < %s; __ri++) { __r = %s(__r, %s); } return __r; }()"
                wrapperStr elemStr initStr reduceBound wname (reduceAccAt "__ri")
        | None ->
        let guard =
            if reduceNonEmpty then ""
            else sprintf "if (%s == 0) { blade_rt::panic(\"BL8003\", \"reduce: empty array, no reduction possible\", nullptr, 0); } " reduceBound
        sprintf "[&]() { %s%s %s __r = %s; for (size_t __ri = 1; __ri < %s; __ri++) { __r = %s(__r, %s); } return __r; }()"
            guard wrapperStr elemStr (reduceAccAt "0") reduceBound wname (reduceAccAt "__ri")
    | _ ->
        "/* reduce: non-callable kernel (typechecker or IR bug) */"



and renderLetExpr (subst: SubstMap) (names: Map<IRId, string>) id value body : string =
    // For inline let expressions, we need statement context
    let names' = Map.add id (sprintf "__v%d" id) names
    if isUnitExpr value then
        // Unit-valued binding. lowerTypedBlock sequences STATEMENTS
        // (assignments, for-in loops) through dummy lets, so a unit value
        // here is normally a side-effecting statement, not dead code.
        // Render its statement form as an IIFE prelude. The pre-fix code
        // skipped the value outright, which silently discarded kernel-body
        // loops: a block kernel { let mut s = 0.0; for .. { s = s + .. }; s }
        // inlined at a method_for apply site returned the init value
        // unchanged (all-zeros output).
        let stmtPrelude = renderUnitStmts subst names value
        if stmtPrelude = "" then
            // Genuinely effect-free (unit literal): old skip behavior.
            if isUnitExpr body then
                "((void)0)"
            else
                exprToCppCore subst names' body
        elif isUnitExpr body then
            // Effectful value, unit body: whole let is a void expression.
            let bodyStmts = renderUnitStmts subst names' body
            let stmts = [stmtPrelude; bodyStmts] |> List.filter (fun s -> s <> "") |> String.concat " "
            sprintf "([&]() { %s }())" stmts
        else
            sprintf "([&]() { %s return %s; }())" stmtPrelude (exprToCppCore subst names' body)
    else
        // Phase C lift pass produces IRLet bindings whose value can be
        // an inline form (mask/sort/intersect/union). These can't be
        // rendered as a single C++ expression â€” they need a multi-
        // statement materialization sequence. Detect that case and emit
        // an IIFE with the materialization as its prelude. The variable
        // `__v<id>` and `__v<id>_extents` come into scope for the body.
        //
        // For all other values (scalars, function calls, IRApplyCombinator
        // results, etc.), the existing "auto __v = ..." form is correct.
        let inlineElemTypeStr (form: IRExpr) =
            inferInlineElemTypeStr "IRLet inline form" form
        match materializeInlineForm subst names (sprintf "__v%d" id) (inlineElemTypeStr value) value with
        | Some preludeStmts ->
            let bodyStr =
                if isUnitExpr body then "((void)0)"
                else exprToCppCore subst names' body
            let prelude = preludeStmts |> String.concat " "
            sprintf "([&]() { %s return %s; }())" prelude bodyStr
        | None ->
            let valStr = exprToCppCore subst names value
            match body with
            | IRLit IRLitUnit ->
                sprintf "([&]() { auto __v%d = %s; }())" id valStr
            | b when isUnitExpr b ->
                // Unit-typed but effectful tail (assignment / for-in as the
                // block's last statement): emit its statement form rather
                // than a `return` of a statement expression.
                sprintf "([&]() { auto __v%d = %s; %s }())" id valStr (renderUnitStmts subst names' b)
            | _ ->
                let bodyStr = exprToCppCore subst names' body
                sprintf "([&]() { auto __v%d = %s; return %s; }())" id valStr bodyStr


/// Render a unit-typed side-effecting expression â€” a STATEMENT that
/// lowerTypedBlock sequenced through a dummy let (assignment, for-in loop,
/// nested statement block) â€” as flat C++ statement text for splicing into
/// an expression-context IIFE. Returns "" when the expression has no
/// runtime effect (unit literal).
///
/// This is the expression-context sibling of genFuncBody's IRForRange /
/// IRAssign statement arms; it lives in the exprToCppCore let-rec group
/// because statements re-enter expression rendering for their operands
/// (loop bounds, assignment RHS, indices).
and renderUnitStmts (subst: SubstMap) (names: Map<IRId, string>) (expr: IRExpr) : string =
    match expr with
    | IRLit IRLitUnit -> ""
    | IRAssign _ ->
        sprintf "%s;" (exprToCppCore subst names expr)
    | IRConstraintCheck (cond, message, span) ->
        sprintf "if (!(%s)) { blade_rt::panic(\"BL8001\", \"%s\", %s); }"
            (exprToCppCore subst names cond) message (panicSpanArgs span)
    | IRForRange (vid, lo, hi, body) ->
        // Same loop-var naming (__k<id>) and int64_t convention as
        // genForRangeBinding / EmitCpp.forLoopFrom, so inlined kernel
        // loops read like their module-level counterparts. int64_t, not
        // size_t: the loop var is the user's Int64 for-in variable, and an
        // unsigned binding wraps negative intermediates in body arithmetic.
        let varName = sprintf "__k%d" vid
        let names' = Map.add vid varName names
        let loStr = exprToCppCore subst names lo
        let hiStr = exprToCppCore subst names hi
        let bodyStmts = renderUnitStmts subst names' body
        sprintf "for (int64_t %s = %s; %s < %s; %s++) { %s }" varName loStr varName hiStr varName bodyStmts
    | IRLet (letId, value, body) ->
        // Statement-position let chain (a nested block): declare non-unit
        // values, splice unit statements, continue down the chain. Inline
        // forms (mask/sort/...) get their multi-statement materialization,
        // mirroring renderLetExpr's expression-position handling.
        let names' = Map.add letId (sprintf "__v%d" letId) names
        let valueStmt =
            if isUnitExpr value then
                renderUnitStmts subst names value
            else
                match materializeInlineForm subst names (sprintf "__v%d" letId) (inferInlineElemTypeStr "statement-position let" value) value with
                | Some prelude -> prelude |> String.concat " "
                | None -> sprintf "auto __v%d = %s;" letId (exprToCppCore subst names value)
        [valueStmt; renderUnitStmts subst names' body]
        |> List.filter (fun s -> s <> "")
        |> String.concat " "
    | IRSequence elems ->
        elems
        |> List.map (renderUnitStmts subst names)
        |> List.filter (fun s -> s <> "")
        |> String.concat " "
    | other ->
        // Not statically unit: evaluate for side effects, discard the value.
        // Also the safety net for unhandled statement forms â€” a visible
        // C++ expression beats a silent drop.
        sprintf "(void)(%s);" (exprToCppCore subst names other)


and renderExtentExpr (subst: SubstMap) (names: Map<IRId, string>) arr dim : string =
    // Statically resolved when the index type's extent expression is a
    // literal-arithmetic value (Idx<5>, Idx<n+1> with n compile-time, etc.)
    // â€” emit as a compile-time literal eligible for use in static contexts.
    // Falls back to a runtime read from <name>_extents[dim] for genuinely
    // dynamic extents (mask, group_by groups, sort outputs derived from
    // those, etc.).
    match inferExprType arr with
    | ArrayElem at when dim < at.IndexTypes.Length ->
        match tryEvalIntIR at.IndexTypes.[dim].Extent with
        | Some n -> sprintf "%dL" n
        | None ->
            let arrName = exprToCppCore subst names arr
            // A rank-1 ragged/dep-idx operand is a RaggedRow<T> (per
            // cppArrayTypeStr), which carries its length inline as `.len`,
            // not via a pointer-to-extents like Array<T,1>. Its only axis is
            // dim 0. Every other operand (Array, higher-rank ragged) uses the
            // materialized `.extents[dim]`.
            let isRaggedRow = isRaggedRowType at
            // A rank-1 all-dims compound (the filtered-set case,
            // compound(A, mask(A, p))) has no .extents member; its sole
            // axis's runtime extent is the compact index's cardinality.
            // (Multi-rank compound extents are rejected at typecheck,
            // same ill-posedness rule as ragged slots.)
            let isRank1Compound = isCompoundArrayType at && at.IndexTypes.Length = 1
            if isRaggedRow && dim = 0 then
                sprintf "(int64_t)(%s.len)" arrName
            elif isRank1Compound && dim = 0 then
                sprintf "(int64_t)(%s.idx->cardinality)" arrName
            else
                sprintf "(int64_t)(%s.extents[%d])" arrName dim
    | _ ->
        // Should be unreachable â€” typecheck rejects non-arrays. Surface a
        // visible #error rather than emit garbage if the IR is malformed.
        "/* extents: argument is not an array (typechecker bug) */"


and genApplyCombinatorExpr (subst: SubstMap) (names: Map<IRId, string>) (info: ApplyInfo) : string =
    // Extract array info
    let arrayNames = 
        info.Arrays |> List.mapi (fun i arr ->
            match arr with
            | IRVar (id, _) -> Map.tryFind id names |> Option.defaultValue (sprintf "arr%d" i)
            | IRParam (name, _, _) -> name
            | _ -> sprintf "arr%d" i)
    
    // Stage 3c.2: kernel resolution unified via `resolveCallable`. The
    // 2-array Cartesian sum-reduce emits the kernel as a local wrapper
    // closure inside the IIFE, dropping the previous duck-typed inline
    // body (which re-bound the kernel body inside an `elemStr`-typed
    // closure to compensate for sections' formerly-hardcoded Float64
    // types). With sections now honestly typed (`lowerTypedSection`
    // + `inferReduce` kernel-param unify), the wrapper carries the
    // right signature directly.
    //
    // IRReynolds-wrapped kernels are peeled â€” at this inline-form
    // emission site the Reynolds symmetrization is informational and
    // doesn't change what the kernel computes per call; the symmetric
    // accumulation is the iteration structure, not the kernel itself.
    // (See audit item 4 for the open question on whether this peel is
    // actually correctness-preserving.)
    let (kernelInner, _) = peelReynolds info.Kernel

    // Infer output element type from ApplyInfo
    let elemTypeStr =
        match info.OutputType with
        | IRTScalar et -> primTypeToCpp et
        | ArrayElem arr -> elemTypeToCpp arr.ElemType
        | t -> irTypeToCpp t

    // For scalar output (simple accumulation), generate inline loop
    // This is a simplified version - full version would use LoopNestCodeGen
    match resolveCallable kernelInner with
    | Some callable when callable.Params.Length = 2 && arrayNames.Length = 2 ->
        let arr1 = arrayNames.[0]
        let arr2 = arrayNames.[1]
        let (wrapperCode, wname) = genCallableWrapper "" callable
        let wrapperStr = wrapperCode |> String.concat " "
        // Generate as IIFE with nested loops.
        sprintf "([&]() { %s%s __result = 0; for (size_t __i0 = 0; __i0 < %s.extents[0]; __i0++) { for (size_t __i1 = 0; __i1 < %s.extents[0]; __i1++) { __result += %s(%s[__i0], %s[__i1]); } } return __result; }())"
            wrapperStr elemTypeStr arr1 arr2 wname arr1 arr2
    | _ ->
        // Fallback - can't generate inline code
        exprError (sprintf "inline combinator not supported for %d arrays" arrayNames.Length)

/// Convert IRExpr to C++ with an additional variable binding
and exprToCppWithVarCore (subst: SubstMap) (names: Map<IRId, string>) (varId: IRId) (varName: string) (expr: IRExpr) : string =
    let names' = Map.add varId varName names
    exprToCppCore subst names' expr

/// Convenience wrapper: render with no contains-substitution. This is the
/// API every existing caller uses; the substitution-aware path goes through
/// exprToCppWithSubst (defined outside the let-rec group).
///
/// Wrappers are inside the recursion group because sibling helpers
/// (`genApplyCombinatorExpr`, `materializeInlineForm`) reference these
/// names; defining them as plain `let` after the group would push them
/// out of scope at those call sites.
and exprToCpp (names: Map<IRId, string>) (expr: IRExpr) : string =
    exprToCppCore emptySubst names expr

and exprToCppWithVar (names: Map<IRId, string>) (varId: IRId) (varName: string) (expr: IRExpr) : string =
    exprToCppWithVarCore emptySubst names varId varName expr

/// Generate the C++ statements that materialize an inline form (IRMask,
/// IRIntersect, IRUnion, IRSort) into `varName` and `varName + "_extents"`.
///
/// Returns statements WITHOUT leading indentation; callers add their own
/// prefix (per-line `ind` for genBinding/genFuncBody, space-joining for
/// inline IIFE inside exprToCpp).
///
/// `elemTypeStr` is the C++ element type of the form's result array, as
/// already resolved by the caller. Different callers have different needs
/// for type-resolution strictness:
///   - genBinding uses inferElemTypeStrict (emits #error on unresolvable)
///   - genFuncBody / exprToCpp IIFE use silent fallback (lift pass should
///     guarantee resolvable types upstream, but no #error scaffolding here)
/// Pulling elem-type resolution out of the helper keeps it format-neutral
/// and lets each caller surface errors appropriately for its context.
///
/// Mutually recursive with exprToCpp so it can render nested predicate
/// and key bodies. Returns None for forms outside this set.
and materializeInlineForm (subst: SubstMap) (names: Map<IRId, string>) (varName: string) (elemTypeStr: string) (form: IRExpr) : string list option =
    match form with
    | IRMask (arrExpr, predExpr) ->
        materializeMaskForm subst names varName elemTypeStr arrExpr predExpr
    | IRIntersect (aExpr, bExpr) ->
        materializeIntersectForm subst names varName elemTypeStr aExpr bExpr
    | IRUnion (aExpr, bExpr) ->
        materializeUnionForm subst names varName elemTypeStr aExpr bExpr
    | IRUnique aExpr ->
        materializeUniqueForm subst names varName elemTypeStr aExpr
    | IRSort (arrExpr, keyExpr) ->
        materializeSortForm subst names varName elemTypeStr arrExpr keyExpr
    | IRTranspose (arrExpr, d1, d2) ->
        materializeTransposeForm subst names varName elemTypeStr arrExpr d1 d2
    | IRDecompact (arrExpr, dimArg) ->
        materializeDecompactForm subst names varName elemTypeStr arrExpr dimArg
    | IRArrayNegate arrExpr | IRArrayConjugate arrExpr ->
        materializeNegateConjugateForm subst names varName elemTypeStr form arrExpr
    | IRGram (lExpr, rExpr, sameArray) ->
        materializeGramForm subst names varName elemTypeStr lExpr rExpr sameArray
    | _ -> None


and materializeMaskForm (subst: SubstMap) (names: Map<IRId, string>) (varName: string) (elemTypeStr: string) (arrExpr: IRExpr) (predExpr: IRExpr) : string list option =
    // mask(A, pred) -> the Bool PRESENCE array over A's own index space,
    // m[i] = pred(A[i]). One pass, no value copying: compaction belongs
    // to compound(A, m); iteration to range<CompoundIdx<m>>. A contains(B, x)
    // inside the predicate renders as a linear scan (see the note on the
    // predicate arm below); the retired probe/set-hoist machinery has been
    // removed.
    let arrName = exprToCppCore subst names arrExpr
    let maskRank =
        match inferExprType arrExpr with
        | ArrayElem a -> a.IndexTypes.Length
        | _ -> 1
    // A RaggedRow-typed source (mask over a peeled row param) carries its
    // length inline as .len; everything else reads .extents[0].
    let srcBound =
        match inferExprType arrExpr with
        | ArrayElem a when isRaggedRowType a -> sprintf "%s.len" arrName
        | _ -> sprintf "%s.extents[0]" arrName
    if maskRank <> 1 then
        Some [sprintf "#error \"Blade codegen: mask over a rank-%d array is not yet supported (rank-1 only for now; rank-k masks land with the compound composition round)\"" maskRank]
    else
    match resolveCallable predExpr with
    | Some callable when callable.Params.Length = 1 ->
        // Emit the per-element predicate call. (An earlier "local set-hoist"
        // that pre-built an unordered_set for hoistable contains nodes was a
        // no-op: genCallableWrapper calls the predicate by NAME and ignores
        // the swapped body, so the set was built but never queried. It has
        // been removed; the contains runs as a linear scan inside the named
        // predicate. Making the semijoin set actually fire is a separate
        // optimization, tracked apart from the probe-machinery excision.)
        let (wrapperCode, wname) = genCallableWrapper varName callable
        let predParamName = sprintf "__%s_x" varName
        // Source element type (elemTypeStr is the RESULT type, i.e. bool).
        let srcElemStr =
            match inferExprType arrExpr with
            | ArrayElem a -> elemTypeToCpp a.ElemType
            | _ -> "double"
        Some (
            wrapperCode @ [
                sprintf "size_t %s_extents[1] = {%s};" varName srcBound
                sprintf "Array<bool, 1> %s = { new bool[%s], %s_extents };" varName srcBound varName
                sprintf "for (size_t __mi = 0; __mi < %s; __mi++) {" srcBound
                sprintf "    %s %s = %s[__mi];" srcElemStr predParamName arrName
                sprintf "    %s[__mi] = %s(%s);" varName wname predParamName
                "}"
            ]
        )
    | _ ->
        // Degenerate (unresolved predicate): all-true mask; #error would be
        // kinder but this mirrors the prior fallback's shape.
        Some [
            sprintf "size_t %s_extents[1] = {%s};" varName srcBound
            sprintf "Array<bool, 1> %s = { new bool[%s], %s_extents };" varName srcBound varName
            sprintf "for (size_t __mi = 0; __mi < %s; __mi++) %s[__mi] = true;" srcBound varName
        ]



and materializeIntersectForm (subst: SubstMap) (names: Map<IRId, string>) (varName: string) (elemTypeStr: string) (aExpr: IRExpr) (bExpr: IRExpr) : string list option =
    // SQL INTERSECT: unique values appearing in BOTH arrays, output in
    // first-occurrence order from A. Two-pass with set reuse, mirroring
    // unique() â€” first pass counts unique A-elements that are also in
    // B, second pass emits them in order.
    //
    // The `__seen.insert(x).second` idiom is a one-shot "is-first?"
    // check: returns true iff x wasn't previously in the set. Used in
    // both passes so each unique A-element is counted exactly once
    // (regardless of how often it repeats in A).
    let aName = exprToCppCore subst names aExpr
    let bName = exprToCppCore subst names bExpr
    Some [
        sprintf "std::unordered_set<%s> %s__b_set;" elemTypeStr varName
        sprintf "for (size_t __si = 0; __si < %s.extents[0]; __si++) %s__b_set.insert(%s[__si]);" bName varName bName
        sprintf "std::unordered_set<%s> %s__seen;" elemTypeStr varName
        sprintf "size_t %s__count = 0;" varName
        sprintf "for (size_t __si = 0; __si < %s.extents[0]; __si++) {" aName
        sprintf "    %s __x = %s[__si];" elemTypeStr aName
        sprintf "    if (%s__b_set.count(__x) && %s__seen.insert(__x).second) %s__count++;" varName varName varName
        "}"
        sprintf "size_t %s_extents[1] = {%s__count};" varName varName
        sprintf "Array<%s, 1> %s = { new %s[%s__count], %s_extents };" elemTypeStr varName elemTypeStr varName varName
        sprintf "%s__seen.clear();" varName
        sprintf "size_t %s__fill = 0;" varName
        sprintf "for (size_t __si = 0; __si < %s.extents[0]; __si++) {" aName
        sprintf "    %s __x = %s[__si];" elemTypeStr aName
        sprintf "    if (%s__b_set.count(__x) && %s__seen.insert(__x).second) %s[%s__fill++] = __x;" varName varName varName varName
        "}"
    ]


and materializeUnionForm (subst: SubstMap) (names: Map<IRId, string>) (varName: string) (elemTypeStr: string) (aExpr: IRExpr) (bExpr: IRExpr) : string list option =
    // SQL UNION: unique values appearing in EITHER array, output in
    // first-occurrence order across the concatenation A ++ B. Two-pass
    // with set reuse. Each pass walks A then B; the shared seen set
    // ensures A's elements appear before B's, and within each, only
    // first occurrences survive.
    let aName = exprToCppCore subst names aExpr
    let bName = exprToCppCore subst names bExpr
    Some [
        sprintf "std::unordered_set<%s> %s__seen;" elemTypeStr varName
        sprintf "size_t %s__count = 0;" varName
        sprintf "for (size_t __si = 0; __si < %s.extents[0]; __si++) {" aName
        sprintf "    if (%s__seen.insert(%s[__si]).second) %s__count++;" varName aName varName
        "}"
        sprintf "for (size_t __si = 0; __si < %s.extents[0]; __si++) {" bName
        sprintf "    if (%s__seen.insert(%s[__si]).second) %s__count++;" varName bName varName
        "}"
        sprintf "size_t %s_extents[1] = {%s__count};" varName varName
        sprintf "Array<%s, 1> %s = { new %s[%s__count], %s_extents };" elemTypeStr varName elemTypeStr varName varName
        sprintf "%s__seen.clear();" varName
        sprintf "size_t %s__fill = 0;" varName
        sprintf "for (size_t __si = 0; __si < %s.extents[0]; __si++) {" aName
        sprintf "    if (%s__seen.insert(%s[__si]).second) %s[%s__fill++] = %s[__si];" varName aName varName varName aName
        "}"
        sprintf "for (size_t __si = 0; __si < %s.extents[0]; __si++) {" bName
        sprintf "    if (%s__seen.insert(%s[__si]).second) %s[%s__fill++] = %s[__si];" varName bName varName varName bName
        "}"
    ]


and materializeUniqueForm (subst: SubstMap) (names: Map<IRId, string>) (varName: string) (elemTypeStr: string) (aExpr: IRExpr) : string list option =
    // First pass: insert each element into an unordered_set; count
    // first-occurrences. Second pass: clear the set, rescan, emit on
    // first occurrence. Two passes keep allocation exact (no
    // intermediate vector) while preserving first-occurrence order.
    let aName = exprToCppCore subst names aExpr
    Some [
        sprintf "std::unordered_set<%s> %s__seen;" elemTypeStr varName
        sprintf "size_t %s__count = 0;" varName
        sprintf "for (size_t __ui = 0; __ui < %s.extents[0]; __ui++) {" aName
        sprintf "    if (%s__seen.insert(%s[__ui]).second) %s__count++;" varName aName varName
        "}"
        sprintf "size_t %s_extents[1] = {%s__count};" varName varName
        sprintf "Array<%s, 1> %s = { new %s[%s__count], %s_extents };" elemTypeStr varName elemTypeStr varName varName
        sprintf "%s__seen.clear();" varName
        sprintf "size_t %s__fill = 0;" varName
        sprintf "for (size_t __ui = 0; __ui < %s.extents[0]; __ui++) {" aName
        sprintf "    if (%s__seen.insert(%s[__ui]).second) %s[%s__fill++] = %s[__ui];" varName aName varName varName aName
        "}"
    ]


and materializeSortForm (subst: SubstMap) (names: Map<IRId, string>) (varName: string) (elemTypeStr: string) (arrExpr: IRExpr) (keyExpr: IRExpr) : string list option =
    // Key-callable resolution via `resolveCallable`. The key
    // function emits as a local wrapper closure
    // (`__wrap_<id>_<varName>`) that forwards to the lifted
    // function with captures pulled by reference. The wrapper
    // takes the element value as its single arg and returns
    // the orderable key; the stable_sort's comparator invokes
    // the wrapper on each element under comparison.
    //
    // Fallback for unresolved keyExpr (shouldn't happen for
    // well-typed sort calls): emit a sort that's a no-op on
    // key (returns literal 0 â€” all elements compare equal,
    // preserving input order under stable_sort).
    let arrName = exprToCppCore subst names arrExpr
    // A rank-1 compound operand (compound(A, mask(A, p)) -- the filtered
    // set) sorts its compact buffer: bound = cardinality, elements via
    // .data[i]. Sorting discards coordinate meaning by construction, so
    // the DENSE output shape is the semantically honest one. Dense
    // operands keep .extents/operator[].
    let isR1Compound =
        match inferExprType arrExpr with
        | ArrayElem at -> isCompoundArrayType at && at.IndexTypes.Length = 1
        | _ -> false
    let srcBound = if isR1Compound then sprintf "%s.idx->cardinality" arrName else sprintf "%s.extents[0]" arrName
    let srcAt (i: string) = if isR1Compound then sprintf "%s.data[%s]" arrName i else sprintf "%s[%s]" arrName i
    let (wrapperCode, keyCall) =
        match resolveCallable keyExpr with
        | Some callable when callable.Params.Length = 1 ->
            let (code, wname) = genCallableWrapper varName callable
            (code, wname)
        | _ -> ([], "[](auto) { return 0; }")  // degenerate fallback
    Some (
        wrapperCode @ [
            sprintf "size_t* %s__perm = new size_t[%s];" varName srcBound
            sprintf "for (size_t __pi = 0; __pi < %s; __pi++) %s__perm[__pi] = __pi;" srcBound varName
            sprintf "std::stable_sort(%s__perm, %s__perm + %s, [&](size_t __a, size_t __b) {" varName varName srcBound
            sprintf "    return %s(%s) < %s(%s);" keyCall (srcAt "__a") keyCall (srcAt "__b")
            "});"
            sprintf "size_t %s_extents[1] = {%s};" varName srcBound
            sprintf "Array<%s, 1> %s = { new %s[%s], %s_extents };" elemTypeStr varName elemTypeStr srcBound varName
            sprintf "for (size_t __si = 0; __si < %s; __si++) %s[__si] = %s;" srcBound varName (srcAt (sprintf "%s__perm[__si]" varName))
        ]
    )


and materializeTransposeForm (subst: SubstMap) (names: Map<IRId, string>) (varName: string) (elemTypeStr: string) (arrExpr: IRExpr) (d1: int) (d2: int) : string list option =
    // Hard transpose: allocate a fresh pool at the SWAPPED extents and copy
    // every element with axes d1/d2 exchanged. The result is an independent
    // array (new pool, new row-pointers) â€” no aliasing back to the source,
    // which is why this is always correct (never a soft/view transpose).
    // General rank: an N-deep nested loop over the SOURCE extents; the
    // destination subscript list is the source loop vars with positions
    // d1 and d2 swapped. TypeCheck guarantees both axes are arity-1 SymNone,
    // so the source is rectangular and every dim is a single plain Idx.
    let arrName = exprToCppCore subst names arrExpr
    (match inferExprType arrExpr with
     | ArrayElem arrTy ->
        let rank = arrTy.IndexTypes.Length
        let extentsName = sprintf "%s_extents" varName
        // Source loop variables, one per dimension.
        let srcVar d = sprintf "__t%s_%d" varName d
        // Destination extents = source extents with d1/d2 swapped.
        let swapDim d = if d = d1 then d2 elif d = d2 then d1 else d
        let extentDecl =
            [ sprintf "size_t %s[%d];" extentsName rank ]
            @ [ for d in 0 .. rank - 1 ->
                    sprintf "%s[%d] = %s.extents[%d];" extentsName d arrName (swapDim d) ]
        let allocDecl =
            arrayAlloc { Ind = ""; Elem = elemTypeStr; Rank = rank; Name = varName
                         Symm = "nullptr"; Strict = None; Extents = extentsName }
        // Nested copy loops over the SOURCE extents.
        let openLoops =
            [ for d in 0 .. rank - 1 ->
                let ind = String.replicate d "    "
                sprintf "%sfor (size_t %s = 0; %s < %s.extents[%d]; %s++) {"
                    ind (srcVar d) (srcVar d) arrName d (srcVar d) ]
        // dst subscript at position p reads the source var whose dimension
        // maps to p under the swap, i.e. dst[swap(p)] index = srcVar(p).
        // Equivalently: walk source vars in order, but write them into dst
        // at swapped positions. Build dst index from src vars: the dst's
        // dimension d is fed by source dimension swapDim(d).
        let srcIdx = [ for d in 0 .. rank - 1 -> sprintf "[%s]" (srcVar d) ] |> String.concat ""
        let dstIdx = [ for d in 0 .. rank - 1 -> sprintf "[%s]" (srcVar (swapDim d)) ] |> String.concat ""
        let bodyInd = String.replicate rank "    "
        let body = [ sprintf "%s%s%s = %s%s;" bodyInd varName dstIdx arrName srcIdx ]
        let closeLoops = [ for d in rank - 1 .. -1 .. 0 -> sprintf "%s}" (String.replicate d "    ") ]
        Some (extentDecl @ [allocDecl] @ openLoops @ body @ closeLoops)
     | _ -> None)


and materializeDecompactForm (subst: SubstMap) (names: Map<IRId, string>) (varName: string) (elemTypeStr: string) (arrExpr: IRExpr) (dimArg: int) : string list option =
    // Decompaction = binary group FISSION. decompact(A, d) isolates the
    // logical dimension d of a compact group as a free Idx, cutting on BOTH
    // sides: SymIdx<r,n> -> SymIdx<dPos,n> -> Idx<n> -> SymIdx<r-dPos-1,n>.
    // Edges degenerate to a single cut. Storage is value-equivalent to the
    // source but strictly larger (fission breaks the inter-axis dependency,
    // so each sub-group ranges over the full [0,n) again) â€” the cost paid to
    // make the freed axis densely indexable / transposable.
    //
    // TWO emitted shapes:
    //   (1) General SYMMETRIC fission, any rank, any d (sole compact slot):
    //       GATHER into a fission-shaped output allocated with a per-group
    //       SYMM mask {left-run | freed-singleton | right-run}. Each output
    //       cell is written exactly once: enumerate left-group canonical
    //       coords (left-justified), freed dense axis, right-group canonical
    //       coords; assemble the logical r-tuple; sort; read the source at
    //       its left-justified canonical address. (Validated rank 2-5, all
    //       cut positions, against the runtime allocator.)
    //   (2) ANTISYMMETRIC rank-2 (fully dissolves to dense nÃ—n): the legacy
    //       two-image scatter with sign on the mirror and zeroed diagonal.
    //   (3) ANTISYMMETRIC rank>=3 (general, any cut): per-group-strict
    //       (allocate_strict) fission into the chain Antisym<aLen> -> Idx ->
    //       Antisym<bLen>, with the full-tuple sign baked at scatter and each
    //       residual group's own antisymmetry applied lazily on read. Handles
    //       boundary cuts (one residual group), one-sided interior cuts
    //       (rank 4: one group + a degenerate plain residual), two-sided
    //       interior cuts (rank 5: two groups), and the rank-3 interior case
    //       (both residuals degenerate -> fully dense).
    let arrName = exprToCppCore subst names arrExpr
    (match inferExprType arrExpr with
     | ArrayElem arrTy ->
        // The compact group being decompacted is the LAST index slot
        // (TypeCheck enforces: any preceding slots are plain free Idx
        // singletons). Read the group's arity r and symmetry from that last
        // slot; the leading free slots become an outer loop product that
        // wraps the fission scatter. `leadingN` = number of leading free
        // dims; their extents are emitted before the group's freed/expanded
        // axes, and their indices map identically source->dest.
        let leadingN = max 0 (arrTy.IndexTypes.Length - 1)
        let (r, sym) =
            match List.tryLast arrTy.IndexTypes with
            | Some ix -> (max 1 ix.Rank, ix.Symmetry)
            | None -> (0, SymNone)
        // Leading free loop variables and the per-dimension subscript they
        // contribute (prefixed to both the output and source addresses).
        let leadVar j = sprintf "__dc%s_S%d" varName j
        let leadSubs = [ for j in 0 .. leadingN - 1 -> sprintf "[%s]" (leadVar j) ] |> String.concat ""
        let extentsName = sprintf "%s_extents" varName
        let nExpr = sprintf "%s.extents[0]" arrName
        (match sym with
         | SymSymmetric when r >= 2 ->
            // ----- General symmetric fission (gather) -----
            // The targeted group is the LAST slot, preceded by `leadingN`
            // free singleton dims (global indices 0..leadingN-1). The cut's
            // position WITHIN the group is therefore the global dim minus
            // the leading count â€” NOT the global dim itself. (For the sole-
            // slot case leadingN=0 so they coincide, which is why this only
            // surfaced once chained decompaction produced leading dims:
            // using the global dim made aLen too large, emitting more tuple
            // entries than the group's arity.)
            let dPos = dimArg - leadingN   // logical position within the group
            let aLen = dPos            // left group arity
            let bLen = r - dPos - 1    // right group arity
            // Build the per-group SYMM mask: a run of arity>=2 is one group
            // (compact); arity-1 (and the freed axis) are distinct singletons
            // (dense). This mirrors buildSymmVec's adjacent-equal grouping.
            let mask =
                let acc = System.Collections.Generic.List<int>()
                let mutable g = 1
                let emitGroup len =
                    if len = 1 then
                        acc.Add g
                        g <- g + 1
                    elif len > 1 then
                        for _ in 1 .. len do acc.Add g
                        g <- g + 1
                    // len <= 0: emit nothing, do NOT advance the group counter
                // Leading free dims are distinct dense singletons, emitted
                // before the fission group's mask entries.
                for _ in 1 .. leadingN do emitGroup 1
                emitGroup aLen
                emitGroup 1            // the freed axis (always a singleton)
                emitGroup bLen
                List.ofSeq acc
            let symmArg = hoistSymmDecl (sprintf "%s_symm" varName) mask
            // Total output rank = leading free dims + the fission group's
            // r expanded axes. All axes share extent n (== arrName.extents[0]).
            let totalRank = leadingN + r
            let extentDecl =
                [ sprintf "size_t %s[%d];" extentsName totalRank ]
                @ [ for i in 0 .. totalRank - 1 -> sprintf "%s[%d] = %s;" extentsName i nExpr ]
            let allocDecl =
                arrayAlloc { Ind = ""; Elem = elemTypeStr; Rank = totalRank; Name = varName
                             Symm = symmArg; Strict = None; Extents = extentsName }
            // Emit a left-justified canonical nest for a group. Returns the
            // generated loop-open lines, the storage subscript ("[v0][v1]..")
            // and the names of the per-level LOGICAL vars (prefix sums).
            let lvName tag k = sprintf "__dc%s_%s%d" varName tag k
            let emitGroupNest (tag: string) (len: int) (startIndent: int)
                : string list * string * string list =
                let mutable lines = []
                let mutable subs = ""
                let mutable logs = []
                for k in 0 .. len - 1 do
                    let ind = String.replicate (startIndent + k) "    "
                    let v = lvName tag k
                    let logName = v + "_log"
                    let bound =
                        if k = 0 then nExpr
                        else sprintf "%s - %s" nExpr ((lvName tag (k-1)) + "_log")
                    let logRhs =
                        if k = 0 then v
                        else sprintf "%s + %s" ((lvName tag (k-1)) + "_log") v
                    lines <- lines @
                        [ forLoop ind v bound
                          sprintf "%s    size_t %s = %s;" ind logName logRhs ]
                    subs <- subs + sprintf "[%s]" v
                    logs <- logs @ [logName]
                (lines, subs, logs)
            let fv = sprintf "__dc%s_F" varName
            // Leading free dims become the outermost loops; the fission nest
            // is emitted indented beneath them. Their indices are prefixed
            // (leadSubs) to both the output and source addresses.
            let leadLines =
                [ for j in 0 .. leadingN - 1 ->
                    let ind = String.replicate j "    "
                    sprintf "%sfor (size_t %s = 0; %s < %s; %s++) {" ind (leadVar j) (leadVar j) nExpr (leadVar j) ]
            let mutable depth = leadingN
            let (lLines, lSubs, lLogs) = emitGroupNest "L" aLen depth
            depth <- depth + aLen
            let fInd = String.replicate depth "    "
            let fLine = forLoop fInd fv nExpr
            depth <- depth + 1
            let (rLines, rSubs, rLogs) = emitGroupNest "R" bLen depth
            depth <- depth + bLen
            let logicalTuple = lLogs @ [fv] @ rLogs
            let bodyInd = String.replicate depth "    "
            let arrInit = logicalTuple |> String.concat ", "
            let srcSub =
                [ for k in 0 .. r - 1 ->
                    if k = 0 then sprintf "[__dc%s_t[0]]" varName
                    else sprintf "[__dc%s_t[%d] - __dc%s_t[%d]]" varName k varName (k-1) ]
                |> String.concat ""
            // Free leading dims map identically source->dest, so prefix them
            // to both subscripts.
            let outSub = leadSubs + lSubs + sprintf "[%s]" fv + rSubs
            let srcSubFull = leadSubs + srcSub
            let body =
                [ sprintf "%ssize_t __dc%s_t[%d] = { %s };" bodyInd varName r arrInit
                  sprintf "%sstd::sort(__dc%s_t, __dc%s_t + %d);" bodyInd varName varName r
                  sprintf "%s%s%s = %s%s;" bodyInd varName outSub arrName srcSubFull ]
            let closes = [ for dd in depth - 1 .. -1 .. 0 -> sprintf "%s}" (String.replicate dd "    ") ]
            Some (extentDecl @ [allocDecl] @ leadLines @ lLines @ [fLine] @ rLines @ body @ closes)
         | SymAntisymmetric when r = 2 ->
            // ----- Antisym rank-2: fully dissolves to dense nÃ—n -----
            // Zero-fill (diagonal stays 0). Walk a in [0,n), b in [0,n-a-1);
            // strict: i=a, j=a+b+1. Write +A to (i,j), -A to (j,i).
            let extentDecl =
                [ sprintf "size_t %s[2] = { %s, %s };" extentsName nExpr nExpr ]
            let allocDecl =
                sprintf "Array<%s, 2> %s = { allocate<typename promote<%s, 2>::type, nullptr>(%s), %s };"
                    elemTypeStr varName elemTypeStr extentsName extentsName
            let a = sprintf "__dc%s_a" varName
            let b = sprintf "__dc%s_b" varName
            let zeroFill =
                [ sprintf "for (size_t __dcz0 = 0; __dcz0 < %s; __dcz0++)" nExpr
                  sprintf "    for (size_t __dcz1 = 0; __dcz1 < %s; __dcz1++)" nExpr
                  sprintf "        %s[__dcz0][__dcz1] = 0;" varName ]
            let loops =
                [ sprintf "for (size_t %s = 0; %s < %s; %s++) {" a a nExpr a
                  sprintf "    for (size_t %s = 0; %s + 1 < %s - %s; %s++) {" b b nExpr a b
                  sprintf "        size_t __dci = %s; size_t __dcj = %s + %s + 1;" a a b
                  sprintf "        %s[__dci][__dcj] = %s[%s][%s];" varName arrName a b
                  sprintf "        %s[__dcj][__dci] = -(%s[%s][%s]);" varName arrName a b
                  "    }"
                  "}" ]
            Some (extentDecl @ [allocDecl] @ zeroFill @ loops)
         | SymHermitian when r = 2 ->
            // ----- Hermitian rank-2: dissolves to dense nÃ—n -----
            // Source is upper-triangle Hermitian storage (from gram). Walk the
            // INCLUSIVE upper triangle i<=j (diagonal kept â€” it is real for a
            // Hermitian matrix, unlike the zeroed antisym diagonal): write the
            // stored value to [i][j] and its CONJUGATE to the mirror [j][i].
            // conj_scalar is std::conj on complex / identity on real, so this
            // also handles a (degenerate) real Hermitian = symmetric input.
            let extentDecl =
                [ sprintf "size_t %s[2] = { %s, %s };" extentsName nExpr nExpr ]
            let allocDecl =
                sprintf "Array<%s, 2> %s = { allocate<typename promote<%s, 2>::type, nullptr>(%s), %s };"
                    elemTypeStr varName elemTypeStr extentsName extentsName
            let a = sprintf "__dc%s_a" varName
            let b = sprintf "__dc%s_b" varName
            let loops =
                [ sprintf "for (size_t %s = 0; %s < %s; %s++) {" a a nExpr a
                  sprintf "    for (size_t %s = 0; %s + %s < %s; %s++) {" b a b nExpr b
                  sprintf "        size_t __dci = %s; size_t __dcj = %s + %s;" a a b
                  sprintf "        %s[__dci][__dcj] = %s[%s][%s];" varName arrName a b
                  sprintf "        if (__dci != __dcj) %s[__dcj][__dci] = nested_array_utilities::conj_scalar(%s[%s][%s]);" varName arrName a b
                  "    }"
                  "}" ]
            Some (extentDecl @ [allocDecl] @ loops)
         | SymAntisymmetric when r >= 3 ->
            // ----- Antisym rank>=3: COMPACT-RESIDUAL fission (general) -----
            // decompact(anti<r>, dPos) severs the group into a chain:
            //   left residual (arity dPos) -> freed Idx -> right residual
            //   (arity r-1-dPos), the two residuals being INDEPENDENT antisym
            //   groups (NOT one merged group). Each residual of arity>=2 is a
            //   compact strict group; arity 1 degenerates to a plain Idx;
            //   arity 0 is absent. Storage is per-group strict (allocate_strict)
            //   with the mask derived from the result TYPE via
            //   buildSymmVecWithStrict. The scatter stores CANONICAL values
            //   with the FULL-tuple (cross-group + freed) sign BAKED (canon_fold
            //   over the whole logical tuple); each residual group's OWN
            //   antisymmetry is applied lazily on read. Proven end-to-end
            //   (twogroup_clean / general_scatter_emit) for boundary (one
            //   residual), one-sided interior (rank 4), and two-sided interior
            //   (rank 5) cuts. The rank-3 interior case (both residuals arity 1)
            //   is fully dense and handled by the same emission (no strict
            //   groups, two plain freed-style loops + the freed axis).
            let dPos = dimArg
            let aLen = dPos
            let bLen = r - dPos - 1
            // Per-slot descriptors in logical order: (kind, arity, startVar fn).
            // kind: "group" (strict compact, arity>=2) | "plain" (single dense
            // axis: a degenerate residual OR the freed axis).
            // Build the ordered slot list.
            let slotList =
                [ if aLen >= 2 then yield ("group", aLen)
                  elif aLen = 1 then yield ("plain", 1)
                  yield ("freed", 1)
                  if bLen >= 2 then yield ("group", bLen)
                  elif bLen = 1 then yield ("plain", 1) ]
            // Result type drives the storage mask. resultType isn't bound in
            // this arm (only the source arrTy is), so build the mask directly
            // from slotList using the same grouping rule as
            // buildSymmVecWithStrict: each arity>=2 group is one strict
            // compact group; each plain/freed axis is its own dense singleton.
            let (symmMaskVec, strictMaskVec) =
                let mutable symm = []
                let mutable strict = []
                let mutable g = 1
                for (kind, slotRank) in slotList do
                    match kind with
                    | "group" ->
                        for _ in 0 .. slotRank - 1 do
                            symm <- symm @ [g]
                            strict <- strict @ [1]
                        g <- g + 1
                    | _ ->
                        symm <- symm @ [g]
                        strict <- strict @ [0]
                        g <- g + 1
                (symm, strict)
            let symmArg = hoistSymmDecl (sprintf "%s_symm" varName) symmMaskVec
            let strictArg = hoistSymmDecl (sprintf "%s_strict" varName) strictMaskVec
            let extentDecl =
                [ sprintf "size_t %s[%d];" extentsName r ]
                @ [ for i in 0 .. r - 1 -> sprintf "%s[%d] = %s;" extentsName i nExpr ]
            let allocDecl =
                arrayAlloc { Ind = ""; Elem = elemTypeStr; Rank = r; Name = varName
                             Symm = symmArg; Strict = Some strictArg; Extents = extentsName }
            // Emit the loop nest in slot order. Track:
            //   - loopLines: the for-loop openers (with indentation)
            //   - storeSubs: the storage subscript pieces (strict-relative for
            //     groups, raw var for plain/freed)
            //   - logTuple: the logical index expressions in slot order (for
            //     assembling the full tuple whose sign is baked)
            let mutable loopLines = []
            let mutable storeSubs = ""
            let mutable logTuple = []
            let mutable depth = 0
            let mutable gi = 0    // group counter (for var naming)
            let mutable pi = 0    // plain/freed counter
            for (kind, slotRank) in slotList do
                match kind with
                | "group" ->
                    // strict left-justified sub-nest of `slotRank` levels.
                    let g = gi
                    gi <- gi + 1
                    for k in 0 .. slotRank - 1 do
                        let ind = String.replicate depth "    "
                        let v = sprintf "__dc%s_g%d_%d" varName g k
                        let logName = v + "_log"
                        let bound =
                            if k = 0 then nExpr
                            else sprintf "%s - %s - 1" nExpr (sprintf "__dc%s_g%d_%d_log" varName g (k-1))
                        let logRhs =
                            if k = 0 then v
                            else sprintf "%s + %s + 1" (sprintf "__dc%s_g%d_%d_log" varName g (k-1)) v
                        loopLines <- loopLines @
                            [ forLoop ind v bound
                              sprintf "%s    size_t %s = %s;" ind logName logRhs ]
                        storeSubs <- storeSubs + sprintf "[%s]" v
                        logTuple <- logTuple @ [logName]
                        depth <- depth + 1
                | _ ->
                    // "plain" (degenerate residual) or "freed": one dense axis.
                    let v = sprintf "__dc%s_p%d" varName pi
                    pi <- pi + 1
                    let ind = String.replicate depth "    "
                    loopLines <- loopLines @
                        [ forLoop ind v nExpr ]
                    storeSubs <- storeSubs + sprintf "[%s]" v
                    logTuple <- logTuple @ [v]
                    depth <- depth + 1
            let bodyInd = String.replicate depth "    "
            let arrInit = logTuple |> String.concat ", "
            // Source read: the source is the rank-r strict antisym storage; the
            // canonical value lives at the strict left-justified position of the
            // SORTED logical tuple. canon_fold sorts __dc_a in place (strict) and
            // yields parity + zero flag (repeat â‡’ antisym 0).
            let srcSub =
                [ for k in 0 .. r - 1 ->
                    if k = 0 then sprintf "[__dc%s_t[0]]" varName
                    else sprintf "[__dc%s_t[%d] - __dc%s_t[%d] - 1]" varName k varName (k-1) ]
                |> String.concat ""
            let body =
                [ sprintf "%sstd::array<size_t,%d> __dc%s_a = { %s };" bodyInd r varName arrInit
                  sprintf "%sbool __dc%s_z; int __dc%s_p = nested_array_utilities::canon_fold<%d>(__dc%s_a, true, __dc%s_z);" bodyInd varName varName r varName varName
                  sprintf "%ssize_t __dc%s_t[%d] = { %s };" bodyInd varName r
                      (String.concat ", " [ for k in 0 .. r - 1 -> sprintf "__dc%s_a[%d]" varName k ])
                  sprintf "%s%s%s = __dc%s_z ? %s() : nested_array_utilities::canon_transform<%s>(%s%s, __dc%s_p, nested_array_utilities::ReadTransform::NegateOnSwap);"
                      bodyInd varName storeSubs varName elemTypeStr elemTypeStr arrName srcSub varName ]
            let closes = [ for dd in depth - 1 .. -1 .. 0 -> sprintf "%s}" (String.replicate dd "    ") ]
            Some (extentDecl @ [allocDecl] @ loopLines @ body @ closes)
         | _ -> None)
     | _ -> None)


and materializeNegateConjugateForm (subst: SubstMap) (names: Map<IRId, string>) (varName: string) (elemTypeStr: string) (form: IRExpr) (arrExpr: IRExpr) : string list option =
    // Whole-array eager transform (negate for antisym transpose, conjugate
    // for Hermitian transpose). Type-PRESERVING: the result has the same
    // storage shape/SYMM as the source, so we allocate a fresh same-shape
    // array and run a flat contiguous-pool transform (negate_pool /
    // conjugate_pool). Every array reaching here has compact storage (one
    // contiguous pool), so pool_base + count is correct and storage-agnostic.
    let isConj = (match form with IRArrayConjugate _ -> true | _ -> false)
    let arrName = exprToCppCore subst names arrExpr
    let srcType = inferExprType arrExpr
    (match srcType with
     | ArrayElem arrTy ->
        let rank = arrTy.IndexTypes |> List.sumBy (fun ix -> max 1 ix.Rank)
        let extentsName = sprintf "%s_extents" varName
        // Same-shape extents: copy the source's logical extents.
        let extentDecl =
            [ sprintf "size_t %s[%d];" extentsName rank ]
            @ [ for d in 0 .. rank - 1 -> sprintf "%s[%d] = %s.extents[%d];" extentsName d arrName d ]
        // Allocate the destination with the SOURCE's storage class so the
        // result type is identical (antisym stays antisym, etc.).
        let spec = classifyOutputStorage srcType
        let symmArg =
            match spec with
            | AllocPerGroupStrict _ ->
                // Compact-grouped SYMM (antisym grouped like symmetric) so it
                // aligns with the STRICT mask emitAllocRhs hoists.
                let (sVec, _) = buildSymmVecWithStrict srcType
                if hasRealSymmetry sVec then hoistSymmDecl (sprintf "%s_symm" varName) sVec
                else "nullptr"
            | _ ->
                let symmVec = buildSymmVec srcType
                if hasRealSymmetry symmVec then hoistSymmDecl (sprintf "%s_symm" varName) symmVec
                else "nullptr"
        let allocRhs =
            match emitAllocRhs spec elemTypeStr rank symmArg extentsName with
            | Ok rhs -> rhs
            | Error msg -> sprintf "{ nullptr, %s };\n#error \"%s\"" extentsName msg
        let allocDecl = sprintf "Array<%s, %d> %s = %s;" elemTypeStr rank varName allocRhs
        // Element count: count_antisym for antisym storage, count_leaves
        // (with the SYMM mask) otherwise. Matches the allocator's traversal.
        let countExpr =
            match spec with
            | AllocAntisymmetric ->
                // Strict storage: all-ones mask + DIAGONALS=false, same as the
                // unified allocate path (count_antisym retired from Blade emission).
                let allOnes = List.replicate rank 1
                let cMask = hoistSymmDecl (sprintf "%s_anti" extentsName) allOnes
                sprintf "count_leaves<typename promote<%s, %d>::type, %s, false>(%s)" elemTypeStr rank cMask extentsName
            | AllocPerGroupStrict strictVec ->
                // Mixed strictness: count via the per-group-strict recurrence
                // using the same SYMM + STRICT masks the allocator used.
                let cStrict = hoistSymmDecl (sprintf "%s_cstrict" extentsName) strictVec
                sprintf "count_leaves_strict<typename promote<%s, %d>::type, %s, %s>(%s)" elemTypeStr rank symmArg cStrict extentsName
            | _ ->
                // Symmetric/Hermitian/dense: DIAGONALS defaults true, DEPTH defaults 0.
                sprintf "count_leaves<typename promote<%s, %d>::type, %s>(%s)" elemTypeStr rank symmArg extentsName
        let countName = sprintf "%s_n" varName
        let routine = if isConj then "conjugate_pool" else "negate_pool"
        let call =
            [ sprintf "size_t %s = %s;" countName countExpr
              sprintf "%s(pool_base(%s.data), pool_base(%s.data), %s);" routine varName arrName countName ]
        Some (extentDecl @ [allocDecl] @ call)
     | _ -> None)


and materializeArrayCopyForm (subst: SubstMap) (names: Map<IRId, string>) (varName: string) (elemTypeStr: string) (arrExpr: IRExpr) : string list option =
    // Deep copy of an existing array into a freshly allocated same-shape,
    // same-storage buffer. Backs the COPY semantics of `let mut a = Z`
    // (IRModule.MutableArrayLets): binding the Array<T,N> wrapper by value
    // shares the data pointer, so mutations through `a` would silently
    // corrupt `Z`. Structure mirrors materializeNegateConjugateForm —
    // same-shape alloc with the source's storage class (symmetric stays
    // symmetric, etc.), count_leaves for the pool cardinality — with the
    // transform replaced by a flat std::copy_n over the contiguous pool.
    let arrName = exprToCppCore subst names arrExpr
    let srcType = inferExprType arrExpr
    (match srcType with
     | ArrayElem arrTy ->
        let rank = arrTy.IndexTypes |> List.sumBy (fun ix -> max 1 ix.Rank)
        let extentsName = sprintf "%s_extents" varName
        let extentDecl =
            [ sprintf "size_t %s[%d];" extentsName rank ]
            @ [ for d in 0 .. rank - 1 -> sprintf "%s[%d] = %s.extents[%d];" extentsName d arrName d ]
        let spec = classifyOutputStorage srcType
        let symmArg =
            match spec with
            | AllocPerGroupStrict _ ->
                let (sVec, _) = buildSymmVecWithStrict srcType
                if hasRealSymmetry sVec then hoistSymmDecl (sprintf "%s_symm" varName) sVec
                else "nullptr"
            | _ ->
                let symmVec = buildSymmVec srcType
                if hasRealSymmetry symmVec then hoistSymmDecl (sprintf "%s_symm" varName) symmVec
                else "nullptr"
        let allocRhs =
            match emitAllocRhs spec elemTypeStr rank symmArg extentsName with
            | Ok rhs -> rhs
            | Error msg -> sprintf "{ nullptr, %s };\n#error \"%s\"" extentsName msg
        let allocDecl = sprintf "Array<%s, %d> %s = %s;" elemTypeStr rank varName allocRhs
        let countExpr =
            match spec with
            | AllocAntisymmetric ->
                let allOnes = List.replicate rank 1
                let cMask = hoistSymmDecl (sprintf "%s_anti" extentsName) allOnes
                sprintf "count_leaves<typename promote<%s, %d>::type, %s, false>(%s)" elemTypeStr rank cMask extentsName
            | AllocPerGroupStrict strictVec ->
                let cStrict = hoistSymmDecl (sprintf "%s_cstrict" extentsName) strictVec
                sprintf "count_leaves_strict<typename promote<%s, %d>::type, %s, %s>(%s)" elemTypeStr rank symmArg cStrict extentsName
            | _ ->
                sprintf "count_leaves<typename promote<%s, %d>::type, %s>(%s)" elemTypeStr rank symmArg extentsName
        let countName = sprintf "%s_n" varName
        let call =
            [ sprintf "size_t %s = %s;" countName countExpr
              sprintf "std::copy_n(pool_base(%s.data), %s, pool_base(%s.data));" arrName countName varName ]
        Some (extentDecl @ [allocDecl] @ call)
     | _ -> None)


and materializeGramForm (subst: SubstMap) (names: Map<IRId, string>) (varName: string) (elemTypeStr: string) (lExpr: IRExpr) (rExpr: IRExpr) (sameArray: bool) : string list option =
    // gram(A, B) = A * B^H:  result[i][j] = sum_k A[i][k] * conj(B[j][k]).
    // A : m x n, B : p x n.  conj() is std::conj on complex, identity on real
    // (we always emit std::conj; for real element types it is a harmless
    // no-op via the conj_scalar overload). Two modes:
    //   sameArray  -> square m x m, SymHermitian/SymSymmetric storage,
    //                 UPPER-TRIANGLE scatter only (i<=j, left-justified jr);
    //                 the lower triangle is recovered lazily on read
    //                 (canon ConjugateOnSwap for Hermitian, plain for sym).
    //   distinct   -> dense m x p, full scatter over all (i,j).
    let lName = exprToCppCore subst names lExpr
    let rName = exprToCppCore subst names rExpr
    let lTy = inferExprType lExpr
    let rTy = inferExprType rExpr
    (match lTy, rTy with
     | ArrayElem la, ArrayElem ra ->
        // element type of the result (complex iff either operand complex)
        let isComplexElem (t: IRType) =
            match t with IRTScalar (ETComplex64 | ETComplex128) -> true | _ -> false
        let outElem =
            if isComplexElem la.ElemType then la.ElemType
            elif isComplexElem ra.ElemType then ra.ElemType
            else la.ElemType
        let outElemStr = irTypeToCpp outElem
        // The contracted-axis extent comes from A's trailing dim at runtime.
        let nExtent = sprintf "%s.extents[1]" lName
        let mExtent = sprintf "%s.extents[0]" lName
        let pExtent = sprintf "%s.extents[0]" rName
        let extentsName = sprintf "%s_extents" varName
        // conj wrapper on B's element (std::conj; identity-safe on reals via
        // conj_scalar). Use conj_scalar to keep one spelling for real/complex.
        let mulTerm i j =
            sprintf "%s[%s][__gk] * nested_array_utilities::conj_scalar(%s[%s][__gk])" lName i rName j
        if sameArray then
            // square m x m, symmetric/Hermitian upper-triangle storage
            let extentDecl =
                [ sprintf "size_t %s[2];" extentsName
                  sprintf "%s[0] = %s;" extentsName mExtent
                  sprintf "%s[1] = %s;" extentsName mExtent ]
            let symmVec = [1; 1]
            let symmArg = hoistSymmDecl (sprintf "%s_symm" varName) symmVec
            let allocDecl =
                sprintf "Array<%s, 2> %s = { allocate<typename promote<%s, 2>::type, %s>(%s), %s };"
                    outElemStr varName outElemStr symmArg extentsName extentsName
            let loop =
                [ sprintf "for (size_t __gi = 0; __gi < %s; __gi++) {" mExtent
                  sprintf "    for (size_t __gjr = 0; __gjr < %s - __gi; __gjr++) {" mExtent
                  sprintf "        size_t __gj = __gi + __gjr;"
                  sprintf "        %s __gacc = %s();" outElemStr outElemStr
                  sprintf "        for (size_t __gk = 0; __gk < %s; __gk++) {" nExtent
                  sprintf "            __gacc += %s;" (mulTerm "__gi" "__gj")
                  sprintf "        }"
                  sprintf "        %s[__gi][__gjr] = __gacc;" varName
                  sprintf "    }"
                  sprintf "}" ]
            Some (extentDecl @ [allocDecl] @ loop)
        else
            // dense m x p
            let extentDecl =
                [ sprintf "size_t %s[2];" extentsName
                  sprintf "%s[0] = %s;" extentsName mExtent
                  sprintf "%s[1] = %s;" extentsName pExtent ]
            let allocDecl =
                sprintf "Array<%s, 2> %s = { allocate<typename promote<%s, 2>::type, nullptr>(%s), %s };"
                    outElemStr varName outElemStr extentsName extentsName
            let loop =
                [ sprintf "for (size_t __gi = 0; __gi < %s; __gi++) {" mExtent
                  sprintf "    for (size_t __gj = 0; __gj < %s; __gj++) {" pExtent
                  sprintf "        %s __gacc = %s();" outElemStr outElemStr
                  sprintf "        for (size_t __gk = 0; __gk < %s; __gk++) {" nExtent
                  sprintf "            __gacc += %s;" (mulTerm "__gi" "__gj")
                  sprintf "        }"
                  sprintf "        %s[__gi][__gj] = __gacc;" varName
                  sprintf "    }"
                  sprintf "}" ]
            Some (extentDecl @ [allocDecl] @ loop)
     | _ -> None)

/// Convert IRExpr to C++ using context
let exprToCppCtx (ctx: CodeGenContext) (expr: IRExpr) : string =
    exprToCpp ctx.VarNames expr

/// Render an IRExpr with a contains-substitution map active. Used by the
/// mask renderer (Step 3, upcoming): the mask walks its predicate, hoists
/// builds for each hoistable contains, builds the substitution map, and
/// then calls this function to produce the predicate's C++ string with
/// `set.count(...)` substituted for each hoisted IRContains node.
///
/// With an empty substitution this is byte-identical to `exprToCpp`.
///
/// The substitution propagates through every renderer in the rec group:
/// `exprToCppCore`, `exprToCppWithVarCore`, `genApplyCombinatorExpr`, and
/// `materializeInlineForm`. That means a contains nested inside a method_for
/// kernel inside a mask predicate gets the same treatment as a contains
/// directly in the predicate â€” wherever Phase B's bottom-up walk would
/// flag the probe, Step 2's renderer can substitute it. External callers
/// of `materializeInlineForm` / `genApplyCombinatorExpr` (the binding-
/// level entry points) pass `emptySubst`; Step 3 wires the mask renderer
/// to populate a real subst map when materializing a mask whose
/// predicate carries probes.
let exprToCppWithSubst (subst: SubstMap) (names: Map<IRId, string>) (expr: IRExpr) : string =
    exprToCppCore subst names expr

// ============================================================================
// Loop Nest Code Generation
// ============================================================================

/// Generate the element binding expression for a single array at a loop level
/// Returns (cppCode, newPeeledName) where newPeeledName is used for subsequent levels
let genElementBindingNew (level: LoopIndexBinding) (elem: ElementBinding) (currentName: string) 
    : string * string =
    match elem.Virtual with
    | VirtualRange offset when
        (match level.Extent with IRCompoundMask _ -> true | _ -> false) &&
        (match elem.SlotTag with Some t when t.StartsWith "__halowin|" -> true | _ -> false) ->
        // halo<CompoundIdx<m>>: the kernel param is a POINTER into the
        // materialized compound index's contiguous rank_to_tuple table at the
        // CENTER cell (ordinal i + start). Body reads w(o) then step it by a
        // signed subscript (IRHaloUnhash: `w[(o)][0]`) — param-local, so the
        // reads survive kernel lifting to a standalone function. The interior
        // bound shrink is on the loop header (StrictOffset).
        let centerExpr =
            match offset with
            | None -> level.IndexName
            | Some (IRLit (IRLitInt n)) -> sprintf "(%s + %dL)" level.IndexName n
            | Some off -> sprintf "(%s + %s)" level.IndexName (exprToCpp Map.empty off)
        let code =
            sprintf "const std::array<size_t, 1>* %s = &%s_cidx->rank_to_tuple[%s];"
                elem.ParamName elem.ArrayName centerExpr
        (code, elem.ParamName)
    | VirtualRange _ when (match level.Extent with IRCompoundMask _ -> true | _ -> false) ->
        // range<CompoundIdx<m>>: ONE loop level over the present cells; each
        // kernel param binds one COORDINATE of the current cell's tuple via
        // the materialized index's O(1) unhash (rank_to_tuple lookup). The
        // index variable `<name>_cidx` is emitted by the caller's
        // materialization step (genCompoundIndexFromMask) before the nest.
        // Offsets are not meaningful on a masked product space; TypeCheck
        // never produces one for a compound range slot.
        let code = sprintf "int64_t %s = %s_cidx->unhash(%s)[%d];"
                           elem.ParamName elem.ArrayName level.IndexName elem.RankComponent
        (code, elem.ParamName)
    | VirtualRange offset ->
        // range<I>: kernel param gets the loop index, plus offset if present.
        // The binding must be int64_t, NOT size_t: the param is Int64-typed in
        // Blade (and the standalone lambda signature), and a size_t binding
        // makes negative intermediates wrap unsigned before any Float64
        // conversion — 0.5 * (i - 1) at i=0 came out as 0.5 * 2^64-1.
        // Same signedness rule for the unhash and reverse arms above/below.
        let valueExpr =
            match offset with
            | None -> level.IndexName
            | Some (IRLit (IRLitInt n)) -> sprintf "(%s + %dL)" level.IndexName n
            | Some off -> sprintf "(%s + %s)" level.IndexName (exprToCpp Map.empty off)
        let code = sprintf "int64_t %s = %s;" elem.ParamName valueExpr
        (code, elem.ParamName)
    | VirtualReverse ->
        // reverse<I>: kernel param gets (extent - 1 - i)
        let extentStr =
            match level.Extent with
            | IRLit (IRLitInt n) -> sprintf "%d" n
            | _ -> sprintf "%s.extents[%d]" elem.ArrayName elem.DimIndex
        let code = sprintf "int64_t %s = (%s - 1 - %s);" elem.ParamName extentStr level.IndexName
        (code, elem.ParamName)
    | RealArray when level.FusedRank.IsSome ->
        // Arc 1 fused JOINT level (see IR.fuseJointSLevels): this single loop
        // level spans the argument's whole plain-dense S-block (d dims), so the
        // grouped triangular iteration ranges over whole argument index tuples â€”
        // the joint symmetry, the only one an identity group licenses
        // (docs/formalism.md Â§12.4). The loop var is left-justified-relative
        // under triangular chaining, so component 0 first shifts it to the
        // ABSOLUTE compound coordinate p (deps + strict offset, mirroring the
        // dense arm's case 1) and binds it once per (level, array); every
        // component then decodes its per-dim coordinate row-major
        //   coord_rc = (p / prod_{j>rc} n_j) % n_rc
        // (matching lex enumeration and the storage bijection) and peels
        // exactly one dimension of the array.
        let d = level.FusedRank.Value
        let rc = elem.RankComponent
        let extAt j = sprintf "%s.extents[%d]" elem.ArrayName j
        let strideAfter k =
            if k >= d - 1 then "1"
            else [k + 1 .. d - 1] |> List.map extAt |> String.concat " * "
        let pAbs = sprintf "__p%d_a%d" level.Level elem.ArrayPosition
        let pAbsDecl =
            if rc = 0 then
                let depParts = level.BoundDependencies |> List.map (sprintf "__i%d")
                let offsetParts = if level.StrictOffset > 0 then [string level.StrictOffset] else []
                let sum =
                    match depParts @ offsetParts with
                    | [] -> level.IndexName
                    | shifts -> sprintf "%s + %s" level.IndexName (String.concat " + " shifts)
                sprintf "size_t %s = %s; " pAbs sum
            else ""
        let coordName = sprintf "%s_c%d" pAbs rc
        let coordExpr =
            if d = 1 then pAbs
            elif rc = 0 then sprintf "%s / (%s)" pAbs (strideAfter 0)
            elif rc = d - 1 then sprintf "%s %% %s" pAbs (extAt rc)
            else sprintf "(%s / (%s)) %% %s" pAbs (strideAfter rc) (extAt rc)
        let levelsConsumed = rc + 1
        let resultRank = elem.ArrayRank - levelsConsumed
        let elemTypeStr = elemTypeToCpp elem.ArrayElemType
        let newName = sprintf "%s__%s_%d" currentName level.IndexName rc
        let peel =
            if resultRank <= 0 then
                sprintf "%s %s = %s[%s];" elemTypeStr newName currentName coordName
            else
                sprintf "Array<%s, %d> %s = { %s.data[%s], %s.extents + 1 };"
                    elemTypeStr resultRank newName currentName coordName currentName
        let code = sprintf "%ssize_t %s = %s; %s" pAbsDecl coordName coordExpr peel
        (code, newName)
    | RealArray when (match level.Extent with IRCompoundMask _ -> true | _ -> false) ->
        // Compound axis: peel the present-cell index `r` against the COMPACT
        // buffer (.data), not the dense .extents grid. A compound axis has no
        // bound-dependency / strict shifts (it is its own group) and the compound
        // level is the array's first level, so the index is just the loop var.
        // With no trailing dims (all-dims mask, resultRank <= 0) this is the
        // scalar leaf data[r]; with a trailing dim it yields the trailing ROW
        // base pointer (data + r*trailing_stride), which the ordinary dense peel
        // then indexes for the (single) trailing level. Ragged trailing (variable
        // per-cell extent) needs a cell_offset table and is not yet emitted.
        let levelsConsumed = elem.RankComponent + 1
        let resultRank = elem.ArrayRank - levelsConsumed
        let elemTypeStr = elemTypeToCpp elem.ArrayElemType
        let r = level.IndexName
        let newName = sprintf "%s__%s" currentName r
        let code =
            if resultRank <= 0 then
                sprintf "%s %s = %s.data[%s];" elemTypeStr newName currentName r
            else
                sprintf "%s* %s = %s.data + %s * %s.trailing_stride;" elemTypeStr newName currentName r currentName
        (code, newName)
    | RealArray ->
        // After indexing once, remaining rank decreases
        let levelsConsumed = elem.RankComponent + 1  // How many levels of this array consumed so far
        let resultRank = elem.ArrayRank - levelsConsumed
        let elemTypeStr = elemTypeToCpp elem.ArrayElemType
        
        // Array index into THIS level. The base is the current loop var.
        //
        // Two cases, distinguished by whether the array has already been
        // peeled (sliced) at an outer level:
        //
        //  (1) Reading the ORIGINAL array (currentName == elem.ArrayName): the
        //      array is still flat at this position, so the index must be the
        //      ABSOLUTE coordinate = loop var + sum of bound-dependency vars
        //      (which shift a row-relative 0-based loop var to its absolute row
        //      base) + the strict offset. This is the producer-style flat read,
        //      e.g. A[__i1 + __i0].
        //
        //  (2) Reading into an ALREADY-SLICED sub-array (currentName has been
        //      peeled, currentName <> elem.ArrayName): each outer peel already
        //      consumed its index via `data[__ik]`, so the sub-array is the row
        //      and the within-row index is the LOCAL loop var alone. Re-adding
        //      the dependency vars here double-counts the outer index and reads
        //      out of bounds. (This is the compact-symmetric elementwise-read
        //      bug: sym____i0[__i1 + __i0] should be sym____i0[__i1]. Verified
        //      against a dense reference: local-only yields the correct
        //      canonical order.) The StrictOffset (antisym diagonal shift) is a
        //      within-row concept and still applies.
        let isSliced = currentName <> elem.ArrayName
        let arrayIndex =
            let depParts =
                if isSliced then []   // outer indices already consumed by the slice
                else level.BoundDependencies |> List.map (sprintf "__i%d")
            let offsetParts = if level.StrictOffset > 0 then [string level.StrictOffset] else []
            match depParts @ offsetParts with
            | [] -> level.IndexName
            | shifts -> sprintf "%s + %s" level.IndexName (String.concat " + " shifts)
        
        let newName = sprintf "%s__%s" currentName level.IndexName
        let code =
            if resultRank <= 0 then
                // Scalar leaf: peel returns the element value directly.
                sprintf "%s %s = %s[%s];" elemTypeStr newName currentName arrayIndex
            else
                // Sub-array peel: construct a wrapper so the sub still
                // carries shape information (.extents shifted one level
                // deeper). The wrapper's data pointer comes from indexing
                // the parent's data; the extents pointer is parent's
                // extents+1. Indexing transparency works through operator[].
                sprintf "Array<%s, %d> %s = { %s.data[%s], %s.extents + 1 };" 
                    elemTypeStr resultRank newName currentName arrayIndex currentName
        (code, newName)

/// Streamed-source element binding: the source is a `alias.stream` provider
/// read — NO materialized array exists, so instead of peeling, accumulate
/// this level's ABSOLUTE site coordinate, and at the FIBER level (exactly
/// one trailing axis remaining) emit the provider's in-nest fiber read into
/// this element's dedicated buffer plus a rank-1 wrapper bind (the same
/// `Array<T,1>` shape a fiber peel would produce, so the kernel body is
/// untouched). Returns (lines, Some wrapperName at the fiber level, updated
/// accumulated site coordinates).
let genElementBindingStreamed (level: LoopIndexBinding) (elem: ElementBinding) (spec: ProviderReadSpec) (accSites: string list)
    : string list * string option * string list =
    let fiberGen =
        match Blade.ProviderRegistry.tryFind spec.Provider with
        | Some p ->
            (match p.GenStreamFiber with
             | Some g -> g
             | None -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf "provider '%s' does not support streamed reads (variable '%s')" spec.Provider spec.VarName))))
        | None -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf "provider '%s' is not registered" spec.Provider)))
    let litExtents =
        spec.VarType.IndexTypes
        |> List.collect (fun ix -> List.replicate ix.Rank ix.Extent)
        |> List.map (fun e ->
            match e with
            | IRLit (IRLitInt n) -> n
            | _ -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf "streamed variable '%s' requires literal extents" spec.VarName))))
    let elemTypeStr = elemTypeToCpp elem.ArrayElemType
    let bufName = sprintf "%s_fb_p%d" elem.ArrayName elem.ArrayPosition
    let emitFiber (sites: string list) (newName: string) : string list =
        fiberGen spec.FilePath spec.VarName elem.ArrayName bufName sites spec.VarType
        @ [ sprintf "Array<%s, 1> %s = { %s, %s_fiber_ext };" elemTypeStr newName bufName elem.ArrayName ]
    match elem.Virtual with
    | RealArray when level.FusedRank.IsSome ->
        // Fused joint level (identity-group comm over multi-dim sites): keep
        // the absolute-compound-coordinate shift and the row-major per-dim
        // decode, but with LITERAL extents (there is no array to consult),
        // and replace the chained peels with coordinate accumulation.
        let d = level.FusedRank.Value
        let rc = elem.RankComponent
        let strideAfter k =
            if k >= d - 1 then 1L
            else [k + 1 .. d - 1] |> List.fold (fun acc j -> acc * litExtents.[j]) 1L
        let pAbs = sprintf "__p%d_a%d" level.Level elem.ArrayPosition
        let pAbsDecl =
            if rc = 0 then
                let depParts = level.BoundDependencies |> List.map (sprintf "__i%d")
                let offsetParts = if level.StrictOffset > 0 then [string level.StrictOffset] else []
                let sum =
                    match depParts @ offsetParts with
                    | [] -> level.IndexName
                    | shifts -> sprintf "%s + %s" level.IndexName (String.concat " + " shifts)
                [ sprintf "size_t %s = %s;" pAbs sum ]
            else []
        let coordName = sprintf "%s_c%d" pAbs rc
        let coordExpr =
            if d = 1 then pAbs
            elif rc = 0 then sprintf "%s / %dUL" pAbs (strideAfter 0)
            elif rc = d - 1 then sprintf "%s %% %dUL" pAbs litExtents.[rc]
            else sprintf "(%s / %dUL) %% %dUL" pAbs (strideAfter rc) litExtents.[rc]
        let coordDecl = sprintf "size_t %s = %s;" coordName coordExpr
        let resultRank = elem.ArrayRank - (rc + 1)
        let sites' = accSites @ [coordName]
        if resultRank <= 0 then
            raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf "streamed variable '%s': elementwise consumption is not stream-eligible in v1 — use a fiber kernel (rank-1 array parameter over the trailing axis) or bind with .read" spec.VarName)))
        elif resultRank = 1 then
            let newName = sprintf "%s__%s_%d" elem.ArrayName level.IndexName rc
            (pAbsDecl @ [coordDecl] @ emitFiber sites' newName, Some newName, sites')
        else
            (pAbsDecl @ [coordDecl], None, sites')
    | RealArray ->
        // Plain dense/triangular level: the ABSOLUTE coordinate (the source
        // is never sliced — deps + strict offset always apply).
        let arrayIndex =
            let depParts = level.BoundDependencies |> List.map (sprintf "__i%d")
            let offsetParts = if level.StrictOffset > 0 then [string level.StrictOffset] else []
            match depParts @ offsetParts with
            | [] -> level.IndexName
            | shifts -> sprintf "%s + %s" level.IndexName (String.concat " + " shifts)
        let resultRank = elem.ArrayRank - (elem.RankComponent + 1)
        let sites' = accSites @ [arrayIndex]
        if resultRank <= 0 then
            raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf "streamed variable '%s': elementwise consumption is not stream-eligible in v1 — use a fiber kernel (rank-1 array parameter over the trailing axis) or bind with .read" spec.VarName)))
        elif resultRank = 1 then
            let newName = sprintf "%s__%s" elem.ArrayName level.IndexName
            (emitFiber sites' newName, Some newName, sites')
        else
            ([], None, sites')
    | _ ->
        raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf "streamed variable '%s': virtual/compound binding shapes are not stream-eligible" spec.VarName)))

/// Fiber destination buffers already declared in the CURRENT program —
/// a program with several nests over one streamed source must declare
/// each `<v>_fb_p<pos>` buffer once and reuse it (sequential nests; same
/// source ⇒ same length and element type). AsyncLocal like the symm-decl
/// collector: parallel test tasks get their own cell; program assembly
/// resets it.
let private streamBufDeclsStorage =
    System.Threading.AsyncLocal<Set<string> ref>()

let streamBufDeclsCell () : Set<string> ref =
    let v = streamBufDeclsStorage.Value
    if isNull (box v) then
        let fresh = ref Set.empty
        streamBufDeclsStorage.Value <- fresh
        fresh
    else v

/// Streamed inputs across a set of leaf nests: the streamed map restricted
/// to their inputs, plus the per-argument fiber destination buffer
/// declarations to emit before the (merged) nest — skipping buffers already
/// declared earlier in this program. Shared by the single-leaf and fused
/// paths so buffer naming cannot drift.
let streamedNestSetup (streamedArrays: Map<string, ProviderReadSpec>) (ind: string) (leafCgs: LoopNestCodeGen list) : Map<string, ProviderReadSpec> * string list =
    let streamedMap =
        leafCgs
        |> List.collect (fun cg -> cg.InputArrayNames)
        |> List.distinct
        |> List.choose (fun n -> Map.tryFind n streamedArrays |> Option.map (fun s -> (n, s)))
        |> Map.ofList
    let prologue =
        if Map.isEmpty streamedMap then []
        else
            let cell = streamBufDeclsCell ()
            leafCgs
            |> List.collect (fun cg -> cg.Bindings)
            |> List.collect (fun b -> b.Elements)
            |> List.choose (fun e ->
                match Map.tryFind e.ArrayName streamedMap with
                | Some spec when e.ArrayRank - (e.RankComponent + 1) = 1 ->
                    let bufName = sprintf "%s_fb_p%d" e.ArrayName e.ArrayPosition
                    if Set.contains bufName cell.Value then None
                    else
                        cell.Value <- Set.add bufName cell.Value
                        let elemCpp = elemTypeToCpp e.ArrayElemType
                        let fiberLen =
                            match (List.last spec.VarType.IndexTypes).Extent with
                            | IRLit (IRLitInt n) -> n
                            | _ -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf "streamed variable '%s' requires literal extents" spec.VarName)))
                        Some (sprintf "%s%s* %s = new %s[%d];" ind elemCpp bufName elemCpp fiberLen)
                | _ -> None)
            |> List.distinct
    (streamedMap, prologue)

/// Decide the OpenMP pragma for a loop NEST, based on the bound structure of
/// its levels. Parallelization strategy is a function of index-type structure:
///
///   - RECTANGULAR leading levels (bound has no dependency on outer indices:
///     BoundDependencies empty AND StrictOffset = 0) form a perfect rectangular
///     iteration space. Where additionally COLLAPSE-ELIGIBLE (see gate below),
///     they can be fused:
///       #pragma omp parallel for collapse(d)
///     where d = count of leading rectangular AND collapse-eligible levels
///     (d >= 2 to be worth it). Collapse is valuable for short outer dimensions:
///     a 3x3x3 nest has only 3 outer iterations, so without collapse no more
///     than 3 threads can be used; collapse(3) exposes all 27.
///
///   - TRIANGULAR levels (symmetric/antisymmetric: inner bound depends on outer
///     indices, e.g. j < N - i) are NON-RECTANGULAR. OpenMP `collapse` requires
///     a rectangular space and is unsafe here. But the OUTERMOST loop is still
///     independently parallelizable (each outer index owns a disjoint triangular
///     sub-slab). Triangular work is unbalanced across the outer index (i=0 does
///     the most, i=N-1 the least), so we use dynamic scheduling:
///       #pragma omp parallel for schedule(dynamic)
///     on the outer loop only; inner dependent loops stay sequential.
///
/// COLLAPSE-ELIGIBILITY GATE (architectural seam):
///   OpenMP `collapse(d)` requires the d collapsed loops to be PERFECTLY NESTED
///   â€” no code of any kind between the loop headers. This is in direct tension
///   with Blade's iteration model (DMWF), which assumes loop levels are
///   SEPARABLE: code may be injected between levels (streaming-I/O batch
///   boundaries, per-level Reynolds folds, etc.). So a level is collapse-eligible
///   only if (a) it is rectangular AND (b) nothing is injected between it and the
///   next collapsed level.
///
///   Today (b) always holds: production codegen injects nothing between levels.
///   So collapseEligible == isRectangular for now. When streaming/batching lands,
///   a level that carries a batch boundary (or any inter-level injection) becomes
///   collapse-INELIGIBLE even if rectangular, and the collapse prefix must stop
///   before it. That future constraint has exactly one home: the collapseEligible
///   predicate below. The rest of the decision logic does not change.
///
/// Returns the pragma string (with trailing newline+indent) for the OUTERMOST
/// loop, or "" if the nest should not be parallelized. Inner loops never carry
/// a pragma (collapse subsumes them; triangular inners are sequential).
///
/// The decision is driven entirely by per-level bound structure already present
/// in the bindings â€” no index-type tag is consulted directly, so new index
/// types get a sensible strategy from their bound shape automatically.
let genNestPragma (bindings: LoopIndexBinding list) (pragmaIndent: string) : string =
    match bindings with
    | [] -> ""
    | outer :: rest ->
        if not outer.IsParallel then ""
        else
            // A level is "rectangular" iff its bound is independent of outer indices.
            let isRectangular (b: LoopIndexBinding) =
                b.BoundDependencies.IsEmpty && b.StrictOffset = 0
            // COLLAPSE-ELIGIBILITY GATE. Currently equals rectangularity, but is
            // a SEPARATE predicate by design: it is the single extension point
            // for the future "no inter-level injection" constraint (see header).
            // When a binding gains an inter-level-injection marker (batch
            // boundary, streaming stage), add that exclusion HERE â€” e.g.
            //   isRectangular b && not b.HasInterLevelInjection
            // and the collapse prefix below will correctly stop before it.
            let collapseEligible (b: LoopIndexBinding) =
                isRectangular b
            // Collapse depth = length of the leading prefix that is BOTH
            // rectangular and collapse-eligible. (takeWhile stops at the first
            // level failing either condition â€” that is the gate doing its job.)
            let collapseDepth =
                bindings |> List.takeWhile collapseEligible |> List.length
            // Any triangular level anywhere below the outer loop means the
            // per-outer-iteration work is unbalanced (inner extents shrink with
            // the outer index), so dynamic scheduling is warranted.
            let hasTriangularBelow =
                rest |> List.exists (fun b -> not (isRectangular b))
            if collapseDepth >= 2 then
                // Perfect, collapse-eligible rectangular prefix of >=2 levels:
                // fuse them. (A collapsed rectangular prefix is balanced; static.)
                sprintf "#pragma omp parallel for collapse(%d)\n%s" collapseDepth pragmaIndent
            elif hasTriangularBelow then
                // Outer loop rectangular (or single), but triangular work below:
                // parallelize the outer loop with dynamic schedule for balance.
                sprintf "#pragma omp parallel for schedule(dynamic)\n%s" pragmaIndent
            else
                // Outer loop parallel, remaining work balanced (rectangular or
                // none): plain static parallel for.
                sprintf "#pragma omp parallel for\n%s" pragmaIndent

/// Generate a for-loop header (no pragma; pragmas are nest-level, see
/// genNestPragma, and are prepended only at the outermost level by the caller).
/// Bounds are computed as: extent - sum of all dependency indices
/// Array names that are compound in this loop nest: those carrying an
/// IRCompoundMask binding. A sibling binding referencing the same array with a
/// non-mask Extent is that compound's trailing dim (bound = trailing_stride).
let compoundArrayNamesOf (bindings: LoopIndexBinding list) : Set<string> =
    bindings
    |> List.choose (fun b -> match b.Extent with IRCompoundMask _ -> Some b.ExtentArrayRef | _ -> None)
    |> Set.ofList

/// The C++ expression for a loop level's (un-subtracted) upper bound. Shared
/// by genForLoopHeader (the `for` header renderer) and the MPI-dense slab
/// prologue (which needs the OUTERMOST level's extent to compute per-rank
/// [lo, hi) bounds) â€” factored so the two can never drift.
let genLoopBoundExpr (compoundArrays: Set<string>) (binding: LoopIndexBinding) : string =
    match binding.Extent with
    | IRLit (IRLitInt n) -> sprintf "%d" n
    // A compound axis iterates its present cells, so its bound is the
    // runtime cardinality of the compact index, not a dense .extents entry.
    // (The compound level carries IRCompoundMask as its Extent; ExtentArrayRef
    // is the compound array's name -> `<arr>.idx->cardinality`.) A VIRTUAL
    // compound source (range<CompoundIdx<m>>) has no Compound value to hang
    // `.idx` off; its bound is the standalone materialized index
    // `<name>_cidx->cardinality` (see genCompoundIndexFromMask).
    | IRCompoundMask _ ->
        let isVirtualCompound =
            binding.Elements
            |> List.exists (fun e ->
                e.ArrayName = binding.ExtentArrayRef &&
                (match e.Virtual with VirtualRange _ -> true | _ -> false))
        if isVirtualCompound
        then sprintf "%s_cidx->cardinality" binding.ExtentArrayRef
        else sprintf "%s.idx->cardinality" binding.ExtentArrayRef
    // A compound array's trailing dim has no dense .extents; its (single)
    // trailing extent is the compact buffer's trailing_stride. A literal
    // trailing extent already took the IRLit arm above, so this catches a
    // NON-literal trailing extent, where `.extents[dim]` would reference a
    // member the Compound layout does not have. (Multi-trailing is not yet
    // supported: trailing_stride is the product, not a per-dim extent.)
    | _ when Set.contains binding.ExtentArrayRef compoundArrays ->
        sprintf "%s.trailing_stride" binding.ExtentArrayRef
    // Arc 1 fused JOINT level: the axis spans the array's first d dense
    // dims; its bound is the product of those extents. A literal product
    // was already folded to IRLit by IR.fuseJointSLevels (first arm); this
    // renders the runtime form.
    | _ when binding.FusedRank.IsSome ->
        [0 .. binding.FusedRank.Value - 1]
        |> List.map (sprintf "%s.extents[%d]" binding.ExtentArrayRef)
        |> String.concat " * "
    | _ -> sprintf "%s.extents[%d]" binding.ExtentArrayRef binding.ExtentDimRef

let genForLoopHeader (compoundArrays: Set<string>) (binding: LoopIndexBinding) : string =
    let extentStr = genLoopBoundExpr compoundArrays binding

    // Compute bound subtraction from dependencies
    let subtraction =
        if binding.BoundDependencies.IsEmpty && binding.StrictOffset = 0 then ""
        else
            let depParts = binding.BoundDependencies |> List.map (sprintf "__i%d")
            let offsetParts = if binding.StrictOffset > 0 then [sprintf "%d" binding.StrictOffset] else []
            depParts @ offsetParts |> String.concat " - " |> sprintf " - %s"

    sprintf "for (size_t %s = 0; %s < %s%s; %s++) {"
        binding.IndexName
        binding.IndexName
        extentStr
        subtraction
        binding.IndexName

/// Generate complete loop nest as C++ code
/// Tracks peeled names across levels and generates element bindings for all arrays at each level

// permutations / permSign moved to ReynoldsCore.fs (shared term-plan core).

/// Is this binary operation commutative? (a op b) = (b op a)
let isCommutativeOp (op: IRBinOp) : bool =
    match op with
    | IRAdd | IRMul | IREq | IRNeq | IRAnd | IROr -> true
    | _ -> false

/// Is this binary operation associative? (a op b) op c = a op (b op c)
/// Only ops that are BOTH commutative and associative get flattened.
let isAssociativeOp (op: IRBinOp) : bool =
    match op with
    | IRAdd | IRMul | IRAnd | IROr -> true
    | _ -> false

/// Flatten nested applications of the same commutative+associative op into a list of operands.
/// E.g. (a * b) * c â†’ [a; b; c]
let rec flattenAssocOp (mode: IRBinOpMode) (op: IRBinOp) (expr: IRExpr) : IRExpr list =
    match expr with
    | IRBinOp (m, o, l, r) when o = op && m = mode ->
        flattenAssocOp mode op l @ flattenAssocOp mode op r
    | _ -> [expr]

/// Generate a canonical string key for an IR expression under a given name mapping.
/// Commutative binary operations have their children sorted by canonical key,
/// and associative+commutative chains are flattened and sorted, so that e.g.
/// (a * b) * c and c * (b * a) produce the same key.
/// Used for Reynolds permutation deduplication.
let rec canonicalKey (nameMap: Map<int, string>) (expr: IRExpr) : string =
    match expr with
    | IRVar (id, _) ->
        Map.tryFind id nameMap |> Option.defaultValue (sprintf "v%d" id)
    | IRParam (name, _, _) ->
        sprintf "p:%s" name
    | IRLit lit ->
        match lit with
        | IRLitInt n -> string n
        // Round-trip spelling: %g's 6-digit key would COLLIDE distinct
        // constants and wrongly deduplicate structurally-different
        // Reynolds terms (multiplicity miscount).
        | IRLitFloat f -> floatToCppLiteral f
        | IRLitBool b -> if b then "true" else "false"
        | IRLitString s -> sprintf "\"%s\"" s
        | IRLitUnit -> "()"
    | IRBinOp (mode, op, l, r) when isCommutativeOp op && isAssociativeOp op ->
        let operands = flattenAssocOp mode op expr
        let keys = operands |> List.map (canonicalKey nameMap) |> List.sort
        sprintf "(%A/%A %s)" mode op (keys |> String.concat " ")
    | IRBinOp (mode, op, l, r) when isCommutativeOp op ->
        let lk = canonicalKey nameMap l
        let rk = canonicalKey nameMap r
        let children = [lk; rk] |> List.sort
        sprintf "(%A/%A %s %s)" mode op children.[0] children.[1]
    | IRBinOp (mode, op, l, r) ->
        sprintf "(%A/%A %s %s)" mode op (canonicalKey nameMap l) (canonicalKey nameMap r)
    | IRUnaryOp (op, inner) ->
        sprintf "(u%A %s)" op (canonicalKey nameMap inner)
    | IRApp (func, args, _) ->
        let fk = canonicalKey nameMap func
        let ak = args |> List.map (canonicalKey nameMap) |> String.concat ","
        sprintf "(call %s [%s])" fk ak
    | IRIf (cond, thn, els) ->
        sprintf "(if %s %s %s)" (canonicalKey nameMap cond) (canonicalKey nameMap thn) (canonicalKey nameMap els)
    | IRLet (id, value, body) ->
        sprintf "(let v%d=%s in %s)" id (canonicalKey nameMap value) (canonicalKey nameMap body)
    | IRTupleProj (tup, idx, _) ->
        sprintf "(proj %d %s)" idx (canonicalKey nameMap tup)
    | IRTuple elems ->
        let ek = elems |> List.map (canonicalKey nameMap) |> String.concat ","
        sprintf "(tuple %s)" ek
    | IRComplex (re, im) ->
        sprintf "(complex %s %s)" (canonicalKey nameMap re) (canonicalKey nameMap im)
    | IRFieldAccess (obj, field) ->
        sprintf "(field %s %s)" (canonicalKey nameMap obj) field
    | IRStructLit (name, fields) ->
        let fk = fields |> List.map (fun (f, e) -> sprintf "%s=%s" f (canonicalKey nameMap e)) |> String.concat ","
        sprintf "(struct %s {%s})" name fk
    | IRMatch (scrutinee, cases) ->
        let sk = canonicalKey nameMap scrutinee
        let ck = cases |> List.map (fun c -> sprintf "%A->%s" c.Pattern (canonicalKey nameMap c.Body)) |> String.concat "|"
        sprintf "(match %s [%s])" sk ck
    | IRIndex (arr, indices, _) ->
        let ak = canonicalKey nameMap arr
        let ik = indices |> List.map (canonicalKey nameMap) |> String.concat ","
        sprintf "(idx %s [%s])" ak ik
    | IRArrayLit (elems, _) ->
        let ek = elems |> List.map (canonicalKey nameMap) |> String.concat ","
        sprintf "(arrlit [%s])" ek
    | IRExtent (arr, dim) ->
        sprintf "(extent %s %d)" (canonicalKey nameMap arr) dim
    | IRRank arr ->
        sprintf "(rank %s)" (canonicalKey nameMap arr)
    | IRPolyIndex (pack, idx) ->
        sprintf "(polyidx %s %s)" (canonicalKey nameMap pack) (canonicalKey nameMap idx)
    | IRNth -> "nth"
    | IRZero -> "zero"
    | IRSlice (arr, dim, start, stop) ->
        sprintf "(slice %s %d %s %s)" (canonicalKey nameMap arr) dim (canonicalKey nameMap start) (canonicalKey nameMap stop)
    | IRCurry (arr, idx, rank) ->
        sprintf "(curry %s %s %d)" (canonicalKey nameMap arr) (canonicalKey nameMap idx) rank
    | IRTranspose (arr, d1, d2) ->
        sprintf "(transpose %s %d %d)" (canonicalKey nameMap arr) d1 d2
    | IRDecompact (arr, d) ->
        sprintf "(decompact %s %d)" (canonicalKey nameMap arr) d
    | IRArrayNegate arr ->
        sprintf "(array_negate %s)" (canonicalKey nameMap arr)
    | IRArrayConjugate arr ->
        sprintf "(array_conjugate %s)" (canonicalKey nameMap arr)
    | IRAssign (lhs, rhs) ->
        sprintf "(assign %s %s)" (canonicalKey nameMap lhs) (canonicalKey nameMap rhs)
    | IRForRange (vid, lo, hi, body) ->
        sprintf "(for v%d %s %s %s)" vid (canonicalKey nameMap lo) (canonicalKey nameMap hi) (canonicalKey nameMap body)
    | _ ->
        // Combinators, compute, reynolds, etc. â€” won't appear in kernel bodies.
        // Use unique repr to prevent false dedup.
        sprintf "(opaque %d %A)" (expr.GetHashCode()) (expr.GetType().Name)

/// Reynolds kernel codegen result: C++ expression + dedup statistics.
type ReynoldsResult = {
    CppExpr: string
    TotalPerms: int
    UniqueTerms: int
}

/// Generate the kernel expression string, applying Reynolds permutation sum if needed.
/// For non-Reynolds kernels, just returns `exprToCpp nameMap kernelExpr`.
/// For Reynolds kernels, generates the sum over all permutations of the kernel parameters,
/// deduplicating structurally equivalent permutations via canonical keys.

let genKernelExprWithReynolds
    (kernelExpr: IRExpr)
    (kernelParams: IRParam list)
    (hasReynolds: bool)
    (isAntisymmetric: bool)
    (nameMap: Map<int, string>)
    (paramFinalNames: Map<int, string>) : ReynoldsResult =
    if hasReynolds && kernelParams.Length >= 2 then
        let n = kernelParams.Length
        let paramCppNames =
            kernelParams |> List.map (fun p ->
                Map.tryFind p.VarId paramFinalNames
                |> Option.defaultValue (sprintf "__p%d" p.VarId))
        // Name map for a permutation: each kernel param's VarId maps to the
        // C++ name of the parameter it is permuted to (layered over nameMap).
        let permNameMap (perm: int list) =
            kernelParams |> List.mapi (fun i p ->
                (p.VarId, paramCppNames.[perm.[i]]))
            |> List.fold (fun acc (vid, name) -> Map.add vid name acc) nameMap
        // Enumerate + dedup the permutation terms (canonical key normalizes
        // commutative ops). The plan is rendering-independent, so a future IR
        // interpreter can reuse the exact enumeration/dedup/ordering.
        let plan = reynoldsTermPlan n isAntisymmetric (fun perm -> canonicalKey (permNameMap perm) kernelExpr)
        let totalPerms = plan.TotalPerms
        let uniqueTerms = plan.Terms.Length
        // Build the sum expression with multiplicity coefficients
        let formatTerm coeff expr =
            match isAntisymmetric with
            | true ->
                if abs coeff = 1 then
                    if coeff > 0 then expr else sprintf "(-%s)" expr
                else sprintf "(%d * %s)" coeff expr
            | false ->
                if coeff = 1 then expr else sprintf "(%d * %s)" coeff expr
        let sumExpr =
            plan.Terms |> List.mapi (fun i (coeff, perm) ->
                let expr = exprToCpp (permNameMap perm) kernelExpr
                let term = formatTerm coeff expr
                if i = 0 then term
                elif isAntisymmetric && coeff < 0 then
                    sprintf " - %s" (formatTerm (abs coeff) expr)
                else sprintf " + %s" term)
            |> String.concat ""
        let cppExpr =
            if plan.Terms.IsEmpty then
                "0.0"  // Complete cancellation (e.g. antisymmetrization of symmetric kernel)
            else
                sprintf "(%s)" sumExpr
        { CppExpr = cppExpr; TotalPerms = totalPerms; UniqueTerms = uniqueTerms }
    else
        { CppExpr = exprToCpp nameMap kernelExpr; TotalPerms = 1; UniqueTerms = 1 }

/// Compact output subscript for a compound-output loop nest. The present-cell
/// axis (the IRCompoundMask binding) contributes r*trailing_stride; a trailing
/// binding contributes the dense within-cell offset. Mirrors the read-side
/// addressing in genElementBindingNew (data + r*stride, then [t]). Supports <= 1
/// trailing dim (the realistic load_compound shape); multi-trailing needs a
/// strided sum and is deferred. All-dims (no trailing) -> .data[r].
let compoundOutputSubscript (bindings: LoopIndexBinding list) (outName: string) : string =
    let isComp (b: LoopIndexBinding) = match b.Extent with IRCompoundMask _ -> true | _ -> false
    match bindings |> List.tryFind isComp with
    | None -> ""
    | Some cb ->
        match bindings |> List.filter (isComp >> not) with
        | [] -> sprintf ".data[%s]" cb.IndexName
        | [tb] -> sprintf ".data[%s * %s.trailing_stride + %s]" cb.IndexName outName tb.IndexName
        | tbs -> sprintf ".data[%s * %s.trailing_stride + %s]" cb.IndexName outName (tbs |> List.map (fun b -> b.IndexName) |> String.concat " + ")

/// --- Dense-halo carousel (sliding-window reuse) -----------------------------
/// For the INNERMOST loop level whose sole element is a dense halo window, the
/// body's simple window reads `A(w(k))` are hoisted into a span-sized set of
/// rotating scalar locals: warm-up loads before the innermost header, then one
/// shift + ONE new load at the loop tail — instead of one load per read per
/// iteration. Ordinal contiguity makes this sound: stepping the center by one
/// evicts exactly the oldest ordinal and admits exactly one new one.
/// The transform is a pure rendering substitution (reference-keyed SubstMap):
/// values are bit-identical, and the reuse structure becomes explicit in the
/// emitted C++ — the seam that pays off for expensive sources (hashed/sparse
/// maps, streamed windows, fused producers) where a re-read is not a cache hit.
/// Bails (None) whenever rotation could be unsound or names unresolvable:
/// Reynolds perm-rendering, any parallel level (omp collapse forbids code
/// between headers, and a split iteration space breaks rotation), MPI slab,
/// streamed sources, dynamic start offsets, spans > 8, or reads whose array /
/// prefix indices reference anything but captures, outer scope, or virtual
/// (range/window) params.
let private planHaloCarousel
    (streamed: Map<string, ProviderReadSpec>)
    (codeGen: LoopNestCodeGen)
    (outerNames: Map<int, string>) : (SubstMap * string list * string list) option =
    if codeGen.HasReynolds || codeGen.MpiSlab || not streamed.IsEmpty
       || codeGen.Bindings.IsEmpty
       || (codeGen.Bindings |> List.exists (fun b -> b.IsParallel)) then None
    else
    let inner = List.last codeGen.Bindings
    match inner.Elements with
    | [elem] when (match elem.SlotTag with
                   | Some t -> t.StartsWith "__halowin|d:"
                   | None -> false) ->
        // Center start offset (the warm-up's first center is `start`, since
        // the shrunk loop begins at 0). Dynamic starts bail.
        let startOpt =
            match elem.Virtual with
            | VirtualRange None -> Some 0L
            | VirtualRange (Some (IRLit (IRLitInt s))) -> Some s
            | _ -> None
        match startOpt with
        | None -> None
        | Some start ->
            let wid = elem.ParamVarId
            let wname = elem.ParamName
            // Names resolvable BEFORE emission: outer scope, captures, and
            // every level's virtual params (range windows / ordinals). Real
            // arrays' peeled names are emission-internal — reads touching
            // them bail per group.
            let prefixMap =
                let fromElems =
                    codeGen.Bindings
                    |> List.collect (fun b -> b.Elements)
                    |> List.choose (fun e ->
                        match e.Virtual with
                        | VirtualRange _ | VirtualReverse -> Some (e.ParamVarId, e.ParamName)
                        | RealArray -> None)
                let m0 = codeGen.Captures |> List.fold (fun acc c -> Map.add c.Id c.Name acc) outerNames
                fromElems |> List.fold (fun acc (k, v) -> Map.add k v acc) m0
            let rec varIdsOf (e: IRExpr) : Set<int> =
                let self = match e with IRVar (id, _) -> Set.singleton id | _ -> Set.empty
                childrenOf e |> List.fold (fun acc c -> Set.union acc (varIdsOf c)) self
            // Static signed offset of a window-read subscript: w(k) lowers to
            // Add(w, Lit k) for k >= 0 and Add(w, Neg(Lit k)) for negatives.
            let offOf = function
                | IRLit (IRLitInt k) -> Some (int k)
                | IRUnaryOp (IRNeg, IRLit (IRLitInt k)) -> Some (int -k)
                | _ -> None
            // Collect window reads by NODE REFERENCE (the SubstMap contract).
            let mutable found : (IRExpr * int * IRExpr list * int) list = []   // node, arrId, prefix, k
            let rec scan (e: IRExpr) =
                (match e with
                 | IRIndex (IRVar (aid, _), idxs, _) when not (List.isEmpty idxs) ->
                     (match List.last idxs with
                      | IRBinOp (IRElementwise, IRAdd, IRVar (vid, _), offExpr) when vid = wid ->
                          (match offOf offExpr with
                           | Some k -> found <- (e, aid, (idxs |> List.take (idxs.Length - 1)), k) :: found
                           | None -> ())
                      | _ -> ())
                 | _ -> ())
                childrenOf e |> List.iter scan
            scan codeGen.KernelExpr
            // Groups: same array + identically-rendered prefix (outer-window
            // reads etc. — invariant across the innermost run by the wid check).
            let renderable (aid: int) (prefix: IRExpr list) =
                Map.containsKey aid prefixMap
                && (prefix |> List.forall (fun p ->
                        let vs = varIdsOf p
                        not (Set.contains wid vs)
                        && vs |> Set.forall (fun v -> Map.containsKey v prefixMap)))
            let groups =
                found
                |> List.filter (fun (_, aid, prefix, _) -> renderable aid prefix)
                |> List.groupBy (fun (_, aid, prefix, _) ->
                    (aid, prefix |> List.map (exprToCppCore emptySubst prefixMap) |> String.concat "|"))
                |> List.filter (fun (_, reads) ->
                    let ks = reads |> List.map (fun (_, _, _, k) -> k) |> List.distinct
                    ks.Length >= 2 && (List.max ks - List.min ks + 1) <= 8)
            if groups.IsEmpty then None
            else
                // Ring buffer, head = the loop index itself. The window's
                // values stay STATIONARY in a pow2-capacity buffer; the loop
                // index (which already increments once per pass) locates the
                // logical start, so each iteration performs exactly ONE write
                // — the new value drops into the slot the departing value
                // vacated ((i + span) & mask) — and zero data movement.
                // Reads are buf[(i + slot) & mask]; the pow2 pad makes the
                // mod a mask (pad entries are seeded but never read live).
                let idxName = inner.IndexName
                let mutable subst : SubstMap = []
                let mutable warmup : string list = []
                let mutable tail : string list = []
                groups |> List.iteri (fun g ((aid, _), reads) ->
                    let arrS = Map.find aid prefixMap
                    let (_, _, prefix, _) = List.head reads
                    let prefixS =
                        prefix |> List.map (exprToCppCore emptySubst prefixMap >> sprintf "[%s]") |> String.concat ""
                    let ks = reads |> List.map (fun (_, _, _, k) -> k)
                    let mink = List.min ks
                    let maxk = List.max ks
                    let span = maxk - mink + 1
                    let cap = let mutable c = 1 in (while c < span do c <- c * 2); c
                    let mask = cap - 1
                    // Uniquified per nest via the output name: several halo
                    // nests can share one C++ scope (sequential lets in main).
                    let buf = sprintf "__car_%s_%d" (sanitizeCppName codeGen.OutputName) g
                    // size_t casts: the Array wrapper's operator[] takes size_t
                    // and the wrapper also converts to a raw pointer, so an
                    // int64 subscript is ambiguous — exact-match it instead.
                    let loadAt (ord: int64) = sprintf "%s%s[(size_t)%dL]" arrS prefixS ord
                    let inits =
                        [ for j in 0 .. span - 1 -> loadAt (start + int64 mink + int64 j) ]
                        @ List.replicate (cap - span) (loadAt (start + int64 mink + int64 (span - 1)))
                    warmup <- warmup @
                        [ sprintf "// halo carousel: %s window [%d..%d] — ring of %d, head = %s, one write/step" arrS mink maxk cap idxName
                          sprintf "std::array %s{ %s };" buf (String.concat ", " inits) ]
                    tail <- tail @
                        [ sprintf "%s[(%s + %dUL) & %dUL] = %s%s[(size_t)(%s + %dL)];" buf idxName span mask arrS prefixS wname (1 + maxk) ]
                    for (node, _, _, k) in reads do
                        subst <- (node, sprintf "%s[(%s + %dUL) & %dUL]" buf idxName (k - mink) mask) :: subst)
                Some (subst, warmup, tail)
    | _ -> None

let genLoopNestStreamed (streamed: Map<string, ProviderReadSpec>) (codeGen: LoopNestCodeGen) (outerNames: Map<int, string>) (indent: int) : string list =
    let ind n = String.replicate n "    "
    let mutable lines = []
    let mutable depth = indent

    // Track current peeled name for each array position
    let mutable currentNames : Map<int, string> =
        codeGen.InputArrayNames |> List.mapi (fun i n -> (i, n)) |> Map.ofList

    // Track final peeled name for each param VarId (for kernel body substitution)
    let mutable paramFinalNames : Map<int, string> = Map.empty

    // Streamed sources: accumulated ABSOLUTE site coordinates per array
    // position (consumed by the fiber read at the S/T boundary).
    let mutable streamSites : Map<int, string list> = Map.empty
    
    // Generate nested loops with element bindings
    // Nest-level OpenMP pragma (collapse for rectangular, dynamic for triangular)
    // is prepended only at the outermost level.
    //
    // OpenMP thread-coverage instrumentation (test mode only): records the set
    // of distinct OpenMP threads that actually executed the outer parallel
    // region, and prints the count afterward. This empirically answers "did the
    // runtime distribute this generated loop across multiple threads?" â€” the
    // ground-truth question, not a heuristic on pragma text. Race-free: each
    // thread writes ONLY its own slot in __omp_seen[], so no two threads touch
    // the same address. Gated behind ompTestModeEnabled() so user codegen is
    // never polluted.
    let ompInstrument = ompTestModeEnabled ()
    let outerIsParallel =
        match codeGen.Bindings with
        | outer :: _ -> outer.IsParallel
        | [] -> false
    // Unique region tag derived from the (unique) output name.
    let regionTag = codeGen.OutputName
    if ompInstrument && outerIsParallel then
        lines <- lines @ [
            ind depth + "// [omp-coverage] thread observation (test mode)"
            ind depth + "int __omp_maxth = omp_get_max_threads();"
            ind depth + "bool* __omp_seen = new bool[__omp_maxth]();"
            ind depth + "int* __omp_team = new int[__omp_maxth]();"
        ]

    let mutable atOuterLevel = true
    let lastBindingIdx = (List.length codeGen.Bindings) - 1
    let mutable bidx = 0
    let compoundArrays = compoundArrayNamesOf codeGen.Bindings
    // Dense-halo carousel plan (None when inapplicable): warm-up lines are
    // injected just BEFORE the innermost header, the rotation at the loop
    // tail, and the body renders through the reference-keyed SubstMap.
    let carousel = planHaloCarousel streamed codeGen outerNames
    for binding in codeGen.Bindings do
        // Generate the loop header (pragma only on the outermost loop).
        // Fused-fold nests accumulate into shared scalars â€” not race-safe
        // under a parallel-for â€” so the pragma is suppressed entirely
        // (an omp `reduction(...)` clause is the future upgrade path).
        let isOuter = atOuterLevel
        let pragmaPrefix =
            if isOuter && codeGen.FoldWrapper.IsNone
            then genNestPragma codeGen.Bindings (ind depth) else ""
        atOuterLevel <- false
        // MPI slab mode: the outermost level iterates this rank's slab
        // [__blade_mpi_lo_<out>, __blade_mpi_hi_<out>) â€” bounds declared by
        // the slab prologue genApplyCombinator emitted before the nest.
        // Inner levels are untouched.
        let header =
            if isOuter && codeGen.MpiSlab then
                sprintf "for (size_t %s = __blade_mpi_lo_%s; %s < __blade_mpi_hi_%s; %s++) {"
                    binding.IndexName codeGen.OutputName
                    binding.IndexName codeGen.OutputName
                    binding.IndexName
            else genForLoopHeader compoundArrays binding
        // Carousel warm-up: seed the rotating window locals for the first
        // center, in the scope just outside the innermost loop (re-seeded
        // per outer iteration in multi-level nests).
        if bidx = lastBindingIdx then
            match carousel with
            | Some (_, warmupLines, _) ->
                for w in warmupLines do lines <- lines @ [ind depth + w]
            | None -> ()
        lines <- lines @ [ind depth + pragmaPrefix + header]
        depth <- depth + 1
        // Thread-coverage marker: record this thread as seen and the team size
        // it observes. Each thread writes ONLY its own slot (race-free). Team
        // size is captured per-slot (not a single guarded write) because
        // schedule(dynamic) does not guarantee any thread runs any iteration â€”
        // taking the max over slots afterward recovers the true team size.
        //
        // Placed inside the INNERMOST loop body (after ALL loop headers), not
        // the outer body: OpenMP `collapse(d)` requires the collapsed loops to
        // be perfectly nested with no intervening code (and OMP API calls are
        // explicitly forbidden between collapsed headers). Marking in the
        // innermost body is past any collapsed prefix and always legal. The
        // marker is idempotent (each thread re-sets its own slot to the same
        // values), so running it per innermost-iteration is harmless.
        if ompInstrument && outerIsParallel && bidx = lastBindingIdx then
            lines <- lines @ [
                ind depth + "{ int __tn = omp_get_thread_num(); __omp_seen[__tn] = true; __omp_team[__tn] = omp_get_num_threads(); }"
            ]
        bidx <- bidx + 1
        
        // Generate element bindings for all arrays at this level. Zipping an
        // array WITH ITSELF puts two operand slots on the same (array, index):
        // both peel to the byte-identical declaration, so an identical
        // (name, code) pair is emitted once (the second slot's params resolve
        // to the first slot's binding via paramFinalNames below); the second
        // `double A____i0 = A[__i0];` was a g++ redeclaration error.
        let mutable declaredNames : Map<string, string> = Map.empty
        for elem in binding.Elements do
            match Map.tryFind elem.ArrayName streamed with
            | Some sspec ->
                // Streamed source: no array to peel — accumulate the site
                // coordinate; at the fiber level this emits the provider
                // read + the rank-1 wrapper the kernel body consumes.
                let acc = Map.tryFind elem.ArrayPosition streamSites |> Option.defaultValue []
                let (codeLines, fiberBound, acc') = genElementBindingStreamed binding elem sspec acc
                streamSites <- Map.add elem.ArrayPosition acc' streamSites
                for c in codeLines do
                    lines <- lines @ [ind depth + c]
                (match fiberBound with
                 | Some fname ->
                     currentNames <- Map.add elem.ArrayPosition fname currentNames
                     paramFinalNames <- Map.add elem.ParamVarId fname paramFinalNames
                 | None -> ())
            | None ->
                let currentName =
                    Map.tryFind elem.ArrayPosition currentNames
                    |> Option.defaultValue elem.ArrayName
                let (code, newName) = genElementBindingNew binding elem currentName
                if Map.tryFind newName declaredNames <> Some code then
                    lines <- lines @ [ind depth + code]
                declaredNames <- Map.add newName code declaredNames
                currentNames <- Map.add elem.ArrayPosition newName currentNames
                // Record mapping for kernel body
                match elem.Virtual with
                | VirtualRange _ | VirtualReverse ->
                    paramFinalNames <- Map.add elem.ParamVarId elem.ParamName paramFinalNames
                | RealArray ->
                    paramFinalNames <- Map.add elem.ParamVarId newName paramFinalNames
    
    // Build name map for kernel body from final peeled names
    // Start from outer scope, then overlay kernel params (kernel params take priority)
    let nameMap = paramFinalNames |> Map.fold (fun acc k v -> Map.add k v acc) outerNames
    let nameMap =
        codeGen.Captures
        |> List.fold (fun acc c -> Map.add c.Id c.Name acc) nameMap
    
    // Generate kernel assignment (with Reynolds permutation sum if applicable).
    // The shape of the assignment depends on output type:
    //   - Array output: indexed slot assignment, `name[i][j]... = kernelBody;`.
    //     Each loop binding contributes one bracketed index. This is the standard
    //     case for `method_for(...) <@> kernel` returning a tensor.
    //   - Scalar output: sum accumulation, `name += kernelBody;`. The loop nest
    //     still iterates over input dimensions, but the kernel result is summed
    //     into a scalar accumulator declared by the caller (genApplyCombinator).
    //     Used when the function's return type is scalar even though the
    //     `<@>` kernel produces a per-iteration value (the Cartesian-sum-reduce
    //     pattern that the old hand-written IIFE handled for the 2-array case).
    let outputIdx =
        match codeGen.OutputType with
        | IRTScalar _ -> ""
        // Compound output inherits the input CompoundIdx: write into the compact
        // buffer (.data[r*stride + t]), not a dense [i][j] slot.
        | ArrayElem at when isCompoundArrayType at ->
            compoundOutputSubscript codeGen.Bindings codeGen.OutputName
        | _ ->
            codeGen.Bindings
            |> List.map (fun b -> sprintf "[%s]" b.IndexName)
            |> String.concat ""
    let assignOp =
        match codeGen.OutputType with
        | IRTScalar _ -> "+="
        | _ -> "="

    let reynoldsResult =
        match carousel with
        | Some (csubst, _, _) ->
            // Carousel body: same expression, window reads substituted to the
            // rotating locals (planHaloCarousel already excluded Reynolds).
            { CppExpr = exprToCppCore csubst nameMap codeGen.KernelExpr; TotalPerms = 1; UniqueTerms = 1 }
        | None ->
            genKernelExprWithReynolds codeGen.KernelExpr codeGen.KernelParams codeGen.HasReynolds codeGen.IsAntisymmetric nameMap paramFinalNames
    if codeGen.HasReynolds && reynoldsResult.UniqueTerms < reynoldsResult.TotalPerms then
        lines <- lines @ [ind depth + sprintf "// Reynolds: %d/%d perms unique (dedup %dx)" reynoldsResult.UniqueTerms reynoldsResult.TotalPerms (reynoldsResult.TotalPerms / max 1 reynoldsResult.UniqueTerms)]
    let assignLine =
        match codeGen.FoldWrapper with
        // Fused fold: accumulate the kernel value through the fold-kernel
        // wrapper into the caller-declared scalar accumulator.
        | Some wname -> sprintf "%s = %s(%s, %s);" codeGen.OutputName wname codeGen.OutputName reynoldsResult.CppExpr
        | None -> sprintf "%s%s %s %s;" codeGen.OutputName outputIdx assignOp reynoldsResult.CppExpr
    lines <- lines @ [ind depth + assignLine]
    // Carousel rotation: shift the window by one ordinal and load the single
    // new leading value for the next center.
    match carousel with
    | Some (_, _, tailLines) ->
        for t in tailLines do lines <- lines @ [ind depth + t]
    | None -> ()

    // Close all loops
    for _ in codeGen.Bindings do
        depth <- depth - 1
        lines <- lines @ [ind depth + "}"]

    // [omp-coverage] after the nest: count distinct threads that ran the outer
    // region and print a parseable line. The harness reads "distinct=K" and the
    // available-thread count to decide pass/fail (K>1 when maxth>1 â‡’ genuinely
    // parallel; K==1 with maxth==1 â‡’ correctly serial on a 1-core environment).
    // [omp-coverage] after the nest: report the parallel team size and the
    // number of threads that actually did work. The harness uses:
    //   - teamsz > 1               â‡’ a genuine parallel region was created
    //   - maxth > 1 && teamsz == 1 â‡’ ERROR: pragma not honored (serial region)
    //   - maxth > 1 && teamsz > 1 && distinct == 1 â‡’ WARNING: region parallel
    //                                but scheduler put all work on one thread
    //                                (an allowed scheduler choice, not a bug)
    //   - maxth == 1               â‡’ single-core context, correctly serial
    if ompInstrument && outerIsParallel then
        lines <- lines @ [
            ind depth + "{ int __omp_distinct = 0; int __omp_teamsz = 0;"
            ind depth + "  for (int __t = 0; __t < __omp_maxth; __t++) {"
            ind depth + "    if (__omp_seen[__t]) __omp_distinct++;"
            ind depth + "    if (__omp_team[__t] > __omp_teamsz) __omp_teamsz = __omp_team[__t];"
            ind depth + "  }"
            ind depth + sprintf "  std::cout << \"[omp-coverage] region=%s teamsz=\" << __omp_teamsz << \" distinct=\" << __omp_distinct << \" maxth=\" << __omp_maxth << std::endl;" regionTag
            ind depth + "  delete[] __omp_seen; delete[] __omp_team; }"
        ]

    lines

/// The ordinary (no streamed sources) nest emitter — every existing call
/// site goes through here; only genApplyCombinator's provider-aware paths
/// use genLoopNestStreamed directly.
let genLoopNest (codeGen: LoopNestCodeGen) (outerNames: Map<int, string>) (indent: int) : string list =
    genLoopNestStreamed Map.empty codeGen outerNames indent


// ============================================================================
// Symmetry Vector Generation
// ============================================================================


// ============================================================================
// Array Allocation Generation
// ============================================================================


// ============================================================================
// Function Template Generation
// ============================================================================

/// Generate template parameter list for a combinator function
let genTemplateParams (inputCount: int) (hasOutput: bool) : string =
    let inputs = 
        [0 .. inputCount - 1] 
        |> List.collect (fun i -> 
            [sprintf "typename ITYPE%d" (i+1)
             sprintf "const size_t IRANK%d" (i+1)
             sprintf "const size_t* ISYM%d" (i+1)])
    let output =
        if hasOutput then
            ["typename OTYPE"; "const size_t ORANK"; "const size_t* OSYM"]
        else []
    inputs @ output |> String.concat ", "

/// Generate function parameter list
let genFunctionParams (inputNames: string list) (outputName: string) : string =
    let inputs =
        inputNames |> List.mapi (fun i name ->
            [sprintf "typename promote<ITYPE%d, IRANK%d>::type %s" (i+1) (i+1) name
             sprintf "const size_t %s_extents[IRANK%d]" name (i+1)])
        |> List.concat
    let output =
        [sprintf "typename promote<OTYPE, ORANK>::type %s" outputName
         sprintf "const size_t %s_extents[ORANK]" outputName]
    inputs @ output |> String.concat ",\n    "

// ============================================================================
// Complete Function Generation
// ============================================================================

/// Generate a complete C++ function from LoopNestCodeGen
let genFunction (codeGen: LoopNestCodeGen) (funcName: string) : string list =
    let inputCount = codeGen.InputArrayNames.Length
    
    // Template declaration
    let templateParams = genTemplateParams inputCount true
    let funcParams = genFunctionParams codeGen.InputArrayNames codeGen.OutputName
    
    // Function signature
    let signature = 
        [sprintf "template<%s>" templateParams
         sprintf "void %s(" funcName
         sprintf "    %s) {" funcParams]
    
    // Body with loop nest
    let body = genLoopNest codeGen Map.empty 1
    
    // Close
    let close = ["}"]
    
    signature @ body @ close

/// Generate header includes
let genIncludes () : string list =
    ["#include <cstdint>"
     "#include <cstdlib>"  // for rand()
     "#include <cmath>"
     "#include <complex>"
     "#include <functional>"
     "#include <tuple>"
     "#include <variant>"
     "#include <string>"
     "#include <iostream>"
     "#include <iomanip>"
     "#include <chrono>"
     "#include <algorithm>"  // std::stable_sort (used by sort())
     "#include <numeric>"    // std::iota (used by sort())
     "#include <unordered_map>"  // group_keys Case 3 (dynamic ngroups via hash discovery)
     "#include <unordered_set>"  // unique() dedup, contains() hoist (future)
     "// Note: OpenMP disabled for portability"
     (if ompTestModeEnabled () then "#include <omp.h>  // omp-coverage test-mode instrumentation" else "// #include <omp.h>")
     "#include \"nested_array_utilities.cpp\""
     "#include \"rand_runtime.hpp\""
     "#include <exception>"                 // std::exception for main()'s BL8005 catch
     "#include \"blade_runtime.hpp\""        // blade_rt::panic + BLADE_FRAME shadow stack
     "using namespace nested_array_utilities;"
     "using std::cout;"
     "using std::endl;"
     ""
     "#define TIME std::chrono::high_resolution_clock::now()"
     "#define TIME_DIFF std::chrono::duration_cast<std::chrono::nanoseconds>(end - start).count()"
     ""]

// ============================================================================
// C++ runtime headers
// ============================================================================
//
// The Blade C++ runtime lives in cpp/*.hpp at the source root and is read
// from disk at codegen time, not embedded in the F# binary as string
// literals. Blade.fsproj copies the cpp/ directory into the build output
// via <CopyToOutputDirectory>, so AppContext.BaseDirectory + "cpp" resolves
// to the correct location regardless of where dotnet run is invoked from.
//
// The generated C++ test output picks up the headers when Main.fs writes
// them into each test's output directory alongside the .cpp file (the
// existing pattern; see Main.fs's writes of headerFile / arrayTypesHeaderFile).
// g++ then resolves `#include "nested_array_utilities.hpp"` relative to
// the .cpp file's directory â€” no -I flag needed, no build-output paths
// leaked into the C++ compile line.

/// Resolve the path of a runtime header file shipped in the cpp/ directory
/// next to the compiler binary. Used by both genRuntimeHeader and
/// genRuntimeArrayTypesHeader; centralized here so the AppContext.BaseDirectory
/// and "cpp" subpath assumptions live in one place.
let private cppRuntimeHeaderPath (filename: string) : string =
    System.IO.Path.Combine(System.AppContext.BaseDirectory, "cpp", filename)

/// Read a Blade C++ runtime header from disk. Fails loudly if the build
/// hasn't copied cpp/ into the output directory â€” this is a configuration
/// error rather than a compiler bug, so the message points at .fsproj.
let private readCppRuntimeHeader (filename: string) : string =
    let path = cppRuntimeHeaderPath filename
    if not (System.IO.File.Exists path) then
        raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf
            "C++ runtime header not found at: %s\n\
             The build should copy cpp/%s into the output directory.\n\
             Check that Blade.fsproj contains a <None Include=\"cpp/%s\">\n\
             item with <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>."
            path filename filename)))
    System.IO.File.ReadAllText path

/// Generate the runtime header file content (read from cpp/nested_array_utilities.hpp).
/// Main.fs writes the result alongside each test's generated .cpp so
/// `#include "nested_array_utilities.hpp"` resolves at g++ time.
let genRuntimeHeader () : string =
    readCppRuntimeHeader "nested_array_utilities.hpp"

/// Generate the array-types runtime header (read from cpp/nested_array_types.hpp).
/// Contains the wrapper structs (Array<T,N>, Ragged<T>, RaggedRow<T>, and
/// Compound<T,RANK>) that carry shape metadata alongside the data pointer.
/// It `#include`s index_types.h (compound_index_t + the tabulated index bases),
/// which is therefore deployed next to it -- see deployRuntimeHeaders.
let genRuntimeArrayTypesHeader () : string =
    readCppRuntimeHeader "nested_array_types.hpp"

/// Read the index-types runtime header: compound_index_t plus the tabulated
/// index bases. nested_array_types.hpp `#include`s it, so it must ship next to
/// every generated .cpp (via deployRuntimeHeaders) for the include to resolve.
let genIndexTypesHeader () : string =
    readCppRuntimeHeader "index_types.h"

/// The C++ runtime header set. SINGLE SOURCE OF TRUTH: a header newly
/// depended on by the runtime is added here once (and to Blade.fsproj's copy
/// set), after which it reaches every emit site via deployRuntimeHeaders.
/// Exposed as names so callers that clean up after a compile (Cli.fs) know
/// exactly which files were deployed.
let runtimeHeaderNames : string list =
    [ "nested_array_utilities.hpp"
      "nested_array_types.hpp"
      "index_types.h"
      // Host combinadic linearize/unlinearize â€” included only by MPI-mode
      // programs (genMpiNestSimplicial), but deployed unconditionally so the
      // deploy/cleanup bookkeeping stays uniform.
      "linearized_storage.hpp"
      // `rand` module runtime (blade_rand::uniform/normal). Deployed
      // unconditionally (header-only, cheap); referenced by every program's
      // include list.
      "rand_runtime.hpp"
      // Runtime error support: blade_rt shadow call stack + panic() and the
      // BLADE_FRAME macro (Stage 6). Header-only, host-only (device passes see
      // no-op stubs); deployed unconditionally and included by every program.
      "blade_runtime.hpp" ]

/// Deploy every C++ runtime header next to a generated .cpp so its `#include`s
/// resolve at g++ time with no -I flag. These are pre-existing static files in
/// cpp/, copied verbatim -- nothing is generated or transformed.
let deployRuntimeHeaders (outputDir: string) : unit =
    runtimeHeaderNames
    |> List.iter (fun name ->
        System.IO.File.WriteAllText(System.IO.Path.Combine(outputDir, name), readCppRuntimeHeader name))

/// Generate includes that reference external header
let genIncludesExternal () : string list =
    ["#include <cstdint>"
     "#include <cstdlib>"
     "#include <cmath>"
     "#include <complex>"
     "#include <functional>"
     "#include <tuple>"
     "#include <variant>"
     "#include <string>"
     "#include <iostream>"
     "#include <iomanip>"
     "#include <chrono>"
     "#include <algorithm>"  // std::stable_sort (used by sort())
     "#include <numeric>"    // std::iota (used by sort())
     "#include <unordered_map>"  // group_keys Case 3 (dynamic ngroups via hash discovery)
     "#include <unordered_set>"  // unique() dedup, contains() hoist (future)
     "#include <omp.h>"
     "#include \"nested_array_utilities.hpp\""
     "#include \"nested_array_types.hpp\""
     "#include \"rand_runtime.hpp\""
     "#include <exception>"                 // std::exception for main()'s BL8005 catch
     "#include \"blade_runtime.hpp\""        // blade_rt::panic + BLADE_FRAME shadow stack
     "using std::cout;"
     "using std::endl;"
     ""
     "#define TIME std::chrono::high_resolution_clock::now()"
     "#define TIME_DIFF std::chrono::duration_cast<std::chrono::nanoseconds>(end - start).count()"
     ""]


// ============================================================================
// Full Program Generation
// ============================================================================

/// Generate a complete C++ program from multiple LoopNestCodeGen
let genProgram (functions: (string * LoopNestCodeGen) list) : string =
    let includes = genIncludes ()
    
    let funcCode = 
        functions 
        |> List.collect (fun (name, cg) -> genFunction cg name @ [""])
    
    (includes @ funcCode) |> String.concat "\n"

// ============================================================================
// Array Literal Generation
// ============================================================================

/// Extract float values from array literal for initialization
let rec extractLiteralValues (expr: IRExpr) : float list =
    match expr with
    | IRLit (IRLitFloat f) -> [f]
    | IRLit (IRLitInt n) -> [float n]
    | IRLit (IRLitBool b) -> [if b then 1.0 else 0.0]
    | IRUnaryOp (IRNeg, IRLit (IRLitFloat f)) -> [-f]
    | IRUnaryOp (IRNeg, IRLit (IRLitInt n)) -> [float -n]
    | IRArrayLit (elements, _) -> elements |> List.collect extractLiteralValues
    | _ -> []

/// Compute dimensions of an array literal
let rec computeArrayDims (expr: IRExpr) : int list =
    match expr with
    | IRArrayLit (elements, _) ->
        let thisLen = elements.Length
        match elements with
        | first :: _ -> thisLen :: computeArrayDims first
        | [] -> [0]
    | _ -> []

/// Compute per-row lengths for a ragged literal. Returns the inner sub-array
/// length for each outer entry. For [[1,2,3], [4,5], [6,7,8,9]] returns [3; 2; 4].
/// For non-ragged or non-nested input, returns the empty list.
let computeRaggedRowLengths (elements: IRExpr list) : int list =
    elements |> List.choose (fun e ->
        match e with
        | IRArrayLit (inner, _) -> Some inner.Length
        | _ -> None)

// ============================================================================
// Allocation Tracking (preparatory infrastructure for future garbage collection)
// ============================================================================
//
// Blade currently leaks all heap allocations on program exit (programs are
// compute-and-print-and-exit; OS reclaims memory). A future round will add
// scope-aware allocation tracking so that arrays allocated inside a function
// can be freed when the function returns (or its enclosing scope ends).
//
// To keep the door open without doing the full refactor now, new allocation
// sites should route through `emitHeapAlloc` and `emitStackAlloc` helpers
// below. These currently emit plain C++ allocation but will eventually:
//   1. Record the allocation in a per-scope arena.
//   2. Emit a `delete[]` or arena-cleanup call when the scope exits.
//   3. Distinguish "owned" from "borrowed" arrays for return-value handling.
//
// Existing allocation sites (rectangular `genArrayLiteral`, `mask`, `sort`,
// `group_keys`, etc.) currently emit `new`/`allocate<>` directly. They should
// be migrated to these helpers in a follow-up round; the current code path
// is correct (just leaky) and migration can be incremental.

/// Emit a heap allocation. Currently just emits `new T[n]`. The wrapper exists
/// so a future garbage-collection scheme can record the allocation without
/// touching every call site.
let emitHeapAlloc (cppType: string) (sizeExpr: string) : string =
    sprintf "new %s[%s]" cppType sizeExpr

/// Emit a stack-allocated array (fixed-size, lives in enclosing scope).
/// Returns the C++ declaration text. The name and size are baked into the
/// declaration; this helper exists for symmetry with emitHeapAlloc and so a
/// future scope tracker can detect stack-allocated arrays as a separate
/// category from heap arrays.
let emitStackAlloc (cppType: string) (varName: string) (sizeExpr: string) : string =
    sprintf "%s %s[%s];" cppType varName sizeExpr

/// Generate code to allocate and initialize an array from literal values
let genArrayLiteral (ctx: CodeGenContext) (varName: string) (elements: IRExpr list) (arrType: IRArrayType) : string list =
    let ind = indentStr ctx
    let elemType = elemTypeToCpp arrType.ElemType
    let rank = arrayRank arrType
    
    // Ragged literal path: detected when the array type has a RaggedIdx-tagged
    // inner index. Emits offsets table, flat backing buffer, and a row-pointer
    // table, all populated from the literal's structure.
    //
    // Storage layout (current): all stack-allocated. The flat backing buffer
    // and row-pointer table live in the enclosing function's scope. This
    // works when the ragged literal doesn't need to outlive its declaration
    // scope (e.g., construct-print-exit in main()). If a ragged literal is
    // ever returned from a function or stored into a longer-lived binding,
    // this path needs upgrading to heap allocation:
    //   - new T[total] for the flat backing
    //   - new T*[n] for the row pointers (each pointing into the flat buffer)
    // That upgrade is local to this case and doesn't affect the type system.
    // DepIdx-annotated literal path: detected when the array type has a
    // DepIdx-inner-tagged index. The lens table comes from evaluating the
    // formula stored in the inner record's Extent (e.g., `Idx<3 - i>`) for
    // each i; the literal's row data is verified against those lens. Once
    // computed, the runtime layout is identical to the ragged path
    // (`_lens`/`_offsets`/flat backing/row pointers) so all downstream
    // codegen â€” iteration, kernel-param peel, function-param calling
    // convention, print â€” uses the same machinery.
    //
    // Static-formula limitation: evalDepIdxExtent reduces arithmetic with
    // the outer's IRVar substituted for `i`. Formulas that reference free
    // variables or runtime values fall through the None case and surface as
    // a codegen error here. Runtime-extent formulas are deferred work.
    if isDepIdxArrayType arrType then
        // Find the outer record (its IRId is the one substituted for `i` in
        // the inner extent) and the inner record (carries the formula).
        let outerOpt =
            arrType.IndexTypes |> List.tryFind (fun idx -> idx.IxKind = IxKDepOuter)
        let innerOpt =
            arrType.IndexTypes |> List.tryFind (fun idx -> idx.IxKind = IxKDepInner)
        match outerOpt, innerOpt with
        | Some outer, Some inner ->
            let outerExtentOpt = tryEvalIntIR outer.Extent
            match outerExtentOpt with
            | None ->
                [sprintf "%s#error \"Blade codegen: DepIdx outer extent is not a compile-time integer for binding '%s'\""
                    ind varName]
            | Some n ->
                // Evaluate the inner formula for each i in [0..n).
                let lenResults =
                    [0 .. (int n) - 1]
                    |> List.map (fun i -> evalDepIdxExtent outer.Id i inner.Extent)
                if lenResults |> List.exists Option.isNone then
                    [sprintf "%s#error \"Blade codegen: DepIdx inner extent formula not statically evaluable for binding '%s' (runtime-extent formulas are not yet supported)\""
                        ind varName]
                else
                    let lens = lenResults |> List.map Option.get
                    // Verify literal row counts match the formula-computed lens.
                    let actualRowLengths = computeRaggedRowLengths elements
                    let mismatch =
                        actualRowLengths.Length <> lens.Length ||
                        List.zip actualRowLengths lens |> List.exists (fun (a, b) -> a <> b)
                    if mismatch then
                        let expected = lens |> List.map string |> String.concat ", "
                        let actual = actualRowLengths |> List.map string |> String.concat ", "
                        [sprintf "%s#error \"Blade codegen: DepIdx literal row lengths [%s] do not match formula-computed lens [%s] for binding '%s'\""
                            ind actual expected varName]
                    else
                        let total = lens |> List.sum
                        let allValues = extractLiteralValues (IRArrayLit (elements, arrType))
                        if allValues.Length <> total then
                            [sprintf "%s#error \"Blade codegen: DepIdx literal value count (%d) does not match sum of formula-computed lens (%d) for binding '%s'\""
                                ind allValues.Length total varName]
                        else
                            // Layout is identical to ragged from here on.
                            let nRows = lens.Length
                            let lensList = lens |> List.map string |> String.concat ", "
                            let offsets = lens |> List.scan (fun acc len -> acc + len) 0
                            let offsetsList = offsets |> List.map string |> String.concat ", "
                            // Float elements need round-trip literals (see
                            // floatToCppLiteral); integral elements keep the
                            // bare spelling (a `.0` suffix would be a C++
                            // narrowing error in the braced initializer).
                            let renderFlat (v: float) =
                                if elemType.Contains "double" || elemType.Contains "float"
                                then floatToCppLiteral v else sprintf "%g" v
                            let flatValues = allValues |> List.map renderFlat |> String.concat ", "
                            let extentsDecl = sprintf "%sstatic constexpr const size_t %s_extents[1] = {%d};" ind varName nRows
                            let lensDecl = sprintf "%sstatic constexpr const size_t %s_lens[%d] = {%s};" ind varName nRows lensList
                            let offsetsDecl = sprintf "%sstatic constexpr const size_t %s_offsets[%d] = {%s};" ind varName (nRows + 1) offsetsList
                            let flatDecl = sprintf "%s%s %s__flat[%d] = {%s};" ind elemType varName total flatValues
                            // Row pointer array (stack-allocated). The Ragged
                            // wrapper holds a pointer to this array.
                            let rowPtrsDecl = sprintf "%s%s* %s__rows[%d];" ind elemType varName nRows
                            let rowPtrsInit =
                                [ sprintf "%sfor (size_t __ri = 0; __ri < %d; __ri++) {" ind nRows
                                  // Reads from the static-constexpr global declared above; the wrapper isn't yet constructed.
                                  sprintf "%s    %s__rows[__ri] = &%s__flat[%s_offsets[__ri]];" ind varName varName varName
                                  sprintf "%s}" ind ]
                            // Wrap into Ragged<T>: data + extents + lens + offsets.
                            let wrapperDecl = sprintf "%sRagged<%s> %s = { %s__rows, %s_extents, %s_lens, %s_offsets };" 
                                                ind elemType varName varName varName varName varName
                            [extentsDecl; lensDecl; offsetsDecl; flatDecl; rowPtrsDecl] @ rowPtrsInit @ [wrapperDecl]
        | _ ->
            [sprintf "%s#error \"Blade codegen: DepIdx array type missing outer or inner record for binding '%s' (typechecker bug)\""
                ind varName]
    elif isRaggedArrayType arrType then
        let rowLengths = computeRaggedRowLengths elements
        let n = rowLengths.Length
        let total = rowLengths |> List.sum
        // Flat list of all element values, in row-major order
        let allValues = extractLiteralValues (IRArrayLit (elements, arrType))
        if allValues.Length <> total then
            // Sanity check: number of leaf values must match sum of row lengths.
            [sprintf "%s#error \"Blade codegen: ragged literal value count (%d) does not match sum of row lengths (%d) for binding '%s'\""
                ind allValues.Length total varName]
        else
            let lensList = rowLengths |> List.map string |> String.concat ", "
            let offsets =
                rowLengths |> List.scan (fun acc len -> acc + len) 0
            let offsetsList = offsets |> List.map string |> String.concat ", "
            // Same float/integral literal split as the DepIdx branch above.
            let renderFlat (v: float) =
                if elemType.Contains "double" || elemType.Contains "float"
                then floatToCppLiteral v else sprintf "%g" v
            let flatValues = allValues |> List.map renderFlat |> String.concat ", "
            let extentsDecl = sprintf "%sstatic constexpr const size_t %s_extents[1] = {%d};" ind varName n
            let lensDecl = sprintf "%sstatic constexpr const size_t %s_lens[%d] = {%s};" ind varName n lensList
            let offsetsDecl = sprintf "%sstatic constexpr const size_t %s_offsets[%d] = {%s};" ind varName (n + 1) offsetsList
            let flatDecl = sprintf "%s%s %s__flat[%d] = {%s};" ind elemType varName total flatValues
            // Row pointer array (stack-allocated, separate name from the
            // wrapper so they don't collide). The Ragged<T> wrapper bundles
            // it with the lens/offsets/extents.
            let rowPtrsDecl = sprintf "%s%s* %s__rows[%d];" ind elemType varName n
            let rowPtrsInit =
                [ sprintf "%sfor (size_t __ri = 0; __ri < %d; __ri++) {" ind n
                  // Reads from the static-constexpr `<name>_offsets` global
                  // declared just above. The Ragged wrapper itself isn't yet
                  // in scope â€” it's constructed AFTER this loop runs.
                  sprintf "%s    %s__rows[__ri] = &%s__flat[%s_offsets[__ri]];" ind varName varName varName
                  sprintf "%s}" ind ]
            let wrapperDecl = sprintf "%sRagged<%s> %s = { %s__rows, %s_extents, %s_lens, %s_offsets };" 
                                ind elemType varName varName varName varName varName
            [extentsDecl; lensDecl; offsetsDecl; flatDecl; rowPtrsDecl] @ rowPtrsInit @ [wrapperDecl]
    else
        // Rectangular path: existing behavior.
        let structuralDims = computeArrayDims (IRArrayLit (elements, arrType))
        // Rows-of-computed-arrays: when the literal's nesting is shallower
        // than the declared rank — elements are array-VALUED expressions
        // (e.g. `method_for(..) |> compute` results bound to names) rather
        // than nested bracket literals — computeArrayDims only sees the
        // bracket levels. The missing inner extents come from the array
        // type's trailing IndexTypes (the typechecker has already verified
        // each element against exactly those index types). Without this,
        // the extents table was emitted short ({2} for a rank-2 array, so
        // extents[1] read as 0) and every downstream shape consumer — the
        // auto-print loop, method_for fibers over rows — saw zero-length
        // rows: a silent miscompile (M = [], prodsum = 0).
        let dims =
            if structuralDims.Length >= rank then structuralDims
            elif arrType.IndexTypes |> List.forall (fun ix -> ix.Rank = 1) then
                let tail =
                    arrType.IndexTypes
                    |> List.skip structuralDims.Length
                    |> List.map (fun ix ->
                        match ix.Extent with
                        | IRLit (IRLitInt n) -> Some (int n)
                        | _ -> None)
                if tail |> List.forall Option.isSome
                then structuralDims @ (tail |> List.map Option.get)
                else structuralDims
            else structuralDims
        if dims.IsEmpty then
            [sprintf "%s// Empty array literal" ind]
        elif dims.Length < rank then
            // Inner extents couldn't be recovered statically (parametric or
            // compound index types). Refuse loudly rather than emit the
            // short-extents table that silently reads as empty.
            [sprintf "%s#error \"Blade codegen: array literal for '%s' nests %d level(s) but the declared rank is %d, and the missing inner extents are not static — bind the rows to a fully-literal array or annotate with static Idx<n> extents\""
                ind varName dims.Length rank]
        else
            // Generate extents declaration
            let extentsValues = dims |> List.map string |> String.concat ", "
            let extentsDecl = sprintf "%sstatic constexpr const size_t %s_extents[%d] = {%s};" 
                                ind varName rank extentsValues
            
            // Generate allocation as Array<T,N> wrapper. Single brace-init
            // bundles the data pointer (from allocate<>) with the extents
            // pointer (the static-constexpr global emitted above).
            let allocDecl =
                arrayAlloc { Ind = ind; Elem = elemType; Rank = rank; Name = varName
                             Symm = "nullptr"; Strict = None; Extents = varName + "_extents" }
            
            // Generate initialization
            let values = extractLiteralValues (IRArrayLit (elements, arrType))
            
            // Decide between fast scalar path and per-element expression path.
            // The fast path uses `%g` formatting and is correct only when all
            // elements extracted to a complete float list (length matches the
            // dim product). Struct literals, computed values, or any element
            // that extractLiteralValues returns nothing for falls into the
            // expression path, which renders via exprToCpp (handles
            // IRStructLit, IRApp, etc. uniformly).
            //
            // Pre-Phase-D, the fast path was the only path: when any element
            // wasn't a scalar literal, no init was emitted at all (silent
            // miscompile reading uninitialized memory).
            let totalExpected = dims |> List.fold (*) 1
            let useFastScalarPath = values.Length = totalExpected && totalExpected > 0
            
            // Generalize over rank N. The two paths diverge only in how each
            // leaf is rendered:
            //   - fast path: %g-formatted floats from extractLiteralValues,
            //     in row-major order
            //   - per-element path: walk the nested IRArrayLit, render each
            //     leaf via exprToCpp (covers struct lits, computed values)
            // Both produce assignments of the form `name[iâ‚€][iâ‚]...[iâ‚™â‚‹â‚] = E;`.
            //
            // Pattern follows extractLiteralValues / computeArrayDims â€” recurse
            // through IRArrayLit nesting, treating non-IRArrayLit nodes as
            // leaves. The old rank-1 / rank-2 / TODO-for-higher dispatch is
            // gone; rank-3+ now works at parity with rank-1 and rank-2.
            let rec enumerateIndexPaths (ds: int list) : int list list =
                match ds with
                | [] -> [[]]
                | n :: rest ->
                    let tails = enumerateIndexPaths rest
                    [for i in 0 .. n - 1 do
                        for t in tails do
                            yield i :: t]
            let rec walkLeaves (idxPath: int list) (e: IRExpr) : (int list * IRExpr) list =
                match e with
                | IRArrayLit (children, _) ->
                    children
                    |> List.mapi (fun i c -> walkLeaves (idxPath @ [i]) c)
                    |> List.concat
                | leaf -> [(idxPath, leaf)]
            let formatIndexPath (path: int list) : string =
                path |> List.map (sprintf "[%d]") |> String.concat ""
            
            let initCode =
                if useFastScalarPath then
                    // Row-major enumeration of (iâ‚€,â€¦,iâ‚™â‚‹â‚) tuples zipped with
                    // the flat value list. extractLiteralValues already walks
                    // in row-major order, so the alignment is exact.
                    let paths = enumerateIndexPaths dims
                    List.zip paths values |> List.map (fun (path, v) ->
                        // Round-trip literal (see floatToCppLiteral); plain
                        // assignment converts implicitly for integral
                        // element types, so no narrowing concern here.
                        sprintf "%s%s%s = %s;" ind varName (formatIndexPath path) (floatToCppLiteral v))
                else
                    // Per-element path: walk the nested IRArrayLit. Index path
                    // accumulates as we descend; leaves render via exprToCpp.
                    walkLeaves [] (IRArrayLit (elements, arrType))
                    |> List.collect (fun (path, leaf) ->
                        if path.Length >= rank then
                            [sprintf "%s%s%s = %s;" ind varName (formatIndexPath path) (exprToCpp ctx.VarNames leaf)]
                        else
                            // Array-valued leaf (a computed row): deep-copy the
                            // remaining dims elementwise. Assigning the Array
                            // wrapper into the row slot would alias the row to
                            // the source's buffer instead of copying (and for
                            // deeper rank gaps doesn't even compile).
                            //
                            // The loop bounds come from the DECLARED extents;
                            // annotation-vs-actual extent mismatches are not
                            // (yet) rejected by unify for computed arrays, so
                            // guard each copied dim at runtime — a mismatch
                            // must be a loud exit(1), not an OOB read.
                            let subDims = dims |> List.skip path.Length
                            let srcName = sprintf "__cpsrc_%s" (path |> List.map string |> String.concat "_")
                            let loopVars = subDims |> List.mapi (fun j _ -> sprintf "__cp%d" j)
                            let idxSuffix = loopVars |> List.map (sprintf "[%s]") |> String.concat ""
                            let srcDecl =
                                sprintf "%s    const auto& %s = (%s);" ind srcName (exprToCpp ctx.VarNames leaf)
                            let guards =
                                subDims |> List.mapi (fun j n ->
                                    sprintf "%s    if (%s.extents[%d] != %d) { std::cerr << \"Blade runtime: array literal row %s of '%s' has extent \" << %s.extents[%d] << \" in dim %d, but the declared type expects %d\" << std::endl; blade_rt::panic(\"BL8006\", \"array literal extent mismatch\", nullptr, 0); }"
                                        ind srcName j n (formatIndexPath path) varName srcName j j n)
                            let opens =
                                List.zip loopVars subDims
                                |> List.mapi (fun j (v, n) ->
                                    sprintf "%s    %sfor (size_t %s = 0; %s < %d; %s++) {" ind (String.replicate j "    ") v v n v)
                            let body =
                                sprintf "%s    %s%s%s%s = %s%s;"
                                    ind (String.replicate subDims.Length "    ")
                                    varName (formatIndexPath path) idxSuffix
                                    srcName idxSuffix
                            let closes =
                                [for j in subDims.Length - 1 .. -1 .. 0 -> sprintf "%s    %s}" ind (String.replicate j "    ")]
                            [sprintf "%s{" ind] @ [srcDecl] @ guards @ opens @ [body] @ closes @ [sprintf "%s}" ind])
            
            [extentsDecl; allocDecl] @ initCode

/// Generate code for a scalar binding
let genScalarBinding (ctx: CodeGenContext) (name: string) (value: IRExpr) (ty: IRType) : string list =
    let ind = indentStr ctx
    // Defense for upstream type-inference cache misses: when the binding's
    // declared type is IRTInfer or IRTUnit but the value isn't actually
    // unit-valued, re-derive the type from the value via inferExprType.
    // This catches cases where the lift pass's structFieldsCache may have
    // missed an IRFieldAccess and labeled the binding IRTUnit.
    let resolvedTy = 
        match ty with 
        | IRTInfer _ -> inferExprType value 
        | IRTUnit when not (isUnitExpr value) ->
            let inferred = inferExprType value
            if inferred = IRTUnit then ty else inferred
        | t -> t
    // A previous auto-fallback (which emitted `auto <name> = <expr>;` when
    // a binding's resolvedTy was IRTUnit and the RHS was a shape-bearing
    // expression like IRMask/IRSort/IRVar/IRFieldAccess/IRIntersect/IRUnion)
    // was removed after probe testing suggested those RHS shapes always
    // resolve to a non-IRTUnit type by this point.
    //
    // History note: that conclusion turned out to be conditional, not
    // absolute. Under parallel test execution, the codegen struct-fields
    // cache was racing with the IR-side cache, causing intermittent
    // IRTUnit results for IRFieldAccess on struct fields. The race was
    // fixed by making both caches AsyncLocal'd per task; the auto-fallback
    // is correctly absent post-fix because the cache reliably returns the
    // right type. If a future regression reaches this branch with a
    // shape-bearing IRTUnit binding, the expression-statement form below
    // will produce invalid C++ â€” that's intentional: such a regression
    // indicates a real bug in upstream resolution (cache, type
    // propagation, etc.) and should be diagnosed there rather than
    // papered over with auto-deduction here.
    match resolvedTy with
    | IRTUnit ->
        if isUnitExpr value then 
            []
        else
            // Genuinely unit-valued: emit as expression statement
            let valueStr = exprToCppCtx ctx value
            [sprintf "%s%s;" ind valueStr]
    | _ ->
        // Array-typed bindings render as Array<T,N> / Ragged<T> wrappers
        // when the RHS itself produces a wrapper. For RHSes that produce
        // bare pointers (IRIndex peeling a sub-array, IRApp where the
        // callee returns T*), render bare to avoid a brace-init mismatch.
        //
        // Producers that yield wrappers: IRFieldAccess, IRArrayLit (via
        // genArrayLiteral, but those go through a different binding
        // path), IRVar that resolves to a wrapper,
        // IRMask/IRSort/IRIntersect/IRUnion (via materializeInlineForm).
        // IRApp is also included â€” function calls returning IRTArray emit
        // `Array<T, N>` at the function-decl level (genFuncDef), so
        // their let-bound results must use the same wrapper type, not the
        // raw `promote<T, N>::type` storage pointer that would lose
        // `.extents` and silently decay to the data pointer.
        // Rank-1 ragged-family SUB-VIEW binding: `let row = r(i)` on a ragged
        // (or DepIdx-allocated) parent, or `let g0 = grouped(i)` on a group_by
        // result. Previously these bound as a raw T* (RaggedRow's decay
        // operator / the grouped row pointer), losing the row LENGTH -- so
        // every downstream length-dependent op (reduce, extents, print)
        // emitted accessors a bare pointer doesn't have. Bind as
        // RaggedRow<T> instead:
        //   * ragged/DepIdx parent: Ragged<T>::operator[] already RETURNS
        //     RaggedRow{ptr,len}, so only the declared type changes.
        //   * grouped parent: the Array<T*,1> value has no lens member; the
        //     length comes from the group_keys offsets table (looked up via
        //     ctx.GroupedArrays), same source the peel path uses.
        let raggedRowSubview =
            match value, resolvedTy with
            | IRIndex (parent, [idxExpr], _), ArrayElem rowTy when isRaggedRowType rowTy ->
                let valueStr = exprToCppCtx ctx value
                let elemStr = elemTypeToCpp rowTy.ElemType
                let isGroupRow = rowTy.IndexTypes.[0].IxKind = IxKGroupMember
                if isGroupRow then
                    let parentName = exprToCppCtx ctx parent
                    match Map.tryFind parentName ctx.GroupedArrays with
                    | Some gkName ->
                        let idxStr = exprToCppCtx ctx idxExpr
                        Some [sprintf "%sRaggedRow<%s> %s = { %s, %s__offsets[(%s) + 1] - %s__offsets[%s] };"
                                  ind elemStr name valueStr gkName idxStr gkName idxStr]
                    | None -> None  // grouped parent not registered (non-var producer): fall through to raw
                else
                    Some [sprintf "%sRaggedRow<%s> %s = %s;" ind elemStr name valueStr]
            | _ -> None
        if raggedRowSubview.IsSome then raggedRowSubview.Value
        else
        // Plain dense PARTIAL positional read: `let r0 = A(i)` supplies FEWER
        // subscripts than the array's rank, so the result is a row/slab sub-view
        // (residual rank >= 1), not a scalar. The raw path below would bind it as
        // `promote<T, R>::type` -- a bare data pointer that has lost `.extents`,
        // so every downstream `.extents` consumer (auto-print, method_for/zip)
        // fails to compile. Bind the Array<T, R> wrapper directly, mirroring the
        // loop-peel slice idiom (genElementBindingNew): the data pointer steps
        // through the consumed leading dims and the extents pointer shifts past
        // them, e.g. `Array<double,1> r0 = { A.data[0L], A.extents + 1 };`.
        //
        // Scoped to fully plain-dense rectangular arrays (every axis IxKPlain /
        // SymNone / SDimension / arity-1) so the consumed-dims count equals the
        // subscript count and the extents shift is exact. Compound partial reads
        // take the IRTuple arm in producesWrapper; ragged/dep-idx rows take
        // raggedRowSubview above; and a flat single-subscript into PACKED
        // symmetric storage returns a row pointer under compact semantics that
        // must NOT be re-wrapped here -- all excluded by the axis predicate.
        let densePartialSubview =
            match value, resolvedTy with
            | IRIndex (arr, indices, _), ArrayElem residTy
                    when not (List.isEmpty indices)
                         && indices |> List.forall (function IRTuple _ -> false | _ -> true) ->
                match inferExprType arr with
                | ArrayElem arrTy
                        when arrTy.IndexTypes.Length > indices.Length
                             && arrTy.IndexTypes |> List.forall (fun ix ->
                                    ix.IxKind = IxKPlain && ix.Symmetry = SymNone
                                    && ix.Kind = SDimension && ix.Rank = 1) ->
                    let arrStr = exprToCppCtx ctx arr
                    let subscripts =
                        indices
                        |> List.map (fun i -> sprintf "[%s]" (exprToCppCtx ctx i))
                        |> String.concat ""
                    Some [sprintf "%s%s %s = { %s.data%s, %s.extents + %d };"
                              ind (cppArrayTypeStr residTy) name arrStr subscripts arrStr indices.Length]
                | _ -> None
            | _ -> None
        if densePartialSubview.IsSome then densePartialSubview.Value
        else
        let producesWrapper =
            match value with
            | IRFieldAccess _ -> true
            | IRVar _ -> true                // assume wrapper (most producers migrated)
            | IRMask _ | IRSort _ | IRIntersect _ | IRUnion _ | IRUnique _ -> true
            | IRApp _ -> true                // function-call returns wrapped Array
            | IRTupleProj _ -> true          // tuple elements carry wrappers (irTypeToCpp IRTTuple)
            | IRIndex (a, (IRTuple coords) :: _, _) ->
                // A PARTIAL compound read (formalism 4.5) produces a wrapper:
                // Compound<T, RR> for a residual-rank >= 2 read
                // (make_partial_compound[_gather]) or Array<T, 1> for a dense
                // rank-1 residual (make_partial_window / _gather_dense). A
                // FULL compound read stays on the raw path (scalar, or a
                // trailing-row T* sub-view via .row()), as does ordinary
                // positional peeling on non-compound arrays.
                (match inferExprType a with
                 | ArrayElem at when isCompoundArrayType at ->
                     let k =
                         at.IndexTypes
                         |> List.tryFind (fun ix -> ix.IxKind = IxKCompound)
                         |> Option.map (fun ix -> ix.Rank)
                         |> Option.defaultValue coords.Length
                     (match classifyCompoundIndexTuple k coords with
                      | CompoundPartial _ -> true
                      | CompoundFull -> false)
                 | _ -> false)
            | _ -> false
        let cppType =
            match resolvedTy with
            | ArrayElem arr when producesWrapper -> cppArrayTypeStr arr
            | _ -> irTypeToCpp resolvedTy
        let valueStr = exprToCppCtx ctx value
        [sprintf "%s%s %s = %s;" ind cppType name valueStr]

// ============================================================================
// Loop Application Code Generation
// ============================================================================

/// Build a simple (no symmetry) ApplyInfo for applying a unary kernel to arrays.
/// Used by >>@ and @>> to construct stage-2 pipeline applications.
let defaultIndexType () = { Id = 0; Rank = 1; Extent = IRLit (IRLitInt 0); Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
/// Build a default IRArrayType.
let defaultArrayType (et: IRType) = { ElemType = et; IndexTypes = [defaultIndexType ()]; IsVirtual = false; Identity = None }

let buildSimpleApplyInfo (arrays: IRExpr list) (kernel: IRExpr) (outputType: IRType) : ApplyInfo =
    let arrayTypes = arrays |> List.map (fun a -> 
        match inferExprType a with 
        | ArrayElem arr -> arr 
        | _ -> defaultArrayType (IRTScalar ETFloat64))
    let identities = arrays |> List.mapi (fun i _ -> AIDLiteral i)
    let sDims = arrayTypes |> List.map arrayRank
    let totalSDims = List.sum sDims
    {
        Loop = IRMethodFor {
            Arrays = arrays
            Identities = identities
            ArrayTypes = arrayTypes
            SDimsPerArray = sDims
            TotalSDims = totalSDims
            SharedIndexType = None
        }
        Kernel = kernel
        Arrays = arrays
        Identities = identities
        ArrayTypes = arrayTypes
        SharedIndexType = None
        SymcomStates = List.replicate totalSDims SCNeither
        TriangularLevels = List.replicate totalSDims false
        SDimsPerArray = sDims
        KernelInputRanks = List.replicate (List.length arrays) 0
        KernelOutputRank = 0
        KernelTDims = []
        SpeedupFactor = 1L
        ReynoldsSpeedup = 1L
        HasReynolds = false
        OutputType = outputType
        IsCoIteration = false
    }

/// Emit a CUDA kernel for any single S-dimension symmetry group of arity >= 2,
/// symmetric (inclusive simplex i0<=i1<=...) or antisymmetric (strict simplex
/// i0<i1<...). This is the GENERAL simplicial-unrank kernel: it replaces the
/// former per-rank closed-form kernels (Sym2/Anti2/Sym3/Anti3) with one path
/// driven by the group's arity R and symmetry.
///
/// Per Section 9.2/10.8 of the formalism, S-dims ARE the iteration structure:
/// they are stored compactly and flattened to a contiguous pool to cross the
/// extern "C" device boundary, and the flat thread id unranks to the canonical
/// S-tuple. (T-dims, by contrast, live inside the kernel as per-thread slices and
/// do NOT participate in this addressing â€” handled separately, not here. This
/// path requires a scalar-leaf, all-S, single-group output: T-rank 0.)
///
/// The device unrank is the combinatorial number system (proven against
/// lexicographic tuple order, and in exact emitted form, for R=2..5, both
/// variants, in the sandbox):
///   strict = (Symmetry = SymAntisymmetric)
///   neff   = strict ? N : N + R - 1          (inclusive maps to strict over N+R-1)
///   card   = C(neff, R)
///   unrank(t): x=0; rem=t
///     for pos in 0..R-1:
///       after = R - pos - 1
///       v = x; while (rem >= C(neff-1-v, after)) { rem -= C(...); v++ }
///       idx[pos] = strict ? v : (v - pos)     (de-shift for inclusive)
///       x = v + 1
/// A single __device__ binomial helper __blade_binom is emitted once per .cu.
///
/// Fold: symmetric is a raw comm kernel (hasReynolds=false); antisymmetric IS the
/// Reynolds antisymmetrization (hasReynolds=true, isAntisym=true).
/// Storage: symmetric -> allocate<T, SYMM={1..1}>; antisymmetric ->
/// allocate_strict<T, SYMM={1..1}, STRICT={1..1}>.
/// `mpiRange` (the `where mpi, cuda(...)` hybrid, MixedParallelismPlan.md
/// phase 3): the launch is RANK-SCOPED — the kernel gains [lo, hi) flat
/// cell-range parameters (thread t computes absolute cell lo + t), the
/// wrapper selects the rank's device (rank % deviceCount), launches
/// ceil((hi-lo)/block) blocks and copies back only the [lo, hi) slice; the
/// host side emits the same balanced cell-range split as the MPI host path
/// and restores the full pool with the standard cell-range Allgatherv. The
/// wrapper is dllexport'd: the hybrid build compiles the .cu into a
/// self-contained MSVC DLL (nvcc -shared) that the g++/-lmsmpi host links
/// directly (the netcdf.dll trick), avoiding any cross-ABI object link.
/// With mpiRange = false the emission is byte-identical to before.
let genCudaKernelSimplicial (mpiRange: bool) (softSplit: bool) (codeGen: LoopNestCodeGen) (name: string) (blockSize: int) : string list option =
    // Detect a single S-dim symmetry group of arity >= 2 (sym or antisym).
    let grpOpt =
        match codeGen.OutputType with
        | ArrayElem arr ->
            match arr.IndexTypes with
            | [ix] when (max 1 ix.Rank) >= 2
                        && (ix.Symmetry = SymSymmetric || ix.Symmetry = SymAntisymmetric)
                        && ix.Kind = SDimension
                        && isCudaBoundarySafeElem arr.ElemType ->
                Some (arr.ElemType, ix.Extent, (max 1 ix.Rank), ix.Symmetry)
            | _ -> None
        | _ -> None
    if grpOpt.IsNone then None
    else
    let (outElemTy, extentExpr, grpRank, sym) = grpOpt.Value
    let strict = (sym = SymAntisymmetric)
    // Antisym requires the Reynolds antisymmetrization; symmetric is a raw comm.
    if strict && not (codeGen.HasReynolds && codeGen.IsAntisymmetric) then None
    elif (not strict) && codeGen.IsAntisymmetric then None
    else
    let nOpt = match extentExpr with IRLit (IRLitInt n) -> Some n | _ -> None
    if nOpt.IsNone then None
    else
    let n = nOpt.Value
    if codeGen.InputArrayNames.IsEmpty then None
    else
    let bindings = codeGen.Bindings
    if List.length bindings < grpRank then None
    else
    let srcName = match codeGen.InputArrayNames with n0 :: _ -> n0 | [] -> ""
    if srcName = "" then None
    else
    let elemCpp = elemTypeToCpp outElemTy
    let r = int grpRank
    // card = C(neff, R), neff = strict ? N : N+R-1
    let neff = if strict then n else n + int64 r - 1L
    let binom (m: int64) (k: int) : int64 =
        if k < 0 || m < int64 k then 0L
        else
            let mutable num = 1L
            let mutable den = 1L
            for i in 0 .. k - 1 do
                num <- num * (m - int64 i)
                den <- den * int64 (i + 1)
            num / den
    let card = binom neff r
    let kernelName = sprintf "__cuda_%s" (sanitizeCppName name)
    let launchName = sprintf "__launch_%s" (sanitizeCppName name)
    // Per-level index variables idx[0..r-1] -> device names.
    let idxVarOf pos = sprintf "__blade_idx_%d" pos
    // Operand reads keyed by elem.ParamVarId (the var-id the kernel body uses),
    // each binding level reading at its unranked index.
    let mutable paramFinalNames : Map<IRId, string> = Map.empty
    let readBinds =
        [ for b in bindings do
            let idxVar = idxVarOf b.Level
            // A FUSED joint level (arc 1) carries one element per source dim,
            // all sharing (Level, ArrayPosition) and the same ParamVarId. On
            // the DEVICE the operand is the flat pool, where the compound
            // index IS the row-major position â€” a single flat read serves the
            // whole fused block (no per-dim decode needed, unlike the host
            // peel chain). Dedup so the read variable is declared once
            // (duplicate declarations were an nvcc redefinition error).
            for elem in b.Elements |> List.distinctBy (fun e -> e.ArrayPosition) do
                let readName = sprintf "__blade_op_%d_%d" b.Level elem.ArrayPosition
                let etStr = elemTypeToCpp elem.ArrayElemType
                paramFinalNames <- Map.add elem.ParamVarId readName paramFinalNames
                yield sprintf "    %s %s = %s[%s];" etStr readName srcName idxVar ]
    let nameMap =
        codeGen.Captures |> List.fold (fun acc c -> Map.add c.Id c.Name acc) paramFinalNames
    // Antisym: Reynolds fold (true,true) emits the signed antisymmetrization.
    // Symmetric: raw comm kernel (false,false).
    let reynolds =
        genKernelExprWithReynolds codeGen.KernelExpr codeGen.KernelParams strict strict nameMap paramFinalNames
    // Device combinadic unrank loop. Emits idx_0..idx_{r-1} as absolute indices.
    //
    // Per level, the count of cells whose value is < v has the closed form
    //   cum(v) = C(neff-x, after+1) - C(neff-v, after+1)
    // (hockey-stick identity over C(neff-1-q, after) for q in [x, v); verified in
    // the sandbox against lexicographic order for r=2..5, both variants). Because
    // cum is monotincreasing in v, each level brackets its value by BINARY SEARCH
    // in O(log n) rather than the O(n) linear scan â€” restoring the cost the former
    // closed-form rank-2/3 kernels had, now generalized to arbitrary rank. Total
    // per-thread cost O(r log n).
    //
    // FUTURE O(1) OPTION (deferred until timing tests exist): the unrank has no
    // constant-time closed form â€” inverting the combinatorial number system is
    // fundamentally a search. To approach O(1) per thread, precompute the
    // card x r table of canonical tuples ONCE (the MethodLoop's S-structure is
    // fixed and reused across kernel applications), store it flat in device
    // memory, and have each thread load idx[pos] = table[t*r + pos] (one coalesced
    // read). This trades O(r log n) arithmetic for a memory gather + one-time table
    // build, amortized over repeated applications. Whether it actually beats the
    // arithmetic depends on r, n, reuse count, and memory-vs-compute balance on the
    // target GPU, so it should be chosen by BENCHMARK, not assumed â€” GPU arithmetic
    // is often nearly free relative to bandwidth, so the table is not obviously
    // faster despite being "O(1)".
    let unrank =
        [ "    size_t __blade_t = __blade_i;"
          sprintf "    long __blade_neff = %dL;" neff
          "    long __blade_x = 0;"
          "    long long __blade_rem = (long long)__blade_t;" ]
        @ [ for pos in 0 .. r - 1 do
              let after = r - pos - 1
              // binary search largest v in [x, neff] with cum(v) <= rem
              yield sprintf "    long __blade_lo_%d = __blade_x; long __blade_hi_%d = __blade_neff; long __blade_vf_%d = __blade_x;" pos pos pos
              yield sprintf "    long long __blade_base_%d = __blade_binom(__blade_neff - __blade_x, %d);" pos (after + 1)
              yield  "    while (true) {"
              yield sprintf "        if (__blade_lo_%d > __blade_hi_%d) break;" pos pos
              yield sprintf "        long __blade_mid_%d = (__blade_lo_%d + __blade_hi_%d) / 2;" pos pos pos
              yield sprintf "        long long __blade_cum_%d = __blade_base_%d - __blade_binom(__blade_neff - __blade_mid_%d, %d);" pos pos pos (after + 1)
              yield sprintf "        if (__blade_cum_%d <= __blade_rem) { __blade_vf_%d = __blade_mid_%d; __blade_lo_%d = __blade_mid_%d + 1; }" pos pos pos pos pos
              yield sprintf "        else { __blade_hi_%d = __blade_mid_%d - 1; }" pos pos
              yield  "    }"
              yield sprintf "    long long __blade_cumf_%d = __blade_base_%d - __blade_binom(__blade_neff - __blade_vf_%d, %d);" pos pos pos (after + 1)
              yield sprintf "    __blade_rem -= __blade_cumf_%d;" pos
              // strict -> absolute v ; inclusive -> v - pos (de-shift)
              if strict then
                  yield sprintf "    size_t %s = (size_t)__blade_vf_%d;" (idxVarOf pos) pos
              else
                  yield sprintf "    size_t %s = (size_t)(__blade_vf_%d - %d);" (idxVarOf pos) pos pos
              yield sprintf "    __blade_x = __blade_vf_%d + 1;" pos ]
    let kernelParams = sprintf "const %s* %s" elemCpp srcName
    let kernelDef =
        if mpiRange then
            // Rank-scoped: thread t computes ABSOLUTE cell lo + t for
            // t in [0, hi - lo); the unrank below consumes __blade_i.
            [ sprintf "__global__ void %s(%s, %s* __blade_out, size_t __blade_rlo, size_t __blade_rhi) {" kernelName kernelParams elemCpp
              "    size_t __blade_tid = (size_t)blockIdx.x * blockDim.x + threadIdx.x;"
              "    if (__blade_tid >= __blade_rhi - __blade_rlo) return;"
              "    size_t __blade_i = __blade_rlo + __blade_tid;" ]
            @ unrank @ readBinds
            @ [ sprintf "    __blade_out[__blade_i] = %s;" reynolds.CppExpr; "}" ]
        else
            [ sprintf "__global__ void %s(%s, %s* __blade_out, size_t __blade_card) {" kernelName kernelParams elemCpp
              "    size_t __blade_i = (size_t)blockIdx.x * blockDim.x + threadIdx.x;"
              "    if (__blade_i >= __blade_card) return;" ]
            @ unrank @ readBinds
            @ [ sprintf "    __blade_out[__blade_i] = %s;" reynolds.CppExpr; "}" ]
    let wrapper =
        if mpiRange then
            // dllexport'd: the hybrid build ships the .cu as a self-contained
            // MSVC DLL the g++ host links directly.
            [ sprintf "extern \"C\" __declspec(dllexport) void %s(const %s* %s, %s* __blade_host_out, size_t __blade_rlo, size_t __blade_rhi, int __blade_rank) {" launchName elemCpp srcName elemCpp
              "    int __blade_dc = 1; cudaGetDeviceCount(&__blade_dc); if (__blade_dc < 1) __blade_dc = 1;"
              "    cudaSetDevice(__blade_rank % __blade_dc);"
              "    if (__blade_rhi <= __blade_rlo) return;"
              sprintf "    size_t __blade_card = %dUL;" card
              sprintf "    %s* __blade_d_%s; cudaMalloc(&__blade_d_%s, %dUL * sizeof(%s));" elemCpp srcName srcName n elemCpp
              sprintf "    cudaMemcpy(__blade_d_%s, %s, %dUL * sizeof(%s), cudaMemcpyHostToDevice);" srcName srcName n elemCpp
              sprintf "    %s* __blade_d_out; cudaMalloc(&__blade_d_out, __blade_card * sizeof(%s));" elemCpp elemCpp
              sprintf "    size_t __blade_blocks = ((__blade_rhi - __blade_rlo) + %dUL - 1UL) / %dUL;" blockSize blockSize
              sprintf "    %s<<<(unsigned)__blade_blocks, %d>>>(__blade_d_%s, __blade_d_out, __blade_rlo, __blade_rhi);" kernelName blockSize srcName
              "    cudaDeviceSynchronize();"
              sprintf "    cudaMemcpy(__blade_host_out + __blade_rlo, __blade_d_out + __blade_rlo, (__blade_rhi - __blade_rlo) * sizeof(%s), cudaMemcpyDeviceToHost);" elemCpp
              sprintf "    cudaFree(__blade_d_%s);" srcName
              "    cudaFree(__blade_d_out);"; "}" ]
        elif softSplit then
            // <&> soft-join split wrappers (see genCudaKernel's softSplit arm):
            // begin = H2D + ASYNC launch on a round-robin device, end = sync +
            // D2H + free. Device selection lives HERE (the g++ host half never
            // touches the CUDA API); one device => default-stream serialization.
            let sdPrefix = sprintf "__blade_sd_%s" (sanitizeCppName name)
            [ sprintf "static %s* %s_d_src = nullptr;" elemCpp sdPrefix
              sprintf "static %s* %s_d_out = nullptr;" elemCpp sdPrefix
              sprintf "static int %s_dev = 0;" sdPrefix
              sprintf "extern \"C\" void %s_begin(const %s* %s, int __blade_leaf) {" launchName elemCpp srcName
              "    int __blade_dc = 1; cudaGetDeviceCount(&__blade_dc); if (__blade_dc < 1) __blade_dc = 1;"
              sprintf "    %s_dev = __blade_leaf %% __blade_dc;" sdPrefix
              sprintf "    cudaSetDevice(%s_dev);" sdPrefix
              sprintf "    size_t __blade_card = %dUL;" card
              sprintf "    cudaMalloc(&%s_d_src, %dUL * sizeof(%s));" sdPrefix n elemCpp
              sprintf "    cudaMemcpy(%s_d_src, %s, %dUL * sizeof(%s), cudaMemcpyHostToDevice);" sdPrefix srcName n elemCpp
              sprintf "    cudaMalloc(&%s_d_out, __blade_card * sizeof(%s));" sdPrefix elemCpp
              sprintf "    size_t __blade_blocks = (__blade_card + %dUL - 1UL) / %dUL;" blockSize blockSize
              sprintf "    %s<<<(unsigned)__blade_blocks, %d>>>(%s_d_src, %s_d_out, __blade_card);" kernelName blockSize sdPrefix sdPrefix
              "}"
              sprintf "extern \"C\" void %s_end(%s* __blade_host_out) {" launchName elemCpp
              sprintf "    cudaSetDevice(%s_dev);" sdPrefix
              "    cudaDeviceSynchronize();"
              sprintf "    cudaMemcpy(__blade_host_out, %s_d_out, %dUL * sizeof(%s), cudaMemcpyDeviceToHost);" sdPrefix card elemCpp
              sprintf "    cudaFree(%s_d_src);" sdPrefix
              sprintf "    cudaFree(%s_d_out);" sdPrefix
              "    cudaSetDevice(0);"; "}" ]
        else
            [ sprintf "extern \"C\" void %s(const %s* %s, %s* __blade_host_out) {" launchName elemCpp srcName elemCpp
              sprintf "    size_t __blade_card = %dUL;" card
              sprintf "    %s* __blade_d_%s; cudaMalloc(&__blade_d_%s, %dUL * sizeof(%s));" elemCpp srcName srcName n elemCpp
              sprintf "    cudaMemcpy(__blade_d_%s, %s, %dUL * sizeof(%s), cudaMemcpyHostToDevice);" srcName srcName n elemCpp
              sprintf "    %s* __blade_d_out; cudaMalloc(&__blade_d_out, __blade_card * sizeof(%s));" elemCpp elemCpp
              sprintf "    size_t __blade_blocks = (__blade_card + %dUL - 1UL) / %dUL;" blockSize blockSize
              sprintf "    %s<<<(unsigned)__blade_blocks, %d>>>(__blade_d_%s, __blade_d_out, __blade_card);" kernelName blockSize srcName
              "    cudaDeviceSynchronize();"
              sprintf "    cudaMemcpy(__blade_host_out, __blade_d_out, __blade_card * sizeof(%s), cudaMemcpyDeviceToHost);" elemCpp
              sprintf "    cudaFree(__blade_d_%s);" srcName
              "    cudaFree(__blade_d_out);"; "}" ]
    // Emit the __device__ binomial helper once per .cu. Idempotency is keyed on
    // the cell's own contents (race-safe: cudaKernelDefsCell is AsyncLocal, so each
    // program-assembly flow has its own cell, reset per program) rather than a
    // module-level mutable, which would not reset between programs and would race
    // under the parallel test runner.
    let cell = cudaKernelDefsCell ()
    let helperMarker = "__device__ static long long __blade_binom"
    let binomHelper =
        if cell.Value |> List.exists (fun l -> l.StartsWith helperMarker) then []
        else
            [ "__device__ static long long __blade_binom(long m, long k) {"
              "    if (k < 0 || m < (long)k) return 0;"
              "    if (k == 0) return 1;"
              "    long long num = 1; long long den = 1;"
              "    for (long i = 0; i < k; i++) { num *= (m - i); den *= (i + 1); }"
              "    return num / den;"
              "}"
              "" ]
    cell.Value <- cell.Value @ (binomHelper @ kernelDef @ [""] @ wrapper @ [""])
    // Host-side inline allocation matching the host storage:
    //   symmetric  -> allocate<T, SYMM={1..1}>
    //   antisym    -> allocate_strict<T, SYMM={1..1}, STRICT={1..1}>
    let extentsName = sprintf "%s_extents" name
    let ones = List.replicate r 1
    let symmArg = hoistSymmDecl (sprintf "%s_symm" name) ones
    let extentDecls =
        [ sprintf "    size_t* %s = new size_t[%d];" extentsName r ]
        @ [ for d in 0 .. r - 1 -> sprintf "    %s[%d] = %dUL;" extentsName d n ]
    let allocLine =
        if strict then
            let strictArg = hoistSymmDecl (sprintf "%s_strict" name) ones
            arrayAlloc { Ind = "    "; Elem = elemCpp; Rank = r; Name = name
                         Symm = symmArg; Strict = Some strictArg; Extents = extentsName }
        else
            arrayAlloc { Ind = "    "; Elem = elemCpp; Rank = r; Name = name
                         Symm = symmArg; Strict = None; Extents = extentsName }
    if mpiRange then
        // Rank-scoped launch: balanced flat cell-range split (the same
        // q/rem the MPI host path uses), per-rank device launch over
        // [lo, hi), then cell-range Allgatherv restores the full pool.
        let mpiDtype =
            match outElemTy with
            | AnyPrimElem et -> mpiDatatypeOf et
            | _ -> None
        match mpiDtype with
        | None -> None  // no MPI datatype => not hybrid-eligible
        | Some dtype ->
            let split =
                [ sprintf "    size_t __blade_mpi_n_%s = %dUL;" name card
                  sprintf "    size_t __blade_mpi_q_%s = __blade_mpi_n_%s / (size_t)__blade_mpi_size;" name name
                  sprintf "    size_t __blade_mpi_r_%s = __blade_mpi_n_%s %% (size_t)__blade_mpi_size;" name name
                  sprintf "    size_t __blade_mpi_lo_%s = (size_t)__blade_mpi_rank * __blade_mpi_q_%s + ((size_t)__blade_mpi_rank < __blade_mpi_r_%s ? (size_t)__blade_mpi_rank : __blade_mpi_r_%s);" name name name name
                  sprintf "    size_t __blade_mpi_hi_%s = __blade_mpi_lo_%s + __blade_mpi_q_%s + ((size_t)__blade_mpi_rank < __blade_mpi_r_%s ? 1 : 0);" name name name name ]
            let launch =
                // Ranks launch concurrently: the sections are independent
                // (each writes its own [lo, hi) slice; the CUDA driver
                // time-slices a shared device) and the Allgatherv below is
                // the only cross-rank dependency. (Bring-up used a token-
                // ring serialization here; removed once the differential
                // passed, and re-verified without it.)
                [ sprintf "    %s(pool_base(%s.data), pool_base(%s.data), __blade_mpi_lo_%s, __blade_mpi_hi_%s, __blade_mpi_rank);" launchName srcName name name name ]
            let gather =
                [ sprintf "    { // MPI: restore full %s on all ranks (device ranges)" name
                  sprintf "        if (__blade_mpi_n_%s > 2147483647ULL) { std::cerr << \"error[BL8004]: element count exceeds int32 range (rank \" << __blade_mpi_rank << \")\" << std::endl; MPI_Abort(MPI_COMM_WORLD, 13); }" name
                  "        int* __blade_mpi_counts = new int[__blade_mpi_size];"
                  "        int* __blade_mpi_displs = new int[__blade_mpi_size];"
                  "        for (int __r = 0; __r < __blade_mpi_size; __r++) {"
                  sprintf "            size_t __lo = (size_t)__r * __blade_mpi_q_%s + ((size_t)__r < __blade_mpi_r_%s ? (size_t)__r : __blade_mpi_r_%s);" name name name
                  sprintf "            size_t __hi = __lo + __blade_mpi_q_%s + ((size_t)__r < __blade_mpi_r_%s ? 1 : 0);" name name
                  "            __blade_mpi_counts[__r] = (int)(__hi - __lo);"
                  "            __blade_mpi_displs[__r] = (int)__lo;"
                  "        }"
                  sprintf "        MPI_Allgatherv(MPI_IN_PLACE, 0, MPI_DATATYPE_NULL, pool_base(%s.data), __blade_mpi_counts, __blade_mpi_displs, %s, MPI_COMM_WORLD);" name dtype
                  "        delete[] __blade_mpi_counts; delete[] __blade_mpi_displs;"
                  "    }" ]
            Some (extentDecls @ [allocLine] @ split @ launch @ gather)
    else
    let inlineLines =
        extentDecls
        @ [ allocLine ]
        // Soft-join caller sequences the begin/end calls itself; return the
        // host output allocation only.
        @ (if softSplit then []
           else [ sprintf "    %s(pool_base(%s.data), pool_base(%s.data));" launchName srcName name ])
    Some inlineLines

/// MPI flat-cell-range decomposition of a single-S-group symmetric or
/// antisymmetric nest (the triangular sibling of the dense MpiSlab path).
/// The packed pool of C(n+r-1, r) (sym) / C(n, r) (antisym) cells is split
/// into balanced contiguous ranges [lo, hi) per rank; each rank unranks its
/// cells back to canonical coordinates via linearized_storage's host
/// unlinearize (O(r log n) bisection â€” the same combinadics the CUDA
/// simplicial kernel does on device; those passing differentials pin that
/// linearized_storage's canonical order == allocate<>'s pool DFS order), and
/// the Allgatherv over cell ranges restores the full pool on all ranks.
/// Same shape gates as genCudaKernelSimplicial (with the elem gate swapped
/// for the MPI-datatype requirement); None = out of scope, the caller
/// decides whether that is an error. `innerOmp` (the `where mpi, omp(...)`
/// hybrid) threads each rank's flat cell-range: a bare `parallel for` on
/// the cell loop — each cell unranks and writes independently, so the
/// pragma is race-free by construction (no collapse/schedule decision to
/// make on a single flat loop).
let genMpiNestSimplicial (innerOmp: bool) (codeGen: LoopNestCodeGen) (name: string) : string list option =
    // Detect a single S-dim symmetry group of arity >= 2 (sym or antisym).
    let grpOpt =
        match codeGen.OutputType with
        | ArrayElem arr ->
            match arr.IndexTypes with
            | [ix] when (max 1 ix.Rank) >= 2
                        && (ix.Symmetry = SymSymmetric || ix.Symmetry = SymAntisymmetric)
                        && ix.Kind = SDimension ->
                match arr.ElemType with
                | AnyPrimElem et when (mpiDatatypeOf et).IsSome ->
                    Some (arr.ElemType, (mpiDatatypeOf et).Value, ix.Extent, (max 1 ix.Rank), ix.Symmetry)
                | _ -> None
            | _ -> None
        | _ -> None
    match grpOpt with
    | None -> None
    | Some (outElemTy, mpiDtype, extentExpr, grpRank, sym) ->
    let strict = (sym = SymAntisymmetric)
    // Antisym requires the Reynolds antisymmetrization; symmetric is a raw comm.
    if strict && not (codeGen.HasReynolds && codeGen.IsAntisymmetric) then None
    elif (not strict) && codeGen.IsAntisymmetric then None
    else
    // Every peel must be a SIMPLE one: one leading-dim level per array
    // position (RankComponent 0, DimIndex 0). A packed-symmetric INPUT
    // (co-iteration) binds multiple rank components of one position to a
    // single kernel param â€” its cell read needs linearize(idx...), which
    // this emitter does not do; without this guard the per-level reads
    // would silently overwrite each other. Rejecting -> caller's #error.
    let peelsAreSimple =
        codeGen.Bindings |> List.forall (fun b ->
            b.Elements |> List.forall (fun e -> e.RankComponent = 0 && e.DimIndex = 0))
    if not peelsAreSimple then None
    else
    match extentExpr with
    | IRLit (IRLitInt n) when not codeGen.InputArrayNames.IsEmpty
                              && List.length codeGen.Bindings >= int grpRank ->
        let elemCpp = elemTypeToCpp outElemTy
        let r = int grpRank
        let neff = if strict then n else n + int64 r - 1L
        let binom (m: int64) (k: int) : int64 =
            if k < 0 || m < int64 k then 0L
            else
                let mutable num = 1L
                let mutable den = 1L
                for i in 0 .. k - 1 do
                    num <- num * (m - int64 i)
                    den <- den * int64 (i + 1)
                num / den
        let card = binom neff r
        let nsName = if strict then "antisymmetric" else "symmetric"
        let idxVar = sprintf "__blade_mpi_idx_%s" name
        // Operand reads keyed by elem.ParamVarId, each level reading its
        // array at the ABSOLUTE unranked coordinate (host arrays are in
        // scope by name â€” unlike the device path, which streams one flat
        // pool and reads it positionally).
        let mutable paramFinalNames : Map<IRId, string> = Map.empty
        let readBinds =
            [ for b in codeGen.Bindings do
                for elem in b.Elements |> List.distinctBy (fun e -> e.ArrayPosition) do
                    let readName = sprintf "__blade_op_%d_%d" b.Level elem.ArrayPosition
                    paramFinalNames <- Map.add elem.ParamVarId readName paramFinalNames
                    // Scalar peel (rank-1 input) binds the element; FIBER
                    // peel (rank-2 input, e.g. comoment kernels over
                    // per-variable observation rows) binds a sub-array
                    // WRAPPER sharing the parent's extents â€” same pattern
                    // as genLoopNest's host peel â€” so kernel intrinsics
                    // (prodsum's `.extents[0]` bound) keep working.
                    let resultRank = elem.ArrayRank - 1
                    if resultRank <= 0 then
                        yield sprintf "        auto %s = %s[%s[%d]];" readName elem.ArrayName idxVar b.Level
                    else
                        yield sprintf "        Array<%s, %d> %s = { %s.data[%s[%d]], %s.extents + 1 };"
                                  (elemTypeToCpp elem.ArrayElemType) resultRank readName
                                  elem.ArrayName idxVar b.Level elem.ArrayName ]
        let nameMap =
            codeGen.Captures |> List.fold (fun acc c -> Map.add c.Id c.Name acc) paramFinalNames
        // Antisym: Reynolds fold (true,true) emits the signed
        // antisymmetrization; symmetric: raw comm kernel (false,false).
        let reynolds =
            genKernelExprWithReynolds codeGen.KernelExpr codeGen.KernelParams strict strict nameMap paramFinalNames
        // Host packed allocation â€” identical to the CUDA simplicial path:
        // symmetric -> allocate<T, SYMM={1..1}>, antisym -> allocate_strict.
        let extentsName = sprintf "%s_extents" name
        let ones = List.replicate r 1
        let symmArg = hoistSymmDecl (sprintf "%s_symm" name) ones
        let extentDecls =
            [ sprintf "    size_t* %s = new size_t[%d];" extentsName r ]
            @ [ for d in 0 .. r - 1 -> sprintf "    %s[%d] = %dUL;" extentsName d n ]
        let allocLine =
            if strict then
                let strictArg = hoistSymmDecl (sprintf "%s_strict" name) ones
                arrayAlloc { Ind = "    "; Elem = elemCpp; Rank = r; Name = name
                             Symm = symmArg; Strict = Some strictArg; Extents = extentsName }
            else
                arrayAlloc { Ind = "    "; Elem = elemCpp; Rank = r; Name = name
                             Symm = symmArg; Strict = None; Extents = extentsName }
        // Balanced cell-range split over the packed pool. Cell counts are
        // near-equal by construction (contiguous flat ranges), unlike an
        // outer-row slab of a triangle. Same q/rem formula as the dense slab.
        let split =
            [ sprintf "    size_t __blade_mpi_n_%s = %dUL;" name card
              sprintf "    size_t __blade_mpi_q_%s = __blade_mpi_n_%s / (size_t)__blade_mpi_size;" name name
              sprintf "    size_t __blade_mpi_r_%s = __blade_mpi_n_%s %% (size_t)__blade_mpi_size;" name name
              sprintf "    size_t __blade_mpi_lo_%s = (size_t)__blade_mpi_rank * __blade_mpi_q_%s + ((size_t)__blade_mpi_rank < __blade_mpi_r_%s ? (size_t)__blade_mpi_rank : __blade_mpi_r_%s);" name name name name
              sprintf "    size_t __blade_mpi_hi_%s = __blade_mpi_lo_%s + __blade_mpi_q_%s + ((size_t)__blade_mpi_rank < __blade_mpi_r_%s ? 1 : 0);" name name name name ]
        let loop =
            [ sprintf "    %s* __blade_mpi_out_%s = nested_array_utilities::pool_base(%s.data);" elemCpp name name ]
            @ (if innerOmp then [ "    #pragma omp parallel for" ] else [])
            @ [ sprintf "    for (size_t __blade_c = __blade_mpi_lo_%s; __blade_c < __blade_mpi_hi_%s; __blade_c++) {" name name
                // Per-cell unrank (O(r log n)). Odometer advance â€” unrank once
                // at lo, then increment lexicographically â€” is the amortized-
                // O(1) upgrade once timing tests exist.
                sprintf "        auto %s = linearized_storage::%s::unlinearize<%d>(__blade_c, %dUL);" idxVar nsName r n ]
            @ readBinds
            @ [ sprintf "        __blade_mpi_out_%s[__blade_c] = %s;" name reynolds.CppExpr
                "    }" ]
        let gather =
            [ sprintf "    { // MPI: restore full %s on all ranks" name
              sprintf "        if (__blade_mpi_n_%s > 2147483647ULL) { std::cerr << \"error[BL8004]: element count exceeds int32 range (rank \" << __blade_mpi_rank << \")\" << std::endl; MPI_Abort(MPI_COMM_WORLD, 13); }" name
              "        int* __blade_mpi_counts = new int[__blade_mpi_size];"
              "        int* __blade_mpi_displs = new int[__blade_mpi_size];"
              "        for (int __r = 0; __r < __blade_mpi_size; __r++) {"
              sprintf "            size_t __lo = (size_t)__r * __blade_mpi_q_%s + ((size_t)__r < __blade_mpi_r_%s ? (size_t)__r : __blade_mpi_r_%s);" name name name
              sprintf "            size_t __hi = __lo + __blade_mpi_q_%s + ((size_t)__r < __blade_mpi_r_%s ? 1 : 0);" name name
              "            __blade_mpi_counts[__r] = (int)(__hi - __lo);"
              "            __blade_mpi_displs[__r] = (int)__lo;"
              "        }"
              sprintf "        MPI_Allgatherv(MPI_IN_PLACE, 0, MPI_DATATYPE_NULL, __blade_mpi_out_%s, __blade_mpi_counts, __blade_mpi_displs, %s, MPI_COMM_WORLD);" name mpiDtype
              "        delete[] __blade_mpi_counts; delete[] __blade_mpi_displs;"
              "    }" ]
        Some (extentDecls @ [allocLine] @ split @ loop @ gather)
    | _ -> None

/// Emit a CUDA kernel for the first-kernel scope (rectangular pointwise,
/// boundary-safe scalar elements, single-chunk synchronous). Returns
///   Some inlineLaunchLines  when emitted: the __global__ kernel + its
///     extern "C" launch wrapper are appended to cudaKernelDefsCell (destined
///     for the .cu file); the returned lines are the inline .cpp host code.
///   None  when out of scope (caller falls back to the host loop).
/// Gates: every binding rectangular const-extent RealArray scalar-leaf; array
/// output with boundary-safe elem type; no Reynolds. Only flat T*/size_t cross
/// the extern "C" boundary (pool_base supplies flat host pointers).
let genCudaKernel (softSplit: bool) (codeGen: LoopNestCodeGen) (name: string) (blockSize: int) : string list option =
    let bindings = codeGen.Bindings
    let nDims = List.length bindings
    let rectOk =
        bindings |> List.forall (fun b ->
            b.BoundDependencies.IsEmpty && b.StrictOffset = 0
            && (match b.Extent with IRLit (IRLitInt _) -> true | _ -> false)
            && (b.Elements |> List.forall (fun e -> match e.Virtual with RealArray -> true | _ -> false)))
    let outElemTyOpt =
        match codeGen.OutputType with
        | ArrayElem arr when isCudaBoundarySafeElem arr.ElemType -> Some arr.ElemType
        | _ -> None
    if codeGen.HasReynolds || not rectOk || outElemTyOpt.IsNone then None
    else
    let outElemTy = outElemTyOpt.Value
    let elemCpp = elemTypeToCpp outElemTy
    let extentLits =
        bindings |> List.map (fun b -> match b.Extent with IRLit (IRLitInt n) -> n | _ -> 0L)
    let cardinality = extentLits |> List.fold (fun a n -> a * n) 1L
    let kernelName = sprintf "__cuda_%s" (sanitizeCppName name)
    let launchName = sprintf "__launch_%s" (sanitizeCppName name)
    let mutable paramFinalNames : Map<IRId, string> = Map.empty
    let mutable currentNames : Map<int, string> =
        codeGen.InputArrayNames |> List.mapi (fun i n -> (i, n)) |> Map.ofList
    let bodyBinds =
        [ for b in bindings do
            let mutable declared : Set<string> = Set.empty
            for elem in b.Elements do
                let cur = Map.tryFind elem.ArrayPosition currentNames |> Option.defaultValue elem.ArrayName
                let newName = sprintf "%s__%s" cur b.IndexName
                let etStr = elemTypeToCpp elem.ArrayElemType
                currentNames <- Map.add elem.ArrayPosition newName currentNames
                paramFinalNames <- Map.add elem.ParamVarId newName paramFinalNames
                // Self-zip: two operand slots on the same (array, index) peel
                // to the identical declaration â€” bind once (nvcc redefinition).
                if not (Set.contains newName declared) then
                    declared <- Set.add newName declared
                    yield sprintf "    %s %s = %s[%s];" etStr newName cur b.IndexName ]
    let nameMap =
        codeGen.Captures |> List.fold (fun acc c -> Map.add c.Id c.Name acc) paramFinalNames
    let reynolds = genKernelExprWithReynolds codeGen.KernelExpr codeGen.KernelParams false false nameMap paramFinalNames
    let recover =
        [ yield "    size_t __blade_g = __blade_i;"
          for i in (nDims - 1) .. -1 .. 0 do
            let e = extentLits.[i]
            let b = bindings.[i]
            yield sprintf "    size_t %s = __blade_g %% %dUL;" b.IndexName e
            if i > 0 then yield sprintf "    __blade_g /= %dUL;" e ]
    let kernelParams =
        codeGen.InputArrayNames |> List.map (fun n -> sprintf "const %s* %s" elemCpp n) |> String.concat ", "
    let kernelDef =
        // NOTE on naming: generated CUDA-internal identifiers use a `__blade_`
        // prefix. The plain `__out` originally chosen collided with MSVC's SAL
        // annotation macro `__out` (sal.h, pulled in transitively on Windows),
        // which expands to nothing â€” turning `__out[__i] = ...` into a stray
        // `[__i] = ...` that nvcc/cl rejected as a bad attribute. `__in/__inout/
        // __out` are MSVC macros; `__blade_*` cannot collide with SAL or other
        // implementation-reserved names.
        [ sprintf "__global__ void %s(%s, %s* __blade_out, size_t __blade_card) {" kernelName kernelParams elemCpp
          "    size_t __blade_i = (size_t)blockIdx.x * blockDim.x + threadIdx.x;"
          "    if (__blade_i >= __blade_card) return;" ]
        @ recover @ bodyBinds
        @ [ sprintf "    __blade_out[__blade_i] = %s;" reynolds.CppExpr; "}" ]
    let wrapInParams =
        codeGen.InputArrayNames |> List.map (fun n -> sprintf "const %s* %s" elemCpp n) |> String.concat ", "
    let sdPrefix = sprintf "__blade_sd_%s" (sanitizeCppName name)
    let wrapper =
        if softSplit then
            // <&> soft-join split wrappers: begin = H2D + ASYNC launch on a
            // round-robin device (leaf % deviceCount, queried HERE so the host
            // half never touches the CUDA API); end = per-device sync + D2H +
            // free. One device => the default stream serializes the leaves.
            [ for n in codeGen.InputArrayNames -> sprintf "static %s* %s_d_%s = nullptr;" elemCpp sdPrefix n ]
            @ [ sprintf "static %s* %s_d_out = nullptr;" elemCpp sdPrefix
                sprintf "static int %s_dev = 0;" sdPrefix
                sprintf "extern \"C\" void %s_begin(%s, int __blade_leaf) {" launchName wrapInParams
                "    int __blade_dc = 1; cudaGetDeviceCount(&__blade_dc); if (__blade_dc < 1) __blade_dc = 1;"
                sprintf "    %s_dev = __blade_leaf %% __blade_dc;" sdPrefix
                sprintf "    cudaSetDevice(%s_dev);" sdPrefix
                sprintf "    size_t __blade_card = %dUL;" cardinality ]
            @ [ for (i, n) in List.mapi (fun i n -> (i, n)) codeGen.InputArrayNames do
                  let sz = extentLits.[i]
                  yield sprintf "    cudaMalloc(&%s_d_%s, %dUL * sizeof(%s));" sdPrefix n sz elemCpp
                  yield sprintf "    cudaMemcpy(%s_d_%s, %s, %dUL * sizeof(%s), cudaMemcpyHostToDevice);" sdPrefix n n sz elemCpp ]
            @ [ sprintf "    cudaMalloc(&%s_d_out, __blade_card * sizeof(%s));" sdPrefix elemCpp
                sprintf "    size_t __blade_blocks = (__blade_card + %dUL - 1UL) / %dUL;" blockSize blockSize
                sprintf "    %s<<<(unsigned)__blade_blocks, %d>>>(%s, %s_d_out, __blade_card);" kernelName blockSize
                  (codeGen.InputArrayNames |> List.map (fun n -> sprintf "%s_d_%s" sdPrefix n) |> String.concat ", ") sdPrefix
                "}"
                sprintf "extern \"C\" void %s_end(%s* __blade_host_out) {" launchName elemCpp
                sprintf "    cudaSetDevice(%s_dev);" sdPrefix
                "    cudaDeviceSynchronize();"
                sprintf "    cudaMemcpy(__blade_host_out, %s_d_out, %dUL * sizeof(%s), cudaMemcpyDeviceToHost);" sdPrefix cardinality elemCpp ]
            @ [ for n in codeGen.InputArrayNames -> sprintf "    cudaFree(%s_d_%s);" sdPrefix n ]
            @ [ sprintf "    cudaFree(%s_d_out);" sdPrefix
                "    cudaSetDevice(0);"; "}" ]
        else
        [ sprintf "extern \"C\" void %s(%s, %s* __blade_host_out) {" launchName wrapInParams elemCpp
          sprintf "    size_t __blade_card = %dUL;" cardinality ]
        @ [ for (i, n) in List.mapi (fun i n -> (i, n)) codeGen.InputArrayNames do
              let sz = extentLits.[i]
              yield sprintf "    %s* __blade_d_%s; cudaMalloc(&__blade_d_%s, %dUL * sizeof(%s));" elemCpp n n sz elemCpp
              yield sprintf "    cudaMemcpy(__blade_d_%s, %s, %dUL * sizeof(%s), cudaMemcpyHostToDevice);" n n sz elemCpp ]
        @ [ sprintf "    %s* __blade_d_out; cudaMalloc(&__blade_d_out, __blade_card * sizeof(%s));" elemCpp elemCpp
            sprintf "    size_t __blade_blocks = (__blade_card + %dUL - 1UL) / %dUL;" blockSize blockSize
            sprintf "    %s<<<(unsigned)__blade_blocks, %d>>>(%s, __blade_d_out, __blade_card);" kernelName blockSize
              (codeGen.InputArrayNames |> List.map (sprintf "__blade_d_%s") |> String.concat ", ")
            "    cudaDeviceSynchronize();"
            sprintf "    cudaMemcpy(__blade_host_out, __blade_d_out, __blade_card * sizeof(%s), cudaMemcpyDeviceToHost);" elemCpp ]
        @ [ for n in codeGen.InputArrayNames -> sprintf "    cudaFree(__blade_d_%s);" n ]
        @ [ "    cudaFree(__blade_d_out);"; "}" ]
    let cell = cudaKernelDefsCell ()
    cell.Value <- cell.Value @ (kernelDef @ [""] @ wrapper @ [""])
    let outputRank = nDims
    let extentsName = sprintf "%s_extents" name
    // First-kernel scope is rectangular (no symmetry), so pass `nullptr` directly
    // as the symm template arg â€” not via a function-local static (MSVC C2131).
    let inlineLines =
        [ sprintf "    size_t* %s = new size_t[%d];" extentsName outputRank ]
        @ (bindings |> List.mapi (fun i b ->
              match b.Extent with
              | IRLit (IRLitInt n) -> sprintf "    %s[%d] = %dUL;" extentsName i n
              | _ ->
                  match b.FusedRank with
                  | Some d ->
                      let prod = [0 .. d - 1] |> List.map (sprintf "%s.extents[%d]" b.ExtentArrayRef) |> String.concat " * "
                      sprintf "    %s[%d] = %s;" extentsName i prod
                  | None -> sprintf "    %s[%d] = %s.extents[%d];" extentsName i b.ExtentArrayRef b.ExtentDimRef))
        @ [ sprintf "    Array<%s, %d> %s = { allocate<typename promote<%s, %d>::type, nullptr>(%s), %s };"
                elemCpp outputRank name elemCpp outputRank extentsName extentsName
            sprintf "    %s(%s, pool_base(%s.data));"
                launchName
                // Inputs are Array<T,N> wrappers (array-literal bindings render as
                // `Array<T,1> A = { ... }`), same as the output â€” so the flat pool
                // is reached via `.data`. (An earlier version dropped `.data` on
                // inputs after a host-shape test that wrongly modeled inputs as
                // bare pointers; the self-contained program uses wrappers.)
                (codeGen.InputArrayNames |> List.map (fun n -> sprintf "pool_base(%s.data)" n) |> String.concat ", ")
                name ]
    if softSplit then
        // Soft-join caller sequences the begin/end calls itself; return the
        // host output allocation only (everything but the final call line).
        Some (inlineLines |> List.filter (fun l -> not (l.Contains (launchName + "("))))
    else
    Some inlineLines

/// CUDA CO-FUSION: one `__global__` computing EVERY leaf's output on a single
/// shared grid. This is the device analog of the host merged nest, for the
/// all-cuda case. Scope (returns None â†’ caller rejects with steering when any
/// fails): all leaves SAME arity and rectangular (a flat thread grid can't
/// stagger arities â€” that needs guarded shallow writes, gated separately),
/// SAME input arrays in the same order (so inputs are loaded once and shared),
/// no Reynolds, boundary-safe output elems, matching block size. Each leaf
/// still keeps its own kernel expression and output buffer.
let genCudaCoFusion (leafCgs: LoopNestCodeGen list) (leafNames: string list) (name: string) (blockSize: int) : string list option =
    let primary = List.head leafCgs
    let bindings = primary.Bindings
    let nDims = List.length bindings
    let rectOk (cg: LoopNestCodeGen) =
        cg.Bindings |> List.forall (fun b ->
            b.BoundDependencies.IsEmpty && b.StrictOffset = 0
            && (match b.Extent with IRLit (IRLitInt _) -> true | _ -> false)
            && (b.Elements |> List.forall (fun e -> match e.Virtual with RealArray -> true | _ -> false)))
    // Every leaf: same arity, rectangular, no Reynolds, boundary-safe output,
    // and identical input arrays (name + order) so the grid + input loads are
    // genuinely shared.
    let sameArity = leafCgs |> List.forall (fun cg -> cg.Bindings.Length = nDims)
    let sameInputs = leafCgs |> List.forall (fun cg -> cg.InputArrayNames = primary.InputArrayNames)
    let outElemOpt (cg: LoopNestCodeGen) =
        match cg.OutputType with
        | ArrayElem arr when isCudaBoundarySafeElem arr.ElemType -> Some arr.ElemType
        | _ -> None
    if not sameArity || not sameInputs || nDims = 0
       || leafCgs |> List.exists (fun cg -> not (rectOk cg) || cg.HasReynolds || (outElemOpt cg).IsNone)
    then None
    else
    let extentLits = bindings |> List.map (fun b -> match b.Extent with IRLit (IRLitInt n) -> n | _ -> 0L)
    let cardinality = extentLits |> List.fold (fun a n -> a * n) 1L
    // Per-input cpp elem type (from the primary's element bindings by array
    // position), so mixed-type inputs are declared correctly.
    let inputElemCpp =
        primary.InputArrayNames |> List.mapi (fun pos _ ->
            primary.Bindings
            |> List.collect (fun b -> b.Elements)
            |> List.tryFind (fun e -> e.ArrayPosition = pos)
            |> Option.map (fun e -> elemTypeToCpp e.ArrayElemType)
            |> Option.defaultValue "double")
    let kernelName = sprintf "__cuda_%s" (sanitizeCppName name)
    let launchName = sprintf "__launch_%s" (sanitizeCppName name)
    // Shared coordinate recovery (row-major unflatten of the flat thread id).
    let recover =
        [ yield "    size_t __blade_g = __blade_i;"
          for i in (nDims - 1) .. -1 .. 0 do
            let e = extentLits.[i]
            let b = bindings.[i]
            yield sprintf "    size_t %s = __blade_g %% %dUL;" b.IndexName e
            if i > 0 then yield sprintf "    __blade_g /= %dUL;" e ]
    // Shared input peels (identical across leaves â€” bound once). Also records
    // per-array-position peeled names to bridge each leaf's params.
    let mutable currentNames : Map<int, string> = primary.InputArrayNames |> List.mapi (fun i n -> (i, n)) |> Map.ofList
    let mutable sharedFinal : Map<int, string> = Map.empty  // arrayPosition -> peeled name
    let bodyBinds =
        [ for b in bindings do
            let mutable declared : Set<string> = Set.empty
            for elem in b.Elements do
                let cur = Map.tryFind elem.ArrayPosition currentNames |> Option.defaultValue elem.ArrayName
                let newName = sprintf "%s__%s" cur b.IndexName
                let etStr = elemTypeToCpp elem.ArrayElemType
                currentNames <- Map.add elem.ArrayPosition newName currentNames
                sharedFinal <- Map.add elem.ArrayPosition newName sharedFinal
                // Self-zip: two operand slots on the same (array, index) peel
                // to the identical declaration â€” bind once (nvcc redefinition).
                if not (Set.contains newName declared) then
                    declared <- Set.add newName declared
                    yield sprintf "    %s %s = %s[%s];" etStr newName cur b.IndexName ]
    // Per-leaf output write: map the leaf's params to the shared peeled names
    // by array position (same convention as the host merge).
    let leafWrites =
        leafCgs |> List.mapi (fun k cg ->
            let pfn =
                cg.Bindings |> List.collect (fun b -> b.Elements)
                |> List.choose (fun e -> Map.tryFind e.ArrayPosition sharedFinal |> Option.map (fun nm -> (e.ParamVarId, nm)))
                |> Map.ofList
            let nameMap = cg.Captures |> List.fold (fun acc c -> Map.add c.Id c.Name acc) pfn
            let rr = genKernelExprWithReynolds cg.KernelExpr cg.KernelParams false false nameMap pfn
            sprintf "    __blade_out_%d[__blade_i] = %s;" k rr.CppExpr)
    let sharedInParams =
        (primary.InputArrayNames, inputElemCpp) ||> List.map2 (fun n et -> sprintf "const %s* %s" et n) |> String.concat ", "
    let outParams =
        leafCgs |> List.mapi (fun k cg ->
            let et = (outElemOpt cg).Value |> elemTypeToCpp
            sprintf "%s* __blade_out_%d" et k) |> String.concat ", "
    let kernelDef =
        [ sprintf "__global__ void %s(%s, %s, size_t __blade_card) {" kernelName sharedInParams outParams
          "    size_t __blade_i = (size_t)blockIdx.x * blockDim.x + threadIdx.x;"
          "    if (__blade_i >= __blade_card) return;" ]
        @ recover @ bodyBinds @ leafWrites @ [ "}" ]
    let wrapInParams =
        (primary.InputArrayNames, inputElemCpp) ||> List.map2 (fun n et -> sprintf "const %s* %s" et n) |> String.concat ", "
    let wrapOutParams =
        leafCgs |> List.mapi (fun k cg ->
            let et = (outElemOpt cg).Value |> elemTypeToCpp
            sprintf "%s* __blade_host_out_%d" et k) |> String.concat ", "
    let wrapper =
        [ sprintf "extern \"C\" void %s(%s, %s) {" launchName wrapInParams wrapOutParams
          sprintf "    size_t __blade_card = %dUL;" cardinality ]
        @ [ for (i, n) in List.mapi (fun i n -> (i, n)) primary.InputArrayNames do
              let sz = extentLits |> List.fold (fun a n -> a * n) 1L  // inputs span the same grid cardinality
              let et = inputElemCpp.[i]
              yield sprintf "    %s* __blade_d_%s; cudaMalloc(&__blade_d_%s, %dUL * sizeof(%s));" et n n sz et
              yield sprintf "    cudaMemcpy(__blade_d_%s, %s, %dUL * sizeof(%s), cudaMemcpyHostToDevice);" n n sz et ]
        @ [ for k in 0 .. leafCgs.Length - 1 do
              let et = (outElemOpt leafCgs.[k]).Value |> elemTypeToCpp
              yield sprintf "    %s* __blade_d_out_%d; cudaMalloc(&__blade_d_out_%d, __blade_card * sizeof(%s));" et k k et ]
        @ [ sprintf "    size_t __blade_blocks = (__blade_card + %dUL - 1UL) / %dUL;" blockSize blockSize
            sprintf "    %s<<<(unsigned)__blade_blocks, %d>>>(%s, %s, __blade_card);"
                kernelName blockSize
                (primary.InputArrayNames |> List.map (sprintf "__blade_d_%s") |> String.concat ", ")
                (List.init leafCgs.Length (sprintf "__blade_d_out_%d") |> String.concat ", ")
            "    cudaDeviceSynchronize();" ]
        @ [ for k in 0 .. leafCgs.Length - 1 do
              let et = (outElemOpt leafCgs.[k]).Value |> elemTypeToCpp
              yield sprintf "    cudaMemcpy(__blade_host_out_%d, __blade_d_out_%d, __blade_card * sizeof(%s), cudaMemcpyDeviceToHost);" k k et ]
        @ [ for n in primary.InputArrayNames -> sprintf "    cudaFree(__blade_d_%s);" n ]
        @ [ for k in 0 .. leafCgs.Length - 1 -> sprintf "    cudaFree(__blade_d_out_%d);" k ]
        @ [ "}" ]
    let cell = cudaKernelDefsCell ()
    cell.Value <- cell.Value @ (kernelDef @ [""] @ wrapper @ [""])
    // Inline: allocate each output Array on the host, then a single launch.
    let inlineLines =
        (leafCgs |> List.mapi (fun k cg ->
            let lname = leafNames.[k]
            let et = (outElemOpt cg).Value |> elemTypeToCpp
            let extentsName = sprintf "%s_extents" lname
            [ sprintf "    size_t* %s = new size_t[%d];" extentsName nDims ]
            @ (cg.Bindings |> List.mapi (fun i b ->
                  match b.Extent with
                  | IRLit (IRLitInt n) -> sprintf "    %s[%d] = %dUL;" extentsName i n
                  | _ -> sprintf "    %s[%d] = %s.extents[%d];" extentsName i b.ExtentArrayRef b.ExtentDimRef))
            @ [ sprintf "    Array<%s, %d> %s = { allocate<typename promote<%s, %d>::type, nullptr>(%s), %s };"
                    et nDims lname et nDims extentsName extentsName ]) |> List.concat)
        @ [ sprintf "    %s(%s, %s);"
                launchName
                (primary.InputArrayNames |> List.map (fun n -> sprintf "pool_base(%s.data)" n) |> String.concat ", ")
                (leafNames |> List.map (fun ln -> sprintf "pool_base(%s.data)" ln) |> String.concat ", ") ]
    Some inlineLines

/// Classification of a loop nest for MPI decomposition (`where mpi`).
type MpiShape =
    /// Dense rectangular nest: outermost level slab-decomposed across ranks.
    | MpiDense
    /// Single symmetric/antisymmetric group: flat cell-range decomposition
    /// over the packed pool (Phase 3).
    | MpiSimplicial
    /// Not decomposable (v1): carries the human-readable reason for #error.
    | MpiIneligible of string

/// Classify a built loop nest for MPI eligibility. v1 scope: per-cell
/// array-output kernels over real (non-virtual) arrays whose element type has
/// a native MPI datatype. Everything else is rejected LOUDLY (the caller
/// emits #error) rather than silently serialized â€” an inert-looking `mpi`
/// clause under the emit gate would otherwise misreport scaling results.
let classifyMpiShape (codeGen: LoopNestCodeGen) : MpiShape =
    let bindings = codeGen.Bindings
    let allReal =
        bindings |> List.forall (fun b ->
            b.Elements |> List.forall (fun e ->
                match e.Virtual with RealArray -> true | _ -> false))
    let anyCompound =
        bindings |> List.exists (fun b ->
            match b.Extent with IRCompoundMask _ -> true | _ -> false)
    let anyFused = bindings |> List.exists (fun b -> b.FusedRank.IsSome)
    let isRectangular =
        bindings |> List.forall (fun b ->
            b.BoundDependencies.IsEmpty && b.StrictOffset = 0)
    if codeGen.FoldWrapper.IsSome then
        MpiIneligible "fold accumulation reorders floating-point reduction"
    else
        match codeGen.OutputType with
        | IRTScalar _ ->
            MpiIneligible "scalar output is a cross-cell reduction (floating-point reassociation)"
        | ArrayElem at when isCompoundArrayType at ->
            MpiIneligible "compound iteration domains are not decomposed (v1)"
        | ArrayElem at ->
            match at.ElemType with
            | AnyPrimElem et when (mpiDatatypeOf et).IsSome ->
                if anyCompound then MpiIneligible "compound iteration domains are not decomposed (v1)"
                elif not allReal then MpiIneligible "virtual sources (range/reverse) are not decomposed (v1)"
                elif bindings.IsEmpty then MpiIneligible "empty loop nest"
                elif isRectangular && not anyFused then MpiDense
                elif not isRectangular && not anyFused then MpiSimplicial
                else MpiIneligible "fused joint levels are not decomposed (v1)"
            | _ -> MpiIneligible "element type has no native MPI datatype"
        | _ -> MpiIneligible "output shape is not a plain array"

/// Generate the complete code for a combinator application (L <@> f)
let genApplyCombinator (ctx: CodeGenContext) (name: string) (info: ApplyInfo) (builder: IRBuilder) : string list =
    let ind = indentStr ctx
    
    // ============================================================================
    // Special case: ragged peel for grouped arrays.
    // ============================================================================
    // Triggered when method_for is applied to a single grouped array
    // (recognized by Tag = "__group_outer" on its first index type).
    // This bypasses the generic loop-nest builder for two reasons:
    //   1. The inner extent is ragged (per group, from gk__offsets).
    //   2. The kernel param ('g' in lambda(g) -> g(0)) has unresolved type at
    //      typecheck time, so kernelInputRanks=[0] and the loop builder would
    //      otherwise try to iterate both dims of the rank-2 grouped array.
    // We also rewrite IRApp(g, args) -> IRIndex(g, args) on the kernel body so
    // that 'g(0)' renders as 'g_local[0]' rather than 'g_local(0)'.
    // Generalized ragged peel: handles two source patterns
    //   (a) group_by output: outer index tagged __group_outer; lengths derived
    //       from the corresponding group_keys array via ctx.GroupedArrays.
    //   (b) ragged literal: inner index tagged __raggedidx_inline; lengths in
    //       arr_lens, offsets in arr_offsets (emitted by genArrayLiteral).
    // In both cases, the same loop structure works: outer loop over rows,
    // sub-array binding for the kernel param, kernel body executed per row.
    let tryRaggedPeel () : string list option =
        if info.Arrays.Length <> 1 then None
        else
            let arrType = info.ArrayTypes.[0]
            let isGroupedOuter =
                match arrType.IndexTypes with
                | outer :: _ -> outer.IxKind = IxKGroupOuter
                | _ -> false
            // Detect ragged-or-DepIdx input: at least 2 IndexTypes, and the
            // *inner* (any non-first) carries any of the ragged tags or the
            // DepIdx-inner tag. Covers ragged literals (__raggedidx_inline),
            // function-param closed form (__raggedidx), function-param opaque
            // form (__raggedidx_opaque), and DepIdx-allocated arrays
            // (__depidx_inner â€” runtime layout matches ragged once allocated).
            // All want the peel codegen path: outer iteration over rows,
            // sub-array binding for the kernel.
            let isRaggedLiteral =
                arrType.IndexTypes.Length >= 2 &&
                arrType.IndexTypes |> List.skip 1 |> List.exists (fun idx ->
                    isRaggedFamilyKind idx.IxKind || idx.IxKind = IxKDepInner)
            if not isGroupedOuter && not isRaggedLiteral then None
            else
                let arrExpr = info.Arrays.[0]
                let arrName = exprToCppCtx ctx arrExpr
                // Resolve "lengths source" â€” where to read each row's length
                // and offset. For group_by, this is the group_keys metadata
                // (gk__offsets, gk__ngroups). For ragged literals, it's the
                // array's own _offsets/_lens emitted at construction.
                let lengthsSource =
                    if isGroupedOuter then
                        match Map.tryFind arrName ctx.GroupedArrays with
                        | Some gkName ->
                            Some (sprintf "%s__ngroups" gkName,
                                  sprintf "%s__offsets[__g + 1] - %s__offsets[__g]" gkName gkName)
                        | None -> None
                    else
                        // Ragged literal: lens/extents are co-emitted with
                        // the array. The outer count is in arr_extents[0];
                        // each row's length is in arr_lens[__g].
                        Some (sprintf "%s.extents[0]" arrName,
                              sprintf "%s.lens[__g]" arrName)
                match lengthsSource with
                | None -> None
                | Some (ngroupsExpr, perRowLenExpr) ->
                    // Elementwise-vs-consuming dispatch, keyed on the OUTPUT
                    // type -- the authoritative signal, because TypeCheck
                    // already made this exact decision (paramUsedAsArray):
                    // a CONSUMING kernel (lambda(g) -> reduce(g, +)) collapses
                    // the ragged inner dim, so the output is dense rank-1; an
                    // ELEMENTWISE kernel (lambda(e) -> e * 2.0) leaves it in
                    // place, so the output keeps a ragged-family inner slot.
                    // Re-deriving the choice from the param type or body here
                    // would risk disagreeing with the type the rest of the
                    // pipeline committed to.
                    let outputInnerKinds =
                        match info.OutputType with
                        | ArrayElem a when a.IndexTypes.Length >= 2 ->
                            a.IndexTypes |> List.skip 1 |> List.map (fun ix -> ix.IxKind)
                        | _ -> []
                    let outputIsRaggedShaped =
                        outputInnerKinds |> List.exists (fun k -> isRaggedFamilyKind k || k = IxKDepInner)
                    let outputIsGroupShaped =
                        outputInnerKinds |> List.exists ((=) IxKGroupMember)
                    match resolveCallable info.Kernel with
                    | Some callable when callable.Params.Length = 1 && outputIsGroupShaped ->
                        // Elementwise map over a group_by result. The grouped
                        // value is an Array<T*,1> without lens/offsets wrapper
                        // members, and the group-shaped output type has no
                        // consumer support downstream (print, further peels
                        // resolve lengths through ctx.GroupedArrays, which this
                        // site cannot extend). Gate rather than miscompile;
                        // mapping BEFORE grouping is semantically equivalent.
                        Some (codegenError ctx ind "elementwise map over a group_by result is not yet supported; apply the map to the values BEFORE group_by (equivalent), or reduce per group")
                    | Some callable when callable.Params.Length = 1 && outputIsRaggedShaped ->
                        // Elementwise map over a ragged array: shape-preserving.
                        // The output is a fresh Ragged<T> that SHARES the
                        // parent's extents/lens/offsets metadata (same shape by
                        // construction) over a newly allocated contiguous pool
                        // (offsets[n] = total element count) with its own
                        // row-pointer table. Kernel applies per element; the
                        // param binds each element value.
                        let param = callable.Params.[0]
                        let inElemStr = elemTypeToCpp arrType.ElemType
                        let outElem =
                            match info.OutputType with
                            | ArrayElem a -> a.ElemType
                            | t -> t
                        let outElemStr = elemTypeToCpp outElem
                        let eName = sprintf "%s__e" name
                        let nameMap0 = Map.add param.VarId eName ctx.VarNames
                        let nameMap =
                            callable.Captures
                            |> List.fold (fun m c -> Map.add c.Id c.Name m) nameMap0
                        let bodyStr = exprToCpp nameMap callable.Body
                        let code =
                            [ sprintf "%s// ragged elementwise map over '%s' (shape-preserving; shares extents/lens/offsets)" ind arrName
                              sprintf "%s%s* %s__pool = new %s[%s.offsets[%s.extents[0]]];" ind outElemStr name outElemStr arrName arrName
                              sprintf "%s%s** %s__rows = new %s*[%s.extents[0]];" ind outElemStr name outElemStr arrName
                              sprintf "%sRagged<%s> %s = { %s__rows, %s.extents, %s.lens, %s.offsets };" ind outElemStr name name arrName arrName arrName
                              sprintf "%sfor (size_t __g = 0; __g < %s.extents[0]; __g++) {" ind arrName
                              sprintf "%s    %s__rows[__g] = %s__pool + %s.offsets[__g];" ind name name arrName
                              sprintf "%s    for (size_t __k = 0; __k < %s.lens[__g]; __k++) {" ind arrName
                              sprintf "%s        const %s %s = %s[__g][__k];" ind inElemStr eName arrName
                              sprintf "%s        %s__rows[__g][__k] = %s;" ind name bodyStr
                              sprintf "%s    }" ind
                              sprintf "%s}" ind ]
                        Some code
                    | Some callable when callable.Params.Length = 1 ->
                        let param = callable.Params.[0]
                        // Rewriter from the pre-Path-1 days: rewrites
                        // `g(args)` to `g[args]` in the kernel body. With
                        // Lowering's dispatch fix, the body already has
                        // IRIndex where it used to have IRApp(IRVar(g)),
                        // so this rewriter is effectively a no-op on
                        // post-Path-1 bodies but kept for defense in depth.
                        let rewriter e =
                            match e with
                            | IRApp (IRVar (id, ty), args, _) when id = param.VarId ->
                                IRIndex (IRVar (id, ty), args, None)
                            | _ -> e
                        let body = mapIRExpr rewriter callable.Body
                        // Element type of the inner sub-array (for the param binding type).
                        let arrElemStr = elemTypeToCpp arrType.ElemType
                        // Element type of the OUTPUT (per-row result).
                        let outElem =
                            match info.OutputType with
                            | ArrayElem a -> a.ElemType
                            | IRTScalar _ as t -> t
                            | _ ->
                                match inferExprType body with
                                | IRTScalar _ as t -> t
                                | ArrayElem a -> a.ElemType
                                | _ -> arrType.ElemType
                        let outElemStr = elemTypeToCpp outElem
                        // Sub-array binding name.
                        let subName = sprintf "%s__sub" name
                        let nameMap0 = Map.add param.VarId subName ctx.VarNames
                        let nameMap =
                            callable.Captures
                            |> List.fold (fun m c -> Map.add c.Id c.Name m) nameMap0
                        let bodyStr = exprToCpp nameMap body
                        let originLabel =
                            if isGroupedOuter then "grouped array" else "ragged literal"
                        // The peeled sub-row's C++ type must match the kernel
                        // PARAM's type, because the body's length accessor is
                        // generated from that param type (a rank-1 ragged/dep-idx
                        // param renders as RaggedRow<T>, using `.len`; anything
                        // else is Array<T,1>, using `.extents[0]`). Emitting a
                        // type that disagrees with the body's accessor produces
                        // `RaggedRow.extents[0]` / `Array.len` mismatches. So we
                        // derive the peel type from param.Type via the same
                        // predicate cppArrayTypeStr uses.
                        let paramIsRaggedRow =
                            match param.Type with
                            | ArrayElem at ->
                                (isRaggedArrayType at || isDepIdxArrayType at)
                                && at.IndexTypes.Length = 1
                            | _ -> false
                        let subDeclLines =
                            if paramIsRaggedRow then
                                // RaggedRow: inline `len` scalar, no separate _extents.
                                [ sprintf "%s    RaggedRow<%s> %s = { %s[__g], %s };" ind arrElemStr subName arrName perRowLenExpr ]
                            else
                                // Array<T,1>: length via a materialized _extents buffer.
                                [ sprintf "%s    size_t %s_extents[1] = {%s};" ind subName perRowLenExpr
                                  sprintf "%s    Array<%s, 1> %s = { %s[__g], %s_extents };" ind arrElemStr subName arrName subName ]
                        let code =
                            [ sprintf "%s// ragged peel over %s '%s'" ind originLabel arrName
                              sprintf "%ssize_t %s_extents[1] = {%s};" ind name ngroupsExpr
                              sprintf "%sArray<%s, 1> %s = { new %s[%s], %s_extents };" ind outElemStr name outElemStr ngroupsExpr name
                              sprintf "%sfor (size_t __g = 0; __g < %s; __g++) {" ind ngroupsExpr ]
                            @ subDeclLines
                            @ [ sprintf "%s    %s[__g] = %s;" ind name bodyStr
                                sprintf "%s}" ind ]
                        Some code
                    | _ -> None
    
    // MPI decomposition request: the resolved kernel opted into `mpi` AND the
    // emit gate is on (blade run --mpi N / the MPI test block). With the gate
    // off the clause is fully inert â€” every path below behaves exactly as
    // before. Checked HERE, before the special-case dispatches, so ragged /
    // grouped applications of an mpi kernel error loudly instead of silently
    // taking a serial special path.
    let mpiRequested =
        match resolveKernel info.Kernel with
        | Some rk -> rk.Callable.IsMpiParallel && mpiEmitModeEnabled ()
        | None -> false
    let mpiError (reason: string) : string list =
        [sprintf "%s#error \"mpi: kernel for '%s' is not MPI-eligible: %s\"" ind name reason]

    let raggedResult = tryRaggedPeel ()
    if raggedResult.IsSome then
        if mpiRequested then mpiError "ragged/grouped iteration domains are not decomposed (v1)"
        else raggedResult.Value
    else
    
    // Ragged/grouped operands are handled ONLY by tryRaggedPeel (single
    // array, single-param kernel). Anything that slipped past it -- a
    // multi-array method_for mixing a ragged operand with others, or a
    // multi-param kernel over a ragged array -- would fall into the standard
    // loop nest, which knows nothing about per-row lengths: it would emit a
    // doubled-up dense nest over the placeholder inner extent and index the
    // output 2D. That is SILENTLY WRONG code, so gate it loudly here.
    // (DepIdx-tagged arrays are deliberately not gated: their dependent
    // bounds have their own standard-nest handling.) Co-iteration semantics
    // for ragged operands -- e.g. aligning a dense per-row array against
    // ragged rows -- are a language-design question for the rewrite spec.
    let raggedStandardNestOperand =
        info.ArrayTypes |> List.exists (fun at ->
            at.IndexTypes |> List.exists (fun ix ->
                match ix.IxKind with
                | IxKRagged | IxKRaggedInline | IxKRaggedOpaque
                | IxKGroupOuter | IxKGroupMember -> true
                | _ -> false))
    if raggedStandardNestOperand then
        codegenError ctx ind "method_for over a ragged or grouped operand supports only the single-array, single-row-param form (lambda(g) -> ...) or an elementwise map (lambda(e) -> ...); mixing ragged operands with other arrays or multi-param kernels is not yet supported"
    else
    
    // Pre-materialize any inline array expressions (mask, intersect, union, etc.)
    // These need to be bound to temporary variables before the loop nest can reference them.
    let mutable preCode = []
    let mutable tempCtx = ctx
    let materializedArrays =
        info.Arrays |> List.mapi (fun i arr ->
            match arr with
            | IRVar (id, _) ->
                let name = Map.tryFind id tempCtx.VarNames |> Option.defaultValue (sprintf "arr%d" i)
                (name, arr)
            | IRRange (idxTys, _) when idxTys |> List.exists (fun ix -> ix.IxKind = IxKCompound) ->
                // range<CompoundIdx<m>>: materialize the standalone
                // compound_index_t for the driver BEFORE the loop nest. The
                // loop header bounds off `<name>_cidx->cardinality` and the
                // element bindings extract per-axis coordinates via
                // `<name>_cidx->unhash(r)[c]` (genElementBindingNew). The
                // compound slot must be the range's SOLE index type for now;
                // mixing (range<CompoundIdx<m>, J>) is unsupported.
                let rname = sprintf "__range%d" i
                (match idxTys with
                 | [ix] ->
                     (match ix.Extent with
                      | IRCompoundMask (IRVar (mid, _)) ->
                          (match Map.tryFind mid tempCtx.VarNames with
                           | Some maskName ->
                               let (idxLines, _) = genCompoundIndexFromMask maskName ix.Rank (sprintf "%s_cidx" rname)
                               preCode <- preCode @ (idxLines |> List.map (fun s -> ind + s))
                           | None ->
                               preCode <- preCode @ codegenError ctx ind "range<CompoundIdx>: mask variable not found in scope at codegen")
                      | _ ->
                          preCode <- preCode @ codegenError ctx ind "range<CompoundIdx>: the mask must be a NAMED Array<Bool like ...> variable (inline mask expressions are not supported); let-bind the mask first")
                 | _ ->
                     preCode <- preCode @ codegenError ctx ind "range<CompoundIdx<m>, ...>: a compound range slot cannot be combined with other index types in one range<> (not yet supported)")
                (rname, arr)
            | IRRange _ -> (sprintf "__range%d" i, arr)
            | IRVirtualReverse _ -> (sprintf "__rev%d" i, arr)
            | IRBlocked _ -> (sprintf "__blk%d" i, arr)
            | IRMask _ | IRIntersect _ | IRUnion _ | IRUnique _ ->
                // Auto-materialize: when a method_for receives an inline form
                // as one of its arrays, generate a temporary binding before
                // the loop nest. The Phase C lift pass deliberately leaves
                // these in IRMethodFor.Arrays slots (treated as a "blessed
                // position"), routing them through this path instead.
                //
                // Strict elem-type inference (with #error on unresolvable)
                // happens here; the shared `materializeInlineForm` helper
                // emits the C++ template.
                let tmpName = sprintf "%s__tmp%d" name i
                let tmpId = builder.FreshId()
                let tmpType = inferExprType arr
                let (elemET, autoMaterErr) = inferElemTypeStrict tempCtx ind arr "auto-materialize"
                let elemStr = elemTypeToCpp elemET
                preCode <- preCode @ autoMaterErr
                let matStmts =
                    match materializeInlineForm emptySubst tempCtx.VarNames tmpName elemStr arr with
                    | Some s -> s
                    | None -> []
                let code = matStmts |> List.map (fun s -> ind + s)
                preCode <- preCode @ code
                tempCtx <- addVarName tmpId tmpName tempCtx
                (tmpName, IRVar (tmpId, tmpType))
            | IRSort _ | IRGroupKeys _ | IRGroupBy _ ->
                // Per design decision: these operations require let-binding.
                // Auto-materializing them inline would require duplicating their
                // codegen here (mask/intersect/union do it because they predate
                // the let-only convention), and we deliberately stopped paying
                // that cost. Surface a clear error instead of emitting bad C++.
                let opName =
                    match arr with
                    | IRSort _ -> "sort"
                    | IRGroupKeys _ -> "group_keys"
                    | IRGroupBy _ -> "group_by"
                    | _ -> "?"
                let errCode = codegenError ctx ind (sprintf "'%s' must be let-bound before use in method_for; e.g. let s = %s(...) then method_for(s)" opName opName)
                preCode <- preCode @ errCode
                (sprintf "arr%d" i, arr)
            | _ -> (sprintf "arr%d" i, arr))
    
    let arrayNames = materializedArrays |> List.map fst
    let updatedArrays = materializedArrays |> List.map snd
    let info = { info with Arrays = updatedArrays }
    
    if arrayNames.IsEmpty then
        codegenError ctx ind (sprintf "no arrays in method_for for '%s' â€” kernel cannot be applied" name)
    else
        // Build LoopNestCodeGen (handles both outer product and co-iteration)
        let codeGen = buildLoopNestCodeGen info arrayNames name builder

        // STREAMED provider inputs (`alias.stream`): no materialized arrays
        // exist — the nest inlines per-fiber reads at the S/T boundary.
        // Pre-allocate one destination buffer per streamed fiber binding (a
        // comm kernel holds several fibers of one source concurrently, so a
        // per-source buffer would be clobbered).
        let (streamedMap, streamPrologue) = streamedNestSetup ctx.StreamedArrays ind [codeGen]

        // Get output rank and type info
        let outputRank =
            match codeGen.OutputType with
            | ArrayElem arr -> arrayRank arr
            | IRTScalar _ -> 0
            | _ -> 0

        let outputElemType =
            match codeGen.OutputType with
            | ArrayElem arr -> elemTypeToCpp arr.ElemType
            | IRTScalar et -> primTypeToCpp et
            | t -> irTypeToCpp t

        // MPI classification of the built nest (None when not requested / gate
        // off â€” the clause is then fully inert and nothing below changes).
        let mpiShape = if mpiRequested then Some (classifyMpiShape codeGen) else None

        // Branch on output shape. Array output gets the full ceremony
        // (symmetry vector, extents declaration, allocation, then loop nest
        // with indexed assignments). Scalar output gets a single scalar
        // accumulator initialized to zero, then the same loop nest with
        // `+=` accumulation (genLoopNest detects this via codeGen.OutputType).
        // The scalar branch handles the Cartesian-sum-reduce pattern that
        // the old hand-written 2-array IIFE special-cased â€” now generalized
        // to any number of input arrays through the shared LoopNestCodeGen
        // machinery (commutativity, Reynolds, etc. all carry through).
        match codeGen.OutputType with
        | IRTScalar _ when mpiRequested ->
            mpiError "scalar output is a cross-cell reduction (floating-point reassociation)"
        | IRTScalar _ ->
            // Scalar accumulator: declare initialized to 0, then run the
            // loop nest which accumulates into it via genLoopNest's `+=`.
            let scalarDecl = sprintf "%s%s %s = 0;" ind outputElemType name
            let loopCode = genLoopNestStreamed streamedMap codeGen tempCtx.VarNames tempCtx.Indent
            preCode @ streamPrologue @ [scalarDecl; ""] @ loopCode
        | _ when (match mpiShape with Some (MpiIneligible _) -> true | _ -> false) ->
            let reason = match mpiShape with Some (MpiIneligible r) -> r | _ -> ""
            mpiError reason
        | _ when mpiShape = Some MpiSimplicial ->
            // Flat cell-range decomposition over the packed pool (the
            // triangular sibling of the dense slab below). Like the CUDA
            // inline path, the emitter replaces the whole array-output
            // ceremony (extents/alloc/loop/gather); shapes it can't take
            // (non-literal extent, multi-group output) error loudly rather
            // than silently serialize.
            let kernelCudaBlock =
                match resolveKernel info.Kernel with
                | Some rk when rk.Callable.IsCudaKernel && cudaEmitModeEnabled () -> Some rk.Callable.CudaBlockSize
                | _ -> None
            match kernelCudaBlock with
            | Some bs ->
                // `where mpi, cuda(...)`: rank-scoped device launch over
                // this rank's flat cell-range + cell-range Allgatherv.
                (match genCudaKernelSimplicial true false codeGen name bs with
                 | Some lines -> preCode @ [""] @ lines
                 | None -> mpiError "mpi+cuda hybrid: kernel shape is not device-eligible (single sym/antisym group, literal extent, MPI-datatype element required)")
            | None ->
            let innerOmp =
                match resolveKernel info.Kernel with
                | Some rk -> rk.Callable.IsOmpParallel
                | None -> false
            match genMpiNestSimplicial innerOmp codeGen name with
            | Some lines -> preCode @ [""] @ lines
            | None -> mpiError "symmetric shape outside v1 MPI scope (single sym/antisym group with literal extent required)"
        | _ ->
            // CUDA dispatch: if the resolved kernel opted into cuda AND the case
            // is in first-kernel scope, emit a device kernel (+ .cu wrapper) and
            // an inline launch instead of the host loop. genCudaKernel returns
            // None for out-of-scope cases, falling back to the host loop below.
            let cudaInline =
                match resolveKernel info.Kernel with
                | Some rk when rk.Callable.IsCudaKernel && cudaEmitModeEnabled () && mpiRequested ->
                    // `where mpi, cuda(...)` over a DENSE rectangular nest:
                    // the simplicial hybrid is implemented (rank-scoped
                    // launches over packed cell-ranges); the dense-slab
                    // device variant is not emitted yet. Loud rather than
                    // launching a full-extent kernel inside an MPI slab.
                    raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf "mpi+cuda hybrid for dense rectangular nests is not emitted yet (the sym/antisym simplicial hybrid is) — run with one emit gate, or make the output a packed group")))
                | Some rk when rk.Callable.IsCudaKernel && cudaEmitModeEnabled () ->
                    // CUDA emission is gated: it only fires in the dedicated CUDA
                    // phase (which compiles+links the .cu). During ordinary
                    // host-only compilation the flag is off, so the `cuda` clause
                    // stays inert (host fallback) â€” otherwise the emitted
                    // `extern "C"` launch call would be an undefined symbol at link
                    // time (the .cu isn't built in the host corpus).
                    // Try the symmetric rank-2 triangular path, then the
                    // antisymmetric rank-2 strict-triangular path, then the
                    // rectangular pointwise path; None => host loop.
                    // One general simplicial kernel handles any single S-group of
                    // arity >= 2 (symmetric inclusive / antisymmetric strict, any
                    // rank); then the rectangular pointwise path; None => host loop.
                    genCudaKernelSimplicial false false codeGen name rk.Callable.CudaBlockSize
                    |> Option.orElseWith (fun () -> genCudaKernel false codeGen name rk.Callable.CudaBlockSize)
                | _ -> None
            match cudaInline with
            | Some launchLines -> preCode @ [""] @ launchLines
            | None ->
            // Array output: symmetry vector, extents, allocation, loop nest.
            let symmVecName = sprintf "%s_symm" name
            // When there's no symmetry, pass `nullptr` DIRECTLY as the template
            // argument rather than routing through a named local
            // `static constexpr const size_t* R_symm = nullptr`. MSVC rejects the
            // address of a function-local static as a constant expression in the
            // `if constexpr ((bool)SYMM && ...)` inside count_leaves/build_skeleton
            // (error C2131, "unevaluable pointer value") â€” even when the value is
            // nullptr. Passing the `nullptr` literal sidesteps it and matches the
            // array-literal allocation path. g++ accepts both, so no regression.
            let symmArg =
                if hasRealSymmetry codeGen.OutputSymmVec then hoistSymmDecl symmVecName codeGen.OutputSymmVec
                else "nullptr"
            // No function-local symm decl: rectangular -> nullptr (literal),
            // symmetric -> hoisted to namespace scope (see hoistSymmDecl). Either
            // way nothing symm-related is declared inside main().

            // Generate extent computation
            let extentsName = sprintf "%s_extents" name
            let extentsDecl = sprintf "%ssize_t* %s = new size_t[%d];" ind extentsName outputRank

            // Fill extents from loop level info
            let extentsFill =
                codeGen.Bindings |> List.mapi (fun i b ->
                    match b.Extent with
                    | IRLit (IRLitInt n) ->
                        sprintf "%s%s[%d] = %s;" ind extentsName i (sprintf "%d" n)
                    | IRCompoundMask _ ->
                        // Compound-inner halo level (the only compound level
                        // that reaches the DENSE output path — plain compound
                        // ranges take the Compound-output branch): written
                        // cells = cardinality minus the interior shrink, which
                        // rides the binding's StrictOffset (see IR loop build).
                        let sub = if b.StrictOffset > 0 then sprintf " - %d" b.StrictOffset else ""
                        sprintf "%s%s[%d] = %s_cidx->cardinality%s;" ind extentsName i b.ExtentArrayRef sub
                    | _ ->
                        // Fused joint level (arc 1): output extent = product of
                        // the source array's fused dims.
                        match b.FusedRank with
                        | Some d ->
                            let prod = [0 .. d - 1] |> List.map (sprintf "%s.extents[%d]" b.ExtentArrayRef) |> String.concat " * "
                            sprintf "%s%s[%d] = %s;" ind extentsName i prod
                        | None ->
                            sprintf "%s%s[%d] = %s.extents[%d];" ind extentsName i b.ExtentArrayRef b.ExtentDimRef)

            // Generate allocation as Array<T,N> wrapper. extentsName here is
            // a runtime-allocated `size_t*` (not a static constexpr); the
            // wrapper just stores a pointer to it, so the same brace-init
            // pattern works.
            let allocRhs =
                match emitAllocRhs (classifyOutputStorage codeGen.OutputType)
                          outputElemType outputRank symmArg extentsName with
                | Ok rhs -> rhs
                | Error msg -> sprintf "{ nullptr, %s };\n#error \"%s\"" extentsName msg
            let allocDecl = sprintf "%sArray<%s, %d> %s = %s;"
                                ind outputElemType outputRank name allocRhs

            // MPI dense slab mode: the nest's outermost level iterates this
            // rank's [lo, hi) slab (MpiSlab flag), bounded by the prologue
            // below; the Allgatherv afterward restores the full output on all
            // ranks (SPMD invariant â€” downstream code needs no changes).
            let mpiDense = (mpiShape = Some MpiDense)
            let codeGen = if mpiDense then { codeGen with MpiSlab = true } else codeGen

            // Generate loop nest
            let loopCode = genLoopNestStreamed streamedMap codeGen tempCtx.VarNames tempCtx.Indent

            // Per-rank slab bounds. Balanced split: q = n/P with the first
            // n%P ranks taking one extra row; P > n degenerates to empty
            // slabs (lo == hi), which is correct (zero-count Allgatherv).
            let mpiSlabPrologue =
                if not mpiDense then []
                else
                    let outerBound =
                        genLoopBoundExpr (compoundArrayNamesOf codeGen.Bindings)
                                         (List.head codeGen.Bindings)
                    [ sprintf "%ssize_t __blade_mpi_n_%s = %s;" ind name outerBound
                      sprintf "%ssize_t __blade_mpi_q_%s = __blade_mpi_n_%s / (size_t)__blade_mpi_size;" ind name name
                      sprintf "%ssize_t __blade_mpi_r_%s = __blade_mpi_n_%s %% (size_t)__blade_mpi_size;" ind name name
                      sprintf "%ssize_t __blade_mpi_lo_%s = (size_t)__blade_mpi_rank * __blade_mpi_q_%s + ((size_t)__blade_mpi_rank < __blade_mpi_r_%s ? (size_t)__blade_mpi_rank : __blade_mpi_r_%s);" ind name name name name
                      sprintf "%ssize_t __blade_mpi_hi_%s = __blade_mpi_lo_%s + __blade_mpi_q_%s + ((size_t)__blade_mpi_rank < __blade_mpi_r_%s ? 1 : 0);" ind name name name name ]

            // Post-loop gather: every rank contributed a contiguous pool range
            // [lo*inner, hi*inner) (row-major DFS pool = slab of outer rows),
            // so MPI_IN_PLACE Allgatherv on the full pool reassembles the
            // array identically on all ranks. Counts/displs are runtime-
            // filled â€” P is only known at mpiexec time. MPI counts are int:
            // guard totals above 2^31-1 with MPI_Abort.
            let mpiGather =
                if not mpiDense then []
                else
                    let extentsName = sprintf "%s_extents" name
                    let dtype =
                        match codeGen.OutputType with
                        | ArrayElem at ->
                            (match at.ElemType with
                             | AnyPrimElem et -> mpiDatatypeOf et
                             | _ -> None)
                        | _ -> None
                        |> Option.defaultValue "MPI_DOUBLE"
                    let innerProd =
                        if outputRank <= 1 then "1"
                        else
                            [1 .. outputRank - 1]
                            |> List.map (fun i -> sprintf "%s[%d]" extentsName i)
                            |> String.concat " * "
                    [ sprintf "%s{ // MPI: restore full %s on all ranks" ind name
                      sprintf "%s    %s* __blade_mpi_pool = nested_array_utilities::pool_base(%s.data);" ind outputElemType name
                      sprintf "%s    size_t __blade_mpi_inner = %s;" ind innerProd
                      sprintf "%s    if (__blade_mpi_n_%s * __blade_mpi_inner > 2147483647ULL) { std::cerr << \"error[BL8004]: element count exceeds int32 range (rank \" << __blade_mpi_rank << \")\" << std::endl; MPI_Abort(MPI_COMM_WORLD, 13); }" ind name
                      sprintf "%s    int* __blade_mpi_counts = new int[__blade_mpi_size];" ind
                      sprintf "%s    int* __blade_mpi_displs = new int[__blade_mpi_size];" ind
                      sprintf "%s    for (int __r = 0; __r < __blade_mpi_size; __r++) {" ind
                      sprintf "%s        size_t __lo = (size_t)__r * __blade_mpi_q_%s + ((size_t)__r < __blade_mpi_r_%s ? (size_t)__r : __blade_mpi_r_%s);" ind name name name
                      sprintf "%s        size_t __hi = __lo + __blade_mpi_q_%s + ((size_t)__r < __blade_mpi_r_%s ? 1 : 0);" ind name name
                      sprintf "%s        __blade_mpi_counts[__r] = (int)((__hi - __lo) * __blade_mpi_inner);" ind
                      sprintf "%s        __blade_mpi_displs[__r] = (int)(__lo * __blade_mpi_inner);" ind
                      sprintf "%s    }" ind
                      sprintf "%s    MPI_Allgatherv(MPI_IN_PLACE, 0, MPI_DATATYPE_NULL, __blade_mpi_pool, __blade_mpi_counts, __blade_mpi_displs, %s, MPI_COMM_WORLD);" ind dtype
                      sprintf "%s    delete[] __blade_mpi_counts; delete[] __blade_mpi_displs;" ind
                      sprintf "%s}" ind ]

            // Combine all (prepend any pre-materialized temporaries). A compound
            // output inherits the input CompoundIdx: allocate a fresh compact
            // buffer and SHARE the input compound's idx (same mask) and
            // trailing_stride -- not a dense extents/allocate<> Array. (Unary map:
            // the sole input is the compound; binary same-mask is deferred.) The
            // loop nest writes every present cell, so no zero-init is needed; the
            // dense extents/alloc lines above are unused on this path. The shared
            // idx pointer is non-owning, matching the manual-free memory model.
            match codeGen.OutputType with
            | ArrayElem at when isCompoundArrayType at ->
                let inName = codeGen.InputArrayNames |> List.tryHead |> Option.defaultValue name
                let leadRank =
                    at.IndexTypes
                    |> List.tryFind (fun idx -> idx.IxKind = IxKCompound)
                    |> Option.map (fun idx -> idx.Rank)
                    |> Option.defaultValue 1
                // A range<CompoundIdx> DRIVER has no Compound value to share an
                // idx from; the output shares the standalone materialized index
                // (`<name>_cidx`, emitted into preCode above) with trailing
                // stride 1. A compound VALUE input shares its `.idx` and stride.
                let inputIsCompoundRange =
                    match info.Arrays |> List.tryHead with
                    | Some (IRRange (its, _)) -> its |> List.exists (fun ix -> ix.IxKind = IxKCompound)
                    | _ -> false
                let idxExpr = if inputIsCompoundRange then sprintf "%s_cidx" inName else sprintf "%s.idx" inName
                let strideExpr = if inputIsCompoundRange then "1" else sprintf "%s.trailing_stride" inName
                let compDecl =
                    sprintf "%snested_array_utilities::Compound<%s, %d> %s = { new %s[%s->cardinality * %s], %s, %s };"
                        ind outputElemType leadRank name outputElemType idxExpr strideExpr idxExpr strideExpr
                preCode @ streamPrologue @ [""; compDecl; ""] @ loopCode
            | _ ->
                preCode @ streamPrologue @ [""; extentsDecl] @ extentsFill @ [""; allocDecl; ""]
                @ mpiSlabPrologue @ loopCode @ mpiGather

/// Execution backend a fusion leaf requests. Backends are whole-leaf: `omp`
/// threads a leaf's outer loop level, while `cuda`/`mpi` are whole-nest
/// device/domain transforms (a leaf is a device kernel launch or a
/// rank-decomposed domain, not a per-level pragma). This granularity is why
/// "cov's inner dim is cuda" really means "cov is a cuda leaf": cuda cannot
/// share a host loop header with a sibling. Serial (no clause) is the default.
type LeafBackend =
    | BkSerial
    | BkOmp
    | BkCuda of blockSize: int
    | BkMpi

/// Classify a leaf's requested backend from its resolved kernel's opt-in
/// flags (parser guarantees at most one of omp/cuda/mpi per where-clause).
/// cuda/mpi are gated by their emit modes exactly like the single-kernel
/// path: outside `blade test --cuda` / `--mpi` (or the corresponding run
/// flag) the clause is INERT and the leaf classifies as serial host â€” so a
/// plain host build never spuriously rejects a `where cuda` leaf in a fusion
/// tree; the device co-fusion (and its mixed-backend conflict) engages only
/// when the backend is actually active.
let classifyLeafBackend (info: ApplyInfo) : LeafBackend =
    match resolveKernel info.Kernel with
    | Some rk when rk.Callable.IsCudaKernel && cudaEmitModeEnabled () -> BkCuda rk.Callable.CudaBlockSize
    | Some rk when rk.Callable.IsMpiParallel && mpiEmitModeEnabled () -> BkMpi
    | Some rk when rk.Callable.IsOmpParallel -> BkOmp
    | _ -> BkSerial

/// Whether two backends are the SAME host class for hard-fusion (<&!>)
/// agreement. Only serial and omp are host backends; cuda/mpi are not host.
let private isHostBackend = function BkSerial | BkOmp -> true | _ -> false

/// Nest-level pragma for a MERGED host nest, honoring the joined host-parallel
/// decision and the staggered shape. A staggered tower must parallelize the
/// OUTER loop only (each outer index owns disjoint output slabs across every
/// leaf â€” safe), never `collapse` (collapsing an inner rectangular prefix
/// would re-run a shallow leaf's assignment once per inner iteration â€” a race
/// on its cell). A non-staggered merge (all leaves write at the deepest level)
/// may collapse exactly like a single nest, so it defers to genNestPragma.
let genFusedNestPragma (bindings: LoopIndexBinding list) (staggered: bool) (pragmaIndent: string) : string =
    match bindings with
    | [] -> ""
    | outer :: rest ->
        if not staggered then genNestPragma bindings pragmaIndent
        else
            let isRectangular (b: LoopIndexBinding) =
                b.BoundDependencies.IsEmpty && b.StrictOffset = 0
            let hasTriangularBelow = rest |> List.exists (fun b -> not (isRectangular b))
            if hasTriangularBelow then
                sprintf "#pragma omp parallel for schedule(dynamic)\n%s" pragmaIndent
            else
                sprintf "#pragma omp parallel for\n%s" pragmaIndent

/// Check that a set of fusion leaves can legally merge into ONE loop nest.
/// Leaves may have DIFFERENT depths (arities): a shallower leaf's loop levels
/// must prefix-match the deepest leaf's levels (same extent, same triangular
/// bound dependencies, same strict offset, same fused joint rank), because
/// the merged nest reuses the deepest leaf's loop headers and a shallower
/// leaf's assignment executes at its own depth. Arrays do NOT have to match
/// across leaves: each leaf peels its own arrays (identical peels are
/// deduplicated at emission).
/// Returns the primary (deepest) leaf, or a human-readable incompatibility.
let checkMergeCompatible (leafCgs: LoopNestCodeGen list) : Result<LoopNestCodeGen, string> =
    if leafCgs.IsEmpty then Error "no fusion leaves" else
    if leafCgs |> List.exists (fun cg -> cg.Bindings.IsEmpty) then
        Error "a leaf has no loop levels (scalar application)"
    else
    let primary = leafCgs |> List.maxBy (fun cg -> cg.Bindings.Length)
    let boundEq (a: LoopIndexBinding) (b: LoopIndexBinding) =
        // Same runtime extent: literal-equal, or resolved against the same
        // array dimension (covers a literal vs. runtime rendering of the
        // same axis â€” ExtentArrayRef/DimRef name the SAME dimension, so the
        // bound value is identical either way).
        let extentEq =
            match a.Extent, b.Extent with
            | IRLit la, IRLit lb -> la = lb
            | _ -> a.ExtentArrayRef = b.ExtentArrayRef && a.ExtentDimRef = b.ExtentDimRef
        extentEq
        && a.BoundDependencies = b.BoundDependencies
        && a.StrictOffset = b.StrictOffset
        && a.FusedRank = b.FusedRank
    let incompat =
        leafCgs |> List.tryPick (fun cg ->
            cg.Bindings
            |> List.mapi (fun j b -> (j, b))
            |> List.tryPick (fun (j, b) ->
                if boundEq primary.Bindings.[j] b then None
                else Some (sprintf "loop level %d of '%s' does not match '%s' (extent or triangular structure differs)"
                               j cg.OutputName primary.OutputName)))
    match incompat with
    | Some reason -> Error reason
    | None -> Ok primary

/// Generate ONE merged loop nest for a set of fusion leaves (<&!>, fusable
/// <&>, and reduce over fused trees). The nest structure comes from the
/// DEEPEST leaf (validated by checkMergeCompatible); each leaf's assignment
/// is emitted at its OWN depth, so mixed-arity towers stagger:
///     for i0 { m1[i0] = ..; for i1 { m2[i0][i1] = ..; for i2 { .. } } }
/// and every leaf streams the shared outer elements from one load. Each
/// leaf's kernel params resolve through the leaf's OWN element bindings
/// (never bridged positionally to another leaf's arrays); peels that render
/// identically â€” shared arrays at shared levels â€” are emitted once.
/// `hostParallel` is the JOINED host-backend decision from the caller (the
/// per-operator omp rule: <&> = any leaf omp, <&!> = all leaves omp). When
/// true the merged nest's outer level gets an omp pragma, driven by the
/// staggered-aware genFusedNestPragma. A fused FOLD ignores it (scalar
/// accumulators race; omp reduction clauses are the future upgrade).
/// `mpiSlabVar`, when Some sv, makes the OUTER shared level iterate this
/// rank's slab [__blade_mpi_lo_<sv>, __blade_mpi_hi_<sv>) instead of the full
/// extent (MPI co-fusion â€” every leaf's output is then a contiguous outer-row
/// slab restored by a per-leaf Allgatherv). Under mpi+omp hybrid co-fusion
/// (the all-mpi fusion arm), hostParallel = true additionally puts a bare
/// `#pragma omp parallel for` on the cell loop inside each rank's slab.
let genFusedLoopNestStreamed (streamed: Map<string, ProviderReadSpec>) (leafCgs: LoopNestCodeGen list) (outerNames: Map<int, string>) (indent: int) (hostParallel: bool) (mpiSlabVar: string option) : string list =
    let ind n = String.replicate n "    "
    let primary = leafCgs |> List.maxBy (fun cg -> cg.Bindings.Length)
    let staggered = leafCgs |> List.exists (fun cg -> cg.Bindings.Length <> primary.Bindings.Length)
    let emitOuterOmp = hostParallel && primary.FoldWrapper.IsNone
    // Streamed fiber reads share per-source handles and per-argument
    // buffers — not thread-safe under a host-parallel outer loop in v1.
    if emitOuterOmp && not (Map.isEmpty streamed) then
        raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf "streamed provider reads are not thread-safe under omp in v1 — bind with .read (streamed sources: %s)"
            (streamed |> Map.toList |> List.map fst |> String.concat ", "))))
    let mutable lines = []
    let mutable depth = indent

    // Per-leaf peeled-name state: (leafIdx, arrayPosition) -> current name.
    let mutable currentNames : Map<int * int, string> = Map.empty
    // Per-leaf kernel-param resolution: leafIdx -> (paramVarId -> final name).
    let mutable paramFinalNames : Map<int, Map<int, string>> =
        leafCgs |> List.mapi (fun li _ -> (li, Map.empty)) |> Map.ofList
    // Streamed sources: accumulated ABSOLUTE site coordinates per
    // (leaf, array position).
    let mutable streamSites : Map<int * int, string list> = Map.empty

    let compoundArrays = compoundArrayNamesOf primary.Bindings
    let mutable atOuterLevel = true

    for j in 0 .. primary.Bindings.Length - 1 do
        let pBinding = primary.Bindings.[j]
        // Nest-level OpenMP pragma at the outermost level only, gated by the
        // JOINED host-parallel decision (not the primary leaf's own flag â€” a
        // <&> sibling's omp configures the shared header). The outer binding's
        // IsParallel is forced on so genNestPragma's collapse/schedule logic
        // engages for the non-staggered case; staggered uses outer-only.
        let pragmaPrefix =
            if atOuterLevel && emitOuterOmp then
                let forced = { pBinding with IsParallel = true } :: List.tail primary.Bindings
                genFusedNestPragma forced staggered (ind depth)
            else ""
        atOuterLevel <- false
        // MPI co-fusion: the outer shared level iterates this rank's row slab.
        let header =
            match mpiSlabVar with
            | Some sv when j = 0 ->
                sprintf "for (size_t %s = __blade_mpi_lo_%s; %s < __blade_mpi_hi_%s; %s++) {"
                    pBinding.IndexName sv pBinding.IndexName sv pBinding.IndexName
            | _ -> genForLoopHeader compoundArrays pBinding
        lines <- lines @ [ind depth + pragmaPrefix + header]
        depth <- depth + 1

        // Element peels for every leaf that iterates this level, from the
        // leaf's OWN arrays. declaredNames maps emitted C++ name -> code
        // line: an identical (name, code) pair is a shared peel (emit once);
        // the same name with DIFFERENT code is a cross-leaf collision of
        // virtual param names, disambiguated by a per-leaf suffix.
        let mutable declaredNames : Map<string, string> = Map.empty
        for li in 0 .. leafCgs.Length - 1 do
            let cg = leafCgs.[li]
            if j < cg.Bindings.Length then
                let binding = cg.Bindings.[j]
                for elem in binding.Elements do
                    match Map.tryFind elem.ArrayName streamed with
                    | Some sspec ->
                        // Streamed source in a fused nest: same interception
                        // as the single-leaf nest, with cross-leaf FIBER
                        // DEDUP falling out of the name->code map — two
                        // leaves reading the same source fiber at the same
                        // level produce byte-identical read+wrapper blocks,
                        // emitted once and shared via paramFinalNames.
                        let acc = Map.tryFind (li, elem.ArrayPosition) streamSites |> Option.defaultValue []
                        let (codeLines, fiberBound, acc') = genElementBindingStreamed binding elem sspec acc
                        streamSites <- Map.add (li, elem.ArrayPosition) acc' streamSites
                        let joined = String.concat "\n" codeLines
                        (match fiberBound with
                         | Some fname ->
                             if Map.tryFind fname declaredNames <> Some joined then
                                 for c in codeLines do lines <- lines @ [ind depth + c]
                             declaredNames <- Map.add fname joined declaredNames
                             currentNames <- Map.add (li, elem.ArrayPosition) fname currentNames
                             let pfn = Map.find li paramFinalNames
                             paramFinalNames <- Map.add li (Map.add elem.ParamVarId fname pfn) paramFinalNames
                         | None ->
                             // Intermediate level (fused site decode decls):
                             // dedup identical blocks across leaves by content.
                             if joined <> "" && Map.tryFind joined declaredNames <> Some joined then
                                 for c in codeLines do lines <- lines @ [ind depth + c]
                                 declaredNames <- Map.add joined joined declaredNames)
                    | None ->
                        let cur =
                            Map.tryFind (li, elem.ArrayPosition) currentNames
                            |> Option.defaultValue elem.ArrayName
                        let (code0, name0) = genElementBindingNew binding elem cur
                        let (code, newName) =
                            match Map.tryFind name0 declaredNames with
                            | Some prior when prior <> code0 ->
                                let renamed = { elem with ParamName = sprintf "%s__l%d" elem.ParamName li }
                                genElementBindingNew binding renamed cur
                            | _ -> (code0, name0)
                        if Map.tryFind newName declaredNames <> Some code then
                            lines <- lines @ [ind depth + code]
                        declaredNames <- Map.add newName code declaredNames
                        currentNames <- Map.add (li, elem.ArrayPosition) newName currentNames
                        let pfn = Map.find li paramFinalNames
                        paramFinalNames <- Map.add li (Map.add elem.ParamVarId newName pfn) paramFinalNames

        // Assignments for the leaves whose nest ENDS at this level (all of
        // their peels are in scope here; deeper levels never see them).
        for li in 0 .. leafCgs.Length - 1 do
            let cg = leafCgs.[li]
            if cg.Bindings.Length = j + 1 then
                let pfn = Map.find li paramFinalNames
                let nameMap = pfn |> Map.fold (fun acc k v -> Map.add k v acc) outerNames
                let nameMap = cg.Captures |> List.fold (fun acc c -> Map.add c.Id c.Name acc) nameMap
                let rr = genKernelExprWithReynolds cg.KernelExpr cg.KernelParams cg.HasReynolds cg.IsAntisymmetric nameMap pfn
                if cg.HasReynolds && rr.UniqueTerms < rr.TotalPerms then
                    lines <- lines @ [ind depth + sprintf "// Reynolds: %d/%d perms unique (dedup %dx)" rr.UniqueTerms rr.TotalPerms (rr.TotalPerms / max 1 rr.UniqueTerms)]
                // Cell write for compute; accumulate-through-wrapper for the
                // fused fold (scalar accumulators, no cell indexing).
                let assign =
                    match cg.FoldWrapper with
                    | Some wname -> sprintf "%s = %s(%s, %s);" cg.OutputName wname cg.OutputName rr.CppExpr
                    | None ->
                        let outputIdx =
                            cg.Bindings |> List.map (fun b -> sprintf "[%s]" b.IndexName) |> String.concat ""
                        sprintf "%s%s = %s;" cg.OutputName outputIdx rr.CppExpr
                lines <- lines @ [ind depth + assign]

    // Close all loops
    for _ in primary.Bindings do
        depth <- depth - 1
        lines <- lines @ [ind depth + "}"]

    lines

/// The ordinary (no streamed sources) fused-nest emitter.
let genFusedLoopNest (leafCgs: LoopNestCodeGen list) (outerNames: Map<int, string>) (indent: int) (hostParallel: bool) (mpiSlabVar: string option) : string list =
    genFusedLoopNestStreamed Map.empty leafCgs outerNames indent hostParallel mpiSlabVar


/// Generate C++ code for inline object_for application (e.g., A [+] B)
let genObjectForApplication (ctx: CodeGenContext) (name: string) (objInfo: ObjectForInfo) (arrays: IRExpr list) (builder: IRBuilder) : string list =
    let ind = indentStr ctx
    
    // Get array names
    let arrayNames = arrays |> List.mapi (fun i arr ->
        match arr with
        | IRVar (id, _) -> Map.tryFind id ctx.VarNames |> Option.defaultValue (sprintf "arr%d" i)
        | _ -> sprintf "arr%d" i)
    
    // Resolve the kernel via `resolveCallable`. The wrapper closure
    // takes the kernel's regular params (elementwise) and forwards to
    // the lifted function with captures pulled by reference. Loop
    // bodies invoke the wrapper with the per-iteration array slots â€”
    // eliminating the need for intermediate scalar locals.
    match resolveCallable objInfo.Kernel with
    | Some callable when callable.Params.Length = 1 || callable.Params.Length = 2 ->
        let (wrapperCode, wname) = genCallableWrapper name callable
        // Output element type is the kernel's RETURN type: comparison/logical
        // kernels (`A < B`) consume numeric inputs but PRODUCE bool, and
        // array<->scalar broadcast kernels can PROMOTE (`I * 2.5` is
        // int64 -> double â€” storing into an int64 result array would be a
        // float-conversion error under -Werror). Only when the return type
        // isn't a value primitive (unit, unresolved) fall back to the
        // historical param-based inference.
        let elemTypeStr =
            match callable.RetType with
            | IRTScalar et when et <> ETUnit -> primTypeToCpp et
            | _ ->
                callable.Params |> List.tryPick (fun p ->
                    match p.Type with
                    | IRTScalar et -> Some (primTypeToCpp et)
                    | ArrayElem arr -> Some (elemTypeToCpp arr.ElemType)
                    | _ -> None)
                |> Option.defaultValue (match callable.Params with p :: _ -> irTypeToCpp p.Type | [] -> "void")
        // Indent wrapper-emission lines to match surrounding scope.
        let wrapperLines = wrapperCode |> List.map (fun s -> ind + s)
        match objInfo.InputRanks, arrayNames with
        | [1; 1], [arrA; arrB] ->
            // Outer product: result[i][j] = kernel(A[i], B[j])
            let extentsDecl = sprintf "%ssize_t %s_extents[2] = {%s.extents[0], %s.extents[0]};" ind name arrA arrB
            let allocDecl = sprintf "%sArray<%s, 2> %s = { allocate<promote<%s, 2>::type>(%s_extents), %s_extents };" ind elemTypeStr name elemTypeStr name name
            let loopCode = [
                sprintf "%sfor (size_t __i0 = 0; __i0 < %s.extents[0]; __i0++) {" ind arrA
                sprintf "%s    for (size_t __i1 = 0; __i1 < %s.extents[0]; __i1++) {" ind arrB
                sprintf "%s        %s[__i0][__i1] = %s(%s[__i0], %s[__i1]);" ind name wname arrA arrB
                sprintf "%s    }" ind
                sprintf "%s}" ind
            ]
            [extentsDecl; allocDecl; ""] @ wrapperLines @ loopCode

        | [0; 0], [arrA; arrB] ->
            // Elementwise: result[i] = kernel(A[i], B[i])
            let extentsDecl = sprintf "%ssize_t %s_extents[1] = {%s.extents[0]};" ind name arrA
            let allocDecl = sprintf "%sArray<%s, 1> %s = { allocate<promote<%s, 1>::type>(%s_extents), %s_extents };" ind elemTypeStr name elemTypeStr name name
            let loopCode = [
                sprintf "%sfor (size_t __i0 = 0; __i0 < %s.extents[0]; __i0++) {" ind arrA
                sprintf "%s    %s[__i0] = %s(%s[__i0], %s[__i0]);" ind name wname arrA arrB
                sprintf "%s}" ind
            ]
            [extentsDecl; allocDecl; ""] @ wrapperLines @ loopCode

        | [0], [arrA] ->
            // Single-array elementwise map (array<->scalar broadcast):
            // result[i] = kernel(A[i]). The scalar is baked into the 1-param
            // kernel, so only the array is iterated.
            let extentsDecl = sprintf "%ssize_t %s_extents[1] = {%s.extents[0]};" ind name arrA
            let allocDecl = sprintf "%sArray<%s, 1> %s = { allocate<promote<%s, 1>::type>(%s_extents), %s_extents };" ind elemTypeStr name elemTypeStr name name
            let loopCode = [
                sprintf "%sfor (size_t __i0 = 0; __i0 < %s.extents[0]; __i0++) {" ind arrA
                sprintf "%s    %s[__i0] = %s(%s[__i0]);" ind name wname arrA
                sprintf "%s}" ind
            ]
            [extentsDecl; allocDecl; ""] @ wrapperLines @ loopCode

        | _ ->
            // Unsupported configuration
            codegenError ctx ind (sprintf "unsupported object_for configuration for '%s'" name)
    | _ ->
        codegenError ctx ind (sprintf "object_for kernel for '%s' does not resolve to a callable" name)

// ============================================================================
// Binding Generation
// ============================================================================

/// Unroll an IRLet chain into a list of (varId, valueExpr) statements and a final return expression.
/// e.g., IRLet(id1, v1, IRLet(id2, v2, body)) â†’ statements=[(id1,v1), (id2,v2)], return=body
let rec unrollLetChain (expr: IRExpr) : (IRId * IRExpr) list * IRExpr =
    match expr with
    | IRLet (id, value, body) ->
        let (rest, final) = unrollLetChain body
        ((id, value) :: rest, final)
    | _ -> ([], expr)

// ============================================================================
// Recursive Parallel/Fusion Tree Helpers
// ============================================================================

/// Materialize an `IRComposeApply` (slot-inverted compose-apply form)
/// into the named target. Used by:
///   - the IRCompute dispatcher (the primary path: `let r = (o1 >>@ o2) <@> A |> compute`)
///   - parallel/fusion leaf emission (when a compose-apply appears as
///     a `<&>` or `<&!>` leaf alongside canonical applies)
///   - method-composition stage-1 materialization (when a compose-apply
///     is the left of `@>>`)
///
/// Resolves the composition through `ctx.DeferredComputations` to find
/// the underlying `IRComposeObj`, then emits the chained-loop codegen:
/// two stages (one per object in the chain), each running an
/// element-wise loop with the resolved kernel. If both kernels emit as
/// named C++ functions, generates direct call loops; otherwise falls
/// back to per-stage `IRApplyCombinator` materialization via
/// `genApplyCombinator`.
///
/// Does NOT register `name` in the returned context as a binding â€” that
/// is the caller's responsibility (in canonical use, the caller is a
/// `let`-binding handler that calls `addVarName binding.Id name`).
let genComposeApply
    (ctx: CodeGenContext)
    (name: string)
    (info: ComposeApplyInfo)
    (outputType: IRType)
    (builder: IRBuilder)
    : string list * CodeGenContext =
    let ind = indentStr ctx
    let rec resolveDeferred e =
        match e with
        | IRVar (id, _) ->
            match Map.tryFind id ctx.DeferredComputations with
            | Some d -> resolveDeferred d
            | None -> e
        | _ -> e
    let resolvedComposition = resolveDeferred info.Composition
    match resolvedComposition with
    | IRComposeObj (obj1, obj2) ->
        let rObj1 = resolveDeferred obj1
        let rObj2 = resolveDeferred obj2
        let kernel1 = match rObj1 with IRObjectFor o -> o.Kernel | _ -> rObj1
        let kernel2 = match rObj2 with IRObjectFor o -> o.Kernel | _ -> rObj2
        let arrays = info.InputArrays

        let kernelName1 = match kernel1 with IRVar (id, _) -> Map.tryFind id ctx.VarNames | _ -> None
        let kernelName2 = match kernel2 with IRVar (id, _) -> Map.tryFind id ctx.VarNames | _ -> None

        let arrName =
            match arrays with
            | [IRVar (id, _)] -> Map.tryFind id ctx.VarNames |> Option.defaultValue "arr0"
            | _ -> "arr0"
        let arrRank =
            match arrays with
            | [a] -> (match inferExprType a with ArrayElem arr -> arrayRank arr | _ -> 1)
            | _ -> 1
        let (elemType, elemTypeErrCode) =
            match arrays with
            | a :: _ ->
                match inferExprType a with
                | ArrayElem arr -> (elemTypeToCpp arr.ElemType, [])
                | IRTScalar et -> (primTypeToCpp et, [])
                | t ->
                    (elemTypeToCpp (IRTScalar ETFloat64),
                     codegenError ctx ind (sprintf ">>@: could not determine input element type (got %A) - likely a typechecker or IR bug" t))
            | [] ->
                (elemTypeToCpp (IRTScalar ETFloat64),
                 codegenError ctx ind ">>@: empty array list - likely an IR-builder bug")

        match kernelName1, kernelName2 with
        | Some k1, Some k2 ->
            // Both kernels are named C++ lambdas - direct call loops
            let s1Name = sprintf "%s__s1" name
            let s1Code = [
                sprintf "%sconst size_t* %s_extents = %s.extents;" ind s1Name arrName
                arrayAlloc { Ind = ind; Elem = elemType; Rank = arrRank; Name = s1Name
                             Symm = "nullptr"; Strict = None; Extents = s1Name + "_extents" }
                forLoop ind "__i0" (arrName + ".extents[0]")
                sprintf "%s    %s[__i0] = %s(%s[__i0]);" ind s1Name k1 arrName
                sprintf "%s}" ind
            ]
            let s2Code = [
                sprintf "%sconst size_t* %s_extents = %s.extents;" ind name s1Name
                arrayAlloc { Ind = ind; Elem = elemType; Rank = arrRank; Name = name
                             Symm = "nullptr"; Strict = None; Extents = name + "_extents" }
                forLoop ind "__i0" (s1Name + ".extents[0]")
                sprintf "%s    %s[__i0] = %s(%s[__i0]);" ind name k2 s1Name
                sprintf "%s}" ind
            ]
            (elemTypeErrCode @ s1Code @ [""] @ s2Code, ctx)
        | _ ->
            // Fallback: inline lambdas - use ApplyInfo per stage.
            // We materialize via direct `genApplyCombinator` calls
            // (rather than constructing IRBindings and going through
            // genBinding) to keep this helper independent of the
            // recursive let-binding group below.
            let s1Name = sprintf "%s__s1" name
            let s1Id = builder.FreshId()
            let s1ElemType : IRType =
                match resolveCallable kernel1 with
                | Some callable -> callable.RetType
                | None -> IRTScalar ETFloat64
            let inputArrayTypes = arrays |> List.map (fun a ->
                match inferExprType a with
                | ArrayElem arr -> arr
                | _ -> defaultArrayType (IRTScalar ETFloat64))
            let totalInputDims = inputArrayTypes |> List.sumBy arrayRank
            let s1Type = mkArrayArrow [for _ in 1..totalInputDims -> defaultIndexType ()] s1ElemType None
            let s1Info = buildSimpleApplyInfo arrays kernel1 s1Type
            let code1 = genApplyCombinator ctx s1Name s1Info builder
            let ctx1 = addVarName s1Id s1Name ctx

            let s2OutputType =
                match outputType with
                | IRTUnit ->
                    match s1Type with
                    | ArrayElem arr -> mkArrayLike { arr with ElemType = s1ElemType }
                    | _ -> mkArrayLike (defaultArrayType s1ElemType)
                | other -> other
            let s2Info = buildSimpleApplyInfo [IRVar(s1Id, s1Type)] kernel2 s2OutputType
            let code2 = genApplyCombinator ctx1 name s2Info builder
            (code1 @ [""] @ code2, ctx1)
    | _ ->
        // Composition didn't resolve to IRComposeObj at codegen time -
        // should be impossible after the IRComposeApply split. Emit
        // codegen-time error rather than silently generating bad code.
        let errCode = codegenError ctx ind (sprintf "IRComposeApply: Composition did not resolve to IRComposeObj (got %A) - IR-builder bug" resolvedComposition)
        (errCode, ctx)

/// Attempt ONE merged loop nest materializing every leaf of a computation
/// combinator tree. This is the <&!> compute path and the opportunistic <&>
/// path: per-leaf output allocation, a single (possibly staggered) nest via
/// genFusedLoopNest, and the flat make_tuple convention. `isMandatory`
/// selects the backend rule: <&!> (true) requires every leaf's backend to be
/// IDENTICAL; <&> (false) lets absence (serial) defer to an explicit sibling.
/// Error carries a diagnosis when the leaves cannot legally share a nest â€”
/// the caller decides (<&!> reports it, <&> falls back to independent nests).
let tryGenMergedCompute (ctx: CodeGenContext) (name: string) (infos: ApplyInfo list) (isMandatory: bool) (builder: IRBuilder) : Result<string list * string * Map<string, string list>, string> =
    let ind = indentStr ctx
    let arrayNamesOf (info: ApplyInfo) =
        info.Arrays |> List.mapi (fun i arr ->
            match arr with
            | IRVar (id, _) -> Map.tryFind id ctx.VarNames |> Option.defaultValue (sprintf "arr%d" i)
            | IRRange _ -> sprintf "__range%d" i
            | IRVirtualReverse _ -> sprintf "__rev%d" i
            | IRBlocked _ -> sprintf "__blk%d" i
            | _ -> sprintf "arr%d" i)
    if infos |> List.exists (fun info -> info.Arrays.IsEmpty) then
        Error (sprintf "no arrays in method_for for fused '%s'" name)
    elif infos |> List.exists (fun info ->
             info.Arrays |> List.exists (fun a ->
                 match a with
                 | IRRange (its, _) -> its |> List.exists (fun ix -> ix.IxKind = IxKCompound)
                 | _ -> false)) then
        // The fused multi-output path allocates outputs through the dense
        // extents machinery and does not materialize a standalone
        // compound_index_t; a compound range here would emit references to
        // an undeclared `__rangeN_cidx`.
        Error "range<CompoundIdx> is not yet supported in fused (multi-output) loop applications; use a single-kernel method_for"
    else
        let leafNames = infos |> List.mapi (fun i _ -> sprintf "%s_%d" name i)
        // Each leaf's nest is built against its OWN arrays â€” allocation
        // extents and kernel-param element bindings both come from here.
        let leafCgs = infos |> List.mapi (fun i info ->
            buildLoopNestCodeGen info (arrayNamesOf info) leafNames.[i] builder)
        let backends = infos |> List.map classifyLeafBackend
        // Host output allocation â€” shared by every backend (cuda/mpi restore
        // the full output on the host too).
        let declCode = leafCgs |> List.mapi (fun i cg ->
            let lname = leafNames.[i]
            let symmVecName = sprintf "%s_symm" lname
            // Pass nullptr DIRECTLY when there's no symmetry â€” a function-local
            // `static constexpr const size_t* X_symm = nullptr` can't be used
            // as a constant template arg under MSVC (C2131; the address of a
            // function-local static isn't a core-constant-expression). Only
            // emit a named decl for the non-empty (real symmetry) case.
            let symmArg =
                if hasRealSymmetry cg.OutputSymmVec then hoistSymmDecl symmVecName cg.OutputSymmVec
                else "nullptr"
            let outputRank = match cg.OutputType with ArrayElem arr -> arrayRank arr | _ -> 0
            let outputElemType = match cg.OutputType with ArrayElem arr -> elemTypeToCpp arr.ElemType | IRTScalar et -> primTypeToCpp et | t -> irTypeToCpp t
            let extentsName = sprintf "%s_extents" lname
            let extentsDecl = sprintf "%ssize_t* %s = new size_t[%d];" ind extentsName outputRank
            let extentsFill =
                cg.Bindings |> List.mapi (fun j b ->
                    match b.Extent with
                    | IRLit (IRLitInt n) -> sprintf "%s%s[%d] = %d;" ind extentsName j n
                    | _ ->
                        match b.FusedRank with
                        | Some d ->
                            let prod = [0 .. d - 1] |> List.map (sprintf "%s.extents[%d]" b.ExtentArrayRef) |> String.concat " * "
                            sprintf "%s%s[%d] = %s;" ind extentsName j prod
                        | None -> sprintf "%s%s[%d] = %s.extents[%d];" ind extentsName j b.ExtentArrayRef b.ExtentDimRef)
            let allocRhs =
                match emitAllocRhs (classifyOutputStorage cg.OutputType)
                          outputElemType outputRank symmArg extentsName with
                | Ok rhs -> rhs
                | Error msg -> sprintf "{ nullptr, %s };\n#error \"%s\"" extentsName msg
            let allocDecl = sprintf "%sArray<%s, %d> %s = %s;"
                                ind outputElemType outputRank lname allocRhs
            [extentsDecl] @ extentsFill @ [allocDecl]) |> List.concat
        let tupleLine = sprintf "%sauto %s = std::make_tuple(%s);" ind name (leafNames |> String.concat ", ")
        let childrenMap = Map.ofList [name, leafNames]
        let wrap body = (declCode @ [""] @ body @ [""] @ [tupleLine], name, childrenMap)
        // Device paths emit their OWN output allocation inline (host restore
        // buffers), so they must NOT prepend the host declCode.
        let wrapDevice body = (body @ [""] @ [tupleLine], name, childrenMap)
        let staggered =
            let deepest = leafCgs |> List.map (fun cg -> cg.Bindings.Length) |> List.max
            leafCgs |> List.exists (fun cg -> cg.Bindings.Length <> deepest)

        // Loop structure (bounds/triangularity) must agree regardless of
        // backend. THEN the per-level backend must agree at each shared level:
        // shared levels are one physical header, so one backend.
        checkMergeCompatible leafCgs |> Result.bind (fun _primary ->
            let backendName = function
                | BkSerial -> "serial" | BkOmp -> "omp"
                | BkCuda _ -> "cuda" | BkMpi -> "mpi"
            match backends with
            // ---- All host (serial/omp): merged host nest --------------------
            | _ when backends |> List.forall isHostBackend ->
                let anyOmp = backends |> List.exists (function BkOmp -> true | _ -> false)
                let allOmp = backends |> List.forall (function BkOmp -> true | _ -> false)
                let allSerial = backends |> List.forall (function BkSerial -> true | _ -> false)
                let hostParallelR =
                    if isMandatory then
                        // <&!>: every leaf's backend must be identical.
                        if allOmp then Ok true
                        elif allSerial then Ok false
                        else Error "mixed serial/omp leaves under <&!> â€” mandatory fusion needs one backend at the shared level; annotate every leaf the same or use <&>"
                    else
                        // <&>: absence (serial) defers to an explicit omp sibling.
                        Ok anyOmp
                hostParallelR |> Result.map (fun hostParallel ->
                    let (sm, sp) = streamedNestSetup ctx.StreamedArrays ind leafCgs
                    wrap (sp @ genFusedLoopNestStreamed sm leafCgs ctx.VarNames ctx.Indent hostParallel None))
            // ---- All cuda: device co-fusion --------------------------------
            | _ when backends |> List.forall (function BkCuda _ -> true | _ -> false) ->
                let blockSizes = backends |> List.choose (function BkCuda b -> Some b | _ -> None) |> List.distinct
                if blockSizes.Length > 1 then
                    Error "cuda leaves request different block sizes â€” a shared launch needs one block size; unify the cuda(block: N) clauses or force separately"
                elif staggered then
                    // A flat thread grid over the deepest leaf's cardinality
                    // would redundantly (racily) re-write the shallow leaves'
                    // cells; correct staggered-device fusion needs guarded
                    // writes â€” a separate design. Reject with steering.
                    Error "cuda co-fusion of DIFFERENT arities (staggered nest) is not supported yet â€” give the leaves equal arity or force each with |> compute"
                else
                    match genCudaCoFusion leafCgs leafNames name blockSizes.Head with
                    | Some inlineLines -> Ok (wrapDevice inlineLines)
                    | None -> Error "cuda co-fusion requires the leaves to be rectangular, non-Reynolds, boundary-safe, and share the same input arrays â€” force the leaves separately with |> compute"
            // ---- All mpi: domain co-fusion ---------------------------------
            // ONE outer-row slab decomposition SHARED by every leaf: each rank
            // runs the merged (possibly staggered) nest over its [lo, hi) rows,
            // then each leaf's output â€” a contiguous outer-row pool slab â€” is
            // restored on all ranks by its own Allgatherv. v1 scope: all leaves
            // dense rectangular (triangular/simplicial co-decomposition later).
            | _ when backends |> List.forall (function BkMpi -> true | _ -> false) ->
                let primaryCg = leafCgs |> List.maxBy (fun cg -> cg.Bindings.Length)
                let ineligible =
                    List.zip leafCgs (leafCgs |> List.map classifyMpiShape)
                    |> List.tryPick (fun (cg, shape) ->
                        match shape with
                        | MpiDense -> None
                        | MpiSimplicial -> Some (sprintf "leaf '%s' is triangular; mpi co-fusion decomposes dense rectangular leaves only (v1)" cg.OutputName)
                        | MpiIneligible r -> Some (sprintf "leaf '%s': %s" cg.OutputName r))
                match ineligible with
                | Some reason -> Error reason
                | None ->
                // Hybrid mpi+omp co-fusion: the shared slab loop is ALSO the
                // (single) omp-parallel region when the leaves' inner backend
                // is omp. Leaf bindings' IsParallel carries the kernel's omp
                // opt-in; the <&!>/<&> agreement rule mirrors the host arm.
                let leafOmp = leafCgs |> List.map (fun cg -> cg.Bindings |> List.exists (fun b -> b.IsParallel))
                let hostParallelR =
                    if isMandatory then
                        if leafOmp |> List.forall id then Ok true
                        elif leafOmp |> List.forall (id >> not) then Ok false
                        else Error "mixed serial/omp INNER backends under <&!> mpi co-fusion — the shared slab is one omp region; annotate every leaf `mpi, omp(...)` the same or use <&>"
                    else
                        Ok (leafOmp |> List.exists id)
                hostParallelR |> Result.bind (fun hostParallel ->
                    let outerBound =
                        genLoopBoundExpr (compoundArrayNamesOf primaryCg.Bindings) (List.head primaryCg.Bindings)
                    // Shared row-slab bounds (balanced split; P > n â†’ empty slabs).
                    let prologue =
                        [ sprintf "%ssize_t __blade_mpi_n_%s = %s;" ind name outerBound
                          sprintf "%ssize_t __blade_mpi_q_%s = __blade_mpi_n_%s / (size_t)__blade_mpi_size;" ind name name
                          sprintf "%ssize_t __blade_mpi_r_%s = __blade_mpi_n_%s %% (size_t)__blade_mpi_size;" ind name name
                          sprintf "%ssize_t __blade_mpi_lo_%s = (size_t)__blade_mpi_rank * __blade_mpi_q_%s + ((size_t)__blade_mpi_rank < __blade_mpi_r_%s ? (size_t)__blade_mpi_rank : __blade_mpi_r_%s);" ind name name name name
                          sprintf "%ssize_t __blade_mpi_hi_%s = __blade_mpi_lo_%s + __blade_mpi_q_%s + ((size_t)__blade_mpi_rank < __blade_mpi_r_%s ? 1 : 0);" ind name name name name ]
                    let (sm, sp) = streamedNestSetup ctx.StreamedArrays ind leafCgs
                    let nest = sp @ genFusedLoopNestStreamed sm leafCgs ctx.VarNames ctx.Indent hostParallel (Some name)
                    // Per-leaf Allgatherv: leaf k's pool holds a contiguous slab
                    // [lo*inner_k, hi*inner_k) of outer rows (inner_k = product of
                    // its non-outer extents), so MPI_IN_PLACE reassembles it.
                    let gathers =
                        leafCgs |> List.mapi (fun k cg ->
                            let lname = leafNames.[k]
                            let outputRank = match cg.OutputType with ArrayElem arr -> arrayRank arr | _ -> 1
                            let outElemCpp = match cg.OutputType with ArrayElem arr -> elemTypeToCpp arr.ElemType | _ -> "double"
                            let dtype =
                                match cg.OutputType with
                                | ArrayElem at -> (match at.ElemType with AnyPrimElem et -> mpiDatatypeOf et | _ -> None)
                                | _ -> None
                                |> Option.defaultValue "MPI_DOUBLE"
                            let extentsName = sprintf "%s_extents" lname
                            let innerProd =
                                if outputRank <= 1 then "1"
                                else [1 .. outputRank - 1] |> List.map (fun i -> sprintf "%s[%d]" extentsName i) |> String.concat " * "
                            [ sprintf "%s{ // MPI: restore full %s on all ranks" ind lname
                              sprintf "%s    %s* __blade_mpi_pool = nested_array_utilities::pool_base(%s.data);" ind outElemCpp lname
                              sprintf "%s    size_t __blade_mpi_inner = %s;" ind innerProd
                              sprintf "%s    if (__blade_mpi_n_%s * __blade_mpi_inner > 2147483647ULL) { std::cerr << \"error[BL8004]: element count exceeds int32 range (rank \" << __blade_mpi_rank << \")\" << std::endl; MPI_Abort(MPI_COMM_WORLD, 13); }" ind name
                              sprintf "%s    int* __blade_mpi_counts = new int[__blade_mpi_size];" ind
                              sprintf "%s    int* __blade_mpi_displs = new int[__blade_mpi_size];" ind
                              sprintf "%s    for (int __r = 0; __r < __blade_mpi_size; __r++) {" ind
                              sprintf "%s        size_t __lo = (size_t)__r * __blade_mpi_q_%s + ((size_t)__r < __blade_mpi_r_%s ? (size_t)__r : __blade_mpi_r_%s);" ind name name name
                              sprintf "%s        size_t __hi = __lo + __blade_mpi_q_%s + ((size_t)__r < __blade_mpi_r_%s ? 1 : 0);" ind name name
                              sprintf "%s        __blade_mpi_counts[__r] = (int)((__hi - __lo) * __blade_mpi_inner);" ind
                              sprintf "%s        __blade_mpi_displs[__r] = (int)(__lo * __blade_mpi_inner);" ind
                              sprintf "%s    }" ind
                              sprintf "%s    MPI_Allgatherv(MPI_IN_PLACE, 0, MPI_DATATYPE_NULL, __blade_mpi_pool, __blade_mpi_counts, __blade_mpi_displs, %s, MPI_COMM_WORLD);" ind dtype
                              sprintf "%s    delete[] __blade_mpi_counts; delete[] __blade_mpi_displs;" ind
                              sprintf "%s}" ind ]) |> List.concat
                    Ok (wrap (prologue @ [""] @ nest @ [""] @ gathers)))
            // ---- Mixed backends: cannot share one nest ----------------------
            | _ ->
                let names = backends |> List.map backendName |> List.distinct |> String.concat ", "
                Error (sprintf "leaves request different execution backends (%s) â€” a fused nest has one backend per shared level; force the differing leaves separately with |> compute" names))

/// <&> SOFT JOIN over independent cuda leaves that cannot share one nest:
/// each leaf keeps its OWN kernel (own block size, arity, inputs) and the
/// launches are split into a begin pass (H2D + async launch) and an end pass
/// (sync + D2H), with leaves assigned round-robin across visible devices
/// INSIDE the .cu wrappers (leaf % deviceCount â€” the host half never touches
/// the CUDA API, so the g++ split build needs no cudart link). One device =>
/// the default stream serializes the leaves (correct, no overlap â€” exactly
/// the soft join's "run the rest in serial"); multiple devices => the begin
/// pass genuinely overlaps them. Returns None when any leaf is not
/// device-eligible (caller falls back to fully independent nests; kernels
/// already appended for earlier leaves become dead-but-harmless .cu defs).
let tryGenCudaSoftJoin (ctx: CodeGenContext) (name: string) (infos: ApplyInfo list) (builder: IRBuilder) : (string list * string * Map<string, string list>) option =
    let backends = infos |> List.map classifyLeafBackend
    if infos.Length < 2
       || not (backends |> List.forall (function BkCuda _ -> true | _ -> false))
       || infos |> List.exists (fun info -> info.Arrays.IsEmpty) then None
    else
    let arrayNamesOf (info: ApplyInfo) =
        info.Arrays |> List.mapi (fun i arr ->
            match arr with
            | IRVar (id, _) -> Map.tryFind id ctx.VarNames |> Option.defaultValue (sprintf "arr%d" i)
            | IRRange _ -> sprintf "__range%d" i
            | IRVirtualReverse _ -> sprintf "__rev%d" i
            | IRBlocked _ -> sprintf "__blk%d" i
            | _ -> sprintf "arr%d" i)
    let leafNames = infos |> List.mapi (fun i _ -> sprintf "%s_%d" name i)
    let leafCgs = infos |> List.mapi (fun i info ->
        buildLoopNestCodeGen info (arrayNamesOf info) leafNames.[i] builder)
    let blocks = backends |> List.map (function BkCuda b -> b | _ -> 256)
    // Per-leaf emission in split mode: simplicial first, then rectangular
    // (the single-kernel dispatch order). Each returns the host output
    // allocation lines; the wrapper takes ONLY the arrays it actually reads
    // (simplicial = the first input; rectangular = all inputs).
    let pieces =
        List.zip3 leafCgs leafNames blocks
        |> List.map (fun (cg, lname, bs) ->
            match genCudaKernelSimplicial false true cg lname bs with
            | Some alloc -> Some (alloc, [List.head cg.InputArrayNames], lname)
            | None ->
                genCudaKernel true cg lname bs
                |> Option.map (fun alloc -> (alloc, cg.InputArrayNames, lname)))
    if pieces |> List.exists Option.isNone then None
    else
    let pieces = pieces |> List.map Option.get
    let header =
        [ sprintf "    // <&> soft join: %d independent cuda kernels. Begin pass launches" pieces.Length
          "    // async round-robin over devices (inside the wrappers); end pass syncs." ]
    let allocs = pieces |> List.collect (fun (alloc, _, _) -> alloc)
    let begins =
        pieces |> List.mapi (fun k (_, args, lname) ->
            let argStr = args |> List.map (fun n -> sprintf "pool_base(%s.data)" n) |> String.concat ", "
            sprintf "    __launch_%s_begin(%s, %d);" (sanitizeCppName lname) argStr k)
    let ends =
        pieces |> List.map (fun (_, _, lname) ->
            sprintf "    __launch_%s_end(pool_base(%s.data));" (sanitizeCppName lname) lname)
    let tupleLine = sprintf "    auto %s = std::make_tuple(%s);" name (leafNames |> String.concat ", ")
    Some (header @ allocs @ begins @ ends @ [""; tupleLine], name, Map.ofList [name, leafNames])

/// Recursively generate code for a parallel composition tree (<&>).
/// When every leaf is an unforced loop application whose loop structures can
/// legally share one nest, the leaves are MERGED into a single (possibly
/// staggered) nest â€” <&> means "fuse when legal" â€” so towers like
/// mean <&> cov <&> skew stream the shared arrays from one load. Otherwise
/// each leaf gets its own independent loop nest.
/// Returns (code_lines, result_variable_name, tupleChildrenMap).
/// tupleChildrenMap tracks pair structure for nested tuple destructuring.
let rec genParallelTree (ctx: CodeGenContext) (name: string) (expr: IRExpr) (builder: IRBuilder) : string list * string * Map<string, string list> =
    let ind = indentStr ctx
    // Collect all leaf expressions from the parallel/fusion tree
    let rec collectLeaves (e: IRExpr) : IRExpr list =
        match e with
        | IRParallel (left, right, _) | IRFusion (left, right) ->
            collectLeaves left @ collectLeaves right
        | IRVar (id, _) ->
            match Map.tryFind id ctx.DeferredComputations with
            | Some deferred -> collectLeaves deferred
            | None -> [e]
        | _ -> [e]
    let leaves = collectLeaves expr
    match leaves with
    | [single] ->
        // Single leaf â€” generate directly, no tuple wrapping
        match single with
        | IRApplyCombinator info ->
            let code = genApplyCombinator ctx name info builder
            (code, name, Map.empty)
        | IRComposeApply info ->
            let (code, _) = genComposeApply ctx name info info.OutputType builder
            (code, name, Map.empty)
        | IRVar (id, _) ->
            let existingName = Map.tryFind id ctx.VarNames |> Option.defaultValue name
            ([], existingName, Map.empty)
        | _ ->
            let code = genScalarBinding ctx name single (inferExprType single)
            (code, name, Map.empty)
    | _ ->
        // Opportunistic fusion: all leaves unforced loop applications with
        // mergeable loop structures -> one shared nest (see doc comment).
        let mergeInfos = leaves |> List.choose (function IRApplyCombinator info -> Some info | _ -> None)
        let merged =
            if mergeInfos.Length = leaves.Length && mergeInfos.Length >= 2 then
                match tryGenMergedCompute ctx name mergeInfos false builder with
                | Ok result -> Some result
                | Error _ ->
                    // <&> is a SOFT join: leaves that cannot share one nest
                    // still run. Independent cuda leaves get the multi-device
                    // begin/end driver; anything else falls through to the
                    // fully independent per-leaf nests below.
                    tryGenCudaSoftJoin ctx name mergeInfos builder
            else None
        match merged with
        | Some result -> result
        | None ->
        // Multiple leaves â€” generate each, assemble flat tuple
        let leafNames = leaves |> List.mapi (fun i _ -> sprintf "%s_%d" name i)
        let allCode =
            (leaves, leafNames) ||> List.map2 (fun leaf leafName ->
                match leaf with
                | IRApplyCombinator info ->
                    genApplyCombinator ctx leafName info builder @ [""]
                | IRComposeApply info ->
                    let (code, _) = genComposeApply ctx leafName info info.OutputType builder
                    code @ [""]
                | IRVar (id, _) ->
                    let existingName = Map.tryFind id ctx.VarNames |> Option.defaultValue leafName
                    if existingName <> leafName then
                        [sprintf "%sauto& %s = %s;" ind leafName existingName; ""]
                    else []
                | _ ->
                    genScalarBinding ctx leafName leaf (inferExprType leaf) @ [""])
            |> List.concat
        let tupleLine = sprintf "%sauto %s = std::make_tuple(%s);" ind name (leafNames |> String.concat ", ")
        let childMap = Map.ofList [name, leafNames]
        (allCode @ [tupleLine], name, childMap)

/// Collect all leaf expressions from a fusion tree in left-to-right order.
let rec collectFusionLeaves (expr: IRExpr) : IRExpr list =
    match expr with
    | IRFusion (left, right) -> collectFusionLeaves left @ collectFusionLeaves right
    | _ -> [expr]

/// Build nested std::make_pair expression matching the tree structure of an IRFusion/IRParallel.
/// Consumes names from the list in left-to-right order.
let rec buildPairTree (expr: IRExpr) (names: string list) : string * string list =
    match expr with
    | IRFusion (left, right) | IRParallel (left, right, _) ->
        let (leftStr, names') = buildPairTree left names
        let (rightStr, names'') = buildPairTree right names'
        (sprintf "std::make_pair(%s, %s)" leftStr rightStr, names'')
    | _ ->
        match names with
        | n :: rest -> (n, rest)
        | [] -> (exprError "internal: no names left in buildPairTree", [])

/// Generate code for N-way mandatory fusion (<&!>).
/// Collects all leaf ApplyCombinators, generates a single fused loop nest,
/// then builds nested pair tree for the result.
/// Build named pair tree from an IRExpr tree structure and a list of leaf names.
/// Generates named intermediate std::make_pair variables for each internal node.
/// Uses __p_ prefix for intermediates to avoid collision with leaf names.
/// Returns (code_lines, result_name, tupleChildrenMap).
let genFusionTree (ctx: CodeGenContext) (name: string) (expr: IRExpr) (builder: IRBuilder) : string list * string * Map<string, string list> =
    let ind = indentStr ctx
    let rawLeaves = collectFusionLeaves expr
    
    // Resolve IRVar leaves through DeferredComputations
    let leaves = rawLeaves |> List.map (fun leaf ->
        match leaf with
        | IRVar (id, _) ->
            match Map.tryFind id ctx.DeferredComputations with
            | Some deferred -> deferred
            | None -> leaf
        | _ -> leaf)
    
    // Extract ApplyInfo from each leaf
    let infos = leaves |> List.choose (fun e ->
        match e with
        | IRApplyCombinator info -> Some info
        | _ -> None)
    
    if infos.Length < 2 || infos.Length <> leaves.Length then
        // Not all leaves are ApplyCombinators â€” fall back to parallel generation
        genParallelTree ctx name expr builder
    else
        // <&!> is MANDATORY fusion: incompatible loop structures are a loud
        // codegen diagnostic, never a silent fallback to independent nests
        // (use <&> for that).
        match tryGenMergedCompute ctx name infos true builder with
        | Ok result -> result
        | Error reason ->
            (codegenError ctx ind (sprintf "<&!> cannot fuse these computations into one loop nest: %s (use <&> to allow independent loops)" reason), name, Map.empty)

/// Compute the number of flat leaves for a type (recursing into nested tuples).
let rec tupleLeafCount (ty: IRType) : int =
    match ty with
    | IRTTuple ts -> ts |> List.sumBy tupleLeafCount
    | _ -> 1

/// For a tuple type, compute the flat child range [start, start+count) for each top-level element.
/// E.g. ((Î±,Î²), Î³) â†’ [(0, 2); (2, 1)] meaning element 0 spans flat indices 0..1, element 1 is flat index 2.
let tupleLeafRanges (ty: IRType) : (int * int) list =
    match ty with
    | IRTTuple ts ->
        let mutable offset = 0
        ts |> List.map (fun t ->
            let count = tupleLeafCount t
            let range = (offset, count)
            offset <- offset + count
            range)
    | _ -> [(0, 1)]

/// C++-side name for a binding. Anonymous tuple bindings ("_") get a unique
/// synthesized name to avoid C++ redefinition errors. Shared by the binding
/// dispatcher and every per-shape generator in its recursive chain (Â§2.5).
let bindingCppName (binding: IRBinding) : string =
    if binding.Name = "_" then sprintf "__tup_%d" binding.Id else binding.Name

/// Generate C++ code for an IR binding: the DISPATCHER (audit Â§2.5).
/// Each binding shape's emission lives in its own named `genXxxBinding`
/// generator below (same `let rec ... and` chain), so every path is
/// independently findable and testable; this match only destructures the
/// shape and delegates. Arms not yet extracted retain their inline bodies â€”
/// the migration is one generator at a time, gated by the full suite.
/// Under MPI scaffolding, a provider write must run on ONE rank only —
/// every rank executes main() (SPMD), and P processes racing on the same
/// store files would tear them. Rank 0 writes; other ranks skip. (Data is
/// identical on all ranks: distributed reads Allgatherv-restore, and mpi
/// kernel outputs are restored the same way.)
let guardProviderWrite (ind: string) (lines: string list) : string list =
    if mpiProgramOn () then
        [ ind + "if (__blade_mpi_rank == 0) { // provider write: rank 0 only (SPMD)" ]
        @ (lines |> List.map (fun s -> ind + "    " + s))
        @ [ ind + "}" ]
    else
        lines |> List.map (fun s -> ind + s)

/// Copy between a packed (SymIdx/AntisymIdx leading group + dense trailing)
/// array's storage and its canonical flat pool buffer `<flatBase>_flat`
/// (ascending-lex cells x row-major trailing block). Direction:
/// toFlat=false fills the array from the buffer (read materialization);
/// toFlat=true fills the buffer from the array (write flatten).
///
/// SYMMETRIC groups: the allocator's flat pool holds exactly the canonical
/// cells in ascending-lex order (each row stores its diagonal-anchored
/// tail), so the copy is a linear pool_base walk — differentially pinned
/// against linearized_storage's order.
/// ANTISYMMETRIC groups: the unified strict allocator keeps a dead
/// diagonal slot per level (subscript s at level k addresses absolute
/// coordinate ix[k-1] + s), so the host pool is NOT compact — the copy
/// unranks each cell via linearized_storage::antisymmetric::unlinearize
/// and uses diagonal-anchored relative subscripts.
let genPackedPoolCopy (arrTy: IRArrayType) (arrayCpp: string) (flatBase: string) (varName: string) (toFlat: bool) : string list =
    let (lead, trailing) =
        match arrTy.IndexTypes with
        | l :: rest when l.Symmetry <> SymNone && l.Rank >= 2 -> (l, rest)
        | _ -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf "packed pool copy of '%s': expected a leading packed group" varName)))
    if trailing |> List.exists (fun ix -> ix.Symmetry <> SymNone || ix.Rank <> 1) then
        raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf "packed pool copy of '%s': only one leading packed group plus dense trailing dims is supported" varName)))
    let litOf (e: IRExpr) =
        match e with
        | IRLit (IRLitInt n) -> n
        | _ -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf "packed pool copy of '%s' requires literal extents" varName)))
    let n = litOf lead.Extent
    let r = lead.Rank
    let binom (m: int64) (k: int) : int64 =
        if k < 0 || m < int64 k then 0L
        else
            let mutable num = 1L
            let mutable den = 1L
            for i in 0 .. k - 1 do
                num <- num * (m - int64 i)
                den <- den * int64 (i + 1)
            num / den
    let card =
        match lead.Symmetry with
        | SymSymmetric -> binom (n + int64 r - 1L) r
        | SymAntisymmetric -> binom n r
        | s -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf "packed pool copy of '%s': %A groups are not supported" varName s)))
    let trailExts = trailing |> List.map (fun ix -> litOf ix.Extent)
    let trail = trailExts |> List.fold (*) 1L
    match lead.Symmetry with
    | SymSymmetric ->
        // Linear pool copy (canonical cells x trailing, contiguous).
        let total = card * trail
        if toFlat then
            [ sprintf "{ auto* __pc_pool = nested_array_utilities::pool_base(%s.data);" arrayCpp
              sprintf "  for (size_t __pc_i = 0; __pc_i < %d; __pc_i++) { %s_flat[__pc_i] = __pc_pool[__pc_i]; } }" total flatBase ]
        else
            [ sprintf "{ auto* __pc_pool = nested_array_utilities::pool_base(%s.data);" arrayCpp
              sprintf "  for (size_t __pc_i = 0; __pc_i < %d; __pc_i++) { __pc_pool[__pc_i] = %s_flat[__pc_i]; } }" total flatBase ]
    | _ ->
        // Antisymmetric: unrank + relative subscripts.
        let groupSubs =
            [ for k in 0 .. r - 1 ->
                if k = 0 then sprintf "[__pc_ix[0]]"
                else sprintf "[__pc_ix[%d] - __pc_ix[%d]]" k (k - 1) ]
            |> String.concat ""
        let trailVars = trailExts |> List.mapi (fun i _ -> sprintf "__pc_t%d" i)
        let trailSubs = trailVars |> List.map (sprintf "[%s]") |> String.concat ""
        let trailIdx =
            if trailVars.IsEmpty then "0"
            else
                let mutable acc = trailVars.[0]
                for i in 1 .. trailVars.Length - 1 do
                    acc <- sprintf "(%s) * %d + %s" acc trailExts.[i] trailVars.[i]
                acc
        let flatIdx = sprintf "__pc_f * %d + %s" trail trailIdx
        let assign =
            if toFlat then sprintf "%s_flat[%s] = %s%s%s;" flatBase flatIdx arrayCpp groupSubs trailSubs
            else sprintf "%s%s%s = %s_flat[%s];" arrayCpp groupSubs trailSubs flatBase flatIdx
        let trailOpen =
            trailVars |> List.mapi (fun i v ->
                sprintf "%sfor (size_t %s = 0; %s < %d; %s++) {" (String.replicate (i + 1) "    ") v v trailExts.[i] v)
        let trailClose =
            [ for i in trailVars.Length - 1 .. -1 .. 0 -> String.replicate (i + 1) "    " + "}" ]
        [ sprintf "for (size_t __pc_f = 0; __pc_f < %d; __pc_f++) {" card
          sprintf "    auto __pc_ix = linearized_storage::antisymmetric::unlinearize<%d>(__pc_f, %dUL);" r n ]
        @ trailOpen
        @ [ String.replicate (trailVars.Length + 1) "    " + assign ]
        @ trailClose
        @ [ "}" ]

let rec genBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding

    match binding.Value with
    | _ when Map.containsKey binding.Id ctx.ProviderReads ->
        genProviderReadBinding ctx binding builder
    | _ when Map.containsKey binding.Id ctx.ProviderWrites ->
        genProviderWriteBinding ctx binding builder
    | _ when Map.containsKey binding.Id ctx.RandomInits ->
        match ctx.RandomInits.[binding.Id] with
        | RandGen _ -> genRandGenBinding ctx binding builder
        | FillModulus _ -> genRandomInitBinding ctx binding builder
    | _ when Map.containsKey binding.Id ctx.CompoundInits ->
        genCompoundInitBinding ctx binding builder 
    | IRMask (arrExpr, predExpr) ->
        genMaskBinding ctx binding builder arrExpr predExpr
    | IRIntersect (aExpr, bExpr) ->
        genIntersectBinding ctx binding builder aExpr bExpr
    | IRUnion (aExpr, bExpr) ->
        genUnionBinding ctx binding builder aExpr bExpr
    | IRUnique arrExpr ->
        genUniqueBinding ctx binding builder arrExpr
    | IRGroupKeys keys ->
        genGroupKeysBinding ctx binding builder keys
    | IRGroupBy (vals, gk) ->
        genGroupByBinding ctx binding builder vals gk
    | IRSort (arrExpr, keyExpr) ->
        genSortBinding ctx binding builder arrExpr keyExpr
    | IRTranspose (arrExpr, d1, d2) ->
        genTransposeBinding ctx binding builder arrExpr d1 d2
    | IRDecompact (arrExpr, d) ->
        genDecompactBinding ctx binding builder arrExpr d
    | IRArrayNegate arrExpr | IRArrayConjugate arrExpr ->
        genArrayNegateConjugateBinding ctx binding builder arrExpr
    | IRGram (_, _, _) ->
        genGramBinding ctx binding builder 
    | IRReduce (arrExpr, kernelExpr, initExpr) ->
        genReduceBinding ctx binding builder arrExpr kernelExpr initExpr
    | IRReduceCompute (compExpr, kernelExpr, seedExpr) ->
        genReduceComputeBinding ctx binding builder compExpr kernelExpr seedExpr
    | IRProdSum _ ->
        // Scalar result; the expression renderer emits the fused-loop IIFE.
        let code = genScalarBinding ctx name binding.Value binding.Type
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')
    | IRArrayLit (elements, arrType) ->
        let code = genArrayLiteral ctx name elements arrType
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')
    
    | IRApplyCombinator info ->
        // Defer: computation not materialized until |> compute or combinator forces it
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred computation>" ind name], ctx')

    | IRComposeApply _ ->
        // Defer: compose-apply is also a lazy computation; materialized
        // when |> compute reaches it (or a combinator forces it).
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred compose-apply>" ind name], ctx')
    
    | IRParallel _ | IRFusion _ ->
        // Defer: computation combinator not materialized until |> compute
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred computation combinator>" ind name], ctx')
    
    | IRFunctorMap _ ->
        // Defer: functor map not materialized until |> compute
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred functor map>" ind name], ctx')
    
    | IRZip _ ->
        // Defer: zip is a lazy array combinator, absorbed by method_for or materialized by |> compute
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred zip>" ind name], ctx')
    
    | IRChoice (left, right) ->
        genChoiceBinding ctx binding builder left right
    | IRFallback (left, right) ->
        genFallbackBinding ctx binding builder left right
    | IRGuard (_, body) ->
        genGuardBinding ctx binding builder body
    | IRSequence elems ->
        genSequenceBinding ctx binding builder elems
    | IRCompute inner ->
        genComputeBinding ctx binding builder inner
    | IRMethodFor _ ->
        // method_for creates a loop object - no runtime code needed
        // Just track the variable name for later use
        let ctx' = addVarName binding.Id name ctx
        ([sprintf "%s// %s = method_for(...) [loop object]" ind name], ctx')
    
    | IRObjectFor _ ->
        // object_for creates a loop object - no runtime code needed
        let ctx' = addVarName binding.Id name ctx
        ([sprintf "%s// %s = object_for(...) [loop object]" ind name], ctx')
    
    | IRApp (IRObjectFor objInfo, args, _) ->
        // Inline application of object_for - need to expand to loop nest
        // This handles cases like: let added = A [+] B
        // Convert to an ApplyCombinator-like structure and generate
        let arrays =
            match args with
            | [IRTuple elems] -> elems
            | _ -> args
        let code = genObjectForApplication ctx name objInfo arrays builder
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')
    
    | IRStructLit _ ->
        // Struct construction â€” where-constraint guards are separate
        // IRConstraintCheck bindings synthesized by the checker.
        let code = genScalarBinding ctx name binding.Value binding.Type
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')
    
    | IRTupleProj (parentExpr, projIdx, isFlat) ->
        genTupleProjBinding ctx binding builder parentExpr projIdx isFlat
    | IRVar (srcId, _) ->
        genVarAliasBinding ctx binding builder srcId
    | IRBind (comp, cont) ->
        genBindChainBinding ctx binding builder comp cont
    | IRTuple _ | IRComplex _ | IRFieldAccess _ | IRLit _ | IRBinOp _ | IRUnaryOp _ | IRIf _ | IRApp _ | IRParam _ | IRMatch _
    | IRPure _ | IRIndex _ | IRExtent _ | IRContains _ ->
        genScalarExprBinding ctx binding builder
    
    | IRCompose _ ->
        // Function composition: uses generic lambdas (auto... args)
        let valueStr = exprToCppCtx ctx binding.Value
        let code = [sprintf "%sauto %s = %s;" ind name valueStr]
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')

    | IRComposeObj _ ->
        // Defer: ObjectLoop composition, materialized when applied via <@>
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred compose_obj>" ind name], ctx')

    | IRComposeMeth _ ->
        // Defer: computation composition, materialized when |> compute
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred compose_meth>" ind name], ctx')
    
    | IRLet _ ->
        genLetChainBinding ctx binding builder 
    | IRAssign _ ->
        // Assignment expression: generate as statement
        let code = [sprintf "%s%s;" ind (exprToCppCtx ctx binding.Value)]
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')

    | IRConstraintCheck (cond, message, span) ->
        // Runtime constraint guard â€” the loud-failure idiom (cerr + abort).
        let code =
            [ sprintf "%sif (!(%s)) {" ind (exprToCppCtx ctx cond)
              sprintf "%s    blade_rt::panic(\"BL8001\", \"%s\", %s);" ind message (panicSpanArgs span)
              sprintf "%s}" ind ]
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')

    | IRForRange (vid, lo, hi, body) ->
        genForRangeBinding ctx binding builder vid lo hi body
    | other ->
        let ctx' = addVarName binding.Id name ctx
        let nodeType = other.GetType().Name
        (codegenError ctx ind (sprintf "unsupported expression for binding '%s' (IR node: %s)" name nodeType), ctx')

// ============================================================================
// Module Generation
// ============================================================================

/// Generate a function body as a list of C++ statements.
/// Unrolls IRLet chains into sequential variable declarations with a final return.

and genScalarExprBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Check if it's a tuple of deferred computations
    match binding.Value with
    | IRTuple elems when elems |> List.forall (fun e ->
        match e with
        | IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRFunctorMap _ -> true
        | IRVar (id, _) -> Map.containsKey id ctx.DeferredComputations
        | _ -> false) ->
        // All elements are computations â€” defer the whole tuple
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred computation tuple>" ind name], ctx')
    | IRFieldAccess _ when (match binding.Type with ArrayElem _ -> true | _ -> false) ->
        // Struct field of array type: the field itself is already an
        // Array<T,N> / Ragged<T> wrapper (per genStructDef field
        // rendering). Assigning it to a wrapper-typed binding copies
        // the wrapper, which carries its shape via .extents. No
        // companion alias needed.
        let dataCode = genScalarBinding ctx name binding.Value binding.Type
        let ctx' = addVarName binding.Id name ctx
        (dataCode, ctx')
    | IRIndex (arrExpr, indices, identity) when (match arrExpr with
                                                 | IRVar (id, _) -> Map.containsKey id ctx.DeferredComputations
                                                 | IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _
                                                 | IRFunctorMap _ | IRComposeMeth _ | IRBind _ | IRCompute _ -> true
                                                 | _ -> false) ->
        // Positional read whose base array is a still-unforced computation
        // (e.g. the PPL formers' row slices `let __ppl_row_A_i = A(i)` over a
        // COMPUTED source array): the emitted C++ indexes the array by NAME
        // (`A.data[i]`), so the producer must be materialized in scope first —
        // the same contract the rearrangement combinators enforce via the
        // shared forceDeferredArrayInput helper.
        let (forceCode, ctx, arrExpr') = forceDeferredArrayInput ctx builder (sprintf "%s__arr" name) arrExpr
        let code = genScalarBinding ctx name (IRIndex (arrExpr', indices, identity)) binding.Type
        let ctx' = addVarName binding.Id name ctx
        (forceCode @ code, ctx')
    | _ ->
        // Scalar expressions including tuples, field access, match, bind, pure
        let code = genScalarBinding ctx name binding.Value binding.Type
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')

and genGroupKeysBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (keys: IRExpr list) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // group_keys: build CSR offsets + permutation from a key array.
    //
    // Three cases, dispatched on (ngroupsOpt, enumValuesOpt) from the
    // typecheck-derived IRTGroupKeys:
    //   Case 1 â€” positional buckets (Idx<N> keys): ngroups known at
    //     compile time, keys are integer bucket indices in [0, N).
    //     Stack-allocated counts/offsets/fill (sized at compile time).
    //   Case 2 â€” EnumIdx reverse lookup: ngroups known at compile
    //     time, plus an explicit list of admissible key values (ints
    //     or strings). Emits a __bucket(__v) lambda that maps each
    //     key to its position in the values list.
    //   Case 3 â€” dynamic discovery: ngroups not known at compile
    //     time. Builds key â†’ bucket-index map (std::unordered_map)
    //     in first-occurrence order, then reuses the map for counts
    //     and permutation. All sizing arrays are heap-allocated.
    //
    // C++ ABI emitted by all three cases (consumed by IRGroupBy
    // codegen and method_for ragged-peel paths below):
    //   <name>__ngroups : size_t (count of groups)
    //   <name>__offsets : size_t* or size_t[] (CSR, length ngroups+1)
    //   <name>__perm    : size_t* (permutation, length input)
    // Plus Case-specific transients (__counts, __fill, __lookup,
    // __bucket) not consumed outside this block.
    //
    // The `<name>` binding itself is a void* sentinel â€” gk's state
    // lives in the suffix-named symbols above, not in a single C++
    // value. Downstream consumers read those symbols by name.
    //
    // Compound (multi-key) mode: when `keys` has length >1, the
    // dispatch becomes an unordered_map<std::tuple<...>, size_t>
    // keyed by the tuple of component values. Each unique tuple
    // discovered in the input becomes its own bucket. The C++ ABI
    // (__ngroups, __offsets, __perm) is identical to the single-key
    // dynamic case â€” downstream consumers don't need to know whether
    // grouping was single- or multi-key. Compound mode requires the
    // tuple_hasher helper from nested_array_utilities.hpp.
    match keys with
    | [] ->
        let ctx' = addVarName binding.Id name ctx
        (codegenError ctx ind "group_keys with empty key list (should have been caught by typechecker)", ctx')
    | [singleKey] ->
        // Existing single-key path: three sub-cases (positional /
        // EnumIdx / dynamic), dispatched on the binding's
        // IRTGroupKeys (ngroupsOpt, enumValuesOpt).
        let keysName = exprToCppCtx ctx singleKey
        let (elemType, keysElemErrCode) = inferElemTypeStrict ctx ind singleKey "group_keys"
        let elemStr = elemTypeToCpp elemType
        match binding.Type with
        | IRTGroupKeys (outerIdx, _, enumValuesOpt) ->
            let ngroupsOpt =
                match outerIdx.Extent with
                | IRLit (IRLitInt n) -> Some (int n)
                | _ -> None
            match ngroupsOpt, enumValuesOpt with
            | None, _ ->
                // Case 3 â€” dynamic ngroups via hash discovery. Builds a key â†’
                // bucket-index map in a single discovery pass, then reuses the
                // map for counts/offsets/permutation. Bucket indices are
                // assigned in first-occurrence order: the bucket for a key is
                // its position in the sequence of distinct keys as encountered
                // walking the input left-to-right.
                //
                // Replaces an earlier max-key-scan implementation that only
                // worked for dense [0..N) keys; sparse keys (e.g. [101, 205,
                // 307]) caused max-scan to allocate one bucket per integer in
                // [0..max], almost all empty, with the wrong semantics.
                //
                // For monotonic dense keys (the historical Case 3 pattern,
                // e.g. [0,1,2,0,1,2]) the hash and max-scan paths produce
                // identical bucket orderings, so existing tests are unchanged.
                // Non-monotonic dense keys differ (hash uses first-occurrence
                // order, max-scan used numeric order); users who want numeric
                // ordering should annotate with `Idx<N>` to opt into Case 1.
                let code = keysElemErrCode @ [
                    sprintf "%s// group_keys: dynamic ngroups (hash discovery, %s keys)" ind elemStr
                    sprintf "%sstd::unordered_map<%s, size_t> %s__lookup;" ind elemStr name
                    sprintf "%ssize_t %s__ngroups = 0;" ind name
                    sprintf "%sfor (size_t __ki = 0; __ki < %s.extents[0]; __ki++) {" ind keysName
                    sprintf "%s    %s __k = %s[__ki];" ind elemStr keysName
                    sprintf "%s    if (%s__lookup.find(__k) == %s__lookup.end()) %s__lookup[__k] = %s__ngroups++;" ind name name name name
                    sprintf "%s}" ind
                    sprintf "%ssize_t* %s__counts = new size_t[%s__ngroups]();" ind name name
                    sprintf "%sfor (size_t __ki = 0; __ki < %s.extents[0]; __ki++) {" ind keysName
                    sprintf "%s    %s__counts[%s__lookup[%s[__ki]]]++;" ind name name keysName
                    sprintf "%s}" ind
                    sprintf "%ssize_t* %s__offsets = new size_t[%s__ngroups + 1];" ind name name
                    sprintf "%s%s__offsets[0] = 0;" ind name
                    sprintf "%sfor (size_t __gi = 0; __gi < %s__ngroups; __gi++) %s__offsets[__gi + 1] = %s__offsets[__gi] + %s__counts[__gi];" ind name name name name
                    sprintf "%ssize_t* %s__fill = new size_t[%s__ngroups]();" ind name name
                    sprintf "%ssize_t* %s__perm = new size_t[%s.extents[0]];" ind name keysName
                    sprintf "%sfor (size_t __ki = 0; __ki < %s.extents[0]; __ki++) {" ind keysName
                    sprintf "%s    size_t __g = %s__lookup[%s[__ki]];" ind name keysName
                    sprintf "%s    %s__perm[%s__offsets[__g] + %s__fill[__g]++] = __ki;" ind name name name
                    sprintf "%s}" ind
                    sprintf "%ssize_t %s_extents[1] = {%s__ngroups};" ind name name
                    sprintf "%svoid* %s = nullptr; // gk: state in %s__ngroups, %s__offsets, %s__perm" ind name name name name
                ]
                let ctx' = addVarName binding.Id name ctx
                (code, ctx')
            | Some ngroups, None ->
                // Case 1: positional bucketing. keys[i] in [0, ngroups).
                let code = keysElemErrCode @ [
                    sprintf "%s// group_keys: %d groups, positional buckets (Idx<N> keys)" ind ngroups
                    sprintf "%ssize_t %s__ngroups = %d;" ind name ngroups
                    sprintf "%ssize_t %s__counts[%d] = {0};" ind name ngroups
                    sprintf "%sfor (size_t __ki = 0; __ki < %s.extents[0]; __ki++) {" ind keysName
                    sprintf "%s    %s__counts[%s[__ki]]++;" ind name keysName
                    sprintf "%s}" ind
                    sprintf "%ssize_t %s__offsets[%d];" ind name (ngroups + 1)
                    sprintf "%s%s__offsets[0] = 0;" ind name
                    sprintf "%sfor (size_t __gi = 0; __gi < %d; __gi++) %s__offsets[__gi + 1] = %s__offsets[__gi] + %s__counts[__gi];" ind ngroups name name name
                    sprintf "%ssize_t %s__fill[%d] = {0};" ind name ngroups
                    sprintf "%ssize_t* %s__perm = new size_t[%s.extents[0]];" ind name keysName
                    sprintf "%sfor (size_t __ki = 0; __ki < %s.extents[0]; __ki++) {" ind keysName
                    sprintf "%s    size_t __g = (size_t)%s[__ki];" ind keysName
                    sprintf "%s    %s__perm[%s__offsets[__g] + %s__fill[__g]++] = __ki;" ind name name name
                    sprintf "%s}" ind
                    sprintf "%ssize_t %s_extents[1] = {%s__ngroups};" ind name name
                    sprintf "%svoid* %s = nullptr; // gk: state in %s__ngroups, %s__offsets, %s__perm" ind name name name name
                ]
                let ctx' = addVarName binding.Id name ctx
                (code, ctx')
            | Some ngroups, Some values ->
                // Case 2: EnumIdx â€” keys are arbitrary integers OR strings,
                // mapped to bucket indices [0, ngroups) via an
                // `unordered_map<K, size_t>` lookup. Each value's bucket
                // index is its position in the EnumIdx values list. The
                // map is `static const` at the enclosing function scope:
                // initialized once on first encounter (thread-safe magic
                // static), reused across every group_keys evaluation in
                // the same call site. Lookup falls through to bucket 0
                // for unknown keys, preserving the prior silent-default
                // behavior (EnumIdx is type-checked, so unknown keys
                // indicate a typechecker bug rather than a user error).
                //
                // Why a map and not a switch / if-chain: dispatch cost
                // scales O(1) instead of O(values) per element. Especially
                // visible for string EnumIdx, where prior if-chains
                // compared each key against every value-literal in turn.
                let bucketEntries =
                    let renderVal v =
                        match v with
                        | EVInt n -> sprintf "%dLL" n
                        | EVString s -> escapeStringLit s
                    values
                    |> List.mapi (fun i v ->
                        sprintf "{%s, (size_t)%d}" (renderVal v) i)
                    |> String.concat ", "
                let bucketMapDecl =
                    sprintf "static const std::unordered_map<%s, size_t> %s__bucket_map = {%s};" elemStr name bucketEntries
                let bucketLambdaDecl =
                    sprintf "auto %s__bucket = [](const %s& __v) -> size_t { auto it = %s__bucket_map.find(__v); return it != %s__bucket_map.end() ? it->second : (size_t)0; };" name elemStr name name
                let code = keysElemErrCode @ [
                    sprintf "%s// group_keys: %d groups, EnumIdx reverse lookup (unordered_map dispatch)" ind ngroups
                    sprintf "%ssize_t %s__ngroups = %d;" ind name ngroups
                    sprintf "%s%s" ind bucketMapDecl
                    sprintf "%s%s" ind bucketLambdaDecl
                    sprintf "%ssize_t %s__counts[%d] = {0};" ind name ngroups
                    sprintf "%sfor (size_t __ki = 0; __ki < %s.extents[0]; __ki++) {" ind keysName
                    sprintf "%s    %s__counts[%s__bucket(%s[__ki])]++;" ind name name keysName
                    sprintf "%s}" ind
                    sprintf "%ssize_t %s__offsets[%d];" ind name (ngroups + 1)
                    sprintf "%s%s__offsets[0] = 0;" ind name
                    sprintf "%sfor (size_t __gi = 0; __gi < %d; __gi++) %s__offsets[__gi + 1] = %s__offsets[__gi] + %s__counts[__gi];" ind ngroups name name name
                    sprintf "%ssize_t %s__fill[%d] = {0};" ind name ngroups
                    sprintf "%ssize_t* %s__perm = new size_t[%s.extents[0]];" ind name keysName
                    sprintf "%sfor (size_t __ki = 0; __ki < %s.extents[0]; __ki++) {" ind keysName
                    sprintf "%s    size_t __g = %s__bucket(%s[__ki]);" ind name keysName
                    sprintf "%s    %s__perm[%s__offsets[__g] + %s__fill[__g]++] = __ki;" ind name name name
                    sprintf "%s}" ind
                    sprintf "%ssize_t %s_extents[1] = {%s__ngroups};" ind name name
                    sprintf "%svoid* %s = nullptr; // gk: state in %s__ngroups, %s__offsets, %s__perm" ind name name name name
                ]
                let ctx' = addVarName binding.Id name ctx
                (code, ctx')
        | _ ->
            let ctx' = addVarName binding.Id name ctx
            (codegenError ctx ind (sprintf "group_keys binding '%s' has wrong inferred type (expected IRTGroupKeys)" name), ctx')
    | multipleKeys ->
        // Compound (multi-key) dispatch: tuple-keyed unordered_map.
        // Each (k1, k2, ...) tuple discovered in the input becomes its
        // own bucket, assigned a unique index in first-occurrence order.
        // The bucket count (ngroups) and tuple-bucket mapping are both
        // discovered at runtime; this is structurally the dynamic Case 3
        // generalized to multi-component keys.
        //
        // Tuple type: std::tuple<T1, T2, ...> where Ti is the element
        // type of the i-th key array. Each Ti must be a hashable scalar
        // type for std::unordered_map to work; this is enforced
        // implicitly by IRTGroupKeys construction in typecheck (only
        // valid index-component types reach here).
        //
        // tuple_hasher: provided by nested_array_utilities.hpp, applies
        // the canonical hash-combine recipe across tuple components.
        //
        // C++ ABI emitted is identical to single-key Case 3:
        //   <name>__ngroups : size_t (discovered count of unique tuples)
        //   <name>__offsets : size_t* (CSR, length ngroups+1)
        //   <name>__perm    : size_t* (permutation, length input)
        // Plus compound-specific transients (__lookup, __counts, __fill).
        //
        // Downstream IRGroupBy and method_for ragged-peel paths see
        // a normal CSR structure â€” they don't need to know that the
        // grouping was compound rather than scalar.
        
        // Per-key data: C++ name + element type + any err lines.
        let keyData =
            multipleKeys |> List.map (fun k ->
                let kName = exprToCppCtx ctx k
                let (kElem, kErr) = inferElemTypeStrict ctx ind k "group_keys (compound key)"
                (kName, elemTypeToCpp kElem, kErr))
        let keyErrCode = keyData |> List.collect (fun (_, _, e) -> e)
        let keyNames = keyData |> List.map (fun (n, _, _) -> n)
        let tupleTypeStr =
            keyData
            |> List.map (fun (_, t, _) -> t)
            |> String.concat ", "
            |> sprintf "std::tuple<%s>"
        // Use the FIRST key array's extents for outer iteration. Typecheck
        // has verified all key arrays share the outer extent.
        let outerExtent = sprintf "%s.extents[0]" (List.head keyNames)
        // make_tuple(k1[__ki], k2[__ki], ...) expression.
        let makeTupleAt indexVar =
            keyNames
            |> List.map (fun n -> sprintf "%s[%s]" n indexVar)
            |> String.concat ", "
            |> sprintf "std::make_tuple(%s)"
        let code = keyErrCode @ [
            sprintf "%s// group_keys: compound dispatch (%d-key tuple), dynamic ngroups via hash discovery" ind multipleKeys.Length
            sprintf "%sstd::unordered_map<%s, size_t, tuple_hasher> %s__lookup;" ind tupleTypeStr name
            sprintf "%ssize_t %s__ngroups = 0;" ind name
            sprintf "%sfor (size_t __ki = 0; __ki < %s; __ki++) {" ind outerExtent
            sprintf "%s    auto __k = %s;" ind (makeTupleAt "__ki")
            sprintf "%s    if (%s__lookup.find(__k) == %s__lookup.end()) %s__lookup[__k] = %s__ngroups++;" ind name name name name
            sprintf "%s}" ind
            sprintf "%ssize_t* %s__counts = new size_t[%s__ngroups]();" ind name name
            sprintf "%sfor (size_t __ki = 0; __ki < %s; __ki++) {" ind outerExtent
            sprintf "%s    %s__counts[%s__lookup[%s]]++;" ind name name (makeTupleAt "__ki")
            sprintf "%s}" ind
            sprintf "%ssize_t* %s__offsets = new size_t[%s__ngroups + 1];" ind name name
            sprintf "%s%s__offsets[0] = 0;" ind name
            sprintf "%sfor (size_t __gi = 0; __gi < %s__ngroups; __gi++) %s__offsets[__gi + 1] = %s__offsets[__gi] + %s__counts[__gi];" ind name name name name
            sprintf "%ssize_t* %s__fill = new size_t[%s__ngroups]();" ind name name
            sprintf "%ssize_t* %s__perm = new size_t[%s];" ind name outerExtent
            sprintf "%sfor (size_t __ki = 0; __ki < %s; __ki++) {" ind outerExtent
            sprintf "%s    size_t __g = %s__lookup[%s];" ind name (makeTupleAt "__ki")
            sprintf "%s    %s__perm[%s__offsets[__g] + %s__fill[__g]++] = __ki;" ind name name name
            sprintf "%s}" ind
            sprintf "%ssize_t %s_extents[1] = {%s__ngroups};" ind name name
            sprintf "%svoid* %s = nullptr; // gk: state in %s__ngroups, %s__offsets, %s__perm (compound)" ind name name name name
        ]
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')



and genComputeBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (inner: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Compute unwraps - handle the inner expression
    // Recursive resolver: peels IRFunctorMap wrappers, resolves IRVar through deferred,
    // and handles IRComposeMeth by extracting right's kernel as a functor wrapper.
    // Returns (innerExpr, wrapperFunctions) where wrappers are innermost-first
    let rec resolveComputation (expr: IRExpr) (wrappers: IRExpr list) : IRExpr * IRExpr list =
        match expr with
        | IRVar (id, _) ->
            match Map.tryFind id ctx.DeferredComputations with
            | Some deferred -> resolveComputation deferred wrappers
            | None -> (expr, wrappers)
        | IRCompute inner ->
            // Stage 3c.0 added an IRCompute wrap at lambda lift time around
            // bodies whose top form is IRApplyCombinator. The wrap is
            // semantically the identity at compute time (force evaluation,
            // which a computation already is). Peel it through here so the
            // downstream dispatch sees the unwrapped form. Without this,
            // a double-wrapped IRCompute(IRCompute(IRApplyCombinator)) â€”
            // produced when the bind expansion explicitly wraps an
            // already-wrapped continuation body â€” would fall through to
            // the generic case below and fail to materialize.
            resolveComputation inner wrappers
        | IRFunctorMap (f, inner) ->
            resolveComputation inner (f :: wrappers)
        | IRGuard (cond, body) ->
            // Resolve through guard: push wrappers into the body
            let (innerResolved, innerWrappers) = resolveComputation body wrappers
            (IRGuard (cond, innerResolved), innerWrappers)
        | IRComposeMeth (left, right) ->
            // @>> : c1 @>> c2 means "at each index, apply c2's kernel to c1's result"
            // The kernel resolves to a callable via the
            // CallablesTable; the returned kernel expression flows
            // into the wrappers list and gets substituted by
            // betaReduce via the same resolution.
            let rec extractInlinableKernel e =
                match e with
                | IRVar (id, _) ->
                    match Map.tryFind id ctx.DeferredComputations with
                    | Some d -> extractInlinableKernel d
                    | None -> None
                | IRApplyCombinator info ->
                    match resolveCallable info.Kernel with
                    | Some _ -> Some info.Kernel
                    | None -> None
                | IRFunctorMap (f, inner) ->
                    match extractInlinableKernel inner with
                    | Some k -> Some (IRCompose (k, f))
                    | None -> None
                | _ -> None
            match extractInlinableKernel right with
            | Some kernel -> resolveComputation left (kernel :: wrappers)
            | None -> (expr, wrappers)  // fallback: leave for IRComposeMeth handler
        | _ -> (expr, wrappers)
    
    let (resolved, functorWrappers) = resolveComputation inner []
    
    // Compose functor wrappers into ApplyInfo kernel if present
    // f <$> (L <@> g) â†’ L <@> (f âˆ˜ g)
    // Wraps kernel body: Î»params â†’ f(g(params))
    let applyFunctorWrappers (info: ApplyInfo) (wrappers: IRExpr list) : ApplyInfo =
        if wrappers.IsEmpty then info
        else
            // Beta-reduce: substitute wrapper's parameter with inner body
            // f <$> (L <@> g) where f = Î»x â†’ h(x)
            // becomes L <@> Î»params â†’ h(g(params))
            let betaReduce (wrapper: IRExpr) (body: IRExpr) : IRExpr =
                // Resolve through the CallablesTable. When the
                // wrapper resolves to a single-param callable,
                // substitute the param VarId with `body`. When it
                // doesn't resolve (or arity mismatch), fall back
                // to IRApp form (still correct, just not inlined).
                match resolveCallable wrapper with
                | Some c when c.Params.Length = 1 ->
                    // Substitute param VarId with body expression
                    let paramId = c.Params.[0].VarId
                    let rec subst (expr: IRExpr) =
                        match expr with
                        | IRVar (id, _) when id = paramId -> body
                        | IRVar _ | IRLit _ | IRParam _ -> expr
                        | IRBinOp (m, op, l, r) -> IRBinOp (m, op, subst l, subst r)
                        | IRUnaryOp (op, e) -> IRUnaryOp (op, subst e)
                        | IRIf (c, t, e) -> IRIf (subst c, subst t, subst e)
                        | IRApp (f, args, rt) -> IRApp (subst f, args |> List.map subst, rt)
                        | IRIndex (a, idxs, ty) -> IRIndex (subst a, idxs |> List.map subst, ty)
                        | IRTuple es -> IRTuple (es |> List.map subst)
                        | IRComplex (re, im) -> IRComplex (subst re, subst im)
                        | IRTupleProj (e, i, flat) -> IRTupleProj (subst e, i, flat)
                        | IRFieldAccess (e, f) -> IRFieldAccess (subst e, f)
                        | IRLet (id, v, b) -> IRLet (id, subst v, subst b)
                        | _ -> expr  // For complex nodes, leave as-is
                    subst c.Body
                | _ ->
                    // Can't beta-reduce (couldn't resolve, or arity
                    // mismatch), fall back to IRApp (IIFE in C++).
                    let retTy =
                        match resolveCallable wrapper with
                        | Some c -> c.RetType
                        | None -> IRTScalar ETFloat64
                    IRApp (wrapper, [body], retTy)
            
            let wrappedKernel =
                // Stage 3c.4a: synthetic-registry construction. Each
                // per-use-site wrap produces a fresh IRCallable with
                // a new builder-allocated id, registers it in the
                // codegen-pass synthetic registry, and returns an
                // IRVar reference. resolveCallable now queries both
                // the module's CallablesTable and the synthetic
                // registry, so downstream consumers (buildLoopNest-
                // CodeGen for inline emission, betaReduce for further
                // kernel-fold) see the wrapped body uniformly.
                // `mapKernelInner` peels any `IRReynolds` wrapper,
                // applies the transform to the inner callable, and
                // re-wraps with Reynolds (preserving isAntisymmetric)
                // if it was present.
                let wrapBody (body: IRExpr) =
                    wrappers |> List.fold (fun b w -> betaReduce w b) body
                let buildInline (c: IRCallable) : IRExpr =
                    let synthetic =
                        { c with Id = builder.FreshId()
                                 Body = wrapBody c.Body }
                    registerSyntheticCallable synthetic
                mapKernelInner buildInline info.Kernel
            // Update output type from outermost wrapper's return
            // type, resolving the wrapper through resolveCallable.
            // info.OutputType might otherwise be stale relative to
            // the wrapped kernel, mis-typing downstream sites (the
            // element-type adjustment below, output allocation).
            let newOutputType =
                match wrappers |> List.tryHead with
                | Some w ->
                    match resolveCallable w with
                    | Some c -> c.RetType
                    | None -> info.OutputType
                | None -> info.OutputType
            // If output is an array, update the element type
            let adjustedOutputType =
                match info.OutputType, newOutputType with
                | ArrayElem arr, IRTScalar et -> mkArrayLike { arr with ElemType = IRTScalar et }
                | _ -> newOutputType
            { info with Kernel = wrappedKernel; OutputType = adjustedOutputType }
    
    match resolved with
    | IRApplyCombinator info ->
        // After the IRComposeApply split, info.Loop is never a
        // composed-object chain here â€” those route through the
        // IRComposeApply arm below. The inner resolvedLoop dispatch
        // that previously distinguished IRComposeObj from normal
        // applies is no longer needed.
        let info' = applyFunctorWrappers info functorWrappers
        let code = genApplyCombinator ctx name info' builder
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')

    | IRComposeApply info ->
        // Slot-inverted apply: (object_for(f) >>@ object_for(g)) <@> A.
        //
        // If functorWrappers is non-empty (the @>> case, where
        // extractInlinableKernel of the right operand surfaced its
        // kernel as a wrapper to apply to the left's result),
        // materialize the compose-apply into a temporary and then
        // emit a separate element-wise stage that applies the
        // wrapped kernel chain. For canonical IRApplyCombinator
        // this is handled by applyFunctorWrappers folding the
        // wrappers into the kernel body; for compose-apply that
        // doesn't apply (no single kernel slot), so we use the
        // stage-on-stage form instead.
        if functorWrappers.IsEmpty then
            let (code, ctx') = genComposeApply ctx name info info.OutputType builder
            let ctx'' = addVarName binding.Id name ctx'
            (code, ctx'')
        else
            let s1Name = sprintf "%s__wrap_s1" name
            let s1Id = builder.FreshId()
            let s1Type = info.OutputType
            let (code1, ctx1) = genComposeApply ctx s1Name info s1Type builder
            let ctx1' = addVarName s1Id s1Name ctx1
            // Build an ApplyInfo whose kernel is the innermost
            // wrapper, then fold the rest on top via the existing
            // wrapper-composition machinery.
            let firstWrapper = List.head functorWrappers
            let restWrappers = List.tail functorWrappers
            let baseInfo = buildSimpleApplyInfo [IRVar(s1Id, s1Type)] firstWrapper binding.Type
            let finalInfo = applyFunctorWrappers baseInfo restWrappers
            let code2 = genApplyCombinator ctx1' name finalInfo builder
            let ctx2 = addVarName binding.Id name ctx1'
            (code1 @ [""] @ code2, ctx2)
    
    | IRComposeMeth (left, right) ->
        // @>> : sequential composition â€” compute left, feed result to right's kernel
        // Stage 1: materialize left computation
        let s1Name = sprintf "%s__s1" name
        let s1Id = builder.FreshId()
        
        // Resolve left through deferred
        let rec resolveDeferred e =
            match e with
            | IRVar (id, _) -> 
                match Map.tryFind id ctx.DeferredComputations with
                | Some d -> resolveDeferred d
                | None -> e
            | _ -> e
        let resolvedLeft = resolveDeferred left
        let resolvedRight = resolveDeferred right

        // Extract right's kernel
        let rightKernel = 
            match resolvedRight with 
            | IRApplyCombinator info -> info.Kernel 
            | _ -> resolvedRight
        let rightKernelName = 
            match rightKernel with 
            | IRVar (id, _) -> Map.tryFind id ctx.VarNames 
            | _ -> None
        
        // Materialize left as stage 1
        let s1Type = inferExprType resolvedLeft
        let s1Binding = { Id = s1Id; Name = s1Name; Type = s1Type; Value = IRCompute resolvedLeft; IsConst = true; IsMutable = false }
        let (code1, ctx1) = genBinding ctx s1Binding builder
        
        match rightKernelName with
        | Some kName ->
            // Right kernel is a named function â€” generate element-wise function-call loop
            let arrRank = match s1Type with ArrayElem arr -> arrayRank arr | _ -> 1
            // s1Type comes from the upstream binding; it MUST be an
            // array at this point or the composition is malformed.
            // No `double` fallback: a non-array s1Type indicates a
            // real upstream bug worth diagnosing, not papering over.
            let (elemType, elemTypeErrCode) =
                match s1Type with
                | ArrayElem arr -> (elemTypeToCpp arr.ElemType, [])
                | t ->
                    (elemTypeToCpp (IRTScalar ETFloat64),
                     codegenError ctx ind (sprintf "method composition: left side has non-array type %A (typechecker or IR bug)" t))
            let s2Code = [
                sprintf "%sconst size_t* %s_extents = %s.extents;" ind name s1Name
                sprintf "%sArray<%s, %d> %s = { allocate<typename promote<%s, %d>::type, nullptr>(%s_extents), %s_extents };" ind elemType arrRank name elemType arrRank name name
                sprintf "%sfor (size_t __i0 = 0; __i0 < %s.extents[0]; __i0++) {" ind s1Name
                sprintf "%s    %s[__i0] = %s(%s[__i0]);" ind name kName s1Name
                sprintf "%s}" ind
            ]
            let ctx2 = addVarName binding.Id name ctx1
            (code1 @ [""] @ elemTypeErrCode @ s2Code, ctx2)
        | None ->
            // Right kernel is inline lambda â€” use buildSimpleApplyInfo path
            let s2Info = buildSimpleApplyInfo [IRVar(s1Id, s1Type)] rightKernel binding.Type
            let code2 = genApplyCombinator ctx1 name s2Info builder
            let ctx2 = addVarName binding.Id name ctx1
            (code1 @ [""] @ code2, ctx2)
    
    | IRBind (comp, cont) ->
        // Monadic bind: c >>= k
        // Stage 1: materialize comp
        let s1Name = sprintf "%s__s1" name
        let s1Id = builder.FreshId()
        
        // Resolve comp through deferred
        let rec resolveDeferred e =
            match e with
            | IRVar (id, _) -> 
                match Map.tryFind id ctx.DeferredComputations with
                | Some d -> resolveDeferred d
                | None -> e
            | _ -> e
        let resolvedComp = resolveDeferred comp
        
        let s1Type = inferExprType resolvedComp
        let s1Binding = { Id = s1Id; Name = s1Name; Type = s1Type; Value = IRCompute resolvedComp; IsConst = true; IsMutable = false }
        let (code1, ctx1) = genBinding ctx s1Binding builder
        
        // Resolve continuation to its underlying callable. Post-3c.4
        // the continuation arrives as IRVar(callableId, _) for any
        // let-bound or inline continuation; resolveCallable handles
        // both the CallablesTable and the synthetic registry.
        let resolvedCont = resolveDeferred cont
        match resolveCallable resolvedCont with
        | Some lInfo when lInfo.Params.Length >= 1 ->
            // Bind lambda parameter to stage 1 result
            let param = lInfo.Params.[0]
            let ctx2 = addVarName param.VarId s1Name ctx1
            
            // Generate code for lambda body as a computation
            let bodyBinding = { Id = binding.Id; Name = name; Type = binding.Type; Value = IRCompute lInfo.Body; IsConst = true; IsMutable = false }
            let (code2, ctx3) = genBinding ctx2 bodyBinding builder
            (code1 @ [""] @ code2, ctx3)
        | _ ->
            // Fallback: continuation not resolvable to callable â€”
            // generate a function call against whatever cont
            // reference we have.
            let contName = match cont with IRVar (id, _) -> Map.tryFind id ctx.VarNames | _ -> None
            match contName with
            | Some kName ->
                let code = [sprintf "%sauto %s = %s(%s);" ind name kName s1Name]
                let ctx' = addVarName binding.Id name ctx1
                (code1 @ [""] @ code, ctx')
            | None ->
                let code = genScalarBinding ctx1 name (IRApp(cont, [IRVar(s1Id, s1Type)], binding.Type)) binding.Type
                let ctx' = addVarName binding.Id name ctx1
                (code1 @ [""] @ code, ctx')

    | IRParallel _ ->
        // Parallel composition: recursively generate independent loops, combine as nested pairs
        let (code, _, childrenMap) = genParallelTree ctx name resolved builder
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with TupleChildren = Map.fold (fun acc k v -> Map.add k v acc) ctx'.TupleChildren childrenMap }
        (code, ctx')
    
    | IRFusion _ ->
        // Mandatory fusion: single fused loop nest with all kernels
        let (code, _, childrenMap) = genFusionTree ctx name resolved builder
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with TupleChildren = Map.fold (fun acc k v -> Map.add k v acc) ctx'.TupleChildren childrenMap }
        (code, ctx')
    
    | IRChoice (left, right) ->
        // Computation-level choice: materialize both sides, element-wise combine
        // result[i] = (lhs[i] != 0) ? lhs[i] : rhs[i]
        // If functor wrappers present: f <$> (c1 <|> c2) â‰¡ (f <$> c1) <|> (f <$> c2)
        let wrapSide side =
            if functorWrappers.IsEmpty then side
            else functorWrappers |> List.fold (fun acc w -> IRFunctorMap(w, acc)) side
        let left' = wrapSide left
        let right' = wrapSide right
        let nameL = sprintf "%s__lhs" name
        let nameR = sprintf "%s__rhs" name
        let idL = builder.FreshId()
        let idR = builder.FreshId()
        let bindingL = { Id = idL; Name = nameL; Type = binding.Type; Value = IRCompute left'; IsConst = true; IsMutable = false }
        let bindingR = { Id = idR; Name = nameR; Type = binding.Type; Value = IRCompute right'; IsConst = true; IsMutable = false }
        let (codeL, ctxL) = genBinding ctx bindingL builder
        let (codeR, ctxR) = genBinding ctxL bindingR builder
        
        let ind = indentStr ctx
        let rank = match binding.Type with ArrayElem arr -> arrayRank arr | _ -> 0
        // Choice `<|>` legitimately handles both array and scalar
        // bindings (rank=0 â‡’ scalar). Both cases get their elem type
        // from the binding's resolved type. A type that's neither
        // ArrayElem nor IRTScalar at this point is an upstream
        // typechecker bug.
        let (elemType, elemTypeErrCode) =
            match binding.Type with
            | ArrayElem arr -> (elemTypeToCpp arr.ElemType, [])
            | IRTScalar et -> (primTypeToCpp et, [])
            | t ->
                (elemTypeToCpp (IRTScalar ETFloat64),
                 codegenError ctx ind (sprintf "<|>: binding type is neither array nor scalar (got %A) â€” likely a typechecker or IR bug" t))
        
        if rank = 0 then
            // Scalar choice
            let code = [sprintf "%s%s %s = (%s != 0) ? %s : %s;" ind elemType name nameL nameL nameR]
            let ctx' = addVarName binding.Id name ctxR
            (codeL @ [""] @ codeR @ [""] @ elemTypeErrCode @ code, ctx')
        else
            // Array choice: allocate result, element-wise combine.
            // Read the source's shape via the wrapper's .extents
            // member; populate name_extents alias for the allocate<>
            // template (which still takes a const size_t*).
            // Array choice: allocate result, element-wise combine. The
            // choice of two same-shaped arrays is rectangular at this layer;
            // pass nullptr for SYMM (a symmetric <|> would need the operand's
            // hoisted symm name, which isn't threaded here â€” out of scope and
            // not currently produced). This avoids referencing a nonexistent
            // function-local `nameL_symm` after the symm-hoist refactor.
            let extentsAlias = sprintf "%sconst size_t* %s_extents = %s.extents;" ind name nameL
            let allocDecl = sprintf "%sArray<%s, %d> %s = { allocate<typename promote<%s, %d>::type, nullptr>(%s_extents), %s_extents };" 
                                ind elemType rank name elemType rank name name
            
            // Generate nested loops for element-wise choice
            let mutable loopLines = []
            let mutable depth = ctx.Indent
            let indD d = String.replicate d "    "
            for i in 0 .. rank - 1 do
                let bound = sprintf "%s.extents[%d]" name i
                loopLines <- loopLines @ [sprintf "%sfor (size_t __i%d = 0; __i%d < %s; __i%d++) {" (indD depth) i i bound i]
                depth <- depth + 1
            
            let idxStr = [for i in 0 .. rank - 1 -> sprintf "[__i%d]" i] |> String.concat ""
            let lhsElem = sprintf "%s%s" nameL idxStr
            let rhsElem = sprintf "%s%s" nameR idxStr
            loopLines <- loopLines @ [sprintf "%s%s%s = (%s != 0) ? %s : %s;" (indD depth) name idxStr lhsElem lhsElem rhsElem]
            
            for _ in 0 .. rank - 1 do
                depth <- depth - 1
                loopLines <- loopLines @ [sprintf "%s}" (indD depth)]
            
            let ctx' = addVarName binding.Id name ctxR
            (codeL @ [""] @ codeR @ [""] @ elemTypeErrCode @ [extentsAlias; allocDecl; ""] @ loopLines, ctx')

    | IRFallback (left, right) ->
        // <|:> allocated-fallback materialization (storage-keyed, unlike the
        // value-keyed IRChoice arm above) — see genFallbackMaterialize.
        // Functor wrappers are not distributed over storage fallback (f <$>
        // (A <|:> B) would need f mapped over both operands' storage): reject.
        if not functorWrappers.IsEmpty then
            let code = codegenError ctx (indentStr ctx) "<$> over a <|:> fallback is not supported; materialize the fallback with |> compute first, then map"
            (code, addVarName binding.Id (bindingCppName binding) ctx)
        else
            genFallbackMaterialize ctx binding builder left right

    | IRGuard (cond, body) ->
        // guard(p, c) |> compute: conditionally execute computation
        // Strategy: wrap the kernel body with the guard condition
        // guard(cond, L <@> f) â†’ L <@> (Î»args â†’ cond ? f(args) : 0)
        // This allocates the array always but fills with zeros when false
        let isComputation =
            match body with
            | IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ | IRFallback _ -> true
            | IRVar (id, _) -> Map.containsKey id ctx.DeferredComputations
            | _ -> false
        if isComputation then
            // Resolve the inner computation
            let resolvedBody =
                match body with
                | IRVar (id, _) -> Map.tryFind id ctx.DeferredComputations |> Option.defaultValue body
                | _ -> body
            match resolvedBody with
            | IRApplyCombinator info ->
                // Wrap kernel: Î»params â†’ cond ? kernel_body : 0
                //
                // Resolves the kernel through resolveCallable and
                // routes through the synthetic registry: a fresh
                // callable with a new builder-allocated id holds
                // the conditional-wrapped body, gets registered,
                // and is referenced via IRVar. The original
                // callable in module.Functions is unchanged â€” the
                // guard wrap is per-use-site.
                let zeroForReturnType (retTy: IRType) =
                    match retTy with
                    | IRTScalar ETBool -> IRLit (IRLitBool false)
                    | IRTScalar ETInt64 | IRTScalar ETInt32 -> IRLit (IRLitInt 0L)
                    | IRTIdxTagged (IRTScalar (ETInt64 | ETInt32), _) -> IRLit (IRLitInt 0L)
                    | _ -> IRLit (IRLitFloat 0.0)
                let buildGuarded (c: IRCallable) : IRExpr =
                    let zeroVal = zeroForReturnType c.RetType
                    let synthetic =
                        { c with Id = builder.FreshId()
                                 Body = IRIf (cond, c.Body, zeroVal) }
                    registerSyntheticCallable synthetic
                // `mapKernelInner` peels any `IRReynolds` wrapper,
                // applies `buildGuarded` to the inner callable, and
                // re-wraps with Reynolds (preserving isAntisymmetric)
                // if it was present. Before this consolidation the
                // peel was open-coded as `resolveCallable info.Kernel`
                // which returns None on Reynolds-wrapped kernels,
                // silently dropping the guard predicate.
                let wrappedKernel = mapKernelInner buildGuarded info.Kernel
                let guardedInfo = { info with Kernel = wrappedKernel }
                // Apply any functor wrappers
                let finalInfo = applyFunctorWrappers guardedInfo functorWrappers
                let code = genApplyCombinator ctx name finalInfo builder
                let ctx' = addVarName binding.Id name ctx
                (code, ctx')
            | _ ->
                // Non-apply computation (parallel, fusion, etc.) â€” fall back to scalar guard
                let guardExpr = IRGuard (cond, body)
                let code = genScalarBinding ctx name guardExpr binding.Type
                let ctx' = addVarName binding.Id name ctx
                (code, ctx')
        else
            // Scalar guard: treat as scalar expression via exprToCpp
            let guardExpr = IRGuard (cond, body)
            let code = genScalarBinding ctx name guardExpr binding.Type
            let ctx' = addVarName binding.Id name ctx
            (code, ctx')
    
    | IRSequence elems ->
        // Homogeneous n-ary parallel: each child produces same type
        // Result is array indexed by Idx<N> containing the child results
        // IMPORTANT: each child generates against the original ctx, not accumulated,
        // to prevent one child's output from contaminating another's array resolution.
        let n = elems.Length
        let childNames = elems |> List.mapi (fun i _ -> sprintf "%s_%d" name i)
        let (allCode, mergedVarNames) =
            (elems, childNames) ||> List.map2 (fun elem childName ->
                let wrappedElem =
                    if functorWrappers.IsEmpty then elem
                    else functorWrappers |> List.fold (fun acc w -> IRFunctorMap(w, acc)) elem
                let childType =
                    match wrappedElem with
                    | IRApplyCombinator info -> info.OutputType
                    | IRComposeApply info -> info.OutputType
                    | _ -> inferExprType wrappedElem
                let childBinding = { Id = builder.FreshId(); Name = childName; Type = childType; Value = IRCompute wrappedElem; IsConst = true; IsMutable = false }
                genBinding ctx childBinding builder)
            |> List.fold (fun (accCode, accNames) (code, newCtx) ->
                (accCode @ code @ [""], Map.fold (fun a k v -> Map.add k v a) accNames newCtx.VarNames)
            ) ([], ctx.VarNames)
        // Determine child element type and rank
        let childType = inferExprType (List.head elems)
        let (childElemType, childRank, childTypeErrCode) =
            match childType with
            | ArrayElem arr -> (elemTypeToCpp arr.ElemType, arrayRank arr, [])
            | IRTScalar et -> (primTypeToCpp et, 0, [])
            | t ->
                (elemTypeToCpp (IRTScalar ETFloat64), 0,
                 codegenError ctx ind (sprintf "IRSequence: child has non-array, non-scalar type %A (likely a typechecker or IR bug)" t))
        let outerRank = childRank + 1
        // Build extents array: [N, child_extents...]
        let extentsEntries =
            [sprintf "%d" n]
            @ [for d in 0 .. childRank - 1 -> sprintf "%s.extents[%d]" (List.head childNames) d]
        let extentsDecl = sprintf "%ssize_t %s_extents[%d] = {%s};" ind name outerRank (extentsEntries |> String.concat ", ")
        // Allocate pointer array (for array children) or value array (for scalar children),
        // wrapped in Array<T,N>. The `new` allocation produces the underlying data; the
        // wrapper ties it together with the freshly emitted name_extents.
        let allocDecl =
            if childRank > 0 then
                sprintf "%sArray<%s, %d> %s = { new %s*[%d], %s_extents };" ind childElemType outerRank name childElemType n name
            else
                sprintf "%sArray<%s, 1> %s = { new %s[%d], %s_extents };" ind childElemType name childElemType n name
        let assignLines =
            childNames |> List.mapi (fun i cn ->
                sprintf "%s%s[%d] = %s;" ind name i cn)
        let ctx' = { ctx with VarNames = Map.add binding.Id name mergedVarNames }
        (allCode @ childTypeErrCode @ [extentsDecl; allocDecl] @ assignLines, ctx')
    
    | _ ->
        // Other compute expressions - treat as scalar
        let code = genScalarBinding ctx name resolved binding.Type
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')



and genProviderReadBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Deferred provider read materialized at the `|> alias.read` force point
    // (approach (b)): emit the provider's reader producing `name`, dispatched
    // through the registry on the spec's provider name. A compound
    // (load_compound) read carries a mask; a plain dense read does not.
    let spec = ctx.ProviderReads.[binding.Id]
    let pspec =
        match Blade.ProviderRegistry.tryFind spec.Provider with
        | Some p -> p
        | None -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf "provider '%s' is not registered â€” was ProviderStatics.install () run?" spec.Provider)))
    if spec.Streamed then
        // Streamed read: emit only the provider's hoisted stream prologue
        // (open handles, fiber extents vector). Consuming nests inline the
        // per-fiber reads via ctx.StreamedArrays; nothing named `name`
        // exists as an array, so any non-nest consumer fails to compile —
        // and the eligible-shape checks in the nest fail loudly first.
        match pspec.GenStreamOpen with
        | None ->
            raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf "provider '%s' does not support streamed reads (variable '%s' — bind with .read)" spec.Provider spec.VarName)))
        | Some gen ->
            let code = gen spec.FilePath spec.VarName name spec.VarType
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with StreamedArrays = Map.add name spec ctx'.StreamedArrays }
            (code |> List.map (fun s -> ind + s), ctx')
    else
    let isPackedVar =
        spec.VarType.IndexTypes |> List.exists (fun ix -> ix.Symmetry <> SymNone && ix.Rank >= 2)
    if isPackedVar then
        // Packed (SymIdx/AntisymIdx) read: the provider assembles the store's
        // canonical flat pool into `<name>_flat`; the materialization lives
        // HERE because the SYMM template argument must be hoisted to
        // namespace scope (hoistSymmDecl), which a provider string generator
        // cannot reach. The pool copy is linear: the allocator's flat pool
        // holds exactly the canonical cells in ascending-lex order (the same
        // pinned order the store uses), so no per-cell unlinearize is needed.
        if spec.MaskName.IsSome then
            raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf "provider '%s': load_compound over a packed variable ('%s') is not supported" spec.Provider spec.VarName)))
        match pspec.GenReadPacked with
        | None ->
            raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf "provider '%s' does not support packed (symmetric/antisymmetric) variables (variable '%s')" spec.Provider spec.VarName)))
        | Some gen ->
            (match binding.Type with
             | ArrayElem arrTy ->
                 // Distribute only whole-variable reads, and only when the
                 // program has MPI scaffolding (windows are small and local).
                 let opts : Blade.ProviderRegistry.PackedReadOpts =
                     { Distribute = mpiProgramOn () && spec.Window.IsNone
                       Window = spec.Window }
                 let assemble = gen spec.FilePath spec.VarName name spec.VarType opts
                 let elemCpp = elemTypeToCpp arrTy.ElemType
                 let componentExtents =
                     arrTy.IndexTypes |> List.collect (fun idx -> List.replicate idx.Rank idx.Extent)
                 let rank = componentExtents.Length
                 let extentTerms =
                     componentExtents |> List.map (fun e ->
                         match e with
                         | IRLit (IRLitInt n) -> sprintf "%d" n
                         | _ -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf "packed provider read of '%s' requires literal extents" spec.VarName))))
                 let extentsName = sprintf "%s_extents" name
                 let extentsArr = sprintf "size_t %s[] = { %s };" extentsName (String.concat ", " extentTerms)
                 let symmVec = buildSymmVec binding.Type
                 let symmArg =
                     if hasRealSymmetry symmVec then hoistSymmDecl (sprintf "%s_symm" name) symmVec
                     else "nullptr"
                 let allocLine =
                     match emitAllocRhs (classifyOutputStorage binding.Type) elemCpp rank symmArg extentsName with
                     | Ok rhs -> sprintf "Array<%s, %d> %s = %s;" elemCpp rank name rhs
                     | Error msg -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf "packed provider read '%s': %s" spec.VarName msg)))
                 let copy = genPackedPoolCopy arrTy name name spec.VarName false
                 ((assemble @ [extentsArr; allocLine] @ copy @ [sprintf "delete[] %s_flat;" name])
                  |> List.map (fun s -> ind + s),
                  addVarName binding.Id name ctx)
             | _ -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.iceCodegen (sprintf "packed provider read '%s': binding is not array-typed" spec.VarName))))
    else
    let readCode =
        (match spec.MaskName, spec.MaskType with
         | Some maskName, Some maskType ->
             (match pspec.GenReadCompoundVar with
              | Some gen -> gen spec.FilePath spec.VarName maskName name spec.VarType maskType
              | None -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf "provider '%s' does not support load_compound (variable '%s')" spec.Provider spec.VarName))))
         | _ ->
             pspec.GenReadVar spec.FilePath spec.VarName name spec.VarType)
    (readCode |> List.map (fun s -> ind + s), addVarName binding.Id name ctx)

and genProviderWriteBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    let ind = indentStr ctx
    // Deferred provider write (`alias.write("path", A)`): flatten the source
    // array into `<base>_flat` (row-major Horner, the inverse of genReadVar's
    // materialization copy), hand the provider's writer that buffer, then
    // release it. `base` is the write binding's own cpp name suffixed over
    // the source's, so two writes of one array cannot collide.
    let spec = ctx.ProviderWrites.[binding.Id]
    let pspec =
        match Blade.ProviderRegistry.tryFind spec.Provider with
        | Some p -> p
        | None -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf "provider '%s' is not registered â€” was ProviderStatics.install () run?" spec.Provider)))
    let srcCpp =
        match Map.tryFind spec.SourceId ctx.VarNames with
        | Some n -> n
        | None -> sanitizeCppName spec.VarName
    let baseName = sprintf "%s_wr%d" srcCpp (int binding.Id)
    let arrTy = spec.SourceType
    let elemCpp = elemTypeToCpp arrTy.ElemType
    let componentExtents =
        arrTy.IndexTypes |> List.collect (fun idx -> List.replicate idx.Rank idx.Extent)
    let rank = componentExtents.Length
    let extentTerms =
        componentExtents |> List.map (fun e ->
            match e with
            | IRLit (IRLitInt n) -> sprintf "%d" n
            | _ -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf "provider write of '%s' requires literal extents" spec.VarName))))
    let isPacked =
        arrTy.IndexTypes |> List.exists (fun idx -> idx.Symmetry <> SymNone && idx.Rank >= 2)
    if isPacked then
        // Packed (SymIdx/AntisymIdx) source: the flatten is a LINEAR pool
        // copy — the allocator's flat pool holds exactly the canonical
        // cells in ascending-lex order, which is the store's pool order.
        // GenReadPacked presence is the provider's packed-layout capability
        // flag (read and write go together: both are pool-order I/O).
        if pspec.GenReadPacked.IsNone then
            raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (sprintf "provider '%s' does not support packed (symmetric/antisymmetric) writes (variable '%s')" spec.Provider spec.VarName)))
        let poolCount =
            deviceBufferCardinality (deviceBufferTypeOfArray arrTy) |> exprToCpp ctx.VarNames
        let flatten =
            [ sprintf "// Write %s (packed pool) to %s" spec.VarName spec.FilePath
              sprintf "%s* %s_flat = new %s[%s];" elemCpp baseName elemCpp poolCount ]
            @ genPackedPoolCopy arrTy srcCpp baseName spec.VarName true
        let writeCode = pspec.GenWriteVar spec.FilePath spec.VarName baseName arrTy spec.DimNames
        let cleanup = [ sprintf "delete[] %s_flat;" baseName ]
        (guardProviderWrite ind (flatten @ writeCode @ cleanup), ctx)
    else
    let extentNames = extentTerms |> List.mapi (fun i _ -> sprintf "%s_ext%d" baseName i)
    let extentDecls =
        List.zip extentNames extentTerms
        |> List.map (fun (n, t) -> sprintf "size_t %s = %s;" n t)
    let idxVars = [ for i in 0 .. rank - 1 -> sprintf "%s_i%d" baseName i ]
    let openLoops =
        idxVars |> List.mapi (fun d iv ->
            let indl = String.replicate d "    "
            sprintf "%sfor (size_t %s = 0; %s < %s; %s++) {" indl iv iv extentNames.[d] iv)
    let nestedSub = idxVars |> List.map (sprintf "[%s]") |> String.concat ""
    let flatIdx =
        match idxVars with
        | [] -> "0"
        | first :: _ ->
            let mutable acc = first
            for i in 1 .. rank - 1 do
                acc <- sprintf "(%s) * %s + %s" acc extentNames.[i] idxVars.[i]
            acc
    let bodyInd = String.replicate rank "    "
    let flatten =
        [ sprintf "// Write %s to %s" spec.VarName spec.FilePath ]
        @ extentDecls
        @ [ sprintf "%s* %s_flat = new %s[%s];" elemCpp baseName elemCpp (String.concat " * " extentNames) ]
        @ openLoops
        @ [ sprintf "%s%s_flat[%s] = %s%s;" bodyInd baseName flatIdx srcCpp nestedSub ]
        @ [ for d in rank - 1 .. -1 .. 0 -> sprintf "%s}" (String.replicate d "    ") ]
    let writeCode = pspec.GenWriteVar spec.FilePath spec.VarName baseName arrTy spec.DimNames
    let cleanup = [ sprintf "delete[] %s_flat;" baseName ]
    (guardProviderWrite ind (flatten @ writeCode @ cleanup), ctx)


/// rand.uniform/normal(key, shape): allocate the dense Float64 array (self-typed
/// from the shape) and fill its flat contiguous pool with `card` deterministic
/// draws keyed by `key`, via the blade_rand runtime. All rand arrays are dense
/// SymNone, so pool_base gives the full pool and the draw count is the product
/// of extents. Mirrors the fill_random dense path but uses a flat pool fill.
and genRandGenBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    let kind, keyExpr =
        match ctx.RandomInits.[binding.Id] with
        | RandGen (k, key) -> k, key
        | FillModulus _ -> "uniform", IRLit (IRLitInt 0L)  // unreachable: dispatch guards this
    match binding.Type with
    | ArrayElem arrTy ->
        let elemCpp = elemTypeToCpp arrTy.ElemType
        let extents = arrTy.IndexTypes |> List.collect (fun idx -> List.replicate idx.Rank idx.Extent)
        let rank = extents.Length
        let nonLiteral = extents |> List.exists (fun e -> match e with IRLit (IRLitInt _) -> false | _ -> true)
        if nonLiteral then
            ([sprintf "%s#error \"rand binding '%s' requires literal extents\"" ind name], addVarName binding.Id name ctx)
        else
            let extentTerms = extents |> List.map (fun e -> match e with IRLit (IRLitInt n) -> string n | _ -> "0")
            let extentsName = sprintf "%s_extents" name
            let extentsArr = sprintf "%ssize_t %s[] = { %s };" ind extentsName (String.concat ", " extentTerms)
            let card = extents |> List.fold (fun acc e -> match e with IRLit (IRLitInt n) -> acc * n | _ -> acc) 1L
            let allocLine =
                sprintf "%sArray<%s, %d> %s = { allocate<typename promote<%s, %d>::type, nullptr>(%s), %s };"
                    ind elemCpp rank name elemCpp rank extentsName extentsName
            let fillLine =
                sprintf "%sblade_rand::%s(nested_array_utilities::pool_base(%s.data), (size_t)%dLL, (int64_t)(%s));"
                    ind kind name card (exprToCpp ctx.VarNames keyExpr)
            ([extentsArr; allocLine; fillLine], addVarName binding.Id name ctx)
    | _ ->
        ([sprintf "%s#error \"rand binding '%s' is not an array type\"" ind name], addVarName binding.Id name ctx)

and genRandomInitBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Random-fill constructor (`let A: Array<..> = fill_random(mod)`):
    // allocate the nested Array (same form as a literal binding) and fill it
    // with rand() % mod via the runtime fill_random. Shape/elem come from the
    // binding's array type; the modulus from RandomInits. Rectangular, so
    // SYMM defaults to nullptr. fill_random deduces its type from the first
    // arg, so pass the raw nested pointer (.data), not the Array wrapper --
    // the wrapper would deduce as a scalar leaf and never recurse.
    let modExpr =
        match ctx.RandomInits.[binding.Id] with
        | FillModulus m -> m
        | RandGen _ -> IRLit (IRLitInt 1L)  // unreachable: dispatch routes RandGen to genRandGenBinding
    (match binding.Type with
     | ArrayElem arrTy ->
         let elemCpp = elemTypeToCpp arrTy.ElemType
         let allDenseRank1 =
             arrTy.IndexTypes |> List.forall (fun idx -> idx.Rank = 1 && idx.Symmetry = SymNone)
         let hasHermitian =
             arrTy.IndexTypes |> List.exists (fun idx -> idx.Symmetry = SymHermitian)
         if allDenseRank1 then
             // Dense-rectangular path: unchanged (byte-compatible with the
             // pre-arc-3 emission â€” the runtime fill_random walks the shape).
             let rank = arrayRank arrTy
             let extentNames = arrTy.IndexTypes |> List.mapi (fun i _ -> sprintf "%s_extent_%d" name i)
             let extentDecls =
                 arrTy.IndexTypes |> List.mapi (fun i idx ->
                     match idx.Extent with
                     | IRLit (IRLitInt n) -> sprintf "%ssize_t %s_extent_%d = %d;" ind name i n
                     | _ -> sprintf "%s#error \"fill_random binding '%s' has a non-literal extent at dim %d\"" ind name i)
             let extentsArr = sprintf "%ssize_t %s_extents[] = { %s };" ind name (String.concat ", " extentNames)
             let allocLine =
                 sprintf "%sArray<%s, %d> %s = { allocate<typename promote<%s, %d>::type, nullptr>(%s_extents), %s_extents };"
                     ind elemCpp rank name elemCpp rank name name
             let fillLine =
                 sprintf "%sfill_random(%s.data, %s_extents, (int)(%s));" ind name name (exprToCpp ctx.VarNames modExpr)
             (extentDecls @ [extentsArr; allocLine; fillLine], addVarName binding.Id name ctx)
         elif hasHermitian then
             // Hermitian stores the full n^2 cells but they are CONSTRAINT-
             // COUPLED (A(i,j) = conj(A(j,i))): independent pool draws would
             // violate the invariant, so hermitian fill needs a canonical-
             // half fill + mirrored conjugation â€” not yet emitted.
             ([sprintf "%s#error \"fill_random binding '%s': HermitianIdx is not supported (stored cells are constraint-coupled)\"" ind name],
              addVarName binding.Id name ctx)
         else
             // GENERALIZED fill (arc 3, formalism 3.5): one draw per STORED
             // cell. Compact storage classes (SymIdx/AntisymIdx, mixed with
             // dense axes) allocate with their SYMM vector and fill the flat
             // pool linearly â€” the pool holds exactly the canonical cells, so
             // symmetry holds by construction, antisym diagonals stay
             // implicit zeros, and the draw count is the storage cardinality
             // (deviceBufferCardinality â€” same closed forms as allocation).
             let componentExtents =
                 arrTy.IndexTypes |> List.collect (fun idx -> List.replicate idx.Rank idx.Extent)
             let rank = componentExtents.Length
             let nonLiteral =
                 componentExtents |> List.exists (fun e -> match e with IRLit (IRLitInt _) -> false | _ -> true)
             if nonLiteral then
                 ([sprintf "%s#error \"fill_random binding '%s' requires literal extents\"" ind name],
                  addVarName binding.Id name ctx)
             else
                 let extentTerms =
                     componentExtents |> List.map (fun e ->
                         match e with IRLit (IRLitInt n) -> sprintf "%d" n | _ -> "0")
                 let extentsName = sprintf "%s_extents" name
                 let extentsArr = sprintf "%ssize_t %s[] = { %s };" ind extentsName (String.concat ", " extentTerms)
                 let symmVec = buildSymmVec binding.Type
                 let symmArg =
                     if hasRealSymmetry symmVec then hoistSymmDecl (sprintf "%s_symm" name) symmVec
                     else "nullptr"
                 let allocLines =
                     match emitAllocRhs (classifyOutputStorage binding.Type) elemCpp rank symmArg extentsName with
                     | Ok rhs -> [sprintf "%sArray<%s, %d> %s = %s;" ind elemCpp rank name rhs]
                     | Error msg -> [sprintf "%s#error \"fill_random '%s': %s\"" ind name msg]
                 let poolCount =
                     deviceBufferCardinality (deviceBufferTypeOfArray arrTy)
                     |> exprToCpp ctx.VarNames
                 let fillLines =
                     [ sprintf "%s{ auto* __fr_pool = nested_array_utilities::pool_base(%s.data);" ind name
                       sprintf "%s  for (size_t __fr_i = 0; __fr_i < %s; __fr_i++) { __fr_pool[__fr_i] = static_cast<%s>(rand() %% (int)(%s)); } }"
                           ind poolCount elemCpp (exprToCpp ctx.VarNames modExpr) ]
                 (extentsArr :: allocLines @ fillLines, addVarName binding.Id name ctx)
     | _ ->
         ([sprintf "%s#error \"fill_random binding '%s' is not an array type\"" ind name], addVarName binding.Id name ctx))


and genCompoundInitBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Compound-construction constructor (`let B = compound(dense, mask)`):
    // materialize the compound index from the bool mask (P0,
    // genCompoundIndexFromMask), then scatter the dense array's present
    // leading cells into a compact buffer and bundle a Compound<T, RANK>.
    // The mask covers a LEADING PREFIX of dense's dims (validated by
    // compoundViewType in typecheck); remaining dims fold into trailing_stride.
    //
    // dense and mask are lowered IRVar references (recorded in CompoundInits);
    // exprToCpp yields their in-scope C++ variable names. Both are Array<...>
    // wrappers, so .data is the nested pointer and pool_base flattens to the
    // contiguous row-major pool the scatter walks.
    let (denseExpr, maskExpr) = ctx.CompoundInits.[binding.Id]
    let denseName = exprToCpp ctx.VarNames denseExpr
    // The mask operand may be written INLINE inside compound(...) --
    // `compound(A, mask(A, p))` -- in which case it lowers to a bare
    // IRMask node. exprToCpp cannot render a mask inline (it needs a
    // multi-statement materialization), so it would emit the
    // BLADE_CODEGEN_ERROR sentinel as the "name". Materialize such an
    // inline mask into a Bool presence-array temp first (the same helper
    // the method_for auto-materialize path uses), then feed that temp's
    // name to the index builder. A let-bound mask
    // (`let m = mask(...); compound(A, m)`) arrives as an IRVar and skips
    // this. maskPre is prepended to the emitted lines below.
    let (maskPre, maskName) =
        match maskExpr with
        | IRMask _ ->
            let tmpName = sprintf "%s__masksrc" name
            (match materializeInlineForm emptySubst ctx.VarNames tmpName "bool" maskExpr with
             | Some stmts -> (stmts |> List.map (fun s -> ind + s), tmpName)
             | None -> ([], exprToCpp ctx.VarNames maskExpr))
        | _ -> ([], exprToCpp ctx.VarNames maskExpr)
    (match binding.Type with
     | ArrayElem arrTy when isCompoundArrayType arrTy ->
         let leadRank =
             arrTy.IndexTypes
             |> List.tryFind (fun ix -> ix.IxKind = IxKCompound)
             |> Option.map (fun ix -> ix.Rank)
             |> Option.defaultValue 1
         let elemCpp = elemTypeToCpp arrTy.ElemType
         // The compound array type carries leadRank (compound) + trailing
         // slots; the number of trailing dims = arrTy.IndexTypes.Length - 1.
         let trailingDimCount = arrTy.IndexTypes.Length - 1
         let idxName = sprintf "%s_idx" name
         let (idxLines, _) = genCompoundIndexFromMask maskName leadRank idxName
         // trail = product of dense.extents[leadRank .. leadRank+trailingDimCount-1]
         let trailTerms =
             [ for d in 0 .. trailingDimCount - 1 -> sprintf "%s.extents[%d]" denseName (leadRank + d) ]
         let trailExpr = match trailTerms with | [] -> "1" | xs -> String.concat " * " xs
         let lines =
             maskPre
             @ (idxLines |> List.map (fun l -> ind + l))
             @ [ sprintf "%ssize_t %s_trail = %s;" ind name trailExpr
                 sprintf "%s%s* %s_densepool = nested_array_utilities::pool_base(%s.data);" ind elemCpp name denseName
                 sprintf "%s%s* %s_compact = new %s[%s->cardinality * %s_trail];" ind elemCpp name elemCpp idxName name
                 // scatter present leading cells (row-major prefix-popcount)
                 compactScatter { Ind = ind; Name = name; IdxName = idxName }
                 sprintf "%snested_array_utilities::Compound<%s, %d> %s { %s_compact, %s, %s_trail };" ind elemCpp leadRank name name idxName name ]
         (lines, addVarName binding.Id name ctx)
     | _ ->
         ([sprintf "%s#error \"compound() binding '%s' is not a CompoundIdx array type\"" ind name], addVarName binding.Id name ctx))


/// Rearrangement combinators (group_by, sort, mask, transpose, decompact,
/// intersect, union, unique, array negate/conjugate) index their array
/// inputs by NAME in the emitted C++, so they need a MATERIALIZED input.
/// An input that is still an unforced computation â€” a deferred binding
/// (only a "<deferred computation>" comment in the C++), or an inline
/// computation node â€” is forced here first, exactly as |> compute would
/// force it. Returns (forceCode, ctx', expr') where expr' names the
/// materialized array; callers rebuild their form node from expr' before
/// rendering. `tmpBase` names the synthetic temporary when the input is an
/// inline computation (and is the fallback name for a deferred binding
/// missing from VarNames); pass a distinct base per input slot.
and forceDeferredArrayInput (ctx: CodeGenContext) (builder: IRBuilder) (tmpBase: string) (expr: IRExpr) : string list * CodeGenContext * IRExpr =
    match expr with
    | IRVar (srcId, ty) when Map.containsKey srcId ctx.DeferredComputations ->
        // Materialize under the deferred binding's own name, then drop it
        // from the deferred map: later consumers (including a second
        // rearrangement over the same binding) must see the materialized
        // array, not re-force it into a C++ redefinition.
        let srcName = Map.tryFind srcId ctx.VarNames |> Option.defaultValue tmpBase
        let matBinding = { Id = srcId; Name = srcName; Type = ty; Value = IRCompute (IRVar (srcId, ty)); IsConst = true; IsMutable = false }
        let (code, ctx1) = genBinding ctx matBinding builder
        let ctx1 = { ctx1 with DeferredComputations = Map.remove srcId ctx1.DeferredComputations }
        (code @ [""], ctx1, expr)
    | IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRComposeMeth _ | IRBind _ | IRCompute _ ->
        // Inline computation as the array argument: materialize into a
        // synthetic temporary and rearrange over that.
        let tmpId = builder.FreshId()
        let ty = inferExprType expr
        let matBinding = { Id = tmpId; Name = tmpBase; Type = ty; Value = IRCompute expr; IsConst = true; IsMutable = false }
        let (code, ctx1) = genBinding ctx matBinding builder
        (code @ [""], ctx1, IRVar (tmpId, ty))
    | _ -> ([], ctx, expr)


and genMaskBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (arrExpr: IRExpr) (predExpr: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // mask(array, pred): a Bool PRESENCE array in the SOURCE index space —
    // m[i] = pred(A[i]), same extent as A, NO compaction (compaction happens
    // downstream in compound(A, m)). See materializeMaskForm. [This comment
    // previously said "eager compaction", contradicting the emitted code, and
    // misled a semantics audit — the presence-array behavior is the truth.]
    // Strict elem-type inference (emits #error if unresolvable) and
    // predicate-callable validation happen here at the call site; the
    // shared `materializeInlineForm` helper just emits the C++ template.
    //
    // Validation accepts any predicate that resolves to a
    // single-parameter callable through resolveCallable.
    let (forceCode, ctx, arrExpr) = forceDeferredArrayInput ctx builder (sprintf "%s__arr" name) arrExpr
    let (elemET, elemErrCode) = inferElemTypeStrict ctx ind arrExpr "mask"
    let elemStr = elemTypeToCpp elemET
    let predErrCode =
        match resolveCallable predExpr with
        | Some callable when callable.Params.Length = 1 -> []
        | _ -> codegenError ctx ind "mask: predicate must resolve to a single-parameter callable; got something else (typechecker or IR bug)"
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name elemStr (IRMask (arrExpr, predExpr)) with
        | Some s -> s
        | None -> []  // Unreachable: helper supports IRMask
    let code = forceCode @ elemErrCode @ predErrCode @ [sprintf "%s// mask: count + compact" ind] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genIntersectBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (aExpr: IRExpr) (bExpr: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // intersect(A, B): elements present in both arrays.
    let (forceCodeA, ctx, aExpr) = forceDeferredArrayInput ctx builder (sprintf "%s__a" name) aExpr
    let (forceCodeB, ctx, bExpr) = forceDeferredArrayInput ctx builder (sprintf "%s__b" name) bExpr
    let (elemET, elemErrCode) = inferElemTypeStrict ctx ind aExpr "intersect"
    let elemStr = elemTypeToCpp elemET
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name elemStr (IRIntersect (aExpr, bExpr)) with
        | Some s -> s
        | None -> []
    let code = forceCodeA @ forceCodeB @ elemErrCode @ [sprintf "%s// intersect: build set from B, scan A" ind] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genUnionBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (aExpr: IRExpr) (bExpr: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // union(A, B): all elements from A, plus elements from B not in A.
    let (forceCodeA, ctx, aExpr) = forceDeferredArrayInput ctx builder (sprintf "%s__a" name) aExpr
    let (forceCodeB, ctx, bExpr) = forceDeferredArrayInput ctx builder (sprintf "%s__b" name) bExpr
    let (elemET, elemErrCode) = inferElemTypeStrict ctx ind aExpr "union"
    let elemStr = elemTypeToCpp elemET
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name elemStr (IRUnion (aExpr, bExpr)) with
        | Some s -> s
        | None -> []
    let code = forceCodeA @ forceCodeB @ elemErrCode @ [sprintf "%s// union: all of A, plus elements from B not in A" ind] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genUniqueBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (arrExpr: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // unique(A): dedup, preserving first-occurrence order. Two-pass:
    // first counts unique elements via std::unordered_set, then fills
    // the output array on a second pass (clearing the set in between
    // so first-occurrence membership testing repeats identically).
    let (forceCode, ctx, arrExpr) = forceDeferredArrayInput ctx builder (sprintf "%s__arr" name) arrExpr
    let (elemET, elemErrCode) = inferElemTypeStrict ctx ind arrExpr "unique"
    let elemStr = elemTypeToCpp elemET
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name elemStr (IRUnique arrExpr) with
        | Some s -> s
        | None -> []
    let code = forceCode @ elemErrCode @ [sprintf "%s// unique: dedup via unordered_set, first-occurrence order" ind] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genGroupByBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (vals: IRExpr) (gk: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // group_by: per-group nested pointer allocation. Each grouped[g] is a
    // separately-allocated buffer of size offsets[g+1] - offsets[g], holding
    // the values for group g in the order discovered by the keys scan.
    // Layout matches normal rank-2 nested arrays so dimensional currying
    // (kernel taking a sub-array) works without touching the loop builder.
    // Outer extent = gk__ngroups; inner is ragged. Track grouped â†’ gk so
    // future ragged-aware iteration can recover offsets.
    //
    // The outer pointer-array is wrapped in Array<T*, 1>. The wrapper's
    // .extents points at the 2-element local size_t array {ngroups, 0};
    // .extents[0] = ngroups, .extents[1] reads 0 (placeholder for the
    // ragged inner). Element type T* keeps `grouped[g]` as a bare row
    // pointer for downstream peeling. Print's inner-loop bound of 0
    // means no values printed, matching prior behavior.
    //
    // group_by's copy loop indexes vals by name, so it needs a MATERIALIZED
    // input; the shared helper forces a still-deferred or inline vals first.
    let (forceCode, ctx, vals) = forceDeferredArrayInput ctx builder (sprintf "%s__vals" name) vals
    let valsName = exprToCppCtx ctx vals
    let gkName = exprToCppCtx ctx gk
    let (elemType, elemErrCode) = inferElemTypeStrict ctx ind vals "group_by"
    let elemStr = elemTypeToCpp elemType
    let code = elemErrCode @ [
        sprintf "%s// group_by: per-group nested allocation, group-contiguous via gk__perm" ind
        sprintf "%ssize_t %s_extents[2] = {%s__ngroups, 0}; // inner extent is ragged" ind name gkName
        sprintf "%sArray<%s*, 1> %s = { new %s*[%s__ngroups], %s_extents };" ind elemStr name elemStr gkName name
        sprintf "%sfor (size_t __g = 0; __g < %s__ngroups; __g++) {" ind gkName
        sprintf "%s    size_t __sz = %s__offsets[__g + 1] - %s__offsets[__g];" ind gkName gkName
        sprintf "%s    %s[__g] = new %s[__sz];" ind name elemStr
        sprintf "%s    for (size_t __k = 0; __k < __sz; __k++) {" ind
        sprintf "%s        %s[__g][__k] = %s[%s__perm[%s__offsets[__g] + __k]];" ind name valsName gkName gkName
        sprintf "%s    }" ind
        sprintf "%s}" ind
    ]
    let ctx' = addVarName binding.Id name ctx
    let ctx' = { ctx' with GroupedArrays = Map.add name gkName ctx'.GroupedArrays }
    (forceCode @ code, ctx')



and genSortBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (arrExpr: IRExpr) (keyExpr: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // sort(array, key): stable ascending sort by key.
    //
    // Phase 1 (current): eager materialization. Construct a permutation via
    // std::stable_sort with a comparator that calls the user's key function,
    // then write the permuted elements into a fresh contiguous buffer.
    //
    // Phase 2 (future): lazy chain handle. The result would be a handle
    // recording (key_fn, permutation, source_pointer); materialization would
    // be deferred to first access. Long chains of sorts and other rearrange-
    // ments can then be analyzed by the compiler before any layout commits,
    // enabling sort-skip, merge-style joins, and other optimizations.
    // Materialization caching (memoize-on-first-access) sits downstream of
    // those analyses, not as a substitute for them.
    let (forceCode, ctx, arrExpr) = forceDeferredArrayInput ctx builder (sprintf "%s__arr" name) arrExpr
    let (elemET, elemErrCode) = inferElemTypeStrict ctx ind arrExpr "sort"
    let elemStr = elemTypeToCpp elemET
    // Validate single-param key callable. Helper falls back to a 0L key
    // (preserving input order); the #error here surfaces the IR bug
    // before the silently-wrong sort runs.
    let keyErrCode =
        match resolveCallable keyExpr with
        | Some callable when callable.Params.Length = 1 -> []
        | _ -> codegenError ctx ind "sort: key must resolve to a single-parameter callable; got something else (typechecker or IR bug)"
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name elemStr (IRSort (arrExpr, keyExpr)) with
        | Some s -> s
        | None -> []
    let code = forceCode @ elemErrCode @ keyErrCode @ [sprintf "%s// sort: stable_sort on permutation, eager materialization" ind] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genTransposeBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (arrExpr: IRExpr) (d1: int) (d2: int) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // transpose(array, [d1, d2]): hard transpose â€” allocate a fresh pool at
    // the swapped extents and copy with axes d1/d2 exchanged. Eager
    // materialization (same phase-1 strategy as sort); the result is an
    // independent array with no aliasing back to the source. TypeCheck has
    // already verified both axes are arity-1 SymNone and in range.
    let (forceCode, ctx, arrExpr) = forceDeferredArrayInput ctx builder (sprintf "%s__arr" name) arrExpr
    let (elemET, elemErrCode) = inferElemTypeStrict ctx ind arrExpr "transpose"
    let elemStr = elemTypeToCpp elemET
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name elemStr (IRTranspose (arrExpr, d1, d2)) with
        | Some s -> s
        | None -> []
    let code = forceCode @ elemErrCode @ [sprintf "%s// transpose: hard (swapped-extent alloc + axis-swapped copy)" ind] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genDecompactBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (arrExpr: IRExpr) (d: int) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // decompact(array, d): pull the compact component at dim d out as a
    // free Idx. Hard materialization â€” allocate a fresh dense pool and
    // scatter the canonical (triangular-packed) source elements into all
    // of the decompacted component's image positions, applying the per-
    // class transform (Sym copy / Antisym sign + zero diagonal / Hermitian
    // conj). TypeCheck has verified dim d targets a compact slot and that
    // the Antisym middle-peel case is excluded.
    let (forceCode, ctx, arrExpr) = forceDeferredArrayInput ctx builder (sprintf "%s__arr" name) arrExpr
    let (elemET, elemErrCode) = inferElemTypeStrict ctx ind arrExpr "decompact"
    let elemStr = elemTypeToCpp elemET
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name elemStr (IRDecompact (arrExpr, d)) with
        | Some s -> s
        | None -> []
    let code = forceCode @ elemErrCode @ [sprintf "%s// decompact: hard (dense alloc + symmetry-expanding scatter)" ind] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genArrayNegateConjugateBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (arrExpr: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Whole-array eager negate/conjugate (the cheap intra-group transposes).
    // Type-preserving: same-shape alloc + flat contiguous-pool transform.
    let isConj = (match binding.Value with IRArrayConjugate _ -> true | _ -> false)
    let label = if isConj then "conjugate" else "negate"
    let (forceCode, ctx, arrExpr) = forceDeferredArrayInput ctx builder (sprintf "%s__arr" name) arrExpr
    let (elemET, elemErrCode) = inferElemTypeStrict ctx ind arrExpr (sprintf "array_%s" label)
    let elemStr = elemTypeToCpp elemET
    let form = if isConj then IRArrayConjugate arrExpr else IRArrayNegate arrExpr
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name elemStr form with
        | Some s -> s
        | None -> []
    let code = forceCode @ elemErrCode @ [sprintf "%s// array_%s: whole-array eager transform (same-shape alloc + pool loop)" ind label] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genGramBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // gram(A, B) = A * B^H. Materialized as a triangular (same-array,
    // symmetric/Hermitian) or full (distinct, dense) scatter with an inner
    // contracted-axis reduction. The shared helper emits the statement form.
    let elemStr =
        match binding.Type with
        | ArrayElem at -> irTypeToCpp at.ElemType
        | _ -> "double"
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name elemStr binding.Value with
        | Some s -> s
        | None -> []
    let code = [sprintf "%s// gram: A * B^H (Gram product)" ind] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genReduceBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (arrExpr: IRExpr) (kernelExpr: IRExpr) (initExpr: IRExpr option) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // reduce(array, op): T/S reduction. Consumes the innermost dim by a
    // binary kernel, producing a scalar (rank-1 input only for now).
    //
    // Empty-array handling (post-extents integration):
    //   - Static extent > 0: standard loop, no runtime check
    //     (typecheck already proved non-emptiness)
    //   - Dynamic extent: emit a runtime guard that aborts cleanly on
    //     empty rather than reading uninitialized memory from arr[0]
    //   - Static extent = 0: typecheck rejects before reaching here
    //
    // Section kernels like (+) lower to callables during Lowering
    // with properly-resolved scalar types. Resolution flows
    // through `resolveCallable`.
    let arrName = exprToCppCtx ctx arrExpr
    let (elemType, elemErrCode) = inferElemTypeStrict ctx ind arrExpr "reduce"
    let elemStr = elemTypeToCpp elemType

    // Length accessor: a rank-1 ragged-family operand (a let-bound row of
    // a ragged/DepIdx array or of a group_by result) is a RaggedRow<T>,
    // which carries its length inline as `.len` -- not `.extents[0]`.
    // Same predicate the expression-form reduce, IRExtent, and the
    // sub-view binding use, so the accessor always matches the declared
    // type. Element access `%s[%s]` works for both via operator[].
    let isRaggedRowOperand =
        match inferExprType arrExpr with
        | ArrayElem at -> isRaggedRowType at
        | _ -> false
    // A compound operand reduces over its compact buffer: bound =
    // cardinality * trailing_stride (all present values), elements via
    // .data[i]. This is what makes `reduce(compound(A, mask(A, p)), (+))`
    // -- SQL's SUM(x) WHERE p -- a one-liner. Mirrors the expression-form
    // reduce's compound handling.
    let isCompoundOperand =
        match inferExprType arrExpr with
        | ArrayElem at -> isCompoundArrayType at
        | _ -> false
    let boundExpr =
        if isRaggedRowOperand then sprintf "%s.len" arrName
        elif isCompoundOperand then sprintf "(%s.idx->cardinality * %s.trailing_stride)" arrName arrName
        else sprintf "%s.extents[0]" arrName
    let elemAt (i: string) =
        if isCompoundOperand then sprintf "%s.data[%s]" arrName i
        else sprintf "%s[%s]" arrName i

    // Decide whether to emit a runtime extent check based on whether
    // the array's innermost-dim extent is statically known.
    let isStaticallyNonEmpty =
        match inferExprType arrExpr with
        | ArrayElem at when at.IndexTypes.Length >= 1 ->
            match tryEvalIntIR at.IndexTypes.[at.IndexTypes.Length - 1].Extent with
            | Some n -> n > 0L
            | None -> false
        | _ -> false

    let guardLines =
        // The 3-arg (init) form defines the empty fold as init, so it never
        // needs an emptiness guard, static or dynamic.
        if isStaticallyNonEmpty || initExpr.IsSome then []
        else [
            sprintf "%s// reduce: dynamic extent â€” runtime non-emptiness guard" ind
            sprintf "%sif (%s == 0) { blade_rt::panic(\"BL8003\", \"reduce: empty array, no reduction possible\", nullptr, 0); }" ind boundExpr
        ]

    let code =
        match resolveCallable kernelExpr with
        | Some callable when callable.Params.Length = 2 ->
            // Stage 3c.2: wrapper-based emission. The fold's
            // accumulator and the wrapper agree on type â€” both come
            // from the array's elem type via the inferReduce
            // unification â€” so the call `__r = __wrap(__r, arr[i])`
            // type-checks without narrowing/conversion warnings.
            let (wrapperCode, wname) = genCallableWrapper name callable
            let wrapperLines = wrapperCode |> List.map (fun s -> ind + s)
            // Seed and loop start: without init, seed = arr[0], fold from 1;
            // with init, seed = init, fold over ALL elements from 0.
            let (seedStr, loopStart) =
                match initExpr with
                | Some initE -> (exprToCppCtx ctx initE, "0")
                | None -> (elemAt "0", "1")
            elemErrCode @ guardLines @ wrapperLines @ [
                sprintf "%s// reduce: accumulator loop, eager" ind
                sprintf "%s%s %s = %s;" ind elemStr name seedStr
                sprintf "%sfor (size_t __ri = %s; __ri < %s; __ri++) {" ind loopStart boundExpr
                sprintf "%s    %s = %s(%s, %s);" ind name wname name (elemAt "__ri")
                sprintf "%s}" ind
            ]
        | _ ->
            let errLines = codegenError ctx ind "reduce: kernel must resolve to a binary callable (typechecker or IR bug if not)"
            elemErrCode @ errLines
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')

and genReduceComputeBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (compExpr: IRExpr) (kernelExpr: IRExpr) (seedExpr: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // The fused reduction terminal: reduce(deferred, op[, init]). Fold every
    // cell of a deferred computation â€” a single unforced apply, or an <&!>
    // fusion tree of them â€” into scalar accumulator(s) through the fold
    // kernel's wrapper, WITHOUT materializing any output array. One loop
    // nest total; a fusion tree gets one accumulator per leaf and packs a
    // flat tuple (mirroring genFusionTree's make_tuple convention, so tuple
    // destructuring projects with flat get<i> indices).
    let rec resolveDeferred e =
        match e with
        | IRVar (id, _) ->
            (match Map.tryFind id ctx.DeferredComputations with
             | Some d -> resolveDeferred d
             | None -> e)
        | _ -> e
    let rec collectLeaves e =
        match resolveDeferred e with
        | IRFusion (l, r) -> collectLeaves l @ collectLeaves r
        | other -> [other]
    let leaves = collectLeaves compExpr
    let infos = leaves |> List.choose (function IRApplyCombinator i -> Some i | _ -> None)
    let ctx' = addVarName binding.Id name ctx
    if infos.IsEmpty || infos.Length <> leaves.Length then
        (codegenError ctx ind "reduce over a deferred computation requires unforced method_for/object_for applications at every leaf (typechecker or IR bug if not)", ctx')
    else
        // The fold bypasses genApplyCombinator's special input paths
        // (ragged peel, grouped, compound, CUDA) â€” reject what they handle.
        let unsupportedInput =
            infos |> List.exists (fun info ->
                info.ArrayTypes |> List.exists (fun at ->
                    at.IndexTypes |> List.exists (fun ix ->
                        isRaggedFamilyKind ix.IxKind || ix.IxKind = IxKDepInner
                        || ix.IxKind = IxKGroupOuter || ix.IxKind = IxKCompound)))
        if unsupportedInput then
            (codegenError ctx ind "reduce over a deferred computation is not supported for ragged/grouped/compound inputs yet â€” force with |> compute and reduce the array", ctx')
        else
            match resolveCallable kernelExpr with
            | Some callable when callable.Params.Length = 2 ->
                let (wrapperCode, wname) = genCallableWrapper name callable
                let wrapperLines = wrapperCode |> List.map (fun s -> ind + s)
                // Accumulator C++ type: the fold callable's return type (the
                // checker unified it with every leaf's element type).
                let elemStr =
                    match callable.RetType with
                    | IRTScalar et -> primTypeToCpp et
                    | t -> irTypeToCpp t
                let seedStr = exprToCppCtx ctx seedExpr
                let arrayNamesOf (info: ApplyInfo) =
                    info.Arrays |> List.mapi (fun i arr ->
                        match arr with
                        | IRVar (id, _) -> Map.tryFind id ctx.VarNames |> Option.defaultValue (sprintf "arr%d" i)
                        | IRRange _ -> sprintf "__range%d" i
                        | IRVirtualReverse _ -> sprintf "__rev%d" i
                        | IRBlocked _ -> sprintf "__blk%d" i
                        | _ -> sprintf "arr%d" i)
                let foldCg (info: ApplyInfo) (accName: string) =
                    let cg = buildLoopNestCodeGen info (arrayNamesOf info) accName builder
                    { cg with OutputType = callable.RetType; FoldWrapper = Some wname }
                match infos with
                | [single] ->
                    let cg = foldCg single name
                    let code =
                        wrapperLines
                        @ [sprintf "%s%s %s = %s;" ind elemStr name seedStr]
                        @ genLoopNest cg ctx.VarNames ctx.Indent
                    (code, ctx')
                | _ :: _ ->
                    // Fused tree: ONE merged nest, one scalar accumulator per
                    // leaf. Each leaf accumulates at its OWN depth from its
                    // OWN arrays (genFusedLoopNest staggers mixed-arity
                    // trees), so incompatible loop structures are a loud
                    // diagnostic, never silently-shared loops.
                    let leafNames = infos |> List.mapi (fun i _ -> sprintf "%s_%d" name i)
                    let leafCgs = infos |> List.mapi (fun i info -> foldCg info leafNames.[i])
                    // A fused fold writes shared scalar accumulators, which race
                    // under any parallel/device backend (omp reduction clauses
                    // and device reductions are the future upgrade). Reject a
                    // device leaf loudly; host leaves fold serially.
                    let deviceLeaf = infos |> List.tryPick (fun info ->
                        match classifyLeafBackend info with
                        | BkCuda _ -> Some "cuda" | BkMpi -> Some "mpi" | _ -> None)
                    match checkMergeCompatible leafCgs, deviceLeaf with
                    | _, Some bk ->
                        (codegenError ctx ind (sprintf "reduce over a fused computation with a %s leaf: device/parallel reductions over a fused tree are not supported yet â€” force the leaf with |> compute and reduce the array" bk), ctx')
                    | Error reason, _ ->
                        (codegenError ctx ind (sprintf "reduce over a fused computation: cannot fuse the leaves into one loop nest: %s" reason), ctx')
                    | Ok _, None ->
                        let declCode =
                            leafNames |> List.map (fun ln -> sprintf "%s%s %s = %s;" ind elemStr ln seedStr)
                        let (sm, sp) = streamedNestSetup ctx.StreamedArrays ind leafCgs
                        let loopCode = sp @ genFusedLoopNestStreamed sm leafCgs ctx.VarNames ctx.Indent false None
                        let tupleLine = sprintf "%sauto %s = std::make_tuple(%s);" ind name (leafNames |> String.concat ", ")
                        // Destructure sub-bindings resolve through TupleChildren
                        // straight to the accumulator names (the fusion-tree
                        // convention) â€” never through std::get on the nested type.
                        let ctxOut = { ctx' with TupleChildren = Map.add name leafNames ctx'.TupleChildren }
                        (wrapperLines @ declCode @ loopCode @ [tupleLine], ctxOut)
                | [] ->
                    (codegenError ctx ind "reduce over a deferred computation: no leaves (unreachable)", ctx')
            | _ ->
                (codegenError ctx ind "reduce over a deferred computation: fold kernel must resolve to a binary callable (typechecker or IR bug if not)", ctx')



and genTupleProjBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (parentExpr: IRExpr) (projIdx: int) (isFlat: bool) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Check if parent is a deferred computation tuple â€” if so, project and defer
    let parentDeferred =
        match parentExpr with
        | IRVar (pid, _) -> Map.tryFind pid ctx.DeferredComputations
        | _ -> None
    match parentDeferred with
    | Some (IRTuple elems) when projIdx < elems.Length ->
        // Parent is a deferred tuple â€” project out the element and defer it
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id elems.[projIdx] ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred computation (tuple proj)>" ind name], ctx')
    | Some (IRParallel _ | IRFusion _) ->
        // Parent is a deferred combinator â€” defer the projection too
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred computation (proj of combinator)>" ind name], ctx')
    | _ ->
        // Tuple projection â€” resolve through TupleChildren map
        let parentName =
            match parentExpr with
            | IRVar (pid, _) -> Map.tryFind pid ctx.VarNames |> Option.defaultValue "_"
            | _ -> "_"
        let parentType = inferExprType parentExpr
        let flatChildren =
            match Map.tryFind parentName ctx.TupleChildren with
            | Some children -> children
            | None -> []

        if isFlat then
            // Flat projection: projIdx is a flat leaf index
            if projIdx < flatChildren.Length then
                let sourceName = flatChildren.[projIdx]
                let code = [sprintf "%sauto& %s = %s;" ind name sourceName]
                let extentsAlias =
                    match IR.stripUnits binding.Type with
                    | ArrayElem _ ->
                        [sprintf "%sconst size_t* %s_extents = %s.extents;" ind name sourceName]
                    | _ -> []
                let ctx' = addVarName binding.Id name ctx
                let ctx' =
                    match Map.tryFind sourceName ctx'.TupleChildren with
                    | Some children -> { ctx' with TupleChildren = Map.add name children ctx'.TupleChildren }
                    | None -> ctx'
                (code @ extentsAlias, ctx')
            else
                let code = genScalarBinding ctx name binding.Value binding.Type
                let ctx' = addVarName binding.Id name ctx
                (code, ctx')
        else
            // Structural projection: projIdx is a type-level index
            let ranges = tupleLeafRanges parentType
            let (flatStart, leafCount) =
                if projIdx < ranges.Length then ranges.[projIdx]
                else (projIdx, 1)

            if leafCount > 1 && flatChildren.Length > 0 && flatStart + leafCount <= flatChildren.Length then
                // Sub-tuple: synthesize from flat children range
                let subChildren = flatChildren.[flatStart .. flatStart + leafCount - 1]
                let tupleLine = sprintf "%sauto %s = std::make_tuple(%s);" ind name (subChildren |> String.concat ", ")
                let ctx' = addVarName binding.Id name ctx
                let ctx' = { ctx' with TupleChildren = Map.add name subChildren ctx'.TupleChildren }
                ([tupleLine], ctx')

            elif flatStart < flatChildren.Length then
                // Single leaf at computed position
                let sourceName = flatChildren.[flatStart]
                let code = [sprintf "%sauto& %s = %s;" ind name sourceName]
                let extentsAlias =
                    match IR.stripUnits binding.Type with
                    | ArrayElem _ ->
                        [sprintf "%sconst size_t* %s_extents = %s.extents;" ind name sourceName]
                    | _ -> []
                let ctx' = addVarName binding.Id name ctx
                let ctx' =
                    match Map.tryFind sourceName ctx'.TupleChildren with
                    | Some children -> { ctx' with TupleChildren = Map.add name children ctx'.TupleChildren }
                    | None -> ctx'
                (code @ extentsAlias, ctx')

            else
                // No TupleChildren â€” fall back to std::get
                let code = genScalarBinding ctx name binding.Value binding.Type
                let ctx' = addVarName binding.Id name ctx
                (code, ctx')



and genVarAliasBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (srcId: IRId) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Check if source is deferred â€” propagate deferral
    match Map.tryFind srcId ctx.DeferredComputations with
    | Some deferred ->
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id deferred ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred computation (alias)>" ind name], ctx')
    | None ->
        // Stage 3c.3: let-binding whose value is a reference to a
        // lifted callable. `let f = lambda(...)` lowers to
        // `binding.Value = IRVar(callable.Id, funcType)`, and
        // subsequent uses of `f` reference the binding by its
        // bindingId. Emit a wrapper closure bound to the user's
        // name `f` so direct calls `f(args)` work â€” the wrapper's
        // signature matches the callable's regular params (captures
        // pulled in by reference via `[&]`, hidden from the user's
        // call site). Without this, genScalarBinding's fallback
        // emits `std::function<...> f = __lambda_X;`, which fails
        // to compile because the lifted function has captures as
        // extra positional params and doesn't match the user-facing
        // function type.
        //
        // Closure body: `return __lambda_X(regulars..., captures...);`
        // Comparison/logical ops return Bool by convention; arithmetic
        // returns the callable's declared RetType. The wrapper's
        // declared return type defers to `auto` because the closure
        // body is a trivial forwarding call â€” the compiler infers
        // it precisely from the callable's signature, and the user
        // doesn't see the wrapper's type directly.
        match resolveCallable binding.Value with
        | Some callable ->
            let safeName = sanitizeCppName callable.Name
            let paramSig =
                callable.Params
                |> List.map (fun p ->
                    match p.Type with
                    | ArrayElem arr -> sprintf "%s %s" (cppArrayTypeStr arr) p.Name
                    | _ -> sprintf "%s %s" (irTypeToCpp p.Type) p.Name)
                |> String.concat ", "
            let regularArgs = callable.Params |> List.map (fun p -> p.Name)
            let captureArgs = callable.Captures |> List.map (fun c -> c.Name)
            let allArgs = (regularArgs @ captureArgs) |> String.concat ", "
            // Wrapper type: `std::function<Ret(P1, P2, ...)>`. Explicit
            // type per the codegen convention (auto reserved for thin
            // forwarding wrappers prefixed `__wrap_*`). std::function
            // is required when the wrapper itself flows into another
            // function's capture slot â€” the receiving function takes
            // captures as `std::function<...>&`, which can't bind to
            // an rvalue temporary if we emit raw closures via auto.
            // std::function gives the binding a stable lvalue type
            // that matches the capture-slot signature.
            let paramTypes =
                callable.Params
                |> List.map (fun p ->
                    match p.Type with
                    | ArrayElem arr -> cppArrayTypeStr arr
                    | _ -> irTypeToCpp p.Type)
            let retTypeStr =
                match callable.RetType with
                | ArrayElem arr -> cppArrayTypeStr arr
                | t -> irTypeToCpp t
            let funcTypeStr =
                sprintf "std::function<%s(%s)>" retTypeStr (String.concat ", " paramTypes)
            let code = [sprintf "%s%s %s = [&](%s) { return %s(%s); };" ind funcTypeStr name paramSig safeName allArgs]
            let ctx' = addVarName binding.Id name ctx
            (code, ctx')
        | None ->
            // Plain variable alias â€” may be aliasing a tuple, propagate children
            let srcName = Map.tryFind srcId ctx.VarNames |> Option.defaultValue ""
            let hasTupleChildren = Map.containsKey srcName ctx.TupleChildren
            // An ASSIGNABLE binding of an existing DENSE array (`let a = Z` /
            // `let mut a = Z`; block-level via ctx.MutableArrayLets,
            // top-level via IsMutable — TypeCheck marks every non-static let
            // assignable, so both spellings admit `a(i) = ...`) deep-copies
            // the storage: the wrapper-by-value alias below shares Z's data
            // pointer, so mutations through `a` would silently corrupt `Z`.
            // Compound/ragged/dep-idx initializers keep the historical alias
            // (no dense .extents/pool contract; no assignment path exercises
            // them today).
            let mutArrayCopy =
                if binding.IsMutable || Set.contains binding.Id ctx.MutableArrayLets then
                    match binding.Type with
                    | ArrayElem at when not (isCompoundArrayType at)
                                       && not (isRaggedArrayType at)
                                       && not (isDepIdxArrayType at) ->
                        materializeArrayCopyForm emptySubst ctx.VarNames name (elemTypeToCpp at.ElemType) binding.Value
                    | _ -> None
                else None
            match mutArrayCopy with
            | Some copyStmts ->
                let ctx' = addVarName binding.Id name ctx
                (copyStmts |> List.map (fun s -> ind + s), ctx')
            | None ->
                // Use auto& when source has flat TupleChildren to avoid type mismatch
                let code =
                    if hasTupleChildren then
                        [sprintf "%sauto& %s = %s;" ind name srcName]
                    else
                        genScalarBinding ctx name binding.Value binding.Type
                let ctx' = addVarName binding.Id name ctx
                let ctx' =
                    match Map.tryFind srcName ctx'.TupleChildren with
                    | Some children -> { ctx' with TupleChildren = Map.add name children ctx'.TupleChildren }
                    | None -> ctx'
                (code, ctx')



and genChoiceBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (left: IRExpr) (right: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Only defer when children are computation-level (not scalar)
    let isCompExpr e = match e with IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ | IRFallback _ | IRComposeObj _ | IRComposeMeth _ | IRBind _ | IRGuard _ | IRSequence _ -> true | IRVar _ -> true | _ -> false
    if isCompExpr left || isCompExpr right then
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred choice>" ind name], ctx')
    else
        // Scalar choice: generate directly
        let code = genScalarBinding ctx name binding.Value binding.Type
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')



/// `let C = A <|:> B` binding site. The operands are arrays (typecheck
/// guarantees it — scalars steer to <|>), and the combinator is lazy like the
/// rest of its family: defer, and materialize at |> compute
/// (genFallbackMaterialize via genComputeBinding's IRFallback arm).
and genFallbackBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (left: IRExpr) (right: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    let ctx' = addVarName binding.Id name ctx
    let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
    ([sprintf "%s// %s = <deferred fallback>" ind name], ctx')

/// Materialize `A <|:> B` (allocated-fallback, formalism 2.6): read A where
/// A's STORAGE holds the cell, else B. Two storage regimes, one judgment:
///   * compound-left: the CompoundIdx mask IS the allocation record. Iterate
///     the dense underlying space (B's extents = result extents); present
///     lead-tuples read A's compact buffer (linearize * trailing_stride),
///     absent ones read B. An allocated zero survives — the distinguisher
///     from <|>'s value-keyed zero test.
///   * dense-left: allocation = the nested-pointer chain, checked per curry
///     level by the fallback_copy<> runtime helper (nullptr-robust; compiler-
///     built arrays are fully allocated, partially-allocated ones arrive via
///     the C++-level partial-depth allocation API).
/// The result is always a fully-allocated dense array.
and genFallbackMaterialize (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (left: IRExpr) (right: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Operand prep: named materialized vars pass through; anything else (a
    // deferred-computation var, an inline combinator, or a nested IRFallback
    // from the object_for(<|:>) fold) materializes into a synthetic
    // sub-binding first (the genChoiceBinding pattern).
    let prepOperand (ctxIn: CodeGenContext) (e: IRExpr) (tag: string) : string list * CodeGenContext * string * IRType =
        match e with
        | IRVar (id, ty) when Map.containsKey id ctxIn.VarNames && not (Map.containsKey id ctxIn.DeferredComputations) ->
            ([], ctxIn, Map.find id ctxIn.VarNames, ty)
        | _ ->
            let subTy = match e with IRVar (_, ty) -> ty | _ -> binding.Type
            let subName = sprintf "%s__%s" name tag
            let subBinding = { Id = builder.FreshId(); Name = subName; Type = subTy
                               Value = IRCompute e; IsConst = true; IsMutable = false }
            let (code, ctx') = genBinding ctxIn subBinding builder
            (code, ctx', subName, subTy)
    let (codeL, ctxL, nameL, tyL) = prepOperand ctx left "lhs"
    let (codeR, ctxR, nameR, _tyR) = prepOperand ctxL right "rhs"
    match binding.Type with
    | ArrayElem resArr ->
        let rank = arrayRank resArr
        let elemType = elemTypeToCpp resArr.ElemType
        let leftCompound =
            match tyL with
            | ArrayElem aL when isCompoundArrayType aL -> Some aL
            | _ -> None
        // Result extents come from the operand that spans the dense space:
        // the left array for dense-left (operands are type-unified), the
        // RIGHT array for compound-left (the left is compact storage).
        let extentsSrc = match leftCompound with Some _ -> nameR | None -> nameL
        let extentsAlias = sprintf "%sconst size_t* %s_extents = %s.extents;" ind name extentsSrc
        let allocDecl = sprintf "%sArray<%s, %d> %s = { allocate<typename promote<%s, %d>::type, nullptr>(%s_extents), %s_extents };"
                            ind elemType rank name elemType rank name name
        let indD d = String.replicate d "    "
        let idxVar i = sprintf "__fb%d" i
        let subscript n = [for i in 0 .. n - 1 -> sprintf "[%s]" (idxVar i)] |> String.concat ""
        let bodyLines =
            match leftCompound with
            | None ->
                // Dense-left: one nullptr-robust recursive copy.
                [sprintf "%snested_array_utilities::fallback_copy<%s, %d>(%s.data, %s.data, %s.data, %s_extents);"
                    ind elemType rank name nameL nameR name]
            | Some aL ->
                let leadRank =
                    aL.IndexTypes
                    |> List.tryFind (fun ix -> ix.IxKind = IxKCompound)
                    |> Option.map (fun ix -> ix.Rank)
                    |> Option.defaultValue 1
                let trailingCount = rank - leadRank
                // Runtime shape guard: the mask's underlying extents must
                // agree with the dense right operand (statically only ranks
                // and element types are checkable — the mask is runtime data).
                let guards =
                    [ for d in 0 .. leadRank - 1 ->
                        sprintf "%sif (%s.idx->extents[%d] != %s_extents[%d]) { blade_rt::panic(\"BL8001\", \"<|:>: compound left operand's underlying extents disagree with the dense right operand's shape\", nullptr, 0); }"
                            ind nameL d name d ]
                let mutable lines = guards
                let mutable depth = ctx.Indent
                for i in 0 .. leadRank - 1 do
                    lines <- lines @ [sprintf "%sfor (size_t %s = 0; %s < %s_extents[%d]; %s++) {" (indD depth) (idxVar i) (idxVar i) name i (idxVar i)]
                    depth <- depth + 1
                let leadTuple =
                    [for i in 0 .. leadRank - 1 -> idxVar i] |> String.concat ", "
                lines <- lines @ [sprintf "%sstd::array<size_t, %d> __fb_t{{ %s }};" (indD depth) leadRank leadTuple]
                // Row-major flatten of the trailing coordinate inside a
                // present cell's contiguous block.
                let trailOffsetExpr =
                    if trailingCount = 0 then ""
                    else
                        [leadRank .. rank - 1]
                        |> List.fold (fun acc j ->
                            if acc = "" then idxVar j
                            else sprintf "(%s * %s_extents[%d] + %s)" acc name j (idxVar j)) ""
                let readCompact =
                    if trailingCount = 0 then sprintf "%s.data[%s.idx->linearize(__fb_t)]" nameL nameL
                    else sprintf "%s.data[%s.idx->linearize(__fb_t) * %s.trailing_stride + %s]" nameL nameL nameL trailOffsetExpr
                let emitTrailingAssign (baseDepth: int) (rhs: string) : string list =
                    let mutable ls = []
                    let mutable d = baseDepth
                    for j in leadRank .. rank - 1 do
                        ls <- ls @ [sprintf "%sfor (size_t %s = 0; %s < %s_extents[%d]; %s++) {" (indD d) (idxVar j) (idxVar j) name j (idxVar j)]
                        d <- d + 1
                    ls <- ls @ [sprintf "%s%s%s = %s;" (indD d) name (subscript rank) rhs]
                    for _ in leadRank .. rank - 1 do
                        d <- d - 1
                        ls <- ls @ [sprintf "%s}" (indD d)]
                    ls
                lines <- lines @ [sprintf "%sif (%s.idx->present(__fb_t)) {" (indD depth) nameL]
                lines <- lines @ emitTrailingAssign (depth + 1) readCompact
                lines <- lines @ [sprintf "%s} else {" (indD depth)]
                lines <- lines @ emitTrailingAssign (depth + 1) (sprintf "%s%s" nameR (subscript rank))
                lines <- lines @ [sprintf "%s}" (indD depth)]
                for _ in 0 .. leadRank - 1 do
                    depth <- depth - 1
                    lines <- lines @ [sprintf "%s}" (indD depth)]
                lines
        let ctx' = addVarName binding.Id name ctxR
        (codeL @ codeR @ [""; sprintf "%s// <|:> allocated-fallback: %s where allocated, else %s" ind nameL nameR; extentsAlias; allocDecl] @ bodyLines, ctx')
    | t ->
        let code = codegenError ctx ind (sprintf "<|:>: binding type is not an array (got %A) — likely a typechecker or IR bug" t)
        (codeL @ codeR @ code, addVarName binding.Id name ctxR)

and genGuardBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (body: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Guard wrapping a computation: defer for later materialization via |> compute
    // Recurse through nested guards to check if the leaf body is a computation
    let rec leafIsComputation e =
        match e with
        | IRGuard (_, inner) -> leafIsComputation inner
        | IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ | IRFallback _ | IRComposeObj _ | IRComposeMeth _ | IRBind _ | IRSequence _ -> true
        | IRVar (id, _) -> Map.containsKey id ctx.DeferredComputations
        | _ -> false
    if leafIsComputation body then
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred guard>" ind name], ctx')
    else
        // Scalar guard: generate directly
        let code = genScalarBinding ctx name binding.Value binding.Type
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')



and genSequenceBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (elems: IRExpr list) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Defer: sequence is a flat n-ary parallel, materialized by |> compute
    let isCompExpr e = match e with IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ | IRFallback _ | IRComposeObj _ | IRComposeMeth _ | IRBind _ | IRGuard _ | IRSequence _ -> true | IRVar _ -> true | _ -> false
    if elems |> List.exists isCompExpr then
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred sequence>" ind name], ctx')
    else
        // All scalars: generate as tuple
        let code = genScalarBinding ctx name binding.Value binding.Type
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')



and genForRangeBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (vid: IRId) (lo: IRExpr) (hi: IRExpr) (body: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Imperative for-range loop
    let loStr = exprToCppCtx ctx lo
    let hiStr = exprToCppCtx ctx hi
    let varName = sprintf "__k%d" vid
    let innerCtx = addVarName vid varName ctx
    // Unroll the body IRLet chain into statements
    let (bodyLets, _bodyFinal) = unrollLetChain body
    let (bodyCode, _) =
        bodyLets |> List.fold (fun (accCode, accCtx) (id, value) ->
            let tempName = sprintf "__v%d" id
            let tempBinding = {
                Id = id; Name = tempName; Type = inferExprType value
                Value = value; IsConst = true; IsMutable = false
            }
            let (code, ctx') = genBinding { accCtx with Indent = ctx.Indent + 1 } tempBinding builder
            (accCode @ code, { ctx' with Indent = ctx.Indent })
        ) ([], innerCtx)
    let code =
        [forLoopFrom ind varName loStr hiStr]
        @ bodyCode
        @ [sprintf "%s}" ind]
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genBindChainBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (comp: IRExpr) (cont: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Monadic bind: defer if comp is a deferred computation
    let isCompDeferred =
        match comp with
        | IRVar (id, _) -> Map.containsKey id ctx.DeferredComputations
        | IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRFunctorMap _ -> true
        | _ -> false
    if isCompDeferred then
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred bind>" ind name], ctx')
    else
        // Scalar bind: cont(comp)
        let code = genScalarBinding ctx name binding.Value binding.Type
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')



and genLetChainBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Block expression: unroll the IRLet chain into sequential bindings
    let (lets, finalExpr) = unrollLetChain binding.Value
    let (allCode, foldCtx) =
        lets |> List.fold (fun (accCode, accCtx) (id, value) ->
            let tempName = sprintf "__v%d" id
            let tempBinding = {
                Id = id; Name = tempName; Type = inferExprType value
                Value = value; IsConst = true; IsMutable = false
            }
            let (code, ctx') = genBinding accCtx tempBinding builder
            (accCode @ code, ctx')
        ) ([], ctx)
    // Generate the final named binding
    let finalBinding = {
        Id = binding.Id; Name = name; Type = binding.Type
        Value = finalExpr; IsConst = binding.IsConst; IsMutable = binding.IsMutable
    }
    let (finalCode, finalCtx) = genBinding foldCtx finalBinding builder
    (allCode @ finalCode, finalCtx)


let genFuncBody (ctx: CodeGenContext) (builder: IRBuilder) (names: Map<IRId, string>) (indent: string) (body: IRExpr) : string list =
    // Deep unroll: flatten all nested IRLet chains into a flat list
    let rec deepUnroll (expr: IRExpr) : (IRId * IRExpr) list * IRExpr =
        match expr with
        | IRLet (id, value, body) ->
            // Check if value itself contains nested IRLets
            let (innerLets, innerFinal) = deepUnroll value
            let (restLets, restFinal) = deepUnroll body
            // If value had nested lets, emit those first, then bind the final value
            match innerLets with
            | [] -> ((id, value) :: restLets, restFinal)
            | _ -> (innerLets @ [(id, innerFinal)] @ restLets, restFinal)
        | _ -> ([], expr)
    let (lets, retExpr) = deepUnroll body
    let mutable currentNames = names
    // Indentation for genApplyCombinator emissions: the function body lives one
    // level deeper than the function declaration's ctx.Indent.
    let bodyIndent = ctx.Indent + 1
    let stmts = lets |> List.collect (fun (id, value) ->
        let varName = sprintf "__v%d" id
        match value with
        | IRForRange (vid, lo, hi, forBody) ->
            // Route through genForRangeBinding â€” the recursive binding-level
            // renderer â€” so nested for-in loops (and any statement form its
            // genBinding dispatch supports) work inside FUNCTION bodies
            // exactly as they do at module level. The old inline renderer
            // here was flat: a nested IRForRange fell through exprToCpp and
            // emitted an unsupported-expression marker.
            let bodyCtx = { ctx with VarNames = currentNames; Indent = bodyIndent }
            let tempBinding = {
                Id = id; Name = varName; Type = IRTUnit
                Value = value; IsConst = true; IsMutable = false
            }
            let (code, _) = genForRangeBinding bodyCtx tempBinding builder vid lo hi forBody
            currentNames <- Map.add id varName currentNames
            code
        | IRAssign (target, v) ->
            let targetStr =
                match target with
                | LVVar tid -> Map.tryFind tid currentNames |> Option.defaultValue (sprintf "__v%d" tid)
                | _ -> exprToCpp currentNames target
            currentNames <- Map.add id varName currentNames
            [sprintf "%s%s = %s;" indent targetStr (exprToCpp currentNames v)]
        | IRConstraintCheck (cond, message, span) ->
            currentNames <- Map.add id varName currentNames
            [ sprintf "%sif (!(%s)) {" indent (exprToCpp currentNames cond)
              sprintf "%s    blade_rt::panic(\"BL8001\", \"%s\", %s);" indent message (panicSpanArgs span)
              sprintf "%s}" indent ]
        | IRLit IRLitUnit ->
            // Skip unit literals (side effects already emitted)
            currentNames <- Map.add id varName currentNames
            []
        | IRMethodFor _ | IRObjectFor _ ->
            // Loop objects are compile-time only â€” they're resolved when <@> is processed
            currentNames <- Map.add id varName currentNames
            []
        | IRApplyCombinator _ | IRComposeApply _ ->
            // Unevaluated computations â€” deferred until |> compute forces them
            currentNames <- Map.add id varName currentNames
            []
        | IRCompute (IRApplyCombinator info) ->
            // Function-body let-binding of `method_for(...) <@> kernel |> compute`.
            // Use the statement-form genApplyCombinator, which emits the full
            // sequence (extents declaration, allocation, loop nest) with
            // `varName_extents` etc. as proper C++ identifiers â€” so any
            // downstream operation that uses the companion-array convention
            // (reduce's inline IIFE, extents() runtime read, etc.) finds them.
            //
            // The inline genApplyCombinatorExpr can't preserve that convention
            // through an IIFE boundary (companion arrays would be lambda-local,
            // not visible in the enclosing scope) and only handles the 2-array
            // accumulation form anyway. Routing through genApplyCombinator here
            // mirrors what genBinding does at the module level.
            let bodyCtx = { ctx with VarNames = currentNames; Indent = bodyIndent }
            let code = genApplyCombinator bodyCtx varName info builder
            currentNames <- Map.add id varName currentNames
            code
        | IRArrayLit _ ->
            // Array literal as a function-body let (e.g. a locally built
            // buffer that loops then fill): route through genBinding, whose
            // IRArrayLit arm emits the statement form (extents + allocate +
            // per-element init). The default arm's exprToCpp has no inline
            // rendering for array literals.
            let bodyCtx = { ctx with VarNames = currentNames; Indent = bodyIndent }
            let tempBinding = {
                Id = id; Name = varName; Type = inferExprType value
                Value = value; IsConst = false; IsMutable = true
            }
            let (code, _) = genBinding bodyCtx tempBinding builder
            currentNames <- Map.add id varName currentNames
            code
        | IRVar _ when Set.contains id ctx.MutableArrayLets ->
            // Function-body `let mut a = Z` over an array: route through
            // genBinding so genVarAliasBinding's mut-copy path runs (fresh
            // alloc + pool copy). The default arm's `auto` alias would share
            // Z's storage and let mutations through `a` corrupt it.
            let bodyCtx = { ctx with VarNames = currentNames; Indent = bodyIndent }
            let tempBinding = {
                Id = id; Name = varName; Type = inferExprType value
                Value = value; IsConst = false; IsMutable = true
            }
            let (code, _) = genBinding bodyCtx tempBinding builder
            currentNames <- Map.add id varName currentNames
            code
        | IRMask _ | IRIntersect _ | IRUnion _ | IRSort _ | IRUnique _ | IRTranspose _ | IRDecompact _ | IRArrayNegate _ | IRArrayConjugate _ | IRGram _ ->
            // Phase C lift pass can place an inline form as a let value at
            // function-body level. The same materialization helper used by
            // exprToCpp's IRLet (for kernel-body IIFEs) produces format-
            // neutral statement lines; here we emit them with the function
            // body's indent rather than space-joined inline.
            let elemStr = inferInlineElemTypeStr "lambda-body inline form" value
            match materializeInlineForm emptySubst currentNames varName elemStr value with
            | Some matStmts ->
                currentNames <- Map.add id varName currentNames
                matStmts |> List.map (fun s -> indent + s)
            | None ->
                // Defensive: shouldn't fire for the patterns we matched.
                let valStr = exprToCpp currentNames value
                currentNames <- Map.add id varName currentNames
                [sprintf "%sauto %s = %s;" indent varName valStr]
        | _ ->
            let valStr = exprToCpp currentNames value
            currentNames <- Map.add id varName currentNames
            [sprintf "%sauto %s = %s;" indent varName valStr])
    if isUnitExpr retExpr then
        stmts  // Void function: no return statement needed
    else
        // If the return expression is `compute(applyCombinator)`, synthesize
        // an internal let binding so the statement-form genApplyCombinator
        // emits the full sequence (extents/alloc + loop nest for array
        // output, scalar accumulator + loop nest for scalar output) and we
        // return the bound name. This unifies both shapes through the same
        // LoopNestCodeGen machinery; without it, exprToCpp would route
        // through the inline expression-form genApplyCombinatorExpr â€” which
        // is still a hardcoded 2-array IIFE special case kept for inline
        // expression contexts that lack a surrounding statement scope (a
        // separate cleanup will fold that into a wrapper around this path).
        match retExpr with
        | IRCompute (IRApplyCombinator info) ->
            let retVarName = sprintf "__ret%d" (builder.FreshId())
            let bodyCtx = { ctx with VarNames = currentNames; Indent = ctx.Indent + 1 }
            let combCode = genApplyCombinator bodyCtx retVarName info builder
            stmts @ combCode @ [sprintf "%sreturn %s;" indent retVarName]
        | IRArrayLit (elements, arrType) ->
            // Array literal as return value: lift to a local binding, then
            // return. `genArrayLiteral` is the statement-form generator for
            // IRArrayLit (extents-table + allocate-call + per-element init);
            // exprToCpp has no inline form for it, so without this lift the
            // return falls through to the unsupported-IR-node sentinel.
            //
            // The lifted binding gets a synthetic __retN name so it can't
            // collide with user names. Return-by-value of the Array<T,N>
            // wrapper copies the pointer; the underlying buffer (allocated
            // via allocate<>) lives on the heap so the caller receives a
            // valid array.
            let retVarName = sprintf "__ret%d" (builder.FreshId())
            let bodyCtx = { ctx with VarNames = currentNames; Indent = ctx.Indent + 1 }
            let arrayCode = genArrayLiteral bodyCtx retVarName elements arrType
            stmts @ arrayCode @ [sprintf "%sreturn %s;" indent retVarName]
        | _ ->
            let retStr = exprToCpp currentNames retExpr
            stmts @ [sprintf "%sreturn %s;" indent retStr]

let genFuncDef (ctx: CodeGenContext) (builder: IRBuilder) (funcDef: IRFuncDef) : string list * CodeGenContext =
    let ind = indentStr ctx
    let bodyInd = ind + "    "

    // Array parameters are wrappers. Body reads shape via
    // `<name>.extents[d]`, `<name>.lens[d]`, `<name>.offsets[d]`. No need
    // for separate body-level aliases â€” the wrapper IS the binding.
    let paramStr (name: string) (ty: IRType) : string =
        match ty with
        | ArrayElem arr -> sprintf "%s %s" (cppArrayTypeStr arr) name
        | _ -> sprintf "%s %s" (irTypeToCpp ty) name
    let captureParamStr (cap: CaptureInfo) : string =
        // Captures are appended after the regular params. Pass-by-reference
        // so mutation propagates and the captures' lifetimes are tied to
        // the wrapper's `[&]` capture at the use site (Stage 3c.1).
        // `T&` for plain types, `Array<T, N>&` for arrays â€” the wrapper
        // declaration treats them symmetrically; the `&` after the type
        // string is appended without parsing.
        //
        // Stage 3c.3 wrinkle: function-typed captures use `const
        // std::function<...>&` rather than non-const reference. Top-level
        // function declarations in Blade (`function name(args) = body`)
        // emit as ordinary C++ functions; their names denote function
        // references, not std::function values. A non-const reference
        // parameter can't bind to such a function-reference rvalue (C++
        // would have to materialize a temporary std::function, which
        // can't bind to non-const). const references CAN bind to rvalue
        // temporaries, so the implicit conversion at the call site
        // works. The trade-off is loss of mutation-through-capture for
        // function values â€” fine because function values are immutable
        // bindings in Blade.
        match cap.Type with
        | ArrayElem arr -> sprintf "%s& %s" (cppArrayTypeStr arr) cap.Name
        | FuncElem _ -> sprintf "const %s& %s" (irTypeToCpp cap.Type) cap.Name
        | _ -> sprintf "%s& %s" (irTypeToCpp cap.Type) cap.Name
    let regularParams = funcDef.Params |> List.map (fun p -> paramStr p.Name p.Type)
    let captureParams = funcDef.Captures |> List.map captureParamStr
    let paramList = (regularParams @ captureParams) |> String.concat ", "

    // Use declared return type, or infer from body as fallback
    let retType = 
        match funcDef.RetType with
        | IRTInfer _ -> irTypeToCpp (inferExprType funcDef.Body)  // Should not happen with typed IR
        | ArrayElem arr -> cppArrayTypeStr arr
        | t -> irTypeToCpp t

    // Build name map: regular params + captures both contribute, so the
    // body's IRVar references to captured variables resolve to the same
    // C++ name the signature declared.
    let bodyNames =
        funcDef.Params
        |> List.fold (fun m p -> Map.add p.VarId p.Name m) ctx.VarNames
    let bodyNames =
        funcDef.Captures
        |> List.fold (fun m c -> Map.add c.Id c.Name m) bodyNames

    // Generate proper C++ function
    let safeName = sanitizeCppName funcDef.Name
    let bodyStmts = genFuncBody ctx builder bodyNames bodyInd funcDef.Body
    // Shadow-stack frame (Stage 6): named as the Blade function so a runtime
    // panic prints a Blade call stack. file/line are nullptr/0 because
    // IRCallable carries no span (threading one would touch TypeCheck.fs's
    // IRCallable constructions, owned by a concurrent agent) — the function
    // name is the main win. Host-only via the BLADE_FRAME macro's CUDA guard.
    let frame = [sprintf "%sBLADE_FRAME(\"%s\", nullptr, 0);" bodyInd (cppStrEscape funcDef.Name)]
    let code =
        [sprintf "%s%s %s(%s) {" ind retType safeName paramList]
        @ frame
        @ bodyStmts
        @ [sprintf "%s}" ind]

    let ctx' = addVarName funcDef.Id funcDef.Name ctx
    (code, ctx')

/// Generate a function as a C++ lambda (for functions that capture module-level bindings)
let genFuncDefAsLambda (ctx: CodeGenContext) (builder: IRBuilder) (funcDef: IRFuncDef) : string list * CodeGenContext =
    let ind = indentStr ctx

    // Array params are wrappers; same approach as genFuncDef.
    let paramList =
        funcDef.Params
        |> List.map (fun p ->
            match p.Type with
            | ArrayElem arr -> sprintf "%s %s" (cppArrayTypeStr arr) p.Name
            | _ -> sprintf "%s %s" (irTypeToCpp p.Type) p.Name)
        |> String.concat ", "

    let retType =
        match funcDef.RetType with
        | IRTInfer _ -> irTypeToCpp (inferExprType funcDef.Body)
        | ArrayElem arr -> cppArrayTypeStr arr
        | t -> irTypeToCpp t

    let bodyNames = funcDef.Params |> List.fold (fun m p -> Map.add p.VarId p.Name m) ctx.VarNames
    let safeName = sanitizeCppName funcDef.Name
    // std::function type with one param type per Blade param (no companion args).
    let paramTypeList =
        funcDef.Params
        |> List.map (fun p ->
            match p.Type with
            | ArrayElem arr -> cppArrayTypeStr arr
            | _ -> irTypeToCpp p.Type)
        |> String.concat ", "
    let funcType = sprintf "std::function<%s(%s)>" retType paramTypeList
    // Statement-form body via genFuncBody â€” the same renderer proper C++
    // functions use â€” so for-in loops, local array literals, and element
    // assignment work in captured functions too. (The old inline
    // `return <exprToCpp body>` form silently DROPPED loop and assignment
    // statements: a captured function containing a for-in compiled to just
    // its final expression.)
    let bodyInd = ind + "    "
    let bodyCtx = { ctx with VarNames = bodyNames; Indent = ctx.Indent + 1 }
    let bodyStmts = genFuncBody bodyCtx builder bodyNames bodyInd funcDef.Body
    // Shadow-stack frame (Stage 6); see genFuncDef. Name-only (nullptr/0).
    let frame = [sprintf "%sBLADE_FRAME(\"%s\", nullptr, 0);" bodyInd (cppStrEscape funcDef.Name)]
    let code =
        [sprintf "%s%s %s = [&](%s) -> %s {" ind funcType safeName paramList retType]
        @ frame
        @ bodyStmts
        @ [sprintf "%s};" ind]
    let ctx' = addVarName funcDef.Id funcDef.Name ctx
    (code, ctx')

/// Generate C++ code for an entire IR module.
/// Returns (functionDefs, bindingCode) â€” functions go outside main(), bindings inside.
/// Forward declarations for file-scope functions. Factored out so genModule
/// and genModuleSplit share one source of truth. Signature uses Array<T,N> /
/// Ragged<T> wrappers; captures appear after regular params as additional
/// reference-typed args, matching genFuncDef's emission.
let private genForwardDecls (fileScopeFuncs: IRFuncDef list) : string list =
    let decls =
        fileScopeFuncs |> List.map (fun funcDef ->
            let paramList =
                funcDef.Params
                |> List.map (fun p ->
                    match p.Type with
                    | ArrayElem arr -> sprintf "%s %s" (cppArrayTypeStr arr) p.Name
                    | _ -> sprintf "%s %s" (irTypeToCpp p.Type) p.Name)
            let captureList =
                funcDef.Captures
                |> List.map (fun cap ->
                    match cap.Type with
                    | ArrayElem arr -> sprintf "%s& %s" (cppArrayTypeStr arr) cap.Name
                    | FuncElem _ -> sprintf "const %s& %s" (irTypeToCpp cap.Type) cap.Name
                    | _ -> sprintf "%s& %s" (irTypeToCpp cap.Type) cap.Name)
            let allParams = (paramList @ captureList) |> String.concat ", "
            let retType =
                match funcDef.RetType with
                | IRTInfer _ -> irTypeToCpp (inferExprType funcDef.Body)
                | ArrayElem arr -> cppArrayTypeStr arr
                | t -> irTypeToCpp t
            let safeName = sanitizeCppName funcDef.Name
            sprintf "%s %s(%s);" retType safeName allParams)
    if decls.IsEmpty then [] else decls @ [""]

/// Classify a binding as a "computation" (forced combinator / compute) vs
/// "data setup" (array literals, scalar lets, plain values). Used only by the
/// split-timing path to decide which timing phase a binding's emitted code
/// belongs to. Walks past IRLet/IRCompute wrappers to the operative form.
let rec private isComputeBindingExpr (e: IRExpr) : bool =
    match e with
    | IRCompute _ -> true
    | IRApplyCombinator _ | IRComposeApply _ | IRReynolds _
    | IRMethodFor _ | IRObjectFor _ | IRBind _ | IRParallel _
    | IRFusion _ | IRChoice _ | IRFallback _ | IRArrayProduct _ | IRComposeObj _
    | IRComposeMeth _ | IRCompose _ | IRFunctorMap _ | IRPure _
    | IRReplicate _ | IRSequence _ -> true
    | IRLet (_, _, body) -> isComputeBindingExpr body
    | _ -> false

let private isComputeBinding (b: IRBinding) : bool =
    isComputeBindingExpr b.Value

/// Compute the set of functions that must be emitted INSIDE main() as
/// std::function lambda bindings (genFuncDefAsLambda) rather than as free
/// C++ functions (genFuncDef). A function is main-local if its body has a
/// free variable naming a module-level binding (that binding only exists as
/// a local inside main), or — transitively — if it references another
/// main-local function: a free C++ function calling a main()-local
/// std::function fails compilation with "'<name>' was not declared in this
/// scope". References a function already receives as explicit capture
/// parameters (lifted lambdas with function-typed captures) do NOT
/// propagate — the call-site wrapper closes over those inside main, so the
/// callee's main-locality never leaks into the lifted function's body.
let private computeMainLocalFuncIds (modul: IRModule) (ctx0: CodeGenContext) : Set<IRId> =
    let funcIds = modul.Functions |> List.map (fun f -> f.Id) |> Set.ofList
    let capturesModuleBinding (funcDef: IRFuncDef) =
        let paramIds = funcDef.Params |> List.map (fun p -> p.VarId) |> Set.ofList
        let captureIds = funcDef.Captures |> List.map (fun cap -> cap.Id) |> Set.ofList
        let bound = Set.unionMany [paramIds; captureIds; funcIds]
        let freeVars = Set.difference (collectVarRefsIR funcDef.Body) bound
        freeVars |> Set.exists (fun id -> Map.containsKey id ctx0.VarNames)
    let uncapturedFuncRefs =
        modul.Functions
        |> List.map (fun f ->
            let captureIds = f.Captures |> List.map (fun cap -> cap.Id) |> Set.ofList
            (f.Id, Set.difference (Set.intersect (collectVarRefsIR f.Body) funcIds) captureIds))
        |> Map.ofList
    let direct =
        modul.Functions
        |> List.filter capturesModuleBinding
        |> List.map (fun f -> f.Id)
        |> Set.ofList
    let rec close (acc: Set<IRId>) =
        let acc' =
            modul.Functions
            |> List.fold (fun s f ->
                if Set.contains f.Id s then s
                elif not (Set.isEmpty (Set.intersect uncapturedFuncRefs.[f.Id] s)) then Set.add f.Id s
                else s) acc
        if acc' = acc then acc else close acc'
    close direct

let genModule (modul: IRModule) (builder: IRBuilder) : string list * string list =
    // Phase D / companion-array gap: populate the codegen-side struct fields
    // cache so inferExprType can resolve IRFieldAccess result types.
    setCodegenStructFieldsCache modul.Types

    // Install the callables table so exprAttrs walks (e.g., from the
    // mask renderer) can do cross-procedural probe analysis. Lambdas
    // and named functions are now handled uniformly: the IR-tree walk
    // descends into lambda bodies directly, and the CallablesTable
    // resolves named function IDs to their (params, body) so the IRApp
    // arm can walk them with parameter substitution.
    let callables = IR.buildCallablesTableForModule modul
    IR.setCallablesContext callables |> ignore

    let ctx0 = emptyContext ()
    let ctx0 = { ctx0 with ProviderReads = modul.ProviderReads; ProviderWrites = modul.ProviderWrites; RandomInits = modul.RandomInits; CompoundInits = modul.CompoundInits; MutableArrayLets = modul.MutableArrayLets }

    // First pass: register ALL names (both bindings and functions) in context
    let ctx0 =
        modul.Bindings |> List.fold (fun c b -> addVarName b.Id b.Name c) ctx0
    let ctx0 =
        modul.Functions |> List.fold (fun c f -> addVarName f.Id f.Name c) ctx0

    // Build a combined list of items with their "order" based on ID.
    // Lower IDs were created earlier in the source. Lifted lambdas
    // live alongside source-level functions in module.Functions and
    // emit as ordinary top-level C++ functions: genFuncDef appends
    // captures as reference parameters, and lambda bodies whose top
    // form is IRApplyCombinator are wrapped in IRCompute at lift
    // time so genFuncBody's return-position handler renders them
    // correctly. Use sites reference the lifted callable through
    // IRVar(callable.Id) and call it through a thin wrapper closure
    // (genCallableWrapper) that hides the capture parameters from
    // consumers expecting the callable's surface arity.
    let bindingItems = modul.Bindings |> List.map (fun b -> (b.Id, Choice1Of2 b))
    let funcItems = modul.Functions |> List.map (fun f -> (f.Id, Choice2Of2 f))
    let allItems = bindingItems @ funcItems |> List.sortBy fst

    // Generate in ID order (approximates source order).
    // First, collect file-scope functions to generate forward declarations.
    //
    // Explicit-pass capture: lifted callables receive their captures
    // as additional reference-typed parameters appended after the
    // regular params (see genFuncDef). For the
    // file-scope eligibility check, capture VarIds therefore count as
    // "param-like" â€” they're in the function's actual C++ signature,
    // not its enclosing scope. Without including them in `paramIds`
    // here, `collectVarRefsIR funcDef.Body` reports them as free vars
    // and the function gets excluded from forward declarations. After
    // Stage 3c.3 that's a problem: `let f = lambda(...)` emits a
    // wrapper closure `auto f = [&](...) { return __lambda_X(..., captures); };`
    // at the binding's emission site, which may precede the lifted
    // function's definition in the file. Without a forward decl,
    // `__lambda_X` is unknown at the wrapper's site and the C++
    // compile fails with "not declared in this scope".
    //
    // Main-locality is TRANSITIVE (computeMainLocalFuncIds): a function
    // whose body references a main-local function is itself main-local,
    // since its free-function form couldn't name the main()-scoped
    // std::function it calls.
    let mainLocalFuncIds = computeMainLocalFuncIds modul ctx0

    let fileScopeFuncs =
        allItems |> List.choose (fun (_, item) ->
            match item with
            | Choice2Of2 funcDef when not (Set.contains funcDef.Id mainLocalFuncIds) -> Some funcDef
            | _ -> None)

    // Generate forward declarations for all file-scope functions (shared
    // helper, also used by genModuleSplit).
    let forwardDecls = genForwardDecls fileScopeFuncs

    let (funcCode, bindCode, finalCtx) =
        allItems |> List.fold (fun (fc, bc, c) (_, item) ->
            match item with
            | Choice1Of2 binding ->
                let (code, c') = genBinding c binding builder
                (fc, bc @ code @ [""], c')
            | Choice2Of2 funcDef ->
                if Set.contains funcDef.Id mainLocalFuncIds then
                    let (code, c') = genFuncDefAsLambda c builder funcDef
                    (fc, bc @ code @ [""], c')
                else
                    let (code, c') = genFuncDef c builder funcDef
                    (fc @ code @ [""], bc, c')
        ) (forwardDecls, [], ctx0)
    
    // Merge context warnings into module-level collector
    let cell = exprWarningsCell ()
    cell.Value <- cell.Value @ finalCtx.Warnings.Value

    (funcCode, bindCode)

/// Split-timing variant of genModule: identical context-threaded emission, but
/// binding output is routed into TWO buckets â€” `setupCode` (data-setup
/// bindings) and `computeCode` (forced computations) â€” so the caller can place
/// a timing checkpoint between them. Functions stay in `funcCode`. Context is
/// still threaded through ALL items in ID order (later bindings reference
/// earlier ones), so only the OUTPUT is partitioned, never the evaluation
/// order. Returns (funcCode, setupCode, computeCode).
let genModuleSplit (modul: IRModule) (builder: IRBuilder) : string list * string list * string list =
    setCodegenStructFieldsCache modul.Types
    let callables = IR.buildCallablesTableForModule modul
    IR.setCallablesContext callables |> ignore
    let ctx0 = emptyContext ()
    let ctx0 = { ctx0 with ProviderReads = modul.ProviderReads; ProviderWrites = modul.ProviderWrites; RandomInits = modul.RandomInits; CompoundInits = modul.CompoundInits; MutableArrayLets = modul.MutableArrayLets }
    let ctx0 =
        modul.Bindings |> List.fold (fun c b -> addVarName b.Id b.Name c) ctx0
    let ctx0 =
        modul.Functions |> List.fold (fun c f -> addVarName f.Id f.Name c) ctx0
    let bindingItems = modul.Bindings |> List.map (fun b -> (b.Id, Choice1Of2 b))
    let funcItems = modul.Functions |> List.map (fun f -> (f.Id, Choice2Of2 f))
    let allItems = bindingItems @ funcItems |> List.sortBy fst
    // Transitive main-locality — same rule as genModule; see
    // computeMainLocalFuncIds.
    let mainLocalFuncIds = computeMainLocalFuncIds modul ctx0
    let fileScopeFuncs =
        allItems |> List.choose (fun (_, item) ->
            match item with
            | Choice2Of2 funcDef when not (Set.contains funcDef.Id mainLocalFuncIds) -> Some funcDef
            | _ -> None)
    let forwardDecls = genForwardDecls fileScopeFuncs
    // Single split point: emit in strict ID order (NO reordering), and once
    // the first compute binding is seen, every subsequent item stays in the
    // compute phase. This preserves all cross-binding dependencies â€” a consumer
    // of a compute result (e.g. `decompact(sym,0)` reading `sym`) is emitted
    // AFTER its producer because it comes later in ID order and the phase flag
    // is already in compute. The setup phase is exactly the leading run of
    // data-declaration bindings before the first computation, matching the
    // archaic prototype's "input allocation" vs "calculation" split.
    //
    // (Per-binding classification was wrong: it floated a non-compute consumer
    // like decompact UP into setup, above the compute binding it depends on,
    // producing an out-of-order "'sym' was not declared" C++ error.)
    let (funcCode, setupCode, computeCode, _seenCompute, finalCtx) =
        let onlyBinding = splitTimingOnlyBinding ()
        allItems |> List.fold (fun (fc, sc, cc, seen, c) (_, item) ->
            match item with
            | Choice1Of2 binding ->
                // When a specific binding name is designated as the timed
                // kernel, the clock starts exactly at that binding (everything
                // prior â€” producers, decompact chains â€” is setup). Otherwise
                // fall back to "first compute binding starts the clock".
                let nowCompute =
                    match onlyBinding with
                    | Some target -> seen || binding.Name = target
                    | None -> seen || isComputeBinding binding
                let (code, c') = genBinding c binding builder
                if nowCompute then
                    (fc, sc, cc @ code @ [""], true, c')
                else
                    (fc, sc @ code @ [""], cc, false, c')
            | Choice2Of2 funcDef ->
                if Set.contains funcDef.Id mainLocalFuncIds then
                    // Lambda-as-binding (closure definition): follows the
                    // current phase â€” setup if before the first compute, else
                    // compute â€” so it never floats across a dependency.
                    let (code, c') = genFuncDefAsLambda c builder funcDef
                    if seen then (fc, sc, cc @ code @ [""], true, c')
                    else (fc, sc @ code @ [""], cc, false, c')
                else
                    // True top-level function: always the function-def bucket,
                    // emitted in the preamble (no effect on the phase flag).
                    let (code, c') = genFuncDef c builder funcDef
                    (fc @ code @ [""], sc, cc, seen, c')
        ) (forwardDecls, [], [], false, ctx0)
    let cell = exprWarningsCell ()
    cell.Value <- cell.Value @ finalCtx.Warnings.Value
    (funcCode, setupCode, computeCode)

/// Generate a complete C++ program with main() from an IR module
let genStructDef (name: string) (fields: (string * IRType) list) : string list =
    let fieldLines = fields |> List.map (fun (fname, fty) ->
        // Array-typed fields render as Array<T,N> / Ragged<T> wrappers so
        // the field carries its shape with it. Other types use the
        // standard irTypeToCpp rendering.
        let cppTy =
            match fty with
            | ArrayElem arr -> cppArrayTypeStr arr
            | _ -> irTypeToCpp fty
        sprintf "    %s %s;" cppTy fname)
    [sprintf "struct %s {" name]
    @ fieldLines
    @ ["};"
       ""]

/// Generate type definitions for a module
let genTypeDefs (modul: IRModule) : string list =
    modul.Types |> List.collect (function
        | IRTDStruct (name, fields) -> genStructDef name fields
        | IRTDVariant (name, variants) ->
            // Check if any variant has data
            let hasData = variants |> List.exists (fun (_, d) -> d.IsSome)
            if hasData then
                // Tagged union using std::variant
                // Generate wrapper structs for each variant (with _T suffix to avoid name clash)
                let variantStructs = variants |> List.collect (fun (vname, data) ->
                    match data with
                    | Some ty -> 
                        [sprintf "struct %s_T { %s value; };" vname (irTypeToCpp ty)]
                    | None -> 
                        [sprintf "struct %s_T {};" vname]
                )
                // Generate the variant type alias
                let variantTypes = variants |> List.map (fun (v, _) -> v + "_T") |> String.concat ", "
                let variantAlias = sprintf "using %s = std::variant<%s>;" name variantTypes
                // Generate constructor functions for variants with data
                let ctorFuncs = variants |> List.collect (fun (vname, data) ->
                    match data with
                    | Some ty ->
                        [sprintf "inline %s %s(%s v) { return %s_T{v}; }" name vname (irTypeToCpp ty) vname]
                    | None ->
                        [sprintf "const %s %s = %s_T{};" name vname vname]
                )
                variantStructs @ [variantAlias] @ ctorFuncs @ [""]
            else
                // Simple enum - use plain enum for unscoped names
                [sprintf "enum %s { %s };" name 
                    (variants |> List.map fst |> String.concat ", ")
                 ""]
        | IRTDAlias (name, ty) ->
            [sprintf "using %s = %s;" name (irTypeToCpp ty); ""]
        | IRTDIndexType (name, _) ->
            // Emit a typedef for the index type so foreign-key element types
            // (IRTIdxTagged (_, IRefNamed name)) can render as the alias
            // rather than bare int64_t. The alias is transparent â€”
            // int64_t-compatible â€” but makes generated C++ self-documenting
            // and leaves a hook for future strong typing.
            [sprintf "using %s = int64_t;" name; ""]
        | IRTDEnumIdx (name, _, values) ->
            // EnumIdx alias: render as the underlying runtime type. All-int
            // values â†’ int64_t; all-string values â†’ std::string. The chosen
            // C++ type must match what the Case 2 reverse-lookup dispatch
            // and any keys array stored under this type expect.
            let underlying = EnumValue.underlyingElemType values
            [sprintf "using %s = %s;" name (primTypeToCpp underlying); ""]
    )

/// Generate code to print a scalar value
let genPrintScalar (name: string) : string list =
    [sprintf "    cout << \"%s = \" << %s << endl;" name name]

/// Generate code to print an array value (flattened for easy parsing)
let genPrintArrayFlat (name: string) (rank: int) : string list =
    let firstVar = sprintf "%s__first" name
    if rank < 1 then
        [sprintf "    cout << \"%s = <rank-0>\" << endl;" name]
    elif rank <= 3 then
        // Ranks 1-3: flat comma-separated output
        let loopVars = [| "i"; "j"; "k" |]
        let opens = [
            sprintf "    cout << \"%s = [\";" name
            sprintf "    bool %s = true;" firstVar ]
        let loops =
            [ for d in 0 .. rank - 1 ->
                sprintf "    %sfor (size_t %s = 0; %s < %s.extents[%d]; %s++) {"
                    (String.replicate d "    ") loopVars.[d] loopVars.[d] name d loopVars.[d] ]
        let inner =
            let ind = "    " + String.replicate rank "    "
            let idx = loopVars.[0..rank-1] |> Array.map (sprintf "[%s]") |> String.concat ""
            [ sprintf "%sif (!%s) cout << \", \";" ind firstVar
              sprintf "%s%s = false;" ind firstVar
              sprintf "%scout << %s%s;" ind name idx ]
        let closes =
            [ for d in rank - 1 .. -1 .. 0 ->
                sprintf "    %s}" (String.replicate d "    ") ]
        let finish = [ sprintf "    cout << \"]\" << endl;" ]
        opens @ loops @ inner @ closes @ finish
    elif rank = 4 then
        // Rank 4: 2D grid of 2D blocks
        // Print as: name[i][j] = [ row0; row1; ... ] for each (i,j)
        [
            sprintf "    cout << \"%s (\" << %s.extents[0] << \"x\" << %s.extents[1] << \"x\" << %s.extents[2] << \"x\" << %s.extents[3] << \"):\" << endl;"
                name name name name name
            sprintf "    for (size_t i = 0; i < %s.extents[0]; i++) {" name
            sprintf "        for (size_t j = 0; j < %s.extents[1]; j++) {" name
            sprintf "            cout << \"  %s[\" << i << \"][\" << j << \"] = [\";" name
            sprintf "            bool %s = true;" firstVar
            sprintf "            for (size_t k = 0; k < %s.extents[2]; k++) {" name
            sprintf "                for (size_t l = 0; l < %s.extents[3]; l++) {" name
            sprintf "                    if (!%s) cout << \", \";" firstVar
            sprintf "                    %s = false;" firstVar
            sprintf "                    cout << %s[i][j][k][l];" name
            "                }"
            "            }"
            sprintf "            cout << \"]\" << endl;"
            "        }"
            "    }"
        ]
    else
        // Rank 5+: just print total size and first few elements
        [
            sprintf "    cout << \"%s = <rank-%d array>\" << endl;" name rank
        ]

/// Generate print loop for arrays with per-dimension symmetry awareness.
/// Expands IRIndexType list into per-dimension loop structure:
///   - SymIdx<k,n>: k dims, first is free range, rest subtract prior vars in group
///   - Idx<n>: 1 dim, free range
/// This correctly handles mixed symmetry (e.g. SymIdx<2> + Idx).
let genPrintArraySymAware (name: string) (indexTypes: IRIndexType list) : string list =
    // Expand index types into per-dimension info: (loopVar, dimIdx, offsetVars)
    // offsetVars = list of loop vars to subtract from extent (empty for free dims)
    let loopVarNames = [| "i"; "j"; "k"; "l"; "m"; "n_"; "p"; "q" |]
    let dims =
        indexTypes |> List.fold (fun (acc, dimIdx) idx ->
            let idxRank = max 1 idx.Rank
            let isSym = idx.Symmetry = SymSymmetric || idx.Symmetry = SymAntisymmetric || idx.Symmetry = SymHermitian
            // Antisymmetric storage is STRICT (i < j < ...): each successive
            // level in the group loses one more slot than the symmetric
            // (i <= j) case. The writer applies this as StrictOffset=1 per
            // triangular antisym level (genLoopHeader / IR strictOffset). The
            // reader must mirror it exactly, or it walks one element past the
            // end of each strict-packed row into adjacent/garbage memory â€”
            // precisely the antisym-Reynolds value mismatch. Symmetric stays
            // strictConst = 0 (left-justified bound n - i).
            let strictConst = if idx.Symmetry = SymAntisymmetric then 1 else 0
            let groupDims =
                [0 .. idxRank - 1] |> List.map (fun a ->
                    let loopVar = if dimIdx + a < loopVarNames.Length then loopVarNames.[dimIdx + a] else sprintf "d%d" (dimIdx + a)
                    let offsets =
                        if isSym && a > 0 then
                            [0 .. a - 1] |> List.map (fun prev -> loopVarNames.[dimIdx + prev])
                        else []
                    // Strict offset applies on every group level beyond the
                    // first (a > 0): level a subtracts a * strictConst.
                    let strict = if a > 0 then a * strictConst else 0
                    (loopVar, dimIdx + a, offsets, strict))
            (acc @ groupDims, dimIdx + idxRank)
        ) ([], 0) |> fst
    let rank = dims.Length
    if rank < 1 || rank > 8 then
        [sprintf "    cout << \"%s = <rank-%d array>\" << endl;" name rank]
    else
        let firstVar = sprintf "%s__first" name
        let opens = [
            sprintf "    cout << \"%s = [\";" name
            sprintf "    bool %s = true;" firstVar ]
        let loops =
            dims |> List.map (fun (loopVar, dimIdx, offsets, strict) ->
                let indent = "    " + String.replicate (dims |> List.findIndex (fun (v,_,_,_) -> v = loopVar)) "    "
                // Bound = extents[d] - (prior loop vars) - (strict constant).
                // For a free dim both are empty/zero -> bare extent. For a
                // symmetric group level -> extent - priorVars. For an
                // antisymmetric group level -> extent - priorVars - a (strict).
                let subParts =
                    offsets @ (if strict > 0 then [string strict] else [])
                let bound =
                    match subParts with
                    | [] -> sprintf "%s.extents[%d]" name dimIdx
                    | _ ->
                        let sub = subParts |> String.concat " - "
                        sprintf "%s.extents[%d] - %s" name dimIdx sub
                forLoop indent loopVar bound)
        let innerIndent = "    " + String.replicate rank "    "
        let idx = dims |> List.map (fun (v,_,_,_) -> sprintf "[%s]" v) |> String.concat ""
        let inner = [
            sprintf "%sif (!%s) cout << \", \";" innerIndent firstVar
            sprintf "%s%s = false;" innerIndent firstVar
            sprintf "%scout << %s%s;" innerIndent name idx ]
        let closes =
            [for d in rank - 1 .. -1 .. 0 ->
                sprintf "    %s}" (String.replicate d "    ")]
        let finish = [sprintf "    cout << \"]\" << endl;"]
        opens @ loops @ inner @ closes @ finish

/// Compute which binding IDs are deferred (no C++ code generated).
/// A binding is deferred if it's a computation that hasn't been materialized via |> compute.
let computeDeferredIds (bindings: IRBinding list) : Set<int> =
    let isDeferred (ids: Set<int>) (e: IRExpr) =
        match e with
        | IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ | IRFallback _ | IRComposeObj _ | IRComposeMeth _ | IRBind _ | IRZip _ | IRSequence _ -> true
        | IRVar (id, _) -> Set.contains id ids
        | _ -> false
    bindings |> List.fold (fun ids b ->
        let shouldDefer =
            match b.Value with
            | IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ -> true
            | IRZip _ -> true
            | IRComposeObj _ -> true
            | IRBind (comp, _) -> isDeferred ids comp
            | IRComposeMeth (left, right) -> isDeferred ids left || isDeferred ids right
            | IRFunctorMap (_, inner) -> isDeferred ids inner
            | IRChoice (left, right) -> isDeferred ids left || isDeferred ids right
            // <|:> operands are arrays, so the binding ALWAYS defers
            // (genFallbackBinding) — materialization happens at |> compute.
            | IRFallback _ -> true
            | IRGuard (_, body) ->
                let rec leafIsDeferred e =
                    match e with
                    | IRGuard (_, inner) -> leafIsDeferred inner
                    | _ -> isDeferred ids e
                leafIsDeferred body
            | IRSequence elems -> elems |> List.exists (isDeferred ids)
            | IRTuple elems -> elems |> List.forall (isDeferred ids)
            | IRTupleProj (IRVar (pid, _), _, _) -> Set.contains pid ids
            | IRVar (srcId, _) -> Set.contains srcId ids
            | _ -> false
        if shouldDefer then Set.add b.Id ids else ids
    ) Set.empty

let genPrintStatements (modul: IRModule) : string list =
    let deferredIds = computeDeferredIds modul.Bindings
    modul.Bindings |> List.collect (fun b ->
        let isPrintable =
            if Set.contains b.Id deferredIds then false
            // A STREAMED provider read has no materialized array (fiber
            // reads happen inside consuming nests) — nothing to print.
            elif (match Map.tryFind b.Id modul.ProviderReads with
                  | Some spec -> spec.Streamed
                  | None -> false) then false
            else
            match b.Value with
            | IRCompute (IRApplyCombinator _) -> true
            | IRCompute (IRComposeApply _) -> true
            | IRCompute (IRParallel _) -> true
            | IRCompute (IRFusion _) -> true
            | IRCompute (IRVar _) -> true
            | IRCompute (IRFunctorMap _) -> true
            | IRCompute (IRChoice _) -> true
            | IRCompute (IRFallback _) -> true
            | IRCompute (IRComposeMeth _) -> true
            | IRCompute (IRBind _) -> true
            | IRCompute (IRGuard _) -> true
            | IRCompute (IRSequence _) -> true
            | IRCompute _ | IRMethodFor _ | IRObjectFor _ -> false
            | _ -> true
        
        let hasSymmetry =
            match IR.stripUnits b.Type with
            | ArrayElem arr ->
                arr.IndexTypes |> List.exists (fun idx ->
                    idx.Symmetry = SymSymmetric || idx.Symmetry = SymAntisymmetric || idx.Symmetry = SymHermitian)
            | _ -> false
        
        if isPrintable then
            match IR.stripUnits b.Type with
            | IRTScalar (ETFloat64 | ETFloat32 | ETInt64 | ETInt32 | ETBool | ETComplex64 | ETComplex128 | ETString) ->
                genPrintScalar b.Name
            | IRTIdxTagged _ ->
                // Tagged int prints same as int
                genPrintScalar b.Name
            | ArrayElem arrType when isCompoundArrayType arrType ->
                // Compound (load_compound) values wrap a compact buffer plus a
                // compound_index_t pointer; there is no operator<< for
                // Compound<T,RANK>. Skip auto-print with a diagnostic so the
                // generated program still compiles -- scalar value-checks via
                // element access (e.g. data(lead, t)) remain available.
                [sprintf "    // (compound array '%s' not auto-printed; Compound<T,RANK> has no operator<<)" b.Name]
            | ArrayElem arrType ->
                // Phase D: arrays of named (struct) types -- cout's operator<<
                // isn't defined for user types. For rank-1 arrays of structs,
                // emit a per-field print loop driven by the IRTDStruct's
                // declared field list (looked up from modul.Types). Format:
                //   name = [{f1: V, f2: V}, {f1: V, f2: V}, ...]
                // Higher ranks fall back to a skip comment for now;
                // value-checks via scalar field reads still work in either
                // case.
                match arrType.ElemType with
                | FuncElem _ ->
                    // Arrays of functions have no general `operator<<`.
                    // std::function isn't streamable, and a generic print
                    // would need either signature-specific formatting or
                    // address-printing â€” neither is meaningful for testing.
                    // Skip with a diagnostic comment so the surrounding
                    // value-check on scalar results derived from calls
                    // (e.g. `let r = funcs(1)(5.0)`) still runs.
                    [sprintf "    // (array '%s' of function values not auto-printed; std::function isn't streamable)" b.Name]
                | IRTNamed structName ->
                    let rank = arrayRank arrType
                    let structFields =
                        modul.Types |> List.tryPick (fun td ->
                            match td with
                            | IRTDStruct (n, fs) when n = structName -> Some fs
                            | _ -> None)
                    match structFields, rank with
                    | Some fields, 1 when not (List.isEmpty fields) ->
                        let firstVar = sprintf "%s__first" b.Name
                        let fieldPrints =
                            fields |> List.mapi (fun i (fname, _) ->
                                let prefix = if i = 0 then "" else ", "
                                sprintf "        cout << \"%s%s: \" << %s[i].%s;" prefix fname b.Name fname)
                        [
                            sprintf "    cout << \"%s = [\";" b.Name
                            sprintf "    bool %s = true;" firstVar
                            sprintf "    for (size_t i = 0; i < %s.extents[0]; i++) {" b.Name
                            sprintf "        if (!%s) cout << \", \";" firstVar
                            sprintf "        %s = false;" firstVar
                            sprintf "        cout << \"{\";"
                        ]
                        @ fieldPrints
                        @ [
                            sprintf "        cout << \"}\";"
                            "    }"
                            sprintf "    cout << \"]\" << endl;"
                        ]
                    | _ ->
                        // Struct not found in module Types, or rank > 1, or
                        // no fields â€” emit diagnostic comment and skip.
                        [sprintf "    // (array '%s' of struct '%s' not auto-printed; access individual fields via %s[i].field)" b.Name structName b.Name]
                | IRTTuple _ ->
                    // std::tuple has no operator<<; value-checks read
                    // components via destructuring instead.
                    [sprintf "    // (array '%s' of tuple values not auto-printed; std::tuple has no operator<<)" b.Name]
                | _ ->
                let rank = arrayRank arrType
                // Distinguish three cases for ragged-tagged bindings, based on
                // what C++ metadata exists for each:
                //
                //   (a) Ragged literal binding: Value is IRArrayLit with ragged
                //       shape. genArrayLiteral emitted both _lens and _extents.
                //       Use the ragged print loop.
                //
                //   (b) Ragged-peel output: Value is IRApplyCombinator whose
                //       codegen path emitted _extents but not _lens. The
                //       runtime shape is rank-1 rectangular (one scalar per
                //       outer iteration). Use flat print.
                //
                //   (c) Sub-view binding: Value is IRIndex or similar (e.g.,
                //       r(1)). Neither _lens nor _extents was emitted; print
                //       would reference undefined names. Skip.
                let isRaggedLiteralBinding =
                    (isRaggedArrayType arrType || isDepIdxArrayType arrType) &&
                    (match b.Value with
                     | IRArrayLit _ -> true
                     | _ -> false)
                // A FULL compound read that leaves trailing dims (B((i,j)) or
                // B((i,j), _) on Array<T like CompoundIdx<m>, Idx<...>>) binds
                // the raw trailing-row T* (.row()), which carries no .extents
                // member -- the flat print loop would not compile. Skip with a
                // diagnostic; scalar derivations (r(t)) still print. (PARTIAL
                // reads are unaffected: they produce real wrappers.)
                let isCompoundRowSubview =
                    match b.Value with
                    | IRIndex (a, (IRTuple coords) :: _, _) ->
                        (match inferExprType a with
                         | ArrayElem at when isCompoundArrayType at ->
                             let k =
                                 at.IndexTypes
                                 |> List.tryFind (fun ix -> ix.IxKind = IxKCompound)
                                 |> Option.map (fun ix -> ix.Rank)
                                 |> Option.defaultValue coords.Length
                             (match classifyCompoundIndexTuple k coords with
                              | CompoundFull -> true  // dense-typed result of a full read = trailing row
                              | CompoundPartial _ -> false)
                         | _ -> false)
                    | _ -> false
                // Look through IRCompute wrappers (from |> compute) to find
                // the underlying combinator. Also check |> bind continuations
                // and other materialization wrappers as needed.
                let rec unwrapMaterialization (e: IRExpr) : IRExpr =
                    match e with
                    | IRCompute inner -> unwrapMaterialization inner
                    | _ -> e
                let isRaggedPeelOutput =
                    isRaggedArrayType arrType &&
                    (match unwrapMaterialization b.Value with
                     | IRApplyCombinator _ -> true
                     | _ -> false)
                // A rank-1 ragged-family SUB-VIEW binding (`let row = r(i)`,
                // `let g0 = grouped(i)`) is a RaggedRow<T> with an inline
                // `.len` -- printable directly, now that the binding carries
                // its length (it used to bind as a bare T* and had to be
                // skipped).
                let isRaggedRowBinding =
                    isRaggedRowType arrType &&
                    (match b.Value with IRIndex _ -> true | _ -> false)
                if isCompoundRowSubview then
                    [sprintf "    // (trailing-row view '%s' not auto-printed; the raw T* row carries no extents â€” derive scalars via %s(t))" b.Name b.Name]
                elif isRaggedRowBinding then
                    let firstVar = sprintf "%s__first" b.Name
                    [
                        sprintf "    cout << \"%s = [\";" b.Name
                        sprintf "    bool %s = true;" firstVar
                        sprintf "    for (size_t __rk = 0; __rk < %s.len; __rk++) {" b.Name
                        sprintf "        if (!%s) cout << \", \";" firstVar
                        sprintf "        %s = false;" firstVar
                        sprintf "        cout << %s[__rk];" b.Name
                        "    }"
                        "    cout << \"]\" << endl;"
                    ]
                elif isRaggedLiteralBinding || (isRaggedPeelOutput && rank >= 2) then
                    // Ragged wrapper with lens/offsets companions: a ragged
                    // LITERAL, or an ELEMENTWISE map output (shape-preserving
                    // Ragged<T> sharing the parent's metadata). Iterate rows
                    // via .lens; print as the flat value sequence the
                    // validation framework expects.
                    let firstVar = sprintf "%s__first" b.Name
                    [
                        sprintf "    cout << \"%s = [\";" b.Name
                        sprintf "    bool %s = true;" firstVar
                        sprintf "    for (size_t __ri = 0; __ri < %s.extents[0]; __ri++) {" b.Name
                        sprintf "        for (size_t __rj = 0; __rj < %s.lens[__ri]; __rj++) {" b.Name
                        sprintf "            if (!%s) cout << \", \";" firstVar
                        sprintf "            %s = false;" firstVar
                        sprintf "            cout << %s[__ri][__rj];" b.Name
                        "        }"
                        "    }"
                        "    cout << \"]\" << endl;"
                    ]
                elif isRaggedPeelOutput then
                    // Peel output is rank-1 rectangular at runtime; flat print
                    // works regardless of the type-level rank/tag.
                    genPrintArrayFlat b.Name 1
                elif isRaggedArrayType arrType then
                    // Sub-view binding: no metadata to drive a print loop.
                    // Skip rather than emit broken code. Scalar derivations
                    // from the sub-view still print normally.
                    [sprintf "    // (sub-view of ragged array '%s' not printed; metadata not propagated)" b.Name]
                elif hasSymmetry && rank >= 2 && rank <= 8 then
                    genPrintArraySymAware b.Name arrType.IndexTypes
                else
                    genPrintArrayFlat b.Name rank
            | IRTTuple _ -> []
            | IRTNamed _ -> []
            | IRTUnit -> []
            | _ -> []
        else []
    )

/// Assemble the main() function wrapper around binding code and print statements.
/// `mpi` = true (MPI emit mode + module uses mpi): main takes argc/argv,
/// brackets the body with MPI_Init/Finalize, and guards the timing + result
/// prints behind rank 0 â€” every rank computes (SPMD), exactly one rank
/// reports, so output is deterministic and byte-comparable to a serial run.
/// `mpi` = false emits the historical wrapper byte-identically.
let genMainWrapper (mpi: bool, mpiThreaded: bool) (testName: string) (bodyIndented: string list) (printCode: string list) : string list =
    let header =
        if mpi then
            [ "int main(int argc, char** argv) {"
              "    cout << std::setprecision(15);"
              "    cout << std::boolalpha;"
              (if mpiThreaded then
                  "    { int __blade_mpi_prov; MPI_Init_thread(&argc, &argv, MPI_THREAD_FUNNELED, &__blade_mpi_prov); if (__blade_mpi_prov < MPI_THREAD_FUNNELED) { std::cerr << \"error[BL8004]: MPI thread support below MPI_THREAD_FUNNELED\" << std::endl; MPI_Abort(MPI_COMM_WORLD, 14); } }"
               else
                  "    MPI_Init(&argc, &argv);")
              "    MPI_Comm_rank(MPI_COMM_WORLD, &__blade_mpi_rank);"
              "    MPI_Comm_size(MPI_COMM_WORLD, &__blade_mpi_size);"
              "    auto start = TIME;"
              "" ]
        else
            [ "int main() {"
              "    cout << std::setprecision(15);"
              "    cout << std::boolalpha;"
              "    auto start = TIME;"
              "" ]
    let timing =
        if mpi then
            [ ""
              "    auto end = TIME;"
              "    double elapsed = 1e-9 * TIME_DIFF;"
              sprintf "    if (__blade_mpi_rank == 0) { cout << \"%s completed in \" << elapsed << \"s\" << endl; }" testName
              ""
              "    // Print results for verification (rank 0 only)"
              "    if (__blade_mpi_rank == 0) {" ]
        else
            [ ""
              "    auto end = TIME;"
              "    double elapsed = 1e-9 * TIME_DIFF;"
              sprintf "    cout << \"%s completed in \" << elapsed << \"s\" << endl;" testName
              ""
              "    // Print results for verification" ]
    let footer =
        if mpi then
            [ "    }"
              ""
              "    MPI_Finalize();"
              "    return 0;"
              "}" ]
        else
            [ ""
              "    return 0;"
              "}" ]
    // Wrap the whole body in try/catch so C++ exceptions (bad_alloc, etc.)
    // route to blade_rt::panic (BL8005) instead of std::terminate. MPI
    // init/finalize straddle the try; a panic exits without MPI_Finalize,
    // acceptable on a failure path. Success-path output is byte-identical.
    let tryLine = [ "    try {" ]
    let catchClose =
        [ "    } catch (const std::exception& e) { blade_rt::panic(\"BL8005\", e.what(), nullptr, 0); }"
          "      catch (...) { blade_rt::panic(\"BL8005\", \"unknown exception\", nullptr, 0); }"
          "}" ]
    let footerBody = footer |> List.rev |> List.tail |> List.rev  // drop footer's closing "}"
    header @ tryLine @ bodyIndented @ timing @ printCode @ footerBody @ catchClose

/// Split-timing variant of genMainWrapper. `setupIndented` is input-data setup
/// (array literals, etc.); `computeIndented` is the computation. Emits two
/// checkpoints: "Input Allocation took <t>s" around setup, and the canonical
/// "<name> completed in <t>s" around ONLY the compute region â€” so the harness's
/// existing "completed in" parser reads the compute time, not the whole body.
/// The clock variable is reused (start/end reset between phases) exactly as the
/// archaic Blade prototype did.
let genMainWrapperSplit (mpi: bool, mpiThreaded: bool) (testName: string) (setupIndented: string list) (computeIndented: string list) (printCode: string list) : string list =
    let header =
        if mpi then
            [ "int main(int argc, char** argv) {"
              "    cout << std::setprecision(15);"
              "    cout << std::boolalpha;"
              (if mpiThreaded then
                  "    { int __blade_mpi_prov; MPI_Init_thread(&argc, &argv, MPI_THREAD_FUNNELED, &__blade_mpi_prov); if (__blade_mpi_prov < MPI_THREAD_FUNNELED) { std::cerr << \"error[BL8004]: MPI thread support below MPI_THREAD_FUNNELED\" << std::endl; MPI_Abort(MPI_COMM_WORLD, 14); } }"
               else
                  "    MPI_Init(&argc, &argv);")
              "    MPI_Comm_rank(MPI_COMM_WORLD, &__blade_mpi_rank);"
              "    MPI_Comm_size(MPI_COMM_WORLD, &__blade_mpi_size);"
              "    auto start = TIME;"
              "" ]
        else
            [ "int main() {"
              "    cout << std::setprecision(15);"
              "    cout << std::boolalpha;"
              "    auto start = TIME;"
              "" ]
    let setupTiming =
        let line = sprintf "cout << \"%s input allocation took \" << setup_elapsed << \"s\" << endl;" testName
        [ ""
          "    auto end = TIME;"
          "    double setup_elapsed = 1e-9 * TIME_DIFF;"
          (if mpi then sprintf "    if (__blade_mpi_rank == 0) { %s }" line else "    " + line)
          ""
          "    start = TIME;" ]
    let computeTiming =
        let line = sprintf "cout << \"%s completed in \" << elapsed << \"s\" << endl;" testName
        if mpi then
            [ ""
              "    end = TIME;"
              "    double elapsed = 1e-9 * TIME_DIFF;"
              sprintf "    if (__blade_mpi_rank == 0) { %s }" line
              ""
              "    // Print results for verification (rank 0 only)"
              "    if (__blade_mpi_rank == 0) {" ]
        else
            [ ""
              "    end = TIME;"
              "    double elapsed = 1e-9 * TIME_DIFF;"
              "    " + line
              ""
              "    // Print results for verification" ]
    let footer =
        if mpi then
            [ "    }"
              ""
              "    MPI_Finalize();"
              "    return 0;"
              "}" ]
        else
            [ ""
              "    return 0;"
              "}" ]
    // See genMainWrapper: wrap the body in try/catch -> blade_rt::panic (BL8005).
    let tryLine = [ "    try {" ]
    let catchClose =
        [ "    } catch (const std::exception& e) { blade_rt::panic(\"BL8005\", e.what(), nullptr, 0); }"
          "      catch (...) { blade_rt::panic(\"BL8005\", \"unknown exception\", nullptr, 0); }"
          "}" ]
    let footerBody = footer |> List.rev |> List.tail |> List.rev  // drop footer's closing "}"
    header @ tryLine @ setupIndented @ setupTiming @ computeIndented @ computeTiming @ printCode @ footerBody @ catchClose

/// Generate a C++ program (uses external runtime header)
/// Generate print statements for all bindings in a module.
/// Shared by genSelfContainedProgram and genProgramWithExternalRuntime.
let genMainProgram (modul: IRModule) (testName: string) : string =
    (exprWarningsCell ()).Value <- []
    // Reset the CUDA kernel collector; genCudaKernel appends during genModule.
    (cudaKernelDefsCell ()).Value <- []
    (symmDeclsCell ()).Value <- []
    (streamBufDeclsCell ()).Value <- Set.empty
    let builder = IRBuilder()
    // Codegen-synthesized ids (sequence children, __s1 stages, __ret temps)
    // must not collide with typecheck/lowering ids arriving in the module â€”
    // a reused id re-registers the original variable's name in VarNames.
    // 2^30 is far above any real program's id count.
    builder.EnsureAtLeast(0x40000000)

    let includes = genIncludes ()
    // MPI scaffolding (see genSelfContainedProgram).
    let mpiOn = mpiEmitModeEnabled () && moduleUsesMpi modul
    setMpiProgramOn mpiOn
    let includes = if mpiOn then includes @ ["#include <mpi.h>"; "#include \"linearized_storage.hpp\""] else includes
    let mpiDecls =
        if mpiOn then
            [ "static int __blade_mpi_rank = 0;"
              "static int __blade_mpi_size = 1;" ]
        else []
    let (funcDefs, bindCode) = genModule modul builder

    // extern "C" launch-wrapper prototypes for any CUDA kernels emitted during
    // genModule. Bodies live in the .cu (nvcc); the .cpp needs only the proto to
    // call across the linkage boundary. Extract each wrapper's signature line
    // (starts `extern "C" void __launch_`) and `;`-terminate it.
    let cudaProtos =
        (cudaKernelDefsCell ()).Value
        |> List.filter (fun line -> line.StartsWith("extern \"C\"") && line.Contains("void __launch_"))
        |> List.map (fun sigLine ->
            // The hybrid (mpi+cuda) wrappers are dllexport'd in the .cu;
            // the host proto imports plainly (MinGW links the DLL exports).
            let trimmed = sigLine.Replace("__declspec(dllexport) ", "").TrimEnd()
            (if trimmed.EndsWith("{") then trimmed.Substring(0, trimmed.Length - 1).TrimEnd() else trimmed) + ";")
    let symmDecls = (symmDeclsCell ()).Value

    let bodyIndented = bindCode |> List.map (fun s -> "    " + s)
    let mainFunc = genMainWrapper (mpiOn, mpiOn && moduleHybridMpiOmp modul) testName bodyIndented []

    (includes @ [""] @ mpiDecls @ symmDecls @ [""] @ cudaProtos @ [""] @ funcDefs @ mainFunc) |> String.concat "\n"

/// The .cu file content for the most recently assembled program, or None if no
/// CUDA kernel was emitted. Call AFTER genMainProgram/genProgramFromIR (the
/// collector is populated during assembly).
let getCudaFileContent () : string option =
    match (cudaKernelDefsCell ()).Value with
    | [] -> None
    | defs ->
        let header =
            [ "// Generated CUDA kernels (.cu) â€” compiled by nvcc, linked with the .cpp."
              "#include <cstddef>"
              "#include <cstdint>"
              "" ]
        Some ((header @ defs) |> String.concat "\n")

/// Generate a complete C++ program from an IR program (all modules)
let genProgramFromIR (program: IRProgram) (testName: string) : string =
    match program.Modules with
    | [] -> "// Empty program\nint main() { return 0; }\n"
    | [modul] -> genMainProgram modul testName
    | modules ->
        let merged = {
            Name = "merged"
            Types = modules |> List.collect (fun m -> m.Types)
            Functions = modules |> List.collect (fun m -> m.Functions)
            Bindings = modules |> List.collect (fun m -> m.Bindings)
            StaticFunctionUsage = Map.empty
            ProviderReads = modules |> List.fold (fun acc m -> Map.fold (fun a k v -> Map.add k v a) acc m.ProviderReads) Map.empty
            ProviderWrites = modules |> List.fold (fun acc m -> Map.fold (fun a k v -> Map.add k v a) acc m.ProviderWrites) Map.empty
            RandomInits = modules |> List.fold (fun acc m -> Map.fold (fun a k v -> Map.add k v a) acc m.RandomInits) Map.empty
            CompoundInits = modules |> List.fold (fun acc m -> Map.fold (fun a k v -> Map.add k v a) acc m.CompoundInits) Map.empty
            MutableArrayLets = modules |> List.fold (fun acc m -> Set.union acc m.MutableArrayLets) Set.empty
        }
        genMainProgram merged testName

/// Provider-driven #include lines for a module: the union of each involved
/// provider's declared includes (registry-dispatched over the module's
/// reads and writes), plus linearized_storage.hpp when any provider-read or
/// -written array is packed (SymIdx/AntisymIdx) — the unlinearize copy in
/// packed readers is index-type-driven, not provider-specific. Deduplicated,
/// provider order sorted for deterministic emission.
let providerIncludes (modul: IRModule) : string list =
    let readSpecs = modul.ProviderReads |> Map.toList |> List.map snd
    let writeSpecs = modul.ProviderWrites |> Map.toList |> List.map snd
    let providers =
        (readSpecs |> List.map (fun s -> s.Provider))
        @ (writeSpecs |> List.map (fun s -> s.Provider))
        |> List.distinct |> List.sort
    let fromProviders =
        providers |> List.collect (fun p ->
            match Blade.ProviderRegistry.tryFind p with
            | Some spec -> spec.Includes ()
            | None -> [])
    let isPackedArr (at: IRArrayType) =
        at.IndexTypes |> List.exists (fun ix -> ix.Symmetry <> SymNone && ix.Rank >= 2)
    let anyPacked =
        (readSpecs |> List.exists (fun s -> isPackedArr s.VarType))
        || (writeSpecs |> List.exists (fun s -> isPackedArr s.SourceType))
    (fromProviders @ (if anyPacked then ["#include \"linearized_storage.hpp\""] else []))
    |> List.distinct

/// Generate C++ struct definition from IRTDStruct
let genSelfContainedProgram (modul: IRModule) (testName: string) : string =
    let builder = IRBuilder()
    builder.EnsureAtLeast(0x40000000)  // see genMainProgram: keep codegen ids disjoint
    // Reset the CUDA kernel collector for this program; genCudaKernel appends
    // during genModule. Read afterward via getCudaFileContent for the .cu file.
    (cudaKernelDefsCell ()).Value <- []
    // Reset the symm-decl hoist collector; symmetric outputs append namespace-
    // scope symm arrays during genModule, emitted in the preamble below.
    (symmDeclsCell ()).Value <- []
    (streamBufDeclsCell ()).Value <- Set.empty
    
    let includes =
        // Provider reads/writes emit provider-specific runtime calls (nc_*,
        // fstream chunk I/O, ...) needing their own headers. Added only when
        // the module actually has provider I/O, so non-provider programs gain
        // no extra dependency (registry-dispatched per provider).
        genIncludesExternal () @ providerIncludes modul
    // MPI scaffolding: only when the emit gate is on AND the module has an
    // mpi kernel (a PURE predicate â€” includes/printCode are computed before
    // genModule runs, so an emission-time cell would not work here). Adds
    // <mpi.h> and the namespace-scope rank/size globals (loop nests are also
    // emitted inside top-level function bodies, so main() locals can't work).
    // Defaults 0/1 keep any pre-Init execution serially correct.
    let mpiOn = mpiEmitModeEnabled () && moduleUsesMpi modul
    setMpiProgramOn mpiOn
    let includes = if mpiOn then includes @ ["#include <mpi.h>"; "#include \"linearized_storage.hpp\""] else includes
    let mpiDecls =
        if mpiOn then
            [ "static int __blade_mpi_rank = 0;"
              "static int __blade_mpi_size = 1;" ]
        else []
    let typeDefs = genTypeDefs modul
    let printCode = genPrintStatements modul

    // Split-timing mode emits two clock checkpoints (input allocation vs
    // compute) via genModuleSplit + genMainWrapperSplit; default mode emits the
    // single whole-body clock. Both share the same CUDA-proto / symm-decl
    // preamble, computed after generation (genModule* populates the cells).
    let mainFunc =
        if splitTimingModeEnabled () then
            let (funcDefs, setupCode, computeCode) = genModuleSplit modul builder
            let setupIndented = setupCode |> List.map (fun s -> "    " + s)
            let computeIndented = computeCode |> List.map (fun s -> "    " + s)
            (funcDefs, genMainWrapperSplit (mpiOn, mpiOn && moduleHybridMpiOmp modul) testName setupIndented computeIndented printCode)
        else
            let (funcDefs, bindCode) = genModule modul builder
            let bodyIndented = bindCode |> List.map (fun s -> "    " + s)
            (funcDefs, genMainWrapper (mpiOn, mpiOn && moduleHybridMpiOmp modul) testName bodyIndented printCode)
    let (funcDefs, mainBody) = mainFunc

    // extern "C" launch-wrapper prototypes for any CUDA kernels emitted: the
    // .cpp calls them across the linkage boundary (bodies live in the .cu).
    let cudaProtos =
        (cudaKernelDefsCell ()).Value
        |> List.filter (fun line -> line.StartsWith("extern \"C\"") && line.Contains("void __launch_"))
        |> List.map (fun sigLine ->
            // The hybrid (mpi+cuda) wrappers are dllexport'd in the .cu;
            // the host proto imports plainly (MinGW links the DLL exports).
            let trimmed = sigLine.Replace("__declspec(dllexport) ", "").TrimEnd()
            (if trimmed.EndsWith("{") then trimmed.Substring(0, trimmed.Length - 1).TrimEnd() else trimmed) + ";")

    // Namespace-scope symm arrays hoisted out of main() (MSVC constant-address
    // requirement â€” see hoistSymmDecl).
    let symmDecls = (symmDeclsCell ()).Value

    (includes @ typeDefs @ [""] @ mpiDecls @ symmDecls @ [""] @ cudaProtos @ [""] @ funcDefs @ mainBody) |> String.concat "\n"

/// Generate a C++ program with external runtime header
/// Returns (mainFileContent, headerFileContent)
let genProgramWithExternalRuntime (modul: IRModule) (testName: string) : string * string =
    let builder = IRBuilder()
    builder.EnsureAtLeast(0x40000000)  // see genMainProgram: keep codegen ids disjoint
    
    let includes =
        // See genSelfContainedProgram: provider headers only for provider I/O.
        genIncludesExternal () @ providerIncludes modul
    // MPI scaffolding (see genSelfContainedProgram).
    let mpiOn = mpiEmitModeEnabled () && moduleUsesMpi modul
    setMpiProgramOn mpiOn
    let includes = if mpiOn then includes @ ["#include <mpi.h>"; "#include \"linearized_storage.hpp\""] else includes
    let mpiDecls =
        if mpiOn then
            [ "static int __blade_mpi_rank = 0;"
              "static int __blade_mpi_size = 1;" ]
        else []
    let typeDefs = genTypeDefs modul
    let (funcDefs, bindCode) = genModule modul builder

    let bodyIndented = bindCode |> List.map (fun s -> "    " + s)
    let printCode = genPrintStatements modul
    let mainFunc = genMainWrapper (mpiOn, mpiOn && moduleHybridMpiOmp modul) testName bodyIndented printCode

    let mainFile = (includes @ typeDefs @ [""] @ mpiDecls @ funcDefs @ mainFunc) |> String.concat "\n"
    let headerFile = genRuntimeHeader ()
    (mainFile, headerFile)

/// Generate a self-contained C++ program from an IR program
let genSelfContainedProgramFromIR (program: IRProgram) (testName: string) : string * string list =
    // Reset module-level expression warnings (per-task via AsyncLocal cell)
    let cell = exprWarningsCell ()
    cell.Value <- []
    let code =
        match program.Modules with
        | [] -> "// Empty program\nint main() { return 0; }\n"
        | [modul] -> genSelfContainedProgram modul testName
        | modules ->
            // Multi-module: merge all modules into one for code generation
            // Functions and bindings from earlier modules come first
            let merged = {
                Name = "merged"
                Types = modules |> List.collect (fun m -> m.Types)
                Functions = modules |> List.collect (fun m -> m.Functions)
                Bindings = modules |> List.collect (fun m -> m.Bindings)
                StaticFunctionUsage = modules |> List.fold (fun acc m -> 
                    Map.fold (fun a k v -> Map.add k v a) acc m.StaticFunctionUsage) Map.empty
                ProviderReads = modules |> List.fold (fun acc m -> Map.fold (fun a k v -> Map.add k v a) acc m.ProviderReads) Map.empty
                ProviderWrites = modules |> List.fold (fun acc m -> Map.fold (fun a k v -> Map.add k v a) acc m.ProviderWrites) Map.empty
                RandomInits = modules |> List.fold (fun acc m -> Map.fold (fun a k v -> Map.add k v a) acc m.RandomInits) Map.empty
                CompoundInits = modules |> List.fold (fun acc m -> Map.fold (fun a k v -> Map.add k v a) acc m.CompoundInits) Map.empty
                MutableArrayLets = modules |> List.fold (fun acc m -> Set.union acc m.MutableArrayLets) Set.empty
            }
            genSelfContainedProgram merged testName
    (code, cell.Value)
