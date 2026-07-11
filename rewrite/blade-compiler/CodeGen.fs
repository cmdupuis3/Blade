// Blade-DSL C++ Code Generation
// Transforms IR structures into C++ source code
// Generates complete, compilable C++ programs

module Blade.CodeGen

open Blade.IR

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
    /// Set of struct type names that have where constraints (need validate() calls)
    ConstrainedStructs: Set<string>
    /// Map from C++ variable name to its tuple children names (for extents provenance)
    /// e.g., "_0" → ["_0_0", "_0_1"] means _0 is a pair whose components are _0_0 and _0_1
    TupleChildren: Map<string, string list>
    /// Map from IRId to deferred computation expressions (not yet materialized).
    /// Computations (L <@> f) are only materialized when |> compute forces them.
    /// Combinators like <&!> look through variables to find the original ApplyInfo for fusion.
    DeferredComputations: Map<IRId, IRExpr>
    /// Deferred provider reads, keyed by the receiving binding's IRId, lifted
    /// from IRModule.ProviderReads. Consumed by genBinding to emit the NetCDF
    /// reader (genReadVar / genReadCompoundVar).
    ProviderReads: Map<IRId, ProviderReadSpec>
    /// Deferred random-fill constructors, keyed by the receiving binding's IRId,
    /// lifted from IRModule.RandomInits. Consumed by genBinding to emit
    /// allocate<> + the runtime fill_random. Value is the modulus expression.
    RandomInits: Map<IRId, IRExpr>
    /// Deferred compound-construction constructors (compound(dense, mask)), keyed
    /// by the receiving binding's IRId, lifted from IRModule.CompoundInits.
    /// Consumed by genBinding to emit P0 index materialization + a dense->compact
    /// scatter. Value is (loweredDense, loweredMask).
    CompoundInits: Map<IRId, IRExpr * IRExpr>
    /// Map from grouped-array C++ name to its source GroupKeys C++ name.
    /// Populated by genBinding for IRGroupBy; consulted by method_for codegen
    /// when peeling a ragged outer dimension (Tag = "__group_outer").
    GroupedArrays: Map<string, string>
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
/// this by passing nullptr; symmetric ones cannot — they need the actual array.)
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
/// per distinct name — avoids duplicate definitions if the same output symm is
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
///   Ok rhs    — the `{ allocate...(extents), extents }` brace-init expression
///               the Array<T,N> wrapper is initialized from, or
///   Error msg — a diagnostic for a shape with no representable allocator; the
///               call site emits a `#error` line so the TU fails loudly rather
///               than silently mis-allocating.
///
///   AllocDense        -> allocate<promote<T,R>::type, nullptr>(extents)
///   AllocSymmetric    -> allocate<promote<T,R>::type, SYMM>(extents)   (SYMM hoisted)
///   AllocAntisymmetric-> allocate<promote<T,R>::type, {1,..}, false>(extents)
///                        (all-grouped mask + DIAGONALS=false = strict simplex;
///                         unified with the symmetric path — antisym is a strict
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
        // symmetric — antisym is "a symmetric grouping that happens to be
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
        // like symmetric) — the caller builds it via buildSymmVecWithStrict so
        // SYMM and STRICT align position-for-position. Sign is handled lazily on
        // read (canon_*), never baked into storage.
        let strictName = hoistSymmDecl (sprintf "%s_strict" extentsName) strictVec
        Ok (sprintf "{ allocate_strict<typename promote<%s, %d>::type, %s, %s>(%s), %s }"
                elemType rank symmArg strictName extentsName extentsName)
    | AllocUnsupported reason ->
        Error (sprintf "Blade codegen: unsupported antisymmetric output storage — %s" reason)

/// Module-level OpenMP test-mode flag. When set, parallel loop nests emit
/// additional thread-coverage instrumentation: each parallel region records
/// which OpenMP threads actually executed iterations, and prints a parseable
/// line after the loop. This lets the test harness verify that the emitted
/// pragmas produce GENUINE parallel regions (the runtime honored them), not
/// just that they are syntactically present.
///
/// This is OFF by default and is toggled ON only by the test harness — never
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
/// checkpoints — one around input-data setup ("Input Allocation took <t>s")
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
/// before it — producers, decompact chains, any setup — is attributed to the
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
/// compiled and linked alongside the .cpp — which happens ONLY in the dedicated
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
// tree — no transformations happen between — so references are stable.
//
// Linear search is fine: per-mask probe counts are small (typically 1–3).
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
    ConstrainedStructs = Set.empty
    TupleChildren = Map.empty
    DeferredComputations = Map.empty
    ProviderReads = Map.empty
    RandomInits = Map.empty
    CompoundInits = Map.empty
    GroupedArrays = Map.empty
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

// (The duplicate collectVarRefs walker that lived here is gone — audit
// §3.2 [now]. Capture computation and match-case usage checks now call
// IR.collectVarRefsIR, the canonical ExprShape-based collector.)

/// Struct-fields registry for `IRFieldAccess` type resolution. ONE cache now
/// (half of audit §2.4's duplicated-cache hazard fixed): this forwards to
/// IR.fs's AsyncLocal cache — the same registry the lift pass populates — so
/// both population points (liftInlineFormsModule entry and genModule entry)
/// fill the same cache from the same module's Types, and a pass reordering
/// or new entry point can no longer leave codegen consulting an empty
/// duplicate. Kept under its historical name; it is just IR.setStructFieldsCache.
let setCodegenStructFieldsCache (types: IRTypeDef list) = IR.setStructFieldsCache types

// (Synthetic sentinel index IDs and the compound partial-index
// classification — CompoundIndexForm, classifyCompoundIndexTuple,
// synthSlotId* — moved to IR.fs beside the canonical typeOf (audit §2.2);
// they resolve here via `open Blade.IR`.)

/// Infer type from an IR expression — thin alias of the canonical
/// reconstruction (audit §2.2): the full derivation lives in IR.typeOf,
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
///   - InvalidElem: hard error — these types have no value-level meaning.
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
    | IRTTuple ts -> sprintf "std::tuple<%s>" (ts |> List.map irTypeToCpp |> String.concat ", ")
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
        // unconditionally — a `using <name> = ...;` is emitted alongside
        // the type declaration, so the alias is in scope. For IRefAnon
        // there's no alias to use; render the inner type directly.
        match idxRef with
        | IRefNamed name -> name
        | IRefAnon _ -> irTypeToCpp inner
    | IRTNamed "String" -> "std::string"  // Blade String → C++ std::string
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
        //     arrow, renders as `promote<elem, rank>::type` — the raw
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
                    | _ -> failwith "unreachable — guarded by isAllSVal")
            let paramList = String.concat ", " paramTypes
            sprintf "std::function<%s(%s)>" (arrowSlotTypeForFuncSig result) paramList
        elif (isAllStored || isAllVirtual) && not slots.IsEmpty then
            // Reconstruct an IRArrayType view for rendering
            let indexTypes =
                slots |> List.map (function
                    | SIdx i | SIdxVirt i -> i
                    | _ -> failwith "unreachable")
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
/// loudly rather than silently emit code that might miscompile — e.g., indexing
/// with a float, or narrowing int64 keys to double in a sort comparator.
/// Returns (elemType as IRType, optional error code).
let inferElemTypeStrict (ctx: CodeGenContext) (ind: string) (expr: IRExpr) (opName: string) : IRType * string list =
    match inferExprType expr with
    | ArrayElem arr -> (arr.ElemType, [])
    | IRTScalar _ as t -> (t, [])
    | t ->
        let msg = sprintf "%s: could not determine element type from expression (got %A) — likely a typechecker or IR bug" opName t
        let errLines = codegenError ctx ind msg
        // Sentinel: the #error makes the C++ refuse to compile, so the
        // Float64 we return is never actually exercised in valid output.
        (IRTScalar ETFloat64, errLines)


/// Variant of inferElemTypeStrict for ctx-less callers (e.g., inside
/// `exprToCpp` or other pure expression-rendering helpers). Mirrors the
/// "peel array combinator wrappers and infer the elem type" pattern that
/// inline-form codegen uses. On failure, records a warning into the
/// AsyncLocal exprWarnings collector and returns a sentinel C++ type
/// string that won't compile — so a regression at this layer surfaces
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
            [sprintf "%s: could not determine element type from inline form (got %A) — likely a typechecker or IR bug" opName t]
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

/// True iff a type's underlying scalar is a complex element type. Used to
/// decide whether conj must emit std::conj (complex) or is the identity (real).
let rec isComplexType (t: IRType) : bool =
    match t with
    | IRTScalar (ETComplex64 | ETComplex128) -> true
    | IRTIdxTagged (inner, _) -> isComplexType inner
    | IRTUnitAnnotated (inner, _) -> isComplexType inner
    | _ -> false

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
    | IRLit (IRLitFloat f) -> sprintf "%g" f
    | IRLit (IRLitBool b) -> if b then "true" else "false"
    | IRLit (IRLitString s) -> sprintf "std::string(%s)" (escapeStringLit s)
    | IRLit IRLitUnit -> "((void)0)"
    | IRVar (id, _) -> Map.tryFind id names |> Option.defaultValue (sprintf "__v%d" id)
    | IRParam (name, _, _) -> name
    | IRBinOp (_, op, l, r) ->
        let lStr = exprToCppSimple names l
        let rStr = exprToCppSimple names r
        if op = IRCaret then sprintf "pow(%s, %s)" lStr rStr
        else sprintf "(%s %s %s)" lStr (binOpToCpp op) rStr
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
    | IRLitFloat f -> sprintf "%g" f
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

