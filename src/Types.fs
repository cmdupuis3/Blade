// Blade type-system core: the expression-independent layer (element types,
// symmetry/kind classifiers, unit signatures), usable without IR.fs's
// 6k-line type/expression knot. IRType/IRIndexType stay in IR.fs since
// IRIndexType.Extent is an IRExpr; the boundary a decoupling rewrite would extend.
module Blade.Types

type IRId = int

/// Symmetry class for index types
type SymmetryClass =
    | SymNone          // No symmetry (dense)
    | SymSymmetric     // Symmetric (i <= j)
    | SymAntisymmetric // Antisymmetric (i < j, negate on swap)
    | SymHermitian     // Hermitian (conjugate on swap)
    /// ITERATED WREATH (`OrbIdx<[(r1,s1),...,(rd,sd)], n>`, d >= 2 after
    /// normalization -- docs/plan-orbit-index-types.md section 2): S_r1 wr
    /// ... wr S_rd on prod(ri) axes, character = product of per-level signs;
    /// placed via an iterated-binomial fold, not a single combinadic.
    /// Nullary DELIBERATELY: level list rides Extent as `IROrbitClass`,
    /// forcing every exhaustive match to decide this case. Depth 1 never
    /// reaches here -- `OrbIdx<[(r,+/-)],n>` normalizes to Sym/Antisym,
    /// empty to plain `Idx<n>`.
    | SymWreath

/// Per-parameter SIGN parity of a kernel, recorded on the callable for the
/// wreath-tie soundness gate (`IR.deduceWreathTie`); mirrors
/// `Deduce.SignParity` (IRCallable sits below Deduce.fs in compile order).
/// TypeCheck is the sole producer; empty means "never computed" (all-KspUnknown).
type KernelSignParity =
    | KspOdd      // provably f(.., -x, ..) = -f(.., x, ..) in that parameter
    | KspEven     // provably f(.., -x, ..) =  f(.., x, ..) in that parameter
    | KspUnknown  // neither provable

/// Placement (membership + ranking) class for index types -- the Level-1
/// axis, orthogonal to the symmetry-transform axis (SymmetryClass): which
/// tuples are stored and how a tuple ranks to a flat offset.
/// PlaceDense for SymNone; PlaceCombinatorial (carrying SymmetryClass, so
/// ranking distinguishes inclusive sym/herm from strict antisym) for the
/// rest. Tabulated types (CompoundIdx/SparseIdx) diverge: validity is
/// mask-/list-derived, not symmetry-derived. Exhaustiveness (FS0025) here
/// is deliberate -- adding a case forces every dispatch site to update.
type PlacementClass =
    | PlaceDense                          // row-major dense (Idx, EnumIdx)
    | PlaceCombinatorial of SymmetryClass // CNS-ranked compact (sym/antisym/herm)
    | PlaceTabulated                      // mask/list-derived, runtime table (CompoundIdx)

/// Core Level-1 classifier on the symmetry axis; the shared entry point
/// for placement-proxy sites (cardinality, compact grouping, allocator
/// choice) that read SymmetryClass today.
let placementClassOf (sym: SymmetryClass) : PlacementClass =
    match sym with
    | SymNone -> PlaceDense
    // Wreath is closed-form placed (iterated binomial, one C(.,.) per level)
    // so it's Combinatorial not Tabulated -- but not a single combinadic
    // over `Rank`: consumers must read the level list off the Extent
    // marker, not assume C(n+r-1,r). `bufferGroupCardinality` does this.
    | SymWreath -> PlaceCombinatorial SymWreath
    | SymSymmetric | SymAntisymmetric | SymHermitian -> PlaceCombinatorial sym

/// ORBIT PLACEMENT (design skeleton, NOT WIRED IN): generalizes the two
/// cases above as a TRIPLE -- R positions, POSITION GROUP G <= S_R of
/// interchangeable slot permutations, CHARACTER chi: G -> {+1,-1,conj}
/// (`SymmetryClass` above IS chi; `PositionGroup` the missing half).
/// Closed-form rank only for PgFullSym (C(N+R-1,R) inclusive / C(N,R)
/// strict -- BladeDMWF/BladeBinomial, today's comm/antisymm) and
/// PgProduct (prod_j C(N_j+R_j-1,R_j) -- BladeMixedRadix, tied-perm
/// layout); PgOpaque varies in stabilizer size, a Burnside sum with no
/// closed form (BladeCounting.v), PlaceTabulated. ZERO-SET: G-stabilizer
/// chi = -1 forces zero (v = chi(h)*v = -v) -- why PlaceCombinatorial
/// carries SymmetryClass, generalizing the antisymmetric zero-diagonal.
type PositionGroup =
    | PgTrivial                       // G = 1                -> dense
    | PgFullSym of rank: int          // G = S_R              -> closed form
    | PgProduct of ranks: int list    // G = prod_j S_{R_j}   -> mixed radix
    | PgOpaque of tag: string         // any other finite G   -> runtime table

