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
    | IRTInfer of int       // Unresolved type variable (id for unification)

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
    | IRVar of IRId
    | IRParam of name: string * idx: int
    | IRBinOp of IRBinOpMode * IRBinOp * IRExpr * IRExpr
    | IRUnaryOp of IRUnaryOp * IRExpr
    | IRArrayLit of IRExpr list * IRArrayType
    | IRIndex of array: IRExpr * index: IRExpr list * identity: ArrayIdentity option
    | IRSlice of array: IRExpr * dim: int * start: IRExpr * stop: IRExpr
    | IRCurry of array: IRExpr * index: IRExpr * resultRank: int
    | IRApp of func: IRExpr * args: IRExpr list
    | IRLambda of LambdaInfo
    | IRTuple of IRExpr list
    | IRTupleProj of IRExpr * int
    | IRTupleCons of head: IRExpr * tail: IRExpr
    | IRTupleDecons of tuple: IRExpr
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
    | IRZip of IRExpr list
    | IRAlign of arrays: IRExpr list * spec: AlignSpec
    | IRStack of IRExpr list
    | IRTranspose of array: IRExpr * perm: int list
    | IRReverse of array: IRExpr * dim: int
    | IRShift of array: IRExpr * dim: int * offset: IRExpr * boundary: BoundaryMode
    | IRDiag of array: IRExpr
    | IRJoin of arrays: IRExpr list * dim: int
    | IRSubset of array: IRExpr * dim: int * start: IRExpr * length: IRExpr
    | IRRange of IRIndexType
    | IRVirtualReverse of IRIndexType
    | IRBlocked of IRIndexType * blockSize: IRExpr
    | IRArity of int option  // None = unresolved, Some n = bound to n at call site
    | IRNth
    | IRZero
    | IRRank of array: IRExpr
    | IRPolyIndex of pack: IRExpr * index: IRExpr  // Dynamic poly-pack indexing: args[k]
    | IRExtent of array: IRExpr * dim: int
    | IRAssign of target: IRId * value: IRExpr

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
    Loop: IRExpr
    Kernel: IRExpr
    SymcomStates: SymcomState list
    TriangularLevels: bool list
    SDimsPerArray: int list
    KernelInputRanks: int list
    KernelOutputRank: int
    SpeedupFactor: int64
    ReynoldsSpeedup: int64        // Additional speedup from Reynolds (n!× for triangular)
    HasReynolds: bool              // Whether kernel has Reynolds annotation
    OutputType: IRType             // Deduced output array type
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
    | IRPatVariant of tag: int * IRPattern option

and BoundaryMode =
    | BndShrink
    | BndPad of IRExpr
    | BndPeriodic
    | BndReflect

and AlignSpec = {
    Offsets: (int * int) list
    Boundary: BoundaryMode
}

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

/// Extract index space info from an array type
let extractIndexSpaces (arrType: IRArrayType) : IndexSpaceInfo list =
    arrType.IndexTypes |> List.map (fun idx ->
        { Tag = idx.Tag; Extent = idx.Extent; Symmetry = idx.Symmetry; 
          Kind = idx.Kind; SourceArity = idx.Arity })

/// Check if two index spaces are "shared" (same logical index space)
let indexSpacesMatch (a: IndexSpaceInfo) (b: IndexSpaceInfo) : bool =
    if a.Kind <> SDimension || b.Kind <> SDimension then false
    else
        match a.Tag, b.Tag with
        | Some tagA, Some tagB -> tagA = tagB
        | None, None ->
            match a.Extent, b.Extent with
            | IRVar idA, IRVar idB -> idA = idB
            | IRParam (nA, _), IRParam (nB, _) -> nA = nB
            | IRLit (IRLitInt nA), IRLit (IRLitInt nB) -> nA = nB
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
/// irank(f, i) = rank of the i-th parameter type
let extractKernelInputRanks (kernel: IRExpr) : int list =
    match kernel with
    | IRLambda info ->
        info.Params |> List.map (fun p ->
            match p.Type with
            | IRTArray arr -> arr.IndexTypes.Length
            | IRTScalar _ -> 0
            | IRTUnit -> 0
            | _ -> 0)
    | _ -> []

