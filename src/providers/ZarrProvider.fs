// Blade-DSL Zarr Store Provider
// Compile-time metadata extraction from Zarr stores (v2 and v3).
//
// A Zarr store is a DIRECTORY: JSON metadata files plus one raw binary file
// per chunk — inherently multi-file. Metadata and compile-time data reads
// are pure .NET (System.Text.Json + File IO, no native library); runtime
// data I/O is deferred to generated C++ that uses only std::fstream /
// std::filesystem (no link-time dependency — contrast NetcdfProvider's
// libnetcdf).
//
// v1 scope (loud, specific rejections outside it):
//   - UNCOMPRESSED chunks only: v2 `compressor: null` / v3 a single `bytes`
//     codec. gzip/blosc/zstd/sharding/transpose are rejected BY NAME at
//     parse; the ZarrCodec seam below is where future codecs slot in
//     (decode on the F# fold path, emitted decompression on the C++ path,
//     plus Build.fs link flags recorded in ProviderSpec.LinkNeeds).
//   - Little-endian on-disk data ('<' or '|' v2 dtypes, v3 bytes codec
//     endian "little"); big-endian is rejected.
//   - C (row-major) order; v2 order "F" is rejected (v3 F-order arrives as
//     a transpose codec, already rejected).
//   - Numeric dtypes only: f4/f8 and the integer codings (collapsed to
//     ETInt64, mirroring NetcdfProvider.ncTypeToElemType).
//
// Missing chunk files read as fill_value (the Zarr contract); a missing
// chunk with a null fill_value (v2) is a loud error, never silent zeros.
// Edge chunks are stored FULL-SIZE (padded) — readers copy only the
// intersection with the array bounds.
module Blade.ZarrProvider

open System
open System.IO
open System.Text.Json
open Blade.IR
open Blade.Types

// ============================================================================
// Metadata model
// ============================================================================

/// Normalized dtype: Code is the v2-style suffix without the byte-order
/// char ("f8", "i4", "u2", ...); v3 names normalize onto the same codes.
type ZarrDtype = {
    Code: string
    Elem: ElemType
    ByteSize: int
    IsFloat: bool
}

type ZarrFill =
    | FillFloat of float
    | FillInt of int64
    /// v2 `fill_value: null` — legal until a chunk is actually missing,
    /// then a loud error (never silent data invention).
    | FillNone

/// The codec seam. v1 supports identity only; future compressed codecs
/// (gzip, blosc, ...) add arms here, decode in `decodeChunk` for the
/// compile-time path, and emit decompression calls in CppZarr for the
/// runtime path (plus link flags via ProviderSpec.LinkNeeds).
type ZarrCodec =
    | CodecIdentity

/// Triangular-decomposed layout (the `blade` attribute, spec_version 1):
/// the physical array's LEADING dimension is a packed simplex pool —
/// C(n+r-1, r) cells for "sym", C(n, r) for "antisym" — in canonical
/// ascending-lex order (== linearized_storage's linearize order == the
/// allocator's DFS pool order, differentially pinned). Trailing dimensions
/// are ordinary dense axes. Chunking the pool dimension yields contiguous
/// flat-cell ranges — exactly the decomposition the MPI backend distributes
/// (one decomposition block = one chunk). See providers/ZarrTriangularSpec.md.
type PackedGroup = {
    Sym: SymmetryClass
    Rank: int
    Extent: int64
}

/// Block ordering for the simplex-blocks decomposition: ascending-lex over
/// tile multisets (the combinadic rank), or the recursive-halving DFS
/// ("mixed-radix path") order that keeps subtrees contiguous.
type BlockOrder =
    | OrderLex
    | OrderPath

/// simplex-blocks decomposition parameters: the pool is stored as padded
/// block rows [blockCount, tile^rank] instead of one flat pool. See
/// providers/ZarrSimplexBlocksPlan.md.
type SimplexBlocksInfo = {
    /// Tile edge B over the index interval [0, extent).
    Tile: int64
    /// T = ceil(extent / Tile).
    Grid: int64
    Order: BlockOrder
}

type BladeLayout = {
    /// v1: exactly one packed group, and it is the leading dimension.
    Group: PackedGroup
    /// Trailing dense extents (must match the physical shape's tail).
    DenseDims: int64 list
    /// None: layout "packed" (flat canonical pool, decomposition
    /// "flat-ranges"). Some: layout "packed-blocks" (simplex-blocks rows).
    Blocks: SimplexBlocksInfo option
}

/// C(m, k) — cardinality arithmetic for pool validation.
let binom (m: int64) (k: int) : int64 =
    if k < 0 || m < int64 k then 0L
    else
        let mutable num = 1L
        let mutable den = 1L
        for i in 0 .. k - 1 do
            num <- num * (m - int64 i)
            den <- den * int64 (i + 1)
        num / den

/// Packed pool cardinality of a group: multiset (sym) or strict (antisym)
/// combinations.
let packedCardinality (g: PackedGroup) : int64 =
    match g.Sym with
    | SymSymmetric -> binom (g.Extent + int64 g.Rank - 1L) g.Rank
    | SymAntisymmetric -> binom g.Extent g.Rank
    | _ -> 0L

// ============================================================================
// Simplex-blocks math (decomposition scheme "simplex-blocks")
// ============================================================================
// The block grid of a rank-r simplex with T tiles IS SymIdx<r, T>: blocks are
// tile MULTISETS in both the sym and antisym cases (an antisym block with a
// repeated tile holds the strict cells inside that tile). AntisymIdx is
// special because of the diagonal in two ways here: per-tile factors are
// C(w, m) not C(w+m-1, m), and a tile of width w with multiplicity m > w
// contributes an EMPTY block (its padded row is all fill) — e.g. every
// repeated-tile block is empty when Tile = 1.
module SimplexBlocks =

    /// Combinadic rank of a canonical coordinate tuple (ascending-lex order;
    /// sorted, strictly increasing when strict). Mirrors
    /// linearized_storage::{symmetric|antisymmetric}::linearize.
    let rankOfCoords (strict: bool) (n: int64) (coords: int64[]) : int64 =
        let r = coords.Length
        // Completions with m positions remaining, next value >= v (+1 strict).
        let completions (v: int64) (m: int) : int64 =
            if strict then binom (n - v - 1L) m
            else binom ((n - v) + int64 m - 1L) m
        let mutable rank = 0L
        let mutable lo = 0L
        for k in 0 .. r - 1 do
            let mutable v = lo
            while v < coords.[k] do
                rank <- rank + completions v (r - k - 1)
                v <- v + 1L
            lo <- coords.[k] + (if strict then 1L else 0L)
        rank

    /// Inverse of rankOfCoords.
    let unrankToCoords (strict: bool) (n: int64) (r: int) (rank: int64) : int64[] =
        let completions (v: int64) (m: int) : int64 =
            if strict then binom (n - v - 1L) m
            else binom ((n - v) + int64 m - 1L) m
        let coords = Array.zeroCreate r
        let mutable rest = rank
        let mutable lo = 0L
        for k in 0 .. r - 1 do
            let mutable v = lo
            let mutable c = completions v (r - k - 1)
            while rest >= c do
                rest <- rest - c
                v <- v + 1L
                c <- completions v (r - k - 1)
            coords.[k] <- v
            lo <- v + (if strict then 1L else 0L)
        coords

    /// Number of blocks: tile multisets of size r over T tiles (both
    /// symmetries — blocks are always multisets).
    let blockCount (r: int) (T: int64) : int64 = binom (T + int64 r - 1L) r

    /// Width of tile t over [0, n) with edge B (the last tile may be ragged).
    let tileWidth (n: int64) (B: int64) (t: int64) : int64 = min B (n - t * B)

    /// Cells in a block (tile multiset, ascending): group tiles by value;
    /// a tile of width w with multiplicity m contributes C(w+m-1, m) (sym)
    /// or C(w, m) (antisym — ZERO when m > w: the empty diagonal blocks).
    let blockCellCount (strict: bool) (n: int64) (B: int64) (tiles: int64[]) : int64 =
        tiles
        |> Array.countBy id
        |> Array.fold (fun acc (t, m) ->
            let w = tileWidth n B t
            let f = if strict then binom w m else binom (w + int64 m - 1L) m
            acc * f) 1L

    /// The format's fixed row width: B^r bounds every block's cell count
    /// (each per-tile factor is <= w^m <= B^m).
    let maxBlockCells (r: int) (B: int64) : int64 =
        let mutable acc = 1L
        for _ in 1 .. r do acc <- acc * B
        acc

    /// A block's cells in absolute ascending-lex order (the within-block
    /// canonical order both the writer and the emitted C++ use): branch-free
    /// bounds i_k in [max(tile_k*B, i_{k-1}+strict), min((tile_k+1)*B, n)).
    let enumBlockCells (strict: bool) (n: int64) (B: int64) (tiles: int64[]) : seq<int64[]> =
        let r = tiles.Length
        let rec go (k: int) (prev: int64) (acc: int64 list) : seq<int64[]> =
            seq {
                if k = r then
                    yield (List.rev acc |> Array.ofList)
                else
                    let tileLo = tiles.[k] * B
                    let lo =
                        if k = 0 then tileLo
                        else max tileLo (prev + (if strict then 1L else 0L))
                    let hi = min ((tiles.[k] + 1L) * B) n
                    for i in lo .. hi - 1L do
                        yield! go (k + 1) i (i :: acc)
            }
        go 0 -1L []

    /// Tile multisets in recursive-halving DFS ("mixed-radix path") order:
    /// split [a, b) at the midpoint; children ordered all-low first
    /// (j = r down to 0 tiles in the low half), low part major within a
    /// child. Requires a power-of-two grid so splits stay clean.
    let rec private pathMultisets (a: int64) (b: int64) (r: int) : seq<int64 list> =
        seq {
            if r = 0 then yield []
            elif b - a = 1L then yield List.replicate r a
            else
                let m = a + (b - a) / 2L
                for j in r .. -1 .. 0 do
                    for low in pathMultisets a m j do
                        for high in pathMultisets m b (r - j) do
                            yield low @ high
        }

    let isPowerOfTwo (x: int64) = x > 0L && (x &&& (x - 1L)) = 0L

    /// Physical row of each block under "path" order:
    /// pathRows.[lexBlockRank] = row index in the store.
    let pathRows (r: int) (T: int64) : int64[] =
        if not (isPowerOfTwo T) then
            failwithf "simplex-blocks path order requires a power-of-two grid (got %d)" T
        let rows = Array.zeroCreate (int (blockCount r T))
        pathMultisets 0L T r
        |> Seq.iteri (fun i ms ->
            rows.[int (rankOfCoords false T (Array.ofList ms))] <- int64 i)
        rows

    /// (physical padded cell count, canonical pool cell count) — the padding
    /// overhead report for a configuration.
    let paddingReport (strict: bool) (n: int64) (B: int64) (r: int) : int64 * int64 =
        let T = (n + B - 1L) / B
        let phys = blockCount r T * maxBlockCells r B
        let pool =
            if strict then binom n r else binom (n + int64 r - 1L) r
        (phys, pool)

    /// physCellIdx -> poolCellIdx map (cells only, trailing dims excluded);
    /// -1 marks padding. physCellIdx = physicalRow * B^r + localOffset,
    /// where localOffset counts the block's cells in absolute ascending-lex
    /// order. Shared by the F# writer (inverted) and reader so the two sides
    /// cannot drift.
    let blocksCellMap (strict: bool) (n: int64) (B: int64) (r: int) (order: BlockOrder) : int[] =
        let T = (n + B - 1L) / B
        let nBlocks = blockCount r T
        let rowW = maxBlockCells r B
        let rowOf =
            match order with
            | OrderLex -> fun (b: int64) -> b
            | OrderPath ->
                let rows = pathRows r T
                fun (b: int64) -> rows.[int b]
        let map = Array.create (int (nBlocks * rowW)) -1
        for b in 0L .. nBlocks - 1L do
            let tiles = unrankToCoords false T r b
            let row = rowOf b
            let mutable local = 0L
            for cell in enumBlockCells strict n B tiles do
                let pool = rankOfCoords strict n cell
                map.[int (row * rowW + local)] <- int pool
                local <- local + 1L
        map

let decodeChunk (codec: ZarrCodec) (raw: byte[]) : byte[] =
    match codec with
    | CodecIdentity -> raw

type ZarrArrayMeta = {
    Name: string
    /// Absolute path of the array directory (metadata + chunk files).
    ArrayDir: string
    Shape: int64 list
    Chunks: int64 list
    Dtype: ZarrDtype
    /// xarray `_ARRAY_DIMENSIONS` (v2 .zattrs) / `dimension_names` (v3).
    DimNames: string list option
    FillValue: ZarrFill
    Codec: ZarrCodec
    /// Triangular-decomposed layout from the `blade` attribute; None for
    /// ordinary dense arrays.
    Blade: BladeLayout option
    /// 2 | 3
    Version: int
    /// Chunk-key separator ("." v2 default, "/" v3 default).
    ChunkKeySep: string
    /// Chunk-key prefix ("" for v2 and v3 "v2" encoding; "c" for v3 default).
    ChunkKeyPrefix: string
}

type ZarrStore = {
    Path: string
    Version: int
    Arrays: ZarrArrayMeta list
}

/// A variable's payload read at compile time: dimension extents plus the
/// row-major flat buffer. Mirrors NetcdfProvider.NcVarData.
type ZarrVarData = {
    DimLengths: int list
    Payload: ZarrPayload
}
and ZarrPayload =
    | ZFloats of float[]
    | ZInts of int64[]

// ============================================================================
// Dtype tables
// ============================================================================

/// v2-style normalized code -> dtype record. The integer collapse to
/// ETInt64 mirrors ncTypeToElemType.
let private dtypeOfCode (code: string) : Result<ZarrDtype, string> =
    match code with
    | "f4" -> Ok { Code = "f4"; Elem = ETFloat32; ByteSize = 4; IsFloat = true }
    | "f8" -> Ok { Code = "f8"; Elem = ETFloat64; ByteSize = 8; IsFloat = true }
    | "i1" -> Ok { Code = "i1"; Elem = ETInt64; ByteSize = 1; IsFloat = false }
    | "i2" -> Ok { Code = "i2"; Elem = ETInt64; ByteSize = 2; IsFloat = false }
    | "i4" -> Ok { Code = "i4"; Elem = ETInt64; ByteSize = 4; IsFloat = false }
    | "i8" -> Ok { Code = "i8"; Elem = ETInt64; ByteSize = 8; IsFloat = false }
    | "u1" -> Ok { Code = "u1"; Elem = ETInt64; ByteSize = 1; IsFloat = false }
    | "u2" -> Ok { Code = "u2"; Elem = ETInt64; ByteSize = 2; IsFloat = false }
    | "u4" -> Ok { Code = "u4"; Elem = ETInt64; ByteSize = 4; IsFloat = false }
    | "u8" -> Ok { Code = "u8"; Elem = ETInt64; ByteSize = 8; IsFloat = false }
    | other -> Error (sprintf "unsupported dtype '%s' (numeric f4/f8/i*/u* only in v1 — bool, complex, datetime and string dtypes are not supported)" other)