/// Detect whether an IRArrayType represents a DepIdx array — outer Idx plus
/// an inner record whose Extent is a function of the outer iteration index.
/// Recognized by the `__depidx_inner` tag on a non-first index. Once
/// allocated (lens computed from formula at construction), the runtime
/// layout matches a ragged array — same `_lens` / `_offsets` / row-pointer
/// companions — so iteration-time predicates treat both as "has row-lengths
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
///   * Rank-1 ragged/dep-idx → `RaggedRow<T>` (a peeled-row slice — produced
///     when the kernel side of a `method_for` over a 2D ragged peels the
///     inner-ragged dim into the kernel's binding; the runtime layout is
///     a row pointer + length).
///   * Rank ≥ 2 ragged/dep-idx → `Ragged<T>` (full multi-row container).
///   * Otherwise → `Array<T, N>` (uniform shape).
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
/// IRTInfer or a synthesized shape, and (2) handling void returns —
/// since C++14, `return voidFn()` in an `auto`-returning context deduces
/// to `void` and is legal. Consumers that need a specific signature
/// (e.g., an `std::function<T(P)>`-typed slot) can still wrap the wrapper.
///
/// The wrapper name combines the callable id with a caller-supplied
/// suffix. Same callable referenced from multiple consumer sites at the
/// same C++ scope would otherwise produce duplicate names (since the
/// callable id alone isn't unique-per-use). Callers pass a suffix that
/// disambiguates — typically the let binding's name or a fresh counter
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
/// (deferred — for now we error out).
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
        match Map.tryFind id names with
        | Some name -> name
        | None -> sprintf "__v%d" id
    | IRParam (name, _, _) -> name
    | IRBinOp (_, op, l, r) ->
        let lStr = exprToCppCore subst names l
        let rStr = exprToCppCore subst names r
        if op = IRCaret then
            sprintf "pow(%s, %s)" lStr rStr
        else
            sprintf "(%s %s %s)" lStr (binOpToCpp op) rStr
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
            // Flat projection into potentially nested tuple — compute navigation path
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
        // This fires ONLY for compact-group random access — the case nothing
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
                            // No real fold happened (shouldn't reach: anyCompact true) — return raw.
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
                            failwithf "Partial compound indexing combined with a SUPPLIED trailing index is not yet supported; leave the trailing dim free (omit it or write `_`), or index the residual separately (let r = B((...)); r(...))."
                        elif trailingDims.Length > 1 then
                            failwithf "Partial compound indexing with %d trailing dimensions is not supported (multi-trailing compounds are unsupported throughout: the wrapper stores only the trailing-stride product, not per-dim extents)." trailingDims.Length
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
    | IRApp (func, args, _) ->
        // Function signatures take Array<T,N> / Ragged<T> wrappers
        // natively, one argument per Blade param. Array args pass through
        // as-is (the wrapper carries its own shape via .extents/.lens/
        // .offsets); no companion-arg synthesis. Non-array args render
        // through exprToCpp normally.
        let funcStr = exprToCppCore subst names func
        let argStrs =
            args |> List.collect (fun a ->
                let argStr = exprToCppCore subst names a
                match a, inferExprType a with
                | (IRVar _ | IRParam _), ArrayElem _ -> [argStr]
                | _ -> [argStr])
        sprintf "%s(%s)" funcStr (argStrs |> String.concat ", ")
    | IRLet (id, value, body) ->
        // For inline let expressions, we need statement context
        let names' = Map.add id (sprintf "__v%d" id) names
        if isUnitExpr value then
            // Unit-valued binding: skip the auto declaration
            if isUnitExpr body then
                "((void)0)"
            else
                exprToCppCore subst names' body
        else
            // Phase C lift pass produces IRLet bindings whose value can be
            // an inline form (mask/sort/intersect/union). These can't be
            // rendered as a single C++ expression — they need a multi-
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
                | _ ->
                    let bodyStr = exprToCppCore subst names' body
                    sprintf "([&]() { auto __v%d = %s; return %s; }())" id valStr bodyStr
    | IRMethodFor _ -> exprError "loop object used as value"
    | IRObjectFor _ -> exprError "loop object used as value"
    | IRApplyCombinator _ | IRComposeApply _ -> 
        exprError "unevaluated computation used as value - use |> compute"
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
        // Statically resolved when the index type's extent expression is a
        // literal-arithmetic value (Idx<5>, Idx<n+1> with n compile-time, etc.)
        // — emit as a compile-time literal eligible for use in static contexts.
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
            // Should be unreachable — typecheck rejects non-arrays. Surface a
            // visible #error rather than emit garbage if the IR is malformed.
            "/* extents: argument is not an array (typechecker bug) */"

    | IRReduce (arrExpr, kernelExpr) ->
        // Inline reduction as an IIFE. Mirrors the genBinding form's loop but
        // wraps it in `[&]() { ... }()` so it can appear in expression context
        // — kernel bodies (lambda(g) -> reduce(g)) and arithmetic
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
        // NOT `.extents[0]` — and it is indexed `g[i]` via its operator[]. Detect
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
        // scope are structurally avoided — each IIFE is its own block.
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
            let guard =
                if reduceNonEmpty then ""
                else sprintf "if (%s == 0) { std::cerr << \"reduce: empty array, no reduction possible\" << std::endl; std::abort(); } " reduceBound
            sprintf "[&]() { %s%s %s __r = %s; for (size_t __ri = 1; __ri < %s; __ri++) { __r = %s(__r, %s); } return __r; }()"
                guard wrapperStr elemStr (reduceAccAt "0") reduceBound wname (reduceAccAt "__ri")
        | _ ->
            "/* reduce: non-callable kernel (typechecker or IR bug) */"

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
        // Generate nested ternary for match expressions
        let scrut = exprToCppCore subst names scrutinee
        let rec genCase (cases: IRMatchCase list) : string =
            match cases with
            | [] -> "([&]() -> double { std::cerr << \"Blade: non-exhaustive match\" << std::endl; std::abort(); return 0; }())"
            | [case] ->
                // Last case - assume it matches (wildcard or variable)
                // But if there's a guard, we must still check it.
                let abortExpr = "([&]() -> double { std::cerr << \"Blade: non-exhaustive match\" << std::endl; std::abort(); return 0; }())"
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
                    // Last variant case — extract payload and evaluate body
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
                    // Last tuple case — bind each element
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
    | IRGuard (cond, body) ->
        // guard(p, c) → p ? c : 0 (type-appropriate zero)
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
        exprError "opaque-extent marker reached expression rendering — kernel-param sub-array was not bound to a concrete extent at the peel point (codegen routing bug)"
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
/// That delegation is deferred — no current test exercises an inline
/// non-2-array combinator, so the cleanup waits for one. Bindings and
/// returns already go through genApplyCombinator, which is where the real
/// machinery lives.
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
    // IRReynolds-wrapped kernels are peeled — at this inline-form
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

    | IRIntersect (aExpr, bExpr) ->
        // SQL INTERSECT: unique values appearing in BOTH arrays, output in
        // first-occurrence order from A. Two-pass with set reuse, mirroring
        // unique() — first pass counts unique A-elements that are also in
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
    | IRUnion (aExpr, bExpr) ->
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
    | IRUnique aExpr ->
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
    | IRSort (arrExpr, keyExpr) ->
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
        // key (returns literal 0 — all elements compare equal,
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
    | IRTranspose (arrExpr, d1, d2) ->
        // Hard transpose: allocate a fresh pool at the SWAPPED extents and copy
        // every element with axes d1/d2 exchanged. The result is an independent
        // array (new pool, new row-pointers) — no aliasing back to the source,
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
                sprintf "Array<%s, %d> %s = { allocate<typename promote<%s, %d>::type, nullptr>(%s), %s };"
                    elemTypeStr rank varName elemTypeStr rank extentsName extentsName
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
    | IRDecompact (arrExpr, dimArg) ->
        // Decompaction = binary group FISSION. decompact(A, d) isolates the
        // logical dimension d of a compact group as a free Idx, cutting on BOTH
        // sides: SymIdx<r,n> -> SymIdx<dPos,n> -> Idx<n> -> SymIdx<r-dPos-1,n>.
        // Edges degenerate to a single cut. Storage is value-equivalent to the
        // source but strictly larger (fission breaks the inter-axis dependency,
        // so each sub-group ranges over the full [0,n) again) — the cost paid to
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
        //   (2) ANTISYMMETRIC rank-2 (fully dissolves to dense n×n): the legacy
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
                // the leading count — NOT the global dim itself. (For the sole-
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
                    sprintf "Array<%s, %d> %s = { allocate<typename promote<%s, %d>::type, %s>(%s), %s };"
                        elemTypeStr totalRank varName elemTypeStr totalRank symmArg extentsName extentsName
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
                            [ sprintf "%sfor (size_t %s = 0; %s < %s; %s++) {" ind v v bound v
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
                let fLine = sprintf "%sfor (size_t %s = 0; %s < %s; %s++) {" fInd fv fv nExpr fv
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
                // ----- Antisym rank-2: fully dissolves to dense n×n -----
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
                // ----- Hermitian rank-2: dissolves to dense n×n -----
                // Source is upper-triangle Hermitian storage (from gram). Walk the
                // INCLUSIVE upper triangle i<=j (diagonal kept — it is real for a
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
                    sprintf "Array<%s, %d> %s = { allocate_strict<typename promote<%s, %d>::type, %s, %s>(%s), %s };"
                        elemTypeStr r varName elemTypeStr r symmArg strictArg extentsName extentsName
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
                                [ sprintf "%sfor (size_t %s = 0; %s < %s; %s++) {" ind v v bound v
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
                            [ sprintf "%sfor (size_t %s = 0; %s < %s; %s++) {" ind v v nExpr v ]
                        storeSubs <- storeSubs + sprintf "[%s]" v
                        logTuple <- logTuple @ [v]
                        depth <- depth + 1
                let bodyInd = String.replicate depth "    "
                let arrInit = logTuple |> String.concat ", "
                // Source read: the source is the rank-r strict antisym storage; the
                // canonical value lives at the strict left-justified position of the
                // SORTED logical tuple. canon_fold sorts __dc_a in place (strict) and
                // yields parity + zero flag (repeat ⇒ antisym 0).
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
    | IRArrayNegate arrExpr | IRArrayConjugate arrExpr ->
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
    | IRGram (lExpr, rExpr, sameArray) ->
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
    | _ -> None

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
/// directly in the predicate — wherever Phase B's bottom-up walk would
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
    | VirtualRange _ when (match level.Extent with IRCompoundMask _ -> true | _ -> false) ->
        // range<CompoundIdx<m>>: ONE loop level over the present cells; each
        // kernel param binds one COORDINATE of the current cell's tuple via
        // the materialized index's O(1) unhash (rank_to_tuple lookup). The
        // index variable `<name>_cidx` is emitted by the caller's
        // materialization step (genCompoundIndexFromMask) before the nest.
        // Offsets are not meaningful on a masked product space; TypeCheck
        // never produces one for a compound range slot.
        let code = sprintf "size_t %s = %s_cidx->unhash(%s)[%d];"
                           elem.ParamName elem.ArrayName level.IndexName elem.RankComponent
        (code, elem.ParamName)
    | VirtualRange offset ->
        // range<I>: kernel param gets the loop index, plus offset if present
        let valueExpr =
            match offset with
            | None -> level.IndexName
            | Some (IRLit (IRLitInt n)) -> sprintf "(%s + %dL)" level.IndexName n
            | Some off -> sprintf "(%s + %s)" level.IndexName (exprToCpp Map.empty off)
        let code = sprintf "size_t %s = %s;" elem.ParamName valueExpr
        (code, elem.ParamName)
    | VirtualReverse ->
        // reverse<I>: kernel param gets (extent - 1 - i)
        let extentStr =
            match level.Extent with
            | IRLit (IRLitInt n) -> sprintf "%d" n
            | _ -> sprintf "%s.extents[%d]" elem.ArrayName elem.DimIndex
        let code = sprintf "size_t %s = (%s - 1 - %s);" elem.ParamName extentStr level.IndexName
        (code, elem.ParamName)
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
///   — no code of any kind between the loop headers. This is in direct tension
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
/// in the bindings — no index-type tag is consulted directly, so new index
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
            // boundary, streaming stage), add that exclusion HERE — e.g.
            //   isRectangular b && not b.HasInterLevelInjection
            // and the collapse prefix below will correctly stop before it.
            let collapseEligible (b: LoopIndexBinding) =
                isRectangular b
            // Collapse depth = length of the leading prefix that is BOTH
            // rectangular and collapse-eligible. (takeWhile stops at the first
            // level failing either condition — that is the gate doing its job.)
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

let genForLoopHeader (compoundArrays: Set<string>) (binding: LoopIndexBinding) : string =
    let extentStr = 
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
        | _ -> sprintf "%s.extents[%d]" binding.ExtentArrayRef binding.ExtentDimRef
    
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
/// Generate all permutations of a list of integers
let rec permutations (items: int list) : int list list =
    match items with
    | [] -> [[]]
    | _ ->
        items |> List.collect (fun x ->
            let rest = items |> List.filter (fun i -> i <> x)
            permutations rest |> List.map (fun p -> x :: p))

/// Count inversions to get permutation sign (+1 for even, -1 for odd)

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
/// E.g. (a * b) * c → [a; b; c]
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
        | IRLitFloat f -> sprintf "%g" f
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
        // Combinators, compute, reynolds, etc. — won't appear in kernel bodies.
        // Use unique repr to prevent false dedup.
        sprintf "(opaque %d %A)" (expr.GetHashCode()) (expr.GetType().Name)
let permSign (perm: int list) : int =
    let mutable inv = 0
    for i in 0 .. perm.Length - 2 do
        for j in i + 1 .. perm.Length - 1 do
            if perm.[i] > perm.[j] then inv <- inv + 1
    if inv % 2 = 0 then 1 else -1

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
        let allPerms = permutations [0 .. n - 1]
        let totalPerms = allPerms.Length
        // For each permutation, generate:
        //   - canonical key (for grouping — commutative ops normalized)
        //   - C++ expression (for actual emission)
        //   - sign
        let permData =
            allPerms |> List.map (fun perm ->
                let permNameMap =
                    kernelParams |> List.mapi (fun i p ->
                        (p.VarId, paramCppNames.[perm.[i]]))
                    |> List.fold (fun acc (vid, name) -> Map.add vid name acc) nameMap
                let sign = permSign perm
                let key = canonicalKey permNameMap kernelExpr
                let cppExpr = exprToCpp permNameMap kernelExpr
                (key, sign, cppExpr))
        // Group by canonical key to deduplicate equivalent permutations.
        // For symmetric Reynolds: identical keys accumulate multiplicity.
        // For antisymmetric Reynolds: identical keys accumulate net sign (may cancel to 0).
        let grouped =
            permData
            |> List.groupBy (fun (key, _, _) -> key)
            |> List.choose (fun (_key, group) ->
                let representativeCpp = let (_, _, cpp) = group.Head in cpp
                if isAntisymmetric then
                    let netSign = group |> List.sumBy (fun (_, s, _) -> s)
                    if netSign = 0 then None
                    else Some (netSign, representativeCpp)
                else
                    Some (group.Length, representativeCpp))
        let uniqueTerms = grouped.Length
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
            grouped |> List.mapi (fun i (coeff, expr) ->
                let term = formatTerm coeff expr
                if i = 0 then term
                elif isAntisymmetric && coeff < 0 then
                    sprintf " - %s" (formatTerm (abs coeff) expr)
                else sprintf " + %s" term)
            |> String.concat ""
        let cppExpr =
            if grouped.IsEmpty then
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

let genLoopNest (codeGen: LoopNestCodeGen) (outerNames: Map<int, string>) (indent: int) : string list =
    let ind n = String.replicate n "    "
    let mutable lines = []
    let mutable depth = indent
    
    // Track current peeled name for each array position
    let mutable currentNames : Map<int, string> = 
        codeGen.InputArrayNames |> List.mapi (fun i n -> (i, n)) |> Map.ofList
    
    // Track final peeled name for each param VarId (for kernel body substitution)
    let mutable paramFinalNames : Map<int, string> = Map.empty
    
    // Generate nested loops with element bindings
    // Nest-level OpenMP pragma (collapse for rectangular, dynamic for triangular)
    // is prepended only at the outermost level.
    //
    // OpenMP thread-coverage instrumentation (test mode only): records the set
    // of distinct OpenMP threads that actually executed the outer parallel
    // region, and prints the count afterward. This empirically answers "did the
    // runtime distribute this generated loop across multiple threads?" — the
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
    for binding in codeGen.Bindings do
        // Generate the loop header (pragma only on the outermost loop)
        let pragmaPrefix = if atOuterLevel then genNestPragma codeGen.Bindings (ind depth) else ""
        atOuterLevel <- false
        lines <- lines @ [ind depth + pragmaPrefix + genForLoopHeader compoundArrays binding]
        depth <- depth + 1
        // Thread-coverage marker: record this thread as seen and the team size
        // it observes. Each thread writes ONLY its own slot (race-free). Team
        // size is captured per-slot (not a single guarded write) because
        // schedule(dynamic) does not guarantee any thread runs any iteration —
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
        
        // Generate element bindings for all arrays at this level
        for elem in binding.Elements do
            let currentName = 
                Map.tryFind elem.ArrayPosition currentNames 
                |> Option.defaultValue elem.ArrayName
            let (code, newName) = genElementBindingNew binding elem currentName
            lines <- lines @ [ind depth + code]
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

    let reynoldsResult = genKernelExprWithReynolds codeGen.KernelExpr codeGen.KernelParams codeGen.HasReynolds codeGen.IsAntisymmetric nameMap paramFinalNames
    if codeGen.HasReynolds && reynoldsResult.UniqueTerms < reynoldsResult.TotalPerms then
        lines <- lines @ [ind depth + sprintf "// Reynolds: %d/%d perms unique (dedup %dx)" reynoldsResult.UniqueTerms reynoldsResult.TotalPerms (reynoldsResult.TotalPerms / max 1 reynoldsResult.UniqueTerms)]
    lines <- lines @ [ind depth + sprintf "%s%s %s %s;" codeGen.OutputName outputIdx assignOp reynoldsResult.CppExpr]

    // Close all loops
    for _ in codeGen.Bindings do
        depth <- depth - 1
        lines <- lines @ [ind depth + "}"]

    // [omp-coverage] after the nest: count distinct threads that ran the outer
    // region and print a parseable line. The harness reads "distinct=K" and the
    // available-thread count to decide pass/fail (K>1 when maxth>1 ⇒ genuinely
    // parallel; K==1 with maxth==1 ⇒ correctly serial on a 1-core environment).
    // [omp-coverage] after the nest: report the parallel team size and the
    // number of threads that actually did work. The harness uses:
    //   - teamsz > 1               ⇒ a genuine parallel region was created
    //   - maxth > 1 && teamsz == 1 ⇒ ERROR: pragma not honored (serial region)
    //   - maxth > 1 && teamsz > 1 && distinct == 1 ⇒ WARNING: region parallel
    //                                but scheduler put all work on one thread
    //                                (an allowed scheduler choice, not a bug)
    //   - maxth == 1               ⇒ single-core context, correctly serial
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
// the .cpp file's directory — no -I flag needed, no build-output paths
// leaked into the C++ compile line.

/// Resolve the path of a runtime header file shipped in the cpp/ directory
/// next to the compiler binary. Used by both genRuntimeHeader and
/// genRuntimeArrayTypesHeader; centralized here so the AppContext.BaseDirectory
/// and "cpp" subpath assumptions live in one place.
let private cppRuntimeHeaderPath (filename: string) : string =
    System.IO.Path.Combine(System.AppContext.BaseDirectory, "cpp", filename)

/// Read a Blade C++ runtime header from disk. Fails loudly if the build
/// hasn't copied cpp/ into the output directory — this is a configuration
/// error rather than a compiler bug, so the message points at .fsproj.
let private readCppRuntimeHeader (filename: string) : string =
    let path = cppRuntimeHeaderPath filename
    if not (System.IO.File.Exists path) then
        failwithf
            "C++ runtime header not found at: %s\n\
             The build should copy cpp/%s into the output directory.\n\
             Check that Blade.fsproj contains a <None Include=\"cpp/%s\">\n\
             item with <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>."
            path filename filename
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
      "index_types.h" ]

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
    // codegen — iteration, kernel-param peel, function-param calling
    // convention, print — uses the same machinery.
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
                            let flatValues = allValues |> List.map (sprintf "%g") |> String.concat ", "
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
            let flatValues = allValues |> List.map (sprintf "%g") |> String.concat ", "
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
                  // in scope — it's constructed AFTER this loop runs.
                  sprintf "%s    %s__rows[__ri] = &%s__flat[%s_offsets[__ri]];" ind varName varName varName
                  sprintf "%s}" ind ]
            let wrapperDecl = sprintf "%sRagged<%s> %s = { %s__rows, %s_extents, %s_lens, %s_offsets };" 
                                ind elemType varName varName varName varName varName
            [extentsDecl; lensDecl; offsetsDecl; flatDecl; rowPtrsDecl] @ rowPtrsInit @ [wrapperDecl]
    else
        // Rectangular path: existing behavior.
        let dims = computeArrayDims (IRArrayLit (elements, arrType))
        if dims.IsEmpty then
            [sprintf "%s// Empty array literal" ind]
        else
            // Generate extents declaration
            let extentsValues = dims |> List.map string |> String.concat ", "
            let extentsDecl = sprintf "%sstatic constexpr const size_t %s_extents[%d] = {%s};" 
                                ind varName rank extentsValues
            
            // Generate allocation as Array<T,N> wrapper. Single brace-init
            // bundles the data pointer (from allocate<>) with the extents
            // pointer (the static-constexpr global emitted above).
            let allocDecl = sprintf "%sArray<%s, %d> %s = { allocate<typename promote<%s, %d>::type, nullptr>(%s_extents), %s_extents };" 
                                ind elemType rank varName elemType rank varName varName
            
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
            // Both produce assignments of the form `name[i₀][i₁]...[iₙ₋₁] = E;`.
            //
            // Pattern follows extractLiteralValues / computeArrayDims — recurse
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
                    // Row-major enumeration of (i₀,…,iₙ₋₁) tuples zipped with
                    // the flat value list. extractLiteralValues already walks
                    // in row-major order, so the alignment is exact.
                    let paths = enumerateIndexPaths dims
                    List.zip paths values |> List.map (fun (path, v) ->
                        sprintf "%s%s%s = %g;" ind varName (formatIndexPath path) v)
                else
                    // Per-element path: walk the nested IRArrayLit. Index path
                    // accumulates as we descend; leaves render via exprToCpp.
                    walkLeaves [] (IRArrayLit (elements, arrType))
                    |> List.map (fun (path, leaf) ->
                        sprintf "%s%s%s = %s;" ind varName (formatIndexPath path) (exprToCpp ctx.VarNames leaf))
            
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
    // will produce invalid C++ — that's intentional: such a regression
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
        // IRApp is also included — function calls returning IRTArray emit
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
        let producesWrapper =
            match value with
            | IRFieldAccess _ -> true
            | IRVar _ -> true                // assume wrapper (most producers migrated)
            | IRMask _ | IRSort _ | IRIntersect _ | IRUnion _ | IRUnique _ -> true
            | IRApp _ -> true                // function-call returns wrapped Array
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
/// do NOT participate in this addressing — handled separately, not here. This
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
let genCudaKernelSimplicial (codeGen: LoopNestCodeGen) (name: string) (blockSize: int) : string list option =
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
            for elem in b.Elements do
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
    // in O(log n) rather than the O(n) linear scan — restoring the cost the former
    // closed-form rank-2/3 kernels had, now generalized to arbitrary rank. Total
    // per-thread cost O(r log n).
    //
    // FUTURE O(1) OPTION (deferred until timing tests exist): the unrank has no
    // constant-time closed form — inverting the combinatorial number system is
    // fundamentally a search. To approach O(1) per thread, precompute the
    // card x r table of canonical tuples ONCE (the MethodLoop's S-structure is
    // fixed and reused across kernel applications), store it flat in device
    // memory, and have each thread load idx[pos] = table[t*r + pos] (one coalesced
    // read). This trades O(r log n) arithmetic for a memory gather + one-time table
    // build, amortized over repeated applications. Whether it actually beats the
    // arithmetic depends on r, n, reuse count, and memory-vs-compute balance on the
    // target GPU, so it should be chosen by BENCHMARK, not assumed — GPU arithmetic
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
        [ sprintf "__global__ void %s(%s, %s* __blade_out, size_t __blade_card) {" kernelName kernelParams elemCpp
          "    size_t __blade_i = (size_t)blockIdx.x * blockDim.x + threadIdx.x;"
          "    if (__blade_i >= __blade_card) return;" ]
        @ unrank @ readBinds
        @ [ sprintf "    __blade_out[__blade_i] = %s;" reynolds.CppExpr; "}" ]
    let wrapper =
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
            sprintf "    Array<%s, %d> %s = { allocate_strict<typename promote<%s, %d>::type, %s, %s>(%s), %s };"
                elemCpp r name elemCpp r symmArg strictArg extentsName extentsName
        else
            sprintf "    Array<%s, %d> %s = { allocate<typename promote<%s, %d>::type, %s>(%s), %s };"
                elemCpp r name elemCpp r symmArg extentsName extentsName
    let inlineLines =
        extentDecls
        @ [ allocLine
            sprintf "    %s(pool_base(%s.data), pool_base(%s.data));" launchName srcName name ]
    Some inlineLines