/// Placement from the (G, chi) pair. Agrees with `placementClassOf` on
/// every shipped index type (`SymIdx<R,N>` is `PgFullSym R`, dense
/// `Idx<N>` is `PgTrivial`) -- a strict generalization, not a rewrite.
let placementOfOrbit (g: PositionGroup) (chi: SymmetryClass) : PlacementClass =
    match g with
    | PgTrivial -> PlaceDense
    | PgFullSym _ -> PlaceCombinatorial chi
    | PgProduct _ -> PlaceCombinatorial chi
    | PgOpaque _ -> PlaceTabulated

/// Commutativity/Symmetry state at each loop level (Section 13.1):
/// determines whether triangular iteration is valid at this position.
type SymcomState =
    | SCNeither       // Independent iteration - no optimization
    | SCSymmetric     // Same array, symmetric dimension - triangular valid
    | SCCommutative   // Different arrays but in comm group - triangular valid
    | SCBoth          // Same array + comm group - triangular valid, best case

// Array Identity Tracking (critical for symmetry exploitation)

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

// Index Types with Dependency Tracking

/// Dimension kind: S-dimensions (spatial, participate in symmetry
/// optimization) vs T-dimensions (temporal/time, do not).
type DimensionKind =
    | SDimension   // Spatial dimension - participates in symmetry
    | TDimension   // Temporal dimension - does not participate in symmetry

/// Reserved KIND of an index type (audit section 3.3): the discriminator
/// alongside `Tag` ("__..." sentinel strings sharing a namespace with user
/// index-type names) -- Tag is for NAMES, IxKind for KINDS. Constructors
/// mirror the sentinel into Tag too; the IR validator enforces the two AGREE.
/// All kind DISPATCH reads IxKind, directly or via IxSymmetryLike/
/// IxCompound/... active patterns.
type IxKind =
    | IxKPlain              // ordinary index type (user-named or anonymous)
    | IxKCompound           // "__compoundidx": masked product space
    | IxKCompoundDynamic    // "__compoundidx_dynamic": mask known only at runtime
    | IxKSparse             // "__sparseidx": explicit key enumeration, hash lookup
    | IxKOrbit              // "__orbidx": iterated-wreath (OrbIdx), depth >= 2.
                            // (rank,sign) level list rides Extent as IROrbitClass;
                            // Rank = product of level ranks. Always paired with
                            // Symmetry = SymWreath (depth <=1 normalizes away).
    | IxKDep                // "__depidx": dependent-extent head marker
    | IxKDepInner           // "__depidx_inner": the dependent inner dimension
    | IxKDepOuter           // "__depidx_outer": the outer dim a DepIdx depends on
    | IxKRagged             // "__raggedidx": ragged (per-row extent) dimension
    | IxKRaggedInline       // "__raggedidx_inline": ragged with inline lengths
    | IxKRaggedOpaque       // "__raggedidx_opaque": context-supplied extent
    | IxKGroupOuter         // "__group_outer": group_by outer (per-group) slot
    | IxKGroupMember        // "__group_member": group_by member slot
    | IxKSeq                // "__seq": sequence-combinator-produced dimension
    | IxKIrreps             // "__irreps:<name>:<payload>": block-structured dense
                            // index over an equivariant irreps spec; the tag is
                            // PARAMETERIZED (spec payload + optional alias name),
                            // so the kind maps from the prefix, not one sentinel
    | IxKPgIrreps           // "__pgirreps:<group>:<name>:<payload>": the SECOND
                            // block-spec member (point groups); same
                            // parameterized-tag discipline as IxKIrreps, over a
                            // DIFFERENT frozen prefix (O(3) format is byte-frozen).
    | IxKErrorRaggedNoPrior // "__error_ragged_no_prior": typecheck error marker
    | IxKErrorIrrepsBadSpec // "__error_irreps_bad_spec": typecheck error marker
    | IxKErrorPgIrrepsBadSpec // "__error_pgirreps_bad_spec": typecheck error marker