/// v2 dtype string ("<f8", "|i1", ">f4"): byte-order char + code.
let zarrDtypeV2 (dtype: string) : Result<ZarrDtype, string> =
    if String.IsNullOrEmpty dtype || dtype.Length < 2 then
        Error (sprintf "malformed v2 dtype '%s'" dtype)
    else
        match dtype.[0] with
        | '<' | '|' -> dtypeOfCode (dtype.Substring 1)
        | '>' -> Error (sprintf "big-endian dtype '%s' is not supported (little-endian stores only)" dtype)
        | _ -> Error (sprintf "malformed v2 dtype '%s' (expected a byte-order prefix '<', '|' or '>')" dtype)

/// v3 data_type name ("float64", "int32", ...).
let zarrDtypeV3 (name: string) : Result<ZarrDtype, string> =
    match name with
    | "float32" -> dtypeOfCode "f4"
    | "float64" -> dtypeOfCode "f8"
    | "int8" -> dtypeOfCode "i1"
    | "int16" -> dtypeOfCode "i2"
    | "int32" -> dtypeOfCode "i4"
    | "int64" -> dtypeOfCode "i8"
    | "uint8" -> dtypeOfCode "u1"
    | "uint16" -> dtypeOfCode "u2"
    | "uint32" -> dtypeOfCode "u4"
    | "uint64" -> dtypeOfCode "u8"
    | other -> Error (sprintf "unsupported data_type '%s' (numeric float32/float64/int*/uint* only in v1)" other)

// ============================================================================
// JSON parsing helpers
// ============================================================================

let private tryProp (el: JsonElement) (name: string) : JsonElement option =
    match el.TryGetProperty name with
    | true, v -> Some v
    | _ -> None

let private jsonInt64List (el: JsonElement) : int64 list =
    el.EnumerateArray() |> Seq.map (fun e -> e.GetInt64()) |> List.ofSeq

let private jsonStringList (el: JsonElement) : string list =
    el.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> List.ofSeq

/// fill_value: number, "NaN"/"Infinity"/"-Infinity" strings, or null.
let private parseFill (where_: string) (isFloat: bool) (el: JsonElement option) : Result<ZarrFill, string> =
    match el with
    | None -> Ok FillNone
    | Some e ->
        match e.ValueKind with
        | JsonValueKind.Null -> Ok FillNone
        | JsonValueKind.Number ->
            if isFloat then Ok (FillFloat (e.GetDouble()))
            else
                match e.TryGetInt64() with
                | true, n -> Ok (FillInt n)
                | _ -> Ok (FillInt (int64 (e.GetDouble())))
        | JsonValueKind.String ->
            (match e.GetString(), isFloat with
             | "NaN", true -> Ok (FillFloat nan)
             | "Infinity", true -> Ok (FillFloat infinity)
             | "-Infinity", true -> Ok (FillFloat (-infinity))
             | s, _ -> Error (sprintf "%s: unsupported fill_value '%s'" where_ s))
        | _ -> Error (sprintf "%s: unsupported fill_value kind %A" where_ e.ValueKind)

/// Parse + validate the `blade` layout attribute (spec_version 1) against
/// the physical shape. `attrs` is the attributes object (the v2 .zattrs
/// root / v3 `attributes` value); an absent "blade" key is an ordinary
/// dense array (Ok None). All violations are loud and specific.
let parseBladeLayout (where_: string) (shape: int64 list) (attrs: JsonElement option) : Result<BladeLayout option, string> =
    match attrs |> Option.bind (fun a -> tryProp a "blade") with
    | None -> Ok None
    | Some b ->
        let strOf name dflt =
            tryProp b name
            |> Option.map (fun v -> if v.ValueKind = JsonValueKind.String then v.GetString() else "")
            |> Option.defaultValue dflt
        let specVersion =
            tryProp b "spec_version"
            |> Option.map (fun v -> if v.ValueKind = JsonValueKind.Number then v.GetInt32() else -1)
            |> Option.defaultValue -1
        let layoutStr = strOf "layout" ""
        if specVersion <> 1 then
            Error (sprintf "%s: blade.spec_version %d is not supported (this reader implements spec_version 1)" where_ specVersion)
        elif layoutStr <> "packed" && layoutStr <> "packed-blocks" then
            Error (sprintf "%s: blade.layout '%s' is not supported ('packed' or 'packed-blocks')" where_ layoutStr)
        elif strOf "order" "ascending-lex" <> "ascending-lex" then
            Error (sprintf "%s: blade.order '%s' is not supported ('ascending-lex' only — the pinned linearized_storage order)" where_ (strOf "order" ""))
        else
        match tryProp b "index_types" with
        | Some its when its.ValueKind = JsonValueKind.Array && its.GetArrayLength() >= 1 ->
            let entries = its.EnumerateArray() |> List.ofSeq
            let parseEntry (i: int) (e: JsonElement) : Result<Choice<PackedGroup, int64>, string> =
                let kind =
                    tryProp e "kind"
                    |> Option.map (fun v -> if v.ValueKind = JsonValueKind.String then v.GetString() else "")
                    |> Option.defaultValue ""
                let extent =
                    tryProp e "extent"
                    |> Option.map (fun v -> if v.ValueKind = JsonValueKind.Number then v.GetInt64() else -1L)
                    |> Option.defaultValue -1L
                match kind with
                | "dense" ->
                    if extent > 0L then Ok (Choice2Of2 extent)
                    else Error (sprintf "%s: blade.index_types[%d]: dense entry needs a positive extent" where_ i)
                | "sym" | "antisym" ->
                    let rank =
                        tryProp e "rank"
                        |> Option.map (fun v -> if v.ValueKind = JsonValueKind.Number then v.GetInt32() else -1)
                        |> Option.defaultValue -1
                    if rank < 2 then Error (sprintf "%s: blade.index_types[%d]: packed group needs rank >= 2" where_ i)
                    elif extent <= 0L then Error (sprintf "%s: blade.index_types[%d]: packed group needs a positive extent" where_ i)
                    else
                        let sym = if kind = "sym" then SymSymmetric else SymAntisymmetric
                        Ok (Choice1Of2 { Sym = sym; Rank = rank; Extent = extent })
                | "herm" ->
                    Error (sprintf "%s: blade.index_types[%d]: kind 'herm' is reserved (constraint-coupled cells) and not supported in spec_version 1" where_ i)
                | other ->
                    Error (sprintf "%s: blade.index_types[%d]: unknown kind '%s' (sym | antisym | dense)" where_ i other)
            let rec collect i acc =
                if i >= entries.Length then Ok (List.rev acc)
                else
                    match parseEntry i entries.[i] with
                    | Ok c -> collect (i + 1) (c :: acc)
                    | Error e -> Error e
            match collect 0 [] with
            | Error e -> Error e
            | Ok parsed ->
                match parsed with
                | Choice1Of2 group :: rest ->
                    if rest |> List.exists (function Choice1Of2 _ -> true | _ -> false) then
                        Error (sprintf "%s: blade layout supports exactly ONE packed group in spec_version 1 (and it must be leading)" where_)
                    else
                        let denseDims = rest |> List.map (function Choice2Of2 d -> d | _ -> 0L)
                        let card = packedCardinality group
                        if layoutStr = "packed" then
                            match shape with
                            | pool :: tail when pool = card && tail = denseDims ->
                                Ok (Some { Group = group; DenseDims = denseDims; Blocks = None })
                            | pool :: _ when pool <> card ->
                                Error (sprintf "%s: blade packed group (%s, rank %d, extent %d) has cardinality %d but the pool dimension is %d — a corrupt or mislabeled store"
                                           where_ (if group.Sym = SymSymmetric then "sym" else "antisym") group.Rank group.Extent card (List.head shape))
                            | _ ->
                                Error (sprintf "%s: blade dense dims %A do not match the physical trailing shape %A" where_ denseDims (List.tail shape))
                        else
                            // packed-blocks: shape = [blockCount, tile^rank] @ dense,
                            // parameters from the decomposition object.
                            match tryProp b "decomposition" with
                            | None ->
                                Error (sprintf "%s: blade.layout 'packed-blocks' requires a decomposition object (scheme 'simplex-blocks')" where_)
                            | Some d ->
                                let dStr name dflt =
                                    tryProp d name
                                    |> Option.map (fun v -> if v.ValueKind = JsonValueKind.String then v.GetString() else "")
                                    |> Option.defaultValue dflt
                                let dInt name =
                                    tryProp d name
                                    |> Option.map (fun v -> if v.ValueKind = JsonValueKind.Number then v.GetInt64() else -1L)
                                let scheme = dStr "scheme" ""
                                if scheme <> "simplex-blocks" then
                                    Error (sprintf "%s: blade decomposition scheme '%s' is not supported for packed-blocks ('simplex-blocks' only)" where_ scheme)
                                else
                                match dInt "tile", dInt "grid" with
                                | Some tile, Some grid when tile > 0L && grid > 0L ->
                                    let expectGrid = (group.Extent + tile - 1L) / tile
                                    let orderStr = dStr "block_order" "ascending-lex"
                                    let orderRes =
                                        match orderStr with
                                        | "ascending-lex" -> Ok OrderLex
                                        | "path" ->
                                            if SimplexBlocks.isPowerOfTwo grid then
                                                match dInt "depth" with
                                                | Some dep when (1L <<< int dep) <> grid ->
                                                    Error (sprintf "%s: blade decomposition depth %d does not match grid %d (expected log2)" where_ dep grid)
                                                | _ -> Ok OrderPath
                                            else Error (sprintf "%s: blade block_order 'path' requires a power-of-two grid (got %d)" where_ grid)
                                        | other -> Error (sprintf "%s: blade block_order '%s' is not supported ('ascending-lex' or 'path')" where_ other)
                                    if grid <> expectGrid then
                                        Error (sprintf "%s: blade decomposition grid %d does not match ceil(extent %d / tile %d) = %d" where_ grid group.Extent tile expectGrid)
                                    else
                                    match orderRes with
                                    | Error e -> Error e
                                    | Ok order ->
                                        let nBlocks = SimplexBlocks.blockCount group.Rank grid
                                        let rowW = SimplexBlocks.maxBlockCells group.Rank tile
                                        match shape with
                                        | b0 :: b1 :: tail when b0 = nBlocks && b1 = rowW && tail = denseDims ->
                                            Ok (Some { Group = group
                                                       DenseDims = denseDims
                                                       Blocks = Some { Tile = tile; Grid = grid; Order = order } })
                                        | _ ->
                                            Error (sprintf "%s: packed-blocks physical shape %A does not match [blockCount %d, tile^rank %d] @ dense %A"
                                                       where_ shape nBlocks rowW denseDims)
                                | _ ->
                                    Error (sprintf "%s: blade decomposition needs positive integer tile and grid" where_)
                | _ ->
                    Error (sprintf "%s: blade layout's FIRST index_types entry must be the packed group (sym/antisym) in spec_version 1" where_)
        | _ -> Error (sprintf "%s: blade layout is missing index_types" where_)

// ============================================================================
// Array metadata parsers (pure: JSON text in, Result out — unit-testable)
// ============================================================================

/// Parse a v2 array's `.zarray` (+ optional `.zattrs` for _ARRAY_DIMENSIONS).
let parseArrayMetaV2 (name: string) (arrayDir: string) (zarrayJson: string) (zattrsJson: string option) : Result<ZarrArrayMeta, string> =
    try
        use doc = JsonDocument.Parse zarrayJson
        let root = doc.RootElement
        let where_ = sprintf "array '%s'" name
        match tryProp root "shape", tryProp root "chunks", tryProp root "dtype" with
        | Some shapeEl, Some chunksEl, Some dtypeEl ->
            let shape = jsonInt64List shapeEl
            let chunks = jsonInt64List chunksEl
            if shape.Length <> chunks.Length then
                Error (sprintf "%s: shape rank %d != chunks rank %d" where_ shape.Length chunks.Length)
            elif chunks |> List.exists (fun c -> c <= 0L) then
                Error (sprintf "%s: non-positive chunk extent" where_)
            else
            // Compression gate (the v1 uncompressed contract).
            let compressorErr =
                match tryProp root "compressor" with
                | None | Some _ when (match tryProp root "compressor" with
                                      | Some c -> c.ValueKind = JsonValueKind.Null
                                      | None -> true) -> None
                | Some c ->
                    let cid =
                        match tryProp c "id" with
                        | Some idEl when idEl.ValueKind = JsonValueKind.String -> idEl.GetString()
                        | _ -> "<unknown>"
                    Some (sprintf "%s uses compressor '%s' — compressed Zarr stores are not supported in v1 (uncompressed only); see the ZarrCodec extension point" where_ cid)
                | None -> None
            match compressorErr with
            | Some e -> Error e
            | None ->
            let filtersErr =
                match tryProp root "filters" with
                | None -> None
                | Some f when f.ValueKind = JsonValueKind.Null -> None
                | Some f when f.ValueKind = JsonValueKind.Array && f.GetArrayLength() = 0 -> None
                | Some _ -> Some (sprintf "%s uses filters — not supported in v1 (uncompressed, unfiltered only)" where_)
            match filtersErr with
            | Some e -> Error e
            | None ->
            let orderErr =
                match tryProp root "order" with
                | Some o when o.ValueKind = JsonValueKind.String && o.GetString() = "C" -> None
                | Some o when o.ValueKind = JsonValueKind.String -> Some (sprintf "%s has order '%s' — only C (row-major) order is supported" where_ (o.GetString()))
                | _ -> None  // missing order: tolerate, C assumed
            match orderErr with
            | Some e -> Error e
            | None ->
            match zarrDtypeV2 (dtypeEl.GetString()) with
            | Error e -> Error (sprintf "%s: %s" where_ e)
            | Ok dt ->
            match parseFill where_ dt.IsFloat (tryProp root "fill_value") with
            | Error e -> Error e
            | Ok fill ->
            let sep =
                match tryProp root "dimension_separator" with
                | Some s when s.ValueKind = JsonValueKind.String -> s.GetString()
                | _ -> "."
            if sep <> "." && sep <> "/" then
                Error (sprintf "%s: unsupported dimension_separator '%s'" where_ sep)
            else
            // .zattrs carries both the xarray dim names and the blade layout.
            let (dimNames, bladeRes) =
                match zattrsJson with
                | None -> (None, Ok None)
                | Some txt ->
                    try
                        use adoc = JsonDocument.Parse txt
                        let root = adoc.RootElement
                        let dn =
                            match tryProp root "_ARRAY_DIMENSIONS" with
                            | Some arr when arr.ValueKind = JsonValueKind.Array -> Some (jsonStringList arr)
                            | _ -> None
                        (dn, parseBladeLayout where_ shape (Some root))
                    with _ -> (None, Ok None)
            match bladeRes with
            | Error e -> Error e
            | Ok blade ->
            Ok { Name = name; ArrayDir = arrayDir; Shape = shape; Chunks = chunks
                 Dtype = dt; DimNames = dimNames; FillValue = fill; Codec = CodecIdentity
                 Blade = blade; Version = 2; ChunkKeySep = sep; ChunkKeyPrefix = "" }
        | _ -> Error (sprintf "array '%s': .zarray is missing shape/chunks/dtype" name)
    with ex ->
        Error (sprintf "array '%s': malformed .zarray JSON: %s" name ex.Message)