/// Emit a CUDA kernel for the first-kernel scope (rectangular pointwise,
/// boundary-safe scalar elements, single-chunk synchronous). Returns
///   Some inlineLaunchLines  when emitted: the __global__ kernel + its
///     extern "C" launch wrapper are appended to cudaKernelDefsCell (destined
///     for the .cu file); the returned lines are the inline .cpp host code.
///   None  when out of scope (caller falls back to the host loop).
/// Gates: every binding rectangular const-extent RealArray scalar-leaf; array
/// output with boundary-safe elem type; no Reynolds. Only flat T*/size_t cross
/// the extern "C" boundary (pool_base supplies flat host pointers).
let genCudaKernel (codeGen: LoopNestCodeGen) (name: string) (blockSize: int) : string list option =
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
            for elem in b.Elements do
                let cur = Map.tryFind elem.ArrayPosition currentNames |> Option.defaultValue elem.ArrayName
                let newName = sprintf "%s__%s" cur b.IndexName
                let etStr = elemTypeToCpp elem.ArrayElemType
                currentNames <- Map.add elem.ArrayPosition newName currentNames
                paramFinalNames <- Map.add elem.ParamVarId newName paramFinalNames
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
        // which expands to nothing — turning `__out[__i] = ...` into a stray
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
    let wrapper =
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
    // as the symm template arg — not via a function-local static (MSVC C2131).
    let inlineLines =
        [ sprintf "    size_t* %s = new size_t[%d];" extentsName outputRank ]
        @ (bindings |> List.mapi (fun i b ->
              match b.Extent with
              | IRLit (IRLitInt n) -> sprintf "    %s[%d] = %dUL;" extentsName i n
              | _ -> sprintf "    %s[%d] = %s.extents[%d];" extentsName i b.ExtentArrayRef b.ExtentDimRef))
        @ [ sprintf "    Array<%s, %d> %s = { allocate<typename promote<%s, %d>::type, nullptr>(%s), %s };"
                elemCpp outputRank name elemCpp outputRank extentsName extentsName
            sprintf "    %s(%s, pool_base(%s.data));"
                launchName
                // Inputs are Array<T,N> wrappers (array-literal bindings render as
                // `Array<T,1> A = { ... }`), same as the output — so the flat pool
                // is reached via `.data`. (An earlier version dropped `.data` on
                // inputs after a host-shape test that wrongly modeled inputs as
                // bare pointers; the self-contained program uses wrappers.)
                (codeGen.InputArrayNames |> List.map (fun n -> sprintf "pool_base(%s.data)" n) |> String.concat ", ")
                name ]
    Some inlineLines

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
            // (__depidx_inner — runtime layout matches ragged once allocated).
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
                // Resolve "lengths source" — where to read each row's length
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
    
    let raggedResult = tryRaggedPeel ()
    if raggedResult.IsSome then raggedResult.Value
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
        codegenError ctx ind (sprintf "no arrays in method_for for '%s' — kernel cannot be applied" name)
    else
        // Build LoopNestCodeGen (handles both outer product and co-iteration)
        let codeGen = buildLoopNestCodeGen info arrayNames name builder
        
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

        // Branch on output shape. Array output gets the full ceremony
        // (symmetry vector, extents declaration, allocation, then loop nest
        // with indexed assignments). Scalar output gets a single scalar
        // accumulator initialized to zero, then the same loop nest with
        // `+=` accumulation (genLoopNest detects this via codeGen.OutputType).
        // The scalar branch handles the Cartesian-sum-reduce pattern that
        // the old hand-written 2-array IIFE special-cased — now generalized
        // to any number of input arrays through the shared LoopNestCodeGen
        // machinery (commutativity, Reynolds, etc. all carry through).
        match codeGen.OutputType with
        | IRTScalar _ ->
            // Scalar accumulator: declare initialized to 0, then run the
            // loop nest which accumulates into it via genLoopNest's `+=`.
            let scalarDecl = sprintf "%s%s %s = 0;" ind outputElemType name
            let loopCode = genLoopNest codeGen tempCtx.VarNames tempCtx.Indent
            preCode @ [scalarDecl; ""] @ loopCode
        | _ ->
            // CUDA dispatch: if the resolved kernel opted into cuda AND the case
            // is in first-kernel scope, emit a device kernel (+ .cu wrapper) and
            // an inline launch instead of the host loop. genCudaKernel returns
            // None for out-of-scope cases, falling back to the host loop below.
            let cudaInline =
                match resolveKernel info.Kernel with
                | Some rk when rk.Callable.IsCudaKernel && cudaEmitModeEnabled () ->
                    // CUDA emission is gated: it only fires in the dedicated CUDA
                    // phase (which compiles+links the .cu). During ordinary
                    // host-only compilation the flag is off, so the `cuda` clause
                    // stays inert (host fallback) — otherwise the emitted
                    // `extern "C"` launch call would be an undefined symbol at link
                    // time (the .cu isn't built in the host corpus).
                    // Try the symmetric rank-2 triangular path, then the
                    // antisymmetric rank-2 strict-triangular path, then the
                    // rectangular pointwise path; None => host loop.
                    // One general simplicial kernel handles any single S-group of
                    // arity >= 2 (symmetric inclusive / antisymmetric strict, any
                    // rank); then the rectangular pointwise path; None => host loop.
                    genCudaKernelSimplicial codeGen name rk.Callable.CudaBlockSize
                    |> Option.orElseWith (fun () -> genCudaKernel codeGen name rk.Callable.CudaBlockSize)
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
            // (error C2131, "unevaluable pointer value") — even when the value is
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
                    | _ ->
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

            // Generate loop nest
            let loopCode = genLoopNest codeGen tempCtx.VarNames tempCtx.Indent

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
                preCode @ [""; compDecl; ""] @ loopCode
            | _ ->
                preCode @ [""; extentsDecl] @ extentsFill @ [""; allocDecl; ""] @ loopCode

