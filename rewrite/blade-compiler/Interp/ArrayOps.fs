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
let canonFold (sym: SymmetryClass) (coords: int64[]) : int64[] * int * bool =
    match sym with
    | SymNone -> (Array.copy coords, 0, false)
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

// ============================================================================
// §2 General indexing (IRIndex, plain dense) + poly-index
// ============================================================================

let private hasSymmetry (idxTys: IRIndexType list) : bool =
    idxTys
    |> List.exists (fun idx ->
        idx.Symmetry = SymSymmetric
        || idx.Symmetry = SymAntisymmetric
        || idx.Symmetry = SymHermitian)

/// Zero value for a scalar element type (implicit-zero of a strict-diagonal
/// antisymmetric access; mirrors `return T()` in the lazy compact reader).
let private zeroOfElemTy (elemTy: IRType) : Value =
    match elemThrough elemTy with
    | Some (ETFloat64 | ETFloat32) -> VFloat 0.0
    | Some (ETInt64 | ETInt32) -> VInt 0L
    | Some (ETComplex64 | ETComplex128) -> VComplex (0.0, 0.0)
    | Some ETBool -> VBool false
    | _ -> VFloat 0.0

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
    if hasSymmetry arr.IndexTypes then
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

// ============================================================================
// §7 Compound (masked product space, formalism 4.5) — construction + reads
// ============================================================================
// The value-space twin of runtime `Compound<T,RANK>` + `compound_index_t`
// (cpp/nested_array_types.hpp:133, index_types.h:235). A compound bundles the
// rank<->tuple bijection over a masked product space with a compact backing
// buffer holding only the present cells (each followed by its trailing block).
// Every read/reduce mirrors a specific C++ helper byte-for-byte (§4.7 pin-points):
//   full scalar   C((i,j))       -> data[linearize(coords)*trail + t]
//   trailing row  C((i,j), _)    -> the trailing block (Array<T,1|2>)
//   partial       C((i,_)) etc.  -> residual dense window / gather OR residual
//                                   Compound (make_partial_* family)
//   reduce/sort/…                -> walk the compact buffer (.data)

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

/// Row-major unflatten of `flat` over `extents` (into `out`).
let private unflatten (extents: int64[]) (flat: int64) (out: int64[]) : unit =
    let mutable rem = flat
    for d = extents.Length - 1 downto 0 do
        out.[d] <- rem % extents.[d]
        rem <- rem / extents.[d]