/// Parse a v3 array's `zarr.json`.
let parseArrayMetaV3 (name: string) (arrayDir: string) (zarrJson: string) : Result<ZarrArrayMeta, string> =
    try
        use doc = JsonDocument.Parse zarrJson
        let root = doc.RootElement
        let where_ = sprintf "array '%s'" name
        let nodeType =
            match tryProp root "node_type" with
            | Some nt when nt.ValueKind = JsonValueKind.String -> nt.GetString()
            | _ -> ""
        if nodeType <> "array" then Error (sprintf "%s: zarr.json node_type is '%s', expected 'array'" where_ nodeType)
        else
        match tryProp root "shape", tryProp root "data_type", tryProp root "chunk_grid" with
        | Some shapeEl, Some dtypeEl, Some gridEl ->
            let shape = jsonInt64List shapeEl
            let gridName =
                match tryProp gridEl "name" with
                | Some n when n.ValueKind = JsonValueKind.String -> n.GetString()
                | _ -> ""
            if gridName <> "regular" then
                Error (sprintf "%s: chunk_grid '%s' is not supported (regular only)" where_ gridName)
            else
            let chunks =
                tryProp gridEl "configuration"
                |> Option.bind (fun c -> tryProp c "chunk_shape")
                |> Option.map jsonInt64List
            match chunks with
            | None -> Error (sprintf "%s: chunk_grid.configuration.chunk_shape missing" where_)
            | Some chunks when chunks.Length <> shape.Length ->
                Error (sprintf "%s: shape rank %d != chunk_shape rank %d" where_ shape.Length chunks.Length)
            | Some chunks when chunks |> List.exists (fun c -> c <= 0L) ->
                Error (sprintf "%s: non-positive chunk extent" where_)
            | Some chunks ->
            // Codec gate: exactly one `bytes` codec, little-endian.
            let codecErr =
                match tryProp root "codecs" with
                | None -> None  // lenient: absent codecs = raw bytes
                | Some cs when cs.ValueKind = JsonValueKind.Array ->
                    let arr = cs.EnumerateArray() |> List.ofSeq
                    let names =
                        arr |> List.map (fun c ->
                            match tryProp c "name" with
                            | Some n when n.ValueKind = JsonValueKind.String -> n.GetString()
                            | _ -> "<unknown>")
                    match names with
                    | [] -> None
                    | ["bytes"] ->
                        let endian =
                            tryProp arr.[0] "configuration"
                            |> Option.bind (fun cfg -> tryProp cfg "endian")
                            |> Option.map (fun e -> e.GetString())
                            |> Option.defaultValue "little"
                        if endian = "little" then None
                        else Some (sprintf "%s: big-endian bytes codec is not supported (little-endian stores only)" where_)
                    | _ ->
                        let bad = names |> List.filter (fun n -> n <> "bytes")
                        Some (sprintf "%s uses codec(s) %s — compressed/transformed Zarr stores are not supported in v1 (a single little-endian 'bytes' codec only); see the ZarrCodec extension point"
                                  where_ (bad @ (if List.isEmpty bad then names else []) |> List.map (sprintf "'%s'") |> String.concat ", "))
                | Some _ -> Some (sprintf "%s: malformed codecs" where_)
            match codecErr with
            | Some e -> Error e
            | None ->
            match zarrDtypeV3 (dtypeEl.GetString()) with
            | Error e -> Error (sprintf "%s: %s" where_ e)
            | Ok dt ->
            match parseFill where_ dt.IsFloat (tryProp root "fill_value") with
            | Error e -> Error e
            | Ok fill ->
            let (prefix, sep) =
                match tryProp root "chunk_key_encoding" with
                | None -> ("c", "/")
                | Some cke ->
                    let ename =
                        match tryProp cke "name" with
                        | Some n when n.ValueKind = JsonValueKind.String -> n.GetString()
                        | _ -> "default"
                    let csep dflt =
                        tryProp cke "configuration"
                        |> Option.bind (fun c -> tryProp c "separator")
                        |> Option.map (fun s -> s.GetString())
                        |> Option.defaultValue dflt
                    match ename with
                    | "default" -> ("c", csep "/")
                    | "v2" -> ("", csep ".")
                    | other -> (other, "!")  // marker checked below
            if sep = "!" then
                Error (sprintf "%s: unsupported chunk_key_encoding '%s'" where_ prefix)
            elif sep <> "." && sep <> "/" then
                Error (sprintf "%s: unsupported chunk-key separator '%s'" where_ sep)
            else
            let dimNames =
                match tryProp root "dimension_names" with
                | Some dn when dn.ValueKind = JsonValueKind.Array ->
                    let names = dn.EnumerateArray() |> Seq.map (fun e -> if e.ValueKind = JsonValueKind.String then e.GetString() else "") |> List.ofSeq
                    if names |> List.exists String.IsNullOrEmpty then None else Some names
                | _ -> None
            match parseBladeLayout where_ shape (tryProp root "attributes") with
            | Error e -> Error e
            | Ok blade ->
            Ok { Name = name; ArrayDir = arrayDir; Shape = shape; Chunks = chunks
                 Dtype = dt; DimNames = dimNames; FillValue = fill; Codec = CodecIdentity
                 Blade = blade; Version = 3; ChunkKeySep = sep; ChunkKeyPrefix = prefix }
        | _ -> Error (sprintf "array '%s': zarr.json is missing shape/data_type/chunk_grid" name)
    with ex ->
        Error (sprintf "array '%s': malformed zarr.json: %s" name ex.Message)

// ============================================================================
// Store discovery (the multi-file walk)
// ============================================================================

let private isValidIdent (s: string) =
    s.Length > 0
    && (Char.IsLetter s.[0] || s.[0] = '_')
    && s |> Seq.forall (fun ch -> Char.IsLetterOrDigit ch || ch = '_')

let private readTextOpt (path: string) : string option =
    if File.Exists path then Some (File.ReadAllText path) else None

let private loadArrayV2 (name: string) (arrayDir: string) : ZarrArrayMeta =
    let zarray = File.ReadAllText (Path.Combine(arrayDir, ".zarray"))
    let zattrs = readTextOpt (Path.Combine(arrayDir, ".zattrs"))
    match parseArrayMetaV2 name arrayDir zarray zattrs with
    | Ok m -> m
    | Error e -> failwithf "Zarr store: %s" e

let private loadArrayV3 (name: string) (arrayDir: string) : ZarrArrayMeta =
    let zj = File.ReadAllText (Path.Combine(arrayDir, "zarr.json"))
    match parseArrayMetaV3 name arrayDir zj with
    | Ok m -> m
    | Error e -> failwithf "Zarr store: %s" e

/// Load all metadata from a Zarr store directory. Recognizes, in order:
/// v3 (`zarr.json` group or single array), v2 group (`.zgroup` + array
/// subdirectories), v2 single array (`.zarray`). Array names must be valid
/// Blade identifiers (they become struct field names). One level of
/// nesting only in v1 (arrays directly under the store root).
let load (path: string) : ZarrStore =
    let full = Path.GetFullPath path
    let checkName (n: string) =
        if not (isValidIdent n) then
            failwithf "Zarr store '%s': array name '%s' is not a valid identifier (it becomes a struct field)" path n
    let leafName () =
        let n = Path.GetFileName (full.TrimEnd [| '/' ; '\\' |])
        let n = if n.EndsWith ".zarr" then n.Substring(0, n.Length - 5) else n
        if isValidIdent n then n else "data"
    let v3Path = Path.Combine(full, "zarr.json")
    if File.Exists v3Path then
        use doc = JsonDocument.Parse (File.ReadAllText v3Path)
        let nodeType =
            match doc.RootElement.TryGetProperty "node_type" with
            | true, nt when nt.ValueKind = JsonValueKind.String -> nt.GetString()
            | _ -> ""
        match nodeType with
        | "array" ->
            let name = leafName ()
            { Path = full; Version = 3; Arrays = [ loadArrayV3 name full ] }
        | "group" ->
            let arrays =
                Directory.GetDirectories full
                |> Array.filter (fun d -> File.Exists (Path.Combine(d, "zarr.json")))
                |> Array.choose (fun d ->
                    use adoc = JsonDocument.Parse (File.ReadAllText (Path.Combine(d, "zarr.json")))
                    match adoc.RootElement.TryGetProperty "node_type" with
                    | true, nt when nt.ValueKind = JsonValueKind.String && nt.GetString() = "array" ->
                        let name = Path.GetFileName d
                        checkName name
                        Some (loadArrayV3 name d)
                    | _ -> None)
                |> Array.sortBy (fun a -> a.Name)
                |> List.ofArray
            { Path = full; Version = 3; Arrays = arrays }
        | other -> failwithf "Zarr store '%s': zarr.json node_type '%s' is neither 'group' nor 'array'" path other
    elif File.Exists (Path.Combine(full, ".zgroup")) then
        let arrays =
            Directory.GetDirectories full
            |> Array.filter (fun d -> File.Exists (Path.Combine(d, ".zarray")))
            |> Array.map (fun d ->
                let name = Path.GetFileName d
                checkName name
                loadArrayV2 name d)
            |> Array.sortBy (fun a -> a.Name)
            |> List.ofArray
        { Path = full; Version = 2; Arrays = arrays }
    elif File.Exists (Path.Combine(full, ".zarray")) then
        let name = leafName ()
        { Path = full; Version = 2; Arrays = [ loadArrayV2 name full ] }
    else
        failwithf "'%s' is not a Zarr store (no zarr.json, .zgroup, or .zarray found)" path

let tryFindArray (store: ZarrStore) (varName: string) : ZarrArrayMeta option =
    store.Arrays |> List.tryFind (fun a -> a.Name = varName)

// ============================================================================
// Chunk-grid math
// ============================================================================

/// Chunk key for a grid coordinate: v2 "i.j" (rank-0: "0"); v3 default
/// "c/i/j" (rank-0: "c"); v3 "v2"-encoding like v2.
let chunkKey (meta: ZarrArrayMeta) (coords: int64 list) : string =
    let joined = coords |> List.map string |> String.concat meta.ChunkKeySep
    match meta.ChunkKeyPrefix, coords with
    | "", [] -> "0"
    | "", _ -> joined
    | p, [] -> p
    | p, _ -> p + meta.ChunkKeySep + joined

/// Per-dimension chunk-grid extents (ceil-div; rank-0 -> []).
let gridDims (shape: int64 list) (chunks: int64 list) : int64 list =
    List.map2 (fun s c -> (s + c - 1L) / c) shape chunks

/// All grid coordinates, row-major (rank-0 -> [[]], the single scalar chunk).
let gridCoords (shape: int64 list) (chunks: int64 list) : int64 list list =
    let rec cartesian dims =
        match dims with
        | [] -> [ [] ]
        | d :: rest ->
            [ for i in 0L .. d - 1L do
                for tail in cartesian rest -> i :: tail ]
    cartesian (gridDims shape chunks)

/// Row-major strides for a shape (innermost stride 1).
let rowMajorStrides (lens: int list) : int list =
    let rec go = function
        | [] -> []
        | [_] -> [1]
        | _ :: rest ->
            let tail = go rest
            (List.head tail * List.head rest) :: tail
    go lens

// ============================================================================
// Compile-time data read (chunk assembly for the static fold + tests)
// ============================================================================

let private decodeFloatCell (code: string) (b: byte[]) (off: int) : float =
    match code with
    | "f8" -> BitConverter.ToDouble(b, off)
    | "f4" -> float (BitConverter.ToSingle(b, off))
    | c -> failwithf "decodeFloatCell: not a float code '%s'" c

let private decodeIntCell (code: string) (b: byte[]) (off: int) : int64 =
    match code with
    | "i8" -> BitConverter.ToInt64(b, off)
    | "i4" -> int64 (BitConverter.ToInt32(b, off))
    | "i2" -> int64 (BitConverter.ToInt16(b, off))
    | "i1" -> int64 (sbyte b.[off])
    | "u1" -> int64 b.[off]
    | "u2" -> int64 (BitConverter.ToUInt16(b, off))
    | "u4" -> int64 (BitConverter.ToUInt32(b, off))
    | "u8" -> int64 (BitConverter.ToUInt64(b, off))
    | c -> failwithf "decodeIntCell: not an integer code '%s'" c

