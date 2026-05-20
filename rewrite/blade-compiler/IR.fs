// Blade-DSL Intermediate Representation
// Lowered from AST, ready for optimization and code generation
// Implements the Structural Trinity: Loop Reification, Arity Polymorphism, Dimensional Currying

module Blade.IR

open System

// ============================================================================
// Core Type Definitions
// ============================================================================

/// Unique identifier for IR values
type IRId = int

/// Symmetry class for index types
type SymmetryClass =
    | SymNone          // No symmetry (dense)
    | SymSymmetric     // Symmetric (i <= j)
    | SymAntisymmetric // Antisymmetric (i < j, negate on swap)
    | SymHermitian     // Hermitian (conjugate on swap)

/// Commutativity/Symmetry state at each loop level (Section 13.1)
/// Determines whether triangular iteration is valid at this position
type SymcomState =
    | SCNeither       // Independent iteration - no optimization
    | SCSymmetric     // Same array, symmetric dimension - triangular valid
    | SCCommutative   // Different arrays but in comm group - triangular valid
    | SCBoth          // Same array + comm group - triangular valid, best case

// ============================================================================
// Array Identity Tracking (Critical for Symmetry Exploitation)
// ============================================================================

/// Tracks the identity of arrays for commutativity detection
type ArrayIdentity =
    | AIDLiteral of IRId                    // Literal array with unique ID
    | AIDVariable of name: string           // Named variable
    | AIDParameter of name: string * idx: int  // Function parameter
    | AIDDerived of base': ArrayIdentity * op: string  // Derived from another array

/// Check if two array identities are the same
let sameIdentity a b =
    match a, b with
    | AIDVariable n1, AIDVariable n2 -> n1 = n2
    | AIDParameter (n1, i1), AIDParameter (n2, i2) -> n1 = n2 && i1 = i2
    | AIDLiteral id1, AIDLiteral id2 -> id1 = id2
    | _ -> false

// ============================================================================
// Index Types with Dependency Tracking
// ============================================================================

/// Dimension kind - S-dimensions (spatial) vs T-dimensions (temporal/time)
/// Only S-dimensions participate in symmetry optimization
type DimensionKind =
    | SDimension   // Spatial dimension - participates in symmetry
    | TDimension   // Temporal dimension - does not participate in symmetry

/// Unit of measure signature: product of base units with integer exponents
/// e.g. velocity = {meters: 1, seconds: -1}
/// Dimensionless = empty map
type UnitSig = Map<string, int>

/// Unit arithmetic: dimensionless (empty map)
let unitDimensionless : UnitSig = Map.empty

/// Normalize: remove zero-exponent entries
let unitNormalize (u: UnitSig) : UnitSig =
    u |> Map.filter (fun _ exp -> exp <> 0)

/// Unit multiplication: add exponents
let unitMul (a: UnitSig) (b: UnitSig) : UnitSig =
    let merged =
        Map.fold (fun acc k v ->
            let existing = Map.tryFind k acc |> Option.defaultValue 0
            Map.add k (existing + v) acc) a b
    unitNormalize merged

/// Unit division: subtract exponents
let unitDiv (a: UnitSig) (b: UnitSig) : UnitSig =
    let merged =
        Map.fold (fun acc k v ->
            let existing = Map.tryFind k acc |> Option.defaultValue 0
            Map.add k (existing - v) acc) a b
    unitNormalize merged

/// Unit power: scale all exponents
let unitPow (u: UnitSig) (n: int) : UnitSig =
    if n = 0 then unitDimensionless
    else u |> Map.map (fun _ exp -> exp * n) |> unitNormalize

/// Check if two unit signatures are compatible (equal)
let unitCompatible (a: UnitSig) (b: UnitSig) : bool =
    unitNormalize a = unitNormalize b

/// Pretty-print a unit signature
let ppUnitSig (u: UnitSig) : string =
    if Map.isEmpty u then "dimensionless"
    else
        let pos = u |> Map.filter (fun _ e -> e > 0) |> Map.toList
        let neg = u |> Map.filter (fun _ e -> e < 0) |> Map.toList
        let ppTerm (name, exp) =
            if exp = 1 then name
            elif exp = -1 then name
            else sprintf "%s^%d" name exp
        let posStr = pos |> List.map ppTerm |> String.concat " * "
        let negStr = neg |> List.map (fun (n, e) -> ppTerm (n, -e)) |> String.concat " * "
        match pos, neg with
        | [], [] -> "dimensionless"
        | _, [] -> posStr
        | [], _ -> sprintf "1 / (%s)" negStr
        | _, _ -> sprintf "%s / %s" posStr (if neg.Length > 1 then sprintf "(%s)" negStr else negStr)

/// Value carried by an EnumIdx alias declaration. The values list can be
/// either all-int or all-string (mixed lists are rejected at typecheck time).
/// The chosen kind determines the underlying runtime representation: an
/// all-int EnumIdx lowers to int64_t in C++; an all-string EnumIdx lowers to
/// std::string. Both kinds support the same SQL-like operations (group_keys,
/// group_by); only the comparison op in the Case 2 reverse-lookup dispatch
/// differs. Defined before the mutual block because IRType.IRTGroupKeys
/// carries an EnumValue list option.
type EnumValue =
    | EVInt of int64
    | EVString of string

/// Element types for arrays
type ElemType =
    | ETInt32
    | ETInt64
    | ETFloat32
    | ETFloat64
    | ETComplex64
    | ETComplex128
    | ETBool
    | ETUnit
    | ETString
    // Note: ETIndexRef (the legacy element-position foreign-key tag) was
    // retired in the Option C migration. Named index references — in both
    // value and element position — are now represented at the IRType level
    // as `IRTIdxTagged (inner, IRefNamed name)`, unifying with the value-
    // position encoding. Element-position lowering produces this directly.

/// Index type in IR - represents a single dimension's structure
and IRIndexType = {
    Id: IRId
    Arity: int               // Number of index components (1 for Idx, 2 for SymIdx<2>, etc.)
    Extent: IRExpr           // Size expression (may depend on outer indices)
    Symmetry: SymmetryClass
    Tag: string option       // Optional semantic tag (for index space matching)
    Kind: DimensionKind      // S-dimension or T-dimension
    Dependencies: IRId list  // Dependencies on outer loop indices (for triangular iteration)
}

/// Array type in IR with identity tracking.
/// ElemType is a full IRType (post-Phase B2): primitives are wrapped as
/// IRTScalar, structs/sums appear as IRTNamed, inference variables as
/// IRTInfer, etc. Active patterns (PrimElem, AnyPrimElem, NamedElem, etc.)
/// project the IRType into role-specific shapes for consumers.
and IRArrayType = {
    ElemType: IRType
    IndexTypes: IRIndexType list
    IsVirtual: bool          // Virtual array (range, reverse, etc.)
    Identity: ArrayIdentity option  // For tracking array identity
}

/// Reference to an index type from the value side.
/// Used by IRTIdxTagged as the nominal tag (parallel to UnitSig for
/// IRTUnitAnnotated). Carries identity only — not the source index type's
/// structure (arity, symmetry, bijection, etc.), which lives in
/// IRIndexType records attached to arrays.
///
/// Two index tags are compatible iff their IdxRefs are structurally
/// equal: same name for named, same nominalId for anonymous.
and IdxRef =
    /// User-defined named index type: Nat<LatIdx>.
    /// Identity is the name.
    | IRefNamed of string
    /// Anonymous Idx<n> occurrence: Nat<Idx<n>>.
    /// Identity is the nominalId — fresh per source TyIdx node, must
    /// match the corresponding IRIndexType.Id of the index type that
    /// emits this value. Extent preserved for diagnostics / pretty-
    /// printing only; NOT part of identity for unification.
    | IRefAnon of nominalId: int * extent: IRExpr