/// The Tag sentinel for a kind (None for IxKPlain). The single source of
/// the kind<->sentinel correspondence, used by constructors that mirror
/// the kind into Tag and by the validator's agreement check.
let ixKindSentinel (k: IxKind) : string option =
    match k with
    | IxKPlain -> None
    | IxKCompound -> Some "__compoundidx"
    | IxKCompoundDynamic -> Some "__compoundidx_dynamic"
    | IxKSparse -> Some "__sparseidx"
    | IxKOrbit -> Some "__orbidx"
    | IxKDep -> Some "__depidx"
    | IxKDepInner -> Some "__depidx_inner"
    | IxKDepOuter -> Some "__depidx_outer"
    | IxKRagged -> Some "__raggedidx"
    | IxKRaggedInline -> Some "__raggedidx_inline"
    | IxKRaggedOpaque -> Some "__raggedidx_opaque"
    | IxKGroupOuter -> Some "__group_outer"
    | IxKGroupMember -> Some "__group_member"
    | IxKSeq -> Some "__seq"
    | IxKIrreps -> None     // parameterized tag (mkIrrepsTag), no single sentinel;
                            // Tag missing the prefix FAILS validator agreement (intended)
    | IxKPgIrreps -> None   // ditto (mkPgIrrepsTag)
    | IxKErrorRaggedNoPrior -> Some "__error_ragged_no_prior"
    | IxKErrorIrrepsBadSpec -> Some "__error_irreps_bad_spec"
    | IxKErrorPgIrrepsBadSpec -> Some "__error_pgirreps_bad_spec"

// IrrepsIdx tag encoding: the spec payload rides IN the Tag string. Tag
// equality is already index-space identity everywhere, so no side registry
// or new IRIndexTypeG field is needed. Format: "__irreps:<name>:<payload>",
// <name> = alias ("" if anonymous), <payload> = "l,p,m|l,p,m|..." in spec
// order. Pure string ops; core stays ML-free, Blade.ML owns decoding.

let irrepsTagPrefix = "__irreps:"

/// Serialize an irreps spec (+ optional alias name) into its canonical Tag.
let mkIrrepsTag (aliasName: string option) (spec: (int * int * int) list) : string =
    let payload =
        spec |> List.map (fun (l, p, m) -> sprintf "%d,%d,%d" l p m) |> String.concat "|"
    sprintf "%s%s:%s" irrepsTagPrefix (defaultArg aliasName "") payload

/// Parse an irreps Tag back into (alias name option, (l, parity, mult) list).
/// Total: any string not produced by mkIrrepsTag yields None.
let (|IrrepsTag|_|) (tag: string) : (string option * (int * int * int) list) option =
    if not (tag.StartsWith irrepsTagPrefix) then None
    else
        let rest = tag.Substring irrepsTagPrefix.Length
        match rest.IndexOf ':' with
        | -1 -> None
        | sep ->
            let name = rest.Substring(0, sep)
            let entryOf (s: string) =
                match s.Split ',' with
                | [| l; p; m |] ->
                    match System.Int32.TryParse l, System.Int32.TryParse p, System.Int32.TryParse m with
                    | (true, lv), (true, pv), (true, mv) -> Some (lv, pv, mv)
                    | _ -> None
                | _ -> None
            let entries = rest.Substring(sep + 1).Split '|' |> Array.toList |> List.map entryOf
            if List.forall Option.isSome entries then
                Some ((if name = "" then None else Some name), List.map Option.get entries)
            else None

// PgIrrepsIdx tag encoding -- the SECOND block-spec member, same discipline
// as the irreps tag but a DELIBERATELY SEPARATE format (O(3) `__irreps:` is
// BYTE-FROZEN): "__pgirreps:<group>:<name>:<payload>". <group> = point
// group's frozen registry name ("C4", "D4"); <name> = alias ("" if
// anonymous); <payload> = "LABEL,mult|..." in spec order. LABEL NAMES ride
// in the tag on purpose (frozen table data; the tag IS the diagnostic
// identity). Pure string ops; Blade.ML owns decoding incl. the
// unknown-label diagnostic. The two prefixes are disjoint, so tag equality
// decides CROSS-MEMBER identity for free: `IrrepsIdx<s>` and
// `PgIrrepsIdx<G, s'>` of the same extent are always distinct types.

let pgIrrepsTagPrefix = "__pgirreps:"