/// Generate a fused loop nest with multiple kernel assignments.
/// All kernels share the same loop structure (bindings), only differ in kernel expr and output.
/// Each extra kernel tuple: (outName, kernelExpr, params, captures, hasReynolds, isAntisymmetric)
let genFusedLoopNest (codeGen: LoopNestCodeGen) (extraKernels: (string * IRExpr * IRParam list * CaptureInfo list * bool * bool) list) (outerNames: Map<int, string>) (indent: int) : string list =
    let ind n = String.replicate n "    "
    let mutable lines = []
    let mutable depth = indent
    
    // Track current peeled name for each array position
    let mutable currentNames : Map<int, string> = 
        codeGen.InputArrayNames |> List.mapi (fun i n -> (i, n)) |> Map.ofList
    let mutable paramFinalNames : Map<int, string> = Map.empty
    
    // Generate nested loops with element bindings (same as genLoopNest)
    // Nest-level OpenMP pragma prepended only at the outermost level.
    let mutable atOuterLevel = true
    let compoundArrays = compoundArrayNamesOf codeGen.Bindings
    for binding in codeGen.Bindings do
        let pragmaPrefix = if atOuterLevel then genNestPragma codeGen.Bindings (ind depth) else ""
        atOuterLevel <- false
        lines <- lines @ [ind depth + pragmaPrefix + genForLoopHeader compoundArrays binding]
        depth <- depth + 1
        for elem in binding.Elements do
            let currentName = 
                Map.tryFind elem.ArrayPosition currentNames 
                |> Option.defaultValue elem.ArrayName
            let (code, newName) = genElementBindingNew binding elem currentName
            lines <- lines @ [ind depth + code]
            currentNames <- Map.add elem.ArrayPosition newName currentNames
            match elem.Virtual with
            | VirtualRange _ | VirtualReverse ->
                paramFinalNames <- Map.add elem.ParamVarId elem.ParamName paramFinalNames
            | RealArray ->
                paramFinalNames <- Map.add elem.ParamVarId newName paramFinalNames
    
    // Output index expression (shared by all kernels)
    let outputIdx = 
        codeGen.Bindings 
        |> List.map (fun b -> sprintf "[%s]" b.IndexName)
        |> String.concat ""
    
    // First kernel assignment (uses primary LoopNestCodeGen's Reynolds info)
    let nameMap0 = paramFinalNames |> Map.fold (fun acc k v -> Map.add k v acc) outerNames
    let nameMap0 = codeGen.Captures |> List.fold (fun acc c -> Map.add c.Id c.Name acc) nameMap0
    let reynoldsResult0 = genKernelExprWithReynolds codeGen.KernelExpr codeGen.KernelParams codeGen.HasReynolds codeGen.IsAntisymmetric nameMap0 paramFinalNames
    if codeGen.HasReynolds && reynoldsResult0.UniqueTerms < reynoldsResult0.TotalPerms then
        lines <- lines @ [ind depth + sprintf "// Reynolds: %d/%d perms unique (dedup %dx)" reynoldsResult0.UniqueTerms reynoldsResult0.TotalPerms (reynoldsResult0.TotalPerms / max 1 reynoldsResult0.UniqueTerms)]
    lines <- lines @ [ind depth + sprintf "%s%s = %s;" codeGen.OutputName outputIdx reynoldsResult0.CppExpr]
    
    // Additional kernel assignments
    // Each extra kernel has its own param VarIds that need mapping to the same peeled names.
    // We bridge via position: primary param[i].VarId → peeledName, extra param[i].VarId → same peeledName.
    for (outName, kernelExpr, extraParams, captures, hasReynolds, isAntisym) in extraKernels do
        let extraParamFinalNames =
            extraParams |> List.mapi (fun i p ->
                match codeGen.KernelParams |> List.tryItem i with
                | Some primaryParam ->
                    match Map.tryFind primaryParam.VarId paramFinalNames with
                    | Some peeledName -> Some (p.VarId, peeledName)
                    | None -> None
                | None -> None)
            |> List.choose id
            |> Map.ofList
        let nameMap = extraParamFinalNames |> Map.fold (fun acc k v -> Map.add k v acc) outerNames
        let nameMap = captures |> List.fold (fun acc c -> Map.add c.Id c.Name acc) nameMap
        let reynoldsResult = genKernelExprWithReynolds kernelExpr extraParams hasReynolds isAntisym nameMap extraParamFinalNames
        if hasReynolds && reynoldsResult.UniqueTerms < reynoldsResult.TotalPerms then
            lines <- lines @ [ind depth + sprintf "// Reynolds: %d/%d perms unique (dedup %dx)" reynoldsResult.UniqueTerms reynoldsResult.TotalPerms (reynoldsResult.TotalPerms / max 1 reynoldsResult.UniqueTerms)]
        lines <- lines @ [ind depth + sprintf "%s%s = %s;" outName outputIdx reynoldsResult.CppExpr]
    
    // Close all loops
    for _ in codeGen.Bindings do
        depth <- depth - 1
        lines <- lines @ [ind depth + "}"]
    
    lines


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
    // bodies invoke the wrapper with the per-iteration array slots —
    // eliminating the need for intermediate scalar locals.
    match resolveCallable objInfo.Kernel with
    | Some callable when callable.Params.Length = 1 || callable.Params.Length = 2 ->
        let (wrapperCode, wname) = genCallableWrapper name callable
        // Output element type is the kernel's RETURN type. Comparison/logical
        // kernels (`A < B`, `above2 && below5`) consume numeric or bool inputs
        // but PRODUCE bool, so the result array must be Array<bool> even when the
        // inputs are double. For every other kernel the return type matches the
        // inputs, so fall back to the historical param-based inference to avoid
        // disturbing the arithmetic and user-kernel paths.
        let elemTypeStr =
            match callable.RetType with
            | IRTScalar ETBool -> "bool"
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
/// e.g., IRLet(id1, v1, IRLet(id2, v2, body)) → statements=[(id1,v1), (id2,v2)], return=body
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
/// Does NOT register `name` in the returned context as a binding — that
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
                sprintf "%sArray<%s, %d> %s = { allocate<typename promote<%s, %d>::type, nullptr>(%s_extents), %s_extents };" ind elemType arrRank s1Name elemType arrRank s1Name s1Name
                sprintf "%sfor (size_t __i0 = 0; __i0 < %s.extents[0]; __i0++) {" ind arrName
                sprintf "%s    %s[__i0] = %s(%s[__i0]);" ind s1Name k1 arrName
                sprintf "%s}" ind
            ]
            let s2Code = [
                sprintf "%sconst size_t* %s_extents = %s.extents;" ind name s1Name
                sprintf "%sArray<%s, %d> %s = { allocate<typename promote<%s, %d>::type, nullptr>(%s_extents), %s_extents };" ind elemType arrRank name elemType arrRank name name
                sprintf "%sfor (size_t __i0 = 0; __i0 < %s.extents[0]; __i0++) {" ind s1Name
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

/// Recursively generate code for a parallel composition tree (<&>).
/// Each leaf IRApplyCombinator gets its own independent loop nest.
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
        // Single leaf — generate directly, no tuple wrapping
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
        // Multiple leaves — generate each, assemble flat tuple
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
        // Not all leaves are ApplyCombinators — fall back to parallel generation
        genParallelTree ctx name expr builder
    else
        // Generate output names for each leaf
        let leafNames = infos |> List.mapi (fun i _ -> sprintf "%s_%d" name i)
        
        // Use first info for loop structure
        let primaryInfo = infos.[0]
        let primaryName = leafNames.[0]
        
        // Extract array names from the shared loop
        let arrayNames = 
            primaryInfo.Arrays |> List.mapi (fun i arr ->
                match arr with
                | IRVar (id, _) -> 
                    Map.tryFind id ctx.VarNames |> Option.defaultValue (sprintf "arr%d" i)
                | IRRange _ -> sprintf "__range%d" i
                | IRVirtualReverse _ -> sprintf "__rev%d" i
                | IRBlocked _ -> sprintf "__blk%d" i
                | _ -> sprintf "arr%d" i)
        
        if arrayNames.IsEmpty then
            (codegenError ctx ind (sprintf "no arrays in method_for for parallel/sequence '%s'" name), name, Map.empty)
        elif primaryInfo.Arrays |> List.exists (fun a ->
                 match a with
                 | IRRange (its, _) -> its |> List.exists (fun ix -> ix.IxKind = IxKCompound)
                 | _ -> false) then
            // The fused multi-output path allocates outputs through the dense
            // extents machinery and does not materialize a standalone
            // compound_index_t; a compound range here would emit references to
            // an undeclared `__rangeN_cidx`. Fail loudly with a diagnosis
            // instead of a confusing C++ compile error.
            (codegenError ctx ind "range<CompoundIdx> is not yet supported in parallel/fused (multi-output) loop applications; use a single-kernel method_for", name, Map.empty)
        else
            // Build primary LoopNestCodeGen
            let codeGenPrimary = buildLoopNestCodeGen primaryInfo arrayNames primaryName builder
            
            // Extract kernel info for all additional leaves via
            // `resolveKernel`, which peels any `IRReynolds` wrapper and
            // resolves the inner callable through the CallablesTable +
            // synthetic registry.
            let extraKernels = 
                (List.tail infos, List.tail leafNames) ||> List.map2 (fun info outName ->
                    let (kParams, kBody, captures, hasReynolds, isAntisym) =
                        match resolveKernel info.Kernel with
                        | Some rk ->
                            (rk.Callable.Params, rk.Callable.Body, rk.Callable.Captures,
                             rk.Reynolds.HasReynolds, rk.Reynolds.IsAntisymmetric)
                        | None -> ([], IRLit IRLitUnit, [], false, false)
                    (outName, kBody, kParams, captures, hasReynolds, isAntisym))
            
            // Generate symm vec + extents + allocation for each output
            let allCodeGens = infos |> List.mapi (fun i info ->
                buildLoopNestCodeGen info arrayNames leafNames.[i] builder)
            
            let declCode = allCodeGens |> List.mapi (fun i cg ->
                let lname = leafNames.[i]
                let symmVecName = sprintf "%s_symm" lname
                // Pass nullptr DIRECTLY when there's no symmetry — a function-local
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
                        | _ -> sprintf "%s%s[%d] = %s.extents[%d];" ind extentsName j b.ExtentArrayRef b.ExtentDimRef)
                let allocRhs =
                    match emitAllocRhs (classifyOutputStorage cg.OutputType)
                              outputElemType outputRank symmArg extentsName with
                    | Ok rhs -> rhs
                    | Error msg -> sprintf "{ nullptr, %s };\n#error \"%s\"" extentsName msg
                let allocDecl = sprintf "%sArray<%s, %d> %s = %s;" 
                                    ind outputElemType outputRank lname allocRhs
                [extentsDecl] @ extentsFill @ [allocDecl]) |> List.concat
            
            // Generate single fused loop nest
            let loopCode = genFusedLoopNest codeGenPrimary extraKernels ctx.VarNames ctx.Indent
            
            // Build flat tuple from leaf names
            let tupleLine = sprintf "%sauto %s = std::make_tuple(%s);" ind name (leafNames |> String.concat ", ")
            let childrenMap = Map.ofList [name, leafNames]
            
            (declCode @ [""] @ loopCode @ [""] @ [tupleLine], name, childrenMap)