/// IR Types
and IRType =
    | IRTScalar of ElemType
    | IRTTuple of IRType list
    | IRTLoop of LoopType
    | IRTComputation of IRType   // Suspended computation producing this type
    | IRTUnit
    | IRTPoly of baseType: IRType * arityVar: string  // Arity-polymorphic type
    | IRTNat of int option  // Type-level natural number (None = variable)
    // IRTIdxTagged: a base type wrapped with a nominal index-type tag.
    // The shape parallels IRTUnitAnnotated (base * UnitSig) but uses an
    // IdxRef tag with NO multiplicative algebra — index tags are nominal
    // labels, not exponent vectors. Formalism §4.18.5 puts Nat<LatIdx>
    // alongside Float<meters>, so the wrapper shape matches; the
    // composition semantics deliberately don't.
    //
    // Typical inner type is IRTScalar ETInt64 (giving "Nat<I>"), but the
    // constructor accepts any IRType — same flexibility as IRTUnitAnnotated.
    //
    // Identity: structural equality on (inner, idxRef). Two IRTIdxTagged
    // values unify iff their inner types unify AND their IdxRefs match
    // (name=name for IRefNamed, nominalId=nominalId for IRefAnon).
    //
    // Renders the inner type at codegen — the tag is a typecheck-time
    // invariant, not a runtime carrier. For IRefNamed, the C++ typedef
    // alias is used as a documentation hook.
    //
    // Distinct from `IRTNat of int option` (type-level naturals for
    // ranks and known extent values): IRTIdxTagged is a runtime value's
    // type; IRTNat is a type-parameter.
    //
    // Replaces the ElemType-level encoding (ETAnonIdx, ETIndexRef) for
    // value positions. The legacy encodings remain in place during
    // transition; IRTIdxTagged is structurally equivalent and treated
    // identically at every match site.
    | IRTIdxTagged of inner: IRType * tag: IdxRef
    | IRTNamed of string    // Named type (struct, sum type, etc.)
    | IRTInfer of int       // Unresolved type variable (id for unification)
    | IRTUnitAnnotated of IRType * UnitSig  // Type with unit-of-measure annotation
    | IRTGroupKeys of outerIdx: IRIndexType * sourceIdx: IRIndexType * enumValues: EnumValue list option
      // GroupKeys: CSR structure mapping sourceIdx → groups indexed by outerIdx.
      // enumValues: if keys are EnumIdx, carries the actual key values for reverse lookup.
    // IRTArrow: unified arrow type. Subsumes function types (all-SVal slots)
    // and is the production form for array types after Segment 3 producer
    // migration. The slot list represents the consumption order — applying
    // values consumes slots left-to-right; once all slots are consumed, the
    // result type emerges.
    //
    // Slot kinds:
    //   - SIdx (storage-backed): takes an index value. Array dim with real
    //     allocated storage. The IRIndexType carries Tag, Symmetry, and
    //     Extent for the slot's domain.
    //   - SIdxVirt (virtual index): takes an index value but the values
    //     are computed on-the-fly (no allocated storage). Models virtual
    //     arrays like `range<I>`, `reverse<I>`, etc.
    //   - SVal (value/closure): takes a value of the given type. A pure
    //     function param.
    //
    // The `identity` field tracks array-handle identity for stored-array
    // shapes (was IRArrayType.Identity). Pure functions and virtual arrays
    // carry `None`. Mixed-shape arrows may carry `Some` when the outermost
    // dimension is stored.
    //
    // Shape constraints (enforced at `mkVirtualArrayArrow` entry via
    // `validateArrowShape`; other constructors are constraint-safe by
    // construction):
    //   1. If any slot is SIdxVirt, all slots from that point onward must
    //      also be SIdxVirt (no SIdx or SVal after the first SIdxVirt).
    //      Reason: virtual generation can't contain stored sub-arrays or
    //      function closures — once we go virtual, we stay virtual.
    //   2. If any slot is SIdxVirt, the result type must not be IRTArrow.
    //      Reason: a virtual array's elements must be simple values, not
    //      nested arrays/functions.
    //
    // Internal transformations (normalize, substTypeInIRType, Subst.Resolve)
    // preserve these invariants on validly-constructed input — they
    // restructure or substitute without introducing new slot-kind patterns.
    //
    // Empty-slot policy:
    //   `IRTArrow ([], ret, None)` is reserved for nullary functions
    //   produced by `mkFuncArrow []`. `ArrayElem` rejects this shape so
    //   nullary function calls don't get misclassified as rank-0 array
    //   indexing. Rank-0 arrays collapse to their element type at the
    //   `mkArrayLike` producer site, matching Subst.Resolve.
    //
    // Examples:
    //   [SVal; SVal] -> ret           : pure binary function
    //   [SIdx; SIdx] -> elem          : stored 2D array
    //   [SIdxVirt; SIdxVirt] -> elem  : virtual 2D generator
    //   [SIdx; SIdxVirt] -> elem      : stored array of virtual sub-arrays
    //   [SVal; SIdx] -> elem          : function returning a stored array
    //   [SIdxVirt; SIdx] -> elem      : INVALID — stored after virtual
    //
    // Retirement state: Segments 2 and 4 retired IRTFunc; Segments 3 and 5
    // retired IRTArray. IRTArrow is now the sole arrow-shaped type; functions,
    // stored arrays, and virtual arrays are distinguished by slot kind only.
    | IRTArrow of slots: IRArrowSlot list * result: IRType * identity: ArrayIdentity option

and IRArrowSlot =
    | SIdx of IRIndexType       // Storage-backed slot, consumed by an index value
    | SIdxVirt of IRIndexType   // Virtual slot — values computed on-the-fly, no storage
    | SVal of IRType            // Value/closure slot, consumed by any value of that type

/// Kind of loop object with arity tracking
and LoopType = {
    Kind: LoopKind
    Arity: int option        // None = arity-polymorphic
    ArrayTypes: IRType list  // Types of bound arrays (for MethodLoop)
    KernelType: IRType option  // Type of bound kernel (for ObjectLoop)
}

and LoopKind =
    | LKMethod   // MethodLoop - arrays bound, awaiting kernel
    | LKObject   // ObjectLoop - kernel bound, awaiting arrays

/// Literal values
and IRLit =
    | IRLitInt of int64
    | IRLitFloat of float
    | IRLitBool of bool
    | IRLitString of string
    | IRLitUnit

/// Binary operations
and IRBinOp =
    | IRAdd | IRSub | IRMul | IRDiv | IRMod | IRCaret  // ^ for power
    | IREq | IRNeq | IRLt | IRLe | IRGt | IRGe
    | IRAnd | IROr

/// Mode for binary array operations
and IRBinOpMode =
    | IRElementwise   // a + b (zip iteration)
    | IROuter         // a [+] b (cross iteration)

/// Unary operations
and IRUnaryOp =
    | IRNeg | IRNot

/// IR Expressions - SSA-like representation
and IRExpr =
    | IRLit of IRLit
    | IRVar of id: IRId * ty: IRType
    | IRParam of name: string * idx: int * ty: IRType
    | IRBinOp of IRBinOpMode * IRBinOp * IRExpr * IRExpr
    | IRUnaryOp of IRUnaryOp * IRExpr
    | IRArrayLit of IRExpr list * IRArrayType
    | IRIndex of array: IRExpr * index: IRExpr list * identity: ArrayIdentity option
    | IRSlice of array: IRExpr * dim: int * start: IRExpr * stop: IRExpr
    | IRCurry of array: IRExpr * index: IRExpr * resultRank: int
    | IRApp of func: IRExpr * args: IRExpr list * retType: IRType
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
    /// Fused form of `mask(A, lambda(x) -> ... contains(B, x) ...)` produced
    /// by the rewriteMaskContains pre-codegen pass. Carries the input array
    /// A, the set source B (a "buildOn" expression invariant w.r.t. the
    /// mask iteration), the parameter id that the residual references, and
    /// the residual predicate body — i.e., the lambda's body with the
    /// `IRContains(B, x)` call replaced by `IRSetMember(paramId, x)`.
    /// Codegen renders this as: build set from B, scan A, apply residual
    /// per element with set-membership replacing the contains call.
    | IRMaskWithSet of array: IRExpr * setSource: IRExpr * paramId: IRId * residual: IRExpr
    | IRIntersect of IRExpr * IRExpr          // intersect(A, B) - set intersection (deduplicated, order from A)
    | IRUnion of IRExpr * IRExpr              // union(A, B) - set union (deduplicated, A's elements first)
    | IRUnique of array: IRExpr               // unique(A) - dedup, first-occurrence order
    | IRContains of array: IRExpr * value: IRExpr  // contains(A, x) - membership test, returns bool
    /// Set-membership query against a precomputed set tied to a mask's
    /// paramId. Only appears inside an `IRMaskWithSet`'s residual: codegen
    /// for IRMaskWithSet generates a set variable from setSource, gives it
    /// a name, and renders IRSetMember(paramId, value) as
    /// `<setName>.count(value)`. The paramId identifies which mask's set
    /// this query belongs to (in case of future nested masks).
    | IRSetMember of paramId: IRId * value: IRExpr
    | IRGroupBy of values: IRExpr * grouping: IRExpr  // group_by(vals, gk) - apply grouping
    | IRGroupKeys of keys: IRExpr list               // group_keys(keys1, keys2, ...) - CSR grouping; multi-key ⇒ compound dispatch
    | IRSort of array: IRExpr * key: IRExpr          // sort(arr, key) - stable ascending sort by key
    | IRReduce of array: IRExpr * kernel: IRExpr     // reduce(arr, op) - fold innermost dim by kernel
    | IRZip of IRExpr list
    | IRAlign of arrays: IRExpr list * spec: AlignSpec
    | IRStack of IRExpr list
    | IRTranspose of array: IRExpr * perm: int list
    | IRReverse of array: IRExpr * dim: int
    | IRShift of array: IRExpr * dim: int * offset: IRExpr * boundary: BoundaryMode
    | IRDiag of array: IRExpr
    | IRJoin of arrays: IRExpr list * dim: int
    | IRSubset of array: IRExpr * dim: int * start: IRExpr * length: IRExpr
    | IRRange of IRIndexType * offset: IRExpr option
    | IRVirtualReverse of IRIndexType
    | IRBlocked of IRIndexType * blockSize: IRExpr
    | IRArity of resolved: int option * paramName: string  // None = unresolved (use paramName), Some n = bound
    | IRNth
    | IRZero
    | IRRank of array: IRExpr
    | IRPolyIndex of pack: IRExpr * index: IRExpr  // Dynamic poly-pack indexing: args[k]
    | IRExtent of array: IRExpr * dim: int
    // Ragged-extent marker: at the position this appears as an IRIndexType's
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
    // Opaque-extent marker: appears as an IRIndexType's Extent when the
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
    RetType: IRType
    Body: IRExpr
    IsStatic: bool
    IsCommutative: bool
    CommGroups: int list list
    Parallelism: (int * int) list
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
    Type: IRType
    IsMutable: bool
}

/// Information about a method_for construction
and MethodForInfo = {
    Arrays: IRExpr list
    Identities: ArrayIdentity list
    ArrayTypes: IRArrayType list
    SDimsPerArray: int list
    TotalSDims: int
    SharedIndexType: IRIndexType option  // For co-iteration: shared index space from 'in' clause
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
    ArrayTypes: IRArrayType list            // Array type info
    SharedIndexType: IRIndexType option     // For co-iteration (zip)
    SymcomStates: SymcomState list
    TriangularLevels: bool list
    SDimsPerArray: int list
    KernelInputRanks: int list
    KernelOutputRank: int
    KernelTDims: IRIndexType list           // T-dimension index types from kernel return type
    SpeedupFactor: int64
    ReynoldsSpeedup: int64        // Reynolds permutation count (n!); actual terms may be fewer after dedup
    HasReynolds: bool              // Whether kernel has Reynolds annotation
    OutputType: IRType             // Deduced output array type
    IsCoIteration: bool            // True for 'for ... in' co-iteration
}

and IRParam = {
    Name: string
    Type: IRType
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

// ============================================================================
// IRType normalization (Segment 6 — Path B-nested)
// ============================================================================
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
    SourceArity: int
}


/// Check if two index spaces are "shared" (same logical index space)
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
type LoopLevelInfo = {
    ArrayIndex: int
    LocalDimIndex: int
    ArityIndex: int
    GlobalLevelIndex: int
    IndexSpace: IndexSpaceInfo
}

