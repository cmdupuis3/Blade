// Blade tree-walking interpreter — dense array storage, access, virtual arrays,
// eager array ops, and byte-parity array printing (Milestone M2-alpha).
//
// This file owns the VALUE-SPACE array machinery the M2 nest interpreter
// (Interp/Loops.fs) and the new Core.fs / Print.fs arms build on: it allocates
// and reshapes stores, reads/writes/peels cells, produces virtual-array element
// values, folds reductions, runs the eager set/reshape ops, and — critically —
// emits the SAME stdout the compiled C++ produces for a top-level array binding
// (CodeGen.genPrintArrayFlat / genPrintArraySymAware), byte-for-byte.
//
// SCOPE: M2-alpha = DENSE arrays (rank-1 flat / rank>=2 nested rows). The
// symmetric-compact, ragged, and compound storage classes are LATER milestones;
// their entry points raise `ArrayOpUnsupported` (see the CONTRACT NOTES header
// below for how the driver must classify that). canonFold IS fully implemented
// (pure, needed by the M2.5 symmetry read/write it precedes).
//
// ============================================================================
// CONTRACT NOTES (deviations from m2-design.md §1 — read before wiring)
// ============================================================================
//
//  (1) NO InterpState PARAMETER.  m2-design §1 threads `InterpState` through
//      indexArray / reduceArray / prodSum / maskArray / sortArray. InterpState
//      is defined in Interp/Core.fs, which compiles AFTER this file (ArrayOps.fs
//      sits after RandMirror.fs, before Core.fs). Referencing it here is
//      impossible without a forward dependency. It is also UNNECESSARY: every
//      one of those functions receives the interpreter-state-dependent work as a
//      CLOSURE (`fold`, `pred`, `key`) that the caller builds from its own
//      InterpState. The reduce/prodSum bodies need no state (they only iterate +
//      panic; the panic exn `InterpPanic` lives in Value.fs and IS available).
//      ⇒ ArrayOps is InterpState-free. Loops.fs / Core.fs adapt their call sites
//        (they already hold the state to build the closures).
//
//  (2) OWN "unsupported" EXCEPTION.  Core.InterpUnsupported and
//      Print.PrintUnsupported both live in files that compile AFTER this one, so
//      they cannot be raised here. This file raises `ArrayOpUnsupported msg` for
//      any not-yet-implemented storage class (symmetric-compact / ragged /
//      compound / rank-5+ nest, etc.).  ⇒ Run.fs MUST gain a catch arm:
//          | ArrayOps.ArrayOpUnsupported feature ->
//                { ExitCode = ExitUnsupported; Stdout = "";
//                  Stderr = sprintf "interp-unsupported: %s" feature }
//        placed alongside the existing Core.InterpUnsupported / Print.PrintUnsupported
//        arms (Run.fs:141-144), so the differential gate SKIP-classifies these
//        exactly as it does the other unsupported categories (ExitUnsupported=125).
//
//  (3) VirtualKind is defined HERE (not in the IR). It is the value-space
//      descriptor the nest reads for a range/reverse/blocked source.
//
// Compiled inside Blade.fsproj AFTER Interp/RandMirror.fs and BEFORE Interp/Core.fs.
// References Value.fs (value universe), CppFormat.fs (iostream-parity scalar
// formatters), Numerics.fs (bit-exact arithmetic), and the concrete IR/Types.
module Blade.Interp.ArrayOps

open System.Text
open System.Collections.Generic
open Blade.Types
open Blade.IR
open Blade.Interp.Value
open Blade.Interp.CppFormat

module N = Blade.Interp.Numerics

// ============================================================================
// Faults
// ============================================================================

/// Raised for an array storage class / print form not yet interpreted
/// (symmetric-compact, ragged, compound, rank-5+ nests, ...). The driver maps
/// this to the gate's ExitUnsupported (see CONTRACT NOTE (2)); it is the M2
/// analog of Core.InterpUnsupported for the array layer.
exception ArrayOpUnsupported of string

// ============================================================================
// Value <-> store-cell coercions
// ============================================================================
// Writing a kernel result / literal element into a typed unboxed store may need
// a widening (an Int64 kernel result stored into a Float64 output, etc.), just
// as C++ performs the implicit conversion at the assignment. These follow the
// same rules as Core.fs's private toI64/toF64 and Numerics' asF64/asI64.

let private toF64v (v: Value) : float =
    match v with
    | VFloat f -> f
    | VFloat32 f -> float f
    | VInt n -> float n
    | VInt32 n -> float n
    | VBool b -> if b then 1.0 else 0.0
    | VChar c -> float (int c)
    | VComplex (r, _) -> r
    | _ -> nan

let private toI64v (v: Value) : int64 =
    match v with
    | VInt n -> n
    | VInt32 n -> int64 n
    | VFloat f -> int64 f
    | VFloat32 f -> int64 (float f)
    | VBool b -> if b then 1L else 0L
    | VChar c -> int64 (int c)
    | _ -> 0L

let private toComplexv (v: Value) : float * float =
    match v with
    | VComplex (r, i) -> (r, i)
    | other -> (toF64v other, 0.0)

let private toBoolv (v: Value) : bool =
    match v with
    | VBool b -> b
    | VInt n -> n <> 0L
    | VInt32 n -> n <> 0
    | _ -> false

// ============================================================================
// Element-type projection
// ============================================================================

/// Project the primitive ElemType out of an element IRType, seeing through unit
/// annotations and nominal index-tag wrappers (Nat<I> = IRTIdxTagged(IRTScalar
/// ETInt64, _)). None for non-scalar element types (struct / func / nested array).
/// Mirrors Print.elemThrough.
let rec elemThrough (ty: IRType) : ElemType option =
    match ty with
    | IRTScalar et -> Some et
    | IRTUnitAnnotated (inner, _) -> elemThrough inner
    | IRTIdxTagged (inner, _) -> elemThrough inner
    | _ -> None

// ============================================================================
// §1 Storage: allocate / reshape flat backing stores
// ============================================================================
//
// STORAGE MODEL (m2-design §7): a dense BladeArray is a rank-1 FLAT unboxed
// store, or a rank>=2 SNested tree whose leaves are flat rows (row-major). A
// peel (peelDim) shares — does not copy — the parent's SNested row, so mutation
// through the peel is visible in the parent (the C++ `{data[i], extents+1}`
// view). Narrow element types WIDEN per Value.fs's documented gap: Float32→
// SFloat, Int32→SInt, Complex64→SComplex; String/struct/tuple/func → SObj.

let private storeLen (s: Store) : int =
    match s with
    | SFloat a -> a.Length
    | SInt a -> a.Length
    | SComplex a -> a.Length
    | SBool a -> a.Length
    | SObj a -> a.Length
    | SNested r -> r.Length
    | SRagged (r, _, _) -> r.Length

/// Copy a contiguous slice out of a FLAT store (used to partition a flat backing
/// into rows when reshaping). Nested/ragged stores are never sliced this way.
let private sliceStore (s: Store) (start: int) (len: int) : Store =
    match s with
    | SFloat a -> SFloat (Array.sub a start len)
    | SInt a -> SInt (Array.sub a start len)
    | SComplex a -> SComplex (Array.sub a start len)
    | SBool a -> SBool (Array.sub a start len)
    | SObj a -> SObj (Array.sub a start len)
    | SNested _ | SRagged _ -> raise (ArrayOpUnsupported "sliceStore: expected a flat store")

let rec private deepCopyStore (s: Store) : Store =
    match s with
    | SFloat a -> SFloat (Array.copy a)
    | SInt a -> SInt (Array.copy a)
    | SComplex a -> SComplex (Array.copy a)
    | SBool a -> SBool (Array.copy a)
    | SObj a -> SObj (Array.copy a)
    | SNested rows -> SNested (rows |> Array.map deepCopyStore)
    | SRagged (rows, lens, offs) -> SRagged (rows |> Array.map deepCopyStore, Array.copy lens, Array.copy offs)

/// Allocate a zeroed FLAT store of `n` cells for an element type. Widens narrow
/// element types (Value.fs gap); non-scalar elements get an SObj of VUnit
/// placeholders (each overwritten by writeCell during materialization).
let storeOfElemType (elemTy: IRType) (n: int) : Store =
    match elemThrough elemTy with
    | Some (ETFloat64 | ETFloat32) -> SFloat (Array.zeroCreate n)
    | Some (ETInt64 | ETInt32) -> SInt (Array.zeroCreate n)
    | Some (ETComplex64 | ETComplex128) -> SComplex (Array.zeroCreate n)
    | Some ETBool -> SBool (Array.zeroCreate n)
    | Some ETString -> SObj (Array.create n (VString ""))
    | Some ETUnit -> SObj (Array.create n VUnit)
    | _ -> SObj (Array.create n VUnit)

/// Pack a row-major flat array of leaf Values into an unboxed store for the
/// given element type (mirrors storeOfElemType's widening).
let storeOfValues (elemTy: IRType) (vs: Value[]) : Store =
    match elemThrough elemTy with
    | Some (ETFloat64 | ETFloat32) -> SFloat (vs |> Array.map toF64v)
    | Some (ETInt64 | ETInt32) -> SInt (vs |> Array.map toI64v)
    | Some (ETComplex64 | ETComplex128) ->
        SComplex (vs |> Array.map (fun v -> let (r, i) = toComplexv v in struct (r, i)))
    | Some ETBool -> SBool (vs |> Array.map toBoolv)
    | _ -> SObj (Array.copy vs)

/// Reshape a FLAT row-major store (length = product of extents) into the nested
/// row structure for `extents.[dim..]`. Rank-1 (innermost) returns the flat leaf.
let rec private reshapeFlat (extents: int64[]) (dim: int) (flat: Store) : Store =
    if dim >= extents.Length - 1 then
        flat
    else
        let outer = int extents.[dim]
        let innerLen =
            [ for d in dim + 1 .. extents.Length - 1 -> int extents.[d] ]
            |> List.fold (*) 1
        SNested (Array.init outer (fun r -> reshapeFlat extents (dim + 1) (sliceStore flat (r * innerLen) innerLen)))

/// Wrap a FLAT row-major store as a rank-N dense BladeArray: rank<=1 keeps the
/// flat store; rank>=2 reshapes into SNested rows (m2-design §1).
let mkDenseArray (elemTy: IRType) (indexTypes: IRIndexType list) (extents: int64[]) (flat: Store) : BladeArray =
    let data = if extents.Length <= 1 then flat else reshapeFlat extents 0 flat
    { ElemType = elemTy; IndexTypes = indexTypes; Extents = extents; Data = data }

/// Convenience allocator: a zeroed dense BladeArray of the given shape (the nest
/// output-allocation path fills it via writeCell). Not in the §1 contract; added
/// so Loops.fs can allocate without hand-building the flat store.
let allocDense (elemTy: IRType) (indexTypes: IRIndexType list) (extents: int64[]) : BladeArray =
    let total = extents |> Array.fold (fun acc e -> acc * int e) 1
    mkDenseArray elemTy indexTypes extents (storeOfElemType elemTy total)

// ============================================================================
// §2 Access: read / write / peel + shape accessors
// ============================================================================

/// Rank (number of dense loop levels) of an array.
let rank (arr: BladeArray) : int = arr.Extents.Length

/// Extent of a dimension (0 for an out-of-range dim).
let extent (arr: BladeArray) (dim: int) : int64 =
    if dim >= 0 && dim < arr.Extents.Length then arr.Extents.[dim] else 0L

/// Read the scalar leaf at a FULL coordinate path (absolute dense row-major
/// coords). For partial (sub-array) reads use peelDim / indexArray instead.
let readCell (arr: BladeArray) (coords: int64 list) : Value =
    let rec go (store: Store) (cs: int64 list) : Value =
        match cs, store with
        | [ i ], SFloat a -> VFloat a.[int i]
        | [ i ], SInt a -> VInt a.[int i]
        | [ i ], SComplex a -> let struct (r, im) = a.[int i] in VComplex (r, im)
        | [ i ], SBool a -> VBool a.[int i]
        | [ i ], SObj a -> a.[int i]
        | i :: rest, SNested rows -> go rows.[int i] rest
        | i :: rest, SRagged (rows, _, _) -> go rows.[int i] rest
        | _ -> raise (InterpPanic ("BL8003", "array read: coordinate/shape mismatch", None, 0))
    go arr.Data coords

/// Write `v` (coerced to the store's cell type) at a FULL coordinate path.
/// Materialization only — grows nothing; the cell must already exist.
let writeCell (arr: BladeArray) (coords: int64 list) (v: Value) : unit =
    let rec go (store: Store) (cs: int64 list) : unit =
        match cs, store with
        | [ i ], SFloat a -> a.[int i] <- toF64v v
        | [ i ], SInt a -> a.[int i] <- toI64v v
        | [ i ], SComplex a -> let (r, im) = toComplexv v in a.[int i] <- struct (r, im)
        | [ i ], SBool a -> a.[int i] <- toBoolv v
        | [ i ], SObj a -> a.[int i] <- v
        | i :: rest, SNested rows -> go rows.[int i] rest
        | i :: rest, SRagged (rows, _, _) -> go rows.[int i] rest
        | _ -> raise (InterpPanic ("BL8003", "array write: coordinate/shape mismatch", None, 0))
    go arr.Data coords

