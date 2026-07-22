// Blade type-system core (audit §4: Types.fs) — the expression-INDEPENDENT
// layer: element types, symmetry/kind classifications, unit signatures.
// Everything here is consumable without depending on IR.fs's 6k-line
// type/expression knot; Check-side code that only needs these can stop
// importing all of IR. (IRType/IRIndexType themselves CANNOT move while
// IRIndexType.Extent is an IRExpr — severing that coupling is a rewrite
// design decision; this file is the boundary the rewrite extends.)
module Blade.Types

type IRId = int

/// Symmetry class for index types
type SymmetryClass =
    | SymNone          // No symmetry (dense)
    | SymSymmetric     // Symmetric (i <= j)
    | SymAntisymmetric // Antisymmetric (i < j, negate on swap)
    | SymHermitian     // Hermitian (conjugate on swap)

/// Placement (membership + ranking) class for index types -- the Level-1 axis,
/// orthogonal to the symmetry-transform axis (SymmetryClass). It answers "which
/// tuples are stored, and how is a tuple ranked to a flat offset", independent
/// of any value transform applied on non-canonical access.
///
/// For the four built-in classes, placement is a function of the symmetry class:
/// PlaceDense for SymNone; PlaceCombinatorial for the three symmetries, which
/// carry their SymmetryClass so ranking/cardinality can distinguish inclusive
/// (sym/herm) from strict (antisym) combinadics. The placement-vs-symmetry
/// distinction only DIVERGES with tabulated types (CompoundIdx / SparseIdx),
/// whose validity is mask-/list-derived rather than a symmetry; those will add a
/// PlaceTabulated case here, recognized by placementOf from the index type's
/// definition rather than its SymmetryClass. Adding that case makes the dispatch
/// functions warn (FS0025) until each handles it -- the intended openness check.
type PlacementClass =
    | PlaceDense                          // row-major dense (Idx, EnumIdx)
    | PlaceCombinatorial of SymmetryClass // CNS-ranked compact (sym/antisym/herm)
    | PlaceTabulated                      // mask/list-derived, runtime table (CompoundIdx)

/// Core Level-1 classifier on the symmetry axis. The placement-proxy sites
/// (cardinality, compact grouping, allocator choice) read SymmetryClass today,
/// so this is the shared entry point; placementOf (below, once IRIndexType is
/// defined) wraps it for a full index type and is where tabulated detection will
/// hook in.
let placementClassOf (sym: SymmetryClass) : PlacementClass =
    match sym with
    | SymNone -> PlaceDense
    | SymSymmetric | SymAntisymmetric | SymHermitian -> PlaceCombinatorial sym

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

/// Reserved KIND of an index type (audit §3.3): the semantic discriminator
/// that used to be smuggled through `Tag` as "__..." sentinel strings, in
/// the same namespace as user index-type names. `Tag` is for NAMES; IxKind
/// is for KINDS. One case per legacy sentinel so the migration is 1:1.
///
/// Migration state: constructors still write the legacy sentinel into Tag
/// alongside the IxKind (some downstream identity/matching logic keys on
/// Tag equality generically), and the IR validator enforces that the two
/// encodings AGREE — so they cannot silently diverge. All kind DISPATCH
/// reads IxKind (directly or via the IxSymmetryLike/IxCompound/... active
/// pattern); dropping the Tag sentinels entirely is a rewrite-phase step.
type IxKind =
    | IxKPlain              // ordinary index type (user-named or anonymous)
    | IxKCompound           // "__compoundidx": masked product space
    | IxKCompoundDynamic    // "__compoundidx_dynamic": mask known only at runtime
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
    | IxKErrorRaggedNoPrior // "__error_ragged_no_prior": typecheck error marker
    | IxKErrorIrrepsBadSpec // "__error_irreps_bad_spec": typecheck error marker

/// The legacy Tag sentinel for a kind (None for IxKPlain). The single
/// source of the kind<->sentinel correspondence, used by constructors that
/// still mirror the kind into Tag and by the validator's agreement check.
let ixKindSentinel (k: IxKind) : string option =
    match k with
    | IxKPlain -> None
    | IxKCompound -> Some "__compoundidx"
    | IxKCompoundDynamic -> Some "__compoundidx_dynamic"
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
                            // an IxKIrreps record whose Tag is missing the prefix
                            // then FAILS the validator agreement check — intended
    | IxKErrorRaggedNoPrior -> Some "__error_ragged_no_prior"
    | IxKErrorIrrepsBadSpec -> Some "__error_irreps_bad_spec"