/// Compute per-array S-dimension counts (accounting for arity expansion)
let computeSDimsPerArray (arrayTypes: IRArrayType list) : int list =
    arrayTypes |> List.map (fun arr ->
        arr.IndexTypes 
        |> List.filter (fun idx -> idx.Kind = SDimension) 
        |> List.sumBy (fun idx -> idx.Arity))

/// Build the flattened loop level structure
let buildLoopLevelStructure (arrayTypes: IRArrayType list) (sDimsPerArray: int list) : LoopLevelInfo list =
    let mutable levels = []
    let mutable globalIdx = 0
    
    for arrIdx in 0 .. arrayTypes.Length - 1 do
        let arr = arrayTypes.[arrIdx]
        let mutable localDimIdx = 0
        
        for idx in arr.IndexTypes do
            if idx.Kind = SDimension then
                for arityIdx in 0 .. idx.Arity - 1 do
                    levels <- levels @ [{
                        ArrayIndex = arrIdx
                        LocalDimIndex = localDimIdx
                        ArityIndex = arityIdx
                        GlobalLevelIndex = globalIdx
                        IndexSpace = { 
                            Tag = idx.Tag
                            Extent = idx.Extent
                            Symmetry = idx.Symmetry
                            Kind = idx.Kind
                            SourceArity = idx.Arity
                        }
                    }]
                    globalIdx <- globalIdx + 1
                localDimIdx <- localDimIdx + 1
    
    levels

// ============================================================================
// Identity Group Detection
// ============================================================================

type IdentityGroup = {
    StartIndex: int
    Arity: int
    Identity: ArrayIdentity
}

let partitionIntoIdentityGroups (identities: ArrayIdentity list) : IdentityGroup list =
    match identities with
    | [] -> []
    | first :: rest ->
        let mutable groups = []
        let mutable currentGroup = { StartIndex = 0; Arity = 1; Identity = first }
        
        for i, id in List.indexed rest do
            if sameIdentity id currentGroup.Identity then
                currentGroup <- { currentGroup with Arity = currentGroup.Arity + 1 }
            else
                groups <- groups @ [currentGroup]
                currentGroup <- { StartIndex = i + 1; Arity = 1; Identity = id }
        
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
    
    let levels = buildLoopLevelStructure arrayTypes sDimsPerArray
    if levels.IsEmpty then []
    else
        let inSameCommGroup i j =
            commGroups |> List.exists (fun cg ->
                List.contains i cg && List.contains j cg)
        
        let sameArrayIdentity i j =
            i < identities.Length && j < identities.Length &&
            sameIdentity identities.[i] identities.[j]
        
        let countSymmetricGroup (level: LoopLevelInfo) =
            if level.IndexSpace.Symmetry <> SymSymmetric && level.IndexSpace.Symmetry <> SymAntisymmetric then 1
            else
                let mutable count = 1
                let mutable idx = level.GlobalLevelIndex - 1
                while idx >= 0 do
                    let priorLevel = levels.[idx]
                    if priorLevel.ArrayIndex = level.ArrayIndex &&
                       priorLevel.LocalDimIndex = level.LocalDimIndex &&
                       priorLevel.ArityIndex = levels.[idx + 1].ArityIndex - 1 then
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
                    (sameArrayIdentity thisLevel.ArrayIndex priorLevel.ArrayIndex ||
                     indexSpacesMatch thisLevel.IndexSpace priorLevel.IndexSpace)
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
                    level.ArityIndex = priorLevel.ArityIndex + 1 &&
                    (level.IndexSpace.Symmetry = SymSymmetric ||
                     level.IndexSpace.Symmetry = SymAntisymmetric)
                
                let isCommutative =
                    inSameCommGroup level.ArrayIndex priorLevel.ArrayIndex &&
                    (sameArrayIdentity level.ArrayIndex priorLevel.ArrayIndex ||
                     indexSpacesMatch level.IndexSpace priorLevel.IndexSpace)
                
                match isSymmetric, isCommutative with
                | false, false -> SCNeither
                | true, false -> SCSymmetric
                | false, true -> SCCommutative
                | true, true ->
                    let symArity = countSymmetricGroup level
                    let commArity = countCommutativeGroup level
                    if factorial symArity >= factorial commArity then SCSymmetric 
                    else SCCommutative)

/// Determine which loop levels can use triangular iteration
let computeTriangularLevels
    (arrayTypes: IRArrayType list)
    (identities: ArrayIdentity list)
    (commGroups: int list list)
    (sDimsPerArray: int list) : bool list =
    
    let states = computeAllSymcomStates identities arrayTypes commGroups sDimsPerArray
    states |> List.map (fun s ->
        match s with
        | SCSymmetric | SCCommutative | SCBoth -> true
        | SCNeither -> false)

/// Compute speedup factor considering partial product symmetry
let computePartialProductSpeedup 
    (arrayTypes: IRArrayType list)
    (identities: ArrayIdentity list) 
    (commGroups: int list list)
    (sDimsPerArray: int list) : int64 =
    
    let identityGroups = partitionIntoIdentityGroups identities
    let hasFullIdentity = 
        identityGroups.Length = 1 && 
        identityGroups.[0].Arity = identities.Length &&
        identities.Length > 1
    
    if hasFullIdentity then
        let allPositions = [0 .. identities.Length - 1]
        let allCommutative = 
            commGroups |> List.exists (fun cg ->
                allPositions |> List.forall (fun p -> List.contains p cg))
        
        if allCommutative && not arrayTypes.IsEmpty then
            let r = identities.Length
            let d = arrayTypes.[0].IndexTypes 
                    |> List.filter (fun idx -> idx.Kind = SDimension) 
                    |> List.sumBy (fun idx -> idx.Arity)
            if d > 0 then pown (factorial r) d else 1L
        else
            arrayTypes 
            |> List.collect (fun arr -> arr.IndexTypes)
            |> List.filter (fun idx -> idx.Kind = SDimension && idx.Symmetry = SymSymmetric && idx.Arity > 1)
            |> List.fold (fun acc idx -> acc * factorial idx.Arity) 1L
    else
        let levels = buildLoopLevelStructure arrayTypes sDimsPerArray
        let states = computeAllSymcomStates identities arrayTypes commGroups sDimsPerArray
        
        let mutable speedup = 1L
        let mutable processedGroups = Set.empty
        
        for i, state in List.indexed states do
            if i < levels.Length then
                let level = levels.[i]
                let canTriangulate = 
                    match state with
                    | SCSymmetric | SCCommutative | SCBoth -> true
                    | SCNeither -> false
                
                if canTriangulate then
                    let groupArity =
                        match state with
                        | SCSymmetric -> level.IndexSpace.SourceArity
                        | _ -> 2  // Commutative pair
                    
                    let groupStart = i - groupArity + 1
                    let groupKey = (groupStart, groupArity)
                    
                    if groupArity > 1 && not (Set.contains groupKey processedGroups) then
                        speedup <- speedup * factorial groupArity
                        processedGroups <- Set.add groupKey processedGroups
        
        speedup

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

// ----------------------------------------------------------------------------
// ContainsProbe — payload for the contains-aware-mask optimization
// ----------------------------------------------------------------------------
//
// A ContainsProbe records a single use of `contains(B, x)` somewhere
// inside an expression. The bottom-up walker (exprAttrs, defined later)
// collects probes from every subexpression and propagates them upward
// through every compositional construct (binops, ifs, function-call
// arguments, nested lambda bodies, match cases, let bodies, ...). The
// propagation is *consumed* at IRMask — a mask's predicate is the only
// place where probes get resolved. Anywhere else, they keep flowing up
// until they reach a mask.
//
// IRFuncDef carries a Probes list summarizing the contains calls inside
// its body, with BuildOn expressions stated in terms of the function's
// formal parameters. At a call site, parameters are substituted with
// the actual arguments and the probes are imported into the caller's
// analysis — so a mask predicate that calls a function whose body has
// a contains gets the same propagation as if the contains were inline.
//
// The optimization driven by these probes lives at codegen: when a mask
// is rendered, the codegen inspects its predicate's probes, hoists each
// hoistable build (B that doesn't depend on the mask's kernel binders)
// into a `std::unordered_set` constructed in the mask's preamble, and
// substitutes `set.count(value)` for the original IRContains node when
// rendering the predicate body. Non-hoistable probes (B references the
// kernel param) fall through to the existing per-iteration scan.
//
// `Node` is the actual IRContains object reference. Codegen builds a
// substitution map keyed on this reference; matching at the contains
// site uses object identity to decide whether to render the substituted
// form or fall back to the linear-scan IIFE.
//
// Probes propagate through every compositional construct. They also
// flow through function-call boundaries: when exprAttrs walks an
// IRApp(IRVar(fId), args, _), it consults a per-module callables
// table; if `fId` resolves, the function body is walked with parameter
// substitution applied. This unifies how lambdas and functions are
// treated for probe analysis — both are "callables whose body we walk
// through." Recursion is handled by a visited-set passed through the
// walker.
//
// Caveat: the codegen substitution mechanism in Phase C operates on
// `Node` reference equality within the rendered tree. Probes whose
// IRContains lives inside a called function's body propagate to the
// caller's analysis (so the user can see them), but the substitution
// itself can't fire from the caller's mask boundary — the rendered
// tree only contains the IRApp call, not the inlined contains. The
// mask renderer applies a reachability check before hoisting, so
// unreachable probes don't generate unused preamble. Cross-procedural
// calls compile to ordinary function calls with the IIFE inside.
//
// `BuildOn` is the array argument (B). Tests assert on this; the
// hoistability check (FreeVars(BuildOn) ∩ maskBinders = ∅) also reads it.

type ContainsProbe = {
    Node:    IRExpr
    BuildOn: IRExpr
}

/// Active pattern recognizing "this expression is a probe over some array."
/// Returns (containsNode, arrayArg, valueArg). Future expansion (e.g. an
/// IRPosition or IRCount operator with similar build-then-probe shape) is
/// done by adding cases here; the rest of the propagator stays unchanged.
let (|ContainsProbeAt|_|) (expr: IRExpr) : (IRExpr * IRExpr * IRExpr) option =
    match expr with
    | IRContains (arr, value) -> Some (expr, arr, value)
    | _ -> None