/// Extract output rank from a lambda's return type or infer from body
/// orank(f) = rank of kernel's output (T-dimensions)
let extractKernelOutputRank (kernel: IRExpr) : int =
    match kernel with
    | IRLambda info ->
        // Try to infer from body expression
        let rec inferRank expr =
            match expr with
            | IRArrayLit (_, arrTy) -> arrTy.IndexTypes.Length
            | IRIndex (_, _, _) -> 0  // Scalar indexing result
            | IRBinOp (mode, _, _, _) ->
                match mode with
                | IROuter -> 1  // Outer product adds dimension (simplified)
                | IRElementwise -> 0  // Elementwise ops preserve rank 0
            | IRLet (_, _, body) -> inferRank body
            | IRIf (_, thenBr, _) -> inferRank thenBr
            | _ -> 0
        inferRank info.Body
    | _ -> 0

/// Compute S-dimensions per array given array ranks and kernel input ranks
/// SDims[i] = rank(array[i]) - irank(kernel, i)
let computeSDimsWithKernel (arrayTypes: IRArrayType list) (kernelInputRanks: int list) : int list =
    if kernelInputRanks.IsEmpty then
        // No kernel info - assume scalar kernel (irank = 0 for all)
        computeSDimsPerArray arrayTypes
    else
        (arrayTypes, kernelInputRanks) ||> List.map2 (fun arr irank ->
            let arrayRank = arr.IndexTypes 
                            |> List.filter (fun idx -> idx.Kind = SDimension) 
                            |> List.sumBy (fun idx -> idx.Arity)
            max 0 (arrayRank - irank))

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
            if level.IndexSpace.Symmetry <> SymSymmetric then 1
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
                    level.IndexSpace.Symmetry = SymSymmetric
                
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
        | lastIdx :: _ -> IRVar lastIdx

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
type IRTypeDef =
    | IRTDAlias of name: string * ty: IRType
    | IRTDStruct of name: string * fields: (string * IRType) list
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
    member this.MkVar() = IRVar (this.FreshId())
    
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
    (kernelOutputRank: int)
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
                
                // Get S-dims from first member (all should have same structure)
                match groupMembers with
                | [] -> []
                | (_, arrTy, numSDims) :: _ ->
                    let sDimIndices = arrTy.IndexTypes |> List.take (min numSDims arrTy.IndexTypes.Length)
                    sDimIndices |> List.map (fun idx ->
                        if inCommGroup && arity > 1 then
                            // Use SymIdx with appropriate arity
                            { idx with 
                                Arity = arity
                                Symmetry = SymSymmetric
                                Id = builder.FreshId() }
                        else
                            // Use regular Idx (arity 1)
                            { idx with 
                                Arity = 1
                                Symmetry = SymNone
                                Id = builder.FreshId() }))
        
        // Step 4: Build T-dims from kernel output
        let outputTDims = 
            if kernelOutputRank > 0 then
                [0..kernelOutputRank-1] |> List.map (fun i ->
                    { Id = builder.FreshId()
                      Arity = 1
                      Extent = IRLit (IRLitInt 0L)  // Placeholder - real extent from kernel
                      Symmetry = SymNone
                      Tag = None
                      Kind = TDimension
                      Dependencies = [] })
            else []
        
        // Combine S-dims and T-dims
        let allDims = outputSDims @ outputTDims
        
        if allDims.IsEmpty then
            IRTScalar ETFloat64  // Scalar output
        else
            IRTArray { 
                ElemType = ETFloat64
                IndexTypes = allDims
                IsVirtual = false
                Identity = None 
            }

