// Blade-DSL Intermediate Representation
// Lowered from AST, ready for optimization and code generation
// Implements the Structural Trinity: Loop Reification, Arity Polymorphism, Dimensional Currying

module Blade.IR

open Blade.Types

open System

// Core Type Definitions

/// Unique identifier for IR values
/// Index type in IR - represents a single dimension's structure
type IRLit =
    | IRLitInt of int64
    | IRLitFloat of float
    | IRLitBool of bool
    | IRLitString of string
    | IRLitUnit

/// Binary operations
type IRBinOp =
    | IRAdd | IRSub | IRMul | IRDiv | IRMod | IRCaret  // ^ for power
    | IREq | IRNeq | IRLt | IRLe | IRGt | IRGe
    | IRAnd | IROr

/// Mode for binary array operations
type IRBinOpMode =
    | IRElementwise   // a + b (zip iteration)
    | IROuter         // a [+] b (cross iteration)

/// Unary operations
type IRUnaryOp =
    | IRNeg | IRNot | IRConj
    | IRReal | IRImag | IRArg  // complex component/phase accessors
                              // (real/imag: identity/0 on real operands)
    | IRMath of string  // scalar math intrinsic (exp/log/sqrt/...);
                        // renders as std::<name>(arg), result Float64
                        // (complex operand preserves the complex type,
                        //  except abs which always yields the real magnitude)

/// IR Expressions - SSA-like representation
type IRExpr =
    | IRLit of IRLit
    | IRVar of id: IRId * ty: IRTypeG<IRExpr>
    | IRParam of name: string * idx: int * ty: IRTypeG<IRExpr>
    | IRBinOp of IRBinOpMode * IRBinOp * IRExpr * IRExpr
    | IRUnaryOp of IRUnaryOp * IRExpr
    | IRArrayLit of IRExpr list * IRArrayTypeG<IRExpr>
    | IRIndex of array: IRExpr * index: IRExpr list * identity: ArrayIdentity option
    | IRSlice of array: IRExpr * dim: int * start: IRExpr * stop: IRExpr
    | IRCurry of array: IRExpr * index: IRExpr * resultRank: int
    | IRApp of func: IRExpr * args: IRExpr list * retType: IRTypeG<IRExpr>
    | IRTuple of IRExpr list
    // std::complex<double>(re, im) at codegen. Distinct from IRTuple: Complex
    // is a scalar throughout the IR. Components are arbitrary float-typed
    // IRExpr, not just literals -- supports `complex(x, y)` for x, y: Float64.
    | IRComplex of re: IRExpr * im: IRExpr
    | IRTupleProj of IRExpr * int * bool  // expr, index, isFlat (true=flat leaf index, false=structural type index)
    | IRTupleCons of head: IRExpr * tail: IRExpr
    | IRTupleDecons of tuple: IRExpr
    | IRFieldAccess of obj: IRExpr * field: string  // Struct field access: obj.field
    | IRStructLit of typeName: string * fields: (string * IRExpr) list  // Struct literal: T { f1 = e1, ... }
    | IRIf of cond: IRExpr * thenBr: IRExpr * elseBr: IRExpr
    | IRMatch of scrutinee: IRExpr * cases: IRMatchCase list
    | IRLet of IRId * value: IRExpr * body: IRExpr
    | IRMethodFor of MethodForInfo
    | IRObjectFor of ObjectForInfo
    | IRApplyCombinator of ApplyInfo
    /// Slot-inverted apply: `(object_for(f) >>@ object_for(g)) <@> arrays`.
    /// A canonical apply's kernel slot holds a callable; the compose case
    /// threads arrays through a composed-object chain instead. Kept as a
    /// separate variant so no consumer needs to inspect Loop's shape to tell
    /// the two apart. See `ComposeApplyInfo` below.
    | IRComposeApply of ComposeApplyInfo
    | IRBind of comp: IRExpr * cont: IRExpr
    | IRParallel of IRExpr * IRExpr * fusionDepth: int option
    | IRFusion of IRExpr * IRExpr
    | IRChoice of IRExpr * IRExpr
    // <|:> allocated-fallback: left where storage holds the cell, else right.
    // Distinct from IRChoice (value-level zero test) -- see TExprFallback.
    | IRFallback of IRExpr * IRExpr
    | IRArrayProduct of IRExpr * IRExpr
    | IRComposeObj of IRExpr * IRExpr
    | IRComposeMeth of IRExpr * IRExpr
    | IRCompose of IRExpr * IRExpr
    | IRFunctorMap of func: IRExpr * comp: IRExpr
    | IRPure of IRExpr
    | IRCompute of IRExpr
    | IRReynolds of kernel: IRExpr * isAntisymmetric: bool  // Reynolds combinator
    | IRGuard of cond: IRExpr * body: IRExpr
    | IRSequence of IRExpr list
    | IRReplicate of count: IRExpr * body: IRExpr
    | IRMask of array: IRExpr * pred: IRExpr  // mask(array, pred) - filter array by predicate
    | IRIntersect of IRExpr * IRExpr          // intersect(A, B) - set intersection (deduplicated, order from A)
    | IRUnion of IRExpr * IRExpr              // union(A, B) - set union (deduplicated, A's elements first)
    | IRUnique of array: IRExpr               // unique(A) - dedup, first-occurrence order
    | IRContains of array: IRExpr * value: IRExpr  // contains(A, x) - membership test, returns bool
    | IRGroupBy of values: IRExpr * grouping: IRExpr  // group_by(vals, gk) - apply grouping
    | IRGroupKeys of keys: IRExpr list               // group_keys(keys1, keys2, ...) - CSR grouping; multi-key => compound dispatch
    | IRSort of array: IRExpr * key: IRExpr          // sort(arr, key) - stable ascending sort by key
    | IRReduce of array: IRExpr * kernel: IRExpr * init: IRExpr option  // reduce(arr, op[, init]) - fold innermost dim; init seeds the fold and defines the empty result
    // reduce(deferred, op[, init]): the FUSED reduction terminal -- folds a
    // deferred computation (IRApplyCombinator, or an IRFusion tree of them)
    // without materializing the array(s). ONE loop nest; one scalar
    // accumulator per fusion leaf (tuple of scalars for trees). init is
    // ALWAYS filled by the checker (identity for (+)/(*) sections, user's
    // init otherwise -- arbitrary kernels REQUIRE an explicit init).
    | IRReduceCompute of computation: IRExpr * kernel: IRExpr * init: IRExpr
    | IRProdSum of args: IRExpr list  // prodsum(x1..xk): fused sum_t prod_l x_l(t) over rank-1 arrays of equal extent; empty extent => 0
    | IRZip of IRExpr list
    | IRAlign of arrays: IRExpr list * spec: AlignSpec
    | IRStack of IRExpr list
    | IRTranspose of array: IRExpr * dim1: int * dim2: int
    | IRDecompact of array: IRExpr * dim: int
    | IRGram of left: IRExpr * right: IRExpr * isSameArray: bool  // A * B^H contraction; symmetric/Hermitian when isSameArray
    | IRMatmul of left: IRExpr * right: IRExpr  // A(m x k) * B(k x n) -> dense m x n; the math package's matmul, emitted through blade_linalg
    /// eigh(S): eigendecomposition of a rank-2 square operand -> the TUPLE
    /// (Q, LAM), emitted through `blade_lapack`. TUPLE-typed with TWO fresh
    /// pools; for complex input Q is complex but LAM is real (eigenvalues of
    /// a Hermitian matrix are real). Exists only when `lapackAvailable ()`
    /// held at elaboration; gate off, `math.eigh` expands to synthesized
    /// Jacobi source instead and this node is never built.
    | IREigh of operand: IRExpr
    | IRArrayNegate of array: IRExpr     // whole-array elementwise negation (eager); type-preserving
    | IRArrayConjugate of array: IRExpr  // whole-array elementwise conjugation (eager); type-preserving
    | IRReverse of array: IRExpr * dim: int
    | IRShift of array: IRExpr * dim: int * offset: IRExpr * boundary: BoundaryMode
    | IRDiag of array: IRExpr
    | IRJoin of arrays: IRExpr list * dim: int
    | IRSubset of array: IRExpr * dim: int * start: IRExpr * length: IRExpr
    | IRRange of IRIndexTypeG<IRExpr> list * offset: IRExpr option
    | IRVirtualReverse of IRIndexTypeG<IRExpr>
    | IRBlocked of IRIndexTypeG<IRExpr> * blockSize: IRExpr
    // halo<CompoundIdx<m>> window read w(o): the COORDINATE of the present
    // cell at ordinal (window + offset). Renders via the peel-emitted local
    // alias `<w>_hcidx` of the materialized compound index (dense halo reads
    // stay plain `(w + o)` arithmetic and never build this node). This node
    // is also the future carousel seam: buffered reads replace its rendering.
    | IRHaloUnhash of window: IRExpr * offset: int64
    | IRArity of resolved: int option * paramName: string  // None = unresolved (use paramName), Some n = bound
    | IRNth
    | IRZero
    | IRRank of array: IRExpr
    | IRPolyIndex of pack: IRExpr * index: IRExpr  // Dynamic poly-pack indexing: args[k]
    // Pack tail from cons-destructuring `let head :: tail = A`: a "shifted
    // pack view" behaving as a Poly pack of arity one less than `pack`,
    // dropping the first `drop` elements. Pre-monomorphization only --
    // specializeFunction rewrites it into concrete params, so it never
    // survives to codegen/interp.
    | IRPolyTail of pack: IRExpr * drop: int
    | IRExtent of array: IRExpr * dim: int
    // Ragged-extent marker: where this appears as an Extent, codegen emits a
    // lookup into the lengths array at the current iteration's flat outer
    // position. The lengths array is shaped to match the prior index
    // dimensions (e.g. Array<Nat like Idx<M>, Idx<N>> for `Idx<M>, Idx<N>,
    // RaggedIdx<lengths>`). Distinct from IRExtent (queries an array's
    // extent metadata): this reads a value from the lengths array itself.
    | IRRaggedLookup of lengths: IRExpr
    // Compound-index marker (formalism 4.5): where this appears as an Extent,
    // the index type is a CompoundIdx -- a masked product space whose valid
    // coordinates are selected by `mask`, a RUNTIME array (a CompoundIdx is
    // identified by a whole-mask hash). Rank = mask rank; per-dim extents
    // come from the mask's array type at codegen. Cardinality is NOT
    // closed-form -- compound_index_t builds its rank<->tuple table at
    // construction and reports cardinality at runtime. Distinct from
    // IRRaggedLookup: a compound mask couples all dimensions at once.
    | IRCompoundMask of mask: IRExpr
    // Residual-compound marker (formalism 4.5, partial indexing): where this
    // appears as an Extent, the index type is a CompoundIdx from PARTIALLY
    // indexing a parent compound -- pinning the first `prefixLen` (= j) of
    // the parent's k coordinates, k-j left free. Rank = k-j. Representation:
    // SHARED DATA, MATERIALIZED INDEX -- data is a non-copied window into the
    // parent's lex-sorted pool (prefix_range [lo,hi)); the index is a freshly
    // materialized compound_index_t<k-j> over that window. Cost is O(window),
    // not O(n). `parent` is the parent compound array; `prefixLen` is j --
    // pinned coordinate VALUES live at the indexing site (IRIndex), not here.
    // Codegen for the residual construction is not yet implemented; the
    // partial-index path emits an explicit error rather than miscompiling.
    | IRCompoundProject of parent: IRExpr * prefixLen: int
    // Sparse extent marker (formalism 3.5): where this appears as an Extent,
    // the index type is a SparseIdx -- an explicit valid-tuple enumeration
    // with hash lookup, keys in GIVEN order (never sorted). Twin of
    // IRCompoundMask, mask replaced by SkStatic (compile-time-folded key
    // list, baked as literals) or SkRuntime (runtime rank-1 tuple-element
    // array; sparse_index_t built from it at runtime). Rank = key tuple
    // arity; Cardinality = key count. Drives PlaceTabulated, like IRCompoundMask.
    | IRSparseKeys of source: SparseKeysSource
    // Orbit (iterated-wreath) class marker: where this appears as an Extent,
    // the index type is an `OrbIdx` of DEPTH >= 2 (docs/plan-orbit-index-types.md
    // section 2). Twin of IRSparseKeys: carries the flat level list
    // [(r1,s1), ..., (rd,sd)], outermost-last, `true` = '+' -- has nowhere
    // else to live without a field every other index kind carries as None.
    // `extent` is the BASE extent n (the fold's M0), an ordinary extent
    // expression reached like a plain Idx extent by folding/varref/subst.
    // Rank = PRODUCT of the level ranks. Cardinality is closed-form (section
    // 4's iterated binomial, `Blade.OrbRank.cellCountChecked`), so this
    // drives PlaceCombinatorial, NOT PlaceTabulated. DEPTH <= 1 NEVER
    // PRODUCES THIS: `OrbIdx<[(r,+)],n>` lowers to the exact SymIdx record,
    // `OrbIdx<[],n>` to the exact Idx record.
    | IROrbitClass of levels: (int * bool) list * extent: IRExpr
    // Opaque-extent marker: an Extent determined by surrounding context
    // rather than declared up front (e.g. kernel-parameter type
    // `RaggedIdx<_>`, where the peeled param is bound to a sub-array whose
    // `_extents[0]` carries the actual length). Unlike IRRaggedLookup it
    // carries no data -- the loop binding's ExtentArrayRef tells codegen
    // where to read the concrete extent from. Should not normally reach
    // codegen directly; the loop builder reads it via ExtentArrayRef.
    | IROpaqueExtent
    | IRAssign of target: IRExpr * value: IRExpr
    | IRForRange of varId: IRId * lo: IRExpr * hi: IRExpr * body: IRExpr
    /// Runtime constraint guard, statement-positioned:
    /// `if (!(cond)) { blade_rt::panic("BL8001", message, file, line); }`.
    /// Synthesized (mutual-group joint checks, struct constraint checks);
    /// value type is unit. The span carries source provenance for runtime
    /// traces; noSpan degrades the panic to a nullptr file / 0 line. Safe to
    /// add here: IRConstraintCheck is statement-positioned and never hits
    /// the structural `=` fast paths other IRExpr cases flow through.
    | IRConstraintCheck of cond: IRExpr * message: string * span: Blade.Ast.Span

/// Abstract callable in IR: the merged form of source-level functions and
/// lambdas. Lives in the IRExpr mutual-recursion group because
/// `IRCallable.Body : IRExpr` cycles with IRExpr's variants.
///
/// Field roles by source kind: Id/Name always populated (lambdas get
/// synthesized "__lambda_<id>"); Captures empty for functions, populated for
/// lambdas by lambda-lifting; IsCommutative/CommGroups default false/[] when
/// unannotated; Parallelism fields populate identically for both kinds
/// (omp/cuda/mpi clauses from a where-clause or a strategy list); IsStatic/
/// IsArityPoly/ArityParam are function-only (false/None for lambdas);
/// RetType comes from declaration for functions, inference for lambdas.
///
/// Codegen renders all callables as top-level C++ functions
/// (originalParams..., captures...); call sites referencing a callable with
/// non-empty Captures get a thin C++ lambda wrapper forwarding captures by
/// reference, preserving OCaml-style capture semantics.
and IRCallable = {
    Id: IRId
    Name: string
    Params: IRParam list
    RetType: IRTypeG<IRExpr>
    Body: IRExpr
    IsStatic: bool
    IsCommutative: bool
    CommGroups: int list list
    // AntisymGroups: positions a `where anticomm(...)` clause declared
    // anti-invariant. Also appear in CommGroups (same grouping/iteration
    // license as comm); this list is the extra bit saying the simplex is
    // STRICT (no diagonal, sign flip on swap). Empty unless declared, so the
    // reynolds antisymmetrization path (its own flag on IRReynolds) is
    // untouched.
    AntisymGroups: int list list
    // Per-(param-index, dim-count) detail from an `omp(...)` clause.
    // IsOmpParallel is the derived "requested OpenMP" opt-in flag driving
    // loop parallelization (buildLoopNestCodeGen).
    Parallelism: (int * int) list
    IsOmpParallel: bool
    // Opt-in flag for the `cuda` strategy: true emits a flat-launch
    // __global__ kernel + host launch instead of a host loop nest
    // (genCudaKernel). CudaBlockSize is the launch block size (default 256).
    // omp/cuda/mpi are mutually exclusive; all false means serial host loop.
    IsCudaKernel: bool
    CudaBlockSize: int
    // Opt-in flag for `mpi`: iteration domain decomposed across MPI ranks
    // (SPMD; output restored via Allgatherv). Gated like cuda -- inert
    // unless mpiEmitMode is on (genApplyCombinator).
    IsMpiParallel: bool
    IsArityPoly: bool
    ArityParam: string option
    Captures: CaptureInfo list
    // Per-parameter SIGN parity of the body (KspOdd/KspEven/KspUnknown, in
    // declaration order), consumed by `deduceWreathTie`'s soundness gate.
    // Populated by the TypeCheck apply seam (including eta wrappers, via the
    // callee's FuncSignParities) and carried through Lowering so codegen and
    // the interpreter see the same values. Empty for non-kernel callables --
    // missing entries read as KspUnknown.
    SignParities: KernelSignParity list
}

/// Semantic-marker alias for IRCallable naming "top-level function in
/// module.Functions" -- distinct in intent from codegen-internal synthetic
/// callables and let-bound aliases; same underlying record.
and IRFuncDef = IRCallable

and CaptureInfo = {
    Id: IRId
    Name: string
    /// Lifted-lambda codegen extends the C++ signature with one parameter
    /// per capture, typed as a reference (`T&`) so mutation propagates and
    /// the value stays alive via the wrapper's `[&]` capture. Irrelevant for
    /// source-level functions (no captures).
    Type: IRTypeG<IRExpr>
    IsMutable: bool
}

/// Information about a method_for construction
and MethodForInfo = {
    Arrays: IRExpr list
    Identities: ArrayIdentity list
    ArrayTypes: IRArrayTypeG<IRExpr> list
    SDimsPerArray: int list
    TotalSDims: int
    SharedIndexTypes: IRIndexTypeG<IRExpr> list  // For co-iteration: shared iteration records (empty = not co-iteration; multi = product space)
}

/// Information about an object_for construction
and ObjectForInfo = {
    Kernel: IRExpr
    CommGroups: int list list
    InputRanks: int list  // irank(f, i) for each parameter
    OutputRank: int       // orank(f) - T-dimensions added by kernel
}

/// Information about combinator application (<@>)
and ApplyInfo = {
    Loop: IRExpr                            // Provenance: IRMethodFor or IRObjectFor
    Kernel: IRExpr
    Arrays: IRExpr list                     // The actual array expressions
    Identities: ArrayIdentity list          // Array identity tracking (for symmetry)
    ArrayTypes: IRArrayTypeG<IRExpr> list            // Array type info
    SharedIndexTypes: IRIndexTypeG<IRExpr> list      // For co-iteration (zip): shared records (empty = not co-iteration)
    SymcomStates: SymcomState list
    TriangularLevels: bool list
    SDimsPerArray: int list
    KernelInputRanks: int list
    KernelOutputRank: int
    KernelTDims: IRIndexTypeG<IRExpr> list           // T-dimension index types from kernel return type
    SpeedupFactor: int64
    ReynoldsSpeedup: int64        // Reynolds permutation count (n!); actual terms may be fewer after dedup
    HasReynolds: bool              // Whether kernel has Reynolds annotation
    OutputType: IRTypeG<IRExpr>             // Deduced output array type
    IsCoIteration: bool            // True for 'for ... in' co-iteration
}

/// Information for slot-inverted compose application:
///   (object_for(f) >>@ object_for(g)) <@> A
/// In a canonical `IRApplyCombinator` the `Kernel` slot is a callable
/// reference; in the compose case the kernel slot of `<@>` instead
/// holds the input arrays threaded through the composed-object chain.
/// `Composition` is the chain itself (`IRComposeObj`, or an `IRVar`
/// let-bound to one -- codegen resolves the latter via
/// `ctx.DeferredComputations`). `InputArrays` are the arrays the
/// chain is applied to. Unlike `ApplyInfo`, no symmetry/triangulation
/// metadata is carried: the composition's leaves carry their own.
and ComposeApplyInfo = {
    Composition: IRExpr     // Provenance: IRComposeObj (or IRVar bound to one)
    InputArrays: IRExpr list
    OutputType: IRTypeG<IRExpr>
}

and IRParam = {
    Name: string
    Type: IRTypeG<IRExpr>
    Index: int
    VarId: IRId   // The variable ID used in the lambda body
}

and IRMatchCase = {
    Pattern: IRPattern
    Guard: IRExpr option
    Body: IRExpr
}

and IRPattern =
    | IRPatWild
    | IRPatVar of IRId
    | IRPatLit of IRLit
    | IRPatTuple of IRPattern list
    | IRPatCons of IRPattern * IRPattern
    | IRPatVariant of name: string * tag: int * IRPattern option * isEnum: bool

and BoundaryMode =
    | BndShrink
    | BndPad of IRExpr
    | BndPeriodic
    | BndReflect

and AlignSpec = {
    Offsets: (int * int) list
    Boundary: BoundaryMode
}

/// The key source of an IRSparseKeys extent (see that case's doc). SkStatic
/// carries the compile-time-folded entry list (outer = keys in given order,
/// inner = one tuple's coordinates); SkRuntime references the runtime keys
/// array expression.
and SparseKeysSource =
    | SkStatic of entries: int64 list list
    | SkRuntime of keys: IRExpr


// Concrete instantiations of the Types.fs generic family at IRExpr --
// the prototype's extent representation. Everything downstream uses
// these names; swapping the extent representation (the rewrite's
// dedicated Extent DU) is a type-argument change here, nothing more.
type IRType = IRTypeG<IRExpr>
type IRIndexType = IRIndexTypeG<IRExpr>
type IRArrayType = IRArrayTypeG<IRExpr>
type IdxRef = IdxRefG<IRExpr>
type IRArrowSlot = IRArrowSlotG<IRExpr>
type LoopType = LoopTypeG<IRExpr>

// OrbIdx (iterated-wreath) accessors. The level list rides the Extent slot as
// an IROrbitClass marker (see that case), so every consumer reads it through
// these three functions rather than pattern-matching the extent inline -- one
// place to change if the carrier ever moves to a record field.

/// The (rank, isPlus) level list of a wreath class, OUTERMOST-LAST. `[]` for
/// every non-wreath record, so `not (List.isEmpty (orbitLevelsOf ix))` is
/// exactly "this is a depth >= 2 OrbIdx".
let orbitLevelsOf (ix: IRIndexType) : (int * bool) list =
    match ix.Extent with
    | IROrbitClass (levels, _) -> levels
    | _ -> []

/// The BASE extent (the section 4 fold's M0) of a wreath class; the record's own
/// Extent for anything else, so extent-reading code that does not care about
/// the class can go through here uniformly.
let orbitBaseExtent (ix: IRIndexType) : IRExpr =
    match ix.Extent with
    | IROrbitClass (_, n) -> n
    | other -> other

/// Render a level list in the surface spelling: `[(2,-), (2,+)]`.
let ppOrbitLevels (levels: (int * bool) list) : string =
    "[" + (levels |> List.map (fun (r, plus) -> sprintf "(%d,%s)" r (if plus then "+" else "-"))
                  |> String.concat ", ") + "]"

/// The ONE text for the hard refusal at the storage boundary: every refusal
/// site (typecheck gates, allocator, loop builder, compact read/print,
/// providers) calls this instead of spelling its own string. `where_` names
/// the seam ("let binding 'R'", "reduce()", ...) and reads as a prefix.
let orbitStorageUnsupported (where_: string) (levels: (int * bool) list) : string =
    sprintf "%s: OrbIdx<%s, n> is a declarable index class of depth %d, and a DEDUCED one can now be \
allocated, written, printed, READ at an arbitrary tuple (the per-level canon fold, the accumulated \
character, the zero set), FULLY DECOMPACTED to its dense tensor, and round-tripped through a Zarr \
store (the spec_version 2 'orbit' head -- providers/ZarrTriangularSpec.md). What is still missing is every \
path that would put a wreath pool anywhere but under its own traversal nest: an OrbIdx ANNOTATION \
(a store is now a producer, but the annotation also admits classes nothing produces), reduce/prodsum \
over the pool, transpose, PARTIAL (per-level) decompaction, a WINDOWED or distributed store read, and \
provider I/O outside Zarr (CSV and NetCDF have no pool axis to carry the class on). So the \
compiler refuses here rather than compute an address it cannot compute. \
The depth-1 spellings work through the existing compact machinery instead: OrbIdx<[(r,+)], n> is \
exactly SymIdx<r, n>, OrbIdx<[(r,-)], n> is exactly AntisymIdx<r, n>, and OrbIdx<[], n> is exactly \
Idx<n>."
            where_ (ppOrbitLevels levels) (List.length levels)

/// Bridge to `Blade.OrbRank`'s own level spelling: OrbRank is a
/// dependency-free module (no Blade namespace, so proofs/scripts can `#load`
/// it standalone) and owns `OrbSign`; the IR side keeps a bare bool. The ONE
/// conversion point.
let orbRankLevels (levels: (int * bool) list) : Blade.OrbRank.Level list =
    levels |> List.map (fun (r, plus) ->
        (r, (if plus then Blade.OrbRank.OPlus else Blade.OrbRank.OMinus)))

/// section 7.2's normalization: a level with r = 1 is the trivial group S_1,
/// a no-op at EITHER sign, so it's dropped. Mirrors `OrbRank.normalizeLevels`
/// over the IR's level representation (the ONE conversion point above keeps
/// the two spellings from growing separate rules).
let normalizeOrbitLevels (levels: (int * bool) list) : (int * bool) list =
    levels |> List.filter (fun (r, _) -> r <> 1)

/// Which of the three record shapes a normalized level list takes. THE ONE
/// normalization rule: both producers of an OrbIdx class -- the SURFACE type
/// (`TypeCheck.orbitIndexRecord`) and DEDUCTION (`deduceWreathTie`) -- route
/// through here, so they cannot disagree about the same class.
type OrbitNormalForm =
    /// `[]` -- the trivial class. The plain `Idx<n>` record.
    | OrbNfTrivial
    /// `[(r,s)]` -- exactly the `SymIdx<r,n>` / `AntisymIdx<r,n>` record.
    | OrbNfDepth1 of rank: int * isPlus: bool
    /// depth >= 2 -- the `SymWreath` record (see `mkWreathIndexRecord`).
    | OrbNfWreath of levels: (int * bool) list

let orbitNormalForm (levels: (int * bool) list) : OrbitNormalForm =
    match normalizeOrbitLevels levels with
    | [] -> OrbNfTrivial
    | [ (r, s) ] -> OrbNfDepth1 (r, s)
    | ls -> OrbNfWreath ls

/// Build the depth >= 2 `SymWreath` index record: Rank = RAW AXIS COUNT
/// (product of level ranks), level list in the Extent slot as an
/// `IROrbitClass` marker, `IxKOrbit` + "__orbidx" sentinel Tag (validator
/// enforces agreement). Shared by surface-type lowering and deduction so the
/// two cannot build differently-shaped records for the same class; `levels`
/// must already be normalized (a rank-1 level would miscount the Rank).
///
/// The axis fold SATURATES at a 65536 cap rather than running to completion
/// (Rank is a loop count/subscript arity all over the compiler; a
/// wrapped-negative Rank would be a nonsense type, not an error).
let mkWreathIndexRecord (id: IRId) (levels: (int * bool) list) (baseExtent: IRExpr) : IRIndexType =
    let axes64 = levels |> List.fold (fun a (r, _) -> if a > 65536L then a else a * int64 r) 1L
    if axes64 > 65536L then
        failwithf "OrbIdx%s: the class acts on more than 65536 raw axes (the product of its level ranks), which is \
past what this compiler will build an index record for. An iterated-wreath class's cell count leaves int64 well before \
its rank does (docs/plan-orbit-index-types.md section 7.2), so a class this wide is not storable at any extent."
                  (ppOrbitLevels levels)
    { Id = id; Rank = int axes64; Extent = IROrbitClass (levels, baseExtent)
      Symmetry = SymWreath; Tag = Some "__orbidx"; IxKind = IxKOrbit
      Kind = SDimension; Dependencies = [] }

/// Level-1 placement classification of a full index type. Today this derives
/// purely from the symmetry class (placementClassOf); it is the seam where
/// tabulated detection (CompoundIdx / SparseIdx, from the index type's typedef)
/// will hook in. Derived, not stored (mirrors behaviorOf): there is no
/// PlacementClass field on IRIndexType to fall out of sync.
let placementOf (ix: IRIndexType) : PlacementClass =
    // Tabulated placement is detected from the Extent carrier (IRCompoundMask),
    // not the symmetry class -- a CompoundIdx has Symmetry = SymNone but is NOT
    // dense. Everything else derives from symmetry via placementClassOf.
    match ix.Extent with
    | IRCompoundMask _ -> PlaceTabulated
    | IRSparseKeys _ -> PlaceTabulated
    // A residual (partially-indexed) compound: Rank 1 means one free
    // coordinate at the pinned prefix -- a contiguous window [lo,hi),
    // iterated as an ordinary dense 1-D loop. Rank >= 2 is still a masked
    // product space needing the tabulated (materialized child index) path.
    | IRCompoundProject _ -> if ix.Rank <= 1 then placementClassOf ix.Symmetry else PlaceTabulated
    | _ -> placementClassOf ix.Symmetry

/// Compute the promoted element type for two numeric types per section 3.4.2.
/// Returns None if the types are incompatible for promotion. Index nominal
/// tags are not represented at the ElemType level; their strict unification
/// happens at the IRType level via IRTIdxTagged in `unify`.
let promoteElemType (a: ElemType) (b: ElemType) : ElemType option =
    if a = b then Some a
    else
        match a, b with
        | ETInt32, ETInt64   | ETInt64, ETInt32   -> Some ETInt64
        | ETInt32, ETFloat32 | ETFloat32, ETInt32 -> Some ETFloat32
        | ETInt32, ETFloat64 | ETFloat64, ETInt32 -> Some ETFloat64
        | ETInt64, ETFloat32 | ETFloat32, ETInt64 -> Some ETFloat64
        | ETInt64, ETFloat64 | ETFloat64, ETInt64 -> Some ETFloat64
        | ETFloat32, ETFloat64 | ETFloat64, ETFloat32 -> Some ETFloat64
        // Complex mixed with a real (int/float) or a narrower complex widens to
        // the appropriate complex width. Complex64 mixed with Float64 or with
        // Complex128 widens to Complex128 (component precision follows the wider
        // operand). CodeGen inserts the explicit casts std::complex's same-type
        // operators require (see coerceComplexOperand).
        | ETComplex128, (ETFloat64 | ETFloat32 | ETInt64 | ETInt32 | ETComplex64 | ETComplex128)
        | (ETFloat64 | ETFloat32 | ETInt64 | ETInt32 | ETComplex64), ETComplex128 -> Some ETComplex128
        | ETComplex64, (ETFloat32 | ETInt64 | ETInt32 | ETComplex64)
        | (ETFloat32 | ETInt64 | ETInt32), ETComplex64 -> Some ETComplex64
        | ETComplex64, ETFloat64 | ETFloat64, ETComplex64 -> Some ETComplex128
        | _ -> None

/// Active pattern for assignment target (lvalue) classification
let (|LVVar|LVIndex|LVField|LVOther|) = function
    | IRVar (id, _)              -> LVVar id
    | IRIndex (arr, indices, _)  -> LVIndex (arr, indices)
    | IRFieldAccess (obj, field) -> LVField (obj, field)
    | e                          -> LVOther e

// Element-type active patterns.
//
// These patterns project an IRType into the role of "array element type."
// Each pattern represents one concern (primitives, inference variables,
// named types, function values); a site that only handles primitives uses
// `PrimElem` or `AnyPrimElem` and fails to match otherwise, so silent
// fallthroughs are caught at compile time rather than producing wrong-typed
// values.

/// Strict primitive element. Doesn't match through unit annotation.
let (|PrimElem|_|) (ty: IRType) =
    match ty with
    | IRTScalar et -> Some et
    | _ -> None

/// Primitive element, optionally unit-annotated or index-tagged. Workhorse
/// for read sites that just want the primitive and don't care whether
/// wrappers are attached. Matches through both IRTUnitAnnotated (physical
/// units) and IRTIdxTagged (nominal index tags) -- both preserve their
/// inner type and erase at codegen.
let (|AnyPrimElem|_|) (ty: IRType) =
    match ty with
    | IRTScalar et -> Some et
    | IRTUnitAnnotated (IRTScalar et, _) -> Some et
    | IRTIdxTagged (IRTScalar et, _) -> Some et
    | _ -> None

/// Unit-annotated primitive: returns both the elem type and the unit
/// signature. For unit-aware sites that need to track or propagate units.
let (|UnitPrimElem|_|) (ty: IRType) =
    match ty with
    | IRTUnitAnnotated (IRTScalar et, units) -> Some (et, units)
    | _ -> None

/// Inference variable in element position: the natural representation for
/// "kernel param's elem type, deferred until <@> unifies it with the
/// source array's per-row type."
let (|InferElem|_|) (ty: IRType) =
    match ty with
    | IRTInfer id -> Some id
    | _ -> None

/// Named (struct or sum) elem type: the dispatch site for eventual codegen
/// support of arrays of user-defined types.
let (|NamedElem|_|) (ty: IRType) =
    match ty with
    | IRTNamed name -> Some name
    | _ -> None

/// Function-valued elem type: reflects the array-function duality
/// (Array<T like Idx<n>> is conceptually `Idx<n> -> T`). Matches `IRTArrow`
/// when every slot is `SVal` (pure function, no storage-backed slots),
/// returning the `(args, ret)` view. Matches the empty-slot case too
/// (`IRTArrow ([], ret, None)`, a nullary function) -- the symmetric
/// counterpart to ArrayElem's empty-slot rejection. `identity` is ignored:
/// functions don't carry array identity.
let (|FuncElem|_|) (ty: IRType) =
    match ty with
    | IRTArrow (slots, ret, _) when slots |> List.forall (function SVal _ -> true | _ -> false) ->
        let args = slots |> List.map (function SVal t -> t | _ -> failwith "unreachable")
        Some (args, ret)
    | _ -> None

/// Smart constructor: build an arrow-shaped type from a parameter type
/// list and return type. Produces an `IRTArrow` with all-`SVal` slots
/// and no identity -- the unified-IR function form.
let mkFuncArrow (args: IRType list) (ret: IRType) : IRType =
    IRTArrow (args |> List.map SVal, ret, None)

/// Validate the shape constraints on an IRTArrow's slot list and result:
///   1. If any slot is SIdxVirt, all slots from that point on must be too
///      (no SIdx/SVal after the first SIdxVirt).
///   2. If any slot is SIdxVirt, the result must NOT be IRTArrow (virtual
///      arrays' elements must be simple values).
/// Returns [] if well-formed, else human-readable error strings.
let rec validateArrowShape (slots: IRArrowSlot list) (result: IRType) : string list =
    let errs = ResizeArray<string>()
    let firstVirt =
        slots |> List.tryFindIndex (function SIdxVirt _ -> true | _ -> false)
    match firstVirt with
    | None -> ()  // No virtual slots -- no constraint
    | Some k ->
        // Constraint 1: all slots at or after k must be SIdxVirt
        slots
        |> List.iteri (fun i slot ->
            if i > k then
                match slot with
                | SIdxVirt _ -> ()
                | SIdx _ ->
                    errs.Add(sprintf "Slot %d is SIdx but appears after first SIdxVirt at %d (stored cannot follow virtual)" i k)
                | SVal _ ->
                    errs.Add(sprintf "Slot %d is SVal but appears after first SIdxVirt at %d (virtual arrays cannot contain functions)" i k))
        // Constraint 2: result must not be an arrow
        match result with
        | IRTArrow _ ->
            errs.Add("Virtual arrow has IRTArrow result (virtual arrays cannot contain arrays/functions)")
        | _ -> ()
    errs |> List.ofSeq

/// Array-shaped type: matches `IRTArrow` when slots are uniformly all
/// `SIdx` (stored) or all `SIdxVirt` (virtual); mixed slots or any `SVal`
/// do NOT match. Returns an `IRArrayType` view (`.ElemType`, `.IndexTypes`,
/// `.IsVirtual`, `.Identity`) reconstructed fresh on each match --
/// `IRArrayType` is view-only, no DU constructor wraps it. Empty-slot
/// arrows do NOT match (nullary functions, per `mkFuncArrow []`); rank-0
/// arrays don't exist as a type form (`mkArrayLike` collapses them to
/// their element type at the producer side).
let (|ArrayElem|_|) (ty: IRType) =
    match ty with
    | IRTArrow ([], _, _) -> None  // nullary function, not an array -- see docstring
    | IRTArrow (slots, result, identity) ->
        let allStored = slots |> List.forall (function SIdx _ -> true | _ -> false)
        let allVirtual = slots |> List.forall (function SIdxVirt _ -> true | _ -> false)
        if allStored || allVirtual then
            let indexTypes =
                slots |> List.map (function
                    | SIdx i -> i
                    | SIdxVirt i -> i
                    | _ -> failwith "unreachable -- checked by guards above")
            Some {
                ElemType = result
                IndexTypes = indexTypes
                IsVirtual = allVirtual
                Identity = identity
            }
        else
            None
    | _ -> None

/// Stored-array variant of ArrayElem. Matches IRTArrow with all-SIdx slots
/// (non-empty). Useful for codegen paths that need to allocate / read
/// storage and want to reject virtual sources at the type-match level.
let (|StoredArrayElem|_|) (ty: IRType) =
    match ty with
    | IRTArrow ([], _, _) -> None  // nullary function -- see ArrayElem
    | IRTArrow (slots, result, identity)
        when slots |> List.forall (function SIdx _ -> true | _ -> false) ->
        let indexTypes = slots |> List.map (function SIdx i -> i | _ -> failwith "unreachable")
        Some {
            ElemType = result
            IndexTypes = indexTypes
            IsVirtual = false
            Identity = identity
        }
    | _ -> None

/// Virtual-array variant of ArrayElem. Matches IRTArrow with all-SIdxVirt
/// slots (non-empty). Useful for iteration codegen and range/reverse/blocked
/// dispatch.
let (|VirtualArrayElem|_|) (ty: IRType) =
    match ty with
    | IRTArrow ([], _, _) -> None  // nullary function -- see ArrayElem
    | IRTArrow (slots, result, _identity)
        when slots |> List.forall (function SIdxVirt _ -> true | _ -> false) ->
        let indexTypes = slots |> List.map (function SIdxVirt i -> i | _ -> failwith "unreachable")
        Some {
            ElemType = result
            IndexTypes = indexTypes
            IsVirtual = true
            Identity = None  // Virtual arrays don't carry identity
        }
    | _ -> None

/// Smart constructor for a stored array arrow. The slot list is the
/// array's index types each wrapped as `SIdx`, the result is the
/// element type, and identity is the caller-supplied handle.
let mkArrayArrow (indexTypes: IRIndexType list) (elemType: IRType) (identity: ArrayIdentity option) : IRType =
    IRTArrow (indexTypes |> List.map SIdx, elemType, identity)

/// Smart constructor for a virtual array arrow. Identity is forced to `None`
/// (virtual arrays don't materialize, so there's no handle to track).
///
/// Gate: invokes `validateArrowShape` and raises on any violation -- most
/// commonly an `IRTArrow` passed as `elemType` (virtual arrays must hold
/// simple values). This is a compiler-invariant check, not a user-facing
/// diagnostic; user-facing rejection (e.g. `reverse` of an array-of-arrays)
/// belongs earlier, in TypeCheck. A raise here means an invalid shape got
/// through upstream -- treat as a bug report on that path.
///
/// `mkArrayArrow`/`mkFuncArrow` don't gate: without any `SIdxVirt` neither
/// constraint can fire. If those ever admit mixed slots, move the gate here too.
let mkVirtualArrayArrow (indexTypes: IRIndexType list) (elemType: IRType) : IRType =
    let slots = indexTypes |> List.map SIdxVirt
    match validateArrowShape slots elemType with
    | [] -> IRTArrow (slots, elemType, None)
    | errs ->
        failwithf "mkVirtualArrayArrow: invalid virtual-array shape (compiler invariant violation):\n  %s\n  indexTypes count: %d, elemType: %A"
                  (System.String.Join("\n  ", errs))
                  indexTypes.Length
                  elemType

/// Smart constructor that takes an `IRArrayType` view (as returned by
/// `ArrayElem`) and produces the appropriate `IRTArrow` form. Dispatches
/// on `IsVirtual` to choose between `mkArrayArrow` (stored, all-SIdx)
/// and `mkVirtualArrayArrow` (virtual, all-SIdxVirt).
///
/// Used at form-update producer sites: `ArrayElem arr -> mkArrayLike { arr with ... }`.
/// The virtual/stored character is preserved through the rebuild.
let mkArrayLike (arr: IRArrayType) : IRType =
    // Rank-0 collapse: a zero-rank array equals its element. Prevents the
    // empty-slot `IRTArrow ([], _, _)` form, which is reserved for nullary
    // functions per mkFuncArrow. Without this guard, `mkArrayLike` with
    // empty IndexTypes would produce a shape ambiguous with a nullary
    // function and consumers via ArrayElem would reject it.
    if arr.IndexTypes.IsEmpty then
        arr.ElemType
    elif arr.IsVirtual then
        mkVirtualArrayArrow arr.IndexTypes arr.ElemType
    else
        mkArrayArrow arr.IndexTypes arr.ElemType arr.Identity

/// The kappa_k component array type of a Dist<order, elem, axes> (typed dist
/// tower, ppl/NOTES.md). kappa_1 is the mean tensor over the variable axes
/// as-declared; kappa_k for k >= 2 is the order-k joint cumulant, symmetric-
/// packed over the FUSED variable-axis space: one SymIdx record of Rank k
/// whose Extent is the product of the axes' extents. Used by the checker to
/// type cumulant(d, k) projections and by Zonk to ERASE IRTDist into the
/// component tuple.
let distComponentType (k: int) (elem: IRType) (axes: IRIndexType list) : IRType =
    if k = 1 then
        mkArrayArrow axes elem None
    else
        let fusedExtent =
            match axes with
            | [] -> IRLit (IRLitInt 0L)
            | [one] -> one.Extent
            | first :: rest ->
                rest |> List.fold (fun acc a ->
                    match acc, a.Extent with
                    | IRLit (IRLitInt m), IRLit (IRLitInt n) -> IRLit (IRLitInt (m * n))
                    | l, r -> IRBinOp (IRElementwise, IRMul, l, r)) first.Extent
        let symIdx = {
            Id = (axes |> List.tryHead |> Option.map (fun a -> a.Id) |> Option.defaultValue 0)
            Rank = k
            Extent = fusedExtent
            Symmetry = SymSymmetric
            Tag = None; IxKind = IxKPlain
            Kind = SDimension
            Dependencies = []
        }
        mkArrayArrow [symIdx] elem None

/// All component types kappa_1 .. kappa_order of a Dist -- the tuple a Dist
/// value erases to after type checking.
let distComponentTypes (order: int) (elem: IRType) (axes: IRIndexType list) : IRType list =
    [ for k in 1 .. order -> distComponentType k elem axes ]

/// The view transform behind `load_compound(var, mask)`: replace a
/// variable's dimensions with a single CompoundIdx whose presence mask is
/// `maskIR`. Pure type transform -- no data read; materialization happens
/// at `|> read`. The mask is an INPUT array, so no is_nan synthesis (Blade
/// core stays NaN-less).
///
/// The mask covers a LEADING PREFIX of the variable's dims, matched by
/// index-type Id; the masked prefix collapses into one CompoundIdx
/// (Rank = mask rank), the remainder stays regular trailing slots
/// (`Array<T like CompoundIdx<mask>, Idx<...>>`, formalism 4.5; all-dims
/// coverage gives scalar `Compound<T, RANK>`). The mask is ANY integer
/// array (NetCDF has no bool type; flag vars are NC_BYTE/NC_INT, nonzero =
/// present) -- `load_compound` is itself the signal that triggers the int ->
/// std::vector<bool> conversion at materialization. Reordered/non-prefix/
/// non-integer masks and ragged trailing dims are rejected or deferred.
let compoundViewType (freshId: IRId) (varArr: IRArrayType) (maskArr: IRArrayType) (maskIR: IRExpr) : Result<IRType, string> =
    let isMaskElem =
        match maskArr.ElemType with
        | IRTScalar ETInt64 | IRTScalar ETInt32 | IRTScalar ETBool -> true
        | _ -> false
    if not isMaskElem then
        Error (sprintf "load_compound: the mask must be an integer presence array (NetCDF stores flag/mask variables as NC_BYTE/NC_INT, read as Int; nonzero = present); got element type %A" maskArr.ElemType)
    else
        let varIdxs = varArr.IndexTypes
        let maskIdxs = maskArr.IndexTypes
        let maskRank = List.length maskIdxs
        // The mask must cover a LEADING prefix, in order; remaining dims are
        // regular trailing slots (maskRank = total -> empty trailing, scalar
        // Compound). Two dims "correspond" via INDEX-SPACE IDENTITY, not
        // equal extents (mirrors IR.indexSpacesMatch, replicated inline since
        // this precedes it): same Id (provider shares one index-type
        // instance per file), OR same non-anonymous Tag (a NAMED index type
        // matches across fresh Ids), OR both anonymous sharing a named-
        // reference extent (IRVar id / IRParam name). Bare literal-extent
        // equality does NOT match (formalism 14.6) -- shared identity must
        // be established by NAMING the index types.
        let dimsMatch (d: IRIndexType) (m: IRIndexType) : bool =
            if d.Id = m.Id then true
            else
                match d.Tag, m.Tag with
                | Some tagD, Some tagM -> tagD = tagM
                | None, None ->
                    (match d.Extent, m.Extent with
                     | IRVar (idD, _), IRVar (idM, _) -> idD = idM
                     | IRParam (nD, _, _), IRParam (nM, _, _) -> nD = nM
                     | _ -> false)
                | _ -> false
        let isLeadingPrefix =
            maskRank <= List.length varIdxs
            && List.forall2 dimsMatch (varIdxs |> List.truncate maskRank) maskIdxs
        if not isLeadingPrefix then
            let varIds = varIdxs |> List.map (fun i -> i.Id)
            let maskIds = maskIdxs |> List.map (fun i -> i.Id)
            Error (sprintf "compound/load_compound: the mask must cover a leading prefix of the array's dimensions, sharing index-space identity (same named index type, or same provider dimension). Mask and dense leading dimensions do not correspond (mask dim Ids %A vs array dim Ids %A). Name the shared index types (e.g. `type LatIdx = Idx<n>`) so the mask and dense array refer to the same index space; reordered or non-prefix masks are not yet supported" maskIds varIds)
        else
            let compoundIdx =
                { Id = freshId
                  Rank = maskRank
                  Extent = IRCompoundMask maskIR
                  Symmetry = SymNone
                  Tag = Some "__compoundidx"; IxKind = IxKCompound
                  Kind = SDimension
                  Dependencies = [] }
            // Trailing regular dims: the variable's dimensions after the masked
            // prefix. Empty in the all-dims case (scalar Compound<T, RANK>).
            let trailing = varArr.IndexTypes |> List.skip maskRank
            Ok (mkArrayArrow (compoundIdx :: trailing) varArr.ElemType varArr.Identity)

// Type-pattern matching (concrete + abstract) -- shared by the type-structure
// test harness and the language server's "type of expression" queries.
//
// `matchesTypePattern pattern actual` decides whether `actual` is an
// INSTANCE of `pattern` (CONCRETE = strict structural equality on identity
// dimensions; ABSTRACT = holes matching any filling). Deliberately NOT
// `unify` (symmetric, treats SymNone as compatible with any symmetry --
// wrong for an assertion) and NOT raw `=` (too strict: compares extents/
// inference ids/synthetic tags, none of which are type identity).
//
// Holes in the PATTERN: `IRTInfer _` (whole-type hole), `IRTNat None`
// (abstract type-level nat), an abstract index Extent (always ignored --
// extents are runtime values, never type identity). Everything else in the
// pattern matches concretely.
let rec matchesTypePattern (pattern: IRType) (actual: IRType) : bool =
    match pattern, actual with
    // Whole-type hole in the pattern matches anything.
    | IRTInfer _, _ -> true
    | IRTScalar e1, IRTScalar e2 -> e1 = e2
    | IRTNamed n1, IRTNamed n2 -> n1 = n2
    | IRTUnit, IRTUnit -> true
    | IRTNat _, IRTNat _ -> true       // nat-vs-nat: value is not type identity
    | ArrayElem p, ArrayElem a ->
        // Rank and virtual character are identity.
        p.IndexTypes.Length = a.IndexTypes.Length
        && p.IsVirtual = a.IsVirtual
        && List.forall2 matchesIndexPattern p.IndexTypes a.IndexTypes
        && matchesTypePattern p.ElemType a.ElemType
    | IRTTuple ps, IRTTuple as_ ->
        ps.Length = as_.Length && List.forall2 matchesTypePattern ps as_
    | FuncElem (pa, pr), FuncElem (aa, ar) ->
        pa.Length = aa.Length
        && List.forall2 matchesTypePattern pa aa
        && matchesTypePattern pr ar
    | IRTComputation p, IRTComputation a -> matchesTypePattern p a
    | _ -> pattern = actual   // fallback: exact structural equality

/// Per-index pattern match. Rank and Symmetry are type identity and must match
/// exactly UNLESS the pattern leaves them abstract:
///   - Rank = 0 in the pattern is the "any rank" hole.
///   - A user-meaningful Tag in the pattern must match; a None tag or a
///     synthetic `__` tag in the pattern is treated as "don't care".
/// Extent and Dependencies are NEVER compared (runtime / iteration detail).
/// Kind (S/T dimension) IS compared (it's part of how the dimension behaves).
and matchesIndexPattern (p: IRIndexType) (a: IRIndexType) : bool =
    let rankOk = p.Rank = 0 || p.Rank = a.Rank
    let symOk = p.Symmetry = a.Symmetry
    let kindOk = p.Kind = a.Kind
    let tagOk =
        match p.Tag with
        | None -> true
        | Some t when t.StartsWith("__") -> true
        | Some t -> (a.Tag = Some t)
    rankOk && symOk && kindOk && tagOk


//
// The IR admits two definitionally-equivalent encodings of the same type
// (formalism section 5.2): Nested (IRTArrow ([SIdx I; SIdx J], IRTArrow
// ([SVal P], R, None), Some id)) vs Flat (IRTArrow ([SIdx I; SIdx J; SVal P],
// R, Some id)). Producers emit nested forms exclusively, so this normalizer
// is currently a no-op on producer output; its value is a canonical form for
// decidable type equivalence, future-proofing for any mixed-slot producer,
// and making the section 5.2 identity an algorithm rather than an external
// proof obligation.
//
// `NormalizeMode` is a parameter so the canonical direction is a single
// choice-point. `ToFlat` is the eventual B-flat direction; currently stubbed.

/// Direction of canonical-form normalization.
///   - `ToNested`: split mixed-slot arrows at slot-kind boundaries into
///     nested uniform-kind arrows. Currently the committed canonical form.
///   - `ToFlat`: merge nested uniform-kind arrows into a single mixed-slot
///     arrow. Not yet implemented -- reserved for future B-flat migration.
type NormalizeMode =
    | ToNested
    | ToFlat

/// Kind discriminator for arrow slots, used for grouping consecutive
/// slots of the same kind during normalization. The integer values are
/// arbitrary; only equality matters.
let private slotKind (s: IRArrowSlot) : int =
    match s with
    | SIdx _ -> 0
    | SIdxVirt _ -> 1
    | SVal _ -> 2

/// True if all slots have the same kind. Vacuously true for an empty
/// list (which represents a nullary function per mkFuncArrow []).
let private isUniformKind (slots: IRArrowSlot list) : bool =
    match slots with
    | [] -> true
    | first :: rest ->
        let k = slotKind first
        rest |> List.forall (fun s -> slotKind s = k)

/// Group consecutive slots of the same kind into sub-lists. Order is
/// preserved. For an empty input, returns empty. For uniform input,
/// returns a single-group list.
let private groupConsecutiveByKind (slots: IRArrowSlot list) : IRArrowSlot list list =
    let rec loop (current: IRArrowSlot list) (acc: IRArrowSlot list list) (remaining: IRArrowSlot list) =
        match remaining with
        | [] ->
            match current with
            | [] -> List.rev acc
            | _ -> List.rev (List.rev current :: acc)
        | x :: xs ->
            match current with
            | [] -> loop [x] acc xs
            | y :: _ when slotKind x = slotKind y -> loop (x :: current) acc xs
            | _ -> loop [x] (List.rev current :: acc) xs
    loop [] [] slots

/// Normalize an IRType to the canonical form selected by `mode`; walks every
/// IRType subterm, splitting mixed-slot `IRTArrow`s at slot-kind boundaries
/// (ToNested) into a chain of nested uniform-kind arrows.
///
/// Identity propagation (ToNested split): the outermost split sub-arrow
/// inherits the original identity, inner sub-arrows get `None` -- identity
/// tracks a stored-array handle from program start, and inner sub-arrows are
/// either function residuals (no identity) or function-returned arrays
/// (identity unknown at type level).
///
/// `ToFlat` is not yet implemented and raises.
let rec normalize (mode: NormalizeMode) (ty: IRType) : IRType =
    match mode with
    | ToNested -> normalizeToNested ty
    | ToFlat -> failwith "normalize ToFlat: not yet implemented (reserved for B-flat migration)"

and normalizeToNested (ty: IRType) : IRType =
    match ty with
    | IRTArrow (slots, result, idOpt) ->
        // Recurse first: normalize result, and any IRType inside SVal slots.
        // Index types (SIdx, SIdxVirt) carry IRIndexType, which doesn't
        // contain IRType members -- opaque under this walker.
        let normResult = normalizeToNested result
        let normSlots =
            slots |> List.map (fun s ->
                match s with
                | SVal t -> SVal (normalizeToNested t)
                | SIdx _ | SIdxVirt _ -> s)
        // Now decide whether to split this arrow.
        if isUniformKind normSlots then
            // Already uniform; rebuild with normalized sub-parts.
            IRTArrow (normSlots, normResult, idOpt)
        else
            // Mixed slots -- split at kind boundaries into nested arrows.
            let groups = groupConsecutiveByKind normSlots
            match groups with
            | [] ->
                // Unreachable: isUniformKind is true for empty lists, so
                // we never enter this branch with [] slots.
                IRTArrow (normSlots, normResult, idOpt)
            | firstGroup :: restGroups ->
                // Build inner arrows right-to-left with None identity.
                let inner =
                    List.foldBack
                        (fun grp acc -> IRTArrow (grp, acc, None))
                        restGroups
                        normResult
                // Outermost arrow inherits the original identity.
                IRTArrow (firstGroup, inner, idOpt)

    // Compound types: recurse into substructure.
    | IRTTuple ts ->
        IRTTuple (ts |> List.map normalizeToNested)
    | IRTLoop lt ->
        IRTLoop { lt with
                    ArrayTypes = lt.ArrayTypes |> List.map normalizeToNested
                    KernelType = lt.KernelType |> Option.map normalizeToNested }
    | IRTComputation inner ->
        IRTComputation (normalizeToNested inner)
    | IRTPoly (baseT, var) ->
        IRTPoly (normalizeToNested baseT, var)
    | IRTIdxTagged (inner, tag) ->
        IRTIdxTagged (normalizeToNested inner, tag)
    | IRTUnitAnnotated (inner, units) ->
        IRTUnitAnnotated (normalizeToNested inner, units)
    | IRTDist (order, elem, axes) ->
        // Axes are IRIndexTypes (no IRType members) -- opaque under this walker.
        IRTDist (order, normalizeToNested elem, axes)

    // Leaf types -- no IRType subterms.
    | IRTScalar _
    | IRTUnit
    | IRTNat _
    | IRTNamed _
    | IRTInfer _
    | IRTGroupKeys _ -> ty

/// Structural equivalence on IRTypes, modulo the canonical (B-nested)
/// normalization: `true` iff `t1` and `t2` normalize to the same form under
/// `normalize ToNested`.
///
/// Bridges section 5.3 mixed-slot identity (flat mixed-slot arrows and their
/// split nested forms, e.g. `IRTArrow ([SIdx I, SVal A], R, _)` ==
/// `IRTArrow ([SIdx I], IRTArrow ([SVal A], R, _), _)`), but NOT section 5.2
/// array identity: `Array<T like I, J>` and `Array<Array<T like J> like I>`
/// are NOT equivalent here (ToNested only splits at slot-kind boundaries;
/// uniform-kind multi-slot is already canonical). That collapse becomes
/// available once `ToFlat` is implemented (B-flat migration).
///
/// Does NOT alpha-rename IRTInfer ids -- those are globally unique
/// unification handles, not bound variables.
let irTypeEquiv (t1: IRType) (t2: IRType) : bool =
    normalize ToNested t1 = normalize ToNested t2

/// Tuple elem type. Arrays of tuples. Useful for structured records that
/// don't have a named type definition.
let (|TupleElem|_|) (ty: IRType) =
    match ty with
    | IRTTuple ts -> Some ts
    | _ -> None

/// Pre-specialization parameter-pack type (`Poly<T^r>`): a base type + arity
/// variable that `specializeFunction` expands into `r` individual params at
/// compile time. Semantically a tuple of base types, not a container -- no
/// value-level representation outside the function-parameter position.
///
/// `Poly` in element position (`Array<Poly<T^k> like ...>`) has unclear
/// semantics (packs resolve at specialization time, array elements at
/// runtime); sites matching `PolyElem` should emit an explicit "not
/// implemented" error rather than silently doing the wrong thing.
let (|PolyElem|_|) (ty: IRType) =
    match ty with
    | IRTPoly (inner, var) -> Some (inner, var)
    | _ -> None

/// Elem types that aren't valid as array elements. These represent
/// runtime structures or compile-time-only constructs that have no
/// value-level meaning:
///   - IRTLoop: a loop object, not a value
///   - IRTComputation: deferred-computation wrapper
///   - IRTGroupKeys: opaque runtime CSR structure
let (|InvalidElem|_|) (ty: IRType) =
    match ty with
    | IRTLoop _
    | IRTComputation _
    | IRTGroupKeys _ -> Some ()
    | _ -> None


/// Strip unit annotation from a type, returning the bare type
let rec stripUnits (ty: IRType) : IRType =
    match ty with
    | IRTUnitAnnotated (inner, _) -> stripUnits inner
    | _ -> ty

/// Extract unit signature from a type, if present
let getUnits (ty: IRType) : UnitSig option =
    match ty with
    | IRTUnitAnnotated (_, units) -> Some units
    | _ -> None

/// Flatten nested tuple types: ((a, b), c) -> (a, b, c)
/// Makes left-folded tuples syntactically equivalent to flat tuples.
let rec flattenTupleType (ty: IRType) : IRType =
    match ty with
    | IRTTuple ts ->
        let flattened =
            ts |> List.collect (fun t ->
                match flattenTupleType t with
                | IRTTuple inner -> inner
                | other -> [other])
        IRTTuple flattened
    | _ -> ty

/// Extract the flat leaf types from a potentially nested tuple.
/// ((a, b), c) -> [a; b; c]
let rec flattenTupleLeaves (ty: IRType) : IRType list =
    match ty with
    | IRTTuple ts -> ts |> List.collect flattenTupleLeaves
    | _ -> [ty]

// Loop Structure (For Code Generation)

// Index Space Matching (for Partial Product Symmetry)

/// Information about an index space (for partial symmetry detection)
type IndexSpaceInfo = {
    Tag: string option
    Extent: IRExpr
    Symmetry: SymmetryClass
    Kind: DimensionKind
    SourceRank: int
}


/// Check if two index spaces are "shared" (same logical index space).
/// DIAGNOSTIC-ONLY: nominal index-space identity is NOT a symmetry license --
/// distinct arrays over the same named index type get NO triangular grouping
/// (proofs.md shared_units_insufficient; grouping requires array identity,
/// see rawAxisGroups). Kept for alignment diagnostics and future
/// nominal-typing checks.
let indexSpacesMatch (a: IndexSpaceInfo) (b: IndexSpaceInfo) : bool =
    if a.Kind <> SDimension || b.Kind <> SDimension then false
    else
        match a.Tag, b.Tag with
        | Some tagA, Some tagB -> tagA = tagB
        | None, None ->
            // Only match on named references (variables or parameters),
            // not on anonymous literal extents. Two arrays that happen to
            // have the same length don't share an index space.
            // See section 14.6: "commutativity is the license, shared index spaces
            // are the payoff" -- shared means same named type, not same extent.
            match a.Extent, b.Extent with
            | IRVar (idA, _), IRVar (idB, _) -> idA = idB
            | IRParam (nA, _, _), IRParam (nB, _, _) -> nA = nB
            | _ -> false
        | _ -> false

// Loop Level Structure

/// Represents a single loop level in the nested loop structure
/// The KIND of an index type -- the classification iteration/addressing and
/// other kind-specific logic dispatch on. Derived from Symmetry + Tag
/// (mirrors behaviorOf). Symmetry-like classes are ONE grouped arm (shared
/// triangular/simplex storage); Compound, Dep, Ragged are siblings with
/// their own storage/iteration. SymNone is NOT a kind -- "no class
/// assigned", resolving to plain dense only when no tag claims it. New
/// kinds (Enum, CG, ...) add an arm here.
let (|IxSymmetryLike|IxCompound|IxDep|IxRagged|IxDense|) (ix: IRIndexType) =
    match ix.Symmetry with
    // A wreath class groups with symmetry-like: compact storage over a
    // permutation group with a +-1 character needs canonicalization/compact
    // iteration. It is NOT a single simplex, so consumers reached through
    // this arm refuse it explicitly (orbitStorageUnsupported) instead of
    // falling into depth-1 triangular machinery -- IxDense would silently
    // miscompile (a dense walk over prod(ri) axes into a pool sized for the fold).
    | SymSymmetric | SymAntisymmetric | SymHermitian | SymWreath -> IxSymmetryLike
    | SymNone ->
        // Kind dispatch reads IxKind, never Tag strings (audit section 3.3) --
        // exhaustive, so a new IxKind case must decide its family here.
        (match ix.IxKind with
         // IxKSparse groups with compound: same tabulated storage/iteration
         // shape (materialized index, cardinality-bounded loop).
         | IxKCompound | IxKCompoundDynamic | IxKSparse -> IxCompound
         // IxKOrbit with Symmetry = SymNone is a MALFORMED record (the two
         // are stamped together by the one constructor; validateIR checks
         // Tag/IxKind agreement). Grouping it dense would give that record a
         // plausible dense reading, so it's refused via symmetry-like instead.
         | IxKOrbit -> IxSymmetryLike
         | IxKDep | IxKDepInner | IxKDepOuter -> IxDep
         | IxKRagged | IxKRaggedInline | IxKRaggedOpaque -> IxRagged
         | IxKPlain | IxKGroupOuter | IxKGroupMember | IxKSeq
         // IxKIrreps is dense BY DESIGN: every cell of the irreps space is
         // stored (extent = total_dim(spec), no compression); the block
         // structure is type identity, not a storage/iteration class.
         // IxKPgIrreps (the point-group member) is dense for exactly the same
         // reason -- extent = pg_total_dim(spec), every cell stored.
         | IxKIrreps | IxKPgIrreps
         | IxKErrorRaggedNoPrior | IxKErrorIrrepsBadSpec
         | IxKErrorPgIrrepsBadSpec -> IxDense)

type LoopLevelInfo = {
    ArrayIndex: int
    LocalDimIndex: int
    RankIndex: int
    GlobalLevelIndex: int
    IndexSpace: IndexSpaceInfo
    /// Joint product symmetry: Some factors marks this level as the FUSION of
    /// its argument's entire plain-dense S-block into one compound axis
    /// (extent = product of the factors' extents). Iteration decodes per-dim
    /// coordinates from the compound index (row-major order). Fusion makes
    /// cross-argument commutative grouping sound: one identity group licenses
    /// only the JOINT symmetry over whole argument index tuples (docs/
    /// formalism.md section 12.4, proofs.md diagonal_group_law) -- never
    /// per-dimension partnering (per_dim_swap_not_symmetry) or across
    /// distinct arrays via shared index spaces (shared_units_insufficient).
    FusedFactors: IRIndexType list option
}

/// Compute per-array S-dimension counts (accounting for arity expansion)
let computeSDimsPerArray (arrayTypes: IRArrayType list) : int list =
    arrayTypes |> List.map (fun arr ->
        arr.IndexTypes 
        |> List.filter (fun idx -> idx.Kind = SDimension) 
        |> List.sumBy (fun idx -> idx.Rank))

/// Build the RAW (by-array, unreordered) loop levels: one level per S-dimension
/// arity component, emitted array-by-array in index-type order. Pre-grouping
/// structure; product-symmetry reordering is applied separately by
/// buildLoopLevelStructure so the grouping rule lives in one place
/// (rawAxisGroups) and the reorder cannot drift from it.
let buildRawLoopLevels (arrayTypes: IRArrayType list) (sDimsPerArray: int list) : LoopLevelInfo list =
    let mutable levels = []
    let mutable globalIdx = 0
    
    for arrIdx in 0 .. arrayTypes.Length - 1 do
        let arr = arrayTypes.[arrIdx]
        let mutable localDimIdx = 0
        // Cumulative depth of levels emitted for THIS array so far. RankIndex
        // must be this cumulative depth, not the per-record arity position --
        // genElementBindingNew uses `levelsConsumed = RankIndex + 1` to decide
        // slice-vs-scalar-leaf, and a multi-arity record (SymIdx<2>) and a
        // sequence of rank-1 records span the same number of levels, so depth
        // must increment continuously across records.
        let mutable arrLevel = 0

        for idx in arr.IndexTypes do
            // A wreath slot needs the SEGMENT-PEELED nest (plan-orbidx-
            // bijections section 2), not ordinary loop levels: emitting
            // `idx.Rank` levels here would walk the raw axes densely (or as a
            // single simplex) and fill the wrong number of cells in the
            // wrong order.
            if idx.Symmetry = SymWreath then
                failwith (orbitStorageUnsupported "loop construction (buildRawLoopLevels)"
                                                  (orbitLevelsOf idx))
            if idx.Kind = SDimension then
                // A CompoundIdx is a SINGLE semantic axis -- it iterates its
                // present cells, not a dense grid over the mask's dimensions --
                // so it contributes exactly ONE loop level regardless of mask
                // rank; SourceRank carries the mask rank for the codegen
                // consumer that emits the compacted bound/address.
                let levelCount = match idx with | IxCompound -> 1 | _ -> idx.Rank
                for _compIdx in 0 .. levelCount - 1 do
                    levels <- levels @ [{
                        ArrayIndex = arrIdx
                        LocalDimIndex = localDimIdx
                        RankIndex = arrLevel
                        GlobalLevelIndex = globalIdx
                        IndexSpace = {
                            Tag = idx.Tag
                            Extent = idx.Extent
                            Symmetry = idx.Symmetry
                            Kind = idx.Kind
                            SourceRank = idx.Rank
                        }
                        FusedFactors = None
                    }]
                    globalIdx <- globalIdx + 1
                    arrLevel <- arrLevel + 1
                localDimIdx <- localDimIdx + 1
    
    levels

/// Joint product symmetry: fuse each eligible argument's plain-dense multi-
/// level S-block into a SINGLE compound loop level, so cross-argument
/// commutative grouping operates on whole argument index tuples -- the JOINT
/// symmetry, the only symmetry a single identity group licenses (docs/
/// formalism.md section 12.4; proofs.md diagonal_group_law). Per-dimension
/// partnering (SymIdx per data dimension, claiming (r!)^d) is unsound
/// (per_dim_swap_not_symmetry, counting_general_C).
///
/// Eligibility (all required): the argument sits in a comm group with
/// another SAME-array position (identity; shared index spaces license
/// nothing: shared_units_insufficient); it contributes >= 2 S-levels, ALL
/// rank-1 DENSE-STORED records (SymNone, no dependencies, IxKind in
/// {IxKPlain, IxKIrreps} -- symmetric/ragged/dep/compound records need
/// unrank decode and don't fuse); the source is a real array. Identity
/// partners share an array type, so eligibility is uniform across a group.
///
/// IxKIrreps unlocks `comm` over multi-axis irreps arrays: an irreps axis is
/// DENSE BY DESIGN (extent = total_dim(spec) = cardinality, no compaction
/// bijection), so the fused axis is an honest row-major product and section
/// 12.4's joint doctrine applies verbatim -- the block structure is TYPE
/// IDENTITY, not a storage class. Excluded: a SYMMETRIC factor stores only
/// canonical cells (extent != cardinality), so its sound joint form is the
/// wreath product, not S_r over a flat compound. Widening past IxKind is
/// NOT sound.
let fuseJointSLevels
    (identities: ArrayIdentity list)
    (commGroups: int list list)
    (arrayTypes: IRArrayType list)
    (rawLevels: LoopLevelInfo list) : LoopLevelInfo list =
    let hasIdentityPartner k =
        commGroups |> List.exists (fun cg ->
            List.contains k cg &&
            cg |> List.exists (fun q ->
                q <> k && q < identities.Length && k < identities.Length &&
                sameIdentity identities.[k] identities.[q]))
    let sRecordsByArray =
        arrayTypes |> List.map (fun arr ->
            arr.IndexTypes |> List.filter (fun idx -> idx.Kind = SDimension))
    let productExtent (es: IRExpr list) : IRExpr =
        es |> List.reduce (fun a b ->
            match a, b with
            | IRLit (IRLitInt x), IRLit (IRLitInt y) -> IRLit (IRLitInt (x * y))
            | _ -> IRBinOp (IRElementwise, IRMul, a, b))
    let fused =
        rawLevels
        |> List.groupBy (fun l -> l.ArrayIndex)
        |> List.collect (fun (arrIdx, lvls) ->
            let recs = if arrIdx < sRecordsByArray.Length then sRecordsByArray.[arrIdx] else []
            let isVirtual = arrIdx < arrayTypes.Length && arrayTypes.[arrIdx].IsVirtual
            // Dense rank-1 factors only: IxKPlain/IxKIrreps are the two kinds
            // whose extent IS their cardinality -- everything else compacts,
            // varies per row, or is synthetic, none of which decode as a
            // row-major product. Symmetry must independently be SymNone (a
            // symmetric record is the deferred wreath-product case).
            // IxKPgIrreps meets the same premise but is NOT admitted: fusion
            // is an OPTIMIZATION, so skipping it only costs the joint
            // compound axis, and not fusing is the safe default.
            let isDenseFusableKind (k: IxKind) =
                match k with
                | IxKPlain | IxKIrreps -> true
                | _ -> false
            let allPlainDense =
                recs.Length = lvls.Length &&
                recs |> List.forall (fun r ->
                    r.Rank = 1 && r.Symmetry = SymNone && isDenseFusableKind r.IxKind &&
                    List.isEmpty r.Dependencies)
            if not isVirtual && lvls.Length >= 2 && hasIdentityPartner arrIdx && allPlainDense then
                let rep = List.head lvls
                // The fused axis is ANONYMOUS: Tag = None (and, on the output
                // record deduceOutputType builds from these factors,
                // IxKind = IxKPlain). A batch x irreps product is NOT an irreps
                // space: the compound coordinate mixes a representation index
                // with a non-representation one, so no spec describes it.
                [ { rep with
                      LocalDimIndex = 0
                      RankIndex = 0
                      IndexSpace =
                        { Tag = None
                          Extent = productExtent (recs |> List.map (fun r -> r.Extent))
                          Symmetry = SymNone
                          Kind = SDimension
                          SourceRank = recs.Length }
                      FusedFactors = Some recs } ]
            else lvls)
    fused |> List.mapi (fun i lv -> { lv with GlobalLevelIndex = i })

/// THE single canonical grouping rule, operating on RAW (post-fusion) levels.
/// Assigns each level an axis-group id (in first-appearance order). Two levels
/// share a group iff they are product-symmetric partners under either
/// multiplicity axis: (A) WITHIN one index type -- consecutive arity
/// components of a single symmetric/antisymmetric record (same array, same
/// LocalDimIndex, consecutive RankIndex); (B) ACROSS arguments -- same comm
/// group AND SAME ARRAY (identity) AND each side's S-block is a single level
/// (d = 1, or fused). Identity is REQUIRED (docs/formalism.md section
/// 11.2/12.4): a shared named index space alone licenses nothing
/// (shared_units_insufficient), and per-dimension partnering of a multi-dim
/// identity group is unsound (per_dim_swap_not_symmetry); multi-dim groups
/// reach here only through fusion, as whole-tuple axes. Both the loop
/// reorder/iteration AND the output storage layout derive from this one
/// function, so they cannot drift apart.
let rawAxisGroups
    (identities: ArrayIdentity list)
    (commGroups: int list list)
    (rawLevels: LoopLevelInfo list) : int list =
    let inSameCommGroup i j =
        commGroups |> List.exists (fun cg -> List.contains i cg && List.contains j cg)
    let sameArrayIdentity i j =
        i < identities.Length && j < identities.Length &&
        sameIdentity identities.[i] identities.[j]
    let sLevelCount =
        let counts = rawLevels |> List.countBy (fun l -> l.ArrayIndex) |> Map.ofList
        fun arrIdx -> match counts.TryFind arrIdx with Some c -> c | None -> 0
    let mergesWith (lv: LoopLevelInfo) (prior: LoopLevelInfo) : bool =
        let withinType =
            lv.ArrayIndex = prior.ArrayIndex &&
            lv.LocalDimIndex = prior.LocalDimIndex &&
            lv.RankIndex = prior.RankIndex + 1 &&
            // SymWreath is DELIBERATELY absent and BYPASSED, not accidentally
            // unmatched: `buildRawLoopLevels` refuses a wreath INPUT outright,
            // and a wreath-producing application never reaches this function
            // (deduceWreathTie fires first; its iteration is the segment-
            // peeled `orb_visit` nest). If one ever did arrive, this merge
            // would flatten its prod(ri) raw axes into ONE triangular group --
            // and since this function also drives STORAGE layout, that would
            // silently allocate a single-simplex pool for a nested class.
            (lv.IndexSpace.Symmetry = SymSymmetric ||
             lv.IndexSpace.Symmetry = SymAntisymmetric ||
             lv.IndexSpace.Symmetry = SymHermitian)
        let acrossArray =
            inSameCommGroup lv.ArrayIndex prior.ArrayIndex &&
            lv.LocalDimIndex = prior.LocalDimIndex &&
            sameArrayIdentity lv.ArrayIndex prior.ArrayIndex &&
            sLevelCount lv.ArrayIndex = 1 &&
            sLevelCount prior.ArrayIndex = 1
        withinType || acrossArray
    let arr = List.toArray rawLevels
    let groupOf = Array.create arr.Length -1
    let mutable nextGroup = 0
    for gi in 0 .. arr.Length - 1 do
        let prior = [ gi - 1 .. -1 .. 0 ] |> List.tryFind (fun j -> mergesWith arr.[gi] arr.[j])
        match prior with
        | Some j -> groupOf.[gi] <- groupOf.[j]
        | None ->
            groupOf.[gi] <- nextGroup
            nextGroup <- nextGroup + 1
    List.ofArray groupOf

/// Build the loop level structure: fuse joint S-blocks (fuseJointSLevels),
/// then REORDER so levels sharing an axis group (rawAxisGroups) are
/// CONTIGUOUS, grouped by axis rather than by array. Grouped output storage
/// lays its symmetric dims out adjacently (e.g. joint SymIdx<2, Lat*Lon>
/// spans its two fused levels back-to-back); the loop nest must visit dims
/// in the SAME order for the write subscript and triangular bounds to line
/// up with storage. Both this reorder and deduceOutputType derive their
/// ordering from rawAxisGroups, so iteration and storage cannot disagree.
/// STABLE group-by (each group keeps its by-array relative order), so
/// single-axis/single-array cases reorder as an identity.
let buildLoopLevelStructure
    (identities: ArrayIdentity list)
    (commGroups: int list list)
    (arrayTypes: IRArrayType list)
    (sDimsPerArray: int list) : LoopLevelInfo list =
    let raw0 = buildRawLoopLevels arrayTypes sDimsPerArray
    let raw = fuseJointSLevels identities commGroups arrayTypes raw0
    let groups = rawAxisGroups identities commGroups raw
    // Bucket order = first appearance of each group id; stable within bucket.
    let keyed = List.zip groups raw
    let bucketOrder =
        groups |> List.fold (fun acc g -> if List.contains g acc then acc else acc @ [g]) []
    let reordered =
        bucketOrder
        |> List.collect (fun g -> keyed |> List.filter (fun (gg, _) -> gg = g) |> List.map snd)
    reordered |> List.mapi (fun i lv -> { lv with GlobalLevelIndex = i })

// Identity Group Detection

type IdentityGroup = {
    StartIndex: int
    Rank: int
    Identity: ArrayIdentity
}

let partitionIntoIdentityGroups (identities: ArrayIdentity list) : IdentityGroup list =
    match identities with
    | [] -> []
    | first :: rest ->
        let mutable groups = []
        let mutable currentGroup = { StartIndex = 0; Rank = 1; Identity = first }
        
        for i, id in List.indexed rest do
            if sameIdentity id currentGroup.Identity then
                currentGroup <- { currentGroup with Rank = currentGroup.Rank + 1 }
            else
                groups <- groups @ [currentGroup]
                currentGroup <- { StartIndex = i + 1; Rank = 1; Identity = id }
        
        groups @ [currentGroup]

// Consolidated Symmetry Analysis (Section 13.1-13.2, 14.5-14.6)

/// Helper: factorial
let factorial n = 
    let rec f acc = function
        | 0 | 1 -> acc
        | n -> f (acc * int64 n) (n - 1)
    f 1L n

/// Compute SymcomState for all loop levels: SYMMETRIC (consecutive arity
/// components of the same SymIdx) and COMMUTATIVE (arrays in the same comm
/// group with matching index spaces).
let computeAllSymcomStates
    (identities: ArrayIdentity list)
    (arrayTypes: IRArrayType list)
    (commGroups: int list list)
    (sDimsPerArray: int list) : SymcomState list =

    // A WREATH class never reaches here: `deduceWreathTie` fires first at the
    // typecheck seam, an UNTIED wreath input is refused at the same seam, and
    // `buildRawLoopLevels` refuses one outright as a backstop. The two
    // `Symmetry = SymSymmetric || SymAntisymmetric` tests below mirror
    // rawAxisGroups.mergesWith.withinType and would go equally wrong on a
    // wreath: prod(ri) raw axes read as a single shrinking simplex, silently.
    let levels = buildLoopLevelStructure identities commGroups arrayTypes sDimsPerArray
    if levels.IsEmpty then []
    else
        let inSameCommGroup i j =
            commGroups |> List.exists (fun cg ->
                List.contains i cg && List.contains j cg)

        let sameArrayIdentity i j =
            i < identities.Length && j < identities.Length &&
            sameIdentity identities.[i] identities.[j]

        // Commutative licensing: identity required (shared index spaces
        // license nothing -- shared_units_insufficient), and each
        // side's S-block must be a single level (d = 1 or fused): mirrors
        // rawAxisGroups.mergesWith.acrossArray exactly.
        let sLevelCount =
            let counts = levels |> List.countBy (fun l -> l.ArrayIndex) |> Map.ofList
            fun arrIdx -> match counts.TryFind arrIdx with Some c -> c | None -> 0

        let countSymmetricGroup (level: LoopLevelInfo) =
            if level.IndexSpace.Symmetry <> SymSymmetric && level.IndexSpace.Symmetry <> SymAntisymmetric then 1
            else
                let mutable count = 1
                let mutable idx = level.GlobalLevelIndex - 1
                while idx >= 0 do
                    let priorLevel = levels.[idx]
                    if priorLevel.ArrayIndex = level.ArrayIndex &&
                       priorLevel.LocalDimIndex = level.LocalDimIndex &&
                       priorLevel.RankIndex = levels.[idx + 1].RankIndex - 1 then
                        count <- count + 1
                        idx <- idx - 1
                    else
                        idx <- -1
                count
        
        let countCommutativeGroup (level: LoopLevelInfo) =
            let mutable count = 1
            let mutable idx = level.GlobalLevelIndex - 1
            while idx >= 0 do
                let priorLevel = levels.[idx]
                let thisLevel = if idx = level.GlobalLevelIndex - 1 then level else levels.[idx + 1]
                let canContinue =
                    inSameCommGroup thisLevel.ArrayIndex priorLevel.ArrayIndex &&
                    sameArrayIdentity thisLevel.ArrayIndex priorLevel.ArrayIndex &&
                    sLevelCount thisLevel.ArrayIndex = 1 &&
                    sLevelCount priorLevel.ArrayIndex = 1
                if canContinue then
                    count <- count + 1
                    idx <- idx - 1
                else
                    idx <- -1
            count
        
        levels |> List.mapi (fun globalIdx level ->
            if globalIdx = 0 then SCNeither
            else
                let priorLevel = levels.[globalIdx - 1]
                
                let isSymmetric =
                    level.ArrayIndex = priorLevel.ArrayIndex &&
                    level.LocalDimIndex = priorLevel.LocalDimIndex &&
                    level.RankIndex = priorLevel.RankIndex + 1 &&
                    (level.IndexSpace.Symmetry = SymSymmetric ||
                     level.IndexSpace.Symmetry = SymAntisymmetric)
                
                let isCommutative =
                    inSameCommGroup level.ArrayIndex priorLevel.ArrayIndex &&
                    sameArrayIdentity level.ArrayIndex priorLevel.ArrayIndex &&
                    sLevelCount level.ArrayIndex = 1 &&
                    sLevelCount priorLevel.ArrayIndex = 1
                
                match isSymmetric, isCommutative with
                | false, false -> SCNeither
                | true, false -> SCSymmetric
                | false, true -> SCCommutative
                | true, true ->
                    let symRank = countSymmetricGroup level
                    let commRank = countCommutativeGroup level
                    if factorial symRank >= factorial commRank then SCSymmetric 
                    else SCCommutative)

/// CANONICAL AXIS GROUPING -- the single source of dimension grouping that
/// both OUTPUT STORAGE (deduceOutputType) and ITERATION (buildLoopLevelStructure
/// reorder / iminMap chaining) derive from, so the two cannot drift apart.
///
/// Returns one group id per loop level (parallel to buildLoopLevelStructure's
/// output order). Two levels share a group iff joint-symmetric partners --
/// iterate/store as one higher-rank symmetric index -- under either axis:
///   (A) WITHIN one index type: consecutive arity components of a single
///       symmetric/antisymmetric record (a SymIdx<r> spans r levels, same
///       LocalDimIndex, consecutive RankIndex).
///   (B) ACROSS arguments: the SAME ARRAY repeated in a commutative group,
///       each occurrence's S-block one level (d = 1, or fused for d >= 2).
///       Array identity is REQUIRED -- nominal identity across distinct
///       arrays licenses nothing (shared_units_insufficient) -- and a
///       multi-dim identity group forms ONE joint group, never one per data
///       dimension (per_dim_swap_not_symmetry).
let computeAxisGroups
    (identities: ArrayIdentity list)
    (arrayTypes: IRArrayType list)
    (commGroups: int list list)
    (sDimsPerArray: int list) : int list =
    // Group ids parallel to buildLoopLevelStructure's REORDERED output order,
    // via the one shared grouping rule (rawAxisGroups): the reorder is itself
    // a stable group-by on the same rule, so contiguous same-group runs come
    // out with contiguous ids -- what the iteration consumers index by.
    let levels = buildLoopLevelStructure identities commGroups arrayTypes sDimsPerArray
    rawAxisGroups identities commGroups levels

/// Determine which loop levels can use triangular iteration. A level iterates
/// triangularly iff it is a non-first member of its canonical axis group (the
/// first member is the group's root, iterated fully; each later member descends
/// relative to its predecessors). Derives from the single computeAxisGroups
/// analysis so this stays in lock-step with the iminMap chaining and the output
/// storage layout.
let computeTriangularLevels
    (arrayTypes: IRArrayType list)
    (identities: ArrayIdentity list)
    (commGroups: int list list)
    (sDimsPerArray: int list) : bool list =

    let groupIds = computeAxisGroups identities arrayTypes commGroups sDimsPerArray
    let seen = System.Collections.Generic.HashSet<int>()
    groupIds |> List.map (fun g ->
        let priorMember = seen.Contains g
        seen.Add g |> ignore
        priorMember)

/// Compute the iteration-count speedup from the canonical axis grouping: each
/// axis group of size g >= 2 is one joint simplex contributing g!; distinct
/// groups multiply. One identity group over a d-dimensional array yields a
/// single fused group of size r -- speedup r!, never (r!)^d
/// (per_dim_swap_not_symmetry, counting_general_C); multiplicative factors
/// come only from DISTINCT groups (separate comm groups or within-record
/// symmetric blocks). docs/formalism.md section 12.4.
let computePartialProductSpeedup
    (arrayTypes: IRArrayType list)
    (identities: ArrayIdentity list)
    (commGroups: int list list)
    (sDimsPerArray: int list) : int64 =
    let levels = buildLoopLevelStructure identities commGroups arrayTypes sDimsPerArray
    let groups = rawAxisGroups identities commGroups levels
    groups
    |> List.countBy id
    |> List.fold (fun acc (_, size) -> if size >= 2 then acc * factorial size else acc) 1L

/// Compute the lower bound for triangular iteration
let computeTriangularBound 
    (loopIndex: int) 
    (priorIndices: IRId list) 
    (extent: IRExpr) 
    (state: SymcomState) : IRExpr =
    
    match state with
    | SCNeither -> IRLit (IRLitInt 0L)
    | SCSymmetric | SCCommutative | SCBoth ->
        match priorIndices with
        | [] -> IRLit (IRLitInt 0L)
        | lastIdx :: _ -> IRVar (lastIdx, IRTScalar ETInt64)

// IR Declarations

type IRTypeDef =
    | IRTDAlias of name: string * ty: IRType
    // Struct where-constraints don't live on the type def: the checker
    // synthesizes IRConstraintCheck guards at every assignment site.
    | IRTDStruct of name: string * fields: (string * IRType) list
    | IRTDVariant of name: string * variants: (string * IRType option) list
    | IRTDIndexType of name: string * idx: IRIndexType
    | IRTDEnumIdx of name: string * idx: IRIndexType * values: EnumValue list
      // Named index type declaration, e.g. "type lat = Idx<180>"
      // Provides nominal identity: two arrays sharing module.lat
      // have the same index space.  Future: schemas can supply these
      // so that multiple files share the same nominal types.

/// Helpers for EnumValue lists. allInt/allString classify a values list;
/// underlyingElemType produces the corresponding ElemType for codegen
/// (used for both the `using <name> = ...;` alias and the reverse-lookup
/// comparison op).
module EnumValue =
    let allInt (vs: EnumValue list) =
        vs |> List.forall (function EVInt _ -> true | _ -> false)
    let allString (vs: EnumValue list) =
        vs |> List.forall (function EVString _ -> true | _ -> false)
    let underlyingElemType (vs: EnumValue list) : ElemType =
        if allString vs && not vs.IsEmpty then ETString
        else ETInt64  // empty or all-int both default to int64

/// Top-level binding
type IRBinding = {
    Id: IRId
    Name: string
    Type: IRType
    Value: IRExpr
    IsConst: bool
    IsMutable: bool
}

/// IR Module
/// Specification for a deferred provider data read, recovered at the read site
/// (`view |> alias.read`) and consumed at codegen to emit the provider's
/// reader. Keyed in IRModule.ProviderReads by the receiving binding's IRId. A
/// plain dense read leaves MaskName/MaskType = None; a load_compound read
/// carries both. Provider is the registry module name ("netcdf", "zarr") --
/// codegen dispatches the emitters through it.
type ProviderReadSpec = {
    Provider: string
    FilePath: string
    VarName: string
    VarType: IRArrayType
    MaskName: string option
    MaskType: IRArrayType option
    /// Sub-simplex window [lo, hi) for `alias.read_window(var, lo, hi)`
    /// over a packed variable; None for whole-variable reads. When set,
    /// VarType is the WINDOW type (leading packed extent = hi-lo).
    Window: (int64 * int64) option
    /// `alias.stream(var)`: not materialized at the binding -- consuming loop
    /// nests inline per-fiber reads at the S/T boundary. Only fiber-kernel
    /// method_for consumers are stream-eligible; other consumption is a
    /// loud codegen error steering to `.read`.
    Streamed: bool
}

/// Specification for a deferred provider data write (`alias.write("path", A)`),
/// keyed in IRModule.ProviderWrites by the write binding's IRId. The source
/// must be a named top-level array binding; codegen emits a flatten prologue
/// (nested -> `<cpp>_flat`, row-major) and then the provider's writer.
type ProviderWriteSpec = {
    Provider: string
    FilePath: string
    /// Variable name inside the store (the source binding's surface name).
    VarName: string
    /// The source array binding being written.
    SourceId: IRId
    SourceType: IRArrayType
    /// Dimension names for the store's metadata (named index types when
    /// known; synthesized dim<i> otherwise).
    DimNames: string list
}

/// Deferred array-fill constructor spec, keyed in RandomInits by the receiving
/// binding's IRId. `fill_random(mod)` records a FillModulus (rand() % mod, C
/// rand(), nondeterministic); `rand.uniform/normal(key)` records a RandGen
/// (deterministic mt19937_64-based runtime, keyed by `key`). Both allocate the
/// binding's array type and fill its pool at codegen.
type RandomFillSpec =
    | FillModulus of IRExpr              // fill_random(mod)
    | RandGen of kind: string * key: IRExpr   // rand.<kind>(key), kind = "uniform" | "normal"

type IRModule = {
    Name: string
    Types: IRTypeDef list
    Functions: IRFuncDef list
    Bindings: IRBinding list
    /// Diagnostics: static function usage tracking (function name -> usage kind)
    /// "compile-time" | "runtime" | "both" | "unused"
    StaticFunctionUsage: Map<string, string>
    /// Deferred provider reads, keyed by the receiving binding's IRId.
    /// Populated during lowering, consumed at codegen for the provider reader.
    ProviderReads: Map<IRId, ProviderReadSpec>
    /// Deferred provider writes (`alias.write("path", A)`), keyed by the
    /// write binding's IRId; consumed at codegen for a flatten prologue + writer.
    ProviderWrites: Map<IRId, ProviderWriteSpec>
    /// Deferred random-fill array constructors, keyed by the receiving
    /// binding's IRId (`fill_random(mod)` or `rand.uniform/normal(key)`);
    /// consumed at codegen to emit allocate<> + a pool fill.
    RandomInits: Map<IRId, RandomFillSpec>
    /// Deferred compound-construction constructors (compound(dense, mask)),
    /// keyed by the receiving binding's IRId; consumed at codegen for P0
    /// index materialization + a dense->compact scatter. Value is (loweredDense, loweredMask).
    CompoundInits: Map<IRId, IRExpr * IRExpr>
    /// Deferred sparse-construction constructors (sparse(values, keys)),
    /// keyed by the receiving binding's IRId; consumed at codegen for index
    /// materialization + a straight pool copy (key order, no scatter). The
    /// keys source rides the binding TYPE's IRSparseKeys extent.
    SparseInits: Map<IRId, IRExpr>
    /// Block-level `let mut` bindings of ARRAY type, by binding IRId. IRLet
    /// erases the surface mutability flag, but a mut binding initialized from
    /// an existing array (`let mut a = Z`) must DEEP-COPY the storage --
    /// binding by value shares the data pointer, so mutation through `a`
    /// would silently corrupt `Z`. CodeGen/interpreter deep-copy at the
    /// binding site when the id is in this set.
    MutableArrayLets: Set<IRId>
    /// EMISSION-ORDER PROXY for functions a compiler pass SYNTHESIZED from an
    /// existing one: `derived id -> origin id`. Codegen emits bindings and
    /// functions interleaved in IRId order (a lower id means "written earlier
    /// in the source"), which is load-bearing for every `computeMainLocalFuncIds`
    /// main-local function: those are `std::function` LOCALS inside `main()`
    /// with no forward declaration, so a use before definition is a hard C++
    /// error. A pass minting a copy with `builder.FreshId()` gets the LARGEST
    /// id and sorts after every call site it just rewrote -- exactly the
    /// failure mode that broke `ml-equiv`/`sgs`.
    ///
    /// Contract: the derived function is placed IMMEDIATELY AFTER its origin
    /// in `Functions`, and codegen keys order on the ORIGIN's id, so a copy
    /// is emitted at exactly its origin's program point -- no per-pass
    /// main-locality reasoning required. Empty for modules with no
    /// synthesized copies; populated by `shapeMonomorphizeModule`.
    DerivedFuncOrigins: Map<IRId, IRId>
}

/// IR Program
type IRProgram = {
    Modules: IRModule list
}

/// Query: the fully-deduced IR type of a top-level binding by name, searched
/// across all modules of a lowered program. Shallow accessor intended for reuse
/// by the language server (hover / inline type) and the type-structure test
/// harness. Returns None if no binding with that name exists.
let bindingTypeByName (program: IRProgram) (name: string) : IRType option =
    program.Modules
    |> List.tryPick (fun m ->
        m.Bindings |> List.tryFind (fun b -> b.Name = name) |> Option.map (fun b -> b.Type))


// IR Construction Helpers

type IRBuilder() =
    let mutable nextId = 0
    let mutable nextInferId = 0
    
    member _.FreshId() =
        let id = nextId
        nextId <- nextId + 1
        id

    member _.CurrentId() = nextId
    /// Raise the id floor so ids minted here can never collide with ids
    /// minted by an earlier builder (codegen builds a fresh IRBuilder and
    /// must not reuse typecheck/lowering-era ids -- a synthetic binding
    /// registered under a reused id hijacks the original variable's
    /// rendered name).
    member _.EnsureAtLeast(n: int) = if nextId < n then nextId <- n
    member _.Reset() = 
        nextId <- 0
        nextInferId <- 0
    member this.MkVar(ty) = IRVar (this.FreshId(), ty)
    
    member _.FreshInferType() = 
        let id = nextInferId
        nextInferId <- nextInferId + 1
        IRTInfer id
    
    member this.MkLet(value, bodyFn) =
        let id = this.FreshId()
        IRLet(id, value, bodyFn id)

/// Caller-supplied fields that distinguish a named source-level function
/// from an anonymous lambda when building an IRCallable. Everything else
/// (params, body, captures, commutativity, parallelism) is the same for
/// both and is passed positionally to `mkCallable`.
///
///   - NameOverride: Some name for a source function or a named let-bound
///     lambda; None synthesizes "__lambda_<id>" for a truly anonymous one.
///   - IdOverride: Some id when the callable's IRId was allocated up front
///     (a source function's FuncId, or a named lambda that must be bound in
///     scope before its body is lowered for self-reference); None allocates
///     a fresh id here.
///   - IsStatic / IsArityPoly / ArityParam: function-only metadata; the
///     anonymous-lambda default (`defaultLambdaOptions`) leaves them off.
type CallableOptions =
    { NameOverride : string option
      IdOverride   : IRId option
      IsStatic     : bool
      IsArityPoly  : bool
      ArityParam   : string option }

/// Options for an anonymous lambda: no name, fresh id, no function-only
/// metadata. The single construction point for every lambda-shaped callable
/// in Lowering.fs (real lambdas, operator sections, partial applications,
/// synthesized binop kernels).
let defaultLambdaOptions : CallableOptions =
    { NameOverride = None; IdOverride = None; IsStatic = false; IsArityPoly = false; ArityParam = None }

/// The single builder for an IRCallable -- the merged construction point for
/// source-level functions and lambdas. `opts` carries the fields that differ
/// between the two (name, id, static, arity-poly); captures, return type,
/// commutativity, and parallelism are caller-supplied per callable (omp/
/// cuda/mpi clauses from either a where-clause or a strategy list flow
/// through identically for both kinds).
let mkCallable
    (builder: IRBuilder)
    (opts: CallableOptions)
    (parms: IRParam list)
    (body: IRExpr)
    (retType: IRType)
    (captures: CaptureInfo list)
    (isCommutative: bool)
    (commGroups: int list list)
    (parallelism: (int * int) list)
    (isOmpParallel: bool)
    (isCudaKernel: bool)
    (cudaBlockSize: int)
    (isMpiParallel: bool)
    : IRCallable =
    let id = match opts.IdOverride with Some i -> i | None -> builder.FreshId()
    {
        Id = id
        Name = match opts.NameOverride with Some n -> n | None -> sprintf "__lambda_%d" id
        Params = parms
        RetType = retType
        Body = body
        IsStatic = opts.IsStatic
        IsCommutative = isCommutative
        CommGroups = commGroups
        // Declared antisymmetry is grafted on by the ONE construction site
        // that has it (Lowering.lowerTypedLambda, from the lambda's
        // where-clause); every other callable-building site is comm-only.
        AntisymGroups = []
        Parallelism = parallelism
        IsOmpParallel = isOmpParallel
        IsCudaKernel = isCudaKernel
        CudaBlockSize = cudaBlockSize
        IsMpiParallel = isMpiParallel
        IsArityPoly = opts.IsArityPoly
        ArityParam = opts.ArityParam
        Captures = captures
        // Like AntisymGroups: grafted on by the one construction site that has
        // it (Lowering.lowerTypedLambda, from the typechecked kernel's
        // summary); every other callable-building site carries none.
        SignParities = []
    }

/// Build a fresh IRCallable for an anonymous inline lambda: synthesized
/// "__lambda_<id>" name, fresh id, not static/arity-polymorphic. Thin
/// wrapper over `mkCallable` with `defaultLambdaOptions`.
let mkLambdaCallable
    (builder: IRBuilder)
    (parms: IRParam list)
    (body: IRExpr)
    (retType: IRType)
    (captures: CaptureInfo list)
    (isCommutative: bool)
    (commGroups: int list list)
    (parallelism: (int * int) list)
    (isOmpParallel: bool)
    (isCudaKernel: bool)
    (cudaBlockSize: int)
    (isMpiParallel: bool)
    : IRCallable =
    mkCallable builder defaultLambdaOptions parms body retType captures
               isCommutative commGroups parallelism isOmpParallel
               isCudaKernel cudaBlockSize isMpiParallel

// The deduced WREATH TIE (docs/plan-orbit-index-types.md section 7 / section 9 step 4)
//
// section 1's motivating gap: `func(A, A)` with `A : SymIdx<2,n>` under
// `where comm` licenses `S_2 wr S_2` on the output's FOUR raw axes, with no
// prior way to say so. Juxtaposing `SymIdx<2,n>` x2 drops the tie (pool 36
// cells vs orbit count 21); widening to `SymIdx<4,n>` over-claims full S_4.
//
// The rule is APPEND A LEVEL: input class `L`, `k` repeated occurrences tied
// by a declared clause -> `L ++ [(k, s)]`, `s = '+'` for comm, `'-'` for
// anticomm. Depth-1 `SymIdx<r,n>` contributes `[(r,+)]`, `AntisymIdx<r,n>`
// contributes `[(r,-)]`, an already-wreath input contributes its own list --
// so depth 3 (`let P = f(A,A) in g(P,P)`, section 7.1) falls out of the same
// rule.

/// A deduced wreath tie: `Positions` argument slots holding the SAME object,
/// tied by one declared comm/anticomm clause, over a common inner class.
type WreathTie = {
    /// The tied argument positions, ascending. Length >= 2.
    Positions: int list
    /// The class of EACH tied argument (its own level list, outermost-last).
    InnerLevels: (int * bool) list
    /// The OUTPUT class: `InnerLevels ++ [(k, OuterIsPlus)]`, normalized.
    OutputLevels: (int * bool) list
    /// section 4's fold origin M0 -- the extent every tied argument shares.
    BaseExtent: IRExpr
    /// The appended level's character: '+' under comm, '-' under anticomm.
    OuterIsPlus: bool
}

/// The level list an ARGUMENT contributes to a tie built over it, with its base
/// extent. `None` for every class that cannot be a wreath sub-block:
///   * dense (`SymNone`) -- already ties via `rawAxisGroups` into a plain
///     `SymIdx<k,n>` (one level, not two); routing it here would double-count.
///   * Hermitian -- conjugation is outside `Hom(G,+-1)` (section 6), no sign
///     list describes it (plan section 3's `HermitianIdx` note).
///   * compound / sparse / ragged / dep / irreps / multi-record -- not a
///     permutation class over one extent.
///
/// Requires EXACTLY ONE S-dimensional index record: a wreath pool is a flat
/// cell array with nothing to juxtapose a second dimension against, so a
/// T-dimension fiber beside the compact block would be SILENTLY DROPPED.
/// Dependencies must be empty for the same reason -- section 4's fold needs a
/// plain extent, not a dependent one.
let private wreathArgContribution (at: IRArrayType) : ((int * bool) list * IRExpr) option =
    match at.IndexTypes with
    | [ ix ] when ix.Kind = SDimension && List.isEmpty ix.Dependencies ->
        (match ix.Symmetry with
         | SymSymmetric when ix.Rank >= 2 && ix.IxKind = IxKPlain -> Some ([ (ix.Rank, true) ], ix.Extent)
         | SymAntisymmetric when ix.Rank >= 2 && ix.IxKind = IxKPlain -> Some ([ (ix.Rank, false) ], ix.Extent)
         | SymWreath ->
             let ls = orbitLevelsOf ix
             if List.isEmpty ls then None else Some (ls, orbitBaseExtent ix)
         | _ -> None)
    | _ -> None

/// `deduceWreathTie`'s three-way answer: `WreathTie option` cannot express a
/// third outcome where the tie WOULD fire but firing it would corrupt values,
/// so the application must be REFUSED (condition 6 below) rather than fall
/// back to a different, also-wrong deduced type. Only the typecheck seam
/// surfaces `WreathKernelNotOdd` to the user; reaching it in codegen or the
/// interpreter is an internal error (loud backstop).
type WreathTieVerdict =
    /// No tie: distinct inputs, a partial tie, a dense input, a mixed clause.
    /// Juxtaposes through the axis-group machinery as usual.
    | WreathNoTie
    /// The tie fires; output is the wreath class the payload describes.
    | WreathTied of WreathTie
    /// Conditions hold, but the kernel's recorded sign parity in tied
    /// argument `argPos` fails the '-'-inner-level oddness requirement
    /// (condition 6): `KspEven` (provable contradiction) or `KspUnknown`
    /// (unprovable, no declaration to trust).
    | WreathKernelNotOdd of argPos: int * parity: KernelSignParity * innerLevels: (int * bool) list

/// THE deduction rule, in one place. Fires only when EVERY argument position
/// is in ONE tie. Conditions, all required:
///   1. `not isReynolds` (section 8.1) -- under `reynolds` a clause is an
///      iteration license, not a kernel claim; not sound grounds for a
///      wreath storage class.
///   2. `k >= 2` arguments, all the SAME IDENTITY (`sameIdentity`; stable
///      only for bare variables, so `let P = f(A,A) in g(P,P)` ties but
///      inline `g(f(A,A), f(A,A))` does not -- no CSE).
///   3. All positions contribute the SAME inner class over the SAME extent
///      (`wreathArgContribution`).
///   4. A DECLARED clause spans all positions (`commGroups`, comm union
///      anticomm); sign is '-' iff `antisymGroups` also spans all. A
///      repeated argument alone gets only `(r!)^k` (BladeWreath.v);
///      commutativity buys the extra factor (`noncomm_r3_loses_the_swap`).
///   5. The kernel contributes no T-dimensions and consumes no inner
///      dimension (`classifyOutputStorage` refuses that combination anyway).
///   6. SOUNDNESS GATE (section 8.1's per-level extension): any '-' inner
///      level requires the kernel provably SIGN-ODD (`KspOdd`) in every tied
///      argument, h(-p, q) = -h(p, q) -- the wreath analogue of BL4015's
///      compact-class-inheritance certificate. `KspEven` is the silent-
///      corruption case (`CommContradictsBody` analog); `KspUnknown` REFUSES
///      TOO, since no clause declares per-argument oddness (same precedent
///      as BL4015); all-'+' levels need no certificate. Unobservable while
///      only canonical cells were reachable -- `decompact` and the mirrored
///      read turn it into a silent wrong VALUE (corpus 213/214). The gate
///      reads `argParities` (`IRCallable.SignParities`), so all four call
///      sites judge from the same values; a missing entry is `KspUnknown`.
///
/// Anything else returns WreathNoTie, juxtaposed as usual. EVERY consumer
/// (deduction, codegen, interpreter, the typecheck guard) calls THIS with
/// the same arguments, so the soundness gate lives here, not at the
/// typecheck call site.
let deduceWreathTie
    (arrayTypes: IRArrayType list)
    (identities: ArrayIdentity list)
    (commGroups: int list list)
    (antisymGroups: int list list)
    (kernelTDims: IRIndexType list)
    (kernelConsumesInner: bool)
    (isReynolds: bool)
    (argParities: KernelSignParity list) : WreathTieVerdict =
    let k = List.length arrayTypes
    if isReynolds || k < 2 || List.length identities <> k
       || not (List.isEmpty kernelTDims) || kernelConsumesInner then WreathNoTie
    else
    let spansAll (gs: int list list) =
        gs |> List.exists (fun g -> [ 0 .. k - 1 ] |> List.forall (fun i -> List.contains i g))
    if not (spansAll commGroups) then WreathNoTie
    else
    match arrayTypes |> List.map wreathArgContribution with
    | [] -> WreathNoTie
    | contributions when contributions |> List.exists Option.isNone -> WreathNoTie
    | contributions ->
        let (levels0, ext0) = Option.get (List.head contributions)
        let uniform =
            contributions |> List.forall (fun c ->
                match c with
                | Some (ls, e) -> ls = levels0 && e = ext0
                | None -> false)
        let sameObject =
            identities |> List.forall (fun idn -> sameIdentity idn (List.head identities))
        if not (uniform && sameObject) then WreathNoTie
        else
            let isPlus = not (spansAll antisymGroups)
            match orbitNormalForm (levels0 @ [ (k, isPlus) ]) with
            // Only a genuine depth >= 2 class is a tie this layer owns. The
            // other two normal forms are unreachable here (k >= 2 and levels0 is
            // non-empty with every rank >= 2), but they route to the plain
            // SymIdx/AntisymIdx record rather than to a malformed wreath one --
            // the normalization is shared with the surface type precisely so a
            // collapse is handled once and identically.
            | OrbNfWreath outLevels ->
                // Condition 6. The check keys off the INNER levels only: the
                // appended (outer) level's sign is the declared clause itself
                // -- comm's inclusive simplex or anticomm's pin spelling -- and
                // both inherit depth-1's existing validation (the
                // CommContradictsBody / AntisymmContradictsBody pair checks).
                // What depth 1 never had is a mirror INSIDE one argument.
                let innerHasMinus = levels0 |> List.exists (fun (_, plus) -> not plus)
                let firstNotOdd =
                    if not innerHasMinus then None
                    else
                        [ 0 .. k - 1 ]
                        |> List.tryPick (fun i ->
                            match List.tryItem i argParities with
                            | Some KspOdd -> None
                            | Some p -> Some (i, p)
                            | None -> Some (i, KspUnknown))
                match firstNotOdd with
                | Some (i, p) -> WreathKernelNotOdd (i, p, levels0)
                | None ->
                    WreathTied { Positions = [ 0 .. k - 1 ]
                                 InnerLevels = levels0
                                 OutputLevels = outLevels
                                 BaseExtent = ext0
                                 OuterIsPlus = isPlus }
            | OrbNfTrivial | OrbNfDepth1 _ -> WreathNoTie

/// Deduce output array type from loop application, per formalism section 10.9:
/// 1. Group arrays by identity (consecutive identical arrays)
/// 2. For each group: if comm + arity > 1, use SymIdx; else Idx.
///    AntisymIdx instead of SymIdx when DECLARED antisymmetric (`where
///    anticomm(a, b)`: kernel used AS-IS, storing f(i,j) for i<j only, reads
///    of (j,i) negated by TfNegateOnSwap; no permutation sum) or when
///    isReynoldsAntisym: Reynolds antisymmetrization over a commutative
///    same-array group produces a strictly-triangular output (C(n,r) strict
///    tuples, no diagonal) NOT a symmetric one (C(n+r-1,r)) -- without this,
///    a same-array reynolds(f, Antisymmetric) would deduce the wrong
///    cardinality with a spurious zero diagonal. With NO Reynolds clause, a
///    rank-0 elementwise kernel instead PRESERVES the input group's compact
///    storage class verbatim (it doesn't reshape symmetry).
/// 3. Concatenate S-dims from all groups
/// 4. Add T-dims from kernel output
let deduceOutputType
    (arrayTypes: IRArrayType list)
    (identities: ArrayIdentity list)
    (commGroups: int list list)
    (antisymGroups: int list list)
    (sDimsPerArray: int list)
    (kernelTDims: IRIndexType list)
    (elemType: IRType)
    (isReynolds: bool)
    (isReynoldsAntisym: bool)
    (kernelConsumesInner: bool)
    (kernelSignParities: KernelSignParity list)
    (builder: IRBuilder) : IRType =

    if arrayTypes.IsEmpty then IRTUnit
    else
    // Step 1a: THE WREATH TIE, ahead of the axis-group machinery and total
    // when it fires. Cannot be a post-pass over `sGroups`: the answer is ONE
    // index record where the group walk would emit k (nothing to rewrite in
    // place), and `buildLoopLevelStructure` below REFUSES a wreath input
    // outright -- so the tie must be decided before any loop level is built.
    // The wreath output's iteration is the segment-peeled `orb_visit` nest,
    // so nothing downstream wants axis groups for it.
    match deduceWreathTie arrayTypes identities commGroups antisymGroups
                          kernelTDims kernelConsumesInner isReynolds kernelSignParities with
    | WreathTied tie ->
        mkArrayArrow [ mkWreathIndexRecord (builder.FreshId()) tie.OutputLevels tie.BaseExtent ]
                     elemType None
    | WreathKernelNotOdd (argPos, _, _) ->
        // Unreachable: the typecheck apply seam runs the SAME call with the
        // SAME arguments first and surfaces this verdict as a user-facing
        // error before any output type is deduced. Loud rather than a silent
        // fall-through to the plain SymIdx/AntisymIdx record -- that record
        // would be a DIFFERENT wrong type, with no warning anywhere.
        failwith (sprintf "internal: deduceOutputType reached a wreath tie whose kernel is not \
provably sign-odd in tied argument %d; the typecheck seam should have refused this application"
                          argPos)
    | WreathNoTie ->
        // Step 1+2: Build output S-dim index types from the SINGLE canonical
        // axis grouping (rawAxisGroups) -- the same source iteration uses --
        // so output storage and loop iteration cannot disagree. Supports
        // PARTIAL product symmetry: distinct arrays sharing an index space at
        // some positions (comm over A<Lat,Lon>, B<Lat,Depth>) symmetrize
        // ONLY the shared axis, an axis-level fact (section 14.6).
        //
        // Each axis group becomes ONE output S-dim index: group size > 1 ->
        // a higher-rank SYMMETRIC (or ANTISYMMETRIC under Reynolds) index,
        // covering both same-array repetition and shared-named-space
        // arguments, plus a within-type symmetric record's own arity
        // components. Group size == 1 -> the source index type copied
        // VERBATIM (Id refreshed), preserving Rank/Symmetry/ragged/dep
        // structure -- load-bearing for a single symmetric input and for
        // elementwise-over-ragged/dep.
        //
        // A level's (ArrayIndex, LocalDimIndex) recovers its source
        // IRIndexType, so the verbatim copy uses the full original record.
        let sLevels = buildLoopLevelStructure identities commGroups arrayTypes sDimsPerArray
        let sGroups = rawAxisGroups identities commGroups sLevels
        // Per array: its S-dimension index-type records, in order (LocalDimIndex
        // indexes into this list).
        let sDimRecordsByArray =
            arrayTypes |> List.map (fun arr ->
                arr.IndexTypes |> List.filter (fun idx -> idx.Kind = SDimension))
        let sourceRecord (lv: LoopLevelInfo) : IRIndexType option =
            if lv.ArrayIndex < sDimRecordsByArray.Length then
                let recs = sDimRecordsByArray.[lv.ArrayIndex]
                if lv.LocalDimIndex < recs.Length then Some recs.[lv.LocalDimIndex] else None
            else None
        let outputSDims =
            // Group ids in reordered level order; emit one index per group, in
            // first-appearance order (which matches the loop nest order).
            let levelArr = List.toArray sLevels
            let groupArr = List.toArray sGroups
            // Is this axis group the one a `where anticomm(...)` clause
            // declared? The clause names KERNEL PARAMETERS (1:1 with
            // argument positions): every member's ArrayIndex must be listed
            // in one declared group, spanning MORE than one argument (a
            // within-type symmetric block, all one ArrayIndex, is an INPUT
            // property never reclassified by a kernel clause).
            let groupIsDeclaredAntisym (memberIdxs: int list) : bool =
                if List.isEmpty antisymGroups then false
                else
                    let arrIdxs =
                        memberIdxs |> List.map (fun k -> levelArr.[k].ArrayIndex) |> List.distinct
                    List.length arrIdxs > 1
                    && antisymGroups |> List.exists (fun g ->
                           arrIdxs |> List.forall (fun a -> List.contains a g))
            let mutable emittedGroups = []
            let mutable result = []
            for gi in 0 .. levelArr.Length - 1 do
                let g = groupArr.[gi]
                if not (List.contains g emittedGroups) then
                    emittedGroups <- emittedGroups @ [g]
                    let memberIdxs = [ for k in 0 .. levelArr.Length - 1 do if groupArr.[k] = g then yield k ]
                    let groupRank = List.length memberIdxs
                    let repLevel = levelArr.[List.head memberIdxs]
                    match repLevel.FusedFactors with
                    | Some factors when not factors.IsEmpty && groupRank > 1 ->
                        // JOINT output record: one symmetric index of rank
                        // groupRank over the COMPOUND extent -- SymIdx<r,
                        // prod(n_j)>, the only sound output for one identity
                        // group over a multi-dim array (docs/formalism.md
                        // section 8.4/12.4). Per-dimension SymIdx-per-dim is
                        // unsound (per_dim_swap_not_symmetry) and can't even
                        // hold the result (counting_general_C).
                        let groupSymmetry =
                            if isReynolds then
                                (if isReynoldsAntisym then SymAntisymmetric else SymSymmetric)
                            elif groupIsDeclaredAntisym memberIdxs then SymAntisymmetric
                            else SymSymmetric
                        let prodExtent =
                            factors |> List.map (fun f -> f.Extent)
                                    |> List.reduce (fun a b ->
                                        match a, b with
                                        | IRLit (IRLitInt x), IRLit (IRLitInt y) -> IRLit (IRLitInt (x * y))
                                        | _ -> IRBinOp (IRElementwise, IRMul, a, b))
                        // The compound axis is ANONYMOUS and PLAIN: Tag =
                        // None AND IxKind = IxKPlain must BOTH be stamped,
                        // not inherited from the template factor -- IxKIrreps
                        // is an admissible factor kind, so a template-
                        // inherited IxKIrreps beside Tag = None would break
                        // the Tag<->IxKind agreement the validator enforces.
                        // A batch x irreps product is NOT an irreps space:
                        // the joint coordinate ranges over tuples with no
                        // single-space representation structure. Dependencies
                        // are empty by fusion eligibility.
                        let template = List.head factors
                        result <- result @ [{ template with
                                                Extent = prodExtent
                                                Rank = groupRank
                                                Symmetry = groupSymmetry
                                                Tag = None
                                                IxKind = IxKPlain
                                                Id = builder.FreshId() }]
                    | Some factors ->
                        // Defensive: a lone fused level cannot occur by
                        // construction (identity partners fuse uniformly);
                        // restore the source records verbatim if it ever does.
                        result <- result @ (factors |> List.map (fun f -> { f with Id = builder.FreshId() }))
                    | None ->
                    match sourceRecord repLevel with
                    | None -> ()
                    | Some rep ->
                        if groupRank > 1 then
                            // A Reynolds clause shapes the output symmetry
                            // (sym/antisym per variant), overriding the
                            // input's native storage class. With NO
                            // Reynolds, a rank-0 elementwise kernel preserves
                            // the input's compact storage class verbatim;
                            // only a plain (SymNone) multi-level group
                            // defaults to symmetric, unless the kernel
                            // DECLARED it antisymmetric (`where anticomm`),
                            // pinning the strict simplex like comm pins the
                            // inclusive one.
                            let groupSymmetry =
                                if isReynolds then
                                    (if isReynoldsAntisym then SymAntisymmetric else SymSymmetric)
                                elif groupIsDeclaredAntisym memberIdxs then SymAntisymmetric
                                else
                                    // A wreath REPRESENTATIVE keeps its own class:
                                    // widening it to a bare SymSymmetric over the
                                    // group rank would claim full S_R on axes the
                                    // wreath does not tie (section 7's whole point), and
                                    // no deduction produces one anyway -- storage
                                    // refuses it upstream.
                                    (match rep.Symmetry with
                                     | SymSymmetric | SymAntisymmetric | SymHermitian | SymWreath -> rep.Symmetry
                                     | SymNone -> SymSymmetric)
                            result <- result @ [{ rep with Rank = groupRank; Symmetry = groupSymmetry; Id = builder.FreshId() }]
                        else
                            // size-1 group: verbatim copy, refresh Id only.
                            // EXCEPT a halo slot: the output axis is the
                            // plain dense INTERIOR of the wrapped index (the
                            // window structure is consumed by iteration). A
                            // compound-inner halo output must NOT stay
                            // IxKCompound -- its cell count is cardinality
                            // minus the shrink, not the mask cardinality, so
                            // it allocates as a dense Array.
                            let rep' =
                                match rep.Tag with
                                | Some t when t.StartsWith "__halowin|" ->
                                    { rep with Tag = None; IxKind = IxKPlain }
                                | _ -> rep
                            result <- result @ [{ rep' with Id = builder.FreshId() }]
            result
            // Drop indices tagged "consumed by the kernel" -- ONLY when the
            // kernel consumes an inner dimension (array-typed param of rank
            // > 0, e.g. `lambda(g: Array<...>) -> reduce(g, ...)`): the
            // kernel receives a sub-array along the tagged dim, so the dim
            // is not part of the output. For a rank-0 ELEMENTWISE kernel
            // nothing is consumed, so the ragged/dependent inner dim must
            // PROPAGATE (dropping it collapsed a ragged/DepIdx array to a
            // plain Idx record -- the elementwise-over-ragged/dep gap):
            //   __group_member, __raggedidx, __raggedidx_inline,
            //   __raggedidx_opaque, __depidx_inner.
            |> List.filter (fun idx ->
                match idx.IxKind with
                | IxKGroupMember
                | IxKRagged
                | IxKRaggedInline
                | IxKRaggedOpaque
                | IxKDepInner -> not kernelConsumesInner
                | _ -> true)
        
        // Step 4: T-dims from kernel output (passed in with real extents)
        let outputTDims = 
            kernelTDims |> List.map (fun idx ->
                { idx with Kind = TDimension; Id = builder.FreshId() })
        
        // Combine S-dims and T-dims
        let allDims = outputSDims @ outputTDims
        
        if allDims.IsEmpty then
            // Rank-0 output: just the element type itself.
            // elemType is already IRType, so no IRTScalar wrap is needed.
            elemType
        else
            mkArrayArrow allDims elemType None


// Code Generation Structures
// These structures provide explicit bindings between loop indices, arrays,
// and kernel parameters to facilitate code generation.

/// What kind of element access to generate for a loop level
type VirtualKind =
    | RealArray           // Normal: elem = arr[i]
    | VirtualRange of offset: IRExpr option  // range<I>: elem = i + offset
    | VirtualReverse      // reverse<I>: elem = (n - 1 - i)

/// Per-array element peeling info at a loop level
type ElementBinding = {
    /// Which input array this element comes from (0-based)
    ArrayPosition: int
    /// Name of the array expression (for code gen)
    ArrayName: string
    /// The kernel parameter name this feeds into
    ParamName: string
    /// The kernel parameter's VarId (for substitution in kernel body)
    ParamVarId: IRId
    /// Which dimension within the array (for multi-dim arrays)
    DimIndex: int
    /// For SymIdx: which arity component (0, 1, 2, ...)
    RankComponent: int
    /// Element type of the array (for explicit typing).
    /// IRType to align with IRArrayType.ElemType.
    ArrayElemType: IRType
    /// Total rank of the array being indexed
    ArrayRank: int
    /// Virtual array kind (range, reverse, or real)
    Virtual: VirtualKind
    /// The iterated slot's Tag (None for real arrays / untagged slots).
    /// Carries the "__halowin|" payload to the element peel, which needs to
    /// distinguish a halo window over a compound domain (ordinal + cidx
    /// alias) from a plain compound coordinate binding.
    SlotTag: string option
}

/// A single loop level: how to iterate + what to peel
type LoopIndexBinding = {
    /// Loop level index (0, 1, 2, ...)
    Level: int
    /// Generated index variable name ("__i0", "__i1", ...)
    IndexName: string
    /// Extent as IRExpr (for flexible code gen)
    Extent: IRExpr
    /// Array name for non-literal extent lookup (e.g. "A" -> "A_extents[dim]")
    ExtentArrayRef: string
    /// Dimension index for non-literal extent lookup
    ExtentDimRef: int
    /// List of prior level indices to subtract from bound (empty = no subtraction)
    BoundDependencies: int list
    /// Extra offset to subtract from bound (1 for antisymmetric strict i < j)
    StrictOffset: int
    /// Some d marks a FUSED joint level spanning d plain-dense source
    /// dims of its array (extent = product of the array's first d extents).
    /// Codegen renders the bound as extents[0]*...*extents[d-1] and each
    /// element binding decodes its per-dim coordinate from the compound index
    /// (row-major). None = ordinary single-dim level.
    FusedRank: int option
    /// Whether this loop level is parallelized
    IsParallel: bool
    /// Symcom state at this level
    State: SymcomState
    /// Element bindings at this level (1 for outer product, N for co-iteration)
    Elements: ElementBinding list
}

/// Complete information needed to generate a loop nest
/// Reduce-over-deferred-computation fold-chunking (docs/plan-cpp-perf-
/// exploitation.md section 2): the OUTERMOST loop level of a fold nest
/// splits into contiguous per-thread chunks; each thread runs the WHOLE
/// inner nest serially over its chunk into a private accumulator, and
/// partials combine in thread order through the fold wrapper. Only the
/// outermost level chunks: a triangular/dependent inner level then runs
/// exactly as it would serially, and reordering is only between chunks --
/// what the kernel's comm licence covers. Set only when the outermost
/// binding is rectangular (no bound deps, no strict offset), so chunk
/// arithmetic is a plain split of [0, extent).
///
/// Chunks seed from their own first CONTRIBUTED value, not the caller's seed
/// (`HasName` tracks "has one yet"), so no identity element is required and
/// an empty-inner-nest outer index contributes nothing. The caller's seed
/// enters the combine first in the outer accumulator, making the result the
/// serial left fold up to associativity.
type FoldChunkPlan = {
    /// C++ type of the accumulator and the per-thread partials array.
    ElemCpp: string
    /// Uniquifying suffix for the generated locals (the fold binding's name).
    Tag: string
}

type LoopNestCodeGen = {
    /// All loop index bindings, in nesting order (outermost first)
    Bindings: LoopIndexBinding list
    /// The kernel expression (lambda body with param references)
    KernelExpr: IRExpr
    /// Kernel parameter info (for building element access expressions)
    KernelParams: IRParam list
    /// Captured variables from outer scope
    Captures: CaptureInfo list
    /// Output variable name
    OutputName: string
    /// Output type
    OutputType: IRType
    /// Output symmetry vector (for allocation)
    OutputSymmVec: int list
    /// Input array names in order
    InputArrayNames: string list
    /// Speedup factor (for comments/verification)
    SpeedupFactor: int64
    /// Whether kernel has Reynolds operator
    HasReynolds: bool
    /// Whether Reynolds is antisymmetric (sign alternates with permutation parity)
    IsAntisymmetric: bool
    /// Fused-fold mode: when Some, the nest ACCUMULATES
    /// `OutputName = <wrapper>(OutputName, kernel)` into a caller-declared
    /// scalar instead of assigning output cells ("+"/"*" fast-path to
    /// `+=`/`*=`). Caller declares/seeds the accumulator, forces OutputType
    /// scalar, suppresses the OMP pragma (scalar accumulation isn't race-safe).
    FoldWrapper: string option
    /// MPI slab mode (dense rectilinear decomposition): when true, the
    /// OUTERMOST loop iterates the per-rank slab [lo,hi) instead of
    /// [0, extent); caller emits the slab-bound prologue + Allgatherv.
    /// Always false outside mpiEmitMode.
    MpiSlab: bool
    /// Whether the resolved kernel callable ASKED for OpenMP, independent of
    /// whether the nest gets a pragma. `IsParallel` on the bindings is the
    /// CONJUNCTION of this and "level 0", so once serial the request bit
    /// isn't recoverable from bindings alone -- kept separately so emitters
    /// can mark a requested-but-serial nest instead of silently dropping it.
    OmpRequested: bool
    /// Comm-licensed parallel fold over this nest. Meaningful only together
    /// with FoldWrapper; None everywhere else. See FoldChunkPlan for the
    /// shape guarantees.
    FoldChunk: FoldChunkPlan option
}

/// One dimension GROUP of a device buffer. Mirrors a single IRIndexType:
/// a group is either a plain rectangular axis (Rank=1, SymNone) or a
/// symmetric/antisymmetric/hermitian block of Rank>=2 sharing one extent.
type BufferDimGroup = {
    Rank: int
    Extent: IRExpr
    Symmetry: SymmetryClass
    Kind: DimensionKind
    Dependencies: IRId list
}

/// The dimensional type of one contiguous device buffer (one array's pool).
/// The pool holds `cardinality` scalars of `ElemType` in linearize (DFS) order
/// -- identical to allocate<>'s pool order, the invariant making host/device
/// access consistent. A skeleton (promote<T,N>::type) is a VIEW of these bytes;
/// this type is what makes the bytes interpretable and specifies the forward
/// (native->device) and inverse (device->native) transforms the CUDA shim uses.
/// Derived from IRArrayType (authoritative) so it cannot drift.
type DeviceBufferType = {
    ElemType: IRType
    Groups: BufferDimGroup list
}

/// Project an IRArrayType into a DeviceBufferType. Each IRIndexType becomes one
/// BufferDimGroup. Pure restructuring of information already on the array.
/// A T-dimension never participates in symmetry, so its regime is forced SymNone.
let deviceBufferTypeOfArray (arr: IRArrayType) : DeviceBufferType =
    { ElemType = arr.ElemType
      Groups =
        arr.IndexTypes |> List.map (fun ix ->
            { Rank = ix.Rank
              Extent = ix.Extent
              Symmetry = (match ix.Kind with TDimension -> SymNone | SDimension -> ix.Symmetry)
              Kind = ix.Kind
              Dependencies = ix.Dependencies }) }

/// True iff every group is plain rectangular with a constant (non-dependent)
/// extent: the scope of the FIRST cuda kernel. False => fall back to host loop.
let isRectangularConstBuffer (bt: DeviceBufferType) : bool =
    bt.Groups |> List.forall (fun g ->
        g.Rank = 1 && g.Symmetry = SymNone && List.isEmpty g.Dependencies)

/// True iff the element scalar type crosses an `extern "C"` linkage boundary
/// cleanly (the CUDA launch wrapper is extern "C" so g++ and nvcc agree on
/// an unmangled symbol; every crossing type needs a stable shared ABI).
/// Fundamental scalars (int32/int64/float/double/bool) qualify. std::complex
/// also qualifies: the C++ standard guarantees std::complex<T> is layout-
/// compatible with T[2] and thrust::complex<T> shares that layout, so the
/// host .cpp keeps std::complex in the extern "C" signatures while the .cu
/// uses thrust::complex internally (std::complex's operators are host-only
/// under nvcc), reinterpreting through cudaMemcpy's void*. EXCLUDED:
/// ETString (non-POD, never crosses) and ETUnit (not a data element).
///
/// A non-boundary-safe element type falls back to the host loop rather than
/// emit an unlinkable kernel. Uses AnyPrimElem so a unit-annotated or
/// idx-tagged primitive is still recognized by its underlying scalar.
let isCudaBoundarySafeElem (ty: IRType) : bool =
    match ty with
    | AnyPrimElem ETInt32 | AnyPrimElem ETInt64
    | AnyPrimElem ETFloat32 | AnyPrimElem ETFloat64
    | AnyPrimElem ETBool
    | AnyPrimElem ETComplex64 | AnyPrimElem ETComplex128 -> true
    | _ -> false

/// Binomial C(n, k) as int64. Incremental multiplicative form; each partial
/// C(n, i+1) = C(n, i)*(n-i)/(i+1) is itself an integer, so the mid-loop
/// division is EXACT (not lossy).
let binomI64 (n: int64) (k: int64) : int64 =
    if k < 0L || k > n then 0L
    else
        let k = if k > n - k then n - k else k
        let mutable result = 1L
        let mutable i = 0L
        while i < k do
            result <- result * (n - i) / (i + 1L)
            i <- i + 1L
        result

/// Per-group scalar count as an IRExpr. Literal extents fold to a literal:
///   rectangular (Rank=1)      => extent
///   symmetric/hermitian (r>=2) => C(n + r - 1, r)   (multiset combinations)
///   antisymmetric (r>=2)       => C(n, r)            (strict combinations)
///   wreath (OrbIdx, depth d)   => section 4's ITERATED binomial over the level list
/// Symbolic-symmetric counts (binomial of a runtime extent) are deferred; the
/// first kernel gates on isRectangularConstBuffer so only the literal and the
/// symbolic-rectangular paths are reachable today.
let bufferGroupCardinality (g: BufferDimGroup) : IRExpr =
    match g.Extent with
    // ---- Wreath (OrbIdx) groups, FIRST and total ----------------------------
    // Count = section 4 fold M0 = n; Mi = C(M+r-1,r) at '+', C(M,r) at '-' --
    // NOT one combinadic over g.Rank. The obvious fallbacks are wrong in ways
    // nothing downstream would notice: `| other -> other` would return the
    // MARKER itself as the count; `PlaceCombinatorial _` over R = prod(ri)
    // computes C(n+R-1,R), e.g. C(19,4) = 3876 for [(2,+),(2,+)] at n=4
    // against the true 55. Overflow and a non-literal extent are ERRORS,
    // never a fallback (section 7.2's failure mode is silent int64
    // wraparound); `cellCountChecked` is the exactly-checked fold that
    // detects it.
    | IROrbitClass (levels, baseExtent) ->
        (match baseExtent with
         | IRLit (IRLitInt n) ->
             (match Blade.OrbRank.cellCountChecked (orbRankLevels levels) n with
              | Ok cells -> IRLit (IRLitInt cells)
              | Error detail ->
                  failwithf "OrbIdx<%s, %d>: the class's cell count cannot be computed -- %s. \
An iterated-wreath class grows one binomial per level, so a deep class over a large extent leaves \
int64 well before its rank does (docs/plan-orbit-index-types.md section 7.2); reduce the extent or the depth."
                            (ppOrbitLevels levels) n detail)
         | _ ->
             failwithf "OrbIdx<%s, ?>: a wreath class needs a COMPILE-TIME extent. Its cell count is \
the iterated binomial fold over the level list starting from the extent, and each level's output is \
the next level's ground set, so a runtime extent has no closed-form cell count to allocate against."
                       (ppOrbitLevels levels))
    | IRLit (IRLitInt n) ->
        let r = int64 g.Rank
        let count =
            // Cardinality is a placement-axis question: dense product vs a
            // combinadic over the group's arity. Strict (antisym) gives C(n,r);
            // inclusive (sym/herm) gives the multiset count C(n+r-1,r). Behavior
            // identical to the prior SymmetryClass match; expressed via the
            // Level-1 PlacementClass so a future PlaceTabulated arm slots in here
            // (and FS0025 will flag this site until it does).
            match placementClassOf g.Symmetry with
            | PlaceDense -> n
            | PlaceCombinatorial SymAntisymmetric -> binomI64 n r
            // A wreath group whose extent is a bare literal instead of an
            // IROrbitClass marker has lost its level list, so there is nothing
            // to fold. That is a malformed record, not a case to guess at:
            // C(n+R-1, R) over the raw axis count is the wrong number, and it
            // is the wrong number silently. (Kept explicit rather than folded
            // into `PlaceCombinatorial _` so the wildcard below stays honestly
            // "sym or hermitian".)
            | PlaceCombinatorial SymWreath ->
                failwithf "OrbIdx group of rank %d at extent %d has no level list: a wreath record's \
Extent must be the IROrbitClass marker carrying [(r,s), ...]. This record was built without it."
                          g.Rank n
            | PlaceCombinatorial _ -> binomI64 (n + r - 1L) r
            // Unreachable: placementClassOf yields only Dense/Combinatorial, and a
            // compound carries an IRCompoundMask extent (taken by `| other` below,
            // returning the runtime-cardinality expr), so this literal-extent branch
            // is never tabulated. Defensive fallback to the literal count.
            | PlaceTabulated -> n
        IRLit (IRLitInt count)
    | other -> other

/// Total pool cardinality as an IRExpr (product of per-group factors -- product
/// symmetry: independent groups multiply). Folds to a literal when all extents
/// are literal.
let deviceBufferCardinality (bt: DeviceBufferType) : IRExpr =
    match bt.Groups with
    | [] -> IRLit (IRLitInt 1L)
    | groups ->
        groups
        |> List.map bufferGroupCardinality
        |> List.reduce (fun a b ->
            match a, b with
            | IRLit (IRLitInt x), IRLit (IRLitInt y) -> IRLit (IRLitInt (x * y))
            | _ -> IRBinOp (IRElementwise, IRMul, a, b))

/// True iff a symmetry vector encodes ANY actual symmetry -- two adjacent
/// positions share a group number (a symmetric/antisymmetric block). A
/// purely rectangular output yields all-distinct consecutive groups (e.g.
/// [1;2;3]), which is NOT symmetry and must pass nullptr to allocate.
/// Matters for MSVC: a rectangular rank-2+ output with a non-empty vec like
/// [1;2] took the named-static-array allocate path and hit C2131 (address
/// of a function-local static isn't a constant); routing no-real-symmetry
/// to nullptr fixes that (allocate treats null SYMM and all-distinct SYMM
/// the same: full rectangular).
let hasRealSymmetry (symmVec: int list) : bool =
    symmVec
    |> List.pairwise
    |> List.exists (fun (a, b) -> a = b)

/// Build symmetry vector from output type: consecutive equal values
/// indicate symmetric dimensions.
let buildSymmVec (outputType: IRType) : int list =
    match outputType with
    | ArrayElem arr ->
        let mutable symmVec = []
        let mutable groupNum = 1
        let mutable prevSymm = None
        
        for idx in arr.IndexTypes do
            // A wreath group has no (SYMM) encoding: the vector says "these
            // adjacent axes form ONE shrinking simplex", and a wreath's axes
            // shrink per level, not uniformly. The alternative to refusing is a
            // symmVec that describes a different array than the one the type
            // names, so refuse.
            if idx.Symmetry = SymWreath then
                failwith (orbitStorageUnsupported "storage allocation (buildSymmVec)" (orbitLevelsOf idx))
            for compIdx in 0 .. idx.Rank - 1 do
                // Hermitian shares the symmetric storage layout: the upper
                // triangle is stored compactly (same {1,1,..} mask as SymIdx),
                // and the lower triangle is recovered by conjugation at read
                // time. So Hermitian groups identically to symmetric here; only
                // the READ path differs (std::conj on lower-triangle access).
                let isSymmetric =
                    (idx.Symmetry = SymSymmetric || idx.Symmetry = SymHermitian) && idx.Rank > 1
                if isSymmetric && compIdx > 0 then
                    // Continue same group
                    symmVec <- symmVec @ [groupNum]
                else
                    // Start new group
                    if prevSymm = Some true && compIdx = 0 then
                        groupNum <- groupNum + 1
                    symmVec <- symmVec @ [groupNum]
                    if not isSymmetric then
                        groupNum <- groupNum + 1
                prevSymm <- Some isSymmetric
        symmVec
    | _ -> []

/// Like buildSymmVec, but groups ALL compact classes (symmetric, Hermitian, AND
/// antisymmetric) into shared storage groups, and returns a parallel per-group
/// STRICT mask. buildSymmVec deliberately treats antisym as non-symmetric (one
/// singleton group per dim) because plain antisym storage uses a SEPARATE
/// all-spanning allocate_antisym. For the PER-GROUP-STRICT path we instead want
/// an antisym group to be one compact SYMM group (so storage shrinks) with its
/// strictness carried in STRICT. Returns (symmVec, strictVec) of equal length:
///   symmVec[d]   = storage group number at dim d (adjacent-equal = same group)
///   strictVec[d] = 1 if that group drops its diagonal (antisym), else 0
/// A dense/freed axis (arity-1 SymNone) is its own group with strict 0.
let buildSymmVecWithStrict (outputType: IRType) : (int list * int list) =
    match outputType with
    | ArrayElem arr ->
        let mutable symmVec = []
        let mutable strictVec = []
        let mutable groupNum = 1
        let mutable prevCompact = None
        for idx in arr.IndexTypes do
            // All compact classes (sym/herm/antisym) form shrinking storage
            // groups when arity > 1. Antisym differs only by its STRICT flag.
            let isCompact =
                (match idx.Symmetry with
                 | SymSymmetric | SymAntisymmetric | SymHermitian -> true
                 // A wreath group is compact, but NOT as one (SYMM, STRICT)
                 // pair: strictness varies per LEVEL, and the shrinking-row
                 // skeleton this vector describes is a single simplex. Emitting
                 // `strict = 0` here would allocate an inclusive triangle over
                 // prod(ri) axes -- a plausible-looking pool of the wrong size.
                 | SymWreath ->
                     failwith (orbitStorageUnsupported "storage allocation (buildSymmVecWithStrict)"
                                                       (orbitLevelsOf idx))
                 | SymNone -> false) && idx.Rank > 1
            let isStrict = idx.Symmetry = SymAntisymmetric && idx.Rank > 1
            for compIdx in 0 .. idx.Rank - 1 do
                if isCompact && compIdx > 0 then
                    symmVec <- symmVec @ [groupNum]
                    strictVec <- strictVec @ [if isStrict then 1 else 0]
                else
                    if prevCompact = Some true && compIdx = 0 then
                        groupNum <- groupNum + 1
                    symmVec <- symmVec @ [groupNum]
                    strictVec <- strictVec @ [if isStrict then 1 else 0]
                    if not isCompact then
                        groupNum <- groupNum + 1
                prevCompact <- Some isCompact
        (symmVec, strictVec)
    | _ -> ([], [])

// Index-type behavior interface (the storage-class abstraction).
//
// Each index-type CLASS (Rectangular / Symmetric / Antisymmetric / Hermitian,
// and later Compound / Tree / Graph / CG) populates one stateless behavior
// object, keyed on the index type's SymmetryClass and DERIVED, never stored,
// so the class and its behavior cannot drift (see `behaviorFor`).
//
// Methods return BACKEND-NEUTRAL descriptors (AllocSpec, TransposeBehavior),
// never C++ strings -- the IR stays backend-agnostic. A per-backend emitter
// (CodeGen for C++) turns descriptors into concrete code, mirroring how
// allocate/linearize are pre-rolled runtime routines codegen merely CALLS.

/// Names a runtime allocation routine + how its symmetry mask is supplied.
/// Backend-neutral: the C++ emitter maps AllocDense/AllocSymmetric ->
/// `allocate<T,SYMM>` and AllocAntisymmetric -> `allocate_antisym<T>`.
type AllocSpec =
    | AllocDense                       // rectangular: allocate<T, nullptr>
    | AllocSymmetric                   // triangular upper: allocate<T, SYMM-vec>
    | AllocAntisymmetric               // strict simplex: allocate_antisym<T>
    | AllocPerGroupStrict of strict: int list
                                       // mixed strictness across groups: a
                                       // companion STRICT[] mask parallel to
                                       // SYMM-vec (1 = strict, 0 = inclusive/
                                       // dense). Emits allocate_strict<T,
                                       // SYMM, STRICT>. Arises from antisym
                                       // fission (e.g. Idx -> AntisymIdx<2>).
    /// Iterated-wreath (OrbIdx, depth >= 2) pool: a FLAT array of exactly
    /// `cells` scalars in `orb_visit` order (the plan's one hard invariant,
    /// plan-orbidx-bijections section 3). NOT a nested skeleton: a wreath's
    /// rows shrink per LEVEL, so `allocate<>`'s single-simplex recurrence
    /// can't describe it. The C++ emitter SIZES from
    /// `orb_cell_count<Levels...>(n)` and pins it against `cells` at run
    /// time, so the two fold implementations cannot drift silently.
    /// `cells` comes from `OrbRank.cellCountChecked`, the SAME fold
    /// `bufferGroupCardinality` uses, so allocation and cardinality cannot
    /// answer differently. `levels` is the class as the RECORD holds it.
    | AllocWreath of levels: (int * bool) list * extent: int64 * cells: int64
    | AllocUnsupported of reason: string

/// Placement-axis allocator dispatch: which runtime allocator backs an array
/// governed by a given PlacementClass. Level-1 counterpart to behaviorFor
/// (the symmetry axis): the allocator is a property of PLACEMENT, not the
/// value transform, so it lives here. dense -> AllocDense; strict combinadic
/// (antisym) -> AllocAntisymmetric; inclusive combinadic (symmetric AND
/// Hermitian, sharing upper-triangle layout) -> AllocSymmetric. A future
/// PlaceTabulated arm adds the tabulated allocator here (FS0025 flags this
/// match until it does).
let allocRoutineFor (pc: PlacementClass) : AllocSpec =
    match pc with
    | PlaceDense -> AllocDense
    | PlaceCombinatorial SymAntisymmetric -> AllocAntisymmetric
    // A wreath class DOES have an allocator (AllocWreath), but it can't be
    // named from the placement class alone -- the size is the section 4 fold
    // over the LEVEL LIST and extent, which a bare `PlacementClass` doesn't
    // carry; `classifyOutputStorage` reads them off the record. Refusing
    // here (rather than guessing AllocSymmetric, whose triangle sizes a
    // pool the fold never agrees with) keeps this function honest.
    | PlaceCombinatorial SymWreath ->
        AllocUnsupported "OrbIdx (iterated-wreath) allocation cannot be decided from the placement class \
alone: the pool size is the iterated-binomial fold over the class's level list and extent \
(docs/plan-orbit-index-types.md section 4), which a bare PlacementClass does not carry. Route through \
classifyOutputStorage, which reads the level list off the index record."
    | PlaceCombinatorial _ -> AllocSymmetric
    | PlaceTabulated ->
        // Compound storage is runtime-sized from the mask's popcount, via the
        // emitted compound_index_t, not a closed-form allocator. Unreached
        // here until codegen wires it (no caller passes PlaceTabulated yet).
        AllocUnsupported "compound (tabulated) allocation is emitted via compound_index_t at codegen"

/// Semantic result of transposing two dimensions that lie WITHIN one index
/// type (an intra-type dimensional swap). Backend-neutral decision; the C++
/// emitter realizes each (identity = return source; negated/conjugated = a
/// same-shape copy via the corresponding runtime routine; data-move = the
/// existing dense axis-swap copy).
type TransposeBehavior =
    | TIdentity                        // symmetric: storage unchanged, A(i,j)=A(j,i)
    | TNegatedCopy                     // antisymmetric: whole-array sign flip
    | TConjugatedCopy                  // hermitian: whole-array conjugation
    | TDataMove                        // rectangular: physical axis swap (dense copy)
    | TRequiresDecompaction of reason: string  // would break the symmetry relation

/// How a compact group folds an arbitrary index sub-tuple to its canonical
/// (stored) representative -- the FOLD phase of a lazy read (formalism 4.16,
/// 14.2). Backend-neutral; the C++ emitter realizes each as inline fold code.
///   CanonNone    -- rectangular / freed axis: indices are already canonical,
///                  no reorder, always stored (identity fold).
///   CanonSort    -- symmetric / Hermitian: sort within the group, track swap
///                  parity, always stored (diagonal kept).
///   CanonSortStrict -- antisymmetric: sort within the group, track parity, AND
///                  return "not stored" (implicit zero) on any repeated index
///                  (the dropped diagonal / strict-simplex storage).
///   CanonWreathFold -- iterated wreath (OrbIdx, depth >= 2): the section 5 fold of
///                  per-LEVEL sorts, innermost first, with the character
///                  accumulated multiplicatively and the zero set taken at any
///                  '-' level with two equal sub-blocks. Structurally different
///                  from CanonSort/CanonSortStrict, which sort ONE flat block
///                  once; the reference implementation is OrbRank.canonOrb.
///                  Not emitted; every consumer refuses on this arm rather
///                  than degrade to a flat sort, which would fold distinct
///                  orbits onto the same cell.
type CanonicalizeBehavior =
    | CanonNone
    | CanonSort
    | CanonSortStrict
    | CanonWreathFold

/// What transform a lazy read applies to the fetched canonical value given the
/// fold's swap parity -- the TRANSFORM phase (formalism 4.16). Backend-neutral.
///   TfIdentity         -- symmetric / rectangular: value unchanged on swap.
///   TfNegateOnSwap     -- antisymmetric: negate when swap parity is odd.
///   TfConjugateOnSwap  -- Hermitian: conjugate when swap parity is odd
///                        (conj_scalar is identity on real element types, so
///                        Hermitian-of-real degenerates to symmetric for free).
type ReadTransformBehavior =
    | TfIdentity
    | TfNegateOnSwap
    | TfConjugateOnSwap

/// The interface every index-type class populates. Stateless: one shared
/// instance per class. Methods take the live IRIndexType (or relevant
/// metadata) as arguments rather than caching it, so they always read current
/// metadata and cannot go stale.
type IIndexTypeBehavior =
    /// Human-readable class name (diagnostics).
    abstract member ClassName : string
    /// The symmetry class this behavior implements (round-trips with behaviorFor).
    abstract member Symmetry : SymmetryClass
    /// Reject metadata that is contradictory for this class (smart-constructor
    /// guard). Antisymmetric/Symmetric/Hermitian require arity >= 2; Hermitian
    /// is rank-2 only; etc. Ok () means well-formed.
    abstract member Validate : IRIndexType -> Result<unit, string>
    /// What an intra-type transpose of two of this class's dimensions does.
    abstract member TransposeWithin : unit -> TransposeBehavior
    /// How this class folds an index sub-tuple to its canonical stored form
    /// (the FOLD phase of a lazy read). See CanonicalizeBehavior.
    abstract member Canonicalize : unit -> CanonicalizeBehavior
    /// What transform a lazy read applies to the fetched value given the fold's
    /// swap parity (the TRANSFORM phase). See ReadTransformBehavior.
    abstract member ReadTransform : unit -> ReadTransformBehavior

/// Rectangular (no symmetry): dense storage, physical transpose.
type private RectangularBehavior() =
    interface IIndexTypeBehavior with
        member _.ClassName = "Rectangular"
        member _.Symmetry = SymNone
        member _.Validate _ = Ok ()
        member _.TransposeWithin () = TDataMove
        member _.Canonicalize () = CanonNone        // dense: indices already canonical
        member _.ReadTransform () = TfIdentity

/// Symmetric: triangular storage; transpose within the group is the identity
/// (A(i,j) = A(j,i), canonical storage unchanged).
type private SymmetricBehavior() =
    interface IIndexTypeBehavior with
        member _.ClassName = "Symmetric"
        member _.Symmetry = SymSymmetric
        member _.Validate ix =
            if ix.Rank < 2 then Error (sprintf "Symmetric index requires rank >= 2 (got %d): a symmetry relation needs at least two components" ix.Rank)
            else Ok ()
        member _.TransposeWithin () = TIdentity
        member _.Canonicalize () = CanonSort        // sort within group, diagonal kept
        member _.ReadTransform () = TfIdentity      // symmetric: no change on swap

/// Antisymmetric: strict-simplex storage; transpose within the group negates
/// the whole array (any transposition is an odd permutation -> parity -1).
type private AntisymmetricBehavior() =
    interface IIndexTypeBehavior with
        member _.ClassName = "Antisymmetric"
        member _.Symmetry = SymAntisymmetric
        member _.Validate ix =
            if ix.Rank < 2 then Error (sprintf "Antisymmetric index requires rank >= 2 (got %d): an antisymmetry relation needs at least two components" ix.Rank)
            else Ok ()
        member _.TransposeWithin () = TNegatedCopy
        member _.Canonicalize () = CanonSortStrict  // sort; implicit-zero on repeat
        member _.ReadTransform () = TfNegateOnSwap   // negate on odd parity

/// Hermitian: shares symmetric (upper-triangle) storage, conjugation on read;
/// transpose within the group conjugates the whole array. Rank-2 only.
type private HermitianBehavior() =
    interface IIndexTypeBehavior with
        member _.ClassName = "Hermitian"
        member _.Symmetry = SymHermitian
        member _.Validate ix =
            if ix.Rank <> 2 then Error (sprintf "Hermitian index requires rank = 2 (got %d): the Hermitian relation is defined on a matrix (two components)" ix.Rank)
            else Ok ()
        member _.TransposeWithin () = TConjugatedCopy
        member _.Canonicalize () = CanonSort        // sort within group, diagonal kept (real)
        member _.ReadTransform () = TfConjugateOnSwap  // conjugate on odd parity

/// Iterated wreath (OrbIdx, depth >= 2): compact storage over
/// S_r1 wr ... wr S_rd with the character the product of the level signs.
/// Every method here describes the class HONESTLY; emission is what's
/// missing, and the consumers of Canonicalize refuse on CanonWreathFold.
type private WreathBehavior() =
    interface IIndexTypeBehavior with
        member _.ClassName = "Wreath"
        member _.Symmetry = SymWreath
        member _.Validate ix =
            let levels = orbitLevelsOf ix
            if List.isEmpty levels then
                Error "Wreath index carries no level list: a SymWreath record's Extent must be the \
IROrbitClass marker holding [(r,s), ...]"
            elif List.length levels < 2 then
                Error (sprintf "Wreath index of depth %d: depth <= 1 normalizes to Idx / SymIdx / \
AntisymIdx at lowering and must never reach a SymWreath record" (List.length levels))
            elif levels |> List.exists (fun (r, _) -> r < 2) then
                Error (sprintf "Wreath index %s: every level rank must be >= 2 after normalization \
(rank-1 levels are the trivial group and are dropped at either sign)" (ppOrbitLevels levels))
            else
                let axes = levels |> List.fold (fun a (r, _) -> a * r) 1
                if ix.Rank <> axes then
                    Error (sprintf "Wreath index %s acts on %d raw axes but the record's Rank is %d"
                                   (ppOrbitLevels levels) axes ix.Rank)
                else Ok ()
        // Swapping two axes of a wreath class is a permutation of the raw
        // axes not necessarily in the group (only within-level and block-
        // exchange permutations are); no whole-array copy realizes it, so
        // decompaction first is the only sound answer.
        member _.TransposeWithin () =
            TRequiresDecompaction "an OrbIdx (iterated-wreath) group: a raw-axis swap is generally not \
an element of S_r1 wr ... wr S_rd, so it is not realizable as a whole-array transform on the compact pool"
        member _.Canonicalize () = CanonWreathFold
        // The character is the PRODUCT of the per-level signs, so it is +-1 and
        // rides the existing negate-on-odd-parity channel -- provided the fold
        // that produces the parity is the per-level one (CanonWreathFold), not
        // a flat sort. An all-'+' class simply never reports odd parity.
        member _.ReadTransform () = TfNegateOnSwap

// Shared stateless singletons (one per class).
let private rectangularBehavior = RectangularBehavior() :> IIndexTypeBehavior
let private symmetricBehavior = SymmetricBehavior() :> IIndexTypeBehavior
let private antisymmetricBehavior = AntisymmetricBehavior() :> IIndexTypeBehavior
let private hermitianBehavior = HermitianBehavior() :> IIndexTypeBehavior
let private wreathBehavior = WreathBehavior() :> IIndexTypeBehavior

/// Total, exhaustive resolver from symmetry class to behavior. Adding a new
/// SymmetryClass case forces a new arm here (compile error otherwise) -- the
/// openness guarantee: a new index-type class is "write a behavior + one arm".
let behaviorFor (sym: SymmetryClass) : IIndexTypeBehavior =
    match sym with
    | SymNone -> rectangularBehavior
    | SymSymmetric -> symmetricBehavior
    | SymAntisymmetric -> antisymmetricBehavior
    | SymHermitian -> hermitianBehavior
    | SymWreath -> wreathBehavior

/// Derived behavior accessor for an index type. Behavior follows Symmetry;
/// there is no stored Behavior field to fall out of sync.
let behaviorOf (ix: IRIndexType) : IIndexTypeBehavior = behaviorFor ix.Symmetry

/// Active pattern grouping the symmetry-like classes (those backed by compact
/// triangular/simplex storage with a symmetry relation), so call sites that
/// only care about "is this a compact symmetry class" match the group rather
/// than enumerating. Rectangular and (future) Compound/Tree/Graph/CG fall to
/// the `_` branch.
let (|SymmetryLike|_|) (sym: SymmetryClass) : SymmetryClass option =
    match sym with
    // SymWreath belongs here for the same reason it belongs in IxSymmetryLike:
    // it IS a compact symmetry class. Call sites that then assume a single
    // simplex must check the class, not just membership -- they refuse
    // instead.
    | SymSymmetric | SymAntisymmetric | SymHermitian | SymWreath -> Some sym
    | SymNone -> None

/// Validate an index type against its class's well-formedness rules. Smart
/// constructors route through this; a future migration can make IRIndexType
/// only constructible via these guarded builders.
let validateIndexType (ix: IRIndexType) : Result<unit, string> =
    (behaviorOf ix).Validate ix

/// Storage allocation spec for an output array, derived from its index TYPE
/// (not from the kernel's Reynolds descriptor). Source of truth for which
/// C++ allocator to emit. The per-index-class decision comes from
/// allocRoutineFor (the placement axis); the whole-array COMPOSITION rules
/// (a single antisymmetric index spanning all dims is allocatable; antisym
/// mixed with other components is not, since allocate_antisym has no
/// per-group mask) live here, because they are a property of the array's
/// index-list combination, not of any one class.
///
/// CRITICAL distinction from LoopNestCodeGen.IsAntisymmetric: that flag comes
/// from the Reynolds descriptor and describes the COMPUTATION (sign
/// alternation on permutation parity). It is orthogonal to STORAGE: a kernel
/// may antisymmetrize its arithmetic while writing a rectangular output, or
/// an antisymmetric-typed output may be filled by a non-Reynolds kernel.
/// Allocation must key off storage, so it reads the output index type here.
let classifyOutputStorage (outputType: IRType) : AllocSpec =
    match outputType with
    // A wreath component short-circuits the whole composition question: a
    // SOLE OrbIdx group of depth >= 2 over a compile-time extent allocates a
    // flat pool of exactly `cellCountChecked levels n` cells (docs/plan-
    // orbit-index-types.md section 4), in orb_visit order. Size comes from
    // `OrbRank.cellCountChecked`, the SAME fold `bufferGroupCardinality`
    // uses, so allocator and cardinality cannot disagree (overflow is a
    // diagnostic, not section 7.2's silent wraparound).
    //
    // COMBINATIONS ARE REFUSED, deliberately: a wreath pool has no nested
    // skeleton to hang a second dimension group off, and no runtime layout
    // mixes one with a dense or triangular block. Deduction never produces
    // the combination, so this is a backstop for a future producer, not a
    // reachable user program.
    | ArrayElem arr when arr.IndexTypes |> List.exists (fun ix -> ix.Symmetry = SymWreath) ->
        let ix = arr.IndexTypes |> List.find (fun ix -> ix.Symmetry = SymWreath)
        let levels = orbitLevelsOf ix
        (match arr.IndexTypes with
         | [ _ ] ->
             (match orbitBaseExtent ix with
              | IRLit (IRLitInt n) ->
                  (match Blade.OrbRank.cellCountChecked (orbRankLevels levels) n with
                   | Ok cells -> AllocWreath (levels, n, cells)
                   | Error detail ->
                       AllocUnsupported (sprintf "OrbIdx<%s, %d>: the class's cell count cannot be \
computed -- %s. An iterated-wreath class grows one binomial per level, so a deep class over a large \
extent leaves int64 well before its rank does (docs/plan-orbit-index-types.md section 7.2)."
                                                 (ppOrbitLevels levels) n detail))
              | _ ->
                  AllocUnsupported (sprintf "OrbIdx<%s, ?>: a wreath class needs a COMPILE-TIME extent \
to allocate against. Its cell count is the iterated binomial fold over the level list starting from \
the extent, and each level's output is the next level's ground set, so a runtime extent has no \
closed-form pool size." (ppOrbitLevels levels)))
         | _ ->
             AllocUnsupported (sprintf "an OrbIdx<%s, n> group combined with %d other index group(s): a \
wreath pool is a flat cell array with no nested skeleton to juxtapose against, and no runtime layout \
mixes one with a dense or triangular block."
                                       (ppOrbitLevels levels) (List.length arr.IndexTypes - 1)))
    | ArrayElem arr ->
        let antisymIdxs =
            arr.IndexTypes |> List.filter (fun ix -> ix.Symmetry = SymAntisymmetric)
        match antisymIdxs with
        | [] ->
            // No antisymmetric component: symmetric iff buildSymmVec finds a
            // real symmetric block. buildSymmVec groups SymHermitian like
            // SymSymmetric (Hermitian shares compact upper-triangle storage),
            // so hasRealSymmetry covers Hermitian too. Per-class routine for a
            // symmetric/hermitian index is AllocSymmetric; plain index AllocDense.
            let symmVec = buildSymmVec outputType
            if hasRealSymmetry symmVec then AllocSymmetric
            else AllocDense
        | [ single ] when single.Rank = (arr.IndexTypes |> List.sumBy (fun ix -> ix.Rank)) ->
            // Exactly one antisymmetric index spanning every dimension: the
            // pure-antisymmetric shape allocate_antisym supports. Placement-axis
            // routine confirms (PlaceCombinatorial SymAntisymmetric -> AllocAntisymmetric).
            allocRoutineFor (PlaceCombinatorial SymAntisymmetric)
        | _ ->
            // Antisymmetric group(s) combined with other components in one
            // storage block -- the mixed-strictness layout the global DIAGONALS
            // flag cannot express, but the per-group STRICT mask can. This is
            // the compact-residual fission shape (e.g. Idx -> AntisymIdx<2>:
            // a freed dense axis beside a strict residual pair). Each group is
            // uniformly strict (antisym) or dense, so buildSymmVecWithStrict
            // produces a well-formed (SYMM, STRICT) pair; emit allocate_strict.
            // (Sign is handled lazily on read via canon_*, not here.)
            let (_symmVec, strictVec) = buildSymmVecWithStrict outputType
            AllocPerGroupStrict strictVec
    | _ -> AllocDense

// Cross-procedural analysis context. All callable references in IR are
// IRVar(callable.Id, funcType); resolveCallable threads them back to
// the underlying IRCallable via the CallablesTable installed in the
// AsyncLocal context. Consumers (buildLoopNestCodeGen, validator's
// ApplyInfo check, mask-rewrite, exprAttrs IRApp arm) all share this
// resolution path.

type CallablesTable = Map<IRId, IRCallable>

type AnalysisContext = {
    Callables: CallablesTable
    Visited:   Set<IRId>
    /// Per-codegen-pass registry of transient synthetic callables, for
    /// transformations that need "callable with modified body" (e.g.
    /// applyFunctorWrappers' inline-wrap, IRIf guard wrap) without storing
    /// them in module.Functions. `resolveCallable` queries this registry
    /// alongside the module's CallablesTable. Mutable Dictionary for cheap
    /// in-place accumulation; a per-flow AsyncLocal field, so concurrent
    /// module compilations don't interfere.
    SyntheticCallables: System.Collections.Generic.Dictionary<IRId, IRCallable>
}

let private analysisCtxStorage =
    System.Threading.AsyncLocal<AnalysisContext>()

let private emptyAnalysisCtx : AnalysisContext =
    { Callables = Map.empty
      Visited = Set.empty
      SyntheticCallables = System.Collections.Generic.Dictionary<IRId, IRCallable>() }

let private currentAnalysisCtx () : AnalysisContext =
    let v = analysisCtxStorage.Value
    if isNull (box v) then emptyAnalysisCtx else v

/// Install the callables table. Returns the previous context for
/// stack-style save/restore by the caller. The synthetic registry
/// is reset to a fresh empty Dictionary -- each module compilation
/// starts with no synthetic callables. Synthetic callables produced
/// during one module's codegen don't leak into another's.
let setCallablesContext (callables: CallablesTable) : AnalysisContext =
    let prev = currentAnalysisCtx ()
    analysisCtxStorage.Value <-
        { prev with
            Callables = callables
            SyntheticCallables = System.Collections.Generic.Dictionary<IRId, IRCallable>() }
    prev

/// Restore a previously-captured context.
let restoreAnalysisContext (ctx: AnalysisContext) : unit =
    analysisCtxStorage.Value <- ctx

/// Run `action` with fId added to Visited, restoring on completion.
/// Used by the IRApp arm of exprAttrs to mark a function as being
/// walked, so mutual recursion short-circuits when the cycle closes.
let private withVisited (fId: IRId) (action: unit -> 'T) : 'T =
    let prev = currentAnalysisCtx ()
    analysisCtxStorage.Value <- { prev with Visited = Set.add fId prev.Visited }
    try action()
    finally analysisCtxStorage.Value <- prev

/// Register a synthetic callable in the current AnalysisContext's
/// registry and return an IRVar reference to it. The caller must
/// supply a fresh IRId for the callable (typically via
/// IRBuilder.FreshId()) so it doesn't collide with module.Functions
/// ids or other synthetic ids. The returned IRVar can be consumed
/// like any other callable reference -- resolveCallable will find
/// the registered version via the SyntheticCallables registry.
let registerSyntheticCallable (callable: IRCallable) : IRExpr =
    let ctx = currentAnalysisCtx ()
    ctx.SyntheticCallables.[callable.Id] <- callable
    let paramTypes = callable.Params |> List.map (fun p -> p.Type)
    let funcType = mkFuncArrow paramTypes callable.RetType
    IRVar (callable.Id, funcType)

/// Resolve an expression at a "callable position" to the underlying
/// IRCallable: `IRVar(id, _)` resolving in the CallablesTable (module.
/// Functions + let-binding aliases) or the SyntheticCallables registry
/// (codegen-internal synthetics); anything else (or an unresolvable IRVar)
/// returns None.
let resolveCallable (expr: IRExpr) : IRCallable option =
    match expr with
    | IRVar (id, _) ->
        let ctx = currentAnalysisCtx ()
        match Map.tryFind id ctx.Callables with
        | Some c -> Some c
        | None ->
            // Fall through to synthetic registry. Dictionary lookup is
            // O(1); the registry is typically empty or single-digit
            // entries per module compilation.
            match ctx.SyntheticCallables.TryGetValue(id) with
            | true, c -> Some c
            | false, _ -> None
    | _ -> None

// Reynolds peel/resolve helpers
//
// The kernel slot of an ApplyInfo (and of functor-wrapper composition
// sites) may be either a bare callable reference (`IRVar(id, _)`) or
// that same reference wrapped in `IRReynolds(_, isAntisymmetric)`.
// Several passes need to look through the optional Reynolds wrapper to
// reach the underlying callable; these three helpers express the common
// pattern in one place rather than each site open-coding its own peel +
// resolveCallable dance.

/// Captures the flags carried by an `IRReynolds` wrapper. For
/// non-Reynolds kernels both flags are `false`. The invariant
/// `not HasReynolds => not IsAntisymmetric` is preserved by construction
/// in `peelReynolds` (the only constructor in normal use).
type ReynoldsDescriptor = {
    HasReynolds: bool
    IsAntisymmetric: bool
}

/// Peel an `IRReynolds` wrapper if present, returning the inner
/// expression and a descriptor of the wrapper's flags. For non-Reynolds
/// expressions the input is returned unchanged with a descriptor whose
/// flags are both `false`.
let peelReynolds (expr: IRExpr) : IRExpr * ReynoldsDescriptor =
    match expr with
    | IRReynolds (inner, isAnti) ->
        (inner, { HasReynolds = true; IsAntisymmetric = isAnti })
    | other ->
        (other, { HasReynolds = false; IsAntisymmetric = false })

/// Result of resolving a (possibly Reynolds-wrapped) kernel expression
/// to a callable through the CallablesTable + synthetic registry.
type ResolvedKernel = {
    Callable: IRCallable
    Reynolds: ReynoldsDescriptor
}

/// Peel any `IRReynolds` wrapper and resolve the inner expression to a
/// callable via `resolveCallable` (CallablesTable + synthetic registry).
/// Returns `None` if the inner doesn't resolve, regardless of whether a
/// Reynolds wrapper was present.
let resolveKernel (expr: IRExpr) : ResolvedKernel option =
    let (inner, desc) = peelReynolds expr
    resolveCallable inner
    |> Option.map (fun c -> { Callable = c; Reynolds = desc })

/// Apply a transformation to the inner callable of a (possibly
/// Reynolds-wrapped) kernel expression. Preserves the Reynolds wrapper
/// (with its `isAntisymmetric` flag) if present. If the inner doesn't
/// resolve to a callable, returns the original expression unchanged.
let mapKernelInner (transform: IRCallable -> IRExpr) (expr: IRExpr) : IRExpr =
    let (inner, desc) = peelReynolds expr
    match resolveCallable inner with
    | Some c ->
        let transformed = transform c
        if desc.HasReynolds then IRReynolds (transformed, desc.IsAntisymmetric)
        else transformed
    | None -> expr

/// Build LoopNestCodeGen from ApplyInfo
let buildLoopNestCodeGen 
    (info: ApplyInfo) 
    (arrayNames: string list)
    (outputName: string)
    (builder: IRBuilder) : LoopNestCodeGen =
    
    // Use explicit array info from ApplyInfo (not extracted from Loop)
    let arrays = info.Arrays
    let identities = info.Identities
    let arrayTypes = info.ArrayTypes
    let sDimsPerArray = info.SDimsPerArray
    
    // `resolveKernel` peels any `IRReynolds` wrapper and resolves the inner
    // callable via module.Functions or let-binding aliases in the
    // CallablesTable + synthetic registry; captures the wrapper's
    // `isAntisymmetric` flag in the descriptor.
    let (kernelParams, kernelBody, commGroups, captures, isAntisymmetric) =
        match resolveKernel info.Kernel with
        | Some rk ->
            (rk.Callable.Params, rk.Callable.Body, rk.Callable.CommGroups,
             rk.Callable.Captures, rk.Reynolds.IsAntisymmetric)
        | None -> ([], IRLit IRLitUnit, [], [], false)

    // Positions the kernel declared antisymmetric (`where anticomm(a, b)`).
    // Already inside CommGroups (same grouping/iteration license); this list
    // is only the STRICTNESS of the simplex -- a declared-antisym position
    // iterates i < j, never i <= j, since strict-simplex storage has no
    // diagonal cell to write. Empty for every kernel without the clause.
    let declaredAntisymGroups =
        match resolveKernel info.Kernel with
        | Some rk -> rk.Callable.AntisymGroups
        | None -> []
    // Under a Reynolds wrapper the VARIANT owns the output symmetry: a
    // declared clause on the wrapped kernel is an iteration license only,
    // never a storage claim, so the declared list is consulted only outside
    // reynolds (keeps `reynolds(k, Symmetric)` with a stray anticomm clause
    // from iterating off its own storage).
    let inDeclaredAntisym (arrayIdx: int) =
        not info.HasReynolds
        && declaredAntisymGroups |> List.exists (List.contains arrayIdx)

    // Opt-in parallelism: the loop nest is parallelized ONLY if the resolved
    // kernel callable requested OpenMP via an `omp(...)` clause. No clause =>
    // serial (the language default, like C/Rust) -- never a structural default
    // of parallelizing level 0 unconditionally. The genNestPragma strategy
    // logic (collapse vs. dynamic) is the IMPLEMENTATION of "how to
    // parallelize once omp is asked".
    let kernelRequestedOmp =
        match resolveKernel info.Kernel with
        | Some rk -> rk.Callable.IsOmpParallel
        | None -> false
    // The `omp(a: n)` DEPTHS, as (paramIndex, n). Until now this list was built
    // by extractParallelism and then never read: `omp` was effectively a boolean
    // and the collapse depth came purely from bound structure, so `omp(a: 1)` on
    // a 2-level nest still emitted `collapse(2)` -- threading a dimension of an
    // argument that was never licensed.
    let ompDepths =
        match resolveKernel info.Kernel with
        | Some rk -> rk.Callable.Parallelism
        | None -> []

    // Map kernel params to (source, slot). A VIRTUAL source (range<...>) consumes
    // one param PER index-type slot; every other source consumes one. This mirrors
    // buildApplyInfo's expandedRows so param indices line up. The flat param index
    // for (arrayPos, slot) is (sum of spans of earlier sources) + the slot. For
    // single-slot sources every span is 1, so paramStart pos == pos and the
    // mapping degenerates to one-param-per-position.
    let isVirtualSrc pos =
        pos < arrays.Length &&
        (match arrays.[pos] with IRRange _ | IRVirtualReverse _ -> true | _ -> false)
    let paramSpan pos =
        if isVirtualSrc pos && pos < arrayTypes.Length then
            max 1 (arrayTypes.[pos].IndexTypes |> List.sumBy (fun ix -> ix.Rank))
        else 1
    let paramStart pos = List.init (max 0 pos) paramSpan |> List.sum

    // `omp(a: n)` is a LICENSE, not a demand: "up to n dimensions of this
    // argument may carry OpenMP threads". It CAPS the structural strategy
    // rather than replacing it -- `omp(a: 2)` on a nest that can only
    // collapse one level still collapses one; `omp(a: 1)` on a collapsible
    // 2-level nest stops at one instead of silently taking both.
    //
    // Depth counts levels OF THAT ARGUMENT, outermost first (formalism.md
    // section 17.3). A real array owns all its rank components through ONE
    // param, so the license covers its first n components. A virtual source
    // spends one param PER slot, so any n >= 1 covers that param's one level.
    let ompDepthOfParam (pIdx: int) : int option =
        ompDepths |> List.tryPick (fun (i, n) -> if i = pIdx then Some n else None)
    // A clause whose variable names no parameter leaves ompDepths EMPTY while
    // IsOmpParallel stays true (extractParallelism drops unmatched names). That
    // is a source mistake -- TypeCheck warns about it -- but it must not silently
    // turn a requested nest serial, so fall back to an "outermost level only"
    // licence rather than to nothing.
    let licenseUnresolved = kernelRequestedOmp && List.isEmpty ompDepths
    let isLevelLicensed (arrayPos: int) (rankIdx: int) (level: int) : bool =
        if not kernelRequestedOmp then false
        elif licenseUnresolved then level = 0
        else
            let (pIdx, ordinal) =
                if isVirtualSrc arrayPos then (paramStart arrayPos + rankIdx, 0)
                else (paramStart arrayPos, rankIdx)
            match ompDepthOfParam pIdx with
            | Some n -> ordinal < n
            | None -> false

    // Helper: create an ElementBinding for an array at a given arity component
    let mkElement (arrayPos: int) (rankComponent: int) (dimIndex: int) =
        let arrName = if arrayPos < arrayNames.Length then arrayNames.[arrayPos] else sprintf "arr%d" arrayPos
        let arrType = if arrayPos < arrayTypes.Length then Some arrayTypes.[arrayPos] else None
        let elemType = arrType |> Option.map (fun t -> t.ElemType) |> Option.defaultValue (IRTScalar ETFloat64)
        // ArrayRank counts LOOP LEVELS, not total index rank: a compound slot is
        // ONE level (the cardinality axis) regardless of mask rank, matching
        // buildRawLoopLevels. For dense/symmetric slots level count == Rank, so
        // this is unchanged there. genElementBindingNew's
        // resultRank = ArrayRank - levelsConsumed relies on this level count.
        let arrRank = arrType |> Option.map (fun t -> t.IndexTypes |> List.sumBy (fun i -> match i with IxCompound -> 1 | _ -> i.Rank))
                              |> Option.defaultValue 1
        // This level's slot within its source, by rank component (rank-1
        // slots: component == slot position; multi-rank slots consume one
        // component per rank, mirroring flatParamIdx below).
        let slotAt (idxs: IRIndexType list) (rc: int) =
            let rec go rem acc =
                match rem with
                | [] -> None
                | (ix: IRIndexType) :: rest ->
                    if rc < acc + ix.Rank then Some ix else go rest (acc + ix.Rank)
            go idxs 0
        let slotTag =
            arrType
            |> Option.bind (fun t -> slotAt t.IndexTypes rankComponent)
            |> Option.bind (fun ix -> ix.Tag)
        let virtualKind =
            if arrayPos < arrays.Length then
                match arrays.[arrayPos] with
                | IRRange (_, offset) ->
                    // Halo slot: the center's start offset rides the slot's
                    // "__halowin|" TAG (per-slot -- IRRange's single offset is
                    // shared by all slots, which multi-slot ranges like
                    // range<halo<Lat,..>, halo<Lon,..>> cannot use).
                    match slotTag |> Option.bind haloStartOffsetOfTag with
                    | Some s when s > 0L -> VirtualRange (Some (IRLit (IRLitInt s)))
                    | Some _ -> VirtualRange offset
                    | None -> VirtualRange offset
                | IRVirtualReverse _ -> VirtualReverse
                | _ -> RealArray
            else RealArray
        // Per-slot param for a virtual source (range<...>): flat param index
        // = (sum of earlier sources' spans, via paramStart) + this level's
        // position WITHIN its source (levelInfo.RankIndex, which resets per
        // source) -- range<SymIdx<2,N>> yields two params (i, j) at
        // RankIndex 0, 1. (dimIndex = LocalDimIndex would be WRONG: it's
        // shared across a multi-rank type's components, collapsing them onto
        // the first param -- the range<SymIdx<2,N>> "__v3 not declared" bug.)
        // Real/non-virtual sources resolve to paramStart pos.
        let flatParamIdx =
            if isVirtualSrc arrayPos then paramStart arrayPos + rankComponent
            else paramStart arrayPos
        let param = if flatParamIdx >= 0 && flatParamIdx < kernelParams.Length then Some kernelParams.[flatParamIdx] else None
        let paramName = param |> Option.map (fun p -> p.Name) |> Option.defaultValue (sprintf "p%d" arrayPos)
        let paramVarId = param |> Option.map (fun p -> p.VarId) |> Option.defaultValue -1
        {
            ArrayPosition = arrayPos
            ArrayName = arrName
            ParamName = paramName
            ParamVarId = paramVarId
            DimIndex = dimIndex
            RankComponent = rankComponent
            ArrayElemType = elemType
            ArrayRank = arrRank
            Virtual = virtualKind
            SlotTag = slotTag
        }
    
    let bindings =
        if info.IsCoIteration then
            // Co-iteration over the PRODUCT of the shared records. A plain
            // rank-1 record contributes ONE level at its own extent; a packed
            // symmetric/antisymmetric record contributes Rank triangular levels
            // over its flat canonical cells (bounds depend on the record's own
            // earlier levels, strict offset for antisym) -- byte-identical to
            // plain single-record behavior when the list is [packed].
            // All operands peel at EVERY level. Each level's extent/dim-ref
            // comes from its record and the record's cumulative base dim, so
            // non-square products (range<Lat, Lon> with Lat != Lon) bound
            // correctly -- a single-record shortcut hardcoding dim 0 would not.
            let sharedRecords = info.SharedIndexTypes
            // Reference first real array for extent lookups
            let refArrayName = if arrayNames.Length > 0 then arrayNames.[0] else "arr0"
            // Base dim in refArray's extents for each record = cumulative prior
            // rank; also equals the record's base global loop level.
            let baseDims =
                sharedRecords
                |> List.scan (fun acc sr -> acc + sr.Rank) 0
                |> List.take sharedRecords.Length
            List.zip sharedRecords baseDims
            |> List.collect (fun (sr, baseDim) ->
                let isAntisymmetric = sr.Symmetry = SymAntisymmetric
                let isTriangular = sr.Symmetry = SymSymmetric || isAntisymmetric
                [0 .. sr.Rank - 1] |> List.map (fun k ->
                    let level = baseDim + k
                    let indexName = sprintf "__i%d" level
                    // Triangular bounds chain within the RECORD's own levels only.
                    let deps = if isTriangular && k > 0 then [baseDim .. level - 1] else []
                    let strictOffset =
                        if isTriangular && isAntisymmetric then k
                        else 0
                    // Which levels the `omp(...)` clause LICENSES. No clause =>
                    // serial (the default). Triangularity does not veto a
                    // licensed level: the outer loop of a triangular nest is
                    // independently parallelizable (each outer index owns a
                    // disjoint sub-slab); genNestPragma picks the safe strategy
                    // (collapse vs dynamic) from the licensed prefix.
                    //
                    // CO-ITERATION: every argument is peeled at EVERY level, so
                    // each licensed argument's depth licenses that many levels
                    // from the outside in; the most permissive one wins. (There
                    // is no per-argument level ownership to distinguish here --
                    // that only exists on the outer-product path below.)
                    let coIterLicense =
                        if not kernelRequestedOmp then 0
                        elif licenseUnresolved then 1
                        elif List.isEmpty ompDepths then 0
                        else ompDepths |> List.map snd |> List.max
                    let isParallel = level < coIterLicense
                    let state =
                        if isTriangular && k > 0 then SCSymmetric
                        else SCNeither
                    // All arrays peel at this level
                    let elements =
                        [0 .. arrayNames.Length - 1] |> List.map (fun arrIdx ->
                            mkElement arrIdx level level)
                    {
                        Level = level
                        IndexName = indexName
                        Extent = sr.Extent
                        ExtentArrayRef = refArrayName
                        ExtentDimRef = baseDim  // record's base dim (packed: all k share it)
                        BoundDependencies = deps
                        StrictOffset = strictOffset
                        FusedRank = None
                        IsParallel = isParallel
                        State = state
                        Elements = elements
                    }))
        else
            // Outer product: one element per level
            let loopLevels = buildLoopLevelStructure identities commGroups arrayTypes sDimsPerArray
            let triangularLevels = info.TriangularLevels
            let symcomStates = info.SymcomStates
            
            // Compute the iminMap from the single canonical axis grouping
            // (computeAxisGroups), so chaining stays in lock-step with
            // triangular-level detection, the loop reorder, and the output
            // storage layout. A level is either the FIRST member of its axis
            // group (root, maps to itself) or chains to the NEAREST EARLIER
            // level sharing its group (descends triangularly). The grouping
            // encodes the symmetry rule: a repeated array under comm forms
            // ONE rank-r simplex over its S-axis (joint symmetry, r! once);
            // distinct groups multiply independently -- never per-dimension
            // for one group, never across arrays via shared index types
            // (docs/formalism.md section 12.4).
            let axisGroupIds = computeAxisGroups identities arrayTypes commGroups sDimsPerArray
            let groupAt i = if i < axisGroupIds.Length then axisGroupIds.[i] else -1
            let iminMap = 
                loopLevels |> List.mapi (fun globalIdx _level ->
                    let g = groupAt globalIdx
                    let prior =
                        [ globalIdx - 1 .. -1 .. 0 ]
                        |> List.tryFind (fun j -> groupAt j = g)
                    match prior with
                    | Some j -> j
                    | None -> globalIdx)   // first member of this axis group = root
            
            // Compute dependency path for each level
            let rec dependencyPath (level: int) : int list =
                if level < 0 || level >= iminMap.Length then []
                elif iminMap.[level] = level then []
                else iminMap.[level] :: dependencyPath iminMap.[level]
            
            let boundDependencies = loopLevels |> List.mapi (fun i _ -> dependencyPath i)
            
            loopLevels |> List.map (fun levelInfo ->
                let level = levelInfo.GlobalLevelIndex
                let indexName = sprintf "__i%d" level
                let arrayPos = levelInfo.ArrayIndex
                let arrName = if arrayPos < arrayNames.Length then arrayNames.[arrayPos] else sprintf "arr%d" arrayPos
                
                let deps = if level < boundDependencies.Length then boundDependencies.[level] else []
                let isTriangular = level < triangularLevels.Length && triangularLevels.[level]
                let state = if level < symcomStates.Length then symcomStates.[level] else SCNeither
                // Which levels the `omp(...)` clause licenses (see the
                // shared-index path note above). OUTER PRODUCT: each level is
                // owned by exactly one source, so the licence is checked against
                // THAT argument's depth, with RankIndex as the level's ordinal
                // within its source. genNestPragma then picks collapse vs.
                // dynamic over the licensed prefix.
                let isParallel = isLevelLicensed arrayPos levelInfo.RankIndex level
                // Strict (j > i > ...) bounds are required whenever the OUTPUT
                // storage is antisymmetric -- strict-triangular storage has no
                // diagonal to visit. Three ways the output is antisymmetric:
                // (1) the input index type is itself SymAntisymmetric; (2) a
                // Reynolds antisymmetrization over a commutative group (inputs
                // are plain SymNone, so IndexSpace.Symmetry alone would miss
                // it); (3) the kernel DECLARED this position antisymmetric
                // (`where anticomm(a, b)`) -- same shape as (2) but the flag
                // rides the callable, not a Reynolds wrapper, and is checked
                // per LEVEL so a declared pair can't make an unrelated group
                // strict.
                //
                // The strict offset is CUMULATIVE across the group: level a
                // (0-based within the strict group) must start a slots past
                // the group base, since each prior index already consumed
                // one diagonal slot -- equal to List.length deps (level 1 ->
                // 1, level 2 -> 2, ...). A flat offset of 1 is correct only
                // at rank 2; at rank >= 3 it under-shifts, visiting
                // non-canonical tuples that alias storage cells (the antisym
                // rank-3 storage-collision bug).
                //
                // SCOPE: this offset belongs to the loop BOUND / the ABSOLUTE
                // coordinate of a flat (not-yet-peeled) read, NOT a storage
                // subscript. A peeled row of a strict-packed array is already
                // diagonal-free and shortened by the allocator, so the
                // 0-based loop var IS the slot; re-adding this offset there
                // walks one cell past the row (CodeGen's genElementBindingNew
                // isSliced arm, Interp's peelElement).
                let strictOffset =
                    if isTriangular &&
                       (levelInfo.IndexSpace.Symmetry = SymAntisymmetric
                        || isAntisymmetric
                        || inDeclaredAntisym arrayPos)
                    then List.length deps
                    else
                        // Compound-inner halo: the interior shrink cannot fold
                        // into the extent (the mask cardinality is runtime),
                        // so it rides the bound subtraction. Safe here: this
                        // level's elements are virtual window peels, so no
                        // array subscript couples to StrictOffset. Dense halo
                        // slots pre-shrink their extent at typecheck and stay 0.
                        match levelInfo.IndexSpace.Tag, levelInfo.IndexSpace.Extent with
                        | Some tag, IRCompoundMask _ ->
                            match haloShrinkOfTag tag with
                            | Some s -> int s
                            | None -> 0
                        | _ -> 0
                
                // A compound VIRTUAL source (range<CompoundIdx<m>>) is ONE loop
                // level but spans SourceRank kernel params (one per mask
                // dimension). Emit one element PER rank component so every
                // coordinate param gets bound (each extracts component rc of
                // the cell tuple: genElementBindingNew's unhash(r)[rc] arm).
                // A real compound ARRAY keeps the single peel element (it
                // reads .data[r], not per-axis coordinates).
                let elements =
                    let isCompoundLevel =
                        match levelInfo.IndexSpace.Extent with IRCompoundMask _ | IRSparseKeys _ -> true | _ -> false
                    let isVirtualSource =
                        arrayPos < arrays.Length &&
                        (match arrays.[arrayPos] with IRRange _ -> true | _ -> false)
                    match levelInfo.FusedFactors with
                    | Some factors ->
                        // Fused JOINT level: one element per source dim;
                        // each decodes its coordinate from the compound loop
                        // index and peels one dimension (genElementBindingNew's
                        // fused arm). RankComponent doubles as the dim position.
                        [0 .. factors.Length - 1]
                        |> List.map (fun rc -> mkElement arrayPos rc rc)
                    | None ->
                    if isCompoundLevel && isVirtualSource then
                        [0 .. levelInfo.IndexSpace.SourceRank - 1]
                        |> List.map (fun rc -> mkElement arrayPos rc levelInfo.LocalDimIndex)
                    else
                        [mkElement arrayPos levelInfo.RankIndex levelInfo.LocalDimIndex]

                {
                    Level = level
                    IndexName = indexName
                    Extent = levelInfo.IndexSpace.Extent
                    ExtentArrayRef = arrName
                    ExtentDimRef = levelInfo.LocalDimIndex
                    BoundDependencies = deps
                    StrictOffset = strictOffset
                    FusedRank = levelInfo.FusedFactors |> Option.map List.length
                    IsParallel = isParallel
                    State = state
                    Elements = elements
                })
    
    let outputSymmVec = buildSymmVec info.OutputType
    
    {
        Bindings = bindings
        KernelExpr = kernelBody
        KernelParams = kernelParams
        Captures = captures
        OutputName = outputName
        OutputType = info.OutputType
        OutputSymmVec = outputSymmVec
        InputArrayNames = arrayNames
        SpeedupFactor = info.SpeedupFactor
        HasReynolds = info.HasReynolds
        IsAntisymmetric = isAntisymmetric
        FoldWrapper = None
        MpiSlab = false
        OmpRequested = kernelRequestedOmp
        FoldChunk = None
    }

// Canonical expression traversal -- ExprShape (audit section 3.2)
//
// THE one place that knows every IRExpr variant's immediate expression
// children. Every generic walker (mapIRExpr, collectVarRefsIR,
// collectTypesInExpr, exprAttrs) folds over this shape, so a new variant is
// added in exactly one place. Wildcard-free: a new variant fails to compile
// until declared here, an exhaustiveness guarantee a per-walker `| _ ->`
// fallback would silently destroy.
//
// Scope decisions (uniform across all walkers by construction): IRIndexType
// is OPAQUE to traversal (extent-marker expressions inside it are reached by
// dedicated extent paths, never generic traversal); boundary pads and range
// offsets ARE children (the BndPad expression in IRShift/IRAlign and
// IRRange's offset are real sub-expressions -- hand-maintained per-walker
// versions can disagree about this without the canonical shape).
//
// `rebuild` requires exactly the children it handed out (same count, same
// order); anything else is a hard failure, never a silent drop.

/// Child-list mismatch in a rebuild -- always a walker bug, never recoverable.
let private badChildren (ctor: string) : 'a =
    failwithf "ExprShape.rebuild: child list does not match %s's shape" ctor

/// Total active pattern: an expression's immediate children, plus a function
/// rebuilding the same variant around replacement children.
let (|ExprShape|) (expr: IRExpr) : IRExpr list * (IRExpr list -> IRExpr) =
    match expr with
    // -- Leaves: no expression children ------------------------------------
    | IRLit _ | IRVar _ | IRParam _ | IRNth | IRZero | IROpaqueExtent
    | IRVirtualReverse _ | IRArity _ ->
        [], (function [] -> expr | _ -> badChildren "leaf")
    | IRRange (idxTys, offset) ->
        Option.toList offset,
        (function
         | [] when Option.isNone offset -> expr
         | [off'] when Option.isSome offset -> IRRange (idxTys, Some off')
         | _ -> badChildren "IRRange")

    // -- One child ----------------------------------------------------------
    | IRUnaryOp (op, e) -> [e], (function [e'] -> IRUnaryOp (op, e') | _ -> badChildren "IRUnaryOp")
    | IRTupleProj (e, i, flat) -> [e], (function [e'] -> IRTupleProj (e', i, flat) | _ -> badChildren "IRTupleProj")
    | IRTupleDecons e -> [e], (function [e'] -> IRTupleDecons e' | _ -> badChildren "IRTupleDecons")
    | IRFieldAccess (e, fld) -> [e], (function [e'] -> IRFieldAccess (e', fld) | _ -> badChildren "IRFieldAccess")
    | IRPure e -> [e], (function [e'] -> IRPure e' | _ -> badChildren "IRPure")
    | IRCompute e -> [e], (function [e'] -> IRCompute e' | _ -> badChildren "IRCompute")
    | IRReynolds (e, anti) -> [e], (function [e'] -> IRReynolds (e', anti) | _ -> badChildren "IRReynolds")
    | IRTranspose (e, d1, d2) -> [e], (function [e'] -> IRTranspose (e', d1, d2) | _ -> badChildren "IRTranspose")
    | IRDecompact (e, d) -> [e], (function [e'] -> IRDecompact (e', d) | _ -> badChildren "IRDecompact")
    | IREigh e -> [e], (function [e'] -> IREigh e' | _ -> badChildren "IREigh")
    | IRHaloUnhash (w, o) -> [w], (function [w'] -> IRHaloUnhash (w', o) | _ -> badChildren "IRHaloUnhash")
    | IRArrayNegate e -> [e], (function [e'] -> IRArrayNegate e' | _ -> badChildren "IRArrayNegate")
    | IRArrayConjugate e -> [e], (function [e'] -> IRArrayConjugate e' | _ -> badChildren "IRArrayConjugate")
    | IRReverse (e, d) -> [e], (function [e'] -> IRReverse (e', d) | _ -> badChildren "IRReverse")
    | IRDiag e -> [e], (function [e'] -> IRDiag e' | _ -> badChildren "IRDiag")
    | IRRank e -> [e], (function [e'] -> IRRank e' | _ -> badChildren "IRRank")
    | IRExtent (e, d) -> [e], (function [e'] -> IRExtent (e', d) | _ -> badChildren "IRExtent")
    | IRRaggedLookup e -> [e], (function [e'] -> IRRaggedLookup e' | _ -> badChildren "IRRaggedLookup")
    | IRCompoundMask e -> [e], (function [e'] -> IRCompoundMask e' | _ -> badChildren "IRCompoundMask")
    | IRCompoundProject (e, plen) -> [e], (function [e'] -> IRCompoundProject (e', plen) | _ -> badChildren "IRCompoundProject")
    | IRSparseKeys (SkRuntime e) -> [e], (function [e'] -> IRSparseKeys (SkRuntime e') | _ -> badChildren "IRSparseKeys")
    | IRSparseKeys (SkStatic _) -> [], (function [] -> expr | _ -> badChildren "IRSparseKeys")   // baked entries: no child exprs
    // The level list is compile-time data, never an expression; the BASE extent
    // is an ordinary extent expression and is exposed as the one child, so
    // substitution / varref collection / folding reach it exactly as they reach
    // a plain Idx extent.
    | IROrbitClass (levels, n) ->
        [n], (function [n'] -> IROrbitClass (levels, n') | _ -> badChildren "IROrbitClass")
    | IRUnique e -> [e], (function [e'] -> IRUnique e' | _ -> badChildren "IRUnique")
    | IRBlocked (idxTy, bs) -> [bs], (function [bs'] -> IRBlocked (idxTy, bs') | _ -> badChildren "IRBlocked")

    // -- Two children ---------------------------------------------------------
    | IRBinOp (mode, op, l, r) -> [l; r], (function [l'; r'] -> IRBinOp (mode, op, l', r') | _ -> badChildren "IRBinOp")
    | IRComplex (re, im) -> [re; im], (function [re'; im'] -> IRComplex (re', im') | _ -> badChildren "IRComplex")
    | IRTupleCons (h, t) -> [h; t], (function [h'; t'] -> IRTupleCons (h', t') | _ -> badChildren "IRTupleCons")
    | IRBind (c, k) -> [c; k], (function [c'; k'] -> IRBind (c', k') | _ -> badChildren "IRBind")
    | IRParallel (a, b, d) -> [a; b], (function [a'; b'] -> IRParallel (a', b', d) | _ -> badChildren "IRParallel")
    | IRFusion (a, b) -> [a; b], (function [a'; b'] -> IRFusion (a', b') | _ -> badChildren "IRFusion")
    | IRChoice (a, b) -> [a; b], (function [a'; b'] -> IRChoice (a', b') | _ -> badChildren "IRChoice")
    | IRFallback (a, b) -> [a; b], (function [a'; b'] -> IRFallback (a', b') | _ -> badChildren "IRFallback")
    | IRArrayProduct (a, b) -> [a; b], (function [a'; b'] -> IRArrayProduct (a', b') | _ -> badChildren "IRArrayProduct")
    | IRComposeObj (a, b) -> [a; b], (function [a'; b'] -> IRComposeObj (a', b') | _ -> badChildren "IRComposeObj")
    | IRComposeMeth (a, b) -> [a; b], (function [a'; b'] -> IRComposeMeth (a', b') | _ -> badChildren "IRComposeMeth")
    | IRCompose (a, b) -> [a; b], (function [a'; b'] -> IRCompose (a', b') | _ -> badChildren "IRCompose")
    | IRFunctorMap (fn, c) -> [fn; c], (function [fn'; c'] -> IRFunctorMap (fn', c') | _ -> badChildren "IRFunctorMap")
    | IRGuard (c, b) -> [c; b], (function [c'; b'] -> IRGuard (c', b') | _ -> badChildren "IRGuard")
    | IRReplicate (count, body) -> [count; body], (function [c'; b'] -> IRReplicate (c', b') | _ -> badChildren "IRReplicate")
    | IRMask (a, p) -> [a; p], (function [a'; p'] -> IRMask (a', p') | _ -> badChildren "IRMask")
    | IRIntersect (a, b) -> [a; b], (function [a'; b'] -> IRIntersect (a', b') | _ -> badChildren "IRIntersect")
    | IRUnion (a, b) -> [a; b], (function [a'; b'] -> IRUnion (a', b') | _ -> badChildren "IRUnion")
    | IRContains (a, v) -> [a; v], (function [a'; v'] -> IRContains (a', v') | _ -> badChildren "IRContains")
    | IRGroupBy (v, k) -> [v; k], (function [v'; k'] -> IRGroupBy (v', k') | _ -> badChildren "IRGroupBy")
    | IRSort (a, k) -> [a; k], (function [a'; k'] -> IRSort (a', k') | _ -> badChildren "IRSort")
    | IRReduce (a, k, None) -> [a; k], (function [a'; k'] -> IRReduce (a', k', None) | _ -> badChildren "IRReduce")
    | IRReduce (a, k, Some i) -> [a; k; i], (function [a'; k'; i'] -> IRReduce (a', k', Some i') | _ -> badChildren "IRReduce")
    | IRReduceCompute (c, k, i) -> [c; k; i], (function [c'; k'; i'] -> IRReduceCompute (c', k', i') | _ -> badChildren "IRReduceCompute")
    | IRProdSum args -> args, (fun args' -> IRProdSum args')
    | IRPolyIndex (p, i) -> [p; i], (function [p'; i'] -> IRPolyIndex (p', i') | _ -> badChildren "IRPolyIndex")
    | IRPolyTail (p, drop) -> [p], (function [p'] -> IRPolyTail (p', drop) | _ -> badChildren "IRPolyTail")
    | IRAssign (t, v) -> [t; v], (function [t'; v'] -> IRAssign (t', v') | _ -> badChildren "IRAssign")
    | IRConstraintCheck (c, msg, sp) -> [c], (function [c'] -> IRConstraintCheck (c', msg, sp) | _ -> badChildren "IRConstraintCheck")
    | IRCurry (arr, idx, r) -> [arr; idx], (function [arr'; idx'] -> IRCurry (arr', idx', r) | _ -> badChildren "IRCurry")
    | IRGram (l, r, same) -> [l; r], (function [l'; r'] -> IRGram (l', r', same) | _ -> badChildren "IRGram")
    | IRMatmul (l, r) -> [l; r], (function [l'; r'] -> IRMatmul (l', r') | _ -> badChildren "IRMatmul")
    | IRLet (id, v, b) -> [v; b], (function [v'; b'] -> IRLet (id, v', b') | _ -> badChildren "IRLet")

    // -- Three children -------------------------------------------------------
    | IRIf (c, t, e) -> [c; t; e], (function [c'; t'; e'] -> IRIf (c', t', e') | _ -> badChildren "IRIf")
    | IRSlice (arr, d, s, e) -> [arr; s; e], (function [arr'; s'; e'] -> IRSlice (arr', d, s', e') | _ -> badChildren "IRSlice")
    | IRSubset (arr, d, s, len) -> [arr; s; len], (function [arr'; s'; len'] -> IRSubset (arr', d, s', len') | _ -> badChildren "IRSubset")
    | IRForRange (vid, lo, hi, body) -> [lo; hi; body], (function [lo'; hi'; b'] -> IRForRange (vid, lo', hi', b') | _ -> badChildren "IRForRange")
    | IRShift (arr, d, off, bnd) ->
        (match bnd with
         | BndPad p ->
             [arr; off; p],
             (function [arr'; off'; p'] -> IRShift (arr', d, off', BndPad p') | _ -> badChildren "IRShift")
         | BndShrink | BndPeriodic | BndReflect ->
             [arr; off],
             (function [arr'; off'] -> IRShift (arr', d, off', bnd) | _ -> badChildren "IRShift"))

    // -- Head + list ----------------------------------------------------------
    | IRIndex (arr, idxs, ident) ->
        arr :: idxs,
        (function
         | arr' :: idxs' when idxs'.Length = idxs.Length -> IRIndex (arr', idxs', ident)
         | _ -> badChildren "IRIndex")
    | IRApp (f, args, rt) ->
        f :: args,
        (function
         | f' :: args' when args'.Length = args.Length -> IRApp (f', args', rt)
         | _ -> badChildren "IRApp")

    // -- Lists ----------------------------------------------------------------
    | IRArrayLit (es, ty) -> es, (fun es' -> if es'.Length = es.Length then IRArrayLit (es', ty) else badChildren "IRArrayLit")
    | IRTuple es -> es, (fun es' -> if es'.Length = es.Length then IRTuple es' else badChildren "IRTuple")
    | IRSequence es -> es, (fun es' -> if es'.Length = es.Length then IRSequence es' else badChildren "IRSequence")
    | IRZip es -> es, (fun es' -> if es'.Length = es.Length then IRZip es' else badChildren "IRZip")
    | IRStack es -> es, (fun es' -> if es'.Length = es.Length then IRStack es' else badChildren "IRStack")
    | IRJoin (es, d) -> es, (fun es' -> if es'.Length = es.Length then IRJoin (es', d) else badChildren "IRJoin")
    | IRGroupKeys ks -> ks, (fun ks' -> if ks'.Length = ks.Length then IRGroupKeys ks' else badChildren "IRGroupKeys")
    | IRAlign (es, spec) ->
        (match spec.Boundary with
         | BndPad p ->
             es @ [p],
             (fun cs ->
                 if cs.Length <> es.Length + 1 then badChildren "IRAlign"
                 else
                     let es', rest = List.splitAt es.Length cs
                     IRAlign (es', { spec with Boundary = BndPad (List.exactlyOne rest) }))
         | BndShrink | BndPeriodic | BndReflect ->
             es, (fun es' -> if es'.Length = es.Length then IRAlign (es', spec) else badChildren "IRAlign"))
    | IRStructLit (tn, fields) ->
        List.map snd fields,
        (fun es' ->
            if es'.Length <> fields.Length then badChildren "IRStructLit"
            else IRStructLit (tn, List.map2 (fun (n, _) e' -> (n, e')) fields es'))

    // -- Match: flat child list is scrutinee, then per-case guard?/body ------
    | IRMatch (scrut, cases) ->
        let caseChildren = cases |> List.collect (fun c -> Option.toList c.Guard @ [c.Body])
        scrut :: caseChildren,
        (function
         | scrut' :: rest ->
             // Re-thread the flat list back through the (guard?, body) case
             // structure; leftovers or shortfalls are shape violations.
             let cases', leftover =
                 cases |> List.fold (fun (acc, remaining) c ->
                     match c.Guard, remaining with
                     | Some _, g' :: b' :: tl -> (acc @ [{ c with Guard = Some g'; Body = b' }], tl)
                     | None, b' :: tl -> (acc @ [{ c with Body = b' }], tl)
                     | _ -> badChildren "IRMatch") ([], rest)
             if not (List.isEmpty leftover) then badChildren "IRMatch"
             else IRMatch (scrut', cases')
         | [] -> badChildren "IRMatch")

    // -- Info-record combinators ----------------------------------------------
    | IRMethodFor info ->
        info.Arrays,
        (fun arrs' ->
            if arrs'.Length = info.Arrays.Length then IRMethodFor { info with Arrays = arrs' }
            else badChildren "IRMethodFor")
    | IRObjectFor info ->
        [info.Kernel], (function [k'] -> IRObjectFor { info with Kernel = k' } | _ -> badChildren "IRObjectFor")
    | IRApplyCombinator info ->
        info.Loop :: info.Kernel :: info.Arrays,
        (function
         | l' :: k' :: arrs' when arrs'.Length = info.Arrays.Length ->
             IRApplyCombinator { info with Loop = l'; Kernel = k'; Arrays = arrs' }
         | _ -> badChildren "IRApplyCombinator")
    | IRComposeApply info ->
        info.Composition :: info.InputArrays,
        (function
         | c' :: arrs' when arrs'.Length = info.InputArrays.Length ->
             IRComposeApply { info with Composition = c'; InputArrays = arrs' }
         | _ -> badChildren "IRComposeApply")

/// Immediate expression children of a node, in canonical order.
let childrenOf (ExprShape (children, _)) : IRExpr list = children

/// Rebuild a node around replacement children (same count/order as
/// childrenOf handed out).
let rebuildWith (expr: IRExpr) (children: IRExpr list) : IRExpr =
    let (ExprShape (_, rebuild)) = expr
    rebuild children

/// Pattern bindings: IRPatVar introduces an IRId visible in the case body
/// and (if present) the guard. Nested patterns (tuple, cons, variant
/// payload) accumulate all their child bindings.
let rec patternBoundIds (pat: IRPattern) : Set<IRId> =
    match pat with
    | IRPatWild | IRPatLit _ -> Set.empty
    | IRPatVar id -> Set.singleton id
    | IRPatTuple pats -> pats |> List.map patternBoundIds |> Set.unionMany
    | IRPatCons (h, t) -> Set.union (patternBoundIds h) (patternBoundIds t)
    | IRPatVariant (_, _, Some p, _) -> patternBoundIds p
    | IRPatVariant (_, _, None, _) -> Set.empty

/// The variants that introduce variable scopes, factored out for
/// binder-aware dispatchers (exprAttrs today; any future capture or escape
/// analysis). Returns the children NOT under a binder, plus one
/// (boundIds, scopedChildren) group per scope. Non-binding variants return
/// None and fall through to the generic ExprShape arm of whichever
/// dispatcher is asking -- so a new binding variant needs exactly one case
/// here to get correct scoping everywhere.
let (|BinderShape|_|) (expr: IRExpr) : (IRExpr list * (Set<IRId> * IRExpr list) list) option =
    match expr with
    | IRLet (id, value, body) ->
        // `value` is deliberately OUTSIDE the scope: a reference to `id`
        // inside its own value is ill-formed IR, and leaving it unscoped
        // keeps such a bug visible as a free var at the outer level.
        Some ([value], [Set.singleton id, [body]])
    | IRForRange (vid, lo, hi, body) ->
        Some ([lo; hi], [Set.singleton vid, [body]])
    | IRMatch (scrut, cases) ->
        // Pattern bindings are visible in both the guard and the body.
        Some ([scrut],
              cases |> List.map (fun c ->
                  (patternBoundIds c.Pattern, Option.toList c.Guard @ [c.Body])))
    | _ -> None

// Expression Mapping (bottom-up rewriter)

/// Apply f to every sub-expression bottom-up, then to the root.
/// f should return the expression unchanged for cases it doesn't handle.
/// Generic recursion is a fold over ExprShape -- variant-specific structure
/// lives entirely in the shape enumeration above.
let rec mapIRExpr (f: IRExpr -> IRExpr) (expr: IRExpr) : IRExpr =
    let mapped =
        match expr with
        | ExprShape ([], _) -> expr
        | ExprShape (children, rebuild) -> rebuild (children |> List.map (mapIRExpr f))
    f mapped

/// Collect every variable id referenced (IRVar) anywhere in an expression.
/// The one var-ref collector (audit section 3.2): capture computation and
/// match-case usage checks in CodeGen call this rather than keeping their
/// own duplicate. Recursion is the ExprShape fold, so no variant's subtree
/// can be silently skipped by a stray `| _ -> Set.empty` catchall.
///
/// Scoping contract: only IRForRange subtracts its binder -- its loop var is
/// synthesized and callers never mean it. IRLet ids and match-pattern ids
/// stay IN the result because the call sites subtract or query specific ids
/// themselves. For real free/bound analysis use exprAttrs, which scopes all
/// binders via BinderShape.
let rec collectVarRefsIR (expr: IRExpr) : Set<IRId> =
    match expr with
    | IRVar (id, _) -> Set.singleton id
    | IRForRange (vid, lo, hi, body) ->
        Set.unionMany [collectVarRefsIR lo; collectVarRefsIR hi; Set.remove vid (collectVarRefsIR body)]
    | ExprShape (children, _) ->
        children |> List.map collectVarRefsIR |> Set.unionMany

// HM Type Substitution
//
// Substitutes IRTInfer occurrences with concrete types throughout types and
// expressions. Pure structural substitution -- no rewrites, no expansion.
// This is the substrate shared between HM monomorphization and (eventually,
// in the unified architecture) Poly's type-substitution step.

/// Substitute IRTInfer occurrences in a type, recursing into compound types.
/// Bindings is a map from type-var ID to concrete type. Type vars not in
/// the map are left as IRTInfer.
let rec substTypeInIRType (bindings: Map<int, IRType>) (ty: IRType) : IRType =
    match ty with
    | IRTInfer n when bindings.ContainsKey n -> bindings.[n]
    | IRTTuple ts -> IRTTuple (ts |> List.map (substTypeInIRType bindings))
    | IRTComputation t -> IRTComputation (substTypeInIRType bindings t)
    | IRTPoly (base', var) -> IRTPoly (substTypeInIRType bindings base', var)
    | IRTUnitAnnotated (inner, units) -> IRTUnitAnnotated (substTypeInIRType bindings inner, units)
    | IRTIdxTagged (inner, idxRef) -> IRTIdxTagged (substTypeInIRType bindings inner, idxRef)
    | IRTDist (order, elem, axes) -> IRTDist (order, substTypeInIRType bindings elem, axes)
    | IRTArrow (slots, result, identity) ->
        let substSlot = function
            | SIdx idx -> SIdx idx
            | SIdxVirt idx -> SIdxVirt idx   // IRIndexType has no IRType members; opaque
            | SVal ty -> SVal (substTypeInIRType bindings ty)
        IRTArrow (slots |> List.map substSlot, substTypeInIRType bindings result, identity)
    | _ -> ty

/// Substitute IRVar references throughout an expression tree: each
/// IRVar(id, _) with an entry in `mapping` is replaced by the mapped
/// expression. Used when importing a called function's probes into a
/// caller's analysis, resolving the probe's formal-parameter references to
/// the actual argument expressions at the call site. Walks bottom-up via
/// mapIRExpr; only IRVar nodes in `mapping` are affected.
let substituteIRVars (mapping: Map<IRId, IRExpr>) (expr: IRExpr) : IRExpr =
    mapIRExpr (fun e ->
        match e with
        | IRVar (id, _) when Map.containsKey id mapping -> Map.find id mapping
        | _ -> e) expr

/// Substitute types in an IRExpr at all type-bearing positions. Uses
/// mapIRExpr for structural traversal; the per-node callback only updates
/// fields that carry types directly (IRVar.ty, IRApp.retType, etc.).
let substTypeInIRExpr (bindings: Map<int, IRType>) (expr: IRExpr) : IRExpr =
    let st = substTypeInIRType bindings
    let substInNode e =
        match e with
        | IRVar (id, ty) -> IRVar (id, st ty)
        | IRParam (n, i, ty) -> IRParam (n, i, st ty)
        | IRApp (fn, args, retType) -> IRApp (fn, args, st retType)
        | IRArrayLit (elems, aty) ->
            IRArrayLit (elems, { aty with ElemType = st aty.ElemType })
        | IRMethodFor info ->
            IRMethodFor { info with
                            ArrayTypes = info.ArrayTypes
                                         |> List.map (fun aty -> { aty with ElemType = st aty.ElemType }) }
        | IRApplyCombinator info ->
            // ArrayTypes (ElemType may be a type variable referencing the
            // surrounding function's T) and OutputType (the deduced
            // result-array type, often sharing that T) must be substituted
            // in lockstep with the rest of the tree; skipping them leaves
            // stale IRTInfer in spec bodies, which the IR validator flags.
            IRApplyCombinator { info with
                                  ArrayTypes = info.ArrayTypes
                                               |> List.map (fun aty ->
                                                    { aty with ElemType = st aty.ElemType })
                                  OutputType = st info.OutputType }
        | IRComposeApply info ->
            // ComposeApplyInfo carries only OutputType as a type-bearing
            // field. The composition's leaves (IRObjectFor entries with
            // their own kernels) carry their own type metadata that the
            // generic walk reaches via the Composition / InputArrays
            // descent.
            IRComposeApply { info with OutputType = st info.OutputType }
        | _ -> e
    mapIRExpr substInNode expr

/// Types carried directly on a node -- no reconstruction, no environment.
/// The shared first tier of the canonical typing (audit section 2.2): the whole of
/// exprTypeIfKnown below, and the first arm of typeOf.
let (|CarriedType|_|) (expr: IRExpr) : IRType option =
    match expr with
    | IRVar (_, ty) -> Some ty
    | IRParam (_, _, ty) -> Some ty
    | IRApp (_, _, retType) -> Some retType
    | IRArrayLit (_, aty) -> Some (mkArrayLike aty)
    | IRStructLit (typeName, _) -> Some (IRTNamed typeName)
    | IRApplyCombinator info -> Some info.OutputType
    | IRComposeApply info -> Some info.OutputType
    | IRLit (IRLitInt _) -> Some (IRTScalar ETInt64)
    | IRLit (IRLitFloat _) -> Some (IRTScalar ETFloat64)
    | IRLit (IRLitBool _) -> Some (IRTScalar ETBool)
    | IRLit (IRLitString _) -> Some (IRTScalar ETString)
    | IRLit IRLitUnit -> Some IRTUnit
    | _ -> None

/// Get the type of an IRExpr where determinable from the node directly --
/// deliberately the CarriedType tier only, NOT the full typeOf
/// reconstruction. Used at HM call sites to extract arg types for
/// unification against param types: a reconstructed type could carry
/// pre-substitution type variables, so only node-carried types are safe.
/// Anything else returns None; the call site falls back to the function's
/// declared type-var positions.
let exprTypeIfKnown (expr: IRExpr) : IRType option =
    match expr with
    | CarriedType ty -> Some ty
    | _ -> None

/// Unify a parameter type against an argument type, accumulating
/// (typeVarId, concreteType) bindings. Walks pairs structurally:
/// ArrayElem pairs ElemType, IRTTuple pairs elementwise, FuncElem
/// pairs args and ret. An IRTInfer on the param side absorbs whatever's
/// on the arg side. This is one-sided (not full unification) because at
/// HM call sites the arg type is fully concrete.
let rec unifyParamWithArg (paramTy: IRType) (argTy: IRType) (acc: Map<int, IRType>) : Map<int, IRType> =
    match paramTy, argTy with
    | IRTInfer n, t when not (acc.ContainsKey n) -> Map.add n t acc
    | IRTInfer n, t when acc.[n] = t -> acc  // Consistent reuse -- fine
    | IRTInfer _, _ -> acc  // Inconsistent -- leave as-is; the IR validator will catch it
    | ArrayElem pa, ArrayElem aa ->
        unifyParamWithArg pa.ElemType aa.ElemType acc
    | IRTTuple pts, IRTTuple ats when pts.Length = ats.Length ->
        List.zip pts ats |> List.fold (fun m (p, a) -> unifyParamWithArg p a m) acc
    | FuncElem (pas, pr), FuncElem (aas, ar) when pas.Length = aas.Length ->
        // FuncElem matches IRTArrow with all-SVal slots (function form).
        let acc' = List.zip pas aas |> List.fold (fun m (p, a) -> unifyParamWithArg p a m) acc
        unifyParamWithArg pr ar acc'
    | IRTComputation pt, IRTComputation at -> unifyParamWithArg pt at acc
    | IRTPoly (pb, _), IRTPoly (ab, _) -> unifyParamWithArg pb ab acc
    | IRTUnitAnnotated (pi, _), IRTUnitAnnotated (ai, _) -> unifyParamWithArg pi ai acc
    | IRTIdxTagged (pi, _), IRTIdxTagged (ai, _) -> unifyParamWithArg pi ai acc
    | IRTDist (po, pe, _), IRTDist (ao, ae, _) when po = ao -> unifyParamWithArg pe ae acc
    | IRTArrow (pSlots, pRet, _), IRTArrow (aSlots, aRet, _) when pSlots.Length = aSlots.Length ->
        // Generic IRTArrow-vs-IRTArrow: handles arrows with SIdx and/or
        // SIdxVirt slots (FuncElem above only matched all-SVal arrows).
        // Identity is ignored for unification -- it's metadata, not type.
        let unifySlot acc' (p, a) =
            match p, a with
            | SVal pt, SVal at -> unifyParamWithArg pt at acc'
            | SIdx _, SIdx _ -> acc'
            | SIdxVirt _, SIdxVirt _ -> acc'
            | _ -> acc'
        let acc' = List.zip pSlots aSlots |> List.fold unifySlot acc
        unifyParamWithArg pRet aRet acc'
    | _ -> acc  // Concrete types or unhandled compound -- no bindings learned

/// Walk a type collecting all IRTInfer IDs found inside (recursively).
let rec collectInferIds (ty: IRType) : Set<int> =
    match ty with
    | IRTInfer n -> Set.singleton n
    | IRTTuple ts -> ts |> List.fold (fun s t -> Set.union s (collectInferIds t)) Set.empty
    | IRTComputation t -> collectInferIds t
    | IRTPoly (b, _) -> collectInferIds b
    | IRTUnitAnnotated (i, _) -> collectInferIds i
    | IRTIdxTagged (i, _) -> collectInferIds i
    | IRTDist (_, e, _) -> collectInferIds e
    | IRTArrow (slots, ret, _) ->
        let slotIds =
            slots |> List.fold (fun s slot ->
                match slot with
                | SVal ty -> Set.union s (collectInferIds ty)
                | SIdx _ | SIdxVirt _ -> s) Set.empty
        Set.union slotIds (collectInferIds ret)
    | _ -> Set.empty

/// Does this function carry any unresolved type variables in its declared
/// signature (params or return type)? Boundary criterion for HM
/// monomorphization -- only such functions need specialization.
let hasTypeVarsInSignature (func: IRFuncDef) : bool =
    let paramIds =
        func.Params |> List.fold (fun s p -> Set.union s (collectInferIds p.Type)) Set.empty
    let retIds = collectInferIds func.RetType
    not (Set.isEmpty paramIds && Set.isEmpty retIds)

/// Does this function have type vars in its PARAMETERS specifically? Only such
/// functions need per-call-site specialization. A function whose type vars sit
/// ONLY in the return type (params fully concrete, e.g. `lambda(x) ->
/// rowsum(x)` echoing an HM helper's abstract return) must be KEPT -- body
/// call-site-rewritten, return type substituted from global bindings -- not
/// dropped-and-specialized; dropping it while referenced as a first-class
/// kernel value leaves a dangling VarId.
let hasTypeVarsInParams (func: IRFuncDef) : bool =
    func.Params
    |> List.exists (fun p -> not (Set.isEmpty (collectInferIds p.Type)))

// HM Monomorphization
//
// Generates specialized copies of functions with free type variables in
// their signatures, one per unique call-site type pattern. Sibling pass to
// Arity (Poly) monomorphization; runs before Poly so type-substitution
// happens at the abstract signature level before Poly expands param packs.
// Architecture mirrors monomorphizeModule's 5-phase shape (identify, collect
// call sites with bindings, specialize, build rewrite map, rewrite all
// expressions); the key difference is SpecRequest carrying (typeVarId ->
// concreteType) bindings rather than a single arity int.

/// Collect call sites of HM-polymorphic functions.
/// Returns list of (funcId, sortedBindings) pairs. Bindings are sorted
/// by ID to give a canonical key for deduplication across call sites.
let collectHMCallSites (hmFuncMap: Map<IRId, IRFuncDef>) (expr: IRExpr) : (IRId * (int * IRType) list) list =
    let results = System.Collections.Generic.List<_>()
    let walk e =
        match e with
        | IRApp (IRVar (funcId, _), args, _) when hmFuncMap.ContainsKey funcId ->
            let func = hmFuncMap.[funcId]
            // Pair each param with its corresponding arg, unifying types
            let bindings =
                if args.Length <> func.Params.Length then Map.empty
                else
                    List.zip func.Params args
                    |> List.fold (fun acc (p, arg) ->
                        match exprTypeIfKnown arg with
                        | Some argTy -> unifyParamWithArg p.Type argTy acc
                        | None -> acc) Map.empty
            // Convert to sorted list for canonical comparison
            let sortedBindings =
                bindings |> Map.toList |> List.sortBy fst
            results.Add((funcId, sortedBindings))
        | _ -> ()
        e
    mapIRExpr walk expr |> ignore
    results |> Seq.toList

/// Occurrence-id-INDEPENDENT structural key for a type: same element, rank,
/// extent, and symmetry => same key, regardless of the per-occurrence index-type
/// `Id`s. Used BOTH to dedup HM specializations and to NAME them, so the two
/// stay consistent. Without this, one recursive poly kernel (`comoment_prod`)
/// specialized at the same arity from two call chains carried two different
/// index ids for the same `Array<double, Idx<3>>` -- distinct dedup keys but an
/// identical mangled name (a naive `mangleType` collapses every array to "T"),
/// which g++ rejects as a redefinition. Output is C++-identifier-safe.
let rec canonTypeKey (ty: IRType) : string =
    match ty with
    | IRTScalar ETFloat64 -> "double"
    | IRTScalar ETFloat32 -> "float"
    | IRTScalar ETInt64 -> "int64"
    | IRTScalar ETInt32 -> "int32"
    | IRTScalar ETBool -> "bool"
    | IRTScalar ETString -> "string"
    | IRTScalar ETComplex64 -> "c64"
    | IRTScalar ETComplex128 -> "c128"
    | IRTNamed n -> n
    | IRTUnit -> "unit"
    | IRTIdxTagged (inner, _) -> canonTypeKey inner
    | IRTUnitAnnotated (inner, _) -> canonTypeKey inner
    | IRTPoly (b, _) -> "poly_" + canonTypeKey b
    | IRTTuple ts -> "tup_" + (ts |> List.map canonTypeKey |> String.concat "_")
    | ArrayElem arr ->
        let symTag =
            function
            | SymNone -> "0" | SymSymmetric -> "s"
            | SymAntisymmetric -> "a" | SymHermitian -> "h"
            // The LEVEL LIST is part of a wreath class's identity, so this
            // monomorphization key has to carry it: [(2,+),(2,+)] and
            // [(2,-),(2,-)] are both Rank 4 and would otherwise share a
            // specialization. Rendered inline (w2p2p) rather than as a bare "w".
            | SymWreath -> "w"
        let idxKey (idx: IRIndexType) =
            let ext, levelTag =
                match idx.Extent with
                | IRLit (IRLitInt n) -> string n, ""
                | IROrbitClass (levels, n) ->
                    (match n with IRLit (IRLitInt v) -> string v | _ -> "d"),
                    (levels |> List.map (fun (r, p) -> sprintf "%d%s" r (if p then "p" else "m"))
                            |> String.concat "")
                | _ -> "d", ""
            sprintf "r%ds%s%se%s" idx.Rank (symTag idx.Symmetry) levelTag ext
        sprintf "arr_%s__%s" (canonTypeKey arr.ElemType)
            (arr.IndexTypes |> List.map idxKey |> String.concat "_")
    | IRTInfer id -> sprintf "v%d" id
    | _ -> "T"

/// Generate a specialized copy of a function for a given set of type-var
/// bindings. Substitutes types throughout params, return, and body, and
/// mangles the name to encode the binding pattern.
let specializeHMFunction (func: IRFuncDef) (bindings: Map<int, IRType>) (builder: IRBuilder) (callables: Map<IRId, IRCallable>) : IRFuncDef * IRCallable list =
    let newParams =
        func.Params |> List.map (fun p ->
            { p with Type = substTypeInIRType bindings p.Type
                     VarId = builder.FreshId() })
    let newRetType = substTypeInIRType bindings func.RetType
    // Rewrite body: substitute types AND remap old param VarIds to new ones
    let varIdRemap =
        List.zip func.Params newParams
        |> List.map (fun (oldP, newP) -> (oldP.VarId, newP.VarId))
        |> Map.ofList
    let bodyWithTypes = substTypeInIRExpr bindings func.Body

    // Lifted lambdas capturing HM-polymorphic params must be cloned-and-
    // specialized alongside their enclosing function: the lambda lives in
    // `module.Functions`, so the `mapIRExpr` walk over `bodyWithTypes` only
    // reaches an `IRVar(lambdaId, _)` reference -- its own body and
    // Captures.Type still hold the unsubstituted T, and Captures.Id still
    // points at the pre-spec function's param VarIds, failing validation
    // (dangling VarId, unresolved IRTInfer). Fix: for each lifted callable
    // the body references whose captures intersect this function's params,
    // clone it -- fresh ids for its own params, captures' Ids/types remapped
    // via `varIdRemap`/`bindings`, body's IRVar refs via the combined map.
    // The original lambda stays in module.Functions unchanged.
    let origParamIds = func.Params |> List.map (fun p -> p.VarId) |> Set.ofList
    let lambdaClones = System.Collections.Generic.Dictionary<IRId, IRCallable>()
    // Ids applied directly in the body (heads of IRApp). These go through the
    // module-level call-site rewrite + memoized spec path, so we must NOT
    // clone them into this parent -- doing so would bypass spec dedup and
    // leave any inner HM calls in the clone unrewritten.
    let appliedIds =
        let acc = System.Collections.Generic.HashSet<IRId>()
        mapIRExpr (fun e ->
            (match e with
             | IRApp (IRVar (id, _), _, _) -> acc.Add id |> ignore
             | _ -> ())
            e) bodyWithTypes |> ignore
        acc
    let needsClone (c: IRCallable) : bool =
        // (a) closures capturing one of this function's params, or (b)
        // HM-polymorphic callables referenced as first-class values (e.g. an
        // operator-section lambda passed as a `reduce` kernel): the
        // module-level pass drops every un-applied HM function, so without a
        // clone the spec body would reference a deleted id. Applied HM
        // callees are excluded -- they specialize via the normal spec path.
        (c.Captures |> List.exists (fun cap -> Set.contains cap.Id origParamIds))
        || (hasTypeVarsInSignature c && not (appliedIds.Contains c.Id))
    // Walk bodyWithTypes to identify referenced lambdas needing clones.
    let _ =
        mapIRExpr (fun e ->
            (match e with
             | IRVar (id, _) when callables.ContainsKey id && not (lambdaClones.ContainsKey id) ->
                 let lam = callables.[id]
                 if needsClone lam then
                     let cloneId = builder.FreshId()
                     let newCaps =
                         lam.Captures |> List.map (fun cap ->
                             let newId =
                                 match Map.tryFind cap.Id varIdRemap with
                                 | Some n -> n
                                 | None -> cap.Id
                             { cap with Id = newId; Type = substTypeInIRType bindings cap.Type })
                     // Clone lambda's own params with fresh VarIds (independent
                     // of the parent's param remap). The combined remap
                     // covers both parent's captures-as-our-captures and
                     // our local params.
                     let paramRemap =
                         lam.Params |> List.map (fun p -> (p.VarId, builder.FreshId())) |> Map.ofList
                     let newParams' =
                         lam.Params |> List.map (fun p ->
                             { p with VarId = paramRemap.[p.VarId]
                                      Type = substTypeInIRType bindings p.Type })
                     let combinedRemap =
                         varIdRemap
                         |> Map.fold (fun acc k v -> Map.add k v acc) paramRemap
                     let newBody =
                         lam.Body
                         |> substTypeInIRExpr bindings
                         |> mapIRExpr (fun e2 ->
                             match e2 with
                             | IRVar (id2, ty) when combinedRemap.ContainsKey id2 ->
                                 IRVar (combinedRemap.[id2], ty)
                             | _ -> e2)
                     let newRet = substTypeInIRType bindings lam.RetType
                     let clone =
                         { lam with
                             Id = cloneId
                             Name = sprintf "%s_HM_%d" lam.Name cloneId
                             Params = newParams'
                             Captures = newCaps
                             Body = newBody
                             RetType = newRet }
                     lambdaClones.[id] <- clone
             | _ -> ())
            e) bodyWithTypes

    let bodyRewritten =
        mapIRExpr (fun e ->
            match e with
            | IRVar (id, ty) when varIdRemap.ContainsKey id -> IRVar (varIdRemap.[id], ty)
            | IRVar (id, _) when lambdaClones.ContainsKey id ->
                let clone = lambdaClones.[id]
                let funcTy = mkFuncArrow (clone.Params |> List.map (fun p -> p.Type)) clone.RetType
                IRVar (clone.Id, funcTy)
            | _ -> e) bodyWithTypes
    // Name-mangle by binding signature, using the occurrence-id-independent
    // canonTypeKey so the emitted name matches the HM dedup key exactly (arrays
    // don't collapse to a colliding "T"; see canonTypeKey).
    let suffix =
        bindings
        |> Map.toList
        |> List.sortBy fst
        |> List.map (fun (id, ty) -> sprintf "_%d_%s" id (canonTypeKey ty))
        |> String.concat ""
    let spec =
        { func with
            Id = builder.FreshId()
            Name = sprintf "%s_HM%s" func.Name suffix
            Params = newParams
            RetType = newRetType
            Body = bodyRewritten }
    let clonesList = lambdaClones.Values |> List.ofSeq
    (spec, clonesList)

/// Driver: monomorphize all HM-polymorphic functions in a module.
///
/// An *iterative* fixpoint, not single-pass: specialization can expose new
/// concrete types (specializing `twiceId(x: T) -> T = id(id(x))` with
/// `T -> Int64` makes the inner `id(x)` call's arg concrete, licensing
/// `id`'s own specialization). A single pass would see `id(x)`'s arg as
/// still-abstract `IRTInfer 10001`. The loop runs until no new
/// (funcId, bindings) keys appear.
///
/// Also (a) substitutes call-site-learned bindings into a binding's
/// *declared type* (TypeCheck leaves it `IRTInfer N` when the call site's
/// return was polymorphic), and (b) substitutes types throughout each spec
/// body so it's free of `IRTInfer`.
let monomorphizeHMFunctions (modul: IRModule) (builder: IRBuilder) : IRModule =
    // 1. Identify functions with type vars in signature
    // Only functions with type vars in their PARAMETERS get dropped-and-
    // specialized per call site. Return-only-type-var functions (e.g. a former
    // kernel `lambda(x) -> hmHelper(x)` whose params are concrete but whose
    // return echoes the helper's abstract return) stay in newFunctions, where
    // their body is call-site-rewritten and their return type substituted.
    let hmFuncs = modul.Functions |> List.filter hasTypeVarsInParams
    if hmFuncs.IsEmpty then modul
    else
    let hmFuncMap = hmFuncs |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let hmFuncIdSet = hmFuncs |> List.map (fun f -> f.Id) |> Set.ofList

    // 2. Iterate to fixpoint: each round inspects the original module's
    //    expressions AND earlier-round spec bodies (an HM call's arg types
    //    may only become concrete after substitution). Keyed by (funcId,
    //    canonical (varId, type-key) list) -- occurrence-id-independent
    //    (canonTypeKey), so the same specialization from two differently-
    //    numbered call chains dedups to one spec.
    let mutable specMap : Map<IRId * (int * string) list, IRFuncDef> = Map.empty
    // Cloned lambdas accumulated across spec generation.
    // See specializeHMFunction's clone logic.
    let lambdaClones = System.Collections.Generic.List<IRCallable>()
    let mutable changed = true
    let mutable iterationGuard = 0
    let MAX_ITERATIONS = 16  // pathological safety net; real programs converge in 2-3
    while changed && iterationGuard < MAX_ITERATIONS do
        changed <- false
        iterationGuard <- iterationGuard + 1

        // Sources of call sites: original module + already-generated specs.
        // Spec bodies need scanning because an outer spec's body, after type
        // substitution, may contain HM calls with newly-concrete arg types.
        let sitesFromFuncs =
            modul.Functions |> List.collect (fun f -> collectHMCallSites hmFuncMap f.Body)
        let sitesFromBindings =
            modul.Bindings |> List.collect (fun b -> collectHMCallSites hmFuncMap b.Value)
        let sitesFromSpecs =
            specMap |> Map.toList
                    |> List.collect (fun (_, spec) -> collectHMCallSites hmFuncMap spec.Body)
        let uniqueSites =
            (sitesFromFuncs @ sitesFromBindings @ sitesFromSpecs) |> List.distinct

        for (funcId, sortedBindings) in uniqueSites do
            let key = (funcId, sortedBindings |> List.map (fun (id, ty) -> (id, canonTypeKey ty)))
            // Only generate specs whose bindings are entirely concrete. A
            // self-binding like (10001, IRTInfer 10002) means the call site
            // was inside a still-abstract context; the fixpoint revisits it
            // after the surrounding spec is generated and its types become
            // concrete.
            let allConcrete =
                sortedBindings |> List.forall (fun (_, v) ->
                    match v with IRTInfer _ -> false | _ -> true)
            // Also require the call site to have resolved every type var in
            // the callee's PARAMETERS -- an empty/partial binding means the
            // call sits inside a still-abstract enclosing function (e.g. a
            // pack element's not-yet-concrete type); the fixpoint revisits
            // once the enclosing function is specialized.
            let origFunc = hmFuncMap.[funcId]
            let paramVarIds =
                origFunc.Params
                |> List.fold (fun s p -> Set.union s (collectInferIds p.Type)) Set.empty
            let boundIds = sortedBindings |> List.map fst |> Set.ofList
            let paramVarsCovered =
                paramVarIds |> Set.forall (fun id -> Set.contains id boundIds)
            if allConcrete && paramVarsCovered && not (Map.containsKey key specMap) then
                let bindingMap = sortedBindings |> Map.ofList
                let availableCallables =
                    modul.Functions @ (lambdaClones |> List.ofSeq)
                    |> List.map (fun f -> (f.Id, f))
                    |> Map.ofList
                let (spec, clones) = specializeHMFunction origFunc bindingMap builder availableCallables
                specMap <- Map.add key spec specMap
                lambdaClones.AddRange(clones)
                changed <- true

    // 3. Build the call-site rewriter using the now-frozen specMap.
    //    Same logic as before, but operating against a complete spec map.
    let rewriteCallSite e =
        match e with
        | IRApp (IRVar (funcId, _), args, _) when hmFuncMap.ContainsKey funcId ->
            let func = hmFuncMap.[funcId]
            let bindings =
                if args.Length <> func.Params.Length then Map.empty
                else
                    List.zip func.Params args
                    |> List.fold (fun acc (p, arg) ->
                        match exprTypeIfKnown arg with
                        | Some argTy -> unifyParamWithArg p.Type argTy acc
                        | None -> acc) Map.empty
            let sortedBindings = bindings |> Map.toList |> List.sortBy fst
            let key = (funcId, sortedBindings |> List.map (fun (id, ty) -> (id, canonTypeKey ty)))
            match Map.tryFind key specMap with
            | Some spec ->
                IRApp (IRVar (spec.Id, mkFuncArrow (spec.Params |> List.map (fun p -> p.Type)) spec.RetType),
                       args, spec.RetType)
            | None -> e
        | _ -> e

    // 4. Build a *global*, conflict-free type-var binding map for the whole
    //    module: a downstream binding like `let r = result(0)` references
    //    the same IRTInfer N as the upstream `let result = arr_id(xs)`
    //    (TypeCheck propagates a polymorphic return through dependents
    //    without generalizing top-level functions), so per-binding-local
    //    substitution alone wouldn't fix r.Type. If the same ID binds to
    //    different concrete types at different call sites, drop it from the
    //    global map -- the per-call-site rewrite still produces the right
    //    specs, and the per-binding fallback covers r.Type.
    let collectAllBindingsFromExpr (expr: IRExpr) : (int * IRType) list =
        collectHMCallSites hmFuncMap expr
        |> List.collect (fun (_, sortedBindings) ->
            sortedBindings |> List.choose (fun (k, v) ->
                match v with
                | IRTInfer _ -> None  // self-binding; ignore
                | _ -> Some (k, v)))
    let allObservedBindings : (int * IRType) list =
        let fromFns =
            modul.Functions |> List.collect (fun f -> collectAllBindingsFromExpr f.Body)
        let fromBindings =
            modul.Bindings |> List.collect (fun b -> collectAllBindingsFromExpr b.Value)
        let fromSpecs =
            specMap |> Map.toList
                    |> List.collect (fun (_, s) -> collectAllBindingsFromExpr s.Body)
        fromFns @ fromBindings @ fromSpecs
    // Group by ID; keep only IDs whose observations all agree.
    let globalBindings : Map<int, IRType> =
        allObservedBindings
        |> List.groupBy fst
        |> List.choose (fun (id, pairs) ->
            let distinctTypes = pairs |> List.map snd |> List.distinct
            match distinctTypes with
            | [singleTy] -> Some (id, singleTy)
            | _ -> None)  // conflict -- leave alone, per-call-site rewrite handles each
        |> Map.ofList

    // Per-binding-local fallback: for a call sitting directly in a binding's
    // value, when global bindings don't already cover the IDs in its
    // declared type.
    let unionBindingsFromExpr (expr: IRExpr) : Map<int, IRType> =
        collectAllBindingsFromExpr expr
        |> List.fold (fun acc (k, v) ->
            match Map.tryFind k acc with
            | Some _ -> acc
            | None -> Map.add k v acc) Map.empty

    // 5. Rewrite all expressions; substitute binding declared types using
    //    the union of (global, per-binding-local) bindings, and rewrite call
    //    sites inside spec bodies. Local bindings win over global on
    //    conflict (each call site is locally consistent).
    let mergeBindings (local: Map<int, IRType>) : Map<int, IRType> =
        local |> Map.fold (fun acc k v -> Map.add k v acc) globalBindings
    let newFunctions =
        modul.Functions
        |> List.filter (fun f -> not (Set.contains f.Id hmFuncIdSet))
        |> List.map (fun f ->
            let bindings = mergeBindings (unionBindingsFromExpr f.Body)
            let bodyWithRewrittenCalls = mapIRExpr rewriteCallSite f.Body
            let bodyWithSubstitutedTypes = substTypeInIRExpr bindings bodyWithRewrittenCalls
            { f with Body = bodyWithSubstitutedTypes
                     RetType = substTypeInIRType bindings f.RetType
                     Params = f.Params |> List.map (fun p ->
                                { p with Type = substTypeInIRType bindings p.Type })
                     // Captures carry types too (a former kernel captures the HM
                     // helper it calls); leaving them abstract emits an
                     // unresolved-type sentinel in the lifted lambda's signature.
                     Captures = f.Captures |> List.map (fun c ->
                                { c with Type = substTypeInIRType bindings c.Type }) })
    let newBindings =
        modul.Bindings
        |> List.map (fun b ->
            let bindings = mergeBindings (unionBindingsFromExpr b.Value)
            let newType = substTypeInIRType bindings b.Type
            let valueWithRewrittenCalls = mapIRExpr rewriteCallSite b.Value
            let valueWithSubstitutedTypes = substTypeInIRExpr bindings valueWithRewrittenCalls
            { b with Type = newType; Value = valueWithSubstitutedTypes })
    // Spec function bodies need the same treatment as ordinary function
    // bodies: their inner HM calls (e.g. `id(id(x))` inside `twiceId`'s spec)
    // must be rewritten to point at the inner specs, and any residual
    // IRTInfer in their expression-tree types substituted out.
    let specFuncs =
        specMap
        |> Map.toList
        |> List.map (fun (_, spec) ->
            let bindings = mergeBindings (unionBindingsFromExpr spec.Body)
            let bodyWithRewrittenCalls = mapIRExpr rewriteCallSite spec.Body
            let bodyWithSubstitutedTypes = substTypeInIRExpr bindings bodyWithRewrittenCalls
            { spec with Body = bodyWithSubstitutedTypes
                        RetType = substTypeInIRType bindings spec.RetType
                        Params = spec.Params |> List.map (fun p ->
                                   { p with Type = substTypeInIRType bindings p.Type })
                        Captures = spec.Captures |> List.map (fun c ->
                                   { c with Type = substTypeInIRType bindings c.Type }) })

    { modul with
        Functions = newFunctions @ specFuncs @ (lambdaClones |> List.ofSeq)
        Bindings = newBindings }

/// Post-monomorphization rewrite: a raw *elementwise* `IRBinOp` whose
/// operands are BOTH arrays becomes the `method_for(zip ..) <@> kernel |>
/// compute` co-iteration combinator -- the same shape TypeCheck.inferBinOp
/// synthesizes for a top-level `x + y`.
///
/// Pack-element operands can't be recognized at lowering/type-check time:
/// in `firstsum(A: Poly<Float64^1>) = A[0] + A[1]` the element type is
/// unresolved until Poly + HM specialization substitutes the concrete
/// `Array<..>` in. Running here (after BOTH monomorphizers) the operand
/// types are concrete. Without this the binop stays a raw `Array op Array`
/// with no C++ operator overload, and the interpreter rejects it (BL8010).
///
/// The synthesized `lambda(a, b) -> a op b` kernel closes over nothing.
/// Only Elementwise mode is rewritten (pack-element `A[i] + A[j]`); outer
/// products and scalar/broadcast binops are left untouched.
let lowerArrayBinOpsModule (modul: IRModule) (builder: IRBuilder) : IRModule =
    let newLambdas = System.Collections.Generic.List<IRCallable>()
    let isCmpOrLogical op =
        match op with
        | IREq | IRNeq | IRLt | IRLe | IRGt | IRGe | IRAnd | IROr -> true
        | _ -> false
    // Distinct identity per distinct operand var so codegen's symmetry
    // deduction treats `A_0 + A_1` as two different arrays (and `A_0 + A_0` as
    // the same array -- correct commutative collapse).
    let identityOf e =
        match e with
        | IRVar (id, _) -> AIDVariable (sprintf "__coi%d" id)
        | _ -> AIDVariable "__coi"
    // Operand type, seeing through a `|> compute` wrapper so a *nested* array
    // binop -- whose inner rewrite already produced `IRCompute(IRApplyCombinator)`
    // (an array-typed, but not CarriedType-tagged, node) -- is still recognized
    // as an array operand by the enclosing binop (`A[0] + A[1] + A[2]`).
    let rec operandType e =
        match e with
        | IRCompute inner -> operandType inner
        // See through a let (e.g. the scalar-materialization wrapper a nested
        // array-scalar broadcast produces) to the value it ultimately yields.
        | IRLet (_, _, body) -> operandType body
        | CarriedType ty -> Some ty
        | _ -> None
    // Broadcast a scalar against an array (`arr op scalar` / `scalar op
    // arr`): value-space twin of TypeCheck.inferBinOp's array-scalar path,
    // for pack elements whose array type only resolves post-monomorphization
    // (e.g. `head - mean(head)` in a Poly<T^1> kernel). The scalar is
    // materialized into a captured local; a single-array method_for maps it.
    let broadcastScalar op (arr: IRExpr) (arrTy: IRType) (la: IRArrayType)
                        (scalarE: IRExpr) (sElem: ElemType) (scalarOnLeft: bool) : IRExpr =
        let arrElem = match la.ElemType with PrimElem et -> et | _ -> ETFloat64
        let kernelRet = if isCmpOrLogical op then IRTScalar ETBool else IRTScalar arrElem
        let sId = builder.FreshId()
        let sTy = IRTScalar sElem
        let xId = builder.FreshId()
        let xVar = IRVar (xId, IRTScalar arrElem)
        let sVar = IRVar (sId, sTy)
        // Kernel `lambda(__bx) -> __bx op s` (or `s op __bx`); `s` is captured.
        let kbody =
            if scalarOnLeft then IRBinOp (IRElementwise, op, sVar, xVar)
            else IRBinOp (IRElementwise, op, xVar, sVar)
        let parms : IRParam list =
            [ { Name = "__bx"; Type = IRTScalar arrElem; Index = 0; VarId = xId } ]
        let cap : CaptureInfo = { Id = sId; Name = sprintf "__v%d" sId; Type = sTy; IsMutable = false }
        let lam = mkLambdaCallable builder parms kbody kernelRet [cap] false [] [] false false 256 false
        newLambdas.Add lam
        let kernelFuncType = IRTArrow ([SVal (IRTScalar arrElem)], kernelRet, None)
        let ident = identityOf arr
        let sdims = la.IndexTypes.Length
        let outputType =
            match arrTy with
            | IRTArrow (slots, _, id2) -> IRTArrow (slots, kernelRet, id2)
            | _ -> arrTy
        let mfInfo : MethodForInfo =
            { Arrays = [arr]; Identities = [ident]; ArrayTypes = [la]
              SDimsPerArray = [sdims]; TotalSDims = sdims; SharedIndexTypes = [] }
        let applyInfo : ApplyInfo =
            { Loop = IRMethodFor mfInfo
              Kernel = IRVar (lam.Id, kernelFuncType)
              Arrays = [arr]; Identities = [ident]; ArrayTypes = [la]
              SharedIndexTypes = []; SymcomStates = [SCNeither]; TriangularLevels = [false]
              SDimsPerArray = [sdims]; KernelInputRanks = [0]; KernelOutputRank = 0
              KernelTDims = []; SpeedupFactor = 1L; ReynoldsSpeedup = 1L; HasReynolds = false
              OutputType = outputType; IsCoIteration = false }
        // Materialize the scalar once, outside the loop, as the captured local.
        IRLet (sId, scalarE, IRCompute (IRApplyCombinator applyInfo))
    let rewrite (e: IRExpr) : IRExpr =
        match e with
        | IRBinOp (IRElementwise, op, l, r) ->
            match operandType l, operandType r with
            | Some ((ArrayElem la) as lt), Some ((ArrayElem ra) as rt) ->
                let elemTypeL = match la.ElemType with PrimElem et -> et | _ -> ETFloat64
                let elemTypeR = match ra.ElemType with PrimElem et -> et | _ -> ETFloat64
                let kernelRet =
                    if isCmpOrLogical op then IRTScalar ETBool else IRTScalar elemTypeL
                // Co-iteration kernel: lambda(__zl, __zr) -> __zl op __zr.
                let aId = builder.FreshId()
                let bId = builder.FreshId()
                let kbody =
                    IRBinOp (IRElementwise, op,
                             IRVar (aId, IRTScalar elemTypeL),
                             IRVar (bId, IRTScalar elemTypeR))
                let parms : IRParam list =
                    [ { Name = "__zl"; Type = IRTScalar elemTypeL; Index = 0; VarId = aId }
                      { Name = "__zr"; Type = IRTScalar elemTypeR; Index = 1; VarId = bId } ]
                let lam =
                    mkLambdaCallable builder parms kbody kernelRet [] false [] [] false false 256 false
                newLambdas.Add lam
                let kernelFuncType =
                    IRTArrow ([SVal (IRTScalar elemTypeL); SVal (IRTScalar elemTypeR)], kernelRet, None)
                // Materialize non-variable operands into let-bindings so the loop
                // reads NAMED arrays. A function-call operand (`head + packsum1(tail)`
                // in a recursive pack kernel) must be evaluated once and bound --
                // codegen's loop reads `arr[i]` by the operand's name, and an
                // unnamed call expression there is an undeclared identifier.
                let mutable prelude : (IRId * IRExpr) list = []
                let materialize (operand: IRExpr) (ty: IRType) : IRExpr =
                    match operand with
                    | IRVar _ -> operand
                    | _ ->
                        let id = builder.FreshId()
                        prelude <- prelude @ [(id, operand)]
                        IRVar (id, ty)
                let lArr = materialize l lt
                let rArr = materialize r rt
                let identities = [identityOf lArr; identityOf rArr]
                let arrayTypes = [la; ra]
                // Shared co-iteration record: the left array's index axes (both
                // operands share the same iteration space -- the elementwise
                // conformance the type-checker guaranteed).
                let sharedIdx = la.IndexTypes
                let sdimsPerArray = [la.IndexTypes.Length; ra.IndexTypes.Length]
                let totalSDims = List.sum sdimsPerArray
                let mfInfo : MethodForInfo =
                    { Arrays = [lArr; rArr]
                      Identities = identities
                      ArrayTypes = arrayTypes
                      SDimsPerArray = sdimsPerArray
                      TotalSDims = totalSDims
                      SharedIndexTypes = sharedIdx }
                // Output array type: left operand's index axes, element type from
                // the kernel (Bool for comparison/logical, else arithmetic elem).
                let outputType =
                    match lt with
                    | IRTArrow (slots, _, ident) -> IRTArrow (slots, kernelRet, ident)
                    | _ -> lt
                let applyInfo : ApplyInfo =
                    { Loop = IRMethodFor mfInfo
                      Kernel = IRVar (lam.Id, kernelFuncType)
                      Arrays = [lArr; rArr]
                      Identities = identities
                      ArrayTypes = arrayTypes
                      SharedIndexTypes = sharedIdx
                      SymcomStates = [SCNeither; SCNeither]
                      TriangularLevels = [false; false]
                      SDimsPerArray = sdimsPerArray
                      KernelInputRanks = [0; 0]
                      KernelOutputRank = 0
                      KernelTDims = []
                      SpeedupFactor = 1L
                      ReynoldsSpeedup = 1L
                      HasReynolds = false
                      OutputType = outputType
                      IsCoIteration = true }
                let combined = IRCompute (IRApplyCombinator applyInfo)
                // Wrap in the hoisted operand bindings (outermost = first operand).
                List.foldBack (fun (id, v) acc -> IRLet (id, v, acc)) prelude combined
            | Some ((ArrayElem la) as lt), Some (IRTScalar sElem) ->
                broadcastScalar op l lt la r sElem false
            | Some (IRTScalar sElem), Some ((ArrayElem ra) as rt) ->
                broadcastScalar op r rt ra l sElem true
            | _ -> e
        | _ -> e
    let rewriteExpr expr = mapIRExpr rewrite expr
    let newFunctions =
        modul.Functions |> List.map (fun f -> { f with Body = rewriteExpr f.Body })
    let newBindings =
        modul.Bindings |> List.map (fun b -> { b with Value = rewriteExpr b.Value })
    { modul with
        Functions = newFunctions @ (newLambdas |> List.ofSeq)
        Bindings = newBindings }

// Arity Monomorphization

/// Locate every Poly param's index in a function, in declaration order.
/// Each Poly pack is independent -- different slots may have different
/// concrete arities at any given call site. Returns [] for non-Poly
/// functions.
let findPolyParamIndices (func: IRFuncDef) : int list =
    func.Params
    |> List.mapi (fun i p ->
        match p.Type with
        | IRTPoly _ -> Some i
        | _ -> None)
    |> List.choose id

/// Compute the concrete pack arity for a call to an arity-polymorphic
/// function, returning one arity per Poly slot (in declaration order). Each
/// pack stands independently -- `pairSum((1.0, 2.0), (3.0, 4.0, 5.0))` is a
/// valid call with arities [2; 3]. Three call shapes are recognized:
/// (a) Variadic -- single Poly param, no free params: every positional arg
///     is a pack element, returns [args.Length].
/// (b) Single-Poly tuple-as-pack -- single Poly param plus free params: the
///     pack is a tuple at the Poly slot, returns [tuple.Length].
/// (c) Multi-Poly tuple-as-pack -- every Poly slot gets its own tuple,
///     returns [size_slot_0; size_slot_1; ...].
/// Returns None for unsupported shapes (mismatched arg count, non-tuple at
/// a required Poly slot).
let computePolyArity (func: IRFuncDef) (args: IRExpr list) : int list option =
    let polyIndices = findPolyParamIndices func
    // A call passing a symbolic pack tail (`f(tail)` inside an un-specialized
    // recursive body, `tail = IRPolyTail(A, k)`) is NOT a concrete call site:
    // its true arity is only known once the ENCLOSING function is
    // specialized, at which point specializeFunction spreads the tail into
    // `f(A_k, .., A_{n-1})`, the real call site the worklist then picks up.
    // Treating the symbolic call as concrete (arity = arg-count = 1) mints a
    // bogus specialization -- for a `| 2` base it cascades into an invalid
    // arity-0 spec. Reject it here.
    if args |> List.exists (function IRPolyTail _ -> true | _ -> false) then None
    else
    match polyIndices with
    | [] -> None
    | [pidx] when func.Params.Length = 1 ->
        // Variadic -- args are the pack elements directly
        Some [args.Length]
    | _ ->
        // Tuple-as-pack at every Poly slot. args.Length must equal the
        // formal arity (no variadic spreading when free params or multiple
        // packs are involved).
        if args.Length <> func.Params.Length then None
        else
            let perSlot =
                polyIndices |> List.map (fun pidx ->
                    match List.item pidx args with
                    | IRTuple elems -> Some elems.Length
                    | _ -> None)
            if perSlot |> List.forall Option.isSome then
                Some (perSlot |> List.map Option.get)
            else None

/// Rewrite a call's arg list to match the specialized function's expanded
/// param list. Variadic single-Poly leaves args flat; tuple-as-pack (single
/// or multi) expands each tuple at its Poly slot. Each pack expands
/// independently -- different slots can yield different numbers of elements.
let flattenAtPolyPosition (func: IRFuncDef) (args: IRExpr list) : IRExpr list =
    let polyIndices = findPolyParamIndices func
    match polyIndices with
    | [] -> args
    | [_] when func.Params.Length = 1 -> args  // variadic -- already flat
    | _ ->
        if args.Length <> func.Params.Length then args
        else
            let polySet = Set.ofList polyIndices
            args |> List.mapi (fun i a ->
                if Set.contains i polySet then
                    match a with
                    | IRTuple elems -> elems
                    | _ -> [a]  // shouldn't happen post-computePolyArity
                else
                    [a])
            |> List.concat

/// Collect all call sites to arity-polymorphic functions.
/// Returns list of (funcId, arities) pairs where arities is the per-slot
/// arity list. Unsupported call shapes are silently skipped -- they surface
/// as type errors downstream rather than producing wrong-arity specs.
let collectPolyCallSites (polyFuncMap: Map<IRId, IRFuncDef>) (expr: IRExpr) : (IRId * int list) list =
    let results = System.Collections.Generic.List<_>()
    let walk e =
        match e with
        | IRApp (IRVar (funcId, _), args, _) when Map.containsKey funcId polyFuncMap ->
            let func = polyFuncMap.[funcId]
            match computePolyArity func args with
            | Some arities -> results.Add((funcId, arities))
            | None -> ()
        | _ -> ()
        e
    mapIRExpr walk expr |> ignore
    results |> Seq.toList

/// Create a monomorphized copy of a poly function for a list of slot arities.
/// `arities` carries one arity per Poly slot, in declaration order -- packs
/// are independent, so different slots may have different sizes.
let specializeFunction (func: IRFuncDef) (arities: int list) (funcMap: Map<IRId, IRFuncDef>) (builder: IRBuilder) : IRFuncDef =
    let polyIndices = findPolyParamIndices func
    if List.isEmpty polyIndices then func  // Not actually poly -- shouldn't happen
    elif List.length arities <> List.length polyIndices then func  // arity-count mismatch
    else
        // Per-slot info: original param, base type, the N_slot new params
        // (where N_slot is that slot's arity).
        let slotInfo =
            List.zip polyIndices arities
            |> List.map (fun (pidx, slotArity) ->
                let polyParam = func.Params.[pidx]
                let baseType =
                    match polyParam.Type with
                    | IRTPoly (bt, _) -> bt
                    | _ -> IRTScalar ETFloat64
                let newParams =
                    List.init slotArity (fun i ->
                        { Name = sprintf "%s_%d" polyParam.Name i
                          Type = baseType
                          Index = 0  // recomputed below
                          VarId = builder.FreshId() } : IRParam)
                (pidx, polyParam, baseType, newParams))

        // Expand param list: walk original, replace each Poly slot with its
        // (slot-specific number of) new params. Reindex flat.
        let polySet = polyIndices |> Set.ofList
        let slotByIdx = slotInfo |> List.map (fun (i, _, _, np) -> (i, np)) |> Map.ofList
        let expandedParams =
            func.Params
            |> List.mapi (fun i p ->
                if Set.contains i polySet then slotByIdx.[i]
                else [p])
            |> List.concat
            |> List.mapi (fun newIdx p -> { p with Index = newIdx })

        // Per-slot data indexed by slot, used during body rewrite.
        let newParamsBySlot =
            slotInfo |> List.map (fun (_, _, _, np) -> np) |> List.toArray
        let baseTypeBySlot =
            slotInfo |> List.map (fun (_, _, bt, _) -> bt) |> List.toArray
        let aritiesArr = arities |> List.toArray

        // Alias map: VarId -> (slotIdx, offset), for each pack-slot param plus
        // every let-alias and cons-destructuring tail view built from it. The
        // offset is leading elements dropped: 0 for the pack itself; `off +
        // drop` for `let t = tail-of(view)` (from `let head :: tail = view`).
        // A read `view[k]` resolves to expanded param `off + k`; a call
        // passing `view` spreads params `off ..`. Top-down walk so a later
        // alias sees an earlier one (lets are ordered).
        let aliasInfo : Map<IRId, int * int> =
            let mutable info =
                slotInfo
                |> List.mapi (fun slotIdx (_, polyParam, _, _) -> (polyParam.VarId, (slotIdx, 0)))
                |> Map.ofList
            let rec walk expr =
                match expr with
                | IRLet (id, value, body) ->
                    (match value with
                     | IRVar (srcId, _) when Map.containsKey srcId info ->
                         info <- Map.add id info.[srcId] info
                     | IRPolyTail (IRVar (srcId, _), drop) when Map.containsKey srcId info ->
                         let (slot, off) = info.[srcId]
                         info <- Map.add id (slot, off + drop) info
                     | _ -> ())
                    walk value; walk body
                | ExprShape (children, _) -> children |> List.iter walk
            walk func.Body
            info
        // Slot-only view, for the pack-former unroller's membership test.
        let aliasToSlot = aliasInfo |> Map.map (fun _ (slot, _) -> slot)

        // Map param name -> slot index, for the IRArity intrinsic. `arity(xs)`
        // resolves to slot 0's arity; `arity(ys)` to slot 1's, etc.
        let paramNameToSlot =
            slotInfo
            |> List.mapi (fun slotIdx (_, p, _, _) -> (p.Name, slotIdx))
            |> Map.ofList

        // Draw expanded param `idx` of `slot` as an IRVar (used for reads and for
        // spreading a pack argument at a call site).
        let slotParamVar slot idx =
            IRVar (newParamsBySlot.[slot].[idx].VarId, baseTypeBySlot.[slot])

        // Rewrite body: resolve IRPolyIndex reads to the expanded param,
        // IRArity to a literal, and spread any pack-view argument at a call
        // site into its trailing params (`f(tail)` becomes
        // `f(A_off, .., A_{n-1})`, a normal call the driver re-collects --
        // how recursion over a shrinking pack terminates at the base case).
        let rewrite e =
            match e with
            | IRPolyIndex (IRVar (id, _), IRLit (IRLitInt k)) when Map.containsKey id aliasInfo ->
                let (slotIdx, off) = aliasInfo.[id]
                let idx = off + int k
                let slotArity = aritiesArr.[slotIdx]
                if idx >= 0 && idx < slotArity then slotParamVar slotIdx idx
                else e
            | IRPolyIndex (IRVar (id, _), _) when Map.containsKey id aliasInfo ->
                e  // Dynamic index -- can't monomorphize, leave as-is
            | IRArity (_, name) when Map.containsKey name paramNameToSlot ->
                let slotIdx = paramNameToSlot.[name]
                IRLit (IRLitInt (int64 aritiesArr.[slotIdx]))
            | IRApp (callee, args, rty)
                when args |> List.exists (fun a ->
                        match a with IRVar (id, _) -> Map.containsKey id aliasInfo | _ -> false) ->
                let expandedArgs =
                    args |> List.collect (fun a ->
                        match a with
                        | IRVar (id, _) when Map.containsKey id aliasInfo ->
                            let (slot, off) = aliasInfo.[id]
                            [ for j in off .. aritiesArr.[slot] - 1 -> slotParamVar slot j ]
                        | _ -> [a])
                IRApp (callee, expandedArgs, rty)
            | _ -> e
        let newBody = mapIRExpr rewrite func.Body

        // Static match reduction: after `rewrite` turns `arity(A)` into a
        // literal, `match arity(A) with | k -> .. | _ -> ..` picks its one
        // live arm at compile time. Essential for recursion termination: the
        // base arm must be selected (and the recursive arm, which
        // destructures the pack and calls f(tail), discarded) at the base
        // arity, or specialization would shrink past 0 and destructure an
        // empty pack. Only guard-free arms are reduced.
        let rec reduceArityMatch expr =
            match expr with
            | IRMatch (IRLit (IRLitInt n), cases) ->
                let chosen =
                    cases |> List.tryFind (fun c ->
                        c.Guard.IsNone &&
                        (match c.Pattern with
                         | IRPatLit (IRLitInt m) -> m = n
                         | IRPatWild | IRPatVar _ -> true
                         | _ -> false))
                match chosen with
                | Some c ->
                    match c.Pattern with
                    | IRPatVar vid -> reduceArityMatch (IRLet (vid, IRLit (IRLitInt n), c.Body))
                    | _ -> reduceArityMatch c.Body
                | None -> expr  // no guard-free arm matches; leave for runtime
            | ExprShape (children, rebuild) -> rebuild (children |> List.map reduceArityMatch)
        let newBody = reduceArityMatch newBody

        // Drop the now-dead pack-alias let bindings (`let _ = A`, `let tail = A[1..]`).
        // Every use of them was rewritten to expanded params above; the bindings
        // themselves reference the pre-expansion pack (a dangling VarId) or an
        // IRPolyTail marker (no codegen), so they must not survive.
        let rec dropAliasLets expr =
            match expr with
            | IRLet (id, _, rest) when Map.containsKey id aliasInfo -> dropAliasLets rest
            | ExprShape (children, rebuild) -> rebuild (children |> List.map dropAliasLets)
        let newBody = dropAliasLets newBody

        // Second pass: unroll IRForRange with literal bounds. This handles
        // `for k in 0..arity(args)` after arity is resolved.
        let rec unrollForRanges expr =
            match expr with
            | IRLet (id, IRForRange (vid, IRLit (IRLitInt lo), IRLit (IRLitInt hi), body), rest) ->
                let restUnrolled = unrollForRanges rest
                let indices = [ int lo .. int hi - 1 ] |> List.rev
                indices |> List.fold (fun acc k ->
                    let substBody =
                        mapIRExpr (fun e ->
                            match e with
                            | IRVar (varId, _) when varId = vid -> IRLit (IRLitInt (int64 k))
                            | _ -> e) body
                    let substBody2 = mapIRExpr rewrite substBody
                    let dummyId = builder.FreshId()
                    IRLet (dummyId, unrollForRanges substBody2, acc)
                ) restUnrolled
            | IRLet (id, v, b) -> IRLet (id, unrollForRanges v, unrollForRanges b)
            | _ -> expr
        let newBody = unrollForRanges newBody

        // Pack-former unrolling pass (poly-specialization only).
        //
        // Recognizes a let-bound former whose iteration source is a single
        // virtual range and whose kernel (a lifted lambda) reads THIS
        // function's pack via a dynamic `IRPolyIndex` (`args[k]`). Two
        // things block ordinary codegen once arity is known: (1) the kernel
        // keeps the dynamic subscript `args[k]`, invalid once the pack is
        // split into scalar params; (2) the range extent is an opaque
        // `IRParam` placeholder (not `IRArity`), so codegen emits an
        // undeclared `__range0.extents[0]`. Since the pack size is exactly
        // the slot arity, unroll the former into an n-element ARRAY LITERAL:
        // element k = the kernel body with its ordinal param substituted to
        // `Lit k`, re-run through `rewrite` so `IRPolyIndex(pack, Lit k)`
        // becomes the k-th monomorphized param.
        //
        // Tightly scoped: fires ONLY when the kernel lambda body references a
        // pack slot of this function (`aliasToSlot` membership test); every
        // other former is left byte-for-byte untouched.
        let unrollPackFormers expr =
            let tryUnroll (info: ApplyInfo) : IRExpr option =
                // The virtual source must be a single range with no real data
                // arrays threaded in (a pure ordinal iteration).
                let pureRange =
                    match info.Arrays with
                    | [IRRange _] -> true
                    | _ -> false
                match info.Kernel with
                | IRVar (lamId, _) when pureRange && Map.containsKey lamId funcMap ->
                    let lam = funcMap.[lamId]
                    match lam.Params with
                    | [ordinalParam] ->
                        // Which pack slot (if any) does the lambda body read?
                        let mutable packSlot = None
                        mapIRExpr (fun e ->
                            match e with
                            | IRPolyIndex (IRVar (pid, _), _) when Map.containsKey pid aliasToSlot ->
                                packSlot <- Some aliasToSlot.[pid]
                                e
                            | _ -> e) lam.Body |> ignore
                        match packSlot with
                        | Some slotIdx ->
                            let n = aritiesArr.[slotIdx]
                            // Element type + index-type shape from the former's
                            // deduced OutputType arrow; the extent is replaced
                            // with the (now literal) pack size.
                            let (idxRec, elemTy) =
                                match info.OutputType with
                                | IRTArrow ([SIdx ix], et, _) -> (ix, et)
                                | IRTArrow ([SIdxVirt ix], et, _) -> (ix, et)
                                | _ ->
                                    ({ Id = builder.FreshId(); Rank = 1
                                       Extent = IRLit (IRLitInt (int64 n))
                                       Symmetry = SymNone; Tag = None
                                       Kind = SDimension; IxKind = IxKPlain
                                       Dependencies = [] }, baseTypeBySlot.[slotIdx])
                            let arrTy : IRArrayTypeG<IRExpr> =
                                { ElemType = elemTy
                                  IndexTypes = [ { idxRec with Extent = IRLit (IRLitInt (int64 n)) } ]
                                  IsVirtual = false
                                  Identity = None }
                            let elems =
                                [ for k in 0 .. n - 1 ->
                                    let substituted =
                                        mapIRExpr (fun e ->
                                            match e with
                                            | IRVar (vid, _) when vid = ordinalParam.VarId ->
                                                IRLit (IRLitInt (int64 k))
                                            | _ -> e) lam.Body
                                    mapIRExpr rewrite substituted ]
                            Some (IRArrayLit (elems, arrTy))
                        | None -> None
                    | _ -> None
                | _ -> None
            mapIRExpr (fun e ->
                match e with
                | IRLet (id, IRCompute (IRApplyCombinator info), rest) ->
                    (match tryUnroll info with
                     | Some arrLit -> IRLet (id, arrLit, rest)
                     | None -> e)
                | IRLet (id, IRApplyCombinator info, rest) ->
                    (match tryUnroll info with
                     | Some arrLit -> IRLet (id, arrLit, rest)
                     | None -> e)
                | _ -> e) expr
        let newBody = unrollPackFormers newBody

        let declaredRetType =
            match func.RetType with
            | IRTPoly (bt, _) -> bt
            | other -> other
        // Arity-dependent return rank: when arity reduction collapses this
        // specialization's body to a SCALAR (an empty-pack base arm, e.g.
        // `| 0 -> zero`), the specialization genuinely returns T^0, not the
        // declared array shape, and the enclosing op broadcasts it. We only
        // ever narrow a declared array to the body's scalar, never widen.
        let rec finalScalarType e =
            match e with
            | IRLet (_, _, body) -> finalScalarType body
            | CarriedType (IRTScalar _ as ty) -> Some ty
            | _ -> None
        let newRetType =
            match finalScalarType newBody with
            | Some sTy -> sTy               // arity-collapsed scalar base (see above)
            | None -> declaredRetType

        // Commutativity group expansion: each original index maps to a
        // (newStart, span) in the expanded list. For a Poly slot, span =
        // that slot's arity; for a free param, span = 1.
        let origToNew =
            let mutable acc = []
            let mutable cur = 0
            for i in 0 .. func.Params.Length - 1 do
                let span =
                    if Set.contains i polySet then
                        // Look up this slot's arity. polyIndices is in order,
                        // so its position in the list is the slot index.
                        let slotIdx = polyIndices |> List.findIndex (fun x -> x = i)
                        aritiesArr.[slotIdx]
                    else 1
                acc <- (i, (cur, span)) :: acc
                cur <- cur + span
            acc |> List.rev |> Map.ofList

        // Specializing arity groups means rewriting group indices to
        // account for expanded parameters: a pack slot of span k becomes the
        // k consecutive expanded positions, everything else maps 1:1.
        let expandGroups (groups: int list list) =
            groups |> List.map (fun group ->
                group |> List.collect (fun idx ->
                    match Map.tryFind idx origToNew with
                    | Some (start, span) ->
                        if span = 1 then [start]
                        else List.init span (fun i -> start + i)
                    | None -> [idx]))
        // Expand whatever groups the source carried, independent of
        // IsCommutative: the declared-anticomm spelling means the flag and a
        // non-empty group list are not always set together -- an antisym
        // kernel has groups but is NOT commutative -- so the expansion keys
        // off the lists themselves and the flag is carried through untouched.
        let newIsComm = func.IsCommutative
        let newCommGroups = expandGroups func.CommGroups
        let newAntisymGroups = expandGroups func.AntisymGroups

        // Mangled name encodes every slot's arity, so different shapes get
        // distinct specializations. `pairSum_arity_2_3` for arities [2; 3].
        let arityTag = arities |> List.map string |> String.concat "_"

        { Id = builder.FreshId()
          Name = sprintf "%s_arity_%s" func.Name arityTag
          Params = expandedParams
          RetType = newRetType
          Body = newBody
          IsStatic = func.IsStatic
          IsCommutative = newIsComm
          CommGroups = newCommGroups
          AntisymGroups = newAntisymGroups
          Parallelism = func.Parallelism
          IsOmpParallel = func.IsOmpParallel
          IsCudaKernel = func.IsCudaKernel
          CudaBlockSize = func.CudaBlockSize
          IsMpiParallel = func.IsMpiParallel
          IsArityPoly = false
          ArityParam = None
          // Specialized clones inherit the original's captures verbatim;
          // arity specialization doesn't introduce new free vars.
          Captures = func.Captures
          // Sign parities are per-PARAMETER: a pack slot expanding to k
          // positions replicates its origin's parity across them. Vacuous
          // today (only apply-seam kernel lambdas carry a summary, and
          // those are fixed-arity) but kept honest for a future producer.
          SignParities =
            (if List.isEmpty func.SignParities then []
             else
                func.SignParities
                |> List.mapi (fun idx p ->
                    match Map.tryFind idx origToNew with
                    | Some (_, span) -> List.replicate span p
                    | None -> [p])
                |> List.concat) }

// Inline-Form Lifting Pass
//
// Some IR forms (IRMask, IRSort, IRIntersect, IRUnion, IRGroupBy, IRGroupKeys)
// require a *named binding*: codegen for the form emits multi-statement
// setup (size computation, allocation, fill loop), so inline use as a
// sub-expression (`reduce(mask(g, pred), op)`) would need every consumer
// (IRExtent, IRIndex, IRApp, ...) to inline-emit that setup itself.
//
// Rather than bespoke inline-materialization per consumer, this pass
// normalizes the IR: any inline-form occurrence in a non-let-RHS position
// is rewritten to a fresh `IRLet(tmp, form, parent(IRVar(tmp, ty)))`, so
// codegen only ever sees the canonical `let tmp = mask(...)` pattern.
//
// Blessed positions (no rewrite): the value side of an IRLet, and the
// Arrays list of IRMethodFor/IRApplyCombinator (auto-materialized at
// codegen). Everywhere else the rewrite fires.

/// Map from struct name to its fields, used by typeOf for IRFieldAccess
/// resolution. Built at liftInlineFormsModule entry, used throughout the
/// same lift-pass invocation.
///
/// Thread-safety: the test runner compiles tests in parallel
/// (`Array.Parallel.mapi`); a plain module-level mutable Dictionary would
/// let one test's `setStructFieldsCache` wipe another's cache state,
/// causing intermittent `IRTUnit` results (see the `Struct Array With
/// Array Field` regression). `AsyncLocal<T>` with a fresh Dictionary per
/// set call gives each task its own instance.
let private structFieldsCacheStorage =
    System.Threading.AsyncLocal<System.Collections.Generic.Dictionary<string, (string * IRType) list>>()

let private getStructFieldsCache () : System.Collections.Generic.Dictionary<string, (string * IRType) list> =
    let v = structFieldsCacheStorage.Value
    if isNull v then
        let fresh = System.Collections.Generic.Dictionary<string, (string * IRType) list>()
        structFieldsCacheStorage.Value <- fresh
        fresh
    else v

let setStructFieldsCache (types: IRTypeDef list) =
    // Create a fresh Dictionary for this async context -- do not reuse and
    // .Clear() a shared instance, since other tasks may hold the same
    // reference from earlier in the parallel test run.
    let cache = System.Collections.Generic.Dictionary<string, (string * IRType) list>()
    for td in types do
        match td with
        | IRTDStruct (name, fields) -> cache.[name] <- fields
        | _ -> ()
    structFieldsCacheStorage.Value <- cache

let tryLookupFieldType (objType: IRType) (fieldName: string) : IRType option =
    match objType with
    | IRTNamed structName ->
        let cache = getStructFieldsCache ()
        match cache.TryGetValue(structName) with
        | true, fields ->
            fields |> List.tryFind (fun (n, _) -> n = fieldName) |> Option.map snd
        | false, _ -> None
    | _ -> None

// Canonical expression typing -- typeOf (audit section 2.2)
//
// THE one type-reconstruction over IR expressions. Until every IR node
// carries its type (the rewrite's design), passes that need a type must
// re-derive it -- multiple hand-maintained derivations invite silent
// divergence between them (a wrong-codegen bug class). The three roles:
//   - typeOf                -- the full reconstruction (this section)
//   - exprTypeIfKnown       -- the CarriedType subset only (HM call sites
//                             must not unify against reconstructed types;
//                             see its doc comment)
//   - CodeGen.inferExprType -- thin alias of typeOf
//
// Dispatch is organized as active-pattern families feeding a top-level
// match, not one flat 74-arm wall:
//   CarriedType -- types carried directly on the node (defined earlier,
//                 shared with exprTypeIfKnown)
//   TypeVia     -- variants whose type IS one distinguished child's type
//   IntValued   -- index-arithmetic markers, always Int64
// Structural rules (indexing, group_by, gram, ...) follow, and the
// deliberately-untyped variants (loop objects, emission-internal markers)
// are enumerated WITHOUT a wildcard, so a new IRExpr variant demands an
// explicit typing decision here instead of silently becoming IRTUnit.

/// Variants whose type equals one distinguished child's type. The returned
/// expression is the child whose type to take -- not necessarily the first
/// child (e.g. `IRComposeMeth (_, right)` types as `right`).
let (|TypeVia|_|) (expr: IRExpr) : IRExpr option =
    match expr with
    // Shape- and element-preserving array transforms.
    | IRSort (a, _) | IRArrayNegate a | IRArrayConjugate a
    // Element-preserving, extent-changing set ops (extent is runtime data,
    // not part of the arrow shape consumers read).
    | IRIntersect (a, _) | IRUnion (a, _) | IRUnique a
    // Computation wrappers erase at this level.
    | IRCompute a | IRPure a
    // Control flow: the type of the canonical branch/body.
    | IRLet (_, _, a) | IRIf (_, a, _) | IRGuard (_, a) | IRChoice (a, _)
    // <|:> result is the dense expansion; the RIGHT side already carries the
    // dense type (compound-left widens to it, dense-left unified with it).
    | IRFallback (_, a)
    // @>> composition: the right side's type.
    | IRComposeMeth (_, a) ->
        Some a
    | IRMatch (_, c :: _) -> Some c.Body
    | _ -> None

/// Index-space arithmetic markers: always Int64 scalars.
let (|IntValued|_|) (expr: IRExpr) : unit option =
    match expr with
    | IRArity _ | IRNth | IRRank _ | IRExtent _ | IRRaggedLookup _
    | IRCompoundMask _ | IRCompoundProject _ | IRSparseKeys _ | IROrbitClass _
    | IROpaqueExtent | IRRange _ ->
        Some ()
    | _ -> None

// Synthetic sentinel index IDs
//
// Some typeOf branches construct an IRIndexType in flight (e.g. recovering
// the rank-2 shape of a not-yet-let-bound IRGroupBy result) without access
// to an IRBuilder to allocate fresh IDs. Convention: synthetic sentinel IDs
// are NEGATIVE (IRBuilder.FreshId counts up from 0, so the negative range
// never collides); each synthesizing call site picks a distinct one below.
// IDs are not load-bearing for codegen -- consumers pattern-match on
// structure (ArrayElem, IRTScalar) and `Tag`, not `Id`.
let synthSlotIdOuter : IRId = -1
let synthSlotIdMember : IRId = -2
let synthSlotIdCompoundResidual : IRId = -3

// Compound partial-index classification (formalism 4.5)
//
// Shared by typeOf's IRIndex arm, CodeGen's genScalarBinding wrapper
// decision, and exprToCppCore's compoundRead emission, so the three never
// disagree about WHICH indexing form a compound read is.
//
// A wildcard coordinate arrives as an `IRLit IRLitUnit` sentinel: TypeCheck's
// dispatchAppOrIndex rewrites each TExprWildcard hole to a unit literal
// (never a valid coordinate, so unambiguous). A short tuple (arity j < k, no
// sentinels) pins the LEADING j coordinates -- B((a,b)) and B((a,b,_)) on a
// rank-3 compound are the same read.

/// Classification of the FIRST index against a rank-k compound head slot.
type CompoundIndexForm =
    /// All k coordinates pinned: the compound axis is fully consumed.
    | CompoundFull
    /// Partial: `pinned` = (axis position, coordinate expr) for each pinned
    /// axis in increasing position order; `freePos` = the free axis positions.
    | CompoundPartial of pinned: (int * IRExpr) list * freePos: int list

let classifyCompoundIndexTuple (k: int) (coords: IRExpr list) : CompoundIndexForm =
    let isFreeSentinel = function IRLit IRLitUnit -> true | _ -> false
    if coords |> List.exists isFreeSentinel then
        // Full-arity wildcard tuple (TypeCheck enforces arity = k and >= 1 pin).
        let indexed = coords |> List.mapi (fun i c -> (i, c))
        let pinned = indexed |> List.filter (fun (_, c) -> not (isFreeSentinel c))
        let free = indexed |> List.filter (fun (_, c) -> isFreeSentinel c) |> List.map fst
        CompoundPartial (pinned, free)
    elif coords.Length < k then
        // Short tuple: leading-prefix pin, trailing axes free.
        CompoundPartial (coords |> List.mapi (fun i c -> (i, c)), [coords.Length .. k - 1])
    else
        CompoundFull

/// Coverage-arm backstop: a family active pattern above no longer covers a
/// constructor its coverage arm claims. Impossible unless a family pattern
/// was edited out of sync with typeOf's coverage tail -- fail loudly rather
/// than mistype silently.
let private unreachableTyping (family: string) (expr: IRExpr) : 'a =
    failwithf "typeOf: family pattern %s no longer covers %s -- coverage arm and family out of sync"
        family (expr.GetType().Name)

/// The canonical expression type reconstruction. See the section comment
/// above for how this relates to exprTypeIfKnown and CodeGen.inferExprType.
let rec typeOf (expr: IRExpr) : IRType =
    match expr with
    // -- Node-carried types (shared with exprTypeIfKnown) --
    | CarriedType ty -> ty

    // -- Pass-throughs: the type of one distinguished child --
    | TypeVia child -> typeOf child

    // -- Index-arithmetic markers --
    | IntValued -> IRTScalar ETInt64

    | IRBinOp (_, op, left, right) ->
        (match op with
         | IREq | IRNeq | IRLt | IRLe | IRGt | IRGe | IRAnd | IROr -> IRTScalar ETBool
         | _ ->
             match typeOf left, typeOf right with
             | IRTScalar e1, IRTScalar e2 ->
                 IRTScalar (promoteElemType e1 e2 |> Option.defaultValue e1)
             | lt, _ -> lt)
    | IRUnaryOp (op, operand) ->
        (match op with
         | IRNot -> IRTScalar ETBool
         | IRNeg -> typeOf operand
         | IRConj -> typeOf operand
         // real/imag project a complex to its component width (identity on a
         // real operand); arg is a real angle. Complex128 -> Float64,
         // Complex64 -> Float32.
         | IRReal | IRImag ->
             (match typeOf operand with
              | IRTScalar ETComplex64 -> IRTScalar ETFloat32
              | IRTScalar ETComplex128 -> IRTScalar ETFloat64
              | other -> other)
         | IRArg -> IRTScalar ETFloat64
         // abs always yields the real magnitude (Float64); other intrinsics on
         // a complex operand preserve the complex type (std::exp(complex)->
         // complex), and stay Float64 on a real operand.
         | IRMath "abs" -> IRTScalar ETFloat64
         | IRMath _ ->
             (match typeOf operand with
              | IRTScalar (ETComplex64 | ETComplex128) as ct -> ct
              | _ -> IRTScalar ETFloat64))
    | IRTuple exprs -> IRTTuple (exprs |> List.map typeOf)
    | IRComplex (re, _) ->
        // Complex type derived from component width: Float32 -> Complex64,
        // Float64 -> Complex128. Reports as a scalar (NOT a tuple) -- that's
        // the whole point of having a separate IRComplex node.
        (match typeOf re with
         | IRTScalar ETFloat32 -> IRTScalar ETComplex64
         | _ -> IRTScalar ETComplex128)
    | IRTupleProj (e, i, isFlat) ->
        let parentTy = typeOf e
        if isFlat then
            let leaves = flattenTupleLeaves parentTy
            if i < leaves.Length then leaves.[i] else IRTUnit
        else
            (match parentTy with
             | IRTTuple ts when i < ts.Length -> ts.[i]
             | _ -> IRTUnit)
    | IRMatch (_, []) -> IRTUnit
    | IRIndex (arr, indices, _) ->
        // Indexing peels dimensions; full indexing yields the element type.
        //
        // Compound-head partial indexing is the exception to positional
        // peeling: a rank-k compound is ONE slot filled by ONE tuple, and a
        // PARTIAL tuple (short prefix, or full-arity with wildcard sentinels)
        // REPLACES that slot with a residual fragment rather than consuming
        // it (mirroring TypeCheck's compoundResidualType). Without this
        // branch a partial read reports the element type, breaking chained/
        // inline consumers at codegen.
        (match typeOf arr with
         | ArrayElem arrTy ->
             let headTabulated =
                 match arrTy.IndexTypes with
                 | h :: _ when (h.IxKind = IxKCompound || h.IxKind = IxKSparse) -> Some h
                 | _ -> None
             (match headTabulated, indices with
              | Some h, (IRTuple coords) :: trailingIdxs ->
                  (match classifyCompoundIndexTuple h.Rank coords with
                   | CompoundPartial (pinned, freePos) ->
                       let rr = freePos.Length
                       // Residual keeps the PARENT's kind (a partially indexed
                       // sparse is a sparse), mirroring tabulatedResidualType.
                       let residualTag, residualKind =
                           match h.IxKind with
                           | IxKSparse -> Some "__sparseidx", IxKSparse
                           | _ -> Some "__compoundidx", IxKCompound
                       let residual =
                           if rr = 1 then
                               { Id = synthSlotIdCompoundResidual; Rank = 1
                                 Extent = IRCompoundProject (arr, pinned.Length)
                                 Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                                 Kind = SDimension; Dependencies = [] }
                           else
                               { Id = synthSlotIdCompoundResidual; Rank = rr
                                 Extent = IRCompoundProject (arr, pinned.Length)
                                 Symmetry = SymNone; Tag = residualTag; IxKind = residualKind
                                 Kind = SDimension; Dependencies = [] }
                       let trailingSlots = List.tail arrTy.IndexTypes
                       let trailingRemaining =
                           if trailingIdxs.Length <= trailingSlots.Length
                           then trailingSlots |> List.skip trailingIdxs.Length
                           else []
                       mkArrayLike { arrTy with IndexTypes = residual :: trailingRemaining }
                   | CompoundFull ->
                       // Full tuple consumes the one compound slot; any further
                       // indices consume trailing slots positionally (the
                       // pre-existing rule below already counts them right).
                       if indices.Length >= arrTy.IndexTypes.Length then arrTy.ElemType
                       else mkArrayLike { arrTy with IndexTypes = arrTy.IndexTypes |> List.skip indices.Length })
              | _ ->
                  if indices.Length >= arrTy.IndexTypes.Length then arrTy.ElemType
                  else mkArrayLike { arrTy with IndexTypes = arrTy.IndexTypes |> List.skip indices.Length })
         | t -> t)
    | IRSequence exprs ->
        (match exprs with
         | [] -> IRTUnit
         | _ ->
             // Sequence produces array with Idx<N> over element type
             let elemType = typeOf (List.head exprs)
             (match elemType with
              | ArrayElem arr ->
                  // Array elements: prepend sequence dimension
                  let seqIdx = { Id = 0; Rank = 1; Extent = IRLit (IRLitInt (int64 exprs.Length)); Symmetry = SymNone; Tag = Some "__seq"; IxKind = IxKSeq; Kind = SDimension; Dependencies = [] }
                  mkArrayLike { arr with IndexTypes = seqIdx :: arr.IndexTypes }
              | IRTScalar et ->
                  // Scalar elements: simple array
                  let seqIdx = { Id = 0; Rank = 1; Extent = IRLit (IRLitInt (int64 exprs.Length)); Symmetry = SymNone; Tag = Some "__seq"; IxKind = IxKSeq; Kind = SDimension; Dependencies = [] }
                  mkArrayArrow [seqIdx] (IRTScalar et) None
              | _ -> elemType))
    | IRAssign _ -> IRTUnit
    | IRForRange _ -> IRTUnit
    | IRConstraintCheck _ -> IRTUnit
    | IRFieldAccess (obj, field) ->
        // Resolved via the ONE struct-fields cache (structFieldsCacheStorage
        // above), populated both at liftInlineFormsModule entry and at
        // codegen module entry from the same module's Types -- collapsing the
        // duplicate codegen-side cache that audit section 2.4 flagged as a
        // valid-but-wrong-lookup hazard.
        (match tryLookupFieldType (typeOf obj) field with
         | Some ty -> ty
         | None -> IRTUnit)
    | IRFunctorMap (f, c) ->
        // f <$> c: return type is f's return type
        (match typeOf f with
         | FuncElem (_, retTy) -> retTy
         | _ -> typeOf c)  // fallback: preserve computation type
    | IRBind (_, cont) ->
        // >>= : result type is continuation's return type
        (match typeOf cont with
         | FuncElem (_, retTy) -> retTy
         | t -> t)
    | IRParallel (l, r, _) -> IRTTuple [typeOf l; typeOf r]
    | IRFusion (l, r) -> IRTTuple [typeOf l; typeOf r]
    | IRMask (arr, _) ->
        // Bool presence array over the source's own index space (verbatim
        // records -- index-space identity feeds compound()).
        (match typeOf arr with
         | ArrayElem a -> mkArrayLike { a with ElemType = IRTScalar ETBool }
         | t -> t)
    | IRContains _ -> IRTScalar ETBool  // Membership returns bool
    | IRGroupBy (v, gk) ->
        // TypeCheck's `ExprGroupBy` rule constructs a rank-2 array type with
        // `__group_outer` + `__group_member` tagged index slots. For
        // let-bound group_by results (the only currently-allowed usage;
        // inline group_by in method_for() is rejected at codegen entry) the
        // binding's Type field carries this form, and `IRVar` lookups return
        // it correctly. This branch fires when an IRGroupBy node is
        // consulted directly (lifted bindings, future inline support):
        // reconstruct the same rank-2 form so shape-matching consumers see
        // the correct structure (see `synthSlotId*` above).
        let valsTy = typeOf v
        let gkTy = typeOf gk
        (match gkTy, valsTy with
         | IRTGroupKeys (outerIdx, _, _), ArrayElem valsArr ->
             let outer = { outerIdx with Id = synthSlotIdOuter; Tag = Some "__group_outer"; IxKind = IxKGroupOuter }
             let memberIdx = {
                 Id = synthSlotIdMember
                 Rank = 1
                 Extent = IRParam ("__groupsz", 0, IRTNat None)
                 Symmetry = SymNone
                 Tag = Some "__group_member"; IxKind = IxKGroupMember
                 Kind = SDimension
                 Dependencies = []
             }
             mkArrayArrow [outer; memberIdx] valsArr.ElemType None
         | _ ->
             // Fallback: gk isn't IRTGroupKeys-typed yet or v isn't an
             // array. Returning vals's type preserves the prior placeholder
             // behavior -- same shape, same element type -- so any caller
             // that was previously satisfied stays satisfied.
             valsTy)
    | IRGroupKeys _ -> IRTUnit  // GroupKeys is an opaque structure, not a runtime value with a simple type
    | IRTranspose (arr, d1, d2) ->
        // Swap the two index slots. (TypeCheck has already verified both axes
        // are arity-1 SymNone, so dim index == slot index here.)
        (match typeOf arr with
         | ArrayElem a when d1 < a.IndexTypes.Length && d2 < a.IndexTypes.Length ->
            let swapped =
                a.IndexTypes
                |> List.mapi (fun i ix ->
                    if i = d1 then a.IndexTypes.[d2]
                    elif i = d2 then a.IndexTypes.[d1]
                    else ix)
            mkArrayLike { a with IndexTypes = swapped }
         | t -> t)
    | IRDecompact (arr, d) ->
        // Split the compact slot containing dim d: left-remainder / extracted
        // Idx / right-remainder. Shape only (codegen reads arity/symmetry off
        // this); Ids reused -- authoritative nominal type is set by TypeCheck.
        (match typeOf arr with
         // FULL decompaction of a wreath class, ahead of the dimension walk:
         // `d` is the number of LEVELS TO KEEP (docs/plan-orbidx-decompaction.md
         // section 4.3), and TypeCheck's inferDecompact has already refused
         // every d except 0. The shape is the dense rank-prod(ri) tensor: one
         // plain Idx<n> axis per raw axis, extent the class's BASE extent
         // (reading `ix.Extent` directly would put the IROrbitClass marker
         // itself on a dense axis). Built from scratch, not `{ ix with ... }`:
         // IxKOrbit, the "__orbidx" Tag, and the IROrbitClass extent are all
         // wreath-specific and validator-enforced together.
         | ArrayElem a when (match a.IndexTypes with
                             | [ ix ] -> ix.Symmetry = SymWreath
                             | _ -> false) ->
            let ix = List.head a.IndexTypes
            let axes = max 1 ix.Rank
            let baseExtent = orbitBaseExtent ix
            let denseAxis =
                { Id = ix.Id; Rank = 1; Extent = baseExtent
                  Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                  Kind = SDimension; Dependencies = [] }
            mkArrayLike { a with IndexTypes = List.replicate axes denseAxis }
         | ArrayElem a ->
            let rec walk slotIdx acc remaining =
                match remaining with
                | [] -> None
                | (ix: IRIndexType) :: rest ->
                    let ar = max 1 ix.Rank
                    if d < acc + ar then Some (slotIdx, ar, d - acc, ix)
                    else walk (slotIdx + 1) (acc + ar) rest
            (match walk 0 0 a.IndexTypes with
             // A wreath COMBINED with other slots is excluded rather than
             // refused: `typeOf` is a pure shape reconstruction with no error
             // channel, and TypeCheck's inferDecompact already refuses that
             // arrangement with the real diagnostic. Excluding it means that
             // if one somehow arrived here, this returns the array's shape
             // UNCHANGED instead of a `{ ix with Rank = ar }` whose Rank no
             // longer matches its level list (internally inconsistent but
             // looking well formed).
             | Some (slot, r, posInSlot, ix) when r >= 2 && ix.Symmetry <> SymNone
                                                  && ix.Symmetry <> SymWreath ->
                let mkRemainder (ar: int) : IRIndexType list =
                    if ar <= 0 then []
                    elif ar = 1 then [ { ix with Rank = 1; Symmetry = SymNone } ]
                    else [ { ix with Rank = ar } ]
                let extracted = { ix with Rank = 1; Symmetry = SymNone }
                let replacement = mkRemainder posInSlot @ [extracted] @ mkRemainder (r - 1 - posInSlot)
                let newIdx =
                    a.IndexTypes
                    |> List.mapi (fun i s -> (i, s))
                    |> List.collect (fun (i, s) -> if i = slot then replacement else [s])
                mkArrayLike { a with IndexTypes = newIdx }
             | _ -> mkArrayLike a)
         | t -> t)
    | IRGram (l, r, sameArray) ->
        // gram(A, B) = A * B^H. A : m x n, B : p x n -> m x p. Element type is
        // complex iff either operand is complex. Same-array -> square m x m,
        // compact group of arity 2 (Hermitian if complex, else symmetric);
        // distinct -> dense m x p (two plain axes).
        (match typeOf l, typeOf r with
         | ArrayElem la, ArrayElem ra when la.IndexTypes.Length >= 1 && ra.IndexTypes.Length >= 1 ->
            let isComplexElem (t: IRType) =
                match t with IRTScalar (ETComplex64 | ETComplex128) -> true | _ -> false
            let outElem =
                if isComplexElem la.ElemType then la.ElemType
                elif isComplexElem ra.ElemType then ra.ElemType
                else la.ElemType
            let mOuter = la.IndexTypes.[0]
            let pOuter = ra.IndexTypes.[0]
            if sameArray then
                let sym = if isComplexElem outElem then SymHermitian else SymSymmetric
                let grp = { mOuter with Rank = 2; Symmetry = sym }
                mkArrayLike { la with ElemType = outElem; IndexTypes = [grp] }
            else
                let s0 = { mOuter with Rank = 1; Symmetry = SymNone }
                let s1 = { pOuter with Rank = 1; Symmetry = SymNone }
                mkArrayLike { la with ElemType = outElem; IndexTypes = [s0; s1] }
         | t, _ -> t)
    | IRMatmul (l, r) ->
        // matmul(A, B). A : m x k, B : k x n -> DENSE m x n (two plain axes,
        // SymNone). No conjugation and no symmetry claim: unlike gram, matmul
        // over the same array is not symmetric, so there is no same-array mode.
        // Element type is the left operand's (the checker requires both real
        // Float64 -- the shim's current domain).
        (match typeOf l, typeOf r with
         | ArrayElem la, ArrayElem ra when la.IndexTypes.Length >= 1 && ra.IndexTypes.Length >= 1 ->
            let mOuter = la.IndexTypes.[0]
            let nOuter = List.last ra.IndexTypes
            let s0 = { mOuter with Rank = 1; Symmetry = SymNone }
            let s1 = { nOuter with Rank = 1; Symmetry = SymNone }
            mkArrayLike { la with IndexTypes = [s0; s1] }
         | t, _ -> t)
    | IREigh operand ->
        // eigh(S) -> (Q : n x n dense, LAM : n dense).
        //
        // THE MIXED-ELEMENT TUPLE: Q inherits the operand's element type, but
        // LAM does NOT (eigenvalues of a symmetric/Hermitian matrix are REAL,
        // so Complex128 yields `(Array<Complex128,2>, Array<Float64,1>)`) --
        // matching `blade_lapack`'s signatures (`std::complex<double>** V`
        // beside `double* lam`); getting it wrong would be a silent
        // storage-width error rather than a type error.
        //
        // The operand is rank-2 square in EITHER admissible spelling (ONE
        // compact slot of arity 2, or TWO plain dense axes); both give n from
        // the first slot's extent. Ids here are cosmetic -- the authoritative
        // result type is the one `inferEigh` built and lowering attached.
        (match typeOf operand with
         | ArrayElem sa when not sa.IndexTypes.IsEmpty ->
            let ix0 = sa.IndexTypes.Head
            let axis = { ix0 with Rank = 1; Symmetry = SymNone; IxKind = IxKPlain; Dependencies = [] }
            let lamElem =
                match sa.ElemType with
                | IRTScalar ETComplex128 -> IRTScalar ETFloat64
                | IRTScalar ETComplex64 -> IRTScalar ETFloat32
                | t -> t
            let qTy = mkArrayLike { sa with IndexTypes = [axis; axis] }
            let lamTy = mkArrayLike { sa with ElemType = lamElem; IndexTypes = [axis] }
            IRTTuple [qTy; lamTy]
         | t -> t)
    | IRHaloUnhash _ ->
        // A window neighbor read yields the inner index's coordinate: int64.
        IRTScalar ETInt64
    | IRReduce (arr, _, _) ->
        // Reduces innermost dim by 1. For rank-1 input, result is a scalar.
        (match typeOf arr with
         | ArrayElem a when a.IndexTypes.Length = 1 -> a.ElemType  // IRType already
         | ArrayElem a ->
             // Multi-rank reduction: drop innermost index. (Not yet supported by
             // codegen; TypeCheck rejects rank>1 today, but keep this consistent.)
             mkArrayLike { a with IndexTypes = a.IndexTypes |> List.take (a.IndexTypes.Length - 1) }
         | t -> t)
    | IRReduceCompute (comp, _, seed) ->
        // Fused reduction terminal: one scalar per fusion leaf. The seed
        // carries the accumulator type (checker-unified with every leaf's
        // element type); the result mirrors the tree's nested-pair shape.
        let rec shape e =
            match e with
            | IRFusion (l, r) -> IRTTuple [shape l; shape r]
            | _ -> typeOf seed
        shape comp
    | IRProdSum args ->
        // Scalar: the fused fold of rank-1 operands (TypeCheck enforces rank 1).
        (match args with
         | first :: _ ->
             (match typeOf first with
              | ArrayElem a -> a.ElemType
              | t -> t)
         | [] -> IRTScalar ETFloat64)

    // -- Deliberately untyped (loop objects, combinator/emission-internal
    //    markers -- not runtime values with a simple type). Enumerated with
    //    no wildcard so a NEW variant demands a typing decision here.
    | IRMethodFor _ | IRObjectFor _ | IRReynolds _ | IRArrayProduct _
    | IRComposeObj _ | IRCompose _
    | IRSlice _ | IRCurry _ | IRSubset _ | IRShift _ | IRReverse _ | IRDiag _
    | IRZip _ | IRAlign _ | IRStack _ | IRJoin _
    | IRTupleCons _ | IRTupleDecons _ | IRPolyIndex _ | IRPolyTail _ | IRReplicate _
    | IRVirtualReverse _ | IRBlocked _ | IRZero ->
        IRTUnit

    // -- Coverage tail ---------------------------------------------------
    // Every constructor below is already handled by a family pattern above
    // (partial active patterns are invisible to the exhaustiveness checker).
    // Listing them keeps this match provably exhaustive WITHOUT a wildcard --
    // a brand-new IRExpr variant still fails to compile until it gets a
    // typing rule -- and if one of these arms ever fires, a family pattern
    // was edited out of sync: fail loudly, never mistype.
    | IRVar _ | IRParam _ | IRApp _ | IRArrayLit _ | IRStructLit _
    | IRApplyCombinator _ | IRComposeApply _ | IRLit _ ->
        unreachableTyping "CarriedType" expr
    | IRSort _ | IRArrayNegate _ | IRArrayConjugate _ | IRIntersect _
    | IRUnion _ | IRUnique _ | IRCompute _ | IRPure _ | IRLet _ | IRIf _
    | IRGuard _ | IRChoice _ | IRFallback _ | IRComposeMeth _ | IRMatch _ ->
        unreachableTyping "TypeVia" expr
    | IRArity _ | IRNth | IRRank _ | IRExtent _ | IRRaggedLookup _
    | IRCompoundMask _ | IRCompoundProject _ | IRSparseKeys _ | IROrbitClass _
    | IROpaqueExtent | IRRange _ ->
        unreachableTyping "IntValued" expr

/// Predicate: is this an inline form that needs lifting when in a non-blessed
/// position? Excludes IRReduce (its own codegen handles inline forms via
/// IIFE; the array argument is what gets lifted, not reduce itself).
/// Includes IRReduceCompute (statement-shaped, no expression/IIFE rendering,
/// only emittable at a let-binding); IRCompute(IRApplyCombinator ...) (only
/// correct as a statement-form loop nest at a let-RHS -- the bare unwrapped
/// IRApplyCombinator is NOT lifted, being genuinely deferred with no
/// materialized value); IRMatmul (an intrinsic node from the math package's
/// in-place elaborator, so it reaches ordinary expression positions and must
/// be hoisted to materialize its pool -- IRGram is NOT included, entering
/// only via the `gram` keyword's let-RHS); and IREigh (same elaborator, two
/// pools plus the naming tuple).
let isInlineForm (e: IRExpr) : bool =
    match e with
    | IRMask _ | IRSort _ | IRIntersect _ | IRUnion _ | IRUnique _
    | IRGroupBy _ | IRGroupKeys _ | IRTranspose _ | IRDecompact _ | IRArrayNegate _ | IRArrayConjugate _
    | IRReduceCompute _ | IRMatmul _ | IREigh _ -> true
    | IRCompute (IRApplyCombinator _) -> true
    | _ -> false

/// A loop-form array operand (in a method_for / apply-combinator / compose-apply
/// `Arrays` list) that is itself a forced or inline elementwise computation --
/// e.g. the left input `A * B` of a chained positional op `A * B * C`, which
/// lowers to `IRCompute(IRApplyCombinator ...)`. Unlike the blessed inline forms
/// (mask/intersect/union/unique), these have NO codegen-side auto-materialize
/// path, so the loop-nest builder names them `arr0` and reads an array it never
/// declared (`error: 'arr0' was not declared in this scope`). They must be
/// hoisted to their own let-RHS so codegen materializes each into a real temp
/// before the outer loop consumes it -- exactly as writing the intermediate
/// `let` by hand would. Deliberately narrow: it does NOT list the blessed inline
/// forms, so their existing auto-materialize path stays untouched.
///
/// Array-typed APPLICATION and partial-INDEX operands are included for the same
/// reason -- `f(x) + g(x)` (both operands calls) and `m(0) + m(1)` (both operands
/// row views) equally leave the nest with no named array to read. A call operand
/// must also be evaluated exactly once rather than re-invoked per element. This
/// mirrors the `materialize` helper in `lowerArrayBinOpsModule`, which covers the
/// raw-`IRBinOp` half of the same problem. Fully-indexed reads are scalar, so
/// they fail the array-type test and stay inline.
let private isNestedLoopComputeArg (e: IRExpr) : bool =
    let isArrayTyped () =
        match typeOf e with
        | ArrayElem _ -> true
        | _ -> false
    match e with
    | IRCompute _ -> true
    | IRApp (IRObjectFor _, _, _) -> true
    | IRApp _ | IRIndex _ -> isArrayTyped ()
    // `m.matmul(A, B) * 2.0` puts a matmul directly in a loop form's Arrays
    // list. As an intrinsic node -- not a synthesized function-call IRApp,
    // which the line above already hoists -- it needs its own entry or the
    // nest reads an `arr<i>` it never declared.
    | IRMatmul _ -> true
    // IREigh is deliberately ABSENT, and its absence is a decision rather than
    // an omission: an eigh node is TUPLE-typed, and a loop form's `Arrays` slot
    // holds arrays. There is no surface spelling that puts a tuple where the
    // nest expects an array -- the destructured `Q` / `LAM` are what reach a
    // loop, and those are ordinary IRVars by then. Adding an arm here would be
    // a dead branch that reads as if it guarded something.
    | _ -> false

/// An INLINE array literal sitting directly in a loop form's `Arrays` list --
/// e.g. the right operand of `yr - [2.0, -14.0]`. Same gap as
/// `isNestedLoopComputeArg`: the blessed-position exemption assumes codegen's
/// auto-materialize covers the slot, but that arm only knows the inline
/// mask/intersect/union/unique forms. An IRArrayLit falls through to the
/// `arr<i>` placeholder and the nest peels an identifier that was never
/// declared. Hoisting it to its own let-RHS routes it through the ordinary
/// array-literal emission, exactly as let-binding it by hand would.
let private isInlineArrayLitArg (e: IRExpr) : bool =
    match e with
    | IRArrayLit _ -> true
    | _ -> false

/// Peel any IRLet chain that descendant lifts produced.
/// When a sub-expression's lift wraps it in `IRLet(id, v, IRLet(...,inner))`,
/// the chain shouldn't be visible to the parent context (e.g., an outer
/// IRArrayLit's element list, or a struct field value). Peeling pulls the
/// chain out as a list of bindings; the caller's wrapLets re-wraps them at
/// the appropriate enclosing level.
///
/// Without peeling, lifts produced by descendant calls would appear as
/// siblings of other elements in multi-child contexts, breaking codegen
/// (e.g., the genArrayLiteral walker treats IRLet as a leaf and emits an
/// IIFE that doesn't know how to render an IRArrayLit inline).
let peelLetChain (e: IRExpr) : (IRId * IRType * IRExpr) list * IRExpr =
    let rec loop acc e =
        match e with
        | IRLet (id, v, body) -> loop (acc @ [(id, typeOf v, v)]) body
        | _ -> (acc, e)
    loop [] e

/// Predicate: is this an IRFieldAccess whose result type is an array? Such
/// accesses need to be hoisted to a let-RHS so codegen can synthesize the
/// companion `_extents` (and `_lens` for ragged) array -- without a let-RHS
/// drain point, the field access expression `t.samples` produces a pointer
/// but no shape information, breaking any consumer that expects an extents
/// sibling (kernel args, reduce, method_for, etc.).
let private isArrayFieldAccess (e: IRExpr) : bool =
    match e with
    | IRFieldAccess _ ->
        match typeOf e with
        | ArrayElem _ -> true
        | _ -> false
    | _ -> false

/// Lift a single child if it's an inline form. Returns either ([], child)
/// for the no-rewrite case, or ([(id, ty, child)], IRVar(id, ty)) for the
/// lifted case.
///
/// Also peels any IRLet chain the descendant produced, so the chain
/// bindings hoist alongside any new lift binding to the caller's wrap
/// point.
let liftChild (builder: IRBuilder) (child: IRExpr) : (IRId * IRType * IRExpr) list * IRExpr =
    let (peeled, inner) = peelLetChain child
    if isInlineForm inner then
        let id = builder.FreshId()
        let ty = typeOf inner
        (peeled @ [(id, ty, inner)], IRVar (id, ty))
    elif isArrayFieldAccess inner then
        // Hoist `t.samples` (when samples is array-typed) into a
        // let-RHS so codegen can synthesize `<bound_name>_extents`.
        let id = builder.FreshId()
        let ty = typeOf inner
        (peeled @ [(id, ty, inner)], IRVar (id, ty))
    else
        (peeled, inner)

/// Like `liftChild`, but additionally lifts IRArrayLit. Used at sites
/// where an inline IRArrayLit can't render (struct field values, function
/// args). NOT used at IRArrayLit element positions -- there, the inner
/// IRArrayLit must remain so the genArrayLiteral walker sees full nesting
/// depth (otherwise dims and per-leaf indexing break).
let liftChildIncludingArrayLit (builder: IRBuilder) (child: IRExpr) : (IRId * IRType * IRExpr) list * IRExpr =
    let (peeled, inner) = peelLetChain child
    match inner with
    | IRArrayLit (_, arrTy) ->
        let id = builder.FreshId()
        let ty = mkArrayLike arrTy
        (peeled @ [(id, ty, inner)], IRVar (id, ty))
    | e when isInlineForm e ->
        let id = builder.FreshId()
        let ty = typeOf e
        (peeled @ [(id, ty, e)], IRVar (id, ty))
    | e when isArrayFieldAccess e ->
        // Same hoisting as liftChild, so struct field values and
        // function args carrying `t.samples` get the same treatment.
        let id = builder.FreshId()
        let ty = typeOf e
        (peeled @ [(id, ty, e)], IRVar (id, ty))
    | e -> (peeled, e)

/// Like `liftChild`, but ALSO hoists array-typed applications, partial index
/// reads and forced computations -- i.e. everything `isNestedLoopComputeArg`
/// covers. Used for the LINALG intrinsic operands (`gram`, `matmul`): their
/// emission spells each operand's C++ text more than once (`X.extents[0]`,
/// `X.extents[1]`, `X.data`), so an unhoisted call operand would be
/// re-invoked per occurrence, allocating a fresh array each time and handing
/// the contraction two different (if equal-valued) pools.
let liftChildEvaluatedOnce (builder: IRBuilder) (child: IRExpr) : (IRId * IRType * IRExpr) list * IRExpr =
    let (peeled, inner) = peelLetChain child
    if isInlineForm inner || isNestedLoopComputeArg inner || isArrayFieldAccess inner then
        let id = builder.FreshId()
        let ty = typeOf inner
        (peeled @ [(id, ty, inner)], IRVar (id, ty))
    else
        (peeled, inner)

/// Lift a list of children, accumulating bindings.
let liftChildren (builder: IRBuilder) (children: IRExpr list) : (IRId * IRType * IRExpr) list * IRExpr list =
    children |> List.fold (fun (binds, acc) child ->
        let (b, c) = liftChild builder child
        (binds @ b, acc @ [c])) ([], [])

/// Wrap an expression with a sequence of let-bindings (innermost first).
let wrapLets (bindings: (IRId * IRType * IRExpr) list) (body: IRExpr) : IRExpr =
    List.foldBack (fun (id, _, v) acc -> IRLet (id, v, acc)) bindings body

/// Walk an expression bottom-up, hoisting any inline form found in a
/// non-blessed child position into a fresh IRLet wrapping the parent.
///
/// Note: when an inline form is itself the IRLet-RHS, we leave it alone
/// (that's the canonical position). When it's nested inside IRMethodFor's
/// or IRApplyCombinator's Arrays list, we also leave it -- codegen's
/// auto-materialize handles those positions.
let rec liftExpr (builder: IRBuilder) (expr: IRExpr) : IRExpr =
    match expr with
    // Leaves: nothing to do
    | IRLit _ | IRVar _ | IRParam _ | IRNth | IRZero
    | IRRange _ | IRVirtualReverse _ | IRArity _
    | IROpaqueExtent -> expr

    // Blessed positions: don't lift the value's top-level inline form; do
    // descend into both sides for nested cases.
    | IRLet (id, value, body) ->
        IRLet (id, liftExpr builder value, liftExpr builder body)

    // The inline forms themselves: descend into their sub-expressions
    // (which may contain further nested inline forms), but DO NOT lift
    // them at this point -- the parent's child slot will lift them if
    // needed.
    | IRMask (a, p) ->
        // Lift inline-form array arg so codegen sees a let-bound name in
        // the array slot (rather than another inline form it can't render
        // inside its own template). The predicate is a lambda -- not an
        // inline form -- so it just recurses normally.
        let a' = liftExpr builder a
        let p' = liftExpr builder p
        let (binds, aFinal) = liftChildIncludingArrayLit builder a'
        wrapLets binds (IRMask (aFinal, p'))
    | IRSort (a, k) ->
        let a' = liftExpr builder a
        let k' = liftExpr builder k
        let (binds, aFinal) = liftChildIncludingArrayLit builder a'
        wrapLets binds (IRSort (aFinal, k'))
    | IRIntersect (a, b) ->
        let a' = liftExpr builder a
        let b' = liftExpr builder b
        let (bindsA, aFinal) = liftChildIncludingArrayLit builder a'
        let (bindsB, bFinal) = liftChildIncludingArrayLit builder b'
        wrapLets (bindsA @ bindsB) (IRIntersect (aFinal, bFinal))
    | IRUnion (a, b) ->
        let a' = liftExpr builder a
        let b' = liftExpr builder b
        let (bindsA, aFinal) = liftChildIncludingArrayLit builder a'
        let (bindsB, bFinal) = liftChildIncludingArrayLit builder b'
        wrapLets (bindsA @ bindsB) (IRUnion (aFinal, bFinal))
    | IRUnique a ->
        let a' = liftExpr builder a
        let (binds, aFinal) = liftChildIncludingArrayLit builder a'
        wrapLets binds (IRUnique aFinal)
    | IRGroupBy (v, k) -> IRGroupBy (liftExpr builder v, liftExpr builder k)
    | IRGroupKeys ks -> IRGroupKeys (List.map (liftExpr builder) ks)

    // Contains returns a scalar Bool -- its array argument may be an inline
    // form that needs lifting (so codegen can read .extents off a named binding).
    | IRContains (arr, v) ->
        let arr' = liftExpr builder arr
        let v' = liftExpr builder v
        let (binds, arrFinal) = liftChild builder arr'
        wrapLets binds (IRContains (arrFinal, v'))

    // Single-child consumers where the array slot can hold an inline form
    | IRReduce (arr, kernel, init) ->
        let arr' = liftExpr builder arr
        let kernel' = liftExpr builder kernel
        let init' = init |> Option.map (liftExpr builder)
        let (binds, arrFinal) = liftChild builder arr'
        wrapLets binds (IRReduce (arrFinal, kernel', init'))
    | IRReduceCompute (comp, kernel, seed) ->
        // The computation child is a deferred combinator (apply/fusion
        // tree) -- never lift it into a binding (it has no materialized
        // value); recurse for nested inline forms in kernel arrays/seed.
        IRReduceCompute (liftExpr builder comp, liftExpr builder kernel, liftExpr builder seed)
    | IRProdSum args ->
        // Every operand slot can hold an inline form; lift each so codegen
        // reads .extents off named bindings.
        let (allBinds, finals) =
            args |> List.fold (fun (bs, fs) a ->
                let a' = liftExpr builder a
                let (b, aFinal) = liftChild builder a'
                (bs @ b, fs @ [aFinal])) ([], [])
        wrapLets allBinds (IRProdSum finals)
    | IRExtent (arr, dim) ->
        let arr' = liftExpr builder arr
        let (binds, arrFinal) = liftChild builder arr'
        wrapLets binds (IRExtent (arrFinal, dim))
    | IRIndex (arr, idxs, identity) ->
        let arr' = liftExpr builder arr
        let idxs' = idxs |> List.map (liftExpr builder)
        let (binds, arrFinal) = liftChild builder arr'
        wrapLets binds (IRIndex (arrFinal, idxs', identity))
    | IRSlice (arr, dim, s, e) ->
        let arr' = liftExpr builder arr
        let s' = liftExpr builder s
        let e' = liftExpr builder e
        let (binds, arrFinal) = liftChild builder arr'
        wrapLets binds (IRSlice (arrFinal, dim, s', e'))
    | IRSubset (arr, dim, s, l) ->
        let arr' = liftExpr builder arr
        let s' = liftExpr builder s
        let l' = liftExpr builder l
        let (binds, arrFinal) = liftChild builder arr'
        wrapLets binds (IRSubset (arrFinal, dim, s', l'))
    | IRCurry (arr, idx, r) ->
        let arr' = liftExpr builder arr
        let idx' = liftExpr builder idx
        let (binds, arrFinal) = liftChild builder arr'
        wrapLets binds (IRCurry (arrFinal, idx', r))
    | IRTranspose (arr, d1, d2) ->
        let arr' = liftExpr builder arr
        let (binds, arrFinal) = liftChild builder arr'
        wrapLets binds (IRTranspose (arrFinal, d1, d2))
    | IRDecompact (arr, d) ->
        let arr' = liftExpr builder arr
        let (binds, arrFinal) = liftChild builder arr'
        wrapLets binds (IRDecompact (arrFinal, d))
    | IRHaloUnhash (w, o) ->
        // Scalar coordinate read; the window is a param var -- nothing to lift.
        IRHaloUnhash (liftExpr builder w, o)
    // Both linalg intrinsics use the evaluate-once lift: their emitters spell
    // each operand several times (extents + data), so a call/index operand has
    // to arrive as a named binding.
    | IRGram (l, r, s) ->
        let l' = liftExpr builder l
        let r' = liftExpr builder r
        let (bindsL, lFinal) = liftChildEvaluatedOnce builder l'
        let (bindsR, rFinal) = liftChildEvaluatedOnce builder r'
        wrapLets (bindsL @ bindsR) (IRGram (lFinal, rFinal, s))
    | IRMatmul (l, r) ->
        let l' = liftExpr builder l
        let r' = liftExpr builder r
        let (bindsL, lFinal) = liftChildEvaluatedOnce builder l'
        let (bindsR, rFinal) = liftChildEvaluatedOnce builder r'
        wrapLets (bindsL @ bindsR) (IRMatmul (lFinal, rFinal))
    | IREigh operand ->
        // Same evaluate-once lift, same reason: `materializeEighForm` spells
        // the operand THREE times (`.extents[0]`, and `.data` twice, bare and
        // via `pool_base`), so `eigh(f(A))` would otherwise re-invoke `f`
        // per occurrence, each call allocating a fresh pool.
        let operand' = liftExpr builder operand
        let (binds, opFinal) = liftChildEvaluatedOnce builder operand'
        wrapLets binds (IREigh opFinal)
    | IRArrayNegate arr ->
        let arr' = liftExpr builder arr
        let (binds, arrFinal) = liftChild builder arr'
        wrapLets binds (IRArrayNegate arrFinal)
    | IRArrayConjugate arr ->
        let arr' = liftExpr builder arr
        let (binds, arrFinal) = liftChild builder arr'
        wrapLets binds (IRArrayConjugate arrFinal)
    | IRReverse (arr, d) ->
        let arr' = liftExpr builder arr
        let (binds, arrFinal) = liftChild builder arr'
        wrapLets binds (IRReverse (arrFinal, d))
    | IRDiag arr ->
        let arr' = liftExpr builder arr
        let (binds, arrFinal) = liftChild builder arr'
        wrapLets binds (IRDiag arrFinal)
    | IRRank arr ->
        let arr' = liftExpr builder arr
        let (binds, arrFinal) = liftChild builder arr'
        wrapLets binds (IRRank arrFinal)
    | IRShift (arr, d, off, bm) ->
        let arr' = liftExpr builder arr
        let off' = liftExpr builder off
        let (binds, arrFinal) = liftChild builder arr'
        wrapLets binds (IRShift (arrFinal, d, off', bm))

    // Multi-child consumers (any arg can be an inline form)
    | IRApp (fn, args, retTy) ->
        // Function args may contain inline IRArrayLit (e.g.,
        // `f([1.0, 2.0, 3.0])`) which can't render inline. Use the
        // extended helper that lifts both inline forms and IRArrayLit.
        let fn' = liftExpr builder fn
        let args' = args |> List.map (liftExpr builder)
        let (binds, argsFinal) =
            args' |> List.fold (fun (accB, accA) a ->
                let (b, a') = liftChildIncludingArrayLit builder a
                (accB @ b, accA @ [a'])) ([], [])
        wrapLets binds (IRApp (fn', argsFinal, retTy))
    | IRJoin (arrs, dim) ->
        let arrs' = arrs |> List.map (liftExpr builder)
        let (binds, arrsFinal) = liftChildren builder arrs'
        wrapLets binds (IRJoin (arrsFinal, dim))
    | IRStack arrs ->
        let arrs' = arrs |> List.map (liftExpr builder)
        let (binds, arrsFinal) = liftChildren builder arrs'
        wrapLets binds (IRStack arrsFinal)
    | IRZip arrs ->
        let arrs' = arrs |> List.map (liftExpr builder)
        let (binds, arrsFinal) = liftChildren builder arrs'
        wrapLets binds (IRZip arrsFinal)
    | IRAlign (arrs, sp) ->
        let arrs' = arrs |> List.map (liftExpr builder)
        let (binds, arrsFinal) = liftChildren builder arrs'
        wrapLets binds (IRAlign (arrsFinal, sp))
    | IRTuple es ->
        let es' = es |> List.map (liftExpr builder)
        let (binds, esFinal) = liftChildren builder es'
        wrapLets binds (IRTuple esFinal)
    | IRComplex (re, im) ->
        let re' = liftExpr builder re
        let im' = liftExpr builder im
        let (binds, esFinal) = liftChildren builder [re'; im']
        match esFinal with
        | [reF; imF] -> wrapLets binds (IRComplex (reF, imF))
        | _ -> wrapLets binds (IRComplex (re', im'))  // unreachable; defensive
    | IRArrayLit (es, ty) ->
        // Peel any IRLet chains from element results
        // (descendant lifts) and re-wrap them at THIS level. Don't lift the
        // peeled inner expressions further -- IRArrayLit elements must
        // remain as the genArrayLiteral walker expects (nested IRArrayLit
        // for multi-dim, scalar leaves at the bottom). Replacing an inner
        // IRArrayLit with an IRVar would shorten computeArrayDims to just
        // this level and break extents/print/walker.
        let es' = es |> List.map (liftExpr builder)
        let (binds, esPeeled) = es' |> List.fold (fun (accB, accE) e ->
            let (b, e') = peelLetChain e
            (accB @ b, accE @ [e'])) ([], [])
        wrapLets binds (IRArrayLit (esPeeled, ty))

    // BinOps: array-typed binops can have inline forms on either side.
    | IRBinOp (mode, op, l, r) ->
        let l' = liftExpr builder l
        let r' = liftExpr builder r
        let (lBinds, lFinal) = liftChild builder l'
        let (rBinds, rFinal) = liftChild builder r'
        wrapLets (lBinds @ rBinds) (IRBinOp (mode, op, lFinal, rFinal))
    | IRUnaryOp (op, e) ->
        let e' = liftExpr builder e
        let (binds, eFinal) = liftChild builder e'
        wrapLets binds (IRUnaryOp (op, eFinal))

    // Pass-through traversals (no lift at this level; descend into sub-expressions)
    | IRTupleProj (e, i, fl) -> IRTupleProj (liftExpr builder e, i, fl)
    | IRTupleCons (h, t) -> IRTupleCons (liftExpr builder h, liftExpr builder t)
    | IRTupleDecons e -> IRTupleDecons (liftExpr builder e)
    | IRFieldAccess (e, f) -> IRFieldAccess (liftExpr builder e, f)
    | IRStructLit (n, flds) ->
        // Nested element types: descend into each field expression,
        // then lift IRArrayLit and inline-form values into auto-let bindings.
        // Array literals are statement-level constructs (allocation; rendered
        // by genArrayLiteral, not exprToCpp), so they cannot appear inline as
        // struct field values. The auto-let pattern moves the literal to a
        // let-RHS where genArrayLiteral handles it; the field value becomes
        // an IRVar reference. liftChildIncludingArrayLit also peels any
        // IRLet chains the descent produced (so they hoist past this struct
        // lit to the next drain point).
        let flds' = flds |> List.map (fun (fn, fe) -> (fn, liftExpr builder fe))
        let (binds, fldsLifted) =
            flds' |> List.fold (fun (accBinds, accFlds) (fn, fe) ->
                let (b, fe') = liftChildIncludingArrayLit builder fe
                (accBinds @ b, accFlds @ [(fn, fe')])) ([], [])
        wrapLets binds (IRStructLit (n, fldsLifted))
    | IRIf (c, t, e) -> IRIf (liftExpr builder c, liftExpr builder t, liftExpr builder e)
    | IRMatch (scr, cases) ->
        IRMatch (liftExpr builder scr, cases |> List.map (fun c ->
            { c with Guard = c.Guard |> Option.map (liftExpr builder)
                     Body = liftExpr builder c.Body }))
    | IRSequence es -> IRSequence (es |> List.map (liftExpr builder))
    | IRGuard (c, b) -> IRGuard (liftExpr builder c, liftExpr builder b)
    | IRReplicate (c, b) -> IRReplicate (liftExpr builder c, liftExpr builder b)
    | IRPure e -> IRPure (liftExpr builder e)
    | IRCompute e ->
        // Drain any let-chain that lifting the inner expression produced out
        // of the IRCompute wrapper: genComputeBinding has no IRLet arm, so a
        // let left inside IRCompute falls to the scalar exprToCpp path and
        // errors (BLADE_CODEGEN_ERROR_UNEVALUATED_COMPUTATION). Peeling it out
        // yields `let __t = A * B in (... |> compute)`, materialized by
        // genLetChainBinding as ordered statement bindings.
        let e' = liftExpr builder e
        let (peeled, inner) = peelLetChain e'
        wrapLets peeled (IRCompute inner)
    | IRReynolds (e, a) -> IRReynolds (liftExpr builder e, a)
    | IRBind (c, k) -> IRBind (liftExpr builder c, liftExpr builder k)
    | IRParallel (a, b, d) -> IRParallel (liftExpr builder a, liftExpr builder b, d)
    | IRFusion (a, b) -> IRFusion (liftExpr builder a, liftExpr builder b)
    | IRChoice (a, b) -> IRChoice (liftExpr builder a, liftExpr builder b)
    | IRFallback (a, b) -> IRFallback (liftExpr builder a, liftExpr builder b)
    | IRArrayProduct (a, b) -> IRArrayProduct (liftExpr builder a, liftExpr builder b)
    | IRComposeObj (a, b) -> IRComposeObj (liftExpr builder a, liftExpr builder b)
    | IRComposeMeth (a, b) -> IRComposeMeth (liftExpr builder a, liftExpr builder b)
    | IRCompose (a, b) -> IRCompose (liftExpr builder a, liftExpr builder b)
    | IRFunctorMap (fn, c) -> IRFunctorMap (liftExpr builder fn, liftExpr builder c)
    | IRPolyIndex (p, i) -> IRPolyIndex (liftExpr builder p, liftExpr builder i)
    | IRPolyTail (p, drop) -> IRPolyTail (liftExpr builder p, drop)
    | IRRaggedLookup l -> IRRaggedLookup (liftExpr builder l)
    | IRCompoundMask mk -> IRCompoundMask (liftExpr builder mk)
    | IRCompoundProject (parent, plen) -> IRCompoundProject (liftExpr builder parent, plen)
    | IRSparseKeys (SkRuntime keys) -> IRSparseKeys (SkRuntime (liftExpr builder keys))
    | IRSparseKeys (SkStatic _) -> expr
    // Only the base extent can hold a liftable inline form; the level list is data.
    | IROrbitClass (levels, n) -> IROrbitClass (levels, liftExpr builder n)
    | IRAssign (t, v) -> IRAssign (t, liftExpr builder v)
    | IRConstraintCheck (c, msg, sp) -> IRConstraintCheck (liftExpr builder c, msg, sp)
    | IRForRange (vid, lo, hi, body) ->
        IRForRange (vid, liftExpr builder lo, liftExpr builder hi, liftExpr builder body)
    | IRBlocked (it, bs) -> IRBlocked (it, liftExpr builder bs)

    // Loop forms: their auto-materialize handles top-level Arrays for
    // inline forms. We still descend into the kernels and any nested
    // expressions, AND lift any array-typed IRFieldAccess in Arrays so
    // codegen can find the companion `_extents` (auto-materialize doesn't
    // synthesize extents from struct field types).
    | IRMethodFor info ->
        let arrays' = info.Arrays |> List.map (liftExpr builder)
        let (binds, arraysFinal) =
            arrays' |> List.fold (fun (accB, accA) a ->
                let (peeled, inner) = peelLetChain a
                if isArrayFieldAccess inner || isNestedLoopComputeArg inner || isInlineArrayLitArg inner then
                    let id = builder.FreshId()
                    let ty = typeOf inner
                    (accB @ peeled @ [(id, ty, inner)], accA @ [IRVar (id, ty)])
                else
                    (accB @ peeled, accA @ [inner])) ([], [])
        wrapLets binds (IRMethodFor { info with Arrays = arraysFinal })
    | IRObjectFor info ->
        IRObjectFor { info with Kernel = liftExpr builder info.Kernel }
    | IRApplyCombinator info ->
        let loop' = liftExpr builder info.Loop
        let kernel' = liftExpr builder info.Kernel
        let arrays' = info.Arrays |> List.map (liftExpr builder)
        let (binds, arraysFinal) =
            arrays' |> List.fold (fun (accB, accA) a ->
                let (peeled, inner) = peelLetChain a
                if isArrayFieldAccess inner || isNestedLoopComputeArg inner || isInlineArrayLitArg inner then
                    let id = builder.FreshId()
                    let ty = typeOf inner
                    (accB @ peeled @ [(id, ty, inner)], accA @ [IRVar (id, ty)])
                else
                    (accB @ peeled, accA @ [inner])) ([], [])
        wrapLets binds (IRApplyCombinator { info with Loop = loop'; Kernel = kernel'; Arrays = arraysFinal })
    | IRComposeApply info ->
        // Same array-let lifting as IRApplyCombinator, applied to
        // InputArrays. No Kernel slot to lift (slot inversion: the
        // arrays *are* what would have gone in the kernel position).
        let composition' = liftExpr builder info.Composition
        let arrays' = info.InputArrays |> List.map (liftExpr builder)
        let (binds, arraysFinal) =
            arrays' |> List.fold (fun (accB, accA) a ->
                let (peeled, inner) = peelLetChain a
                if isArrayFieldAccess inner || isNestedLoopComputeArg inner || isInlineArrayLitArg inner then
                    let id = builder.FreshId()
                    let ty = typeOf inner
                    (accB @ peeled @ [(id, ty, inner)], accA @ [IRVar (id, ty)])
                else
                    (accB @ peeled, accA @ [inner])) ([], [])
        wrapLets binds (IRComposeApply { info with Composition = composition'; InputArrays = arraysFinal })

/// Lift inline forms across an entire IR module's bindings and functions.
let liftInlineFormsModule (modul: IRModule) (builder: IRBuilder) : IRModule =
    // Populate the struct fields cache so typeOf can resolve IRFieldAccess
    // result types. Required for hoisting array-typed field accesses to
    // let-RHS so codegen can synthesize their _extents companions.
    setStructFieldsCache modul.Types
    let liftedBindings =
        modul.Bindings |> List.map (fun b -> { b with Value = liftExpr builder b.Value })
    let liftedFunctions =
        modul.Functions |> List.map (fun f -> { f with Body = liftExpr builder f.Body })
    { modul with Bindings = liftedBindings; Functions = liftedFunctions }

/// Monomorphize all arity-polymorphic functions in an IR module.
/// Collects call sites, generates specialized versions, rewrites calls.
let monomorphizeModule (modul: IRModule) (builder: IRBuilder) : IRModule =
    // 1. Identify poly functions
    let polyFuncs =
        modul.Functions |> List.filter (fun f -> f.IsArityPoly)
    if polyFuncs.IsEmpty then modul  // Nothing to do
    else
    let polyFuncIds = polyFuncs |> List.map (fun f -> f.Id) |> Set.ofList
    let polyFuncMap = polyFuncs |> List.map (fun f -> (f.Id, f)) |> Map.ofList

    // 2. Collect call sites from the original module. Seed ONLY from concrete
    //    entry points -- NON-poly functions and top-level bindings. A poly
    //    function's own body reaches other poly functions (and itself) with the
    //    still-symbolic pack/tail as the argument (`f(rest)`, `comoment_prod(a)`),
    //    which computePolyArity would mis-read as an arity-1 call and mint a
    //    bogus spec. Those calls become CONCRETE -- the tail/pack spread into
    //    real per-element args -- only once the enclosing function is specialized,
    //    at which point the spec-body scan below (step 3) picks up the real
    //    arity. (A `| 2`-base recursion seeded from a poly call site would
    //    otherwise cascade into an invalid arity-0 spec.)
    let callSitesFromFuncs =
        modul.Functions
        |> List.filter (fun f -> not f.IsArityPoly)
        |> List.collect (fun f -> collectPolyCallSites polyFuncMap f.Body)
    let callSitesFromBindings =
        modul.Bindings |> List.collect (fun b -> collectPolyCallSites polyFuncMap b.Value)

    // 3. Generate specialized functions to a fixpoint. Specializing a
    //    function can introduce NEW call sites: a recursion over a shrinking
    //    pack (`f(tail)`) is rewritten into an arity-(n-1) call, so
    //    specializing f_arity_n demands f_arity_(n-1), down to the base arm
    //    `match arity` statically selects. The worklist scans each fresh
    //    spec's body and enqueues what it finds until nothing new appears.
    let funcMap = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let mutable specMap : Map<IRId * int list, IRFuncDef> = Map.empty
    let queue = System.Collections.Generic.Queue<IRId * int list>()
    for site in (callSitesFromFuncs @ callSitesFromBindings) |> List.distinct do
        queue.Enqueue site
    let mutable guard = 0
    let MAX_SPECS = 100000  // runaway backstop; real recursion depth = max pack arity
    while queue.Count > 0 && guard < MAX_SPECS do
        guard <- guard + 1
        let (funcId, arity) = queue.Dequeue()
        if not (Map.containsKey (funcId, arity) specMap) then
            let origFunc = polyFuncMap.[funcId]
            let spec = specializeFunction origFunc arity funcMap builder
            specMap <- Map.add (funcId, arity) spec specMap
            for site in collectPolyCallSites polyFuncMap spec.Body do
                if not (Map.containsKey site specMap) then queue.Enqueue site
    let specializations = specMap |> Map.toList

    // 4. Build rewrite function for call sites. The arity comes from
    //    computePolyArity (shape-aware: variadic vs tuple-as-pack), and the
    //    args are flattened at the Poly slot so they line up with the
    //    specialization's expanded param list.
    let rewriteCallSite e =
        match e with
        | IRApp (IRVar (funcId, fty), args, _) when polyFuncIds.Contains funcId ->
            let func = polyFuncMap.[funcId]
            match computePolyArity func args with
            | Some arity ->
                match Map.tryFind (funcId, arity) specMap with
                | Some spec ->
                    let flatArgs = flattenAtPolyPosition func args
                    IRApp (IRVar (spec.Id, fty), flatArgs, spec.RetType)
                | None -> e
            | None -> e
        | _ -> e

    // 5. Rewrite all expressions in module
    let newFunctions =
        modul.Functions
        |> List.filter (fun f -> not f.IsArityPoly)  // Remove original poly funcs
        |> List.map (fun f -> { f with Body = mapIRExpr rewriteCallSite f.Body })
    let newBindings =
        modul.Bindings
        |> List.map (fun b -> { b with Value = mapIRExpr rewriteCallSite b.Value })
    // Spec bodies carry the recursive/other poly calls (as arity-(n-1) variadic
    // applications of the ORIGINAL poly id); rewrite those to the concrete specs
    // too, or the recursion would reference a poly function that no longer exists.
    let specFuncs =
        specializations |> List.map (fun (_, spec) ->
            { spec with Body = mapIRExpr rewriteCallSite spec.Body })

    // Prune lifted kernel lambdas that captured a poly PACK and are now dead.
    // The pack-former unroller inlines such a lambda's body into the
    // specialized caller, leaving the original lambda unreferenced and
    // un-codegen-able (its body still subscripts a Poly-typed pack that no
    // longer exists as a scalar). Drop it once unreferenced. The gate is
    // narrow (synthesized "__lambda_" name, Poly-typed capture, unreferenced)
    // so no live function is ever removed.
    let allFuncs = newFunctions @ specFuncs
    let referencedIds =
        (allFuncs |> List.map (fun f -> f.Body))
        @ (newBindings |> List.map (fun b -> b.Value))
        |> List.map collectVarRefsIR
        |> Set.unionMany
    let capturesPolyPack (f: IRFuncDef) =
        f.Captures |> List.exists (fun c ->
            match c.Type with IRTPoly _ -> true | _ -> false)
    let prunedFuncs =
        allFuncs |> List.filter (fun f ->
            not (f.Name.StartsWith("__lambda_")
                 && capturesPolyPack f
                 && not (Set.contains f.Id referencedIds)))

    { modul with
        Functions = prunedFuncs
        Bindings = newBindings }

// Shape monomorphization (docs/plan-cpp-perf-exploitation.md)
//
// A function declared over a SYMBOLIC extent (`f(A: Array<Float64 like Idx<n>>)`)
// carries `IRParam ("n", 0, IRTNat None)` as its index records' `Extent` -- a
// cosmetic placeholder that nothing downstream turns into a number (unify
// never compares extents; HM substitution can't carry them). So
// `genLoopBoundExpr` falls to `<arr>.extents[d]` in every such function, and
// the flat elementwise mode (which needs a literal extent for its compile-
// time cell count) can never fire there.
//
// This pass closes that gap the way `monomorphizeModule` closes the arity
// gap: collect call sites, key a spec map by (funcId, extent signature), and
// emit one specialized copy per unique signature with the placeholders
// rewritten to `IRLit`. The generic copy stays for calls that don't pin a
// literal.
//
// It does NOT change the runtime ABI: `Array<T,R>` still carries
// `const size_t* extents`, and every `.extents[d]` expression left alone
// still yields the right number (the literal came from the ARGUMENT's own
// type), so the rewrite is confined to `IRIndexTypeG.Extent` fields and
// never touches body expressions.
//
// Explosion control: dedupe by signature, cap at SHAPE_SPEC_CAP copies per
// function, and decline recursive/mutually recursive functions outright --
// bounded by real call-site diversity and capped in the compiler.

/// Most specialized copies any one function may earn. Past this, further call
/// sites keep the generic copy -- declines are counted and surfaced under
/// BLADE_DEBUG_SHAPE_SPEC, never as a user diagnostic (a missed optimization
/// is not a program defect).
let private SHAPE_SPEC_CAP = 4

/// `BLADE_DEBUG_SHAPE_SPEC=1` prints the per-module specialize/cap/decline
/// census. Orchestration/diagnostic aid only; silent by default.
let private shapeSpecDebug () =
    match System.Environment.GetEnvironmentVariable("BLADE_DEBUG_SHAPE_SPEC") with
    | null | "" | "0" -> false
    | _ -> true

/// The bakeable symbolic extent: the cosmetic placeholder `lowerExtentExpr`
/// emits for `Idx<n>`. `"?"` is that function's give-up marker (an extent
/// expression it could not lower) and names nothing, so it never bakes.
let private (|ShapeSymbolicExtent|_|) (e: IRExpr) =
    match e with
    | IRParam (name, _, _) when name <> "?" && name <> "" -> Some name
    | _ -> None

let private (|ShapeLiteralExtent|_|) (e: IRExpr) =
    match e with
    | IRLit (IRLitInt n) when n > 0L -> Some n
    | _ -> None

/// Rewrite an EXTENT expression under a name->literal substitution.
/// Deliberately narrow: the placeholder itself, arithmetic built over it
/// (dependent extents like `n - i` stay correct), and an orbit class's base
/// extent. Everything else -- compound masks, sparse key sources, ragged
/// lookups, `extents(A)` reads, opaque extents -- is a RUNTIME value that
/// happens to sit in an Extent slot and must be left alone.
let rec private shapeRewriteExtent (subst: Map<string, int64>) (e: IRExpr) : IRExpr =
    match e with
    | IRParam (name, _, _) when subst.ContainsKey name -> IRLit (IRLitInt subst.[name])
    | IRBinOp (mode, op, l, r) ->
        IRBinOp (mode, op, shapeRewriteExtent subst l, shapeRewriteExtent subst r)
    | IRUnaryOp (op, x) -> IRUnaryOp (op, shapeRewriteExtent subst x)
    | IROrbitClass (levels, n) -> IROrbitClass (levels, shapeRewriteExtent subst n)
    | _ -> e

let private shapeRewriteIx (subst: Map<string, int64>) (ix: IRIndexType) : IRIndexType =
    { ix with Extent = shapeRewriteExtent subst ix.Extent }

/// Rewrite every index record reachable from a type. Structural mirror of
/// `substTypeInIRType`, but over the Extent axis instead of the IRTInfer axis
/// -- and it must descend into the arrow SLOTS, which the HM substituter
/// deliberately skips (`SIdx idx -> SIdx idx`) precisely because extents were
/// out of its scope.
let rec private shapeRewriteType (subst: Map<string, int64>) (ty: IRType) : IRType =
    match ty with
    | IRTArrow (slots, result, identity) ->
        let slots' =
            slots |> List.map (function
                | SIdx ix -> SIdx (shapeRewriteIx subst ix)
                | SIdxVirt ix -> SIdxVirt (shapeRewriteIx subst ix)
                | SVal t -> SVal (shapeRewriteType subst t))
        IRTArrow (slots', shapeRewriteType subst result, identity)
    | IRTTuple ts -> IRTTuple (ts |> List.map (shapeRewriteType subst))
    | IRTComputation t -> IRTComputation (shapeRewriteType subst t)
    | IRTPoly (b, v) -> IRTPoly (shapeRewriteType subst b, v)
    | IRTUnitAnnotated (t, u) -> IRTUnitAnnotated (shapeRewriteType subst t, u)
    | IRTIdxTagged (t, tag) ->
        // IRefAnon's extent is diagnostics-only (never part of tag identity),
        // but keeping it in step avoids printing `Idx<n>` beside a baked bound.
        let tag' =
            match tag with
            | IRefAnon (nid, ext) -> IRefAnon (nid, shapeRewriteExtent subst ext)
            | other -> other
        IRTIdxTagged (shapeRewriteType subst t, tag')
    | IRTDist (order, elem, axes) ->
        IRTDist (order, shapeRewriteType subst elem, axes |> List.map (shapeRewriteIx subst))
    | IRTGroupKeys (outerIdx, sourceIdx, ev) ->
        IRTGroupKeys (shapeRewriteIx subst outerIdx, shapeRewriteIx subst sourceIdx, ev)
    | IRTLoop lt ->
        IRTLoop { lt with
                    ArrayTypes = lt.ArrayTypes |> List.map (shapeRewriteType subst)
                    KernelType = lt.KernelType |> Option.map (shapeRewriteType subst) }
    | _ -> ty

let private shapeRewriteArrayType (subst: Map<string, int64>) (aty: IRArrayType) : IRArrayType =
    { aty with
        ElemType = shapeRewriteType subst aty.ElemType
        IndexTypes = aty.IndexTypes |> List.map (shapeRewriteIx subst) }

/// Rewrite every type-bearing field of every node in an expression tree.
/// `mapIRExpr` supplies the traversal; this callback enumerates the positions
/// that actually carry index records -- IRIndexType is OPAQUE to ExprShape,
/// so the records on IRRange/IRVirtualReverse/IRBlocked and the combinator
/// info records must be named here or they are never reached.
let private shapeRewriteExpr (subst: Map<string, int64>) (expr: IRExpr) : IRExpr =
    if Map.isEmpty subst then expr else
    let rt = shapeRewriteType subst
    let rix = shapeRewriteIx subst
    let rat = shapeRewriteArrayType subst
    mapIRExpr (fun e ->
        match e with
        | IRVar (id, ty) -> IRVar (id, rt ty)
        // NOTE: only the node's TYPE. An IRParam in expression position is a
        // parameter reference; the extent PLACEHOLDER of the same shape only
        // ever lives inside an index record, which this walk reaches through
        // the type positions instead.
        | IRParam (n, i, ty) -> IRParam (n, i, rt ty)
        | IRApp (f, args, retTy) -> IRApp (f, args, rt retTy)
        | IRArrayLit (es, aty) -> IRArrayLit (es, rat aty)
        | IRRange (ixs, off) -> IRRange (ixs |> List.map rix, off)
        | IRVirtualReverse ix -> IRVirtualReverse (rix ix)
        | IRBlocked (ix, bs) -> IRBlocked (rix ix, bs)
        | IRMethodFor info ->
            IRMethodFor { info with
                            ArrayTypes = info.ArrayTypes |> List.map rat
                            SharedIndexTypes = info.SharedIndexTypes |> List.map rix }
        | IRApplyCombinator info ->
            IRApplyCombinator { info with
                                  ArrayTypes = info.ArrayTypes |> List.map rat
                                  SharedIndexTypes = info.SharedIndexTypes |> List.map rix
                                  KernelTDims = info.KernelTDims |> List.map rix
                                  OutputType = rt info.OutputType }
        | IRComposeApply info -> IRComposeApply { info with OutputType = rt info.OutputType }
        | _ -> e) expr

/// Every symbolic extent NAME a parameter list mentions, with its occurrence
/// count. A name bakes only when EVERY one of its occurrences was pinned to
/// the same literal by the call site -- see `shapeSignatureAt`.
let private shapeSymbolicOccurrences (paramTys: IRType list) : Map<string, int> =
    let acc = System.Collections.Generic.Dictionary<string, int>()
    let bump name =
        acc.[name] <- (match acc.TryGetValue name with | true, v -> v | _ -> 0) + 1
    let rec goIx (ix: IRIndexType) =
        match ix.Extent with
        | ShapeSymbolicExtent name -> bump name
        | _ -> ()
    and goTy (ty: IRType) =
        match ty with
        | IRTArrow (slots, result, _) ->
            slots |> List.iter (function
                | SIdx ix | SIdxVirt ix -> goIx ix
                | SVal t -> goTy t)
            goTy result
        | IRTTuple ts -> ts |> List.iter goTy
        | IRTComputation t | IRTPoly (t, _) | IRTUnitAnnotated (t, _) | IRTIdxTagged (t, _) -> goTy t
        | IRTDist (_, elem, axes) -> goTy elem; axes |> List.iter goIx
        | IRTGroupKeys (a, b, _) -> goIx a; goIx b
        | IRTLoop lt -> lt.ArrayTypes |> List.iter goTy; lt.KernelType |> Option.iter goTy
        | _ -> ()
    paramTys |> List.iter goTy
    acc |> Seq.map (fun kv -> (kv.Key, kv.Value)) |> Map.ofSeq

/// Walk a (parameter type, argument type) pair in lockstep, recording for each
/// symbolic parameter extent the literal the argument pins it to. Positions
/// that fail to line up structurally simply record nothing, which -- because a
/// name bakes only at FULL occurrence coverage -- makes them a decline rather
/// than a guess.
let private shapeObservations (paramTy: IRType) (argTy: IRType) : (string * int64) list =
    let acc = System.Collections.Generic.List<string * int64>()
    let obsIx (p: IRIndexType) (a: IRIndexType) =
        match p.Extent with
        | ShapeSymbolicExtent name ->
            // The two records must describe the same KIND of axis before the
            // argument's number can be believed as this axis' bound.
            if p.Rank = a.Rank && p.Symmetry = a.Symmetry && p.IxKind = a.IxKind then
                match a.Extent with
                | ShapeLiteralExtent n -> acc.Add((name, n))
                | _ -> ()
        | _ -> ()
    let rec go (p: IRType) (a: IRType) =
        match p, a with
        | IRTArrow (ps, pr, _), IRTArrow (as_, ar, _) when ps.Length = as_.Length ->
            List.iter2 (fun pslot aslot ->
                match pslot, aslot with
                | SIdx pi, SIdx ai | SIdxVirt pi, SIdxVirt ai
                | SIdx pi, SIdxVirt ai | SIdxVirt pi, SIdx ai -> obsIx pi ai
                | SVal pt, SVal at -> go pt at
                | _ -> ()) ps as_
            go pr ar
        | IRTTuple pts, IRTTuple ats when pts.Length = ats.Length -> List.iter2 go pts ats
        | IRTComputation pt, IRTComputation at
        | IRTPoly (pt, _), IRTPoly (at, _)
        | IRTUnitAnnotated (pt, _), IRTUnitAnnotated (at, _)
        | IRTIdxTagged (pt, _), IRTIdxTagged (at, _) -> go pt at
        // A unit/tag wrapper on one side only: unwrap and keep pairing.
        | IRTUnitAnnotated (pt, _), _ -> go pt a
        | _, IRTUnitAnnotated (at, _) -> go p at
        | IRTIdxTagged (pt, _), _ -> go pt a
        | _, IRTIdxTagged (at, _) -> go p at
        | _ -> ()
    go paramTy argTy
    acc |> List.ofSeq

/// The extent signature a call site pins on a callee: the sorted
/// (name, literal) list that keys the spec map. A name is admitted only when
/// the arguments pinned EVERY occurrence of it in the parameter list, all to
/// the SAME literal. Both halves matter: full coverage rules out
/// `f(a: Idx<n>, b: Idx<n>)` called with a literal `a` and runtime `b` (baking
/// `n` from `a` would install a wrong bound on `b`'s loop); agreement rules
/// out the same call with a 3-array and a 5-array (unify never compares
/// extents, so this typechecks today).
let private shapeSignatureAt (func: IRFuncDef) (args: IRExpr list) : (string * int64) list =
    if args.Length <> func.Params.Length then [] else
    let paramTys = func.Params |> List.map (fun p -> p.Type)
    let occ = shapeSymbolicOccurrences paramTys
    if Map.isEmpty occ then [] else
    let obs =
        List.zip paramTys args
        |> List.collect (fun (pty, arg) ->
            match exprTypeIfKnown arg with
            | Some aty -> shapeObservations pty aty
            | None -> [])
    obs
    |> List.groupBy fst
    |> List.choose (fun (name, pairs) ->
        let lits = pairs |> List.map snd
        match Map.tryFind name occ with
        | Some k when lits.Length = k && (lits |> List.distinct |> List.length) = 1 ->
            Some (name, List.head lits)
        | _ -> None)
    |> List.sortBy fst

/// Would a specialized copy actually pay? Only if the body iterates: the
/// baked literal reaches the emitted C++ exclusively through loop bounds and
/// through the flat mode's compile-time cell count. A function that merely
/// forwards or reads `extents(A)` gets nothing from a copy, so it does not
/// get one.
let private shapeSpecWorthwhile (func: IRFuncDef) : bool =
    let mutable found = false
    mapIRExpr (fun e ->
        (match e with
         | IRApplyCombinator _ | IRComposeApply _ | IRMethodFor _
         | IRReduce _ | IRReduceCompute _ | IRProdSum _ | IRForRange _
         | IRGram _ | IRMatmul _ | IRArrayProduct _ | IRArrayNegate _ | IRArrayConjugate _
         | IRReynolds _ | IRDecompact _ | IRTranspose _ -> found <- true
         | _ -> ())
        e) func.Body |> ignore
    found

/// Function ids that can reach themselves through the module's static call
/// graph (direct self-recursion or a mutual cycle). This pass declines to
/// specialize these: a spec body's recursive call would name the ORIGINAL id, and
/// rewriting it to the spec is only sound when the recursive call pins the
/// same signature -- an analysis this pass does not do.
let private shapeRecursiveIds (funcs: IRFuncDef list) : Set<IRId> =
    let ids = funcs |> List.map (fun f -> f.Id) |> Set.ofList
    let direct =
        funcs
        |> List.map (fun f -> (f.Id, Set.intersect ids (collectVarRefsIR f.Body)))
        |> Map.ofList
    let mutable reach = direct
    let mutable changed = true
    let mutable guard = 0
    while changed && guard < 64 do
        changed <- false
        guard <- guard + 1
        let next =
            reach |> Map.map (fun _ vs ->
                vs |> Set.fold (fun acc v ->
                    match Map.tryFind v reach with
                    | Some vs2 -> Set.union acc vs2
                    | None -> acc) vs)
        if next <> reach then
            changed <- true
            reach <- next
    reach |> Map.toList |> List.choose (fun (k, vs) -> if Set.contains k vs then Some k else None) |> Set.ofList

/// One planned specialization: the callee it copies, the name->literal map the
/// copy bakes, and the fresh id/name the copy will carry.
type private ShapeSpec = {
    Orig: IRFuncDef
    Subst: Map<string, int64>
    SpecId: IRId
    SpecName: string
}

/// Give every symbolic-extent function a literal-extent copy per distinct
/// call-site shape. Runs after arity and HM monomorphization (both can
/// create the call sites this reads) and before codegen.
let shapeMonomorphizeModule (modul: IRModule) (builder: IRBuilder) : IRModule =
    let debug = shapeSpecDebug ()
    let recursiveIds = shapeRecursiveIds modul.Functions
    let candidates =
        modul.Functions
        |> List.filter (fun f ->
            not f.IsArityPoly
            && not (Set.contains f.Id recursiveIds)
            && not (Map.isEmpty (shapeSymbolicOccurrences (f.Params |> List.map (fun p -> p.Type))))
            && shapeSpecWorthwhile f)
    if candidates.IsEmpty then modul else
    let candMap = candidates |> List.map (fun f -> (f.Id, f)) |> Map.ofList

    // Planned specs, keyed exactly as monomorphizeModule's specMap is:
    // (callee id, the signature that distinguishes this copy).
    let mutable specMap : Map<IRId * (string * int64) list, ShapeSpec> = Map.empty
    // Distinct signatures turned away by the cap (a set, not a tally: the
    // fixpoint re-visits the same declined site every round).
    let mutable capDeclines : Set<IRId * (string * int64) list> = Set.empty

    /// Rewrite one call site against the CURRENT spec map. Pure, so the
    /// scan below can apply it to a throwaway copy of every body and read
    /// the cascade (an inner call's baked return type is an outer call's
    /// literal argument extent) without committing to anything.
    let rewriteCallSites (expr: IRExpr) : IRExpr =
        mapIRExpr (fun e ->
            match e with
            | IRApp (IRVar (fid, fty), args, retTy) when candMap.ContainsKey fid ->
                let sign = shapeSignatureAt candMap.[fid] args
                if List.isEmpty sign then e
                else
                    match Map.tryFind (fid, sign) specMap with
                    | Some spec ->
                        let subst = spec.Subst
                        IRApp (IRVar (spec.SpecId, shapeRewriteType subst fty),
                               args,
                               shapeRewriteType subst retTy)
                    | None -> e
            | _ -> e) expr

    let specBody (s: ShapeSpec) : IRExpr = shapeRewriteExpr s.Subst s.Orig.Body

    // Fixpoint: each round rewrites every body (originals, bindings, and the
    // specs planned so far) with the current map, then harvests the call sites
    // exposed. A new spec can expose more (its body's own calls now carry
    // literal argument extents), so the round repeats until nothing changes.
    // `rounds < 8` is a runaway backstop; real convergence is 2. Anything past
    // the backstop simply keeps the generic copy.
    let mutable changed = true
    let mutable rounds = 0
    while changed && rounds < 8 do
        changed <- false
        rounds <- rounds + 1
        let bodies =
            (modul.Functions |> List.map (fun f -> f.Body))
            @ (modul.Bindings |> List.map (fun b -> b.Value))
            @ (specMap |> Map.toList |> List.map (snd >> specBody))
        let sites =
            bodies
            |> List.collect (fun b ->
                let mutable found = []
                mapIRExpr (fun e ->
                    (match e with
                     | IRApp (IRVar (fid, _), args, _) when candMap.ContainsKey fid ->
                         let sign = shapeSignatureAt candMap.[fid] args
                         if not (List.isEmpty sign) then found <- (fid, sign) :: found
                     | _ -> ())
                    e) (rewriteCallSites b) |> ignore
                found)
            |> List.distinct
        for (fid, sign) in sites do
            if not (Map.containsKey (fid, sign) specMap) then
                let existing = specMap |> Map.filter (fun (k, _) _ -> k = fid) |> Map.count
                if existing >= SHAPE_SPEC_CAP then
                    capDeclines <- Set.add (fid, sign) capDeclines
                else
                    let orig = candMap.[fid]
                    let specId = builder.FreshId()
                    let suffix = sign |> List.map (fun (n, v) -> sprintf "_%s%d" n v) |> String.concat ""
                    specMap <- Map.add (fid, sign)
                                       { Orig = orig
                                         Subst = Map.ofList sign
                                         SpecId = specId
                                         SpecName = sprintf "%s_shape%s" orig.Name suffix }
                                       specMap
                    changed <- true

    if Map.isEmpty specMap then
        (if debug then
            eprintfn "[shape-spec] %s: %d candidate(s), 0 specialized" modul.Name candidates.Length)
        modul
    else

    // Materialize the copies. Param VarIds are deliberately NOT freshened
    // (unlike the HM specializer, which freshens and then must clone every
    // lifted lambda that captured a param to repair Captures.Id): the copy is
    // type-identical to the original at every VALUE position -- only Extent
    // fields differ -- so sharing VarIds keeps any lifted lambda the body
    // references bound to its original parameters. The lambda's own index
    // records keep their symbolic extents, costing the optimization inside
    // the lambda and nothing else (an unbaked extent still emits the correct
    // runtime read).
    let materialize (s: ShapeSpec) : IRFuncDef =
        { s.Orig with
            Id = s.SpecId
            Name = s.SpecName
            Params = s.Orig.Params |> List.map (fun p -> { p with Type = shapeRewriteType s.Subst p.Type })
            RetType = shapeRewriteType s.Subst s.Orig.RetType
            Captures = s.Orig.Captures |> List.map (fun c -> { c with Type = shapeRewriteType s.Subst c.Type })
            Body = rewriteCallSites (specBody s) }

    // PLACEMENT IS PART OF CORRECTNESS, not cosmetics. Codegen interleaves
    // bindings and functions in IRId order; every function
    // `computeMainLocalFuncIds` classifies as main-local is a `std::function`
    // LOCAL inside main() with no forward declaration, so a copy carrying a
    // fresh (largest) id sorts AFTER the call sites this pass just rewrote to
    // it -- a scope error. The copy is placed immediately after its origin
    // here, keyed by the ORIGIN's id (IRModule.DerivedFuncOrigins), so it
    // lands at exactly its origin's program point and is visible to precisely
    // the call sites the origin was.
    let specsByOrigin =
        specMap |> Map.toList |> List.map snd |> List.groupBy (fun s -> s.Orig.Id) |> Map.ofList
    let newFunctions =
        modul.Functions
        |> List.collect (fun f ->
            let f' = { f with Body = rewriteCallSites f.Body }
            match Map.tryFind f.Id specsByOrigin with
            | Some specs -> f' :: (specs |> List.map materialize)
            | None -> [f'])
    let newBindings = modul.Bindings |> List.map (fun b -> { b with Value = rewriteCallSites b.Value })
    let derivedOrigins =
        specMap
        |> Map.toList
        |> List.fold (fun acc (_, s) -> Map.add s.SpecId s.Orig.Id acc) modul.DerivedFuncOrigins

    if debug then
        let perFunc =
            specMap |> Map.toList |> List.map (fun ((fid, _), s) -> (fid, s.Orig.Name))
            |> List.groupBy id |> List.map (fun ((_, n), g) -> sprintf "%s x%d" n g.Length)
        eprintfn "[shape-spec] %s: %d candidate(s), %d spec(s) [%s], %d cap-decline(s), %d recursive decline(s), %d round(s)"
                 modul.Name candidates.Length specMap.Count (String.concat "; " perFunc)
                 (Set.count capDeclines) (Set.count recursiveIds) rounds

    { modul with
        Functions = newFunctions
        Bindings = newBindings
        DerivedFuncOrigins = derivedOrigins }

// Pretty Printing

let rec ppIRType = function
    | IRTScalar ETInt32 -> "Int32"
    | IRTScalar ETInt64 -> "Int64"
    | IRTScalar ETFloat32 -> "Float32"
    | IRTScalar ETFloat64 -> "Float64"
    | IRTScalar ETComplex64 -> "Complex64"
    | IRTScalar ETComplex128 -> "Complex128"
    | IRTScalar ETBool -> "Bool"
    | IRTScalar ETUnit -> "Void"
    | IRTScalar ETString -> "String"
    | IRTTuple ts ->
        sprintf "(%s)" (ts |> List.map ppIRType |> String.concat ", ")
    | IRTLoop lt ->
        match lt.Kind with
        | LKMethod -> sprintf "MethodLoop<%d>" (lt.Arity |> Option.defaultValue 0)
        | LKObject -> sprintf "ObjectLoop<%d>" (lt.Arity |> Option.defaultValue 0)
    | IRTComputation t -> sprintf "Computation<%s>" (ppIRType t)
    | IRTUnit -> "Void"
    | IRTPoly (base', var) -> sprintf "Poly<%s, %s>" (ppIRType base') var
    | IRTNat (Some n) -> sprintf "Nat<%d>" n
    | IRTNat None -> "Nat<?>"
    | IRTIdxTagged (inner, idxRef) ->
        // Conventional form: when the inner is the typical int64 backing,
        // render compactly as "Nat<I>" (parallel to "Float<meters>"); for
        // other inner types, show both ("(inner)<I>") to surface the
        // wrapper shape.
        let tagStr =
            match idxRef with
            | IRefNamed name -> name
            | IRefAnon (id, extent) ->
                let extentStr =
                    match extent with
                    | IRLit (IRLitInt n) -> sprintf "%d" n
                    | IRParam (name, _, _) -> name
                    | IRVar (vid, _) -> sprintf "v%d" vid
                    | _ -> "?"
                sprintf "Idx<%s>#%d" extentStr id
            | IRefAny -> "_"
        match inner with
        | IRTScalar ETInt64 | IRTScalar ETInt32 -> sprintf "Nat<%s>" tagStr
        | other -> sprintf "(%s)<%s>" (ppIRType other) tagStr
    | IRTDist (order, elem, axes) ->
        let axesStr = axes |> List.map ppIndexType |> String.concat ", "
        sprintf "Dist<%d, %s like %s>" order (ppIRType elem) axesStr
    | IRTNamed name -> name  // Named types print as themselves
    | IRTInfer id -> sprintf "T?%d" id
    | IRTUnitAnnotated (inner, units) -> sprintf "%s<%s>" (ppIRType inner) (ppUnitSig units)
    | IRTGroupKeys (outerIdx, sourceIdx, _) -> sprintf "GroupKeys<%s, %s>" (ppIndexType outerIdx) (ppIndexType sourceIdx)
    | IRTArrow (slots, result, identity) ->
        // Renders the unified arrow form. For array-shaped arrows (all-SIdx
        // or all-SIdxVirt with non-empty slots), use the user-friendly
        // "Array<elem like indices>" rendering, which keeps error messages
        // recognizable. Other shapes (functions, mixed slots) get the
        // canonical "Arrow<...>" form.
        let isAllStored = not slots.IsEmpty && slots |> List.forall (function SIdx _ -> true | _ -> false)
        let isAllVirtual = not slots.IsEmpty && slots |> List.forall (function SIdxVirt _ -> true | _ -> false)
        if isAllStored || isAllVirtual then
            let indices =
                slots |> List.map (function
                    | SIdx i | SIdxVirt i -> ppIndexType i
                    | _ -> failwith "unreachable")
                |> String.concat ", "
            sprintf "Array<%s like %s>" (ppIRType result) indices
        else
            let slotStr =
                slots |> List.map (function
                    | SIdx idx -> sprintf "Idx<%s>" (ppIndexType idx)
                    | SIdxVirt idx -> sprintf "VirtIdx<%s>" (ppIndexType idx)
                    | SVal ty -> ppIRType ty)
                |> String.concat ", "
            let idStr =
                match identity with
                | Some _ -> " [id]"
                | None -> ""
            sprintf "Arrow<%s -> %s>%s" slotStr (ppIRType result) idStr

and ppIndexType (idx: IRIndexType) =
    // Inline extent printing since ppIRExpr is defined later
    let extentStr =
        match idx.Extent with
        | IRLit (IRLitInt n) -> sprintf "%d" n
        | IRVar (id, _) -> sprintf "v%d" id
        | IRParam (name, _, _) -> name
        | _ -> "?"
    match idx with
    | IrrepsIdxLike rendered -> ppIrrepsPower idx rendered
    | PgIrrepsIdxLike rendered -> ppIrrepsPower idx rendered
    | _ ->
        match idx.Symmetry with
        | SymNone -> sprintf "Idx<%s>" extentStr
        | SymSymmetric -> sprintf "SymIdx<%d, %s>" idx.Rank extentStr
        | SymAntisymmetric -> sprintf "AntisymIdx<%d, %s>" idx.Rank extentStr
        | SymHermitian -> sprintf "HermitianIdx<%s>" extentStr
        // Round-trippable surface spelling: the level list IS the type, so a
        // diagnostic that showed only the rank would name a different class.
        | SymWreath -> sprintf "OrbIdx<%s, %s>" (ppOrbitLevels (orbitLevelsOf idx)) (ppExtentOf (orbitBaseExtent idx))

/// The extent-slot rendering shared by both index printers: the small set of
/// extent shapes a diagnostic can name, "?" for everything else. Factored out
/// because a wreath record's extent lives one level down (inside the
/// IROrbitClass marker) and both printers have to reach it the same way.
and ppExtentOf (e: IRExpr) =
    match e with
    | IRLit (IRLitInt n) -> sprintf "%d" n
    | IRVar (id, _) -> sprintf "v%d" id
    | IRParam (name, _, _) -> name
    | _ -> "?"

/// Render an irreps-identity record whose Symmetry/Rank make it a symmetric
/// POWER of that irreps space (`SymIdx<k, IrrepsIdx<s>>` -- what
/// deduceOutputType infers for a comm group over irreps-typed inputs). A
/// plain rank-1 irreps index prints as its own base form. Shared by both
/// index printers, and by both BLOCK-SPEC members (IrrepsIdxLike and
/// PgIrrepsIdxLike), since the power wrapper says nothing about which
/// member the base belongs to.
and ppIrrepsPower (idx: IRIndexType) (renderedBase: string) =
    match idx.Symmetry with
    | SymSymmetric -> sprintf "SymIdx<%d, %s>" idx.Rank renderedBase
    | SymAntisymmetric -> sprintf "AntisymIdx<%d, %s>" idx.Rank renderedBase
    | SymHermitian -> sprintf "HermitianIdx<%s>" renderedBase
    // No surface spelling takes a block-spec base under a wreath class (the
    // OrbIdx grammar's second argument is a SymIdxBase, so `OrbIdx<[...],
    // IrrepsIdx<s>>` parses, but nothing lowers a block-spec base into a
    // SymWreath record today). Render both halves rather than drop one.
    | SymWreath -> sprintf "OrbIdx<%s, %s>" (ppOrbitLevels (orbitLevelsOf idx)) renderedBase
    | SymNone -> renderedBase

/// Build a map from IRIndexType.Id -> type name from a module's IRTDIndexType defs
let indexNameMap (modul: IRModule) : Map<IRId, string> =
    modul.Types
    |> List.choose (function
        | IRTDIndexType (name, idx) -> Some (idx.Id, name)
        | IRTDEnumIdx (name, idx, _) -> Some (idx.Id, name)
        | _ -> None)
    |> Map.ofList

/// Context-aware pretty-printers that resolve named index types
let rec ppIRTypeIn (names: Map<IRId, string>) = function
    | ArrayElem arr ->
        let indices = arr.IndexTypes |> List.map (ppIndexTypeIn names) |> String.concat ", "
        // `like`, not a comma: this printer feeds the REPL's type echo and the
        // IDE tooltips, where the string is read AS SOURCE. `Array<T, I>` is
        // not the array spelling in any position -- it does not parse.
        sprintf "Array<%s like %s>" (ppIRTypeIn names arr.ElemType) indices
    | other -> ppIRType other

and ppIndexTypeIn (names: Map<IRId, string>) (idx: IRIndexType) =
    let nominal = Map.tryFind idx.Id names
    let extentStr =
        match nominal with
        | Some name -> name
        // A wreath record's extent is one level down, inside the IROrbitClass
        // marker; `orbitBaseExtent` is the identity on every other record, so
        // this one call covers both.
        | None -> ppExtentOf (orbitBaseExtent idx)
    match idx with
    | IrrepsIdxLike rendered -> ppIrrepsPower idx rendered
    | PgIrrepsIdxLike rendered -> ppIrrepsPower idx rendered
    | _ ->
        match idx.Symmetry with
        // A plain alias keeps the documented `Idx<Lat>` form: that type's one
        // slot IS the extent, and the alias stands for exactly that extent.
        | SymNone -> sprintf "Idx<%s>" extentStr
        // An alias of a COMPACT class names the WHOLE class, whose argument
        // slots are (rank, extent) -- slots a name does not fill. Routing it
        // through the extent slot produced `SymIdx<2, MySym>`, which reads as
        // "extent = MySym" and does not parse. The bare name IS the surface
        // spelling of this type (`Array<Int32 like MySym>`), so print that.
        | _ when nominal.IsSome -> nominal.Value
        | SymSymmetric -> sprintf "SymIdx<%d, %s>" idx.Rank extentStr
        | SymAntisymmetric -> sprintf "AntisymIdx<%d, %s>" idx.Rank extentStr
        | SymHermitian -> sprintf "HermitianIdx<%s>" extentStr
        | SymWreath -> sprintf "OrbIdx<%s, %s>" (ppOrbitLevels (orbitLevelsOf idx)) extentStr

let ppSymcomState = function
    | SCNeither -> "Neither"
    | SCSymmetric -> "Symmetric"
    | SCCommutative -> "Commutative"
    | SCBoth -> "Both"

let ppBinOp = function
    | IRAdd -> "+"
    | IRSub -> "-"
    | IRMul -> "*"
    | IRDiv -> "/"
    | IRMod -> "%"
    | IRCaret -> "^"
    | IREq -> "=="
    | IRNeq -> "!="
    | IRLt -> "<"
    | IRLe -> "<="
    | IRGt -> ">"
    | IRGe -> ">="
    | IRAnd -> "&&"
    | IROr -> "||"

let ppBinOpWithMode mode op =
    let opStr = ppBinOp op
    match mode with
    | IRElementwise -> opStr
    | IROuter -> sprintf "[%s]" opStr

let ppUnaryOp = function
    | IRNeg -> "-"
    | IRNot -> "!"
    | IRConj -> "conj"
    | IRReal -> "real"
    | IRImag -> "imag"
    | IRArg -> "arg"
    | IRMath name -> name

/// Pretty print IR expressions with optional name mapping for variables
let rec ppIRExprWithNames (names: Map<int, string>) indent (expr: IRExpr) =
    let pp = ppIRExprWithNames names 0
    let ind = String.replicate indent "  "
    match expr with
    | IRLit (IRLitInt n) -> sprintf "%d" n
    | IRLit (IRLitFloat f) -> sprintf "%f" f
    | IRLit (IRLitBool b) -> if b then "true" else "false"
    | IRLit (IRLitString s) -> sprintf "\"%s\"" s
    | IRLit IRLitUnit -> "()"
    | IRVar (id, _) -> 
        match Map.tryFind id names with
        | Some name -> name
        | None -> sprintf "v%d" id
    | IRParam (name, _, _) -> name
    | IRBinOp (mode, op, a, b) ->
        sprintf "(%s %s %s)" (pp a) (ppBinOpWithMode mode op) (pp b)
    | IRUnaryOp (op, a) ->
        sprintf "(%s%s)" (ppUnaryOp op) (pp a)
    | IRTuple es ->
        sprintf "(%s)" (es |> List.map pp |> String.concat ", ")
    | IRComplex (re, im) ->
        sprintf "complex(%s, %s)" (pp re) (pp im)
    | IRTupleProj (e, i, _) ->
        sprintf "%s.%d" (pp e) i
    | IRIf (c, t, e) ->
        sprintf "if %s then %s else %s" (pp c) (pp t) (pp e)
    | IRLet (id, v, b) ->
        // Add the let-bound name to mapping for body
        let names' = Map.add id (sprintf "v%d" id) names
        sprintf "let v%d = %s in\n%s%s" id (pp v) ind (ppIRExprWithNames names' indent b)
    | IRMethodFor info ->
        let arrs = info.Arrays |> List.map pp |> String.concat ", "
        let sdims = info.SDimsPerArray |> List.map string |> String.concat "," 
        sprintf "method_for(%s) [sdims=[%s], total=%d]" arrs sdims info.TotalSDims
    | IRObjectFor info ->
        let iranks = info.InputRanks |> List.map string |> String.concat ","
        sprintf "object_for(%s) [comm=%A, iranks=[%s], orank=%d]" 
            (pp info.Kernel) info.CommGroups iranks info.OutputRank
    | IRApplyCombinator info ->
        let states = info.SymcomStates |> List.map ppSymcomState |> String.concat ", "
        let triLevels = info.TriangularLevels |> List.map string |> String.concat ","
        let reynoldsStr = if info.HasReynolds then sprintf ", reynolds=%d perms" info.ReynoldsSpeedup else ""
        let outputStr = 
            match info.OutputType with
            | IRTUnit -> ""
            | t -> sprintf ", out=%s" (ppIRType t)
        sprintf "(%s <@> %s) [states=%s, tri=[%s], speedup=%dx%s%s]" 
            (pp info.Loop) (pp info.Kernel) states triLevels info.SpeedupFactor reynoldsStr outputStr
    | IRComposeApply info ->
        let arrs = info.InputArrays |> List.map pp |> String.concat ", "
        let outputStr = 
            match info.OutputType with
            | IRTUnit -> ""
            | t -> sprintf ", out=%s" (ppIRType t)
        sprintf "(%s <@> [%s]) [compose-apply%s]" (pp info.Composition) arrs outputStr
    | IRCompute c ->
        sprintf "(%s |> compute)" (pp c)
    | IRReynolds (k, isAntisym) ->
        let symStr = if isAntisym then ", Antisymmetric" else ""
        sprintf "reynolds(%s%s)" (pp k) symStr
    | IRPure e ->
        sprintf "pure(%s)" (pp e)
    | IRParallel (a, b, depth) ->
        sprintf "(%s <&> %s) [fusion=%A]" (pp a) (pp b) depth
    | IRFusion (a, b) ->
        sprintf "(%s <&!> %s)" (pp a) (pp b)
    | IRBind (c, k) ->
        sprintf "(%s >>= %s)" (pp c) (pp k)
    | IRFunctorMap (f, c) ->
        sprintf "(%s <$> %s)" (pp f) (pp c)
    | IRIndex (arr, idxs, _) ->
        sprintf "%s(%s)" (pp arr) (idxs |> List.map pp |> String.concat ", ")
    | IRCurry (arr, idx, rank) ->
        sprintf "%s(%s) [->rank %d]" (pp arr) (pp idx) rank
    | IRApp (f, args, _) ->
        sprintf "%s(%s)" (pp f) (args |> List.map pp |> String.concat ", ")
    | IRZip arrs ->
        sprintf "zip(%s)" (arrs |> List.map pp |> String.concat ", ")
    | IRStack arrs ->
        sprintf "stack(%s)" (arrs |> List.map pp |> String.concat ", ")
    | IRArity (None, name) -> sprintf "arity(%s)" name
    | IRArity (Some n, name) -> sprintf "arity(%s=%d)" name n
    | IRNth -> "nth"
    | IRZero -> "zero"
    | IRRank arr -> sprintf "rank(%s)" (pp arr)
    | IRPolyIndex (pack, idx) -> sprintf "%s[%s]" (pp pack) (pp idx)
    | IRPolyTail (pack, drop) -> sprintf "%s[%d..]" (pp pack) drop
    | IRChoice (a, b) ->
        sprintf "(%s <|> %s)" (pp a) (pp b)
    | IRFallback (a, b) ->
        sprintf "(%s <|:> %s)" (pp a) (pp b)
    | IRCompose (f, g) ->
        sprintf "(%s >> %s)" (pp f) (pp g)
    | IRComposeObj (f, g) ->
        sprintf "(%s >>@ %s)" (pp f) (pp g)
    | IRComposeMeth (f, g) ->
        sprintf "(%s @>> %s)" (pp f) (pp g)
    | IRConstraintCheck (c, msg, _) ->
        sprintf "check(%s, \"%s\")" (pp c) msg
    | IRAssign (target, v) ->
        let targetStr =
            match target with
            | LVVar id ->
                match Map.tryFind id names with
                | Some name -> name
                | None -> sprintf "v%d" id
            | LVIndex (arr, idxs) ->
                let arrStr = pp arr
                let idxStr = idxs |> List.map pp |> String.concat ", "
                sprintf "%s[%s]" arrStr idxStr
            | LVField (obj, f) -> sprintf "%s.%s" (pp obj) f
            | LVOther e -> pp e
        sprintf "%s <- %s" targetStr (pp v)
    | IRForRange (vid, lo, hi, body) ->
        let varName = Map.tryFind vid names |> Option.defaultValue (sprintf "v%d" vid)
        sprintf "for %s in %s..%s { %s }" varName (pp lo) (pp hi) (pp body)
    | _ -> "<expr>"

/// Default pretty printer (no name context)
let ppIRExpr indent expr = ppIRExprWithNames Map.empty indent expr


// IR Validator -- catches malformed IR between lowering and codegen

/// Attempt to statically evaluate an IRExpr to an int64, for resolving
/// extent expressions to compile-time literals (e.g. derived extents like
/// `Idx<n+1>`); anything more general returns None. Intentionally narrow --
/// StaticEval.fs already provides a full evaluator over the surface AST; the
/// use cases here (extents() inspection, reduce()'s non-emptiness check)
/// only need arithmetic over int literals.
let rec tryEvalIntIR (expr: IRExpr) : int64 option =
    match expr with
    | IRLit (IRLitInt n) -> Some n
    | IRBinOp (_, op, l, r) ->
        match tryEvalIntIR l, tryEvalIntIR r with
        | Some lv, Some rv ->
            match op with
            | IRAdd -> Some (lv + rv)
            | IRSub -> Some (lv - rv)
            | IRMul -> Some (lv * rv)
            | IRDiv when rv <> 0L -> Some (lv / rv)
            | IRMod when rv <> 0L -> Some (lv % rv)
            | _ -> None
        | _ -> None
    | IRUnaryOp (IRNeg, e) ->
        tryEvalIntIR e |> Option.map (fun n -> -n)
    | _ -> None

// AnalysisContext -- unified callable-walking for cross-procedural analysis
//
// `exprAttrs` walks an expression tree to compute attributes (FreeVars,
// BoundVars, IsPure); its IRApp arm follows IRVar(fId) through the
// CallablesTable, substitutes the callee's params with the call's args, and
// walks the body, so free variables from inside a callee surface to the
// caller's analysis. `Visited` short-circuits recursion (mutual and direct
// self-recursion stop on re-entry). CallablesTable is set once per module at
// codegen entry; Visited is augmented/restored at each IRApp boundary by
// `withVisited`; both live in one AsyncLocal record.
//
// This gives a mask predicate's exprAttrs walk visibility into every
// reachable contains (direct, through inline lambdas, through function
// calls up to recursion); whether codegen can substitute set.count for a
// given probe is a separate reachability check on the rendered tree.
//
// (CallablesTable, AnalysisContext, analysisCtxStorage, currentAnalysisCtx,
// setCallablesContext, restoreAnalysisContext, withVisited, and
// resolveCallable were moved earlier in the file, to before
// buildLoopNestCodeGen, because that builder needs to resolve IRVar-typed
// kernels through the CallablesTable.)

/// Build a CallablesTable from a module's function list. Codegen calls
/// this at module entry and installs the result via setCallablesContext.
let buildCallablesTable (funcs: IRCallable list) : CallablesTable =
    funcs |> List.map (fun f -> (f.Id, f)) |> Map.ofList

/// Build a CallablesTable from a full module, including alias entries
/// for let-bindings that reference lifted callables.
///
/// Motivation: when `let f = lambda(...)` lowers, the lambda
/// gets lifted to module.Functions with callableId, and the binding's
/// value is `IRVar(callableId, funcType)`. The binding itself has a
/// FRESH `bindingId` distinct from `callableId`. Subsequent references
/// to `f` lower as `IRVar(bindingId, _)`, NOT `IRVar(callableId, _)` --
/// they go through the binding's identity, not the callable's.
///
/// Without alias entries, `resolveCallable(IRVar(bindingId, _))` returns
/// None because the binding id isn't in the function table. Consumers
/// then fall to their non-callable fallback, which for the loop nest
/// kernel-extraction site means an empty body (rendered as
/// `((void)0)` in the generated C++).
///
/// This helper walks both top-level bindings AND nested IRLet
/// expressions (a `let f = lambda(...) in body` inside a block becomes
/// `IRLet(f.Id, ..., body)` inside the enclosing binding's value).
/// Every alias of the form `bindingId = IRVar(callableId, _)` where
/// callableId resolves in the base table adds `bindingId -> callable`
/// to the alias map. Multiple hops are followed transitively
/// (`let g = f` where `f` itself aliases a callable resolves `g` to the
/// same callable). The result is the base table with all aliases merged.
let buildCallablesTableForModule (modul: IRModule) : CallablesTable =
    let baseTable = buildCallablesTable modul.Functions
    let aliases = System.Collections.Generic.Dictionary<IRId, IRId>()
    // Side-effecting visitor: at every IRLet, record bindingId -> targetId
    // if the value is a direct IRVar reference. Returns the expression
    // unchanged so `mapIRExpr` walks the whole tree.
    let visitor (e: IRExpr) : IRExpr =
        match e with
        | IRLet (bindingId, IRVar (targetId, _), _) ->
            aliases.[bindingId] <- targetId
        | _ -> ()
        e
    let walk (e: IRExpr) : unit = mapIRExpr visitor e |> ignore
    // Walk top-level binding values; also record alias if a top-level
    // binding's value is a direct IRVar (handles `let f = lambda(...)`
    // at module scope).
    modul.Bindings |> List.iter (fun b ->
        (match b.Value with
         | IRVar (targetId, _) -> aliases.[b.Id] <- targetId
         | _ -> ())
        walk b.Value)
    // Walk function bodies (nested IRLets there too).
    modul.Functions |> List.iter (fun f -> walk f.Body)
    // Resolve transitive aliases (bindingId -> targetId -> ...) with a
    // fixed step bound. Well-formed IR has fresh ids per binding so
    // cycles are structurally impossible; the bound is defensive.
    let resolveTransitive (startId: IRId) : IRId =
        let mutable curr = startId
        let mutable steps = 0
        while steps < 32 && aliases.ContainsKey(curr) do
            curr <- aliases.[curr]
            steps <- steps + 1
        curr
    // For each alias, follow transitively; if the final target is a
    // real callable, add the binding id -> callable entry.
    let mutable result = baseTable
    for kvp in aliases do
        let finalId = resolveTransitive kvp.Key
        match Map.tryFind finalId baseTable with
        | Some callable -> result <- Map.add kvp.Key callable result
        | None -> ()
    result

// (resolveCallable was moved earlier in the file -- see the analysisCtx
// block before buildLoopNestCodeGen -- so that the loop-nest builder
// can call it for IRVar-typed kernels.)

// ExprAttrs -- bottom-up attribute computation for IR expressions
//
// A single bottom-up pass that computes
//   FreeVars  -- IRIds referenced from outside this expression's binders
//   BoundVars -- IRIds introduced inside (by IRLet, lambda params, etc.)
//   IsPure    -- no observable side effects
// for any IRExpr.
//
// This does NOT drive any rewrite. It exists so that future passes (a
// general hoist, then LICM/CSE) can consume a uniform, audited source of
// "what does this expression depend on?".
//
// Design notes:
//   - No memoization: a correctness foundation, not a hot path. Add a
//     reference-keyed cache if profiling later shows this dominating.
//   - IsPure is currently true for all native Blade IR (no I/O, no
//     in-language mutation beyond codegen's deterministic allocations);
//     exists for forward compatibility with a future impure construct.
//   - Exhaustive by construction: only semantically special variants have
//     explicit arms (IRVar contributes a free var; IRApp follows resolvable
//     callees; BinderShape variants scope their bound ids). Everything else
//     merges its children's attrs via the canonical ExprShape fold.

type ExprAttrs = {
    FreeVars:  Set<IRId>
    BoundVars: Set<IRId>
    IsPure:    bool
}

let private emptyAttrs : ExprAttrs =
    { FreeVars = Set.empty; BoundVars = Set.empty; IsPure = true }

let private mergeAttrs (a: ExprAttrs) (b: ExprAttrs) : ExprAttrs =
    { FreeVars  = Set.union a.FreeVars  b.FreeVars
      BoundVars = Set.union a.BoundVars b.BoundVars
      IsPure    = a.IsPure && b.IsPure }

let private mergeMany (xs: ExprAttrs list) : ExprAttrs =
    List.fold mergeAttrs emptyAttrs xs

let rec exprAttrs (expr: IRExpr) : ExprAttrs =
    match expr with
    // -- Variable reference: the one FreeVars source --
    | IRVar (id, _) ->
        { emptyAttrs with FreeVars = Set.singleton id }

    | IRApp (f, args, _) ->
        let baseAttrs = mergeMany (exprAttrs f :: List.map exprAttrs args)
        // Unified cross-procedural analysis: if the called function is a
        // direct IRVar reference and resolvable in the current
        // CallablesTable, walk its body with parameter substitution.
        // This treats named functions the same way the IR tree walker
        // already treats inline lambdas -- both are "callables whose
        // body we walk." Recursion is bounded by the visited set in
        // AnalysisContext, which is augmented at every function-body
        // walk and restored afterwards.
        //
        // The walked body's probes will have Node references pointing
        // at IRContains nodes inside the function body, not in the
        // caller's tree. The mask renderer's reachability check in
        // codegen filters those out before adding to its substitution
        // map, so unreachable probes don't generate unused preamble.
        // They remain visible in the analysis for diagnostic or
        // future-use purposes.
        match f with
        | IRVar (fId, _) ->
            let ctx = currentAnalysisCtx ()
            match Map.tryFind fId ctx.Callables with
            | Some callable when not (Set.contains fId ctx.Visited) ->
                // Substitute formal params with actual args. Lengths
                // should match in well-typed IR; defensively truncate.
                let parms = callable.Params
                let body = callable.Body
                let n = min args.Length parms.Length
                let mapping =
                    List.zip (List.truncate n parms) (List.truncate n args)
                    |> List.map (fun (p, a) -> (p.VarId, a))
                    |> Map.ofList
                let body' = substituteIRVars mapping body
                let bodyAttrs = withVisited fId (fun () -> exprAttrs body')
                mergeAttrs baseAttrs bodyAttrs
            | _ -> baseAttrs
        | _ -> baseAttrs

    // -- Binders: scoped children lose their bound ids, which surface in
    //    BoundVars instead. One arm covers IRLet, IRForRange, and IRMatch
    //    via BinderShape -- a new binding variant needs exactly one
    //    BinderShape case to get correct scoping here. (IRLet's value
    //    arrives in the free part: a reference to the let-id inside its
    //    own value is ill-formed IR, and NOT subtracting it there keeps
    //    such a bug visible as a free var at the outer level.)
    | BinderShape (free, scopes) ->
        let freeAttrs = free |> List.map exprAttrs |> mergeMany
        let scopeAttrs =
            scopes |> List.map (fun (bound, parts) ->
                let a = parts |> List.map exprAttrs |> mergeMany
                { FreeVars  = Set.difference a.FreeVars bound
                  BoundVars = Set.union a.BoundVars bound
                  IsPure    = a.IsPure })
        mergeMany (freeAttrs :: scopeAttrs)

    // -- Everything else: merge over the canonical children --
    | ExprShape (children, _) ->
        children |> List.map exprAttrs |> mergeMany


/// Validation error with context
type IRValidationError = {
    Message: string
    Context: string  // e.g. "in binding 'result'" or "in function 'covariance'"
}

/// Recursively collect all types from an IRExpr tree. The per-variant TYPE
/// contributions are enumerated in `own` (a contribution override with a
/// default, not a traversal); recursion into children is the canonical
/// ExprShape fold, so no variant's subtree can be silently skipped. (A bare
/// `| _ -> []` catchall would stop RECURSION at whichever variants it didn't
/// enumerate -- IRSlice, IRShift, IRMask, IRZip, ... -- hiding any unresolved
/// types below them from the validator.)
let collectTypesInExpr (expr: IRExpr) : IRType list =
    let rec go (e: IRExpr) : IRType list =
        let own =
            match e with
            | IRVar (_, ty) -> [ty]
            | IRParam (_, _, ty) -> [ty]
            | IRApp (_, _, retTy) -> [retTy]
            | IRArrayLit (_, arrTy) -> [mkArrayLike arrTy]
            | IRApplyCombinator info -> [info.OutputType]
            | IRComposeApply info -> [info.OutputType]
            | _ -> []
        own @ (childrenOf e |> List.collect go)
    go expr

/// Check if a type contains any unresolved IRTInfer
let rec containsInfer (ty: IRType) : int option =
    match ty with
    | IRTInfer id -> Some id
    | IRTTuple ts -> ts |> List.tryPick containsInfer
    | IRTComputation inner -> containsInfer inner
    | IRTUnitAnnotated (inner, _) -> containsInfer inner
    | IRTIdxTagged (inner, _) -> containsInfer inner
    | IRTPoly (inner, _) -> containsInfer inner
    | IRTArrow (slots, ret, _) ->
        let slotInfer =
            slots |> List.tryPick (function
                | SVal ty -> containsInfer ty
                | SIdx _ | SIdxVirt _ -> None)
        match slotInfer with
        | Some _ -> slotInfer
        | None -> containsInfer ret
    | _ -> None

/// Collect all VarIds defined (brought into scope) by an expression
let rec collectDefinedIds (expr: IRExpr) : Set<IRId> =
    match expr with
    | IRLet (id, value, body) -> Set.add id (Set.union (collectDefinedIds value) (collectDefinedIds body))
    | IRForRange (vid, lo, hi, body) ->
        Set.add vid (Set.unionMany [collectDefinedIds lo; collectDefinedIds hi; collectDefinedIds body])
    | IRMatch (scrut, cases) ->
        let caseIds = cases |> List.collect (fun c ->
            let patIds = collectPatternIds c.Pattern
            Set.toList patIds)
        Set.union (collectDefinedIds scrut) (Set.ofList caseIds)
    | _ -> Set.empty

/// Collect VarIds bound by a pattern
and collectPatternIds (pat: IRPattern) : Set<IRId> =
    match pat with
    | IRPatVar id -> Set.singleton id
    | IRPatTuple pats -> pats |> List.map collectPatternIds |> Set.unionMany
    | IRPatCons (h, t) -> Set.union (collectPatternIds h) (collectPatternIds t)
    | IRPatVariant (_, _, Some inner, _) -> collectPatternIds inner
    | _ -> Set.empty

/// Validate a single IRModule, returning a list of errors
let validateModule (externalIds: Set<IRId>) (modul: IRModule) : IRValidationError list =
    let errors = ResizeArray<IRValidationError>()
    let addError ctx msg = errors.Add({ Message = msg; Context = ctx })

    // checkApplyInfo (below) resolves kernel slots through `resolveCallable`,
    // which needs the CallablesTable installed in the AsyncLocal analysis
    // context; install it via buildCallablesTableForModule (so let-bound
    // kernel references resolve through their alias) and restore the prior
    // context on exit so the validator doesn't leak state.
    let savedCtx = setCallablesContext (buildCallablesTableForModule modul)

    // Track all defined IDs (bindings + functions). External Ids come from
    // other modules visible via imports; without import metadata in
    // IRModule the validator can't cheaply distinguish "imported and used"
    // from "unrelated module's Id that happens to match", so it accepts all
    // program Ids as in-scope.
    let moduleIds =
        let bindIds = modul.Bindings |> List.map (fun b -> b.Id) |> Set.ofList
        let funcIds = modul.Functions |> List.map (fun f -> f.Id) |> Set.ofList
        Set.unionMany [bindIds; funcIds; externalIds]

    // Tag/IxKind agreement: the two encodings must never diverge -- a
    // construction that stamps a sentinel Tag without the matching IxKind
    // (or vice versa) is exactly the valid-but-wrong hazard this field
    // exists to kill. ixKindOfTag maps sentinels to kinds and everything
    // else to IxKPlain, so equality enforces both directions.
    let rec indexTypesOfType (ty: IRType) : IRIndexType list =
        match ty with
        | IRTArrow (slots, ret, _) ->
            (slots |> List.collect (function
                | SIdx ix | SIdxVirt ix -> [ix]
                | SVal t -> indexTypesOfType t))
            @ indexTypesOfType ret
        | IRTTuple ts -> ts |> List.collect indexTypesOfType
        | IRTComputation t | IRTPoly (t, _)
        | IRTUnitAnnotated (t, _) | IRTIdxTagged (t, _) -> indexTypesOfType t
        | _ -> []
    let checkKindAgreement ctx (ty: IRType) =
        for ix in indexTypesOfType ty do
            if ixKindOfTag ix.Tag <> ix.IxKind then
                addError ctx (sprintf "index type Tag/IxKind disagree: Tag=%A IxKind=%A (index id %d)" ix.Tag ix.IxKind ix.Id)

    // --- Check 1: No unresolved IRTInfer in binding types ---
    for b in modul.Bindings do
        let ctx = sprintf "in binding '%s'" b.Name
        match containsInfer b.Type with
        | Some id -> addError ctx (sprintf "unresolved type variable T?%d in declared type" id)
        | None -> ()
        checkKindAgreement ctx b.Type
        // Also check types inside the expression tree
        for ty in collectTypesInExpr b.Value do
            match containsInfer ty with
            | Some id -> addError ctx (sprintf "unresolved type variable T?%d in expression" id)
            | None -> ()
            checkKindAgreement ctx ty

    // --- Check 1b: No unresolved IRTInfer in function types ---
    for f in modul.Functions do
        let ctx = sprintf "in function '%s'" f.Name
        match containsInfer f.RetType with
        | Some id -> addError ctx (sprintf "unresolved type variable T?%d in return type" id)
        | None -> ()
        checkKindAgreement ctx f.RetType
        for p in f.Params do
            match containsInfer p.Type with
            | Some id -> addError ctx (sprintf "unresolved type variable T?%d in param '%s'" id p.Name)
            | None -> ()
            checkKindAgreement ctx p.Type
        for ty in collectTypesInExpr f.Body do
            match containsInfer ty with
            | Some id -> addError ctx (sprintf "unresolved type variable T?%d in body" id)
            | None -> ()
            checkKindAgreement ctx ty
    
    // --- Check 2: No dangling VarId references ---
    // Walk the expression tree, threading scope through lets, lambdas, matches, for-ranges
    let rec checkScope (scope: Set<IRId>) (ctx: string) (expr: IRExpr) =
        match expr with
        | IRVar (id, _) ->
            if not (Set.contains id scope) then
                addError ctx (sprintf "dangling VarId reference: v%d" id)
        | IRLet (id, value, body) ->
            checkScope scope ctx value
            checkScope (Set.add id scope) ctx body
        | IRForRange (vid, lo, hi, body) ->
            checkScope scope ctx lo
            checkScope scope ctx hi
            checkScope (Set.add vid scope) ctx body
        | IRMatch (scrut, cases) ->
            checkScope scope ctx scrut
            for c in cases do
                let patIds = collectPatternIds c.Pattern
                let caseScope = Set.union scope patIds
                c.Guard |> Option.iter (checkScope caseScope ctx)
                checkScope caseScope ctx c.Body
        | IRApp (f, args, _) ->
            checkScope scope ctx f
            args |> List.iter (checkScope scope ctx)
        | IRBinOp (_, _, l, r) -> checkScope scope ctx l; checkScope scope ctx r
        | IRUnaryOp (_, e) -> checkScope scope ctx e
        | IRIf (c, t, e) -> checkScope scope ctx c; checkScope scope ctx t; checkScope scope ctx e
        | IRTuple es -> es |> List.iter (checkScope scope ctx)
        | IRComplex (re, im) -> checkScope scope ctx re; checkScope scope ctx im
        | IRTupleProj (e, _, _) -> checkScope scope ctx e
        | IRArrayLit (es, _) -> es |> List.iter (checkScope scope ctx)
        | IRIndex (arr, idxs, _) -> checkScope scope ctx arr; idxs |> List.iter (checkScope scope ctx)
        | IRFieldAccess (obj, _) -> checkScope scope ctx obj
        | IRStructLit (_, fields) -> fields |> List.iter (fun (_, e) -> checkScope scope ctx e)
        | IRCompute inner -> checkScope scope ctx inner
        | IRReynolds (inner, _) -> checkScope scope ctx inner
        | IRMethodFor info -> info.Arrays |> List.iter (checkScope scope ctx)
        | IRObjectFor info -> checkScope scope ctx info.Kernel
        | IRSort (a, k) -> checkScope scope ctx a; checkScope scope ctx k
        | IRTranspose (a, _, _) -> checkScope scope ctx a
        | IRDecompact (a, _) -> checkScope scope ctx a
        | IRHaloUnhash (w, _) -> checkScope scope ctx w
        | IRArrayNegate a -> checkScope scope ctx a
        | IRArrayConjugate a -> checkScope scope ctx a
        | IRReduce (a, k, i) ->
            checkScope scope ctx a; checkScope scope ctx k
            (match i with Some e -> checkScope scope ctx e | None -> ())
        | IRProdSum args -> args |> List.iter (checkScope scope ctx)
        | IRApplyCombinator info ->
            checkScope scope ctx info.Loop
            checkScope scope ctx info.Kernel
            info.Arrays |> List.iter (checkScope scope ctx)
        | IRComposeApply info ->
            checkScope scope ctx info.Composition
            info.InputArrays |> List.iter (checkScope scope ctx)
        | IRParallel (a, b, _) -> checkScope scope ctx a; checkScope scope ctx b
        | IRFusion (a, b) -> checkScope scope ctx a; checkScope scope ctx b
        | IRChoice (a, b) -> checkScope scope ctx a; checkScope scope ctx b
        | IRFallback (a, b) -> checkScope scope ctx a; checkScope scope ctx b
        | IRBind (c, k) -> checkScope scope ctx c; checkScope scope ctx k
        | IRFunctorMap (f, c) -> checkScope scope ctx f; checkScope scope ctx c
        | IRGuard (c, b) -> checkScope scope ctx c; checkScope scope ctx b
        | IRSequence es -> es |> List.iter (checkScope scope ctx)
        | IRPure e -> checkScope scope ctx e
        | IRAssign (t, v) -> checkScope scope ctx t; checkScope scope ctx v
        | IRConstraintCheck (c, _, _) -> checkScope scope ctx c
        | _ -> ()  // Literals, params, etc. -- no var refs
    
    let mutable cumulativeScope = moduleIds
    for b in modul.Bindings do
        let ctx = sprintf "in binding '%s'" b.Name
        checkScope cumulativeScope ctx b.Value
        cumulativeScope <- Set.add b.Id cumulativeScope
    
    for f in modul.Functions do
        let ctx = sprintf "in function '%s'" f.Name
        let paramIds = f.Params |> List.map (fun p -> p.VarId) |> Set.ofList
        // Lifted lambdas live in module.Functions with their captures in
        // `f.Captures` (separate from `f.Params`). The captures' Ids
        // reference the enclosing source-level var; the lambda's body
        // references those Ids directly. Because the lambda is its own
        // top-level function, the enclosing function's params aren't in
        // scope at the validator's `for f in modul.Functions` loop; we
        // have to add the function's own Captures' Ids to the visible
        // scope so the body's references resolve.
        let captureIds = f.Captures |> List.map (fun c -> c.Id) |> Set.ofList
        let funcScope = Set.unionMany [moduleIds; paramIds; captureIds]
        checkScope funcScope ctx f.Body
    
    // --- Check 3: ApplyInfo consistency ---
    let rec checkApplyInfo (ctx: string) (expr: IRExpr) =
        match expr with
        | IRApplyCombinator info ->
            if info.Arrays.Length <> info.ArrayTypes.Length then
                addError ctx (sprintf "ApplyInfo: Arrays.Length=%d != ArrayTypes.Length=%d" info.Arrays.Length info.ArrayTypes.Length)
            if info.Arrays.Length <> info.Identities.Length then
                addError ctx (sprintf "ApplyInfo: Arrays.Length=%d != Identities.Length=%d" info.Arrays.Length info.Identities.Length)
            if info.SDimsPerArray.Length <> info.Arrays.Length && info.SDimsPerArray.Length <> 0 then
                addError ctx (sprintf "ApplyInfo: SDimsPerArray.Length=%d != Arrays.Length=%d" info.SDimsPerArray.Length info.Arrays.Length)
            // Canonical apply: Kernel slot is a callable reference, either
            // IRVar(id, _) or IRReynolds(IRVar(id, _), _); `resolveKernel`
            // peels any Reynolds wrapper. `info.Loop = IRObjectFor _` can
            // only arise from canonical `object_for(g) <@> A` (the
            // slot-inverted compose case routes through IRComposeApply), so
            // it also unambiguously implies a callable kernel. Skip the
            // check when Loop is IRVar (let-bound; could resolve to either
            // shape, and the binding env isn't available here) -- codegen
            // retains its own resolution for that case.
            let kernelSlotIsCallable =
                match info.Loop with
                | IRMethodFor _ | IRObjectFor _ -> true
                | _ -> false
            if kernelSlotIsCallable then
                match resolveKernel info.Kernel with
                | Some rk ->
                    let lInfo = rk.Callable
                    if lInfo.Params.Length <> info.KernelInputRanks.Length then
                        addError ctx (sprintf "ApplyInfo: kernel params=%d != KernelInputRanks.Length=%d" lInfo.Params.Length info.KernelInputRanks.Length)
                    // Verify CommGroup indices are in range
                    for cg in lInfo.CommGroups do
                        for idx in cg do
                            if idx < 0 || idx >= lInfo.Params.Length then
                                addError ctx (sprintf "CommGroup index %d out of range [0, %d)" idx lInfo.Params.Length)
                | None ->
                    // Identify the structural form to make the error
                    // actionable for whoever introduced the malformed
                    // IR. Shape names match the IRExpr discriminator so
                    // a grep against the constructor finds the producer.
                    let (inner, desc) = peelReynolds info.Kernel
                    let shapeDesc =
                        match inner with
                        | IRVar (id, _) ->
                            sprintf "IRVar(v%d) [id resolves in neither CallablesTable nor synthetic registry]" id
                        | IRLit _ -> "IRLit [literal in kernel slot]"
                        | IRBinOp _ -> "IRBinOp [unlifted operator expression]"
                        | IRApp _ -> "IRApp [unlifted application]"
                        | IRZero -> "IRZero [zero placeholder; should have been synthesized to a callable]"
                        | IRReynolds _ -> "IRReynolds [nested Reynolds wrapper, not supported]"
                        | _ -> "non-callable expression"
                    let prefix =
                        if desc.HasReynolds then "ApplyInfo: IRReynolds inner is"
                        else "ApplyInfo: kernel slot is"
                    addError ctx (sprintf "%s %s" prefix shapeDesc)
        | IRComposeApply info ->
            // Compose-apply: InputArrays threaded through a composed
            // object chain. Composition should resolve to IRComposeObj
            // (possibly through a let-binding); InputArrays must be
            // non-empty (you can't apply a compose to nothing).
            if info.InputArrays.IsEmpty then
                addError ctx "ComposeApplyInfo: InputArrays is empty"
            match info.Composition with
            | IRComposeObj _ | IRVar _ -> ()   // expected shapes
            | other ->
                let shapeName =
                    match other with
                    | IRLit _ -> "IRLit"
                    | IRObjectFor _ -> "IRObjectFor [single object, not composed]"
                    | IRMethodFor _ -> "IRMethodFor [should be IRApplyCombinator, not IRComposeApply]"
                    | _ -> "non-compose expression"
                addError ctx (sprintf "ComposeApplyInfo: Composition is %s; expected IRComposeObj or IRVar" shapeName)
        | _ -> ()
        // Recurse into sub-expressions
        match expr with
        | IRLet (_, v, b) -> checkApplyInfo ctx v; checkApplyInfo ctx b
        | IRCompute inner -> checkApplyInfo ctx inner
        | IRParallel (a, b, _) -> checkApplyInfo ctx a; checkApplyInfo ctx b
        | IRFusion (a, b) -> checkApplyInfo ctx a; checkApplyInfo ctx b
        | IRChoice (a, b) -> checkApplyInfo ctx a; checkApplyInfo ctx b
        | IRFallback (a, b) -> checkApplyInfo ctx a; checkApplyInfo ctx b
        | IRBind (c, k) -> checkApplyInfo ctx c; checkApplyInfo ctx k
        | IRFunctorMap (f, c) -> checkApplyInfo ctx f; checkApplyInfo ctx c
        | IRGuard (_, b) -> checkApplyInfo ctx b
        | IRSequence elems -> elems |> List.iter (checkApplyInfo ctx)
        | _ -> ()
    
    for b in modul.Bindings do
        checkApplyInfo (sprintf "in binding '%s'" b.Name) b.Value
    for f in modul.Functions do
        checkApplyInfo (sprintf "in function '%s'" f.Name) f.Body
    
    // --- Check 4: No empty match arms ---
    let rec checkEmptyMatch (ctx: string) (expr: IRExpr) =
        match expr with
        | IRMatch (_, []) -> addError ctx "empty match expression (no cases)"
        | _ -> ()
        match expr with
        | IRLet (_, v, b) -> checkEmptyMatch ctx v; checkEmptyMatch ctx b
        | IRIf (c, t, e) -> checkEmptyMatch ctx c; checkEmptyMatch ctx t; checkEmptyMatch ctx e
        | IRMatch (s, cases) ->
            checkEmptyMatch ctx s
            cases |> List.iter (fun c -> checkEmptyMatch ctx c.Body)
        | IRCompute inner -> checkEmptyMatch ctx inner
        | _ -> ()
    
    for b in modul.Bindings do
        checkEmptyMatch (sprintf "in binding '%s'" b.Name) b.Value

    // Restore the prior AnalysisContext so the validator doesn't
    // leak its installed CallablesTable to subsequent passes.
    restoreAnalysisContext savedCtx
    errors |> Seq.toList

/// Validate an entire IR program.
/// Pre-collects all defined Ids across modules so cross-module references
/// (selective imports of values/functions) don't appear dangling within
/// individual module validation passes.
let validateIR (program: IRProgram) : Result<IRProgram, string list> =
    let allIds =
        program.Modules |> List.collect (fun m ->
            (m.Bindings |> List.map (fun b -> b.Id)) @
            (m.Functions |> List.map (fun f -> f.Id)))
        |> Set.ofList
    let allErrors =
        program.Modules |> List.collect (validateModule allIds)
    if allErrors.IsEmpty then
        Ok program
    else
        let messages = allErrors |> List.map (fun e -> sprintf "[IR Validation] %s: %s" e.Context e.Message)
        Error messages
