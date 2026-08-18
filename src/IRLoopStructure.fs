// Loop-level structure computation (LoopLevelInfo, symcom states, axis
// groups, triangular bounds) plus the wreath-tie deduction and
// deduceOutputType, which consume it. Peeled from IR.fs; everything here
// is a consumer-side shape analysis, not part of the core IR surface.
module Blade.IRLoopStructure

open Blade.Types
open Blade.IR

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
let internal wreathArgContribution (at: IRArrayType) : ((int * bool) list * IRExpr) option =
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