/// Partial (residual) compound indexing (formalism 4.5): pinning some axes and
/// leaving `freePos` free. Unifies the four C++ reconstitution helpers
/// (make_partial_window / _compound / _gather*) — the residual's lex enumeration
/// over the free axes, gathered from the parent via linearize, agrees cell-for-
/// cell with the shared-window slice, so ONE gather covers prefix + scattered.
///   residual rank 1  -> dense Array<T,1> (trail 1) or Array<T,2> (trail>1)
///   residual rank>=2 -> residual Compound<T,RR> (its own materialized index)
let compoundPartial (cv: CompoundValue) (pinned: (int * int64) list) (freePos: int list) : Value =
    let rr = List.length freePos
    let itrail = int cv.TrailingStride
    let freeArr = Array.ofList freePos
    let freeExtents = freeArr |> Array.map (fun p -> cv.LeadExtents.[p])
    // Recombine a free-axis coordinate tuple with the pinned axes into a full
    // parent tuple.
    let recombine (freeCoords: int64[]) : int64[] =
        let full = Array.zeroCreate cv.LeadRank
        pinned |> List.iter (fun (pos, v) -> full.[pos] <- v)
        Array.iteri (fun d p -> full.[p] <- freeCoords.[d]) freeArr
        full
    if rr = 1 then
        // Dense residual over the ONE free axis: gather present cells in
        // free-axis order (make_partial_window / _gather_dense[_trail]).
        let freeAxis = freeArr.[0]
        let ranks = ResizeArray<int>()
        let mutable v = 0L
        while v < cv.LeadExtents.[freeAxis] do
            let full = recombine [| v |]
            let off = compoundMaskOffset cv.LeadExtents full
            if cv.Mask.[int off] then ranks.Add(compoundLinearize cv full)
            v <- v + 1L
        if itrail = 1 then
            let vs = ranks.ToArray() |> Array.map (fun r -> compactCell cv.Data r)
            VArray
                { ElemType = cv.ElemType
                  IndexTypes = trailingIndexTypes cv
                  Extents = [| int64 vs.Length |]
                  Data = storeOfValues cv.ElemType vs }
        else
            // rank-2 dense {present cells, trailing extent}: each present cell's
            // whole trailing block (make_partial_window_trail / _gather_dense_trail).
            let rows =
                ranks.ToArray()
                |> Array.map (fun r ->
                    let vs = Array.init itrail (fun t -> compactCell cv.Data (r * itrail + t))
                    storeOfValues cv.ElemType vs)
            VArray
                { ElemType = cv.ElemType
                  IndexTypes = []
                  Extents = [| int64 rows.Length; cv.TrailingStride |]
                  Data = SNested rows }
    else
        // Residual COMPOUND: build a fresh sub-index over the free axes, gather
        // each present residual cell's trailing block from the parent.
        let subtotal = freeExtents |> Array.fold (*) 1L
        let submask = Array.zeroCreate (int subtotal)
        let fc = Array.zeroCreate rr
        for flat in 0 .. int subtotal - 1 do
            unflatten freeExtents (int64 flat) fc
            let full = recombine fc
            submask.[flat] <- cv.Mask.[int (compoundMaskOffset cv.LeadExtents full)]
        let (subTable, subRankOf, subCard) = buildCompoundIndex freeExtents submask
        let compact = Array.create (int subCard * itrail) VUnit
        for r in 0 .. int subCard - 1 do
            let full = recombine subTable.[r]
            let prank = compoundLinearize cv full
            for t in 0 .. itrail - 1 do
                compact.[r * itrail + t] <- compactCell cv.Data (prank * itrail + t)
        VCompound
            { ElemType = cv.ElemType
              IndexTypes = cv.IndexTypes
              LeadRank = rr
              LeadExtents = freeExtents
              Mask = submask
              Table = subTable
              RankOf = subRankOf
              Cardinality = subCard
              TrailingStride = cv.TrailingStride
              Data = storeOfValues cv.ElemType compact }

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

/// Flat ranks 1-3: `name = [c0, c1, ...]` row-major, ", "-separated, `]`, newline.
let private emitFlat123 (sb: StringBuilder) (name: string) (arr: BladeArray) (et: ElemType) : unit =
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
        let isSym =
            ix.Symmetry = SymSymmetric || ix.Symmetry = SymAntisymmetric || ix.Symmetry = SymHermitian
        let strictConst = if ix.Symmetry = SymAntisymmetric then 1 else 0
        let groupStart = dimIdx
        for comp in 0 .. a - 1 do
            let priorDims = if isSym && comp > 0 then [ groupStart .. groupStart + comp - 1 ] else []
            dims.Add(dimIdx, priorDims, strictConst)
            dimIdx <- dimIdx + 1
    let rank = dims.Count
    if rank < 1 || rank > 8 then
        sb.Append(name).Append(" = <rank-").Append(string rank).Append(" array>").Append('\n') |> ignore
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
            | SRagged _ when (match b.Value with IRGroupBy _ -> false | _ -> true) ->
                // A ragged / DepIdx literal prints as its FLAT backing buffer
                // (CodeGen's ragged auto-print streams the flat pool, row after
                // row): `name = [v0, v1, ..., v_total]`. Byte-verified vs the
                // compiled binary (index-types 018/023/077: r = [1, 2, ..., 9]).
                sb.Append(b.Name).Append(" = [") |> ignore
                raggedFlatValues arr.Data
                |> List.iteri (fun i v ->
                    if i > 0 then sb.Append(", ") |> ignore
                    sb.Append(formatCell et v) |> ignore)
                sb.Append("]").Append('\n') |> ignore
            | _ ->
                let rank = arr.Extents.Length
                if hasSymmetry arrType.IndexTypes && rank >= 2 && rank <= 8 then
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