/// Read an array's full payload by assembling its chunks. Missing chunk
/// files fill with fill_value (loud error when fill_value is null); chunk
/// files must be exactly full-chunk-sized (edge chunks are stored padded).
let readArrayData (meta: ZarrArrayMeta) : Result<ZarrVarData, string> =
    try
        let shape = meta.Shape |> List.map int
        let chunks = meta.Chunks |> List.map int
        let rank = shape.Length
        let total = shape |> List.fold (*) 1
        let chunkCount = chunks |> List.fold (*) 1
        let bs = meta.Dtype.ByteSize
        let gStr = rowMajorStrides shape
        let cStr = rowMajorStrides chunks
        let shapeArr = List.toArray shape
        let chunksArr = List.toArray chunks
        let gStrArr = List.toArray gStr
        let cStrArr = List.toArray cStr
        let outF = if meta.Dtype.IsFloat then Array.zeroCreate<float> (max total 1) else [||]
        let outI = if meta.Dtype.IsFloat then [||] else Array.zeroCreate<int64> (max total 1)
        for coords in gridCoords meta.Shape meta.Chunks do
            let coordsArr = coords |> List.map int |> List.toArray
            let key = chunkKey meta coords
            let file = Path.Combine(meta.ArrayDir, key.Replace('/', Path.DirectorySeparatorChar))
            let chunkBytes =
                if File.Exists file then
                    let raw = decodeChunk meta.Codec (File.ReadAllBytes file)
                    if raw.Length <> chunkCount * bs then
                        failwithf "chunk '%s' of array '%s' is %d bytes, expected %d — a compressed or corrupt store?"
                            key meta.Name raw.Length (chunkCount * bs)
                    Some raw
                else
                    match meta.FillValue with
                    | FillNone ->
                        failwithf "chunk '%s' of array '%s' is missing and fill_value is null — refusing to invent data" key meta.Name
                    | _ -> None
            // Copy the chunk's intersection with the array bounds (edge
            // chunks are stored full-size; the overhang is ignored).
            let rec copy (d: int) (gBase: int) (cBase: int) =
                if d = rank then
                    match chunkBytes with
                    | Some raw ->
                        if meta.Dtype.IsFloat then outF.[gBase] <- decodeFloatCell meta.Dtype.Code raw (cBase * bs)
                        else outI.[gBase] <- decodeIntCell meta.Dtype.Code raw (cBase * bs)
                    | None ->
                        (match meta.FillValue with
                         | FillFloat f when meta.Dtype.IsFloat -> outF.[gBase] <- f
                         | FillInt n when not meta.Dtype.IsFloat -> outI.[gBase] <- n
                         | FillFloat f -> outI.[gBase] <- int64 f
                         | FillInt n -> outF.[gBase] <- float n
                         | FillNone -> ())
                else
                    let basePos = coordsArr.[d] * chunksArr.[d]
                    let lim = min chunksArr.[d] (shapeArr.[d] - basePos)
                    for l in 0 .. lim - 1 do
                        copy (d + 1) (gBase + (basePos + l) * gStrArr.[d]) (cBase + l * cStrArr.[d])
            if total > 0 then copy 0 0 0
        Ok { DimLengths = shape
             Payload = if meta.Dtype.IsFloat then ZFloats outF else ZInts outI }
    with ex ->
        Error ex.Message

/// Read a variable's full payload at compile time (provider contract).
let readVarData (path: string) (varName: string) : Result<ZarrVarData, string> =
    try
        let store = load path
        match tryFindArray store varName with
        | None ->
            Error (sprintf "variable '%s' not found in Zarr store '%s' (arrays: %s)"
                       varName path (store.Arrays |> List.map (fun a -> a.Name) |> String.concat ", "))
        | Some meta -> readArrayData meta
    with ex ->
        Error ex.Message

/// Canonical pool of a packed variable regardless of physical layout:
/// "packed" reads the pool directly; "packed-blocks" reassembles it from
/// the padded block rows via the shared cell map. Logical DimLengths =
/// [cardinality] @ dense dims. Ground truth for tests and the differential
/// gate between the two layouts.
let readPackedPool (meta: ZarrArrayMeta) : Result<ZarrVarData, string> =
    match meta.Blade with
    | None -> Error (sprintf "variable '%s' has no blade packed layout" meta.Name)
    | Some layout ->
        match layout.Blocks with
        | None -> readArrayData meta
        | Some info ->
            match readArrayData meta with
            | Error e -> Error e
            | Ok phys ->
                let g = layout.Group
                let strict = (g.Sym = SymAntisymmetric)
                let card = int (packedCardinality g)
                let trail = layout.DenseDims |> List.fold (fun a d -> a * int d) 1
                let cellMap = SimplexBlocks.blocksCellMap strict g.Extent info.Tile g.Rank info.Order
                let dims = card :: (layout.DenseDims |> List.map int)
                let remap (src: 'a[]) (zero: 'a) : 'a[] =
                    let out = Array.create (max (card * trail) 1) zero
                    for p in 0 .. cellMap.Length - 1 do
                        let pool = cellMap.[p]
                        if pool >= 0 then
                            for t in 0 .. trail - 1 do
                                out.[pool * trail + t] <- src.[p * trail + t]
                    out
                match phys.Payload with
                | ZFloats xs -> Ok { DimLengths = dims; Payload = ZFloats (remap xs 0.0) }
                | ZInts xs -> Ok { DimLengths = dims; Payload = ZInts (remap xs 0L) }

// ============================================================================
// Mapping to Blade IR types (mirrors NetcdfProvider.ncFileToModule)
// ============================================================================

/// Build a named IRIndexType for a dimension (name, extent).
let zarrDimToNamedIndexType (builder: IRBuilder) (name: string, length: int64) : string * IRIndexType =
    let idx = {
        Id = builder.FreshId()
        Rank = 1
        Extent = IRLit (IRLitInt length)
        Symmetry = SymNone
        Tag = None; IxKind = IxKPlain
        Kind = SDimension
        Dependencies = []
    }
    (name, idx)

/// Resolved dimension names for an array: its DimNames when present (count
/// must match rank), synthesized "<var>_dim<i>" otherwise.
let private resolvedDimNames (a: ZarrArrayMeta) : string list =
    match a.DimNames with
    | Some ns when ns.Length = a.Shape.Length -> ns
    | Some ns ->
        failwithf "Zarr array '%s': %d dimension names for rank %d" a.Name ns.Length a.Shape.Length
    | None -> a.Shape |> List.mapi (fun i _ -> sprintf "%s_dim%d" a.Name i)

/// Convert a ZarrStore into an IRModule using structs for dims/vars —
/// the same shape ncFileToModule produces:
///
///   type x = Idx<20>          (named index types, one per dimension)
///   struct dims = { x: Array<Float64, Idx<x>> }   (coordinate arrays)
///   struct vars = { A: Array<Float32, Idx<x>, ...> }
///
/// Coordinate arrays (1-D, named after their dimension) go in dims; unlike
/// NetCDF (whose dims struct hardcodes Int64 coordinates), a Zarr
/// coordinate array's ACTUAL element type is used when the array exists.
/// Dimension names come from xarray's _ARRAY_DIMENSIONS (v2) /
/// dimension_names (v3); arrays without names get per-array synthesized
/// dimensions. Same-named dimensions must agree on extent across arrays.
let zarrStoreToModule
    (builder: IRBuilder)
    (moduleName: string)
    (store: ZarrStore)
    (externalDimMap: Map<string, IRIndexType> option)
    : IRModule =

    // Step 0: dimension universe (first-seen order), extent-consistent.
    // A blade-packed array's POOL dimension is not a shareable dimension —
    // its physical extent is a derived cardinality and its index space is
    // the packed group itself (typed per-var below) — so only the trailing
    // dense dims join the universe.
    let sharedDims (a: ZarrArrayMeta) : (string * int64) list =
        let all = List.zip (resolvedDimNames a) a.Shape
        match a.Blade with
        | Some l when l.Blocks.IsSome -> all |> List.skip 2  // [blockCount, tile^rank] physical dims
        | Some _ -> List.tail all                            // [cardinality] pool dim
        | None -> all
    let dimOrder = ResizeArray<string>()
    let dimExtents = System.Collections.Generic.Dictionary<string, int64>()
    for a in store.Arrays do
        for (dn, ext) in sharedDims a do
            if not (isValidIdent dn) then
                failwithf "Zarr array '%s': dimension name '%s' is not a valid identifier" a.Name dn
            match dimExtents.TryGetValue dn with
            | true, prev when prev <> ext ->
                failwithf "Zarr store '%s': dimension '%s' has conflicting extents %d and %d across arrays" store.Path dn prev ext
            | true, _ -> ()
            | _ ->
                dimExtents.[dn] <- ext
                dimOrder.Add dn

    // Step 1: named index types.
    let (indexTypeDefs, dimMap) =
        match externalDimMap with
        | Some dm -> ([], dm)
        | None ->
            let pairs =
                dimOrder |> List.ofSeq
                |> List.map (fun dn -> zarrDimToNamedIndexType builder (dn, dimExtents.[dn]))
            let typeDefs = pairs |> List.map (fun (name, idx) -> IRTDIndexType(name, idx))
            (typeDefs, Map.ofList pairs)

    let isCoordinateArr (a: ZarrArrayMeta) =
        a.Blade.IsNone && a.Shape.Length = 1 && resolvedDimNames a = [a.Name]

    let coordElem (dn: string) : ElemType =
        store.Arrays
        |> List.tryFind (fun a -> a.Name = dn && isCoordinateArr a)
        |> Option.map (fun a -> a.Dtype.Elem)
        |> Option.defaultValue ETInt64

    // Step 2: dims struct — one coordinate array per dimension.
    let dimsFields =
        dimOrder |> List.ofSeq |> List.map (fun dn ->
            let idx = dimMap.[dn]
            let arrType = mkArrayArrow [idx] (IRTScalar (coordElem dn)) (Some (AIDVariable dn))
            (dn, arrType))
    let dimsStruct = IRTDStruct("dims", dimsFields)

    // Step 3: vars struct — data arrays (coordinate arrays excluded).
    // A blade-packed array types with its packed group as the LEADING index
    // type — the exact record shape source-level SymIdx/AntisymIdx lowering
    // produces (TypeCheck.lowerIndexType), so downstream compact codegen
    // engages identically.
    let varsFields =
        store.Arrays
        |> List.filter (not << isCoordinateArr)
        |> List.map (fun a ->
            let trailingIdx =
                sharedDims a
                |> List.map (fun (dn, _) ->
                    match Map.tryFind dn dimMap with
                    | Some idx -> idx
                    | None -> failwithf "Zarr array '%s': dimension '%s' not found in module dim map" a.Name dn)
            let indexTypes =
                match a.Blade with
                | Some layout ->
                    let g = layout.Group
                    let packedIdx = {
                        Id = builder.FreshId()
                        Rank = g.Rank
                        Extent = IRLit (IRLitInt g.Extent)
                        Symmetry = g.Sym
                        Tag = None; IxKind = IxKPlain
                        Kind = SDimension
                        Dependencies = []
                    }
                    packedIdx :: trailingIdx
                | None -> trailingIdx
            let arrType = {
                ElemType = IRTScalar a.Dtype.Elem
                IndexTypes = indexTypes
                IsVirtual = false
                Identity = Some (AIDVariable a.Name)
            }
            (a.Name, mkArrayLike arrType))
    let varsStruct = IRTDStruct("vars", varsFields)

    {
        Name = moduleName
        Types = indexTypeDefs @ [dimsStruct; varsStruct]
        Functions = []
        Bindings = []
        StaticFunctionUsage = Map.empty
        ProviderReads = Map.empty
        ProviderWrites = Map.empty
        RandomInits = Map.empty
        CompoundInits = Map.empty
        MutableArrayLets = Set.empty
    }

/// Convenience: load a store and produce a module in one step (contract).
let loadAsModule (builder: IRBuilder) (moduleName: string) (path: string) : IRModule =
    let store = load path
    zarrStoreToModule builder moduleName store None

// ============================================================================
// Store fingerprint / version stamp (multi-file provenance)
// ============================================================================

/// Every file under the store, sorted by relative path (deterministic).
let private storeFiles (root: string) : (string * string) list =
    if Directory.Exists root then
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        |> Array.map (fun f -> (Path.GetRelativePath(root, f).Replace('\\', '/'), f))
        |> Array.sortBy fst
        |> List.ofArray
    else []

/// SHA256 over (relative path, contents) of every file in the store —
/// metadata AND chunks, so provenance pins the actual payload.
let storeFingerprint (root: string) : string =
    use sha = System.Security.Cryptography.SHA256.Create()
    for (rel, file) in storeFiles root do
        let relBytes = Text.Encoding.UTF8.GetBytes(rel + "\n")
        sha.TransformBlock(relBytes, 0, relBytes.Length, null, 0) |> ignore
        let content = File.ReadAllBytes file
        sha.TransformBlock(content, 0, content.Length, null, 0) |> ignore
    sha.TransformFinalBlock([||], 0, 0) |> ignore
    sha.Hash |> Array.map (sprintf "%02x") |> String.concat ""

/// Max mtime over the store's files (fold-memo change stamp).
let storeVersionStamp (root: string) : int64 =
    try
        storeFiles root
        |> List.map (fun (_, f) -> File.GetLastWriteTimeUtc(f).Ticks)
        |> function [] -> 0L | ts -> List.max ts
    with _ -> 0L

// ============================================================================
// C++ code generation (pure std C++17: <fstream>, <filesystem>)
// ============================================================================