/// Serialize a point-group spec (group name + optional alias name +
/// (LABEL, mult) entries) into its canonical Tag.
let mkPgIrrepsTag (group: string) (aliasName: string option) (spec: (string * int) list) : string =
    let payload =
        spec |> List.map (fun (label, m) -> sprintf "%s,%d" label m) |> String.concat "|"
    sprintf "%s%s:%s:%s" pgIrrepsTagPrefix group (defaultArg aliasName "") payload

/// Parse a pg-irreps Tag back into (group, alias name option, (LABEL, mult) list).
/// Total: any string not produced by mkPgIrrepsTag yields None.
let (|PgIrrepsTag|_|) (tag: string) : (string * string option * (string * int) list) option =
    if not (tag.StartsWith pgIrrepsTagPrefix) then None
    else
        let rest = tag.Substring pgIrrepsTagPrefix.Length
        match rest.IndexOf ':' with
        | -1 -> None
        | sepG ->
            let group = rest.Substring(0, sepG)
            let rest2 = rest.Substring(sepG + 1)
            match rest2.IndexOf ':' with
            | -1 -> None
            | sepN ->
                let name = rest2.Substring(0, sepN)
                let entryOf (s: string) =
                    match s.Split ',' with
                    | [| label; m |] when label <> "" ->
                        match System.Int32.TryParse m with
                        | true, mv -> Some (label, mv)
                        | _ -> None
                    | _ -> None
                let entries = rest2.Substring(sepN + 1).Split '|' |> Array.toList |> List.map entryOf
                if group <> "" && List.forall Option.isSome entries then
                    Some (group, (if name = "" then None else Some name), List.map Option.get entries)
                else None

// Halo window tag encoding. Like IrrepsIdx, the payload rides IN the Tag:
// "__halowin|<k>:<innerName>|<o1,o2,..>", <k> = 'd' (dense inner) or 'c'
// (compound inner: ordinals walk PRESENT cells), <innerName> = wrapped
// index's alias, csv = static signed offset set (center = 0). Loop building
// re-derives the center's start offset per-slot (shared IRRange offset
// can't express multi-slot ranges); window reads re-derive offset set/kind.

let haloWinTagPrefix = "__halowin|"

/// Parse a halo window Tag into (isCompound, inner alias name, offset list).
/// Total: any string not shaped like a halo tag yields None.
let (|HaloWinTag|_|) (tag: string) : (bool * string * int list) option =
    if not (tag.StartsWith haloWinTagPrefix) then None
    else
        match tag.Split '|' with
        | [| _; marked; csv |] when marked.Length >= 2 && (marked.[0] = 'd' || marked.[0] = 'c') && marked.[1] = ':' ->
            let offs =
                csv.Split ',' |> Array.toList
                |> List.map (fun s -> match System.Int32.TryParse s with true, v -> Some v | _ -> None)
            if List.forall Option.isSome offs && not offs.IsEmpty then
                Some (marked.[0] = 'c', marked.Substring 2, List.map Option.get offs)
            else None
        | _ -> None

/// The center's first valid ordinal for a halo slot: max(0, -min(offsets union {0})).
/// The loop over the SHRUNK slot starts at 0; adding this to the loop index
/// yields the true center ordinal in the inner index's space.
let haloStartOffsetOfTag (tag: string) : int64 option =
    match tag with
    | HaloWinTag (_, _, offs) -> Some (int64 (max 0 (- (min 0 (List.min offs)))))
    | _ -> None

/// Interior loss of a halo slot: (-min(offsets union {0})) + max(offsets union {0}).
/// Dense slots fold this into the extent at typecheck; compound slots (whose
/// extent is the runtime mask cardinality) subtract it at the loop bound.
let haloShrinkOfTag (tag: string) : int64 option =
    match tag with
    | HaloWinTag (_, _, offs) ->
        Some (int64 ((- (min 0 (List.min offs))) + (max 0 (List.max offs))))
    | _ -> None