/// Type definition in IR
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
type IRModule = {
    Name: string
    Types: IRTypeDef list
    Functions: IRFuncDef list
    Bindings: IRBinding list
    /// Diagnostics: static function usage tracking (function name → usage kind)
    /// "compile-time" | "runtime" | "both" | "unused"
    StaticFunctionUsage: Map<string, string>
}

/// IR Program
type IRProgram = {
    Modules: IRModule list
}

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
        Parallelism = []
        IsArityPoly = false
        ArityParam = None
        Captures = captures
    }

/// Deduce output array type from loop application
/// According to formalism section 10.9:
/// 1. Group arrays by identity (consecutive identical arrays)
/// 2. For each group: if comm + arity > 1, use SymIdx; else Idx
/// 3. Concatenate S-dims from all groups
/// 4. Add T-dims from kernel output
let deduceOutputType 
    (arrayTypes: IRArrayType list) 
    (identities: ArrayIdentity list) 
    (commGroups: int list list)
    (sDimsPerArray: int list)
    (kernelTDims: IRIndexType list)
    (elemType: IRType)
    (builder: IRBuilder) : IRType =
    
    if arrayTypes.IsEmpty then IRTUnit
    else
        // Step 1: Build identity groups (consecutive identical arrays)
        let groups = 
            let mutable result = []
            let mutable currentGroup = []
            let mutable currentId = None
            
            for i, (arrTy, identity) in List.zip arrayTypes identities |> List.indexed do
                let sDims = if i < sDimsPerArray.Length then sDimsPerArray.[i] else 0
                match currentId with
                | None -> 
                    currentId <- Some identity
                    currentGroup <- [(i, arrTy, sDims)]
                | Some id when sameIdentity id identity ->
                    currentGroup <- currentGroup @ [(i, arrTy, sDims)]
                | Some _ ->
                    result <- result @ [(currentId.Value, currentGroup)]
                    currentId <- Some identity
                    currentGroup <- [(i, arrTy, sDims)]
            
            if currentGroup.Length > 0 then
                result <- result @ [(currentId.Value, currentGroup)]
            result
        
        // Step 2 & 3: Build S-dim index types for each group
        let outputSDims = 
            groups |> List.collect (fun (_, groupMembers) ->
                let arity = groupMembers.Length
                let inCommGroup = 
                    // Check if all indices of this group are in the same comm group
                    let indices = groupMembers |> List.map (fun (i, _, _) -> i)
                    commGroups |> List.exists (fun cg -> 
                        indices |> List.forall (fun i -> List.contains i cg))
                
                if inCommGroup && arity > 1 then
                    // Commutative group: create symmetric index with higher arity
                    // Takes S-dims from first member, uses arity = group size
                    match groupMembers with
                    | [] -> []
                    | (_, arrTy, numSDims) :: _ ->
                        let sDimIndices = arrTy.IndexTypes |> List.take (min numSDims arrTy.IndexTypes.Length)
                        sDimIndices |> List.map (fun idx ->
                            { idx with 
                                Arity = arity
                                Symmetry = SymSymmetric
                                Id = builder.FreshId() })
                else
                    // Non-commutative: each member contributes its own S-dims
                    groupMembers |> List.collect (fun (_, arrTy, numSDims) ->
                        let sDimIndices = arrTy.IndexTypes |> List.take (min numSDims arrTy.IndexTypes.Length)
                        sDimIndices |> List.map (fun idx ->
                            { idx with 
                                Arity = 1
                                Symmetry = SymNone
                                Id = builder.FreshId() })))
            // Drop indices tagged as "consumed by the kernel" — these inner
            // dimensions are not part of the output iteration structure;
            // the kernel implicitly receives a sub-array along them and
            // produces a per-outer-iteration result.
            //
            //   - __group_member        : ragged inner of group_by output
            //   - __raggedidx          : closed RaggedIdx<lens>
            //   - __raggedidx_inline   : ragged literal's inferred inner
            //   - __raggedidx_opaque   : opaque RaggedIdx<_>
            //   - __depidx_inner       : DepIdx inner (formula-driven extent)
            //
            // Without this filter, the output type retains the consumed dim,
            // making subsequent operations (e.g., reduce(method_for_result))
            // see a multi-rank array where there should be one outer dim.
            // Print-path code historically compensated by special-casing
            // ragged outputs to rank-1, but that only worked when no further
            // type-querying operation was applied.
            |> List.filter (fun idx ->
                match idx.Tag with
                | Some "__group_member"
                | Some "__raggedidx"
                | Some "__raggedidx_inline"
                | Some "__raggedidx_opaque"
                | Some "__depidx_inner" -> false
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
    ArityComponent: int
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
}

/// Build symmetry vector from output type
/// Consecutive equal values indicate symmetric dimensions
let buildSymmVec (outputType: IRType) : int list =
    match outputType with
    | ArrayElem arr ->
        let mutable symmVec = []
        let mutable groupNum = 1
        let mutable prevSymm = None
        
        for idx in arr.IndexTypes do
            for arityIdx in 0 .. idx.Arity - 1 do
                let isSymmetric = idx.Symmetry = SymSymmetric && idx.Arity > 1
                if isSymmetric && arityIdx > 0 then
                    // Continue same group
                    symmVec <- symmVec @ [groupNum]
                else
                    // Start new group
                    if prevSymm = Some true && arityIdx = 0 then
                        groupNum <- groupNum + 1
                    symmVec <- symmVec @ [groupNum]
                    if not isSymmetric then
                        groupNum <- groupNum + 1
                prevSymm <- Some isSymmetric
        symmVec
    | _ -> []

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
    
    // Extract kernel info through `resolveCallable`, which handles
    // both module.Functions references and let-binding aliases via
    // the CallablesTable. Reynolds wrapping is peeled and its
    // isAntisymmetric flag flows through; the inner expression is
    // resolved the same way.
    let (kernelParams, kernelBody, commGroups, captures, isAntisymmetric) =
        let extract (k: IRExpr) =
            match resolveCallable k with
            | Some c -> Some (c.Params, c.Body, c.CommGroups, c.Captures)
            | None -> None
        match info.Kernel with
        | IRReynolds (inner, isAnti) ->
            match extract inner with
            | Some (p, b, g, c) -> (p, b, g, c, isAnti)
            | None -> ([], IRLit IRLitUnit, [], [], false)
        | other ->
            match extract other with
            | Some (p, b, g, c) -> (p, b, g, c, false)
            | None -> ([], IRLit IRLitUnit, [], [], false)
    
    // Map array position to kernel param
    let paramByArrayPos = 
        kernelParams |> List.mapi (fun i p -> (i, p)) |> Map.ofList
    
    // Helper: create an ElementBinding for an array at a given arity component
    let mkElement (arrayPos: int) (arityComponent: int) (dimIndex: int) =
        let arrName = if arrayPos < arrayNames.Length then arrayNames.[arrayPos] else sprintf "arr%d" arrayPos
        let arrType = if arrayPos < arrayTypes.Length then Some arrayTypes.[arrayPos] else None
        let elemType = arrType |> Option.map (fun t -> t.ElemType) |> Option.defaultValue (IRTScalar ETFloat64)
        let arrRank = arrType |> Option.map (fun t -> t.IndexTypes |> List.sumBy (fun i -> i.Arity))
                              |> Option.defaultValue 1
        let virtualKind =
            if arrayPos < arrays.Length then
                match arrays.[arrayPos] with
                | IRRange (_, offset) -> VirtualRange offset
                | IRVirtualReverse _ -> VirtualReverse
                | _ -> RealArray
            else RealArray
        let param = Map.tryFind arrayPos paramByArrayPos
        let paramName = param |> Option.map (fun p -> p.Name) |> Option.defaultValue (sprintf "p%d" arrayPos)
        let paramVarId = param |> Option.map (fun p -> p.VarId) |> Option.defaultValue -1
        {
            ArrayPosition = arrayPos
            ArrayName = arrName
            ParamName = paramName
            ParamVarId = paramVarId
            DimIndex = dimIndex
            ArityComponent = arityComponent
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
                let numLevels = idx.Arity
                let isSymmetric = idx.Symmetry = SymSymmetric
                let isAntisymmetric = idx.Symmetry = SymAntisymmetric
                let isTriangular = isSymmetric || isAntisymmetric
                
                [0 .. numLevels - 1] |> List.map (fun level ->
                    let indexName = sprintf "__i%d" level
                    let deps = if isTriangular && level > 0 then [0 .. level - 1] else []
                    let strictOffset =
                        if isTriangular && isAntisymmetric then level
                        else 0
                    let isParallel = level = 0 && not isTriangular
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
                        IsParallel = isParallel
                        State = state
                        Elements = elements
                    })
            | None -> []  // Should not happen
        else
            // Outer product: one element per level
            let loopLevels = buildLoopLevelStructure arrayTypes sDimsPerArray
            let triangularLevels = info.TriangularLevels
            let symcomStates = info.SymcomStates
            
            // Compute the iminMap
            let iminMap = 
                loopLevels |> List.mapi (fun globalIdx level ->
                    let state = if globalIdx < symcomStates.Length then symcomStates.[globalIdx] else SCNeither
                    match state with
                    | SCNeither -> globalIdx
                    | SCSymmetric -> globalIdx - 1
                    | SCCommutative -> globalIdx - 1
                    | SCBoth -> globalIdx - 1
                )
            
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
                let isParallel = level = 0 && not isTriangular
                let strictOffset =
                    if isTriangular && levelInfo.IndexSpace.Symmetry = SymAntisymmetric then 1
                    else 0
                
                let element = mkElement arrayPos levelInfo.ArityIndex levelInfo.LocalDimIndex
                
                {
                    Level = level
                    IndexName = indexName
                    Extent = levelInfo.IndexSpace.Extent
                    ExtentArrayRef = arrName
                    ExtentDimRef = levelInfo.LocalDimIndex
                    BoundDependencies = deps
                    StrictOffset = strictOffset
                    IsParallel = isParallel
                    State = state
                    Elements = [element]
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
    }

// ============================================================================
// Expression Mapping (bottom-up rewriter)
// ============================================================================

