// Code-generation structures and storage policy: element/loop-index
// bindings, device buffer shapes, symmetry vectors, allocation routing,
// the index-type behavior strategies, and buildLoopNestCodeGen.
module Blade.IRStorage

open Blade.Types
open Blade.IR
open Blade.IRLoopStructure

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
    /// SHARED-VALUE mode (reduction joins): when `Some cppElemType` the nest
    /// neither writes cells nor accumulates -- it DECLARES
    /// `const <cppElemType> OutputName = <kernel>;` once per iteration of the
    /// joint nest, and other leaves read that name. This is how a named
    /// deferred map consumed by several legs (`let ct = cos <@> ph`, no
    /// `compute`) is evaluated ONCE per cell instead of once per leg (or
    /// materialized into an array). Meaningful only inside a merged nest, and
    /// only for leaves ordered BEFORE their consumers; None everywhere else.
    ShareDecl: string option
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
type internal RectangularBehavior() =
    interface IIndexTypeBehavior with
        member _.ClassName = "Rectangular"
        member _.Symmetry = SymNone
        member _.Validate _ = Ok ()
        member _.TransposeWithin () = TDataMove
        member _.Canonicalize () = CanonNone        // dense: indices already canonical
        member _.ReadTransform () = TfIdentity

/// Symmetric: triangular storage; transpose within the group is the identity
/// (A(i,j) = A(j,i), canonical storage unchanged).
type internal SymmetricBehavior() =
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
type internal AntisymmetricBehavior() =
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
type internal HermitianBehavior() =
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
type internal WreathBehavior() =
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
let internal rectangularBehavior = RectangularBehavior() :> IIndexTypeBehavior
let internal symmetricBehavior = SymmetricBehavior() :> IIndexTypeBehavior
let internal antisymmetricBehavior = AntisymmetricBehavior() :> IIndexTypeBehavior
let internal hermitianBehavior = HermitianBehavior() :> IIndexTypeBehavior
let internal wreathBehavior = WreathBehavior() :> IIndexTypeBehavior

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
        ShareDecl = None
    }
