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

/// Array type in IR with identity tracking
and IRArrayType = {
    ElemType: ElemType
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

/// A single level of a loop nest
type LoopLevel = {
    Index: IRId
    Extent: IRExpr
    LowerBound: IRExpr
    State: SymcomState
    Parallel: bool
    BoundDependencies: IRId list
}

/// Complete loop nest specification
type LoopNest = {
    Levels: LoopLevel list
    Kernel: IRExpr
    OutputType: IRType
    Commutativity: int list list
    SpeedupFactor: int64
}

/// Computation graph node
type CompNode =
    | CNLeaf of LoopNest
    | CNParallel of CompNode * CompNode * fusionDepth: int
    | CNBind of CompNode * continuation: IRExpr
    | CNPure of IRExpr
    | CNChoice of CompNode * CompNode
    | CNGuard of cond: IRExpr * body: CompNode
    | CNFused of LoopNest * kernels: IRExpr list
    | CNSequence of CompNode list

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

/// Per-loop-level symmetry info for partial product symmetry
type LoopLevelSymmetry = {
    LevelIndex: int
    ArrayIndex: int
    SharedWithLevels: int list
    CanTriangulate: bool
    TriangularArity: int
}

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
    (elemType: ElemType)
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
        
        // Step 4: T-dims from kernel output (passed in with real extents)
        let outputTDims = 
            kernelTDims |> List.map (fun idx ->
                { idx with Kind = TDimension; Id = builder.FreshId() })
        
        // Combine S-dims and T-dims
        let allDims = outputSDims @ outputTDims
        
        if allDims.IsEmpty then
            IRTScalar elemType  // Scalar output
        else
            IRTArray { 
                ElemType = elemType
                IndexTypes = allDims
                IsVirtual = false
                Identity = None 
            }

/// Build a loop nest from ApplyInfo
let buildLoopNest (info: ApplyInfo) (builder: IRBuilder) : LoopNest =
    let arrayTypes = info.ArrayTypes
    let identities = info.Identities
    
    let commGroups = 
        match info.Kernel with
        | IRLambda lInfo -> lInfo.CommGroups
        | _ -> []
    
    let sDimsPerArray = info.SDimsPerArray
    let loopLevels = buildLoopLevelStructure arrayTypes sDimsPerArray
    let triangularLevels = computeTriangularLevels arrayTypes identities commGroups sDimsPerArray
    let speedup = computePartialProductSpeedup arrayTypes identities commGroups sDimsPerArray
    
    // Deduce output type - infer element type and T-dims from kernel/arrays
    let (elemType, kernelTDims) =
        match info.Kernel with
        | IRLambda lInfo ->
            let et =
                lInfo.Params |> List.tryPick (fun p ->
                    match p.Type with IRTScalar et -> Some et | IRTArray arr -> Some arr.ElemType | _ -> None)
                |> Option.defaultValue ETFloat64
            // T-dims come from the kernel's return type if it's an array
            let tDims =
                match info.KernelTDims with
                | _ :: _ -> info.KernelTDims  // Already computed (from TypeCheck)
                | [] -> []
            (et, tDims)
        | _ ->
            let et =
                arrayTypes |> List.tryPick (fun at -> Some at.ElemType)
                |> Option.defaultValue ETFloat64
            (et, info.KernelTDims)
    let outputType = deduceOutputType arrayTypes identities commGroups sDimsPerArray kernelTDims elemType builder
    
    // Compute symcom states for group-scoped dependency tracking
    let symcomStates = computeAllSymcomStates identities arrayTypes commGroups sDimsPerArray

    // iminMap: each level points to its immediate dependency within the same
    // triangular group, or to itself if it starts a new group (no dependency).
    let iminMap =
        loopLevels |> List.mapi (fun globalIdx _level ->
            let state = if globalIdx < symcomStates.Length then symcomStates.[globalIdx] else SCNeither
            match state with
            | SCNeither -> globalIdx
            | SCSymmetric | SCCommutative | SCBoth -> globalIdx - 1)

    // dependencyPath: chase the imin chain to collect all levels this one depends on.
    let rec dependencyPath (level: int) : int list =
        if level < 0 || level >= iminMap.Length then []
        elif iminMap.[level] = level then []
        else iminMap.[level] :: dependencyPath iminMap.[level]

    let boundDependencies = loopLevels |> List.mapi (fun i _ -> dependencyPath i)

    // Assign fresh IRIds to each level, then map level indices to IRIds for BoundDependencies
    let levelIds = loopLevels |> List.map (fun _ -> builder.FreshId())

    let mutable levels = []

    for levelInfo in loopLevels do
        let i = levelInfo.GlobalLevelIndex
        let canTriangulate = if i < triangularLevels.Length then triangularLevels.[i] else false
        let idx = levelIds.[i]
        let extent = levelInfo.IndexSpace.Extent
        let state = if canTriangulate then SCCommutative else SCNeither
        let deps = boundDependencies.[i] |> List.map (fun li -> levelIds.[li])
        let lowerBound = computeTriangularBound i deps extent state
        
        let level = {
            Index = idx
            Extent = extent
            LowerBound = lowerBound
            State = state
            Parallel = i = 0
            BoundDependencies = deps
        }
        
        levels <- levels @ [level]
    
    {
        Levels = levels
        Kernel = info.Kernel
        OutputType = outputType
        Commutativity = commGroups
        SpeedupFactor = speedup
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
    /// Element type of the array (for explicit typing)
    ArrayElemType: ElemType
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
        let elemType = arrType |> Option.map (fun t -> t.ElemType) |> Option.defaultValue ETFloat64
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
        | IRRange _ | IRVirtualReverse _ | IRArity _ -> expr
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
        sprintf "Array<%s like %s>" (ppElemType arr.ElemType) indices
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

and ppElemType = function
    | ETInt32 -> "Int32"
    | ETInt64 -> "Int64"
    | ETFloat32 -> "Float32"
    | ETFloat64 -> "Float64"
    | ETComplex64 -> "Complex64"
    | ETComplex128 -> "Complex128"
    | ETBool -> "Bool"
    | ETUnit -> "Void"
    | ETString -> "String"
    | ETIndexRef name -> name

/// Build a map from IRIndexType.Id -> type name from a module's IRTDIndexType defs
let indexNameMap (modul: IRModule) : Map<IRId, string> =
    modul.Types
    |> List.choose (function
        | IRTDIndexType (name, idx) -> Some (idx.Id, name)
        | _ -> None)
    |> Map.ofList

/// Context-aware pretty-printers that resolve named index types
let rec ppIRTypeIn (names: Map<IRId, string>) = function
    | IRTArray arr ->
        let indices = arr.IndexTypes |> List.map (ppIndexTypeIn names) |> String.concat ", "
        sprintf "Array<%s, %s>" (ppElemType arr.ElemType) indices
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
let rec collectVarRefsIR (expr: IRExpr) : Set<IRId> =
    match expr with
    | IRVar (id, _) -> Set.singleton id
    | IRLit _ | IRParam _ | IRNth | IRZero -> Set.empty
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
        arr.IndexTypes |> List.tryPick (fun _ -> None)
        |> Option.orElse (containsInfer (IRTScalar arr.ElemType) |> Option.bind (fun _ -> None))
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