/// Peel ONE (outermost) dimension at index i. rank-1 (or rank-0) yields the
/// scalar leaf; rank>=2 yields a sub-array VIEW whose Store ALIASES the parent's
/// SNested row (mutation through the peel is visible in the parent — the C++
/// `{ data[i], extents+1 }` view, m2-design §7).
let peelDim (arr: BladeArray) (i: int64) : Value =
    if arr.Extents.Length <= 1 then
        readCell arr [ i ]
    else
        let childIdx = match arr.IndexTypes with _ :: t -> t | [] -> []
        let childExt = arr.Extents.[1..]
        match arr.Data with
        | SNested rows ->
            VArray { ElemType = arr.ElemType; IndexTypes = childIdx; Extents = childExt; Data = rows.[int i] }
        | SRagged (rows, lens, _) ->
            // A peeled ragged row: its length is per-row (lens[i]), NOT the
            // parent's placeholder inner extent. The row becomes an ordinary FLAT
            // rank-1 array (its own leaf store) so every downstream dense op —
            // index, extents(row), reduce, print — works unchanged and in-bounds.
            let rlen = if int i < lens.Length then lens.[int i] else int64 (storeLen rows.[int i])
            VArray { ElemType = arr.ElemType; IndexTypes = childIdx; Extents = [| rlen |]; Data = rows.[int i] }
        | _ ->
            // A rank>=2 array whose backing is unexpectedly flat: fall back to a
            // scalar read (keeps the interpreter total; should not occur for
            // arrays built through mkDenseArray).
            readCell arr [ i ]

/// Dimensional curry: `arr[idx]` — peel the first dim, yielding a sub-array
/// (rank>=2) or scalar (rank-1). Identical to peelDim (IRCurry, m2-design §6).
let curryArray (arr: BladeArray) (i: int64) : Value = peelDim arr i

// ============================================================================
// §1 Symmetry-aware canonicalization (canonFold) — PURE, complete
// ============================================================================
// Used by the M2.5 compact-symmetric read/write it precedes. Returns the sorted
// (left-justified) storage coordinates, the swap PARITY (0 even / 1 odd — the
// read transform applies negate/conjugate on odd parity), and whether the tuple
// hits a STRICT diagonal (antisymmetric repeated index ⇒ the element is zero).

let private sortWithParity (coords: int64[]) : int64[] * int =
    let a = Array.copy coords
    let mutable swaps = 0
    for i in 1 .. a.Length - 1 do
        let mutable j = i
        while j > 0 && a.[j - 1] > a.[j] do
            let t = a.[j - 1] in a.[j - 1] <- a.[j]
            a.[j] <- t
            swaps <- swaps + 1
            j <- j - 1
    (a, swaps % 2)

/// Canonicalize an index tuple over a compact symmetry group.
///   SymNone       → (coords, 0, false)  (no canonicalization)
///   SymSymmetric  → (sorted, parity, false)
///   SymHermitian  → (sorted, parity, false)  (parity drives conjugate-on-swap)
///   SymAntisym    → (sorted, parity, isZero) (isZero when any index repeats)
///   SymWreath    → REFUSED. §5's canonicalization for a wreath class is a FOLD
///                  OF PER-LEVEL SORTS (innermost first), not one flat sort:
///                  sorting all prod(ri) coordinates together maps distinct
///                  orbits onto the same canonical tuple and computes a
///                  meaningless parity. The reference implementation is
///                  OrbRank.canonOrb, which also needs the level list this
///                  signature does not carry. Refusing keeps the failure loud;
///                  the alternative is a plausible tuple read from the wrong
///                  cell.
let canonFold (sym: SymmetryClass) (coords: int64[]) : int64[] * int * bool =
    match sym with
    | SymNone -> (Array.copy coords, 0, false)
    | SymWreath ->
        failwith (Blade.IR.orbitStorageUnsupported "compact read (canonFold)" [])
    | SymSymmetric
    | SymHermitian ->
        let sorted, parity = sortWithParity coords
        (sorted, parity, false)
    | SymAntisymmetric ->
        let hasRepeat = (coords |> Array.distinct).Length <> coords.Length
        let sorted, parity = sortWithParity coords
        (sorted, parity, hasRepeat)

/// Left-justify a sorted index tuple to compact STORAGE coords (mirrors
/// nested_array_utilities::canon_left_justify, cpp:564): c[0]=p[0];
/// c[k] = p[k] - p[k-1] - (strict ? 1 : 0). `strict` is true for an
/// antisymmetric group (each successive row one shorter — the dropped diagonal).
let canonLeftJustify (sorted: int64[]) (strict: bool) : int64[] =
    let r = sorted.Length
    let c = Array.zeroCreate r
    if r > 0 then c.[0] <- sorted.[0]
    for k in 1 .. r - 1 do
        c.[k] <- sorted.[k] - sorted.[k - 1] - (if strict then 1L else 0L)
    c

// ============================================================================
// §1b Compact (symmetric / antisymmetric / Hermitian) OUTPUT allocation
// ============================================================================
// A compact BladeArray keeps the LOGICAL Extents (so reads/prints know the true
// shape) + the output IndexTypes (so the symmetric-aware printer and canonical
// reader see the compact structure); only Data is a left-justified nested
// SKELETON whose rows shrink within each symmetry group. This mirrors
// nested_array_utilities::allocate/build_skeleton (cpp:185-244) row-length for
// row-length: at flattened depth d inside a group (symmVec[d-1]=symmVec[d]) a row
// holds extents[d]-lastIndex cells, where lastIndex threads the parent index
// (+strict seed for antisym). The nest writes at the raw left-justified loop
// coords (interpretNest's storage coords), so writeCell navigates it directly;
// the sym printer reads the same raw coords (canonical by construction).

/// symmVec/strictVec come from IR.buildSymmVecWithStrict on the OUTPUT type:
///   symmVec[d]   = storage group number at flattened dim d (adjacent-equal =
///                  one shrinking group),  strictVec[d] = 1 if antisymmetric.
let allocCompact (elemTy: IRType) (idxTys: IRIndexType list) (extents: int64[])
                 (symmVec: int[]) (strictVec: int[]) : BladeArray =
    let rank = extents.Length
    let rec build (depth: int) (lastIndex: int64) : Store =
        let inGroupWithPrev = depth > 0 && symmVec.[depth - 1] = symmVec.[depth]
        let n =
            let raw = if inGroupWithPrev then extents.[depth] - lastIndex else extents.[depth]
            if raw < 0L then 0L else raw
        if depth >= rank - 1 then
            storeOfElemType elemTy (int n)
        else
            let nextInGroup = symmVec.[depth] = symmVec.[depth + 1]
            let strictOff = int64 strictVec.[depth]
            SNested (Array.init (int n) (fun i ->
                let childLast =
                    if nextInGroup then
                        if inGroupWithPrev then int64 i + lastIndex + strictOff
                        else int64 i + strictOff
                    else 0L
                build (depth + 1) childLast))
    let data =
        if rank = 0 then storeOfElemType elemTy 1
        elif rank = 1 then storeOfElemType elemTy (int extents.[0])
        else build 0 0L
    { ElemType = elemTy; IndexTypes = idxTys; Extents = extents; Data = data }

// ----------------------------------------------------------------------------
// OrbIdx (iterated-wreath) pools — docs/plan-orbit-index-types.md §4, §9 step 4
// ----------------------------------------------------------------------------
//
// A wreath array is a FLAT pool of exactly `OrbRank.cellCountChecked levels n`
// cells in `visitStream` order (== the C++ `orb_visit` order == ascending-lex
// canonical), which is the plan's one hard invariant. Deliberately NOT the
// shrinking-row SNested skeleton `allocCompact` builds: a wreath's rows shrink
// per LEVEL, so no single simplex describes them, and a skeleton shaped like one
// would put every cell at a plausible-but-wrong offset.
//
// The record keeps its honest RAW-AXIS Extents (prod(ri) copies of n) so that a
// consumer asking for the logical shape gets the truth; only the Data layout is
// flat. Every path that would navigate `Extents` as a nested store is refused
// (forEachStorageCell / emitSymAware); the two READ doors (indexArray and
// readCompact) go through the flat §2 read instead.

/// Zero value for a scalar element type (implicit-zero of a strict-diagonal
/// antisymmetric access, and of a wreath zero-set tuple; mirrors `return T()`
/// in the lazy compact reader). Defined here rather than beside the other
/// compact-read helpers below because the wreath read needs it first.
let zeroOfElemTy (elemTy: IRType) : Value =
    match elemThrough elemTy with
    | Some (ETFloat64 | ETFloat32) -> VFloat 0.0
    | Some (ETInt64 | ETInt32) -> VInt 0L
    | Some (ETComplex64 | ETComplex128) -> VComplex (0.0, 0.0)
    | Some ETBool -> VBool false
    | _ -> VFloat 0.0

/// True iff any slot of this record list is a depth >= 2 OrbIdx class.
let hasWreath (idxTys: IRIndexType list) : bool =
    idxTys |> List.exists (fun ix -> ix.Symmetry = SymWreath)

/// Allocate a zeroed wreath pool: `cellCountChecked` cells, flat, in stream
/// order. The count comes from the SAME checked fold `IR.classifyOutputStorage`
/// sized the compiled pool with, so the two backends cannot allocate differently
/// (§7.2: the failure to guard is a silent wraparound, and a second independent
/// computation is how you get one).
let allocWreath (elemTy: IRType) (idxTys: IRIndexType list)
                (levels: (int * bool) list) (n: int64) : BladeArray =
    let cells =
        match Blade.OrbRank.cellCountChecked (Blade.IR.orbRankLevels levels) n with
        | Ok c -> c
        | Error detail ->
            raise (ArrayOpUnsupported
                     (sprintf "OrbIdx%s at extent %d: cell count -- %s"
                              (Blade.IR.ppOrbitLevels levels) n detail))
    let axes = levels |> List.fold (fun a (r, _) -> a * r) 1
    { ElemType = elemTy
      IndexTypes = idxTys
      Extents = Array.create axes n
      Data = storeOfElemType elemTy (int cells) }

/// Number of stored cells of a wreath pool (its flat store length).
let wreathCellCount (arr: BladeArray) : int = storeLen arr.Data

/// Read the pool cell at a CANONICAL tuple, by its `orbRank` position. This is
/// the read the traversal nest performs on a wreath INPUT (the depth >= 3
/// shape): the tuple comes straight out of `visitStream`, so it is canonical by
/// construction and carries character +1. It deliberately does NOT fold: a
/// canonical tuple is a fixed point, so folding would be dead work on the hot
/// path, and going through the fold here would hide a stream/rank disagreement.
/// The MIRRORED read is `wreathReadAny` below.
let wreathReadCanonical (arr: BladeArray) (levels: (int * bool) list) (n: int64)
                        (tuple: int list) : Value =
    match Blade.OrbRank.orbRank (Blade.IR.orbRankLevels levels) (int n) tuple with
    | Error detail ->
        raise (ArrayOpUnsupported
                 (sprintf "OrbIdx%s canonical read at (%s): %s"
                          (Blade.IR.ppOrbitLevels levels)
                          (tuple |> List.map string |> String.concat ",") detail))
    | Ok r -> readCell arr [ r ]

/// docs/plan-orbidx-decompaction.md §2's read at an ARBITRARY raw tuple:
///
///     dense[t] = 0                                if canon(t) is zero-set
///              = chi(t) * pool[orbRank(canon(t))]  otherwise
///
/// The interpreter twin of the emitted `orb_read<T, Levels...>`, and it does
/// not re-derive the semantics: `OrbRank.orbReadPlan` -- the same core the
/// reference `OrbRank.orbRead` is built on, pinned against held-out tables by
/// `blade test orbrank` -- answers WHICH cell and WHICH character, and the only
/// thing added here is the cell type (a `Value` negate instead of an int64 one,
/// which is exactly why the plan is factored out rather than orbRead called).
///
/// THE TWO FAILURE MODES STAY APART, as the header's DOMAIN CONTRACT insists:
///   * ZERO SET -> the element type's zero. A VALUE. A '-' level genuinely
///     stores nothing there and the dense tensor genuinely holds 0.
///   * OUT OF DOMAIN (digit outside [0,n), wrong axis rank, wrong pool size,
///     malformed class) -> BL8003. Answering it with 0 would alias an
///     off-by-one onto a structural zero, which is the one confusion this whole
///     path is shaped to avoid.
/// Every out-of-domain case here is unreachable for a tuple the typechecker
/// admitted EXCEPT an out-of-range coordinate, which is the same unchecked
/// hazard a SymIdx subscript has (readCell's raw store access); the difference
/// is that this one diagnoses instead of reading a neighbouring cell.
let wreathReadAny (arr: BladeArray) (levels: (int * bool) list) (n: int64)
                  (tuple: int list) : Value =
    match Blade.OrbRank.orbReadPlan "OrbIdx read" (Blade.IR.orbRankLevels levels)
                                    (int n) (storeLen arr.Data) tuple with
    | Error detail ->
        raise (InterpPanic ("BL8003",
                            sprintf "OrbIdx%s read at (%s): %s"
                                    (Blade.IR.ppOrbitLevels levels)
                                    (tuple |> List.map string |> String.concat ",") detail,
                            None, 0))
    | Ok Blade.OrbRank.OrbZeroCell -> zeroOfElemTy arr.ElemType
    | Ok (Blade.OrbRank.OrbPoolCell (i, chi)) ->
        let v = readCell arr [ int64 i ]
        if chi < 0 then N.evalUnaryOp IRNeg v else v