module CppZarr =

    let private elemCppOf (t: IRType) : string =
        match t with
        | IRTScalar ETFloat32 -> "float"
        | IRTScalar ETFloat64 -> "double"
        | IRTScalar ETInt32 -> "int"
        | IRTScalar ETInt64 -> "long long"
        | _ -> "double"

    /// On-disk C++ type per normalized dtype code.
    let private diskCppOf (code: string) : string =
        match code with
        | "f8" -> "double"
        | "f4" -> "float"
        | "i8" -> "int64_t"
        | "i4" -> "int32_t"
        | "i2" -> "int16_t"
        | "i1" -> "int8_t"
        | "u1" -> "uint8_t"
        | "u2" -> "uint16_t"
        | "u4" -> "uint32_t"
        | "u8" -> "uint64_t"
        | c -> failwithf "diskCppOf: unknown dtype code '%s'" c

    /// v2 dtype string for a Blade elem type (the write format).
    let private v2DtypeOf (t: IRType) : string =
        match t with
        | IRTScalar ETFloat32 -> "<f4"
        | IRTScalar ETFloat64 -> "<f8"
        | IRTScalar ETInt32 -> "<i4"
        | IRTScalar ETInt64 -> "<i8"
        | _ -> "<f8"

    let private normPath (p: string) : string = p.Replace('\\', '/')

    let private fmtF (f: float) : string =
        if Double.IsNaN f then "std::numeric_limits<double>::quiet_NaN()"
        elif Double.IsPositiveInfinity f then "std::numeric_limits<double>::infinity()"
        elif Double.IsNegativeInfinity f then "(-std::numeric_limits<double>::infinity())"
        else f.ToString("R", Globalization.CultureInfo.InvariantCulture)

    /// A loud runtime failure (the ncChecked discipline: never exit 0 with
    /// an uninitialized buffer).
    let private zExit (message: string) : string =
        sprintf "{ std::cerr << \"Zarr error: %s\" << std::endl; std::exit(1); }" message

    /// The chunk-assembly core shared by the dense and packed readers:
    /// emits C++ that assembles the (physical, dense) on-disk array into a
    /// flat row-major buffer `<cppVarName>_flat` of C++ type `elemCpp`.
    /// All metadata (shape, chunk grid, dtype, fill, key encoding) is baked
    /// at compile time from the store's actual metadata — the generated
    /// program parses no JSON. Missing chunks fill with fill_value (or fail
    /// loudly when fill_value is null); a missing/renamed store fails
    /// loudly at the metadata existence check. The caller owns (and must
    /// delete[]) `<cppVarName>_flat`.
    let private genAssembleFlat (storePath: string) (store: ZarrStore) (meta: ZarrArrayMeta) (cppVarName: string) (elemCpp: string) : string list =
        let v = cppVarName
        let varName = meta.Name
        let rank = meta.Shape.Length
        if rank = 0 then
            failwithf "Zarr codegen: rank-0 variable '%s' has no runtime read (bind it with `let static` instead)" varName
        let diskCpp = diskCppOf meta.Dtype.Code
        let shape = meta.Shape |> List.map int
        let chunks = meta.Chunks |> List.map int
        let grid = gridDims meta.Shape meta.Chunks |> List.map int
        let gStr = rowMajorStrides shape
        let cStr = rowMajorStrides chunks
        let total = shape |> List.fold (*) 1
        let chunkCount = chunks |> List.fold (*) 1
        let chunkBytes = chunkCount * meta.Dtype.ByteSize
        // Bake the path AS GIVEN (netcdf parity): a relative store path
        // resolves against the executable's working directory at runtime,
        // not against wherever the compiler happened to run.
        let arrayDir =
            let rel = Path.GetRelativePath(store.Path, meta.ArrayDir)
            normPath (if rel = "." then storePath else Path.Combine(storePath, rel))
        let metaFile = if meta.Version = 3 then "zarr.json" else ".zarray"

        // Chunk key expression from the loop counters, e.g.
        // std::to_string(c0) + "." + std::to_string(c1)  /  "c" "/" ...
        let keyExpr =
            let coordParts = [ for d in 0 .. rank - 1 -> sprintf "std::to_string(%s_c%d)" v d ]
            let sepLit = sprintf "\"%s\"" meta.ChunkKeySep
            let joined = String.concat (sprintf " + %s + " sepLit) coordParts
            if meta.ChunkKeyPrefix = "" then joined
            else sprintf "std::string(\"%s\") + %s + %s" meta.ChunkKeyPrefix sepLit joined

        let fillDecl =
            match meta.FillValue with
            | FillFloat f -> [ sprintf "%s %s_fillv = (%s)%s;" elemCpp v elemCpp (fmtF f) ]
            | FillInt n -> [ sprintf "%s %s_fillv = (%s)%dLL;" elemCpp v elemCpp n ]
            | FillNone -> []

        let header =
            [ sprintf "// Read %s from zarr store %s (v%d, uncompressed)" varName (normPath storePath) meta.Version
              sprintf "{ std::ifstream %s_zm(\"%s/%s\"); if (!%s_zm) %s }"
                  v arrayDir metaFile v
                  (zExit (sprintf "array '%s' not found in store '%s' (missing %s)" varName (normPath storePath) metaFile))
              sprintf "%s* %s_flat = new %s[%d];" elemCpp v elemCpp total
              sprintf "%s* %s_cbuf = new %s[%d];" diskCpp v diskCpp chunkCount ]
            @ fillDecl

        // Grid loops.
        let gridLoops =
            [ for d in 0 .. rank - 1 ->
                let ind = String.replicate d "    "
                sprintf "%sfor (size_t %s_c%d = 0; %s_c%d < %d; %s_c%d++) {" ind v d v d grid.[d] v d ]
        let gInd = String.replicate rank "    "

        // In-bounds limits per dim (edge chunks are stored padded; copy the
        // intersection only).
        let limDecls =
            [ for d in 0 .. rank - 1 do
                yield sprintf "%ssize_t %s_lim%d = %d - %s_c%d * %d;" gInd v d shape.[d] v d chunks.[d]
                yield sprintf "%sif (%s_lim%d > %d) %s_lim%d = %d;" gInd v d chunks.[d] v d chunks.[d] ]

        // Copy loops (shared shape by both branches): global index from
        // (chunkCoord*chunk + local) with row-major strides.
        let copyLoops (assign: string) =
            [ for d in 0 .. rank - 1 ->
                let ind = gInd + String.replicate (d + 1) "    "
                sprintf "%sfor (size_t %s_l%d = 0; %s_l%d < %s_lim%d; %s_l%d++) {" ind v d v d v d v d ]
            @ [ gInd + String.replicate (rank + 1) "    " + assign ]
            @ [ for d in rank - 1 .. -1 .. 0 -> gInd + String.replicate (d + 1) "    " + "}" ]
        let gIdx =
            [ for d in 0 .. rank - 1 -> sprintf "(%s_c%d * %d + %s_l%d) * %d" v d chunks.[d] v d gStr.[d] ]
            |> String.concat " + "
        let cIdx =
            [ for d in 0 .. rank - 1 -> sprintf "%s_l%d * %d" v d cStr.[d] ]
            |> String.concat " + "

        let presentBranch =
            [ gInd + sprintf "if (%s_cf) {" v
              gInd + sprintf "    %s_cf.read((char*)%s_cbuf, %d);" v v chunkBytes
              gInd + sprintf "    if (%s_cf.gcount() != (std::streamsize)%d) { std::cerr << \"Zarr error: chunk '\" << %s_key << \"' of '%s' is short (expected %d bytes) — a compressed or corrupt store?\" << std::endl; std::exit(1); }"
                  v chunkBytes v varName chunkBytes ]
            @ (copyLoops (sprintf "%s_flat[%s] = (%s)%s_cbuf[%s];" v gIdx elemCpp v cIdx))
        let missingBranch =
            match meta.FillValue with
            | FillNone ->
                [ gInd + "} else {"
                  gInd + sprintf "    std::cerr << \"Zarr error: chunk '\" << %s_key << \"' of '%s' is missing and fill_value is null\" << std::endl; std::exit(1);" v varName
                  gInd + "}" ]
            | _ ->
                [ gInd + "} else {" ]
                @ (copyLoops (sprintf "%s_flat[%s] = %s_fillv;" v gIdx v) |> List.map (fun s -> "    " + s))
                @ [ gInd + "}" ]

        let chunkBody =
            limDecls
            @ [ gInd + sprintf "std::string %s_key = %s;" v keyExpr
                gInd + sprintf "std::ifstream %s_cf(std::string(\"%s/\") + %s_key, std::ios::binary);" v arrayDir v ]
            @ presentBranch
            @ missingBranch

        let gridClose = [ for d in rank - 1 .. -1 .. 0 -> String.replicate d "    " + "}" ]

        header
        @ gridLoops
        @ chunkBody
        @ gridClose
        @ [ sprintf "delete[] %s_cbuf;" v ]

    /// Generate C++ to read a DENSE variable from an uncompressed Zarr
    /// store: chunk assembly into `<v>_flat`, then the same materialization
    /// form as CppNetcdf.genReadVar — nested Array via allocate<>,
    /// flat->nested copy, buffers released.
    let genReadVar (storePath: string) (varName: string) (cppVarName: string) (arrType: IRArrayType) : string list =
        let store = load storePath
        let meta =
            match tryFindArray store varName with
            | Some m -> m
            | None -> failwithf "Zarr codegen: variable '%s' not found in store '%s'" varName storePath
        if meta.Blade.IsSome then
            failwithf "Zarr codegen: variable '%s' is blade-packed; the dense reader cannot materialize it (this indicates a typing inconsistency)" varName
        let v = cppVarName
        let elemCpp = elemCppOf arrType.ElemType
        let shape = meta.Shape |> List.map int
        let rank = shape.Length
        let assemble = genAssembleFlat storePath store meta v elemCpp

        let extentDecls =
            shape |> List.mapi (fun i n -> sprintf "size_t %s_extent_%d = %d;" v i n)
        let extentNames = shape |> List.mapi (fun i _ -> sprintf "%s_extent_%d" v i)
        let idxVars = [ for i in 0 .. rank - 1 -> sprintf "%s_i%d" v i ]
        let openLoops =
            idxVars |> List.mapi (fun d iv ->
                let ind = String.replicate d "    "
                sprintf "%sfor (size_t %s = 0; %s < %s; %s++) {" ind iv iv extentNames.[d] iv)
        let nestedSub = idxVars |> List.map (sprintf "[%s]") |> String.concat ""
        let flatIdx =
            let mutable acc = idxVars.[0]
            for i in 1 .. rank - 1 do
                acc <- sprintf "(%s) * %s + %s" acc extentNames.[i] idxVars.[i]
            acc
        let bodyInd = String.replicate rank "    "
        let materialize =
            extentDecls
            @ [ sprintf "size_t %s_extents[] = { %s };" v (String.concat ", " extentNames)
                sprintf "Array<%s, %d> %s = { allocate<typename promote<%s, %d>::type, nullptr>(%s_extents), %s_extents };"
                    elemCpp rank v elemCpp rank v v ]
            @ openLoops
            @ [ sprintf "%s%s%s = %s_flat[%s];" bodyInd v nestedSub v flatIdx ]
            @ [ for d in rank - 1 .. -1 .. 0 -> sprintf "%s}" (String.replicate d "    ") ]
            @ [ sprintf "delete[] %s_flat;" v ]

        assemble @ materialize

    /// Blocks-layout pool assembly with PER-BLOCK chunk I/O: for each block
    /// (tile multiset — symmetric::unlinearize over tiles for BOTH
    /// symmetries), optionally skip it (window / MPI cell-range ownership),
    /// read ITS chunk file only, and scatter its cells into the canonical
    /// pool — branch-free intra-block bounds, pool index via linearize.
    /// Antisym blocks with a repeated narrow tile iterate zero cells (the
    /// empty-diagonal-block case).
    ///
    /// distribute: rank-scoped I/O — each rank handles only blocks whose
    /// pool range [first-cell, last-cell] intersects its balanced flat-cell
    /// range, then MPI_Allgatherv restores the full pool buffer on all
    /// ranks. Requires the program's MPI scaffolding (rank/size globals,
    /// MPI_Init) — the codegen intercept only sets it when present.
    /// windowSkip: [wlo, whi) coordinate window — blocks with any tile
    /// interval disjoint from the window are not read at all (the cells
    /// themselves are filtered by the caller's extraction pass).
    let private genAssemblePackedBlocks
            (storePath: string) (store: ZarrStore) (meta: ZarrArrayMeta)
            (layout: BladeLayout) (info: SimplexBlocksInfo)
            (v: string) (elemCpp: string)
            (distribute: bool) (windowSkip: (int64 * int64) option) : string list =
        let g = layout.Group
        let strict = (g.Sym = SymAntisymmetric)
        let sInc = if strict then 1 else 0
        let r = g.Rank
        let n = g.Extent
        let T = info.Grid
        let B = info.Tile
        let nBlocks = SimplexBlocks.blockCount r T
        let rowW = SimplexBlocks.maxBlockCells r B
        let card = packedCardinality g
        let trailExts = layout.DenseDims
        let trail = trailExts |> List.fold (fun a d -> a * d) 1L
        let nsName = if strict then "antisymmetric" else "symmetric"
        let diskCpp = diskCppOf meta.Dtype.Code
        let chunkCells = rowW * trail
        let chunkBytes = chunkCells * int64 meta.Dtype.ByteSize
        let arrayDir =
            let rel = Path.GetRelativePath(store.Path, meta.ArrayDir)
            normPath (if rel = "." then storePath else Path.Combine(storePath, rel))
        let metaFile = if meta.Version = 3 then "zarr.json" else ".zarray"
        let tableDecl =
            match info.Order with
            | OrderLex -> []
            | OrderPath ->
                if nBlocks > 1_000_000L then
                    failwithf "Zarr codegen: variable '%s': path-order block table would need %d entries — beyond the emission cap" meta.Name nBlocks
                let rows = SimplexBlocks.pathRows r T
                [ sprintf "static const size_t %s_sbrow[%d] = { %s };" v rows.Length (rows |> Array.map string |> String.concat ", ") ]
        let rowExpr =
            match info.Order with
            | OrderLex -> sprintf "%s_sb" v
            | OrderPath -> sprintf "%s_sbrow[%s_sb]" v v
        // Runtime chunk key for physical coords (row, 0, 0...): the block
        // row is the only varying chunk coordinate.
        let keyExpr =
            let sep = meta.ChunkKeySep
            let zeros = List.replicate (1 + trailExts.Length) "0" |> String.concat sep
            let core = sprintf "std::to_string(%s_row) + \"%s%s\"" v sep zeros
            if meta.ChunkKeyPrefix = "" then core
            else sprintf "std::string(\"%s%s\") + %s" meta.ChunkKeyPrefix sep core

        let fillDecl =
            match meta.FillValue with
            | FillFloat f -> [ sprintf "%s %s_fillv = (%s)%s;" elemCpp v elemCpp (fmtF f) ]
            | FillInt fi -> [ sprintf "%s %s_fillv = (%s)%dLL;" elemCpp v elemCpp fi ]
            | FillNone -> []

        // Distribution: balanced flat-cell range [dlo, dhi) per rank (same
        // q/rem split as the MPI compute decomposition).
        let distDecls =
            if not distribute then [] else
            [ sprintf "// zarr mpi: distributed simplex-blocks read of '%s' (rank-scoped chunk I/O + Allgatherv)" meta.Name
              sprintf "size_t %s_dq = %dUL / (size_t)__blade_mpi_size;" v card
              sprintf "size_t %s_dr = %dUL %% (size_t)__blade_mpi_size;" v card
              sprintf "size_t %s_dlo = (size_t)__blade_mpi_rank * %s_dq + ((size_t)__blade_mpi_rank < %s_dr ? (size_t)__blade_mpi_rank : %s_dr);" v v v v
              sprintf "size_t %s_dhi = %s_dlo + %s_dq + ((size_t)__blade_mpi_rank < %s_dr ? 1 : 0);" v v v v ]

        // Window skip: any tile interval disjoint from [wlo, whi) means no
        // block cell can be fully inside the window.
        let windowSkipLines =
            match windowSkip with
            | None -> []
            | Some (wlo, whi) ->
                [ for k in 0 .. r - 1 ->
                    sprintf "    if (%s_sbt[%d] * %d >= %dUL || (%s_sbt[%d] + 1) * %d <= %dUL) continue;" v k B whi v k B wlo ]

        // Ownership skip (distribute): exact block pool range via greedy
        // first/last cells (within-block enumeration is pool-monotone).
        let ownershipSkipLines =
            if not distribute then [] else
            let fwd =
                [ for k in 0 .. r - 1 do
                    if k = 0 then
                        yield sprintf "    { size_t lo = %s_sbt[0] * %d; size_t hi = (%s_sbt[0] + 1) * %d; if (hi > %dUL) hi = %dUL; %s_fc[0] = lo; if (lo >= hi) %s_emptyb = true; }" v B v B n n v v
                    else
                        yield sprintf "    { size_t lo = %s_sbt[%d] * %d; size_t p = %s_fc[%d] + %d; if (p > lo) lo = p; size_t hi = (%s_sbt[%d] + 1) * %d; if (hi > %dUL) hi = %dUL; %s_fc[%d] = lo; if (lo >= hi) %s_emptyb = true; }" v k B v (k - 1) sInc v k B n n v k v ]
            let bwd =
                [ for k in r - 2 .. -1 .. 0 ->
                    if strict then
                        sprintf "    { size_t cap; if (%s_lc[%d] < 1) { %s_emptyb = true; cap = 0; } else cap = %s_lc[%d] - 1; size_t hi = (%s_sbt[%d] + 1) * %d; if (hi > %dUL) hi = %dUL; size_t c = hi - 1; if (cap < c) c = cap; %s_lc[%d] = c; if (c < %s_sbt[%d] * %d) %s_emptyb = true; }" v (k + 1) v v (k + 1) v k B n n v k v k B v
                    else
                        sprintf "    { size_t cap = %s_lc[%d]; size_t hi = (%s_sbt[%d] + 1) * %d; if (hi > %dUL) hi = %dUL; size_t c = hi - 1; if (cap < c) c = cap; %s_lc[%d] = c; if (c < %s_sbt[%d] * %d) %s_emptyb = true; }" v (k + 1) v k B n n v k v k B v ]
            [ sprintf "    std::array<size_t, %d> %s_fc; std::array<size_t, %d> %s_lc;" r v r v
              sprintf "    bool %s_emptyb = false;" v ]
            @ fwd
            @ [ sprintf "    { size_t hi = (%s_sbt[%d] + 1) * %d; if (hi > %dUL) hi = %dUL; %s_lc[%d] = hi - 1; }" v (r - 1) B n n v (r - 1) ]
            @ bwd
            @ [ sprintf "    if (%s_emptyb) continue;" v
                sprintf "    if (linearized_storage::%s::linearize<%d>(%s_lc, %dUL) < %s_dlo || linearized_storage::%s::linearize<%d>(%s_fc, %dUL) >= %s_dhi) continue;" nsName r v n v nsName r v n v ]

        // Per-block chunk read (missing chunk -> fill_value / loud on null).
        let chunkRead =
            [ sprintf "    size_t %s_row = %s;" v rowExpr
              sprintf "    std::string %s_key = %s;" v keyExpr
              sprintf "    std::ifstream %s_cf(std::string(\"%s/\") + %s_key, std::ios::binary);" v arrayDir v
              sprintf "    bool %s_have = (bool)%s_cf;" v v ]
            @ (match meta.FillValue with
               | FillNone ->
                   [ sprintf "    if (!%s_have) { std::cerr << \"Zarr error: chunk '\" << %s_key << \"' of '%s' is missing and fill_value is null\" << std::endl; std::exit(1); }" v v meta.Name ]
               | _ -> [])
            @ [ sprintf "    if (%s_have) {" v
                sprintf "        %s_cf.read((char*)%s_cbuf, %d);" v v chunkBytes
                sprintf "        if (%s_cf.gcount() != (std::streamsize)%d) { std::cerr << \"Zarr error: chunk '\" << %s_key << \"' of '%s' is short (expected %d bytes) — a compressed or corrupt store?\" << std::endl; std::exit(1); }" v chunkBytes v meta.Name chunkBytes
                "    }" ]

        // Branch-free intra-block bounds per level.
        let cellLoops =
            [ for k in 0 .. r - 1 do
                let ind = String.replicate (k + 1) "    "
                yield sprintf "%ssize_t %s_lo%d = %s_sbt[%d] * %d;" ind v k v k B
                if k > 0 then
                    yield sprintf "%s{ size_t %s_p = %s_i%d + %d; if (%s_p > %s_lo%d) %s_lo%d = %s_p; }"
                              ind v v (k - 1) sInc v v k v k v
                yield sprintf "%ssize_t %s_hi%d = (%s_sbt[%d] + 1) * %d; if (%s_hi%d > %d) %s_hi%d = %d;"
                          ind v k v k B v k n v k n
                yield sprintf "%sfor (size_t %s_i%d = %s_lo%d; %s_i%d < %s_hi%d; %s_i%d++) {"
                          ind v k v k v k v k v k ]
        let bodyInd = String.replicate (r + 1) "    "
        let coordsInit = [ for k in 0 .. r - 1 -> sprintf "%s_i%d" v k ] |> String.concat ", "
        let trailVars = trailExts |> List.mapi (fun i _ -> sprintf "%s_sbt%d" v i)
        let guardInd = if distribute then "    " else ""
        let trailOpen =
            trailVars |> List.mapi (fun i tv ->
                sprintf "%sfor (size_t %s = 0; %s < %d; %s++) {" (bodyInd + guardInd + String.replicate i "    ") tv tv trailExts.[i] tv)
        let trailClose =
            [ for i in trailVars.Length - 1 .. -1 .. 0 -> bodyInd + guardInd + String.replicate i "    " + "}" ]
        let trailIdx =
            if trailVars.IsEmpty then "0"
            else
                let mutable acc = trailVars.[0]
                for i in 1 .. trailVars.Length - 1 do
                    acc <- sprintf "(%s) * %d + %s" acc trailExts.[i] trailVars.[i]
                acc
        let valueExpr =
            match meta.FillValue with
            | FillNone -> sprintf "(%s)%s_cbuf[%s_local * %d + %s]" elemCpp v v trail trailIdx
            | _ -> sprintf "(%s_have ? (%s)%s_cbuf[%s_local * %d + %s] : %s_fillv)" v elemCpp v v trail trailIdx v
        let assign =
            sprintf "%s%s_flat[%s_pool * %d + %s] = %s;"
                (bodyInd + guardInd + String.replicate trailVars.Length "    ")
                v v trail trailIdx valueExpr
        let cellBody =
            [ bodyInd + sprintf "std::array<size_t, %d> %s_sbc = { %s };" r v coordsInit
              bodyInd + sprintf "size_t %s_pool = linearized_storage::%s::linearize<%d>(%s_sbc, %dUL);" v nsName r v n ]
            @ (if distribute then [ bodyInd + sprintf "if (%s_pool >= %s_dlo && %s_pool < %s_dhi) {" v v v v ] else [])
            @ trailOpen
            @ [ assign ]
            @ trailClose
            @ (if distribute then [ bodyInd + "}" ] else [])
            @ [ bodyInd + sprintf "%s_local++;" v ]
        let cellClose = [ for k in r - 1 .. -1 .. 0 -> String.replicate (k + 1) "    " + "}" ]

        // Post-loop restoration under distribution: contiguous cell-range
        // Allgatherv (counts in ELEMENTS = cells x trailing block).
        let mpiDtype =
            match elemCpp with
            | "double" -> "MPI_DOUBLE"
            | "float" -> "MPI_FLOAT"
            | "long long" -> "MPI_LONG_LONG"
            | "int" -> "MPI_INT"
            | other -> failwithf "Zarr codegen: variable '%s': no MPI datatype for element type '%s'" meta.Name other
        let gather =
            if not distribute then [] else
            [ sprintf "if (__blade_mpi_size > 1) { // zarr mpi: restore full pool of '%s' on all ranks" meta.Name
              sprintf "    if (%dULL > 2147483647ULL) { MPI_Abort(MPI_COMM_WORLD, 13); }" (card * trail)
              sprintf "    int* %s_cnt = new int[__blade_mpi_size]; int* %s_dsp = new int[__blade_mpi_size];" v v
              "    for (int __r = 0; __r < __blade_mpi_size; __r++) {"
              sprintf "        size_t __lo = (size_t)__r * %s_dq + ((size_t)__r < %s_dr ? (size_t)__r : %s_dr);" v v v
              sprintf "        size_t __hi = __lo + %s_dq + ((size_t)__r < %s_dr ? 1 : 0);" v v
              sprintf "        %s_cnt[__r] = (int)((__hi - __lo) * %d); %s_dsp[__r] = (int)(__lo * %d);" v trail v trail
              "    }"
              sprintf "    MPI_Allgatherv(MPI_IN_PLACE, 0, MPI_DATATYPE_NULL, %s_flat, %s_cnt, %s_dsp, %s, MPI_COMM_WORLD);" v v v mpiDtype
              sprintf "    delete[] %s_cnt; delete[] %s_dsp;" v v
              "}" ]

        [ sprintf "// Read %s from zarr store %s (simplex-blocks: %d blocks, grid %d, tile %d)" meta.Name (normPath storePath) nBlocks T B
          sprintf "{ std::ifstream %s_zm(\"%s/%s\"); if (!%s_zm) { std::cerr << \"Zarr error: array '%s' not found in store '%s' (missing %s)\" << std::endl; std::exit(1); } }"
              v arrayDir metaFile v meta.Name (normPath storePath) metaFile ]
        @ tableDecl
        @ fillDecl
        @ distDecls
        @ [ sprintf "%s* %s_flat = new %s[%d];" elemCpp v elemCpp (card * trail)
            sprintf "%s* %s_cbuf = new %s[%d];" diskCpp v diskCpp chunkCells
            sprintf "for (size_t %s_sb = 0; %s_sb < %d; %s_sb++) {" v v nBlocks v
            sprintf "    auto %s_sbt = linearized_storage::symmetric::unlinearize<%d>(%s_sb, %dUL);" v r v T ]
        @ windowSkipLines
        @ ownershipSkipLines
        @ chunkRead
        @ [ sprintf "    size_t %s_local = 0;" v ]
        @ cellLoops
        @ cellBody
        @ cellClose
        @ [ "}"
            sprintf "delete[] %s_cbuf;" v ]
        @ gather

    /// Generate C++ assembling a blade-packed variable's canonical flat
    /// pool into `<cppVarName>_flat` (provider contract GenReadPacked: the
    /// codegen intercept performs the packed allocation and pool copy, and
    /// releases the buffer). Validates the declared Blade index types
    /// against the store's blade layout — group symmetry/rank and trailing
    /// dense extents must agree; the leading extent is the group extent for
    /// whole reads, hi-lo for windowed reads. Dispatches on the store's
    /// physical layout: flat pool ("packed") or simplex-blocks rows
    /// ("packed-blocks"). opts.Distribute emits the MPI rank-scoped read
    /// (blocks layout only — flat stores read fully on every rank);
    /// opts.Window emits the sub-simplex extraction (blocks stores skip
    /// non-intersecting chunks entirely).
    let genReadPacked (storePath: string) (varName: string) (cppVarName: string) (arrType: IRArrayType) (opts: Blade.ProviderRegistry.PackedReadOpts) : string list =
        let store = load storePath
        let meta =
            match tryFindArray store varName with
            | Some m -> m
            | None -> failwithf "Zarr codegen: variable '%s' not found in store '%s'" varName storePath
        match meta.Blade with
        | None ->
            failwithf "Zarr codegen: variable '%s' has no blade packed layout but was typed packed (this indicates a typing inconsistency)" varName
        | Some layout ->
            let g = layout.Group
            let expectedLead =
                match opts.Window with
                | None -> g.Extent
                | Some (lo, hi) ->
                    if lo < 0L || lo >= hi || hi > g.Extent then
                        failwithf "Zarr codegen: variable '%s': window [%d, %d) is outside the packed extent %d" varName lo hi g.Extent
                    hi - lo
            (match arrType.IndexTypes with
             | lead :: rest ->
                 let leadOk =
                     lead.Symmetry = g.Sym && lead.Rank = g.Rank
                     && (match lead.Extent with IRLit (IRLitInt n) -> n = expectedLead | _ -> false)
                 let restExtents =
                     rest |> List.map (fun ix ->
                         match ix.Extent with IRLit (IRLitInt n) -> n | _ -> -1L)
                 let restOk =
                     (rest |> List.forall (fun ix -> ix.Symmetry = SymNone && ix.Rank = 1))
                     && restExtents = layout.DenseDims
                 if not (leadOk && restOk) then
                     failwithf "Zarr codegen: variable '%s': declared packed type does not match the store's blade layout (group %A rank %d expected lead extent %d, dense %A)"
                         varName g.Sym g.Rank expectedLead layout.DenseDims
             | [] -> failwithf "Zarr codegen: variable '%s': packed read with no index types" varName)
            let elemCpp = elemCppOf arrType.ElemType
            match opts.Window with
            | None ->
                (match layout.Blocks with
                 | None -> genAssembleFlat storePath store meta cppVarName elemCpp
                 | Some info -> genAssemblePackedBlocks storePath store meta layout info cppVarName elemCpp opts.Distribute None)
            | Some (wlo, whi) ->
                // Assemble the SOURCE pool (blocks stores skip chunks whose
                // tiles miss the window), then extract the translated
                // sub-simplex: window cells enumerate in ascending-lex, so
                // the destination index is a running counter.
                let v = cppVarName
                let srcV = v + "_ws"
                let assemble =
                    match layout.Blocks with
                    | None -> genAssembleFlat storePath store meta srcV elemCpp
                    | Some info -> genAssemblePackedBlocks storePath store meta layout info srcV elemCpp false (Some (wlo, whi))
                let strict = (g.Sym = SymAntisymmetric)
                let sInc = if strict then 1 else 0
                let r = g.Rank
                let n = g.Extent
                let w = whi - wlo
                let wcard = packedCardinality { g with Extent = w }
                let trailExts = layout.DenseDims
                let trail = trailExts |> List.fold (fun a d -> a * d) 1L
                let nsName = if strict then "antisymmetric" else "symmetric"
                let wLoops =
                    [ for k in 0 .. r - 1 ->
                        let ind = String.replicate (k + 1) "    "
                        let lo = if k = 0 then sprintf "%dUL" wlo else sprintf "%s_w%d + %d" v (k - 1) sInc
                        sprintf "%sfor (size_t %s_w%d = %s; %s_w%d < %dUL; %s_w%d++) {" ind v k lo v k whi v k ]
                let bodyInd = String.replicate (r + 1) "    "
                let coordsInit = [ for k in 0 .. r - 1 -> sprintf "%s_w%d" v k ] |> String.concat ", "
                let trailVars = trailExts |> List.mapi (fun i _ -> sprintf "%s_wt%d" v i)
                let trailOpen =
                    trailVars |> List.mapi (fun i tv ->
                        sprintf "%sfor (size_t %s = 0; %s < %d; %s++) {" (bodyInd + String.replicate i "    ") tv tv trailExts.[i] tv)
                let trailClose =
                    [ for i in trailVars.Length - 1 .. -1 .. 0 -> bodyInd + String.replicate i "    " + "}" ]
                let trailIdx =
                    if trailVars.IsEmpty then "0"
                    else
                        let mutable acc = trailVars.[0]
                        for i in 1 .. trailVars.Length - 1 do
                            acc <- sprintf "(%s) * %d + %s" acc trailExts.[i] trailVars.[i]
                        acc
                let wClose = [ for k in r - 1 .. -1 .. 0 -> String.replicate (k + 1) "    " + "}" ]
                assemble
                @ [ sprintf "// window [%d, %d) extraction: translated %s sub-simplex (%d cells)" wlo whi nsName wcard
                    sprintf "%s* %s_flat = new %s[%d];" elemCpp v elemCpp (wcard * trail)
                    sprintf "size_t %s_wdst = 0;" v ]
                @ wLoops
                @ [ bodyInd + sprintf "std::array<size_t, %d> %s_wc = { %s };" r v coordsInit
                    bodyInd + sprintf "size_t %s_wsrc = linearized_storage::%s::linearize<%d>(%s_wc, %dUL);" v nsName r v n ]
                @ trailOpen
                @ [ bodyInd + String.replicate trailVars.Length "    "
                    + sprintf "%s_flat[%s_wdst * %d + %s] = %s_flat[%s_wsrc * %d + %s];" v v trail trailIdx srcV v trail trailIdx ]
                @ trailClose
                @ [ bodyInd + sprintf "%s_wdst++;" v ]
                @ wClose
                @ [ sprintf "delete[] %s_flat;" srcV ]

    /// STREAMED fiber reads, hoisted prologue: metadata existence check,
    /// the fiber buffer (trailing-axis length), a per-t-chunk segment
    /// buffer, and the fill value for missing chunks. Dense variables
    /// only in v1 (site dims dense, fiber = the LAST axis).
    let genStreamOpen (storePath: string) (varName: string) (cppVarName: string) (arrType: IRArrayType) : string list =
        let store = load storePath
        let meta =
            match tryFindArray store varName with
            | Some m -> m
            | None -> failwithf "Zarr codegen: variable '%s' not found in store '%s'" varName storePath
        if meta.Blade.IsSome then
            failwithf "Zarr stream of '%s': packed variables are not streamable in v1 (bind with .read)" varName
        if arrType.IndexTypes |> List.exists (fun ix -> ix.Symmetry <> SymNone || ix.Rank <> 1) then
            failwithf "Zarr stream of '%s': dense variables only" varName
        if meta.Shape.Length < 2 then
            failwithf "Zarr stream of '%s': needs at least one site dim plus the trailing fiber axis (rank >= 2)" varName
        let v = cppVarName
        let elemCpp = elemCppOf arrType.ElemType
        let diskCpp = diskCppOf meta.Dtype.Code
        let fiberLen = List.last meta.Shape
        let ctT = List.last meta.Chunks
        let arrayDir =
            let rel = Path.GetRelativePath(store.Path, meta.ArrayDir)
            normPath (if rel = "." then storePath else Path.Combine(storePath, rel))
        let metaFile = if meta.Version = 3 then "zarr.json" else ".zarray"
        let fillDecl =
            match meta.FillValue with
            | FillFloat f -> [ sprintf "%s %s_fillv = (%s)%s;" elemCpp v elemCpp (fmtF f) ]
            | FillInt fi -> [ sprintf "%s %s_fillv = (%s)%dLL;" elemCpp v elemCpp fi ]
            | FillNone -> []
        [ sprintf "// Stream %s from zarr store %s (chunked fiber reads at the S/T boundary)" varName (normPath storePath)
          sprintf "{ std::ifstream %s_zm(\"%s/%s\"); if (!%s_zm) { std::cerr << \"Zarr error: array '%s' not found in store '%s' (missing %s)\" << std::endl; std::exit(1); } }"
              v arrayDir metaFile v varName (normPath storePath) metaFile
          sprintf "size_t %s_fiber_ext[1] = { %d };" v fiberLen
          sprintf "%s* %s_fseg = new %s[%d];" diskCpp v diskCpp ctT ]
        @ fillDecl

    /// STREAMED fiber reads, in-nest: assemble one trailing-axis fiber at
    /// the given site coordinates from the chunk file(s) covering it —
    /// one seek+read per t-chunk (the fiber is contiguous WITHIN a chunk
    /// because the fiber axis is the innermost). Missing chunks fill (or
    /// fail loudly under a null fill_value).
    let genStreamFiber (storePath: string) (varName: string) (cppVarName: string) (destBuf: string) (siteExprs: string list) (arrType: IRArrayType) : string list =
        let store = load storePath
        let meta =
            match tryFindArray store varName with
            | Some m -> m
            | None -> failwithf "Zarr codegen: variable '%s' not found in store '%s'" varName storePath
        let v = cppVarName
        let elemCpp = elemCppOf arrType.ElemType
        let bs = meta.Dtype.ByteSize
        let shape = meta.Shape
        let chunks = meta.Chunks
        let d = shape.Length - 1
        if siteExprs.Length <> d then
            failwithf "Zarr stream of '%s': %d site coordinates for %d site dims" varName siteExprs.Length d
        let fiberLen = List.last shape
        let ctT = List.last chunks
        let gridT = (fiberLen + ctT - 1L) / ctT
        let arrayDir =
            let rel = Path.GetRelativePath(store.Path, meta.ArrayDir)
            normPath (if rel = "." then storePath else Path.Combine(storePath, rel))
        // Chunk key from site-chunk coords + the t-chunk counter.
        let sep = meta.ChunkKeySep
        let coordParts =
            [ for k in 0 .. d - 1 -> sprintf "std::to_string((size_t)(%s) / %d)" siteExprs.[k] chunks.[k] ]
            @ [ sprintf "std::to_string(%s_tc)" v ]
        let joined = String.concat (sprintf " + \"%s\" + " sep) coordParts
        let keyExpr =
            if meta.ChunkKeyPrefix = "" then joined
            else sprintf "std::string(\"%s%s\") + %s" meta.ChunkKeyPrefix sep joined
        // Within-chunk fiber start (elements): Horner over chunk-local site
        // coords with chunk-internal strides, times the t-chunk extent.
        let offExpr =
            let mutable acc = sprintf "((size_t)(%s) %% %d)" siteExprs.[0] chunks.[0]
            for k in 1 .. d - 1 do
                acc <- sprintf "(%s * %d + (size_t)(%s) %% %d)" acc chunks.[k] siteExprs.[k] chunks.[k]
            sprintf "%s * %d" acc ctT
        let missingBranch =
            match meta.FillValue with
            | FillNone ->
                [ sprintf "    } else { std::cerr << \"Zarr error: chunk '\" << %s_key << \"' of '%s' is missing and fill_value is null\" << std::endl; std::exit(1); }" v varName ]
            | _ ->
                [ "    } else {"
                  sprintf "        for (size_t %s_q = 0; %s_q < %s_len; %s_q++) %s[%s_tc * %d + %s_q] = %s_fillv;" v v v v destBuf v ctT v v
                  "    }" ]
        [ sprintf "for (size_t %s_tc = 0; %s_tc < %d; %s_tc++) {" v v gridT v
          sprintf "    std::string %s_key = %s;" v keyExpr
          sprintf "    size_t %s_len = %d - %s_tc * %d; if (%s_len > %d) %s_len = %d;" v fiberLen v ctT v ctT v ctT
          sprintf "    std::ifstream %s_cf(std::string(\"%s/\") + %s_key, std::ios::binary);" v arrayDir v
          sprintf "    if (%s_cf) {" v
          sprintf "        %s_cf.seekg((std::streamoff)((%s) * %d));" v offExpr bs
          sprintf "        %s_cf.read((char*)%s_fseg, %s_len * %d);" v v v bs
          sprintf "        if (%s_cf.gcount() != (std::streamsize)(%s_len * %d)) { std::cerr << \"Zarr error: chunk '\" << %s_key << \"' of '%s' is short — a compressed or corrupt store?\" << std::endl; std::exit(1); }" v v bs v varName
          sprintf "        for (size_t %s_q = 0; %s_q < %s_len; %s_q++) %s[%s_tc * %d + %s_q] = (%s)%s_fseg[%s_q];" v v v v destBuf v ctT v elemCpp v v ]
        @ missingBranch
        @ [ "}" ]

    /// Generate C++ to write a variable as an uncompressed Zarr v2 array
    /// (one chunk = the whole array). v2 is the write format: flat
    /// "."-separated chunk keys (no per-chunk directories) and readable by
    /// every zarr-python/xarray in the field; our own reader handles both
    /// versions, so roundtrips are format-agnostic. Multiple writes into
    /// one store root accumulate arrays (the .zgroup is idempotent);
    /// re-writing the same variable overwrites it. The caller provides
    /// `<cppVarName>_flat` (row-major) — see the codegen write intercept.
    let genWriteVar (storePath: string) (varName: string) (cppVarName: string) (arrType: IRArrayType) (dimNames: string list) : string list =
        let v = cppVarName
        // A packed (SymIdx/AntisymIdx) leading group writes as its POOL:
        // on-disk shape = [cardinality] @ trailing dense extents, with the
        // blade layout attribute recording the group. The flat buffer the
        // intercept hands over is already in canonical pool order.
        let packedLead =
            match arrType.IndexTypes with
            | lead :: _ when lead.Symmetry <> SymNone && lead.Rank >= 2 -> Some lead
            | _ -> None
        let litExtent (context: string) (e: IRExpr) =
            match e with
            | IRLit (IRLitInt n) -> n
            | _ -> failwithf "Zarr write of '%s' requires literal extents (%s)" varName context
        let (shape, bladeAttrJson) =
            match packedLead with
            | Some lead ->
                (match lead.Symmetry with
                 | SymSymmetric | SymAntisymmetric -> ()
                 | s -> failwithf "Zarr write of '%s': %A packed groups are not supported" varName s)
                let trailing =
                    arrType.IndexTypes |> List.tail |> List.map (fun ix ->
                        if ix.Symmetry <> SymNone || ix.Rank <> 1 then
                            failwithf "Zarr write of '%s': only one leading packed group plus dense trailing dims is supported" varName
                        litExtent "trailing dim" ix.Extent)
                let group = { Sym = lead.Symmetry; Rank = lead.Rank; Extent = litExtent "packed extent" lead.Extent }
                let card = packedCardinality group
                let kindStr = if group.Sym = SymSymmetric then "sym" else "antisym"
                let idxEntries =
                    (sprintf "{\\\"kind\\\": \\\"%s\\\", \\\"rank\\\": %d, \\\"extent\\\": %d}" kindStr group.Rank group.Extent)
                    :: (trailing |> List.map (sprintf "{\\\"kind\\\": \\\"dense\\\", \\\"extent\\\": %d}"))
                let attr =
                    sprintf ", \\\"blade\\\": {\\\"spec_version\\\": 1, \\\"layout\\\": \\\"packed\\\", \\\"order\\\": \\\"ascending-lex\\\", \\\"index_types\\\": [%s], \\\"decomposition\\\": {\\\"scheme\\\": \\\"flat-ranges\\\"}}"
                        (String.concat ", " idxEntries)
                (card :: trailing, attr)
            | None ->
                let componentExtents =
                    arrType.IndexTypes |> List.collect (fun idx -> List.replicate idx.Rank idx.Extent)
                (componentExtents |> List.map (litExtent "dim"), "")
        let rank = shape.Length
        let elemCpp = elemCppOf arrType.ElemType
        let dtype = v2DtypeOf arrType.ElemType
        let total = shape |> List.fold (fun a b -> a * b) 1L
        let root = normPath storePath
        let arrayDir = sprintf "%s/%s" root varName
        let shapeJson = shape |> List.map string |> String.concat ", "
        let dims =
            if dimNames.Length >= rank then List.truncate rank dimNames
            else [ for i in 0 .. rank - 1 -> sprintf "dim%d" i ]
        // Escaped for splicing inside the C++ string literal (like the
        // .zarray body below): the emitted code must print {"..."} JSON.
        let dimsJson = dims |> List.map (fun d -> sprintf "\\\"%s\\\"" d) |> String.concat ", "
        let zarrayJson =
            sprintf "{\\\"zarr_format\\\": 2, \\\"shape\\\": [%s], \\\"chunks\\\": [%s], \\\"dtype\\\": \\\"%s\\\", \\\"compressor\\\": null, \\\"fill_value\\\": 0, \\\"order\\\": \\\"C\\\", \\\"filters\\\": null}"
                shapeJson shapeJson dtype
        let zattrsJson = sprintf "{\\\"_ARRAY_DIMENSIONS\\\": [%s]%s}" dimsJson bladeAttrJson
        let chunkKey0 =
            if rank = 0 then "0"
            else List.replicate rank "0" |> String.concat "."
        let wCheck (stream: string) (context: string) =
            sprintf "if (!%s.good()) %s" stream (zExit context)
        [
            sprintf "// Write %s to zarr store %s (v2, uncompressed, single chunk)" varName root
            sprintf "{ std::error_code %s_ec; std::filesystem::create_directories(\"%s\", %s_ec); if (%s_ec) %s }"
                v arrayDir v v (zExit (sprintf "cannot create store directory '%s'" arrayDir))
            sprintf "{ std::ofstream %s_zg(\"%s/.zgroup\", std::ios::trunc); %s_zg << \"{\\\"zarr_format\\\": 2}\"; %s }"
                v root v (wCheck (sprintf "%s_zg" v) (sprintf "cannot write '%s/.zgroup'" root))
            sprintf "{ std::ofstream %s_za(\"%s/.zarray\", std::ios::trunc); %s_za << \"%s\"; %s }"
                v arrayDir v zarrayJson (wCheck (sprintf "%s_za" v) (sprintf "cannot write '%s/.zarray'" arrayDir))
            sprintf "{ std::ofstream %s_zt(\"%s/.zattrs\", std::ios::trunc); %s_zt << \"%s\"; %s }"
                v arrayDir v zattrsJson (wCheck (sprintf "%s_zt" v) (sprintf "cannot write '%s/.zattrs'" arrayDir))
            sprintf "{ std::ofstream %s_ch(\"%s/%s\", std::ios::binary | std::ios::trunc); %s_ch.write((const char*)%s_flat, sizeof(%s) * %d); %s }"
                v arrayDir chunkKey0 v v elemCpp total
                (wCheck (sprintf "%s_ch" v) (sprintf "cannot write chunk '%s/%s'" arrayDir chunkKey0))
        ]

    /// Required C++ includes for Zarr I/O (std only — no link flags).
    let genIncludes () : string list =
        [ "#include <fstream>"
          "#include <filesystem>"
          "#include <cstdint>"
          "#include <string>"
          "#include <limits>" ]