/// Derive the kind from a (possibly user-supplied) Tag value: sentinel strings
/// map to their kind, anything else (user names, "__anon", None) is IxKPlain.
/// For dynamic-tag sites; a literal sentinel should state IxKind directly.
let ixKindOfTag (tag: string option) : IxKind =
    match tag with
    | Some "__compoundidx" -> IxKCompound
    | Some "__compoundidx_dynamic" -> IxKCompoundDynamic
    | Some "__sparseidx" -> IxKSparse
    | Some "__orbidx" -> IxKOrbit
    | Some "__depidx" -> IxKDep
    | Some "__depidx_inner" -> IxKDepInner
    | Some "__depidx_outer" -> IxKDepOuter
    | Some "__raggedidx" -> IxKRagged
    | Some "__raggedidx_inline" -> IxKRaggedInline
    | Some "__raggedidx_opaque" -> IxKRaggedOpaque
    | Some "__group_outer" -> IxKGroupOuter
    | Some "__group_member" -> IxKGroupMember
    | Some "__seq" -> IxKSeq
    | Some "__error_ragged_no_prior" -> IxKErrorRaggedNoPrior
    | Some "__error_irreps_bad_spec" -> IxKErrorIrrepsBadSpec
    | Some "__error_pgirreps_bad_spec" -> IxKErrorPgIrrepsBadSpec
    | Some t when t.StartsWith irrepsTagPrefix -> IxKIrreps
    | Some t when t.StartsWith pgIrrepsTagPrefix -> IxKPgIrreps
    // A compound-inner halo slot keeps IxKCompound: the compound machinery
    // (cidx materialization, cardinality bound) must still engage. Dense
    // halo slots fall through to IxKPlain like any other "__" placeholder.
    | Some t when t.StartsWith (haloWinTagPrefix + "c:") -> IxKCompound
    | _ -> IxKPlain

/// Ragged FAMILY: dimensions whose extent varies per outer row.
let isRaggedFamilyKind (k: IxKind) : bool =
    match k with
    | IxKRagged | IxKRaggedInline | IxKRaggedOpaque -> true
    | _ -> false

/// Ragged-ROW family: inner dims whose rank-1 rows carry their length inline
/// (`.len` on a RaggedRow<T>) rather than via `.extents` -- peeled ragged
/// rows, DepIdx-allocated inners, and group_by members all share this shape.
let isRaggedRowKind (k: IxKind) : bool =
    match k with
    | IxKRagged | IxKRaggedInline | IxKRaggedOpaque
    | IxKDepInner | IxKGroupMember -> true
    | _ -> false

/// Unit of measure signature. `Dims` is the STRUCTURAL layer: a product of
/// base units with integer exponents (e.g. velocity = {meters: 1, seconds:
/// -1}); dimensionless = empty map. `Nominal` is the QUANTITY layer: a
/// nominal identity declared via `Unit speed: mps`, entailing exactly the
/// dims it was declared with. Structural units and plain aliases carry
/// Nominal = None; the nominal layer is exactly one level deep (quantity
/// names are TERMINAL in unit algebra).
type UnitSig = { Nominal: string option; Dims: Map<string, int> }

/// Unit arithmetic: dimensionless (no nominal, empty dims)
let unitDimensionless : UnitSig = { Nominal = None; Dims = Map.empty }

/// A structural (non-nominal) signature over the given dims.
let unitOfDims (dims: Map<string, int>) : UnitSig = { Nominal = None; Dims = dims }

/// Normalize: remove zero-exponent entries (nominal untouched)
let unitNormalize (u: UnitSig) : UnitSig =
    { u with Dims = u.Dims |> Map.filter (fun _ exp -> exp <> 0) }

/// Unit multiplication: add exponents. Multiplicative composition DROPS the
/// nominal layer: a quantity is an identity, not a factor, so `speed * s`
/// yields the structural product of the dims.
let unitMul (a: UnitSig) (b: UnitSig) : UnitSig =
    let merged =
        Map.fold (fun acc k v ->
            let existing = Map.tryFind k acc |> Option.defaultValue 0
            Map.add k (existing + v) acc) a.Dims b.Dims
    unitNormalize { Nominal = None; Dims = merged }

/// Unit division: subtract exponents (drops nominal, like unitMul)
let unitDiv (a: UnitSig) (b: UnitSig) : UnitSig =
    let merged =
        Map.fold (fun acc k v ->
            let existing = Map.tryFind k acc |> Option.defaultValue 0
            Map.add k (existing - v) acc) a.Dims b.Dims
    unitNormalize { Nominal = None; Dims = merged }

/// Unit power: scale all exponents (drops nominal, like unitMul)
let unitPow (u: UnitSig) (n: int) : UnitSig =
    if n = 0 then unitDimensionless
    else unitNormalize { Nominal = None; Dims = u.Dims |> Map.map (fun _ exp -> exp * n) }