/// Write the pool cell at stream position `k` (the traversal nest's own
/// counter). Position-addressed rather than tuple-addressed precisely because
/// the nest already knows the position: `visitStream` yields cells in order, so
/// the writer bumps a counter exactly as the C++ visitor's `linear_index` does.
let wreathWriteAt (arr: BladeArray) (k: int64) (v: Value) : unit =
    writeCell arr [ k ] v

// ============================================================================
// §2 General indexing (IRIndex, plain dense) + poly-index
// ============================================================================

let private hasSymmetry (idxTys: IRIndexType list) : bool =
    idxTys
    |> List.exists (fun idx ->
        idx.Symmetry = SymSymmetric
        || idx.Symmetry = SymAntisymmetric
        || idx.Symmetry = SymHermitian)

/// Apply a compact read transform at the given swap parity (mirrors
/// nested_array_utilities::canon_transform + ReadTransform, cpp:573):
///   Symmetric  -> Identity;   Antisymmetric -> NegateOnSwap;
///   Hermitian  -> ConjugateOnSwap (identity on reals — conj_scalar).
let private applyReadTransform (sym: SymmetryClass) (parity: int) (v: Value) : Value =
    if parity = 0 then v
    else
        match sym with
        | SymAntisymmetric -> N.evalUnaryOp IRNeg v
        | SymHermitian -> N.evalUnaryOp IRConj v
        | _ -> v

/// Canonical (compact) random read: given a FULL logical coordinate list covering
/// every dimension, fold each compact group (canon_fold: sort + swap parity +
/// strict-diagonal zero-guard), left-justify to storage coords, read the raw
/// stored cell, and chain the per-group read transforms. Mirrors
/// CodeGen.renderIndexExpr's lazyCompactRead (CodeGen.fs:1463-1538) +
/// nested_array_utilities. Plain (SymNone) slots pass their index through.
let readCompact (arr: BladeArray) (logicalCoords: int64 list) : Value =
    // A SOLE wreath slot is the §2 read, delegated whole -- the per-slot fold
    // below cannot express it (canonFold is ONE flat sort with a strict flag,
    // and a wreath pool is flat, not a nest of left-justified rows). Routing it
    // here rather than at every caller is what makes `decompactArray` work over
    // a wreath source with no change: it walks the DENSE output's cells and
    // reads the source at each logical tuple, which is exactly this.
    //
    // A wreath COMBINED with other slots still refuses: the mixed case has no
    // pool layout at all (classifyOutputStorage refuses to allocate one), so
    // there is nothing to read from and the storage message is the honest one.
    match arr.IndexTypes with
    | [ ix ] when ix.Symmetry = SymWreath ->
        let n = if arr.Extents.Length >= 1 then arr.Extents.[0] else 0L
        // int64 -> int is the ONE narrowing on this path (coordinates are `int`
        // through the whole artifact layer, F# and C++ alike). Range-check
        // BEFORE truncating, not after: 2^32 + 1 truncates to 1, which the
        // storage gate would then accept as a perfectly good coordinate and
        // serve a cell from -- the silent aliased read that gate exists to
        // prevent, sneaking in one conversion upstream of it.
        (match logicalCoords |> List.tryFind (fun c -> c < 0L || c >= n) with
         | Some bad ->
             raise (InterpPanic ("BL8003",
                                 sprintf "OrbIdx%s read: coordinate %d outside [0,%d)"
                                         (Blade.IR.ppOrbitLevels (Blade.IR.orbitLevelsOf ix)) bad n,
                                 None, 0))
         | None ->
             wreathReadAny arr (Blade.IR.orbitLevelsOf ix) n
                           (logicalCoords |> List.map int))
    | _ ->
    arr.IndexTypes |> List.iter (fun ix ->
        if ix.Symmetry = SymWreath then
            failwith (Blade.IR.orbitStorageUnsupported "compact read of a wreath group combined with \
other index slots (readCompact)"
                                                       (Blade.IR.orbitLevelsOf ix)))
    let coords = Array.ofList logicalCoords
    let storage = ResizeArray<int64>()
    let mutable transforms = []          // (parity, sym) per compact group, slot order
    let mutable isZero = false
    let mutable cursor = 0
    for ix in arr.IndexTypes do
        let a = max 1 ix.Rank
        let these = Array.sub coords cursor a
        cursor <- cursor + a
        if ix.Symmetry <> SymNone && a >= 2 then
            let (sorted, parity, z) = canonFold ix.Symmetry these
            if z then isZero <- true
            let strict = ix.Symmetry = SymAntisymmetric
            for c in canonLeftJustify sorted strict do storage.Add c
            transforms <- transforms @ [ (parity, ix.Symmetry) ]
        else
            for c in these do storage.Add c
    if isZero then zeroOfElemTy arr.ElemType
    else
        let raw = readCell arr (List.ofSeq storage)
        transforms |> List.fold (fun v (p, sym) -> applyReadTransform sym p v) raw

/// Plain dense random read through an index list: chained peels (row-major).
/// A full index list yields a scalar; a partial list yields a sub-array view.
/// Out-of-range indices panic BL8003 (matching blade_rt on the abort probes).
/// A compact (symmetric/antisym/Hermitian) array with a FULL index list routes
/// to the canonical reader (readCompact); a partial (sub-array) compact read is
/// still gated (M3+).
let indexArray (arr: BladeArray) (indices: Value list) : Value =
    // A wreath pool answers TRUE to "is this compact", but its store is FLAT and
    // `hasSymmetry` (Sym/Antisym/Herm only) is FALSE for it -- so without this
    // arm the dense peel below would walk `Extents` as if the store were nested
    // and read a plausible cell from the wrong place, or panic with a shape
    // message that names nothing. Subscripting a wreath array at an arbitrary
    // tuple IS the mirrored read, and it is now implemented: route the
    // FULL-ARITY form to §2 (canonOrb fold + character + rank), which is the
    // twin of the emitted `orb_read`. A PARTIAL list has no answer -- a wreath
    // pool has no sub-array views (its rows shrink per level, so no residual
    // class describes a fibre) -- and the typechecker refuses it before here;
    // this is the backstop, and it deliberately matches the shape of the
    // compact-partial refusal one arm down rather than inventing a view.
    if hasWreath arr.IndexTypes then
        let ix = arr.IndexTypes |> List.find (fun ix -> ix.Symmetry = SymWreath)
        let totalRank = arr.IndexTypes |> List.sumBy (fun ix -> max 1 ix.Rank)
        if indices.Length = totalRank then
            readCompact arr (indices |> List.map toI64v)
        else
            raise (ArrayOpUnsupported
                     (sprintf "index: OrbIdx%s spans %d raw axes and takes exactly that many flat \
subscripts; a partial (sub-array) read of a wreath pool has no residual class"
                              (Blade.IR.ppOrbitLevels (Blade.IR.orbitLevelsOf ix)) totalRank))
    elif hasSymmetry arr.IndexTypes then
        let totalRank = arr.IndexTypes |> List.sumBy (fun ix -> max 1 ix.Rank)
        if indices.Length = totalRank then
            readCompact arr (indices |> List.map toI64v)
        else
            raise (ArrayOpUnsupported "index: partial (sub-array) read of a compact symmetric array (M3+)")
    else
    let rec go (cur: Value) (idxs: Value list) : Value =
        match idxs with
        | [] -> cur
        | iv :: rest ->
            match cur with
            | VArray a ->
                let i = toI64v iv
                if i < 0L || a.Extents.Length < 1 || i >= a.Extents.[0] then
                    raise (InterpPanic ("BL8003", "array index out of bounds", None, 0))
                go (peelDim a i) rest
            | _ -> raise (InterpPanic ("BL8003", "indexing a non-array value", None, 0))
    go (VArray arr) indices

/// Poly-pack indexing (IRPolyIndex): a tuple pack → get<i>; an array pack → peel.
let polyIndex (pack: Value) (i: int64) : Value =
    match pack with
    | VTuple els when i >= 0L && int i < els.Length -> els.[int i]
    | VArray a -> peelDim a i
    | _ -> raise (ArrayOpUnsupported "IRPolyIndex: pack is neither a tuple nor an array")

// ============================================================================
// §3 Virtual arrays
// ============================================================================

/// Value-space descriptor for a no-store virtual source (m2-design §0.10, §3).
type VirtualKind =
    /// range<I> (+offset): element at loop index i is `i + offset`. offset 0 for
    /// a plain `0..N`. int64 throughout so `i - 1` at i=0 is -1, not an unsigned
    /// wrap (the signedness fix, CodeGen.fs:2958-2964).
    | VRange of offset: int64
    /// reverse<I>: element at loop index i is `extent - 1 - i`.
    | VReverse
    /// blocked<I>: blocked single-index iteration; the produced element VALUE is
    /// still the flat index i (blocking reorders iteration, not the value).
    /// NOTE: best-effort — no compiled-binary pin yet (blocked lands in the mpi
    /// domain-decomposition slice, outside the dense M2 corpus). FLAG.
    | VBlocked of blockSize: int64

/// The element value a virtual source produces at loop index i (given the level
/// extent for the reverse arm). Always Int64-typed, matching the C++ int64_t
/// kernel-param binding.
let virtualElem (vk: VirtualKind) (extent: int64) (i: int64) : Value =
    match vk with
    | VRange off -> VInt (i + off)
    | VReverse -> VInt (extent - 1L - i)
    | VBlocked _ -> VInt i

// ============================================================================
// §4 Array-literal construction (IRArrayLit, incl. nested rank>=2)
// ============================================================================
//
// The Core.fs IRArrayLit arm evaluates the literal's TOP-LEVEL element exprs to
// Values, then calls here. For a rank-1 literal those Values are scalar leaves
// (packed into a flat store); for rank>=2 each element is itself a VArray row
// (also covers "rows of computed arrays": elements that are array-VALUED exprs,
// CodeGen.fs:4254). The outer extent is the element count; inner extents come
// from the first row's shape (rectangular). Ragged / DepIdx literals are M2.7.

let arrayLitFromValues (arrType: IRArrayType) (elems: Value list) : BladeArray =
    let elemTy = arrType.ElemType
    let idxTys = arrType.IndexTypes
    let isRagged =
        Blade.CodeGen.isRaggedArrayType arrType || Blade.CodeGen.isDepIdxArrayType arrType
    match elems with
    | (VArray _) :: _ when isRagged ->
        // Ragged / DepIdx literal (heterogeneous per-row lengths). CSR layout
        // mirroring CodeGen.genArrayLiteral's Ragged<T> emission (CodeGen.fs:
        // 4485-4522): each row kept as its OWN leaf store; `lens` = the actual
        // per-row lengths (NOT a uniform inner extent taken from the first row —
        // that rectangular assumption is exactly the bug that made r(2,3) read
        // past a short row, BL8003); `offsets` = exclusive prefix-sum, length
        // nRows+1. rank = 2 (outer Idx + the ONE ragged inner record); the
        // logical Extents' inner slot is the max row length (rank/`extents(r)`
        // fidelity only — every per-row bound is served from `lens` by peelDim).
        let rows =
            elems
            |> List.map (function
                | VArray a -> a.Data
                | v -> raise (ArrayOpUnsupported (sprintf "ragged literal: non-array row (%A)" v)))
            |> Array.ofList
        let lens =
            elems
            |> List.map (function
                | VArray a -> (if a.Extents.Length >= 1 then a.Extents.[0] else 0L)
                | _ -> 0L)
            |> Array.ofList
        let offsets = Array.zeroCreate (rows.Length + 1)
        for r in 0 .. rows.Length - 1 do offsets.[r + 1] <- offsets.[r] + lens.[r]
        let innerExtent = if lens.Length = 0 then 0L else Array.max lens
        { ElemType = elemTy
          IndexTypes = idxTys
          Extents = [| int64 rows.Length; innerExtent |]
          Data = SRagged (rows, lens, offsets) }
    | (VArray _) :: _ when
            idxTys |> List.exists (fun ix ->
                ix.Rank >= 2 &&
                (match ix.Symmetry with
                 | SymSymmetric | SymAntisymmetric | SymHermitian -> true
                 | SymNone | SymWreath -> false)) ->
        // COMPACT literal (TypeCheck.checkCompactArrayLit; CodeGen's compact
        // branch of genArrayLiteral): the rows ARE the left-justified simplex,
        // so they shrink, and the FIRST one is not the shape of the axis —
        // extents come from the index records (the component extents every
        // compact read folds against). Taking them from the first row, as the
        // rectangular arm below does, is off by one for a strict (antisym)
        // group, whose first row is n-1 wide.
        let rows =
            elems
            |> List.map (function
                | VArray a -> a.Data
                | v -> raise (ArrayOpUnsupported (sprintf "compact literal: non-array row (%A)" v)))
            |> Array.ofList
        let extents =
            idxTys
            |> List.collect (fun ix ->
                let e =
                    match ix.Extent with
                    | IRLit (IRLitInt n) -> n
                    | _ -> raise (ArrayOpUnsupported "compact literal: non-static extent")
                List.replicate (max 1 ix.Rank) e)
            |> Array.ofList
        { ElemType = elemTy; IndexTypes = idxTys; Extents = extents; Data = SNested rows }
    | (VArray first) :: _ ->
        // rank>=2: each element is a row; nest the rows' stores (shared — the
        // rows are freshly-evaluated and owned by this literal).
        let rows =
            elems
            |> List.map (function
                | VArray a -> a.Data
                | v -> raise (ArrayOpUnsupported (sprintf "array literal: mixed row/scalar elements (%A)" v)))
            |> Array.ofList
        let extents = Array.append [| int64 rows.Length |] first.Extents
        { ElemType = elemTy; IndexTypes = idxTys; Extents = extents; Data = SNested rows }
    | _ ->
        // rank-1 (or empty): scalar leaves packed flat.
        let vs = Array.ofList elems
        { ElemType = elemTy
          IndexTypes = idxTys
          Extents = [| int64 vs.Length |]
          Data = storeOfValues elemTy vs }