/// Compute the number of flat leaves for a type (recursing into nested tuples).
let rec tupleLeafCount (ty: IRType) : int =
    match ty with
    | IRTTuple ts -> ts |> List.sumBy tupleLeafCount
    | _ -> 1

/// For a tuple type, compute the flat child range [start, start+count) for each top-level element.
/// E.g. ((α,β), γ) → [(0, 2); (2, 1)] meaning element 0 spans flat indices 0..1, element 1 is flat index 2.
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
/// dispatcher and every per-shape generator in its recursive chain (§2.5).
let bindingCppName (binding: IRBinding) : string =
    if binding.Name = "_" then sprintf "__tup_%d" binding.Id else binding.Name

/// Generate C++ code for an IR binding: the DISPATCHER (audit §2.5).
/// Each binding shape's emission lives in its own named `genXxxBinding`
/// generator below (same `let rec ... and` chain), so every path is
/// independently findable and testable; this match only destructures the
/// shape and delegates. Arms not yet extracted retain their inline bodies —
/// the migration is one generator at a time, gated by the full suite.
let rec genBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding

    match binding.Value with
    | _ when Map.containsKey binding.Id ctx.ProviderReads ->
        genProviderReadBinding ctx binding builder 
    | _ when Map.containsKey binding.Id ctx.RandomInits ->
        genRandomInitBinding ctx binding builder 
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
    | IRReduce (arrExpr, kernelExpr) ->
        genReduceBinding ctx binding builder arrExpr kernelExpr
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
    
    | IRStructLit (typeName, _) ->
        // Struct construction - emit binding then validate() if constrained
        let code = genScalarBinding ctx name binding.Value binding.Type
        let validateCode =
            if ctx.ConstrainedStructs.Contains typeName then
                [sprintf "%s%s.validate();" ind name]
            else []
        let ctx' = addVarName binding.Id name ctx
        (code @ validateCode, ctx')
    
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
        // All elements are computations — defer the whole tuple
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
    //   Case 1 — positional buckets (Idx<N> keys): ngroups known at
    //     compile time, keys are integer bucket indices in [0, N).
    //     Stack-allocated counts/offsets/fill (sized at compile time).
    //   Case 2 — EnumIdx reverse lookup: ngroups known at compile
    //     time, plus an explicit list of admissible key values (ints
    //     or strings). Emits a __bucket(__v) lambda that maps each
    //     key to its position in the values list.
    //   Case 3 — dynamic discovery: ngroups not known at compile
    //     time. Builds key → bucket-index map (std::unordered_map)
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
    // The `<name>` binding itself is a void* sentinel — gk's state
    // lives in the suffix-named symbols above, not in a single C++
    // value. Downstream consumers read those symbols by name.
    //
    // Compound (multi-key) mode: when `keys` has length >1, the
    // dispatch becomes an unordered_map<std::tuple<...>, size_t>
    // keyed by the tuple of component values. Each unique tuple
    // discovered in the input becomes its own bucket. The C++ ABI
    // (__ngroups, __offsets, __perm) is identical to the single-key
    // dynamic case — downstream consumers don't need to know whether
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
                // Case 3 — dynamic ngroups via hash discovery. Builds a key →
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
                // Case 2: EnumIdx — keys are arbitrary integers OR strings,
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
                        | IR.EVInt n -> sprintf "%dLL" n
                        | IR.EVString s -> escapeStringLit s
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
        // a normal CSR structure — they don't need to know that the
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
            // a double-wrapped IRCompute(IRCompute(IRApplyCombinator)) —
            // produced when the bind expansion explicitly wraps an
            // already-wrapped continuation body — would fall through to
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
    // f <$> (L <@> g) → L <@> (f ∘ g)
    // Wraps kernel body: λparams → f(g(params))
    let applyFunctorWrappers (info: ApplyInfo) (wrappers: IRExpr list) : ApplyInfo =
        if wrappers.IsEmpty then info
        else
            // Beta-reduce: substitute wrapper's parameter with inner body
            // f <$> (L <@> g) where f = λx → h(x)
            // becomes L <@> λparams → h(g(params))
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
        // composed-object chain here — those route through the
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
        // @>> : sequential composition — compute left, feed result to right's kernel
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
            // Right kernel is a named function — generate element-wise function-call loop
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
            // Right kernel is inline lambda — use buildSimpleApplyInfo path
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
            // Fallback: continuation not resolvable to callable —
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
        // If functor wrappers present: f <$> (c1 <|> c2) ≡ (f <$> c1) <|> (f <$> c2)
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
        // bindings (rank=0 ⇒ scalar). Both cases get their elem type
        // from the binding's resolved type. A type that's neither
        // ArrayElem nor IRTScalar at this point is an upstream
        // typechecker bug.
        let (elemType, elemTypeErrCode) =
            match binding.Type with
            | ArrayElem arr -> (elemTypeToCpp arr.ElemType, [])
            | IRTScalar et -> (primTypeToCpp et, [])
            | t ->
                (elemTypeToCpp (IRTScalar ETFloat64),
                 codegenError ctx ind (sprintf "<|>: binding type is neither array nor scalar (got %A) — likely a typechecker or IR bug" t))
        
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
            // hoisted symm name, which isn't threaded here — out of scope and
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
    
    | IRGuard (cond, body) ->
        // guard(p, c) |> compute: conditionally execute computation
        // Strategy: wrap the kernel body with the guard condition
        // guard(cond, L <@> f) → L <@> (λargs → cond ? f(args) : 0)
        // This allocates the array always but fills with zeros when false
        let isComputation =
            match body with
            | IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ -> true
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
                // Wrap kernel: λparams → cond ? kernel_body : 0
                //
                // Resolves the kernel through resolveCallable and
                // routes through the synthetic registry: a fresh
                // callable with a new builder-allocated id holds
                // the conditional-wrapped body, gets registered,
                // and is referenced via IRVar. The original
                // callable in module.Functions is unchanged — the
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
                // Non-apply computation (parallel, fusion, etc.) — fall back to scalar guard
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
    // Deferred provider read materialized at the `|> read` force point
    // (approach (b)): emit the NetCDF reader producing `name`. A compound
    // (load_compound) read carries a mask; a plain dense read does not.
    let spec = ctx.ProviderReads.[binding.Id]
    let readCode =
        (match spec.MaskName, spec.MaskType with
         | Some maskName, Some maskType ->
             Blade.NetcdfProvider.CppNetcdf.genReadCompoundVar spec.FilePath spec.VarName maskName name spec.VarType maskType
         | _ ->
             Blade.NetcdfProvider.CppNetcdf.genReadVar spec.FilePath spec.VarName name spec.VarType)
    (readCode |> List.map (fun s -> ind + s), addVarName binding.Id name ctx)


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
    let modExpr = ctx.RandomInits.[binding.Id]
    (match binding.Type with
     | ArrayElem arrTy ->
         let rank = arrayRank arrTy
         let elemCpp = elemTypeToCpp arrTy.ElemType
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
                 sprintf "%s{ size_t %s_r = 0; for (size_t %s_c = 0; %s_c < %s_grid; %s_c++) if (%s_maskvec[%s_c]) { for (size_t %s_t = 0; %s_t < %s_trail; %s_t++) %s_compact[%s_r * %s_trail + %s_t] = %s_densepool[%s_c * %s_trail + %s_t]; %s_r++; } }"
                         ind name name name idxName name idxName name name name name name name name name name name name name name name
                 sprintf "%snested_array_utilities::Compound<%s, %d> %s { %s_compact, %s, %s_trail };" ind elemCpp leadRank name name idxName name ]
         (lines, addVarName binding.Id name ctx)
     | _ ->
         ([sprintf "%s#error \"compound() binding '%s' is not a CompoundIdx array type\"" ind name], addVarName binding.Id name ctx))


and genMaskBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (arrExpr: IRExpr) (predExpr: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // mask(array, pred): eager compaction — scan, count, allocate, fill.
    // Strict elem-type inference (emits #error if unresolvable) and
    // predicate-callable validation happen here at the call site; the
    // shared `materializeInlineForm` helper just emits the C++ template.
    //
    // Validation accepts any predicate that resolves to a
    // single-parameter callable through resolveCallable.
    let (elemET, elemErrCode) = inferElemTypeStrict ctx ind arrExpr "mask"
    let elemStr = elemTypeToCpp elemET
    let predErrCode =
        match resolveCallable predExpr with
        | Some callable when callable.Params.Length = 1 -> []
        | _ -> codegenError ctx ind "mask: predicate must resolve to a single-parameter callable; got something else (typechecker or IR bug)"
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name elemStr binding.Value with
        | Some s -> s
        | None -> []  // Unreachable: helper supports IRMask
    let code = elemErrCode @ predErrCode @ [sprintf "%s// mask: count + compact" ind] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genIntersectBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (aExpr: IRExpr) (bExpr: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // intersect(A, B): elements present in both arrays.
    let (elemET, elemErrCode) = inferElemTypeStrict ctx ind aExpr "intersect"
    let elemStr = elemTypeToCpp elemET
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name elemStr binding.Value with
        | Some s -> s
        | None -> []
    let code = elemErrCode @ [sprintf "%s// intersect: build set from B, scan A" ind] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genUnionBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (aExpr: IRExpr) (bExpr: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // union(A, B): all elements from A, plus elements from B not in A.
    let (elemET, elemErrCode) = inferElemTypeStrict ctx ind aExpr "union"
    let elemStr = elemTypeToCpp elemET
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name elemStr binding.Value with
        | Some s -> s
        | None -> []
    let code = elemErrCode @ [sprintf "%s// union: all of A, plus elements from B not in A" ind] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genUniqueBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (arrExpr: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // unique(A): dedup, preserving first-occurrence order. Two-pass:
    // first counts unique elements via std::unordered_set, then fills
    // the output array on a second pass (clearing the set in between
    // so first-occurrence membership testing repeats identically).
    let (elemET, elemErrCode) = inferElemTypeStrict ctx ind arrExpr "unique"
    let elemStr = elemTypeToCpp elemET
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name elemStr binding.Value with
        | Some s -> s
        | None -> []
    let code = elemErrCode @ [sprintf "%s// unique: dedup via unordered_set, first-occurrence order" ind] @ (matStmts |> List.map (fun s -> ind + s))
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
    // Outer extent = gk__ngroups; inner is ragged. Track grouped → gk so
    // future ragged-aware iteration can recover offsets.
    //
    // The outer pointer-array is wrapped in Array<T*, 1>. The wrapper's
    // .extents points at the 2-element local size_t array {ngroups, 0};
    // .extents[0] = ngroups, .extents[1] reads 0 (placeholder for the
    // ragged inner). Element type T* keeps `grouped[g]` as a bare row
    // pointer for downstream peeling. Print's inner-loop bound of 0
    // means no values printed, matching prior behavior.
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
    (code, ctx')



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
        match materializeInlineForm emptySubst ctx.VarNames name elemStr binding.Value with
        | Some s -> s
        | None -> []
    let code = elemErrCode @ keyErrCode @ [sprintf "%s// sort: stable_sort on permutation, eager materialization" ind] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genTransposeBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (arrExpr: IRExpr) (d1: int) (d2: int) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // transpose(array, [d1, d2]): hard transpose — allocate a fresh pool at
    // the swapped extents and copy with axes d1/d2 exchanged. Eager
    // materialization (same phase-1 strategy as sort); the result is an
    // independent array with no aliasing back to the source. TypeCheck has
    // already verified both axes are arity-1 SymNone and in range.
    let (elemET, elemErrCode) = inferElemTypeStrict ctx ind arrExpr "transpose"
    let elemStr = elemTypeToCpp elemET
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name elemStr binding.Value with
        | Some s -> s
        | None -> []
    let code = elemErrCode @ [sprintf "%s// transpose: hard (swapped-extent alloc + axis-swapped copy)" ind] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genDecompactBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (arrExpr: IRExpr) (d: int) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // decompact(array, d): pull the compact component at dim d out as a
    // free Idx. Hard materialization — allocate a fresh dense pool and
    // scatter the canonical (triangular-packed) source elements into all
    // of the decompacted component's image positions, applying the per-
    // class transform (Sym copy / Antisym sign + zero diagonal / Hermitian
    // conj). TypeCheck has verified dim d targets a compact slot and that
    // the Antisym middle-peel case is excluded.
    let (elemET, elemErrCode) = inferElemTypeStrict ctx ind arrExpr "decompact"
    let elemStr = elemTypeToCpp elemET
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name elemStr binding.Value with
        | Some s -> s
        | None -> []
    let code = elemErrCode @ [sprintf "%s// decompact: hard (dense alloc + symmetry-expanding scatter)" ind] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genArrayNegateConjugateBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (arrExpr: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Whole-array eager negate/conjugate (the cheap intra-group transposes).
    // Type-preserving: same-shape alloc + flat contiguous-pool transform.
    let isConj = (match binding.Value with IRArrayConjugate _ -> true | _ -> false)
    let label = if isConj then "conjugate" else "negate"
    let (elemET, elemErrCode) = inferElemTypeStrict ctx ind arrExpr (sprintf "array_%s" label)
    let elemStr = elemTypeToCpp elemET
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name elemStr binding.Value with
        | Some s -> s
        | None -> []
    let code = elemErrCode @ [sprintf "%s// array_%s: whole-array eager transform (same-shape alloc + pool loop)" ind label] @ (matStmts |> List.map (fun s -> ind + s))
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



and genReduceBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (arrExpr: IRExpr) (kernelExpr: IRExpr) : string list * CodeGenContext =
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
        if isStaticallyNonEmpty then []
        else [
            sprintf "%s// reduce: dynamic extent — runtime non-emptiness guard" ind
            sprintf "%sif (%s == 0) { std::cerr << \"reduce: empty array, no reduction possible\" << std::endl; std::abort(); }" ind boundExpr
        ]

    let code =
        match resolveCallable kernelExpr with
        | Some callable when callable.Params.Length = 2 ->
            // Stage 3c.2: wrapper-based emission. The fold's
            // accumulator and the wrapper agree on type — both come
            // from the array's elem type via the inferReduce
            // unification — so the call `__r = __wrap(__r, arr[i])`
            // type-checks without narrowing/conversion warnings.
            let (wrapperCode, wname) = genCallableWrapper name callable
            let wrapperLines = wrapperCode |> List.map (fun s -> ind + s)
            elemErrCode @ guardLines @ wrapperLines @ [
                sprintf "%s// reduce: accumulator loop, eager" ind
                sprintf "%s%s %s = %s;" ind elemStr name (elemAt "0")
                sprintf "%sfor (size_t __ri = 1; __ri < %s; __ri++) {" ind boundExpr
                sprintf "%s    %s = %s(%s, %s);" ind name wname name (elemAt "__ri")
                sprintf "%s}" ind
            ]
        | _ ->
            let errLines = codegenError ctx ind "reduce: kernel must resolve to a binary callable (typechecker or IR bug if not)"
            elemErrCode @ errLines
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genTupleProjBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (parentExpr: IRExpr) (projIdx: int) (isFlat: bool) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Check if parent is a deferred computation tuple — if so, project and defer
    let parentDeferred =
        match parentExpr with
        | IRVar (pid, _) -> Map.tryFind pid ctx.DeferredComputations
        | _ -> None
    match parentDeferred with
    | Some (IRTuple elems) when projIdx < elems.Length ->
        // Parent is a deferred tuple — project out the element and defer it
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id elems.[projIdx] ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred computation (tuple proj)>" ind name], ctx')
    | Some (IRParallel _ | IRFusion _) ->
        // Parent is a deferred combinator — defer the projection too
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred computation (proj of combinator)>" ind name], ctx')
    | _ ->
        // Tuple projection — resolve through TupleChildren map
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
                // No TupleChildren — fall back to std::get
                let code = genScalarBinding ctx name binding.Value binding.Type
                let ctx' = addVarName binding.Id name ctx
                (code, ctx')



and genVarAliasBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (srcId: IRId) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Check if source is deferred — propagate deferral
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
        // name `f` so direct calls `f(args)` work — the wrapper's
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
        // body is a trivial forwarding call — the compiler infers
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
            // function's capture slot — the receiving function takes
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
            // Plain variable alias — may be aliasing a tuple, propagate children
            let srcName = Map.tryFind srcId ctx.VarNames |> Option.defaultValue ""
            let hasTupleChildren = Map.containsKey srcName ctx.TupleChildren
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
    let isCompExpr e = match e with IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ | IRComposeObj _ | IRComposeMeth _ | IRBind _ | IRGuard _ | IRSequence _ -> true | IRVar _ -> true | _ -> false
    if isCompExpr left || isCompExpr right then
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred choice>" ind name], ctx')
    else
        // Scalar choice: generate directly
        let code = genScalarBinding ctx name binding.Value binding.Type
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')



and genGuardBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (body: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Guard wrapping a computation: defer for later materialization via |> compute
    // Recurse through nested guards to check if the leaf body is a computation
    let rec leafIsComputation e =
        match e with
        | IRGuard (_, inner) -> leafIsComputation inner
        | IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ | IRComposeObj _ | IRComposeMeth _ | IRBind _ | IRSequence _ -> true
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
    let isCompExpr e = match e with IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ | IRComposeObj _ | IRComposeMeth _ | IRBind _ | IRGuard _ | IRSequence _ -> true | IRVar _ -> true | _ -> false
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
        [sprintf "%sfor (size_t %s = %s; %s < %s; %s++) {" ind varName loStr varName hiStr varName]
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
            let loopVar = sprintf "__k%d" vid
            let loStr = exprToCpp currentNames lo
            let hiStr = exprToCpp currentNames hi
            let innerNames = Map.add vid loopVar currentNames
            let (bodyLets, _) = deepUnroll forBody
            let mutable bodyNames = innerNames
            let bodyStmts = bodyLets |> List.collect (fun (bid, bval) ->
                let bName = sprintf "__v%d" bid
                match bval with
                | IRAssign (target, v) ->
                    let targetStr =
                        match target with
                        | LVVar tid -> Map.tryFind tid bodyNames |> Option.defaultValue (sprintf "__v%d" tid)
                        | _ -> exprToCpp bodyNames target
                    bodyNames <- Map.add bid bName bodyNames
                    [sprintf "%s    %s = %s;" indent targetStr (exprToCpp bodyNames v)]
                | _ ->
                    let valStr = exprToCpp bodyNames bval
                    bodyNames <- Map.add bid bName bodyNames
                    [sprintf "%s    auto %s = %s;" indent bName valStr])
            currentNames <- Map.add id varName currentNames
            [sprintf "%sfor (size_t %s = %s; %s < %s; %s++) {" indent loopVar loStr loopVar hiStr loopVar]
            @ bodyStmts
            @ [sprintf "%s}" indent]
        | IRAssign (target, v) ->
            let targetStr =
                match target with
                | LVVar tid -> Map.tryFind tid currentNames |> Option.defaultValue (sprintf "__v%d" tid)
                | _ -> exprToCpp currentNames target
            currentNames <- Map.add id varName currentNames
            [sprintf "%s%s = %s;" indent targetStr (exprToCpp currentNames v)]
        | IRLit IRLitUnit ->
            // Skip unit literals (side effects already emitted)
            currentNames <- Map.add id varName currentNames
            []
        | IRMethodFor _ | IRObjectFor _ ->
            // Loop objects are compile-time only — they're resolved when <@> is processed
            currentNames <- Map.add id varName currentNames
            []
        | IRApplyCombinator _ | IRComposeApply _ ->
            // Unevaluated computations — deferred until |> compute forces them
            currentNames <- Map.add id varName currentNames
            []
        | IRCompute (IRApplyCombinator info) ->
            // Function-body let-binding of `method_for(...) <@> kernel |> compute`.
            // Use the statement-form genApplyCombinator, which emits the full
            // sequence (extents declaration, allocation, loop nest) with
            // `varName_extents` etc. as proper C++ identifiers — so any
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
        // through the inline expression-form genApplyCombinatorExpr — which
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
    // for separate body-level aliases — the wrapper IS the binding.
    let paramStr (name: string) (ty: IRType) : string =
        match ty with
        | ArrayElem arr -> sprintf "%s %s" (cppArrayTypeStr arr) name
        | _ -> sprintf "%s %s" (irTypeToCpp ty) name
    let captureParamStr (cap: CaptureInfo) : string =
        // Captures are appended after the regular params. Pass-by-reference
        // so mutation propagates and the captures' lifetimes are tied to
        // the wrapper's `[&]` capture at the use site (Stage 3c.1).
        // `T&` for plain types, `Array<T, N>&` for arrays — the wrapper
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
        // function values — fine because function values are immutable
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
    let code =
        [sprintf "%s%s %s(%s) {" ind retType safeName paramList]
        @ bodyStmts
        @ [sprintf "%s}" ind]
    
    let ctx' = addVarName funcDef.Id funcDef.Name ctx
    (code, ctx')

/// Generate a function as a C++ lambda (for functions that capture module-level bindings)
let genFuncDefAsLambda (ctx: CodeGenContext) (funcDef: IRFuncDef) : string list * CodeGenContext =
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
    let bodyStr = exprToCpp bodyNames funcDef.Body
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
    let code =
        if isUnitExpr funcDef.Body then
            [sprintf "%s%s %s = [&](%s) { %s; };" ind funcType safeName paramList bodyStr]
        else
            [sprintf "%s%s %s = [&](%s) -> %s { return %s; };" ind funcType safeName paramList retType bodyStr]
    let ctx' = addVarName funcDef.Id funcDef.Name ctx
    (code, ctx')

/// Generate C++ code for an entire IR module.
/// Returns (functionDefs, bindingCode) — functions go outside main(), bindings inside.
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
    | IRFusion _ | IRChoice _ | IRArrayProduct _ | IRComposeObj _
    | IRComposeMeth _ | IRCompose _ | IRFunctorMap _ | IRPure _
    | IRReplicate _ | IRSequence _ -> true
    | IRLet (_, _, body) -> isComputeBindingExpr body
    | _ -> false

let private isComputeBinding (b: IRBinding) : bool =
    isComputeBindingExpr b.Value

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
    
    // Collect constrained struct names from type definitions
    let constrainedNames = 
        modul.Types |> List.choose (function
            | IRTDStruct (name, _, Some _) -> Some name
            | _ -> None)
        |> Set.ofList
    let ctx0 = { ctx0 with ConstrainedStructs = constrainedNames; ProviderReads = modul.ProviderReads; RandomInits = modul.RandomInits; CompoundInits = modul.CompoundInits }
    
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
    
    // Build set of all function IDs (to exclude from free var checks)
    let funcIds = modul.Functions |> List.map (fun f -> f.Id) |> Set.ofList
    
    // Generate in ID order (approximates source order).
    // First, collect file-scope functions to generate forward declarations.
    //
    // Explicit-pass capture: lifted callables receive their captures
    // as additional reference-typed parameters appended after the
    // regular params (see genFuncDef). For the
    // file-scope eligibility check, capture VarIds therefore count as
    // "param-like" — they're in the function's actual C++ signature,
    // not its enclosing scope. Without including them in `paramIds`
    // here, `collectVarRefsIR funcDef.Body` reports them as free vars
    // and the function gets excluded from forward declarations. After
    // Stage 3c.3 that's a problem: `let f = lambda(...)` emits a
    // wrapper closure `auto f = [&](...) { return __lambda_X(..., captures); };`
    // at the binding's emission site, which may precede the lifted
    // function's definition in the file. Without a forward decl,
    // `__lambda_X` is unknown at the wrapper's site and the C++
    // compile fails with "not declared in this scope".
    let hasFreeVarsCheck (funcDef: IRFuncDef) (c: CodeGenContext) =
        let paramIds = funcDef.Params |> List.map (fun p -> p.VarId) |> Set.ofList
        let captureIds = funcDef.Captures |> List.map (fun cap -> cap.Id) |> Set.ofList
        let bound = Set.unionMany [paramIds; captureIds; funcIds]
        let freeVars = Set.difference (collectVarRefsIR funcDef.Body) bound
        freeVars |> Set.exists (fun id -> Map.containsKey id c.VarNames)
    
    let fileScopeFuncs =
        allItems |> List.choose (fun (_, item) ->
            match item with
            | Choice2Of2 funcDef when not (hasFreeVarsCheck funcDef ctx0) -> Some funcDef
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
                if hasFreeVarsCheck funcDef c then
                    let (code, c') = genFuncDefAsLambda c funcDef
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
/// binding output is routed into TWO buckets — `setupCode` (data-setup
/// bindings) and `computeCode` (forced computations) — so the caller can place
/// a timing checkpoint between them. Functions stay in `funcCode`. Context is
/// still threaded through ALL items in ID order (later bindings reference
/// earlier ones), so only the OUTPUT is partitioned, never the evaluation
/// order. Returns (funcCode, setupCode, computeCode).
let genModuleSplit (modul: IRModule) (builder: IRBuilder) : string list * string list * string list =
    setCodegenStructFieldsCache modul.Types
    let callables = IR.buildCallablesTableForModule modul
    IR.setCallablesContext callables |> ignore
    let ctx0 = emptyContext ()
    let constrainedNames =
        modul.Types |> List.choose (function
            | IRTDStruct (name, _, Some _) -> Some name
            | _ -> None)
        |> Set.ofList
    let ctx0 = { ctx0 with ConstrainedStructs = constrainedNames; ProviderReads = modul.ProviderReads; RandomInits = modul.RandomInits; CompoundInits = modul.CompoundInits }
    let ctx0 =
        modul.Bindings |> List.fold (fun c b -> addVarName b.Id b.Name c) ctx0
    let ctx0 =
        modul.Functions |> List.fold (fun c f -> addVarName f.Id f.Name c) ctx0
    let bindingItems = modul.Bindings |> List.map (fun b -> (b.Id, Choice1Of2 b))
    let funcItems = modul.Functions |> List.map (fun f -> (f.Id, Choice2Of2 f))
    let allItems = bindingItems @ funcItems |> List.sortBy fst
    let funcIds = modul.Functions |> List.map (fun f -> f.Id) |> Set.ofList
    let hasFreeVarsCheck (funcDef: IRFuncDef) (c: CodeGenContext) =
        let paramIds = funcDef.Params |> List.map (fun p -> p.VarId) |> Set.ofList
        let captureIds = funcDef.Captures |> List.map (fun cap -> cap.Id) |> Set.ofList
        let bound = Set.unionMany [paramIds; captureIds; funcIds]
        let freeVars = Set.difference (collectVarRefsIR funcDef.Body) bound
        freeVars |> Set.exists (fun id -> Map.containsKey id c.VarNames)
    let fileScopeFuncs =
        allItems |> List.choose (fun (_, item) ->
            match item with
            | Choice2Of2 funcDef when not (hasFreeVarsCheck funcDef ctx0) -> Some funcDef
            | _ -> None)
    let forwardDecls = genForwardDecls fileScopeFuncs
    // Single split point: emit in strict ID order (NO reordering), and once
    // the first compute binding is seen, every subsequent item stays in the
    // compute phase. This preserves all cross-binding dependencies — a consumer
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
                // prior — producers, decompact chains — is setup). Otherwise
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
                if hasFreeVarsCheck funcDef c then
                    // Lambda-as-binding (closure definition): follows the
                    // current phase — setup if before the first compute, else
                    // compute — so it never floats across a dependency.
                    let (code, c') = genFuncDefAsLambda c funcDef
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
let genStructDef (name: string) (fields: (string * IRType) list) (invariant: StructConstraintInfo option) : string list =
    let fieldLines = fields |> List.map (fun (fname, fty) ->
        // Array-typed fields render as Array<T,N> / Ragged<T> wrappers so
        // the field carries its shape with it. Other types use the
        // standard irTypeToCpp rendering.
        let cppTy =
            match fty with
            | ArrayElem arr -> cppArrayTypeStr arr
            | _ -> irTypeToCpp fty
        sprintf "    %s %s;" cppTy fname)
    let validateLines =
        match invariant with
        | Some info ->
            // Build name map: IRVar IDs in invariant -> field names
            let nameMap = info.FieldBindings |> List.fold (fun m (fname, fid) -> Map.add fid fname m) Map.empty
            let constraintStr = exprToCpp nameMap info.Expr
            ["    void validate() const {"
             sprintf "        if (!(%s)) {" constraintStr
             sprintf "            std::cerr << \"Constraint violation in %s\" << std::endl;" name
             "            std::abort();"
             "        }"
             "    }"]
        | None -> []
    [sprintf "struct %s {" name]
    @ fieldLines
    @ validateLines
    @ ["};"
       ""]

/// Generate type definitions for a module
let genTypeDefs (modul: IRModule) : string list =
    modul.Types |> List.collect (function
        | IRTDStruct (name, fields, invariant) -> genStructDef name fields invariant
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
            // rather than bare int64_t. The alias is transparent —
            // int64_t-compatible — but makes generated C++ self-documenting
            // and leaves a hook for future strong typing.
            [sprintf "using %s = int64_t;" name; ""]
        | IRTDEnumIdx (name, _, values) ->
            // EnumIdx alias: render as the underlying runtime type. All-int
            // values → int64_t; all-string values → std::string. The chosen
            // C++ type must match what the Case 2 reverse-lookup dispatch
            // and any keys array stored under this type expect.
            let underlying = IR.EnumValue.underlyingElemType values
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
            // end of each strict-packed row into adjacent/garbage memory —
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
                sprintf "%sfor (size_t %s = 0; %s < %s; %s++) {" indent loopVar loopVar bound loopVar)
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
        | IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ | IRComposeObj _ | IRComposeMeth _ | IRBind _ | IRZip _ | IRSequence _ -> true
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
            else
            match b.Value with
            | IRCompute (IRApplyCombinator _) -> true
            | IRCompute (IRComposeApply _) -> true
            | IRCompute (IRParallel _) -> true
            | IRCompute (IRFusion _) -> true
            | IRCompute (IRVar _) -> true
            | IRCompute (IRFunctorMap _) -> true
            | IRCompute (IRChoice _) -> true
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
                    // address-printing — neither is meaningful for testing.
                    // Skip with a diagnostic comment so the surrounding
                    // value-check on scalar results derived from calls
                    // (e.g. `let r = funcs(1)(5.0)`) still runs.
                    [sprintf "    // (array '%s' of function values not auto-printed; std::function isn't streamable)" b.Name]
                | IRTNamed structName ->
                    let rank = arrayRank arrType
                    let structFields =
                        modul.Types |> List.tryPick (fun td ->
                            match td with
                            | IRTDStruct (n, fs, _) when n = structName -> Some fs
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
                        // no fields — emit diagnostic comment and skip.
                        [sprintf "    // (array '%s' of struct '%s' not auto-printed; access individual fields via %s[i].field)" b.Name structName b.Name]
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
                    [sprintf "    // (trailing-row view '%s' not auto-printed; the raw T* row carries no extents — derive scalars via %s(t))" b.Name b.Name]
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
let genMainWrapper (testName: string) (bodyIndented: string list) (printCode: string list) : string list =
    [ "int main() {"
      "    cout << std::setprecision(15);"
      "    cout << std::boolalpha;"
      "    auto start = TIME;"
      "" ]
    @ bodyIndented
    @ [ ""
        "    auto end = TIME;"
        "    double elapsed = 1e-9 * TIME_DIFF;"
        sprintf "    cout << \"%s completed in \" << elapsed << \"s\" << endl;" testName
        ""
        "    // Print results for verification" ]
    @ printCode
    @ [ ""
        "    return 0;"
        "}" ]

/// Split-timing variant of genMainWrapper. `setupIndented` is input-data setup
/// (array literals, etc.); `computeIndented` is the computation. Emits two
/// checkpoints: "Input Allocation took <t>s" around setup, and the canonical
/// "<name> completed in <t>s" around ONLY the compute region — so the harness's
/// existing "completed in" parser reads the compute time, not the whole body.
/// The clock variable is reused (start/end reset between phases) exactly as the
/// archaic Blade prototype did.
let genMainWrapperSplit (testName: string) (setupIndented: string list) (computeIndented: string list) (printCode: string list) : string list =
    [ "int main() {"
      "    cout << std::setprecision(15);"
      "    cout << std::boolalpha;"
      "    auto start = TIME;"
      "" ]
    @ setupIndented
    @ [ ""
        "    auto end = TIME;"
        "    double setup_elapsed = 1e-9 * TIME_DIFF;"
        sprintf "    cout << \"%s input allocation took \" << setup_elapsed << \"s\" << endl;" testName
        ""
        "    start = TIME;" ]
    @ computeIndented
    @ [ ""
        "    end = TIME;"
        "    double elapsed = 1e-9 * TIME_DIFF;"
        sprintf "    cout << \"%s completed in \" << elapsed << \"s\" << endl;" testName
        ""
        "    // Print results for verification" ]
    @ printCode
    @ [ ""
        "    return 0;"
        "}" ]

/// Generate a C++ program (uses external runtime header)
/// Generate print statements for all bindings in a module.
/// Shared by genSelfContainedProgram and genProgramWithExternalRuntime.
let genMainProgram (modul: IRModule) (testName: string) : string =
    (exprWarningsCell ()).Value <- []
    // Reset the CUDA kernel collector; genCudaKernel appends during genModule.
    (cudaKernelDefsCell ()).Value <- []
    (symmDeclsCell ()).Value <- []
    let builder = IRBuilder()
    
    let includes = genIncludes ()
    let (funcDefs, bindCode) = genModule modul builder

    // extern "C" launch-wrapper prototypes for any CUDA kernels emitted during
    // genModule. Bodies live in the .cu (nvcc); the .cpp needs only the proto to
    // call across the linkage boundary. Extract each wrapper's signature line
    // (starts `extern "C" void __launch_`) and `;`-terminate it.
    let cudaProtos =
        (cudaKernelDefsCell ()).Value
        |> List.filter (fun line -> line.StartsWith("extern \"C\" void __launch_"))
        |> List.map (fun sigLine ->
            let trimmed = sigLine.TrimEnd()
            (if trimmed.EndsWith("{") then trimmed.Substring(0, trimmed.Length - 1).TrimEnd() else trimmed) + ";")
    let symmDecls = (symmDeclsCell ()).Value
    
    let bodyIndented = bindCode |> List.map (fun s -> "    " + s)
    let mainFunc = genMainWrapper testName bodyIndented []
    
    (includes @ [""] @ symmDecls @ [""] @ cudaProtos @ [""] @ funcDefs @ mainFunc) |> String.concat "\n"

/// The .cu file content for the most recently assembled program, or None if no
/// CUDA kernel was emitted. Call AFTER genMainProgram/genProgramFromIR (the
/// collector is populated during assembly).
let getCudaFileContent () : string option =
    match (cudaKernelDefsCell ()).Value with
    | [] -> None
    | defs ->
        let header =
            [ "// Generated CUDA kernels (.cu) — compiled by nvcc, linked with the .cpp."
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
            RandomInits = modules |> List.fold (fun acc m -> Map.fold (fun a k v -> Map.add k v a) acc m.RandomInits) Map.empty
            CompoundInits = modules |> List.fold (fun acc m -> Map.fold (fun a k v -> Map.add k v a) acc m.CompoundInits) Map.empty
        }
        genMainProgram merged testName

/// Generate C++ struct definition from IRTDStruct
let genSelfContainedProgram (modul: IRModule) (testName: string) : string =
    let builder = IRBuilder()
    // Reset the CUDA kernel collector for this program; genCudaKernel appends
    // during genModule. Read afterward via getCudaFileContent for the .cu file.
    (cudaKernelDefsCell ()).Value <- []
    // Reset the symm-decl hoist collector; symmetric outputs append namespace-
    // scope symm arrays during genModule, emitted in the preamble below.
    (symmDeclsCell ()).Value <- []
    
    let includes =
        // Provider reads (load_compound / load) emit nc_* calls, which need the
        // netcdf C API declarations. Added only when the module actually has
        // provider reads, so non-provider programs gain no libnetcdf dependency.
        let baseInc = genIncludesExternal ()
        if Map.isEmpty modul.ProviderReads then baseInc
        else baseInc @ ["#include <netcdf.h>"]
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
            (funcDefs, genMainWrapperSplit testName setupIndented computeIndented printCode)
        else
            let (funcDefs, bindCode) = genModule modul builder
            let bodyIndented = bindCode |> List.map (fun s -> "    " + s)
            (funcDefs, genMainWrapper testName bodyIndented printCode)
    let (funcDefs, mainBody) = mainFunc

    // extern "C" launch-wrapper prototypes for any CUDA kernels emitted: the
    // .cpp calls them across the linkage boundary (bodies live in the .cu).
    let cudaProtos =
        (cudaKernelDefsCell ()).Value
        |> List.filter (fun line -> line.StartsWith("extern \"C\" void __launch_"))
        |> List.map (fun sigLine ->
            let trimmed = sigLine.TrimEnd()
            (if trimmed.EndsWith("{") then trimmed.Substring(0, trimmed.Length - 1).TrimEnd() else trimmed) + ";")

    // Namespace-scope symm arrays hoisted out of main() (MSVC constant-address
    // requirement — see hoistSymmDecl).
    let symmDecls = (symmDeclsCell ()).Value

    (includes @ typeDefs @ [""] @ symmDecls @ [""] @ cudaProtos @ [""] @ funcDefs @ mainBody) |> String.concat "\n"

/// Generate a C++ program with external runtime header
/// Returns (mainFileContent, headerFileContent)
let genProgramWithExternalRuntime (modul: IRModule) (testName: string) : string * string =
    let builder = IRBuilder()
    
    let includes =
        // See genSelfContainedProgram: netcdf C API needed only for provider reads.
        let baseInc = genIncludesExternal ()
        if Map.isEmpty modul.ProviderReads then baseInc
        else baseInc @ ["#include <netcdf.h>"]
    let typeDefs = genTypeDefs modul
    let (funcDefs, bindCode) = genModule modul builder
    
    let bodyIndented = bindCode |> List.map (fun s -> "    " + s)
    let printCode = genPrintStatements modul
    let mainFunc = genMainWrapper testName bodyIndented printCode
    
    let mainFile = (includes @ typeDefs @ [""] @ funcDefs @ mainFunc) |> String.concat "\n"
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
                RandomInits = modules |> List.fold (fun acc m -> Map.fold (fun a k v -> Map.add k v a) acc m.RandomInits) Map.empty
                CompoundInits = modules |> List.fold (fun acc m -> Map.fold (fun a k v -> Map.add k v a) acc m.CompoundInits) Map.empty
            }
            genSelfContainedProgram merged testName
    (code, cell.Value)