/// Check if two unit signatures are compatible: dims must be equal, and the
/// nominal layers must AGREE -- both the same quantity, or at least one side
/// structural (None). Two DIFFERENT quantities are incompatible even over
/// identical dims (that is what the nominal layer is for).
let unitCompatible (a: UnitSig) (b: UnitSig) : bool =
    (unitNormalize a).Dims = (unitNormalize b).Dims
    && (match a.Nominal, b.Nominal with
        | Some na, Some nb -> na = nb
        | _ -> true)

/// Merge two COMPATIBLE signatures (post-unitCompatible), keeping whichever
/// nominal is present: additive ops over `speed + m/s` stay `speed`.
let unitJoin (a: UnitSig) (b: UnitSig) : UnitSig =
    match a.Nominal with
    | Some _ -> a
    | None -> { a with Nominal = b.Nominal }

/// Pretty-print a unit signature (error-message form). A quantity prints as
/// its nominal name; a structural signature prints its dims product; the
/// empty structural signature prints "dimensionless".
let ppUnitSig (u: UnitSig) : string =
    match u.Nominal with
    | Some n -> n
    | None ->
        let dims = u.Dims
        if Map.isEmpty dims then "dimensionless"
        else
            let pos = dims |> Map.filter (fun _ e -> e > 0) |> Map.toList
            let neg = dims |> Map.filter (fun _ e -> e < 0) |> Map.toList
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

/// Pretty-print a unit signature in TYPE-ARGUMENT position (`Float64<...>`).
/// A quantity renders as its nominal name; a structural signature as its dims;
/// an empty structural signature -- dims cancelled via division, e.g.
/// `speed/speed` or `m/m` -- renders as `Unitless`, distinct from a bare type
/// that never had units. Display provenance only: Unitless does not affect
/// unification (a dims-empty sig unifies freely either way).
let ppUnitSigType (u: UnitSig) : string =
    match u.Nominal with
    | Some n -> n
    | None ->
        if Map.isEmpty ((unitNormalize u).Dims) then "Unitless"
        else ppUnitSig u

/// Value carried by an EnumIdx alias declaration: all-int or all-string
/// (mixed rejected at typecheck). All-int lowers to int64_t, all-string to
/// std::string; both share the same SQL-like ops, differing only in the
/// Case-2 comparison. Predeclared: IRTGroupKeys below needs the type.
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
    // Named index references (value and element position) are represented
    // at the IRType level as `IRTIdxTagged (inner, IRefNamed name)` below.


// The IRType family, generic over the extent representation 'Ext. In IR.fs,
// IRIndexType.Extent is an IRExpr -- the coupling that fused types to
// expressions (index types depend on runtime values: ragged lengths,
// compound masks, formalism 4.5). Generic here lets this file own the full
// type structure with no expression dependency; IR.fs instantiates
// `type IRType = IRTypeG<IRExpr>` (et al.) unchanged.

/// Kind of loop object (leaf; referenced by LoopTypeG).
type LoopKind =
    | LKMethod   // MethodLoop - arrays bound, awaiting kernel
    | LKObject   // ObjectLoop - kernel bound, awaiting arrays

type IRIndexTypeG<'Ext> = {
    Id: IRId
    Rank: int               // Number of index components (1 for Idx, 2 for SymIdx<2>, etc.)
    Extent: 'Ext           // Size expression (may depend on outer indices)
    Symmetry: SymmetryClass
    Tag: string option       // Name (index space matching); mirrors IxKind's
                             // sentinel -- validator enforces agreement (3.3)
    Kind: DimensionKind      // S-dimension or T-dimension
    IxKind: IxKind           // Reserved kind discriminator (never a user name)
    Dependencies: IRId list  // Dependencies on outer loop indices (for triangular iteration)
}

/// Array type in IR with identity tracking. ElemType is a full IRTypeG<'Ext>
/// (IRTScalar/IRTNamed/IRTInfer for primitives/structs-sums/inference vars);
/// active patterns (PrimElem, AnyPrimElem, NamedElem, etc.) project it into
/// role-specific shapes for consumers.
and IRArrayTypeG<'Ext> = {
    ElemType: IRTypeG<'Ext>
    IndexTypes: IRIndexTypeG<'Ext> list
    IsVirtual: bool          // Virtual array (range, reverse, etc.)
    Identity: ArrayIdentity option  // For tracking array identity
}