// ----------------------------------------------------------------------------
// IrrepsIdx tag encoding. The spec payload rides IN the Tag string — Tag
// equality is already index-space identity everywhere (unification, SIdx
// slot matching, group matching), so spec identity needs no side registry
// and no new IRIndexTypeG field. Format: "__irreps:<name>:<payload>" where
// <name> is the nominative alias name ("" for anonymous IrrepsIdx<spec>)
// and <payload> is "l,p,m|l,p,m|..." in spec order. Pure string ops only —
// core stays ML-free; Blade.ML owns StaticValue->spec decoding.
// ----------------------------------------------------------------------------

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

// ----------------------------------------------------------------------------
// Halo window tag encoding. Like IrrepsIdx above, the halo slot's payload
// rides IN the Tag string: "__halowin|<k>:<innerName>|<o1,o2,..>" where
// <k> is 'd' (dense inner) or 'c' (compound inner: ordinals walk PRESENT
// cells), <innerName> is the wrapped index's alias name ("" for anonymous /
// compound) and the csv is the static signed offset set (center = 0, sign =
// direction). Loop building re-derives the center's start offset from it
// (per-slot — the single shared IRRange offset cannot express multi-slot
// ranges), window reads re-derive the offset set and inner kind. Pure string
// ops; no new IRIndexTypeG field.
// ----------------------------------------------------------------------------

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

/// The center's first valid ordinal for a halo slot: max(0, -min(offsets ∪ {0})).
/// The loop over the SHRUNK slot starts at 0; adding this to the loop index
/// yields the true center ordinal in the inner index's space.
let haloStartOffsetOfTag (tag: string) : int64 option =
    match tag with
    | HaloWinTag (_, _, offs) -> Some (int64 (max 0 (- (min 0 (List.min offs)))))
    | _ -> None

/// Interior loss of a halo slot: (-min(offsets ∪ {0})) + max(offsets ∪ {0}).
/// Dense slots fold this into the extent at typecheck; compound slots (whose
/// extent is the runtime mask cardinality) subtract it at the loop bound.
let haloShrinkOfTag (tag: string) : int64 option =
    match tag with
    | HaloWinTag (_, _, offs) ->
        Some (int64 ((- (min 0 (List.min offs))) + (max 0 (List.max offs))))
    | _ -> None

/// Derive the kind from a (possibly user-supplied) Tag value: sentinel
/// strings map to their kind, anything else — user names, "__anon"
/// placeholders, None — is IxKPlain. For construction sites whose tag is
/// dynamic; sites with a literal sentinel should state the IxKind directly.
let ixKindOfTag (tag: string option) : IxKind =
    match tag with
    | Some "__compoundidx" -> IxKCompound
    | Some "__compoundidx_dynamic" -> IxKCompoundDynamic
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
    | Some t when t.StartsWith irrepsTagPrefix -> IxKIrreps
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

/// Ragged-ROW family: inner dimensions whose rank-1 rows carry their length
/// inline (`.len` on a RaggedRow<T>) rather than via `.extents` — peeled
/// ragged rows, DepIdx-allocated inners, and group_by members all share
/// this runtime shape.
let isRaggedRowKind (k: IxKind) : bool =
    match k with
    | IxKRagged | IxKRaggedInline | IxKRaggedOpaque
    | IxKDepInner | IxKGroupMember -> true
    | _ -> false

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


// ============================================================================
// The IRType family, generic over the extent representation (audit §4's
// "highest-leverage split", via the genericize-as-a-wedge route).
// ============================================================================
// IRIndexType's Extent is the ONE coupling that fused types to expressions:
// index types can be parameterized by runtime values (ragged lengths,
// compound masks — formalism §4.5), which the prototype models by embedding
// IRExpr in the extent slot. Making the family generic over 'Ext lets this
// file own the full type structure with no expression dependency; IR.fs
// instantiates the abbreviations `type IRType = IRTypeG<IRExpr>` (et al.),
// so every consumer keeps compiling unchanged. The rewrite's planned move
// — a dedicated Extent DU holding identity REFERENCES to runtime values
// instead of embedded expressions — then becomes a type-argument swap here
// rather than a restructuring of every file.