/// Apply f to every sub-expression bottom-up, then to the root.
/// f should return the expression unchanged for cases it doesn't handle.
let rec mapIRExpr (f: IRExpr -> IRExpr) (expr: IRExpr) : IRExpr =
    let m = mapIRExpr f
    let ms = List.map m
    let mapped =
        match expr with
        // Leaves
        | IRLit _ | IRVar _ | IRParam _ | IRNth | IRZero
        | IRRange _ | IRVirtualReverse _ | IRArity _
        | IROpaqueExtent -> expr
        // Unary
        | IRUnaryOp (op, e) -> IRUnaryOp (op, m e)
        | IRTupleProj (e, i, flat) -> IRTupleProj (m e, i, flat)
        | IRTupleDecons e -> IRTupleDecons (m e)
        | IRFieldAccess (e, fld) -> IRFieldAccess (m e, fld)
        | IRPure e -> IRPure (m e)
        | IRCompute e -> IRCompute (m e)
        | IRReynolds (e, a) -> IRReynolds (m e, a)
        | IRTranspose (e, p) -> IRTranspose (m e, p)
        | IRReverse (e, d) -> IRReverse (m e, d)
        | IRDiag e -> IRDiag (m e)
        | IRRank e -> IRRank (m e)
        | IRExtent (e, d) -> IRExtent (m e, d)
        | IRRaggedLookup l -> IRRaggedLookup (m l)
        | IRAssign (t, v) -> IRAssign (m t, m v)
        | IRForRange (vid, lo, hi, body) -> IRForRange (vid, m lo, m hi, m body)
        // Binary
        | IRBinOp (mode, op, l, r) -> IRBinOp (mode, op, m l, m r)
        | IRTupleCons (h, t) -> IRTupleCons (m h, m t)
        | IRBind (c, k) -> IRBind (m c, m k)
        | IRParallel (a, b, d) -> IRParallel (m a, m b, d)
        | IRFusion (a, b) -> IRFusion (m a, m b)
        | IRChoice (a, b) -> IRChoice (m a, m b)
        | IRArrayProduct (a, b) -> IRArrayProduct (m a, m b)
        | IRComposeObj (a, b) -> IRComposeObj (m a, m b)
        | IRComposeMeth (a, b) -> IRComposeMeth (m a, m b)
        | IRCompose (a, b) -> IRCompose (m a, m b)
        | IRFunctorMap (fn, c) -> IRFunctorMap (m fn, m c)
        | IRGuard (c, b) -> IRGuard (m c, m b)
        | IRReplicate (c, b) -> IRReplicate (m c, m b)
        | IRMask (a, p) -> IRMask (m a, m p)
        | IRMaskWithSet (a, s, pid, r) -> IRMaskWithSet (m a, m s, pid, m r)
        | IRIntersect (a, b) -> IRIntersect (m a, m b)
        | IRUnion (a, b) -> IRUnion (m a, m b)
        | IRUnique a -> IRUnique (m a)
        | IRContains (a, v) -> IRContains (m a, m v)
        | IRSetMember (pid, v) -> IRSetMember (pid, m v)
        | IRGroupBy (v, k) -> IRGroupBy (m v, m k)
        | IRGroupKeys ks -> IRGroupKeys (List.map m ks)
        | IRSort (a, k) -> IRSort (m a, m k)
        | IRReduce (a, k) -> IRReduce (m a, m k)
        | IRPolyIndex (p, i) -> IRPolyIndex (m p, m i)
        // Ternary
        | IRIf (c, t, e) -> IRIf (m c, m t, m e)
        | IRSlice (arr, d, s, e) -> IRSlice (m arr, d, m s, m e)
        | IRSubset (arr, d, s, l) -> IRSubset (m arr, d, m s, m l)
        | IRShift (arr, d, off, bm) -> IRShift (m arr, d, m off, bm)
        // 1 + list
        | IRIndex (arr, idxs, id) -> IRIndex (m arr, ms idxs, id)
        | IRApp (fn, args, rt) -> IRApp (m fn, ms args, rt)
        | IRCurry (arr, idx, r) -> IRCurry (m arr, m idx, r)
        // Lists
        | IRArrayLit (es, ty) -> IRArrayLit (ms es, ty)
        | IRTuple es -> IRTuple (ms es)
        | IRComplex (re, im) -> IRComplex (m re, m im)
        | IRSequence es -> IRSequence (ms es)
        | IRZip es -> IRZip (ms es)
        | IRAlign (es, sp) -> IRAlign (ms es, sp)
        | IRStack es -> IRStack (ms es)
        | IRJoin (es, d) -> IRJoin (ms es, d)
        // Structured
        | IRLet (id, v, b) -> IRLet (id, m v, m b)
        | IRStructLit (tn, flds) -> IRStructLit (tn, flds |> List.map (fun (n, e) -> (n, m e)))
        | IRBlocked (it, bs) -> IRBlocked (it, m bs)
        | IRMatch (scr, cases) ->
            IRMatch (m scr, cases |> List.map (fun c ->
                { c with Guard = c.Guard |> Option.map m; Body = m c.Body }))
        | IRMethodFor info ->
            IRMethodFor { info with Arrays = ms info.Arrays }
        | IRObjectFor info ->
            IRObjectFor { info with Kernel = m info.Kernel }
        | IRApplyCombinator info ->
            IRApplyCombinator { info with Loop = m info.Loop; Kernel = m info.Kernel; Arrays = ms info.Arrays }
    f mapped

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
        | _ -> e
    mapIRExpr substInNode expr

/// Get the type of an IRExpr where determinable from the node directly.
/// Used at HM call sites to extract arg types for unification against
/// param types. Returns None for nodes whose type isn't carried locally.
let exprTypeIfKnown (expr: IRExpr) : IRType option =
    match expr with
    | IRVar (_, ty) -> Some ty
    | IRParam (_, _, ty) -> Some ty
    | IRApp (_, _, retType) -> Some retType
    | IRArrayLit (_, aty) -> Some (mkArrayLike aty)
    | IRLit (IRLitInt _) -> Some (IRTScalar ETInt64)
    | IRLit (IRLitFloat _) -> Some (IRTScalar ETFloat64)
    | IRLit (IRLitBool _) -> Some (IRTScalar ETBool)
    | IRLit (IRLitString _) -> Some (IRTScalar ETString)
    | IRFieldAccess _ | IRIndex _ | IRBinOp _ | IRUnaryOp _ ->
        // These nodes' types aren't carried locally. Leaving as None means
        // call sites with such args fall back to the function's declared
        // type-var positions; they'll be substituted with whatever else
        // unification provides.
        None
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

/// Minimal type inferrer for the lift pass. Only handles cases that show up
/// as the result of an inline form (mask/sort/etc preserve their argument's
/// type). Falls back to IRTUnit for shapes the lift pass doesn't expect to
/// hit; if a fallback ever fires in practice, the resulting IRVar's type
/// would be wrong and a downstream codegen step would surface it.
/// Map from struct name to its fields, used by liftInferType for IRFieldAccess
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

let rec liftInferType (expr: IRExpr) : IRType =
    match expr with
    | IRVar (_, ty) -> ty
    | IRParam (_, _, ty) -> ty
    | IRApp (_, _, retTy) -> retTy
    | IRArrayLit (_, arrTy) -> mkArrayLike arrTy
    | IRMask (arr, _) -> liftInferType arr
    | IRMaskWithSet (arr, _, _, _) -> liftInferType arr  // Same element type, filtered extent
    | IRSort (arr, _) -> liftInferType arr
    | IRIntersect (a, _) -> liftInferType a
    | IRUnion (a, _) -> liftInferType a
    | IRUnique a -> liftInferType a
    | IRContains _ -> IRTScalar ETBool
    | IRSetMember _ -> IRTScalar ETBool
    | IRGroupBy (v, _) -> liftInferType v  // Approximation; codegen recomputes
    | IRGroupKeys _ -> IRTUnit  // GroupKeys is opaque
    | IRLet (_, _, body) -> liftInferType body
    | IRIndex (arr, idxs, _) ->
        match liftInferType arr with
        | ArrayElem a when idxs.Length >= a.IndexTypes.Length -> a.ElemType
        | ArrayElem a -> mkArrayLike { a with IndexTypes = a.IndexTypes |> List.skip idxs.Length }
        | t -> t
    | IRTupleProj (e, i, _) ->
        match liftInferType e with
        | IRTTuple ts when i < ts.Length -> ts.[i]
        | t -> t
    | IRFieldAccess (obj, field) ->
        // Phase D / companion-array gap: resolve field type via the struct
        // definitions map. Returns IRTUnit if the obj isn't an IRTNamed
        // struct (typecheck rejects this; the IRTUnit fallback is for
        // robustness against malformed IR — codegen surfaces the issue).
        match tryLookupFieldType (liftInferType obj) field with
        | Some ty -> ty
        | None -> IRTUnit
    | IRCompute e -> liftInferType e
    | IRPure e -> liftInferType e
    | IRApplyCombinator info -> info.OutputType
    | IRIf (_, t, _) -> liftInferType t
    | IRMatch (_, c :: _) -> liftInferType c.Body
    | IRMatch (_, []) -> IRTUnit
    | _ -> IRTUnit