/// Reference to an index type from the value side; the nominal tag used by
/// IRTIdxTagged (parallel to UnitSig for IRTUnitAnnotated). Carries identity
/// only, not structure (arity, symmetry, bijection), which lives on
/// IRIndexTypeG<'Ext> array records. Two tags are compatible iff IdxRefs
/// are structurally equal (name for named, nominalId for anonymous).
and IdxRefG<'Ext> =
    /// User-defined named index type: Nat<LatIdx>. Identity is the name.
    | IRefNamed of string
    /// Anonymous Idx<n> occurrence: Nat<Idx<n>>. Identity is the nominalId
    /// -- fresh per source TyIdx node, matching the IRIndexTypeG<'Ext>.Id.
    /// Extent is kept for diagnostics only, NOT part of unification identity.
    | IRefAnon of nominalId: int * extent: 'Ext
    /// The tag WILDCARD `Nat<_>` / `Int64<_>` / `Float64<_>`: matches any
    /// nominal index tag, unit signature, or none. Carries NO identity (a
    /// declining PARAMETER, not a value's tag); only originates from a
    /// declared parameter type. Soundness:
    ///   - Legal in parameter position only -- return types, let/field/
    ///     index slots reject it as a silent hole (irTypeHasTagWildcard,
    ///     BL4003).
    ///   - Unification is permissive both ways but does NOT absorb the
    ///     matched tag (checkArrayIndexTags: warn, allow) -- that would be
    ///     the tag-VARIABLE feature, which this is not.
    ///   - Arithmetic-transparent (`1.0 * m` works on a wildcard param,
    ///     refused for concrete `Nat<Y>`); erases to inner type at codegen.
    | IRefAny

/// IR Types
and IRTypeG<'Ext> =
    | IRTScalar of ElemType
    | IRTTuple of IRTypeG<'Ext> list
    | IRTLoop of LoopTypeG<'Ext>
    | IRTComputation of IRTypeG<'Ext>   // Suspended computation producing this type
    | IRTUnit
    | IRTPoly of baseType: IRTypeG<'Ext> * arityVar: string  // Arity-polymorphic type
    | IRTNat of int option  // Type-level natural number (None = variable)
    // IRTIdxTagged: a base type wrapped with a nominal index-type tag,
    // shaped like IRTUnitAnnotated (base * UnitSig) but with an
    // IdxRefG<'Ext> tag and NO multiplicative algebra -- tags are nominal
    // labels, not exponent vectors (formalism 4.18.5: Nat<LatIdx> alongside
    // Float<meters> matches shape only). Typical inner is IRTScalar
    // ETInt64 ("Nat<I>"), any IRTypeG<'Ext> accepted. Identity: structural
    // equality on (inner, idxRef) -- unify iff inner types unify AND
    // IdxRefs match (name=name for IRefNamed, nominalId=nominalId for
    // IRefAnon). Renders as inner type at codegen (tag is typecheck-only);
    // IRefNamed uses the C++ typedef as a doc hook. Distinct from
    // `IRTNat of int option`: this is a runtime value's type, IRTNat a
    // type-parameter for ranks.
    | IRTIdxTagged of inner: IRTypeG<'Ext> * tag: IdxRefG<'Ext>
    // IRTDist: a distribution at stochastic order `order` -- typed form of
    // the PPL dist tower (ppl/NOTES.md). `order` is a STATIC plain int (not
    // IRTNat; unification ignores its value); `elem` is the cumulant type
    // (typically ETFloat64); `axes` types the random vector so component
    // projection can be typed: cumulant k over axes D is Array<elem like
    // SymIdx<k, D>>. Strict-unification typecheck-time invariant (only the
    // dist-construction intrinsic and dist-typed ops produce one), ERASED
    // before codegen to the tuple of packed cumulants (kappa_1..kappa_order).
    | IRTDist of order: int * elem: IRTypeG<'Ext> * axes: IRIndexTypeG<'Ext> list
    | IRTNamed of string    // Named type (struct, sum type, etc.)
    | IRTInfer of int       // Unresolved type variable (id for unification)
    | IRTUnitAnnotated of IRTypeG<'Ext> * UnitSig  // Type with unit-of-measure annotation
    | IRTGroupKeys of outerIdx: IRIndexTypeG<'Ext> * sourceIdx: IRIndexTypeG<'Ext> * enumValues: EnumValue list option
      // GroupKeys: CSR mapping sourceIdx -> groups by outerIdx; enumValues carries key values for reverse lookup when keys are EnumIdx.
    // IRTArrow: the sole arrow-shaped type, subsuming function types (all-SVal
    // slots) and stored/virtual arrays (distinguished only by slot kind);
    // slots consumed left-to-right, result emerges once all are consumed.
    // SIdx = storage-backed index slot (Tag/Symmetry/Extent on the
    // IRIndexTypeG<'Ext>); SIdxVirt = virtual index slot, computed on-the-fly,
    // no storage (`range<I>`, `reverse<I>`); SVal = value/closure slot.
    // `identity`: None for pure functions/virtual arrays, Some when the
    // outermost dimension is stored.
    // Shape constraints (`mkVirtualArrayArrow`/`validateArrowShape`): (1) once
    // a slot is SIdxVirt every slot after it must be too -- no stored
    // sub-arrays/closures after virtual; (2) if any slot is SIdxVirt the
    // result must not be IRTArrow -- virtual elements must be simple values.
    // `IRTArrow ([], ret, None)` is reserved for nullary functions;
    // `ArrayElem` rejects it so a nullary call isn't misread as rank-0
    // indexing (rank-0 arrays collapse to element type at `mkArrayLike`).
    // Examples:
    //   [SVal; SVal] -> ret           : pure binary function
    //   [SIdx; SIdx] -> elem          : stored 2D array
    //   [SIdxVirt; SIdxVirt] -> elem  : virtual 2D generator
    //   [SIdx; SIdxVirt] -> elem      : stored array of virtual sub-arrays
    //   [SVal; SIdx] -> elem          : function returning a stored array
    //   [SIdxVirt; SIdx] -> elem      : INVALID -- stored after virtual
    | IRTArrow of slots: IRArrowSlotG<'Ext> list * result: IRTypeG<'Ext> * identity: ArrayIdentity option

