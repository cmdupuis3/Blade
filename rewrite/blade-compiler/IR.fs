// Blade-DSL Intermediate Representation
// Lowered from AST, ready for optimization and code generation
// Implements the Structural Trinity: Loop Reification, Arity Polymorphism, Dimensional Currying

module Blade.IR

open Blade.Types

open System

// ============================================================================
// Core Type Definitions
// ============================================================================

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
    | IRMath of string  // scalar math intrinsic (exp/log/sqrt/...);
                        // renders as std::<name>(arg), result Float64

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
    // Complex literal construction: std::complex<double>(re, im) at codegen.
    // Distinct from IRTuple to preserve scalar nature — Complex is a scalar
    // throughout the IR; the tuple-shaped surface syntax is consumed
    // entirely between Parser and TypeCheck. Components are arbitrary
    // float-typed IRExpr (matching what checkExpr accepts), not just
    // literal floats; this supports both `(1.0, 0.0): Complex128` and
    // `(x, y): Complex128` for x, y: Float64.
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
    /// Distinct from IRApplyCombinator because the kernel slot of a
    /// canonical apply holds a callable, whereas the compose case threads
    /// arrays through a composed-object chain. Modeling these as separate
    /// variants removes the slot overloading that previously forced every
    /// consumer to inspect Loop's shape to figure out which interpretation
    /// to apply. See `ComposeApplyInfo` below.
    | IRComposeApply of ComposeApplyInfo
    | IRBind of comp: IRExpr * cont: IRExpr
    | IRParallel of IRExpr * IRExpr * fusionDepth: int option
    | IRFusion of IRExpr * IRExpr
    | IRChoice of IRExpr * IRExpr
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
    | IRGroupKeys of keys: IRExpr list               // group_keys(keys1, keys2, ...) - CSR grouping; multi-key ⇒ compound dispatch
    | IRSort of array: IRExpr * key: IRExpr          // sort(arr, key) - stable ascending sort by key
    | IRReduce of array: IRExpr * kernel: IRExpr * init: IRExpr option  // reduce(arr, op[, init]) - fold innermost dim; init seeds the fold and defines the empty result
    // reduce(deferred, op[, init]) — the FUSED reduction terminal: fold a
    // deferred computation (IRApplyCombinator, or an IRFusion tree of them)
    // without materializing the array(s). ONE loop nest; one scalar
    // accumulator per fusion leaf (result = tuple of scalars for trees).
    // Semantically the fold is a binary-kernel stage in the loop-object
    // composition algebra (object_for((+)) is a reduction stage); this node
    // is the checker-typed forcing of that composition. init is ALWAYS
    // filled by the checker (identity for (+)/(*) sections, user's init
    // otherwise — arbitrary kernels REQUIRE an explicit init).
    | IRReduceCompute of computation: IRExpr * kernel: IRExpr * init: IRExpr
    | IRProdSum of args: IRExpr list  // prodsum(x1..xk): fused Σ_t Π_ℓ xℓ(t) over rank-1 arrays of equal extent; empty extent ⇒ 0
    | IRZip of IRExpr list
    | IRAlign of arrays: IRExpr list * spec: AlignSpec
    | IRStack of IRExpr list
    | IRTranspose of array: IRExpr * dim1: int * dim2: int
    | IRDecompact of array: IRExpr * dim: int
    | IRGram of left: IRExpr * right: IRExpr * isSameArray: bool  // A * B^H contraction; symmetric/Hermitian when isSameArray
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
    | IRArity of resolved: int option * paramName: string  // None = unresolved (use paramName), Some n = bound
    | IRNth
    | IRZero
    | IRRank of array: IRExpr
    | IRPolyIndex of pack: IRExpr * index: IRExpr  // Dynamic poly-pack indexing: args[k]
    | IRExtent of array: IRExpr * dim: int
    // Ragged-extent marker: at the position this appears as an IRIndexTypeG<IRExpr>'s
    // Extent, the codegen emits a lookup into the lengths array using the
    // current iteration's flat outer position. The lengths array is shaped
    // to match the prior index dimensions (e.g., Array<Nat like Idx<M>, Idx<N>>
    // for `Idx<M>, Idx<N>, RaggedIdx<lengths>`); the codegen handles the
    // flat-position computation internally so the user doesn't need to expose
    // the flattening at the type level.
    //
    // Distinct from IRExtent (which queries an array's extent metadata):
    // IRRaggedLookup reads a value from the lengths array at the current
    // iteration's logical position.
    | IRRaggedLookup of lengths: IRExpr
    // Compound-index marker (formalism 4.5): where this appears as an
    // IRIndexTypeG<IRExpr>'s Extent, the index type is a CompoundIdx -- a masked product
    // space whose valid coordinates are selected by `mask`, a RUNTIME array
    // value (index types are parameterized by runtime values; a CompoundIdx is
    // identified by a whole-mask hash, 4.5). Rank (= mask rank) lives on the
    // IRIndexTypeG<IRExpr> record; per-dimension extents are recovered from the mask's
    // array type at codegen. Cardinality is NOT closed-form -- the emitted
    // compound_index_t builds its rank<->tuple table at construction and reports
    // cardinality at runtime. Distinct from IRRaggedLookup (a nested dependent
    // extent): a compound mask couples all dimensions at once.
    | IRCompoundMask of mask: IRExpr
    // Residual-compound marker (formalism 4.5, partial indexing): where this
    // appears as an IRIndexTypeG<IRExpr>'s Extent, the index type is a CompoundIdx that
    // arose from PARTIALLY indexing a parent compound -- pinning the first
    // `prefixLen` (= j) of the parent's k coordinates and leaving k-j free. The
    // residual is a masked product space over the remaining k-j axes, restricted
    // to the parent's valid tuples whose leading j coords match the pinned
    // prefix. Its Rank (= k-j) lives on the IRIndexTypeG<IRExpr> record.
    //
    // Representation (design decision, this feature): SHARED DATA, MATERIALIZED
    // INDEX. The residual's data is a non-copied window into the parent's
    // contiguous lex-sorted pool (the prefix_range [lo,hi) slice); its index is a
    // freshly materialized compound_index_t<k-j> built over that window with the
    // prefix stripped. This reconciles formalism 4.5's internal tension (the
    // identity language said "view over (parent mask, fixed coords)"; the cost
    // note said "O(n) scan/materialize") -- the data is a view, the index is
    // materialized, so the O(window) cost note is the accurate one.
    //
    // `parent` is the parent compound array expression; `prefixLen` is j. The
    // pinned coordinate VALUES are carried by the indexing site (IRIndex), not
    // here -- this marker only records the residual's shape/identity so that
    // Extent-consuming passes (PlaceTabulated detection, level counting, varref
    // collection) treat it uniformly with IRCompoundMask. Codegen for the
    // residual construction is deferred (phase 2); until then the partial-index
    // path emits an explicit not-yet-implemented error rather than miscompiling.
    | IRCompoundProject of parent: IRExpr * prefixLen: int
    // Opaque-extent marker: appears as an IRIndexTypeG<IRExpr>'s Extent when the
    // extent is determined by surrounding context rather than declared up
    // front. The canonical use is a kernel-parameter type `RaggedIdx<_>`
    // (or any future "context-supplied extent" variant), where at the peel
    // point the kernel param is bound to a sub-array whose `_extents[0]`
    // carries the actual length.
    //
    // Distinct from IRRaggedLookup (which carries a specific lengths
    // expression to read from): IROpaqueExtent carries no data — the
    // ExtentArrayRef on the loop binding tells codegen where to read the
    // concrete extent from (typically the sub-array's own _extents).
    //
    // Should not normally reach codegen for direct rendering; the loop
    // builder reads it indirectly via the binding's ExtentArrayRef path.
    | IROpaqueExtent
    | IRAssign of target: IRExpr * value: IRExpr
    | IRForRange of varId: IRId * lo: IRExpr * hi: IRExpr * body: IRExpr

/// Abstract callable in IR. The merged form of source-level functions
/// and lambdas. Lives in the IRExpr mutual-recursion group because
/// `IRCallable.Body : IRExpr` forms a cycle with IRExpr's variants.
///
/// Field roles by source kind:
///   - Id, Name: always populated. Lambdas get synthesized "__lambda_<id>".
///   - Params, Body: the callable's interface and computation.
///   - Captures: free vars from enclosing scope. Empty for source-level
///     functions; populated for lambdas by lambda-lifting.
///   - IsCommutative, CommGroups: kernel symmetry annotations.
///     IsCommutative=false, CommGroups=[] when not annotated.
///   - Parallelism, IsStatic, IsArityPoly, ArityParam: function-only
///     metadata; empty/false/None for lambdas.
///   - RetType: explicit return type. Functions get it from declaration;
///     lambdas get it from inference at construction time.
///
/// Codegen (Stage 5) renders all callables as top-level C++ functions
/// with signature (originalParams..., captures...). At call sites
/// referencing a callable with non-empty Captures, a thin C++ lambda
/// wrapper forwards the captures by reference, preserving the
/// OCaml-style capture semantics users expect from lambdas.
and IRCallable = {
    Id: IRId
    Name: string
    Params: IRParam list
    RetType: IRTypeG<IRExpr>
    Body: IRExpr
    IsStatic: bool
    IsCommutative: bool
    CommGroups: int list list
    // Parallelism: per-(param-index, dim-count) detail from an `omp(...)` clause.
    // IsOmpParallel is the derived "this callable requested OpenMP" flag — the
    // opt-in signal that drives loop parallelization (see buildLoopNestCodeGen).
    // Kept as a bool alongside the detail list, mirroring IsCommutative/CommGroups.
    Parallelism: (int * int) list
    IsOmpParallel: bool
    // IsCudaKernel is the analogous opt-in flag for the `cuda` strategy: when
    // true, codegen emits a flat-launch __global__ kernel + host launch instead
    // of a host loop nest (see genCudaKernel — added in a following increment).
    // CudaBlockSize carries the launch block size (default 256). omp and cuda are
    // mutually exclusive today, so at most one of IsOmpParallel/IsCudaKernel is
    // true; both false = serial host loop (the default).
    IsCudaKernel: bool
    CudaBlockSize: int
    IsArityPoly: bool
    ArityParam: string option
    Captures: CaptureInfo list
}

/// IRFuncDef is a semantic-marker alias for IRCallable that names
/// "top-level function in module.Functions" — distinct in intent
/// from the codegen-internal synthetic callables and from let-bound
/// aliases. The two type names refer to the same underlying record;
/// usage sites pick whichever conveys intent.
and IRFuncDef = IRCallable