// ============================================================================
// F#-side store writer (fixtures and programmatic store creation)
// ============================================================================

module ZarrWrite =

    type WritePayload =
        | WF32 of float32[]
        | WF64 of float[]
        | WI32 of int32[]
        | WI64 of int64[]

    type WriteVar = {
        Name: string
        /// None -> no _ARRAY_DIMENSIONS / dimension_names written.
        DimNames: string list option
        Shape: int64 list
        Chunks: int64 list
        FillValue: ZarrFill
        /// Row-major, length = product Shape.
        Data: WritePayload
        /// Chunk grid coordinates to leave UNWRITTEN (fill_value tests).
        OmitChunks: int64 list list
        /// Triangular-decomposed layout: Shape must be [cardinality] @
        /// DenseDims and Data must already be in canonical ascending-lex
        /// pool order. Writes the `blade` attribute.
        Blade: BladeLayout option
    }

    let private payloadInfo (p: WritePayload) : string * string * int =
        // (v2 dtype, v3 data_type, byte size)
        match p with
        | WF32 _ -> ("<f4", "float32", 4)
        | WF64 _ -> ("<f8", "float64", 8)
        | WI32 _ -> ("<i4", "int32", 4)
        | WI64 _ -> ("<i8", "int64", 8)

    let private cellBytes (p: WritePayload) (idx: int) : byte[] =
        match p with
        | WF32 xs -> BitConverter.GetBytes xs.[idx]
        | WF64 xs -> BitConverter.GetBytes xs.[idx]
        | WI32 xs -> BitConverter.GetBytes xs.[idx]
        | WI64 xs -> BitConverter.GetBytes xs.[idx]

    let private fillBytes (p: WritePayload) (fill: ZarrFill) : byte[] =
        match p, fill with
        | WF32 _, FillFloat f -> BitConverter.GetBytes (float32 f)
        | WF32 _, FillInt n -> BitConverter.GetBytes (float32 n)
        | WF64 _, FillFloat f -> BitConverter.GetBytes f
        | WF64 _, FillInt n -> BitConverter.GetBytes (float n)
        | WI32 _, FillInt n -> BitConverter.GetBytes (int32 n)
        | WI32 _, FillFloat f -> BitConverter.GetBytes (int32 f)
        | WI64 _, FillInt n -> BitConverter.GetBytes n
        | WI64 _, FillFloat f -> BitConverter.GetBytes (int64 f)
        | _, FillNone -> Array.zeroCreate 8  // edge padding when no fill declared
        |> fun bs -> bs

    let private jsonNum (fill: ZarrFill) : string =
        match fill with
        | FillFloat f when Double.IsNaN f -> "\"NaN\""
        | FillFloat f when Double.IsPositiveInfinity f -> "\"Infinity\""
        | FillFloat f when Double.IsNegativeInfinity f -> "\"-Infinity\""
        | FillFloat f -> f.ToString("R", Globalization.CultureInfo.InvariantCulture)
        | FillInt n -> string n
        | FillNone -> "null"

    /// The `blade` attribute value for a packed layout (plain JSON — the
    /// F# writer emits real files, no C++ string escaping).
    let private bladeAttrValue (layout: BladeLayout) : string =
        let kindStr = if layout.Group.Sym = SymSymmetric then "sym" else "antisym"
        let idxEntries =
            (sprintf "{\"kind\": \"%s\", \"rank\": %d, \"extent\": %d}" kindStr layout.Group.Rank layout.Group.Extent)
            :: (layout.DenseDims |> List.map (sprintf "{\"kind\": \"dense\", \"extent\": %d}"))
        let (layoutStr, decompJson) =
            match layout.Blocks with
            | None -> ("packed", "{\"scheme\": \"flat-ranges\"}")
            | Some i ->
                let orderStr = match i.Order with OrderLex -> "ascending-lex" | OrderPath -> "path"
                ("packed-blocks",
                 sprintf "{\"scheme\": \"simplex-blocks\", \"tile\": %d, \"grid\": %d, \"block_order\": \"%s\"}" i.Tile i.Grid orderStr)
        sprintf "{\"spec_version\": 1, \"layout\": \"%s\", \"order\": \"ascending-lex\", \"index_types\": [%s], \"decomposition\": %s}"
            layoutStr (String.concat ", " idxEntries) decompJson

    /// Normalize a blocks-layout WriteVar to its physical form: the caller
    /// supplies the LOGICAL view (Shape = [cardinality] @ dense, Data = the
    /// canonical pool); this permutes/pads into block rows and sets the
    /// mandated physical shape/chunks ([blockCount, tile^rank] @ dense,
    /// one block = one chunk). Ordinary vars pass through unchanged.
    let private toPhysical (var: WriteVar) : WriteVar =
        match var.Blade with
        | Some layout when layout.Blocks.IsSome ->
            let info = layout.Blocks.Value
            let g = layout.Group
            let strict = (g.Sym = SymAntisymmetric)
            let card = packedCardinality g
            let trail = layout.DenseDims |> List.fold (fun a d -> a * int d) 1
            let logicalShape = card :: layout.DenseDims
            if var.Shape <> logicalShape then
                failwithf "ZarrWrite '%s': blocks-layout vars take the LOGICAL shape [cardinality] @ dense = %A (got %A); Data is the canonical pool"
                    var.Name logicalShape var.Shape
            let cellMap = SimplexBlocks.blocksCellMap strict g.Extent info.Tile g.Rank info.Order
            let physCells = cellMap.Length
            let nBlocks = SimplexBlocks.blockCount g.Rank info.Grid
            let rowW = SimplexBlocks.maxBlockCells g.Rank info.Tile
            let inline remap (src: 'a[]) (pad: 'a) : 'a[] =
                let out = Array.create (physCells * trail) pad
                for p in 0 .. physCells - 1 do
                    let pool = cellMap.[p]
                    if pool >= 0 then
                        for t in 0 .. trail - 1 do
                            out.[p * trail + t] <- src.[pool * trail + t]
                out
            let padAsFloat =
                match var.FillValue with
                | FillFloat f -> f
                | FillInt n -> float n
                | FillNone -> 0.0
            let data' =
                match var.Data with
                | WF64 xs -> WF64 (remap xs padAsFloat)
                | WF32 xs -> WF32 (remap xs (float32 padAsFloat))
                | WI64 xs -> WI64 (remap xs (int64 padAsFloat))
                | WI32 xs -> WI32 (remap xs (int32 padAsFloat))
            let physRank = 2 + layout.DenseDims.Length
            { var with
                Shape = nBlocks :: rowW :: layout.DenseDims
                Chunks = 1L :: rowW :: layout.DenseDims
                Data = data'
                DimNames = (match var.DimNames with
                            | Some ns when ns.Length = physRank -> Some ns
                            | _ -> None)
                OmitChunks = [] }
        | _ -> var

    /// Build one chunk's bytes: the in-bounds region from Data, edge
    /// overhang padded with the fill value (zeros under FillNone).
    let private chunkBytesFor (var: WriteVar) (coords: int64 list) : byte[] =
        let shape = var.Shape |> List.map int
        let chunks = var.Chunks |> List.map int
        let rank = shape.Length
        let (_, _, bs) = payloadInfo var.Data
        let chunkCount = chunks |> List.fold (*) 1
        let gStr = rowMajorStrides shape
        let cStr = rowMajorStrides chunks
        let coordsArr = coords |> List.map int |> List.toArray
        let shapeArr = List.toArray shape
        let chunksArr = List.toArray chunks
        let gStrArr = List.toArray gStr
        let cStrArr = List.toArray cStr
        let out = Array.zeroCreate<byte> (chunkCount * bs)
        let pad = fillBytes var.Data var.FillValue
        // Pre-fill everything with padding, then overwrite in-bounds cells.
        for c in 0 .. chunkCount - 1 do
            Array.blit pad 0 out (c * bs) bs
        let rec go (d: int) (gBase: int) (cBase: int) =
            if d = rank then
                Array.blit (cellBytes var.Data gBase) 0 out (cBase * bs) bs
            else
                let basePos = coordsArr.[d] * chunksArr.[d]
                let lim = min chunksArr.[d] (shapeArr.[d] - basePos)
                for l in 0 .. lim - 1 do
                    go (d + 1) (gBase + (basePos + l) * gStrArr.[d]) (cBase + l * cStrArr.[d])
        if chunkCount > 0 && (shape |> List.fold (*) 1) > 0 then go 0 0 0
        out

    let private writeChunks (arrayDir: string) (var: WriteVar) (sep: string) (prefix: string) : unit =
        let omit = var.OmitChunks |> List.map (List.map string >> String.concat ",") |> Set.ofList
        for coords in gridCoords var.Shape var.Chunks do
            let skip = Set.contains (coords |> List.map string |> String.concat ",") omit
            if not skip then
                let joined = coords |> List.map string |> String.concat sep
                let key =
                    match prefix, coords with
                    | "", [] -> "0"
                    | "", _ -> joined
                    | p, [] -> p
                    | p, _ -> p + sep + joined
                let file = Path.Combine(arrayDir, key.Replace('/', Path.DirectorySeparatorChar))
                Directory.CreateDirectory (Path.GetDirectoryName file) |> ignore
                File.WriteAllBytes(file, chunkBytesFor var coords)

    /// Write a Zarr v2 group store ("." chunk keys, .zgroup/.zarray/.zattrs).
    let writeStoreV2 (root: string) (vars: WriteVar list) : unit =
        Directory.CreateDirectory root |> ignore
        File.WriteAllText(Path.Combine(root, ".zgroup"), "{\"zarr_format\": 2}")
        for var in vars |> List.map toPhysical do
            let (dtype, _, _) = payloadInfo var.Data
            let arrayDir = Path.Combine(root, var.Name)
            Directory.CreateDirectory arrayDir |> ignore
            let shapeJson = var.Shape |> List.map string |> String.concat ", "
            let chunksJson = var.Chunks |> List.map string |> String.concat ", "
            File.WriteAllText(
                Path.Combine(arrayDir, ".zarray"),
                sprintf "{\"zarr_format\": 2, \"shape\": [%s], \"chunks\": [%s], \"dtype\": \"%s\", \"compressor\": null, \"fill_value\": %s, \"order\": \"C\", \"filters\": null}"
                    shapeJson chunksJson dtype (jsonNum var.FillValue))
            let attrParts =
                [ match var.DimNames with
                  | Some dims ->
                      yield sprintf "\"_ARRAY_DIMENSIONS\": [%s]" (dims |> List.map (sprintf "\"%s\"") |> String.concat ", ")
                  | None -> ()
                  match var.Blade with
                  | Some layout -> yield sprintf "\"blade\": %s" (bladeAttrValue layout)
                  | None -> () ]
            if not attrParts.IsEmpty then
                File.WriteAllText(
                    Path.Combine(arrayDir, ".zattrs"),
                    sprintf "{%s}" (String.concat ", " attrParts))
            writeChunks arrayDir var "." ""

    /// Write a Zarr v3 group store (zarr.json nodes, "c/"-prefixed keys).
    let writeStoreV3 (root: string) (vars: WriteVar list) : unit =
        Directory.CreateDirectory root |> ignore
        File.WriteAllText(Path.Combine(root, "zarr.json"), "{\"zarr_format\": 3, \"node_type\": \"group\"}")
        for var in vars |> List.map toPhysical do
            let (_, dataType, _) = payloadInfo var.Data
            let arrayDir = Path.Combine(root, var.Name)
            Directory.CreateDirectory arrayDir |> ignore
            let shapeJson = var.Shape |> List.map string |> String.concat ", "
            let chunksJson = var.Chunks |> List.map string |> String.concat ", "
            let fillJson =
                match var.FillValue with
                | FillNone -> "0"  // v3 requires a fill_value
                | f -> jsonNum f
            let dimsJson =
                match var.DimNames with
                | Some dims ->
                    sprintf ", \"dimension_names\": [%s]" (dims |> List.map (sprintf "\"%s\"") |> String.concat ", ")
                | None -> ""
            let attrsJson =
                match var.Blade with
                | Some layout -> sprintf ", \"attributes\": {\"blade\": %s}" (bladeAttrValue layout)
                | None -> ""
            File.WriteAllText(
                Path.Combine(arrayDir, "zarr.json"),
                sprintf "{\"zarr_format\": 3, \"node_type\": \"array\", \"shape\": [%s], \"data_type\": \"%s\", \"chunk_grid\": {\"name\": \"regular\", \"configuration\": {\"chunk_shape\": [%s]}}, \"chunk_key_encoding\": {\"name\": \"default\", \"configuration\": {\"separator\": \"/\"}}, \"fill_value\": %s, \"codecs\": [{\"name\": \"bytes\", \"configuration\": {\"endian\": \"little\"}}]%s%s}"
                    shapeJson dataType chunksJson fillJson dimsJson attrsJson)
            writeChunks arrayDir var "/" "c"