/// Build a loop nest from ApplyInfo
let buildLoopNest (info: ApplyInfo) (builder: IRBuilder) : LoopNest =
    let arrayTypes = 
        match info.Loop with
        | IRMethodFor mfInfo -> mfInfo.ArrayTypes
        | _ -> []
    
    let identities = 
        match info.Loop with
        | IRMethodFor mfInfo -> mfInfo.Identities
        | _ -> []
    
    let commGroups = 
        match info.Kernel with
        | IRLambda lInfo -> lInfo.CommGroups
        | _ -> []
    
    let sDimsPerArray = info.SDimsPerArray
    let loopLevels = buildLoopLevelStructure arrayTypes sDimsPerArray
    let triangularLevels = computeTriangularLevels arrayTypes identities commGroups sDimsPerArray
    let speedup = computePartialProductSpeedup arrayTypes identities commGroups sDimsPerArray
    
    // Deduce output type
    let outputType = deduceOutputType arrayTypes identities commGroups sDimsPerArray info.KernelOutputRank builder
    
    let mutable levels = []
    let mutable priorIndices = []
    
    for levelInfo in loopLevels do
        let i = levelInfo.GlobalLevelIndex
        let canTriangulate = if i < triangularLevels.Length then triangularLevels.[i] else false
        let idx = builder.FreshId()
        let extent = levelInfo.IndexSpace.Extent
        let state = if canTriangulate then SCCommutative else SCNeither
        let lowerBound = computeTriangularBound i priorIndices extent state
        
        let level = {
            Index = idx
            Extent = extent
            LowerBound = lowerBound
            State = state
            Parallel = i = 0
            BoundDependencies = priorIndices
        }
        
        levels <- levels @ [level]
        priorIndices <- idx :: priorIndices
    
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
type LoopIndexBinding = {
    /// Loop level index (0, 1, 2, ...)
    Level: int
    /// Generated index variable name ("__i0", "__i1", ...)
    IndexName: string
    /// Which input array this index iterates over (0-based)
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
    /// Extent as IRExpr (for flexible code gen)
    Extent: IRExpr
    /// Lower bound as IRExpr (IRLit 0 or IRVar for triangular)
    LowerBound: IRExpr
    /// Whether this loop level is parallelized
    IsParallel: bool
    /// Symcom state at this level
    State: SymcomState
}