/// Kind of loop object (leaf; referenced by LoopTypeG).
type LoopKind =
    | LKMethod   // MethodLoop - arrays bound, awaiting kernel
    | LKObject   // ObjectLoop - kernel bound, awaiting arrays

type IRIndexTypeG<'Ext> = {
    Id: IRId
    Rank: int               // Number of index components (1 for Idx, 2 for SymIdx<2>, etc.)
    Extent: 'Ext           // Size expression (may depend on outer indices)
    Symmetry: SymmetryClass
    Tag: string option       // Name (for index space matching). Legacy: still
                             // mirrors IxKind's sentinel during migration —
                             // the validator enforces agreement (audit §3.3).
    Kind: DimensionKind      // S-dimension or T-dimension
    IxKind: IxKind           // Reserved kind discriminator (never a user name)
    Dependencies: IRId list  // Dependencies on outer loop indices (for triangular iteration)
}

/// Array type in IR with identity tracking.
/// ElemType is a full IRTypeG<'Ext> (post-Phase B2): primitives are wrapped as
/// IRTScalar, structs/sums appear as IRTNamed, inference variables as
/// IRTInfer, etc. Active patterns (PrimElem, AnyPrimElem, NamedElem, etc.)
/// project the IRTypeG<'Ext> into role-specific shapes for consumers.
and IRArrayTypeG<'Ext> = {
    ElemType: IRTypeG<'Ext>
    IndexTypes: IRIndexTypeG<'Ext> list
    IsVirtual: bool          // Virtual array (range, reverse, etc.)
    Identity: ArrayIdentity option  // For tracking array identity
}