and IRArrowSlotG<'Ext> =
    | SIdx of IRIndexTypeG<'Ext>       // Storage-backed slot, consumed by an index value
    | SIdxVirt of IRIndexTypeG<'Ext>   // Virtual slot -- values computed on-the-fly, no storage
    | SVal of IRTypeG<'Ext>            // Value/closure slot, consumed by any value of that type

/// Kind of loop object with arity tracking
and LoopTypeG<'Ext> = {
    Kind: LoopKind
    Arity: int option        // None = arity-polymorphic
    ArrayTypes: IRTypeG<'Ext> list  // Types of bound arrays (for MethodLoop)
    KernelType: IRTypeG<'Ext> option  // Type of bound kernel (for ObjectLoop)
}

/// Render an IxKIrreps record's identity for diagnostics: the round-trippable
/// `IrrepsIdx<[(l, p, m), ...]>`, prefixed with the alias if present. None
/// for non-irreps records (a pre-match arm ahead of Symmetry dispatch).
let (|IrrepsIdxLike|_|) (ix: IRIndexTypeG<'Ext>) : string option =
    if ix.IxKind <> IxKIrreps then None
    else
        match ix.Tag with
        | Some (IrrepsTag (nameOpt, triples)) ->
            let payload =
                triples
                |> List.map (fun (l, p, m) -> sprintf "(%d, %d, %d)" l p m)
                |> String.concat ", "
            let core = sprintf "IrrepsIdx<[%s]>" payload
            Some (match nameOpt with
                  | Some n -> sprintf "%s (= %s)" n core
                  | None -> core)
        | _ ->
            // Kind says irreps but the tag is missing/unparseable -- a state
            // validateIR rejects; render a placeholder rather than crash.
            Some "IrrepsIdx<?>"

/// The pg sibling of `IrrepsIdxLike`: renders `PgIrrepsIdx<C4, [("A", 1),
/// ("E", 2)]>`, prefixed with the alias if present. NOT merged with
/// IrrepsIdxLike -- they render differently (group is part of pg identity;
/// labels are names, not (l, parity) integers) and diagnostics show both.
let (|PgIrrepsIdxLike|_|) (ix: IRIndexTypeG<'Ext>) : string option =
    if ix.IxKind <> IxKPgIrreps then None
    else
        match ix.Tag with
        | Some (PgIrrepsTag (group, nameOpt, entries)) ->
            let payload =
                entries
                |> List.map (fun (label, m) -> sprintf "(\"%s\", %d)" label m)
                |> String.concat ", "
            let core = sprintf "PgIrrepsIdx<%s, [%s]>" group payload
            Some (match nameOpt with
                  | Some n -> sprintf "%s (= %s)" n core
                  | None -> core)
        | _ -> Some "PgIrrepsIdx<?>"
