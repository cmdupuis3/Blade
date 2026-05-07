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
    | ETIndexRef of string  // Foreign key: integer tagged with an index type name

/// Compute the promoted element type for two numeric types per §3.4.2.
/// Returns None if the types are incompatible for promotion.
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
        // Index refs promote with integers, preserving the tag
        | ETIndexRef name, ETInt64 | ETInt64, ETIndexRef name -> Some (ETIndexRef name)
        | ETIndexRef name, ETInt32 | ETInt32, ETIndexRef name -> Some (ETIndexRef name)
        | _ -> None

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

/// Index type in IR - represents a single dimension's structure
type IRIndexType = {
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

/// IR Types
and IRType =
    | IRTScalar of ElemType
    | IRTArray of IRArrayType
    | IRTTuple of IRType list
    | IRTFunc of args: IRType list * ret: IRType
    | IRTLoop of LoopType
    | IRTComputation of IRType   // Suspended computation producing this type
    | IRTUnit
    | IRTPoly of baseType: IRType * arityVar: string  // Arity-polymorphic type
    | IRTNat of int option  // Type-level natural number (None = variable)
    | IRTNamed of string    // Named type (struct, sum type, etc.)
    | IRTInfer of int       // Unresolved type variable (id for unification)
    | IRTUnitAnnotated of IRType * UnitSig  // Type with unit-of-measure annotation
    | IRTGroupKeys of outerIdx: IRIndexType * sourceIdx: IRIndexType * enumValues: int64 list option
      // GroupKeys: CSR structure mapping sourceIdx → groups indexed by outerIdx.
      // enumValues: if keys are EnumIdx, carries the actual key values for reverse lookup.

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
    | IRLambda of LambdaInfo
    | IRTuple of IRExpr list
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
    | IRIntersect of IRExpr * IRExpr          // intersect(A, B) - elements in both
    | IRUnion of IRExpr * IRExpr              // union(A, B) - elements in either
    | IRGroupBy of values: IRExpr * grouping: IRExpr  // group_by(vals, gk) - apply grouping
    | IRGroupKeys of keys: IRExpr                    // group_keys(keys) - build CSR structure
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

/// Lambda with proper capture tracking
and LambdaInfo = {
    Params: IRParam list
    Body: IRExpr
    Captures: CaptureInfo list
    IsCommutative: bool
    CommGroups: int list list
}

and CaptureInfo = {
    Id: IRId
    Name: string
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

/// Primitive element, optionally unit-annotated. Workhorse for read sites
/// that just want the primitive and don't care whether units are attached.
let (|AnyPrimElem|_|) (ty: IRType) =
    match ty with
    | IRTScalar et -> Some et
    | IRTUnitAnnotated (IRTScalar et, _) -> Some et
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
/// natural. Codegen support for these is future work.
let (|FuncElem|_|) (ty: IRType) =
    match ty with
    | IRTFunc (args, ret) -> Some (args, ret)
    | _ -> None

/// Array-valued elem type. Nested arrays. Currently the parser accepts
/// `Array<Array<T like Idx<N>> like Idx<M>>` but `lowerElemType` silently
/// demotes the inner array to ETFloat64. After Phase B2, this pattern
/// fires correctly and codegen needs to handle nested-array elem types
/// (the C++ promote<T,k> template handles this transparently per design).
let (|ArrayElem|_|) (ty: IRType) =
    match ty with
    | IRTArray arr -> Some arr
    | _ -> None

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

/// Function definition in IR
type IRFuncDef = {
    Id: IRId
    Name: string
    Params: IRParam list
    RetType: IRType
    Body: IRExpr
    IsStatic: bool
    Commutativity: int list list option
    Parallelism: (int * int) list
    IsArityPoly: bool
    ArityParam: string option
}

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
    | IRTDEnumIdx of name: string * idx: IRIndexType * values: int64 list
      // Named index type declaration, e.g. "type lat = Idx<180>"
      // Provides nominal identity: two arrays sharing module.lat
      // have the same index space.  Future: schemas can supply these
      // so that multiple files share the same nominal types.

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
            IRTArray { 
                ElemType = elemType
                IndexTypes = allDims
                IsVirtual = false
                Identity = None 
            }


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
    | IRTArray arr ->
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
    
    // Extract kernel info
    let (kernelParams, kernelBody, commGroups, captures, isAntisymmetric) =
        match info.Kernel with
        | IRLambda lInfo -> (lInfo.Params, lInfo.Body, lInfo.CommGroups, lInfo.Captures, false)
        | IRReynolds (IRLambda lInfo, isAnti) -> (lInfo.Params, lInfo.Body, lInfo.CommGroups, lInfo.Captures, isAnti)
        | _ -> ([], IRLit IRLitUnit, [], [], false)
    
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
        | IRIntersect (a, b) -> IRIntersect (m a, m b)
        | IRUnion (a, b) -> IRUnion (m a, m b)
        | IRGroupBy (v, k) -> IRGroupBy (m v, m k)
        | IRGroupKeys k -> IRGroupKeys (m k)
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
        | IRLambda info ->
            IRLambda { info with Body = m info.Body }
        | IRMethodFor info ->
            IRMethodFor { info with Arrays = ms info.Arrays }
        | IRObjectFor info ->
            IRObjectFor { info with Kernel = m info.Kernel }
        | IRApplyCombinator info ->
            IRApplyCombinator { info with Loop = m info.Loop; Kernel = m info.Kernel; Arrays = ms info.Arrays }
    f mapped

// ============================================================================
// Arity Monomorphization
// ============================================================================

/// Collect all call sites to arity-polymorphic functions.
/// Returns list of (funcId, concreteArity) pairs.
let collectPolyCallSites (polyFuncIds: Set<IRId>) (expr: IRExpr) : (IRId * int) list =
    let results = System.Collections.Generic.List<_>()
    let walk e =
        match e with
        | IRApp (IRVar (funcId, _), args, _) when polyFuncIds.Contains funcId ->
            results.Add((funcId, args.Length))
        | _ -> ()
        e  // don't transform, just inspect
    mapIRExpr walk expr |> ignore
    results |> Seq.toList

/// Create a monomorphized copy of a poly function for a specific arity.
/// Expands the Poly<T> param into N individual params and rewrites body.
let specializeFunction (func: IRFuncDef) (arity: int) (builder: IRBuilder) : IRFuncDef =
    // Find the Poly param
    let polyParamIdx =
        func.Params |> List.tryFindIndex (fun p ->
            match p.Type with IRTPoly _ -> true | _ -> false)
    match polyParamIdx with
    | None -> func  // Not actually poly — shouldn't happen
    | Some pidx ->
        let polyParam = func.Params.[pidx]
        let baseType =
            match polyParam.Type with
            | IRTPoly (bt, _) -> bt
            | _ -> IRTScalar ETFloat64

        // Generate N new params to replace the single Poly param
        let newParams =
            List.init arity (fun i ->
                { Name = sprintf "%s_%d" polyParam.Name i
                  Type = baseType
                  Index = pidx + i
                  VarId = builder.FreshId() } : IRParam)

        // Build full expanded param list (non-poly params keep their positions)
        let before = func.Params |> List.take pidx
        let after = func.Params |> List.skip (pidx + 1)
        // Re-index the trailing params
        let afterReindexed =
            after |> List.mapi (fun i p -> { p with Index = pidx + arity + i })
        let expandedParams = before @ newParams @ afterReindexed

        // Collect all VarIds that are transitive let-aliases of the poly param.
        // Walk top-down so that aliases-of-aliases are discovered before their uses.
        let collectLetAliases (rootId: IRId) (expr: IRExpr) : Set<IRId> =
            let mutable aliases = Set.singleton rootId
            let rec walk expr =
                match expr with
                | IRLet (id, IRVar (srcId, _), body) ->
                    if Set.contains srcId aliases then
                        aliases <- Set.add id aliases
                    walk body
                | IRLet (_, value, body) -> walk value; walk body
                | IRLambda info -> walk info.Body
                | IRIf (c, t, e) -> walk c; walk t; walk e
                | IRMatch (s, cases) -> walk s; cases |> List.iter (fun c -> walk c.Body)
                | IRBinOp (_, _, l, r) -> walk l; walk r
                | IRForRange (_, lo, hi, body) -> walk lo; walk hi; walk body
                | _ -> ()
            walk expr
            aliases

        let polyAliases = collectLetAliases polyParam.VarId func.Body

        // Rewrite body: replace IRPolyIndex and IRArity
        let rewrite e =
            match e with
            | IRPolyIndex (IRVar (id, _), IRLit (IRLitInt k)) when Set.contains id polyAliases ->
                let idx = int k
                if idx >= 0 && idx < arity then
                    IRVar (newParams.[idx].VarId, baseType)
                else e  // out of range — leave as-is (will error at C++)
            | IRPolyIndex (IRVar (id, _), _) when Set.contains id polyAliases ->
                // Dynamic index — can't monomorphize, leave as-is
                // (future: generate switch statement)
                e
            | IRArity (None, name) when name = polyParam.Name ->
                IRLit (IRLitInt (int64 arity))
            | IRArity (_, name) when name = polyParam.Name ->
                IRLit (IRLitInt (int64 arity))
            | _ -> e
        let newBody = mapIRExpr rewrite func.Body

        // Second pass: unroll IRForRange with literal bounds
        // This handles `for k in 0..arity(args)` after arity is resolved
        let rec unrollForRanges expr =
            match expr with
            | IRLet (id, IRForRange (vid, IRLit (IRLitInt lo), IRLit (IRLitInt hi), body), rest) ->
                // Unroll: replace the for-range with N copies of body
                let restUnrolled = unrollForRanges rest
                let indices = [ int lo .. int hi - 1 ] |> List.rev
                indices |> List.fold (fun acc k ->
                    // Substitute loop variable with literal k in body
                    let substBody =
                        mapIRExpr (fun e ->
                            match e with
                            | IRVar (varId, _) when varId = vid -> IRLit (IRLitInt (int64 k))
                            | _ -> e) body
                    // Re-run the poly index rewrite on the substituted body
                    let substBody2 = mapIRExpr rewrite substBody
                    let dummyId = builder.FreshId()
                    IRLet (dummyId, unrollForRanges substBody2, acc)
                ) restUnrolled
            | IRLet (id, v, b) -> IRLet (id, unrollForRanges v, unrollForRanges b)
            | _ -> expr
        let newBody = unrollForRanges newBody

        // Resolve return type
        let newRetType =
            match func.RetType with
            | IRTPoly (bt, _) -> bt
            | other -> other

        // Expand commutativity groups if present
        // If the poly param was in a comm group, expand it to cover all new params
        let newComm =
            func.Commutativity |> Option.map (fun groups ->
                groups |> List.map (fun group ->
                    group |> List.collect (fun idx ->
                        if idx = pidx then List.init arity (fun i -> pidx + i)
                        else [if idx > pidx then idx + arity - 1 else idx])))

        { Id = builder.FreshId()
          Name = sprintf "%s_arity%d" func.Name arity
          Params = expandedParams
          RetType = newRetType
          Body = newBody
          IsStatic = func.IsStatic
          Commutativity = newComm
          Parallelism = func.Parallelism  // May need expansion too
          IsArityPoly = false
          ArityParam = None }

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
/// resolution. Built once at the entry to liftInlineFormsModule and threaded
/// through. A mutable cell because liftExpr is recursive over many cases and
/// passing through every signature would be noisy; the map is set at module-
/// entry and read-only thereafter.
let private structFieldsCache : System.Collections.Generic.Dictionary<string, (string * IRType) list> =
    System.Collections.Generic.Dictionary<string, (string * IRType) list>()

let setStructFieldsCache (types: IRTypeDef list) =
    structFieldsCache.Clear()
    for td in types do
        match td with
        | IRTDStruct (name, fields, _) -> structFieldsCache.[name] <- fields
        | _ -> ()

let tryLookupFieldType (objType: IRType) (fieldName: string) : IRType option =
    match objType with
    | IRTNamed structName ->
        match structFieldsCache.TryGetValue(structName) with
        | true, fields ->
            fields |> List.tryFind (fun (n, _) -> n = fieldName) |> Option.map snd
        | false, _ -> None
    | _ -> None

let rec liftInferType (expr: IRExpr) : IRType =
    match expr with
    | IRVar (_, ty) -> ty
    | IRParam (_, _, ty) -> ty
    | IRApp (_, _, retTy) -> retTy
    | IRArrayLit (_, arrTy) -> IRTArray arrTy
    | IRMask (arr, _) -> liftInferType arr
    | IRSort (arr, _) -> liftInferType arr
    | IRIntersect (a, _) -> liftInferType a
    | IRUnion (a, _) -> liftInferType a
    | IRGroupBy (v, _) -> liftInferType v  // Approximation; codegen recomputes
    | IRGroupKeys _ -> IRTUnit  // GroupKeys is opaque
    | IRLet (_, _, body) -> liftInferType body
    | IRIndex (arr, idxs, _) ->
        match liftInferType arr with
        | IRTArray a when idxs.Length >= a.IndexTypes.Length -> a.ElemType
        | IRTArray a -> IRTArray { a with IndexTypes = a.IndexTypes |> List.skip idxs.Length }
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
    | IRMask _ | IRSort _ | IRIntersect _ | IRUnion _
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
        | IRTArray _ -> true
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
        let ty = IRTArray arrTy
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
    | IRMask (a, p) -> IRMask (liftExpr builder a, liftExpr builder p)
    | IRSort (a, k) -> IRSort (liftExpr builder a, liftExpr builder k)
    | IRIntersect (a, b) -> IRIntersect (liftExpr builder a, liftExpr builder b)
    | IRUnion (a, b) -> IRUnion (liftExpr builder a, liftExpr builder b)
    | IRGroupBy (v, k) -> IRGroupBy (liftExpr builder v, liftExpr builder k)
    | IRGroupKeys k -> IRGroupKeys (liftExpr builder k)

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

    // Lambdas: descend into body
    | IRLambda info ->
        IRLambda { info with Body = liftExpr builder info.Body }

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
        modul.Functions |> List.collect (fun f -> collectPolyCallSites polyFuncIds f.Body)
    let callSitesFromBindings =
        modul.Bindings |> List.collect (fun b -> collectPolyCallSites polyFuncIds b.Value)
    let uniqueCallSites =
        (callSitesFromFuncs @ callSitesFromBindings) |> List.distinct

    // 3. Generate specialized functions
    let specializations =
        uniqueCallSites |> List.map (fun (funcId, arity) ->
            let origFunc = polyFuncMap.[funcId]
            let spec = specializeFunction origFunc arity builder
            ((funcId, arity), spec))
    let specMap = specializations |> Map.ofList

    // 4. Build rewrite function for call sites
    let rewriteCallSite e =
        match e with
        | IRApp (IRVar (funcId, fty), args, _) when polyFuncIds.Contains funcId ->
            let arity = args.Length
            match Map.tryFind (funcId, arity) specMap with
            | Some spec -> IRApp (IRVar (spec.Id, fty), args, spec.RetType)
            | None -> e  // No specialization found — leave as-is
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
    | IRTScalar (ETIndexRef name) -> name
    | IRTArray arr ->
        let indices = arr.IndexTypes |> List.map ppIndexType |> String.concat ", "
        sprintf "Array<%s like %s>" (ppIRType arr.ElemType) indices
    | IRTTuple ts ->
        sprintf "(%s)" (ts |> List.map ppIRType |> String.concat ", ")
    | IRTFunc (args, ret) ->
        sprintf "(%s) -> %s" (args |> List.map ppIRType |> String.concat ", ") (ppIRType ret)
    | IRTLoop lt ->
        match lt.Kind with
        | LKMethod -> sprintf "MethodLoop<%d>" (lt.Arity |> Option.defaultValue 0)
        | LKObject -> sprintf "ObjectLoop<%d>" (lt.Arity |> Option.defaultValue 0)
    | IRTComputation t -> sprintf "Computation<%s>" (ppIRType t)
    | IRTUnit -> "Void"
    | IRTPoly (base', var) -> sprintf "Poly<%s, %s>" (ppIRType base') var
    | IRTNat (Some n) -> sprintf "Nat<%d>" n
    | IRTNat None -> "Nat<?>"
    | IRTNamed name -> name  // Named types print as themselves
    | IRTInfer id -> sprintf "T?%d" id
    | IRTUnitAnnotated (inner, units) -> sprintf "%s<%s>" (ppIRType inner) (ppUnitSig units)
    | IRTGroupKeys (outerIdx, sourceIdx, _) -> sprintf "GroupKeys<%s, %s>" (ppIndexType outerIdx) (ppIndexType sourceIdx)

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
    | IRTArray arr ->
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
    | IRLambda info ->
        let ps = info.Params |> List.map (fun p -> sprintf "%s:%s" p.Name (ppIRType p.Type)) |> String.concat ", "
        let commStr = if info.IsCommutative then " [comm]" else ""
        // Build name mapping from parameter VarIds to names
        let names' = info.Params |> List.fold (fun m p -> Map.add p.VarId p.Name m) names
        sprintf "lambda(%s)%s -> %s" ps commStr (ppIRExprWithNames names' 0 info.Body)
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
    | IRLambda info ->
        let paramIds = info.Params |> List.map (fun p -> p.VarId) |> Set.ofList
        Set.difference (collectVarRefsIR info.Body) paramIds
    | IRApp (f, args, _) -> Set.unionMany (collectVarRefsIR f :: List.map collectVarRefsIR args)
    | IRTuple es -> Set.unionMany (List.map collectVarRefsIR es)
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
    | IRIntersect (a, b) -> Set.union (collectVarRefsIR a) (collectVarRefsIR b)
    | IRUnion (a, b) -> Set.union (collectVarRefsIR a) (collectVarRefsIR b)
    | IRGroupBy (v, k) -> Set.union (collectVarRefsIR v) (collectVarRefsIR k)
    | IRGroupKeys k -> collectVarRefsIR k
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
        | IRLambda info ->
            (info.Params |> List.map (fun p -> p.Type)) @ go info.Body
        | IRTuple elems -> elems |> List.collect go
        | IRTupleProj (e, _, _) -> go e
        | IRArrayLit (elems, arrTy) -> [IRTArray arrTy] @ (elems |> List.collect go)
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
    | IRTArray arr ->
        // Phase B2: ElemType is IRType, so recurse directly. Also check
        // index extents — they're IRExpr not IRType so a separate walk
        // would be needed; for now we check elem type only (consistent
        // with prior behavior, which never actually walked index types
        // due to the bug fixed in S1).
        containsInfer arr.ElemType
    | IRTTuple ts -> ts |> List.tryPick containsInfer
    | IRTFunc (args, ret) -> (args @ [ret]) |> List.tryPick containsInfer
    | IRTComputation inner -> containsInfer inner
    | IRTUnitAnnotated (inner, _) -> containsInfer inner
    | IRTPoly (inner, _) -> containsInfer inner
    | _ -> None

/// Collect all VarIds defined (brought into scope) by an expression
let rec collectDefinedIds (expr: IRExpr) : Set<IRId> =
    match expr with
    | IRLet (id, value, body) -> Set.add id (Set.union (collectDefinedIds value) (collectDefinedIds body))
    | IRLambda info ->
        let paramIds = info.Params |> List.map (fun p -> p.VarId) |> Set.ofList
        Set.union paramIds (collectDefinedIds info.Body)
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
let validateModule (modul: IRModule) : IRValidationError list =
    let errors = ResizeArray<IRValidationError>()
    let addError ctx msg = errors.Add({ Message = msg; Context = ctx })
    
    // Track all defined IDs (bindings + functions define names in scope)
    let moduleIds =
        let bindIds = modul.Bindings |> List.map (fun b -> b.Id) |> Set.ofList
        let funcIds = modul.Functions |> List.map (fun f -> f.Id) |> Set.ofList
        Set.union bindIds funcIds
    
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
        | IRLambda info ->
            let paramIds = info.Params |> List.map (fun p -> p.VarId) |> Set.ofList
            checkScope (Set.union scope paramIds) ctx info.Body
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
        let funcScope = Set.union moduleIds paramIds
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
            // Check kernel param count vs input ranks
            match info.Kernel with
            | IRLambda lInfo | IRReynolds (IRLambda lInfo, _) ->
                if lInfo.Params.Length <> info.KernelInputRanks.Length then
                    addError ctx (sprintf "ApplyInfo: kernel params=%d != KernelInputRanks.Length=%d" lInfo.Params.Length info.KernelInputRanks.Length)
                // Verify CommGroup indices are in range
                for cg in lInfo.CommGroups do
                    for idx in cg do
                        if idx < 0 || idx >= lInfo.Params.Length then
                            addError ctx (sprintf "CommGroup index %d out of range [0, %d)" idx lInfo.Params.Length)
            | _ -> ()
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
    
    errors |> Seq.toList

/// Validate an entire IR program
let validateIR (program: IRProgram) : Result<IRProgram, string list> =
    let allErrors =
        program.Modules |> List.collect validateModule
    if allErrors.IsEmpty then
        Ok program
    else
        let messages = allErrors |> List.map (fun e -> sprintf "[IR Validation] %s: %s" e.Context e.Message)
        Error messages