/// IRReplicate(count, body): a rank-added array of `count` copies of body.
/// Rows are deep-copied so the copies don't alias. (replicate/001.)
let replicateArray (count: int64) (body: Value) : Value =
    let c = int count
    match body with
    | VArray a ->
        let rows = Array.init c (fun _ -> deepCopyStore a.Data)
        VArray
            { ElemType = a.ElemType
              IndexTypes = a.IndexTypes
              Extents = Array.append [| count |] a.Extents
              Data = SNested rows }
    | scalar ->
        match N.scalarElem scalar with
        | Some et ->
            let elemTy = IRTScalar et
            VArray
                { ElemType = elemTy
                  IndexTypes = []
                  Extents = [| count |]
                  Data = storeOfValues elemTy (Array.create c scalar) }
        | None -> raise (ArrayOpUnsupported "replicate: body is neither a scalar nor an array")

/// `A <|:> B` dense-left allocated-fallback (fallback_copy<T,N>, nested_array_
/// utilities.hpp:106; genFallbackMaterialize dense arm, CodeGen.fs:9410). The
/// per-curry-level rule is `dst[i..] = a ? a[i..] : b[i..]`, keyed on A's
/// ALLOCATION (non-null pointer chain). Every in-language Blade array is FULLY
/// allocated, and the interpreter's value space has no partial-depth null-pointer
/// notion, so the mask is all-present ⇒ the result is a copy of A with its
/// allocated zeros preserved (`A <|:> B ≡ A` — the distinguisher from value-keyed
/// `<|>`, which would replace those zeros with B). B is unused. copyBladeArray
/// gives a fresh backing pool (fallback always yields a freshly-allocated array).
let fallbackDense (a: BladeArray) : BladeArray = copyBladeArray a

// ============================================================================
// §5 Reductions
// ============================================================================

/// Row-major flat leaves of an array (rank-1: its elements).
let private flatLeaves (arr: BladeArray) : Value[] =
    let out = ResizeArray<Value>()
    let rec loop (dim: int) (acc: int64 list) =
        if dim = arr.Extents.Length then out.Add(readCell arr (List.rev acc))
        else
            let e = arr.Extents.[dim]
            let mutable i = 0L
            while i < e do
                loop (dim + 1) (i :: acc)
                i <- i + 1L
    loop 0 []
    out.ToArray()

/// reduce(arr, fold[, init]) over a MATERIALIZED array (genReduceBinding parity,
/// m2-design §5). Without init: seed = arr[0], fold i=1..n-1; EMPTY ⇒ panic
/// BL8003 "reduce: empty array, no reduction possible" (matches blade_rt). With
/// init: seed = init, fold ALL i from 0; empty result IS init (never panics).
let reduceArray (arr: BladeArray) (fold: Value -> Value -> Value) (init: Value option) : Value =
    let leaves = flatLeaves arr
    match init with
    | Some seed ->
        let mutable acc = seed
        for v in leaves do
            acc <- fold acc v
        acc
    | None ->
        if leaves.Length = 0 then
            raise (InterpPanic ("BL8003", "reduce: empty array, no reduction possible", None, 0))
        let mutable acc = leaves.[0]
        for k in 1 .. leaves.Length - 1 do
            acc <- fold acc leaves.[k]
        acc

/// prodsum(x1..xk) = Σ_t Π_ℓ xℓ[t] over rank-1 equal-extent arrays; seed 0;
/// empty extent ⇒ 0 (IRProdSum, m2-design §0.8). Uses Numerics for bit-exact
/// promotion; the seed's type follows the first arg's element type.
let prodSum (args: BladeArray list) : Value =
    match args with
    | [] -> VInt 0L
    | first :: _ ->
        let n = if first.Extents.Length >= 1 then int first.Extents.[0] else 0
        let zero =
            match elemThrough first.ElemType with
            | Some (ETFloat64 | ETFloat32) -> VFloat 0.0
            | Some (ETComplex64 | ETComplex128) -> VComplex (0.0, 0.0)
            | _ -> VInt 0L
        let mutable sum = zero
        for t in 0 .. n - 1 do
            let mutable prod : Value option = None
            for a in args do
                let v = readCell a [ int64 t ]
                prod <-
                    match prod with
                    | None -> Some v
                    | Some p -> Some(N.evalBinOp IRMul p v)
            match prod with
            | Some p -> sum <- N.evalBinOp IRAdd sum p
            | None -> ()
        sum

// ============================================================================
// §6 Eager set / reshape ops (dense rank-1 unless noted)
// ============================================================================
// These mostly serve the SQL-ish categories outside the M2 loop-object corpus;
// first-occurrence order (unique/intersect/union) and a STABLE sort are pinned
// to CodeGen's semantics. Higher-rank forms beyond transpose are LATER.

let private cmpValues (a: Value) (b: Value) : int =
    match a, b with
    | VInt x, VInt y -> compare x y
    | VInt32 x, VInt32 y -> compare x y
    | VBool x, VBool y -> compare x y
    | VString x, VString y -> System.String.CompareOrdinal(x, y)
    | _ -> compare (toF64v a) (toF64v b)

let private eqValues (a: Value) (b: Value) : bool = cmpValues a b = 0

let private elems1 (arr: BladeArray) : Value list =
    if arr.Extents.Length <> 1 then
        raise (ArrayOpUnsupported "eager op: only rank-1 arrays are supported")
    [ for i in 0L .. arr.Extents.[0] - 1L -> readCell arr [ i ] ]

let private mkRank1 (elemTy: IRType) (idxTys: IRIndexType list) (vs: Value list) : BladeArray =
    let a = Array.ofList vs
    { ElemType = elemTy
      IndexTypes = idxTys
      Extents = [| int64 a.Length |]
      Data = storeOfValues elemTy a }

/// mask(arr, pred): keep elements where pred(v) is truthy (first-occurrence
/// order preserved). pred is the caller's kernel closure (Value -> VBool).
///
/// DEPRECATED SEMANTICS — the OLD filtering `mask`. The current language `mask`
/// is a Bool PRESENCE array (see `maskPresence` below); this filtering form is
/// no longer what CodeGen emits. Kept only so nothing that still references it
/// breaks; the IR arm routes to `maskPresence`, NOT here.
let maskArray (arr: BladeArray) (pred: Value -> Value) : BladeArray =
    let kept = elems1 arr |> List.filter (fun v -> toBoolv (pred v))
    mkRank1 arr.ElemType arr.IndexTypes kept

/// mask(arr, pred): the CURRENT semantics — a rank-1 Bool PRESENCE array over
/// arr's OWN index space, `m[i] = pred(arr[i])`. ONE pass, NO compaction, NO
/// reorder, NO value copy: compaction belongs to `compound(A, m)` and iteration
/// to `range<CompoundIdx<m>>`. Byte-verified against the compiled binary
/// (`materializeMaskForm`, CodeGen.fs:2245): the emitted C++ allocates
/// `Array<bool,1>` of length `A.extents[0]` and writes `m[i] = pred(A[i])`, so
/// `extents(m)` is the SOURCE extent, not a filtered cardinality. rank-1 only
/// (CodeGen emits `#error` for rank>1). The result carries the source's
/// IndexTypes so a downstream `compound(A, m)` sees the shared index space; the
/// element type is Bool. `pred` is the caller's kernel closure (Value -> Value).
let maskPresence (arr: BladeArray) (pred: Value -> Value) : BladeArray =
    if arr.Extents.Length <> 1 then
        raise (ArrayOpUnsupported "mask over a rank>1 array (rank-1 only; mirrors CodeGen's #error)")
    let n = arr.Extents.[0]
    let bools = Array.init (int n) (fun i -> toBoolv (pred (readCell arr [ int64 i ])))
    { ElemType = IRTScalar ETBool
      IndexTypes = arr.IndexTypes
      Extents = [| n |]
      Data = SBool bools }

/// unique(arr): dedup, first-occurrence order.
let uniqueArray (arr: BladeArray) : BladeArray =
    let seen = ResizeArray<Value>()
    for v in elems1 arr do
        if not (seen |> Seq.exists (eqValues v)) then seen.Add v
    mkRank1 arr.ElemType arr.IndexTypes (List.ofSeq seen)

/// sort(arr, key): STABLE ascending sort by key(v) (List.sortWith is stable).
let sortArray (arr: BladeArray) (key: Value -> Value) : BladeArray =
    let sorted = elems1 arr |> List.sortWith (fun a b -> cmpValues (key a) (key b))
    mkRank1 arr.ElemType arr.IndexTypes sorted

/// intersect(a, b): a's elements that also appear in b, first-occurrence, deduped.
let intersectArray (a: BladeArray) (b: BladeArray) : BladeArray =
    let bvals = elems1 b
    let out = ResizeArray<Value>()
    for v in elems1 a do
        if (bvals |> List.exists (eqValues v)) && not (out |> Seq.exists (eqValues v)) then
            out.Add v
    mkRank1 a.ElemType a.IndexTypes (List.ofSeq out)

/// union(a, b): a's elements then b's not-already-present, first-occurrence.
let unionArray (a: BladeArray) (b: BladeArray) : BladeArray =
    let out = ResizeArray<Value>()
    let add v = if not (out |> Seq.exists (eqValues v)) then out.Add v
    for v in elems1 a do add v
    for v in elems1 b do add v
    mkRank1 a.ElemType a.IndexTypes (List.ofSeq out)

/// transpose(arr, d1, d2): swap two dimensions (any rank; new dense array).
let transposeArray (arr: BladeArray) (d1: int) (d2: int) : BladeArray =
    let r = arr.Extents.Length
    if d1 < 0 || d2 < 0 || d1 >= r || d2 >= r then
        raise (ArrayOpUnsupported "transpose: dimension index out of range")
    let newExtents = Array.copy arr.Extents
    let t = newExtents.[d1] in newExtents.[d1] <- newExtents.[d2]
    newExtents.[d2] <- t
    let out = allocDense arr.ElemType arr.IndexTypes newExtents
    let rec loop (dim: int) (acc: int64 list) =
        if dim = r then
            let src = readCell arr (List.rev acc)
            let dst = List.rev acc |> List.toArray
            let tmp = dst.[d1] in dst.[d1] <- dst.[d2]
            dst.[d2] <- tmp
            writeCell out (List.ofArray dst) src
        else
            let mutable i = 0L
            while i < arr.Extents.[dim] do
                loop (dim + 1) (i :: acc)
                i <- i + 1L
    loop 0 []
    out

/// stack(A1..An): fresh LEADING axis of extent n over n same-shaped arrays,
/// so `stack(A,B,C)(k)` selects array k (formalism 2.6). Rank r -> r+1.
///
/// Mirrors CodeGen.materializeStackForm: a fresh dense pool plus a per-source
/// element COPY — never an aliasing assembly, so writing through a source after
/// the stack cannot reach it. The output IndexTypes reuse the child's (the
/// extra leading Idx<n> is not reflected there, exactly as forceSequence does;
/// printing keys off the binding type).
let stackArrays (arrs: BladeArray list) : BladeArray =
    match arrs with
    | [] -> raise (ArrayOpUnsupported "stack: no operands")
    | first :: _ ->
        let srcRank = first.Extents.Length
        let outExtents = Array.append [| int64 arrs.Length |] first.Extents
        let out = allocDense first.ElemType first.IndexTypes outExtents
        arrs |> List.iteri (fun k src ->
            let rec loop (dim: int) (acc: int64 list) =
                if dim = srcRank then
                    let coords = List.rev acc
                    writeCell out (int64 k :: coords) (readCell src coords)
                else
                    let mutable i = 0L
                    while i < src.Extents.[dim] do
                        loop (dim + 1) (i :: acc)
                        i <- i + 1L
            loop 0 [])
        out

/// join(A1..An, d): concatenate along dimension d (formalism 2.6) — rank is
/// preserved, extents[d] is the sum of the operands' extents[d], every other
/// axis agrees. Mirrors CodeGen.materializeJoinForm's running-offset copy.
let joinArrays (arrs: BladeArray list) (dim: int) : BladeArray =
    match arrs with
    | [] -> raise (ArrayOpUnsupported "join: no operands")
    | first :: _ ->
        let r = first.Extents.Length
        if dim < 0 || dim >= r then
            raise (ArrayOpUnsupported "join: dimension index out of range")
        let outExtents = Array.copy first.Extents
        outExtents.[dim] <- arrs |> List.sumBy (fun a -> a.Extents.[dim])
        let out = allocDense first.ElemType first.IndexTypes outExtents
        let mutable offset = 0L
        for src in arrs do
            let rec loop (dim2: int) (acc: int64 list) =
                if dim2 = r then
                    let coords = List.rev acc
                    let dst = coords |> List.mapi (fun d i -> if d = dim then i + offset else i)
                    writeCell out dst (readCell src coords)
                else
                    let mutable i = 0L
                    while i < src.Extents.[dim2] do
                        loop (dim2 + 1) (i :: acc)
                        i <- i + 1L
            loop 0 []
            offset <- offset + src.Extents.[dim]
        out