and CaptureInfo = {
    Id: IRId
    Name: string
    /// The capture's IR type. Lifted-lambda codegen (Stage 3c) extends
    /// the C++ function signature with one parameter per capture, typed
    /// from this field as a reference (`T&`) so mutation propagates and
    /// the value stays alive via the wrapper's `[&]` capture at the use
    /// site. Source-level functions have no captures, so this field is
    /// irrelevant for them.
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
    SharedIndexType: IRIndexTypeG<IRExpr> option  // For co-iteration: shared index space from 'in' clause
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
    SharedIndexType: IRIndexTypeG<IRExpr> option     // For co-iteration (zip)
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
/// let-bound to one — codegen resolves the latter via
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


// Concrete instantiations of the Types.fs generic family at IRExpr —
// the prototype's extent representation. Everything downstream uses
// these names; swapping the extent representation (the rewrite's
// dedicated Extent DU) is a type-argument change here, nothing more.
type IRType = IRTypeG<IRExpr>
type IRIndexType = IRIndexTypeG<IRExpr>
type IRArrayType = IRArrayTypeG<IRExpr>
type IdxRef = IdxRefG<IRExpr>
type IRArrowSlot = IRArrowSlotG<IRExpr>
type LoopType = LoopTypeG<IRExpr>

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
    // A residual (partially-indexed) compound: the residual RANK decides
    // placement. Rank 1 means exactly one free coordinate remains at the pinned
    // prefix -- a single contiguous window [lo,hi) of present cells, iterated as
    // an ordinary dense 1-D loop (no hash table). Rank >= 2 is still a masked
    // product space over the remaining axes and needs the tabulated (materialized
    // child index) path. (Option X: one carrier, placement by residual rank.)
    | IRCompoundProject _ -> if ix.Rank <= 1 then placementClassOf ix.Symmetry else PlaceTabulated
    | _ -> placementClassOf ix.Symmetry

/// Compute the promoted element type for two numeric types per §3.4.2.
/// Returns None if the types are incompatible for promotion. Index nominal
/// tags are no longer represented at the ElemType level (ETIndexRef was
/// retired in the Option C migration); their strict unification happens
/// at the IRType level via IRTIdxTagged in `unify`.
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
        | _ -> None

/// Active pattern for assignment target (lvalue) classification
let (|LVVar|LVIndex|LVField|LVOther|) = function
    | IRVar (id, _)              -> LVVar id
    | IRIndex (arr, indices, _)  -> LVIndex (arr, indices)
    | IRFieldAccess (obj, field) -> LVField (obj, field)
    | e                          -> LVOther e

// ============================================================================
// Element-type active patterns
// ============================================================================
//
// These patterns project an IRType into the role of "array element type."
// `IRArrayType.ElemType` is `IRType` (post-Phase B2), so consumers can be
// rewritten to use feature-scoped patterns rather than ad-hoc match arms.
//
// Design philosophy: each pattern represents one "concern" (primitives,
// inference variables, named types, function values, etc.). A site that
// only handles primitives uses `PrimElem` or `AnyPrimElem`; the pattern
// fails to match for any other case, so silent fallthroughs are caught
// at compile time rather than producing wrong-typed values.

/// Strict primitive element. Doesn't match through unit annotation.
let (|PrimElem|_|) (ty: IRType) =
    match ty with
    | IRTScalar et -> Some et
    | _ -> None

/// Primitive element, optionally unit-annotated or index-tagged. Workhorse
/// for read sites that just want the primitive and don't care whether
/// wrappers are attached. Matches through both IRTUnitAnnotated (physical
/// units) and IRTIdxTagged (nominal index tags) — both preserve their
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

/// Inference variable in element position. After Phase B2, this becomes
/// the natural representation for "kernel param's elem type, deferred
/// until <@> unifies it with the source array's per-row type."
let (|InferElem|_|) (ty: IRType) =
    match ty with
    | IRTInfer id -> Some id
    | _ -> None

/// Named (struct or sum) elem type. Future Phase D enables codegen support
/// for arrays of user-defined types; this pattern is the dispatch site.
let (|NamedElem|_|) (ty: IRType) =
    match ty with
    | IRTNamed name -> Some name
    | _ -> None

/// Function-valued elem type. Reflects the array-function duality: in
/// Blade, an Array<T like Idx<n>> is conceptually a function `Idx<n> -> T`,
/// so functions in elem position (arrays of functions) are structurally
/// Function-shaped type. Matches `IRTArrow` when every slot is `SVal`
/// (i.e., a pure function with no storage-backed slots), and returns
/// the canonical `(args, ret)` view that consumers want.
///
/// Note: matches the empty-slot case too — `IRTArrow ([], ret, None)`
/// is a nullary function (produced by `mkFuncArrow []`). This is the
/// symmetric counterpart to ArrayElem's empty-slot rejection: only
/// FuncElem accepts the empty-arrow shape.
///
/// The `identity` field on IRTArrow is ignored here — functions don't
/// carry array identity; producers of function arrows always set it
/// to None.
let (|FuncElem|_|) (ty: IRType) =
    match ty with
    | IRTArrow (slots, ret, _) when slots |> List.forall (function SVal _ -> true | _ -> false) ->
        let args = slots |> List.map (function SVal t -> t | _ -> failwith "unreachable")
        Some (args, ret)
    | _ -> None

/// Smart constructor: build an arrow-shaped type from a parameter type
/// list and return type. Produces an `IRTArrow` with all-`SVal` slots
/// and no identity — the unified-IR function form.
let mkFuncArrow (args: IRType list) (ret: IRType) : IRType =
    IRTArrow (args |> List.map SVal, ret, None)

/// Validate the shape constraints on an IRTArrow's slot list and result.
///
/// Constraints (per the unified-arrow design):
///   1. If any slot is SIdxVirt, all slots from that point onward must
///      also be SIdxVirt. Equivalently: no SIdx or SVal may appear
///      after the first SIdxVirt.
///   2. If any slot is SIdxVirt, the result type must NOT be IRTArrow —
///      virtual arrays' elements must be simple values.
///
/// Returns the empty list if the arrow is well-formed; otherwise a
/// list of human-readable error strings.
let rec validateArrowShape (slots: IRArrowSlot list) (result: IRType) : string list =
    let errs = ResizeArray<string>()
    let firstVirt =
        slots |> List.tryFindIndex (function SIdxVirt _ -> true | _ -> false)
    match firstVirt with
    | None -> ()  // No virtual slots — no constraint
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

/// Array-shaped type. Matches `IRTArrow` when slots are *uniformly* either
/// all `SIdx` (stored) or all `SIdxVirt` (virtual). Mixed-slot arrows
/// (some `SIdx` + some `SIdxVirt`, or any `SVal`) do NOT match — they
/// have no IRArrayType-equivalent encoding.
///
/// Returns an `IRArrayType` record as a view of the arrow's array shape;
/// the record exists primarily as a convenient destructuring target with
/// `.ElemType`, `.IndexTypes`, `.IsVirtual`, `.Identity` accessors. After
/// Segment 5, `IRArrayType` is a view-only type (no DU constructor wraps it).
///
/// `IsVirtual` is reconstructed from slot kinds:
///   - all SIdx → IsVirtual = false
///   - all SIdxVirt → IsVirtual = true
/// The reconstruction allocates a fresh record on each match; tolerable
/// for typecheck/codegen frequencies.
///
/// Empty-slot arrows do NOT match — they represent nullary functions
/// (per `mkFuncArrow []`). Rank-0 arrays don't exist as a type form
/// after Segment 3d: `mkArrayLike` collapses rank-0 IRArrayType inputs
/// to their element type at the producer side.
let (|ArrayElem|_|) (ty: IRType) =
    match ty with
    | IRTArrow ([], _, _) -> None  // nullary function, not an array — see docstring
    | IRTArrow (slots, result, identity) ->
        let allStored = slots |> List.forall (function SIdx _ -> true | _ -> false)
        let allVirtual = slots |> List.forall (function SIdxVirt _ -> true | _ -> false)
        if allStored || allVirtual then
            let indexTypes =
                slots |> List.map (function
                    | SIdx i -> i
                    | SIdxVirt i -> i
                    | _ -> failwith "unreachable — checked by guards above")
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
    | IRTArrow ([], _, _) -> None  // nullary function — see ArrayElem
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
    | IRTArrow ([], _, _) -> None  // nullary function — see ArrayElem
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

/// Smart constructor for a virtual array arrow. Identity is forced to
/// `None` (virtual arrays don't materialize, so there's no handle to
/// track).
///
/// **Gate**: invokes `validateArrowShape` on the constructed slot/result
/// pair and raises if any violations are reported. The most common
/// trigger is passing an `IRTArrow` as `elemType` — virtual arrays must
/// hold simple values, not nested arrays or function closures.
///
/// This is a compiler-invariant check, not a user-facing diagnostic.
/// User-facing rejection of invalid virtual-array shapes (e.g.,
/// `reverse` of an array-of-arrays) should happen earlier, in
/// TypeCheck, before reaching this constructor. If this raises, the
/// upstream type-check or lowering let an invalid shape through and
/// the error message should be treated as a bug report on that path.
///
/// `mkArrayArrow` (all-SIdx) and `mkFuncArrow` (all-SVal) don't gate
/// because their slot-construction is structurally constraint-safe:
/// without any `SIdxVirt`, neither constraint can fire. If those
/// constructors are ever changed to admit mixed slots, the gate should
/// move here too.
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

/// The κ_k component array type of a Dist<order, elem, axes> (typed dist
/// tower, ppl/NOTES.md). κ_1 is the mean tensor over the variable axes
/// as-declared; κ_k for k >= 2 is the order-k joint cumulant, symmetric
/// packed over the FUSED variable-axis space: one SymIdx record of Rank k
/// whose Extent is the product of the axes' extents (the base dimension of
/// the fused space, matching what the elaborated method_for(A ×k) comm
/// tower produces). Used by the checker to type cumulant(d, k) projections
/// and by Zonk to ERASE IRTDist into the component tuple.
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

/// All component types κ_1 .. κ_order of a Dist — the tuple a Dist value
/// erases to after type checking.
let distComponentTypes (order: int) (elem: IRType) (axes: IRIndexType list) : IRType list =
    [ for k in 1 .. order -> distComponentType k elem axes ]

/// The view transform behind `load_compound(var, mask)`: replace a variable's
/// dimensions with a single CompoundIdx whose presence mask is `maskIR`. This
/// is a pure type transform -- no data is read; materialization happens later
/// at `|> read`. The mask is an INPUT (constructed outside Blade, e.g. by
/// unlinearizing a CompoundIdx and writing NaN-filled gaps), so there is no
/// is_nan synthesis here and the Blade core stays NaN-less.
///
/// The mask covers a LEADING PREFIX of the variable's dimensions, in order,
/// matched by index-type Id (the provider shares Ids across variables within a
/// file). The masked prefix collapses into one CompoundIdx (Rank = the mask's
/// rank); any remaining variable dimensions are kept as regular trailing index
/// slots -- the formalism's `Array<T like CompoundIdx<mask>, Idx<...>>` shape
/// (4.5). All-dims coverage is the prefix case with an empty trailing, giving a
/// scalar `Compound<T, RANK>`. The mask may be ANY integer array: NetCDF has no
/// boolean type, and a flag/mask variable is stored as NC_BYTE/NC_INT and read
/// back as Int. No special bool type is required, because the load_compound
/// call is itself the signal that this variable is a mask -- the int ->
/// std::vector<bool> conversion (nonzero = present) is triggered by
/// load_compound at materialization, where compound_index_t needs it; a plain
/// read of the same variable leaves it as Int. The trailing dims are assumed
/// regular here; reordered / non-prefix masks, non-integer masks, and a ragged
/// trailing dim (variable per-cell trailing size) are rejected or deferred.
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
        // The mask must cover a LEADING prefix of the variable's dimensions, in
        // order. Remaining dims become regular trailing slots. All-dims coverage
        // is the maskRank = total case (empty trailing -> scalar Compound).
        //
        // Two dimensions "correspond" when they share INDEX-SPACE IDENTITY, not
        // when they merely have equal extents. This mirrors IR.indexSpacesMatch
        // (defined later in this file, over IndexSpaceInfo; replicated inline here
        // because compoundViewType precedes it and needs the IRIndexType-level
        // check plus the shared-Id fast path). Identity holds when:
        //   (a) same IRIndexType Id -- the provider shares one index-type
        //       instance across a file's variables (dimMap), so a mask variable
        //       and a data variable over the same NetCDF dimension match here; or
        //   (b) same non-anonymous Tag -- a user-declared NAMED index type
        //       (`type LatIdx = Idx<n>`) carries Tag = Some "LatIdx", so two
        //       source arrays annotated over the same named type match by name
        //       even though each reference gets a fresh Id; or
        //   (c) both anonymous but sharing a named-reference extent (IRVar id /
        //       IRParam name) -- same rule as indexSpacesMatch's None/None arm.
        // Deliberately does NOT match bare literal-extent equality: two anonymous
        // same-length arrays do not share an index space (formalism 14.6). This is
        // the user's contract -- they establish shared identity by NAMING the
        // index types (or via a provider); coincidental shapes are not enough.
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

// ============================================================================
// Type-pattern matching (concrete + abstract) — shared by the type-structure
// test harness and (eventually) the language server's "type of expression"
// queries and surface type-ascription checks.
// ============================================================================
//
// `matchesTypePattern pattern actual` decides whether `actual` is an INSTANCE
// of `pattern`. The pattern is the asserted/expected type; it may be CONCRETE
// (fully specified — then this is strict structural equality on the dimensions
// that define a type's identity) or ABSTRACT (containing holes that match any
// concrete filling). This is deliberately NOT `unify`:
//   - `unify` is symmetric and treats SymNone as compatible with any symmetry
//     (correct for inference, wrong for an assertion — it would accept a plain
//     Idx where AntisymIdx<2> is asserted). Here the pattern's symmetry/arity
//     must match EXACTLY unless that position is a hole.
//   - raw structural `=` is too strict: it compares extents, inference-var ids,
//     and synthetic `__` tags, none of which are part of a type's identity.
//
// Holes in the PATTERN (the abstract positions, each matching anything):
//   - IRTInfer _            : a whole-type hole (element type, etc.)
//   - IRTNat None           : an abstract type-level nat (extent placeholder)
//   - index Extent of (IRTInfer _ / IRLit-less)  : abstract extent — IGNORED
//     for matching regardless (extents are runtime values, never type identity)
// Everything else in the pattern is matched concretely against `actual`.
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
// (formalism §5.2):
//
//   Nested (uniform per arrow):
//     IRTArrow ([SIdx I; SIdx J],
//               IRTArrow ([SVal P], R, None),
//               Some id)
//
//   Flat (mixed slots in one arrow):
//     IRTArrow ([SIdx I; SIdx J; SVal P], R, Some id)
//
// Producers currently emit nested forms exclusively, so this normalizer is a
// no-op on the existing producer output. Its value is:
//   1. Defining a canonical form so type equivalence is decidable by
//      structural equality after normalization.
//   2. Future-proofing for any code path that produces mixed-slot arrows
//      (e.g., a future B-flat migration where flat IS canonical).
//   3. Making the formalism's §5.2 identity an algorithm rather than an
//      external proof obligation.
//
// `NormalizeMode` is a parameter so the canonical direction is a single
// choice-point rather than scattered convention. `ToFlat` is the eventual
// B-flat direction; currently stubbed.

/// Direction of canonical-form normalization.
///   - `ToNested`: split mixed-slot arrows at slot-kind boundaries into
///     nested uniform-kind arrows. Currently the committed canonical form.
///   - `ToFlat`: merge nested uniform-kind arrows into a single mixed-slot
///     arrow. Not yet implemented — reserved for future B-flat migration.
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

/// Normalize an IRType to the canonical form selected by `mode`. The
/// transformation walks every IRType subterm; for `IRTArrow` it splits
/// mixed-slot arrows at slot-kind boundaries (ToNested) into a chain
/// of nested uniform-kind arrows.
///
/// Identity propagation rule (ToNested split): the outermost split
/// sub-arrow inherits the original identity. All inner sub-arrows get
/// `None`. Rationale: identity tracks a stored-array handle that exists
/// at the start of the program; inner sub-arrows in a split chain are
/// either function residuals (no identity) or function-returned arrays
/// (fresh identity not known at type level).
///
/// `ToFlat` is not yet implemented and raises `notImplemented`.
let rec normalize (mode: NormalizeMode) (ty: IRType) : IRType =
    match mode with
    | ToNested -> normalizeToNested ty
    | ToFlat -> failwith "normalize ToFlat: not yet implemented (reserved for B-flat migration)"

and normalizeToNested (ty: IRType) : IRType =
    match ty with
    | IRTArrow (slots, result, idOpt) ->
        // Recurse first: normalize result, and any IRType inside SVal slots.
        // Index types (SIdx, SIdxVirt) carry IRIndexType, which doesn't
        // contain IRType members — opaque under this walker.
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
            // Mixed slots — split at kind boundaries into nested arrows.
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
        // Axes are IRIndexTypes (no IRType members) — opaque under this walker.
        IRTDist (order, normalizeToNested elem, axes)

    // Leaf types — no IRType subterms.
    | IRTScalar _
    | IRTUnit
    | IRTNat _
    | IRTNamed _
    | IRTInfer _
    | IRTGroupKeys _ -> ty

/// Structural equivalence on IRTypes, modulo the canonical (B-nested)
/// normalization. Returns `true` iff `t1` and `t2` normalize to the
/// same structural form under `normalize ToNested`.
///
/// What this currently bridges (§5.3 mixed-slot identity):
///   - flat mixed-slot arrows and their split nested forms, e.g.
///     `IRTArrow ([SIdx I, SVal A], R, _)` ≡
///     `IRTArrow ([SIdx I], IRTArrow ([SVal A], R, _), _)`.
///
/// What this does NOT currently bridge (§5.2 array identity — deferred):
///   - flat uniform-kind arrays and their nested counterparts, e.g.
///     `Array<T like I, J>` and `Array<Array<T like J> like I>` are NOT
///     equivalent here. `ToNested` only splits at slot-kind boundaries;
///     uniform-kind multi-slot is the canonical form for uniform input.
///     The §5.2 collapse becomes available when `ToFlat` is implemented
///     (B-flat migration).
///
/// It does NOT perform alpha-renaming on IRTInfer ids — those are
/// globally unique unification handles, not bound variables.
let irTypeEquiv (t1: IRType) (t2: IRType) : bool =
    normalize ToNested t1 = normalize ToNested t2

/// Tuple elem type. Arrays of tuples. Useful for structured records that
/// don't have a named type definition.
let (|TupleElem|_|) (ty: IRType) =
    match ty with
    | IRTTuple ts -> Some ts
    | _ -> None

/// Pre-specialization parameter-pack type (`Poly<T^r>`). IRTPoly carries
/// a base type and an arity variable; `specializeFunction` expands it
/// into `r` individual parameters of the base type at compile time. So
/// a Poly is semantically a tuple of base types, not a container — it
/// has no value-level representation outside the function-parameter
/// position where specialization handles it.
///
/// Consequently, `Poly` in element position (`Array<Poly<T^k> like ...>`)
/// has unclear semantics: parameter packs are resolved at specialization
/// time, but array elements are accessed at runtime. Sites that match
/// `PolyElem` should emit an explicit "not implemented" error rather
/// than silently doing the wrong thing.
///
/// The future direction (if/when Poly elements get coherent semantics)
/// is likely to desugar to nested arrays, sharing the codepath with Q3.
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

/// Flatten nested tuple types: ((α, β), γ) → (α, β, γ)
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
/// ((α, β), γ) → [α; β; γ]
let rec flattenTupleLeaves (ty: IRType) : IRType list =
    match ty with
    | IRTTuple ts -> ts |> List.collect flattenTupleLeaves
    | _ -> [ty]

// ============================================================================
// Loop Structure (For Code Generation)
// ============================================================================

// (LoopLevel, LoopNest, and CompNode types removed in Phase B6: dead code
// from a prior architecture replaced by LoopNestCodeGen / buildLoopNestCodeGen.
// The `buildLoopNest` function that produced LoopNest was also unused and
// has been removed. Removing this block also eliminated the S3-tagged
// ETFloat64 fallbacks in the elem-type derivation it contained.)

// ============================================================================
// Index Space Matching (for Partial Product Symmetry)
// ============================================================================

/// Information about an index space (for partial symmetry detection)
type IndexSpaceInfo = {
    Tag: string option
    Extent: IRExpr
    Symmetry: SymmetryClass
    Kind: DimensionKind
    SourceRank: int
}


/// Check if two index spaces are "shared" (same logical index space).
/// DIAGNOSTIC-ONLY since arc 1: nominal index-space identity is NOT a
/// symmetry license — distinct arrays over the same named index type get NO
/// triangular grouping (proofs.md shared_units_insufficient refuted the old
/// §14.6 rule; grouping requires array identity, see rawAxisGroups). Kept for
/// alignment diagnostics and future nominal-typing checks.
let indexSpacesMatch (a: IndexSpaceInfo) (b: IndexSpaceInfo) : bool =
    if a.Kind <> SDimension || b.Kind <> SDimension then false
    else
        match a.Tag, b.Tag with
        | Some tagA, Some tagB -> tagA = tagB
        | None, None ->
            // Only match on named references (variables or parameters),
            // not on anonymous literal extents. Two arrays that happen to
            // have the same length don't share an index space.
            // See §14.6: "commutativity is the license, shared index spaces
            // are the payoff" — shared means same named type, not same extent.
            match a.Extent, b.Extent with
            | IRVar (idA, _), IRVar (idB, _) -> idA = idB
            | IRParam (nA, _, _), IRParam (nB, _, _) -> nA = nB
            | _ -> false
        | _ -> false

// ============================================================================
// Loop Level Structure
// ============================================================================

/// Represents a single loop level in the nested loop structure
/// The KIND of an index type -- the classification that iteration/addressing
/// and other kind-specific logic dispatch on. Derived (not stored) from
/// Symmetry + Tag, mirroring behaviorOf. The symmetry-like classes are ONE
/// grouped arm (they share triangular/simplex storage and the fold facet via
/// behaviorOf); Compound, Dep, and Ragged are siblings with their own
/// storage/iteration. SymNone is NOT a kind: it is "no class assigned", which
/// resolves to a plain dense index only when no tag claims it. Ragged variants
/// (inline/opaque) and Dep sub-records (inner/outer) group here for now; a
/// nested match splits them where each kind's iteration is built, mirroring how
/// IxSymmetryLike defers the specific symmetry to ix.Symmetry. New kinds (Enum,
/// CG, ...) add an arm here.
let (|IxSymmetryLike|IxCompound|IxDep|IxRagged|IxDense|) (ix: IRIndexType) =
    match ix.Symmetry with
    | SymSymmetric | SymAntisymmetric | SymHermitian -> IxSymmetryLike
    | SymNone ->
        // Kind dispatch reads IxKind, never Tag strings (audit §3.3) — and
        // is exhaustive, so a new IxKind case must decide its family here.
        (match ix.IxKind with
         | IxKCompound | IxKCompoundDynamic -> IxCompound
         | IxKDep | IxKDepInner | IxKDepOuter -> IxDep
         | IxKRagged | IxKRaggedInline | IxKRaggedOpaque -> IxRagged
         | IxKPlain | IxKGroupOuter | IxKGroupMember | IxKSeq
         | IxKErrorRaggedNoPrior -> IxDense)

type LoopLevelInfo = {
    ArrayIndex: int
    LocalDimIndex: int
    RankIndex: int
    GlobalLevelIndex: int
    IndexSpace: IndexSpaceInfo
    /// Arc 1 (joint product symmetry): Some factors marks this level as the
    /// FUSION of its argument's entire plain-dense S-block into one compound
    /// axis (extent = product of the factors' extents; factors = the original
    /// S-dim records in order). Iteration decodes per-dim coordinates from the
    /// compound index (row-major = lex enumeration order). Fusion is what makes
    /// cross-argument commutative grouping sound for multi-dim identity groups:
    /// one identity group licenses only the JOINT symmetry over whole argument
    /// index tuples (docs/formalism.md §12.4, proofs.md diagonal_group_law) —
    /// never per-dimension partnering (per_dim_swap_not_symmetry), and never
    /// across distinct arrays via shared index spaces
    /// (shared_units_insufficient).
    FusedFactors: IRIndexType list option
}

/// Compute per-array S-dimension counts (accounting for arity expansion)
let computeSDimsPerArray (arrayTypes: IRArrayType list) : int list =
    arrayTypes |> List.map (fun arr ->
        arr.IndexTypes 
        |> List.filter (fun idx -> idx.Kind = SDimension) 
        |> List.sumBy (fun idx -> idx.Rank))

/// Build the flattened loop level structure
/// Build the RAW (by-array, unreordered) loop levels: one level per S-dimension
/// arity component, emitted array-by-array in index-type order. This is the
/// pre-grouping structure; product-symmetry reordering is applied separately by
/// buildLoopLevelStructure so that the grouping rule lives in exactly one place
/// (rawAxisGroups) and the reorder cannot drift from it.
let buildRawLoopLevels (arrayTypes: IRArrayType list) (sDimsPerArray: int list) : LoopLevelInfo list =
    let mutable levels = []
    let mutable globalIdx = 0
    
    for arrIdx in 0 .. arrayTypes.Length - 1 do
        let arr = arrayTypes.[arrIdx]
        let mutable localDimIdx = 0
        // Cumulative count of levels emitted for THIS array so far. RankIndex
        // must be this cumulative depth — NOT the per-record arity position —
        // because genElementBindingNew uses it as `levelsConsumed = RankIndex +
        // 1` to decide slice-vs-scalar-leaf (resultRank = ArrayRank -
        // levelsConsumed). A single multi-arity record (e.g. SymIdx<2>, one
        // record Rank 2) and a sequence of rank-1 records (e.g. dense Idx,
        // Idx — two records) BOTH span the same number of levels, so the depth
        // must increment continuously across records.
        let mutable arrLevel = 0
        
        for idx in arr.IndexTypes do
            if idx.Kind = SDimension then
                // A CompoundIdx is a SINGLE semantic axis -- it iterates its
                // present cells (cardinality), not a dense grid over the mask's
                // leadRank dimensions -- so it contributes exactly ONE loop level
                // regardless of mask rank. Symmetric/dense slots still expand one
                // level per arity component. The compacted bound and compact
                // address for the compound level are emitted by the codegen
                // consumer; SourceRank carries the mask rank for that consumer.
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

/// Arc 1 (corrected product symmetry): fuse each eligible argument's plain-
/// dense multi-level S-block into a SINGLE compound loop level, so that
/// cross-argument commutative grouping operates on whole argument index tuples
/// — the JOINT symmetry, which is the only symmetry a single identity group
/// licenses (docs/formalism.md §12.4; proofs.md diagonal_group_law). The old
/// per-dimension partnering produced SymIdx per data dimension and an (r!)^d
/// claim, both refuted (per_dim_swap_not_symmetry, counting_general_C).
///
/// Eligibility (all required):
///   - the argument sits in a comm group together with at least one OTHER
///     position holding the SAME array (identity; shared index spaces license
///     nothing: shared_units_insufficient),
///   - it contributes >= 2 S-levels, ALL plain dense rank-1 records (SymNone,
///     IxKPlain, no dependencies) — symmetric/ragged/dep/compound records do
///     not fuse (their joint form needs unrank decode; such arguments simply
///     do not group across positions), and
///   - the source is a real array (not a range/reverse virtual).
/// Identity partners share an array type, so eligibility is uniform across a
/// group: every member fuses or none does — a fused level therefore always
/// finds its partners fused.
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
            let allPlainDense =
                recs.Length = lvls.Length &&
                recs |> List.forall (fun r ->
                    r.Rank = 1 && r.Symmetry = SymNone && r.IxKind = IxKPlain &&
                    List.isEmpty r.Dependencies)
            if not isVirtual && lvls.Length >= 2 && hasIdentityPartner arrIdx && allPlainDense then
                let rep = List.head lvls
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
/// multiplicity axis of the index-type scheme:
///   (A) WITHIN one index type: consecutive arity components of a single
///       symmetric/antisymmetric record (same array, same LocalDimIndex,
///       consecutive RankIndex).
///   (B) ACROSS arguments: same comm group AND SAME ARRAY (identity) AND each
///       side's S-block is a single level (d = 1, or the fused compound level
///       from fuseJointSLevels). Corrected per docs/formalism.md §11.2/§12.4:
///       identity is REQUIRED — distinct arrays sharing a named index space
///       license nothing (proofs.md shared_units_insufficient refuted the old
///       §14.6 nominal-identity rule) — and per-dimension partnering of a
///       multi-dim identity group is unsound (per_dim_swap_not_symmetry);
///       multi-dim groups reach here only through fusion, as whole-tuple axes.
/// Both the loop reorder/iteration AND the output storage layout derive from
/// this one function, so they cannot drift apart.
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

/// Build the loop level structure: fuse joint S-blocks (fuseJointSLevels,
/// arc 1), then REORDER so that levels sharing an axis group (per
/// rawAxisGroups) are CONTIGUOUS — grouped by axis across the repeated comm
/// arguments rather than by array. Grouped output storage lays its symmetric
/// dims out adjacently (e.g. the joint SymIdx<2, Lat*Lon> spans its two fused
/// levels back-to-back); the loop nest must visit dims in the SAME order for
/// the write subscript and triangular bounds to line up with storage. Both
/// this reorder and deduceOutputType derive their ordering from rawAxisGroups,
/// so iteration and storage cannot disagree. The reorder is a STABLE group-by
/// (each group's members keep their by-array relative order), which preserves
/// per-array slice state (currentNames keyed by ArrayPosition) and
/// RankComponent. For single-axis / single-array cases every level is its own
/// or one shared group in emission order, so the reorder is an identity.
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

// ============================================================================
// Identity Group Detection
// ============================================================================

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

// ============================================================================
// Kernel Rank Analysis (irank and orank)
// ============================================================================

/// Extract input ranks from a lambda's parameter types

// ============================================================================
// Consolidated Symmetry Analysis (Section 13.1-13.2, 14.5-14.6)
// ============================================================================

/// Helper: factorial
let factorial n = 
    let rec f acc = function
        | 0 | 1 -> acc
        | n -> f (acc * int64 n) (n - 1)
    f 1L n

// (LoopLevelSymmetry type removed in Phase B6: defined but never referenced.)

/// Compute SymcomState for all loop levels
/// 
/// Two sources of triangular iteration:
/// 1. SYMMETRIC: consecutive arity components of same SymIdx index type
/// 2. COMMUTATIVE: arrays in same comm group with matching index spaces
let computeAllSymcomStates 
    (identities: ArrayIdentity list) 
    (arrayTypes: IRArrayType list)
    (commGroups: int list list)
    (sDimsPerArray: int list) : SymcomState list =
    
    let levels = buildLoopLevelStructure identities commGroups arrayTypes sDimsPerArray
    if levels.IsEmpty then []
    else
        let inSameCommGroup i j =
            commGroups |> List.exists (fun cg ->
                List.contains i cg && List.contains j cg)

        let sameArrayIdentity i j =
            i < identities.Length && j < identities.Length &&
            sameIdentity identities.[i] identities.[j]

        // Corrected commutative licensing (arc 1): identity required (shared
        // index spaces license nothing — shared_units_insufficient), and each
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

/// CANONICAL AXIS GROUPING — the single source of dimension grouping that both
/// the OUTPUT STORAGE layout (deduceOutputType) and the ITERATION layout
/// (buildLoopLevelStructure reorder / iminMap chaining) are intended to derive
/// from, so the two cannot drift out of sync (the storage-vs-iteration
/// divergence behind this session's product-symmetry bugs).
///
/// Returns one group id per loop level (parallel to `buildLoopLevelStructure`'s
/// output order). Two levels share a group iff they are joint-symmetric
/// partners — i.e. iterate/store as one higher-rank symmetric index — under
/// EITHER of the two multiplicity axes the index-type scheme allows:
///
///   (A) WITHIN one index type: consecutive arity components of a single
///       symmetric/antisymmetric record (a SymIdx<r> spans r levels at the same
///       LocalDimIndex of the same array, consecutive RankIndex).
///   (B) ACROSS arguments: the SAME ARRAY repeated in a commutative group,
///       where each occurrence's S-block is one level (d = 1, or the fused
///       compound level from fuseJointSLevels for d >= 2). Corrected (arc 1):
///       array identity is REQUIRED — nominal index-type identity across
///       distinct arrays licenses nothing (shared_units_insufficient) — and a
///       multi-dim identity group forms ONE joint group over compound tuples,
///       never one group per data dimension (per_dim_swap_not_symmetry).
let computeAxisGroups
    (identities: ArrayIdentity list)
    (arrayTypes: IRArrayType list)
    (commGroups: int list list)
    (sDimsPerArray: int list) : int list =
    // Group ids parallel to buildLoopLevelStructure's REORDERED output order,
    // using the one shared grouping rule (rawAxisGroups). Applying rawAxisGroups
    // to the already-reordered levels is well-defined: the reorder is itself a
    // stable group-by on the same rule, so contiguous same-group runs come out
    // with contiguous ids — exactly what the iteration consumers index by.
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
/// groups multiply. This is the CORRECTED accounting (arc 1): one identity
/// group over a d-dimensional array yields a single fused group of size r —
/// speedup r!, never (r!)^d (per_dim_swap_not_symmetry, counting_general_C);
/// multiplicative factors come only from DISTINCT groups (separate comm groups
/// or within-record symmetric blocks). docs/formalism.md §12.4.
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

// ============================================================================
// IR Declarations
// ============================================================================

/// Constraint information for structs with where clauses
type StructConstraintInfo = {
    /// The lowered constraint expression (e.g., m1 + m2 == m_out)
    Expr: IRExpr
    /// Mapping from field names to the IRVar IDs used in the constraint expr
    FieldBindings: (string * IRId) list
}

type IRTypeDef =
    | IRTDAlias of name: string * ty: IRType
    | IRTDStruct of name: string * fields: (string * IRType) list * invariant: StructConstraintInfo option
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
/// (`view |> read`) and consumed at codegen to emit the NetCDF reader. Keyed in
/// IRModule.ProviderReads by the receiving binding's IRId. A plain dense read
/// leaves MaskName/MaskType = None; a load_compound read carries both.
type ProviderReadSpec = {
    FilePath: string
    VarName: string
    VarType: IRArrayType
    MaskName: string option
    MaskType: IRArrayType option
}

type IRModule = {
    Name: string
    Types: IRTypeDef list
    Functions: IRFuncDef list
    Bindings: IRBinding list
    /// Diagnostics: static function usage tracking (function name → usage kind)
    /// "compile-time" | "runtime" | "both" | "unused"
    StaticFunctionUsage: Map<string, string>
    /// Deferred provider reads, keyed by the receiving binding's IRId.
    /// Populated during lowering (at `let x = view |> read` over a provider
    /// variable) and consumed at codegen to emit the NetCDF reader. Empty for
    /// modules with no provider reads.
    ProviderReads: Map<IRId, ProviderReadSpec>
    /// Deferred random-fill array constructors, keyed by the receiving binding's
    /// IRId. Populated during lowering (at `let A: Array<..> = fill_random(mod)`)
    /// and consumed at codegen to emit allocate<> + the runtime fill_random. The
    /// value is the (lowered) modulus expression. Empty for modules with none.
    RandomInits: Map<IRId, IRExpr>
    /// Deferred compound-construction constructors (compound(dense, mask)),
    /// keyed by the receiving binding's IRId. Populated during lowering and
    /// consumed at codegen to emit P0 index materialization + a dense->compact
    /// scatter. Value is (loweredDense, loweredMask). Empty for modules with none.
    CompoundInits: Map<IRId, IRExpr * IRExpr>
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


// ============================================================================
// IR Construction Helpers
// ============================================================================

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
    /// must not reuse typecheck/lowering-era ids — a synthetic binding
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

/// Build a fresh IRCallable for an inline lambda with sensible defaults
/// for the lambda case: synthesized "__lambda_<id>" name, no parallelism
/// hints, not arity-polymorphic, not static. Function-style metadata
/// fields default to their identity values so lambdas and functions can
/// share the same record type without spurious distinction.
///
/// Used by every lambda-construction site in Lowering.fs to avoid
/// repeating boilerplate. The captures list, return type, and
/// commutativity metadata are caller-supplied because they depend on
/// the specific lambda being built.
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
    : IRCallable =
    let id = builder.FreshId()
    {
        Id = id
        Name = sprintf "__lambda_%d" id
        Params = parms
        RetType = retType
        Body = body
        IsStatic = false
        IsCommutative = isCommutative
        CommGroups = commGroups
        // Lambda-level parallelism IS propagated: omp/cuda clauses on a lambda
        // flow TypedLambdaInfo.Parallel -> here. Callers supply the omp detail +
        // flag and the cuda flag + block size (mutually exclusive today).
        Parallelism = parallelism
        IsOmpParallel = isOmpParallel
        IsCudaKernel = isCudaKernel
        CudaBlockSize = cudaBlockSize
        IsArityPoly = false
        ArityParam = None
        Captures = captures
    }

/// Deduce output array type from loop application
/// According to formalism section 10.9:
/// 1. Group arrays by identity (consecutive identical arrays)
/// 2. For each group: if comm + arity > 1, use SymIdx; else Idx
///    (AntisymIdx instead of SymIdx when isReynoldsAntisym is set -- Reynolds
///     antisymmetrization over a commutative same-array group produces a
///     strictly-triangular antisymmetric output, NOT a symmetric one. The
///     antisymmetric output stores C(n,r) strict tuples with no diagonal,
///     versus C(n+r-1,r) for symmetric. This is what makes the
///     allocate_antisym storage path reachable from the common Reynolds use
///     case; without it a same-array reynolds(f, Antisymmetric) would deduce
///     symmetric storage (wrong cardinality, spurious zero diagonal).)
///    When there is NO Reynolds clause (isReynolds = false), a rank-0
///    elementwise kernel instead PRESERVES the input group's compact storage
///    class verbatim (Sym/Antisym/Hermitian), since the kernel does not reshape
///    symmetry; the Reynolds flags only apply when a Reynolds clause is present.
/// 3. Concatenate S-dims from all groups
/// 4. Add T-dims from kernel output
let deduceOutputType 
    (arrayTypes: IRArrayType list) 
    (identities: ArrayIdentity list) 
    (commGroups: int list list)
    (sDimsPerArray: int list)
    (kernelTDims: IRIndexType list)
    (elemType: IRType)
    (isReynolds: bool)
    (isReynoldsAntisym: bool)
    (kernelConsumesInner: bool)
    (builder: IRBuilder) : IRType =
    
    if arrayTypes.IsEmpty then IRTUnit
    else
        // Step 1+2: Build output S-dim index types from the SINGLE canonical
        // axis grouping (rawAxisGroups) — the same source the iteration thread
        // uses — so output storage and loop iteration cannot disagree. This
        // replaces the older array-identity grouping, which could not express
        // PARTIAL product symmetry: distinct arrays sharing an index space at
        // some positions (e.g. comm over A<Lat,Lon>, B<Lat,Depth>) must
        // symmetrize ONLY the shared axis (Lat), which is an axis-level fact,
        // not an array-level one (§14.6).
        //
        // Each axis group becomes ONE output S-dim index:
        //   - group size > 1  -> a higher-rank SYMMETRIC index (Rank = group
        //     size), or ANTISYMMETRIC under a Reynolds antisymmetrization. This
        //     covers both same-array repetition and distinct arrays sharing a
        //     named index space, plus a within-type symmetric record (whose
        //     arity components form one group of that arity).
        //   - group size == 1 -> the source index type copied VERBATIM (Id
        //     refreshed only), preserving its own Rank/Symmetry and any
        //     ragged/dep structure. This is load-bearing for a single symmetric
        //     input (method_for(sym) <@> h carries SymIdx<r,N> through) and for
        //     elementwise-over-ragged/dep (rank-0 kernel preserves input shape).
        //
        // A level's (ArrayIndex, LocalDimIndex) recovers its source IRIndexType
        // from that array's S-dim records, so the verbatim copy uses the full
        // original record (not the projected IndexSpace).
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
                        // Arc 1 JOINT output record: one symmetric index of rank
                        // groupRank over the COMPOUND extent (product of the fused
                        // argument's per-dim extents) — SymIdx<r, prod(n_j)>, the
                        // only sound output for one identity group over a
                        // multi-dim array (docs/formalism.md §8.4/§12.4). The old
                        // per-dimension SymIdx-per-dim output is refuted
                        // (per_dim_swap_not_symmetry) and cannot even hold the
                        // result (counting_general_C).
                        let groupSymmetry =
                            if isReynolds then
                                (if isReynoldsAntisym then SymAntisymmetric else SymSymmetric)
                            else SymSymmetric
                        let prodExtent =
                            factors |> List.map (fun f -> f.Extent)
                                    |> List.reduce (fun a b ->
                                        match a, b with
                                        | IRLit (IRLitInt x), IRLit (IRLitInt y) -> IRLit (IRLitInt (x * y))
                                        | _ -> IRBinOp (IRElementwise, IRMul, a, b))
                        let template = List.head factors
                        result <- result @ [{ template with
                                                Extent = prodExtent
                                                Rank = groupRank
                                                Symmetry = groupSymmetry
                                                Tag = None
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
                            // A Reynolds clause is a kernel-level unary combinator: when
                            // present it shapes the output symmetry (sym/antisym per the
                            // variant), and the input array's native storage class does
                            // not override it. With NO Reynolds, a rank-0 elementwise
                            // kernel preserves the input's compact storage class verbatim
                            // (Sym/Antisym/Hermitian); only a plain (SymNone) multi-level
                            // group defaults to symmetric.
                            let groupSymmetry =
                                if isReynolds then
                                    (if isReynoldsAntisym then SymAntisymmetric else SymSymmetric)
                                else
                                    (match rep.Symmetry with
                                     | SymSymmetric | SymAntisymmetric | SymHermitian -> rep.Symmetry
                                     | SymNone -> SymSymmetric)
                            result <- result @ [{ rep with Rank = groupRank; Symmetry = groupSymmetry; Id = builder.FreshId() }]
                        else
                            // size-1 group: verbatim copy (preserve Rank, Symmetry,
                            // ragged/dep structure), refresh Id only.
                            result <- result @ [{ rep with Id = builder.FreshId() }]
            result
            // Drop indices tagged as "consumed by the kernel" — but ONLY when
            // the kernel actually consumes an inner dimension (it has an
            // array-typed parameter of rank > 0, e.g. `lambda(g: Array<...>) ->
            // reduce(g, ...)`). In that case the kernel implicitly receives a
            // sub-array along the tagged dim and produces a per-outer-iteration
            // result, so the dim is not part of the output.
            //
            // For a rank-0 ELEMENTWISE kernel (all scalar params, e.g.
            // `lambda(e) -> e * 2`), nothing is consumed: the kernel maps each
            // scalar and the ragged/dependent inner dim must PROPAGATE to the
            // output. Dropping it there collapsed a ragged/DepIdx array to a
            // single Idx record (the elementwise-over-ragged/dep gap).
            //
            //   - __group_member        : ragged inner of group_by output
            //   - __raggedidx          : closed RaggedIdx<lens>
            //   - __raggedidx_inline   : ragged literal's inferred inner
            //   - __raggedidx_opaque   : opaque RaggedIdx<_>
            //   - __depidx_inner       : DepIdx inner (formula-driven extent)
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
            // After Phase B2, elemType is already IRType so no IRTScalar wrap.
            elemType
        else
            mkArrayArrow allDims elemType None


// ============================================================================
// Code Generation Structures
// ============================================================================
// These structures provide explicit bindings between loop indices, arrays,
// and kernel parameters to facilitate code generation.

/// Binding between a loop index and its corresponding array/parameter
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
    /// IRType to align with IRArrayType.ElemType (Phase B2).
    ArrayElemType: IRType
    /// Total rank of the array being indexed
    ArrayRank: int
    /// Virtual array kind (range, reverse, or real)
    Virtual: VirtualKind
}

/// A single loop level: how to iterate + what to peel
type LoopIndexBinding = {
    /// Loop level index (0, 1, 2, ...)
    Level: int
    /// Generated index variable name ("__i0", "__i1", ...)
    IndexName: string
    /// Extent as IRExpr (for flexible code gen)
    Extent: IRExpr
    /// Array name for non-literal extent lookup (e.g. "A" → "A_extents[dim]")
    ExtentArrayRef: string
    /// Dimension index for non-literal extent lookup
    ExtentDimRef: int
    /// List of prior level indices to subtract from bound (empty = no subtraction)
    BoundDependencies: int list
    /// Extra offset to subtract from bound (1 for antisymmetric strict i < j)
    StrictOffset: int
    /// Arc 1: Some d marks a FUSED joint level spanning d plain-dense source
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
    /// Fused-fold mode (reduce over a deferred computation): when Some,
    /// the nest ACCUMULATES `OutputName = <wrapper>(OutputName, kernel)`
    /// into a caller-declared scalar instead of assigning output cells —
    /// "+" / "*" fast-path to `+=` / `*=`. The caller declares and seeds
    /// the accumulator, forces OutputType scalar, and suppresses the
    /// nest-level OMP pragma (scalar accumulation is not race-safe).
    FoldWrapper: string option
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
/// — identical to allocate<>'s pool order, the invariant making host/device
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
/// cleanly. The CUDA launch wrapper is declared extern "C" so g++ (compiling
/// the .cpp) and nvcc (compiling the .cu) agree on an unmangled symbol; every
/// type crossing that boundary must have a stable ABI both compilers share.
/// Fundamental scalars (int32/int64/float/double/bool) qualify. EXCLUDED:
///   - ETComplex* (std::complex<...>): a C++ class template; even though
///     std::complex<double> is often layout-compatible with double[2], that is
///     NOT guaranteed across extern "C", so we don't rely on it.
///   - ETString (std::string): a non-POD C++ object — never crosses.
///   - ETUnit (void): not a data element.
///
/// FUTURE / std::complex (it matters — first-class numeric type): the eventual
/// path is DEFINED, not speculative. The C++ standard guarantees
/// std::complex<double> is layout-compatible with double[2] (array-oriented
/// access via reinterpret is well-defined), and CUDA's cuDoubleComplex /
/// cuFloatComplex share that layout. So complex support crosses the boundary as
/// a `double*` (the complex pool reinterpreted as cardinality*2 doubles); the
/// kernel operates on cuDoubleComplex over the same bytes; the inverse
/// reinterprets back. Excluded from the FIRST kernel only because it adds the
/// reinterpret-cast boundary convention + a CUDA-complex codegen mapping, which
/// shouldn't ride along on the first kernel — but it is a known, well-defined
/// extension, not a wall.
///
/// A non-boundary-safe element type makes the kernel fall back to the host loop
/// (gate, don't emit an unlinkable kernel). Uses AnyPrimElem so a unit-annotated
/// or idx-tagged primitive (e.g. a Float64 carrying a unit) is still recognized
/// by its underlying scalar.
let isCudaBoundarySafeElem (ty: IRType) : bool =
    match ty with
    | AnyPrimElem ETInt32 | AnyPrimElem ETInt64
    | AnyPrimElem ETFloat32 | AnyPrimElem ETFloat64
    | AnyPrimElem ETBool -> true
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
/// Symbolic-symmetric counts (binomial of a runtime extent) are deferred; the
/// first kernel gates on isRectangularConstBuffer so only the literal and the
/// symbolic-rectangular paths are reachable today.
let bufferGroupCardinality (g: BufferDimGroup) : IRExpr =
    match g.Extent with
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
            | PlaceCombinatorial _ -> binomI64 (n + r - 1L) r
            // Unreachable: placementClassOf yields only Dense/Combinatorial, and a
            // compound carries an IRCompoundMask extent (taken by `| other` below,
            // returning the runtime-cardinality expr), so this literal-extent branch
            // is never tabulated. Defensive fallback to the literal count.
            | PlaceTabulated -> n
        IRLit (IRLitInt count)
    | other -> other

/// Total pool cardinality as an IRExpr (product of per-group factors — product
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

/// Build symmetry vector from output type
/// Consecutive equal values indicate symmetric dimensions
/// True iff a symmetry vector encodes ANY actual symmetry — i.e. two adjacent
/// positions share a group number (a symmetric/antisymmetric block). A purely
/// rectangular output yields all-distinct consecutive groups (e.g. [1;2;3]),
/// which is NOT symmetry and must be treated as "no symmetry" (pass nullptr to
/// allocate). This matters for MSVC: a rectangular rank-2+ output was getting a
/// non-empty vec like [1;2], which took the named-static-array allocate path and
/// hit C2131 (address of a function-local static isn't a constant). Routing the
/// no-real-symmetry case to nullptr fixes that and is semantically identical
/// (allocate treats null SYMM and all-distinct SYMM the same: full rectangular).
let hasRealSymmetry (symmVec: int list) : bool =
    symmVec
    |> List.pairwise
    |> List.exists (fun (a, b) -> a = b)

let buildSymmVec (outputType: IRType) : int list =
    match outputType with
    | ArrayElem arr ->
        let mutable symmVec = []
        let mutable groupNum = 1
        let mutable prevSymm = None
        
        for idx in arr.IndexTypes do
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
/// singleton group per dim) because the legacy antisym path used a SEPARATE
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

/// Storage allocation, derived from an output array's index TYPE (not from the
/// kernel's Reynolds descriptor). The per-index-class allocator comes from
/// allocRoutineFor (the placement axis):
///   - AllocDense / AllocSymmetric -> allocate<T, SYMM>(...)
///       (SYMM = nullptr for dense, hoisted {1,1,..} vec for symmetric;
///        Hermitian shares the symmetric path — same upper-triangle storage)
///   - AllocAntisymmetric -> allocate_antisym<T>(...)  (strict simplex, no mask)
///
/// CRITICAL distinction from LoopNestCodeGen.IsAntisymmetric: that flag comes
/// from the Reynolds descriptor and describes the COMPUTATION (sign alternation
/// on permutation parity). It is orthogonal to STORAGE: a kernel may
/// antisymmetrize its arithmetic while writing a rectangular output, or an
/// antisymmetric-typed output may be filled by a non-Reynolds kernel. Allocation
/// must key off storage, so it reads the output index type here.
///
/// The runtime allocate_antisym applies the strict shrink at EVERY pointer
/// depth uniformly (no per-group mask), so it is only correct for a SINGLE
/// antisymmetric index spanning all dimensions. A type annotation always
/// produces exactly that shape, so a mixed antisym+free output cannot arise from
/// the front end today. AllocUnsupported is returned defensively if one ever
/// does, so callers fail loudly (no silent mis-allocation).
// ============================================================================
// Index-type behavior interface (the storage-class abstraction).
//
// Each index-type CLASS (Rectangular / Symmetric / Antisymmetric / Hermitian,
// and later Compound / Tree / Graph / CG) populates one stateless behavior
// object. The behavior is keyed on the index type's SymmetryClass and DERIVED,
// never stored, so the class and its behavior cannot drift: change the
// SymmetryClass and the behavior follows automatically (see `behaviorFor`).
//
// The methods return BACKEND-NEUTRAL descriptors (AllocSpec, TransposeBehavior),
// never C++ strings — the IR stays backend-agnostic (a Python backend would
// consume the same descriptors). A per-backend emitter (in CodeGen for C++)
// turns the descriptors into concrete code. This mirrors how allocate/linearize
// are pre-rolled runtime routines the codegen merely CALLS rather than computes.
//
// This is introduced ADDITIVELY here: the existing scattered `match Symmetry`
// dispatch (emitAllocRhs, transpose typecheck, etc.) is migrated onto this
// interface in subsequent steps, each verified against the test suite.
// ============================================================================

/// Names a runtime allocation routine + how its symmetry mask is supplied.
/// Backend-neutral: the C++ emitter maps AllocDense/AllocSymmetric ->
/// `allocate<T,SYMM>` and AllocAntisymmetric -> `allocate_antisym<T>`.
type AllocSpec =
    | AllocDense                       // rectangular: allocate<T, nullptr>
    | AllocSymmetric                   // triangular upper: allocate<T, SYMM-vec>
    | AllocAntisymmetric               // strict simplex: allocate_antisym<T>
    | AllocPerGroupStrict of strict: int list
                                       // mixed strictness across groups: a
                                       // companion STRICT[] mask parallel to the
                                       // SYMM-vec (1 = group drops its diagonal /
                                       // strict, 0 = inclusive or dense). Emits
                                       // allocate_strict<T, SYMM, STRICT>. Arises
                                       // from antisym fission leaving a residual
                                       // antisymmetric sub-group beside a freed
                                       // dense axis (e.g. Idx -> AntisymIdx<2>).
    | AllocUnsupported of reason: string

/// Placement-axis allocator dispatch: which runtime allocator backs an array
/// whose storage is governed by a given PlacementClass. This is the Level-1
/// counterpart to behaviorFor (the symmetry axis) -- the allocator is a property
/// of PLACEMENT (which tuples are stored and how they rank), not of the value
/// transform, so it lives here rather than on IIndexTypeBehavior. Behavior is
/// identical to the per-class AllocRoutine it replaces: dense -> AllocDense;
/// strict combinadic (antisym) -> AllocAntisymmetric; inclusive combinadic
/// (symmetric AND Hermitian, which share the upper-triangle layout) ->
/// AllocSymmetric. A future PlaceTabulated arm adds the tabulated allocator here
/// (and FS0025 flags this match until it does).
let allocRoutineFor (pc: PlacementClass) : AllocSpec =
    match pc with
    | PlaceDense -> AllocDense
    | PlaceCombinatorial SymAntisymmetric -> AllocAntisymmetric
    | PlaceCombinatorial _ -> AllocSymmetric
    | PlaceTabulated ->
        // Compound storage is runtime-sized from the mask's popcount and is
        // allocated through the emitted compound_index_t, not a closed-form
        // allocator. Wired at codegen; unreached here until then (no caller
        // passes PlaceTabulated yet -- classifyOutputStorage routes compound
        // separately at codegen time).
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
/// (stored) representative — the FOLD phase of a lazy read (formalism 4.16,
/// 14.2). Backend-neutral; the C++ emitter realizes each as inline fold code.
///   CanonNone    — rectangular / freed axis: indices are already canonical,
///                  no reorder, always stored (identity fold).
///   CanonSort    — symmetric / Hermitian: sort within the group, track swap
///                  parity, always stored (diagonal kept).
///   CanonSortStrict — antisymmetric: sort within the group, track parity, AND
///                  return "not stored" (implicit zero) on any repeated index
///                  (the dropped diagonal / strict-simplex storage).
type CanonicalizeBehavior =
    | CanonNone
    | CanonSort
    | CanonSortStrict

/// What transform a lazy read applies to the fetched canonical value given the
/// fold's swap parity — the TRANSFORM phase (formalism 4.16). Backend-neutral.
///   TfIdentity         — symmetric / rectangular: value unchanged on swap.
///   TfNegateOnSwap     — antisymmetric: negate when swap parity is odd.
///   TfConjugateOnSwap  — Hermitian: conjugate when swap parity is odd
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

// Shared stateless singletons (one per class).
let private rectangularBehavior = RectangularBehavior() :> IIndexTypeBehavior
let private symmetricBehavior = SymmetricBehavior() :> IIndexTypeBehavior
let private antisymmetricBehavior = AntisymmetricBehavior() :> IIndexTypeBehavior
let private hermitianBehavior = HermitianBehavior() :> IIndexTypeBehavior

/// Total, exhaustive resolver from symmetry class to behavior. Adding a new
/// SymmetryClass case forces a new arm here (compile error otherwise) — the
/// openness guarantee: a new index-type class is "write a behavior + one arm".
let behaviorFor (sym: SymmetryClass) : IIndexTypeBehavior =
    match sym with
    | SymNone -> rectangularBehavior
    | SymSymmetric -> symmetricBehavior
    | SymAntisymmetric -> antisymmetricBehavior
    | SymHermitian -> hermitianBehavior

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
    | SymSymmetric | SymAntisymmetric | SymHermitian -> Some sym
    | SymNone -> None

/// Validate an index type against its class's well-formedness rules. Smart
/// constructors route through this; a future migration can make IRIndexType
/// only constructible via these guarded builders.
let validateIndexType (ix: IRIndexType) : Result<unit, string> =
    (behaviorOf ix).Validate ix

/// Storage allocation spec for an output array, derived from its index TYPE.
/// Source of truth for which C++ allocator to emit. The per-index-class
/// decision comes from allocRoutineFor (the placement axis); the whole-array
/// COMPOSITION rules (a single antisymmetric index spanning all dims is
/// allocatable; antisym mixed with other components is not, since
/// allocate_antisym has no per-group mask) live here, because they are a
/// property of the array's index-list combination, not of any one class.
let classifyOutputStorage (outputType: IRType) : AllocSpec =
    match outputType with
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
            // storage block — the mixed-strictness layout the global DIAGONALS
            // flag cannot express, but the per-group STRICT mask can. This is
            // the compact-residual fission shape (e.g. Idx -> AntisymIdx<2>:
            // a freed dense axis beside a strict residual pair). Each group is
            // uniformly strict (antisym) or dense, so buildSymmVecWithStrict
            // produces a well-formed (SYMM, STRICT) pair; emit allocate_strict.
            // (Sign is handled lazily on read via canon_*, not here.)
            let (_symmVec, strictVec) = buildSymmVecWithStrict outputType
            AllocPerGroupStrict strictVec
    | _ -> AllocDense

// ============================================================================
// Cross-procedural analysis context. All callable references in IR are
// IRVar(callable.Id, funcType); resolveCallable threads them back to
// the underlying IRCallable via the CallablesTable installed in the
// AsyncLocal context. Consumers (buildLoopNestCodeGen, validator's
// ApplyInfo check, mask-rewrite, exprAttrs IRApp arm) all share this
// resolution path.
// ============================================================================

type CallablesTable = Map<IRId, IRCallable>

type AnalysisContext = {
    Callables: CallablesTable
    Visited:   Set<IRId>
    /// Per-codegen-pass registry of transient synthetic callables.
    /// These are created during codegen by transformations that need
    /// to express "callable with modified body" — e.g.
    /// applyFunctorWrappers' inline-wrap or the IRIf guard wrap —
    /// without storing them in module.Functions (they're consumed
    /// immediately by buildLoopNestCodeGen for inline emission and
    /// don't need C++ function emission). `resolveCallable` queries
    /// this registry alongside the module's CallablesTable. The
    /// registry is mutable (Dictionary, not Map) for cheap in-place
    /// accumulation; it's a per-flow AsyncLocal field, so concurrent
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
/// is reset to a fresh empty Dictionary — each module compilation
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
/// like any other callable reference — resolveCallable will find
/// the registered version via the SyntheticCallables registry.
let registerSyntheticCallable (callable: IRCallable) : IRExpr =
    let ctx = currentAnalysisCtx ()
    ctx.SyntheticCallables.[callable.Id] <- callable
    let paramTypes = callable.Params |> List.map (fun p -> p.Type)
    let funcType = mkFuncArrow paramTypes callable.RetType
    IRVar (callable.Id, funcType)

/// Resolve an expression at a "callable position" to the underlying
/// IRCallable. Handles:
///   - `IRVar(id, _)` where id resolves in the CallablesTable
///     (module.Functions + let-binding aliases): returns Some c.
///   - `IRVar(id, _)` where id resolves in the SyntheticCallables
///     registry (codegen-internal synthetic callables): returns
///     Some c.
///   - Anything else (or unresolvable IRVar): returns None.
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

// ============================================================================
// Reynolds peel/resolve helpers
//
// The kernel slot of an ApplyInfo (and of functor-wrapper composition
// sites) may be either a bare callable reference (`IRVar(id, _)`) or
// that same reference wrapped in `IRReynolds(_, isAntisymmetric)`.
// Several passes need to look through the optional Reynolds wrapper to
// reach the underlying callable. Before consolidation each site
// open-coded the peel + resolveCallable dance, with subtly different
// tuple shapes. These three helpers express the common patterns.
// ============================================================================

/// Captures the flags carried by an `IRReynolds` wrapper. For
/// non-Reynolds kernels both flags are `false`. The invariant
/// `not HasReynolds ⇒ not IsAntisymmetric` is preserved by construction
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
    
    // Extract kernel info through `resolveKernel`, which peels any
    // `IRReynolds` wrapper and resolves the inner callable via both
    // module.Functions references and let-binding aliases in the
    // CallablesTable + synthetic registry. The Reynolds wrapper's
    // `isAntisymmetric` flag is captured in the descriptor.
    let (kernelParams, kernelBody, commGroups, captures, isAntisymmetric) =
        match resolveKernel info.Kernel with
        | Some rk ->
            (rk.Callable.Params, rk.Callable.Body, rk.Callable.CommGroups,
             rk.Callable.Captures, rk.Reynolds.IsAntisymmetric)
        | None -> ([], IRLit IRLitUnit, [], [], false)

    // Opt-in parallelism: the loop nest is parallelized ONLY if the resolved
    // kernel callable requested OpenMP via an `omp(...)` clause. No clause =>
    // serial (the language default, like C/Rust). This replaces the earlier
    // structural rule (isParallel = level=0 unconditionally), which parallelized
    // everything. The genNestPragma strategy logic (collapse vs. dynamic) is
    // preserved as the IMPLEMENTATION of "how to parallelize once omp is asked".
    let kernelRequestedOmp =
        match resolveKernel info.Kernel with
        | Some rk -> rk.Callable.IsOmpParallel
        | None -> false
    
    // Map kernel params to (source, slot). A VIRTUAL source (range<...>) consumes
    // one param PER index-type slot; every other source consumes one. This mirrors
    // buildApplyInfo's expandedRows so param indices line up. The flat param index
    // for (arrayPos, slot) is (sum of spans of earlier sources) + the slot. For
    // single-slot sources every span is 1, so paramStart pos == pos and the
    // mapping is identical to the old one-param-per-position scheme.
    let isVirtualSrc pos =
        pos < arrays.Length &&
        (match arrays.[pos] with IRRange _ | IRVirtualReverse _ -> true | _ -> false)
    let paramSpan pos =
        if isVirtualSrc pos && pos < arrayTypes.Length then
            max 1 (arrayTypes.[pos].IndexTypes |> List.sumBy (fun ix -> ix.Rank))
        else 1
    let paramStart pos = List.init (max 0 pos) paramSpan |> List.sum
    
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
        let virtualKind =
            if arrayPos < arrays.Length then
                match arrays.[arrayPos] with
                | IRRange (_, offset) -> VirtualRange offset
                | IRVirtualReverse _ -> VirtualReverse
                | _ -> RealArray
            else RealArray
        // Per-slot param for a virtual source (range<...>): the flat param index
        // is (sum of earlier sources' param spans, via paramStart) + this level's
        // position WITHIN its source. That within-source position is the rank
        // component (levelInfo.RankIndex): it advances once per loop level and
        // resets per source (buildRawLoopLevels' arrLevel), so a rank-1 slot and
        // each arity component of a multi-rank slot both map to consecutive
        // params -- range<SymIdx<2,N>> yields two params (i, j) at RankIndex 0, 1.
        // (Previously this used dimIndex = LocalDimIndex, which is shared across
        // the rank components of a single multi-rank index type; that collapsed
        // all of them onto the first param and left the rest undeclared -- the
        // range<SymIdx<2,N>> "__v3 not declared" bug. LocalDimIndex == RankIndex
        // for every rank-1 slot, so this only changes the multi-rank case.)
        // Real / non-virtual sources resolve to paramStart pos (else branch).
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
        }
    
    let bindings =
        if info.IsCoIteration then
            // Co-iteration: build levels from shared index type, all arrays peel at every level
            let sharedIdx = info.SharedIndexType
            match sharedIdx with
            | Some idx ->
                let numLevels = idx.Rank
                let isSymmetric = idx.Symmetry = SymSymmetric
                let isAntisymmetric = idx.Symmetry = SymAntisymmetric
                let isTriangular = isSymmetric || isAntisymmetric
                
                [0 .. numLevels - 1] |> List.map (fun level ->
                    let indexName = sprintf "__i%d" level
                    let deps = if isTriangular && level > 0 then [0 .. level - 1] else []
                    let strictOffset =
                        if isTriangular && isAntisymmetric then level
                        else 0
                    // Outer level is the parallelization candidate, but ONLY if
                    // the kernel opted into OpenMP via an `omp(...)` clause. No
                    // clause => serial (the default). Triangularity does not veto
                    // it: the outermost loop of a triangular nest is independently
                    // parallelizable (each outer index owns a disjoint sub-slab);
                    // genNestPragma picks the safe strategy (collapse vs dynamic).
                    let isParallel = level = 0 && kernelRequestedOmp
                    let state =
                        if isTriangular && level > 0 then SCSymmetric
                        else SCNeither
                    // Reference first real array for extent lookups
                    let refArrayName = if arrayNames.Length > 0 then arrayNames.[0] else "arr0"
                    // All arrays peel at this level
                    let elements =
                        [0 .. arrayNames.Length - 1] |> List.map (fun arrIdx ->
                            mkElement arrIdx level level)
                    {
                        Level = level
                        IndexName = indexName
                        Extent = idx.Extent
                        ExtentArrayRef = refArrayName
                        ExtentDimRef = 0  // Shared index: all dims have same extent
                        BoundDependencies = deps
                        StrictOffset = strictOffset
                        FusedRank = None
                        IsParallel = isParallel
                        State = state
                        Elements = elements
                    })
            | None -> []  // Should not happen
        else
            // Outer product: one element per level
            let loopLevels = buildLoopLevelStructure identities commGroups arrayTypes sDimsPerArray
            let triangularLevels = info.TriangularLevels
            let symcomStates = info.SymcomStates
            
            // Compute the iminMap from the single canonical axis grouping
            // (computeAxisGroups), so chaining stays in lock-step with the
            // triangular-level detection, the loop reorder, and the output
            // storage layout. Each level either:
            //   - is the FIRST member of its axis group -> root (maps to itself,
            //     iterated fully), or
            //   - chains to the NEAREST EARLIER level sharing its axis group ->
            //     descends triangularly relative to it.
            // The grouping encodes the CORRECTED symmetry rule (arc 1): a
            // repeated array under comm forms ONE rank-r simplex over its
            // (possibly fused compound) S-axis — the joint symmetry, r! once —
            // and distinct groups (separate comm groups, within-type symmetric
            // records) stay independent and multiply. Never per-dimension for
            // one group, never across distinct arrays via shared index types
            // (docs/formalism.md §12.4).
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
                // Outer level is the parallelization candidate, but ONLY if the
                // kernel opted into OpenMP (see shared-index path note above);
                // genNestPragma picks collapse vs. dynamic from bound structure.
                let isParallel = level = 0 && kernelRequestedOmp
                // Strict (j > i > ...) bounds are required whenever the OUTPUT
                // storage is antisymmetric — strict-triangular storage has no
                // diagonal, so the iteration must not visit it. Two ways the
                // output is antisymmetric:
                //   (1) the input index type is itself SymAntisymmetric (an
                //       explicit AntisymIdx array), reflected in IndexSpace, or
                //   (2) this application is a Reynolds antisymmetrization over a
                //       commutative group (isAntisymmetric, from the Reynolds
                //       descriptor). Here the INPUT arrays are plain (SymNone) —
                //       the triangular iteration comes from the commutative path
                //       — so IndexSpace.Symmetry alone would miss it.
                // The strict offset is CUMULATIVE across the group: level a (the
                // a-th index within the strict group, 0-based) must start a slots
                // past the group base, because each prior index already consumed
                // one diagonal slot. That cumulative depth equals the number of
                // bound-dependency levels this level carries (List.length deps):
                // level 1 -> 1, level 2 -> 2, etc. (A flat offset of 1 is correct
                // only at rank 2, where level 1 is the sole strict level; at rank
                // >= 3 a flat 1 under-shifts, making the loop visit non-canonical
                // tuples with repeated indices that alias storage cells — the
                // antisym rank-3 storage-collision bug.)
                let strictOffset =
                    if isTriangular &&
                       (levelInfo.IndexSpace.Symmetry = SymAntisymmetric || isAntisymmetric)
                    then List.length deps
                    else 0
                
                // A compound VIRTUAL source (range<CompoundIdx<m>>) is ONE loop
                // level (present-cell axis) but spans SourceRank kernel params
                // (one per mask dimension, per the rank rule / expandedRows).
                // Emit one element PER rank component at this single level so
                // every coordinate param gets bound -- each element extracts
                // component rc of the cell tuple (genElementBindingNew's
                // compound VirtualRange arm: unhash(r)[rc]). A real compound
                // ARRAY keeps the single peel element (it reads .data[r], not
                // per-axis coordinates).
                let elements =
                    let isCompoundLevel =
                        match levelInfo.IndexSpace.Extent with IRCompoundMask _ -> true | _ -> false
                    let isVirtualSource =
                        arrayPos < arrays.Length &&
                        (match arrays.[arrayPos] with IRRange _ -> true | _ -> false)
                    match levelInfo.FusedFactors with
                    | Some factors ->
                        // Fused JOINT level (arc 1): one element per source dim;
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
    }

// ============================================================================
// Canonical expression traversal — ExprShape (audit §3.2)
// ============================================================================
//
// THE one place that knows every IRExpr variant's immediate expression
// children. Every generic walker (mapIRExpr, collectVarRefsIR,
// collectTypesInExpr, exprAttrs) is a fold over this shape, so a new IRExpr
// variant is added in exactly one enumeration — this one. The match is
// deliberately wildcard-free: a new variant fails to compile until its shape
// is declared here, which is the exhaustiveness guarantee the old per-walker
// `| _ ->` fallbacks silently destroyed.
//
// Scope decisions (uniform across all walkers by construction):
//   - IRIndexType is OPAQUE to expression traversal. Extent-marker
//     expressions living inside an IRIndexType are reached by the dedicated
//     extent paths, never by generic traversal — matching the long-standing
//     behavior of mapIRExpr.
//   - Boundary pads and range offsets ARE children: the BndPad expression in
//     IRShift/IRAlign and IRRange's offset are real sub-expressions. (The old
//     hand-maintained walkers disagreed about these — mapIRExpr skipped them,
//     exprAttrs walked the pads — the canonical shape includes them.)
//
// `rebuild` requires exactly the children it handed out (same count, same
// order); anything else is a hard failure, never a silent drop.

/// Child-list mismatch in a rebuild — always a walker bug, never recoverable.
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
    | IRArrayNegate e -> [e], (function [e'] -> IRArrayNegate e' | _ -> badChildren "IRArrayNegate")
    | IRArrayConjugate e -> [e], (function [e'] -> IRArrayConjugate e' | _ -> badChildren "IRArrayConjugate")
    | IRReverse (e, d) -> [e], (function [e'] -> IRReverse (e', d) | _ -> badChildren "IRReverse")
    | IRDiag e -> [e], (function [e'] -> IRDiag e' | _ -> badChildren "IRDiag")
    | IRRank e -> [e], (function [e'] -> IRRank e' | _ -> badChildren "IRRank")
    | IRExtent (e, d) -> [e], (function [e'] -> IRExtent (e', d) | _ -> badChildren "IRExtent")
    | IRRaggedLookup e -> [e], (function [e'] -> IRRaggedLookup e' | _ -> badChildren "IRRaggedLookup")
    | IRCompoundMask e -> [e], (function [e'] -> IRCompoundMask e' | _ -> badChildren "IRCompoundMask")
    | IRCompoundProject (e, plen) -> [e], (function [e'] -> IRCompoundProject (e', plen) | _ -> badChildren "IRCompoundProject")
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
    | IRAssign (t, v) -> [t; v], (function [t'; v'] -> IRAssign (t', v') | _ -> badChildren "IRAssign")
    | IRCurry (arr, idx, r) -> [arr; idx], (function [arr'; idx'] -> IRCurry (arr', idx', r) | _ -> badChildren "IRCurry")
    | IRGram (l, r, same) -> [l; r], (function [l'; r'] -> IRGram (l', r', same) | _ -> badChildren "IRGram")
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
/// dispatcher is asking — so a new binding variant needs exactly one case
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

// ============================================================================
// Expression Mapping (bottom-up rewriter)
// ============================================================================

/// Apply f to every sub-expression bottom-up, then to the root.
/// f should return the expression unchanged for cases it doesn't handle.
/// Generic recursion is a fold over ExprShape — variant-specific structure
/// lives entirely in the shape enumeration above.
let rec mapIRExpr (f: IRExpr -> IRExpr) (expr: IRExpr) : IRExpr =
    let mapped =
        match expr with
        | ExprShape ([], _) -> expr
        | ExprShape (children, rebuild) -> rebuild (children |> List.map (mapIRExpr f))
    f mapped

/// Collect every variable id referenced (IRVar) anywhere in an expression.
/// The one var-ref collector (audit §3.2 [now]: CodeGen's hand-maintained
/// duplicate `collectVarRefs` is gone; capture computation and match-case
/// usage checks in CodeGen call this). Recursion is the ExprShape fold, so
/// no variant's subtree can be silently skipped the way the old
/// `| _ -> Set.empty` catchalls did.
///
/// Scoping contract: only IRForRange subtracts its binder — its loop var is
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

// ============================================================================
// HM Type Substitution
// ============================================================================
//
// Substitutes IRTInfer occurrences with concrete types throughout types and
// expressions. Pure structural substitution — no rewrites, no expansion.
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

/// Substitute IRVar references throughout an expression tree. For each
/// IRVar(id, _) where `mapping` has an entry, replace the node with the
/// mapped expression. Used when importing a called function's probes
/// into a caller's analysis: the probe's BuildOn references the
/// function's formal parameters, and at the call site we want those
/// references resolved to the actual argument expressions.
///
/// Walks bottom-up via mapIRExpr; only IRVar nodes are affected, and
/// only those whose id appears in `mapping`. Other variant types (IRApp,
/// IRBinOp, etc.) carry their own substituted children automatically.
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
            // ApplyInfo carries three type-bearing pieces that must be
            // substituted in lockstep with the rest of the expression
            // tree: ArrayTypes (each has an ElemType that may be a type
            // variable referencing the surrounding function's T) and
            // OutputType (the deduced result-array type, often a fresh
            // IRTArray whose ElemType is the same T). Skipping these
            // leaves stale IRTInfer in spec function bodies, which the
            // IR validator flags as "unresolved type variable in body"
            // for the HM specialization.
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

/// Types carried directly on a node — no reconstruction, no environment.
/// The shared first tier of the canonical typing (audit §2.2): the whole of
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

/// Get the type of an IRExpr where determinable from the node directly —
/// deliberately the CarriedType tier only, NOT the full typeOf
/// reconstruction. Used at HM call sites to extract arg types for
/// unification against param types: a reconstructed type could carry
/// pre-substitution type variables, so only node-carried types are safe to
/// unify with. For anything else this returns None, and the call site falls
/// back to the function's declared type-var positions; they'll be
/// substituted with whatever else unification provides.
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
    | IRTInfer n, t when acc.[n] = t -> acc  // Consistent reuse — fine
    | IRTInfer _, _ -> acc  // Inconsistent — leave as-is; the IR validator will catch it
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
        // Identity is ignored for unification — it's metadata, not type.
        let unifySlot acc' (p, a) =
            match p, a with
            | SVal pt, SVal at -> unifyParamWithArg pt at acc'
            | SIdx _, SIdx _ -> acc'
            | SIdxVirt _, SIdxVirt _ -> acc'
            | _ -> acc'
        let acc' = List.zip pSlots aSlots |> List.fold unifySlot acc
        unifyParamWithArg pRet aRet acc'
    | _ -> acc  // Concrete types or unhandled compound — no bindings learned

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
/// monomorphization — only such functions need specialization.
let hasTypeVarsInSignature (func: IRFuncDef) : bool =
    let paramIds =
        func.Params |> List.fold (fun s p -> Set.union s (collectInferIds p.Type)) Set.empty
    let retIds = collectInferIds func.RetType
    not (Set.isEmpty paramIds && Set.isEmpty retIds)

// ============================================================================
// HM Monomorphization
// ============================================================================
//
// Generates specialized copies of functions with free type variables in
// their signatures, one per unique call-site type pattern. Sibling pass
// to the existing Arity (Poly) monomorphization. Runs before Poly so that
// type-substitution happens at the abstract signature level, then Poly
// expands param packs against concrete element types.
//
// Architecture mirrors monomorphizeModule's 5-phase shape: identify,
// collect call sites with bindings, specialize, build rewrite map,
// rewrite all expressions. The key difference vs Poly: SpecRequest carries
// (typeVarId → concreteType) bindings rather than a single arity int.

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

    // Stage 3c.3 follow-on: lifted lambdas that capture HM-polymorphic
    // params need to be cloned-and-specialized alongside their
    // enclosing function. Pre-3c.3 the lambda was inline in the
    // enclosing function's body so the existing `mapIRExpr` walk over
    // `bodyWithTypes` covered everything in one pass: the lambda's
    // body, captures, and the VarId remap all got reached. Post-3c.3
    // the lambda lives in `module.Functions` and the body only carries
    // an `IRVar(lambdaId, _)` reference; the lambda's body and its
    // Captures.Type fields still hold the unsubstituted T type, and
    // its Captures.Id still points at the pre-spec function's param
    // VarIds. Both checks fail validation: dangling VarId (the spec
    // function's params have new ids) AND unresolved IRTInfer (T
    // never got substituted in the lambda).
    //
    // Fix: for each lifted callable the body references whose
    // captures intersect this function's params, clone it. The clone
    // gets fresh ids for its own params, has captures' Ids and types
    // remapped via `varIdRemap` and `bindings`, body's IRVar refs
    // remapped via the combined map. The original lambda stays in
    // module.Functions unchanged — other specializations or the
    // unspecialized form (which doesn't survive monomorphization
    // anyway) would build their own clones.
    let origParamIds = func.Params |> List.map (fun p -> p.VarId) |> Set.ofList
    let lambdaClones = System.Collections.Generic.Dictionary<IRId, IRCallable>()
    let needsClone (c: IRCallable) : bool =
        c.Captures |> List.exists (fun cap -> Set.contains cap.Id origParamIds)
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
    // Name-mangle by binding signature: f__T_double__U_int64 etc.
    let mangleType ty =
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
        | _ -> "T"  // Fallback for compound types — rare in practice
    let suffix =
        bindings
        |> Map.toList
        |> List.sortBy fst
        |> List.map (fun (id, ty) -> sprintf "_%d_%s" id (mangleType ty))
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
/// This is an *iterative* fixpoint algorithm, not single-pass. The reason
/// is that specialization can expose new concrete types: when we specialize
/// `twiceId(x: T) -> T = id(id(x))` with `T → Int64`, the substitution
/// makes the inner `id(x)` call's arg type concrete (Int64), which then
/// licenses specializing `id` itself with `T → Int64`. A single pass would
/// see `id(x)`'s arg as `IRTInfer 10001` (still abstract before substitution)
/// and either skip the binding or produce a useless self-binding spec.
/// The loop runs until no new (funcId, bindings) keys appear.
///
/// The algorithm also (a) substitutes call-site-learned type bindings into
/// the binding's *declared type* (`IRBinding.Type`), since TypeCheck leaves
/// it as `IRTInfer N` when the call site's return type was polymorphic;
/// and (b) substitutes types throughout each spec function's body so that
/// post-specialization their bodies are free of `IRTInfer`.
let monomorphizeHMFunctions (modul: IRModule) (builder: IRBuilder) : IRModule =
    // 1. Identify functions with type vars in signature
    let hmFuncs = modul.Functions |> List.filter hasTypeVarsInSignature
    if hmFuncs.IsEmpty then modul
    else
    let hmFuncMap = hmFuncs |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let hmFuncIdSet = hmFuncs |> List.map (fun f -> f.Id) |> Set.ofList

    // 2. Iterate to fixpoint: each round may discover new specializations
    //    by inspecting both the original module's expressions AND the bodies
    //    of specs generated in earlier rounds (those bodies may contain HM
    //    calls whose arg types only became concrete after substitution).
    let mutable specMap : Map<IRId * (int * IRType) list, IRFuncDef> = Map.empty
    // Stage 3c.3: cloned lambdas accumulated across spec generation.
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
            let key = (funcId, sortedBindings)
            // Only generate specs whose bindings are entirely concrete.
            // A self-binding like (10001, IRTInfer 10002) means the call
            // site was inside a still-abstract context (e.g. the original
            // body of `twiceId` before its own specialization). Such a
            // spec would carry unresolved IRTInfer through to the
            // validator. The fixpoint will revisit this call site after
            // the surrounding spec is generated and its body's types
            // become concrete; at that point the bindings are fully
            // concrete and we generate the real spec.
            let allConcrete =
                sortedBindings |> List.forall (fun (_, v) ->
                    match v with IRTInfer _ -> false | _ -> true)
            if allConcrete && not (Map.containsKey key specMap) then
                let origFunc = hmFuncMap.[funcId]
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
            match Map.tryFind (funcId, sortedBindings) specMap with
            | Some spec ->
                IRApp (IRVar (spec.Id, mkFuncArrow (spec.Params |> List.map (fun p -> p.Type)) spec.RetType),
                       args, spec.RetType)
            | None -> e
        | _ -> e

    // 4. Build a *global*, conflict-free type-var binding map for the
    //    whole module. The motivation: a downstream binding like
    //    `let r = result(0)` references the same IRTInfer N as the
    //    upstream `let result = arr_id(xs)` (because TypeCheck propagates
    //    the function's polymorphic return type through to dependents
    //    without generalizing top-level functions). Per-binding-local
    //    substitution wouldn't fix r.Type because r.Value contains no
    //    HM call directly. A global union does, with the caveat that we
    //    must detect conflicts: if the same ID is bound to different
    //    concrete types at different call sites (which can happen when
    //    a polymorphic function is used with multiple types in one
    //    program), we drop that ID from the global map and let the
    //    per-call-site rewrite still produce the right specs — the
    //    binding type for any individual `let r = f(...)` will still
    //    get its concrete substitution via the per-binding fallback.
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
            | _ -> None)  // conflict — leave alone, per-call-site rewrite handles each
        |> Map.ofList

    // Per-binding-local fallback: useful when a call sits *directly* in
    // a binding's value (the existing simple case) and there's no
    // conflict from elsewhere. Same construction as before, but only
    // applied when global bindings don't already cover the IDs in
    // the binding's declared type.
    let unionBindingsFromExpr (expr: IRExpr) : Map<int, IRType> =
        collectAllBindingsFromExpr expr
        |> List.fold (fun acc (k, v) ->
            match Map.tryFind k acc with
            | Some _ -> acc
            | None -> Map.add k v acc) Map.empty

    // 5. Rewrite all expressions; substitute binding declared types
    //    using the union of (global, per-binding-local) bindings;
    //    also substitute types and rewrite call sites inside spec bodies.
    //    Local bindings layer on top of global — local wins for IDs in
    //    conflict at the global level (each call site is locally
    //    consistent even when globally inconsistent).
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
                                { p with Type = substTypeInIRType bindings p.Type }) })
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
                                   { p with Type = substTypeInIRType bindings p.Type }) })

    { modul with
        Functions = newFunctions @ specFuncs @ (lambdaClones |> List.ofSeq)
        Bindings = newBindings }

// ============================================================================
// Arity Monomorphization
// ============================================================================

/// Locate every Poly param's index in a function, in declaration order.
/// Each Poly pack is independent — different slots may have different
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
/// pack stands independently — `pairSum((1.0, 2.0), (3.0, 4.0, 5.0))` is a
/// valid call with arities [2; 3]. Three call shapes are recognized:
///
/// (a) Variadic — only when the function has a single Poly param and no
///     free params. Every positional arg is a pack element. Returns
///     [args.Length].
///
/// (b) Single-Poly tuple-as-pack — single Poly param accompanied by free
///     params. The pack is a tuple at the Poly slot; free args follow in
///     their normal positions. Returns [tuple.Length].
///
/// (c) Multi-Poly tuple-as-pack — every Poly slot receives a tuple, each
///     with its own (potentially different) arity. Free args occupy non-
///     Poly positions. Returns [size_slot_0; size_slot_1; ...].
///
/// Returns None for unsupported shapes (mismatched arg count, non-tuple at
/// a required Poly slot).
let computePolyArity (func: IRFuncDef) (args: IRExpr list) : int list option =
    let polyIndices = findPolyParamIndices func
    match polyIndices with
    | [] -> None
    | [pidx] when func.Params.Length = 1 ->
        // Variadic — args are the pack elements directly
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
/// independently — different slots can yield different numbers of elements.
let flattenAtPolyPosition (func: IRFuncDef) (args: IRExpr list) : IRExpr list =
    let polyIndices = findPolyParamIndices func
    match polyIndices with
    | [] -> args
    | [_] when func.Params.Length = 1 -> args  // variadic — already flat
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
/// arity list. Unsupported call shapes are silently skipped — they surface
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
/// `arities` carries one arity per Poly slot, in declaration order — packs
/// are independent, so different slots may have different sizes.
let specializeFunction (func: IRFuncDef) (arities: int list) (builder: IRBuilder) : IRFuncDef =
    let polyIndices = findPolyParamIndices func
    if List.isEmpty polyIndices then func  // Not actually poly — shouldn't happen
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

        // Collect transitive let-aliases of each Poly param's VarId.
        let collectLetAliases (rootId: IRId) (expr: IRExpr) : Set<IRId> =
            let mutable aliases = Set.singleton rootId
            let rec walk expr =
                match expr with
                | IRLet (id, IRVar (srcId, _), body) ->
                    if Set.contains srcId aliases then
                        aliases <- Set.add id aliases
                    walk body
                | IRLet (_, value, body) -> walk value; walk body
                | IRIf (c, t, e) -> walk c; walk t; walk e
                | IRMatch (s, cases) -> walk s; cases |> List.iter (fun c -> walk c.Body)
                | IRBinOp (_, _, l, r) -> walk l; walk r
                | IRForRange (_, lo, hi, body) -> walk lo; walk hi; walk body
                | _ -> ()
            walk expr
            aliases

        // Map alias VarId → slot index, so the IRPolyIndex rewrite knows
        // which newParams set to draw from for a given xs/ys/zs reference.
        let aliasToSlot =
            slotInfo
            |> List.mapi (fun slotIdx (_, polyParam, _, _) ->
                let aliases = collectLetAliases polyParam.VarId func.Body
                aliases |> Set.toList |> List.map (fun aid -> (aid, slotIdx)))
            |> List.concat
            |> Map.ofList

        // Map param name → slot index, for the IRArity intrinsic. `arity(xs)`
        // resolves to slot 0's arity; `arity(ys)` to slot 1's, etc.
        let paramNameToSlot =
            slotInfo
            |> List.mapi (fun slotIdx (_, p, _, _) -> (p.Name, slotIdx))
            |> Map.ofList

        // Per-slot data indexed by slot, used during body rewrite.
        let newParamsBySlot =
            slotInfo |> List.map (fun (_, _, _, np) -> np) |> List.toArray
        let baseTypeBySlot =
            slotInfo |> List.map (fun (_, _, bt, _) -> bt) |> List.toArray
        let aritiesArr = arities |> List.toArray

        // Rewrite body: replace IRPolyIndex (per-slot lookup) and IRArity
        // (per-slot arity literal). Each Poly slot is handled independently.
        let rewrite e =
            match e with
            | IRPolyIndex (IRVar (id, _), IRLit (IRLitInt k)) when Map.containsKey id aliasToSlot ->
                let slotIdx = aliasToSlot.[id]
                let slotArity = aritiesArr.[slotIdx]
                let kInt = int k
                if kInt >= 0 && kInt < slotArity then
                    IRVar (newParamsBySlot.[slotIdx].[kInt].VarId, baseTypeBySlot.[slotIdx])
                else e
            | IRPolyIndex (IRVar (id, _), _) when Map.containsKey id aliasToSlot ->
                e  // Dynamic index — can't monomorphize, leave as-is
            | IRArity (_, name) when Map.containsKey name paramNameToSlot ->
                let slotIdx = paramNameToSlot.[name]
                IRLit (IRLitInt (int64 aritiesArr.[slotIdx]))
            | _ -> e
        let newBody = mapIRExpr rewrite func.Body

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

        let newRetType =
            match func.RetType with
            | IRTPoly (bt, _) -> bt
            | other -> other

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
        // account for expanded parameters. If the source had groups,
        // newCommGroups is the expanded form; otherwise empty.
        let (newIsComm, newCommGroups) =
            if func.IsCommutative then
                let expanded =
                    func.CommGroups |> List.map (fun group ->
                        group |> List.collect (fun idx ->
                            match Map.tryFind idx origToNew with
                            | Some (start, span) ->
                                if span = 1 then [start]
                                else List.init span (fun i -> start + i)
                            | None -> [idx]))
                (true, expanded)
            else (false, [])

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
          Parallelism = func.Parallelism
          IsOmpParallel = func.IsOmpParallel
          IsCudaKernel = func.IsCudaKernel
          CudaBlockSize = func.CudaBlockSize
          IsArityPoly = false
          ArityParam = None
          // Specialized clones inherit the original's captures verbatim;
          // arity specialization doesn't introduce new free vars.
          Captures = func.Captures }

// ============================================================================
// Inline-Form Lifting Pass
// ============================================================================
//
// Some IR forms (IRMask, IRSort, IRIntersect, IRUnion, IRGroupBy, IRGroupKeys)
// represent operations whose codegen requires a *named binding*: the loop body
// references the result by name (e.g., `m_extents[0]`, `m[i]`), and the
// codegen for the form itself emits multi-statement setup (size computation,
// allocation, fill loop). When these forms appear inline as a sub-expression
// — say `reduce(mask(g, pred), op)` — the IIFE wrapping `reduce` would need
// to inline-emit the entire mask setup as statements before reducing, and
// every other site that consumes an array (IRExtent, IRIndex, IRApp, ...)
// would need parallel handling.
//
// Rather than adding bespoke inline-materialization to each consumer, this
// pass normalizes the IR: any inline-form occurrence in a non-let-RHS position
// is rewritten to a fresh `IRLet(tmp, form, parent(IRVar(tmp, ty)))`. Codegen
// then sees only the canonical pattern `let tmp = mask(...)` and emits its
// existing genBinding path.
//
// The blessed positions (no rewrite) are:
//   - The value side of an IRLet (already named)
//   - The Arrays list of IRMethodFor / IRApplyCombinator (handled by their
//     own auto-materialize at codegen)
//
// Everywhere else (IRReduce.array, IRExtent.array, IRIndex.array, IRApp args,
// etc.), the rewrite fires.

/// Map from struct name to its fields, used by typeOf for IRFieldAccess
/// resolution. Built at the entry to liftInlineFormsModule and used throughout
/// the same lift-pass invocation.
///
/// Thread-safety: the test runner uses `Array.Parallel.mapi` to compile
/// tests in parallel. With a plain module-level mutable Dictionary, one
/// test's `setStructFieldsCache` would wipe another concurrent test's
/// cache state — causing intermittent `IRTUnit` results from
/// `tryLookupFieldType` and downstream codegen errors (see the
/// `Struct Array With Array Field` regression). Wrapping the cache in
/// `AsyncLocal<T>` and assigning a fresh Dictionary per set call gives
/// each task its own instance.
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
    // Create a fresh Dictionary for this async context — do not reuse and
    // .Clear() a shared instance, since other tasks may hold the same
    // reference from earlier in the parallel test run.
    let cache = System.Collections.Generic.Dictionary<string, (string * IRType) list>()
    for td in types do
        match td with
        | IRTDStruct (name, fields, _) -> cache.[name] <- fields
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

// ============================================================================
// Canonical expression typing — typeOf (audit §2.2)
// ============================================================================
//
// THE one type-reconstruction over IR expressions. Until every IR node
// carries its type (the rewrite's design), passes that need a type must
// re-derive it. Previously three hand-maintained derivations existed —
// CodeGen.inferExprType (full, with its own compound/group_by/gram rules),
// IR.typeOf (a partial second copy for the lift pass), and
// IR.exprTypeIfKnown (deliberately local) — and any divergence between them
// was a silent wrong-codegen bug. Now:
//   - typeOf                — the full reconstruction (this section)
//   - exprTypeIfKnown       — the CarriedType subset only (HM call sites
//                             must not unify against reconstructed types;
//                             see its doc comment)
//   - CodeGen.inferExprType — thin alias of typeOf
//
// Dispatch is organized as active-pattern families feeding a top-level
// match, not one flat 74-arm wall:
//   CarriedType — types carried directly on the node (defined earlier,
//                 shared with exprTypeIfKnown)
//   TypeVia     — variants whose type IS one distinguished child's type
//   IntValued   — index-arithmetic markers, always Int64
// Structural rules (indexing, group_by, gram, ...) follow, and the
// deliberately-untyped variants (loop objects, emission-internal markers)
// are enumerated WITHOUT a wildcard, so a new IRExpr variant demands an
// explicit typing decision here instead of silently becoming IRTUnit.

/// Variants whose type equals one distinguished child's type. The returned
/// expression is the child whose type to take — not necessarily the first
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
    // @>> composition: the right side's type.
    | IRComposeMeth (_, a) ->
        Some a
    | IRMatch (_, c :: _) -> Some c.Body
    | _ -> None

/// Index-space arithmetic markers: always Int64 scalars.
let (|IntValued|_|) (expr: IRExpr) : unit option =
    match expr with
    | IRArity _ | IRNth | IRRank _ | IRExtent _ | IRRaggedLookup _
    | IRCompoundMask _ | IRCompoundProject _ | IROpaqueExtent | IRRange _ ->
        Some ()
    | _ -> None

// ----------------------------------------------------------------------------
// Synthetic sentinel index IDs
//
// Some typeOf branches need to construct an IRIndexType in flight — e.g., to
// recover the rank-2 shape of an IRGroupBy result that was not already
// let-bound (and therefore has no typecheck-derived IRBinding.Type to
// consult). Those branches don't have access to an IRBuilder and can't
// allocate fresh IDs via FreshId().
//
// Convention: synthetic sentinel IDs are NEGATIVE. IRBuilder.FreshId starts
// at 0 and counts up, so the negative range is reserved and never collides
// with builder-assigned IDs. Each call site that synthesizes indices picks a
// distinct negative ID below.
//
// IDs are not load-bearing for codegen decisions — consumers of inferred
// types pattern-match on structure (ArrayElem, IRTScalar) and on `Tag`, not
// on `Id`. The IDs serve only to satisfy IRIndexType's record shape.
// ----------------------------------------------------------------------------
let synthSlotIdOuter : IRId = -1
let synthSlotIdMember : IRId = -2
let synthSlotIdCompoundResidual : IRId = -3

// ----------------------------------------------------------------------------
// Compound partial-index classification (formalism 4.5)
//
// Shared by typeOf's IRIndex arm, CodeGen's genScalarBinding wrapper
// decision, and exprToCppCore's compoundRead emission, so the three never
// disagree about WHICH indexing form a compound read is.
//
// A wildcard coordinate arrives as an `IRLit IRLitUnit` sentinel inside the
// index tuple: TypeCheck's dispatchAppOrIndex rewrites each consumed
// TExprWildcard hole to a unit literal, and unit is never a valid coordinate
// value, so the encoding is unambiguous. A short tuple (arity j < k, no
// sentinels) pins the LEADING j coordinates -- B((a,b)) and B((a,b,_)) on a
// rank-3 compound are the same read.
// ----------------------------------------------------------------------------

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
/// was edited out of sync with typeOf's coverage tail — fail loudly rather
/// than mistype silently.
let private unreachableTyping (family: string) (expr: IRExpr) : 'a =
    failwithf "typeOf: family pattern %s no longer covers %s — coverage arm and family out of sync"
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
         | IRMath _ -> IRTScalar ETFloat64)
    | IRTuple exprs -> IRTTuple (exprs |> List.map typeOf)
    | IRComplex (re, _) ->
        // Complex type derived from component width: Float32 → Complex64,
        // Float64 → Complex128. Reports as a scalar (NOT a tuple) — that's
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
        // it -- mirroring TypeCheck's compoundResidualType (dense Idx for one
        // free axis, residual CompoundIdx for >= 2). Without this branch a
        // partial read reports the element type (1 index >= 1 slot), which
        // breaks chained/inline consumers (reduce over a residual, chained
        // partials) at codegen.
        (match typeOf arr with
         | ArrayElem arrTy ->
             let headCompound =
                 match arrTy.IndexTypes with
                 | h :: _ when h.IxKind = IxKCompound -> Some h
                 | _ -> None
             (match headCompound, indices with
              | Some h, (IRTuple coords) :: trailingIdxs ->
                  (match classifyCompoundIndexTuple h.Rank coords with
                   | CompoundPartial (pinned, freePos) ->
                       let rr = freePos.Length
                       let residual =
                           if rr = 1 then
                               { Id = synthSlotIdCompoundResidual; Rank = 1
                                 Extent = IRCompoundProject (arr, pinned.Length)
                                 Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                                 Kind = SDimension; Dependencies = [] }
                           else
                               { Id = synthSlotIdCompoundResidual; Rank = rr
                                 Extent = IRCompoundProject (arr, pinned.Length)
                                 Symmetry = SymNone; Tag = Some "__compoundidx"; IxKind = IxKCompound
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
    | IRFieldAccess (obj, field) ->
        // Resolved via the ONE struct-fields cache (structFieldsCacheStorage
        // above), populated both at liftInlineFormsModule entry and at
        // codegen module entry from the same module's Types — collapsing the
        // duplicate codegen-side cache that audit §2.4 flagged as a
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
        // The TypeCheck-side `ExprGroupBy` rule constructs a rank-2 array
        // type with `__group_outer` + `__group_member` tagged index slots
        // (see TypeCheck.fs:ExprGroupBy). For let-bound group_by results
        // — which is the only currently-allowed usage; inline group_by
        // in method_for() is rejected at codegen entry — the binding's
        // Type field carries this rank-2 form, and `IRVar` lookups
        // return it correctly.
        //
        // This branch fires when an IRGroupBy node is consulted
        // directly (e.g., lifted bindings, future inline support).
        // Reconstruct the same rank-2 form here so consumers that
        // pattern-match on shape (ArrayElem, rank checks) see the
        // correct structure. See `synthSlotId*` above for the
        // sentinel-ID convention used here.
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
             // behavior — same shape, same element type — so any caller
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
        // this); Ids reused — authoritative nominal type is set by TypeCheck.
        (match typeOf arr with
         | ArrayElem a ->
            let rec walk slotIdx acc remaining =
                match remaining with
                | [] -> None
                | (ix: IRIndexType) :: rest ->
                    let ar = max 1 ix.Rank
                    if d < acc + ar then Some (slotIdx, ar, d - acc, ix)
                    else walk (slotIdx + 1) (acc + ar) rest
            (match walk 0 0 a.IndexTypes with
             | Some (slot, r, posInSlot, ix) when r >= 2 && ix.Symmetry <> SymNone ->
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
    //    markers — not runtime values with a simple type). Enumerated with
    //    no wildcard so a NEW variant demands a typing decision here.
    | IRMethodFor _ | IRObjectFor _ | IRReynolds _ | IRArrayProduct _
    | IRComposeObj _ | IRCompose _
    | IRSlice _ | IRCurry _ | IRSubset _ | IRShift _ | IRReverse _ | IRDiag _
    | IRZip _ | IRAlign _ | IRStack _ | IRJoin _
    | IRTupleCons _ | IRTupleDecons _ | IRPolyIndex _ | IRReplicate _
    | IRVirtualReverse _ | IRBlocked _ | IRZero ->
        IRTUnit

    // -- Coverage tail ---------------------------------------------------
    // Every constructor below is already handled by a family pattern above
    // (partial active patterns are invisible to the exhaustiveness checker).
    // Listing them keeps this match provably exhaustive WITHOUT a wildcard —
    // a brand-new IRExpr variant still fails to compile until it gets a
    // typing rule — and if one of these arms ever fires, a family pattern
    // was edited out of sync: fail loudly, never mistype.
    | IRVar _ | IRParam _ | IRApp _ | IRArrayLit _ | IRStructLit _
    | IRApplyCombinator _ | IRComposeApply _ | IRLit _ ->
        unreachableTyping "CarriedType" expr
    | IRSort _ | IRArrayNegate _ | IRArrayConjugate _ | IRIntersect _
    | IRUnion _ | IRUnique _ | IRCompute _ | IRPure _ | IRLet _ | IRIf _
    | IRGuard _ | IRChoice _ | IRComposeMeth _ | IRMatch _ ->
        unreachableTyping "TypeVia" expr
    | IRArity _ | IRNth | IRRank _ | IRExtent _ | IRRaggedLookup _
    | IRCompoundMask _ | IRCompoundProject _ | IROpaqueExtent | IRRange _ ->
        unreachableTyping "IntValued" expr

/// Predicate: is this an inline form that needs lifting when in a non-blessed
/// position? Note: we deliberately exclude IRReduce — reduce's codegen
/// handles inline forms via IIFE (when the array is named) and the array
/// argument to reduce IS what we lift, not reduce itself.
let isInlineForm (e: IRExpr) : bool =
    match e with
    | IRMask _ | IRSort _ | IRIntersect _ | IRUnion _ | IRUnique _
    | IRGroupBy _ | IRGroupKeys _ | IRTranspose _ | IRDecompact _ | IRArrayNegate _ | IRArrayConjugate _ -> true
    | _ -> false

/// Path B / Phase D: peel any IRLet chain that descendant lifts produced.
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
/// companion `_extents` (and `_lens` for ragged) array — without a let-RHS
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
/// Path B / Phase D: also peels any IRLet chain the descendant produced,
/// so the chain bindings hoist alongside any new lift binding to the
/// caller's wrap point.
let liftChild (builder: IRBuilder) (child: IRExpr) : (IRId * IRType * IRExpr) list * IRExpr =
    let (peeled, inner) = peelLetChain child
    if isInlineForm inner then
        let id = builder.FreshId()
        let ty = typeOf inner
        (peeled @ [(id, ty, inner)], IRVar (id, ty))
    elif isArrayFieldAccess inner then
        // Phase D: hoist `t.samples` (when samples is array-typed) into a
        // let-RHS so codegen can synthesize `<bound_name>_extents`.
        let id = builder.FreshId()
        let ty = typeOf inner
        (peeled @ [(id, ty, inner)], IRVar (id, ty))
    else
        (peeled, inner)

/// Like `liftChild`, but additionally lifts IRArrayLit. Used at sites
/// where an inline IRArrayLit can't render (struct field values, function
/// args). NOT used at IRArrayLit element positions — there, the inner
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
        // Phase D: same hoisting as liftChild, so struct field values and
        // function args carrying `t.samples` get the same treatment.
        let id = builder.FreshId()
        let ty = typeOf e
        (peeled @ [(id, ty, e)], IRVar (id, ty))
    | e -> (peeled, e)

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
/// or IRApplyCombinator's Arrays list, we also leave it — codegen's
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
    // them at this point — the parent's child slot will lift them if
    // needed.
    | IRMask (a, p) ->
        // Lift inline-form array arg so codegen sees a let-bound name in
        // the array slot (rather than another inline form it can't render
        // inside its own template). The predicate is a lambda — not an
        // inline form — so it just recurses normally.
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

    // Contains returns a scalar Bool — its array argument may be an inline
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
        // tree) — never lift it into a binding (it has no materialized
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
    | IRGram (l, r, s) ->
        let l' = liftExpr builder l
        let r' = liftExpr builder r
        let (bindsL, lFinal) = liftChild builder l'
        let (bindsR, rFinal) = liftChild builder r'
        wrapLets (bindsL @ bindsR) (IRGram (lFinal, rFinal, s))
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
        // Phase D: function args may contain inline IRArrayLit (e.g.,
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
        // Path B / Phase D: peel any IRLet chains from element results
        // (descendant lifts) and re-wrap them at THIS level. Don't lift the
        // peeled inner expressions further — IRArrayLit elements must
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
        // Phase D / nested element types: descend into each field expression,
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
    | IRCompute e -> IRCompute (liftExpr builder e)
    | IRReynolds (e, a) -> IRReynolds (liftExpr builder e, a)
    | IRBind (c, k) -> IRBind (liftExpr builder c, liftExpr builder k)
    | IRParallel (a, b, d) -> IRParallel (liftExpr builder a, liftExpr builder b, d)
    | IRFusion (a, b) -> IRFusion (liftExpr builder a, liftExpr builder b)
    | IRChoice (a, b) -> IRChoice (liftExpr builder a, liftExpr builder b)
    | IRArrayProduct (a, b) -> IRArrayProduct (liftExpr builder a, liftExpr builder b)
    | IRComposeObj (a, b) -> IRComposeObj (liftExpr builder a, liftExpr builder b)
    | IRComposeMeth (a, b) -> IRComposeMeth (liftExpr builder a, liftExpr builder b)
    | IRCompose (a, b) -> IRCompose (liftExpr builder a, liftExpr builder b)
    | IRFunctorMap (fn, c) -> IRFunctorMap (liftExpr builder fn, liftExpr builder c)
    | IRPolyIndex (p, i) -> IRPolyIndex (liftExpr builder p, liftExpr builder i)
    | IRRaggedLookup l -> IRRaggedLookup (liftExpr builder l)
    | IRCompoundMask mk -> IRCompoundMask (liftExpr builder mk)
    | IRCompoundProject (parent, plen) -> IRCompoundProject (liftExpr builder parent, plen)
    | IRAssign (t, v) -> IRAssign (t, liftExpr builder v)
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
                if isArrayFieldAccess inner then
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
                if isArrayFieldAccess inner then
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
                if isArrayFieldAccess inner then
                    let id = builder.FreshId()
                    let ty = typeOf inner
                    (accB @ peeled @ [(id, ty, inner)], accA @ [IRVar (id, ty)])
                else
                    (accB @ peeled, accA @ [inner])) ([], [])
        wrapLets binds (IRComposeApply { info with Composition = composition'; InputArrays = arraysFinal })

/// Lift inline forms across an entire IR module's bindings and functions.
let liftInlineFormsModule (modul: IRModule) (builder: IRBuilder) : IRModule =
    // Phase D / companion-array gap: populate the struct fields cache so
    // typeOf can resolve IRFieldAccess result types. Required for
    // hoisting array-typed field accesses to let-RHS so codegen can
    // synthesize their _extents companions.
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

    // 2. Collect all call sites with concrete arities
    let callSitesFromFuncs =
        modul.Functions |> List.collect (fun f -> collectPolyCallSites polyFuncMap f.Body)
    let callSitesFromBindings =
        modul.Bindings |> List.collect (fun b -> collectPolyCallSites polyFuncMap b.Value)
    let uniqueCallSites =
        (callSitesFromFuncs @ callSitesFromBindings) |> List.distinct

    // 3. Generate specialized functions
    let specializations =
        uniqueCallSites |> List.map (fun (funcId, arity) ->
            let origFunc = polyFuncMap.[funcId]
            let spec = specializeFunction origFunc arity builder
            ((funcId, arity), spec))
    let specMap = specializations |> Map.ofList

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
    let specFuncs = specializations |> List.map snd

    { modul with
        Functions = newFunctions @ specFuncs
        Bindings = newBindings }

// ============================================================================
// Pretty Printing
// ============================================================================

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
        // "Array<elem like indices>" rendering — same form as the legacy
        // IRTArray printer, which keeps error messages recognizable. Other
        // shapes (functions, mixed slots) get the canonical "Arrow<...>" form.
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
    match idx.Symmetry with
    | SymNone -> sprintf "Idx<%s>" extentStr
    | SymSymmetric -> sprintf "SymIdx<%d, %s>" idx.Rank extentStr
    | SymAntisymmetric -> sprintf "AntisymIdx<%d, %s>" idx.Rank extentStr
    | SymHermitian -> sprintf "HermitianIdx<%s>" extentStr

// (ppElemType removed in Phase B6: unused after ppIRType was made recursive
// over IRType in Phase B2. The primitive-only printer is no longer needed
// since elem types are now full IRTypes printed by ppIRType.)

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
        sprintf "Array<%s, %s>" (ppIRTypeIn names arr.ElemType) indices
    | other -> ppIRType other

and ppIndexTypeIn (names: Map<IRId, string>) (idx: IRIndexType) =
    let extentStr =
        match Map.tryFind idx.Id names with
        | Some name -> name
        | None ->
            match idx.Extent with
            | IRLit (IRLitInt n) -> sprintf "%d" n
            | IRVar (id, _) -> sprintf "v%d" id
            | IRParam (name, _, _) -> name
            | _ -> "?"
    match idx.Symmetry with
    | SymNone -> sprintf "Idx<%s>" extentStr
    | SymSymmetric -> sprintf "SymIdx<%d, %s>" idx.Rank extentStr
    | SymAntisymmetric -> sprintf "AntisymIdx<%d, %s>" idx.Rank extentStr
    | SymHermitian -> sprintf "HermitianIdx<%s>" extentStr

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
    | IRChoice (a, b) ->
        sprintf "(%s <|> %s)" (pp a) (pp b)
    | IRCompose (f, g) ->
        sprintf "(%s >> %s)" (pp f) (pp g)
    | IRComposeObj (f, g) ->
        sprintf "(%s >>@ %s)" (pp f) (pp g)
    | IRComposeMeth (f, g) ->
        sprintf "(%s @>> %s)" (pp f) (pp g)
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


// ============================================================================
// IR Validator — catches malformed IR between lowering and codegen
// ============================================================================

/// Collect all variable IDs referenced in an expression (for scope validation)
/// Attempt to statically evaluate an IRExpr to an int64. Used for resolving
/// extent expressions to compile-time literals when possible. Handles literal
/// integer arithmetic (the common case for derived index extents like
/// `Idx<n+1>`); anything more general (variable references, function calls,
/// runtime-dependent expressions) returns None.
///
/// This is intentionally narrow — a full static evaluator over IR would be a
/// much larger undertaking (and StaticEval.fs already provides one over the
/// surface AST). The use cases right now are extent inspection in extents()
/// and reduce()'s non-emptiness check, both of which only need arithmetic
/// over int literals.
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

// (collectVarRefsIR now lives beside the canonical ExprShape traversal,
// before mapIRExpr. The hand-maintained copy that lived here had a
// `| _ -> Set.empty` catchall that silently skipped ~15 variants; the
// shape-based fold cannot skip anything.)

// ============================================================================
// AnalysisContext — unified callable-walking for cross-procedural analysis
// ============================================================================
//
// `exprAttrs` walks an expression tree to compute attributes (FreeVars,
// BoundVars, IsPure). The IRApp arm follows IRVar(fId) references via
// the CallablesTable, substitutes the callee's params with the call's
// args, and walks the body, so free variables originating inside a
// callee's body are surfaced to the caller's analysis.
//
// `Visited` short-circuits recursion. When walking f's body, we add f
// to Visited; if g calls f (mutual recursion), the IRApp arm sees f in
// Visited and stops. Direct self-recursion stops on the first walk.
//
// The CallablesTable is set once per module at codegen entry. Visited
// is augmented (and restored) at each IRApp boundary by `withVisited`.
// Both live in a single AsyncLocal record so we have one piece of
// per-flow state rather than two.
//
// What this gives the optimization: a mask predicate's exprAttrs walk
// sees every reachable contains — direct, through inline lambdas, and
// through function calls (up to recursion). Whether codegen can
// actually substitute set.count for any given probe is a separate
// question answered by reachability check on the rendered tree.
//
// (CallablesTable, AnalysisContext, analysisCtxStorage, currentAnalysisCtx,
// setCallablesContext, restoreAnalysisContext, withVisited, and
// resolveCallable were moved earlier in the file — to before
// buildLoopNestCodeGen — because Stage 3c.3 needs that builder to
// resolve IRVar-typed kernels through the CallablesTable.)

/// Build a CallablesTable from a module's function list. Codegen calls
/// this at module entry and installs the result via setCallablesContext.
let buildCallablesTable (funcs: IRCallable list) : CallablesTable =
    funcs |> List.map (fun f -> (f.Id, f)) |> Map.ofList

/// Build a CallablesTable from a full module, including alias entries
/// for let-bindings that reference lifted callables.
///
/// Stage 3c.3 motivation: when `let f = lambda(...)` lowers, the lambda
/// gets lifted to module.Functions with callableId, and the binding's
/// value is `IRVar(callableId, funcType)`. The binding itself has a
/// FRESH `bindingId` distinct from `callableId`. Subsequent references
/// to `f` lower as `IRVar(bindingId, _)`, NOT `IRVar(callableId, _)` —
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
/// callableId resolves in the base table adds `bindingId → callable`
/// to the alias map. Multiple hops are followed transitively
/// (`let g = f` where `f` itself aliases a callable resolves `g` to the
/// same callable). The result is the base table with all aliases merged.
let buildCallablesTableForModule (modul: IRModule) : CallablesTable =
    let baseTable = buildCallablesTable modul.Functions
    let aliases = System.Collections.Generic.Dictionary<IRId, IRId>()
    // Side-effecting visitor: at every IRLet, record bindingId → targetId
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
    // Resolve transitive aliases (bindingId → targetId → ...) with a
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
    // real callable, add the binding id → callable entry.
    let mutable result = baseTable
    for kvp in aliases do
        let finalId = resolveTransitive kvp.Key
        match Map.tryFind finalId baseTable with
        | Some callable -> result <- Map.add kvp.Key callable result
        | None -> ()
    result

// (resolveCallable was moved earlier in the file — see the analysisCtx
// block before buildLoopNestCodeGen — so that the loop-nest builder
// can call it for IRVar-typed kernels after Stage 3c.3.)

// ============================================================================
// ExprAttrs — bottom-up attribute computation for IR expressions
// ============================================================================
//
// Phase B of the LICM roadmap: a single bottom-up pass that computes
//   FreeVars  — IRIds referenced from outside this expression's binders
//   BoundVars — IRIds introduced inside (by IRLet, lambda params, etc.)
//   IsPure    — no observable side effects
// for any IRExpr.
//
// Phase B does NOT drive any rewrite. The function exists so that future
// passes (Phase C: general hoist; Phase D: LICM/CSE) can consume a
// uniform, audited source of "what does this expression depend on?".
//
// Design notes:
//   - No memoization. Phase B is a correctness foundation, not a hot
//     path. If profiling later shows attribute computation dominating,
//     add a reference-keyed cache then.
//   - IsPure is currently true for all native Blade IR (no I/O, no
//     in-language mutation that affects observable behavior beyond what
//     codegen wraps in deterministic allocations). The field exists for
//     forward compatibility — when an impure construct lands, its arm
//     declares IsPure = false and the field starts mattering.
//   - Exhaustive by construction: only the semantically special variants
//     have explicit arms (IRVar contributes a free var; IRApp follows
//     resolvable callees; the BinderShape variants scope their bound ids).
//     Everything else — including any future variant — merges its
//     children's attrs via the canonical ExprShape fold.

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
        // already treats inline lambdas — both are "callables whose
        // body we walk." Recursion is bounded by the visited set in
        // AnalysisContext, which is augmented at every function-body
        // walk and restored afterwards.
        //
        // The walked body's probes will have Node references pointing
        // at IRContains nodes inside the function body, not in the
        // caller's tree. The mask renderer's reachability check
        // (Phase C codegen) filters those out before adding to its
        // substitution map, so unreachable probes don't generate
        // unused preamble. They remain visible in the analysis for
        // diagnostic or future-use purposes.
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
    //    via BinderShape — a new binding variant needs exactly one
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
/// ExprShape fold, so no variant's subtree can be silently skipped. (The
/// previous version's `| _ -> []` catchall stopped RECURSION at ~15
/// variants — IRSlice, IRShift, IRMask, IRZip, ... — hiding any unresolved
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

    // checkApplyInfo (below) resolves kernel slots through
    // `resolveCallable` to inspect param count and comm groups,
    // which requires the CallablesTable to be installed in the
    // AsyncLocal analysis context. We install it here using
    // buildCallablesTableForModule (so let-bound kernel references
    // resolve through their binding-id → callable alias) and
    // restore the prior context on exit, so the validator doesn't
    // leak state to subsequent passes.
    let savedCtx = setCallablesContext (buildCallablesTableForModule modul)
    
    // Track all defined IDs (bindings + functions define names in scope).
    // External Ids come from other modules visible via imports; the IR-level
    // validator can't cheaply distinguish "imported and used" from "unrelated
    // module's Id that happens to match" without import metadata in IRModule,
    // so we accept all program Ids as in-scope. False negatives (an
    // accidental cross-module reference) would require a TypeCheck/Lowering
    // bug to manifest, which would likely surface elsewhere.
    let moduleIds =
        let bindIds = modul.Bindings |> List.map (fun b -> b.Id) |> Set.ofList
        let funcIds = modul.Functions |> List.map (fun f -> f.Id) |> Set.ofList
        Set.unionMany [bindIds; funcIds; externalIds]
    
    // Tag/IxKind agreement (audit §3.3 migration): while both encodings
    // exist, they must never diverge — a construction or with-update that
    // stamps a sentinel Tag without the matching IxKind (or vice versa)
    // is exactly the valid-but-wrong hazard the field was added to kill.
    // ixKindOfTag maps sentinels to kinds and everything else to IxKPlain,
    // so equality enforces both directions.
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
        | IRBind (c, k) -> checkScope scope ctx c; checkScope scope ctx k
        | IRFunctorMap (f, c) -> checkScope scope ctx f; checkScope scope ctx c
        | IRGuard (c, b) -> checkScope scope ctx c; checkScope scope ctx b
        | IRSequence es -> es |> List.iter (checkScope scope ctx)
        | IRPure e -> checkScope scope ctx e
        | IRAssign (t, v) -> checkScope scope ctx t; checkScope scope ctx v
        | _ -> ()  // Literals, params, etc. — no var refs
    
    let mutable cumulativeScope = moduleIds
    for b in modul.Bindings do
        let ctx = sprintf "in binding '%s'" b.Name
        checkScope cumulativeScope ctx b.Value
        cumulativeScope <- Set.add b.Id cumulativeScope
    
    for f in modul.Functions do
        let ctx = sprintf "in function '%s'" f.Name
        let paramIds = f.Params |> List.map (fun p -> p.VarId) |> Set.ofList
        // Stage 3c.3: lifted lambdas live in module.Functions with their
        // captures in `f.Captures` (separate from `f.Params`). The
        // captures' Ids reference the enclosing source-level var; the
        // lambda's body references those Ids directly. Before 3c.3, the
        // lambda lived inline inside its enclosing function's body, so
        // the body's IRVar references to captures resolved against the
        // enclosing function's params — and the validator's scope
        // walk picked them up that way. After 3c.3, the lambda is its
        // own top-level function and the enclosing function's params
        // aren't in scope at the validator's `for f in modul.Functions`
        // loop; we have to add the function's own Captures' Ids to
        // the visible scope so the body's references resolve.
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
            // Canonical apply: Kernel slot is a callable reference,
            // either IRVar(id, _) or IRReynolds(IRVar(id, _), _).
            // `resolveKernel` peels any Reynolds wrapper and resolves
            // the inner via CallablesTable + synthetic registry.
            //
            // After the IRComposeApply split, `info.Loop = IRObjectFor _`
            // can only arise from canonical `object_for(g) <@> A` going
            // through `buildApplyInfo` — the slot-inverted compose case
            // routes through IRComposeApply, and the meta-application
            // patterns (object_for(<@>) <@> tuples) don't produce
            // TExprApply at all. So IRObjectFor in the Loop slot also
            // unambiguously implies a callable kernel.
            //
            // We still skip the check when Loop is IRVar (let-bound;
            // could resolve to either canonical or compose-apply
            // shape, and we don't have the binding env here) — the
            // codegen path retains its own resolution for that case.
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