/// Predicate: is this an inline form that needs lifting when in a non-blessed
/// position? Note: we deliberately exclude IRReduce — reduce's codegen
/// handles inline forms via IIFE (when the array is named) and the array
/// argument to reduce IS what we lift, not reduce itself.
let isInlineForm (e: IRExpr) : bool =
    match e with
    | IRMask _ | IRMaskWithSet _ | IRSort _ | IRIntersect _ | IRUnion _ | IRUnique _
    | IRGroupBy _ | IRGroupKeys _ -> true
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
        | IRLet (id, v, body) -> loop (acc @ [(id, liftInferType v, v)]) body
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
        match liftInferType e with
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
        let ty = liftInferType inner
        (peeled @ [(id, ty, inner)], IRVar (id, ty))
    elif isArrayFieldAccess inner then
        // Phase D: hoist `t.samples` (when samples is array-typed) into a
        // let-RHS so codegen can synthesize `<bound_name>_extents`.
        let id = builder.FreshId()
        let ty = liftInferType inner
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
        let ty = liftInferType e
        (peeled @ [(id, ty, e)], IRVar (id, ty))
    | e when isArrayFieldAccess e ->
        // Phase D: same hoisting as liftChild, so struct field values and
        // function args carrying `t.samples` get the same treatment.
        let id = builder.FreshId()
        let ty = liftInferType e
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
        let (binds, aFinal) = liftChild builder a'
        wrapLets binds (IRMask (aFinal, p'))
    | IRMaskWithSet (a, s, pid, r) ->
        // Same shape as IRMask: lift the array arg (which may be another
        // inline form) so codegen can name it. setSource is also lifted
        // through liftExpr for nested inline forms. Residual carries
        // IRSetMember references; lift-pass leaves those alone.
        let a' = liftExpr builder a
        let s' = liftExpr builder s
        let r' = liftExpr builder r
        let (bindsA, aFinal) = liftChild builder a'
        let (bindsS, sFinal) = liftChild builder s'
        wrapLets (bindsA @ bindsS) (IRMaskWithSet (aFinal, sFinal, pid, r'))
    | IRSort (a, k) ->
        let a' = liftExpr builder a
        let k' = liftExpr builder k
        let (binds, aFinal) = liftChild builder a'
        wrapLets binds (IRSort (aFinal, k'))
    | IRIntersect (a, b) ->
        let a' = liftExpr builder a
        let b' = liftExpr builder b
        let (bindsA, aFinal) = liftChild builder a'
        let (bindsB, bFinal) = liftChild builder b'
        wrapLets (bindsA @ bindsB) (IRIntersect (aFinal, bFinal))
    | IRUnion (a, b) ->
        let a' = liftExpr builder a
        let b' = liftExpr builder b
        let (bindsA, aFinal) = liftChild builder a'
        let (bindsB, bFinal) = liftChild builder b'
        wrapLets (bindsA @ bindsB) (IRUnion (aFinal, bFinal))
    | IRUnique a ->
        let a' = liftExpr builder a
        let (binds, aFinal) = liftChild builder a'
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

    // IRSetMember is a leaf-level membership query against a precomputed
    // set tied to a paramId. The set itself was built by the enclosing
    // IRMaskWithSet's preamble — no inline form to lift here. Just walk
    // the value expression and reconstruct.
    | IRSetMember (paramId, v) ->
        IRSetMember (paramId, liftExpr builder v)

    // Single-child consumers where the array slot can hold an inline form
    | IRReduce (arr, kernel) ->
        let arr' = liftExpr builder arr
        let kernel' = liftExpr builder kernel
        let (binds, arrFinal) = liftChild builder arr'
        wrapLets binds (IRReduce (arrFinal, kernel'))
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
    | IRTranspose (arr, p) ->
        let arr' = liftExpr builder arr
        let (binds, arrFinal) = liftChild builder arr'
        wrapLets binds (IRTranspose (arrFinal, p))
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
                    let ty = liftInferType inner
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
                    let ty = liftInferType inner
                    (accB @ peeled @ [(id, ty, inner)], accA @ [IRVar (id, ty)])
                else
                    (accB @ peeled, accA @ [inner])) ([], [])
        wrapLets binds (IRApplyCombinator { info with Loop = loop'; Kernel = kernel'; Arrays = arraysFinal })

/// Lift inline forms across an entire IR module's bindings and functions.
let liftInlineFormsModule (modul: IRModule) (builder: IRBuilder) : IRModule =
    // Phase D / companion-array gap: populate the struct fields cache so
    // liftInferType can resolve IRFieldAccess result types. Required for
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
    | SymSymmetric -> sprintf "SymIdx<%d, %s>" idx.Arity extentStr
    | SymAntisymmetric -> sprintf "AntisymIdx<%d, %s>" idx.Arity extentStr
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
    | SymSymmetric -> sprintf "SymIdx<%d, %s>" idx.Arity extentStr
    | SymAntisymmetric -> sprintf "AntisymIdx<%d, %s>" idx.Arity extentStr
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

let rec collectVarRefsIR (expr: IRExpr) : Set<IRId> =
    match expr with
    | IRVar (id, _) -> Set.singleton id
    | IRLit _ | IRParam _ | IRNth | IRZero | IROpaqueExtent -> Set.empty
    | IRBinOp (_, _, l, r) -> Set.union (collectVarRefsIR l) (collectVarRefsIR r)
    | IRUnaryOp (_, e) -> collectVarRefsIR e
    | IRIf (c, t, e) -> Set.unionMany [collectVarRefsIR c; collectVarRefsIR t; collectVarRefsIR e]
    | IRLet (_, v, b) -> Set.union (collectVarRefsIR v) (collectVarRefsIR b)
    | IRApp (f, args, _) -> Set.unionMany (collectVarRefsIR f :: List.map collectVarRefsIR args)
    | IRTuple es -> Set.unionMany (List.map collectVarRefsIR es)
    | IRComplex (re, im) -> Set.union (collectVarRefsIR re) (collectVarRefsIR im)
    | IRTupleProj (e, _, _) -> collectVarRefsIR e
    | IRArrayLit (es, _) -> Set.unionMany (List.map collectVarRefsIR es)
    | IRIndex (arr, idxs, _) -> Set.unionMany (collectVarRefsIR arr :: List.map collectVarRefsIR idxs)
    | IRFieldAccess (obj, _) -> collectVarRefsIR obj
    | IRStructLit (_, fields) -> Set.unionMany (fields |> List.map (snd >> collectVarRefsIR))
    | IRCompute inner -> collectVarRefsIR inner
    | IRReynolds (inner, _) -> collectVarRefsIR inner
    | IRMethodFor info -> Set.unionMany (List.map collectVarRefsIR info.Arrays)
    | IRObjectFor info -> collectVarRefsIR info.Kernel
    | IRApplyCombinator info ->
        Set.unionMany [collectVarRefsIR info.Loop; collectVarRefsIR info.Kernel; Set.unionMany (List.map collectVarRefsIR info.Arrays)]
    | IRMatch (scrut, cases) ->
        Set.union (collectVarRefsIR scrut) (cases |> List.map (fun c -> collectVarRefsIR c.Body) |> Set.unionMany)
    | IRAssign (t, v) -> Set.union (collectVarRefsIR t) (collectVarRefsIR v)
    | IRForRange (vid, lo, hi, body) ->
        Set.unionMany [collectVarRefsIR lo; collectVarRefsIR hi; Set.remove vid (collectVarRefsIR body)]
    | IRGuard (c, b) -> Set.union (collectVarRefsIR c) (collectVarRefsIR b)
    | IRMask (a, p) -> Set.union (collectVarRefsIR a) (collectVarRefsIR p)
    | IRMaskWithSet (a, s, _, r) ->
        // paramId is a binder, not a reference; residual may reference it
        // but it's bound by the mask, not free. Union of array, setSource,
        // and residual; the binder is removed by exprAttrs's BoundVars logic.
        Set.unionMany [collectVarRefsIR a; collectVarRefsIR s; collectVarRefsIR r]
    | IRIntersect (a, b) -> Set.union (collectVarRefsIR a) (collectVarRefsIR b)
    | IRUnion (a, b) -> Set.union (collectVarRefsIR a) (collectVarRefsIR b)
    | IRUnique a -> collectVarRefsIR a
    | IRContains (a, v) -> Set.union (collectVarRefsIR a) (collectVarRefsIR v)
    | IRSetMember (_, v) -> collectVarRefsIR v  // paramId is a binder reference, not a free var
    | IRGroupBy (v, k) -> Set.union (collectVarRefsIR v) (collectVarRefsIR k)
    | IRGroupKeys ks -> ks |> List.map collectVarRefsIR |> Set.unionMany
    | IRSort (a, k) -> Set.union (collectVarRefsIR a) (collectVarRefsIR k)
    | IRReduce (a, k) -> Set.union (collectVarRefsIR a) (collectVarRefsIR k)
    | IRExtent (a, _) -> collectVarRefsIR a
    | IRRaggedLookup l -> collectVarRefsIR l
    | IRSequence es -> Set.unionMany (List.map collectVarRefsIR es)
    | IRParallel (a, b, _) -> Set.union (collectVarRefsIR a) (collectVarRefsIR b)
    | IRFusion (a, b) -> Set.union (collectVarRefsIR a) (collectVarRefsIR b)
    | IRChoice (a, b) -> Set.union (collectVarRefsIR a) (collectVarRefsIR b)
    | IRBind (c, k) -> Set.union (collectVarRefsIR c) (collectVarRefsIR k)
    | IRFunctorMap (f, c) -> Set.union (collectVarRefsIR f) (collectVarRefsIR c)
    | IRPure e -> collectVarRefsIR e
    | _ -> Set.empty

// ============================================================================
// AnalysisContext — unified callable-walking for cross-procedural analysis
// ============================================================================
//
// `exprAttrs` walks an expression tree to compute attributes (FreeVars,
// BoundVars, IsPure, Probes). For probes specifically, the analysis is
// "complete" only if it can see every IRContains the program will
// execute. That includes contains calls inside the bodies of functions
// called from the analyzed expression. The IRApp arm follows
// IRVar(fId) references via the CallablesTable, substitutes the
// callee's params with the call's args, and walks the body — surfacing
// probes that originate inside the callee's body.
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
// rewriteMaskContains — fuse mask(A, lambda(x) -> ... contains(B, x) ...)
// ============================================================================
//
// The IR-level optimization that replaces Phase C's codegen substitution
// machinery with a structural rewrite. For each IRMask in the module:
//   1. Resolve the predicate to an IRCallable.
//   2. If the callable has exactly one parameter (TypeCheck enforces
//      this; defensive otherwise), scan its body for IRContains nodes.
//   3. A node is "hoistable" iff its BuildOn (the array argument)
//      doesn't reference the predicate's parameter — i.e., the set
//      can be precomputed once per mask invocation, not per element.
//   4. If exactly one hoistable IRContains is found, rewrite to:
//        IRMaskWithSet(arr, buildOn, paramId, residual)
//      where `residual` is the callable's body with the IRContains
//      replaced by `IRSetMember(paramId, value)`.
//   5. Otherwise (zero or 2+ hoistable nodes), leave as IRMask.
//      Option A (per design discussion): multi-contains masks fall
//      back to per-element evaluation. Revisit if real code hits this.
//
// This handles the LOCAL case: contains directly inside the predicate's
// body. The CROSS-PROCEDURAL case (contains inside a function called
// from the predicate) is M1.2 v2 — not yet implemented. The unified
// analysis sees those probes for diagnostic purposes but optimization
// doesn't fire across the call.

/// Collect hoistable IRContains nodes from an expression. Returns
/// (containsNode, buildOn, value) for each IRContains whose BuildOn
/// doesn't reference paramId. Walks via mapIRExpr for full coverage.
let private collectHoistableContains (paramId: IRId) (body: IRExpr)
        : (IRExpr * IRExpr * IRExpr) list =
    let mutable results = []
    let _ = mapIRExpr (fun e ->
        match e with
        | IRContains (buildOn, value) ->
            let refs = collectVarRefsIR buildOn
            if not (Set.contains paramId refs) then
                results <- (e, buildOn, value) :: results
            e
        | _ -> e) body
    List.rev results

/// Replace a specific subexpression (by reference identity) with another.
/// Used to swap an IRContains node for an IRSetMember node within a
/// predicate body. Reference identity ensures we only replace the
/// specific node we collected; structurally-equal duplicates elsewhere
/// in the tree are left alone.
let private replaceNodeByRef (target: IRExpr) (replacement: IRExpr) (root: IRExpr) : IRExpr =
    mapIRExpr (fun e ->
        if System.Object.ReferenceEquals(e, target) then replacement
        else e) root

/// Attempt to rewrite a single IRMask to IRMaskWithSet. Returns the
/// rewritten form if the pattern matches, otherwise None.
let private tryRewriteMaskContains (expr: IRExpr) : IRExpr option =
    match expr with
    | IRMask (arr, pred) ->
        match resolveCallable pred with
        | None -> None  // Predicate isn't a recognizable callable
        | Some callable when callable.Params.Length <> 1 -> None
        | Some callable ->
            let paramId = callable.Params.[0].VarId
            let hoistable = collectHoistableContains paramId callable.Body
            match hoistable with
            | [(node, buildOn, value)] ->
                let setMember = IRSetMember (paramId, value)
                let residual = replaceNodeByRef node setMember callable.Body
                Some (IRMaskWithSet (arr, buildOn, paramId, residual))
            | _ ->
                // 0 hoistable: nothing to optimize.
                // 2+ hoistable: multi-contains case — skip per Option A
                // (design decision: revisit when real code demands it).
                None
    | _ -> None

/// Apply tryRewriteMaskContains bottom-up across an expression tree.
/// Bottom-up means inner masks are rewritten before outer ones — useful
/// when a mask's array argument is itself a mask, so the inner rewrite
/// is visible when the outer is considered.
let private rewriteMaskContainsExpr (expr: IRExpr) : IRExpr =
    mapIRExpr (fun e ->
        match tryRewriteMaskContains e with
        | Some rewritten -> rewritten
        | None -> e) expr

/// Module-level rewrite pass. Runs after lift, before codegen. Builds
/// and installs the CallablesTable so resolveCallable can resolve
/// kernel slots and predicate references to their underlying callable.
let rewriteMaskContains (modul: IRModule) : IRModule =
    let callables = buildCallablesTable modul.Functions
    let prev = setCallablesContext callables
    try
        let newBindings =
            modul.Bindings |> List.map (fun b ->
                { b with Value = rewriteMaskContainsExpr b.Value })
        let newFunctions =
            modul.Functions |> List.map (fun f ->
                { f with Body = rewriteMaskContainsExpr f.Body })
        { modul with Bindings = newBindings; Functions = newFunctions }
    finally
        restoreAnalysisContext prev

// ============================================================================
// ExprAttrs — bottom-up attribute computation for IR expressions
// ============================================================================
//
// Phase B of the LICM roadmap: a single bottom-up pass that computes
//   FreeVars  — IRIds referenced from outside this expression's binders
//   BoundVars — IRIds introduced inside (by IRLet, lambda params, etc.)
//   IsPure    — no observable side effects
// for any IRExpr. Each non-leaf arm unions children's attrs and adjusts
// for any binders introduced at that node. Leaves contribute trivially.
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
//   - The previous `collectVarRefsIR` has a `| _ -> Set.empty` catchall
//     that silently misses references in several variants (IRSlice,
//     IRCurry, IRStack, IRJoin, IRSubset, IRShift, IRTranspose, ...).
//     exprAttrs is exhaustive — every IRExpr variant has an explicit
//     arm. The corpus tests are how we know this matters.

// ----------------------------------------------------------------------------
// ContainsProbe — payload for the contains-aware-mask optimization
// ----------------------------------------------------------------------------
//
// The type, active pattern, and detailed doc-comment for ContainsProbe
// live earlier in this file (right after the IRExpr definition), because
// IRFuncDef carries a `Probes: ContainsProbe list` field and forward
// references aren't possible in F#. See lines ~410 for the definitions.

type ExprAttrs = {
    FreeVars:  Set<IRId>
    BoundVars: Set<IRId>
    IsPure:    bool
    Probes:    ContainsProbe list
}

let private emptyAttrs : ExprAttrs =
    { FreeVars = Set.empty; BoundVars = Set.empty; IsPure = true; Probes = [] }

let private mergeAttrs (a: ExprAttrs) (b: ExprAttrs) : ExprAttrs =
    { FreeVars  = Set.union a.FreeVars  b.FreeVars
      BoundVars = Set.union a.BoundVars b.BoundVars
      IsPure    = a.IsPure && b.IsPure
      // Concatenate probes left-to-right so the order matches source order
      // for predictable substitution-map population at codegen.
      Probes    = a.Probes @ b.Probes }

let private mergeMany (xs: ExprAttrs list) : ExprAttrs =
    List.fold mergeAttrs emptyAttrs xs

/// Pattern bindings: IRPatVar introduces an IRId visible in the case body
/// and (if present) the guard. Nested patterns (tuple, cons, variant
/// payload) accumulate all their child bindings.
let rec private patternBoundIds (pat: IRPattern) : Set<IRId> =
    match pat with
    | IRPatWild | IRPatLit _ -> Set.empty
    | IRPatVar id -> Set.singleton id
    | IRPatTuple pats -> pats |> List.map patternBoundIds |> Set.unionMany
    | IRPatCons (h, t) -> Set.union (patternBoundIds h) (patternBoundIds t)
    | IRPatVariant (_, _, Some p, _) -> patternBoundIds p
    | IRPatVariant (_, _, None, _) -> Set.empty

let rec exprAttrs (expr: IRExpr) : ExprAttrs =
    match expr with
    // -- Leaves: no children, no binders, pure --
    | IRLit _ | IRParam _ | IRNth | IRZero | IROpaqueExtent
    | IRRange _ | IRVirtualReverse _ | IRArity _ ->
        emptyAttrs

    // IRBlocked carries a block-size expression that may reference variables.
    | IRBlocked (_, blockSize) -> exprAttrs blockSize

    // -- Variable reference: contributes to FreeVars --
    | IRVar (id, _) ->
        { emptyAttrs with FreeVars = Set.singleton id }

    // -- Binders: variables introduced here are *not* free in this node --
    | IRLet (id, value, body) ->
        let va = exprAttrs value
        let ba = exprAttrs body
        // id is bound in body; remove from body's free vars before union.
        // Any reference to id inside `value` would be ill-formed IR (use
        // before binding); we still union without subtracting, so such a
        // bug would still show up as id ∈ FreeVars at the outer level.
        // Probes propagate from both halves: an IRContains in the value
        // is just as collectable as one in the body.
        { FreeVars  = Set.union va.FreeVars (Set.remove id ba.FreeVars)
          BoundVars = Set.unionMany [va.BoundVars; ba.BoundVars; Set.singleton id]
          IsPure    = va.IsPure && ba.IsPure
          Probes    = va.Probes @ ba.Probes }

    | IRForRange (vid, lo, hi, body) ->
        let la = exprAttrs lo
        let ha = exprAttrs hi
        let ba = exprAttrs body
        { FreeVars  = Set.unionMany [la.FreeVars; ha.FreeVars; Set.remove vid ba.FreeVars]
          BoundVars = Set.unionMany [la.BoundVars; ha.BoundVars; ba.BoundVars; Set.singleton vid]
          IsPure    = la.IsPure && ha.IsPure && ba.IsPure
          Probes    = la.Probes @ ha.Probes @ ba.Probes }

    | IRMatch (scrut, cases) ->
        let sa = exprAttrs scrut
        let caseAttrs =
            cases |> List.map (fun c ->
                let pIds = patternBoundIds c.Pattern
                let ga = c.Guard |> Option.map exprAttrs |> Option.defaultValue emptyAttrs
                let ba = exprAttrs c.Body
                // Pattern bindings are visible in guard and body. Probes
                // propagate from guard and body alike.
                { FreeVars  = Set.union (Set.difference ga.FreeVars pIds) (Set.difference ba.FreeVars pIds)
                  BoundVars = Set.unionMany [ga.BoundVars; ba.BoundVars; pIds]
                  IsPure    = ga.IsPure && ba.IsPure
                  Probes    = ga.Probes @ ba.Probes })
        mergeMany (sa :: caseAttrs)

    // -- Unary expressions: one child, no binders --
    | IRUnaryOp (_, e)
    | IRTupleProj (e, _, _)
    | IRTupleDecons e
    | IRFieldAccess (e, _)
    | IRRank e
    | IRReverse (e, _)
    | IRDiag e
    | IRPure e
    | IRCompute e
    | IRReynolds (e, _)
    | IRRaggedLookup e
    | IRTranspose (e, _) ->
        exprAttrs e

    // IRExtent's dim is a static int, so just the array.
    | IRExtent (a, _) -> exprAttrs a

    // -- Binary expressions: two children, no binders --
    | IRBinOp (_, _, l, r)
    | IRComplex (l, r)
    | IRTupleCons (l, r)
    | IRIntersect (l, r)
    | IRUnion (l, r)
    | IRGroupBy (l, r)
    | IRSort (l, r)
    | IRReduce (l, r)
    | IRFusion (l, r)
    | IRChoice (l, r)
    | IRBind (l, r)
    | IRArrayProduct (l, r)
    | IRComposeObj (l, r)
    | IRComposeMeth (l, r)
    | IRCompose (l, r)
    | IRFunctorMap (l, r)
    | IRCurry (l, r, _)
    | IRPolyIndex (l, r)
    | IRGuard (l, r)
    | IRAssign (l, r) ->
        mergeAttrs (exprAttrs l) (exprAttrs r)

    // IRContains is the probe site. Match directly (rather than via the
    // ContainsProbeAt active pattern) so F#'s exhaustiveness checker can
    // verify coverage — partial active patterns return `option` and the
    // checker doesn't treat them as definitely matching IRContains. The
    // active pattern stays exported for downstream consumers (codegen
    // substitution, future operator-decomposition extensions) where
    // exhaustiveness isn't being asserted.
    | IRContains (arr, value) as node ->
        let a = exprAttrs arr
        let v = exprAttrs value
        let combined = mergeAttrs a v
        let probe = { Node = node; BuildOn = arr }
        { combined with Probes = probe :: combined.Probes }

    // IRMask consumes the predicate's probes. The predicate is the only
    // place a mask can resolve them; once the mask has them, they do not
    // propagate further up the tree. The array argument's probes
    // (uncommon — an IRMask whose array operand itself contains a
    // contains call) still propagate.
    | IRMask (arr, pred) ->
        let arrAttrs = exprAttrs arr
        let predAttrs = exprAttrs pred
        let consumed = { predAttrs with Probes = [] }
        mergeAttrs arrAttrs consumed

    // IRMaskWithSet is the post-rewrite form: the contains-based optimization
    // is already extracted. The residual no longer contains IRContains nodes
    // for THIS mask's optimization, but may contain other contains-based
    // probes (nested masks, residual logic). Attribute computation walks
    // arr, setSource, and residual; the paramId is a binder for the residual.
    | IRMaskWithSet (arr, setSrc, paramId, residual) ->
        let arrAttrs = exprAttrs arr
        let setSrcAttrs = exprAttrs setSrc
        let residualAttrs = exprAttrs residual
        // paramId is bound by the mask; remove from FreeVars, add to BoundVars.
        let residual' = {
            residualAttrs with
                FreeVars = Set.remove paramId residualAttrs.FreeVars
                BoundVars = Set.add paramId residualAttrs.BoundVars
        }
        // The residual's probes (if any) are consumed by this mask, like IRMask.
        let consumed = { residual' with Probes = [] }
        mergeMany [arrAttrs; setSrcAttrs; consumed]

    // IRSetMember is a "leaf" lookup against a precomputed set. It references
    // paramId (which is bound by the enclosing IRMaskWithSet) and the value.
    | IRSetMember (paramId, value) ->
        let v = exprAttrs value
        // paramId is referenced (bound by the enclosing IRMaskWithSet, so
        // resolves correctly). Including it in FreeVars lets the enclosing
        // mask's BoundVars logic close over it.
        { v with FreeVars = Set.add paramId v.FreeVars }

    | IRUnique a -> exprAttrs a

    | IRParallel (a, b, _) -> mergeAttrs (exprAttrs a) (exprAttrs b)

    // -- Ternary and structured --
    | IRIf (c, t, e) ->
        mergeMany [exprAttrs c; exprAttrs t; exprAttrs e]

    | IRIndex (arr, idxs, _) ->
        mergeMany (exprAttrs arr :: List.map exprAttrs idxs)

    | IRSlice (a, _, start, stop) ->
        mergeMany [exprAttrs a; exprAttrs start; exprAttrs stop]

    | IRShift (a, _, offset, boundary) ->
        let ba =
            match boundary with
            | BndPad e -> exprAttrs e
            | BndShrink | BndPeriodic | BndReflect -> emptyAttrs
        mergeMany [exprAttrs a; exprAttrs offset; ba]

    | IRSubset (a, _, start, length) ->
        mergeMany [exprAttrs a; exprAttrs start; exprAttrs length]

    | IRReplicate (count, body) ->
        mergeAttrs (exprAttrs count) (exprAttrs body)

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

    | IRStructLit (_, fields) ->
        fields |> List.map (snd >> exprAttrs) |> mergeMany

    // -- List-valued --
    | IRTuple es | IRSequence es | IRZip es | IRStack es ->
        es |> List.map exprAttrs |> mergeMany

    | IRArrayLit (es, _) ->
        es |> List.map exprAttrs |> mergeMany

    | IRGroupKeys ks ->
        ks |> List.map exprAttrs |> mergeMany

    | IRJoin (arrs, _) ->
        arrs |> List.map exprAttrs |> mergeMany

    | IRAlign (arrs, spec) ->
        let bIfPad =
            match spec.Boundary with
            | BndPad e -> exprAttrs e
            | _ -> emptyAttrs
        mergeMany (bIfPad :: List.map exprAttrs arrs)

    // -- Loop/combinator nodes --
    | IRMethodFor info ->
        info.Arrays |> List.map exprAttrs |> mergeMany

    | IRObjectFor info ->
        exprAttrs info.Kernel

    | IRApplyCombinator info ->
        mergeMany (
            exprAttrs info.Loop
            :: exprAttrs info.Kernel
            :: List.map exprAttrs info.Arrays)


/// Validation error with context
type IRValidationError = {
    Message: string
    Context: string  // e.g. "in binding 'result'" or "in function 'covariance'"
}

/// Recursively collect all types from an IRExpr tree
let rec collectTypesInExpr (expr: IRExpr) : IRType list =
    let rec go e =
        match e with
        | IRVar (_, ty) -> [ty]
        | IRLit _ -> []
        | IRParam (_, _, ty) -> [ty]
        | IRBinOp (_, _, l, r) -> go l @ go r
        | IRUnaryOp (_, inner) -> go inner
        | IRIf (c, t, e) -> go c @ go t @ go e
        | IRLet (_, v, b) -> go v @ go b
        | IRApp (f, args, retTy) -> [retTy] @ go f @ (args |> List.collect go)
        | IRTuple elems -> elems |> List.collect go
        | IRComplex (re, im) -> go re @ go im
        | IRTupleProj (e, _, _) -> go e
        | IRArrayLit (elems, arrTy) -> [mkArrayLike arrTy] @ (elems |> List.collect go)
        | IRIndex (arr, idxs, _) -> go arr @ (idxs |> List.collect go)
        | IRFieldAccess (obj, _) -> go obj
        | IRStructLit (_, fields) -> fields |> List.collect (snd >> go)
        | IRMatch (scrut, cases) ->
            go scrut @ (cases |> List.collect (fun c ->
                go c.Body @ (c.Guard |> Option.map go |> Option.defaultValue [])))
        | IRCompute inner -> go inner
        | IRReynolds (inner, _) -> go inner
        | IRMethodFor info -> info.Arrays |> List.collect go
        | IRObjectFor info -> go info.Kernel
        | IRApplyCombinator info ->
            [info.OutputType] @ go info.Loop @ go info.Kernel @ (info.Arrays |> List.collect go)
        | IRParallel (a, b, _) -> go a @ go b
        | IRFusion (a, b) -> go a @ go b
        | IRChoice (a, b) -> go a @ go b
        | IRGuard (c, b) -> go c @ go b
        | IRSequence elems -> elems |> List.collect go
        | IRAssign (t, v) -> go t @ go v
        | IRForRange (_, lo, hi, body) -> go lo @ go hi @ go body
        | IRBind (comp, cont) -> go comp @ go cont
        | IRFunctorMap (f, c) -> go f @ go c
        | IRPure e -> go e
        | _ -> []
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
    
    // --- Check 1: No unresolved IRTInfer in binding types ---
    for b in modul.Bindings do
        let ctx = sprintf "in binding '%s'" b.Name
        match containsInfer b.Type with
        | Some id -> addError ctx (sprintf "unresolved type variable T?%d in declared type" id)
        | None -> ()
        // Also check types inside the expression tree
        for ty in collectTypesInExpr b.Value do
            match containsInfer ty with
            | Some id -> addError ctx (sprintf "unresolved type variable T?%d in expression" id)
            | None -> ()
    
    // --- Check 1b: No unresolved IRTInfer in function types ---
    for f in modul.Functions do
        let ctx = sprintf "in function '%s'" f.Name
        match containsInfer f.RetType with
        | Some id -> addError ctx (sprintf "unresolved type variable T?%d in return type" id)
        | None -> ()
        for p in f.Params do
            match containsInfer p.Type with
            | Some id -> addError ctx (sprintf "unresolved type variable T?%d in param '%s'" id p.Name)
            | None -> ()
        for ty in collectTypesInExpr f.Body do
            match containsInfer ty with
            | Some id -> addError ctx (sprintf "unresolved type variable T?%d in body" id)
            | None -> ()
    
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
        | IRReduce (a, k) -> checkScope scope ctx a; checkScope scope ctx k
        | IRApplyCombinator info ->
            checkScope scope ctx info.Loop
            checkScope scope ctx info.Kernel
            info.Arrays |> List.iter (checkScope scope ctx)
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
            // Check kernel param count vs input ranks.
            // Stage 3c.4c: kernel slot is always IRVar (or
            // IRReynolds(IRVar)); resolve through resolveCallable to
            // get param count and comm groups.
            let kernelInner =
                match info.Kernel with
                | IRReynolds (inner, _) -> inner
                | other -> other
            match resolveCallable kernelInner with
            | Some lInfo ->
                if lInfo.Params.Length <> info.KernelInputRanks.Length then
                    addError ctx (sprintf "ApplyInfo: kernel params=%d != KernelInputRanks.Length=%d" lInfo.Params.Length info.KernelInputRanks.Length)
                // Verify CommGroup indices are in range
                for cg in lInfo.CommGroups do
                    for idx in cg do
                        if idx < 0 || idx >= lInfo.Params.Length then
                            addError ctx (sprintf "CommGroup index %d out of range [0, %d)" idx lInfo.Params.Length)
            | None -> ()
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