// ============================================================================
// §6.5 Symmetry producers — decompact / gram / negate / conjugate (M7-β)
// ============================================================================
// Eager producers over compact/dense storage. Each mirrors a CodeGen
// materialize*Form emitter (CodeGen.fs: 2477 decompact, 2806 negate/conjugate,
// 2924 gram), byte-verified against the compiled binary.

/// Apply `f` to every scalar leaf of a store, producing a fresh store of the
/// SAME shape (preserving the SNested/SRagged skeleton). Backs the whole-array
/// negate/conjugate contiguous-pool transforms.
let rec private mapStoreLeaves (f: Value -> Value) (s: Store) : Store =
    match s with
    | SFloat a -> SFloat (a |> Array.map (fun x -> toF64v (f (VFloat x))))
    | SInt a -> SInt (a |> Array.map (fun x -> toI64v (f (VInt x))))
    | SComplex a ->
        SComplex (a |> Array.map (fun (struct (r, i)) ->
            let (nr, ni) = toComplexv (f (VComplex (r, i))) in struct (nr, ni)))
    | SBool a -> SBool (a |> Array.map (fun x -> toBoolv (f (VBool x))))
    | SObj a -> SObj (a |> Array.map f)
    | SNested rows -> SNested (rows |> Array.map (mapStoreLeaves f))
    | SRagged (rows, lens, offs) -> SRagged (rows |> Array.map (mapStoreLeaves f), Array.copy lens, Array.copy offs)

/// Whole-array elementwise negate/conjugate (IRArrayNegate / IRArrayConjugate,
/// CodeGen.fs:2806). Type- AND storage-shape-PRESERVING: negate_pool /
/// conjugate_pool run a flat transform over the contiguous pool, so the result
/// carries the source's exact IndexTypes/Extents/skeleton with every stored cell
/// transformed. Antisym intra-group transpose reaches negate; Hermitian adjoint
/// reaches conjugate (over the already-transposed dense image). conj on a real
/// element is the identity (N.evalUnaryOp IRConj).
let negateConjugateArray (conj: bool) (src: BladeArray) : BladeArray =
    let f = if conj then N.evalUnaryOp IRConj else N.evalUnaryOp IRNeg
    { src with Data = mapStoreLeaves f src.Data }

/// Enumerate every STORED cell of a compact/dense storage shape (`idxTys` +
/// `extents`), invoking `visit storageCoords logicalCoords`. Mirrors
/// emitSymAware's left-justified storage walk EXACTLY — the per-dim bound is
/// `extents[d] - Σ(prior group storage coords) - (#prior)*strictConst` — and
/// reconstructs the LOGICAL tuple via canon_left_justify's inverse
/// (p_k = p_{k-1} + s_k + strict). Plain (SymNone / arity-1) dims: storage ==
/// logical. Used by decompact to walk its (partially compact) output and read
/// the value-equivalent source cell at each logical coordinate.
let private forEachStorageCell (idxTys: IRIndexType list) (extents: int64[])
                               (visit: int64 list -> int64 list -> unit) : unit =
    // Per-flattened-dim descriptor: (dimIdx, priorGroupDims, strictConst).
    let dims = ResizeArray<int * int list * int>()
    let mutable dimIdx = 0
    for ix in idxTys do
        let a = max 1 ix.Rank
        // A wreath group's stored cells are NOT a shrinking-row simplex: the
        // per-dim bound below (extent minus prior storage coords) describes one
        // triangle, and a wreath's rows shrink per LEVEL. Walking it that way
        // would visit the wrong cell set, silently.
        if ix.Symmetry = SymWreath then
            failwith (Blade.IR.orbitStorageUnsupported "storage-cell walk (forEachStorageCell)"
                                                       (Blade.IR.orbitLevelsOf ix))
        let isSym =
            ix.Symmetry = SymSymmetric || ix.Symmetry = SymAntisymmetric || ix.Symmetry = SymHermitian
        let strictConst = if ix.Symmetry = SymAntisymmetric then 1 else 0
        let groupStart = dimIdx
        for comp in 0 .. a - 1 do
            let priorDims = if isSym && comp > 0 then [ groupStart .. groupStart + comp - 1 ] else []
            dims.Add(dimIdx, priorDims, strictConst)
            dimIdx <- dimIdx + 1
    let rank = dims.Count
    let storage : int64[] = Array.zeroCreate rank
    let logical : int64[] = Array.zeroCreate rank
    let rec loop (d: int) =
        if d = rank then visit (List.ofArray storage) (List.ofArray logical)
        else
            let (dIdx, priorDims, strictConst) = dims.[d]
            let subStore = priorDims |> List.sumBy (fun pd -> storage.[pd])
            let bound = extents.[dIdx] - subStore - int64 (List.length priorDims * strictConst)
            let mutable i = 0L
            while i < bound do
                storage.[d] <- i
                logical.[d] <-
                    match priorDims with
                    | [] -> i
                    | _ -> logical.[d - 1] + i + int64 strictConst
                loop (d + 1)
                i <- i + 1L
    if rank > 0 then loop 0

/// decompact(src, d): binary group FISSION (materializeDecompactForm,
/// CodeGen.fs:2477). Fission is VALUE-EQUIVALENT — it only re-groups storage; the
/// logical tensor is unchanged — so every OUTPUT canonical cell equals the SOURCE
/// read at that SAME logical coordinate. Allocate the fission-shaped output (from
/// its carried type `outType`), enumerate its stored cells, and fill each from
/// `readCompact src logicalCoords`. The source read applies the source group's
/// canon_fold (sort + antisym sign + strict-diagonal zero + Hermitian conj),
/// exactly reproducing the C++ scatter's baked full-tuple sign. This single
/// uniform algorithm covers all four C++ shapes (symmetric gather, antisym r2
/// dense, Hermitian r2 dense, antisym r>=3 per-group-strict residual) AND chained
/// decompaction (the intermediate is itself a mixed-compact source readCompact
/// folds correctly).
let decompactArray (src: BladeArray) (outType: IRType) : BladeArray =
    match outType with
    | ArrayElem outArr ->
        let outIdxTys = outArr.IndexTypes
        let outElem = outArr.ElemType
        let totalRank = outIdxTys |> List.sumBy (fun ix -> max 1 ix.Rank)
        let n = if src.Extents.Length >= 1 then src.Extents.[0] else 0L
        let extents = Array.create totalRank n
        let (osym, ostrict) = buildSymmVecWithStrict outType
        let out =
            if hasRealSymmetry osym then
                allocCompact outElem outIdxTys extents (Array.ofList osym) (Array.ofList ostrict)
            else
                allocDense outElem outIdxTys extents
        forEachStorageCell outIdxTys extents (fun storageCoords logicalCoords ->
            writeCell out storageCoords (readCompact src logicalCoords))
        out
    | _ -> raise (ArrayOpUnsupported "decompact: output type is not an array")

/// gram(left, right) = left * right^H:  R[i][j] = Σ_k left[i][k]*conj(right[j][k])
/// (materializeGramForm, CodeGen.fs:2924). Two modes, driven by the carried
/// output type: same-array → square m×m stored as the upper-triangle
/// Sym/Hermitian compact (jr = j - i; the lower triangle is recovered lazily on
/// read, so a downstream decompact/print sees the full matrix); distinct → dense
/// m×p full scatter. conj is std::conj on complex / identity on real. Inputs are
/// forced to real arrays by the caller; for same-array the caller passes the same
/// array as both operands. The k-fold accumulates ascending (matching the C++
/// `acc += ...` order) for byte-parity.
let gramArray (left: BladeArray) (right: BladeArray) (outType: IRType) : BladeArray =
    match outType with
    | ArrayElem outArr ->
        let outElem = outArr.ElemType
        let m = if left.Extents.Length >= 1 then left.Extents.[0] else 0L
        let nn = if left.Extents.Length >= 2 then left.Extents.[1] else 0L
        let p = if right.Extents.Length >= 1 then right.Extents.[0] else 0L
        let zero = zeroOfElemTy outElem
        let dot (i: int64) (j: int64) : Value =
            let mutable acc = zero
            for k in 0L .. nn - 1L do
                let lv = readCell left [ i; k ]
                let rv = N.evalUnaryOp IRConj (readCell right [ j; k ])
                acc <- N.evalBinOp IRAdd acc (N.evalBinOp IRMul lv rv)
            acc
        let (osym, ostrict) = buildSymmVecWithStrict outType
        if hasRealSymmetry osym then
            // same-array: compact Sym/Hermitian m×m, upper triangle (j = i + jr).
            let extents = [| m; m |]
            let out = allocCompact outElem outArr.IndexTypes extents (Array.ofList osym) (Array.ofList ostrict)
            for i in 0L .. m - 1L do
                for jr in 0L .. m - i - 1L do
                    writeCell out [ i; jr ] (dot i (i + jr))
            out
        else
            // distinct: dense m×p, full scatter.
            let extents = [| m; p |]
            let out = allocDense outElem outArr.IndexTypes extents
            for i in 0L .. m - 1L do
                for j in 0L .. p - 1L do
                    writeCell out [ i; j ] (dot i j)
            out
    | _ -> raise (ArrayOpUnsupported "gram: output type is not an array")

/// matmul(left, right) = left * right:  R[i][j] = Σ_t left[i][t]*right[t][j]
/// (materializeMatmulForm in CodeGen; the C++ side is one
/// `blade_linalg::blade_matmul` call). Always DENSE m×n — unlike gram there is
/// no same-array symmetry to claim, since A·A is not symmetric.
///
/// BYTE-IDENTITY: the t-fold accumulates ASCENDING from the element zero,
/// matching BOTH the shim's native fallback (`acc += A(i,t) * B(t,j)`, one
/// local accumulator per output cell) and the synthesized Blade triple loop
/// this route replaced. That agreement is what tests/InterpDiff.fs checks.
let matmulArray (left: BladeArray) (right: BladeArray) (outType: IRType) : BladeArray =
    match outType with
    | ArrayElem outArr ->
        let outElem = outArr.ElemType
        let m = if left.Extents.Length >= 1 then left.Extents.[0] else 0L
        let kk = if left.Extents.Length >= 2 then left.Extents.[1] else 0L
        let n = if right.Extents.Length >= 2 then right.Extents.[1] else 0L
        let zero = zeroOfElemTy outElem
        let extents = [| m; n |]
        let out = allocDense outElem outArr.IndexTypes extents
        for i in 0L .. m - 1L do
            for j in 0L .. n - 1L do
                let mutable acc = zero
                for t in 0L .. kk - 1L do
                    let lv = readCell left [ i; t ]
                    let rv = readCell right [ t; j ]
                    acc <- N.evalBinOp IRAdd acc (N.evalBinOp IRMul lv rv)
                writeCell out [ i; j ] acc
        out
    | _ -> raise (ArrayOpUnsupported "matmul: output type is not an array")