/// Complete information needed to generate a loop nest
type LoopNestCodeGen = {
    /// All loop index bindings, in nesting order (outermost first)
    Bindings: LoopIndexBinding list
    /// The kernel expression (lambda body with param references)
    KernelExpr: IRExpr
    /// Kernel parameter info (for building element access expressions)
    KernelParams: IRParam list
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
    
    // Extract method_for info
    let (arrays, identities, arrayTypes, sDimsPerArray) =
        match info.Loop with
        | IRMethodFor mfInfo -> 
            (mfInfo.Arrays, mfInfo.Identities, mfInfo.ArrayTypes, mfInfo.SDimsPerArray)
        | _ -> ([], [], [], [])
    
    // Extract kernel info
    let (kernelParams, kernelBody, commGroups) =
        match info.Kernel with
        | IRLambda lInfo -> (lInfo.Params, lInfo.Body, lInfo.CommGroups)
        | IRReynolds (IRLambda lInfo, _) -> (lInfo.Params, lInfo.Body, lInfo.CommGroups)
        | _ -> ([], IRLit IRLitUnit, [])
    
    // Build loop level structure
    let loopLevels = buildLoopLevelStructure arrayTypes sDimsPerArray
    let triangularLevels = info.TriangularLevels
    let symcomStates = info.SymcomStates
    
    // Generate index names and track prior indices for triangular bounds
    let mutable bindings = []
    let mutable priorIndexIds : IRId list = []
    
    // Map array position to param
    let paramByArrayPos = 
        kernelParams |> List.mapi (fun i p -> (i, p)) |> Map.ofList
    
    for levelInfo in loopLevels do
        let level = levelInfo.GlobalLevelIndex
        let indexName = sprintf "__i%d" level
        let indexId = builder.FreshId()
        let arrayPos = levelInfo.ArrayIndex
        let arrName = if arrayPos < arrayNames.Length then arrayNames.[arrayPos] else sprintf "arr%d" arrayPos
        
        // Get corresponding kernel param
        let param = Map.tryFind arrayPos paramByArrayPos
        let paramName = param |> Option.map (fun p -> p.Name) |> Option.defaultValue (sprintf "p%d" arrayPos)
        let paramVarId = param |> Option.map (fun p -> p.VarId) |> Option.defaultValue -1
        
        // Lower bound for triangular iteration
        let isTriangular = level < triangularLevels.Length && triangularLevels.[level]
        let state = if level < symcomStates.Length then symcomStates.[level] else SCNeither
        
        let lowerBound =
            if isTriangular && not priorIndexIds.IsEmpty then
                // Triangular: start from prior index
                IRVar (List.head priorIndexIds)
            else
                IRLit (IRLitInt 0L)
        
        // Parallelism: typically outermost non-triangular loop
        let isParallel = level = 0 && not isTriangular
        
        let binding = {
            Level = level
            IndexName = indexName
            ArrayPosition = arrayPos
            ArrayName = arrName
            ParamName = paramName
            ParamVarId = paramVarId
            DimIndex = levelInfo.LocalDimIndex
            ArityComponent = levelInfo.ArityIndex
            Extent = levelInfo.IndexSpace.Extent
            LowerBound = lowerBound
            IsParallel = isParallel
            State = state
        }
        
        bindings <- bindings @ [binding]
        priorIndexIds <- indexId :: priorIndexIds
    
    let outputSymmVec = buildSymmVec info.OutputType
    
    {
        Bindings = bindings
        KernelExpr = kernelBody
        KernelParams = kernelParams
        OutputName = outputName
        OutputType = info.OutputType
        OutputSymmVec = outputSymmVec
        InputArrayNames = arrayNames
        SpeedupFactor = info.SpeedupFactor
        HasReynolds = info.HasReynolds
    }

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
    | IRTScalar ETUnit -> "Unit"
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
    | IRTUnit -> "Unit"
    | IRTPoly (base', var) -> sprintf "Poly<%s, %s>" (ppIRType base') var
    | IRTNat (Some n) -> sprintf "Nat<%d>" n
    | IRTNat None -> "Nat<?>"
    | IRTInfer id -> sprintf "T?%d" id

and ppIndexType (idx: IRIndexType) =
    // Inline extent printing since ppIRExpr is defined later
    let extentStr = 
        match idx.Extent with
        | IRLit (IRLitInt n) -> sprintf "%d" n
        | IRVar id -> sprintf "v%d" id
        | IRParam (name, _) -> name
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
    | ETUnit -> "Unit"

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
            | IRVar id -> sprintf "v%d" id
            | IRParam (name, _) -> name
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
let rec ppIRExprWithNames (names: Map<int, string>) indent expr =
    let pp = ppIRExprWithNames names 0
    let ind = String.replicate indent "  "
    match expr with
    | IRLit (IRLitInt n) -> sprintf "%d" n
    | IRLit (IRLitFloat f) -> sprintf "%f" f
    | IRLit (IRLitBool b) -> if b then "true" else "false"
    | IRLit IRLitUnit -> "()"
    | IRVar id -> 
        match Map.tryFind id names with
        | Some name -> name
        | None -> sprintf "v%d" id
    | IRParam (name, _) -> name
    | IRBinOp (mode, op, a, b) ->
        sprintf "(%s %s %s)" (pp a) (ppBinOpWithMode mode op) (pp b)
    | IRUnaryOp (op, a) ->
        sprintf "(%s%s)" (ppUnaryOp op) (pp a)
    | IRTuple es ->
        sprintf "(%s)" (es |> List.map pp |> String.concat ", ")
    | IRTupleProj (e, i) ->
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
        let reynoldsStr = if info.HasReynolds then sprintf ", reynolds=%dx" info.ReynoldsSpeedup else ""
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
    | IRApp (f, args) ->
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
    | IRArity None -> "arity"
    | IRArity (Some n) -> sprintf "arity(=%d)" n
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
    | IRAssign (id, v) ->
        match Map.tryFind id names with
        | Some name -> sprintf "%s <- %s" name (pp v)
        | None -> sprintf "v%d <- %s" id (pp v)
    | _ -> "<expr>"

/// Default pretty printer (no name context)
let ppIRExpr indent expr = ppIRExprWithNames Map.empty indent expr

/// Pretty print a loop nest
let ppLoopNest (nest: LoopNest) =
    let levels = 
        nest.Levels |> List.map (fun l ->
            sprintf "  for v%d in [%s..%s] [%s, par=%b]"
                l.Index
                (ppIRExpr 0 l.LowerBound)
                (ppIRExpr 0 l.Extent)
                (ppSymcomState l.State)
                l.Parallel)
        |> String.concat "\n"
    sprintf "LoopNest (speedup=%dx):\n%s\n  body: %s" 
        nest.SpeedupFactor levels (ppIRExpr 2 nest.Kernel)