// ============================================================================
// Provider registration record
// ============================================================================

let private adaptVarData (d: ZarrVarData) : Blade.ProviderRegistry.ProviderVarData =
    { DimLengths = d.DimLengths
      Payload =
        match d.Payload with
        | ZFloats xs -> Blade.ProviderRegistry.PFloats xs
        | ZInts xs -> Blade.ProviderRegistry.PInts xs }

/// The zarr ProviderSpec (surface module name: "zarr"). Registered by
/// ProviderStatics.install ().
let spec : Blade.ProviderRegistry.ProviderSpec = {
    Name = "zarr"
    LoadAsModule = loadAsModule
    ReadVarData = fun path varName ->
        // Packed (blade-layout) variables do not fold: StaticValue has no
        // packed carrier (SVTuple nesting assumes dense row-major), so the
        // pool would fold to a WRONG dense shape. Loud steering instead.
        try
            let store = load path
            match tryFindArray store varName with
            | Some m when m.Blade.IsSome ->
                Error (sprintf "variable '%s' has a packed (blade: layout=packed) pool layout — triangular variables do not fold at compile time in v1; bind with a plain `let ... |> <alias>.read`" varName)
            | _ -> readVarData path varName |> Result.map adaptVarData
        with ex -> Error ex.Message
    GenReadVar = CppZarr.genReadVar
    GenReadPacked = Some CppZarr.genReadPacked
    GenReadCompoundVar = None  // load_compound: rejected loudly in v1
    GenWriteVar = CppZarr.genWriteVar
    GenStreamOpen = Some CppZarr.genStreamOpen
    GenStreamFiber = Some CppZarr.genStreamFiber
    Includes = CppZarr.genIncludes
    VarDimNames = fun path varName ->
        try
            load path |> fun s -> tryFindArray s varName |> Option.bind (fun m -> m.DimNames)
        with _ -> None
    Fingerprint = storeFingerprint
    VersionStamp = storeVersionStamp
    LinkNeeds = "none (pure std C++17)"
}