/// eigh(S) -> (Q, LAM): symmetric eigendecomposition by cyclic two-sided
/// Jacobi. Q's COLUMNS are the eigenvectors, LAM is descending, and each Q
/// column is sign-fixed so the first row attaining the maximum |entry| is
/// positive — the conventions `MathDecls.eighDecl` documents and
/// `blade_lapack`'s `emit_values_desc` / `emit_vectors_desc` reproduce.
///
/// THIS IS A DELIBERATE COPY of `BladeMath.Jacobi.eigh` (src/math/Jacobi.fs),
/// operation for operation, NOT a call into it. BladeMath is a SEPARATE fsproj
/// that the compiler never references, and that separation is the whole reason
/// it can serve as the VALUE ORACLE for the generated Blade code: an oracle
/// that shares source with the thing it checks proves only that one copy exists.
/// The duplication is the point; keeping it in step is a review obligation the
/// oracle differential (`blade test diff-oracle math`) already enforces from the
/// other side.
///
/// REACHABILITY. `IREigh` only exists when LAPACK was available at ELABORATION
/// time; with the gate off `math.eigh` still expands to synthesized Blade Jacobi
/// source, which the interpreter walks as ordinary Blade code. So this function
/// runs only in a gate-ON interpreter run — a configuration the plan says must
/// never be used for byte-identity (`interp` / `diff-oracle` must run gate-off,
/// because an eigensolver's output is not unique). It exists so the interpreter
/// has an answer for every node the compiler can build, not as a differential
/// twin of the LAPACK route: the two agree on eigenvalues and on the two
/// normalised freedoms, not bit for bit, and inside a degenerate eigenvalue's
/// subspace not even on the basis.
///
/// The operand is read through `indexArray`, which routes a compact
/// (symmetric / Hermitian) rank-2 group to the canonical reader and a dense one
/// to the ordinary peel — so the PACKED and DENSE surface spellings both work
/// here with one code path, exactly as they do through the shim.
///
/// COMPLEX IS DECLINED, by name. A Hermitian Jacobi needs complex rotations and
/// would be a NEW implementation with no oracle behind it — strictly worse than
/// refusing, since a plausible-looking wrong answer is the failure mode this
/// whole layer is built to avoid. The compiled `?heev` / `?hpev` route is the
/// only implementation of the complex case.
let eighArrays (operand: BladeArray) (outType: IRType) : BladeArray * BladeArray =
    let (qTy, lamTy) =
        match outType with
        | IRTTuple [ qT; lamT ] ->
            (match qT, lamT with
             | ArrayElem qa, ArrayElem la -> (qa, la)
             | _ -> raise (ArrayOpUnsupported "eigh: result type is not a pair of arrays"))
        | _ -> raise (ArrayOpUnsupported "eigh: result type is not a 2-tuple")
    (match elemThrough operand.ElemType with
     | Some (ETComplex64 | ETComplex128) ->
         raise (ArrayOpUnsupported "eigh: the interpreter implements the REAL symmetric case only; a Hermitian (complex) eigendecomposition is available from the compiled LAPACK route (?heev / ?hpev) and is deliberately not re-implemented here without an oracle behind it")
     | _ -> ())
    let n = if operand.Extents.Length >= 1 then int operand.Extents.[0] else 0
    // Working copy of the symmetric input + the eigenvector accumulator,
    // identical to the oracle's `aw` / `q` (and to eighDecl's `aw` / `qm`).
    let aw = Array2D.init n n (fun i j -> toF64v (indexArray operand [VInt (int64 i); VInt (int64 j)]))
    let qm = Array2D.init n n (fun i j -> if i = j then 1.0 else 0.0)
    // `MathDecls.defaultSweeps` (10), duplicated here for the same
    // oracle-independence reason the algorithm is. The intrinsic only ever
    // means the DEFAULT schedule: the planned elaborator rule keeps the
    // synthesized Jacobi path whenever an explicit SWEEPS argument is given,
    // because a stated sweep budget is a request for that algorithm and LAPACK
    // has no analogue of it. If that rule ever changes, this constant is the
    // other half of the change.
    let sweeps = 10
    for _sweep in 1 .. sweeps do
        for p in 0 .. n - 2 do
            for r in p + 1 .. n - 1 do
                let apq = aw.[p, r]
                let app = aw.[p, p]
                let aqq = aw.[r, r]
                let conv = abs apq <= 1.0e-15 * sqrt (abs app * abs aqq + 1.0e-300)
                let theta = (aqq - app) / (if conv then 1.0 else 2.0 * apq)
                let tt = (if theta >= 0.0 then 1.0 else -1.0) / (abs theta + sqrt (1.0 + theta * theta))
                let cs = if conv then 1.0 else 1.0 / sqrt (1.0 + tt * tt)
                let sn = if conv then 0.0 else cs * tt
                // AW <- AW.R (columns p, r), then AW <- R^T.AW (rows p, r).
                for i in 0 .. n - 1 do
                    let tp = aw.[i, p]
                    let tq = aw.[i, r]
                    aw.[i, p] <- cs * tp - sn * tq
                    aw.[i, r] <- sn * tp + cs * tq
                for i in 0 .. n - 1 do
                    let tp = aw.[p, i]
                    let tq = aw.[r, i]
                    aw.[p, i] <- cs * tp - sn * tq
                    aw.[r, i] <- sn * tp + cs * tq
                // Accumulate the column rotation into Q.
                for i in 0 .. n - 1 do
                    let tp = qm.[i, p]
                    let tq = qm.[i, r]
                    qm.[i, p] <- cs * tp - sn * tq
                    qm.[i, r] <- sn * tp + cs * tq
    let lam = Array.init n (fun j -> aw.[j, j])
    // Selection sort descending (ties keep original order) + Q column swaps.
    for kk in 0 .. n - 1 do
        let mutable best = kk
        for j in kk + 1 .. n - 1 do
            if lam.[j] > lam.[best] then best <- j
        let tl = lam.[kk] in lam.[kk] <- lam.[best]; lam.[best] <- tl
        for i in 0 .. n - 1 do
            let tq = qm.[i, kk] in qm.[i, kk] <- qm.[i, best]; qm.[i, best] <- tq
    // Sign fix: first row attaining max |entry| per column made positive.
    for j in 0 .. n - 1 do
        let mutable bigv = 0.0
        let mutable best = 0
        for i in 0 .. n - 1 do
            let mag = abs qm.[i, j]
            if mag > bigv then
                best <- i
                bigv <- mag
        let flip = if qm.[best, j] < 0.0 then -1.0 else 1.0
        for i in 0 .. n - 1 do qm.[i, j] <- qm.[i, j] * flip
    let qOut = allocDense qTy.ElemType qTy.IndexTypes [| int64 n; int64 n |]
    let lamOut = allocDense lamTy.ElemType lamTy.IndexTypes [| int64 n |]
    for i in 0 .. n - 1 do
        for j in 0 .. n - 1 do writeCell qOut [ int64 i; int64 j ] (VFloat qm.[i, j])
    for j in 0 .. n - 1 do writeCell lamOut [ int64 j ] (VFloat lam.[j])
    (qOut, lamOut)

// ============================================================================
// §7 Compound (masked product space, formalism 4.5) — construction + reads
// ============================================================================
// The value-space twin of runtime `Compound<T,RANK>` + `compound_index_t`
// (cpp/nested_array_types.hpp:133, index_types.h:235). A compound bundles the
// rank<->tuple bijection over a masked product space with a compact backing
// buffer holding only the present cells (each followed by its trailing block).
// Every read/reduce mirrors a specific C++ helper byte-for-byte (§4.7 pin-points):
//   full scalar   C(i, j)     -> data[linearize(coords)*trail + t]  (flat
//                                subscripts; typecheck packs them into the
//                                one IR-level tuple this section consumes)
//   trailing row  C(i, j)     -> the trailing block when trailing dims exist
//   reduce/sort/…              -> walk the compact buffer (.data)
// (partial/wildcard reads moved to SparseIdx — §7b's sparsePartial)

/// Flatten a rank-N Bool mask array to row-major bits — the presence vector a
/// compound_index_t enumerates (pool_base flatten, genCompoundIndexFromMask).
let maskToBits (arr: BladeArray) : bool[] = flatLeaves arr |> Array.map toBoolv

/// Row-major (lex) flat offset of a tuple over the masked grid — mirrors
/// compound_index_t::mask_offset (index_types.h:283): off = Σ off*extents[d]+t[d].
let compoundMaskOffset (leadExtents: int64[]) (tuple: int64[]) : int64 =
    let mutable off = 0L
    for d in 0 .. leadExtents.Length - 1 do off <- off * leadExtents.[d] + tuple.[d]
    off

/// Build the rank<->tuple bijection from flat mask bits over the masked grid,
/// scanning the product space in row-major LEX order and appending each
/// mask-valid tuple (compound_index_t::enumerate, index_types.h:288-300). Returns
/// (rank_to_tuple, mask_offset -> rank map, cardinality).
let buildCompoundIndex (leadExtents: int64[]) (maskBits: bool[]) : int64[][] * Dictionary<int64, int> * int64 =
    let table = ResizeArray<int64[]>()
    let rankOf = Dictionary<int64, int>()
    let rank = leadExtents.Length
    let idx = Array.zeroCreate rank
    let rec enumerate (depth: int) =
        if depth = rank then
            let off = compoundMaskOffset leadExtents idx
            if maskBits.[int off] then
                rankOf.[off] <- table.Count
                table.Add(Array.copy idx)
        else
            let e = leadExtents.[depth]
            let mutable v = 0L
            while v < e do
                idx.[depth] <- v
                enumerate (depth + 1)
                v <- v + 1L
    (if rank > 0 then enumerate 0)
    (table.ToArray(), rankOf, int64 table.Count)

/// Read compact-buffer cell `i` as a Value (mirrors the stored element type).
let private compactCell (data: Store) (i: int) : Value =
    match data with
    | SFloat a -> VFloat a.[i]
    | SInt a -> VInt a.[i]
    | SComplex a -> let struct (r, im) = a.[i] in VComplex (r, im)
    | SBool a -> VBool a.[i]
    | SObj a -> a.[i]
    | _ -> raise (InterpPanic ("BL8003", "compound read: unexpected backing store", None, 0))

/// linearize(tuple) -> compact rank via the reverse map (compound_index_t::
/// linearize = tuple_to_rank.at). A tuple that is not present is a program
/// error (C++ .at() throws); the corpus only ever reads present cells.
let compoundLinearize (cv: CompoundValue) (coords: int64[]) : int =
    match cv.RankOf.TryGetValue(compoundMaskOffset cv.LeadExtents coords) with
    | true, r -> r
    | _ -> raise (InterpPanic ("BL8003", "compound read: coordinate not present in mask", None, 0))

/// Build a Compound VALUE from a dense array + a bool mask array (the compound()
/// / load_compound constructor). `arrType` is the compound view type (its
/// IxKCompound slot carries LeadRank). The mask covers the LEADING `leadRank`
/// dims of `dense`; remaining dense dims fold into the trailing stride. Scatter
/// each present leading cell's trailing block into a compact buffer, in the
/// index's lex rank order (genCompoundInitBinding, CodeGen.fs:8581-8640).
let buildCompound (arrType: IRArrayType) (dense: BladeArray) (mask: BladeArray) : CompoundValue =
    let leadRank =
        arrType.IndexTypes
        |> List.tryFind (fun ix -> ix.IxKind = IxKCompound)
        |> Option.map (fun ix -> ix.Rank)
        |> Option.defaultValue (max 1 mask.Extents.Length)
    let leadExtents = mask.Extents
    let maskBits = flatLeaves mask |> Array.map toBoolv
    let (table, rankOf, card) = buildCompoundIndex leadExtents maskBits
    let trail =
        [ leadRank .. dense.Extents.Length - 1 ]
        |> List.fold (fun acc d -> acc * dense.Extents.[d]) 1L
    let denseVals = flatLeaves dense
    let itrail = int trail
    let compact = Array.create (int card * itrail) VUnit
    for r in 0 .. int card - 1 do
        let off = int (compoundMaskOffset leadExtents table.[r])
        for t in 0 .. itrail - 1 do
            compact.[r * itrail + t] <- denseVals.[off * itrail + t]
    { ElemType = arrType.ElemType
      IndexTypes = arrType.IndexTypes
      LeadRank = leadRank
      LeadExtents = leadExtents
      Mask = maskBits
      Table = table
      RankOf = rankOf
      Cardinality = card
      TrailingStride = trail
      Data = storeOfValues arrType.ElemType compact }

/// The trailing (regular) index types of a compound (everything after the
/// compound head slot); used to shape a trailing-row / residual sub-view.
let private trailingIndexTypes (cv: CompoundValue) : IRIndexType list =
    match cv.IndexTypes with _ :: t -> t | [] -> []

/// Full-tuple SCALAR read: `data[linearize(coords)*trailing_stride + trailOffset]`
/// (Compound::operator(), nested_array_types.hpp:145).
let compoundFullScalar (cv: CompoundValue) (coords: int64[]) (trailOffset: int64) : Value =
    let r = compoundLinearize cv coords
    compactCell cv.Data (r * int cv.TrailingStride + int trailOffset)

/// Trailing-ROW sub-view for a resolved lead tuple (Compound::row): the
/// contiguous span of `trailing_stride` cells at data + linearize(coords)*trail.
/// A dense rank-1 array over the (single) trailing dim.
let compoundRow (cv: CompoundValue) (coords: int64[]) : Value =
    let r = compoundLinearize cv coords
    let itrail = int cv.TrailingStride
    let vs = Array.init itrail (fun t -> compactCell cv.Data (r * itrail + t))
    VArray
        { ElemType = cv.ElemType
          IndexTypes = trailingIndexTypes cv
          Extents = [| cv.TrailingStride |]
          Data = storeOfValues cv.ElemType vs }

// (compoundPartial — the interpreter's partial-compound gather — was removed
// with the flat-subscript conversion: partial/wildcard reads are a SparseIdx
// feature now (sparsePartial in §7b below). A CompoundPartial classification
// on a compound head is an internal invariant break, backstopped at the read
// sites in Loops.fs / CodeGen's compoundRead.)

/// The compact present values of a compound as a plain rank-1 dense array
/// (cardinality*trailing_stride cells, buffer order) — the operand form the
/// eager ops (sort/reduce/set-op) consume, matching CodeGen's compound-operand
/// path which walks `.data` (§4.1, genReduceBinding reduceBound §1936).
let compoundToDense (cv: CompoundValue) : BladeArray =
    let n = int cv.Cardinality * int cv.TrailingStride
    let vs = Array.init n (fun i -> compactCell cv.Data i)
    { ElemType = cv.ElemType
      IndexTypes = []
      Extents = [| int64 n |]
      Data = storeOfValues cv.ElemType vs }

/// reduce over a compound's present cells (init required for the always-emitted
/// empty guard; without init, empty panics — matching genReduceBinding).
let compoundReduce (cv: CompoundValue) (fold: Value -> Value -> Value) (init: Value option) : Value =
    reduceArray (compoundToDense cv) fold init

/// `S <|:> D` compound-left allocated fallback: a DENSE array shaped like D, in
/// which each of S's PRESENT leading cells overwrites its trailing block onto a
/// copy of D (absent leading cells keep D — the SQL sparse-overlay regime,
/// genFallbackMaterialize compound-left arm, CodeGen.fs:9398-9449). Single
/// trailing dim only (the compiler-wide compound gate).
let fallbackCompoundLeft (cvS: CompoundValue) (d: BladeArray) : BladeArray =
    let result = copyBladeArray d
    let itrail = int cvS.TrailingStride
    for r in 0 .. int cvS.Cardinality - 1 do
        let lead = Array.toList cvS.Table.[r]
        for tr in 0 .. itrail - 1 do
            let coords = if itrail = 1 then lead else lead @ [ int64 tr ]
            writeCell result coords (compactCell cvS.Data (r * itrail + tr))
    result