/// Reference to an index type from the value side.
/// Used by IRTIdxTagged as the nominal tag (parallel to UnitSig for
/// IRTUnitAnnotated). Carries identity only — not the source index type's
/// structure (arity, symmetry, bijection, etc.), which lives in
/// IRIndexTypeG<'Ext> records attached to arrays.
///
/// Two index tags are compatible iff their IdxRefs are structurally
/// equal: same name for named, same nominalId for anonymous.
and IdxRefG<'Ext> =
    /// User-defined named index type: Nat<LatIdx>.
    /// Identity is the name.
    | IRefNamed of string
    /// Anonymous Idx<n> occurrence: Nat<Idx<n>>.
    /// Identity is the nominalId — fresh per source TyIdx node, must
    /// match the corresponding IRIndexTypeG<'Ext>.Id of the index type that
    /// emits this value. Extent preserved for diagnostics / pretty-
    /// printing only; NOT part of identity for unification.
    | IRefAnon of nominalId: int * extent: 'Ext
    /// The tag WILDCARD `Nat<_>` / `Int64<_>` / `Float64<_>`: matches a value
    /// carrying any nominal index tag, any unit signature, or none at all.
    ///
    /// Unlike the other two cases this carries NO identity — it is not a tag a
    /// value can have, it is a tag a PARAMETER declines to constrain. It only
    /// ever originates from a declared parameter type; no producer (iteration
    /// tagging, literal coercion, provider lowering) ever emits one.
    ///
    /// Consequences that keep it sound:
    ///   - Legal in parameter position only. A return type, let annotation,
    ///     struct field or array index slot has no incoming value to source a
    ///     tag from, so a wildcard there would be a silent hole; those sites
    ///     reject it via irTypeHasTagWildcard (BL4003).
    ///   - Unification is permissive in BOTH directions (Unify's wildcard arm
    ///     runs ahead of the strict named-vs-named arm), but the wildcard does
    ///     NOT absorb the tag it matched — the parameter's type stays
    ///     `Base<_>`. A wildcard-typed value therefore carries no more tag
    ///     guarantee than an untagged int, and checkArrayIndexTags treats the
    ///     two identically (warn, allow). Making the tag flow onward is the
    ///     tag-VARIABLE feature, which this is not.
    ///   - Arithmetic-transparent: inferBinOp strips it, so `1.0 * m` works on
    ///     a wildcard param even though it is refused for a concrete `Nat<Y>`.
    ///   - Erases to the inner type at codegen; there is no `using` alias.
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
    // IRTIdxTagged: a base type wrapped with a nominal index-type tag.
    // The shape parallels IRTUnitAnnotated (base * UnitSig) but uses an
    // IdxRefG<'Ext> tag with NO multiplicative algebra — index tags are nominal
    // labels, not exponent vectors. Formalism §4.18.5 puts Nat<LatIdx>
    // alongside Float<meters>, so the wrapper shape matches; the
    // composition semantics deliberately don't.
    //
    // Typical inner type is IRTScalar ETInt64 (giving "Nat<I>"), but the
    // constructor accepts any IRTypeG<'Ext> — same flexibility as IRTUnitAnnotated.
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
    | IRTIdxTagged of inner: IRTypeG<'Ext> * tag: IdxRefG<'Ext>
    // IRTDist: a distribution carried to stochastic order `order` — the
    // typed form of the PPL dist tower (ppl/NOTES.md). Nominal and
    // parameterized: `order` is a STATIC int (resolved before this type is
    // constructed — deliberately a plain int, not IRTNat, whose unification
    // ignores the value); `elem` is the cumulant element type (typically
    // IRTScalar ETFloat64); `axes` are the variable-axis index types of the
    // underlying random vector, needed to type component projection —
    // cumulant k of a Dist over axes D is Array<elem like SymIdx<k, D>>.
    //
    // Like IRTIdxTagged, this is a typecheck-time invariant with strict
    // unification (a bare tuple never implicitly becomes a Dist; only the
    // dist construction intrinsic and dist-typed operators produce one) and
    // it is ERASED before codegen: a Dist value lowers to the tuple of its
    // packed cumulant component arrays (κ_1 .. κ_order).
    | IRTDist of order: int * elem: IRTypeG<'Ext> * axes: IRIndexTypeG<'Ext> list
    | IRTNamed of string    // Named type (struct, sum type, etc.)
    | IRTInfer of int       // Unresolved type variable (id for unification)
    | IRTUnitAnnotated of IRTypeG<'Ext> * UnitSig  // Type with unit-of-measure annotation
    | IRTGroupKeys of outerIdx: IRIndexTypeG<'Ext> * sourceIdx: IRIndexTypeG<'Ext> * enumValues: EnumValue list option
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
    //     allocated storage. The IRIndexTypeG<'Ext> carries Tag, Symmetry, and
    //     Extent for the slot's domain.
    //   - SIdxVirt (virtual index): takes an index value but the values
    //     are computed on-the-fly (no allocated storage). Models virtual
    //     arrays like `range<I>`, `reverse<I>`, etc.
    //   - SVal (value/closure): takes a value of the given type. A pure
    //     function param.
    //
    // The `identity` field tracks array-handle identity for stored-array
    // shapes (was IRArrayTypeG<'Ext>.Identity). Pure functions and virtual arrays
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
    | IRTArrow of slots: IRArrowSlotG<'Ext> list * result: IRTypeG<'Ext> * identity: ArrayIdentity option

and IRArrowSlotG<'Ext> =
    | SIdx of IRIndexTypeG<'Ext>       // Storage-backed slot, consumed by an index value
    | SIdxVirt of IRIndexTypeG<'Ext>   // Virtual slot — values computed on-the-fly, no storage
    | SVal of IRTypeG<'Ext>            // Value/closure slot, consumed by any value of that type

/// Kind of loop object with arity tracking
and LoopTypeG<'Ext> = {
    Kind: LoopKind
    Arity: int option        // None = arity-polymorphic
    ArrayTypes: IRTypeG<'Ext> list  // Types of bound arrays (for MethodLoop)
    KernelType: IRTypeG<'Ext> option  // Type of bound kernel (for ObjectLoop)
}

/// Render an IxKIrreps index record's identity for diagnostics: the
/// round-trippable long form IrrepsIdx<[(l, p, m), ...]> — error messages
/// must show WHICH spec mismatched — prefixed with the nominative alias
/// name when the tag carries one. None for non-irreps records, so printers
/// use it as a pre-match arm ahead of their Symmetry dispatch.
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
            // Kind says irreps but the tag is missing/unparseable — a state
            // validateIR rejects; render a placeholder rather than crash.
            Some "IrrepsIdx<?>"

/// Literal values