// ============================================================================
// §7b Sparse (explicit key enumeration, formalism 3.5) — construction + reads
// ============================================================================
// The value-space twin of runtime `Sparse<T,RANK>` + `sparse_index_t`
// (cpp/nested_array_types.hpp, index_types.h). The compound §7 machinery MINUS
// the grid: keys stay in GIVEN order (iteration order == key order), the
// reverse map keys structurally on the tuple (TupleKeyComparer — no grid
// offset exists), and every partial read is a gather over the entry list
// (make_partial_sparse_gather / make_sparse_gather_dense[_trail]).

/// Build the rank<->tuple bijection from an explicit key list in GIVEN order.
/// Duplicate keys panic (sparse_index_t's ctor throws — the bijection would be
/// ill-defined); InterpDiff parity requires the same failure here.
let buildSparseIndex (keys: int64[][]) : Dictionary<int64[], int> * int64 =
    let rankOf = Dictionary<int64[], int>(TupleKeyComparer())
    keys |> Array.iteri (fun r key ->
        if rankOf.ContainsKey key then
            raise (InterpPanic ("BL8005", "SparseIdx: duplicate key tuple", None, 0))
        rankOf.[key] <- r)
    (rankOf, int64 keys.Length)

/// Build a Sparse VALUE from a values array + an explicit key list (the
/// sparse() constructor). `arrType` is the sparse view type (its IxKSparse slot
/// carries the key tuple arity). The values' LEADING dimension is the key axis
/// (one cell per key, in key order); any remaining dims fold into the trailing
/// stride, mirroring buildCompound's leading/trailing split. No scatter: the
/// flattened values pool IS the compact buffer's key-major layout, so this is a
/// straight copy (genSparseInitBinding's pool_base loop).
let buildSparse (arrType: IRArrayType) (values: BladeArray) (keys: int64[][])
                (rankOf: Dictionary<int64[], int>) (card: int64) : SparseValue =
    let leadRank =
        arrType.IndexTypes
        |> List.tryFind (fun ix -> ix.IxKind = IxKSparse)
        |> Option.map (fun ix -> ix.Rank)
        |> Option.defaultValue (if keys.Length > 0 then keys.[0].Length else 1)
    if values.Extents.Length < 1 || values.Extents.[0] <> card then
        raise (InterpPanic ("BL8001", "sparse(values, keys): values length does not match key count", None, 0))
    let trail =
        [ 1 .. values.Extents.Length - 1 ]
        |> List.fold (fun acc d -> acc * values.Extents.[d]) 1L
    let valueCells = flatLeaves values
    { ElemType = arrType.ElemType
      IndexTypes = arrType.IndexTypes
      LeadRank = leadRank
      Keys = keys
      RankOf = rankOf
      Cardinality = card
      TrailingStride = trail
      Data = storeOfValues arrType.ElemType (Array.sub valueCells 0 (int card * int trail)) }

/// linearize(tuple) -> rank via the structural hash (sparse_index_t::linearize
/// = tuple_to_rank.at). A missing key is a program error (C++ .at() throws).
let sparseLinearize (sv: SparseValue) (coords: int64[]) : int =
    match sv.RankOf.TryGetValue coords with
    | true, r -> r
    | _ -> raise (InterpPanic ("BL8003", "sparse read: key tuple not present in key set", None, 0))

let private sparseTrailingIndexTypes (sv: SparseValue) : IRIndexType list =
    match sv.IndexTypes with _ :: t -> t | [] -> []

/// Full-key SCALAR read (Sparse::operator()).
let sparseFullScalar (sv: SparseValue) (coords: int64[]) (trailOffset: int64) : Value =
    let r = sparseLinearize sv coords
    compactCell sv.Data (r * int sv.TrailingStride + int trailOffset)

/// Trailing-ROW sub-view for a resolved key (Sparse::row).
let sparseRow (sv: SparseValue) (coords: int64[]) : Value =
    let r = sparseLinearize sv coords
    let itrail = int sv.TrailingStride
    let vs = Array.init itrail (fun t -> compactCell sv.Data (r * itrail + t))
    VArray
        { ElemType = sv.ElemType
          IndexTypes = sparseTrailingIndexTypes sv
          Extents = [| sv.TrailingStride |]
          Data = storeOfValues sv.ElemType vs }

/// Partial (residual) sparse indexing: ALWAYS a gather — one pass over Keys in
/// key order keeping the entries whose pinned axes match. Residual keys are the
/// matches' free-axis coordinates (automatically distinct, parent key order).
///   residual rank 1  -> dense Array<T,1> (trail 1) or Array<T,2> (trail>1)
///   residual rank>=2 -> residual Sparse (its own key table, key order)
let sparsePartial (sv: SparseValue) (pinned: (int * int64) list) (freePos: int list) : Value =
    let rr = List.length freePos
    let itrail = int sv.TrailingStride
    let freeArr = Array.ofList freePos
    let matches =
        [| for r in 0 .. int sv.Cardinality - 1 do
             let key = sv.Keys.[r]
             if pinned |> List.forall (fun (pos, v) -> key.[pos] = v) then
                 yield (r, freeArr |> Array.map (fun p -> key.[p])) |]
    if rr = 1 then
        if itrail = 1 then
            let vs = matches |> Array.map (fun (r, _) -> compactCell sv.Data r)
            VArray
                { ElemType = sv.ElemType
                  IndexTypes = sparseTrailingIndexTypes sv
                  Extents = [| int64 vs.Length |]
                  Data = storeOfValues sv.ElemType vs }
        else
            // rank-2 dense {matches, trailing extent}: each match's whole
            // trailing block (make_sparse_gather_dense_trail).
            let rows =
                matches
                |> Array.map (fun (r, _) ->
                    let vs = Array.init itrail (fun t -> compactCell sv.Data (r * itrail + t))
                    storeOfValues sv.ElemType vs)
            VArray
                { ElemType = sv.ElemType
                  IndexTypes = []
                  Extents = [| int64 rows.Length; sv.TrailingStride |]
                  Data = SNested rows }
    else
        // Residual SPARSE: sub-key table in parent key order, gathered blocks.
        let subKeys = matches |> Array.map snd
        let (subRankOf, subCard) = buildSparseIndex subKeys
        let compact = Array.create (int subCard * itrail) VUnit
        matches |> Array.iteri (fun i (r, _) ->
            for t in 0 .. itrail - 1 do
                compact.[i * itrail + t] <- compactCell sv.Data (r * itrail + t))
        VSparse
            { ElemType = sv.ElemType
              IndexTypes = sv.IndexTypes
              LeadRank = rr
              Keys = subKeys
              RankOf = subRankOf
              Cardinality = subCard
              TrailingStride = sv.TrailingStride
              Data = storeOfValues sv.ElemType compact }

/// The compact values of a sparse as a plain rank-1 dense array (buffer/key
/// order) — the operand form the eager ops consume, mirroring compoundToDense.
let sparseToDense (sv: SparseValue) : BladeArray =
    let n = int sv.Cardinality * int sv.TrailingStride
    let vs = Array.init n (fun i -> compactCell sv.Data i)
    { ElemType = sv.ElemType
      IndexTypes = []
      Extents = [| int64 n |]
      Data = storeOfValues sv.ElemType vs }

/// reduce over a sparse's cells (key order; init drives the empty guard).
let sparseReduce (sv: SparseValue) (fold: Value -> Value -> Value) (init: Value option) : Value =
    reduceArray (sparseToDense sv) fold init

// ============================================================================
// §8 group_keys / group_by (CSR grouping) — build + read
// ============================================================================
// group_keys builds a CSR structure (offsets + group-contiguous member perm);
// group_by gathers each group's values into a ragged array. Bucket ORDER is the
// subtle part (§4.2/4.8): first-appearance (dynamic / multi-key), numeric-value
// (positional Idx<N>), or enum-list-position (EnumIdx). CodeGen stores NO keys
// array — the perm recovers everything (genGroupKeysBinding, CodeGen.fs:7511).

/// The three group-key bucketing regimes, dispatched on the group_keys binding's
/// IRTGroupKeys type (single key) or key arity (>1 ⇒ dynamic tuple-hash).
///   GKDynamic      — Case 3 / multi-key: bucket = first-appearance ordinal.
///   GKPositional n — Case 1 (Idx<N> keys): bucket = the integer key value.
///   GKEnum values  — Case 2 (EnumIdx): bucket = the key's position in `values`.
type GroupKeyCase =
    | GKDynamic
    | GKPositional of ngroups: int
    | GKEnum of values: Value[]

/// An injective-enough string key for first-appearance dedup (mirrors the C++
/// unordered_map keyed by the value / tuple; ints value-equal, strings ordinal).
let private valueDedupKey (v: Value) : string =
    match v with
    | VInt n -> "i" + string n
    | VInt32 n -> "i" + string (int64 n)
    | VString s -> "s" + s
    | VBool b -> "b" + (if b then "1" else "0")
    | VFloat f -> "f" + f.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
    | VFloat32 f -> "f" + (float f).ToString("R", System.Globalization.CultureInfo.InvariantCulture)
    | VChar c -> "c" + string (int c)
    | _ -> "?"

/// Build the CSR grouping (offsets length ngroups+1, member perm in group-
/// contiguous input order). Bucket order per the regime; counts/offsets/perm
/// exactly mirror genGroupKeysBinding (CodeGen.fs:7595-7607).
let buildGroupKeys (keyArrays: BladeArray list) (gkCase: GroupKeyCase) : GroupKeysValue =
    let n = if List.isEmpty keyArrays then 0 else int keyArrays.[0].Extents.[0]
    let buckets = Array.zeroCreate n
    let ngroups =
        match gkCase with
        | GKPositional ng ->
            for i in 0 .. n - 1 do buckets.[i] <- int (toI64v (readCell keyArrays.[0] [ int64 i ]))
            ng
        | GKEnum values ->
            for i in 0 .. n - 1 do
                let v = readCell keyArrays.[0] [ int64 i ]
                buckets.[i] <- (match values |> Array.tryFindIndex (eqValues v) with Some p -> p | None -> 0)
            values.Length
        | GKDynamic ->
            let lookup = Dictionary<string, int>()
            let mutable ng = 0
            for i in 0 .. n - 1 do
                let key =
                    keyArrays |> List.map (fun a -> valueDedupKey (readCell a [ int64 i ])) |> String.concat ""
                match lookup.TryGetValue key with
                | true, b -> buckets.[i] <- b
                | _ -> lookup.[key] <- ng; buckets.[i] <- ng; ng <- ng + 1
            ng
    let counts = Array.zeroCreate (max 1 ngroups)
    for i in 0 .. n - 1 do counts.[buckets.[i]] <- counts.[buckets.[i]] + 1
    let offsets = Array.zeroCreate (ngroups + 1)
    for g in 0 .. ngroups - 1 do offsets.[g + 1] <- offsets.[g] + int64 counts.[g]
    let fill = Array.zeroCreate (max 1 ngroups)
    let perm = Array.zeroCreate n
    for i in 0 .. n - 1 do
        let g = buckets.[i]
        perm.[int offsets.[g] + fill.[g]] <- int64 i
        fill.[g] <- fill.[g] + 1
    { Offsets = offsets; Members = perm }

/// group_by(vals, gk): gather each group's values (`vals[perm[offsets[g]+k]]`,
/// input order) into a ragged rank-2 array (genGroupByBinding, CodeGen.fs:8767).
/// Extents = [ngroups; 0] — the inner is ragged, print-bound 0 (auto-print → []).
let buildGroupBy (idxTys: IRIndexType list) (gk: GroupKeysValue) (vals: BladeArray) : BladeArray =
    let ngroups = gk.Offsets.Length - 1
    let rows =
        Array.init ngroups (fun g ->
            let lo = int gk.Offsets.[g]
            let hi = int gk.Offsets.[g + 1]
            let vs = Array.init (hi - lo) (fun k -> readCell vals [ gk.Members.[lo + k] ])
            storeOfValues vals.ElemType vs)
    let lens = Array.init ngroups (fun g -> gk.Offsets.[g + 1] - gk.Offsets.[g])
    { ElemType = vals.ElemType
      IndexTypes = idxTys
      Extents = [| int64 ngroups; 0L |]
      Data = SRagged (rows, lens, gk.Offsets) }

// ============================================================================
// §PRINT: byte-parity array binding printer (mirrors CodeGen genPrintStatements)
// ============================================================================
//
// A top-level array binding prints via genPrintArrayFlat (ranks 1-4, else a
// rank-N placeholder) or genPrintArraySymAware (symmetric ranks 2-8), per the
// genPrintStatements dispatch (CodeGen.fs:9889). This mirrors the FLAT path
// byte-for-byte; the sym-aware, ragged, and non-scalar-element paths are LATER
// (raise ArrayOpUnsupported ⇒ the gate SKIP-classifies, exactly as codegen's
// comment-only / M2.5 cases are handled). Print.printBindings calls this in
// place of its current PrintUnsupported raise for array bindings, appending to
// the SAME StringBuilder (no timing line here — printBindings emits that once).
//
// FLAT FORMAT (genPrintArrayFlat, verified against the compiled binary):
//   rank 1-3 :  name = [c0, c1, c2, ...]\n     (row-major, ", " between cells)
//   rank 4   :  name (E0xE1xE2xE3):\n
//               ␠␠name[i][j] = [ ... ]\n        (one line per (i,j); 2-space lead)
//   rank 5+  :  name = <rank-N array>\n
//   rank 0   :  name = <rank-0>\n
// Each cell renders as `cout << name[...]` would for the element's C++ static
// type — i.e. formatFloat15 for Float64, etc.

let private isPrintableScalarEt (et: ElemType) : bool =
    match et with
    | ETFloat64 | ETFloat32 | ETInt64 | ETInt32 | ETBool | ETComplex64 | ETComplex128 | ETString -> true
    | ETUnit -> false

/// Render one array cell exactly as `cout << name[...]` for its element type,
/// coercing the stored Value to that static type (mirrors Print.formatScalar).
let private formatCell (et: ElemType) (v: Value) : string =
    match et with
    | ETFloat64 ->
        match v with
        | VFloat f -> formatFloat15 f
        | VFloat32 f -> formatFloat15 (float f)
        | VInt n -> formatFloat15 (float n)
        | VInt32 n -> formatFloat15 (float n)
        | VComplex (r, _) -> formatFloat15 r
        | _ -> formatFloat15 nan
    | ETFloat32 ->
        match v with
        | VFloat32 f -> formatFloat32 f
        | VFloat f -> formatFloat32 (float32 f)
        | VInt n -> formatFloat32 (float32 n)
        | VInt32 n -> formatFloat32 (float32 n)
        | _ -> formatFloat32 (float32 nan)
    | ETInt64 ->
        match v with
        | VInt n -> formatInt64 n
        | VInt32 n -> formatInt64 (int64 n)
        | VBool b -> formatInt64 (if b then 1L else 0L)
        | VFloat f -> formatInt64 (int64 f)
        | _ -> formatInt64 0L
    | ETInt32 ->
        match v with
        | VInt32 n -> formatInt32 n
        | VInt n -> formatInt32 (int32 n)
        | VChar c -> formatInt32 (int32 c)
        | _ -> formatInt32 0
    | ETBool ->
        match v with
        | VBool b -> formatBool b
        | VInt n -> formatBool (n <> 0L)
        | _ -> formatBool false
    | ETComplex128 | ETComplex64 ->
        match v with
        | VComplex (r, im) -> formatComplex r im
        | VFloat f -> formatComplex f 0.0
        | VFloat32 f -> formatComplex (float f) 0.0
        | VInt n -> formatComplex (float n) 0.0
        | VInt32 n -> formatComplex (float n) 0.0
        | _ -> formatComplex 0.0 0.0
    | ETString ->
        match v with
        | VString s -> formatString s
        | _ -> ""
    | ETUnit -> ""

/// Iterate every coordinate of a dense array in row-major order.
let private forEachCoordRowMajor (extents: int64[]) (f: int64 list -> unit) : unit =
    let n = extents.Length
    let rec loop (dim: int) (acc: int64 list) =
        if dim = n then f (List.rev acc)
        else
            let e = extents.[dim]
            let mutable i = 0L
            while i < e do
                loop (dim + 1) (i :: acc)
                i <- i + 1L
    loop 0 []

/// Rank 2: `name = [[a, b], [c, d]]` — the twin of CodeGen.genPrintNested2, and
/// byte-identical to it. `innerBound` takes the outer coordinate because a
/// compact group's row shrinks with it (`extents[1] - i`, minus one more when
/// the group is strict); a dense pair ignores its argument.
let private emitNested2 (sb: StringBuilder) (name: string) (arr: BladeArray) (et: ElemType)
                        (outerBound: int64) (innerBound: int64 -> int64) : unit =
    sb.Append(name).Append(" = [") |> ignore
    let mutable i = 0L
    while i < outerBound do
        if i > 0L then sb.Append(", ") |> ignore
        sb.Append("[") |> ignore
        let mutable first = true
        let mutable j = 0L
        while j < innerBound i do
            if not first then sb.Append(", ") |> ignore
            first <- false
            sb.Append(formatCell et (readCell arr [ i; j ])) |> ignore
            j <- j + 1L
        sb.Append("]") |> ignore
        i <- i + 1L
    sb.Append("]").Append('\n') |> ignore

/// Ranks 1 and 3: `name = [c0, c1, ...]` row-major, ", "-separated, `]`, newline.
/// (Rank 2 goes through emitNested2 — see its twin's note on why only that rank
/// nests.)
let private emitFlat123 (sb: StringBuilder) (name: string) (arr: BladeArray) (et: ElemType) : unit =
    if arr.Extents.Length = 2 then
        emitNested2 sb name arr et arr.Extents.[0] (fun _ -> arr.Extents.[1])
    else
    sb.Append(name).Append(" = [") |> ignore
    let mutable first = true
    forEachCoordRowMajor arr.Extents (fun coords ->
        if not first then sb.Append(", ") |> ignore
        first <- false
        sb.Append(formatCell et (readCell arr coords)) |> ignore)
    sb.Append("]").Append('\n') |> ignore

/// Rank 4: a header line then one `␠␠name[i][j] = [ ... ]` line per outer pair.
let private emitRank4 (sb: StringBuilder) (name: string) (arr: BladeArray) (et: ElemType) : unit =
    let e = arr.Extents
    sb.Append(name).Append(" (")
      .Append(string e.[0]).Append('x').Append(string e.[1]).Append('x')
      .Append(string e.[2]).Append('x').Append(string e.[3]).Append("):").Append('\n')
    |> ignore
    let mutable i = 0L
    while i < e.[0] do
        let mutable j = 0L
        while j < e.[1] do
            sb.Append("  ").Append(name).Append('[').Append(string i).Append("][")
              .Append(string j).Append("] = [")
            |> ignore
            let mutable first = true
            let mutable k = 0L
            while k < e.[2] do
                let mutable l = 0L
                while l < e.[3] do
                    if not first then sb.Append(", ") |> ignore
                    first <- false
                    sb.Append(formatCell et (readCell arr [ i; j; k; l ])) |> ignore
                    l <- l + 1L
                k <- k + 1L
            sb.Append("]").Append('\n') |> ignore
            j <- j + 1L
        i <- i + 1L

/// Symmetric-aware print: mirror CodeGen.genPrintArraySymAware (CodeGen.fs:9791)
/// EXACTLY. Iterate the compact (triangular / strict-triangular) index space in
/// left-justified STORAGE coordinates — the bound at group component a is
/// `extents[d] - Σ(prior group vars) - a*strictConst` (strictConst = 1 for
/// antisymmetric, 0 for symmetric/Hermitian) — and read each RAW stored cell
/// (`name[i][j]...`, canonical by construction so no fold on the print path).
/// Framing is identical to the flat printer: `name = [c0, c1, ...]\n`.
let private emitSymAware (sb: StringBuilder) (name: string) (arr: BladeArray) (et: ElemType) : unit =
    // Per-dimension descriptor in flattened order: (dimIdx, priorGroupDims, strictConst).
    let dims = ResizeArray<int * int list * int>()
    let mutable dimIdx = 0
    for ix in arr.IndexTypes do
        let a = max 1 ix.Rank
        // The interpreter print path must byte-match the compiled one; neither
        // has a wreath walk, and a triangular walk here would print a cell set
        // that no other path agrees with.
        if ix.Symmetry = SymWreath then
            failwith (Blade.IR.orbitStorageUnsupported "compact print (interp emitSymAware)"
                                                       (Blade.IR.orbitLevelsOf ix))
        let isSym =
            ix.Symmetry = SymSymmetric || ix.Symmetry = SymAntisymmetric || ix.Symmetry = SymHermitian
        let strictConst = if ix.Symmetry = SymAntisymmetric then 1 else 0
        let groupStart = dimIdx
        for comp in 0 .. a - 1 do
            let priorDims = if isSym && comp > 0 then [ groupStart .. groupStart + comp - 1 ] else []
            dims.Add(dimIdx, priorDims, strictConst)
            dimIdx <- dimIdx + 1
    let rank = dims.Count
    // Bound at one dimension: extent minus the prior group coords minus the
    // strict constant — the twin of genPrintArraySymAware's `boundAt`.
    let boundAt (d: int) (coords: int64[]) =
        let (dIdx, priorDims, strictConst) = dims.[d]
        let sub = (priorDims |> List.sumBy (fun pd -> coords.[pd])) + int64 (List.length priorDims * strictConst)
        arr.Extents.[dIdx] - sub
    if rank < 1 || rank > 8 then
        sb.Append(name).Append(" = <rank-").Append(string rank).Append(" array>").Append('\n') |> ignore
    elif rank = 2 then
        emitNested2 sb name arr et (boundAt 0 [| 0L; 0L |]) (fun i -> boundAt 1 [| i; 0L |])
    else
        sb.Append(name).Append(" = [") |> ignore
        let coords : int64[] = Array.zeroCreate rank
        let mutable first = true
        let rec loop (d: int) =
            if d = rank then
                if not first then sb.Append(", ") |> ignore
                first <- false
                sb.Append(formatCell et (readCell arr (List.ofArray coords))) |> ignore
            else
                let (dIdx, priorDims, strictConst) = dims.[d]
                let sub = (priorDims |> List.sumBy (fun pd -> coords.[pd])) + int64 (List.length priorDims * strictConst)
                let bound = arr.Extents.[dIdx] - sub
                let mutable i = 0L
                while i < bound do
                    coords.[d] <- i
                    loop (d + 1)
                    i <- i + 1L
        loop 0
        sb.Append("]").Append('\n') |> ignore

/// Flatten a ragged/nested store's leaves in row-major order into a Value list.
/// Used by the ragged auto-print, which streams the flat backing buffer.
let rec private raggedFlatValues (s: Store) : Value list =
    match s with
    | SFloat a -> a |> Array.toList |> List.map VFloat
    | SInt a -> a |> Array.toList |> List.map VInt
    | SComplex a -> a |> Array.toList |> List.map (fun (struct (r, im)) -> VComplex (r, im))
    | SBool a -> a |> Array.toList |> List.map VBool
    | SObj a -> a |> Array.toList
    | SNested rows -> rows |> Array.toList |> List.collect raggedFlatValues
    | SRagged (rows, _, _) -> rows |> Array.toList |> List.collect raggedFlatValues

/// Print a top-level ARRAY binding to `sb`, byte-matching the compiled binary.
/// Dense scalar-element arrays (flat 1-3, grid 4, placeholder) via the flat
/// emitters; symmetric/antisym/Hermitian (rank 2-8) via emitSymAware; ragged
/// literals stream their flat backing buffer; non-scalar element arrays raise
/// ArrayOpUnsupported (LATER; gate SKIPs).
let printArrayBinding (b: IRBinding) (arr: BladeArray) (sb: StringBuilder) : unit =
    match stripUnits b.Type with
    | ArrayElem arrType ->
        match elemThrough arrType.ElemType with
        | Some et when isPrintableScalarEt et ->
            match arr.Data with
            // A group_by result is SRagged too, but its auto-print is the DENSE
            // flat print over Extents=[ngroups; 0] → the empty `name = []`
            // (genPrintArrayFlat; inner extent 0 emits no cells). Route it to the
            // flat emitter below rather than streaming the backing pool.
            | SRagged (rows, lens, _) when (match b.Value with IRGroupBy _ -> false | _ -> true) ->
                // A ragged / DepIdx literal prints its rows NESTED, like every
                // other rank-2 array (CodeGen's ragged auto-print walks
                // `lens[i]` and brackets each row): `name = [[..], [..]]`. The
                // row boundary is the one thing the flat pool cannot show, and
                // a ragged store keeps each row as its own leaf here.
                sb.Append(b.Name).Append(" = [") |> ignore
                rows
                |> Array.iteri (fun i row ->
                    if i > 0 then sb.Append(", ") |> ignore
                    sb.Append("[") |> ignore
                    // `lens` is the row bound the compiled printer walks; a row
                    // store can hold more than its length (a peel or a provider
                    // read backs rows with a shared buffer).
                    raggedFlatValues row
                    |> List.truncate (int lens.[i])
                    |> List.iteri (fun j v ->
                        if j > 0 then sb.Append(", ") |> ignore
                        sb.Append(formatCell et v) |> ignore)
                    sb.Append("]") |> ignore)
                sb.Append("]").Append('\n') |> ignore
            | _ ->
                let rank = arr.Extents.Length
                // An OrbIdx (iterated-wreath) binding prints its POOL CELLS in
                // storage order -- `visitStream` order, the same ascending-lex
                // canonical sequence the compiled printer walks with
                // orb_cell_count -- with the framing every other array printer
                // uses. Checked FIRST: a wreath record is "compact" at every
                // predicate below, but its store is flat and neither emitSymAware
                // (a single shrinking simplex) nor the dense emitters (a nested
                // row walk over Extents) describe it.
                if hasWreath arrType.IndexTypes then
                    sb.Append(b.Name).Append(" = [") |> ignore
                    let cells = wreathCellCount arr
                    for k in 0 .. cells - 1 do
                        if k > 0 then sb.Append(", ") |> ignore
                        sb.Append(formatCell et (readCell arr [ int64 k ])) |> ignore
                    sb.Append("]").Append('\n') |> ignore
                elif hasSymmetry arrType.IndexTypes && rank >= 2 && rank <= 8 then
                    emitSymAware sb b.Name arr et
                elif rank < 1 then
                    sb.Append(b.Name).Append(" = <rank-0>").Append('\n') |> ignore
                elif rank <= 3 then
                    emitFlat123 sb b.Name arr et
                elif rank = 4 then
                    emitRank4 sb b.Name arr et
                else
                    sb.Append(b.Name).Append(" = <rank-").Append(string rank).Append(" array>").Append('\n')
                    |> ignore
        | _ -> raise (ArrayOpUnsupported (sprintf "print: array '%s' of non-scalar element type" b.Name))
    | _ -> raise (ArrayOpUnsupported (sprintf "print: binding '%s' is not an array type" b.Name))
